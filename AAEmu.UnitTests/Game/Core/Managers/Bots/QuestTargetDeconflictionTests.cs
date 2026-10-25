using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Prey deconfliction in <see cref="QuestObjectiveTargetSelector"/>: two actors
/// with the SAME census converge on ONE prey between them rather than both.
/// Driven through the REAL headless actors (no fakes) and the REAL funnel, so
/// sibling commitment is read the same way production reads it — the wake's own
/// 25 m <c>NearbyCharacterObjIds</c> census, each character's live
/// <c>CurrentTarget</c>.
///
/// The contract worth pinning is the ASSIGNMENT, not the plumbing: a lone actor
/// still takes the nearest legal prey; two actors sharing one census split; and
/// the split is a pure function of the same world state, so waking the actors in
/// either order cannot change who gets what.
/// </summary>
[NotInParallel]
public class QuestTargetDeconflictionTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_nextBoarObjId = 0x7A000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
    }

    [Test]
    public async Task Canonical260_ThreeLootSources_NearestLegalPreyIsSelectable()
    {
        // The real quest template is loaded by SetUp. Mirror only the three
        // exact canonical NPC -> pack -> item rows missing from the headless
        // ItemManager/LootGameData surfaces; restore all prior rows afterward.
        var sources = new (uint Npc, uint Pack, uint Mapping, uint Loot)[]
        {
            (3484, 4533, 2096, 5807),
            (3497, 5746, 17961, 18068),
            (7675, 5745, 17960, 18067),
        };
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var map = (Dictionary<uint, List<LootPackDroppingNpc>>)typeof(ItemManager)
            .GetField("_lootPackDroppingNpc", flags)!.GetValue(ItemManager.Instance)!;
        var packs = (Dictionary<uint, LootPack>)typeof(LootGameData)
            .GetField("_lootPacks", flags)!.GetValue(LootGameData.Instance)!;
        var priorMaps = sources.ToDictionary(s => s.Npc, s => map.GetValueOrDefault(s.Npc));
        var priorPacks = sources.ToDictionary(s => s.Pack, s => packs.GetValueOrDefault(s.Pack));
        try
        {
            foreach (var s in sources)
            {
                map[s.Npc] = [new LootPackDroppingNpc
                    { Id = s.Mapping, NpcId = s.Npc, LootPackId = s.Pack, DefaultPack = true }];
                var loot = new Loot { Id = s.Loot, Group = 0, ItemId = 8128,
                    DropRate = 10_000_000, MinAmount = 1, MaxAmount = 1,
                    LootPackId = s.Pack, GradeId = 0, AlwaysDrop = false };
                packs[s.Pack] = new LootPack { Id = s.Pack, Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
                    Groups = [], ActabilityGroups = [], GroupCount = 1 };
            }
            QuestFixtureRow.Invalidate(260);
            var row = QuestFixtureRow.FromQuestData(260);
            await Assert.That(row.GatherActId).IsEqualTo(6902u);
            await Assert.That(row.PreyItem).IsEqualTo(8128u);
            await Assert.That(row.Need).IsEqualTo(3);
            await Assert.That(row.PreyTemplate).IsEqualTo(3484u);
            await Assert.That(row.PreyPack).IsEqualTo(4533u);

            var (actor, session) = GameplayActorTestRig.CreateActor("quest260-three-prey");
            GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
            var region = session.World.GetRegionByPos(Vector3.Zero)
                ?? throw new InvalidOperationException("Native fixture region missing.");
            region.AddObject(actor.Character);
            actor.Character.Region = region;
            // Accepted quest state is fixture setup, matching Activate251;
            // the selector receives no chosen target or committed CurrentTarget.
            actor.Character.Quests.ActiveQuests[260] = new Quest(actor.Character)
            {
                TemplateId = 260, Status = QuestStatus.Progress,
                Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0]
            };
            var normalNear = SpawnNpcAt(session, 3497, new Vector3(3, 0, 0));
            var normalFar = SpawnNpcAt(session, 7675, new Vector3(7, 0, 0));
            var strongFar = SpawnNpcAt(session, 3484, new Vector3(12, 0, 0));
            actor.Character.Level = 5;
            foreach (var id in new[] { normalNear, normalFar })
            {
                var npc = session.World.GetNpc(id)!;
                npc.Template.NpcGradeId = NpcGradeType.Normal;
                npc.Template.Level = 4;
                npc.Level = 4;
            }
            var strong = session.World.GetNpc(strongFar)!;
            strong.Template.NpcGradeId = NpcGradeType.Strong;
            strong.Template.Level = 5;
            strong.Level = 5;
            await Assert.That(actor.Character.CurrentTarget).IsNull();
            foreach (var id in new[] { normalNear, normalFar, strongFar })
            {
                var npc = session.World.GetNpc(id)!;
                await Assert.That(CombatDecisionTree.IsHostileTarget(actor.Character, npc)).IsTrue();
                await Assert.That(actor.Character.CanAttack(npc)).IsTrue();
                await Assert.That(actor.Character.CanSeeTarget(npc)).IsTrue();
            }
            var funnel = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.From(actor.Observe()), "quest260-three-sources", row);
            await Assert.That(funnel.SourceResolved).IsTrue();
            await Assert.That(funnel.Candidates.Select(c => c.ObjId))
                .IsEquivalentTo(new[] { normalNear, normalFar, strongFar });
            // Existing single-representative code marks both Normal source
            // templates as mismatches and chooses the more distant Strong NPC.
            // Expected behavior is to choose the nearest legally eligible
            // source of item 8128, without assigning any target to the bot.
            await Assert.That(funnel.SelectedObjId).IsEqualTo(normalNear);
            await Assert.That(funnel.Candidates.Single(c => c.ObjId == normalNear).QuestRelevant).IsTrue();
            await Assert.That(funnel.Candidates.Single(c => c.ObjId == normalFar).QuestRelevant).IsTrue();
            await Assert.That(actor.Character.CurrentTarget).IsNull();

            // Source types do not override a valid current commitment.
            actor.Character.CurrentTarget = strong;
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-committed", row).SelectedObjId).IsEqualTo(strongFar);
            actor.Character.CurrentTarget = null;

            // A foreign tag moves the nearest source behind useful alternatives.
            var (other, _) = GameplayActorTestRig.CreateActor("three-sources-tagger");
            var near = session.World.GetNpc(normalNear)!;
            near.CharacterTagging = new Tagging(near);
            near.CharacterTagging.AddTagger(other.Character, int.MaxValue / 4);
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-claimed", row).SelectedObjId).IsEqualTo(normalFar);
            near.CharacterTagging.ClearAllTaggers();
            near.Hp = 0;
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-dead", row).SelectedObjId).IsEqualTo(normalFar);
            near.Hp = 100; // Test world input, no resurrection claim.
            var ownMapping = map[3497];
            map[3497] = [new LootPackDroppingNpc { NpcId = 3497, LootPackId = 4533, DefaultPack = true }];
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-wrong-pack", row).SelectedObjId).IsEqualTo(normalFar);
            map[3497] = ownMapping;
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-restored", row).SelectedObjId).IsEqualTo(normalNear);
            // Snapshot inputs describe a fulfilled need or absent quest; neither
            // may create further hunt work even though matching mobs are alive.
            var fulfilled = BotObservedContext.Capture(actor) with { BagItemCounts = new Dictionary<uint, int> { [8128] = 3 } };
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                fulfilled, "three-sources-fulfilled", row).SelectedObjId).IsEqualTo(0u);
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor) with { ActiveQuestIds = [] },
                "three-sources-noquest", row).SelectedObjId).IsEqualTo(0u);

            GameplayActorTestRig.SetNpcPosition(session, normalNear, new Vector3(18, 0, 0));
            GameplayActorTestRig.SetNpcPosition(session, normalFar, new Vector3(24, 0, 0));
            GameplayActorTestRig.SetNpcPosition(session, strongFar, new Vector3(30, 0, 0));
            actor.Character.CurrentTarget = near;
            TravelLegDispatch.EnsureJourney(actor, normalNear, near.Transform.World.Position, 3, 2,
                TravelLegDispatch.PursuitMoveOwner);
            TravelIntentStore.SetPursuitPurpose(actor.ActorId, 260);
            // Declared failed-navigation terminal fixture: apply the existing
            // recovery rule across source types, without claiming a live failure.
            TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner);
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-exhausted", row).SelectedObjId).IsEqualTo(normalFar);
            GameplayActorTestRig.SetNpcPosition(session, normalNear, new Vector3(2, 0, 0));
            await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, normalNear, 260)).IsFalse();
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "three-sources-contact", row).SelectedObjId).IsEqualTo(normalNear);
            TravelIntentStore.Disarm(actor.ActorId, TravelLegDispatch.PursuitMoveOwner);
        }
        finally
        {
            foreach (var s in sources)
            {
                if (priorMaps[s.Npc] is { } rows) map[s.Npc] = rows; else map.Remove(s.Npc);
                if (priorPacks[s.Pack] is { } pack) packs[s.Pack] = pack; else packs.Remove(s.Pack);
            }
            QuestFixtureRow.Invalidate(260);
        }
    }

    // ------------------------------------------------------------- the contract

    /// <summary>
    /// THE DEFECT: with two boars and two actors that see the SAME census, the
    /// first to be evaluated takes the nearest. Before deconfliction the second
    /// re-derived the identical flat order and took that same boar — both actors,
    /// one prey, one corpse, one lootable winner. The second must instead take
    /// the boar nobody is on.
    /// </summary>
    [Test]
    public async Task TwoActorsSameCensus_SplitAcrossPrey()
    {
        var (a1, session) = GameplayActorTestRig.CreateActor("deconf-one");
        var (a2, _) = GameplayActorTestRig.CreateActor("deconf-two");
        // One world: both characters must resolve each other for the sibling read.
        JoinSiblingToRegion(session, a2, new Vector3(2, 0, 0));

        var near = SpawnBoar(session, a1, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(a1.Character);
        Activate251(a2.Character);

        // Actor 1 wakes first and commits — its target is world state, not local.
        var first = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-1", Fixture());
        await Assert.That(first.SelectedObjId).IsEqualTo(near);
        a1.Character.CurrentTarget = session.World.GetNpc(near);

        // Actor 2 wakes on the SAME census, with actor 1 now visibly committed.
        var second = QuestObjectiveTargetSelector.Evaluate(a2.Character, BotObservedContext.From(a2.Observe()), "deconf-2", Fixture());

        // It must NOT pile onto the boar actor 1 is already fighting...
        await Assert.That(second.SelectedObjId == near).IsFalse();
        // ...it takes the other one.
        await Assert.That(second.SelectedObjId).IsEqualTo(far);

        // And the two wakes really did agree on one boar BETWEEN them: distinct.
        await Assert.That(first.SelectedObjId == second.SelectedObjId).IsFalse();
    }

    /// <summary>
    /// A LONE actor is unchanged. With no sibling committed to anything, the
    /// nearest legal prey is still the selection — the deconfliction tier is
    /// inert when there is no contention, so the historical
    /// nearest-first behaviour is preserved exactly.
    /// </summary>
    [Test]
    public async Task LoneActor_StillTakesTheNearestPrey()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("deconf-lone");
        var near = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(actor.Character);

        var funnel = QuestObjectiveTargetSelector.Evaluate(actor.Character, BotObservedContext.From(actor.Observe()), "deconf-lone-1", Fixture());

        await Assert.That(funnel.SelectedObjId).IsEqualTo(near);
    }

    /// <summary>
    /// ORDER INDEPENDENCE. The same world state evaluated in the opposite
    /// sequence must produce the same PAIRING of actors to prey — not the same
    /// actor order. Swapping which actor wakes first may legitimately swap who
    /// gets the nearest boar (that is nearest-first, and it is deterministic);
    /// what must not vary is that the two actors END UP ON DIFFERENT PREY.
    ///
    /// This is the property that stops the fix degenerating into a race: a bot
    /// that happened to tick first must not be able to starve the other.
    /// </summary>
    [Test]
    public async Task OrderIndependent_SameAssignmentEitherWakeOrder()
    {
        var (a1, session) = GameplayActorTestRig.CreateActor("deconf-ord-1");
        var (a2, _) = GameplayActorTestRig.CreateActor("deconf-ord-2");
        JoinSiblingToRegion(session, a2, new Vector3(2, 0, 0));

        var near = SpawnBoar(session, a1, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(a1.Character);
        Activate251(a2.Character);

        // Order A: actor 1 first.
        var aFirst = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-ord-a1", Fixture());
        await Assert.That(aFirst.SelectedObjId).IsEqualTo(near);
        a1.Character.CurrentTarget = session.World.GetNpc(near);
        var aSecond = QuestObjectiveTargetSelector.Evaluate(a2.Character, BotObservedContext.From(a2.Observe()), "deconf-ord-a2", Fixture());

        // Reset the world to the identical starting state.
        a1.Character.CurrentTarget = null;
        a2.Character.CurrentTarget = null;

        // Order B: actor 2 first.
        var bFirst = QuestObjectiveTargetSelector.Evaluate(a2.Character, BotObservedContext.From(a2.Observe()), "deconf-ord-b1", Fixture());
        await Assert.That(bFirst.SelectedObjId).IsEqualTo(near);
        a2.Character.CurrentTarget = session.World.GetNpc(near);
        var bSecond = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-ord-b2", Fixture());

        // Same SET of prey in use by the pair, whichever actor led.
        var orderA = new[] { aFirst.SelectedObjId, aSecond.SelectedObjId }.OrderBy(x => x).ToArray();
        var orderB = new[] { bFirst.SelectedObjId, bSecond.SelectedObjId }.OrderBy(x => x).ToArray();

        await Assert.That(orderA).IsEquivalentTo(orderB);
        // And in both orders the pair is deconflicted — two actors, two boars.
        await Assert.That(aSecond.SelectedObjId == aFirst.SelectedObjId).IsFalse();
        await Assert.That(bSecond.SelectedObjId == bFirst.SelectedObjId).IsFalse();
        // The follower took the far boar in both directions.
        await Assert.That(aSecond.SelectedObjId).IsEqualTo(far);
        await Assert.That(bSecond.SelectedObjId).IsEqualTo(far);
    }

    /// <summary>
    /// An actor ALREADY on a boar keeps it. The deconfliction reads SIBLING
    /// commitment only — an actor is never diverted from its own live target by
    /// its own history, so re-evaluating mid-fight cannot make a bot drop the
    /// prey it is already killing (which would strand the corpse it must loot).
    /// </summary>
    [Test]
    public async Task ActorAlreadyOnPrey_KeepsIt_EvenIfAnotherActorContests()
    {
        var (a1, session) = GameplayActorTestRig.CreateActor("deconf-hold-1");
        var (a2, _) = GameplayActorTestRig.CreateActor("deconf-hold-2");
        JoinSiblingToRegion(session, a2, new Vector3(2, 0, 0));

        var near = SpawnBoar(session, a1, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(a1.Character);
        Activate251(a2.Character);

        // BOTH are on the near boar (the pre-fix world state, staged directly).
        a1.Character.CurrentTarget = session.World.GetNpc(near);
        a2.Character.CurrentTarget = session.World.GetNpc(near);

        // Re-evaluating actor 1 must not make it abandon its own kill.
        var funnel = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-hold-1", Fixture());
        await Assert.That(funnel.SelectedObjId).IsEqualTo(near);
    }

    /// <summary>
    /// Deconfliction is a TIE-BREAK, never a filter. When every candidate prey
    /// is contested (more committed siblings than free boars), the actor must
    /// still receive the NEAREST contested one rather than nothing — otherwise a
    /// crowded map would park the actor with no target and no corpse to loot.
    ///
    /// A genuinely crowded census takes TWO siblings, not one: a character
    /// holds a single <c>CurrentTarget</c> (<c>Unit.CurrentTarget</c>), so no
    /// lone sibling can ever be committed to both boars at once. Each sibling
    /// takes a DIFFERENT boar, which is what puts every candidate into the
    /// contested tier and leaves the actor choosing afresh nowhere free to go.
    /// </summary>
    [Test]
    public async Task AllCandidatesContested_StillSelectsTheNearest_NoStarvation()
    {
        var (a1, session) = GameplayActorTestRig.CreateActor("deconf-crowd-1");
        var (a2, _) = GameplayActorTestRig.CreateActor("deconf-crowd-2");
        var (a3, _) = GameplayActorTestRig.CreateActor("deconf-crowd-3");
        JoinSiblingToRegion(session, a2, new Vector3(2, 0, 0));
        JoinSiblingToRegion(session, a3, new Vector3(4, 0, 0));

        var near = SpawnBoar(session, a1, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(a1.Character);
        Activate251(a2.Character);
        Activate251(a3.Character);

        // Each sibling holds a DIFFERENT boar — so BOTH candidates are
        // contested and nothing is free. a1 itself has committed to nothing,
        // so no tier-0 self-commitment can answer for it.
        a2.Character.CurrentTarget = session.World.GetNpc(near);
        a3.Character.CurrentTarget = session.World.GetNpc(far);

        var funnel = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-crowd-1", Fixture());

        // Still a target, and still the nearest one — not nothing, and not the
        // tier-2 winner decided by census enumeration order.
        await Assert.That(funnel.SelectedObjId).IsEqualTo(near);
    }

    /// <summary>
    /// A sibling's target that is NOT prey is irrelevant. Only prey commitment
    /// should push an actor off a boar; a sibling fighting something else must
    /// not perturb the nearest-first order.
    /// </summary>
    [Test]
    public async Task SiblingCommittedToNonPrey_DoesNotPerturbNearestFirst()
    {
        var (a1, session) = GameplayActorTestRig.CreateActor("deconf-other-1");
        var (a2, _) = GameplayActorTestRig.CreateActor("deconf-other-2");
        JoinSiblingToRegion(session, a2, new Vector3(2, 0, 0));

        var near = SpawnBoar(session, a1, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(a1.Character);
        Activate251(a2.Character);

        // Actor 2 is locked onto a NON-prey target.
        var other = SpawnExtraOther(session, new Vector3(4, 0, 0));
        a2.Character.CurrentTarget = session.World.GetNpc(other);

        var funnel = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-other-1", Fixture());

        await Assert.That(funnel.SelectedObjId).IsEqualTo(near);
    }

    /// <summary>
    /// Deconfliction must not disturb the FUNNEL accounting: the raw/relevant/
    /// legal tallies and every candidate row still describe the same census,
    /// because selection ordering is the only thing that changed.
    ///
    /// The commitment belongs to the SIBLING, not to the actor being evaluated:
    /// an actor's OWN target is tier 0 and must win by design (see
    /// <c>ActorAlreadyOnPrey_KeepsIt_EvenIfAnotherActorContests</c>), so
    /// staging it on the evaluator would pin the selection to that same boar and
    /// the assertion below would contradict the tier contract instead of
    /// exercising the funnel.
    /// </summary>
    [Test]
    public async Task FunnelAccounting_UntouchedByDeconfliction()
    {
        var (a1, session) = GameplayActorTestRig.CreateActor("deconf-funnel-1");
        var (a2, _) = GameplayActorTestRig.CreateActor("deconf-funnel-2");
        JoinSiblingToRegion(session, a2, new Vector3(2, 0, 0));

        var near = SpawnBoar(session, a1, new Vector3(0, 0, 0), new Vector3(3, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(a1.Character);
        Activate251(a2.Character);

        // The sibling holds the NEAR boar, so the free far boar outranks it.
        a2.Character.CurrentTarget = session.World.GetNpc(near);

        var funnel = QuestObjectiveTargetSelector.Evaluate(a1.Character, BotObservedContext.From(a1.Observe()), "deconf-funnel-1", Fixture());

        // Both boars were still COUNTED as relevant+legal even though only one
        // was selected — deconfliction reorders, it never filters.
        await Assert.That(funnel.RelevantCount).IsEqualTo(2);
        await Assert.That(funnel.LegalCount).IsEqualTo(2);
        await Assert.That(funnel.Candidates.Count).IsEqualTo(2);
        // And the diagnostic still reports the SELECTED row's own distance.
        await Assert.That(funnel.SelectedObjId).IsEqualTo(far);
    }

    [Test]
    public async Task TwoFreshActors_BeforeEitherCommits_SelectTheSameNearestPrey()
    {
        var (first, session) = GameplayActorTestRig.CreateActor("fresh-competition-a");
        var (second, _) = GameplayActorTestRig.CreateActor("fresh-competition-b");
        JoinSiblingToRegion(session, second, new Vector3(2, 0, 0));
        var near = SpawnBoar(session, first, Vector3.Zero, new Vector3(3, 0, 0));
        _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(first.Character);
        Activate251(second.Character);
        // Characterize the uncommitted window; selection itself reserves nothing.
        var a = QuestObjectiveTargetSelector.Evaluate(first.Character, BotObservedContext.From(first.Observe()), "fresh-a", Fixture());
        var b = QuestObjectiveTargetSelector.Evaluate(second.Character, BotObservedContext.From(second.Observe()), "fresh-b", Fixture());
        await Assert.That(a.SelectedObjId).IsEqualTo(near);
        await Assert.That(b.SelectedObjId).IsEqualTo(near);
        await Assert.That(first.Character.CurrentTarget).IsNull();
        await Assert.That(second.Character.CurrentTarget).IsNull();
    }

    [Test]
    public async Task ForeignTagWithoutVisibleCurrentTarget_PrefersUnclaimedAlternative()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("foreign-claim-choice");
        var (tagger, _) = GameplayActorTestRig.CreateActor("foreign-claim-owner");
        JoinSiblingToRegion(session, tagger, new Vector3(2, 0, 0));
        var near = SpawnBoar(session, actor, Vector3.Zero, new Vector3(3, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(near)!;
        npc.CharacterTagging.AddTagger(tagger.Character, 60);
        await Assert.That(npc.CharacterTagging.Tagger).IsSameReferenceAs(tagger.Character);
        await Assert.That(tagger.Character.CurrentTarget).IsNull();
        var selected = QuestObjectiveTargetSelector.Evaluate(actor.Character, BotObservedContext.From(actor.Observe()), "foreign-claim", Fixture());
        await Assert.That(selected.SelectedObjId).IsEqualTo(far);
        await Assert.That(selected.Candidates.Any(c => c.ObjId == near && c.CombatLegal)).IsTrue();
        await Assert.That(selected.Candidates.Single(c => c.ObjId == near).HasCompetingClaim).IsTrue();
        npc.CharacterTagging.ClearAllTaggers();
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, BotObservedContext.From(actor.Observe()), "claim-released", Fixture()).SelectedObjId).IsEqualTo(near);
        // Claim preference does not invent an attack prohibition or loot permission.
    }

    [Test]
    [Arguments("all-contested")]
    [Arguments("all-contested-committed")]
    [Arguments("own-tag")]
    [Arguments("self-defense")]
    [Arguments("real-party")]
    public async Task ClaimPreference_PreservesSupportedFallbacksAndOwnership(string control)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("claim-control-" + control);
        var (owner, _) = GameplayActorTestRig.CreateActor("claim-control-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var near = SpawnBoar(session, actor, Vector3.Zero, new Vector3(3, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(12, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(near)!;
        if (control == "real-party")
        {
            GameplayActorTestRig.ForceSeedTeamManager();
            await Assert.That(actor.PartyInvite(owner.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(owner.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
        }
        npc.CharacterTagging.AddTagger(control == "own-tag" ? actor.Character : owner.Character, 60);
        if (control.StartsWith("all-contested", StringComparison.Ordinal))
            session.World.GetNpc(far)!.CharacterTagging.AddTagger(owner.Character, 60);
        if (control == "all-contested-committed") actor.SetTarget(near);
        if (control == "self-defense") npc.CurrentAggroTarget = actor.Character;
        var running = actor.MoveTo(new Vector3(100, 0, 0));
        var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character, BotObservedContext.From(actor.Observe()), "claim-control", Fixture());
        await Assert.That(chosen.SelectedObjId).IsEqualTo(near);
        await Assert.That(chosen.LegalCount).IsEqualTo(2);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(running);
        await Assert.That(running.State).IsEqualTo(ActorLifecycleState.Running);
        actor.Stop();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task GatherItem_ForeignClaimLosesToUsableAlternative_EvenWhenCommitted(bool alreadyCommitted, bool siblingOnAlternative)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gather-claim-choice");
        var (owner, _) = GameplayActorTestRig.CreateActor("gather-claim-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var near = SpawnBoar(session, actor, Vector3.Zero, new Vector3(6, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(18, 0, 0));
        Activate251(actor.Character);
        session.World.GetNpc(near)!.CharacterTagging.AddTagger(owner.Character, 60);
        if (alreadyCommitted) actor.SetTarget(near);
        if (siblingOnAlternative) owner.SetTarget(far);

        var snapshot = BotObservedContext.Capture(actor);
        var before = actor.Character.CurrentTarget;
        var issued = actor.AuditTrace.Count;
        var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character,
            snapshot, "gather-foreign-commitment", Fixture());

        await Assert.That(chosen.SelectedObjId).IsEqualTo(far);
        await Assert.That(chosen.LegalCount).IsEqualTo(2);
        await Assert.That(chosen.Candidates.Single(c => c.ObjId == near).HasCompetingClaim).IsTrue();
        await Assert.That(chosen.Candidates.Single(c => c.ObjId == far).HasCompetingClaim).IsFalse();
        // Selection only: no target mutation, attack, loot or quest credit.
        await Assert.That(ReferenceEquals(actor.Character.CurrentTarget, before)).IsTrue();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(issued);

        try
        {
            ActorRequest? pursuit = null;
            for (var wake = 0; wake < 5 && pursuit == null; wake++)
            {
                var result = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"gather-claim-execute-{wake}" });
                if (result.Request is { Action: ActorActionType.Move, TargetId: var target } move && target == far)
                    pursuit = move;
            }
            await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(far);
            await Assert.That(pursuit).IsNotNull();
            await Assert.That(pursuit!.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
            var position = actor.Character.Transform.World.Position;
            actor.Tick(TimeSpan.FromMilliseconds(100));
            await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, new Vector3(18, 0, 0)))
                .IsLessThan(Vector3.Distance(position, new Vector3(18, 0, 0)));
            await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Objectives.All(x => x == 0)).IsTrue();
        }
        finally
        {
            actor.StopAutoAttack();
            actor.Stop();
            TravelIntentStore.Disarm(actor.ActorId, TravelLegDispatch.PursuitMoveOwner);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GatherItem_UnusableAlternativeDoesNotDisplaceForeignFallback(bool exhaustedApproach)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gather-claim-recovery");
        var (owner, _) = GameplayActorTestRig.CreateActor("gather-claim-recovery-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var near = SpawnBoar(session, actor, Vector3.Zero, new Vector3(6, 0, 0));
        var far = SpawnExtraBoar(session, new Vector3(18, 0, 0));
        Activate251(actor.Character);
        session.World.GetNpc(near)!.CharacterTagging.AddTagger(owner.Character, 60);
        QuestBehavior.ClearLeashStaleMemory();
        try
        {
            if (exhaustedApproach)
            {
                TravelLegDispatch.EnsureJourney(actor, far, new Vector3(18, 0, 0), 3, 2,
                    TravelLegDispatch.PursuitMoveOwner);
                TravelIntentStore.SetPursuitPurpose(actor.ActorId, Quest251);
                TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                    TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner);
                await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, far, Quest251)).IsTrue();
            }
            else
            {
                for (var i = 0; i < QuestBehavior.CombatStallWakes; i++)
                    QuestBehavior.CountLeashStaleWake(actor.ActorId, far, 100, $"claim-stale-{i}", out _);
                await Assert.That(QuestBehavior.IsLeashStaleTarget(actor.ActorId, far)).IsTrue();
            }
            var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "gather-unusable-alternative", Fixture());
            await Assert.That(chosen.SelectedObjId).IsEqualTo(near);
            await Assert.That(chosen.LegalCount).IsEqualTo(2);
        }
        finally
        {
            QuestBehavior.ClearLeashStaleMemory();
            TravelIntentStore.Disarm(actor.ActorId, TravelLegDispatch.PursuitMoveOwner);
            actor.Stop();
        }
    }

    [Test]
    public async Task GatherItem_SoleForeignPreyRemainsSelectable()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gather-sole-foreign");
        var (owner, _) = GameplayActorTestRig.CreateActor("gather-sole-owner");
        var prey = SpawnBoar(session, actor, Vector3.Zero, new Vector3(6, 0, 0));
        Activate251(actor.Character);
        session.World.GetNpc(prey)!.CharacterTagging.AddTagger(owner.Character, 60);
        var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "gather-sole-foreign", Fixture());
        await Assert.That(chosen.SelectedObjId).IsEqualTo(prey);
        await Assert.That(chosen.LegalCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task HuntObjective_ClaimPreferenceFollowsNativeCredit(bool sharedHuntCredit, bool committed,
        bool siblingOnAlternative)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hunt-foreign-control");
        var (owner, _) = GameplayActorTestRig.CreateActor("hunt-foreign-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var row = QuestFixtureRow.FromQuestData(266);
        // Canonical group-hunt quest; native group membership determines relevance.
        await Assert.That(row.HuntActId).IsNotEqualTo(0u);
        SpawnBoar(session, actor, Vector3.Zero, new Vector3(80, 0, 0));
        var near = SpawnNpcAt(session, 3494, new Vector3(6, 0, 0));
        var alternative = SpawnNpcAt(session, 3494, new Vector3(18, 0, 0));
        actor.Character.Quests.ActiveQuests[266] = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
        {
            Status = QuestStatus.Progress, Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        session.World.GetNpc(near)!.CharacterTagging.AddTagger(owner.Character, 60);
        if (siblingOnAlternative)
            owner.Character.CurrentTarget = session.World.GetNpc(alternative);
        if (committed)
            actor.SetTarget(near);
        var oldSharing = AppConfiguration.Instance.World.TagShareEnabled;
        try
        {
            AppConfiguration.Instance.World.TagShareEnabled = sharedHuntCredit;
            var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "hunt-native-credit", row);
            // With shared credit enabled, preserve the existing committed hunt.
            // Otherwise prefer a real opportunity to earn native tag/team credit.
            await Assert.That(chosen.SelectedObjId).IsEqualTo(sharedHuntCredit && (committed || siblingOnAlternative) ? near : alternative);
            await Assert.That(chosen.LegalCount).IsEqualTo(2);
        }
        finally
        {
            AppConfiguration.Instance.World.TagShareEnabled = oldSharing;
        }
    }

    [Test]
    [Arguments(false, "sole")]
    [Arguments(true, "sole")]
    [Arguments(false, "stale")]
    [Arguments(true, "stale")]
    [Arguments(false, "exhausted")]
    [Arguments(true, "exhausted")]
    public async Task HuntObjective_ClaimPreferenceKeepsLegalFallback(bool sharedHuntCredit, string alternativeState)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hunt-claim-fallback");
        var (owner, _) = GameplayActorTestRig.CreateActor("hunt-claim-fallback-owner");
        SpawnBoar(session, actor, Vector3.Zero, new Vector3(80, 0, 0));
        var near = SpawnNpcAt(session, 3494, new Vector3(6, 0, 0));
        var far = alternativeState == "sole" ? 0u : SpawnNpcAt(session, 3494, new Vector3(18, 0, 0));
        var row = QuestFixtureRow.FromQuestData(266);
        actor.Character.Quests.ActiveQuests[266] = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
        {
            Status = QuestStatus.Progress, Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        session.World.GetNpc(near)!.CharacterTagging.AddTagger(owner.Character, 60);
        var oldSharing = AppConfiguration.Instance.World.TagShareEnabled;
        QuestBehavior.ClearLeashStaleMemory();
        try
        {
            AppConfiguration.Instance.World.TagShareEnabled = sharedHuntCredit;
            if (alternativeState == "exhausted")
            {
                TravelLegDispatch.EnsureJourney(actor, far, new Vector3(18, 0, 0), 3, 2,
                    TravelLegDispatch.PursuitMoveOwner);
                TravelIntentStore.SetPursuitPurpose(actor.ActorId, 266);
                TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                    TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner);
                await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, far, 266)).IsTrue();
            }
            else if (alternativeState == "stale")
            {
                for (var i = 0; i < QuestBehavior.CombatStallWakes; i++)
                    QuestBehavior.CountLeashStaleWake(actor.ActorId, far, 100, $"hunt-claim-stale-{i}", out _);
                await Assert.That(QuestBehavior.IsLeashStaleTarget(actor.ActorId, far)).IsTrue();
            }
            var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "hunt-native-credit-fallback", row);
            await Assert.That(chosen.SelectedObjId).IsEqualTo(near);
            await Assert.That(chosen.LegalCount).IsEqualTo(far == 0 ? 1 : 2);
        }
        finally
        {
            AppConfiguration.Instance.World.TagShareEnabled = oldSharing;
            QuestBehavior.ClearLeashStaleMemory();
            TravelIntentStore.Disarm(actor.ActorId, TravelLegDispatch.PursuitMoveOwner);
            actor.Stop();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrowdedGather_KeepsAcceptedQuest_AndAcceptsOtherEligibleWork(bool blockedCorpse)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("crowded-next-goal");
        var (owner, _) = GameplayActorTestRig.CreateActor("crowded-native-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var prey = SpawnBoar(session, actor, Vector3.Zero, new Vector3(3, 0, 0));
        ActivateGatherGoal(actor.Character);
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        QuestLegActor.SetResolver(null);
        QuestLegActor.Stage(actor.Character.Id, actor);
        try
        {
            var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
            var arbiter = new BotGoalArbiter([module]);
            var bot = new PlayerBotRuntime(actor.Character, "crowded-next-goal");
            _ = arbiter.Arbitrate(bot, 12f);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);

            // Native foreign claim is the supplied world change. The next offer
            // is a supplied opportunity, not an externally assigned bot goal.
            session.World.GetNpc(prey)!.CharacterTagging.AddTagger(owner.Character, 60);
            if (blockedCorpse)
            {
                ActivateGatherGoal(owner.Character);
                MakeBlockedCorpse(session.World.GetNpc(prey)!, owner.Character);
            }
            var crowded = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == Quest251);
            await Assert.That(crowded.PreySourceContended).IsTrue().Because(
                $"work={crowded}; claim={QuestObjectiveTargetSelector.HasCompetingClaim(actor.Character, session.World.GetNpc(prey)!)}; doodads={string.Join(',', Fixture().GatherDoodadTemplates)}");
            GameplayActorTestRig.SeedQuestTalkObjective(93_211, 93_212, 93_213, 93_214, 93_215, level: 1);
            var giver = session.SpawnNpc(93_214);
            GameplayActorTestRig.SetNpcPosition(session, giver, new Vector3(2, 0, 0));
            var npc = session.World.GetNpc(giver)!;
            actor.Character.Region.AddObject(npc);
            npc.Region = actor.Character.Region;
            QuestFixtureRow.InvalidateAll();
            _ = arbiter.Arbitrate(bot, 12f);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(93_211u);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var executor = new BotRoamStepExecutor
            {
                ActorFactory = _ => actor, TimeProvider = clock,
                ActiveActivityProvider = arbiter.GetActiveActivity,
                QuestChosenProvider = module.GetChosenQuest,
                QuestTerminalSink = module.NotifyQuestTerminal,
                QuestGiveUpSink = module.NotifyQuestGiveUp,
                LegOutcomeSink = arbiter.ReportLegOutcome,
            };
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AcceptQuest
                && r.TargetId == 93_211u && r.Result == ActorLifecycleState.Completed)).IsTrue();
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(93_211u)).IsTrue();
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(Quest251)).IsTrue();
            await Assert.That(actor.Character.Quests.HasQuestCompleted(Quest251)).IsFalse();
            await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Objectives[0]).IsEqualTo(0);
            // A new unclaimed boar must not steal the new accepted purpose.
            session.World.GetNpc(prey)!.CharacterTagging.ClearAllTaggers();
            _ = arbiter.Arbitrate(bot, 12f);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(93_211u);
        }
        finally
        {
            actor.PreemptCurrent("crowded next-goal teardown");
            QuestLegActor.ClearAll();
            QuestLegActor.SetResolver(null);
        }
    }

    [Test]
    public async Task CrowdedGather_WithNoOtherOffer_SeeksWork_AndReturnsWhenClaimChanges()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("crowded-search-return");
        var (owner, _) = GameplayActorTestRig.CreateActor("crowded-search-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var prey = SpawnBoar(session, actor, Vector3.Zero, new Vector3(3, 0, 0));
        ActivateGatherGoal(actor.Character);
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        QuestLegActor.SetResolver(null);
        QuestLegActor.Stage(actor.Character.Id, actor);
        try
        {
            var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
            var bot = new PlayerBotRuntime(actor.Character, "crowded-search-return");
            var context = new BotActivityContext { Bot = bot, GameHour = 12f };
            await Assert.That(module.CanActivate(context).CanActivate).IsTrue();
            session.World.GetNpc(prey)!.CharacterTagging.AddTagger(owner.Character, 60);
            var crowded = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == Quest251);
            await Assert.That(crowded.PreySourceContended).IsTrue().Because(
                $"work={crowded}; claim={QuestObjectiveTargetSelector.HasCompetingClaim(actor.Character, session.World.GetNpc(prey)!)}; doodads={string.Join(',', Fixture().GatherDoodadTemplates)}");
            var waiting = module.CanActivate(context);
            await Assert.That(waiting.CanActivate).IsTrue();
            await Assert.That(waiting.ActivityName).IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(Quest251)).IsTrue();
            session.World.GetNpc(prey)!.CharacterTagging.ClearAllTaggers();
            await Assert.That(module.CanActivate(context).ActivityName).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);
        }
        finally
        {
            QuestLegActor.ClearAll();
            QuestLegActor.SetResolver(null);
        }
    }

    // --------------------------------------------------------------- fixture

    [Test]
    [Arguments("own-tag")]
    [Arguments("unclaimed-alternative")]
    [Arguments("real-party")]
    [Arguments("self-defense")]
    [Arguments("missing-prey")]
    public async Task GatherChoice_PreservesUsefulAndUnknownOpportunities(string control)
    {
        var (actor, owner, session, prey, module, bot, context) = CrowdedGoalRig("gather-control-" + control);
        try
        {
            await Assert.That(module.CanActivate(context).CanActivate).IsTrue();
            if (control == "real-party")
            {
                GameplayActorTestRig.ForceSeedTeamManager();
                await Assert.That(actor.PartyInvite(owner.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
                await Assert.That(owner.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
            }
            prey.CharacterTagging.AddTagger(control == "own-tag" ? actor.Character : owner.Character, 60);
            if (control == "self-defense") prey.CurrentAggroTarget = actor.Character;
            if (control == "unclaimed-alternative") _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
            if (control == "missing-prey")
                GameplayActorTestRig.SetNpcPosition(session, prey.ObjId, new Vector3(500, 0, 0));
            var work = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == Quest251);
            await Assert.That(work.PreySourceContended).IsFalse();
            await Assert.That(module.CanActivate(context).ActivityName).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(Quest251)).IsFalse();
        }
        finally { EndCrowdedGoal(actor); }
    }

    [Test]
    [Arguments("move")]
    [Arguments("auto-attack")]
    [Arguments("item-cast")]
    public async Task CrowdedGather_WaitsForNativeActionBoundary(string action)
    {
        var (actor, owner, session, prey, module, bot, context) = CrowdedGoalRig("gather-busy-" + action);
        var itemSkill = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestItemUseSkillId);
        var oldCastingTime = itemSkill.CastingTime;
        try
        {
            await Assert.That(module.CanActivate(context).CanActivate).IsTrue();
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            ActorRequest request;
            if (action == "auto-attack")
            {
                request = actor.AutoAttack(prey.ObjId);
                await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
                await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
            }
            else if (action == "item-cast")
            {
                GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 1);
                itemSkill.CastingTime = 5000;
                request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
                await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            }
            else
            {
                request = actor.MoveTo(new Vector3(100, 0, 0));
                await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            }
            var before = actor.AuditTrace.Count;
            var nativeAttack = actor.Character.AutoAttackTask;
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);
            if (action == "auto-attack")
                await Assert.That(actor.Character.AutoAttackTask).IsSameReferenceAs(nativeAttack);
            else
            {
                await Assert.That(actor.ActiveRequest).IsSameReferenceAs(request);
                await Assert.That(actor.AuditTrace.Count).IsEqualTo(before);
            }
            if (action == "auto-attack")
            {
                await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
                actor.StopAutoAttack();
            }
            else actor.PreemptCurrent("supplied safe action boundary");
            await Assert.That(module.CanActivate(context).ActivityName).IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(Quest251)).IsTrue();
        }
        finally
        {
            itemSkill.CastingTime = oldCastingTime;
            actor.StopAutoAttack();
            EndCrowdedGoal(actor);
        }
    }

    [Test]
    [Arguments("unknown")]
    [Arguments("own")]
    [Arguments("foreign")]
    public async Task GatherChoice_ResolvesCorpsePermissionBeforeDeferring(string permission)
    {
        var (actor, owner, session, prey, module, bot, context) = CrowdedGoalRig("gather-corpse-" + permission);
        try
        {
            _ = module.CanActivate(context);
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            var corpse = session.World.GetNpc(SpawnExtraBoar(session, new Vector3(5, 0, 0)))!;
            GameplayActorTestRig.SeedItemTemplate(Meat4058);
            corpse.Hp = 0;
            if (permission == "unknown") GameplayActorTestRig.SeedLootContainer(corpse, (Meat4058, 1));
            else
            {
                ActivateGatherGoal(owner.Character);
                var recipient = permission == "own" ? actor.Character : owner.Character;
                corpse.CharacterTagging.AddTagger(recipient, 60);
                corpse.LootingContainer.GenerateLoot(recipient);
            }
            await Assert.That(corpse.LootingContainer.ItemEntryCount).IsGreaterThan(0);
            await Assert.That(corpse.LootingContainer.GetNpcLootPermission(actor.Character))
                .IsEqualTo(permission == "unknown" ? (bool?)null : permission == "own");
            var work = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == Quest251);
            await Assert.That(work.PreySourceContended).IsEqualTo(permission == "foreign");
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(permission == "foreign" ? 0u : Quest251);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(Quest251)).IsFalse();
        }
        finally { EndCrowdedGoal(actor); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrowdedGather_DoesNotHideAnotherObjectiveOrComponent(bool selective)
    {
        var (actor, owner, session, prey, _, _, _) = CrowdedGoalRig("gather-other-objective-" + selective);
        try
        {
            actor.Character.Quests.ActiveQuests.Clear();
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            // Supplied two-objective quest. The crowded gather is evaluated
            // first, but the ordinary talk objective is useful now.
            var questId = selective ? 93_231u : 93_221u;
            GameplayActorTestRig.SeedQuestTalkObjective(questId, questId + 1, questId + 2, questId + 3, questId + 4, level: 1);
            var template = QuestManager.Instance.GetTemplate(questId);
            template.Selective = selective;
            var component = template.GetComponents(QuestComponentKind.Progress).Single();
            if (selective)
            {
                component = new QuestComponentTemplate(template) { Id = questId - 1, KindId = QuestComponentKind.Progress };
                template.Components[component.Id] = component;
                var components = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
                    .GetField("_componentTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(QuestManager.Instance)!;
                components[component.Id] = component;
            }
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            { ActId = 1, DetailId = questId + 5, Count = 3, ItemId = Meat4058, ThisComponentObjectiveIndex = 1 });
            QuestFixtureRow.InvalidateAll();
            actor.Character.Quests.ActiveQuests[questId] = new Quest(template, actor.Character)
            { Status = QuestStatus.Progress, Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] };
            _ = SpawnNpcAt(session, questId + 4, new Vector3(4, 0, 0));
            var work = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == questId);
            await Assert.That(work.Leg).IsEqualTo("talk");
            await Assert.That(work.PreySourceContended).IsFalse();
            var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
            var bot = new PlayerBotRuntime(actor.Character, "other-objective");
            await Assert.That(module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12 }).CanActivate).IsTrue();
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(questId);
            var plan = QuestDirector.PlanWake(BotObservedContext.Capture(actor), questId, actor.Character).Single();
            await Assert.That(plan.Fixture!.TalkActId).IsNotEqualTo(0u)
                .Because("the useful talk objective must actually execute, not just appear in the opportunity summary");
            var liveQuest = actor.Character.Quests.ActiveQuests[questId];
            for (var wake = 0; wake < 3 && !actor.AuditTrace.Any(r => r.Action == ActorActionType.Talk); wake++)
                QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"mixed-talk-{selective}-{wake}" });
            await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Talk
                && r.Result == ActorLifecycleState.Completed)).IsTrue();
            var talk = template.GetComponents(QuestComponentKind.Progress).SelectMany(c => c.ActTemplates)
                .OfType<QuestActObjTalk>().Single();
            // Native completion may remove/reset the active objective record.
            await Assert.That(talk.GetObjective(liveQuest) > 0
                || actor.Character.Quests.HasQuestCompleted(questId)).IsTrue();
        }
        finally { EndCrowdedGoal(actor); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrowdedHunt_UsesNativeCreditRule_AndWaitsForUsefulClaimChange(bool sharedHuntCredit)
    {
        var (actor, owner, session, _, module, bot, context) = CrowdedGoalRig("hunt-goal-" + sharedHuntCredit);
        var previous = AppConfiguration.Instance.World.TagShareEnabled;
        try
        {
            AppConfiguration.Instance.World.TagShareEnabled = sharedHuntCredit;
            actor.Character.Quests.ActiveQuests.Clear();
            actor.Character.Quests.ActiveQuests[266] = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
            { Status = QuestStatus.Progress, Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] };
            var prey = session.World.GetNpc(SpawnNpcAt(session, 3494, new Vector3(5, 0, 0)))!;
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            var decision = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(sharedHuntCredit ? 266u : 0u);
            await Assert.That(decision.ActivityName).IsEqualTo(sharedHuntCredit
                ? QuestBootstrapActivityModule.ActivityName : QuestBootstrapActivityModule.DiscoveryActivityName);
            // The same attackable foreign prey is not a changed opportunity.
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(sharedHuntCredit ? 266u : 0u);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(266)).IsTrue();
            await Assert.That(actor.Character.Quests.ActiveQuests[266].Objectives.All(v => v == 0)).IsTrue();
            prey.CharacterTagging.ClearAllTaggers();
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
        }
        finally
        {
            AppConfiguration.Instance.World.TagShareEnabled = previous;
            EndCrowdedGoal(actor);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrowdedHunt_PreservesSuppliedDeliveryOnlyWhenItemExists(bool hasItem)
    {
        var (actor, owner, session, _, module, bot, context) = CrowdedGoalRig("hunt-delivery-" + hasItem);
        var previous = AppConfiguration.Instance.World.TagShareEnabled;
        try
        {
            AppConfiguration.Instance.World.TagShareEnabled = false;
            actor.Character.Quests.ActiveQuests.Clear();
            var questId = hasItem ? 93_501u : 93_511u;
            GameplayActorTestRig.SeedQuestTalkObjective(questId, questId + 1, questId + 2, questId + 3, questId + 4, level: 1);
            var template = QuestManager.Instance.GetTemplate(questId);
            var progress = template.GetComponents(QuestComponentKind.Progress).Single();
            progress.ActTemplates.Clear();
            progress.ActTemplates.Add(new QuestActObjMonsterHunt(progress)
            { ActId = 1, DetailId = questId + 5, Count = 5, NpcId = 3494, ThisComponentObjectiveIndex = 0 });
            progress.ActTemplates.Add(new QuestActObjItemGather(progress)
            { ActId = 2, DetailId = questId + 6, Count = 3, ItemId = Meat4058, ThisComponentObjectiveIndex = 1 });
            var supply = new QuestComponentTemplate(template) { Id = questId + 7, KindId = QuestComponentKind.Supply };
            supply.ActTemplates.Add(new QuestActSupplyItem(supply)
            { ActId = 3, DetailId = questId + 8, Count = 3, ItemId = Meat4058 });
            template.Components[supply.Id] = supply;
            GameplayActorTestRig.SeedItemTemplate(Meat4058);
            if (hasItem) GameplayActorTestRig.StockItem(session, Meat4058, 3);
            QuestFixtureRow.InvalidateAll();
            actor.Character.Quests.ActiveQuests[questId] = new Quest(template, actor.Character)
            { Status = QuestStatus.Progress, Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] };
            var prey = session.World.GetNpc(SpawnNpcAt(session, 3494, new Vector3(5, 0, 0)))!;
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            var work = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == questId);
            await Assert.That(work.PreySourceContended).IsEqualTo(!hasItem);
            await Assert.That(work.UsefulSourcePerceived).IsEqualTo(hasItem);
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(hasItem ? questId : 0u);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(questId)).IsTrue();
        }
        finally
        {
            AppConfiguration.Instance.World.TagShareEnabled = previous;
            EndCrowdedGoal(actor);
        }
    }

    [Test]
    [Arguments("own-tag")]
    [Arguments("unclaimed-alternative")]
    [Arguments("real-party")]
    [Arguments("self-defense")]
    [Arguments("missing-prey")]
    public async Task HuntChoice_PreservesUsefulAndUnknownOpportunities(string control)
    {
        var (actor, owner, session, _, module, bot, context) = CrowdedGoalRig("hunt-control-" + control);
        var previous = AppConfiguration.Instance.World.TagShareEnabled;
        try
        {
            AppConfiguration.Instance.World.TagShareEnabled = false;
            actor.Character.Quests.ActiveQuests.Clear();
            actor.Character.Quests.ActiveQuests[266] = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
            { Status = QuestStatus.Progress, Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] };
            var prey = session.World.GetNpc(SpawnNpcAt(session, 3494, new Vector3(5, 0, 0)))!;
            _ = module.CanActivate(context);
            if (control == "real-party")
            {
                GameplayActorTestRig.ForceSeedTeamManager();
                await Assert.That(actor.PartyInvite(owner.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
                await Assert.That(owner.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
            }
            prey.CharacterTagging.AddTagger(control == "own-tag" ? actor.Character : owner.Character, 60);
            if (control == "self-defense") prey.CurrentAggroTarget = actor.Character;
            if (control == "unclaimed-alternative") _ = SpawnNpcAt(session, 3494, new Vector3(12, 0, 0));
            if (control == "missing-prey")
                GameplayActorTestRig.SetNpcPosition(session, prey.ObjId, new Vector3(500, 0, 0));
            var work = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == 266);
            await Assert.That(work.PreySourceContended).IsFalse();
            if (control != "missing-prey")
            {
                await Assert.That(work.UsefulSourcePerceived).IsTrue();
                _ = module.CanActivate(context);
                await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            }
            else await Assert.That(work.UsefulSourcePerceived).IsFalse();
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(266)).IsTrue();
        }
        finally
        {
            AppConfiguration.Instance.World.TagShareEnabled = previous;
            EndCrowdedGoal(actor);
        }
    }

    [Test]
    public async Task CrowdedHunt_ActuallyAcceptsAnotherEligibleQuest_AndKeepsHuntAccepted()
    {
        var (actor, owner, session, _, module, bot, _) = CrowdedGoalRig("hunt-other-work");
        var previous = AppConfiguration.Instance.World.TagShareEnabled;
        try
        {
            AppConfiguration.Instance.World.TagShareEnabled = false;
            actor.Character.Quests.ActiveQuests.Clear();
            actor.Character.Quests.ActiveQuests[266] = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
            { Status = QuestStatus.Progress, Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] };
            var prey = session.World.GetNpc(SpawnNpcAt(session, 3494, new Vector3(5, 0, 0)))!;
            var arbiter = new BotGoalArbiter([module]);
            _ = arbiter.Arbitrate(bot, 12);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            GameplayActorTestRig.SeedQuestTalkObjective(93_401, 93_402, 93_403, 93_404, 93_405, level: 1);
            _ = SpawnNpcAt(session, 93_404, new Vector3(3, 0, 0));
            QuestFixtureRow.InvalidateAll();
            _ = arbiter.Arbitrate(bot, 12);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(93_401u);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var executor = new BotRoamStepExecutor
            {
                ActorFactory = _ => actor, TimeProvider = clock,
                ActiveActivityProvider = arbiter.GetActiveActivity,
                QuestChosenProvider = module.GetChosenQuest,
                QuestTerminalSink = module.NotifyQuestTerminal,
                QuestGiveUpSink = module.NotifyQuestGiveUp,
                LegOutcomeSink = arbiter.ReportLegOutcome,
            };
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AcceptQuest
                && r.TargetId == 93_401u && r.Result == ActorLifecycleState.Completed)).IsTrue();
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(266)).IsTrue();
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(93_401)).IsTrue();
            prey.CharacterTagging.ClearAllTaggers();
            _ = arbiter.Arbitrate(bot, 12);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(93_401u);
        }
        finally
        {
            AppConfiguration.Instance.World.TagShareEnabled = previous;
            EndCrowdedGoal(actor);
        }
    }

    [Test]
    [Arguments("foreign-blocked")]
    [Arguments("own")]
    [Arguments("unknown-claim")]
    [Arguments("unknown-spawner")]
    [Arguments("unblocked-spawner")]
    [Arguments("living-alternative")]
    public async Task DeadGatherSource_UsesActualLootPermissionAndSpawnState(string condition)
    {
        var (actor, owner, session, prey, module, bot, context) = CrowdedGoalRig("dead-source-" + condition);
        try
        {
            _ = module.CanActivate(context);
            ActivateGatherGoal(owner.Character);
            var recipient = condition == "own" ? actor.Character : owner.Character;
            MakeBlockedCorpse(prey, recipient, condition != "unknown-claim");
            if (condition == "unknown-spawner") prey.Spawner = null;
            if (condition == "unblocked-spawner") prey.Spawner.SpawnedNpcs.Clear();
            if (condition == "living-alternative") _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
            var permission = prey.LootingContainer.GetNpcLootPermission(actor.Character);
            await Assert.That(permission).IsEqualTo(condition == "unknown-claim" ? (bool?)null : condition == "own");
            var contended = condition == "foreign-blocked";
            var work = BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == Quest251);
            await Assert.That(work.PreySourceContended).IsEqualTo(contended);
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(contended ? 0u : Quest251);
            // Re-observing the same foreign corpse does not restore a useful source.
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(contended ? 0u : Quest251);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(Quest251)).IsTrue();
            await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Objectives.All(x => x == 0)).IsTrue();
            if (contended)
            {
                _ = SpawnExtraBoar(session, new Vector3(12, 0, 0));
                _ = module.CanActivate(context);
                await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);
            }
        }
        finally { EndCrowdedGoal(actor); }
    }

    private static void MakeBlockedCorpse(Npc prey, Character recipient, bool generateClaim = true)
    {
        // Supplied death/spawn membership for observation controls; this is
        // not a proof of earning the kill. The ordinary container resolves rights.
        GameplayActorTestRig.SeedItemTemplate(Meat4058);
        prey.CharacterTagging.AddTagger(recipient, 60);
        prey.Hp = 0;
        if (generateClaim) prey.LootingContainer.GenerateLoot(recipient);
        else GameplayActorTestRig.SeedLootContainer(prey, (Meat4058, 1));
        prey.Spawner = new NpcSpawner { SpawnerId = prey.ObjId, UnitId = prey.TemplateId, ParentWorld = prey.ParentWorld };
        prey.Spawner.SpawnedNpcs[prey.Spawner.SpawnerId] = [prey];
    }

    [Test]
    public async Task Discovery_AlreadyRunningToOtherWork_DoesNotTurnBackForReleasedPrey()
    {
        var (actor, owner, _, prey, module, bot, context) = CrowdedGoalRig("gather-discovery-journey");
        try
        {
            _ = module.CanActivate(context);
            prey.CharacterTagging.AddTagger(owner.Character, 60);
            await Assert.That(module.CanActivate(context).ActivityName).IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
            actor.SetPendingMoveOwner(BotRoamStepExecutor.QuestTravelMoveOwner);
            var journey = actor.MoveTo(new Vector3(100, 0, 0));
            await Assert.That(journey.IsTerminal).IsFalse();
            prey.CharacterTagging.ClearAllTaggers();
            var before = actor.AuditTrace.Count;
            await Assert.That(module.CanActivate(context).ActivityName).IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(journey);
            await Assert.That(actor.AuditTrace.Count).IsEqualTo(before);
            actor.PreemptCurrent("supplied end of discovery journey");
            await Assert.That(module.CanActivate(context).ActivityName).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);
        }
        finally { EndCrowdedGoal(actor); }
    }

    [Test]
    [Arguments("new-choice-unknown-source")]
    [Arguments("new-choice-useful-source")]
    [Arguments("retained-journey")]
    [Arguments("unknown-without-offer")]
    public async Task NewGoalChoice_PrefersNativeNearbyOfferOverUnobservedSource(string condition)
    {
        var (actor, _, session, prey, module, bot, context) = CrowdedGoalRig("nearby-offer-" + condition);
        try
        {
            const uint offeredQuest = 94_761;
            if (condition != "new-choice-useful-source")
                GameplayActorTestRig.SetNpcPosition(session, prey.ObjId, new Vector3(500, 0, 0));
            if (condition == "retained-journey")
            {
                _ = module.CanActivate(context);
                await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(Quest251);
                actor.SetPendingMoveOwner(BotRoamStepExecutor.QuestTravelMoveOwner);
                await Assert.That(actor.MoveTo(new Vector3(100, 0, 0)).IsTerminal).IsFalse();
            }
            if (condition != "unknown-without-offer")
            {
                GameplayActorTestRig.SeedQuestTalkObjective(offeredQuest, 94_762, 94_763, 94_764, 94_765, level: 1);
                _ = SpawnNpcAt(session, 94_764, new Vector3(3, 0, 0));
                QuestFixtureRow.InvalidateAll();
            }
            var listing = BotOpportunityListing.List(actor);
            var work = listing.ActiveWork.Single(w => w.QuestId == Quest251);
            await Assert.That(work.UsefulSourcePerceived).IsEqualTo(condition == "new-choice-useful-source");
            if (condition is "new-choice-unknown-source" or "new-choice-useful-source")
                await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == offeredQuest)).IsTrue();
            var journey = actor.ActiveRequest;
            var arbiter = new BotGoalArbiter([module]);
            _ = arbiter.Arbitrate(bot, 12);
            var expected = condition == "new-choice-unknown-source" ? offeredQuest : Quest251;
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(expected);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(Quest251)).IsTrue();
            if (condition == "retained-journey")
            {
                await Assert.That(actor.ActiveRequest).IsSameReferenceAs(journey);
                await Assert.That(actor.ActiveRequest!.IsTerminal).IsFalse();
            }
            if (expected == offeredQuest)
            {
                var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
                var executor = new BotRoamStepExecutor
                {
                    ActorFactory = _ => actor, TimeProvider = clock,
                    ActiveActivityProvider = arbiter.GetActiveActivity,
                    QuestChosenProvider = module.GetChosenQuest,
                    QuestTerminalSink = module.NotifyQuestTerminal,
                    QuestGiveUpSink = module.NotifyQuestGiveUp,
                    LegOutcomeSink = arbiter.ReportLegOutcome,
                };
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await executor.StepAsync(bot, CancellationToken.None);
                await Assert.That(actor.AuditTrace.Any(a => a.Action == ActorActionType.AcceptQuest
                    && a.TargetId == offeredQuest && a.Result == ActorLifecycleState.Completed)).IsTrue();
                await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(offeredQuest)).IsTrue();
                await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Objectives.All(n => n == 0)).IsTrue();
            }
        }
        finally { EndCrowdedGoal(actor); }
    }

    private static (GameplayActor Actor, GameplayActor Owner, HeadlessSession Session, Npc Prey,
        QuestBootstrapActivityModule Module, PlayerBotRuntime Bot, BotActivityContext Context) CrowdedGoalRig(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        var (owner, _) = GameplayActorTestRig.CreateActor(name + "-owner");
        JoinSiblingToRegion(session, owner, new Vector3(2, 0, 0));
        var prey = session.World.GetNpc(SpawnBoar(session, actor, Vector3.Zero, new Vector3(3, 0, 0)))!;
        ActivateGatherGoal(actor.Character);
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        QuestLegActor.SetResolver(null);
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, name);
        return (actor, owner, session, prey, module, bot, new BotActivityContext { Bot = bot, GameHour = 12 });
    }

    private static void EndCrowdedGoal(GameplayActor actor)
    {
        actor.PreemptCurrent("crowded goal test teardown");
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    private static void ActivateGatherGoal(Character character)
    {
        // Goal interpretation requires native quest steps, unlike the older
        // snapshot-only selector fixtures which deliberately stage ids only.
        character.Quests.ActiveQuests[Quest251] = new Quest(QuestManager.Instance.GetTemplate(Quest251), character)
        {
            Status = QuestStatus.Progress, Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
    }

    private static QuestFixtureRow Fixture()
        => QuestFixtureRow.FromQuestData(Quest251);

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

    /// <summary>
    /// Joins a second actor's character to the same world AND region as the
    /// first, at a position INSIDE the 25 m census band.
    ///
    /// Both halves matter. <c>JoinActorWorld</c> alone only moves the character
    /// into the world registry; <c>Observe</c>'s <c>GetAround</c> walks the
    /// REGION graph off <c>obj.Region</c>, so a character left out of it is
    /// invisible to the census. And a sibling parked at its own spawn point sits
    /// outside the 25 m band, so it is invisible too — the test would then pass
    /// for the wrong reason (a lone actor, not a deconflicted pair).
    /// </summary>
    private static void JoinSiblingToRegion(HeadlessSession session, GameplayActor sibling, Vector3 position)
    {
        GameplayActorTestRig.SetPosition(sibling, position);
        GameplayActorTestRig.JoinActorWorld(session, sibling);
        var character = sibling.Character;
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the sibling position");
        region.AddObject(character);
        character.Region = region;
    }

    /// <summary>
    /// A second live 3475 — <c>HeadlessSession.SpawnNpc</c> dedups by template,
    /// so the extra candidate is built directly (the same pattern the G6 funnel
    /// fixture uses).
    /// </summary>
    private static uint SpawnExtraBoar(HeadlessSession session, Vector3 npcPos)
        => SpawnNpcAt(session, Boar3475, npcPos);

    /// <summary>A live NON-prey hostile, so a sibling can be committed to something that is not quest prey.</summary>
    private static uint SpawnExtraOther(HeadlessSession session, Vector3 npcPos)
        => SpawnNpcAt(session, 9001, npcPos);

    private static uint SpawnNpcAt(HeadlessSession session, uint templateId, Vector3 npcPos)
    {
        var objId = s_nextBoarObjId++;
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = templateId,
            Template = new NpcTemplate { Id = templateId, Scale = 1f },
            Hp = 100,
            MaxHp = 100,
            IsVisible = true,
        };
        npc.Transform.Local.SetPosition(npcPos);
        session.World.AddObject(npc);
        var region = session.World.GetRegionByPos(npcPos);
        if (region != null)
        {
            region.AddObject(npc);
            npc.Region = region;
        }
        return objId;
    }

    /// <summary>
    /// Additive, missing-only static surface for the 251 funnel: the 3475→4530
    /// →4058 loot link and the quest-251 Progress gather act for item 4058.
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

        QuestFixtureRow.InvalidateAll();
    }
}
