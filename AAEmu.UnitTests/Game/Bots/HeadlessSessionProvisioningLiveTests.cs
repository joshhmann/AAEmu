using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AAEmu.Game.Core.Managers;
using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// Live-rig gate: skips unless AAEMU_LIVE_RIG=1 AND AAEMU_E2E_DB_PASSWORD are
/// set (the e2e MySQL root password). The gate stays hermetic elsewhere.
/// </summary>
public sealed class LiveRigOnlyAttribute : SkipAttribute
{
    public LiveRigOnlyAttribute()
        : base("Live rig: set AAEMU_LIVE_RIG=1 and AAEMU_E2E_DB_PASSWORD (e2e MySQL root password)")
    {
    }

    public override Task<bool> ShouldSkip(TestRegisteredContext testContext)
    {
        var enabled = Environment.GetEnvironmentVariable("AAEMU_LIVE_RIG") is ("1" or "true" or "True");
        var dbPassword = Environment.GetEnvironmentVariable("AAEMU_E2E_DB_PASSWORD");
        return Task.FromResult(!enabled || string.IsNullOrEmpty(dbPassword));
    }
}

/// <summary>
/// Live provision → activate → persist → deactivate round-trip for the
/// PRODUCTION HeadlessSession path (t_302b67bf slice 4).
///
/// Boots a REAL game server (published from this tree) against the e2e MySQL
/// with the provisioning control host enabled (AAEMU_BOT_PROVISION_TEST=1,
/// loopback :1261), then drives the production path end-to-end:
///
///   provision   → real aaemu_login.users row (HeadlessBot + banned=1) +
///                 real characters row + ActivateHeadless embodiment
///   verify rows → flag/block/ownership data contract in MySQL
///   setLevel    → in-memory gameplay mutation (persist probe)
///   deactivate  → CharacterLifecycleService.Deactivate (leave-save)
///   verify      → character row persisted (level survives), not deleted
///   re-provision→ idempotent reuse of the managed account
///   teardown    → rows deleted, server killed (try/finally)
///
/// Gate-discipline: SKIPPED unless AAEMU_LIVE_RIG=1 + AAEMU_E2E_DB_PASSWORD.
/// One-stack discipline: only run when no other E2E suite is in flight (the
/// rig never touches the shared E2E runtime or the :1239/:1237 servers; it
/// publishes to its own scratch dir and boots on :1279/:1280).
/// </summary>
public class HeadlessSessionProvisioningLiveTests
{
    private const string Username = "bot_managed_rig_0001";
    // Normalized display name (human create path normalizes: first char upper,
    // rest lower) — the provision path mirrors it, so the row/response name is
    // "Rigbot01" even when the request says "RigBot01".
    private const string CharacterName = "Rigbot01";
    private const int ControlPort = 1261;
    private const int GamePort = 1279;
    // 1281, not 1280: a lingering shared-stack game (e.g. another worker's
    // finished-but-not-torn-down run) holds :1280 — the rig must boot without
    // touching the shared stack's ports.
    private const int StreamPort = 1281;

    private static string DbHost => Environment.GetEnvironmentVariable("AAEMU_E2E_DB_HOST") ?? "127.0.0.1";
    private static string DbPort => Environment.GetEnvironmentVariable("AAEMU_E2E_DB_PORT") ?? "3306";
    private static string DbPassword => Environment.GetEnvironmentVariable("AAEMU_E2E_DB_PASSWORD") ?? "";

    private static string RepoRoot { get; } = FindRepoRoot();
    private static string RigDir { get; set; } = "";
    private Process _server;
    private uint _accountId;
    private uint _characterId;

