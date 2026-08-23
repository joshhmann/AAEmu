using System.Text;
using System.Text.Json;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M8 auditable-economy assertion — Economy loop v0 (<c>m8-economy-cycle-v0</c>)
/// LIVE hook with LEDGER RECONCILIATION ACROSS A PROCESS RESTART:
///
///   1. A REAL game server boots; ONE bot is provisioned through the shared
///      lifecycle and runs N full day cycles through the M5.1 contract ONLY
///      (BUY seeds → PLANT → GROW → HARVEST → CRAFT → SELL → DEPOSIT).
///   2. The bridge response carries a `ledger` block captured from observable
///      character state BEFORE deactivation (money / bank money2 / labor /
///      per-template bag + bank counts) plus the scenario's own conservation
///      criteria (currency/bank/stage-sums/labor/seeds EXACT reconciliation).
///   3. The deterministic save trigger lands everything to MySQL, and the
///      persisted row set is polled until it MATCHES the ledger snapshot.
///   4. The game process is FORCE-RESTARTED (kill -9 semantics via
///      <see cref="E2eStack.RestartGameServer"/> — MySQL persists), the state
///      is re-read, and the FULL ledger must be EQUAL (money + bank + every
///      item count per container): no duplication, no loss, deposits
///      survived. This is the M8 copper/labor/item conservation proof across
///      a restart.
///
/// H stays UNKNOWN: proxy/bot-functional evidence only.
/// </summary>
[Collection("e2e")]
public class EconomyDayCycleE2eTests
{
    private const string TemplateName = "m8-economy-cycle-v0";
    // Hyphen-free: NameManager rejects '-' in character names.
    private const string BotName = "M8Economy";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private sealed record PersistedLedger(
        long Money,
        long BankMoney,
        List<(int SlotType, uint TemplateId, int Count)> Items);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task EconomyCycle_LedgerReconciles_AcrossGameProcessRestart()
    {
        E2eStack.EnsureUp();

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"bot\":\"{BotName}\",\"fresh\":true,\"cycles\":2}}",
            timeoutMs: 600_000); // 2 × (merchant walk + crop growth + craft) on the live world

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";

        // Machine-readable report into the E2E logs (gate evidence convention).
        Directory.CreateDirectory(EvidenceDir);
        var reportPath = Path.Combine(EvidenceDir, "m8-economy-cycle-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(passed,
            $"economy cycle FAIL at {failStage}: {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}");

        // Stage coverage: the whole circuit ran — twice.
        var stageNames = JsonDocument.Parse(
                response.TryGetProperty("stages", out var st) ? st.ToString() : "[]").RootElement
            .EnumerateArray()
            .Select(s => s.TryGetProperty("Stage", out var sn) ? sn.GetString() ?? "" : "")
            .ToList();
        foreach (var prefix in new[] { "BUY-SEEDS-0", "PLANT-0-", "HARVEST-0-", "CRAFT-0", "SELL-0-", "DEPOSIT-MONEY-0", "DEPOSIT-MONEY-1" })
            Assert.True(stageNames.Any(n => n.StartsWith(prefix, StringComparison.Ordinal)),
                $"stage '{prefix}…' missing from the run ({string.Join(", ", stageNames)})");

        // All ledger criteria passed (currency/bank/stage-sums/labor/seeds/lifecycle).
        var failedCriteria = JsonDocument.Parse(
                response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]").RootElement
            .EnumerateArray()
            .Where(c => !(c.TryGetProperty("Passed", out var cp) && cp.GetBoolean()))
            .Select(c => c.TryGetProperty("Name", out var cn) ? cn.GetString() : "?")
            .ToList();
        Assert.True(failedCriteria.Count == 0,
            $"ledger criteria failed: {string.Join(", ", failedCriteria)}\nEvidence:\n{evidence}");

        // ---- the pre-restart ledger snapshot (bridge-captured observable state)
        var ledger = response.GetProperty("ledger");
        var characterId = ledger.GetProperty("characterId").GetUInt32();
        var expectedMoney = ledger.GetProperty("money").GetInt64();
        var expectedBankMoney = ledger.GetProperty("bankMoney").GetInt64();
        var expectedBag = ToCountMap(ledger.GetProperty("bagItems"));
        var expectedBankItems = ToCountMap(ledger.GetProperty("bankItems"));

