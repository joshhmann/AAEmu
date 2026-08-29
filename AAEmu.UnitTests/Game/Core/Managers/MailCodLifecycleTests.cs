using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// MAIL-02 COD (Cash On Delivery) lifecycle and attachment boundary test suite:
/// - Enforces COD payment deduction on receiver and payment dispatch to sender on looting items.
/// - Validates insufficient coins refusal on COD looting.
/// - Validates partial attachment transfer when receiver inventory capacity is exceeded.
/// - Validates sent-tab mail deletion ownership and wire emission.
/// </summary>
[NotInParallel]
public sealed class MailCodLifecycleTests
{
    private const long CodMailId = 200;
    private const long MultiItemMailId = 201;
    private const ulong AttachedItemId1 = 6001;
    private const ulong AttachedItemId2 = 6002;
    private const uint AttachedItemTemplateId1 = 8201;
    private const uint AttachedItemTemplateId2 = 8202;

    private CharacterMock _sender;
    private CharacterMock _receiver;
    private MailManager _mailManager;
    private GameConnection _senderConn;
    private GameConnection _receiverConn;
    private PacketCaptureSession _senderCapture;
    private PacketCaptureSession _receiverCapture;

    [Before(Test)]
    public void Setup()
    {
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

        _sender = new CharacterMock { AccountId = 1, Id = 1, Name = "sender", Money = 1000, NumInventorySlots = 50 };
        _sender.Inventory = new Inventory(_sender);
        _sender.Mails = new CharacterMails(_sender);

        _receiver = new CharacterMock { AccountId = 2, Id = 2, Name = "receiver", Money = 1000, NumInventorySlots = 50 };
        _receiver.Inventory = new Inventory(_receiver);
        _receiver.Mails = new CharacterMails(_receiver);

        var nameManager = new NameManager();
        nameManager.Load([], [], []);
        nameManager.AddCharacter(_sender.Id, _sender.Name, 1);
        nameManager.AddCharacter(_receiver.Id, _receiver.Name, 1);

        var mailIdManager = new CountingMailIdManager();

        _mailManager = new MailManager(
            mailIdManager,
            nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);

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

        _senderCapture = new PacketCaptureSession();
        _receiverCapture = new PacketCaptureSession();
        _senderConn = new GameConnection(_senderCapture) { ActiveChar = _sender };
        _sender.Connection = _senderConn;
        _receiverConn = new GameConnection(_receiverCapture) { ActiveChar = _receiver };
        _receiver.Connection = _receiverConn;

        _mailManager._allPlayerMails = [];
    }

