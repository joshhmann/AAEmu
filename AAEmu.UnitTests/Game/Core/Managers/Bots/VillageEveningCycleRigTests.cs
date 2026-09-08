using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C5-S3 village evening (ROADMAP M8 living-village contracts) — withdraw
/// → merchant-sell → deposit → return-home through
/// <see cref="VillageEveningCycle"/>, plus the C2 chatter pass under budgets:
/// every market leg calls the EXISTING <see cref="GameplayActor"/> actions
/// unchanged (WithdrawItem / Sell / DepositMoney / MoveTo — the hauler
/// slice-3 sale→deposit→return-home leg order adapted to an immediate
/// merchant refund), and chatter lines are counted under a zone cap with
/// silence for incomplete legs and battling villagers.
///
/// Fail-pre discipline: every test FAILS without the composer (CS0246 —
/// no such type) and on the corresponding composition defect (skipped
/// deposit, duplicated sale on retry, skipped return leg, over-budget
/// chatter, chatter during battle, consumed output on a refused sale), and
/// PASSES on the composed real paths. No engine diffs: the composer calls
/// existing actions only (engine gates stay pinned by
/// GameplayActorM51BuySellTests / GameplayActorDepositTests).
///
/// Chatter note (honest): the rig pins budgets and silence ONLY — line
/// counts, caps, and hold reasons. Message text is never asserted.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class VillageEveningCycleRigTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);
    private static readonly Vector3 AwayHome = new(1020f, 1000f, 100f); // 20 units east: exercises the move pump

    // Fixture output (own ids — missing-only seeding, never overwrites):
    // 1 × output (99_041, refund 10, sellable) sells for 10c per unit.
    private const uint OutputItemId = 99_041;
    private const int OutputRefund = 10;
    private const uint UnsellableItemId = 99_042;

    private static uint s_nextWorldId = 0x9400_0000; // fresh base: 0x9000 slice-1 / 0x6300 hauler
    private readonly List<WorldInstance> _registeredWorlds = [];

    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.Seed();
        SeedFixtureTemplates();
        GameplayActorTestRig.SeedMerchantPack();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        // Headless persistence boundary (the VillageDayCycleRigTests
        // convention): dead-port MySQL fails fast instead of attempting
        // localhost:3306.
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
    public async Task Evening_SaleDepositReturn_CompletesAndConserves()
    {
        var alice = SetupVillager("m8v3-full-alice", outputCount: 3);
        var bob = SetupVillager("m8v3-full-bob", outputCount: 2);

        var result = VillageEveningCycle.Run(
        [
            alice.Spec(AwayHome),
            bob.Spec(AwayHome)
        ], new VillageEveningCycle.VillageEveningOptions { CycleId = "m8v3-full" }, new EveningPump());

        await Assert.That(result.Passed).IsTrue();
        // Exact market evidence per villager (refund 10c/unit, grade-0 M51 precedent).
        var aliceEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-full-alice");
        var bobEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-full-bob");
        await Assert.That(aliceEntry.Withdrawn).IsEqualTo(3);
        await Assert.That(aliceEntry.SoldCount).IsEqualTo(3);
        await Assert.That(aliceEntry.Refund).IsEqualTo(30);
        await Assert.That(aliceEntry.BankDeposited).IsEqualTo(30);
        await Assert.That(bobEntry.Refund).IsEqualTo(20);
        // Observable end state: refund banked exactly once, inventory net zero,
        // output gone from bank+bag, both villagers home.
        await Assert.That(alice.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(alice.Actor.Character.Money2).IsEqualTo(30);
        await Assert.That(bob.Actor.Character.Money2).IsEqualTo(20);
        await Assert.That(BankPlusBag(alice.Actor, OutputItemId)).IsEqualTo(0);
        await Assert.That(BankPlusBag(bob.Actor, OutputItemId)).IsEqualTo(0);
        foreach (var villager in new[] { alice, bob })
            await Assert.That(Vector3.Distance(villager.Actor.Character.Transform.World.Position, AwayHome))
                .IsLessThanOrEqualTo(GameplayActor.ArrivalRadius + 0.1f);
        // Composition evidence: all four leg actions in the trace, M5 law intact.
        foreach (var action in new[] { ActorActionType.WithdrawItem, ActorActionType.Sell, ActorActionType.DepositMoney, ActorActionType.Move })
            await Assert.That(result.TraceRecords.Any(r => r.Action == action)).IsTrue();
        foreach (var name in new[] { "evening-market-complete", "village-audit-complete", "evening-chatter-budget", "evening-chatter-silence" })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-deposit-conservation-m8v3-full-alice" && c.Passed)).IsTrue();
        // Chatter defaults OFF: markets ran, nobody spoke.
        await Assert.That(result.Report.ChatterLinesTotal).IsEqualTo(0);
    }

    [Test]
    public async Task Evening_NoSaleableOutput_HoldsWithReason()
    {
        var unbound = SetupVillager("m8v3-hold-unbound", outputCount: 3);
        var empty = SetupVillager("m8v3-hold-empty", outputCount: 0);

        var result = VillageEveningCycle.Run(
        [
            unbound.Spec(TestPosition, outputTemplateId: 0),
            empty.Spec(TestPosition)
        ], new VillageEveningCycle.VillageEveningOptions { CycleId = "m8v3-hold" }, new EveningPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var entry in result.Report.Villagers)
        {
            await Assert.That(entry.MarketComplete).IsFalse();
            await Assert.That(entry.Holds.Count).IsGreaterThan(0);
            await Assert.That(entry.Withdrawn).IsEqualTo(0);
            await Assert.That(entry.Refund).IsEqualTo(0);
        }
        // A refused withdraw still leaves its Rejected audit record (never
        // Completed, never Running): holds are evidenced, not silent.
        await Assert.That(result.TraceRecords.Any(r => r.Result == ActorLifecycleState.Completed)).IsFalse();
        await Assert.That(result.TraceRecords.Count).IsEqualTo(1);
        foreach (var record in result.TraceRecords)
        {
            await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        }
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-market-complete" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task Evening_UnsellableOutput_FailsClosedAndIsolates()
    {
        // Withdraw succeeds, then SELL refuses (Sellable=false): the output
        // must stay in the bag and the healthy villager must still complete.
        var good = SetupVillager("m8v3-iso-good", outputCount: 3);
        var bad = SetupVillager("m8v3-iso-bad", outputCount: 3, outputTemplateId: UnsellableItemId);

        var result = VillageEveningCycle.Run(
        [
            good.Spec(TestPosition),
            bad.Spec(TestPosition, outputTemplateId: UnsellableItemId)
        ], new VillageEveningCycle.VillageEveningOptions { CycleId = "m8v3-iso" }, new EveningPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("SELL-m8v3-iso-bad");
        var goodEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-iso-good");
        var badEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-iso-bad");
        await Assert.That(goodEntry.MarketComplete).IsTrue();
        await Assert.That(goodEntry.Refund).IsEqualTo(30);
        await Assert.That(badEntry.MarketComplete).IsFalse();
        // Fail-closed retention: the refused output was never consumed.
        await Assert.That(BagOnly(bad.Actor, UnsellableItemId)).IsEqualTo(3);
        await Assert.That(BankOnly(bad.Actor, UnsellableItemId)).IsEqualTo(0);
        await Assert.That(bad.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(result.Criteria.Any(c => c.Name == "sell-hold-output-retained" && c.Passed)).IsTrue();
        // The healthy villager's evening stands untouched by the failure.
        await Assert.That(good.Actor.Character.Money2).IsEqualTo(30);
    }

    [Test]
    public async Task Evening_SameCycleKeyRetry_NeverDuplicates()
    {
        var alice = SetupVillager("m8v3-retry-alice", outputCount: 3);
        var options = new VillageEveningCycle.VillageEveningOptions { CycleId = "m8v3-retry" };

        var first = VillageEveningCycle.Run([alice.Spec(TestPosition)], options, new EveningPump());
        await Assert.That(first.Passed).IsTrue();
        await Assert.That(alice.Actor.Character.Money2).IsEqualTo(30);

        // Rerun under the SAME cycle id: the request-key dedupe refuses the
        // duplicate withdraw pre-flight (the slice-1 retry shape) — the
        // evening fails closed with zero state change: no second withdraw,
        // sale, or deposit, exactly one banked refund in the world.
        var retry = VillageEveningCycle.Run([alice.Spec(TestPosition)], options, new EveningPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("WITHDRAW-m8v3-retry-alice");
        var entry = retry.Report.Villagers.Single();
        await Assert.That(entry.MarketComplete).IsFalse();
        await Assert.That(alice.Actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(alice.Actor.Character.Money2).IsEqualTo(30);
        await Assert.That(BankPlusBag(alice.Actor, OutputItemId)).IsEqualTo(0);
        await Assert.That(retry.TraceRecords.Where(r => r.Result == ActorLifecycleState.Completed)
            .Any(r => r.Action is ActorActionType.WithdrawItem or ActorActionType.Sell or ActorActionType.DepositMoney)).IsFalse();
    }

    [Test]
    public async Task Evening_Chatter_ZoneBudget_CapsLines()
    {
        var sink = new RecordingSink();
        var villagers = new[] { "m8v3-chat-a", "m8v3-chat-b", "m8v3-chat-c" }
            .Select(name => SetupVillager(name, outputCount: 1))
            .ToList();

        var result = VillageEveningCycle.Run(
            villagers.Select(v => v.Spec(TestPosition)).ToList(),
            new VillageEveningCycle.VillageEveningOptions
            {
                CycleId = "m8v3-chat",
                Chatter = new VillageEveningCycle.VillageChatterOptions
                {
                    Enabled = true,
                    ZoneLinesPerEvening = 1,
                    MaxLinesPerVillager = 1
                },
                ChatterSink = sink
            }, new EveningPump());

        await Assert.That(result.Passed).IsTrue();
        // Budgets only — message text is never asserted (chatter note above).
        await Assert.That(sink.Lines.Count).IsEqualTo(1);
        await Assert.That(result.Report.ChatterLinesTotal).IsEqualTo(1);
        await Assert.That(result.Report.Villagers.Count(v => v.LinesSpoken == 1)).IsEqualTo(1);
        await Assert.That(result.Report.Villagers.Where(v => v.LinesSpoken == 0)
            .All(v => v.ChatterHold == "zone budget exhausted")).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-chatter-budget" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-chatter-silence" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task Evening_Chatter_Silence_InBattleAndAtWork()
    {
        var sink = new RecordingSink();
        var healthy = SetupVillager("m8v3-sil-healthy", outputCount: 1);
        var battler = SetupVillager("m8v3-sil-battler", outputCount: 1);
        battler.Actor.Character.IsInBattle = true;
        var idle = SetupVillager("m8v3-sil-idle", outputCount: 0);

        var result = VillageEveningCycle.Run(
        [
            healthy.Spec(TestPosition),
            battler.Spec(TestPosition),
            idle.Spec(TestPosition)
        ],
            new VillageEveningCycle.VillageEveningOptions
            {
                CycleId = "m8v3-sil",
                Chatter = new VillageEveningCycle.VillageChatterOptions
                {
                    Enabled = true,
                    ZoneLinesPerEvening = 10,
                    MaxLinesPerVillager = 1
                },
                ChatterSink = sink
            }, new EveningPump());

        await Assert.That(result.Passed).IsTrue();
        // Budget is ample: only silence rules suppress lines — counts and
        // hold reasons only, never content.
        await Assert.That(sink.Lines.Count).IsEqualTo(1);
        await Assert.That(sink.Lines[0].Speaker.Id).IsEqualTo(healthy.Actor.Character.Id);
        var healthyEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-sil-healthy");
        var battlerEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-sil-battler");
        var idleEntry = result.Report.Villagers.Single(v => v.Name == "m8v3-sil-idle");
        await Assert.That(healthyEntry.LinesSpoken).IsEqualTo(1);
        await Assert.That(battlerEntry.LinesSpoken).IsEqualTo(0);
        await Assert.That(battlerEntry.ChatterHold).IsEqualTo("in battle — silent");
        await Assert.That(idleEntry.LinesSpoken).IsEqualTo(0);
        await Assert.That(idleEntry.ChatterHold).IsEqualTo("legs incomplete — silent while at work");
        await Assert.That(result.Criteria.Any(c => c.Name == "evening-chatter-silence" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task Evening_Chatter_DisabledByDefault_Silent()
    {
        var alice = SetupVillager("m8v3-off-alice", outputCount: 2);
        var bob = SetupVillager("m8v3-off-bob", outputCount: 2);

        // Default options: chatter gate off, no sink bound — markets run, silence holds.
        var result = VillageEveningCycle.Run(
        [
            alice.Spec(TestPosition),
            bob.Spec(TestPosition)
        ], new VillageEveningCycle.VillageEveningOptions { CycleId = "m8v3-off" }, new EveningPump());

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Report.ChatterLinesTotal).IsEqualTo(0);
        foreach (var entry in result.Report.Villagers)
        {
            await Assert.That(entry.LinesSpoken).IsEqualTo(0);
            await Assert.That(entry.ChatterHold).IsEqualTo("chatter disabled");
            await Assert.That(entry.MarketComplete).IsTrue();
        }
    }

    // ------------------------------------------------------------ rig below

    private sealed record EveningVillager(GameplayActor Actor, HeadlessSession Session, uint MerchantObjId, string Name)
    {
        public VillageEveningCycle.VillagerEveningSpec Spec(Vector3 home, uint? outputTemplateId = null, uint? merchantObjId = null)
            => new()
            {
                Name = Name,
                Actor = Actor,
                OutputTemplateId = outputTemplateId ?? OutputItemId,
                MerchantObjId = merchantObjId ?? MerchantObjId,
                Home = home
            };
    }

    private EveningVillager SetupVillager(string name, int outputCount, uint? outputTemplateId = null)
    {
        var (actor, session) = CreateActorOnUniqueWorld(name);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.SeedMerchantPack();
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1101);
        GameplayActorTestRig.SetNpcPosition(session, merchant, TestPosition);
        if (outputCount > 0)
        {
            var tid = outputTemplateId ?? OutputItemId;
            actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.QuestSupplyItems, tid, outputCount, 0);
            var deposit = actor.DepositItem(tid);
            if (deposit.State != ActorLifecycleState.Completed)
                throw new InvalidOperationException($"StockBank setup failed for {name}: {deposit.Detail}");
        }
        return new EveningVillager(actor, session, merchant, name);
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

    private static void SeedFixtureTemplates()
    {
        // Missing-only: fixture owns the 99_04x ids; seeded once, never
        // overwritten with different values (SeedTradeItemTemplate updates in
        // place, so guard on existence first).
        var templates = (System.Collections.Generic.Dictionary<uint, AAEmu.Game.Models.Game.Items.Templates.ItemTemplate>)
            typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        if (!templates.ContainsKey(OutputItemId))
            GameplayActorTestRig.SeedTradeItemTemplate(OutputItemId, price: 100, refund: OutputRefund, sellable: true);
        if (!templates.ContainsKey(UnsellableItemId))
            GameplayActorTestRig.SeedTradeItemTemplate(UnsellableItemId, price: 100, refund: 0, sellable: false);
    }

    private static int BagOnly(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Inventory, templateId);

    private static int BankOnly(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Bank, templateId);

    private static int BankPlusBag(GameplayActor actor, uint templateId)
        => BankOnly(actor, templateId) + BagOnly(actor, templateId);

    /// <summary>Headless move pump (the HaulerSaleDepositCycleTests shape, on foot).</summary>
    private sealed class EveningPump : IVillageMovePump
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
