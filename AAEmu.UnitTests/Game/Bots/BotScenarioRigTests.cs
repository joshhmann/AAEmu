using System.Reflection;
using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// Template rig (P1 t_5efae4f1) — the bot as a parameterized test harness.
///
/// The rig seeds the REAL singleton surface (QuestManager.Load + real
/// unit_reqs from the canonical compact.sqlite3, via the pilot rig), builds
/// fixture headless bots (ordinary Characters, no Connection), and runs the
/// template library through <see cref="BotScenarioRunner"/> — real engine
/// paths end to end: CharacterQuests.AddQuest gates, UnitEvents,
/// QuestManager.DoReportEvents, CharacterSkills, AcquireDefaultItem.
///
/// Test outcomes ARE the template outcomes: a FAIL here is a real finding
/// (engine/data/rig defect) — every failure carries the run's evidence
/// block (§17 taxonomy). The run wrapper writes the evidence table to
/// scorecard-explorations/bot-template-rig.md (deterministic header).
/// </summary>
[NotInParallel]
public class BotScenarioRigTests
{
    #region Helpers

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    /// <summary>Fixture world adapter: spawn-on-demand turn-in targets
    /// (the session world registers them into the unit registry).</summary>
    private sealed class FixtureWorldAdapter : BotScenarioRunner.IScenarioWorldAdapter
    {
        private readonly HeadlessSession _session;

        public FixtureWorldAdapter(HeadlessSession session) => _session = session;

        public uint ResolveNpcObjId(uint npcTemplateId) => _session.SpawnNpc(npcTemplateId);

        public uint ResolveDoodadObjId(uint doodadTemplateId) => _session.SpawnDoodad(doodadTemplateId);
    }

    private static HeadlessSession NewBot(string name, byte level, Race race = Race.Nuian)
        => HeadlessSession.Create((uint)(name.GetHashCode() & 0xFFFF), name, level, race);

    private static BotScenarioRunner.ScenarioRunResult Run(BotScenarioTemplate template, HeadlessSession session)
        => BotScenarioRunner.Run(template, session.Character, new FixtureWorldAdapter(session));

