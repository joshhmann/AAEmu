using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M5 policy-extension consumer #2 — PARTY: proposal-driven choice among
/// legal party actions (invite / accept / follow / assist) through the
/// EXISTING <see cref="IGameplayActor"/> actions (PartyInvite, PartyAccept,
/// MoveToUnit, SetTarget) and the ordinary TeamManager service.
///
/// Decision discipline (the M5 contract):
///   - perception rides <see cref="BotObservedContext.Capture"/> (Observe —
///     direct server-state query, no packets);
///   - hard legality is evaluated BEFORE preference. Accept/follow/assist
///     legality reads ONLY the immutable observation context
///     (PendingInvitationOwnerId / InParty / PartyLeaderObjId /
///     PartyLeaderTargetObjId). Invite legality (target exists, not self, no
///     pending invitation on the TARGET, target not already a member) is not
///     visible in the inviter's own context, so it is resolved through
///     ordinary TeamManager service reads at PERCEPTION time and the
///     proposal is only offered when inviteable — the same perception-time
///     gate convention as the economy consumer's merchant check;
///   - selection is deterministic (fixed priority, then tie-break key);
///   - dispatch calls the existing actor methods only — no new gameplay
///     path, no direct DB / Transform / ZoneId / GM / reflection shortcuts;
///   - the terminal postcondition is evaluated against the terminal
///     observation capture.
///
/// Invite postcondition note: a successful invite changes nothing observable
/// in the INVITER's own context (TeamManager exposes no invitation
/// enumeration on the inviter), so the proposal's postcondition is
/// documented as `_ => true` and the scenario asserts the real outcome via
/// the criterion <c>TeamManager.Instance.GetActiveInvitation(targetId) !=
/// null</c> — the same ordinary service query PartyInvite itself pre-flights.
/// </summary>
public static class PartyDecisionScenario
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "m5-party-decision";

    /// <summary>Scenario parameters.</summary>
    public sealed record PartyOptions
    {
        /// <summary>Character objId the invite candidate targets.</summary>
        public uint InviteTargetObjId { get; init; }

        /// <summary>Distance at or below which the follow candidate is satisfied.</summary>
        public float FollowDistance { get; init; } = 3f;

        public float MoveSpeed { get; init; } = 5f;

        public TimeSpan MoveTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Policy version stamped on every proposal.</summary>
        public string PolicyVersion { get; init; } = "party-v1";

        // ---- fixed priorities (policy; personality stays 0) ----
        public int AcceptPriority { get; init; } = 40;
        public int InvitePriority { get; init; } = 30;
        public int FollowPriority { get; init; } = 20;
        public int AssistPriority { get; init; } = 10;

        /// <summary>
        /// Optional driver for in-flight requests (the follow move leg).
        /// Rigs inject their deterministic driver; when null the scenario
        /// ticks the actor inline (bounded by MoveTimeout) — deterministic
        /// headless AND correct for synchronous dispatch.
        /// </summary>
        public Func<GameplayActor, ActorRequest, ActorRequest>? Drive { get; init; }
    }

    /// <summary>Structured run result — decision-path evidence attached.</summary>
    public sealed class PartyRunResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<string> Notes { get; init; } = [];
        /// <summary>The actor's full audit trace, in execution order.</summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        /// <summary>The action the deterministic selector chose (null = no legal proposal).</summary>
        public ActorActionType? SelectedAction { get; init; }
        /// <summary>Why each non-selected candidate was rejected (legality-before-preference evidence).</summary>
        public IReadOnlyList<BotProposalRejection> Rejections { get; init; } = [];
        public string SelectionExplanation { get; init; } = "";
        /// <summary>Whether the selected proposal's terminal postcondition held.</summary>
        public bool ExpectedPostconditionSatisfied { get; init; }

        public string Evidence()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Scenario: {Scenario}");
            sb.AppendLine($"Verdict: {(Passed ? "PASS" : "FAIL")}" +
                          (FailStage.Length > 0 ? $" at {FailStage}" : "") +
                          (Failure is { } f ? $" ({f})" : "") +
                          (FailReason.Length > 0 ? $" — {FailReason}" : ""));
            sb.AppendLine($"- selection: {SelectedAction?.ToString() ?? "none"} — {SelectionExplanation}");
            foreach (var r in Rejections)
                sb.AppendLine($"- rejection: {r.Proposal.Goal} — {r.Reason}");
            foreach (var note in Notes)
                sb.AppendLine($"- note: {note}");
            foreach (var s in Stages)
                sb.AppendLine($"- stage {s.Stage}: {s.Advance}, step={s.StepObserved}, status={s.StatusObserved}");
            foreach (var c in Criteria)
                sb.AppendLine($"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}");
            foreach (var t in TraceRecords)
                sb.AppendLine($"- trace: {t.Action}({t.TargetId})→{t.Result}{(t.Failure is { } fr ? $"/{fr}" : "")}");
            return sb.ToString();
        }
    }

    /// <summary>Runs one decision cycle on an embodied character.</summary>
    public static PartyRunResult Run(Character character, PartyOptions? options = null)
    {
        var opts = options ?? new PartyOptions();
        var actor = new GameplayActor(character);
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var notes = new List<string>();

        try
        {
            // ---------------------------------------------------- 1. PERCEIVE
            var context = BotObservedContext.Capture(actor);

            // Perception-time ordinary service reads (the same queries the
            // party engine paths pre-flight):
            //  - invite legality: target resolves to a Character in the world,
            //    is not self, has no pending invitation, and is not already a
            //    member of the inviter's team;
            //  - the pending invitation's owner Character.Id (the accept
            //    postcondition compares the joined team's owner against it).
            var inviteable = TryResolveInviteTarget(actor, opts.InviteTargetObjId);
            var pendingInviterId = TeamManager.Instance.GetActiveInvitation(character.Id)?.Owner?.Id ?? 0;

            // ---------------------------------------------------- 2. DECIDE
            // The candidate set is FIXED and bounded (invite / accept /
            // follow / assist); hard legality is evaluated BEFORE preference
            // and every precondition reads ONLY the immutable observation
            // context (PendingInvitationOwnerId / InParty / PartyLeaderObjId
            // / PartyLeaderTargetObjId) — except the invite gate, whose
            // target-side state is not visible in the inviter's context and
            // is therefore resolved through ordinary TeamManager service
            // reads at perception time (the same query PartyInvite itself
            // pre-flights).
            var proposals = new List<BotDecisionProposal>
            {
                new(
                    goal: "party.invite",
                    action: ActorActionType.PartyInvite,
                    targetId: opts.InviteTargetObjId,
                    // A successful invite changes nothing observable in the
                    // inviter's own context (TeamManager exposes no
                    // invitation enumeration on the inviter); the scenario
                    // asserts the real outcome via the criterion below.
                    expectedPostcondition: new BotProposalPostcondition(
                        "invitation record exists on the target (asserted by scenario criterion)",
                        _ => true),
                    idempotencyKey: $"party:{character.Id}:1:invite",
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: "invite the target character to the party",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.InvitePriority,
                    tieBreakKey: "invite",
                    hardPreconditions:
                    [
                        // Closure over the perception-time legality result —
                        // an ordinary service read at perception time, not
                        // during selection.
                        new BotProposalPrecondition("target-inviteable", _ => inviteable)
                    ]),
                new(
                    goal: "party.accept",
                    action: ActorActionType.PartyAccept,
                    targetId: 0,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"character is in the team owned by inviter {pendingInviterId}",
                        observed => observed.InParty && observed.PartyOwnerId == pendingInviterId),
                    idempotencyKey: $"party:{character.Id}:1:accept",
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: "accept the pending party invitation",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.AcceptPriority,
                    tieBreakKey: "accept",
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("pending-invitation",
                            observed => observed.PendingInvitationOwnerId != 0)
                    ]),
                new(
                    goal: "party.follow",
                    action: ActorActionType.Move,
                    targetId: context.PartyLeaderObjId,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"within {opts.FollowDistance:0.###} of the party leader",
                        observed => MathUtil.CalculateDistance(
                            observed.Position, observed.PartyLeaderPosition, true) <= opts.FollowDistance),
                    idempotencyKey: $"party:{character.Id}:1:follow",
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: "follow the party leader",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.FollowPriority,
                    tieBreakKey: "follow",
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("in-party",
                            observed => observed.InParty && observed.PartyLeaderObjId != 0)
                    ]),
                new(
                    goal: "party.assist",
                    action: ActorActionType.Target,
                    targetId: context.PartyLeaderTargetObjId,
                    expectedPostcondition: new BotProposalPostcondition(
                        "character targets the party leader's current target",
                        observed => observed.CurrentTargetObjId == observed.PartyLeaderTargetObjId),
                    idempotencyKey: $"party:{character.Id}:1:assist",
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: "assist the party leader's current target",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.AssistPriority,
                    tieBreakKey: "assist",
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("in-party-with-leader-target",
                            observed => observed.InParty && observed.PartyLeaderTargetObjId != 0)
                    ])
            };

            var decision = BotDecisionSelector.Select(context, proposals);
            if (!decision.HasProposal)
            {
                return Fail("DECIDE", ActorFailureReason.WrongDecision,
                    $"no legal party proposal: {decision.Explanation}", actor, stages, criteria, notes, decision);
            }

            // ---------------------------------------------------- 3. EXECUTE
            var execution = BotDecisionCycle.Execute(actor, context, decision.Proposal!,
                (gameplayActor, proposal) =>
                {
                    switch (proposal.Action)
                    {
                        case ActorActionType.PartyInvite:
                            return gameplayActor.PartyInvite(proposal.TargetId, proposal.IdempotencyKey);
                        case ActorActionType.PartyAccept:
                            return gameplayActor.PartyAccept(proposal.IdempotencyKey);
                        case ActorActionType.Move:
                            var move = gameplayActor.MoveToUnit(
                                proposal.TargetId, opts.MoveSpeed, opts.MoveTimeout, proposal.IdempotencyKey);
                            if (opts.Drive != null)
                                return opts.Drive((GameplayActor)gameplayActor, move);
                            // Inline bounded pump (LevelingLoopScenario
                            // convention): deterministic headless AND correct
                            // for synchronous dispatch.
                            var elapsed = TimeSpan.Zero;
                            var tick = TimeSpan.FromMilliseconds(100);
                            while (!move.IsTerminal && elapsed <= opts.MoveTimeout)
                            {
                                gameplayActor.Tick(tick);
                                elapsed += tick;
                            }
                            return move;
                        case ActorActionType.Target:
                            return gameplayActor.SetTarget(proposal.TargetId);
                        default:
                            throw new InvalidOperationException($"party decision cannot dispatch {proposal.Action}");
                    }
                });
            var request = execution.Request;
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                request.Action.ToString(), 1, request.State.ToString(), request.TargetId.ToString(), request.Detail ?? ""));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("dispatch-completed",
                request.State == ActorLifecycleState.Completed,
                $"{request.Action}: {request.Detail ?? request.State.ToString()}"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("terminal-postcondition-satisfied",
                execution.ExpectedPostconditionSatisfied,
                execution.Proposal.ExpectedPostcondition.Description));
            if (request.State != ActorLifecycleState.Completed || !execution.ExpectedPostconditionSatisfied)
            {
                return Fail("EXECUTE", request.Failure ?? ActorFailureReason.RejectedAction,
                    $"{request.Action} {request.State}: {request.Detail ?? execution.Proposal.ExpectedPostcondition.Description}",
                    actor, stages, criteria, notes, decision, execution);
            }

            // Invite outcome is asserted through the ordinary service query
            // (the same query PartyInvite itself pre-flights) because the
            // inviter's own context cannot observe the target's invitation.
            // The invitation dictionary is keyed by the target's Character.Id
            // (not ObjId), so the target is resolved through the ordinary
            // world lookup first.
            if (decision.Proposal!.Action == ActorActionType.PartyInvite)
            {
                var targetId = character.ParentWorld?.GetUnit(opts.InviteTargetObjId)?.Id ?? 0;
                var invitation = targetId == 0 ? null : TeamManager.Instance.GetActiveInvitation(targetId);
                criteria.Add(new BotScenarioRunner.CriterionVerdict("invitation-record-exists",
                    invitation != null,
                    invitation == null
                        ? $"no invitation record on target {opts.InviteTargetObjId}"
                        : $"invitation on target {opts.InviteTargetObjId} from owner {invitation.Owner.Id}"));
            }

            // ---------------------------------------------------- 4. VERIFY
            var traceComplete = actor.AuditTrace.Count > 0
                && actor.AuditTrace.All(r => r.Result == ActorLifecycleState.Completed
                    && r.StateChanges.Any(s => s.Contains("Requested"))
                    && r.StateChanges.Any(s => s.Contains("Accepted"))
                    && r.StateChanges.Any(s => s.Contains("Completed")));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", traceComplete,
                $"completed records {actor.AuditTrace.Count(r => r.Result == ActorLifecycleState.Completed)}/{actor.AuditTrace.Count}"));

            var passed = criteria.All(c => c.Passed);
            return new PartyRunResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                Failure = passed ? null : ActorFailureReason.WrongDecision,
                FailReason = passed ? "" : string.Join("; ", criteria.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")),
                Stages = stages,
                Criteria = criteria,
                Notes = notes,
                TraceRecords = [.. actor.AuditTrace],
                SelectedAction = decision.Proposal!.Action,
                Rejections = decision.Rejections,
                SelectionExplanation = decision.Explanation,
                ExpectedPostconditionSatisfied = execution.ExpectedPostconditionSatisfied
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor, stages, criteria, notes, null);
        }
    }

    /// <summary>
    /// Perception-time invite gate (ordinary service reads — the same gates
    /// the PartyInvite engine path pre-flights): the target resolves to a
    /// Character in the world, is not self, has no pending invitation, and
    /// is not already a member of the inviter's team.
    /// </summary>
    private static bool TryResolveInviteTarget(GameplayActor actor, uint targetObjId)
    {
        if (actor.Character.ParentWorld?.GetUnit(targetObjId) is not Character target)
            return false;
        if (target.Id == actor.Character.Id)
            return false;
        if (TeamManager.Instance.GetActiveInvitation(target.Id) != null)
            return false;
        var inviterTeam = TeamManager.Instance.GetActiveTeamByUnit(actor.Character.Id);
        return inviterTeam?.IsMember(target.Id) != true;
    }

    private static PartyRunResult Fail(
        string stage, ActorFailureReason reason, string detail,
        GameplayActor actor,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        List<string> notes,
        BotDecisionSelection? decision,
        BotDecisionExecution? execution = null)
        => new()
        {
            Scenario = ScenarioName,
            Passed = false,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            Stages = stages,
            Criteria = criteria,
            Notes = notes,
            TraceRecords = [.. actor.AuditTrace],
            SelectedAction = decision?.Proposal?.Action,
            Rejections = decision?.Rejections ?? [],
            SelectionExplanation = decision?.Explanation ?? "",
            ExpectedPostconditionSatisfied = execution?.ExpectedPostconditionSatisfied ?? false
        };
}
