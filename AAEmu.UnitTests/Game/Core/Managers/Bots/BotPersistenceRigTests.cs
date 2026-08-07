using System.Text.RegularExpressions;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Utils.Mocks;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// P1 slice #7 rig (t_afbce6a0) — playerbot_* metadata schema + IBotPersistence
/// dirty-flush.
///
/// Proves the success signal: metadata persists on deactivate/shutdown, NO
/// per-step DB writes (write-count assertions against a recording in-memory
/// DB), additive schema migrates clean (live MySQL via Testcontainers when
/// docker is present; static additive checks always).
/// </summary>
[NotInParallel]
public class BotPersistenceRigTests
{
    private const uint BotA = 1001;
    private const uint BotB = 1002;

    private static BotPersistenceManager NewManager(out BotPersistenceDbMock db)
    {
        var mock = new BotPersistenceDbMock();
        db = mock;
        return new BotPersistenceManager(() => mock);
    }

    // ------------------------------------------------------------------
    // No per-step writes
    // ------------------------------------------------------------------

    [Test]
    public async Task AiStepMarks_NoFlush_ZeroDbWrites()
    {
        var manager = NewManager(out var db);

        // Simulate 50 AI steps mutating bot metadata (the forbidden pattern:
        // per-step writes). Marks are cheap in-memory ops only.
        for (var step = 0; step < 50; step++)
        {
            manager.MarkDirty(BotA, BotMetadataDomain.Profile);
            manager.MarkDirty(BotA, BotMetadataDomain.Activity);
            manager.MarkDirty(BotB, step % 2 == 0 ? BotMetadataDomain.Home : BotMetadataDomain.MemoryFlags);
        }

        await Assert.That(db.WriteCount).IsEqualTo(0);
        await Assert.That(db.Statements).IsEmpty();
        await Assert.That(db.InTransaction).IsFalse();
        await Assert.That(manager.IsDirty(BotA)).IsTrue();
        await Assert.That(manager.IsDirty(BotB)).IsTrue();
        await Assert.That(manager.DirtyRecordCount).IsEqualTo(2);
        await Assert.That(manager.RegisteredCount).IsEqualTo(2);
    }

    // ------------------------------------------------------------------
    // Batched periodic flush: dirty-only, idempotent
    // ------------------------------------------------------------------

    [Test]
    public async Task FlushAll_WritesOnlyDirtyDomains_BatchedAndIdempotent()
    {
        var manager = NewManager(out var db);

        manager.MarkDirty(BotA, BotMetadataDomain.Profile | BotMetadataDomain.Home);
        manager.MarkDirty(BotB, BotMetadataDomain.Profile);

        var written = await manager.FlushAllAsync(BotFlushReason.Periodic);

        // 3 statements: profile + home (bot A), profile (bot B). One transaction.
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(db.WriteCount).IsEqualTo(3);
        await Assert.That(db.InTransaction).IsFalse();
        await Assert.That(db.Tables["playerbot_profile"].Count).IsEqualTo(2);
        await Assert.That(db.Tables["playerbot_home"].Count).IsEqualTo(1);
        await Assert.That(db.Tables.ContainsKey("playerbot_activity")).IsFalse();

        // Second flush of clean records: zero additional writes.
        var second = await manager.FlushAllAsync(BotFlushReason.Periodic);
        await Assert.That(second).IsEqualTo(0);
        await Assert.That(db.WriteCount).IsEqualTo(3);

        // Only the periodic cycle counts as a flush.
        await Assert.That(manager.TotalFlushCycles).IsEqualTo(1);
    }

    // ------------------------------------------------------------------
    // Metadata round-trip (all six domains)
    // ------------------------------------------------------------------

