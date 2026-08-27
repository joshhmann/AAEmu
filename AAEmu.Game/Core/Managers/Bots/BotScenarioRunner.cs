using System.Text;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Scenario runner (P1 t_5efae4f1) — the bot is the living test harness.
///
/// Pipeline: rig from template (normal gameplay surfaces) → negative gate
/// probes (each must be engine-REFUSED) → drive the quest through the
/// IGameplayActor contract (accept → progress → turn-in, all real engine
/// paths) → verify acceptance criteria → emit a structured PASS/FAIL verdict
/// with evidence and a spec §17 failure reason.
///
/// The runner is engine-path-pure: every mutation flows through
/// CharacterQuests.AddQuest / the UnitEvents surface /
/// QuestManager.DoReportEvents / CharacterSkills / AcquireDefaultItem.
/// No bot-only state, no direct DB writes (AGENTS.md #9/#10).
///
/// World target resolution (turn-in NPCs/doodads) goes through
/// <see cref="IScenarioWorldAdapter"/> so the same runner drives fixture
/// worlds (unit rig: spawn-on-demand) and the live world (gate stage:
/// real spawners / provisioned citizens).
/// </summary>
public static class BotScenarioRunner
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>How turn-in targets resolve in the scenario's world.</summary>
    public interface IScenarioWorldAdapter
    {
        /// <summary>Resolves an NPC template id to a live objId (0 = unresolvable).</summary>
        uint ResolveNpcObjId(uint npcTemplateId);

        /// <summary>Resolves a doodad template id to a live objId (0 = unresolvable).</summary>
        uint ResolveDoodadObjId(uint doodadTemplateId);
    }

    #region Verdict model

    /// <summary>One drive-stage verdict (observed engine state after events + advance).</summary>
    public sealed record ScenarioStageVerdict(
        string Stage, int EventsFired, string Advance, string StepObserved, string StatusObserved);

    /// <summary>One gate-check verdict (a refusal probe result).</summary>
    public sealed record GateVerdict(string Name, bool Passed, string Detail);

    /// <summary>One acceptance-criterion verdict.</summary>
    public sealed record CriterionVerdict(string Name, bool Passed, string Detail);

    /// <summary>
    /// The structured run result. FailStage is one of RIG / GATE / ACCEPT /
    /// &lt;drive stage&gt; / VERIFY; Failure is the spec §17 taxonomy reason
    /// (see <see cref="ActorFailureReason"/>), never "bot got stuck".
    /// </summary>
    public sealed class ScenarioRunResult
    {
        public required string Template { get; init; }

        public bool Passed { get; init; }

        public string FailStage { get; init; } = "";

        public ActorFailureReason? Failure { get; init; }

        public string FailReason { get; init; } = "";

        public List<string> RigNotes { get; init; } = [];

        public List<GateVerdict> Gates { get; init; } = [];

        public List<ScenarioStageVerdict> Stages { get; init; } = [];

        public List<CriterionVerdict> Criteria { get; init; } = [];

        /// <summary>
        /// The per-action audit records for this run, in execution order
        /// (M5 trace shape via <see cref="ActorAuditRecord.ToJson"/> — real
        /// server timestamps requested_at/started_at/completed_at). Quest
        /// drives leave this empty; fleet scenarios (ah-conservation) fill
        /// it so the E2E writer can emit trace evidence directly from the
        /// bridge response (evidence hygiene t_6e2725b5: no worker-side
        /// transcription of the deterministic evidence block). Also the
        /// machine-readable replay evidence for scenario-family drives
        /// (AuctionHouseScenario, M1M2ReplayScenario); empty for classic
        /// single-quest templates.
        /// </summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];

        public int ActorRequests { get; init; }

        /// <summary>Human-readable evidence block (deterministic — no wall-clock).</summary>
        public string Evidence()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Scenario: {Template}");
            sb.AppendLine($"Verdict: {(Passed ? "PASS" : "FAIL")}" +
                          (FailStage.Length > 0 ? $" at {FailStage}" : "") +
                          (Failure is { } f ? $" ({f})" : "") +
                          (FailReason.Length > 0 ? $" — {FailReason}" : ""));
            foreach (var note in RigNotes)
                sb.AppendLine($"- rig note: {note}");
            foreach (var g in Gates)
                sb.AppendLine($"- gate [{g.Name}]: {(g.Passed ? "REFUSED (pass)" : "ACCEPTED (gate broken)")} {g.Detail}");
            foreach (var s in Stages)
                sb.AppendLine($"- stage {s.Stage}: {s.EventsFired} events, advance={s.Advance}, step={s.StepObserved}, status={s.StatusObserved}");
            foreach (var c in Criteria)
                sb.AppendLine($"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}");
            sb.AppendLine($"- actor requests: {ActorRequests}");
            return sb.ToString();
        }
    }

    #endregion

    #region Run

    /// <summary>
    /// Runs a template end-to-end on an embodied character (fixture-created
    /// or production-provisioned — the runner does not care how the
    /// character came to exist, only that it is an ordinary Character with a
    /// parent world).
    /// </summary>
    public static ScenarioRunResult Run(BotScenarioTemplate template, Character character, IScenarioWorldAdapter world)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(character);

        // Lane D auction-house conservation scenario (t_52b2b084) — a
        // fleet-driven trade scenario, not a quest drive: dispatch to the
        // auction runner when the template is the auction-house template.
        // The quest machinery below is never entered for it.
        if (template.Name == AuctionHouseScenario.ScenarioName)
            return AuctionHouseScenario.Run(character);

        // BACKTRACK Phase 1 (t_61a0eebb) — the M1/M2 contract replay: the
        // curated golden route driven headless through contract actions
        // only. Dispatched before the single-quest machinery (the replay
        // drives 16 quests and needs the world adapter for real NPC
        // turn-in resolution).
        if (template.Name == M1M2ReplayScenario.ScenarioName)
            return M1M2ReplayScenario.Run(character, world);

        if (template.Name == M1M2ReplayScenario.MinSliceScenarioName)
            return M1M2ReplayScenario.RunMinSlice(character, world);

        // BACKTRACK Phase 2 (t_b4f455b0) — the M3a contract + M4
        // economic/navigation replay: the curated farm → craft → pack →
        // vehicle → trade → bank route driven through the M5.1 + B1
        // contract actions only, with conservation + lifecycle asserts.
        if (template.Name == M3aM4ReplayScenario.ScenarioName)
            return M3aM4ReplayScenario.Run(character, world);

        // M7 gating spike — one adventurer clearing the Solzreed fox-cull
        // kill-quest chain end-to-end (accept → travel → hunt → loot →
        // auto-complete) through the M5 contract actions only.
        if (template.Name == AdventurerSpikeScenario.ScenarioName)
            return AdventurerSpikeScenario.Run(character, world);

        // PB-002 Autonomous Leveling Loop — perception-driven autonomous quest
        // progression chain (accept → pursue → auto-equip → turn-in).
        if (template.Name == LevelingLoopScenario.ScenarioName)
            return LevelingLoopScenario.RunAsScenario(character);

        var rigNotes = new List<string>();
        var actor = new GameplayActor(character);
        var controller = new PlayerBotController(character);

        try
        {
            // ---------------------------------------------------------- 1. RIG
            var rigFailure = ApplyRig(template, character, controller, rigNotes);
            if (rigFailure != null)
                return Fail(template, "RIG", rigFailure.Value.Failure, rigFailure.Value.Reason, rigNotes, actor);

            // --------------------------------------------------- 2. GATE probes
            var gates = new List<GateVerdict>();
            foreach (var gate in template.GateChecks)
            {
                var verdict = ProbeGate(gate, actor);
                gates.Add(verdict);
                if (!verdict.Passed)
                    return Fail(template, "GATE", ActorFailureReason.WrongDecision,
                        $"gate probe '{gate.Name}' was ACCEPTED by the engine — the gate under test is not enforced: {verdict.Detail}",
                        rigNotes, actor, gates);
            }

            // ---------------------------------------------------------- 3. DRIVE
            // A template carries exactly ONE drive: the quest shape (P1) or
            // the M5.1 economy-replay shape. Both fire through the actor
            // contract; the economy shape verifies every request Completed
            // before the next step runs.
            if (template.Drive != null && template.EconomyDrive != null)
                return Fail(template, "RIG", ActorFailureReason.WrongDecision,
                    "template carries both a quest drive and an economy drive — exactly one is allowed",
                    rigNotes, actor, gates);

            var stages = new List<ScenarioStageVerdict>();
            (string Stage, ActorFailureReason Failure, string Reason)? driveFailure = template.Drive != null
                ? Drive(template, character, controller, actor, world, stages)
                : EconomyDrive(template, character, controller, actor, world, stages);
            if (driveFailure != null)
                return Fail(template, driveFailure.Value.Stage, driveFailure.Value.Failure,
                    driveFailure.Value.Reason, rigNotes, actor, gates, stages);

            // ----------------------------------------------------------- 4. VERIFY
            var criteria = new List<CriterionVerdict>();
            foreach (var criterion in template.Criteria)
            {
                var verdict = EvaluateCriterion(criterion, character, controller, actor);
                criteria.Add(verdict);
                if (!verdict.Passed)
                    return Fail(template, "VERIFY", ActorFailureReason.WrongDecision,
                        $"criterion '{criterion.Name}' failed: {verdict.Detail}",
                        rigNotes, actor, gates, stages, criteria);
            }

            return new ScenarioRunResult
            {
                Template = template.Name,
                Passed = true,
                RigNotes = rigNotes,
                Gates = gates,
                Stages = stages,
                Criteria = criteria,
                ActorRequests = actor.AuditTrace.Count
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Scenario '{Template}' crashed", template.Name);
            return Fail(template, "RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", rigNotes, actor);
        }
    }

    #endregion

    #region Rig

    private static (ActorFailureReason Failure, string Reason)? ApplyRig(
        BotScenarioTemplate template, Character character, PlayerBotController controller, List<string> rigNotes)
    {
        // Level (ordinary character record — the field level gates evaluate).
        character.Level = template.Level;

        // Copper balance (ordinary character record — the M5.1 bank
        // actions read/write the same balance the client sees).
        character.Money = template.Money;

        // Ability trees ("class").
        if (template.AbilityTrees.Count > 0)
        {
            character.Ability1 = template.AbilityTrees.Count > 0 ? template.AbilityTrees[0] : AbilityType.None;
            character.Ability2 = template.AbilityTrees.Count > 1 ? template.AbilityTrees[1] : AbilityType.None;
            character.Ability3 = template.AbilityTrees.Count > 2 ? template.AbilityTrees[2] : AbilityType.None;
        }

        // Ability exp rig — direct exp set on the ordinary ability record
        // (same surface the t10 census rig drives). Saturation strategy:
        // use the engine's own curve for the exact level when it is sane
        // (real server data, or a monotonic rig curve); fall back to
        // int.MaxValue (falls off the binary search's top end → max level
        // ≥ any requirement) when the curve wraps negative (the census rig's
        // level*100M curve wraps past level 21 — GetExpForLevel is garbage
        // there). int.MaxValue alone is NOT safe as the primary value:
        // quest-completion default rewards land on the ACTIVE abilities
        // (AddActiveExp clamps against GetExpForLevel(MaxPlayerLevel)), so
        // a wrapped curve + near-max exp can overflow negative and make
        // GetAbilityLevel throw (ArgumentOutOfRangeException) post-reward.
        foreach (var (ability, minLevel) in template.AbilityLevels)
        {
            if (!character.Abilities.Abilities.TryGetValue(ability, out var abilityRecord))
            {
                rigNotes.Add($"ability {ability} not seeded on character — skipped (BUG-012 guard)");
                continue;
            }

            if (character.Abilities.GetAbilityLevel(ability) >= minLevel)
                continue;

            var targetExp = ExperienceManager.Instance.GetExpForLevel(minLevel);
            abilityRecord.Exp = targetExp > 0
                ? Math.Max(abilityRecord.Exp, targetExp)
                : int.MaxValue;
        }

        // Skills — normal learn path.
        foreach (var skillId in template.Skills)
        {
            try
            {
                character.Skills.AddSkill(skillId);
            }
            catch (Exception ex)
            {
                rigNotes.Add($"skill {skillId} learn refused: {ex.Message}");
            }
        }

        // Starting items — normal items path.
        foreach (var item in template.StartingItems)
            controller.StockInventory(item.ItemId, item.Count, item.Grade);

        // Quest state — engine surfaces only.
        foreach (var state in template.QuestStates)
        {
            switch (state.State)
            {
                case BotQuestPreState.Completed:
                    character.Quests.SetCompletedQuestFlag(state.QuestId, true);
                    break;
                case BotQuestPreState.Accepted:
                    if (!character.Quests.AddQuest(state.QuestId, false, QuestAcceptorType.Npc, 0))
                        return (ActorFailureReason.RejectedAction,
                            $"pre-accepted quest {state.QuestId} refused by engine gate");
                    break;
                case BotQuestPreState.Ready:
                    if (!character.Quests.AddQuest(state.QuestId, false, QuestAcceptorType.Npc, 0))
                        return (ActorFailureReason.RejectedAction,
                            $"pre-ready quest {state.QuestId} refused by engine gate");
                    var quest = character.Quests.ActiveQuests.GetValueOrDefault(state.QuestId);
                    var advances = 0;
                    while (quest is { Status: not QuestStatus.Ready } && advances++ < 8)
                        _ = quest.RunCurrentStep();
                    if (quest?.Status != QuestStatus.Ready)
                        rigNotes.Add($"pre-ready quest {state.QuestId} did not reach Ready after {advances} advances (step {quest?.Step}, status {quest?.Status})");
                    break;
            }
        }

        // Position (zone + world placement).
        if (template.Position is { } position)
        {
            character.Transform.Local.SetPosition(position);
            if (template.ZoneId != 0)
                character.Transform.ZoneId = template.ZoneId;
        }
        else if (template.ZoneId != 0)
        {
            character.Transform.ZoneId = template.ZoneId;
        }

        return null;
    }

    #endregion

    #region Gate probes

    private static GateVerdict ProbeGate(ScenarioGateCheck gate, GameplayActor actor)
    {
        try
        {
            switch (gate)
            {
                case LevelGateCheck levelGate:
                {
                    var originalLevel = actor.Character.Level;
                    var probeLevel = Math.Max((byte)1, (byte)(levelGate.RefusedBelow - 1));
                    actor.Character.Level = probeLevel;
                    var request = actor.AcceptQuest(levelGate.QuestId,
                        Enum.Parse<QuestAcceptorType>(levelGate.AcceptorType, ignoreCase: true), levelGate.AcceptorId);
                    actor.Character.Level = originalLevel;
                    return request.State == ActorLifecycleState.Rejected
                        ? new GateVerdict(gate.Name, true, $"refused at probe level {probeLevel} (below {levelGate.RefusedBelow}): {request.Detail}")
                        : new GateVerdict(gate.Name, false, $"accepted at probe level {probeLevel} (below {levelGate.RefusedBelow})");
                }
                case AbilityGateCheck abilityGate:
                {
                    var ability = abilityGate.Ability;
                    var originalExp = actor.Character.Abilities.Abilities.TryGetValue(ability, out var record) ? record.Exp : 0;
                    if (actor.Character.Abilities.Abilities.TryGetValue(ability, out var probeRecord))
                        probeRecord.Exp = 0; // level 1 — below any gate
                    var request = actor.AcceptQuest(abilityGate.QuestId,
                        Enum.Parse<QuestAcceptorType>(abilityGate.AcceptorType, ignoreCase: true), abilityGate.AcceptorId);
                    if (actor.Character.Abilities.Abilities.TryGetValue(ability, out var restoreRecord))
                        restoreRecord.Exp = originalExp;
                    return request.State == ActorLifecycleState.Rejected
                        ? new GateVerdict(gate.Name, true, $"refused with {ability} below {abilityGate.RefusedBelow}: {request.Detail}")
                        : new GateVerdict(gate.Name, false, $"accepted with {ability} at level {Math.Max((byte)1, (byte)(abilityGate.RefusedBelow - 1))}");
                }
                case PrereqGateCheck prereqGate:
                {
                    var originalState = actor.Character.Quests.HasQuestCompleted(prereqGate.PrereqQuestId);
                    actor.Character.Quests.SetCompletedQuestFlag(prereqGate.PrereqQuestId, false);
                    var request = actor.AcceptQuest(prereqGate.QuestId,
                        Enum.Parse<QuestAcceptorType>(prereqGate.AcceptorType, ignoreCase: true), prereqGate.AcceptorId);
                    actor.Character.Quests.SetCompletedQuestFlag(prereqGate.PrereqQuestId, originalState);
                    return request.State == ActorLifecycleState.Rejected
                        ? new GateVerdict(gate.Name, true, $"refused without prereq quest {prereqGate.PrereqQuestId} completed: {request.Detail}")
                        : new GateVerdict(gate.Name, false, $"accepted without prereq quest {prereqGate.PrereqQuestId} completed");
                }
                default:
                    return new GateVerdict(gate.Name, false, $"unknown gate check type {gate.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            return new GateVerdict(gate.Name, false, $"probe crashed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    #endregion

    #region Drive

    private static (string Stage, ActorFailureReason Failure, string Reason)? Drive(
        BotScenarioTemplate template, Character character, PlayerBotController controller,
        GameplayActor actor, IScenarioWorldAdapter world, List<ScenarioStageVerdict> stages)
    {
        var questId = template.Drive.QuestId;
        var acceptorType = Enum.Parse<QuestAcceptorType>(template.Drive.AcceptorType, ignoreCase: true);

        // Accept through the real gate (validated action request).
        var acceptRequest = actor.AcceptQuest(questId, acceptorType, template.Drive.AcceptorId);
        if (acceptRequest.State != ActorLifecycleState.Completed)
            return ("ACCEPT", acceptRequest.Failure ?? ActorFailureReason.RejectedAction,
                $"accept refused by engine gate: {acceptRequest.Detail}");

        // Stage loop — fire events, ONE advance, record (the calibrated
        // scenario-harness semantics; the completion path drops the quest
        // from ActiveQuests — terminal state, correct engine behavior).
        foreach (var stage in template.Drive.Stages)
        {
            var fired = 0;
            foreach (var scenarioEvent in stage.Events)
            {
                FireEvent(scenarioEvent, questId, character, controller, actor, world);
                fired++;
            }

            var active = character.Quests.ActiveQuests.ContainsKey(questId);
            string advance = "skipped (quest terminal)";
            string stepObserved = "-", statusObserved = "-";
            if (active)
            {
                var advanceRequest = actor.AdvanceQuest(questId);
                if (advanceRequest.State != ActorLifecycleState.Completed)
                    return (stage.Name, advanceRequest.Failure ?? ActorFailureReason.StateTransition,
                        $"advance refused: {advanceRequest.Detail}");
                advance = "ran";
            }

            var quest = character.Quests.ActiveQuests.GetValueOrDefault(questId);
            stepObserved = quest?.Step.ToString() ?? "Dropped";
            statusObserved = quest?.Status.ToString() ?? "Completed";
            stages.Add(new ScenarioStageVerdict(stage.Name, fired, advance, stepObserved, statusObserved));

            if (!character.Quests.ActiveQuests.ContainsKey(questId))
                break; // completed — remaining stages are terminal pass-through
        }

        return null;
    }

    /// <summary>
    /// Economy replay drive (M5.1, t_7c224245): fires each step's
    /// Deposit/Withdraw events through the actor contract and requires
    /// every request Completed before the next step runs — a refused or
    /// failed event fails the template with its §17 reason and detail.
    /// This is the replay vocabulary the Phase 2 M3a/M4 economic replay
    /// drives (recorded deposit/withdraw sequences replayed through normal
    /// gameplay services on a live bot).
    /// </summary>
    private static (string Stage, ActorFailureReason Failure, string Reason)? EconomyDrive(
        BotScenarioTemplate template, Character character, PlayerBotController controller,
        GameplayActor actor, IScenarioWorldAdapter world, List<ScenarioStageVerdict> stages)
    {
        foreach (var step in template.EconomyDrive!.Steps)
        {
            var fired = 0;
            var lastEvent = "";
            var lastDetail = "";
            foreach (var scenarioEvent in step.Events)
            {
                lastEvent = scenarioEvent.Type;
                var request = FireEvent(scenarioEvent, 0, character, controller, actor, world);
                fired++;
                if (request is not { State: ActorLifecycleState.Completed })
                    return (step.Name, request?.Failure ?? ActorFailureReason.RejectedAction,
                        $"economy event '{scenarioEvent.Type}' not completed: {request?.Detail ?? "no request returned"}");
                lastDetail = request.Detail ?? "completed";
            }

            stages.Add(new ScenarioStageVerdict(step.Name, fired, "n/a", lastEvent, lastDetail));
        }

        return null;
    }

    /// <summary>
    /// Fires one scenario event through the world pipeline surface. Report
    /// events resolve their target via the world adapter and drive the REAL
    /// turn-in path (DoReportEvents) through the actor contract. Quest
    /// events return null; M5.1 economy events (DepositMoney/WithdrawMoney/
    /// DepositItem/WithdrawItem) return the actor request so the economy
    /// drive can verify every request Completed.
    /// </summary>
    private static ActorRequest? FireEvent(ScenarioEvent scenarioEvent, uint questId, Character character,
        PlayerBotController controller, GameplayActor actor, IScenarioWorldAdapter world)
    {
        switch (scenarioEvent.Type)
        {
            case "MonsterHunt":
                controller.KillNpc(scenarioEvent.NpcId, scenarioEvent.Count);
                return null;
            case "MonsterGroupHunt":
                controller.KillNpcGroup(scenarioEvent.NpcId, scenarioEvent.Count);
                return null;
            case "ItemGather":
                controller.GatherItem(questId, scenarioEvent.ItemId, scenarioEvent.Count);
                return null;
            case "ItemGroupGather":
                character.Events.OnItemGroupGather(character, new OnItemGroupGatherArgs
                {
                    ItemId = scenarioEvent.ItemId, ItemGroupId = scenarioEvent.ItemGroupId, Count = scenarioEvent.Count
                });
                return null;
            case "ItemUse":
                controller.UseItem(scenarioEvent.ItemId, scenarioEvent.Count);
                return null;
            case "ItemGroupUse":
                character.Events.OnItemGroupUse(character, new OnItemGroupUseArgs
                {
                    ItemGroupId = scenarioEvent.ItemGroupId, Count = scenarioEvent.Count
                });
                return null;
            case "Talk":
                controller.TalkToNpc(questId, scenarioEvent.NpcId);
                return null;
            case "TalkNpcGroup":
                character.Events.OnTalkNpcGroupMade(character, new OnTalkNpcGroupMadeArgs
                {
                    NpcGroupId = scenarioEvent.NpcGroupId, NpcId = scenarioEvent.NpcId, QuestComponentId = scenarioEvent.ComponentId
                });
                return null;
            case "Interaction":
                controller.InteractWithDoodad(scenarioEvent.DoodadId, scenarioEvent.Count);
                return null;
            case "EnterSphere":
                controller.EnterSphere(questId, scenarioEvent.ComponentId);
                return null;
            case "Craft":
                for (var i = 0; i < scenarioEvent.Count; i++)
                    character.Events.OnCraft(character, new OnCraftArgs { CraftId = scenarioEvent.CraftId });
                return null;
            case "ExpressFire":
                controller.ExpressEmotion(scenarioEvent.NpcId, scenarioEvent.EmotionId);
                return null;
            case "LevelUp":
                controller.LevelUp();
                return null;
            case "Aggro":
                controller.AggroNpc(scenarioEvent.NpcId);
                return null;
            case "ZoneKill":
                controller.ZoneKill(scenarioEvent.ZoneGroupId);
                return null;
            case "CinemaStarted":
                controller.CinemaStarted(scenarioEvent.CinemaId);
                return null;
            case "CinemaEnded":
                controller.CinemaEnded(scenarioEvent.CinemaId);
                return null;
            case "DepositMoney":
                return actor.DepositMoney(scenarioEvent.Amount);
            case "WithdrawMoney":
                return actor.WithdrawMoney(scenarioEvent.Amount);
            case "DepositItem":
                return actor.DepositItem(scenarioEvent.ItemId);
            case "WithdrawItem":
                return actor.WithdrawItem(scenarioEvent.ItemId);
            case "ReportNpc":
            {
                var objId = world.ResolveNpcObjId(scenarioEvent.NpcId);
                if (objId == 0)
                    throw new InvalidOperationException(
                        $"ReportNpc: NPC template {scenarioEvent.NpcId} unresolvable in scenario world (quest {questId})");
                var request = actor.TurnInQuest(questId, objId, scenarioEvent.Selected);
                
                if (request.State != ActorLifecycleState.Completed)
                    throw new InvalidOperationException($"ReportNpc turn-in refused: {request.Detail}");
                return null;
            }
            case "ReportDoodad":
            {
                var objId = world.ResolveDoodadObjId(scenarioEvent.DoodadId);
                if (objId == 0)
                    throw new InvalidOperationException(
                        $"ReportDoodad: doodad template {scenarioEvent.DoodadId} unresolvable in scenario world (quest {questId})");
                var request = actor.TurnInAtDoodad(questId, objId, scenarioEvent.Selected);
                
                if (request.State != ActorLifecycleState.Completed)
                    throw new InvalidOperationException($"ReportDoodad turn-in refused: {request.Detail}");
                return null;
            }
            case "ReportJournal":
            {
                var request = actor.AutoTurnInQuest(questId, scenarioEvent.Selected);
                
                if (request.State != ActorLifecycleState.Completed)
                    throw new InvalidOperationException($"ReportJournal turn-in refused: {request.Detail}");
                return null;
            }
            default:
                throw new InvalidOperationException($"unknown scenario event type '{scenarioEvent.Type}'");
        }
    }

    #endregion

    #region Criteria

    private static CriterionVerdict EvaluateCriterion(ScenarioCriterion criterion, Character character,
        PlayerBotController controller, GameplayActor actor)
    {
        try
        {
            switch (criterion)
            {
                case QuestCompletedCriterion completed:
                {
                    var isActive = character.Quests.HasQuest(completed.QuestId);
                    var flag = character.Quests.HasQuestCompleted(completed.QuestId);
                    return flag && !isActive
                        ? new CriterionVerdict(criterion.Name, true, $"quest {completed.QuestId} completed (flag set, not active)")
                        : new CriterionVerdict(criterion.Name, false,
                            $"quest {completed.QuestId} not completed: active={isActive}, flag={flag}");
                }
                case QuestNotActiveCriterion notActive:
                {
                    var isActive = character.Quests.HasQuest(notActive.QuestId);
                    return !isActive
                        ? new CriterionVerdict(criterion.Name, true, $"quest {notActive.QuestId} not active")
                        : new CriterionVerdict(criterion.Name, false, $"quest {notActive.QuestId} still active");
                }
                case LevelAtLeastCriterion level:
                    return character.Level >= level.Level
                        ? new CriterionVerdict(criterion.Name, true, $"level {character.Level} >= {level.Level}")
                        : new CriterionVerdict(criterion.Name, false, $"level {character.Level} < {level.Level}");
                case ItemHeldCriterion item:
                {
                    var held = controller.InventoryCount(item.ItemId);
                    return held >= item.Count
                        ? new CriterionVerdict(criterion.Name, true, $"holds {held} of item {item.ItemId} (need {item.Count})")
                        : new CriterionVerdict(criterion.Name, false, $"holds {held} of item {item.ItemId} (need {item.Count})");
                }
                case SkillKnownCriterion skill:
                {
                    var known = character.Skills.Skills.ContainsKey(skill.SkillId)
                                || SkillManager.Instance.IsDefaultSkill(skill.SkillId)
                                || SkillManager.Instance.IsCommonSkill(skill.SkillId);
                    return known
                        ? new CriterionVerdict(criterion.Name, true, $"skill {skill.SkillId} known")
                        : new CriterionVerdict(criterion.Name, false, $"skill {skill.SkillId} not learned");
                }
                case AbilityLevelCriterion ability:
                {
                    var level = character.Abilities.GetAbilityLevel(ability.Ability);
                    return level >= ability.Level
                        ? new CriterionVerdict(criterion.Name, true, $"{ability.Ability} at level {level} (need {ability.Level})")
                        : new CriterionVerdict(criterion.Name, false, $"{ability.Ability} at level {level} (need {ability.Level})");
                }
                case ReAcceptRefusedCriterion reAccept:
                {
                    var accepted = character.Quests.AddQuest(reAccept.QuestId, false,
                        Enum.Parse<QuestAcceptorType>(reAccept.AcceptorType, ignoreCase: true), reAccept.AcceptorId);
                    return !accepted
                        ? new CriterionVerdict(criterion.Name, true,
                            $"re-accept of completed quest {reAccept.QuestId} refused by engine (repeatable/daily gate)")
                        : new CriterionVerdict(criterion.Name, false,
                            $"re-accept of completed quest {reAccept.QuestId} was ACCEPTED — repeatable gate not enforced");
                }
                case BankMoneyCriterion bankMoney:
                    return character.Money2 == bankMoney.Expected
                        ? new CriterionVerdict(criterion.Name, true, $"bank money {character.Money2} == {bankMoney.Expected}")
                        : new CriterionVerdict(criterion.Name, false, $"bank money {character.Money2} != {bankMoney.Expected}");
                case ContainerItemCriterion containerItem:
                {
                    var held = character.Inventory.GetItemsCount(containerItem.Container, containerItem.ItemId);
                    return held == containerItem.Count
                        ? new CriterionVerdict(criterion.Name, true,
                            $"{containerItem.Container} holds {held} of item {containerItem.ItemId} (expected {containerItem.Count})")
                        : new CriterionVerdict(criterion.Name, false,
                            $"{containerItem.Container} holds {held} of item {containerItem.ItemId} (expected {containerItem.Count})");
                }
                default:
                    return new CriterionVerdict(criterion.Name, false, $"unknown criterion type {criterion.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            return new CriterionVerdict(criterion.Name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    #endregion

    #region Result helpers

    private static ScenarioRunResult Fail(BotScenarioTemplate template, string stage,
        ActorFailureReason? failure, string reason, List<string> rigNotes,
        GameplayActor actor, List<GateVerdict>? gates = null, List<ScenarioStageVerdict>? stages = null,
        List<CriterionVerdict>? criteria = null)
        => new()
        {
            Template = template.Name,
            Passed = false,
            FailStage = stage,
            Failure = failure,
            FailReason = reason,
            RigNotes = rigNotes,
            Gates = gates ?? [],
            Stages = stages ?? [],
            Criteria = criteria ?? [],
            ActorRequests = actor.AuditTrace.Count
        };

    #endregion
}
