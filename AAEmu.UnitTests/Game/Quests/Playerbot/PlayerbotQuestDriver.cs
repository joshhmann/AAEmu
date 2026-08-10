using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Quests.Playerbot;

/// <summary>
/// M2b pilot quest driver: drives one real quest through the real engine on a
/// headless bot, using the manifest's drive spec (acceptor, inventory, events)
/// but ALWAYS through engine surfaces:
///   - accept     -> CharacterQuests.AddQuest (real gates)
///   - progress   -> UnitEvents (engine event surface) + RunCurrentStep
///   - turn-in    -> QuestManager.DoReportEvents (real packet path)
/// Report events in manifests are replaced by the real turn-in path (spawn the
/// NPC in the session world, then DoReportEvents). PERSIST stages are skipped
/// here — the pilot's restart-persistence metric runs its own checkpoints.
///
/// Stage-loop semantics mirror the scenario driver EXACTLY (which is calibrated
/// green on the same manifests, 189/189): fire the stage's events, then ONE
/// RunCurrentStep, then evaluate. The loop must NOT keep advancing until
/// "rest": the engine's completion path (GoToNextStep -> SetCompletedQuestFlag
/// -> DropQuest -> Quest.Drop) leaves a completed quest object in
/// Step=Drop/Status=Dropped — the correct terminal state, but only reachable
/// BY the REWARD stage. Extra advances before evaluation misread the stage
/// (e.g. quest 259's START expects Progress, but a rest-loop lands Ready).
/// </summary>
public static class PlayerbotQuestDriver
{
    public sealed record PilotStageVerdict(string Stage, StageOutcome Outcome, string Reason = "", string StepObserved = "", string StatusObserved = "");

    public sealed record PilotQuestResult(uint QuestId, bool Passed, string Name = "", string FailStage = "", string FailReason = "", List<PilotStageVerdict> Stages = null)
    {
        public string ReproTrace()
            => $"quest {QuestId} ({Name}) FAILED at stage {FailStage}: {FailReason}" +
               (Stages?.Count > 0 ? "\n  stages: " + string.Join(" | ", Stages.Select(s => s.ToString())) : "");
    }

    /// <summary>
    /// Drives one quest to completion on a fresh-or-reused bot.
    /// </summary>
    /// <param name="bot">Bot controller (holds the headless character).</param>
    /// <param name="session">Headless session (world spawns for turn-in NPCs).</param>
    /// <param name="manifest">Drive spec (from the scenario manifests).</param>
    /// <param name="level">Level to set the character to before accepting (the
    /// bot "leveled through play" simulation; the real gates still evaluate).</param>
    public static PilotQuestResult DriveQuest(PlayerBotController bot, HeadlessSession session,
        QuestScenarioManifest manifest, byte level)
    {
        var questId = manifest.QuestId;
        var stages = new List<PilotStageVerdict>();
        var name = manifest.Name;

        // Rig the world/services this quest needs (real services, rigged data).
        PlayerbotPilotRig.RegisterQuestItems(manifest);
        PlayerbotPilotRig.SeedQuestGroups(manifest);

        // Inventory preseed (acceptor item, gather objectives).
        if (manifest.Inventory is { Count: > 0 })
        {
            foreach (var stockItem in manifest.Inventory)
                bot.StockInventory(stockItem.ItemId, stockItem.Count);
        }

        // The bot levels through play between quests; gates still evaluate.
        if (bot.Character.Level < level)
            bot.Character.Level = level;

        // ACCEPT through the real gate.
        var acceptorType = Enum.Parse<QuestAcceptorType>(manifest.Acceptor.Type, ignoreCase: true);
        if (!bot.AcceptQuest(questId, acceptorType, manifest.Acceptor.Id))
        {
            var reason = $"accept refused by engine gate (acceptor {manifest.Acceptor.Type}/{manifest.Acceptor.Id})";
            stages.Add(new PilotStageVerdict("ACCEPT", StageOutcome.Fail, reason));
            return new PilotQuestResult(questId, false, name, "ACCEPT", reason, stages);
        }

        var quest = bot.ActiveQuest(questId);
        if (quest == null)
        {
            var reason = "quest not in ActiveQuests after AddQuest returned true";
            stages.Add(new PilotStageVerdict("ACCEPT", StageOutcome.Fail, reason));
            return new PilotQuestResult(questId, false, name, "ACCEPT", reason, stages);
        }

        return RunStagesFrom(bot, session, quest, manifest, 0, stages);
    }

    /// <summary>
    /// Accepts a quest and advances it through every non-terminal stage
    /// (START/SUPPLY/PROGRESS) WITHOUT turning in. Leaves the quest active at
    /// Ready — the exact state a disconnect mid-quest must survive. Used by the
    /// restart-persistence checkpoint.
    /// </summary>
    public static Quest PrepareQuest(PlayerBotController bot, HeadlessSession session,
        QuestScenarioManifest manifest, byte level)
    {
        var questId = manifest.QuestId;

        PlayerbotPilotRig.RegisterQuestItems(manifest);
        PlayerbotPilotRig.SeedQuestGroups(manifest);
        if (manifest.Inventory is { Count: > 0 })
        {
            foreach (var stockItem in manifest.Inventory)
                bot.StockInventory(stockItem.ItemId, stockItem.Count);
        }
        if (bot.Character.Level < level)
            bot.Character.Level = level;

        var acceptorType = Enum.Parse<QuestAcceptorType>(manifest.Acceptor.Type, ignoreCase: true);
        if (!bot.AcceptQuest(questId, acceptorType, manifest.Acceptor.Id))
            return null;

        var quest = bot.ActiveQuest(questId);
        if (quest == null)
            return null;

        foreach (var stage in manifest.Stages)
        {
            if (IsTerminalStage(stage.Name))
                continue;
            foreach (var rawEvent in stage.Events)
            {
                if (IsReportEvent(rawEvent))
                    continue; // never turn in during prepare
                QuestScenarioDriver.FireEvent(quest, rawEvent);
            }
            bot.Advance(questId);
        }

        return bot.ActiveQuest(questId);
    }

