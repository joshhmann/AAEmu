using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C3 farmer v1 (ROADMAP M8 living-village contracts) — one owned-crop
/// cycle through <see cref="FarmerCycleScenario"/>: owned-plot precheck →
/// harvest (real <c>Doodad.Use</c> via the unchanged
/// <see cref="GameplayActor.Harvest"/>) → deposit yield into the bank
/// (Warehouse, unchanged <see cref="GameplayActor.DepositItem"/>) → replant
/// the approved potato seed (unchanged <see cref="GameplayActor.Plant"/>) →
/// canned shortage report (audit trail + return value).
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (missing precheck, harvest bypass, skipped deposit, phantom plant,
/// swallowed hold, deleted yield) and PASSES on the composed real paths.
/// Headless boundary honesty (the Plant M5.1 split): with MySQL on a dead
/// port the replant's persistence tail throws AFTER the seed is consumed, so
/// the replant leg is Interrupted — the test asserts the seed was consumed
/// exactly once, which is the conservation proof, not a Completed.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>] // housing-lane limiter: serialize against house/pack/harvest suites sharing WorldManager/singleton state
[NotInParallel] // process-wide MySQL.SetConfiguration + singleton state + AppConfiguration
public class FarmerCycleScenarioTests
{
    // Canonical potato ids (the slice allowlist is seed 15659 only).
    private const uint SeedId = CropHarvestLoopTests.PotatoSeedItemId;
    private const uint PotatoId = CropHarvestLoopTests.PotatoItemId;
    private const uint GoldenId = CropHarvestLoopTests.GoldenPotatoItemId;

    private static uint _nextWorldInstanceId = 0x8000_0000; // fresh base: 0x4 plant / 0x5 M3aM4 / 0x6 harvest+loadpack / 0x7 soak

    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        CropHarvestLoopRig.Seed();
        // Doodad.Save() must fail FAST and deterministically headless (the
        // Plant M5.1 dead-port convention): the replant engine path runs and
        // consumes one seed, then Interrupts at the persistence boundary.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        MySQL.SetConfiguration(null);
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task FarmerV1_HappyPath_HarvestDepositReplantConserves()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-happy-1");
        var crop = PlantMatureCrop(actor, session);
        var cropId = crop.ObjId;
        // Harvest through the real chain: canonical pack 6452 shape, banked.
        var result = FarmerCycleScenario.Run(actor, cropId,
            new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-happy-1" });

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.FailStage).IsEqualTo("");
        // Harvest through the real chain: canonical pack 6452 shape.
        await Assert.That(BankCount(session, PotatoId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BankCount(session, PotatoId)).IsLessThanOrEqualTo(4);
        await Assert.That(BankCount(session, GoldenId)).IsEqualTo(1);
        // Plot reset: the final phase deleted the crop.
        await Assert.That(session.World.GetDoodad(cropId)).IsNull();
        // Seed law over the leg: 4 → harvest grant +1 → replant consume −1.
        await Assert.That(result.Report.SeedsBeforeHarvest).IsEqualTo(4);
        await Assert.That(result.Report.SeedsAfterHarvest).IsEqualTo(5);
        await Assert.That(result.Report.SeedsAfterReplant).IsEqualTo(4);
        await Assert.That(result.Report.DepositHolds).IsEmpty();
        await Assert.That(result.Report.YieldDeposited.ContainsKey(PotatoId)).IsTrue();
        await Assert.That(result.Report.YieldDeposited.ContainsKey(GoldenId)).IsTrue();
        // Replant ran the engine path and stopped at the headless
        // persistence boundary with exactly one seed consumed.
        await Assert.That(result.Report.ReplantOutcome.Contains("Interrupted")).IsTrue();
        await Assert.That(result.Report.ReplantOutcome.Contains("persistence boundary")).IsTrue();
    }

    [Test]
    public async Task FarmerV1_Immature_RejectsStateTransition_NoSideEffects()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-immature-1");
        StockSeed(actor);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        PositionAtActor(actor, crop);
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SeedlingPhase);

        var result = FarmerCycleScenario.Run(actor, crop.ObjId,
            new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-immature-1" });

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("HARVEST");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(result.FailReason.Contains("not harvestable in phase")).IsTrue();
        // Nothing yielded, no deposit or plant attempted, crop untouched.
        await Assert.That(BagCount(actor, PotatoId)).IsEqualTo(0);
        await Assert.That(BagCount(actor, SeedId)).IsEqualTo(4);
        await Assert.That(session.World.GetDoodad(crop.ObjId)).IsNotNull();
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SeedlingPhase);
        await Assert.That(result.TraceRecords.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FarmerV1_ForeignCrop_RejectsPrecheck_EngineNeverEntered()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-foreign-1");
        var crop = PlantMatureCrop(actor, session);
        // Another character's crop: directly character-owned by a stranger.
        crop.OwnerType = DoodadOwnerType.Character;
        crop.OwnerId = 999_999u;

        var result = FarmerCycleScenario.Run(actor, crop.ObjId,
            new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-foreign-1" });

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PRECHECK");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(result.FailReason.Contains("no permission to harvest")).IsTrue();
        // The engine path was never entered: still mature, no yield.
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);
        await Assert.That(BagCount(actor, PotatoId)).IsEqualTo(0);
        await Assert.That(BagCount(actor, SeedId)).IsEqualTo(4);
        await Assert.That(result.TraceRecords).IsEmpty();
    }

    [Test]
    public async Task FarmerV1_DespawnScheduled_RejectsBeforeEngine()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-despawn-1");
        var crop = PlantMatureCrop(actor, session);
        crop.Despawn = DateTime.UtcNow.AddSeconds(5);

        var result = FarmerCycleScenario.Run(actor, crop.ObjId,
            new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-despawn-1" });

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("HARVEST");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(result.FailReason.Contains("scheduled for despawn")).IsTrue();
        await Assert.That(BagCount(actor, PotatoId)).IsEqualTo(0);
        await Assert.That(BagCount(actor, SeedId)).IsEqualTo(4);
    }

    [Test]
    public async Task FarmerV1_RestartMidCycle_NoDupNoLoss()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-restart-1");
        var crop = PlantMatureCrop(actor, session);
        var options = new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-restart-1" };

        var first = FarmerCycleScenario.Run(actor, crop.ObjId, options);
        await Assert.That(first.Passed).IsTrue();
        var potatoBanked = BankCount(session, PotatoId);
        var goldenBanked = BankCount(session, GoldenId);
        var potatoBag = BagCount(actor, PotatoId);
        var seeds = BagCount(actor, SeedId);

        // Restart with the same cycle id: the crop is gone (final phase
        // deleted it), so the rerun fails closed at PRECHECK — nothing
        // re-executes, nothing moves twice.
        var second = FarmerCycleScenario.Run(actor, crop.ObjId, options);

        await Assert.That(second.Passed).IsFalse();
        await Assert.That(second.FailStage).IsEqualTo("PRECHECK");
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(second.FailReason.Contains("not found in world")).IsTrue();
        await Assert.That(BankCount(session, PotatoId)).IsEqualTo(potatoBanked);
        await Assert.That(BankCount(session, GoldenId)).IsEqualTo(goldenBanked);
        await Assert.That(BagCount(actor, PotatoId)).IsEqualTo(potatoBag);
        await Assert.That(BagCount(actor, SeedId)).IsEqualTo(seeds);
    }

    [Test]
    public async Task FarmerV1_BankFull_HoldsWithReason_YieldKept()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-bankfull-1");
        var crop = PlantMatureCrop(actor, session);
        // One-slot bank, filled with a seed stack (the deposit-test precedent).
        actor.Character.Inventory.Warehouse.ContainerSize = 1;
        GameplayActorTestRig.StockItem(session, SeedId, 1);
        await Assert.That(actor.DepositItem(SeedId).State).IsEqualTo(ActorLifecycleState.Completed);

        var result = FarmerCycleScenario.Run(actor, crop.ObjId,
            new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-bankfull-1" });

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("DEPOSIT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Holds recorded with the engine reason; the yield stays in the bag
        // (never deleted, never vended).
        await Assert.That(result.Report.DepositHolds.Count).IsEqualTo(2);
        foreach (var hold in result.Report.DepositHolds)
            await Assert.That(hold.Contains("bank is full")).IsTrue();
        await Assert.That(BagCount(actor, PotatoId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BagCount(actor, GoldenId)).IsEqualTo(1);
        await Assert.That(BankCount(session, PotatoId)).IsEqualTo(0);
        // Seed arithmetic: setup 5 → rig plant −1 → filler +1 → filler
        // deposit moves the WHOLE stack (5) → harvest grant +1 → replant −1.
        await Assert.That(result.Report.SeedsAfterHarvest).IsEqualTo(1);
        await Assert.That(result.Report.SeedsAfterReplant).IsEqualTo(0);
    }

    [Test]
    public async Task FarmerV1_UnapprovedSeed_HoldsReplant_ShortageReported()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-unapproved-1");
        var crop = PlantMatureCrop(actor, session);

        var result = FarmerCycleScenario.Run(actor, crop.ObjId,
            new FarmerCycleScenario.FarmerCycleOptions
            {
                CycleId = "m8c3-unapproved-1",
                SeedItemId = GameplayActorTestRig.TestItemTemplateId // not the approved potato seed
            });

        // Harvest + deposits completed; only the replant held.
        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("REPLANT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(result.Report.ReplantOutcome.Contains("not on the approved list")).IsTrue();
        await Assert.That(BankCount(session, PotatoId)).IsGreaterThanOrEqualTo(2);
        // No phantom plant: no seed consumed for the held replant (the
        // potato-seed grant was deposited as ordinary yield under this option).
        await Assert.That(BagCount(actor, SeedId)).IsEqualTo(0);
        await Assert.That(BankCount(session, SeedId)).IsEqualTo(5);
    }

    [Test]
    public async Task FarmerV1_ShortageReport_ShapeAndDestination()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m8c3-report-1");
        var crop = PlantMatureCrop(actor, session);

        var result = FarmerCycleScenario.Run(actor, crop.ObjId,
            new FarmerCycleScenario.FarmerCycleOptions { CycleId = "m8c3-report-1" });

        await Assert.That(result.Passed).IsTrue();
        var report = result.Report;
        await Assert.That(report.CycleId).IsEqualTo("m8c3-report-1");
        await Assert.That(report.CropObjId).IsEqualTo(crop.ObjId);
        await Assert.That(report.HarvestOutcome.Contains("Completed")).IsTrue();
        await Assert.That(report.YieldDeposited[PotatoId]).IsGreaterThanOrEqualTo(2);
        await Assert.That(report.YieldDeposited[GoldenId]).IsEqualTo(1);
        await Assert.That(report.DepositHolds).IsEmpty();
        await Assert.That(report.Passed).IsTrue();
        // Audit-trail destination: harvest + 2 deposits + replant records,
        // each in the structured wire shape.
        await Assert.That(result.TraceRecords.Count).IsEqualTo(4);
        await Assert.That(result.TraceRecords[0].Action).IsEqualTo(ActorActionType.Harvest);
        await Assert.That(result.TraceRecords[^1].Action).IsEqualTo(ActorActionType.Plant);
        foreach (var record in result.TraceRecords)
        {
            var json = record.ToJson();
            await Assert.That(json.Contains("\"trace_id\"")).IsTrue();
            await Assert.That(json.Contains("\"action\"")).IsTrue();
            await Assert.That(json.Contains("\"result\"")).IsTrue();
            await Assert.That(json.Contains("\"state_changes\"")).IsTrue();
        }
    }

    // ------------------------------------------------------------- helpers

    private static (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);

        var uniqueWorldId = _nextWorldInstanceId++;
        var worldIdField = typeof(WorldInstance).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        worldIdField?.SetValue(session.World, uniqueWorldId);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, uniqueWorldId);

        return (actor, session);
    }

    /// <summary>Stocks seeds, plants on the rig house plot, and grows the
    /// crop to the mature phase through the REAL scheduled growth tasks.</summary>
    private static Doodad PlantMatureCrop(GameplayActor actor, HeadlessSession session)
    {
        StockSeed(actor);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        PositionAtActor(actor, crop);
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        return crop;
    }

    private static void StockSeed(GameplayActor actor)
        => actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);

    private static void PositionAtActor(GameplayActor actor, Doodad crop)
        => crop.Transform.Local.SetPosition(actor.Character.Transform.World.Position);

    private static int BagCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);

    private static int BankCount(HeadlessSession session, uint templateId)
        => session.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);
}
