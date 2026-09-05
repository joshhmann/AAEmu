using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 policy-extension rig — PARTY consumer: the deterministic
/// perception → legal-candidate → selection → actor-dispatch → terminal
/// postcondition decision path through <see cref="PartyDecisionScenario"/>
/// on the real TeamManager party surface (the same rig conventions as
/// <see cref="GameplayActorPartyTests"/> / <see cref="PartyFollowAssistScenarioRigTests"/>).
///
/// The scenario composes proposals for invite / accept / follow / assist;
/// hard legality reads ONLY the immutable observation context
/// (PendingInvitationOwnerId / InParty / PartyLeaderObjId /
/// PartyLeaderTargetObjId) plus perception-time ordinary service reads
/// (invite target resolution). Selection is deterministic (fixed priority:
/// accept 40 > invite 30 > follow 20 > assist 10, then tie-break key);
/// dispatch calls the existing IGameplayActor methods only.
///
/// No generated evidence files: these tests are pure asserts.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // process-wide TeamManager singleton + ExecutionBoundary pin
public class PartyDecisionScenarioRigTests
{
    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        GameplayActorTestRig.ForceSeedTeamManager();
    }

    [After(Test)]
    public void TearDown()
    {
        ExecutionBoundary.ResetForTest();
    }

    private static (GameplayActor Leader, GameplayActor Member, GameplayActor Third, HeadlessSession Session) CreatePartyScene(string name)
    {
        var (leader, session) = GameplayActorTestRig.CreateActor(name + "-leader");
        var (member, _) = GameplayActorTestRig.CreateActor(name + "-member");
        var (third, _) = GameplayActorTestRig.CreateActor(name + "-third");
        GameplayActorTestRig.JoinActorWorld(session, member);
        GameplayActorTestRig.JoinActorWorld(session, third);
        return (leader, member, third, session);
    }

    private static void JoinParty(GameplayActor leader, GameplayActor member)
    {
        _ = leader.PartyInvite(member.Character.ObjId);
        _ = member.PartyAccept();
    }

    [Test]
    public async Task PartyDecision_LeaderInPartyWithTarget_InviteWinsAndCompletes()
    {
        var (leader, member, third, session) = CreatePartyScene("m5pt-invite");
        JoinParty(leader, member);
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);
        _ = leader.SetTarget(targetObjId);

        // The LEADER (team owner) runs the decision: in party, has a target,
        // third is inviteable. Invite (30) beats follow (20) and assist (10).
        var result = PartyDecisionScenario.Run(leader.Character, new PartyDecisionScenario.PartyOptions
        {
            InviteTargetObjId = third.Character.ObjId,
            FollowDistance = 3f,
            MoveSpeed = 5f,
            MoveTimeout = TimeSpan.FromSeconds(30)
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.PartyInvite);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        // The invite actually landed through the real engine path (the team
        // owner's AskToJoin is engine-legal).
        await Assert.That(TeamManager.Instance.GetActiveInvitation(third.Character.Id)).IsNotNull();
        await Assert.That(result.Criteria.Any(c => c.Name == "invitation-record-exists" && c.Passed)).IsTrue();
        await Assert.That(result.TraceRecords.Select(r => r.Action)).Contains(ActorActionType.PartyInvite);
    }

    [Test]
    public async Task PartyDecision_PendingInvitation_AcceptWinsAndJoinsTeam()
    {
        var (leader, member, third, session) = CreatePartyScene("m5pt-accept");
        // Member has a pending invitation from the leader (not yet accepted).
        _ = leader.PartyInvite(member.Character.ObjId);
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);
        _ = leader.SetTarget(targetObjId);

        var result = PartyDecisionScenario.Run(member.Character, new PartyDecisionScenario.PartyOptions
        {
            InviteTargetObjId = third.Character.ObjId,
            FollowDistance = 3f,
            MoveSpeed = 5f,
            MoveTimeout = TimeSpan.FromSeconds(30)
        });

        // Accept (40) beats invite (30), follow (20), assist (10).
        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.PartyAccept);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        // The accept actually joined the team through the real engine path.
        await Assert.That(member.Character.InParty).IsTrue();
        var team = TeamManager.Instance.GetActiveTeamByUnit(member.Character.Id);
        await Assert.That(team).IsNotNull();
        await Assert.That(team!.OwnerId).IsEqualTo(leader.Character.Id);
        await Assert.That(team.IsMember(leader.Character.Id)).IsTrue();
        await Assert.That(team.IsMember(member.Character.Id)).IsTrue();
        await Assert.That(result.TraceRecords.Select(r => r.Action)).Contains(ActorActionType.PartyAccept);
    }

    [Test]
    public async Task PartyDecision_NotInParty_OnlyInviteLegal_InviteWins()
    {
        var (leader, member, third, session) = CreatePartyScene("m5pt-solo");
        // Member is NOT in a party: follow/assist illegal (no leader), accept
        // illegal (no pending invitation). Only invite is legal.
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);
        _ = leader.SetTarget(targetObjId);

        var result = PartyDecisionScenario.Run(member.Character, new PartyDecisionScenario.PartyOptions
        {
            InviteTargetObjId = third.Character.ObjId,
            FollowDistance = 3f,
            MoveSpeed = 5f,
            MoveTimeout = TimeSpan.FromSeconds(30)
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.PartyInvite);
        // The invite landed on the third character.
        await Assert.That(TeamManager.Instance.GetActiveInvitation(third.Character.Id)).IsNotNull();
        // Legality-before-preference evidence: accept/follow/assist were
        // rejected by named preconditions.
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.PartyAccept && r.Reason.Contains("pending-invitation"))).IsTrue();
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Move && r.Reason.Contains("in-party"))).IsTrue();
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Target && r.Reason.Contains("in-party-with-leader-target"))).IsTrue();
    }

    [Test]
    public async Task PartyDecision_LeaderHasNoTarget_AssistIllegal_InviteWins()
    {
        var (leader, member, third, session) = CreatePartyScene("m5pt-noassist");
        JoinParty(leader, member);
        // Leader has NO target: assist illegal. Invite is legal (third is
        // inviteable) and beats follow — invite wins.
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);

        var result = PartyDecisionScenario.Run(leader.Character, new PartyDecisionScenario.PartyOptions
        {
            InviteTargetObjId = third.Character.ObjId,
            FollowDistance = 3f,
            MoveSpeed = 5f,
            MoveTimeout = TimeSpan.FromSeconds(30)
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.PartyInvite);
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Target && r.Reason.Contains("in-party-with-leader-target"))).IsTrue();
    }

    [Test]
    public async Task PartyDecision_FollowDispatch_MovesMemberToLeader()
    {
        var (leader, member, third, session) = CreatePartyScene("m5pt-move");
        JoinParty(leader, member);
        // Make invite illegal (third already invited) so follow (20) wins
        // over invite (30): the follow leg is the dispatch under test.
        _ = leader.PartyInvite(third.Character.ObjId);
        leader.Character.Transform.Local.SetPosition(new Vector3(20, 0, 0));
        member.Character.Transform.Local.SetPosition(Vector3.Zero);
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);
        _ = leader.SetTarget(targetObjId);

        var result = PartyDecisionScenario.Run(member.Character, new PartyDecisionScenario.PartyOptions
        {
            InviteTargetObjId = third.Character.ObjId,
            FollowDistance = 3f,
            MoveSpeed = 5f,
            MoveTimeout = TimeSpan.FromSeconds(30)
        });

        await Assert.That(result.Passed, result.Evidence()).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        // The member actually moved to the leader through the real movement
        // model (the follow postcondition is distance-based).
        await Assert.That(MathUtil.CalculateDistance(
            member.Character.Transform.World.Position, leader.Character.Transform.World.Position, true))
            .IsLessThanOrEqualTo(3f);
        await Assert.That(result.TraceRecords.Select(r => r.Action)).Contains(ActorActionType.Move);
    }
}
