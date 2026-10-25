using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.GameData.Framework;
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

    private static NpcSpawner Seed(WorldInstance world, uint templateId, Vector3 position)
    {
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_100 + spawners.Count);
        spawners[key] =
        [
            new NpcSpawner
            {
                Id = key, SpawnerId = key,
                UnitId = templateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            }
        ];
        return spawners[key][0];
    }

    [Test]
    public async Task CorpseOnlySource_IsSkippedAndReconsideredWithoutRebuildingIndex()
    {
        var world = World();
        var near = Seed(world, 977_005, new Vector3(10, 0, 0));
        Seed(world, 977_005, new Vector3(50, 0, 0));
        var snapshot = BotSpawnerIndex.For(world);
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_005, Vector3.Zero))
            .IsEqualTo(new Vector3(10, 0, 0));
        var corpse = new Npc { TemplateId = 977_005, Template = new NpcTemplate { Id = 977_005 }, Hp = 0 };
        near.SpawnedNpcs[near.SpawnerId] = [corpse];
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_005, Vector3.Zero))
            .IsEqualTo(new Vector3(50, 0, 0));
        near.SpawnedNpcs[near.SpawnerId].Add(new Npc { TemplateId = 977_005, Template = new NpcTemplate { Id = 977_005 }, Hp = 100 });
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_005, Vector3.Zero))
            .IsEqualTo(new Vector3(10, 0, 0));
        near.SpawnedNpcs[near.SpawnerId].Clear();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_005, Vector3.Zero))
            .IsEqualTo(new Vector3(10, 0, 0));
        near.SpawnedNpcs[near.SpawnerId + 1] = [corpse];
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_005, Vector3.Zero))
            .IsEqualTo(new Vector3(10, 0, 0));
        await Assert.That(BotSpawnerIndex.For(world)).IsSameReferenceAs(snapshot);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NativeInactiveVariants_DoNotReplaceACorpseBlockedSource(bool livingInactiveMember)
    {
        using var definitions = new NpcSpawnerSearchTestRig();
        var world = World();
        var near = definitions.Add(world, new Vector3(10, 0, 0));
        definitions.Add(world, new Vector3(50, 0, 0));
        await Assert.That(near.Count).IsEqualTo(3);
        var active = near.Single(s => s.SpawnerId == 3348);
        var inactive = near.Single(s => s.SpawnerId == 3713);
        await Assert.That(active.IsOptimalSpawner()).IsTrue();
        await Assert.That(inactive.IsOptimalSpawner()).IsFalse();
        active.SpawnedNpcs[active.SpawnerId] = [new Npc { TemplateId = 3451, Hp = 0 }];
        if (livingInactiveMember)
            inactive.SpawnedNpcs[inactive.SpawnerId] = [new Npc { TemplateId = 3451, Hp = 100 }];
        var snapshot = BotSpawnerIndex.For(world);
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 3451, Vector3.Zero))
            .IsEqualTo(new Vector3(livingInactiveMember ? 10 : 50, 0, 0));
        // Removing the corpse makes the native active source inspectable again.
        active.SpawnedNpcs[active.SpawnerId].Clear();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 3451, Vector3.Zero))
            .IsEqualTo(new Vector3(10, 0, 0));
        await Assert.That(BotSpawnerIndex.For(world)).IsSameReferenceAs(snapshot);
    }

    [Test]
    public async Task SingleNormalDefinition_UsesNativeSelectionRatherThanACategoryRule()
    {
        var world = World();
        var source = Seed(world, 977_006, new Vector3(10, 0, 0));
        source.Template = new NpcSpawnerTemplate { Id = source.SpawnerId, NpcSpawnerCategoryId = NpcSpawnerCategory.Normal };
        source.NpcSpawnerIds = [source.SpawnerId];
        await Assert.That(source.IsOptimalSpawner()).IsTrue();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 977_006, Vector3.Zero))
            .IsEqualTo(new Vector3(10, 0, 0));
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

/// <summary>
/// Canonical wasp definition subset, supplied before the behavior runs:
/// 3451 maps to 3348 (Autocreated), 3713 and 3733 (Normal), with no schedules.
/// Uses native AddNpcSpawner cloning/index registration. Corpses/living members
/// in callers are explicit state inputs, never earned kill/spawn evidence.
/// </summary>
internal sealed class NpcSpawnerSearchTestRig : IDisposable
{
    private readonly object? _previousNpcs;
    private readonly object? _previousSchedules;
    private static FieldInfo NpcsField => typeof(Singleton<NpcGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static FieldInfo SchedulesField => typeof(Singleton<GameScheduleManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;

    public NpcSpawnerSearchTestRig()
    {
        _previousNpcs = NpcsField.GetValue(null);
        _previousSchedules = SchedulesField.GetValue(null);
        var data = new NpcGameData();
        NpcsField.SetValue(null, data);
        var schedules = new GameScheduleManager(Mock.Of<IGameDataManager>().Object);
        typeof(GameScheduleManager).GetField("_gameScheduleSpawnerIds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(schedules, new Dictionary<int, List<int>>());
        SchedulesField.SetValue(null, schedules);
        foreach (var (id, memberId, category, max) in new[] {
            (3348u, 3350u, NpcSpawnerCategory.Autocreated, 1u),
            (3713u, 3785u, NpcSpawnerCategory.Normal, 20u),
            (3733u, 4262u, NpcSpawnerCategory.Normal, 10u) })
        {
            var member = new NpcSpawnerNpc { Id = memberId, NpcSpawnerTemplateId = id, MemberId = 3451,
                MemberType = "Npc", Weight = 1f };
            data.AddNpcSpawner(new NpcSpawnerTemplate { Id = id, NpcSpawnerCategoryId = category,
                MaxPopulation = max, Npcs = [member], SpawnDelayMin = 53, SpawnDelayMax = 53, ActivationState = true });
            data.AddMemberAndSpawnerTemplateIds(member);
        }
    }

    public List<NpcSpawner> Add(WorldInstance world, Vector3 position)
    {
        world.SpawnManager.AddNpcSpawner(new NpcSpawner { UnitId = 3451,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z } });
        return world.SpawnManager.GetAllSpawners().Values.SelectMany(s => s)
            .Where(s => s.Position.X == position.X && s.Position.Y == position.Y && s.Position.Z == position.Z).ToList();
    }

    public void Dispose()
    {
        SchedulesField.SetValue(null, _previousSchedules);
        NpcsField.SetValue(null, _previousNpcs);
    }
}
