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
/// M8 Crafter multi-count + chance slice (ROADMAP M8 living-village
/// contracts) — <see cref="CrafterWorkstationCycle"/> with Count &gt; 1 and
/// Rate &lt; 100 products: count crafts withdraw Amount × count up front and
/// run one unchanged single-step <see cref="GameplayActor.Craft"/> leg per
/// step (exact consumption, labor per step); chance products assert per-run
/// bounds (never phantom, never negative) with the distribution pinned
/// statistically over N runs, never by exact count.
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (single-step consumption on a count&gt;1 run, mid-cycle partial burn on a
/// short bank, duplicate output on same-key retry, always-grant/always-drop
/// chance rows, multi-count pack runs) and PASSES on the composed real
/// paths. Raw engine gates (unknown recipe, busy queue, wrong bench) stay
/// pinned by GameplayActorCraftTests + the slice-1 tests — these tests pin
/// the COMPOSER's count loop and chance bounds, not the engine.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class CrafterMultiCountChanceTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Fixture MULTI recipe: 2 × matA (99_063) + 1 × matB (99_064) → 1 ×
    // fixture product (99_065, plain bag-grant item) at the rig bench, skill
    // with labor 10. Id range 99_061+: 99_031-99_035 are slice-1,
    // 99_041-99_045 merchant, 99_043/99_044 hauler-vendor, 99_046-99_053
    // pack/merchant-plain — missing-only seeding is first-wins, so ranges
    // must not overlap.
    private const uint MultiCraftId = 99_061;
    private const uint MultiCraftSkillId = 99_062;
    private const int MultiCraftLaborCost = 10;
    private const uint MultiMatAItemId = 99_063;
    private const uint MultiMatBItemId = 99_064;
    private const uint MultiProductItemId = 99_065;
    private const int MultiMatAAmount = 2;
    private const int MultiMatBAmount = 1;

    // Fixture CHANCE recipe: 1 × chanceMat (99_068) → 1 × guaranteed
    // (99_069, Rate 100) + 1 × bonus (99_070, Rate 50) at the rig bench,
    // skill with labor 10. The guaranteed co-product pins that the chance
    // split did not weaken the exact path.
    private const uint ChanceCraftId = 99_066;
    private const uint ChanceCraftSkillId = 99_067;
    private const int ChanceCraftLaborCost = 10;
    private const uint ChanceMatItemId = 99_068;
    private const uint ChanceGuaranteedItemId = 99_069;
    private const uint ChanceBonusItemId = 99_070;
    private const int ChanceBonusRate = 50;

    // Fixture PACK recipe for the count guard: 2 × packMat (99_073) → 1 ×
    // fixture cargo pack (BackpackTemplate + TradePack → ResultsInBackpack).
    private const uint PackCraftId = 99_071;
    private const uint PackCraftSkillId = 99_072;
    private const uint PackMatItemId = 99_073;
    private const int PackMatAmount = 2;
    private static uint PackProductId => GameplayActorTestRig.CargoPackTemplateId;

    private static uint s_nextWorldId = 0x6400_0000; // fresh base: 0x6200 crafter-ws / 0x6300 crafter-pack

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.SeedCargoPackSurface();
        GameplayActorTestRig.SeedCraftSurface();
        SeedFixtureMultiRecipe();
        SeedFixtureChanceRecipe();
        SeedFixturePackRecipe();
        GameplayActorTestRig.SeedItemTemplate(MultiMatAItemId);
        GameplayActorTestRig.SeedItemTemplate(MultiMatBItemId);
        GameplayActorTestRig.SeedItemTemplate(MultiProductItemId);
        GameplayActorTestRig.SeedItemTemplate(ChanceMatItemId);
        GameplayActorTestRig.SeedItemTemplate(ChanceGuaranteedItemId);
        GameplayActorTestRig.SeedItemTemplate(ChanceBonusItemId);
        GameplayActorTestRig.SeedItemTemplate(PackMatItemId);
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
    public async Task MultiCount_HappyPath_ConsumesAmountTimesCount_GrantsPerStep()
    {
        const int count = 3;
        var (actor, _, benchObjId) = CreateRig("m8cm-t1-multi");
        StockBank(actor, MultiMatAItemId, MultiMatAAmount * count);
        StockBank(actor, MultiMatBItemId, MultiMatBAmount * count);

        var result = CrafterWorkstationCycle.Run(actor, MultiOptions("m8cm-t1", benchObjId, count), new CrafterPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "withdraw-completed", "craft-completed",
                     "craft-materials-conserved", "craft-product-granted", "craft-labor-conserved",
                     "store-completed", "store-conservation"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Three engine steps ran (fail-pre: a composer that ignores Count
        // runs one step — StepsCompleted, consumption, product, and labor
        // below all catch it).
        await Assert.That(result.StepsCompleted).IsEqualTo(count);
        await Assert.That(result.Stages.Count(s => s.Stage.StartsWith("CRAFT-"))).IsEqualTo(count);

        // Materials: exactly Amount × count consumed, from a zero baseline.
        await Assert.That(BagOnlyCount(actor, MultiMatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MultiMatBItemId)).IsEqualTo(0);
        await Assert.That(result.MaterialsConsumed[MultiMatAItemId]).IsEqualTo(MultiMatAAmount * count);
        await Assert.That(result.MaterialsConsumed[MultiMatBItemId]).IsEqualTo(MultiMatBAmount * count);

        // Labor: exactly one recipe cost per step.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - MultiCraftLaborCost * count));
        await Assert.That(result.LaborCharged).IsEqualTo(MultiCraftLaborCost * count);

        // Product: one grant per step, out of the bag, into the bank.
        await Assert.That(BagOnlyCount(actor, MultiProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MultiProductItemId)).IsEqualTo(count);
        await Assert.That(result.Report!.ProductStored[MultiProductItemId]).IsEqualTo(count);
        await Assert.That(result.Report.MissingRows).IsEmpty();
        await Assert.That(result.Report.Holds).IsEmpty();
    }

    [Test]
    public async Task MultiCount_PartialShortage_HoldsBeforeAnyStep_NothingConsumed()
    {
        const int count = 3;
        var (actor, _, benchObjId) = CreateRig("m8cm-t1-short");
        // One step's worth only — the bank cannot cover Amount × count.
        StockBank(actor, MultiMatAItemId, MultiMatAAmount);
        StockBank(actor, MultiMatBItemId, MultiMatBAmount);

        var result = CrafterWorkstationCycle.Run(actor, MultiOptions("m8cm-t1-short", benchObjId, count), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("WITHDRAW");
        await Assert.That(result.StepsCompleted).IsEqualTo(0);
        // Fail-pre: a composer that checks only one step's worth would run
        // step 1 and burn materials mid-cycle — nothing may be consumed.
        await Assert.That(BagOnlyCount(actor, MultiMatAItemId)).IsEqualTo(MultiMatAAmount);
        await Assert.That(BagOnlyCount(actor, MultiMatBItemId)).IsEqualTo(MultiMatBAmount);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(BagOnlyCount(actor, MultiProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MultiProductItemId)).IsEqualTo(0);
        await Assert.That(result.Report!.Holds.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task MultiCount_SameKeyRetry_NeverDuplicatesOutput()
    {
        const int count = 2;
        var (actor, _, benchObjId) = CreateRig("m8cm-t1-retry");
        StockBank(actor, MultiMatAItemId, MultiMatAAmount * count);
        StockBank(actor, MultiMatBItemId, MultiMatBAmount * count);

        var first = CrafterWorkstationCycle.Run(actor, MultiOptions("m8cm-t1-retry", benchObjId, count), new CrafterPump());
        await Assert.That(first.Passed).IsTrue();
        await Assert.That(first.StepsCompleted).IsEqualTo(count);

        // Re-provision the bank, then rerun under the SAME cycle id: the
        // FIRST keyed leg (withdraw) must refuse the duplicate key
        // pre-flight — no step re-runs, nothing is duplicated.
        StockBank(actor, MultiMatAItemId, MultiMatAAmount * count);
        StockBank(actor, MultiMatBItemId, MultiMatBAmount * count);
        var retry = CrafterWorkstationCycle.Run(actor, MultiOptions("m8cm-t1-retry", benchObjId, count), new CrafterPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("WITHDRAW");
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        // Exactly one run's products in the world; labor charged once (two
        // steps); the re-provisioned rows sit untouched in the bank.
        await Assert.That(BankCount(actor, MultiProductItemId)).IsEqualTo(count);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - MultiCraftLaborCost * count));
        await Assert.That(BagOnlyCount(actor, MultiMatAItemId)).IsEqualTo(0);
        await Assert.That(BagOnlyCount(actor, MultiMatBItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, MultiMatAItemId)).IsEqualTo(MultiMatAAmount * count);
        await Assert.That(BankCount(actor, MultiMatBItemId)).IsEqualTo(MultiMatBAmount * count);
    }

    [Test]
    public async Task ChanceProduct_StatisticalBounds_ExactConsumptionPerRun()
    {
        // N runs share one rig (unique cycle ids — no idempotency overlap):
        // labor is topped up once (N × cost), materials are re-stocked per
        // run, and the bonus bank delta per run is the grant (0 or 1).
        // Bounds [1, N-1] over N=20 fail only on an always-grant or
        // always-drop defect (false-failure p = 2×0.5^20 ≈ 2e-6) — an exact
        // count assert here would flake on every healthy run.
        const int runs = 20;
        var (actor, _, benchObjId) = CreateRig("m8cc-t1-stat", labor: 500);
        var bonusTotal = 0;
        for (var i = 0; i < runs; i++)
        {
            StockBank(actor, ChanceMatItemId, 1);
            var bonusBefore = BankCount(actor, ChanceBonusItemId);
            var guaranteedBefore = BankCount(actor, ChanceGuaranteedItemId);

            var result = CrafterWorkstationCycle.Run(actor, ChanceOptions($"m8cc-t1-stat-{i}", benchObjId), new CrafterPump());

            // A miss still passes: materials + labor are consumed (canonical
            // 1.2 rate-gated behavior) and the guaranteed row still stores.
            await Assert.That(result.Passed).IsTrue();
            await Assert.That(result.Criteria.Any(c => c.Name == "craft-chance-product-bounded" && c.Passed)).IsTrue();
            await Assert.That(result.MaterialsConsumed[ChanceMatItemId]).IsEqualTo(1);
            await Assert.That(BankCount(actor, ChanceGuaranteedItemId) - guaranteedBefore).IsEqualTo(1);

            var bonusGrant = BankCount(actor, ChanceBonusItemId) - bonusBefore;
            await Assert.That(bonusGrant is 0 or 1).IsTrue();
            bonusTotal += bonusGrant;
        }

        await Assert.That(bonusTotal).IsGreaterThan(0);
        await Assert.That(bonusTotal).IsLessThan(runs);
        // Exact consumption + labor across all runs (no leakage per roll).
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(500 - ChanceCraftLaborCost * runs));
        await Assert.That(BagOnlyCount(actor, ChanceMatItemId)).IsEqualTo(0);
        await Assert.That(BankCount(actor, ChanceGuaranteedItemId)).IsEqualTo(runs);
    }

    [Test]
    public async Task PackRecipe_WithCount_RejectedPrecheck_NoMutation()
    {
        var (actor, _, benchObjId) = CreateRig("m8cm-t1-packcount");
        StockBank(actor, PackMatItemId, PackMatAmount * 2);

        var result = CrafterWorkstationCycle.Run(actor, PackOptions("m8cm-t1-packcount", benchObjId), new CrafterPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PRECHECK");
        // Fail-pre: a composer that loops pack steps would consume materials
        // and refuse the second step mid-cycle — nothing may move at all.
        await Assert.That(result.StepsCompleted).IsEqualTo(0);
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(BankCount(actor, PackMatItemId)).IsEqualTo(PackMatAmount * 2);
        await Assert.That(BagOnlyCount(actor, PackMatItemId)).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(result.PackItemId).IsEqualTo((ulong)0);
    }

    // ------------------------------------------------------------ rig below

    private (GameplayActor actor, HeadlessSession session, uint benchObjId) CreateRig(string name, short labor = 100)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        actor.Character.Level = 10; // pack craft level gate (MinLevelToCraftSell)
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = labor;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        return (actor, session, benchObjId);
    }

    private CrafterWorkstationCycle.CrafterWorkstationOptions MultiOptions(string cycle, uint benchObjId, int count)
        => new()
        {
            CycleId = cycle,
            CraftId = MultiCraftId,
            BenchObjId = benchObjId,
            Count = count
        };

    private CrafterWorkstationCycle.CrafterWorkstationOptions ChanceOptions(string cycle, uint benchObjId)
        => new()
        {
            CycleId = cycle,
            CraftId = ChanceCraftId,
            BenchObjId = benchObjId
        };

    private CrafterWorkstationCycle.CrafterWorkstationOptions PackOptions(string cycle, uint benchObjId)
        => new()
        {
            CycleId = cycle,
            CraftId = PackCraftId,
            BenchObjId = benchObjId,
            Count = 2
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
    /// Seeds the fixture multi recipe: 2 × matA + 1 × matB → 1 × fixture
    /// product at the rig bench (missing-only, the slice-1 SeedFixtureRecipe
    /// pattern).
    /// </summary>
    private static void SeedFixtureMultiRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(MultiCraftId))
        {
            crafts[MultiCraftId] = new Craft
            {
                Id = MultiCraftId,
                SkillId = MultiCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = MultiMatAItemId, Amount = MultiMatAAmount },
                    new CraftMaterial { ItemId = MultiMatBItemId, Amount = MultiMatBAmount }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = MultiProductItemId, Amount = 1, Rate = 100 }
                ]
            };
        }

        SeedCraftSkill(MultiCraftSkillId, MultiCraftLaborCost);
    }

    /// <summary>
    /// Seeds the fixture chance recipe: 1 × chanceMat → 1 × guaranteed
    /// (Rate 100) + 1 × bonus (Rate 50) at the rig bench (missing-only).
    /// </summary>
    private static void SeedFixtureChanceRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(ChanceCraftId))
        {
            crafts[ChanceCraftId] = new Craft
            {
                Id = ChanceCraftId,
                SkillId = ChanceCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = ChanceMatItemId, Amount = 1 }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = ChanceGuaranteedItemId, Amount = 1, Rate = 100 },
                    new CraftProduct { ItemId = ChanceBonusItemId, Amount = 1, Rate = ChanceBonusRate }
                ]
            };
        }

        SeedCraftSkill(ChanceCraftSkillId, ChanceCraftLaborCost);
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

        SeedCraftSkill(PackCraftSkillId, 10);
    }

    private static void SeedCraftSkill(uint skillId, int laborCost)
    {
        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(skillId))
        {
            skills[skillId] = new SkillTemplate
            {
                Id = skillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = laborCost,
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
    /// once the engine queue is active (the CrafterPump craft shape), then
    /// settles the skill-GCD quiescence the ICrafterPump contract requires.
    /// Live world ticks space casts naturally, but this rig ticks
    /// synchronously — without the settle, a back-to-back next step hits
    /// the engine's 150ms re-cast gate (SkillResult.CooldownTime,
    /// TRACE-only): no skill casts, no labor charged, while the step's
    /// effect still consumes materials.
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

            // Settle only terminal requests: extra ticks on a timed-out
            // (still-Running) request could flip its verdict late.
            if (request.IsTerminal)
            {
                var settleDeadline = Environment.TickCount64 + 2000;
                while (Environment.TickCount64 < settleDeadline)
                {
                    var character = actor.Character;
                    if (DateTime.UtcNow >= character.SkillLastUsed.AddMilliseconds(150)
                        && DateTime.UtcNow >= character.GlobalCooldown)
                        break;
                    actor.Tick(TimeSpan.FromMilliseconds(20));
                }
            }

            return request;
        }
    }
}
