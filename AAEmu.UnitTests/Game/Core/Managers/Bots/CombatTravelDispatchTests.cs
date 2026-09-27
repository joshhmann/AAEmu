using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// CombatTravelDispatch: the combat KITE-SPACING seam — the sub-critical spacing
/// leg routed through <see cref="TravelBrain.Decide"/> →
/// <see cref="TravelIntentStore"/> → <see cref="TravelBrainPlanner"/> instead of
/// issuing its own ad-hoc retreat <c>Move</c>.
///
/// Two layers are pinned here:
///  - the PURE verdict table (<see cref="CombatTravelDispatch.DecideKiteLeg"/>)
///    driven from hand-built <see cref="CombatBrainDecision"/> /
///    <see cref="TravelBrainPlanner.Prepared"/> values — the sub-critical gate, the
///    fallback when the journey is disarmed, the terminal mapping, and the
///    never-a-bare-failure arm;
///  - the LIVE adapter (<see cref="CombatTravelDispatch.Prepare"/>) against the real
///    headless actor — the journey arming/identity rule, the retreat anchor the kite
///    walks, and the settled-intent stickiness.
///
/// The OWNERSHIP BOUNDARY (sub-critical kite here vs critical flee in Survival) is
/// pinned by construction: a combat decision that is not the kite arm falls back, so
/// the only two wakes that ever route through this seam are the ones the survival
/// layer does not already own.
/// </summary>
[NotInParallel]
public class CombatTravelDispatchTests
{
    private const uint BoarObjId = 3475;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AAEmu.Game.Models.AppConfiguration.Instance.World ??= new AAEmu.Game.Models.Game.WorldConfig();
        AAEmu.UnitTests.Game.Quests.Playerbot.PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
        ExecutionBoundary.ResetForTest();
    }

    // ------------------------------------------------------------ sub-critical gate

    /// <summary>
    /// The KITE arm (ranged/caster undercutting its band floor) is the one combat
    /// spacing wake this seam serves. Every other combat arm — including the
    /// DISENGAGE arm whose Disengaging fact Survival carries forward as its own flee —
    /// falls back to the caller's pre-brain rule, so this seam and the survival layer
    /// can never put two legs on one wake.
    /// </summary>
    [Test]
    public async Task OnlyTheKiteArm_Routes_TheDisengageArmStaysSurvivals()
    {
        var kite = KiteDecision();
        await Assert.That(CombatTravelDispatch.IsSubCriticalSpacing(kite)).IsTrue();

        // The CRITICAL disengage (all three causes share the arm): Survival owns it —
        // the published Disengaging fact makes the survival flee the leg, so this
        // seam must not serve it.
        foreach (var reason in (CombatDisengageReason[])[CombatDisengageReason.CriticalHp,
                     CombatDisengageReason.CriticalNoEscape, CombatDisengageReason.LeashEdge])
        {
            var disengage = kite with { Arm = CombatArm.Disengage, DisengageReason = reason };
            await Assert.That(CombatTravelDispatch.IsSubCriticalSpacing(disengage)).IsFalse();
            var verdict = CombatTravelDispatch.DecideKiteLeg(Prepared(), disengage);
            await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Fallback);
            await Assert.That(verdict.Decision).IsNull();
            // The caller's own pre-brain destination still travels (the brain's own
            // RetreatDestination — the same SafeAnchor shape), so the fallback leg is
            // byte-identical to the pre-brain behavior.
            await Assert.That(verdict.Destination).IsEqualTo(kite.Destination);
        }

        // No-verb arms never route either.
        foreach (var arm in (CombatArm[])[CombatArm.SurvivalVeto, CombatArm.CrowdControl, CombatArm.Hold])
        {
            var noVerb = kite with { Arm = arm, Verb = CombatVerb.Hold, Destination = null };
            await Assert.That(CombatTravelDispatch.IsSubCriticalSpacing(noVerb)).IsFalse();
            await Assert.That(CombatTravelDispatch.DecideKiteLeg(Prepared(), noVerb).Verb)
                .IsEqualTo(CombatTravelDispatch.KiteLegVerb.Fallback);
        }
    }

    /// <summary>
    /// THE OWNERSHIP BOUNDARY, measured against the survival layer's own rule: a
    /// sub-critical kite publishes <c>Engaged</c> (not <c>Disengaging</c>), so the
    /// survival brain reaches a non-vetoing verdict and the kite wake is provably
    /// Survival's NOT to take — which is exactly why this seam may serve it.
    /// </summary>
    [Test]
    public async Task SubCriticalKite_LeavesTheWakeToCombat_WhileACriticalDisengageHandsItToSurvival()
    {
        const uint actorObjId = 55001;

        // The kite wake publishes the engagement as ENGAGED (the planner's own rule),
        // so the survival fact reads "no retreat" — no veto, the kite is not Survival's.
        CombatBrainEngagement.Publish(
            actorObjId, BoarObjId, 100, Vector3.Zero, 50f,
            crowdControlled: false, disengaging: false, DateTime.UtcNow);

        var engaged = SurvivalBrain.Decide(new SurvivalBrainInputs(
            actorObjId, SelfHpRatio: 0.80f, CombatRetreatPublished: false,
            CommittedTargetObjId: BoarObjId, SelectedTargetObjId: BoarObjId,
            SelfPosition: Vector3.Zero, ThreatPosition: Vector3.Zero));
        await Assert.That(engaged.Veto).IsFalse();
        await Assert.That(engaged.IsFlee).IsFalse();

        // A CRITICAL disengage publishes the Disengaging fact, which the survival layer
        // carries forward as its own flee — the wake is Survival's, and the flee leg
        // walks the SHARED anchor (the kite's destination by construction).
        CombatBrainEngagement.PublishState(actorObjId, crowdControlled: false, disengaging: true, DateTime.UtcNow);
        var critical = SurvivalBrain.Decide(new SurvivalBrainInputs(
            actorObjId, SelfHpRatio: 0.80f, CombatRetreatPublished: true,
            CommittedTargetObjId: BoarObjId, SelectedTargetObjId: BoarObjId,
            SelfPosition: new Vector3(100f, 100f, 10f), ThreatPosition: new Vector3(70f, 100f, 10f)));
        await Assert.That(critical.IsFlee).IsTrue();
        await Assert.That(critical.Veto).IsTrue();
        await Assert.That(critical.Reason).IsEqualTo(SurvivalReason.CombatRetreat);

        // Both legs come from the ONE anchor implementation — never a second escape ray.
        var shared = TravelBrain.SafeAnchor(
            new Vector3(100f, 100f, 10f), new Vector3(70f, 100f, 10f), TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(critical.Destination!.Value).IsEqualTo(shared);
    }

    // ------------------------------------------------------------ verdict table

    /// <summary>The retreat arm's Move routes as this leg's own Move, carrying the travel decision and the anchor destination.</summary>
    [Test]
    public async Task RetreatArm_RoutesAsMove_CarryingTheTravelDecision()
    {
        var prepared = Prepared();
        var verdict = CombatTravelDispatch.DecideKiteLeg(prepared, KiteDecision());

        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Move);
        await Assert.That(verdict.HasLeg).IsTrue();
        await Assert.That(verdict.Routed).IsTrue();
        await Assert.That(verdict.DispatchToken).IsEqualTo("move");
        await Assert.That(verdict.Reason).IsEqualTo("retreat");
        await Assert.That(verdict.Terminal).IsNull();
        await Assert.That(verdict.Decision).IsEqualTo(prepared.Decision);
        await Assert.That(verdict.Destination).IsEqualTo(prepared.Decision.Destination);
    }

    /// <summary>An abandonment withdraws NAMING its terminal — never a bare failure, and never a leg to the abandoned destination.</summary>
    [Test]
    public async Task Abandonment_WithdrawsNamingTheTerminal()
    {
        foreach (var (terminal, token) in ((TravelTerminal, string)[])
                 [
                     (TravelTerminal.Unreachable, "unreachable"),
                     (TravelTerminal.TargetGone, "target-gone"),
                     (TravelTerminal.WrongWorld, "wrong-world")
                 ])
        {
            var abandoned = new TravelDecision(
                TravelArm.Repath, TravelVerb.Hold, TravelMode.Local, terminal, TravelReason.RepathExhausted,
                BoarObjId, null, float.NaN, 0, 2);
            var verdict = CombatTravelDispatch.DecideKiteLeg(Prepared(abandoned), KiteDecision());

            await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Withdraw);
            await Assert.That(verdict.IsTerminal).IsTrue();
            await Assert.That(verdict.Terminal).IsEqualTo(token);
            await Assert.That(verdict.DispatchToken).IsEqualTo("withdrawn");
            await Assert.That(verdict.HasLeg).IsFalse();
        }
    }

    /// <summary>A withhold that is NOT progress (an unreadable distance) withdraws with the named reason, not a claimed hold.</summary>
    [Test]
    public async Task NonProgressHold_WithdrawsNamingTheReason()
    {
        var withheld = new TravelDecision(
            TravelArm.Arrival, TravelVerb.Hold, TravelMode.Local, TravelTerminal.None,
            TravelReason.DistanceUnreadable, BoarObjId, null, float.NaN, 0, 0);
        var verdict = CombatTravelDispatch.DecideKiteLeg(Prepared(withheld), KiteDecision());

        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Withdraw);
        await Assert.That(verdict.Reason).IsEqualTo("distance-unreadable");
        await Assert.That(verdict.IsTerminal).IsFalse();
    }

    /// <summary>A live leg still serving the anchor HOLDs (its progress is not restarted every wake).</summary>
    [Test]
    public async Task LiveLegTrackingTheAnchor_HoldsInsteadOfRestarting()
    {
        var live = new TravelDecision(
            TravelArm.LegHold, TravelVerb.Hold, TravelMode.Local, TravelTerminal.None,
            TravelReason.LegLive, BoarObjId, null, float.NaN, 0, 0);
        var verdict = CombatTravelDispatch.DecideKiteLeg(Prepared(live), KiteDecision());

        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Held);
        await Assert.That(verdict.DispatchToken).IsEqualTo("held");
        await Assert.That(verdict.HasLeg).IsFalse();
    }

    /// <summary>A verb this seam cannot serve (a unit-relative leg) withdraws naming the fact rather than dispatching blind.</summary>
    [Test]
    public async Task UnservedVerb_WithdrawsNamingTheFact()
    {
        var follow = new TravelDecision(
            TravelArm.Follow, TravelVerb.MoveToUnit, TravelMode.Follow, TravelTerminal.None,
            TravelReason.FollowSelected, BoarObjId, Vector3.Zero, 5f, 0, 0);
        var verdict = CombatTravelDispatch.DecideKiteLeg(Prepared(follow), KiteDecision());

        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Withdraw);
        await Assert.That(verdict.Reason).IsEqualTo("unexpected-verb-MoveToUnit");
        await Assert.That(verdict.HasLeg).IsFalse();
    }

    /// <summary>
    /// The kite's anchor RECEDES every wake, and the chain's leg-hold arm sits behind
    /// the retreat arm — so this seam applies the hold itself: a live kite leg already
    /// pointing at this wake's anchor (within the shared drift tolerance) HOLDs, and a
    /// leg that drifted past it is re-issued.
    /// </summary>
    [Test]
    public async Task RecedingAnchor_IsHeldWhileTheLegStillServesIt_AndRetrackedWhenItDrifts()
    {
        var prepared = Prepared();
        var anchor = prepared.Decision.Destination!.Value;

        // A live kite leg already at this wake's anchor: keep its progress.
        var held = CombatTravelDispatch.DecideKiteLeg(
            Prepared(legLive: true, legDestination: anchor), KiteDecision());
        await Assert.That(held.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Held);
        await Assert.That(held.Reason).IsEqualTo("anchor-held");
        await Assert.That(held.HasLeg).IsFalse();

        // Within the shared tolerance counts as serving the same anchor.
        var within = CombatTravelDispatch.DecideKiteLeg(
            Prepared(legLive: true,
                legDestination: anchor + new Vector3(TravelBrain.LegDriftToleranceM * 0.5f, 0f, 0f)),
            KiteDecision());
        await Assert.That(within.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Held);

        // Drifted well past the tolerance: the leg no longer serves the anchor, so the
        // wake re-issues it.
        var drifted = CombatTravelDispatch.DecideKiteLeg(
            Prepared(legLive: true,
                legDestination: anchor + new Vector3(TravelBrain.LegDriftToleranceM * 4f, 0f, 0f)),
            KiteDecision());
        await Assert.That(drifted.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Move);
        await Assert.That(drifted.Destination).IsEqualTo(anchor);

        // An unreadable live leg destination is never assumed to be tracking.
        var unread = CombatTravelDispatch.DecideKiteLeg(
            Prepared(legLive: true, legDestination: null), KiteDecision());
        await Assert.That(unread.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Move);
    }

    /// <summary>
    /// The LIVE adapter wires the caller's leg reading through: a leg reported at the
    /// kite's own anchor HOLDs, and the same wake with no leg in flight re-issues it.
    /// </summary>
    [Test]
    public async Task Prepare_AppliesTheCallerLegReadingToTheRecedingAnchorHold()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("kite-leg-reading");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(BoarObjId);
        var threat = here + new Vector3(2f, 0f, 0f);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, threat);
        var anchor = TravelBrain.SafeAnchor(here, threat, TravelBrain.RetreatAnchorDistanceM);

        var held = CombatTravelDispatch.Prepare(actor, boarObjId, threat, anchor);
        await Assert.That(held).IsNotNull();
        var heldVerdict = CombatTravelDispatch.DecideKiteLeg(held, KiteDecision(arm: CombatArm.BackOff));
        await Assert.That(heldVerdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Held);

        var issued = CombatTravelDispatch.Prepare(actor, boarObjId, threat, liveLegDestination: null);
        var issuedVerdict = CombatTravelDispatch.DecideKiteLeg(issued, KiteDecision(arm: CombatArm.BackOff));
        await Assert.That(issuedVerdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Move);
        await Assert.That(issuedVerdict.Destination).IsEqualTo(anchor);
    }

    /// <summary>
    /// An UNRESOLVABLE threat is not a bare failure here: the retreat anchor is derived
    /// from the positions the caller passes and the chain's retreat arm precedes the
    /// moving-target probe, so the kite still walks its named anchor (never a withheld
    /// wake and never an unnamed failure). The three named terminals remain reachable on
    /// a journey that reached one, which the sticky re-read covers.
    /// </summary>
    [Test]
    public async Task Prepare_UnresolvableThreat_StillWalksTheNamedAnchor()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("kite-target-unresolved");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        const uint missingThreat = 999_999; // never spawned: the world cannot resolve it
        var threat = here + new Vector3(2f, 0f, 0f);

        var prepared = CombatTravelDispatch.Prepare(actor, missingThreat, threat, null);

        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Decision.Arm).IsEqualTo(TravelArm.Retreat);
        await Assert.That(prepared.Value.Decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(prepared.Value.Decision.Terminal).IsEqualTo(TravelTerminal.None);

        var verdict = CombatTravelDispatch.DecideKiteLeg(prepared, KiteDecision(arm: CombatArm.BackOff));
        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Move);
        await Assert.That(verdict.Reason).IsEqualTo("retreat");
        await Assert.That(verdict.Destination)
            .IsEqualTo(TravelBrain.SafeAnchor(here, threat, TravelBrain.RetreatAnchorDistanceM));
    }

    /// <summary>
    /// The caller's own leg check reads only THIS seam's kite leg: a live Move of any
    /// other owner (the return/pursuit/survival legs) is never mistaken for the kite's.
    /// </summary>
    [Test]
    public async Task IsOurLiveLeg_RecognisesOnlyTheKiteOwner()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("kite-owner-check");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));

        await Assert.That(CombatTravelDispatch.IsOurLiveLeg(actor.ActiveRequest)).IsFalse();

        actor.SetPendingMoveOwner(CombatTravelDispatch.KiteMoveOwner);
        var leg = actor.MoveTo(new Vector3(120f, 100f, 10f));
        await Assert.That(leg.MoveOwner).IsEqualTo(CombatTravelDispatch.KiteMoveOwner);
        await Assert.That(CombatTravelDispatch.IsOurLiveLeg(actor.ActiveRequest)).IsTrue();

        // A live Move under ANOTHER owner is a foreign leg, not ours.
        actor.PreemptCurrent("test foreign retrack");
        actor.SetPendingMoveOwner(TravelLegDispatch.ReturnMoveOwner);
        actor.MoveTo(new Vector3(130f, 100f, 10f));
        await Assert.That(CombatTravelDispatch.IsOurLiveLeg(actor.ActiveRequest)).IsFalse();
    }

    /// <summary>A disarmed journey falls back to the caller's pre-brain rule (the brain's own retreat destination).</summary>
    [Test]
    public async Task NoJourney_FallsBackToThePreBrainRule()
    {
        var verdict = CombatTravelDispatch.DecideKiteLeg(null, KiteDecision());

        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Fallback);
        await Assert.That(verdict.DispatchToken).IsEqualTo("fallback");
        await Assert.That(verdict.Routed).IsFalse();
        await Assert.That(verdict.Destination).IsEqualTo(KiteDecision().Destination);
    }

    /// <summary>The describe token is space- and bracket-free, and names the routing fact.</summary>
    [Test]
    public async Task Describe_IsBracketAndSpaceFree()
    {
        var routed = CombatTravelDispatch.Describe(
            CombatTravelDispatch.DecideKiteLeg(Prepared(), KiteDecision()));
        await Assert.That(routed.Contains(' ')).IsFalse();
        await Assert.That(routed.Contains('[')).IsFalse();
        await Assert.That(routed.Contains(']')).IsFalse();
        await Assert.That(routed.Contains(":routed=true")).IsTrue();

        var fallback = CombatTravelDispatch.Describe(
            CombatTravelDispatch.DecideKiteLeg(null, KiteDecision()));
        await Assert.That(fallback.Contains(":routed=false")).IsTrue();
    }

    // ------------------------------------------------------------ live adapter

    /// <summary>
    /// The KITE ROUTES THROUGH Decide: the live adapter arms the journey, runs the
    /// travel chain, and the retreat arm's Move walks the SHARED 25 m safe anchor
    /// opposite the threat — the same anchor the combat brain's own fallback and the
    /// survival flee leg use.
    /// </summary>
    [Test]
    public async Task Prepare_ArmsTheJourneyAndWalksTheSharedKiteAnchor()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("kite-routes");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(BoarObjId);
        var threat = here + new Vector3(2f, 0f, 0f); // too close: the kite wants room
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, threat);

        var prepared = CombatTravelDispatch.Prepare(actor, boarObjId, threat, liveLegDestination: null);

        await Assert.That(prepared).IsNotNull();
        var decision = prepared!.Value.Decision;
        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Retreat);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.Retreat);
        await Assert.That(decision.TargetObjId).IsEqualTo(boarObjId);

        // The anchor is the ONE shared implementation, 25 m opposite the threat.
        var shared = TravelBrain.SafeAnchor(here, threat, TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(decision.Destination!.Value).IsEqualTo(shared);
        await Assert.That(Vector3.Distance(here, shared)).IsCloseTo(25f, 0.001f);
        await Assert.That(shared.X).IsLessThan(here.X); // away from a threat to the east

        // The ROUTED anchor and the brain's OWN spacing destination are the same point
        // by construction: run the pure combat chain over matching inputs and compare.
        // This is the behavior-preservation proof — the seam swaps the leg's SOURCE,
        // never the escape ray (both come from the shared SafeAnchor).
        var brainDecision = CombatBrain.Decide(new CombatBrainInputs(
            ActorObjId: actor.ActorId, Role: CombatRole.RangedPhysical, Alive: true,
            SelfHpRatio: 0.80f, SelfLevel: 10, SelfPosition: here, EnemyCount: 1,
            NearestEnemyDistanceM: 2f, TookDamageThisFrame: false, CrowdControlled: false,
            CcAvailable: true, IsAutoAttackLive: false, HealItemTemplateId: 0,
            SkillMinRangeM: 0f, SkillMaxRangeM: 0f, SelectedSkillId: 0, LastSkillUsed: 0,
            IncumbentObjId: boarObjId, IncumbentValid: true, IncumbentUnknown: false,
            IncumbentScore: 0, CommitmentInForce: false, IncumbentPosition: threat,
            IncumbentDistanceM: 2f, IncumbentLeashDriftM: 0f, LeashBudgetM: 50f,
            Candidates: [], NowUtc: DateTime.UtcNow));
        await Assert.That(brainDecision.Arm).IsEqualTo(CombatArm.BackOff);
        await Assert.That(CombatTravelDispatch.IsSubCriticalSpacing(brainDecision)).IsTrue();
        await Assert.That(brainDecision.Destination!.Value).IsEqualTo(shared);

        // The journey is armed for this threat (the identity the leg keeps across wakes).
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armed, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(armed.Kind).IsEqualTo(TravelTargetKind.Unit);
        await Assert.That(armed.TargetObjId).IsEqualTo(boarObjId);

        // The verdict routes it as this leg's own Move.
        var verdict = CombatTravelDispatch.DecideKiteLeg(prepared, KiteDecision(
            destination: shared, arm: CombatArm.BackOff));
        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Move);
        await Assert.That(verdict.Destination).IsEqualTo(shared);
    }

    /// <summary>Re-arming the SAME threat keeps the journey (its counters survive); a different threat starts clean.</summary>
    [Test]
    public async Task EnsureJourney_KeepsTheSameThreat_AndRestartsOnANewOne()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("kite-journey");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));
        var first = session.SpawnNpc(BoarObjId);
        var here = new Vector3(100f, 100f, 10f);

        await Assert.That(CombatTravelDispatch.EnsureJourney(actor, first, here + new Vector3(2f, 0f, 0f))).IsTrue();
        await Assert.That(TravelIntentStore.NoteResolveMiss(actor.ActorId, CombatTravelDispatch.KiteMoveOwner)).IsEqualTo(1);

        // Same threat: the journey (and its counters) survive a refresh.
        await Assert.That(CombatTravelDispatch.EnsureJourney(actor, first, here + new Vector3(4f, 0f, 0f))).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var kept, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(kept.TargetObjId).IsEqualTo(first);
        await Assert.That(kept.ResolveAttempts).IsEqualTo(1);

        // The journey boundary drops it, and a fresh threat arms clean.
        await Assert.That(CombatTravelDispatch.EndJourney(actor)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsFalse();
        await Assert.That(CombatTravelDispatch.EnsureJourney(actor, first + 1, here)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var fresh, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(fresh.ResolveAttempts).IsEqualTo(0);

        // No threat identity: nothing can be armed, and the caller keeps its own rule.
        await Assert.That(CombatTravelDispatch.EnsureJourney(actor, 0, here)).IsFalse();
    }

    /// <summary>
    /// A SETTLED journey is re-read as its NAMED terminal instead of being decided
    /// again — the store's documented stickiness, and what makes "never a bare
    /// navigation failure" true for this consumer.
    /// </summary>
    [Test]
    public async Task Prepare_ReReadsASettledJourneyAsItsNamedTerminal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("kite-settled");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(BoarObjId);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(2f, 0f, 0f));

        CombatTravelDispatch.EnsureJourney(actor, boarObjId, here + new Vector3(2f, 0f, 0f));
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable, TravelReason.RepathExhausted,
            CombatTravelDispatch.KiteMoveOwner);

        var prepared = CombatTravelDispatch.Prepare(actor, boarObjId, here + new Vector3(2f, 0f, 0f), liveLegDestination: null);

        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(prepared.Value.Decision.Verb).IsEqualTo(TravelVerb.Hold);

        var verdict = CombatTravelDispatch.DecideKiteLeg(prepared, KiteDecision(arm: CombatArm.BackOff));
        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Withdraw);
        await Assert.That(verdict.Terminal).IsEqualTo("unreachable");
        await Assert.That(verdict.HasLeg).IsFalse();
    }

    /// <summary>An actor with no object identity cannot own a journey: Prepare returns null and the caller's rule stands.</summary>
    [Test]
    public async Task Prepare_WithoutActorIdentity_ReturnsNullForTheFallback()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("kite-no-identity");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(BoarObjId);
        actor.Character.ObjId = 0;

        var prepared = CombatTravelDispatch.Prepare(actor, boarObjId, here + new Vector3(2f, 0f, 0f), liveLegDestination: null);

        await Assert.That(prepared).IsNull();
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);
        var verdict = CombatTravelDispatch.DecideKiteLeg(prepared, KiteDecision());
        await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Fallback);
        await Assert.That(verdict.Reason).IsEqualTo("no-journey");
    }

    /// <summary>The leg it actually issued is the one banked (the mode/destination the next wake's drift read uses).</summary>
    [Test]
    public async Task PublishDispatched_BanksTheIssuedLeg()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("kite-published");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        CombatTravelDispatch.EnsureJourney(actor, BoarObjId, here);

        var anchor = here + new Vector3(25f, 0f, 0f);
        CombatTravelDispatch.PublishDispatched(actor, new TravelDecision(
            TravelArm.Retreat, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
            TravelReason.Retreat, BoarObjId, anchor, float.NaN, 0, 0));

        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var banked, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(banked.Mode).IsEqualTo(TravelMode.Local);
        await Assert.That(banked.PriorDestination).IsEqualTo(anchor);

        // A fallback verdict carries no decision and banks nothing new.
        CombatTravelDispatch.PublishDispatched(actor, null);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var untouched, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(untouched.PriorDestination).IsEqualTo(anchor);
    }

    // ------------------------------------------------------------ fixture

    private static CombatBrainDecision KiteDecision(
        Vector3? destination = null, CombatArm arm = CombatArm.BackOff)
        => new(
            arm, CombatVerb.Move, CombatBrainState.Engaged, BoarObjId, 0, 0,
            destination ?? new Vector3(75f, 100f, 10f), CombatBrain.MeleeMinM, CombatBrain.RangedPhysicalMaxM,
            2f, 0.80f, (int)CombatThreat.Engaged, CombatDisengageReason.None, CombatReason.BackOff);

    /// <summary>A hand-built Prepared whose decision is the retreat arm (the live adapter's own shape, minus the world).</summary>
    private static TravelBrainPlanner.Prepared Prepared(
        TravelDecision? decision = null, bool legLive = false, Vector3? legDestination = null)
    {
        var self = new Vector3(100f, 100f, 10f);
        var threat = self + new Vector3(2f, 0f, 0f);
        var anchor = TravelBrain.SafeAnchor(self, threat, TravelBrain.RetreatAnchorDistanceM);
        var inputs = new TravelBrainInputs(
            ActorObjId: 1, SelfPosition: self, TargetKind: TravelTargetKind.Unit, TargetObjId: BoarObjId,
            Destination: threat, DistanceM: 2f, ArrivalRadiusM: TravelBrain.DefaultArrivalRadiusM,
            LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM, FollowRequested: false,
            DestWorldKnown: false, ActorWorldId: 1, ActorInstanceId: 1, DestWorldId: 0, DestInstanceId: 0,
            TargetResolved: true, PriorResolveAttempts: 0, LegOutcome: TravelLegOutcome.None,
            LegLive: legLive, LegTargetObjId: BoarObjId, LegDestinationKnown: legDestination.HasValue,
            LegDestination: legDestination ?? Vector3.Zero, PriorRepathCount: 0, RepathBudget: TravelBrain.DefaultRepathBudget,
            RouteAvailable: false, RouteWaypoint: Vector3.Zero, RouteWaypointCount: 0,
            PriorMode: TravelMode.None, RetreatRequested: true, ThreatObjId: BoarObjId, ThreatPosition: threat);
        return new TravelBrainPlanner.Prepared(
            inputs,
            decision ?? new TravelDecision(
                TravelArm.Retreat, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
                TravelReason.Retreat, BoarObjId, anchor, 2f, 0, 0),
            []);
    }
}
