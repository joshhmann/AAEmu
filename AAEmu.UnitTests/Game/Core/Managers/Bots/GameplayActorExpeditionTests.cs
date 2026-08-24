using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Core.Network.Game;

using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.2 expedition contract actions on the IGameplayActor surface, through
/// the REAL engine path (mirrors the M7 Party v1 rig conventions and the
/// ExpeditionManagerRigTests seeding):
///  - create: ExpeditionManager.CreateExpedition — the exact
///    CSCreateExpeditionPacket call (party members auto-join);
///  - invite: ExpeditionManager.Invite — the exact
///    CSInviteToExpeditionPacket call (the invitation IS the client packet;
///    every engine refusal is a silent void mirrored pre-flight);
///  - accept: ExpeditionManager.ReplyInvite(join=true) — the exact
///    CSReplyExpeditionInvitationPacket call (NO server-side invitation
///    record exists, so the contract refuses unknown expeditions /
///    non-member inviters pre-flight);
///  - leave: the static ExpeditionManager.Leave.
///
/// Connection-mediated paths ride the rig's AttachCaptureConnection (the
/// ExpeditionManagerRigTests.Conn convention): capture-backed
/// GameConnections, no network.
///
/// Persistence note: every mutation ends in ExpeditionManager.Save → MySQL.
/// The ACTOR swallows exactly that terminal failure (RunExpeditionEngineCall)
/// and post-checks verified state, so these tests need no save swallowing.
/// </summary>
[NotInParallel]
public class GameplayActorExpeditionTests
{
    private sealed class StubExpeditionIdManager : AAEmu.Game.Core.Managers.Id.IExpeditionIdManager
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
        private readonly Dictionary<string, Character> _byName;
        public StubWorldManager(params Character[] characters)
            => _byName = characters.ToDictionary(c => c.Name, c => c);

        public Character GetCharacter(string name)
            => _byName.GetValueOrDefault(name);
        public Character GetCharacterById(uint id)
            => _byName.Values.FirstOrDefault(c => c.Id == id);
        public Character GetCharacterByObjId(uint id) => null;
        public List<Character> GetAllCharacters() => [.. _byName.Values];
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

