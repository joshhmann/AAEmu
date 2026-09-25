using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest objective→target selector (G4, production).
///
/// Shape: the scenario-only <c>LevelingLoopScenario.SelectHuntTarget</c> precedent,
/// production-owned. Candidates come ONLY from the immutable observation snapshot
/// (<c>NearbyNpcObjIds</c> — never a world scan); per-candidate truth (template,
/// liveness, hostility, visibility, distance) resolves live via
/// <c>ParentWorld.GetNpc</c> at proposal time — never stored, never cached (pure
/// function per wake).
///
/// Quest-relevance (quest state + static loot chain, no combat state) and
/// combat-legality (engine rules, queried live) share nothing but the objId and
/// are projected separately. Legality reuses
/// <see cref="CombatDecisionTree.IsHostileTarget"/> — never a copy.
/// Ordering is nearest-first flat with an ObjId tiebreak (G3 doctrine:
/// deterministic before any cut).
///
/// The quest's static facts (gather act, item, need, prey template, loot pack,
/// reporter, reward) arrive as ONE derived <see cref="QuestFixtureRow"/> per
/// wake — never hand-typed constants. Credit-time identity is the item
/// (QuestActObjItemGather.OnItemGather matches act id + item); kill-time
/// identity is the template. This selector carries the template→objId
/// resolution explicitly and STOPS at selection — no travel, no combat, no
/// credit, no turn-in.
/// </summary>
public static class QuestObjectiveTargetSelector
{
    /// <summary>Diagnostic-only per-candidate funnel bound (selection itself is unbounded).</summary>
    public const int CandidatesShown = 12;

    /// <summary>Per-candidate funnel row: separate relevance/legality flags, distance, first failing predicate.</summary>
    public sealed record CandidateFunnel(
        uint ObjId,
        uint TemplateId,
        bool QuestRelevant,
        bool CombatLegal,
        double DistanceM,
        string Reject);

    /// <summary>
    /// Full wake funnel: objective/source resolution, relevance,
    /// raw→relevant→legal tallies, per-predicate reject counts, ordered
    /// candidate rows, selection, the CycleId join key, and the resolved
    /// fixture row the wake ran against.
    /// </summary>
    public sealed record ObjectiveTargetFunnel(
        string CycleId,
        uint QuestId,
        bool ObjectiveResolved,
        string ObjectiveDetail,
        bool SourceResolved,
        string SourceDetail,
        bool Relevance,
        int HaveCount,
        int NeedCount,
        int RawCount,
        int RelevantCount,
        int LegalCount,
        uint SelectedObjId,
        uint SelectedTemplateId,
        double SelectedDistanceM,
        int RejectUnresolved,
        int RejectDead,
        int RejectTemplate,
        int RejectHostile,
        int RejectAttack,
        int RejectStealth,
        IReadOnlyList<CandidateFunnel> Candidates,
        QuestFixtureRow Fixture);

    /// <summary>
    /// Quest-relevance wrapper (snapshot only, no combat state): the fixture
    /// quest is active AND fewer than its need count of its gather item are
    /// held. Per-candidate relevance adds the template match (the fixture
    /// row's loot link).
    /// </summary>
    public static bool IsRelevant(BotObservedContext observation, QuestFixtureRow fixture)
    {
        if (observation == null || fixture == null)
            return false;
        if (!observation.ActiveQuestIds.Contains(fixture.QuestId))
            return false;
        return !observation.BagItemCounts.TryGetValue(fixture.PreyItem, out var have) || have < fixture.Need;
    }

