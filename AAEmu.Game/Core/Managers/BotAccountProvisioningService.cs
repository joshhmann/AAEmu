using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Account category for aaemu_login.users.account_type. 0 = ordinary player
/// account (the login server's default); 1 = managed bot account
/// (ARCHITECTURE_REVIEW deliverable 10 slice 4 / ROADMAP M6.0 — ManagedBotAccount,
/// account_type=HeadlessBot).
/// </summary>
public enum BotAccountType : byte
{
    /// <summary>Ordinary human account. Never provisioned by this service.</summary>
    Player = 0,

    /// <summary>Managed bot account: real row, client login blocked.</summary>
    HeadlessBot = 1,
}

/// <summary>
/// A provisioned managed bot account (the real aaemu_login.users row).
/// </summary>
/// <param name="AccountId">The users.id of the managed account.</param>
/// <param name="Username">The managed username (bot_managed_* namespace).</param>
/// <param name="Secret">The generated managed secret (plaintext, returned once at
/// provisioning time; null when an existing provisioned account was reused).</param>
/// <param name="AccountType">Always <see cref="BotAccountType.HeadlessBot"/> for
/// provisioned accounts.</param>
/// <param name="ClientLoginBlocked">True — bot accounts are banned at the login
/// server (users.banned = 1) so no client can ever authenticate with them.</param>
public sealed record BotProvisionedAccount(
    uint AccountId,
    string Username,
    string Secret,
    BotAccountType AccountType,
    bool ClientLoginBlocked);

/// <summary>
/// Managed bot account provisioning — real aaemu_login.users rows with the
/// HeadlessBot flag (ARCHITECTURE_REVIEW deliverable 10, slice 4 / t_302b67bf).
///
/// A bot citizen is a REAL managed account + ordinary character rows; the pilot's
/// DB-row-less HeadlessSession.Create is E2E-fixture only and is NOT the
/// production citizen path (review correction (b)). This service is the
/// production account side of that path:
///
///  1. <see cref="EnsureLoginSchema"/> — idempotently adds users.account_type to
///     the login DB (self-healing schema; the same migration ships as a
///     SQL/updates file for managed environments).
///  2. <see cref="ProvisionBotAccount"/> — INSERTs a real users row in the
///     bot_managed_* namespace with account_type=HeadlessBot and banned=1.
///     banned=1 is the client-login block: the login server's existing auth
///     path (LoginController.Login) checks users.banned on every auth and
///     denies — no login-server code change needed, and the account_type
///     column is the durable marker a future login-side check can consume.
///  3. Lookup helpers (<see cref="GetAccountType"/>, <see cref="IsBotAccount"/>).
///
/// Policy: this service NEVER creates or adopts a non-bot account. Re-provisioning
/// an existing bot_managed_ username is idempotent (returns the existing row);
/// provisioning over an existing Player account throws.
///
/// The login DB is reached through the game server's MySQL connection with a
/// database-qualified table name (aaemu_login.users) — the same MySQL instance
/// hosts both schemas (E2E/dev: root; prod: the ops-applied SQL/updates file +
/// grants). Failures are logged, never fatal to the server.
/// </summary>
public interface IBotAccountProvisioningService
{
    /// <summary>Idempotently ensures aaemu_login.users.account_type exists. True when the column is present afterwards.</summary>
    bool EnsureLoginSchema();

    /// <summary>Provisions (or reuses) a managed bot account row. Never touches non-bot accounts.</summary>
    BotProvisionedAccount ProvisionBotAccount(string username);

    /// <summary>Reads the account category for an account id (0/Player when the row is missing).</summary>
    BotAccountType GetAccountType(uint accountId);

    /// <summary>Reads the account category for a username (0/Player when the row is missing).</summary>
    BotAccountType GetAccountType(string username);

    /// <summary>True when the account row is a managed bot account (account_type = HeadlessBot).</summary>
    bool IsBotAccount(uint accountId);
}

