using System.Globalization;
using System.Text;
using System.Text.Json;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// DOMINION-01 slice-1 persistence proof (the dossier's PASS criterion):
/// a declared dominion survives a game-server restart.
///
///   1. Boot the REAL stack (fresh MySQL volume re-seeded from SQL/, so the
///      aaemu_game.dominions table is created by the server's own SQL updater).
///   2. DECLARE via the direct-manager-API seam at rig level — the bridge
///      "dominion"/declare op calls DominionManager.Declare, the exact call
///      the DeclareDominion special effect makes. Assert the MySQL row.
///   3. KILL + RESTART the game process. All in-memory manager state is gone;
///      the only source for the dominion is the MySQL row re-read at boot.
///   4. After reboot the manager store must contain the declared dominion
///      (bridge "dominion"/list), with expedition/tax-rate/declaredAt intact,
///      and game-restart.log must show the DominionManager load line counting
///      1 declared dominion.
///
/// Evidence: trace JSONL + verdict report under $E2E_ROOT/logs.
/// </summary>
[Collection("e2e")]
public class DominionRestartPersistenceE2eTests
{
    private const uint ZoneGroup = 33;          // o_salpimari (siege_zones id=1)
    private const uint ExpeditionId = 424242;
    private const string ExpeditionName = "DominionRig";
    private const int TaxRate = 37;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private readonly List<Dictionary<string, object?>> _trace = [];

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Dominion_Declare_Persists_Across_GameServerRestart()
    {
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // --------------------------------- PHASE 1 · DECLARE (manager API)
            var before = Trace(bridge, "list-before",
                "{\"cmd\":\"dominion\",\"op\":\"list\"}");
            var declare = Trace(bridge, "declare",
                $"{{\"cmd\":\"dominion\",\"op\":\"declare\",\"zoneGroup\":{ZoneGroup}," +
                $"\"expeditionId\":{ExpeditionId},\"expeditionName\":\"{ExpeditionName}\",\"taxRate\":{TaxRate}}}");
            Assert.Equal(ZoneGroup, (uint)ToLong(declare["declared"]!));

            // ------------------------------- PHASE 2 · MYSQL ROW (source of truth)
            var row = SnapshotDominionRow();
            Assert.True(row != null, "dominions row missing after declare");
            Assert.Equal(ExpeditionId, ToLong(row!["expedition_id"]!));
            Assert.Equal(ExpeditionName, row["expedition_name"]);
            Assert.Equal(TaxRate, ToLong(row["tax_rate"]!));
            var declaredAtDb = (DateTime)row["declared_at"]!;
            Assert.True(declaredAtDb >= startedAt.AddMinutes(-1) && declaredAtDb <= DateTime.UtcNow.AddMinutes(1),
                $"declared_at out of plausible window: {declaredAtDb:O}");

            // ------------------------------------------------ PHASE 3 · RESTART
            E2eStack.RestartGameServer();

            // ------------------------- PHASE 4 · RELOAD PROOF (from MySQL only)
            using var bridge2 = new BotDriveClient(E2eStack.BridgePort);
            var after = Trace(bridge2, "list-after-restart", "{\"cmd\":\"dominion\",\"op\":\"list\"}");
            var dominions = ((JsonElement)after["dominions"]!).EnumerateArray().ToList();
            var restored = dominions.FirstOrDefault(d => d.GetProperty("zoneGroupId").GetUInt32() == ZoneGroup);
            Assert.True(restored.ValueKind != JsonValueKind.Undefined,
                $"zone_group {ZoneGroup} missing from the manager store after restart: {after}");

            Assert.Equal(ExpeditionId, restored.GetProperty("expeditionId").GetUInt32());
            Assert.Equal(ExpeditionName, restored.GetProperty("expeditionName").GetString());
            Assert.Equal(TaxRate, restored.GetProperty("taxRate").GetInt32());
            var declaredAtRestored = restored.GetProperty("declaredAt").GetDateTime();
            Assert.True(Math.Abs((declaredAtRestored - declaredAtDb).TotalSeconds) < 2,
                $"declared_at drifted across restart: db={declaredAtDb:O} restored={declaredAtRestored:O}");

            // Load-line evidence straight from the restarted server's log.
            var restartLog = Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");
            var logText = File.Exists(restartLog) ? File.ReadAllText(restartLog) : "";
            Assert.Contains("declared dominion(s)", logText);

            await WriteEvidenceAsync(startedAt, before, declare, after);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dominion-e2e] FAILED: {ex}");
            throw;
        }
    }

    private static Dictionary<string, object?>? SnapshotDominionRow()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT zone_group_id, expedition_id, expedition_name, tax_rate, declared_at FROM dominions WHERE zone_group_id = @zg";
        cmd.Parameters.AddWithValue("@zg", ZoneGroup);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Dictionary<string, object?>
        {
            ["zone_group_id"] = Convert.ToInt64(reader["zone_group_id"]),
            ["expedition_id"] = Convert.ToUInt32(reader["expedition_id"]),
            ["expedition_name"] = reader["expedition_name"].ToString(),
            ["tax_rate"] = Convert.ToInt32(reader["tax_rate"]),
            ["declared_at"] = Convert.ToDateTime(reader["declared_at"])
        };
    }

    private Dictionary<string, object?> Trace(BotDriveClient bridge, string label, string json)
    {
        var data = bridge.Call(json);
        var dict = new Dictionary<string, object?>();
        foreach (var prop in data.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        _trace.Add(new Dictionary<string, object?>
        {
            ["at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["label"] = label,
            ["request"] = json,
            ["response"] = dict
        });
        Console.WriteLine($"[dominion-e2e] {label}: {data}");
        return dict;
    }

    private static long ToLong(object value) => value switch
    {
        JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt64(),
        JsonElement e => long.Parse(e.ToString(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
    };

    private async Task WriteEvidenceAsync(DateTime startedAt,
        Dictionary<string, object?> before, Dictionary<string, object?> declare,
        Dictionary<string, object?> after)
    {
        Directory.CreateDirectory(EvidenceDir);

        var tracePath = Path.Combine(EvidenceDir, "dominion-restart-e2e-trace.jsonl");
        var lines = _trace.Select(entry => JsonSerializer.Serialize(entry));
        await File.WriteAllLinesAsync(tracePath, lines, Encoding.UTF8);

        var report = new
        {
            gate = "DOMINION-01 slice-1 persistence",
            verdict = "PASS",
            startedAt = startedAt.ToString("O"),
            finishedAt = DateTime.UtcNow.ToString("O"),
            zoneGroup = ZoneGroup,
            expeditionId = ExpeditionId,
            taxRate = TaxRate,
            listBefore = before,
            declare,
            listAfterRestart = after
        };
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDir, "dominion-restart-e2e-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
