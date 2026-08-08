using AAEmu.Game.Core.Managers;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Hermetic rig for the slice-4 managed bot account provisioning data
/// contract (t_302b67bf). No MySQL: these tests lock the SQL shapes, the
/// HeadlessBot flag semantics, and the client-login block values the login
/// server's EXISTING auth path enforces (users.banned). The live round-trip
/// (real rows + activation) rides the env-gated live rig — see
/// HeadlessSessionProvisioningLiveTests.
/// </summary>
public class BotAccountProvisioningServiceTests
{
    // ---------------------------------------------------------------- username rules

    [Test]
    public async Task IsValidManagedUsername_RejectsNonManagedNamespace()
    {
        // Human-style names must never be provisionable — the service may
        // never create or adopt a non-bot account.
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("josh")).IsFalse();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("player_123")).IsFalse();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("BotManaged_001")).IsFalse(); // wrong case prefix
    }

    [Test]
    public async Task IsValidManagedUsername_RejectsEmptyOrBarePrefix()
    {
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("")).IsFalse();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("bot_managed_")).IsFalse(); // nothing after the prefix
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername(null!)).IsFalse();
    }

    [Test]
    public async Task IsValidManagedUsername_RejectsCharactersOutsideLoginServerNameRules()
    {
        // Mirrors LoginController.UsernameRegex (^[\p{L}\p{Nd}_.\-@]{1,32}$) —
        // a provisioned name must always be auth-queryable.
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("bot_managed_über*")).IsFalse();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("bot_managed_has space")).IsFalse();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername(
            "bot_managed_" + new string('a', 40))).IsFalse(); // > 32 chars total
    }

    [Test]
    public async Task IsValidManagedUsername_AcceptsManagedNames()
    {
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("bot_managed_000001")).IsTrue();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("bot_managed_rig_0001")).IsTrue();
        await Assert.That(BotAccountProvisioningService.IsValidManagedUsername("bot_managed_Äffchen_01")).IsTrue(); // unicode letters OK
    }

    [Test]
    public void ProvisionBotAccount_RejectsNonManagedUsername()
    {
        Assert.Throws<ArgumentException>(() => BotAccountProvisioningService.Instance.ProvisionBotAccount("josh"));
    }

    // ---------------------------------------------------------------- credential hygiene

    [Test]
    public async Task HashManagedSecret_ProducesLoginServerLegacyFormat()
    {
        // The stored password must be verifiable by PasswordService's legacy
        // path: base64(SHA256(secret)) = 44 chars decoding to 32 bytes.
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var stored = BotAccountProvisioningService.HashManagedSecret(secret);

        await Assert.That(stored.Length).IsEqualTo(44);
        await Assert.That(Convert.FromBase64String(stored).Length).IsEqualTo(32);
        // Deterministic for the same secret.
        await Assert.That(BotAccountProvisioningService.HashManagedSecret(secret)).IsEqualTo(stored);
        // A different secret hashes differently.
        await Assert.That(BotAccountProvisioningService.HashManagedSecret(secret + "x")).IsNotEqualTo(stored);
    }

    // ---------------------------------------------------------------- client-login block data contract

    [Test]
    public async Task ClientLoginBlockBanReason_IsLoginServersUseBotForeverValue()
    {
        // users.ban_reason = 19 maps to the login server's comment-only
        // "use_bot_forever" reason (LoginDeniedReason; numeric stable even
        // though the enum name is undocumented upstream). The reason only
        // shapes the denial message; banned=1 is the enforcement.
        await Assert.That(BotAccountProvisioningService.ClientLoginBlockBanReason).IsEqualTo((byte)19);
    }

    [Test]
    public async Task HeadlessBotAccountType_IsOne()
    {
        // users.account_type contract: 0 = Player (login server default),
        // 1 = HeadlessBot (managed bot account).
        await Assert.That((byte)BotAccountType.HeadlessBot).IsEqualTo((byte)1);
        await Assert.That((byte)BotAccountType.Player).IsEqualTo((byte)0);
    }

    [Test]
    public async Task ProvisionInsertSql_CarriesHeadlessBotBlockContract()
    {
        var sql = BotAccountProvisioningService.BuildProvisionInsertSql();

        // Real managed row in the LOGIN database.
        await Assert.That(sql).Contains("aaemu_login.users");
        // The HeadlessBot flag column + value parameter.
        await Assert.That(sql).Contains("`account_type`");
        await Assert.That(sql).Contains("@account_type");
        // The client-login block: banned + ban_reason ride the login
        // server's EXISTING auth check (LoginController.Login denies
        // banned accounts before any world access).
        await Assert.That(sql).Contains("`banned`");
        await Assert.That(sql).Contains("@banned");
        await Assert.That(sql).Contains("`ban_reason`");
        await Assert.That(sql).Contains("@ban_reason");
        // Credential columns for the managed secret.
        await Assert.That(sql).Contains("`username`");
        await Assert.That(sql).Contains("`password`");
        await Assert.That(sql).Contains("`email`");
    }

    [Test]
    public async Task EnsureSchemaAlterSql_TargetsLoginUsersAccountType()
    {
        var sql = BotAccountProvisioningService.BuildEnsureSchemaAlterSql();

        await Assert.That(sql).Contains("ALTER TABLE `aaemu_login`.`users`");
        await Assert.That(sql).Contains("ADD COLUMN `account_type`");
        await Assert.That(sql).Contains("DEFAULT 0"); // existing rows stay Player
    }

    [Test]
    public async Task GetAccountType_WithoutMySql_ReturnsPlayerGracefully()
    {
        // No MySQL configured in the hermetic gate: the lookup must degrade to
        // Player (never throw) — the provisioning path fails loudly at the
        // INSERT instead of silently misclassifying accounts.
        await Assert.That(BotAccountProvisioningService.Instance.GetAccountType(123456789u)).IsEqualTo(BotAccountType.Player);
        await Assert.That(BotAccountProvisioningService.Instance.GetAccountType("bot_managed_absent_0001")).IsEqualTo(BotAccountType.Player);
    }
}