    [Test]
    public async Task RoundTrip_FlushThenRestore_AllDomainsEqual()
    {
        var manager = NewManager(out var db);

        var record = manager.GetOrCreate(BotA, accountId: 7);

        record.Profile.Fidelity = BotFidelity.Reduced;
        record.Profile.BehaviorProfile = "roam";
        record.Profile.ScheduleEnabled = false;
        record.Profile.LastSeenUtc = new DateTime(2026, 8, 7, 12, 30, 0, DateTimeKind.Utc);

        record.Schedule.Add(new BotScheduleEntry
        {
            CharacterId = BotA,
            DayMask = 0b0101_1111,
            StartTime = new TimeSpan(6, 0, 0),
            EndTime = new TimeSpan(12, 0, 0),
            ActivityType = "questdrive",
            Params = "{\"route\":\"marianople\"}",
            Enabled = true
        });
        record.Schedule.Add(new BotScheduleEntry
        {
            CharacterId = BotA,
            DayMask = 0b0010_0000,
            StartTime = new TimeSpan(18, 30, 0),
            EndTime = new TimeSpan(21, 0, 0),
            ActivityType = "traderun",
            Params = null,
            Enabled = false
        });

        record.Activity.ActivityType = "questdrive";
        record.Activity.State = BotActivityState.Running;
        record.Activity.StartedAtUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        record.Activity.EndedAtUtc = DateTime.MinValue;
        record.Activity.Cycles = 3;
        record.Activity.FailureCount = 1;
        record.Activity.LastError = "nav timeout";

        record.Home.WorldId = 2;
        record.Home.ZoneId = 5;
        record.Home.X = 12.5f;
        record.Home.Y = -3.25f;
        record.Home.Z = 0.75f;
        record.Home.Yaw = 1.5f;
        record.Home.ReturnOnCombatExit = false;

        record.MemoryFlags.Flags = 0b1010UL;

        record.PopulationState.Fidelity = BotFidelity.Full;
        record.PopulationState.PressureState = BotPressureState.High;
        record.PopulationState.LastTransitionAtUtc = new DateTime(2026, 8, 7, 11, 59, 0, DateTimeKind.Utc);
        record.PopulationState.TransitionCount = 9;

        record.MarkAll();

        var written = await manager.FlushAllAsync(BotFlushReason.Periodic);
        // profile(1) + schedule(delete 1 + 2 inserts) + activity(1) + home(1)
        // + memory_flags(1) + population_state(1) = 8 statements.
        await Assert.That(written).IsEqualTo(8);
        await Assert.That(manager.IsDirty(BotA)).IsFalse();

        var restored = await manager.RestoreAsync(BotA);

        await Assert.That(restored.Profile.AccountId).IsEqualTo(7u);
        await Assert.That(restored.Profile.Fidelity).IsEqualTo(BotFidelity.Reduced);
        await Assert.That(restored.Profile.BehaviorProfile).IsEqualTo("roam");
        await Assert.That(restored.Profile.ScheduleEnabled).IsFalse();
        await Assert.That(restored.Profile.LastSeenUtc).IsEqualTo(record.Profile.LastSeenUtc);
        await Assert.That(restored.Profile.CreatedAtUtc).IsEqualTo(record.Profile.CreatedAtUtc);

        await Assert.That(restored.Schedule.Count).IsEqualTo(2);
        await Assert.That(restored.Schedule[0].Id).IsNotEqualTo(0L); // DB-assigned
        await Assert.That(restored.Schedule[0].DayMask).IsEqualTo((byte)0b0101_1111);
        await Assert.That(restored.Schedule[0].StartTime).IsEqualTo(new TimeSpan(6, 0, 0));
        await Assert.That(restored.Schedule[0].EndTime).IsEqualTo(new TimeSpan(12, 0, 0));
        await Assert.That(restored.Schedule[0].ActivityType).IsEqualTo("questdrive");
        await Assert.That(restored.Schedule[0].Params).IsEqualTo("{\"route\":\"marianople\"}");
        await Assert.That(restored.Schedule[0].Enabled).IsTrue();
        await Assert.That(restored.Schedule[1].ActivityType).IsEqualTo("traderun");
        await Assert.That(restored.Schedule[1].Params).IsNull();
        await Assert.That(restored.Schedule[1].Enabled).IsFalse();

        await Assert.That(restored.Activity.ActivityType).IsEqualTo("questdrive");
        await Assert.That(restored.Activity.State).IsEqualTo(BotActivityState.Running);
        await Assert.That(restored.Activity.Cycles).IsEqualTo(3u);
        await Assert.That(restored.Activity.FailureCount).IsEqualTo(1u);
        await Assert.That(restored.Activity.LastError).IsEqualTo("nav timeout");

        await Assert.That(restored.Home.WorldId).IsEqualTo(2u);
        await Assert.That(restored.Home.ZoneId).IsEqualTo(5u);
        await Assert.That(restored.Home.X).IsEqualTo(12.5f);
        await Assert.That(restored.Home.Y).IsEqualTo(-3.25f);
        await Assert.That(restored.Home.Z).IsEqualTo(0.75f);
        await Assert.That(restored.Home.Yaw).IsEqualTo(1.5f);
        await Assert.That(restored.Home.ReturnOnCombatExit).IsFalse();

        await Assert.That(restored.MemoryFlags.Flags).IsEqualTo(0b1010UL);

        await Assert.That(restored.PopulationState.Fidelity).IsEqualTo(BotFidelity.Full);
        await Assert.That(restored.PopulationState.PressureState).IsEqualTo(BotPressureState.High);
        await Assert.That(restored.PopulationState.TransitionCount).IsEqualTo(9u);
    }

