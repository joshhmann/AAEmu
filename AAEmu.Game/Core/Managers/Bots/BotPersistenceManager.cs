using System.Collections.Concurrent;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Dirty-flagged bot metadata persistence (ARCHITECTURE_REVIEW H4 / spec §13).
///
/// Rules this class enforces:
///   * NO per-AI-step writes — the only write path is a flush (periodic batch
///     or mandatory deactivate/downgrade/shutdown).
///   * Periodic flush is a bounded batch: one connection + one transaction per
///     cycle, only dirty domains, dirty bits cleared only after commit.
///   * Mandatory flush: FlushAsync(charId, Deactivate|Downgrade) for lifecycle
///     transitions; ShutdownAsync() (hooked via BotPersistenceBootstrap) for
///     server exit. Nothing pending is ever dropped silently.
///   * Fail-soft: a failed flush logs, keeps the dirty bits, and retries on the
///     next cycle — it never throws into gameplay callers.
///
/// Gameplay state is NOT this manager's job — characters/inventory/quests ride
/// the normal Character persistence. This manager only touches the additive
/// playerbot_* tables.
/// </summary>
public sealed class BotPersistenceManager : Singleton<BotPersistenceManager>, IBotPersistence
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Default periodic batch interval. Kept as an internal constant for this
    /// slice (config wiring is deliberately out of scope — card t_afbce6a0
    /// forbids config mutation); tunable later via Initialize(interval).
    /// </summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<uint, BotMetadataRecord> _records = new();
    private readonly Func<IBotPersistenceDb> _dbFactory;
    private readonly SemaphoreSlim _flushMutex = new(1, 1);

    private TimeSpan _flushInterval = DefaultFlushInterval;
    private Timer? _flushTimer;
    private bool _initialized;
    private bool _shutdown;
    private long _totalFlushCycles;
    private static volatile bool s_instanceInitialized;

    /// <summary>Production ctor (Singleton reflection fallback + bootstrap path).</summary>
    public BotPersistenceManager()
        : this(() => new MySqlBotPersistenceDb(MySQL.CreateConnection()))
    {
    }

    /// <summary>Test seam: inject a recording/in-memory IBotPersistenceDb factory.</summary>
    public BotPersistenceManager(Func<IBotPersistenceDb> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Starts the periodic batched flush. Idempotent. Called by
    /// BotPersistenceBootstrap once the DI container is up; tests may call it
    /// with a short interval.
    /// </summary>
    public void Initialize(TimeSpan? interval = null)
    {
        if (_initialized)
            return;
        _initialized = true;
        s_instanceInitialized = true;

        if (interval.HasValue)
            _flushInterval = interval.Value;

        Logger.Info($"Bot metadata persistence initialized — periodic dirty-flush every {_flushInterval.TotalSeconds:0}s");
        _flushTimer = new Timer(_ => OnPeriodicTick(), null, _flushInterval, _flushInterval);
    }

    /// <summary>
    /// Mandatory final flush: stops the timer, then persists everything
    /// pending. Idempotent. Returns after the flush completes (or its
    /// cancellation is signalled).
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        _shutdown = true;
        StopTimer();
        try
        {
            var written = await FlushAllAsync(BotFlushReason.Shutdown, ct).ConfigureAwait(false);
            if (written > 0)
                Logger.Info($"Bot metadata shutdown flush persisted {written} statement(s)");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Bot metadata shutdown flush failed — pending dirty state left in memory");
        }
    }

    /// <summary>
    /// Bounded synchronous final flush for process-exit signal handlers
    /// (ProcessExit / CancelKeyPress / SIGTERM). Never throws; never runs when
    /// the manager was never initialized (e.g. unit-test processes).
    /// </summary>
    public void ShutdownFlushSync()
    {
        if (!s_instanceInitialized)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ShutdownAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // The process is exiting; a failed final flush must not hang it.
        }
    }

    /// <summary>Static entry used by BotPersistenceBootstrap signal hooks — flushes only when initialized.</summary>
    public static void ShutdownFlushIfInitialized()
    {
        if (!s_instanceInitialized)
            return;
        try
        {
            Instance.ShutdownFlushSync();
        }
        catch
        {
            // Never let a dying process be held up by the final flush.
        }
    }

    // ------------------------------------------------------------------ registry

    public BotMetadataRecord GetOrCreate(uint characterId, uint accountId = 0)
    {
        return _records.GetOrAdd(characterId, id =>
        {
            var record = new BotMetadataRecord(id);
            record.Profile.AccountId = accountId;
            return record;
        });
    }

    public BotMetadataRecord? Get(uint characterId) =>
        _records.TryGetValue(characterId, out var record) ? record : null;

    public bool IsRegistered(uint characterId) => _records.ContainsKey(characterId);

    public void MarkDirty(uint characterId, BotMetadataDomain domain)
    {
        GetOrCreate(characterId).Mark(domain);
    }

    public bool IsDirty(uint characterId) => Get(characterId)?.HasAnyDirty ?? false;

    public int RegisteredCount => _records.Count;

    public int DirtyRecordCount => _records.Values.Count(r => r.HasAnyDirty);

    public long TotalFlushCycles => Interlocked.Read(ref _totalFlushCycles);

    // ------------------------------------------------------------------ flush

    /// <inheritdoc />
    public async Task<int> FlushAsync(uint characterId, BotFlushReason reason, CancellationToken ct = default)
    {
        if (!_records.TryGetValue(characterId, out var record) || !record.HasAnyDirty)
            return 0;

        await _flushMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!record.HasAnyDirty)
                return 0;
            var written = await FlushRecordsCoreAsync([record], reason, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalFlushCycles);
            return written;
        }
        finally
        {
            _flushMutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> FlushAllAsync(BotFlushReason reason, CancellationToken ct = default)
    {
        var records = _records.Values.Where(r => r.HasAnyDirty).ToArray();
        if (records.Length == 0)
            return 0;

        await _flushMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var written = await FlushRecordsCoreAsync(records, reason, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalFlushCycles);
            return written;
        }
        finally
        {
            _flushMutex.Release();
        }
    }

    /// <summary>
    /// One connection + one transaction for the whole batch; dirty bits are
    /// cleared only after the commit succeeds, so a failure retries the same
    /// domains next cycle. Per-record writes are bounded by the number of
    /// registered bots, never by AI-step volume.
    /// </summary>
    private async Task<int> FlushRecordsCoreAsync(BotMetadataRecord[] records, BotFlushReason reason, CancellationToken ct)
    {
        using var db = _dbFactory();
        await db.BeginAsync(ct).ConfigureAwait(false);

        var written = 0;
        var flushed = new List<BotMetadataRecord>();
        try
        {
            foreach (var record in records)
            {
                if (!record.HasAnyDirty)
                    continue;
                written += await WriteRecordAsync(db, record, ct).ConfigureAwait(false);
                flushed.Add(record);
            }

            await db.CommitAsync(ct).ConfigureAwait(false);

            // Dirty bits clear only AFTER the commit lands — a failed commit
            // keeps them set so the next cycle retries the exact same domains.
            foreach (var record in flushed)
                record.ClearDirty();

            return written;
        }
        catch (Exception e)
        {
            await db.RollbackAsync(ct).ConfigureAwait(false);
            Logger.Error(e, $"Bot metadata flush ({reason}) failed for {records.Length} record(s) — dirty bits kept for retry");
            throw;
        }
    }

    /// <summary>Writes every dirty domain of one record. Returns statement count.</summary>
    private static async Task<int> WriteRecordAsync(IBotPersistenceDb db, BotMetadataRecord record, CancellationToken ct)
    {
        var written = 0;
        var characterId = record.CharacterId;

        if (record.IsDirty(BotMetadataDomain.Profile))
        {
            var profile = record.Profile;
            await db.ExecuteNonQueryAsync(
                "REPLACE INTO `playerbot_profile` " +
                "(`character_id`, `account_id`, `fidelity`, `behavior_profile`, `schedule_enabled`, `last_seen`, `created_at`, `updated_at`) " +
                "VALUES (@character_id, @account_id, @fidelity, @behavior_profile, @schedule_enabled, @last_seen, @created_at, @updated_at)",
                [
                    P("@character_id", characterId),
                    P("@account_id", profile.AccountId),
                    P("@fidelity", (byte)profile.Fidelity),
                    P("@behavior_profile", profile.BehaviorProfile),
                    P("@schedule_enabled", profile.ScheduleEnabled),
                    P("@last_seen", profile.LastSeenUtc),
                    P("@created_at", profile.CreatedAtUtc),
                    P("@updated_at", DateTime.UtcNow)
                ],
                ct).ConfigureAwait(false);
            written++;
        }

        if (record.IsDirty(BotMetadataDomain.Schedule))
        {
            // Full-list replace: the in-memory schedule list is authoritative.
            await db.ExecuteNonQueryAsync(
                "DELETE FROM `playerbot_schedule` WHERE `character_id` = @character_id",
                [P("@character_id", characterId)],
                ct).ConfigureAwait(false);
            written++;

            foreach (var entry in record.Schedule)
            {
                await db.ExecuteNonQueryAsync(
                    "INSERT INTO `playerbot_schedule` " +
                    "(`id`, `character_id`, `day_mask`, `start_time`, `end_time`, `activity_type`, `params`, `enabled`, `updated_at`) " +
                    "VALUES (@id, @character_id, @day_mask, @start_time, @end_time, @activity_type, @params, @enabled, @updated_at)",
                    [
                        P("@id", entry.Id),
                        P("@character_id", characterId),
                        P("@day_mask", entry.DayMask),
                        P("@start_time", entry.StartTime),
                        P("@end_time", entry.EndTime),
                        P("@activity_type", entry.ActivityType),
                        P("@params", entry.Params),
                        P("@enabled", entry.Enabled),
                        P("@updated_at", DateTime.UtcNow)
                    ],
                    ct).ConfigureAwait(false);
                written++;
            }
        }

        if (record.IsDirty(BotMetadataDomain.Activity))
        {
            var activity = record.Activity;
            await db.ExecuteNonQueryAsync(
                "REPLACE INTO `playerbot_activity` " +
                "(`character_id`, `activity_type`, `state`, `started_at`, `ended_at`, `cycles`, `failure_count`, `last_error`, `updated_at`) " +
                "VALUES (@character_id, @activity_type, @state, @started_at, @ended_at, @cycles, @failure_count, @last_error, @updated_at)",
                [
                    P("@character_id", characterId),
                    P("@activity_type", activity.ActivityType),
                    P("@state", (byte)activity.State),
                    P("@started_at", activity.StartedAtUtc),
                    P("@ended_at", activity.EndedAtUtc),
                    P("@cycles", activity.Cycles),
                    P("@failure_count", activity.FailureCount),
                    P("@last_error", activity.LastError),
                    P("@updated_at", DateTime.UtcNow)
                ],
                ct).ConfigureAwait(false);
            written++;
        }

        if (record.IsDirty(BotMetadataDomain.Home))
        {
            var home = record.Home;
            await db.ExecuteNonQueryAsync(
                "REPLACE INTO `playerbot_home` " +
                "(`character_id`, `world_id`, `zone_id`, `x`, `y`, `z`, `yaw`, `return_on_combat_exit`, `updated_at`) " +
                "VALUES (@character_id, @world_id, @zone_id, @x, @y, @z, @yaw, @return_on_combat_exit, @updated_at)",
                [
                    P("@character_id", characterId),
                    P("@world_id", home.WorldId),
                    P("@zone_id", home.ZoneId),
                    P("@x", home.X),
                    P("@y", home.Y),
                    P("@z", home.Z),
                    P("@yaw", home.Yaw),
                    P("@return_on_combat_exit", home.ReturnOnCombatExit),
                    P("@updated_at", DateTime.UtcNow)
                ],
                ct).ConfigureAwait(false);
            written++;
        }

        if (record.IsDirty(BotMetadataDomain.MemoryFlags))
        {
            await db.ExecuteNonQueryAsync(
                "REPLACE INTO `playerbot_memory_flags` " +
                "(`character_id`, `flags`, `last_updated`) VALUES (@character_id, @flags, @last_updated)",
                [
                    P("@character_id", characterId),
                    P("@flags", record.MemoryFlags.Flags),
                    P("@last_updated", DateTime.UtcNow)
                ],
                ct).ConfigureAwait(false);
            written++;
        }

        if (record.IsDirty(BotMetadataDomain.PopulationState))
        {
            var population = record.PopulationState;
            await db.ExecuteNonQueryAsync(
                "REPLACE INTO `playerbot_population_state` " +
                "(`character_id`, `fidelity`, `pressure_state`, `last_transition_at`, `transition_count`, `updated_at`) " +
                "VALUES (@character_id, @fidelity, @pressure_state, @last_transition_at, @transition_count, @updated_at)",
                [
                    P("@character_id", characterId),
                    P("@fidelity", (byte)population.Fidelity),
                    P("@pressure_state", (byte)population.PressureState),
                    P("@last_transition_at", population.LastTransitionAtUtc),
                    P("@transition_count", population.TransitionCount),
                    P("@updated_at", DateTime.UtcNow)
                ],
                ct).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    // ------------------------------------------------------------------ restore

    /// <inheritdoc />
    public async Task<BotMetadataRecord> RestoreAsync(uint characterId, CancellationToken ct = default)
    {
        using var db = _dbFactory();
        var record = new BotMetadataRecord(characterId);

        var profileRows = await db.QueryAsync(
            "SELECT `account_id`, `fidelity`, `behavior_profile`, `schedule_enabled`, `last_seen`, `created_at` " +
            "FROM `playerbot_profile` WHERE `character_id` = @character_id",
            [P("@character_id", characterId)],
            ct).ConfigureAwait(false);
        if (profileRows.Count > 0)
        {
            var row = profileRows[0];
            record.Profile.AccountId = GetUInt32(row, "account_id");
            record.Profile.Fidelity = (BotFidelity)GetByte(row, "fidelity");
            record.Profile.BehaviorProfile = GetString(row, "behavior_profile") ?? "idle";
            record.Profile.ScheduleEnabled = GetBool(row, "schedule_enabled");
            record.Profile.LastSeenUtc = GetDateTime(row, "last_seen");
            record.Profile.CreatedAtUtc = GetDateTime(row, "created_at");
        }

        var scheduleRows = await db.QueryAsync(
            "SELECT `id`, `day_mask`, `start_time`, `end_time`, `activity_type`, `params`, `enabled` " +
            "FROM `playerbot_schedule` WHERE `character_id` = @character_id ORDER BY `id`",
            [P("@character_id", characterId)],
            ct).ConfigureAwait(false);
        foreach (var row in scheduleRows)
        {
            record.Schedule.Add(new BotScheduleEntry
            {
                Id = GetInt64(row, "id"),
                CharacterId = characterId,
                DayMask = GetByte(row, "day_mask"),
                StartTime = GetTimeSpan(row, "start_time"),
                EndTime = GetTimeSpan(row, "end_time"),
                ActivityType = GetString(row, "activity_type") ?? "idle",
                Params = GetString(row, "params"),
                Enabled = GetBool(row, "enabled")
            });
        }

        var activityRows = await db.QueryAsync(
            "SELECT `activity_type`, `state`, `started_at`, `ended_at`, `cycles`, `failure_count`, `last_error` " +
            "FROM `playerbot_activity` WHERE `character_id` = @character_id",
            [P("@character_id", characterId)],
            ct).ConfigureAwait(false);
        if (activityRows.Count > 0)
        {
            var row = activityRows[0];
            record.Activity.ActivityType = GetString(row, "activity_type") ?? "idle";
            record.Activity.State = (BotActivityState)GetByte(row, "state");
            record.Activity.StartedAtUtc = GetDateTime(row, "started_at");
            record.Activity.EndedAtUtc = GetDateTime(row, "ended_at");
            record.Activity.Cycles = GetUInt32(row, "cycles");
            record.Activity.FailureCount = GetUInt32(row, "failure_count");
            record.Activity.LastError = GetString(row, "last_error");
        }

        var homeRows = await db.QueryAsync(
            "SELECT `world_id`, `zone_id`, `x`, `y`, `z`, `yaw`, `return_on_combat_exit` " +
            "FROM `playerbot_home` WHERE `character_id` = @character_id",
            [P("@character_id", characterId)],
            ct).ConfigureAwait(false);
        if (homeRows.Count > 0)
        {
            var row = homeRows[0];
            record.Home.WorldId = GetUInt32(row, "world_id");
            record.Home.ZoneId = GetUInt32(row, "zone_id");
            record.Home.X = GetFloat(row, "x");
            record.Home.Y = GetFloat(row, "y");
            record.Home.Z = GetFloat(row, "z");
            record.Home.Yaw = GetFloat(row, "yaw");
            record.Home.ReturnOnCombatExit = GetBool(row, "return_on_combat_exit");
        }

        var memoryRows = await db.QueryAsync(
            "SELECT `flags` FROM `playerbot_memory_flags` WHERE `character_id` = @character_id",
            [P("@character_id", characterId)],
            ct).ConfigureAwait(false);
        if (memoryRows.Count > 0)
            record.MemoryFlags.Flags = GetUInt64(memoryRows[0], "flags");

        var populationRows = await db.QueryAsync(
            "SELECT `fidelity`, `pressure_state`, `last_transition_at`, `transition_count` " +
            "FROM `playerbot_population_state` WHERE `character_id` = @character_id",
            [P("@character_id", characterId)],
            ct).ConfigureAwait(false);
        if (populationRows.Count > 0)
        {
            var row = populationRows[0];
            record.PopulationState.Fidelity = (BotFidelity)GetByte(row, "fidelity");
            record.PopulationState.PressureState = (BotPressureState)GetByte(row, "pressure_state");
            record.PopulationState.LastTransitionAtUtc = GetDateTime(row, "last_transition_at");
            record.PopulationState.TransitionCount = GetUInt32(row, "transition_count");
        }

        return record;
    }

    // ------------------------------------------------------------------ periodic tick

    private void OnPeriodicTick()
    {
        if (_shutdown)
            return;
        try
        {
            var written = FlushAllAsync(BotFlushReason.Periodic).GetAwaiter().GetResult();
            if (written > 0)
                Logger.Debug($"Bot metadata periodic flush persisted {written} statement(s)");
        }
        catch (Exception e)
        {
            // Fail-soft: dirty bits survive, the next cycle retries.
            Logger.Error(e, "Periodic bot metadata flush failed — will retry next cycle");
        }
    }

    private void StopTimer()
    {
        var timer = _flushTimer;
        _flushTimer = null;
        timer?.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private static MySqlParameter P(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private static uint GetUInt32(Dictionary<string, object> row, string column) =>
        Convert.ToUInt32(row[column]);

    private static long GetInt64(Dictionary<string, object> row, string column) =>
        Convert.ToInt64(row[column]);

    private static ulong GetUInt64(Dictionary<string, object> row, string column) =>
        Convert.ToUInt64(row[column]);

    private static byte GetByte(Dictionary<string, object> row, string column) =>
        Convert.ToByte(row[column]);

    private static bool GetBool(Dictionary<string, object> row, string column) =>
        Convert.ToBoolean(row[column]);

    private static float GetFloat(Dictionary<string, object> row, string column) =>
        Convert.ToSingle(row[column]);

    private static DateTime GetDateTime(Dictionary<string, object> row, string column)
    {
        var value = row[column];
        return value is DateTime dateTime ? dateTime : DateTime.MinValue;
    }

    private static TimeSpan GetTimeSpan(Dictionary<string, object> row, string column)
    {
        var value = row[column];
        return value is TimeSpan timeSpan ? timeSpan : TimeSpan.Zero;
    }

    private static string? GetString(Dictionary<string, object> row, string column)
    {
        var value = row[column];
        return value is DBNull or null ? null : Convert.ToString(value);
    }
}
