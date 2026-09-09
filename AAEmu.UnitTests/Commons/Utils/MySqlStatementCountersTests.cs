using AAEmu.Commons.Utils.DB;

namespace AAEmu.UnitTests.Commons.Utils;

[NotInParallel]
public class MySqlStatementCountersTests
{
    [Test]
    public async Task RecordStopped_NonStatementOperation_IsIgnored()
    {
        var before = MySqlStatementCounters.Snapshot();
        MySqlStatementCounters.RecordStopped("Connection (pooled)", "aaemu_game", false);
        var after = MySqlStatementCounters.Snapshot();
        await Assert.That(after.Statements).IsEqualTo(before.Statements);
        await Assert.That(after.FailedStatements).IsEqualTo(before.FailedStatements);
    }

    [Test]
    public async Task RecordStopped_Statement_CountsTotalAndPerStore()
    {
        var before = MySqlStatementCounters.Snapshot();
        MySqlStatementCounters.RecordStopped("SQL Statement", "aaemu_game", false);
        MySqlStatementCounters.RecordStopped("SQL Statement", "aaemu_login", false);
        MySqlStatementCounters.RecordStopped("SQL Statement", null, false);
        var after = MySqlStatementCounters.Snapshot();
        await Assert.That(after.Statements - before.Statements).IsEqualTo(3);
        await Assert.That(after.GameStatements - before.GameStatements).IsEqualTo(1);
        await Assert.That(after.LoginStatements - before.LoginStatements).IsEqualTo(1);
        await Assert.That(after.OtherStatements - before.OtherStatements).IsEqualTo(1);
        await Assert.That(after.FailedStatements - before.FailedStatements).IsEqualTo(0);
    }

    [Test]
    public async Task RecordStopped_FailedStatement_CountsFailed()
    {
        var before = MySqlStatementCounters.Snapshot();
        MySqlStatementCounters.RecordStopped("SQL Statement", "aaemu_game", true);
        var after = MySqlStatementCounters.Snapshot();
        await Assert.That(after.Statements - before.Statements).IsEqualTo(1);
        await Assert.That(after.FailedStatements - before.FailedStatements).IsEqualTo(1);
    }

    [Test]
    public async Task Snapshot_PerStoreSumsToTotal()
    {
        var before = MySqlStatementCounters.Snapshot();
        MySqlStatementCounters.RecordStopped("SQL Statement", "aaemu_game", false);
        MySqlStatementCounters.RecordStopped("SQL Statement", "AAEMU_LOGIN", false);
        var after = MySqlStatementCounters.Snapshot();
        await Assert.That(after.Statements - before.Statements)
            .IsEqualTo((after.GameStatements - before.GameStatements)
                + (after.LoginStatements - before.LoginStatements)
                + (after.OtherStatements - before.OtherStatements));
    }

    [Test]
    public async Task EnsureStarted_IsIdempotentAndMarksStarted()
    {
        MySqlStatementCounters.EnsureStarted();
        MySqlStatementCounters.EnsureStarted();
        await Assert.That(MySqlStatementCounters.IsStarted).IsTrue();
    }
}
