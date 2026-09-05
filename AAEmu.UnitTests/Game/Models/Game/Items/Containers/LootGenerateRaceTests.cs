using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.Items.Containers;

/// <summary>
/// Regression for the loot double-generation race (bot-wildlife crash
/// cluster): concurrent killing blows for the same NPC reached
/// <see cref="LootingContainer.GenerateLoot"/> from bot skill-plot threads,
/// and the pre-fix check-then-set guard was not atomic — both generations
/// ran Items.Clear + RegisterItems and the corpse ended up with duplicated
/// loot. The guard is now atomic under ItemsLock: hammering it from 16
/// threads must always leave exactly one generation behind.
/// </summary>
[NotInParallel]
public class LootGenerateRaceTests
{
    private const uint CorpseTemplateId = 91_101;
    private const uint KillerTemplateId = 91_102;
    private const uint RaceLootPackId = 91_501;
    private const uint RaceLootItemId = 91_503;

    [Test]
    public async Task GenerateLoot_ConcurrentKillingBlows_GeneratesOnce()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();

        var (_, session) = GameplayActorTestRig.CreateActor("loot-race");
        GameplayActorTestRig.SeedItemTemplate(RaceLootItemId);
        SeedDeterministicLootPack();
        SeedNpcLootMapping();

        var corpse = session.World.GetNpc(session.SpawnNpc(CorpseTemplateId));
        corpse.CharacterTagging = new Tagging(corpse);
        // NPC killer: no tagger/tag-team and not a Character, so generation
        // takes the empty-EligiblePlayers path (no SendPacket surface) and
        // the deterministic pack yields exactly one item per generation.
        var killer = session.World.GetNpc(session.SpawnNpc(KillerTemplateId));
        killer.CharacterTagging = new Tagging(killer);

        var container = corpse.LootingContainer;
        var generatedFlag = typeof(LootingContainer).GetProperty("AlreadyGenerated", BindingFlags.NonPublic | BindingFlags.Instance)!;

        for (var trial = 0; trial < 40; trial++)
        {
            generatedFlag.SetValue(container, false);
            container.Items.Clear();

            using var gate = new ManualResetEventSlim(false);
            var tasks = new Task[16];
            for (var i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Run(() =>
                {
                    gate.Wait();
                    container.GenerateLoot(killer);
                });
            gate.Set();
            await Task.WhenAll(tasks);

            await Assert.That(container.Items.Count).IsEqualTo(1);
        }
    }

    /// <summary>
    /// Seeds a pack that yields exactly one item per generation: a single
    /// AlwaysDrop row (groupNo 0 skips the group dice) with MinAmount ==
    /// MaxAmount, so a second generation is observable as a second entry.
    /// </summary>
    private static void SeedDeterministicLootPack()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (field.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            field.SetValue(LootGameData.Instance, packs);
        }

        var loot = new Loot
        {
            Id = 1,
            Group = 7,
            ItemId = RaceLootItemId,
            DropRate = 10_000_000,
            MinAmount = 1,
            MaxAmount = 1,
            LootPackId = RaceLootPackId,
            GradeId = 0,
            AlwaysDrop = true
        };
        packs[RaceLootPackId] = new LootPack
        {
            Id = RaceLootPackId,
            Loots = [loot],
            LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
            Groups = [],
            ActabilityGroups = [],
            GroupCount = 1
        };
    }

    private static void SeedNpcLootMapping()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (field.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            field.SetValue(ItemManager.Instance, map);
        }
        map[CorpseTemplateId] =
        [
            new LootPackDroppingNpc
            {
                Id = 1,
                NpcId = CorpseTemplateId,
                LootPackId = RaceLootPackId,
                DefaultPack = true
            }
        ];
    }
}
