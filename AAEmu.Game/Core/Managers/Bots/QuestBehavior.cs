using System.Globalization;
using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest behavior leg (copper-bootstrap): the WHOLE quest decision — advance
/// each active quest once, otherwise discover from the nearest in-range NPCs
/// and accept the lowest-level in-band offer — through the ONE shared
/// <see cref="BotDecisionSelector"/> (Finding D: accept is NOT split out —
/// accept proposals compete with advance/turn-in in a single Select over one
/// proposal list with deterministic cross-type priorities).
///
/// Reuses, never duplicates: <see cref="BotObservedContext"/> perceive entry,
/// <see cref="BotDecisionProposal"/> (+ hard preconditions), the shared
/// selector, <see cref="BotDecisionCycle"/> stage/dispatch/terminal bridge,
/// existing <see cref="IGameplayActor"/> verbs only, per-wake idempotency
/// keys, and the existing audit records. Objective pursuit itself (kill,
/// gather, talk) rides the existing hunt/interact branches — this behavior
/// only advances the step machine and acquires new quests.
///
/// Owned by <see cref="BotBehaviorRuntime"/> (lifecycle); hosted per-wake by
/// the roam executor's quest leg. NOT owned here: leg gating (quest.* +
/// actor-idle), CycleId minting, band/cap defaults
/// (<see cref="QuestDecisionScenario.QuestOptions"/> are constructed by the
/// caller), travel fallback (ArmQuestTravel stays navigation-side), and all
/// engine legality (range, IsDiscoverable, liveness stay fail-closed gates at
/// dispatch via TryBegin/Reject).
///
/// Decision discipline (the NeedsDecisionScenario precedent):
///   - perception rides BotObservedContext.Capture (active quest ids) plus
///     live NPC resolution for discovery targets;
///   - hard legality is evaluated BEFORE preference; range, liveness, and
///     gate checks stay the engine's own fail-closed gates at dispatch;
///   - selection is deterministic (fixed priority, personality weight 0);
///   - dispatch calls existing actor methods only — no new gameplay path.
/// </summary>
public static class QuestBehavior
{
    private static readonly Logger SweepDiagLog = LogManager.GetCurrentClassLogger();

    /// <summary>Diagnostic-only sweep enumeration cap (behavior take-3 cut is unchanged).</summary>
    private const int SweepDiagEnumCap = 256;
    /// <summary>Diagnostic-only per-field caps so log/detail strings stay bounded.</summary>
    private const int SweepDiagCandidatesShown = 8;
    private const int SweepDiagOutcomesShown = 3;

    /// <summary>
    /// G5 pursuit: fixture-scoped engagement boundary for skill-2 autoattack.
    /// Quoted from the roam <c>HuntMeleeRange</c> precedent (3.0 m,
    /// BotRoamStepExecutor): the flat distance at which the bot STOPS closing
    /// and holds. Fixture-scoped, NOT a universal combat-range rule — the
    /// engine owns real range refusal (Skill.Use TooClose/TooFar bands).
    /// </summary>
    private const float PursuitStopRadiusM = 3.0f;
    /// <summary>
    /// G5 pursuit retrack gate: a live pursuit leg is re-issued only after
    /// the committed target moved more than this since the last issue.
    /// Quoted exactly from the roam drift precedent (BotRoamStepExecutor
    /// hunt/butcher/follow legs: reissue only on &gt; 2.0 m destination
    /// drift) — per-wake re-issue discipline, never in-leg tracking.
    /// </summary>
    private const float PursuitRetrackDriftM = 2.0f;
    /// <summary>G5 pursuit leg speed (roam HuntChaseSpeed default precedent).</summary>
    private const float PursuitSpeedMps = 4.5f;
    /// <summary>G5 pursuit leg budget (roam hunt-leg 10 s precedent).</summary>
    private static readonly TimeSpan PursuitLegTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// G7c corpse-approach leg budget: the walk to a STATIC pinned corpse, so the roam
    /// point-leg precedent (60 s) rather than the pursuit's 10 s chase budget — at
    /// <see cref="PursuitSpeedMps"/> one leg crosses the engine's whole loot range
    /// (200 m) and arrives, instead of timing out mid-walk and spending the journey's
    /// repath budget on ground the actor was already covering. A corpse does not run,
    /// so there is nothing for a tighter leg budget to protect against.
    /// </summary>
    private static readonly TimeSpan LootApproachLegTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Goal key routing turn-in proposals through Dispatch.</summary>
    private const string TurnInGoal = "quest.turn-in";
    /// <summary>Goal key routing the G8b return Move/Stop/InteractNpc through Dispatch.</summary>
    private const string ReturnGoal = "quest.return";
    /// <summary>
    /// G8b return arrival radius: the InteractNpc engine gate itself
    /// (GameplayActor.MaxInteractRange, 25 m flat). Inside → audited Stop,
    /// then InteractNpc; outside → drift-gated MoveToUnit. Brain-side radius
    /// check per wake, the PursuitStopRadiusM discipline at 25 m.
    /// </summary>
    private const float ReturnInteractRadiusM = GameplayActor.MaxInteractRange;
    /// <summary>Goal key routing pursuit Move/Stop through Dispatch.</summary>
    private const string PursuitGoal = "quest.objective-pursuit";
    /// <summary>Goal key routing the G6 combat AutoAttack through Dispatch.</summary>
    private const string CombatGoal = "quest.objective-combat";
    /// <summary>Goal key routing the G7c corpse Loot through Dispatch.</summary>
    private const string LootGoal = "quest.objective-loot";
    /// <summary>Goal key routing the item-use objective's UseItem through Dispatch.</summary>
    private const string UseItemGoal = "quest.objective-use-item";
    /// <summary>Goal key routing the gather-from-doodad objective's Interact through Dispatch.</summary>
    private const string GatherGoal = "quest.objective-gather";
    /// <summary>
    /// Gather arrival radius: the Interact engine gate itself
    /// (GameplayActor.MaxInteractRange, 25 m flat). Inside → audited Stop,
    /// then Interact; outside → drift-gated MoveTo. The return leg's own
    /// radius discipline at 25 m.
    /// </summary>
    private const float GatherInteractRadiusM = GameplayActor.MaxInteractRange;
    /// <summary>
    /// The gather leg's movement-owner tag (telemetry + the leg's own live-leg
    /// reading, so the return/pursuit/loot boundaries can never disarm it and it
    /// can never disarm theirs — one actor walks one journey PER OWNING LEG).
    /// </summary>
    internal const string GatherMoveOwner = "GATHER_MOVE_TO_UNIT";
    /// <summary>
    /// The preemption detail the gather retrack stages (caller-authored: retires
    /// a leg the caller itself decided to replace, never a navigation failure).
    /// </summary>
    internal const string GatherRetrackDetail = "quest gather retrack";
    /// G6 hold-confirm gate: a pursuit Stop leg already landed for this
    /// (actor, target) at these poses stays landed — the Stop proposal
    /// withdraws while neither side moved, so the lower-priority combat
    /// proposal can win a settled wake. Any motion re-arms Stop (range-hold
    /// wins). Memory-only wake cache, same discipline as LastPursuitIssue.
    /// </summary>
    private static readonly Dictionary<uint, (uint TargetObjId, Vector3 ActorPos, Vector3 TargetPos)> LastStopHold = new();
    /// <summary>
    /// Last-issued pursuit target position per actor (memory-only wake cache
    /// for the drift gate; the G4 funnel re-selects every wake, this cache
    /// never competes with it). Keyed by ActorId; bounded by full clear.
    /// </summary>
    private static readonly Dictionary<uint, (uint TargetObjId, Vector3 TargetPos)> LastPursuitIssue = new();
    /// <summary>
    /// Last-issued G8b return reporter position per actor (memory-only wake
    /// cache for the drift gate; the reporter is re-resolved every wake, this
    /// cache never competes with it). Separate from <c>LastPursuitIssue</c> so
    /// prey-pursuit telemetry never entangles return intent. Keyed by
    /// ActorId; bounded by full clear.
    /// </summary>
    private static readonly Dictionary<uint, (uint TargetObjId, Vector3 TargetPos)> LastReturnIssue = new();
    /// <summary>
    /// Last-issued corpse-approach position per actor (memory-only wake cache for the
    /// approach leg's own drift reading, the same discipline <c>LastPursuitIssue</c>
    /// keeps). Separate from the other issue memories so a corpse approach can never
    /// entangle the prey/reporter telemetry, and vice versa. Keyed by ActorId;
    /// bounded by full clear.
    /// </summary>
    private static readonly Dictionary<uint, (uint TargetObjId, Vector3 TargetPos)> LastApproachIssue = new();
    /// <summary>
    /// Last-issued gather-doodad position per actor (memory-only wake cache for
    /// the gather leg's own drift reading, the same discipline
    /// <c>LastPursuitIssue</c> keeps). Separate from the other issue memories
    /// so a well approach can never entangle prey/reporter/corpse telemetry,
    /// and vice versa. Keyed by ActorId; bounded by full clear.
    /// </summary>
    private static readonly Dictionary<uint, (uint TargetObjId, Vector3 TargetPos)> LastGatherIssue = new();
    private static readonly object PursuitSync = new();

    /// <summary>
    /// Give-up streaks per (actor, quest): how many consecutive wakes the
    /// quest's plan failed its gate, or its objective slice resolved no target,
    /// with the NAMED reason of the latest streak. Reset the moment the quest's
    /// slice resolves or lands, so a data reload that fixes a plan clears the
    /// streak instead of counting toward the give-up. Memory-only wake cache,
    /// same discipline as <c>LastCorpse</c>/<c>LootedCorpses</c>: keyed by
    /// (actor, quest), bounded by full clear.
    /// </summary>
    private static readonly Dictionary<(uint ActorId, uint QuestId), QuestGiveUpStreak> GiveUpStreaks = new();

    /// <summary>One quest's give-up streak: the consecutive-wake count and the named reason it is counting toward.</summary>
    internal sealed record QuestGiveUpStreak(int Wakes, string Reason);

    internal static bool TryGetGiveUpStreak(uint actorId, uint questId, out QuestGiveUpStreak? streak)
    {
        lock (PursuitSync)
            return GiveUpStreaks.TryGetValue((actorId, questId), out streak);
    }

    internal static void ClearGiveUpMemory()
    {
        lock (PursuitSync)
            GiveUpStreaks.Clear();
    }

    /// <summary>
    /// Counts one failed wake for (actor, quest) and reports whether the streak
    /// has reached <paramref name="threshold"/>: the give-up verdict. A streak
    /// whose reason CHANGED restarts at one — the count must belong to one named
    /// failure, or a quest whose failure mode flips every wake would "earn" a
    /// give-up it never sustained. A threshold of 0 disables the rule (no
    /// streak is kept, no verdict is reached).
    /// </summary>
    private static bool CountGiveUpWake(uint actorId, uint questId, string reason, int threshold)
    {
        if (threshold <= 0)
            return false;
        lock (PursuitSync)
        {
            if (GiveUpStreaks.Count >= GiveUpMemoryBound)
                GiveUpStreaks.Clear();
            var wakes = GiveUpStreaks.TryGetValue((actorId, questId), out var prior) && prior.Reason == reason
                ? prior.Wakes + 1
                : 1;
            GiveUpStreaks[(actorId, questId)] = new QuestGiveUpStreak(wakes, reason);
            return wakes >= threshold;
        }
    }

    /// <summary>Clears a (actor, quest) streak: the quest's slice resolved or landed this wake.</summary>
    private static void ClearGiveUpWake(uint actorId, uint questId)
    {
        lock (PursuitSync)
            GiveUpStreaks.Remove((actorId, questId));
    }

    private static void PublishGiveUp(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, QuestGiveUp giveUp)
        => SweepDiagLog.Info(
            "QuestGiveUpDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} wakes={Wakes} threshold={Threshold} reason=[{Reason}]",
            opts.CycleId, actor.Character?.Id ?? 0, actor.ActorId, giveUp.QuestId,
            giveUp.Wakes, giveUp.Threshold, giveUp.Reason.Replace(' ', '_'));

    /// <summary>
    /// The survival-veto-clear precondition, shared by every objective leg that
    /// must stand down while a survival condition owns the actor's wake.
    ///
    /// Reads ONLY the frozen observation plus the brain's own published fact —
    /// no live world scan, no engine query, and nothing that would make the
    /// selector's evaluation non-deterministic:
    ///  - the published fact is the authority: while the combat brain is
    ///    disengaging, the veto is set and the leg is withheld;
    ///  - the observation's own vitals decide "the actor cannot act": at or below
    ///    zero hp with a readable maximum is DOWN;
    ///  - the low-hp arm is gated on evidence that a fight is on (a live
    ///    commitment, or a selected target), because a low bar out of combat is a
    ///    recovery concern, never this veto.
    ///
    /// Fail-closed in the other direction: an unreadable hp (zero maximum) never
    /// fabricates a veto.
    /// </summary>
    private static BotProposalPrecondition SurvivalVetoClear(IGameplayActor actor)
        => new("survival-veto-clear", observed => !IsSurvivalVetoed(actor.ActorId, observed));

    /// <summary>
    /// True while a survival condition owns the wake, as the SURVIVAL layer
    /// published it.
    ///
    /// The rule itself lives in exactly one place — <see cref="SurvivalVetoState"/>
    /// (fed by <see cref="SurvivalBrain.Decide"/> through
    /// <see cref="SurvivalBrainPlanner"/>) — so this precondition, the loot
    /// planner's own veto read, and the lane's recorded verdict can never be three
    /// drifting versions of the same rule. See the planner for the evaluated arms
    /// (down, the combat brain's published retreat, critical hp with fight evidence,
    /// and the named holds that veto nobody).
    ///
    /// <paramref name="observed"/> is the frozen observation the wake already holds:
    /// the veto is EVALUATED for it and published, so the fact a consumer reads on
    /// this wake describes exactly this frame — and an unreadable frame
    /// (an unknown maximum) is the named, non-vetoing hold rather than a fabricated
    /// veto.
    /// </summary>
    internal static bool IsSurvivalVetoed(uint actorObjId, BotObservedContext observed)
        => SurvivalBrainPlanner.EvaluateAndPublish(actorObjId, observed);

    /// <summary>
    /// G7b corpse recognition (observe-only): the pinned prey corpse per actor.
    /// Populated ONLY from the pinned-target dead transition (the target-dead
    /// withdrawal in <c>PursuitEmit</c>/<c>CombatEmit</c>, or the
    /// no-selection withdrawal while a quest-pinned target (<c>LastStopHold</c> /
    /// <c>LastPursuitIssue</c>) resolves dead): ObjId + TemplateId + QuestId +
    /// detecting cycle/time. Same-ObjId continuity is mandatory (engine: live
    /// Npc ObjId X stays corpse X until despawn) — a recorded objId that
    /// resolves live again is dropped as recycled. Memory-only wake cache,
    /// same discipline as <c>LastStopHold</c>/<c>LastPursuitIssue</c>: keyed by
    /// ActorId, bounded by full clear. Never dispatches, never mutates.
    /// </summary>
    internal sealed record QuestCorpseRecord(uint ObjId, uint TemplateId, uint QuestId, string CycleId, DateTimeOffset DetectedUtc);
    private static readonly Dictionary<uint, QuestCorpseRecord> LastCorpse = new();
    private const int CorpseMemoryBound = 256;
    /// <summary>Bound for the give-up streak map (one entry per actor+quest; full clear when exceeded).</summary>
    private const int GiveUpMemoryBound = 512;
    /// <summary>
    /// Records the pinned corpse for an actor. Guards (non-zero objId, the
    /// fixture row's prey template, the row's own quest id) keep irrelevant
    /// dead NPCs out — callers pass the pinned selection's live resolution,
    /// never a world scan. The questId form derives the row itself (a direct
    /// caller such as a test has no row in hand); the wake path passes the
    /// already-resolved row so the per-wake derivation is never repeated.
    /// </summary>
    internal static void NotePinnedCorpse(uint actorId, uint objId, uint templateId, uint questId, string cycleId)
        => NotePinnedCorpse(actorId, objId, templateId, QuestFixtureRow.FromQuestData(questId), cycleId);

    private static void NotePinnedCorpse(uint actorId, uint objId, uint templateId, QuestFixtureRow fixture, string cycleId)
    {
        if (objId == 0 || templateId != fixture.PreyTemplate)
            return;
        lock (PursuitSync)
        {
            if (LastCorpse.Count >= CorpseMemoryBound)
                LastCorpse.Clear();
            // The PIN is this leg's journey identity: a DIFFERENT corpse is another
            // journey, so the corpse approach armed for the previous pin ends with it
            // (a banked terminal must not outlive the corpse it was reached on, and a
            // fresh corpse must never inherit the old one's spent repath budget), while
            // a same-ObjId re-pin keeps that journey's counters. Owner-scoped, so no
            // other leg's journey on this actor is dropped.
            if (LastCorpse.TryGetValue(actorId, out var prior) && prior.ObjId != objId)
                LootTravelDispatch.EndJourneyForPin(actorId);
            LastCorpse[actorId] = new QuestCorpseRecord(objId, templateId, fixture.QuestId, cycleId ?? "-", DateTimeOffset.UtcNow);
        }
    }
    internal static bool TryGetCorpse(uint actorId, out QuestCorpseRecord? record)
    {
        lock (PursuitSync)
            return LastCorpse.TryGetValue(actorId, out record);
    }
    internal static void ClearCorpseMemory()
    {
        lock (PursuitSync)
            LastCorpse.Clear();
    }
    /// <summary>
    /// G7c loot-once memory: (actor, corpse ObjId) pairs whose Loot verb has
    /// already run its terminal course. The proposal withholds any recorded
    /// corpse present here, so the SAME corpse never re-dispatches across
    /// re-ticks — even if its container refills. Keyed stable per corpse
    /// (never per wake); the dispatch idempotency key mirrors it. Memory-only
    /// wake cache, same discipline as <c>LastCorpse</c>: keyed by ActorId,
    /// bounded by full clear. Records dispatches only, never policy.
    /// </summary>
    private static readonly HashSet<(uint ActorId, uint ObjId)> LootedCorpses = new();
    internal static bool IsLootDispatched(uint actorId, uint objId)
    {
        lock (PursuitSync)
            return LootedCorpses.Contains((actorId, objId));
    }
    internal static void ClearLootMemory()
    {
        lock (PursuitSync)
            LootedCorpses.Clear();
    }

