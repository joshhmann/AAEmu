using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// A per-world copy of the NPC spawner table, grouped by NPC template, that
/// the bots' quest travel reads instead of copying and scanning the whole
/// table on every decision. Populated-area run 5 measured the quest-giver
/// search at 260-300 ms per call (1.5 s for the first): it copied every
/// spawner in the world (<see cref="AAEmu.Game.Core.Managers.World.SpawnManager.GetAllSpawners"/>)
/// and re-checked the same NPC template's quests once per spawner.
///
/// Spawners are placed at world load and change only for world events. The
/// index is rebuilt when the table's group count changes (an event adding or
/// removing spawners) and otherwise at most once per
/// <see cref="RefreshInterval"/>. Entries keep
/// the table's enumeration order (<see cref="Entry.Ordinal"/>), so callers that
/// broke ties by "first found" still pick the same spawner.
///
/// Known staleness, bounded by <see cref="RefreshInterval"/>: a spawner moved
/// in place, or a reload that clears and refills the same number of groups,
/// is seen up to 60 s late (travel walks to the old spot until then).
/// </summary>
public static class BotSpawnerIndex
{
    public static TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    public readonly record struct Entry(uint Id, uint UnitId, Vector3? Position, int Ordinal);

    public sealed class Snapshot
    {
        public required IReadOnlyDictionary<uint, Entry[]> ByTemplate { get; init; }
        public required DateTime BuiltUtc { get; init; }
        public required int GroupCount { get; init; }
    }

    private static readonly ConditionalWeakTable<WorldInstance, Snapshot> s_snapshots = new();
    private static readonly object s_buildLock = new();

    /// <summary>The world's current snapshot, or null when it has no spawner tables.</summary>
    public static Snapshot? For(WorldInstance? world)
    {
        if (world?.SpawnManager == null)
            return null;
        var now = DateTime.UtcNow;
        var groups = world.SpawnManager.NpcSpawnerGroupCount;
        if (s_snapshots.TryGetValue(world, out var snapshot) && IsCurrent(snapshot, now, groups))
            return snapshot;
        lock (s_buildLock)
        {
            if (s_snapshots.TryGetValue(world, out snapshot) && IsCurrent(snapshot, now, groups))
                return snapshot;
            var all = world.SpawnManager.GetAllSpawners();
            if (all == null)
                return null;
            var byTemplate = new Dictionary<uint, List<Entry>>();
            var ordinal = 0;
            foreach (var spawner in all.SelectMany(s => s.Value))
            {
                var p = spawner.Position;
                var entry = new Entry(spawner.Id, spawner.UnitId,
                    p == null ? null : new Vector3(p.X, p.Y, p.Z), ordinal++);
                if (!byTemplate.TryGetValue(spawner.UnitId, out var list))
                    byTemplate[spawner.UnitId] = list = [];
                list.Add(entry);
            }
            snapshot = new Snapshot
            {
                ByTemplate = byTemplate.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
                BuiltUtc = now,
                GroupCount = all.Count,
            };
            s_snapshots.AddOrUpdate(world, snapshot);
            return snapshot;
        }
    }

    private static bool IsCurrent(Snapshot snapshot, DateTime now, int groups)
        => snapshot.GroupCount == groups && now - snapshot.BuiltUtc < RefreshInterval;

    /// <summary>Drops every cached snapshot (rigs that edit spawners between reads).</summary>
    public static void Invalidate() => s_snapshots.Clear();
}
