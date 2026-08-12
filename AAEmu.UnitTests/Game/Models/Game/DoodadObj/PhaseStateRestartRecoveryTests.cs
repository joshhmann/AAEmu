using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

/// <summary>
/// M3b-2 restart-cycle harness (ROADMAP M3b): door/window phase state +
/// crop/livestock recovery across a simulated server restart.
///
/// Canonical 1.2 chains (real compact.sqlite3 rows, verified 2026-08-11):
///   potato crop   2259: 4379 seedling → [growth 583: 60s] → 4456 small
///                       → [growth 584: 9min] → 4457 mature → harvest chain
///                       (constants reused from CropHarvestLoopTests)
///   dairy calf    2672: 5780 calf (start) → [growth 791: 12,348,000ms]
///                       → 5781 growing calf → [growth 792: 111,132,000ms]
///                       → 12774 mature cow (timer/milking chain)
///   house door    4278: 10521 (start) → 10522 open → [timer 3923: 1600ms]
///                       → 10563 closed → 10564 open → [timer 3924: 1600ms]
///                       → 10521 (toggles persist as FuncGroupId)
///
/// "Restart" is simulated honestly: the pre-restart doodad's persisted
/// state (templateId, phaseId, plant/growth/phase times, position) is
/// captured, a NEW doodad instance is created, and the loader's exact
/// field order is applied — the same contract SpawnManager reads from the
/// doodads table at boot. No MySQL in unit tests: the MySQL write tail is
/// represented by CountingDoodad (Save() override) so a mid-load write is
/// observable as a count + captured values.
///
/// Singleton discipline (t_4f11a519): seeds only what is missing; the
/// crop/door/calf chains are ADDITIVE on the existing DoodadManager.
/// </summary>
[NotInParallel] // touches process-wide SingletonContainer.ServiceProvider + singletons
public class PhaseStateRestartRecoveryTests
{
    // Dairy calf (livestock) — canonical 1.2
    internal const uint DairyCalfDoodadId = 2672;   // 젖소 송아지
    internal const uint CalfStartPhase = 5780;      // kind=1 start — growth 791: 12,348,000ms → 5781
    internal const uint CalfGrowPhase = 5781;       // growth 792: 111,132,000ms → 12774 (mature cow)
    internal const uint CalfGrowthFuncId = 791;
    internal const int CalfStartDelayMs = 12_348_000;

    // House door (door/window phase state) — canonical 1.2
    internal const uint DoorDoodadId = 4278;        // 문 (housing binding doodad)
    internal const uint DoorStartPhase = 10521;     // kind=1 start — animate 560
    internal const uint DoorOpenPhase = 10522;      // animate 561 + timer 3923: 1600ms → 10563
    internal const uint DoorClosedPhase = 10563;    // animate 562 (closed frame)
    internal const uint DoorOpen2Phase = 10564;     // animate 563 + timer 3924: 1600ms → 10521
    internal const uint DoorRevertTimerFuncId = 3923;
    internal const int DoorRevertDelayMs = 1600;

    // Potato wilt/spoil (FARM-01 rot chain, M3 canonical audit §2.2) — canonical 1.2
    internal const uint WiltedPhase = 10042;    // 소멸 전환 (wilt transition group)
    internal const uint SpoiledPhase = 6112;    // 변질된 감자 (spoiled potato)
    internal const uint WiltTimerFuncId = 3403; // wilted group timer: 500 ms → spoiled
    internal const int WiltTimerDelayMs = 500;
    internal const uint SpoilTimerFuncId = 1352; // spoiled timer: 48 h → final 4459 (despawn)
    internal const int SpoilDelayMs = 172_800_000;

    private WorldConfig _previousWorldConfig;
    private GameplayActor _actor;
    private HeadlessSession _session;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig(); // GrowthRate 1.0

        PhaseStateRestartRecoveryRig.Seed();

