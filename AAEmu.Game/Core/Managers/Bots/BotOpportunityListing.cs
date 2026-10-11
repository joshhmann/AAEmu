using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Read-only opportunity listing for one bot in one supported area: which quests it can
/// actually try, which nearby hunts are really there, and whether it needs recovery.
///
/// This is observation ONLY: it never accepts, advances, turns in, moves, attacks,
/// loots, consumes, or mutates the character or the world. Every row carries the
/// reason it was listed or withheld, so a caller can tell "can't do this" (a named
/// engine refusal) from "haven't checked yet" (an explicit unknown).
///
/// Question answered per row:
/// - Eligible quest work: quests offered by NPCs/doodads the actor can currently
///   perceive (the bounded region census — the same world facts the server already
///   knows through <see cref="WorldManager.GetAround{T}(GameObject,float)"/>) that
///   ALSO pass the real accept gate (<see cref="GameplayActor.IsDiscoverable"/> —
///   the same pre-conditions <c>CharacterQuests.AddQuest</c> enforces: known
///   template, no active duplicate, supply-item gate, every Start component's
///   unit_reqs for THIS character, completed non-repeatable stays hidden).
///   Ineligible quests are never listed; the row names the rejecting predicate.
/// - Viable nearby hunts: live NPCs from the actor's own bounded census
///   (<see cref="GameplayActor.Observe"/>'s 25 m region read) that serve an
///   ACTIVE quest's hunt objective (single-template or group, resolved live
///   through the engine's own <c>QuestManager.CheckGroupNpc</c>) AND pass the
///   same engine gates the quest target selector applies
///   (<see cref="QuestObjectiveTargetSelector"/>: alive, template-relevant,
///   <see cref="CombatDecisionTree.IsHostileTarget"/>, <c>CanAttack</c>,
///   <c>CanSeeTarget</c>). A hunt target that fails a gate is withheld with
///   its first failing predicate — never listed as viable.
/// - Recovery needs: actual vitals (<c>Character.Hp/MaxHp/Mp/MaxMp</c>) against
///   the normal recovery thresholds (<see cref="OutOfCombatRecoveryModule"/>),
///   plus whether the bag holds an edible consumable (the module's own
///   <c>IsEdibleConsumable</c> predicate — no vendor-stock or eligibility
///   invention: without a consumable the listing says rest, not buy).
///
/// Unknown stays unknown (layer-02 direction): a missing region census is
/// reported as coverage-invalid with zero hunts and zero quest sources — never
/// as an exhausted area. An unscanned source is reported unscanned, never
/// exhausted. No spawner-position lookup, no retry counters, no coordinate
/// list: hunting comes from live population state (actually spawned NPCs in
/// the census), not configured spawner data.
///
/// Supported area: the Solzreed starter corridor around the natural spawn
/// (<see cref="SolzreedStarterCorridorProfile.StarterSpawnPosition"/>) — the
/// smallest area where a short walk reaches real quest + hunting + recovery:
/// the field-test spawn (E-48), quest givers a walk away (3512 ~223m,
/// 3515 ~592m, 3516 ~573m per npc_spawns.json, outside the 25m census at
/// spawn, so the listing is truthfully empty at spawn), and wildlife prey
/// (faction 115) in the same census. The listing itself is area-agnostic
/// (it reads whatever census the actor stands in); the area choice only
/// bounds what the tests seed.
/// </summary>
public static class BotOpportunityListing
{
    /// <summary>
    /// Census radius: the same bounded read <see cref="GameplayActor.Observe"/>
    /// performs (25 m region census). The listing never scans beyond what the
    /// actor's own observation could perceive.
    /// </summary>
    public const float CensusRadiusM = 25f;

    /// <summary>One eligible quest offer with the reason it is actionable.</summary>
    public sealed record EligibleQuest(
        uint QuestId,
        QuestAcceptorType AcceptorType,
        uint AcceptorTemplateId,
        uint SourceObjId,
        float DistanceM,
        string Reason);

    /// <summary>One viable nearby hunt target with the reason it is actionable.</summary>
    public sealed record ViableHunt(
        uint TargetObjId,
        uint TargetTemplateId,
        uint QuestId,
        float DistanceM,
        int HaveCount,
        int NeedCount,
        string Reason);
    /// <summary>One accepted quest's still-actionable work with the leg that owns it.</summary>
    public sealed record ActiveQuestWork(
        uint QuestId,
        string Leg,
        string Reason);