    // ------------------------------------------------------------------
    // Mandatory flush: deactivate (immediate + targeted)
    // ------------------------------------------------------------------

    [Test]
    public async Task DeactivateFlush_ImmediateAndTargeted_LeavesOtherBotsDirty()
    {
        var manager = NewManager(out var db);

        manager.MarkDirty(BotA, BotMetadataDomain.Profile | BotMetadataDomain.Activity);
        manager.MarkDirty(BotB, BotMetadataDomain.Home);

        // Deactivation MUST persist immediately — not batched, not waiting for the timer.
        var written = await manager.FlushAsync(BotA, BotFlushReason.Deactivate);

        await Assert.That(written).IsEqualTo(2);
        await Assert.That(db.WriteCount).IsEqualTo(2);
        await Assert.That(manager.IsDirty(BotA)).IsFalse();
        await Assert.That(manager.IsDirty(BotB)).IsTrue(); // untouched bot stays dirty

        // The remaining dirty bot is picked up by the next periodic flush.
        var periodic = await manager.FlushAllAsync(BotFlushReason.Periodic);
        await Assert.That(periodic).IsEqualTo(1);
        await Assert.That(db.WriteCount).IsEqualTo(3);
        await Assert.That(manager.IsDirty(BotB)).IsFalse();
    }

    // ------------------------------------------------------------------
    // Mandatory flush: shutdown
    // ------------------------------------------------------------------

    [Test]
    public async Task ShutdownFlush_Mandatory_AllPendingPersisted()
    {
        var manager = NewManager(out var db);

        manager.MarkDirty(BotA, BotMetadataDomain.Profile);
        manager.MarkDirty(BotB, BotMetadataDomain.Home | BotMetadataDomain.Schedule);

        await manager.ShutdownAsync();

        await Assert.That(db.WriteCount).IsEqualTo(3); // profile + home + schedule delete
        await Assert.That(manager.DirtyRecordCount).IsEqualTo(0);
        await Assert.That(manager.IsDirty(BotA)).IsFalse();
        await Assert.That(manager.IsDirty(BotB)).IsFalse();

        // Idempotent: a second shutdown flush writes nothing new.
        var after = await manager.FlushAllAsync(BotFlushReason.Periodic);
        await Assert.That(after).IsEqualTo(0);
        await Assert.That(db.WriteCount).IsEqualTo(3);
    }

    // ------------------------------------------------------------------
    // Periodic batching (real timer)
    // ------------------------------------------------------------------

    [Test]
    public async Task PeriodicTimer_FlushesDirtyAfterInterval()
    {
        var manager = NewManager(out var db);
        manager.Initialize(TimeSpan.FromMilliseconds(150));

        manager.MarkDirty(BotA, BotMetadataDomain.Profile);

        // Poll for the timer-driven flush (generous bound to stay hermetic).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (db.WriteCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        await Assert.That(db.WriteCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(manager.IsDirty(BotA)).IsFalse();

        await manager.ShutdownAsync(); // stop the timer
    }

    // ------------------------------------------------------------------
    // Migration: additive schema
    // ------------------------------------------------------------------

    private const string MigrationFileName = "2026-08-07-playerbot-metadata.sql";

