using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest bootstrap decision scenario (copper-bootstrap leg): one
/// quest action per scheduler wake through EXISTING
/// <see cref="IGameplayActor"/> actions only.
///
/// Decision discipline (the <see cref="NeedsDecisionScenario"/> precedent):
///   - perception rides <see cref="BotObservedContext.Capture"/> (active
///     quest ids) plus live NPC resolution for discovery targets;
///   - hard legality is evaluated BEFORE preference; range, liveness, and
///     gate checks stay the engine's own fail-closed gates at dispatch;
///   - selection is deterministic (fixed priority, personality weight 0);
///   - dispatch calls existing actor methods only — AdvanceQuest,
///     DiscoverQuests, AcceptQuest — no new gameplay path.
///
/// Per-wake order: advance each active quest once (objective credit flows
/// from world events the hunt/interact legs drive; completions drop the
/// quest from ActiveQuests and pay copper rewards through the normal
/// engine path), otherwise discover from the nearest in-range NPCs and
/// accept the lowest-level in-band offer. Objective pursuit itself (kill,
/// gather, talk) rides the existing hunt/interact branches — this scenario
/// only advances the step machine and acquires new quests.
/// </summary>
public static class QuestDecisionScenario
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "quest-bootstrap-decision";

    /// <summary>
    /// Scenario parameters. Defaults configure no discovery band, so a
    /// default run only advances active quests and never accepts (never throws).
    /// </summary>
    public sealed record QuestOptions
    {
        /// <summary>Idempotency namespace for this run's legs.</summary>
        public string CycleId { get; init; } = "q1";

        /// <summary>Policy version stamped on every proposal.</summary>
        public string PolicyVersion { get; init; } = "quest-v1";

        /// <summary>Inclusive availability band for offering choice.</summary>
        public byte BandMin { get; init; } = 1;

        /// <summary>Inclusive availability band for offering choice.</summary>
        public byte BandMax { get; init; } = 9;

        /// <summary>How many nearby NPCs to sweep for offerings per wake.</summary>
        public int MaxDiscoverTargets { get; init; } = 3;

        // ---- fixed priorities (policy; personality weight stays 0) ----
        public int TurnInPriority { get; init; } = 30;
        public int AdvancePriority { get; init; } = 20;
        public int AcceptPriority { get; init; } = 10;
    }

    /// <summary>Structured run result — decision-path evidence attached.</summary>
    public sealed class QuestRunResult
    {
        public required string Scenario { get; init; }
        public bool WorkSelected { get; init; }
        /// <summary>The action the deterministic selector chose (null = run failed before selection).</summary>
        public ActorActionType? SelectedAction { get; init; }
        /// <summary>The dispatched request (null when nothing was dispatched).</summary>
        public ActorRequest? Request { get; init; }
        /// <summary>Why each non-selected candidate was refused (legality-before-preference evidence).</summary>
        public IReadOnlyList<BotProposalRejection> Rejections { get; init; } = [];
        public string Explanation { get; init; } = "";
        /// <summary>Quest ids completed by this leg (left ActiveQuests during the wake).</summary>
        public IReadOnlyList<uint> CompletedQuestIds { get; init; } = [];
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        /// <summary>The actor's full audit trace, in execution order.</summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
    }

    /// <summary>Runs one quest-bootstrap decision cycle on a live actor.</summary>
    public static QuestRunResult Run(
        IGameplayActor actor,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs,
        QuestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var opts = options ?? new QuestOptions();

        try
        {
            // ---------------------------------------------------- 1. PERCEIVE
            var context = BotObservedContext.Capture(actor);
            var before = context.ActiveQuestIds.ToHashSet();

            // ---------------------------------------------------- 2. DECIDE
            var proposals = new List<BotDecisionProposal>();
            foreach (var questId in before.Order())
            {
                proposals.Add(AdvanceProposal(actor, opts, questId));
                foreach (var turnIn in TurnInProposals(actor, opts, questId))
                    proposals.Add(turnIn);
            }

            // Discovery needs live NPC targets: nearest in-range NPCs only
            // (range itself stays the engine gate at dispatch).
            var discoverTargets = new List<uint>();
            if (nearbyNpcs != null)
            {
                var position = context.Position;
                foreach (var npc in nearbyNpcs(actor.Character, GameplayActor.MaxQuestDiscoverRange))
                {
                    if (npc == null)
                        continue;
                    discoverTargets.Add(npc.ObjId);
                    if (discoverTargets.Count >= opts.MaxDiscoverTargets)
                        break;
                }
            }

            // Discover first (read-like, non-mutating), then offer accepts.
            var offerings = new List<(uint TargetObjId, QuestOffering Offering)>();
            foreach (var targetObjId in discoverTargets)
            {
                var discover = actor.DiscoverQuests(targetObjId,
                    idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:discover:{targetObjId}");
                if (discover is not { IsTerminal: true, State: ActorLifecycleState.Completed })
                    continue;
                if (discover.Result is not QuestDiscoveryResult result)
                    continue;
                foreach (var offering in result.Offerings)
                {
                    if (offering.Level < opts.BandMin || offering.Level > opts.BandMax)
                        continue;
                    if (before.Contains(offering.QuestId))
                        continue;
                    offerings.Add((targetObjId, offering));
                }
            }
            foreach (var (targetObjId, offering) in offerings
                         .OrderBy(o => o.Offering.Level)
                         .ThenBy(o => o.Offering.QuestId))
                proposals.Add(AcceptProposal(actor, opts, targetObjId, offering));

            var decideContext = BotObservedContext.Capture(actor);
            var decision = BotDecisionSelector.Select(decideContext, proposals);
            if (!decision.HasProposal)
            {
                return Fail("DECIDE", ActorFailureReason.WrongDecision,
                    $"no legal quest proposal: {decision.Explanation}", actor, null, decision.Rejections);
            }

            // ---------------------------------------------------- 3. EXECUTE
            var selected = decision.Proposal!;
            actor.SetPendingDecision(selected.Goal, opts.PolicyVersion,
                decision.Rejections.Count + 1, decision.Rejections.Count, opts.CycleId);
            var execution = BotDecisionCycle.Execute(actor, decideContext, selected,
                static (gameplayActor, proposal) => Dispatch(gameplayActor, proposal));
            var request = execution.Request;
            if (!request.IsTerminal)
            {
                return Fail("EXECUTE", ActorFailureReason.Starvation,
                    $"{request.Action} left the terminal surface",
                    actor, selected.Action, decision.Rejections, request);
            }

            var after = BotObservedContext.Capture(actor).ActiveQuestIds.ToHashSet();
            var completed = before.Where(q => !after.Contains(q)).ToList();
            return new QuestRunResult
            {
                Scenario = ScenarioName,
                WorkSelected = true,
                SelectedAction = selected.Action,
                Request = request,
                Rejections = decision.Rejections,
                Explanation = decision.Explanation,
                CompletedQuestIds = completed,
                TraceRecords = [.. actor.AuditTrace]
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor, null, []);
        }
    }

    private static BotDecisionProposal AdvanceProposal(IGameplayActor actor, QuestOptions opts, uint questId)
        => new(
            goal: "quest.advance",
            action: ActorActionType.AdvanceQuest,
            targetId: questId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} step machine advanced",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:advance:{questId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "advance the active quest step machine",
            policyVersion: opts.PolicyVersion,
            priority: opts.AdvancePriority,
            tieBreakKey: $"advance:{questId:D10}",
            payload: null,
            hardPreconditions:
            [
                new BotProposalPrecondition("quest-active",
                    observed => observed.ActiveQuestIds.Contains(questId))
            ]);
    /// <summary>
    /// Turn-in proposals for a Ready active quest (the LevelingLoop TurnIn
    /// precedent): NPC reporter → TurnInQuest, doodad reporter →
    /// TurnInAtDoodad, neither → AutoTurnInQuest. Reporter objIds resolve
    /// per wake — never stored. Readiness is the live quest Status; the
    /// engine revalidates at dispatch.
    /// </summary>
    private static IEnumerable<BotDecisionProposal> TurnInProposals(
        IGameplayActor actor, QuestOptions opts, uint questId)
    {
        var character = actor.Character;
        if (character.Quests?.ActiveQuests.GetValueOrDefault(questId) is not { Status: QuestStatus.Ready })
            yield break;
        var template = QuestManager.Instance.GetTemplate(questId);
        if (template == null)
            yield break;
        var readyActs = template.GetComponents(QuestComponentKind.Ready)
            .SelectMany(c => c.ActTemplates).ToList();
        var reportNpc = readyActs.OfType<QuestActConReportNpc>().FirstOrDefault();
        var reportDoodad = readyActs.OfType<QuestActConReportDoodad>().FirstOrDefault();
        if (reportNpc != null)
        {
            var reporter = character.ParentWorld?.GetNpcByTemplateId(reportNpc.NpcId);
            if (reporter == null)
                yield break;
            yield return TurnInProposal(actor, opts, questId, ActorActionType.TurnInQuest,
                reporter.ObjId, new QuestTurnInParams(reporter.ObjId, -1));
        }
        else if (reportDoodad != null)
        {
            var doodad = character.ParentWorld?.GetAllDoodads().FirstOrDefault(d => d?.TemplateId == reportDoodad.DoodadId);
            if (doodad == null)
                yield break;
            yield return TurnInProposal(actor, opts, questId, ActorActionType.TurnInDoodad,
                doodad.ObjId, new QuestTurnInParams(doodad.ObjId, -1));
        }
        else
        {
            yield return TurnInProposal(actor, opts, questId, ActorActionType.AutoTurnIn,
                0, new QuestTurnInParams(0, -1));
        }
    }

    private static BotDecisionProposal TurnInProposal(
        IGameplayActor actor, QuestOptions opts, uint questId,
        ActorActionType action, uint targetId, QuestTurnInParams turnIn)
        => new(
            goal: "quest.turn-in",
            action: action,
            targetId: questId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {questId} completed by turn-in",
                _ => true),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:turnin:{questId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "turn in the ready quest for copper reward",
            policyVersion: opts.PolicyVersion,
            priority: opts.TurnInPriority,
            tieBreakKey: $"turnin:{questId:D10}",
            payload: turnIn,
            hardPreconditions:
            [
                new BotProposalPrecondition("quest-active",
                    observed => observed.ActiveQuestIds.Contains(questId))
            ]);

    private static BotDecisionProposal AcceptProposal(
        IGameplayActor actor, QuestOptions opts, uint targetObjId, QuestOffering offering)
        => new(
            goal: "quest.accept",
            action: ActorActionType.AcceptQuest,
            targetId: offering.QuestId,
            expectedPostcondition: new BotProposalPostcondition(
                $"quest {offering.QuestId} is active",
                observed => observed.ActiveQuestIds.Contains(offering.QuestId)),
            idempotencyKey: $"quest:{actor.ActorId}:{opts.CycleId}:accept:{offering.QuestId}",
            timeout: TimeSpan.FromSeconds(30),
            rationale: $"lowest offered level in [{opts.BandMin}..{opts.BandMax}]",
            policyVersion: opts.PolicyVersion,
            priority: opts.AcceptPriority + Math.Max(0, opts.BandMax - offering.Level),
            tieBreakKey: offering.QuestId.ToString("D10"),
            payload: (offering, targetObjId),
            hardPreconditions:
            [
                new BotProposalPrecondition("quest-not-active",
                    observed => !observed.ActiveQuestIds.Contains(offering.QuestId))
            ]);

    private static ActorRequest Dispatch(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        return proposal.Action switch
        {
            ActorActionType.AdvanceQuest => gameplayActor.AdvanceQuest(
                proposal.TargetId, proposal.IdempotencyKey),
            ActorActionType.AcceptQuest when proposal.Payload is (QuestOffering offering, uint _) => gameplayActor.AcceptQuest(
                proposal.TargetId, offering.AcceptorType, offering.AcceptorId, proposal.IdempotencyKey),
            ActorActionType.TurnInQuest when proposal.Payload is QuestTurnInParams turnIn => gameplayActor.TurnInQuest(
                proposal.TargetId, turnIn.TargetObjId, turnIn.SelectedReward, proposal.IdempotencyKey),
            ActorActionType.TurnInDoodad when proposal.Payload is QuestTurnInParams turnInDoodad => gameplayActor.TurnInAtDoodad(
                proposal.TargetId, turnInDoodad.TargetObjId, turnInDoodad.SelectedReward, proposal.IdempotencyKey),
            ActorActionType.AutoTurnIn when proposal.Payload is QuestTurnInParams auto => gameplayActor.AutoTurnInQuest(
                proposal.TargetId, auto.SelectedReward, proposal.IdempotencyKey),
        };
    }

    private static QuestRunResult Fail(
        string stage, ActorFailureReason reason, string detail,
        IGameplayActor actor,
        ActorActionType? selected,
        IReadOnlyList<BotProposalRejection> rejections,
        ActorRequest? request = null)
        => new()
        {
            Scenario = ScenarioName,
            WorkSelected = false,
            SelectedAction = selected,
            Request = request,
            Rejections = rejections,
            Explanation = detail,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            TraceRecords = [.. actor.AuditTrace]
        };
}