        // The circuit closed with a REAL deposit: the bank must hold proceeds.
        Assert.True(expectedBankMoney > 0,
            $"bank deposit did not land (bankMoney={expectedBankMoney})\nEvidence:\n{evidence}");
        Assert.True(expectedBag.Count > 0 || expectedBankItems.Count > 0,
            $"ledger snapshot carries no items\nEvidence:\n{evidence}");

        // ---- deterministic save: poll MySQL until the persisted rows MATCH
        // the ledger snapshot (the disconnect save skips inventory; items
        // reach MySQL via the SaveManager pass triggered by the save cmd).
        bridge.Send("{\"cmd\":\"save\"}");
        var pre = await WaitForPersistedLedgerAsync(characterId, expectedMoney, expectedBankMoney,
            expectedBag, expectedBankItems, TimeSpan.FromSeconds(180));
        Assert.True(pre != null,
            $"MySQL never matched the ledger snapshot within the save window " +
            $"(money {expectedMoney}, bank {expectedBankMoney})\nEvidence:\n{evidence}");

        // ---- FORCE RESTART of the game process (MySQL persists)
        E2eStack.RestartGameServer();

        var post = SnapshotPersistedLedger(characterId);
        Assert.NotNull(post);

        // FULL ledger equality across the restart: money, bank, and EVERY
        // item count per container (bag slot_type=2, bank slot_type=3).
        Assert.Equal(pre!.Money, post!.Money);
        Assert.Equal(pre.BankMoney, post.BankMoney);
        Assert.Equal(pre.Items, post.Items); // ordered (slot_type, template, count) multiset

        // The auditable-economy headline: the DEPOSIT SURVIVED the kill -9.
        Assert.Equal(expectedBankMoney, post.BankMoney);
        Assert.Equal(expectedMoney, post.Money);

        var report = new StringBuilder();
        report.AppendLine("# m8-economy-cycle-v0 — ledger reconciliation across game restart");
        report.AppendLine($"- characterId {characterId}: money {pre.Money} == {post.Money}, bank {pre.BankMoney} == {post.BankMoney}");
        report.AppendLine($"- items pre == post ({pre.Items.Count} distinct container/template rows), byte-identical counts");
        report.AppendLine("- verdict: PASS (copper/bank/item conservation held across a process-level restart)");
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "m8-economy-cycle-reconcile.md"), report.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<uint, int> ToCountMap(JsonElement element)
    {
        var map = new Dictionary<uint, int>();
        foreach (var prop in element.EnumerateObject())
            map[uint.Parse(prop.Name)] = prop.Value.GetInt32();
        return map;
    }

    private static PersistedLedger? SnapshotPersistedLedger(uint characterId)
    {
        long? money = null;
        long? bankMoney = null;
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT money, money2 FROM characters WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", characterId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                money = reader.GetInt64(0);
                bankMoney = reader.GetInt64(1);
            }
        }

        if (money == null)
            return null;

        var items = new List<(int SlotType, uint TemplateId, int Count)>();
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT slot_type, template_id, SUM(count) FROM items WHERE owner = @id " +
                "GROUP BY slot_type, template_id ORDER BY slot_type, template_id";
            cmd.Parameters.AddWithValue("@id", characterId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add((reader.GetInt32(0), reader.GetUInt32(1), reader.GetInt32(2)));
        }

        return new PersistedLedger(money.Value, bankMoney!.Value, items);
    }

    private static async Task<PersistedLedger?> WaitForPersistedLedgerAsync(
        uint characterId, long expectedMoney, long expectedBankMoney,
        Dictionary<uint, int> expectedBag, Dictionary<uint, int> expectedBankItems, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = SnapshotPersistedLedger(characterId);
            if (snapshot != null &&
                snapshot.Money == expectedMoney &&
                snapshot.BankMoney == expectedBankMoney &&
                CountsMatch(snapshot.Items, 2, expectedBag) &&
                CountsMatch(snapshot.Items, 3, expectedBankItems))
            {
                return snapshot;
            }

            await Task.Delay(2000);
        }

        return null;
    }

    private static bool CountsMatch(List<(int SlotType, uint TemplateId, int Count)> items,
        int slotType, Dictionary<uint, int> expected)
    {
        var actual = items.Where(i => i.Item1 == slotType).ToDictionary(i => i.Item2, i => i.Item3);
        return actual.Count == expected.Count && actual.All(kv =>
            expected.TryGetValue(kv.Key, out var count) && count == kv.Value);
    }
}
