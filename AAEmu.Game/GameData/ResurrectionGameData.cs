using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.GameData;

/// <summary>
/// Canonical resurrection wait ladder from compact.sqlite3 resurrection_waiting_times
/// (read-only reference data): escalating base waiting_time by consecutive-death index
/// (id1→0s, id2→5s, id3→45s, id4→90s, id5+→180s) plus the 600s penalty window after
/// which the death counter resets. Consumed by Character.DoDie's countdown
/// (surfaced on SCUnitDeathPacket.resurrectWaitingTime).
/// </summary>
[GameData]
public class ResurrectionGameData : Singleton<ResurrectionGameData>, IGameDataLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Pre-canonical hardcoded ladder — fallback for private servers with trimmed DBs.</summary>
    public static readonly int[] FallbackWaitingTimesSeconds = [15, 30, 60, 90, 120, 150, 180, 210, 240];
    /// <summary>Pre-canonical 5min reset window — fallback for private servers with trimmed DBs.</summary>
    public const int FallbackPenaltyWindowSeconds = 300;

    /// <summary>Base respawn countdown in seconds, indexed by consecutive-death count (clamped).</summary>
    public IReadOnlyList<int> WaitingTimesSeconds { get; private set; } = FallbackWaitingTimesSeconds;
    /// <summary>
    /// Siege-specific countdown in seconds. Loaded for fidelity but NOT wired into the
    /// death path yet: no trustworthy is-in-siege signal exists at the DoDie callsite
    /// (DominionManager.GetCurrentPhase needs a zone-group→siege-zone lookup that would be
    /// new coupling invented at the death path). FOLLOW-UP: route siege-phase deaths
    /// through this ladder once a ready is-in-siege helper exists.
    /// </summary>
    public IReadOnlyList<int> SiegeWaitingTimesSeconds { get; private set; } = [20, 15, 10, 5, 0, 0, 0, 0, 0, 0];
    /// <summary>Seconds without dying after which the consecutive-death counter resets.</summary>
    public int PenaltyWindowSeconds { get; private set; } = FallbackPenaltyWindowSeconds;

    public int Count => WaitingTimesSeconds.Count;

    public void Load(SqliteConnection connection)
    {
        try
        {
            var waitingTimes = new List<int>();
            var siegeWaitingTimes = new List<int>();
            var penaltyWindow = 0;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT penalty_duration, waiting_time, siege_waiting_time FROM resurrection_waiting_times ORDER BY id";
                command.Prepare();
                using (var sqliteReader = command.ExecuteReader())
                using (var reader = new SQLiteWrapperReader(sqliteReader))
                {
                    while (reader.Read())
                    {
                        penaltyWindow = reader.GetInt32("penalty_duration");
                        waitingTimes.Add(reader.GetInt32("waiting_time"));
                        siegeWaitingTimes.Add(reader.GetInt32("siege_waiting_time"));
                    }
                }
            }

            if (waitingTimes.Count == 0)
            {
                Logger.Warn("[ResurrectionGameData] resurrection_waiting_times is empty, keeping fallback ladder");
                return;
            }

            WaitingTimesSeconds = waitingTimes;
            SiegeWaitingTimesSeconds = siegeWaitingTimes;
            PenaltyWindowSeconds = penaltyWindow;
        }
        catch (Exception ex)
        {
            // Trimmed private-server DBs may lack the table entirely — keep serving the fallback ladder.
            Logger.Warn(ex, "[ResurrectionGameData] failed to load resurrection_waiting_times, keeping fallback ladder");
        }
    }

    public void PostLoad()
    {
        // Do nothing
    }

    /// <summary>Wait seconds for the given consecutive-death count, clamped to the ladder.</summary>
    public int GetWaitingTimeSeconds(int consecutiveDeathCount)
        => ResolveWaitTimeSeconds(WaitingTimesSeconds, consecutiveDeathCount);

    /// <summary>
    /// Pure index clamp shared by the DoDie countdown. Negative counts pin to the first row.
    /// </summary>
    public static int ResolveWaitTimeSeconds(IReadOnlyList<int> ladder, int consecutiveDeathCount)
    {
        if (ladder == null || ladder.Count == 0)
            return 0;
        var index = Math.Clamp(consecutiveDeathCount, 0, ladder.Count - 1);
        return ladder[index];
    }

    public static int GetFallbackWaitingTimeSeconds(int consecutiveDeathCount)
        => ResolveWaitTimeSeconds(FallbackWaitingTimesSeconds, consecutiveDeathCount);

    /// <summary>
    /// Live ladder entry for DoDie: loaded table when GameDataManager ran, else the
    /// legacy hardcoded shape (PeekInstance never constructs — headless rigs keep old behavior).
    /// </summary>
    public static int GetActiveWaitingTimeSeconds(int consecutiveDeathCount)
        => PeekInstance?.GetWaitingTimeSeconds(consecutiveDeathCount)
            ?? GetFallbackWaitingTimeSeconds(consecutiveDeathCount);

    public static int ActivePenaltyWindowSeconds
        => PeekInstance?.PenaltyWindowSeconds ?? FallbackPenaltyWindowSeconds;
}