    /// <summary>
    /// Pure per-wake evaluation: resolve the Progress objective and the loot
    /// source from live game data for the given fixture row, filter snapshot
    /// candidates by relevance × legality, and return the nearest-first +
    /// ObjId-tiebreak selection (0 when nothing qualifies). Never stores,
    /// never mutates.
    /// </summary>
    public static ObjectiveTargetFunnel Evaluate(
        Character? character, BotObservedContext? observation, string? cycleId, QuestFixtureRow fixture)
    {
        var cycle = string.IsNullOrEmpty(cycleId) ? "-" : cycleId;
        var (objectiveResolved, objectiveDetail) = ResolveObjective(fixture);
        var (sourceResolved, sourceDetail) = ResolveSource(fixture);

        var relevance = IsRelevant(observation!, fixture);
        var have = 0;
        if (observation?.BagItemCounts.TryGetValue(fixture.PreyItem, out var held) == true)
            have = held;

        var rawCount = 0;
        var candidates = new List<CandidateFunnel>();
        var rejectUnresolved = 0;
        var rejectDead = 0;
        var rejectTemplate = 0;
        var rejectHostile = 0;
        var rejectAttack = 0;
        var rejectStealth = 0;
        var relevantCount = 0;
        var legalCount = 0;
        Npc? best = null;
        var bestDistance = double.MaxValue;
        uint bestObjId = 0;

        var actorPos = character?.Transform.World.Position;
        foreach (var objId in observation?.NearbyNpcObjIds ?? [])
        {
            rawCount++;
            var npc = character?.ParentWorld?.GetNpc(objId);
            if (npc == null)
            {
                rejectUnresolved++;
                continue;
            }

            var templateMatch = npc.TemplateId == fixture.PreyTemplate;
            var questRelevant = relevance && templateMatch;
            if (questRelevant)
                relevantCount++;

            string reject;
            bool combatLegal;
            double distance;
            if (!actorPos.HasValue)
            {
                distance = double.NaN;
                reject = "actor-pos";
                combatLegal = false;
            }
            else
            {
                distance = MathUtil.CalculateDistance(actorPos.Value, npc.Transform.World.Position, false);
                if (npc.Hp <= 0)
                {
                    reject = "dead";
                    rejectDead++;
                    combatLegal = false;
                }
                else if (!templateMatch)
                {
                    reject = "template";
                    rejectTemplate++;
                    combatLegal = false;
                }
                else if (!CombatDecisionTree.IsHostileTarget(character!, npc))
                {
                    reject = "hostile";
                    rejectHostile++;
                    combatLegal = false;
                }
                else if (!character!.CanAttack(npc))
                {
                    reject = "attack";
                    rejectAttack++;
                    combatLegal = false;
                }
                else if (!character.CanSeeTarget(npc))
                {
                    reject = "stealth";
                    rejectStealth++;
                    combatLegal = false;
                }
                else
                {
                    reject = "-";
                    combatLegal = true;
                }
            }

            if (combatLegal)
                legalCount++;
            if (candidates.Count < CandidatesShown)
                candidates.Add(new CandidateFunnel(objId, npc.TemplateId, questRelevant, combatLegal, distance, reject));

            // Nearest-first flat with an ObjId tiebreak. Relevance gates the
            // selection: a legal non-fixture-template NPC is never the
            // objective target.
            if (questRelevant && combatLegal && !double.IsNaN(distance)
                && (distance < bestDistance || (distance == bestDistance && objId < bestObjId)))
            {
                best = npc;
                bestDistance = distance;
                bestObjId = objId;
            }
        }

        return new ObjectiveTargetFunnel(
            cycle, fixture.QuestId, objectiveResolved, objectiveDetail, sourceResolved, sourceDetail,
            relevance, have, fixture.Need,
            rawCount,
            relevantCount, legalCount,
            best?.ObjId ?? 0, best?.TemplateId ?? 0, best == null ? double.NaN : bestDistance,
            rejectUnresolved, rejectDead, rejectTemplate, rejectHostile, rejectAttack, rejectStealth,
            candidates,
            fixture);

    }

    /// <summary>
    /// FAIL-OBJECTIVE predicate: the Progress component of the fixture quest
    /// must expose the ItemGather act for the fixture item (act id, detail,
    /// ×need). Fail-closed: any unreadable state resolves false with a named
    /// detail.
    /// </summary>
    private static (bool Resolved, string Detail) ResolveObjective(QuestFixtureRow fixture)
    {
        try
        {
            var template = QuestManager.Instance.GetTemplate(fixture.QuestId);
            if (template == null)
                return (false, $"no-template-{fixture.QuestId}");
            var gathers = template.GetComponents(QuestComponentKind.Progress)
                .SelectMany(c => c.ActTemplates)
                .OfType<QuestActObjItemGather>()
                .ToList();
            if (gathers.Count == 0)
                return (false, "no-progress-gather-acts");
            var match = gathers.FirstOrDefault(a => a.ItemId == fixture.PreyItem);
            if (match == null)
                return (false, $"no-{fixture.PreyItem}-gather-act(gatherItems={string.Join(",", gathers.Select(a => a.ItemId))})");
            return (true, $"act{match.ActId}:item{match.ItemId}x{match.Count}");
        }
        catch (Exception ex)
        {
            return (false, $"exception-{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// FAIL-SOURCE predicate: the fixture row's static loot link
    /// (prey template→pack→item) must resolve in live game data (NPC→pack row
    /// plus pack→item row). Fail-closed.
    /// </summary>
    private static (bool Resolved, string Detail) ResolveSource(QuestFixtureRow fixture)
    {
        try
        {
            var packs = ItemManager.Instance.GetLootPackIdByNpcId(fixture.PreyTemplate);
            var hasPack = packs.Any(p => p.LootPackId == fixture.PreyPack);
            var pack = LootGameData.Instance.GetPack(fixture.PreyPack);
            var hasItem = pack?.Loots?.Any(l => l.ItemId == fixture.PreyItem) == true;
            if (hasPack && hasItem)
                return (true, $"npc{fixture.PreyTemplate}>pack{fixture.PreyPack}>item{fixture.PreyItem}");
            return (false, $"pack{fixture.PreyPack}={hasPack}_item{fixture.PreyItem}={hasItem}");
        }
        catch (Exception ex)
        {
            return (false, $"exception-{ex.GetType().Name}");
        }
    }
}
