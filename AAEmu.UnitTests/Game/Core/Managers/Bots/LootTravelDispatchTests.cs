using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The loot brain's corpse-approach caller seam
/// (<see cref="LootTravelDispatch"/>): the ONE place the walk-to-the-pinned-corpse
/// leg and the wired travel layer meet.
///
/// Two layers of proof:
///  - the VERDICT TABLE, over hand-built decisions — no world, no actor: only the
///    approach's own verbs route (MoveToUnit / the audited Stop), every other verb
///    and every withholding arm falls back or withdraws NAMING its cause, and a
///    fallback never carries a leg the brain did not ask for;
///  - the LIVE CHAIN on the REAL headless actor: the journey arms per (actor,
///    corpse) with the corpse's live position, the leg routes through
///    <see cref="TravelBrain.Decide"/> (the mode-select arm on the resolved unit),
///    a RECYCLED corpse names <see cref="TravelTerminal.TargetGone"/> instead of
///    walking the actor to whatever now holds that objId, a DESPAWNED corpse spends
///    the brain's one re-resolve and then names the same terminal, and the loot-once
///    memory the take arm owns is untouched by every one of those wakes.
/// </summary>
[NotInParallel]
public class LootTravelDispatchTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Threat3476 = 3476;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint ProbeItemTemplateId = 91_411;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
        LootLedger.ClearAll();
        TravelIntentStore.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        TravelIntentStore.ClearAll();
        LootLedger.ClearAll();
        ExecutionBoundary.ResetForTest();
    }

    // ------------------------------------------------------------- live chain

    /// <summary>
    /// THE ROUTING: a pinned dead corpse arms a journey on the CORPSE's own objId
    /// and position, and the wake's leg comes from <see cref="TravelBrain.Decide"/>
    /// (the mode-select arm over the planner's live unit resolve) — the existing
    /// MoveToUnit verb, no position leg, no new verb.
    /// </summary>
    [Test]
    public async Task PinnedCorpse_RoutesTheLegThroughDecideOnTheCorpseIdentity()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-route");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));

        var prepared = LootTravelDispatch.Prepare(actor, Request(corpseObjId));

        await Assert.That(prepared).IsNotNull();
        var decision = prepared!.Value.Decision;
        // The brain's OWN arms: the live unit resolve selected the follow leg
        // (a unit target), which is the MoveToUnit verb on the corpse objId.
        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Follow);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.FollowSelected);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveToUnit);
        await Assert.That(decision.TargetObjId).IsEqualTo(corpseObjId);
        await Assert.That(decision.Destination).IsNotNull();
        await Assert.That(decision.Destination!.Value.X).IsCloseTo(here.X + 20f, 0.01f);

        // The journey is armed ON THE CORPSE, as a point journey (not a station
        // follow), so arrival is the journey's own terminal.
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var intent, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(intent.Kind).IsEqualTo(TravelTargetKind.Unit);
        await Assert.That(intent.TargetObjId).IsEqualTo(corpseObjId);
        await Assert.That(intent.FollowRequested).IsFalse();

        var verdict = LootTravelDispatch.DecideApproach(prepared, false, false, "fresh", "fresh");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.MoveToUnit);
        await Assert.That(verdict.Routed).IsTrue();
        await Assert.That(verdict.HasLeg).IsTrue();
        await Assert.That(verdict.DispatchToken).IsEqualTo("move");
        await Assert.That(verdict.Terminal).IsNull();
    }

    /// <summary>
    /// The case the loot probe withholds on today — a corpse outside loot range —
    /// is exactly what the approach leg is for: the SAME routing closes on it
    /// through the existing leg verbs, with the distance measured by the planner.
    /// </summary>
    [Test]
    public async Task PinnedCorpseBeyondLootRange_StillRoutesAClosingLeg()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-far");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(300f, 0f, 0f));

        var prepared = LootTravelDispatch.Prepare(actor, Request(corpseObjId));

        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Inputs.DistanceM).IsCloseTo(300f, 0.01f);
        await Assert.That(prepared.Value.Decision.Verb).IsEqualTo(TravelVerb.MoveToUnit);
        var verdict = LootTravelDispatch.DecideApproach(prepared, false, true, "retrack", "0.5m");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.MoveToUnit);
        await Assert.That(verdict.Reason).IsEqualTo("retrack");
    }

    /// <summary>
    /// ARRIVAL: inside the arrival radius the journey reaches its destination and
    /// the leg's own audited Stop is the verdict — the take is the loot arm's, and
    /// this seam never names a Loot verb.
    /// </summary>
    [Test]
    public async Task CorpseInsideArrivalRadius_NamesTheAuditedStopAndTheArrivedTerminal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-arrive");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(2f, 0f, 0f));

        var prepared = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.Arrived);

        var verdict = LootTravelDispatch.DecideApproach(prepared, true, false, "fresh", "fresh");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Stop);
        await Assert.That(verdict.DispatchToken).IsEqualTo("stop");
        await Assert.That(verdict.Reason).IsEqualTo("arrived");
        await Assert.That(verdict.Terminal).IsEqualTo("arrived");
        await Assert.That(verdict.IsTerminal).IsTrue();
        await Assert.That(verdict.HasLeg).IsTrue();
        // The journey's terminal is banked, so every later wake of THIS journey
        // re-reads it instead of re-deriving anything.
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.Arrived);
    }

    /// <summary>
    /// RECYCLED CORPSE: the recorded objId resolves LIVE again (the engine's own
    /// ObjId-recycling rule, read through the SAME <see cref="LootCorpseProbe"/> the
    /// loot chain reads) — the travel layer's own named
    /// <see cref="TravelTerminal.TargetGone"/> is what the leg withdraws with, and
    /// no leg is ever dispatched toward the row that now holds that objId.
    /// </summary>
    [Test]
    public async Task RecycledCorpse_NamesTargetGoneAndNeverWalksToTheNewOccupant()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-recycled");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));

        // The objId is alive again: it is not our corpse any more.
        session.World.GetNpc(corpseObjId)!.Hp = 100;

        var prepared = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        await Assert.That(prepared.Value.Decision.Verb).IsEqualTo(TravelVerb.Hold);

        var verdict = LootTravelDispatch.DecideApproach(prepared, false, false, "fresh", "fresh");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Terminal).IsEqualTo("target-gone");
        await Assert.That(verdict.Reason).IsEqualTo("terminal-target-gone");

        // Sticky: the banked terminal is re-read on the next wake rather than the
        // recycle fact being re-derived (or the journey silently resuming).
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.TargetGone);

        var later = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        var laterVerdict = LootTravelDispatch.DecideApproach(later, false, false, "fresh", "fresh");
        await Assert.That(laterVerdict.Terminal).IsEqualTo("target-gone");
        await Assert.That(laterVerdict.HasLeg).IsFalse();
    }

    /// <summary>
    /// A resolved row of another template is the same recycle fact: the pin named
    /// the prey template, so a corpse of anything else is not the corpse the leg was
    /// armed for — TargetGone, never a walk to a stranger's corpse.
    /// </summary>
    [Test]
    public async Task CorpseOfAnotherTemplate_IsRecycledAndNamesTargetGone()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-other-template");
        var here = new Vector3(100f, 100f, 10f);
        var objId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));
        session.World.GetNpc(objId)!.TemplateId = 9_999;

        var prepared = LootTravelDispatch.Prepare(actor, Request(objId));
        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        await Assert.That(prepared.Value.Decision.Reason).IsEqualTo(TravelReason.TargetGone);
    }

    /// <summary>
    /// DESPAWNED CORPSE: the resolve spends the brain's ONE re-resolve (a NAMED
    /// hold, never a bare failure) and only the second consecutive miss is the
    /// terminal — so a corpse that merely left this wake's world read is not
    /// abandoned on a single reading.
    /// </summary>
    [Test]
    public async Task DespawnedCorpse_HoldsOnceThenNamesTargetGone()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-despawn");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));

        // Wake 1 arms the journey while the corpse is still there.
        await Assert.That(LootTravelDispatch.Prepare(actor, Request(corpseObjId))).IsNotNull();

        // The corpse leaves the world (recycled out, despawned, region teardown).
        session.World.RemoveObject(session.World.GetNpc(corpseObjId)!);

        var first = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Value.Decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(first.Value.Decision.Reason).IsEqualTo(TravelReason.TargetUnresolved);
        await Assert.That(first.Value.Decision.Terminal).IsEqualTo(TravelTerminal.None);

        var firstVerdict = LootTravelDispatch.DecideApproach(first, false, false, "fresh", "fresh");
        await Assert.That(firstVerdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
        await Assert.That(firstVerdict.Reason).IsEqualTo("target-unresolved");
        await Assert.That(firstVerdict.Terminal).IsNull();
        await Assert.That(firstVerdict.HasLeg).IsFalse();

        // The second consecutive miss is the terminal, and it is banked.
        var second = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        var secondVerdict = LootTravelDispatch.DecideApproach(second, false, false, "fresh", "fresh");
        await Assert.That(secondVerdict.Terminal).IsEqualTo("target-gone");
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.TargetGone);
    }

    /// <summary>
    /// NO PIN, NO JOURNEY (the fail-closed direction): with nothing pinned there is
    /// nothing to arm, so <see cref="LootTravelDispatch.Prepare"/> returns null and
    /// the caller's own pre-brain rule owns the wake — the brain never fabricates a
    /// journey (or a decision) for a corpse that was never named.
    /// </summary>
    [Test]
    public async Task NoPinnedCorpse_ArmsNothingAndFallsBackToThePreBrainRule()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("loot-approach-nopin");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));

        var prepared = LootTravelDispatch.Prepare(actor, Request(0));

        await Assert.That(prepared).IsNull();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsFalse();

        // The fallback branch is the caller's exact pre-brain rule.
        var inGate = LootTravelDispatch.DecideApproach(null, true, false, "fresh", "fresh");
        await Assert.That(inGate.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Stop);
        await Assert.That(inGate.Reason).IsEqualTo("in-range");
        await Assert.That(inGate.Routed).IsFalse();
        await Assert.That(inGate.Decision).IsNull();

        var holding = LootTravelDispatch.DecideApproach(null, false, true, "retrack", "0.5m");
        await Assert.That(holding.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Held);
        await Assert.That(holding.Reason).IsEqualTo("drift-held(drift=0.5m)");
        await Assert.That(holding.HasLeg).IsFalse();

        var closing = LootTravelDispatch.DecideApproach(null, false, false, "1.2m", "1.2m");
        await Assert.That(closing.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.MoveToUnit);
        await Assert.That(closing.Reason).IsEqualTo("1.2m");
        await Assert.That(closing.HasLeg).IsTrue();
    }

    /// <summary>
    /// A journey ended by the caller arms CLEAN next time: the boundary drops the
    /// banked verdict with the journey, so a re-pinned corpse can never inherit an
    /// abandoned one's terminal or budget.
    /// </summary>
    [Test]
    public async Task EndJourney_DropsTheBankedTerminalWithTheJourney()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-end");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));
        session.World.GetNpc(corpseObjId)!.Hp = 100;

        _ = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.TargetGone);

        await Assert.That(LootTravelDispatch.EndJourney(actor)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsFalse();

        // A fresh pin resolves dead and routes a leg again — no inherited terminal.
        session.World.GetNpc(corpseObjId)!.Hp = 0;
        var prepared = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(prepared).IsNotNull();
        await Assert.That(prepared!.Value.Decision.Verb).IsEqualTo(TravelVerb.MoveToUnit);
        await Assert.That(prepared.Value.Decision.Terminal).IsEqualTo(TravelTerminal.None);
    }

    /// <summary>
    /// The leg's own owner tag is this leg's alone, and the outcome mapping is the
    /// ONE mapping (<see cref="TravelLegDispatch.MapLegOutcome"/>) — so a live leg
    /// this seam issued reads Running and nothing else's leg is ever read as ours.
    /// </summary>
    [Test]
    public async Task LegOutcomeMapping_ReadsOnlyThisLegsOwnOwnerTag()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-owner");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));
        await Assert.That(LootTravelDispatch.MapLegOutcome(actor, corpseObjId))
            .IsEqualTo(TravelLegOutcome.None);

        if (actor is GameplayActor concrete)
            concrete.SetPendingMoveOwner(LootTravelDispatch.ApproachMoveOwner);
        var leg = actor.MoveToUnit(corpseObjId, 4.5f);
        _ = leg;

        await Assert.That(LootTravelDispatch.IsOurLiveLeg(actor.ActiveRequest, corpseObjId)).IsTrue();
        await Assert.That(LootTravelDispatch.MapLegOutcome(actor, corpseObjId))
            .IsEqualTo(TravelLegOutcome.Running);
        // The same live leg is NOT this seam's when the caller asks about another
        // corpse: the objId gate is part of the identity, not only the owner tag.
        await Assert.That(LootTravelDispatch.IsOurLiveLeg(actor.ActiveRequest, corpseObjId + 1)).IsFalse();
        await Assert.That(TravelLegDispatch.IsOurLiveLeg(
            actor.ActiveRequest, corpseObjId, TravelLegDispatch.ReturnMoveOwner)).IsFalse();
    }

    /// <summary>
    /// A SETTLED journey's arrival may not outlive the corpse's identity: once the
    /// recorded objId is alive again, the leg stops re-reading its banked arrival at
    /// the row that now holds that objId — the recycle fact overrides the sticky
    /// terminal, so the caller is never told to halt on a stranger.
    /// </summary>
    [Test]
    public async Task ArrivedJourney_RecycledCorpse_OverridesTheStickyArrivalWithTargetGone()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-arrived-recycle");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(2f, 0f, 0f));

        var arrived = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(arrived).IsNotNull();
        await Assert.That(arrived!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.Arrived);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.Arrived);

        // The objId is alive again — the arrival is no longer about our corpse.
        session.World.GetNpc(corpseObjId)!.Hp = 100;

        var later = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(later).IsNotNull();
        await Assert.That(later!.Value.Decision.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        var verdict = LootTravelDispatch.DecideApproach(later, true, false, "fresh", "fresh");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
        await Assert.That(verdict.Terminal).IsEqualTo("target-gone");
        await Assert.That(verdict.HasLeg).IsFalse();
    }

    /// <summary>
    /// The journey is per (actor, CORPSE): a re-pin to another corpse starts a CLEAN
    /// journey (new budget, no inherited terminal), while a re-pin to the SAME corpse
    /// keeps the counters — so a newly pinned corpse can never inherit the budget
    /// another corpse spent.
    /// </summary>
    [Test]
    public async Task Journey_IsPerCorpse_ARepinToAnotherCorpseStartsCleanAndTheBudgetIsNotInherited()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-repin");
        var here = new Vector3(100f, 100f, 10f);

        // Corpse A spends a repath (a failed leg), then is abandoned: the journey
        // carries a spent budget and a terminal.
        var corpseA = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));
        var a = LootTravelDispatch.Prepare(actor, Request(corpseA, TravelLegOutcome.TimedOut));
        await Assert.That(a).IsNotNull();
        await Assert.That(a!.Value.Decision.Reason).IsEqualTo(TravelReason.Repathed);
        // The caller banks the leg it actually issued, which is what spends the attempt.
        LootTravelDispatch.PublishDispatched(actor, a.Value.Decision);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armedA, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(armedA.RepathCount).IsEqualTo(1);

        // The SAME corpse re-armed (a caller refresh) keeps the spent attempt.
        GameplayActorTestRig.SetNpcPosition(session, corpseA, here + new Vector3(21f, 0f, 0f));
        var again = LootTravelDispatch.Prepare(actor, Request(corpseA, TravelLegOutcome.TimedOut));
        await Assert.That(again!.Value.Decision.RepathCount).IsEqualTo(2);
        LootTravelDispatch.PublishDispatched(actor, again.Value.Decision);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var refreshed, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(refreshed.RepathCount).IsEqualTo(2);

        // Another corpse: a NEW journey — the counters start at zero. (The rig's
        // SpawnNpc resolves by TEMPLATE, so the first corpse must leave the world or
        // the second "spawn" would hand back the same objId.)
        session.World.RemoveObject(session.World.GetNpc(corpseA)!);
        var corpseB = SpawnDeadBoar(session, actor, here + new Vector3(0f, 30f, 0f), here + new Vector3(60f, 30f, 0f));
        await Assert.That(corpseB).IsNotEqualTo(corpseA);
        var b = LootTravelDispatch.Prepare(actor, Request(corpseB));
        await Assert.That(b).IsNotNull();
        await Assert.That(b!.Value.Decision.TargetObjId).IsEqualTo(corpseB);
        await Assert.That(b.Value.Decision.RepathCount).IsEqualTo(0);
        await Assert.That(b.Value.Decision.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armedB, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(armedB.TargetObjId).IsEqualTo(corpseB);
        await Assert.That(armedB.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(armedB.RepathCount).IsEqualTo(0);
    }

    /// <summary>
    /// TWO JOURNEYS, ONE ACTOR: the kite's spacing journey and the corpse approach
    /// are each keyed by their OWNING LEG, so arming both on the same actor leaves
    /// two coexisting rows. This is the regression the owner-less keying could not
    /// survive: without the leg owner both legs collapsed onto one row, and each
    /// wake (or either leg's own <c>EndJourney</c>) wiped the other's counters,
    /// route and banked terminal.
    ///
    /// Pinned here: both rows exist, the corpse wake advances ONLY its own row, and
    /// each leg's boundary drops only its own journey.
    /// </summary>
    [Test]
    public async Task KiteAndCorpseJourneys_CoexistOnOneActor_AndNeitherEndDisarmsTheOther()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-combat-two-journeys");
        var here = new Vector3(100f, 100f, 10f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(20f, 0f, 0f));
        var threatObjId = SpawnThreat(session, actor, here, here + new Vector3(2f, 0f, 0f));
        await Assert.That(threatObjId).IsNotEqualTo(corpseObjId);

        // The combat kite arms its own journey on the SAME actor the corpse approach
        // will arm on...
        await Assert.That(CombatTravelDispatch.EnsureJourney(actor, threatObjId, here + new Vector3(2f, 0f, 0f))).IsTrue();

        // ... and the corpse approach arms its own row, on that same actor.
        var approach = LootTravelDispatch.Prepare(actor, Request(corpseObjId, TravelLegOutcome.TimedOut));
        await Assert.That(approach).IsNotNull();

        // BOTH rows coexist: one per owning leg, each holding its own target. Keyed
        // owner-lessly this could only ever be one row (the second arm wiping the
        // first's counters, route and terminal), which is the regression.
        await Assert.That(TravelIntentStore.Count).IsEqualTo(2);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var kite, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(kite.TargetObjId).IsEqualTo(threatObjId);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var corpse, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(corpse.TargetObjId).IsEqualTo(corpseObjId);
        await Assert.That(kite.LegOwner).IsEqualTo(CombatTravelDispatch.KiteMoveOwner);
        await Assert.That(corpse.LegOwner).IsEqualTo(LootTravelDispatch.ApproachMoveOwner);

        // The kite now carries its own distinguishable progress, issued leg and
        // verdict — each write through the SEAM, so it lands on the kite's row.
        await Assert.That(TravelIntentStore.NoteResolveMiss(actor.ActorId, CombatTravelDispatch.KiteMoveOwner)).IsEqualTo(1);
        var kiteAnchor = TravelBrain.SafeAnchor(here, here + new Vector3(2f, 0f, 0f), TravelBrain.RetreatAnchorDistanceM);
        CombatTravelDispatch.PublishDispatched(actor, new TravelDecision(
            TravelArm.Retreat, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
            TravelReason.Retreat, threatObjId, kiteAnchor, float.NaN, 0, 0));
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable, TravelReason.RepathExhausted,
            CombatTravelDispatch.KiteMoveOwner);

        // ADVANCING ONE LEAVES THE OTHER INTACT: the corpse leg banks the repath it
        // actually issued (its own counters), and the kite's row — counters, issued
        // mode/destination, banked terminal — survives untouched.
        LootTravelDispatch.PublishDispatched(actor, approach!.Value.Decision);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var corpseAfter, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(corpseAfter.RepathCount).IsEqualTo(approach.Value.Decision.RepathCount);
        await Assert.That(corpseAfter.RepathCount).IsGreaterThan(0);

        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var kiteAfter, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(kiteAfter.ResolveAttempts).IsEqualTo(1);
        await Assert.That(kiteAfter.Mode).IsEqualTo(TravelMode.Local);
        await Assert.That(kiteAfter.PriorDestination).IsEqualTo(kiteAnchor);
        await Assert.That(kiteAfter.Terminal).IsEqualTo(TravelTerminal.Unreachable);

        // The kite leg's own boundary drops only the kite's journey...
        await Assert.That(CombatTravelDispatch.EndJourney(actor)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var corpseStill, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(corpseStill.TargetObjId).IsEqualTo(corpseObjId);
        await Assert.That(corpseStill.RepathCount).IsEqualTo(corpseAfter.RepathCount);

        // ... re-arming the kite leaves the corpse row alone ...
        await Assert.That(CombatTravelDispatch.EnsureJourney(actor, threatObjId, here + new Vector3(2f, 0f, 0f))).IsTrue();
        await Assert.That(TravelIntentStore.Count).IsEqualTo(2);

        // ... and the corpse leg's own boundary drops only ITS journey, leaving the
        // kite's armed.
        await Assert.That(LootTravelDispatch.EndJourney(actor)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var kiteAlone, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(kiteAlone.TargetObjId).IsEqualTo(threatObjId);
        await Assert.That(TravelIntentStore.Count).IsEqualTo(1);
    }

    /// <summary>
    /// LOOT-ONCE PRESERVED: the approach leg rides ADDITIVELY beside the take. After
    /// the real quest leg takes a corpse, the memory the take arm owns is intact
    /// (the dispatch flag and the ledger's Take row), the approach seam still routes
    /// its leg for that corpse without writing either, and the next real wake still
    /// withholds the take with the frozen <c>already-looted</c> token.
    /// </summary>
    [Test]
    public async Task LootOnceMemory_IsUntouchedByTheApproachLegAndStillWithholds()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-once");
        var corpseObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(corpseObjId);

        // Wake 1: the pursuit range-hold quest-pins the corpse.
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-once-1" });

        var npc = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;

        // Wake 2: the take lands, banking both the leg's own loot-once flag and the
        // decision layer's ledger row.
        var take = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-once-2" });
        await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, corpseObjId)).IsTrue();
        await Assert.That(LootLedger.Read(actor.ActorId, corpseObjId).Disposition)
            .IsEqualTo(LootDisposition.Take);

        // The approach leg for the SAME corpse still routes (the loot decision is a
        // different layer's), and it left both memories exactly as the take banked
        // them — the approach slice can neither un-loot nor re-loot a corpse. The
        // actor has walked away from the corpse, so the leg closes rather than
        // halting on it.
        GameplayActorTestRig.SetPosition(actor, new Vector3(40, 0, 0));
        var prepared = LootTravelDispatch.Prepare(actor, Request(corpseObjId));
        await Assert.That(prepared).IsNotNull();
        var verdict = LootTravelDispatch.DecideApproach(prepared, false, false, "fresh", "fresh");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.MoveToUnit);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, corpseObjId)).IsTrue();
        await Assert.That(LootLedger.Read(actor.ActorId, corpseObjId).Disposition)
            .IsEqualTo(LootDisposition.Take);

        // Wake 3 through the real stack: the corpse's container is REFILLED, so the
        // wake cannot withhold merely because the corpse emptied — the loot-once
        // memory is what withholds, with the frozen vocabulary every lane scanner
        // keys on.
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        var after = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-once-3" });
        await Assert.That(after.SelectedAction == ActorActionType.Loot).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot)).IsEqualTo(1);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        var lootArm = after.LegEvidence.FirstOrDefault(e => e.Leg == QuestLegId.Loot).Detail ?? "";
        await Assert.That(lootArm).Contains("validate=already-looted");
        await Assert.That(lootArm).Contains("arm=LootOnce");
    }

    // ------------------------------------------------- live caller (G7c leg)

    /// <summary>
    /// THE WIRING, end to end on the real stack: a pinned corpse OUT of the engine's
    /// loot range is exactly the withhold the take cannot fix, so the loot leg
    /// dispatches the corpse-approach <c>Move</c> — on the SAME <c>MoveToUnit</c> verb
    /// the sibling legs use, owner-tagged <c>LOOT_APPROACH_MOVE_TO_UNIT</c>, with the
    /// frozen <c>validate=</c> token and the leg's own <c>dispatch=</c>/<c>reason=</c>
    /// tokens plus the brain's additive <c>:travel=</c> fragment.
    ///
    /// It is also the PRIORITY proof: nothing else on this wake is legal (no prey in
    /// the funnel census, the quest not Ready), so the approach at
    /// <c>ObjectiveLootPriority</c> outranks the step machine's advance — the walk
    /// happens BEFORE more quest work, which is the point of the leg.
    /// </summary>
    [Test]
    public async Task PinnedCorpseOutOfLootRange_DispatchesTheApproachMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-wire");
        var here = new Vector3(0f, 0f, 0f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(250f, 0f, 0f));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        QuestBehavior.NotePinnedCorpse(actor.ActorId, corpseObjId, Boar3475, Quest251, "wire-1");

        var run = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-wire-1" });

        await Assert.That(run.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(run.Request!.TargetId).IsEqualTo(corpseObjId);
        await Assert.That(run.Request.MoveOwner).IsEqualTo(LootTravelDispatch.ApproachMoveOwner);
        await Assert.That(run.Request.State).IsEqualTo(ActorLifecycleState.Running);

        // The leg's own lane line: the frozen validate token for an unreachable corpse,
        // the approach's own dispatch/reason tokens, and the brain's routing.
        var lootArm = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
        await Assert.That(lootArm).Contains("validate=not-lootable");
        await Assert.That(lootArm).Contains(":dispatch=move");
        await Assert.That(lootArm).Contains(":travel=");
        await Assert.That(lootArm).Contains("routed=true");

        // The journey is armed ON the corpse, owned by this leg, and banked unspent.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(armed.TargetObjId).IsEqualTo(corpseObjId);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(armed.RepathCount).IsEqualTo(0);
    }

    /// <summary>
    /// THE TASK'S OWN VERIFY (walk → take, loot-once preserved): the approach closes
    /// the distance and the SAME corpse is then TAKEN by the loot arm — the take is
    /// still the only thing that touches the container, the ledger and the loot-once
    /// flag, and the approach journey is ended with the take rather than left banked.
    /// </summary>
    [Test]
    public async Task ApproachLands_ThenTheSameCorpseIsTakenAndTheJourneyEnds()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-take");
        var here = new Vector3(0f, 0f, 0f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(250f, 0f, 0f));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        QuestBehavior.NotePinnedCorpse(actor.ActorId, corpseObjId, Boar3475, Quest251, "take-1");
        var bagBefore = GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId);

        var walk = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-take-1" });
        await Assert.That(walk.SelectedAction).IsEqualTo(ActorActionType.Move);

        // The walk carries the actor into the loot gate (the leg's own arrival point);
        // the leg then settles, which is the idle actor the take's wake needs.
        GameplayActorTestRig.SetPosition(actor, new Vector3(240f, 0f, 0f));
        await Assert.That(actor.PreemptCurrent("unit-test approach leg settled")).IsTrue();
        var take = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-take-2" });

        await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(take.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId) - bagBefore).IsEqualTo(1);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);

        // The take ended the approach journey (a later re-pin of this corpse arms clean
        // instead of inheriting a banked terminal), and the loot-once memory is the
        // take's own: the NEXT wake still withholds with the frozen token.
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, corpseObjId)).IsTrue();
        await Assert.That(LootLedger.Read(actor.ActorId, corpseObjId).Disposition)
            .IsEqualTo(LootDisposition.Take);
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsFalse();

        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        var after = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-take-3" });
        await Assert.That(after.SelectedAction == ActorActionType.Loot).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot)).IsEqualTo(1);
        var lootArm = after.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
        await Assert.That(lootArm).Contains("validate=already-looted");
        // The loot-once withhold is NOT a reachability withhold: no approach leg.
        await Assert.That(lootArm.Contains(":dispatch=move")).IsFalse();
    }

    /// <summary>
    /// RECYCLE, LIVE (the whole stack, not just the seam): once the recorded objId
    /// resolves LIVE again it is not our corpse, and the actor is never walked to
    /// whatever now holds that objId — the recognition arm drops the record, so the
    /// loot chain sees no corpse at all and the leg issues no second approach. The
    /// seam's own named <see cref="TravelTerminal.TargetGone"/> override for a corpse
    /// that resolves onto an occupant is pinned separately, directly over
    /// <see cref="LootTravelDispatch"/>.
    /// </summary>
    [Test]
    public async Task PinnedCorpseRecycled_Live_IssuesNoLegOntoTheNewOccupant()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-recycle-live");
        var here = new Vector3(0f, 0f, 0f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(250f, 0f, 0f));
        Activate251(actor.Character);
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(corpseObjId)!, (ProbeItemTemplateId, 1));
        QuestBehavior.NotePinnedCorpse(actor.ActorId, corpseObjId, Boar3475, Quest251, "recycle-1");

        // Wake 1: dead and out of reach — the approach is armed and dispatched.
        var walk = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-recycle-1" });
        await Assert.That(walk.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        // The walk leg settles (a running leg lives in ActiveRequest, not the trace).
        await Assert.That(actor.PreemptCurrent("unit-test approach leg settled")).IsTrue();
        await Assert.That(actor.AuditTrace.Count(r => r.MoveOwner == LootTravelDispatch.ApproachMoveOwner)).IsEqualTo(1);

        // The objId is alive again: it is not our corpse any more. No second leg is
        // issued onto the row that now holds that objId, the frozen withhold stands,
        // and the approach journey ended with the record it belonged to.
        session.World.GetNpc(corpseObjId)!.Hp = 100;
        var run = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-recycle-2" });

        await Assert.That(run.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.MoveOwner == LootTravelDispatch.ApproachMoveOwner)).IsEqualTo(1);
        var lootArm = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
        await Assert.That(lootArm.Contains(":dispatch=move")).IsFalse();
        await Assert.That(QuestBehavior.TryGetCorpse(actor.ActorId, out _)).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsFalse();
    }

    /// <summary>
    /// THE RETRACK IS NOT A FAILURE: when the pinned corpse's position is not the point
    /// the live approach leg was issued against, the leg preempts that leg ITSELF. If
    /// that self-preemption were read as a foreign interruption, every re-issue would
    /// spend a repath and a healthy approach would abandon after two — so this pins
    /// that the budget survives it, exactly as the pursuit leg's own retrack does.
    /// </summary>
    [Test]
    public async Task ApproachRetrack_DoesNotSpendTheRepathBudget()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-retrack");
        var here = new Vector3(0f, 0f, 0f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(250f, 0f, 0f));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        QuestBehavior.NotePinnedCorpse(actor.ActorId, corpseObjId, Boar3475, Quest251, "retrack-1");

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-retrack-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Move);

        // The corpse's recorded point moves (a haul, a rig, a GM) but stays out of
        // range, so this wake re-issues rather than holding.
        GameplayActorTestRig.SetNpcPosition(session, corpseObjId, here + new Vector3(260f, 0f, 0f));

        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-retrack-2" });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Move);
        var lootArm = second.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
        await Assert.That(lootArm).Contains(":reason=retrack");
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, LootTravelDispatch.ApproachMoveOwner)).IsTrue();
        await Assert.That(armed.RepathCount).IsEqualTo(0);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);

        // The retired leg was retired by the CALLER itself — the shared constant, so the
        // staging site and the recognizer can never drift apart.
        var retired = actor.AuditTrace.First(r => r.Action == ActorActionType.Move
            && r.MoveOwner == LootTravelDispatch.ApproachMoveOwner);
        await Assert.That(retired.Result).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(retired.Detail).Contains(TravelLegDispatch.LootApproachRetrackDetail);
    }

    /// <summary>
    /// NO PIN, NO LEG (the fail-closed direction on the real stack): a corpse the chain
    /// did not establish as OURS, and a corpse the ledger banked a terminal skip for,
    /// both keep the frozen withhold — <c>verb=none</c> and no journey — because
    /// neither is reachability. The brain never fabricates a leg for a corpse it was
    /// not armed for.
    /// </summary>
    [Test]
    public async Task WithholdThatIsNotReachability_DispatchesNoLeg()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("loot-approach-noleg");
        var here = new Vector3(0f, 0f, 0f);
        var corpseObjId = SpawnDeadBoar(session, actor, here, here + new Vector3(250f, 0f, 0f));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));

        // No pin at all: the chain reads no corpse, so the frozen withhold stands.
        var noPin = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-noleg-1" });
        await Assert.That(noPin.SelectedAction == ActorActionType.Move).IsFalse();
        var noPinArm = noPin.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
        await Assert.That(noPinArm).Contains("verb=none");
        await Assert.That(noPinArm.Contains(":dispatch=move")).IsFalse();
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);

        // A corpse the ledger banked a TERMINAL skip for, at the same out-of-range
        // distance. The chain's order puts the PROBE arm first, so the frozen token is
        // the out-of-range one — but the walk is still refused, because a corpse this
        // chain will never take is not worth approaching.
        QuestBehavior.NotePinnedCorpse(actor.ActorId, corpseObjId, Boar3475, Quest251, "noleg-2");
        LootLedger.Bank(actor.ActorId, corpseObjId, LootDisposition.Skipped, LootReason.Junk, DateTime.UtcNow);
        var skipped = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "loot-approach-noleg-2" });
        await Assert.That(skipped.SelectedAction == ActorActionType.Move).IsFalse();
        var skipArm = skipped.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
        await Assert.That(skipArm).Contains("validate=not-lootable");
        await Assert.That(skipArm).Contains("ownership=Self");
        await Assert.That(skipArm.Contains(":dispatch=move")).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, LootTravelDispatch.ApproachMoveOwner)).IsFalse();
    }

    // -------------------------------------------------------- verdict table
    /// <summary>
    /// The table over hand-built brain wakes — no world, no actor. A position leg
    /// and the retreat leg are verbs this approach does not serve, so they withdraw
    /// NAMING the fact rather than dispatching something the caller cannot serve; a
    /// live leg keeps its progress; an unmeasurable distance withdraws naming the
    /// read that failed.
    /// </summary>
    [Test]
    public async Task HandBuiltWakes_WithdrawOnEveryVerbThisLegCannotServe()
    {
        var positionLeg = PreparedFor(new TravelDecision(
            TravelArm.Local, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
            TravelReason.LocalSelected, 0, new Vector3(10f, 0f, 0f), 12f, 0, 0));
        var verdict = LootTravelDispatch.DecideApproach(positionLeg, false, false, "fresh", "fresh");
        await Assert.That(verdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
        await Assert.That(verdict.Reason).IsEqualTo("unexpected-verb-MoveTo");
        await Assert.That(verdict.HasLeg).IsFalse();

        var retreat = PreparedFor(new TravelDecision(
            TravelArm.Retreat, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
            TravelReason.Retreat, 7, new Vector3(10f, 0f, 0f), float.NaN, 0, 0));
        var retreatVerdict = LootTravelDispatch.DecideApproach(retreat, false, false, "fresh", "fresh");
        await Assert.That(retreatVerdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
        await Assert.That(retreatVerdict.Reason).IsEqualTo("unexpected-verb-MoveTo");

        var liveLeg = PreparedFor(new TravelDecision(
            TravelArm.LegHold, TravelVerb.Hold, TravelMode.Follow, TravelTerminal.None,
            TravelReason.LegLive, 42, null, 20f, 0, 0));
        var liveVerdict = LootTravelDispatch.DecideApproach(liveLeg, false, true, "retrack", "0.4m");
        await Assert.That(liveVerdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Held);
        await Assert.That(liveVerdict.Reason).IsEqualTo("drift-held(drift=0.4m)");
        await Assert.That(liveVerdict.HasLeg).IsFalse();
        await Assert.That(liveVerdict.Routed).IsTrue();

        var unmeasurable = PreparedFor(new TravelDecision(
            TravelArm.Arrival, TravelVerb.Hold, TravelMode.Follow, TravelTerminal.None,
            TravelReason.DistanceUnreadable, 42, null, float.NaN, 0, 0));
        var unmeasurableVerdict = LootTravelDispatch.DecideApproach(unmeasurable, false, false, "fresh", "fresh");
        await Assert.That(unmeasurableVerdict.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
        await Assert.That(unmeasurableVerdict.Reason).IsEqualTo("distance-unreadable");
        await Assert.That(unmeasurableVerdict.Terminal).IsNull();
    }

    /// <summary>
    /// The vocabulary this table prints is the SAME the quest return leg's table
    /// prints — the two seams are one vocabulary by construction, so a lane parser
    /// reading <c>dispatch=</c>/<c>reason=</c> never has to special-case the loot
    /// slice.
    /// </summary>
    [Test]
    public async Task LaneVocabulary_MatchesTheReturnLegsTableTokens()
    {
        var reasonRows = new[]
        {
            (TravelReason.TargetUnresolved, "target-unresolved"),
            (TravelReason.TargetGone, "target-gone"),
            (TravelReason.DistanceUnreadable, "distance-unreadable"),
            (TravelReason.NoIntent, "no-journey"),
            (TravelReason.WrongWorld, "wrong-world"),
            (TravelReason.RepathExhausted, "unreachable"),
            (TravelReason.LegLive, "withheld")
        };

        foreach (var (reason, token) in reasonRows)
        {
            // A Hold on the reason (LegLive is the progress row, so it is compared
            // through its own token below).
            var prepared = PreparedFor(new TravelDecision(
                TravelArm.TargetProbe, TravelVerb.Hold, TravelMode.Follow, TravelTerminal.None,
                reason, 42, null, 20f, 0, 0));
            var approach = LootTravelDispatch.DecideApproach(prepared, false, false, "fresh", "fresh");
            var ret = TravelLegDispatch.DecideReturnLeg(prepared, false, false, false, "fresh", "fresh");
            await Assert.That(approach.Reason).IsEqualTo(ret.Reason);
            await Assert.That(approach.DispatchToken).IsEqualTo(ret.DispatchToken);
            await Assert.That(approach.Terminal).IsEqualTo(ret.Terminal);
            if (reason == TravelReason.LegLive)
            {
                await Assert.That(approach.Reason).IsEqualTo("drift-held(drift=fresh)");
            }
            else
            {
                await Assert.That(approach.Reason).IsEqualTo(token);
                await Assert.That(approach.Verb).IsEqualTo(LootTravelDispatch.ApproachVerb.Withdraw);
            }

            // The fallback branch's tokens are the same shape too.
            var approachFallback = LootTravelDispatch.DecideApproach(null, false, false, "1.5m", "1.5m");
            var retFallback = TravelLegDispatch.DecideReturnLeg(null, false, false, false, "1.5m", "1.5m");
            await Assert.That(approachFallback.DispatchToken).IsEqualTo(retFallback.DispatchToken);
            await Assert.That(approachFallback.Reason).IsEqualTo(retFallback.Reason);
        }
    }

    /// <summary>
    /// The observability a lane line carries: the brain's own space-free decision
    /// description plus whether this leg routed and which dispatch it named — built
    /// on read, nothing allocated per decision.
    /// </summary>
    [Test]
    public async Task WakeDescription_NamesTheDecisionRoutingAndDispatch()
    {
        var routed = LootTravelDispatch.Describe(LootTravelDispatch.DecideApproach(
            PreparedFor(new TravelDecision(
                TravelArm.Follow, TravelVerb.MoveToUnit, TravelMode.Follow, TravelTerminal.None,
                TravelReason.FollowSelected, 42, new Vector3(10f, 0f, 0f), 20f, 0, 0)),
            false, false, "fresh", "fresh"));
        await Assert.That(routed).Contains("arm=Follow:verb=MoveToUnit:mode=Follow:terminal=none");
        await Assert.That(routed).Contains("reason=FollowSelected");
        await Assert.That(routed).Contains("routed=true:dispatch=move");

        var absent = LootTravelDispatch.Describe(
            LootTravelDispatch.DecideApproach(null, true, false, "fresh", "fresh"));
        await Assert.That(absent).Contains("verdict=none:reason=in-range:routed=false:dispatch=stop");

        var terminal = LootTravelDispatch.Describe(LootTravelDispatch.DecideApproach(
            PreparedFor(new TravelDecision(
                TravelArm.TargetProbe, TravelVerb.Hold, TravelMode.Follow, TravelTerminal.TargetGone,
                TravelReason.TargetGone, 42, null, 20f, 0, 0)),
            false, false, "fresh", "fresh"));
        await Assert.That(terminal).Contains("routed=true:dispatch=withdrawn:terminal=target-gone");
    }

    // ------------------------------------------------------------- fixture

    private static LootTravelDispatch.Request Request(
        uint corpseObjId, TravelLegOutcome legOutcome = TravelLegOutcome.None, bool legLive = false)
        => new(
            CorpseObjId: corpseObjId,
            PreyTemplateId: Boar3475,
            ArrivalRadiusM: TravelBrain.DefaultArrivalRadiusM,
            LegOutcome: legOutcome,
            LegLive: legLive,
            RepathBudget: TravelBrain.DefaultRepathBudget);

    /// <summary>A hand-built brain wake: default inputs (which read as "intent armed") with the given decision.</summary>
    private static TravelBrainPlanner.Prepared PreparedFor(in TravelDecision decision)
        => new(
            Inputs: default(TravelBrainInputs) with
            {
                TargetKind = TravelTargetKind.Unit,
                TargetObjId = decision.TargetObjId,
                DistanceM = decision.DistanceM,
                PriorMode = decision.Mode,
                PriorRepathCount = decision.RepathCount
            },
            Decision: decision,
            Route: []);

    private static uint SpawnDeadBoar(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 corpsePos)
    {
        var objId = SpawnBoar(session, actor, actorPos, corpsePos);
        session.World.GetNpc(objId)!.Hp = 0;
        return objId;
    }

    /// <summary>
    /// A live unit of ANOTHER template, for the two-journey coexistence test: the
    /// rig's <c>SpawnNpc</c> dedupes by template id, so the combat kite's threat must
    /// be a different row than the boar the corpse approach pins.
    /// </summary>
    private static uint SpawnThreat(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 threatPos)
        => SpawnNpcAt(session, actor, actorPos, threatPos, Threat3476);

    private static uint SpawnBoar(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos)
        => SpawnNpcAt(session, actor, actorPos, npcPos, Boar3475);

    private static uint SpawnNpcAt(
        HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos, uint templateId)
    {
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = session.SpawnNpc(templateId);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        var npcRegion = session.World.GetRegionByPos(npcPos);
        npcRegion?.AddObject(npc);
        npc.Region = npcRegion;
        return npcObjId;
    }

    private static void Activate251(Character character)
    {
        character.Quests.ActiveQuests[Quest251] = new Quest(character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
    }

    /// <summary>
    /// Additive, missing-only static surface for the 251 funnel: skill-2
    /// template (AutoAttack dispatch), quest-251 Progress gather act for item
    /// 4058 (objective resolution), and the 3475→4530→4058 loot link (source
    /// resolution). Canonical pilot data is never clobbered.
    /// </summary>
    private static void SeedCombatSurface()
    {
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    private static void SeedQuest251GatherAct()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(Quest251, out var template))
        {
            template = new QuestTemplate { Id = Quest251, Level = 2 };
            questTemplates[Quest251] = template;
        }
        if (template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == Meat4058))
            return;

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        const uint progressComponentId = 2510901;
        if (!componentTemplates.TryGetValue(progressComponentId, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = progressComponentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[progressComponentId] = component;
        }
        if (!template.Components.ContainsKey(progressComponentId))
            template.Components[progressComponentId] = component;
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ItemId == Meat4058))
        {
            var act = new QuestActObjItemGather(component)
            {
                ActId = GatherActId,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = GatherNeed,
                ItemId = Meat4058,
            };
            component.ActTemplates.Add(act);
            var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
                .GetField("_actTemplatesByDetailType", flags)!.GetValue(manager)!;
            if (!actsByType.TryGetValue(nameof(QuestActObjItemGather), out var acts))
            {
                acts = [];
                actsByType[nameof(QuestActObjItemGather)] = acts;
            }
            acts[616] = act;
        }
    }

    private static void SeedBoarLootLink()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || !rows.Any(r => r.LootPackId == Pack4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(Pack4530, out var pack) || pack.Loots.All(l => l.ItemId != Meat4058))
        {
            var loot = new Loot
            {
                Id = Pack4530,
                Group = 0,
                ItemId = Meat4058,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = Pack4530,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[Pack4530] = new LootPack
                {
                    Id = Pack4530,
                    Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
                    Groups = [],
                    ActabilityGroups = [],
                    GroupCount = 1,
                };
            }
            else
            {
                pack.Loots.Add(loot);
                if (pack.LootsByGroupNo.TryGetValue(0, out var group))
                    group.Add(loot);
                else
                    pack.LootsByGroupNo[0] = [loot];
            }
        }

        // The 251 row memo is derived from exactly these two tables: a staged
        // link must never be shadowed by a row memoized from the prior staging.
        QuestFixtureRow.InvalidateAll();
    }
}
