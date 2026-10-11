using System.Globalization;
using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.World;
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
    /// <summary>Goal key routing the interact-with-doodad objective's Interact through Dispatch.</summary>
    private const string InteractGoal = "quest.objective-interact";
    /// <summary>Goal key routing the supplied-gather monitor through the leg loop. Never dispatched.</summary>
    private const string DeliverGoal = "quest.objective-deliver";
    /// <summary>Goal key routing the talk objective's Talk through Dispatch.</summary>
    private const string TalkGoal = "quest.objective-talk";
    /// <summary>
    /// Talk arrival radius: the SAME engine gate the Talk verb itself enforces
    /// (GameplayActor.MaxInteractRange, 25 m flat — a conversation outside
    /// interaction range is refused by the actor, not merely wasted). Inside →
    /// audited Stop, then Talk; outside → drift-gated MoveTo. Same discipline
    /// the gather/interact/return legs hold at 25 m.
    /// </summary>
    private const float TalkRadiusM = GameplayActor.MaxInteractRange;
    /// Gather arrival radius: the Interact engine gate itself
    /// (GameplayActor.MaxInteractRange, 25 m flat). Inside → audited Stop,
    /// then Interact; outside → drift-gated MoveTo. The return leg's own
    /// radius discipline at 25 m.
    /// </summary>
    private const float GatherInteractRadiusM = GameplayActor.MaxInteractRange;
    /// <summary>
    /// Interact arrival radius: the SAME Interact engine gate (25 m flat).
    /// Inside → audited Stop, then Interact with the row's own
    /// <c>InteractUseSkill</c>; outside → drift-gated MoveTo.
    /// </summary>
    private const float InteractRadiusM = GameplayActor.MaxInteractRange;
    /// <summary>
    /// The interact leg's movement-owner tag (telemetry + the leg's own
    /// live-leg reading, so the return/pursuit/loot/gather boundaries can never
    /// disarm it and it can never disarm theirs — one actor walks one journey
    /// PER OWNING LEG).
    /// </summary>
    internal const string InteractMoveOwner = "INTERACT_MOVE_TO_UNIT";
    /// <summary>
    /// The preemption detail the interact retrack stages (caller-authored:
    /// retires a leg the caller itself decided to replace, never a navigation
    /// failure).
    /// </summary>
    internal const string InteractRetrackDetail = "quest interact retrack";
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
    /// <summary>
    /// Last-issued interact-doodad position per actor (memory-only wake cache
    /// for the interact leg's own drift reading, the same discipline
    /// <c>LastPursuitIssue</c> keeps). Separate from the other issue memories
    /// so a seedling approach can never entangle prey/reporter/corpse/well
    /// telemetry, and vice versa. Keyed by ActorId; bounded by full clear.
    /// </summary>
    private static readonly Dictionary<uint, (uint TargetObjId, Vector3 TargetPos)> LastInteractIssue = new();
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
    /// No-progress stall streaks per (actor, target): consecutive hold-wakes with no HP
    /// decrease while the pursuit leg yields <c>inside-combat-band</c> or the combat leg
    /// yields <c>already-live</c>. Memory-only wake cache, same discipline as
    /// <c>GiveUpStreaks</c>: keyed by (actor, target), bounded by full clear.
    /// Reset on a self-attributed HP decrease (the actor's own damage landed, so the
    /// hold is working), on target switch/loss/death (a different fight), and on
    /// unreadable HP or unreadable attribution — fail-closed, never forces motion
    /// on unknown state. The per-target streak is the primary signal; the
    /// actor-wide backstop below (<see cref="CombatStallActorWakes"/>) is what
    /// survives a target switch.
    /// </summary>
    private static readonly Dictionary<(uint ActorId, uint TargetObjId), CombatStallStreak> CombatStallStreaks = new();
    /// <summary>One target's stall streak: last seen HP, consecutive no-progress hold-wakes, and the cycle that last counted.</summary>
    private sealed record CombatStallStreak(int Hp, int Wakes, string CycleId);
    /// <summary>
    /// Actor-wide consecutive hold-wakes with no HP decrease: the target-agnostic
    /// backstop behind the per-target stall streak. Every consecutive hold-wake
    /// counts no matter which live target it held on (pure sum across switches),
    /// so rotating N live targets without dealing damage trips the contact
    /// close-in at the same threshold. Reset on a self-attributed same-target HP
    /// decrease (the actor's own damage landed = progress), on target loss/death
    /// (a gone target ends the fight), and on unreadable HP — fail-closed, never
    /// forces motion on unknown state. Deduped by cycle so one wake counts once. No
    /// quest key: the stall is an actor property, never a quest one.
    /// </summary>
    private static readonly Dictionary<uint, CombatStallActorStreak> CombatStallActorWakes = new();
    /// <summary>One actor's stall backstop: the target and HP of the last counted wake, consecutive hold-wakes, and the cycle that last counted.</summary>
    private sealed record CombatStallActorStreak(uint TargetObjId, int Hp, int Wakes, string CycleId);
    /// <summary>
    /// Stall-trip threshold: consecutive no-progress hold-wakes before the pursuit leg stops
    /// yielding and closes to contact. Matches the <c>QuestOptions.GiveUpAfterUnresolvedWakes</c>
    /// default (10): the same "ten stagnant wakes means stuck" convention the give-up rule uses.
    /// </summary>
    internal const int CombatStallWakes = 10;
    /// <summary>Bound for the stall streak map (one entry per actor+target; full clear when exceeded).</summary>
    private const int CombatStallMemoryBound = 512;
    /// <summary>Clears all stall streaks (test seam, mirroring <c>ClearGiveUpMemory</c>). Clears the per-target streaks AND the actor-wide backstop.</summary>
    internal static void ClearCombatStallMemory()
    {
        lock (PursuitSync)
        {
            CombatStallStreaks.Clear();
            CombatStallActorWakes.Clear();
        }
    }
    /// <summary>
    /// Counts one no-progress hold-wake for (actor, target); reports true once the
    /// per-target streak OR the actor-wide backstop reaches <see cref="CombatStallWakes"/>.
    /// Both the pursuit <c>inside-combat-band</c> yield
    /// and the combat <c>already-live</c> gates feed this one streak, deduped by
    /// <paramref name="cycleId"/> so a wake both legs hold counts once — the count is
    /// wakes, not legs. A null/empty cycle skips dedup and counts (only test rigs omit
    /// it; counting is the honest record of the hold observed).
    /// Fail-closed: target 0 or unreadable HP (negative) drops the actor's streaks and
    /// never trips. A self-attributed HP decrease clears the streak (the actor's own
    /// damage landed = progress); a foreign-only delta keeps counting toward the trip.
    /// Unreadable attribution keeps the historical behavior and clears (never a trip
    /// on unknown state).
    /// A different target for the same actor drops the old per-target streak (a
    /// different fight) but never the actor-wide backstop — consecutive hold-wakes
    /// sum across the switch, so rotating live targets without dealing damage
    /// still trips the close-in.
    /// </summary>
    internal static bool CountCombatStallWake(uint actorId, uint targetObjId, int hp, string? cycleId, out int wakes)
        => CountCombatStallWake(actorId, targetObjId, hp, cycleId, out wakes, selfAttributed: null);
    /// <summary>
    /// Attributed stall-wake count: <paramref name="selfAttributed"/> names whether the
    /// observed HP delta carries the actor's own damage (the tagger/contributor read at
    /// the call site — the same evidence <see cref="LootBrain.ClassifyOwnership"/> reads).
    /// True (actor is tagger/contributor) resets on an HP decrease; false (foreign-only
    /// damage) keeps counting toward the trip; null (unreadable attribution) keeps the
    /// historical behavior and resets — fail-closed, never a trip on unknown state.
    /// </summary>
    internal static bool CountCombatStallWake(uint actorId, uint targetObjId, int hp, string? cycleId, out int wakes, bool? selfAttributed)
    {
        var perTargetTripped = CountNoProgressWake(CombatStallStreaks, actorId, targetObjId, hp, cycleId, out wakes, selfAttributed);
        var actorTripped = CountCombatStallActorWake(actorId, targetObjId, hp, cycleId, out var actorWakes, selfAttributed);
        wakes = Math.Max(wakes, actorWakes);
        return perTargetTripped || actorTripped;
    }
    /// <summary>
    /// Counts one stale-pin wake for (actor, target): a Disengaged-at-LeashEdge
    /// wake with unchanged HP. Reports true once the streak reaches
    /// <see cref="CombatStallWakes"/> — the same "ten stagnant wakes means stuck"
    /// convention the stall trip and the give-up rule share. Fed ONLY from the
    /// combat leg's true LeashEdge verdict (never a target switch, never unknown
    /// state); shared fail-closed body with <see cref="CountCombatStallWake"/>.
    /// </summary>
    internal static bool CountLeashStaleWake(uint actorId, uint targetObjId, int hp, string? cycleId, out int wakes)
        => CountLeashStaleWake(actorId, targetObjId, hp, cycleId, out wakes, selfAttributed: null);
    /// <summary>
    /// Attributed stale-pin wake count: a foreign-only HP delta keeps counting toward
    /// the trip; unreadable attribution keeps the historical behavior and clears —
    /// fail-closed, never a trip on unknown state. See the attributed
    /// <see cref="CountCombatStallWake(uint, uint, int, string?, out int, bool?)"/>
    /// overload for the <paramref name="selfAttributed"/> contract.
    /// </summary>
    internal static bool CountLeashStaleWake(uint actorId, uint targetObjId, int hp, string? cycleId, out int wakes, bool? selfAttributed)
        => CountNoProgressWake(LeashStaleStreaks, actorId, targetObjId, hp, cycleId, out wakes, selfAttributed);
    /// <summary>
    /// The shared no-progress streak body behind the stall trip and the stale-pin
    /// watchdog: consecutive same-HP wakes for one (actor, target), deduped by
    /// <paramref name="cycleId"/> so one wake counts once. Fail-closed: target 0
    /// or unreadable HP (negative) drops the actor's streaks and never trips; a
    /// self-attributed HP decrease clears (the actor's own damage landed = progress)
    /// while a foreign-only delta keeps counting; unreadable attribution
    /// (<paramref name="selfAttributed"/> null) clears like before — never a trip
    /// on unknown state. A different target for the same actor drops the old streak
    /// (a different fight).
    /// </summary>
    private static bool CountNoProgressWake(Dictionary<(uint ActorId, uint TargetObjId), CombatStallStreak> streaks, uint actorId, uint targetObjId, int hp, string? cycleId, out int wakes, bool? selfAttributed)
    {
        // Runtime tuning seam: the stall-trip threshold reads the live snapshot
        // (override) or the canonical CombatStallWakes default — next-wake
        // visible, byte-identical when no override is set.
        var threshold = BotBrainTuningStore.ResolveCombatStallWakes();
        wakes = 0;
        if (targetObjId == 0 || hp < 0)
        {
            ClearNoProgressActor(streaks, actorId);
            return false;
        }
        lock (PursuitSync)
        {
            if (streaks.Count >= CombatStallMemoryBound)
                streaks.Clear();
            foreach (var key in streaks.Keys.Where(k => k.ActorId == actorId && k.TargetObjId != targetObjId).ToList())
                streaks.Remove(key);
            if (streaks.TryGetValue((actorId, targetObjId), out var prior))
            {
                if (!string.IsNullOrEmpty(cycleId) && cycleId == prior.CycleId)
                {
                    wakes = prior.Wakes;
                    return wakes >= threshold;
                }
                // Self/foreign attribution: only the actor's own damage (or unreadable
                // attribution — fail-closed to the historical reset) clears the streak;
                // a foreign-only HP delta keeps counting toward the trip.
                if (hp < prior.Hp && selfAttributed != false)
                {
                    streaks.Remove((actorId, targetObjId));
                    return false;
                }
                wakes = prior.Wakes + 1;
            }
            else
            {
                wakes = 1;
            }
            streaks[(actorId, targetObjId)] = new CombatStallStreak(hp, wakes, cycleId ?? "");
            return wakes >= threshold;
        }
    }
    /// <summary>
    /// The actor-wide stall backstop: consecutive hold-wakes with no HP decrease
    /// across target switches (pure sum — every consecutive hold-wake counts no
    /// matter which live target it held on); deduped by <paramref name="cycleId"/>
    /// so one wake counts once. Fail-closed: target 0 or unreadable HP (negative)
    /// drops the backstop and never trips. A self-attributed HP decrease on the
    /// CURRENT target clears (the actor's own damage landed = progress) while a
    /// foreign-only delta keeps counting; unreadable attribution
    /// (<paramref name="selfAttributed"/> null) clears like before — never a trip
    /// on unknown state. An HP reading on a NEW target never clears (different HP
    /// pool, not progress).
    /// </summary>
    private static bool CountCombatStallActorWake(uint actorId, uint targetObjId, int hp, string? cycleId, out int wakes, bool? selfAttributed)
    {
        var threshold = BotBrainTuningStore.ResolveCombatStallWakes();
        wakes = 0;
        if (targetObjId == 0 || hp < 0)
        {
            lock (PursuitSync)
                CombatStallActorWakes.Remove(actorId);
            return false;
        }
        lock (PursuitSync)
        {
            if (CombatStallActorWakes.Count >= CombatStallMemoryBound)
                CombatStallActorWakes.Clear();
            if (CombatStallActorWakes.TryGetValue(actorId, out var prior))
            {
                if (!string.IsNullOrEmpty(cycleId) && cycleId == prior.CycleId)
                {
                    wakes = prior.Wakes;
                    return wakes >= threshold;
                }
                // Same self/foreign contract as the per-target body above: only the
                // actor's own damage (or unreadable attribution) clears the backstop.
                if (targetObjId == prior.TargetObjId && hp < prior.Hp && selfAttributed != false)
                {
                    CombatStallActorWakes.Remove(actorId);
                    return false;
                }
                wakes = prior.Wakes + 1;
            }
            else
            {
                wakes = 1;
            }
            CombatStallActorWakes[actorId] = new CombatStallActorStreak(targetObjId, hp, wakes, cycleId ?? "");
            return wakes >= threshold;
        }
    }
    /// <summary>
    /// Reads whether the actor itself dealt damage to this target from the engine's own
    /// tagging surface — the same tag/contributor evidence
    /// <see cref="LootBrain.ClassifyOwnership"/> reads: true when the actor is the tagger
    /// or appears in the contributor census, false when a readable claim names only
    /// foreign damage (a foreign tagger, or a contributor census with no self entry),
    /// null when no claim was found or the surface is unreadable (fail-closed: the
    /// counters treat null as "reset", never a trip on unknown state).
    /// </summary>
    private static bool? ReadSelfDamageAttribution(Character? character, Npc? npc, uint actorObjId)
    {
        if (character == null || npc == null)
            return null;
        try
        {
            var tagging = npc.CharacterTagging;
            if (tagging == null)
                return null;
            var tagger = tagging.Tagger;
            // Any tagger at all is a readable claim: self when it is the actor,
            // foreign when it is anyone else (the kill29 shape — tagger=44112 on
            // 44324 while char 4 only holds). No tagger is no claim found, which
            // is unreadable attribution (fail-closed reset), not a foreign verdict.
            if (tagger != null)
                return tagger.Id == character.Id || tagger.ObjId == actorObjId;
            if (tagging.TagTeam != 0)
                return null;
            var contributors = tagging.GetAllContributors(LootingContainer.MaxLootingRange);
            if (contributors.Count == 0)
                return null;
            foreach (var contributor in contributors)
            {
                if (contributor == null)
                    continue;
                if (contributor.Id == character.Id || contributor.ObjId == actorObjId)
                    return true;
            }
            return false;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>Drops every stall streak for one actor: its target identity is gone (lost/dead/unselected), so no streak may survive onto the next fight.</summary>
    private static void ClearCombatStallActor(uint actorId)
    {
        ClearNoProgressActor(CombatStallStreaks, actorId);
        lock (PursuitSync)
            CombatStallActorWakes.Remove(actorId);
        // The stale-pin watchdog keys on the same live identity: a gone target
        // ends its fight, so the leash streak ends here too — one fused release,
        // never a streak surviving onto the next fight.
        ClearLeashStaleActor(actorId);
    }
    /// <summary>Drops every no-progress streak in one map for one actor.</summary>
    private static void ClearNoProgressActor(Dictionary<(uint ActorId, uint TargetObjId), CombatStallStreak> streaks, uint actorId)
    {
        lock (PursuitSync)
        {
            foreach (var key in streaks.Keys.Where(k => k.ActorId == actorId).ToList())
                streaks.Remove(key);
        }
    }
    /// <summary>
    /// Stale-pin watchdog memory per (actor, target): consecutive
    /// Disengaged-at-LeashEdge wakes with unchanged HP while in position (the
    /// combat leg only consults the brain inside its admission radius, so a
    /// counted wake is already an in-position one). Same discipline as
    /// <c>CombatStallStreaks</c>: memory-only wake cache, keyed by
    /// (actor, target), bounded by full clear. The streak feeds only from the
    /// combat leg's true LeashEdge verdict and trips into the selector's
    /// demotion, so the next wake re-selects a killable target instead of
    /// holding the leash-dead pin.
    /// </summary>
    private static readonly Dictionary<(uint ActorId, uint TargetObjId), CombatStallStreak> LeashStaleStreaks = new();
    /// <summary>Reads whether (actor, target) tripped the stale-pin watchdog (selector + test seam).</summary>
    internal static bool IsLeashStaleTarget(uint actorId, uint targetObjId)
    {
        if (targetObjId == 0)
            return false;
        // Runtime tuning seam: same live threshold the counter trips at.
        var threshold = BotBrainTuningStore.ResolveCombatStallWakes();
        lock (PursuitSync)
            return LeashStaleStreaks.TryGetValue((actorId, targetObjId), out var streak) && streak.Wakes >= threshold;
    }
    /// <summary>Clears all stale-pin streaks (test seam, mirroring <c>ClearCombatStallMemory</c>).</summary>
    internal static void ClearLeashStaleMemory()
    {
        lock (PursuitSync)
            LeashStaleStreaks.Clear();
    }
    /// <summary>Drops every stale-pin streak for one actor.</summary>
    private static void ClearLeashStaleActor(uint actorId) => ClearNoProgressActor(LeashStaleStreaks, actorId);

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
    internal sealed record QuestCorpseRecord(uint ObjId, uint TemplateId, uint QuestId, string CycleId, DateTimeOffset DetectedUtc)
    {
        internal long InstanceToken { get; init; }
    }
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

    private static void NotePinnedCorpse(uint actorId, uint objId, uint templateId, QuestFixtureRow fixture, string cycleId, long instanceToken = 0)
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
            if (LastCorpse.TryGetValue(actorId, out var prior) && (prior.ObjId != objId || prior.InstanceToken != instanceToken))
                LootTravelDispatch.EndJourneyForPin(actorId);
            LastCorpse[actorId] = new QuestCorpseRecord(objId, templateId, fixture.QuestId, cycleId ?? "-", DateTimeOffset.UtcNow) { InstanceToken = instanceToken };
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
    /// G7c loot-once memory: (actor, corpse ObjId) entries bound to the NPC instance whose Loot verb has
    /// already run its terminal course. A fresh NPC at a recycled object number
    /// is independent; the same instance remains spent. The proposal withholds any recorded
    /// corpse present here, so the SAME corpse never re-dispatches across
    /// re-ticks — even if its container refills. Keyed stable per corpse
    /// (never per wake); the dispatch idempotency key mirrors it. Memory-only
    /// wake cache, same discipline as <c>LastCorpse</c>: keyed by ActorId,
    /// bounded by full clear. Records dispatches only, never policy.
    /// </summary>
    private static readonly Dictionary<(uint ActorId, uint ObjId), long> LootedCorpses = new();
    internal static bool IsLootDispatched(uint actorId, uint objId)
    {
        lock (PursuitSync)
            return LootedCorpses.ContainsKey((actorId, objId));
    }
    private static bool IsLootDispatched(uint actorId, Npc? npc)
    {
        if (npc == null) return false;
        var token = LootCorpseIdentity.GetToken(npc);
        lock (PursuitSync)
        {
            var key = (actorId, npc.ObjId);
            if (!LootedCorpses.TryGetValue(key, out var prior)) return false;
            if (prior == token) return true;
            LootedCorpses.Remove(key);
            return false;
        }
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
    /// <summary>Test-only reset for the interact leg's issue memory (same discipline as the sibling clears).</summary>
    internal static void ClearInteractMemory()
    {
        lock (PursuitSync)
            LastInteractIssue.Clear();
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
    /// Whether a corpse is terminally spent for ANY actor: the container exists
    /// and is readable with 0 items (the probe's own empty tally). Loot
    /// generation is one-shot, so 0-items never refills — terminal regardless
    /// of who emptied it or how the ownership classifier reads the tagging.
    /// Read-only, and called OUTSIDE <see cref="PursuitSync"/> so the world
    /// read never runs under the lock. Fail-closed: any unreadable surface
    /// (missing owner/container) returns false and current behavior is kept.
    /// </summary>
    private static bool IsTerminalEmptyCorpse(Character? character, Npc? npc)
    {
        if (character?.ParentWorld == null || npc == null)
            return false;
        try
        {
            var probe = ProbeCorpse(character, npc);
            return probe.ContainerExists && probe.ItemCount == 0;
        }
        catch
        {
            return false;
        }
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
        NotePinnedCorpse(actor.ActorId, npc.ObjId, npc.TemplateId, funnel.Fixture, opts.CycleId, LootCorpseIdentity.GetToken(npc));
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
    /// objId gone from the world → gone fragment AND the record released (the
    /// departure is reported for this wake; the stale slot is not persisted).
    /// Death-wake re-pin: a newly dead quest-relevant pinned target shadows
    /// any stale record — the fresh corpse is pinned (old released) so
    /// <c>LootEmit</c> evaluates the fresh container. Same-ObjId pins
    /// keep continuity through the record path; irrelevant dead units never
    /// re-pin. Dispatches nothing.
    /// Fresh-prey pre-emption: a dead-and-present RECORD is not the end of the
    /// arm. The record is a single slot, so a kill that lands while the slot
    /// names an EARLIER corpse has nothing to say for itself until that corpse
    /// resolves live or leaves the world — and until then the loot arm
    /// evaluates the OLD container on every wake. So before probing the record
    /// this arm asks the census for a nearer dead prey, and takes the slot when
    /// one exists and the PIN does not still name the record (a pin that does
    /// name it is a corpse this leg is genuinely still owed, and keeps it).
    ///
    /// Spent corpses: a corpse THIS actor terminally looted is not a corpse this
    /// arm reports, from any of its three entry points (the death-wake re-pin,
    /// the record probe, the pin's own resolve) — naming it every wake was the
    /// kill25 stall (39 already-looted verdicts on one corpse), and the release
    /// in <c>LootEmit</c> ran a wake too late to stop it.
    /// </summary>
    private static string ObservePinnedCorpse(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts,
        QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel, BotObservedContext? observation)
    {
        var character = actor.Character;
        uint pin = 0;
        uint holdPin = 0;
        QuestCorpseRecord? stale = null;
        lock (PursuitSync)
        {
            LastCorpse.TryGetValue(actor.ActorId, out stale);
            // Both candidates are lifted here and RANKED outside the lock: ranking
            // needs a world read, and this file keeps every world read out of the
            // critical section.
            if (LastStopHold.TryGetValue(actor.ActorId, out var hold))
                holdPin = hold.TargetObjId;
            if (LastPursuitIssue.TryGetValue(actor.ActorId, out var last))
                pin = last.TargetObjId;
        }
        // A reused object number can name a fresh NPC even on its first dead wake.
        // End only the old corpse's approach; never reset the new NPC's loot facts.
        if (stale is { InstanceToken: not 0 } && character?.ParentWorld?.GetNpc(stale.ObjId) is { } replacement
            && stale.InstanceToken != LootCorpseIdentity.GetToken(replacement))
        {
            lock (PursuitSync)
            {
                LastCorpse.Remove(actor.ActorId);
                LastApproachIssue.Remove(actor.ActorId);
            }
            LootTravelDispatch.EndJourneyForPin(actor.ActorId);
            stale = null;
        }
        // A hold is only ABANDONED in favour of a pursuit issue record that has
        // something to say: the hold memory is SHARED by all five Stop legs
        // (pursuit, return, gather, interact, talk), so "the last Stop landed" is
        // not by itself "the last Stop landed on prey" — a reporter, a talk npc, a
        // well and a seedling are all one entry — and nothing ever removes an
        // entry. A hold naming a boar that was killed and despawned minutes ago
        // therefore used to pin this leg to that dead objId for the rest of the
        // actor's life: with the pin frozen, the re-pin arm's own
        // <c>pin != stale.ObjId</c> guard was blocked, a corpse standing 0.2 m
        // away was never recognised, and the issue record — written on every
        // pursuit move toward the prey actually being hunted, and read by nothing
        // at all — could not even be consulted.
        //
        // Precedence, in order: a live-prey hold keeps the pin it always had; with
        // no issue record to fall back to (pin still 0) the hold is used exactly as
        // before, so the corpse a Stop landed on is still recognised with no
        // change; otherwise the issue record wins, which is that record's own
        // meaning — "the prey this actor is hunting".
        if (holdPin != 0 && (HoldPinsLivePrey(character, holdPin, funnel.Fixture.PreyTemplate) || pin == 0))
            pin = holdPin;
        // A pin naming a corpse this actor already terminally looted is spent
        // (kill25): the loot-once arm withholds it on every later wake, so
        // re-pinning it here hands the leg back a corpse nothing will ever take
        // and the fresh-corpse arms below never run. Same owner-scoped fact as
        // the pin-resolve arm and the census scan — the pin falls through.
        if (pin != 0 && (stale == null || pin != stale.ObjId)
            && !IsLootDispatched(actor.ActorId, character?.ParentWorld?.GetNpc(pin)))
        {
            var fresh = character?.ParentWorld?.GetNpc(pin);
            if (fresh != null && fresh.Hp <= 0 && fresh.TemplateId == funnel.Fixture.PreyTemplate
                && !IsTerminalEmptyCorpse(character, fresh))
            {
                NotePinnedCorpse(actor.ActorId, fresh.ObjId, fresh.TemplateId, funnel.Fixture, opts.CycleId, LootCorpseIdentity.GetToken(fresh));
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
                // The recorded objId no longer RESOLVES: the corpse despawned or left
                // the world. This wake still reports the departure truthfully — the
                // fragment is built from the record snapshot already read, and this
                // wake's observation of a departed corpse is a real observation.
                //
                // What must NOT survive is the RECORD. The single-slot record was
                // only ever a pointer at "the corpse this actor's loot leg is owed",
                // and the thing it points at is gone: keeping it made every later
                // kill shadow its own corpse fragment behind a stale objId and left
                // the loot chain re-evaluating a container that cannot resolve
                // (the chain reads this record as its ONLY corpse pin). The slot is
                // released the same owner-scoped way the recycled arm above releases
                // it — never another actor's record, never another leg's journey —
                // so a later corpse can be recognised and pinned again.
                lock (PursuitSync)
                    LastCorpse.Remove(actor.ActorId);
                return $":corpse={stale.ObjId}:gone=true";
            }
            else
            {
                // THE FRESH-PREY PRE-EMPION. A dead-and-present record is not the
                // only corpse this leg may be owed: the actor's own kill can land
                // while the record still names an EARLIER corpse — one this actor
                // never pinned, inherited through a shared hold, or pinned
                // before its own last kill — and the probe below then reports
                // THAT corpse, and the loot arm evaluates it, on every wake
                // until it despawns. That window spans the actor's next several
                // kills (measured 15-381 s in a 30-minute run), so the fresh prey
                // this leg just made is never the corpse it is handed.
                //
                // Two guards, both over values already in hand — no new world read
                // and no new scan: the census this wake already carries is the
                // only candidate source, and `cur` is already resolved.
                //  - STILL OWED: while the PIN names the record, this leg is
                //    genuinely owed THIS corpse and keeps the slot. That is the
                //    precedence the two arms above have always had, and the pin
                //    re-pin arm has already had its chance to move it.
                //  - NEARER: the census corpse must be strictly nearer than the
                //    record, so a corpse this actor is already closing on is
                //    never traded away for one further out.
                //
                // The re-pin itself runs through NotePinnedCorpse, so the handoff
                // is the ordinary one: a DIFFERENT objId ends the approach journey
                // armed on the old corpse (owner-scoped — this actor's only leg),
                // and the fresh corpse never inherits the old one's repath budget.
                //
                // SPENT RECORD (kill25: 39 already-looted verdicts on one corpse).
                // A record naming a corpse THIS actor terminally looted is done —
                // the loot-once arm withholds it on every later wake — so probing
                // it here re-reports the spent corpse on every wake and, because
                // it is usually the nearest, the census pre-emption below never
                // gets to look past it. Released exactly the way the recycled and
                // gone arms release it (this actor's record only, approach journey
                // ended with it), so this wake continues into the ordinary fresh-
                // corpse resolution instead of reporting the corpse already taken.
                if (IsLootDispatched(actor.ActorId, cur) || IsTerminalEmptyCorpse(character, cur))
                {
                    lock (PursuitSync)
                        LastCorpse.Remove(actor.ActorId);
                    LootTravelDispatch.EndJourneyForPin(actor.ActorId);
                    return NoteCensusCorpse(actor, opts, funnel, observation);
                }
                var here = character?.Transform.World.Position;
                var recordDistanceM = here.HasValue
                    ? MathUtil.CalculateDistance(here.Value, cur.Transform.World.Position, false)
                    : double.MaxValue;
                if (pin != stale.ObjId && !double.IsNaN(recordDistanceM))
                {
                    var fresh = NoteCensusCorpse(actor, opts, funnel, observation,
                        excludeObjId: stale.ObjId, nearerThanM: recordDistanceM);
                    if (fresh.Length > 0)
                        return fresh;
                }
                var probe = ProbeCorpse(character, cur);
                return FormatCorpseFragment(stale.ObjId, probe.ItemCount, probe.Lootable);
            }
        }
        // The pinned objId's own resolve: a pin that names a dead fixture prey
        // is our corpse even with no record yet (first post-death wake).
        if (pin != 0)
        {
            var npc = character?.ParentWorld?.GetNpc(pin);
            // A pin that names a corpse this actor already terminally looted is
            // spent (kill25): the loot-once arm withholds it on every later wake,
            // so reporting it re-probes a corpse nothing will ever take and the
            // fresh-prey arms below are never reached. Same owner-scoped fact,
            // same exclusion as the census scan — the pin falls through to the
            // ordinary arms instead of shadowing them.
            if (npc != null && npc.Hp <= 0 && npc.TemplateId == funnel.Fixture.PreyTemplate
                && !IsLootDispatched(actor.ActorId, npc)
                && !IsTerminalEmptyCorpse(character, npc))
            {
                NotePinnedCorpse(actor.ActorId, npc.ObjId, npc.TemplateId, funnel.Fixture, opts.CycleId, LootCorpseIdentity.GetToken(npc));
                var probeNow = ProbeCorpse(character, npc);
                return FormatCorpseFragment(npc.ObjId, probeNow.ItemCount, probeNow.Lootable);
            }
        }
        // Neither arm above answered: no record survived, and the pin (if any)
        // is not a corpse. Only here may the census name one — so a live record
        // and a live pin keep the precedence they have always had.
        return NoteCensusCorpse(actor, opts, funnel, observation);
    }

    /// <summary>
    /// G7b recognition arm 3 (census): the pin-free case the two arms above
    /// cannot reach. <c>LastCorpse</c> is written ONLY from a pin, and a pin is
    /// written only by the pursuit Stop/Move dispatches — so an actor that kills
    /// its prey from inside the combat brain's own commitment (which withdraws
    /// the pursuit leg before that Stop lands) never records its own kill. The
    /// corpse is then standing in the wake's own census, at contact range, and
    /// <c>LootEmit</c> is handed <c>CorpseObjId: 0</c> — which
    /// <c>LootBrainPlanner</c> reads as "no claim found, not our corpse"
    /// (<c>corpseIsOurPinnedCorpse: false</c>), so ownership is
    /// <c>Unresolved</c> and the brain withholds on every wake of every kill.
    ///
    /// Resolves the nearest DEAD prey of the fixture template from the wake's
    /// OWN census (<c>NearbyNpcObjIds</c> — the same snapshot and the same 25 m
    /// radius <see cref="QuestObjectiveTargetSelector"/> reads; no second
    /// perception, no widened census, no world scan beyond it). Nearest-first
    /// with the selector's own ObjId tiebreak, so the choice is deterministic.
    ///
    /// A stale record does not fence this arm off either: it runs ahead of the
    /// pinned resolve too, and there a fresh census corpse pre-empts the stale
    /// pin only when the pin names a different objId AND the fresh corpse is
    /// strictly nearer than the record. A live record and a live pin keep the
    /// precedence they have always had. Every gate the
    /// pinned arms already apply is applied here identically: the fixture's own
    /// prey template (inside <see cref="NotePinnedCorpse"/> and again before it)
    /// and dead-or-nothing.
    ///
    /// A corpse this actor has already terminally looted is not a candidate here
    /// or in the pin-resolve arm: it is dead prey of the right template, so
    /// without that gate it simply wins the nearest-first scan every wake
    /// (kill25: 39 already-looted verdicts on one corpse) and the pin release
    /// has nothing fresh to fall through to. The exclusion reads this actor's
    /// own loot-once memory — a terminal fact it established — so no new state
    /// and no extra perception enter the scan.
    ///
    /// It is NOT loot rights, NOT a range grant and NOT an
    /// unconditional take — the pinned record it writes is the only thing that
    /// changes, and the loot brain's own ownership, safety, loot-once, worth and
    /// probe gates then read exactly what they read for a Stop-pinned corpse.
    ///
    /// Owner-scoped: the record is written under <c>actor.ActorId</c>, like every
    /// other memory here. Every world read runs OUTSIDE <see cref="PursuitSync"/>.
    /// </summary>
    private static string NoteCensusCorpse(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts,
        QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel, BotObservedContext? observation)
        => NoteCensusCorpse(actor, opts, funnel, observation, excludeObjId: 0, nearerThanM: double.MaxValue);

    /// <summary>
    /// The same census resolution under an existing record's two guards.
    /// <paramref name="excludeObjId"/> drops a candidate the caller has already
    /// ruled on (the record it is deciding about), and
    /// <paramref name="nearerThanM"/> admits only a corpse strictly nearer than
    /// that. Both are the caller's own already-read facts, so the arbitration
    /// adds no perception of its own. Defaults reproduce the pin-free arm
    /// exactly: nothing excluded, nothing distance-bounded.
    /// </summary>
    private static string NoteCensusCorpse(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts,
        QuestObjectiveTargetSelector.ObjectiveTargetFunnel funnel, BotObservedContext? observation,
        uint excludeObjId, double nearerThanM)
    {
        var character = actor.Character;
        var preyTemplate = funnel.Fixture.PreyTemplate;
        if (preyTemplate == 0 || observation == null || character?.ParentWorld == null)
            return "";

        var here = character.Transform.World.Position;
        Npc? corpse = null;
        var bestDistance = double.MaxValue;
        var bestObjId = uint.MaxValue;
        foreach (var objId in observation.NearbyNpcObjIds)
        {
            if (objId == excludeObjId)
                continue;
            var npc = character.ParentWorld.GetNpc(objId);
            if (npc == null || npc.Hp > 0 || npc.TemplateId != preyTemplate)
                continue;
            // SPENT-CORPSE EXCLUSION (kill25: the same looted corpse re-resolved
            // every wake — 39 already-looted verdicts, one per wake, while the
            // actor stood on it). The pin release above owns the RECORD, but the
            // scan's own nearest-first pick had no such fact: a corpse this actor
            // terminally looted is still dead prey of the fixture template and is
            // still the NEAREST one, so it won the scan again the moment the
            // release dropped the slot — release, re-pin, re-probe, forever, and
            // no fresh corpse was ever considered. The loot-once memory is the
            // actor's own terminal fact about this corpse (the same one
            // `LootEmit`'s release reads), so it belongs beside the template and
            // dead gates here: an already-taken corpse is not a candidate. A
            // corpse anyone ELSE took is untouched — this is owner-scoped, and
            // the engine's own ownership gates still decide such a corpse.
            if (IsLootDispatched(actor.ActorId, npc))
                continue;
            // TERMINAL-EMPTY EXCLUSION (contended-corpse race: a second
            // actor pins a corpse ANY killer already emptied — container
            // exists, 0 items — and starves live prey because Target never
            // commits while the census keeps re-pinning the spent corpse).
            // Loot generation is one-shot, so 0-items never refills: terminal
            // for ANY actor regardless of the tagging/ownership reading, read
            // through the probe surface — so it belongs beside the
            // loot-once gate here: such a corpse is not a candidate. Scoped to
            // container-exists/0-items on purpose: an unreadable container is
            // generation-in-flight, never terminal. Ownership still decides
            // non-empty takes; only the EMPTY exclusion is ownership-free.
            if (IsTerminalEmptyCorpse(character, npc))
                continue;
            var distance = MathUtil.CalculateDistance(here, npc.Transform.World.Position, false);
            if (double.IsNaN(distance)
                || !(distance < bestDistance || (distance == bestDistance && objId < bestObjId)))
                continue;
            corpse = npc;
            bestDistance = distance;
            bestObjId = objId;
        }

        if (corpse == null || bestDistance >= nearerThanM)
            return "";
        NotePinnedCorpse(actor.ActorId, corpse.ObjId, corpse.TemplateId, funnel.Fixture, opts.CycleId, LootCorpseIdentity.GetToken(corpse));
        var probe = ProbeCorpse(character, corpse);
        return FormatCorpseFragment(corpse.ObjId, probe.ItemCount, probe.Lootable);
    }

    /// <summary>
    /// Whether the actor's last Stop hold still names LIVE quest prey — the only
    /// reading under which a hold may own this leg's corpse pin. Same legality
    /// rule the re-pin arm and <see cref="NotePinnedCorpse"/> already apply to a
    /// pin (fixture prey template, and a corpse is a corpse only while dead), so
    /// the hold is validated by the file's own standard rather than a new one.
    /// Read-only, and called OUTSIDE <see cref="PursuitSync"/> so the world read
    /// never runs under the lock.
    /// </summary>
    private static bool HoldPinsLivePrey(Character? character, uint holdPin, uint preyTemplate)
    {
        if (character?.ParentWorld == null || preyTemplate == 0)
            return false;
        var held = character.ParentWorld.GetNpc(holdPin);
        return held != null && held.Hp > 0 && held.TemplateId == preyTemplate;
    }
    /// <summary>Diagnostic-only raw sweep objId census bound (objId:templateId:flatM, no spaces).</summary>
    private const int SweepDiagRawIdsShown = 12;
    /// <summary>
    /// Runs the wake's plan set through the leg loop and the decision
    /// pipeline: <see cref="QuestDirector.Run"/> perceives once and assembles
    /// the plans, then hands them here with that same snapshot.
    /// When <c>opts.ChosenQuestId</c> carries the owning module's purpose, the
    /// plan set is already scoped to it (see <see cref="QuestDirector.PlanWake"/>);
    /// the resulting request's quest identity is verified below and stamped on
    /// the run result — any change carries an explicit reason, never silence.
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
            // Chosen-purpose scoping (HIGH 1): when opts.ChosenQuestId carries
            // the owning module's purpose, ONLY that quest's offering may be
            // proposed — ordinary acceptance cannot select another quest on its
            // own. The sweep above still collects every in-band offering (the
            // diagnostics stay honest); the rank/cut below narrows to the
            // purpose. A chosen quest that is already active never reaches an
            // offering (the before-set filter above); its work flows through
            // the scoped plan set instead. A chosen offer still seen is served,
            // never labeled stale.
            var eligible = opts.ChosenQuestId != 0
                ? offerings.Where(o => o.Offering.QuestId == opts.ChosenQuestId)
                : offerings;
            // Server-state cause for the carried purpose, read once per wake for
            // the no-proposal reason below: active quest (the wake's before-set),
            // offer still available (seen in this sweep — a valid fresh offer is
            // NOT stale), completed quest (engine completed flag on a
            // non-repeatable), or unavailable (the engine's own accept-gate
            // reason). Anything else is an offer this bounded sweep did not see
            // (offerer out of range, sweep cut, or a non-swept channel) — the
            // purpose is kept, never released, and the reason says so.
            var chosenCause = ChosenCause(actor, opts, before, offerings);
            // Offerings are RANKED before they are proposed, and the bound is
            // applied after ranking — so a wake with more in-band offers than
            // the bound drops its WORST offers, and the shared selector never
            // sees a candidate list past its own ceiling (which would make it
            // decide nothing at all). Each offering's quest row is derived ONCE
            // here and reused by both the ranking cut and the emitted proposal,
            // so the two never disagree about what the quest offers. The order is
            // the selector's own key order (priority, weight, tie-break), so the
            // bound's cut and the selector's pick agree on what "best" means.
            // With a purpose carried, the ranked set holds at most the chosen
            // quest, so the bound can never cut it in favor of another offer.
            var ranked = eligible
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
            // includeLegalAlternatives is armed ONLY by the opt-in shadow lane:
            // a flag-off wake pays nothing for the observer-only alternatives,
            // and no eligibility is re-evaluated either way — the selector's
            // own single pass produced them.
            var decision = BotDecisionSelector.Select(decideContext, proposals,
                includeLegalAlternatives: JevShadow.Enabled);
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
                var corpseKeys = objectiveFunnel == null ? "" : ObservePinnedCorpse(actor, opts, objectiveFunnel, context).Replace(":", " ");
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
                // Link-1b observability: gate-withheld legs (e.g. the Advance
                // steady-progress-uncredited-source withdraw) ride the DECIDE
                // bracket as parser-safe withdraws=Leg:reason pairs placed before
                // the 300-char outcomes blob so the token survives the 600-char
                // QuestDecideDetail cut; "-" when nothing withdrew. Additive:
                // unknown key, no spaces, existing keys byte-identical behind it.
                var withdrawKeys = $" withdraws={(legEvidence.Any(e => !e.Entered) ? string.Join(",", legEvidence.Where(e => !e.Entered).Select(e => $"{e.Leg}:{e.Detail}".Replace(' ', '_'))) : "-")}";
                // Stage 4: a wake whose only quest failed its fixture gate
                // reports FIXTURE, never a bare sweep miss — the plan's own
                // reason leads, and the bracket stays byte-identical behind it
                // so the BotQuestFunnel parser keeps parsing.
                var decideDetail = $"no legal quest proposal: {decision.Explanation} [swept={discoverTargets.Count} targets={string.Join(",", discoverTargets)} rawIds={rawIdsValue}{corpseKeys} offerings={offerings.Count} inBand={inBand} legal={proposals.Count} firstZero={firstZero}{withdrawKeys}{sweepKeys}{objectiveKeys}]";
                var failStage = "DECIDE";
                if (planFailures.Count > 0)
                {
                    failStage = "FIXTURE";
                    decideDetail = $"plan failed: {string.Join(" ; ", planFailures.Select(f => f.Reason))} | {decideDetail}";
                }
                // HIGH 1: a carried purpose that yielded no proposal is never
                // silent — the result names the SERVER-STATE cause as an
                // ADVISORY (SelectedQuestId / PurposeSwitchReason have no
                // production reader; the real release is owned by
                // QuestBootstrapActivityModule on its own later listing
                // turn). A valid fresh offer, or an active quest whose legs
                // withdrew this wake, KEEPS the purpose (SelectedQuestId 0,
                // "held" — never "stale"). A sweep-unseen purpose is also
                // KEPT ("unseen": this bounded sweep did not see the offer
                // — never "stale"). Only a completed or engine-rejected
                // unavailable purpose is "stale" (and additionally flagged
                // Releasable on the advisory fields). Survival-vetoed legs
                // name the veto cause below.
                var staleDetail = opts.ChosenQuestId != 0
                    ? chosenCause.Kind switch
                    {
                        "offer-available" or "active" =>
                            $"purpose-held chosen={opts.ChosenQuestId} {chosenCause.Detail} ({decideDetail})",
                        "offer-unseen" =>
                            $"purpose-unseen chosen={opts.ChosenQuestId} {chosenCause.Detail} ({decideDetail})",
                        _ =>
                            $"purpose-stale chosen={opts.ChosenQuestId} {chosenCause.Detail} ({decideDetail})"
                    }
                    : "";
                return Fail(failStage, ActorFailureReason.WrongDecision, decideDetail,
                    actor, null, decision.Rejections, null, legEvidence, planFailures, giveUps,
                    purposeSwitchReason: staleDetail,
                    selectedQuestId: chosenCause.Releasable ? opts.ChosenQuestId : 0);
            }
            // ---------------------------------------------------- 3. EXECUTE
            var selected = decision.Proposal!;
            // Chosen-purpose dispatch guard: with a purpose carried, a selected
            // quest that is NOT the chosen one is NEVER dispatched — an
            // explanatory message after executing another quest is not control.
            // With the scoped rank above this is unreachable in production; the
            // guard stays as the fail-closed backstop (no silent substitution:
            // nothing lands, the actual reason is surfaced, and the owner may
            // choose again). Survival keeps its own path: vetoed legs never
            // reach selection (their own preconditions withhold them), so this
            // guard never names a veto.
            if (opts.ChosenQuestId != 0 && QuestIdOf(selected) is var selectedQuest && selectedQuest != opts.ChosenQuestId)
            {
                var mismatchCause = ChosenCause(actor, opts, before, offerings);
                var mismatchDetail = $"purpose-mismatch chosen={opts.ChosenQuestId} selected={selectedQuest} {mismatchCause.Detail}: no unrelated quest action dispatched ({decision.Explanation})";
                return Fail("DECIDE", ActorFailureReason.WrongDecision, mismatchDetail,
                    actor, selected.Action, decision.Rejections, null, legEvidence, planFailures, giveUps,
                    purposeSwitchReason: mismatchDetail,
                    selectedQuestId: mismatchCause.Releasable ? opts.ChosenQuestId : 0);
            }
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
                decision.Rejections.Count + 1, decision.Rejections.Count, opts.CycleId, QuestIdOf(selected));
            actor.SetPendingCycleId(opts.CycleId);
            BotDecisionExecution execution;
            try
            {
                execution = BotDecisionCycle.Execute(actor, decideContext, selected,
                    static (gameplayActor, proposal) => Dispatch(gameplayActor, proposal));
            }
            finally
            {
                actor.SetPendingDecision(null, null, 0, 0, null, null);
                actor.SetPendingCycleId(null);
            }
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
                if (selected.Goal is PursuitGoal or ReturnGoal or LootGoal or GatherGoal or InteractGoal && selected.Action == ActorActionType.Move
                    && request.State == ActorLifecycleState.Running)
                {
                    var afterRunning = BotObservedContext.Capture(actor).ActiveQuestIds.ToHashSet();
                    var completedRunning = before.Where(q => !afterRunning.Contains(q)).ToList();
                    var legKind = selected.Goal switch
                    {
                        ReturnGoal => "return",
                        LootGoal => "loot approach",
                        GatherGoal => "gather",
                        InteractGoal => "interact",
                        _ => "pursuit"
                    };
                    var runningQuest = QuestIdOf(selected);
                    var (runningSwitch, runningSwitchReason) = PurposeSwitch(opts, runningQuest, request, legEvidence, planFailures, giveUps);
                    return new QuestDecisionScenario.QuestRunResult
                    {
                        Scenario = QuestDecisionScenario.ScenarioName,
                        WorkSelected = true,
                        SelectedAction = selected.Action,
                        SelectedProposal = selected,
                        Request = request,
                        SelectedQuestId = runningSwitch,
                        PurposeSwitchReason = runningSwitchReason,
                        Rejections = decision.Rejections,
                        LegalAlternatives = decision.LegalAlternatives,
                        SelectionCandidateCount = decision.SelectionCandidateCount,
                        Explanation = decision.Explanation + $" [{legKind} leg running toward {selected.TargetId}]",
                        CompletedQuestIds = completedRunning,
                        LegEvidence = legEvidence,
                        PlanFailures = planFailures,
                        GiveUps = giveUps,
                        TuningRevision = BotBrainTuningStore.Revision,
                        TraceRecords = [.. actor.AuditTrace]
                    };
                }
                // A selector DID run and DID pick before the dispatch starved,
                // and the recorder emits this row (MaybeEmitQuest only guards
                // result == null). Dropping the alternatives here would make the
                // row claim a lone proposal it never had.
                return Fail("EXECUTE", ActorFailureReason.Starvation,
                    $"{request.Action} left the terminal surface",
                    actor, selected.Action, decision.Rejections, request, legEvidence, planFailures, giveUps,
                    legalAlternatives: decision.LegalAlternatives,
                    selectionCandidateCount: decision.SelectionCandidateCount);
            }

            var after = BotObservedContext.Capture(actor).ActiveQuestIds.ToHashSet();
            var completed = before.Where(q => !after.Contains(q)).ToList();
            // A completed quest leaves the plan set, so its streak is gone with
            // it — cleared explicitly so the map never holds a stale quest.
            foreach (var completedId in completed)
                ClearGiveUpWake(actor.ActorId, completedId);
            var (landedQuest, landedSwitchReason) = PurposeSwitch(opts, QuestIdOf(selected), request, legEvidence, planFailures, giveUps);
            return new QuestDecisionScenario.QuestRunResult
            {
                Scenario = QuestDecisionScenario.ScenarioName,
                WorkSelected = true,
                SelectedAction = selected.Action,
                SelectedProposal = selected,
                Request = request,
                SelectedQuestId = landedQuest,
                PurposeSwitchReason = landedSwitchReason,
                Rejections = decision.Rejections,
                LegalAlternatives = decision.LegalAlternatives,
                Explanation = decision.Explanation,
                CompletedQuestIds = completed,
                LegEvidence = legEvidence,
                PlanFailures = planFailures,
                GiveUps = giveUps,
                TuningRevision = BotBrainTuningStore.Revision,
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
    /// Quest identity of a selected proposal: every quest-work proposal carries
    /// its quest in <c>TargetId</c> (advance, turn-in, item-use consume) or in
    /// its tie-break key (<c>"name:questId:target"</c> for target, pursuit,
    /// combat, loot, gather, interact, return, talk legs). The accept leg is
    /// the one shape whose TargetId IS the quest id already. Returns 0 when no
    /// identity is readable — never a guess.
    /// </summary>
    internal static uint QuestIdOf(BotDecisionProposal proposal)
    {
        if (proposal.Goal == UseItemGoal)
            return QuestIdFromTieBreak(proposal.TieBreakKey);
        var tie = QuestIdFromTieBreak(proposal.TieBreakKey);
        return tie != 0 ? tie : proposal.TargetId;
    }

    /// <summary>Reads the quest id out of a leg tie-break key (0 when absent).</summary>
    private static uint QuestIdFromTieBreak(string tieBreakKey)
    {
        if (string.IsNullOrEmpty(tieBreakKey))
            return 0;
        foreach (var part in tieBreakKey.Split(':'))
        {
            if (uint.TryParse(part, out var questId) && questId != 0)
                return questId;
        }
        return 0;
    }

    /// <summary>
    /// Verifies the wake's quest against the carried purpose: returns the
    /// served quest plus the explicit switch reason (empty when the wake
    /// served the purpose, or when no purpose was carried). A wake that lands
    /// work for another quest while a purpose is carried is never silent: the
    /// reason names the carried purpose, the served quest, and the evidence
    /// that justified it (give-up verdict, plan failure, or the leg evidence
    /// that withdrew the purpose's work). Safety preemption needs no switch:
    /// the veto withholds ordinary proposals through their own preconditions,
    /// so a vetoed wake lands nothing (or safety's own non-quest work) with
    /// the veto named in the rejections/evidence it already carries.
    /// </summary>
    private static (uint QuestId, string Reason) PurposeSwitch(
        QuestDecisionScenario.QuestOptions opts,
        uint servedQuestId,
        ActorRequest request,
        IReadOnlyList<LegEvidence> legEvidence,
        IReadOnlyList<QuestPlanFailure> planFailures,
        IReadOnlyList<QuestGiveUp> giveUps)
    {
        if (opts.ChosenQuestId == 0)
            return (servedQuestId, "");
        if (servedQuestId == opts.ChosenQuestId)
            return (servedQuestId, "");
        var evidence = giveUps.Count > 0
            ? $"give-up {giveUps[0].Reason}"
            : planFailures.Count > 0
                ? $"plan failed {planFailures[0].Reason}"
                : legEvidence.Count > 0
                    ? $"purpose withdrew ({legEvidence[0].Leg}:{legEvidence[0].Detail})"
                    : "purpose yielded no proposal";
        return (servedQuestId,
            $"purpose-switch chosen={opts.ChosenQuestId} served={servedQuestId} request={request.Action}:{request.TargetId} {evidence}");
    }

    /// <summary>
    /// Server-state cause for the carried purpose (HIGH 1): distinguishes the
    /// four states using live server reads — (a) the offer is still available
    /// (seen in this wake's sweep: a valid fresh offer is NEVER stale), (b)
    /// the quest is active (the wake's own before-set: its legs own this
    /// wake), (c) the quest is completed (engine completed flag on a
    /// non-repeatable), (d) the offer is unavailable (the engine's own
    /// accept-gate reason via the same predicates AddQuest refuses). A
    /// purpose this bounded sweep did not see is "unseen" — the purpose is
    /// KEPT (the offerer may be out of range, past the sweep cut, or on a
    /// non-swept channel; absence from one sweep never proves
    /// disappearance), so the owner does not churn. Releasable is an
    /// ADVISORY flag for the result fields only (SelectedQuestId /
    /// PurposeSwitchReason have no production reader): the REAL release is
    /// owned by QuestBootstrapActivityModule, which re-reads the live
    /// opportunity listing on its own later turn. Survival veto is NOT a
    /// cause here — vetoed legs withhold through their own preconditions,
    /// and the rejections/evidence they already carry name the veto.
    /// </summary>
    private static (string Kind, string Detail, bool Releasable) ChosenCause(
        IGameplayActor actor,
        QuestDecisionScenario.QuestOptions opts,
        HashSet<uint> before,
        List<(uint TargetObjId, QuestOffering Offering)> offerings)
    {
        if (opts.ChosenQuestId == 0)
            return ("none", "no purpose carried", false);
        var chosen = opts.ChosenQuestId;
        if (before.Contains(chosen))
            return ("active", $"quest {chosen} is active (ordinary work serves it through the scoped plan set)", false);
        if (offerings.Any(o => o.Offering.QuestId == chosen))
            return ("offer-available", $"quest {chosen} offer still available (valid fresh offer, purpose kept)", false);
        var quests = actor.Character?.Quests;
        var template = QuestManager.Instance?.GetTemplate(chosen);
        if (quests != null && template != null && quests.HasQuestCompleted(chosen) && !template.Repeatable)
            return ("completed", $"quest {chosen} completed (non-repeatable, releasable-advisory)", true);
        if (actor is GameplayActor concrete)
        {
            var reject = concrete.DiscoverRejectReason(chosen);
            if (reject.Length != 0 && reject != "ALREADY_ACTIVE")
                return ("offer-unavailable", $"quest {chosen} unavailable ({reject}, releasable-advisory)", true);
        }
        return ("offer-unseen", $"quest {chosen} not seen in this sweep (offerer out of range, past the sweep cut, or a non-swept channel — purpose kept)", false);
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
    internal static bool GatherCredited(uint questId, Quest quest, QuestFixtureRow fixture)
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
    /// doodad granting the row's item (ANY of <c>GatherDoodadTemplates</c>; the
    /// quest's alias accepts every granting well) — to walk to. The gate owns
    /// all three reasons, so the loop's evidence names the real cause of the
    /// withdraw. Never dispatches; the resolve is a read-only census of the
    /// wake's own perception snapshot.
    internal static string? GatherEnter(QuestLegContext context, QuestLegWake wake)
    {
        var quest = context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        if (quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
            return "quest-not-usable";
        var fixture = context.Fixture!;
        if (GatherCredited(context.QuestId, quest, fixture))
            return "objective-credited";
        return ResolveGatherDoodad(context, fixture).ObjId == 0 ? "gather-no-source" : null;
    }

    /// <summary>
    /// The wake's gather source: the NEAREST perceived doodad (flat distance
    /// from the actor) whose template is ANY of the row's granting templates —
    /// the quest's <c>quest_act_obj_alias_id</c> (4415's 1784 "any well") accepts
    /// every granting well, so the leg walks the closest one, not the lowest-id
    /// one. ObjId tiebreak. The chosen template's draw skill
    /// (<see cref="QuestFixtureRow.ResolveDoodadUseSkill"/>, per-template — never
    /// the row's single id) rides the selection. 0 when the row names no template
    /// or none is perceived. Read-only over the wake's own snapshot — never a
    /// world scan, never stored.
    /// </summary>
    internal static (uint ObjId, uint TemplateId, uint UseSkill) ResolveGatherDoodad(QuestLegContext context, QuestFixtureRow fixture)
    {
        var intel = ReadGatherSourceIntel(context, fixture);
        if (intel.PerceivedGranting.Count == 0)
            return (0, 0, 0);
        var first = intel.PerceivedGranting[0];
        // Per-template skill: a hand-built row stages only the single id (no
        // derived skill), so the leg resolves the CHOSEN template's skill live
        // off its func tables — derived rows already carry the same value.
        return (first.ObjId, first.TemplateId, QuestFixtureRow.ResolveDoodadUseSkill(first.TemplateId));
    }

    /// <summary>
    /// One perceived granting source: a doodad in the wake's 25 m census whose
    /// template grants the objective item, with its flat distance from the actor.
    /// </summary>
    public readonly record struct PerceivedGranting(uint ObjId, uint TemplateId, float DistanceM);

    /// <summary>
    /// The brain input shape for the gather-source decision — requirements →
    /// nearby → distance, frozen per wake. (1) requirements arrive on the
    /// fixture row itself (item, need, granting set, reporter); (2) nearby is
    /// the perceived granting set (every census doodad granting the item,
    /// nearest-first) plus the surroundings census around it (total perceived
    /// doodads/NPCs — competing objectives and hazards the Decide chain reads
    /// with context, never a single-point target); (3) distance is the ranked
    /// spawner candidates across ALL granting templates. The Decide chain
    /// (GatherEmit / the Advance gate / the travel arming) owns the
    /// travel/withdraw verdict over this shape; the readers below never decide.
    /// </summary>
    public readonly record struct GatherSourceIntel(
        IReadOnlyList<PerceivedGranting> PerceivedGranting,
        int NearbyDoodadCount,
        int NearbyNpcCount,
        IReadOnlyList<GatherSpawnerCandidate> RankedSpawners);

    /// <summary>
    /// Reads the gather-source intel for one wake: the perceived granting set
    /// (wake snapshot census × granting templates, nearest-first with an ObjId
    /// tiebreak), the surroundings census counts, and — when
    /// <paramref name="here"/> and <paramref name="world"/> are supplied — the
    /// ranked spawner candidates. Null world/position reads perception only
    /// (no spawner scan). Never dispatches, never stored.
    /// </summary>
    public static GatherSourceIntel ReadGatherSourceIntel(
        QuestLegContext context, QuestFixtureRow fixture, WorldInstance? world = null, Vector3? here = null)
    {
        List<PerceivedGranting>? perceived = null;
        var nearbyDoodads = 0;
        var nearbyNpcs = 0;
        var observed = context.Observation;
        if (observed != null)
        {
            nearbyDoodads = observed.NearbyDoodadObjIds.Count;
            nearbyNpcs = observed.NearbyNpcObjIds.Count;
            var templates = fixture.GatherDoodadTemplates;
            var singleFallback = templates == null || templates.Count == 0;
            var character = context.Actor.Character;
            var lookup = character?.ParentWorld;
            var actorPos = character?.Transform.World.Position;
            if ((singleFallback ? fixture.GatherDoodadTemplate != 0 : true)
                && lookup != null && actorPos.HasValue)
            {
                foreach (var objId in observed.NearbyDoodadObjIds)
                {
                    var doodad = lookup.GetDoodad(objId);
                    if (doodad == null)
                        continue;
                    var granting = singleFallback
                        ? doodad.TemplateId == fixture.GatherDoodadTemplate
                        : ContainsTemplate(templates!, doodad.TemplateId);
                    if (!granting)
                        continue;
                    (perceived ??= new List<PerceivedGranting>()).Add(new PerceivedGranting(
                        objId, doodad.TemplateId,
                        MathUtil.CalculateDistance(actorPos.Value, doodad.Transform.World.Position, false)));
                }
            }
        }
        if (perceived != null)
            perceived.Sort(static (a, b) =>
            {
                var d = a.DistanceM.CompareTo(b.DistanceM);
                return d != 0 ? d : a.ObjId.CompareTo(b.ObjId);
            });
        IReadOnlyList<GatherSpawnerCandidate> ranked =
            world != null && here.HasValue
                ? RankGatherSpawnerCandidates(world, fixture, here.Value)
                : Array.Empty<GatherSpawnerCandidate>();
        return new GatherSourceIntel(
            perceived != null ? perceived.ToArray() : Array.Empty<PerceivedGranting>(),
            nearbyDoodads, nearbyNpcs, ranked);
    }

    /// <summary>Membership over the granting set without allocating (sets hold a handful of ids).</summary>
    private static bool ContainsTemplate(IReadOnlyList<uint> templates, uint templateId)
    {
        for (var i = 0; i < templates.Count; i++)
        {
            if (templates[i] == templateId)
                return true;
        }
        return false;
    }


    /// <summary>
    /// Gather-from-doodad leg: draw the objective item from the perceived well
    /// through the real <c>Interact</c> contract with the CHOSEN well's own draw
    /// skill (per-template <see cref="ResolveGatherDoodad"/> selection — e.g. the
    /// well's fake-use row carries the draw skill; a skill-0 row stays a
    /// skill-less <c>Doodad.Use</c>; no new actor verb). The leg reads, in order:
    /// (1) requirements — the fixture row (item, need, granting set, reporter);
    /// (2) nearby — the 25 m perception census (a perceived granting source
    /// short-circuits travel: no spawner walk while a source is in range);
    /// (3) distance — the nearest perceived granting source by flat distance;
    /// then the decision (Move / Stop / Interact) with the reason naming the
    /// chosen template + distance. Per wake: source lost → withdraw; outside the
    /// 25 m Interact gate → Move (drift-gated retrack only after &gt; 2.0 m target
    /// motion since the last issue); inside unsettled → Stop (audited halt);
    /// settled → Interact. Credit is the engine's own objective counter plus the
    /// wake's bag census (never a world re-scan). Never advances, never turns
    /// in — those legs stay separate competitors.
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
        var source = ResolveGatherDoodad(context, fixture);
        var selected = source.ObjId;
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
        var granting = false;
        var templates = fixture.GatherDoodadTemplates;
        if (templates == null || templates.Count == 0)
            granting = doodad.TemplateId == fixture.GatherDoodadTemplate;
        else
        {
            for (var i = 0; i < templates.Count; i++)
            {
                if (templates[i] == doodad.TemplateId)
                {
                    granting = true;
                    break;
                }
            }
        }
        if (!granting)
        {
            diag = $"validate=FAIL-source-recycled:item={fixture.PreyItem}:need={fixture.Need}:doodad={source.TemplateId}:have={have}:target={selected}:template={doodad.TemplateId}:rangeM=NA:dispatch=withdrawn:reason=source-recycled";
            return null;
        }
        if (doodad.Despawn > DateTime.MinValue)
        {
            diag = $"validate=FAIL-source-despawn:item={fixture.PreyItem}:need={fixture.Need}:doodad={fixture.GatherDoodadTemplate}:have={have}:target={selected}:rangeM=NA:dispatch=withdrawn:reason=source-despawn";
            return null;
        }
        var doodadPos = doodad.Transform.World.Position;
        var dist = MathUtil.CalculateDistance(actorPos.Value, doodadPos, false);
        var doodadTemplate = source.TemplateId != 0 ? source.TemplateId : doodad.TemplateId;
        // The skill rides the row's staged id — EXCEPT a fully-derived row (set
        // non-empty) always resolves the CHOSEN template's skill live off its
        // func tables. A hand-built row (empty set) keeps its staged skill —
        // the regression seam: GatherUseSkill 0 stays skill-less, and a staged
        // non-zero id is never overwritten. The leg never invents a skill id.
        var useSkill = fixture.GatherDoodadTemplates.Count != 0
            ? source.UseSkill
            : fixture.GatherUseSkill;
        var prefix = $"validate=ok:item={fixture.PreyItem}:need={fixture.Need}:doodad={doodadTemplate}:have={have}:target={selected}:rangeM={M(dist)}";
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
                rationale: $"quest {questId} gather: well {doodadTemplate} ({selected}) at {M(dist)}m flat (outside {GatherInteractRadiusM:F1}m gate), drift {driftText} — closing",
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
                rationale: $"quest {questId} gather: well {doodadTemplate} ({selected}) at {M(dist)}m flat (inside {GatherInteractRadiusM:F1}m gate) — hold, no interact yet",
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
        diag = $"{prefix}:dispatch=interact:reason=settled:skill={useSkill}";
        return new BotDecisionProposal(
            goal: GatherGoal,
            action: ActorActionType.Interact,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} gather interact on doodad {selected} delivered",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:gather-interact:{questId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {questId} gather: well {doodadTemplate} ({selected}) at {M(dist)}m flat (inside {GatherInteractRadiusM:F1}m gate), settled — Interact draw",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveGatherPriority,
            tieBreakKey: $"gather:{questId}:{selected:D10}",
            skillId: useSkill,
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
    /// True when the quest's interaction objective already carries its required
    /// count. The gather credit helper's twin for the interaction act: reads the
    /// act's live objective counter off the SAME quest object the leg already
    /// holds, resolving the <c>QuestActObjInteraction</c> from the static
    /// template tables — game data, never a world/perception read.
    /// Fail-closed: an unreadable template or a missing act reads NOT credited,
    /// so the leg keeps working rather than silently declaring victory.
    /// </summary>
    internal static bool InteractCredited(uint questId, Quest quest, QuestFixtureRow fixture)
    {
        var interact = QuestManager.Instance?.GetTemplate(questId)?
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjInteraction>()
            .FirstOrDefault(a => a.ActId == fixture.InteractActId);
        if (interact == null)
            return false;
        return interact.GetObjective(quest) >= Math.Max(1, interact.Count);
    }

    /// <summary>
    /// Interact-with-doodad leg entry gate: the step machine has work only for
    /// an active, non-Ready quest, the objective leg has work only while the
    /// interaction objective is not yet credited, and the leg needs a source —
    /// a perceived doodad of the row's own
    /// <c>InteractDoodadTemplate</c> — to walk to. The gate owns all three
    /// reasons, so the loop's evidence names the real cause of the withdraw.
    /// Never dispatches; the resolve is a read-only census of the wake's own
    /// perception snapshot.
    /// </summary>
    internal static string? InteractEnter(QuestLegContext context, QuestLegWake wake)
    {
        var quest = context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        if (quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
            return "quest-not-usable";
        var fixture = context.Fixture!;
        if (InteractCredited(context.QuestId, quest, fixture))
            return "objective-credited";
        return ResolveInteractDoodad(context, fixture) == 0 ? "interact-no-source" : null;
    }

    /// <summary>
    /// The wake's interact source: the first perceived doodad objId whose
    /// template is the row's own <c>InteractDoodadTemplate</c> (perception
    /// order — the leg never re-sorts). 0 when the row names no template or
    /// none is perceived. Read-only over the wake's own snapshot — never a
    /// world scan, never stored.
    /// </summary>
    private static uint ResolveInteractDoodad(QuestLegContext context, QuestFixtureRow fixture)
    {
        if (fixture.InteractDoodadTemplate == 0)
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
            if (doodad != null && doodad.TemplateId == fixture.InteractDoodadTemplate)
                return objId;
        }
        return 0;
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
    /// Interact-with-doodad leg: work the perceived seedling through the real
    /// skill-bound <c>Interact</c> contract with the row's own
    /// <c>InteractUseSkill</c> (read off the seedling's func tables — the
    /// FakeUse row carries the watering skill; no new actor verb). Per wake:
    /// source lost → withdraw; outside the 25 m Interact gate → Move
    /// (drift-gated retrack only after &gt; 2.0 m target motion since the last
    /// issue); inside unsettled → Stop (audited halt); settled → Interact.
    /// Credit is the engine's own objective counter (the skill pipeline's
    /// InteractionEffect emits the quest interaction event — never a world
    /// re-scan). Never advances, never turns in — those legs stay separate
    /// competitors. A skill-0 row never reaches this leg (the director's own
    /// skill gate refuses the plan) — the leg still fails closed on one.
    /// </summary>
    internal static BotDecisionProposal? InteractEmit(QuestLegContext context, ref string diag, ref int hpBefore)
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
            diag = $"validate=quest-not-active:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:rangeM=NA:dispatch=withdrawn:reason=quest-not-active";
            return null;
        }
        if (fixture.InteractUseSkill == 0)
        {
            diag = $"validate=no-skill:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill=0:rangeM=NA:dispatch=withdrawn:reason=no-skill";
            return null;
        }
        if (InteractCredited(questId, quest, fixture))
        {
            diag = $"validate=objective-credited:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:rangeM=NA:dispatch=withdrawn:reason=objective-credited";
            return null;
        }
        var selected = ResolveInteractDoodad(context, fixture);
        if (selected == 0)
        {
            diag = $"validate=no-source:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:rangeM=NA:dispatch=withdrawn:reason=no-source";
            return null;
        }
        var character = actor.Character;
        var actorPos = character?.Transform.World.Position;
        var doodad = character?.ParentWorld?.GetDoodad(selected);
        if (character == null || !actorPos.HasValue || character.ParentWorld == null || doodad == null)
        {
            diag = $"validate=FAIL-source-lost:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:target={selected}:rangeM=NA:dispatch=withdrawn:reason=source-lost";
            return null;
        }
        if (doodad.TemplateId != fixture.InteractDoodadTemplate)
        {
            diag = $"validate=FAIL-source-recycled:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:target={selected}:template={doodad.TemplateId}:rangeM=NA:dispatch=withdrawn:reason=source-recycled";
            return null;
        }
        if (doodad.Despawn > DateTime.MinValue)
        {
            diag = $"validate=FAIL-source-despawn:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:target={selected}:rangeM=NA:dispatch=withdrawn:reason=source-despawn";
            return null;
        }
        var doodadPos = doodad.Transform.World.Position;
        var dist = MathUtil.CalculateDistance(actorPos.Value, doodadPos, false);
        var prefix = $"validate=ok:doodad={fixture.InteractDoodadTemplate}:need={fixture.InteractNeed}:skill={fixture.InteractUseSkill}:target={selected}:rangeM={M(dist)}";
        if (dist > InteractRadiusM)
        {
            var (liveMove, legLive, driftText) = InteractLegState(actor, selected, doodadPos);
            if (legLive)
            {
                diag = $"{prefix}:dispatch=held:reason=drift-held(drift={driftText})";
                return null;
            }
            diag = $"{prefix}:dispatch=move:reason={(liveMove ? "retrack" : driftText)}";
            return new BotDecisionProposal(
                goal: InteractGoal,
                action: ActorActionType.Move,
                targetId: selected,
                expectedPostcondition: new BotProposalPostcondition(
                    $"interact leg toward quest {questId} doodad {selected} dispatched",
                    _ => true),
                idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:interact:{questId}:{selected}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {questId} interact: seedling {fixture.InteractDoodadTemplate} ({selected}) at {M(dist)}m flat (outside {InteractRadiusM:F1}m gate), drift {driftText} — closing",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectiveInteractPriority,
                tieBreakKey: $"interact:{questId}:{selected:D10}",
                destination: doodadPos,
                hardPreconditions:
                [
                    new BotProposalPrecondition($"quest-{questId}-relevant",
                        observed => observed.ActiveQuestIds.Contains(questId)),
                    SurvivalVetoClear(actor)
                ]);
        }
        if (!InteractHoldConfirmed(actor, selected, actorPos.Value, doodadPos))
        {
            diag = $"{prefix}:dispatch=stop:reason=in-range";
            return new BotDecisionProposal(
                goal: InteractGoal,
                action: ActorActionType.Stop,
                targetId: selected,
                expectedPostcondition: new BotProposalPostcondition(
                    $"holding at {M(dist)}m off quest {questId} doodad {selected} (stop-before-interact)",
                    _ => true),
                idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:interact-stop:{questId}:{selected}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {questId} interact: seedling {fixture.InteractDoodadTemplate} ({selected}) at {M(dist)}m flat (inside {InteractRadiusM:F1}m gate) — hold, no interact yet",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectiveInteractPriority,
                tieBreakKey: $"interact:{questId}:{selected:D10}",
                hardPreconditions:
                [
                    new BotProposalPrecondition($"quest-{questId}-relevant",
                        observed => observed.ActiveQuestIds.Contains(questId)),
                    SurvivalVetoClear(actor)
                ]);
        }
        diag = $"{prefix}:dispatch=interact-with:reason=settled:skill={fixture.InteractUseSkill}";
        return new BotDecisionProposal(
            goal: InteractGoal,
            action: ActorActionType.InteractWith,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} interact on doodad {selected} delivered",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:interact-use:{questId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {questId} interact: seedling {fixture.InteractDoodadTemplate} ({selected}) at {M(dist)}m flat (inside {InteractRadiusM:F1}m gate), settled — InteractWith watering (skill {fixture.InteractUseSkill} derived)",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveInteractPriority,
            tieBreakKey: $"interact:{questId}:{selected:D10}",
            hardPreconditions:
            [
                new BotProposalPrecondition($"quest-{questId}-relevant",
                    observed => observed.ActiveQuestIds.Contains(questId)),
                SurvivalVetoClear(actor)
            ]);
    }
    /// <summary>
    /// True when the quest's supplied-gather objective already carries its
    /// required count. The gather credit helper's twin for the supply path:
    /// reads the act's live objective counter off the SAME quest object the
    /// leg already holds, resolving the <c>QuestActObjItemGather</c> from the
    /// static template tables — game data, never a world/perception read.
    /// Fail-closed: an unreadable template or a missing act reads NOT
    /// credited, so the leg keeps working rather than silently declaring
    /// victory.
    /// </summary>
    private static bool DeliverCredited(uint questId, Quest quest, QuestFixtureRow fixture)
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
    /// Supplied-gather leg entry gate: the step machine has work only for an
    /// active, non-Ready quest. The monitor enters on EVERY such wake —
    /// including the Supply/Progress pre-Ready stages where the supply receipt
    /// already credited the objective — so the lane observes the delivery
    /// (the Progress window is transient: one advance drains it to Ready,
    /// where this gate withdraws). The gate owns the withdraw reason, so the
    /// loop's evidence names the real cause. The monitor needs NO world source
    /// (the Supply receipt is in the bag), so there is no second reason —
    /// never dispatches, never a world scan, never stored.
    /// </summary>
    internal static string? DeliverEnter(QuestLegContext context, QuestLegWake wake)
    {
        var quest = context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        return quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed }
            ? "quest-not-usable"
            : null;
    }
    /// <summary>
    /// Supplied-gather monitor leg: name the delivery state per wake, then
    /// yield. The objective item rides the quest's own Supply act — the step
    /// machine grants it through the Advance leg on accept, and the engine's
    /// own objective counter plus the wake's bag census observe the credit
    /// (never a world re-scan, never an injection). The monitor NEVER
    /// dispatches: it returns null with a validate/credit diag fragment so
    /// the selector sees Advance (Progress) and TurnIn (Ready) unopposed,
    /// and the lane reads the basil carry per wake — including the FIRST
    /// split: accept at the 9789 giver, report at the 10857 reporter. When
    /// the objective is already credited the monitor still fires an
    /// observation hit (dispatch=monitor:reason=objective-credited) instead of
    /// withdrawing: the Supply→Ready drain is a single advance, so Progress is
    /// transient and a resting Ready quest has no monitor wake otherwise.
    /// </summary>
    internal static BotDecisionProposal? DeliverEmit(QuestLegContext context, ref string diag, ref int hpBefore)
    {
        var actor = context.Actor;
        var fixture = context.Fixture!;
        var questId = context.QuestId;
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
        {
            diag = $"validate=quest-not-active:item={fixture.PreyItem}:need={fixture.Need}:supply={fixture.SupplyItem}:have=NA:dispatch=withdrawn:reason=quest-not-active";
            return null;
        }
        if (fixture.SupplyItem == 0 || fixture.PreyItem != fixture.SupplyItem)
        {
            diag = $"validate=unproven-source:item={fixture.PreyItem}:need={fixture.Need}:supply={fixture.SupplyItem}:have=NA:dispatch=withdrawn:reason=unproven-source";
            return null;
        }
        var have = 0;
        if (context.Observation?.BagItemCounts.TryGetValue(fixture.PreyItem, out var held) == true)
            have = held;
        if (DeliverCredited(questId, quest, fixture))
        {
            diag = $"validate=objective-credited:item={fixture.PreyItem}:need={fixture.Need}:supply={fixture.SupplyItem}:have={have}:accept=9789:report=10857:split=accept-9789-report-10857:dispatch=monitor:reason=objective-credited";
            return null;
        }
        diag = $"validate=ok:item={fixture.PreyItem}:need={fixture.Need}:supply={fixture.SupplyItem}:have={have}:accept=9789:report=10857:split=accept-9789-report-10857:dispatch=monitor:reason=supply-credit";
        return null;
    }

    /// <summary>
    /// The interact leg's own reading of the leg IT dispatched (the pursuit
    /// discipline, kept for this caller's lane vocabulary): whether a Move leg of
    /// ours is live at all, and — when there is one — whether it still serves the
    /// seedling (motion since the last issue under <see cref="PursuitRetrackDriftM"/>).
    /// <c>driftText</c> is the caller's own drift reading (<c>fresh</c> before any
    /// issue).
    /// </summary>
    private static (bool LiveMove, bool LegLive, string DriftText) InteractLegState(
        IGameplayActor actor, uint doodadObjId, Vector3 doodadPos)
    {
        var liveMove = actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }
            && actor.ActiveRequest.TargetId == doodadObjId
            && actor.ActiveRequest.MoveOwner == InteractMoveOwner;
        var driftText = "fresh";
        var legLive = liveMove;
        lock (PursuitSync)
        {
            if (LastInteractIssue.TryGetValue(actor.ActorId, out var last) && last.TargetObjId == doodadObjId)
            {
                var drift = Vector3.Distance(last.TargetPos, doodadPos);
                driftText = $"{drift.ToString("F1", CultureInfo.InvariantCulture)}m";
                legLive = liveMove && drift <= PursuitRetrackDriftM;
            }
        }
        return (liveMove, legLive, driftText);
    }

    /// <summary>
    /// The interact leg's settled-halt memory: a Stop already landed for this
    /// seedling at (nearly) these poses. Read-only over the shared hold memory.
    /// </summary>
    private static bool InteractHoldConfirmed(
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
    /// True when the quest's talk objective already carries its required count.
    /// The item-use credit helper's twin for the talk family: reads the act's
    /// live objective counter off the SAME quest object the leg already holds
    /// (<c>QuestActTemplate.GetObjective</c>), resolving the
    /// <c>QuestActObjTalk</c> / <c>QuestActObjTalkNpcGroup</c> act by the row's
    /// own act id off the static template tables — game data, never a
    /// world/perception read. Fail-closed: an unreadable template or a missing
    /// act reads NOT credited, so the leg keeps working rather than silently
    /// declaring victory.
    /// </summary>
    internal static bool TalkCredited(uint questId, Quest quest, QuestFixtureRow fixture)
    {
        var talk = QuestManager.Instance?.GetTemplate(questId)?
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .FirstOrDefault(a => a.ActId == fixture.TalkActId
                && a is QuestActObjTalk or QuestActObjTalkNpcGroup);
        if (talk == null)
            return false;
        return talk.GetObjective(quest) >= Math.Max(1, talk.Count);
    }

    /// <summary>
    /// Talk-objective leg entry gate: the step machine has work only for an
    /// active, non-Ready quest, and the objective leg has work only while the
    /// talk objective is not yet credited. The gate owns both reasons so the
    /// loop's evidence names the real cause of the withdraw. Never dispatches;
    /// the credit check is a read off the static template tables and the live
    /// quest object.
    /// </summary>
    internal static string? TalkEnter(QuestLegContext context, QuestLegWake wake)
    {
        var quest = context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        if (quest is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
            return "quest-not-usable";
        var fixture = context.Fixture!;
        return TalkCredited(context.QuestId, quest, fixture)
            ? "objective-credited"
            : null;
    }

    /// <summary>
    /// The talk leg's settled-halt memory: a Stop already landed for this npc at
    /// (nearly) these poses. Read-only over the shared hold memory — the same
    /// discipline the gather/interact legs hold, so a settled actor yields its
    /// wake to the Talk proposal instead of re-issuing Stop forever.
    /// </summary>
    private static bool TalkHoldConfirmed(IGameplayActor actor, uint npcObjId, Vector3 actorPos, Vector3 npcPos)
    {
        lock (PursuitSync)
        {
            return LastStopHold.TryGetValue(actor.ActorId, out var hold)
                   && hold.TargetObjId == npcObjId
                   && Vector3.Distance(hold.ActorPos, actorPos) <= 0.5f
                   && Vector3.Distance(hold.TargetPos, npcPos) <= 0.5f;
        }
    }

    /// <summary>
    /// The npc the talk objective names, resolved from the wake's OWN perception
    /// snapshot only — never a world scan. A single-npc talk matches the act's
    /// npc template exactly; a group talk matches any perceived npc whose
    /// template passes the engine's own group membership test
    /// (<c>QuestManager.CheckGroupNpc</c> — the same predicate
    /// <c>QuestActObjTalkNpcGroup.OnTalkNpcGroupMade</c> and
    /// <c>IGameplayActor.Talk</c> gate on, so the leg can never select an npc
    /// the engine would refuse to credit). Nearest-first with an ObjId
    /// tiebreak (the deterministic-before-any-cut ordering the selector uses).
    /// Returns 0 when nothing perceived matches; the caller withdraws by name.
    /// </summary>
    private static uint ResolveTalkNpc(IGameplayActor actor, QuestFixtureRow fixture, BotObservedContext? observation)
    {
        var character = actor.Character;
        var world = character?.ParentWorld;
        if (world == null || observation == null)
            return 0;

        var actorPos = character!.Transform.World.Position;
        var bestObjId = 0u;
        var bestDistance = double.MaxValue;
        foreach (var objId in observation.NearbyNpcObjIds)
        {
            var npc = world.GetNpc(objId);
            if (npc == null || !TalkTargetMatches(fixture, npc.TemplateId))
                continue;
            var distance = MathUtil.CalculateDistance(actorPos, npc.Transform.World.Position, false);
            if (distance < bestDistance || (distance == bestDistance && objId < bestObjId))
            {
                bestObjId = objId;
                bestDistance = distance;
            }
        }
        return bestObjId;
    }

    /// <summary>
    /// Whether an npc template is one this quest's talk objective credits: the
    /// single-npc act matches its own npc template, the group act matches any
    /// template the engine's own group membership test accepts. An objective
    /// that names neither matches nothing — the director's source gate refuses
    /// such a plan before this leg ever runs.
    /// </summary>
    private static bool TalkTargetMatches(QuestFixtureRow fixture, uint npcTemplateId)
        => fixture.TalkNpcTemplate != 0
            ? npcTemplateId == fixture.TalkNpcTemplate
            : fixture.TalkNpcGroup != 0 && QuestManager.Instance.CheckGroupNpc(fixture.TalkNpcGroup, npcTemplateId);

    /// <summary>
    /// Talk-objective leg: hold the conversation with the npc the objective
    /// names, through the real <c>Talk</c> contract — the same registry-green
    /// verb (FROZEN G2) the return leg's dialogue fallback rides, and the exact
    /// request that fires the <c>CSQuestTalkMadePacket</c> whose
    /// <c>QuestManager.DoTalkMadeEvents</c> raises the objective's own
    /// <c>OnTalkMade</c> / <c>OnTalkNpcGroupMade</c> from. Per wake: the target
    /// is resolved from the wake's own perception snapshot (never a world scan);
    /// outside the 25 m Talk engine gate → Move (drift-gated retrack only after
    /// &gt; <see cref="PursuitRetrackDriftM"/> of target motion since the last
    /// issue); inside but unsettled → Stop (audited halt); settled → Talk.
    /// Credit is the engine's own objective counter (never a world re-scan, never
    /// a synthetic credit). Never advances, never turns in — those legs stay
    /// separate competitors.
    /// </summary>
    internal static BotDecisionProposal? TalkEmit(QuestLegContext context, ref string diag, ref int hpBefore)
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
            diag = $"validate=quest-not-active:npc={fixture.TalkNpcTemplate}:group={fixture.TalkNpcGroup}:act={fixture.TalkActId}:target=-:rangeM=NA:dispatch=withdrawn:reason=quest-not-active";
            return null;
        }

        var selected = ResolveTalkNpc(actor, fixture, context.Observation);
        var npc = selected == 0 ? null : actor.Character.ParentWorld?.GetNpc(selected);
        if (npc == null)
        {
            diag = $"validate=no-target:npc={fixture.TalkNpcTemplate}:group={fixture.TalkNpcGroup}:act={fixture.TalkActId}:target=-:rangeM=NA:dispatch=withdrawn:reason=no-target";
            return null;
        }
        // The objId is re-read every wake from the snapshot; a recycled npc (an
        // objId that came back carrying a different template) is refused by name
        // rather than talked to, exactly as the gather/interact legs refuse a
        // recycled source.
        if (!TalkTargetMatches(fixture, npc.TemplateId))
        {
            diag = $"validate=FAIL-target-recycled:npc={fixture.TalkNpcTemplate}:group={fixture.TalkNpcGroup}:act={fixture.TalkActId}:target={selected}:template={npc.TemplateId}:rangeM=NA:dispatch=withdrawn:reason=target-recycled";
            return null;
        }

        var actorPos = actor.Character.Transform.World.Position;
        var npcPos = npc.Transform.World.Position;
        var dist = MathUtil.CalculateDistance(actorPos, npcPos, false);
        var prefix = $"validate=ok:npc={fixture.TalkNpcTemplate}:group={fixture.TalkNpcGroup}:act={fixture.TalkActId}:target={selected}:rangeM={M(dist)}";

        // NO APPROACH BRANCH, BY DESIGN. The Talk engine gate and the
        // perception window are the SAME radius: the gate is
        // GameplayActor.MaxInteractRange (25 m) and ResolveTalkNpc selects only
        // from observation.NearbyNpcObjIds, which GameplayActor.Observe reads
        // as WorldManager.GetAround<Npc>(Character, 25f). So every target this
        // leg can resolve is by construction already inside the gate, and an
        // out-of-range branch here would be unreachable — you cannot approach a
        // target you cannot perceive. Widening the approach radius would not fix
        // that either; it would only hide the fact until target resolution was
        // re-plumbed off the spawner table, which is a separate change and not
        // this leg's business. ARRIVAL IS THE TRAVEL RESOLVER'S JOB — see
        // ResolveQuestTravelTarget / ResolveQuestObjectiveSource, which already
        // walk to spawners. Do not re-add a Move leg here without first changing
        // how the leg finds its target.
        if (dist > TalkRadiusM)
        {
            // Defensive only: unreachable while the two radii stay equal. A
            // guard rather than a branch, so a future divergence refuses by name
            // instead of dispatching a conversation the actor would reject.
            diag = $"{prefix}:dispatch=withdrawn:reason=out-of-gate";
            return null;
        }
        if (!TalkHoldConfirmed(actor, selected, actorPos, npcPos))
        {
            diag = $"{prefix}:dispatch=stop:reason=in-range";
            return new BotDecisionProposal(
                goal: TalkGoal,
                action: ActorActionType.Stop,
                targetId: selected,
                expectedPostcondition: new BotProposalPostcondition(
                    $"holding at {M(dist)}m off quest {questId} npc {selected} (stop-before-talk)",
                    _ => true),
                idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:talk-stop:{questId}:{selected}",
                timeout: TimeSpan.FromSeconds(30),
                rationale: $"quest {questId} talk: npc {npc.TemplateId} ({selected}) at {M(dist)}m flat (inside {TalkRadiusM:F1}m gate) — hold, no talk yet",
                policyVersion: opts.PolicyVersion,
                priority: opts.ObjectiveTalkPriority,
                tieBreakKey: $"talk:{questId}:{selected:D10}",
                hardPreconditions:
                [
                    new BotProposalPrecondition($"quest-{questId}-relevant",
                        observed => observed.ActiveQuestIds.Contains(questId)),
                    SurvivalVetoClear(actor)
                ]);
        }

        diag = $"{prefix}:dispatch=talk:reason=settled";
        return new BotDecisionProposal(
            goal: TalkGoal,
            action: ActorActionType.Talk,
            targetId: selected,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} talk objective credited (npc {selected})",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:talk-verb:{questId}:{selected}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {questId} talk: npc {npc.TemplateId} ({selected}) at {M(dist)}m flat (inside {TalkRadiusM:F1}m gate), settled — Talk",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveTalkPriority,
            tieBreakKey: $"talk:{questId}:{selected:D10}",
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
    ///
    /// Link-1 withdraw: a steady Progress quest whose uncredited gather/interact
    /// objective's source is known yet lies outside the 25 m perception
    /// census also withdraws the advance — the step machine would no-op on the
    /// steady step while the quest-travel arm walks the actor to the spawner.
    /// Real Start/Supply-drain and credit-then-advance wakes keep landing: the
    /// withdraw fires only when a known spawner sits outside perception range,
    /// never on credited objectives, unknown spawners, or sources already in
    /// range. The gate owns this reason too.
    ///
    /// Give-up arm: a quest whose objective slice has resolved NO target for
    /// <see cref="QuestDecisionScenario.QuestOptions.GiveUpAfterUnresolvedWakes"/>
    /// consecutive wakes is one this world cannot serve, so the advance leg stops
    /// re-issuing the same no-op AdvanceQuest on every remaining wake — the
    /// observed stall was 36 543 landed AdvanceQuest requests for quest 251,
    /// byte-identical apart from cycle_id, step and status unchanged. The count is
    /// the SAME per-(actor, quest) streak the funnel already increments and gives
    /// up at, read through <see cref="TryGetGiveUpStreak"/>: no second counter,
    /// no second threshold, no backoff. A threshold of 0 disables the arm, exactly
    /// as it disables the give-up verdict itself. The streak's own NAMED reason
    /// stays where the funnel already publishes it (the give-up diag and the run
    /// result's GiveUps); this reason carries the wake count, so the DECIDE
    /// bracket stays short. Resolution clears the streak, so the arm is
    /// recoverable, never permanent.
    /// </summary>
    internal static string? AdvanceEnter(QuestLegContext context, QuestLegWake wake)
    {
        if (context.Actor.Character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId) is not { Status: not QuestStatus.Ready and not QuestStatus.Completed })
            return "quest-not-advanceable";
        if (HasUncreditedSourceOutsidePerception(context, context.Fixture))
            return "steady-progress-uncredited-source";
        var threshold = context.Options.GiveUpAfterUnresolvedWakes;
        if (threshold > 0
            && TryGetGiveUpStreak(context.Actor.ActorId, context.QuestId, out var streak)
            && streak is { } counted
            && counted.Wakes >= threshold)
            return $"give-up-threshold (funnel unresolved {counted.Wakes} wakes)";
        return null;
    }

    /// <summary>
    /// True when the quest sits at a steady Progress step whose uncredited
    /// gather/interact objective's source is known (a spawner exists)
    /// yet lies outside the 25 m perception census — the branch-1b arm
    /// condition. Same reads as the travel fallback's objective-source
    /// resolver (fixture templates + credit counters + spawner scan), never a
    /// second scan discipline. The NPC-loot arm uses the same derived prey,
    /// living nearby census and first-spawner lookup as ordinary quest travel.
    /// Start/Supply/Deliver drain and credited-objective wakes keep landing.
    /// Fail-closed: an unreadable quest, fixture, world, or spawner surface
    /// reads false (the advance keeps landing) rather than withdrawing work
    /// the step machine could do.
    /// </summary>
    internal static bool HasUncreditedSourceOutsidePerception(QuestLegContext context, QuestFixtureRow? row)
    {
        var character = context.Actor.Character;
        var quest = character.Quests?.ActiveQuests.GetValueOrDefault(context.QuestId);
        if (quest is not { Status: QuestStatus.Progress })
            return false;
        if (row == null)
            return false;
        var world = character.ParentWorld;
        if (world?.SpawnManager == null)
            return false;
        var here = character.Transform.World.Position;
        // (1) requirements: the fixture row's granting set (single-id rows fall
        // back to the single id). (2) nearby: a perceived granting source
        // short-circuits travel — the gather leg owns this wake, no spawner walk.
        // (3) distance: the nearest spawner across ALL granting templates.
        if (!GatherCredited(context.QuestId, quest, row)
            && HasGatherTemplates(row)
            && ResolveGatherDoodad(context, row).ObjId == 0
            && TryNearestGatherSpawnerPosition(world, row, here, out _, out var gatherPos)
            && MathUtil.CalculateDistance(here, gatherPos, false) > GameplayActor.MaxInteractRange)
            return true;
        if (row.InteractDoodadTemplate != 0 && !InteractCredited(context.QuestId, quest, row))
        {
            if (TryNearestDoodadSpawnerPosition(world, row.InteractDoodadTemplate, here, out var interactPos)
                && MathUtil.CalculateDistance(here, interactPos, false) > GameplayActor.MaxInteractRange)
                return true;
        }
        // The same NPC-loot source arm as ordinary quest travel. A steady,
        // unfinished objective cannot advance while its prey is still outside
        // perception; leave that wake to the existing travel fallback.
        if (row.GatherActId != 0 && row.PreyTemplate != 0
            && row.ObjectivePattern != AAEmu.Game.Models.Game.Quests.Director.QuestPattern.Deliver
            && !GatherCredited(context.QuestId, quest, row)
            && (!HasGatherTemplates(row) || ResolveGatherDoodad(context, row).ObjId == 0)
            && !HasPerceivedLivingPrey(context.Actor,
                context.Observation ?? BotObservedContext.Capture(context.Actor), row.PreyTemplate)
            && TryNpcSpawnerPosition(world, row.PreyTemplate) is { } preyPos
            && MathUtil.CalculateDistance(here, preyPos, false) > GameplayActor.MaxInteractRange)
            return true;
        return false;
    }

    // Shared by the Advance entry gate and ordinary quest travel. Preserve the
    // existing first-matching spawner policy; unknown tables remain unknown.
    internal static Vector3? TryNpcSpawnerPosition(WorldInstance world, uint npcTemplateId)
    {
        var allSpawners = world.SpawnManager?.GetAllSpawners();
        if (allSpawners == null)
            return null;
        var spawner = allSpawners
            .SelectMany(s => s.Value)
            .FirstOrDefault(s => s.UnitId == npcTemplateId);
        return spawner == null
            ? null
            : new Vector3(spawner.Position.X, spawner.Position.Y, spawner.Position.Z);
    }

    internal static bool HasPerceivedLivingPrey(IGameplayActor actor, BotObservedContext snapshot, uint preyTemplate)
    {
        var world = actor.Character.ParentWorld;
        foreach (var objId in snapshot.NearbyNpcObjIds)
        {
            if (world?.GetNpc(objId) is { Hp: > 0 } live && live.TemplateId == preyTemplate)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the row names any gather source: a non-empty granting set, or
    /// a hand-built single id (the shared layer owns the set; single-id rows
    /// predate it and keep working through the same fallback).
    /// </summary>
    internal static bool HasGatherTemplates(QuestFixtureRow row)
        => (row.GatherDoodadTemplates != null && row.GatherDoodadTemplates.Count != 0)
            || row.GatherDoodadTemplate != 0;

    /// <summary>
    /// One ranked spawner candidate: which granting template owns it, where it
    /// is, and how far (flat) the actor stands from it. The brain input shape
    /// for the objective-source decision — the Decide chain owns the
    /// travel/withdraw verdict over this frozen ranking, never the resolver.
    /// </summary>
    public readonly record struct GatherSpawnerCandidate(uint TemplateId, Vector3 Position, float DistanceM);

    /// <summary>
    /// The ranked candidate set for the gather objective: every spawner of
    /// every granting template (row set, else the single id), each with its
    /// flat distance from <paramref name="here"/>, ascending with a template-id
    /// tiebreak. Empty when no spawner exists (or the surfaces are unreadable) —
    /// the caller's unresolvable-source signal. Pure data-layer scan: no
    /// perception read, no dispatch, no verdict — the brain decides over it.
    /// </summary>
    public static IReadOnlyList<GatherSpawnerCandidate> RankGatherSpawnerCandidates(
        WorldInstance world, QuestFixtureRow row, Vector3 here)
    {
        if (world?.SpawnManager == null || row == null)
            return Array.Empty<GatherSpawnerCandidate>();
        List<uint> templates;
        if (row.GatherDoodadTemplates != null && row.GatherDoodadTemplates.Count != 0)
            templates = new List<uint>(row.GatherDoodadTemplates);
        else if (row.GatherDoodadTemplate != 0)
            templates = new List<uint> { row.GatherDoodadTemplate };
        else
            return Array.Empty<GatherSpawnerCandidate>();
        List<GatherSpawnerCandidate>? ranked = null;
        foreach (var templateId in templates)
        {
            List<DoodadSpawner> spawners;
            try
            {
                spawners = world.SpawnManager.GetDoodadSpawnersByUnitId(templateId);
            }
            catch
            {
                continue;
            }
            if (spawners == null)
                continue;
            foreach (var spawner in spawners)
            {
                if (spawner?.Position == null)
                    continue;
                var p = spawner.Position;
                var candidate = new Vector3(p.X, p.Y, p.Z);
                (ranked ??= new List<GatherSpawnerCandidate>()).Add(
                    new GatherSpawnerCandidate(templateId, candidate, Vector3.Distance(here, candidate)));
            }
        }
        if (ranked == null)
            return Array.Empty<GatherSpawnerCandidate>();
        ranked.Sort(static (a, b) =>
        {
            var d = a.DistanceM.CompareTo(b.DistanceM);
            return d != 0 ? d : a.TemplateId.CompareTo(b.TemplateId);
        });
        return ranked.ToArray();
    }

    /// <summary>
    /// Nearest spawner position ACROSS all granting templates (row set, else the
    /// single id) — the quest's alias accepts every granting well, so the walk
    /// targets the closest well's spawner, not the lowest-id template's. Also
    /// returns the owning template (the reason string names template +
    /// distance). False when no granting template has a spawner. The brain's
    /// travel/withdraw verdict reads <see cref="RankGatherSpawnerCandidates"/>;
    /// this is the single-winner convenience over that ranking.
    /// </summary>
    internal static bool TryNearestGatherSpawnerPosition(
        WorldInstance world, QuestFixtureRow row, Vector3 here, out uint templateId, out Vector3 position)
    {
        var ranked = RankGatherSpawnerCandidates(world, row, here);
        if (ranked.Count == 0)
        {
            templateId = 0;
            position = default;
            return false;
        }
        templateId = ranked[0].TemplateId;
        position = ranked[0].Position;
        return true;
    }

    /// <summary>Nearest spawner position for a doodad template, or false when unknown.</summary>
    internal static bool TryNearestDoodadSpawnerPosition(WorldInstance world, uint doodadTemplateId, Vector3 here, out Vector3 position)
    {
        position = default;
        List<DoodadSpawner> spawners;
        try
        {
            spawners = world.SpawnManager.GetDoodadSpawnersByUnitId(doodadTemplateId);
        }
        catch
        {
            return false;
        }
        if (spawners == null || spawners.Count == 0)
            return false;
        var best = (Vector3?)null;
        var bestDist = float.MaxValue;
        foreach (var spawner in spawners)
        {
            if (spawner?.Position == null)
                continue;
            var p = spawner.Position;
            var candidate = new Vector3(p.X, p.Y, p.Z);
            var d = Vector3.Distance(here, candidate);
            if (d < bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }
        if (best == null)
            return false;
        position = best.Value;
        return true;
    }

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
                    observed => observed.ActiveQuestIds.Contains(questId)),
                SurvivalVetoClear(actor)
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
                    observed => observed.ActiveQuestIds.Contains(questId)),
                SurvivalVetoClear(actor)
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
                    observed => observed.CurrentTargetObjId != selected),
                SurvivalVetoClear(actor)
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
            // No selection, no fight: any streak for this actor belongs to a target
            // the funnel no longer serves, so it must not survive onto the next one.
            ClearCombatStallActor(actor.ActorId);
            // G7b (observe-only): the funnel drops dead candidates, so the
            // post-death wake withdraws here — re-resolve ONLY the quest-pinned
            // objId (never a scan) and name the corpse with its probe.
            // The journey boundary: a prey identity the funnel no longer serves is
            // over, so a banked verdict must not outlive it and a LATER prey must
            // not inherit its repath budget (the return leg's own rule).
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.PursuitMoveOwner);
            var corpseFrag = ObservePinnedCorpse(actor, opts, funnel, context.Observation);
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
            // The stall streak keys on the LIVE target identity: any wake the pinned target
            // resolves lost/dead/illegal ends the fight, so the streak must end with it —
            // otherwise the next target would inherit wakes it never held.
            ClearCombatStallActor(actor.ActorId);
            // G7b (observe-only): the pinned-target dead transition names the
            // corpse with its read-only probe (all context known here: funnel
            // quest/objective, pinned objId/template/dead-state). A lost
            // target re-resolves ONLY the quest-pinned objId (never a scan).
            var corpseFrag = invalidReason == "target-dead" && npc != null
                ? NoteDeadSelection(actor, opts, funnel, npc)
                : ObservePinnedCorpse(actor, opts, funnel, context.Observation);
            diag = $"validate=FAIL-{invalidReason}:rangeM=NA:dispatch=withdrawn:reason={invalidReason}{corpseFrag}";
            return null;
        }
        npcPos = npc!.Transform.World.Position;
        // THE AUTHORITATIVE METRIC. `MathUtil.CalculateDistance(pos, pos, false)` is a
        // 2-D XZ reading with no radius subtraction, and it is the metric the ENGINE
        // refuses casts against is NOT taken on: `Skill.Use` judges the same pair with
        // `unit.GetDistanceTo(target, true)` — 3-D, less the target's model radius
        // (`BaseUnit.cs:229`) and the caster's own (`:232`). Comparing a 2-D band
        // against a 3-D-minus-radii gate makes the band systematically OPTIMISTIC by
        // the two radii plus the elevation drop, so an actor is told it is inside its
        // own band — and stands its closing leg down — while the engine measures it out
        // of range of every verb it has.
        //
        // So the band and the admission gate read the ENGINE'S OWN number, through the
        // very accessor the engine calls. Same method, same `includeZAxis: true`, same
        // radius arithmetic, same float rounding — there is no second metric left to
        // disagree, and no radius arithmetic reimplemented here.
        var dist = character!.GetDistanceTo(npc, true);

        // ------------------------------------------------------- TRAVEL DECISION
        // What this leg dispatched (its own reading of its own leg — the brain never
        // reads an actor request), then what the travel chain asks for this wake.
        // The journey is OWNER-TAGGED so the return leg's own boundary can never
        // disarm it (one actor walks one journey PER OWNING LEG).
        var legOutcome = TravelLegDispatch.MapLegOutcome(
            actor, selected, TravelLegDispatch.PursuitMoveOwner);
        var (liveMove, legLive, driftText) = PursuitLegState(actor, selected, npcPos);
        var brain = CaptureTravelPrepared(TravelLegDispatch.Prepare(
            actor, selected, npcPos, PursuitStopRadiusM, legLive, legOutcome,
            legOwner: TravelLegDispatch.PursuitMoveOwner));
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

        // THE IN-BAND YIELD. Closing is the work of a role that is not yet
        // standing where it can attack. Once the actor IS inside its own combat
        // band, re-issuing a Move costs this wake the one thing it was for: the
        // combat leg (priority 23) below has already consulted the brain at this
        // exact distance and been handed a verb that reaches, so the closing leg
        // stands down and the attack dispatches instead.
        //
        // This is the Move branch's twin of the Stop branch's
        // <see cref="CombatBrainEngagement.ShouldYieldPursuit"/> yield above —
        // same seam, same vocabulary, one settled and one closing arm. It cannot
        // be read from the published engagement, because that fact is written
        // only when a proposal WINS selection and the pursuit leg runs first, so
        // it is read from the ONE band the admission radius above already reads
        // (no second scan, no fitted constant, no role-specific branch).
        //
        // The journey ends with the arm that withdraws: the closing leg's own
        // boundary, so a commitment to walk toward a prey this actor no longer
        // needs to walk toward cannot outlive the wake.
        if (CombatOwnsInsideBand(character, dist))
        {
            // NO-PROGRESS STALL TRIP. The yield above is open-ended: when the live
            // AutoAttack loop deals no damage (refused at the range edge, mob regen
            // outpaces DPS), both legs yield null every wake forever. After
            // CombatStallWakes consecutive hold-wakes with no HP decrease, this leg
            // stops yielding and closes to PursuitStopRadiusM contact — where the
            // melee auto-attack verb always reaches — instead of holding at range.
            // Chosen over a combat-side Stop+restart: one re-dispatch path (the
            // existing Move below), no new verb, no loop-lifecycle meddling, and
            // the contact pose is the same one melee roles already fight from.
            // Fail-closed: unreadable HP (npc null here is impossible — validated
            // above — but Hp is still read live) resets inside the counter.
            if (CountCombatStallWake(actor.ActorId, selected, npc.Hp, opts.CycleId, out var stallWakes, ReadSelfDamageAttribution(character, npc, actor.ActorId)))
            {
                diag = $"validate=ok:rangeM={M(dist)}:dispatch=move:reason=stall-close-in(stall={stallWakes}wakes_hp={npc.Hp}){travelFrag}";
                return new BotDecisionProposal(
                    goal: PursuitGoal,
                    action: ActorActionType.Move,
                    targetId: selected,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"pursuit leg toward quest {fixture.QuestId} target {selected} dispatched",
                        _ => true),
                    idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:pursuit-stall:{fixture.QuestId}:{selected}",
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: $"quest {fixture.QuestId} pursuit: live {fixture.PreyTemplate} {selected} at {M(dist)}m with no HP progress for {stallWakes} wakes — closing to contact",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.ObjectivePursuitPriority,
                    tieBreakKey: $"pursuit:{fixture.QuestId}:{selected:D10}",
                    payload: payload,
                    hardPreconditions:
                    [
                        new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                            observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
                        SurvivalVetoClear(actor)
                    ]);
            }
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.PursuitMoveOwner);
            diag = $"validate=ok:rangeM={M(dist)}:dispatch=held:reason=inside-combat-band{travelFrag}";
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
                    observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
                SurvivalVetoClear(actor)
            ]);
    }

    /// <summary>
    /// THE COMBAT BRAIN'S ADMISSION RADIUS: the distance from which this actor's
    /// combat brain is consulted at all.
    ///
    /// It is the actor's OWN band ceiling (<see cref="CombatBrain.RoleBand"/> read
    /// through the engine's own <see cref="CombatDecisionTree.InferRole"/>), floored
    /// at <see cref="PursuitStopRadiusM"/>. It is deliberately NOT the pursuit stop
    /// radius reused as a combat gate: a flat, role-blind admission radius is what
    /// left the brain permanently inside a ranged role's weapon FLOOR, where no
    /// ranged verb exists, so CloseRange/Sustain could never run and the only
    /// reachable verdict was BackOff. Derived from the band, the brain is consulted
    /// from the distance where its own arms are legal.
    ///
    /// A melee role keeps the stop radius and gains only its own reach
    /// (max(3.0, 3.5) = 3.5 m, and with an unarmed rig the engine's own fist
    /// reach of 3.0 m), so it still closes to contact — and because the
    /// pursuit Stop is emitted for everything at or inside 3.0 m at the HIGHER
    /// priority, no melee wake changes owner.
    ///
    /// The ceiling is the band BOUNDED BY THE ACTOR'S REAL REACH (see
    /// <see cref="AutoAttackReachM"/>). An archer holding a 20 m bow is
    /// admitted to 20 m, not to the 22 m its role band would allow: past its own
    /// reach there is no verb the engine will accept, so admitting the actor
    /// there strands it between a closing leg that has stood down and an attack
    /// the engine refuses.
    ///
    /// This decides only whether the brain is ASKED; the engine's own range refusal
    /// (<c>Skill.Use</c> TooClose/TooFar) still owns legality at dispatch.
    /// </summary>
    internal static float CombatAdmissionRadiusM(Character? character)
    {
        var (bandMin, bandMax) = CombatBrain.RoleBand(
            character != null ? CombatDecisionTree.InferRole(character) : CombatRole.Melee);

        // A ceiling the actor's own weapon cannot reach is not a ceiling, it is a
        // parking spot: the actor is admitted, the closing leg stands down, and
        // the only verb the brain can name is an attack the engine refuses. So
        // the band ceiling is bounded by the REACH the engine will enforce for
        // the auto-attack this role actually fires, read through the engine's own
        // overwrite rule (never the union in
        // <see cref="CombatDecisionTree.GetSkillEffectiveRange"/>, which would
        // widen a 20 m bow back out to the template's 25 m and restore the very
        // gap this narrows).
        var reachMax = AutoAttackReachM(character);
        if (reachMax <= 0f)
        {
            // Unreadable skill surface: the role band stands rather than the
            // actor being admitted nowhere, or everywhere.
            return MathF.Max(PursuitStopRadiusM, bandMax);
        }

        // A reach BELOW the role's own floor is not a spacing constraint, it is an
        // unreachable band: nothing this actor can fire reaches from inside the
        // interval the role wants, so narrowing the ceiling to it would collapse a
        // ranged role onto its contact range — telling an unarmed caster to walk to
        // 3 m, which is the "make every bot melee" failure, not a fix. The band
        // stays the authority there; the brain's own DesiredBand conflict rule
        // already decides which of two unattainable bounds yields.
        var ceiling = reachMax < bandMin ? bandMax : MathF.Min(bandMax, reachMax);
        return MathF.Max(PursuitStopRadiusM, ceiling);
    }

    /// <summary>
    /// THE AUTO-ATTACK'S REAL REACH: the max range the engine will enforce for the
    /// auto-attack skill this actor would fire, or 0 when that cannot be read.
    ///
    /// This is the exact skill <c>GameplayActor.AutoAttack</c> picks, and the exact
    /// range rule it will be judged by — so the answer is read from the engine's
    /// own tables through <see cref="CombatDecisionTree.GetSkillEnforcedRange"/>,
    /// not fitted to any run. The 4.0 m / ranged-weapon branch of the skill choice
    /// is reproduced here only to answer "which skill fires at a SPACING distance",
    /// where a role that cannot reach with its weapon falls back to melee.
    /// </summary>
    private static float AutoAttackReachM(Character? character)
    {
        if (character == null)
            return 0f;

        // PeekInstance, never Instance: SkillManager is DI-only (no
        // parameterless ctor), so `Instance` THROWS in a headless rig that never
        // seeded it. A spacing decision must degrade to "reach unreadable"
        // (the role band stands), never take the whole wake down with it.
        var skillManager = Core.Managers.SkillManager.PeekInstance;
        if (skillManager == null)
            return 0f;

        var role = CombatDecisionTree.InferRole(character);
        var hasRangedWeapon = character.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Ranged) != null;

        var skillId = role == CombatRole.RangedPhysical || hasRangedWeapon
            ? CombatDecisionTree.BasicRangedAutoAttackSkillId
            : CombatDecisionTree.BasicMeleeAutoAttackSkillId;

        var template = skillManager.GetSkillTemplate(skillId);
        return template == null ? 0f : CombatDecisionTree.GetSkillEnforcedRange(template, character).MaxRange;
    }

    /// <summary>
    /// True while this actor stands INSIDE its own combat band — the interval
    /// the role wants, below its ceiling — so its brain's own spacing arms
    /// (<see cref="CombatArm.CloseRange"/> and <see cref="CombatArm.BackOff"/>)
    /// cannot apply and its next verdict is a verb that acts from here.
    ///
    /// It reads the SAME band ceiling <see cref="CombatAdmissionRadiusM"/> reads,
    /// through the same <see cref="CombatDecisionTree.InferRole"/>, so "the
    /// distance the brain is consulted from" and "the distance the closing leg
    /// stands down at" are one number derived from one rule and can never drift
    /// apart. No second scan, no per-role branch, no fitted constant.
    ///
    /// The floor is deliberately NOT part of this test: a role below its own
    /// floor still needs the closing leg, because a ranged role that cannot
    /// reach has no verb to fire — which is exactly the state the brain's
    /// BackOff arm exists to fix, and it fixes it with its own Move.
    ///
    /// This decides only whether the CLOSING leg still has work; the engine's
    /// own range refusal (<c>Skill.Use</c> TooClose/TooFar) still owns legality
    /// at dispatch.
    /// </summary>
    internal static bool CombatOwnsInsideBand(Character? character, float distanceM)
        => distanceM <= CombatAdmissionRadiusM(character);

    /// <summary>
    /// G6 combat proposal (the wired quest only): AutoAttack for the G4-selected
    /// prey objId at <c>ObjectiveCombatPriority</c>, just below pursuit 24 — so
    /// the closing leg wins while the actor is still OUTSIDE its band, and the
    /// attack wins from the moment it stands inside (see
    /// <see cref="CombatOwnsInsideBand"/>: the pursuit Move arm stands down
    /// there, so the pair can no longer deadlock a ranged role inside its own
    /// band). Target
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
            ClearCombatStallActor(actor.ActorId);
            // G7b (observe-only): post-death funnel withdrawal — re-resolve ONLY
            // the quest-pinned objId (never a scan), name the corpse + probe.
            // The kite's journey boundary: a threat identity the funnel no longer
            // serves is over, so a banked verdict must not outlive it and a LATER
            // threat must not inherit its repath budget (the pursuit leg's own
            // rule, from the same seam).
            CombatTravelDispatch.EndJourney(actor);
            var corpseFrag = ObservePinnedCorpse(actor, opts, funnel, context.Observation);
            diag = $"validate={(selected == 0 ? "no-selection" : "not-relevant")}:target=-:template=-:alive=NA:visible=NA:hostile=NA:legal=NA:distM=NA:verb=none:hpBefore=NA{corpseFrag}";
            return null;
        }
        var character = actor.Character;
        var actorPos = character?.Transform.World.Position;
        var npc = character?.ParentWorld?.GetNpc(selected);
        if (character == null || !actorPos.HasValue || character.ParentWorld == null || npc == null)
        {
            ClearCombatStallActor(actor.ActorId);
            // The threat identity is gone: the journey it armed is over (a fresh
            // resolve next wake arms a clean one) — never a banked verdict or a
            // spent budget carried onto an identity that no longer exists.
            CombatTravelDispatch.EndJourney(actor);
            var corpseFrag = ObservePinnedCorpse(actor, opts, funnel, context.Observation);
            diag = $"validate=target-lost:target={selected}:template={fixture.PreyTemplate}:alive=NA:visible=NA:hostile=NA:legal=NA:distM=NA:verb=none:hpBefore=NA{corpseFrag}";
            return null;
        }
        var alive = npc.Hp > 0;
        var hostile = alive && CombatDecisionTree.IsHostileTarget(character, npc);
        var attackable = alive && character.CanAttack(npc);
        var visible = character.CanSeeTarget(npc);
        if (!alive)
        {
            ClearCombatStallActor(actor.ActorId);
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
        // STALE-LOOP REDISPATCH. A live loop aimed at a target the funnel no
        // longer serves (selection switched after the Target leg re-pointed
        // CurrentTarget — the loop's struck target still names the old prey),
        // or a loop the stall trip already condemned (CombatStallWakes
        // no-progress hold-wakes), is torn down and re-dispatched on the
        // current selection instead of withdrawn. The loop's target is the
        // engine's own struck target (the auto-attack task's skill), read off
        // the same live character surface this gate already reads — never a
        // memory. Fail-closed: an unreadable loop target keeps the historical
        // withdraw (covers hand-raised test flags with no task behind them).
        var redispatchStaleLoop = false;
        if (character.IsAutoAttack)
        {
            // Same streak the pursuit in-band yield feeds (cycle-deduped there): an
            // already-live loop that deals no damage is the same stall from the
            // combat side. Counting here lets a wake the pursuit leg never reaches
            // (Stop branch inside 3 m) still accrue — the trip itself always fires
            // from pursuit, so there is exactly one re-dispatch path.
            var stallTripped = CountCombatStallWake(actor.ActorId, selected, npc.Hp, opts.CycleId, out _, ReadSelfDamageAttribution(character, npc, actor.ActorId));
            var loopTarget = ReadLiveLoopTarget(character);
            if (loopTarget.HasValue && (loopTarget.Value != selected || stallTripped))
            {
                // Armed: fall through to the dispatch below (range + brain), and
                // the brain-Hold gate below re-checks this flag. The re-point
                // rides the Target leg's SetTarget plus this leg's AutoAttack
                // re-affirm (the engine sets CurrentTarget when it differs and
                // the loop task strikes CurrentTarget every tick) — Emit builds
                // proposals, never mutates.
                redispatchStaleLoop = true;
            }
            else
            {
                diag = $"validate=already-live:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true:distM={M(MathUtil.CalculateDistance(actorPos.Value, npc.Transform.World.Position, false))}:verb=none:hpBefore={npc.Hp}";
                return null;
            }
        }
        // THE AUTHORITATIVE METRIC, the same accessor and the same
        // `includeZAxis: true` the pursuit leg reads and the very one
        // `Skill.Use` refuses on — so "admitted" and "the engine will let the
        // cast run" are judged by ONE number instead of two that disagree by
        // the two model radii and the elevation drop.
        var dist = character.GetDistanceTo(npc, true);
        if (dist > CombatAdmissionRadiusM(character))
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
        var combatBrain = CaptureCombatPrepared(CombatBrainPlanner.Prepare(
            actor, null, selected,
            // The rotation arm is a SEPARATE gate (see
            // QuestOptions.EnableCombatRotation): the frozen G6 lane dispatches
            // the engine's auto-attack loop, and flipping the quest path onto
            // skill casts is a behavior change with its own fixture, not a
            // side effect of building the brain.
            allowSkillCasts: opts.EnableCombatRotation && Core.Managers.SkillManager.Instance != null,
            nowUtc: DateTime.UtcNow));
        var brainDecision = CombatBrainPlanner.Decide(combatBrain);

        // THE LEASH TELEMETRY. `driftM` is the committed target's ANCHOR→TARGET
        // drift this wake — NOT the actor's contact distance already rendered
        // above as `distM`, which can read ~2 m while the leash fires — measured
        // against `leashM`, the budget it is measured by; the edge sits at
        // `leashM × CombatBrainEngagement.LeashEdgeFraction`, so one line says
        // whether the leash was just tripped or overshot by a wide margin. It is
        // a pure read of the wake's OWN already-prepared inputs (`Decide` is a
        // pass-through of `combatBrain.Inputs`), so it costs no second `Prepare`,
        // reaches no arm, and cannot move a verdict.
        //
        // TWO SAME-NAMED `already-live` GATES reach this leg and must not be
        // conflated: the pre-brain one near the top of this method
        // (`character.IsAutoAttack`) returns BEFORE `Prepare`, so it has no
        // inputs and cannot carry drift; the one below is the brain's own `Hold`
        // verdict, downstream of `Prepare`, and is the line a repeated leash
        // disengage actually logs.
        var leashFrag = $":driftM={M(combatBrain.Inputs.IncumbentLeashDriftM)}"
                        + $":leashM={M(combatBrain.Inputs.LeashBudgetM)}";

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
            // STALE-PIN WATCHDOG FEED. A Disengaged-at-LeashEdge verdict with
            // unchanged HP is the stuck pin's own signature: the mob sits past
            // its leash budget (the brain correctly refuses), the actor stands
            // in position (this leg only consults the brain inside the
            // admission radius), and no damage lands. N such wakes trip the
            // selector demotion, so the next wake re-selects a killable target
            // instead of holding Follow for thousands of wakes. Fail-closed:
            // ONLY the true LeashEdge verdict counts (a crowd-control hold or
            // a critical-hp retreat never does); any HP decrease resets inside
            // the counter; a gone target clears through ClearCombatStallActor.
            if (brainDecision is { Arm: CombatArm.Disengage, DisengageReason: CombatDisengageReason.LeashEdge })
                CountLeashStaleWake(actor.ActorId, selected, npc.Hp, opts.CycleId, out _, ReadSelfDamageAttribution(character, npc, actor.ActorId));
            diag = $"validate=ok:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true" +
                   $":distM={M(dist)}:verb=none:hpBefore={npc.Hp}:brain={brainDecision.Describe()}{leashFrag}";
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
        if (brainVerb == CombatVerb.Hold && redispatchStaleLoop)
        {
            // The pre-brain gate already condemned this loop (wrong target or
            // stall trip): the brain's own Hold names the same stale loop, so
            // fall through to re-dispatch on the current selection instead of
            // withdrawing (brainVerb rewritten, never re-decided).
            brainVerb = CombatVerb.AutoAttack;
            action = ActorActionType.AutoAttack;
        }
        if (brainVerb == CombatVerb.Hold)
        {
            // Same streak as the pre-brain gate above (cycle-deduped): the brain's
            // own Hold is the third name for "loop live, no new dispatch".
            CountCombatStallWake(actor.ActorId, selected, npc.Hp, opts.CycleId, out _, ReadSelfDamageAttribution(character, npc, actor.ActorId));
            diag = $"validate=already-live:target={selected}:template={npc.TemplateId}:alive=true:visible=true:hostile=true:legal=true" +
                   $":distM={M(dist)}:verb=none:hpBefore={npc.Hp}:brain={brainDecision.Describe()}{leashFrag}";
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
                   $":distM={M(dist)}:verb=none:hpBefore={npc.Hp}:brain={brainDecision.Describe()}{leashFrag}{kiteFrag}";
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
               $":distM={M(dist)}:verb={verbText}:hpBefore={npc.Hp}:brain={brainDecision.Describe()}{leashFrag}{kiteFrag}";
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

        var already = candidate != null && IsLootDispatched(actor.ActorId, actor.Character?.ParentWorld?.GetNpc(candidate.ObjId));

        var character = actor.Character;
        var prepared = LootBrainPlanner.Prepare(
            actor,
            context.Observation,
            new LootBrainPlanner.Request(
                CorpseObjId: candidate?.ObjId ?? 0,
                PreyTemplateId: fixture.PreyTemplate,
                MinContainerValueCopper: opts.MinContainerValueCopper,
                AlreadyLooted: already,
                // LOOP RELEASE is the CORPSE's loop, never the actor's: the kill leg
                // owns the corpse it is still killing until that kill's own teardown
                // runs, and a take dispatched inside THAT loop would race it. A live
                // loop on any other target (the measured shape: the corpse died, then
                // the actor engaged an unrelated mob) owns no teardown on this corpse
                // and must never withhold it — so the reading is the one identity
                // comparison the arm was written about. `candidate` is non-null by
                // construction: a null pin (no corpse of ours at all) resolves
                // CorpseObjId 0, and the planner reads that as an unreadable corpse
                // which the probe arm withholds before this gate is ever consulted.
                LoopLive: candidate != null && character?.IsAutoAttack == true
                          && character.CurrentTarget?.ObjId == candidate.ObjId,
                SafeRadiusM: opts.LootSafeRadiusM,
                NowUtc: DateTime.UtcNow));

        var decision = prepared.Decision;
        var validate = LootBrain.ValidateToken(decision.Reason);
        // Flat, bracket-free key list (nested brackets would nest inside the
        // DECIDE/loot brackets a lane parser scans, so the brain's own fragment
        // joins them as siblings rather than a block).
        var brainFrag = $":lootBrain={LootBrain.Describe(decision)}:{prepared.DescribeOwnership()}:corpseInstance={prepared.CorpseToken}";
        // The container tally the funnel scanners read is the probe's own
        // reading; a corpse that never resolved reports an explicit "-" exactly
        // as the pre-brain arm did.
        var containerText = prepared.CorpseResolved ? prepared.Inputs.ContainerItemCount.ToString(CultureInfo.InvariantCulture) : "-";
        var lootableText = prepared.CorpseResolved
            ? (prepared.Inputs.ContainerLootable ? "true" : "false")
            : "-";

        if (!decision.IsEmit)
        {
            // SPENT-CORPSE PIN RELEASE (kill23: same corpse re-pinned 30x). A corpse
            // this actor's own Loot verb already ran its terminal course on is DONE:
            // the loot-once arm withholds it on every later wake, so keeping the
            // LastCorpse pin makes ObservePinnedCorpse re-report it (and LootEmit
            // re-evaluate it) forever instead of moving on to the next corpse.
            // Released the same owner-scoped way the gone=true path releases it —
            // this actor's record only, approach journey ended with it — so the
            // next wake re-resolves (fresh-prey pre-emption, pin resolve, census).
            // Fail-closed: only on the actor's OWN confirmed loot-once verdict for
            // the PINNED corpse (AlreadyLooted input read from this actor's memory
            // for this corpse, planner reason AlreadyLooted, corpse resolved and
            // ObjId-equal). A foreign take, a skip, an unreadable corpse, or any
            // other withhold keeps the pin — unknown state never drops identity.
            if (decision.Reason == LootReason.AlreadyLooted && already
                && prepared.CorpseResolved && candidate != null
                && decision.CorpseObjId == candidate.ObjId)
            {
                lock (PursuitSync)
                    LastCorpse.Remove(actor.ActorId);
                LootTravelDispatch.EndJourneyForPin(actor.ActorId);
            }
            // EMPTY-CORPSE PIN RELEASE (the contended-corpse race, mirrored from
            // the census exclusion above): an empty withhold on the PINNED
            // corpse — container exists/0 items — is terminal for ANY actor
            // (one-shot generation never refills), so the pin drops the same
            // owner-scoped way. TargetEmit then commits the funnel's live
            // selection via the existing SetTarget.
            else if (candidate != null && prepared.CorpseResolved
                && decision.CorpseObjId == candidate.ObjId
                && prepared.Inputs.ContainerReadable && prepared.Inputs.ContainerItemCount == 0
                && decision.Reason is LootReason.OwnershipForeign or LootReason.OwnershipUnresolved or LootReason.ContainerEmpty)
            {
                lock (PursuitSync)
                    LastCorpse.Remove(actor.ActorId);
                LootTravelDispatch.EndJourneyForPin(actor.ActorId);
            }
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
            idempotencyKey: $"quest:{actor.ActorId}:loot:{fixture.QuestId}:{decision.CorpseObjId}:instance:{prepared.CorpseToken}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"quest {fixture.QuestId} loot: dead {fixture.PreyTemplate} {decision.CorpseObjId} container={prepared.Inputs.ContainerItemCount} — Loot once ({LootBrain.Describe(decision)})",
            policyVersion: opts.PolicyVersion,
            priority: opts.ObjectiveLootPriority,
            tieBreakKey: $"loot:{fixture.QuestId}:{decision.CorpseObjId:D10}",
            payload: prepared,
            hardPreconditions:
            [
                new BotProposalPrecondition("corpse-instance-current", _ => prepared.IsCurrent),
                new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                    observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
                SurvivalVetoClear(actor)
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
                hardPreconditions: LootApproachPreconditions(actor, fixture)),
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
    private static BotProposalPrecondition[] LootApproachPreconditions(IGameplayActor actor, QuestFixtureRow fixture)
        =>
        [
            new BotProposalPrecondition($"quest-{fixture.QuestId}-relevant",
                observed => QuestObjectiveTargetSelector.IsRelevant(observed, fixture)),
            SurvivalVetoClear(actor)
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
        var brain = CaptureTravelPrepared(TravelLegDispatch.Prepare(
            actor, reporter.ObjId, reporterPos, ReturnInteractRadiusM, legLive, legOutcome,
            legOwner: TravelLegDispatch.ReturnMoveOwner));
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
                            observed => observed.ActiveQuestIds.Contains(fixture.QuestId)),
                        SurvivalVetoClear(actor)
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
                            observed => observed.ActiveQuestIds.Contains(fixture.QuestId)),
                        SurvivalVetoClear(actor)
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
                            observed => observed.ActiveQuestIds.Contains(fixture.QuestId)),
                        SurvivalVetoClear(actor)
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
            ActorActionType.Loot when proposal.Goal == LootGoal => DispatchLoot(gameplayActor, proposal),
            ActorActionType.Move when proposal.Goal == LootGoal => DispatchLootApproachMove(gameplayActor, proposal),
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
            // Interact-with-doodad: the quest-owned seedling watering rides MoveTo
            // on the doodad's live POSITION (a doodad is BaseUnit, never Unit —
            // the position leg is the roam butcher-approach shape), the audited
            // Stop halt inside 25 m, and InteractWith once settled (the
            // skill-bound pipeline: the verb derives the row's InteractUseSkill
            // from the seedling's own func tables and emits the quest
            // interaction event through InteractionEffect — no new actor verb).
            // Goal-guarded so no other Move/Stop/InteractWith proposal can ever
            // route here.
            ActorActionType.Move when proposal.Goal == InteractGoal => DispatchInteractMove(gameplayActor, proposal),
            ActorActionType.Stop when proposal.Goal == InteractGoal => DispatchInteractStop(gameplayActor, proposal),
            ActorActionType.InteractWith when proposal.Goal == InteractGoal => gameplayActor.InteractWith(proposal.TargetId, proposal.IdempotencyKey),
            // Talk objective: the quest-owned conversation rides the audited Stop
            // halt inside the Talk engine gate, then Talk once settled — the
            // same registry-green verb the return leg's dialogue fallback rides,
            // on the packet path that raises this objective's own credit event.
            // NO Move arm by design: the talk gate and the perception window are
            // the same radius, so the leg only ever resolves an already-in-range
            // npc (see the NO APPROACH BRANCH record in TalkEmit). Arrival is the
            // travel resolver's job. Goal-guarded so no other Stop/Talk proposal
            // can ever route here.
            ActorActionType.Stop when proposal.Goal == TalkGoal => DispatchTalkStop(gameplayActor, proposal),
            ActorActionType.Talk when proposal.Goal == TalkGoal => gameplayActor.Talk(proposal.TargetId, proposal.IdempotencyKey),
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
    /// The engine's own struck target of the live auto-attack loop: the
    /// <c>InitialTarget</c> the loop's skill struck when <c>GameplayActor.AutoAttack</c>
    /// fired it (the same wake that raised <c>IsAutoAttack</c>). Null when no task
    /// runs, the skill is unreadable, or the struck target is gone — every one of
    /// those is "unknown", and the caller fails closed to the historical withdraw.
    /// </summary>
    private static uint? ReadLiveLoopTarget(Character character)
    {
        try
        {
            return character.AutoAttackTask?.Skill?.InitialTarget?.ObjId;
        }
        catch
        {
            return null;
        }
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
    /// Jev shadow: freezes one travel-brain <c>Prepared</c> this wake for the lane.
    /// Returns the SAME prepared value the caller was about to use, so the capture
    /// is observation only — the arm, destination and dispatch are byte-identical
    /// whether the flag is on or off. Flag-off is a static read and a return of
    /// the argument; null (a journey that could not be armed) stays null.
    /// </summary>
    private static TravelBrainPlanner.Prepared? CaptureTravelPrepared(TravelBrainPlanner.Prepared? prepared)
    {
        if (JevShadow.Enabled && prepared is { } value)
            JevShadow.CaptureTravel(value.Inputs.ActorObjId, value.Inputs, value.Decision);
        return prepared;
    }

    /// <summary>
    /// Jev shadow: freezes the combat-brain <c>Prepared</c> this wake for the lane
    /// and returns the same value. Observation only — the planner already ran and
    /// this adds no read, no plan, and no dispatch of its own.
    /// </summary>
    private static CombatBrainPlanner.Prepared CaptureCombatPrepared(CombatBrainPlanner.Prepared prepared)
    {
        if (JevShadow.Enabled)
            JevShadow.CaptureCombat(prepared.Inputs.ActorObjId, prepared);
        return prepared;
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
        var prepared = proposal.Payload is LootBrainPlanner.Prepared value ? value : default;
        ActorRequest request;
        if (gameplayActor is GameplayActor concrete)
            request = concrete.Loot(prepared, proposal.IdempotencyKey);
        else if (prepared.IsCurrent && ReferenceEquals(gameplayActor.Character, prepared.ExpectedCharacter))
            request = gameplayActor.Loot(proposal.TargetId, proposal.IdempotencyKey);
        else
        {
            request = new ActorRequest(ActorActionType.Loot, proposal.TargetId, null, 0, null,
                idempotencyKey: proposal.IdempotencyKey);
            request.Accept("loot proposal validation");
            request.Reject(ActorFailureReason.StateTransition, "Observed corpse was replaced before Loot dispatch.");
        }
        if (request.IsTerminal && prepared.IsCurrent
            && ReferenceEquals(gameplayActor.Character, prepared.ExpectedCharacter)
            && gameplayActor.ActorId == prepared.Inputs.ActorObjId
            && proposal.TargetId == prepared.Inputs.CorpseObjId)
        {
            lock (PursuitSync)
            {
                if (LootedCorpses.Count >= 256)
                    LootedCorpses.Clear();
                LootedCorpses[(gameplayActor.ActorId, proposal.TargetId)] = prepared.CorpseToken;
            }
            // The decision layer's own bank of the same fact: a take is recorded
            // only for a Loot verb that actually ran its terminal course, so the
            // ledger never claims a take the actor refused.
            LootBrainPlanner.PublishTake(prepared, DateTime.UtcNow);
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
    /// Interact Move dispatch: preempt a live Move leg (retrack only), issue the
    /// position leg on the seedling's live position (the roam butcher-approach
    /// shape — a doodad is BaseUnit, never Unit, so MoveToUnit cannot resolve
    /// it), and record the seedling's live position for the next wake's drift
    /// gate. Never touches _move state or queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchInteractMove(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        if (gameplayActor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move })
            gameplayActor.PreemptCurrent(InteractRetrackDetail);
        if (gameplayActor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(InteractMoveOwner);
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
                if (LastInteractIssue.Count >= 256)
                    LastInteractIssue.Clear();
                LastInteractIssue[gameplayActor.ActorId] = (proposal.TargetId, doodadPos.Value);
            }
        }
        return request;
    }
    /// <summary>
    /// Interact Stop dispatch: issue the audited Stop halt, then record both
    /// poses in the shared hold memory so the next wake's interact proposal can
    /// confirm the hold and yield to Interact. Shares <c>LastStopHold</c> with
    /// the other legs — entries are (actor, target) keyed by check, and the
    /// seedling objId never equals a prey/reporter/corpse/well target. Never
    /// touches _move state or queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchInteractStop(IGameplayActor gameplayActor, BotDecisionProposal proposal)
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
    /// Talk Stop dispatch: issue the audited Stop halt, then record both poses in
    /// the shared hold memory so the next wake's talk proposal can confirm the
    /// hold and yield to Talk (without it, Stop would win every in-range wake and
    /// the conversation could never start). Shares <c>LastStopHold</c> with the
    /// other legs — entries are (actor, target) keyed by check, and the talk
    /// npc objId never equals a prey/reporter/corpse/well target. Never touches
    /// _move state or queue kinds — public actor verbs only.
    /// </summary>
    private static ActorRequest DispatchTalkStop(IGameplayActor gameplayActor, BotDecisionProposal proposal)
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
    /// the dispatch trace + terminal state + detail, and the target HP the
    /// engine itself observed for this swing.
    ///
    /// <b>Why delta is not a dispatch-time HP difference.</b> A swing whose
    /// skill template carries an effect delay/speed (ranged auto-attack) does
    /// NOT reduce HP when it is dispatched: the engine schedules the effects
    /// and the damage lands hundreds of milliseconds later. Re-reading
    /// <c>npc.Hp</c> at dispatch therefore samples BEFORE the damage exists,
    /// and rendering that as <c>delta=0</c> is what made a working ranged
    /// weapon look like it dealt nothing. This line reports what is knowable
    /// at dispatch time and marks the rest as pending rather than inventing
    /// a number — the resolved figure arrives on the actor's
    /// <c>BotCastEffectResolved</c> line for the same trace id.
    ///
    /// No behavior input; Info so the lane file target records it. The line
    /// reports the verb that ACTUALLY dispatched, read off the request the
    /// engine returned. <c>hpBefore</c> is only observed on the attack arm,
    /// so the HP fields and the delta are <c>NA</c> for any other verb
    /// rather than a fabricated <c>0</c>.
    /// </summary>
    private static void LogCombatOutcome(IGameplayActor actor, QuestDecisionScenario.QuestOptions opts, BotDecisionProposal selected, ActorRequest request, int hpBefore, QuestFixtureRow fixture)
    {
        var npc = actor.Character?.ParentWorld?.GetNpc(selected.TargetId);
        var hpAfter = npc?.Hp ?? -1;
        var hpBeforeText = hpBefore >= 0 ? hpBefore.ToString(CultureInfo.InvariantCulture) : "NA";

        // The engine's own observation for this exact swing. Resolved only
        // after the bounded window closes; while it is open the damage is
        // scheduled and NOT yet observable, which is exactly the ranged case.
        var effect = actor.GetCastEffectObservation(request.TraceId);

        // A swing with NO delay lands inline, so by the time this line runs
        // the damage is already in npc.Hp and the post-dispatch read IS the
        // real delta. Prefer that number: reporting "pending" for damage we
        // can already see would trade a true figure for an unmeasured one.
        var inlineDelta = hpBefore >= 0 && hpAfter >= 0 && hpBefore - hpAfter > 0
            ? (hpBefore - hpAfter).ToString(CultureInfo.InvariantCulture)
            : null;

        string deltaText;
        string hpAfterText;
        if (effect.State == CastEffectResolution.Resolved)
        {
            deltaText = effect.Describe();
            hpAfterText = effect.HpAfter?.ToString(CultureInfo.InvariantCulture) ?? "NA";
        }
        else if (effect.State == CastEffectResolution.Pending)
        {
            // Deferred: the engine scheduled the effects, the target's HP is
            // still the pre-strike value, and any difference here would be
            // another source's damage, not this swing's. Say so.
            deltaText = inlineDelta ?? "pending";
            hpAfterText = "pending";
        }
        else
        {
            deltaText = inlineDelta ?? "NA";
            hpAfterText = hpAfter >= 0 ? hpAfter.ToString(CultureInfo.InvariantCulture) : "NA";
        }
        var detail = (request.Detail ?? "").Replace(' ', '_');
        SweepDiagLog.Info(
            "QuestObjectiveCombatDiag cycle={Cycle} char={CharId} actor={ActorObjId} quest={Quest} target={Target} template={Template} verb={Verb} " +
            "trace={Trace} state={State} detail=[{Detail}] hpBefore={HpBefore} hpAfter={HpAfter} delta={Delta} targetAlive={Alive} damage={Damage}",
            opts.CycleId, actor.Character?.Id ?? 0, actor.ActorId, fixture.QuestId, selected.TargetId,
            npc?.TemplateId ?? 0, request.Action, request.TraceId, request.State,
            TruncateSweep(detail, 200), hpBeforeText, hpAfterText, deltaText,
            npc != null && npc.Hp > 0, effect.State.ToString().ToLowerInvariant());
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
        IReadOnlyList<QuestGiveUp>? giveUps = null,
        IReadOnlyList<BotDecisionProposal>? legalAlternatives = null,
        int? selectionCandidateCount = null,
        uint selectedQuestId = 0,
        string purposeSwitchReason = "")
        => new()
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = false,
            SelectedAction = selected,
            Request = request,
            SelectedQuestId = selectedQuestId,
            PurposeSwitchReason = purposeSwitchReason,
            Rejections = rejections,
            LegalAlternatives = legalAlternatives,
            SelectionCandidateCount = selectionCandidateCount,
            Explanation = detail,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            LegEvidence = legEvidence ?? [],
            PlanFailures = planFailures ?? [],
            GiveUps = giveUps ?? [],
            TuningRevision = BotBrainTuningStore.Revision,
            TraceRecords = [.. actor.AuditTrace]
        };
}
