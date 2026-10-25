using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

// Supplied source/member/queue states, not earned gameplay or live spawn evidence.
[NotInParallel]
public class BotSourceDiagnosticsTests
{
    private const uint TemplateId = 978_401;

    private static WorldInstance World()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor("source-observer");
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        return world;
    }

    private static NpcSpawner Source(WorldInstance world)
    {
        var map = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var id = (uint)(978_500 + map.Count);
        var source = new NpcSpawner { Id = id, SpawnerId = id, UnitId = TemplateId,
            Position = new WorldSpawnPosition { X = 10, Y = 20, Z = 30 } };
        map[id] = [source];
        return source;
    }

    private static Npc Member(NpcSpawner source, uint objId = 978_900, int hp = 100)
        => new() { ObjId = objId, TemplateId = TemplateId, Spawner = source,
            Template = new NpcTemplate { Id = TemplateId, Scale = 1 }, Hp = hp, MaxHp = 100 };

    private static BotSourceDiagnostics.Observation Observe(WorldInstance world,
        Func<uint, bool> match, int queueLimit = BotSourceDiagnostics.QueueLimit)
    {
        // Explicit fixture knowledge supplied through the ordinary cache owner.
        _ = BotSpawnerIndex.For(world);
        return BotSourceDiagnostics.Capture(world, match, queueLimit);
    }

    [Test]
    public async Task ObserverDoesNotWarmOrRefreshTheBotsIndex()
    {
        var world = World();
        Source(world);
        await Assert.That(BotSourceDiagnostics.Capture(world, id => id == TemplateId).IndexKnown).IsFalse();
        await Assert.That(BotSpawnerIndex.Peek(world)).IsNull();
        var before = BotSpawnerIndex.For(world);
        Source(world); // New native source, not yet read by the normal owner.
        await Assert.That(BotSourceDiagnostics.Capture(world, id => id == TemplateId).SourceCount).IsEqualTo(1);
        await Assert.That(BotSpawnerIndex.Peek(world)).IsSameReferenceAs(before!);
        _ = BotSpawnerIndex.For(world);
        await Assert.That(BotSourceDiagnostics.Capture(world, id => id == TemplateId).SourceCount).IsEqualTo(2);
    }

    [Test]
    public async Task LivingAndCorpse_ReadNativeStateWithoutChangingIt()
    {
        var world = World();
        var source = Source(world);
        var living = Member(source);
        var corpse = Member(source, 978_901, 0);
        world.AddObject(living);
        world.AddObject(corpse);
        source.SpawnedNpcs[source.SpawnerId] = [living, corpse];
        var before = source.SpawnedNpcs[source.SpawnerId].ToArray();
        var seen = Observe(world, id => id == TemplateId);
        var row = seen.Sources.Single();
        await Assert.That(row.Members.Single(n => n.ObjId == living.ObjId).Registered).IsTrue();
        await Assert.That(row.Members.Single(n => n.ObjId == living.ObjId).Dead).IsFalse();
        await Assert.That(row.Members.Single(n => n.ObjId == corpse.ObjId).Dead).IsTrue();
        await Assert.That(row.Members.Single(n => n.ObjId == corpse.ObjId).LootEntries).IsEqualTo(0);
        await Assert.That(row.MemberCount).IsEqualTo(2);
        await Assert.That(source.SpawnedNpcs[source.SpawnerId]).IsEquivalentTo(before);
        await Assert.That(living.Hp).IsEqualTo(100);
        await Assert.That(corpse.Hp).IsEqualTo(0);
        await Assert.That(seen.IndexAgeSeconds!.Value).IsGreaterThanOrEqualTo(0d);
        using var json = JsonDocument.Parse(BotSourceDiagnostics.Serialize(seen));
        await Assert.That(json.RootElement.GetProperty("Sources")[0].GetProperty("Position").GetString())
            .IsEqualTo("10,20,30");
    }

    [Test]
    public async Task SameObjIdOnAnotherObject_DoesNotEstablishRegistration()
    {
        var world = World();
        var source = Source(world);
        var old = Member(source);
        var replacement = Member(source);
        world.AddObject(replacement);
        source.SpawnedNpcs[source.SpawnerId] = [old];
        var seen = Observe(world, id => id == TemplateId).Sources.Single();
        await Assert.That(seen.Members.Single().Registered).IsFalse();
        await Assert.That(world.GetNpc(replacement.ObjId)).IsSameReferenceAs(replacement);
    }

    [Test]
    public async Task DueTimestampIsNotQueueMembership_QueuedNpcCanLeaveMemberList()
    {
        var world = World();
        var source = Source(world);
        var pending = Member(source, hp: 0);
        pending.DeadTime = DateTime.UtcNow;
        pending.Respawn = pending.DeadTime.AddMinutes(1);
        source.SpawnedNpcs[source.SpawnerId] = [pending];
        var first = Observe(world, id => id == TemplateId).Sources.Single();
        await Assert.That(first.Members.Single().QueuedForRespawn).IsEqualTo(false);
        world.SpawnManager.AddRespawn(pending);
        world.SpawnManager.AddDespawn(pending);
        source.SpawnedNpcs[source.SpawnerId].Clear();
        var second = Observe(world, id => id == TemplateId).Sources.Single();
        await Assert.That(second.MemberCount).IsEqualTo(0);
        await Assert.That(second.QueuedRespawnCount).IsEqualTo(1);
        await Assert.That(second.QueuedRespawns.Single().QueuedForRespawn).IsEqualTo(true);
        await Assert.That(second.QueuedDespawns.Single().QueuedForDespawn).IsEqualTo(true);
        await Assert.That(pending.Respawn).IsEqualTo(first.Members.Single().RespawnDue);
        await Assert.That(first.Members.Single().DiedUtc).IsEqualTo(pending.DeadTime);
    }

    [Test]
    public async Task EmptyAndInactiveSources_RemainDistinctFromReady()
    {
        var world = World();
        var empty = Source(world);
        var unknown = Observe(world, id => id == TemplateId).Sources.Single();
        await Assert.That(unknown.MemberCount).IsEqualTo(0);
        await Assert.That(unknown.NativeOptimal).IsNull();
        await Assert.That(unknown.SearchBlocked).IsEqualTo(false);
        using var definitions = new NpcSpawnerSearchTestRig();
        definitions.Add(world, new Vector3(40, 0, 0));
        var native = Observe(world, id => id == 3451);
        await Assert.That(native.Sources.Single(s => s.SpawnerId == 3348).NativeOptimal).IsEqualTo(true);
        await Assert.That(native.Sources.Single(s => s.SpawnerId == 3713).NativeOptimal).IsEqualTo(false);
        await Assert.That(empty.SpawnedNpcs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BoundsAndQueueCopies_AreExplicitAndDoNotMutateQueues()
    {
        var world = World();
        var source = Source(world);
        for (var i = 0; i < BotSourceDiagnostics.SourceLimit + 1; i++) Source(world);
        source.SpawnedNpcs[source.SpawnerId] = Enumerable.Range(0, BotSourceDiagnostics.MemberLimit + 1)
            .Select(i => Member(source, (uint)(979_000 + i))).ToList();
        // Source/member bound is independent of output ordering.
        var full = Observe(world, id => id == TemplateId);
        await Assert.That(full.SourceCount).IsEqualTo(BotSourceDiagnostics.SourceLimit + 2);
        await Assert.That(full.SourcesShown).IsEqualTo(BotSourceDiagnostics.SourceLimit);
        await Assert.That(full.Sources[0].MemberCount).IsEqualTo(BotSourceDiagnostics.MemberLimit + 1);
        await Assert.That(full.Sources[0].Members.Length).IsEqualTo(BotSourceDiagnostics.MemberLimit);
        var queued = Member(source);
        world.SpawnManager.AddRespawn(queued);
        var copy = world.SpawnManager.CaptureQueues(1);
        copy.Respawns[0] = Member(source, 979_500);
        await Assert.That(world.SpawnManager.CaptureQueues(1).Respawns.Single()).IsSameReferenceAs(queued);
        world.SpawnManager.AddRespawn(Member(source, 979_501));
        var bounded = world.SpawnManager.CaptureQueues(1);
        await Assert.That(bounded.RespawnTotal).IsEqualTo(2);
        await Assert.That(bounded.Respawns.Length).IsEqualTo(1);
        var omitted = world.SpawnManager.CaptureQueues(2).Respawns.Except(bounded.Respawns).Cast<Npc>().Single();
        source.SpawnedNpcs[source.SpawnerId] = [omitted];
        var incomplete = Observe(world, id => id == TemplateId, queueLimit: 1);
        await Assert.That(incomplete.RespawnQueueTruncated).IsTrue();
        await Assert.That(incomplete.Sources[0].Members.Single().QueuedForRespawn).IsNull();
        await Assert.That(Observe(world, id => id == TemplateId)
            .Sources[0].Members.Single().QueuedForRespawn).IsEqualTo(true);
    }

    [Test]
    [Arguments(null, 0u)]
    [Arguments("", 0u)]
    [Arguments("-1", 0u)]
    [Arguments("all", 0u)]
    [Arguments("13", 13u)]
    public async Task ObserverFilter_RequiresAnExplicitCharacterId(string? input, uint expected)
        => await Assert.That(BotSourceDiagnostics.ParseObserverId(input)).IsEqualTo(expected);
}
