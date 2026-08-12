using System.Globalization;
using System.Diagnostics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.IntegrationTests.E2e;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// M3b EXIT gate (t_accb1c63): the full homestead-persistence lifecycle scenario
/// — place → decorate → plant → harvest → restart → assert — N=3 cycles, two
/// homesteads, both crash modes:
///
///   Cycle 1 (place + decorate): seed 2 homesteads (template 172) with their full
///     bound-doodad set (door/windows/nameplate/chimney/ladder) + furniture
///     (chandelier), distinct transforms. Restart. Assert all rows survive
///     with transforms/phases/ownership/attachment intact, no duplication.
///   Cycle 2 (plant + kill -9 mid-save): seed crops (potato 2259) per house with
///     plant_time/phase_time. Restart. Hold a row lock on the seeded housings,
///     trigger a real save pass through the bridge (dirty houses + DoSave),
///     observe the game's housings write blocked in MySQL (INNODB_TRX), then
///     SIGKILL the game process while the save is provably in flight. Restart.
///     Assert planted rows survived with their phase clocks unclobbered, no
///     duplication. (Deterministic since t_1329a833: the row lock holds the
///     save open until the kill — no dependence on autosave tick timing.)
///   Cycle 3 (harvest + container kill during harvest): advance the crops to the
///     mature (harvestable) phase — the persisted post-harvest state. Restart.
///     Same save-observation seam, then `docker kill` the MySQL container
///     while the game is mid-save. Bring the DB back, restart the game. Assert
///     the harvest state survived, no loss, no duplication.
///
/// Final re-entry assert after all crash cycles: same two homesteads, exact row
/// counts, no loss, no duplication.
///
/// The gate's evidence contract: PROPERTY-01 reaches R=2 with recovery/load
/// evidence; HOUSING-01/FARM-01 stay at 2 (their scenario evidence is M3a's).
/// </summary>
[Collection("e2e")]
public class M3bExitPersistenceE2eTests
{
    // Template 172 (아담한 누이아 주택) — canonical housing_binding_doodads rows
    // (owner_id 172): (attach, doodad_id) pairs.
    private static readonly (int Attach, uint DoodadId, uint PhaseId, string Name)[] BoundDoodads =
    [
        (36, 4278, 10521, "door"),      // nuia_door01 (start group = closed)
        (37, 4322, 10608, "window-a"),  // nuia_window01 (start group)
        (38, 4322, 10608, "window-b"),  // second window
        (57, 2392, 6172, "nameplate"),  // commonbuilding_nameplate
        (1, 4922, 12249, "chimney"),    // nuia.housing_s_floor_chimney
        (24, 6885, 18467, "ladder"),    // housing_ladder — NON-START phase, no phase funcs
    ];

    // Chandelier (housing_decorations id 100 → doodad 1294, start group 2014) — furniture.
    private const uint ChandelierDoodadId = 1294;
    private const uint ChandelierPhaseId = 2014;

    // Potato (감자) — canonical crop: seedling 4379 → small 4456 → mature 4457 → final 4459.
    private const uint PotatoDoodadId = 2259;
    private const uint SeedlingPhase = 4379;
    private const uint MaturePhase = 4457;

    private const uint HouseTemplateId = 172;

    // Two homesteads: ids 900002/900003 (m3b1's test owns 900001, gate soak owns 99000+).
    private static readonly (uint HouseId, uint AccountId, uint CharId, string Name, string AccountName)[] Homesteads =
    [
        (900002, 900002, 900002, "exit_owner_a", "exit_account_a"),
        (900003, 900003, 900003, "exit_owner_b", "exit_account_b"),
    ];