    /// <summary>Test-only reset for the gather leg's issue memory (same discipline as the sibling clears).</summary>
    internal static void ClearGatherMemory()
    {
        lock (PursuitSync)
            LastGatherIssue.Clear();
    }
    /// <summary>
    /// Read-only loot-container probe for a resolved corpse: containerExists +
    /// item count + the authoritative lootable predicate reusing
    /// <c>GameplayActor.Loot</c>'s own legality gates (owner resolves, flat
    /// distance within <c>LootingContainer.MaxLootingRange</c>, non-empty
    /// container) as reads only — never opening, mutating, or transferring.
    /// Policy stays in <c>Loot</c>; this mirrors its preconditions.
    /// </summary>
    internal static (bool ContainerExists, int ItemCount, bool Lootable) ProbeCorpse(Character? character, Npc? npc)
    {
        if (character?.ParentWorld == null || npc == null)
            return (false, 0, false);
        var owner = character.ParentWorld.GetBaseUnit(npc.ObjId);
        if (owner == null)
            return (false, 0, false);
        var container = owner.LootingContainer;
        if (container == null)
            return (false, 0, false);
        var count = container.Items.Count;
        var inRange = MathUtil.CalculateDistance(
            character.Transform.World.Position, owner.Transform.World.Position, false)
            <= Models.Game.Items.Containers.LootingContainer.MaxLootingRange;
        return (true, count, count > 0 && inRange);
    }
    /// <summary>
    /// Pure diag fragment for a recognized corpse
    /// (<c>:corpse={objId}:container={n}:lootable={true|false}</c>) or
    /// <c>:corpse={objId}:gone=true</c> once despawned. Carries no spaces or
    /// brackets so the funnel log and DECIDE detail stay greppable and the
    /// test-side funnel parser keeps parsing byte-identical.
    /// </summary>
    internal static string FormatCorpseFragment(uint objId, int itemCount, bool lootable)
        => $":corpse={objId}:container={itemCount}:lootable={(lootable ? "true" : "false")}";
    /// <summary>
    /// G7b recognition arm 1 (direct): the pinned selection resolves dead in a
    /// target-dead withdrawal — questId/objective-context (funnel), target
    /// objId/template/dead-state (live npc) are all known here, so no second
    /// world scan runs. Records the corpse (fixture-row prey-template guard
    /// inside) and returns the read-only probe fragment, or "" when the dead
    /// unit is not our prey (irrelevant-dead-NPC ignored). Dispatches nothing.
    /// </summary>
    private static string NoteDeadSelection(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts,
        QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel, Npc npc)
    {
        if (npc.Hp > 0 || npc.TemplateId != funnel.Fixture.PreyTemplate)
            return "";
        NotePinnedCorpse(actor.ActorId, npc.ObjId, npc.TemplateId, funnel.Fixture, opts.CycleId);
        var probe = ProbeCorpse(actor.Character, npc);
        return FormatCorpseFragment(npc.ObjId, probe.ItemCount, probe.Lootable);
    }
    /// <summary>
    /// G7b recognition arm 2 (pin-memory): the funnel drops dead candidates,
    /// so post-death wakes usually withdraw with no-selection — the committed
    /// selection is then only reachable through the quest-pinned memories
    /// (<c>LastStopHold</c> / <c>LastPursuitIssue</c>, both fixture-quest-owned
    /// by construction) or the recorded corpse itself. Resolves ONLY that pinned
    /// objId (a direct resolve, never a scan): dead prey → record + probe
    /// fragment; recorded objId resolving live → drop as recycled; recorded
    /// objId gone from the world → gone fragment (no container claim).
    /// Death-wake re-pin: a newly dead quest-relevant pinned target shadows
    /// any stale record — the fresh corpse is pinned (old released) so
    /// <c>LootEmit</c> evaluates the fresh container. Same-ObjId pins
    /// keep continuity through the record path; irrelevant dead units never
    /// re-pin. Dispatches nothing.
    /// </summary>
    private static string ObservePinnedCorpse(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts,
        QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel)
    {
        var character = actor.Character;
        uint pin = 0;
        QuestCorpseRecord? stale = null;
        lock (PursuitSync)
        {
            LastCorpse.TryGetValue(actor.ActorId, out stale);
            if (LastStopHold.TryGetValue(actor.ActorId, out var hold))
                pin = hold.TargetObjId;
            else if (LastPursuitIssue.TryGetValue(actor.ActorId, out var last))
                pin = last.TargetObjId;
        }
        if (pin != 0 && (stale == null || pin != stale.ObjId))
        {
            var fresh = character?.ParentWorld?.GetNpc(pin);
            if (fresh != null && fresh.Hp <= 0 && fresh.TemplateId == funnel.Fixture.PreyTemplate)
            {
                NotePinnedCorpse(actor.ActorId, fresh.ObjId, fresh.TemplateId, funnel.Fixture, opts.CycleId);
                var freshProbe = ProbeCorpse(character, fresh);
                return FormatCorpseFragment(fresh.ObjId, freshProbe.ItemCount, freshProbe.Lootable);
            }
        }
        if (stale != null)
        {
            var cur = character?.ParentWorld?.GetNpc(stale.ObjId);
            if (cur != null && cur.Hp > 0)
            {
                lock (PursuitSync)
                    LastCorpse.Remove(actor.ActorId);
                // The recorded objId resolves LIVE: the corpse this leg could have been
                // walking to is not our corpse any more, so the corpse approach ends with
                // the record (owner-scoped — no other leg's journey is touched). Without
                // it a recycled corpse would leave an armed journey behind that a later
                // re-pin of the same objId could inherit.
                LootTravelDispatch.EndJourneyForPin(actor.ActorId);
            }
            else if (cur == null)
            {
                return $":corpse={stale.ObjId}:gone=true";
            }
            else
            {
                var probe = ProbeCorpse(character, cur);
                return FormatCorpseFragment(stale.ObjId, probe.ItemCount, probe.Lootable);
            }
        }
        if (pin == 0)
            return "";
        var npc = character?.ParentWorld?.GetNpc(pin);
        if (npc == null || npc.Hp > 0 || npc.TemplateId != funnel.Fixture.PreyTemplate)
            return "";
        NotePinnedCorpse(actor.ActorId, npc.ObjId, npc.TemplateId, funnel.Fixture, opts.CycleId);
        var probeNow = ProbeCorpse(character, npc);
        return FormatCorpseFragment(npc.ObjId, probeNow.ItemCount, probeNow.Lootable);
    }
    /// <summary>Diagnostic-only raw sweep objId census bound (objId:templateId:flatM, no spaces).</summary>
    private const int SweepDiagRawIdsShown = 12;
    /// <summary>
    /// Runs the wake's plan set through the leg loop and the decision
    /// pipeline: <see cref="QuestDirector.Run"/> perceives once and assembles
    /// the plans, then hands them here with that same snapshot.
    /// </summary>
    internal static QuestDecisionScenario.QuestRunResult Run(
        IGameplayActor actor,
        QuestDecisionScenario.QuestOptions opts,
        BotObservedContext context,
        IReadOnlyList<QuestPlan> plans,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plans);

        try
        {
            // ---------------------------------------------------- 1. PERCEIVE
            var before = context.ActiveQuestIds.ToHashSet();

            // G4 objective→target (additive): per-active-quest proposal path
            // for the wired quest's first progress step. Evaluated once per wake from
            // the perceive snapshot; a selection competes as a Target proposal
            // dispatched through GameplayActor.SetTarget only. Discovery,
            // accept, advance, and turn-in below are untouched (G3 frozen).
            var proposals = new List<BotDecisionProposal>();
            QuestObjectiveTargetSelector.ObjectiveTargetFunnel? objectiveFunnel = null;
            QuestFixtureRow? objectiveFixture = null;
            var combatHpBefore = -1;
            // Stage 4 failed-plan evidence: a plan that failed its fixture gate is
            // named at run-result level (never a silent skip) and in the lane log,
            // whether or not the wake found other work.
            var planFailures = new List<QuestPlanFailure>();
            // Give-up verdicts this wake, named with the streak that earned them.
            var giveUps = new List<QuestGiveUp>();
            // Per-wake evidence rows in plan/leg evaluation order: skipped legs
            // carry their enter gate's named reason, entered legs their own diag.
            var legEvidence = new List<LegEvidence>();
            foreach (var plan in plans)
            {
                if (plan.HasFailed)
                {
                    var failure = new QuestPlanFailure(plan.QuestId, plan.FailStage, plan.FailReason);
                    planFailures.Add(failure);
                    LogPlanFailure(actor, opts, failure);
                    // Give-up rule, branch 1: a plan that fails its gate the same
                    // way for N consecutive wakes is not retried any more — and
                    // the give-up NAMES the plan's own reason verbatim, so the
                    // decision is never silent and never invented.
                    if (CountGiveUpWake(actor.ActorId, plan.QuestId, failure.Reason, opts.GiveUpAfterPlanFailures))
                    {
                        var giveUp = new QuestGiveUp(plan.QuestId, failure.Reason,
                            opts.GiveUpAfterPlanFailures, opts.GiveUpAfterPlanFailures);
                        giveUps.Add(giveUp);
                        PublishGiveUp(actor, opts, giveUp);
                    }
                    continue;
                }
                // An objective shape this vocabulary cannot serve yet keeps its
                // bootstrap floor; the gap is logged (never silent) and gives up
                // on the same rule, naming the act it could not serve. A plan
                // that neither failed nor is unserved clears its plan-failure
                // streak — EXCEPT when it carries an objective slice, whose own
                // resolution decides (branch 2 below owns that clear, so a plan
                // whose slice is unresolved keeps its count).
                if (plan.Unserved.Length != 0)
                {
                    LogUnservedPlan(actor, opts, plan);
                    if (CountGiveUpWake(actor.ActorId, plan.QuestId, plan.Unserved, opts.GiveUpAfterPlanFailures))
                    {
                        var giveUp = new QuestGiveUp(plan.QuestId, plan.Unserved,
                            opts.GiveUpAfterPlanFailures, opts.GiveUpAfterPlanFailures);
                        giveUps.Add(giveUp);
                        PublishGiveUp(actor, opts, giveUp);
                    }
                }
                else if (!HasObjectiveSlice(plan))
                {
                    ClearGiveUpWake(actor.ActorId, plan.QuestId);
                }
                var questId = plan.QuestId;
                var fixture = plan.Fixture;
                // Stage 4: one plan per quest per wake (assembled by
                // QuestDirector.PlanWake from the wake's single perception),
                // driven here as a generic leg loop. The plan's declaration
                // order IS the evaluation order, so each leg's Emit runs in the
                // pre-factoring order and the proposal list is assembled
                // exactly as the inline arms did, no new arm and no new goal.
                // The Advance leg's enter gate owns the "already-Ready quest has
                // no advance work" rule (RunCurrentStep from Ready is a no-op
                // that still reports Completed, which the leg would count as
                // landed work — starving the route layer so the bot never walks
                // to an out-of-range reporter: observed live, 60 "advances" on a
                // Ready quest, 0 turn-ins, bot never moved).
                var legContext = new QuestLegContext(actor, opts, questId, fixture, null)
                {
                    // The wake's own perception (captured once by QuestDirector.Run
                    // before the plans were built) — never a second one.
                    Observation = context
                };
                var wake = new QuestLegWake();
                QuestObjectiveTargetSelector.ObjectiveTargetFunnel? planFunnel = null;
                foreach (var leg in plan.Legs)
                {
                    // NeedsFunnel marks the objective group: the plan's shared
                    // funnel is evaluated exactly once per wake, immediately
                    // before the first leg that reads it (the plan never stores
                    // it, and the run result keeps the first one for the DECIDE
                    // bracket and the combat/loot outcome lines).
                    if (leg.NeedsFunnel && planFunnel == null)
                    {
                        planFunnel = QuestObjectiveTargetSelector.Evaluate(
                            actor.Character, context, opts.CycleId, fixture!);
                        objectiveFixture ??= fixture;
                        objectiveFunnel ??= planFunnel;
                        legContext = legContext with { Funnel = planFunnel };
                    }
                    // The leg's own entry gate decides whether it runs this wake
                    // and, when it does not, names why — the loop never invents a
                    // skip reason. A null gate enters unconditionally.
                    var skip = leg.Enter?.Invoke(legContext, wake);
                    if (skip != null)
                    {
                        wake.RecordSkip(questId, leg.Id, skip);
                        continue;
                    }
                    var diag = QuestLegWake.NoProposal;
                    var hpBefore = wake.HpBefore;
                    var proposal = leg.Emit(legContext, ref diag, ref hpBefore);
                    wake.HpBefore = hpBefore;
                    // The leg's convergence check (only Return has one) reads the
                    // proposal it just emitted; its verdict is wake state TurnIn's
                    // gate reads, never a reason the loop invents.
                    if (proposal != null && leg.Exit != null)
                        wake.ReturnConverged = leg.Exit(actor, proposal);
                    wake.Record(questId, leg.Id, proposal, diag);
                    if (leg.Id == QuestLegId.Combat)
                        combatHpBefore = wake.HpBefore;
                    if (proposal != null)
                        proposals.Add(proposal);
                }
                legEvidence.AddRange(wake.Evidence);
                if (planFunnel != null)
                {
                    LogObjectiveFunnel(actor, opts, planFunnel, wake);
                    // Give-up rule, branch 2: a plan that RUNS but whose objective
                    // slice resolves no target for N consecutive wakes is given up
                    // too — the target the quest needs is not reachable in this
                    // world, and the reason is the slice's own named detail
                    // (objective/source/selection), never a generic phrase. A
                    // resolved slice clears the streak immediately.
                    var unresolved = UnresolvedTargetReason(questId, planFunnel);
                    if (unresolved.Length == 0)
                    {
                        ClearGiveUpWake(actor.ActorId, questId);
                    }
                    else if (CountGiveUpWake(actor.ActorId, questId, unresolved, opts.GiveUpAfterUnresolvedWakes))
                    {
                        var giveUp = new QuestGiveUp(questId, unresolved,
                            opts.GiveUpAfterUnresolvedWakes, opts.GiveUpAfterUnresolvedWakes);
                        giveUps.Add(giveUp);
                        PublishGiveUp(actor, opts, giveUp);
                    }
                }
            }
            // Discovery needs live NPC targets: nearest in-range NPCs only
            // (range itself stays the engine gate at dispatch).
            // Nearest-first flat-distance order before the MaxDiscoverTargets
            // take cut (raw diagnostics above stay enumeration order).
            var discoverTargets = new List<uint>();
            // ---- sweep diagnostics (additive, bounded; behavior unchanged) ----
            // rawCount = full provider enumeration (capped); rawShown =
            // bounded per-candidate table (objId:templateId:flatM:3dM) in
            // enumeration order; the behavior take-3 cut below is untouched.
            var rawCount = 0;
            var enumCapped = false;
            var rawShown = new List<string>();
            // Raw sweep objId census (diagnostic-only, additive): first-N
            // objId:templateId:flatM in enumeration order. No spaces so the
            // DECIDE bracket keeps parsing byte-identical (parser ignores it).
            var rawIds = new List<string>();
            // carries one. UNAVAILABLE/NULL tokens, never an exception.
            var actorPos = actor.Character?.Transform?.World.Position;
            var actorPosText = actorPos.HasValue
                ? $"({actorPos.Value.X:F1},{actorPos.Value.Y:F1},{actorPos.Value.Z:F1})"
                : "UNAVAILABLE";
            var actorZone = actor.Character?.Transform?.ZoneId.ToString() ?? "UNAVAILABLE";
            var actorRegion = actor.Character?.Region != null ? actor.Character.Region.Id.ToString() : "NULL";
            if (nearbyNpcs != null)
            {
                var position = context.Position;
                var candidates = new List<Npc>();
                foreach (var npc in nearbyNpcs(actor.Character, GameplayActor.MaxQuestDiscoverRange))
                {
                    if (npc == null)
                        continue;
                    if (rawCount >= SweepDiagEnumCap)
                    {
                        enumCapped = true;
                        break;
                    }
                    rawCount++;
                    if (rawShown.Count < SweepDiagCandidatesShown)
                    {
                        var npcPos = npc.Transform.World.Position;
                        var shown = !actorPos.HasValue
                            ? $"{npc.ObjId}:{npc.TemplateId}:NA:NA"
                            : $"{npc.ObjId}:{npc.TemplateId}:{MathUtil.CalculateDistance(actorPos.Value, npcPos, false):F1}:{MathUtil.CalculateDistance(actorPos.Value, npcPos, true):F1}";
                        rawShown.Add(shown);
                    }
                    if (rawIds.Count < SweepDiagRawIdsShown)
                    {
                        var npcPos2 = npc.Transform.World.Position;
                        rawIds.Add(!actorPos.HasValue
                            ? $"{npc.ObjId}:{npc.TemplateId}:NA"
                            : $"{npc.ObjId}:{npc.TemplateId}:{MathUtil.CalculateDistance(actorPos.Value, npcPos2, false):F1}");
                    }
                    candidates.Add(npc);
                }
                // Ordering-only: nearest-first (flat distance from actor)
                // before the take-3 cut. Cap, band/active filters, selector,
                // dispatch, and legality gates are unchanged.
                IEnumerable<Npc> ordered = candidates;
                if (actorPos.HasValue)
                    ordered = candidates
                        .OrderBy(n => MathUtil.CalculateDistance(actorPos.Value, n.Transform.World.Position, false))
                        .ThenBy(n => n.ObjId);
                foreach (var npc in ordered)
                {
                    discoverTargets.Add(npc.ObjId);
                    if (discoverTargets.Count >= opts.MaxDiscoverTargets)
                        break;
                }
            }

            // Discover first (read-like, non-mutating), then offer accepts.
            var offerings = new List<(uint TargetObjId, QuestOffering Offering)>();
            var inBand = 0; // funnel tally only: band-passing offers seen (no behavior use)
            var outcomeShown = new List<string>(); // bounded per-target discover outcomes
            foreach (var targetObjId in discoverTargets)
            {
                var discover = actor.DiscoverQuests(targetObjId,
                    idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:discover:{targetObjId}");
                if (outcomeShown.Count < SweepDiagOutcomesShown)
                    outcomeShown.Add(DescribeDiscoverOutcome(discover));
                if (discover is not { IsTerminal: true, State: ActorLifecycleState.Completed })
                    continue;
                if (discover.Result is not QuestDiscoveryResult result)
                    continue;
                foreach (var offering in result.Offerings)
                {
                    if (offering.Level < opts.BandMin || offering.Level > opts.BandMax)
                        continue;
                    inBand++;
                    if (before.Contains(offering.QuestId))
                        continue;
                    offerings.Add((targetObjId, offering));
                }
            }
            // One bounded Info line per quest wake so a lane log joins the sweep
            // end to end (raw candidates with distances, truncation, per-target
            // outcomes, actor pose). No behavior input; Info (not Debug) so the
            // lane file target records it.
            SweepDiagLog.Info(
                "QuestSweepDiag cycle={Cycle} char={CharId} actor={Actor} zone={Zone} region={Region} " +
                "raw={Raw}{Capped} eval=[{Eval}] trunc={Trunc} outcomes=[{Outcomes}] offers={Offers} inBand={InBand}",
                opts.CycleId, actor.ActorId,
                actorPosText, actorZone, actorRegion,
                rawCount, enumCapped ? "+" : "", string.Join(",", rawShown),
                Math.Max(0, rawCount - discoverTargets.Count),
                string.Join(";", outcomeShown),
                offerings.Count, inBand);
            // Offerings are RANKED before they are proposed, and the bound is
            // applied after ranking — so a wake with more in-band offers than
            // the bound drops its WORST offers, and the shared selector never
            // sees a candidate list past its own ceiling (which would make it
            // decide nothing at all). Each offering's quest row is derived ONCE
            // here and reused by both the ranking cut and the emitted proposal,
            // so the two never disagree about what the quest offers. The order is
            // the selector's own key order (priority, weight, tie-break), so the
            // bound's cut and the selector's pick agree on what "best" means.
            var ranked = offerings
                .Select(o => (o.TargetObjId, o.Offering, Row: QuestFixtureRow.FromQuestData(o.Offering.QuestId)))
                .OrderByDescending(o => AcceptRank(opts, o.Offering, o.Row).Priority)
                .ThenByDescending(o => AcceptRank(opts, o.Offering, o.Row).Weight)
                .ThenBy(o => o.Offering.QuestId)
                .ToList();
            var proposed = 0;
            foreach (var (targetObjId, offering, row) in ranked)
            {
                if (opts.MaxAcceptProposals > 0 && proposed >= opts.MaxAcceptProposals)
                    break;
                proposals.Add(AcceptProposal(actor, opts, targetObjId, offering, row));
                proposed++;
            }

            var decideContext = BotObservedContext.Capture(actor);
            var decision = BotDecisionSelector.Select(decideContext, proposals);
            if (!decision.HasProposal)
            {
                // Funnel tallies (counts only, feeds bridge lastDetail): where the
                // quest pipeline ran dry — sweep-empty vs no-offerings vs all-filtered.
                // Appended sweep keys (raw/trunc/actor/zone/region/outcomes/rawIds)
                // ride inside the same bracket for the lane log; the BotQuestFunnel
                // parser ignores unknown keys, and values carry no spaces so the
                // known keys keep parsing byte-identical. rawIds sits right after
                // targets (early) so it survives the 600-char QuestDecideDetail cut.
                var firstZero = discoverTargets.Count == 0 ? "sweep-empty" : offerings.Count == 0 ? "no-offerings" : "all-filtered";
                var rawIdsValue = rawIds.Count == 0 ? "-" : string.Join(",", rawIds);
                // G7b (observe-only): corpse recognition rides the DECIDE
                // bracket right after rawIds (early — survives the 600-char
                // QuestDecideDetail cut) so the observe payload names OUR
                // corpse quest-side. Parser-safe keys (parser ignores unknown
                // keys); zero behavior input, zero dispatch.
                var corpseKeys = objectiveFunnel == null ? "" : ObservePinnedCorpse(actor, opts, objectiveFunnel).Replace(":", " ");
                var sweepKeys =
                    $" raw={rawCount} trunc={Math.Max(0, rawCount - discoverTargets.Count)}" +
                    $" actor={actorPosText} zone={actorZone} region={actorRegion}" +
                    $" outcomes={TruncateSweep(string.Join(";", outcomeShown), 300)}";
                // G4 funnel keys (additive, parser-safe): the 251
                // objective→target tallies ride the same DECIDE bracket so a
                // no-selection wake names its earliest failing predicate.
                var objectiveKeys = objectiveFunnel == null ? "" :
                    $" q251tgt_raw={objectiveFunnel.RawCount} q251tgt_rel={objectiveFunnel.RelevantCount}" +
                    $" q251tgt_legal={objectiveFunnel.LegalCount}" +
                    $" q251tgt_selected={(objectiveFunnel.SelectedObjId == 0 ? "-" : objectiveFunnel.SelectedObjId.ToString(CultureInfo.InvariantCulture))}" +
                    $" q251tgt_objective={(objectiveFunnel.ObjectiveResolved ? "ok" : "FAIL-OBJECTIVE")}" +
                    $" q251tgt_source={(objectiveFunnel.SourceResolved ? "ok" : "FAIL-SOURCE")}" +
                    $" q251tgt_relevance={(objectiveFunnel.Relevance ? "true" : "false")}";
                // Stage 4: a wake whose only quest failed its fixture gate
                // reports FIXTURE, never a bare sweep miss — the plan's own
                // reason leads, and the bracket stays byte-identical behind it
                // so the BotQuestFunnel parser keeps parsing.
                var decideDetail = $"no legal quest proposal: {decision.Explanation} [swept={discoverTargets.Count} targets={string.Join(",", discoverTargets)} rawIds={rawIdsValue}{corpseKeys} offerings={offerings.Count} inBand={inBand} legal={proposals.Count} firstZero={firstZero}{sweepKeys}{objectiveKeys}]";
                var failStage = "DECIDE";
                if (planFailures.Count > 0)
                {
                    failStage = "FIXTURE";
                    decideDetail = $"plan failed: {string.Join(" ; ", planFailures.Select(f => f.Reason))} | {decideDetail}";
                }
                return Fail(failStage, ActorFailureReason.WrongDecision, decideDetail,
                    actor, null, decision.Rejections, null, legEvidence, planFailures, giveUps);
            }

            // ---------------------------------------------------- 3. EXECUTE
            var selected = decision.Proposal!;
            // E2E-ONLY turn-in withhold (default-off): the leg ran whole —
            // perception, eligibility, and selection above are untouched — but
            // a selected turn-in is NOT dispatched. SelectedAction stays set
            // (the proposal is observable in diagnostics) while Request stays
            // null (nothing landed) and the quest stays Ready/active.
            if (opts.WithholdTurnIn && selected.Goal == TurnInGoal)
            {
                var reporterObjId = selected.Payload is QuestTurnInParams turnInParams ? turnInParams.TargetObjId : 0u;
                return Fail("WITHHELD", ActorFailureReason.WrongDecision,
                    $"withheld turn-in {selected.Action} quest {selected.TargetId} reporter={reporterObjId} (test-only withhold; proposal observable, dispatch skipped): {decision.Explanation}",
                    actor, selected.Action, decision.Rejections);
            }
            actor.SetPendingDecision(selected.Goal, opts.PolicyVersion,
                decision.Rejections.Count + 1, decision.Rejections.Count, opts.CycleId);
            // Part-3 join key: the per-wake CycleId (StepQuestLeg mints
            // quest-{characterId}-{ticks}) travels request → audit record so
            // a test can join this wake to its terminal audit row.
            actor.SetPendingCycleId(opts.CycleId);
            var execution = BotDecisionCycle.Execute(actor, decideContext, selected,
                static (gameplayActor, proposal) => Dispatch(gameplayActor, proposal));
            var request = execution.Request;
            if (selected.Goal == CombatGoal && objectiveFixture != null)
                LogCombatOutcome(actor, opts, selected, request, combatHpBefore, objectiveFixture);
            if (selected.Goal == LootGoal && objectiveFixture != null)
                LogLootOutcome(actor, opts, selected, request, objectiveFixture);
            if (!request.IsTerminal)
            {
                // G5 pursuit: a dispatched-but-running Move leg IS the landed
                // work — closing 10-20 m takes many wakes, and the quest leg
                // only runs while the actor is idle, so a Running leg means
                // the NEXT wakes skip this leg until the move terminates.
                // Returning landed keeps QuestLegActive (the quest-blind
                // hunt/butcher loops stay suppressed while closing) and keeps
                // the travel fallback from arming against our own leg. Every
                // pre-existing action keeps the terminal-surface contract.
                if (selected.Goal is PursuitGoal or ReturnGoal or LootGoal or GatherGoal && selected.Action == ActorActionType.Move
                    && request.State == ActorLifecycleState.Running)
                {
                    var afterRunning = BotObservedContext.Capture(actor).ActiveQuestIds.ToHashSet();
                    var completedRunning = before.Where(q => !afterRunning.Contains(q)).ToList();
                    var legKind = selected.Goal switch
                    {
                        ReturnGoal => "return",
                        LootGoal => "loot approach",
                        GatherGoal => "gather",
                        _ => "pursuit"
                    };
                    return new QuestDecisionScenario.QuestRunResult
                    {
                        Scenario = QuestDecisionScenario.ScenarioName,
                        WorkSelected = true,
                        SelectedAction = selected.Action,
                        Request = request,
                        Rejections = decision.Rejections,
                        Explanation = decision.Explanation + $" [{legKind} leg running toward {selected.TargetId}]",
                        CompletedQuestIds = completedRunning,
                        LegEvidence = legEvidence,
                        PlanFailures = planFailures,
                        GiveUps = giveUps,
                        TraceRecords = [.. actor.AuditTrace]
                    };
                }
                return Fail("EXECUTE", ActorFailureReason.Starvation,
                    $"{request.Action} left the terminal surface",
                    actor, selected.Action, decision.Rejections, request, legEvidence, planFailures, giveUps);
            }

            var after = BotObservedContext.Capture(actor).ActiveQuestIds.ToHashSet();
            var completed = before.Where(q => !after.Contains(q)).ToList();
            // A completed quest leaves the plan set, so its streak is gone with
            // it — cleared explicitly so the map never holds a stale quest.
            foreach (var completedId in completed)
                ClearGiveUpWake(actor.ActorId, completedId);
            return new QuestDecisionScenario.QuestRunResult
            {
                Scenario = QuestDecisionScenario.ScenarioName,
                WorkSelected = true,
                SelectedAction = selected.Action,
                Request = request,
                Rejections = decision.Rejections,
                Explanation = decision.Explanation,
                CompletedQuestIds = completed,
                LegEvidence = legEvidence,
                PlanFailures = planFailures,
                GiveUps = giveUps,
                TraceRecords = [.. actor.AuditTrace]
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor, null, []);
        }
    }