    /// <summary>
    /// Re-seeds the ExperienceManager with a monotonic 10M curve (max 550M
    /// &lt; int.MaxValue — no int32 wrap). The census rig's level*100M curve
    /// WRAPS past level 21 (GetExpForLevel(22+) is negative), and quest
    /// completion grants DEFAULT level-based rewards that land on the active
    /// abilities (AddActiveExp clamps against GetExpForLevel(MaxPlayerLevel))
    /// — with a wrapped curve + saturated exp that overflows negative and
    /// GetAbilityLevel throws post-reward. The template rig needs a curve
    /// where rigged exp + reward stays positive and monotonic. Safe for the
    /// shared process: the census re-seeds its own curve on every tier run.
    /// </summary>
    private static void SeedSafeExperienceCurve()
    {
        var experienceManager = new ExperienceManager();
        var expTemplates = new List<ExperienceLevelTemplate>();
        var expByLevel = new List<int>();
        for (var level = 1; level <= 55; level++)
        {
            expTemplates.Add(new ExperienceLevelTemplate
            {
                Level = (byte)level,
                TotalExp = level * 10_000_000,
                TotalMateExp = level * 10_000_000,
                SkillPoints = 1
            });
            expByLevel.Add(level * 10_000_000);
        }

        SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
        SetField(experienceManager, "_expByLevel", expByLevel);
        SetField(experienceManager, "_mateExpByLevel", expByLevel);
        SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)55);
        SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)50);
        SetSingleton(typeof(Singleton<ExperienceManager>), experienceManager);
    }

    /// <summary>
    /// Registers the template's item ids in the rigged ItemManager so the
    /// normal items path (AcquireDefaultItem, gather inventory reads,
    /// reward distribution) can create/count them — the pilot rig only
    /// registers census-manifest items. Real server: ItemManager is loaded
    /// with real templates; this is rig-only.
    /// </summary>
    private static void RegisterTemplateItems(BotScenarioTemplate template)
    {
        var templatesField = typeof(ItemManager).GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance);
        var templates = (Dictionary<uint, ItemTemplate>)templatesField!.GetValue(ItemManager.Instance)!;

        void Register(uint itemId)
        {
            if (itemId != 0 && !templates.ContainsKey(itemId))
                templates[itemId] = new ItemTemplate { Id = itemId, MaxCount = 100 };
        }

        foreach (var item in template.StartingItems)
            Register(item.ItemId);
        foreach (var stage in template.Drive?.Stages ?? [])
        {
            foreach (var scenarioEvent in stage.Events)
            {
                Register(scenarioEvent.ItemId);
                Register(scenarioEvent.ItemGroupId);
            }
        }

        // M5.1 economy-replay templates carry no quest drive; register the
        // item templates their Deposit/Withdraw events touch.
        foreach (var step in template.EconomyDrive?.Steps ?? [])
        {
            foreach (var scenarioEvent in step.Events)
            {
                Register(scenarioEvent.ItemId);
                Register(scenarioEvent.ItemGroupId);
            }
        }
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName,
                       BindingFlags.NonPublic | BindingFlags.Instance)
                   ?? throw new InvalidOperationException($"Cannot locate field {fieldName} on {instance.GetType().Name}");
        field.SetValue(instance, value);
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    /// <summary>Rigs the full template surface: safe curve + registered
    /// items, then runs the template on a fresh bot. Returns the run result
    /// and the bot's session (for post-run state assertions).</summary>
    private static (BotScenarioRunner.ScenarioRunResult Result, HeadlessSession Session) RunRigged(
        BotScenarioTemplate template, string botName, byte level, Race race = Race.Nuian)
    {
        SeedSafeExperienceCurve();
        RegisterTemplateItems(template);
        var session = NewBot(botName, level, race);
        return (Run(template, session), session);
    }

    /// <summary>Appends one template's evidence to the rig evidence table.
    /// The file is REGENERATED per test process (first call truncates) into
    /// scorecard-explorations/generated/ (gitignored): TUnit's execution
    /// order varies between filtered and full-suite runs, so a committed
    /// evidence file can never stay byte-stable (gate-run churn, t_5a7b187d).
    /// The committed scorecard-explorations/bot-template-rig.md is a curated
    /// snapshot with a pointer to this live output.</summary>
    private static bool s_evidenceInitialized;
    private static readonly object s_evidenceLock = new();

    private static void AppendEvidence(BotScenarioRunner.ScenarioRunResult result)
    {
        lock (s_evidenceLock)
        {
            var path = Path.Combine(RepoRoot(), "scorecard-explorations", "generated", "bot-template-rig.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder();
            if (!s_evidenceInitialized)
            {
                s_evidenceInitialized = true;
                if (File.Exists(path))
                    File.Delete(path);
                sb.AppendLine("# Bot template rig — scenario template evidence (P1 t_5efae4f1)");
                sb.AppendLine();
                sb.AppendLine("> Generated by BotScenarioRigTests (deterministic — no wall-clock).");
                sb.AppendLine("> Section order follows TUnit execution order (varies by run context).");
                sb.AppendLine("> Engine: real QuestManager.Load + real unit_reqs from canonical compact.sqlite3;");
                sb.AppendLine("> bots = ordinary Character records (no Connection); all mutations through normal gameplay services.");
                sb.AppendLine();
            }

            sb.AppendLine("## " + result.Template);
            sb.AppendLine("```");
            sb.AppendLine(result.Evidence());
            sb.AppendLine("```");
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
            Console.WriteLine("template rig evidence appended to " + path);
        }
    }

    #endregion

    #region Template library runs

    /// <summary>
    /// (a) Level-22 quest gating check (quest 168): the engine must refuse
    /// accept below level 22 (gate probe) and admit at 22, then the bot
    /// completes the quest through accept → report → reward.
    /// </summary>
    [Test]
    public async Task Level22Gate_Quest168_RefusedBelow_CompletesAt22()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (result, session) = RunRigged(BotScenarioTemplates.Level22QuestGate, "tpl-l22", 22);
        AppendEvidence(result);

        await Assert.That(result.Passed, "template FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.FailStage, "no fail stage on a pass").IsEmpty();
        // The gate probe must have been exercised and REFUSED by the engine.
        await Assert.That(result.Gates.Count, "expected the level gate probe").IsEqualTo(1);
        await Assert.That(result.Gates[0].Passed, "level gate probe must be engine-refused: " + result.Gates[0].Detail).IsTrue();
        // The drive must have observed the real engine states.
        await Assert.That(result.Stages.Count, "expected START/READY/REWARD stages").IsGreaterThanOrEqualTo(2);
        // The criteria (completed flag + level) must hold.
        await Assert.That(result.Criteria.All(c => c.Passed), "all criteria must pass: " +
            string.Join("; ", result.Criteria.Where(c => !c.Passed).Select(c => c.Detail))).IsTrue();
        await Assert.That(session.Character.Quests.HasQuestCompleted(168)).IsTrue();
    }

    /// <summary>
    /// (b) Ability-prerequisite quest (5531 "ability check test"): the
    /// engine must refuse accept with Fight below 50 (gate probe) and admit
    /// a bot rigged to Fight/Magic/Love 50, then complete the quest.
    /// </summary>
    [Test]
    public async Task AbilityGate_Quest5531_RefusedBelow_CompletesAt50()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (result, session) = RunRigged(BotScenarioTemplates.AbilityPrerequisiteGate, "tpl-abi", 50);
        AppendEvidence(result);

        await Assert.That(result.Passed, "template FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.Gates.Count, "expected the ability gate probe").IsEqualTo(1);
        await Assert.That(result.Gates[0].Passed, "ability gate probe must be engine-refused: " + result.Gates[0].Detail).IsTrue();
        await Assert.That(session.Character.Quests.HasQuestCompleted(5531)).IsTrue();
    }

    /// <summary>
    /// (c) Cat-34 daily cycle (1959, detail Task=6): completes once; the
    /// engine refuses re-accept (REPEATABLE='f' + completed flag); the Task
    /// detail is NOT in the daily-reset family so the flag survives
    /// ResetDailyQuests — the character cycle is the honest loop.
    /// </summary>
    [Test]
    public async Task Cat34Daily_Quest1959_Completes_ReAcceptRefused()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (result, session) = RunRigged(BotScenarioTemplates.Cat34DailyCycle, "tpl-cat34", 10);
        AppendEvidence(result);

        await Assert.That(result.Passed, "template FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.Gates.Count, "expected the prereq gate probe").IsEqualTo(1);
        await Assert.That(result.Gates[0].Passed, "prereq gate probe must be engine-refused: " + result.Gates[0].Detail).IsTrue();
        await Assert.That(session.Character.Quests.HasQuestCompleted(1959)).IsTrue();
        // The ReAcceptRefused criterion ran inside the runner — it must be
        // present and passing (re-accept refused by the engine).
        var reAcceptCriterion = result.Criteria.FirstOrDefault(c => c.Name == "reaccept-refused");
        await Assert.That(reAcceptCriterion, "re-accept criterion must be present").IsNotNull();
        await Assert.That(reAcceptCriterion!.Passed, "re-accept must be engine-refused: " + reAcceptCriterion.Detail).IsTrue();

        // Daily-reset semantics (the Task(6) fix): the completed flag must
        // SURVIVE the reset (detail 6 not in the 7/9/10/11 family), so the
        // re-accept stays refused — the character cycle.
        session.Character.LeaveTime = DateTime.UtcNow.AddDays(-2);
        session.Character.Quests.CheckDailyResetAtLogin();
        await Assert.That(session.Character.Quests.HasQuestCompleted(1959),
            "cat-34 (detail Task=6) completed flag must SURVIVE the daily reset").IsTrue();
    }

    /// <summary>
    /// (d) M5.1 deposit/withdraw economy cycle (t_7c224245): the
    /// deposit-withdraw-cycle template drives money + item through the REAL
    /// engine paths (ChangeMoney — CSDepositMoneyPacket/CSWithdrawMoneyPacket
    /// — and SplitOrMoveItem — CSSwapItemsPacket) on a live bot. Every
    /// economy event must be Completed through the actor contract, and the
    /// acceptance criteria (bank balance + per-container quantities) must
    /// hold. Retries can never double-move: after each successful move the
    /// source container is empty of the template.
    /// </summary>
    [Test]
    public async Task DepositWithdrawCycle_EconomyDrive_Completes_BalancesVerified()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (result, session) = RunRigged(BotScenarioTemplates.DepositWithdrawCycle, "tpl-m51-econ", 1);
        AppendEvidence(result);

        await Assert.That(result.Passed, "template FAILED:\n" + result.Evidence()).IsTrue();
        // All four economy events ran as Completed actor requests.
        await Assert.That(result.Stages.Count, "expected 4 economy steps").IsEqualTo(4);
        await Assert.That(result.Stages.All(s => s.EventsFired == 1), "one event per step").IsTrue();
        await Assert.That(result.ActorRequests, "4 deposit/withdraw requests on the actor trace").IsGreaterThanOrEqualTo(4);
        // Criteria verified bank balance 600 + round-tripped item quantities.
        await Assert.That(result.Criteria.All(c => c.Passed), "all criteria must pass: " +
            string.Join("; ", result.Criteria.Where(c => !c.Passed).Select(c => c.Detail))).IsTrue();
        // Direct state proof on the ordinary character record.
        await Assert.That(session.Character.Money2).IsEqualTo(600);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Inventory, 15589)).IsEqualTo(5);
        await Assert.That(session.Character.Inventory.GetItemsCount(SlotType.Bank, 15589)).IsEqualTo(0);
    }

    /// <summary>
    /// Negative control: a bot below the level gate has NO template gate
    /// check — the drive's own accept must be engine-refused (proves the
    /// path surfaces real gates without the probe machinery).
    /// </summary>
    [Test]
    public async Task LevelGate_NegativeControl_Level21_AcceptRefusedByDrive()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        // Same quest/drive as level22-gate but level 21 and NO gate checks.
        var template = new BotScenarioTemplate
        {
            Name = "level21-negative-control",
            Description = "Negative control: level 21 against the level-22 gate quest (no probe).",
            Level = 21,
            QuestStates = [new QuestStateRig(348, BotQuestPreState.Completed)],
            GateChecks = [],
            Drive = BotScenarioTemplates.Level22QuestGate.Drive,
            Criteria = [new QuestCompletedCriterion("quest-168-completed", 168)]
        };
        var (result, session) = RunRigged(template, "tpl-l21", 21);

        await Assert.That(result.Passed, "level-21 bot must NOT pass the level-22 template").IsFalse();
        await Assert.That(result.FailStage, "failure must surface at ACCEPT").IsEqualTo("ACCEPT");
        await Assert.That(result.Failure, "accept refusal is §17 RejectedAction").IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(session.Character.Quests.HasQuestCompleted(168), "quest must not complete").IsFalse();
    }

    /// <summary>
    /// PB-002 Autonomous Leveling Loop template in the library is registered and can run.
    /// </summary>
    [Test]
    public async Task LevelingLoop_TemplateLibrary_Registered()
    {
        var template = BotScenarioTemplates.Get(LevelingLoopScenario.ScenarioName);
        await Assert.That(template).IsNotNull();
        await Assert.That(template!.Name).IsEqualTo(LevelingLoopScenario.ScenarioName);
        await Assert.That(BotScenarioTemplates.Library.ContainsKey(LevelingLoopScenario.ScenarioName)).IsTrue();
    }

    #endregion

    #region Actor quest actions (contract surface)

    /// <summary>Accept of an unknown quest → Rejected(RejectedAction) with
    /// the gate detail — no crash, no fake state.</summary>
    [Test]
    public async Task ActorAcceptQuest_UnknownQuest_RejectedWithTaxonomy()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = NewBot("actor-unknown", 1);
        var actor = new GameplayActor(session.Character);

        var request = actor.AcceptQuest(9_999_999, QuestAcceptorType.Npc, 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(session.Character.Quests.HasQuest(9_999_999u)).IsFalse();
        // The rejection must be audited (state_changes Requested→Accepted→Rejected).
        var audit = actor.AuditTrace.FirstOrDefault(a => a.Action == ActorActionType.AcceptQuest);
        await Assert.That(audit, "quest accept must emit an audit record").IsNotNull();
    }

    /// <summary>Advance of a quest that is not active → Rejected(StateTransition).</summary>
    [Test]
    public async Task ActorAdvanceQuest_NotActive_RejectedStateTransition()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = NewBot("actor-advance", 1);
        var actor = new GameplayActor(session.Character);

        var request = actor.AdvanceQuest(168);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
    }

    /// <summary>Full quest lifecycle through the actor: accept → advance →
    /// turn-in completes the quest (real engine paths, audit trace filled).</summary>
    [Test]
    public async Task ActorQuestLifecycle_AcceptAdvanceTurnIn_CompletesQuest()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        SeedSafeExperienceCurve();
        var session = NewBot("actor-lifecycle", level: 22);
        var actor = new GameplayActor(session.Character);

        // Rig the kind-31 prereq (engine flag API) like the template does.
        session.Character.Quests.SetCompletedQuestFlag(348, true);

        var accept = actor.AcceptQuest(168, QuestAcceptorType.Npc, 641);
        await Assert.That(accept.State, "accept must complete: " + accept.Detail).IsEqualTo(ActorLifecycleState.Completed);

        // START stage advance (no events) — quest walks to Ready.
        var advance = actor.AdvanceQuest(168);
        await Assert.That(advance.State, "advance must complete: " + advance.Detail).IsEqualTo(ActorLifecycleState.Completed);

        // Turn-in at the report NPC (spawned into the session world). The
        // actor drains the engine's post-report evaluations (report → Ready →
        // Reward → completed+drop) the same way the real evaluation queue
        // does, so ONE turn-in both completes the quest and awards rewards.
        var npcObjId = session.SpawnNpc(639);
        var turnIn = actor.TurnInQuest(168, npcObjId, 0);
        await Assert.That(turnIn.State, "turn-in must complete: " + turnIn.Detail).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(turnIn.Result, "turn-in must report completion: " + turnIn.Detail).IsEqualTo(true);
        await Assert.That(session.Character.Quests.HasQuestCompleted(168)).IsTrue();
        await Assert.That(session.Character.Quests.HasQuest(168), "completed quest must drop from ActiveQuests").IsFalse();

        // A follow-up advance after completion hits the terminal state
        // (quest dropped) — Rejected(StateTransition), never re-executed.
        var afterCompletion = actor.AdvanceQuest(168);
        await Assert.That(afterCompletion.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(afterCompletion.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // Audits: accept + advance + turn-in all recorded Completed; the
        // post-completion advance is recorded as its own Rejected record.
        var questAudits = actor.AuditTrace.Where(a => a.Action is ActorActionType.AcceptQuest or ActorActionType.AdvanceQuest or ActorActionType.TurnInQuest).ToList();
        await Assert.That(questAudits.Count, "expected accept+advance+turn-in+rejected-advance audit records").IsEqualTo(4);
        await Assert.That(questAudits.Count(a => a.Result is ActorLifecycleState.Completed), "expected 3 completed quest audits").IsEqualTo(3);
        await Assert.That(questAudits.Single(a => a.Result is ActorLifecycleState.Rejected).Action).IsEqualTo(ActorActionType.AdvanceQuest);
    }

    #endregion

    #region Seeded-defect regression proof

    /// <summary>
    /// Seeded defect (pilot pattern, quest 168): the report act's NPC is
    /// swapped to a non-existent template. The template must FAIL (the bot
    /// cannot complete the quest through the real report path); reverting
    /// the defect must restore green — fail-before / pass-after at template
    /// level, the regression-harness contract in action.
    /// </summary>
    [Test]
    public async Task SeededDefect_WrongReportNpc_FailBefore_PassAfter()
    {
        PlayerbotPilotRig.SeedPilotSingletons();

        // ---- FAIL-BEFORE: inject the data defect into the loaded template ----
        var template = AAEmu.Game.Core.Managers.QuestManager.Instance.GetTemplate(168);
        var reportAct = template.Components.Values
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActConReportNpc>()
            .First();
        var originalNpcId = reportAct.NpcId;
        reportAct.NpcId = 999_999; // wrong report target (data-defect class)

        var session = NewBot("tpl-seeded-fail", level: 22);
        var failResult = RunRigged(BotScenarioTemplates.Level22QuestGate, "tpl-seeded-fail", 22).Result;
        AppendEvidence(failResult);

        await Assert.That(failResult.Passed, "seeded defect must FAIL the template").IsFalse();
        await Assert.That(failResult.FailStage is "READY" or "REWARD" or "VERIFY",
            "failure must surface at report/completion, got " + failResult.FailStage).IsTrue();
        Console.WriteLine("FAIL-BEFORE evidence:\n" + failResult.Evidence());

        // ---- PASS-AFTER: revert the defect ----
        reportAct.NpcId = originalNpcId;

        var session2 = NewBot("tpl-seeded-pass", level: 22);
        var passResult = RunRigged(BotScenarioTemplates.Level22QuestGate, "tpl-seeded-pass", 22).Result;
        await Assert.That(passResult.Passed,
            "reverted defect must PASS the template: " + passResult.Evidence()).IsTrue();
    }

    #endregion
}
