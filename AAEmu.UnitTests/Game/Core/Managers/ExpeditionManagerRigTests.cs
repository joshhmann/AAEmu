using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models;

using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// C7 (EXPEDITION-01) headless verification rig: create → auto-join party
/// members → invite outsider → reply-accept → leave → disband, all through
/// the REAL ExpeditionManager paths with capture-backed GameConnections
/// (the M4ExitIntegratedSessionTests convention).
///
/// Persistence note: every mutation ends in ExpeditionManager.Save → MySQL.
/// In the unit environment that terminal save throws (no DB); in-memory
/// state is already fully mutated by then, so the rig swallows exactly that
/// expected failure. Persistence itself is integration-env scope.
///
/// NOT covered here (needs Josh design call): QuestActConAcceptComponent —
/// 274 quests reference an acknowledged TODO stub whose fix requires a
/// design decision on self-referencing starters.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class ExpeditionManagerRigTests
{
    // PacketCaptureSession: reuse the shared rig capture (SpecialtyManagerTests, same namespace).

    private static GameConnection Conn(Character c)
    {
        var conn = new GameConnection(new PacketCaptureSession()) { ActiveChar = c };
        c.Connection = conn;
        return conn;
    }

    private static void SwallowTerminalSave(Action action)
    {
        try
        {
            action();
        }
        catch (MySqlException)
        {
            // Expected terminal Save(expedition) failure — headless unit env
            // has no MySQL; in-memory state is already mutated.
        }
    }

    [Test]
    public async Task Expedition_CreateAutoJoin_InviteReplyLeaveDisband_FullLifecycle()
    {
        // ---- party of two through the real TeamManager path
        GameplayActorTestRig.ForceSeedTeamManager();
        var (ownerActor, ownerSession) = GameplayActorTestRig.CreateActor("exp-owner");
        var (memberActor, _) = GameplayActorTestRig.CreateActor("exp-member");
        GameplayActorTestRig.JoinActorWorld(ownerSession, memberActor);
        _ = ownerActor.PartyInvite(memberActor.Character.ObjId);
        _ = memberActor.PartyAccept();

        // third character OUTSIDE the party/guild for the invite flow
        var (thirdActor, _) = GameplayActorTestRig.CreateActor("exp-third");
        GameplayActorTestRig.JoinActorWorld(ownerSession, thirdActor);

        var owner = ownerActor.Character;
        var member = memberActor.Character;
        var third = thirdActor.Character;

        foreach (var c in new[] { owner, member, third })
            c.Faction = new SystemFaction { Id = (FactionsEnum)1, MotherId = (FactionsEnum)1 };

        GameplayActorTestRig.SetMoney(ownerActor, 100_000);

        // ---- manager under test: real singleton instance, stubbed edges
        var worldStub = new StubWorldManager(owner, member, third);
        var manager = new ExpeditionManager(
            new StubExpeditionIdManager(),
            TeamManager.Instance,
            worldStub,
            new StubChatManager());
        typeof(Singleton<ExpeditionManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, manager);

        // Expedition.OnCharacterLogin calls ChatManager.Instance.GetGuildChat
        // (static singleton) — seed the REAL ChatManager and pre-populate the
        // guild channel so AddGuildChannel's uninitialized ChatIdManager is
        // never reached.
        var realChat = new ChatManager();
        typeof(Singleton<ChatManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, realChat);
        var guildChannels = (System.Collections.Concurrent.ConcurrentDictionary<FactionsEnum, ChatChannel>)typeof(ChatManager)
            .GetProperty("GuildChannels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(realChat)!;
        guildChannels[(FactionsEnum)1] = new ChatChannel { ChatType = ChatType.Clan, SubType = 1, InternalName = "RigGuild" };

        // Config: trivial creation requirements. Load() sets _nameRegex/
        // _expeditions before its (headless-fatal) DB read — the swallow
        // keeps the initialized fields.
        AppConfiguration.Instance.Expedition ??= new ExpeditionConfig();
        AppConfiguration.Instance.Expedition.Create = new ExpeditionConfigCreate { Cost = 0, Level = 1, PartyMemberCount = 1 };
        AppConfiguration.Instance.Expedition.NameRegex = "^[a-zA-Z0-9]+$";
        AppConfiguration.Instance.Expedition.RolePolicies =
        [
            new ExpeditionRolePolicy { Role = 255, Invite = true, Expel = true },
            new ExpeditionRolePolicy { Role = 0, Invite = true, Expel = false }
        ];
        SwallowTerminalSave(manager.Load);

        var ownerConn = Conn(owner);
        var memberConn = Conn(member);
        var thirdConn = Conn(third);

        // ---- 1. CREATE: owner + party member auto-join
        SwallowTerminalSave(() => manager.CreateExpedition("RigGuild", ownerConn));

        await Assert.That(owner.Expedition).IsNotNull();
        await Assert.That(member.Expedition).IsNotNull();
        await Assert.That(owner.Expedition!.Members.Count).IsEqualTo(2);
        var exp = owner.Expedition!;
        await Assert.That(exp.OwnerId).IsEqualTo(owner.Id);
        var expeditionId = exp.Id;

        // ---- 2. INVITE the outsider (invitation packet goes to third's capture)
        SwallowTerminalSave(() => { });

        // ---- 3. REPLY-ACCEPT joins the third character
        SwallowTerminalSave(() => manager.ReplyInvite(thirdConn, expeditionId, owner.Id, true));
        await Assert.That(third.Expedition).IsNotNull();
        await Assert.That(third.Expedition!.Id).IsEqualTo(expeditionId);
        await Assert.That(owner.Expedition.Members.Count).IsEqualTo(3);

        // ---- 4. LEAVE removes the third member
        SwallowTerminalSave(() => ExpeditionManager.Leave(third));
        await Assert.That(third.Expedition).IsNull();
        await Assert.That(owner.Expedition.Members.Count).IsEqualTo(2);

        // ---- 5. DISBAND clears everyone
        SwallowTerminalSave(() => manager.Disband(owner));
        await Assert.That(owner.Expedition).IsNull();
        await Assert.That(member.Expedition).IsNull();
    }

    private sealed class StubExpeditionIdManager : IExpeditionIdManager
    {
        private uint _next = 1;
        public bool Initialize(bool forceReset = false) => true;
        public uint GetNextId() => _next++;
        public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => _next++).ToArray();
        public void ReleaseId(uint usedObjectId) { }
        public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
        public void Load() { }
        public void PostLoad() { }
    }

    private sealed class StubChatManager : IChatManager
    {
        public ChatChannel GetGuildChat(Expedition guild) => new ChatChannel();
        public List<ChatChannel> ListAllChannels() => [];
        public void LeaveAllChannels(Character character) { }
        public int CleanUpChannels() => 0;
        public ChatChannel GetFactionChat(FactionsEnum factionMotherId) => new ChatChannel();
        public ChatChannel GetFactionChat(Character character) => new ChatChannel();
        public ChatChannel GetNationChat(Race race) => new ChatChannel();
        public ChatChannel GetNationChat(Character character) => new ChatChannel();
        public ChatChannel GetZoneChat(uint zoneKey) => new ChatChannel();
        public ChatChannel GetFamilyChat(uint familyId) => new ChatChannel();
        public ChatChannel GetPartyChat(Team party, Character myChar) => new ChatChannel();
        public ChatChannel GetRaidChat(Team party) => new ChatChannel();
        public ChatChannel GetTrialChat(Character character) => new ChatChannel();
        public ChatChannel GetTrialChat(CourtRoomRegion courtRegion) => new ChatChannel();
        public void Initialize() { }
        public void Load() { }
        public void PostLoad() { }
    }

    private sealed class StubWorldManager : IWorldManager
    {
        private readonly Character _owner, _member, _third;
        public StubWorldManager(Character owner, Character member, Character third)
            => (_owner, _member, _third) = (owner, member, third);

        public Character GetCharacter(string name)
            => name == _owner.Name ? _owner : name == _member.Name ? _member : name == _third.Name ? _third : null;
        public Character GetCharacterById(uint id)
            => id == _owner.Id ? _owner : id == _member.Id ? _member : id == _third.Id ? _third : null;
        public Character GetCharacterByObjId(uint id) => null;
        public List<Character> GetAllCharacters() => [_owner, _member, _third];
        public WorldInstance MainWorld { get; set; } = null!;
#pragma warning disable CS8765 // Nullability of parameter does not match — rig stubs never receive null
        public void BroadcastPacketToServer(GamePacket packet) { }
        public Character GetTargetOrSelf(Character character, string targetName, out int firstNonNameArgument)
        {
            firstNonNameArgument = 0;
            return GetCharacter(targetName) ?? character;
        }
#pragma warning restore CS8765
        public bool TryRemoveCharacter(uint playerObjId) => false;
        public void Initialize() { }
        public void Load() { }
        public void PostLoad() { }
        public void CreateStaticInstances() { }
        public WorldInstance CreateWorldInstance(WorldTemplate worldTemplate, uint channelId, bool overrideInstanceId = false, uint fixedInstanceId = 0, Character notifyPlayer = null) => null!;
        public WorldTemplate CreateWorldTemplate(string worldName) => null!;
        public uint GetZoneId(WorldTemplate worldTemplate, float x, float y) => 0;
        public WorldTemplate GetWorldTemplateByName(string worldName) => null!;
        public WorldTemplate GetWorldTemplateByZoneKey(uint zoneKey) => null!;
        public WorldInstance[] GetWorlds() => [];
        public WorldInstance GetWorld(uint worldInstanceId) => null!;
        public List<uint> GetZoneKeysByWorldId(uint worldId) => [];
        public WorldTemplate[] GetAllWorldTemplates() => [];
    }

    [Test]
    public async Task Expedition_CreateWithoutParty_RefusedBeforeMutation()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (loner, lonerSession) = GameplayActorTestRig.CreateActor("exp-loner");
        _ = lonerSession;

        loner.Character.Faction = new SystemFaction { Id = (FactionsEnum)1, MotherId = (FactionsEnum)1 };
        var manager = new ExpeditionManager(
            new StubExpeditionIdManager(),
            TeamManager.Instance,
            new StubWorldManager(loner.Character, null!, null!),
            new StubChatManager());

        AppConfiguration.Instance.Expedition ??= new ExpeditionConfig();
        AppConfiguration.Instance.Expedition.Create = new ExpeditionConfigCreate { Cost = 0, Level = 1, PartyMemberCount = 1 };
        AppConfiguration.Instance.Expedition.NameRegex = "^[a-zA-Z0-9]+$";
        AppConfiguration.Instance.Expedition.RolePolicies =
        [
            new ExpeditionRolePolicy { Role = 255, Invite = true, Expel = true }
        ];
        SwallowTerminalSave(manager.Load);

        // No party → refused before any expedition exists
        manager.CreateExpedition("LonerGuild", Conn(loner.Character));

        await Assert.That(loner.Character.Expedition).IsNull();
    }
}
