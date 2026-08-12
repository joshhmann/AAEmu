using System.Text.Json;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M2b-E2E quest driver — the pilot's calibrated drive (PlayerbotQuestDriver +
/// QuestScenarioAssertions stage model) executed over the BotDriveBridge
/// against a REAL networked bot character on a REAL server:
///   - accept     -> CharacterQuests.AddQuest on the live character (real gates)
///   - progress   -> the engine's UnitEvents surface via the bridge
///   - turn-in    -> QuestManager.DoReportEvents at a REAL world NPC objId
/// Every mutation happens server-side through normal gameplay services; the
/// bridge only executes, the runner only reads state back.
///
/// Stage-loop semantics mirror the pilot EXACTLY: fire the stage's events,
/// then ONE advance, then evaluate against the manifest's expect block.
/// </summary>
public static class E2eQuestDriver
{
    public sealed record StageVerdict(string Stage, bool Passed, string Reason = "", string StepObserved = "", string StatusObserved = "");

    public sealed record QuestResult(uint QuestId, bool Passed, string Name = "", string FailStage = "", string FailReason = "", List<StageVerdict>? Stages = null)
    {
        public string ReproTrace()
            => $"quest {QuestId} ({Name}) FAILED at stage {FailStage}: {FailReason}" +
               (Stages is { Count: > 0 } ? "\n  stages: " + string.Join(" | ", Stages.Select(s => s.ToString())) : "");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Drives one quest to completion on a networked bot.</summary>
    public static QuestResult DriveQuest(BotDriveClient bridge, string botName, E2eQuestManifest manifest, int level)
    {
        var stages = new List<StageVerdict>();

        // Inventory preseed (acceptor item, gather objectives) through the real
        // item acquisition path.
        foreach (var (itemId, count) in manifest.Inventory)
            Call(bridge, botName, $"{{ \"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"stock\",\"item\":{itemId},\"count\":{count} }}");

        // The bot "levels through play"; real gates still evaluate.
        Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"setLevel\",\"level\":{Math.Max(level, 1)}}}");

        // ACCEPT through the real gate.
        var accept = Call(bridge, botName,
            $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"accept\",\"quest\":{manifest.QuestId},\"acceptor\":\"{manifest.AcceptorType}\",\"acceptorId\":{manifest.AcceptorId}}}");
        if (!accept.GetProperty("accepted").GetBoolean())
        {
            var reason = $"accept refused by engine gate (acceptor {manifest.AcceptorType}/{manifest.AcceptorId})";
            stages.Add(new StageVerdict("ACCEPT", false, reason));
            return new QuestResult(manifest.QuestId, false, manifest.Name, "ACCEPT", reason, stages);
        }

        return RunStagesFrom(bridge, botName, manifest, 0, stages);
    }

    /// <summary>
    /// Accepts a quest and advances it through every non-terminal stage WITHOUT
    /// turning in — the exact state a disconnect mid-quest must survive.
    /// </summary>
    public static bool PrepareQuest(BotDriveClient bridge, string botName, E2eQuestManifest manifest, int level)
    {
        foreach (var (itemId, count) in manifest.Inventory)
            Call(bridge, botName, $"{{ \"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"stock\",\"item\":{itemId},\"count\":{count} }}");
        Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"setLevel\",\"level\":{Math.Max(level, 1)}}}");

        var accept = Call(bridge, botName,
            $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"accept\",\"quest\":{manifest.QuestId},\"acceptor\":\"{manifest.AcceptorType}\",\"acceptorId\":{manifest.AcceptorId}}}");
        if (!accept.GetProperty("accepted").GetBoolean())
            return false;

