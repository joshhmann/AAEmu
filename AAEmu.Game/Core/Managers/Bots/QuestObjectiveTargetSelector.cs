using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest objective→target selector (G4, production).
///
/// Shape: the scenario-only <c>LevelingLoopScenario.SelectHuntTarget</c> precedent,
/// production-owned. Candidates come ONLY from the immutable observation snapshot
/// (<c>NearbyNpcObjIds</c> plus the bounded living-NPC census — never a global world scan); per-candidate truth (template,
/// liveness, hostility, visibility, distance) resolves live via
/// <c>ParentWorld.GetNpc</c> at proposal time — never stored, never cached (pure
/// function per wake).
///
/// Quest-relevance (quest state + static loot chain, no combat state) and
/// combat-legality (engine rules, queried live) share nothing but the objId and
/// are projected separately. Legality reuses
/// <see cref="CombatDecisionTree.IsHostileTarget"/> — never a copy.
/// Ordering is nearest-first flat with an ObjId tiebreak (G3 doctrine:
/// deterministic before any cut), raised ONE tier: a candidate no sibling has
/// already committed to outranks a nearer one a sibling is already on, so a
/// converged group spreads across the census instead of piling onto one boar.
/// The rule is a pure read of live world state — it stores no claim, mints no
/// id, and cannot go stale; with no contention it reduces to the flat ordering
/// exactly.
///
/// The quest's static facts (gather act, item, need, prey template, loot pack,
/// reporter, reward) arrive as ONE derived <see cref="QuestFixtureRow"/> per
/// wake — never hand-typed constants. Credit-time identity is the item
/// (QuestActObjItemGather.OnItemGather matches act id + item); kill-time
/// identity is the template. This selector carries the template→objId
/// resolution explicitly and STOPS at selection — no travel, no combat, no
/// credit, no turn-in.
///
/// Prey deconfliction reads SIBLING commitment from the wake's own census
/// (<c>NearbyCharacterObjIds</c> → each character's live <c>CurrentTarget</c>),
/// never a claim registry: no state outlives the wake that read it, and a
/// departing sibling's target is released the moment the next census drops it.
/// </summary>
public static class QuestObjectiveTargetSelector
{
    /// <summary>Diagnostic-only per-candidate funnel bound (selection itself is unbounded).</summary>
    public const int CandidatesShown = 12;

    /// <summary>Bounded living quest-prey sight range. Does not grant interaction or attack reach.</summary>
    public const float LivingPreyScanRadiusM = 100f;

    internal static IEnumerable<uint> PreyCandidates(BotObservedContext snapshot)
        => snapshot.NearbyNpcObjIds.Concat(snapshot.NearbyLivingNpcObjIds).Distinct();

    // The same ordinary legality gates decide both target selection and whether
    // observed prey can own the wake instead of travel. No new combat rules.
    internal static bool IsSuitableLivingPrey(Character character, Npc npc, uint templateId)
        => LivingPreyRejection(character, npc, templateId) == "-";

    internal static bool IsSuitableGatherPrey(Character character, Npc npc, QuestFixtureRow fixture)
        => IsQuestNpcSource(fixture, npc.TemplateId)
            && IsSuitableLivingPrey(character, npc, npc.TemplateId);

    // Cached reference links identify possible sources; the loaded native pack
    // must still grant this item. Never substitute another NPC's loot pack.
    internal static bool IsQuestNpcSource(QuestFixtureRow fixture, uint templateId)
        => fixture.HuntActId != 0
            ? HuntMatches(fixture, templateId)
            : ValidNpcSources(fixture).Any(s => s.NpcTemplateId == templateId);

    /// <summary>
    /// <see cref="IsQuestNpcSource"/> for many templates: the quest's hunt
    /// objective (or its valid drop sources) is read once, then each template
    /// is a plain comparison. Same answers as calling it per template; the
    /// spawner search tests every template in the world, and re-reading the
    /// quest per template made it one of the costliest parts of a bot's turn
    /// (populated-area run 6).
    /// </summary>
    internal static Func<uint, bool> QuestNpcSourceMatcher(QuestFixtureRow fixture)
    {
        if (fixture.HuntActId == 0)
        {
            var sources = ValidNpcSources(fixture).Select(s => s.NpcTemplateId).ToHashSet();
            return sources.Contains;
        }
        return HuntObjective(fixture) switch
        {
            QuestActObjMonsterHunt hunt => templateId => hunt.NpcId == templateId,
            QuestActObjMonsterGroupHunt hunt => templateId => QuestManager.Instance.CheckGroupNpc(hunt.QuestMonsterGroupId, templateId),
            _ => static _ => false
        };
    }