        (_actor, _session) = GameplayActorTestRig.CreateActor("restart-farmer");
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    /// <summary>
    /// PASS-AFTER: the fixed loader path (ApplyLoadedState) is read-only —
    /// no row write at boot, stored phase_time survives untouched, phase and
    /// owner state land on the instance.
    /// </summary>
    [Test]
    public async Task ApplyLoadedState_OnPersistentDoodad_IsReadOnly_AndPreservesPhaseTime()
    {
        var storedPhaseTime = DateTime.UtcNow.AddMinutes(-3);
        var doodad = new CountingDoodad { IsPersistent = true, DbId = 4242 };

        doodad.ApplyLoadedState(
            4242, CropHarvestLoopTests.SmallPhase,
            storedPhaseTime.AddHours(-1), storedPhaseTime.AddHours(-1), storedPhaseTime,
            11, DoodadOwnerType.Housing, AttachPointKind.None,
            itemId: 0, ownerDbId: 77, scale: 1f, data: 0, FarmType.Invalid);

        await Assert.That(doodad.SaveCount).IsEqualTo(0);          // no boot-time write
        await Assert.That(doodad.CapturedWrite).IsNull();
        await Assert.That(doodad.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SmallPhase);
        await Assert.That(doodad.PhaseTime).IsEqualTo(storedPhaseTime); // no clobber
        await Assert.That(doodad.OverridePhaseTime).IsEqualTo(storedPhaseTime);
        await Assert.That(doodad.OwnerType).IsEqualTo(DoodadOwnerType.Housing);
        await Assert.That(doodad.OwnerDbId).IsEqualTo(77u);
    }

    /// <summary>
    /// The load suppression must NOT leak into gameplay: once loaded, a real
    /// phase change (door open/close, crop growth step) still persists the
    /// row and resets the phase clock — exactly once per change.
    /// </summary>
    [Test]
    public async Task ApplyLoadedState_ThenGameplayPhaseChange_SavesExactlyOnce()
    {
        var storedPhaseTime = DateTime.UtcNow.AddMinutes(-3);
        var doodad = new CountingDoodad { IsPersistent = true, DbId = 4242 };
        doodad.ApplyLoadedState(
            4242, CropHarvestLoopTests.SeedlingPhase,
            storedPhaseTime.AddHours(-1), storedPhaseTime.AddHours(-1), storedPhaseTime,
            11, DoodadOwnerType.Housing, AttachPointKind.None,
            itemId: 0, ownerDbId: 77, scale: 1f, data: 0, FarmType.Invalid);

        doodad.FuncGroupId = CropHarvestLoopTests.SmallPhase; // gameplay phase change

        await Assert.That(doodad.SaveCount).IsEqualTo(1);     // persisted once
        await Assert.That(doodad.PhaseTime).IsGreaterThan(storedPhaseTime); // new phase clock
    }

