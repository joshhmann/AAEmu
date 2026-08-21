using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 Party v1 (slice 1) — the PartyInvite/PartyAccept contract actions on
/// the IGameplayActor surface, through the REAL engine path:
///  - invite: TeamManager.AskToJoin — the exact CSInviteToTeamPacket call,
///    via the target-object overload (skips the global name registry so
///    headless rigs resolve);
///  - accept: TeamManager.ReplyToJoinTeam — the exact
///    CSReplyToJoinTeamPacket call (isReject: false, isArea: false). With
///    invitation.TeamId 0 the engine creates the team (CreateNewTeam).
/// The engine's refusal modes on both paths are SILENT voids, so the
/// contract post-checks the observable outcomes: the invitation record
/// (invite) and Character.InParty + active-team membership (accept).
///
/// Both actors share ONE world through the rig's JoinActorWorld helper
/// (each CreateActor gets its own session world; ResolveUnit →
/// ParentWorld.GetUnit must see the invite target).
///
/// Contract tests run headless — no controller, no client, no packets
/// (Unit.SendPacket is null-safe without a Connection; the rig's
/// TeamManager seed returns real ChatChannel instances and incrementing
/// team ids so the engine's CreateNewTeam chat wiring runs intact).
///
/// Idempotency proofs (the acceptance-criterion-3 family):
///  - same-key retry: rejected pre-flight by the key gate (no Running
///    transition, invitation/team state untouched);
///  - fresh-key retry while the first invitation is pending: refused by
///    the already-invited pre-flight (StateTransition);
///  - fresh-key re-invite after a successful accept: refused by the
///    already-a-member pre-flight (StateTransition);
///  - accept with no pending invitation: refused BEFORE the engine is
///    entered, so a retry cannot double-join.
/// </summary>
[NotInParallel]
public class GameplayActorPartyTests
{
    [Test]
    public async Task PartyInviteThenAccept_BothMembersOfSameParty()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (inviter, hostSession) = GameplayActorTestRig.CreateActor("m7-party-1a");
        var (guest, _) = GameplayActorTestRig.CreateActor("m7-party-1b");
        GameplayActorTestRig.JoinActorWorld(hostSession, guest);

        // Invite through the real engine path: the invitation record lands.
        var invite = inviter.PartyInvite(guest.Character.ObjId);
        await Assert.That(invite.State).IsEqualTo(ActorLifecycleState.Completed);
        var invitation = TeamManager.Instance.GetActiveInvitation(guest.Character.Id);
        await Assert.That(invitation).IsNotNull();
        await Assert.That(invitation!.Owner.Id).IsEqualTo(inviter.Character.Id);
        await Assert.That(invitation.IsParty).IsTrue();

        // Accept through the real engine path: the team is created and both
        // characters are members (engine sets InParty on both sides).
        var accept = guest.PartyAccept();
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(inviter.Character.InParty).IsTrue();
        await Assert.That(guest.Character.InParty).IsTrue();

        var team = TeamManager.Instance.GetActiveTeamByUnit(inviter.Character.Id);
        await Assert.That(team).IsNotNull();
        await Assert.That(team!.IsParty).IsTrue();
        await Assert.That(team.OwnerId).IsEqualTo(inviter.Character.Id);
        await Assert.That(team.MembersCount()).IsEqualTo(2);
        await Assert.That(team.IsMember(inviter.Character.Id)).IsTrue();
        await Assert.That(team.IsMember(guest.Character.Id)).IsTrue();
        await Assert.That(TeamManager.Instance.GetActiveTeamByUnit(guest.Character.Id)!.Id).IsEqualTo(team.Id);

        // The engine consumed the invitation record on accept.
        await Assert.That(TeamManager.Instance.GetActiveInvitation(guest.Character.Id)).IsNull();

