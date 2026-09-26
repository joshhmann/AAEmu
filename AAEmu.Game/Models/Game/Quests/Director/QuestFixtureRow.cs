using System.Collections.Concurrent;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// One quest's static fixture facts, DERIVED from live game data — never
/// hand-typed. The row is the single place a quest-owned production path reads
/// its act ids, item ids, prey template, loot pack, reporter template and
/// reward item from, so a data change (or a different quest) cannot silently
/// diverge from the DB.
///
/// Derivation (every field read off the loaded QuestManager / ItemManager /
/// LootGameData surfaces; any unresolvable field stays zero and the caller's
/// fail-closed predicates name the failure):
///   ObjectivePattern / ObjectiveActType — the quest's objective SHAPE, read
///     off the Progress component's first objective act (CountsAsAnObjective,
///     in (component id, act id) order): an item gather derives
///     <see cref="QuestPattern.KillX"/>, an item use derives
///     <see cref="QuestPattern.UseItem"/>. A quest with NO Progress objective
///     act derives <see cref="QuestPattern.Unknown"/> with an empty act type —
///     the pure-bootstrap shape (advance + turn-in only). A Progress objective
///     act the vocabulary does not serve yet derives
///     <see cref="QuestPattern.Unknown"/> WITH its act type name, which is the
///     caller's fail-closed trigger. The row is derived for EVERY quest; the
///     shape is what lets the director tell "no objective" (bootstrap) from
///     "an objective we cannot serve" (fail closed).
///   GatherActId / PreyItem / Need — the Progress component's first
///     QuestActObjItemGather (act id, item id, count), in (component id, act
///     id) order.
///   PreyTemplate / PreyPack — the loot chain for PreyItem: the
///     loot_pack_dropping_npcs row whose loot pack actually carries the item,
///     preferring a default-pack row, then the lowest (npc, pack) ids.
///   UseItemActId / UseItemTemplateId / UseItemNeed — the Progress component's
///     first QuestActObjItemUse (act id, item template, count), the item-use
///     objective's own facts (distinct from the gather act's).
///   AutoComplete — true when the quest carries a QuestActConAutoComplete in
///     its Ready or Reward component: the engine completes it on its own
///     evaluation, so the plan needs no NPC turn-in leg to finish it.
///   ReporterTemplate — the Ready component's QuestActConReportNpc npc,
///     falling back to the Start component's QuestActConAcceptNpc.
///   RewardItem — the Reward component's QuestActSupplyItem item, falling back
///     to the Supply component.
///
/// Memoized per quest id (<see cref="FromQuestData"/>): the derivation's loot
/// inversion walks the whole <c>loot_pack_dropping_npcs</c> table (~15k rows)
/// and probes each row's pack, so a per-wake re-derivation is the wake path's
/// dominant cost once it is called per active quest per wake. A miss derives
/// and stores; a hit returns the stored row. Reads stay pure — the memo is a
/// cache of a pure read, never a world mutation.
///
/// Invalidation is EXPLICIT, never time- or hit-based, so a row can never
/// outlive the data it was derived from: <see cref="Invalidate"/> /
/// <see cref="InvalidateAll"/> are called from the reload seams that rebuild
/// the three surfaces the row reads — <c>QuestManager.Load</c> (templates),
/// <c>ItemManager.Load</c> (npc→loot-pack rows) and <c>LootGameData.Load</c>
/// (loot packs). Each bumps a generation before the next derivation is allowed
/// to publish, so a row derived across a reload boundary is never stored.
/// Any rig or caller that stages these surfaces outside those loaders must
/// invalidate too, or it will read a row derived from the previous staging.
/// </summary>
public sealed record QuestFixtureRow(
    uint QuestId,
    uint GatherActId,
    uint PreyItem,
    int Need,
    uint PreyTemplate,
    uint PreyPack,
    uint ReporterTemplate,
    uint RewardItem)
{
    /// <summary>
    /// The quest's objective shape, derived from the Progress component's first
    /// objective act (see the type doc). <see cref="QuestPattern.Unknown"/> with
    /// an empty <see cref="ObjectiveActType"/> is the pure-bootstrap shape (no
    /// Progress objective act at all); <see cref="QuestPattern.Unknown"/> WITH an
    /// act type is an objective this vocabulary cannot serve yet — the caller's
    /// fail-closed trigger.
    /// </summary>
    public QuestPattern ObjectivePattern { get; init; } = QuestPattern.Unknown;

    /// <summary>
    /// The Progress objective act's CLR type name (e.g.
    /// <c>QuestActObjItemGather</c>), or "" when the quest carries no objective
    /// act. Named so an unserved objective's refusal says WHICH act it could
    /// not serve.
    /// </summary>
    public string ObjectiveActType { get; init; } = "";

    /// <summary>The Progress component's first QuestActObjItemUse act id (0 when the quest carries none).</summary>
    public uint UseItemActId { get; init; }

    /// <summary>The item template that act consumes (0 when the quest carries no item-use objective).</summary>
    public uint UseItemTemplateId { get; init; }

    /// <summary>The item-use objective's required count (0 when the quest carries none).</summary>
    public int UseItemNeed { get; init; }

    /// <summary>
    /// True when the quest carries a <c>QuestActConAutoComplete</c> in its Ready
    /// or Reward component: the engine completes the quest on its own step
    /// evaluation, so the plan needs no NPC turn-in leg to finish it.
    /// </summary>
    public bool AutoComplete { get; init; }

    /// <summary>
    /// Memo of derived rows, one per quest id. Concurrent: the wake path can
    /// derive different quests from different bot threads.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, MemoEntry> RowCache = new();

    /// <summary>
    /// Bumped by every invalidation. A derivation that started before a reload
    /// compares generations before publishing, so a row derived against a
    /// half-replaced surface is served to its caller but never memoized.
    /// </summary>
    private static long s_dataGeneration;

    /// <summary>
    /// The three surfaces the derivation reads, WITHOUT constructing any of
    /// them. Captured per derivation and re-checked per lookup.
    /// </summary>
    private readonly record struct DataSources(
        QuestManager? Quest,
        ItemManager? Items,
        LootGameData? Loot)
    {
        public static DataSources Snapshot() => new(
            Singleton<QuestManager>.PeekInstance,
            Singleton<ItemManager>.PeekInstance,
            Singleton<LootGameData>.PeekInstance);

        /// <summary>True when the live surfaces still are the captured ones.</summary>
        public bool Unchanged => ReferenceEquals(Quest, Singleton<QuestManager>.PeekInstance)
                                 && ReferenceEquals(Items, Singleton<ItemManager>.PeekInstance)
                                 && ReferenceEquals(Loot, Singleton<LootGameData>.PeekInstance);
    }

    /// <summary>
    /// A memoized row plus the surfaces it was derived from. A reload rebuilds
    /// those surfaces IN PLACE (same references), so the explicit invalidation
    /// seam is what catches it; a wholesale replacement (a rig swapping a
    /// manager singleton) changes the references, which this entry detects on
    /// lookup — a replaced surface can never be shadowed by a row derived from
    /// its predecessor.
    /// </summary>
    private readonly record struct MemoEntry(QuestFixtureRow Row, DataSources Sources);

    /// <summary>
    /// The derived row for <paramref name="questId"/>: the memoized row on a
    /// hit, else a fresh derivation that is stored. Never throws: an unreadable
    /// surface yields the zero row (or a partly-populated one when only the
    /// loot source is unresolvable).
    /// </summary>
    public static QuestFixtureRow FromQuestData(uint questId)
    {
        if (RowCache.TryGetValue(questId, out var cached) && cached.Sources.Unchanged)
            return cached.Row;

        // Snapshot BEFORE deriving so the entry names the surfaces the
        // derivation actually read, never a replacement that landed mid-read.
        var sources = DataSources.Snapshot();
        var generation = Interlocked.Read(ref s_dataGeneration);
        var derived = DeriveFromQuestData(questId);

        if (Interlocked.Read(ref s_dataGeneration) == generation)
            RowCache[questId] = new MemoEntry(derived, sources);
        return derived;
    }

    /// <summary>
    /// Drops the memoized row for one quest. Public for the game-data reload
    /// path and for anything else that replaces the row's source surfaces.
    /// </summary>
    public static void Invalidate(uint questId)
    {
        Interlocked.Increment(ref s_dataGeneration);
        RowCache.TryRemove(questId, out _);
    }

    /// <summary>
    /// Drops every memoized row — the reload seam's call, because a rebuild of
    /// templates, npc→pack rows or loot packs can change any quest's row.
    /// </summary>
    public static void InvalidateAll()
    {
        Interlocked.Increment(ref s_dataGeneration);
        RowCache.Clear();
    }

    /// <summary>
    /// The derivation itself: pure reads of the loaded tables, no caching and
    /// no world mutation. <see cref="FromQuestData"/> is the only entry point
    /// callers need.
    /// </summary>
    private static QuestFixtureRow DeriveFromQuestData(uint questId)
    {
        if (questId == 0)
            return Empty(questId);

        uint gatherActId = 0;
        uint preyItem = 0;
        var need = 0;
        uint reporterTemplate = 0;
        uint rewardItem = 0;
        uint useItemActId = 0;
        uint useItemTemplateId = 0;
        var useItemNeed = 0;
        var autoComplete = false;
        var objectivePattern = QuestPattern.Unknown;
        var objectiveActType = "";
        try
        {
            var template = QuestManager.Instance?.GetTemplate(questId);
            if (template == null)
                return Empty(questId);

            var gather = FirstAct<QuestActObjItemGather>(template, QuestComponentKind.Progress);
            if (gather != null)
            {
                gatherActId = gather.ActId;
                preyItem = gather.ItemId;
                need = gather.Count;
            }

            var useItem = FirstAct<QuestActObjItemUse>(template, QuestComponentKind.Progress);
            if (useItem != null)
            {
                useItemActId = useItem.ActId;
                useItemTemplateId = useItem.ItemId;
                useItemNeed = useItem.Count;
            }

            // The objective SHAPE is the Progress component's first act that
            // counts as an objective — the act the quest's step machine waits
            // on. A quest with none is the pure-bootstrap shape; one whose act
            // this vocabulary does not serve keeps the act's name so the
            // director can refuse it BY NAME instead of guessing.
            var objective = template.GetComponents(QuestComponentKind.Progress)
                .SelectMany(c => c.ActTemplates)
                .Where(a => a.CountsAsAnObjective)
                .OrderBy(a => a.ParentComponent.Id)
                .ThenBy(a => a.ActId)
                .FirstOrDefault();
            if (objective != null)
            {
                objectiveActType = objective.GetType().Name;
                objectivePattern = objective switch
                {
                    QuestActObjItemGather => QuestPattern.KillX,
                    QuestActObjItemUse => QuestPattern.UseItem,
                    _ => QuestPattern.Unknown
                };
            }

            // Auto-complete: the engine's own completion act, on the Ready or
            // Reward step (252 carries it on Reward; a quest that carries it
            // needs no reporter turn-in leg).
            autoComplete = FirstAct<QuestActConAutoComplete>(template, QuestComponentKind.Ready) != null
                           || FirstAct<QuestActConAutoComplete>(template, QuestComponentKind.Reward) != null;

            // Reporter: the Ready-step turn-in target, else the Start-step
            // acceptor (a quest may carry both; for 251 they agree).
            reporterTemplate = FirstAct<QuestActConReportNpc>(template, QuestComponentKind.Ready)?.NpcId
                               ?? FirstAct<QuestActConAcceptNpc>(template, QuestComponentKind.Start)?.NpcId
                               ?? 0;

            // Reward: the Reward-step supply, else the Supply-step supply.
            rewardItem = (FirstAct<QuestActSupplyItem>(template, QuestComponentKind.Reward)
                          ?? FirstAct<QuestActSupplyItem>(template, QuestComponentKind.Supply))?.ItemId ?? 0;
        }
        catch
        {
            // Unreadable quest surface: keep whatever resolved.
        }

        var (preyTemplate, preyPack) = ResolveDropSource(preyItem);

        return new QuestFixtureRow(
            questId, gatherActId, preyItem, need, preyTemplate, preyPack, reporterTemplate, rewardItem)
        {
            ObjectivePattern = objectivePattern,
            ObjectiveActType = objectiveActType,
            UseItemActId = useItemActId,
            UseItemTemplateId = useItemTemplateId,
            UseItemNeed = useItemNeed,
            AutoComplete = autoComplete
        };
    }

    private static QuestFixtureRow Empty(uint questId)
        => new(questId, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Linear loot-list membership (the lists hold a handful of rows; no LINQ allocation).</summary>
    private static bool HasLoot(List<Loot> loots, uint itemId)
    {
        for (var i = 0; i < loots.Count; i++)
        {
            if (loots[i].ItemId == itemId)
                return true;
        }
        return false;
    }

    /// <summary>First act of <typeparamref name="T"/> in the given component kind, in (component id, act id) order.</summary>
    private static T? FirstAct<T>(QuestTemplate template, QuestComponentKind kind) where T : QuestActTemplate
        => template.GetComponents(kind)
            .SelectMany(c => c.ActTemplates)
            .OfType<T>()
            .OrderBy(a => a.ParentComponent.Id)
            .ThenBy(a => a.ActId)
            .FirstOrDefault();

    /// <summary>
    /// Inverts the npc→loot-pack→loot chain for <paramref name="itemId"/>: the
    /// (npc template, loot pack) whose pack carries the item. Prefers a
    /// default-pack row, then the lowest npc id, then the lowest pack id, so
    /// the result is deterministic for any item. Never throws (a bare or
    /// unloaded loot surface yields (0, 0)).
    /// </summary>
    private static (uint PreyTemplate, uint PreyPack) ResolveDropSource(uint itemId)
    {
        if (itemId == 0)
            return (0, 0);

        try
        {
            // Invert npc→pack→item in one pass over the ~15k rows: each row
            // probes its pack's loot list and the best row wins by a total
            // order (default-pack, npc id, pack id), so the result never
            // depends on enumeration order.
            uint bestTemplate = 0;
            uint bestPack = 0;
            var bestDefault = false;
            foreach (var row in ItemManager.Instance.GetAllLootPackDroppingNpcs())
            {
                var pack = LootGameData.Instance.GetPack(row.LootPackId);
                if (pack?.Loots == null || !HasLoot(pack.Loots, itemId))
                    continue;
                var better = bestPack == 0
                             || (row.DefaultPack && !bestDefault)
                             || (row.DefaultPack == bestDefault
                                 && (row.NpcId < bestTemplate
                                     || (row.NpcId == bestTemplate && row.LootPackId < bestPack)));
                if (!better)
                    continue;
                bestTemplate = row.NpcId;
                bestPack = row.LootPackId;
                bestDefault = row.DefaultPack;
            }
            return (bestTemplate, bestPack);
        }
        catch
        {
            return (0, 0);
        }
    }
}
