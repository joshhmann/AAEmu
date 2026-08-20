using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// B4 audit-trace flush sink (PlayerBotAuditSink): hermetic contract tests —
/// SQL shapes, buffer cap (drop-oldest), flush discipline (null/broken
/// connection keeps the buffer; never throws). Instances are constructed
/// directly (not the singleton) so tests are isolated. No MySQL required.
/// </summary>
public class PlayerBotAuditSinkTests
{
    [Test]
    public async Task InsertSql_TargetsPlayerbotAudit_WithCharacterAndJson()
    {
        var sql = PlayerBotAuditSink.BuildInsertSql();
        await Assert.That(sql).Contains("INSERT INTO");
        await Assert.That(sql).Contains("playerbot_audit");
        await Assert.That(sql).Contains("@character_id");
        await Assert.That(sql).Contains("@audit_json");
    }

    [Test]
    public async Task EnsureSchemaSql_CreatesPlayerbotAudit_Idempotent()
    {
        var sql = PlayerBotAuditSink.BuildEnsureSchemaSql();
        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
        await Assert.That(sql).Contains("playerbot_audit");
        var check = PlayerBotAuditSink.BuildEnsureSchemaCheckSql();
        await Assert.That(check).Contains("information_schema.TABLES");
        await Assert.That(check).Contains("playerbot_audit");
    }

    [Test]
    public async Task Enqueue_Buffers_AndDropsOldestPastCap()
    {
        var sink = new PlayerBotAuditSink();
        sink.Enqueue(1, """{"trace_id":"a"}""");
        sink.Enqueue(1, """{"trace_id":"b"}""");
        await Assert.That(sink.BufferedCount).IsEqualTo(2);

        // Empty payloads are ignored.
        sink.Enqueue(1, string.Empty);
        await Assert.That(sink.BufferedCount).IsEqualTo(2);

        // Cap pressure: buffer never exceeds MaxBufferedRecords (drop-oldest).
        for (var i = 0; i < PlayerBotAuditSink.MaxBufferedRecords + 500; i++)
            sink.Enqueue(2, $"{{\"n\":{i}}}");
        await Assert.That(sink.BufferedCount).IsEqualTo(PlayerBotAuditSink.MaxBufferedRecords);
    }

    [Test]
    public async Task Flush_NullConnection_KeepsBuffer_AndNeverThrows()
    {
        var sink = new PlayerBotAuditSink();
        sink.Enqueue(7, """{"trace_id":"x"}""");
        sink.Flush(null, null);
        await Assert.That(sink.BufferedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Flush_WithoutMySql_KeepsBuffer_AndNeverThrows()
    {
        // No MySQL in the unit rig: EnsureSchema fails gracefully (logged,
        // once-guarded) and the buffered record survives for the next tick.
        var sink = new PlayerBotAuditSink();
        sink.Enqueue(9, """{"trace_id":"y"}""");
        sink.Flush(new MySql.Data.MySqlClient.MySqlConnection("Server=127.0.0.1;Port=1;Connection Timeout=1"), null);
        await Assert.That(sink.BufferedCount).IsEqualTo(1);
        await Assert.That(sink.EnsureSchema()).IsFalse();
    }
}
