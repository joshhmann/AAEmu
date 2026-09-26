using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// SurvivalBrainPlanner: the LIVE ADAPTER against the real headless actor — the
/// vitals read from the wake's own snapshot, the combat layer's published facts,
/// the threat's live resolve for the flee destination, and the publish seam.
///
/// The pure chain is covered by <c>SurvivalBrainTests</c>; what is pinned here is
/// the adapter's own honesty contract: an absent snapshot or an unreadable maximum
/// reads NaN (never a fabricated veto), a published combat retreat reaches the
/// decision, and an unresolvable threat still yields a usable anchor.
/// </summary>
[NotInParallel]
public class SurvivalBrainPlannerTests
{
    private const uint Boar3475 = 3475;
    private const uint BoarObjId = 91_201;
    private static readonly DateTime Wake = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
        ExecutionBoundary.ResetForTest();
    }

    [Test]
    public async Task CriticalHpWithACommittedTarget_BuildsTheFleeLegTowardTheLiveThreat()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("surv-flee");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(Boar3475);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(10f, 0f, 0f));

        actor.Character.Hp = 15;
        actor.Character.MaxHp = 100;
        CombatBrainEngagement.Publish(actor.ActorId, boarObjId, 100, Vector3.Zero, 50f,
            crowdControlled: false, disengaging: false, Wake);

        var prepared = SurvivalBrainPlanner.Prepare(actor, new BotObservedContext
        {
            ActorId = actor.ActorId, Hp = 15, MaxHp = 100, CurrentTargetObjId = boarObjId
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(SurvivalReason.HpCritical);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(SurvivalVerb.Move);
        await Assert.That(prepared.HasLeg).IsTrue();
        await Assert.That(prepared.Vetoes).IsTrue();
        await Assert.That(prepared.Inputs.SelfHpRatio).IsCloseTo(0.15f, 0.0001f);

        // The destination is the shared anchor around the LIVE resolved threat, and
        // it walks AWAY from the boar.
        var expected = TravelBrain.SafeAnchor(here, here + new Vector3(10f, 0f, 0f), TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(prepared.Decision.Destination).IsEqualTo(expected);
        await Assert.That(prepared.Decision.Destination!.Value.X).IsLessThan(here.X);
    }

    [Test]
    public async Task PublishingThePreparedWake_RecordsTheFactForTheConsumer()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("surv-publish");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));
        actor.Character.Hp = 10;
        actor.Character.MaxHp = 100;

        var prepared = SurvivalBrainPlanner.Prepare(actor, new BotObservedContext
        {
            ActorId = actor.ActorId, Hp = 10, MaxHp = 100, CurrentTargetObjId = BoarObjId
        });
        var vetoed = SurvivalBrainPlanner.Publish(actor.ActorId, prepared);

        await Assert.That(vetoed).IsTrue();
        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsTrue();
        await Assert.That(SurvivalVetoState.TryGet(actor.ActorId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.HpCritical);
    }

    [Test]
    public async Task UnreadableVitalsFromTheActor_PublishANamedHoldAndNoVeto()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("surv-unreadable");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));
        actor.Character.Hp = 0;
        actor.Character.MaxHp = 0;

        var prepared = SurvivalBrainPlanner.Prepare(actor, new BotObservedContext
        {
            ActorId = actor.ActorId, Hp = 0, MaxHp = 0
        });

        await Assert.That(float.IsNaN(prepared.Inputs.SelfHpRatio)).IsTrue();
        await Assert.That(prepared.Decision.Reason).IsEqualTo(SurvivalReason.VitalsUnreadable);
        await Assert.That(prepared.Vetoes).IsFalse();

        // The consumer seam publishes the same named hold and vetoes nothing.
        await Assert.That(SurvivalBrainPlanner.EvaluateAndPublish(actor.ActorId, new BotObservedContext
        {
            ActorId = actor.ActorId, Hp = 0, MaxHp = 0
        })).IsFalse();
        await Assert.That(SurvivalVetoState.TryGet(actor.ActorId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.VitalsUnreadable);
    }

    [Test]
    public async Task NoObservationAtAll_ReadsUnreadableRatherThanHealthy()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("surv-no-snapshot");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));

        var prepared = SurvivalBrainPlanner.Prepare(actor, observation: null);

        await Assert.That(prepared.Decision.Reason).IsEqualTo(SurvivalReason.VitalsUnreadable);
        await Assert.That(prepared.Vetoes).IsFalse();
        await Assert.That(prepared.HasLeg).IsFalse();
    }

    [Test]
    public async Task PublishedCombatRetreat_ReachesTheDecisionThroughTheAdapter()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("surv-retreat");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));
        var boarObjId = session.SpawnNpc(Boar3475);
        CombatBrainEngagement.Publish(actor.ActorId, boarObjId, 100, Vector3.Zero, 50f,
            crowdControlled: false, disengaging: true, Wake);

        var prepared = SurvivalBrainPlanner.Prepare(actor, new BotObservedContext
        {
            ActorId = actor.ActorId, Hp = 100, MaxHp = 100
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(SurvivalReason.CombatRetreat);
        await Assert.That(prepared.Vetoes).IsTrue();
    }

    [Test]
    public async Task OutOfCombatLowHp_DoesNotVetoThroughTheAdapter()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("surv-recover");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));

        var prepared = SurvivalBrainPlanner.Prepare(actor, new BotObservedContext
        {
            ActorId = actor.ActorId, Hp = 10, MaxHp = 100
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(SurvivalVerdict.Recover);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(SurvivalReason.OutOfCombatCritical);
        await Assert.That(prepared.Vetoes).IsFalse();
        await Assert.That(prepared.HasLeg).IsFalse();
    }
}
