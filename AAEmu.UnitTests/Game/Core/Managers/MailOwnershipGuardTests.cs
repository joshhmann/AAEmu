using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.UnitTests.Utils.Mocks;
using System.Collections.Concurrent;
using AAEmu.Game.Models.Game.Items.Containers;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// MAIL-01 ownership-hardening rig: every receive-path C2S mail packet that used to
/// trust the client-supplied mailId (read / take-item / take-money / delete) must
/// refuse cross-character access with <see cref="ErrorMessageType.MailInvalid"/>,
/// mirroring the CSTakeAttachmentSequentially precedent, while owner flows (including
/// sent-tab reads) keep working. Packets are delivered through the REAL decode seam
/// (packet.Connection assignment + Read(PacketStream), the MerchantRigTests.Deliver
/// convention) against capture-backed GameConnections, so assertions observe exactly
/// what the engine emits on the wire.
/// </summary>
[NotInParallel]
public sealed class MailOwnershipGuardTests
{
    private const long ReceiverMailId = 100;   // bob -> tester (tester owns the receive entry)
    private const long SentMailId = 101;       // tester -> mallory (tester owns the sent entry)
    private const long ItemMailId = 102;       // bob -> tester, one item attachment
    private const long MoneyMailId = 103;      // bob -> tester, copper only
    private const ulong AttachedItemId = 5001;
    private const uint AttachedItemTemplateId = 8100;
    private const int AttachedMoney = 777;

    private CharacterMock _tester;      // Id 1: legitimate party in every fixture
    private CharacterMock _mallory;     // Id 2: the cross-character attacker / return-roundtrip receiver
    private MailManager _mailManager;
    private GameConnection _testerConn;
    private GameConnection _malloryConn;
    private PacketCaptureSession _testerCapture;
    private PacketCaptureSession _malloryCapture;

    [Before(Test)]
    public void Setup()
    {
        // new Inventory() resolves containers through ItemManager.Instance and
        // item acquisition touches QuestManager — seed the same fixture-manager
        // convention ItemProcBindingTests uses (only when no live instance).
        if (typeof(Singleton<ItemManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) is null)
        {
            typeof(Singleton<ItemManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, BuildFixtureItemManager());
        }

        if (typeof(Singleton<QuestManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) is null)
        {
            var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
            SetPrivateField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
            SetPrivateField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
            SetPrivateField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
            typeof(Singleton<QuestManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, questManager);
        }


        _tester = new CharacterMock { AccountId = 1, Id = 1, Name = "tester", Money = 1000, NumInventorySlots = 50 };
        _tester.Inventory = new Inventory(_tester);
        _tester.Mails = new CharacterMails(_tester);
        _mallory = new CharacterMock { AccountId = 2, Id = 2, Name = "mallory", Money = 500, NumInventorySlots = 50 };
        _mallory.Inventory = new Inventory(_mallory);
        _mallory.Mails = new CharacterMails(_mallory);

        var nameManager = new NameManager();
        nameManager.Load([], [], []);
        nameManager.AddCharacter(_tester.Id, _tester.Name, 1);
        nameManager.AddCharacter(_mallory.Id, _mallory.Name, 1);

        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();

        _mailManager = new MailManager(
            mailIdManager,
            nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);

        // Reset singleton caches so Instance properties resolve via ServiceProvider
        // (the MailTests/MailReturnTests convention).
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var services = new ServiceCollection();
        services.AddSingleton(_mailManager);
        services.AddSingleton(nameManager);
        SingletonContainer.ServiceProvider = services.BuildServiceProvider();
        _testerCapture = new PacketCaptureSession();
        _malloryCapture = new PacketCaptureSession();
        _testerConn = new GameConnection(_testerCapture) { ActiveChar = _tester };
        _tester.Connection = _testerConn;
        _malloryConn = new GameConnection(_malloryCapture) { ActiveChar = _mallory };
        _mallory.Connection = _malloryConn;

        _mailManager._allPlayerMails = [];
    }

