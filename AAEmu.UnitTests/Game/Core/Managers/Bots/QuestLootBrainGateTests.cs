using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// LootBrain live wiring, end to end on the REAL headless actor: the Loot leg
/// now asks the brain for its verdict, and the brain's own arms (the ones the
/// frozen G7c funnel never had) actually withhold a take through the real
/// decision stack — while the frozen arms keep their byte-identical behaviour.
///
/// These drive the full path (<c>QuestDecisionScenario.Run</c> over the real
/// leg loop), so they cover the live adapter's reads as a consumer observes them:
/// which action lands, which funnel token the loot arm prints, and what the
/// ledger banked. The ledger's own bank-only mechanics are exercised separately
/// against the pure store.
/// </summary>
[NotInParallel]
public class QuestLootBrainGateTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint ProbeItemTemplateId = 91_411;
    private const uint JunkItemTemplateId = 91_412;
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
        LootBrainPlanner.CohortResolverForTests = null;
    }

    /// <summary>
    /// The frozen behaviour, unchanged: a recognized lootable corpse is taken
    /// once, its grant lands, and the funnel's loot arm still prints the
    /// <c>validate=ok</c>/<c>lootable=true</c> pair every lane scanner keys on —
    /// now with the brain's own arm named additively beside it.
    /// </summary>
    [Test]
    public async Task RecognizedLootableCorpse_StillTakesOnceThroughTheBrain()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-take");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-take-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);

        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;
        var bagBefore = GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId);

        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-take-2" });

        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(second.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId) - bagBefore).IsEqualTo(1);

        // The funnel's loot arm: the frozen vocabulary, plus the brain's own arm.
        var lootArm = LootArmOf(second);
        await Assert.That(lootArm).Contains("validate=ok");
        await Assert.That(lootArm).Contains("lootable=true");
        await Assert.That(lootArm).Contains("verb=Loot");
        await Assert.That(lootArm).Contains("arm=Emit");

        // The dispatcher banked the take beside the quest leg's own loot-once memory.
        await Assert.That(LootLedger.Read(actor.ActorId, npcObjId).Disposition)
            .IsEqualTo(LootDisposition.Take);
    }

    /// <summary>
    /// THE FIX, on the live stack: a corpse the actor is ALREADY STANDING ON is
    /// taken even with live hostiles on the same tile. The take lands and the item
    /// is granted — the grant the 30-minute run never produced.
    /// </summary>
    [Test]
    public async Task CorpseAtContactRangeWithHostilesOnTheSameTile_TakesTheLoot()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-camp");
        // The corpse spawns ON the actor's tile (0,0,0) — the melee-adjacent shape.
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(0, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-camp-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;
        var bagBefore = GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId);

        // Live hostiles inside the old 15 m safe radius, on the actor's own tile.
        _ = SpawnHostile(session, actor, 91_701, new Vector3(2, 0, 0));
        _ = SpawnHostile(session, actor, 91_702, new Vector3(4, 0, 0));

        var taken = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-camp-2" });

        await Assert.That(taken.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(taken.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId) - bagBefore).IsEqualTo(1);
        var lootArm = LootArmOf(taken);
        await Assert.That(lootArm).Contains("arm=Emit");
        await Assert.That(lootArm).Contains("validate=ok");
        await Assert.That(LootLedger.Read(actor.ActorId, npcObjId).Disposition)
            .IsEqualTo(LootDisposition.Take);
    }

    /// <summary>
    /// THE FIX: a LIVE, UNRELATED fight must not withhold the take on a corpse
    /// that died minutes ago. This is the measured kill10 shape verbatim — Abby
    /// killed boar 44432, then engaged an unrelated live target, and the actor-
    /// global loop-live guard withheld 44432 on every later wake (the lane read
    /// <c>validate=loop-live … arm=LoopRelease</c> twice for the same corpse while
    /// the fight that caused it was on a different objId entirely). The take now
    /// lands and the grant lands with it.
    /// </summary>
    [Test]
    public async Task LiveUnrelatedFight_DoesNotWithholdTheTakeOnADeadCorpse()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-unrelated");
        var here = new Vector3(0, 0, 0);
        var corpseObjId = SpawnBoar(session, actor, here, here + new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(corpseObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-unrelated-1" });

        var corpse = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItemTemplateId, 1));
        corpse.Hp = 0;

        // The actor dropped the corpse as its target and engaged an UNRELATED
        // live target; the auto-attack loop is live, but on a different objId.
        var otherObjId = SpawnHostile(session, actor, 91_811, here + new Vector3(3, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(otherObjId);
        actor.Character.IsAutoAttack = true;
        var bagBefore = GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId);

        var taken = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-unrelated-2" });

        await Assert.That(taken.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(taken.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId) - bagBefore).IsEqualTo(1);
        var lootArm = LootArmOf(taken);
        await Assert.That(lootArm).Contains("arm=Emit");
        await Assert.That(lootArm).DoesNotContain("loop-live");
        await Assert.That(LootLedger.Read(actor.ActorId, corpseObjId).Disposition)
            .IsEqualTo(LootDisposition.Take);
    }

    /// <summary>
    /// THE HAZARD INVARIANT, pinned by name: while the actor is STILL
    /// auto-attacking the very corpse it is trying to loot, the withhold must
    /// still fire. This is the safety property the arm exists for — the kill leg
    /// owns that corpse until it tears itself down, and a take dispatched inside
    /// the loop would race that teardown. Scoping the guard to the corpse must
    /// not weaken it into a no-op.
    /// </summary>
    [Test]
    public async Task AutoAttackingTheCorpseItself_StillWithholdsOnLoopLive()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-ownloop");
        var here = new Vector3(0, 0, 0);
        var corpseObjId = SpawnBoar(session, actor, here, here + new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(corpseObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-ownloop-1" });

        var corpse = session.World.GetNpc(corpseObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItemTemplateId, 1));
        corpse.Hp = 0;
        var bagBefore = GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId);

        // The target is STILL the corpse itself and the loop is still live — the
        // exact state the arm was written for.
        actor.Character.CurrentTarget = corpse;
        actor.Character.IsAutoAttack = true;

        var held = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-ownloop-2" });

        await Assert.That(held.SelectedAction == ActorActionType.Loot).IsFalse();
        var lootArm = LootArmOf(held);
        await Assert.That(lootArm).Contains("validate=loop-live");
        await Assert.That(lootArm).Contains("arm=LoopRelease");
        await Assert.That(GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId) - bagBefore).IsEqualTo(0);
        await Assert.That(LootLedger.Read(actor.ActorId, corpseObjId).Disposition)
            .IsEqualTo(LootDisposition.Undecided);
    }

    /// <summary>
    /// INVARIANT, live: an unreadable wake census still withholds with its OWN
    /// reason at contact range. Driving the leg with no observation snapshot is
    /// what the planner reads UNRESOLVED from, so the fix cannot launder an
    /// unreadable perception into a take.
    /// </summary>
    [Test]
    public async Task UnreadableCensusAtContactRange_HoldsTheTakeWithSafetyUnresolved()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-failclosed");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(0, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-fc-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;

        // No observation snapshot: the planner's census reads UNRESOLVED.
        var (resolved, safe, _, corpseDistance) = LootBrainPlanner.ReadSafety(
            actor.Character, null, LootBrain.DefaultSafeRadiusM, npcObjId);

        await Assert.That(resolved).IsFalse();
        await Assert.That(safe).IsFalse();

        // And the chain still names SafetyUnresolved for that verdict, in place.
        var decision = LootBrain.Decide(new LootBrainInputs(
            ActorObjId: actor.ActorId,
            CorpseObjId: npcObjId,
            SurvivalVetoed: false,
            CorpseState: LootCorpseState.Dead,
            Ownership: LootOwnership.Self,
            ContainerReadable: true,
            ContainerItemCount: 1,
            ContainerInRange: true,
            LoopLive: false,
            AlreadyLooted: false,
            SafetyResolved: resolved,
            Safe: safe,
            NearestHostileDistanceM: float.NaN,
            CorpseDistanceM: corpseDistance,
            Worth: LootWorth.Worthwhile,
            Space: LootSpace.Room,
            PriorDisposition: LootDisposition.Undecided,
            PriorReason: LootReason.None,
            NowUtc: DateTime.UtcNow));
        await Assert.That(decision.Arm).IsEqualTo(LootArm.Safety);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.SafetyUnresolved);
    }

    /// <summary>
    /// The brain's WORTH arm skips an all-junk container TERMINALLY: the corpse is
    /// banked as skipped, never dispatched, and — the bank-only point — never
    /// reconsidered on later wakes even after the container refills with meat.
    /// </summary>
    [Test]
    public async Task AllJunkContainer_SkipsTerminallyAndNeverReconsiders()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-worth");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-worth-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        SeedTrashTemplate(JunkItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (JunkItemTemplateId, 1));
        npc.Hp = 0;

        var skipped = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-worth-2" });

        await Assert.That(skipped.SelectedAction == ActorActionType.Loot).IsFalse();
        var lootArm = LootArmOf(skipped);
        await Assert.That(lootArm).Contains("validate=worth-skip");
        await Assert.That(lootArm).Contains("arm=Worth");
        await Assert.That(LootLedger.Read(actor.ActorId, npcObjId).Disposition)
            .IsEqualTo(LootDisposition.Skipped);
        await Assert.That(LootLedger.Read(actor.ActorId, npcObjId).Cause)
            .IsEqualTo(LootReason.Junk);

        // The container refills with an item that is NOT junk: the banked skip
        // still withholds, and the later wake names the ORIGINAL cause.
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        var later = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-worth-3" });

        await Assert.That(later.SelectedAction == ActorActionType.Loot).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();
        var laterArm = LootArmOf(later);
        await Assert.That(laterArm).Contains("validate=prior-skip");
        await Assert.That(laterArm).Contains("cause=worth-skip");
        await Assert.That(laterArm).Contains("arm=LootOnce");
        await Assert.That(session.World.GetNpc(npcObjId)!.LootingContainer.Items.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The worth arm's optional floor: a container whose entries are ordinary
    /// (non-junk-classified) items still skips when the whole content is under the
    /// caller's copper floor — and an unreadable worth never skips at all. Flipping
    /// the floor alone flips the outcome, which is the proof the knob is the cause.
    /// </summary>
    [Test]
    public async Task ContainerValueFloor_IsTheOnlyDifferenceBetweenSkipAndTake()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-floor");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-floor-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        // A refundable item with a tiny value and a GRADE, so BotBagManager's
        // own junk rule reads false (a grade-0 refundable item is junk by
        // definition) — only the copper floor can skip this one.
        SeedValuedTemplate(JunkItemTemplateId, refund: 5);
        GameplayActorTestRig.SeedLootContainer(npc, (JunkItemTemplateId, 1));
        GradeSeededLoot(npc, 1);
        npc.Hp = 0;

        var floored = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-floor-2", MinContainerValueCopper = 100 });

        await Assert.That(floored.SelectedAction == ActorActionType.Loot).IsFalse();
        await Assert.That(LootArmOf(floored)).Contains("validate=worth-skip");
        await Assert.That(LootLedger.Read(actor.ActorId, npcObjId).Cause).IsEqualTo(LootReason.Junk);

        // Same corpse, same container, floor OFF: the take proceeds.
        LootLedger.Forget(actor.ActorId, npcObjId);
        GameplayActorTestRig.SeedLootContainer(npc, (JunkItemTemplateId, 1));
        GradeSeededLoot(npc, 1);
        var taken = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-floor-3", MinContainerValueCopper = 0 });

        await Assert.That(taken.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(LootArmOf(taken)).Contains("validate=ok");
    }

    /// <summary>
    /// A CORPSE THAT WAS LOOTED IS NEVER RE-DECIDED — AND NEVER NAMED AGAIN.
    ///
    /// This is the live shape of the defect the fix removes: the take EMPTIES the
    /// container, and the very next wake reads that emptiness through the probe arm
    /// — which precedes loot-once in the chain order — so the corpse withheld as an
    /// undecided <c>not-lootable</c>, the terminal take was masked by the tally it
    /// caused, and the same corpse was re-decided on every later wake forever.
    ///
    /// kill25 closed the remaining half: naming the spent corpse on every wake is
    /// itself the defect (39 already-looted verdicts on one corpse, the bot
    /// standing on it), so a corpse this actor terminally looted is no longer a
    /// corpse-recognition candidate at all. The observable contract: after the
    /// take, every later wake dispatches no second Loot, never reports
    /// <c>not-lootable</c>, and no longer names the emptied corpse — the terminal
    /// take stays in the ledger where the next corpse can be found.
    /// </summary>
    [Test]
    public async Task LootedCorpse_NeverReadsNotLootableOnALaterWake()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-reloot");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-reloot-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;

        var taken = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-reloot-2" });
        await Assert.That(taken.SelectedAction).IsEqualTo(ActorActionType.Loot);

        // The take really emptied the container: this is the state the probe reads.
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        var lootsBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot);

        // Three further wakes on the same corpse: the terminal fact must hold on
        // every one of them rather than decaying back into a probe withhold.
        for (var wake = 3; wake <= 5; wake++)
        {
            var later = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"lootb-reloot-{wake}" });

            var lootArm = LootArmOf(later);
            await Assert.That(later.SelectedAction == ActorActionType.Loot).IsFalse();
            await Assert.That(lootArm).DoesNotContain("validate=not-lootable");
            await Assert.That(lootArm).Contains("target=-");
            await Assert.That(lootArm).DoesNotContain($"target={npcObjId}");
        }

        // No wake dispatched a second take of a corpse already emptied.
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot)).IsEqualTo(lootsBefore);
        await Assert.That(LootLedger.Read(actor.ActorId, npcObjId).Disposition).IsEqualTo(LootDisposition.Take);
    }

    /// <summary>
    /// The worth arm never fabricates a skip from an unreadable container: an
    /// entry whose item could not be resolved reads WORTH unresolved and the take
    /// proceeds, exactly as the frozen probe did.
    /// </summary>
    [Test]
    public async Task UnreadableEntryWorth_DoesNotFabricateASkip()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-unreadable");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-unreadable-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        // An entry whose item could NOT be built at all (the template id is not
        // registered, so ItemManager.Create returns null and the seed stores a
        // null Item): the worth read cannot classify it, so it must read
        // unresolved rather than claim junk.
        GameplayActorTestRig.SeedLootContainer(npc, (99_999, 1));
        npc.Hp = 0;

        var prepared = LootBrainPlanner.Prepare(
            actor, BotObservedContext.Capture(actor),
            new LootBrainPlanner.Request(
                CorpseObjId: npcObjId,
                PreyTemplateId: Boar3475,
                MinContainerValueCopper: 0,
                AlreadyLooted: false,
                LoopLive: false,
                SafeRadiusM: LootBrain.DefaultSafeRadiusM,
                NowUtc: DateTime.UtcNow));

        await Assert.That(prepared.Inputs.Worth).IsEqualTo(LootWorth.Unresolved);
        await Assert.That(prepared.Decision.Arm).IsEqualTo(LootArm.Emit);
    }

    /// <summary>
    /// THE FIX, live: a corpse tagged by a fellow managed bot (no engine party,
    /// so TagTeam is 0) resolves Party — the take proceeds. A corpse tagged by
    /// a stranger still withholds as foreign-claim. The cohort resolver names
    /// the bot/stranger split without a login DB.
    /// </summary>
    [Test]
    public async Task FellowBotTagger_ResolvesPartyAndTakesWhileStrangerWithholds()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lootb-cohort");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lootb-cohort-1" });

        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;

        // A second rig actor is the tagger: AddTagger records it as the claim.
        var (taggerActor, _) = GameplayActorTestRig.CreateActor("lootb-tagger");
        npc.CharacterTagging.AddTagger(taggerActor.Character, 10);
        await Assert.That(npc.CharacterTagging.Tagger).IsNotNull();

        // Fellow managed bot (both accounts are cohort bots): Party → take.
        const uint botAccount = 7001;
        const uint strangerAccount = 7002;
        actor.Character.AccountId = botAccount;
        taggerActor.Character.AccountId = botAccount;
        LootBrainPlanner.CohortResolverForTests = accountId => accountId == botAccount;

        var prepared = LootBrainPlanner.Prepare(
            actor, BotObservedContext.Capture(actor),
            new LootBrainPlanner.Request(
                CorpseObjId: npcObjId,
                PreyTemplateId: Boar3475,
                MinContainerValueCopper: 0,
                AlreadyLooted: false,
                LoopLive: false,
                SafeRadiusM: LootBrain.DefaultSafeRadiusM,
                NowUtc: DateTime.UtcNow));

        await Assert.That(prepared.Evidence.TaggerSharesActorTeam).IsTrue();
        await Assert.That(prepared.Inputs.Ownership).IsEqualTo(LootOwnership.Party);
        await Assert.That(LootBrain.MayLoot(prepared.Inputs.Ownership)).IsTrue();
        await Assert.That(prepared.Decision.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(LootVerb.Loot);

        // Stranger tagger (actor is a bot, tagger is not): Foreign → withhold.
        taggerActor.Character.AccountId = strangerAccount;
        var foreign = LootBrainPlanner.Prepare(
            actor, BotObservedContext.Capture(actor),
            new LootBrainPlanner.Request(
                CorpseObjId: npcObjId,
                PreyTemplateId: Boar3475,
                MinContainerValueCopper: 0,
                AlreadyLooted: false,
                LoopLive: false,
                SafeRadiusM: LootBrain.DefaultSafeRadiusM,
                NowUtc: DateTime.UtcNow));

        await Assert.That(foreign.Evidence.TaggerSharesActorTeam).IsFalse();
        await Assert.That(foreign.Inputs.Ownership).IsEqualTo(LootOwnership.Foreign);
        await Assert.That(LootBrain.MayLoot(foreign.Inputs.Ownership)).IsFalse();
        await Assert.That(foreign.Decision.Arm).IsEqualTo(LootArm.Ownership);
        await Assert.That(foreign.Decision.Reason).IsEqualTo(LootReason.OwnershipForeign);
        LootBrainPlanner.CohortResolverForTests = null;
    }

    /// <summary>
    /// The ledger's bank-only mechanics, against the store itself: only terminal
    /// dispositions are accepted, a take is monotonic, and a read of an unbaked
    /// corpse is undecided — never a fabricated skip.
    /// </summary>
    [Test]
    public async Task Ledger_BanksOnlyTerminalFactsAndNeverUnbanksATake()
    {
        LootLedger.ClearAll();
        var now = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        await Assert.That(LootLedger.Read(1, 500)).IsEqualTo((LootDisposition.Undecided, LootReason.None));

        // An undecided write is refused: the bank carries terminal facts only.
        await Assert.That(LootLedger.Bank(1, 500, LootDisposition.Undecided, LootReason.LoopLive, now)).IsFalse();
        await Assert.That(LootLedger.Read(1, 500).Disposition).IsEqualTo(LootDisposition.Undecided);

        // A skip banks with its cause and reads back.
        await Assert.That(LootLedger.Bank(1, 500, LootDisposition.Skipped, LootReason.NoBagRoom, now)).IsTrue();
        await Assert.That(LootLedger.Read(1, 500)).IsEqualTo((LootDisposition.Skipped, LootReason.NoBagRoom));

        // A take overrides a skip, then a later skip can never override the take.
        await Assert.That(LootLedger.Bank(1, 500, LootDisposition.Take, LootReason.Emitted, now)).IsTrue();
        await Assert.That(LootLedger.Bank(1, 500, LootDisposition.Skipped, LootReason.Junk, now)).IsFalse();
        await Assert.That(LootLedger.Read(1, 500).Disposition).IsEqualTo(LootDisposition.Take);

        // Keyed per (actor, corpse): another actor's corpse is untouched.
        await Assert.That(LootLedger.Read(2, 500).Disposition).IsEqualTo(LootDisposition.Undecided);

        LootLedger.ClearAll();
        await Assert.That(LootLedger.Count).IsEqualTo(0);
    }

    // ------------------------------------------------------------ fixture

    /// <summary>The loot leg's funnel fragment from a run result's leg evidence.</summary>
    private static string LootArmOf(QuestDecisionScenario.QuestRunResult result)
        => result.LegEvidence.FirstOrDefault(e => e.Leg == QuestLegId.Loot).Detail ?? "";

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

    private static uint SpawnBoar(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = session.SpawnNpc(Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return npcObjId;
    }

    /// <summary>A live hostile beside the corpse, joined to the actor's region so the wake census sees it.</summary>
    private static uint SpawnHostile(HeadlessSession session, GameplayActor actor, uint templateId, Vector3 npcPos)
    {
        var objId = session.SpawnNpc(templateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, npcPos);
        var region = session.World.GetRegionByPos(npcPos);
        region?.AddObject(npc);
        npc.Region = region;
        return objId;
    }

    /// <summary>A sellable, junk-category item template (BotBagManager.IsTrash: true).</summary>
    private static void SeedTrashTemplate(uint templateId)
    {
        GameplayActorTestRig.SeedItemTemplate(templateId);
        var template = ItemManager.Instance.GetTemplate(templateId)!;
        template.Sellable = true;
        template.CategoryId = (int)AAEmu.Game.Models.Game.Items.ItemCategory.Trash_Miscellaneous;
        template.Refund = 1;
    }

    /// <summary>
    /// A sellable item in a PROTECTED (non-trash) category with a small refund:
    /// <c>BotBagManager.IsTrash</c> reads false, so only the copper floor can skip it.
    /// </summary>
    private static void SeedValuedTemplate(uint templateId, int refund)
    {
        GameplayActorTestRig.SeedItemTemplate(templateId);
        var template = ItemManager.Instance.GetTemplate(templateId)!;
        template.Sellable = true;
        template.CategoryId = (int)AAEmu.Game.Models.Game.Items.ItemCategory.Material;
        template.Refund = refund;
    }

    /// <summary>Gives every seeded container entry the given grade (the seed itself always creates grade 0).</summary>
    private static void GradeSeededLoot(Npc npc, byte grade)
    {
        foreach (var (_, entry) in npc.LootingContainer.Items)
        {
            if (entry?.Item != null)
                entry.Item.Grade = grade;
        }
    }

    private static void SetFreeForAll(Npc npc)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(AAEmu.Game.Models.Game.Items.Containers.LootingContainer)
            .GetProperty("TeamLootingRule", flags)
            ?.SetValue(npc.LootingContainer,
                new AAEmu.Game.Models.Game.Team.LootingRule
                {
                    LootMethod = AAEmu.Game.Models.Game.Team.LootingRuleMethod.FreeForAll
                });
        typeof(AAEmu.Game.Models.Game.Items.Containers.LootingContainer)
            .GetProperty("LootOwnerType", flags)
            ?.SetValue(npc.LootingContainer, LootOwnerType.Npc);
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
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
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
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
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