    private static QuestActTemplate? HuntObjective(QuestFixtureRow fixture)
    {
        var act = QuestManager.Instance.GetTemplate(fixture.QuestId)?
            .GetComponents(QuestComponentKind.Progress).SelectMany(c => c.ActTemplates)
            .FirstOrDefault(a => a.ActId == fixture.HuntActId);
        return act switch
        {
            QuestActObjMonsterHunt hunt when hunt.NpcId != 0
                && hunt.NpcId == fixture.HuntNpcTemplate && hunt.Count == fixture.HuntNeed => hunt,
            QuestActObjMonsterGroupHunt hunt when hunt.QuestMonsterGroupId != 0
                && hunt.QuestMonsterGroupId == fixture.HuntNpcGroup && hunt.Count == fixture.HuntNeed => hunt,
            _ => null
        };
    }

    private static bool HuntMatches(QuestFixtureRow fixture, uint templateId)
        => HuntObjective(fixture) switch
        {
            QuestActObjMonsterHunt hunt => hunt.NpcId == templateId,
            QuestActObjMonsterGroupHunt hunt => QuestManager.Instance.CheckGroupNpc(hunt.QuestMonsterGroupId, templateId),
            _ => false
        };

    // Read the ordinary act's counter, never a bag count or an action result.
    // Missing, completed, or inactive component facts fail closed.
    private static bool TryHuntCount(Character? character, QuestFixtureRow fixture, out int count)
    {
        count = -1;
        try
        {
            var quest = character?.Quests.ActiveQuests.GetValueOrDefault(fixture.QuestId);
            var act = HuntObjective(fixture);
            if (quest == null || act == null || quest.Status != QuestStatus.Progress)
                return false;
            count = act.GetObjective(quest);
            return count >= 0 && QuestDirector.ReadObjectiveGroups(quest).Any(g => g.Active
                && g.Remaining.Any(a => a.ActId == fixture.HuntActId));
        }
        catch
        {
            return false;
        }
    }

    internal static IEnumerable<QuestFixtureRow.NpcDropSource> ValidNpcSources(QuestFixtureRow fixture)
    {
        foreach (var source in fixture.GatherNpcSources)
        {
            bool valid;
            try
            {
                valid = ItemManager.Instance.GetLootPackIdByNpcId(source.NpcTemplateId)
                    .Any(p => p.LootPackId == source.LootPackId)
                    && LootGameData.Instance.GetPack(source.LootPackId)?.Loots
                        ?.Any(l => l.ItemId == fixture.PreyItem) == true;
            }
            catch
            {
                valid = false;
            }
            if (valid)
                yield return source;
        }
    }

    private static string LivingPreyRejection(Character character, Npc npc, uint templateId)
    {
        if (npc.Hp <= 0) return "dead";
        if (npc.TemplateId != templateId) return "template";
        if (!CombatDecisionTree.IsHostileTarget(character, npc)) return "hostile";
        if (!character.CanAttack(npc)) return "attack";
        if (!character.CanSeeTarget(npc)) return "stealth";
        return "-";
    }

    /// <summary>Per-candidate funnel row: separate relevance/legality flags, distance, first failing predicate.</summary>
    public sealed record CandidateFunnel(
        uint ObjId,
        uint TemplateId,
        bool QuestRelevant,
        bool CombatLegal,
        double DistanceM,
        string Reject)
    {
        public bool HasCompetingClaim { get; init; }
    }

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
        => IsRelevant(null, observation, fixture);

