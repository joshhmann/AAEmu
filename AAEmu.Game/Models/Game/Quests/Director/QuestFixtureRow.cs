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
///   GatherActId / PreyItem / Need — the Progress component's first
///     QuestActObjItemGather (act id, item id, count), in (component id, act
///     id) order.
///   PreyTemplate / PreyPack — the loot chain for PreyItem: the
///     loot_pack_dropping_npcs row whose loot pack actually carries the item,
///     preferring a default-pack row, then the lowest (npc, pack) ids.
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
            questId, gatherActId, preyItem, need, preyTemplate, preyPack, reporterTemplate, rewardItem);
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