    /// <summary>One accepted quest that cannot proceed, with the explicit blocker.</summary>
    public sealed record BlockedQuestWork(
        uint QuestId,
        string Blocker);

    /// <summary>Recovery state with the reason for the advised disposition.</summary>
    public sealed record RecoveryNeed(
        bool NeedsRecovery,
        float HpRatio,
        float MpRatio,
        bool HasConsumable,
        string Reason);

    /// <summary>
    /// The full read-only listing: eligible quests + viable hunts + recovery,
    /// plus coverage validity so a caller can tell "nothing here" (valid empty
    /// census) from "couldn't look" (invalid census).
    ///
    /// QuestOffersUnknown is the busy-actor witness: true when at least one
    /// quest-offer observation (per-NPC DiscoverQuests or DiscoverSelfQuests)
    /// was refused because the actor was busy with a live request. The
    /// eligible set is then INCOMPLETE — unknown, never disappearance or
    /// exhaustion. Hunts ride pure region reads (no request gate) and stay
    /// authoritative regardless of this flag.
    /// </summary>
    public sealed record OpportunityListing(
        bool CoverageValid,
        string CoverageReason,
        IReadOnlyList<EligibleQuest> EligibleQuests,
        IReadOnlyList<ViableHunt> ViableHunts,
        RecoveryNeed Recovery,
        bool QuestOffersUnknown,
        IReadOnlyList<ActiveQuestWork> ActiveWork,
        IReadOnlyList<BlockedQuestWork> BlockedWork);

    /// <summary>
    /// Builds the listing for the actor's current position. Read-only: no
    /// quest, movement, combat, loot, item, or vitals mutation.
    /// </summary>
    public static OpportunityListing List(IGameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var character = actor.Character;

        // Coverage validity FIRST (layer-02 direction: missing coverage must not
        // read as a confident empty world). The region graph is the census
        // backstop: without a region the bounded scan cannot run, so every
        // population-derived section is empty-with-reason, not exhausted.
        var coverageValid = character.Region != null;
        var coverageReason = coverageValid
            ? "census-valid"
            : "census-unavailable:no-region — nothing scanned, none seen-depleted";

        var eligible = new List<EligibleQuest>();
        var hunts = new List<ViableHunt>();
        var seenQuests = new HashSet<uint>();

        if (coverageValid)
        {
            var actorPos = character.Transform.World.Position;
            var questOffersUnknown = CollectEligibleQuests(actor, character, actorPos, eligible, seenQuests);
            CollectViableHunts(character, actorPos, hunts);
            var recovery = AssessRecovery(character);
            eligible.Sort(static (a, b) => a.QuestId.CompareTo(b.QuestId));
            hunts.Sort(static (a, b) =>
            {
                var byDist = a.DistanceM.CompareTo(b.DistanceM);
                return byDist != 0 ? byDist : a.TargetObjId.CompareTo(b.TargetObjId);
            });
            CollectActiveWork(character, hunts, out var active, out var blocked);
            return new OpportunityListing(coverageValid, coverageReason, eligible, hunts, recovery, questOffersUnknown, active, blocked);
        }
        return new OpportunityListing(coverageValid, coverageReason, eligible, hunts, AssessRecovery(character), false,
            Array.Empty<ActiveQuestWork>(), Array.Empty<BlockedQuestWork>());
    }