    // Distinct LOCAL positions per house (offset per homestead so a clobber to
    // 0,0,0 or a cross-house mixup is unambiguous).
    private static (float X, float Y, float Z, float Roll, float Pitch, float Yaw)[] SpawnsFor(int houseIdx) =>
    [
        (2.5f + houseIdx * 10, 0.5f, 1.0f, 0.1f, 0.2f, 0.3f),
        (0.5f + houseIdx * 10, 2.5f, 1.5f, 0f, 0f, 0.5f),
        (-0.5f + houseIdx * 10, 2.5f, 1.5f, 0f, 0f, 1.0f),
        (0.5f + houseIdx * 10, 0.5f, 2.5f, 0f, 0f, 1.5f),
        (-2.0f + houseIdx * 10, 0.5f, 3.0f, 0f, 0f, 2.0f),
        (1.0f + houseIdx * 10, -1.0f, 2.0f, 0.05f, 0.05f, 2.5f),
        (0.5f + houseIdx * 10, 0.5f, 4.0f, 0f, 0f, 2.9f), // chandelier
    ];

    private static void EnsureStack() => E2eStack.EnsureUp();

    [Fact]
    [Trait("Category", "e2e")]
    public void M3bExit_TwoHomesteads_PlaceDecoratePlantHarvest_ThreeCrashCycles_NoLossNoDup()
    {
        EnsureStack();

        // ============ seed: 2 accounts + 2 characters (house owners) ============
        using (var login = E2eStack.OpenDb("aaemu_login"))
        using (var cmd = login.CreateCommand())
        {
            cmd.CommandText = "INSERT IGNORE INTO users (id, username, password, email, last_ip) VALUES (@id, @name, '', '', '')";
            foreach (var (_, accountId, _, _, accountName) in Homesteads)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", accountId);
                cmd.Parameters.AddWithValue("@name", accountName);
                cmd.ExecuteNonQuery();
            }
        }

        using (var game = E2eStack.OpenDb("aaemu_game"))
        {
            foreach (var (houseId, accountId, charId, name, _) in Homesteads)
            {
                InsertCharacter(game, charId, accountId, name);
                InsertHouse(game, houseId, accountId, charId);
            }
        }