        // Full audit record shape (invite side).
        var record = inviter.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(invite.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(inviter.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.PartyInvite);
        await Assert.That(record.TargetId).IsEqualTo(guest.Character.ObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First().StartsWith("Requested")).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (inviting"))).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();

        // Audit record shape (accept side).
        var acceptRecord = guest.AuditTrace[0];
        await Assert.That(acceptRecord.Action).IsEqualTo(ActorActionType.PartyAccept);
        await Assert.That(acceptRecord.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(acceptRecord.StateChanges.Any(s => s.Contains("Running (accepting"))).IsTrue();
    }

    [Test]
    public async Task PartyAccept_NoPendingInvitation_Rejected_NeverEntersEngine()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (actor, _) = GameplayActorTestRig.CreateActor("m7-party-2a");

        var request = actor.PartyAccept();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("no pending party invitation")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.InParty).IsFalse();
        await Assert.That(TeamManager.Instance.GetActiveTeamByUnit(actor.Character.Id)).IsNull();
    }

    [Test]
    public async Task PartyInvite_TargetAlreadyInvited_RejectedPreFlight()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (inviter, hostSession) = GameplayActorTestRig.CreateActor("m7-party-3a");
        var (guest, _) = GameplayActorTestRig.CreateActor("m7-party-3b");
        GameplayActorTestRig.JoinActorWorld(hostSession, guest);

        await Assert.That(inviter.PartyInvite(guest.Character.ObjId, idempotencyKey: "inv:1").State)
            .IsEqualTo(ActorLifecycleState.Completed);

        // Fresh-key re-invite while the first invitation is still pending:
        // the engine would silently refuse — the contract refuses pre-flight.
        var retry = inviter.PartyInvite(guest.Character.ObjId, idempotencyKey: "inv:2");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("already has a pending")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Exactly one invitation record exists.
        await Assert.That(TeamManager.Instance.GetActiveInvitation(guest.Character.Id)).IsNotNull();
    }

    [Test]
    public async Task PartyInvite_RetrySameKey_RejectedPreFlight_NoDuplicateInvite()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (inviter, hostSession) = GameplayActorTestRig.CreateActor("m7-party-4a");
        var (guest, _) = GameplayActorTestRig.CreateActor("m7-party-4b");
        GameplayActorTestRig.JoinActorWorld(hostSession, guest);

        var original = inviter.PartyInvite(guest.Character.ObjId, idempotencyKey: "inv:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight by the ledger; the audit record shows no Running.
        var retry = inviter.PartyInvite(guest.Character.ObjId, idempotencyKey: "inv:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Correlation: the key still resolves to the ORIGINAL outcome.
        var correlated = inviter.FindByKey("inv:1");
        await Assert.That(correlated).IsNotNull();
        await Assert.That(correlated!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(correlated.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task PartyInvite_AlreadyMembers_RejectedPreFlight()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (inviter, hostSession) = GameplayActorTestRig.CreateActor("m7-party-5a");
        var (guest, _) = GameplayActorTestRig.CreateActor("m7-party-5b");
        GameplayActorTestRig.JoinActorWorld(hostSession, guest);

        await Assert.That(inviter.PartyInvite(guest.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(guest.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);

        // Fresh-key re-invite after the accept: the target already sits in
        // the inviter's team — refused pre-flight, engine never entered.
        var reinvite = inviter.PartyInvite(guest.Character.ObjId, idempotencyKey: "inv:again");
        await Assert.That(reinvite.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(reinvite.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(reinvite.Detail?.Contains("already a member")).IsTrue();
        await Assert.That(reinvite.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Still exactly one team with two members.
        var team = TeamManager.Instance.GetActiveTeamByUnit(inviter.Character.Id);
        await Assert.That(team!.MembersCount()).IsEqualTo(2);
    }

    [Test]
    public async Task PartyInvite_TargetNotInWorld_Rejected()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (inviter, _) = GameplayActorTestRig.CreateActor("m7-party-6a");

        var request = inviter.PartyInvite(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in world")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }
}
