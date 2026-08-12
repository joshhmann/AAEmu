using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
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
    /// FAIL-BEFORE (2026-08-11): the loader's current field order
    /// (SpawnManager.SpawnPersistentDoodads) assigns FuncGroupId while the
    /// doodad is already IsPersistent — the setter fires Save() mid-load.
    /// That boot-time write persists phase_time = DateTime.UtcNow (clobbering
    /// the stored phase start, so a SECOND restart drifts the growth clock by
    /// the previous boot time) and the fresh doodad's still-default position
    /// (0,0,0 — the real position is applied later in the loader). A restart
    /// must never write the row; the stored phase_time/position stay until
    /// the next real gameplay mutation.
    /// </summary>
    [Test]
    public async Task CurrentLoadOrder_OnPersistentDoodad_WritesRowMidLoad()
    {
        var storedPhaseTime = DateTime.UtcNow.AddMinutes(-3);
        var doodad = new CountingDoodad { IsPersistent = true, DbId = 4242 };

        // EXACT field order of SpawnManager.SpawnPersistentDoodads today:
        doodad.FuncGroupId = CropHarvestLoopTests.SmallPhase;   // setter fires Save() + PhaseTime = now
        doodad.OwnerId = 11;
        doodad.OwnerType = DoodadOwnerType.Housing;
        doodad.AttachPoint = AttachPointKind.None;
        doodad.PlantTime = storedPhaseTime.AddHours(-1);
        doodad.GrowthTime = storedPhaseTime.AddHours(-1);
        doodad.OverridePhaseTime = storedPhaseTime;
        doodad.PhaseTime = storedPhaseTime;
        doodad.SetScale(1f);
        doodad.SetData(0);
        doodad.FarmType = FarmType.Invalid;

        // A boot load must be read-only: no mid-load row write.
        await Assert.That(doodad.SaveCount).IsEqualTo(0);
        // And the (hypothetical) write must not carry a clobbered phase_time.
        await Assert.That(doodad.CapturedWrite is null || doodad.CapturedWrite.Value.PhaseTime == storedPhaseTime).IsTrue();
    }

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
        phaseFuncTemplates.TryAdd("DoodadFuncGrowth", new Dictionary<uint, DoodadPhaseFuncTemplate>
        {
            [PhaseStateRestartRecoveryTests.CalfGrowthFuncId] = new DoodadFuncGrowth
            {
                Delay = PhaseStateRestartRecoveryTests.CalfStartDelayMs,
                StartScale = 1000, EndScale = 1000,
                NextPhase = (int)PhaseStateRestartRecoveryTests.CalfGrowPhase
            },
            [792] = new DoodadFuncGrowth { Delay = 111_132_000, StartScale = 1000, EndScale = 1000, NextPhase = 12774 }
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
        phaseFuncTemplates.TryAdd("DoodadFuncTimer", new Dictionary<uint, DoodadPhaseFuncTemplate>
        {
            [PhaseStateRestartRecoveryTests.DoorRevertTimerFuncId] = new DoodadFuncTimer
            {
                Delay = PhaseStateRestartRecoveryTests.DoorRevertDelayMs,
                NextPhase = (int)PhaseStateRestartRecoveryTests.DoorClosedPhase
            },
            [3924] = new DoodadFuncTimer { Delay = 1600, NextPhase = (int)PhaseStateRestartRecoveryTests.DoorStartPhase }
        });
        phaseFuncTemplates.TryAdd("DoodadFuncAnimate", new Dictionary<uint, DoodadPhaseFuncTemplate>
        {
            [560] = new DoodadFuncAnimate { Name = "start", PlayOnce = false },
            [561] = new DoodadFuncAnimate { Name = "open", PlayOnce = false },
            [562] = new DoodadFuncAnimate { Name = "closed", PlayOnce = false },
            [563] = new DoodadFuncAnimate { Name = "open2", PlayOnce = false }
        });

        _ = funcsByGroups; // door/calf chains have no interaction funcs — phase funcs only
        _ = funcsById;
    }

    private static DoodadFuncGroups MakeGroup(uint id, DoodadFuncGroups.DoodadFuncGroupKind kind)
        => new() { Id = id, Almighty = 0, GroupKindId = kind };

    private static DoodadPhaseFunc P(uint groupId, uint funcId, string funcType)
        => new() { GroupId = groupId, FuncId = funcId, FuncType = funcType };

    private static object GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target);
}
