using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pure observation projection for the live perception/quest loop (Workstream
/// E1 / converted scenario A1). Reads what the production scheduler already
/// did — active/ready/completed quest state off the live
/// <see cref="CharacterQuests"/>, plus the executor's per-wake quest-leg flag
/// and audit trace — and shapes it into a flat, JSON-friendly snapshot.
///
/// This is observation ONLY: it never accepts, advances, or turns in a quest,
/// never mutates the character, and never invents a parallel quest model. The
/// bridge <c>quest</c> op and the E2E driver consume exactly this projection,
/// so the driver's assertions and the report's fields read the same source of
/// truth the engine wrote.
///
/// Kept as a pure static projection (no DI, no world lookups) so it is
/// unit-testable against a rig-provisioned character without a live server.
/// </summary>
public static class BotQuestLoopObservation
{
    /// <summary>Flat per-quest row (id/step/status/objective counter).</summary>
    public sealed record QuestRow
    {
        public required uint QuestId { get; init; }
        public required string Status { get; init; }
        public required string Step { get; init; }
        public required int[] Objectives { get; init; }
    }

    /// <summary>One projected per-wake quest-loop snapshot.</summary>
    public sealed record Snapshot
    {
        public required uint CharacterId { get; init; }
        public required string CharacterName { get; init; }
        public required byte Level { get; init; }
        public required long Money { get; init; }
        public required float X { get; init; }
        public required float Y { get; init; }
        public required float Z { get; init; }
        public required uint ZoneId { get; init; }

        /// <summary>Did the quest leg land work this wake (executor flag)?</summary>
        public required bool QuestLegActive { get; init; }

        /// <summary>Active quests, ordered by id (deterministic).</summary>
        public required IReadOnlyList<QuestRow> ActiveQuests { get; init; }

        /// <summary>How many arc quests the character has completed.</summary>
        public required int CompletedCount { get; init; }

        /// <summary>Completed flags for a caller-supplied arc, in arc order.</summary>
        public required IReadOnlyList<bool> ArcCompleted { get; init; }
    }

    /// <summary>
    /// Projects the live character's quest-loop state. <paramref name="questIds"/>
    /// is an optional arc (e.g. the curated 6198/330 chain) whose completed
    /// flags are reported positionally; pass empty to skip.
    /// </summary>
    public static Snapshot Capture(Character character, bool questLegActive, IReadOnlyList<uint>? questIds = null)
    {
        ArgumentNullException.ThrowIfNull(character);

        var quests = character.Quests;
        var active = new List<QuestRow>();
        if (quests != null)
        {
            foreach (var (questId, quest) in quests.ActiveQuests
                         .Where(kv => kv.Value != null)
                         .OrderBy(kv => kv.Key))
            {
                active.Add(new QuestRow
                {
                    QuestId = questId,
                    Status = quest!.Status.ToString(),
                    Step = quest.Step.ToString(),
                    Objectives = quest.Objectives ?? [],
                });
            }
        }

        var arc = new List<bool>();
        if (questIds is { Count: > 0 } && quests != null)
            foreach (var questId in questIds)
                arc.Add(quests.HasQuestCompleted(questId));

        var position = character.Transform.World.Position;
        return new Snapshot
        {
            CharacterId = character.Id,
            CharacterName = character.Name ?? "",
            Level = character.Level,
            Money = character.Money,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            ZoneId = character.Transform.ZoneId,
            QuestLegActive = questLegActive,
            ActiveQuests = active,
            CompletedCount = quests?.CompletedQuestCount ?? 0,
            ArcCompleted = arc,
        };
    }

    /// <summary>Ids of active quests whose Status reads Ready (turn-in work waiting).</summary>
    public static IReadOnlyList<uint> ReadyQuestIds(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ActiveQuests
            .Where(q => string.Equals(q.Status, nameof(QuestStatus.Ready), StringComparison.Ordinal))
            .Select(q => q.QuestId)
            .ToList();
    }

    /// <summary>True when every arc quest has a completed flag (the whole chain landed).</summary>
    public static bool ArcComplete(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ArcCompleted.Count > 0 && snapshot.ArcCompleted.All(done => done);
    }
}
