using System.Collections.Concurrent;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Bots;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// B4 playerbot_metadata store (M6 deferred gate #5 — "B4 metadata
/// persistence implemented; bot-world restart test (2 checkpoints)").
/// Durable per-bot state (personality/profession/home/schedule/behavior/
/// planner) keyed by characters.id, modeled on BotAccountProvisioningService:
///
///  1. <see cref="EnsureSchema"/> — lazy, once-guarded, self-healing
///     information_schema check + CREATE TABLE IF NOT EXISTS (the same
///     migration ships as SQL/updates/2026-08-20_aaemu_game_playerbot_metadata.sql
///     and in the base SQL/aaemu_game.sql dump for managed environments).
///  2. <see cref="GetForRead"/> — cache-first read; a miss tries the DB once
///     and caches the row (or <see cref="PlayerBotMetadata.Empty"/> on
///     absence/failure). NEVER throws.
///  3. Record* mutations — update the cache, mark the row dirty, and
///     write-through (REPLACE INTO) immediately: the E2E restarts are HARD
///     KILLS, so metadata must reach the DB on mutation and/or the periodic
///     autosave tick, never only at shutdown. On DB failure the error is
///     logged and the row stays dirty for the next save tick.
///  4. <see cref="SaveDirty"/> — SaveManager.DoSave hook: REPLACEs every
///     dirty row inside the ambient save transaction and clears the dirty
///     flag ONLY on success. All exceptions are caught and logged — this
///     must never break the save cycle.
///
/// Failures are logged, never fatal to the server.
/// </summary>
public class PlayerBotMetadataStore : Singleton<PlayerBotMetadataStore>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<uint, PlayerBotMetadata> _cache = new();
    private readonly ConcurrentDictionary<uint, byte> _dirty = new();

    /// <summary>
    /// One schema attempt per process: 0 = not attempted, 1 = ensured,
    /// -1 = attempted and FAILED (the CREATE is idempotent and the table
    /// also ships via SQL/updates, so a boot-time DB hiccup is a logged
    /// error, not a retry loop).
    /// </summary>
    private int _schemaEnsured;

    /// <summary>Idempotently ensures aaemu_game.playerbot_metadata exists. True when the table is present afterwards.</summary>
    public bool EnsureSchema()
    {
        var state = Volatile.Read(ref _schemaEnsured);
        if (state != 0)
            return state == 1;

        lock (_ioLock)
        {
            if (_schemaEnsured != 0)
                return _schemaEnsured == 1;
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
                    Logger.Info("PlayerBotMetadata: created aaemu_game.playerbot_metadata (B4 metadata store)");
                }
                _schemaEnsured = 1;
                return true;
            }
            catch (Exception e)
            {
                _schemaEnsured = -1;
                Logger.Error(e, "PlayerBotMetadata: failed to ensure aaemu_game.playerbot_metadata — apply SQL/updates migration manually (bot metadata will not persist)");
                return false;
            }
        }
    }

    /// <summary>Cache-first read; never throws. A DB miss/absence caches <see cref="PlayerBotMetadata.Empty"/>.</summary>
    public PlayerBotMetadata GetForRead(uint characterId)
    {
        if (_cache.TryGetValue(characterId, out var cached))
            return cached;

        var metadata = TryLoad(characterId) ?? PlayerBotMetadata.Empty(characterId);
        _cache[characterId] = metadata;
        return metadata;
    }

    /// <summary>Records (and persists) the bot's home position.</summary>
    public void RecordHome(uint characterId, uint worldId, uint zoneId, float x, float y, float z)
    {
        lock (_ioLock)
        {
            var metadata = GetForRead(characterId);
            metadata.HasHome = true;
            metadata.HomeWorldId = worldId;
            metadata.HomeZoneId = zoneId;
            metadata.HomeX = x;
            metadata.HomeY = y;
            metadata.HomeZ = z;
            WriteThroughLocked(metadata);
        }
    }

    /// <summary>Records (and persists) the bot's serialized schedule (JSON).</summary>
    public void RecordSchedule(uint characterId, string scheduleJson)
    {
        lock (_ioLock)
        {
            var metadata = GetForRead(characterId);
            metadata.Schedule = scheduleJson ?? string.Empty;
            WriteThroughLocked(metadata);
        }
    }

    /// <summary>Records (and persists) the bot's profession.</summary>
    public void RecordProfession(uint characterId, string profession)
    {
        lock (_ioLock)
        {
            var metadata = GetForRead(characterId);
            metadata.Profession = profession ?? string.Empty;
            WriteThroughLocked(metadata);
        }
    }

    /// <summary>Records (and persists) the bot's personality.</summary>
    public void RecordPersonality(uint characterId, string personality)
    {
        lock (_ioLock)
        {
            var metadata = GetForRead(characterId);
            metadata.Personality = personality ?? string.Empty;
            WriteThroughLocked(metadata);
        }
    }

    /// <summary>Records (and persists) the bot's serialized behavior config (JSON).</summary>
    public void RecordBehaviorConfig(uint characterId, string behaviorConfigJson)
    {
        lock (_ioLock)
        {
            var metadata = GetForRead(characterId);
            metadata.BehaviorConfig = behaviorConfigJson ?? string.Empty;
            WriteThroughLocked(metadata);
        }
    }

    /// <summary>Records (and persists) the bot's serialized planner state (JSON).</summary>
    public void RecordPlannerState(uint characterId, string plannerStateJson)
    {
        lock (_ioLock)
        {
            var metadata = GetForRead(characterId);
            metadata.PlannerState = plannerStateJson ?? string.Empty;
            WriteThroughLocked(metadata);
        }
    }

    /// <summary>
    /// SaveManager.DoSave hook: REPLACEs every dirty row inside the ambient
    /// save transaction. Dirty flags clear ONLY on success; every failure is
    /// caught and logged so the save cycle is never broken.
    /// </summary>
    public void SaveDirty(MySqlConnection connection, MySqlTransaction transaction)
    {
        if (connection == null)
        {
            Logger.Warn("PlayerBotMetadata: SaveDirty called without a connection — {Count} dirty row(s) kept for the next tick", _dirty.Count);
            return;
        }

        foreach (var characterId in _dirty.Keys)
        {
            if (!_cache.TryGetValue(characterId, out var metadata))
            {
                _dirty.TryRemove(characterId, out _);
                continue;
            }

            try
            {
                UpsertRow(connection, transaction, metadata);
                _dirty.TryRemove(characterId, out _);
            }
            catch (Exception e)
            {
                Logger.Error(e, "PlayerBotMetadata: SaveDirty failed for character {CharacterId} — row stays dirty", characterId);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>True when the row has mutations not yet persisted (test surface).</summary>
    internal bool IsDirty(uint characterId) => _dirty.ContainsKey(characterId);

    /// <summary>Marks dirty + write-through REPLACE; on failure the row stays dirty. Caller holds _ioLock.</summary>
    private void WriteThroughLocked(PlayerBotMetadata metadata)
    {
        _dirty[metadata.CharacterId] = 1;
        try
        {
            EnsureSchema();
            using var connection = MySQL.CreateConnection();
            UpsertRow(connection, null, metadata);
            _dirty.TryRemove(metadata.CharacterId, out _);
        }
        catch (Exception e)
        {
            Logger.Error(e, "PlayerBotMetadata: write-through failed for character {CharacterId} — row stays dirty for the next save tick", metadata.CharacterId);
        }
    }

    /// <summary>One-shot DB load; null on absence or failure (logged).</summary>
    private PlayerBotMetadata TryLoad(uint characterId)
    {
        try
        {
            EnsureSchema();
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildSelectSql();
            command.Parameters.AddWithValue("@character_id", characterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new PlayerBotMetadata
            {
                CharacterId = reader.GetUInt32("character_id"),
                Personality = reader.GetString("personality"),
                Profession = reader.GetString("profession"),
                HasHome = reader.GetBoolean("has_home"),
                HomeWorldId = reader.GetUInt32("home_world_id"),
                HomeZoneId = reader.GetUInt32("home_zone_id"),
                HomeX = reader.GetFloat("home_x"),
                HomeY = reader.GetFloat("home_y"),
                HomeZ = reader.GetFloat("home_z"),
                Schedule = reader.IsDBNull(reader.GetOrdinal("schedule")) ? string.Empty : reader.GetString("schedule"),
                BehaviorConfig = reader.IsDBNull(reader.GetOrdinal("behavior_config")) ? string.Empty : reader.GetString("behavior_config"),
                PlannerState = reader.IsDBNull(reader.GetOrdinal("planner_state")) ? string.Empty : reader.GetString("planner_state"),
            };
        }
        catch (Exception e)
        {
            Logger.Error(e, "PlayerBotMetadata: load failed for character {CharacterId} — reading as empty", characterId);
            return null;
        }
    }

    private static void UpsertRow(MySqlConnection connection, MySqlTransaction transaction, PlayerBotMetadata metadata)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildUpsertSql();
        command.Parameters.AddWithValue("@character_id", metadata.CharacterId);
        command.Parameters.AddWithValue("@personality", metadata.Personality);
        command.Parameters.AddWithValue("@profession", metadata.Profession);
        command.Parameters.AddWithValue("@has_home", metadata.HasHome);
        command.Parameters.AddWithValue("@home_world_id", metadata.HomeWorldId);
        command.Parameters.AddWithValue("@home_zone_id", metadata.HomeZoneId);
        command.Parameters.AddWithValue("@home_x", metadata.HomeX);
        command.Parameters.AddWithValue("@home_y", metadata.HomeY);
        command.Parameters.AddWithValue("@home_z", metadata.HomeZ);
        command.Parameters.AddWithValue("@schedule", metadata.Schedule);
        command.Parameters.AddWithValue("@behavior_config", metadata.BehaviorConfig);
        command.Parameters.AddWithValue("@planner_state", metadata.PlannerState);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ SQL shapes (kept as builders so the
    // ------------------------------------------------------------------ hermetic rig can lock the data contract)

    internal static string BuildEnsureSchemaCheckSql()
        => "SELECT COUNT(*) FROM information_schema.TABLES " +
           "WHERE TABLE_SCHEMA = 'aaemu_game' AND TABLE_NAME = 'playerbot_metadata'";

    internal static string BuildEnsureSchemaSql()
        => "CREATE TABLE IF NOT EXISTS `playerbot_metadata` (" +
           "`character_id` INT UNSIGNED NOT NULL PRIMARY KEY, " +
           "`personality` VARCHAR(255) NOT NULL DEFAULT '', " +
           "`profession` VARCHAR(64) NOT NULL DEFAULT '', " +
           "`has_home` TINYINT(1) NOT NULL DEFAULT 0, " +
           "`home_world_id` INT UNSIGNED NOT NULL DEFAULT 0, " +
           "`home_zone_id` INT UNSIGNED NOT NULL DEFAULT 0, " +
           "`home_x` FLOAT NOT NULL DEFAULT 0, " +
           "`home_y` FLOAT NOT NULL DEFAULT 0, " +
           "`home_z` FLOAT NOT NULL DEFAULT 0, " +
           "`schedule` TEXT NULL, " +
           "`behavior_config` TEXT NULL, " +
           "`planner_state` TEXT NULL, " +
           "`updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)";

    internal static string BuildSelectSql()
        => "SELECT `character_id`, `personality`, `profession`, `has_home`, " +
           "`home_world_id`, `home_zone_id`, `home_x`, `home_y`, `home_z`, " +
           "`schedule`, `behavior_config`, `planner_state` " +
           "FROM `playerbot_metadata` WHERE `character_id` = @character_id";

    internal static string BuildUpsertSql()
        => "REPLACE INTO `playerbot_metadata` (" +
           "`character_id`, `personality`, `profession`, `has_home`, " +
           "`home_world_id`, `home_zone_id`, `home_x`, `home_y`, `home_z`, " +
           "`schedule`, `behavior_config`, `planner_state`) " +
           "VALUES (@character_id, @personality, @profession, @has_home, " +
           "@home_world_id, @home_zone_id, @home_x, @home_y, @home_z, " +
           "@schedule, @behavior_config, @planner_state)";
}
