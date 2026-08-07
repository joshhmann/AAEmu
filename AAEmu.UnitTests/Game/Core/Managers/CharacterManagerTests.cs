using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;

using NLog;
using NLog.Config;
using NLog.Targets;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel] // reconfigures the process-global NLog LogManager for log-capture tests
public class CharacterManagerTests
{
    private LoggingConfiguration _previousLogConfig;
    private MemoryTarget _logTarget;

    [Before(Test)]
    public void SetUpLogCapture()
    {
        _previousLogConfig = LogManager.Configuration;
        _logTarget = new MemoryTarget("unit-test-memory") { Layout = "${level}|${message}" };
        var config = new LoggingConfiguration();
        config.AddRuleForAllLevels(_logTarget);
        LogManager.Configuration = config;
    }

    [After(Test)]
    public void TearDownLogCapture()
    {
        LogManager.Configuration = _previousLogConfig; // restore (null = default no-config)
    }

    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockWorld = Mock.Of<IWorldManager>();
        var mockAccount = Mock.Of<IAccountManager>();
        var mockName = Mock.Of<INameManager>();
        var mockCharId = Mock.Of<ICharacterIdManager>();
        var mockFaction = Mock.Of<IFactionManager>();
        var mockSkill = Mock.Of<ISkillManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockHousing = Mock.Of<IHousingManager>();
        var mockFamily = Mock.Of<IFamilyManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockTask = Mock.Of<ITaskManager>();

        var manager = new CharacterManager(
            mockWorld.Object,
            mockAccount.Object,
            mockName.Object,
            mockCharId.Object,
            mockFaction.Object,
            mockSkill.Object,
            mockItem.Object,
            mockHousing.Object,
            mockFamily.Object,
            mockMail.Object,
            mockTask.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockAccount);
        Mock.VerifyNoOtherCalls(mockName);
        Mock.VerifyNoOtherCalls(mockCharId);
        Mock.VerifyNoOtherCalls(mockFaction);
        Mock.VerifyNoOtherCalls(mockSkill);
        Mock.VerifyNoOtherCalls(mockItem);
        Mock.VerifyNoOtherCalls(mockHousing);
        Mock.VerifyNoOtherCalls(mockFamily);
        Mock.VerifyNoOtherCalls(mockMail);
        Mock.VerifyNoOtherCalls(mockTask);
    }

    [Test]
    public async Task LogAbilitySetNotice_LogsWarn_NotError()
    {
        // Act — anti-cheat notice for a custom 3-ability class (E2E bot provisioning path)
        CharacterManager.LogAbilitySetNotice(42, "Bot2c2", AbilityType.Fight, AbilityType.Magic, AbilityType.Will);

        // Assert — exactly one event, at Warn, with the notice-not-rejection wording
        var events = _logTarget.Logs.Where(l => l.Contains("2nd and/or 3rd ability")).ToList();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).StartsWith("Warn|");
        await Assert.That(events[0]).Contains("creation is NOT rejected");
        await Assert.That(_logTarget.Logs.Any(l => l.StartsWith("Error|") && l.Contains("2nd and/or 3rd ability"))).IsFalse();
    }

    [Test]
    public async Task Create_WithSecondThirdAbility_LogsWarnNoticeAndCreationProceeds()
    {
        // Arrange — mocks need NO configuration: ValidateCharacterName defaults to
        // CharacterCreateError.Ok (== 0), so Create runs PAST the notice. The rig
        // has no config/DB wired, so Create dies somewhere downstream (the first
        // real dependency after the notice) — the exact exception type is not the
        // contract. What matters: the notice did NOT reject the creation.
        var mockWorld = Mock.Of<IWorldManager>();
        var mockAccount = Mock.Of<IAccountManager>();
        var mockName = Mock.Of<INameManager>();
        var mockCharId = Mock.Of<ICharacterIdManager>();
        var mockFaction = Mock.Of<IFactionManager>();
        var mockSkill = Mock.Of<ISkillManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockHousing = Mock.Of<IHousingManager>();
        var mockFamily = Mock.Of<IFamilyManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockTask = Mock.Of<ITaskManager>();

        var manager = new CharacterManager(
            mockWorld.Object,
            mockAccount.Object,
            mockName.Object,
            mockCharId.Object,
            mockFaction.Object,
            mockSkill.Object,
            mockItem.Object,
            mockHousing.Object,
            mockFamily.Object,
            mockMail.Object,
            mockTask.Object);

        var connection = new GameConnection(Mock.Of<ISession>().Object) { AccountId = 42 };

        // Act — 2nd/3rd ability pre-set (custom class), like the E2E bot provisioning.
        // Any exception here is expected (unit rig is not a full server); the
        // behavior-unchanged assertions below prove the notice did not reject.
        try
        {
            manager.Create(connection, "Bot2c2", Race.Hariharan, Gender.Male, new uint[7], null,
                AbilityType.Fight, AbilityType.Magic, AbilityType.Will, 1);
        }
        catch (Exception)
        {
            // expected — downstream of the notice, out of this card's scope
        }

        // Assert — the notice fired at Warn, not Error ...
        var events = _logTarget.Logs.Where(l => l.Contains("2nd and/or 3rd ability")).ToList();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).StartsWith("Warn|");
        // ... and creation PROCEEDED past the notice: the next dependency call
        // (GetAccountDetails) executed. If the notice ever became a rejection
        // (early return / throw), this WasCalled check fails.
        mockAccount.GetAccountDetails(42).WasCalled();
    }
}
