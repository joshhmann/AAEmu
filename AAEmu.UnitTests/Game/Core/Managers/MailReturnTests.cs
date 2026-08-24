using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class MailReturnTests
{
    private const long TestMailId = 100;

    private CharacterMock _character;
    private MailManager _mailManager;
    private TimeSpan _originalExpireDelay;

    [Before(Test)]
    public void Setup()
    {
        _character = new CharacterMock { AccountId = 1, Id = 1, Name = "tester", Money = 1000 };
        var sender = new CharacterMock { AccountId = 2, Id = 2, Name = "bob" };

        var nameManager = new NameManager();
        nameManager.Load([], [], []);
        nameManager.AddCharacter(_character.Id, _character.Name, 1);
        nameManager.AddCharacter(sender.Id, sender.Name, 1);

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

        _mailManager._allPlayerMails = [];

        _originalExpireDelay = MailManager.MailExpireDelay;
    }

    [After(Test)]
    public void Teardown()
    {
        MailManager.MailExpireDelay = _originalExpireDelay;

        _mailManager._allPlayerMails = null;
        _character = null;
        _mailManager = null;

        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static BaseMail CreateMail(long id, uint senderId, string senderName, uint receiverId, string receiverName,
        MailStatus status, int copperCoins = 0, DateTime? recvDate = null)
    {
        var mail = new BaseMail
        {
            Id = id,
            Title = "test",
            ReceiverName = receiverName,
            MailType = MailType.Normal,
            Header =
            {
                Status = status,
                SenderId = senderId,
                SenderName = senderName,
                ReceiverId = receiverId
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

    [Test]
    public async Task ReturnMail_ReadMailFromOriginalSender_BouncesToIntact()
    {
        var mail = CreateMail(TestMailId, 2, "bob", 1, "tester", MailStatus.Read, copperCoins: 250);
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        var result = _mailManager.ReturnMail(_character, TestMailId);

        await Assert.That(result).IsTrue();
        await Assert.That(mail.Header.Returned).IsTrue();
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        // Roles swapped: original sender is now the receiver (name in canonical normalized form)
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(2u);
        await Assert.That(mail.ReceiverName).IsEqualTo("Bob");
        await Assert.That(mail.Header.SenderId).IsEqualTo(1u);
        // Attachments travel intact
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(250);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)1);
        // Still registered and marked for persistence
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();
        await Assert.That(mail.IsDirty).IsTrue();
    }

    [Test]
    public async Task ReturnMail_DoubleReturn_Refused()
    {
        var mail = CreateMail(TestMailId, 2, "bob", 1, "tester", MailStatus.Read);
        _mailManager._allPlayerMails[mail.Id] = mail;

        await Assert.That(_mailManager.ReturnMail(_character, TestMailId)).IsTrue();

        var secondResult = _mailManager.ReturnMail(_character, TestMailId);

        await Assert.That(secondResult).IsFalse();
        await Assert.That(mail.Header.Returned).IsTrue();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(2u);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();
    }

    [Test]
    public async Task ReturnMail_UnreadMail_Refused()
    {
        var mail = CreateMail(TestMailId, 2, "bob", 1, "tester", MailStatus.Unread);
        _mailManager._allPlayerMails[mail.Id] = mail;

        var result = _mailManager.ReturnMail(_character, TestMailId);

        await Assert.That(result).IsFalse();
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(mail.Header.Returned).IsFalse();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(1u);
        await Assert.That(mail.ReceiverName).IsEqualTo("tester");
    }

    [Test]
    public async Task ReturnMail_ByNonOwner_Refused()
    {
        var mail = CreateMail(TestMailId, 2, "bob", 1, "tester", MailStatus.Read);
        _mailManager._allPlayerMails[mail.Id] = mail;
        var stranger = new CharacterMock { AccountId = 3, Id = 3, Name = "mallory" };

        var result = _mailManager.ReturnMail(stranger, TestMailId);

        await Assert.That(result).IsFalse();
        await Assert.That(mail.Header.Returned).IsFalse();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(1u);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();
    }

    [Test]
    public async Task Expiry_PlayerMailWithAttachments_BouncedBackToSenderIntact()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        var mail = CreateMail(TestMailId, 2, "bob", 1, "tester", MailStatus.Unread,
            copperCoins: 500, recvDate: DateTime.UtcNow - TimeSpan.FromDays(20));
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        _mailManager.CheckAllMailTimings();

        // Canonical fate: bounced once back to the original sender, attachments intact
        await Assert.That(mail.Header.Returned).IsTrue();
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(2u);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(500);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();
    }

    [Test]
    public async Task Expiry_SystemMailUnclaimed_RemovedAndAttachmentsDestroyed()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        // System mail (SenderId 0) cannot bounce; unclaimed attachment fate is destruction
        var mail = CreateMail(TestMailId, 0, ".system", 1, "tester", MailStatus.Read,
            copperCoins: 500, recvDate: DateTime.UtcNow - TimeSpan.FromDays(20));
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        _mailManager.CheckAllMailTimings();

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsFalse();
    }

    [Test]
    public async Task Expiry_MailWithinRetentionWindow_LeftAlone()
    {
        MailManager.MailExpireDelay = TimeSpan.FromDays(14);
        var mail = CreateMail(TestMailId, 2, "bob", 1, "tester", MailStatus.Unread,
            recvDate: DateTime.UtcNow - TimeSpan.FromDays(1));
        mail.IsDelivered = true;
        _mailManager._allPlayerMails[mail.Id] = mail;

        _mailManager.CheckAllMailTimings();

        await Assert.That(mail.Header.Returned).IsFalse();
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(TestMailId)).IsTrue();
    }
}
