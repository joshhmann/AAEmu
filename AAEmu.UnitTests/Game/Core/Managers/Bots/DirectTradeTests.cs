using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Direct player trade parity (capability-matrix gap): two ordinary
/// Character actors drive the real TradeManager path — offer → put-up →
// lock+ok on both sides → item swap executes inside ConfirmTrade.
/// </summary>
[NotInParallel]
public class DirectTradeTests
{
    private const uint TradeItemTemplateId = 91_204;
    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        GameplayActorTestRig.ForceSeedTeamManager();
        GameplayActorTestRig.ForceSeedTradeManager();
    }

    [Test]
    public async Task TradeOffer_Putup_LockOk_BothSides_SwapsItem()
    {
        var (alice, aliceSession) = GameplayActorTestRig.CreateActor("trade-alice");
        var (bob, _) = GameplayActorTestRig.CreateActor("trade-bob");
        GameplayActorTestRig.SeedItemTemplate(TradeItemTemplateId);
        GameplayActorTestRig.JoinActorWorld(aliceSession, bob);
        GameplayActorTestRig.GrantItem(alice, TradeItemTemplateId, 2);

        var offer = alice.TradeOffer(bob.Character.ObjId, "trade-offer-1");
        await Assert.That(offer.State).IsEqualTo(ActorLifecycleState.Completed);

        var putup = alice.TradePutup(TradeItemTemplateId, 1, "trade-putup-1");
        await Assert.That(putup.State).IsEqualTo(ActorLifecycleState.Completed);

        var aliceLock = alice.TradeLockOk("trade-lock-alice");
        await Assert.That(aliceLock.State).IsEqualTo(ActorLifecycleState.Completed);
        var bobLock = bob.TradeLockOk("trade-lock-bob");
        await Assert.That(bobLock.State).IsEqualTo(ActorLifecycleState.Completed);

        await Assert.That(GameplayActorTestRig.BagCount(alice, TradeItemTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(bob, TradeItemTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task TradeOffer_SelfTarget_RejectedWithoutSession()
    {
        var (alice, _) = GameplayActorTestRig.CreateActor("trade-self");
        GameplayActorTestRig.SeedItemTemplate(TradeItemTemplateId);

        var offer = alice.TradeOffer(alice.Character.ObjId);

        await Assert.That(offer.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(offer.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task TradePutup_OutsideSession_RefusedStateTransition()
    {
        var (alice, _) = GameplayActorTestRig.CreateActor("trade-nosession");
        GameplayActorTestRig.SeedItemTemplate(TradeItemTemplateId);
        GameplayActorTestRig.GrantItem(alice, TradeItemTemplateId, 1);

        var putup = alice.TradePutup(TradeItemTemplateId, 1);

        await Assert.That(putup.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(putup.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(GameplayActorTestRig.BagCount(alice, TradeItemTemplateId)).IsEqualTo(1);
    }
}
