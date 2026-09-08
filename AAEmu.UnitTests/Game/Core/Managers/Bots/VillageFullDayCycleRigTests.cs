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
using AAEmu.Game.Models.Game.Char;
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
/// M8 C5-S4 full-day assembly (ROADMAP M8 living-village contracts) — chain
/// <see cref="VillageDayCycle"/> (day) + <see cref="VillageEveningCycle"/>
/// (evening) for one roster through <see cref="VillageFullDayCycle"/>: the day
/// runs in its own idempotency namespace, the evening in its own, and the
/// assembly reconciles the COMBINED ledger (labor charged once, zero net
/// inventory drift, bank drift == evening refunds) plus the M5
/// audit-completeness law over the concatenated traces.
///
/// The evening markets caller-specified banked output (the slice-3 shape):
/// day lots share the same bank containers and are asserted untouched, but
/// vending the day's own harvest is a later slice (honest — see the assembly
/// composer). Chatter runs inside the evening half under its budgets.
///
/// Fail-pre discipline: every test FAILS without the composer (CS0246 —
/// no such type) and on the corresponding assembly defect (evening running
/// after a failed day, labor double-charged across halves, inventory drift,
/// bank drift != refunds, incomplete concatenated audit, over-budget
/// chatter, same-key retry that re-executes), and PASSES on the composed
/// real paths. No engine diffs: the assembly calls the day/evening
/// composers only.
///
/// Restart note (rig-side single-restart variant): the kill-9 + MySQL
/// mechanics are pinned by the slice-2 E2E over the same tables — slice-4
/// adds no new persisted table. The rig proves the new surface instead: the
/// day+evening restart projection is complete, deterministic, and
/// round-trippable (everything the E2E compares pre==post lives in it).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class VillageFullDayCycleRigTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);
    private static readonly Vector3 AwayHome = new(1020f, 1000f, 100f); // 20 units east: exercises the move pump

    // Day fixture recipe (the slice-1 shape, same ids/guards — identical values, idempotent):
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

    // Evening fixture output (the slice-3 shape, same ids/guards): refund 10c/unit.
    private const uint OutputItemId = 99_041;
    private const int OutputRefund = 10;
    private const uint UnsellableItemId = 99_042;

    private static uint s_nextWorldId = 0x9500_0000; // fresh base: 0x9000 slice-1 / 0x9400 slice-3
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
        SeedEveningTemplates();
        GameplayActorTestRig.SeedMerchantPack();
        SeedEquipSurface();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
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
    public async Task FullDay_DayThenEvening_CombinedLedger_Reconciles()
    {
        var farmer = SetupFarmer("m8v4-full-farmer", eveningOutputCount: 3);
        var crafter = SetupCrafter("m8v4-full-crafter", eveningOutputCount: 2);

        var result = VillageFullDayCycle.Run(
        [
            farmer.Assemble(AwayHome),
            crafter.Assemble(AwayHome)
        ], new VillageFullDayCycle.VillageFullDayOptions { CycleId = "m8v4-full" }, new FullDayPump());

        await Assert.That(result.Passed).IsTrue();
        // Both halves present in one result.
        await Assert.That(result.Report.DayReport).IsNotNull();
        await Assert.That(result.Report.EveningReport).IsNotNull();
        await Assert.That(result.Report.ChatterLinesTotal).IsEqualTo(0);
        // Combined ledger criteria.
        foreach (var name in new[] { "fullday-day-passed", "fullday-evening-passed", "fullday-labor-conserved",
                     "fullday-money-conserved", "fullday-bank-reconciled", "fullday-audit-complete" })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();
        // Exact economics: crafter labor charged once across BOTH halves,
        // inventory drift zero, bank drift == evening refunds.
        await Assert.That(crafter.Actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
        await Assert.That(farmer.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(crafter.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(farmer.Actor.Character.Money2).IsEqualTo(30);
        await Assert.That(crafter.Actor.Character.Money2).IsEqualTo(20);
        // Shared containers: day lots banked and untouched, evening output gone.
        await Assert.That(BankOnly(farmer.Actor, PotatoId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BankOnly(crafter.Actor, ProductItemId)).IsEqualTo(1);
        await Assert.That(BankPlusBag(farmer.Actor, OutputItemId)).IsEqualTo(0);
        await Assert.That(BankPlusBag(crafter.Actor, OutputItemId)).IsEqualTo(0);
        // Both halves' legs in one trace: harvest AND merchant sale AND return.
        foreach (var action in new[] { ActorActionType.Harvest, ActorActionType.Sell, ActorActionType.DepositMoney, ActorActionType.Move })
            await Assert.That(result.TraceRecords.Any(r => r.Action == action)).IsTrue();
        // Both villagers home.
        foreach (var villager in new[] { farmer, crafter })
            await Assert.That(Vector3.Distance(villager.Actor.Character.Transform.World.Position, AwayHome))
                .IsLessThanOrEqualTo(GameplayActor.ArrivalRadius + 0.1f);
    }

    [Test]
    public async Task FullDay_DayFailure_SkipsEvening_FailClosed()
    {
        // Farmer holds at REPLANT (unapproved seed); crafter holds at WITHDRAW (empty bank).
        var farmer = SetupFarmer("m8v4-skip-farmer", eveningOutputCount: 1,
            seedItemId: GameplayActorTestRig.TestItemTemplateId);
        var (crafterActor, crafterSession) = CreateActorOnUniqueWorld("m8v4-skip-crafter");
        GameplayActorTestRig.SetPosition(crafterActor, TestPosition);
        crafterActor.Character.LaborPower = 100;
        GameplayActorTestRig.SetMoney(crafterActor, 1_000);
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(crafterSession, crafterActor);
        var crafterMerchant = GameplayActorTestRig.SpawnMerchantNpc(crafterSession, npcTemplateId: 1101);
        GameplayActorTestRig.SetNpcPosition(crafterSession, crafterMerchant, TestPosition);

        var result = VillageFullDayCycle.Run(
        [
            farmer.Assemble(TestPosition),
            new VillageFullDayCycle.FullDayVillager
            {
                Name = "m8v4-skip-crafter",
                DaySpec = new VillageDayCycle.VillagerSpec
                {
                    Name = "m8v4-skip-crafter",
                    Profession = VillageDayCycle.VillageProfession.Crafter,
                    Actor = crafterActor,
                    Anchors = BotDailyAnchors.Template,
                    CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
                    {
                        CycleId = "m8v4-skip-craft",
                        CraftId = CraftId,
                        BenchObjId = benchObjId
                    },
                    CrafterPump = new VillagePump()
                },
                EveningOutputTemplateId = OutputItemId,
                EveningMerchantObjId = crafterMerchant,
                EveningHome = TestPosition
            }
        ], new VillageFullDayCycle.VillageFullDayOptions { CycleId = "m8v4-skip" }, new FullDayPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage.StartsWith("WORK-")).IsTrue();
        // Fail-closed: the evening never ran — no evening report, no market/move legs anywhere.
        await Assert.That(result.Report.DayReport).IsNotNull();
        await Assert.That(result.Report.EveningReport).IsNull();
        await Assert.That(result.TraceRecords.Any(r => r.Action is ActorActionType.Sell or ActorActionType.DepositMoney or ActorActionType.Move)).IsFalse();
        await Assert.That(result.Report.ChatterLinesTotal).IsEqualTo(0);
        // The day's partial state stands as the day left it (harvest banked, no deletion).
        await Assert.That(BankOnly(farmer.Session, PotatoId)).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task FullDay_EveningFailure_ReportsEveningStage_Isolates()
    {
        var good = SetupCrafter("m8v4-iso-good", eveningOutputCount: 3);
        var bad = SetupCrafter("m8v4-iso-bad", eveningOutputCount: 3, eveningOutputTemplateId: UnsellableItemId);

        var result = VillageFullDayCycle.Run(
        [
            good.Assemble(TestPosition),
            bad.Assemble(TestPosition, eveningOutputTemplateId: UnsellableItemId)
        ], new VillageFullDayCycle.VillageFullDayOptions { CycleId = "m8v4-iso" }, new FullDayPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("SELL-m8v4-iso-bad");
        // Both days completed; the healthy evening stands.
        var goodEntry = result.Report.Villagers.Single(v => v.Name == "m8v4-iso-good");
        var badEntry = result.Report.Villagers.Single(v => v.Name == "m8v4-iso-bad");
        await Assert.That(goodEntry.DayEntry).IsNotNull();
        await Assert.That(badEntry.DayEntry).IsNotNull();
        await Assert.That(goodEntry.EveningEntry!.MarketComplete).IsTrue();
        await Assert.That(goodEntry.EveningEntry.Refund).IsEqualTo(30);
        await Assert.That(badEntry.EveningEntry!.MarketComplete).IsFalse();
        await Assert.That(BagOnly(bad.Actor, UnsellableItemId)).IsEqualTo(3);
        await Assert.That(good.Actor.Character.Money2).IsEqualTo(30);
    }
    [Test]
    public async Task FullDay_SameCycleKeyRetry_NeverDuplicates()
    {
        var farmer = SetupFarmer("m8v4-retry-farmer", eveningOutputCount: 3);
        var crafter = SetupCrafter("m8v4-retry-crafter", eveningOutputCount: 2);
        var options = new VillageFullDayCycle.VillageFullDayOptions { CycleId = "m8v4-retry" };

        var first = VillageFullDayCycle.Run([farmer.Assemble(TestPosition), crafter.Assemble(TestPosition)],
            options, new FullDayPump());
        await Assert.That(first.Passed).IsTrue();

        // Rerun under the SAME cycle id: keyed day legs refuse the duplicate,
        // the evening never runs, and every balance/count is unchanged.
        var retry = VillageFullDayCycle.Run([farmer.Assemble(TestPosition), crafter.Assemble(TestPosition)],
            options, new FullDayPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.Report.EveningReport).IsNull();
        await Assert.That(farmer.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(crafter.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(farmer.Actor.Character.Money2).IsEqualTo(30);
        await Assert.That(crafter.Actor.Character.Money2).IsEqualTo(20);
        await Assert.That(BankPlusBag(farmer.Actor, OutputItemId)).IsEqualTo(0);
        await Assert.That(BankPlusBag(crafter.Actor, ProductItemId)).IsEqualTo(1);
        await Assert.That(crafter.Actor.Character.LaborPower).IsEqualTo((short)(100 - CraftLaborCost));
    }

    [Test]
    public async Task FullDay_Chatter_BudgetAcrossHalves()
    {
        var sink = new RecordingSink();
        var farmer = SetupFarmer("m8v4-chat-farmer", eveningOutputCount: 1);
        var crafter = SetupCrafter("m8v4-chat-crafter", eveningOutputCount: 1);

        var result = VillageFullDayCycle.Run(
        [
            farmer.Assemble(TestPosition),
            crafter.Assemble(TestPosition)
        ],
            new VillageFullDayCycle.VillageFullDayOptions
            {
                CycleId = "m8v4-chat",
                Evening = new VillageEveningCycle.VillageEveningOptions
                {
                    CycleId = "m8v4-chat-evening",
                    Chatter = new VillageEveningCycle.VillageChatterOptions
                    {
                        Enabled = true,
                        ZoneLinesPerEvening = 1,
                        MaxLinesPerVillager = 1
                    },
                    ChatterSink = sink
                }
            }, new FullDayPump());

        await Assert.That(result.Passed).IsTrue();
        // Budgets only — message text is never asserted.
        await Assert.That(sink.Lines.Count).IsEqualTo(1);
        await Assert.That(result.Report.ChatterLinesTotal).IsEqualTo(1);
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-chatter-budget" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-chatter-silence" && c.Passed)).IsTrue();
    }


    [Test]
    public async Task FullDay_RestartProjection_CompleteAndDeterministic()
    {
        var farmer = SetupFarmer("m8v4-proj-farmer", eveningOutputCount: 3);
        var crafter = SetupCrafter("m8v4-proj-crafter", eveningOutputCount: 2);
        var specs = new List<VillageFullDayCycle.FullDayVillager>
        {
            farmer.Assemble(AwayHome),
            crafter.Assemble(AwayHome)
        };

        var result = VillageFullDayCycle.Run(specs,
            new VillageFullDayCycle.VillageFullDayOptions { CycleId = "m8v4-proj" }, new FullDayPump());
        await Assert.That(result.Passed).IsTrue();

        // The rig-side single-restart variant: project (pre), drop the
        // in-memory result graph (simulated kill — only the persisted report
        // plus specs and live save-equivalent state remain), re-project from
        // the report alone (post), require identical canonical rows. The
        // kill-9 + MySQL mechanics themselves are the slice-2 E2E's over the
        // same tables; what is proven here is that every value the E2E
        // compares pre==post lives in the projection, deterministically and
        // round-trippably.
        var pre = VillageFullDayCycle.ProjectRestart(result, specs);
        await Assert.That(pre.Count).IsEqualTo(2);
        var persistedReport = result.Report;
        result = null!;
        var post = VillageFullDayCycle.ProjectRestart(
            new VillageFullDayCycle.VillageFullDayResult
            {
                Scenario = VillageFullDayCycle.ScenarioName,
                Report = persistedReport
            }, specs);

        await Assert.That(pre.Count).IsEqualTo(2);
        await Assert.That(post.Count).IsEqualTo(2);
        foreach (var (before, after) in pre.Zip(post))
            await Assert.That(after.ToCanonicalString()).IsEqualTo(before.ToCanonicalString());

        // Completeness: every value the E2E compares pre==post is present —
        // schedule round-trips anchors+phase, ledger matches live reads.
        var actors = specs.ToDictionary(s => s.Name, s => s.DaySpec.Actor);
        foreach (var projection in pre)
        {
            var actor = actors[projection.Name];
            var specAnchors = specs.Single(s => s.Name == projection.Name).DaySpec.Anchors;
            await Assert.That(BotSchedulePayload.TryReadAnchors(projection.ScheduleJson, out var anchors)).IsTrue();
            await Assert.That(anchors).IsEqualTo(specAnchors);
            await Assert.That(BotSchedulePayload.TryReadLastPhase(projection.ScheduleJson, out var phase)).IsTrue();
            await Assert.That(phase).IsEqualTo(BotSchedulePhase.Work);
            await Assert.That(projection.Labor).IsEqualTo(actor.Character.LaborPower);
            await Assert.That(projection.Money).IsEqualTo(actor.Character.Money);
            await Assert.That(projection.BankMoney).IsEqualTo(actor.Character.Money2);
            await Assert.That(projection.EveningOutputBankCount)
                .IsEqualTo(actor.Character.Inventory.GetItemsCount(SlotType.Bank, projection.EveningOutputTemplateId));
            await Assert.That(projection.EveningOutputBagCount)
                .IsEqualTo(actor.Character.Inventory.GetItemsCount(SlotType.Inventory, projection.EveningOutputTemplateId));
            await Assert.That(projection.ReturnedHome).IsTrue();
            await Assert.That(projection.EveningMarketComplete).IsTrue();
            // Round-trip sufficiency: the canonical row parses back whole.
            var parts = projection.ToCanonicalString().Split('|');
            await Assert.That(parts.Length).IsEqualTo(18);
            await Assert.That(parts[0]).IsEqualTo("v1");
            await Assert.That(parts[1]).IsEqualTo(projection.Name);
            await Assert.That(parts[5]).IsEqualTo(projection.Labor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // ------------------------------------------------------------ rig below


    private sealed record SetupVillager(
        VillageDayCycle.VillagerSpec Spec,
        GameplayActor Actor,
        HeadlessSession Session,
        uint MerchantObjId,
        string Name)
    {
        public VillageFullDayCycle.FullDayVillager Assemble(Vector3 home,
            uint? eveningOutputTemplateId = null, uint? eveningMerchantObjId = null)
            => new()
            {
                Name = Name,
                DaySpec = Spec,
                EveningOutputTemplateId = eveningOutputTemplateId ?? OutputItemId,
                EveningMerchantObjId = eveningMerchantObjId ?? MerchantObjId,
                EveningHome = home
            };
    }

    private SetupVillager SetupFarmer(string name, int eveningOutputCount, uint? seedItemId = null)
    {
        var (actor, session) = CreateActorOnUniqueWorld(name);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 1_000);
        StockSeed(actor);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(actor.Character.Transform.World.Position);
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        var merchant = StockEveningMarket(actor, session, eveningOutputCount, OutputItemId);
        return new SetupVillager(new VillageDayCycle.VillagerSpec
        {
            Name = name,
            Profession = VillageDayCycle.VillageProfession.Farmer,
            Actor = actor,
            Anchors = BotDailyAnchors.Template,
            CropObjId = crop.ObjId,
            FarmerSeedItemId = seedItemId ?? PotatoSeedId
        }, actor, session, merchant, name);
    }

    private SetupVillager SetupCrafter(string name, int eveningOutputCount, uint? eveningOutputTemplateId = null)
    {
        var (actor, session) = CreateActorOnUniqueWorld(name);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetMoney(actor, 1_000);
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        StockBank(actor, MatAItemId, MatAAmount);
        StockBank(actor, MatBItemId, MatBAmount);
        var merchant = StockEveningMarket(actor, session, eveningOutputCount, eveningOutputTemplateId ?? OutputItemId);
        return new SetupVillager(new VillageDayCycle.VillagerSpec
        {
            Name = name,
            Profession = VillageDayCycle.VillageProfession.Crafter,
            Actor = actor,
            Anchors = BotDailyAnchors.Template,
            CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
            {
                CycleId = $"m8v4-{name}-craft",
                CraftId = CraftId,
                BenchObjId = benchObjId
            },
            CrafterPump = new VillagePump()
        }, actor, session, merchant, name);
    }

    private static uint StockEveningMarket(GameplayActor actor, HeadlessSession session, int outputCount, uint templateId)
    {
        GameplayActorTestRig.SeedMerchantPack();
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1101);
        GameplayActorTestRig.SetNpcPosition(session, merchant, TestPosition);
        if (outputCount > 0)
        {
            actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.QuestSupplyItems, templateId, outputCount, 0);
            var deposit = actor.DepositItem(templateId);
            if (deposit.State != ActorLifecycleState.Completed)
                throw new InvalidOperationException($"StockEveningMarket setup failed: {deposit.Detail}");
        }
        return merchant;
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

    private static int BagOnly(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Inventory, templateId);

    private static int BankOnly(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

    private static int BankOnly(HeadlessSession session, uint templateId)
        => session.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

    private static int BankPlusBag(GameplayActor actor, uint templateId)
        => BankOnly(actor, templateId) + BagOnly(actor, templateId);

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

    private static void SeedEveningTemplates()
    {
        var templates = (Dictionary<uint, AAEmu.Game.Models.Game.Items.Templates.ItemTemplate>)
            typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        if (!templates.ContainsKey(OutputItemId))
            GameplayActorTestRig.SeedTradeItemTemplate(OutputItemId, price: 100, refund: OutputRefund, sellable: true);
        if (!templates.ContainsKey(UnsellableItemId))
            GameplayActorTestRig.SeedTradeItemTemplate(UnsellableItemId, price: 100, refund: 0, sellable: false);
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

    /// <summary>Headless craft pump (the slice-1 shape).</summary>
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

    /// <summary>Headless move pump (the slice-3 shape, on foot).</summary>
    private sealed class FullDayPump : IVillageMovePump
    {
        public ActorRequest Walk(GameplayActor actor, ActorRequest request, TimeSpan budget)
        {
            var guard = 0;
            while (!request.IsTerminal && guard++ < 120)
                actor.Tick(TimeSpan.FromSeconds(1));
            return request;
        }
    }

    private sealed class RecordingSink : IBotChatterSink
    {
        public List<(Character Speaker, string Message)> Lines { get; } = [];

        public void Say(Character speaker, string message) => Lines.Add((speaker, message));
    }
}
