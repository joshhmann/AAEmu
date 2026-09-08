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
/// M8 Crafter v1 slice-1 (ROADMAP M8 living-village contracts) — withdraw →
/// workstation craft → store through <see cref="CrafterWorkstationCycle"/>:
/// withdraw recipe rows from the bank through the real
/// <see cref="GameplayActor.WithdrawItem"/> path, craft through the real
/// CharacterCraft → CraftEffect chain (bag grant, non-pack product), store
/// the product back into the bank through the real
/// <see cref="GameplayActor.DepositItem"/> path, ending in a canned shortage
/// report.
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (phantom output on missing inputs, swallowed shortage, consumed inputs on
/// a refused craft, duplicate output on same-key retry) and PASSES on the
/// composed real paths. Raw engine rejections (unknown recipe, short bag,
/// wrong bench, low labor) are already pinned by GameplayActorCraftTests —
/// these tests pin the COMPOSER's fail-closed order and conservation, not
/// the engine gates.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class CrafterWorkstationCycleTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Fixture recipe: 2 × matA (99_033) + 1 × matB (99_034) → 1 × fixture
    // product (99_035, plain bag-grant item) at the rig bench, skill with
    // labor 10 (the SeedFixturePackCraft shape, own ids).
    private const uint CraftId = 99_031;
    private const uint CraftSkillId = 99_032;
    private const int CraftLaborCost = 10;
    private const uint MatAItemId = 99_033;
    private const uint MatBItemId = 99_034;
    private const uint ProductItemId = 99_035;
    private const int MatAAmount = 2;
    private const int MatBAmount = 1;

    private static uint s_nextWorldId = 0x6200_0000; // fresh base: 0x6000 loadpack / 0x6001 housing-race / 0x6002 harvest / 0x6100 hauler / 0x8000 farmer

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedCraftSurface();
        SeedFixtureRecipe();
        GameplayActorTestRig.SeedItemTemplate(MatAItemId);
        GameplayActorTestRig.SeedItemTemplate(MatBItemId);
        GameplayActorTestRig.SeedItemTemplate(ProductItemId);
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
    public async Task CraftStore_HappyPath_ConservesMaterialsLaborAndProduct()
    {
        var (actor, _, benchObjId) = CreateRig("m8cw-t1-happy");
        StockBank(actor, MatAItemId, MatAAmount);
        StockBank(actor, MatBItemId, MatBAmount);

        var result = CrafterWorkstationCycle.Run(actor, Options("m8cw-t1", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "withdraw-completed", "craft-completed",
                     "craft-materials-conserved", "craft-product-granted", "craft-labor-conserved",
                     "store-completed", "store-conservation"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Materials: exactly the recipe rows consumed, from a zero baseline.
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MatBItemId)).IsEqualTo(0);
        await Assert.That(result.MaterialsConsumed[MatAItemId]).IsEqualTo(MatAAmount);
        await Assert.That(result.MaterialsConsumed[MatBItemId]).IsEqualTo(MatBAmount);

        // Labor: exactly the recipe cost.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
        await Assert.That(result.LaborCharged).IsEqualTo(CraftLaborCost);

        // Product: out of the bag, into the bank — exactly once.
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(1);
        await Assert.That(result.Report!.ProductStored[ProductItemId]).IsEqualTo(1);
        await Assert.That(result.Report.MissingRows).IsEmpty();
        await Assert.That(result.Report.Holds).IsEmpty();
    }

    [Test]
    public async Task Craft_MissingInputs_FailsClosed_NoPhantomOutput()
    {
        var (actor, _, benchObjId) = CreateRig("m8cw-t1-noinput");
        // Nothing stocked anywhere — the bank holds no material rows.

        var result = CrafterWorkstationCycle.Run(actor, Options("m8cw-t1-noinput", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("WITHDRAW");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Nothing created, nothing consumed, nothing charged.
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(result.Report!.MissingRows.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Withdraw_PartialShortage_HoldsWithReason_NothingConsumed()
    {
        var (actor, _, benchObjId) = CreateRig("m8cw-t1-short");
        StockBank(actor, MatAItemId, 1); // recipe needs 2; MatB absent entirely.

        var result = CrafterWorkstationCycle.Run(actor, Options("m8cw-t1-short", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("WITHDRAW");
        // The withdrawn MatA row stays in the bag (never deleted, never
        // vended); labor unspent; no product anywhere.
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(BagOnlyCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(0);
        await Assert.That(result.Report!.Holds.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Craft_BusyQueue_HoldsWithReason()
    {
        var (actor, _, benchObjId) = CreateRig("m8cw-t1-busy");
        StockBank(actor, MatAItemId, MatAAmount);
        StockBank(actor, MatBItemId, MatBAmount);
        // Occupy the engine queue directly (the GameplayActorCraftTests
        // busy-queue convention). The direct engine entry enforces the
        // bag-scope rule, so the occupying step needs its own bag rows —
        // granted separately from the bank stock the composer will
        // withdraw. The composer's craft leg must still hold.
        GameplayActorTestRig.GrantItem(actor, MatAItemId, MatAAmount);
        GameplayActorTestRig.GrantItem(actor, MatBItemId, MatBAmount);
        var craft = CraftManager.Instance.GetCraftById(CraftId)!;
        actor.Character.Craft.Craft(craft, 1, benchObjId);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsTrue();
        // The occupying engine step runs the normal skill path (which charges
        // its own labor); the composer must charge nothing further.
        var laborAfterOccupy = actor.Character.LaborPower;

        var result = CrafterWorkstationCycle.Run(actor, Options("m8cw-t1-busy", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("CRAFT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        // Occupying rows + withdrawn rows all sit in the bag unconsumed;
        // labor unchanged by the composer; no product granted.
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(MatAAmount * 2);
        await Assert.That(BagOnlyCount(actor, MatBItemId)).IsEqualTo(MatBAmount * 2);
        await Assert.That(actor.Character.LaborPower).IsEqualTo(laborAfterOccupy);
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(0);
    }

    [Test]
    public async Task Cycle_SameKeyRetry_NeverDuplicatesOutput()
    {
        var (actor, _, benchObjId) = CreateRig("m8cw-t1-retry");
        StockBank(actor, MatAItemId, MatAAmount);
        StockBank(actor, MatBItemId, MatBAmount);

        var first = CrafterWorkstationCycle.Run(actor, Options("m8cw-t1-retry", benchObjId), new CrafterPump());
        await Assert.That(first.Passed).IsTrue();

        // Re-provision the bank, then rerun under the SAME cycle id: the
        // FIRST keyed leg (withdraw) must refuse the duplicate key
        // pre-flight (TryBegin StateTransition) — nothing moves, the craft
        // leg is never re-entered, nothing is duplicated.
        StockBank(actor, MatAItemId, MatAAmount);
        StockBank(actor, MatBItemId, MatBAmount);
        var retry = CrafterWorkstationCycle.Run(actor, Options("m8cw-t1-retry", benchObjId), new CrafterPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("WITHDRAW");
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        // Exactly one product in the world (first run's bank row); labor
        // charged once; the re-provisioned rows sit untouched in the bank.
        await Assert.That(BankCount(actor, ProductItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
        await Assert.That(BagOnlyCount(actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MatBItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MatAItemId)).IsEqualTo(MatAAmount);
        await Assert.That(BankCount(actor, MatBItemId)).IsEqualTo(MatBAmount);
    }

    // ------------------------------------------------------------ rig below

    private (GameplayActor actor, HeadlessSession session, uint benchObjId) CreateRig(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        return (actor, session, benchObjId);
    }

    private CrafterWorkstationCycle.CrafterWorkstationOptions Options(string cycle, uint benchObjId)
        => new()
        {
            CycleId = cycle,
            CraftId = CraftId,
            BenchObjId = benchObjId
        };

    // Bag-scoped count (TestRig.BagCount counts ALL containers — including
    // the bank, where the stored product lands — so bag asserts here use
    // the explicit engine scope the composer itself uses).
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
    /// Seeds the fixture recipe: 2 × matA + 1 × matB → 1 × fixture product
    /// at the rig bench (missing-only, the SeedFixturePackCraft pattern).
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
    /// once the engine queue is active (the HaulCraftPump craft shape).
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