    internal static bool IsRelevant(Character? character, BotObservedContext observation, QuestFixtureRow fixture)
    {
        if (observation == null || fixture == null)
            return false;
        if (!observation.ActiveQuestIds.Contains(fixture.QuestId))
            return false;
        if (fixture.HuntActId != 0)
            {
            if (!TryHuntCount(character, fixture, out var count))
                return false;
            var quest = character!.Quests.ActiveQuests.GetValueOrDefault(fixture.QuestId);
            if (quest == null || (quest.Template.Score <= 0 && count >= fixture.HuntNeed))
                return false;
            // Count is a score weight in scored quests. The ordinary server
            // status decides readiness; never demand that many kills instead.
            return quest != null && !QuestDirector.ObjectivesPermitAdvance(quest,
                QuestDirector.ReadObjectiveGroups(quest));
        }
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
        var sourceTemplates = ValidNpcSources(fixture).Select(s => s.NpcTemplateId).ToHashSet();

        var relevance = IsRelevant(character, observation!, fixture);
        var have = 0;
        if (fixture.HuntActId != 0)
            TryHuntCount(character, fixture, out have);
        else if (observation?.BagItemCounts.TryGetValue(fixture.PreyItem, out var held) == true)
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
        var bestTier = byte.MaxValue;

        var actorPos = character?.Transform.World.Position;
        var selfTargetObjId = character?.CurrentTarget?.ObjId ?? 0u;
        var contested = ContestedPreyObjIds(character, observation);
        // Three tiers, in order: 0 the actor's OWN committed prey, 1 prey no
        // sibling is committed to, 2 prey a sibling is already on — plus one
        // DEMOTED lane for a stale pin: a target the stale-pin watchdog tripped
        // ((actor, target) held Disengaged-at-LeashEdge with unchanged HP for
        // CombatStallWakes wakes) is treated as currently unkillable and sinks
        // BELOW every un-tripped candidate, so the next wake re-selects a
        // killable target instead of holding the leash-dead pin. Demotion is
        // never a filter: with nothing else legal the demoted target still
        // wins, exactly like the all-contested tier-2 rule. Fail-closed the
        // other way too — HP damage resets the watchdog inside its counter,
        // so a target being killed is never demoted.
        const byte staleTier = 3;
        foreach (var objId in observation == null ? [] : PreyCandidates(observation))
        {
            rawCount++;
            var npc = character?.ParentWorld?.GetNpc(objId);
            if (npc == null)
            {
                rejectUnresolved++;
                continue;
            }

            var templateMatch = fixture.HuntActId != 0
                ? HuntMatches(fixture, npc.TemplateId)
                : sourceTemplates.Contains(npc.TemplateId);
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
                reject = LivingPreyRejection(character!, npc, templateMatch ? npc.TemplateId : 0);
                // A target can move after capture. The widened band is still
                // bounded at selection time; ordinary 25m fixture snapshots keep
                // their existing semantics.
                if (reject == "-" && !observation!.NearbyNpcObjIds.Contains(objId)
                    && (!double.IsFinite(distance) || distance >= LivingPreyScanRadiusM))
                    reject = "scan-range";
                combatLegal = reject == "-";
                switch (reject)
                {
                    case "dead": rejectDead++; break;
                    case "template": rejectTemplate++; break;
                    case "hostile": rejectHostile++; break;
                    case "attack": rejectAttack++; break;
                    case "stealth": rejectStealth++; break;
                }
            }

            var competingClaim = questRelevant && combatLegal && HasCompetingClaim(character!, npc);
            if (combatLegal)
                legalCount++;
            if (candidates.Count < CandidatesShown)
                candidates.Add(new CandidateFunnel(objId, npc.TemplateId, questRelevant, combatLegal, distance, reject)
                {
                    HasCompetingClaim = competingClaim,
                });
            // Nearest-first flat with an ObjId tiebreak (G3 doctrine:
            // deterministic before any cut), raised to tiers:
            //   0  the actor's OWN committed prey — never diverted;
            //   1  prey no sibling is committed to;
            //   2  prey a sibling is already on or another player has tagged.
            // A converged group therefore spreads across the census instead of
            // piling onto one boar, while a lone bot — or any bot with nothing
            // contested to avoid — still takes the nearest legal prey exactly
            // as before. Within a tier the order is the old flat one.
            //
            // Relevance still gates: a legal non-fixture-template NPC is never
            // the objective target. Deconfliction is a TIE-BREAK among relevant
            // legal prey, never a filter — when every candidate is contested the
            // nearest still wins, so a bot is never left with no target.
            if (questRelevant && combatLegal && !double.IsNaN(distance))
            {
                // A tripped stale pin sinks below every un-tripped candidate
                // (own-commitment included: tier 0 defers to it), yet stays a
                // candidate — never filtered, never skipped. The self id comes
                // from the actor's own live CurrentTarget read above (the
                // actor id is its objId), so the demotion is owner-scoped to
                // this actor's own pin and no other actor's watchdog can move it.
                var tier = objId == selfTargetObjId
                    ? (byte)(QuestBehavior.IsLeashStaleTarget(character?.ObjId ?? 0, objId) ? staleTier : 0)
                    : (byte)(QuestBehavior.IsLeashStaleTarget(character?.ObjId ?? 0, objId) ? staleTier
                        : contested.Contains(objId) || competingClaim ? 2 : 1);
                // An exhausted owned approach cannot keep a useful alternative
                // behind an otherwise valid commitment. Keep the sole fallback:
                // its named terminal lets the existing owner retire finitely.
                if (QuestBehavior.HasExhaustedPursuit(character!, objId, fixture.QuestId))
                    tier = 4;
                if (tier < bestTier
                    || (tier == bestTier && (distance < bestDistance
                        || (distance == bestDistance && objId < bestObjId))))
                {
                    best = npc;
                    bestDistance = distance;
                    bestObjId = objId;
                    bestTier = tier;
                }
            }
        }

