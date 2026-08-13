using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 (t_a69e4998): Plant on the IGameplayActor v2 surface through the
/// REAL engine path — DoodadManager.CreatePlayerDoodad, the exact call the
/// CSCreateDoodadPacket handler makes:
///  - seed resolved through ordinary inventory services (Bag lookup by
///    template), plantability from the canonical item_spawn_doodads mapping,
///  - placement gates mirrored from the packet: use-skill labor cost,
///    public-farm CanPlace (zeroes labor), owned-land AllowedToInteract
///    (zeroes labor), labor gate + charge,
///  - the engine consumes the seed (ItemUse + ConsumeItem), spawns the
///    growing-crop doodad, and persists it (Doodad.Save — the only tail unit
///    tests cannot reach: MySQL is pointed at a dead port (M3b convention)
///    so the write fails fast and deterministically).
///
/// Headless boundary honesty: with MySQL unreachable the engine call throws
/// at the persistence write AFTER the placement landed in-memory (seed
/// consumed, crop doodad spawned). The actor converts that boundary into
/// Interrupted — NOT Rejected — because the B1 invariant is "Rejected ⇒
/// nothing applied" and here the effect WAS applied and the outcome is
/// ambiguous. Interrupted locks the idempotency key (same rule as
/// Interrupted/TimedOut after a timeout ambiguity), which is the
/// "retries must not duplicate" guarantee under test: a same-key retry is
/// refused pre-flight and the seed is consumed exactly once per logical
/// plant. The full Completed + persistent-row path rides the real MySQL
/// stack (Phase 2 M3a/M4 economic replay + the M4_2TradePackRestartE2eTests
/// rig family) — the same split the B1/M5.1 sibling cards use.
///
/// All assertions run headless — no controller, no client, no packets
/// required (Unit.SendPacket is null-safe without a Connection).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel] // process-wide MySQL.SetConfiguration + singleton state
public class GameplayActorPlantActionsTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    private WorldConfig _previousWorldConfig;
    private readonly List<House> _housesAdded = [];

    [Before(Test)]
    public void SetUp()
    {
        // InitDoodad reads World.GrowthRate; the headless host has no config
        // (same pattern as CropHarvestLoopTests).
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();

        GameplayActorTestRig.SeedPlantSurface();

        // Doodad.Save() must fail FAST and deterministically headless: a dead
        // port turns the MySQL write into an immediate MySqlException instead
        // of a localhost:3306 attempt (which could hit a real dev MySQL).
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });

        SetFarmGate(false);
        SetFarmAllowlist(false);
    }

    [After(Test)]
    public void TearDown()
    {
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
        AppConfiguration.Instance.World = _previousWorldConfig;
        SetFarmGate(false);
        SetFarmAllowlist(false);
        RemoveHouses();
    }

    // ================================================================ happy path — real engine path

    [Test]
    public async Task Plant_SeedOnOwnedLand_DrivesRealEnginePath_SpawnsCropAndConsumesSeedOnce()
    {
        var (actor, session) = CreateActor("m51-plant-1");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 5);
        AddHouse(actor.Character, ownerId: actor.Character.Id); // owned, finished house at TestPosition
        var laborBefore = actor.Character.LaborPower;

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        // The REAL engine path ran: the crop doodad was spawned in-world and
        // the seed was consumed by the engine. The only headless boundary is
        // the persistence tail — the request is Interrupted (ambiguous
        // outcome), never Rejected (Rejected ⇒ nothing applied).
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(request.Detail?.Contains("persistence boundary")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(4);
        // Owned land zeroes the use-skill labor cost (packet mirror).
        await Assert.That(actor.Character.LaborPower).IsEqualTo(laborBefore);
        await Assert.That(session.World.GetDoodad(GameplayActorTestRig.TestDoodadObjId)).IsNotNull();

        // Full audit record shape for the terminal transition.
        var record = actor.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Plant);
        await Assert.That(record.TargetId).IsEqualTo(GameplayActorTestRig.TestSeedItemId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (planting"))).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Interrupted")).IsTrue();
    }

    [Test]
    public async Task Plant_SeedOnUnclaimedLand_ChargesLaborExactlyOnce()
    {
        var (actor, session) = CreateActor("m51-plant-2");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        actor.Character.LaborPower = 50;

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        // Labor charged exactly once, before the engine call (packet mirror).
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(50 - GameplayActorTestRig.TestPlantLaborCost));
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(2);
    }

    [Test]
    public async Task Plant_OnPublicFarm_Allowed_ZeroLabor_EnginePath()
    {
        var (actor, session) = CreateActor("m51-plant-3");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        actor.Character.LaborPower = 50;
        SetFarmGate(true);
        SetFarmAllowlist(true); // TestCropDoodadId on the Farm allowlist

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        // Public-farm placement zeroes the labor cost (packet mirror).
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)50);
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(2);
        var doodads = typeof(WorldInstance).GetField("_doodads", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session.World) as System.Collections.Concurrent.ConcurrentDictionary<uint, Doodad>;
        TestContext.Current?.OutputWriter.WriteLine($"DIAG doodadIds=[{string.Join(",", doodads?.Keys ?? [])}]");
        await Assert.That(session.World.GetDoodad(GameplayActorTestRig.TestDoodadObjId)).IsNotNull();
    }

    // ================================================================ rejection taxonomy — pre-flight

    [Test]
    public async Task Plant_OnPublicFarm_Disallowed_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m51-plant-4");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        actor.Character.LaborPower = 50;
        SetFarmGate(true);
        SetFarmAllowlist(false); // doodad NOT on the Farm allowlist

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not allowed on public farm")).IsTrue();
        // Pre-flight refusal: nothing consumed, no labor charged, no Running.
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(3);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)50);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Plant_InsufficientLabor_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m51-plant-5");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 2);
        actor.Character.LaborPower = 3; // < TestPlantLaborCost (5)

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("insufficient labor")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(2);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)3);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Plant_NoSeedInBag_RejectedPreFlight()
    {
        var (actor, _) = CreateActor("m51-plant-6");
        // No stock.

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in inventory")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Plant_NotPlantableItem_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m51-plant-7");
        // An ordinary usable item with NO item_spawn_doodads mapping.
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);

        var request = actor.Plant(GameplayActorTestRig.TestItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not a plantable seed")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Plant_UnknownDoodadTemplate_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m51-plant-8");
        // Mapping exists, but the doodad template is absent from game data.
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestUnseededSeedItemId, 2);

        var request = actor.Plant(GameplayActorTestRig.TestUnseededSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("doodad template")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestUnseededSeedItemId)).IsEqualTo(2);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Plant_HouseNoPermission_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m51-plant-9");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 2);
        actor.Character.AccountId = 100; // NOT the house owner's account
        AddHouse(actor.Character, ownerId: 999_999); // finished private house of another character

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("no permission")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(2);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Plant_NonFinitePosition_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m51-plant-10");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 2);

        var request = actor.Plant(GameplayActorTestRig.TestSeedItemId, new Vector3(float.NaN, 0f, 0f));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("must be finite")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(2);
    }

    // ================================================================ idempotency — retries cannot double-consume

    [Test]
    public async Task Plant_SameKeyRetry_RefusedAfterPersistenceFailure_NoDoubleConsumption()
    {
        var (actor, session) = CreateActor("m51-plant-11");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 5);
        actor.Character.LaborPower = 50; // cover the unclaimed-land labor cost

        var first = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition, idempotencyKey: "plant-retry-1");
        // First attempt: engine path ran, seed consumed, persistence write
        // failed → Interrupted (ambiguous outcome, key locked).
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(4);

        // Same-key retry: the Interrupted outcome LOCKS the key — the retry
        // is refused pre-flight and must not consume anything.
        var retry = actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition, idempotencyKey: "plant-retry-1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(4);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        // The refusal is not an attempt of its own: the original outcome
        // stays under the key (a third retry is still refused).
        var retryRecord = actor.AuditTrace[1];
        await Assert.That(retryRecord.Result).IsEqualTo(ActorLifecycleState.Rejected);
        var findByKey = actor.FindByKey("plant-retry-1");
        await Assert.That(findByKey?.TraceId).IsEqualTo(first.TraceId);
    }

    [Test]
    public async Task Plant_FreshKeySecondPlant_ConsumesExactlyOneMoreSeed()
    {
        var (actor, session) = CreateActor("m51-plant-12");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 5);
        actor.Character.LaborPower = 50; // cover the unclaimed-land labor cost

        actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition, idempotencyKey: "plant-op-1");
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(4);

        // A FRESH key is a genuinely new logical operation (B1 semantics) —
        // it executes and consumes exactly one more seed. No attempt may
        // ever consume two.
        actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition, idempotencyKey: "plant-op-2");
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(3);
    }

    // ================================================================ trace emission

    [Test]
    public async Task Plant_AuditTrace_ToJson_CarriesFullTraceShape()
    {
        var (actor, session) = CreateActor("m51-plant-13");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 2);
        AddHouse(actor.Character, ownerId: actor.Character.Id);

        actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("Plant");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(GameplayActorTestRig.TestSeedItemId);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Interrupted");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
    }

    [Test]
    public async Task Plant_PreFlightRejection_AuditRecord_HasNoRunningTransition()
    {
        var (actor, _) = CreateActor("m51-plant-14");

        actor.Plant(GameplayActorTestRig.TestSeedItemId, TestPosition);

        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Plant);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(record.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(record.StateChanges[0]).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    // ================================================================ rig helpers

    private static (GameplayActor Actor, HeadlessSession Session) CreateActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RigWorld(session);
        return (actor, session);
    }

    /// <summary>
    /// Gives the test world a UNIQUE high-base instance id (all headless
    /// session worlds are born as instance id 1 — the WorldManager registry
    /// is first-wins, so only the first test's world would resolve, and a
    /// later test's planted doodad would land in the first test's world via
    /// the Transform.InstanceId setter chain) and registers it, then attaches
    /// a SpawnManager (CanPlace's GetCommonFarmDoodads + the engine's
    /// AddPlayerDoodad tail dereference it — production worlds get one at
    /// creation). Same registration shape as CropHarvestLoopRig.RegisterWorld.
    /// </summary>
    private static uint s_nextWorldId = 0x4000_0000;

    private static void RigWorld(HeadlessSession session)
    {
        typeof(WorldInstance).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(session.World.Id, session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);

        // Sync the character transform to the RENAMED world id. CreateActor
        // pinned the character's _instanceId to the session world's ORIGINAL
        // id via the backing field (AddObject must never consult the shared
        // registry). The engine's CreatePlayerDoodad does
        // doodad.Transform = character.Transform.CloneDetached(doodad), and
        // the clone ctor resolves GameObject.ParentWorld =
        // WorldManager.GetWorld(InstanceId) — a stale original-id copy would
        // resolve null (the id-1 world was renamed away and never registered
        // under 1) and NRE in set_ParentWorld. Same backing-field bypass
        // pattern as CreateActor.
        typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>
    /// Registers a finished private house at <see cref="TestPosition"/> in
    /// the HousingManager registry. CurrentStep is -1 (finished) so
    /// AllowedToInteract runs the REAL permission model; the binding-doodad
    /// list is empty so the step setter spawns nothing.
    /// </summary>
    private House AddHouse(Character owner, uint ownerId)
    {
        var house = new House
        {
            Id = (uint)(0x9300 + _housesAdded.Count + 1),
            ObjId = (uint)(0x9300 + _housesAdded.Count + 1),
            OwnerId = ownerId,
            // Template FIRST — the CurrentStep setter dereferences it
            // (ModelId = Template.MainModelId on the finished path).
            Template = new HousingTemplate
            {
                GardenRadius = 10f,
                MainModelId = 0,
                HousingBindingDoodad = []
            },
            CurrentStep = -1, // finished house → permission model, not the unfinished shortcut
            Permission = HousingPermission.Private,
            Transform = new Transform(owner)
        };
        house.Transform.Local.SetPosition(TestPosition);
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(HousingManager.Instance)!;
        houses[house.Id] = house;
        _housesAdded.Add(house);
        return house;
    }

    private void RemoveHouses()
    {
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(HousingManager.Instance)!;
        foreach (var house in _housesAdded)
            houses.Remove(house.Id);
        _housesAdded.Clear();
    }

    private static void SetFarmGate(bool enabled) => GameplayActorTestRig.SetFarmGateEnabled(enabled);

    private static void SetFarmAllowlist(bool allowed) => GameplayActorTestRig.SetFarmAllowlist(allowed);

    private static int BagCount(GameplayActor actor, uint itemTemplateId)
    {
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(itemTemplateId, -1, out _, out var count);
        return count;
    }
}
