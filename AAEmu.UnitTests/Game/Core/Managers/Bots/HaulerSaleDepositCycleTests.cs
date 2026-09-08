using System.Net;
using System.Net.Sockets;
using System.Numerics;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C4 hauler/trader v1 slice-3 (ROADMAP M8 living-village contracts) —
/// specialty sale → deposit → return-home through
/// <see cref="HaulerSaleDepositCycle"/>: unload the pack off its cargo point
/// through the real PackPickup path, sell it at the specialty gold trader
/// through the real SellSpecialty path (payout asserted against the created
/// mail), bank the proceeds figure through the real DepositMoney path, drive
/// the empty wagon home through the real DriveVehicle movement model.
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (proceeds paid without consuming the pack, refused sale that still banks,
/// double-applied deposit, skipped return leg, same-key retry that pays
/// twice) and PASSES on the composed real paths. Raw engine rejections
/// (no carried pack, unknown trader, insufficient balance) are already pinned
/// by GameplayActorSellSpecialtyTests / GameplayActorDepositTests — these
/// tests pin the COMPOSER's leg order, hold semantics, payout formula, and
/// conservation, not the engine gates.
///
/// Proceeds note (honest): the specialty payout travels as reward mail (in
/// transit ~22 h), and no TakeMail actor action exists yet — the DEPOSIT leg
/// banks the proceeds figure from operating cash (bank Δ == mail payout).
/// The ledger law (mailΔ + bankΔ + inventoryΔ == payout) pins the no-dup
/// guarantee instead.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HaulerSaleDepositCycleTests
{
    private static readonly Vector3 HomePosition = new(1000f, 1000f, 100f);
    private static readonly Vector3 TraderPosition = new(1100f, 1000f, 100f); // 100 units east

    // Fixture pack recipe (the slice-1 SeedFixturePackRecipe shape, own ids):
    // 1 × potato (7992) → 1 × fixture cargo pack (264901), labor 10.
    private const uint PackCraftId = 99_033;
    private const uint PackCraftSkillId = 99_034;
    private const int PackCraftLaborCost = 10;
    private const uint PackMaterialItemId = CropHarvestLoopTests.PotatoItemId;

    // Fixture specialty surface (the EconomyDayCycleScenarioRigTests
    // SeedHaulerSurfaces shape, own bundle): pack 264901 in bundle 77,
    // profit 1000, static ratio 1000, no refund → base 1000; gold trader
    // template 1003 (specialty_coin_id 0); sale zone key 142 (group 5) ≠
    // pack origin group 26.
    // Worked payout @ fresh-manager max ratio 130% + 5% interest:
    //   round(1000 × 1.30 × 1.05) = round(1365.0) = 1365.
    private const uint GoldTraderTemplateId = 1003;
    private const uint SaleBundleId = 77;
    private const int BundleProfit = 1000;
    private const int BundleRatioStatic = 1000;
    private const uint SaleZoneKey = 142;
    private const uint SaleZoneGroup = 5;
    private const uint PackOriginGroup = 26;
    private const int ExpectedBasePrice = 1000;
    private const long ExpectedPayout = 1365;

    private const int SellLaborCost = 60;

    private static uint s_nextWorldId = 0x6300_0000; // fresh base: 0x6100 slice-1 / 0x6200 slice-2 / 0x7000 economy

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;
    private static object? _previousSusManager;
    private static object? _previousModelManager;
    private static object? _previousZoneManager;
    private static object? _previousSpecialtyManager;
    private static object? _previousMailManager;
    private static object? _previousNameManager;
    private static object? _previousCharacterManager;
    private static object? _previousItemIdManager;
    private static int _previousMinLevel;

    [Before(Test)]
    public void SetUp()
    {
        // Doodad.Delete() must fail FAST and deterministically headless (the
        // PlantActionsTests convention): a dead port turns the MySQL write
        // into an immediate MySqlException instead of a localhost:3306
        // attempt. The UNLOAD leg deletes the cargo doodad via RecoverItem.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });

        GameplayActorTestRig.SeedCargoPackSurface();
        GameplayActorTestRig.SeedCraftSurface();
        SeedFixturePackRecipe();
        GameplayActorTestRig.SeedItemTemplate(PackMaterialItemId);
        SeedEquipSurface();
        SeedSaleSurfaces();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        AppConfiguration.Instance.Specialty.MinLevelToCraftSell = _previousMinLevel;
        SetSingleton(typeof(Singleton<ZoneManager>), _previousZoneManager);
        SetSingleton(typeof(Singleton<SpecialtyManager>), _previousSpecialtyManager);
        SetSingleton(typeof(Singleton<MailManager>), _previousMailManager);
        SetSingleton(typeof(Singleton<NameManager>), _previousNameManager);
        SetSingleton(typeof(Singleton<CharacterManager>), _previousCharacterManager);
        if (_previousItemIdManager != null)
        {
            var idField = typeof(ItemManager).GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? typeof(ItemManager).GetField("itemIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            idField?.SetValue(ItemManager.Instance, _previousItemIdManager);
            _previousItemIdManager = null;
        }
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousSusManager);
        _previousSusManager = null;
        _previousModelManager = null;
        MySQL.SetConfiguration(null);
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
    public async Task SaleDeposit_HappyPath_FullChainConserves()
    {
        // Full chain across all three composers: craft→load (slice-1) at
        // home, drive (slice-2) to the trader, unload→sell→deposit→return
        // (slice-3) home.
        var (actor, session) = GameplayActorTestRig.CreateActor("m8c4-s1-full");
        RegisterWorld(session);
        SeedMovementSingletons();
        AttachCapture(actor);
        GameplayActorTestRig.SetPosition(actor, HomePosition);
        WorldManager.Instance.AddVisibleObject(actor.Character);
        actor.Character.Level = 10;
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetMoney(actor, 100_000);
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, 0x3350u);
        PlaceInWorld(session, slave, HomePosition);
        GameplayActorTestRig.GrantItem(actor, PackMaterialItemId, 1);

        var craftLoad = HaulerPackCraftLoadCycle.Run(actor,
            new HaulerPackCraftLoadCycle.HaulerPackCraftLoadOptions
            {
                CycleId = "m8c4-s1-full-craft",
                PackCraftId = PackCraftId,
                PackMaterialItemId = PackMaterialItemId,
                PackMaterialAmount = 1,
                BenchObjId = benchObjId,
                SlaveObjId = slave.ObjId
            }, new HaulCraftPump());
        await Assert.That(craftLoad.Passed).IsTrue();

        var drive = HaulerDriveRouteCycle.Run(actor,
            new HaulerDriveRouteCycle.HaulerDriveRouteOptions
            {
                CycleId = "m8c4-s1-full-drive",
                SlaveObjId = slave.ObjId,
                Destination = TraderPosition,
                Speed = 10f,
                DriveTimeout = TimeSpan.FromSeconds(30),
                PumpBudget = TimeSpan.FromSeconds(30)
            }, new HaulDrivePump());
        await Assert.That(drive.Passed).IsTrue();

        var traderObjId = SpawnGoldTrader(session);
        MoveToSaleZone(actor);

        var moneyBefore = actor.Character.Money;
        var bankBefore = actor.Character.Money2;
        var result = HaulerSaleDepositCycle.Run(actor, Options("m8c4-s1-full", slave.ObjId, traderObjId, HomePosition), new HaulSalePump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "unload-pack-recovered", "sell-completed", "sell-payout-formula",
                     "sell-labor-conserved", "deposit-completed", "deposit-conservation", "full-leg-ledger",
                     "return-home-completed", "return-home-arrival", "return-home-wagon-empty"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Materials: the single potato consumed by the craft, never refunded.
        await Assert.That(GameplayActorTestRig.BagCount(actor, PackMaterialItemId)).IsEqualTo(0);

        // Products: one pack crafted, then consumed by the sale — nowhere left.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        var foundInSystem = actor.Character.Inventory.SystemContainer.GetAllItemsByTemplate(
            GameplayActorTestRig.CargoPackTemplateId, -1, out var sysPacks, out _);
        await Assert.That(foundInSystem || sysPacks.Count == 0).IsTrue();
        await Assert.That(sysPacks.Count).IsEqualTo(0);
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId > 0)).IsEqualTo(0);

        // Currency across the whole chain: payout mail in transit + banked
        // figure − advanced operating cash == payout (no dup, no leak).
        await Assert.That(result.BasePrice).IsEqualTo(ExpectedBasePrice);
        await Assert.That(result.Payout).IsEqualTo(ExpectedPayout);
        await Assert.That(MailCopper(actor)).IsEqualTo(ExpectedPayout);
        await Assert.That(actor.Character.Money2 - bankBefore).IsEqualTo(ExpectedPayout);
        await Assert.That(actor.Character.Money - moneyBefore).IsEqualTo(-ExpectedPayout);
        await Assert.That(result.BankDeposited).IsEqualTo(ExpectedPayout);

        // Labor across the whole chain: craft 10 + sale 60, exact.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - PackCraftLaborCost - SellLaborCost));
        await Assert.That(result.LaborCharged).IsEqualTo(SellLaborCost);

        // Home: the empty wagon arrived.
        await Assert.That(Math.Abs(slave.Transform.World.Position.X - HomePosition.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(slave.Transform.World.Position.Y - HomePosition.Y)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(slave.Transform.World.Position.Z - HomePosition.Z)).IsLessThan(0.5f);
        await Assert.That(result.DistanceRemaining).IsLessThan(0.5f);
    }

    [Test]
    public async Task Sale_Refused_HoldsWithReason_PackRetainedNoProceeds()
    {
        // Fail-pre: a level-9 seller hits the canonical "10레벨 미만은
        // 특산품 제작/판매 불가" gate — the composer must HOLD at SELL with
        // the engine reason, keep the pack, create no proceeds, touch no bank.
        var (actor, session, slave, traderObjId, packItemId) = CreateTraderRig("m8c4-s1-refused", 0x3360u);
        actor.Character.Level = 9;

        var moneyBefore = actor.Character.Money;
        var bankBefore = actor.Character.Money2;
        var result = HaulerSaleDepositCycle.Run(actor, Options("m8c4-s1-refused", slave.ObjId, traderObjId, HomePosition), new HaulSalePump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("SELL");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(result.Criteria.Any(c => c.Name == "sell-hold-pack-retained" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "sell-hold-no-proceeds" && c.Passed)).IsTrue();

        // The pack stayed carried (never consumed by the refused sale).
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!.Id).IsEqualTo(packItemId);
        // No proceeds anywhere: no mail, no bank movement, no inventory movement.
        await Assert.That(MailCopper(actor)).IsEqualTo(0);
        await Assert.That(actor.Character.Money2).IsEqualTo(bankBefore);
        await Assert.That(actor.Character.Money).IsEqualTo(moneyBefore);
        await Assert.That(result.Payout).IsEqualTo(0);
        await Assert.That(result.BankDeposited).IsEqualTo(0);
        // The return leg never ran: the wagon never left the trader.
        await Assert.That(slave.Transform.World.Position).IsEqualTo(TraderPosition);
    }

    [Test]
    public async Task Deposit_ConservesBankDelta_EqualsPayout()
    {
        // Fail-pre: a double-applying deposit would read 2× payout in the
        // bank — the composer banks the proceeds figure exactly once.
        var (actor, session, slave, traderObjId, _) = CreateTraderRig("m8c4-s1-deposit", 0x3370u);
        GameplayActorTestRig.SetMoney(actor, 50_000);

        var result = HaulerSaleDepositCycle.Run(actor, Options("m8c4-s1-deposit", slave.ObjId, traderObjId, HomePosition), new HaulSalePump());

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Payout).IsEqualTo(ExpectedPayout);
        await Assert.That(result.Criteria.Any(c => c.Name == "deposit-conservation" && c.Passed)).IsTrue();
        await Assert.That(actor.Character.Money2).IsEqualTo(ExpectedPayout);
        await Assert.That(actor.Character.Money).IsEqualTo(50_000 - ExpectedPayout);
        await Assert.That(result.BankDeposited).IsEqualTo(ExpectedPayout);
    }

    [Test]
    public async Task ReturnHome_ArrivesWithEmptyWagon()
    {
        // Fail-pre: a skipped or mangled return leg leaves the wagon short
        // of home — the composer drives the empty wagon all the way back.
        var (actor, session, slave, traderObjId, _) = CreateTraderRig("m8c4-s1-return", 0x3380u);

        var result = HaulerSaleDepositCycle.Run(actor, Options("m8c4-s1-return", slave.ObjId, traderObjId, HomePosition), new HaulSalePump());

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "return-home-completed" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "return-home-arrival" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "return-home-wagon-empty" && c.Passed)).IsTrue();

        // Real movement: the VEHICLE arrived (ArrivalRadius 0.5 per axis).
        var arrival = slave.Transform.World.Position;
        await Assert.That(Math.Abs(arrival.X - HomePosition.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(arrival.Y - HomePosition.Y)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(arrival.Z - HomePosition.Z)).IsLessThan(0.5f);
        await Assert.That(result.DistanceRemaining).IsLessThan(0.5f);
        // Empty and still crewed: no cargo aboard, driver seat still held.
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId > 0)).IsEqualTo(0);
        await Assert.That(slave.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var seated)
            && ReferenceEquals(seated, actor.Character)).IsTrue();
    }

    [Test]
    public async Task Cycle_SameKeyRetry_NeverDuplicatesProceeds()
    {
        // Fail-pre: a retry that pays twice creates a second mail and a
        // second bank credit — the same-key rerun must hold with nothing
        // created and nothing moved.
        var (actor, session, slave, traderObjId, packItemId) = CreateTraderRig("m8c4-s1-retry", 0x3390u);

        var first = HaulerSaleDepositCycle.Run(actor, Options("m8c4-s1-retry", slave.ObjId, traderObjId, HomePosition), new HaulSalePump());
        await Assert.That(first.Passed).IsTrue();
        await Assert.That(MailCopper(actor)).IsEqualTo(ExpectedPayout);
        var bankAfterFirst = actor.Character.Money2;
        var moneyAfterFirst = actor.Character.Money;

        var retry = HaulerSaleDepositCycle.Run(actor, Options("m8c4-s1-retry", slave.ObjId, traderObjId, HomePosition), new HaulSalePump());

        await Assert.That(retry.Passed).IsFalse();
        // The pack is gone (consumed by the first sale), so the rerun holds
        // at PRECHECK — the engine-true backstop. Exactly one payout mail,
        // one bank credit, no inventory movement.
        await Assert.That(retry.FailStage).IsEqualTo("PRECHECK");
        await Assert.That(MailCopper(actor)).IsEqualTo(ExpectedPayout);
        await Assert.That(actor.Character.Money2).IsEqualTo(bankAfterFirst);
        await Assert.That(actor.Character.Money).IsEqualTo(moneyAfterFirst);

        // The engine-level duplicate guard: re-issuing the SELL leg alone
        // with the first run's key is refused as a duplicate (StateTransition),
        // never a second sale — the M4Exit no-dup-proceeds precedent.
        var duplicateSell = actor.SellSpecialty(traderObjId, idempotencyKey: "m8c4-s1-retry-sell");
        await Assert.That(duplicateSell.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(duplicateSell.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(MailCopper(actor)).IsEqualTo(ExpectedPayout);
    }

    // ------------------------------------------------------------ rig below

    /// <summary>
    /// Builds the slice-3 precondition through the REAL paths at the trader:
    /// cargo vehicle summoned, pack equipped (seller == crafter, no 80/20
    /// split), actor boarded at the driver seat, pack loaded onto the first
    /// free cargo point, gold trader 1 m away. Throws when setup itself
    /// refuses — a setup refusal is a rig defect, never a composer verdict.
    /// </summary>
    private (GameplayActor actor, HeadlessSession session, Slave slave, uint traderObjId, ulong packItemId) CreateTraderRig(
        string name, uint slaveObjId)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        SeedMovementSingletons();
        AttachCapture(actor);
        GameplayActorTestRig.SetPosition(actor, TraderPosition);
        WorldManager.Instance.AddVisibleObject(actor.Character);
        actor.Character.Level = 10;
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetMoney(actor, 100_000);
        MoveToSaleZone(actor);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, slaveObjId);
        PlaceInWorld(session, slave, TraderPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId, actor.Character.Id);
        var packItemId = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!.Id;
        var board = actor.BoardVehicle(slaveObjId, AttachPointKind.Driver, idempotencyKey: $"{name}-board");
        if (board.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"rig board failed: {board.State} ({board.Detail})");
        var load = actor.LoadPackOntoVehicle(slaveObjId, idempotencyKey: $"{name}-load");
        if (load.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"rig load failed: {load.State} ({load.Detail})");
        var traderObjId = SpawnGoldTrader(session);
        return (actor, session, slave, traderObjId, packItemId);
    }

    private static HaulerSaleDepositCycle.HaulerSaleDepositOptions Options(string cycle, uint slaveObjId, uint traderObjId, Vector3 home)
        => new()
        {
            CycleId = cycle,
            SlaveObjId = slaveObjId,
            GoldTraderObjId = traderObjId,
            Home = home,
            Speed = 10f,
            DriveTimeout = TimeSpan.FromSeconds(30),
            PumpBudget = TimeSpan.FromSeconds(30)
        };

    private static long MailCopper(GameplayActor actor)
        => MailManager.Instance.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == actor.Character.Id)
            .Sum(m => m.Body.CopperCoins);

    private static void MoveToSaleZone(GameplayActor actor)
    {
        actor.Character.Transform.ZoneId = SaleZoneKey;
        var names = (Dictionary<uint, string>)typeof(NameManager)
            .GetField("_characterIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(NameManager.Instance)!;
        var ids = (Dictionary<string, uint>)typeof(NameManager)
            .GetField("_characterNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(NameManager.Instance)!;
        // MailManager.Send's receiver verification (name AND id must match)
        // gates the payout mail (the SpecialtyManagerTests convention).
        names[actor.Character.Id] = actor.Character.Name;
        ids[actor.Character.Name] = actor.Character.Id;
    }

    private static uint SpawnGoldTrader(HeadlessSession session)
    {
        // Same spawn path as the rig merchants (HeadlessSession.SpawnNpc),
        // then swap in a specialty-trader template (specialty_coin_id 0 =
        // gold trader — the PlaceTrader shape SpecialtyManagerTests uses),
        // 1 m from the wagon (the engine's 2.5 m sale range).
        var objId = session.SpawnNpc(GoldTraderTemplateId);
        var npc = session.World.GetNpc(objId);
        if (npc != null)
        {
            npc.Template = new NpcTemplate { Id = GoldTraderTemplateId, SpecialtyCoinId = 0 };
            npc.Transform.ZoneId = SaleZoneKey;
            npc.Transform.Local.SetPosition(TraderPosition + new Vector3(1f, 0f, 0f));
        }
        return objId;
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
    /// Seeds the fixture sale surfaces (the EconomyDayCycleScenarioRigTests
    /// SeedHaulerSurfaces shape, own ids): specialty bundle row for the
    /// fixture pack, fresh max-ratio manager, sale-zone wiring, Coins
    /// template, cargo-doodad recover phase (so the carried-load UNLOAD runs
    /// the real PackPickup), name/mail/character managers. Previous singleton
    /// instances are saved for TearDown so sibling suites never observe the
    /// swap.
    /// </summary>
    private static void SeedSaleSurfaces()
    {
        _previousMinLevel = AppConfiguration.Instance.Specialty.MinLevelToCraftSell;
        AppConfiguration.Instance.Specialty.MinLevelToCraftSell = 10; // canonical tooltip gate

        // Incrementing item ids (the M3a-3 trap): pack crafting, the cargo
        // doodad link and RecoverItem all resolve the pack instance BY id.
        var idField = typeof(ItemManager).GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? typeof(ItemManager).GetField("itemIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (idField != null)
        {
            _previousItemIdManager ??= idField.GetValue(ItemManager.Instance);
            var existingItems = (System.Collections.Concurrent.ConcurrentDictionary<ulong, Item>)typeof(ItemManager)
                .GetField("_allItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(ItemManager.Instance)!;
            var nextId = 0x01000000u; // ItemIdManager.FirstId — real engine range
            foreach (var existingId in existingItems.Keys)
                if (existingId is > 0 and <= uint.MaxValue && existingId >= nextId)
                    nextId = (uint)existingId + 1;
            var mock = Mock.Of<IItemIdManager>();
            mock.GetNextId().Returns(() => nextId++);
            idField.SetValue(ItemManager.Instance, mock.Object);
        }

        // Fixture pack trade fields: no refund (base == profit exactly); the
        // origin zone group arms the canonical same-zone exclusion (26 ≠ 5).
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        var packTemplate = templates[GameplayActorTestRig.CargoPackTemplateId];
        packTemplate.Refund = 0;
        packTemplate.SpecialtyZoneId = PackOriginGroup;

        // Coins template (MailForSpeciality.FinalizeForSeller early-returns
        // without it — the seller mail would never verify its receiver).
        templates.TryAdd(Item.Coins, new ItemTemplate
        {
            Id = Item.Coins,
            Name = "Coins",
            MaxCount = 1,
            FixedGrade = 0,
            Gradable = false
        });

        // The cargo placed-pack doodad gets a START phase carrying the generic
        // recover skill — after PackVehicleService's InitDoodad the loaded
        // cargo doodad is recoverable, which is what the UNLOAD leg's real
        // PackPickup (RecoverItem / 11361) requires. Missing-only per surface
        // (the shared SeedCargoPackSurface registers the template bare).
        SeedDoodadIdManager();
        var funcGroupId = GameplayActorTestRig.CargoPackDoodadTemplateId + 10;
        var funcId = GameplayActorTestRig.CargoPackDoodadTemplateId + 20;
        GameplayActorTestRig.SeedRecoverablePackDoodad(funcGroupId, funcId);
        var doodadTemplates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DoodadManager.Instance)!;
        if (!doodadTemplates.TryGetValue(GameplayActorTestRig.CargoPackDoodadTemplateId, out var cargoTemplate))
        {
            cargoTemplate = new DoodadTemplate { Id = GameplayActorTestRig.CargoPackDoodadTemplateId };
            doodadTemplates[GameplayActorTestRig.CargoPackDoodadTemplateId] = cargoTemplate;
        }
        var allFuncGroups = (Dictionary<uint, DoodadFuncGroups>)typeof(DoodadManager)
            .GetField("_allFuncGroups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DoodadManager.Instance)!;
        var startGroup = new DoodadFuncGroups { Id = funcGroupId, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start };
        if (allFuncGroups == null)
        {
            allFuncGroups = [];
            SetField(DoodadManager.Instance, "_allFuncGroups", allFuncGroups);
        }
        allFuncGroups.TryAdd(funcGroupId, startGroup);
        cargoTemplate.FuncGroups ??= [];
        if (cargoTemplate.FuncGroups.All(g => g.Id != funcGroupId))
            cargoTemplate.FuncGroups.Add(startGroup);

        // Zone surface: sale zone key → group 5 (≠ pack origin 26).
        _previousZoneManager ??= GetSingleton(typeof(Singleton<ZoneManager>));
        SetSingleton(typeof(Singleton<ZoneManager>), new ZoneManager(Mock.Of<IWorldManager>().Object));
        SetField(ZoneManager.Instance, "_zoneIdToKey", new Dictionary<uint, uint>());
        SetField(ZoneManager.Instance, "_conflicts", new Dictionary<ushort, ZoneConflict>());
        SetField(ZoneManager.Instance, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
        // Empty climate elems keep InitDoodad's growth-bonus probe off the real
        // climate surface headless (GetClimatesByZone iterates it).
        SetField(ZoneManager.Instance, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        SetField(ZoneManager.Instance, "_zones", new Dictionary<uint, Zone>
        {
            [SaleZoneKey] = new() { Id = 1, ZoneKey = SaleZoneKey, GroupId = SaleZoneGroup }
        });
        SetField(ZoneManager.Instance, "_groups", new Dictionary<uint, ZoneGroup>
        {
            [SaleZoneGroup] = new() { Id = SaleZoneGroup },
            [PackOriginGroup] = new() { Id = PackOriginGroup }
        });

        // Specialty surface: one gold trader + one bundle row for the fixture
        // pack (the exact seed shape of SpecialtyManagerTests).
        _previousSpecialtyManager ??= GetSingleton(typeof(Singleton<SpecialtyManager>));
        var specialtyManager = new SpecialtyManager();
        SetField(specialtyManager, "_specialties", new Dictionary<uint, Specialty>());
        SetField(specialtyManager, "_specialtyBundleItems", new Dictionary<uint, SpecialtyBundleItem>());
        SetField(specialtyManager, "_specialtyNpc", new Dictionary<uint, SpecialtyNpc>
        {
            [GoldTraderTemplateId] = new()
            {
                Id = 1, Name = "test-gold-trader",
                NpcId = GoldTraderTemplateId,
                SpecialtyBundleId = SaleBundleId
            }
        });
        SetField(specialtyManager, "_specialtyBundleItemsMapped",
            new Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>>
            {
                [GameplayActorTestRig.CargoPackTemplateId] = new()
                {
                    [SaleBundleId] = new SpecialtyBundleItem
                    {
                        Id = 1,
                        ItemId = GameplayActorTestRig.CargoPackTemplateId,
                        SpecialtyBundleId = SaleBundleId,
                        Profit = BundleProfit,
                        Ratio = BundleRatioStatic,
                        Item = packTemplate
                    }
                }
            });
        SetField(specialtyManager, "_priceRatios", new Dictionary<uint, Dictionary<uint, double>>());
        SetField(specialtyManager, "_soldPackAmountInTick", new Dictionary<uint, Dictionary<uint, int>>());
        SetSingleton(typeof(Singleton<SpecialtyManager>), specialtyManager);

        // Name/mail/character surfaces (ORDER MATTERS: the mail manager holds
        // a direct reference to the seeded NameManager instance).
        _previousNameManager ??= GetSingleton(typeof(Singleton<NameManager>));
        var nameManager = new NameManager();
        SetField(nameManager, "_characterIds", new Dictionary<uint, string>());
        SetField(nameManager, "_characterNames", new Dictionary<string, uint>());
        SetField(nameManager, "_characterAccounts", new Dictionary<uint, uint>());
        SetSingleton(typeof(Singleton<NameManager>), nameManager);

        _previousMailManager ??= GetSingleton(typeof(Singleton<MailManager>));
        var mailIdMock = Mock.Of<IMailIdManager>();
        var nextMailId = 1u;
        mailIdMock.GetNextId().Returns(() => nextMailId++);
        var mailManager = new MailManager(
            mailIdMock.Object,
            NameManager.Instance,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        SetField(mailManager, "_allPlayerMails", new Dictionary<long, BaseMail>());
        SetField(mailManager, "_deletedMailIds", new List<long>());
        SetSingleton(typeof(Singleton<MailManager>), mailManager);

        _previousCharacterManager ??= GetSingleton(typeof(Singleton<CharacterManager>));
        var characterManager = new CharacterManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IAccountManager>().Object,
            NameManager.Instance,
            Mock.Of<ICharacterIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IHousingManager>().Object,
            Mock.Of<IFamilyManager>().Object,
            MailManager.Instance,
            Mock.Of<ITaskManager>().Object);
        SetField(characterManager, "_expertLimits", new Dictionary<int, ExpertLimit>
        {
            [0] = new() { UpLimit = int.MaxValue }
        });
        SetSingleton(typeof(Singleton<CharacterManager>), characterManager);
    }

    /// <summary>
    /// Seeds the fixture pack recipe: 1 × potato → 1 × fixture cargo pack at
    /// the rig bench (missing-only, the slice-1 SeedFixturePackRecipe shape,
    /// own ids).
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
                    new CraftMaterial { ItemId = PackMaterialItemId, Amount = 1 }
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
    /// Doodad.Save() allocates the row id via DoodadIdManager BEFORE the
    /// MySQL write; the rig points MySQL at a dead port so the write fails
    /// fast and deterministically (PlantActionsTests convention), and the id
    /// manager must be initialized to reach it (missing-only).
    /// </summary>
    private static void SeedDoodadIdManager()
    {
        var freeIdsField = typeof(AAEmu.Game.Utils.IdManager).GetField("_freeIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (freeIdsField?.GetValue(AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance) == null)
            AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance.Initialize(false);
    }

    /// <summary>
    /// Null-guards the SkillManager/BuffGameData/ItemGameData dictionaries
    /// the equip/load surface reads (the LoadPackOntoVehicle-tests shape —
    /// missing-only, never replaces populated registries).
    /// </summary>
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
    /// FinalizeTransform runs delta-movement analysis through SusManager
    /// every 5s of accumulated movement, and Character.SetPosition consults
    /// ModelManager when the character is attached to a Slave (deck-height
    /// probe). The headless test process has no DI — seed both singletons
    /// the way GameplayActorDriveVehicleTests does, AFTER the rig's Seed()
    /// has populated WorldManager. Restored in TearDown so sibling suites
    /// never observe the swap.
    /// </summary>
    private static void SeedMovementSingletons()
    {
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new SusManager(WorldManager.Instance));

        _previousModelManager = typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        var modelManager = new ModelManager();
        typeof(ModelManager)
            .GetField("_modelTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<uint, ModelType>());
        typeof(ModelManager)
            .GetField("_models", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>>());
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, modelManager);
    }

    /// <summary>Test session sink — the same pattern the slave-lifecycle and
    /// housing tests use to capture outbound packets.</summary>
    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null!;
        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null!;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    /// <summary>Attaches a real GameConnection with a capture sink to the actor.</summary>
    private static void AttachCapture(GameplayActor actor)
    {
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
    }

    /// <summary>Places the slave in the world AND its region grid so the
    /// vehicle's movement broadcast reaches the rider (BroadcastPacket →
    /// WorldManager.GetAround → region neighbors).</summary>
    private static void PlaceInWorld(HeadlessSession session, BaseUnit unit, Vector3 position)
    {
        if (unit.ParentWorld == null)
            typeof(AAEmu.Game.Models.Game.World.GameObject)
                .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(unit, session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(unit.Transform, session.World.Id);
        unit.Transform.Local.SetPosition(position);
        WorldManager.Instance.AddVisibleObject(unit);
    }

    private static object? GetSingleton(Type singletonBase)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);

    private static void SetSingleton(Type singletonBase, object? instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, instance);

    private static object? GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        return field?.GetValue(target);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    /// <summary>
    /// Headless sale pump: ticks the actor in 1s steps until the return-home
    /// drive reaches a terminal state or the wall-clock budget runs out
    /// (12 × 1s ticks at 10 m/s cover a 100-unit leg with margin — the
    /// slice-2 HaulDrivePump shape).
    /// </summary>
    private sealed class HaulSalePump : IHaulSalePump
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget)
        {
            var deadline = Environment.TickCount64 + (long)pollBudget.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
                actor.Tick(TimeSpan.FromSeconds(1));
            return request;
        }
    }

    /// <summary>
    /// Headless drive pump (the slice-2 HaulDrivePump shape, verbatim).
    /// </summary>
    private sealed class HaulDrivePump : IHaulDrivePump
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget)
        {
            var deadline = Environment.TickCount64 + (long)pollBudget.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
                actor.Tick(TimeSpan.FromSeconds(1));
            return request;
        }
    }

    /// <summary>
    /// Headless craft pump: ticks the actor and applies the REAL CraftEffect
    /// once the engine queue is active (the slice-1 HaulCraftPump shape).
    /// </summary>
    private sealed class HaulCraftPump : IHaulCraftPump
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
