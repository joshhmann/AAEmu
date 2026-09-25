using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Stage 3 fixture gate: <c>QuestDirector.Plan</c> checks every verb a pattern
/// needs against the verified-verb registry BEFORE it builds a leg, so a plan
/// that needs an unproven verb (or an unrecognized act, or an unresolvable
/// source) fails closed with a named <c>HARNESS/UNPROVEN-*</c> reason and ZERO
/// legs — nothing to perceive, nothing to propose, nothing to dispatch.
///
/// The clean case runs the real 251 fixture row off the loaded quest data (the
/// row's prey/pack come through the loot chain, seeded missing-only like the
/// sibling objective-path rigs) against the production registry.
/// </summary>
[NotInParallel]
public class QuestDirectorGateTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedBoarLootLink();
    }

    // ------------------------------------------------------- clean 251 plan

    [Test]
    public async Task Plan251_AllVerbsProven_NoFailure_SevenObjectiveLegs()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(fixture.PreyTemplate).IsEqualTo(Boar3475);
        await Assert.That(fixture.PreyPack).IsEqualTo(Pack4530);

        // Every verb the kill-to-gather pattern needs resolves green in the
        // production table — the precondition the clean plan depends on.
        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.KillX))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.FailStage).IsEqualTo("");
        await Assert.That(plan.FailReason).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.KillX);
        await Assert.That(plan.Legs.Count).IsEqualTo(7);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.TurnIn, QuestLegId.Target, QuestLegId.Pursuit,
            QuestLegId.Combat, QuestLegId.Loot, QuestLegId.Return
        });
    }

    // --------------------------------------------------- forced-OPEN verb

    [Test]
    public async Task Plan_VerbForcedOpen_FailsFixture_UnprovenVerb_ZeroLegs()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        var registry = new StubVerbRegistry().Force("Loot", VerbGateStatus.Open, "G7c", "g7c-loot-report.json");

        var plan = QuestDirector.Plan(Quest251, fixture, registry);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo("HARNESS/UNPROVEN-VERB verb=Loot gate=G7c evidence=g7c-loot-report.json");
        await AssertNoLegs(plan);
    }

    [Test]
    public async Task Plan_VerbAbsentFromRegistry_FailsFixture_GateAbsent()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        var registry = new StubVerbRegistry().Remove("Loot");

        var plan = QuestDirector.Plan(Quest251, fixture, registry);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo("HARNESS/UNPROVEN-VERB verb=Loot gate=absent evidence=-");
        await AssertNoLegs(plan);
    }

    /// <summary>
    /// A failed plan carries no leg for ANY id, so the wake path has nothing it
    /// could evaluate — the gate precedes leg construction, therefore precedes
    /// the first perception read and every proposal/dispatch.
    /// </summary>
    private static async Task AssertNoLegs(QuestPlan plan)
    {
        foreach (var id in Enum.GetValues<QuestLegId>())
            await Assert.That(plan.Leg(id)).IsNull();
        await Assert.That(plan.Legs.Count).IsEqualTo(0);
    }

    // ------------------------------------------- pattern / source branches

    [Test]
    public async Task Plan_NoRecognizedAct_FailsUnprovenPattern()
    {
        var fixture = new QuestFixtureRow(Quest251, 0, 0, 0, 0, 0, 0, 0);

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason).IsEqualTo("HARNESS/UNPROVEN-PATTERN quest=251 act=absent");
        await AssertNoLegs(plan);
    }

    [Test]
    public async Task Plan_GatherActWithoutResolvableSource_FailsUnprovenSource()
    {
        // A recognized gather act whose item has no loot-chain row: the prey
        // template stays 0 because the source never resolved.
        var fixture = new QuestFixtureRow(Quest251, 10473, Meat4058, 3, 0, 0, 3512, 18791);

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason).IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=251 act=10473 item=4058");
        await AssertNoLegs(plan);
    }

    // -------------------------------------------------- runtime fail-closed

    /// <summary>
    /// The acceptance at the wake path, on the real headless actor and the
    /// production registry: a 251 quest whose objective source does not resolve
    /// (the loot chain carries no row for item 4058) fails the plan gate as
    /// UNPROVEN-SOURCE and dispatches NOTHING — no selected action, no request,
    /// no audit record, quest still Progress. Re-seeding the loot link in the
    /// same test makes the SAME staged quest dispatch, so the gate (not the
    /// stage) is what suppressed it.
    /// </summary>
    [Test]
    public async Task Run_SourceUnresolved_PlanFailsClosed_NoDispatch()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("stage3-fail-closed");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };

        // Stage the unresolvable source: the row still carries its gather act
        // (10473) but the loot chain no longer links 4058 to a prey.
        ClearBoarLootLink();
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(fixture.GatherActId).IsEqualTo(10473u);
        await Assert.That(fixture.PreyTemplate).IsEqualTo(0u);

        var plan = QuestDirector.Plan(Quest251, fixture);
        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason).IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=251 act=10473 item=4058");

        var blocked = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "stage3-blocked-1" });

        await Assert.That(blocked.WorkSelected).IsFalse();
        await Assert.That(blocked.SelectedAction).IsNull();
        await Assert.That(blocked.Request).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.Target or ActorActionType.AutoAttack
            or ActorActionType.Loot or ActorActionType.InteractNpc)).IsFalse();
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Status)
            .IsEqualTo(QuestStatus.Progress);

        // Same stage, source resolved: the quest leg now dispatches, proving
        // the gate was the suppressor.
        SeedBoarLootLink();
        await Assert.That(QuestDirector.Plan(Quest251, QuestFixtureRow.FromQuestData(Quest251)).HasFailed).IsFalse();
        var live = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "stage3-blocked-2" });
        await Assert.That(live.SelectedAction).IsNotNull();
    }

    // ---------------------------------------------------------- row memo

    /// <summary>
    /// The wake path derives the 251 row on EVERY wake, so the derivation must
    /// be memoized: two calls return the SAME row, and that row still carries
    /// the real canonical facts (a cache serving a zero row would satisfy a
    /// reference check while breaking the plan it feeds).
    /// </summary>
    [Test]
    public async Task FromQuestData_SameQuestTwice_ReturnsTheSameMemoizedRow()
    {
        var first = QuestFixtureRow.FromQuestData(Quest251);
        var second = QuestFixtureRow.FromQuestData(Quest251);

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(second.PreyTemplate).IsEqualTo(Boar3475);
        await Assert.That(second.PreyPack).IsEqualTo(Pack4530);
        await Assert.That(second.GatherActId).IsEqualTo(10473u);
        await Assert.That(second.Need).IsEqualTo(3);
    }

    /// <summary>
    /// The memo is cleared ONLY by explicit invalidation, and invalidation
    /// must make the next call re-read the live tables: a staged surface change
    /// is invisible while the row is memoized (that is the point of the cache),
    /// and becomes visible the moment the seam invalidates. Serves the
    /// "never a stale row across a data reload" contract — the reload seams call
    /// exactly this invalidation.
    /// </summary>
    [Test]
    public async Task InvalidateAll_AfterStagedDataChange_DerivesTheNewRow()
    {
        var memoized = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(memoized.PreyTemplate).IsEqualTo(Boar3475);

        // Stage the surface change WITHOUT invalidating: the memo still serves
        // the row derived before the change.
        RemoveBoarLootLinkSilently();
        await Assert.That(QuestFixtureRow.FromQuestData(Quest251)).IsSameReferenceAs(memoized);

        // The explicit invalidation seam drops it; the next call sees the
        // staged data (the 251 source no longer resolves).
        QuestFixtureRow.InvalidateAll();
        var refreshed = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(refreshed).IsNotSameReferenceAs(memoized);
        await Assert.That(refreshed.PreyTemplate).IsEqualTo(0u);
        await Assert.That(refreshed.PreyPack).IsEqualTo(0u);
        await Assert.That(refreshed.GatherActId).IsEqualTo(10473u);

        // Leave the canonical link (and its memo) in place for the next test.
        SeedBoarLootLink();
        QuestFixtureRow.InvalidateAll();
    }

    // ------------------------------------------------------ catalog contract

    [Test]
    public async Task PatternCatalog_KillXCoversTheTenGreenVerbs_UnknownHasNone()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.KillX)).IsEquivalentTo(new[]
        {
            "MoveTo", "Target", "Stop", "Observe", "AcceptQuest",
            "AutoAttack", "Loot", "TurnInQuest", "Talk", "InteractNpc"
        });
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.Unknown).Count).IsEqualTo(0);

        // The return leg's dialogue verb joined the set because G8b proved it —
        // so it must resolve green here like every other listed verb, never
        // fall back to the ungated-permitted list.
        var interactNpc = VerifiedVerbRegistry.Instance.Resolve("InteractNpc");
        await Assert.That(interactNpc.IsGreen).IsTrue();
        await Assert.That(interactNpc.GateId).IsEqualTo("G8b");
        await Assert.That(interactNpc.EvidencePath).IsEqualTo("g8b-return-report.json");

        // The three ungated-by-declaration verbs stay off the gate-checked set.
        foreach (var ungated in new[] { "AdvanceQuest", "TurnInDoodad", "AutoTurnIn" })
            await Assert.That(PatternCatalog.VerbsFor(QuestPattern.KillX)).DoesNotContain(ungated);
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Removes the 3475→4530→4058 loot link in this process so the 251 row's
    /// source cannot resolve (the fail-closed staging for the source branch).
    /// Invalidates the row memo: the row is derived from exactly these two
    /// tables, so a staged change must never be shadowed by a memoized row.
    /// </summary>
    private static void ClearBoarLootLink()
    {
        RemoveBoarLootLinkSilently();
        QuestFixtureRow.InvalidateAll();
    }

    /// <summary>
    /// The raw table edit behind <see cref="ClearBoarLootLink"/>, WITHOUT the
    /// memo invalidation — the memo test needs the silent half to show what the
    /// cache serves before an explicit invalidation.
    /// </summary>
    private static void RemoveBoarLootLinkSilently()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        if (typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!.GetValue(ItemManager.Instance)
            is Dictionary<uint, List<LootPackDroppingNpc>> map)
            map.Remove(Boar3475);
        if (typeof(LootGameData).GetField("_lootPacks", flags)!.GetValue(LootGameData.Instance)
            is Dictionary<uint, LootPack> packs)
            packs.Remove(Pack4530);
    }

    /// <summary>
    /// Additive, missing-only seed of the 3475→4530→4058 loot link the 251
    /// fixture row resolves its prey/pack through (same shape/guards as the
    /// sibling objective-path rigs; canonical pilot data is never clobbered).
    /// </summary>
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

        // Same seam rule as the sibling rigs: the 251 row memo is derived from
        // these two tables, so a (re)staged link must not be shadowed by a row
        // memoized from the prior staging.
        QuestFixtureRow.InvalidateAll();
    }
}