    private static bool CollectEligibleQuests(
        IGameplayActor actor, Character character, Vector3 actorPos,
        List<EligibleQuest> eligible, HashSet<uint> seenQuests)
    {
        // NPC channel: every live NPC in the bounded census, through the REAL
        // discover path per target (offer linkage + IsDiscoverable fail-closed
        // filter). Ineligible quests never surface — DiscoverQuests withholds
        // them with the same predicates AddQuest would refuse.
        foreach (var npcObjId in WorldManager.GetAround<Npc>(character, CensusRadiusM).Select(n => n.ObjId).ToList())
        {
            var npc = character.ParentWorld?.GetNpc(npcObjId);
            if (npc == null)
                continue;
            var distance = MathUtil.CalculateDistance(actorPos, npc.Transform.World.Position, false);
            if (distance > GameplayActor.MaxQuestDiscoverRange)
                continue;
            var discover = actor.DiscoverQuests(npcObjId);
            if (IsBusyRefusal(discover))
                return true;
            if (discover is not { IsTerminal: true, State: ActorLifecycleState.Completed })
                continue;
            if (discover.Result is not QuestDiscoveryResult result)
                continue;
            foreach (var offering in result.Offerings)
            {
                if (!seenQuests.Add(offering.QuestId))
                    continue;
                eligible.Add(new EligibleQuest(
                    offering.QuestId,
                    offering.AcceptorType,
                    offering.AcceptorId,
                    npcObjId,
                    distance,
                    $"offered-by-{offering.AcceptorType}-{offering.AcceptorId}-in-range-passes-accept-gate"));
            }
        }

        // Self-perceivable channels (item/sphere/level): the actor's OWN state,
        // no world target — the same triples the engine's auto-start paths
        // consume, filtered through the same IsDiscoverable gate.
        var self = actor.DiscoverSelfQuests();
        if (IsBusyRefusal(self))
            return true;
        if (self is { IsTerminal: true, State: ActorLifecycleState.Completed }
            && self.Result is QuestSelfDiscoveryResult selfResult)
        {
            foreach (var offering in selfResult.Offerings)
            {
                if (!seenQuests.Add(offering.QuestId))
                    continue;
                eligible.Add(new EligibleQuest(
                    offering.QuestId,
                    offering.AcceptorType,
                    offering.AcceptorId,
                    0,
                    0f,
                    $"self-perceivable-{offering.AcceptorType}-passes-accept-gate"));
            }
        }
        return false;
    }

    /// <summary>
    /// True only for the TryBegin busy refusal: a terminal Rejected with the
    /// StateTransition reason while the observation never ran, so the
    /// eligible set is incomplete (unknown), never empty-by-evidence. Any
    /// other terminal outcome (engine rejection, completed-but-empty, null
    /// result) is authoritative: the gate ran and said nothing is offered.
    /// </summary>
    private static bool IsBusyRefusal(ActorRequest? request)
        => request is { IsTerminal: true, State: ActorLifecycleState.Rejected, Failure: ActorFailureReason.StateTransition };

