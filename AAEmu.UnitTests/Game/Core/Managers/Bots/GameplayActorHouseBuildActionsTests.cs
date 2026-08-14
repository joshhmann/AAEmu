using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.2 (t_94761d55): BuildHouse on the IGameplayActor v2 surface through the
/// REAL engine path — HousingManager.Build, the exact call the
/// CSCreateHousePacket handler makes:
///  - the design item INSTANCE is resolved from the actor's own bag by
///    template (the client holds the item and sends its instance id),
///  - the design id resolves through the canonical housings table,
///  - the packet's tax gate is mirrored pre-flight (the engine's own
///    CalculateBuildingTaxInfo + gold/certificate affordability — the
///    engine refuses SILENTLY via an error packet, so the actor refuses
///    with a taxonomy reason BEFORE the engine call),
///  - the engine enforces the canonical placement rules (land zone /
///    faction / category / houseless-only / overlap via
///    HousingPlacementValidator), charges the tax, consumes the design
///    item, creates the house in construction state (CurrentStep 0 for
///    the canonical 3-step design) and registers it.
///
/// Canonical data rig: HousingGameData is loaded from compact.sqlite3
/// (M3a construction-rig pattern) and the zone path resolves the real
/// w_solzreed_1 land zone (zone key 9, faction 148) through the world
/// template's ZoneKeyByRegions grid + a zone fake — the same join the
/// engine performs at boot. The polygon layer is skipped exactly like any
/// world whose pak AreaShapes are unavailable (engine-documented
/// behavior).
///
/// Persistence boundary honesty: the engine's Build swallows its own
/// House.Save() MySQL failure (catch → log → retry on the next save tick)
/// — the placement is registered in-memory and the engine treats the
/// write as recoverable BY DESIGN. The actor therefore Completes on the
/// engine's registered outcome; the full persistent-row path rides the
/// real MySQL stack (Phase 2 M3a/M4 economic replay) — the same split the
/// M5.1 Plant card uses.
///
/// All assertions run headless — no client required (Unit.SendPacket is
/// null-safe; the attached GameConnection is the M2b-E2E no-op-session
/// bridge shape Build dereferences for connection.ActiveChar).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel] // process-wide MySQL.SetConfiguration + singleton state + canonical HousingGameData
public class GameplayActorHouseBuildActionsTests
{
    /// <summary>Canonical 1.2 w_solzreed_1 zone key (faction 148 NuiaAlliance; groups 1+14 allow category 1).</summary>
    private const uint SolzreedZoneKey = 9;
    private const uint SolzreedZoneId = 9;
    private const FactionsEnum SolzreedFaction = FactionsEnum.NuiaAlliance;

    /// <summary>Canonical 1.2 w_solzreed_3 zone key (group 12 — houseless-only).</summary>
    private const uint HouselessZoneKey = 125;

    /// <summary>Canonical 1.2 e_falcony_plateau_1 zone key (faction 149 HaranyaAlliance).</summary>
    private const uint HaranyaZoneKey = 21;
    private const FactionsEnum HaranyaFaction = FactionsEnum.HaranyaAlliance;

    /// <summary>Canonical 1.2 taxation_id 2 (small house) tax in copper: 100,000/week.</summary>
    private const long SmallHouseWeeklyTax = 100_000;

    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    private WorldConfig _previousWorldConfig;
    private object _previousHousingGameData;
    private readonly List<House> _housesAdded = [];
    private HashSet<uint> _houseIdsAtSetup = [];

    [Before(Test)]
    public void SetUp()
    {
        // Tax tail control: with the canonical 7-day config, a fresh
        // house's TaxDueDate lands exactly on now and the UpdateTaxInfo
        // tail walks the mail path (unwired headless). 14 days keeps the
        // house out of tax-due state — the placement outcome is what the
        // Build surface under test produces.
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 14 };

        GameplayActorTestRig.SeedHouseBuildSurface();
        LoadRealHousingGameData();
        // Leak-proofing: every house the ENGINE registers during this test
        // (Build's in-memory placement) must be removed at teardown, not
        // just the pre-placed rig houses — an engine-built house left
        // behind would change the account tax count and overlap checks of
        // every later test in the class.
        _houseIdsAtSetup = HousingManager.Instance.GetAllHouses().Select(h => h.Id).ToHashSet();

