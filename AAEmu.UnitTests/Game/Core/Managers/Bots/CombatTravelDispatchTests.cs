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
    ///
    /// That 25 m is the TRAVEL chain's RETREAT arm's length. The combat brain's own
    /// SPACING destination keeps the same ray but not the same length: it is bounded
    /// by the perception census so the prey it backs off from stays visible on the
    /// next wake (see <see cref="CombatBrain.RetreatDistanceM"/>). The two are the
    /// same escape RAY from the same origin, not the same point — and the ray, not a
    /// metre count, is what this pins.
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

        // The ROUTED anchor and the brain's OWN spacing destination are the same
        // escape RAY by construction: run the pure combat chain over matching inputs
        // and compare. This is the behavior-preservation proof — the seam swaps the
        // leg's SOURCE, never the escape ray (both come from the shared SafeAnchor).
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
        var spacing = brainDecision.Destination!.Value;

        // SAME RAY, shorter step: the spacing point sits on the segment here→shared,
        // away from the threat. This half of the contract survives a legitimate
        // change to the spacing arm's LENGTH; the ray is what may not move.
        await Assert.That(Vector3.Distance(here, spacing) + Vector3.Distance(spacing, shared))
            .IsCloseTo(Vector3.Distance(here, shared), 0.001f)
            .Because("the spacing destination must be collinear with the routed anchor, away from the threat");

        // SAME RAY and the spacing arm's own LENGTH — derived from the same public
        // constants CombatBrain's own BackOffDistanceM is built from, never a
        // literal metre count, so a change to either boundary or the margin is
        // picked up here instead of needing a hand-updated X. `contactM` is the
        // `IncumbentDistanceM` the arm read above, which is what that bound
        // measures against: the NEARER of the census and this actor's own band
        // ceiling — the radius its combat brain is consulted from, so a spacing
        // step bounded by the census alone would push the prey past the point
        // where the brain can ever be consulted again — less the close-in margin,
        // less the contact already spent, capped at the retreat ceiling.
        // At 2 m contact with this archer's 22 m ceiling that is 19 m: the prey
        // lands inside the band, not merely inside the 25 m census.
        const float contactM = 2f;
        var (_, bandCeilingM) = CombatBrain.RoleBand(CombatRole.RangedPhysical);
        var spacingLengthM = Math.Min(
            CombatBrain.RetreatDistanceM,
            Math.Min(
                CombatBrain.AggroBandM - CombatBrain.CloseInMarginM - contactM,
                bandCeilingM - CombatBrain.CloseInMarginM - contactM));
        await Assert.That(spacing).IsEqualTo(TravelBrain.SafeAnchor(here, threat, spacingLengthM))
            .Because("the spacing leg walks the shared anchor at the band-bounded step, not the full retreat length");

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

    // ------------------------------------------------------- bounded-anchor fix

    /// <summary>
    /// THE BOUNDED ANCHOR IS RESTORED. The routed kite leg walked the travel chain's
    /// UNCAPPED <see cref="TravelBrain.RetreatAnchorDistanceM"/> step, which is the
    /// SURVIVAL separation contract — but a spacing wake is not a survival wake. From
    /// 2 m of contact the archer walked 25 m, leaving its prey at 27 m: past the 25 m
    /// perception census, so the next wake could not perceive its own target, the
    /// objective selector withdrew, and the archer spent six runs standing on its prey
    /// and killing nothing.
    ///
    /// This is the failing-before case: the combat brain ALREADY computes the bounded
    /// anchor for exactly this reason (<see cref="CombatBrain.BackOffDistanceM"/>), and
    /// the routed leg was simply discarding it. The expected value is derived from the
    /// same public constants that bound is built from, never a hand-written metre count.
    /// </summary>
    [Test]
    public async Task ARoutedKite_WalksTheCombatBrainsBoundedAnchor_NotTheUncappedRetreatStep()
    {
        const float contactM = 2f;
        var routed = await RouteKiteWake(CombatRole.RangedPhysical, contactM);

        var (_, bandCeilingM) = CombatBrain.RoleBand(CombatRole.RangedPhysical);
        var boundedStepM = Math.Min(
            CombatBrain.RetreatDistanceM,
            Math.Min(
                CombatBrain.AggroBandM - CombatBrain.CloseInMarginM - contactM,
                bandCeilingM - CombatBrain.CloseInMarginM - contactM));
        await Assert.That(Vector3.Distance(routed.Here, routed.Dispatched!.Value))
            .IsCloseTo(boundedStepM, 0.001f)
            .Because($"at {contactM} m contact the archer's spacing step is bounded to {boundedStepM} m " +
                      $"({CombatBrain.AggroBandM} m census and {bandCeilingM} m band, less the " +
                      $"{CombatBrain.CloseInMarginM} m margin, less the contact) — NOT the uncapped " +
                      $"{TravelBrain.RetreatAnchorDistanceM} m survival step");

        var uncapped = TravelBrain.SafeAnchor(
            routed.Here, routed.Threat, TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(routed.Dispatched!.Value).IsNotEqualTo(uncapped)
            .Because("the uncapped anchor is the value this seam used to dispatch, and it is what put the " +
                      "prey outside the census");

        // The prey must land INSIDE both boundaries — admitted, in band, still perceived.
        await Assert.That(Vector3.Distance(routed.Dispatched!.Value, routed.Threat))
            .IsLessThan(bandCeilingM)
            .Because("a step bounded by the census alone still lands outside the band ceiling the " +
                      "combat brain is consulted from, and the next wake can never re-decide it");
    }

    /// <summary>
    /// THE SURVIVAL INVARIANT, stated numerically: "full separation" is the engine's own
    /// <see cref="TravelBrain.RetreatAnchorDistanceM"/> = 25 m walked FROM THE ACTOR,
    /// leaving the prey at contact + 25 m. A survival flee must keep that number exactly
    /// — losing sight of what you are fleeing is the point — while the spacing leg above
    /// takes the shorter, bounded step.
    ///
    /// This is the test that fails if the bounded anchor ever leaks into survival. The
    /// survival path does NOT share the travel retreat arm with this seam (it consumes
    /// <c>SurvivalBrain.Decide</c> through <c>BotRoamStepExecutor</c>, and never reads
    /// <c>RetreatRequested</c>), so the two lengths are independent by construction —
    /// and this pins that independence with both numbers in one place.
    /// </summary>
    [Test]
    public async Task ASurvivalFlee_StillAchievesFullSeparation_AtExactlyTwentyFiveMetres()
    {
        const float contactM = 2f;
        var self = new Vector3(100f, 100f, 10f);
        var threat = self + new Vector3(contactM, 0f, 0f);

        // (a) The survival layer's own flee leg — the consumer that reaches the 25 m
        // separation contract, dispatched by the executor, not through this seam.
        var flee = SurvivalBrain.Decide(new SurvivalBrainInputs(
            ActorObjId: 1, SelfHpRatio: 0.10f, CombatRetreatPublished: true,
            CommittedTargetObjId: BoarObjId, SelectedTargetObjId: BoarObjId,
            SelfPosition: self, ThreatPosition: threat));
        await Assert.That(flee.IsFlee).IsTrue();
        await Assert.That(Vector3.Distance(self, flee.Destination!.Value))
            .IsCloseTo(TravelBrain.RetreatAnchorDistanceM, 0.001f)
            .Because($"full separation is {TravelBrain.RetreatAnchorDistanceM} m from the ACTOR, " +
                      "so the prey lands at contact + 25 m — losing sight of the threat is the point");
        await Assert.That(Vector3.Distance(flee.Destination!.Value, threat))
            .IsCloseTo(contactM + TravelBrain.RetreatAnchorDistanceM, 0.001f);

        // (b) The combat brain's own disengage flee leg keeps the same full length.
        var disengage = CombatBrain.Decide(new CombatBrainInputs(
            ActorObjId: 1, Role: CombatRole.RangedPhysical, Alive: true,
            SelfHpRatio: CombatBrain.FleeHpThreshold, SelfLevel: 10, SelfPosition: self, EnemyCount: 1,
            NearestEnemyDistanceM: contactM, TookDamageThisFrame: false, CrowdControlled: false,
            CcAvailable: true, IsAutoAttackLive: false, HealItemTemplateId: 0,
            SkillMinRangeM: 0f, SkillMaxRangeM: 0f, SelectedSkillId: 0, LastSkillUsed: 0,
            IncumbentObjId: BoarObjId, IncumbentValid: true, IncumbentUnknown: false,
            IncumbentScore: 0, CommitmentInForce: false, IncumbentPosition: threat,
            IncumbentDistanceM: contactM, IncumbentLeashDriftM: 0f, LeashBudgetM: 50f,
            Candidates: [], NowUtc: DateTime.UtcNow));
        await Assert.That(disengage.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(Vector3.Distance(self, disengage.Destination!.Value))
            .IsCloseTo(CombatBrain.RetreatDistanceM, 0.001f)
            .Because("the bounded spacing step is scoped to the SPACING arm; a survival break keeps " +
                      "the engine's full step and must never be shortened by this seam's fix");

        // (c) The two lengths are genuinely different here, so (a)/(b) are not vacuous:
        // the same actor, the same contact, the same threat — one wake flees, one spaces.
        var spacing = await RouteKiteWake(CombatRole.RangedPhysical, contactM);
        await Assert.That(Vector3.Distance(self, spacing.Dispatched!.Value))
            .IsLessThan(Vector3.Distance(self, flee.Destination!.Value))
            .Because("if these were equal the spacing leg would be the survival leg, and the " +
                      "pre-fix oscillation would not have been a spacing defect at all");
    }

    /// <summary>
    /// MELEE NEVER REACHES THIS SEAM, so the fix is a provable NO-OP for it. The spacing
    /// arm is gated <c>inputs.Role != CombatRole.Melee</c>, and a melee band's floor is
    /// <see cref="CombatDecisionTree.DefaultMeleeMin"/> = 1.0 m — the actor standing on
    /// its prey is already IN band, so no distance the engine can produce drops it below
    /// its own floor. The seam therefore never routes a melee wake, and this pins both
    /// halves: the gate rejects it, and the fallback leg it would issue is byte-identical
    /// to the pre-wiring behavior.
    /// </summary>
    [Test]
    public async Task AMeleeActor_IsNeverRoutedByThisSeam_AndItsLegIsUnchanged()
    {
        foreach (var contactM in new[] { 0.1f, CombatBrain.MeleeMinM * 0.5f, 0.99f })
        {
            var decision = CombatBrain.Decide(new CombatBrainInputs(
                ActorObjId: 1, Role: CombatRole.Melee, Alive: true,
                SelfHpRatio: 0.80f, SelfLevel: 10, SelfPosition: new Vector3(100f, 100f, 10f), EnemyCount: 1,
                NearestEnemyDistanceM: contactM, TookDamageThisFrame: false, CrowdControlled: false,
                CcAvailable: true, IsAutoAttackLive: false, HealItemTemplateId: 0,
                SkillMinRangeM: 0f, SkillMaxRangeM: 0f, SelectedSkillId: 0, LastSkillUsed: 0,
                IncumbentObjId: BoarObjId, IncumbentValid: true, IncumbentUnknown: false,
                IncumbentScore: 0, CommitmentInForce: false,
                IncumbentPosition: new Vector3(100f + contactM, 100f, 10f),
                IncumbentDistanceM: contactM, IncumbentLeashDriftM: 0f, LeashBudgetM: 50f,
                Candidates: [], NowUtc: DateTime.UtcNow));

            await Assert.That(CombatTravelDispatch.IsSubCriticalSpacing(decision)).IsFalse()
                .Because($"a melee bot at {contactM} m is inside its own {CombatBrain.MeleeMinM} m band " +
                          "floor, so the spacing arm — and with it this seam — cannot fire");

            // And the wake it WOULD issue keeps the caller's pre-brain rule exactly.
            var verdict = CombatTravelDispatch.DecideKiteLeg(Prepared(), decision);
            await Assert.That(verdict.Verb).IsEqualTo(CombatTravelDispatch.KiteLegVerb.Fallback);
            await Assert.That(verdict.DispatchToken).IsEqualTo("fallback");
            await Assert.That(verdict.Reason).IsEqualTo("not-sub-critical-spacing");
            await Assert.That(verdict.Decision).IsNull();
            await Assert.That(verdict.Destination).IsEqualTo(decision.Destination)
                .Because("a melee wake dispatches the brain's own destination under the original owner, " +
                          "byte-identically to the pre-wiring behavior — the fix cannot touch it");
        }
    }

    /// <summary>
    /// PER-ROLE ARITHMETIC: the dispatched anchor is the combat brain's own bounded step
    /// at every role the spacing arm can serve, at two contact distances. Read from the
    /// same public constants the bound is built from, so a change to any boundary or the
    /// margin moves this table rather than silently diverging from it.
    ///
    /// TWO roles legitimately never appear here, and both are pinned rather than skipped:
    /// MELEE is gated out by <c>Role != Melee</c> (its own 1.0 m floor puts it in band at
    /// any contact the engine can produce), and inside MELEE REACH the caster and healer
    /// are held by the reachability carve-out — they are handed the melee auto-attack
    /// there, which reaches, so spacing would walk them out of the only range where they
    /// have a verb. Only RANGED-PHYSICAL is genuinely stuck at contact (its skill-4 range
    /// is 4-20 m), which is exactly why the archer, and only the archer, was the
    /// zero-kill role. See <see cref="AMeleeActor_IsNeverRoutedByThisSeam_AndItsLegIsUnchanged"/>.
    /// </summary>
    [Test]
    public async Task TheDispatchedAnchor_IsTheRoleBoundedStep_AtTwoContactDistances()
    {
        var roles = new[]
        {
            CombatRole.Melee, CombatRole.RangedPhysical,
            CombatRole.RangedMagic, CombatRole.HealerSupport,
        };

        foreach (var role in roles)
        foreach (var contactM in new[] { 1.5f, 3.5f })
        {
            var (bandMinM, bandMaxM) = CombatBrain.RoleBand(role);
            var expectedStepM = Math.Min(
                CombatBrain.RetreatDistanceM,
                Math.Min(
                    CombatBrain.AggroBandM - CombatBrain.CloseInMarginM - contactM,
                    bandMaxM - CombatBrain.CloseInMarginM - contactM));
            var routed = await RouteKiteWake(role, contactM);

            if (contactM <= CombatBrain.MeleeMaxM && role != CombatRole.RangedPhysical)
            {
                // Inside melee reach the caster/healer/melee all hold the MELEE
                // auto-attack, which reaches — spacing there would deny them a verb
                // they have. Pinned so this table keeps saying so out loud.
                await Assert.That(routed.Routed).IsFalse()
                    .Because($"{role} at {contactM} m is within the {CombatBrain.MeleeMaxM} m melee " +
                              "reach it can still hit from, so the spacing arm must not fire");
                continue;
            }

            await Assert.That(routed.Routed).IsTrue()
                .Because($"{role} at {contactM} m has no verb inside its {bandMinM} m band floor — " +
                          "spacing is the only arm that gives it one back");
            await Assert.That(routed.Dispatched).IsNotNull();
            await Assert.That(Vector3.Distance(routed.Here, routed.Dispatched!.Value))
                .IsCloseTo(expectedStepM, 0.001f)
                .Because($"{role} at {contactM} m: min({CombatBrain.RetreatDistanceM} m ceiling, " +
                          $"{CombatBrain.AggroBandM} m census − {CombatBrain.CloseInMarginM} m − {contactM}, " +
                          $"{bandMaxM} m band − {CombatBrain.CloseInMarginM} m − {contactM})");
            await Assert.That(routed.PreyDistanceM)
                .IsCloseTo(contactM + expectedStepM, 0.001f)
                .Because("the step is measured FROM THE ACTOR, so the prey lands at contact + step");
        }
    }

    /// <summary>
    /// THE CENSUS BOUND, end to end: at every contact where the spacing arm actually
    /// fires, the prey the dispatched anchor leaves behind must stay strictly INSIDE
    /// both the 25 m perception census and this role's own band ceiling. This is the
    /// invariant the uncapped 25 m step broke — from contact it always put the prey
    /// past the horizon, which is what made the archer unkillable from 6 runs.
    /// </summary>
    [Test]
    public async Task TheDispatchedAnchor_NeverCrossesThePerceptionCensus()
    {
        foreach (var role in (CombatRole[])[CombatRole.RangedPhysical, CombatRole.RangedMagic, CombatRole.HealerSupport])
        {
            var (bandMinM, bandMaxM) = CombatBrain.RoleBand(role);
            foreach (var contactM in new[] { 0.1f, 1.5f, 3.5f, 6f, bandMaxM - CombatBrain.CloseInMarginM - 0.01f })
            {
                var routed = await RouteKiteWake(role, contactM);
                if (!routed.Routed)
                    continue; // the reachability carve-out owns this contact; no leg is issued.

                await Assert.That(routed.PreyDistanceM).IsLessThan(CombatBrain.AggroBandM)
                    .Because($"{role} at {contactM} m: the prey must remain inside the " +
                              $"{CombatBrain.AggroBandM} m census or the next wake cannot perceive it, " +
                              "selection withdraws, and the pursuit re-crowds the actor");
                await Assert.That(routed.PreyDistanceM).IsLessThan(bandMaxM)
                    .Because($"{role}'s band ceiling is {bandMaxM} m — the radius its combat brain is " +
                              "consulted from, so a leg past it can never be re-decided");
                await Assert.That(routed.PreyDistanceM).IsGreaterThan(bandMinM)
                    .Because("the bound must not be won by standing still: the step has to open real " +
                              "spacing past this role's floor, or the next wake re-fires the same arm");
            }
        }
    }

    /// <summary>
    /// Drives one routed kite wake END TO END against the live headless actor: the real
    /// combat brain decides the spacing anchor, the real travel chain decides the wake,
    /// and <see cref="CombatTravelDispatch.DecideKiteLeg"/> maps one onto the other. No
    /// hand-built destination — the anchor under test is the one the engine produces.
    /// </summary>
    private static async Task<KiteWake> RouteKiteWake(CombatRole role, float contactM)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor($"kite-{role}-{contactM}");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var threatObjId = session.SpawnNpc(BoarObjId);
        var threat = here + new Vector3(contactM, 0f, 0f);
        GameplayActorTestRig.SetNpcPosition(session, threatObjId, threat);

        var brain = CombatBrain.Decide(new CombatBrainInputs(
            ActorObjId: actor.ActorId, Role: role, Alive: true,
            SelfHpRatio: 0.80f, SelfLevel: 10, SelfPosition: here, EnemyCount: 1,
            NearestEnemyDistanceM: contactM, TookDamageThisFrame: false, CrowdControlled: false,
            CcAvailable: true, IsAutoAttackLive: false, HealItemTemplateId: 0,
            SkillMinRangeM: 0f, SkillMaxRangeM: 0f, SelectedSkillId: 0, LastSkillUsed: 0,
            IncumbentObjId: threatObjId, IncumbentValid: true, IncumbentUnknown: false,
            IncumbentScore: 0, CommitmentInForce: false, IncumbentPosition: threat,
            IncumbentDistanceM: contactM, IncumbentLeashDriftM: 0f, LeashBudgetM: 50f,
            Candidates: [], NowUtc: DateTime.UtcNow));

        // A non-kite wake never routes, and the caller never even prepares a journey
        // for it — mirroring QuestBehavior's own guard.
        if (!CombatTravelDispatch.IsSubCriticalSpacing(brain))
            return new KiteWake(here, threat, null, false);

        var prepared = CombatTravelDispatch.Prepare(actor, threatObjId, threat, liveLegDestination: null);
        var verdict = CombatTravelDispatch.DecideKiteLeg(prepared, brain);
        return new KiteWake(
            here, threat, verdict.Verb == CombatTravelDispatch.KiteLegVerb.Move
                ? verdict.Destination
                : null,
            verdict.Routed);
    }

    /// <summary>One wake's measured outcome: where the leg would walk, and the prey distance it leaves.</summary>
    private readonly record struct KiteWake(
        Vector3 Here, Vector3 Threat, Vector3? Dispatched, bool Routed)
    {
        public float PreyDistanceM => Dispatched is { } anchor
            ? Vector3.Distance(anchor, Threat)
            : float.NaN;
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