    [After(Test)]
    public void Teardown()
    {
        _testerCapture = null;
        _malloryCapture = null;
        _mailManager._allPlayerMails = null;
        _tester = null;
        _mallory = null;
        _mailManager = null;
        _testerConn = null;
        _malloryConn = null;


        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<ItemManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    // ---- fixtures ----------------------------------------------------------


    /// <summary>Delivers a client payload through the real decode seam (MerchantRigTests.Deliver).</summary>
    private static void Deliver(GamePacket packet, GameConnection connection, PacketStream payload)
    {
        // PacketBase<T>.Connection has a public setter (protected getter).
        packet.Connection = connection;
        packet.Read(payload);
    }

    private List<byte[]> TesterFrames => _testerCapture.CapturedPackets;
    private List<byte[]> MalloryFrames => _malloryCapture.CapturedPackets;

    private static PacketStream Payload(Action<PacketStream> write)
    {
        var ps = new PacketStream();
        write(ps);
        return ps;
    }

    private BaseMail SeedReceivedMail(long id, MailStatus status, int copperCoins = 0, Item attachment = null)
    {
        var mail = new BaseMail
        {
            Id = id,
            Title = "test",
            ReceiverName = _tester.Name,
            MailType = MailType.Normal,
            Header =
            {
                Status = status,
                SenderId = 3,
                SenderName = "bob",
                ReceiverId = _tester.Id
            },
            Body =
            {
                Text = "body",
                CopperCoins = copperCoins,
                SendDate = DateTime.UtcNow,
                RecvDate = DateTime.UtcNow
            }
        };
        if (attachment != null)
            mail.Body.Attachments.Add(attachment);
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        _mailManager._allPlayerMails[id] = mail;
        return mail;
    }

    private BaseMail SeedSentMail(long id, MailStatus status)
    {
        var mail = new BaseMail
        {
            Id = id,
            Title = "sent",
            ReceiverName = _mallory.Name,
            MailType = MailType.Normal,
            Header =
            {
                Status = status,
                SenderId = _tester.Id,
                SenderName = _tester.Name,
                ReceiverId = _mallory.Id
            },
            Body =
            {
                Text = "sent body",
                SendDate = DateTime.UtcNow,
                RecvDate = DateTime.UtcNow
            }
        };
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        _mailManager._allPlayerMails[id] = mail;
        return mail;
    }

    private static Item MakeAttachedItem()
    {
        var template = new ItemTemplate
        {
            Id = AttachedItemTemplateId,
            BindType = ItemBindType.Normal,
            MaxCount = 1000
        };
        return new Item(AttachedItemId, template, 5);
    }

    private static PacketStream TakeItemPayload(long mailId, Item item)
    {
        // CSReadMailPacket decode order: mailId i64, itemId u32, id u64, grade u8,
        // flags u8, count u32, detailType u8, creationTime, lifespanMins u32, type2 u32,
        // worldId u8, unsecureDateTime, unpackDateTime, slotType u8, slot u8.
        return Payload(ps =>
        {
            ps.Write(mailId);
            ps.Write((uint)item.TemplateId);
            ps.Write(item.Id);
            ps.Write(item.Grade);
            ps.Write((byte)item.ItemFlags);
            ps.Write(item.Count);
            ps.Write((byte)0);
            ps.Write(DateTime.UtcNow);
            ps.Write(item.LifespanMins);
            ps.Write((uint)item.TemplateId);
            ps.Write((byte)0);
            ps.Write(DateTime.UtcNow);
            ps.Write(DateTime.UtcNow);
            ps.Write((byte)item.SlotType);
            ps.Write((byte)item.Slot);
        });
    }

    // ---- wire-frame helpers (SpecialtyManagerTests frame convention) --------

    private static ushort OpcodeOf(byte[] frame)
    {
        var level = frame[3];
        var opcodeOffset = level == 1 ? 6 : 4;
        return BitConverter.ToUInt16(frame, opcodeOffset);
    }

    private static bool HasError(List<byte[]> frames, ErrorMessageType type)
    {
        foreach (var frame in frames)
        {
            var level = frame[3];
            var opcodeOffset = level == 1 ? 6 : 4;
            if (BitConverter.ToUInt16(frame, opcodeOffset) != SCOffsets.SCErrorMsgPacket)
                continue;
            if (BitConverter.ToInt16(frame, opcodeOffset + 2) == (short)type)
                return true;
        }
        return false;
    }

    private static bool HasOpcode(List<byte[]> frames, ushort opcode) =>
        frames.Any(f => OpcodeOf(f) == opcode);


    // ---- 1. read ------------------------------------------------------------

    [Test]
    public async Task ReadMail_NonReceiver_Refused()
    {
        var mail = SeedReceivedMail(ReceiverMailId, MailStatus.Unread);

        Deliver(new CSReadMailPacket(), _malloryConn,
            Payload(ps => { ps.Write(false); ps.Write(ReceiverMailId); }));
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(mail.OpenDate).IsEqualTo(default);
        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailInvalid)).IsTrue();
        await Assert.That(HasOpcode(MalloryFrames, SCOffsets.SCMailBodyPacket)).IsFalse();
    }

    [Test]
    public async Task ReadMail_Owner_ReceiveTab_BodyStillSent()
    {
        var mail = SeedReceivedMail(ReceiverMailId, MailStatus.Unread);

        Deliver(new CSReadMailPacket(), _testerConn,
            Payload(ps => { ps.Write(false); ps.Write(ReceiverMailId); }));

        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(HasError(TesterFrames, ErrorMessageType.MailInvalid)).IsFalse();
        await Assert.That(HasOpcode(TesterFrames, SCOffsets.SCMailBodyPacket)).IsTrue();
    }

    [Test]
    public async Task ReadMail_SentTab_Owner_CanStillReadOwnSentMail()
    {
        // Legitimate sender-side access must survive the guard (sent-tab view).
        var mail = SeedSentMail(SentMailId, MailStatus.Read);

        Deliver(new CSReadMailPacket(), _testerConn,
            Payload(ps => { ps.Write(true); ps.Write(SentMailId); }));

        await Assert.That(HasError(TesterFrames, ErrorMessageType.MailInvalid)).IsFalse();
        await Assert.That(HasOpcode(TesterFrames, SCOffsets.SCMailBodyPacket)).IsTrue();
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
    }

    [Test]
    public async Task ReadMail_SentTab_NonSender_Refused()
    {
        SeedSentMail(SentMailId, MailStatus.Read);

        Deliver(new CSReadMailPacket(), _malloryConn,
            Payload(ps => { ps.Write(true); ps.Write(SentMailId); }));

        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailInvalid)).IsTrue();
        await Assert.That(HasOpcode(MalloryFrames, SCOffsets.SCMailBodyPacket)).IsFalse();
    }

    // ---- 2. take item ---------------------------------------------------------

    [Test]
    public async Task TakeAttachmentItem_NonOwner_Refused_AttachmentStays()
    {
        var item = MakeAttachedItem();
        var mail = SeedReceivedMail(ItemMailId, MailStatus.Unread, attachment: item);

        Deliver(new CSTakeAttachmentItemPacket(), _malloryConn, TakeItemPayload(ItemMailId, item));

        await Assert.That(mail.Body.Attachments.Contains(item)).IsTrue();
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)1);
        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailInvalid)).IsTrue();
        await Assert.That(HasOpcode(MalloryFrames, SCOffsets.SCAttachmentTakenPacket)).IsFalse();
    }

    [Test]
    public async Task TakeAttachmentItem_Owner_ItemMovesToBag()
    {
        var item = MakeAttachedItem();
        var mail = SeedReceivedMail(ItemMailId, MailStatus.Unread, attachment: item);

        Deliver(new CSTakeAttachmentItemPacket(), _testerConn, TakeItemPayload(ItemMailId, item));

        await Assert.That(mail.Body.Attachments.Contains(item)).IsFalse();
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)0);
        await Assert.That(_tester.Inventory.Bag.Items.Any(i => i?.Id == AttachedItemId)).IsTrue();
        await Assert.That(HasOpcode(TesterFrames, SCOffsets.SCAttachmentTakenPacket)).IsTrue();
    }

    // ---- 3. take money --------------------------------------------------------

    [Test]
    public async Task TakeAttachmentMoney_NonOwner_Refused_MoneyStays()
    {
        var mail = SeedReceivedMail(MoneyMailId, MailStatus.Unread, copperCoins: AttachedMoney);
        var attackerMoneyBefore = _mallory.Money;

        Deliver(new CSTakeAttachmentMoneyPacket(), _malloryConn,
            Payload(ps => ps.Write(MoneyMailId)));

        await Assert.That(mail.Body.CopperCoins).IsEqualTo(AttachedMoney);
        await Assert.That(_mallory.Money).IsEqualTo(attackerMoneyBefore);
        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailInvalid)).IsTrue();
    }

    [Test]
    public async Task TakeAttachmentMoney_Owner_MoneyCredited()
    {
        var mail = SeedReceivedMail(MoneyMailId, MailStatus.Unread, copperCoins: AttachedMoney);
        var ownerMoneyBefore = _tester.Money;

        Deliver(new CSTakeAttachmentMoneyPacket(), _testerConn,
            Payload(ps => ps.Write(MoneyMailId)));

        await Assert.That(mail.Body.CopperCoins).IsEqualTo(0);
        await Assert.That(_tester.Money).IsEqualTo(ownerMoneyBefore + AttachedMoney);
        await Assert.That(HasError(TesterFrames, ErrorMessageType.MailInvalid)).IsFalse();
    }

    // ---- 4. delete ------------------------------------------------------------

    [Test]
    public async Task DeleteMail_NonOwner_Refused_MailStays()
    {
        var mail = SeedReceivedMail(ReceiverMailId, MailStatus.Read);

        Deliver(new CSDeleteMailPacket(), _malloryConn,
            Payload(ps => { ps.Write(ReceiverMailId); ps.Write(false); }));

        await Assert.That(_mailManager._allPlayerMails.ContainsKey(ReceiverMailId)).IsTrue();
        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailInvalid)).IsTrue();
    }

    [Test]
    public async Task DeleteMail_Owner_MailRemoved()
    {
        SeedReceivedMail(ReceiverMailId, MailStatus.Read);

        Deliver(new CSDeleteMailPacket(), _testerConn,
            Payload(ps => { ps.Write(ReceiverMailId); ps.Write(false); }));

        await Assert.That(_mailManager._allPlayerMails.ContainsKey(ReceiverMailId)).IsFalse();
        await Assert.That(HasError(TesterFrames, ErrorMessageType.MailInvalid)).IsFalse();
    }

    // ---- 5. return through the registered packet path --------------------------

    [Test]
    public async Task ReturnMail_NonReceiver_AtPacketLevel_Refused()
    {
        var mail = SeedReceivedMail(ReceiverMailId, MailStatus.Read, copperCoins: AttachedMoney);

        Deliver(new CSReturnMailPacket(), _malloryConn,
            Payload(ps => ps.Write(ReceiverMailId)));

        // Mail untouched: ownership, status and attachments all intact
        await Assert.That(_mailManager._allPlayerMails.ContainsKey(ReceiverMailId)).IsTrue();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(_tester.Id);
        await Assert.That(mail.Header.Returned).IsFalse();
        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailNotAllowedToReturn)).IsTrue();
    }

    [Test]
    public async Task ReturnRoundtrip_ReadMailReturnedThroughRegisteredPacket_BouncesToIntact()
    {
        // tester sent mail to mallory; mallory read it and now returns it via the
        // wired CSReturnMailPacket (opcode STRONGLY_INFERRED as 0x0a2).
        var item = MakeAttachedItem();
        var mail = new BaseMail
        {
            Id = 200,
            Title = "roundtrip",
            ReceiverName = _mallory.Name,
            MailType = MailType.Normal,
            Header =
            {
                Status = MailStatus.Read,
                SenderId = _tester.Id,
                SenderName = _tester.Name,
                ReceiverId = _mallory.Id
            },
            Body =
            {
                Text = "return me",
                CopperCoins = 55,
                SendDate = DateTime.UtcNow,
                RecvDate = DateTime.UtcNow
            }
        };
        mail.Body.Attachments.Add(item);
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        _mailManager._allPlayerMails[200] = mail;

        Deliver(new CSReturnMailPacket(), _malloryConn,
            Payload(ps => ps.Write(200L)));

        // The mail bounced back to the original sender with everything intact
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(_tester.Id);
        await Assert.That(mail.Header.SenderId).IsEqualTo(_mallory.Id);
        await Assert.That(mail.Header.Returned).IsTrue();
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(55);
        await Assert.That(mail.Body.Attachments.Contains(item)).IsTrue();
        await Assert.That(item.OwnerId).IsEqualTo(_tester.Id);
        await Assert.That(_mailManager._allPlayerMails.ContainsKey(200)).IsTrue();


        // Wire semantics: the returning receiver is told the mail went back
        await Assert.That(HasOpcode(MalloryFrames, SCOffsets.SCMailReturnedPacket)).IsTrue();
        await Assert.That(HasError(MalloryFrames, ErrorMessageType.MailNotAllowedToReturn)).IsFalse();
    }

    // ---- fixture ItemManager (ItemProcBindingTests convention) -------------

    private static ItemManager BuildFixtureItemManager()
    {
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            new CountingItemIdManager(),
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        SetPrivateField(itemManager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
        SetPrivateField(itemManager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        SetPrivateField(itemManager, "_removedItems", new List<ulong>());
        return itemManager;
    }

    private static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Field {fieldName} not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    /// <summary>Hand-rolled IItemIdManager — increments from 1.</summary>
    private sealed class CountingItemIdManager : IItemIdManager
    {
        private uint _next = 1;
        public bool Initialize(bool forceReset = false) => true;
        public uint GetNextId() => _next++;
        public uint[] GetNextId(int count)
        {
            var result = new uint[count];
            for (var i = 0; i < count; i++)
                result[i] = GetNextId();
            return result;
        }
        public void ReleaseId(uint usedObjectId) { }
        public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
        public void Load() { }
    }
}
