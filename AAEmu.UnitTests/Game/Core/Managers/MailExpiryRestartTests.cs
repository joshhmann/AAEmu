using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Tasks.Mails;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// MAIL-03 restart-spanning expiry test suite (G4).
///
/// The expiry sweep (MailDeliveryTask → CheckAllMailTimings →
/// ProcessExpiredMail) is a pure function of the fields the Load path
/// restores from the mails table: received_date → Body.RecvDate, returned →
/// Header.Returned, extra → Header.Extra, sender/receiver ids+names, and
/// the money/attachment columns. A server restart therefore re-runs the
/// sweep over the SAME persisted state. These tests simulate the restart
/// with two MailManager instances: instance 1 runs the REAL task path on a
/// seeded delivered mail; instance 2 re-seeds the mail from the load-path
/// field values (as Load would restore it) and runs the sweep again —
/// asserting the persistence contract (the sweep consumes only those
/// fields) without a real MySQL. The true E2E restart boundary (process
/// restart + MySQL round-trip) is covered by MailS3RestartE2eTests for the
/// send/take path; expiry timing itself is in-memory.
/// </summary>
[NotInParallel]
public sealed class MailExpiryRestartTests
{
    private const long TestMailId = 100;

    private CharacterMock _sender;
    private CharacterMock _receiver;
    private MailManager _mailManager;
    private NameManager _nameManager;
    private TimeSpan _originalExpireDelay;

    [Before(Test)]
    public void Setup()
    {
        _sender = new CharacterMock { AccountId = 1, Id = 1, Name = "sender" };
        _receiver = new CharacterMock { AccountId = 2, Id = 2, Name = "receiver" };

        _nameManager = new NameManager();
        _nameManager.Load([], [], []);
        _nameManager.AddCharacter(_sender.Id, _sender.Name, 1);
        _nameManager.AddCharacter(_receiver.Id, _receiver.Name, 1);

        _mailManager = CreateMailManager(_nameManager);

        // Reset singleton caches so Instance properties resolve via ServiceProvider
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var services = new ServiceCollection();
        services.AddSingleton(_mailManager);
        services.AddSingleton(_nameManager);
        SingletonContainer.ServiceProvider = services.BuildServiceProvider();

        _mailManager._allPlayerMails = [];

        _originalExpireDelay = MailManager.MailExpireDelay;
    }

    [After(Test)]
    public void Teardown()
    {
        MailManager.MailExpireDelay = _originalExpireDelay;

        _mailManager._allPlayerMails = null;
        _sender = null;
        _receiver = null;
        _mailManager = null;
        _nameManager = null;

        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static MailManager CreateMailManager(NameManager nameManager)
    {
        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();

        return new MailManager(
            mailIdManager,
            nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
    }

    private static BaseMail CreateMail(long id, uint senderId, string senderName, uint receiverId, string receiverName,
        MailType type, MailStatus status, int copperCoins = 0, long extra = 0, DateTime? recvDate = null)
    {
        var mail = new BaseMail
        {
            Id = id,
            Title = "test",
            ReceiverName = receiverName,
            MailType = type,
            Header =
            {
                Status = status,
                SenderId = senderId,
                SenderName = senderName,
                ReceiverId = receiverId,
                Extra = extra
            },
            Body =
            {
                Text = "body",
                CopperCoins = copperCoins,
                SendDate = DateTime.UtcNow,
                RecvDate = recvDate ?? DateTime.UtcNow
            }
        };
        mail.Header.Attachments = mail.GetTotalAttachmentCount();
        return mail;
    }

    /// <summary>
    /// Re-seeds a mail exactly as the Load path restores it from the mails
    /// table: the persisted columns (received_date, returned, extra, roles,
    /// money) mapped back onto a fresh BaseMail, with IsDelivered derived
    /// from RecvDate (Load: tempMail.IsDelivered = RecvDate <= now).
    /// </summary>
    private static BaseMail ReseedFromLoadPath(long id, uint senderId, string senderName,
        uint receiverId, string receiverName, MailType type, MailStatus status,
        bool returned, int copperCoins, long extra, DateTime recvDate)
    {
        var mail = CreateMail(id, senderId, senderName, receiverId, receiverName,
            type, status, copperCoins, extra, recvDate);
        mail.Header.Returned = returned;
        mail.IsDelivered = mail.Body.RecvDate <= DateTime.UtcNow;
        mail.IsDirty = false;
        return mail;
    }

    [Test]
    public async Task Expiry_RestartSpanning_UnclaimedP2PMail_BouncedThenDestroyedOnSecondExpiry()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        var oldRecvDate = DateTime.UtcNow - TimeSpan.FromDays(15);

        // ---- Instance 1 (pre-restart): a delivered, unclaimed P2P mail
        // expires → bounced back to the original sender through the REAL
        // MailDeliveryTask path, attachments intact.
        var mail = CreateMail(TestMailId, _sender.Id, _sender.Name, _receiver.Id, _receiver.Name,
            MailType.Normal, MailStatus.Unread, copperCoins: 250, recvDate: oldRecvDate);
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        new MailDeliveryTask().Execute();

        await Assert.That(mail.Header.Returned).IsTrue();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(_sender.Id);
        await Assert.That(mail.ReceiverName).IsEqualTo("Sender"); // canonical normalized name
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(250);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();

        // ---- Instance 2 (post-restart): the SAME mail as Load would
        // restore it — returned=true, roles swapped, the old received_date
        // again (still past the retention window). The sweep must destroy
        // it: Returned=true → canBounce false → destroy branch.
        var mail2 = ReseedFromLoadPath(TestMailId, _receiver.Id, _receiver.Name, _sender.Id, "Sender",
            MailType.Normal, MailStatus.Unread, returned: true, copperCoins: 250, extra: 0,
            recvDate: oldRecvDate);
        var mailManager2 = CreateMailManager(_nameManager);
        mailManager2._allPlayerMails = [];
        mailManager2._allPlayerMails[mail2.Id] = mail2;

        mailManager2.CheckAllMailTimings();

        await Assert.That(mailManager2.AllPlayerMails.ContainsKey(TestMailId)).IsFalse();
    }

    [Test]
    public async Task Expiry_RestartSpanning_SystemMail_Destroyed()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        var oldRecvDate = DateTime.UtcNow - TimeSpan.FromDays(15);

        // System mail (SenderId 0) can never bounce; the destroy branch is
        // the only fate across restarts. Instance 1 runs the real task path.
        var mail = CreateMail(TestMailId, 0, ".system", _receiver.Id, _receiver.Name,
            MailType.Normal, MailStatus.Read, copperCoins: 500, recvDate: oldRecvDate);
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        new MailDeliveryTask().Execute();

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsFalse();

        // Instance 2: a second system mail with the same persisted shape is
        // also destroyed (the sweep is idempotent over the load-path fields).
        var mail2 = ReseedFromLoadPath(TestMailId + 1, 0, ".system", _receiver.Id, _receiver.Name,
            MailType.Normal, MailStatus.Read, returned: false, copperCoins: 500, extra: 0,
            recvDate: oldRecvDate);
        var mailManager2 = CreateMailManager(_nameManager);
        mailManager2._allPlayerMails = [];
        mailManager2._allPlayerMails[mail2.Id] = mail2;

        mailManager2.CheckAllMailTimings();

        await Assert.That(mailManager2.AllPlayerMails.ContainsKey(TestMailId + 1)).IsFalse();
    }