    private static void CollectViableHunts(Character character, Vector3 actorPos, List<ViableHunt> hunts)
    {
        var quests = character.Quests;
        if (quests == null)
            return;

        // Hunt objectives of ACTIVE quests only: single-template
        // (QuestActObjMonsterHunt.NpcId) or group (QuestActObjMonsterGroupHunt
        // via the engine's own CheckGroupNpc). Static template facts — the
        // live availability decision happens per spawned NPC below.
        var objectives = new List<(uint QuestId, uint NpcTemplate, uint NpcGroup, int Have, int Need)>();
        foreach (var (questId, quest) in quests.ActiveQuests)
        {
            if (quest == null)
                continue;
            foreach (var act in QuestDirector.ReadObjectiveGroups(quest).Where(g => g.Active).SelectMany(g => g.Remaining))
            {
                var (npcTemplate, npcGroup) = act switch
                {
                    QuestActObjMonsterHunt hunt => (hunt.NpcId, 0u),
                    QuestActObjMonsterGroupHunt group => (0u, group.QuestMonsterGroupId),
                    _ => (0u, 0u)
                };
                if (npcTemplate != 0 || npcGroup != 0)
                    objectives.Add((questId, npcTemplate, npcGroup, act.GetObjective(quest), act.Count));
            }
        }
        if (objectives.Count == 0)
            return;

        foreach (var npc in WorldManager.GetAround<Npc>(character, CensusRadiusM).ToList())
        {
            if (npc == null || npc.Hp <= 0)
                continue;
            foreach (var (questId, npcTemplate, npcGroup, have, need) in objectives)
            {
                var relevant = npcTemplate != 0
                    ? npc.TemplateId == npcTemplate
                    : QuestManager.Instance.CheckGroupNpc(npcGroup, npc.TemplateId);
                if (!relevant)
                    continue;
                // Engine gates, first-failure order matching the production
                // selector: hostility, attack legality, visibility. A failure
                // withholds the target with its predicate — never listed.
                if (!CombatDecisionTree.IsHostileTarget(character, npc))
                    continue;
                if (!SafeCanAttack(character, npc))
                    continue;
                if (!character.CanSeeTarget(npc))
                    continue;
                var distance = MathUtil.CalculateDistance(actorPos, npc.Transform.World.Position, false);
                hunts.Add(new ViableHunt(
                    npc.ObjId,
                    npc.TemplateId,
                    questId,
                    distance,
                    have,
                    need,
                    "live-prey-in-census-passes-hostile-attack-visible-gates"));
                break;
            }
        }
    }
    /// <summary>
    /// Accepted-quest work from ACTUAL quest state (live Status + every
    /// remaining objective's own act facts + live objective counters, never
    /// wake counts): an active quest with a Ready Status carries return/turn-in
    /// (even with no offer/prey — the return IS the work); otherwise every
    /// still-uncredited Progress objective is assessed on its OWN act. The
    /// quest keeps one ActiveWork row (its first still-usable objective in
    /// deterministic act order, so the module keeps working it) and surfaces
    /// every genuinely stuck objective as a BlockedWork row — a resolvable
    /// first objective never hides a missing later target/source. The engine's
    /// component/objective semantics stay authoritative: Selective uses
    /// component alternatives, never recognition of an uncredited action, while a
    /// quest-status or template failure releases with the explicit blocker.
    /// Absence from offers/prey is never itself a blocker. Read-only: no quest,
    /// movement, combat, loot, item, or vitals mutation.
    /// </summary>
    private static void CollectActiveWork(Character character, IReadOnlyList<ViableHunt> hunts, out IReadOnlyList<ActiveQuestWork> active, out IReadOnlyList<BlockedQuestWork> blocked)
    {
        var live = new List<ActiveQuestWork>();
        var stuck = new List<BlockedQuestWork>();
        var quests = character.Quests;
        if (quests == null)
        {
            active = live;
            blocked = stuck;
            return;
        }
        foreach (var (questId, quest) in quests.ActiveQuests.OrderBy(kv => kv.Key))
        {
            if (quest == null)
                continue;
            if (quest.Status == QuestStatus.Failed)
            {
                stuck.Add(new BlockedQuestWork(questId, "quest-failed"));
                continue;
            }
            if (quest.Status == QuestStatus.Ready)
            {
                var row = QuestFixtureRow.FromQuestData(questId);
                var leg = row.ReporterTemplate != 0 ? "return" : "turn-in";
                live.Add(new ActiveQuestWork(questId, leg, $"quest-ready-{leg}-pending"));
                continue;
            }
            if (quest.Status != QuestStatus.Progress)
            {
                stuck.Add(new BlockedQuestWork(questId, $"quest-status-{quest.Status}-not-progress"));
                continue;
            }
            QuestTemplate template;
            try
            {
                var resolved = QuestManager.Instance.GetTemplate(questId);
                if (resolved == null)
                {
                    stuck.Add(new BlockedQuestWork(questId, "quest-template-missing"));
                    continue;
                }
                template = resolved;
            }
            catch
            {
                stuck.Add(new BlockedQuestWork(questId, "quest-template-unreadable"));
                continue;
            }
            var groups = QuestDirector.ReadObjectiveGroups(quest);
            if (QuestDirector.ObjectivesPermitAdvance(quest, groups))
            {
                // This names credited objectives, not a completed quest. The
                // ordinary advance request still checks all engine conditions.
                live.Add(new ActiveQuestWork(questId, "advance", "objective-credited-advance-pending"));
                continue;
            }
            var alternatives = new List<(ActiveQuestWork? Work, List<BlockedQuestWork> Blockers)>();
            foreach (var group in groups)
            {
                var blockers = new List<BlockedQuestWork>();
                ActiveQuestWork? work = null;
                if (!group.Active)
                    blockers.Add(new BlockedQuestWork(questId, $"quest-component-requirements-{group.ComponentId}"));
                else
                {
                    foreach (var act in group.Remaining)
                    {
                        var verdict = AssessObjective(character, template, act, hunts);
                        if (verdict.Blocker != null)
                            blockers.Add(new BlockedQuestWork(questId, verdict.Blocker));
                        else
                            work ??= new ActiveQuestWork(questId, verdict.Leg!, verdict.Reason!);
                    }
                }
                alternatives.Add((work, blockers));
            }
            if (template.Selective)
            {
                // A blocked unused alternative does not block a usable one.
                // Missing-world work stays work; it is NEVER advance credit.
                var usable = alternatives.FirstOrDefault(a => a.Work != null && a.Blockers.Count == 0);
                if (usable.Work != null)
                    live.Add(usable.Work);
                else
                    stuck.AddRange(alternatives.SelectMany(a => a.Blockers));
            }
            else
            {
                stuck.AddRange(alternatives.SelectMany(a => a.Blockers));
                var work = alternatives.Select(a => a.Work).FirstOrDefault(w => w != null);
                if (work != null)
                    live.Add(work);
            }
        }
        active = live;
        blocked = stuck;
    }

