using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// TRADE-01 rig: the direct player-to-player trade handshake driven through
/// the contract actions (TradeOffer / TradePutup / TradeLockOk) over the
/// REAL TradeManager engine path (the CS{CanStart,Start}TradePacket,
/// CSPutupTradeItem, CSTradeLock, CSTradeOk packet calls). Two ordinary
/// Characters share one world and one real TradeManager per test.
/// </summary>
[NotInParallel]
public class TradeHandshakeScenarioRigTests
{
    /// <summary>Stackable trade item offered by the initiator.</summary>
    public const uint ItemA = 93_001;

    /// <summary>Stackable trade item offered by the counterpart.</summary>
    public const uint ItemB = 93_002;

    [Test]
    public async Task TradeHandshake_ItemsAndMoneyBothSides_InventoriesAndMoneyUpdated()
    {
        GameplayActorTestRig.ForceSeedTradeManager();
        var (actorA, sessionA) = GameplayActorTestRig.CreateActor("trade-happy-a");
        var (actorB, _) = GameplayActorTestRig.CreateActor("trade-happy-b");
        GameplayActorTestRig.JoinActorWorld(sessionA, actorB);
        GameplayActorTestRig.SeedItemTemplate(ItemA);
        GameplayActorTestRig.SeedItemTemplate(ItemB);
        GameplayActorTestRig.GrantItem(actorA, ItemA, 5);
        GameplayActorTestRig.GrantItem(actorB, ItemB, 3);
        GameplayActorTestRig.SetMoney(actorA, 1_000);
        GameplayActorTestRig.SetMoney(actorB, 500);

        // Handshake: open, both sides put up item + money, lock + ok twice.
        await Assert.That(actorA.TradeOffer(actorB.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actorA.TradePutup(ItemA, 5).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actorB.TradePutup(ItemB, 3).State).IsEqualTo(ActorLifecycleState.Completed);
        TradeManager.Instance.AddMoney(actorA.Character, 300);
        TradeManager.Instance.AddMoney(actorB.Character, 200);

        var lockA = actorA.TradeLockOk();
        await Assert.That(lockA.State).IsEqualTo(ActorLifecycleState.Completed);
        var lockB = actorB.TradeLockOk();
        await Assert.That(lockB.State).IsEqualTo(ActorLifecycleState.Completed);

        // Session closed for both.
        await Assert.That(TradeManager.Instance.IsInTrade(actorA.ActorId)).IsFalse();
        await Assert.That(TradeManager.Instance.IsInTrade(actorB.ActorId)).IsFalse();

        // Items swapped.
        await Assert.That(GameplayActorTestRig.BagCount(actorA, ItemA)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actorA, ItemB)).IsEqualTo(3);
        await Assert.That(GameplayActorTestRig.BagCount(actorB, ItemB)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actorB, ItemA)).IsEqualTo(5);

