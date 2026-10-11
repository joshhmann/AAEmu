using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Q4 — M5-B1 loot grant proof (caller-side deltas, not container counts).
/// The <see cref="GameplayActor.Loot"/> request returns Completed(granted)
/// where granted = container before-after count — INCLUDING Completed(0).
/// These tests prove the CALLER's bag + money deltas through the real engine
/// path (LootingContainer.OpenBag lootAll → TryReserveLootItem →
/// TryDistributeLootToPlayer), and tell the three contract outcomes apart:
/// caller grant vs no-op Completed(0)/Rejected vs concurrent foreign take.
/// Deterministic seeded packs mirror canonical rows (pack 4530 row 10014:
/// group 0, item 4058, drop 10000000, 1-1); pack 1616 is mirrored as a
/// mapped-but-contentless pack (canonical OQ-1: zero loots/groups/actability
/// rows, so GetPack returns null and the corpse is silently empty).
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class GameplayActorLootGrantTests
{
    private const uint GrantItemTemplateId = 91_411;
    private const uint FillerItemTemplateId = 91_412;
    private const uint PartialItemTemplateId = 91_414;
    private const uint QuestItemTemplateId = 91_413;
    private const uint BoarMeatTemplateId = 4058;
    private const uint CoinsTemplateId = 500; // == Item.Coins (static property, not const)

    private const uint SeededCorpseTemplateId = 91_401;
    private const uint GeneratedCorpseTemplateId = 91_402;
    private const uint BoarPackMirrorId = 4530;
    private const uint EmptyPackMirrorId = 91616;

    #region Caller grant

    [Test]
    public async Task Loot_Grant_ItemCallerDeltaMatchesContainerDelta()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-grant-1");
        GameplayActorTestRig.SeedItemTemplate(GrantItemTemplateId);
        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (GrantItemTemplateId, 2));

        var bagBefore = GameplayActorTestRig.BagCount(actor, GrantItemTemplateId);
        var request = actor.Loot(npcObjId);
        // NOTE: Result counts container ENTRIES removed, not item units — a
        // single stacked entry of 2 grants 2 units with Result == 1. The
        // caller bag delta (not the payload) is the grant proof.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)request.Result!).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GrantItemTemplateId) - bagBefore).IsEqualTo(2);
        await Assert.That(session.World.GetNpc(npcObjId).LootingContainer.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Loot_Grant_MoneyBypassesBag()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-grant-2");
        GameplayActorTestRig.SeedItemTemplate(CoinsTemplateId);
        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        // Count 1: template 500 is first-wins shared state (other suites
        // seed Coins with MaxCount 1), so any larger count could clamp under
        // their shape. The leg proves money-vs-bag ROUTING, not scaling.
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (CoinsTemplateId, 1));

        var moneyBefore = actor.Character.Money;
        var bagBefore = GameplayActorTestRig.BagCount(actor, CoinsTemplateId);
        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money - moneyBefore).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, CoinsTemplateId)).IsEqualTo(bagBefore);
        await Assert.That(session.World.GetNpc(npcObjId).LootingContainer.Items.Count).IsEqualTo(0);
    }

    #endregion

    #region No-op: unmapped and contentless packs

    [Test]
    public async Task Loot_Noop_UnmappedNpc_RejectedZeroDelta()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-noop-1");
        var npcObjId = session.SpawnNpc(GeneratedCorpseTemplateId);
        var npc = session.World.GetNpc(npcObjId);
        npc.CharacterTagging = new Tagging(npc);
        // OQ-4 shape: no loot_pack_dropping_npcs rows (ensure a mapping table
        // exists but carries no rows for this template).
        EnsureLootMaps();
        RemoveNpcLootMapping(GeneratedCorpseTemplateId);
        npc.LootingContainer.GenerateLoot(actor.Character);

        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        var bagBefore = GameplayActorTestRig.BagCount(actor, GrantItemTemplateId);
        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GrantItemTemplateId)).IsEqualTo(bagBefore);
    }

    [Test]
    public async Task Loot_Noop_ContentlessPack_RejectedZeroDelta()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-noop-2");
        var npcObjId = session.SpawnNpc(GeneratedCorpseTemplateId);
        var npc = session.World.GetNpc(npcObjId);
        npc.CharacterTagging = new Tagging(npc);

        // OQ-1 shape: mapping exists but the pack has no content rows, so
        // GetPack returns null and generation yields nothing (mirrors 1616).
        EnsureLootMaps();
        SeedNpcLootMapping(GeneratedCorpseTemplateId, EmptyPackMirrorId);
        RemoveLootPack(EmptyPackMirrorId);
        npc.LootingContainer.GenerateLoot(actor.Character);

        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    #endregion

    #region Generation through the real pack chain

    [Test]
    public async Task Loot_Generation_Real4530Row_YieldsBoarMeat()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-gen-1");
        GameplayActorTestRig.SeedItemTemplate(BoarMeatTemplateId);
        var npcObjId = session.SpawnNpc(GeneratedCorpseTemplateId);
        var npc = session.World.GetNpc(npcObjId);
        npc.CharacterTagging = new Tagging(npc);

        // Mirror of canonical loots row 10014 (pack 4530, group 0, item
        // 4058, drop 10000000, 1-1): group-0 dice always pass at default
        // rates, so generation is deterministic.
        SeedDeterministicPack(BoarPackMirrorId, BoarMeatTemplateId, count: 1, alwaysDrop: false);
        SeedNpcLootMapping(GeneratedCorpseTemplateId, BoarPackMirrorId);
        npc.LootingContainer.GenerateLoot(actor.Character);

        var entries = npc.LootingContainer.Items.Values.ToArray();
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Item.TemplateId).IsEqualTo(BoarMeatTemplateId);
        await Assert.That(entries[0].Item.Count).IsEqualTo(1);
    }

    [Test]
    public Task Loot_Generation_CanonicalQuestPack_Active251YieldsExactlyOne4058()
        => VerifyCanonicalBoarGeneration(hasQuest: true, worldLootRate: 1, expectedMeat: 1);

    [Test]
    public Task Loot_Generation_CanonicalQuestPack_Absent251Excludes4058()
        => VerifyCanonicalBoarGeneration(hasQuest: false, worldLootRate: 1, expectedMeat: 0);

    [Test]
    public Task Loot_Generation_CanonicalQuestPack_ZeroRateDoesNotForceQuestDrop()
        => VerifyCanonicalBoarGeneration(hasQuest: true, worldLootRate: 0, expectedMeat: 0,
            expectedOptional: 0);

    [Test]
    public Task Loot_Generation_Optional8055DoesNotRequire251()
        // The canonical 20% group chance becomes certain at multiplier 5.
        // This deterministic rate control keeps the pack's chance unchanged.
        => VerifyCanonicalBoarGeneration(hasQuest: false, worldLootRate: 5, expectedMeat: 0,
            expectedOptional: 1);

    private static async Task VerifyCanonicalBoarGeneration(bool hasQuest, double worldLootRate,
        int expectedMeat, int? expectedOptional = null)
    {
        const uint optionalPackId = 8055;
        const uint optionalItemId = 29203;
        const uint corpseTemplateId = 91_425;

        // Reuse the canonical quest acceptance fixture, not injected active
        // membership or objective credit. Loot packs below are explicit mirrors
        // of reference rows: this does not reproduce NPC death or corpse tags.
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor($"q4-canonical-{hasQuest}-{worldLootRate}");
        actor.Character.Level = 2;
        GameplayActorTestRig.SeedItemTemplate(BoarMeatTemplateId);
        GameplayActorTestRig.SeedItemTemplate(optionalItemId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        var world = AppConfiguration.Instance.World;
        var previousRate = world.LootRate;
        var meatTemplate = ItemManager.Instance.GetTemplate(BoarMeatTemplateId);
        var optionalTemplate = ItemManager.Instance.GetTemplate(optionalItemId);
        var previousMeatQuest = meatTemplate.LootQuestId;
        var previousOptionalQuest = optionalTemplate.LootQuestId;
        EnsureLootMaps();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var packs = (Dictionary<uint, LootPack>)typeof(LootGameData)
            .GetField("_lootPacks", flags)!.GetValue(LootGameData.Instance)!;
        var mappings = (Dictionary<uint, List<LootPackDroppingNpc>>)typeof(ItemManager)
            .GetField("_lootPackDroppingNpc", flags)!.GetValue(ItemManager.Instance)!;
        packs.TryGetValue(BoarPackMirrorId, out var previousMeatPack);
        packs.TryGetValue(optionalPackId, out var previousOptionalPack);
        mappings.TryGetValue(corpseTemplateId, out var previousMapping);

        try
        {
            world.LootRate = worldLootRate;
            meatTemplate.LootQuestId = 251;
            optionalTemplate.LootQuestId = 0;
            await Assert.That(actor.Character.DropRateMul).IsEqualTo(0f);
            if (hasQuest)
            {
                var accept = actor.AcceptQuest(251, QuestAcceptorType.Npc, 3512);
                await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
            }
            await Assert.That(actor.Character.Quests.HasQuest(251)).IsEqualTo(hasQuest);

            // Canonical row 10014: pack4530/group0, 4058, chance10000000,
            // amount1..1, grade0, AlwaysDrop=false. No loot-group row.
            SeedDeterministicPack(BoarPackMirrorId, BoarMeatTemplateId, count: 1, alwaysDrop: false);
            packs[BoarPackMirrorId].Loots[0].Id = 10014;
            // Canonical row73470's raw chance1 is normalized to10000000 by
            // LootGameData.Load. Its group829 retains chance2000000 (20%).
            var optional = new Loot
            {
                Id = 73470, Group = 1, ItemId = optionalItemId, DropRate = 10_000_000,
                MinAmount = 1, MaxAmount = 1, LootPackId = optionalPackId,
                GradeId = 0, AlwaysDrop = false
            };
            packs[optionalPackId] = new LootPack
            {
                Id = optionalPackId, Loots = [optional], GroupCount = 1,
                LootsByGroupNo = new Dictionary<uint, List<Loot>> { [1] = [optional] },
                Groups = new Dictionary<uint, LootGroups>
                {
                    [1] = new() { Id = 829, PackId = optionalPackId, GroupNo = 1, DropRate = 2_000_000 }
                },
                ActabilityGroups = []
            };
            SeedNpcLootMapping(corpseTemplateId, BoarPackMirrorId);
            mappings[corpseTemplateId].Add(new LootPackDroppingNpc
            {
                Id = 79421, NpcId = corpseTemplateId, LootPackId = optionalPackId, DefaultPack = false
            });
            var npc = session.World.GetNpc(session.SpawnNpc(corpseTemplateId));
            npc.CharacterTagging = new Tagging(npc);
            npc.LootingContainer.GenerateLoot(actor.Character);

            var entries = npc.LootingContainer.Items.Values.ToArray();
            var meat = entries.Where(e => e.Item.TemplateId == BoarMeatTemplateId).ToArray();
            var extras = entries.Where(e => e.Item.TemplateId == optionalItemId).ToArray();
            await Assert.That(meat.Length).IsEqualTo(expectedMeat);
            await Assert.That(meat.All(e => e.Item.Count == 1)).IsTrue();
            await Assert.That(extras.Length <= 1 && extras.All(e => e.Item.Count == 1)).IsTrue();
            if (expectedOptional.HasValue)
                await Assert.That(extras.Length).IsEqualTo(expectedOptional.Value);
            await Assert.That(entries.Length).IsEqualTo(meat.Length + extras.Length);
            // Generation fills the corpse; it does not grant or credit items.
            await Assert.That(GameplayActorTestRig.BagCount(actor, BoarMeatTemplateId)).IsEqualTo(0);
        }
        finally
        {
            world.LootRate = previousRate;
            meatTemplate.LootQuestId = previousMeatQuest;
            optionalTemplate.LootQuestId = previousOptionalQuest;
            if (previousMeatPack == null) packs.Remove(BoarPackMirrorId);
            else packs[BoarPackMirrorId] = previousMeatPack;
            if (previousOptionalPack == null) packs.Remove(optionalPackId);
            else packs[optionalPackId] = previousOptionalPack;
            if (previousMapping == null) mappings.Remove(corpseTemplateId);
            else mappings[corpseTemplateId] = previousMapping;
        }
    }

    #endregion

    #region Foreign take and retry

    [Test]
    public async Task Loot_ForeignTake_VictimRejectedGranteeKeepsDelta()
    {
        var (victim, session) = GameplayActorTestRig.CreateActor("q4-foreign-victim");
        var (grantee, _) = GameplayActorTestRig.CreateActor("q4-foreign-grantee");
        GameplayActorTestRig.JoinActorWorld(session, grantee);
        GameplayActorTestRig.SeedItemTemplate(GrantItemTemplateId);
        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (GrantItemTemplateId, 2));

        var take = grantee.Loot(npcObjId);
        await Assert.That(take.State).IsEqualTo(ActorLifecycleState.Completed);

        var victimBagBefore = GameplayActorTestRig.BagCount(victim, GrantItemTemplateId);
        var retry = victim.Loot(npcObjId);

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(victim, GrantItemTemplateId)).IsEqualTo(victimBagBefore);
        await Assert.That(GameplayActorTestRig.BagCount(grantee, GrantItemTemplateId)).IsEqualTo(2);
        await Assert.That(session.World.GetNpc(npcObjId).LootingContainer.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Loot_RetryAfterSuccess_NoDoubleGrant()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-retry-1");
        GameplayActorTestRig.SeedItemTemplate(GrantItemTemplateId);
        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (GrantItemTemplateId, 2));

        var first = actor.Loot(npcObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)first.Result!).IsEqualTo(1);

        var retry = actor.Loot(npcObjId);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GrantItemTemplateId)).IsEqualTo(2);
    }

    #endregion

    #region Full bag, partial grant, quest gate

    [Test]
    public async Task Loot_FullBag_CompletedZeroContainerIntact()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-fullbag-1");
        GameplayActorTestRig.SeedItemTemplate(GrantItemTemplateId);
        GameplayActorTestRig.SeedItemTemplate(FillerItemTemplateId);
        SetTemplateMaxCount(FillerItemTemplateId, 1);
        FillBagToFull(actor);

        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        var container = session.World.GetNpc(npcObjId).LootingContainer;
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (GrantItemTemplateId, 1));

        var bagBefore = GameplayActorTestRig.BagCount(actor, GrantItemTemplateId);
        var request = actor.Loot(npcObjId);

        // Full bag is completion-with-zero (restore path), not rejection —
        // and the entry must survive (conservation, not destruction).
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)request.Result!).IsEqualTo(0);
        await Assert.That(request.Detail!.Contains("nothing to loot")).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, GrantItemTemplateId)).IsEqualTo(bagBefore);
        await Assert.That(container.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Loot_PartialGrant_ConservesRemainder()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-partial-1");
        GameplayActorTestRig.SeedItemTemplate(PartialItemTemplateId);
        GameplayActorTestRig.SeedItemTemplate(FillerItemTemplateId);
        // MaxCount 1 (own template id, no cross-test talk): the two entries
        // cannot stack, so the second needs its own slot.
        SetTemplateMaxCount(PartialItemTemplateId, 1);
        SetTemplateMaxCount(FillerItemTemplateId, 1);
        FillBagToFreeSlots(actor, freeSlots: 1);

        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        var container = session.World.GetNpc(npcObjId).LootingContainer;
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId),
            (PartialItemTemplateId, 1), (PartialItemTemplateId, 1));

        var first = actor.Loot(npcObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)first.Result!).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, PartialItemTemplateId)).IsEqualTo(1);
        await Assert.That(container.Items.Count).IsEqualTo(1);

        // Free one slot, then the remainder grants exactly once.
        var freed = actor.Character.Inventory.Bag.ConsumeItem(ItemTaskType.Loot, FillerItemTemplateId, 1, null!);
        await Assert.That(freed).IsEqualTo(1);
        var second = actor.Loot(npcObjId);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)second.Result!).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, PartialItemTemplateId)).IsEqualTo(2);
        await Assert.That(container.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Loot_QuestGatedItem_CompletedZeroContainerIntact()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("q4-quest-1");
        GameplayActorTestRig.SeedItemTemplate(QuestItemTemplateId);
        SetTemplateLootQuest(QuestItemTemplateId, 999);
        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        var container = session.World.GetNpc(npcObjId).LootingContainer;
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (QuestItemTemplateId, 1));

        // No quest 999 on the killer: eligibility gate refuses the take, the
        // entry stays, the caller delta is zero (mirrors live 4058/quest-251
        // until the hunt leg runs with an eligible killer).
        var request = actor.Loot(npcObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)request.Result!).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actor, QuestItemTemplateId)).IsEqualTo(0);
        await Assert.That(container.Items.Count).IsEqualTo(1);
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task Loot_ConcurrentTake_SingleWinnerNoDupe()
    {
        var (first, session) = GameplayActorTestRig.CreateActor("q4-race-a");
        var (second, _) = GameplayActorTestRig.CreateActor("q4-race-b");
        GameplayActorTestRig.SeedItemTemplate(GrantItemTemplateId);
        var npcObjId = session.SpawnNpc(SeededCorpseTemplateId);
        var container = session.World.GetNpc(npcObjId).LootingContainer;
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId), (GrantItemTemplateId, 1));

        // Both racers hammer the same single entry: TryReserveLootItem is
        // atomic under ItemsLock, so exactly one take may succeed and the
        // combined caller delta must equal the single generated item.
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task<bool>[16];
        for (var i = 0; i < tasks.Length; i++)
        {
            var player = i % 2 == 0 ? first.Character : second.Character;
            tasks[i] = Task.Run(() =>
            {
                gate.Wait();
                return container.TryTakeLoot(player, 0, null!, true);
            });
        }
        gate.Set();
        await Task.WhenAll(tasks);

        await Assert.That(tasks.Count(t => t.Result)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(first, GrantItemTemplateId)
            + GameplayActorTestRig.BagCount(second, GrantItemTemplateId)).IsEqualTo(1);
        await Assert.That(container.Items.Count).IsEqualTo(0);
    }

    #endregion

    #region Seed helpers (LootGenerateRaceTests reflection pattern)

    private static void SeedDeterministicPack(uint packId, uint itemId, int count, bool alwaysDrop)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (field.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            field.SetValue(LootGameData.Instance, packs);
        }

        var loot = new Loot
        {
            Id = packId,
            Group = 0,
            ItemId = itemId,
            DropRate = 10_000_000,
            MinAmount = count,
            MaxAmount = count,
            LootPackId = packId,
            GradeId = 0,
            AlwaysDrop = alwaysDrop
        };
        packs[packId] = new LootPack
        {
            Id = packId,
            Loots = [loot],
            LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
            Groups = [],
            ActabilityGroups = [],
            GroupCount = 1
        };
    }

    private static void RemoveLootPack(uint packId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (field.GetValue(LootGameData.Instance) is Dictionary<uint, LootPack> packs)
            packs.Remove(packId);
    }

    private static void SeedNpcLootMapping(uint npcTemplateId, uint packId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (field.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            field.SetValue(ItemManager.Instance, map);
        }
        map[npcTemplateId] =
        [
            new LootPackDroppingNpc
            {
                Id = npcTemplateId,
                NpcId = npcTemplateId,
                LootPackId = packId,
                DefaultPack = true
            }
        ];
    }
    private static void EnsureLootMaps()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack>)
            packField.SetValue(LootGameData.Instance, new Dictionary<uint, LootPack>());
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>>)
            mapField.SetValue(ItemManager.Instance, new Dictionary<uint, List<LootPackDroppingNpc>>());
    }

    private static void RemoveNpcLootMapping(uint npcTemplateId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (field.GetValue(ItemManager.Instance) is Dictionary<uint, List<LootPackDroppingNpc>> map)
            map.Remove(npcTemplateId);
    }

    private static void SetTemplateMaxCount(uint templateId, int maxCount)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("_templates", flags)!;
        var templates = (Dictionary<uint, ItemTemplate>)field.GetValue(ItemManager.Instance)!;
        templates[templateId].MaxCount = maxCount;
    }

    private static void SetTemplateLootQuest(uint templateId, uint questId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("_templates", flags)!;
        var templates = (Dictionary<uint, ItemTemplate>)field.GetValue(ItemManager.Instance)!;
        templates[templateId].LootQuestId = questId;
    }

    private static void FillBagToFreeSlots(GameplayActor actor, int freeSlots)
    {
        var bag = actor.Character.Inventory.Bag;
        var guard = 0;
        while (bag.FreeSlotCount > freeSlots && guard++ < 1000)
            GameplayActorTestRig.GrantItem(actor, FillerItemTemplateId, 1);
    }

    private static void FillBagToFull(GameplayActor actor)
        => FillBagToFreeSlots(actor, freeSlots: 0);

    #endregion
}