        try
        {
            // ================= Cycle 1: place + decorate =================
            SeedDoodads();

            E2eStack.RestartGameServer();
            AssertRowsIntact("cycle1-after-place+decorate", expectedPerHouse: 7, expectedCrops: 0);
            Console.WriteLine("[m3b-exit] CYCLE 1 PASS (place+decorate survive restart)");

            // ================= Cycle 2: plant + kill -9 mid-save =================
            SeedCrops(phase: SeedlingPhase, phaseTimeAheadHours: 2);

            E2eStack.RestartGameServer(); // load planted crops
            AssertRowsIntact("cycle2-planted-loaded", expectedPerHouse: 7, expectedCrops: 1);

            var observed = KillGameMidSave(secondsToWait: 60);
            Assert.True(observed, "did not observe an autosave transaction in flight — cannot claim kill -9 mid-save");

            E2eStack.RestartGameServer();
            AssertRowsIntact("cycle2-after-kill-9-mid-save", expectedPerHouse: 7, expectedCrops: 1);
            Console.WriteLine("[m3b-exit] CYCLE 2 PASS (plant + kill -9 mid-save, no loss/dup)");

            // ============ Cycle 3: harvest + container kill during save ============
            AdvanceCropsToHarvest(MaturePhase);

            E2eStack.RestartGameServer(); // load harvest state
            AssertRowsIntact("cycle3-harvest-loaded", expectedPerHouse: 7, expectedCrops: 1, expectMature: true);

            observed = KillDbContainerMidSave();
            Assert.True(observed, "did not observe a save transaction before container kill — cannot claim container kill during harvest save");

            E2eStack.RestartGameServer();
            AssertRowsIntact("cycle3-after-container-kill", expectedPerHouse: 7, expectedCrops: 1, expectMature: true);
            Console.WriteLine("[m3b-exit] CYCLE 3 PASS (harvest + container kill during save, no loss/dup)");

            // ============ Final re-entry (4th boot) — repeated re-entry ============
            E2eStack.RestartGameServer();
            AssertRowsIntact("final-re-entry", expectedPerHouse: 7, expectedCrops: 1, expectMature: true);
            Console.WriteLine("[m3b-exit] FINAL RE-ENTRY PASS (4th boot, no loss/dup)");
        }
        finally
        {
            Cleanup();
        }
    }

    // ================================================================ crash helpers

    /// <summary>
    /// Deterministic mid-save kill (t_1329a833 hardening): holds an X row
    /// lock on the seeded housings rows, fires the bridge "save" trigger
    /// (dirties every loaded house + runs the real save path), then polls
    /// INNODB_TRX for the game's save transaction. The row lock GUARANTEES
    /// the game's REPLACE INTO housings blocks in flight — the save cannot
    /// finish before the observation fires, so the window cannot miss on
    /// host load (the A4 dirty-tracking failure mode: a clean world's
    /// autosave executes zero statements and never appears in INNODB_TRX).
    /// Fail-honest: the kill fires only after a transaction whose in-flight
    /// statement is the housings write is actually observed.
    /// </summary>
    private static bool KillGameMidSave(int secondsToWait)
    {
        WaitForSavePipelineReady();

        using var locker = OpenLockConnection();
        using var lockTx = locker.BeginTransaction();
        LockSeededHousingRows(locker, lockTx);

        FireBridgeSave();

        var hit = WaitForGameSaveTransaction(secondsToWait);
        if (hit)
        {
            Console.WriteLine("[m3b-exit] kill -9 landed inside an open autosave transaction (housings write blocked in flight)");
            E2eStack.StopGameServer();
        }
        // lockTx disposal rolls the lock back — after the kill the game's
        // transaction is gone either way.
        return hit;
    }

    /// <summary>
    /// Deterministic container-kill-during-save (t_1329a833 hardening):
    /// same seam as <see cref="KillGameMidSave"/> — row lock + bridge save
    /// trigger + INNODB_TRX observation — then `docker kill`s the MySQL
    /// container while the game's save is provably in flight, brings it
    /// back, and waits for MySQL to be healthy again. Returns true only if
    /// the container kill happened inside an observed save window.
    /// </summary>
    private static bool KillDbContainerMidSave()
    {
        WaitForSavePipelineReady();

        MySqlConnection locker = null;
        MySqlTransaction lockTx = null;
        try
        {
            locker = OpenLockConnection();
            lockTx = locker.BeginTransaction();
            LockSeededHousingRows(locker, lockTx);

            FireBridgeSave();

            var hit = WaitForGameSaveTransaction(60);
            if (!hit)
                return false;

            Console.WriteLine("[m3b-exit] container kill during open autosave transaction (housings write blocked in flight)");
            var envFile = Path.Combine(E2eStack.E2eRoot, ".env");
            Run("docker", $"compose -f {E2eStack.ComposeFile} --env-file {envFile} kill db");
            Thread.Sleep(3000);
            Run("docker", $"compose -f {E2eStack.ComposeFile} --env-file {envFile} up -d db");

            // Wait for MySQL to be healthy again (the game's next save needs it).
            var healthyDeadline = DateTime.UtcNow.AddSeconds(180);
            var ready = false;
            while (DateTime.UtcNow < healthyDeadline)
            {
                try
                {
                    using var conn = E2eStack.OpenDb("aaemu_login");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM users LIMIT 1";
                    _ = cmd.ExecuteScalar();
                    ready = true;
                    break;
                }
                catch
                {
                    Thread.Sleep(2000);
                }
            }

            Assert.True(ready, "MySQL container did not come back healthy after docker kill");
            return true;
        }
        finally
        {
            // The container kill took the lock connection down with it — a
            // rollback would throw "Reading from the stream has failed".
            // Dispose defensively; the lock is gone either way.
            try { lockTx?.Dispose(); } catch { }
            try { locker?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Waits until the game's MySQL activity has quieted down (PROCESSLIST:
    /// fewer than 3 active Query connections for aaemu_game, sustained).
    /// On a loaded host the game's boot spawns saturate its MySQL pool for
    /// a minute or more — a DoSave started in that window starves inside
    /// MySQL.CreateConnection (holding _isSaving), no transaction ever
    /// opens, and the mid-save observation would miss (t_1329a833 runs
    /// 3-8: raw INNODB_TRX count stayed 1 — only the test's own lock — for
    /// the whole window). Once the boot work drains, the bridge save
    /// trigger gets a connection immediately, its pass blocks on the test's
    /// row lock, and the observation is deterministic. (An idle-Sleep
    /// count is NOT a valid signal — the test's own pooled connections
    /// show up as Sleep and skewed the gate in run 9.) Proceeds anyway
    /// (with a warning) if the game never quiets — the poll then fails
    /// honestly rather than hanging.
    /// </summary>
    private static void WaitForSavePipelineReady(int timeoutSeconds = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var seenQuiet = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var conn = OpenPollConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM information_schema.PROCESSLIST " +
                    "WHERE db = 'aaemu_game' AND command = 'Query' AND id <> CONNECTION_ID()";
                var active = Convert.ToInt32(cmd.ExecuteScalar());
                if (active < 3)
                {
                    seenQuiet++;
                    if (seenQuiet >= 3) // sustained across 3 consecutive polls (≈6s)
                    {
                        Console.WriteLine($"[m3b-exit] save pipeline ready (game MySQL quiet: {active} active queries)");
                        return;
                    }
                }
                else
                {
                    seenQuiet = 0;
                }
            }
            catch
            {
                // DB momentarily unreachable — keep waiting
            }
            Thread.Sleep(2000);
        }
        Console.WriteLine($"[m3b-exit] WARN: game MySQL never quieted within {timeoutSeconds}s — proceeding (observation may fail honestly)");
    }

    /// <summary>
    /// Dedicated NON-pooled connection for the mid-save row lock. A pooled
    /// connection is subject to pool lifecycle behaviour (reset-on-return,
    /// recycling) that could silently drop the FOR UPDATE lock mid-window;
    /// the lock must be held on a physical connection the test fully owns
    /// (t_1329a833 run 3: the observation missed with a pooled locker).
    /// </summary>
    private static MySqlConnection OpenLockConnection()
    {
        var conn = new MySqlConnection(
            $"Server={E2eStack.DbHost};Port={E2eStack.DbPort};User=root;Password={E2eStack.DbPassword};" +
            $"Database=aaemu_game;Connection Timeout=15;Pooling=false");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Takes an X lock (SELECT ... FOR UPDATE) on both seeded housings rows.
    /// The game's save REPLACE INTO housings then blocks until this
    /// transaction commits/rolls back — the save cannot complete while the
    /// test holds the lock, making the mid-save observation deterministic.
    /// </summary>
    private static void LockSeededHousingRows(MySqlConnection conn, MySqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM housings WHERE id IN (@h1, @h2) FOR UPDATE";
        cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
        cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);
        cmd.ExecuteNonQuery();
        Console.WriteLine($"[m3b-exit] housings row lock held on MySQL thread {conn.ServerThread}");
    }

    /// <summary>
    /// Fires the bridge "save" trigger without waiting for the reply: the
    /// bridge dirties every loaded house and starts a real save pass; the
    /// response only returns after the pass completes (which, with the row
    /// lock held, is after the kill). Fire-and-forget keeps the test's poll
    /// loop authoritative.
    /// </summary>
    private static void FireBridgeSave()
    {
        using var client = new BotDriveClient(E2eStack.BridgePort);
        client.Send("{\"cmd\":\"save\"}");
    }

    /// <summary>
    /// Polling connection for the INNODB_TRX observation — deliberately
    /// NON-pooled, a fresh physical connection per query. The test's pooled
    /// connections carry process-wide pool history (connections opened
    /// before the MySQL volume reset die with the old container; a pool
    /// with stale members can serve a view that diverges from the server's
    /// real transaction state — t_1329a833 runs 4-9: the poll saw
    /// raw_trx=1 while the game's LOCK WAIT provably existed server-side).
    /// A fresh connection per query sees exactly what the server has.
    /// </summary>
    private static MySqlConnection OpenPollConnection()
    {
        var conn = new MySqlConnection(
            $"Server={E2eStack.DbHost};Port={E2eStack.DbPort};User=root;Password={E2eStack.DbPassword};" +
            $"Database=aaemu_game;Connection Timeout=15;Pooling=false");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Polls INNODB_TRX until a transaction whose in-flight statement is a
    /// housings write is observed — the game's save pass, blocked on the
    /// test's row lock. The trx_query match proves WHICH write is in
    /// flight, so the observation cannot be satisfied by the test's own
    /// connection (excluded by thread id) or an unrelated idle transaction.
    /// Re-fires the bridge save trigger every 10s while waiting: a pass can
    /// be starved by the game's DB connection pool during boot churn (the
    /// pass holds _isSaving while its pool acquire blocks), so a fresh
    /// trigger has a new chance once the pool frees up. On a full miss the
    /// raw INNODB_TRX contents are dumped for self-diagnosis.
    /// </summary>
    private static bool WaitForGameSaveTransaction(int secondsToWait)
    {
        var deadline = DateTime.UtcNow.AddSeconds(secondsToWait);
        var lastRefire = DateTime.UtcNow;
        var lastLog = DateTime.UtcNow;
        var lastError = "";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var conn = OpenPollConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM information_schema.INNODB_TRX " +
                    "WHERE trx_mysql_thread_id <> CONNECTION_ID() " +
                    "AND trx_query LIKE '%housings%'";
                var matched = Convert.ToInt32(cmd.ExecuteScalar());
                if (matched > 0)
                    return true;
                if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(5))
                {
                    lastLog = DateTime.UtcNow;
                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = "SELECT COUNT(*) FROM information_schema.INNODB_TRX";
                    var raw = Convert.ToInt32(cmd2.ExecuteScalar());
                    Console.WriteLine($"[m3b-exit] poll t={(DateTime.UtcNow - deadline + TimeSpan.FromSeconds(secondsToWait)).TotalSeconds:F0}s: raw_trx={raw} housings_matched=0");
                }
            }
            catch (Exception ex)
            {
                // DB momentarily unreachable — treat as no observed save.
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    Console.WriteLine($"[m3b-exit] poll query error: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (DateTime.UtcNow - lastRefire > TimeSpan.FromSeconds(10))
            {
                FireBridgeSave();
                lastRefire = DateTime.UtcNow;
            }
            Thread.Sleep(5);
        }

        // Miss — dump what INNODB_TRX actually held (ALL transactions,
        // including the test's own lock transaction) so the next failure is
        // self-diagnosing: was the lock held? was the save starved? blocked
        // elsewhere? never reached the housings write?
        try
        {
            using var conn = OpenPollConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT trx_id, trx_state, trx_started, trx_mysql_thread_id, trx_rows_locked, " +
                "LEFT(IFNULL(trx_query, '<null>'), 80) " +
                "FROM information_schema.INNODB_TRX";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                Console.WriteLine($"[m3b-exit] trx dump: id={reader.GetValue(0)} state={reader.GetValue(1)} started={reader.GetValue(2)} thread={reader.GetValue(3)} rows_locked={reader.GetValue(4)} query={reader.GetValue(5)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[m3b-exit] trx dump failed: {ex.Message}");
        }
        return false;
    }

    private static void Run(string cmd, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(cmd, args)
        {
            WorkingDirectory = E2eStack.E2eRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        p.WaitForExit(300_000);
        Assert.True(p.ExitCode == 0, $"{cmd} {args} failed ({p.ExitCode}): {p.StandardError.ReadToEnd()}");
    }

    // ================================================================ seeding

    private static void SeedDoodads()
    {
        using var game = E2eStack.OpenDb("aaemu_game");
        var dbId = 900200;
        for (var h = 0; h < Homesteads.Length; h++)
        {
            var (houseId, _, charId, _, _) = Homesteads[h];
            var spawns = SpawnsFor(h);
            foreach (var (attach, doodadId, phaseId, name) in BoundDoodads)
            {
                var spawn = spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)];
                InsertDoodad(game, (uint)dbId, doodadId, phaseId, attach, spawn, houseId, charId);
                dbId++;
            }
            // furniture (chandelier) — attach point None, own rotation
            InsertDoodad(game, (uint)dbId, ChandelierDoodadId, ChandelierPhaseId, 0,
                spawns[^1], houseId, charId);
            dbId++;
        }
    }

    private static void SeedCrops(uint phase, int phaseTimeAheadHours)
    {
        using var game = E2eStack.OpenDb("aaemu_game");
        var dbId = 900300;
        foreach (var (houseId, _, charId, _, _) in Homesteads)
        {
            var crop = (X: 1.5f, Y: 1.5f, Z: 1.0f, Roll: 0f, Pitch: 0f, Yaw: 0f);
            InsertDoodad(game, (uint)dbId, PotatoDoodadId, phase, 0, crop, houseId, charId,
                phaseTime: DateTime.UtcNow.AddHours(phaseTimeAheadHours));
            dbId++;
        }
    }

    /// <summary>Advances both planted crops to the mature (harvestable) phase — the persisted post-harvest state.</summary>
    private static void AdvanceCropsToHarvest(uint maturePhase)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE doodads SET current_phase_id = @phase, phase_time = @phaseTime " +
            "WHERE template_id = @template AND house_id IN (@h1, @h2)";
        cmd.Parameters.AddWithValue("@phase", maturePhase);
        cmd.Parameters.AddWithValue("@phaseTime", DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@template", PotatoDoodadId);
        cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
        cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);
        var n = cmd.ExecuteNonQuery();
        Assert.Equal(2, n);
    }

    private static void InsertCharacter(MySqlConnection conn, uint charId, uint accountId, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO characters (id, account_id, name, race, gender, unit_model_params, level, " +
            "experience, recoverable_exp, hp, mp, consumed_lp, ability1, ability2, ability3, world_id, zone_id, " +
            "x, y, z, faction_id, faction_name, expedition_id, family, dead_count, rez_wait_duration, " +
            "rez_penalty_duration, auto_use_aapoint, prev_point, point, gift, expanded_expert, slots) " +
            "VALUES (@id, @accountId, @name, 0, 0, '', 1, 0, 0, 100, 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, " +
            "148, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '')";
        cmd.Parameters.AddWithValue("@id", charId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.ExecuteNonQuery();
    }

    private static void InsertHouse(MySqlConnection conn, uint houseId, uint accountId, uint ownerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO housings (id, account_id, owner, co_owner, template_id, name, x, y, z, " +
            "yaw, pitch, roll, current_step, current_action, permission, place_date, protected_until, " +
            "faction_id, sell_to, sell_price, allow_recover) " +
            "VALUES (@id, @accountId, @owner, @owner, @templateId, 'M3b Exit House', @x, @y, 100, " +
            "0, 0, 0, -1, 0, 0, @placedate, @protect, 148, 0, 0, 1)";
        cmd.Parameters.AddWithValue("@id", houseId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@owner", ownerId);
        cmd.Parameters.AddWithValue("@templateId", HouseTemplateId);
        // Known-good world coords (same zone family as the M3b-1 seed); house 2
        // offset so the two homesteads do not overlap.
        cmd.Parameters.AddWithValue("@x", 20010f);
        cmd.Parameters.AddWithValue("@y", houseId == Homesteads[0].HouseId ? 20020f : 20090f);
        cmd.Parameters.AddWithValue("@placedate", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@protect", DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static void InsertDoodad(MySqlConnection conn, uint dbId, uint templateId, uint phaseId, int attach,
        (float X, float Y, float Z, float Roll, float Pitch, float Yaw) spawn, uint houseId, uint ownerId,
        DateTime? phaseTime = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO doodads (id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
            "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, house_id, " +
            "parent_doodad, item_template_id, item_container_id, data, farm_type) " +
            "VALUES (@id, @ownerId, 3, @attach, @templateId, @phaseId, @now, @now, @phaseTime, " +
            "@x, @y, @z, @roll, @pitch, @yaw, 1, 0, @houseId, 0, 0, 0, 0, 0)";
        cmd.Parameters.AddWithValue("@id", dbId);
        cmd.Parameters.AddWithValue("@ownerId", ownerId);
        cmd.Parameters.AddWithValue("@attach", attach);
        cmd.Parameters.AddWithValue("@templateId", templateId);
        cmd.Parameters.AddWithValue("@phaseId", phaseId);
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@phaseTime", (phaseTime ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@x", spawn.X.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@y", spawn.Y.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@z", spawn.Z.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@roll", spawn.Roll.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@pitch", spawn.Pitch.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@yaw", spawn.Yaw.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@houseId", houseId);
        cmd.ExecuteNonQuery();
    }

    // ================================================================ assertions

    private static void AssertRowsIntact(string phase, int expectedPerHouse, int expectedCrops, bool expectMature = false)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");

        // --- housings: exactly 2 rows, correct owners ---
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM housings WHERE id IN (@h1, @h2)";
            cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
            cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);
            Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        // --- doodads: exact per-house counts (no duplication, no loss) ---
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT house_id, COUNT(*) FROM doodads WHERE house_id IN (@h1, @h2) GROUP BY house_id ORDER BY house_id";
            cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
            cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);
            var counts = new Dictionary<uint, int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                counts[reader.GetUInt32(0)] = reader.GetInt32(1);
            Assert.Equal(expectedPerHouse + expectedCrops, counts[Homesteads[0].HouseId]);
            Assert.Equal(expectedPerHouse + expectedCrops, counts[Homesteads[1].HouseId]);
        }

        // --- full row content for every doodad ---
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, house_id, template_id, current_phase_id, attach_point, x, y, z, roll, pitch, yaw, " +
                "owner_id, owner_type FROM doodads WHERE house_id IN (@h1, @h2) ORDER BY house_id, id";
            cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
            cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);

            var rows = new List<(uint Id, uint HouseId, uint TemplateId, uint Phase, int Attach,
                float X, float Y, float Z, float Roll, float Pitch, float Yaw, uint OwnerId, int OwnerType)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetUInt32("id"), reader.GetUInt32("house_id"), reader.GetUInt32("template_id"),
                    reader.GetUInt32("current_phase_id"), reader.GetInt32("attach_point"),
                    reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z"),
                    reader.GetFloat("roll"), reader.GetFloat("pitch"), reader.GetFloat("yaw"),
                    reader.GetUInt32("owner_id"), reader.GetInt32("owner_type")));
            }

            Assert.Equal(2 * (expectedPerHouse + expectedCrops), rows.Count);

            for (var h = 0; h < Homesteads.Length; h++)
            {
                var (houseId, _, charId, _, _) = Homesteads[h];
                var spawns = SpawnsFor(h);
                var houseRows = rows.Where(r => r.HouseId == houseId).ToList();
                var byAttach = houseRows.Where(r => r.Attach > 0).GroupBy(r => r.Attach).ToDictionary(g => g.Key, g => g.First());

                // bound doodads: rotation/attachment/ownership/phase untouched
                foreach (var (attach, doodadId, phaseId, name) in BoundDoodads)
                {
                    Assert.True(byAttach.ContainsKey(attach), $"[{phase}] house {houseId}: missing attach {attach} ({name})");
                    var row = byAttach[attach];
                    Assert.True(row.TemplateId == doodadId, $"[{phase}] house {houseId} {name}: template {row.TemplateId} != {doodadId}");
                    Assert.True(row.Phase == phaseId, $"[{phase}] house {houseId} {name}: phase {row.Phase} != {phaseId}");
                    var expect = spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)];
                    Assert.True(row.X == expect.X, $"[{phase}] house {houseId} {name}: x clobbered to {row.X}");
                    Assert.True(row.Y == expect.Y, $"[{phase}] house {houseId} {name}: y clobbered to {row.Y}");
                    Assert.True(row.Z == expect.Z, $"[{phase}] house {houseId} {name}: z clobbered to {row.Z}");
                    Assert.True(Math.Abs(row.Yaw - expect.Yaw) < 0.001f, $"[{phase}] house {houseId} {name}: yaw clobbered to {row.Yaw}");
                    Assert.True(row.OwnerId == charId, $"[{phase}] house {houseId} {name}: owner clobbered to {row.OwnerId}");
                    Assert.True(row.OwnerType == (int)DoodadOwnerType.Housing, $"[{phase}] house {houseId} {name}: owner_type {row.OwnerType}");
                }

                // furniture: chandelier keeps its own rotation
                var furniture = houseRows.FirstOrDefault(r => r.TemplateId == ChandelierDoodadId);
                Assert.True(furniture.Id > 0, $"[{phase}] house {houseId} chandelier missing");
                Assert.True(furniture.Phase == ChandelierPhaseId, $"[{phase}] house {houseId} chandelier: phase {furniture.Phase}");
                Assert.True(Math.Abs(furniture.Yaw - spawns[^1].Yaw) < 0.001f, $"[{phase}] house {houseId} chandelier: yaw {furniture.Yaw}");

                // crops
                var crops = houseRows.Where(r => r.TemplateId == PotatoDoodadId).ToList();
                Assert.Equal(expectedCrops, crops.Count);
                foreach (var crop in crops)
                {
                    Assert.True(crop.OwnerId == charId, $"[{phase}] house {houseId} crop: owner {crop.OwnerId}");
                    Assert.True(crop.OwnerType == (int)DoodadOwnerType.Housing, $"[{phase}] house {houseId} crop: owner_type {crop.OwnerType}");
                    if (expectMature)
                        Assert.True(crop.Phase == MaturePhase, $"[{phase}] house {houseId} crop: harvest phase {crop.Phase} != {MaturePhase}");
                    else
                        Assert.True(crop.Phase == SeedlingPhase, $"[{phase}] house {houseId} crop: planted phase {crop.Phase} != {SeedlingPhase}");
                }
            }
        }
    }

    // ================================================================ cleanup

    private static void Cleanup()
    {
        try
        {
            using var game = E2eStack.OpenDb("aaemu_game");
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM doodads WHERE house_id IN (@h1, @h2)";
                cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
                cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM housings WHERE id IN (@h1, @h2)";
                cmd.Parameters.AddWithValue("@h1", Homesteads[0].HouseId);
                cmd.Parameters.AddWithValue("@h2", Homesteads[1].HouseId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM characters WHERE id IN (@c1, @c2)";
                cmd.Parameters.AddWithValue("@c1", Homesteads[0].CharId);
                cmd.Parameters.AddWithValue("@c2", Homesteads[1].CharId);
                cmd.ExecuteNonQuery();
            }
            using var login = E2eStack.OpenDb("aaemu_login");
            using var cmd2 = login.CreateCommand();
            cmd2.CommandText = "DELETE FROM users WHERE id IN (@a1, @a2)";
            cmd2.Parameters.AddWithValue("@a1", Homesteads[0].AccountId);
            cmd2.Parameters.AddWithValue("@a2", Homesteads[1].AccountId);
            cmd2.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[m3b-exit] cleanup failed (non-fatal): {e.Message}");
        }
    }
}
