using System.Text.Json;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Progression-graph planner cursor (autonomy Wave B): the durable,
/// inspectable record of where a bot stands on the golden path and what
/// comes next. Milestones are data-driven nodes (quest ids, copper lines,
/// item holdings); the cursor is serialized to PlayerBotMetadata.PlannerState
/// JSON through PlayerBotMetadataStore.RecordPlannerState — the field that
/// was storage-only until this cursor gave it a consumer.
///
/// The cursor answers "how do I get from here to there" WITHOUT duplicating
/// gameplay: legs (quest/farm/hunt) do the doing; the cursor only tracks
/// position and next milestone.
/// </summary>
public sealed class BotProgressionCursor
{
    /// <summary>Milestone kinds on the golden path.</summary>
    public enum MilestoneKind : byte
    {
        /// <summary>Complete a quest id through normal mechanics.</summary>
        CompleteQuest = 0,
        /// <summary>Hold at least the copper amount.</summary>
        HoldCopper = 1,
        /// <summary>Hold at least the count of an item template.</summary>
        HoldItem = 2,
        /// <summary>Reach a character level.</summary>
        ReachLevel = 3,
    }

    /// <summary>One golden-path milestone.</summary>
    public sealed record Milestone(
        string Id,
        MilestoneKind Kind,
        uint QuestId = 0,
        long Copper = 0,
        uint ItemTemplateId = 0,
        int ItemCount = 0,
        byte Level = 0);

    /// <summary>Cursor state: ordered milestones + position.</summary>
    public sealed record CursorState(
        IReadOnlyList<Milestone> Milestones,
        int Position,
        string UpdatedReason)
    {
        public Milestone? Current => Position >= 0 && Position < Milestones.Count ? Milestones[Position] : null;
        public bool Complete => Position >= Milestones.Count;
    }

    /// <summary>Canonical Tier 0 golden path: copper bootstrap → farm sustain.</summary>
    public static IReadOnlyList<Milestone> Tier0Path() =>
    [
        new("quest-4438-design", MilestoneKind.CompleteQuest, QuestId: 4438),
        new("seed-money", MilestoneKind.HoldCopper, Copper: 100),
        new("potato-seed", MilestoneKind.HoldItem, ItemTemplateId: 15659, ItemCount: 1),
        new("potato-yield", MilestoneKind.HoldItem, ItemTemplateId: 7992, ItemCount: 1),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Serializes a cursor for RecordPlannerState.</summary>
    public static string Serialize(CursorState cursor)
        => JsonSerializer.Serialize(cursor, JsonOptions);

    /// <summary>Reads a cursor back; empty/invalid JSON → fresh Tier 0 path.</summary>
    public static CursorState Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new CursorState(Tier0Path(), 0, "fresh");
        try
        {
            return JsonSerializer.Deserialize<CursorState>(json, JsonOptions)
                ?? new CursorState(Tier0Path(), 0, "unparseable");
        }
        catch
        {
            return new CursorState(Tier0Path(), 0, "unparseable");
        }
    }

    /// <summary>
    /// Advances the cursor past satisfied milestones against live state.
    /// Pure function of (cursor, copper, level, bagCounts, completedQuests).
    /// </summary>
    public static CursorState Advance(
        CursorState cursor,
        long copper,
        byte level,
        IReadOnlyDictionary<uint, int> bagCounts,
        IReadOnlySet<uint> completedQuests,
        string reason)
    {
        var position = cursor.Position;
        while (position < cursor.Milestones.Count && IsSatisfied(cursor.Milestones[position], copper, level, bagCounts, completedQuests))
            position++;
        return position == cursor.Position ? cursor : cursor with { Position = position, UpdatedReason = reason };
    }

    private static bool IsSatisfied(
        Milestone milestone,
        long copper,
        byte level,
        IReadOnlyDictionary<uint, int> bagCounts,
        IReadOnlySet<uint> completedQuests)
        => milestone.Kind switch
        {
            MilestoneKind.CompleteQuest => completedQuests.Contains(milestone.QuestId),
            MilestoneKind.HoldCopper => copper >= milestone.Copper,
            MilestoneKind.HoldItem => bagCounts.TryGetValue(milestone.ItemTemplateId, out var count) && count >= milestone.ItemCount,
            MilestoneKind.ReachLevel => level >= milestone.Level,
            _ => false,
        };
}
