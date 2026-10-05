using System.Numerics;

using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The SHARED settled-intent re-read (<c>TravelBrainPlanner.SettledTerminal</c>),
/// pinned once for all three callers — the travel return/pursuit legs
/// (<c>TravelLegDispatch</c>), the combat kite leg (<c>CombatTravelDispatch</c>)
/// and the loot approach leg (<c>LootTravelDispatch</c>). A journey's terminal is
/// sticky, so every caller re-reads the banked verdict on the next wake; these
/// rows pin that one vocabulary, so the three legs can never disagree about the
/// same journey. Pure data in, a decision out — no engine, no world, no rig.
/// </summary>
public class TravelSettledTerminalTests
{
    private const uint CorpseObjId = 4_302_117u;
    private static readonly Vector3 Destination = new(120f, 40f, 12f);

    [Test]
    public async Task SettledTerminal_NamesTheArmThatReachedEachTerminal()
    {
        await Assert.That(ArmOf(TravelTerminal.WrongWorld)).IsEqualTo(TravelArm.PreFlight);
        await Assert.That(ArmOf(TravelTerminal.TargetGone)).IsEqualTo(TravelArm.TargetProbe);
        await Assert.That(ArmOf(TravelTerminal.Arrived)).IsEqualTo(TravelArm.Arrival);
        await Assert.That(ArmOf(TravelTerminal.Unreachable)).IsEqualTo(TravelArm.Repath);
    }

    /// <summary>
    /// The re-read asks the actor for NOTHING and claims no measurement: a
    /// settled journey is over, so it carries a Hold verb, no destination, a NaN
    /// distance (unreadable stays unreadable — never a fabricated 0) and zero
    /// waypoints, while the banked mode, target and repath count ride through.
    /// </summary>
    [Test]
    public async Task SettledTerminal_WithholdsTheVerbAndKeepsMeasurementsUnread()
    {
        var decision = TravelBrainPlanner.SettledTerminal(
            Settled(TravelTerminal.Arrived, TravelReason.Arrived, repathCount: 2));

        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.Destination).IsNull();
        await Assert.That(float.IsNaN(decision.DistanceM)).IsTrue();
        await Assert.That(decision.WaypointCount).IsEqualTo(0);
        await Assert.That(decision.Mode).IsEqualTo(TravelMode.Follow);
        await Assert.That(decision.TargetObjId).IsEqualTo(CorpseObjId);
        await Assert.That(decision.RepathCount).IsEqualTo(2);
    }

    /// <summary>The named terminal and reason are re-read verbatim, never re-derived.</summary>
    [Test]
    public async Task SettledTerminal_CarriesTheBankedTerminalAndReasonVerbatim()
    {
        var decision = TravelBrainPlanner.SettledTerminal(
            Settled(TravelTerminal.Unreachable, TravelReason.RepathExhausted));

        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.RepathExhausted);
        await Assert.That(decision.IsTerminal).IsTrue();
        await Assert.That(decision.IsAbandon).IsTrue();
    }

    /// <summary>An arrival is NOT an abandonment — the one success terminal reads as itself.</summary>
    [Test]
    public async Task SettledTerminal_ArrivedIsNotAnAbandon()
    {
        var decision = TravelBrainPlanner.SettledTerminal(
            Settled(TravelTerminal.Arrived, TravelReason.Arrived));

        await Assert.That(decision.IsAbandon).IsFalse();
    }

    // ------------------------------------------------------------- fixture

    private static TravelArm ArmOf(TravelTerminal terminal)
        => TravelBrainPlanner.SettledTerminal(Settled(terminal, TravelReason.None)).Arm;

    /// <summary>A settled intent row as <c>TravelIntentStore</c> banks one.</summary>
    private static TravelIntentStore.Intent Settled(TravelTerminal terminal, TravelReason reason, int repathCount = 0)
        => new(
            Kind: TravelTargetKind.Unit,
            TargetObjId: CorpseObjId,
            Destination: Destination,
            FollowRequested: true,
            ArrivalRadiusM: 2.5f,
            LocalModeMaxM: 30f,
            RepathBudget: 3,
            ResolveAttempts: 1,
            RepathCount: repathCount,
            Mode: TravelMode.Follow,
            PriorDestination: Destination,
            Terminal: terminal,
            Reason: reason,
            Route: []);
}
