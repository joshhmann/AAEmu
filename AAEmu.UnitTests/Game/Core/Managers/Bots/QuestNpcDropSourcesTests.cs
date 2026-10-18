using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>Static, cached NPC source facts for one supported item-gather objective.</summary>
[NotInParallel]
public class QuestNpcDropSourcesTests
{
    [Test]
    public async Task Quest260_CachesEveryCanonicalSource_OverridesCannotLeakOldSet()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        PlayerbotPilotRig.SeedPilotSingletons(); // Real quest 260 objective template.
        GameplayActorTestRig.Seed();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        var oldMap = mapField.GetValue(ItemManager.Instance);
        var oldPacks = packField.GetValue(LootGameData.Instance);
        // Exact canonical reference rows, staged in intentionally scrambled
        // enumeration order; no live NPC, quest credit or item is supplied.
        var rows = new (uint Npc, uint Pack, uint Mapping, uint Loot)[]
        {
            (7675, 5745, 17960, 18067),
            (3497, 5746, 17961, 18068),
            (3484, 4533, 2096, 5807),
        };
        var map = new Dictionary<uint, List<LootPackDroppingNpc>>();
        var packs = new Dictionary<uint, LootPack>();
        foreach (var row in rows)
        {
            map[row.Npc] = [new LootPackDroppingNpc
                { Id = row.Mapping, NpcId = row.Npc, LootPackId = row.Pack, DefaultPack = true }];
            var item = new Loot { Id = row.Loot, Group = 0, ItemId = 8128,
                DropRate = 10_000_000, MinAmount = 1, MaxAmount = 1,
                LootPackId = row.Pack, GradeId = 0, AlwaysDrop = false };
            packs[row.Pack] = new LootPack { Id = row.Pack, Loots = [item],
                LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [item] },
                Groups = [], ActabilityGroups = [], GroupCount = 1 };
        }
        mapField.SetValue(ItemManager.Instance, map);
        packField.SetValue(LootGameData.Instance, packs);
        QuestFixtureRow.Invalidate(260);
        try
        {
            var derived = QuestFixtureRow.FromQuestData(260);
            var expected = new[]
            {
                new QuestFixtureRow.NpcDropSource(3484, 4533),
                new QuestFixtureRow.NpcDropSource(3497, 5746),
                new QuestFixtureRow.NpcDropSource(7675, 5745),
            };
            await Assert.That(derived.GatherActId).IsEqualTo(6902u);
            await Assert.That(derived.PreyItem).IsEqualTo(8128u);
            await Assert.That(derived.PreyTemplate).IsEqualTo(3484u);
            await Assert.That(derived.PreyPack).IsEqualTo(4533u);
            await Assert.That(derived.GatherNpcSources.SequenceEqual(expected)).IsTrue();
            await Assert.That(QuestFixtureRow.FromQuestData(260)).IsSameReferenceAs(derived);
            await Assert.That(derived.GatherNpcSources).IsSameReferenceAs(derived.GatherNpcSources);

            var overridden = derived with { PreyTemplate = 3497, PreyPack = 5746 };
            await Assert.That(overridden.GatherNpcSources.SequenceEqual(
                [new QuestFixtureRow.NpcDropSource(3497, 5746)])).IsTrue();
            var itemChanged = derived with { PreyItem = 4058 };
            await Assert.That(itemChanged.GatherNpcSources.Count).IsEqualTo(0);
            var handBuilt = new QuestFixtureRow(260, 6902, 8128, 3, 3497, 5746, 0, 0);
            await Assert.That(handBuilt.GatherNpcSources.SequenceEqual(
                [new QuestFixtureRow.NpcDropSource(3497, 5746)])).IsTrue();
            await Assert.That((handBuilt with { PreyPack = 0 }).GatherNpcSources.Count).IsEqualTo(0);

            map.Remove(3497);
            await Assert.That(QuestFixtureRow.FromQuestData(260)).IsSameReferenceAs(derived);
            await Assert.That(derived.GatherNpcSources.SequenceEqual(expected)).IsTrue();
            QuestFixtureRow.Invalidate(260);
            var refreshed = QuestFixtureRow.FromQuestData(260);
            await Assert.That(refreshed).IsNotSameReferenceAs(derived);
            await Assert.That(refreshed.GatherNpcSources.SequenceEqual(
                [expected[0], expected[2]])).IsTrue();
        }
        finally
        {
            mapField.SetValue(ItemManager.Instance, oldMap);
            packField.SetValue(LootGameData.Instance, oldPacks);
            QuestFixtureRow.Invalidate(260);
        }
    }
}
