using System.Collections.Concurrent;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// B4 audit-trace flush (the second half of the ROADMAP B4 line item — the
/// M5 audit trail persisted so it survives restarts and stays queryable for
/// the M8 "auditable economy"): terminal BotActionCommandQueue audit records
/// (one JSON per completed/rejected/interrupted/timed-out control-plane
/// action) are buffered in memory and batch-inserted into
/// aaemu_game.playerbot_audit on the SaveManager tick.
///
/// Discipline:
///  - NEVER on the game-loop thread: Enqueue is a bounded in-memory append
///    only; all DB I/O happens in Flush (the SaveManager transaction).
///  - Telemetry, not gameplay: the buffer is capped (drop-oldest) and Flush
///    failures are logged and retried next tick — audit never blocks or
///    breaks the save cycle.
///  - Same schema conventions as PlayerBotMetadataStore: lazy self-healing
///    CREATE TABLE IF NOT EXISTS (migration also ships via SQL/updates +
///    the base dump), SQL kept as internal static builders for hermetic
///    tests.
/// </summary>
public class PlayerBotAuditSink : Singleton<PlayerBotAuditSink>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Max buffered records; beyond this the oldest are dropped (audit is telemetry).</summary>
    internal const int MaxBufferedRecords = 10_000;

    /// <summary>Max records flushed per save tick.</summary>
    internal const int MaxFlushBatch = 1_000;

    private readonly ConcurrentQueue<(uint CharacterId, string Json)> _pending = new();

    private int _schemaEnsured; // 0 = not attempted, 1 = ensured, -1 = attempted and failed

    /// <summary>Buffer count (test/observability surface).</summary>
    public int BufferedCount => _pending.Count;

    /// <summary>
    /// Buffers one terminal audit record. Called from the execution boundary
    /// (BotActionCommandQueue.PublishSnapshot) — in-memory append ONLY, never
    /// throws, drop-oldest past the cap.
    /// </summary>
    public void Enqueue(uint characterId, string auditJson)
    {
        if (string.IsNullOrEmpty(auditJson))
            return;
        _pending.Enqueue((characterId, auditJson));
        while (_pending.Count > MaxBufferedRecords && _pending.TryDequeue(out _))
        {
            // drop-oldest under cap pressure
        }
    }

    /// <summary>
    /// SaveManager.DoSave hook: batch-inserts buffered records inside the
    /// ambient save transaction. All failures are caught and logged; flushed
    /// rows are only dequeued after a successful insert, so a mid-batch
    /// failure keeps the remainder for the next tick. Never breaks the save
    /// cycle.
    /// </summary>
    public void Flush(MySqlConnection? connection, MySqlTransaction? transaction)
    {
        if (connection == null || _pending.IsEmpty)
            return;
        if (!EnsureSchema())
            return;

        var flushed = 0;
        try
        {
            while (flushed < MaxFlushBatch && _pending.TryPeek(out var row))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = BuildInsertSql();
                command.Parameters.AddWithValue("@character_id", row.CharacterId);
                command.Parameters.AddWithValue("@audit_json", row.Json);
                command.ExecuteNonQuery();
                _pending.TryDequeue(out _);
                flushed++;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "PlayerBotAudit: flush failed after {Flushed} row(s) — {Buffered} record(s) kept for the next tick",
                flushed, _pending.Count);
        }
    }

    /// <summary>Idempotently ensures aaemu_game.playerbot_audit exists.</summary>
    public bool EnsureSchema()
    {
        var state = Volatile.Read(ref _schemaEnsured);
        if (state != 0)
            return state == 1;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildEnsureSchemaCheckSql();
            var exists = (long)(command.ExecuteScalar() ?? 0L) > 0;
            if (!exists)
            {
                command.CommandText = BuildEnsureSchemaSql();
                command.ExecuteNonQuery();
                Logger.Info("PlayerBotAudit: created aaemu_game.playerbot_audit (B4 audit-trace flush)");
            }
            _schemaEnsured = 1;
            return true;
        }
        catch (Exception e)
        {
            _schemaEnsured = -1;
            Logger.Error(e, "PlayerBotAudit: failed to ensure aaemu_game.playerbot_audit — apply SQL/updates migration manually (bot audit will not persist)");
            return false;
        }
    }

    // ------------------------------------------------------------------ SQL shapes (builders so the
    // ------------------------------------------------------------------ hermetic rig can lock the contract)

    internal static string BuildEnsureSchemaCheckSql()
        => "SELECT COUNT(*) FROM information_schema.TABLES " +
           "WHERE TABLE_SCHEMA = 'aaemu_game' AND TABLE_NAME = 'playerbot_audit'";

    internal static string BuildEnsureSchemaSql()
        => "CREATE TABLE IF NOT EXISTS `playerbot_audit` (" +
           "`id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, " +
           "`character_id` INT UNSIGNED NOT NULL, " +
           "`audit_json` TEXT NOT NULL, " +
           "`created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
           "KEY `ix_playerbot_audit_character` (`character_id`))";

    internal static string BuildInsertSql()
        => "INSERT INTO `playerbot_audit` (`character_id`, `audit_json`) VALUES (@character_id, @audit_json)";
}