    [Test]
    [LiveRigOnly]
    [NotInParallel]
    [Timeout(900_000)]
    public async Task Provision_Activate_Persist_Deactivate_RoundTrip()
    {
        var passed = false;
        try
        {
            await RunRoundTripAsync();
            passed = true;
        }
        finally
        {
            CleanupRows();
            KillServer();
            // Remove the rig dir only on success — on failure the server.log
            // inside it is the diagnosis trail.
            if (passed && Directory.Exists(RigDir) && !string.IsNullOrEmpty(RigDir))
            {
                try { Directory.Delete(RigDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private async Task RunRoundTripAsync()
    {
        // ------------------------------------------------------------------ arrange
        RigDir = Path.Combine(Path.GetTempPath(), "aaemu-live-rig-" + Guid.NewGuid().ToString("N")[..8]);
        PublishServer();
        WriteRuntimeConfig();
        StartServer();

        try
        {
            using var control = await ConnectControlAsync(TimeSpan.FromSeconds(180));

            // ------------------------------------------------------------------ provision
            var provision = await SendCommandAsync(control, new
            {
                cmd = "provision",
                username = Username,
                name = CharacterName,
                race = "Nuian",
                gender = "Male",
                level = 1
            }, retries: 90, retryDelay: TimeSpan.FromSeconds(2)); // boot-readiness guard (templates load takes ~1-2min)

            // Fail fast WITH the server's actual error text — a bare ok:false
            // is undiagnosable (run-5 lesson: the retry budget exhausted
            // against the wrong listener and the reason was invisible).
            if (!IsOk(provision))
                throw new InvalidOperationException("provision failed: " + GetError(provision));
            var provisionData = GetData(provision);
            _accountId = GetUInt(provisionData, "accountId");
            _characterId = GetUInt(provisionData, "characterId");
            await Assert.That(_accountId).IsGreaterThan(0u);
            await Assert.That(_characterId).IsGreaterThan(0u);
            await Assert.That(GetBool(provisionData, "clientLoginBlocked")).IsTrue();
            await Assert.That(GetString(provisionData, "name")).IsEqualTo(CharacterName);

            // ------------------------------------------------------------------ verify real rows: account
            using (var conn = OpenDb("aaemu_login"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT `id`, `username`, `account_type`, `banned`, `ban_reason`, `email` " +
                                  "FROM users WHERE `username` = @u";
                cmd.Parameters.AddWithValue("@u", Username);
                using var reader = cmd.ExecuteReader();
                await Assert.That(reader.Read()).IsTrue();
                await Assert.That(reader.GetUInt32("id")).IsEqualTo(_accountId);
                // MySql.Data returns tinyint as Int32 — cast, don't GetByte.
                await Assert.That(reader.GetInt32("account_type")).IsEqualTo((int)BotAccountType.HeadlessBot);
                await Assert.That(reader.GetBoolean("banned")).IsTrue(); // client login blocked
                await Assert.That(reader.GetInt32("ban_reason")).IsEqualTo((int)BotAccountProvisioningService.ClientLoginBlockBanReason);
                await Assert.That(reader.GetString("email")).IsEqualTo($"{Username}@managed.local");
            }

            // ------------------------------------------------------------------ verify real rows: character (ordinary row, owned by the bot account)
            using (var conn = OpenDb("aaemu_game"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT `id`, `account_id`, `name`, `level`, `race`, `world_id`, `deleted` " +
                                  "FROM characters WHERE `id` = @id";
                cmd.Parameters.AddWithValue("@id", _characterId);
                using var reader = cmd.ExecuteReader();
                await Assert.That(reader.Read()).IsTrue();
                await Assert.That(reader.GetUInt32("account_id")).IsEqualTo(_accountId);
                await Assert.That(reader.GetString("name")).IsEqualTo(CharacterName);
                await Assert.That(reader.GetInt32("level")).IsEqualTo(1);
                // Ordinary row contract: world_id is 0 for fresh rows — ServerId
                // is only ever READ from the row, never assigned (verified
                // empirically: the E2E harness's human-path characters also
                // carry world_id=0). Real placement lives in the spawn
                // coordinates (template spawn position) and the embodiment
                // (WorldManager membership, proven by setLevel below).
                await Assert.That(reader.GetUInt32("world_id")).IsEqualTo(0u);
                await Assert.That(reader.GetInt32("deleted")).IsEqualTo(0);
            }

            // ------------------------------------------------------------------ persist probe: mutate in-memory, deactivate, row must keep it
            var setLevel = await SendCommandAsync(control, new { cmd = "setLevel", characterId = _characterId, level = 7 });
            await Assert.That(IsOk(setLevel)).IsTrue();
            await Assert.That(GetInt(GetData(setLevel), "level")).IsEqualTo(7);

            var deactivate = await SendCommandAsync(control, new { cmd = "deactivate", characterId = _characterId, reason = "Logout" });
            await Assert.That(IsOk(deactivate)).IsTrue();

            using (var conn = OpenDb("aaemu_game"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT `level`, `deleted` FROM characters WHERE `id` = @id";
                cmd.Parameters.AddWithValue("@id", _characterId);
                using var reader = cmd.ExecuteReader();
                await Assert.That(reader.Read()).IsTrue();
                await Assert.That(reader.GetInt32("level")).IsEqualTo(7); // leave-save persisted the mutation
                await Assert.That(reader.GetInt32("deleted")).IsEqualTo(0); // deactivate persists, never deletes
            }

            // ------------------------------------------------------------------ idempotent re-provision (same managed account, no duplicate row)
            var reprovision = await SendCommandAsync(control, new
            {
                cmd = "provision",
                username = Username,
                name = "RigBot02", // fresh character name: bot names share the
                                   // human NameManager namespace — reusing
                                   // "Rigbot01" below must now ADOPT the
                                   // existing row (restart-idempotency,
                                   // t_db5b2be7), not create a duplicate
                race = "Nuian",
                gender = "Male",
                level = 1
            });
            await Assert.That(IsOk(reprovision)).IsTrue();
            await Assert.That(GetUInt(GetData(reprovision), "accountId")).IsEqualTo(_accountId);
            await Assert.That(GetUInt(GetData(reprovision), "characterId")).IsGreaterThan(0u);

            using (var conn = OpenDb("aaemu_login"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM users WHERE `username` = @u";
                cmd.Parameters.AddWithValue("@u", Username);
                await Assert.That(Convert.ToInt64(cmd.ExecuteScalar())).IsEqualTo(1); // still exactly one row
            }

            // ------------------------------------------------------------------ restart-idempotent adoption (t_db5b2be7)
            // Re-provisioning the SAME character name — exactly what a restart
            // WITHOUT a DB wipe does — must ADOPT the existing row owned by
            // this bot's managed account: same characterId, no duplicate row,
            // no NameAlreadyExists error. The create-only path failed this
            // with NameAlreadyExists on every boot after the first (0/3 bots
            // on the presence demo).
            var adopt = await SendCommandAsync(control, new
            {
                cmd = "provision",
                username = Username,
                name = CharacterName, // "Rigbot01" — provisioned at the top of this round-trip
                race = "Nuian",
                gender = "Male",
                level = 1
            });
            await Assert.That(IsOk(adopt)).IsTrue();
            await Assert.That(GetUInt(GetData(adopt), "characterId")).IsEqualTo(_characterId);

            using (var conn = OpenDb("aaemu_game"))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM characters WHERE `name` = @n AND `account_id` = @a AND `deleted` = 0";
                cmd.Parameters.AddWithValue("@n", CharacterName);
                cmd.Parameters.AddWithValue("@a", _accountId);
                await Assert.That(Convert.ToInt64(cmd.ExecuteScalar())).IsEqualTo(1); // adopted, never duplicated
            }

            // The server log must show the adopt path — and NO NameAlreadyExists
            // rejection for the re-provisioned name.
            var serverLog = File.ReadAllText(Path.Combine(RigDir, "server.log"));
            await Assert.That(serverLog.Contains("rejected by NameManager", StringComparison.Ordinal)).IsFalse();
            await Assert.That(serverLog.Contains("adopted existing character", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            CleanupRows();
        }
    }

    // ------------------------------------------------------------------ server boot

    private static void PublishServer()
    {
        var gameDir = Path.Combine(RigDir, "game");
        RunProcess("dotnet", $"publish {Path.Combine(RepoRoot, "AAEmu.Game", "AAEmu.Game.csproj")} -c Release -o {gameDir} --nologo",
            RepoRoot, TimeSpan.FromMinutes(10));

        // Canonical game data (compact.sqlite3 etc.) copied from the E2E
        // canonical source (the same one E2eStack.EnsureRuntimeLayout uses);
        // ClientData is symlinked (16GB pak, never mutated — same as E2E).
        // The publish output may carry an empty ClientData dir — replace it
        // with the symlink so the pak is not duplicated (E2eStack pattern).
        CopyDirectory(Path.Combine(GameDataRoot, "Data"), Path.Combine(gameDir, "Data"));
        var clientLink = Path.Combine(gameDir, "ClientData");
        if (Directory.Exists(clientLink))
            Directory.Delete(clientLink, recursive: true);
        Directory.CreateSymbolicLink(clientLink, Path.Combine(GameDataRoot, "ClientData"));

        // Capped NLog config from the E2E runtime (the publish output carries
        // the repo's uncapped daily-rotation version).
        var cappedNlog = Path.Combine(E2eRuntimeRoot, "game", "NLog.config");
        if (File.Exists(cappedNlog))
            File.Copy(cappedNlog, Path.Combine(gameDir, "NLog.config"), overwrite: true);
    }

    private static string GameDataRoot => Path.Combine(E2eRuntimeRoot, "game-data");
    private static string E2eRuntimeRoot => Environment.GetEnvironmentVariable("AAEMU_E2E_ROOT") ?? "/root/aaemu-e2e/runtime";

    private void WriteRuntimeConfig()
    {
        var gameDir = Path.Combine(RigDir, "game");
        var config = $$"""
            {
              "Network": { "Host": "127.0.0.1", "Port": {{GamePort}}, "NumConnections": 4 },
              "StreamNetwork": { "Host": "127.0.0.1", "Port": {{StreamPort}} },
              "LoginNetwork": { "Host": "127.0.0.1", "Port": "9" },
              "Connections": {
                "MySQLProvider": {
                  "Host": "{{DbHost}}", "Port": "{{DbPort}}", "User": "root",
                  "Password": "{{DbPassword}}", "Database": "aaemu_game"
                }
              },
              "ClientData": { "Sources": [ "./ClientData/game_pak" ] },
              "HeightMapsEnable": true,
              "World": { "AutoSaveInterval": 0.5 }
            }
            """;
        File.WriteAllText(Path.Combine(gameDir, "Config.Local.json"), config);
    }

    private void StartServer()
    {
        var gameDir = Path.Combine(RigDir, "game");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = gameDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(Path.Combine(gameDir, "AAEmu.Game.dll"));
        psi.Environment["AAEMU_BOT_PROVISION_TEST"] = "1";
        psi.Environment["AAEMU_BOT_PROVISION_PORT"] = ControlPort.ToString();

        _server = Process.Start(psi)!;
        var logPath = Path.Combine(RigDir, "server.log");
        _ = Task.Run(() => DrainToFile(_server.StandardOutput, logPath));
        _ = Task.Run(() => DrainToFile(_server.StandardError, logPath + ".err"));
    }

    private static void DrainToFile(StreamReader reader, string path)
    {
        try
        {
            using var writer = new StreamWriter(path, append: true);
            while (reader.ReadLine() is { } line)
                writer.WriteLine(line);
        }
        catch
        {
            // server died; drain best-effort
        }
    }

    private void KillServer()
    {
        if (_server == null)
            return;
        try
        {
            if (!_server.HasExited)
                _server.Kill(entireProcessTree: true);
            _server.WaitForExit(10_000);
        }
        catch
        {
            // best effort
        }
        _server = null;
    }

    // ------------------------------------------------------------------ control channel

    private static async Task<TcpClient> ConnectControlAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", ControlPort).ConfigureAwait(false);
                return client;
            }
            catch (SocketException)
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"Provisioning control host did not open :{ControlPort} within {timeout.TotalSeconds}s (server boot failed?)");
    }

    private static async Task<JsonElement> SendCommandAsync(TcpClient client, object command,
        int retries = 1, TimeSpan? retryDelay = null)
    {
        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n");

        for (var attempt = 1; ; attempt++)
        {
            await stream.WriteAsync(payload).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();

            // Transient server-not-ready guard (boot race): retry provision.
            var notReady = !root.GetProperty("ok").GetBoolean()
                           && root.TryGetProperty("error", out var err)
                           && err.GetString()?.Contains("server not ready") == true;
            if (notReady && attempt <= retries)
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                continue;
            }

            if (!root.GetProperty("ok").GetBoolean() && attempt == 1)
                Console.WriteLine($"[live-rig] command failed: {JsonSerializer.Serialize(command)} -> {line}");

            return root;
        }
    }

    // ------------------------------------------------------------------ MySQL

    private static MySqlConnection OpenDb(string database)
    {
        var conn = new MySqlConnection(
            $"Server={DbHost};Port={DbPort};User=root;Password={DbPassword};Database={database};Connection Timeout=15");
        conn.Open();
        return conn;
    }

    private void CleanupRows()
    {
        try
        {
            using var game = OpenDb("aaemu_game");
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM characters WHERE `account_id` IN " +
                                  "(SELECT `id` FROM aaemu_login.users WHERE `username` = @u)";
                cmd.Parameters.AddWithValue("@u", Username);
                cmd.ExecuteNonQuery();
            }

            using var login = OpenDb("aaemu_login");
            using (var cmd = login.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM users WHERE `username` = @u";
                cmd.Parameters.AddWithValue("@u", Username);
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // teardown best effort — never mask the test verdict
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AAEmu.Game", "AAEmu.Game.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)));
    }

    private static void RunProcess(string fileName, string arguments, string workingDir, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            throw new TimeoutException($"{fileName} {arguments} did not finish within {timeout.TotalSeconds}s");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {arguments} failed (exit {process.ExitCode}):\n{stderr.Result}");
    }

    private static uint GetUInt(JsonElement data, string name)
        => data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetUInt32() : 0u;

    private static bool GetBool(JsonElement data, string name)
        => data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement data, string name)
        => data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;

    private static string GetString(JsonElement data, string name)
        => data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string GetError(JsonElement root)
        => root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() ?? "" : "(no error field)";

    private static bool IsOk(JsonElement root)
        => root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    private static JsonElement GetData(JsonElement root)
        => root.TryGetProperty("data", out var data) ? data : default;
}
