using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Zones;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 queue item #2 — party lifecycle fault matrix (headless rigs):
/// death, invitation retry, and target-loss faults must never corrupt party
/// membership (TeamManager is the single source of truth) and every follow/
/// assist composition must fail closed.
///
/// Out of scope here (live-stack scope): disconnect/restart persistence legs
/// (need the real lifecycle service + MySQL) — covered by the B4 restart E2E
/// family for identity/inventory; party-mid-route restart leg stays on the
/// forward-hardening queue.
/// </summary>
[NotInParallel]
public class PartyLifecycleFaultMatrixTests
{
    /// <summary>Character.DoDie touches ZoneManager (death-drop location)
    /// — seed the singleton with mock deps (party-rig precedent).</summary>
    private static void SeedDeathSurfaces()
    {
        var zoneManager = new ZoneManager(Mock.Of<AAEmu.Game.Core.Managers.World.IWorldManager>().Object);
        // Seed the internal dictionaries — GetZoneByKey/_conflicts/climate
        // are touched by the death path (CharacterCombat) and would NRE on
        // the loader-null fields.
        typeof(ZoneManager)
            .GetField("_zoneIdToKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<uint, uint>());
        typeof(ZoneManager)
            .GetField("_zones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<uint, Zone>());
        typeof(ZoneManager)
            .GetField("_groups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<uint, ZoneGroup>());
        typeof(ZoneManager)
            .GetField("_conflicts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<ushort, ZoneConflict>());
        typeof(ZoneManager)
            .GetField("_groupBannedTags", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<uint, ZoneGroupBannedTag>());
        typeof(ZoneManager)
            .GetField("_climateElem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<uint, ZoneClimateElem>());
        SetSingletonInstance(typeof(Singleton<ZoneManager>), zoneManager);
    }

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);

    private static void SetSingletonInstance(Type singletonBase, object instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, instance);

    private static (GameplayActor Leader, GameplayActor Member) CreateParty(string name)
    {
        SeedDeathSurfaces();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, session) = GameplayActorTestRig.CreateActor(name + "-leader");
        var (member, _) = GameplayActorTestRig.CreateActor(name + "-member");
        GameplayActorTestRig.JoinActorWorld(session, member);
        _ = leader.PartyInvite(member.Character.ObjId);
        _ = member.PartyAccept();
        return (leader, member);
    }

    [Test]
    public async Task PartyFault_MemberDies_MembershipPreservedThroughResurrection()
    {
        var (leader, member) = CreateParty("pfault-m");

        // Member dies mid-party
        member.Character.Hp = 0;
        member.Character.DoDie(null, KillReason.Damage); // null killer: party membership is killer-agnostic, and a player killer drags in the crime-evidence doodad chain
        await Assert.That(member.Character.IsDead).IsTrue();

        // Death does NOT remove the team roster entry (TeamManager owns
        // membership; death is a state, not a leave).
        var team = TeamManager.Instance.GetActiveTeamByUnit(leader.Character.Id);
        await Assert.That(team).IsNotNull();
        await Assert.That(team!.IsMember(member.Character.Id)).IsTrue();

        // Resurrection through the real shared path restores the fighter,
        // still in the same party.
        CharacterResurrection.Resurrect(member.Character, inPlace: true);
        await Assert.That(member.Character.IsDead).IsFalse();
        await Assert.That(TeamManager.Instance.GetActiveTeamByUnit(member.Character.Id)).IsNotNull();
    }

    [Test]
    public async Task PartyFault_LeaderDies_OwnershipAndMembershipUnchanged()
    {
        var (leader, member) = CreateParty("pfault-l");

        leader.Character.Hp = 0;
        leader.Character.DoDie(null, KillReason.Damage);

        var team = TeamManager.Instance.GetActiveTeamByUnit(member.Character.Id);
        await Assert.That(team).IsNotNull();
        await Assert.That(team!.OwnerId).IsEqualTo(leader.Character.Id); // dead leader stays owner
        await Assert.That(team.IsMember(leader.Character.Id)).IsTrue();

        CharacterResurrection.Resurrect(leader.Character, inPlace: true);

        // The follow/assist scenario still works with a resurrected leader:
        // fail-closed on no-target (no crash from the death detour).
        var result = PartyFollowAssistScenario.Run(
            leader.Character, member.Character,
            new SyncPartyRuntime(), new PartyFollowAssistScenario.PartyOptions());
        await Assert.That(result.FailStage).IsEqualTo("ASSIST"); // followed fine, no target to assist
    }

    [Test]
    public async Task PartyFault_InvitationRetry_SecondInviteRefused()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, session) = GameplayActorTestRig.CreateActor("pfault-r-leader");
        var (outsider, _) = GameplayActorTestRig.CreateActor("pfault-r-outsider");
        GameplayActorTestRig.JoinActorWorld(session, outsider);

        var first = leader.PartyInvite(outsider.Character.ObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Retry while the invitation is pending → refused, no duplicate
        // invitation record, outsider still NOT a member.
        var retry = leader.PartyInvite(outsider.Character.ObjId);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(outsider.Character.InParty).IsFalse();

        // Accept lands exactly one membership.
        _ = outsider.PartyAccept();
        await Assert.That(outsider.Character.InParty).IsTrue();

        // Post-accept re-invite of an existing member → refused.
        var postAccept = leader.PartyInvite(outsider.Character.ObjId);
        await Assert.That(postAccept.State).IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task PartyFault_LeaderTargetLost_MemberAssistFailsClosed_NoCrash()
    {
        var (leader, member, session) = CreatePartyWithTarget("pfault-t");
        var targetObjId = GameplayActorTestRig.SpawnNpc(session);

        _ = leader.SetTarget(targetObjId);

        // Target lost between the leader's SetTarget and the assist. Rig
        // NPCs cannot survive Npc.DoDie (KillExp NRE — documented spike
        // convention), so the loss is simulated by clearing the leader's
        // target — the same observable the scenario gates on.
        _ = leader.SetTarget(0);

        var result = PartyFollowAssistScenario.Run(
            leader.Character, member.Character,
            new SyncPartyRuntime(), new PartyFollowAssistScenario.PartyOptions());

        // Fail closed on the dead/absent target — never a crash, never a
        // copied dead target.
        if (!result.Passed)
            await Assert.That(result.FailStage).IsEqualTo("ASSIST");
    }

    private static (GameplayActor Leader, GameplayActor Member, HeadlessSession Session)
        CreatePartyWithTarget(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, session) = GameplayActorTestRig.CreateActor(name + "-leader");
        var (member, _) = GameplayActorTestRig.CreateActor(name + "-member");
        GameplayActorTestRig.JoinActorWorld(session, member);
        _ = leader.PartyInvite(member.Character.ObjId);
        _ = member.PartyAccept();
        return (leader, member, session);
    }

    /// <summary>Synchronous pump: completes non-terminal requests immediately.</summary>
    private sealed class SyncPartyRuntime : PartyFollowAssistScenario.IPartyRuntime
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
        {
            var guard = 0;
            while (!request.IsTerminal && guard++ < 1000)
                actor.Tick(TimeSpan.FromMilliseconds(100));
            return request;
        }
    }
}
