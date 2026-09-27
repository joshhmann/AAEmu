using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Shared ArcheAge knowledge (autonomy Wave B): deterministic game rules
/// every bot reads instead of rediscovering — quest prerequisites, offer/
/// report linkage, Tier 0 canonical ids. Static data only; individual bot
/// state (level, copper, bag, quests, location) stays on the
/// Character/observation and is never stored here.
///
/// Backed by the live QuestManager — the same source the engine enforces —
/// so knowledge can never drift from the rules.
/// </summary>
public static class SharedGameKnowledge
{
    /// <summary>Tier 0 canonical ids (real 1.2 rows).</summary>
    public const uint PotatoSeedItemId = 15659;
    public const uint PotatoCropDoodadId = 2259;
    public const uint PotatoItemId = 7992;
    public const long PotatoSeedUnitPrice = 25;
    public const uint PotatoSeedMerchantTemplateId = 8522;
    public const uint ScarecrowDesignQuestId = 4438;
    public const uint ScarecrowDesignItemId = 15596;

    /// <summary>
    /// Tier 0 canonical TREE SAPLING (real 1.2 rows): 코르크참나무 묘목 (cork oak
    /// sapling) 4862 — <see cref="ItemCategory.Saplings"/> (8), sold by the sapling
    /// merchant pack 141 (&lt;묘목 - 지역1&gt;, NPC 8521/8523), plantable via
    /// item_spawn_doodads 4862 → doodad 412 (코르크참나무).
    ///
    /// A SEED is never a sapling: potato seed <see cref="PotatoSeedItemId"/> 15659
    /// is <see cref="ItemCategory.Seed"/> (51) and plants the 감자 crop 2259. Every
    /// sapling identity resolves through <see cref="IsTreeSapling"/> — the engine's
    /// own item category — never through a seed id or a seed-shaped label.
    /// </summary>
    public const uint TreeSaplingItemId = 4862;

    /// <summary>
    /// Canonical unit price of <see cref="TreeSaplingItemId"/> (items.price = 400 for
    /// 4862 — the same value <c>GameplayActor.Buy</c> charges from the pack-141 row).
    /// </summary>
    public const uint TreeSaplingUnitPrice = 400;

    /// <summary>
    /// Canonical "is this item a plantable tree sapling?" — the engine's own
    /// identity, not a label a caller invents. True only for
    /// <see cref="ItemCategory.Saplings"/> rows (모종: 65 canonical 1.2 items, each
    /// carrying an item_spawn_doodads row). A seed
    /// (<see cref="ItemCategory.Seed"/>, e.g. potato seed 15659) is never a sapling
    /// no matter which action asks. An unknown or unloaded template fails closed to
    /// false.
    /// </summary>
    public static bool IsTreeSapling(uint itemTemplateId)
    {
        var template = ItemManager.Instance.GetTemplate(itemTemplateId);
        return template != null && template.CategoryId == (int)ItemCategory.Saplings;
    }

    /// <summary>Level required to start a quest (0 = unknown template).</summary>
    public static byte QuestStartLevel(uint questId)
        => QuestManager.Instance.GetTemplate(questId)?.Level ?? 0;

    /// <summary>NPC templates offering a quest (Start ConAcceptNpc linkage).</summary>
    public static IReadOnlyList<uint> QuestOfferers(uint questId)
    {
        var template = QuestManager.Instance.GetTemplate(questId);
        if (template == null)
            return [];
        var offerers = new List<uint>();
        foreach (var component in template.GetComponents(QuestComponentKind.Start))
            foreach (var act in component.ActTemplates.OfType<QuestActConAcceptNpc>())
                if (act.NpcId != 0 && !offerers.Contains(act.NpcId))
                    offerers.Add(act.NpcId);
        return offerers;
    }

    /// <summary>Reporter NPC template ids for a quest's Ready components (empty = doodad/auto).</summary>
    public static IReadOnlyList<uint> QuestReporters(uint questId)
    {
        var template = QuestManager.Instance.GetTemplate(questId);
        if (template == null)
            return [];
        var reporters = new List<uint>();
        foreach (var component in template.GetComponents(QuestComponentKind.Ready))
            foreach (var act in component.ActTemplates.OfType<QuestActConReportNpc>())
                if (act.NpcId != 0 && !reporters.Contains(act.NpcId))
                    reporters.Add(act.NpcId);
        return reporters;
    }

    /// <summary>Prerequisite quest ids composed into Progress (QuestActObjCompleteQuest).</summary>
    public static IReadOnlyList<uint> ProgressPrerequisites(uint questId)
    {
        var template = QuestManager.Instance.GetTemplate(questId);
        if (template == null)
            return [];
        var prereqs = new List<uint>();
        foreach (var component in template.GetComponents(QuestComponentKind.Progress))
            foreach (var act in component.ActTemplates.OfType<QuestActObjCompleteQuest>())
                if (act.QuestId != 0 && !prereqs.Contains(act.QuestId))
                    prereqs.Add(act.QuestId);
        return prereqs;
    }
}
