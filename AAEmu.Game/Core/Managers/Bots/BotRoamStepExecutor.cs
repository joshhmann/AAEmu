using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.GameData;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
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
///
/// Dormancy: a wake with no live request, no route, no hunt and no butcher
/// returns null, which drops the bot from the scheduler's step-driven re-arm
/// (it wakes only on an external request). <see cref="EnableIdleWake"/>
/// (AAEMU_PRESENCE_IDLE_WAKE, default OFF) replaces that null with a low
/// bounded <see cref="IdleWakeInterval"/> so a provisioned citizen keeps
/// re-arming itself and the world can run unattended.
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
    /// <summary>
    /// Idle-wake gate (default OFF). When OFF a bot with no live request, no
    /// route, no hunt and no butcher returns the shared cached
    /// <see cref="DormantTask"/> (null) exactly as before, and the scheduler's
    /// step-driven re-arm drops the bot permanently (only an external wake —
    /// command, event, director — brings it back). Same env style as the
    /// cadence flags above and <see cref="EnableWildlifeHunt"/>:
    /// AAEMU_PRESENCE_IDLE_WAKE=1 / true / True.
    ///
    /// When ON the dormant step instead reports <see cref="IdleWakeInterval"/>,
    /// so the SAME wake source (the executor's own return value) keeps a
    /// provisioned bot self-re-arming at a low bounded rate and the world can
    /// run unattended — no new scheduler, no second wake source, no scheduler
    /// change. Set as a property so a test can drive the flag deterministically
    /// without mutating process environment; production reads the env at
    /// construction. While OFF the calm path is a static flag read and a
    /// return: no field read, no allocation (the JevShadow calm-wake pin).
    /// </summary>
    public bool EnableIdleWake { get; set; } =
        Environment.GetEnvironmentVariable("AAEMU_PRESENCE_IDLE_WAKE") is "1" or "true" or "True";

    /// <summary>
    /// Whether the quest-offer band tracks the CHARACTER's level instead of
    /// staying pinned at the frozen 1..9 starter band. Defaults to true only
    /// when AAEMU_BOT_LEVEL_BAND is set to "1", "true", or "True". OFF (the
    /// default) = byte-identical 1..9 behaviour, zero behavior change.
    ///
    /// WHY a gate: the band is the single biggest cap on autonomous
    /// progression (the filter compares the QUEST's level and never the
    /// character's, so 1..9 confines the bot to starter content for life), but
    /// widening it changes which quests a production bot walks to. Off keeps
    /// every existing green test and the documented A1.1 evidence unchanged;
    /// the widened policy is opt-in and reversible by unsetting the variable.
    /// </summary>
    public bool EnableLevelBand { get; set; } =
        Environment.GetEnvironmentVariable("AAEMU_BOT_LEVEL_BAND") is "1" or "true" or "True";

    /// <summary>
    /// Levels of headroom added above the character's level when
    /// <see cref="EnableLevelBand"/> is on (AAEMU_BOT_LEVEL_BAND_HEADROOM,
    /// integer &gt;= 1; an unparsable, zero or negative value keeps the default).
    /// Read once at construction, like <see cref="DefaultIdleWakeInterval"/>.
    /// </summary>
    public byte LevelBandHeadroom { get; init; } = DefaultLevelBandHeadroom();

    /// <summary>
    /// Default <see cref="LevelBandHeadroom"/> — 5 levels.
    ///
    /// WHY 5 and not "as high as the data goes": the honest ceiling on offer
    /// content is ZONE, not band. 81 of 96 zones carry no quest below level 10
    /// and 53 zones are 30+-only, so a band opened past ~level+5 mostly widens
    /// the candidate set with offers the bot cannot reach in its own region,
    /// not with content it can now take. Five levels of headroom keeps the
    /// accept ranking meaningful (it still prefers the LOWEST in-band level)
    /// and covers the 10-19 gap a level-10 bot has (zone 1 starts there).
    /// </summary>
    private static byte DefaultLevelBandHeadroom()
        => byte.TryParse(Environment.GetEnvironmentVariable("AAEMU_BOT_LEVEL_BAND_HEADROOM"), out var h) && h > 0
            ? h
            : (byte)5;

    /// <summary>
    /// Resolves the inclusive offer band for a character under the current
    /// policy, in the same shape <see cref="QuestDecisionScenario.QuestOptions"/>
    /// carries it so BOTH consumers (the travel pre-filter and the accept
    /// sweep) read one policy value.
    ///
    /// Policy (gate OFF — the default): the frozen 1..9 starter band, returned
    /// verbatim.
    ///
    /// Policy (gate ON):
    ///   BandMin = 1 always — a floor that never rises. Still-completable
    ///   one-time content below the character must stay visible: the 20-29
    ///   band holds ZERO repeatables and 10-19 holds three, so a band that
    ///   rose with the character would present only already-completed
    ///   non-repeatables (which <c>GameplayActor.DiscoverRejectReason</c>
    ///   already hides as COMPLETED_NON_REPEATABLE) and would go structurally
    ///   dead on a second pass.
    ///   BandMax = clamp(character.Level + headroom, BandMin, 49).
    ///
    /// The 49 clamp is the DELIBERATE <c>LEVEL = 50</c> sentinel decision:
    /// quest_contexts.LEVEL is display data, not the requirement, and 50 means
    /// "ungated" rather than "level 50" — 1086 rows sit there, 642 of them
    /// carrying no requirement row at all. A window that simply followed the
    /// character would clamp itself onto that sentinel and admit all ~1086 at
    /// character level 45 by accident. This policy treats them as a separate,
    /// explicitly-excluded set: out of the band, so they are reachable only if
    /// someone deliberately widens the clamp. They remain discoverable through
    /// <c>GameplayActor.IsDiscoverable</c> and the non-banded engine paths.
    ///
    /// FAIL-CLOSED: an unreadable level returns the 1..9 band rather than
    /// guessing a wider one. Deterministic — pure arithmetic on the
    /// character's own level, no clock, no randomness.
    /// </summary>
    internal static QuestDecisionScenario.QuestOptions ResolveOfferBand(bool levelBand, byte headroom, byte? characterLevel)
    {
        var options = new QuestDecisionScenario.QuestOptions();
        if (!levelBand || characterLevel is not { } level)
            return options;
        options = options with
        {
            BandMin = 1,
            BandMax = (byte)Math.Clamp(level + headroom, options.BandMin, LevelBandSentinelExclusiveCeiling)
        };
        return options;
    }

    /// <summary>
    /// Highest band value the level-relative policy will ever emit: 49, one
    /// below the <c>quest_contexts.LEVEL = 50</c> "ungated" sentinel, which is
    /// never treated as a real level.
    /// </summary>
    internal const byte LevelBandSentinelExclusiveCeiling = 49;

    /// <summary>
    /// Per-character offer band for this wake. The character's level is read
    /// once here and reused by both consumers, so the travel pre-filter and
    /// the accept sweep can never disagree within a wake.
    /// </summary>
    private QuestDecisionScenario.QuestOptions OfferBandFor(GameplayActor actor)
        => ResolveOfferBand(EnableLevelBand, LevelBandHeadroom, actor.Character?.Level);

    /// <summary>
    /// Re-arm interval reported by an otherwise-dormant wake while
    /// <see cref="EnableIdleWake"/> is on. Default 5s, overridable via
    /// AAEMU_PRESENCE_IDLE_WAKE_SECONDS (integer seconds &gt; 0).
    ///
    /// WHY 5s: an idle wake does no world work — the executor's legs are all
    /// absent (that is what makes the step dormant), so the cost per wake is
    /// one arbitration pass plus one cached no-op actor tick. The budget the
    /// cadence has to respect is therefore the BUDGET-DRIVER cost per embodied
    /// char (DB writes &lt;= 500/min), not the region's per-tick work: 5s = 12
    /// wakes/min/char, three orders of magnitude below any world write rate,
    /// and ~75x below <see cref="ActiveCadence"/>. It is also the same 2s-class
    /// granularity the proximity sweep already runs at
    /// (<c>PopulationDirectorOptions.ProximitySweepIntervalMs</c> = 2000ms),
    /// so an idle bot is re-evaluated on a cadence comparable to the world's own
    /// re-arm cadence rather than at a rate that only adds scheduler queue
    /// depth. A non-positive override falls back to the default rather than
    /// reporting a zero delay (the scheduler clamps &lt;=0 to its scan
    /// interval, which would silently turn an idle cadence into a 100 Hz spin).
    /// </summary>
    public TimeSpan IdleWakeInterval { get; init; } = DefaultIdleWakeInterval();


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
    /// The distance one quest-travel leg can HONESTLY walk: the leg's own
    /// constants (<see cref="RoamSpeed"/> m/s x <see cref="RoamLegTimeout"/>
    /// seconds) cut by <see cref="BotPath.TravelChunkEnvelopeMargin"/>. At the
    /// production defaults that is 2.5 x 60 x 0.5 = 75 m of leg.
    ///
    /// WHY THE MARGIN IS 0.5. The raw product (150 m) is the reach of a leg
    /// that walks in a straight line at full speed from a standing start and
    /// arrives exactly as the budget expires. A real leg never does: the bot
    /// re-arms, turns and accelerates from rest on every waypoint, and the
    /// navigation budget can also be consumed by the actor's own
    /// no-progress/stuck detection. So the raw product is an OPTIMISTIC
    /// figure, and sizing chunks at it would reproduce the defect one leg
    /// earlier. Halving it buys a 2x margin against that loss, which
    /// comfortably covers the observed 25.9-31.8 m legs that completed plus
    /// acceleration ramp-up, while costing only more waypoints on a long
    /// trek (the route is the same straight line, so extra waypoints are
    /// extra cheap Stop/turn events, not detours). Deliberately derived from
    /// RoamSpeed/RoamLegTimeout rather than hardcoded, so retuning either
    /// constant retunes the chunking automatically.
    ///
    /// This is a DISTANCE, not a policy change: the budget itself is
    /// untouched and still correctly halts a leg mid-flight.
    /// </summary>
    internal float QuestTravelChunkMeters => (float)(
        RoamSpeed * (RoamLegTimeout > TimeSpan.Zero ? RoamLegTimeout.TotalSeconds : 0d)
        * BotPath.TravelChunkEnvelopeMargin);

    /// <summary>
    /// Builds a quest-travel ROUTE from <paramref name="from"/> (the bot's live
    /// world position) to <paramref name="target"/>, subdivided into legs of
    /// <see cref="QuestTravelChunkMeters"/> (see <see cref="BotPath.PathTo(Vector3, Vector3, float, float, float, float)"/>
    /// for why, and for the measured defect this fixes). Inside one leg it is
    /// the ordinary single-leg <c>PathTo</c> — unchanged behaviour.
    ///
    /// Scope: this is how a quest-travel ROUTE is CONSTRUCTED. It does not
    /// touch <c>MoveTo</c>, <see cref="RoamLegTimeout"/>, <c>GameplayActor</c>,
    /// or the movement integration, and it adds no pathfinding — an obstacle
    /// between the bot and the target is still an obstacle.
    /// </summary>
    private BotPath QuestTravelRouteTo(Vector3 from, Vector3 target)
        => BotPath.PathTo(from, target, QuestTravelChunkMeters, QuestTravelChunkMeters);

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

    /// <summary>
    /// Speed of the survival flee leg (default = 4.5 m/s — the same retreat sprint
    /// the combat/pursuit legs use; breaking contact is not a walk).
    /// </summary>
    public float SurvivalFleeSpeed { get; init; } = 4.5f;

    /// <summary>
    /// Per-leg budget of the survival flee leg (default = 10s — the combat spacing
    /// leg's budget; a retreat that cannot walk 25 m in ten seconds re-decides).
    /// </summary>
    public TimeSpan SurvivalFleeLegTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Movement-owner tag of the survival flee leg. Telemetry only, like every
    /// other owner tag — it is what lets the flee's own live leg be recognised
    /// (and held rather than restarted) on the next wake.
    /// </summary>
    public const string SurvivalFleeMoveOwner = "SURVIVAL_FLEE";
    /// <summary>
    /// Link-1d teleport-jump threshold: the largest per-wake displacement any
    /// single-wake travel can produce (far above a 60s roam leg's reach at
    /// walk/chase speeds). Only harness/GM teleports move that far per tick —
    /// production wakes are unaffected.
    /// </summary>
    public const float TeleportJumpThresholdMeters = 500f;

    /// <summary>Movement-owner tag of quest-travel route legs (armed by ArmQuestTravel).</summary>
    public const string QuestTravelMoveOwner = "QUEST_TRAVEL";

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
    /// Quest-terminal feed (E-66 Gap 2): per completed quest the owning
    /// module's purpose feed — (characterId, questId, engine-confirmed
    /// completed). Null (default) = no module enrolled: zero behavior change
    /// (one null field read where the runtime is constructed). Set once at
    /// composition (production wires the QuestBootstrap module); copied onto
    /// every per-bot runtime at construction so generation rebinding keeps it.
    /// </summary>
    public Action<uint, uint, bool>? QuestTerminalSink { get; set; }

    /// <summary>Returns an earned quest retirement to the existing retained owner.</summary>
    public Action<uint, QuestGiveUp>? QuestGiveUpSink { get; set; }

    /// <summary>
    /// Chosen-quest purpose feed (HIGH 1): the owning module's retained choice
    /// per bot (0 = none). Read once per quest wake and carried on
    /// <c>QuestOptions.ChosenQuestId</c> into the existing execution path, so
    /// the recorded choice scopes the plan set and the resulting request is
    /// verified against it. Null (default) = no module enrolled: zero
    /// behavior change. Production wires the QuestBootstrap module's
    /// <c>GetChosenQuest</c>; the executor never resolves DI.
    /// </summary>
    public Func<uint, uint>? QuestChosenProvider { get; set; }

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
        public ulong Generation { get; set; }
        public required IGameplayActor Actor { get; init; }
        public BotPath? Path { get; set; }
        public ActorRequest? PendingLeg { get; set; }
        public DateTime LastBroadcastUtc { get; set; } = DateTime.MinValue;
        public long? LastBroadcastTicks { get; set; }
        public long? NextBroadcastTicks { get; set; }
        public Vector3? LastBroadcastPosition { get; set; }
        /// <summary>
        /// Link-1d teleport-jump detection: the bot's world position at the top of
        /// the previous wake. When the per-tick displacement exceeds
        /// <see cref="TeleportJumpThresholdMeters"/> while a QUEST_TRAVEL route is
        /// armed, the harness/GM teleported the bot after the route was armed — the
        /// stale live Move would starve the quest leg via the 0a busy-actor guard
        /// forever. Null until the first wake stores it. Memory-only.
        /// </summary>
        public Vector3? LastTickPosition { get; set; }
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
        /// Copper-bootstrap quest-work flag: set when the quest leg ran work
        /// this wake. Consumed by the hunt/butcher/route gates so quest work
        /// preempts wildlife but never party handling or PvP. Independent of
        /// the needs flag: the arbiter holds ONE activity per wake, so at most
        /// one of the two legs fires — both flags stay readable for tests.
        /// </summary>
        public bool QuestLegActive { get; set; }

        /// <summary>Generation-scoped discovery intent; route execution keeps its ordinary owner.</summary>
        public bool QuestDiscoveryActive { get; set; }

        /// <summary>
        /// Quest-travel observability: the world position the quest leg is
        /// walking toward (null = none) and why. Memory-only like the rest of
        /// the state (a restart re-resolves).
        /// </summary>
        public Vector3? QuestTravelTarget { get; set; }
        /// <summary>Human-readable reason for the current quest-travel decision.</summary>
        public string QuestTravelReason { get; set; } = "";

        public uint? QuestTravelDecisionQuestId { get; set; }
        public string? QuestTravelDecisionGoal { get; set; }
        public string? QuestTravelDecisionPolicy { get; set; }
        public string? QuestTravelDecisionCycleId { get; set; }
        public QuestStatus? QuestTravelDecisionStatus { get; set; }

        /// <summary>
        /// Branch-1b re-arm bound: the last armed objective-source spawner
        /// position + template, consecutive arms to it, and the active-quest
        /// id key those arms were counted against. Caps 3 consecutive arms to
        /// the same spawner (1.0 m tolerance) — a DOODAD source only; further
        /// arms withhold with the objective-source-unreachable reason until the
        /// active-quest set changes. A PREY source is exempt: the funnel's
        /// 25 m census is exactly what walking has to fix, so nothing else can
        /// materialize it (see <see cref="ArmQuestTravel"/>). Memory-only like
        /// the rest of the state.
        /// </summary>
        public Vector3? QuestObjectiveSourcePos { get; set; }

        /// <summary>Last armed objective-source template (0 = none).</summary>
        public uint QuestObjectiveSourceTemplate { get; set; }

        /// <summary>Consecutive arms to <see cref="QuestObjectiveSourcePos"/>.</summary>
        public int QuestObjectiveSourceArms { get; set; }

        /// <summary>Sorted active-quest id key the arm count was counted against.</summary>
        public string QuestObjectiveSourceQuestKey { get; set; } = "";

        /// <summary>
        /// Movement-owner tag staged for the NEXT route-layer MoveTo leg
        /// (QUEST_TRAVEL when armed by <c>ArmQuestTravel</c>, ROAM for patrol
        /// routes, OTHER:needs-farm-travel for soil/crop approaches).
        /// Consumed (not cleared) by the route dispatch sites — the same
        /// route keeps its owner across waypoint legs until a new route is
        /// armed. Telemetry only — never read by behavior.
        /// </summary>
        public string? PendingMoveOwner { get; set; }

        /// <summary>
        /// Quest-decision observability (diagnostic): the last quest-leg
        /// outcome summary for this bot — landed action + detail, or the
        /// DECIDE/EXECUTE/RUN fail stage + reason (includes the sweep tallies
        /// and actor pose). Overwritten every quest wake; memory-only like the
        /// rest of the state. Never consumed by behavior — observe payload only.
        /// </summary>
        public string QuestDecideDetail { get; set; } = "";

        /// <summary>
        /// Homestead progression flag: set when the homestead leg ran work this wake.
        /// Consumed by the hunt/butcher/route gates so homestead work preempts
        /// wildlife but never party handling or PvP.
        /// </summary>
        public bool HomesteadLegActive { get; set; }

        /// <summary>
        /// Survival wake ownership flag: set when the ELECTED-WAKE survival gate found
        /// a vetoing verdict (a flee, or the incapacitated hold) and therefore owns
        /// this wake. While set, every other leg this wake stands down. Readable for
        /// tests and diagnostics; the gate re-decides every wake, so the flag is
        /// overwritten rather than latched.
        /// </summary>
        public bool SurvivalWakeOwned { get; set; }

        /// <summary>
        /// Tier 0 needs-work flag: set when the needs leg ran work this wake.
        /// Consumed by the hunt/butcher/route gates so needs work preempts
        /// wildlife but never party handling or PvP.
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
        /// defer cause, stale-drop cause, discard cause), plus the needs brain's
        /// own decision fragment on the arms the brain decides
        /// (<c>[brain=verdict=…:reason=…:verb=…:routed=…:dispatch=…]</c> — the
        /// decision that produced the leg, the named hold/bounded-spiral demand the
        /// wake fell back on, the maturity wait, or the plant/harvest dispatch). Feeds
        /// log lines and test asserts; never a tracing framework, and never read by
        /// behavior.
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

        /// <summary>Timestamp of the last leisure micro-wander step while WaitingMaturity.</summary>
        public DateTime LastLeisureStepUtc { get; set; } = DateTime.MinValue;

        /// <summary>Next loiter duration at a leisure position before moving again.</summary>
        public TimeSpan NextLeisureInterval { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>Timestamp until which the bot is browsing/shopping at a merchant before buying.</summary>
        public DateTime ShoppingUntilUtc { get; set; } = DateTime.MinValue;

        /// <summary>Timestamp until which the bot loiters after planting before moving again.</summary>
        public DateTime PlantingUntilUtc { get; set; } = DateTime.MinValue;

        /// <summary>Timestamp until which the bot loiters after harvesting before moving again.</summary>
        public DateTime HarvestingUntilUtc { get; set; } = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<uint, BotRoamState> _states = [];
    private readonly ConcurrentDictionary<uint, DateTime> _lastStepUtc = [];
    // Generation sidecars for GetOrAdd caches recreated inside a wake
    // (quest runtime, homestead runner/context). The E2E turn-in withhold has
    // no in-wake GetOrAdd site (set externally, evicted at the gate below),
    // so it needs no sidecar. _lastStepUtc is rewritten every wake.
    private readonly ConcurrentDictionary<uint, ulong> _questRuntimeGenerations = [];
    private readonly ConcurrentDictionary<uint, ulong> _homesteadGenerations = [];

    // Cached already-completed step results: the scheduler GetResult()s every
    // return, so a fresh Task<TimeSpan?> allocation per wake was pure churn at
    // ~10 wakes/sec/bot. Tasks are immutable once completed — safe to reuse.
    private static readonly Task<TimeSpan?> DormantTask = Task.FromResult<TimeSpan?>(null);
    private Task<TimeSpan?>? _cadenceTask;
    // The one bounded idle interval, decided ONCE at construction (the flag
    // itself is a settable per-instance test seam, so the interval must not
    // depend on a mutable property). Cached like DormantTask/_cadenceTask so a
    // re-arming idle wake allocates no Task at all.
    private Task<TimeSpan?>? _idleWakeTask;

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

        SupersedeQuestTravelRoute(state, "route replaced");
        state.Path = path;
        if (path != null)
        {
            // Telemetry: patrol routes own their legs as ROAM until a
            // quest-travel or farm approach re-arms the route.
            state.PendingMoveOwner = "ROAM";
            Logger.Info("Roam route assigned: bot {CharacterId} — {Count} waypoints ({Mode})",
                characterId, path.Waypoints.Count, path.Mode);
        }
    }
    /// <summary>
    /// Test/observability seam: the currently assigned route for a bot
    /// (null when none was set or it was cleared). Used by the rig to prove
    /// route arming/clearing without stepping the executor.
    /// </summary>
    internal BotPath? GetRoamRoute(uint characterId)
        => _states.TryGetValue(characterId, out var state) ? state.Path : null;
    /// <summary>
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

    /// <summary>
    /// Non-creating live-actor read (E-66 Gap 1): the bot's quest-leg actor,
    /// or null when the bot has no executor state yet. Never creates — the
    /// <see cref="QuestLegActor"/> resolver docstring forbids creation, so
    /// production prefers this read over <see cref="GetOrCreateActor"/>.
    /// </summary>
    public IGameplayActor? TryGetActor(uint characterId)
        => _states.TryGetValue(characterId, out var state) ? state.Actor : null;

    /// <summary>
    /// Read-only diagnostic hook for the Brain Inspector dashboard
    /// (<c>GET /api/bots/brain/*</c>): returns the quest behavior runtime
    /// hosting this bot's quest leg, when any wake has created one (null =
    /// never ticked). Pure dictionary lookup — never creates, ticks, or
    /// mutates; behavior and cadence are untouched.
    /// </summary>
    internal bool TryGetQuestRuntime(uint characterId, out BotBehaviorRuntime? runtime)
        => _questRuntimes.TryGetValue(characterId, out runtime);

    /// <summary>
    /// Death release (M6.2 death watch): drops the quest running-hold for one
    /// bot so the first post-respawn turn re-decides from the fresh listing.
    /// Observation-only on the runtime plus the owning leg preempted through
    /// the actor — the same discipline as the survival-flee supersede. No
    /// terminal is consumed, no failure recorded: death is not a step
    /// failure. Discovery movement is retired even before a quest runtime
    /// exists. No-op when the bot has no executor state.
    /// </summary>
    public void ReleaseQuestHoldOnDeath(uint characterId, string reason)
    {
        if (!_states.TryGetValue(characterId, out var state))
            return;
        RetireQuestDiscovery(state, reason);
        if (_questRuntimes.TryGetValue(characterId, out var runtime)
            && _questRuntimeGenerations.TryGetValue(characterId, out var runtimeGen)
            && runtimeGen == state.Generation)
        {
            if (state.Actor.ActiveRequest is { IsTerminal: false })
                state.Actor.PreemptCurrent(reason);
            runtime.ReleaseOnDeath(reason);
            state.PendingLeg = null;
        }
    }

    private static void RetireQuestDiscovery(BotRoamState state, string reason)
    {
        if (!state.QuestDiscoveryActive)
            return;
        if (state.PendingLeg is { IsTerminal: false, Action: ActorActionType.Move } pending
            && pending.MoveOwner == QuestTravelMoveOwner
            && ReferenceEquals(state.Actor.ActiveRequest, pending))
            state.Actor.PreemptCurrent(reason);
        SupersedeQuestTravelRoute(state, reason);
        state.QuestDiscoveryActive = false;
    }

    /// <summary>
    /// Telemetry: stages the movement-owner tag on the bot's actor for the
    /// immediately following Move/MoveToUnit dispatch (the pending-owner
    /// pattern — consumed by GameplayActor.NewRequest). No-op for foreign
    /// actor implementations. Never gates or alters dispatch.
    /// </summary>
    private static void StageMoveOwner(IGameplayActor actor, string owner)
    {
        if (actor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(owner);
    }

    private static void PreemptQuestTravelLeg(BotRoamState state, string reason)
    {
        if (state.PendingMoveOwner == QuestTravelMoveOwner
            && state.PendingLeg is { IsTerminal: false, Action: ActorActionType.Move } pending
            && pending.MoveOwner == QuestTravelMoveOwner
            && ReferenceEquals(state.Actor.ActiveRequest, pending))
            state.Actor.PreemptCurrent(reason);
    }

    private static void ClearQuestTravelPurpose(BotRoamState state)
    {
        state.QuestTravelDecisionQuestId = null;
        state.QuestTravelDecisionGoal = null;
        state.QuestTravelDecisionPolicy = null;
        state.QuestTravelDecisionCycleId = null;
        state.QuestTravelDecisionStatus = null;
    }

    private static void CaptureQuestTravelPurpose(BotRoamState state, GameplayActor actor,
        QuestDecisionScenario.QuestOptions band, string? activity)
    {
        ClearQuestTravelPurpose(state);
        if (band.ChosenQuestId == 0)
            return;
        state.QuestTravelDecisionQuestId = band.ChosenQuestId;
        state.QuestTravelDecisionGoal = activity;
        state.QuestTravelDecisionPolicy = band.PolicyVersion;
        state.QuestTravelDecisionCycleId = band.CycleId;
        if (actor.Character.Quests.ActiveQuests.TryGetValue(band.ChosenQuestId, out var quest))
            state.QuestTravelDecisionStatus = quest.Status;
    }

    private void ValidateQuestTravelAuthorization(PlayerBotRuntime bot, BotRoamState state)
    {
        if (state.PendingMoveOwner != QuestTravelMoveOwner)
            return;
        var chosen = QuestChosenProvider?.Invoke(bot.CharacterId) ?? 0u;
        if (state.QuestTravelDecisionQuestId is not { } parent)
        {
            if (chosen != 0)
                SupersedeQuestTravelRoute(state, "selected quest changed");
            return;
        }
        if (parent != chosen || state.Actor is not GameplayActor actor
            || !QuestTravelPhaseAuthorized(actor, state, parent))
            SupersedeQuestTravelRoute(state, "selected quest authorization lost");
    }

    private static bool QuestTravelPhaseAuthorized(GameplayActor actor, BotRoamState state, uint parent)
    {
        var quests = actor.Character.Quests;
        if (!quests.ActiveQuests.TryGetValue(parent, out var quest))
            return state.QuestTravelDecisionStatus == null && actor.IsDiscoverable(parent);
        if (state.QuestTravelDecisionStatus != quest.Status || quest.Status == QuestStatus.Completed)
            return false;
        if (quest.Status == QuestStatus.Ready)
        {
            if (quest.QuestSteps.TryGetValue(QuestComponentKind.Ready, out var ready))
                foreach (var component in ready.Components.Values)
                    foreach (var act in component.Acts)
                        if (act.Template is QuestActConReportNpc report)
                            return actor.Character.ParentWorld?.GetNpcByTemplateId(report.NpcId) == null;
            return true;
        }
        // Cached objective facts and credit only: retained movement must never
        // copy the spawner table or re-run nearest-source/perception selection.
        if (!quest.QuestSteps.TryGetValue(QuestComponentKind.Progress, out var progress))
            return true;
        QuestFixtureRow row;
        try
        {
            row = QuestFixtureRow.FromQuestData(parent);
        }
        catch
        {
            return true; // Unknown source availability is not authoritative absence.
        }
        if (row.GatherActId == 0 && row.InteractActId == 0)
            return true;
        var gatherKnown = row.GatherActId == 0;
        var interactKnown = row.InteractActId == 0;
        foreach (var component in progress.Components.Values)
        {
            foreach (var act in component.Acts)
            {
                var template = act.Template;
                if (template is QuestActObjItemGather && template.ActId == row.GatherActId)
                    gatherKnown = true;
                else if (template is QuestActObjInteraction && template.ActId == row.InteractActId)
                    interactKnown = true;
                else
                    continue;
                if (template.GetObjective(quest) < Math.Max(1, template.Count))
                    return true;
            }
        }
        return !gatherKnown || !interactKnown;
    }

    private ActorRequest IssueRouteMove(IGameplayActor actor, BotRoamState state, Vector3 target)
    {
        var questRoute = state.PendingMoveOwner == QuestTravelMoveOwner;
        StageMoveOwner(actor, state.PendingMoveOwner ?? "ROAM");
        if (questRoute)
        {
            actor.SetPendingDecision(state.QuestTravelDecisionGoal, state.QuestTravelDecisionPolicy,
                1, 0, state.QuestTravelDecisionCycleId, state.QuestTravelDecisionQuestId);
            actor.SetPendingCycleId(state.QuestTravelDecisionCycleId);
        }
        try
        {
            return actor.MoveTo(target, RoamSpeed, RoamLegTimeout);
        }
        finally
        {
            if (questRoute)
            {
                actor.SetPendingDecision(null, null, 0, 0, null, null);
                actor.SetPendingCycleId(null);
            }
        }
    }

    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = TimeProvider.GetUtcNow().UtcDateTime;
        if (!_states.TryGetValue(bot.CharacterId, out var state) || state.Generation != bot.Generation)
        {
            if (state != null)
                RetireQuestDiscovery(state, "discovery activation changed");
            if (state != null)
                SupersedeQuestTravelRoute(state, "generation changed");
            // Generation binding: stale caches from a prior activation are
            // evicted (actor + sidecar entries) and recreated bound to the
            // live generation. Fresh bots (gen 0 runtime, no state) just create.
            _states.TryRemove(bot.CharacterId, out _);
            _questRuntimes.TryRemove(bot.CharacterId, out _);
            _questRuntimeGenerations.TryRemove(bot.CharacterId, out _);
            _homesteadRunners.TryRemove(bot.CharacterId, out _);
            _homesteadContexts.TryRemove(bot.CharacterId, out _);
            _homesteadGenerations.TryRemove(bot.CharacterId, out _);
            _questTurnInWithheld.TryRemove(bot.CharacterId, out _);
            _lastStepUtc.TryRemove(bot.CharacterId, out _);
            state = new BotRoamState { Actor = ActorFactory(bot.Character), Generation = bot.Generation };
            _states[bot.CharacterId] = state;
        }
        var actor = state.Actor;
        if (state.QuestDiscoveryActive
            && ActiveActivityProvider?.Invoke(bot.CharacterId) != QuestBootstrapActivityModule.DiscoveryActivityName)
            RetireQuestDiscovery(state, "discovery activity superseded");
        ValidateQuestTravelAuthorization(bot, state);

        // Link-1d teleport-jump detection: the harness teleports the bot AFTER a
        // branch-2 quest-travel route is armed, so the live QUEST_TRAVEL Move keeps
        // the actor busy and the 0a busy-actor guard starves StepQuestLeg forever.
        // A >500m per-wake jump exceeds any single-wake travel (only tests/GM
        // teleports move that far per tick), so drop the stale route and let the
        // next quest wake re-decide. Scoped to QUEST_TRAVEL exactly — live
        // non-quest legs are never preempted here.
        // TEMPORARY live-lane diagnostic (read-only log, never branched on): wake-path instance witness for EVERY scheduler wake.
        LogInstanceWake("wake-top", bot);
        var currentTickPos = bot.Character.Transform.World.Position;
        if (state.LastTickPosition is { } lastTickPos
            && state.PendingMoveOwner == QuestTravelMoveOwner
            && Vector3.Distance(lastTickPos, currentTickPos) > TeleportJumpThresholdMeters)
        {
            // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
            LogQuestTravelHaltCaller(bot, state, "other", "teleport-jump");
            PreemptQuestTravelLeg(state, "teleport jump during quest travel");
            SupersedeQuestTravelRoute(state, "teleport");
            ResetObjectiveSourceArms(state, "");
        }
        state.LastTickPosition = currentTickPos;

        // Jev shadow (Stage 1, default OFF): reset this actor's frozen brain slots
        // at the START of every wake, so a wake on which a brain did not run
        // reports it absent instead of inheriting the previous wake's inputs. One
        // static flag read; flag-off returns before any dictionary work.
        if (JevShadow.Enabled)
            JevShadow.BeginWake(actor.ActorId);

        // Soak finding (c): the roam executor owns the throttled (4-6 Hz)
        // movement broadcast in step 3b — the actor's own per-apply
        // broadcast would double-send every wake at ~10 Hz and was the
        // dominant heap-churn source under scheduler-driven roam. States can
        // be created by several entry points (SetRoamRoute included), so the
        // flag is enforced here rather than only at one creation site.
        if (actor is GameplayActor concreteActor && concreteActor.BroadcastMovement)
            concreteActor.BroadcastMovement = false;

        // 0- SURVIVAL ELECTED-WAKE GATE, evaluated FIRST — before party handling,
        // before any module election branch: the survival rule runs where the wake
        // actually goes, so the critical-in-fight flee reaches the actor even when
        // the arbiter elects recovery.rest or nothing at all. Only a Flee verdict
        // owns the wake; Hold/Recover leave every existing branch exactly as it is,
        // so healthy wakes are byte-identical to the pre-gate behavior.
        state.SurvivalWakeOwned = StepSurvivalWake(bot, actor, state);

        // 0. Party coordination (PB-002 follow & assist):
        // Auto-accept pending party invites, and if in a party as member, follow/assist the leader.
        bool handledByParty = false;
        try
        {
            var tm = TeamManager.Instance;
            if (tm != null && !state.SurvivalWakeOwned)
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
                                    StageMoveOwner(actor, "OTHER:party-assist");
                                    state.PendingLeg = actor.MoveToUnit(leaderTarget.ObjId, HuntChaseSpeed, TimeSpan.FromSeconds(5));
                                }
                            }
                            else
                            {
                                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                                {
                                    // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                                    LogQuestTravelHaltCaller(bot, state, "settle", "party-assist-in-range");
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
                                    {
                                        // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                                        LogQuestTravelHaltCaller(bot, state, "other", "party-follow-retrack");
                                        _ = actor.Stop();
                                    }
                                    StageMoveOwner(actor, "OTHER:party-follow");
                                    state.PendingLeg = actor.MoveTo(leaderPos, HuntChaseSpeed, TimeSpan.FromSeconds(5));
                                }
                            }
                            else if (distToLeader <= 3.0f)
                            {
                                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                                {
                                    // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                                    LogQuestTravelHaltCaller(bot, state, "settle", "party-follow-arrived");
                                    actor.PreemptCurrent("party follow arrived");
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
        if (!handledByParty && !state.SurvivalWakeOwned
            && ActiveActivityProvider?.Invoke(bot.CharacterId) is string pvpActivity
            && pvpActivity.StartsWith("conflict.", StringComparison.Ordinal))
        {
            pvpEngaged = StepPvpEngagement(bot, actor, state, now);
        }
        // 0b. Tier 0 needs-farm leg: while the arbiter holds a needs.*
        // activity, run one NeedsDecisionScenario leg per wake against the
        // bot's existing actor (GetOrCreateActor — the SAME actor the
        // scheduler ticks, never a second instance). Needs work preempts
        // wildlife/butcher/route but never party handling or PvP.
        // Patrol/hunt movement is paused for the decision tick: a live Move
        // leg would TryBegin-skip the leg forever on hunt-enabled
        // deployments (.165 finding: needs.farm active all session, leg body
        // never ran). Farm-travel movement (Traveling phase) and executing
        // trade legs are never interrupted — the former advances through the
        // route layer, the latter ARE the landed work. Null provider
        // preserves today's behavior exactly.
        state.NeedsLegActive = false;
        if (!handledByParty && !pvpEngaged && !state.SurvivalWakeOwned
            && ActiveActivityProvider?.Invoke(bot.CharacterId) is string needsActivity
            && needsActivity.StartsWith("needs.", StringComparison.Ordinal))
        {
            if (actor.ActiveRequest is { IsTerminal: false } liveNeeds
                && liveNeeds.Action == ActorActionType.Move
                && state.NeedsFarmPhase != NeedsFarmLoopPhase.Traveling)
            {
                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                LogQuestTravelHaltCaller(bot, state, "other", "needs-preempt");
                _ = actor.Stop();
            }
            if (actor.ActiveRequest is not { IsTerminal: false })
            {
                state.NeedsLegActive = StepNeedsFarmLeg(bot, actor, state);
            }
        }

        // 0a. Copper-bootstrap quest leg: while the arbiter holds a quest.*
        // activity, run one runtime-hosted QuestBehavior tick per wake against
        // the bot's existing actor (the SAME actor the scheduler ticks, never a
        // second instance). Quest work preempts wildlife/butcher/route but
        // never party handling or PvP — same arbitration discipline as 0b.
        // Skipped while the actor is busy (TryBegin semantics — the
        // hunt-engage precedent). Null provider preserves today's behavior
        // exactly. The arbiter holds ONE activity per wake, so at most one
        // of the 0a/0b legs fires; both flags stay readable for tests.
        state.QuestLegActive = false;
        // TEMPORARY live-lane diagnostic (read-only witness, never branched on): 0a scheduling-gate snapshot — names which gate holds StepQuestLeg off this wake.
        if (CharacterQuests.DiagEnabled)
        {
            var gateActivity = ActiveActivityProvider?.Invoke(bot.CharacterId);
            var gateQuestHeld = gateActivity != null && gateActivity.StartsWith("quest.", StringComparison.Ordinal);
            var gateActorBusy = actor.ActiveRequest is { IsTerminal: false };
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-sched party={handledByParty} pvp={pvpEngaged} survivalOwned={state.SurvivalWakeOwned} activity={gateActivity ?? "<null>"} questHeld={gateQuestHeld} actorBusy={gateActorBusy} scheduled={!handledByParty && !pvpEngaged && !state.SurvivalWakeOwned && gateQuestHeld && !gateActorBusy}");
            // TEMPORARY live-lane diagnostic (read-only witness, never branched on): busy-holder identity — which live request (action/owner/trace/dest/detail) holds the 0a actor-idle guard off this wake; actorBusy with no live holder is itself the finding.
            var busyHolder = actor.ActiveRequest;
            var busyHolderLive = busyHolder is { IsTerminal: false };
            var busyHolderDest = busyHolder?.Destination;
            var busyHolderDestText = busyHolderDest.HasValue ? "(" + busyHolderDest.Value.X.ToString("F1") + "," + busyHolderDest.Value.Y.ToString("F1") + "," + busyHolderDest.Value.Z.ToString("F1") + ")" : "-";
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-busy-holder actorBusy={gateActorBusy} holderLive={busyHolderLive} holderAction={busyHolder?.Action.ToString() ?? "-"} holderOwner={busyHolder?.MoveOwner ?? "-"} holderState={busyHolder?.State.ToString() ?? "-"} holderTrace={busyHolder?.TraceId.ToString() ?? "-"} holderTarget={busyHolder?.TargetId ?? 0} holderDest={busyHolderDestText} holderDetail={busyHolder?.Detail ?? "-"} pendingOwner={state.PendingMoveOwner ?? "-"} pendingLegState={state.PendingLeg?.State.ToString() ?? "-"} pendingLegOwner={state.PendingLeg?.MoveOwner ?? "-"} routeLive={state.Path is { IsFinished: false }}");
        }
        // P0.4 single owner: the runtime owns a Running job. A quest wake
        // whose actor still carries the runtime's exact owned request routes
        // THROUGH the quest leg (hold/consume), even though the actor is
        // busy — no other fallback may replace it while owned. Every other
        // busy wake keeps the idle guard exactly.
        var ownedBusy = actor.ActiveRequest is { IsTerminal: false } liveBusy
            && _questRuntimes.TryGetValue(bot.CharacterId, out var ownedRuntime)
            && _questRuntimeGenerations.TryGetValue(bot.CharacterId, out var ownedGen)
            && ownedGen == state.Generation
            && ownedRuntime.OwnsRunningRequest(liveBusy);
        var questActivity = ActiveActivityProvider?.Invoke(bot.CharacterId);
        if (!handledByParty && !pvpEngaged && !state.SurvivalWakeOwned
            && questActivity == QuestBootstrapActivityModule.DiscoveryActivityName)
        {
            state.QuestDiscoveryActive = true;
            // Pending route movement owns the actor until its ordinary terminal.
            // Never scan again while running, nor dispatch unscoped quest work.
            if (actor is GameplayActor discoveryActor && actor.ActiveRequest is not { IsTerminal: false })
            {
                if (IsForeignRouteInterruption(state))
                    SupersedeQuestTravelRoute(state, "discovery move interrupted");
                ArmQuestTravel(bot, discoveryActor, state, OfferBandFor(discoveryActor),
                    QuestBootstrapActivityModule.DiscoveryActivityName);
            }
        }
        else if (!handledByParty && !pvpEngaged && !state.SurvivalWakeOwned
            && questActivity != null
            && questActivity.StartsWith("quest.", StringComparison.Ordinal)
            && (actor.ActiveRequest is not { IsTerminal: false } || ownedBusy))
        {
            state.QuestLegActive = StepQuestLeg(bot, actor, state, questActivity);
        }
        else if (state.SurvivalWakeOwned && _questRuntimes.TryGetValue(bot.CharacterId, out var fleeQuest))
        {
            // A flee owns the wake: the quest tick is superseded this wake
            // (observation only — any live preemption stays with the owning
            // leg via PreemptCurrent, the same discipline as the electing
            // activity moving away).
            fleeQuest.Cancel("survival flee owns the wake");
        }
        else if (_questRuntimes.TryGetValue(bot.CharacterId, out var idleQuest))
        {
            // The electing activity moved away from quest.* while a quest tick
            // was pending: mark it superseded (observation only — any live
            // preemption stays with the owning leg via PreemptCurrent).
            idleQuest.Cancel("quest activity not held");
        }

        // 0c. Homestead progression leg: while the arbiter holds a homestead.*
        // activity, run one GOAP plan step per wake against the bot's actor.
        state.HomesteadLegActive = false;
        if (!handledByParty && !pvpEngaged && !state.SurvivalWakeOwned
            && ActiveActivityProvider?.Invoke(bot.CharacterId) is string homeActivity
            && homeActivity.StartsWith("homestead.", StringComparison.Ordinal)
            && actor.ActiveRequest is not { IsTerminal: false })
        {
            state.HomesteadLegActive = StepHomesteadLeg(bot, actor, state);
        }

        // 1. Opportunistic wildlife hunt loop (skipped while fighting players,
        // or while a work leg landed, or while actively farming — needs/quest/homestead preempt hunt acquisition/engagement).
        var isFarmingActive = state.NeedsFarmPhase != NeedsFarmLoopPhase.Idle;
        // P0.4: the hunt/butcher/route sites already stand down on QuestLegActive
        // (landed this wake), but an owned Running job must ALSO hold them on
        // wakes the quest leg defers to the hold — the one exception is the
        // terminal SettleDrop: dead/invalid target teardown may retire the
        // actor's own live leg (preempt + null), never dispatch a new one.
        var questOwnedHold = actor.ActiveRequest is { IsTerminal: false } liveHold
            && _questRuntimes.TryGetValue(bot.CharacterId, out var holdRuntime)
            && _questRuntimeGenerations.TryGetValue(bot.CharacterId, out var holdGen)
            && holdGen == state.Generation
            && holdRuntime.OwnsRunningRequest(liveHold);
        if (!handledByParty && !pvpEngaged && !state.SurvivalWakeOwned
            && !state.NeedsLegActive && !state.QuestLegActive && !state.HomesteadLegActive && !isFarmingActive && EnableWildlifeHunt
            && !questOwnedHold && !state.QuestDiscoveryActive)
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

                    if (bot.Character.IsAutoAttack)
                    {
                        actor.StopAutoAttack();
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
                        // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                        LogQuestTravelHaltCaller(bot, state, "other", "hunt-target-down");
                        actor.PreemptCurrent("hunt target down");
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
                            {
                                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                                LogQuestTravelHaltCaller(bot, state, "other", "hunt-chase-retrack");
                                _ = actor.Stop();
                            }
                            StageMoveOwner(actor, "HUNT");
                            state.PendingLeg = actor.MoveTo(targetPos, HuntChaseSpeed, TimeSpan.FromSeconds(10));
                        }
                    }
                    else
                    {
                        if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                        {
                            // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                            LogQuestTravelHaltCaller(bot, state, "settle", "hunt-engage-range");
                            actor.PreemptCurrent("hunt in engage range");
                            state.PendingLeg = null;
                        }

                        var angle = MathUtil.CalculateAngleFrom(bot.Character.Transform.World.Position, targetUnit.Transform.World.Position);
                        bot.Character.Transform.Local.SetRotationDegree(0f, 0f, (float)angle - 90);
                        bot.Character.Transform.FinalizeTransform();

                        if (!bot.Character.IsAutoAttack)
                        {
                            actor.AutoAttack(targetUnit.ObjId);
                        }

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
                        // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                        LogQuestTravelHaltCaller(bot, state, "other", "hunt-engage-acquire");
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
        // idiom) — zero success-path change. Skipped while a work leg landed
        // or while actively farming (needs/quest/homestead preempt butcher acquisition/engagement).
        if (!handledByParty && !state.SurvivalWakeOwned && !state.NeedsLegActive && !state.QuestLegActive && !state.HomesteadLegActive && !isFarmingActive && EnableWildlifeButcher
            && !questOwnedHold && !state.QuestDiscoveryActive)
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
                        // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                        LogQuestTravelHaltCaller(bot, state, "other", "butcher-target-lost");
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
                            {
                                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                                LogQuestTravelHaltCaller(bot, state, "other", "butcher-approach-retrack");
                                _ = actor.Stop();
                            }
                            StageMoveOwner(actor, "OTHER:butcher-approach");
                            state.PendingLeg = actor.MoveTo(butcherPos, HuntChaseSpeed, TimeSpan.FromSeconds(10));
                        }
                    }
                    else
                    {
                        // Interact needs a free actor (TryBegin busy-rejects) —
                        // park the approach leg first (hunt-engage precedent).
                        if (actor.ActiveRequest is { IsTerminal: false })
                        {
                            // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                            LogQuestTravelHaltCaller(bot, state, "settle", "butcher-in-range");
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
                        // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                        LogQuestTravelHaltCaller(bot, state, "other", "butcher-engage-acquire");
                        _ = actor.Stop();
                        state.PendingLeg = null;
                    }
                    Logger.Debug("Bot {CharacterId} engaged butcherable livestock {TemplateId} ({DoodadId}) at {Dist:F1}m",
                        bot.CharacterId, bestDoodad.TemplateId, bestDoodad.ObjId, bestDoodadDist);
                }
            }
        }
        // 1b. Stale quest-travel guard (G5): a QUEST_TRAVEL route whose
        // pending leg was foreign-interrupted lost its authorization — drop
        // it BEFORE the issue/advance sites below can reissue a leg against
        // the preempting pursuit (the 36 busy-rejects) or resume the stale
        // walk after it completes. Patrol/farm routes and normal travel
        // (Completed arrivals, route-owned arrival halts) never match.
        if (IsForeignRouteInterruption(state))
        {
            // TEMPORARY live-lane diagnostic (read-only witnesses, never branched on): pre-drop cursor snapshot — SupersedeQuestTravelRoute nulls the route; capture before.
            var staleArticle = state.PendingLeg;
            var staleTarget = state.Path is { IsFinished: false } ? state.Path.CurrentTarget : (Vector3?)null;
            var staleCount = state.Path?.Waypoints.Count ?? -1;
            var staleIndex = state.Path is { IsFinished: false } ? state.Path.CurrentIndex : -1;
            LogQuestTravelArticle(state, "stale-guard", staleArticle);
            var detail = state.PendingLeg?.Detail ?? "-";
            SupersedeQuestTravelRoute(state, $"pending leg foreign-interrupted ({detail})");
            LogQuestTravelTurnover(bot, state, "drop", staleTarget, null, staleCount < 0 || staleIndex < 0 ? -1 : staleCount - 1 - staleIndex, staleArticle);
        }
        ValidateQuestTravelAuthorization(bot, state);
        // 2. Issue the next leg when idle, not in party, not hunting, not butchering, not work-legged, not waiting for crop, and a route is active.
        // P0.4: an owned Running job also holds the route sites while the
        // quest leg defers to the hold (QuestLegActive only covers landed).
        if (!handledByParty && !state.SurvivalWakeOwned && !state.NeedsLegActive && !state.QuestLegActive && !state.HomesteadLegActive && !questOwnedHold
            && state.NeedsFarmPhase != NeedsFarmLoopPhase.WaitingMaturity
            && state.TargetNpcObjId == 0 && state.TargetButcherDoodadObjId == 0 && actor.ActiveRequest is not { IsTerminal: false } && state.Path is { IsFinished: false })
        {
            var target = state.Path.CurrentTarget;
            var leg = IssueRouteMove(actor, state, target);
            state.PendingLeg = leg;
            if (leg.IsTerminal && leg.State != ActorLifecycleState.Completed)
            {
                Logger.Warn("Roam leg rejected for bot {CharacterId}: {State} ({Reason}) — advancing route",
                    bot.CharacterId, leg.State, leg.Detail);
                // TEMPORARY live-lane diagnostic (read-only witnesses, never branched on): terminal-article read + Path-finished marker for the unreachable-leg skip.
                LogQuestTravelArticle(state, "reject-advance", state.PendingLeg);
                var rejectFinishedBefore = state.Path.IsFinished;
                var rejectCount = state.Path.Waypoints.Count;
                var rejectIndex = state.Path.CurrentIndex;
                _ = state.Path.Move(bot.Character.Transform.World.Position, flatArrival: true); // advance past the unreachable point
                if (state.Path.IsFinished && !rejectFinishedBefore)
                    LogQuestTravelPathFinished(state, "reject-advance", rejectCount, rejectIndex, leg.Destination ?? state.Path.CurrentTarget);
                if (state.Path.IsFinished)
                {
                    ClearQuestTravelPurpose(state);
                    state.QuestTravelTarget = null;
                }
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

        ValidateQuestTravelAuthorization(bot, state);
        actor.Tick(elapsed);

        // 3a. Flat arrival owns the leg for ground-clamped walkers (only when roaming)
        if (!state.SurvivalWakeOwned
            && state.TargetNpcObjId == 0
            && state.TargetButcherDoodadObjId == 0
            && state.PendingLeg is { IsTerminal: false, Action: ActorActionType.Move }
            && state.Path is { IsFinished: false })
        {
            var flat = MathUtil.CalculateDistance(
                bot.Character.Transform.World.Position, state.Path.CurrentTarget, false);
            if (flat <= state.Path.ArrivalRadius)
            {
                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                LogQuestTravelHaltCaller(bot, state, "flat-arrival", "arrival-radius");
                _ = actor.Stop();
            }
        }
        // 3b. Route advance on arrival: when the pending Move leg reached a terminal state
        // (deferred while a work leg landed — needs/quest preempt route advancement too).
        // TEMPORARY live-lane diagnostic (read-only witness, never branched on): skip side — a terminal Move article the 3b guard below leaves unconsumed (the guard's negation, so it fires exactly when the guard declines).
        if (CharacterQuests.DiagEnabled
            && state.PendingMoveOwner == QuestTravelMoveOwner
            && state.PendingLeg is { IsTerminal: true, Action: ActorActionType.Move } skippedArticle
            && (state.SurvivalWakeOwned
                || state.NeedsLegActive || state.QuestLegActive || state.HomesteadLegActive || questOwnedHold
                || state.NeedsFarmPhase == NeedsFarmLoopPhase.WaitingMaturity
                || state.TargetNpcObjId != 0
                || state.TargetButcherDoodadObjId != 0
                || state.Path is not { IsFinished: false }))
        {
            var skipTarget = state.Path is { IsFinished: false } ? state.Path.CurrentTarget : (Vector3?)null;
            var skipCount = state.Path?.Waypoints.Count ?? -1;
            var skipIndex = state.Path is { IsFinished: false } ? state.Path.CurrentIndex : -1;
            LogQuestTravelTurnover(bot, state, "skip", skipTarget, null, skipCount < 0 || skipIndex < 0 ? -1 : skipCount - 1 - skipIndex, skippedArticle);
        }
        ValidateQuestTravelAuthorization(bot, state);
        if (!state.SurvivalWakeOwned
            && !state.NeedsLegActive && !state.QuestLegActive && !state.HomesteadLegActive && !questOwnedHold
            && state.NeedsFarmPhase != NeedsFarmLoopPhase.WaitingMaturity
            && state.TargetNpcObjId == 0
            && state.TargetButcherDoodadObjId == 0
            && state.PendingLeg is { IsTerminal: true, Action: ActorActionType.Move }
            && state.Path is { IsFinished: false })
        {
            // TEMPORARY live-lane diagnostic (read-only witnesses, never branched on): pre-turnover cursor snapshot — Path.Move steps CurrentIndex and may set IsFinished; capture before.
            var turnoverArticle = state.PendingLeg;
            var turnoverTarget = state.Path.CurrentTarget;
            var turnoverCount = state.Path.Waypoints.Count;
            var turnoverIndex = state.Path.CurrentIndex;
            LogQuestTravelArticle(state, "route-advance", turnoverArticle);
            _ = state.Path.Move(bot.Character.Transform.World.Position, flatArrival: true);
            var turnoverRemaining = turnoverCount - 1 - turnoverIndex;
            if (state.Path.IsFinished)
            {
                LogQuestTravelPathFinished(state, "route-advance", turnoverCount, turnoverIndex, turnoverTarget);
                LogQuestTravelTurnover(bot, state, "finish", turnoverTarget, null, turnoverRemaining, turnoverArticle);
                ClearQuestTravelPurpose(state);
                state.QuestTravelTarget = null;
                state.PendingLeg = null;
            }
            else
            {
                LogQuestTravelTurnover(bot, state, "advance", turnoverTarget, state.Path.CurrentTarget, turnoverRemaining, turnoverArticle);
                state.PendingLeg = IssueRouteMove(actor, state, state.Path.CurrentTarget);
            }
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
                // Telemetry: out-of-request Z write — attribute it so the
                // next attribution read names the clamp, not a stale leg.
                if (actor is GameplayActor clampActor)
                    clampActor.NoteExternalPositionWrite("OTHER:ground-clamp", position);
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
        // Jev shadow: last statement of the wake. A wake that captured brain
        // inputs but reached no emit point (the arbiter elected another leg, or
        // the run produced no decision record) would otherwise pin its slot until
        // its next wake. This releases only a SEALED slot — an emit path already
        // released it, so this is a no-op there — and never touches an actor
        // whose wake is still in progress, because by construction this actor's
        // wake is over.
        if (JevShadow.Enabled)
            JevShadow.ReleaseWakeIfUnemitted(actor.ActorId);

        var effectiveCadence = state.CadenceOverride ?? ActiveCadence;

        // Dormant with the idle-wake gate on: report the bounded idle interval
        // instead of null, so PlayerBotScheduler's step-driven re-arm keeps
        // this bot waking (the arbiter runs every wake, so a newly available
        // module claim is picked up within one idle interval). With the gate OFF
        // this is a static-flag read and a return of the shared DormantTask —
        // byte-identical to today's calm path, allocation included.
        // state.CadenceOverride is deliberately NOT honored here: it is a
        // per-bot present/broadcast-rate seam, and an idle wake must stay low
        // and bounded no matter what rate the bot was set to roam at.
        if (live || routeActive || hunting || butchering)
        {
            return state.CadenceOverride.HasValue
                ? Task.FromResult<TimeSpan?>(effectiveCadence)
                : (_cadenceTask ??= Task.FromResult<TimeSpan?>(ActiveCadence));
        }

        return EnableIdleWake
            ? (_idleWakeTask ??= Task.FromResult<TimeSpan?>(IdleWakeInterval))
            : DormantTask;
    }

    /// <summary>
    /// Default <see cref="IdleWakeInterval"/> — 5 seconds unless
    /// AAEMU_PRESENCE_IDLE_WAKE_SECONDS names a positive integer. Read once at
    /// construction; an unparsable or non-positive value keeps the default
    /// rather than degrading into the scheduler's scan-interval clamp.
    /// </summary>
    private static TimeSpan DefaultIdleWakeInterval()
        => int.TryParse(Environment.GetEnvironmentVariable("AAEMU_PRESENCE_IDLE_WAKE_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(5);

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
    public const float NeedsFarmPerceptionRadius = NeedsSoil.DefaultPerceptionRadiusM;
    /// <summary>
    /// Discovery extension for soil resolution: the perception-radius spiral
    /// runs first; when it finds nothing, a coarser spiral out to this bound
    /// runs before the leg defers (bounded discovery fallback — never a
    /// whole-world scan). The bound itself lives once, in <see cref="NeedsSoil"/>.
    /// </summary>
    public const float NeedsFarmSoilDiscoveryRadius = NeedsSoil.DefaultDiscoveryRadiusM;

    /// <summary>Coarse spiral step for the discovery extension (shared, <see cref="NeedsSoil"/>).</summary>
    public const float NeedsFarmSoilDiscoveryStep = NeedsSoil.DefaultDiscoveryStepM;

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
    /// THE ELECTED-WAKE SURVIVAL GATE.
    ///
    /// The survival veto's only consumers used to live inside the quest leg loop,
    /// but the arm the veto targets (critical hp WITH fight evidence) is exactly the
    /// arm where the arbiter elects recovery.rest (85 &gt; quest 58) or nothing at all
    /// (both the quest module's and the recovery module's in-battle deny) — so the
    /// rule was never evaluated on the wake it was written for. This gate evaluates
    /// it HERE, at the top of the step, before any module election can branch the
    /// wake away, and dispatches the flee leg it asks for.
    ///
    /// The evaluation is FROZEN and allocation-free on the calm path: hp, the
    /// selection and the combat engagement's published facts are plain field/dictionary
    /// reads, so a healthy wake neither scans the world nor pays the formula-backed
    /// <c>Character.MaxHp</c> read. The verdict is published through the single
    /// publisher whenever a veto arm could apply, so every existing consumer reads
    /// this wake's fact rather than a stale one.
    ///
    /// Dispatched legs are held rather than restarted: while our own flee leg is
    /// live and still points at this wake's anchor (within the shared
    /// <see cref="TravelBrain.LegDriftToleranceM"/>), the leg keeps its progress and
    /// the wake is still owned. Only a Flee verdict owns the wake; a Hold (healthy,
    /// or a non-critical fight the combat brain owns) and a Recover (the existing
    /// out-of-combat recovery module's demand) leave the election exactly as it is —
    /// healthy wakes are byte-identical to the pre-gate behavior.
    ///
    /// Returns true when a survival condition owns this wake (a flee leg was
    /// dispatched or held, or the incapacitated hold vetoed every other leg).
    /// </summary>
    private bool StepSurvivalWake(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state)
    {
        var character = bot.Character;

        // CHEAP FACTS FIRST: hp and the selection are plain character fields, and
        // the engagement is one dictionary read — everything the "could this wake
        // veto?" question needs. The hp RATIO needs Character.MaxHp, which is
        // formula-backed and allocates a parameters dictionary on every read, so it
        // is only touched on wakes where a veto arm can actually apply. Calm wakes
        // (hp up, no fight, no retreat) cost nothing and cannot veto.
        var hp = SafeHp(character);
        var selectedTargetObjId = character.CurrentTarget?.ObjId ?? 0u;

        // No veto arm can apply: a readable-hp, non-incapacitated actor with no fight
        // evidence and no published retreat decides Hold(Healthy) or a RECOVER demand
        // — neither vetoes and neither dispatches. Drop any fact still standing from
        // an earlier wake (the cause cleared) and leave every branch as it was.
        if (!SurvivalBrainPlanner.CouldVeto(actor.ActorId, hp, selectedTargetObjId))
        {
            SurvivalVetoState.Clear(actor.ActorId);
            return false;
        }

        // 1. The FROZEN verdict — the one rule, over the facts this wake holds. An
        //    unreadable maximum (an unevaluatable formula) reads as the named hold
        //    that vetoes nobody rather than throwing out of the wake.
        var frozen = SurvivalBrainPlanner.SnapshotInputs(
            actor.ActorId, hp, SafeMaxHp(character), selectedTargetObjId);
        SurvivalVetoState.Publish(actor.ActorId, frozen, out var decision);
        // Jev shadow: freeze THIS wake's already-computed survival inputs + the
        // verdict the gate just reached. No re-plan, no extra vitals read — the
        // MaxHp read above is the only formula read and it already happened.
        if (JevShadow.Enabled)
            JevShadow.CaptureSurvival(actor.ActorId, frozen, decision);

        // A non-vetoing verdict (a non-critical fight the combat brain owns, the
        // recovery demand) leaves every downstream branch exactly as it was. The
        // incapacitated hold vetoes but dispatches nothing: it still owns the wake,
        // so no leg takes it from a down actor.
        if (!decision.IsFlee)
            return decision.Veto;

        // 2. The flee destination needs the threat's live position — the one world
        //    resolve the planner performs, and only on the arm that asks for a leg.
        //    The verdict is a pure function of the same frozen facts, so this
        //    resolves the leg's destination rather than re-deciding the wake.
        var prepared = SurvivalBrainPlanner.Prepare(actor, frozen);
        if (prepared.Decision.Destination is not { } destination)
            return decision.Veto;

        // 3. Hold a live flee leg that still serves this anchor: the anchor is
        //    recomputed from the actor's own position every wake, so re-issuing
        //    unconditionally would restart the escape on every wake and the bot
        //    would never advance. A leg drifted past the shared tolerance is
        //    re-issued (the anchor receded as the bot walked).
        if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move } live
            && live.MoveOwner == SurvivalFleeMoveOwner
            && live.Destination is { } liveDestination
            && Vector3.Distance(liveDestination, destination) <= TravelBrain.LegDriftToleranceM)
        {
            state.PendingLeg = live;
            return true;
        }

        // A live FOREIGN non-move leg (a cast, a loot) keeps the actor this
        // wake: the flee still OWNS the wake (every other leg stands down),
        // but no leg is dispatched against a busy actor — the next wake
        // dispatches once the leg finishes.
        if (actor.ActiveRequest is { IsTerminal: false } busy && busy.Action != ActorActionType.Move)
        {
            state.PendingLeg = busy;
            return true;
        }

        // Dispatch the leg. A live auto-attack loop would keep firing on the mob
        // the bot is walking away from, and a live Move leg would busy-reject
        // this one — the same ordered teardown the combat spacing dispatch uses
        // (stop the loop first, then retrack the leg).
        if (bot.Character.IsAutoAttack)
            actor.StopAutoAttack();
        if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
        {
            // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
            LogQuestTravelHaltCaller(bot, state, "other", "survival-flee-retrack");
            actor.PreemptCurrent(TravelLegDispatch.SurvivalFleeRetrackDetail);
        }

        StageMoveOwner(actor, SurvivalFleeMoveOwner);
        state.PendingLeg = actor.MoveTo(destination, SurvivalFleeSpeed, SurvivalFleeLegTimeout);
        return true;
    }

    /// <summary>
    /// The actor's current hp, or 0 when the property cannot be read (an unreadable
    /// frame — the same named hold the decision chain assigns it, never a throw out
    /// of the wake).
    /// </summary>
    private static int SafeHp(Character character)
    {
        try
        {
            return character.Hp;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The actor's maximum hp, or 0 when the formula-backed property cannot be
    /// evaluated. 0 is the honest "no readable maximum" the survival chain handles by
    /// <see cref="SurvivalReason.VitalsUnreadable"/>; a throw here would otherwise
    /// take the whole wake down over a measurement the gate could not make.
    /// </summary>
    private static int SafeMaxHp(Character character)
    {
        try
        {
            return character.MaxHp;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Copper-bootstrap quest leg (branch 0a): one runtime-hosted
    /// QuestBehavior tick per wake — advance each active quest once, otherwise
    /// discover from the nearest in-range NPCs and accept the lowest-level
    /// in-band offer. Objective pursuit itself (kill, gather, talk) rides the
    /// existing hunt/interact branches — this leg only advances the step
    /// machine and acquires new quests through the existing actor quest
    /// actions. Travel fallback (ArmQuestTravel) stays navigation-side.
    ///
    /// TRAVEL-TO-TARGET: the quest leg outranks PresenceRoam (58 &gt; 50), so the
    /// arbiter never hands this bot to the roam module — without a route armed
    /// here the bot would stand still forever and a reporter outside spawn
    /// radius would never materialize. When the decision scenario has no legal
    /// work (typically: the only active quest is Ready but its reporter is not
    /// spawned yet), the leg arms an ordinary single-leg
    /// <see cref="BotPath.PathTo"/> toward the pending target's spawner and
    /// returns false, so the route layer's own MoveTo legs carry the bot (no
    /// teleport, no Transform writes — the same discipline as the needs-farm
    /// travel branch).
    ///
    /// Returns true only when decision work actually landed (Completed) —
    /// the 0b contract: reject/decide-fail/travel wakes return false so the
    /// route still walks and the scheduler keeps its cadence instead of
    /// spinning.
    /// </summary>
    // TEMPORARY live-lane diagnostic (read-only log, never branched on): scheduler-wake instance witness.
    // Join key with REMOVER=instance-observe rows: bot/char name + charHash/questsHash. Same hashes = same instance (aliasing eliminated); different = aliasing CONFIRMED.
    private static void LogInstanceWake(string site, PlayerBotRuntime bot)
    {
        if (!CharacterQuests.DiagEnabled)
            return;
        var wakeChar = bot.Character;
        if (wakeChar == null)
            return;
        var wakeQuests = wakeChar.Quests;
        if (wakeQuests == null)
            return;
        Logger.Warn($"[QuestRemove] REMOVER=instance-wake site={site} char={wakeChar.Name}({wakeChar.Id}) objId={wakeChar.ObjId} charHash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(wakeChar)} questsHash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(wakeQuests)} activeCount={wakeQuests.ActiveQuests.Count} activeIds=[{string.Join(",", wakeQuests.ActiveQuests.Keys.OrderBy(k => k))}] present254={wakeQuests.ActiveQuests.ContainsKey(254)}");
    }

    // TEMPORARY live-lane diagnostic (read-only witness, never branched on): QUEST_TRAVEL halt-caller — names which route-layer/executor Stop (or preempt) fired on a QUEST_TRAVEL-owned route, with the remaining distance that tells arrival-misfire (dist small) from settle/foreign stops (dist large). Self-gates on QUEST_TRAVEL ownership so every call site stays one line and flag-off allocates nothing.
    private static void LogQuestTravelHaltCaller(PlayerBotRuntime bot, BotRoamState state, string site, string reason)
    {
        if (!CharacterQuests.DiagEnabled)
            return;
        if (state.PendingMoveOwner != QuestTravelMoveOwner)
            return;
        var character = bot.Character;
        if (character == null)
            return;
        var dest = state.Path is { IsFinished: false } ? state.Path.CurrentTarget : state.QuestTravelTarget;
        var destText = dest.HasValue ? "(" + dest.Value.X.ToString("F1") + "," + dest.Value.Y.ToString("F1") + "," + dest.Value.Z.ToString("F1") + ")" : "-";
        var dist = dest.HasValue ? MathUtil.CalculateDistance(character.Transform.World.Position, dest.Value, false) : -1f;
        var elapsedS = state.PendingLeg != null ? state.PendingLeg.Elapsed.TotalSeconds : -1.0;
        Logger.Warn($"[QuestRemove] REMOVER=report-walk-halt-caller site={site} reason={reason} dest={destText} elapsed={elapsedS:F1}s dist={dist:F1}m");
    }

    // TEMPORARY live-lane diagnostic (read-only witness, never branched on): QUEST_TRAVEL chunk-turnover decision — whether the route-advance article (a terminal Move pending leg) advanced the Once cursor (advance), marked it finished (finish), was dropped by the stale-route guard (drop), or was left unconsumed by the 3b guard (skip, with hold= naming the holding guard). current is the arrived chunk target (pre-decision cursor), next is the post-decision target (- on finish/drop), remaining counts waypoints after the arrived chunk (Count-1-Index; -1 when no live route was snapshotted), leg is the terminal article. Only BotPath.Arrive steps the cursor; SupersedeQuestTravelRoute nulls the route. Self-gates on QUEST_TRAVEL ownership like LogQuestTravelHaltCaller so call sites stay one line and flag-off allocates nothing.
    private static void LogQuestTravelTurnover(PlayerBotRuntime bot, BotRoamState state, string decision, Vector3? current, Vector3? next, int remaining, ActorRequest? leg)
    {
        if (!CharacterQuests.DiagEnabled)
            return;
        if (state.PendingMoveOwner != QuestTravelMoveOwner)
            return;
        if (bot.Character == null)
            return;
        var currentText = current.HasValue ? "(" + current.Value.X.ToString("F1") + "," + current.Value.Y.ToString("F1") + "," + current.Value.Z.ToString("F1") + ")" : "-";
        var nextText = next.HasValue ? "(" + next.Value.X.ToString("F1") + "," + next.Value.Y.ToString("F1") + "," + next.Value.Z.ToString("F1") + ")" : "-";
        var legState = leg?.State.ToString() ?? "-";
        if (decision == "skip")
        {
            string hold;
            if (state.SurvivalWakeOwned)
                hold = "survival-owned";
            else if (state.NeedsLegActive)
                hold = "needs-leg";
            else if (state.QuestLegActive)
                hold = "quest-leg";
            else if (state.HomesteadLegActive)
                hold = "homestead-leg";
            else if (state.NeedsFarmPhase == NeedsFarmLoopPhase.WaitingMaturity)
                hold = "farm-wait";
            else if (state.TargetNpcObjId != 0)
                hold = "hunt-target";
            else if (state.TargetButcherDoodadObjId != 0)
                hold = "butcher-target";
            else if (state.Path is not { IsFinished: false })
                hold = "path-finished-or-null";
            else if (state.PendingLeg is not { IsTerminal: true, Action: ActorActionType.Move })
                hold = "article-not-terminal-move";
            else
                hold = "unknown";
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-turnover decision={decision} remainingChunks={remaining} currentTarget={currentText} nextTarget={nextText} legState={legState} hold={hold}");
            return;
        }
        Logger.Warn($"[QuestRemove] REMOVER=report-walk-turnover decision={decision} remainingChunks={remaining} currentTarget={currentText} nextTarget={nextText} legState={legState}");
    }

    // TEMPORARY live-lane diagnostic (read-only witness, never branched on): PendingLeg terminal-article consumption — which site read the terminal Move article first on this wake (stale-guard = the 1b foreign-interruption drop, route-advance = the 3b turnover, reject-advance = the site-2 unreachable-leg skip). Pairs with report-walk-terminal (GameplayActor, every QUEST_TRAVEL terminal) to order article-death vs article-read. Self-gates on QUEST_TRAVEL ownership; flag-off allocates nothing.
    private static void LogQuestTravelArticle(BotRoamState state, string site, ActorRequest? leg)
    {
        if (!CharacterQuests.DiagEnabled)
            return;
        if (state.PendingMoveOwner != QuestTravelMoveOwner)
            return;
        var trace = leg?.TraceId.ToString() ?? "-";
        var articleState = leg?.State.ToString() ?? "-";
        var detail = leg?.Detail ?? "-";
        Logger.Warn($"[QuestRemove] REMOVER=report-walk-article site={site} trace={trace} state={articleState} detail={detail}");
    }

    // TEMPORARY live-lane diagnostic (read-only witness, never branched on): Path-finished marker — which Path.Move call site flipped a Once route to IsFinished (route-advance = the 3b turnover; reject-advance = the site-2 unreachable-leg skip), with the waypoint census that tells premature-finish (index 0 of N>1) from final-leg finish (index N-1). Only BotPath.Arrive sets IsFinished; the route layer only reads it. Self-gates on QUEST_TRAVEL ownership; flag-off allocates nothing.
    private static void LogQuestTravelPathFinished(BotRoamState state, string site, int waypoints, int index, Vector3? current)
    {
        if (!CharacterQuests.DiagEnabled)
            return;
        if (state.PendingMoveOwner != QuestTravelMoveOwner)
            return;
        var currentText = current.HasValue ? "(" + current.Value.X.ToString("F1") + "," + current.Value.Y.ToString("F1") + "," + current.Value.Z.ToString("F1") + ")" : "-";
        Logger.Warn($"[QuestRemove] REMOVER=report-walk-path-finished site={site} waypoints={waypoints} index={index} currentTarget={currentText}");
    }

    private bool StepQuestLeg(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state, string questActivity)
    {
        // TEMPORARY live-lane diagnostic (read-only log, never branched on): wake-path instance witness for this bot.
        LogInstanceWake("quest-leg", bot);
        // Dead bots (scheduler death-watch owns them until resurrection) run
        // no quest work: a stale pre-death hold must never be re-entered from
        // a direct executor call, and no fresh decision may dispatch against
        // a corpse. The death entry already released the hold; this guard is
        // defense-in-depth so direct-step callers get the same discipline.
        if (bot.Character.IsDead)
            return false;
        if (actor is not GameplayActor concreteActor)
            return false;
        Func<Character, float, IEnumerable<Npc>> nearbyNpcs =
            NearbyNpcProvider ?? DefaultNearbyNpcs;
        if (!_questRuntimes.TryGetValue(bot.CharacterId, out var runtime) || !_questRuntimeGenerations.TryGetValue(bot.CharacterId, out var runtimeGen) || runtimeGen != state.Generation)
        {
            runtime = new BotBehaviorRuntime(() => TimeProvider.GetUtcNow()) { QuestCompletedSink = QuestTerminalSink, QuestGiveUpSink = QuestGiveUpSink };
            _questRuntimes[bot.CharacterId] = runtime;
            _questRuntimeGenerations[bot.CharacterId] = state.Generation;
        }
        // ONE band value for the whole wake: the accept sweep (QuestBehavior's
        // in-band filter) and the travel pre-filter (OffersWalkableQuest) must
        // never disagree — a bot that walks to an offerer whose only offers
        // its own accept path discards just paces.
        var band = OfferBandFor(concreteActor);
        // Runtime brain-tuning seam: live overrides layered over the canonical
        // defaults — next-wake visible, byte-identical when no override is set.
        var wakeOptions = BotBrainTuningStore.ApplyTo(band) with
        {
            CycleId = $"quest-{bot.CharacterId}-{TimeProvider.GetUtcNow().UtcTicks}",
            WithholdTurnIn = IsQuestTurnInWithheld(bot.CharacterId),
            ChosenQuestId = QuestChosenProvider?.Invoke(bot.CharacterId) ?? 0,
        };
        var landed = runtime.Tick(concreteActor, nearbyNpcs, wakeOptions, questActivity);
        var result = runtime.LastResult;
        // Diagnostic only: mirror the quest-leg outcome (landed action or
        // fail stage + reason with sweep tallies) into observe state, plus a
        // bot-vs-actor identity probe (same Character reference? same pose?)
        var botPos = bot.Character.Transform.World.Position;
        var actorPos = concreteActor.Character.Transform.World.Position;
        state.QuestDecideDetail = result == null
            ? $"holding {actor.ActiveRequest?.Action} ({TruncateDecide(actor.ActiveRequest?.Detail)})"
            : result.WorkSelected
                ? $"landed {result.SelectedAction} ({TruncateDecide(result.Request?.Detail)})"
                : $"{result.FailStage}: {TruncateDecide(result.FailReason)}";
        // Jev shadow (Stage 1, default OFF): frozen shadow-log row for the
        // offline critic, emitted at this ALREADY-LOGGED point from the
        // frozen CycleId + QuestDecideDetail + QuestRunResult and this wake's
        // already-captured brain inputs — no live reads, no re-plan.
        // Flag-off = one static flag read, zero allocation, zero
        // behavior change (args are never evaluated).
        // The brains carrier is what carries this wake's FROZEN per-brain inputs
        // (Travel / Survival / Needs / Combat) into the row. Before this passed
        // null, so all four blocks serialized null on every row and no brain
        // outside the quest arbitration was harvestable at all.
        if (JevShadow.Enabled)
        {
            JevShadow.MaybeEmitQuest(bot.CharacterId, runtime.BehaviorInstanceId,
                state.QuestDecideDetail, result, JevShadow.TakeBrains(concreteActor.ActorId));
            // This wake is over: the carrier was consumed above, so release the
            // actor's slot. Done in the CALLER because that is where the actor
            // objId (the slot key) is known.
            JevShadow.ReleaseWake(concreteActor.ActorId);
        }
        Logger.Info(
            "QuestSweepDiag cycle={Cycle} char={CharId} botSameChar={Same} botCharId={BotChar} actorCharId={ActorChar} " +
            "botPos=({BX:F1},{BY:F1},{BZ:F1}) actorPos=({AX:F1},{AY:F1},{AZ:F1}) decide=[{Decide}]",
            runtime.BehaviorInstanceId ?? "",
            bot.CharacterId, ReferenceEquals(bot.Character, concreteActor.Character),
            bot.Character.Id, concreteActor.Character.Id,
            botPos.X, botPos.Y, botPos.Z, actorPos.X, actorPos.Y, actorPos.Z,
            TruncateDecide(state.QuestDecideDetail, 400));
        // P0.4: the running-hold wake reports landed with no fresh decision
        // (runtime LastResult is null there) — a hold must never arm fallback
        // against our own leg, and only a Blocked (no-legal-work) wake arms it.
        var holdingRunning = result == null;
        if (landed)
        {
            // G5: a dispatched pursuit leg preempts (and thereby orphans) any
            // armed quest-travel route — drop it synchronously so the route
            // layer can never resume the stale destination after the pursuit
            // completes (the next quest wake re-arms when travel is needed).
            // G8b: the quest-owned return leg preempts the same way, and G7c's
            // corpse approach is the third leg that walks the actor itself.
            var moveOwner = result?.Request?.MoveOwner ?? actor.ActiveRequest?.MoveOwner;
            var returnDispatched = string.Equals(
                moveOwner, TravelLegDispatch.ReturnMoveOwner, StringComparison.Ordinal);
            var approachDispatched = string.Equals(
                moveOwner, LootTravelDispatch.ApproachMoveOwner, StringComparison.Ordinal);
            if ((result?.SelectedAction == ActorActionType.Move || (holdingRunning && actor.ActiveRequest?.Action == ActorActionType.Move))
                && (returnDispatched || approachDispatched
                    || string.Equals(moveOwner, TravelLegDispatch.PursuitMoveOwner, StringComparison.Ordinal)))
            {
                SupersedeQuestTravelRoute(state,
                    returnDispatched ? "quest return dispatched"
                    : approachDispatched ? "loot approach dispatched"
                    : "quest pursuit dispatched");
            }
            // TEMPORARY live-lane diagnostic (read-only log, never branched on): landed short-circuit — ArmQuestTravel not reached this wake.
            if (CharacterQuests.DiagEnabled)
                Logger.Warn($"[QuestRemove] REMOVER=report-walk-entry liveRoute={state.Path is { IsFinished: false }} owner={state.PendingMoveOwner ?? "-"} reached=landed-short-circuit action={result?.SelectedAction} moveOwner={moveOwner ?? "-"}");
            Logger.Debug("Roam quest leg completed for bot {CharacterId}: {Action} ({Detail})",
                bot.CharacterId, result?.SelectedAction, result?.Request?.Detail ?? actor.ActiveRequest?.Detail);
        }
        else if (!holdingRunning)
        {
            // No legal work: if a quest is waiting on a target that is not
            // spawned yet, walk toward its spawner so it CAN spawn.
            ArmQuestTravel(bot, concreteActor, state, wakeOptions, questActivity);
        }
        return landed;
    }

    /// <summary>Diagnostic-only bound for decide-detail strings (observe + log).</summary>
    private static string TruncateDecide(string? value, int max = 600)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value.Substring(0, max) + "…";

    /// <summary>
    /// Arms a bounded Move leg toward the spawner of the target the bot's
    /// active quests are currently waiting on (an unspawned report NPC,
    /// otherwise an uncredited gather/interact objective's doodad source),
    /// or — while no quest is active — toward the nearest spawner offering
    /// a quest in band. Returns true when a fresh route was armed. Purely a
    /// movement decision: the engine still owns discovery/accept/turn-in at
    /// dispatch.
    /// </summary>
    private bool ArmQuestTravel(PlayerBotRuntime bot, GameplayActor actor, BotRoamState state,
        QuestDecisionScenario.QuestOptions band, string? questActivity)
    {
        ValidateQuestTravelAuthorization(bot, state);
        // A live route is already walking — never re-arm (keeps progress) —
        // but only for a route that carries QUEST progress. The owner-blind
        // guard starved the quest leg forever against a patrol ring: a
        // Loop-mode route never sets IsFinished (BotPath.Arrive wraps
        // CurrentIndex modulo the waypoint count), so `state.Path is
        // { IsFinished: false }` is permanently true for a "look alive" ring
        // and every quest wake returned here before the target resolver ran.
        // ROAM is the presence/patrol owner SetRoamRoute stamps (and the only
        // owner that can be Loop-mode forever), so exempting it exactly
        // re-admits the quest leg: quest travel outranks patrol by arbitration
        // (quest.progress 58 > presence.roam 50) and the patrol is re-armed by
        // PresenceRoam's own route-less ResumeRoam once quest work is done.
        // Every other owner (QUEST_TRAVEL progress, OTHER:needs-farm-travel)
        // keeps the original protect-the-walk behaviour, byte-identical.
        // TEMPORARY live-lane diagnostic (read-only log, never branched on): ArmQuestTravel entry — distinguish live-route short-circuit from resolver reach.
        if (CharacterQuests.DiagEnabled)
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-entry liveRoute={state.Path is { IsFinished: false }} owner={state.PendingMoveOwner ?? "-"} reached={(state.Path is { IsFinished: false } && state.PendingMoveOwner != "ROAM" ? "short-circuit" : "resolver")}");
        if (state.Path is { IsFinished: false } && state.PendingMoveOwner != "ROAM")
            return false;

        var character = bot.Character;
        var target = ResolveQuestTravelTarget(actor, band);
        if (target == null)
        {
            state.QuestTravelReason = "no walkable quest target (nothing ready-unspawned, nothing in-band to discover)";
            return false;
        }

        // Branch-1b bound: the resolver prefers a Ready reporter first, then an
        // objective source, then an offerer — when the armed target IS the
        // objective source, count consecutive arms to the same spawner (1.0 m
        // tolerance) and withhold past 3 until the active-quest set changes.
        //
        // PREY EXEMPTION — the bound does NOT apply to a PREY source. Measured
        // on a 30-minute 4-bot run: 7 quests active, quest 251's objective
        // (item 4058 x3) blocked on prey 3475 sitting ~330 m out, so
        // ResolveQuestObjectiveSource resolved it EVERY wake — and the cap then
        // withheld travel from the 4th arm on for the rest of the run: 0
        // BotMoveStart for the final 8 minutes while the quest funnel logged
        // 700+ lines, the bots parked at (15566.9, 14790.8) ~330 m short, and
        // have= stayed at 1. That is the cap double-penalising the ONE
        // objective travel exists to unblock: a prey outside the funnel's 25 m
        // perception census is by definition not yet spawnable-by-sighting, and
        // walking to its spawner is the only thing that can bring it in.
        //
        // Why the prey's OWN guards already bound this, so an exemption cannot
        // run away. The prey arm answers only when the objective is live and the
        // prey is absent (ResolveQuestObjectiveSource :2386-2394): a credited
        // objective, a Deliver shape, or a PERCEIVED prey all make it return
        // false, so arm 1b has no prey source to arm and the walk simply ends.
        // PerceivedPrey re-reads the census EVERY call, so a prey that spawned
        // mid-trek withdraws travel on the very next wake — the exemption is
        // self-cancelling exactly when the walk has done its job. What remains
        // bounded is the same as what the cap bounded: a bot that keeps
        // re-resolving the same prey spawner. It still walks (bounded by the
        // navigation budget per leg), and a genuine obstacle or a route the
        // legs cannot complete still costs exactly what it cost before.
        //
        // A DOODAD source keeps the cap byte for byte: its source is a world
        // object the spawn path materialises in place, so "3 arms and no kill"
        // genuinely does mean unreachable, and that is the behaviour this bound
        // was written for.
        if (character.Quests != null
            && ResolveQuestObjectiveSource(actor, band.ChosenQuestId, out var source, out var sourceTemplate, out var sourceIsPrey)
            && target.Value.Equals(source))
        {
            var sourceDist = Vector3.Distance(character.Transform.World.Position, source);
            var sameSpawner = state.QuestObjectiveSourcePos.HasValue
                && Vector3.Distance(state.QuestObjectiveSourcePos.Value, source) <= 1.0f
                && state.QuestObjectiveSourceTemplate == sourceTemplate;

            // Reset-on-quest-set-change, byte for byte as before.
            var questKey = string.Join(",", character.Quests.ActiveQuests.Keys.OrderBy(k => k));
            if (!string.Equals(questKey, state.QuestObjectiveSourceQuestKey, StringComparison.Ordinal))
            {
                ResetObjectiveSourceArms(state, questKey);
            }
            var kind = sourceIsPrey ? "prey" : "doodad";
            // `!sourceIsPrey` is the whole change: a prey source is the one
            // case where the same spawner can be unreachable for three arms and
            // still be worth walking — the prey is outside the census by
            // definition, so nothing else can materialize it.
            if (sameSpawner && !sourceIsPrey && state.QuestObjectiveSourceArms >= 3)
            {
                state.QuestTravelTarget = null;
                state.QuestTravelReason = $"objective-source-unreachable ({kind} {sourceTemplate})";
                return false;
            }
            state.QuestTravelTarget = source;
            state.QuestTravelReason =
                $"walking to quest objective source ({kind} {sourceTemplate} {sourceDist:F1}m) at ({source.X:F0},{source.Y:F0}) " +
                $"from ({character.Transform.World.Position.X:F0},{character.Transform.World.Position.Y:F0})";

            state.Path = QuestTravelRouteTo(character.Transform.World.Position, source);
            state.PendingLeg = null;
            // Telemetry: the route layer's next legs own as QUEST_TRAVEL.
            state.PendingMoveOwner = "QUEST_TRAVEL";
            CaptureQuestTravelPurpose(state, actor, band, questActivity);
            if (sameSpawner)
            {
                state.QuestObjectiveSourceArms++;
            }
            else
            {
                state.QuestObjectiveSourcePos = source;
                state.QuestObjectiveSourceTemplate = sourceTemplate;
                state.QuestObjectiveSourceArms = 1;
            }
            Logger.Debug("Roam quest travel armed for bot {CharacterId}: walking to quest objective source ({Kind} {Template}) at ({X:F0},{Y:F0})",
                bot.CharacterId, kind, sourceTemplate, source.X, source.Y);
            return true;
        }

        state.QuestTravelTarget = target;
        state.QuestTravelReason =
            $"walking to quest target at ({target.Value.X:F0},{target.Value.Y:F0}) " +
            $"from ({character.Transform.World.Position.X:F0},{character.Transform.World.Position.Y:F0})";

        state.Path = QuestTravelRouteTo(character.Transform.World.Position, target.Value);
        state.PendingLeg = null;
        // Telemetry: the route layer's next legs own as QUEST_TRAVEL.
        state.PendingMoveOwner = "QUEST_TRAVEL";
        CaptureQuestTravelPurpose(state, actor, band, questActivity);
        Logger.Debug("Roam quest travel armed for bot {CharacterId}: walking toward pending quest target at ({X:F0},{Y:F0})",
            bot.CharacterId, target.Value.X, target.Value.Y);
        return true;
    }

    /// <summary>
    /// Clears every objective-source arm counter and re-seeds the quest key.
    /// One place for both reset sites (the quest-set-change clause and the
    /// teleport-jump sweep), so neither can leave a counter behind that the
    /// other would have cleared.
    /// </summary>
    private static void ResetObjectiveSourceArms(BotRoamState state, string questKey)
    {
        state.QuestObjectiveSourceQuestKey = questKey;
        state.QuestObjectiveSourcePos = null;
        state.QuestObjectiveSourceTemplate = 0;
        state.QuestObjectiveSourceArms = 0;
    }

    /// <summary>
    /// Drops a quest-travel route whose authorization is lost (G5 stale-route
    /// resume): a QUEST_TRAVEL route interrupted by a foreign preemption
    /// (pursuit retrack, controller/API interrupt) must never reissue or
    /// resume — the next quest wake re-decides and re-arms when travel is
    /// still needed. Scoped to the QUEST_TRAVEL owner exactly: patrol (ROAM)
    /// and farm-travel routes keep their legitimate pause/resume
    /// (route-owned arrival halts, hunt/party pauses, farm stash/restore).
    /// Normal unsuperseded travel is untouched (still armed, still advancing).
    /// Idempotent: re-entry with no live quest-travel route is a no-op.
    /// </summary>
    private static void SupersedeQuestTravelRoute(BotRoamState state, string reason)
    {
        if (state.PendingMoveOwner != "QUEST_TRAVEL")
            return;
        PreemptQuestTravelLeg(state, reason);
        ClearQuestTravelPurpose(state);
        if (state.Path is not { IsFinished: false } && state.PendingLeg == null && state.QuestTravelTarget == null)
            return;
        state.Path = null;
        state.PendingLeg = null;
        state.QuestTravelTarget = null;
        // TEMPORARY live-lane diagnostic (read-only log, never branched on): QUEST_TRAVEL drop witness — which guard fired. No-op re-entries stay silent.
        if (CharacterQuests.DiagEnabled)
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-superseded reason={reason}");
        state.QuestTravelReason = $"superseded: {reason}";
        Logger.Info("Roam quest travel superseded ({Reason}) — route dropped, next quest wake re-decides", reason);
    }

    /// <summary>
    /// True when a live QUEST_TRAVEL route's pending leg was foreign-
    /// interrupted: terminal Interrupted with any detail OTHER than the route
    /// layer's own arrival halt ("stop requested" — the 3a flat-arrival Stop
    /// and the executor's own settle Stops, all of which keep route
    /// authorization). Foreign preemptions carry distinct reasons (pursuit
    /// "quest pursuit retrack", queue "interrupted by controller"). Completed
    /// arrivals, timeouts, running legs, and non-quest-travel owners never
    /// match — normal travel and patrol pause/resume are unaffected.
    /// </summary>
    internal static bool IsForeignRouteInterruption(BotRoamState state)
        => state.PendingMoveOwner == "QUEST_TRAVEL"
            && state.Path is { IsFinished: false }
            && state.PendingLeg is { IsTerminal: true, Action: ActorActionType.Move, State: ActorLifecycleState.Interrupted }
            && state.PendingLeg.Detail != "stop requested";

    /// <summary>
    /// The world position the quest leg should walk toward: the spawner of a
    /// Ready quest's report NPC when that NPC is not spawned, otherwise the
    /// NEAREST of the active quest's uncredited gather/interact objective's
    /// doodad source and the nearest in-band quest offerer spawner — EXCEPT an
    /// objective whose source is a PREY NPC, which wins outright (see the
    /// tail). Null when there is nothing to walk to (the bot then idles rather
    /// than pacing).
    /// </summary>
    private static Vector3? ResolveQuestTravelTarget(GameplayActor actor, QuestDecisionScenario.QuestOptions band)
    {
        var character = actor.Character;
        var world = character.ParentWorld;
        var quests = character.Quests;
        // TEMPORARY live-lane diagnostic (read-only log, never branched on): resolver entered with no world/quest state — arm-1 never evaluated.
        if ((world == null || quests == null) && CharacterQuests.DiagEnabled)
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-skip quest=0 reporter=0 reason=no-world-or-quests");
        if (world == null || quests == null)
            return null;
        quests.ActiveQuests.TryGetValue(band.ChosenQuestId, out var selectedQuest);
        var chosenActive = band.ChosenQuestId != 0 && selectedQuest != null;
        if (chosenActive && selectedQuest is { Status: QuestStatus.Completed })
            return null;

        // 1. A Ready quest whose reporter is missing — walk to the reporter's
        //    spawner so the world's normal spawn path materializes it.
        // TEMPORARY live-lane diagnostic (read-only log, never branched on): arm-1 reach + Ready-candidate census, once per pass.
        if (CharacterQuests.DiagEnabled)
        {
            var readyCount = 0;
            foreach (var censusQuest in quests.ActiveQuests.Values)
                if (censusQuest is { Status: QuestStatus.Ready })
                    readyCount++;
            Logger.Warn($"[QuestRemove] REMOVER=report-walk-scan readyCount={readyCount}");
        }
        foreach (var (questId, quest) in quests.ActiveQuests.OrderBy(kv => kv.Key))
        {
            if (band.ChosenQuestId != 0 && questId != band.ChosenQuestId)
                continue;
            if (quest is not { Status: QuestStatus.Ready })
            {
                if (CharacterQuests.DiagEnabled)
                    Logger.Warn($"[QuestRemove] REMOVER=report-walk-skip quest={questId} reporter=0 reason=not-ready");
                continue;
            }
            var template = QuestManager.Instance.GetTemplate(questId);
            var reportNpc = template?.GetComponents(QuestComponentKind.Ready)
                .SelectMany(c => c.ActTemplates)
                .OfType<QuestActConReportNpc>()
                .FirstOrDefault();
            if (reportNpc == null)
            {
                if (CharacterQuests.DiagEnabled)
                    Logger.Warn($"[QuestRemove] REMOVER=report-walk-skip quest={questId} reporter=0 reason=no-report-act");
                continue; // doodad/auto turn-ins need no travel
            }
            if (world.GetNpcByTemplateId(reportNpc.NpcId) != null)
            {
                if (CharacterQuests.DiagEnabled)
                    Logger.Warn($"[QuestRemove] REMOVER=report-walk-skip quest={questId} reporter={reportNpc.NpcId} reason=spawned");
                continue; // reporter already spawned — turn-in will land next wake
            }
            if (TrySpawnerPosition(world, reportNpc.NpcId) is { } reportPos)
            {
                if (CharacterQuests.DiagEnabled)
                    Logger.Warn($"[QuestRemove] REMOVER=report-walk-armed quest={questId} reporter={reportNpc.NpcId} spawnerPos=({reportPos.X:F1},{reportPos.Y:F1},{reportPos.Z:F1})");
                return reportPos;
            }
            if (CharacterQuests.DiagEnabled)
                Logger.Warn($"[QuestRemove] REMOVER=report-walk-skip quest={questId} reporter={reportNpc.NpcId} reason=no-spawner-row");
        }
        if (chosenActive && selectedQuest is { Status: QuestStatus.Ready })
            return null;

        // 1b. Active non-Ready quest waiting on an uncredited gather/interact
        //     objective's doodad source — walk to the nearest spawner for that
        //     source so the world's normal spawn path materializes it. Quest-id
        //     order; gather before interact within a quest.
        //
        //     NOT AN ABSOLUTE PRIORITY. This arm used to `return` the instant
        //     it resolved, which made arm 2 unreachable on every wake carrying
        //     an unsatisfied objective. Measured on two real 30-minute 4-bot
        //     unattended runs: the bots spent their runs walking ~572-592 m
        //     toward DOODAD/objective-source spawners, exhausting the 60 s
        //     navigation budget (~150 m of travel per leg) over ~4 legs per
        //     destination, while real quest givers sat 32 m (npc 3597, quest
        //     330), 117 m (npc 3511, quest 6198) and 224 m (npc 3512, quest
        //     251) from the bot's position — 456-490 m from where the bots
        //     actually walked, and never visited at all.
        //
        //     SO: the arm DEFERRS. Its source is kept as the default answer and
        //     compared against arm 2's nearest in-band offerer by DISTANCE FROM
        //     THE BOT — nearer offerer wins, nearer source keeps today's
        //     behaviour byte-for-byte. The comparison reads the two positions
        //     this method ALREADY resolves (arm 2's scan simply runs first when
        //     it is needed, so nothing is resolved twice): no new scan, no new
        //     resolver, no counter, no cooldown, no blacklist. A reachability
        //     filter was tried here once and REGRESSED progression by rejecting
        //     every candidate; this admits both candidates by construction, so
        //     it cannot reject anything.
        //
        //     ONE EXCEPTION, the PREY source (an objective whose source is a
        //     PREY NPC, not a doodad). That is the one source travel can act on
        //     END TO END — walking to it is what brings the prey into the
        //     funnel's census — so it does not compete on distance at all. See
        //     the tail below.
        //
        //     The flag below is set ONLY by the prey arm: both doodad arms
        //     return before it, so it reads true for a prey-NPC source and for
        //     nothing else.
        var objectiveSource = (Vector3?)null;
        var objectiveSourceIsPrey = false;
        if (ResolveQuestObjectiveSource(actor, band.ChosenQuestId, out var resolvedSource, out _, out objectiveSourceIsPrey))
            objectiveSource = resolvedSource;
        if (chosenActive)
            return objectiveSource;

        // 2. Nearest offerer spawner whose offers are actually worth walking
        //    to — in band and not already completed (a spawner whose only
        //    offers are done would send the bot on a pointless trek;
        //    observed live: it walked away from the gated follow-up's offerer
        //    toward unrelated NPCs). THIS SCAN RUNS EVEN WHEN ARM 1b
        //    RESOLVED — that is the fix, and the tail below decides between
        //    the two.
        //
        //    HISTORY. This arm was gated on "no quest at all"
        //    (ActiveQuests.Count == 0), which made a quest this world cannot
        //    serve STRUCTURALLY permanent: measured on a 30-minute 4-bot run,
        //    quest 251's gather objective rejects all 12 candidate NPCs
        //    (rejects=[template=12]), so ActiveQuests stayed at 1 forever and
        //    the bot could never walk toward ANY quest giver — it circled its
        //    27 m presence ring instead, while the nearest real spawned
        //    offerer sat 1331 m away. The wrapper came off; arm 1b then took
        //    the same shape one indirection further out, returning
        //    unconditionally so this scan never ran on an arm-1b wake.
        //
        //    COST. SpawnManager.GetAllSpawners copies the whole table under a
        //    lock, so this scan costs at most ONE copy per wake no matter
        //    which arm wins: arm 1's early return is untouched and arm 1b's
        //    resolver is invoked exactly once, with its answer threaded into
        //    the tail rather than recomputed. The only wake this genuinely
        //    adds work to is the arm-1b-resolves one, which previously
        //    stopped before the scan — that is the unavoidable price of
        //    letting an offerer win at all, and it is one pass over a
        //    dictionary this same walk already copies on every arm-2 wake.
        //    No ordering, short-circuit or memo could avoid it: the winner is
        //    the NEARER of the two, so the nearer one cannot be known before
        //    both are read.
        //
        //    Fail-closed like TrySpawnerPosition: no spawner tables, no walk
        //    target — the bot idles instead of crashing the wake. The tail
        //    also still returns null when nothing in band is walkable.
        var allSpawners = world.SpawnManager?.GetAllSpawners();
        if (allSpawners == null)
            return objectiveSource; // no spawner tables: arm 1b's source if any, else fail closed
        var here = character.Transform.World.Position;
        var best = (Vector3?)null;
        var bestDist = float.MaxValue;
        foreach (var spawner in allSpawners.SelectMany(s => s.Value))
        {
            if (spawner.UnitId == 0)
                continue;
            if (!OffersWalkableQuest(actor, spawner.UnitId, band))
                continue;
            var p = spawner.Position;
            var position = new Vector3(p.X, p.Y, p.Z);
            var d = Vector3.Distance(here, position);
            if (d < bestDist)
            {
                bestDist = d;
                best = position;
            }
        }
        // Arm 2 found nothing walkable. Keep arm 1b's source if it had one —
        // today's behaviour exactly — otherwise decline fail-closed, same
        // null and the same caller's reason string as before.
        if (best == null)
            return objectiveSource;

        // Both arms resolved.
        //
        // A PREY source OUTRANKS the offerer outright. Measured on a 30-minute
        // 4-bot run: quest 251's prey arm resolved a real spawner 265-465 m out
        // on every wake, while nearer quest givers sat 32 m / 117 m / 224 m away,
        // so the nearer-offerer vote buried the prey source 436/436 times; the
        // funnel then rejected its whole candidate pool on template mismatch
        // (have=0) and 324 AdvanceQuest attempts were declined with one
        // byte-identical reason. Worse, the two proximities DISAGREE: the
        // funnel's 25 m perception census is measured at arbitration time,
        // while travel happens over many wakes — the bots did stand 16.1 m from
        // the (15586.5,15117.3) spawner, yet 3475 never entered the census
        // (closest approach to any boar was 59.7 m), so an objective that only
        // walking can unblock is exactly the one a nearer giver out-votes. The
        // prey's OWN guards already withhold when it is perceived or credited,
        // and the 3-arm cap above still bounds the walk, so this is a
        // displacement of the destination, not an abandonment of the quest.
        if (objectiveSource != null && objectiveSourceIsPrey)
            return objectiveSource;

        // Any other source (a DOODAD) keeps the nearer-wins comparison byte for
        // byte: `bestDist` is already the offerer's distance from the bot — arm
        // 2's own ranking metric, not a second measurement — and strict `<`
        // means an exactly-equal objective source still wins, so no doodad
        // fixture that resolves both arms can change behaviour.
        if (objectiveSource != null && Vector3.Distance(here, objectiveSource.Value) < bestDist)
            return objectiveSource; // the source is nearer than the offerer
        return best;                  // the offerer is nearer (or exactly equal is not)
    }

    /// <summary>
    /// True when this NPC template offers at least one quest the bot could
    /// still legally take AND that the decision policy would actually accept.
    /// Legality rides the SAME <see cref="GameplayActor.IsDiscoverable"/> gate
    /// discovery itself applies; the band is a PRE-FILTER ON TOP of it, never
    /// a replacement — without it the bot walks to an offerer whose only
    /// quests are out of band, discovery surfaces nothing, and the loop paces
    /// between useless targets (observed live: 0 discovered at Npc 7137 while
    /// the gated in-band offerer sat 85 m away).
    ///
    /// The band arrives from the caller (<see cref="OfferBandFor"/>) so this
    /// travel pre-filter and the accept sweep read ONE policy value per wake.
    /// </summary>
    private static bool OffersWalkableQuest(GameplayActor actor, uint npcTemplateId,
        QuestDecisionScenario.QuestOptions band)
    {
        foreach (var questId in QuestManager.Instance.GetQuestsOfferedByNpc(npcTemplateId))
        {
            if (band.ChosenQuestId != 0 && questId != band.ChosenQuestId)
                continue;
            var template = QuestManager.Instance.GetTemplate(questId);
            // The template == null guard is load-bearing: 116 quest_components
            // rows have no matching quest_contexts row, and this pre-filter
            // must not dereference what IsDiscoverable has not yet seen.
            if (template == null || template.Level < band.BandMin || template.Level > band.BandMax)
                continue;
            // IsDiscoverable still owns every REAL requirement (Start
            // component unit_reqs — including the MaxLevel kind, which vetoes
            // an offer from ABOVE the character's level — plus the completed
            // non-repeatable rule). The band never substitutes for it.
            if (actor.IsDiscoverable(questId))
                return true;
        }
        return false;
    }

    /// <summary>Spawner position for an NPC template, or null when unspawned/unknown.
    /// Fail-closed: a world without spawner tables (never seeded) reads
    /// unknown rather than throwing — the travel arm then idles instead of
    /// crashing the wake.</summary>
    private static Vector3? TrySpawnerPosition(WorldInstance world, uint npcTemplateId)
        => QuestBehavior.TryNpcSpawnerPosition(world, npcTemplateId);

    /// <summary>
    /// Branch 1b: the nearest spawner position for an active non-Ready quest's
    /// uncredited gather/interact objective source. Quest-id order, gather
    /// before interact within a quest. The gather read is the brain's verdict
    /// inputs in order — (1) requirements: the fixture row's granting set;
    /// (2) nearby: the 25 m perception census — a perceived granting source
    /// short-circuits travel (the gather leg owns the wake, no spawner walk);
    /// (3) distance: the ranked spawner candidates across ALL granting
    /// templates, nearest wins — with the reason naming template + distance.
    /// The executor arms what the brain decides; it never re-decides: the
    /// gather/interact legs own the travel/withdraw verdict, this resolver only
    /// threads their ranked input through to the arming. Resolves spawners
    /// through the character's own world SpawnManager (same discipline as
    /// TrySpawnerPosition). False when no active quest names an uncredited
    /// source (or its source has no spawner).
    ///
    /// PRECEDENCE, unchanged in that order: (a) a DOODAD source — the gather
    /// arm above, then the interact arm — resolves first and returns, so every
    /// doodad-backed objective walks exactly as before; (b) only an objective
    /// with NO doodad source reaches the prey-NPC arm (see below), which
    /// walks to the spawner of the row's derived <c>PreyTemplate</c> so the
    /// prey enters perception range for the funnel to select; (c) otherwise
    /// false, exactly as before.
    ///
    /// <paramref name="sourceIsPrey"/> reports which arm answered — true ONLY
    /// for (b), the prey-NPC source. It is a derivation of (b)'s existing
    /// guards, not a second decision: no arm is added, no guard relaxed, and
    /// both doodad arms keep reporting false.
    /// </summary>
    private static bool ResolveQuestObjectiveSource(GameplayActor actor, uint chosenQuestId, out Vector3 source,
        out uint sourceTemplate, out bool sourceIsPrey)
    {
        source = default;
        sourceTemplate = 0;
        // Prey arm only. Each arm above returns BEFORE this is set, so a
        // gather/interact DOODAD source never reports true — the caller's
        // tie-break therefore reads true for the prey-NPC source and for
        // nothing else.
        sourceIsPrey = false;
        var character = actor.Character;
        var world = character.ParentWorld;
        var quests = character.Quests;
        if (world?.SpawnManager == null || quests == null)
            return false;
        var here = character.Transform.World.Position;
        // True when a prey template is already inside the perception census —
        // i.e. QuestObjectiveTargetSelector would already consider it a
        // candidate, so the funnel owns this wake and travel must not preempt it.
        // LIVENESS, not identity: a DEAD prey-template NPC is not a candidate
        // the selector would take (QuestObjectiveTargetSelector.cs:181-186
        // rejects `Hp <= 0` as `dead`), so it must not suppress this arm
        // either — a corpse standing in the census is what suppressed it and
        // sent the bot to an offerer instead of back to the prey spawner.
        foreach (var (questId, quest) in quests.ActiveQuests.OrderBy(kv => kv.Key))
        {
            if (chosenQuestId != 0 && questId != chosenQuestId)
                continue;
            if (quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
                continue;
            QuestFixtureRow row;
            try
            {
                row = QuestFixtureRow.FromQuestData(questId);
            }
            catch
            {
                continue;
            }
            // The gather arm's answer for THIS row, held back when the row also
            // names a prey so the prey arm below gets its turn; read only when
            // that same row's prey arm declines. Scoped to the iteration, so one
            // quest's fallback can never answer another quest's walk.
            var hasGatherFallback = false;
            var gatherFallbackPos = default(Vector3);
            uint gatherFallbackTemplate = 0;

            if (QuestBehavior.HasGatherTemplates(row) && !QuestBehavior.GatherCredited(questId, quest, row))
            {
                // Nearby first: a perceived granting source means the gather leg
                // owns this wake — travel never preempts it.
                var snapshot = BotObservedContext.Capture(actor);
                var legContext = new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(), questId, row, null)
                {
                    Observation = snapshot
                };
                if (QuestBehavior.ResolveGatherDoodad(legContext, row).ObjId != 0)
                    continue;
                if (QuestBehavior.TryNearestGatherSpawnerPosition(world, row, here, out var gatherTemplate, out var gatherPos))
                {
                    // A row that ALSO derives a PreyTemplate through the loot
                    // chain is the END-TO-END shape: the prey arm below is the
                    // only source travel can complete (kill -> loot -> credit),
                    // so this arm must not claim the wake first. Measured on a
                    // 36-minute 4-bot run: quest 251's gather item reaches both
                    // doodad wells and an npc loot pack, this arm returned the
                    // nearest well on every wake, and all four bots walked
                    // ~600 m off-cluster to non-prey spawners while the prey
                    // spawner stood 666 m out and the 25 m census stayed empty.
                    //
                    // Fail-closed and byte-identical otherwise: the position is
                    // remembered, not discarded, so a hybrid row whose prey has
                    // no spawner (or whose prey is already perceived) still
                    // travels exactly where it did before. A PURE-doodad row
                    // derives PreyTemplate == 0, so it returns here unchanged.
                    if (row.PreyTemplate == 0)
                    {
                        source = gatherPos;
                        sourceTemplate = gatherTemplate;
                        return true;
                    }

                    gatherFallbackPos = gatherPos;
                    gatherFallbackTemplate = gatherTemplate;
                    hasGatherFallback = true;
                }
            }
            // Ordering guard, byte-identical to before: a row whose gather arm
            // already produced a source (held back above, or returned outright)
            // keeps it, so the interact objective never used to out-rank a live
            // gather source and still does not on a hybrid row.
            if (!hasGatherFallback
                && row.InteractDoodadTemplate != 0 && !QuestBehavior.InteractCredited(questId, quest, row))
            {
                if (QuestBehavior.TryNearestDoodadSpawnerPosition(world, row.InteractDoodadTemplate, here, out var interactPos))
                {
                    source = interactPos;
                    sourceTemplate = row.InteractDoodadTemplate;
                    return true;
                }
            }
            // (c) The objective's source is the PREY NPC itself, not a doodad:
            //     a QuestActObjItemGather with no highlight_doodad_id derives a
            //     PreyTemplate through the loot chain (npc>pack>item), and no
            //     doodad template can ever match such an act — so both doodad
            //     arms above no-op and the resolver used to return false, never
            //     carrying the bot to the prey. Left that way the prey stays
            //     outside the 25 m perception census, QuestObjectiveTargetSelector
            //     rejects the whole candidate pool on template mismatch, and the
            //     objective can never advance.
            //
            //     ORDER — why LAST, and the ONE exception. A PURE-doodad
            //     objective has already returned from the arm above, so every
            //     doodad-backed objective walks to the very same spawner at the
            //     very same template with byte-identical reasons. The exception
            //     is the HYBRID row: an item that reaches BOTH a doodad well and
            //     an npc loot pack. Its gather arm is held back (see above) and
            //     this arm gets its turn, because only this source completes the
            //     objective end to end. If this arm declines, the held answer is
            //     returned in its place, so a hybrid row can never end up with
            //     no source at all.
            //
            //     DELIVER EXCLUDED, and that exclusion IS the "source is an NPC"
            //     condition: a quest whose own Supply act grants the gather item
            //     (the basil-delivery shape) has no prey at all — its source is
            //     the grant — so walking it to a mob spawner would be the exact
            //     pointless trek the arm-1b comment above documents (bots burning
            //     the navigation budget on spawners they have no reason to visit).
            //
            //     Same two disciplines as the arms above: a perceived prey means
            //     the funnel owns this wake — travel never preempts it (its own
            //     capture, exactly as each arm above takes its own); and the
            //     spawner lookup is the SAME helper arm 1 uses
            //     (TrySpawnerPosition), not a second scan. Fail-closed: no
            //     gather act, a credited objective, no prey template, a Deliver
            //     shape, or a prey with no spawner all fall through to the same
            //     `false` as before.
            if (row.GatherActId != 0 && row.PreyTemplate != 0
                && row.ObjectivePattern != AAEmu.Game.Models.Game.Quests.Director.QuestPattern.Deliver
                && !QuestBehavior.GatherCredited(questId, quest, row)
                && !QuestBehavior.HasPerceivedLivingPrey(actor, BotObservedContext.Capture(actor), row.PreyTemplate)
                && TrySpawnerPosition(world, row.PreyTemplate) is { } preyPos)
            {
                source = preyPos;
                sourceTemplate = row.PreyTemplate;
                sourceIsPrey = true;
                return true;
            }

            // The gather arm held an answer back for THIS row and the prey arm
            // could not use it (no spawner, Deliver shape, or the prey already
            // perceived). Nothing is lost: the held answer is returned exactly
            // where the gather arm used to return it, with sourceIsPrey still
            // false, so the caller's distance tie-break and the 3-arm doodad cap
            // keep treating it as the doodad source it is.
            if (hasGatherFallback)
            {
                source = gatherFallbackPos;
                sourceTemplate = gatherFallbackTemplate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Per-bot behavior runtimes hosting the quest leg (same pattern as the homestead runner cache).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, BotBehaviorRuntime> _questRuntimes = [];
    /// <summary>E2E-ONLY per-bot turn-in withhold flags (default-off, unset =
    /// false). Set through the quest bridge sub; read by the quest leg into
    /// <see cref="QuestDecisionScenario.QuestOptions"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _questTurnInWithheld = [];

    /// <summary>E2E-ONLY: arm/clear the turn-in withhold for one bot.</summary>
    internal void SetQuestTurnInWithheld(uint characterId, bool withheld)
    {
        if (withheld)
            _questTurnInWithheld[characterId] = true;
        else
            _questTurnInWithheld.TryRemove(characterId, out _);
    }

    /// <summary>E2E-ONLY: current turn-in withhold flag for one bot.</summary>
    internal bool IsQuestTurnInWithheld(uint characterId)
        => _questTurnInWithheld.TryGetValue(characterId, out var withheld) && withheld;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, AAEmu.Game.Core.Managers.Bots.Goap.IGoapPlanRunner> _homesteadRunners = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, AAEmu.Game.Core.Managers.Bots.Goap.BotContext> _homesteadContexts = [];

    /// <summary>
    /// Homestead progression leg (branch 0c): autonomous GOAP planning and multi-tick
    /// execution driving starter bots to acquire an 8x8 farm plot, harvest timber,
    /// craft material packs, and construct their homestead.
    /// </summary>
    private bool StepHomesteadLeg(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state)
    {
        if (!_homesteadRunners.TryGetValue(bot.CharacterId, out var runner) || !_homesteadGenerations.TryGetValue(bot.CharacterId, out var homeGen) || homeGen != state.Generation)
        {
            runner = new AAEmu.Game.Core.Managers.Bots.Goap.GoapPlanRunner();
            _homesteadRunners[bot.CharacterId] = runner;
            _homesteadGenerations[bot.CharacterId] = state.Generation;
        }
        if (!_homesteadContexts.TryGetValue(bot.CharacterId, out var context) || !_homesteadGenerations.TryGetValue(bot.CharacterId, out var ctxGen) || ctxGen != state.Generation)
        {
            context = new AAEmu.Game.Core.Managers.Bots.Goap.BotContext();
            _homesteadContexts[bot.CharacterId] = context;
            _homesteadGenerations[bot.CharacterId] = state.Generation;
        }

        if (!context.Memory.KnownHousingZonePos.HasValue)
        {
            var fallback = new Vector3(20450.0f, 10820.0f, 130.0f);
            context.Memory.KnownHousingZonePos = AAEmu.Game.Core.Managers.Bots.Goap.Actions.TravelToHousingZoneAction.ResolveNearestHousingZone(bot.Character, fallback);
        }

        return runner.Tick(bot, actor, context);
    }

    /// Tier 0 needs-farm leg (branch 0b): legible per-wake re-evaluation, not
    /// a script — observe → seed absent? buy path; seed + invalid soil?
    /// travel path; seed + valid soil? plant; tracked/scanned crop immature?
    /// deferred wait; mature? harvest via the normal path; output + seed?
    /// replant next wake.
    ///
    /// TRAVEL-TO-SOIL arms an ordinary <see cref="BotPath"/> (single-leg
    /// <c>PathTo</c>) to a soil destination and returns false so the route
    /// layer's own MoveTo legs + arrival advance carry the bot (no teleport,
    /// no Transform writes — movement applies through the actor tick exactly
    /// like patrol legs). The destination is decided by the NEEDS BRAIN
    /// (<see cref="NeedsBrain.Decide"/> through <see cref="NeedsBrainPlanner"/> and
    /// <see cref="NeedsFarmLegDispatch"/>) on the wake that resolves it — the same
    /// bounded spiral, so the destination authority is unchanged — and every
    /// verdict the leg cannot serve falls back to the pre-brain rule. Arrival
    /// re-observes and plants normally. MATURITY-WAIT tracks the planted crop
    /// (objId + template) and defers while its phase reads immature — no Harvest is
    /// issued on wait wakes; the engine stays the sole maturity authority at
    /// dispatch.
    ///
    /// PLANT and HARVEST are the brain's TWO ACTION ARMS (the second and third live
    /// callers, through the same <see cref="NeedsFarmLegDispatch"/> seam): the
    /// on-soil + seed wake is decided as the existing Plant verb and the
    /// mature-crop-in-range wake as the existing Harvest verb, each with the arm's
    /// own verdict table, and the named maturity wait is the hold that dispatches
    /// nothing. Every other verdict falls back to this leg's own pre-brain rule, so
    /// the wiring is additive and never widens a wake.
    ///
    /// Returns true only when decision work actually landed (Completed) —
    /// reject/rest/wait/travel/defer wakes return false so the route still
    /// walks and the scheduler keeps its cadence instead of spinning.
    /// </summary>
    private bool StepNeedsFarmLeg(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state)
    {
        var character = bot.Character;
        var position = character.Transform.World.Position;
        var now = TimeProvider.GetUtcNow().UtcDateTime;

        // Post-action standstill cadences (human-like pacing)
        if (now < state.HarvestingUntilUtc)
        {
            BroadcastStandstill(character);
            return false;
        }

        if (now < state.PlantingUntilUtc)
        {
            BroadcastStandstill(character);
            return false;
        }

        // ---- 1. Tracked crop: cheap per-wake liveness check (one world
        // lookup + phase read — not a scan). Gone/harvested/despawned/
        // ownership-changed → drop the stale id and re-evaluate below.
        // An immature crop defers through the brain's wait row — no harvest.
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
                // THE IMMATURE-CROP WAIT ROW: the tracked crop is live but not mature, so
                // the wake's verdict is the NAMED hold — the brain says so and the leg
                // dispatches nothing (a crop that matured between the two reads would be
                // the same row at the next wake, and the engine revalidates maturity
                // fail-closed at dispatch). This arm owns the wake through the live crop
                // and asks no soil question, so no merchant is resolved for it.
                return StepNeedsFarmImmatureCrop(bot, actor, state, character, position, tracked!,
                    $"crop {tracked!.ObjId} immature");
            }
        }

        // ---- 2. Untracked scan: nearest owned/public crop in perception.
        // Mature → harvest path; immature → adopt the track and defer through the
        // brain's wait row (the decision is asked every wake, and a wait issues no
        // Harvest).
        var scanned = NearestNeedsFarmCrop(character, position);
        if (scanned != null)
        {
            if (IsCropMature(scanned))
                return StepNeedsFarmMatureCrop(bot, actor, state, character, position, scanned);
            state.NeedsFarmCropObjId = scanned.ObjId;
            state.NeedsFarmCropTemplateId = scanned.TemplateId;
            return StepNeedsFarmImmatureCrop(bot, actor, state, character, position, scanned,
                $"adopted crop {scanned.ObjId} immature");
        }

        // ---- 3. Seed branches: seed absent → buy path; seed + valid soil →
        // plant; seed + invalid soil → travel path.
        var (nearestMerchant, merchantDist) = ResolveNearestSeedMerchant(character, position);
        var merchantObjId = merchantDist <= GameplayActor.MaxShopRange ? nearestMerchant?.ObjId ?? 0 : 0;
        if (SeedInBag(character) > 0)
        {
            var tooCloseToMerchant = nearestMerchant != null && merchantDist <= 5.0f;
            if (IsValidFarmSoil(character, position) && !tooCloseToMerchant)
            {
                state.NeedsFarmSoilAttempts = 0;
                state.NeedsFarmDeferWakes = 0;
                RestoreFarmRoute(state, bot, "on valid soil");
                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                {
                    // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                    LogQuestTravelHaltCaller(bot, state, "settle", "farm-on-soil");
                    _ = actor.Stop();
                    state.PendingLeg = null;
                }
                BroadcastStandstill(character);
                // THE ACTION ARM'S PLANT ROW: this wake is decided by the needs brain
                // (seed on hand and standing on valid soil → the EXISTING Plant verb).
                // Any other verdict is the named fallback, and the leg's own pre-brain
                // rule — the plant this branch already earned — owns the dispatch, so
                // the decision rides additively and never widens the wake.
                var plantVerdict = DecideNeedsFarmActionLeg(state, actor, position,
                    NeedsFarmLegDispatch.NeedsFarmArm.Plant, nearestMerchant);
                return DispatchNeedsFarmActionLeg(bot, actor, state, plantVerdict,
                    merchantObjId, 0, position, offerPlant: true);
            }
            return StepNeedsFarmTravel(bot, actor, state, character, position);
        }

        if (nearestMerchant != null && merchantDist > GameplayActor.MaxShopRange)
        {
            // Seed needed and merchant visible in perception, but out of shop range — approach merchant!
            var merchantPos = nearestMerchant.Transform.World.Position;
            var enRoute = state.NeedsFarmSoilTarget is { } target
                && state.Path is { IsFinished: false }
                && MathUtil.CalculateDistance(state.Path.CurrentTarget, merchantPos, false) <= GameplayActor.MaxShopRange;
            if (!enRoute)
            {
                ArmFarmRoute(state, bot, merchantPos,
                    $"seed merchant {nearestMerchant.ObjId} at {merchantDist:F1}m — approaching");
            }
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Traveling,
                $"seed merchant {nearestMerchant.ObjId} at {merchantDist:F1}m — approaching");
            return false;
        }

        if (nearestMerchant != null && merchantDist <= GameplayActor.MaxShopRange)
        {
            if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            {
                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                LogQuestTravelHaltCaller(bot, state, "settle", "farm-merchant-range");
                _ = actor.Stop();
                state.PendingLeg = null;
            }
            RestoreFarmRoute(state, bot, "arrived at seed merchant");

            // Face the merchant and broadcast standstill
            var angle = MathUtil.CalculateAngleFrom(character.Transform.World.Position, nearestMerchant.Transform.World.Position);
            character.Transform.Local.SetRotationDegree(0f, 0f, (float)angle - 90);
            character.Transform.FinalizeTransform();
            BroadcastStandstill(character);
        }

        state.NeedsFarmSoilAttempts = 0;
        state.NeedsFarmDeferWakes = 0;
        return AfterNeedsFarmDispatch(state, bot,
            DispatchNeedsFarmLeg(bot, actor, merchantObjId, 0, position));
    }

    /// <summary>
    /// THE ACTION ARM'S DISPATCH TAIL — the one place both action arms' verdicts
    /// become (or refuse to become) a wake:
    ///  - <see cref="NeedsFarmLegDispatch.NeedsFarmActionVerb.WaitMaturity"/>: the
    ///    NAMED hold — no dispatch, no verb, the phase reports the wait. This is the
    ///    row that stops a wake, so it is checked FIRST: a brain that says the crop is
    ///    immature outranks the site's own reading of it.
    ///  - every other verdict (the two routed verbs, and the fallback): the leg's own
    ///    dispatch tail runs exactly as it did before the wiring —
    ///    <paramref name="offerPlant"/> is the caller's pre-brain rule (the
    ///    mature-harvest starvation guard), so a fallback can never dispatch something
    ///    the wake did not already earn.
    /// The brain's routing fact rides into the leg's phase reason either way, so the
    /// decision that produced (or refused) the wake stays readable.
    /// </summary>
    private bool DispatchNeedsFarmActionLeg(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state,
        in NeedsFarmLegDispatch.NeedsFarmActionVerdict verdict,
        uint merchantObjId, uint cropObjId, Vector3 position, bool offerPlant)
    {
        var note = BrainNote(verdict);
        if (verdict.IsWait)
        {
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
                $"crop {verdict.CropObjId} immature — holding, no dispatch{note}");
            return false;
        }
        return AfterNeedsFarmDispatch(state, bot,
            DispatchNeedsFarmLeg(bot, actor, merchantObjId, cropObjId, position, offerPlant), note);
    }

    /// <summary>The brain's routing fact as it rides the leg's own phase reason: the decision,
    /// whether this arm routed through it, and the dispatch it named. Built on read only.</summary>
    private static string BrainNote(in NeedsFarmLegDispatch.NeedsFarmActionVerdict verdict)
        => $" [brain={NeedsFarmLegDispatch.Describe(verdict)}]";

    /// <summary>
    /// THE IMMATURE-CROP WAIT ROW — the leg's own pre-brain maturity read said the
    /// tracked crop is not mature, so the wake asks the brain and reports its verdict.
    /// That verdict is <see cref="NeedsBrain.Decide"/>'s own
    /// <see cref="NeedsVerdict.WaitMaturity"/> row (the same shared maturity rule,
    /// <see cref="NeedsCrop.IsMature"/>, read on the same doodad inside the same wake),
    /// so the row NAMES the hold and dispatches nothing.
    ///
    /// The row is still served through the SAME action tail rather than logged and
    /// ignored: were the two reads ever to disagree (a crop maturing between them), the
    /// brain's dispatch is what the wake does — and the engine revalidates maturity
    /// fail-closed at dispatch, so a harvest it refuses costs one wake, never a wrong
    /// crop.
    /// </summary>
    private bool StepNeedsFarmImmatureCrop(PlayerBotRuntime bot, IGameplayActor actor, BotRoamState state,
        Character character, Vector3 position, Doodad crop, string what)
    {
        var verdict = DecideNeedsFarmActionLeg(state, actor, position,
            NeedsFarmLegDispatch.NeedsFarmArm.Harvest, nearestMerchant: null);
        if (verdict.HasLeg)
        {
            return DispatchNeedsFarmActionLeg(bot, actor, state, verdict,
                ResolveSeedMerchant(character, position), crop.ObjId, position, offerPlant: false);
        }
        SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
            $"{what} — waiting at plot, no harvest issued{BrainNote(verdict)}");
        StepFarmLeisure(bot, actor, state, character, crop.Transform.World.Position);
        return false;
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
        // THE ONE HARVEST RANGE, shared with the brain so the two cannot drift: the arm
        // scoping below relies on this branch and NeedsBrain.Decide agreeing on what
        // "in harvest range" means — a leg that read a narrower range than the brain
        // would approach a crop the brain already called harvestable, and a wider one
        // would dispatch a harvest the brain called an approach.
        const float CropHarvestInteractRange = NeedsBrain.DefaultHarvestRangeM;
        if (dist > CropHarvestInteractRange)
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

        // In interact range! Halt approach, face the crop, and broadcast standstill so bot doesn't walk in place
        if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
        {
            // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
            LogQuestTravelHaltCaller(bot, state, "settle", "farm-harvest-range");
            _ = actor.Stop();
            state.PendingLeg = null;
        }
        RestoreFarmRoute(state, bot, "in harvest range of mature crop");

        var angle = MathUtil.CalculateAngleFrom(character.Transform.World.Position, crop.Transform.World.Position);
        character.Transform.Local.SetRotationDegree(0f, 0f, (float)angle - 90);
        character.Transform.FinalizeTransform();
        BroadcastStandstill(character);

        state.NeedsFarmSoilAttempts = 0;
        var merchantObjId = ResolveSeedMerchant(character, position);
        // THE ACTION ARM'S HARVEST ROW: the mature-crop-in-range wake is decided by the
        // needs brain (the live MATURE crop inside the harvest range → the EXISTING
        // Harvest verb on the tracked objId). Any other verdict falls back to this
        // leg's own pre-brain rule — the harvest this branch already earned — so the
        // decision rides additively and never widens the wake.
        var harvestVerdict = DecideNeedsFarmActionLeg(state, actor, position,
            NeedsFarmLegDispatch.NeedsFarmArm.Harvest, nearestMerchant: null);
        return DispatchNeedsFarmActionLeg(bot, actor, state, harvestVerdict,
            merchantObjId, crop.ObjId, position, offerPlant: false);
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
        var (nearestMerchant, merchantDist) = ResolveNearestSeedMerchant(character, position);
        var tooCloseToMerchant = nearestMerchant != null && merchantDist <= 5.0f;
        if (IsValidFarmSoil(character, position) && !tooCloseToMerchant)
        {
            state.NeedsFarmSoilAttempts = 0;
            state.NeedsFarmDeferWakes = 0;
            RestoreFarmRoute(state, bot, "arrived on valid soil");
            if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            {
                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                LogQuestTravelHaltCaller(bot, state, "settle", "farm-soil-arrival");
                _ = actor.Stop();
                state.PendingLeg = null;
            }
            BroadcastStandstill(character);
            state.PlantingUntilUtc = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(2.0);
            var merchantObjId = ResolveSeedMerchant(character, position);
            // THE SAME PLANT ARM, reached by ARRIVAL rather than by standing on soil at
            // the head of the wake: the on-soil + seed wake is decided by the needs brain
            // (the existing Plant verb), and every other verdict falls back to this
            // branch's own pre-brain rule — the plant the arrival already earned.
            var plantVerdict = DecideNeedsFarmActionLeg(state, actor, position,
                NeedsFarmLegDispatch.NeedsFarmArm.Plant, nearestMerchant);
            return DispatchNeedsFarmActionLeg(bot, actor, state, plantVerdict,
                merchantObjId, 0, position, offerPlant: true);
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
        var brainReason = "";
        if (resolveWake && state.NeedsFarmSoilAttempts <= NeedsFarmMaxSoilAttempts)
        {
            // ---- THE NEEDS BRAIN'S FIRST LIVE DISPATCH CALLER: this leg's soil
            // destination is decided by <see cref="NeedsBrain.Decide"/> through
            // <see cref="NeedsBrainPlanner"/> — the SAME bounded spiral, so the
            // destination authority is unchanged — and the point the decision names
            // is walked by the existing route layer below. The caller never supplies
            // a destination to the planner (SoilDestinationResolved stays false), so
            // a fabricated point cannot reach a travel leg through it: the spiral
            // produced the destination, or the pre-brain rule below owns the wake.
            //
            // The decision rides ADDITIVELY: every verdict that is not a soil
            // destination this leg can walk — a NAMED hold (an unreadable soil
            // surface or seed count, a spent resolve budget), the bounded-spiral
            // demand, a crop arm this leg does not serve, or the brain's own
            // plant-on-soil reading when this leg is standing on soil it refuses to
            // plant on (the merchant-proximity rule) — falls through to the caller's
            // exact pre-brain rule. The route-layer hold for a leg already walking
            // its point stays the CALLER's own reading above (the leg it itself
            // issued), never a second brain arm.
            var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(
                PrepareNeedsFarmTravelBrain(state, actor, position, nearestMerchant));
            brainReason = $" [brain={NeedsFarmLegDispatch.Describe(verdict)}]";
            if (verdict.HasLeg)
            {
                var destination = verdict.Destination!.Value;
                state.NeedsFarmDeferWakes = 0;
                ArmFarmRoute(state, bot, destination,
                    $"soil resolved ({destination.X:F0},{destination.Y:F0}) — traveling{brainReason}");
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Traveling, state.NeedsFarmReason);
                return false;
            }
            var fallback = verdict.SearchCovered ? null : ResolveSoilTarget(character, position);
            if (fallback != null)
            {
                state.NeedsFarmDeferWakes = 0;
                ArmFarmRoute(state, bot, fallback.Value,
                    $"soil resolved ({fallback.Value.X:F0},{fallback.Value.Y:F0}) — traveling{brainReason}");
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Traveling, state.NeedsFarmReason);
                return false;
            }
        }
        RestoreFarmRoute(state, bot, "no farm nearby — bounded defer");
        SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.SeekingSoil,
            state.NeedsFarmSoilAttempts > NeedsFarmMaxSoilAttempts
                ? $"no farm nearby — resolve budget spent, holding defer{brainReason}"
                : $"no farm nearby — bounded defer{brainReason}");
        return false;
    }

    /// <summary>
    /// The needs brain's LIVE PREPARATION for this leg's travel arm: one
    /// <see cref="NeedsBrainPlanner.Prepare"/> over the actor's own record, driven
    /// by THIS leg's own reads and seams — the shared soil probe
    /// (<see cref="ProbeFarmSoil"/>, so the injectable surface the leg uses is the
    /// surface the spiral walks), the shared ground pin
    /// (<see cref="WithGroundZ"/>), the merchant exclusion this leg already
    /// resolved, and the tracked-crop resolve. The SEED COUNT and the crop's
    /// liveness/maturity/distance are read by the planner itself; the SOIL attempt
    /// count is the leg's own, so this leg's resolve bound and the brain's budget
    /// are the same number by construction.
    ///
    /// No destination and no resolve flag is supplied: the destination can only be
    /// the bounded spiral's own output.
    /// </summary>
    private NeedsBrainPlanner.Prepared PrepareNeedsFarmTravelBrain(
        BotRoamState state, IGameplayActor actor, Vector3 position, Npc? nearestMerchant)
        => CaptureNeedsPrepared(NeedsBrainPlanner.Prepare(actor, NeedsFarmBrainRequest(state, position, nearestMerchant,
            soilSearch: true, cropArm: false)));

    /// <summary>
    /// The leg's TWO ACTION ARMS through the brain (the second live caller): one
    /// <see cref="NeedsBrainPlanner.Prepare"/> over this leg's own reads, then
    /// <see cref="NeedsFarmLegDispatch.DecideNeedsFarmAction"/> scoped to
    /// <paramref name="arm"/>. The leg's branch structure stays the caller's own
    /// (its pre-brain rule) and the table names which row applies: the existing
    /// Plant or Harvest verb, the no-verb maturity wait, or the fallback.
    ///
    /// The ACTION arms never run the bounded spiral (<c>SoilSearch</c> is false
    /// whenever the wake is owned by a LIVE CROP), so no destination is produced or
    /// consumed here at all — the planter's Plant verdict comes from the surface
    /// read underfoot, and the harvest arm's from the tracked crop.
    /// </summary>
    private NeedsFarmLegDispatch.NeedsFarmActionVerdict DecideNeedsFarmActionLeg(
        BotRoamState state, IGameplayActor actor, Vector3 position,
        NeedsFarmLegDispatch.NeedsFarmArm arm, Npc? nearestMerchant)
        => NeedsFarmLegDispatch.DecideNeedsFarmAction(
            CaptureNeedsPrepared(NeedsBrainPlanner.Prepare(actor, NeedsFarmBrainRequest(state, position, nearestMerchant,
                soilSearch: arm == NeedsFarmLegDispatch.NeedsFarmArm.Plant, cropArm: true))),
            arm);

    /// <summary>
    /// Jev shadow: freezes one needs-brain <c>Prepared</c> this wake for the lane.
    /// Returns the SAME prepared value the caller was about to use, so the
    /// capture is observation only — the decision, its destination and its
    /// dispatch are byte-identical whether the flag is on or off. Flag-off is a
    /// static read and a return of the argument.
    /// </summary>
    private NeedsBrainPlanner.Prepared CaptureNeedsPrepared(NeedsBrainPlanner.Prepared prepared)
    {
        if (JevShadow.Enabled)
            JevShadow.CaptureNeeds(prepared.Inputs.ActorObjId, prepared.Inputs, prepared.Decision);
        return prepared;
    }

    /// <summary>
    /// THE LEG'S ONE BRAIN REQUEST: every read this leg hands the planner is built
    /// here — the travel arm and both action arms ask through the same request shape,
    /// so the soil probe, the ground pin, the merchant exclusion and the tracked-crop
    /// resolve are the same seam whoever asks (one destination authority, one crop
    /// rule, one seed count).
    ///
    /// <paramref name="cropArm"/> selects which reading of the leg's shared attempt
    /// counter feeds the CROP-approach budget: the travel arm reaches its destination
    /// with the crop track already served by the wait/mature branches (so the layer's
    /// default stands), while the action arms use the leg's own counter — the number
    /// the mature-crop branch itself bounds its re-arms on.
    /// </summary>
    private NeedsBrainPlanner.Request NeedsFarmBrainRequest(
        BotRoamState state, Vector3 position, Npc? nearestMerchant, bool soilSearch, bool cropArm)
        => new(
            SelfPosition: position,
            SeedItemTemplateId: NeedsFarmSeedItemTemplateId,
            SeedReadable: true,
            SeedCount: 0, // the planner reads the bag itself from the template id (Request.Bare's shape)
            CropEnRoute: false,
            SoilEnRoute: false, // the caller's live-leg hold is the leg's own reading, never reached here
            CropApproachAttempts: cropArm ? state.NeedsFarmSoilAttempts : 0,
            MaxCropApproachAttempts: cropArm ? NeedsFarmMaxSoilAttempts : NeedsBrain.DefaultMaxCropApproachAttempts,
            HarvestJustLanded: false,
            HarvestRangeM: NeedsBrain.DefaultHarvestRangeM,
            SoilAttempts: state.NeedsFarmSoilAttempts,
            MaxSoilAttempts: NeedsFarmMaxSoilAttempts,
            SoilProbe: (c, candidate) => ProbeFarmSoil(c, candidate),
            // The same ground pin the pre-brain search applies, with the same anchor
            // Z fallback (the planner hands the candidate alone); 0 height = no data
            // → the anchor Z stands.
            GroundZ: (candidateCharacter, candidate) => WithGroundZ(candidateCharacter, candidate, position.Z),
            SoilDestination: null,
            SoilDestinationResolved: false,
            SeedMerchantPosition: nearestMerchant?.Transform.World.Position ?? Vector3.Zero,
            SeedMerchantResolved: nearestMerchant != null,
            TrackedCropObjId: state.NeedsFarmCropObjId,
            TrackedCropTemplateId: state.NeedsFarmCropTemplateId,
            DoodadResolver: DoodadResolver,
            SoilSearch: soilSearch);

    /// <summary>
    /// Option 1: Leashed farm leisure / micro-wander while WaitingMaturity.
    /// Keeps the bot naturally active within 4-8m of its crop instead of
    /// freezing stiff like a statue or roaming miles away along the highway.
    /// </summary>
    private void StepFarmLeisure(PlayerBotRuntime bot, IGameplayActor actor,
        BotRoamState state, Character character, Vector3 cropPos)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var currentPos = character.Transform.World.Position;
        var distToCrop = MathUtil.CalculateDistance(currentPos, cropPos, false);

        // If drifted outside the 8m farm leash, path back toward the crop
        if (distToCrop > 8.0f)
        {
            if (actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move })
            {
                StageMoveOwner(actor, "OTHER:needs-farm-leisure");
                _ = actor.MoveTo(cropPos, 1.8f, TimeSpan.FromSeconds(10));
            }
            return;
        }

        // While moving on a leisure leg, let it progress naturally
        if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            return;

        // When stationary at a vantage point, broadcast standstill so client stays idle
        BroadcastStandstill(character);

        // When idle at a vantage point, loiter for 8-14s before moving again
        if (now - state.LastLeisureStepUtc < state.NextLeisureInterval)
            return;

        state.LastLeisureStepUtc = now;
        var randomDelaySec = 8 + (int)(bot.CharacterId % 7); // pseudo-random 8..14s
        state.NextLeisureInterval = TimeSpan.FromSeconds(randomDelaySec);

        // Pick a candidate leisure point 3-6m around the crop on valid soil
        var angle = ((now.Ticks / TimeSpan.TicksPerSecond) % 360) * MathF.PI / 180f;
        var radius = 3.0f + (float)((bot.CharacterId + now.Second) % 4);
        var targetX = cropPos.X + MathF.Cos(angle) * radius;
        var targetY = cropPos.Y + MathF.Sin(angle) * radius;
        var candidate = new Vector3(targetX, targetY, cropPos.Z);

        if (IsValidFarmSoil(character, candidate))
        {
            var groundZ = WithGroundZ(character, candidate, cropPos.Z);
            StageMoveOwner(actor, "OTHER:needs-farm-leisure");
            _ = actor.MoveTo(groundZ, 1.8f, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Dispatch tail: maps the decision outcome onto the loop phase (the
    /// observability seam — phase + target + reason stay readable off the
    /// state) and records a landed plant's crop for the wait branch. Keeps
    /// the 0b contract: true only when decision work landed (Completed).
    /// </summary>
    private bool AfterNeedsFarmDispatch(BotRoamState state, PlayerBotRuntime bot,
        NeedsDecisionScenario.NeedsRunResult? result, string brainNote = "")
    {
        if (result == null)
        {
            SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle, "actor seam unavailable");
            return false;
        }
        // Jev shadow (Stage 1, default OFF): the NEEDS arbitration wake. The
        // needs leg runs its own decision cycle, which the quest emit point never
        // sees, so without this the needs class contributes zero rows. Emitted
        // from the already-frozen NeedsRunResult + this wake's captured needs
        // brain inputs — no extra read, no re-plan.
        if (JevShadow.Enabled && state.Actor is { } emitActor)
        {
            JevShadow.MaybeEmitNeeds(emitActor.ActorId, result);
            JevShadow.ReleaseWake(emitActor.ActorId);
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
                state.PlantingUntilUtc = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(2.0);
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.WaitingMaturity,
                    $"planted crop {plantedObjId} — waiting maturity{brainNote}");
                break;
            case ActorActionType.Plant when landed:
                state.PlantingUntilUtc = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(2.0);
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Planting,
                    $"plant landed without crop objId{brainNote}");
                break;
            case ActorActionType.Plant:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Planting,
                    $"plant dispatched ({result.Request?.State}){brainNote}");
                break;
            case ActorActionType.Harvest when landed:
                var harvested = state.NeedsFarmCropObjId;
                state.NeedsFarmCropObjId = 0;
                state.NeedsFarmCropTemplateId = 0;
                var castSeconds = 4.0;
                if (result.Request?.Payload is HarvestParams hp && hp.CastTimeMs > 0)
                {
                    castSeconds = hp.CastTimeMs / 1000.0;
                }
                state.HarvestingUntilUtc = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(castSeconds);
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Replanting,
                    $"harvested crop {harvested} — re-evaluating (seed+output → replant){brainNote}");
                break;
            case ActorActionType.Harvest:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Harvesting,
                    $"harvest dispatched ({result.Request?.State}){brainNote}");
                break;
            case ActorActionType.Buy when landed:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle,
                    $"seed bought — re-evaluating{brainNote}");
                break;
            case ActorActionType.Sell when landed:
                // Earn leg landed: surplus liquidated toward the seed price.
                // Idle re-evaluates to buy on the NEXT wake — never a scripted
                // sell-then-buy chain in one wake (re-evaluation discipline).
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle,
                    $"surplus sold — re-evaluating{brainNote}");
                break;
            default:
                SetNeedsFarmPhase(state, bot, NeedsFarmLoopPhase.Idle,
                    $"rest/reject ({result.SelectedAction}) — yielding{brainNote}");
                break;
        }
        return landed;
    }

    /// <summary>
    /// Nearest seed merchant in perception radius, regardless of shop range.
    /// Returns the merchant NPC and distance, or null if none found in perception.
    /// </summary>
    private (Npc? Merchant, float Distance) ResolveNearestSeedMerchant(Character character, Vector3 position)
    {
        var world = character.ParentWorld;
        if (world == null)
            return (null, float.MaxValue);
        Npc? bestMerchant = null;
        var bestDist = float.MaxValue;
        var merchants = (NearbyNpcProvider ?? DefaultNearbyNpcs)(character, NeedsFarmPerceptionRadius);
        foreach (var npc in merchants)
        {
            if (npc?.Template == null || !npc.Template.Merchant || npc.Template.MerchantPackId == 0)
                continue;
            var pack = NpcManager.Instance.GetGoods(npc.Template.MerchantPackId);
            if (pack == null || !pack.SellsItem(NeedsFarmSeedItemTemplateId))
                continue;
            var d = MathUtil.CalculateDistance(position, npc.Transform.World.Position, false);
            if (d < bestDist)
            {
                bestDist = d;
                bestMerchant = npc;
            }
        }
        return (bestMerchant, bestDist);
    }

    /// <summary>
    /// Seed-merchant discovery: nearest in-range merchant whose pack sells the
    /// seed, 0 when none.
    /// </summary>
    private uint ResolveSeedMerchant(Character character, Vector3 position)
    {
        var (merchant, dist) = ResolveNearestSeedMerchant(character, position);
        return dist <= GameplayActor.MaxShopRange ? merchant?.ObjId ?? 0 : 0;
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
    /// the engine's gate — this only decides whether the track is OURS. The rule
    /// lives once, in <see cref="NeedsCrop"/> (shared with the needs brain).
    /// </summary>
    private static bool IsTrackedCropLive(Doodad? tracked, Character character, BotRoamState state)
        => NeedsCrop.IsTrackedLive(tracked, character, state.NeedsFarmCropTemplateId);

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
    /// path resolves (current phase carries a loot-linked interaction), shared
    /// with the needs brain through <see cref="NeedsCrop"/>. Never mutates — a
    /// probe, not a transition.
    /// </summary>
    private static bool IsCropMature(Doodad doodad) => NeedsCrop.IsMature(doodad);

    /// <summary>
    /// THE SOIL READ, TRI-STATE: the injectable probe (a headless test may map a
    /// surface — always a readable answer) or the shared engine membership chain
    /// (<see cref="NeedsSoil.Probe"/> — the same rule and allowlist the needs
    /// BRAIN's planner resolves destinations with). <c>null</c> means the surface
    /// could not be read at all, which the needs brain turns into a NAMED hold
    /// rather than a fabricated "no soil here"; a probe that threw is that same
    /// unreadable surface. The engine revalidates the count cap fail-closed at
    /// dispatch.
    /// </summary>
    private bool? ProbeFarmSoil(Character character, Vector3 position)
    {
        if (FarmSoilProvider != null)
        {
            try
            {
                return FarmSoilProvider(character, position);
            }
            catch
            {
                return null;
            }
        }
        return NeedsSoil.Probe(character.ParentWorld, position, NeedsFarmSeedItemTemplateId);
    }

    /// <summary>Valid plant soil for our seed: a readable probe that answered yes (an unreadable surface is never a yes).</summary>
    private bool IsValidFarmSoil(Character character, Vector3 position)
        => ProbeFarmSoil(character, position) == true;

    /// <summary>
    /// THE CALLER'S PRE-BRAIN RULE, unchanged: nearest valid soil through the shared
    /// bounded spiral (perception rings, then the coarser discovery extension) over
    /// the same soil probe — the destination authority the needs brain's planner
    /// uses, so both paths resolve the same point from the same surface. Reached only
    /// when the brain itself did not walk that spiral this wake (an unreadable
    /// surface underfoot, or the on-soil-but-too-close-to-merchant case the planter
    /// refuses), so the search is never run twice for the same answer.
    /// </summary>
    private Vector3? ResolveSoilTarget(Character character, Vector3 from)
    {
        var (nearestMerchant, _) = ResolveNearestSeedMerchant(character, from);
        var merchantPos = nearestMerchant?.Transform.World.Position;
        var search = NeedsSoil.ResolveDestination(
            from,
            candidate => ProbeFarmSoil(character, candidate),
            merchantPos,
            (candidate, fallbackZ) => WithGroundZ(character, candidate, fallbackZ));
        return search.Destination;
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

    /// <summary>
    /// Arms the ordinary route layer toward a farm destination (soil or a
    /// mature crop): stashes an unfinished patrol once, then walks a
    /// single-leg <c>PathTo</c> through the standard MoveTo legs. Never
    /// writes the Transform — the actor tick owns movement.
    /// </summary>
    private void ArmFarmRoute(BotRoamState state, PlayerBotRuntime bot, Vector3 target, string reason)
    {
        if (state.NeedsFarmStashedRoute == null && state.Path is { IsFinished: false }
            && state.PendingMoveOwner != QuestTravelMoveOwner)
            state.NeedsFarmStashedRoute = state.Path;
        state.NeedsFarmSoilTarget = target;
        state.NeedsFarmReason = reason;
        SetRoamRoute(bot.Character, BotPath.PathTo(target));
        // Telemetry: SetRoamRoute defaults the route to ROAM — soil/crop
        // approaches own as needs-farm travel instead.
        state.PendingMoveOwner = "OTHER:needs-farm-travel";
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

        var plantPos = position;
        if (offerPlant)
        {
            var rotZ = bot.Character.Transform.Local.Rotation.Z;
            var forward = new Vector3(-MathF.Sin(rotZ), MathF.Cos(rotZ), 0f);
            var candidatePos = position + forward * 1.5f;
            candidatePos = WithGroundZ(bot.Character, candidatePos, position.Z);
            if (IsValidFarmSoil(bot.Character, candidatePos))
            {
                plantPos = candidatePos;
            }
        }

        return NeedsDecisionScenario.Run(concreteActor, new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = $"needs-farm-{bot.CharacterId}-{TimeProvider.GetUtcNow().UtcTicks}",
            HarvestDoodadObjId = cropObjId,
            PlantSeedItemTemplateId = offerPlant ? NeedsFarmSeedItemTemplateId : 0,
            PlantPosition = plantPos,
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
    /// engine's own fail-closed gate at dispatch. The rule lives once, in
    /// <see cref="NeedsCrop"/> (shared with the needs brain).
    /// </summary>
    private static bool IsNeedsFarmCrop(Doodad doodad, Character character)
        => NeedsCrop.IsOurs(doodad, character);


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
    /// Broadcasts an authoritative standstill packet to all nearby observers,
    /// stopping any residual walk/run animation on clients immediately.
    /// </summary>
    private static void BroadcastStandstill(Character character)
    {
        var moveType = BuildStopMoveType(character, character.Transform.World.Position);
        character.BroadcastPacket(new SCOneUnitMovementPacket(character.ObjId, moveType), true);
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
                if (bot.Character.IsAutoAttack)
                {
                    actor.StopAutoAttack();
                }

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
                    {
                        // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                        LogQuestTravelHaltCaller(bot, state, "other", "pvp-engage-retrack");
                        _ = actor.Stop();
                    }
                    StageMoveOwner(actor, "OTHER:pvp-engage");
                    state.PendingLeg = actor.MoveToUnit(target.ObjId, HuntChaseSpeed, TimeSpan.FromSeconds(10));
                }
            }
            else
            {
                if (actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
                {
                    // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                    LogQuestTravelHaltCaller(bot, state, "settle", "pvp-engage-range");
                    _ = actor.Stop();
                    state.PendingLeg = null;
                }

                var angle = MathUtil.CalculateAngleFrom(bot.Character.Transform.World.Position, target.Transform.World.Position);
                bot.Character.Transform.Local.SetRotationDegree(0f, 0f, (float)angle - 90);
                bot.Character.Transform.FinalizeTransform();

                if (!bot.Character.IsAutoAttack)
                {
                    actor.AutoAttack(target.ObjId);
                }

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
                // TEMPORARY live-lane diagnostic (read-only witness, never branched on).
                LogQuestTravelHaltCaller(bot, state, "other", "pvp-engage-acquire");
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