    [Test]
    public async Task Expiry_ChargedMailUnclaimed_SenderReceivesCodRefundAndMailDestroyed()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        var oldRecvDate = DateTime.UtcNow - TimeSpan.FromDays(15);

        // Charged (COD) mail: cannot bounce (Charged is not Normal/Express),
        // so the destroy branch runs — and the COD charge (Header.Extra)
        // must be refunded to the sender through the [COD] payment-mail
        // pattern before the mail is destroyed.
        var mail = CreateMail(TestMailId, _sender.Id, _sender.Name, _receiver.Id, _receiver.Name,
            MailType.Charged, MailStatus.Unread, copperCoins: 0, extra: 500, recvDate: oldRecvDate);
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        new MailDeliveryTask().Execute();

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsFalse();

        var refundMail = _mailManager._allPlayerMails.Values
            .FirstOrDefault(m => m.Header.ReceiverId == _sender.Id && m.Body.CopperCoins == 500);
        await Assert.That(refundMail).IsNotNull();
        await Assert.That(refundMail!.Title).Contains("COD");
        await Assert.That(refundMail.MailType).IsEqualTo(MailType.Normal);
        await Assert.That(refundMail.Header.SenderId).IsEqualTo(0u);
    }

    [Test]
    public async Task Return_ChargedMail_BouncedWithExtraClearedAndRefundMailSent()
    {
        // A Charged mail returned by the receiver (ReturnMail) must not
        // re-levy the COD charge on the original sender: the bounce clears
        // Header.Extra and refunds it through the [COD] payment-mail pattern.
        var mail = CreateMail(TestMailId, _sender.Id, _sender.Name, _receiver.Id, _receiver.Name,
            MailType.Charged, MailStatus.Read, copperCoins: 0, extra: 500);
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        var result = _mailManager.ReturnMail(_receiver, TestMailId);

        await Assert.That(result).IsTrue();
        await Assert.That(mail.Header.Returned).IsTrue();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(_sender.Id);
        await Assert.That(mail.Header.Extra).IsEqualTo(0); // charge cleared — no double levy

        var refundMail = _mailManager._allPlayerMails.Values
            .FirstOrDefault(m => m.Header.ReceiverId == _sender.Id && m.Body.CopperCoins == 500);
        await Assert.That(refundMail).IsNotNull();
        await Assert.That(refundMail!.Title).Contains("COD");
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();
    }
}
