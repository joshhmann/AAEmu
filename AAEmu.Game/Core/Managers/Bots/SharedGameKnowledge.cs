using AAEmu.Game.Core.Managers;
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
