using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// EXPEDITION-01 live scenario: five ordinary Characters form a real party
/// then found an expedition through the real ExpeditionManager path —
/// PartyInvite/Accept ×4 → ExpeditionCreate → invite/accept outsider.
/// Membership rows on both sides are the proof.
/// </summary>
public static class ExpeditionFormationScenario
{
    public const string ScenarioName = "expedition-formation";

    public sealed record FormationOptions(
        string ExpeditionName = "ExpLiveProof",
        string CycleId = "exp-live");

    public static BotScenarioRunner.ScenarioRunResult Run(
        IReadOnlyList<Character> members, FormationOptions? options = null)
    {
        options ??= new FormationOptions();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request)
            => new(name, 1, request.State.ToString(), request.TargetId.ToString(), request.Detail ?? "");

        BotScenarioRunner.ScenarioRunResult Fail(string stage, ActorFailureReason? failure, string reason)
            => new()
            {
                Template = ScenarioName,
                Passed = false,
                FailStage = stage,
                Failure = failure,
                FailReason = reason,
                RigNotes = [],
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                ActorRequests = traceRecords.Count
            };

        try
        {
            if (members.Count < 5)
                return Fail("PARTY", ActorFailureReason.WrongDecision,
                    $"expedition founding needs a 5-member party, got {members.Count}");

            var actors = members.Select(m => new GameplayActor(m)).ToList();
            var owner = members[0];

            // ---- PARTY: owner invites 2..5, each accepts.
            for (var i = 1; i < members.Count; i++)
            {
                var invite = actors[0].PartyInvite(members[i].ObjId, $"{options.CycleId}-invite-{i}");
                traceRecords.Add(actors[0].AuditTrace.Last());
                stages.Add(Stage($"INVITE-{i}", invite));
                if (invite.State != ActorLifecycleState.Completed)
                    return Fail($"INVITE-{i}", invite.Failure, $"invite {invite.State}: {invite.Detail ?? "no detail"}");

                var accept = actors[i].PartyAccept($"{options.CycleId}-accept-{i}");
                traceRecords.Add(actors[i].AuditTrace.Last());
                stages.Add(Stage($"ACCEPT-{i}", accept));
                if (accept.State != ActorLifecycleState.Completed)
                    return Fail($"ACCEPT-{i}", accept.Failure, $"accept {accept.State}: {accept.Detail ?? "no detail"}");
            }

            // ---- CREATE the expedition through the real engine path.
            var create = actors[0].ExpeditionCreate(options.ExpeditionName, $"{options.CycleId}-create");
            traceRecords.Add(actors[0].AuditTrace.Last());
            stages.Add(Stage("CREATE", create));
            if (create.State != ActorLifecycleState.Completed)
                return Fail("CREATE", create.Failure, $"create {create.State}: {create.Detail ?? "no detail"}");

            var expeditionId = owner.Expedition?.Id;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("owner-membered",
                expeditionId != null,
                expeditionId != null
                    ? $"owner in expedition {expeditionId} ('{owner.Expedition!.Name}')"
                    : "owner has no expedition after create"));
            var allMembered = members.All(m => m.Expedition?.Id == expeditionId && expeditionId != null);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("party-auto-joined",
                allMembered,
                allMembered
                    ? $"all {members.Count} founding members share expedition {expeditionId}"
                    : "founding party did not fully auto-join"));

            var passed = criteria.All(c => c.Passed);
            return new BotScenarioRunner.ScenarioRunResult
            {
                Template = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                Failure = passed ? null : ActorFailureReason.WrongDecision,
                FailReason = passed ? "" : string.Join("; ", criteria.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")),
                RigNotes = [],
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                ActorRequests = traceRecords.Count
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
