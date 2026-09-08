using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 Crafter v1 slice-2 (ROADMAP M8 living-village contracts) — merchant
/// buy → stage → (slice-1 withdraw → craft → store) through
/// <see cref="CrafterMerchantCycle"/>: every recipe row bought from a merchant
/// through the real <see cref="GameplayActor.Buy"/> path (CSBuyItemsPacket
/// branch), staged into the bank through the real
/// <see cref="GameplayActor.DepositItem"/> path, then the composed slice-1
/// run, ending in exact currency conservation (money delta == sum of prices).
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (phantom currency on a mischarged buy, swallowed price/range rejection,
/// consumed inputs on a refused craft, duplicate charge on same-key retry)
/// and PASSES on the composed real paths. Raw engine rejections (unknown
/// merchant, out-of-range, not-sold, short funds) are already pinned by
/// GameplayActorM51BuySellTests — these tests pin the COMPOSER's fail-closed
/// order and conservation, not the engine gates.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class CrafterMerchantCycleTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Fixture recipe: 2 × matA (99_043 @ 30) + 1 × matB (99_044 @ 50) → 1 ×
    // fixture product (99_045, plain bag-grant item) at the rig bench, skill
    // with labor 10 (the slice-1 SeedFixtureRecipe shape, own ids).
    private const uint CraftId = 99_041;
    private const uint CraftSkillId = 99_042;
    private const int CraftLaborCost = 10;
    private const uint MatAItemId = 99_043;
    private const uint MatBItemId = 99_044;
    private const uint ProductItemId = 99_045;
    private const int MatAAmount = 2;
    private const int MatBAmount = 1;
    private const int MatAPrice = 30;
    private const int MatBPrice = 50;
    private const long ExpectedBuyTotal = MatAAmount * MatAPrice + MatBAmount * MatBPrice; // 110
    private const long SeedMoney = 10_000;

    private static uint s_nextWorldId = 0x6300_0000; // fresh base: 0x6000 loadpack / 0x6001 housing-race / 0x6002 harvest / 0x6100 hauler / 0x6200 crafter slice-1 / 0x8000 farmer

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedCraftSurface();
        GameplayActorTestRig.SeedTradeSurface();
        SeedFixtureRecipe();
        GameplayActorTestRig.SeedTradeItemTemplate(MatAItemId, MatAPrice, 0, false);
        GameplayActorTestRig.SeedTradeItemTemplate(MatBItemId, MatBPrice, 0, false);
        GameplayActorTestRig.RegisterPlainItemTemplate(ProductItemId);
        GameplayActorTestRig.SeedMerchantPack(MatAItemId, MatBItemId);
        SeedEquipSurface();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        if (_registeredWorld != null)
        {
            var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(WorldManager.Instance)!;
            if (worlds.TryGetValue(_registeredWorld.Id, out var registered) && ReferenceEquals(registered, _registeredWorld))
                worlds.TryRemove(_registeredWorld.Id, out _);
            _registeredWorld = null;
        }
    }

    [Test]
    public async Task BuyCraftStore_HappyPath_ExactCurrencyMaterialProductLaborDeltas()
    {
        var (actor, _, benchObjId, merchantObjId) = CreateRig("m8cm-t1-happy", SeedMoney);

        var result = CrafterMerchantCycle.Run(actor, Options("m8cm-t1", benchObjId, merchantObjId), new CrafterPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "buy-completed", "buy-price-exact", "stage-completed",
                     "withdraw-completed", "craft-completed",
                     "craft-materials-conserved", "craft-product-granted", "craft-labor-conserved",
                     "store-completed", "store-conservation",
                     "currency-conserved", "no-phantom-inputs"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Currency: exactly the template price sum charged, no phantom copper.
        await Assert.That(result.BuyTotal).IsEqualTo(ExpectedBuyTotal);
        await Assert.That(result.MoneySpent).IsEqualTo(ExpectedBuyTotal);
        await Assert.That(actor.Character.Money).IsEqualTo(SeedMoney - ExpectedBuyTotal);
        await Assert.That(result.Report!.MoneyBefore - result.Report.MoneyAfter).IsEqualTo(ExpectedBuyTotal);

        // Materials: exactly the recipe rows consumed, nothing lingering.
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MatBItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MatBItemId)).IsEqualTo(0);
        await Assert.That(result.MaterialsConsumed[MatAItemId]).IsEqualTo(MatAAmount);
        await Assert.That(result.MaterialsConsumed[MatBItemId]).IsEqualTo(MatBAmount);

        // Labor: exactly the recipe cost.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
        await Assert.That(result.LaborCharged).IsEqualTo(CraftLaborCost);

        // Product: out of the bag, into the bank — exactly once.
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(1);
        await Assert.That(result.Report.ProductStored[ProductItemId]).IsEqualTo(1);
        await Assert.That(result.Report.Holds).IsEmpty();
    }

    [Test]
    public async Task Buy_InsufficientFunds_FailsClosed_NothingBoughtNothingCrafted()
    {
        // First row alone costs 60; 59 cannot cover it — the FIRST buy leg
        // must refuse before anything moves.
        var (actor, _, benchObjId, merchantObjId) = CreateRig("m8cm-t1-poor", 59);

        var result = CrafterMerchantCycle.Run(actor, Options("m8cm-t1-poor", benchObjId, merchantObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("BUY");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Nothing bought (no rows in the bag), no copper moved, labor
        // unspent, no product anywhere.
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MatBItemId)).IsEqualTo(0);
        await Assert.That(actor.Character.Money).IsEqualTo(59);
        await Assert.That(result.MoneySpent).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(result.Report!.Holds.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Buy_MerchantDoesNotSell_HoldsWithReason_NothingCrafted()
    {
        var (actor, session, benchObjId, _) = CreateRig("m8cm-t1-nosell", SeedMoney);
        // A merchant whose pack sells nothing relevant (unregistered pack id
        // → GetGoods null → the "does not sell" gate).
        var bareMerchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1102, packId: 77_001);
        GameplayActorTestRig.SetNpcPosition(session, bareMerchant, TestPosition);

        var result = CrafterMerchantCycle.Run(actor, Options("m8cm-t1-nosell", benchObjId, bareMerchant), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("BUY");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(result.FailReason.Contains("does not sell")).IsTrue();
        // Held with reason: no copper moved, nothing crafted, labor unspent.
        await Assert.That(actor.Character.Money).IsEqualTo(SeedMoney);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(result.Report!.Holds.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Buy_MerchantOutOfRange_HoldsWithReason_NothingCrafted()
    {
        var (actor, session, benchObjId, merchantObjId) = CreateRig("m8cm-t1-far", SeedMoney);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition + new Vector3(50f, 0f, 0f));

        var result = CrafterMerchantCycle.Run(actor, Options("m8cm-t1-far", benchObjId, merchantObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("BUY");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(result.FailReason.Contains("out of shop range")).IsTrue();
        // Held with reason: no copper moved, nothing crafted, labor unspent.
        await Assert.That(actor.Character.Money).IsEqualTo(SeedMoney);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(result.Report!.Holds.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Cycle_SameKeyRetry_NeverDuplicatesChargeOrOutput()
    {
        var (actor, _, benchObjId, merchantObjId) = CreateRig("m8cm-t1-retry", SeedMoney);

        var first = CrafterMerchantCycle.Run(actor, Options("m8cm-t1-retry", benchObjId, merchantObjId), new CrafterPump());
        await Assert.That(first.Passed).IsTrue();

        // Rerun under the SAME cycle id: the FIRST keyed leg (buy) must
        // refuse the duplicate key pre-flight (TryBegin StateTransition) —
        // no second charge, the craft leg is never re-entered, nothing is
        // duplicated.
        var retry = CrafterMerchantCycle.Run(actor, Options("m8cm-t1-retry", benchObjId, merchantObjId), new CrafterPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("BUY");
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        // Exactly one charge and one product in the world (first run's);
        // labor charged once; no material rows anywhere.
        await Assert.That(actor.Character.Money).IsEqualTo(SeedMoney - ExpectedBuyTotal);
        await Assert.That(retry.MoneySpent).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MatBItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MatBItemId)).IsEqualTo(0);
    }

    // ------------------------------------------------------------ rig below

    private (GameplayActor actor, HeadlessSession session, uint benchObjId, uint merchantObjId) CreateRig(string name, long money)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, money);
        actor.Character.LaborPower = 100;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1101);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        return (actor, session, benchObjId, merchantObjId);
    }

    private CrafterMerchantCycle.CrafterMerchantOptions Options(string cycle, uint benchObjId, uint merchantObjId)
        => new()
        {
            CycleId = cycle,
            CraftId = CraftId,
            BenchObjId = benchObjId,
            MerchantNpcObjId = merchantObjId
        };

    // Bag-scoped count (TestRig.BagCount counts ALL containers — including
    // the bank, where the stored product lands — so bag asserts here use
    // the explicit engine scope the composer itself uses).
    private static int BagOnlyCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Inventory, templateId);

    private static int BankCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

    private void RegisterWorld(HeadlessSession session)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(WorldInstance).GetField("<Id>k__BackingField", Flags)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", Flags)!
            .GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorld = session.World;
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>
    /// Seeds the fixture recipe: 2 × matA + 1 × matB → 1 × fixture product
    /// at the rig bench (missing-only, the slice-1 pattern, own ids).
    /// </summary>
    private static void SeedFixtureRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(CraftId))
        {
            crafts[CraftId] = new Craft
            {
                Id = CraftId,
                SkillId = CraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = MatAItemId, Amount = MatAAmount },
                    new CraftMaterial { ItemId = MatBItemId, Amount = MatBAmount }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = ProductItemId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(CraftSkillId))
        {
            skills[CraftSkillId] = new SkillTemplate
            {
                Id = CraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = CraftLaborCost,
                ActabilityGroupId = 0,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }
    }

    private static void SeedEquipSurface()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var skillManager = SkillManager.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(skillManager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(skillManager, Activator.CreateInstance(dictType));
            }
        }

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        var itemGameData = ItemGameData.Instance;
        if (typeof(ItemGameData).GetField("_itemGradeBuffs", flags)?.GetValue(itemGameData) == null)
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!.SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
    }

    /// <summary>
    /// Headless craft pump: ticks the actor and applies the REAL CraftEffect
    /// once the engine queue is active (the slice-1 CrafterPump shape).
    /// </summary>
    private sealed class CrafterPump : ICrafterPump
    {
        public ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            var applied = false;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(20));
                if (!applied && actor.Character.Craft is { IsCraftQueueActive: true })
                {
                    var bench = actor.Character.ParentWorld?.GetDoodad(benchObjId);
                    var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                    effect.Apply(actor.Character, null, bench, null,
                        new CastSkill(skillId, 0), new EffectSource(), null, DateTime.UtcNow);
                    applied = true;
                }
            }

            return request;
        }
    }
}