    /// <summary>
    /// Restart cycle — crop: plant → grow to "small" (4456) → server restart
    /// (fresh doodad instance, loader field order) → the growth timer resumes
    /// with the REMAINING time (delay − elapsed), not the full delay and not
    /// a reset; executing the resumed task reaches the mature phase (4457).
    /// </summary>
    [Test]
    public async Task MidGrowthCrop_Restart_ResumesTimerWithRemainingTime()
    {
        var house = CropHarvestLoopRig.MakeHouse(_actor.Character);
        _actor.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.DoodadCreate, CropHarvestLoopTests.PotatoSeedItemId, 5);
        var planted = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house);
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute(); // → small phase

        await Assert.That(planted.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SmallPhase);

        // ---- restart: new process would read the row and build a new instance ----
        var recovered = SimulateRestartLoad(planted, house);

        await Assert.That(recovered).IsNotEqualTo(planted);               // genuinely new instance
        await Assert.That(recovered.DbId).IsEqualTo(planted.DbId);        // same row id (REPLACE, no dup row)
        await Assert.That(recovered.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SmallPhase);
        await Assert.That(recovered.PhaseTime).IsEqualTo(planted.PhaseTime); // stored phase start, unclobbered
        await Assert.That(recovered.PlantTime).IsEqualTo(planted.PlantTime);
        await Assert.That(recovered.FuncTask is DoodadFuncGrowthTask).IsTrue(); // timer resumed

        // 9 min small-phase delay minus the few ms the test took: remaining ≈ 9 min
        var remaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsGreaterThan(539_000);
        await Assert.That(remaining).IsLessThanOrEqualTo(540_500);

        // Growth continues after restart: executing the resumed task matures the crop
        (recovered.FuncTask as DoodadFuncGrowthTask)!.Execute();
        await Assert.That(recovered.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);
    }

    /// <summary>
    /// Restart cycle — overdue crop: the server was down longer than the
    /// phase delay (phase_time older than 9 min while the small-phase delay
    /// is 9 min). On load the catch-up clamps to 1 ms — the crop recovers to
    /// the correct NEXT growth stage immediately instead of waiting a full
    /// fresh delay.
    /// </summary>
    [Test]
    public async Task OverdueCrop_Restart_CatchesUpToMatureImmediately()
    {
        var house = CropHarvestLoopRig.MakeHouse(_actor.Character);
        _actor.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.DoodadCreate, CropHarvestLoopTests.PotatoSeedItemId, 5);
        var planted = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house);
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute(); // → small phase

        // Simulate downtime: the stored phase start predates the delay by 60s
        planted.PhaseTime = DateTime.UtcNow.AddMinutes(-10);
        planted.OverridePhaseTime = planted.PhaseTime;

        var recovered = SimulateRestartLoad(planted, house);

        var remaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsLessThanOrEqualTo(2_000); // clamped catch-up, no fresh 9-min wait

        (recovered.FuncTask as DoodadFuncGrowthTask)!.Execute();
        await Assert.That(recovered.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);
    }

    /// <summary>
    /// Restart cycle — livestock (dairy calf 2672): calf stage 5780 has a
    /// 12,348,000 ms growth func. A calf 1 h into that stage recovers with
    /// ~8,748,000 ms remaining and continues to 5781 on the resumed task.
    /// </summary>
    [Test]
    public async Task LivestockCalf_Restart_RecoversGrowthStage()
    {
        var calf = NewChainDoodad(PhaseStateRestartRecoveryTests.DairyCalfDoodadId, PhaseStateRestartRecoveryTests.CalfStartPhase);
        calf.PlantTime = DateTime.UtcNow.AddHours(-2);
        calf.PhaseTime = DateTime.UtcNow.AddMinutes(-60); // 1 h into the 3.43 h calf stage
        calf.OverridePhaseTime = calf.PhaseTime;

        var recovered = SimulateRestartLoad(calf, house: null);

        await Assert.That(recovered.FuncGroupId).IsEqualTo(PhaseStateRestartRecoveryTests.CalfStartPhase);
        await Assert.That(recovered.PhaseTime).IsEqualTo(calf.PhaseTime);
        await Assert.That(recovered.FuncTask is DoodadFuncGrowthTask).IsTrue();

        // 12,348,000 ms delay − 3,600,000 ms elapsed = 8,748,000 ms remaining
        var remaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsGreaterThan(8_745_000);
        await Assert.That(remaining).IsLessThanOrEqualTo(8_750_000);

        (recovered.FuncTask as DoodadFuncGrowthTask)!.Execute();
        await Assert.That(recovered.FuncGroupId).IsEqualTo(PhaseStateRestartRecoveryTests.CalfGrowPhase);
    }

    /// <summary>
    /// Restart cycle — door/window phase state: the house door 4278 toggles
    /// open (10522) with a 1.6 s auto-revert timer to the closed frame
    /// (10563). A door open at restart restores phase 10522 AND the revert
    /// timer's remaining time; executing it closes the door.
    /// </summary>
    [Test]
    public async Task Door_Restart_RestoresOpenPhase_AndSchedulesRevert()
    {
        var door = NewChainDoodad(PhaseStateRestartRecoveryTests.DoorDoodadId, PhaseStateRestartRecoveryTests.DoorOpenPhase);
        door.PlantTime = DateTime.UtcNow.AddHours(-1);
        door.PhaseTime = DateTime.UtcNow.AddMilliseconds(-800); // 800 ms into the 1.6 s revert
        door.OverridePhaseTime = door.PhaseTime;

        var recovered = SimulateRestartLoad(door, house: null);

        await Assert.That(recovered.FuncGroupId).IsEqualTo(PhaseStateRestartRecoveryTests.DoorOpenPhase); // open state survives
        await Assert.That(recovered.PhaseTime).IsEqualTo(door.PhaseTime);
        await Assert.That(recovered.FuncTask is DoodadFuncTimerTask).IsTrue(); // revert timer resumed

        var remaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsGreaterThan(700);   // ≈ 800 ms left of the 1.6 s revert
        await Assert.That(remaining).IsLessThanOrEqualTo(1_600);

        (recovered.FuncTask as DoodadFuncTimerTask)!.Execute();
        await Assert.That(recovered.FuncGroupId).IsEqualTo(PhaseStateRestartRecoveryTests.DoorClosedPhase); // door closes
    }

    /// <summary>
    /// Restart cycle — door overdue: the server was down longer than the
    /// revert window, so on load the door recovers straight to the closed
    /// frame (correct state, no stale open phase).
    /// </summary>
    [Test]
    public async Task Door_OverdueRestart_RevertsToClosedImmediately()
    {
        var door = NewChainDoodad(PhaseStateRestartRecoveryTests.DoorDoodadId, PhaseStateRestartRecoveryTests.DoorOpenPhase);
        door.PlantTime = DateTime.UtcNow.AddHours(-1);
        door.PhaseTime = DateTime.UtcNow.AddSeconds(-5); // overdue vs the 1.6 s revert
        door.OverridePhaseTime = door.PhaseTime;

        var recovered = SimulateRestartLoad(door, house: null);

        var remaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsLessThanOrEqualTo(2_000);

        (recovered.FuncTask as DoodadFuncTimerTask)!.Execute();
        await Assert.That(recovered.FuncGroupId).IsEqualTo(PhaseStateRestartRecoveryTests.DoorClosedPhase);
    }

    /// <summary>
    /// FARM-01 rot pin (M3 canonical audit §2.2): an unharvested mature
    /// potato (4457) carries DoodadFuncTimer 1350 (174,000,000 ms = 48.33 h →
    /// wilted phase 10042). Entering the mature phase arms the rot timer —
    /// the crop does NOT stay mature forever.
    /// </summary>
    [Test]
    public async Task UnharvestedMatureCrop_ArmsFortyEightHourRotTimer()
    {
        var house = CropHarvestLoopRig.MakeHouse(_actor.Character);
        _actor.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.DoodadCreate, CropHarvestLoopTests.PotatoSeedItemId, 5);
        var planted = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house);
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute(); // seedling → small
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute(); // small → mature

        await Assert.That(planted.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);

        // The mature phase's phase funcs armed the rot timer (1350), not a growth task
        var rotTimer = planted.FuncTask as DoodadFuncTimerTask;
        await Assert.That(rotTimer).IsNotNull();

        // 48.33 h = 174,000,000 ms of rot window from the mature-phase entry
        var remaining = (planted.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsGreaterThan(173_998_000);
        await Assert.That(remaining).IsLessThanOrEqualTo(174_000_000);
    }

    /// <summary>
    /// FARM-01 rot pin (M3 canonical audit §2.2): a mature crop left past the
    /// 48.33 h rot window transitions to the wilted phase (10042) when the
    /// overdue timer fires — elapsed phase_time simulated via the restart-load
    /// path (same as the door-overdue test). The wilted group's
    /// DoodadFuncRatioChange funcs (408 → recover to 4457, 409 → spoiled 6112)
    /// roll a random 0-9999 chance against Ratio 5000 in DoPhaseFuncs;
    /// CumulativePhaseRatio (the engine's public shift for those rolls) is
    /// forced above the ratio so BOTH checks deterministically fail and the
    /// chain's deterministic leg — timer 3403 (500 ms) → spoiled 6112 — fires.
    /// The spoiled phase then arms the 48 h despawn timer (1352 → final 4459).
    /// </summary>
    [Test]
    public async Task OverdueMatureCrop_RotTimer_TransitionsToWiltedThenSpoiled()
    {
        var house = CropHarvestLoopRig.MakeHouse(_actor.Character);
        _actor.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.DoodadCreate, CropHarvestLoopTests.PotatoSeedItemId, 5);
        var planted = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house);
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute();
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute();
        await Assert.That(planted.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);

        // Server was down 49 h: the mature phase's phase_time predates the
        // 48.33 h rot window → on load the rot timer clamps to a 1 ms catch-up.
        planted.PhaseTime = DateTime.UtcNow.AddHours(-49);
        planted.OverridePhaseTime = planted.PhaseTime;

        var recovered = SimulateRestartLoad(planted, house);

        var remaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsLessThanOrEqualTo(2_000); // clamped catch-up, no fresh 48.33 h wait

        recovered.CumulativePhaseRatio = 10_000; // wilt checks deterministically fail (see docblock)

        (recovered.FuncTask as DoodadFuncTimerTask)!.Execute(); // rot timer fires (48.33 h elapsed)

        // Wilted phase entered (소멸 전환)
        await Assert.That(recovered.FuncGroupId).IsEqualTo(WiltedPhase);

        // The wilted group armed its own 500 ms timer (3403 → spoiled 6112)
        await Assert.That(recovered.FuncTask is DoodadFuncTimerTask).IsTrue();
        var wiltRemaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(wiltRemaining).IsGreaterThan(0);
        await Assert.That(wiltRemaining).IsLessThanOrEqualTo(1_500);

        (recovered.FuncTask as DoodadFuncTimerTask)!.Execute(); // 500 ms later → spoiled

        await Assert.That(recovered.FuncGroupId).IsEqualTo(SpoiledPhase); // 변질된 감자

        // The spoiled phase arms the 48 h despawn timer (1352 → final 4459)
        await Assert.That(recovered.FuncTask is DoodadFuncTimerTask).IsTrue();
        var spoilRemaining = (recovered.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(spoilRemaining).IsGreaterThan(172_798_000);
        await Assert.That(spoilRemaining).IsLessThanOrEqualTo(172_800_000);
    }

    /// <summary>
    /// Full restart cycle — place → grow → restart → recover → harvest with
    /// NO duplication: the recovered crop shares the persisted row id (the
    /// loader never re-inserts), yields exactly once after restart, and the
    /// plot resets (doodad removed from the world).
    /// </summary>
    [Test]
    public async Task FullRestartCycle_PlantGrowRestartHarvest_NoDuplication()
    {
        var house = CropHarvestLoopRig.MakeHouse(_actor.Character);
        _actor.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.DoodadCreate, CropHarvestLoopTests.PotatoSeedItemId, 5);

        // Session 1: place + grow to small
        var planted = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house);
        (planted.FuncTask as DoodadFuncGrowthTask)?.Execute();
        await Assert.That(planted.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SmallPhase);

        // ---- restart ----
        var recovered = SimulateRestartLoad(planted, house);
        await Assert.That(recovered.DbId).IsEqualTo(planted.DbId); // one row, stable id

        // Grow out after restart (small → mature)
        (recovered.FuncTask as DoodadFuncGrowthTask)!.Execute();
        await Assert.That(recovered.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);

        // Harvest after restart: exactly one yield
        var potatoBefore = BagCount(CropHarvestLoopTests.PotatoItemId);
        recovered.Use(_actor.Character, CropHarvestLoopTests.HarvestSkillId);
        await Assert.That(BagCount(CropHarvestLoopTests.PotatoItemId)).IsGreaterThanOrEqualTo(potatoBefore + 2);
        await Assert.That(BagCount(CropHarvestLoopTests.PotatoItemId)).IsLessThanOrEqualTo(potatoBefore + 4);

        // No double yield on a second interaction (terminal phase is a no-op)
        var potatoAfter = BagCount(CropHarvestLoopTests.PotatoItemId);
        recovered.Use(_actor.Character, CropHarvestLoopTests.HarvestSkillId);
        await Assert.That(BagCount(CropHarvestLoopTests.PotatoItemId)).IsEqualTo(potatoAfter);

        // Plot reset: the recovered crop is gone from the world
        await Assert.That(_session.World.GetDoodad(recovered.ObjId)).IsNull();
    }

    /// <summary>
    /// Builds a fresh doodad in a chain phase WITHOUT planting (door/calf
    /// chains have no item path) and registers the session world so the
    /// ParentWorld/Transform chains resolve.
    /// </summary>
    private Doodad NewChainDoodad(uint templateId, uint phaseId)
    {
        RegisterWorld(_session.World);
        var doodad = DoodadManager.Instance.Create(_session.World, 0, templateId, null, true);
        doodad.Transform = _actor.Character.Transform.CloneDetached(doodad);
        doodad.Transform.InstanceId = _session.World.Id;
        doodad.Transform.Local.SetPosition(2000f, 2000f, 100f);
        doodad.IsPersistent = false; // unit tests: no MySQL save tail
        doodad.FuncGroupId = phaseId;
        return doodad;
    }

    /// <summary>
    /// Applies the loader's exact restart sequence to a fresh instance:
    /// Create → IsPersistent + ApplyLoadedState → (house parenting when the
    /// original was house-bound) → position → InitDoodad. Mirrors
    /// SpawnManager.SpawnPersistentDoodads.
    /// </summary>
    private Doodad SimulateRestartLoad(Doodad source, House house)
    {
        RegisterWorld(_session.World);
        var doodad = DoodadManager.Instance.Create(_session.World, 0, source.TemplateId, null, true);
        doodad.IsPersistent = false; // unit tests: the MySQL tail is the CountingDoodad concern
        doodad.ApplyLoadedState(
            source.DbId, source.FuncGroupId,
            source.PlantTime, source.GrowthTime, source.PhaseTime,
            source.OwnerId, source.OwnerType, source.AttachPoint,
            source.ItemId, source.OwnerDbId, source.Scale, source.Data, source.FarmType);

        doodad.Transform = source.Transform.CloneDetached(doodad);
        doodad.Transform.InstanceId = _session.World.Id;
        if (house != null)
        {
            doodad.ParentObj = house;
            doodad.ParentObjId = house.ObjId;
            doodad.Transform.Parent = house.Transform;
        }
        doodad.Transform.Local.SetPosition(source.Transform.Local.Position);
        var r = source.Transform.Local.Rotation;
        doodad.Transform.Local.SetRotation(r.X, r.Y, r.Z);

        doodad.InitDoodad();
        doodad.Spawn(); // the loader spawns slave doodads; the rig spawns so world lookups work
        _session.World.SpawnManager?.AddPlayerDoodad(doodad);
        return doodad;
    }

    private static void RegisterWorld(WorldInstance world)
    {
        if (world.Regions == null)
        {
            world.Regions = new Region[
                world.Template.CellX * WorldManager.SECTORS_PER_CELL,
                world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        }
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(world.Id, world);
    }

    private int BagCount(uint templateId)
        => _actor.Character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);

    /// <summary>
    /// Doodad subclass that replaces the MySQL write tail with an in-memory
    /// record, so a load-path Save() call is observable without a database.
    /// </summary>
    private sealed class CountingDoodad : Doodad
    {
        public int SaveCount { get; private set; }
        public (DateTime PhaseTime, Vector3 Position)? CapturedWrite { get; private set; }

        public override void Save()
        {
            SaveCount++;
            CapturedWrite = (PhaseTime, Transform?.Local.Position ?? Vector3.Zero);
        }
    }
}