        return new ObjectiveTargetFunnel(
            cycle, fixture.QuestId, objectiveResolved, objectiveDetail, sourceResolved, sourceDetail,
            relevance, have, fixture.HuntActId != 0
                ? character?.Quests.ActiveQuests.GetValueOrDefault(fixture.QuestId) is { } nativeQuest
                    && HuntObjective(fixture) is { } nativeHunt
                        ? QuestDirector.ObjectiveCountForFullCredit(nativeHunt, nativeQuest)
                        : fixture.HuntNeed
                : fixture.Need,
            rawCount,
            relevantCount, legalCount,
            best?.ObjId ?? 0, best?.TemplateId ?? 0, best == null ? double.NaN : bestDistance,
            rejectUnresolved, rejectDead, rejectTemplate, rejectHostile, rejectAttack, rejectStealth,
            candidates,
            fixture);

    }

    // A living tag is competition evidence, not a new attack or loot rule.
    // Read ordinary team membership; managed accounts alone do not form a party.
    private static bool HasCompetingClaim(Character character, Npc npc)
    {
        if (ReferenceEquals(npc.CurrentAggroTarget, character))
            return false; // Defending against this NPC remains useful.
        var tagging = npc.CharacterTagging;
        if (tagging.TagTeam != 0 && TeamManager.Instance.GetActiveTeam(tagging.TagTeam) is { } team)
            return !team.IsMember(character.Id);
        var tagger = tagging.Tagger;
        return tagger != null && tagger.Id != character.Id
            && !(character.InParty && TeamManager.Instance.AreTeamMembers(character.Id, tagger.Id));
    }

    /// <summary>
    /// The prey objIds at least one SIBLING character has already committed to.
    ///
    /// Read live from the wake's own census (<c>NearbyCharacterObjIds</c>),
    /// which <c>GameplayActor.Observe</c> fills from the same 25 m band as the
    /// NPC list — so this reads nothing the wake could not already see, and
    /// never a world scan. The self entry is skipped, so an actor's own live
    /// commitment never counts against it: a bot ALREADY on a boar keeps it, and
    /// only a bot choosing afresh is diverted. A sibling holding some other
    /// target contributes nothing here.
    ///
    /// This is an OBSERVATION, not a claim: no id is minted, nothing is
    /// reserved, and there is no registry to leak or expire — a departing
    /// sibling's target is released the moment the census drops it. It is
    /// deliberately blind to whether that sibling can actually kill: an actor's
    /// committed prey is committed, and second-guessing that is not this
    /// selector's business.
    ///
    /// Cost: one dictionary lookup per nearby character (the census is already
    /// materialised per wake), zero world scans, zero allocations.
    /// </summary>
    private static HashSet<uint> ContestedPreyObjIds(Character? character, BotObservedContext? observation)
    {
        var world = character?.ParentWorld;
        if (world == null || observation == null)
            return [];

        var siblings = observation.NearbyCharacterObjIds;
        if (siblings.Count == 0)
            return [];

        var selfObjId = character!.ObjId;
        var contested = new HashSet<uint>();
        foreach (var siblingObjId in siblings)
        {
            if (siblingObjId == selfObjId)
                continue;
            var targetObjId = world.GetCharacterByObjId(siblingObjId)?.CurrentTarget?.ObjId ?? 0u;
            if (targetObjId != 0)
                contested.Add(targetObjId);
        }
        return contested;
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
            if (fixture.HuntActId != 0)
                return HuntObjective(fixture) is { } hunt
                    ? (true, $"act{hunt.ActId}:huntx{hunt.Count}")
                    : (false, $"no-matching-hunt-act-{fixture.HuntActId}");
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
            if (fixture.HuntActId != 0)
                return HuntObjective(fixture) != null
                    ? (true, $"hunt-npc-{fixture.HuntNpcTemplate}-group-{fixture.HuntNpcGroup}")
                    : (false, $"no-matching-hunt-source-{fixture.HuntActId}");
            if (fixture.GatherNpcSources.Count > 1)
            {
                var sources = ValidNpcSources(fixture).ToArray();
                return (sources.Length != 0,
                    $"sources={sources.Length}:" + string.Join(",", sources.Take(CandidatesShown)
                        .Select(s => $"npc{s.NpcTemplateId}>pack{s.LootPackId}>item{fixture.PreyItem}")));
            }
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
