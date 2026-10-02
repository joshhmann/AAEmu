using AAEmu.Commons.IO;
using Microsoft.Data.Sqlite;
using NLog;

namespace AAEmu.Game.Utils.DB;

/// <summary>
/// Boot-time READ-ONLY check that the `holdables` table in compact.sqlite3 has the
/// six attack-animation columns (anim_r1_id, anim_l1_id, anim_r2_id, anim_l2_id,
/// anim_r3_id, anim_l3_id) read by the Auto-Attack item load.
///
/// compact.sqlite3 is CANONICAL READ-ONLY REFERENCE DATA (AGENTS.md rule 3 /
/// START-HERE non-negotiable #4; canonical md5 78b3bdbf038db3b927056106efdf91af,
/// 679 tables). This helper therefore NEVER opens it for writing and never
/// issues DDL or DML. It previously did: it reopened the database with
/// <c>Mode=ReadWrite</c> and ran <c>ALTER TABLE holdables ADD COLUMN</c> for
/// every missing column, silently mutating the canonical file at boot. That was
/// a data-integrity violation — the shipped canonical database already carries all
/// six columns, so the ALTER could only ever fire against a stripped or custom
/// database, and in doing so it corrupted the very file the project treats as
/// immutable reference data.
///
/// What it does now: a single read-only schema probe. If the columns are present
/// it is silent. If any are missing it logs an ERROR naming the exact
/// remediation — apply the reviewed patch from <c>SQL/patches/compact/</c> — and
/// the server continues booting. Auto-Attack then falls back to the fist
/// animations, because <see cref="SQLiteWrapperReader"/>'s missing-tolerant
/// accessors read an absent animation column as 0 rather than throwing.
///
/// All ops are best-effort; failures are logged but never throw the server.
/// </summary>
public static class HoldablesSchemaCheck
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly string[] RequiredColumns =
    [
        "anim_r1_id", "anim_l1_id",
        "anim_r2_id", "anim_l2_id",
        "anim_r3_id", "anim_l3_id"
    ];

    /// <summary>
    /// Verifies the six attack-animation columns exist. READ-ONLY by contract:
    /// the database is opened in <c>Mode=ReadOnly</c> and only
    /// <c>PRAGMA table_info</c> is issued — no ALTER, no DDL, no DML, and never
    /// a writable connection. A missing column is REPORTED, not repaired.
    /// </summary>
    public static void EnsureColumns(string directory = "Data", string sqlite = "compact.sqlite3")
    {
        var dbPath = Path.Combine(FileManager.AppPath, directory, sqlite);
        if (!File.Exists(dbPath))
        {
            Logger.Warn("HoldablesSchemaCheck: {0} not found — skipping.", dbPath);
            return;
        }

        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var roConn = new SqliteConnection($"Data Source=file:{dbPath}; Mode=ReadOnly"))
            {
                roConn.Open();
                using var pragma = roConn.CreateCommand();
                pragma.CommandText = "PRAGMA table_info(holdables);";
                using var rdr = pragma.ExecuteReader();
                while (rdr.Read())
                {
                    // column 1 is the name in PRAGMA table_info
                    existing.Add(rdr.GetString(1));
                }
            }

            var missing = new List<string>();
            foreach (var col in RequiredColumns)
            {
                if (!existing.Contains(col))
                    missing.Add(col);
            }

            if (missing.Count == 0)
            {
                Logger.Info("HoldablesSchemaCheck: all 6 anim columns present, no action needed.");
                return;
            }

            // REPORT, DO NOT REPAIR. compact.sqlite3 is canonical read-only
            // reference data; this helper must never write it. The fix belongs in
            // a reviewed patch under SQL/patches/compact/, applied deliberately by
            // whoever owns that database — not silently at every boot. The server
            // still starts: SQLiteWrapperReader's missing-tolerant accessors read
            // an absent animation column as 0, so Auto-Attack degrades to the
            // fist animations exactly as it does for a 0 animation id.
            Logger.Error(
                "HoldablesSchemaCheck: {0} is MISSING holdables column(s): {1} — this file is " +
                "canonical READ-ONLY reference data and is NOT modified at runtime. Apply the " +
                "reviewed patch instead: SQL/patches/compact/ (see the patch's header for the " +
                "exact command). Until then Auto-Attack falls back to the fist animations " +
                "(missing anim columns read as 0). Server continues.",
                dbPath, string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "HoldablesSchemaCheck failed — server will continue");
        }
    }
}