    /// <summary>
    /// Three actors in ONE world, owner+member in a party (real TeamManager),
    /// and the REAL ExpeditionManager singleton swapped for a stub-wired
    /// instance with trivial creation requirements (the
    /// ExpeditionManagerRigTests convention: config overrides, Load-swallow,
    /// ChatManager singleton pre-population).
    /// </summary>
    private static (GameplayActor Owner, GameplayActor Member, GameplayActor Third, GameConnection OwnerConn) SetupThreeActorRig(
        string ownerName, string memberName, string thirdName)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, hostSession) = GameplayActorTestRig.CreateActor(ownerName);
        var (member, _) = GameplayActorTestRig.CreateActor(memberName);
        var (third, _) = GameplayActorTestRig.CreateActor(thirdName);
        GameplayActorTestRig.JoinActorWorld(hostSession, member);
        GameplayActorTestRig.JoinActorWorld(hostSession, third);

        foreach (var c in new[] { owner.Character, member.Character, third.Character })
            c.Faction = new SystemFaction { Id = (FactionsEnum)1, MotherId = (FactionsEnum)1 };

        GameplayActorTestRig.SetMoney(owner, 100_000);

        // Party of two through the REAL engine path (founding requirement).
        _ = owner.PartyInvite(member.Character.ObjId);
        _ = member.PartyAccept();

        var manager = new ExpeditionManager(
            new StubExpeditionIdManager(),
            TeamManager.Instance,
            new StubWorldManager(owner.Character, member.Character, third.Character),
            new StubChatManager());
        typeof(Singleton<ExpeditionManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, manager);

        // Expedition.OnCharacterLogin calls ChatManager.Instance.GetGuildChat
        // (static singleton) — seed the REAL ChatManager and pre-populate the
        // guild channel so AddGuildChannel's uninitialized ChatIdManager is
        // never reached (stub id manager hands out expedition id 1).
        var realChat = new ChatManager();
        typeof(Singleton<ChatManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, realChat);
        var guildChannels = (System.Collections.Concurrent.ConcurrentDictionary<FactionsEnum, ChatChannel>)typeof(ChatManager)
            .GetProperty("GuildChannels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(realChat)!;
        guildChannels[(FactionsEnum)1] = new ChatChannel { ChatType = ChatType.Clan, SubType = 1, InternalName = "BotGuild" };

        // Config: trivial creation requirements; Load() sets _nameRegex/
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
        try
        {
            manager.Load();
        }
        catch (MySqlException)
        {
            // Expected terminal Load failure — headless unit env has no MySQL.
        }

        return (owner, member, third, GameplayActorTestRig.AttachCaptureConnection(owner));
    }

    [Test]
    public async Task Expedition_CreateInviteAcceptLeave_ThreeActorHappyPath()
    {
        var (owner, member, third, _) = SetupThreeActorRig("m7-exp-1-owner", "m7-exp-1-member", "m7-exp-1-third");

        // ---- 1. CREATE: owner + party member auto-join through the real path
        var create = owner.ExpeditionCreate("BotGuild");
        await Assert.That(create.State).IsEqualTo(ActorLifecycleState.Completed);

        await Assert.That(owner.Character.Expedition).IsNotNull();
        await Assert.That(member.Character.Expedition).IsNotNull();
        var exp = owner.Character.Expedition!;
        await Assert.That((uint)exp.Id).IsEqualTo(1u); // stub id manager's first id
        await Assert.That(exp.OwnerId).IsEqualTo(owner.Character.Id);
        await Assert.That(exp.Members.Count).IsEqualTo(2);
        await Assert.That(exp.GetMember(owner.Character)).IsNotNull();
        await Assert.That(exp.GetMember(member.Character)).IsNotNull();
        await Assert.That(create.Detail?.Contains("2 founding member")).IsTrue();

        // ---- 2. INVITE the outsider (silent-void engine; all gates mirrored)
        var invite = owner.ExpeditionInvite(third.Character.Name);
        await Assert.That(invite.State).IsEqualTo(ActorLifecycleState.Completed);

        // ---- 3. ACCEPT joins the third character (the invitation proof)
        var accept = third.ExpeditionAccept(exp.Id, owner.Character.Id);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(third.Character.Expedition!.Id).IsEqualTo(exp.Id);
        await Assert.That(exp.Members.Count).IsEqualTo(3);
        await Assert.That(exp.GetMember(third.Character)).IsNotNull();

        // ---- 4. LEAVE removes the third member again
        var leave = third.ExpeditionLeave();
        await Assert.That(leave.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(third.Character.Expedition).IsNull();
        await Assert.That(exp.Members.Count).IsEqualTo(2);

        // ---- Audit shape: every action emitted its Completed record
        await Assert.That(owner.AuditTrace.Any(r =>
            r.Action == ActorActionType.ExpeditionCreate && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(owner.AuditTrace.Any(r =>
            r.Action == ActorActionType.ExpeditionInvite && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(third.AuditTrace.Any(r =>
            r.Action == ActorActionType.ExpeditionAccept && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(third.AuditTrace.Any(r =>
            r.Action == ActorActionType.ExpeditionLeave && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task Expedition_AlreadyInExpedition_RefusedPreFlight_EngineNeverReentered()
    {
        var (owner, member, _, _) = SetupThreeActorRig("m7-exp-2-owner", "m7-exp-2-member", "m7-exp-2-third");

        var create = owner.ExpeditionCreate("BotGuild");
        await Assert.That(create.State).IsEqualTo(ActorLifecycleState.Completed);
        var exp = owner.Character.Expedition!;
        var membersBefore = exp.Members.Count;

        // Fresh-key second CREATE while already a member: refused pre-flight.
        var secondCreate = owner.ExpeditionCreate("OtherGuild");
        await Assert.That(secondCreate.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(secondCreate.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(secondCreate.Detail?.Contains("already in an expedition")).IsTrue();
        await Assert.That(secondCreate.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Member-side ACCEPT while already a member: ReplyInvite has NO guard
        // (it would add a duplicate roster row) — the contract refuses
        // pre-flight, the engine is never entered.
        var memberAccept = member.ExpeditionAccept(exp.Id, owner.Character.Id);
        await Assert.That(memberAccept.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(memberAccept.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(memberAccept.Detail?.Contains("already in an expedition")).IsTrue();
        await Assert.That(memberAccept.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        await Assert.That(exp.Members.Count).IsEqualTo(membersBefore);
    }

    [Test]
    public async Task Expedition_NonMemberInvite_And_BadTargets_RefusedPreFlight()
    {
        var (owner, member, third, _) = SetupThreeActorRig("m7-exp-3-owner", "m7-exp-3-member", "m7-exp-3-third");

        var create = owner.ExpeditionCreate("BotGuild");
        await Assert.That(create.State).IsEqualTo(ActorLifecycleState.Completed);

        // A NON-MEMBER cannot invite (engine gate: inviterMember == null is a
        // silent void — the contract refuses pre-flight).
        var outsiderInvite = third.ExpeditionInvite(member.Character.Name);
        await Assert.That(outsiderInvite.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(outsiderInvite.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(outsiderInvite.Detail?.Contains("not an expedition member with invite rights")).IsTrue();
        await Assert.That(outsiderInvite.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Unknown invite target: RejectedAction, engine never entered.
        var unknownTarget = owner.ExpeditionInvite("no-such-character");
        await Assert.That(unknownTarget.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(unknownTarget.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(unknownTarget.Detail?.Contains("not found")).IsTrue();
        await Assert.That(unknownTarget.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Target already in the expedition: StateTransition, engine silent
        // void mirrored pre-flight.
        var alreadyMember = owner.ExpeditionInvite(member.Character.Name);
        await Assert.That(alreadyMember.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(alreadyMember.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(alreadyMember.Detail?.Contains("already in an expedition")).IsTrue();
        await Assert.That(alreadyMember.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Nothing changed: still two members, third expedition-less.
        await Assert.That(owner.Character.Expedition!.Members.Count).IsEqualTo(2);
        await Assert.That(third.Character.Expedition).IsNull();
    }

    [Test]
    public async Task Expedition_Accept_UnknownOrForeignExpedition_RefusedPreFlight()
    {
        var (_, _, third, _) = SetupThreeActorRig("m7-exp-4-owner", "m7-exp-4-member", "m7-exp-4-third");

        // Unknown expedition id: ReplyInvite would THROW (unguarded registry
        // index) — the contract refuses pre-flight.
        var unknown = third.ExpeditionAccept((FactionsEnum)999, 1234u);
        await Assert.That(unknown.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(unknown.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(unknown.Detail?.Contains("not found")).IsTrue();
        await Assert.That(unknown.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Real expedition, but the claimed inviter is NOT a member: no such
        // invitation exists server-side — refused pre-flight.
        var (_, ownerB, _, _) = SetupThreeActorRig("m7-exp-4b-owner", "m7-exp-4b-member", "m7-exp-4b-third");
        // NOTE: the second setup REPLACES the ExpeditionManager singleton;
        // the first rig's expedition lives in the replaced manager. Use the
        // CURRENT manager's registry through the actor path instead.
        var create = ownerB.ExpeditionCreate("BotGuildB");
        await Assert.That(create.State).IsEqualTo(ActorLifecycleState.Completed);
        var expB = ownerB.Character.Expedition!;

        var foreignInviter = third.ExpeditionAccept(expB.Id, 424242u);
        await Assert.That(foreignInviter.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(foreignInviter.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(foreignInviter.Detail?.Contains("is not a member")).IsTrue();
        await Assert.That(foreignInviter.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        await Assert.That(third.Character.Expedition).IsNull();
    }

    [Test]
    public async Task Expedition_SameKeyRetry_RejectedPreFlight_NoDuplicateExecution()
    {
        var (owner, _, third, _) = SetupThreeActorRig("m7-exp-5-owner", "m7-exp-5-member", "m7-exp-5-third");

        var completed = owner.ExpeditionCreate("BotGuild", idempotencyKey: "create:same");
        await Assert.That(completed.State).IsEqualTo(ActorLifecycleState.Completed);

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight by the ledger; the audit record shows no Running.
        var retry = owner.ExpeditionCreate("BotGuildAgain", idempotencyKey: "create:same");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Correlation: the key still resolves to the ORIGINAL outcome.
        var correlated = owner.FindByKey("create:same");
        await Assert.That(correlated).IsNotNull();
        await Assert.That(correlated!.TraceId).IsEqualTo(completed.TraceId);
        await Assert.That(correlated.Result).IsEqualTo(ActorLifecycleState.Completed);

        // The retry never touched the engine: still the original expedition.
        await Assert.That(owner.Character.Expedition!.Name).IsEqualTo("BotGuild");
    }
}
