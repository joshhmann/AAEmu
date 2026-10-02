using System.Reflection;
using System.Security.Cryptography;

using AAEmu.Commons.IO;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using NLog;
using NLog.Config;
using NLog.Targets;

namespace AAEmu.UnitTests.Utils.DB;

/// <summary>
/// Data-integrity contract for the boot-time holdables schema check.
///
/// compact.sqlite3 is CANONICAL READ-ONLY REFERENCE DATA (AGENTS.md rule 3).
/// The helper used to reopen it with <c>Mode=ReadWrite</c> and run
/// <c>ALTER TABLE holdables ADD COLUMN</c> at every boot, silently mutating the
/// canonical file. These tests pin the corrected contract:
///
///   1. the helper NEVER mutates the database it inspects — byte-identical
///      before and after, including on a database that is MISSING the columns;
///   2. it never throws (the boot path must survive any defect);
///   3. a missing column is a loud, named report — not a silent repair.
///
/// Every test uses a THROWAWAY COPY under a temp directory. The repository's
/// canonical database is never opened for writing, and never used as a fixture.
/// </summary>
[NotInParallel]
public class HoldablesSchemaCheckTests
{
    private static readonly string[] AnimColumns =
    [
        "anim_r1_id", "anim_l1_id",
        "anim_r2_id", "anim_l2_id",
        "anim_r3_id", "anim_l3_id"
    ];

