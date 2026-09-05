using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Tasks.Mails;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// MAIL-04 unpaid tax lifecycle test suite (G5).
///
/// Covers the MailForTax → PayChargeMoney lifecycle end to end:
/// - MailForTax creation: Billing type, Unpaid status, BillingAmount charge,
///   Extra house/zone encoding, and the Send() path into the mail registry.
/// - PayChargeMoney gold path: deducts the charge, marks the mail Read,
///   extends the house protection, emits SCChargeMoneyPaidPacket +
///   SCMailDeletedPacket, and removes the mail.
/// - PayChargeMoney insufficient funds: refusal with
///   MailNotEnoughMoneyToPayTaxes, no deduction, mail untouched.
/// - PayChargeMoney duplicate payment: the mail is deleted on the first
///   success, so a second call fails the GetMailById null check (MailInvalid)
///   — no double charge. There is no explicit idempotency guard.
/// - PayChargeMoney certificate path (Feature.taxItem): consumes tax
///   certificates (bound first) instead of gold.
/// - Expiry: a Billing mail (SenderId 0) can never bounce and is not Charged,
///   so the sweep destroys it without refund.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public sealed class MailTaxLifecycleTests
{
    private const long TaxMailId = 400;
    private const uint HouseId = 7001;
    private const uint ZoneKey = 9;
    private const uint ZoneGroupId = 1;
    private const int WeeklyTax = 100_000;

    private CharacterMock _owner;
    private MailManager _mailManager;
    private NameManager _nameManager;
    private HousingManager _housingManager;
    private House _house;
    private PacketCaptureSession _capture;
    private GameConnection _connection;
    private TimeSpan _originalExpireDelay;
    private WorldConfig _previousWorldConfig;
    private bool _previousTaxItem;

    [Before(Test)]
    public void Setup()
    {
        // ItemManager singleton: the Inventory ctor resolves
        // ItemManager.Instance (persistent containers) and the certificate
        // path resolves templates + item ids.
        if (typeof(Singleton<ItemManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) is null)
        {
            typeof(Singleton<ItemManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, BuildFixtureItemManager());
        }
        var itemManager = (ItemManager)typeof(Singleton<ItemManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        if (GetPrivateField(itemManager, "_templates") is not Dictionary<uint, ItemTemplate> templates)
        {
            templates = new Dictionary<uint, ItemTemplate>();
            SetPrivateField(itemManager, "_templates", templates);
        }
        templates[Item.TaxCertificate] = new ItemTemplate { Id = Item.TaxCertificate, MaxCount = 100 };
        templates[Item.BoundTaxCertificate] = new ItemTemplate { Id = Item.BoundTaxCertificate, MaxCount = 100 };

        // QuestManager singleton: the certificate consume path fires
        // OnConsumedItem → QuestManager.Instance.DoItemsConsumedEvents.
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

        // FeaturesManager: PayChargeMoney branches on
        // FeaturesManager.Fsets.Check(Feature.taxItem). Default (gold path)
        // is the engine's own documented toggle (FeaturesManager.cs:40).
        if (typeof(Singleton<FeaturesManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) is null)
        {
            var features = new FeaturesManager(Mock.Of<IExperienceManager>().Object);
            features.Initialize();
            typeof(Singleton<FeaturesManager>)
                .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, features);
        }
        _previousTaxItem = FeaturesManager.Fsets.Check(Feature.taxItem);
        FeaturesManager.Fsets.Set(Feature.taxItem, false);

        // PayWeeklyTax reads AppConfiguration.Instance.World.DaysForTaxPayment.
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 7 };

        _owner = new CharacterMock { AccountId = 1, Id = 1, Name = "owner", Money = 1000, NumInventorySlots = 50 };
        _owner.Inventory = new Inventory(_owner);
        _owner.Mails = new CharacterMails(_owner);

        _nameManager = new NameManager();
        _nameManager.Load([], [], []);
        _nameManager.AddCharacter(_owner.Id, _owner.Name, 1);

        // ZoneManager: MailForTax.UpdateTaxInfo resolves the house zone.
        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        SetPrivateField(zoneManager, "_zones", new Dictionary<uint, Zone>
        {
            [ZoneKey] = new() { Id = 1, ZoneKey = ZoneKey, GroupId = ZoneGroupId, Name = "test-zone" }
        });
        SetPrivateField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>());
        SetPrivateField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());

        // Real HousingManager: CalculateBuildingTaxInfo (creation),
        // GetHouseById + PayWeeklyTax (payment).
        _housingManager = new HousingManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IMailManager>().Object,
            _nameManager,
            zoneManager,
            Mock.Of<IDoodadManager>().Object,
            Mock.Of<IUccManager>().Object);
        SetPrivateField(_housingManager, "_houses", new Dictionary<uint, House>());
        SetPrivateField(_housingManager, "_housesTl", new Dictionary<ushort, House>());

        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();

        _mailManager = new MailManager(
            mailIdManager,
            _nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => _housingManager),
            Mock.Of<ILocalizationManager>().Object);

        // Reset singleton caches so Instance properties resolve via ServiceProvider
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<ZoneManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<HousingManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var services = new ServiceCollection();
        services.AddSingleton(_mailManager);
        services.AddSingleton(_nameManager);
        services.AddSingleton(zoneManager);
        services.AddSingleton(_housingManager);
        SingletonContainer.ServiceProvider = services.BuildServiceProvider();

        // House creation must happen after the ZoneManager singleton is
        // resolvable: setting Transform.ZoneId fires Unit.OnZoneChange →
        // ZoneManager.Instance.
        _house = CreateHouse();
        ((Dictionary<uint, House>)GetPrivateField(_housingManager, "_houses"))[HouseId] = _house;

        _capture = new PacketCaptureSession();
        _connection = new GameConnection(_capture) { ActiveChar = _owner };
        _owner.Connection = _connection;

        _mailManager._allPlayerMails = [];

        _originalExpireDelay = MailManager.MailExpireDelay;
    }

    [After(Test)]
    public void Teardown()
    {
        MailManager.MailExpireDelay = _originalExpireDelay;
        AppConfiguration.Instance.World = _previousWorldConfig;
        FeaturesManager.Fsets.Set(Feature.taxItem, _previousTaxItem);

        _mailManager._allPlayerMails = null;
        _owner = null;
        _mailManager = null;
        _nameManager = null;
        _housingManager = null;
        _house = null;
        _capture = null;
        _connection = null;

        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<ZoneManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<HousingManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private List<byte[]> Frames => _capture.CapturedPackets;

    [Test]
    public async Task TaxMail_Creation_SetsBillingTypeUnpaidStatusAndChargeAmount()
    {
        var mail = new MailForTax(_house);

        var finalized = mail.FinalizeMail();

        await Assert.That(finalized).IsTrue();
        await Assert.That(mail.MailType).IsEqualTo(MailType.Billing);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unpaid);
        await Assert.That(mail.Body.BillingAmount).IsEqualTo(WeeklyTax);
        await Assert.That(mail.Header.SenderId).IsEqualTo(0u);
        await Assert.That(mail.Header.SenderName).IsEqualTo(".houseTax");
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(_owner.Id);
        await Assert.That(mail.ReceiverName).IsEqualTo(_nameManager.GetCharacterName(_owner.Id));
        await Assert.That(mail.Header.Extra & 0xFFFFFFFF).IsEqualTo((long)HouseId);
        await Assert.That((mail.Header.Extra >> 48) & 0xFFFF).IsEqualTo((long)ZoneGroupId);
        await Assert.That(mail.Title).IsEqualTo("title(" + ZoneGroupId + ")");

        var sent = mail.Send();

        await Assert.That(sent).IsTrue();
        await Assert.That(mail.Id).IsGreaterThan(0L);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsTrue();
    }

    [Test]
    public async Task PayChargeMoney_GoldPath_SufficientFunds_DeductsMoneyMarksPaidAndDeletesMail()
    {
        var mail = CreateTaxMail(WeeklyTax);
        _mailManager._allPlayerMails[TaxMailId] = mail;
        _owner.Money = 500_000L;
        _owner.Mails.UnreadMailCount.UpdateReceived(MailType.Billing, 1);
        var protectionBefore = _house.ProtectionEndDate;

        var result = _mailManager.PayChargeMoney(_owner, TaxMailId, autoUseAAPoint: false);

        await Assert.That(result).IsTrue();
        await Assert.That(_owner.Money).IsEqualTo(500_000L - WeeklyTax);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TaxMailId)).IsFalse();
        await Assert.That(_owner.Mails.UnreadMailCount.Received).IsEqualTo(0);
        await Assert.That(_house.ProtectionEndDate).IsEqualTo(protectionBefore.AddDays(7));
        await Assert.That(HasOpcode(Frames, SCOffsets.SCChargeMoneyPaidPacket)).IsTrue();
        await Assert.That(HasOpcode(Frames, SCOffsets.SCMailDeletedPacket)).IsTrue();
        await Assert.That(HasError(Frames, ErrorMessageType.MailNotEnoughMoneyToPayTaxes)).IsFalse();
    }

    [Test]
    public async Task PayChargeMoney_GoldPath_InsufficientFunds_RefusesWithoutChanges()
    {
        var mail = CreateTaxMail(WeeklyTax);
        _mailManager._allPlayerMails[TaxMailId] = mail;
        _owner.Money = WeeklyTax - 1L;

        var result = _mailManager.PayChargeMoney(_owner, TaxMailId, autoUseAAPoint: false);

        await Assert.That(result).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(WeeklyTax - 1L);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unpaid);
        await Assert.That(mail.Body.BillingAmount).IsEqualTo(WeeklyTax);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TaxMailId)).IsTrue();
        await Assert.That(HasError(Frames, ErrorMessageType.MailNotEnoughMoneyToPayTaxes)).IsTrue();
        await Assert.That(HasOpcode(Frames, SCOffsets.SCChargeMoneyPaidPacket)).IsFalse();
    }

    [Test]
    public async Task PayChargeMoney_DuplicatePayment_SecondCallRefused()
    {
        // GAP (documented, not fixed): PayChargeMoney has no explicit
        // idempotency guard. The first successful payment deletes the mail,
        // so a second call fails the GetMailById null check with MailInvalid
        // — no double charge, but the refusal reason is "invalid mail" rather
        // than a dedicated "already paid" path.
        var mail = CreateTaxMail(WeeklyTax);
        _mailManager._allPlayerMails[TaxMailId] = mail;
        _owner.Money = 500_000L;

        var first = _mailManager.PayChargeMoney(_owner, TaxMailId, autoUseAAPoint: false);
        var moneyAfterFirst = _owner.Money;
        var framesAfterFirst = Frames.Count;
        var second = _mailManager.PayChargeMoney(_owner, TaxMailId, autoUseAAPoint: false);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(moneyAfterFirst);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TaxMailId)).IsFalse();
        await Assert.That(HasError(Frames.Skip(framesAfterFirst).ToList(), ErrorMessageType.MailInvalid)).IsTrue();
    }

    [Test]
    public async Task PayChargeMoney_CertPath_ConsumesCertificatesBoundFirst()
    {
        FeaturesManager.Fsets.Set(Feature.taxItem, true);
        try
        {
            var mail = CreateTaxMail(40_000);
            _mailManager._allPlayerMails[TaxMailId] = mail;
            _owner.Money = 0; // gold must not be touched on the cert path
            _owner.Mails.UnreadMailCount.UpdateReceived(MailType.Billing, 1);

            // 2 bound + 3 normal certs → ceil(40000/10000) = 4 consumed, bound first
            StockCert(Item.BoundTaxCertificate, 2);
            StockCert(Item.TaxCertificate, 3);

            var result = _mailManager.PayChargeMoney(_owner, TaxMailId, autoUseAAPoint: false);

            await Assert.That(result).IsTrue();
            await Assert.That(_owner.Inventory.GetItemsCount(SlotType.Inventory, Item.BoundTaxCertificate)).IsEqualTo(0);
            await Assert.That(_owner.Inventory.GetItemsCount(SlotType.Inventory, Item.TaxCertificate)).IsEqualTo(1);
            await Assert.That(_owner.Money).IsEqualTo(0);
            await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
            await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TaxMailId)).IsFalse();
            await Assert.That(_owner.Mails.UnreadMailCount.Received).IsEqualTo(0);
            await Assert.That(HasOpcode(Frames, SCOffsets.SCChargeMoneyPaidPacket)).IsTrue();
            // Quirk: the cert branch overwrites BillingAmount with the
            // remaining (0) cert count after a successful consume.
            await Assert.That(mail.Body.BillingAmount).IsEqualTo(0);
        }
        finally
        {
            FeaturesManager.Fsets.Set(Feature.taxItem, false);
        }
    }

    [Test]
    public async Task Expiry_TaxMail_DestroyedWithoutRefund()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        var oldRecvDate = DateTime.UtcNow - TimeSpan.FromDays(15);

        var mail = CreateTaxMail(WeeklyTax, oldRecvDate);
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[TaxMailId] = mail;

        new MailDeliveryTask().Execute();

        // Billing mail (SenderId 0) can never bounce and is not Charged, so
        // the sweep destroys it without a refund mail.
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TaxMailId)).IsFalse();
        await Assert.That(_mailManager._allPlayerMails.Values.Any(m => m.Body.CopperCoins > 0)).IsFalse();
    }

    private House CreateHouse()
    {
        var house = new House
        {
            Id = HouseId,
            ObjId = 0xC000 + HouseId,
            TlId = (ushort)HouseId,
            Template = new HousingTemplate
            {
                Id = 172,
                Name = "test-house",
                Taxation = new Taxation { Tax = (uint)WeeklyTax },
                HeavyTax = false
            },
            TemplateId = 172,
            OwnerId = _owner.Id,
            CoOwnerId = _owner.Id,
            AccountId = _owner.AccountId,
            Name = "test-house",
            Permission = HousingPermission.Private,
            AllowRecover = true,
            PlaceDate = DateTime.UtcNow,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
        house.Transform = new Transform(house, null, ZoneKey, 0, 0f, 0f, 0f, 0f);
        return house;
    }

    private BaseMail CreateTaxMail(int billingAmount, DateTime? recvDate = null)
    {
        var mail = new BaseMail
        {
            Id = TaxMailId,
            Title = "title(" + ZoneGroupId + ")",
            ReceiverName = _nameManager.GetCharacterName(_owner.Id),
            MailType = MailType.Billing,
            Header =
            {
                Status = MailStatus.Unpaid,
                SenderId = 0,
                SenderName = ".houseTax",
                ReceiverId = _owner.Id,
                Extra = ((long)ZoneGroupId << 48) + HouseId
            },
            Body =
            {
                Text = "body",
                BillingAmount = billingAmount,
                SendDate = DateTime.UtcNow,
                RecvDate = recvDate ?? DateTime.UtcNow
            }
        };
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        return mail;
    }

    private void StockCert(uint templateId, int count)
        => _owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.QuestSupplyItems, templateId, count);

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
        SetPrivateField(itemManager, "_templates", new Dictionary<uint, ItemTemplate>());
        return itemManager;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        return field.GetValue(target);
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