        // Money swapped.
        await Assert.That(actorA.Character.Money).IsEqualTo(1_000 - 300 + 200);
        await Assert.That(actorB.Character.Money).IsEqualTo(500 - 200 + 300);
    }

    [Test]
    public async Task TradeHandshake_PartialStackOffer_SplitsCountCorrectly()
    {
        GameplayActorTestRig.ForceSeedTradeManager();
        var (actorA, sessionA) = GameplayActorTestRig.CreateActor("trade-split-a");
        var (actorB, _) = GameplayActorTestRig.CreateActor("trade-split-b");
        GameplayActorTestRig.JoinActorWorld(sessionA, actorB);
        GameplayActorTestRig.SeedItemTemplate(ItemA);
        GameplayActorTestRig.GrantItem(actorA, ItemA, 10);
        GameplayActorTestRig.SetMoney(actorA, 0);
        GameplayActorTestRig.SetMoney(actorB, 0);

        // Offer only 4 of the 10-unit stack.
        await Assert.That(actorA.TradeOffer(actorB.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        var putup = actorA.TradePutup(ItemA, 4);
        await Assert.That(putup.State, putup.Detail).IsEqualTo(ActorLifecycleState.Completed);

        _ = actorA.TradeLockOk();
        var lockB = actorB.TradeLockOk();
        await Assert.That(lockB.State, lockB.Detail).IsEqualTo(ActorLifecycleState.Completed);

        // Sender keeps 6 units in the original stack; receiver gains exactly 4.
        await Assert.That(GameplayActorTestRig.BagCount(actorA, ItemA)).IsEqualTo(6);
        await Assert.That(GameplayActorTestRig.FindBagItem(actorA, ItemA)?.Count).IsEqualTo(6);
        await Assert.That(GameplayActorTestRig.BagCount(actorB, ItemA)).IsEqualTo(4);
        await Assert.That(GameplayActorTestRig.FindBagItem(actorB, ItemA)?.Count).IsEqualTo(4);

        // Nothing leaked: totals conserved.
        await Assert.That(GameplayActorTestRig.BagCount(actorA, ItemA) + GameplayActorTestRig.BagCount(actorB, ItemA)).IsEqualTo(10);
    }

    [Test]
    public async Task TradeHandshake_ReceiverOutOfSpace_FailsClosedWithoutCrashOrCorruption()
    {
        GameplayActorTestRig.ForceSeedTradeManager();
        var (actorA, sessionA) = GameplayActorTestRig.CreateActor("trade-space-a");
        var (actorB, _) = GameplayActorTestRig.CreateActor("trade-space-b");
        GameplayActorTestRig.JoinActorWorld(sessionA, actorB);
        GameplayActorTestRig.SeedItemTemplate(ItemA);
        GameplayActorTestRig.GrantItem(actorA, ItemA, 2);
        GameplayActorTestRig.SetMoney(actorA, 100);
        GameplayActorTestRig.SetMoney(actorB, 0);

        // Receiver has zero free bag slots (container sized to its content).
        actorB.Character.Inventory.Bag.ContainerSize = actorB.Character.Inventory.Bag.Items.Count;
        await Assert.That(actorB.Character.Inventory.Bag.FreeSlotCount).IsEqualTo(0);

        await Assert.That(actorA.TradeOffer(actorB.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actorA.TradePutup(ItemA, 2).State).IsEqualTo(ActorLifecycleState.Completed);

        _ = actorA.TradeLockOk(); // ok recorded, awaiting
        var lockB = actorB.TradeLockOk();
        await Assert.That(lockB.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(lockB.Failure).IsEqualTo(ActorFailureReason.RejectedAction);

        // The refusal canceled the session exactly once — no crash, no
        // corrupted registry — and NOTHING moved.
        await Assert.That(TradeManager.Instance.IsInTrade(actorA.ActorId)).IsFalse();
        await Assert.That(TradeManager.Instance.IsInTrade(actorB.ActorId)).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actorA, ItemA)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actorB, ItemA)).IsEqualTo(0);
        await Assert.That(actorA.Character.Money).IsEqualTo(100);
        await Assert.That(actorB.Character.Money).IsEqualTo(0);

        // Registry still healthy: a fresh trade between the same pair opens.
        var retry = actorA.TradeOffer(actorB.Character.ObjId);
        await Assert.That(retry.State, retry.Detail).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task TradeHandshake_WrongStateRefusals_FailClosedBeforeEngine()
    {
        GameplayActorTestRig.ForceSeedTradeManager();
        var (actorA, sessionA) = GameplayActorTestRig.CreateActor("trade-state-a");
        var (actorB, _) = GameplayActorTestRig.CreateActor("trade-state-b");
        GameplayActorTestRig.JoinActorWorld(sessionA, actorB);
        GameplayActorTestRig.SeedItemTemplate(ItemA);
        GameplayActorTestRig.GrantItem(actorA, ItemA, 1);

        // No session yet: putup and lock+ok are refused pre-flight.
        var earlyPutup = actorA.TradePutup(ItemA, 1);
        await Assert.That(earlyPutup.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(earlyPutup.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        var earlyOk = actorA.TradeLockOk();
        await Assert.That(earlyOk.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(earlyOk.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // Open a session, then re-offer while trading → refused.
        await Assert.That(actorA.TradeOffer(actorB.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        var duplicateOffer = actorA.TradeOffer(actorB.Character.ObjId);
        await Assert.That(duplicateOffer.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(duplicateOffer.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // First-mover lock+ok records this side's lock + ok and completes as
        // "awaiting counterpart" (canonical handshake: either side may
        // confirm first; the trade finishes only when BOTH sides are locked
        // AND ok'd — a one-sided confirm can never finish anything).
        var firstMoverOk = actorA.TradeLockOk();
        await Assert.That(firstMoverOk.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(TradeManager.Instance.IsInTrade(actorA.ActorId)).IsTrue();

        // Putup more than owned → refused, session and inventory untouched.
        var overPutup = actorA.TradePutup(ItemA, 99);
        await Assert.That(overPutup.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(overPutup.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(TradeManager.Instance.GetPutUpItems(actorA.ActorId)).IsEmpty();
        await Assert.That(GameplayActorTestRig.BagCount(actorA, ItemA)).IsEqualTo(1);
    }

    [Test]
    public async Task TradeHandshake_TargetOutOfRange_RefusedWithoutOpeningSession()
    {
        GameplayActorTestRig.ForceSeedTradeManager();
        var (actorA, sessionA) = GameplayActorTestRig.CreateActor("trade-range-a");
        var (actorB, _) = GameplayActorTestRig.CreateActor("trade-range-b");
        GameplayActorTestRig.JoinActorWorld(sessionA, actorB);
        actorA.Character.Transform.Local.SetPosition(new System.Numerics.Vector3(500, 0, 0));
        actorB.Character.Transform.Local.SetPosition(System.Numerics.Vector3.Zero);

        var offer = actorA.TradeOffer(actorB.Character.ObjId);
        await Assert.That(offer.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(offer.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(TradeManager.Instance.IsInTrade(actorA.ActorId)).IsFalse();
        await Assert.That(TradeManager.Instance.IsInTrade(actorB.ActorId)).IsFalse();
    }
}
