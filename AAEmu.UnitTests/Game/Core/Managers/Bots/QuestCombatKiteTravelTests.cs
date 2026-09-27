using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
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
using AAEmu.Game.Models.Game.Skills;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// THE KITE'S LIVE DISPATCH CALLER: the quest combat leg's spacing wake
/// (<see cref="QuestBehavior.CombatEmit"/> → <c>DispatchCombatMove</c>) is wired onto
/// <see cref="CombatTravelDispatch"/>, so the sub-critical kite is decided by
/// <see cref="TravelBrain.Decide"/> → <see cref="TravelIntentStore"/> →
/// <see cref="TravelBrainPlanner"/> AND dispatched under the kite's own movement owner.
///
/// Driven end to end through <see cref="QuestDecisionScenario.Run"/> on the REAL
/// headless actor (the engine verbs actually execute): a ranged actor crowded inside its
/// band floor routes its spacing Move through the travel chain (the shared
/// <see cref="TravelBrain.SafeAnchor"/>, the <c>COMBAT_KITE_MOVE</c> owner, the additive
/// <c>:travel=</c> fragment), a live kite leg that still serves the receding anchor is
/// HELD rather than restarted, and the critical DISENGAGE wake neither routes through
/// this seam nor leaves a kite journey behind (Survival owns that wake).
/// </summary>
[NotInParallel]
public class QuestCombatKiteTravelTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
        SeedCombatSurface();
    }

    [After(Test)]
    public void TearDown()
    {
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
    }

    // ------------------------------------------------------------ live wiring

    /// <summary>
    /// THE KITE ROUTES THROUGH THE SEAM, live: a ranged bot crowded inside its band
    /// floor asks the combat brain for spacing (<c>arm=BackOff</c>), and the spacing
    /// leg it dispatches walks the TRAVEL chain's anchor under the kite's own owner —
    /// not the ad-hoc pre-brain Move the frozen G6 lane issued.
    /// </summary>
    [Test]
    public async Task KiteWake_RoutesThroughTheTravelChain_UnderTheKiteOwner()
    {
        var (actor, session, here, threat, boarObjId) = CreateCrowdedRangedActor("kite-route");

        // Wake 1: the pursuit range-hold (24) owns the wake — the combat leg's kite
        // decision is made (and the journey armed) but its Move loses selection.
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-route-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();

        // Wake 2: settled (nobody moved) — the kite Move lands, and it is the TRAVEL
        // chain's leg: the shared anchor, the kite owner, the additive fragment. A
        // 25 m escape is a RUNNING leg (the same shape the pre-brain spacing Move
        // had), so the wake reports the leg, not a terminal completion.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-route-2" });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(second.Request!.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(second.Request.TargetId).IsEqualTo(0u); // a position leg
        await Assert.That(second.Request.MoveOwner).IsEqualTo(CombatTravelDispatch.KiteMoveOwner);

        var anchor = TravelBrain.SafeAnchor(here, threat, TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(second.Request.Destination!.Value).IsEqualTo(anchor);
        await Assert.That(Vector3.Distance(here, anchor)).IsCloseTo(25f, 0.001f);

        // The leg's diag names the brain's own arm AND the travel chain's routing: the
        // retreat arm, the dispatched Move, and the retreat reason, additively.
        var detail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(detail).Contains("brain=arm=BackOff");
        await Assert.That(detail).Contains(":travel=arm=Retreat:verb=MoveTo:mode=Local:terminal=none");
        await Assert.That(detail).Contains(":routed=true:dispatch=move:reason=retreat");

        // The journey is armed FOR THIS THREAT under the kite's owner, and the leg that
        // actually ran was banked (its issued point) — never a decision without a leg.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(armed.TargetObjId).IsEqualTo(boarObjId);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(armed.PriorDestination).IsEqualTo(anchor);
        await Assert.That(armed.RepathCount).IsEqualTo(0);

        // The LIVE leg is attributed to the kite (the audit row lands when the leg
        // terminates, so the in-flight leg is the honest read here); the pre-brain
        // owner tag was never staged for this wake.
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo(CombatTravelDispatch.KiteMoveOwner);
        await Assert.That(actor.AuditTrace.Any(r => r.MoveOwner == "COMBAT_MOVE_TO")).IsFalse();
    }

    /// <summary>
    /// THE RECEDING ANCHOR'S HOLD, live: with the kite leg in flight and nothing moved,
    /// the next kite wake re-reads its own leg as still serving today's anchor and
    /// HOLDs — the escape is never restarted every wake. The travel fragment names the
    /// hold, and no second leg is issued.
    /// </summary>
    [Test]
    public async Task LiveKiteLegOnAStaticAnchor_IsHeldNotRestarted()
    {
        var (actor, _, _, _, _) = CreateCrowdedRangedActor("kite-hold");

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-hold-1" });
        var issued = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-hold-2" });
        await Assert.That(issued.SelectedAction).IsEqualTo(ActorActionType.Move);
        var issuedDest = issued.Request!.Destination!.Value;
        var movesAfterIssue = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);

        // Wake 3: the same anchor this wake would issue, and our leg is already on it.
        var held = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-hold-3" });
        var detail = held.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(detail).Contains("dispatch=held:reason=anchor-held");
        await Assert.That(detail).Contains(":routed=true");
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(movesAfterIssue);
        await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(issuedDest);

        // The journey survives the hold (its progress is kept, not re-armed clean),
        // and the leg that was issued is still the one banked.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(armed.PriorDestination).IsEqualTo(issuedDest);
    }

    /// <summary>
    /// THE OWNERSHIP BOUNDARY, live: at critical hp with the fight still on, the combat
    /// brain's DISENGAGE arm owns the wake (Survival carries its published fact
    /// forward), so this seam must neither route a leg nor keep a kite journey alive —
    /// the journey boundary is reached and the leg dispatches nothing.
    /// </summary>
    [Test]
    public async Task CriticalDisengage_EndsTheKiteJourney_AndRoutesNoLeg()
    {
        var (actor, _, _, _, _) = CreateCrowdedRangedActor("kite-disengage");

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-disengage-1" });
        var kiteWake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-disengage-2" });
        await Assert.That(kiteWake.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        var movesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);

        // Critical hp with the threat still assigned: the disengage arm fires.
        actor.Character.Hp = Math.Max(1, (int)(actor.Character.MaxHp * CombatBrain.FleeHpThreshold * 0.5f));
        var critical = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-disengage-3" });

        var detail = critical.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(detail).Contains("brain=arm=Disengage");
        await Assert.That(detail).Contains("verb=none");
        await Assert.That(critical.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Emitted).IsFalse();
        // No kite leg, and no other spacing leg either: the Disengage wake is not this
        // seam's to dispatch (Survival owns the retreat).
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(movesBefore);
        // The journey boundary was reached: a banked verdict or a spent budget must not
        // outlive the threat identity it was armed for.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsFalse();
    }

    // ------------------------------------------------------------ boundaries

    /// <summary>
    /// THE THREAT-IDENTITY BOUNDARY, live: once the committed threat is a corpse the
    /// kite no longer needs the leg, so the journey it armed is ENDED — a banked verdict
    /// or a spent budget must not outlive the identity it was reached for, and a later
    /// threat must not inherit it.
    /// </summary>
    [Test]
    public async Task ThreatDead_EndsTheKiteJourney()
    {
        var (actor, session, _, _, boarObjId) = CreateCrowdedRangedActor("kite-threat-gone");

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-threat-gone-1" });
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-threat-gone-2" });
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsTrue();

        // The threat dies: the next wake's combat leg withdraws naming the dead target
        // and drops the journey with it.
        session.World.GetNpc(boarObjId)!.Hp = 0;
        var afterDeath = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-threat-gone-3" });

        var detail = afterDeath.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(detail).Contains("verb=none");
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsFalse();

        // A NEW threat arms a CLEAN journey (no inherited verdict, no spent budget).
        var freshObjId = session.SpawnNpc(Boar3475);
        var fresh = session.World.GetNpc(freshObjId)!;
        fresh.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        fresh.Hp = 100;
        fresh.MaxHp = 100;
        fresh.IsVisible = true;
        actor.Character.CurrentTarget = fresh;
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-threat-gone-4" });
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var rearmed, CombatTravelDispatch.KiteMoveOwner)).IsTrue();
        await Assert.That(rearmed.TargetObjId).IsEqualTo(freshObjId);
        await Assert.That(rearmed.Terminal).IsEqualTo(TravelTerminal.None);
    }

    /// <summary>
    /// AN ABANDONED JOURNEY NEVER DISPATCHES A LEG: with the kite's journey settled on a
    /// named terminal (the travel chain's own stickiness), the spacing wake withdraws
    /// NAMING it — never a leg to the abandoned anchor, and never a bare failure.
    /// </summary>
    [Test]
    public async Task SettledJourney_WithdrawsNamingTheTerminal_NoLeg()
    {
        var (actor, _, _, _, _) = CreateCrowdedRangedActor("kite-abandoned");

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-abandoned-1" });
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-abandoned-2" });
        var movesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);

        // The journey reaches its named terminal (the repath budget spends out — the
        // store banks it and every later wake re-reads it).
        TravelIntentStore.BankTerminal(
            actor.ActorId, TravelTerminal.Unreachable, TravelReason.RepathExhausted,
            CombatTravelDispatch.KiteMoveOwner);

        var settled = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "kite-abandoned-3" });

        var evidence = settled.LegEvidence.Single(e => e.Leg == QuestLegId.Combat);
        await Assert.That(evidence.Emitted).IsFalse();
        await Assert.That(evidence.Detail).Contains(":travel=arm=Repath:verb=Hold:mode=Local:terminal=unreachable");
        await Assert.That(evidence.Detail).Contains(":routed=true:dispatch=withdrawn:reason=terminal-unreachable");
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(movesBefore);
    }

    /// <summary>
    /// THE FALLBACK IS THE PRE-BRAIN LEG, byte-identically: a combat Move whose payload
    /// carries NO travel decision (the non-kite arms, and any wake whose journey could
    /// not be armed) dispatches the brain's own destination under the ORIGINAL
    /// <c>COMBAT_MOVE_TO</c> owner and banks no journey — the wiring is purely additive
    /// on that path.
    /// </summary>
    [Test]
    public async Task FallbackMove_KeepsThePreBrainOwner_AndBanksNoJourney()
    {
        var (actor, _, _, _, _) = CreateCrowdedRangedActor("kite-fallback");
        var destination = actor.Character.Transform.World.Position + new Vector3(60f, 0f, 0f);
        var fallback = new BotDecisionProposal(
            goal: "quest.objective-combat",
            action: ActorActionType.Move,
            targetId: 0,
            expectedPostcondition: new BotProposalPostcondition("test fallback leg", _ => true),
            idempotencyKey: "kite-fallback-leg",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "fallback spacing leg",
            policyVersion: "test",
            priority: 23,
            destination: destination,
            payload: new CombatDispatchParams(
                new CombatBrainDecision(
                    CombatArm.CloseRange, CombatVerb.Move, CombatBrainState.Engaged,
                    1, 0, 0, destination, 1f, 3.5f, 12f, 0.8f, 0, CombatDisengageReason.None,
                    CombatReason.CloseIn)));

        var dispatch = typeof(QuestBehavior).GetMethod("Dispatch",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (ActorRequest)dispatch.Invoke(null, [actor, fallback])!;

        await Assert.That(request.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(request.MoveOwner).IsEqualTo("COMBAT_MOVE_TO");
        await Assert.That(request.Destination).IsEqualTo(destination);
        // Nothing was banked for the kite: a fallback carries no travel decision.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, CombatTravelDispatch.KiteMoveOwner)).IsFalse();
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// A RANGED actor (band floor 12 m) with the 251 prey committed and standing 2 m
    /// away, so the combat brain's own band arm asks for spacing (`BackOff`) rather than
    /// a close-in or a sustain. Healthy vitals, so the heal/flee arms stay out of it.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session, Vector3 Here, Vector3 Threat, uint BoarObjId)
        CreateCrowdedRangedActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        // A ranged role: a melee bot inside its floor is exactly where it wants to be,
        // so only the ranged/caster roles ever reach the spacing arm.
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;

        var here = new Vector3(100f, 100f, 10f);
        var boarObjId = SpawnBoar(session, actor, here, here + new Vector3(2f, 0f, 0f));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        return (actor, session, here, here + new Vector3(2f, 0f, 0f), boarObjId);
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
    /// Additive, missing-only static surface for the 251 funnel (the sibling combat rig's
    /// shape): the Progress gather act for item 4058 (objective resolution) and the
    /// 3475→4530→4058 loot link (source resolution).
    /// </summary>
    private static void SeedCombatSurface()
    {
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
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
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
