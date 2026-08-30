using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
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
///   - QuestActObjItemUse          → consume the act's ItemId through the
///     real UseItem contract until the objective Count is credited by the
///     engine's OnItemUse event.
///   - QuestActObjInteraction      → resolve HighlightDoodadId (or DoodadId)
///     among PERCEIVED doodads and execute InteractWith. Skill-bound funcs
///     enter through CSStartSkillPacket's Skill.Use path, so the engine's
///     InteractionEffect emits OnInteraction credit; no quest event is faked.
///   - QuestActObjMonsterHunt / MonsterGroupHunt → resolve the hunt
///     targets DATA-DRIVEN from the act (NpcId, or monster-group id via
///     QuestManager.CheckGroupNpc) among PERCEIVED hostiles (alive +
///     BaseUnit.CanAttack — the adventurer-spike selection convention),
///     SetTarget → cast rotation → Loot each corpse. Kill credit flows
///     through the REAL engine path either way: LIVE = real cast damage
///     (Npc.DoDie → QuestManager.DoOnMonsterHuntEvents); RIG = the
///     documented synthetic kill through <see cref="IKillCreditSeam"/>
///     (the exact entry point Npc.DoDie calls for a character killer).
///   - QuestActObjAggro              → resolve the acceptor NPC template
///     named by the live quest instance, require that the perceived target
///     has the owner in its real aggro table at a configured rank, then use
///     the same SetTarget → cast → kill path. Aggro credit is read from the
///     live objective after the REAL OnKill event (or the explicit
///     <see cref="IAggroKillCreditSeam"/> rig seam); no counter is written.
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

    /// <summary>
    /// Kill-credit seam used by an aggro objective in a deterministic rig.
    /// Unlike the ordinary hunt seam, this entry point MUST execute the
    /// target's normal death path so Character.Events.OnKill receives the
    /// slain NPC as OnKillArgs.Target. Returning true without that event is
    /// deliberately rejected by AggroLeg.
    /// </summary>
    public interface IAggroKillCreditSeam : IKillCreditSeam
    {
        bool TryKillAggro(GameplayActor actor, Npc target);
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

    /// <summary>Runs the autonomous loop on an embodied character, adapted for the scenario runner.</summary>
    public static BotScenarioRunner.ScenarioRunResult RunAsScenario(Character character, LoopOptions? options = null, IKillCreditSeam? killSeam = null)
    {
        var res = Run(character, options, killSeam);
        return new BotScenarioRunner.ScenarioRunResult
        {
            Template = ScenarioName,
            Passed = res.Passed,
            FailStage = res.FailStage,
            Failure = res.Failure,
            FailReason = res.FailReason,
            RigNotes = res.Notes,
            Stages = res.Links.Select(l => new BotScenarioRunner.ScenarioStageVerdict(
                $"Quest-{l.QuestId}", 1, "completed", l.Pursuit, $"Exp {l.ExperienceBefore}→{l.ExperienceAfter}")).ToList(),
            Criteria = [new BotScenarioRunner.CriterionVerdict("leveling-chain-complete", res.Passed, res.FailReason)],
            TraceRecords = res.TraceRecords,
            ActorRequests = res.TraceRecords.Count
        };
    }

    /// <summary>Runs the autonomous loop on an embodied character.</summary>
    public static LoopRunResult Run(Character character, LoopOptions? options = null, IKillCreditSeam? killSeam = null)
    {
        var opts = options ?? new LoopOptions();
        var actor = new GameplayActor(character);
        var links = new List<LinkRecord>();
        var notes = new List<string>();

        try
        {
            for (var linkIndex = 1; linkIndex <= opts.MaxLinks; linkIndex++)
            {
                // ---------------------------------------------------- 1. PERCEIVE
                var perception = Perceive(actor);
                var bandOfferings = perception.Offerings
                    .Where(o => o.Level >= opts.BandMin && o.Level <= opts.BandMax)
                    .ToList();

                // Engine auto-started quests (engage-combat channel) are
                // already active — pursue and turn them in without an
                // explicit accept dispatch. Lowest level first, then id.
                var autoStartedInBand = perception.AutoStartedQuestIds
                    .Select(id => (Id: id, Level: QuestManager.Instance.GetTemplate(id)?.Level ?? 0))
                    .Where(q => q.Level >= opts.BandMin && q.Level <= opts.BandMax)
                    .OrderBy(q => q.Level)
                    .ThenBy(q => q.Id)
                    .ToList();

                if (bandOfferings.Count == 0 && autoStartedInBand.Count == 0)
                {
                    return Fail("PERCEIVE", ActorFailureReason.Starvation,
                        $"no discoverable quest offerings within band [{opts.BandMin}..{opts.BandMax}] " +
                        $"from {perception.PerceivedNpcCount} NPC(s)/{perception.PerceivedDoodadCount} board(s) " +
                        $"({perception.TotalOfferingsSeen} offering(s) seen, all out of band or gated)", actor, links);
                }

                // ---------------------------------------------------- 2. DECIDE
                // The proposal contract preserves the existing policy:
                // lowest-level offering wins, then lowest quest id. This is
                // a legal, explainable decision over the immutable perception
                // snapshot; personality has no weight in this compatibility
                // path.
                var chosen = (QuestOffering?)null;
                if (bandOfferings.Count > 0)
                {
                    var proposals = bandOfferings.Select(offering => new BotDecisionProposal(
                        goal: "leveling.accept-quest",
                        action: ActorActionType.AcceptQuest,
                        targetId: offering.QuestId,
                        expectedPostcondition: new BotProposalPostcondition(
                            $"quest {offering.QuestId} is active",
                            observed => observed.ActiveQuestIds.Contains(offering.QuestId)),
                        idempotencyKey: $"leveling:{character.Id}:{linkIndex}:{offering.QuestId}",
                        timeout: TimeSpan.FromSeconds(30),
                        rationale: $"lowest offered level in [{opts.BandMin}..{opts.BandMax}]",
                        policyVersion: "leveling-v1",
                        priority: opts.BandMax - offering.Level,
                        tieBreakKey: offering.QuestId.ToString("D10"),
                        payload: offering,
                        hardPreconditions:
                        [
                            new BotProposalPrecondition(
                                "quest-not-active",
                                observed => !observed.ActiveQuestIds.Contains(offering.QuestId))
                        ])).ToList();
                    var decision = BotDecisionSelector.Select(perception.Context, proposals);
                    if (!decision.HasProposal)
                    {
                        return Fail("DECIDE", ActorFailureReason.WrongDecision,
                            $"no legal discovered quest proposal: {decision.Explanation}", actor, links);
                    }

                    chosen = (QuestOffering)decision.Proposal!.Payload!;

                    // ---------------------------------------------------- 3. ACCEPT
                    // Dispatch remains the existing GameplayActor path. The
                    // cycle observes the terminal state before the next loop
                    // iteration replans from a fresh perception sweep.
                    var execution = BotDecisionCycle.Execute(actor, perception.Context,
                        decision.Proposal,
                        static (gameplayActor, proposal) =>
                        {
                            var offering = (QuestOffering)proposal.Payload!;
                            return gameplayActor.AcceptQuest(
                                offering.QuestId, offering.AcceptorType, offering.AcceptorId,
                                proposal.IdempotencyKey);
                        });
                    var accept = execution.Request;
                    if (accept.State != ActorLifecycleState.Completed || !execution.ExpectedPostconditionSatisfied)
                    {
                        return Fail("ACCEPT", ActorFailureReason.RejectedAction,
                            $"accept of discovered quest {chosen.QuestId} refused or did not reach expected state: " +
                            $"{accept.Detail ?? execution.Proposal.ExpectedPostcondition.Description}", actor, links);
                    }
                }
                else
                {
                    // Engine auto-started quests (engage-combat channel) are
                    // already active — no accept dispatch is legal or needed.
                    // The audit trace records the pursuit note instead of a
                    // synthetic AcceptQuest record.
                    var auto = autoStartedInBand[0];
                    chosen = new QuestOffering(auto.Id, (byte)auto.Level, QuestAcceptorType.Unknown, 0);
                    notes.Add($"quest {auto.Id} auto-started (no explicit accept) — pursuing");
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
                // Auto-equip any reward upgrades
                EquipUpgrades(actor);

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
                Notes = notes.Count > 0
                    ? [.. notes, $"completed {links.Count} chained quest(s) unprompted; " +
                                 $"total exp gained {links.Sum(l => l.ExperienceAfter - l.ExperienceBefore)}"]
                    : [$"completed {links.Count} chained quest(s) unprompted; " +
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
        int PerceivedNpcCount, int PerceivedDoodadCount,
        BotObservedContext Context,
        List<uint> AutoStartedQuestIds)
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

        // Self-perceived quest offers (Item in bag, Sphere, Level channels).
        var selfRequest = actor.DiscoverSelfQuests();
        if (selfRequest.State == ActorLifecycleState.Completed && selfRequest.Result is QuestSelfDiscoveryResult selfFound)
        {
            offerings.AddRange(selfFound.Offerings);
        }

        // Engine auto-started quests (e.g. EngageCombatGiveQuestId on first
        // aggro — Unit.AddUnitAggro → CharacterQuests.AddQuestFromNpc). These
        // are already ACTIVE, so they are not discoverable offerings; surface
        // them as a fourth perception channel so the loop can pursue and
        // turn them in without an explicit accept dispatch.
        var autoStarted = new List<uint>();
        if (actor.Character.Quests?.ActiveQuests is { } activeQuests)
        {
            foreach (var questId in activeQuests.Keys)
            {
                if (offerings.Any(o => o.QuestId == questId))
                    continue;
                autoStarted.Add(questId);
            }
        }

        return new PerceptionSnapshot(offerings, npcByTemplate, doodadsByTemplate,
            observation.NearbyNpcObjIds.Count, doodadCount, BotObservedContext.From(observation),
            autoStarted);
    }

    // ------------------------------------------------------------------ pursue

    private static readonly Dictionary<string, string> KnownPrimitiveGaps = new()
    {
        [nameof(QuestActObjItemGroupUse)] =
            "missing item-use pursuit composition (UseItem primitive exists; group resolution uncomposed)",
        [nameof(QuestActObjItemGroupGather)] =
            "missing item-group source resolution (HighlightDoodadId is single-template; group sources unmapped)",
        [nameof(QuestActObjAggro)] =
            "partial: NPC-acceptor aggro rank is pursued; component forms without a non-zero " +
            "acceptor template or a kill path that emits OnKillArgs.Target remain unsupported",
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
                    {
                        var failure = GatherLeg(actor, opts, questId, gather, perception);
                        if (failure != null)
                            return Fail($"OBJECTIVES:gather({gather.ItemId})", ActorFailureReason.Navigation,
                                failure, actor, null);
                        break;
                    }

                case QuestActObjItemUse itemUse:
                    {
                        var useFailure = UseItemLeg(actor, questId, itemUse);
                        if (useFailure != null)
                            return Fail($"OBJECTIVES:item-use({itemUse.ItemId})",
                                ActorFailureReason.RejectedAction, useFailure, actor, null);
                        break;
                    }

                case QuestActObjInteraction interaction:
                    {
                        var (interactionFailure, interactionReason) =
                            InteractionLeg(actor, questId, interaction, perception);
                        if (interactionFailure != null)
                            return Fail($"OBJECTIVES:interaction({interaction.DoodadId})",
                                interactionReason, interactionFailure, actor, null);
                        break;
                    }
                case QuestActObjAggro aggro:
                    {
                        var (aggroFailure, aggroReason) = AggroLeg(actor, opts, killSeam,
                            questId, aggro, perception);
                        if (aggroFailure != null)
                            return Fail($"OBJECTIVES:aggro({aggro.DetailId})", aggroReason,
                                aggroFailure, actor, null);
                        break;
                    }


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

                case QuestActObjTalk talk:
                    {
                        var talkFailure = TalkLeg(actor, opts, questId, talk.NpcId, 0, perception);
                        if (talkFailure != null)
                            return Fail($"OBJECTIVES:talk({talk.NpcId})", ActorFailureReason.Navigation,
                                talkFailure, actor, null);
                        break;
                    }

                case QuestActObjTalkNpcGroup groupTalk:
                    {
                        var groupTalkFailure = TalkLeg(actor, opts, questId, 0, groupTalk.NpcGroupId, perception);
                        if (groupTalkFailure != null)
                            return Fail($"OBJECTIVES:group-talk({groupTalk.NpcGroupId})", ActorFailureReason.Navigation,
                                groupTalkFailure, actor, null);
                        break;
                    }

                case QuestActObjSphere sphere:
                    {
                        var sphereFailure = SphereLeg(actor, opts, questId, sphere, perception);
                        if (sphereFailure != null)
                            return Fail($"OBJECTIVES:sphere({sphere.SphereId})", ActorFailureReason.Navigation,
                                sphereFailure, actor, null);
                        break;
                    }

                case QuestActObjCraft craft:
                    {
                        var (craftFailure, craftReason) = CraftLeg(actor, opts, questId, craft, perception);
                        if (craftFailure != null)
                            return Fail($"OBJECTIVES:craft({craft.CraftId})", craftReason,
                                craftFailure, actor, null);
                        break;
                    }

                case QuestActObjCinema cinema:
                    {
                        var cinemaFailure = CinemaLeg(actor, questId, cinema);
                        if (cinemaFailure != null)
                            return Fail($"OBJECTIVES:cinema({cinema.CinemaId})", ActorFailureReason.RejectedAction,
                                cinemaFailure, actor, null);
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
        if (actor.Character.Quests?.HasQuestCompleted(questId) == true)
            return null; // auto-completed during objective events

        var advance = actor.AdvanceQuest(questId);
        if (advance.State != ActorLifecycleState.Completed)
        {
            return Fail("OBJECTIVES:advance", ActorFailureReason.StateTransition,
                $"advance after objectives refused: {advance.Detail}", actor, null);
        }

        if (actor.Character.Quests?.HasQuestCompleted(questId) == true)
            return null; // auto-completed during advance

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
    /// The item-use leg: consume the exact item named by the objective through
    /// the real UseItem contract. Objective credit is read from the live quest
    /// state, which is updated by the engine's OnItemUse event; inventory
    /// depletion or a rejected use fails closed rather than faking credit.
    /// </summary>
    private static string? UseItemLeg(GameplayActor actor, uint questId, QuestActObjItemUse use)
    {
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return $"quest {questId} left ActiveQuests before item-use pursuit started";

        var usesRemaining = Math.Max(0, use.Count - use.GetObjective(quest));
        for (var useIndex = 0; useIndex < usesRemaining; useIndex++)
        {
            if (actor.Character.Quests.HasQuestCompleted(questId))
                break;

            var available = actor.Character.Inventory?.GetItemsCount(use.ItemId) ?? 0;
            if (available <= 0)
            {
                return $"quest {questId} requires item {use.ItemId} ×{use.Count}, " +
                       $"but inventory has none while objective is {use.GetObjective(quest)}/{use.Count}";
            }

            var request = actor.UseItem(use.ItemId);
            if (request.State != ActorLifecycleState.Completed)
            {
                return $"UseItem {use.ItemId} refused for quest {questId}: {request.Detail}";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves an interaction objective's source from perceived doodads and
    /// drives the ordinary interaction skill pipeline. Progress must increase
    /// in the live quest state after every successful action.
    /// </summary>
    private static (string? Failure, ActorFailureReason Reason) InteractionLeg(
        GameplayActor actor, uint questId, QuestActObjInteraction interaction,
        PerceptionSnapshot perception)
    {
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return ($"quest {questId} left ActiveQuests before interaction pursuit started",
                ActorFailureReason.StateTransition);

        var sourceTemplateId = interaction.HighlightDoodadId > 0
            ? interaction.HighlightDoodadId
            : interaction.DoodadId;
        if (sourceTemplateId == 0)
        {
            return ($"quest {questId} interaction objective {interaction.DetailId} names no doodad source",
                ActorFailureReason.Navigation);
        }

        if (!perception.DoodadObjIdsByTemplate.TryGetValue(sourceTemplateId, out var sources) ||
            sources.Count == 0)
        {
            return ($"quest {questId} needs interaction {interaction.WorldInteractionId} on doodad template " +
                    $"{sourceTemplateId}, but no such source was PERCEIVED nearby",
                ActorFailureReason.Navigation);
        }

        var sourceIndex = 0;
        while (interaction.GetObjective(quest) < interaction.Count)
        {
            if (sourceIndex >= sources.Count)
            {
                return ($"interaction exhausted {sources.Count} perceived source(s) of doodad template " +
                        $"{sourceTemplateId} with objective at " +
                        $"{interaction.GetObjective(quest)}/{interaction.Count}",
                    ActorFailureReason.Starvation);
            }

            var objectiveBefore = interaction.GetObjective(quest);
            var sourceObjId = sources[sourceIndex++];
            var request = actor.InteractWith(sourceObjId);
            if (request.State != ActorLifecycleState.Completed)
            {
                return ($"InteractWith source {sourceObjId} for quest {questId} refused: {request.Detail}",
                    request.Failure ?? ActorFailureReason.RejectedAction);
            }

            if (actor.Character.Quests?.HasQuestCompleted(questId) == true)
                return (null, ActorFailureReason.None);

            quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
            if (quest == null)
            {
                return ($"quest {questId} left ActiveQuests without a completion flag after interaction",
                    ActorFailureReason.StateTransition);
            }

            var objectiveAfter = interaction.GetObjective(quest);
            if (objectiveAfter <= objectiveBefore)
            {
                return ($"InteractWith source {sourceObjId} completed but objective " +
                        $"{interaction.DetailId} stayed at {objectiveAfter}/{interaction.Count}",
                    ActorFailureReason.WrongDecision);
            }
        }

        return (null, ActorFailureReason.None);
    }

    /// <summary>
    /// Resolves a sphere objective's boundary and drives movement into the
    /// sphere trigger volume, ticking the world to trigger OnEnterSphere.
    /// </summary>
    private static string? SphereLeg(
        GameplayActor actor, LoopOptions opts, uint questId, QuestActObjSphere sphere,
        PerceptionSnapshot perception)
    {
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return $"quest {questId} left ActiveQuests before sphere pursuit started";

        if (sphere.GetObjective(quest) >= 1)
            return null;

        Vector3? targetPos = null;

        if (sphere.NpcId > 0)
        {
            if (perception.NpcObjIdsByTemplate.TryGetValue(sphere.NpcId, out var npcObjId))
            {
                var npc = actor.Character.ParentWorld?.GetNpc(npcObjId);
                if (npc != null)
                    targetPos = npc.Transform.World.Position;
            }
            else
            {
                var npc = actor.Character.ParentWorld?.GetNpcByTemplateId(sphere.NpcId);
                if (npc != null)
                    targetPos = npc.Transform.World.Position;
            }
        }

        if (!targetPos.HasValue)
        {
            var sphereComponentId = sphere.ParentComponent?.Id ?? 0;
            var sphereQuests = actor.Character.ParentWorld?.SphereQuestManager?.GetQuestSpheres(sphereComponentId);
            if (sphereQuests is { Count: > 0 })
            {
                targetPos = sphereQuests[0].Xyz;
            }
        }

        if (!targetPos.HasValue)
        {
            return $"quest {questId} sphere objective {sphere.DetailId} (sphere {sphere.SphereId}) has no resolvable location";
        }

        var destination = targetPos.Value;
        var currentPos = actor.Character.Transform.World.Position;
        if (Vector3.Distance(currentPos, destination) > 2.0f)
        {
            var moveReq = actor.NavigateTo(destination, opts.TravelSpeed, opts.TravelTimeout);
            DriveRequest(actor, opts, moveReq);
            if (moveReq.State != ActorLifecycleState.Completed)
                return $"NavigateTo sphere location for quest {questId} failed: {moveReq.Detail}";
        }

        // Inside sphere volume: tick simulation and trigger sphere entry event
        actor.Tick(TimeSpan.FromMilliseconds(500));

        if (sphere.GetObjective(quest) < 1)
        {
            // Rig simulation seam: headless worlds lack continuous zone-tick sphere sweeps;
            // trigger the engine's real DoOnEnterSphereEvents entry with updated character position.
            // Unit-req gating mirrors SphereQuestTrigger.Tick (SphereQuest.cs): the engine only
            // fires OnEnterSphere when CanTriggerSphere passes, so honor the gate here as well.
            var sphereComponentId = sphere.ParentComponent?.Id ?? 0;
            var sphereQuests = actor.Character.ParentWorld?.SphereQuestManager?.GetQuestSpheres(sphereComponentId);
            if (sphereQuests is { Count: > 0 })
            {
                var sphereQuest = sphereQuests[0];
                if (sphereQuest.DbSphere != null &&
                    !UnitRequirementsGameData.Instance.CanTriggerSphere(sphereQuest.DbSphere, actor.Character))
                {
                    return $"quest {questId} entered sphere {destination} but unit_reqs gate denies " +
                           $"sphere {sphere.SphereId} for this character (objective remains 0)";
                }
                QuestManager.Instance.DoOnEnterSphereEvents(actor.Character, sphereQuest, actor.Character.Transform.World.Position);
            }
        }

        if (sphere.GetObjective(quest) < 1)
        {
            return $"quest {questId} entered sphere location {destination} but objective remains 0";
        }

        return null;
    }

    /// <summary>
    /// Resolves recipe requirements, navigates to a required workbench if needed,
    /// and executes the craft action until the craft objective count is met.
    /// </summary>
    private static (string? Failure, ActorFailureReason Reason) CraftLeg(
        GameplayActor actor, LoopOptions opts, uint questId, QuestActObjCraft craftAct,
        PerceptionSnapshot perception)
    {
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return ($"quest {questId} left ActiveQuests before craft pursuit started",
                ActorFailureReason.StateTransition);

        var craft = CraftManager.Instance.GetCraftById(craftAct.CraftId);
        if (craft == null)
            return ($"quest {questId} craft objective {craftAct.DetailId} names unknown craft {craftAct.CraftId}",
                ActorFailureReason.RejectedAction);

        uint workbenchObjId = 0;
        var requiredWorkbenchTemplateId = craft.ReqDoodadId > 0
            ? craft.ReqDoodadId
            : craftAct.HighlightDoodadId;

        if (requiredWorkbenchTemplateId > 0)
        {
            if (perception.DoodadObjIdsByTemplate.TryGetValue(requiredWorkbenchTemplateId, out var doodads) && doodads.Count > 0)
            {
                workbenchObjId = doodads[0];
            }
            else
            {
                return ($"quest {questId} needs craft {craftAct.CraftId} on workbench {requiredWorkbenchTemplateId}, but no workbench was PERCEIVED nearby",
                    ActorFailureReason.Navigation);
            }

            var workbench = actor.Character.ParentWorld?.GetDoodad(workbenchObjId);
            if (workbench != null && Vector3.Distance(actor.Character.Transform.World.Position, workbench.Transform.World.Position) > 5.0f)
            {
                var moveReq = actor.NavigateTo(workbench.Transform.World.Position, opts.TravelSpeed, opts.TravelTimeout);
                DriveRequest(actor, opts, moveReq);
                if (moveReq.State != ActorLifecycleState.Completed)
                    return ($"NavigateTo workbench for quest {questId} failed: {moveReq.Detail}", ActorFailureReason.Navigation);
            }
        }

        var maxAttempts = Math.Max(10, craftAct.Count * 3);
        var attempts = 0;
        while (craftAct.GetObjective(quest) < craftAct.Count && attempts < maxAttempts)
        {
            attempts++;
            var req = actor.Craft(craftAct.CraftId, workbenchObjId);
            DriveRequest(actor, opts, req);
            if (req.State != ActorLifecycleState.Completed)
            {
                return ($"Craft for quest {questId} craftId {craftAct.CraftId} failed: {req.Detail}",
                    req.Failure ?? ActorFailureReason.RejectedAction);
            }

            if (actor.Character.Quests?.HasQuestCompleted(questId) == true)
                return (null, ActorFailureReason.None);

            quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
            if (quest == null)
                return (null, ActorFailureReason.None);
        }

        if (craftAct.GetObjective(quest) < craftAct.Count)
        {
            return ($"Craft for quest {questId} craftId {craftAct.CraftId} exceeded max attempts ({maxAttempts}) without satisfying objective",
                ActorFailureReason.RejectedAction);
        }

        return (null, ActorFailureReason.None);
    }

    /// <summary>
    /// Executes cinema/cutscene playback through GameplayActor.PlayCinema.
    /// </summary>
    private static string? CinemaLeg(
        GameplayActor actor, uint questId, QuestActObjCinema cinema)
    {
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return $"quest {questId} left ActiveQuests before cinema pursuit started";

        if (cinema.GetObjective(quest) >= 1)
            return null;

        var request = actor.PlayCinema(cinema.CinemaId);
        if (request.State != ActorLifecycleState.Completed)
        {
            return $"PlayCinema {cinema.CinemaId} for quest {questId} refused: {request.Detail}";
        }

        if (actor.Character.Quests?.HasQuestCompleted(questId) == true)
            return null;

        if (cinema.GetObjective(quest) < 1)
        {
            return $"quest {questId} cinema {cinema.CinemaId} played but objective remains 0";
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
    /// The talk leg: target NPCs resolved DATA-DRIVEN from the act among
    /// PERCEIVED NPCs (single template matches <paramref name="targetNpcTemplateId"/>,
    /// group matches <paramref name="npcGroupId"/>). The actor approaches the NPC
    /// and performs a real Talk contract action (<see cref="IGameplayActor.Talk"/>),
    /// which emits CSQuestTalkMadePacket and triggers OnTalkMade / OnTalkNpcGroupMade
    /// on the character's quest events.
    /// </summary>
    private static string? TalkLeg(GameplayActor actor, LoopOptions opts, uint questId,
        uint targetNpcTemplateId, uint npcGroupId, PerceptionSnapshot perception)
    {
        uint targetObjId = 0;
        if (targetNpcTemplateId > 0)
        {
            if (perception.NpcObjIdsByTemplate.TryGetValue(targetNpcTemplateId, out var npcs))
            {
                targetObjId = npcs;
            }
        }
        else if (npcGroupId > 0)
        {
            var character = actor.Character;
            var observation = actor.Observe();
            foreach (var objId in observation.NearbyNpcObjIds)
            {
                if (character.ParentWorld?.GetNpc(objId) is { } npc &&
                    QuestManager.Instance.CheckGroupNpc(npcGroupId, npc.TemplateId))
                {
                    targetObjId = objId;
                    break;
                }
            }
        }

        if (targetObjId == 0)
        {
            var targetLabel = targetNpcTemplateId > 0
                ? $"npc template {targetNpcTemplateId}"
                : $"npc group {npcGroupId}";
            return $"quest {questId} requires talk with {targetLabel}, but no matching NPC was perceived";
        }

        var targetNpc = actor.Character.ParentWorld?.GetNpc(targetObjId);
        if (targetNpc == null)
            return $"target NPC {targetObjId} not found in world";

        var distance = Vector3.Distance(actor.Character.Transform.World.Position, targetNpc.Transform.World.Position);
        if (distance > 5f)
        {
            var closeIn = DriveRequest(actor, opts,
                actor.MoveToUnit(targetObjId, opts.TravelSpeed, opts.TravelTimeout));
            if (closeIn.State != ActorLifecycleState.Completed)
            {
                return $"close-in move onto talk NPC {targetObjId} did not complete: {closeIn.State} ({closeIn.Detail ?? "n/a"})";
            }
        }

        var talk = actor.Talk(targetObjId);
        if (talk.State != ActorLifecycleState.Completed)
        {
            return $"Talk action with NPC {targetObjId} refused: {talk.Detail}";
        }

        return null;
    }

    /// <summary>
    /// Scans the actor's inventory bag for equippable items and attempts to
    /// equip them via the real <see cref="IGameplayActor.Equip"/> engine path.
    /// Non-equippable items or occupied slots with higher/equivalent gear are
    /// handled fail-safe by the engine's CanAccept / Equip rules.
    /// </summary>
    private static void EquipUpgrades(GameplayActor actor)
    {
        var character = actor.Character;
        var inventory = character.Inventory;
        if (inventory?.Bag == null)
            return;

        foreach (var item in inventory.Bag.Items.ToList())
        {
            if (item?.Template == null)
                continue;

            var allowedSlots = EquipmentContainer.GetAllowedGearSlots(item.Template);
            if (allowedSlots.Count == 0)
                continue;
            if (item.Template.LevelRequirement > character.Level)
                continue;

            var targetSlot = allowedSlots.FirstOrDefault(s => inventory.Equipment.GetItemBySlot((int)s) == null, allowedSlots[0]);
            var occupant = inventory.Equipment.GetItemBySlot((int)targetSlot);
            if (occupant != null && item.Template.Level <= occupant.Template.Level)
                continue;
            if (occupant?.TemplateId == item.TemplateId)
                continue;

            actor.Equip(item.TemplateId);
        }
    }
    /// <summary>
    /// Aggro-ranked objective leg. The objective's live quest instance supplies
    /// the acceptor NPC template; no scenario NPC id is used. Combat itself is
    /// the same hunt leg (SetTarget → Cast rotation → kill → Loot), while the
    /// additional gate requires a real owner entry in the victim's aggro table
    /// at one of the configured ranks. Progress is accepted only after the
    /// objective's OnKill handler observes the slain NPC as Target.
    /// </summary>
    private static (string? Failure, ActorFailureReason Reason) AggroLeg(
        GameplayActor actor, LoopOptions opts, IKillCreditSeam? killSeam, uint questId,
        QuestActObjAggro aggro, PerceptionSnapshot perception)
    {
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return ($"quest {questId} left ActiveQuests before aggro pursuit started",
                ActorFailureReason.StateTransition);

        if (quest.QuestAcceptorType != QuestAcceptorType.Npc || quest.AcceptorId == 0)
        {
            return ($"quest {questId} aggro objective requires a non-zero NPC acceptor " +
                    $"template, got {quest.QuestAcceptorType}/{quest.AcceptorId}; " +
                    "component-only forms without an NPC acceptor are unsupported",
                ActorFailureReason.WrongDecision);
        }

        if (aggro.Rank1 <= 0 && aggro.Rank2 <= 0 && aggro.Rank3 <= 0)
        {
            return ($"quest {questId} aggro objective {aggro.DetailId} has no configured " +
                    "rank threshold; refusing to invent aggro credit",
                ActorFailureReason.WrongDecision);
        }

        return HuntLeg(actor, opts, killSeam, questId, aggro, quest.AcceptorId, 0,
            perception, aggroObjective: true);
    }


    /// <summary>
    /// The hunt/aggro combat leg: targets resolved DATA-DRIVEN from the act
    /// among PERCEIVED hostiles. Aggro mode reuses this exact engagement path
    /// but stops after the first rank credit (Aggro RunAct is satisfied by any
    /// positive rank), rather than hunting to QuestActTemplate.Count.
    /// </summary>
    private static (string? Failure, ActorFailureReason Reason) HuntLeg(GameplayActor actor, LoopOptions opts,
        IKillCreditSeam? killSeam, uint questId, QuestActTemplate act,
        uint? targetNpcTemplateId, uint monsterGroupId, PerceptionSnapshot perception,
        bool aggroObjective = false)
    {
        var character = actor.Character;
        var quest = character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return ($"quest {questId} left ActiveQuests before hunt pursuit started", ActorFailureReason.StateTransition);

        // The caller's snapshot is only the FIRST sweep's evidence; every
        // round below re-observes (the spike loop's proven shape).
        _ = perception;

        var targetLabel = targetNpcTemplateId is { } template
            ? $"{(aggroObjective ? "aggro target" : "npc template")} {template}"
            : $"monster group {monsterGroupId}";
        var excluded = new HashSet<uint>();
        var noProgress = new Dictionary<uint, int>();
        var noTargetRounds = 0;
        var roundsLeft = opts.MaxHuntRounds;

        while (aggroObjective
            ? act.GetObjective(quest) <= 0
            : act.GetObjective(quest) < act.Count)
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
                    var noTargetReason = aggroObjective && excluded.Count > 0
                        ? $"no perceived {targetLabel} satisfies aggro attribution for owner " +
                          $"{character.ObjId} after excluding target(s) " +
                          $"[{string.Join(", ", excluded)}]; objective remains {act.GetObjective(quest)}"
                        : $"no attackable {targetLabel} perceived after " +
                          $"{opts.NoTargetRetries} re-observe rounds (nearby npcs: " +
                          $"[{string.Join(", ", observation.NearbyNpcObjIds)}])";
                    return (noTargetReason, ActorFailureReason.Starvation);
                }

                continue;
            }

            if (aggroObjective)
            {
                var aggroRating = target.GetAggroRatingInPercent(character.ObjId);
                if (!float.IsFinite(aggroRating) || aggroRating >= 100f ||
                    !AggroRankCanCredit((QuestActObjAggro)act, aggroRating))
                {
                    excluded.Add(target.ObjId);
                    noTargetRounds++;
                    if (noTargetRounds > opts.NoTargetRetries)
                    {
                        return ($"no perceived {targetLabel} satisfies aggro attribution for " +
                                $"owner {character.ObjId}: target {target.ObjId} rating " +
                                $"{aggroRating:0.###}% is absent or outside ranks " +
                                $"{((QuestActObjAggro)act).Rank1}/" +
                                $"{((QuestActObjAggro)act).Rank2}/" +
                                $"{((QuestActObjAggro)act).Rank3}; objective remains " +
                                $"{act.GetObjective(quest)}",
                            ActorFailureReason.Starvation);
                    }

                    continue;
                }

                noTargetRounds = 0;
            }
            else
            {
                noTargetRounds = 0;
            }

            // Sustain (vital recovery): if HP < 35%, recover before engaging
            var maxHp = character.MaxHp > 0 ? character.MaxHp : 1;
            if ((float)character.Hp / maxHp < 0.35f)
            {
                var potion = character.Inventory?.Bag.Items
                    .FirstOrDefault(i => i?.Template != null && (ItemCategory)i.Template.CategoryId is ItemCategory.Healing_Potion or ItemCategory.Potion or ItemCategory.Food);
                if (potion != null)
                {
                    actor.UseItem(potion.TemplateId);
                }

                var regenGuard = 0;
                while ((float)character.Hp / maxHp < 0.8f && regenGuard++ < 10)
                {
                    actor.Tick(TimeSpan.FromSeconds(1));
                }
            }

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
                down = target.Hp <= 0;
                if (!down && killSeam != null)
                {
                    down = aggroObjective && killSeam is IAggroKillCreditSeam aggroSeam
                        ? aggroSeam.TryKillAggro(actor, target)
                        : killSeam.TryKill(actor, target);
                }
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

            if (aggroObjective && act.GetObjective(quest) <= 0)
            {
                return ($"target {target.ObjId} died, but QuestActObjAggro " +
                        $"{act.DetailId} received no live OnKill credit; refusing completion",
                    ActorFailureReason.StateTransition);
            }

            // DOWN: loot the fresh corpse through the real contract path. A
            // Rejected loot is tolerated (recorded, never fatal) — not every
            // hunt objective drops loot.
            excluded.Add(target.ObjId);
            noProgress.Remove(target.ObjId);
            var loot = actor.Loot(target.ObjId);
            if (loot.State == ActorLifecycleState.Rejected)
                Logger.Debug("hunt leg: loot of corpse {ObjId} rejected ({Detail}) — tolerated", target.ObjId, loot.Detail);

            // Auto-equip any upgrades looted from the mob
            EquipUpgrades(actor);
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
    private static bool AggroRankCanCredit(QuestActObjAggro aggro, float rating)
    {
        return (aggro.Rank1 > 0 && rating <= aggro.Rank1)
               || (aggro.Rank2 > 0 && rating <= aggro.Rank2)
               || (aggro.Rank3 > 0 && rating <= aggro.Rank3);
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
        {
            if (request.Action == ActorActionType.Craft && actor.Character.Craft.IsCrafting)
            {
                var craft = CraftManager.Instance.GetCraftById(request.TargetId);
                var benchObjId = (request.Payload as CraftParams)?.DoodadObjId ?? 0;
                var bench = actor.Character.ParentWorld?.GetDoodad(benchObjId);
                var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                effect.Apply(actor.Character, null, bench, null,
                    new CastSkill(craft?.SkillId ?? 0, 0), new EffectSource(), null, DateTime.UtcNow);
            }
            actor.Tick(TimeSpan.FromMilliseconds(20));
        }
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
