using System.Globalization;
using System.Text.Json;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Core.Managers.Bots;

// Opt-in inspection only. None of these observations are decision inputs.
// Queues, members and entity fields are sampled separately, not atomically.
internal static class BotSourceDiagnostics
{
    internal const int SourceLimit = 128;
    internal const int MemberLimit = 8;
    internal const int QueueLimit = 4096;
    private static readonly uint ObserverId = ParseObserverId(
        Environment.GetEnvironmentVariable("AAEMU_BOT_SOURCE_DIAG_CHARACTER_ID"));

    internal static uint ParseObserverId(string? value)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    internal static bool EnabledFor(uint characterId) => ObserverId != 0 && ObserverId == characterId;

    internal sealed record NpcState(uint ObjId, uint TemplateId, bool Registered, int Hp, bool Dead,
        string Position, uint TaggerObjId, uint TagTeam, DateTime DiedUtc, DateTime RespawnDue, DateTime DespawnDue,
        bool? QueuedForRespawn, bool? QueuedForDespawn, int? LootEntries);

    internal sealed record SourceState(uint Id, uint SpawnerId, uint UnitId, string Position,
        bool? NativeOptimal, bool? SearchBlocked, uint? MaxPopulation,
        int? ConfiguredSpawnerRespawnSeconds, int? ConfiguredSpawnerDespawnSeconds,
        int MemberCount, NpcState[] Members, int QueuedRespawnCount, NpcState[] QueuedRespawns,
        int QueuedDespawnCount, NpcState[] QueuedDespawns);

    internal sealed record Observation(DateTime SampledUtc, bool IndexKnown, double? IndexAgeSeconds,
        int SourceCount, int SourcesShown, int MemberLimit, int QueueLimit,
        int RespawnQueueTotal, int DespawnQueueTotal, bool RespawnQueueTruncated,
        bool DespawnQueueTruncated, SourceState[] Sources);

    internal static Observation Capture(WorldInstance world, Func<uint, bool> isSource, int queueLimit = QueueLimit)
    {
        var index = BotSpawnerIndex.Peek(world);
        var now = DateTime.UtcNow;
        if (index == null || world.SpawnManager == null)
            return new Observation(now, false, null, 0, 0, MemberLimit, queueLimit, 0, 0, false, false, []);

        var entries = index.ByTemplate.Where(kv => isSource(kv.Key)).SelectMany(kv => kv.Value)
            .OrderBy(s => s.Ordinal).ToArray();
        var queues = world.SpawnManager.CaptureQueues(queueLimit);
        var respawns = new HashSet<GameObject>(queues.Respawns, ReferenceEqualityComparer.Instance);
        var despawns = new HashSet<GameObject>(queues.Despawns, ReferenceEqualityComparer.Instance);
        var respawnTruncated = queues.RespawnTotal > queues.Respawns.Length;
        var despawnTruncated = queues.DespawnTotal > queues.Despawns.Length;
        // Queue members can have left the source's current member list already.
        var byRespawnSource = queues.Respawns.OfType<Npc>().Where(n => n.Spawner != null).ToLookup(n => n.Spawner);
        var byDespawnSource = queues.Despawns.OfType<Npc>().Where(n => n.Spawner != null).ToLookup(n => n.Spawner);

        bool? Queued(HashSet<GameObject> queue, bool truncated, Npc npc)
            => queue.Contains(npc) ? true : truncated ? null : false;

        NpcState Read(Npc npc)
        {
            var p = npc.Transform.World.Position;
            return new NpcState(npc.ObjId, npc.TemplateId,
                ReferenceEquals(world.GetNpc(npc.ObjId), npc), npc.Hp, npc.IsDead,
                FormattableString.Invariant($"{p.X:R},{p.Y:R},{p.Z:R}"),
                npc.CharacterTagging?.Tagger?.ObjId ?? 0, npc.CharacterTagging?.TagTeam ?? 0,
                npc.DeadTime, npc.Respawn, npc.Despawn, Queued(respawns, respawnTruncated, npc),
                Queued(despawns, despawnTruncated, npc), npc.LootingContainer?.ItemEntryCount);
        }

        var sources = entries.Take(SourceLimit).Select(entry =>
        {
            var s = entry.Spawner;
            Npc[] members = [];
            var memberCount = 0;
            if (s != null && s.SpawnedNpcs.TryGetValue(s.SpawnerId, out var list) && list != null)
            {
                lock (list)
                {
                    memberCount = list.Count;
                    members = list.Take(MemberLimit).ToArray();
                }
            }
            // No list/queue lock survives into world or tagging reads.
            var pendingRespawns = s == null ? [] : byRespawnSource[s].ToArray();
            var pendingDespawns = s == null ? [] : byDespawnSource[s].ToArray();
            var p = entry.Position;
            // Spawner fields are not effective per-NPC delays: native overrides
            // and loot state can alter due timestamps. Never run those rules here.
            return new SourceState(entry.Id, s?.SpawnerId ?? 0, entry.UnitId,
                p.HasValue ? FormattableString.Invariant($"{p.Value.X:R},{p.Value.Y:R},{p.Value.Z:R}") : "unknown",
                s?.Template != null && s.NpcSpawnerIds.Count > 0 ? s.IsOptimalSpawner() : null,
                s?.IsSearchBlocked(entry.UnitId), s?.Template?.MaxPopulation, s?.RespawnTime, s?.DespawnTime,
                memberCount, members.Select(Read).ToArray(), pendingRespawns.Length,
                pendingRespawns.Take(MemberLimit).Select(Read).ToArray(), pendingDespawns.Length,
                pendingDespawns.Take(MemberLimit).Select(Read).ToArray());
        }).ToArray();
        return new Observation(now, true, (now - index.BuiltUtc).TotalSeconds, entries.Length, sources.Length,
            MemberLimit, queueLimit, queues.RespawnTotal, queues.DespawnTotal,
            respawnTruncated, despawnTruncated, sources);
    }

    internal static string Serialize(Observation observation) => JsonSerializer.Serialize(observation);
}