        // Build's persistence tail (house.Save) must fail FAST and
        // deterministically headless: a dead port turns the MySQL write
        // into an immediate MySqlException instead of a localhost:3306
        // attempt (which could hit a real dev MySQL). The engine swallows
        // it (catch → retry next tick) — M3b convention.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
        AppConfiguration.Instance.World = _previousWorldConfig;
        var housingField = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        housingField?.SetValue(null, _previousHousingGameData);
        RemoveHouses();
    }

    // ================================================================ happy path — real engine path

    [Test]
    public async Task BuildHouse_OnValidLandZone_DrivesRealEnginePath_RegistersHouseAndConsumesDesignItemOnce()
    {
        var (actor, session) = CreateActor("m52-house-1");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);
        var moneyBefore = actor.Character.Money;

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        // The REAL engine path ran: the house is registered in the
        // HousingManager registry under the actor, in construction state
        // (canonical design 172 has 3 build steps → CurrentStep 0), and
        // the design item was consumed by the engine.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var newHouse = FindHouse(actor.Character.Id);
        await Assert.That(newHouse).IsNotNull();
        await Assert.That(newHouse.CurrentStep).IsEqualTo(0);
        await Assert.That(newHouse.TemplateId).IsEqualTo(GameplayActorTestRig.TestHouseDesignId);
        await Assert.That(newHouse.OwnerId).IsEqualTo(actor.Character.Id);
        await Assert.That(newHouse.AccountId).IsEqualTo(actor.Character.AccountId);
        // First house on the account: canonical tax path charges nothing.
        await Assert.That(actor.Character.Money).IsEqualTo(moneyBefore);
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(1);

        // Full audit record shape for the terminal transition.
        var record = actor.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.HouseBuild);
        await Assert.That(record.TargetId).IsEqualTo(GameplayActorTestRig.TestHouseDesignId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (building house"))).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();
    }

    // ================================================================ canonical rules — engine gates

    [Test]
    public async Task BuildHouse_OverlappingExistingHouse_Rejected_NothingApplied()
    {
        var (actor, session) = CreateActor("m52-house-2");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);
        actor.Character.Money = 10_000_000; // clear the tax pre-flight — the ENGINE gate is under test
        // Another character's finished house occupies the exact spot
        // (garden 7.5 + 7.5 = 15 m required; distance 0 → overlap).
        AddRegisteredHouse(actor.Character, ownerId: 999_999, TestPosition);

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        // The engine's overlap gate refused — the actor's post-state
        // verification converts the silent error packet into Rejected.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("engine refused")).IsTrue();
        // Nothing applied: no new house, design item NOT consumed.
        await Assert.That(FindHouse(actor.Character.Id)).IsNull();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(2);
    }

    [Test]
    public async Task BuildHouse_OnHouselessOnlyZone_WhenAlreadyOwner_Rejected()
    {
        var (actor, session) = CreateActor("m52-house-3");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);
        actor.Character.Money = 10_000_000; // clear the tax pre-flight — the ENGINE gate is under test
        // The actor already owns a house (canonical w_solzreed_1 plot).
        AddRegisteredHouse(actor.Character, ownerId: actor.Character.Id, new Vector3(1500f, 1500f, 100f));
        // Build on the houseless-only zone w_solzreed_3 (group 12).
        WireZone(session, HouselessZoneKey, "w_solzreed_3", SolzreedFaction, HouselessZoneKey);

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("engine refused")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(2);
    }

    [Test]
    public async Task BuildHouse_OnEnemyFactionZone_Rejected()
    {
        var (actor, session) = CreateActor("m52-house-4");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);
        // e_falcony_plateau_1 is Haranya-owned (149); the actor is Nuian (148).
        WireZone(session, HaranyaZoneKey, "e_falcony_plateau_1", HaranyaFaction, HaranyaZoneKey);

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("engine refused")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(2);
    }

    // ================================================================ rejection taxonomy — pre-flight

    [Test]
    public async Task BuildHouse_InsufficientMoneyForTax_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m52-house-5");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);
        // A second house on the SAME ACCOUNT (another character's plot):
        // canonical tax = 1 week (100,000) + deposit (2×100,000) = 300,000.
        AddRegisteredHouse(actor.Character, ownerId: 999_998, new Vector3(1500f, 1500f, 100f),
            accountId: actor.Character.AccountId);
        actor.Character.Money = SmallHouseWeeklyTax * 3 - 1; // 1 copper short

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not enough money for the house tax")).IsTrue();
        // Pre-flight refusal: nothing consumed, no Running, no house.
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(2);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(FindHouse(actor.Character.Id)).IsNull();
    }

    [Test]
    public async Task BuildHouse_NoDesignItemInBag_RejectedPreFlight()
    {
        var (actor, _) = CreateActor("m52-house-6");
        // No stock.

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in inventory")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task BuildHouse_UnknownDesign_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m52-house-7");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);

        var request = actor.BuildHouse(999_999, GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("unknown house design")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(2);
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task BuildHouse_NoGameConnection_RejectedPreFlight()
    {
        // Deliberately NO AttachConnection and NO zone wiring — the
        // connection gate is a pure pre-flight check; the real Build path
        // is connection-mediated (every engine refusal is an error packet
        // on the connection).
        var (actor, session) = GameplayActorTestRig.CreateActor("m52-house-8");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("no game connection")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task BuildHouse_NonFinitePosition_RejectedPreFlight()
    {
        var (actor, session) = CreateActor("m52-house-9");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);

        var request = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, new Vector3(float.NaN, 0f, 0f));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("must be finite")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(2);
    }

    // ================================================================ idempotency — retries cannot double-build

    [Test]
    public async Task BuildHouse_SameKeyRetry_RefusedAfterCompletion_NoDoubleBuild()
    {
        var (actor, session) = CreateActor("m52-house-10");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);

        var first = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition, idempotencyKey: "house-build-1");
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(1);

        // Same-key retry: the Completed outcome LOCKS the key — the retry
        // is refused pre-flight and must not build or consume anything.
        var retry = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition, idempotencyKey: "house-build-1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(1);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        // The refusal is not an attempt of its own: the original outcome
        // stays under the key.
        var findByKey = actor.FindByKey("house-build-1");
        await Assert.That(findByKey?.TraceId).IsEqualTo(first.TraceId);
    }

    [Test]
    public async Task BuildHouse_FreshKeySecondBuild_AdjacentPlot_RegistersSecondHouse()
    {
        var (actor, session) = CreateActor("m52-house-11");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);
        actor.Character.Money = 10_000_000; // second house on the account is taxed (300,000) — pre-flight must pass

        actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition, idempotencyKey: "house-op-1");
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(1);

        // A FRESH key is a genuinely new logical operation — it executes
        // and consumes exactly one more design item (engine-true backstop:
        // a retry without a design item in the bag can never build twice).
        var second = actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId,
            new Vector3(TestPosition.X + 16f, TestPosition.Y, TestPosition.Z), idempotencyKey: "house-op-2");

        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(BagCount(actor, GameplayActorTestRig.TestDesignItemTemplateId)).IsEqualTo(0);
        await Assert.That(HousingManager.Instance.GetAllHouses()
            .Count(h => h.OwnerId == actor.Character.Id)).IsEqualTo(2);
    }

    // ================================================================ trace emission

    [Test]
    public async Task BuildHouse_AuditTrace_ToJson_CarriesFullTraceShape()
    {
        var (actor, session) = CreateActor("m52-house-12");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestDesignItemTemplateId, 2);

        actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("HouseBuild");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(GameplayActorTestRig.TestHouseDesignId);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
    }

    [Test]
    public async Task BuildHouse_PreFlightRejection_AuditRecord_HasNoRunningTransition()
    {
        var (actor, _) = CreateActor("m52-house-13");

        actor.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
            GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);

        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.HouseBuild);
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
        GameplayActorTestRig.WireHouseZone(session, SolzreedZoneKey, new Zone
        {
            Id = SolzreedZoneId,
            Name = "w_solzreed_1",
            FactionId = SolzreedFaction
        });
        GameplayActorTestRig.AttachConnection(actor);
        return (actor, session);
    }

    /// <summary>Same world rigging as the M5.1 plant tests (unique high-base world id, registration, transform sync).</summary>
    private static uint s_nextWorldId = 0x5000_0000;

    private static void RigWorld(HeadlessSession session)
    {
        typeof(WorldInstance).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(session.World.Id, session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);

        typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>Switches the zone wiring to another canonical zone (key → name/faction).</summary>
    private static void WireZone(HeadlessSession session, uint zoneKey, string zoneName, FactionsEnum faction, uint zoneId)
        => GameplayActorTestRig.WireHouseZone(session, zoneKey, new Zone { Id = zoneId, Name = zoneName, FactionId = faction });

    /// <summary>
    /// Registers a house at the given position in the HousingManager
    /// registry (the M3a-exit MakeRegisteredHouse shape: canonical
    /// template, construction state 0 — the CurrentStep setter needs the
    /// template's build steps).
    /// </summary>
    private House AddRegisteredHouse(Character owner, uint ownerId, Vector3 position, uint? accountId = null)
    {
        var template = HousingGameData.Instance.GetTemplate(GameplayActorTestRig.TestHouseDesignId);
        var house = new House
        {
            Id = (uint)(0x9400 + _housesAdded.Count + 1),
            ObjId = (uint)(0x9400 + _housesAdded.Count + 1),
            TlId = (ushort)(0x9400 + _housesAdded.Count + 1),
            Template = template,
            TemplateId = template.Id,
            OwnerId = ownerId,
            CoOwnerId = ownerId,
            AccountId = accountId ?? owner.AccountId,
            Name = $"preplaced_{_housesAdded.Count + 1}",
            Permission = HousingPermission.Private,
            AllowRecover = true,
            PlaceDate = DateTime.UtcNow,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
        house.Transform = new Transform(house, null, position, Vector3.Zero);
        house.Transform.InstanceId = owner.ParentWorld.Id;
        house.CurrentStep = template.BuildSteps.Count > 0 ? 0 : -1;

        var manager = HousingManager.Instance;
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        houses[house.Id] = house;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        housesTl[house.TlId] = house;
        _housesAdded.Add(house);
        return house;
    }

    private void RemoveHouses()
    {
        var manager = HousingManager.Instance;
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        foreach (var house in _housesAdded)
        {
            houses.Remove(house.Id);
            housesTl.Remove(house.TlId);
        }

        // Engine-registered houses from this test (Build's in-memory
        // placement) — remove everything that wasn't there at SetUp.
        foreach (var id in houses.Keys.Where(id => !_houseIdsAtSetup.Contains(id)).ToList())
            houses.Remove(id);
        foreach (var kv in housesTl.Where(kv => !_housesAdded.Any(h => h.TlId == kv.Key)
                                              && !_houseIdsAtSetup.Contains(kv.Value.Id)).ToList())
            housesTl.Remove(kv.Key);
        _housesAdded.Clear();
    }

    private static House FindHouse(uint ownerId)
        => HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.OwnerId == ownerId);

    private static int BagCount(GameplayActor actor, uint itemTemplateId)
    {
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(itemTemplateId, -1, out _, out var count);
        return count;
    }

    // --- canonical data --------------------------------------------------

    private static string CanonicalDbPath
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(baseDir, "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(baseDir, "..", "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3")
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException("compact.sqlite3 not found in any expected test layout");
        }
    }

    /// <summary>
    /// Loads the canonical housing data (templates, land zones, build
    /// steps, taxations) into the HousingGameData singleton via its real
    /// loader — the same join the engine performs at boot. Save/restore
    /// the singleton so no other test sees it. The Taxation join is part
    /// of the canonical surface: PostLoad reads TaxationsManager.Instance
    /// (the engine boots TaxationsManager before HousingGameData.PostLoad),
    /// so the same canonical DB seeds it — template.Taxation is what the
    /// Build tax branch reads (TaxationId → tax).
    /// </summary>
    private void LoadRealHousingGameData()
    {
        var field = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousHousingGameData = field?.GetValue(null);
        var gameData = new HousingGameData();
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            gameData.Load(connection);

            if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<TaxationsManager>)))
            {
                var taxations = new TaxationsManager { taxations = new Dictionary<uint, Taxation>() };
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, tax FROM taxations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    taxations.taxations[Convert.ToUInt32(reader.GetValue(0))] = new Taxation
                    {
                        Id = Convert.ToUInt32(reader.GetValue(0)),
                        Tax = Convert.ToUInt32(reader.GetValue(1))
                    };
                GameplayActorTestRig.SeedSingleton(typeof(Singleton<TaxationsManager>), taxations);
            }
        }

        gameData.PostLoad();
        field?.SetValue(null, gameData);
    }
}