/// <summary>
/// Seeding for the restart-cycle harness. Additive on CropHarvestLoopRig:
/// potato + base surface first (missing-only), then the livestock/door
/// chains registered into the SAME DoodadManager.
/// </summary>
public static class PhaseStateRestartRecoveryRig
{
    private static bool s_seeded;

    public static void Seed()
    {
        lock (typeof(PhaseStateRestartRecoveryRig))
        {
            if (s_seeded)
                return;

            CropHarvestLoopRig.Seed(); // potato + base surface (missing-only)
            SeedDoodadManager();

            s_seeded = true;
        }
    }

    private static void SeedDoodadManager()
    {
        var manager = DoodadManager.Instance;
        var templates = (Dictionary<uint, DoodadTemplate>)GetField(manager, "_templates");
        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)GetField(manager, "_funcsByGroups");
        var funcsById = (Dictionary<uint, DoodadFunc>)GetField(manager, "_funcsById");
        var phaseFuncs = (Dictionary<uint, List<DoodadPhaseFunc>>)GetField(manager, "_phaseFuncs");
        var phaseFuncTemplates = (Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>)GetField(manager, "_phaseFuncTemplates");

        // --- Dairy calf 2672: calf (5780) → growing calf (5781) → mature cow ---
        templates.TryAdd(PhaseStateRestartRecoveryTests.DairyCalfDoodadId, new DoodadTemplate
        {
            Id = PhaseStateRestartRecoveryTests.DairyCalfDoodadId,
            GrowthTime = 0,
            TotalDoodadGrowthTime = 0,
            FuncGroups =
            [
                MakeGroup(PhaseStateRestartRecoveryTests.CalfStartPhase, DoodadFuncGroups.DoodadFuncGroupKind.Start),
                MakeGroup(PhaseStateRestartRecoveryTests.CalfGrowPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal)
            ]
        });
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.CalfStartPhase,
            [P(PhaseStateRestartRecoveryTests.CalfStartPhase, PhaseStateRestartRecoveryTests.CalfGrowthFuncId, "DoodadFuncGrowth")]);
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.CalfGrowPhase,
            [P(PhaseStateRestartRecoveryTests.CalfGrowPhase, 792, "DoodadFuncGrowth")]);
        // Inner dicts may already exist (potato rig registers DoodadFuncGrowth/Timer) — add INTO them
        phaseFuncTemplates.TryAdd("DoodadFuncGrowth", new Dictionary<uint, DoodadPhaseFuncTemplate>());
        phaseFuncTemplates["DoodadFuncGrowth"].TryAdd(PhaseStateRestartRecoveryTests.CalfGrowthFuncId, new DoodadFuncGrowth
        {
            Delay = PhaseStateRestartRecoveryTests.CalfStartDelayMs,
            StartScale = 1000, EndScale = 1000,
            NextPhase = (int)PhaseStateRestartRecoveryTests.CalfGrowPhase
        });
        phaseFuncTemplates["DoodadFuncGrowth"].TryAdd(792, new DoodadFuncGrowth
        {
            Delay = 111_132_000, StartScale = 1000, EndScale = 1000, NextPhase = 12774
        });

        // --- House door 4278: start → open (auto-revert 1.6s) → closed frame → open (auto-revert) ---
        templates.TryAdd(PhaseStateRestartRecoveryTests.DoorDoodadId, new DoodadTemplate
        {
            Id = PhaseStateRestartRecoveryTests.DoorDoodadId,
            GrowthTime = 0,
            TotalDoodadGrowthTime = 0,
            FuncGroups =
            [
                MakeGroup(PhaseStateRestartRecoveryTests.DoorStartPhase, DoodadFuncGroups.DoodadFuncGroupKind.Start),
                MakeGroup(PhaseStateRestartRecoveryTests.DoorOpenPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
                MakeGroup(PhaseStateRestartRecoveryTests.DoorClosedPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
                MakeGroup(PhaseStateRestartRecoveryTests.DoorOpen2Phase, DoodadFuncGroups.DoodadFuncGroupKind.Normal)
            ]
        });
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.DoorOpenPhase,
        [
            P(PhaseStateRestartRecoveryTests.DoorOpenPhase, 561, "DoodadFuncAnimate"),
            P(PhaseStateRestartRecoveryTests.DoorOpenPhase, PhaseStateRestartRecoveryTests.DoorRevertTimerFuncId, "DoodadFuncTimer")
        ]);
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.DoorClosedPhase,
            [P(PhaseStateRestartRecoveryTests.DoorClosedPhase, 562, "DoodadFuncAnimate")]);
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.DoorOpen2Phase,
        [
            P(PhaseStateRestartRecoveryTests.DoorOpen2Phase, 563, "DoodadFuncAnimate"),
            P(PhaseStateRestartRecoveryTests.DoorOpen2Phase, 3924, "DoodadFuncTimer")
        ]);
        phaseFuncTemplates.TryAdd("DoodadFuncTimer", new Dictionary<uint, DoodadPhaseFuncTemplate>());
        phaseFuncTemplates["DoodadFuncTimer"].TryAdd(PhaseStateRestartRecoveryTests.DoorRevertTimerFuncId, new DoodadFuncTimer
        {
            Delay = PhaseStateRestartRecoveryTests.DoorRevertDelayMs,
            NextPhase = (int)PhaseStateRestartRecoveryTests.DoorClosedPhase
        });
        phaseFuncTemplates["DoodadFuncTimer"].TryAdd(3924, new DoodadFuncTimer
        {
            Delay = 1600, NextPhase = (int)PhaseStateRestartRecoveryTests.DoorStartPhase
        });
        phaseFuncTemplates.TryAdd("DoodadFuncAnimate", new Dictionary<uint, DoodadPhaseFuncTemplate>());
        phaseFuncTemplates["DoodadFuncAnimate"].TryAdd(560, new DoodadFuncAnimate { Name = "start", PlayOnce = false });
        phaseFuncTemplates["DoodadFuncAnimate"].TryAdd(561, new DoodadFuncAnimate { Name = "open", PlayOnce = false });
        phaseFuncTemplates["DoodadFuncAnimate"].TryAdd(562, new DoodadFuncAnimate { Name = "closed", PlayOnce = false });
        phaseFuncTemplates["DoodadFuncAnimate"].TryAdd(563, new DoodadFuncAnimate { Name = "open2", PlayOnce = false });

        // --- Potato wilt/spoil (FARM-01 rot chain) — canonical 1.2 ---
        // mature 4457 phase funcs already carry Timer 1350 (174,000,000 ms →
        // 10042, rigged by CropHarvestLoopRig). The wilted group 10042 (소멸
        // 전환) carries: RatioChange 408 (ratio 5000 → 4457 recover) +
        // RatioChange 409 (ratio 5000 → 6112 spoil) + Timer 3403 (500 ms →
        // 6112). Spoiled 6112 (변질된 감자) carries Timer 1352 (172,800,000 ms
        // → final 4459, despawn). The ratio funcs roll a random chance in
        // DoPhaseFuncs; tests pin the deterministic timer leg via
        // CumulativePhaseRatio (see OverdueMatureCrop_RotTimer...).
        if (templates.TryGetValue(CropHarvestLoopTests.PotatoDoodadId, out var potato))
        {
            potato.FuncGroups.Add(MakeGroup(PhaseStateRestartRecoveryTests.WiltedPhase,
                DoodadFuncGroups.DoodadFuncGroupKind.Normal, CropHarvestLoopTests.PotatoDoodadId));
            potato.FuncGroups.Add(MakeGroup(PhaseStateRestartRecoveryTests.SpoiledPhase,
                DoodadFuncGroups.DoodadFuncGroupKind.Normal, CropHarvestLoopTests.PotatoDoodadId));
        }
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.WiltedPhase,
        [
            P(PhaseStateRestartRecoveryTests.WiltedPhase, 408, "DoodadFuncRatioChange"),
            P(PhaseStateRestartRecoveryTests.WiltedPhase, 409, "DoodadFuncRatioChange"),
            P(PhaseStateRestartRecoveryTests.WiltedPhase, PhaseStateRestartRecoveryTests.WiltTimerFuncId, "DoodadFuncTimer")
        ]);
        phaseFuncs.TryAdd(PhaseStateRestartRecoveryTests.SpoiledPhase,
            [P(PhaseStateRestartRecoveryTests.SpoiledPhase, PhaseStateRestartRecoveryTests.SpoilTimerFuncId, "DoodadFuncTimer")]);
        phaseFuncTemplates.TryAdd("DoodadFuncRatioChange", new Dictionary<uint, DoodadPhaseFuncTemplate>());
        phaseFuncTemplates["DoodadFuncRatioChange"].TryAdd(408, new DoodadFuncRatioChange
        {
            Ratio = 5000,
            NextPhase = (int)CropHarvestLoopTests.MaturePhase
        });
        phaseFuncTemplates["DoodadFuncRatioChange"].TryAdd(409, new DoodadFuncRatioChange
        {
            Ratio = 5000,
            NextPhase = (int)PhaseStateRestartRecoveryTests.SpoiledPhase
        });
        phaseFuncTemplates["DoodadFuncTimer"].TryAdd(PhaseStateRestartRecoveryTests.WiltTimerFuncId, new DoodadFuncTimer
        {
            Delay = PhaseStateRestartRecoveryTests.WiltTimerDelayMs,
            NextPhase = (int)PhaseStateRestartRecoveryTests.SpoiledPhase
        });
        phaseFuncTemplates["DoodadFuncTimer"].TryAdd(PhaseStateRestartRecoveryTests.SpoilTimerFuncId, new DoodadFuncTimer
        {
            Delay = PhaseStateRestartRecoveryTests.SpoilDelayMs,
            NextPhase = (int)CropHarvestLoopTests.FinalPhase
        });

        _ = funcsByGroups; // door/calf/wilt chains have no interaction funcs — phase funcs only
        _ = funcsById;
    }

    private static DoodadFuncGroups MakeGroup(uint id, DoodadFuncGroups.DoodadFuncGroupKind kind, uint almighty = 0)
        => new() { Id = id, Almighty = almighty, GroupKindId = kind };

    private static DoodadPhaseFunc P(uint groupId, uint funcId, string funcType)
        => new() { GroupId = groupId, FuncId = funcId, FuncType = funcType };

    private static object GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target);
}
