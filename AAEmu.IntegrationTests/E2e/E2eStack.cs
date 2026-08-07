using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MySql.Data.MySqlClient;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M2b-E2E live stack orchestration — deterministic Login + Game + MySQL on
/// the dev host:
///   - MySQL 8 container (compose, SQL-seeded aaemu_login/aaemu_game)
///   - Login server process (real binary, real config, port 1237)
///   - Game server process (real binary, canonical Data/ClientData, 1239/1250)
///   - BotDriveBridge on 127.0.0.1:1260 (config-gated, dev config only)
///
/// The stack is REAL: the same binaries, the same config precedence, the same
/// MySQL the prod compose runs — nothing is stubbed or in-process.
/// </summary>
public static class E2eStack
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string E2eRoot { get; } = Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e";
    public static string DbPassword { get; private set; } = "e2e_" + Guid.NewGuid().ToString("N")[..16];
    public static string CanonicalSqliteMd5 { get; private set; } = "";

    public const int LoginPort = 1237;
    public const int GamePort = 1239;
    public const int StreamPort = 1250;
    public const int BridgePort = 1260;
    public const string DbHost = "127.0.0.1";
    public const int DbPort = 3306;

    private static Process _loginProc;
    private static Process _gameProc;
    private static readonly object Gate = new();
    private static bool _stackUp;

    public static string RuntimeLoginDir => Path.Combine(E2eRoot, "runtime", "login");
    public static string RuntimeGameDir => Path.Combine(E2eRoot, "runtime", "game");
    public static string GameDataDir => Path.Combine(E2eRoot, "runtime", "game-data");
    public static string RuntimeSqlite => Path.Combine(RuntimeGameDir, "Data", "compact.sqlite3");
    public static string CanonicalSqlite => Path.Combine(GameDataDir, "Data", "compact.sqlite3");
    public static string ComposeFile => Path.Combine(RepoRoot, "Scripts", "e2e", "docker-compose.yaml");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot locate repo root");
    }

    // ---------------------------------------------------------------- boot

    public static void EnsureUp()
    {
        lock (Gate)
        {
            if (_stackUp)
                return;

            // A previous test run may have left Login/Game processes running
            // (crashed host, interrupted run). They hold the ports and would
            // shadow the fresh servers (the new processes fail to bind, the
            // stale ones answer the wait checks). Kill e2e-owned leftovers
            // first — same adopt/kill discipline as Scripts/e2e/e2e-common.sh.
            KillStaleServers();

            // CYCLE ISOLATION (E2E-1 reset contract): every suite run starts
            // from the byte-identical baseline. Two pollution sources broke
            // runs 7/8/10:
            //   1. the MySQL volume persists bot accounts + completed quests
            //      across runs -> "Quest X already completed, not added!"
            //   2. an aborted seeded-defect phase leaves the RUNTIME sqlite
            //      patched -> the live server loads defected templates and
            //      turn-ins are silently dropped (report NPC mismatch).
            // Wipe the DB volume (fresh SQL re-seed) and restore the runtime
            // sqlite from the canonical copy BEFORE booting anything.
            ResetDbVolume();
            RestoreCanonicalSqlite();
            EnsureDb();

            EnsureServerBinaries();
            EnsureRuntimeLayout();
            StartServers();
            _stackUp = true;
        }
    }

    /// <summary>
    /// Destroys the e2e MySQL container + volume so the next `up` re-seeds
    /// from SQL. Fresh accounts every run: no stale characters, no completed
    /// quests leaking across suite runs.
    /// </summary>
    private static void ResetDbVolume()
    {
        var envFile = Path.Combine(E2eRoot, ".env");
        // Compose project name derives from the compose file's directory
        // (Scripts/e2e -> "e2e"), same as every other invocation in this rig.
        Run("docker", $"compose -f {ComposeFile} --env-file {envFile} down -v", E2eRoot,
            timeoutMs: 120_000, check: false);
    }

    /// <summary>
    /// Kills dotnet processes running AAEmu.Login.dll / AAEmu.Game.dll whose
    /// working directory is under E2E_ROOT — i.e. servers started by a
    /// PREVIOUS E2E test run. Never touches prod servers (cwd differs).
    /// </summary>
    private static void KillStaleServers()
    {
        var root = Path.GetFullPath(E2eRoot);
        foreach (var proc in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                var cmdline = File.ReadAllText($"/proc/{proc.Id}/cmdline").Replace('\0', ' ');
                if (!cmdline.Contains("AAEmu.Login.dll") && !cmdline.Contains("AAEmu.Game.dll"))
                    continue;
                var cwd = new FileInfo($"/proc/{proc.Id}/cwd").LinkTarget;
                if (cwd == null || !Path.GetFullPath(cwd).StartsWith(root, StringComparison.Ordinal))
                    continue;
                Console.WriteLine($"[e2e] killing stale e2e server pid {proc.Id} (cwd {cwd})");
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(10_000);
            }
            catch (Exception)
            {
                // Process died between enumeration and kill — fine.
            }
        }
    }

    private static void Run(string cmd, string args, string workdir, int timeoutMs = 600_000, bool check = true)
    {
        RunCapture(cmd, args, workdir, timeoutMs, check);
    }

    /// <summary>
    /// Runs a command and returns (exitCode, stdout). Unlike <see cref="Run"/>,
    /// the caller decides what a non-zero exit means — used by readiness polls
    /// that must keep waiting while the probed service is not up yet.
    /// </summary>
    private static (int ExitCode, string Stdout) RunCapture(string cmd, string args, string workdir, int timeoutMs = 600_000, bool check = false)
    {
        using var p = Process.Start(new ProcessStartInfo(cmd, args)
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
            throw new TimeoutException($"command timed out: {cmd} {args}");
        Task.WaitAll(stdout, stderr);
        if (check && p.ExitCode != 0)
            throw new InvalidOperationException($"{cmd} {args} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
        return (p.ExitCode, stdout.Result);
    }

    private static void EnsureDb()
    {
        var envFile = Path.Combine(E2eRoot, ".env");
        Directory.CreateDirectory(E2eRoot);
        if (File.Exists(envFile))
        {
            foreach (var line in File.ReadAllLines(envFile))
            {
                if (line.StartsWith("DB_PASSWORD="))
                    DbPassword = line["DB_PASSWORD=".Length..].Trim();
            }
        }
        else
        {
            File.WriteAllText(envFile, $"DB_PASSWORD={DbPassword}\n");
        }

        Run("docker", $"compose -f {ComposeFile} --env-file {envFile} up -d db", E2eRoot);

        // Wait for MySQL by probing it FROM THE TEST PROCESS over TCP
        // (MySql.Data — the same client the Login server uses). No docker
        // exec: passing `mysql -e 'SELECT ...'` through ProcessStartInfo ->
        // compose exec mangles the quoted SQL and the client prints help and
        // exits 1 forever (run14 diagnostic). A direct OpenDb probe only
        // succeeds once the entrypoint finished seeding AND restarted from
        // the temp server to the real one — the exact window Login needs.
        var deadline = DateTime.UtcNow.AddSeconds(180);
        var pollCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            pollCount++;
            try
            {
                // OpenDb() opens the connection itself (already-open on the
                // returned handle — do NOT call Open() again).
                using var conn = OpenDb("aaemu_login");
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM users LIMIT 1";
                _ = cmd.ExecuteScalar();
                Console.WriteLine($"[e2e] db ready (poll #{pollCount})");
                return;
            }
            catch (Exception ex)
            {
                if (pollCount % 10 == 1)
                    Console.WriteLine($"[e2e] db poll #{pollCount} not ready: {ex.GetType().Name}: {ex.Message}");
            }
            Thread.Sleep(2000);
        }

        throw new TimeoutException("MySQL seed did not complete (aaemu_login.users not queryable within 180s)");
    }

    private static void EnsureServerBinaries()
    {
        var loginDll = Path.Combine(RuntimeLoginDir, "AAEmu.Login.dll");
        var gameDll = Path.Combine(RuntimeGameDir, "AAEmu.Game.dll");
        var rebuild = Environment.GetEnvironmentVariable("E2E_REBUILD") == "1";

        if (!rebuild && File.Exists(loginDll) && File.Exists(gameDll))
            return;

        Console.WriteLine("[e2e] publishing Login + Game servers (E2E_REBUILD=1 forces this)...");
        Run("dotnet", $"publish {Path.Combine(RepoRoot, "AAEmu.Login", "AAEmu.Login.csproj")} -c Release -o {RuntimeLoginDir} --nologo", RepoRoot);
        Run("dotnet", $"publish {Path.Combine(RepoRoot, "AAEmu.Game", "AAEmu.Game.csproj")} -c Release -o {RuntimeGameDir} --nologo", RepoRoot);
    }

    private static void EnsureRuntimeLayout()
    {
        if (!Directory.Exists(GameDataDir))
            throw new InvalidOperationException(
                $"E2E data missing at {GameDataDir} — rsync the game .server_files once: " +
                "rsync -a root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/ <E2E_ROOT>/runtime/game-data/");

        // Game runtime: Data is a COPY (the seeded-defect rig patches the
        // runtime copy; canonical stays byte-identical), ClientData is a
        // symlink (16GB, never mutated).
        var gameDataDir = Path.Combine(RuntimeGameDir, "Data");
        if (!File.Exists(RuntimeSqlite))
        {
            Directory.CreateDirectory(gameDataDir);
            CopyDirectory(Path.Combine(GameDataDir, "Data"), gameDataDir);
        }

        var clientLink = Path.Combine(RuntimeGameDir, "ClientData");
        if (!File.Exists(clientLink) && !IsUsableSymlink(clientLink))
        {
            // The publish output may carry an empty ClientData dir — replace it
            // with the symlink so the 16GB pak is not duplicated.
            if (Directory.Exists(clientLink))
                Directory.Delete(clientLink, recursive: true);

            try
            {
                Directory.CreateSymbolicLink(clientLink, Path.Combine(GameDataDir, "ClientData"));
            }
            catch
            {
                throw new InvalidOperationException("Cannot create ClientData symlink (needs unix symlink support)");
            }
        }

        var cfgDir = Path.Combine(RuntimeGameDir, "Configurations");
        if (!Directory.Exists(cfgDir))
            CopyDirectory(Path.Combine(GameDataDir, "Configurations"), cfgDir);

        if (!File.Exists(Path.Combine(RuntimeGameDir, "Config.json")))
            File.Copy(Path.Combine(GameDataDir, "Config.json"), Path.Combine(RuntimeGameDir, "Config.json"));

        if (!File.Exists(Path.Combine(RuntimeLoginDir, "Config.json")))
            File.Copy(Path.Combine(RepoRoot, "AAEmu.Login", "Config.json"), Path.Combine(RuntimeLoginDir, "Config.json"));

        File.WriteAllText(Path.Combine(RuntimeGameDir, "Config.Local.json"), GameLocalConfig());
        File.WriteAllText(Path.Combine(RuntimeLoginDir, "Config.Local.json"), LoginLocalConfig());

        CanonicalSqliteMd5 = Md5File(CanonicalSqlite);
    }

    private static string GameLocalConfig()
        => $$"""
            {
              "Network": { "Host": "*", "Port": 1239, "NumConnections": 10 },
              "StreamNetwork": { "Host": "*", "Port": 1250 },
              "LoginNetwork": { "Host": "127.0.0.1", "Port": "1234" },
              "Connections": {
                "MySQLProvider": {
                  "Host": "127.0.0.1", "Port": "3306", "User": "root",
                  "Password": "{{DbPassword}}", "Database": "aaemu_game"
                }
              },
              "ClientData": { "Sources": [ "./ClientData/game_pak" ] },
              "HeightMapsEnable": true,
              "World": { "AutoSaveInterval": 0.2 },
              "Bots": { "EnableE2EBridge": true, "E2EBridgePort": 1260 }
            }
            """;

    private static string LoginLocalConfig()
        => $$"""
            {
              "InternalNetwork": { "Host": "*", "Port": 1234 },
              "Network": { "Host": "*", "Port": 1237, "NumConnections": 10 },
              "Connections": {
                "MySQLProvider": {
                  "Host": "127.0.0.1", "Port": "3306", "User": "root",
                  "Password": "{{DbPassword}}", "Database": "aaemu_login"
                }
              },
              "GameServers": [
                { "Id": 1, "Name": "AAEmu.Game (e2e)", "Host": "127.0.0.1", "Port": 1239 }
              ]
            }
            """;

    private static void StartServers()
    {
        // Boot order matters: MySQL -> login -> game (the game registers with
        // the login server over the internal connection at boot).
        _loginProc = StartServerProcess("login", RuntimeLoginDir, "AAEmu.Login.dll",
            Path.Combine(E2eRoot, "logs", "login.log"));
        WaitTcp("127.0.0.1", LoginPort, 90);

        _gameProc = StartServerProcess("game", RuntimeGameDir, "AAEmu.Game.dll",
            Path.Combine(E2eRoot, "logs", "game.log"));
        WaitTcp("127.0.0.1", GamePort, 300);
        WaitTcp("127.0.0.1", StreamPort, 300);
        WaitBridge(60);
    }

    private static Process StartServerProcess(string name, string dir, string dll, string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var p = Process.Start(new ProcessStartInfo("dotnet", dll)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) WriteLog(log, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) WriteLog(log, e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Console.WriteLine($"[e2e] {name} server started pid={p.Id} log={logPath}");
        return p;
    }

    private static void WriteLog(FileStream log, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        try
        {
            log.Write(bytes, 0, bytes.Length);
            log.Flush();
        }
        catch
        {
        }
    }

    private static void WaitTcp(string host, int port, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(host, port);
                return;
            }
            catch
            {
                Thread.Sleep(1000);
            }
        }

        throw new TimeoutException($"port {port} never opened within {timeoutSeconds}s");
    }

    private static void WaitBridge(int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new BotDriveClient(BridgePort);
                var pong = client.Call("{\"cmd\":\"ping\"}", 5000);
                if (pong.GetProperty("pong").GetBoolean())
                    return;
            }
            catch
            {
                Thread.Sleep(1000);
            }
        }

        throw new TimeoutException("BotDriveBridge never came up");
    }

    // --------------------------------------------------------------- control

    /// <summary>Stops and restarts ONLY the game server (MySQL + login stay).</summary>
    public static void RestartGameServer()
    {
        StopGameServer();
        _gameProc = StartServerProcess("game", RuntimeGameDir, "AAEmu.Game.dll",
            Path.Combine(E2eRoot, "logs", "game-restart.log"));
        WaitTcp("127.0.0.1", GamePort, 300);
        WaitTcp("127.0.0.1", StreamPort, 300);
        WaitBridge(60);
    }

    public static void StopGameServer()
    {
        try
        {
            _gameProc?.Kill(entireProcessTree: true);
            _gameProc?.WaitForExit(10_000);
        }
        catch
        {
        }

        _gameProc = null;
    }

    public static void StopAll()
    {
        StopGameServer();
        try
        {
            _loginProc?.Kill(entireProcessTree: true);
            _loginProc?.WaitForExit(10_000);
        }
        catch
        {
        }

        _loginProc = null;
        _stackUp = false;
    }

    // ------------------------------------------------------------------ data

    /// <summary>True when the path is a symlink pointing at a real directory.</summary>
    private static bool IsUsableSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget != null && Directory.Exists(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }
    }

    private static string Md5File(string path)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(md5.ComputeHash(fs));
    }

    public static string RuntimeSqliteMd5() => Md5File(RuntimeSqlite);

    /// <summary>Restores the runtime sqlite to the byte-identical canonical copy.</summary>
    public static void RestoreCanonicalSqlite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimeSqlite)!);
        File.Copy(CanonicalSqlite, RuntimeSqlite, overwrite: true);
    }

    /// <summary>
    /// Seeds the known quest defect at the DATA level (the regression-harness
    /// contract): quest 251's report NPC (act id 104, npc 3512) is swapped to a
    /// non-existent template. The runtime copy of compact.sqlite3 is patched;
    /// the canonical file is never touched.
    /// </summary>
    public static void ApplySeededDefect()
    {
        var detailId = QuerySqliteScalar<uint>(
            RuntimeSqlite,
            "SELECT qa.act_detail_id FROM quest_acts qa " +
            "JOIN quest_components qc ON qa.quest_component_id = qc.id " +
            "JOIN quest_contexts ctx ON qc.quest_context_id = ctx.id " +
            "WHERE ctx.id = 251 AND qa.act_detail_type = 'QuestActConReportNpc'");

        QuerySqliteExec(RuntimeSqlite,
            $"UPDATE quest_act_con_report_npcs SET npc_id = 999999 WHERE id = {detailId}");
    }

    public static bool SeededDefectActive()
        => QuerySqliteScalar<uint>(
               RuntimeSqlite,
               "SELECT qac.npc_id FROM quest_act_con_report_npcs qac " +
               "JOIN quest_acts qa ON qac.id = qa.act_detail_id " +
               "JOIN quest_components qc ON qa.quest_component_id = qc.id " +
               "JOIN quest_contexts ctx ON qc.quest_context_id = ctx.id " +
               "WHERE ctx.id = 251 AND qa.act_detail_type = 'QuestActConReportNpc'") == 999999;

    private static T QuerySqliteScalar<T>(string dbPath, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var raw = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(raw, typeof(T))!;
    }

    private static void QuerySqliteExec(string dbPath, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=OFF;" + sql; // defect injection crosses an FK to npcs()
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ MySQL

    public static MySqlConnection OpenDb(string database)
    {
        var conn = new MySqlConnection(
            $"Server={DbHost};Port={DbPort};User=root;Password={DbPassword};Database={database};Connection Timeout=15");
        conn.Open();
        return conn;
    }

    /// <summary>Removes all bot rows (characters + quest state) for the given
    /// account names — cycle teardown, not quest-state manipulation.</summary>
    public static void CleanupBotRows(params string[] accountNames)
    {
        using var conn = OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username IN (@names)))";
        cmd.Parameters.AddWithValue("@names", string.Join(",", accountNames.Select(n => $"'{n.Replace("'", "''")}'")));
        try { cmd.ExecuteNonQuery(); } catch { /* FK-tolerant */ }

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "DELETE FROM completed_quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username IN (@names)))";
        cmd2.Parameters.AddWithValue("@names", string.Join(",", accountNames.Select(n => $"'{n.Replace("'", "''")}'")));
        try { cmd2.ExecuteNonQuery(); } catch { }

        using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = "DELETE FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username IN (@names))";
        cmd3.Parameters.AddWithValue("@names", string.Join(",", accountNames.Select(n => $"'{n.Replace("'", "''")}'")));
        cmd3.ExecuteNonQuery();

        using var conn2 = OpenDb("aaemu_login");
        using var cmd4 = conn2.CreateCommand();
        cmd4.CommandText = "DELETE FROM users WHERE username IN (@names)";
        cmd4.Parameters.AddWithValue("@names", string.Join(",", accountNames.Select(n => $"'{n.Replace("'", "''")}'")));
        cmd4.ExecuteNonQuery();
    }

    /// <summary>Quests row dump for an account (restart-persistence evidence).
    /// The MySQL status column is TINYINT (numeric QuestStatus), so it is read
    /// as int — GetString on a tinyint throws SByte->String.</summary>
    public static List<(uint QuestId, int Status)> DumpQuestRows(string accountName)
    {
        using var conn = OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT q.template_id, q.status FROM quests q " +
            "JOIN characters c ON q.owner = c.id " +
            "JOIN aaemu_login.users u ON c.account_id = u.id " +
            "WHERE u.username = @name ORDER BY q.template_id";
        cmd.Parameters.AddWithValue("@name", accountName);

        var rows = new List<(uint, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetUInt32(0), reader.GetInt32(1)));
        return rows;
    }

    /// <summary>Inventory rows for a character id (restart-persistence
    /// evidence: items only reach MySQL via the periodic SaveManager tick —
    /// the disconnect save skips inventory).</summary>
    public static List<uint> DumpItemRows(uint characterId)
    {
        using var conn = OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id FROM items WHERE owner = @owner ORDER BY template_id";
        cmd.Parameters.AddWithValue("@owner", characterId);

        var rows = new List<uint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(reader.GetUInt32(0));
        return rows;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }
}
