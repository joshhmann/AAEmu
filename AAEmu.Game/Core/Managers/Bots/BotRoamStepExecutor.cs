using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.GameData;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Roam-driven step executor for player bots (integration card t_6bad0654 —
/// the living loop's behavior layer, Option A visibility).
///
/// Per scheduler wake this executor:
///   1. issues the next <see cref="IGameplayActor.MoveTo"/> leg when the
///      actor is idle and the bot has a route (BotPath waypoint loop), and
///   2. ticks the M5 actor (advances the leg through the ordinary Transform),
///   3. applies the Option A visibility layer:
///        a. ground clamp — Z is snapped to the heightmap via
///           WorldManager.GetReferenceHeight (the Simulation.cs:394 pattern),
///        b. throttled movement broadcast — SCOneUnitMovementPacket is
///           broadcast to around-units at ~4-6 Hz (reduced vs the NPC 10 Hz
///           cadence) so real clients see the bot walking.
///   4. opportunistic wildlife combat loop:
///        when nearby hostile wildlife is detected within perception radius,
///        the bot temporarily branches into combat mode (chases, faces, casts
///        class combos, loots upon kill), and resumes its patrol seamlessly.
///
/// The scheduler's per-bot execution lease guarantees at most one in-flight
/// step per bot, and the M5 A1 marshal executes every step on the single
/// execution boundary (the game-loop thread) — so this executor needs no
/// per-bot concurrency guard of its own: it drives the actor
/// (single-writer) from exactly one execution context at a time.
///
/// DI note: this replaces <see cref="GameplayActorStepExecutor"/> as the
/// production IBotStepExecutor wiring in Program.cs. Bots WITHOUT a roam
/// route behave exactly like the plain actor executor (tick-only); the roam
/// drive + visibility is additive per-route.
/// </summary>
public sealed class BotRoamStepExecutor : IBotStepExecutor
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Max elapsed reported per step (clamp against scheduler stalls).</summary>
    public static readonly TimeSpan MaxStepElapsed = TimeSpan.FromSeconds(1);

    /// <summary>Step cadence reported while a request is live (default = 15 Hz / ~66.7ms; can be overridden via AAEMU_PRESENCE_BROADCAST_HZ).</summary>
    public TimeSpan ActiveCadence { get; init; } =
        int.TryParse(Environment.GetEnvironmentVariable("AAEMU_PRESENCE_BROADCAST_HZ"), out var hz) && hz > 0
            ? TimeSpan.FromSeconds(1.0 / hz)
            : TimeSpan.FromSeconds(1.0 / 15);

    /// <summary>Minimum interval between movement broadcasts (default = 15 Hz / ~66.7ms; can be overridden via AAEMU_PRESENCE_BROADCAST_HZ).</summary>
    public TimeSpan BroadcastInterval { get; init; } =
        int.TryParse(Environment.GetEnvironmentVariable("AAEMU_PRESENCE_BROADCAST_HZ"), out var bhz) && bhz > 0
            ? TimeSpan.FromSeconds(1.0 / bhz)
            : TimeSpan.FromSeconds(1.0 / 15);

    /// <summary>Clock for elapsed accounting + broadcast throttle (tests inject FakeTimeProvider).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Ground-height seam for the step-3a clamp (tests inject a fake terrain
    /// heightmap; null → the real one via
    /// <see cref="WorldManager.GetReferenceHeight"/> — the Simulation.cs:394
    /// pattern). Signature: (position, zoneId) → terrain Z, 0 = no data.
    /// </summary>
    public Func<Vector3, uint, float>? GroundHeightProvider { get; init; }

    /// <summary>Actor factory seam (tests inject a recording actor).</summary>
    public Func<Character, IGameplayActor> ActorFactory { get; init; } = c => new GameplayActor(c);

    /// <summary>Walk speed for roam legs (m/s — walking pace, matches ActorFlags walk).</summary>
    public float RoamSpeed { get; init; } = 2.5f;

    /// <summary>Per-leg navigation budget (longer than the actor default so a full route leg never times out mid-walk).</summary>
    public TimeSpan RoamLegTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether opportunistic wildlife hunting is enabled. Defaults to true when
    /// the AAEMU_PRESENCE_HUNT environment variable is set to "1", "true", or "True".
    /// </summary>
    public bool EnableWildlifeHunt { get; set; } =
        Environment.GetEnvironmentVariable("AAEMU_PRESENCE_HUNT") is "1" or "true" or "True";

    /// <summary>Perception radius for detecting nearby wildlife (default = 45m).</summary>
    public float HuntPerceptionRadius { get; init; } =
        float.TryParse(Environment.GetEnvironmentVariable("AAEMU_PRESENCE_HUNT_RADIUS"), out var r) ? r : 45f;

    /// <summary>Scan interval for searching for nearby wildlife (default = 1.2s).</summary>
    public TimeSpan HuntScanInterval { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Cadence for casting skills on engaged wildlife (default = 800ms).</summary>
    public TimeSpan HuntCastInterval { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Melee engagement distance to target before stopping to cast (default = 3.0m).</summary>
    public float HuntMeleeRange { get; init; } = 3.0f;

    /// <summary>Speed at which the bot chases target wildlife (default = 4.5 m/s sprint).</summary>
    public float HuntChaseSpeed { get; init; } = 4.5f;

    /// <summary>Nearby NPC detection seam (null → WorldManager.GetAround&lt;Npc&gt;).</summary>
    public Func<Character, float, IEnumerable<Npc>>? NearbyNpcProvider { get; init; }

    /// <summary>
    /// Conflict activity source (wired to the arbiter's active activity).
    /// When it returns an activity starting with "conflict.", the PvP
    /// engagement branch runs instead of the wildlife hunt for that wake.
    /// Null (default) preserves today's behavior exactly.
    /// </summary>
    public Func<uint, string?>? ActiveActivityProvider { get; init; }

    /// <summary>
    /// PvP attackability gate (default: the real CanAttack path). Test seam
    /// for the conflict branch — the engine relation/zone check needs seeded
    /// singletons headless.
    /// </summary>
    public Func<Character, Character, bool>? CanAttackPlayer { get; init; }

    /// <summary>
    /// Test seam mirroring NearbyNpcProvider, but for player characters.
    /// </summary>
    public Func<Character, float, IEnumerable<Character>>? NearbyCharacterProvider { get; init; }

    /// <summary>Unit resolver seam (null → Character.ParentWorld?.GetUnit).</summary>
    public Func<Character, uint, Unit?>? UnitResolver { get; init; }

    /// <summary>
    /// Whether the opportunistic livestock-butcher loop is enabled. Defaults
    /// to true when the AAEMU_PRESENCE_BUTCHER environment variable is set to
    /// "1", "true", or "True". Off (the default) = zero behavior change.
    /// </summary>
    public bool EnableWildlifeButcher { get; set; } =
        Environment.GetEnvironmentVariable("AAEMU_PRESENCE_BUTCHER") is "1" or "true" or "True";

    /// <summary>Perception radius for detecting nearby butcherable livestock doodads (default = 45m).</summary>
    public float ButcherPerceptionRadius { get; init; } =
        float.TryParse(Environment.GetEnvironmentVariable("AAEMU_PRESENCE_BUTCHER_RADIUS"), out var r) ? r : 45f;

    /// <summary>Scan interval for searching for butcherable livestock (default = 1.2s).</summary>
    public TimeSpan ButcherScanInterval { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Nearby livestock-doodad detection seam (null → WorldManager.GetAround&lt;Doodad&gt;).</summary>
    public Func<Character, float, IEnumerable<Doodad>>? NearbyDoodadProvider { get; init; }

    /// <summary>Livestock-doodad resolver seam (null → Character.ParentWorld?.GetDoodad).</summary>
    public Func<Character, uint, Doodad?>? DoodadResolver { get; init; }
    /// <summary>
    /// Farm-soil probe seam for the travel-to-soil branch (null → the real
    /// farm/subzone lookups: InPublicFarm + GetFarmType + the seed→doodad
    /// allowlist. Tests inject a position map; the DESTINATION is always
    /// resolved by the bounded spiral, never fixture-injected).
    /// </summary>
    public Func<Character, Vector3, bool>? FarmSoilProvider { get; set; }

    /// <summary>
    /// Butcher-skill resolver seam: the interaction skill for a livestock
    /// doodad's CURRENT phase, 0 = not butcherable in this phase (null → the
    /// data-driven default — no doodad or skill ids assumed).
    /// </summary>
    public Func<Doodad, uint>? ButcherSkillResolver { get; init; }


/// <summary>
/// Observable phase of the Tier 0 needs-farm loop (TRAVEL-TO-SOIL +
/// MATURITY-WAIT/RESUME). Readable off <see cref="BotRoamStepExecutor.BotRoamState"/>
/// via the <c>GetBotState</c> seam (tests) and mirrored into log lines on
/// transition (production observability — no tracing framework).
///
/// RESTART BEHAVIOR (documented, not persisted): BotRoamState is per-bot
/// in-memory only. A restart drops the tracked crop id, the soil target,
/// and the travel/reject counters. Post-restart the bot RE-DISCOVERS rather
/// than resumes: the nearest owned crop re-resolves through the bounded
/// perception scan and soil re-resolves through the farm lookups. No
/// persistence was invented for this (the scheduler's per-bot execution
/// lease + memory-only state contract stands).
/// </summary>
public enum NeedsFarmLoopPhase
{
    /// <summary>No farm-loop work this wake (needs inactive, rest/buy path, or idle).</summary>
    Idle,
    /// <summary>Seed present but off valid soil; resolving a reachable soil destination (or bounded-deferred).</summary>
    SeekingSoil,
    /// <summary>Route layer armed to a soil (or mature-crop) destination; MoveTo legs carry the bot.</summary>
    Traveling,
    /// <summary>Plant dispatched on valid soil this wake but not yet landed.</summary>
    Planting,
    /// <summary>Tracked crop exists but reads immature; deferred — no Harvest issued.</summary>
    WaitingMaturity,
    /// <summary>Harvest dispatched on a mature crop this wake (or approaching one).</summary>
    Harvesting,
    /// <summary>Harvest completed; re-evaluating (seed+output → replant) next wake.</summary>
    Replanting
}

    internal sealed class BotRoamState
    {
        public required IGameplayActor Actor { get; init; }
        public BotPath? Path { get; set; }
        public ActorRequest? PendingLeg { get; set; }
        public DateTime LastBroadcastUtc { get; set; } = DateTime.MinValue;
        public long? LastBroadcastTicks { get; set; }
        public long? NextBroadcastTicks { get; set; }
        public Vector3? LastBroadcastPosition { get; set; }
        public float CurrentYawDegrees { get; set; }
        public bool HasInitializedYaw { get; set; }
        public bool WasMoving { get; set; }
        public bool TelemetryLogging { get; set; }
        public TimeSpan? BroadcastIntervalOverride { get; set; }
        public TimeSpan? CadenceOverride { get; set; }

        public uint TargetNpcObjId { get; set; }
        public DateTime TargetEngagedUtc { get; set; } = DateTime.MinValue;
        public DateTime LastScanUtc { get; set; } = DateTime.MinValue;
        public DateTime LastCastUtc { get; set; } = DateTime.MinValue;
        public uint LastSkillUsed { get; set; }

        public uint TargetPlayerObjId { get; set; }
        public DateTime TargetPlayerEngagedUtc { get; set; } = DateTime.MinValue;
        public DateTime LastPvpCastUtc { get; set; } = DateTime.MinValue;
        public uint LastPvpSkillUsed { get; set; }

        public uint TargetButcherDoodadObjId { get; set; }
        public DateTime LastButcherScanUtc { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Tier 0 needs-work flag: set when the needs leg ran work this wake.
        /// Consumed by the hunt/butcher/route gates so needs work preempts
        /// wildlife but never party handling, PvP, or quest work.
        /// </summary>
        public bool NeedsLegActive { get; set; }

        /// <summary>
        /// Tier 0 needs-farm loop observability: the current loop phase
        /// (seeking-soil/traveling/planting/waiting-maturity/harvesting/
        /// replanting — <see cref="NeedsFarmLoopPhase"/>), reset to Idle at
        /// the top of every needs wake and set by the branch that owns the
        /// wake. Restart drops this with the rest of the state (memory-only —
        /// see <see cref="NeedsFarmLoopPhase"/>).
        /// </summary>
        public NeedsFarmLoopPhase NeedsFarmPhase { get; set; } = NeedsFarmLoopPhase.Idle;

        /// <summary>
        /// Resolved farm-soil destination the travel path is walking toward
        /// (null = none). Set when the route layer is armed to soil; cleared
        /// on arrival, on discard, and on bounded defer.
        /// </summary>
        public Vector3? NeedsFarmSoilTarget { get; set; }

        /// <summary>
        /// Tracked planted crop: the objId handed back by a landed Plant
        /// (0 = none tracked). While nonzero the wait branch does a cheap
        /// per-wake liveness check (world lookup + phase read — not a scan)
        /// and harvests via the normal path once the phase reads mature.
        /// </summary>
        public uint NeedsFarmCropObjId { get; set; }

        /// <summary>
        /// Template of the tracked crop (for phase-shape tolerance — a
        /// foreign template under the same objId drops the track).
        /// </summary>
        public uint NeedsFarmCropTemplateId { get; set; }

        /// <summary>
        /// Human-readable reason for the current phase (soil resolve source,
        /// defer cause, stale-drop cause, discard cause). Feeds log lines and
        /// test asserts; never a tracing framework.
        /// </summary>
        public string NeedsFarmReason { get; set; } = "";

        /// <summary>
        /// Bounded soil-resolve attempts since the last successful plant or
        /// arrival: stale/unreachable destinations discard and re-resolve
        /// only up to <see cref="NeedsFarmMaxSoilAttempts"/> per discovery
        /// episode, then the leg defers (never spins forever).
        /// </summary>
        public int NeedsFarmSoilAttempts { get; set; }

        /// <summary>
        /// Consecutive no-farm-nearby defers (bounded: past
        /// <see cref="NeedsFarmMaxDeferWakes"/> the leg keeps deferring but
        /// stops re-logging every wake — still never spins).
        /// </summary>
        public int NeedsFarmDeferWakes { get; set; }
        /// <summary>
        /// Patrol route stashed while a soil/crop approach route owns the
        /// ordinary route layer (restored on arrival/defer so travel never
        /// permanently clobbers the coordinator-armed patrol).
        /// </summary>
        public BotPath? NeedsFarmStashedRoute { get; set; }
    }

    private readonly ConcurrentDictionary<uint, BotRoamState> _states = [];
    private readonly ConcurrentDictionary<uint, DateTime> _lastStepUtc = [];

    // Cached already-completed step results: the scheduler GetResult()s every
    // return, so a fresh Task<TimeSpan?> allocation per wake was pure churn at
    // ~10 wakes/sec/bot. Tasks are immutable once completed — safe to reuse.
    private static readonly Task<TimeSpan?> DormantTask = Task.FromResult<TimeSpan?>(null);
    private Task<TimeSpan?>? _cadenceTask;

    /// <summary>
    /// Resolves the actor instance for a bot character — the SAME actor the
    /// scheduler ticks (control-plane API seam: the queue drives this actor
    /// on the execution boundary, never a second instance). Creates the
    /// per-bot state on first access; never touches the route.
    /// </summary>
    public IGameplayActor GetOrCreateActor(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);

        var characterId = character.Id;
        if (!_states.TryGetValue(characterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(character) };
            _states[characterId] = state;
        }

        return state.Actor;
    }

    /// <summary>
    /// Assigns a roam route to a bot. The route is walked as consecutive
    /// MoveTo legs (Loop mode = patrol forever). Passing null clears the route
    /// (bot returns to tick-only / dormant behavior). Creates the per-bot
    /// state on first assignment (the coordinator arms routes BEFORE the
    /// scheduler's first wake, so the state must exist pre-step). Keyed by
    /// <paramref name="character"/>.Id — the same key the scheduler uses
    /// (PlayerBotRuntime.CharacterId).
    /// </summary>
    public void SetRoamRoute(Character character, BotPath? path)
    {
        ArgumentNullException.ThrowIfNull(character);

        var characterId = character.Id;
        if (!_states.TryGetValue(characterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(character) };
            _states[characterId] = state;
        }

        state.Path = path;
        if (path != null)
            Logger.Info("Roam route assigned: bot {CharacterId} — {Count} waypoints ({Mode})",
                characterId, path.Waypoints.Count, path.Mode);
    }

    /// <summary>
    /// Test/observability seam: the currently assigned route for a bot
    /// (null when none was set or it was cleared). Used by the rig to prove
    /// route arming/clearing without stepping the executor.
    /// </summary>
    internal BotPath? GetRoamRoute(uint characterId)
        => _states.TryGetValue(characterId, out var state) ? state.Path : null;

    /// <summary>
    /// Overrides movement broadcast cadence and scheduler tick cadence for a specific bot (e.g. 5, 10, or 20 Hz).
    /// Pass hz &lt;= 0 to clear the override and revert to defaults.
    /// </summary>
    public void SetBotCadence(Character character, int hz)
    {
        ArgumentNullException.ThrowIfNull(character);
        var characterId = character.Id;
        if (!_states.TryGetValue(characterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(character) };
            _states[characterId] = state;
        }

        if (hz <= 0)
        {
            state.BroadcastIntervalOverride = null;
            state.CadenceOverride = null;
        }
        else
        {
            var interval = TimeSpan.FromSeconds(1.0 / hz);
            state.BroadcastIntervalOverride = interval;
            state.CadenceOverride = interval;
        }
    }

    public void SetBotCadence(uint characterId, int hz)
    {
        if (_states.TryGetValue(characterId, out var state))
        {
            if (hz <= 0)
            {
                state.BroadcastIntervalOverride = null;
                state.CadenceOverride = null;
            }
            else
            {
                var interval = TimeSpan.FromSeconds(1.0 / hz);
                state.BroadcastIntervalOverride = interval;
                state.CadenceOverride = interval;
            }
        }
    }

    /// <summary>
    /// Toggles debug telemetry logging for movement broadcasts for a specific bot.
    /// </summary>
    public bool ToggleTelemetry(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var characterId = character.Id;
        if (!_states.TryGetValue(characterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(character) };
            _states[characterId] = state;
        }

        state.TelemetryLogging = !state.TelemetryLogging;
        return state.TelemetryLogging;
    }

    public bool ToggleTelemetry(uint characterId)
    {
        if (_states.TryGetValue(characterId, out var state))
        {
            state.TelemetryLogging = !state.TelemetryLogging;
            return state.TelemetryLogging;
        }
        return false;
    }

    /// <summary>
    /// Retrieves internal roam state for telemetry and verification.
    /// </summary>
    internal BotRoamState? GetBotState(uint characterId)
        => _states.TryGetValue(characterId, out var state) ? state : null;

    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = TimeProvider.GetUtcNow().UtcDateTime;
        if (!_states.TryGetValue(bot.CharacterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(bot.Character) };
            _states[bot.CharacterId] = state;
        }

        var actor = state.Actor;

        // Soak finding (c): the roam executor owns the throttled (4-6 Hz)
        // movement broadcast in step 3b — the actor's own per-apply
        // broadcast would double-send every wake at ~10 Hz and was the
        // dominant heap-churn source under scheduler-driven roam. States can
        // be created by several entry points (SetRoamRoute included), so the
        // flag is enforced here rather than only at one creation site.
        if (actor is GameplayActor concreteActor && concreteActor.BroadcastMovement)
            concreteActor.BroadcastMovement = false;

        // 0. Party coordination (PB-002 follow & assist):
        // Auto-accept pending party invites, and if in a party as member, follow/assist the leader.
        bool handledByParty = false;
        try
        {
            var tm = TeamManager.Instance;
            if (tm != null)
            {
                if (tm.GetActiveInvitation(bot.CharacterId) is { } invite)
                {
                    var accept = actor.PartyAccept();
                    if (accept.State == ActorLifecycleState.Completed)
                    {
                        Logger.Info("Bot {0} ({1}) accepted party invitation from {2}",
                            bot.CharacterId, bot.Character.Name, invite.Owner?.Id ?? 0);
                    }
                }

                var activeTeam = tm.GetActiveTeamByUnit(bot.CharacterId);
                if (activeTeam != null && activeTeam.IsParty && activeTeam.OwnerId != bot.CharacterId && activeTeam.Members != null)
                {
                    var leader = activeTeam.Members.FirstOrDefault(m => m.Character?.Id == activeTeam.OwnerId)?.Character;
                    if (leader != null && leader.ParentWorld == bot.Character.ParentWorld)
                    {
                        handledByParty = true;
                        var leaderPos = leader.Transform.World.Position;
                        var distToLeader = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, leaderPos, false);

                        if (leader.CurrentTarget is Npc leaderTarget && leaderTarget.Hp > 0)
                        {
                            if (bot.Character.CurrentTarget?.ObjId != leaderTarget.ObjId)
                            {
                                actor.SetTarget(leaderTarget.ObjId);
                            }

                            var targetDist = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, leaderTarget.Transform.World.Position, false);
                            if (targetDist > HuntMeleeRange)
                            {
                                if (actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move })
                                {
                                    state.PendingLeg = actor.MoveToUnit(leaderTarget.ObjId, HuntChaseSpeed, TimeSpan.FromSeconds(5));
                                }
                            }
                            else
                            {
                                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                                {
                                    _ = actor.Stop();
                                    state.PendingLeg = null;
                                }

                                if (now - state.LastCastUtc >= HuntCastInterval)
                                {
                                    state.LastCastUtc = now;
                                    var skillId = 18131u;
                                    actor.Cast(skillId, leaderTarget.ObjId);
                                }
                            }
                        }
                        else
                        {
                            if (distToLeader > 3.5f)
                            {
                                var needsFollowMove = actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move }
                                    || (state.PendingLeg?.Destination.HasValue == true
                                        && Vector3.Distance(state.PendingLeg.Destination.Value, leaderPos) > 3.0f);

                                if (needsFollowMove)
                                {
                                    if (actor.ActiveRequest is { IsTerminal: false })
                                        _ = actor.Stop();
                                    state.PendingLeg = actor.MoveTo(leaderPos, HuntChaseSpeed, TimeSpan.FromSeconds(5));
                                }
                            }
                            else if (distToLeader <= 3.0f)
                            {
                                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                                {
                                    _ = actor.Stop();
                                    state.PendingLeg = null;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Trace(ex, "Bot party step evaluation skipped.");
        }

        // 0. Conflict PvP engagement (war-horn model): while the arbiter holds
        // a conflict.* activity, hostile players preempt wildlife. Same
        // scan/approach/cast shape as the hunt loop below, but targets are
        // Characters gated by CanAttack (or the CanAttackPlayer seam) instead
        // of attackable wildlife. Runs only when ActiveActivityProvider names
        // a conflict activity — null provider preserves today's behavior.
        var pvpEngaged = false;
        if (!handledByParty && ActiveActivityProvider?.Invoke(bot.CharacterId) is string pvpActivity
            && pvpActivity.StartsWith("conflict.", StringComparison.Ordinal))
        {
            pvpEngaged = StepPvpEngagement(bot, actor, state, now);
        }
        // 0b. Tier 0 needs-farm leg: while the arbiter holds a needs.*
        // activity, run one NeedsDecisionScenario leg per wake against the
        // bot's existing actor (GetOrCreateActor — the SAME actor the
        // scheduler ticks, never a second instance). Needs work preempts
        // wildlife/butcher/route but never party handling, PvP, or quest work.
        // Skipped while the actor is busy (TryBegin semantics — the
        // hunt-engage precedent). Null provider preserves today's behavior exactly.
        state.NeedsLegActive = false;
        if (!handledByParty && !pvpEngaged
            && ActiveActivityProvider?.Invoke(bot.CharacterId) is string needsActivity
            && needsActivity.StartsWith("needs.", StringComparison.Ordinal)
            && actor.ActiveRequest is not { IsTerminal: false })
        {
            state.NeedsLegActive = StepNeedsFarmLeg(bot, actor, state);
        }

        // 1. Opportunistic wildlife hunt loop (skipped while fighting players,
        // or while the needs leg landed work — needs preempts hunt acquisition/engagement).
        if (!handledByParty && !pvpEngaged && !state.NeedsLegActive && EnableWildlifeHunt)
        {
            if (state.TargetNpcObjId != 0)
            {
                var targetUnit = UnitResolver != null
                    ? UnitResolver(bot.Character, state.TargetNpcObjId) as Npc
                    : bot.Character.ParentWorld?.GetUnit(state.TargetNpcObjId) as Npc;

                var isDeadOrInvalid = targetUnit == null
                    || targetUnit.Hp <= 0
                    || !IsAttackableWildlife(bot.Character, targetUnit)
                    || now - state.TargetEngagedUtc > TimeSpan.FromSeconds(30);

                if (isDeadOrInvalid)
                {
                    if (targetUnit != null && targetUnit.Hp <= 0)
                    {
                        var loot = actor.Loot(targetUnit.ObjId);
                        // Granted count is the boxed int Result of a Completed Loot request
                        // (GameplayActor.Loot: Complete(request, granted, ...)). The three
                        // terminal outcomes are told apart here: caller grant (Completed,
                        // granted > 0), no-op (Completed, 0 granted — OpenBag granted
                        // nothing), and Rejected-or-foreign-take (non-Completed terminal).
                        var granted = loot.Result is int grantedCount ? grantedCount : 0;
                        if (loot is { IsTerminal: true, State: ActorLifecycleState.Completed } && granted > 0)
                            Logger.Debug("Roam loot granted for bot {CharacterId}: corpse {NpcName} ({NpcId}, template {TemplateId}) — {Granted} item(s)",
                                bot.CharacterId, targetUnit.Name, targetUnit.ObjId, targetUnit.TemplateId, granted);
                        else if (loot is { IsTerminal: true, State: ActorLifecycleState.Completed })
                            Logger.Debug("Roam loot no-op for bot {CharacterId}: corpse {NpcName} ({NpcId}, template {TemplateId}) — Completed with 0 granted ({Detail})",
                                bot.CharacterId, targetUnit.Name, targetUnit.ObjId, targetUnit.TemplateId, loot.Detail);
                        else if (loot.IsTerminal)
                            Logger.Debug("Roam loot rejected for bot {CharacterId}: corpse {NpcName} ({NpcId}, template {TemplateId}) — {State} ({Detail})",
                                bot.CharacterId, targetUnit.Name, targetUnit.ObjId, targetUnit.TemplateId, loot.State, loot.Detail);
                    }

                    if (bot.Character.CurrentTarget?.ObjId == state.TargetNpcObjId)
                    {
                        bot.Character.CurrentTarget = null;
                        bot.Character.BroadcastPacket(new SCTargetChangedPacket(bot.Character.ObjId, 0), true);
                    }

                    state.TargetNpcObjId = 0;
                    state.LastSkillUsed = 0;

                    if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                    {
                        _ = actor.Stop();
                        state.PendingLeg = null;
                    }
                }
                else
                {
                    // Target is valid and alive
                    if (bot.Character.CurrentTarget?.ObjId != targetUnit!.ObjId)
                    {
                        bot.Character.CurrentTarget = targetUnit;
                        bot.Character.BroadcastPacket(new SCTargetChangedPacket(bot.Character.ObjId, targetUnit.ObjId), true);
                    }
                    var dist = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, targetUnit.Transform.World.Position, false);
                    var role = CombatDecisionTree.InferRole(bot.Character);
                    var engageRange = role == CombatRole.Melee ? HuntMeleeRange : 15.0f;

                    if (dist > engageRange)
                    {
                        var targetPos = targetUnit.Transform.World.Position;
                        var needsMove = actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move }
                            || (state.PendingLeg?.Destination.HasValue == true
                                && Vector3.Distance(state.PendingLeg.Destination.Value, targetPos) > 2.0f);

                        if (needsMove)
                        {
                            if (actor.ActiveRequest is { IsTerminal: false })
                                _ = actor.Stop();
                            state.PendingLeg = actor.MoveTo(targetPos, HuntChaseSpeed, TimeSpan.FromSeconds(10));
                        }
                    }
                    else
                    {
                        if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                        {
                            _ = actor.Stop();
                            state.PendingLeg = null;
                        }

                        var angle = MathUtil.CalculateAngleFrom(bot.Character.Transform.World.Position, targetUnit.Transform.World.Position);
                        bot.Character.Transform.Local.SetRotationDegree(0f, 0f, (float)angle - 90);
                        bot.Character.Transform.FinalizeTransform();

                        if (now - state.LastCastUtc >= HuntCastInterval)
                        {
                            var skillId = CombatDecisionTree.SelectPrioritizedSkill(
                                bot.Character,
                                targetUnit,
                                role,
                                null,
                                state.LastSkillUsed);

                            if (skillId > 0)
                            {
                                var castResult = actor.Cast(skillId, targetUnit.ObjId);
                                if (castResult.State != ActorLifecycleState.Rejected)
                                {
                                    state.LastSkillUsed = skillId;
                                    state.LastCastUtc = now;
                                }
                                else
                                {
                                    state.LastSkillUsed = 0;
                                }
                            }
                        }
                    }
                }
            }
            else if (now - state.LastScanUtc >= HuntScanInterval)
            {
                state.LastScanUtc = now;
                var nearbyNpcs = NearbyNpcProvider != null
                    ? NearbyNpcProvider(bot.Character, HuntPerceptionRadius)
                    : WorldManager.GetAround<Npc>(bot.Character, HuntPerceptionRadius);

                Npc? bestNpc = null;
                var bestDist = float.MaxValue;
                foreach (var npc in nearbyNpcs)
                {
                    if (!IsAttackableWildlife(bot.Character, npc))
                        continue;

                    var d = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, npc.Transform.World.Position, false);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestNpc = npc;
                    }
                }

                if (bestNpc != null)
                {
                    state.TargetNpcObjId = bestNpc.ObjId;
                    state.TargetEngagedUtc = now;
                    // Hunt preempts an in-progress butcher approach — a single
                    // active disruption at a time (the butcher scan below only
                    // runs while not hunting).
                    state.TargetButcherDoodadObjId = 0;
                    bot.Character.CurrentTarget = bestNpc;
                    bot.Character.BroadcastPacket(new SCTargetChangedPacket(bot.Character.ObjId, bestNpc.ObjId), true);
                    if (actor.ActiveRequest is { IsTerminal: false })
                    {
                        _ = actor.Stop();
                        state.PendingLeg = null;
                    }
                    Logger.Debug("Bot {CharacterId} engaged wildlife {NpcName} ({NpcId}) at {Dist:F1}m",
                        bot.CharacterId, bestNpc.Name, bestNpc.ObjId, bestDist);
                }
            }
        }

        // 1b. Opportunistic livestock-butcher loop (wildlife slice 4, Option A
        // livestock-only): works on EXISTING livestock doodad chains only
        // (canonical cow 5782 → butchered 5790 → LootPack 79 → 9907, pack 6390
        // → beef 8048; sheep 5649 → 640 → mutton 8052 — resolved data-driven,
        // never assumed). NPC corpses NEVER become doodads: there is no
        // corpse→doodad pipeline here (B stays gated); the leg only scans
        // world doodads already standing on a butcherable phase, approaches
        // ActorRequest. Logging only on the terminal outcome (slice 1/3
        // idiom) — zero success-path change. Skipped while the needs leg
        // landed work (needs preempts butcher acquisition/engagement).
        if (!handledByParty && !state.NeedsLegActive && EnableWildlifeButcher)
        {
            if (state.TargetButcherDoodadObjId != 0)
            {
                var butcherDoodad = DoodadResolver != null
                    ? DoodadResolver(bot.Character, state.TargetButcherDoodadObjId)
                    : bot.Character.ParentWorld?.GetDoodad(state.TargetButcherDoodadObjId);
                var butcherSkillId = butcherDoodad != null ? ResolveButcherSkill(butcherDoodad) : 0;

                if (butcherDoodad == null || butcherSkillId == 0 || butcherDoodad.Despawn > DateTime.MinValue)
                {
                    // Stale: despawned/consumed, or left its butcherable phase
                    // (someone else butchered it into the loot phase) — drop.
                    Logger.Debug("Roam butcher target lost for bot {CharacterId}: doodad {DoodadId} (butcherable no longer)",
                        bot.CharacterId, state.TargetButcherDoodadObjId);
                    state.TargetButcherDoodadObjId = 0;
                    if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                    {
                        _ = actor.Stop();
                        state.PendingLeg = null;
                    }
                }
                else
                {
                    var butcherDist = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, butcherDoodad.Transform.World.Position, false);
                    if (butcherDist > GameplayActor.MaxInteractRange)
                    {
                        var butcherPos = butcherDoodad.Transform.World.Position;
                        var needsButcherMove = actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move }
                            || (state.PendingLeg?.Destination.HasValue == true
                                && Vector3.Distance(state.PendingLeg.Destination.Value, butcherPos) > 2.0f);

                        if (needsButcherMove)
                        {
                            if (actor.ActiveRequest is { IsTerminal: false })
                                _ = actor.Stop();
                            state.PendingLeg = actor.MoveTo(butcherPos, HuntChaseSpeed, TimeSpan.FromSeconds(10));
                        }
                    }
                    else
                    {
                        // Interact needs a free actor (TryBegin busy-rejects) —
                        // park the approach leg first (hunt-engage precedent).
                        if (actor.ActiveRequest is { IsTerminal: false })
                        {
                            _ = actor.Stop();
                            state.PendingLeg = null;
                        }

                        var beforePhase = butcherDoodad.FuncGroupId;
                        var butcher = actor.Interact(butcherDoodad.ObjId, butcherSkillId);
                        if (butcher is { IsTerminal: true, State: ActorLifecycleState.Completed } && butcherDoodad.FuncGroupId != beforePhase)
                            Logger.Debug("Roam butcher completed for bot {CharacterId}: livestock doodad {DoodadId} (template {TemplateId}) phase {Before}→{After} ({Detail})",
                                bot.CharacterId, butcherDoodad.ObjId, butcherDoodad.TemplateId, beforePhase, butcherDoodad.FuncGroupId, butcher.Detail);
                        else if (butcher is { IsTerminal: true, State: ActorLifecycleState.Completed })
                            Logger.Debug("Roam butcher no-op for bot {CharacterId}: livestock doodad {DoodadId} (template {TemplateId}) — Completed with phase unchanged ({Detail})",
                                bot.CharacterId, butcherDoodad.ObjId, butcherDoodad.TemplateId, butcher.Detail);
                        else if (butcher.IsTerminal)
                            Logger.Debug("Roam butcher rejected for bot {CharacterId}: livestock doodad {DoodadId} (template {TemplateId}) — {State} ({Detail})",
                                bot.CharacterId, butcherDoodad.ObjId, butcherDoodad.TemplateId, butcher.State, butcher.Detail);
                        state.TargetButcherDoodadObjId = 0;
                    }
                }
            }
            else if (state.TargetNpcObjId == 0 && now - state.LastButcherScanUtc >= ButcherScanInterval)
            {
                state.LastButcherScanUtc = now;
                var nearbyDoodads = NearbyDoodadProvider != null
                    ? NearbyDoodadProvider(bot.Character, ButcherPerceptionRadius)
                    : WorldManager.GetAround<Doodad>(bot.Character, ButcherPerceptionRadius);

                Doodad? bestDoodad = null;
                var bestDoodadDist = float.MaxValue;
                foreach (var doodad in nearbyDoodads)
                {
                    if (doodad == null || doodad.Despawn > DateTime.MinValue)
                        continue;
                    if (ResolveButcherSkill(doodad) == 0)
                        continue;

                    var d = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, doodad.Transform.World.Position, false);
                    if (d < bestDoodadDist)
                    {
                        bestDoodadDist = d;
                        bestDoodad = doodad;
                    }
                }

                if (bestDoodad != null)
                {
                    state.TargetButcherDoodadObjId = bestDoodad.ObjId;
                    if (actor.ActiveRequest is { IsTerminal: false })
                    {
                        _ = actor.Stop();
                        state.PendingLeg = null;
                    }
                    Logger.Debug("Bot {CharacterId} engaged butcherable livestock {TemplateId} ({DoodadId}) at {Dist:F1}m",
                        bot.CharacterId, bestDoodad.TemplateId, bestDoodad.ObjId, bestDoodadDist);
                }
            }
        }
        // 2. Issue the next leg when idle, not in party, not hunting, not butchering, not needs-working, and a route is active.
        if (!handledByParty && !state.NeedsLegActive && state.TargetNpcObjId == 0 && state.TargetButcherDoodadObjId == 0 && actor.ActiveRequest is not { IsTerminal: false } && state.Path is { IsFinished: false })
        {
            var target = state.Path.CurrentTarget;
            var leg = actor.MoveTo(target, RoamSpeed, RoamLegTimeout);
            state.PendingLeg = leg;
            if (leg.IsTerminal && leg.State != ActorLifecycleState.Completed)
            {
                Logger.Warn("Roam leg rejected for bot {CharacterId}: {State} ({Reason}) — advancing route",
                    bot.CharacterId, leg.State, leg.Detail);
                _ = state.Path.Move(bot.Character.Transform.World.Position, flatArrival: true); // advance past the unreachable point
                state.PendingLeg = null; // already advanced here
            }
        }

        // 3. Tick the actor (advances the active leg through the Transform).
        var elapsed = _lastStepUtc.TryGetValue(bot.CharacterId, out var last)
            ? now - last
            : ActiveCadence;
        _lastStepUtc[bot.CharacterId] = now;
        if (elapsed > MaxStepElapsed)
            elapsed = MaxStepElapsed;

        actor.Tick(elapsed);

        // 3a. Flat arrival owns the leg for ground-clamped walkers (only when roaming)
        if (state.TargetNpcObjId == 0
            && state.TargetButcherDoodadObjId == 0
            && state.PendingLeg is { IsTerminal: false, Action: ActorActionType.Move }
            && state.Path is { IsFinished: false })
        {
            var flat = MathUtil.CalculateDistance(
                bot.Character.Transform.World.Position, state.Path.CurrentTarget, false);
            if (flat <= state.Path.ArrivalRadius)
                _ = actor.Stop();
        }
        // 3b. Route advance on arrival: when the pending Move leg reached a terminal state
        // (deferred while the needs leg landed work — needs preempts route advancement too).
        if (!state.NeedsLegActive
            && state.TargetNpcObjId == 0
            && state.TargetButcherDoodadObjId == 0
            && state.PendingLeg is { IsTerminal: true, Action: ActorActionType.Move }
            && state.Path is { IsFinished: false })
        {
            _ = state.Path.Move(bot.Character.Transform.World.Position, flatArrival: true);
            state.PendingLeg = actor.MoveTo(state.Path.CurrentTarget, RoamSpeed, RoamLegTimeout);
        }

        // 4a. Ground clamp — continuous slope-constrained following
        var position = bot.Character.Transform.World.Position;
        var clampedZ = GroundHeightProvider != null
            ? GroundHeightProvider(position, bot.Character.Transform.ZoneId)
            : (WorldManager.PeekInstance?.GetTerrainHeight(bot.Character.Transform.ZoneId, position.X, position.Y) is { } th && th != 0f
                ? th
                : WorldManager.PeekInstance?.GetReferenceHeight(
                    null, position.X, position.Y, position.Z, bot.Character.Transform.ZoneId) ?? 0f);

        if (clampedZ != 0f)
        {
            var dz = clampedZ - position.Z;
            if (Math.Abs(dz) > 0.001f)
            {
                float targetZ;
                if (Math.Abs(dz) > 3.0f)
                {
                    targetZ = clampedZ;
                }
                else
                {
                    var currentMoveSpeed = state.TargetNpcObjId != 0 || state.TargetButcherDoodadObjId != 0 ? HuntChaseSpeed : RoamSpeed;
                    var maxDz = Math.Max(0.2f, (currentMoveSpeed * (float)elapsed.TotalSeconds) * 1.5f);
                    targetZ = Math.Abs(dz) <= maxDz ? clampedZ : position.Z + Math.Sign(dz) * maxDz;
                }

                bot.Character.Transform.Local.SetPosition(position.X, position.Y, targetZ);
                bot.Character.Transform.FinalizeTransform();
                position = bot.Character.Transform.World.Position;
            }
        }

        // 4b. Movement broadcast with monotonic fixed schedule and standstill packet on stop.
        var currentTicks = TimeProvider.GetTimestamp();
        var freq = TimeProvider.TimestampFrequency;
        var effectiveBroadcastInterval = state.BroadcastIntervalOverride ?? BroadcastInterval;
        var intervalTicks = freq > 0 ? (long)(effectiveBroadcastInterval.TotalSeconds * freq) : 0L;

        if (state.NextBroadcastTicks is null)
        {
            state.NextBroadcastTicks = currentTicks;
            state.LastBroadcastTicks = currentTicks;
            state.LastBroadcastPosition = position;
        }

        if (currentTicks >= state.NextBroadcastTicks.Value)
        {
            var elapsedTicks = currentTicks - (state.LastBroadcastTicks ?? currentTicks);
            var dtSeconds = freq > 0 ? (float)elapsedTicks / freq : (float)effectiveBroadcastInterval.TotalSeconds;
            if (dtSeconds <= 0.001f)
                dtSeconds = (float)effectiveBroadcastInterval.TotalSeconds;

            if (state.LastBroadcastPosition is { } lastPos &&
                Vector3.Distance(lastPos, position) > 0.005f)
            {
                state.WasMoving = true;
                if (bot.Character.Region?.HasHumanObservers() == true)
                {
                    var currentSpeed = state.TargetNpcObjId != 0 || state.TargetButcherDoodadObjId != 0 ? HuntChaseSpeed : RoamSpeed;
                    var targetDest = state.PendingLeg?.Destination ?? state.Path?.CurrentTarget ?? position;
                    var moveType = BuildMoveType(bot.Character, position, targetDest, lastPos, dtSeconds, state, currentSpeed);
                    bot.Character.BroadcastPacket(new SCOneUnitMovementPacket(bot.Character.ObjId, moveType), true);

                    if (state.TelemetryLogging)
                    {
                        Logger.Info("[BotTelemetry] {Bot} Pos=({X:F2},{Y:F2},{Z:F2}) Vel=({Vx},{Vy},{Vz}) Yaw={Yaw:F1} dt={Dt:F3}s",
                            bot.Character.Name, position.X, position.Y, position.Z, moveType.VelX, moveType.VelY, moveType.VelZ, state.CurrentYawDegrees, dtSeconds);
                    }
                }
            }
            else if (state.WasMoving)
            {
                state.WasMoving = false;
                if (bot.Character.Region?.HasHumanObservers() == true)
                {
                    var moveType = BuildStopMoveType(bot.Character, position);
                    bot.Character.BroadcastPacket(new SCOneUnitMovementPacket(bot.Character.ObjId, moveType), true);

                    if (state.TelemetryLogging)
                    {
                        Logger.Info("[BotTelemetry] {Bot} STOP Pos=({X:F2},{Y:F2},{Z:F2})",
                            bot.Character.Name, position.X, position.Y, position.Z);
                    }
                }
            }

            state.LastBroadcastTicks = currentTicks;
            state.LastBroadcastPosition = position;
            state.LastBroadcastUtc = now;

            var nextTicks = state.NextBroadcastTicks.Value;
            while (currentTicks >= nextTicks && intervalTicks > 0)
            {
                nextTicks += intervalTicks;
            }
            state.NextBroadcastTicks = nextTicks;
        }

        var live = actor.ActiveRequest is { IsTerminal: false };
        var routeActive = state.Path is { IsFinished: false };
        var hunting = state.TargetNpcObjId != 0;
        var butchering = state.TargetButcherDoodadObjId != 0;
        var effectiveCadence = state.CadenceOverride ?? ActiveCadence;
        return live || routeActive || hunting || butchering
            ? (state.CadenceOverride.HasValue ? Task.FromResult<TimeSpan?>(effectiveCadence) : (_cadenceTask ??= Task.FromResult<TimeSpan?>(ActiveCadence)))
            : DormantTask;
    }

    /// <summary>
    /// Tier 0 canonical ids (real 1.2 rows): potato seed 15659 → crop 2259 →
    /// yield 7992 (the <c>CropHarvestLoopTests</c> chain).
    /// </summary>
    public const uint NeedsFarmSeedItemTemplateId = 15659;
    public const uint NeedsFarmFoodItemTemplateId = 7992;
    public const long NeedsFarmSeedUnitPrice = 25;
    /// <summary>
    /// Bounded perception radius for needs-farm discovery when no injectable
    /// provider is set (the hunt/butcher loop discipline: prefer the shared
    /// radius seams via <see cref="WorldManager.GetAround{T}"/> over
    /// whole-world scans every wake).
    /// </summary>
    public const float NeedsFarmPerceptionRadius = 45f;
    /// <summary>
    /// Discovery extension for soil resolution: the perception-radius spiral
    /// runs first; when it finds nothing, a coarser spiral out to this bound
    /// runs before the leg defers (bounded discovery fallback — never a
    /// whole-world scan).
    /// </summary>
    public const float NeedsFarmSoilDiscoveryRadius = 150f;

    /// <summary>Coarse spiral step for the discovery extension.</summary>
    public const float NeedsFarmSoilDiscoveryStep = 15f;

    /// <summary>
    /// Soil-destination discards per discovery episode before the leg stops
    /// re-resolving and holds the defer (stale/unreachable destinations
    /// discard and re-resolve only this often — never spin). Reset on
    /// arrival/plant.
    /// </summary>
    public const int NeedsFarmMaxSoilAttempts = 3;

    /// <summary>
    /// Resolve tries run on defer wake 1, then every this many defer wakes
    /// (cheap counters per wake; spiral probes only on resolve wakes).
    /// </summary>
    public const int NeedsFarmSoilResolveIntervalWakes = 10;

    /// <summary>
    /// Tier 0 needs-farm leg (branch 0b): legible per-wake re-evaluation, not
    /// a script — observe → seed absent? buy path; seed + invalid soil?
    /// travel path; seed + valid soil? plant; tracked/scanned crop immature?
    /// deferred wait; mature? harvest via the normal path; output + seed?
    /// replant next wake.
    ///
    /// TRAVEL-TO-SOIL arms an ordinary <see cref="BotPath"/> (single-leg
    /// <c>PathTo</c>) to a spiral-resolved soil destination and returns false
    /// so the route layer's own MoveTo legs + arrival advance carry the bot
    /// (no teleport, no Transform writes — movement applies through the
    /// actor tick exactly like patrol legs). Arrival re-observes and plants
    /// normally. MATURITY-WAIT tracks the planted crop (objId + template) and
    /// defers while its phase reads immature — no Harvest is issued on wait
    /// wakes; the engine stays the sole maturity authority at dispatch.
    ///
    /// Returns true only when decision work actually landed (Completed) —
    /// reject/rest/wait/travel/defer wakes return false so the route still
    /// walks and the scheduler keeps its cadence instead of spinning.
    /// </summary>
    private bool StepNeedsFarmLeg(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state)
    {
        var character = bot.Character;
        var position = character.Transform.World.Position;

        // ---- 1. Tracked crop: cheap per-wake liveness check (one world
        // lookup + phase read — not a scan). Gone/harvested/despawned/
        // ownership-changed → drop the stale id and re-evaluate below.
        if (state.NeedsFarmCropObjId != 0)
        {
            var tracked = ResolveDoodad(character, state.NeedsFarmCropObjId);
            if (!IsTrackedCropLive(tracked, character, state))
            {
                DropTrackedCrop(state, bot, "crop gone/harvested/despawned/ownership-changed — re-evaluating");
            }
            else if (IsCropMature(tracked!))
            {
                return StepNeedsFarmMatureCrop(bot, actor, state, character, position, tracked!);
            }
            else
            {
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
                    $"crop {tracked!.ObjId} immature — deferred, no harvest issued");
                return false; // yield to idle/other behavior
            }
        }

        // ---- 2. Untracked scan: nearest owned/public crop in perception.
        // Mature → harvest path; immature → adopt the track and defer (an
        // immature target never reaches the decision, so no Harvest issues
        // each wake).
        var scanned = NearestNeedsFarmCrop(character, position);
        if (scanned != null)
        {
            if (IsCropMature(scanned))
                return StepNeedsFarmMatureCrop(bot, actor, state, character, position, scanned);
            state.NeedsFarmCropObjId = scanned.ObjId;
            state.NeedsFarmCropTemplateId = scanned.TemplateId;
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
                $"adopted crop {scanned.ObjId} immature — deferred, no harvest issued");
            return false;
        }

        // ---- 3. Seed branches: seed absent → buy path; seed + valid soil →
        // plant; seed + invalid soil → travel path.
        var merchantObjId = ResolveSeedMerchant(character, position);
        if (SeedInBag(character) > 0)
        {
            if (IsValidFarmSoil(character, position))
            {
                state.NeedsFarmSoilAttempts = 0;
                state.NeedsFarmDeferWakes = 0;
                RestoreFarmRoute(state, bot, "on valid soil");
                return AfterNeedsFarmDispatch(state, bot,
                    DispatchNeedsFarmLeg(bot, actor, merchantObjId, 0, position));
            }
            return StepNeedsFarmTravel(bot, actor, state, character, position);
        }

        state.NeedsFarmSoilAttempts = 0;
        state.NeedsFarmDeferWakes = 0;
        return AfterNeedsFarmDispatch(state, bot,
            DispatchNeedsFarmLeg(bot, actor, merchantObjId, 0, position));
    }

    /// <summary>
    /// Mature-crop branch: in harvest range → harvest via the normal decision
    /// path; out of range → approach through the ordinary route layer with
    /// bounded re-arms (never spin), harvesting on arrival.
    /// </summary>
    private bool StepNeedsFarmMatureCrop(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state,
        Character character, Vector3 position, Doodad crop)
    {
        state.NeedsFarmCropObjId = crop.ObjId;
        state.NeedsFarmCropTemplateId = crop.TemplateId;
        var dist = MathUtil.CalculateDistance(position, crop.Transform.World.Position, false);
        if (dist > GameplayActor.MaxInteractRange)
        {
            var enRoute = state.NeedsFarmSoilTarget is { } soil
                && state.Path is { IsFinished: false }
                && MathUtil.CalculateDistance(state.Path.CurrentTarget, soil, false)
                    <= GameplayActor.ArrivalRadius + 1f;
            if (!enRoute)
            {
                if (state.NeedsFarmSoilAttempts >= NeedsFarmMaxSoilAttempts)
                {
                    SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
                        $"mature crop {crop.ObjId} unreachable ({dist:F1}m) — holding, no harvest issued");
                    return false;
                }
                state.NeedsFarmSoilAttempts++;
                state.NeedsFarmSoilTarget = null; // stale/detoured destination discarded before re-arm
                ArmFarmRoute(state, bot, crop.Transform.World.Position,
                    $"mature crop {crop.ObjId} at {dist:F1}m — approaching");
            }
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Traveling,
                $"mature crop {crop.ObjId} at {dist:F1}m — approaching");
            return false;
        }
        state.NeedsFarmSoilAttempts = 0;
        var merchantObjId = ResolveSeedMerchant(character, position);
        return AfterNeedsFarmDispatch(state, bot,
            DispatchNeedsFarmLeg(bot, actor, merchantObjId, crop.ObjId, position, offerPlant: false));
    }

    /// <summary>
    /// Travel-to-soil branch: on soil → restore the patrol, clear the
    /// episode, and plant normally this wake; en-route → yield the wake to
    /// the route layer; stale/finished route while still off soil → discard
    /// and boundedly re-resolve, else bounded defer (never spin forever).
    /// </summary>
    private bool StepNeedsFarmTravel(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state,
        Character character, Vector3 position)
    {
        if (IsValidFarmSoil(character, position))
        {
            state.NeedsFarmSoilAttempts = 0;
            state.NeedsFarmDeferWakes = 0;
            RestoreFarmRoute(state, bot, "arrived on valid soil");
            var merchantObjId = ResolveSeedMerchant(character, position);
            return AfterNeedsFarmDispatch(state, bot,
                DispatchNeedsFarmLeg(bot, actor, merchantObjId, 0, position));
        }
        if (state.NeedsFarmSoilTarget is { } target
            && state.Path is { IsFinished: false }
            && MathUtil.CalculateDistance(state.Path.CurrentTarget, target, false) <= GameplayActor.ArrivalRadius + 1f)
        {
            // En-route: yield the wake to the route layer WITHOUT running
            // the decision — the decision's Rest fallback would Stop() the
            // live route MoveTo leg through the same actor every wake,
            // strangling the walk to one tick per wake (live E2E finding).
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Traveling,
                $"en-route to soil ({target.X:F0},{target.Y:F0})");
            return false;
        }
        if (state.NeedsFarmSoilTarget != null)
        {
            state.NeedsFarmSoilTarget = null;
            state.NeedsFarmSoilAttempts++;
            state.NeedsFarmReason = "soil destination stale/unreachable — discarded";
        }
        state.NeedsFarmDeferWakes++;
        var resolveWake = state.NeedsFarmDeferWakes <= 1
            || (state.NeedsFarmDeferWakes - 1) % NeedsFarmSoilResolveIntervalWakes == 0;
        if (resolveWake && state.NeedsFarmSoilAttempts <= NeedsFarmMaxSoilAttempts)
        {
            var resolved = ResolveSoilTarget(character, position);
            if (resolved != null)
            {
                state.NeedsFarmDeferWakes = 0;
                ArmFarmRoute(state, bot, resolved.Value,
                    $"soil resolved ({resolved.Value.X:F0},{resolved.Value.Y:F0}) — traveling");
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Traveling, state.NeedsFarmReason);
                return false;
            }
        }
        RestoreFarmRoute(state, bot, "no farm nearby — bounded defer");
        SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.SeekingSoil,
            state.NeedsFarmSoilAttempts > NeedsFarmMaxSoilAttempts
                ? "no farm nearby — resolve budget spent, holding defer"
                : "no farm nearby — bounded defer");
        return false;
    }

    /// <summary>
    /// Dispatch tail: maps the decision outcome onto the loop phase (the
    /// observability seam — phase + target + reason stay readable off the
    /// state) and records a landed plant's crop for the wait branch. Keeps
    /// the 0b contract: true only when decision work landed (Completed).
    /// </summary>
    private bool AfterNeedsFarmDispatch(BotRoamState state, PlayerBotRuntime bot,
        NeedsDecisionScenario.NeedsRunResult? result)
    {
        if (result == null)
        {
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle, "actor seam unavailable");
            return false;
        }
        var landed = result.WorkSelected && result.Request?.State == ActorLifecycleState.Completed;
        if (landed)
        {
            Logger.Debug("Roam needs leg completed for bot {CharacterId}: {Action} ({Detail})",
                bot.CharacterId, result.SelectedAction, result.Request!.Detail);
        }
        switch (result.SelectedAction)
        {
            case ActorActionType.Plant when landed
                && result.Request!.Result is uint plantedObjId && plantedObjId != 0:
                state.NeedsFarmCropObjId = plantedObjId;
                state.NeedsFarmCropTemplateId =
                    ResolveDoodad(bot.Character, plantedObjId)?.TemplateId ?? 0;
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
                    $"planted crop {plantedObjId} — waiting maturity");
                break;
            case ActorActionType.Plant when landed:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Planting, "plant landed without crop objId");
                break;
            case ActorActionType.Plant:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Planting,
                    $"plant dispatched ({result.Request?.State})");
                break;
            case ActorActionType.Harvest when landed:
                var harvested = state.NeedsFarmCropObjId;
                state.NeedsFarmCropObjId = 0;
                state.NeedsFarmCropTemplateId = 0;
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Replanting,
                    $"harvested crop {harvested} — re-evaluating (seed+output → replant)");
                break;
            case ActorActionType.Harvest:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Harvesting,
                    $"harvest dispatched ({result.Request?.State})");
                break;
            case ActorActionType.Buy when landed:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle, "seed bought — re-evaluating");
                break;
            case ActorActionType.Sell when landed:
                // Earn leg landed: surplus liquidated toward the seed price.
                // Idle re-evaluates to buy on the NEXT wake — never a scripted
                // sell-then-buy chain in one wake (re-evaluation discipline).
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle, "surplus sold — re-evaluating");
                break;
            default:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle,
                    $"rest/reject ({result.SelectedAction}) — yielding");
                break;
        }
        return landed;
    }

    /// <summary>
    /// Seed-merchant discovery (the existing shop-range head of the leg,
    /// extracted verbatim): nearest in-range merchant whose pack sells the
    /// seed, 0 when none.
    /// </summary>
    private uint ResolveSeedMerchant(Character character, Vector3 position)
    {
        var world = character.ParentWorld;
        uint merchantObjId = 0;
        if (world == null)
            return 0;
        var bestDist = GameplayActor.MaxShopRange;
        var merchants = (NearbyNpcProvider ?? DefaultNearbyNpcs)(character, NeedsFarmPerceptionRadius);
        foreach (var npc in merchants)
        {
            if (npc?.Template == null || !npc.Template.Merchant || npc.Template.MerchantPackId == 0)
                continue;
            var pack = NpcManager.Instance.GetGoods(npc.Template.MerchantPackId);
            if (pack == null || !pack.SellsItem(NeedsFarmSeedItemTemplateId))
                continue;
            var d = MathUtil.CalculateDistance(position, npc.Transform.World.Position, false);
            if (d <= bestDist)
            {
                bestDist = d;
                merchantObjId = npc.ObjId;
            }
        }
        return merchantObjId;
    }

    /// <summary>
    /// Nearest owned/public crop in perception (the existing crop-scan head
    /// of the leg, extracted verbatim but returning the doodad so the wait
    /// branch can read its phase without a second lookup).
    /// </summary>
    private Doodad? NearestNeedsFarmCrop(Character character, Vector3 position)
    {
        Doodad? best = null;
        var bestDist = float.MaxValue;
        foreach (var doodad in (NearbyDoodadProvider ?? DefaultNearbyDoodads)(character, NeedsFarmPerceptionRadius))
        {
            if (doodad == null || doodad.Despawn > DateTime.MinValue)
                continue;
            if (!IsNeedsFarmCrop(doodad, character))
                continue;
            var d = MathUtil.CalculateDistance(position, doodad.Transform.World.Position, false);
            if (d < bestDist)
            {
                bestDist = d;
                best = doodad;
            }
        }
        return best;
    }

    private static int SeedInBag(Character character)
        => character.Inventory?.GetItemsCount(SlotType.Inventory, NeedsFarmSeedItemTemplateId) ?? 0;

    private Doodad? ResolveDoodad(Character character, uint doodadObjId)
        => DoodadResolver != null
            ? DoodadResolver(character, doodadObjId)
            : character.ParentWorld?.GetDoodad(doodadObjId);

    /// <summary>
    /// Tracked-crop liveness: null/gone, despawn-scheduled, template-swapped,
    /// or no longer ours (ownership change) all read stale. Maturity stays
    /// the engine's gate — this only decides whether the track is OURS.
    /// </summary>
    private static bool IsTrackedCropLive(Doodad? tracked, Character character, BotRoamState state)
    {
        if (tracked == null || tracked.Despawn > DateTime.MinValue)
            return false;
        if (state.NeedsFarmCropTemplateId != 0 && tracked.TemplateId != state.NeedsFarmCropTemplateId)
            return false;
        try
        {
            return IsNeedsFarmCrop(tracked, character);
        }
        catch
        {
            return false;
        }
    }

    private static void DropTrackedCrop(BotRoamState state, PlayerBotRuntime bot, string reason)
    {
        state.NeedsFarmCropObjId = 0;
        state.NeedsFarmCropTemplateId = 0;
        state.NeedsFarmReason = reason;
        Logger.Debug("Roam needs-farm loop bot {CharacterId}: dropped tracked crop — {Reason}",
            bot.CharacterId, reason);
    }

    /// <summary>
    /// Maturity read: the same data-driven harvestability the Harvest engine
    /// path resolves (current phase carries a loot-linked interaction).
    /// Never mutates — a probe, not a transition.
    /// </summary>
    private static bool IsCropMature(Doodad doodad)
    {
        try
        {
            return M3aM4ReplayScenario.TryGetHarvestSkill(doodad, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Valid plant soil for our seed: the same membership + doodad-type legs
    /// the decision's perception gate mirrors (the engine revalidates the
    /// count cap fail-closed at dispatch).
    /// </summary>
    private bool IsValidFarmSoil(Character character, Vector3 position)
    {
        if (FarmSoilProvider != null)
        {
            try
            {
                return FarmSoilProvider(character, position);
            }
            catch
            {
                return false;
            }
        }
        var world = character.ParentWorld;
        if (world == null)
            return false;
        if (!PublicFarmManager.Instance.InPublicFarm(world.Template, position))
            return false;
        var farmType = PublicFarmManager.Instance.GetFarmType(world, position);
        if (farmType == FarmType.Invalid)
            return false;
        var doodadId = ItemManager.Instance.GetDoodadIdFromItem(NeedsFarmSeedItemTemplateId);
        if (doodadId == 0)
            return false;
        return CommonFarmGameData.Instance.GetAllowedDoodads(farmType).Contains(doodadId);
    }

    /// <summary>
    /// Nearest valid soil: deterministic spiral (8 compass points per ring)
    /// to the perception radius, then a coarser bounded discovery extension.
    /// The decision says "need valid soil"; this owns waypoints.
    /// </summary>
    private Vector3? ResolveSoilTarget(Character character, Vector3 from)
    {
        foreach (var candidate in SoilSpiralCandidates(from, NeedsFarmPerceptionRadius, 5f))
        {
            if (IsValidFarmSoil(character, candidate))
                return WithGroundZ(character, candidate, from.Z);
        }
        foreach (var candidate in SoilSpiralCandidates(from, NeedsFarmSoilDiscoveryRadius,
                     NeedsFarmSoilDiscoveryStep, NeedsFarmPerceptionRadius))
        {
            if (IsValidFarmSoil(character, candidate))
                return WithGroundZ(character, candidate, from.Z);
        }
        return null;
    }

    /// <summary>
    /// Ground-pins a resolved soil destination (live E2E finding): the
    /// spiral carries the anchor's Z, but terrain along the walk can sit
    /// meters below it — and the actor's MoveTo arrival gate is 3D
    /// (flat AND Z within <see cref="GameplayActor.ArrivalRadius"/>), so a
    /// stale-Z target never completes and the bot parks at its destination
    /// reporting Traveling forever. Sample the same height source the step
    /// clamp reads (terrain height, else reference height); 0 = no data →
    /// keep the anchor Z.
    /// </summary>
    private Vector3 WithGroundZ(Character character, Vector3 candidate, float fallbackZ)
    {
        float groundZ;
        try
        {
            groundZ = GroundHeightProvider != null
                ? GroundHeightProvider(candidate, character.Transform.ZoneId)
                : (WorldManager.PeekInstance?.GetTerrainHeight(character.Transform.ZoneId, candidate.X, candidate.Y) is { } th && th != 0f
                    ? th
                    : WorldManager.PeekInstance?.GetReferenceHeight(
                        null, candidate.X, candidate.Y, candidate.Z, character.Transform.ZoneId) ?? 0f);
        }
        catch
        {
            groundZ = 0f;
        }
        return groundZ != 0f ? new Vector3(candidate.X, candidate.Y, groundZ) : candidate;
    }

    private static IEnumerable<Vector3> SoilSpiralCandidates(Vector3 origin, float maxRadius, float step,
        float skipWithin = 0f)
    {
        for (var r = step; r <= maxRadius + 0.001f; r += step)
        {
            if (r <= skipWithin)
                continue;
            for (var k = 0; k < 8; k++)
            {
                var a = (float)(k * Math.PI / 4);
                yield return new Vector3(
                    origin.X + MathF.Cos(a) * r,
                    origin.Y + MathF.Sin(a) * r,
                    origin.Z);
            }
        }
    }

    /// <summary>
    /// Arms the ordinary route layer toward a farm destination (soil or a
    /// mature crop): stashes an unfinished patrol once, then walks a
    /// single-leg <c>PathTo</c> through the standard MoveTo legs. Never
    /// writes the Transform — the actor tick owns movement.
    /// </summary>
    private void ArmFarmRoute(BotRoamState state, PlayerBotRuntime bot, Vector3 target, string reason)
    {
        if (state.NeedsFarmStashedRoute == null && state.Path is { IsFinished: false })
            state.NeedsFarmStashedRoute = state.Path;
        state.NeedsFarmSoilTarget = target;
        state.NeedsFarmReason = reason;
        SetRoamRoute(bot.Character, BotPath.PathTo(target));
    }

    /// <summary>
    /// Yields the route layer back: restores the stashed patrol, or clears a
    /// spent soil route to tick-only/dormant.
    /// </summary>
    private void RestoreFarmRoute(BotRoamState state, PlayerBotRuntime bot, string why)
    {
        if (state.NeedsFarmStashedRoute != null)
        {
            SetRoamRoute(bot.Character, state.NeedsFarmStashedRoute);
            state.NeedsFarmStashedRoute = null;
            Logger.Debug("Roam needs-farm loop bot {CharacterId}: patrol route restored ({Why})",
                bot.CharacterId, why);
        }
        else if (state.Path is { IsFinished: true })
        {
            SetRoamRoute(bot.Character, null);
        }
        state.NeedsFarmSoilTarget = null;
    }

    /// <summary>
    /// Phase observability: state-readable every wake (the test seam) plus
    /// one log line per TRANSITION (steady states stay quiet).
    /// </summary>
    private void SetNeedsFarmPhase(BotRoamState state, PlayerBotRuntime bot, NeedsFarmLoopPhase phase, string reason)
    {
        state.NeedsFarmReason = reason;
        if (state.NeedsFarmPhase == phase)
            return;
        state.NeedsFarmPhase = phase;
        var target = state.NeedsFarmCropObjId != 0
            ? $"crop={state.NeedsFarmCropObjId}"
            : state.NeedsFarmSoilTarget is { } soil
                ? $"soil=({soil.X:F0},{soil.Y:F0})"
                : "target=-";
        Logger.Info("Roam needs-farm loop bot {CharacterId}: {Phase} {Target} — {Reason}",
            bot.CharacterId, phase, target, reason);
    }

    /// <summary>
    /// Bounded production merchant discovery (the no-provider path): radius
    /// discipline via <see cref="WorldManager.GetAround{T}"/> when the
    /// character carries a region; the same radius over the world registry
    /// for headless/test worlds without regions (production characters
    /// always carry one, so the fallback never runs live). The shop-range
    /// gate still applies per candidate — this only bounds the SCAN.
    /// </summary>
    private IEnumerable<Npc> DefaultNearbyNpcs(Character character, float radius)
    {
        var position = character.Transform.World.Position;
        if (character.Region != null)
            return WorldManager.GetAround<Npc>(character, radius);
        var world = character.ParentWorld;
        return world?.GetAllNpcs().Where(npc =>
            npc != null && MathUtil.CalculateDistance(position, npc.Transform.World.Position, false) <= radius) ?? [];
    }

    /// <summary>
    /// Bounded production crop discovery (the no-provider path): same shape
    /// as <see cref="DefaultNearbyNpcs"/> over the player-doodad spawn
    /// registry. Authoritative dispatch still validates at the engine gates.
    /// </summary>
    private IEnumerable<Doodad> DefaultNearbyDoodads(Character character, float radius)
    {
        var position = character.Transform.World.Position;
        if (character.Region != null)
            return WorldManager.GetAround<Doodad>(character, radius);
        var world = character.ParentWorld;
        return world?.SpawnManager?.GetAllPlayerDoodads()?.Where(doodad =>
            doodad != null && MathUtil.CalculateDistance(position, doodad.Transform.World.Position, false) <= radius) ?? [];
    }

    /// <summary>
    /// Needs-farm dispatch tail: runs one <see cref="NeedsDecisionScenario"/>
    /// decision against the resolved targets. Split from the discovery head
    /// so the bounded-discovery helpers sit between them as siblings. Returns
    /// the run result so the caller can map phases and record the planted
    /// crop; the 0b contract (true only on landed work) lives in
    /// <see cref="AfterNeedsFarmDispatch"/> — not here.
    /// </summary>
    private NeedsDecisionScenario.NeedsRunResult? DispatchNeedsFarmLeg(PlayerBotRuntime bot, IGameplayActor actor, uint merchantObjId, uint cropObjId, Vector3 position)
    {
        return DispatchNeedsFarmLeg(bot, actor, merchantObjId, cropObjId, position, offerPlant: true);
    }

    /// <summary>
    /// Needs-farm dispatch tail: runs one <see cref="NeedsDecisionScenario"/>
    /// decision against the resolved targets. Split from the discovery head
    /// so the bounded-discovery helpers sit between them as siblings. Returns
    /// the run result so the caller can map phases and record the planted
    /// crop; the 0b contract (true only on landed work) lives in
    /// <see cref="AfterNeedsFarmDispatch"/> — not here.
    ///
    /// Mature-harvest precedence (live E2E finding): the decision's fixed
    /// priorities rank Plant (25) above Harvest (10), so a bot standing on
    /// soil with seed would plant FOREVER and starve its own mature crop.
    /// The mature branch therefore dispatches with the plant candidate
    /// unconfigured (seed 0 → refused before preference) — the decision's
    /// own preconditions do the suppression, no priority surgery, no new
    /// gameplay path.
    /// </summary>
    private NeedsDecisionScenario.NeedsRunResult? DispatchNeedsFarmLeg(PlayerBotRuntime bot, IGameplayActor actor, uint merchantObjId, uint cropObjId, Vector3 position, bool offerPlant)
    {
        if (actor is not GameplayActor concreteActor)
            return null;

        return NeedsDecisionScenario.Run(concreteActor, new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = $"needs-farm-{bot.CharacterId}-{TimeProvider.GetUtcNow().UtcTicks}",
            HarvestDoodadObjId = cropObjId,
            PlantSeedItemTemplateId = offerPlant ? NeedsFarmSeedItemTemplateId : 0,
            PlantPosition = position,
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = NeedsFarmSeedItemTemplateId,
            BuyCount = 1,
            BuyUnitPrice = NeedsFarmSeedUnitPrice,
            FoodItemTemplateId = NeedsFarmFoodItemTemplateId,
            // Earn leg: the same in-range seed merchant is the sell target
            // (Sell needs ANY live merchant — no pack/range gate), and the
            // harvested food output is the sellable surplus. The seed itself
            // is excluded by the surplus-not-seed precondition, so the loop
            // can never liquidate the seed it needs to plant.
            SellMerchantNpcObjId = merchantObjId,
            SellSurplusItemTemplateId = NeedsFarmFoodItemTemplateId
        });
    }

    /// <summary>
    /// Tier 0 crop rule: directly character-owned by the bot, or house-bound
    /// where the house allows interaction (the FarmerCycleScenario owned-plot
    /// precedent). System-owned crops on public-farm soil are also accepted
    /// after PublicFarmTick expiry clears their owner. Maturity stays the
    /// engine's own fail-closed gate at dispatch.
    /// </summary>
    private static bool IsNeedsFarmCrop(Doodad doodad, Character character)
    {
        if (doodad.OwnerType == DoodadOwnerType.Character)
            return doodad.OwnerId == character.Id;
        if (doodad.OwnerType == DoodadOwnerType.Housing)
            return HousingManager.Instance.GetHouseById(doodad.OwnerDbId)?.AllowedToInteract(character) == true;
        if (doodad.OwnerType != DoodadOwnerType.System && doodad.OwnerId != 0)
            return false;

        var world = doodad.ParentWorld;
        if (world == null)
            return false;
        var position = doodad.Transform.World.Position;
        if (!PublicFarmManager.Instance.InPublicFarm(world.Template, position) ||
            PublicFarmManager.IsProtected(doodad))
            return false;

        var farmType = PublicFarmManager.Instance.GetFarmType(world, position);
        return CommonFarmGameData.Instance.GetAllowedDoodads(farmType).Contains(doodad.TemplateId);
    }


    /// <summary>
    /// Builds the movement payload for the broadcast — deriving 3D velocity from
    /// post-constraint displacement over monotonic delta-time and applying smooth yaw turning.
    /// </summary>
    private static UnitMoveType BuildMoveType(
        Character character,
        Vector3 position,
        Vector3 targetPos,
        Vector3 lastPos,
        float dtSeconds,
        BotRoamState state,
        float speed = 2.5f)
    {
        var moveType = (UnitMoveType)MoveType.GetType(MoveTypeEnum.Unit);

        var dt = dtSeconds > 0.001f ? dtSeconds : 0.1f;
        var vx = (position.X - lastPos.X) / dt * 1000f;
        var vy = (position.Y - lastPos.Y) / dt * 1000f;
        var vz = (position.Z - lastPos.Z) / dt * 1000f;

        var distMoved = MathUtil.CalculateDistance(lastPos, position, false);
        var angle = distMoved > 0.01f
            ? MathUtil.CalculateAngleFrom(lastPos, position)
            : MathUtil.CalculateAngleFrom(position, targetPos);
        var targetYaw = (float)angle - 90f;

        if (!state.HasInitializedYaw)
        {
            state.CurrentYawDegrees = targetYaw;
            state.HasInitializedYaw = true;
        }
        else
        {
            var maxTurnDelta = 360f * dt;
            state.CurrentYawDegrees = MoveAngleTowards(state.CurrentYawDegrees, targetYaw, maxTurnDelta);
        }

        character.Transform.Local.SetRotationDegree(0f, 0f, state.CurrentYawDegrees);
        var (rx, ry, rz) = character.Transform.Local.ToRollPitchYawSBytesMovement();

        var isRunning = speed > 3.0f;
        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = (short)Math.Clamp(vx, short.MinValue, short.MaxValue);
        moveType.VelY = (short)Math.Clamp(vy, short.MinValue, short.MaxValue);
        moveType.VelZ = (short)Math.Clamp(vz, short.MinValue, short.MaxValue);
        moveType.RotationX = rx;
        moveType.RotationY = ry;
        moveType.RotationZ = rz;
        moveType.ActorFlags = (byte)(isRunning ? 4 : 5);
        moveType.Flags = MoveTypeFlags.Moving;
        moveType.DeltaMovement = [0, (sbyte)(isRunning ? 127 : 63), 0];
        moveType.Stance = isRunning ? GameStanceType.Combat : GameStanceType.Relaxed;
        moveType.Alertness = isRunning ? MoveTypeAlertness.Alert : MoveTypeAlertness.Idle;
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }

    private static float MoveAngleTowards(float current, float target, float maxDelta)
    {
        var diff = (target - current) % 360f;
        if (diff > 180f) diff -= 360f;
        if (diff < -180f) diff += 360f;
        if (Math.Abs(diff) <= maxDelta) return target;
        return current + Math.Sign(diff) * maxDelta;
    }

    /// <summary>
    /// Builds a standstill movement payload broadcast when a bot transitions from moving to stationary.
    /// </summary>
    private static UnitMoveType BuildStopMoveType(Character character, Vector3 position)
    {
        var moveType = (UnitMoveType)MoveType.GetType(MoveTypeEnum.Unit);
        var (rx, ry, rz) = character.Transform.Local.ToRollPitchYawSBytesMovement();

        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = 0;
        moveType.VelY = 0;
        moveType.VelZ = 0;
        moveType.RotationX = rx;
        moveType.RotationY = ry;
        moveType.RotationZ = rz;
        moveType.ActorFlags = 1; // 1-idle
        moveType.Flags = MoveTypeFlags.Stopping;
        moveType.DeltaMovement = [0, 0, 0];
        moveType.Stance = GameStanceType.Relaxed;
        moveType.Alertness = MoveTypeAlertness.Idle;
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }

    /// <summary>
    /// Checks if an NPC is attackable wildlife (monster faction 115, hostile relation, or unfactioned).
    /// Safe against missing FactionManager singleton in test/headless environments.
    /// </summary>
    private static bool IsAttackableWildlife(Character bot, Npc npc)
    {
        if (npc.Hp <= 0)
            return false;

        // Faction 115 is standard monster wildlife
        if ((int?)npc.Faction?.Id == 115)
            return true;

        if (npc.Faction == null)
            return true;

        try
        {
            if (!bot.CanAttack(npc))
                return false;

            return bot.GetRelationStateTo(npc) == RelationState.Hostile;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Conflict PvP engagement for one wake: validate the current player
    /// target, engage (approach + cast through CombatDecisionTree), or scan
    /// for the nearest attackable hostile. Mirrors the wildlife hunt shape;
    /// returns true while actively fighting (wildlife hunt skips that wake).
    /// </summary>
    private bool StepPvpEngagement(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state, DateTime now)
    {
        Character? target = null;
        if (state.TargetPlayerObjId != 0)
        {
            target = (UnitResolver != null
                ? UnitResolver(bot.Character, state.TargetPlayerObjId)
                : bot.Character.ParentWorld?.GetUnit(state.TargetPlayerObjId)) as Character;
            var valid = target != null
                && target.Hp > 0
                && IsAttackablePlayer(bot.Character, target)
                && now - state.TargetPlayerEngagedUtc <= TimeSpan.FromSeconds(30);
            if (!valid)
            {
                if (bot.Character.CurrentTarget?.ObjId == state.TargetPlayerObjId)
                {
                    bot.Character.CurrentTarget = null;
                    bot.Character.BroadcastPacket(new SCTargetChangedPacket(bot.Character.ObjId, 0), true);
                }
                state.TargetPlayerObjId = 0;
                state.LastPvpSkillUsed = 0;
                target = null;
            }
        }

        if (target != null)
        {
            if (bot.Character.CurrentTarget?.ObjId != target.ObjId)
            {
                bot.Character.CurrentTarget = target;
                bot.Character.BroadcastPacket(new SCTargetChangedPacket(bot.Character.ObjId, target.ObjId), true);
            }

            var dist = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, target.Transform.World.Position, false);
            var role = CombatDecisionTree.InferRole(bot.Character);
            var engageRange = role == CombatRole.Melee ? HuntMeleeRange : 15.0f;

            if (dist > engageRange)
            {
                var needsMove = actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move };
                if (needsMove)
                {
                    if (actor.ActiveRequest is { IsTerminal: false })
                        _ = actor.Stop();
                    state.PendingLeg = actor.MoveToUnit(target.ObjId, HuntChaseSpeed, TimeSpan.FromSeconds(10));
                }
            }
            else
            {
                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                {
                    _ = actor.Stop();
                    state.PendingLeg = null;
                }

                var angle = MathUtil.CalculateAngleFrom(bot.Character.Transform.World.Position, target.Transform.World.Position);
                bot.Character.Transform.Local.SetRotationDegree(0f, 0f, (float)angle - 90);
                bot.Character.Transform.FinalizeTransform();

                if (now - state.LastPvpCastUtc >= HuntCastInterval)
                {
                    var skillId = CombatDecisionTree.SelectPrioritizedSkill(
                        bot.Character,
                        target,
                        role,
                        null,
                        state.LastPvpSkillUsed);
                    if (skillId > 0)
                    {
                        var castResult = actor.Cast(skillId, target.ObjId);
                        if (castResult.State != ActorLifecycleState.Rejected)
                        {
                            state.LastPvpSkillUsed = skillId;
                            state.LastPvpCastUtc = now;
                        }
                        else
                        {
                            state.LastPvpSkillUsed = 0;
                        }
                    }
                }
            }
            return true;
        }

        var nearby = NearbyCharacterProvider != null
            ? NearbyCharacterProvider(bot.Character, HuntPerceptionRadius)
            : WorldManager.GetAround<Character>(bot.Character, HuntPerceptionRadius);

        Character? best = null;
        var bestDist = float.MaxValue;
        foreach (var candidate in nearby)
        {
            if (candidate.ObjId == bot.Character.ObjId || candidate.IsDead)
                continue;
            if (!IsAttackablePlayer(bot.Character, candidate))
                continue;
            var d = MathUtil.CalculateDistance(bot.Character.Transform.World.Position, candidate.Transform.World.Position, false);
            if (d < bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }

        if (best != null)
        {
            state.TargetPlayerObjId = best.ObjId;
            state.TargetPlayerEngagedUtc = now;
            bot.Character.CurrentTarget = best;
            bot.Character.BroadcastPacket(new SCTargetChangedPacket(bot.Character.ObjId, best.ObjId), true);
            if (actor.ActiveRequest is { IsTerminal: false })
            {
                _ = actor.Stop();
                state.PendingLeg = null;
            }
            return true;
        }
        return false;
    }

    private bool IsAttackablePlayer(Character me, Character foe)
    {
        if (foe.ObjId == me.ObjId || foe.IsDead)
            return false;
        try
        {
            return CanAttackPlayer?.Invoke(me, foe) ?? me.CanAttack(foe);
        }
        catch
        {
            return false;
        }
    }
    /// <summary>
    /// Butcher-skill seam dispatch: the interaction skill for a livestock
    /// doodad's current phase, 0 = not butcherable in this phase.
    /// </summary>
    private uint ResolveButcherSkill(Doodad doodad)
        => ButcherSkillResolver != null ? ButcherSkillResolver(doodad) : TryResolveButcherSkill(doodad);

    /// <summary>
    /// Data-driven butcher-skill resolution for a livestock doodad's CURRENT
    /// phase (no doodad or skill ids assumed — the canonical 1.2 shape only):
    /// the phase carries a DoodadFuncUse whose skill rides the Butcher world
    /// interaction (wi 20 — butcher skills like 도축하기; feed/milk/shear
    /// Use-skills ride wi 19 instead) and whose NextPhase yields meat (carries
    /// loot funcs). Canonical: cow 5782 Use 498 (skill 13972) → 5790
    /// (LootPack 79 → beef); sheep 5649 Use 1283 (skill 13970) → 640 (loot →
    /// mutton). Returns 0 when the phase has no such func (already-butchered
    /// loot phases, sheared phases, empty groups). Safe against missing
    /// DoodadManager/SkillManager singletons in test/headless environments.
    /// </summary>
    private static uint TryResolveButcherSkill(Doodad doodad)
    {
        List<DoodadFunc>? funcs;
        try
        {
            funcs = DoodadManager.Instance.GetFuncsForGroup(doodad.FuncGroupId);
        }
        catch
        {
            return 0;
        }
        if (funcs == null)
            return 0;

        foreach (var func in funcs)
        {
            if (func == null || func.FuncType != "DoodadFuncUse" || func.SkillId == 0 || func.NextPhase <= 0)
                continue;

            SkillTemplate? skillTemplate;
            try
            {
                skillTemplate = SkillManager.Instance.GetSkillTemplate(func.SkillId);
            }
            catch
            {
                continue;
            }
            if (skillTemplate?.Effects.Any(e =>
                    e?.Template is InteractionEffect interaction
                    && interaction.WorldInteraction == WorldInteractionType.Butcher) != true)
                continue;

            List<DoodadFunc>? nextFuncs;
            try
            {
                nextFuncs = DoodadManager.Instance.GetFuncsForGroup((uint)func.NextPhase);
            }
            catch
            {
                continue;
            }
            if (nextFuncs == null
                || !nextFuncs.Any(f => f?.FuncType is "DoodadFuncLootPack" or "DoodadFuncLootItem"))
                continue;

            return func.SkillId;
        }

        return 0;
    }
}