    /// <summary>
    /// Item-use leg entry gate: the step machine has work only for an active,
    /// non-Ready quest (a Ready quest's only legal work is turn-in), and the
    /// objective leg has work only while the item-use objective is not yet
    /// credited. The gate owns both reasons, so the loop's evidence names the
    /// real cause of the withdraw.
    /// </summary>
    internal static string? UseItemEnter(QuestLegContext context, QuestLegWake wake)
    {
        var quest = context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        if (quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
            return "quest-not-usable";
        var fixture = context.Fixture!;
        return ItemUseCredited(context.QuestId, quest, fixture)
            ? "objective-credited"
            : null;
    }

    /// <summary>
    /// True when the quest's item-use objective already carries its required
    /// count. Reads the act's live objective counter off the SAME quest object
    /// the leg already holds (<c>QuestActTemplate.GetObjective</c>), resolving
    /// the act from the static template tables exactly as the TurnIn leg
    /// resolves its report acts — game data, never a world/perception read.
    /// Fail-closed: an unreadable template or a missing act reads NOT credited,
    /// so the leg keeps working rather than silently declaring victory.
    /// </summary>
    private static bool ItemUseCredited(uint questId, Quest quest, QuestFixtureRow fixture)
    {
        var use = QuestManager.Instance?.GetTemplate(questId)?
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemUse>()
            .FirstOrDefault(a => a.ActId == fixture.UseItemActId);
        if (use == null)
            return false;
        return use.GetObjective(quest) >= Math.Max(1, use.Count);
    }

    /// <summary>
    /// True when the quest's gather objective already carries its required
    /// count. The item-use credit helper's twin for the gather act: reads the
    /// act's live objective counter off the SAME quest object the leg already
    /// holds, resolving the <c>QuestActObjItemGather</c> from the static
    /// template tables — game data, never a world/perception read.
    /// Fail-closed: an unreadable template or a missing act reads NOT credited,
    /// so the leg keeps working rather than silently declaring victory.
    /// </summary>
    private static bool GatherCredited(uint questId, Quest quest, QuestFixtureRow fixture)
    {
        var gather = QuestManager.Instance?.GetTemplate(questId)?
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .FirstOrDefault(a => a.ActId == fixture.GatherActId);
        if (gather == null)
            return false;
        return gather.GetObjective(quest) >= Math.Max(1, gather.Count);
    }
    /// <summary>
    /// Gather-from-doodad leg entry gate: the step machine has work only for an
    /// active, non-Ready quest, the objective leg has work only while the gather
    /// objective is not yet credited, and the leg needs a source — a perceived
    /// doodad of the row's own <c>GatherDoodadTemplate</c> — to walk to. The
    /// gate owns all three reasons, so the loop's evidence names the real cause
    /// of the withdraw. Never dispatches; the resolve is a read-only census of
    /// the wake's own perception snapshot.
    internal static string? GatherEnter(QuestLegContext context, QuestLegWake wake)
    {
        var quest = context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        if (quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
            return "quest-not-usable";
        var fixture = context.Fixture!;
        if (GatherCredited(context.QuestId, quest, fixture))
            return "objective-credited";
        return ResolveGatherDoodad(context, fixture) == 0 ? "gather-no-source" : null;
    }

    /// <summary>
    /// The wake's gather source: the first perceived doodad objId whose template
    /// is the row's own <c>GatherDoodadTemplate</c> (perception order — the same
    /// nearest-first discipline the discovery sweep uses is left to the wake's
    /// snapshot order; the leg never re-sorts). 0 when the row names no template
    /// or none is perceived. Read-only over the wake's own snapshot — never a
    /// world scan, never stored.
    /// </summary>
    private static uint ResolveGatherDoodad(QuestLegContext context, QuestFixtureRow fixture)
    {
        if (fixture.GatherDoodadTemplate == 0)
            return 0;
        var observed = context.Observation?.NearbyDoodadObjIds;
        if (observed == null || observed.Count == 0)
            return 0;
        var world = context.Actor.Character?.ParentWorld;
        if (world == null)
            return 0;
        foreach (var objId in observed)
        {
            var doodad = world.GetDoodad(objId);
            if (doodad != null && doodad.TemplateId == fixture.GatherDoodadTemplate)
                return objId;
        }
        return 0;
    }

    /// <summary>
    /// Gather-from-doodad leg: draw the objective item from the perceived well
    /// through the real <c>Interact</c> contract with the row's own
    /// <c>GatherUseSkill</c> (read off the well's func tables — e.g. the
    /// well's fake-use row carries the draw skill; a skill-0 row stays a
    /// skill-less <c>Doodad.Use</c>; no new actor verb). Per wake:
    /// source lost → withdraw; outside the 25 m Interact gate → Move (drift-gated
    /// retrack only after &gt; 2.0 m target motion since the last issue); inside
    /// unsettled → Stop (audited halt); settled → Interact. Credit is the
    /// engine's own objective counter plus the wake's bag census (never a world
    /// re-scan). Never advances, never turns in — those legs stay separate
    /// competitors.
    /// </summary>
    internal static BotDecisionProposal? GatherEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var fixture = context.Fixture!;
        var questId = context.QuestId;
        static string M(double value)
            => double.IsNaN(value) ? "NA" : value.ToString("F1", CultureInfo.InvariantCulture);
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
        {
            diag = $"validate=quest-not-active:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have=NA:rangeM=NA:dispatch=withdrawn:reason=quest-not-active";
            return null;
        }
        var have = 0;
        if (context.Observation?.BagItemCounts.TryGetValue(fixture.PreyItem, out var held) == true)
            have = held;
        if (GatherCredited(questId, quest, fixture))
        {
            diag = $"validate=objective-credited:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:rangeM=NA:dispatch=withdrawn:reason=objective-credited";
            return null;
        }
        var selected = ResolveGatherDoodad(context, fixture);
        if (selected == 0)
        {
            diag = $"validate=no-source:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:rangeM=NA:dispatch=withdrawn:reason=no-source";
            return null;
        }
        var character = actor.Character;
        var actorPos = character?.Transform.World.Position;
        var doodad = character?.ParentWorld?.GetDoodad(selected);
        if (character == null || !actorPos.HasValue || character.ParentWorld == null || doodad == null)
        {
            diag = $"validate=FAIL-source-lost:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:target={selected}:rangeM=NA:dispatch=withdrawn:reason=source-lost";
            return null;
        }
        if (doodad.TemplateId != fixture.GatherDoodadTemplate)
        {
            diag = $"validate=FAIL-source-recycled:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:target={selected}:template={doodad.TemplateId}:rangeM=NA:dispatch=withdrawn:reason=source-recycled";
            return null;
        }
        if (doodad.Despawn > DateTime.MinValue)
        {
            diag = $"validate=FAIL-source-despawn:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:target={selected}:rangeM=NA:dispatch=withdrawn:reason=source-despawn";
            return null;
        }
        var doodadPos = doodad.Transform.World.Position;
        var dist = MathUtil.CalculateDistance(actorPos.Value, doodadPos, false);
        var prefix = $"validate=ok:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:target={selected}:rangeM={M(dist)}";
        if (dist > GatherInteractRadiusM)
        {
            var (liveMove, legLive, driftText) = GatherLegState(actor, selected, doodadPos);
            if (legLive)
            {
                diag = $"{prefix}:dispatch=held:reason=drift-held(drift={driftText})";
                return null;
            }
            diag = $"{prefix}:dispatch=move:reason={(liveMove ? "retrack" : driftText)}";
            return new BotDecisionProposal(
                goal: GatherGoal,
                action: ActorActionType.Move,
                targetId: selected,
                expectedPostcondition: new BotProposalPostcondition(
                    $"gather leg toward quest {questId} doodad {selected} dispatched",
                    _ => true),
                idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:gather:{questId}:{selected}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {questId} gather: well {fixture.GatherDoodadTemplate} ({selected}) at {M(dist)}m flat (outside {GatherInteractRadiusM:F1}m gate), drift {driftText} — closing",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectiveGatherPriority,
                tieBreakKey: $"gather:{questId}:{selected:D10}",
                destination: doodadPos,
                hardPreconditions:
                [
                    new BotProposalPrecondition($"quest-{questId}-relevant",
                        observed => observed.ActiveQuestIds.Contains(questId)),
                    SurvivalVetoClear(actor)
                ]);
        }
        if (!GatherHoldConfirmed(actor, selected, actorPos.Value, doodadPos))
        {
            diag = $"{prefix}:dispatch=stop:reason=in-range";
            return new BotDecisionProposal(
                goal: GatherGoal,
                action: ActorActionType.Stop,
                targetId: selected,
                expectedPostcondition: new BotProposalPostcondition(
                    $"holding at {M(dist)}m off quest {questId} doodad {selected} (stop-before-interact)",
                    _ => true),
                idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:gather-stop:{questId}:{selected}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {questId} gather: well {fixture.GatherDoodadTemplate} ({selected}) at {M(dist)}m flat (inside {GatherInteractRadiusM:F1}m gate) — hold, no interact yet",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectiveGatherPriority,
                tieBreakKey: $"gather:{questId}:{selected:D10}",
                hardPreconditions:
                [
                    new BotProposalPrecondition($"quest-{questId}-relevant",
                        observed => observed.ActiveQuestIds.Contains(questId)),
                    SurvivalVetoClear(actor)
                ]);
        }
        diag = $"{prefix}:dispatch=interact:reason=settled";
        return new BotDecisionProposal(
            goal: GatherGoal,
            action: ActorActionType.Interact,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} gather interact on doodad {selected} delivered",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:gather-interact:{questId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {questId} gather: well {fixture.GatherDoodadTemplate} ({selected}) at {M(dist)}m flat (inside {GatherInteractRadiusM:F1}m gate), settled — Interact draw",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveGatherPriority,
            tieBreakKey: $"gather:{questId}:{selected:D10}",
            skillId: fixture.GatherUseSkill,
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{questId}-relevant",
                    observed => observed.ActiveQuestIds.Contains(questId)),
                SurvivalVetoClear(actor)
            ]);
    }

    /// <summary>
    /// The gather leg's own reading of the leg IT dispatched (the pursuit
    /// discipline, kept for this caller's lane vocabulary): whether a Move leg of
    /// ours is live at all, and — when there is one — whether it still serves the
    /// well (motion since the last issue under <see cref="PursuitRetrackDriftM"/>).
    /// <c>driftText</c> is the caller's own drift reading (<c>fresh</c> before any
    /// issue).
    /// </summary>
    private static (bool LiveMove, bool LegLive, string DriftText) GatherLegState(
        IGameplayActor actor, uint doodadObjId, Vector3 doodadPos)
    {
        var liveMove = actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }
            && actor.ActiveRequest.TargetId == doodadObjId
            && actor.ActiveRequest.MoveOwner == GatherMoveOwner;
        var driftText = "fresh";
        var legLive = liveMove;
        lock (PursuitSync)
        {
            if (LastGatherIssue.TryGetValue(actor.ActorId, out var last) && last.TargetObjId == doodadObjId)
            {
                var drift = Vector3.Distance(last.TargetPos, doodadPos);
                driftText = $"{drift.ToString("F1", CultureInfo.InvariantCulture)}m";
                legLive = liveMove && drift <= PursuitRetrackDriftM;
            }
        }
        return (liveMove, legLive, driftText);
    }

    /// <summary>
    /// The gather leg's settled-halt memory: a Stop already landed for this well
    /// at (nearly) these poses. Read-only over the shared hold memory.
    /// </summary>
    private static bool GatherHoldConfirmed(
        IGameplayActor actor, uint doodadObjId, Vector3 actorPos, Vector3 doodadPos)
    {
        lock (PursuitSync)
        {
            return LastStopHold.TryGetValue(actor.ActorId, out var hold)
                   && hold.TargetObjId == doodadObjId
                   && Vector3.Distance(hold.ActorPos, actorPos) <= 0.5f
                   && Vector3.Distance(hold.TargetPos, doodadPos) <= 0.5f;
        }
    }

    /// <summary>
    /// Item-use leg: consume the objective item through the real
    /// <c>UseItem</c> contract. The leg reads the wake's OWN perception
    /// snapshot (never a second one) for the bag count and the live quest state
    /// for the credit; when the bag is empty it withdraws with a named reason
    /// rather than dispatching a use the engine would refuse. Never advances,
    /// never turns in — those legs stay separate competitors.
    /// </summary>
    internal static BotDecisionProposal? UseItemEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var fixture = context.Fixture!;
        var questId = context.QuestId;
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
        {
            diag = $"validate=quest-not-active:item={fixture.UseItemTemplateId}:need={fixture.UseItemNeed}:have=NA:dispatch=withdrawn:reason=quest-not-active";
            return null;
        }
        var have = 0;
        if (context.Observation?.BagItemCounts.TryGetValue(fixture.UseItemTemplateId, out var held) == true)
            have = held;
        var credited = ItemUseCredited(questId, quest, fixture);
        if (credited)
        {
            diag = $"validate=objective-credited:item={fixture.UseItemTemplateId}:need={fixture.UseItemNeed}:have={have}:dispatch=withdrawn:reason=objective-credited";
            return null;
        }
        if (have <= 0)
        {
            diag = $"validate=no-item:item={fixture.UseItemTemplateId}:need={fixture.UseItemNeed}:have=0:dispatch=withdrawn:reason=no-item";
            return null;
        }
        diag = $"validate=ok:item={fixture.UseItemTemplateId}:need={fixture.UseItemNeed}:have={have}:dispatch=use:reason=objective-open";
        return new BotDecisionProposal(
            goal: UseItemGoal,
            action: ActorActionType.UseItem,
            // The verb's own primary argument: UseItem(itemTemplateId, targetObjId).
            // The consume is self-targeted, so targetId is the ITEM.
            targetId: fixture.UseItemTemplateId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} item-use objective credited (item {fixture.UseItemTemplateId})",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:useitem:{questId}:{fixture.UseItemTemplateId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {questId} item-use: consume {fixture.UseItemTemplateId} x{fixture.UseItemNeed} (have {have}) — UseItem",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveUseItemPriority,
            tieBreakKey: $"useitem:{questId}:{fixture.UseItemTemplateId:D10}",
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{questId}-relevant",
                    observed => observed.ActiveQuestIds.Contains(questId)),
                SurvivalVetoClear(actor)
            ]);
    }

    /// <summary>
    /// Advance leg entry gate: the step machine has work only for an active,
    /// non-Ready quest (a Ready quest's only legal work is turn-in). The gate
    /// owns this reason, so the loop's evidence names the real cause of the
    /// withdraw instead of a generic skip.
    /// </summary>
    internal static string? AdvanceEnter(QuestLegContext context, QuestLegWake wake)
        => context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId) is not { Status: not QuestStatus.Ready and not QuestStatus.Completed }
            ? "quest-not-advanceable"
            : null;

    /// <summary>
    /// TurnIn leg entry gate — the G8b return arbitration: the quest-owned
    /// return leg owns the wake while converting (outside 25 m → Move, inside
    /// unsettled → Stop, settled → first InteractNpc). Without it the
    /// priority-30 TurnIn would win every Ready wake (WITHHELD under withhold)
    /// and the return leg could never dispatch. Suppression is wake-scoped,
    /// 251-shaped, and convergence-gated: it lifts exactly when the return leg
    /// has converged (a settled InteractNpc proposal whose Completed
    /// InteractNpc to the same reporter already sits in the audit trace), so
    /// TurnIn flows the first wake it is legitimate. Whenever the return slice
    /// withdraws, TurnIn enters unchanged (and the withhold seam still guards
    /// it), so G8a-shape wakes (return withdrawn) keep their WITHHELD
    /// observable.
    /// </summary>
    internal static string? TurnInEnter(QuestLegContext context, QuestLegWake wake)
        => wake.ReturnProposal != null && !wake.ReturnConverged
            ? "return-leg-owns-wake"
            : null;

    /// <summary>Advance leg: one step-machine advance for an active, non-Ready quest.</summary>
    internal static BotDecisionProposal? AdvanceEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var questId = context.QuestId;
        return new(
            goal: "quest.advance",
            action: ActorActionType.AdvanceQuest,
            targetId: questId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} step machine advanced",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:advance:{questId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "advance the active quest step machine",
            policyVersion: opts.PolicyVersion,
            priority: opts.AdvancePriority,
            tieBreakKey: $"advance:{questId:D10}",
            payload: null,
            hardPreconditions:
            [
                new BotProposalPrecondition("quest-active",
                    observed => observed.ActiveQuestIds.Contains(questId))
            ]);
    }
    /// <summary>
    /// Turn-in leg (the LevelingLoop TurnIn precedent): one proposal for a Ready
    /// active quest — NPC reporter → TurnInQuest, doodad reporter →
    /// TurnInAtDoodad, neither → AutoTurnInQuest — or null when the quest is not
    /// Ready or no reporter resolves. Reporter objIds resolve per wake — never
    /// stored. Readiness is the live quest Status; the engine revalidates at
    /// dispatch.
    /// </summary>
    internal static BotDecisionProposal? TurnInEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var questId = context.QuestId;
        var character = actor.Character;
        if (character.Quests?.ActiveQuests.GetValueOrDefault(questId) is not { Status: QuestStatus.Ready })
            return null;
        var template = QuestManager.Instance.GetTemplate(questId);
        if (template == null)
            return null;
        var readyActs = template.GetComponents(QuestComponentKind.Ready)
            .SelectMany(c => c.ActTemplates).ToList();
        var reportNpc = readyActs.OfType<QuestActConReportNpc>().FirstOrDefault();
        var reportDoodad = readyActs.OfType<QuestActConReportDoodad>().FirstOrDefault();
        if (reportNpc != null)
        {
            var reporter = character.ParentWorld?.GetNpcByTemplateId(reportNpc.NpcId);
            if (reporter == null)
                return null;
            return TurnInProposalFor(actor, opts, questId, ActorActionType.TurnInQuest,
                reporter.ObjId, new QuestTurnInParams(reporter.ObjId, -1));
        }
        else if (reportDoodad != null)
        {
            var doodad = character.ParentWorld?.GetAllDoodads().FirstOrDefault(d => d?.TemplateId == reportDoodad.DoodadId);
            if (doodad == null)
                return null;
            return TurnInProposalFor(actor, opts, questId, ActorActionType.TurnInDoodad,
                doodad.ObjId, new QuestTurnInParams(doodad.ObjId, -1));
        }
        else
        {
            return TurnInProposalFor(actor, opts, questId, ActorActionType.AutoTurnIn,
                0, new QuestTurnInParams(0, -1));
        }
    }

    private static BotDecisionProposal TurnInProposalFor(
        IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, uint questId,
        ActorActionType action, uint targetId, QuestTurnInParams turnIn)
        => new(
            goal: TurnInGoal,
            action: action,
            targetId: questId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} completed by turn-in",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:turnin:{questId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "turn in the ready quest for copper reward",
            policyVersion: opts.PolicyVersion,
            priority: opts.TurnInPriority,
            tieBreakKey: $"turnin:{questId:D10}",
            payload: turnIn,
            hardPreconditions:
            [
                new BotProposalPrecondition("quest-active",
                    observed => observed.ActiveQuestIds.Contains(questId))
            ]);

    /// <summary>
    /// The accept RANK of one live offering — the brain's selection hook, a
    /// pure function of the policy options, the offering, and the offering
    /// quest's derived row:
    ///
    ///   - <c>Priority</c> is the frozen band rule,
    ///     <c>AcceptPriority + (BandMax - Level)</c>, clamped at zero below the
    ///     band floor: BAND IS PRIMARY, byte-identical to the pre-brain rule.
    ///   - <c>Weight</c> carries the two tie-break hooks — the REWARD hook (an
    ///     offering whose quest carries a reward item outranks a band-equal one
    ///     that carries none, when <see cref="QuestOptions.PreferRewardingOffers"/>)
    ///     and <see cref="QuestOptions.AcceptPersonalityWeight"/> (0 = uniform
    ///     by default). It rides the selector's own second ordering key, never
    ///     the priority, so a hook can only ever reorder offers the band term
    ///     already tied — it can never lift a lower-band offer above a
    ///     higher-band one.
    ///
    /// The ranked cut and the emitted proposal both read this, so the offers the
    /// bound drops are exactly the ones the selector would have ranked last.
    /// </summary>
    internal static (int Priority, int Weight) AcceptRank(
        QuestDecisionScenario.QuestOptions opts, QuestOffering offering, QuestFixtureRow row)
    {
        var priority = opts.AcceptPriority + Math.Max(0, opts.BandMax - offering.Level);
        var rewarding = opts.PreferRewardingOffers && row.RewardItem != 0;
        var weight = Math.Clamp(
            opts.AcceptPersonalityWeight + (rewarding ? 1 : 0),
            -BotDecisionProposal.MaxPersonalityWeight,
            BotDecisionProposal.MaxPersonalityWeight);
        return (priority, weight);
    }

    /// <summary>
    /// Accept proposal for one live offering, ranked by <see cref="AcceptRank"/>
    /// (band primary, then the reward/personality tie-break hooks). The row is
    /// passed in — the caller derives it once per offering per wake, so the
    /// ranking cut and the proposal never disagree about what the quest offers.
    /// </summary>
    private static BotDecisionProposal AcceptProposal(
        IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, uint targetObjId,
        QuestOffering offering, QuestFixtureRow row)
    {
        var (priority, weight) = AcceptRank(opts, offering, row);
        return new BotDecisionProposal(
            goal: "quest.accept",
            action: ActorActionType.AcceptQuest,
            targetId: offering.QuestId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {offering.QuestId} is active",
                observed => observed.ActiveQuestIds.Contains(offering.QuestId)),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:accept:{offering.QuestId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"lowest offered level in [{opts.BandMin}..{opts.BandMax}]",
            policyVersion: opts.PolicyVersion,
            priority: priority,
            personalityWeight: weight,
            tieBreakKey: offering.QuestId.ToString("D10"),
            payload: (offering, targetObjId),
            hardPreconditions:
            [
                new BotProposalPrecondition("quest-not-active",
                    observed => !observed.ActiveQuestIds.Contains(offering.QuestId))
            ]);
    }

    /// <summary>
    /// G4 objective→target proposal (the wired quest only): the selector's
    /// selected live prey as a Target-proposal competing in the shared Select. Priority
    /// sits above advance/accept (pursue the held objective) and below turn-in
    /// (a Ready quest still reports first). Null when the funnel carries no
    /// selection — relevance, objective, and source gates stay fail-closed.
    ///
    /// G5 enabling (single line, G4 PASS-preserving): the target-unassigned
    /// precondition yields the wake's single dispatch slot once the assignment
    /// postcondition already holds — re-issuing SetTarget for the assigned
    /// target is a terminal no-op that would otherwise starve every
    /// lower-priority proposal (including pursuit-24) forever. The frozen G4
    /// chain fires unchanged whenever the target is NOT assigned, which is
    /// exactly the G4 gate's START condition (fresh staging, never targeted).
    /// </summary>
    internal static BotDecisionProposal? TargetEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var funnel = context.Funnel!;
        var fixture = context.Fixture!;
        if (!funnel.ObjectiveResolved || !funnel.SourceResolved || !funnel.Relevance || funnel.SelectedObjId == 0)
            return null;
        var selected = funnel.SelectedObjId;
        var dist = double.IsNaN(funnel.SelectedDistanceM)
            ? "NA"
            : funnel.SelectedDistanceM.ToString("F1", CultureInfo.InvariantCulture);
        return new BotDecisionProposal(
            goal: "quest.objective-target",
            action: ActorActionType.Target,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"current target is {selected}",
                observed => observed.CurrentTargetObjId == selected),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:target:{fixture.QuestId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {fixture.QuestId} objective: live {funnel.SelectedTemplateId} {selected} at {dist}m",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveTargetPriority,
            tieBreakKey: $"target:{fixture.QuestId}:{selected:D10}",
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                    observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
                new BotProposalPrecondition("target-unassigned",
                    observed => observed.CurrentTargetObjId != selected)
            ]);
    }

    /// <summary>
    /// G5 pursuit proposal (the wired quest only): Move/Stop for the G4-selected
    /// prey objId at <c>ObjectivePursuitPriority</c> (just below Target-25,
    /// so assignment wins first). Target commitment: the funnel's selection
    /// is taken as committed — NO second candidate competition runs here;
    /// only the committed unit is revalidated live every wake
    /// (ParentWorld resolve + alive + <see cref="CombatDecisionTree.IsHostileTarget"/>
    /// + CanAttack + visible; legality is reused, never duplicated).
    /// Per wake: outside the 3.0 m fixture stop radius → Move (fresh leg when
    /// no Move is live, drift-gated retrack only after &gt; 2.0 m target
    /// motion since the last issue); inside → Stop (audited halt, remain
    /// settled). Lost/invalid targets withdraw the proposal with a named
    /// reason (reselect is the funnel's next-wake job). Never Cast,
    /// AutoAttack, Loot, or credit — those verbs are unreachable from here.
    ///
    /// TRAVEL BRAIN (the SECOND live dispatch caller, the return leg's twin):
    /// this leg's closing leg is decided by <see cref="TravelBrain.Decide"/>
    /// through <see cref="TravelLegDispatch"/> — the journey is armed once per
    /// prey (owner-tagged <c>PURSUIT_MOVE_TO_UNIT</c>, so the return leg's own
    /// boundary can never disarm it) and then the brain owns the verb, the drift
    /// hold, the repath budget, and the named abandonment terminals (WrongWorld /
    /// TargetGone / Unreachable, never a bare navigation failure). The leg keeps
    /// its own vocabulary and its own dispatch shapes: the SAME <c>MoveToUnit</c>
    /// on the live prey objId (owner <c>PURSUIT_MOVE_TO_UNIT</c>), the SAME
    /// audited <c>Stop</c> inside the 3.0 m stop radius (whose
    /// hold-confirm/combat-engaged yields are the leg's own and run first), and
    /// the SAME <c>dispatch=</c>/<c>reason=</c> lane tokens; the brain's decision
    /// rides additively in a <c>:travel=</c> fragment. The pre-brain rule is still
    /// the fallback whenever no journey can be armed (an actor with no object
    /// identity), and the journey is ended at this leg's own boundary (the prey
    /// identity is gone) so a banked verdict cannot outlive the reason it was
    /// reached.
    /// </summary>
    internal static BotDecisionProposal? PursuitEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var funnel = context.Funnel!;
        var fixture = context.Fixture!;
        static string M(double value)
            => double.IsNaN(value) ? "NA" : value.ToString("F1", CultureInfo.InvariantCulture);
        var selected = funnel.SelectedObjId;
        if (!funnel.ObjectiveResolved || !funnel.SourceResolved)
        {
            diag = "validate=FAIL-static:rangeM=NA:dispatch=withdrawn:reason=" +
                (!funnel.ObjectiveResolved ? "FAIL-OBJECTIVE" : "FAIL-SOURCE");
            return null;
        }
        if (selected == 0 || !funnel.Relevance)
        {
            // G7b (observe-only): the funnel drops dead candidates, so the
            // post-death wake withdraws here — re-resolve ONLY the quest-pinned
            // objId (never a scan) and name the corpse with its probe.
            // The journey boundary: a prey identity the funnel no longer serves is
            // over, so a banked verdict must not outlive it and a LATER prey must
            // not inherit its repath budget (the return leg's own rule).
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.PursuitMoveOwner);
            var corpseFrag = ObservePinnedCorpse(actor, opts, funnel);
            diag = $"validate={(selected == 0 ? "no-selection" : "not-relevant")}:rangeM=NA:dispatch=withdrawn:reason=" +
                (selected == 0 ? "no-selection" : "not-relevant") + corpseFrag;
            return null;
        }
        var character = actor.Character;
        var actorPos = character?.Transform.World.Position;
        var npc = character?.ParentWorld?.GetNpc(selected);
        string invalidReason;
        Vector3 npcPos;
        if (character == null || !actorPos.HasValue || character.ParentWorld == null)
            invalidReason = "no-world";
        else if (npc == null)
            invalidReason = "target-lost";
        else if (npc.Hp <= 0)
            invalidReason = "target-dead";
        else if (!CombatDecisionTree.IsHostileTarget(character, npc))
            invalidReason = "target-not-hostile";
        else if (!character.CanAttack(npc))
            invalidReason = "target-not-attackable";
        else if (!character.CanSeeTarget(npc))
            invalidReason = "target-not-visible";
        else
            invalidReason = "";
        if (invalidReason != "")
        {
            // G7b (observe-only): the pinned-target dead transition names the
            // corpse with its read-only probe (all context known here: funnel
            // quest/objective, pinned objId/template/dead-state). A lost
            // target re-resolves ONLY the quest-pinned objId (never a scan).
            var corpseFrag = invalidReason == "target-dead" && npc != null
                ? NoteDeadSelection(actor, opts, funnel, npc)
                : ObservePinnedCorpse(actor, opts, funnel);
            diag = $"validate=FAIL-{invalidReason}:rangeM=NA:dispatch=withdrawn:reason={invalidReason}{corpseFrag}";
            return null;
        }
        npcPos = npc!.Transform.World.Position;
        var dist = MathUtil.CalculateDistance(actorPos!.Value, npcPos, false);

        // ------------------------------------------------------- TRAVEL DECISION
        // What this leg dispatched (its own reading of its own leg — the brain never
        // reads an actor request), then what the travel chain asks for this wake.
        // The journey is OWNER-TAGGED so the return leg's own boundary can never
        // disarm it (one actor walks one journey PER OWNING LEG).
        var legOutcome = TravelLegDispatch.MapLegOutcome(
            actor, selected, TravelLegDispatch.PursuitMoveOwner);
        var (liveMove, legLive, driftText) = PursuitLegState(actor, selected, npcPos);
        var brain = TravelLegDispatch.Prepare(
            actor, selected, npcPos, PursuitStopRadiusM, legLive, legOutcome,
            legOwner: TravelLegDispatch.PursuitMoveOwner);
        var verdict = TravelLegDispatch.DecidePursuitLeg(
            brain, dist <= PursuitStopRadiusM, legLive, liveMove ? "retrack" : driftText, driftText);
        // Flat, bracket-free additive fragment: the brain's own arm/verb/mode/
        // terminal/reason beside the leg's unchanged tokens (a lane parser that
        // scans :dispatch= sees exactly what it saw before).
        var travelFrag = brain is { } prepared
            ? $":travel={prepared.Decision.Describe()}:routed=true"
            : ":travel=fallback";
        var payload = new TravelLegDispatch.TravelDispatchParams(verdict.Decision);

        if (dist <= PursuitStopRadiusM)
        {
            // G6 hold-confirm: a Stop already landed for this target at these
            // poses stays landed — withdraw so the combat proposal (priority
            // 23) can win a settled wake. Any actor/target motion re-arms
            // Stop, and an unassigned target never withdraws (Target-25 owns
            // assignment). G5-observed states (closing, holding pre-settle)
            // still land Stop exactly as before.
            lock (PursuitSync)
            {
                if (LastStopHold.TryGetValue(actor.ActorId, out var hold) && hold.TargetObjId == selected
                    && character!.CurrentTarget?.ObjId == selected
                    && Vector3.Distance(hold.ActorPos, actorPos!.Value) <= 0.5f
                    && Vector3.Distance(hold.TargetPos, npcPos) <= 0.5f)
                {
                    diag = $"validate=ok:rangeM={M(dist)}:dispatch=held:reason=hold-confirmed{travelFrag}";
                    return null;
                }
            }
            // CombatBrain-engaged yield fact (mirrors hold-confirm): while the
            // combat brain holds a live commitment for this actor, the stop leg
            // is not the wake's work — the combat arm owns it. Unlike
            // hold-confirm this needs no pose history: it is a single published
            // fact the brain writes when it commits and clears when it ends, so
            // it also covers the first settled wake after a commitment began
            // without a prior pursuit Stop.
            if (CombatBrainEngagement.ShouldYieldPursuit(actor.ActorId))
            {
                diag = $"validate=ok:rangeM={M(dist)}:dispatch=held:reason=combat-brain-engaged{travelFrag}";
                return null;
            }
            if (verdict.Verb != TravelLegDispatch.ReturnLegVerb.Stop)
            {
                // A settled/abandoned journey (the brain's own Hold carries it), or a
                // verb this leg does not serve: no proposal, and the diag still names
                // the prey, the reason, and the terminal.
                diag = $"validate=ok:rangeM={M(dist)}:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{travelFrag}";
                return null;
            }
            diag = $"validate=ok:rangeM={M(dist)}:dispatch=stop:reason=in-range{travelFrag}";
            return new BotDecisionProposal(
                goal: PursuitGoal,
                action: ActorActionType.Stop,
                targetId: selected,
                expectedPostcondition: new BotProposalPostcondition(
                    $"holding at {M(dist)}m off quest {fixture.QuestId} target {selected} (stop-before-attack)",
                    _ => true),
                idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:pursuit-stop:{fixture.QuestId}:{selected}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {fixture.QuestId} pursuit: live {fixture.PreyTemplate} {selected} at {M(dist)}m (inside {PursuitStopRadiusM:F1}m stop radius) — hold, no attack",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectivePursuitPriority,
                tieBreakKey: $"pursuit:{fixture.QuestId}:{selected:D10}",
                payload: payload,
                hardPreconditions:
                [
                    new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                        observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
                    // Survival-veto precondition (the CombatBrain fact): while a
                    // survival condition owns the actor's wake, the pursuit leg
                    // does not dispatch — the retreat/recovery leg owns it. The
                    // observed value is authoritative; the static read is the
                    // per-actor fallback the selector's context cannot carry.
                    SurvivalVetoClear(actor)
                ]);
        }
        if (verdict.Verb != TravelLegDispatch.ReturnLegVerb.MoveToUnit)
        {
            // A live leg still serves the prey (the brain's own leg-hold), or the
            // journey settled: no new leg, and the diag names the reason.
            diag = $"validate=ok:rangeM={M(dist)}:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{travelFrag}";
            return null;
        }
        diag = $"validate=ok:rangeM={M(dist)}:dispatch=move:reason={(liveMove ? "retrack" : driftText)}{travelFrag}";
        return new BotDecisionProposal(
            goal: PursuitGoal,
            action: ActorActionType.Move,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"pursuit leg toward quest {fixture.QuestId} target {selected} dispatched",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:pursuit:{fixture.QuestId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {fixture.QuestId} pursuit: live {fixture.PreyTemplate} {selected} at {M(dist)}m (outside {PursuitStopRadiusM:F1}m stop radius), drift {driftText} — closing",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectivePursuitPriority,
            tieBreakKey: $"pursuit:{fixture.QuestId}:{selected:D10}",
            payload: payload,
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                    observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture))
            ]);
    }
    /// <summary>
    /// G6 combat proposal (the wired quest only): AutoAttack for the G4-selected
    /// prey objId at <c>ObjectiveCombatPriority</c> (just below pursuit 24,
    /// so range-hold always wins while closing or holding). Target
    /// commitment: the funnel's selection is taken as committed — NO second
    /// candidate competition runs here (same-target-no-recompetition); only
    /// the committed unit is revalidated live every wake (ParentWorld
    /// resolve + alive + <see cref="CombatDecisionTree.IsHostileTarget"/>
    /// + CanAttack + visible + assigned + in-range; legality is reused,
    /// never duplicated). Per wake: in-range + G4-assigned + loop not yet
    /// live → AutoAttack (engine's live-actor verb, the roam hunt-leg
    /// shape). Withdraws (null + named diag) when the target is unassigned
    /// (Target-25 owns assignment), out of range (pursuit owns), already
    /// live (no re-dispatch, never steal another loop), or invalid.
    /// Dispatches ONLY GameplayActor.AutoAttack — no Cast, Loot, or credit.
    /// </summary>
    internal static BotDecisionProposal? CombatEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var funnel = context.Funnel!;
        var fixture = context.Fixture!;
        static string M(double value)
            => double.IsNaN(value) ? "NA" : value.ToString("F1", CultureInfo.InvariantCulture);
        hpBefore = -1;
        var selected = funnel.SelectedObjId;
        if (!funnel.ObjectiveResolved || !funnel.SourceResolved)
        {
            diag = "validate=FAIL-static:target=-:template=-:alive=NA:visible=NA:hostile=NA:legal=NA:distM=NA:verb=none:hpBefore=NA";
            return null;
        }
        if (selected == 0 || !funnel.Relevance)
        {
            // G7b (observe-only): post-death funnel withdrawal — re-resolve ONLY
            // the quest-pinned objId (never a scan), name the corpse + probe.
            // The kite's journey boundary: a threat identity the funnel no longer
            // serves is over, so a banked verdict must not outlive it and a LATER
            // threat must not inherit its repath budget (the pursuit leg's own
            // rule, from the same seam).
            CombatTravelDispatch.EndJourney(actor);
            var corpseFrag = ObservePinnedCorpse(actor, opts, funnel);
            diag = $"validate={(selected == 0 ? "no-selection" : "not-relevant")}:target=-:template=-:alive=NA:visible=NA:hostile=NA:legal=NA:distM=NA:verb=none:hpBefore=NA{corpseFrag}";
            return null;
        }
        var character = actor.Character;
        var actorPos = character?.Transform.World.Position;
        var npc = character?.ParentWorld?.GetNpc(selected);
        if (character == null || !actorPos.HasValue || character.ParentWorld == null || npc == null)
        {
            // The threat identity is gone: the journey it armed is over (a fresh
            // resolve next wake arms a clean one) — never a banked verdict or a
            // spent budget carried onto an identity that no longer exists.
            CombatTravelDispatch.EndJourney(actor);
            var corpseFrag = ObservePinnedCorpse(actor, opts, funnel);
            diag = $"validate=target-lost:target={selected}:template={fixture.PreyTemplate}:alive=NA:visible=NA:hostile=NA:legal=NA:distM=NA:verb=none:hpBefore=NA{corpseFrag}";
            return null;
        }
        var alive = npc.Hp > 0;
        var hostile = alive && CombatDecisionTree.IsHostileTarget(character, npc);
        var attackable = alive && character.CanAttack(npc);
        var visible = character.CanSeeTarget(npc);
        if (!alive)
        {
            // G7b (observe-only): THE pinned-target dead transition — questId /
            // objective-context (funnel) + target objId / template / dead-state
            // (live npc) all known here; recognition runs on this resolve, no
            // second world scan. Read-only probe, zero dispatch. A corpse is a
            // threat identity that is gone: the kite journey ends with it.
            CombatTravelDispatch.EndJourney(actor);
            var corpseFrag = NoteDeadSelection(actor, opts, funnel, npc);
            diag = $"validate=target-dead:target={selected}:template={npc.TemplateId}:alive=false:visible={visible}:hostile=false:legal=false:distM=NA:verb=none:hpBefore=NA{corpseFrag}";
            return null;
        }
        if (!hostile || !attackable || !visible)
        {
            var reason = !hostile ? "target-not-hostile" : !attackable ? "target-not-attackable" : "target-not-visible";
            diag = $"validate={reason}:target={selected}:template={npc.TemplateId}:alive=true:visible={visible}:hostile={hostile}:legal=false:distM=NA:verb=none:hpBefore=NA";
            return null;
        }
        if (character.CurrentTarget?.ObjId != selected)
        {
            diag = $"validate=target-unassigned:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true:distM={M(MathUtil.CalculateDistance(actorPos.Value, npc.Transform.World.Position, false))}:verb=none:hpBefore=NA";
            return null;
        }
        if (character.IsAutoAttack)
        {
            diag = $"validate=already-live:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true:distM={M(MathUtil.CalculateDistance(actorPos.Value, npc.Transform.World.Position, false))}:verb=none:hpBefore={npc.Hp}";
            return null;
        }
        var dist = MathUtil.CalculateDistance(actorPos.Value, npc.Transform.World.Position, false);
        if (dist > PursuitStopRadiusM)
        {
            diag = $"validate=out-of-range:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true:distM={M(dist)}:verb=none:hpBefore={npc.Hp}";
            return null;
        }

        // ---------------------------------------------------------- COMBAT BRAIN
        // The brain owns WHAT the combat leg asks for this wake: the same
        // commitment the frozen G6 rule establishes (the funnel's selection, never
        // a second competition), now run through the decision chain — survival
        // veto, disengage, crowd-control hold, heal threshold, band management,
        // and the skill/sustain choice. The brain is pure; the live reads and the
        // engagement publication are the planner's. `allowSkillCasts` is false
        // while the engine's skill surface is not loaded (unit rigs), so no
        // fabricated Cast is ever proposed.
        var combatBrain = CombatBrainPlanner.Prepare(
            actor, null, selected,
            // The rotation arm is a SEPARATE gate (see
            // QuestOptions.EnableCombatRotation): the frozen G6 lane dispatches
            // the engine's auto-attack loop, and flipping the quest path onto
            // skill casts is a behavior change with its own fixture, not a
            // side effect of building the brain.
            allowSkillCasts: opts.EnableCombatRotation && Core.Managers.SkillManager.Instance != null,
            nowUtc: DateTime.UtcNow);
        var brainDecision = CombatBrainPlanner.Decide(combatBrain);

        // ------------------------------------------------------- KITE TRAVEL DECISION
        // The brain's own Move arm is this leg's SPACING leg, and the sub-critical
        // spacing wake (the ranged/caster kite — see
        // <see cref="CombatTravelDispatch.IsSubCriticalSpacing"/>) is decided by the
        // TRAVEL chain: the SAME shared <see cref="TravelBrain.SafeAnchor"/>, now with
        // the journey's own budget, drift hold, and named abandonment terminals.
        // Every other arm — and a kite whose journey could not be armed — keeps the
        // caller's exact pre-brain rule (the brain's own destination), so the fallback
        // leg is byte-identical to what this leg issued before the wiring.
        var kiteLeg = actor.ActiveRequest;
        var kiteBrain = CombatTravelDispatch.IsSubCriticalSpacing(brainDecision)
            ? CombatTravelDispatch.Prepare(actor, selected, npc.Transform.World.Position,
                CombatTravelDispatch.IsOurLiveLeg(kiteLeg) ? kiteLeg!.Destination : null)
            : null;
        var kite = CombatTravelDispatch.DecideKiteLeg(kiteBrain, brainDecision);
        // The kite got its room (the brain is not asking for spacing this wake) or the
        // arm is falling back to its pre-brain rule: that is this leg's JOURNEY
        // BOUNDARY, so the next kite wake arms a clean one instead of inheriting a
        // spent budget or a banked verdict. A ROUTED verdict (move / stop / held)
        // keeps the journey — including the chain's own settled terminal, which the
        // store re-reads stickily instead of letting this caller re-derive it.
        if (!kite.Routed)
            CombatTravelDispatch.EndJourney(actor);

        if (brainDecision.Arm is CombatArm.SurvivalVeto or CombatArm.Disengage or CombatArm.CrowdControl)
        {
            diag = $"validate=ok:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true" +
                   $":distM={M(dist)}:verb=none:hpBefore={npc.Hp}:brain={brainDecision.Describe()}";
            return null; // a survival/CC/retreat condition owns the wake
        }

        var brainVerb = brainDecision.Verb;
        var action = brainVerb switch
        {
            CombatVerb.Cast => ActorActionType.Cast,
            CombatVerb.UseItem => ActorActionType.UseItem,
            CombatVerb.Move => ActorActionType.Move,
            _ => ActorActionType.AutoAttack
        };
        if (brainVerb == CombatVerb.Hold)
        {
            diag = $"validate=already-live:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true" +
                   $":distM={M(dist)}:verb=none:hpBefore={npc.Hp}:brain={brainDecision.Describe()}";
            return null;
        }
        // The travel chain's own withholds on the spacing arm: a live kite leg that
        // still serves this wake's anchor keeps its progress (the anchor RECEDES as
        // the bot walks, so the chain's own leg-hold arm cannot serve it and this
        // reading does), and a journey that reached a named terminal withdraws naming
        // it — never a leg to an abandoned destination, and never a bare failure.
        // A FALLBACK is NOT a withhold: it is this leg's pre-brain leg (the brain's
        // own destination) and dispatches exactly as it did before the wiring, under
        // the original owner.
        var kiteFrag = brainVerb == CombatVerb.Move
            ? $":travel={CombatTravelDispatch.Describe(kite)}"
            : "";
        if (brainVerb == CombatVerb.Move
            && kite.Verb is CombatTravelDispatch.KiteLegVerb.Held or CombatTravelDispatch.KiteLegVerb.Withdraw)
        {
            diag = $"validate=ok:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true" +
                   $":distM={M(dist)}:verb=none:hpBefore={npc.Hp}:brain={brainDecision.Describe()}{kiteFrag}";
            return null;
        }
        var verbText = brainVerb == CombatVerb.Cast
            ? $"Cast({brainDecision.SkillId})"
            : brainVerb == CombatVerb.UseItem
                ? $"UseItem({brainDecision.ItemTemplateId})"
                : brainVerb == CombatVerb.Move ? "Move" : "AutoAttack";
        if (brainVerb == CombatVerb.AutoAttack)
            hpBefore = npc.Hp;
        // The leg that RUNS is the routed one when the travel chain produced it (its
        // own destination is authoritative); the fallback keeps the brain's own.
        var destination = kite.Destination ?? brainDecision.Destination;
        diag = $"validate=ok:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true" +
               $":distM={M(dist)}:verb={verbText}:hpBefore={npc.Hp}:brain={brainDecision.Describe()}{kiteFrag}";
        return new BotDecisionProposal(
            goal: CombatGoal,
            action: action,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"combat {verbText} on quest {fixture.QuestId} target {selected}",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:combat:{fixture.QuestId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {fixture.QuestId} combat: live {fixture.PreyTemplate} {selected} at {M(dist)}m, hp {npc.Hp} — {verbText} ({brainDecision.Describe()})",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveCombatPriority,
            tieBreakKey: $"combat:{fixture.QuestId}:{selected:D10}",
            destination: destination,
            skillId: brainDecision.SkillId,
            payload: new CombatDispatchParams(brainDecision, kite.Decision),
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                    observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
                new BotProposalPrecondition("target-assigned",
                    observed => observed.CurrentTargetObjId == selected),
                // The combat arm yields the wake while a survival condition owns
                // it (the same CombatBrain fact the pursuit leg reads), so the
                // retreat/recovery leg is never fighting the auto-attack for the
                // wake on a critical wake.
                SurvivalVetoClear(actor)
            ]);
    }

    /// <summary>
    /// G7c loot proposal (the wired quest only): Loot for the G7b-recognized corpse
    /// at <c>ObjectiveLootPriority</c> (just below combat 23, so a live combat
    /// decision always wins while the target is alive; above advance so a
    /// lootable corpse is taken before step-machine work).
    ///
    /// The gate ORDER is no longer this leg's own: <see cref="LootBrain"/> owns
    /// the chain (survival veto → ownership → probe → loop release → loot-once →
    /// safe radius → worth → space → emit) and <see cref="LootBrainPlanner"/>
    /// owns every live read (the corpse resolve, the container probe, the engine's
    /// tagging evidence, the wake census, the worth read, the bag slots). This leg
    /// contributes only what the brain cannot know: the quest-owned corpse pin
    /// (<c>LastCorpse</c>, the same-ObjId continuity the G7b arms demand), the
    /// fixture row's prey template, the loot-once memory, and the emitted proposal.
    ///
    /// The <c>validate=</c> vocabulary every existing lane scanner keys on is
    /// preserved verbatim through <see cref="LootBrain.ValidateToken"/> and the
    /// brain's arms are named ADDITIVELY beside it (<c>:lootBrain=arm=…</c> — a
    /// flat sibling key list, never a nested bracket block), so
    /// no lane parser changes shape. Withholds (null + named diag) on every
    /// non-emit arm; dispatches ONLY GameplayActor.Loot, once.
    ///
    /// CORPSE APPROACH (the travel brain's THIRD live dispatch caller, the
    /// pursuit/return legs' twin): one withheld arm is not the take's to fix — the
    /// corpse is OURS, lootable-shaped, and simply out of reach — so that arm closes
    /// on the pinned corpse through <see cref="LootTravelDispatch"/>, over the same
    /// <c>MoveToUnit</c> leg the sibling legs dispatch (owner-tagged
    /// <c>LOOT_APPROACH_MOVE_TO_UNIT</c>; the seam's arrival halt is deliberately NOT
    /// dispatched here, because arrival is exactly when the take itself becomes
    /// legal). The take itself, the loot-once memory, the ledger and every priority
    /// are untouched: the approach only moves the actor into the range its own chain
    /// already published as the reason it could not take, and the SAME wake's
    /// <c>validate=</c> token is preserved byte-for-byte (the leg's
    /// <c>dispatch=</c>/<c>reason=</c> evidence rides additively in a <c>:travel=</c>
    /// fragment).
    /// </summary>
    internal static BotDecisionProposal? LootEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var fixture = context.Fixture!;
        QuestCorpseRecord? rec;
        lock (PursuitSync)
            LastCorpse.TryGetValue(actor.ActorId, out rec);
        QuestCorpseRecord? candidate = rec != null && rec.QuestId == fixture.QuestId ? rec : null;

        bool already;
        lock (PursuitSync)
            already = candidate != null && LootedCorpses.Contains((actor.ActorId, candidate.ObjId));

        var character = actor.Character;
        var prepared = LootBrainPlanner.Prepare(
            actor,
            context.Observation,
            new LootBrainPlanner.Request(
                CorpseObjId: candidate?.ObjId ?? 0,
                PreyTemplateId: fixture.PreyTemplate,
                MinContainerValueCopper: opts.MinContainerValueCopper,
                AlreadyLooted: already,
                LoopLive: character?.IsAutoAttack == true,
                SafeRadiusM: opts.LootSafeRadiusM,
                NowUtc: DateTime.UtcNow));

        var decision = prepared.Decision;
        var validate = LootBrain.ValidateToken(decision.Reason);
        // Flat, bracket-free key list (nested brackets would nest inside the
        // DECIDE/loot brackets a lane parser scans, so the brain's own fragment
        // joins them as siblings rather than a block).
        var brainFrag = $":lootBrain={LootBrain.Describe(decision)}:{prepared.DescribeOwnership()}";
        // The container tally the funnel scanners read is the probe's own
        // reading; a corpse that never resolved reports an explicit "-" exactly
        // as the pre-brain arm did.
        var containerText = prepared.CorpseResolved ? prepared.Inputs.ContainerItemCount.ToString(CultureInfo.InvariantCulture) : "-";
        var lootableText = prepared.CorpseResolved
            ? (prepared.Inputs.ContainerLootable ? "true" : "false")
            : "-";

        if (!decision.IsEmit)
        {
            // The banked skip is final, so it is recorded here — a terminal skip
            // never lags an act the way a take would.
            LootBrainPlanner.PublishSkip(prepared, DateTime.UtcNow);
            // ------------------------------------------------- CORPSE APPROACH
            // The ONE withhold a WALK can clear. The chain reaches its probe arm only
            // for OUR corpse, past the ownership and loot-once gates, so the probe's own
            // `ContainerOutOfRange` verdict is by construction exactly "ours, lootable-
            // shaped, and out of reach" — the brain's reading is taken verbatim rather
            // than its gates being reassembled here, and it can never drift from them.
            // No other withhold asks for a leg: ownership, safety and loop-live are not
            // reachability.
            //
            // The probe arm PRECEDES loot-once in the chain, so an out-of-range corpse
            // can still be one this actor has already taken or one the ledger banked a
            // terminal skip for. Walking to either would be motion with no take at the
            // end of it, so the terminal memories the chain publishes as facts (the
            // caller's own loot-once flag, and the ledger's terminal dispositions — the
            // two sides of the same take, plus the skip the chain never reconsiders) are
            // refused here. Both are READS of established facts, never a second
            // reachability rule.
            var prefix = $"validate={validate}:target={ShowCorpse(prepared)}:container={containerText}" +
                         $":lootable={lootableText}:verb=none{brainFrag}";
            var terminal = already || prepared.Inputs.PriorDisposition is LootDisposition.Take or LootDisposition.Skipped;
            if (prepared.Decision.Reason != LootReason.ContainerOutOfRange || terminal)
            {
                diag = prefix;
                return null;
            }
            // The pinned corpse is the journey's identity, so the approach gets a real
            // repath budget and the named abandonment terminals the sibling legs have.
            // The leg's own routing rides the SAME `dispatch=`/`reason=` keys and the
            // `:travel=` fragment the pursuit / return / combat-kite legs print.
            var approach = RunLootApproach(actor, opts, fixture, prepared);
            diag = $"{prefix}:dispatch={approach.DispatchToken}:reason={approach.Reason}:travel={approach.Travel}";
            return approach.Proposal;
        }

        diag = $"validate={validate}:target={decision.CorpseObjId}:container={containerText}" +
               $":lootable=true:verb=Loot{brainFrag}";
        return new BotDecisionProposal(
            goal: LootGoal,
            action: ActorActionType.Loot,
            targetId: decision.CorpseObjId,
            expectedPostcondition: new BotProposalPostcondition(
                $"looted quest {fixture.QuestId} corpse {decision.CorpseObjId}",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:loot:{fixture.QuestId}:{decision.CorpseObjId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {fixture.QuestId} loot: dead {fixture.PreyTemplate} {decision.CorpseObjId} container={prepared.Inputs.ContainerItemCount} — Loot once ({LootBrain.Describe(decision)})",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveLootPriority,
            tieBreakKey: $"loot:{fixture.QuestId}:{decision.CorpseObjId:D10}",
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                    observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture))
            ]);
    }

    /// <summary>
    /// The corpse objId a withholding loot arm names: the pinned corpse when one
    /// was recorded (so a lane can still join the record to its wake), otherwise
    /// the explicit <c>-</c> the pre-brain arm printed for an absent record.
    /// </summary>
    private static string ShowCorpse(in LootBrainPlanner.Prepared prepared)
        => prepared.Inputs.CorpseObjId != 0
            ? prepared.Inputs.CorpseObjId.ToString(CultureInfo.InvariantCulture)
            : "-";

    /// <summary>
    /// One corpse-approach wake: the proposal the leg would dispatch (null when it
    /// holds or withdraws), the lane's <c>dispatch=</c>/<c>reason=</c> tokens, and the
    /// brain's own additive decision fragment. The journey is owner-tagged
    /// (<see cref="LootTravelDispatch.ApproachMoveOwner"/>) so no other leg's boundary
    /// can disarm it.
    /// </summary>
    private readonly record struct LootApproachWake(BotDecisionProposal? Proposal, string DispatchToken, string Reason, string Travel);

    /// <summary>
    /// Runs the corpse-approach decision for this wake over the SAME pinned corpse the
    /// loot chain just read, and shapes it into the leg's proposal.
    ///
    /// Failure direction: a journey that could not be armed (no actor identity) and a
    /// verb this leg does not serve both produce NO proposal and tokens naming the
    /// fact — never a fabricated leg. The take's own arm is untouched either way.
    /// </summary>
    private static LootApproachWake RunLootApproach(
        IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, QuestFixtureRow fixture,
        in LootBrainPlanner.Prepared prepared)
    {
        var corpseObjId = prepared.Inputs.CorpseObjId;
        var legOutcome = LootTravelDispatch.MapLegOutcome(actor, corpseObjId);
        var (liveMove, legLive, driftText) = LootApproachLegState(actor, corpseObjId);
        var brain = LootTravelDispatch.Prepare(actor, new LootTravelDispatch.Request(
            CorpseObjId: corpseObjId,
            PreyTemplateId: fixture.PreyTemplate,
            ArrivalRadiusM: LootTravelDispatch.ApproachArrivalRadiusM,
            LegOutcome: legOutcome,
            LegLive: legLive,
            RepathBudget: LootTravelDispatch.ApproachRepathBudget));
        var verdict = LootTravelDispatch.DecideApproach(
            brain, prepared.Inputs.ContainerInRange, legLive,
            liveMove ? "retrack" : driftText, driftText);
        var travel = LootTravelDispatch.Describe(verdict);
        // This caller serves exactly ONE verb — the closing Move. The seam's Stop arm
        // (the arrival halt) is deliberately NOT dispatched here, because arrival at
        // this leg's radius is precisely when <c>GameplayActor.Loot</c> becomes legal:
        // the take fires on that wake instead, so a halt the caller issued would be a
        // leg nobody needs. Anything else (a hold, a named terminal, a verb this leg
        // cannot serve) dispatches nothing and still names WHY.
        if (verdict.Verb != LootTravelDispatch.ApproachVerb.MoveToUnit)
            return new LootApproachWake(null, verdict.DispatchToken, verdict.Reason, travel);

        var payload = new LootTravelDispatch.LootApproachDispatchParams(verdict.Decision);
        return new LootApproachWake(
            new BotDecisionProposal(
                goal: LootGoal,
                action: ActorActionType.Move,
                targetId: corpseObjId,
                expectedPostcondition: new BotProposalPostcondition(
                    $"corpse approach toward quest {fixture.QuestId} corpse {corpseObjId} dispatched",
                    _ => true),
                idempotencyKey: $"{opts.CycleId}:loot-approach:{fixture.QuestId}:{corpseObjId}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {fixture.QuestId} corpse approach: prey {fixture.PreyTemplate} {corpseObjId} out of loot range — closing ({verdict.Reason})",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectiveLootPriority,
                tieBreakKey: $"loot-approach:{fixture.QuestId}:{corpseObjId:D10}",
                payload: payload,
                hardPreconditions: LootApproachPreconditions(fixture)),
            verdict.DispatchToken,
            verdict.Reason,
            travel);
    }

    /// <summary>
    /// The approach leg's preconditions: the SAME quest-relevance gate every other
    /// objective leg of this quest carries (the quest is still active and its prey
    /// item is still missing), so the walk is withdrawn the moment the corpse stops
    /// being this quest's work.
    /// </summary>
    private static BotProposalPrecondition[] LootApproachPreconditions(QuestFixtureRow fixture)
        =>
        [
            new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture))
        ];

    /// <summary>
    /// The approach leg's own reading of the leg IT dispatched (the same discipline
    /// <c>PursuitLegState</c> keeps, with this leg's own owner tag and the shared
    /// <see cref="TravelBrain.LegDriftToleranceM"/> window): whether a Move leg of
    /// ours is live at all, and — when there is one — whether it still serves the
    /// corpse (motion since the last issue within the retrack window). A corpse does
    /// not walk, so the drift reading is normally 0.0 m and the hold is the steady
    /// state; the window exists so a corpse that WAS moved (a rig, a GM, a future
    /// haul) re-issues rather than being walked to a stale point.
    /// </summary>
    private static (bool LiveMove, bool LegLive, string DriftText) LootApproachLegState(
        IGameplayActor actor, uint corpseObjId)
    {
        var liveMove = LootTravelDispatch.IsOurLiveLeg(actor.ActiveRequest, corpseObjId);
        var driftText = "fresh";
        var legLive = liveMove;
        lock (PursuitSync)
        {
            if (LastApproachIssue.TryGetValue(actor.ActorId, out var last) && last.TargetObjId == corpseObjId)
            {
                var corpsePos = actor.Character?.ParentWorld?.GetNpc(corpseObjId)?.Transform.World.Position;
                if (corpsePos.HasValue)
                {
                    var drift = Vector3.Distance(last.TargetPos, corpsePos.Value);
                    driftText = $"{drift.ToString("F1", CultureInfo.InvariantCulture)}m";
                    legLive = liveMove && drift <= TravelBrain.LegDriftToleranceM;
                }
            }
        }
        return (liveMove, legLive, driftText);
    }

    /// <summary>
    /// G7c corpse-approach dispatch: preempt a live Move leg the leg itself is
    /// replacing (owner-staged so the retrack preemption is recognised as the
    /// caller's OWN retirement and never spends the journey's repath budget), issue
    /// the unit-relative <c>MoveToUnit</c> on the pinned corpse, then bank the leg
    /// that actually ran. Never touches the loot-once memory, the ledger, or the
    /// take — those belong to the Loot arm alone, which fires on the wake after this
    /// leg lands (the actor is idle again, and inside the engine's loot range).
    /// </summary>
    private static ActorRequest DispatchLootApproachMove(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        if (gameplayActor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            gameplayActor.PreemptCurrent(TravelLegDispatch.LootApproachRetrackDetail);
        // Telemetry: this leg owns as LOOT_APPROACH_MOVE_TO_UNIT (staged before
        // dispatch; PreemptCurrent above carries no request so the stage survives).
        if (gameplayActor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(LootTravelDispatch.ApproachMoveOwner);
        var request = gameplayActor.MoveToUnit(proposal.TargetId, PursuitSpeedMps, LootApproachLegTimeout, proposal.IdempotencyKey);
        LootTravelDispatch.PublishDispatched(gameplayActor,
            proposal.Payload is LootTravelDispatch.LootApproachDispatchParams p ? p.Decision : null);
        var corpsePos = gameplayActor.Character?.ParentWorld?.GetNpc(proposal.TargetId)?.Transform.World.Position;
        if (corpsePos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastApproachIssue.Count >= 256)
                    LastApproachIssue.Clear();
                LastApproachIssue[gameplayActor.ActorId] = (proposal.TargetId, corpsePos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// G8b return proposal (the wired quest only): Move/Stop/InteractNpc for the
    /// Ready-step reporter at <c>ObjectiveReturnPriority</c> (below
    /// pursuit/combat/loot, above advance — the existing layout). Per-wake
    /// live revalidation: the fixture quest active + Ready, reporter resolved per
    /// wake via GetNpcByTemplateId(fixture reporter) (never stored) + template +
    /// flat distance.
    ///
    /// TRAVEL BRAIN (the FIRST live dispatch caller): this leg's closing leg is
    /// decided by <see cref="TravelBrain.Decide"/> through
    /// <see cref="TravelLegDispatch"/> — the journey is armed once per reporter and
    /// then the brain owns the verb, the drift hold, the repath budget, and the
    /// named abandonment terminals (WrongWorld / TargetGone / Unreachable, never a
    /// bare navigation failure). The leg keeps its own vocabulary and its own
    /// dispatch shapes: the SAME <c>MoveToUnit</c> on the live reporter objId
    /// (owner <c>RETURN_MOVE_TO_UNIT</c>), the SAME audited <c>Stop</c> inside the
    /// 25 m InteractNpc gate, the SAME settled <c>InteractNpc</c> dialogue, and the
    /// SAME <c>dispatch=</c>/<c>reason=</c> lane tokens; the brain's decision rides
    /// additively in a <c>:travel=</c> fragment. The pre-brain rule is still the
    /// fallback whenever no journey can be armed (an actor with no object identity),
    /// and the journey is ended at this leg's own boundary (the quest stopped being
    /// Ready, or the reporter identity is gone) so a banked verdict cannot outlive
    /// the reason it was reached.
    ///
    /// Stop hold-confirmed (landed Stop + poses within 0.5 m) → InteractNpc
    /// (dialogue fallback expected — the quest carries no talk-family objective, so
    /// Talk would void-reject; never Talk). Withdraws (null + named diag) when the
    /// quest is not Ready, the reporter is lost/recycled, or the travel journey
    /// reached a terminal. Never TurnIn — the TurnIn proposal stays a separate
    /// competitor (and the withhold seam still guards it), so withhold can never
    /// swallow the return leg. Never Cast, AutoAttack, Loot, or credit.
    /// </summary>
    internal static BotDecisionProposal? ReturnEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var opts = context.Options;
        var fixture = context.Fixture!;
        static string M(double value)
            => double.IsNaN(value) ? "NA" : value.ToString("F1", CultureInfo.InvariantCulture);
        var character = actor.Character;
        if (character?.Quests?.ActiveQuests.GetValueOrDefault(fixture.QuestId) is not { Status: QuestStatus.Ready })
        {
            // The quest no longer needs this leg: the journey is over (a banked
            // verdict must not outlive it), and the next Ready wake arms a fresh one.
            // Owner-tagged: this boundary drops the RETURN leg's journey only, never
            // the pursuit leg's armed one.
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.ReturnMoveOwner);
            diag = "validate=not-ready:reporter=-:template=-:flatM=NA:dist3D=NA:dispatch=withdrawn:reason=not-ready";
            return null;
        }
        var actorPos = character?.Transform.World.Position;
        var reporter = character?.ParentWorld?.GetNpcByTemplateId(fixture.ReporterTemplate);
        if (character == null || !actorPos.HasValue || character.ParentWorld == null || reporter == null)
        {
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.ReturnMoveOwner);
            diag = $"validate=FAIL-reporter-lost:reporter=-:template={fixture.ReporterTemplate}:flatM=NA:dist3D=NA:dispatch=withdrawn:reason=reporter-lost";
            return null;
        }
        if (reporter.TemplateId != fixture.ReporterTemplate)
        {
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.ReturnMoveOwner);
            diag = $"validate=FAIL-reporter-recycled:reporter={reporter.ObjId}:template={reporter.TemplateId}:flatM=NA:dist3D=NA:dispatch=withdrawn:reason=reporter-recycled";
            return null;
        }
        var reporterPos = reporter.Transform.World.Position;
        var flat = MathUtil.CalculateDistance(actorPos.Value, reporterPos, false);
        var dist3 = MathUtil.CalculateDistance(actorPos.Value, reporterPos, true);

        // ------------------------------------------------------- TRAVEL DECISION
        // What this leg dispatched (its own reading of its own leg — the brain never
        // reads an actor request), then what the travel chain asks for this wake.
        var legOutcome = TravelLegDispatch.MapLegOutcome(actor, reporter.ObjId, TravelLegDispatch.ReturnMoveOwner);
        var (liveMove, legLive, driftText) = ReturnLegState(actor, reporter.ObjId, reporterPos);
        var brain = TravelLegDispatch.Prepare(
            actor, reporter.ObjId, reporterPos, ReturnInteractRadiusM, legLive, legOutcome,
            legOwner: TravelLegDispatch.ReturnMoveOwner);
        var verdict = TravelLegDispatch.DecideReturnLeg(
            brain, flat <= ReturnInteractRadiusM,
            ReturnHoldConfirmed(actor, reporter.ObjId, actorPos.Value, reporterPos),
            legLive, liveMove ? "retrack" : driftText, driftText);
        // Flat, bracket-free additive fragment: the brain's own arm/verb/terminal/
        // reason beside the leg's unchanged tokens (a lane parser that scans
        // :dispatch= sees exactly what it saw before).
        var travelFrag = brain is { } prepared
            ? $":travel={prepared.Decision.Describe()}:routed=true"
            : ":travel=fallback";
        var prefix = $"validate=ok:reporter={reporter.ObjId}:template={fixture.ReporterTemplate}" +
                     $":flatM={M(flat)}:dist3D={M(dist3)}";
        var idempotencyKey = verdict.Verb switch
        {
            TravelLegDispatch.ReturnLegVerb.InteractNpc => $"quest:{actor.ActorId}:{opts.CycleId}:return-interact:{fixture.QuestId}:{reporter.ObjId}",
            TravelLegDispatch.ReturnLegVerb.Stop => $"quest:{actor.ActorId}:{opts.CycleId}:return-stop:{fixture.QuestId}:{reporter.ObjId}",
            _ => $"quest:{actor.ActorId}:{opts.CycleId}:return:{fixture.QuestId}:{reporter.ObjId}"
        };
        var payload = new TravelLegDispatch.TravelDispatchParams(verdict.Decision);

        switch (verdict.Verb)
        {
            case TravelLegDispatch.ReturnLegVerb.InteractNpc:
                // Hold-confirm (the G6 discipline, without the assignment gate —
                // return never assigns): a Stop already landed for this reporter
                // at these poses stays landed — withdraw so the InteractNpc
                // proposal can win a settled wake. Any actor/reporter motion
                // re-arms Stop (range-hold wins). Without the withdraw, Stop
                // would win every in-range wake and InteractNpc could never fire;
                // without Stop-first, a live leg busy-rejects InteractNpc.
                diag = $"{prefix}:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{travelFrag}";
                return new BotDecisionProposal(
                    goal: ReturnGoal,
                    action: ActorActionType.InteractNpc,
                    targetId: reporter.ObjId,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"dialogue with quest {fixture.QuestId} reporter {reporter.ObjId} delivered",
                        _ => true),
                    idempotencyKey: idempotencyKey,
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: $"quest {fixture.QuestId} return: live reporter {fixture.ReporterTemplate} ({reporter.ObjId}) at {M(flat)}m flat (inside {ReturnInteractRadiusM:F1}m gate), settled — InteractNpc dialogue",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.ObjectiveReturnPriority,
                    tieBreakKey: $"return:{fixture.QuestId}:{reporter.ObjId:D10}",
                    payload: payload,
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("quest-active",
                            observed => observed.ActiveQuestIds.Contains(fixture.QuestId))
                    ]);
            case TravelLegDispatch.ReturnLegVerb.Stop:
                diag = $"{prefix}:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{travelFrag}";
                return new BotDecisionProposal(
                    goal: ReturnGoal,
                    action: ActorActionType.Stop,
                    targetId: reporter.ObjId,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"holding at {M(flat)}m off quest {fixture.QuestId} reporter {reporter.ObjId} (stop-before-interact)",
                        _ => true),
                    idempotencyKey: idempotencyKey,
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: $"quest {fixture.QuestId} return: live reporter {fixture.ReporterTemplate} ({reporter.ObjId}) at {M(flat)}m flat (inside {ReturnInteractRadiusM:F1}m gate) — hold, no interact yet",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.ObjectiveReturnPriority,
                    tieBreakKey: $"return:{fixture.QuestId}:{reporter.ObjId:D10}",
                    payload: payload,
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("quest-active",
                            observed => observed.ActiveQuestIds.Contains(fixture.QuestId))
                    ]);
            case TravelLegDispatch.ReturnLegVerb.MoveToUnit:
                diag = $"{prefix}:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{travelFrag}";
                return new BotDecisionProposal(
                    goal: ReturnGoal,
                    action: ActorActionType.Move,
                    targetId: reporter.ObjId,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"return leg toward quest {fixture.QuestId} reporter {reporter.ObjId} dispatched",
                        _ => true),
                    idempotencyKey: idempotencyKey,
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: $"quest {fixture.QuestId} return: live reporter {fixture.ReporterTemplate} ({reporter.ObjId}) at {M(flat)}m flat (outside {ReturnInteractRadiusM:F1}m gate), drift {driftText} — closing",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.ObjectiveReturnPriority,
                    tieBreakKey: $"return:{fixture.QuestId}:{reporter.ObjId:D10}",
                    payload: payload,
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("quest-active",
                            observed => observed.ActiveQuestIds.Contains(fixture.QuestId))
                    ]);
            default:
                // Held (a live leg keeps its progress) or Withdrawn (a named travel
                // terminal, or a verb this leg does not serve): no proposal, and the
                // diag still names the reporter, the reason, and the terminal.
                diag = $"{prefix}:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{travelFrag}";
                return null;
        }
    }

    /// <summary>
    /// The pursuit leg's own reading of the leg IT dispatched (the same discipline
    /// <see cref="ReturnLegState"/> keeps, with the pursuit's own owner tag and
    /// <see cref="PursuitRetrackDriftM"/> window): whether a Move leg of ours is live
    /// at all, and — when there is one — whether it still serves the prey (motion
    /// since the last issue under the retrack window). <c>driftText</c> is the
    /// caller's own drift reading (<c>fresh</c> before any issue).
    /// </summary>
    private static (bool LiveMove, bool LegLive, string DriftText) PursuitLegState(
        IGameplayActor actor, uint preyObjId, Vector3 preyPos)
    {
        var liveMove = TravelLegDispatch.IsOurLiveLeg(
            actor.ActiveRequest, preyObjId, TravelLegDispatch.PursuitMoveOwner);
        var driftText = "fresh";
        var legLive = liveMove;
        lock (PursuitSync)
        {
            if (LastPursuitIssue.TryGetValue(actor.ActorId, out var last) && last.TargetObjId == preyObjId)
            {
                var drift = Vector3.Distance(last.TargetPos, preyPos);
                driftText = $"{drift.ToString("F1", CultureInfo.InvariantCulture)}m";
                legLive = liveMove && drift <= PursuitRetrackDriftM;
            }
        }
        return (liveMove, legLive, driftText);
    }

    /// <summary>
    /// The return leg's own reading of the leg IT dispatched (the pursuit
    /// discipline, kept for this caller's lane vocabulary): whether a Move leg of
    /// ours is live at all, and — when there is one — whether it still serves the
    /// reporter (motion since the last issue under
    /// <see cref="PursuitRetrackDriftM"/>). <c>driftText</c> is the caller's own
    /// drift reading (<c>fresh</c> before any issue).
    /// </summary>
    private static (bool LiveMove, bool LegLive, string DriftText) ReturnLegState(
        IGameplayActor actor, uint reporterObjId, Vector3 reporterPos)
    {
        var liveMove = TravelLegDispatch.IsOurLiveLeg(
            actor.ActiveRequest, reporterObjId, TravelLegDispatch.ReturnMoveOwner);
        var driftText = "fresh";
        var legLive = liveMove;
        lock (PursuitSync)
        {
            if (LastReturnIssue.TryGetValue(actor.ActorId, out var last) && last.TargetObjId == reporterObjId)
            {
                var drift = Vector3.Distance(last.TargetPos, reporterPos);
                driftText = $"{drift.ToString("F1", CultureInfo.InvariantCulture)}m";
                legLive = liveMove && drift <= PursuitRetrackDriftM;
            }
        }
        return (liveMove, legLive, driftText);
    }

    /// <summary>
    /// The return leg's settled-halt memory: a Stop already landed for this reporter
    /// at (nearly) these poses. Read-only over the shared hold memory.
    /// </summary>
    private static bool ReturnHoldConfirmed(
        IGameplayActor actor, uint reporterObjId, Vector3 actorPos, Vector3 reporterPos)
    {
        lock (PursuitSync)
        {
            return LastStopHold.TryGetValue(actor.ActorId, out var hold)
                   && hold.TargetObjId == reporterObjId
                   && Vector3.Distance(hold.ActorPos, actorPos) <= 0.5f
                   && Vector3.Distance(hold.TargetPos, reporterPos) <= 0.5f;
        }
    }

    /// <summary>
    /// G8c turn-in release: the wake-scoped TurnIn suppression lifts exactly
    /// when the return leg has converged — the live return proposal is the
    /// settled InteractNpc AND a Completed InteractNpc to the same reporter
    /// already sits in the actor's audit trace (every action emits exactly one
    /// record on its terminal transition). Move/Stop proposals stay suppressed
    /// (outside range / en route / unsettled), and the first settled wake stays
    /// suppressed so its InteractNpc still lands; the release fires on the next
    /// settled wake, when TurnIn (priority 30) outranks the repeat InteractNpc.
    /// A Rejected/timed-out InteractNpc never converges (leg retries cleanly).
    /// Read-only audit scan, bounded trace — never dispatches, never mutates.
    /// </summary>
    internal static bool ReturnExit(IGameplayActor actor, BotDecisionProposal returnProposal)
    {
        if (returnProposal.Goal != ReturnGoal || returnProposal.Action != ActorActionType.InteractNpc)
            return false;
        return actor.AuditTrace.Any(r => r.Action == ActorActionType.InteractNpc
            && r.TargetId == returnProposal.TargetId
            && r.Result == ActorLifecycleState.Completed);
    }

    /// <summary>
    /// G4 funnel diagnostics (additive, bounded): one Info line per quest wake
    /// carrying the wired quest's objective→target funnel — objective/source resolution,
    /// relevance, raw/relevant/legal tallies, selection + template + distance,
    /// per-predicate reject counts, ordered candidate rows, the CycleId join
    /// key, plus the G5 pursuit / G6 combat / G7c loot / G8b return outcomes
    /// (each leg's own validate/range/dispatch diag fragment, read back from the
    /// wake's evidence). Values carry no spaces so the line stays greppable. No
    /// behavior input; Info (not Debug) so the lane file target records it.
    /// </summary>
    private static void LogObjectiveFunnel(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel, QuestLegWake wake)
    {
        static string Num(double value)
            => double.IsNaN(value) ? "NA" : value.ToString("F1", CultureInfo.InvariantCulture);
        var rows = funnel.Candidates.Select(c =>
            $"{c.ObjId}:{c.TemplateId}:{(c.QuestRelevant ? 1 : 0)}:{(c.CombatLegal ? 1 : 0)}:{Num(c.DistanceM)}:{c.Reject}");
        SweepDiagLog.Info(
            "QuestObjectiveTargetDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} objective=[{Objective}] source=[{Source}] " +
            "relevance={Relevance} have={Have} need={Need} raw={Raw} relevant={Relevant} legal={Legal} " +
            "selected={Selected} template={Template} distM={Dist} " +
            "rejects=[unresolved={Unresolved} dead={Dead} template={TemplateRejects} hostile={Hostile} attack={Attack} stealth={Stealth}] " +
            "candidates=[{Candidates}] pursuit=[{Pursuit}] combat=[{Combat}] loot=[{Loot}] return=[{Return}]",
            opts.CycleId, actor.Character.Id, actor.ActorId, funnel.QuestId,
            funnel.ObjectiveResolved ? $"ok:{funnel.ObjectiveDetail}" : $"FAIL-OBJECTIVE:{funnel.ObjectiveDetail}",
            funnel.SourceResolved ? $"ok:{funnel.SourceDetail}" : $"FAIL-SOURCE:{funnel.SourceDetail}",
            funnel.Relevance ? "true" : "false", funnel.HaveCount, funnel.NeedCount,
            funnel.RawCount, funnel.RelevantCount, funnel.LegalCount,
            funnel.SelectedObjId == 0 ? "-" : funnel.SelectedObjId.ToString(CultureInfo.InvariantCulture),
            funnel.SelectedTemplateId == 0 ? "-" : funnel.SelectedTemplateId.ToString(CultureInfo.InvariantCulture),
            Num(funnel.SelectedDistanceM),
            funnel.RejectUnresolved, funnel.RejectDead, funnel.RejectTemplate,
            funnel.RejectHostile, funnel.RejectAttack, funnel.RejectStealth,
            string.Join(",", rows),
            wake.Detail(QuestLegId.Pursuit), wake.Detail(QuestLegId.Combat),
            wake.Detail(QuestLegId.Loot), wake.Detail(QuestLegId.Return));
    }

    /// <summary>
    /// Stage 4 failed-plan diagnostics (additive, bounded): one Info line per
    /// failed plan per wake, so a quest whose plan failed its fixture gate is
    /// visible in the lane even when other work won the wake. Reads only the
    /// plan's own already-computed failure. Info (not Debug) so the lane file
    /// target records it; values carry no spaces.
    /// </summary>
    private static void LogPlanFailure(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, QuestPlanFailure failure)
    {
        SweepDiagLog.Info(
            "QuestPlanDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} plan=failed stage={Stage} reason=[{Reason}]",
            opts.CycleId, actor.Character?.Id ?? 0, actor.ActorId, failure.QuestId,
            failure.Stage, failure.Reason.Replace(' ', '_'));
    }

    /// <summary>
    /// The bootstrap-floor plan whose objective shape this vocabulary cannot
    /// serve yet: one Info line per quest per wake naming the act it could not
    /// serve, so the gap is visible in the lane (never a silent skip) even
    /// though the plan itself keeps the work the step machine can do.
    /// </summary>
    private static void LogUnservedPlan(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, QuestPlan plan)
    {
        SweepDiagLog.Info(
            "QuestPlanDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} plan=bootstrap unserved=[{Unserved}]",
            opts.CycleId, actor.Character?.Id ?? 0, actor.ActorId, plan.QuestId,
            plan.Unserved.Replace(' ', '_'));
    }

    /// <summary>
    /// True when the plan carries an objective SLICE whose own resolution the
    /// give-up rule tracks (branch 2) — i.e. it has legs marked
    /// <see cref="QuestLeg.NeedsFunnel"/>. Such a plan's streak is owned by the
    /// funnel outcome, not by the plan-success path, so the plan loop must not
    /// clear it early.
    /// </summary>
    private static bool HasObjectiveSlice(QuestPlan plan)
    {
        foreach (var leg in plan.Legs)
        {
            if (leg.NeedsFunnel)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The named reason a plan's objective slice resolved no target this wake,
    /// or "" when it resolved one. The vocabulary is the slice's own — the SAME
    /// fail-closed predicates the funnel already reports (objective resolution,
    /// source resolution, selection) — so a give-up cites the real missing
    /// piece and never a phrase this loop invented.
    /// </summary>
    private static string UnresolvedTargetReason(uint questId, QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel)
    {
        if (!funnel.ObjectiveResolved)
            return $"GIVE-UP/UNRESOLVED-TARGET quest={questId} stage=OBJECTIVE detail={funnel.ObjectiveDetail}";
        if (!funnel.SourceResolved)
            return $"GIVE-UP/UNRESOLVED-TARGET quest={questId} stage=SOURCE detail={funnel.SourceDetail}";
        if (funnel.SelectedObjId == 0)
            return $"GIVE-UP/UNRESOLVED-TARGET quest={questId} stage=SELECT detail=no-target raw={funnel.RawCount} relevant={funnel.RelevantCount}";
        return "";
    }
    private static ActorRequest Dispatch(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        return proposal.Action switch
        {
            ActorActionType.AdvanceQuest => gameplayActor.AdvanceQuest(
                proposal.TargetId, proposal.IdempotencyKey),
            ActorActionType.AcceptQuest when proposal.Payload is (QuestOffering offering, uint _) => gameplayActor.AcceptQuest(
                proposal.TargetId, offering.AcceptorType, offering.AcceptorId, proposal.IdempotencyKey),
            ActorActionType.TurnInQuest when proposal.Payload is QuestTurnInParams turnIn => gameplayActor.TurnInQuest(
                proposal.TargetId, turnIn.TargetObjId, turnIn.SelectedReward, proposal.IdempotencyKey),
            ActorActionType.TurnInDoodad when proposal.Payload is QuestTurnInParams turnInDoodad => gameplayActor.TurnInAtDoodad(
                proposal.TargetId, turnInDoodad.TargetObjId, turnInDoodad.SelectedReward, proposal.IdempotencyKey),
            ActorActionType.AutoTurnIn when proposal.Payload is QuestTurnInParams auto => gameplayActor.AutoTurnInQuest(
                proposal.TargetId, auto.SelectedReward, proposal.IdempotencyKey),
            // G4 objective→target: assignment rides the canonical SetTarget verb
            // only — never a direct CurrentTarget write, never the roam bypass.
            ActorActionType.Target => gameplayActor.SetTarget(proposal.TargetId),
            // G5 pursuit: unit-relative close-in rides MoveToUnit (externally
            // reachable, quest-pinned — the roam hunt-leg shape, brain-owned
            // per-wake revalidation, never in-leg tracking). A live Move leg
            // is preempted first (roam :787-789 precedent, public verb) so a
            // retrack never busy-rejects against our own leg. Stop rides the
            // audited Stop halt. Goal-guarded so no other Move/Stop proposal
            // can ever route here.
            ActorActionType.Move when proposal.Goal == PursuitGoal => DispatchPursuitMove(gameplayActor, proposal),
            ActorActionType.Stop when proposal.Goal == PursuitGoal => DispatchPursuitStop(gameplayActor, proposal),
            // G8b return: the quest-owned return leg rides MoveToUnit on the
            // live reporter objId (the pursuit shape, RETURN_MOVE_TO_UNIT
            // owner), the audited Stop halt inside 25 m, and InteractNpc once
            // settled (dialogue fallback expected — 251 carries no talk-family
            // objective, so Talk would void-reject; never Talk). Goal-guarded
            // so no other Move/Stop/InteractNpc proposal can ever route here.
            ActorActionType.Move when proposal.Goal == ReturnGoal => DispatchReturnMove(gameplayActor, proposal),
            ActorActionType.Stop when proposal.Goal == ReturnGoal => DispatchReturnStop(gameplayActor, proposal),
            ActorActionType.InteractNpc when proposal.Goal == ReturnGoal => gameplayActor.InteractNpc(proposal.TargetId, proposal.IdempotencyKey),
            // G6 combat: the quest-owned combat action rides the live actor's
            // AutoAttack verb (no queue kind exists for it by design — dispatched
            // like the roam hunt leg does), and the CombatBrain increment adds the
            // brain's OWN verbs alongside it: the heal consumable (UseItem, self
            // targeted), a skill cast (Cast on the committed objId), and the
            // spacing/retreat move (Move, destination staged by the brain).
            // Goal-guarded so no other proposal can route here.
            ActorActionType.AutoAttack when proposal.Goal == CombatGoal => DispatchCombatAutoAttack(gameplayActor, proposal),
            ActorActionType.Cast when proposal.Goal == CombatGoal => DispatchCombatCast(gameplayActor, proposal),
            ActorActionType.UseItem when proposal.Goal == CombatGoal => DispatchCombatHeal(gameplayActor, proposal),
            ActorActionType.Move when proposal.Goal == CombatGoal => DispatchCombatMove(gameplayActor, proposal),
            // Item-use objective: the quest-owned consume rides the live actor's
            // UseItem verb (the exact SkillItem caster branch the COMBAT-01 heal
            // gate proved), goal-guarded so no other UseItem proposal — the
            // combat heal's included — can ever route here.
            ActorActionType.UseItem when proposal.Goal == UseItemGoal => gameplayActor.UseItem(proposal.TargetId, 0, proposal.IdempotencyKey),
            // G7c loot: the quest-owned corpse take rides the live actor's
            // Loot verb only (the exact CSLootOpenBagPacket lootAll call).
            // Goal-guarded so no other Loot proposal can ever route here.
            // Gather-from-doodad: the quest-owned well draw rides MoveTo on the
            // doodad's live POSITION (a doodad is BaseUnit, never Unit — the
            // position leg is the roam butcher-approach shape), the audited Stop
            // halt inside 25 m, and Interact with the row's own GatherUseSkill
            // once settled (the use skill read off the well's func tables; a
            // skill-0 row stays skill-less; no new actor verb).
            // Goal-guarded so no other Move/Stop/Interact proposal can ever
            // route here.
            ActorActionType.Move when proposal.Goal == GatherGoal => DispatchGatherMove(gameplayActor, proposal),
            ActorActionType.Stop when proposal.Goal == GatherGoal => DispatchGatherStop(gameplayActor, proposal),
            ActorActionType.Interact when proposal.Goal == GatherGoal => gameplayActor.Interact(proposal.TargetId, proposal.SkillId, proposal.IdempotencyKey),
        };
    }

    /// <summary>
    /// G5 pursuit dispatch: preempt a live Move leg (retrack only), issue the
    /// unit-relative MoveToUnit leg, and record the committed target's live
    /// position for the next wake's drift gate. Never touches _move state or
    /// queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchPursuitMove(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        if (gameplayActor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            gameplayActor.PreemptCurrent(TravelLegDispatch.PursuitRetrackDetail);
        // Telemetry: this leg owns as PURSUIT_MOVE_TO_UNIT (staged before
        // dispatch; PreemptCurrent above carries no request so the stage
        // survives to the MoveToUnit below).
        if (gameplayActor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(TravelLegDispatch.PursuitMoveOwner);
        var request = gameplayActor.MoveToUnit(proposal.TargetId, PursuitSpeedMps, PursuitLegTimeout, proposal.IdempotencyKey);
        // Travel brain: the leg that ACTUALLY ran is the one banked (its mode and
        // the point it was issued against — the next wake's drift comparison — plus
        // the repath it spent), read from the decision the proposal carries. A
        // fallback proposal (no journey armed) carries none and banks nothing.
        TravelLegDispatch.PublishDispatched(gameplayActor,
            proposal.Payload is TravelLegDispatch.TravelDispatchParams travel ? travel.Decision : null,
            TravelLegDispatch.PursuitMoveOwner);
        var npcPos = gameplayActor.Character?.ParentWorld?.GetNpc(proposal.TargetId)?.Transform.World.Position;
        if (npcPos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastPursuitIssue.Count >= 256)
                    LastPursuitIssue.Clear();
                LastPursuitIssue[gameplayActor.ActorId] = (proposal.TargetId, npcPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// G5 pursuit Stop dispatch (G6 hold-confirm): issue the audited Stop
    /// halt, then record both poses so the next wake's Stop proposal can
    /// confirm the hold and yield to combat. Never touches _move state or
    /// queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchPursuitStop(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var request = gameplayActor.Stop();
        var character = gameplayActor.Character;
        var actorPos = character?.Transform.World.Position;
        var npcPos = character?.ParentWorld?.GetNpc(proposal.TargetId)?.Transform.World.Position;
        if (actorPos.HasValue && npcPos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastStopHold.Count >= 256)
                    LastStopHold.Clear();
                LastStopHold[gameplayActor.ActorId] = (proposal.TargetId, actorPos.Value, npcPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// G8b return dispatch: preempt a live Move leg (retrack only), issue the
    /// unit-relative MoveToUnit leg on the live reporter objId, and record the
    /// reporter's live position for the next wake's drift gate. The pursuit
    /// shape with the RETURN_MOVE_TO_UNIT owner tag; reuses pursuit speed,
    /// budget, and drift disciplines. Never touches _move state or queue
    /// kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchReturnMove(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        if (gameplayActor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            gameplayActor.PreemptCurrent(TravelLegDispatch.ReturnRetrackDetail);
        // Telemetry: this leg owns as RETURN_MOVE_TO_UNIT (staged before
        // dispatch; PreemptCurrent above carries no request so the stage
        // survives to the MoveToUnit below).
        if (gameplayActor is GameplayActor concrete)
            concrete.SetPendingMoveOwner("RETURN_MOVE_TO_UNIT");
        var request = gameplayActor.MoveToUnit(proposal.TargetId, PursuitSpeedMps, PursuitLegTimeout, proposal.IdempotencyKey);
        // Travel brain: the leg that ACTUALLY ran is the one banked (the mode and
        // the point it was issued against — the next wake's drift comparison — plus
        // the repath it spent), read from the decision the proposal carries. A
        // fallback proposal (no journey armed) carries none and banks nothing.
        TravelLegDispatch.PublishDispatched(gameplayActor,
            proposal.Payload is TravelLegDispatch.TravelDispatchParams travel ? travel.Decision : null,
            TravelLegDispatch.ReturnMoveOwner);
        var npcPos = gameplayActor.Character?.ParentWorld?.GetNpc(proposal.TargetId)?.Transform.World.Position;
        if (npcPos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastReturnIssue.Count >= 256)
                    LastReturnIssue.Clear();
                LastReturnIssue[gameplayActor.ActorId] = (proposal.TargetId, npcPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// G8b return Stop dispatch (hold-confirm): issue the audited Stop halt,
    /// then record both poses in the shared hold memory so the next wake's
    /// return proposal can confirm the hold and yield to InteractNpc. Shares
    /// <c>LastStopHold</c> with pursuit — entries are (actor, target) keyed by
    /// check, and the reporter objId never equals a pursuit target. Never
    /// touches _move state or queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchReturnStop(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var request = gameplayActor.Stop();
        var character = gameplayActor.Character;
        var actorPos = character?.Transform.World.Position;
        var npcPos = character?.ParentWorld?.GetNpc(proposal.TargetId)?.Transform.World.Position;
        if (actorPos.HasValue && npcPos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastStopHold.Count >= 256)
                    LastStopHold.Clear();
                LastStopHold[gameplayActor.ActorId] = (proposal.TargetId, actorPos.Value, npcPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// G6/CombatBrain spacing dispatch: the brain's own Move verb (close in,
    /// back off, retreat). This arm composes the ordered public-verb teardown
    /// itself — a live auto-attack loop stops FIRST (it would keep re-aggroing
    /// the mob the bot is walking away from, and would keep firing through a
    /// spacing move), then a live Move leg is preempted so this leg never
    /// busy-rejects against our own movement — and then issues the destination
    /// leg, tagged so the telemetry names WHICH spacing leg owns the movement.
    /// Never touches _move state or queue kinds — public actor verbs only.
    ///
    /// TRAVEL BRAIN (the kite's live dispatch caller): the sub-critical spacing
    /// wake is decided by <see cref="CombatTravelDispatch"/> — the journey is
    /// armed once per threat and the travel chain owns the anchor, the drift hold,
    /// the repath budget, and the named abandonment terminals — so the leg's
    /// destination is the chain's own, and the leg that actually ran is banked
    /// through <see cref="CombatTravelDispatch.PublishDispatched"/> under the
    /// kite's own owner tag (<c>COMBAT_KITE_MOVE</c>). Every other spacing move
    /// (close-in, and a kite whose journey could not be armed) carries no travel
    /// decision and keeps the pre-brain behavior byte-identically: the brain's own
    /// destination under the original <c>COMBAT_MOVE_TO</c> owner.
    /// </summary>
    private static ActorRequest DispatchCombatMove(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        // Ordered teardown, the same public-verb shape the pursuit dispatch uses:
        // a live auto-attack loop would keep re-aggroing the mob we are walking
        // away from (and would keep firing through a spacing move), so it stops
        // FIRST; then a live Move leg is preempted so this leg never
        // busy-rejects against our own movement.
        if (gameplayActor.Character?.IsAutoAttack == true)
            gameplayActor.StopAutoAttack($"{proposal.IdempotencyKey}:stop-attack");
        if (gameplayActor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            gameplayActor.PreemptCurrent("combat spacing retrack");
        // The travel decision travels on the payload: a routed kite leg carries one
        // (and owns as COMBAT_KITE_MOVE, so the next wake's leg reading — and the
        // journey's own counters — are scoped to this leg), the fallback carries
        // none (and keeps the pre-brain owner).
        var kiteDecision = proposal.Payload is CombatDispatchParams parameters ? parameters.KiteDecision : null;
        if (gameplayActor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(kiteDecision.HasValue ? CombatTravelDispatch.KiteMoveOwner : "COMBAT_MOVE_TO");
        var destination = proposal.Destination
            ?? gameplayActor.Character?.Transform.World.Position
            ?? Vector3.Zero;
        var request = gameplayActor.MoveTo(destination, PursuitSpeedMps, PursuitLegTimeout, proposal.IdempotencyKey);
        // The leg that ACTUALLY ran is the one banked (its mode and destination —
        // the next wake's drift comparison). A fallback proposal carries no travel
        // decision and banks nothing.
        CombatTravelDispatch.PublishDispatched(gameplayActor, kiteDecision);
        PublishCombatEngagement(gameplayActor, proposal);
        return request;
    }

    /// <summary>
    /// G6/CombatBrain AutoAttack dispatch: start (or re-affirm) the engine's
    /// continuous attack loop on the committed target, then publish the
    /// engagement so the wake's facts (engaged / yield) reflect a verb that
    /// actually landed.
    /// </summary>
    private static ActorRequest DispatchCombatAutoAttack(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var request = gameplayActor.AutoAttack(proposal.TargetId, proposal.IdempotencyKey);
        PublishCombatEngagement(gameplayActor, proposal);
        return request;
    }

    /// <summary>
    /// G6/CombatBrain heal dispatch: the self-targeted consumable use named by the
    /// brain's own decision (the item template travels on the payload).
    /// </summary>
    private static ActorRequest DispatchCombatHeal(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var itemTemplateId = proposal.Payload is CombatDispatchParams p ? p.Decision.ItemTemplateId : 0u;
        var request = gameplayActor.UseItem(itemTemplateId, gameplayActor.ActorId, proposal.IdempotencyKey);
        PublishCombatEngagement(gameplayActor, proposal);
        return request;
    }

    /// <summary>
    /// Publishes the dispatched decision's engagement transition — the ONLY place
    /// the combat facts (engaged / disengaging / survival veto) are written, so a
    /// wake where the combat proposal lost selection never publishes an
    /// engagement it did not act on.
    /// </summary>
    private static void PublishCombatEngagement(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        if (proposal.Payload is not CombatDispatchParams parameters)
            return;
        CombatBrainPlanner.PublishDispatched(gameplayActor, parameters.Decision, DateTime.UtcNow);
    }
    /// <summary>
    /// G6/CombatBrain cast dispatch: one audited cast through the engine's own
    /// skill path. A LANDED cast records the skill on the engagement so the next
    /// wake's rotation continues the combo chain (the engine's own
    /// <c>lastSkillUsed</c> discipline, carried per engagement). A refused cast
    /// records nothing, so the chain never continues from a skill that never
    /// fired.
    /// </summary>
    private static ActorRequest DispatchCombatCast(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var request = gameplayActor.Cast(proposal.SkillId, proposal.TargetId, proposal.IdempotencyKey);
        PublishCombatEngagement(gameplayActor, proposal);
        if (request.State == ActorLifecycleState.Completed)
            CombatBrainPlanner.PublishSkillUsed(gameplayActor.ActorId, proposal.SkillId, DateTime.UtcNow);
        return request;
    }

    /// <summary>
    /// G7c loot dispatch: the quest-owned corpse take through the live actor's
    /// canonical <c>Loot</c> verb (the exact CSLootOpenBagPacket lootAll call)
    /// with the per-corpse idempotency key. A terminal outcome (Completed or
    /// Rejected — the verb ran its course) records the loot-once memory so the
    /// proposal withholds this corpse on every later wake. A non-terminal
    /// outcome (a live leg holds the actor) records nothing, so the next wake
    /// retries cleanly. Public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchLoot(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var request = gameplayActor.Loot(proposal.TargetId, proposal.IdempotencyKey);
        if (request.IsTerminal)
        {
            lock (PursuitSync)
            {
                if (LootedCorpses.Count >= 256)
                    LootedCorpses.Clear();
                LootedCorpses.Add((gameplayActor.ActorId, proposal.TargetId));
            }
            // The decision layer's own bank of the same fact: a take is recorded
            // only for a Loot verb that actually ran its terminal course, so the
            // ledger never claims a take the actor refused.
            LootBrainPlanner.PublishTake(gameplayActor.ActorId, proposal.TargetId, DateTime.UtcNow);
            // The corpse this leg was walking to is DONE: the approach journey is over
            // (owner-scoped, so no other leg's journey on this actor is dropped), and a
            // later re-pin of the same objId arms clean rather than inheriting a banked
            // terminal. The approach leg's own issue memory goes with it.
            LootTravelDispatch.EndJourney(gameplayActor);
            lock (PursuitSync)
                LastApproachIssue.Remove(gameplayActor.ActorId);
        }
        return request;
    }
    /// <summary>
    /// Gather Move dispatch: preempt a live Move leg (retrack only), issue the
    /// position leg on the well's live position (the roam butcher-approach shape
    /// — a doodad is BaseUnit, never Unit, so MoveToUnit cannot resolve it), and
    /// record the well's live position for the next wake's drift gate. Never
    /// touches _move state or queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchGatherMove(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        if (gameplayActor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            gameplayActor.PreemptCurrent(GatherRetrackDetail);
        if (gameplayActor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(GatherMoveOwner);
        var destination = proposal.Destination
            ?? gameplayActor.Character?.ParentWorld?.GetDoodad(proposal.TargetId)?.Transform.World.Position
            ?? gameplayActor.Character?.Transform.World.Position
            ?? Vector3.Zero;
        var request = gameplayActor.MoveTo(destination, PursuitSpeedMps, PursuitLegTimeout, proposal.IdempotencyKey);
        var doodadPos = gameplayActor.Character?.ParentWorld?.GetDoodad(proposal.TargetId)?.Transform.World.Position;
        if (doodadPos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastGatherIssue.Count >= 256)
                    LastGatherIssue.Clear();
                LastGatherIssue[gameplayActor.ActorId] = (proposal.TargetId, doodadPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// Gather Stop dispatch: issue the audited Stop halt, then record both poses
    /// in the shared hold memory so the next wake's gather proposal can confirm
    /// the hold and yield to Interact. Shares <c>LastStopHold</c> with the other
    /// legs — entries are (actor, target) keyed by check, and the well objId
    /// never equals a prey/reporter/corpse target. Never touches _move state or
    /// queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchGatherStop(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        var request = gameplayActor.Stop();
        var character = gameplayActor.Character;
        var actorPos = character?.Transform.World.Position;
        var doodadPos = character?.ParentWorld?.GetDoodad(proposal.TargetId)?.Transform.World.Position;
        if (actorPos.HasValue && doodadPos.HasValue)
        {
            lock (PursuitSync)
            {
                if (LastStopHold.Count >= 256)
                    LastStopHold.Clear();
                LastStopHold[gameplayActor.ActorId] = (proposal.TargetId, actorPos.Value, doodadPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// G7c loot outcome diagnostics (additive, bounded): one Info line when
    /// the loot proposal wins a wake — quest/CycleId/target/template/verb,
    /// the dispatch trace + terminal state + grant (container entries taken)
    /// + detail. The gate joins this grant to the container before/after
    /// tallies for conservation. No behavior input; Info so the lane file
    /// target records it.
    /// </summary>
    private static void LogLootOutcome(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, BotDecisionProposal selected, ActorRequest request, QuestFixtureRow fixture)
    {
        var detail = (request.Detail ?? "").Replace(' ', '_');
        SweepDiagLog.Info(
            "QuestObjectiveLootDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} target={Target} template={Template} verb=Loot " +
            "trace={Trace} state={State} grant={Grant} detail=[{Detail}]",
            opts.CycleId, actor.Character?.Id ?? 0, actor.ActorId, fixture.QuestId, selected.TargetId, fixture.PreyTemplate,
            request.TraceId, request.State, request.Result, TruncateSweep(detail, 200));
    }

    /// <summary>
    /// G6 combat outcome diagnostics (additive, bounded): one Info line when
    /// the combat proposal wins a wake — quest/CycleId/target/template/verb,
    /// the dispatch trace + terminal state + detail, and the authoritative
    /// target HP before (proposal time) → after (post-dispatch) with delta.
    /// Damage lands on attack-delay ticks after the terminal, so hpAfter
    /// usually equals hpBefore here; the gate observes the trailing fall on
    /// later wakes. No behavior input; Info so the lane file target records.
    /// </summary>
    private static void LogCombatOutcome(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, BotDecisionProposal selected, ActorRequest request, int hpBefore, QuestFixtureRow fixture)
    {
        var npc = actor.Character?.ParentWorld?.GetNpc(selected.TargetId);
        var hpAfter = npc?.Hp ?? -1;
        var delta = hpBefore >= 0 && hpAfter >= 0 ? hpAfter - hpBefore : 0;
        var detail = (request.Detail ?? "").Replace(' ', '_');
        SweepDiagLog.Info(
            "QuestObjectiveCombatDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} target={Target} template={Template} verb=AutoAttack " +
            "trace={Trace} state={State} detail=[{Detail}] hpBefore={HpBefore} hpAfter={HpAfter} delta={Delta} targetAlive={Alive}",
            opts.CycleId, actor.Character?.Id ?? 0, actor.ActorId, fixture.QuestId, selected.TargetId,
            npc?.TemplateId ?? 0, request.TraceId, request.State,
            TruncateSweep(detail, 200), hpBefore, hpAfter, delta, npc != null && npc.Hp > 0);
    }
    /// <summary>
    /// Diagnostic-only one-token summary of a per-target DiscoverQuests call:
    /// <c>objId:State:sanitized-detail</c> (spaces become underscores and
    /// square brackets become parens so the token stays parseable inside the
    /// DECIDE bracket — the nested <c>[cands=N reject=...]</c> reject block
    /// would otherwise nest brackets and the funnel parser's LastIndexOf
    /// scan would land on the inner open and lose the funnel). Reads only.
    /// </summary>
    private static string DescribeDiscoverOutcome(ActorRequest discover)
    {
        var detail = (discover.Detail ?? "").Replace(' ', '_').Replace('[', '(').Replace(']', ')');
        if (discover is { IsTerminal: true, State: ActorLifecycleState.Completed })
        {
            if (discover.Result is QuestDiscoveryResult result)
            {
                var offers = result.Offerings.Count == 0
                    ? "offers=-"
                    : "offers=" + string.Join(",", result.Offerings.Take(5).Select(o => $"{o.QuestId}L{o.Level}"))
                      + (result.Offerings.Count > 5 ? $"+{result.Offerings.Count - 5}" : "");
                return $"{discover.TargetId}:Completed:{offers}_{TruncateSweep(detail, 120)}";
            }
            return $"{discover.TargetId}:Completed:NoResult";
        }
        return $"{discover.TargetId}:{discover.State}:{TruncateSweep(detail, 120)}";
    }

    private static string TruncateSweep(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";

    /// <summary>
    /// Bounded fail-result factory, shared with <see cref="QuestDirector.Run"/>
    /// so a wake that throws before the leg loop (perception or plan
    /// assembly) reports the same RUN/FidelityError surface it always has.
    /// </summary>
    internal static QuestDecisionScenario.QuestRunResult Fail(
        string stage, ActorFailureReason reason, string detail,
        IGameplayActor actor,
        ActorActionType? selected,
        IReadOnlyList<BotProposalRejection> rejections,
        ActorRequest? request = null,
        IReadOnlyList<LegEvidence>? legEvidence = null,
        IReadOnlyList<QuestPlanFailure>? planFailures = null,
        IReadOnlyList<QuestGiveUp>? giveUps = null)
        => new()
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = false,
            SelectedAction = selected,
            Request = request,
            Rejections = rejections,
            Explanation = detail,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            LegEvidence = legEvidence ?? [],
            PlanFailures = planFailures ?? [],
            GiveUps = giveUps ?? [],
            TraceRecords = [.. actor.AuditTrace]
        };
}
