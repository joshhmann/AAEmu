using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 economy actions (t_7c224245) — Deposit/Withdraw on the
/// IGameplayActor v2 surface through REAL engine paths:
///  - money: Character.ChangeMoney — the exact call CSDepositMoneyPacket /
///    CSWithdrawMoneyPacket make (engine-validated balance, refuses when
///    insufficient);
///  - items: Inventory.SplitOrMoveItem — the exact call CSSwapItemsPacket
///    makes for Inventory↔Bank container moves (whole stack; engine
///    validates source item, slot, container acceptance and target
///    capacity).
/// Contract tests run headless — no controller, no client, no packets
/// (Unit.SendPacket is null-safe without a Connection).
///
/// Idempotency proofs (acceptance criterion 3 — retries and timeouts must
/// not duplicate items, currency or interactions):
///  - same-key retry: rejected pre-flight by the effect ledger (no Running
///    transition, balances/quantities untouched);
///  - fresh-key retry after a success: the engine-true backstop refuses —
///    the source container no longer holds the item / the balance can no
///    longer cover the amount — so nothing executes twice.
/// </summary>
[NotInParallel]
public class GameplayActorM51DepositWithdrawTests
{
    private const uint BankItemTemplateId = 92_101;

    #region DepositMoney — real engine path

    [Test]
    public async Task DepositMoney_CompletesThroughRealEnginePath_MoneyMovesToBank()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-money-1");
        actor.Character.Money = 5000;

        var request = actor.DepositMoney(2000);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(2000L);
        // Real engine path: inventory balance fell, bank balance rose.
        await Assert.That(actor.Character.Money).IsEqualTo(3000L);
        await Assert.That(actor.Character.Money2).IsEqualTo(2000L);

