using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C5-S1 village day (ROADMAP M8 living-village contracts) — two-villager
/// profession half-day through <see cref="VillageDayCycle"/>: the C1 phase
/// machine (pure <see cref="BotScheduleResolver"/> sweep, never TimeManager)
/// dispatches Work ticks to the existing C3/C4 slice composers
/// (<see cref="FarmerCycleScenario"/> + <see cref="CrafterWorkstationCycle"/>),
/// with a combined ledger + the M5 audit-completeness law over the
/// concatenated traces.
///
/// Fail-pre discipline: every test FAILS without the composer (CS0246 —
/// no such type) and on the corresponding composition defect (wrong-profession
/// dispatch, unconserved combined ledger, incomplete audit trail, Work legs
/// during Home, cross-villager contamination on fail-closed, duplicated
/// yield/output on same-cycle retry), and PASSES on the composed real paths.
/// No engine diffs: the composer calls existing composers only.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class VillageDayCycleRigTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Fixture recipe (the CrafterWorkstationCycleTests shape, own cycle ids):
    // 2 x matA (99_033) + 1 x matB (99_034) -> 1 x product (99_035), labor 10.
    private const uint CraftId = 99_031;
    private const uint CraftSkillId = 99_032;
    private const int CraftLaborCost = 10;
    private const uint MatAItemId = 99_033;
    private const uint MatBItemId = 99_034;
    private const uint ProductItemId = 99_035;
    private const int MatAAmount = 2;
    private const int MatBAmount = 1;

    private const uint PotatoSeedId = CropHarvestLoopTests.PotatoSeedItemId;
    private const uint PotatoId = CropHarvestLoopTests.PotatoItemId;
    private const uint GoldenId = CropHarvestLoopTests.GoldenPotatoItemId;

    private static uint s_nextWorldId = 0x9000_0000; // fresh base: 0x8 farmer / 0x62 crafter
    private readonly List<WorldInstance> _registeredWorlds = [];

    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedCraftSurface();
        CropHarvestLoopRig.Seed();
        SeedFixtureRecipe();
        GameplayActorTestRig.SeedItemTemplate(MatAItemId);
        GameplayActorTestRig.SeedItemTemplate(MatBItemId);
        GameplayActorTestRig.SeedItemTemplate(ProductItemId);
        SeedEquipSurface();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        // Headless Plant boundary (the FarmerCycleScenarioTests convention):
        // replant consumes exactly one seed, then Interrupts at persistence.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        MySQL.SetConfiguration(null);
        AppConfiguration.Instance.World = _previousWorldConfig;
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", Flags)!
            .GetValue(WorldManager.Instance)!;
        foreach (var world in _registeredWorlds)
            if (worlds.TryGetValue(world.Id, out var registered) && ReferenceEquals(registered, world))
                worlds.TryRemove(world.Id, out _);
        _registeredWorlds.Clear();
    }

    [Test]
    public async Task VillageDay_TwoProfessions_WorkPhasesDispatchToCorrectComposer()
    {
        var farmer = SetupFarmer("m8v1-dispatch-farmer");
        var crafter = SetupCrafter("m8v1-dispatch-crafter");

        var result = VillageDayCycle.Run(
        [
            farmer.Spec,
            crafter.Spec
        ], new VillageDayCycle.VillageDayOptions { CycleId = "m8v1-dispatch" });

        await Assert.That(result.Passed).IsTrue();
        // Both villagers entered Work during the 06->14 sweep.
        foreach (var entry in result.Report.Villagers)
            await Assert.That(entry.PhasesVisited.Contains(BotSchedulePhase.Work)).IsTrue();
        // Dispatch correctness: the farmer ran harvest legs, the crafter ran craft legs.
        var farmerEntry = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Farmer);
        var crafterEntry = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Crafter);
        await Assert.That(farmerEntry.FarmerResult!.Passed).IsTrue();
        await Assert.That(farmerEntry.FarmerResult.Stages.Any(s => s.Stage.StartsWith("HARVEST"))).IsTrue();
        await Assert.That(crafterEntry.CrafterResult!.Passed).IsTrue();
        await Assert.That(crafterEntry.CrafterResult.Stages.Any(s => s.Stage == "CRAFT")).IsTrue();
        // No cross-profession legs: the farmer ran zero craft stages, the crafter zero harvest stages.
        await Assert.That(farmerEntry.FarmerResult.Stages.Any(s => s.Stage is "CRAFT" or "WITHDRAW-99033" or "STORE-99035")).IsFalse();
        await Assert.That(crafterEntry.CrafterResult.Stages.Any(s => s.Stage.StartsWith("HARVEST"))).IsFalse();
        await Assert.That(result.Criteria.Any(c => c.Name == "dispatch-correct" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task VillageDay_CombinedLedger_Reconciles()
    {
        var farmer = SetupFarmer("m8v1-ledger-farmer");
        var crafter = SetupCrafter("m8v1-ledger-crafter");

        var result = VillageDayCycle.Run(
        [
            farmer.Spec,
            crafter.Spec
        ], new VillageDayCycle.VillageDayOptions { CycleId = "m8v1-ledger" });

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[] { "dispatch-correct", "village-labor-conserved", "village-farmer-seeds-conserved", "village-audit-complete" })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();
        // Observable end state: farmer banked the harvest, crafter banked exactly one product.
        await Assert.That(BankCount(farmer.Session, PotatoId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BankCount(crafter.Actor, ProductItemId)).IsEqualTo(1);
        // Crafter labor charged exactly once across the village.
        await Assert.That(crafter.Actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
        // Farmer seed law over the leg: 5 stocked -> plant -1 -> harvest +1 -> replant -1 = 4.
        var report = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Farmer).FarmerResult!.Report;
        await Assert.That(report.SeedsBeforeHarvest).IsEqualTo(4);
        await Assert.That(report.SeedsAfterHarvest).IsEqualTo(5);
        await Assert.That(report.SeedsAfterReplant).IsEqualTo(4);
    }

    [Test]
    public async Task VillageDay_AuditTrace_Complete()
    {
        var farmer = SetupFarmer("m8v1-audit-farmer");
        var crafter = SetupCrafter("m8v1-audit-crafter");

        var result = VillageDayCycle.Run(
        [
            farmer.Spec,
            crafter.Spec
        ], new VillageDayCycle.VillageDayOptions { CycleId = "m8v1-audit" });

        await Assert.That(result.Passed).IsTrue();
        // Concatenated traces carry both villagers' legs.
        await Assert.That(result.TraceRecords.Count).IsGreaterThanOrEqualTo(8);
        await Assert.That(result.TraceRecords.Any(r => r.Action == ActorActionType.Harvest)).IsTrue();
        await Assert.That(result.TraceRecords.Any(r => r.Action == ActorActionType.Plant)).IsTrue();
        // The M5 law over the concatenation: every Completed record carries
        // the full transition set; no Rejected record ever carries Running.
        foreach (var record in result.TraceRecords.Where(r => r.Result == ActorLifecycleState.Completed))
        {
            await Assert.That(record.StateChanges.Any(s => s.Contains("Requested"))).IsTrue();
            await Assert.That(record.StateChanges.Any(s => s.Contains("Accepted"))).IsTrue();
            await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
            await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
        }
        await Assert.That(result.TraceRecords.Where(r => r.Result == ActorLifecycleState.Rejected).SelectMany(r => r.StateChanges).Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(result.Criteria.Any(c => c.Name == "village-audit-complete" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task VillageDay_HomePhase_HoldsWithReason_NoProfessionLegs()
    {
        // Work windows far outside the 06->14 sweep: the whole day is Home.
        var offHours = new BotDailyAnchors
        {
            HomeBy = 20f,
            WorkStart = 20f,
            WorkEnd = 21f,
            RestStart = 22f,
            RestEnd = 6f
        };
        var (farmerActor, farmerSession) = CreateActorOnUniqueWorld("m8v1-home-farmer");
        var (crafterActor, _) = CreateActorOnUniqueWorld("m8v1-home-crafter");
        GameplayActorTestRig.SetPosition(farmerActor, TestPosition);
        GameplayActorTestRig.SetPosition(crafterActor, TestPosition);

        var result = VillageDayCycle.Run(
        [
            new VillageDayCycle.VillagerSpec
            {
                Name = "m8v1-home-farmer",
                Profession = VillageDayCycle.VillageProfession.Farmer,
                Actor = farmerActor,
                Anchors = offHours,
                CropObjId = 0
            },
            new VillageDayCycle.VillagerSpec
            {
                Name = "m8v1-home-crafter",
                Profession = VillageDayCycle.VillageProfession.Crafter,
                Actor = crafterActor,
                Anchors = offHours,
                CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
                {
                    CycleId = "m8v1-home-craft",
                    CraftId = CraftId,
                    BenchObjId = 0
                },
                CrafterPump = new VillagePump()
            }
        ], new VillageDayCycle.VillageDayOptions { CycleId = "m8v1-home" });

        await Assert.That(result.Passed).IsTrue();
        var homeLaborBefore = crafterActor.Character.LaborPower;
        foreach (var entry in result.Report.Villagers)
        {
            await Assert.That(entry.PhasesVisited.Contains(BotSchedulePhase.Work)).IsFalse();
            await Assert.That(entry.WorkTicks).IsEqualTo(0);
            await Assert.That(entry.FarmerResult).IsNull();
            await Assert.That(entry.CrafterResult).IsNull();
        }
        // No profession leg ran anywhere: zero trace records, labor unspent, bags untouched.
        await Assert.That(result.TraceRecords).IsEmpty();
        await Assert.That(result.Criteria.Any(c => c.Name == "dispatch-correct" && c.Passed)).IsTrue();
        await Assert.That(crafterActor.Character.LaborPower).IsEqualTo(homeLaborBefore);
        await Assert.That(farmerSession.Character.Inventory.Bag.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task VillageDay_FarmerShortageAndCrafterMissingInputs_BothFailClosed()
    {
        // Farmer holds only at REPLANT (unapproved seed); crafter holds at WITHDRAW (empty bank).
        var farmer = SetupFarmer("m8v1-hold-farmer", seedItemId: GameplayActorTestRig.TestItemTemplateId);
        var (crafterActor, holdSession) = CreateActorOnUniqueWorld("m8v1-hold-crafter");
        GameplayActorTestRig.SetPosition(crafterActor, TestPosition);
        crafterActor.Character.LaborPower = 100;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(holdSession, crafterActor);

        var result = VillageDayCycle.Run(
        [
            farmer.Spec,
            new VillageDayCycle.VillagerSpec
            {
                Name = "m8v1-hold-crafter",
                Profession = VillageDayCycle.VillageProfession.Crafter,
                Actor = crafterActor,
                Anchors = BotDailyAnchors.Template,
                CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
                {
                    CycleId = "m8v1-hold-craft",
                    CraftId = CraftId,
                    BenchObjId = benchObjId
                },
                CrafterPump = new VillagePump()
            }
        ], new VillageDayCycle.VillageDayOptions { CycleId = "m8v1-hold" });

        await Assert.That(result.Passed).IsFalse();
        var farmerEntry = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Farmer);
        var crafterEntry = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Crafter);
        await Assert.That(farmerEntry.FarmerResult!.Passed).IsFalse();
        await Assert.That(farmerEntry.FarmerResult.FailStage).IsEqualTo("REPLANT");
        await Assert.That(crafterEntry.CrafterResult!.Passed).IsFalse();
        await Assert.That(crafterEntry.CrafterResult.FailStage).IsEqualTo("WITHDRAW");
        // Fail-closed isolation: the farmer's harvest yield still banked (no deletion),
        // the crafter granted nothing and charged no labor (no phantom output).
        await Assert.That(BankCount(farmer.Session, PotatoId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BagOnlyCount(crafterActor, ProductItemId)).IsEqualTo(0);
        await Assert.That(BankCount(crafterActor, ProductItemId)).IsEqualTo(0);
        await Assert.That(crafterActor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(crafterEntry.CrafterResult.Report!.Holds.Count + crafterEntry.CrafterResult.Report.MissingRows.Count).IsGreaterThan(0);
        await Assert.That(farmerEntry.FarmerResult.Report.ReplantOutcome.Contains("not on the approved list")).IsTrue();
    }

    [Test]
    public async Task VillageDay_SameCycleKeyRetry_NeverDuplicates()
    {
        var farmer = SetupFarmer("m8v1-retry-farmer");
        var crafter = SetupCrafter("m8v1-retry-crafter", "m8v1-retry-craft");
        var options = new VillageDayCycle.VillageDayOptions { CycleId = "m8v1-retry" };

        var first = VillageDayCycle.Run([farmer.Spec, crafter.Spec], options);
        await Assert.That(first.Passed).IsTrue();
        var potatoBanked = BankCount(farmer.Session, PotatoId);
        var goldenBanked = BankCount(farmer.Session, GoldenId);
        var laborAfterFirst = crafter.Actor.Character.LaborPower;

        // Re-provision the crafter bank, then rerun under the SAME cycle id:
        // keyed legs must refuse the duplicate instead of re-executing.
        StockBank(crafter.Actor, MatAItemId, MatAAmount);
        StockBank(crafter.Actor, MatBItemId, MatBAmount);
        var retry = VillageDayCycle.Run([farmer.Spec, crafter.Spec], options);

        await Assert.That(retry.Passed).IsFalse();
        // Exactly one harvest yield and one crafted product in the world.
        await Assert.That(BankCount(farmer.Session, PotatoId)).IsEqualTo(potatoBanked);
        await Assert.That(BankCount(farmer.Session, GoldenId)).IsEqualTo(goldenBanked);
        await Assert.That(BankCount(crafter.Actor, ProductItemId)).IsEqualTo(1);
        // Labor charged once; the re-provisioned rows sit untouched in the bank.
        await Assert.That(crafter.Actor.Character.LaborPower).IsEqualTo(laborAfterFirst);
        await Assert.That(BagOnlyCount(crafter.Actor, MatAItemId)).IsEqualTo(0);
        await Assert.That(BankCount(crafter.Actor, MatAItemId)).IsEqualTo(MatAAmount);
    }

    // ------------------------------------------------------------ rig below

    private sealed record SetupVillager(VillageDayCycle.VillagerSpec Spec, GameplayActor Actor, HeadlessSession Session);

    private SetupVillager SetupFarmer(string name, uint? seedItemId = null)
    {
        var (actor, session) = CreateActorOnUniqueWorld(name);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        StockSeed(actor);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(actor.Character.Transform.World.Position);
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        return new SetupVillager(new VillageDayCycle.VillagerSpec
        {
            Name = name,
            Profession = VillageDayCycle.VillageProfession.Farmer,
            Actor = actor,
            Anchors = BotDailyAnchors.Template,
            CropObjId = crop.ObjId,
            FarmerSeedItemId = seedItemId ?? PotatoSeedId
        }, actor, session);
    }

    private SetupVillager SetupCrafter(string name, string cycleSuffix = "craft")
    {
        var (actor, session) = CreateActorOnUniqueWorld(name);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        StockBank(actor, MatAItemId, MatAAmount);
        StockBank(actor, MatBItemId, MatBAmount);
        return new SetupVillager(new VillageDayCycle.VillagerSpec
        {
            Name = name,
            Profession = VillageDayCycle.VillageProfession.Crafter,
            Actor = actor,
            Anchors = BotDailyAnchors.Template,
            CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
            {
                CycleId = $"m8v1-{cycleSuffix}",
                CraftId = CraftId,
                BenchObjId = benchObjId
            },
            CrafterPump = new VillagePump()
        }, actor, session);
    }

    private (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        return (actor, session);
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
        _registeredWorlds.Add(session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    private static void StockSeed(GameplayActor actor)
        => actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);

    private static void StockBank(GameplayActor actor, uint templateId, int count)
    {
        GameplayActorTestRig.GrantItem(actor, templateId, count);
        var deposit = actor.DepositItem(templateId);
        if (deposit.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"StockBank setup failed for {templateId}: {deposit.Detail}");
    }

    private static int BagOnlyCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Inventory, templateId);

    private static int BankCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

    private static int BankCount(HeadlessSession session, uint templateId)
        => session.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

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

    /// <summary>Headless craft pump (the CrafterWorkstationCycleTests shape).</summary>
    private sealed class VillagePump : ICrafterPump
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