    /// <summary>
    /// Resumes a prepared (already-active) quest from the first terminal stage
    /// (READY turn-in) through completion — the reconnect path. Returns the
    /// same verdict shape as DriveQuest so restart-persistence failures carry
    /// a repro trace.
    /// </summary>
    public static PilotQuestResult ResumePreparedQuest(PlayerBotController bot, HeadlessSession session,
        QuestScenarioManifest manifest)
    {
        var questId = manifest.QuestId;
        var stages = new List<PilotStageVerdict>();
        var quest = bot.ActiveQuest(questId);
        if (quest == null)
        {
            var reason = "quest not active on resume — restore failed";
            stages.Add(new PilotStageVerdict("ACCEPT", StageOutcome.Fail, reason));
            return new PilotQuestResult(questId, false, manifest.Name, "ACCEPT", reason, stages);
        }

        var firstTerminal = -1;
        for (var i = 0; i < manifest.Stages.Count; i++)
        {
            if (IsTerminalStage(manifest.Stages[i].Name))
            {
                firstTerminal = i;
                break;
            }
        }

        return RunStagesFrom(bot, session, quest, manifest, firstTerminal, stages);
    }

    /// <summary>Terminal stages are never driven by PrepareQuest (they are the
    /// resume path) and PERSIST is always a harness-only round-trip check.</summary>
    private static bool IsTerminalStage(string stageName)
        => stageName.ToUpperInvariant() is "READY" or "REWARD" or "PERSIST";

    private static bool IsReportEvent(System.Text.Json.JsonElement rawEvent)
    {
        var type = rawEvent.GetProperty("type").GetString();
        return type is "ReportNpc" or "ReportDoodad" or "ReportJournal";
    }

    /// <summary>
    /// Runs manifest stages from a given index using the scenario driver's
    /// calibrated semantics: fire events -> ONE RunCurrentStep -> evaluate.
    /// </summary>
    private static PilotQuestResult RunStagesFrom(PlayerBotController bot, HeadlessSession session,
        Quest quest, QuestScenarioManifest manifest, int firstStageIndex, List<PilotStageVerdict> stages)
    {
        var questId = manifest.QuestId;
        var name = manifest.Name;

        for (var stageIndex = firstStageIndex; stageIndex < manifest.Stages.Count; stageIndex++)
        {
            var stage = manifest.Stages[stageIndex];
            if (string.Equals(stage.Name, "PERSIST", StringComparison.OrdinalIgnoreCase))
                continue; // pilot restart-persistence metric handles its own checkpoints

            PilotStageVerdict verdict;
            try
            {
                foreach (var rawEvent in stage.Events)
                {
                    var type = rawEvent.GetProperty("type").GetString();
                    switch (type)
                    {
                        // Turn-in events use the REAL packet path (spawn the
                        // target in the session world, then DoReportEvents).
                        case "ReportNpc":
                        {
                            var npcTemplateId = rawEvent.TryGetProperty("npcId", out var n) && n.TryGetUInt32(out var nid) ? nid : 0u;
                            var objId = session.SpawnNpc(npcTemplateId);
                            var selected = rawEvent.TryGetProperty("selected", out var s) && s.TryGetInt32(out var sel)
                                ? sel
                                : manifest.SelectedRewardIndex;
                            _ = bot.ReportTurnIn(questId, objId, selected);
                            break;
                        }
                        case "ReportDoodad":
                        {
                            var doodadTemplateId = rawEvent.TryGetProperty("doodadId", out var d) && d.TryGetUInt32(out var did) ? did : 0u;
                            var objId = session.SpawnDoodad(doodadTemplateId);
                            var selected = rawEvent.TryGetProperty("selected", out var s) && s.TryGetInt32(out var sel)
                                ? sel
                                : manifest.SelectedRewardIndex;
                            _ = bot.ReportDoodadTurnIn(questId, objId, selected);
                            break;
                        }
                        case "ReportJournal":
                            _ = bot.AutoTurnIn(questId, manifest.SelectedRewardIndex);
                            break;
                        default:
                            // Standard engine event surface (same handler the
                            // world interaction pipeline fires).
                            QuestScenarioDriver.FireEvent(quest, rawEvent);
                            break;
                    }
                }

                bot.Advance(questId);

                var eval = QuestScenarioAssertions.EvaluateStage(manifest, quest, bot.Character, stage);
                verdict = new PilotStageVerdict(stage.Name, eval.Outcome, eval.Reason,
                    eval.StepObserved.ToString(), eval.StatusObserved.ToString());
            }
            catch (Exception ex)
            {
                verdict = new PilotStageVerdict(stage.Name, StageOutcome.Fail,
                    $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            stages.Add(verdict);
            if (verdict.Outcome == StageOutcome.Fail)
            {
                return new PilotQuestResult(questId, false, name, stage.Name, verdict.Reason, stages);
            }

            // Terminal: quest dropped from ActiveQuests by the reward path.
            if (!bot.IsActive(questId))
                break;
        }

        var passed = !bot.IsActive(questId) && bot.HasCompleted(questId);
        if (!passed)
        {
            var reason = bot.IsActive(questId)
                ? $"quest still active at end of drive (step={quest.Step}, status={quest.Status})"
                : "quest completed flag not set";
            return new PilotQuestResult(questId, false, name, "VERIFY", reason, stages);
        }

        return new PilotQuestResult(questId, true, name, Stages: stages);
    }
}