    /// <summary>
    /// One remaining objective's verdict: usable/kept work (Leg + Reason) or a
    /// genuinely stuck objective (Blocker). A quest keeps at most one ActiveWork
    /// row (its first still-usable objective in deterministic order) but
    /// surfaces EVERY stuck objective as a BlockedWork row — a resolvable first
    /// objective never hides a genuinely missing later target/source.
    /// </summary>
    private sealed record ObjectiveVerdict(string? Leg, string? Reason, string? Blocker)
    {
        public static ObjectiveVerdict Work(string leg, string reason) => new(leg, reason, null);
        public static ObjectiveVerdict Stuck(string blocker) => new(null, null, blocker);
    }

    /// <summary>
    /// Assesses ONE still-uncredited objective act against live state: its own
    /// CLR type names the leg (never the row's first-only pattern), its own
    /// target/source/item facts name the condition, and only a live census read
    /// (never a template id) decides perceived-vs-missing. Talk/gather/hunt
    /// objectives without a perceived target stay KEPT work with the missing
    /// condition named (absence from the narrow census is never abandonment);
    /// use-item without the item, sourceless interact, prey-less hunts and
    /// unserved act shapes are the genuinely stuck cases with named blockers.
    /// </summary>
    private static ObjectiveVerdict AssessObjective(Character character, QuestTemplate template, QuestActTemplate act, IReadOnlyList<ViableHunt> hunts)
    {
        var questId = template.Id;
        switch (act)
        {
            case QuestActObjTalk talk:
            {
                if (talk.NpcId == 0)
                    return ObjectiveVerdict.Stuck($"quest-talk-no-target-act-{act.ActId}");
                return CensusHasNpcTarget(character, talk.NpcId, 0)
                    ? ObjectiveVerdict.Work("talk", $"talk-objective-pending-npc-{talk.NpcId}-group-0-target-perceived")
                    : ObjectiveVerdict.Work("talk", $"talk-objective-pending-npc-{talk.NpcId}-group-0-no-perceived-target");
            }
            case QuestActObjTalkNpcGroup groupTalk:
            {
                if (groupTalk.NpcGroupId == 0)
                    return ObjectiveVerdict.Stuck($"quest-talk-no-target-act-{act.ActId}");
                return CensusHasNpcTarget(character, 0, groupTalk.NpcGroupId)
                    ? ObjectiveVerdict.Work("talk", $"talk-objective-pending-npc-0-group-{groupTalk.NpcGroupId}-target-perceived")
                    : ObjectiveVerdict.Work("talk", $"talk-objective-pending-npc-0-group-{groupTalk.NpcGroupId}-no-perceived-target");
            }
            case QuestActObjItemUse useItem:
            {
                if (useItem.ItemId == 0)
                    return ObjectiveVerdict.Stuck($"quest-use-item-no-item-act-{act.ActId}");
                return HasObjectiveItem(character, useItem.ItemId)
                    ? ObjectiveVerdict.Work("use-item", $"use-item-objective-pending-item-{useItem.ItemId}")
                    : ObjectiveVerdict.Stuck($"quest-use-item-missing-item-{useItem.ItemId}");
            }
            case QuestActObjInteraction interaction:
            {
                if (interaction.DoodadId == 0)
                    return ObjectiveVerdict.Stuck($"quest-interact-no-source-act-{act.ActId}");
                return CensusHasDoodad(character, [interaction.DoodadId])
                    ? ObjectiveVerdict.Work("gather", $"interact-objective-pending-doodad-{interaction.DoodadId}-source-perceived")
                    : ObjectiveVerdict.Work("gather", $"interact-objective-pending-doodad-{interaction.DoodadId}-no-perceived-source");
            }
            case QuestActObjItemGather gather:
            {
                if (gather.ItemId == 0)
                    return ObjectiveVerdict.Stuck($"quest-gather-no-item-act-{act.ActId}");
                // A gather for the quest's own Supply grant is the supplied-gather
                // shape: the step machine grants it on accept, so the credit is
                // the legitimate supply receipt — the deliver monitor leg owns it.
                var supply = FindSupplyItem(template, gather.ItemId);
                if (supply != null)
                    return ObjectiveVerdict.Work("delivery", $"delivery-objective-pending-item-{gather.ItemId}-supply-{supply.ItemId}");
                var row = QuestFixtureRow.FromQuestData(questId, gather);
                var preyTemplate = row.PreyTemplate;
                var doodadTemplates = row.GatherDoodadTemplates;
                var perceived = CensusHasDoodad(character, doodadTemplates)
                    || (preyTemplate != 0 && WorldManager.GetAround<Npc>(character, QuestObjectiveTargetSelector.LivingPreyScanRadiusM)
                        .Any(n => QuestObjectiveTargetSelector.IsSuitableLivingPrey(character, n, preyTemplate)))
                    || hunts.Any(h => h.QuestId == questId);
                if (perceived)
                    return ObjectiveVerdict.Work("gather", $"gather-objective-pending-item-{gather.ItemId}-source-perceived");
                return HasObjectiveItem(character, gather.ItemId)
                    ? ObjectiveVerdict.Work("gather", $"gather-objective-pending-item-{gather.ItemId}-no-perceived-source")
                    : ObjectiveVerdict.Work("wait", $"gather-objective-waiting-loot-item-{gather.ItemId}-no-perceived-source");
            }
            case QuestActObjMonsterHunt hunt:
            {
                if (hunt.NpcId == 0)
                    return ObjectiveVerdict.Stuck($"quest-hunt-no-prey-act-{act.ActId}");
                // Usable-now is the engine-gated viable-hunt verdict from this
                // same listing (hostile/attack/visible all passed); a merely
                // perceived-but-gated-out prey names that instead. Neither gates
                // the row: no nearby prey is never abandonment.
                if (hunts.Any(h => h.QuestId == questId && h.TargetTemplateId == hunt.NpcId))
                    return ObjectiveVerdict.Work("hunt", $"hunt-objective-pending-prey-{hunt.NpcId}-group-0-prey-perceived");
                return CensusHasNpcTarget(character, hunt.NpcId, 0)
                    ? ObjectiveVerdict.Work("hunt", $"hunt-objective-pending-prey-{hunt.NpcId}-group-0-prey-present-not-viable")
                    : ObjectiveVerdict.Work("hunt", $"hunt-objective-pending-prey-{hunt.NpcId}-group-0-no-perceived-prey");
            }
            case QuestActObjMonsterGroupHunt groupHunt:
            {
                if (groupHunt.QuestMonsterGroupId == 0)
                    return ObjectiveVerdict.Stuck($"quest-hunt-no-prey-act-{act.ActId}");
                if (hunts.Any(h => h.QuestId == questId && IsGroupMember(groupHunt.QuestMonsterGroupId, h.TargetTemplateId)))
                    return ObjectiveVerdict.Work("hunt", $"hunt-objective-pending-prey-0-group-{groupHunt.QuestMonsterGroupId}-prey-perceived");
                return CensusHasNpcTarget(character, 0, groupHunt.QuestMonsterGroupId)
                    ? ObjectiveVerdict.Work("hunt", $"hunt-objective-pending-prey-0-group-{groupHunt.QuestMonsterGroupId}-prey-present-not-viable")
                    : ObjectiveVerdict.Work("hunt", $"hunt-objective-pending-prey-0-group-{groupHunt.QuestMonsterGroupId}-no-perceived-prey");
            }
            default:
                return ObjectiveVerdict.Stuck($"quest-objective-unserved-act-{act.GetType().Name}");
        }
    }
    /// <summary>
    /// The engine's own quest-monster-group membership test (the same test the
    /// talk-group and express-fire acts gate on). Fail-closed: an unreadable
    /// group surface matches nothing rather than every template.
    /// </summary>
    private static bool IsGroupMember(uint groupId, uint npcTemplateId)
    {
        try
        {
            return QuestManager.Instance.CheckGroupNpc(groupId, npcTemplateId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Live census presence of a talk/hunt target: a spawned NPC of the named
    /// template (or of the named group) in the actor's own bounded census.
    /// Alive only — a corpse is not a conversation or a fight. A template id
    /// alone never proves existence; this read does.
    /// </summary>
    private static bool CensusHasNpcTarget(Character character, uint singleTemplateId, uint groupId)
    {
        try
        {
            foreach (var npc in WorldManager.GetAround<Npc>(character, CensusRadiusM).ToList())
            {
                if (npc == null || npc.Hp <= 0)
                    continue;
                if (singleTemplateId != 0
                    ? npc.TemplateId == singleTemplateId
                    : groupId != 0 && IsGroupMember(groupId, npc.TemplateId))
                    return true;
            }
        }
        catch
        {
            // Unreadable census reads absent — never usable, never proof.
        }
        return false;
    }

    /// <summary>
    /// Live census presence of a usable gather/interact source: a spawned
    /// doodad of one of the granting templates in the bounded census that is
    /// not scheduled for despawn (the gather leg refuses despawning sources).
    /// </summary>
    private static bool CensusHasDoodad(Character character, IReadOnlyList<uint> templates)
    {
        if (templates == null || templates.Count == 0)
            return false;
        try
        {
            foreach (var doodad in WorldManager.GetAround<Doodad>(character, CensusRadiusM).ToList())
            {
                if (doodad == null || doodad.Despawn > DateTime.MinValue)
                    continue;
                for (var i = 0; i < templates.Count; i++)
                {
                    if (templates[i] != 0 && templates[i] == doodad.TemplateId)
                        return true;
                }
            }
        }
        catch
        {
            // Unreadable census reads absent — never usable, never proof.
        }
        return false;
    }



    /// <summary>
    /// The Supply-component grant for <paramref name="itemId"/> (the
    /// supplied-gather shape's own source fact). Null when the quest grants no
    /// such supply — the gather is then an ordinary loot gather, never delivery.
    /// </summary>
    private static QuestActSupplyItem? FindSupplyItem(QuestTemplate template, uint itemId)
    {
        try
        {
            return template.GetComponents(QuestComponentKind.Supply)
                .SelectMany(c => c.ActTemplates)
                .OfType<QuestActSupplyItem>()
                .FirstOrDefault(s => s.ItemId == itemId);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasObjectiveItem(Character character, uint itemTemplateId)
    {
        if (itemTemplateId == 0)
            return false;
        try
        {
            return character.Inventory.GetItemsCount(itemTemplateId) > 0;
        }
        catch
        {
            return false;
        }
    }


    private static RecoveryNeed AssessRecovery(Character character)
    {
        float hpRatio;
        float mpRatio;
        try
        {
            hpRatio = character.MaxHp > 0 ? (float)character.Hp / character.MaxHp : float.NaN;
        }
        catch
        {
            hpRatio = float.NaN;
        }
        try
        {
            mpRatio = character.MaxMp > 0 ? (float)character.Mp / character.MaxMp : float.NaN;
        }
        catch
        {
            mpRatio = float.NaN;
        }

        // Unreadable vitals: unknown stays unknown — never "healthy", never
        // "needs recovery".
        if (float.IsNaN(hpRatio) || float.IsNaN(mpRatio))
            return new RecoveryNeed(false, hpRatio, mpRatio, false, "vitals-unreadable-unknown");

        var needsHp = hpRatio < OutOfCombatRecoveryModule.DefaultHpTriggerFraction;
        var needsMp = mpRatio < OutOfCombatRecoveryModule.DefaultMpTriggerFraction;
        var needsRecovery = !character.IsInBattle && (needsHp || needsMp);

        var hasConsumable = HasEdibleConsumable(character);
        var reason = !needsRecovery
            ? "vitals-above-recovery-threshold"
            : character.IsInBattle
                ? "vitals-low-but-in-battle-recovery-deferred"
                : hasConsumable
                    ? "vitals-low-consumable-available"
                    : "vitals-low-no-consumable-rest-to-regen";

        return new RecoveryNeed(needsRecovery, hpRatio, mpRatio, hasConsumable, reason);
    }

    private static bool HasEdibleConsumable(Character character)
    {
        try
        {
            var bag = character.Inventory?.Bag;
            if (bag == null)
                return false;
            foreach (var item in bag.GetItemsSnapshot())
            {
                if (item != null && OutOfCombatRecoveryModule.IsEdibleConsumable(item))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>CanAttack</c> without letting a missing zone/faction singleton read
    /// as hostile-attackable: on engine failure the target is withheld (fail
    /// closed), never listed.
    /// </summary>
    private static bool SafeCanAttack(Character character, Npc npc)
    {
        try
        {
            return character.CanAttack(npc);
        }
        catch
        {
            return false;
        }
    }
}