    private static readonly string[] s_tableNames =
    [
        "playerbot_profile",
        "playerbot_schedule",
        "playerbot_activity",
        "playerbot_home",
        "playerbot_memory_flags",
        "playerbot_population_state"
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    private static string? ResolveMigrationFile()
    {
        var path = Path.Combine(RepoRoot(), "SQL", "patches", MigrationFileName);
        return File.Exists(path) ? path : null;
    }

    [Test]
    public async Task Migration_DefinesAllSixTables_AdditiveOnly()
    {
        var patchPath = ResolveMigrationFile();
        Skip.Unless(patchPath != null, $"Migration {MigrationFileName} not found in repo checkout.");

        var sql = await File.ReadAllTextAsync(patchPath);

        foreach (var table in s_tableNames)
        {
            var createCount = CountOccurrences(sql, $"CREATE TABLE IF NOT EXISTS `{table}`");
            await Assert.That(createCount).IsEqualTo(1,
                $"{table} must be created exactly once, additively (CREATE TABLE IF NOT EXISTS)");
        }

        // Additive-only: no ALTER, no DROP, no FK constraints in the migration.
        await Assert.That(sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(sql.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)).IsFalse();

        // Key columns present on the right tables (whitespace-tolerant —
        // column padding is cosmetic and may change).
        await Assert.That(sql.Contains("PRIMARY KEY (`character_id`) USING BTREE", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Regex.IsMatch(sql, @"`fidelity`\s+tinyint NOT NULL DEFAULT 0", RegexOptions.IgnoreCase)).IsTrue();
        await Assert.That(Regex.IsMatch(sql, @"`pressure_state`\s+tinyint NOT NULL DEFAULT 0", RegexOptions.IgnoreCase)).IsTrue();
        await Assert.That(sql.Contains("KEY `idx_playerbot_schedule_character`", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sql.Contains("`return_on_combat_exit`", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Regex.IsMatch(sql, @"`flags`\s+bigint unsigned NOT NULL DEFAULT 0", RegexOptions.IgnoreCase)).IsTrue();
    }

    // ------------------------------------------------------------------
    // Migration: applies clean on live MySQL (docker-gated)
    // ------------------------------------------------------------------

    [Test]
    public async Task Migration_AppliesCleanly_LiveMySql()
    {
        var dockerSocket = OperatingSystem.IsLinux() ? "/var/run/docker.sock" : null;
        Skip.Unless(dockerSocket != null && File.Exists(dockerSocket),
            "Docker unavailable — live MySQL migration apply skipped (static additive checks still ran).");

        var patchPath = ResolveMigrationFile();
        Skip.Unless(patchPath != null, $"Migration {MigrationFileName} not found in repo checkout.");

        await using var container = new MySqlBuilder()
            .WithImage("mysql:8.0.36")
            .WithDatabase($"rig_{Guid.NewGuid():N}")
            .WithUsername("rig")
            .WithPassword("rig")
            .Build();
        await container.StartAsync();

        try
        {
            using (var connection = new MySqlConnection(container.GetConnectionString()))
            {
                await connection.OpenAsync();

                // Execute the exact shipped file, statement by statement.
                // Strip comment lines, then split on ';' (CREATE TABLEs are
                // multi-line, so line-splitting would fragment them).
                var body = string.Join('\n',
                    (await File.ReadAllTextAsync(patchPath))
                    .Split('\n')
                    .Where(l => !l.TrimStart().StartsWith("--")));
                var statements = body
                    .Split(';', StringSplitOptions.TrimEntries)
                    .Where(s => s.Length > 0)
                    .ToArray();

                await Assert.That(statements.Length).IsEqualTo(6); // one CREATE TABLE per table

                foreach (var statement in statements)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }

                // All six tables exist in the freshly migrated schema.
                var expected = string.Join(",", s_tableNames.Select(t => $"'{t}'"));
                await using (var check = connection.CreateCommand())
                {
                    check.CommandText =
                        "SELECT COUNT(*) FROM `information_schema`.`tables` " +
                        $"WHERE `table_schema` = DATABASE() AND `table_name` IN ({expected})";
                    var count = Convert.ToInt32(await check.ExecuteScalarAsync());
                    await Assert.That(count).IsEqualTo(6);
                }

                // Smoke: the persistence write shape works against the real schema.
                await using (var smoke = connection.CreateCommand())
                {
                    smoke.CommandText =
                        "REPLACE INTO `playerbot_profile` " +
                        "(`character_id`, `account_id`, `fidelity`, `behavior_profile`, `schedule_enabled`, `last_seen`, `created_at`, `updated_at`) " +
                        "VALUES (1, 1, 2, 'roam', 1, '2026-08-07 12:00:00', NOW(), NOW())";
                    await smoke.ExecuteNonQueryAsync();
                }
                await using (var read = connection.CreateCommand())
                {
                    read.CommandText = "SELECT `fidelity`, `behavior_profile` FROM `playerbot_profile` WHERE `character_id` = 1";
                    await using var reader = await read.ExecuteReaderAsync();
                    await Assert.That(await reader.ReadAsync()).IsTrue();
                    await Assert.That(Convert.ToByte(reader["fidelity"])).IsEqualTo((byte)BotFidelity.Full);
                    await Assert.That(Convert.ToString(reader["behavior_profile"])).IsEqualTo("roam");
                }
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