        // Full audit record shape.
        var record = actor.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.DepositMoney);
        await Assert.That(record.TargetId).IsEqualTo(0u);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First().StartsWith("Requested")).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (depositing"))).IsTrue();
    }

    [Test]
    public async Task DepositMoney_NotEnoughMoney_Rejected_NoBalanceChange()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-money-2");
        actor.Character.Money = 100;

        var request = actor.DepositMoney(200);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("refused by engine")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.Money).IsEqualTo(100L);
        await Assert.That(actor.Character.Money2).IsEqualTo(0L);
    }

    [Test]
    public async Task DepositMoney_NonPositiveAmount_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-money-3");
        actor.Character.Money = 100;

        foreach (var amount in new[] { 0L, -5L })
        {
            var request = actor.DepositMoney(amount);
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
            await Assert.That(request.Detail?.Contains("amount must be positive")).IsTrue();
        }

        // Nothing moved.
        await Assert.That(actor.Character.Money).IsEqualTo(100L);
        await Assert.That(actor.Character.Money2).IsEqualTo(0L);
    }

    [Test]
    public async Task DepositMoney_AuditRecord_ToJson_CarriesFullTraceShape()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-money-4");
        actor.Character.Money = 5000;

        actor.DepositMoney(1000);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("DepositMoney");
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
    }

    #endregion

    #region WithdrawMoney — real engine path

    [Test]
    public async Task WithdrawMoney_CompletesThroughRealEnginePath_MoneyMovesToInventory()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-wd-money-1");
        actor.Character.Money2 = 5000;

        var request = actor.WithdrawMoney(2000);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(2000L);
        await Assert.That(actor.Character.Money).IsEqualTo(2000L);
        await Assert.That(actor.Character.Money2).IsEqualTo(3000L);

        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.WithdrawMoney);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (withdrawing"))).IsTrue();
    }

    [Test]
    public async Task WithdrawMoney_NotEnoughBankMoney_Rejected_NoBalanceChange()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-wd-money-2");
        actor.Character.Money2 = 100;

        var request = actor.WithdrawMoney(200);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("refused by engine")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(0L);
        await Assert.That(actor.Character.Money2).IsEqualTo(100L);
    }

    #endregion

    #region DepositItem — real container-move path

    [Test]
    public async Task DepositItem_CompletesThroughRealContainerMovePath_StackMovesToBank()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-dep-item-1");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);

        var request = actor.DepositItem(BankItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(3);
        // Real container move: bag emptied, warehouse holds the stack.
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(0);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(3);

        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.DepositItem);
        await Assert.That(record.TargetId).IsEqualTo(BankItemTemplateId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (depositing item"))).IsTrue();
    }

    [Test]
    public async Task DepositItem_MergesIntoExistingBankStack_WhenRoom()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-dep-item-merge");
        // Stackable template with MaxCount 99.
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        // Bank already holds 2 of the template (through the real move path).
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 2);
        actor.DepositItem(BankItemTemplateId);
        // Bag holds 3 more.
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);

        var request = actor.DepositItem(BankItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // Engine merge (doMerge): one stack of 5 in the bank, none in the bag.
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(5);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(0);
        await Assert.That(session.Character.Inventory.Warehouse.GetAllItemsByTemplate(BankItemTemplateId, -1, out var stacks, out _)).IsTrue();
        await Assert.That(stacks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DepositItem_NotInBag_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-item-2");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);

        var request = actor.DepositItem(BankItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in bag")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task DepositItem_BankFull_Rejected_NoItemMoved()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-dep-item-full");
        // A NON-stackable template (MaxCount=1 — each unit its own slot):
        // the merge path can't absorb the second deposit, so the real
        // container capacity check must refuse it.
        const uint singleTemplateId = 92_102;
        GameplayActorTestRig.RegisterPlainItemTemplate(singleTemplateId, maxCount: 1);
        // Shrink the bank to a single slot.
        actor.Character.Inventory.Warehouse.ContainerSize = 1;
        GameplayActorTestRig.StockItem(session, singleTemplateId, 1);
        await Assert.That(actor.DepositItem(singleTemplateId).State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.StockItem(session, singleTemplateId, 1);
        var request = actor.DepositItem(singleTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("bank is full")).IsTrue();
        // Nothing moved: the bag still holds its stack.
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, singleTemplateId)).IsEqualTo(1);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, singleTemplateId)).IsEqualTo(1);
    }

    #endregion

    #region WithdrawItem — real container-move path

    [Test]
    public async Task WithdrawItem_CompletesThroughRealContainerMovePath_StackMovesToBag()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-wd-item-1");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        // Stock the bank through the real deposit path, then withdraw.
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);
        actor.DepositItem(BankItemTemplateId);

        var request = actor.WithdrawItem(BankItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(3);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(0);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(3);

        var record = actor.AuditTrace[1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.WithdrawItem);
        await Assert.That(record.TargetId).IsEqualTo(BankItemTemplateId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task WithdrawItem_NotInBank_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-wd-item-2");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);

        var request = actor.WithdrawItem(BankItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in bank")).IsTrue();
    }

    #endregion

    #region Money retry idempotency — no duplicate deposits/withdrawals

    [Test]
    public async Task DepositMoney_RetrySameKey_RejectedPreFlight_NoDoubleDeposit()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-money-retry-1");
        actor.Character.Money = 5000;

        var original = actor.DepositMoney(2000, idempotencyKey: "dep-money:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight by the ledger; the audit record shows no Running.
        var retry = actor.DepositMoney(2000, idempotencyKey: "dep-money:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Balances untouched by the retry — exactly one deposit landed.
        await Assert.That(actor.Character.Money).IsEqualTo(3000L);
        await Assert.That(actor.Character.Money2).IsEqualTo(2000L);

        // A THIRD retry is refused too (the refusal never replaced the lock).
        var third = actor.DepositMoney(2000, idempotencyKey: "dep-money:1");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);

        // Correlation: the key still resolves to the ORIGINAL outcome.
        var correlated = actor.FindByKey("dep-money:1");
        await Assert.That(correlated).IsNotNull();
        await Assert.That(correlated!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(correlated.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task DepositMoney_RetryFreshKeyAfterSuccess_LedgerPreFlight_NoDoubleDeposit()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-dep-money-retry-2");
        actor.Character.Money = 2000;

        // Timeout ambiguity: the controller retries with a FRESH key. The
        // effect ledger already carries the deposit credit (recorded after
        // the engine move) and the balance can no longer cover the amount —
        // the duplicate is refused pre-flight with no Running transition.
        // Exactly one deposit landed.
        await Assert.That(actor.DepositMoney(2000, idempotencyKey: "a").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.DepositMoney(2000, idempotencyKey: "b");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("already applied")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.Money2).IsEqualTo(2000L);
        await Assert.That(actor.Character.Money).IsEqualTo(0L);
    }

    [Test]
    public async Task WithdrawMoney_RetrySameKey_RejectedPreFlight_NoDoubleWithdrawal()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-wd-money-retry-1");
        actor.Character.Money2 = 5000;

        await Assert.That(actor.WithdrawMoney(2000, idempotencyKey: "wd-money:1").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.WithdrawMoney(2000, idempotencyKey: "wd-money:1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(actor.Character.Money2).IsEqualTo(3000L);
        await Assert.That(actor.Character.Money).IsEqualTo(2000L);
    }

    [Test]
    public async Task WithdrawMoney_RetryFreshKeyAfterSuccess_LedgerPreFlight_NoDoubleWithdrawal()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-wd-money-retry-2");
        actor.Character.Money2 = 2000;

        await Assert.That(actor.WithdrawMoney(2000, idempotencyKey: "a").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.WithdrawMoney(2000, idempotencyKey: "b");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("already applied")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.Money).IsEqualTo(2000L);
        await Assert.That(actor.Character.Money2).IsEqualTo(0L);
    }

    #endregion

    #region Item retry idempotency — no duplicate deposits/withdrawals

    [Test]
    public async Task DepositItem_RetrySameKey_RejectedPreFlight_NoDoubleMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-dep-item-retry-1");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);

        var original = actor.DepositItem(BankItemTemplateId, idempotencyKey: "dep-item:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        var retry = actor.DepositItem(BankItemTemplateId, idempotencyKey: "dep-item:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Quantities untouched by the retry — exactly one move landed.
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(0);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(3);

        // Correlation: the key still resolves to the ORIGINAL outcome.
        var correlated = actor.FindByKey("dep-item:1");
        await Assert.That(correlated).IsNotNull();
        await Assert.That(correlated!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(correlated.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task DepositItem_RetryFreshKeyAfterSuccess_ItemGoneFromBag_NoDoubleMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-dep-item-retry-2");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);

        // Timeout ambiguity: fresh-key retry. The source container no
        // longer holds the template, so the retry refuses before any
        // engine call — the bank cannot receive a second copy.
        await Assert.That(actor.DepositItem(BankItemTemplateId, idempotencyKey: "a").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.DepositItem(BankItemTemplateId, idempotencyKey: "b");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("not found in bag")).IsTrue();
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(3);
    }

    [Test]
    public async Task WithdrawItem_RetrySameKey_RejectedPreFlight_NoDoubleMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-wd-item-retry-1");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);
        actor.DepositItem(BankItemTemplateId);

        await Assert.That(actor.WithdrawItem(BankItemTemplateId, idempotencyKey: "wd-item:1").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.WithdrawItem(BankItemTemplateId, idempotencyKey: "wd-item:1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(0);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(3);
    }

    [Test]
    public async Task WithdrawItem_RetryFreshKeyAfterSuccess_ItemGoneFromBank_NoDoubleMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-wd-item-retry-2");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 3);
        actor.DepositItem(BankItemTemplateId);

        await Assert.That(actor.WithdrawItem(BankItemTemplateId, idempotencyKey: "a").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.WithdrawItem(BankItemTemplateId, idempotencyKey: "b");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("not found in bank")).IsTrue();
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(3);
    }

    #endregion

    #region Replay-sequence hook — the Phase 2 M3a/M4 replay shape

    /// <summary>
    /// One recorded deposit/withdraw op (the replay vocabulary the Phase 2
    /// economic replay drives). The runner fires these through the actor
    /// contract; this test replays the same sequence a controller would.
    /// </summary>
    private sealed record ReplayOp(ActorActionType Action, long Amount, uint ItemTemplateId, string Key);

    [Test]
    public async Task ReplaySequence_MoneyAndItemCycle_BalancesHold_NoDuplicateOnReplay()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-replay-1");
        GameplayActorTestRig.RegisterPlainItemTemplate(BankItemTemplateId);
        actor.Character.Money = 5000;
        GameplayActorTestRig.StockItem(session, BankItemTemplateId, 5);

        // A recorded economic sequence: deposit 1000 → withdraw 400 →
        // deposit item → withdraw item.
        var ops = new[]
        {
            new ReplayOp(ActorActionType.DepositMoney, 1000, 0, "r1"),
            new ReplayOp(ActorActionType.WithdrawMoney, 400, 0, "r2"),
            new ReplayOp(ActorActionType.DepositItem, 0, BankItemTemplateId, "r3"),
            new ReplayOp(ActorActionType.WithdrawItem, 0, BankItemTemplateId, "r4")
        };

        foreach (var op in ops)
        {
            var request = op.Action switch
            {
                ActorActionType.DepositMoney => actor.DepositMoney(op.Amount, op.Key),
                ActorActionType.WithdrawMoney => actor.WithdrawMoney(op.Amount, op.Key),
                ActorActionType.DepositItem => actor.DepositItem(op.ItemTemplateId, op.Key),
                _ => actor.WithdrawItem(op.ItemTemplateId, op.Key)
            };
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        }

        // Post-sequence state (the same numbers the scenario template's
        // acceptance criteria verify).
        await Assert.That(actor.Character.Money).IsEqualTo(4400L);
        await Assert.That(actor.Character.Money2).IsEqualTo(600L);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, BankItemTemplateId)).IsEqualTo(5);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(0);

        // Four audit records, one per op, all Completed.
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(4);
        await Assert.That(actor.AuditTrace.All(r => r.Result == ActorLifecycleState.Completed)).IsTrue();

        // Replaying the SAME recorded sequence with the same keys is a
        // no-op: every op is refused pre-flight, nothing moves twice.
        foreach (var op in ops)
        {
            var replay = op.Action switch
            {
                ActorActionType.DepositMoney => actor.DepositMoney(op.Amount, op.Key),
                ActorActionType.WithdrawMoney => actor.WithdrawMoney(op.Amount, op.Key),
                ActorActionType.DepositItem => actor.DepositItem(op.ItemTemplateId, op.Key),
                _ => actor.WithdrawItem(op.ItemTemplateId, op.Key)
            };
            await Assert.That(replay.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(replay.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        }

        await Assert.That(actor.Character.Money2).IsEqualTo(600L);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, BankItemTemplateId)).IsEqualTo(0);
    }

    #endregion
}
