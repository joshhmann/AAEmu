using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units.Movements;
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

    /// <summary>Unit resolver seam (null → Character.ParentWorld?.GetUnit).</summary>
    public Func<Character, uint, Unit?>? UnitResolver { get; init; }

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

        // 1. Opportunistic wildlife hunt loop
        if (EnableWildlifeHunt)
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
                        if (loot is { IsTerminal: true, State: not ActorLifecycleState.Completed })
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

        // 2. Issue the next leg when idle, not hunting, and a route is active.
        if (state.TargetNpcObjId == 0 && actor.ActiveRequest is not { IsTerminal: false } && state.Path is { IsFinished: false })
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
            && state.PendingLeg is { IsTerminal: false, Action: ActorActionType.Move }
            && state.Path is { IsFinished: false })
        {
            var flat = MathUtil.CalculateDistance(
                bot.Character.Transform.World.Position, state.Path.CurrentTarget, false);
            if (flat <= state.Path.ArrivalRadius)
                _ = actor.Stop();
        }

        // 3b. Route advance on arrival: when the pending Move leg reached a terminal state
        if (state.TargetNpcObjId == 0
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
                    var currentMoveSpeed = state.TargetNpcObjId != 0 ? HuntChaseSpeed : RoamSpeed;
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
                    var currentSpeed = state.TargetNpcObjId != 0 ? HuntChaseSpeed : RoamSpeed;
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
        var effectiveCadence = state.CadenceOverride ?? ActiveCadence;
        return live || routeActive || hunting
            ? (state.CadenceOverride.HasValue ? Task.FromResult<TimeSpan?>(effectiveCadence) : (_cadenceTask ??= Task.FromResult<TimeSpan?>(ActiveCadence)))
            : DormantTask;
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
}
