using AAEmu.Game.GameData;

namespace AAEmu.UnitTests.Game.GameData;

/// <summary>
/// Pins the canonical resurrection_waiting_times ladder (compact.sqlite3, read-only):
/// id1→0s, id2→5s, id3→45s, id4→90s, id5+→180s, 600s penalty window, clamp beyond 10 deaths.
/// The old hardcoded ladder ([15,30,60,90,...] + 5min window) cannot produce these values.
/// </summary>
public class ResurrectionGameDataTests : SqliteTestBase
{
    private const string CreateTableSql = @"
        CREATE TABLE resurrection_waiting_times (
            id INT,
            penalty_duration INT,
            waiting_time INT,
            siege_waiting_time INT
        )";

    private static readonly (int Id, int Penalty, int Wait, int Siege)[] CanonicalRows =
    [
        (1, 600, 0, 20),
        (2, 600, 5, 15),
        (3, 600, 45, 10),
        (4, 600, 90, 5),
        (5, 600, 180, 0),
        (6, 600, 180, 0),
        (7, 600, 180, 0),
        (8, 600, 180, 0),
        (9, 600, 180, 0),
        (10, 600, 180, 0),
    ];

    private ResurrectionGameData LoadCanonical()
    {
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = CreateTableSql;
            command.ExecuteNonQuery();
        }
        foreach (var (id, penalty, wait, siege) in CanonicalRows)
        {
            using (var command = Connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO resurrection_waiting_times (id, penalty_duration, waiting_time, siege_waiting_time)"
                    + $" VALUES ({id}, {penalty}, {wait}, {siege})";
                command.ExecuteNonQuery();
            }
        }

        var data = new ResurrectionGameData();
        data.Load(Connection);
        return data;
    }

    [Test]
    public async Task FirstDeath_HasNoWait_Id1IsZeroSeconds()
    {
        var data = LoadCanonical();
        await Assert.That(data.GetWaitingTimeSeconds(0)).IsEqualTo(0);
    }

    [Test]
    public async Task ThirdDeath_Waits45Seconds_Id3()
    {
        var data = LoadCanonical();
        await Assert.That(data.GetWaitingTimeSeconds(2)).IsEqualTo(45);
    }

    [Test]
    public async Task FifthDeathOnwards_Waits180Seconds()
    {
        var data = LoadCanonical();
        await Assert.That(data.GetWaitingTimeSeconds(4)).IsEqualTo(180);
        await Assert.That(data.GetWaitingTimeSeconds(9)).IsEqualTo(180);
    }

    [Test]
    public async Task BeyondTenDeaths_ClampsToLastRow()
    {
        var data = LoadCanonical();
        await Assert.That(data.GetWaitingTimeSeconds(10)).IsEqualTo(180);
        await Assert.That(data.GetWaitingTimeSeconds(99)).IsEqualTo(180);
    }

    [Test]
    public async Task PenaltyWindow_Is600Seconds()
    {
        var data = LoadCanonical();
        await Assert.That(data.PenaltyWindowSeconds).IsEqualTo(600);
        await Assert.That(data.Count).IsEqualTo(10);
    }

    [Test]
    public async Task EmptyTable_FallsBackToLegacyHardcodedLadder()
    {
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = CreateTableSql;
            command.ExecuteNonQuery();
        }

        var data = new ResurrectionGameData();
        data.Load(Connection);

        // Legacy shape: [15,30,60,90,120,150,180,210,240] + 5min window
        await Assert.That(data.GetWaitingTimeSeconds(0)).IsEqualTo(15);
        await Assert.That(data.GetWaitingTimeSeconds(2)).IsEqualTo(60);
        await Assert.That(data.PenaltyWindowSeconds).IsEqualTo(300);
    }

    [Test]
    public async Task MissingTable_FallsBackWithoutThrowing()
    {
        var data = new ResurrectionGameData();
        data.Load(Connection);

        await Assert.That(data.GetWaitingTimeSeconds(0)).IsEqualTo(15);
        await Assert.That(data.PenaltyWindowSeconds).IsEqualTo(300);
    }

    [Test]
    public async Task ResolveWaitTimeSeconds_ClampsIndex()
    {
        // Same pure clamp DoDie's countdown uses (via GetWaitingTimeSeconds).
        await Assert.That(ResurrectionGameData.ResolveWaitTimeSeconds([0, 5, 45], 0)).IsEqualTo(0);
        await Assert.That(ResurrectionGameData.ResolveWaitTimeSeconds([0, 5, 45], 2)).IsEqualTo(45);
        await Assert.That(ResurrectionGameData.ResolveWaitTimeSeconds([0, 5, 45], 7)).IsEqualTo(45);
    }
}
