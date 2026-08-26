using System.Numerics;

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
///   - QuestActObjMonsterHunt / MonsterGroupHunt → resolve the hunt
///     targets DATA-DRIVEN from the act (NpcId, or monster-group id via
///     QuestManager.CheckGroupNpc) among PERCEIVED hostiles (alive +
///     BaseUnit.CanAttack — the adventurer-spike selection convention),
///     SetTarget → cast rotation → Loot each corpse. Kill credit flows
///     through the REAL engine path either way: LIVE = real cast damage
///     (Npc.DoDie → QuestManager.DoOnMonsterHuntEvents); RIG = the
///     documented synthetic kill through <see cref="IKillCreditSeam"/>
///     (the exact entry point Npc.DoDie calls for a character killer).
///   - everything else             → fail closed (see GapReason).
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

    /// <summary>
    /// Quest 1652 "난폭한 선돌 수호자 퇴치" — board-accepted single-template
    /// hunt: accept at notice-board doodad 8055 (Level ≥ 3 + mother faction
    /// on start component 7861), Progress = MonsterHunt npc 7673 ×3
    /// (component 7862), NO Ready component → auto-completes.
    /// NOTE: Solzreed's other band hunts are spike-covered (250),
    /// score-gated in this engine (266: score=100 caps the objective at 9
    /// against Count=20 — honestly uncompletable), or kill-accepted (2374).
    /// </summary>
    public const uint SeedQuestBoardHuntId = 1652;
    public const uint SeedBoardDoodadTemplateId = 8055;
    public const uint SeedBoardHuntTargetNpcTemplateId = 7673;

    /// <summary>
    /// Quest 329 "불곰을 조심해!" — board-accepted GROUP hunt: accept at
    /// doodad 144 (board template 5048; Level ≥ 2 + mother faction on start
    /// component 1487), Progress = MonsterGroupHunt act 150 → group 153 ×3
    /// (npcs 7674 성난 불곰 / 7648 배고픈 불곰), NO Ready component →
    /// auto-completes. Verified canonical 1.2, 2026-08-25.
    /// </summary>
    public const uint SeedQuestGroupHuntId = 329;
    public const uint SeedGroupHuntBoardDoodadTemplateId = 5048;
    public const uint SeedGroupHuntTargetNpcTemplateA = 7674;
    public const uint SeedGroupHuntTargetNpcTemplateB = 7648;

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

        // ---- hunt-leg parameters (composed 2026-08-25 slice) ----

        /// <summary>
        /// Skill ids in priority order — the hunt leg casts the rotation
        /// once per burst round (Rejected ones skipped and recorded), the
        /// adventurer-spike combo-chain shape. Live default: 18131 LEADS
        /// (the BUG-016-fixed first hit), 18134 fallback — the spike's
        /// proven live rotation. Rigs inject a fixture skill.
        /// </summary>
        public uint[] CastRotation { get; init; } =
            [AdventurerSpikeScenario.TripleSlashSkillId, AdventurerSpikeScenario.TripleSlashFinisherSkillId];

        /// <summary>Max cast-burst rounds on one target per engagement.</summary>
        public int MaxBurstCasts { get; init; } = 8;

        /// <summary>
        /// Max distance (m) from which the rotation may start — beyond it
        /// the hunt leg closes in with MoveToUnit first. Default 3: the
        /// live rotation lead reaches 4 m (spike-proven slack).
        /// </summary>
        public float HuntEngageRange { get; init; } = 3f;

        /// <summary>Bounded re-observe/re-engage rounds per hunt act.</summary>
        public int MaxHuntRounds { get; init; } = 32;

        /// <summary>
        /// Rounds of executed casts with zero net damage on one target
        /// before it is excluded from reselection (leash-stuck/undamageable
        /// prey — exclusion only, NEVER a kill credit; spike E-M7-9).
        /// </summary>
        public int NoProgressSkipRounds { get; init; } = 3;

        /// <summary>Bounded re-observe retries when no attackable target is visible.</summary>
        public int NoTargetRetries { get; init; } = 4;

        /// <summary>Move-leg pace (m/s) and per-leg budget for close-in legs.</summary>
        public float TravelSpeed { get; init; } = 6f;
        public TimeSpan TravelTimeout { get; init; } = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Optional driver for in-flight requests (move legs). Rigs inject
        /// their deterministic driver; when null the loop ticks the actor
        /// inline (bounded by TravelTimeout) — deterministic headless AND
        /// correct for synchronous dispatch.
        /// </summary>
        public Func<GameplayActor, ActorRequest, ActorRequest>? Drive { get; init; }
    }

    /// <summary>
    /// Kill-credit seam for the hunt leg. LIVE runs pass null — real cast
    /// damage must down the prey (Npc.DoDie → DoOnMonsterHuntEvents
    /// credits). RIGS implement the documented synthetic kill through the
    /// REAL QuestManager.DoOnMonsterHuntEvents entry point (adventurer-spike
    /// convention): bare fixture NPCs carry no template/AI/spawner
    /// scaffolding for a full DoDie. Returns true when the target is down.
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
        [nameof(QuestActObjZoneKill)] =
            "missing zone-scoped kill attribution (zone-gated victim/killer composition — the plain hunt leg does not cover it)",
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

                case QuestActObjMonsterHunt hunt:
                    {
                        var (huntFailure, huntReason) = HuntLeg(actor, opts, killSeam, questId,
                            hunt, hunt.NpcId, 0, perception);
                        if (huntFailure != null)
                            return Fail($"OBJECTIVES:hunt({hunt.NpcId})", huntReason, huntFailure, actor, null);
                        break;
                    }

                case QuestActObjMonsterGroupHunt groupHunt:
                    {
                        var (groupFailure, groupReason) = HuntLeg(actor, opts, killSeam, questId,
                            groupHunt, null, groupHunt.QuestMonsterGroupId, perception);
                        if (groupFailure != null)
                            return Fail($"OBJECTIVES:group-hunt({groupHunt.QuestMonsterGroupId})",
                                groupReason, groupFailure, actor, null);
                        break;
                    }


                default:
                    return Fail($"OBJECTIVES:{act.GetType().Name}", ActorFailureReason.WrongDecision,
                        "unsupported objective type — FAIL-CLOSED (progress would be fake): " +
                        GapReason(act.GetType().Name),
                        actor, null);
            }
        }

        // Objectives met → evaluate the step machine once (the same call the
        // world pipeline makes after events) and require a turn-in-able
        // state before any turn-in is attempted: Ready (report quests) or
        // Completed (auto-complete quests — the advance alone drove them
        // through their reward step).
        var advance = actor.AdvanceQuest(questId);
        if (advance.State != ActorLifecycleState.Completed)
        {
            return Fail("OBJECTIVES:advance", ActorFailureReason.StateTransition,
                $"advance after objectives refused: {advance.Detail}", actor, null);
        }

        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest is { Status: not QuestStatus.Ready and not QuestStatus.Completed })
        {
            return Fail("OBJECTIVES", ActorFailureReason.WrongDecision,
                $"objectives pursued but quest {questId} did not reach a completable state " +
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

    /// <summary>
    /// The hunt leg: targets resolved DATA-DRIVEN from the act among
    /// PERCEIVED hostiles — single-template hunts match <paramref name="targetNpcTemplateId"/>
    /// (the act's NpcId), group hunts match QuestManager.CheckGroupNpc on
    /// the act's monster-group id; every candidate must be ALIVE and
    /// CanAttack (BaseUnit faction check — the adventurer-spike selection
    /// convention). Each engagement is a real SetTarget → Cast rotation →
    /// Loot. Kill credit flows through the REAL engine path either way:
    /// LIVE (killSeam == null) the cast rotation's real damage must down
    /// the prey (Npc.DoDie → DoOnMonsterHuntEvents credits); rigs apply
    /// the documented synthetic kill through <see cref="IKillCreditSeam"/>.
    /// Objective progress is read back from the REAL quest state
    /// (act.GetObjective) every round — kills are never counted by hand.
    /// A target pinned at full HP across NoProgressSkipRounds cast rounds
    /// is excluded from reselection — exclusion only, NEVER a credit.
    /// </summary>
    private static (string? Failure, ActorFailureReason Reason) HuntLeg(GameplayActor actor, LoopOptions opts,
        IKillCreditSeam? killSeam, uint questId, QuestActTemplate act,
        uint? targetNpcTemplateId, uint monsterGroupId, PerceptionSnapshot perception)
    {
        var character = actor.Character;
        var quest = character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return ($"quest {questId} left ActiveQuests before hunt pursuit started", ActorFailureReason.StateTransition);

        // The caller's snapshot is only the FIRST sweep's evidence; every
        // round below re-observes (the spike loop's proven shape).
        _ = perception;

        var targetLabel = targetNpcTemplateId is { } template
            ? $"npc template {template}"
            : $"monster group {monsterGroupId}";
        var excluded = new HashSet<uint>();
        var noProgress = new Dictionary<uint, int>();
        var noTargetRounds = 0;
        var roundsLeft = opts.MaxHuntRounds;

        while (act.GetObjective(quest) < act.Count)
        {
            if (roundsLeft-- <= 0)
            {
                return ($"hunt budget exhausted ({opts.MaxHuntRounds} rounds): objective at " +
                        $"{act.GetObjective(quest)}/{act.Count} of {targetLabel} for quest {questId}",
                    ActorFailureReason.Starvation);
            }

            var observation = actor.Observe();
            var target = SelectHuntTarget(character, observation, targetNpcTemplateId, monsterGroupId, excluded);
            if (target == null)
            {
                noTargetRounds++;
                if (noTargetRounds > opts.NoTargetRetries)
                {
                    return ($"no attackable hunt target ({targetLabel}) perceived after " +
                            $"{opts.NoTargetRetries} re-observe rounds (nearby npcs: " +
                            $"[{string.Join(", ", observation.NearbyNpcObjIds)}])",
                        ActorFailureReason.Starvation);
                }

                continue;
            }
            noTargetRounds = 0;

            var targetRequest = actor.SetTarget(target.ObjId);
            if (targetRequest.State != ActorLifecycleState.Completed)
            {
                return ($"SetTarget on hunt target {target.ObjId} refused: {targetRequest.Detail}",
                    ActorFailureReason.RejectedAction);
            }

            // Distance maintenance: beyond the engage band, close in first
            // and re-observe from the new position next round (melee default).
            var distance = Vector3.Distance(character.Transform.World.Position, target.Transform.World.Position);
            if (distance > opts.HuntEngageRange)
            {
                var closeIn = DriveRequest(actor, opts,
                    actor.MoveToUnit(target.ObjId, opts.TravelSpeed, opts.TravelTimeout));
                if (closeIn.State != ActorLifecycleState.Completed)
                {
                    return ($"close-in move onto hunt target {target.ObjId} did not complete: " +
                            $"{closeIn.State} ({closeIn.Detail ?? "n/a"})",
                        closeIn.Failure ?? ActorFailureReason.Navigation);
                }

                continue;
            }

            // Cast-burst engagement: the rotation runs as a chain each burst
            // round (Rejected skills are skipped); the round ends early when
            // real damage drops the target or the seam applies its credit.
            var hpRoundStart = target.Hp;
            var executedAnyCast = false;
            var down = false;
            for (var burst = 0; burst < opts.MaxBurstCasts && !down; burst++)
            {
                var roundExecuted = false;
                foreach (var skillId in opts.CastRotation)
                {
                    if (target.Hp <= 0)
                        break; // dropped mid-chain — stop casting
                    var cast = actor.Cast(skillId, target.ObjId);
                    if (cast.State != ActorLifecycleState.Rejected)
                        roundExecuted = true;
                }

                if (!roundExecuted)
                    break; // whole rotation refused — re-observe next round
                executedAnyCast = true;

                // LIVE: real damage only. RIG: seam credit (real damage still wins).
                down = target.Hp <= 0 || (killSeam?.TryKill(actor, target) ?? false);
            }

            if (!down)
            {
                // NO-PROGRESS SKIP (spike E-M7-9): casts executed but zero net
                // damage — leash-stuck/undamageable prey is EXCLUDED from
                // reselection after NoProgressSkipRounds (never credited).
                if (executedAnyCast && target.Hp >= hpRoundStart)
                {
                    var pinned = noProgress.GetValueOrDefault(target.ObjId) + 1;
                    noProgress[target.ObjId] = pinned;
                    if (pinned >= opts.NoProgressSkipRounds)
                    {
                        excluded.Add(target.ObjId);
                        noProgress.Remove(target.ObjId);
                    }
                }
                else
                {
                    noProgress.Remove(target.ObjId); // damage landed (or nothing executed) — reset
                }

                continue;
            }

            // DOWN: loot the fresh corpse through the real contract path. A
            // Rejected loot is tolerated (recorded, never fatal) — not every
            // hunt objective drops loot.
            excluded.Add(target.ObjId);
            noProgress.Remove(target.ObjId);
            var loot = actor.Loot(target.ObjId);
            if (loot.State == ActorLifecycleState.Rejected)
                Logger.Debug("hunt leg: loot of corpse {ObjId} rejected ({Detail}) — tolerated", target.ObjId, loot.Detail);
        }

        return (null, ActorFailureReason.None);
    }

    /// <summary>
    /// Hostile-selection primitive (adventurer-spike SelectHostile
    /// convention): the nearest ALIVE NPC the actor can attack
    /// (BaseUnit.CanAttack — faction-based; bare rig NPCs read attackable)
    /// whose template matches the hunt act — directly (single-template
    /// hunt) or through QuestManager.CheckGroupNpc (monster-group hunt).
    /// Observe-driven ONLY: candidates come from the observation's
    /// nearby-NPC list, never a world scan.
    /// </summary>
    private static Npc? SelectHuntTarget(Character character, ActorObservation observation,
        uint? targetNpcTemplateId, uint monsterGroupId, IReadOnlySet<uint> excluded)
    {
        Npc? best = null;
        var bestDistance = float.MaxValue;
        var position = character.Transform.World.Position;
        foreach (var objId in observation.NearbyNpcObjIds)
        {
            if (excluded.Contains(objId))
                continue;
            if (character.ParentWorld?.GetNpc(objId) is not { } npc)
                continue;
            if (npc.Hp <= 0 || excluded.Contains(objId))
                continue;

            var matchesTemplate = targetNpcTemplateId is { } template && npc.TemplateId == template;
            var matchesGroup = monsterGroupId != 0 && QuestManager.Instance.CheckGroupNpc(monsterGroupId, npc.TemplateId);
            if ((!matchesTemplate && !matchesGroup) || !character.CanAttack(npc))
                continue;

            var distance = Vector3.DistanceSquared(position, npc.Transform.World.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = npc;
            }
        }

        return best;
    }

    /// <summary>
    /// Drives one in-flight request to a terminal state. Rigs inject their
    /// deterministic driver via <see cref="LoopOptions.Drive"/>; when null
    /// the loop ticks the actor inline (bounded by TravelTimeout) — the
    /// rig-spike Drive convention, deterministic headless.
    /// </summary>
    private static ActorRequest DriveRequest(GameplayActor actor, LoopOptions opts, ActorRequest request)
    {
        if (opts.Drive != null)
            return opts.Drive(actor, request);

        var deadline = Environment.TickCount64 + (long)opts.TravelTimeout.TotalMilliseconds;
        while (!request.IsTerminal && Environment.TickCount64 < deadline)
            actor.Tick(TimeSpan.FromMilliseconds(20));
        return request;
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

        // Auto-complete quests (no Ready component — the objective advance
        // alone drives them to completion) drop from ActiveQuests during
        // pursuit; that IS the turn-in. Anything else still active goes
        // through the real report paths below.
        if (actor.Character.Quests?.ActiveQuests.ContainsKey(questId) != true)
        {
            if (actor.Character.Quests!.HasQuestCompleted(questId))
                return null;

            return Fail("TURN-IN", ActorFailureReason.StateTransition,
                $"quest {questId} is neither active nor completed — nothing to turn in", actor, null);
        }

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
