using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 policy-extension rig — ECONOMY consumer: the deterministic
/// perception → legal-candidate → selection → actor-dispatch → terminal
/// postcondition decision path through <see cref="EconomyDecisionScenario"/>
/// on the fixture trade surface (the same rig conventions as
/// <see cref="EconomyDayCycleScenarioRigTests"/> / <see cref="GameplayActorTestRig"/>).
///
/// The scenario composes proposals for buy / deposit-money / withdraw-money /
/// deposit-item / withdraw-item; hard legality reads ONLY the immutable
/// observation context (Money/BankMoney/BagItemCounts/BankItemCounts) plus
/// perception-time ordinary service reads (merchant resolution + shop
/// range). Selection is deterministic (fixed priority, then tie-break key);
/// dispatch calls the existing IGameplayActor methods only.
///
/// No generated evidence files: these tests are pure asserts.
/// </summary>
[NotInParallel] // process-wide singletons (TeamManager/ItemManager/NpcManager) + ExecutionBoundary pin
public class EconomyDecisionScenarioRigTests
{
    private const uint BuyTemplateId = 88_201;   // merchant-sold, price 50
    private const uint ItemTemplateId = 88_202;  // plain stackable item (deposit/withdraw item)
    private const int BuyPrice = 50;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedTradeSurface();
        GameplayActorTestRig.SeedTradeItemTemplate(BuyTemplateId, price: BuyPrice, refund: 0, sellable: false);
        GameplayActorTestRig.RegisterPlainItemTemplate(ItemTemplateId);
    }

    [After(Test)]
    public void TearDown()
    {
        ExecutionBoundary.ResetForTest();
    }

    private static (GameplayActor Actor, HeadlessSession Session, uint MerchantObjId) CreateEconomyActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(1000f, 1000f, 100f));
        var packId = GameplayActorTestRig.SeedMerchantPack(BuyTemplateId);
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1000, packId: packId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, new Vector3(1000f, 1000f, 100f));
        return (actor, session, merchantObjId);
    }

    [Test]
    public async Task EconomyDecision_HappyPath_HighestPriorityLegalActionWinsAndCompletes()
    {
        var (actor, _, merchantObjId) = CreateEconomyActor("m5ec-happy");
        GameplayActorTestRig.SetMoney(actor, 1000);
        GameplayActorTestRig.GrantItem(actor, ItemTemplateId, 2);

        var result = EconomyDecisionScenario.Run(actor.Character, new EconomyDecisionScenario.EconomyOptions
        {
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = BuyTemplateId,
            BuyCount = 1,
            ItemTemplateId = ItemTemplateId,
            DepositAmount = 100,
            WithdrawAmount = 50
        });

        // Buy (priority 20) beats deposit-money (10), deposit-item (8),
        // withdraw-money (5), withdraw-item (4) — all legal here.
        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Buy);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        // The buy actually landed through the real engine path.
        await Assert.That(GameplayActorTestRig.BagCount(actor, BuyTemplateId)).IsEqualTo(1);
        await Assert.That(actor.Character.Money).IsEqualTo(1000L - BuyPrice);
        // Decision-path evidence: the audit trace records the dispatched action.
        await Assert.That(result.TraceRecords.Select(r => r.Action)).Contains(ActorActionType.Buy);
        await Assert.That(result.TraceRecords.All(r => r.Result == AAEmu.Game.Core.Managers.Bots.ActorLifecycleState.Completed)).IsTrue();
        // Legality-before-preference: the legal lower-priority candidates
        // (deposit-money, deposit-item) were NOT rejected; the illegal ones
        // (withdraw-money with an empty bank, withdraw-item with an empty
        // warehouse) were rejected with the failed precondition named.
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.WithdrawMoney && r.Reason.Contains("not-enough-bank"))).IsTrue();
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.WithdrawItem && r.Reason.Contains("item-in-bank"))).IsTrue();
        await Assert.That(result.Rejections.Any(r => r.Proposal.Action == ActorActionType.DepositMoney)).IsFalse();
        await Assert.That(result.Rejections.Any(r => r.Proposal.Action == ActorActionType.DepositItem)).IsFalse();
    }

    [Test]
    public async Task EconomyDecision_LegalityBeforePreference_IllegalHighPriorityLosesToLegalLowPriority()
    {
        var (actor, _, merchantObjId) = CreateEconomyActor("m5ec-gate");
        // Not enough money for the buy (price 50 × 1) — the highest-priority
        // candidate is illegal. Deposit-money (10) is legal and must win.
        GameplayActorTestRig.SetMoney(actor, 10);
        GameplayActorTestRig.GrantItem(actor, ItemTemplateId, 2);

        var result = EconomyDecisionScenario.Run(actor.Character, new EconomyDecisionScenario.EconomyOptions
        {
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = BuyTemplateId,
            BuyCount = 1,
            ItemTemplateId = ItemTemplateId,
            DepositAmount = 5,
            WithdrawAmount = 50
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.DepositMoney);
        // The illegal buy was rejected with the failed precondition named.
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Buy && r.Reason.Contains("not-enough-money"))).IsTrue();
        // The deposit actually landed.
        await Assert.That(actor.Character.Money2).IsEqualTo(5L);
        await Assert.That(actor.Character.Money).IsEqualTo(5L);
    }

    [Test]
    public async Task EconomyDecision_NoLegalProposal_FailsClosedWithRejections()
    {
        var (actor, _, merchantObjId) = CreateEconomyActor("m5ec-none");
        // Empty bag, no money, no bank balance: every candidate is illegal.
        GameplayActorTestRig.SetMoney(actor, 0);

        var result = EconomyDecisionScenario.Run(actor.Character, new EconomyDecisionScenario.EconomyOptions
        {
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = BuyTemplateId,
            BuyCount = 1,
            ItemTemplateId = ItemTemplateId,
            DepositAmount = 5,
            WithdrawAmount = 5
        });

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("DECIDE");
        await Assert.That(result.SelectedAction).IsNull();
        // Every candidate was rejected by a named hard precondition.
        await Assert.That(result.Rejections.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(result.Rejections.Any(r => r.Reason.Contains("not-enough-money"))).IsTrue();
        await Assert.That(result.Rejections.Any(r => r.Reason.Contains("item-in-bag"))).IsTrue();
        await Assert.That(result.Rejections.Any(r => r.Reason.Contains("item-in-bank"))).IsTrue();
        // Nothing executed: the only audit record is the perception Observe
        // query itself (no action was dispatched).
        await Assert.That(result.TraceRecords).IsNotEmpty();
        await Assert.That(result.TraceRecords.All(r => r.Action == ActorActionType.Observe)).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(0L);
        await Assert.That(actor.Character.Money2).IsEqualTo(0L);
    }

    [Test]
    public async Task EconomyDecision_TerminalPostconditionHonored_TrueAndFalseCases()
    {
        // Case 1: honest postcondition — deposit lands, bank delta observed.
        var (actor, _, merchantObjId) = CreateEconomyActor("m5ec-post-true");
        GameplayActorTestRig.SetMoney(actor, 1000);
        GameplayActorTestRig.GrantItem(actor, ItemTemplateId, 2);

        var result = EconomyDecisionScenario.Run(actor.Character, new EconomyDecisionScenario.EconomyOptions
        {
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = BuyTemplateId,
            BuyCount = 1,
            ItemTemplateId = ItemTemplateId,
            DepositAmount = 100,
            WithdrawAmount = 50
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "terminal-postcondition-satisfied" && c.Passed)).IsTrue();

        // Case 2: dishonest postcondition — the scenario's own postcondition
        // is honest, so prove the cycle honors a WRONG expectation by
        // dispatching a proposal whose postcondition cannot hold: withdraw
        // with an impossible expectation (bag must hold 1_000_000 copper).
        var (actor2, _, merchantObjId2) = CreateEconomyActor("m5ec-post-false");
        GameplayActorTestRig.SetMoney(actor2, 1000);
        GameplayActorTestRig.GrantItem(actor2, ItemTemplateId, 2);
        // Seed the bank BEFORE the perception capture so the withdraw is
        // legal at selection time (the cycle re-selects against the
        // perception snapshot).
        actor2.Character.Money2 = 100;
        var context = BotObservedContext.Capture(actor2);
        var proposal = new BotDecisionProposal(
            goal: "economy.withdraw-money",
            action: ActorActionType.WithdrawMoney,
            targetId: 0,
            expectedPostcondition: new BotProposalPostcondition(
                "inventory holds at least 1000000 copper",
                observed => observed.Money >= 1_000_000),
            idempotencyKey: $"economy:{actor2.Character.Id}:1:withdraw-money",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "withdraw copper from the bank",
            policyVersion: "economy-v1",
            priority: 5,
            tieBreakKey: "withdraw-money",
            payload: 50L,
            hardPreconditions:
            [
                new BotProposalPrecondition("amount-positive", observed => 50L > 0),
                new BotProposalPrecondition("not-enough-bank", observed => observed.BankMoney >= 50L)
            ]);
        var execution = BotDecisionCycle.Execute(actor2, context, proposal,
            static (gameplayActor, p) => gameplayActor.WithdrawMoney((long)p.Payload!, p.IdempotencyKey));

        await Assert.That(execution.Request.State).IsEqualTo(AAEmu.Game.Core.Managers.Bots.ActorLifecycleState.Completed);
        // The action executed (money moved) but the dishonest postcondition
        // is reported unsatisfied — the cycle never fabricates success.
        await Assert.That(execution.ExpectedPostconditionSatisfied).IsFalse();
        await Assert.That(actor2.Character.Money).IsEqualTo(1050L);
    }

    [Test]
    public async Task EconomyDecision_ItemDepositAndWithdraw_ThroughRealContainerMovePath()
    {
        // Deposit-item (priority 8) beats withdraw-money (5) and
        // withdraw-item (4); buy is illegal (no money).
        var (actor, _, merchantObjId) = CreateEconomyActor("m5ec-item");
        GameplayActorTestRig.SetMoney(actor, 0);
        GameplayActorTestRig.GrantItem(actor, ItemTemplateId, 3);

        var result = EconomyDecisionScenario.Run(actor.Character, new EconomyDecisionScenario.EconomyOptions
        {
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = BuyTemplateId,
            BuyCount = 1,
            ItemTemplateId = ItemTemplateId,
            DepositAmount = 5,
            WithdrawAmount = 5
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.DepositItem);
        // The whole stack moved through the real container path.
        await Assert.That(actor.Character.Inventory.GetItemsCount(AAEmu.Game.Models.Game.Items.SlotType.Inventory, ItemTemplateId)).IsEqualTo(0);
        await Assert.That(actor.Character.Inventory.GetItemsCount(AAEmu.Game.Models.Game.Items.SlotType.Bank, ItemTemplateId)).IsEqualTo(3);
        await Assert.That(result.TraceRecords.Select(r => r.Action)).Contains(ActorActionType.DepositItem);
    }
}
