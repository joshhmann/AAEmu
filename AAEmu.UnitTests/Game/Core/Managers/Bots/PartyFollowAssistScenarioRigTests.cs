using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 Party v1 slice 2 rig: follow leader and assist target are scenario
/// composition over the existing MoveToUnit and SetTarget contract actions.
/// The two ordinary Characters share one real TeamManager party and world.
/// </summary>
[NotInParallel]
public class PartyFollowAssistScenarioRigTests
{
    private sealed class RigPartyRuntime : PartyFollowAssistScenario.IPartyRuntime
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
        {
            var elapsed = TimeSpan.Zero;
            var tick = TimeSpan.FromMilliseconds(100);
            while (!request.IsTerminal && elapsed <= maxWait)
            {
                actor.Tick(tick);
                elapsed += tick;
            }
            return request;
        }
    }

    [Test]
    public async Task PartyScenario_DistantMember_FollowsLeaderAndAssistsTarget()
    {
        var (leader, member, session) = CreateParty("m7-party-follow");
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);
        await Assert.That(leader.SetTarget(targetObjId).State).IsEqualTo(ActorLifecycleState.Completed);

        var result = PartyFollowAssistScenario.Run(
            leader.Character, member.Character, new RigPartyRuntime(), new PartyFollowAssistScenario.PartyOptions());

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.Stages.Select(s => s.Stage)).IsEquivalentTo(new[] { "FOLLOW", "ASSIST" });
        await Assert.That(result.TraceRecords.Select(r => r.Action)).IsEquivalentTo(
            new[] { ActorActionType.Move, ActorActionType.Target });
        await Assert.That(member.Character.CurrentTarget?.ObjId).IsEqualTo(targetObjId);
        await Assert.That(MathUtil.CalculateDistance(
            member.Character.Transform.World.Position, leader.Character.Transform.World.Position, true)).IsLessThanOrEqualTo(3f);
        await Assert.That(result.Criteria.All(c => c.Passed)).IsTrue();
    }

    [Test]
    public async Task PartyScenario_MemberInFormation_HoldsThenAssistsWithoutMove()
    {
        var (leader, member, session) = CreateParty("m7-party-hold");
        leader.Character.Transform.Local.SetPosition(new Vector3(2, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);
        _ = leader.SetTarget(targetObjId);

        var result = PartyFollowAssistScenario.Run(
            leader.Character, member.Character, new RigPartyRuntime(), new PartyFollowAssistScenario.PartyOptions());

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.Stages.Select(s => s.Stage)).IsEquivalentTo(new[] { "FOLLOW-HOLD", "ASSIST" });
        await Assert.That(result.TraceRecords.Count).IsEqualTo(1);
        await Assert.That(result.TraceRecords[0].Action).IsEqualTo(ActorActionType.Target);
        await Assert.That(member.Character.Transform.World.Position).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task PartyScenario_CharactersNotInSameParty_FailsBeforeMutation()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, session) = GameplayActorTestRig.CreateActor("m7-party-gate-a");
        var (member, _) = GameplayActorTestRig.CreateActor("m7-party-gate-b");
        GameplayActorTestRig.JoinActorWorld(session, member);
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        var start = member.Character.Transform.World.Position;

        var result = PartyFollowAssistScenario.Run(
            leader.Character, member.Character, new RigPartyRuntime(), new PartyFollowAssistScenario.PartyOptions());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PARTY-GATE");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(result.TraceRecords).IsEmpty();
        await Assert.That(member.Character.Transform.World.Position).IsEqualTo(start);
        await Assert.That(member.Character.CurrentTarget).IsNull();
    }

    [Test]
    public async Task PartyScenario_LeaderHasNoTarget_FailsClosedAfterFollow()
    {
        var (leader, member, _) = CreateParty("m7-party-no-target");
        leader.Character.Transform.Local.SetPosition(new Vector3(10, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);

        var result = PartyFollowAssistScenario.Run(
            leader.Character, member.Character, new RigPartyRuntime(), new PartyFollowAssistScenario.PartyOptions());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("ASSIST");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.WrongDecision);
        await Assert.That(result.TraceRecords.Count).IsEqualTo(1);
        await Assert.That(result.TraceRecords[0].Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(member.Character.CurrentTarget).IsNull();
    }

    private static (GameplayActor Leader, GameplayActor Member, AAEmu.Game.Models.Game.Bots.HeadlessSession Session)
        CreateParty(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, session) = GameplayActorTestRig.CreateActor(name + "-leader");
        var (member, _) = GameplayActorTestRig.CreateActor(name + "-member");
        GameplayActorTestRig.JoinActorWorld(session, member);
        _ = leader.PartyInvite(member.Character.ObjId);
        _ = member.PartyAccept();
        return (leader, member, session);
    }
}
