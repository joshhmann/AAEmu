using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Quests.Static;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// PB-002 second half — first AUTONOMOUS LEVELING slice: ONE quest-chain
/// segment run by PERCEIVING the offers themselves, never by following a
/// scripted chain list.
///
/// PLAYER_MODE discipline (the whole point of this scenario):
///   discover → pick the lowest-level offering within the configured band
///   → accept → pursue the objectives the QUEST TEMPLATE names (data-driven
///   from QuestManager templates, not from scenario constants) → turn-in
///   → re-discover. The canonical ids below are WORLD SEEDS only (which
///   offerer NPCs / gather sources exist); no decision in the loop reads a
///   quest id, an NPC id or a next-link constant.
///
/// The chain segment (canonical 1.2 compact.sqlite3, verified 2026-08-25):
/// quest 254 "deliver" (accept Npc 3515 → report Npc 3516, unit_reqs
/// Level ≥ 2 on start component 691) chains into quest 255 (start
/// component 695 carries kind-31 CompleteQuestContext(254) + Level ≥ 3;
/// accept Npc 3516; Progress act ItemGather item 13713 ×1 sourced from
/// highlight doodad 678; report Npc 3516). Completing 254 through the real
/// engine is what makes 255 discoverable — the loop must find it again by
/// perception on the next sweep.
///
/// Objective pursuit matrix (fail-closed — an objective type this slice
/// cannot honestly pursue NEVER fakes progress; it stops the loop with a
/// structured reason naming the missing primitive):
///   - no Progress acts            → delivery leg (turn-in directly)
///   - QuestActObjItemGather       → resolve the source doodad template
///     from HighlightDoodadId among PERCEIVED nearby doodads, InteractWith
///     until the bag holds Count items (real acquisition → engine's own
///     DoItemsAcquiredEvents → OnItemGather credit), AdvanceQuest.
///   - everything else             → fail closed (see GapReason).
///
/// Kill objectives are deliberately NOT composed here yet (the primitives
/// exist — SetTarget/Cast/Loot + the adventurer-spike kill credit — but
/// this slice keeps one honest segment small), so hunts fail closed too,
/// naming what exists and what is missing.
///
/// World access discipline: perception rides Observe() (region graph) +
/// DiscoverQuests() per perceived target; every world object the loop
/// touches was returned by one of those two. No GM shortcuts, no direct
/// quest-state mutation.
/// </summary>
public static class LevelingLoopScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key (registered in <see cref="BotScenarioTemplates"/>).</summary>
    public const string ScenarioName = "leveling-loop-perception";

    // ---- Canonical Solzreed segment ids (compact.sqlite3 canonical 1.2).
    // WORLD SEEDS ONLY — tests spawn these so the rig world matches the
    // real zone; the loop's decisions never reference them.
    /// <summary>Quest 254 — delivery: accept Npc 3515, report Npc 3516.</summary>
    public const uint SeedQuestDeliveryId = 254;
    /// <summary>Quest 255 — prereq-chained on 254; accept/report Npc 3516; ItemGather 13713 ×1 (doodad 678).</summary>
    public const uint SeedQuestGatherId = 255;
    public const uint SeedOffererNpcTemplateId = 3515;
    public const uint SeedHubNpcTemplateId = 3516;
    public const uint SeedGatherSourceDoodadTemplateId = 678;
    public const uint SeedGatherItemTemplateId = 13713;

    /// <summary>Loop parameters. Defaults = the honest L1–9 starter band.</summary>
    public sealed record LoopOptions
    {
        /// <summary>Inclusive availability band for offering choice.</summary>
        public byte BandMin { get; init; } = 1;
        public byte BandMax { get; init; } = 9;
        /// <summary>How many chain links to complete unprompted.</summary>
        public int MaxLinks { get; init; } = 2;
        /// <summary>Bounded InteractWith attempts per gather source before failing Navigation.</summary>
        public int MaxAttemptsPerGatherSource { get; init; } = 3;
    }

    /// <summary>
    /// Kill-credit seam for future hunt legs (unused by this slice — hunts
    /// fail closed). LIVE: real cast damage only (Npc.DoDie credits). RIG:
    /// the documented synthetic kill through the REAL
    /// QuestManager.DoOnMonsterHuntEvents entry point (adventurer-spike
    /// convention). Kept in the contract now so the gap report stays exact:
    /// the seam EXISTS, the composed hunt leg does not.
    /// </summary>
    public interface IKillCreditSeam
    {
        bool TryKill(GameplayActor actor, Npc target);
    }

    /// <summary>One completed chain link, as PERCEIVED (never pre-scripted).</summary>
    public sealed record LinkRecord(
        uint QuestId, byte OfferedLevel, uint AcceptorTemplateId,
        string Pursuit, long ExperienceBefore, long ExperienceAfter);

    /// <summary>Structured run result — spec §17 taxonomy, audit trace attached.</summary>
    public sealed class LoopRunResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<LinkRecord> Links { get; init; } = [];
        public List<string> Notes { get; init; } = [];
        /// <summary>The actor's full audit trace, in execution order.</summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];

        public string Evidence()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Scenario: {Scenario}");
            sb.AppendLine($"Verdict: {(Passed ? "PASS" : "FAIL")}" +
                          (FailStage.Length > 0 ? $" at {FailStage}" : "") +
                          (Failure is { } f ? $" ({f})" : "") +
                          (FailReason.Length > 0 ? $" — {FailReason}" : ""));
            foreach (var note in Notes)
                sb.AppendLine($"- note: {note}");
            foreach (var link in Links)
                sb.AppendLine($"- link: quest {link.QuestId} (offered at level {link.OfferedLevel}, " +
                              $"acceptor {link.AcceptorTemplateId}, pursuit [{link.Pursuit}], " +
                              $"exp {link.ExperienceBefore}→{link.ExperienceAfter})");
            foreach (var t in TraceRecords)
                sb.AppendLine($"- trace: {t.Action}({t.TargetId})→{t.Result}{(t.Failure is { } fr ? $"/{fr}" : "")}");
            return sb.ToString();
        }
    }

    /// <summary>Runs the autonomous loop on an embodied character.</summary>
    public static LoopRunResult Run(Character character, LoopOptions? options = null, IKillCreditSeam? killSeam = null)
    {
        var opts = options ?? new LoopOptions();
        var actor = new GameplayActor(character);
        var links = new List<LinkRecord>();

        try
        {
            for (var linkIndex = 1; linkIndex <= opts.MaxLinks; linkIndex++)
            {
                // ---------------------------------------------------- 1. PERCEIVE
                var perception = Perceive(actor);
                var bandOfferings = perception.Offerings
                    .Where(o => o.Level >= opts.BandMin && o.Level <= opts.BandMax)
                    .OrderBy(o => o.Level).ThenBy(o => o.QuestId)
                    .ToList();

                if (bandOfferings.Count == 0)
                {
                    return Fail("PERCEIVE", ActorFailureReason.Starvation,
                        $"no discoverable quest offerings within band [{opts.BandMin}..{opts.BandMax}] " +
                        $"from {perception.PerceivedNpcCount} NPC(s)/{perception.PerceivedDoodadCount} board(s) " +
                        $"({perception.TotalOfferingsSeen} offering(s) seen, all out of band or gated)", actor, links);
                }

                // ---------------------------------------------------- 2. DECIDE
                // Lowest-level offering in band; ties break to the lowest
                // quest id for determinism. NO id is injected here.
                var chosen = bandOfferings[0];

                // ---------------------------------------------------- 3. ACCEPT
                var accept = actor.AcceptQuest(chosen.QuestId, chosen.AcceptorType, chosen.AcceptorId);
                if (accept.State != ActorLifecycleState.Completed)
                {
                    return Fail("ACCEPT", ActorFailureReason.RejectedAction,
                        $"accept of discovered quest {chosen.QuestId} refused: {accept.Detail}", actor, links);
                }

                var expBefore = character.Experience;

                // ---------------------------------------------------- 4. PURSUE
                var template = QuestManager.Instance.GetTemplate(chosen.QuestId)!;
                var pursuitFailure = PursueObjectives(actor, opts, killSeam, chosen.QuestId, template, perception);
                if (pursuitFailure != null)
                    return pursuitFailure;

                // ---------------------------------------------------- 5. TURN-IN
                var turnInFailure = TurnIn(actor, chosen.QuestId, template, perception);
                if (turnInFailure != null)
                    return turnInFailure;

                links.Add(new LinkRecord(chosen.QuestId, chosen.Level, chosen.AcceptorId,
                    DescribePursuit(template), expBefore, character.Experience));
            }

            // -------------------------------------------------------- 6. VERIFY
            if (links.Count < opts.MaxLinks)
            {
                return Fail("VERIFY", ActorFailureReason.WrongDecision,
                    $"loop stopped after {links.Count}/{opts.MaxLinks} links", actor, links);
            }

            return new LoopRunResult
            {
                Scenario = ScenarioName,
                Passed = true,
                Links = links,
                Notes = [$"completed {links.Count} chained quest(s) unprompted; " +
                         $"total exp gained {links.Sum(l => l.ExperienceAfter - l.ExperienceBefore)}"],
                TraceRecords = [.. actor.AuditTrace]
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "leveling loop crashed");
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor, links);
        }
    }

    // ------------------------------------------------------------------ perceive

    private sealed record PerceptionSnapshot(
        List<QuestOffering> Offerings,
        Dictionary<uint, uint> NpcObjIdsByTemplate,
        Dictionary<uint, List<uint>> DoodadObjIdsByTemplate,
        int PerceivedNpcCount, int PerceivedDoodadCount)
    {
        public int TotalOfferingsSeen => Offerings.Count;
    }

    /// <summary>
    /// One perception sweep: Observe (region graph) → DiscoverQuests on
    /// EVERY perceived NPC and board. Only targets Observe returned are
    /// ever touched (PLAYER_MODE).
    /// </summary>
    private static PerceptionSnapshot Perceive(GameplayActor actor)
    {
        var observation = actor.Observe();

        var offerings = new List<QuestOffering>();
        var npcByTemplate = new Dictionary<uint, uint>();
        foreach (var npcObjId in observation.NearbyNpcObjIds)
        {
            var request = actor.DiscoverQuests(npcObjId);
            if (request.State != ActorLifecycleState.Completed || request.Result is not QuestDiscoveryResult found)
                continue;
            offerings.AddRange(found.Offerings);
            npcByTemplate.TryAdd(found.AcceptorTemplateId, found.TargetObjId);
        }

        var doodadsByTemplate = new Dictionary<uint, List<uint>>();
        var doodadCount = 0;
        foreach (var doodadObjId in observation.NearbyDoodadObjIds)
        {
            doodadCount++;
            var doodad = actor.Character.ParentWorld?.GetDoodad(doodadObjId);
            if (doodad == null)
                continue;
            if (!doodadsByTemplate.TryGetValue(doodad.TemplateId, out var list))
                doodadsByTemplate[doodad.TemplateId] = list = [];
            list.Add(doodadObjId);

            // Boards are quest offerers too (ConAcceptDoodad channel).
            var request = actor.DiscoverQuests(doodadObjId);
            if (request.State == ActorLifecycleState.Completed && request.Result is QuestDiscoveryResult found)
                offerings.AddRange(found.Offerings);
        }

        return new PerceptionSnapshot(offerings, npcByTemplate, doodadsByTemplate,
            observation.NearbyNpcObjIds.Count, doodadCount);
    }

    // ------------------------------------------------------------------ pursue

    private static readonly Dictionary<string, string> KnownPrimitiveGaps = new()
    {
        [nameof(QuestActObjMonsterHunt)] =
            "missing composed hunt leg (primitives EXIST: SetTarget/Cast/Loot + real Npc.DoDie/" +
            "DoOnMonsterHuntEvents kill credit — see adventurer-spike-fox)",
        [nameof(QuestActObjMonsterGroupHunt)] =
            "missing composed hunt leg (primitives EXIST: SetTarget/Cast/Loot + group fanout in DoOnMonsterHuntEvents)",
        [nameof(QuestActObjTalk)] =
            "missing talk-credit contract action (no Talk action on IGameplayActor fires OnTalkMade through a real packet path)",
        [nameof(QuestActObjTalkNpcGroup)] =
            "missing talk-credit contract action (no Talk action on IGameplayActor fires OnTalkMade through a real packet path)",
        [nameof(QuestActObjItemUse)] =
            "missing item-use pursuit composition (UseItem primitive exists; objective wiring uncomposed)",
        [nameof(QuestActObjItemGroupUse)] =
            "missing item-use pursuit composition (UseItem primitive exists; group resolution uncomposed)",
        [nameof(QuestActObjItemGroupGather)] =
            "missing item-group source resolution (HighlightDoodadId is single-template; group sources unmapped)",
        [nameof(QuestActObjSphere)] =
            "missing sphere-entry movement primitive",
        [nameof(QuestActObjCinema)] =
            "missing cinema-trigger primitive",
        [nameof(QuestActObjInteraction)] =
            "missing world-interaction credit composition (OnInteraction firing path for objectives unproven headless)",
        [nameof(QuestActObjCraft)] =
            "missing craft-objective composition (Craft action exists; workbench resolution + recipe mapping uncomposed)",
        [nameof(QuestActObjAggro)] =
            "missing aggro-attribution primitive",
        [nameof(QuestActObjExpressFire)] =
            "missing emotion-express primitive",
        [nameof(QuestActObjZoneKill)] =
            "missing composed hunt leg (zone-scoped kills need the hunt leg first)",
        [nameof(QuestActObjCompleteQuest)] =
            "missing cross-quest objective composition",
        [nameof(QuestActObjAbilityLevel)] =
            "missing ability-training composition",
        [nameof(QuestActObjMateLevel)] =
            "missing mount-training composition",
        [nameof(QuestActObjLevel)] =
            "missing level-grind composition",
        [nameof(QuestActEtcItemObtain)] =
            "missing generic item-obtain source resolution",
        [nameof(QuestActCheckTimer)] =
            "missing timed-objective budget policy (quest fails hard on timer expiry)",
        [nameof(QuestActSupplyRemoveItem)] =
            "not an objective act (supply-side) — classifier gap",
    };

    private static string GapReason(string actTypeName)
    {
        return KnownPrimitiveGaps.TryGetValue(actTypeName, out var gap)
            ? $"{actTypeName}: {gap}"
            : $"{actTypeName}: no known pursuit strategy and no named primitive mapping — " +
              "extend LevelingLoopScenario.KnownPrimitiveGaps";
    }

    /// <summary>
    /// Data-driven objective classification off the REAL quest template.
    /// Returns a fail-closed failure, or null when the quest reached Ready.
    /// </summary>
    private static LoopRunResult? PursueObjectives(GameplayActor actor, LoopOptions opts,
        IKillCreditSeam? killSeam, uint questId, QuestTemplate template, PerceptionSnapshot perception)
    {
        var progressActs = template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .ToList();

        // Delivery quests (no Progress acts) skip straight to turn-in.
        if (progressActs.Count == 0)
            return null;

        foreach (var act in progressActs)
        {
            switch (act)
            {
                case QuestActObjItemGather gather:
                    var failure = GatherLeg(actor, opts, questId, gather, perception);
                    if (failure != null)
                        return Fail($"OBJECTIVES:gather({gather.ItemId})", ActorFailureReason.Navigation,
                            failure, actor, null);
                    break;

                default:
                    return Fail($"OBJECTIVES:{act.GetType().Name}", ActorFailureReason.WrongDecision,
                        "unsupported objective type — FAIL-CLOSED (progress would be fake): " +
                        GapReason(act.GetType().Name),
                        actor, null);
            }
        }

        // Objectives met → evaluate the step machine once (the same call the
        // world pipeline makes after events) and require Ready before any
        // turn-in is attempted.
        var advance = actor.AdvanceQuest(questId);
        if (advance.State != ActorLifecycleState.Completed)
        {
            return Fail("OBJECTIVES:advance", ActorFailureReason.StateTransition,
                $"advance after objectives refused: {advance.Detail}", actor, null);
        }

        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest is { Status: not QuestStatus.Ready })
        {
            return Fail("OBJECTIVES", ActorFailureReason.WrongDecision,
                $"objectives pursued but quest {questId} did not reach Ready " +
                $"(step {quest.Step}, status {quest.Status}) — refusing to turn in", actor, null);
        }

        return null;
    }

    /// <summary>
    /// The gather leg: source doodads resolved DATA-DRIVEN from the act's
    /// HighlightDoodadId among PERCEIVED doodads; each interaction is a real
    /// InteractWith (engine grants the item; the engine's OWN
    /// DoItemsAcquiredEvents → OnItemGather path credits the objective —
    /// the loop never fires quest events by hand).
    /// </summary>
    private static string? GatherLeg(GameplayActor actor, LoopOptions opts, uint questId,
        QuestActObjItemGather gather, PerceptionSnapshot perception)
    {
        if (gather.HighlightDoodadId == 0)
        {
            return $"quest {questId} gathers item {gather.ItemId} with NO highlight_doodad_id — " +
                   "missing gather-source resolution primitive (source is not data-discoverable)";
        }

        if (!perception.DoodadObjIdsByTemplate.TryGetValue(gather.HighlightDoodadId, out var sources) ||
            sources.Count == 0)
        {
            return $"quest {questId} needs item {gather.ItemId} ×{gather.Count} from doodad template " +
                   $"{gather.HighlightDoodadId}, but no such source was PERCEIVED nearby";
        }

        var attemptsLeft = opts.MaxAttemptsPerGatherSource * sources.Count;
        var sourceIndex = 0;
        while (actor.Character.Inventory?.GetItemsCount(gather.ItemId) < gather.Count)
        {
            if (attemptsLeft-- <= 0)
            {
                return $"gather exhausted {opts.MaxAttemptsPerGatherSource} attempt(s) per source across " +
                       $"{sources.Count} source(s) of item {gather.ItemId} without reaching ×{gather.Count}";
            }

            var sourceObjId = sources[sourceIndex % sources.Count];
            sourceIndex++;
            var interact = actor.InteractWith(sourceObjId);
            if (interact.State != ActorLifecycleState.Completed)
            {
                return $"InteractWith gather source {sourceObjId} refused: {interact.Detail}";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ turn-in

    private static string DescribePursuit(QuestTemplate template)
    {
        var acts = template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).ToList();
        return acts.Count == 0 ? "delivery" : string.Join("+", acts.Select(a => a.GetType().Name));
    }

    /// <summary>
    /// Resolves the reporter DATA-DRIVEN (Ready components' ConReportNpc /
    /// ConReportDoodad acts) among PERCEIVED targets and turns the quest in
    /// through the real packet path. Auto-report quests use AutoTurnIn.
    /// Returns a fail-closed failure, or null when completed.
    /// </summary>
    private static LoopRunResult? TurnIn(GameplayActor actor, uint questId, QuestTemplate template,
        PerceptionSnapshot perception)
    {
        var readyActs = template.GetComponents(QuestComponentKind.Ready)
            .SelectMany(c => c.ActTemplates).ToList();
        var reportNpc = readyActs.OfType<QuestActConReportNpc>().FirstOrDefault();
        var reportDoodad = readyActs.OfType<QuestActConReportDoodad>().FirstOrDefault();

        ActorRequest request;
        if (reportNpc != null)
        {
            if (!perception.NpcObjIdsByTemplate.TryGetValue(reportNpc.NpcId, out var reporterObjId))
            {
                return Fail("TURN-IN", ActorFailureReason.Navigation,
                    $"report NPC {reportNpc.NpcId} for quest {questId} not among perceived targets", actor, null);
            }

            request = actor.TurnInQuest(questId, reporterObjId);
        }
        else if (reportDoodad != null)
        {
            if (!perception.DoodadObjIdsByTemplate.TryGetValue(reportDoodad.DoodadId, out var reporterObjIds))
            {
                return Fail("TURN-IN", ActorFailureReason.Navigation,
                    $"report doodad {reportDoodad.DoodadId} for quest {questId} not among perceived targets", actor, null);
            }

            request = actor.TurnInAtDoodad(questId, reporterObjIds[0]);
        }
        else
        {
            request = actor.AutoTurnInQuest(questId);
        }

        if (request.State != ActorLifecycleState.Completed)
        {
            return Fail("TURN-IN", ActorFailureReason.RejectedAction,
                $"turn-in of quest {questId} failed: {request.Detail}", actor, null);
        }

        if (!actor.Character.Quests!.HasQuestCompleted(questId))
        {
            return Fail("TURN-IN", ActorFailureReason.WrongDecision,
                $"turn-in executed but quest {questId} did not complete (still active)", actor, null);
        }

        return null;
    }

    private static LoopRunResult Fail(string stage, ActorFailureReason reason, string detail,
        GameplayActor actor, List<LinkRecord>? links)
    {
        Logger.Debug("leveling loop FAILED at {Stage}: {Detail}", stage, detail);
        return new LoopRunResult
        {
            Scenario = ScenarioName,
            Passed = false,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            Links = links ?? [],
            TraceRecords = [.. actor.AuditTrace]
        };
    }
}
