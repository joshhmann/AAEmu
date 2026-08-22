using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M7 Party v1 slice 2: a party member follows the real team owner and
/// assists the owner's current target through the existing M5 contract.
///
/// This is deliberately a scenario/behavior surface, not another actor
/// action. Follow composes MoveToUnit; assist reads the leader's ordinary
/// Character.CurrentTarget and composes SetTarget. Team membership remains
/// owned by TeamManager and both mutations use the same gameplay paths a
/// solo actor uses.
/// </summary>
public static class PartyFollowAssistScenario
{
    public const string ScenarioName = "m7-party-follow-assist";

    public sealed class PartyOptions
    {
        /// <summary>Distance at or below which the member holds formation.</summary>
        public float FollowDistance { get; init; } = 3f;

        public float MoveSpeed { get; init; } = 5f;

        public TimeSpan MoveTimeout { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Runtime seam for deterministic rig movement and live pacing.</summary>
    public interface IPartyRuntime
    {
        ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait);
    }

    public static BotScenarioRunner.ScenarioRunResult Run(Character leader, Character member)
        => Run(leader, member, new LivePartyRuntime(), new PartyOptions());

    public static BotScenarioRunner.ScenarioRunResult Run(
        Character leader, Character member, IPartyRuntime runtime, PartyOptions options)
    {
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        try
        {
            // Party truth comes from the engine registry, not Character.InParty
            // alone (that flag can be stale during disconnect/reconnect edges).
            var leaderTeam = TeamManager.Instance.GetActiveTeamByUnit(leader.Id);
            var memberTeam = TeamManager.Instance.GetActiveTeamByUnit(member.Id);
            if (leaderTeam == null || memberTeam == null || leaderTeam.Id != memberTeam.Id
                || !leaderTeam.IsParty || !leaderTeam.IsMember(leader.Id) || !leaderTeam.IsMember(member.Id))
            {
                return Fail("PARTY-GATE", ActorFailureReason.StateTransition,
                    "leader and member are not active members of the same party",
                    stages, criteria, traceRecords);
            }

            if (leaderTeam.OwnerId != leader.Id)
            {
                return Fail("PARTY-GATE", ActorFailureReason.WrongDecision,
                    $"character {leader.Id} is not party leader {leaderTeam.OwnerId}",
                    stages, criteria, traceRecords);
            }

            if (leader.ParentWorld == null || !ReferenceEquals(leader.ParentWorld, member.ParentWorld))
            {
                return Fail("PARTY-GATE", ActorFailureReason.FidelityError,
                    "party leader and member do not share a world instance",
                    stages, criteria, traceRecords);
            }

            if (options.FollowDistance < 0f || options.MoveSpeed <= 0f || options.MoveTimeout <= TimeSpan.Zero)
            {
                return Fail("RIG", ActorFailureReason.WrongDecision,
                    "follow distance must be non-negative; move speed and timeout must be positive",
                    stages, criteria, traceRecords);
            }

            var actor = new GameplayActor(member);

            // ------------------------------------------------------ 1. FOLLOW
            var distanceBefore = MathUtil.CalculateDistance(
                member.Transform.World.Position, leader.Transform.World.Position, true);
            if (distanceBefore > options.FollowDistance)
            {
                var follow = actor.MoveToUnit(leader.ObjId, options.MoveSpeed, options.MoveTimeout);
                runtime.Drive(actor, follow, options.MoveTimeout);
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage("FOLLOW", leader.ObjId, follow));
                if (follow.State != ActorLifecycleState.Completed)
                {
                    return Fail("FOLLOW", follow.Failure ?? ActorFailureReason.Navigation,
                        $"follow request {follow.State}: {follow.Detail ?? "no detail"}",
                        stages, criteria, traceRecords);
                }
            }
            else
            {
                stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                    "FOLLOW-HOLD", 0, "Completed", leader.ObjId.ToString(),
                    $"member already within follow distance ({distanceBefore:0.###} <= {options.FollowDistance:0.###})"));
            }

            var distanceAfter = MathUtil.CalculateDistance(
                member.Transform.World.Position, leader.Transform.World.Position, true);
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "member-followed-leader", distanceAfter <= options.FollowDistance,
                $"distance {distanceBefore:0.###} -> {distanceAfter:0.###}; threshold {options.FollowDistance:0.###}"));
            if (distanceAfter > options.FollowDistance)
            {
                return Fail("FOLLOW", ActorFailureReason.Navigation,
                    $"member stopped {distanceAfter:0.###} from leader (threshold {options.FollowDistance:0.###})",
                    stages, criteria, traceRecords);
            }

            // ------------------------------------------------------ 2. ASSIST
            var leaderTarget = leader.CurrentTarget;
            if (leaderTarget == null || leaderTarget.ObjId == 0)
            {
                return Fail("ASSIST", ActorFailureReason.WrongDecision,
                    "party leader has no current target to assist",
                    stages, criteria, traceRecords);
            }

            var assist = actor.SetTarget(leaderTarget.ObjId);
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("ASSIST", leaderTarget.ObjId, assist));
            if (assist.State != ActorLifecycleState.Completed)
            {
                return Fail("ASSIST", assist.Failure ?? ActorFailureReason.RejectedAction,
                    $"assist target request {assist.State}: {assist.Detail ?? "no detail"}",
                    stages, criteria, traceRecords);
            }

            var assisted = member.CurrentTarget?.ObjId == leaderTarget.ObjId;
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "member-assisted-leader-target", assisted,
                $"leader target {leaderTarget.ObjId}; member target {member.CurrentTarget?.ObjId ?? 0}"));

            var traceComplete = traceRecords.All(r =>
                r.Result == ActorLifecycleState.Completed
                && r.StateChanges.Any(s => s.Contains("Requested"))
                && r.StateChanges.Any(s => s.Contains("Accepted"))
                && r.StateChanges.Any(s => s.Contains("Completed")));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "lifecycle-trace-complete", traceComplete,
                $"completed records {traceRecords.Count(r => r.Result == ActorLifecycleState.Completed)}/{traceRecords.Count}"));

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
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", stages, criteria, traceRecords);
        }
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, uint target, ActorRequest request)
        => new(name, 1, request.State.ToString(), target.ToString(), request.Detail ?? "");

    private static BotScenarioRunner.ScenarioRunResult Fail(
        string stage, ActorFailureReason failure, string reason,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        List<ActorAuditRecord> traceRecords)
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
}

/// <summary>Live movement pump for the Party v1 follow/assist scenario.</summary>
public sealed class LivePartyRuntime : PartyFollowAssistScenario.IPartyRuntime
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
    {
        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        while (!request.IsTerminal && Environment.TickCount64 < deadline)
        {
            actor.Tick(TickInterval);
            Thread.Sleep(TickInterval);
        }
        return request;
    }
}
