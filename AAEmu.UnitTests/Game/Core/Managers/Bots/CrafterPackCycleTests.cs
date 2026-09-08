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
/// M8 Crafter v1 slice-3 (ROADMAP M8 living-village contracts) — trade-pack
/// production through <see cref="CrafterWorkstationCycle"/>: withdraw recipe
/// materials from the bank through the real
/// <see cref="GameplayActor.WithdrawItem"/> path, craft a pack recipe through
/// the real CharacterCraft → CraftEffect chain (auto-equip into the Backpack
/// slot via TryEquipNewBackPack, NOT the bag), and hold the pack equipped for
/// hauler pickup (no bank store leg — DepositItem only moves bag stacks).
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (phantom pack on missing inputs, bag-granted pack, craft over an occupied
/// slot, duplicate pack on same-key retry) and PASSES on the composed real
/// paths. The non-pack bag → bank path is re-pinned here so the pack branch
/// cannot regress slice-1.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class CrafterPackCycleTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);
    // Fixture PACK recipe: 2 × packMat (99_053) → 1 × fixture cargo pack
    // (CargoPackTemplateId, BackpackTemplate + TradePack → ResultsInBackpack)
    // at the rig bench, skill with labor 10.
    // Id range 99_051+: 99_041-99_045 belong to CrafterMerchantCycleTests and
    // 99_043/99_044 double as HaulerVendorMultipackCycleTests craft ids —
    // missing-only seeding is first-wins, so ranges must not overlap.
    private const uint PackCraftId = 99_051;
    private const uint PackCraftSkillId = 99_052;
    private const int PackCraftLaborCost = 10;
    private const uint PackMatItemId = 99_053;
    private const int PackMatAmount = 2;
    private static uint PackProductId => GameplayActorTestRig.CargoPackTemplateId;

    // Fixture NON-PACK recipe (slice-1 shape, own ids): 1 × plainMat
    // (99_048) → 1 × plain product (99_050, bag grant) at the rig bench.
    private const uint PlainCraftId = 99_046;
    private const uint PlainCraftSkillId = 99_047;
    private const int PlainCraftLaborCost = 10;
    private const uint PlainMatItemId = 99_048;
    private const uint PlainProductItemId = 99_050;

    private static uint s_nextWorldId = 0x6300_0000; // fresh base: 0x6000 loadpack / 0x6001 housing-race / 0x6002 harvest / 0x6100 hauler / 0x6200 crafter-ws

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.SeedCargoPackSurface();
        GameplayActorTestRig.SeedCraftSurface();
        SeedFixturePackRecipe();
        SeedFixturePlainRecipe();
        GameplayActorTestRig.SeedItemTemplate(PackMatItemId);
        GameplayActorTestRig.SeedItemTemplate(PlainMatItemId);
        GameplayActorTestRig.SeedItemTemplate(PlainProductItemId);
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
    public async Task PackProduction_HappyPath_AutoEquipsIntoBackpackSlot()
    {
        var (actor, _, benchObjId) = CreateRig("m8cp-t1-happy");
        StockBank(actor, PackMatItemId, PackMatAmount);

        var result = CrafterWorkstationCycle.Run(actor, PackOptions("m8cp-t1", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "withdraw-completed", "craft-completed",
                     "craft-materials-conserved", "craft-product-granted", "craft-labor-conserved",
                     "pack-auto-equip", "pack-handoff-conserved"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Pack: auto-equipped into the Backpack slot — NOT the bag, NOT the
        // bank. Fail-pre: a bag grant (TryAddNewItem path) fails
        // pack-auto-equip here.
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(pack).IsNotNull();
        await Assert.That(pack!.TemplateId).IsEqualTo(PackProductId);
        await Assert.That(result.PackItemId).IsEqualTo(pack.Id);
        await Assert.That(BagOnlyCount(actor, PackProductId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, PackProductId)).IsEqualTo(0);

        // No store leg ran for a pack (DepositItem cannot move the slot).
        await Assert.That(result.Stages.All(s => !s.Stage.StartsWith("STORE"))).IsTrue();

        // Materials: exactly the recipe rows consumed, from a zero baseline.
        await Assert.That(BagOnlyCount(actor, PackMatItemId)).IsEqualTo(0);
        await Assert.That(result.MaterialsConsumed[PackMatItemId]).IsEqualTo(PackMatAmount);

        // Labor: exactly the recipe cost.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - PackCraftLaborCost));
        await Assert.That(result.LaborCharged).IsEqualTo(PackCraftLaborCost);
        await Assert.That(result.Report!.MissingRows).IsEmpty();
        await Assert.That(result.Report.Holds).IsEmpty();
    }

    [Test]
    public async Task PackProduction_MissingInputs_FailsClosed_NoPhantomPack()
    {
        var (actor, _, benchObjId) = CreateRig("m8cp-t1-noinput");
        // Nothing stocked anywhere — the bank holds no material rows.

        var result = CrafterWorkstationCycle.Run(actor, PackOptions("m8cp-t1-noinput", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("WITHDRAW");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Nothing created, nothing consumed, nothing charged, slot empty.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(BagOnlyCount(actor, PackProductId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, PackProductId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, PackMatItemId)).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(result.PackItemId).IsEqualTo((ulong)0);
        await Assert.That(result.Report!.MissingRows.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task PackProduction_OccupiedSlot_FailsClosed_NoMutation()
    {
        var (actor, _, benchObjId) = CreateRig("m8cp-t1-occupied");
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var incumbent = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!;
        StockBank(actor, PackMatItemId, PackMatAmount);

        var result = CrafterWorkstationCycle.Run(actor, PackOptions("m8cp-t1-occupied", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PRECHECK");
        // The incumbent pack is untouched, bank rows unmoved, labor unspent.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!.Id).IsEqualTo(incumbent.Id);
        await Assert.That(BankCount(actor, PackMatItemId)).IsEqualTo(PackMatAmount);
        await Assert.That(BagOnlyCount(actor, PackMatItemId)).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(result.PackItemId).IsEqualTo((ulong)0);
    }

    [Test]
    public async Task NonPackRecipe_StillTakesBagStorePath_NoRegression()
    {
        var (actor, _, benchObjId) = CreateRig("m8cp-t1-plain");
        StockBank(actor, PlainMatItemId, 1);

        var result = CrafterWorkstationCycle.Run(actor, PlainOptions("m8cp-t1-plain", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "withdraw-completed", "craft-completed",
                     "craft-materials-conserved", "craft-product-granted", "craft-labor-conserved",
                     "store-completed", "store-conservation"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Product: out of the bag, into the bank — exactly once. No pack
        // criteria attach to the bag path, and no pack id is reported.
        await Assert.That(BagOnlyCount(actor, PlainProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, PlainProductItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(result.PackItemId).IsEqualTo((ulong)0);
        await Assert.That(result.Criteria.Any(c => c.Name.StartsWith("pack-"))).IsFalse();
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - PlainCraftLaborCost));
    }

    [Test]
    public async Task PackProduction_SameKeyRetry_NeverDuplicatesPack()
    {
        var (actor, _, benchObjId) = CreateRig("m8cp-t1-retry");
        StockBank(actor, PackMatItemId, PackMatAmount);

        var first = CrafterWorkstationCycle.Run(actor, PackOptions("m8cp-t1-retry", benchObjId), new CrafterPump());
        await Assert.That(first.Passed).IsTrue();
        var firstPackId = first.PackItemId;
        await Assert.That(firstPackId).IsNotEqualTo((ulong)0);

        // Re-provision the bank, then rerun under the SAME cycle id with the
        // pack still carried: the occupied-slot guard refuses before any leg
        // re-runs (withdraw keys are never re-entered) — nothing duplicated.
        // (Post-pickup re-runs with an empty slot hit the same
        // {CycleId}-withdraw-* TryBegin dedupe pinned by slice-1.)
        StockBank(actor, PackMatItemId, PackMatAmount);
        var retry = CrafterWorkstationCycle.Run(actor, PackOptions("m8cp-t1-retry", benchObjId), new CrafterPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("PRECHECK");
        // Exactly one pack in the world: the first run's slot instance.
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(pack).IsNotNull();
        await Assert.That(pack!.Id).IsEqualTo(firstPackId);
        await Assert.That(BagOnlyCount(actor, PackProductId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, PackProductId)).IsEqualTo(0);
        // Labor charged once; the re-provisioned rows sit untouched in the bank.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - PackCraftLaborCost));
        await Assert.That(BagOnlyCount(actor, PackMatItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, PackMatItemId)).IsEqualTo(PackMatAmount);
    }

    // ------------------------------------------------------------ rig below

    private (GameplayActor actor, HeadlessSession session, uint benchObjId) CreateRig(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        actor.Character.Level = 10; // pack craft level gate (MinLevelToCraftSell)
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        return (actor, session, benchObjId);
    }

    private CrafterWorkstationCycle.CrafterWorkstationOptions PackOptions(string cycle, uint benchObjId)
        => new()
        {
            CycleId = cycle,
            CraftId = PackCraftId,
            BenchObjId = benchObjId
        };

    private CrafterWorkstationCycle.CrafterWorkstationOptions PlainOptions(string cycle, uint benchObjId)
        => new()
        {
            CycleId = cycle,
            CraftId = PlainCraftId,
            BenchObjId = benchObjId
        };

    // Bag-scoped count (TestRig.BagCount counts ALL containers — including
    // the bank — so bag asserts here use the explicit engine scope the
    // composer itself uses).
    private static int BagOnlyCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Inventory, templateId);

    private static int BankCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

    private static void StockBank(GameplayActor actor, uint templateId, int count)
    {
        GameplayActorTestRig.GrantItem(actor, templateId, count);
        var deposit = actor.DepositItem(templateId);
        if (deposit.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"StockBank setup failed for {templateId}: {deposit.Detail}");
    }

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
    /// Seeds the fixture pack recipe: 2 × packMat → 1 × fixture cargo pack
    /// at the rig bench (missing-only, the SeedFixturePackCraft pattern).
    /// </summary>
    private static void SeedFixturePackRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(PackCraftId))
        {
            crafts[PackCraftId] = new Craft
            {
                Id = PackCraftId,
                SkillId = PackCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = PackMatItemId, Amount = PackMatAmount }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = GameplayActorTestRig.CargoPackTemplateId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(PackCraftSkillId))
        {
            skills[PackCraftSkillId] = new SkillTemplate
            {
                Id = PackCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = PackCraftLaborCost,
                ActabilityGroupId = 0,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }
    }

    /// <summary>
    /// Seeds the fixture plain recipe: 1 × plainMat → 1 × plain product at
    /// the rig bench (missing-only, the slice-1 SeedFixtureRecipe pattern).
    /// </summary>
    private static void SeedFixturePlainRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(PlainCraftId))
        {
            crafts[PlainCraftId] = new Craft
            {
                Id = PlainCraftId,
                SkillId = PlainCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = PlainMatItemId, Amount = 1 }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = PlainProductItemId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(PlainCraftSkillId))
        {
            skills[PlainCraftSkillId] = new SkillTemplate
            {
                Id = PlainCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = PlainCraftLaborCost,
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
    /// once the engine queue is active (the CrafterPump craft shape).
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
