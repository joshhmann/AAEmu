using System.Diagnostics;

namespace AAEmu.Commons.Utils.DB;

/// <summary>
/// Process-wide MySQL statement counters, fed by the MySql.Data 9.7.0
/// <c>connector-net</c> <see cref="ActivitySource"/> (every command execution —
/// sync and async, reads and writes — emits an <c>SQL Statement</c> activity).
///
/// Coverage is genuinely TOTAL for this process: the listener sits below every
/// call site (<c>MySqlConnection.CreateCommand</c> +
/// <c>ExecuteNonQuery/Reader/Scalar</c>), so no codemod is needed and new
/// direct sites are counted automatically. Started once from
/// <see cref="MySQL"/>'s static constructor — the single choke point every
/// Game connection flows through.
///
/// Honesty limit: user-DML activities carry NO statement text, rows-affected,
/// or byte counts (only internal handshake commands tag <c>db.statement</c>),
/// so these counters measure statement VOLUME, not writes. Do NOT relabel
/// them as write counters; the gate bridge keeps <c>dbWritesAvailable=false</c>
/// until call-site write instrumentation exists.
/// </summary>
public static class MySqlStatementCounters
{
    public const string ActivitySourceName = "connector-net";
    public const string StatementOperation = "SQL Statement";

    private const string GameStore = "aaemu_game";
    private const string LoginStore = "aaemu_login";

    private static long s_statements;
    private static long s_failedStatements;
    private static long s_gameStatements;
    private static long s_loginStatements;
    private static long s_otherStatements;

    private static readonly object s_startLock = new();
    private static bool s_started;
    private static ActivityListener s_listener;

    public static bool IsStarted
    {
        get
        {
            lock (s_startLock)
            {
                return s_started;
            }
        }
    }

    /// <summary>
    /// Idempotent: registers the process-wide listener once. Cheap when no
    /// statements execute (one string comparison per ActivitySource).
    /// </summary>
    public static void EnsureStarted()
    {
        lock (s_startLock)
        {
            if (s_started)
            {
                return;
            }

            s_listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = OnActivityStopped
            };
            ActivitySource.AddActivityListener(s_listener);
            s_started = true;
        }
    }

    private static void OnActivityStopped(Activity activity)
    {
        string store = null;
        var failed = activity.Status == ActivityStatusCode.Error;
        foreach (var tag in activity.Tags)
        {
            if (tag.Key == "db.name")
            {
                store = tag.Value;
            }
            else if (tag.Key == "otel.status_code" && tag.Value == "ERROR")
            {
                failed = true;
            }
        }

        RecordStopped(activity.OperationName, store, failed);
    }

    /// <summary>
    /// Counting core, separated from <see cref="Activity"/> so unit tests can
    /// drive it without a live MySQL server. Only <c>SQL Statement</c>
    /// operations count (<c>Connection (pooled)</c> open/close activities do not).
    public static void RecordStopped(string operation, string store, bool failed)
    {
        if (!string.Equals(operation, StatementOperation, StringComparison.Ordinal))
        {
            return;
        }

        Interlocked.Increment(ref s_statements);
        if (failed)
        {
            Interlocked.Increment(ref s_failedStatements);
        }

        if (string.Equals(store, GameStore, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref s_gameStatements);
        }
        else if (string.Equals(store, LoginStore, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref s_loginStatements);
        }
        else
        {
            Interlocked.Increment(ref s_otherStatements);
        }
    }

    /// <summary>
    /// Cumulative snapshot since process start. The gate harness diffs
    /// consecutive snapshots to get per-window volume (boot handshake
    /// statements are included in the lifetime totals, not in window deltas).
    /// </summary>
    public static MySqlStatementSnapshot Snapshot()
    {
        return new MySqlStatementSnapshot(
            Interlocked.Read(ref s_statements),
            Interlocked.Read(ref s_failedStatements),
            Interlocked.Read(ref s_gameStatements),
            Interlocked.Read(ref s_loginStatements),
            Interlocked.Read(ref s_otherStatements));
    }
}

/// <summary>
/// Immutable cumulative snapshot of <see cref="MySqlStatementCounters"/>.
/// </summary>
public sealed record MySqlStatementSnapshot(
    long Statements,
    long FailedStatements,
    long GameStatements,
    long LoginStatements,
    long OtherStatements);