    [After(Test)]
    public void Teardown()
    {
        _senderCapture = null;
        _receiverCapture = null;
        _mailManager._allPlayerMails = null;
        _sender = null;
        _receiver = null;
        _mailManager = null;
        _senderConn = null;
        _receiverConn = null;

        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private List<byte[]> ReceiverFrames => _receiverCapture.CapturedPackets;
    private List<byte[]> SenderFrames => _senderCapture.CapturedPackets;

    [Test]
    public async Task GetAttached_CodMail_InsufficientMoney_RefusesAndRetainsItems()
    {
        var codMail = CreateCodMail(CodMailId, _sender.Id, _sender.Name, _receiver.Id, _receiver.Name,
            chargeAmount: 500, AttachedItemId1, AttachedItemTemplateId1);
        _mailManager._allPlayerMails[CodMailId] = codMail;

        _receiver.Money = 100;

        var success = _receiver.Mails.GetAttached(CodMailId, takeMoney: true, takeItems: true, takeAllSelected: true);

        await Assert.That(success).IsFalse();
        await Assert.That(_receiver.Money).IsEqualTo(100);
        await Assert.That(codMail.Body.Attachments).HasCount().EqualTo(1);
        await Assert.That(codMail.Header.Extra).IsEqualTo(500);
        await Assert.That(HasError(ReceiverFrames, ErrorMessageType.MailNotEnoughMoney)).IsTrue();
    }

    [Test]
    public async Task GetAttached_CodMail_SufficientMoney_DeductsMoneyLootsItemAndSendsPaymentMail()
    {
        var codMail = CreateCodMail(CodMailId, _sender.Id, _sender.Name, _receiver.Id, _receiver.Name,
            chargeAmount: 400, AttachedItemId1, AttachedItemTemplateId1);
        _mailManager._allPlayerMails[CodMailId] = codMail;

        _receiver.Money = 1000;

        var success = _receiver.Mails.GetAttached(CodMailId, takeMoney: true, takeItems: true, takeAllSelected: true);

        await Assert.That(success).IsTrue();
        await Assert.That(_receiver.Money).IsEqualTo(600);
        await Assert.That(codMail.Body.Attachments).HasCount().EqualTo(0);
        await Assert.That(codMail.Header.Extra).IsEqualTo(0);

        var bagItem = _receiver.Inventory.Bag.GetItemBySlot(0);
        await Assert.That(bagItem).IsNotNull();
        await Assert.That(bagItem!.Id).IsEqualTo(AttachedItemId1);

        var paymentMail = _mailManager._allPlayerMails.Values
            .FirstOrDefault(m => m.Header.ReceiverId == _sender.Id && m.Body.CopperCoins == 400);
        await Assert.That(paymentMail).IsNotNull();
        await Assert.That(paymentMail!.Title).Contains("COD");
    }

    [Test]
    public async Task GetAttached_PartialBagFull_TransfersHeadAndRetainsRemainder()
    {
        var item1 = CreateItem(AttachedItemId1, AttachedItemTemplateId1, _receiver.Id, 1);
        var item2 = CreateItem(AttachedItemId2, AttachedItemTemplateId2, _receiver.Id, 1);

        var mail = new BaseMail
        {
            Id = MultiItemMailId,
            Title = "multi-item",
            ReceiverName = _receiver.Name,
            MailType = MailType.Normal,
            Header =
            {
                Status = MailStatus.Read,
                SenderId = _sender.Id,
                SenderName = _sender.Name,
                ReceiverId = _receiver.Id,
                Attachments = 2
            },
            Body =
            {
                Text = "body",
                Attachments = [item1, item2],
                RecvDate = DateTime.UtcNow
            }
        };
        _mailManager._allPlayerMails[MultiItemMailId] = mail;

        _receiver.NumInventorySlots = 1;
        _receiver.Inventory = new Inventory(_receiver);
        _receiver.Mails = new CharacterMails(_receiver);

        var success = _receiver.Mails.GetAttached(MultiItemMailId, takeMoney: true, takeItems: true, takeAllSelected: true);

        await Assert.That(success).IsFalse();
        await Assert.That(mail.Body.Attachments).HasCount().EqualTo(1);
        await Assert.That(mail.Body.Attachments[0].Id).IsEqualTo(AttachedItemId2);
        await Assert.That(_receiver.Inventory.Bag.GetItemBySlot(0)).IsNotNull();
        await Assert.That(_receiver.Inventory.Bag.GetItemBySlot(0)!.Id).IsEqualTo(AttachedItemId1);
        await Assert.That(HasError(ReceiverFrames, ErrorMessageType.BagFull)).IsTrue();
    }

    [Test]
    public async Task GetAttached_MultiItem_EmitsOneAttachmentTakenPacketPerItem()
    {
        var item1 = CreateItem(AttachedItemId1, AttachedItemTemplateId1, _receiver.Id, 1);
        var item2 = CreateItem(AttachedItemId2, AttachedItemTemplateId2, _receiver.Id, 1);

        var mail = new BaseMail
        {
            Id = MultiItemMailId,
            Title = "multi-item",
            ReceiverName = _receiver.Name,
            MailType = MailType.Normal,
            Header =
            {
                Status = MailStatus.Read,
                SenderId = _sender.Id,
                SenderName = _sender.Name,
                ReceiverId = _receiver.Id,
                Attachments = 2
            },
            Body =
            {
                Text = "body",
                Attachments = [item1, item2],
                RecvDate = DateTime.UtcNow
            }
        };
        _mailManager._allPlayerMails[MultiItemMailId] = mail;

        var success = _receiver.Mails.GetAttached(MultiItemMailId, takeMoney: true, takeItems: true, takeAllSelected: true);

        await Assert.That(success).IsTrue();
        await Assert.That(mail.Body.Attachments).HasCount().EqualTo(0);
        // ZeromusXYZ split: one SCAttachmentTakenPacket per delivered item —
        // a single batched packet silently caps at 10 items on the wire and
        // breaks full-bag/manual-grab delivery.
        await Assert.That(CountOpcode(ReceiverFrames, SCOffsets.SCAttachmentTakenPacket)).IsEqualTo(2);
    }

    [Test]
    public async Task DeleteMail_SentTab_SenderOwns_EmitsDeletedPacket()
    {
        var sentMail = new BaseMail
        {
            Id = 300,
            Title = "sent-mail",
            ReceiverName = _receiver.Name,
            MailType = MailType.Normal,
            Header =
            {
                Status = MailStatus.Read,
                SenderId = _sender.Id,
                SenderName = _sender.Name,
                ReceiverId = _receiver.Id,
                Attachments = 0
            },
            Body =
            {
                Text = "body",
                RecvDate = DateTime.UtcNow
            }
        };
        _mailManager._allPlayerMails[300] = sentMail;

        _sender.Mails.DeleteMail(300, isSent: true);

        await Assert.That(HasOpcode(SenderFrames, SCOffsets.SCMailDeletedPacket)).IsTrue();
        await Assert.That(HasError(SenderFrames, ErrorMessageType.MailInvalid)).IsFalse();
        await Assert.That(_mailManager._allPlayerMails.ContainsKey(300)).IsFalse();
    }

    private static BaseMail CreateCodMail(long id, uint senderId, string senderName, uint receiverId, string receiverName,
        long chargeAmount, ulong itemId, uint itemTemplateId)
    {
        var item = CreateItem(itemId, itemTemplateId, receiverId, 1);
        return new BaseMail
        {
            Id = id,
            Title = "cod-sale",
            ReceiverName = receiverName,
            MailType = MailType.Charged,
            Header =
            {
                Status = MailStatus.Read,
                SenderId = senderId,
                SenderName = senderName,
                ReceiverId = receiverId,
                Attachments = 1,
                Extra = chargeAmount
            },
            Body =
            {
                Text = "cod body",
                Attachments = [item],
                RecvDate = DateTime.UtcNow
            }
        };
    }

    private static Item CreateItem(ulong objId, uint templateId, uint ownerId, int count)
    {
        var template = new ItemTemplate
        {
            Id = templateId,
            BindType = ItemBindType.Normal,
            MaxCount = 100
        };
        return new Item(objId, template, count)
        {
            SlotType = SlotType.Mail,
            Slot = 0,
            OwnerId = ownerId
        };
    }

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

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static bool HasOpcode(List<byte[]> frames, ushort expectedOpcode)
    {
        foreach (var frame in frames)
        {
            var level = frame[3];
            var opcodeOffset = level == 1 ? 6 : 4;
            if (BitConverter.ToUInt16(frame, opcodeOffset) == expectedOpcode)
                return true;
        }
        return false;
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

    private static int CountOpcode(List<byte[]> frames, ushort expectedOpcode)
    {
        var count = 0;
        foreach (var frame in frames)
        {
            var level = frame[3];
            var opcodeOffset = level == 1 ? 6 : 4;
            if (BitConverter.ToUInt16(frame, opcodeOffset) == expectedOpcode)
                count++;
        }
        return count;
    }

    private sealed class CountingMailIdManager : IMailIdManager
    {
        private uint _next = 500;
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
