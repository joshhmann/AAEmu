using AAEmu.Game.Core.Managers.Bots;
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
    [Arguments("own-tag")]
    [Arguments("committed")]
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
        if (control == "all-contested") session.World.GetNpc(far)!.CharacterTagging.AddTagger(owner.Character, 60);
        if (control == "committed") actor.SetTarget(near);
        if (control == "self-defense") npc.CurrentAggroTarget = actor.Character;
        var running = actor.MoveTo(new Vector3(100, 0, 0));
        var chosen = QuestObjectiveTargetSelector.Evaluate(actor.Character, BotObservedContext.From(actor.Observe()), "claim-control", Fixture());
        await Assert.That(chosen.SelectedObjId).IsEqualTo(near);
        await Assert.That(chosen.LegalCount).IsEqualTo(2);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(running);
        await Assert.That(running.State).IsEqualTo(ActorLifecycleState.Running);
        actor.Stop();
    }

    // --------------------------------------------------------------- fixture

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