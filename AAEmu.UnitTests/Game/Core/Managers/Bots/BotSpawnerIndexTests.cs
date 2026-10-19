using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The bots' spawner index replaces whole-table copies in quest travel
/// (populated-area run 5: 260-300 ms per quest-giver search). It must see the
/// same spawners, in the same order, as the table it caches.
/// </summary>
[NotInParallel]
public class BotSpawnerIndexTests
{
    private static WorldInstance World()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor("spawner-index");
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        return world;
    }

    private static void Seed(WorldInstance world, uint templateId, Vector3 position)
    {
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_100 + spawners.Count);
        spawners[key] =
        [
            new NpcSpawner
            {
                Id = key,
                UnitId = templateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            }
        ];
    }

    [Test]
    public async Task SpawnersAddedAfterARead_AreSeenOnTheNextRead()
    {
        var world = World();
        await Assert.That(QuestBehavior.TryNpcSpawnerPosition(world, 977_001)).IsNull();

        Seed(world, 977_001, new Vector3(10, 20, 30));

        await Assert.That(QuestBehavior.TryNpcSpawnerPosition(world, 977_001)).IsEqualTo(new Vector3(10, 20, 30));
    }

    [Test]
    public async Task FirstSpawnerOfATemplate_IsTheFirstInTableOrder()
    {
        var world = World();
        Seed(world, 977_002, new Vector3(1, 1, 1));
        Seed(world, 977_002, new Vector3(2, 2, 2));

        await Assert.That(QuestBehavior.TryNpcSpawnerPosition(world, 977_002)).IsEqualTo(new Vector3(1, 1, 1));
        var entries = BotSpawnerIndex.For(world)!.ByTemplate[977_002];
        await Assert.That(entries[0].Ordinal).IsLessThan(entries[1].Ordinal);
    }

    [Test]
    public async Task NearestPreySpawner_IsRankedOverOnlyThatTemplate()
    {
        var world = World();
        Seed(world, 977_003, new Vector3(500, 0, 0));
        Seed(world, 977_004, new Vector3(5, 0, 0));
        Seed(world, 977_003, new Vector3(50, 0, 0));

        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_003, Vector3.Zero))
            .IsEqualTo(new Vector3(50, 0, 0));
    }
}