public class BotAccountProvisioningService : Singleton<BotAccountProvisioningService>, IBotAccountProvisioningService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Username namespace for managed bot accounts (ROADMAP M6.0: bot_managed_000001…).</summary>
    public const string ManagedUsernamePrefix = "bot_managed_";

    /// <summary>
    /// users.ban_reason written on bot accounts. Maps to the login server's
    /// LoginDeniedReason comment-only value 19 ("use_bot_forever" — the numeric
    /// is stable even though the enum name is undocumented upstream). The reason
    /// only shapes the denial message a client would see; nobody can ever see it
    /// because the block itself (banned=1) is the enforcement.
    /// </summary>
    public const byte ClientLoginBlockBanReason = 19;

    private readonly object _provisionLock = new();

    /// <summary>
    /// Mirrors the login server's account-name rules (LoginController.UsernameRegex:
    /// Unicode letters/digits plus _ . - @, 1-32 chars) so a provisioned name is
    /// always auth-queryable and never rejected by the login server's own parsing.
    /// </summary>
    private static readonly Regex UsernameRegex = new(
        @"^[\p{L}\p{Nd}_.\-@]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public bool EnsureLoginSchema()
    {
        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildEnsureSchemaCheckSql();
            command.Parameters.AddWithValue("@db", "aaemu_login");
            var exists = (long)(command.ExecuteScalar() ?? 0L) > 0;
            if (exists)
                return true;

            command.CommandText = BuildEnsureSchemaAlterSql();
            command.ExecuteNonQuery();
            Logger.Info("Provisioning: added aaemu_login.users.account_type (HeadlessBot flag)");
            return true;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Provisioning: failed to ensure aaemu_login.users.account_type — apply SQL/updates migration manually (bot accounts will not be provisionable)");
            return false;
        }
    }

    /// <inheritdoc />
    public BotProvisionedAccount ProvisionBotAccount(string username)
    {
        if (!IsValidManagedUsername(username))
            throw new ArgumentException(
                $"Managed bot usernames must start with '{ManagedUsernamePrefix}' and match the login server's name rules (Unicode letters/digits/_ . - @, ≤32 chars). Got: '{username}'",
                nameof(username));

        lock (_provisionLock)
        {
            EnsureLoginSchema();

            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();

            // Idempotent reuse: an existing bot_managed_ account is returned as-is.
            command.CommandText = BuildLookupAccountSql();
            command.Parameters.AddWithValue("@username", username);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    var existingId = reader.GetUInt32("id");
                    var existingType = (BotAccountType)reader.GetByte("account_type");
                    var existingBanned = reader.GetBoolean("banned");
                    if (existingType != BotAccountType.HeadlessBot)
                        throw new InvalidOperationException(
                            $"Provisioning refused: '{username}' is an existing non-bot account (account_type={existingType}) — never adopting human accounts");
                    Logger.Info("Provisioning: reused existing managed bot account '{Username}' (id {AccountId}, clientLoginBlocked={Blocked})",
                        username, existingId, existingBanned);
                    return new BotProvisionedAccount(existingId, username, null, existingType, existingBanned);
                }
            }

            // Strong random managed credential. Stored in the login server's legacy
            // format (base64(SHA256(secret)), 44 chars — PasswordService.IsLegacyFormat)
            // so the login server can verify it if ever needed; the client-login
            // block (banned=1) is the enforcement regardless.
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var storedHash = HashManagedSecret(secret);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            command.Parameters.Clear();
            command.CommandText = BuildProvisionInsertSql();
            command.Parameters.AddWithValue("@username", username);
            command.Parameters.AddWithValue("@password", storedHash);
            command.Parameters.AddWithValue("@email", $"{username}@managed.local");
            command.Parameters.AddWithValue("@last_ip", "127.0.0.1");
            command.Parameters.AddWithValue("@last_login", now);
            command.Parameters.AddWithValue("@created_at", now);
            command.Parameters.AddWithValue("@updated_at", now);
            command.Parameters.AddWithValue("@banned", 1);
            command.Parameters.AddWithValue("@ban_reason", ClientLoginBlockBanReason);
            command.Parameters.AddWithValue("@account_type", (byte)BotAccountType.HeadlessBot);
            command.ExecuteNonQuery();

            command.Parameters.Clear();
            command.CommandText = "SELECT `id` FROM aaemu_login.users WHERE `username` = @username";
            command.Parameters.AddWithValue("@username", username);
            var accountId = (uint)(command.ExecuteScalar() ?? 0u);

            Logger.Info("Provisioning: created managed bot account '{Username}' (id {AccountId}, client login blocked)",
                username, accountId);
            return new BotProvisionedAccount(accountId, username, secret, BotAccountType.HeadlessBot, ClientLoginBlocked: true);
        }
    }

    /// <inheritdoc />
    public BotAccountType GetAccountType(uint accountId)
    {
        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildSelectAccountTypeByAccountIdSql();
            command.Parameters.AddWithValue("@accountId", accountId);
            var result = command.ExecuteScalar();
            return result == null ? BotAccountType.Player : (BotAccountType)Convert.ToByte(result);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Provisioning: GetAccountType({AccountId}) failed", accountId);
            return BotAccountType.Player;
        }
    }

    /// <inheritdoc />
    public BotAccountType GetAccountType(string username)
    {
        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildSelectAccountTypeByUsernameSql();
            command.Parameters.AddWithValue("@username", username);
            var result = command.ExecuteScalar();
            return result == null ? BotAccountType.Player : (BotAccountType)Convert.ToByte(result);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Provisioning: GetAccountType('{Username}') failed", username);
            return BotAccountType.Player;
        }
    }

    /// <inheritdoc />
    public bool IsBotAccount(uint accountId) => GetAccountType(accountId) == BotAccountType.HeadlessBot;

    // ------------------------------------------------------------------ pure logic (hermetic-testable)

    /// <summary>Validates a managed bot username: namespace prefix + login-server name rules.</summary>
    internal static bool IsValidManagedUsername(string username)
        => !string.IsNullOrEmpty(username)
           && username.Length > ManagedUsernamePrefix.Length
           && username.StartsWith(ManagedUsernamePrefix, StringComparison.Ordinal)
           && UsernameRegex.IsMatch(username);

    /// <summary>Login-server legacy password format: base64(SHA256(secret)) — 44 chars, verifiable by PasswordService.</summary>
    internal static string HashManagedSecret(string secret)
        => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    // ------------------------------------------------------------------ SQL shapes (kept as builders so the
    // ------------------------------------------------------------------ hermetic rig can lock the data contract)

    internal static string BuildEnsureSchemaCheckSql()
        => "SELECT COUNT(*) FROM information_schema.COLUMNS " +
           "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'users' AND COLUMN_NAME = 'account_type'";

    internal static string BuildEnsureSchemaAlterSql()
        => "ALTER TABLE `aaemu_login`.`users` " +
           "ADD COLUMN `account_type` tinyint unsigned NOT NULL DEFAULT 0 " +
           "COMMENT '0=Player, 1=HeadlessBot (managed bot account; client login blocked)'";

    internal static string BuildLookupAccountSql()
        => "SELECT `id`, `account_type`, `banned` FROM aaemu_login.users WHERE `username` = @username";

    internal static string BuildProvisionInsertSql()
        => "INSERT INTO aaemu_login.users " +
           "(`username`, `password`, `email`, `last_ip`, `last_login`, `created_at`, `updated_at`, `banned`, `ban_reason`, `account_type`) " +
           "VALUES (@username, @password, @email, @last_ip, @last_login, @created_at, @updated_at, @banned, @ban_reason, @account_type)";

    internal static string BuildSelectAccountTypeByAccountIdSql()
        => "SELECT `account_type` FROM aaemu_login.users WHERE `id` = @accountId";

    internal static string BuildSelectAccountTypeByUsernameSql()
        => "SELECT `account_type` FROM aaemu_login.users WHERE `username` = @username";
}