    /// <summary>Logs captured while the helper runs, so a test can assert what was reported.</summary>
    private static List<string> Capture(Action action)
    {
        var target = new MemoryTarget { Layout = "${level}|${message}" };
        var config = new LoggingConfiguration();
        config.AddTarget("capture", target);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, target);
        LogManager.Configuration = config;
        try
        {
            action();
            return target.Logs.ToList();
        }
        finally
        {
            LogManager.Configuration = null;
        }
    }

    private static List<string> ErrorsOf(List<string> logs)
        => logs.Where(l => l.StartsWith("Error|", StringComparison.OrdinalIgnoreCase)
                           || l.StartsWith("Fatal|", StringComparison.OrdinalIgnoreCase))
               .ToList();

    /// <summary>
    /// Builds a throwaway compact.sqlite3-shaped database in a temp lane and
    /// returns its directory. The helper resolves the path via
    /// <c>FileManager.AppPath</c>, so the temp dir is used as the app path.
    /// </summary>
    private static string CreateTempDatabase(bool withAnimColumns)
    {
        var root = Path.Combine(Path.GetTempPath(), "holdables-check-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "compact.sqlite3");

        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE holdables (id INTEGER PRIMARY KEY, name TEXT);" +
                              "INSERT INTO holdables (id, name) VALUES (1, 'fist');";
            cmd.ExecuteNonQuery();

            if (withAnimColumns)
            {
                foreach (var col in AnimColumns)
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE holdables ADD COLUMN {col} INT DEFAULT 0;";
                    alter.ExecuteNonQuery();
                }
            }
        }

        return root;
    }

    private static string DbPath(string root) => Path.Combine(root, "Data", "compact.sqlite3");

    /// <summary>
    /// Runs the helper against a temp lane. The helper resolves the database as
    /// <c>Path.Combine(FileManager.AppPath, directory, sqlite)</c>, so the temp
    /// root has to BE the app path for the duration of the call.
    /// </summary>
    private static void RunAgainst(string root)
    {
        // FileManager.AppPath is get-only with a private static backing field;
        // the auction/singleton rigs in this suite already swap such fields by
        // reflection, so this follows the existing convention.
        var field = typeof(FileManager).GetField("_appPath",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FileManager._appPath backing field not found");
        var previous = field.GetValue(null) as string;
        field.SetValue(null, root);
        try
        {
            HoldablesSchemaCheck.EnsureColumns("Data", "compact.sqlite3");
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    private static string Fingerprint(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static List<string> ColumnNames(string path)
    {
        using var conn = new SqliteConnection($"Data Source=file:{path}; Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(holdables);";
        using var rdr = cmd.ExecuteReader();
        var names = new List<string>();
        while (rdr.Read())
            names.Add(rdr.GetString(1));
        return names;
    }

    /// <summary>
    /// THE REGRESSION TEST. A database missing the animation columns must come
    /// back BYTE-IDENTICAL and still missing them. The old implementation
    /// reopened it ReadWrite and added the columns, so the file changed AND the
    /// schema grew — this test fails on that behaviour.
    /// </summary>
    [Test]
    public async Task EnsureColumns_MissingSchema_ReportsWithoutMutatingTheDatabase()
    {
        var root = CreateTempDatabase(withAnimColumns: false);
        try
        {
            var db = DbPath(root);
            var before = Fingerprint(db);
            var colsBefore = ColumnNames(db);
            await Assert.That(colsBefore.Contains("anim_r1_id")).IsFalse()
                .Because("the fixture must genuinely lack the columns for this test to mean anything");

            var logs = Capture(() => RunAgainst(root));

            // 1. The file is untouched — byte-for-byte.
            var after = Fingerprint(db);
            await Assert.That(after).IsEqualTo(before)
                .Because("compact.sqlite3 is canonical READ-ONLY reference data; the helper " +
                         "must never write it (the old code ALTERed it at every boot)");

            // 2. The schema is untouched — no column was added.
            var colsAfter = ColumnNames(db);
            foreach (var col in AnimColumns)
            {
                await Assert.That(colsAfter.Contains(col)).IsFalse()
                    .Because($"the helper must not ADD {col}; the defect is reported, not repaired");
            }

            // 3. It is reported loudly and by name, with the remediation.
            var errors = ErrorsOf(logs);
            await Assert.That(errors.Count).IsGreaterThan(0)
                .Because("a missing required column is a real defect and must be logged as an ERROR");
            var report = string.Join("\n", errors);
            await Assert.That(report.Contains("anim_r1_id")).IsTrue()
                .Because("the report must name the missing columns");
            await Assert.That(report.Contains("SQL/patches/compact")).IsTrue()
                .Because("the report must name the exact remediation — apply the reviewed patch");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// The healthy path: a complete schema is left alone and is not reported as
    /// a defect. Also pins that the helper stays quiet when there is nothing to
    /// say, so boot logs do not fill with noise.
    /// </summary>
    [Test]
    public async Task EnsureColumns_CompleteSchema_LeavesFileByteIdenticalAndReportsNoError()
    {
        var root = CreateTempDatabase(withAnimColumns: true);
        try
        {
            var db = DbPath(root);
            var before = Fingerprint(db);

            var logs = Capture(() => RunAgainst(root));

            await Assert.That(Fingerprint(db)).IsEqualTo(before)
                .Because("a complete schema must not cause any write at all");
            await Assert.That(ErrorsOf(logs).Count).IsEqualTo(0)
                .Because("a complete schema is not a defect and must not be logged as an ERROR");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// "Log, never throw" is the boot contract that lets the server continue past
    /// a database defect. A file that exists but is not a database at all is the
    /// sharpest case: the read-only probe fails outright, and the helper must
    /// swallow it rather than take the server down.
    /// </summary>
    [Test]
    public async Task EnsureColumns_CorruptDatabase_LogsAndNeverThrows()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdables-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var db = DbPath(root);
        await File.WriteAllTextAsync(db, "this is definitely not a sqlite database");
        try
        {
            // The contract: this call RETURNS. If it throws, the server does not boot.
            var logs = Capture(() => RunAgainst(root));

            await Assert.That(ErrorsOf(logs).Count).IsGreaterThan(0)
                .Because("an unreadable database must be reported, not silently ignored");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// A missing database file is a normal state (e.g. a lane that has not been
    /// provisioned yet) and is a warning, not an error — it must not be reported
    /// as a data-integrity defect.
    /// </summary>
    [Test]
    public async Task EnsureColumns_MissingFile_WarnsAndDoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdables-absent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        try
        {
            var logs = Capture(() => RunAgainst(root));

            await Assert.That(ErrorsOf(logs).Count).IsEqualTo(0)
                .Because("an absent database is a warn-level state, not a data-integrity defect");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// The reader-side half of the fix: a missing animation column must read as
    /// 0 so a stripped database degrades to the fist animations instead of
    /// aborting the item load. <c>GetOrdinal</c> THROWS for an unknown column
    /// (verified: ArgumentOutOfRangeException), which is exactly why the load
    /// path had to move to the missing-tolerant accessor.
    /// </summary>
    [Test]
    public async Task GetUInt32OrDefault_AbsentColumn_ReturnsDefaultWithoutThrowing()
    {
        var root = CreateTempDatabase(withAnimColumns: false);
        try
        {
            using var conn = new SqliteConnection($"Data Source=file:{DbPath(root)}; Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM holdables;";
            using var sqliteReader = cmd.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            reader.Read();

            // The strict accessor still throws — that behaviour is deliberately unchanged.
            var strictThrew = false;
            try { reader.GetUInt32("anim_r1_id", 0); }
            catch (ArgumentOutOfRangeException) { strictThrew = true; }
            await Assert.That(strictThrew).IsTrue()
                .Because("GetOrdinal throws on a missing column; the tolerant accessor exists for that case");

            // The tolerant accessor is what the item load uses, and it degrades to 0.
            await Assert.That(reader.GetUInt32OrDefault("anim_r1_id", 0)).IsEqualTo(0u);
            await Assert.That(reader.TryGetOrdinal("anim_r1_id") is null).IsTrue()
                .Because("an absent column has no ordinal");
            await Assert.That(reader.TryGetOrdinal("id") is not null).IsTrue()
                .Because("a present column resolves an ordinal");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// The tolerant accessor must still return REAL data when the column is
    /// present, so the fix cannot quietly zero out a correct canonical database.
    /// </summary>
    [Test]
    public async Task GetUInt32OrDefault_PresentColumn_ReturnsStoredValue()
    {
        var root = CreateTempDatabase(withAnimColumns: true);
        try
        {
            var db = DbPath(root);
            using (var w = new SqliteConnection($"Data Source={db}"))
            {
                w.Open();
                using var up = w.CreateCommand();
                up.CommandText = "UPDATE holdables SET anim_r1_id = 4242 WHERE id = 1;";
                up.ExecuteNonQuery();
            }

            using var conn = new SqliteConnection($"Data Source=file:{db}; Mode=ReadOnly");
            conn.Open();
            // The anim column must be PROJECTED, exactly as the real holdables
            // query does: an absent column can only yield its real value when the
            // result set actually carries it.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, anim_r1_id FROM holdables;";
            using var sqliteReader = cmd.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            reader.Read();

            await Assert.That(reader.GetUInt32OrDefault("anim_r1_id", 0)).IsEqualTo(4242u)
                .Because("a present column must read its real value; only a MISSING column defaults to 0");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
