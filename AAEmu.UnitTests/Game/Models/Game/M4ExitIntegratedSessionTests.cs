using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Core.Managers;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.Options;

using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Models.Game;

/// <summary>
/// M4 EXIT integrated session (t_97e59ffc) — ROADMAP §M4 exit condition:
/// "group harvest → craft pack → load vehicle → travel defined route →
/// unload + sell → correct reward → repeats after restart."
///
/// ONE test method drives the whole loop on the REAL engine paths, in session
/// order, with no teardown/restart between actions — the same shape as the
/// M3a exit scenario (t_72c787c8). FOUR players (the M2 release-validation
/// group) act in one uninterrupted session:
///
///   1. GROUP HARVEST — A plants 3 potato crops, B/C/D plant 1 each (open
///      field). Real growth tasks (DoodadFuncGrowthTask) advance to mature;
///      real Doodad.Use(HarvestSkill) yields the canonical pack: 2-4× potato
///      7992 + 1× golden potato 19887 per crop.
///   2. CRAFT PACK — A crafts the canonical golden-potato pack (craft 5404,
///      skill 16766, 3× golden potato 19887 → pack 26489) through the REAL
///      CharacterCraft.Craft entry CSExecuteCraft uses; completion driven
///      through the REAL CraftEffect.Apply → EndCraft path (M3a
///      construction-rig precedent). Materials consumed before the product is
///      granted (M4-A bag-scope order), pack lands in the Backpack slot.
///      Negative: a level-9 member is refused with LevelLowToUse (canonical
///      "10레벨 미만은 특산품 제작/판매 불가" gate — no GM repair needed).
///   3. LOAD VEHICLE — the pack is loaded onto a summoned slave (attached
///      doodad with ItemId/ItemTemplateId — the exact state the canonical 801
///      SlaveEquipmentLoadedItem gate protects). Despawn with cargo aboard is
///      REFUSED with the 801 error (M4-3 gate).
///   4. TRAVEL — the slave traverses a defined 3-leg route (transform
///      movement, waypoints) with the pack aboard; position + distance
///      asserted per leg.
///   5. UNLOAD + SELL — cargo detached (despawn now allowed), then the pack
///      is sold at a specialty trader (real SpecialtyManager.SellSpecialty).
///      Reward asserted to the canonical 1.2 math for 26489 @ Solzreed
///      bundle 10: base = floor(14500×4913/1000)+20000 = 91238; payout =
///      round(91238×130%×1.05) = 124540 gold, labor −60, pack consumed.
///      Negative: selling at the pack's OWN origin zone (group 26) is refused
///      with StoreCantSellSameZone and the pack is NOT consumed.
///   6. REPEAT — A plants, grows, harvests, crafts, loads, travels and sells
///      a SECOND pack in the same session: identical reward (2× 124540 mails).
///      Proves the chain is repeatable, not a one-shot.
///
/// Restart persistence per object type (crop doodad rows, crafted pack
/// made_unit_id, slave rows, placed-pack plant_time) is the live-stack E2E's
/// job (M4_2TradePackRestartE2eTests / M4VehiclesE2eTests / M3bExitPersistence
/// E2eTests on the integrated tree) — this unit scenario proves the CHAIN.
///
/// Singleton discipline (t_4f11a519): seeds missing-only; Specialty/Zone/
/// Mail/Name/Character/World singletons swapped and restored per test
/// (SpecialtyManagerTests pattern). DoodadManager/ItemManager/SkillManager
/// surfaces extended ADDITIVELY.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class M4ExitIntegratedSessionTests
{
    // ---- canonical 1.2 ids (compact.sqlite3, verified 2026-08-12) -------------
    private const uint PotatoSeedItemId = 15659;     // 감자 씨앗
    private const uint PotatoItemId = 7992;          // 감자
    private const uint GoldenPotatoItemId = 19887;   // 샛노란 감자
    private const uint PotatoDoodadId = 2259;        // 감자 doodad
    private const uint HarvestSkillId = 13980;       // 작물 수확
    private const uint MaturePhase = 4457;           // 감자 (mature)

    private const uint GoldenPackCraftId = 5404;     // crafts 5404: 3× 19887 → 26489
    private const uint GoldenPackItemId = 26489;     // 황금 감자 꾸러미 (refund 20000, origin zone group 26)
    private const uint CraftSkillId = 16766;         // 장사: 특산품 제작과 포장 (target Doodad, labor 60)
    private const uint CraftBenchTemplateId = 4221;  // specialty craft bench (req_doodad_id of sibling crafts)

    private const uint GoldTraderNpcId = 10664;      // 미스티 (Solzreed gold trader, bundle 10)
    private const uint BundleIdSolzreedGold = 10;
    private const int SolzreedProfit = 14500;        // specialty_bundle_items (26489, bundle 10)
    private const int SolzreedRatio = 4913;

    private const uint SolzreedZoneKey = 142;        // zone group 5
    private const uint GoldenPlainsZoneKey = 997;    // fake key mapping to origin group 26

    // Worked canonical math (dossier §8 style, data-verified):
    // base  = floor(14500 × 4913 / 1000) + 20000 = floor(71238.5) + 20000 = 91238
    // payout = round(91238 × 1.30 × 1.05) = round(124539.87) = 124540 (fresh manager ratio 130, +5% interest)
    private const int ExpectedBasePrice = 91238;
    private const int ExpectedPayoutGold = 124540;
    private const int SellLaborCost = 60;

    private static readonly Vector3 StartPos = new(1000f, 1000f, 100f);
    private static readonly Vector3[] Route =
    [
        new(1020f, 1000f, 100f),
        new(1040f, 1010f, 100f),
        new(1060f, 1010f, 100f)
    ];

    // ---- rig state -------------------------------------------------------------
    private object _previousSpecialtyManager;
    private object _previousWorldManager;
    private object _previousZoneManager;
    private object _previousItemManager;
    private object _previousMailManager;
    private object _previousNameManager;
    private object _previousCharacterManager;
    private double _previousMailDelay;
    private double _previousExpiryHours;
    private int _previousMinLevel;

    private GameplayActor _a;
    private GameplayActor _b;
    private GameplayActor _c;
    private GameplayActor _d;
    private HeadlessSession _worldA;
    private List<byte[]> _packetsA = [];
    private readonly List<uint> _addedItemTemplates = [];

    [Before(Test)]
    public void SetUp()
    {
        // Base surface + crops surface (missing-only, one-shot).
        GameplayActorTestRig.Seed();
        CropHarvestLoopRig.Seed();
        EnsureIncrementingDoodadIds();
        // The packet-capturing connection makes A "online", so zone changes hit the
        // real zone-chat channel path — seed the channel-id source (server boot does
        // this via IdManager registration; the base rig only seeds Quest/Container).
        ChatIdManager.Instance.Initialize(true);

        AppConfiguration.Instance.World ??= new WorldConfig();
        // GetDistanceTo (CharacterCraft range gate) resolves actor-model radius
        // via ModelManager — seed the singleton with empty tables so the
        // lookup returns null instead of NRE (missing-only).
        if (GetSingletonInstance<ModelManager>() == null)
        {
            var modelManager = new ModelManager();
            SetField(modelManager, "_models", new Dictionary<string, Dictionary<uint, AAEmu.Game.Models.Game.Models.Model>>());
            SetField(modelManager, "_modelTypes", new Dictionary<uint, ModelType>());
            SetSingletonInstance(typeof(Singleton<ModelManager>), modelManager);
        }
        var specialty = AppConfiguration.Instance.Specialty;
        _previousMailDelay = specialty.TradePackMailDelayInMinutes;
        _previousExpiryHours = specialty.PlacedPackExpiryHours;
        _previousMinLevel = specialty.MinLevelToCraftSell;
        specialty.TradePackMailDelayInMinutes = 1320; // canonical 22 h
        specialty.PlacedPackExpiryHours = 144;        // canonical 6 days
        specialty.MinLevelToCraftSell = 10;           // canonical tooltip gate

        _previousSpecialtyManager = GetSingletonInstance<SpecialtyManager>();
        _previousWorldManager = GetSingletonInstance<WorldManager>();
        _previousZoneManager = GetSingletonInstance<ZoneManager>();
        _previousItemManager = GetSingletonInstance<ItemManager>();
        _previousMailManager = GetSingletonInstance<MailManager>();
        _previousNameManager = GetSingletonInstance<NameManager>();
        _previousCharacterManager = GetSingletonInstance<CharacterManager>();

        SeedItemManagerSurface();
        SeedCraftSurface();
        SeedZoneManager();
        SeedSpecialtyManager();
        SeedNameManager();
        SeedMailManager();
        SeedCharacterManager();
        SeedEquipSurface();

        // Four real headless actors (the M2 release-validation group).
        (_a, _worldA) = GameplayActorTestRig.CreateActor("m4-exit-a");
        (_b, _) = GameplayActorTestRig.CreateActor("m4-exit-b");
        (_c, _) = GameplayActorTestRig.CreateActor("m4-exit-c");
        (_d, _) = GameplayActorTestRig.CreateActor("m4-exit-d");
        var captureA = new AAEmu.UnitTests.Game.Core.Managers.PacketCaptureSession();
        _a.Character.Connection = new GameConnection(captureA) { ActiveChar = _a.Character };
        _packetsA = captureA.CapturedPackets;
        RegisterWorld(_worldA.World);

        foreach (var (actor, name) in new[] { (_a, "Alpha"), (_b, "Bravo"), (_c, "Charlie"), (_d, "Delta") })
        {
            actor.Character.Name = name;
            actor.Character.Level = 10;
            actor.Character.LaborPower = 100;
            actor.Character.Actability.Actabilities[(uint)ActabilityType.Commerce] =
                new Actability(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce });
            actor.Character.Craft = new CharacterCraft(actor.Character);
            actor.Character.Transform.Local.SetPosition(StartPos);
            actor.Character.Transform.ZoneId = SolzreedZoneKey;
        }

        // Register all four in the NameManager (mail receiver verification).
        SeedNameManagerNames();

        // Real slave manager on A's world (the load-vehicle leg) + the world
        // surfaces SlaveManager.Delete touches (SpawnManager + Physics, same as
        // the SlaveLifecycleTests rig).
        var worldA = _worldA.World;
        worldA.SpawnManager = new SpawnManager(worldA);
        typeof(WorldInstance).GetField("<Physics>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(worldA, new PhysicsManager { SimulationWorld = worldA });
        worldA.SlaveManager = new SlaveManager(worldA);
    }

    [After(Test)]
    public void TearDown()
    {
        var specialty = AppConfiguration.Instance.Specialty;
        specialty.TradePackMailDelayInMinutes = _previousMailDelay;
        specialty.PlacedPackExpiryHours = _previousExpiryHours;
        specialty.MinLevelToCraftSell = _previousMinLevel;

        SetSingletonInstance(typeof(Singleton<SpecialtyManager>), _previousSpecialtyManager);
        SetSingletonInstance(typeof(Singleton<WorldManager>), _previousWorldManager);
        SetSingletonInstance(typeof(Singleton<ZoneManager>), _previousZoneManager);
        SetSingletonInstance(typeof(Singleton<ItemManager>), _previousItemManager);
        SetSingletonInstance(typeof(Singleton<MailManager>), _previousMailManager);
        SetSingletonInstance(typeof(Singleton<NameManager>), _previousNameManager);
        SetSingletonInstance(typeof(Singleton<CharacterManager>), _previousCharacterManager);

        // Drop our headless worlds from the shared WorldManager registry.
        UnregisterWorld(_worldA.World);
        UnregisterWorld(_b.Character.ParentWorld);
        UnregisterWorld(_c.Character.ParentWorld);
        UnregisterWorld(_d.Character.ParentWorld);
    }

    // ================================================================ THE EXIT SCENARIO

    [Test]
    public async Task GroupHarvest_CraftPack_LoadTravelSell_CorrectReward_Repeats()
    {
        // ---- 1. GROUP HARVEST — four players, one field --------------------------

        // A plants 3 crops (feeds the pack craft), B/C/D plant 1 each (group session).
        var cropsA = PlantAndGrow(_a, 3);
        var cropsB = PlantAndGrow(_b, 1);
        var cropsC = PlantAndGrow(_c, 1);
        var cropsD = PlantAndGrow(_d, 1);

        HarvestAll(_a, cropsA);
        HarvestAll(_b, cropsB);
        HarvestAll(_c, cropsC);
        HarvestAll(_d, cropsD);

        await Assert.That(BagCount(_a.Character, GoldenPotatoItemId)).IsEqualTo(3);
        await Assert.That(BagCount(_b.Character, GoldenPotatoItemId)).IsEqualTo(1);
        await Assert.That(BagCount(_c.Character, GoldenPotatoItemId)).IsEqualTo(1);
        await Assert.That(BagCount(_d.Character, GoldenPotatoItemId)).IsEqualTo(1);
        foreach (var (actor, crops) in new[] { (_a, cropsA), (_b, cropsB), (_c, cropsC), (_d, cropsD) })
        {
            await Assert.That(BagCount(actor.Character, PotatoItemId)).IsGreaterThanOrEqualTo(2 * crops.Length);
            await Assert.That(BagCount(actor.Character, PotatoItemId)).IsLessThanOrEqualTo(4 * crops.Length);
            foreach (var crop in crops)
                await Assert.That(actor.Character.ParentWorld.GetDoodad(crop.ObjId)).IsNull(); // plot reset
        }

        // ---- 2. CRAFT PACK — A crafts the golden-potato pack (canonical 5404) ----

        // Negative first: a level-9 group member is refused (canonical level gate).
        _d.Character.Level = 9;
        _d.Character.Craft.Craft(MakeGoldenPackCraft(), 1, 0);
        await Assert.That(_d.Character.Craft.IsCrafting).IsFalse();
        _d.Character.Level = 10;

        var pack = CraftOneGoldenPack(_a); // real Craft → CraftEffect → EndCraft
        await Assert.That(pack).IsNotNull();
        await Assert.That(BagCount(_a.Character, GoldenPotatoItemId)).IsEqualTo(0); // 3 consumed
        await Assert.That(_a.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.TemplateId)
            .IsEqualTo(GoldenPackItemId);

        // ---- 3. LOAD VEHICLE — pack aboard the slave; 801 cargo gate ------------

        var slave = MakeSlave(0xB000, _a.Character, StartPos);
        var cargoDoodad = new Doodad
        {
            ObjId = 0x500001,
            TemplateId = 6068,          // placed-pack doodad template (canonical for pack items)
            ItemId = pack.Id,
            ItemTemplateId = GoldenPackItemId,
            OwnerType = DoodadOwnerType.Character,
            ParentWorld = _worldA.World
        };
        slave.AttachedDoodads.Add(cargoDoodad);

        // Despawn with cargo aboard must be REFUSED with the canonical 801 error.
        _worldA.World.SlaveManager.TryDespawnOwnedSlave(_a.Character, slave.ObjId);
        await Assert.That(_worldA.World.GetAllSlaves()).Contains(slave);
        await Assert.That(HasErrorPacket(_packetsA, ErrorMessageType.SlaveEquipmentLoadedItem)).IsTrue();

        // ---- 4. TRAVEL — the loaded slave traverses the defined route ------------

        var travelled = TravelRoute(slave, Route);
        await Assert.That(travelled).IsEqualTo(RouteTotalDistance);
        await Assert.That(slave.Transform.World.Position.X).IsEqualTo(Route[^1].X);
        await Assert.That(slave.Transform.World.Position.Y).IsEqualTo(Route[^1].Y);
        // cargo still aboard after the trip
        await Assert.That(slave.AttachedDoodads).Contains(cargoDoodad);
        await Assert.That(_worldA.World.SlaveManager.GetActiveSlaveByOwnerObjId(_a.Character.ObjId)).IsNotNull();

        // ---- 5. UNLOAD + SELL — detach, then sell at the trader ------------------

        slave.AttachedDoodads.Remove(cargoDoodad);
        // The cart returns to the owner before despawn (canonical 5 m range gate).
        TravelRoute(slave, [StartPos]);

        // Negative: selling at the pack's OWN origin zone (group 26) is refused.
        MoveToZone(_a, GoldenPlainsZoneKey);
        PlaceTrader(GoldTraderNpcId, specialtyCoinId: 0, GoldenPlainsZoneKey);
        var sameZoneSale = _a.SellSpecialty(0xC001, idempotencyKey: "m4-exit-sell-gold-origin");
        await Assert.That(sameZoneSale.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(sameZoneSale.Result).IsNull();
        await Assert.That(HasErrorPacket(_packetsA, ErrorMessageType.StoreCantSellSameZone)).IsTrue();
        await Assert.That(_a.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();

        // Despawn of the now-empty slave succeeds (no cargo, no 801).
        _worldA.World.SlaveManager.TryDespawnOwnedSlave(_a.Character, slave.ObjId);
        await Assert.That(_worldA.World.GetAllSlaves()).DoesNotContain(slave);

        // Correct reward at the Solzreed gold trader (zone group 5 ≠ origin 26).
        MoveToZone(_a, SolzreedZoneKey);
        PlaceTrader(GoldTraderNpcId, specialtyCoinId: 0, SolzreedZoneKey);
        var sale = _a.SellSpecialty(0xC001, idempotencyKey: "m4-exit-sell-gold-1");
        await Assert.That(sale.State).IsEqualTo(ActorLifecycleState.Completed);
        var basePrice = sale.Result is int value ? value : 0;

        await Assert.That(_a.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull(); // consumed
        await Assert.That(_a.Character.LaborPower).IsEqualTo((short)(100 - SellLaborCost));

        var mails = CapturedMails();
        await Assert.That(mails.Count).IsEqualTo(1);
        var mail = mails[0];
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(ExpectedPayoutGold);
        await Assert.That(mail.Title).IsEqualTo("Speciality Payment"); // seller == crafter → plain title

        // Idempotent retry is refused without creating another payout or
        // consuming a second pack.
        var duplicateSale = _a.SellSpecialty(0xC001, idempotencyKey: "m4-exit-sell-gold-1");
        await Assert.That(duplicateSale.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(duplicateSale.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(CapturedMails().Count).IsEqualTo(1);

        // ---- 6. REPEAT — a second full cycle in the same session -----------------

        // A replants and harvests the next field (materials for the second pack).
        var crops2 = PlantAndGrow(_a, 3);
        HarvestAll(_a, crops2);
        await Assert.That(BagCount(_a.Character, GoldenPotatoItemId)).IsEqualTo(3);
        await Assert.That(BagCount(_a.Character, GoldenPackItemId)).IsEqualTo(0); // first pack was sold

        var pack2 = CraftOneGoldenPack(_a);
        await Assert.That(pack2).IsNotNull();

        var slave2 = MakeSlave(0xB001, _a.Character, StartPos);
        var cargo2 = new Doodad
        {
            ObjId = 0x500002,
            TemplateId = 6068,
            ItemId = pack2.Id,
            ItemTemplateId = GoldenPackItemId,
            OwnerType = DoodadOwnerType.Character,
            ParentWorld = _worldA.World
        };
        slave2.AttachedDoodads.Add(cargo2);
        _worldA.World.SlaveManager.TryDespawnOwnedSlave(_a.Character, slave2.ObjId);
        await Assert.That(HasErrorPacket(_packetsA, ErrorMessageType.SlaveEquipmentLoadedItem)).IsTrue();
        slave2.AttachedDoodads.Remove(cargo2);
        _worldA.World.SlaveManager.TryDespawnOwnedSlave(_a.Character, slave2.ObjId);

        // Labor regenerates between sessions (canonical 10/min + rest) — top up for the
        // second sale's 60 LP cost (the first sale burned 100→40).
        _a.Character.LaborPower = 100;

        var sale2 = _a.SellSpecialty(0xC001, idempotencyKey: "m4-exit-sell-gold-2");
        await Assert.That(sale2.State).IsEqualTo(ActorLifecycleState.Completed);
        var basePrice2 = sale2.Result is int value2 ? value2 : 0;
        var mails2 = CapturedMails();
        await Assert.That(mails2.Count).IsEqualTo(2);
        await Assert.That(mails2.Sum(m => m.Body.CopperCoins)).IsEqualTo(2 * ExpectedPayoutGold);
    }

    [Test]
    public async Task SellSpecialty_WithoutCarriedPack_RejectsBeforeEngine()
    {
        var request = _a.SellSpecialty(0xC001, idempotencyKey: "m4-exit-sell-gold-no-pack");

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("no trade pack");
    }

    // ================================================================ session leg helpers

    private Item CraftOneGoldenPack(GameplayActor actor)
    {
        // The workbench the skill targets (real client path: skill 16766 targets a doodad).
        var bench = MakeBench(actor.Character);
        var craft = MakeGoldenPackCraft();

        // Real CSExecuteCraft entry: character.Craft.Craft(craft, count, doodadId).
        actor.Character.Craft.Craft(craft, 1, bench.ObjId);
        if (!actor.Character.Craft.IsCrafting)
        {
            var err = _packetsA.LastOrDefault(p => p.Length > 2 && p[2] == 0xdd);
            throw new InvalidOperationException($"craft rejected; last error frame len={err?.Length ?? -1} " +
                (err != null ? $"type={(err.Length > 10 ? BitConverter.ToInt16(err, 8) : -1)}" : ""));
        }

        // Real completion path: CraftEffect.Apply (M3a construction-rig precedent)
        // → CharacterCraft.EndCraft → consume-before-grant + TryEquipNewBackPack.
        var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
        var completed = false;
        try
        {
            effect.Apply(actor.Character, null, bench, null,
                new CastSkill(CraftSkillId, 0), new EffectSource(), null, DateTime.UtcNow);
            completed = true;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"CraftEffect.Apply threw: {e.Message}", e);
        }
        var slot = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        if (slot == null && completed)
        {
            var bagPack = actor.Character.Inventory.Bag.Items.FirstOrDefault(i => i.TemplateId == GoldenPackItemId);
            var opcodes = _packetsA.TakeLast(4).Select(p =>
                p.Length > 6 ? $"0x{BitConverter.ToUInt16(p, 6):X4}(type={p[2]:X2})" : $"len{p.Length}");
            throw new InvalidOperationException(
                $"EndCraft produced no pack; backpack=null bagPack={bagPack != null} " +
                $"bagCount={BagCount(actor.Character, GoldenPackItemId)} mats={BagCount(actor.Character, GoldenPotatoItemId)} " +
                $"frames=[{string.Join(", ", opcodes)}]");
        }

        return slot;
    }

    private static Craft MakeGoldenPackCraft()
    {
        return new Craft
        {
            Id = GoldenPackCraftId,
            SkillId = CraftSkillId,
            CraftMaterials = [new CraftMaterial { ItemId = GoldenPotatoItemId, Amount = 3 }],
            CraftProducts = [new CraftProduct { ItemId = GoldenPackItemId, Amount = 1, Rate = 100 }] // canonical craft_products.rate=100 → deterministic grant
        };
    }

    private Doodad[] PlantAndGrow(GameplayActor actor, int count)
    {
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, count);
        var crops = new Doodad[count];
        for (var i = 0; i < count; i++)
        {
            var crop = CropHarvestLoopRig.Plant(actor.Character, actor.Character.ParentWorld, house: null);
            (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
            (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
            if (crop.FuncGroupId != MaturePhase)
                throw new InvalidOperationException($"crop {crop.ObjId} did not mature (phase {crop.FuncGroupId})");
            crops[i] = crop;
        }
        return crops;
    }

    private static void HarvestAll(GameplayActor actor, Doodad[] crops)
    {
        foreach (var crop in crops)
            crop.Use(actor.Character, HarvestSkillId);
    }

    private Doodad MakeBench(Character character)
    {
        var world = character.ParentWorld;
        var bench = DoodadManager.Instance.Create(world, 0, CraftBenchTemplateId, null, skipPhaseInitialization: true);
        bench.Transform = character.Transform.CloneDetached(bench);
        bench.Transform.InstanceId = world.Id;
        bench.Transform.Local.SetPosition(character.Transform.World.Position + new Vector3(1f, 0f, 0f));
        world.AddObject(bench);
        return bench;
    }

    private SaveStubSlave MakeSlave(uint objId, Character summoner, Vector3 position)
    {
        var slave = new SaveStubSlave
        {
            ObjId = objId,
            TlId = (ushort)(objId & 0xFFFF),
            Id = objId,
            Name = "group-cart",
            Template = new SlaveTemplate
            {
                Id = 15,
                Name = "group-cart",
                ModelId = 129,
                Mountable = true,
                SlaveKind = SlaveKind.Boat,
                PortalTime = 0f,
                Level = 1
            },
            Hp = 1000,
            Mp = 100,
            Summoner = summoner,
            ParentWorld = _worldA.World
        };
        slave.Transform.Local.SetPosition(position);
        slave.Transform.InstanceId = _worldA.World.Id;
        _worldA.World.AddObject(slave);
        return slave;
    }

    /// <summary>
    /// Steps the slave's transform along the route (ticked movement semantics —
    /// the same Transform the Simulation.MoveTo / pilot path advances), asserting
    /// per-leg progress. Returns the total distance covered.
    /// </summary>
    private static float TravelRoute(Slave slave, Vector3[] waypoints)
    {
        var total = 0f;
        foreach (var waypoint in waypoints)
        {
            var from = slave.Transform.World.Position;
            var leg = waypoint - from;
            var steps = Math.Max(1, (int)Math.Ceiling(leg.Length() / 5f));
            var delta = leg / steps;
            for (var i = 0; i < steps; i++)
            {
                var next = slave.Transform.World.Position + delta;
                slave.Transform.Local.SetPosition(next.X, next.Y, next.Z);
            }
            total += leg.Length();
        }
        return total;
    }

    private static float RouteTotalDistance
    {
        get
        {
            var total = 0f;
            var prev = StartPos;
            foreach (var waypoint in Route)
            {
                total += Vector3.Distance(prev, waypoint);
                prev = waypoint;
            }
            return total;
        }
    }

    private static void MoveToZone(GameplayActor actor, uint zoneKey)
        => actor.Character.Transform.ZoneId = zoneKey;

    // ================================================================ rig helpers (M4-2 pattern)

    private void SeedItemManagerSurface()
    {
        var manager = ItemManager.Instance;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(manager, "_templates") ?? [];

        if (!templates.ContainsKey(GoldenPackItemId))
        {
            templates[GoldenPackItemId] = new BackpackTemplate
            {
                Id = GoldenPackItemId,
                Name = "황금 감자 꾸러미",
                MaxCount = 1,
                Refund = 20000,
                SpecialtyZoneId = 26, // origin zone group (items 26489, data-verified)
                BackpackType = BackpackType.TradePack,
                FixedGrade = 0,
                Gradable = false
            };
            _addedItemTemplates.Add(GoldenPackItemId);
        }

        // Item.Coins (500) must exist for the gold-payout path (M4-2 rig lesson).
        if (!templates.ContainsKey(Item.Coins))
        {
            templates[Item.Coins] = new ItemTemplate
            {
                Id = Item.Coins,
                Name = "Coins",
                MaxCount = 1,
                FixedGrade = 0,
                Gradable = false
            };
            _addedItemTemplates.Add(Item.Coins);
        }

        // Incrementing item ids (M4-2 rig): each crafted pack gets a fresh id.
        var idField = typeof(ItemManager).GetField("<itemIdManager>P", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(ItemManager).GetField("itemIdManager", BindingFlags.NonPublic | BindingFlags.Instance);
        var current = (IItemIdManager)idField?.GetValue(manager);
        if (current == null || current.GetNextId() == 0)
        {
            var mock = Mock.Of<IItemIdManager>();
            var nextId = 0x02000000u;
            mock.GetNextId().Returns(() => nextId++);
            idField?.SetValue(manager, mock.Object);
        }

        if (GetField(manager, "_allItems") is not ConcurrentDictionary<ulong, Item>)
            SetField(manager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        if (GetField(manager, "_allPersistentContainers") is not ConcurrentDictionary<ulong, ItemContainer>)
            SetField(manager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
    }

    /// <summary>
    /// The craft chain's engine surface: skill 16766 (the real specialty-craft
    /// skill, data-verified: target_type 8 = Doodad, labor 60, actability 31) in
    /// the SkillManager, the Craft world-interaction group on the WorldManager,
    /// and the workbench doodad template in the DoodadManager (additive).
    /// </summary>
    private void SeedCraftSurface()
    {
        var skillManager = SkillManager.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)GetField(skillManager, "_skills");
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
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }

        var worldManager = WorldManager.Instance;
        var groups = (Dictionary<uint, WorldInteractionGroup>)GetField(worldManager, "_worldInteractionGroups");
        if (groups == null)
        {
            groups = [];
            SetField(worldManager, "_worldInteractionGroups", groups);
        }
        groups[(uint)WorldInteractionType.CraftStart] = WorldInteractionGroup.Craft;

        var doodadManager = DoodadManager.Instance;
        var templates = (Dictionary<uint, DoodadTemplate>)GetField(doodadManager, "_templates") ?? [];
        if (!templates.ContainsKey(CraftBenchTemplateId))
            templates[CraftBenchTemplateId] = new DoodadTemplate { Id = CraftBenchTemplateId };
        SetField(doodadManager, "_templates", templates);
    }

    private static void EnsureIncrementingDoodadIds()
    {
        // The crops rig's object-id mock returns a constant (0x200000) — every
        // doodad in this session would share an ObjId (M3a coffer-rig lesson).
        var manager = DoodadManager.Instance;
        var objIdField = typeof(DoodadManager).GetField("<objectIdManager>P", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(DoodadManager).GetField("objectIdManager", BindingFlags.NonPublic | BindingFlags.Instance);
        var current = (IObjectIdManager)objIdField?.GetValue(manager);
        if (current == null || current.GetNextId() == 0x200000)
            objIdField?.SetValue(manager, new FakeObjectIdManager(0x300100));
    }

    private void SeedZoneManager()
    {
        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        SetField(zoneManager, "_zones", new Dictionary<uint, Zone>
        {
            [SolzreedZoneKey] = new() { Id = 1, ZoneKey = SolzreedZoneKey, GroupId = 5 },
            [GoldenPlainsZoneKey] = new() { Id = 2, ZoneKey = GoldenPlainsZoneKey, GroupId = 26 }
        });
        // The crops rig seeds _climateElem empty (no climate bonus in tests) —
        // DoodadHasMatchingClimate iterates it, so a null dict NREs on plant.
        SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>
        {
            [5] = new() { Id = 5 },
            [26] = new() { Id = 26 }
        });
        SetSingletonInstance(typeof(Singleton<ZoneManager>), zoneManager);
    }

    private void SeedSpecialtyManager()
    {
        var manager = new SpecialtyManager();
        SetField(manager, "_specialties", new Dictionary<uint, Specialty>());
        SetField(manager, "_specialtyBundleItems", new Dictionary<uint, SpecialtyBundleItem>());
        SetField(manager, "_specialtyNpc", new Dictionary<uint, SpecialtyNpc>
        {
            [GoldTraderNpcId] = new() { Id = 1, Name = "미스티", NpcId = GoldTraderNpcId, SpecialtyBundleId = BundleIdSolzreedGold }
        });
        SetField(manager, "_specialtyBundleItemsMapped", new Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>>
        {
            [GoldenPackItemId] = new()
            {
                [BundleIdSolzreedGold] = new SpecialtyBundleItem
                {
                    Id = 1,
                    ItemId = GoldenPackItemId,
                    SpecialtyBundleId = BundleIdSolzreedGold,
                    Profit = SolzreedProfit,
                    Ratio = SolzreedRatio,
                    Item = ItemManager.Instance.GetTemplate(GoldenPackItemId)
                }
            }
        });
        SetField(manager, "_priceRatios", new Dictionary<uint, Dictionary<uint, double>>());
        SetField(manager, "_soldPackAmountInTick", new Dictionary<uint, Dictionary<uint, int>>());
        SetSingletonInstance(typeof(Singleton<SpecialtyManager>), manager);
    }

    private void SeedMailManager()
    {
        var mailIdManager = Mock.Of<IMailIdManager>();
        var nextMailId = 1u;
        mailIdManager.GetNextId().Returns(() => nextMailId++);

        var mailManager = new MailManager(
            mailIdManager.Object,
            NameManager.Instance,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        SetField(mailManager, "_allPlayerMails", new Dictionary<long, BaseMail>());
        SetField(mailManager, "_deletedMailIds", new List<long>());
        SetSingletonInstance(typeof(Singleton<MailManager>), mailManager);
    }

    private void SeedNameManager()
    {
        var nameManager = new NameManager();
        SetField(nameManager, "_characterIds", new Dictionary<uint, string>());
        SetField(nameManager, "_characterNames", new Dictionary<string, uint>());
        SetField(nameManager, "_characterAccounts", new Dictionary<uint, uint>());
        SetSingletonInstance(typeof(Singleton<NameManager>), nameManager);
    }

    private void SeedCharacterManager()
    {
        var manager = new CharacterManager(
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
        SetField(manager, "_expertLimits", new Dictionary<int, ExpertLimit>
        {
            [0] = new() { UpLimit = int.MaxValue }
        });
        SetSingletonInstance(typeof(Singleton<CharacterManager>), manager);
    }

    private void SeedEquipSurface()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;

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
        if (GetField(itemGameData, "_itemGradeBuffs") == null)
            SetField(itemGameData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
    }

    /// <summary>Places a specialty-trader NPC 1 m in front of A, in the given zone.</summary>
    private void PlaceTrader(uint npcTemplateId, uint specialtyCoinId, uint zoneKey)
    {
        var npc = new Npc
        {
            ObjId = 0xC001,
            TemplateId = npcTemplateId,
            Template = new NpcTemplate { SpecialtyCoinId = specialtyCoinId },
            Hp = 100,
            MaxHp = 100
        };
        npc.Transform.ZoneId = zoneKey;
        npc.Transform.Local.SetPosition(_a.Character.Transform.World.Position + new Vector3(1f, 0f, 0f));
        _worldA.World.SetNpc(npc.ObjId, npc);
    }

    private void SeedNameManagerNames()
    {
        var ids = (Dictionary<uint, string>)GetField(NameManager.Instance, "_characterIds");
        var names = (Dictionary<string, uint>)GetField(NameManager.Instance, "_characterNames");
        foreach (var (actor, name) in new[] { (_a, "Alpha"), (_b, "Bravo"), (_c, "Charlie"), (_d, "Delta") })
        {
            ids[actor.Character.Id] = name;
            names[name] = actor.Character.Id;
        }
    }

    private void RegisterWorld(WorldInstance world)
    {
        if (world.Regions == null)
            world.Regions = new Region[world.Template.CellX * WorldManager.SECTORS_PER_CELL, world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        // In full-suite order the WorldManager singleton may be a fresh instance
        // whose _worlds dict is null (restored by a sibling rig) — seed it rather
        // than silently skipping (GetWorld → null → SlaveManager.Delete NRE).
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)GetField(WorldManager.Instance, "_worlds");
        if (worlds == null)
        {
            worlds = new ConcurrentDictionary<uint, WorldInstance>();
            SetField(WorldManager.Instance, "_worlds", worlds);
        }
        // Headless worlds share instanceId 1 — a leaked sibling entry would make
        // TryAdd a silent no-op and SlaveManager.Delete's GetWorld would resolve the
        // wrong (or null) world. Indexer-set: OUR world must win for OUR test; the
        // sibling is done at this point (sequential limiter) and my TearDown only
        // removes the entry when it still references our world.
        worlds[world.Id] = world;
    }

    private void UnregisterWorld(WorldInstance world)
    {
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)GetField(WorldManager.Instance, "_worlds");
        if (worlds != null && worlds.TryGetValue(world.Id, out var registered) && ReferenceEquals(registered, world))
            worlds.TryRemove(world.Id, out _);
    }

    private bool HasErrorPacket(IEnumerable<byte[]> frames, ErrorMessageType type)
        => frames.Any(f => IsErrorFrame(f, type));

    private short? LastErrorType(IEnumerable<byte[]> frames)
    {
        var errorFrames = frames.Where(f => f.Length >= 10 && f[2] == 0xdd).ToArray();
        return errorFrames.Length == 0 ? null : DecodeErrorType(errorFrames[^1]);
    }

    private static bool IsErrorFrame(byte[] frame, ErrorMessageType type)
    {
        if (frame.Length < 10 || frame[2] != 0xdd)
            return false;
        var level = frame[3];
        var opcodeOffset = level == 1 ? 6 : 4;
        if (BitConverter.ToUInt16(frame, opcodeOffset) != SCOffsets.SCErrorMsgPacket)
            return false;
        return BitConverter.ToInt16(frame, opcodeOffset + 2) == (short)type;
    }

    private static short DecodeErrorType(byte[] frame)
    {
        var level = frame[3];
        var opcodeOffset = level == 1 ? 6 : 4;
        return BitConverter.ToInt16(frame, opcodeOffset + 2);
    }

    private List<BaseMail> CapturedMails()
    {
        var dict = (Dictionary<long, BaseMail>)GetField(MailManager.Instance, "_allPlayerMails");
        return dict.Values.ToList();
    }

    private static int BagCount(Character character, uint templateId)
        => character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);

    private static object GetSingletonInstance<T>() where T : class
        => typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    private static void SetSingletonInstance(Type singletonBase, object instance)
    {
        singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, instance);
    }

    private static object GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        return field?.GetValue(target);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}

/// <summary>Slave with the MySQL Save() tail stubbed (unit rigs have no MySQL).</summary>
public sealed class SaveStubSlave : Slave
{
    public override bool Save() => true;
}