        foreach (var stage in manifest.Stages)
        {
            if (IsTerminalStage(stage.Name))
                continue;
            foreach (var rawEvent in stage.Events)
            {
                if (IsReportEvent(rawEvent))
                    continue; // never turn in during prepare
                FireEvent(bridge, botName, manifest.QuestId, rawEvent);
            }

            Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"advance\",\"quest\":{manifest.QuestId}}}");
        }

        return IsQuestActive(bridge, botName, manifest.QuestId);
    }

    /// <summary>Resumes a prepared quest from the first terminal stage (the
    /// reconnect path).</summary>
    public static QuestResult ResumePreparedQuest(BotDriveClient bridge, string botName, E2eQuestManifest manifest)
    {
        var stages = new List<StageVerdict>();
        if (!IsQuestActive(bridge, botName, manifest.QuestId))
        {
            var reason = "quest not active on resume — restore failed";
            stages.Add(new StageVerdict("ACCEPT", false, reason));
            return new QuestResult(manifest.QuestId, false, manifest.Name, "ACCEPT", reason, stages);
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

        return RunStagesFrom(bridge, botName, manifest, firstTerminal, stages);
    }

    private static QuestResult RunStagesFrom(BotDriveClient bridge, string botName, E2eQuestManifest manifest, int firstStageIndex, List<StageVerdict> stages)
    {
        var questId = manifest.QuestId;

        for (var stageIndex = firstStageIndex; stageIndex < manifest.Stages.Count; stageIndex++)
        {
            var stage = manifest.Stages[stageIndex];
            // Fresh-probe fidelity stages (TIMEOUT/RESET/GUARD_DIED) operate
            // on a FRESH probe quest built by the scenario harness — never on
            // the live drive. After a successful drive the quest is
            // dropped+completed, so e.g. TIMEOUT's "expected step Fail" can
            // never reproduce against the live quest (false-RED). Skip them
            // exactly like PERSIST.
            if (stage.Name.ToUpperInvariant() is "PERSIST" or "TIMEOUT" or "RESET" or "GUARD_DIED")
                continue; // probe stages have their own harness checkpoints

            StageVerdict verdict;
            try
            {
                if (IsQuestActive(bridge, botName, questId))
                {
                    foreach (var rawEvent in stage.Events)
                        FireEvent(bridge, botName, questId, rawEvent);

                    Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"advance\",\"quest\":{questId}}}");
                }

                // Live-server settle: the server's evaluation queue runs
                // RunCurrentStep asynchronously (~1ms schedule) after every
                // event-driven objective change, so the quest may still be
                // mid-transition when the advance op returns. Poll until the
                // state is stable (or the quest completes/drops), then
                // evaluate — otherwise the driver races the queue and reads a
                // transient state (e.g. quest already dropped+completed at the
                // READY stage).
                var state = SettleQuestState(bridge, botName, questId);
                verdict = EvaluateStage(manifest, stage, bridge, botName, state);
            }
            catch (Exception ex)
            {
                verdict = new StageVerdict(stage.Name, false, $"{ex.GetType().Name}: {ex.Message}");
            }

            stages.Add(verdict);
            if (!verdict.Passed)
                return new QuestResult(questId, false, manifest.Name, stage.Name, verdict.Reason, stages);
        }

        var passed = !IsQuestActive(bridge, botName, questId) && HasCompleted(bridge, botName, questId);
        if (!passed)
        {
            var reason = IsQuestActive(bridge, botName, questId)
                ? "quest still active at end of drive"
                : "quest completed flag not set";
            return new QuestResult(questId, false, manifest.Name, "VERIFY", reason, stages);
        }

        return new QuestResult(questId, true, manifest.Name, Stages: stages);
    }

    /// <summary>
    /// Polls the live quest state until it stops changing (the server's async
    /// evaluation queue has settled), or the quest is dropped/completed.
    /// Returns the last observed state. The queue is scheduled ~1ms after an
    /// event and may run several evaluations; without settling, the driver
    /// reads a transient state and misjudges the stage (the documented
    /// "expected Reward, got quest dropped" class).
    /// </summary>
    private static QuestStateSnapshot SettleQuestState(BotDriveClient bridge, string botName, uint questId)
    {
        var state = QuestState(bridge, botName, questId);
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            var next = QuestState(bridge, botName, questId);
            if (SameState(state, next))
                return next;
            state = next;
            Thread.Sleep(100);
        }

        return state;
    }

    private static bool SameState(QuestStateSnapshot a, QuestStateSnapshot b)
        => a.Active == b.Active && a.Step == b.Step && a.Status == b.Status;

    private static StageVerdict EvaluateStage(E2eQuestManifest manifest, E2eQuestManifest.Stage stage,
        BotDriveClient bridge, string botName, QuestStateSnapshot state)
    {
        var failures = new List<string>();

        // Live-server terminal observation: the evaluation queue may run the
        // reward step (GoToNextStep Reward -> SetCompletedQuestFlag ->
        // DropQuest) between the report event and the state read, so the
        // quest is no longer in ActiveQuests. A dropped quest WITH the
        // completed flag is the terminal evidence of a successful turn-in —
        // equivalent to resting at Reward/Completed (the pilot's calibrated
        // expectation for the READY stage).
        var terminalCompleted = !state.Active && HasCompleted(bridge, botName, manifest.QuestId);

        if (stage.ExpectStep != null)
        {
            if (!state.Active)
            {
                if (!terminalCompleted ||
                    !string.Equals(stage.ExpectStep, "Reward", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"expected step {stage.ExpectStep}, got quest dropped (completed)");
            }
            else if (!string.Equals(state.Step, stage.ExpectStep, StringComparison.OrdinalIgnoreCase))
                failures.Add($"expected step {stage.ExpectStep}, got {state.Step}");
        }

        if (stage.ExpectStatus != null)
        {
            if (!state.Active)
            {
                if (!terminalCompleted ||
                    !string.Equals(stage.ExpectStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"expected status {stage.ExpectStatus}, got quest dropped (completed)");
            }
            else if (!string.Equals(state.Status, stage.ExpectStatus, StringComparison.OrdinalIgnoreCase))
                failures.Add($"expected status {stage.ExpectStatus}, got {state.Status}");
        }

        if (stage.ExpectObjectives != null && state.Active)
        {
            for (var i = 0; i < stage.ExpectObjectives.Length && i < state.Objectives.Length; i++)
            {
                if (state.Objectives[i] != stage.ExpectObjectives[i])
                    failures.Add($"expected objective[{i}] = {stage.ExpectObjectives[i]}, got {state.Objectives[i]}");
            }
        }

        foreach (var (itemId, count) in stage.ExpectRewardItems)
        {
            var found = InvCount(bridge, botName, itemId);
            if (found < count)
                failures.Add($"expected reward item {itemId} x{count} in inventory, found {found}");
        }

        if (stage.ExpectCompleted == true && !HasCompleted(bridge, botName, manifest.QuestId))
            failures.Add("expected completed-quest flag set, found not completed");

        return failures.Count == 0
            ? new StageVerdict(stage.Name, true, StepObserved: state.Step ?? "", StatusObserved: state.Status ?? "")
            : new StageVerdict(stage.Name, false, string.Join("; ", failures), state.Step ?? "", state.Status ?? "");
    }

    /// <summary>
    /// Fires one synthetic gameplay event through the bridge — the same engine
    /// event surface the world interaction pipeline fires (types are the quest
    /// act family names, same as QuestScenarioDriver.FireEvent).
    /// </summary>
    private static void FireEvent(BotDriveClient bridge, string botName, uint questId, JsonElement rawEvent)
    {
        var type = rawEvent.GetProperty("type").GetString();
        var q = $"\"quest\":{questId},";
        switch (type)
        {
            case "MonsterHunt":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"kill\",{q}\"npc\":{UInt(rawEvent, "npcId")},\"count\":{Int(rawEvent, "count", 1)}}}");
                break;
            case "MonsterGroupHunt":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"killGroup\",{q}\"npc\":{UInt(rawEvent, "npcId")},\"count\":{Int(rawEvent, "count", 1)}}}");
                break;
            case "ItemGather":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"gather\",{q}\"item\":{UInt(rawEvent, "itemId")},\"count\":{Int(rawEvent, "count", 1)}}}");
                break;
            case "ItemUse":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"useItem\",\"item\":{UInt(rawEvent, "itemId")},\"times\":{Int(rawEvent, "count", 1)}}}");
                break;
            case "Talk":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"talk\",{q}\"npc\":{UInt(rawEvent, "npcId")}}}");
                break;
            case "Interaction":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"interact\",\"doodad\":{UInt(rawEvent, "doodadId")},\"times\":{Int(rawEvent, "count", 1)}}}");
                break;
            case "EnterSphere":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"enterSphere\",{q}\"component\":{UInt(rawEvent, "componentId")}}}");
                break;
            case "ExpressFire":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"express\",\"npc\":{UInt(rawEvent, "npcId")},\"emotion\":{UInt(rawEvent, "emotionId")}}}");
                break;
            case "LevelUp":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"levelUp\"}}");
                break;
            case "Aggro":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"aggro\",\"npc\":{UInt(rawEvent, "npcId")}}}");
                break;
            case "ZoneKill":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"zoneKill\",\"zoneGroup\":{UInt(rawEvent, "zoneGroupId")}}}");
                break;
            case "CinemaStarted":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"cinemaStarted\",\"cinema\":{UInt(rawEvent, "cinemaId")}}}");
                break;
            case "CinemaEnded":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"cinemaEnded\",\"cinema\":{UInt(rawEvent, "cinemaId")}}}");
                break;
            case "ReportNpc":
            {
                // The live world only spawns NPCs within a player's radius, so
                // the bot teleports to the NPC's spawner position first, then
                // polls until the world actually spawns it (spawn tick + 10s
                // radius cache), then turns in at the REAL objId.
                var npcId = UInt(rawEvent, "npcId");
                Call(bridge, botName,
                    $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"teleportToNpc\",\"npc\":{npcId}}}");
                uint objId = 0;
                var deadline = Environment.TickCount64 + 20_000;
                while (Environment.TickCount64 < deadline)
                {
                    objId = Call(bridge, botName,
                        $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"npcObjId\",\"npc\":{npcId}}}")
                        .GetProperty("objId").GetUInt32();
                    if (objId != 0)
                        break;
                    Thread.Sleep(1000);
                }

                if (objId == 0)
                    throw new InvalidOperationException($"ReportNpc: NPC {npcId} never spawned in the live world after teleport");

                var selected = rawEvent.TryGetProperty("selected", out var s) && s.TryGetInt32(out var sel) ? sel : -1;
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"report\",{q}\"npc\":{npcId},\"selected\":{selected}}}");
                break;
            }
            case "ReportDoodad":
            {
                var doodadId = UInt(rawEvent, "doodadId");
                uint objId = 0;
                var deadline = Environment.TickCount64 + 10_000;
                while (Environment.TickCount64 < deadline)
                {
                    objId = Call(bridge, botName,
                        $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"doodadObjId\",\"doodad\":{doodadId}}}")
                        .GetProperty("objId").GetUInt32();
                    if (objId != 0)
                        break;
                    Thread.Sleep(1000);
                }

                if (objId == 0)
                    throw new InvalidOperationException($"ReportDoodad: doodad {doodadId} not spawned in the live world");

                var selected = rawEvent.TryGetProperty("selected", out var s) && s.TryGetInt32(out var sel) ? sel : -1;
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"reportDoodad\",{q}\"doodad\":{doodadId},\"selected\":{selected}}}");
                break;
            }
            case "ReportJournal":
                Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"autoTurnIn\",{q}}}");
                break;
            default:
                throw new InvalidOperationException($"unsupported event type '{type}' for E2E drive");
        }
    }

    public sealed record QuestStateSnapshot(bool Active, string? Step, string? Status, int[] Objectives);

    public static QuestStateSnapshot QuestState(BotDriveClient bridge, string botName, uint questId)
    {
        var data = Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"questState\",\"quest\":{questId}}}");
        if (!data.GetProperty("active").GetBoolean())
            return new QuestStateSnapshot(false, null, null, []);

        var objectives = new List<int>();
        foreach (var o in data.GetProperty("objectives").EnumerateArray())
            objectives.Add(o.GetInt32());

        return new QuestStateSnapshot(true,
            data.GetProperty("step").GetString(),
            data.GetProperty("status").GetString(),
            objectives.ToArray());
    }

    public static bool IsQuestActive(BotDriveClient bridge, string botName, uint questId)
        => Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"isActive\",\"quest\":{questId}}}").GetProperty("active").GetBoolean();

    public static bool HasCompleted(BotDriveClient bridge, string botName, uint questId)
        => Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"hasCompleted\",\"quest\":{questId}}}").GetProperty("completed").GetBoolean();

    public static int InvCount(BotDriveClient bridge, string botName, uint itemId)
        => Call(bridge, botName, $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"invCount\",\"item\":{itemId}}}").GetProperty("count").GetInt32();

    public static JsonElement Call(BotDriveClient bridge, string botName, string json)
        => bridge.Call(json);

    private static bool IsTerminalStage(string stageName)
        => stageName.ToUpperInvariant() is "READY" or "REWARD" or "PERSIST";

    private static bool IsReportEvent(JsonElement rawEvent)
    {
        var type = rawEvent.GetProperty("type").GetString();
        return type is "ReportNpc" or "ReportDoodad" or "ReportJournal";
    }

    private static uint UInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var val) ? val : 0u;

    private static int Int(JsonElement el, string name, int dflt = 0)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var val) ? val : dflt;
}
