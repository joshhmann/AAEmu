using System.Globalization;
using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.IntegrationTests.E2e;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M8 C5 slice-2 — single kill-9 restart mid-half-day with equality (the
/// user-approved C5-exit slice): TWO villagers (farmer + crafter) run one
/// half-day through the slice-1 composer (<see cref="VillageDayCycle"/>, via
/// the live bridge seam) on the isolated lane, then the game process takes a
/// real kill-9 restart and the persisted state must be EQUAL pre/post:
/// schedule anchors + lastPhase + profession (B4 playerbot_metadata rows,
/// asserted directly like B4BotRestartPersistenceE2eTests) + inventory +
/// ledger (M51 byte-equality style + the EconomyDayCycle money/bank/item
/// reconciliation) + the B4-flushed M5 audit trail (playerbot_audit rows).
///
/// Flow: cold boot (restart first, the M51 warm-merchant mitigation) →
/// bridge scenario m8-village-day-s1 (fresh rows, PASS + all criteria) →
/// bridge "save" → poll MySQL until the persisted rows MATCH the bridge
/// ledger for BOTH bots AND the audit sink flushed every terminal trace
/// record → snapshot (metadata + audit + items + characters + labor) →
/// kill -9 restart of ONLY the game process → re-read and require EQUALITY.
/// Evidence JSON lands in $E2E_ROOT/logs/village-day-restart-report.json.
///
/// H stays UNKNOWN: proxy/bot-functional evidence only.
/// </summary>
[Collection("e2e")]
public class VillageDayCycleRestartE2eTests
{
    private const string FarmerBot = "m8vfarmer";
    private const string CrafterBot = "m8vcrafter";
    private static readonly string[] BotUsernames =
    [
        "bot_managed_" + FarmerBot,
        "bot_managed_" + CrafterBot,
    ];

    private sealed record MetadataSnapshot(uint CharacterId, string Personality, string Profession, bool HasHome,
        uint HomeWorldId, uint HomeZoneId, float HomeX, float HomeY, float HomeZ,
        string Schedule, string BehaviorConfig, string PlannerState);

    private sealed record AuditSnapshot(ulong Id, uint CharacterId, string AuditJson);

    private sealed record CharacterSnapshot(uint Id, uint AccountId, string Name, uint WorldId, uint ZoneId,
        float X, float Y, float Z, byte Level, long Money, long BankMoney, int Labor);

    private sealed record ItemRow(int SlotType, uint TemplateId, int Count);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task VillageHalfDay_ScheduleProfessionInventoryLedger_SurviveKill9_Equal()
    {
        var startedAt = DateTime.UtcNow;
        var cycleId = $"m8v1-c5s2-{startedAt:yyyyMMdd-HHmmss}";
        E2eStack.EnsureUp();

        // Cold-world contract (M51 §1): scripted walk/merchant legs only
        // complete synchronously on a FRESH boot. Restart first, like B4.
        E2eStack.RestartGameServer();

        string failStage = "", failReason = "", evidenceText = "";
        var scenarioPassed = false;

        try
        {
            // ---------------------------------------------------- 1. HALF-DAY
            JsonElement response;
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                response = bridge.Call(
                    $"{{\"cmd\":\"scenario\",\"template\":\"{VillageDayCycle.ScenarioName}\"," +
                    $"\"farmer\":\"{FarmerBot}\",\"crafter\":\"{CrafterBot}\",\"cycleId\":\"{cycleId}\"}}",
                    timeoutMs: 600_000);

                scenarioPassed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
                failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() ?? "" : "";
                failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() ?? "" : "";
                evidenceText = response.TryGetProperty("evidence", out var ev) ? ev.GetString() ?? "" : "";

                Assert.True(scenarioPassed,
                    $"village half-day FAILED at {failStage}: {failReason}\nEvidence:\n{evidenceText}");

                var failedCriteria = response.TryGetProperty("criteria", out var cr)
                    ? cr.EnumerateArray()
                        .Where(c => !(c.TryGetProperty("Passed", out var cp) && cp.GetBoolean()))
                        .Select(c => c.TryGetProperty("Name", out var cn) ? cn.GetString() : "?")
                        .ToList()
                    : [];
                Assert.True(failedCriteria.Count == 0,
                    $"village ledger criteria failed: {string.Join(", ", failedCriteria)}\nEvidence:\n{evidenceText}");

                // Durable-state trigger: the REAL save pass (M51 §2) — every
                // character / item / metadata / audit row flushed before the kill.
                var saveAck = bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
                Assert.True(saveAck.TryGetProperty("saved", out var savedEl) && savedEl.GetBoolean(),
                    "bridge save pass did not complete before the kill");
            }

            // ------------------------------------------------- 2. EXPECTATIONS
            // Per-bot expectations, straight from the bridge payload (the same
            // envelope shape as the economy handler: villagers[] + ledgers[]).
            var villagers = response.GetProperty("villagers").EnumerateArray().ToList();
            Assert.Equal(2, villagers.Count);
            var ledgers = response.GetProperty("ledgers").EnumerateArray().ToList();
            Assert.Equal(2, ledgers.Count);

            var expected = new Dictionary<uint, BotExpectation>();
            foreach (var villager in villagers)
            {
                var characterId = villager.GetProperty("characterId").GetUInt32();
                var profession = villager.GetProperty("profession").GetString() ?? "";
                var lastPhase = villager.TryGetProperty("lastPhase", out var lp) ? lp.GetString() ?? "" : "";
                var traceCount = villager.GetProperty("traceCount").GetInt32();
                var ledger = ledgers.Single(l => l.GetProperty("characterId").GetUInt32() == characterId);
                expected[characterId] = new BotExpectation(
                    profession, lastPhase, traceCount,
                    ledger.GetProperty("money").GetInt64(),
                    ledger.GetProperty("bankMoney").GetInt64(),
                    ToCountMap(ledger.GetProperty("bagItems")),
                    ToCountMap(ledger.GetProperty("bankItems")));
            }

            Assert.Contains(expected.Values, e => e.Profession == "farmer");
            Assert.Contains(expected.Values, e => e.Profession == "crafter");

            // --------------------------------------- 3. WAIT FOR PERSISTENCE
            // Poll until MySQL MATCHES the bridge ledger for both bots AND
            // every terminal trace record reached playerbot_audit (the sink
            // flushes on the SaveManager tick — the rows are not there yet on
            // the first save ack).
            var persisted = await WaitForPersistedVillageAsync(expected, TimeSpan.FromSeconds(180));
            Assert.True(persisted,
                "MySQL never matched the village ledger within the save window " +
                $"(bots {string.Join(",", expected.Keys)})\nEvidence:\n{evidenceText}");

            // ------------------------------------------------- 4. PRE SNAPSHOT
            var preMeta = expected.Keys.ToDictionary(id => id, SnapshotMetadata);
            var preAudit = expected.Keys.ToDictionary(id => id, SnapshotAuditRows);
            var preChars = expected.Keys.ToDictionary(id => id, SnapshotCharacter);
            var preItems = expected.Keys.ToDictionary(id => id, SnapshotItems);

            // Content: the rows actually carry the day's state (not just equal
            // emptiness) — profession per villager, anchors + lastPhase in the
            // schedule JSON, audit rows covering every terminal trace record.
            foreach (var (characterId, exp) in expected)
            {
                var meta = preMeta[characterId];
                Assert.NotNull(meta);
                Assert.Equal(exp.Profession, meta!.Profession);
                Assert.True(meta.HasHome, $"bot {characterId}: playerbot_metadata.has_home must be 1");
                Assert.Contains("\"anchors\"", meta.Schedule);
                Assert.Contains("\"workStart\":8", meta.Schedule);
                Assert.Contains($"\"lastPhase\":\"{exp.LastPhase}\"", meta.Schedule);
                Assert.True(exp.TraceCount > 0, $"bot {characterId}: half-day must emit trace records");
                Assert.Equal(exp.TraceCount, preAudit[characterId].Count);
                Assert.NotNull(preChars[characterId]);
            }

            // --------------------------------------------------- 5. KILL -9
            // StopGameServer kills the process tree — only MySQL survives.
            E2eStack.RestartGameServer();

            // ------------------------------------------------ 6. POST ASSERTS
            foreach (var (characterId, exp) in expected)
            {
                // -- metadata: byte-equal (anchors + lastPhase + profession ride here).
                var postMeta = SnapshotMetadata(characterId);
                Assert.NotNull(postMeta);
                Assert.Equal(preMeta[characterId], postMeta);

                // -- audit trail: byte-equal rows (same ids, same JSON).
                var postAudit = SnapshotAuditRows(characterId);
                Assert.Equal(preAudit[characterId], postAudit);

                // -- characters row: identity + balances + position intact.
                var postChar = SnapshotCharacter(characterId);
                Assert.NotNull(postChar);
                AssertRestartCharacterIntact(preChars[characterId]!, postChar!);

                // -- inventory + ledger: money, bank, every item count per container.
                var postItems = SnapshotItems(characterId);
                Assert.Equal(preItems[characterId], postItems);
                Assert.Equal(exp.Money, postChar!.Money);
                Assert.Equal(exp.BankMoney, postChar.BankMoney);
            }
        }
        finally
        {
            await CleanupAsync();
        }

        await WriteReportAsync(startedAt, cycleId, scenarioPassed, failStage, failReason, evidenceText);
    }

    private sealed record BotExpectation(string Profession, string LastPhase, int TraceCount,
        long Money, long BankMoney, Dictionary<uint, int> Bag, Dictionary<uint, int> Bank);

    // ------------------------------------------------------------- snapshots

    private static Dictionary<uint, int> ToCountMap(JsonElement element)
    {
        var map = new Dictionary<uint, int>();
        foreach (var prop in element.EnumerateObject())
            map[uint.Parse(prop.Name, CultureInfo.InvariantCulture)] = prop.Value.GetInt32();
        return map;
    }

    /// <summary>B4 playerbot_metadata row for one bot (null when absent).</summary>
    private static MetadataSnapshot? SnapshotMetadata(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, personality, profession, has_home, home_world_id, home_zone_id, " +
            "home_x, home_y, home_z, schedule, behavior_config, planner_state " +
            "FROM playerbot_metadata WHERE character_id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new MetadataSnapshot(
            reader.GetUInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
            reader.GetUInt32(4), reader.GetUInt32(5),
            reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8),
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            reader.IsDBNull(11) ? string.Empty : reader.GetString(11));
    }

    /// <summary>B4 playerbot_audit rows for one bot, id-ordered (created_at is
    /// clock evidence, not state — excluded from equality like M51 plant_time
    /// tolerance; the (id, character, JSON) triple is the asserted state).</summary>
    private static List<AuditSnapshot> SnapshotAuditRows(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, character_id, audit_json FROM playerbot_audit " +
            "WHERE character_id = @id ORDER BY id";
        cmd.Parameters.AddWithValue("@id", characterId);
        var rows = new List<AuditSnapshot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new AuditSnapshot(reader.GetUInt64(0), reader.GetUInt32(1), reader.GetString(2)));
        return rows;
    }

    private static CharacterSnapshot? SnapshotCharacter(uint characterId)
    {
        CharacterSnapshot? snap = null;
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, account_id, name, world_id, zone_id, x, y, z, level, money, money2 " +
                "FROM characters WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", characterId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                snap = new CharacterSnapshot(
                    reader.GetUInt32(0), reader.GetUInt32(1), reader.GetString(2),
                    reader.GetUInt32(3), reader.GetUInt32(4),
                    reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7),
                    reader.GetByte(8), reader.GetInt64(9), reader.GetInt64(10), 0);
            }
        }

        if (snap == null)
            return null;

        // Labor persists on the accounts row (Character.LaborPower setter →
        // AccountManager.UpdateLabor), not on characters.
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT labor FROM accounts WHERE account_id = @accountId";
            cmd.Parameters.AddWithValue("@accountId", snap.AccountId);
            var labor = Convert.ToInt32(cmd.ExecuteScalar());
            return snap with { Labor = labor };
        }
    }

    /// <summary>Item multiset per container (the economy reconciliation shape:
    /// (slot_type, template, count), ordered).</summary>
    private static List<ItemRow> SnapshotItems(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT slot_type, template_id, SUM(count) FROM items WHERE owner = @id " +
            "GROUP BY slot_type, template_id ORDER BY slot_type, template_id";
        cmd.Parameters.AddWithValue("@id", characterId);
        var rows = new List<ItemRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new ItemRow(reader.GetInt32(0), reader.GetUInt32(1), reader.GetInt32(2)));
        return rows;
    }

    /// <summary>
    /// THE restart assertion for the character row: identity + balances +
    /// labor intact, position unmoved (M51 epsilon style — deactivated bots
    /// never step, so any drift IS the defect).
    /// </summary>
    private static void AssertRestartCharacterIntact(CharacterSnapshot pre, CharacterSnapshot post)
    {
        Assert.Equal(pre.Id, post.Id);
        Assert.Equal(pre.AccountId, post.AccountId);
        Assert.Equal(pre.Name, post.Name);
        Assert.Equal(pre.Level, post.Level);
        Assert.Equal(pre.Money, post.Money);
        Assert.Equal(pre.BankMoney, post.BankMoney);
        Assert.Equal(pre.Labor, post.Labor);
        Assert.Equal(pre.WorldId, post.WorldId);
        Assert.True(MathF.Abs(pre.X - post.X) < 0.01f &&
                    MathF.Abs(pre.Y - post.Y) < 0.01f &&
                    MathF.Abs(pre.Z - post.Z) < 0.01f,
            $"character {pre.Name} moved across restart: " +
            $"({pre.X},{pre.Y},{pre.Z}) → ({post.X},{post.Y},{post.Z})");
    }

    private static bool LedgerMatches(uint characterId, BotExpectation exp)
    {
        var snap = SnapshotCharacter(characterId);
        if (snap == null || snap.Money != exp.Money || snap.BankMoney != exp.BankMoney)
            return false;
        var items = SnapshotItems(characterId);
        return CountsMatch(items, 2, exp.Bag) && CountsMatch(items, 3, exp.Bank);
    }

    private static bool CountsMatch(List<ItemRow> items, int slotType, Dictionary<uint, int> expected)
    {
        var actual = items.Where(i => i.SlotType == slotType).ToDictionary(i => i.TemplateId, i => i.Count);
        return actual.Count == expected.Count && actual.All(kv =>
            expected.TryGetValue(kv.Key, out var count) && count == kv.Value);
    }

    /// <summary>
    /// Polls until BOTH bots' persisted rows match the bridge ledger (money /
    /// bank / bag slot_type=2 / bank slot_type=3) AND every terminal trace
    /// record reached playerbot_audit AND the metadata rows carry the
    /// profession. Mirrors the economy save-window poll, extended to the
    /// village's two bots + audit + metadata.
    /// </summary>
    private static async Task<bool> WaitForPersistedVillageAsync(
        Dictionary<uint, BotExpectation> expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ready = true;
            foreach (var (characterId, exp) in expected)
            {
                if (!LedgerMatches(characterId, exp))
                {
                    ready = false;
                    break;
                }

                if (SnapshotAuditRows(characterId).Count != exp.TraceCount)
                {
                    ready = false;
                    break;
                }

                var meta = SnapshotMetadata(characterId);
                if (meta == null || meta.Profession != exp.Profession ||
                    !meta.Schedule.Contains("\"anchors\"", StringComparison.Ordinal))
                {
                    ready = false;
                    break;
                }
            }

            if (ready)
                return true;
            await Task.Delay(2000);
        }

        return false;
    }

    // ---------------------------------------------------------------- report

    private async Task WriteReportAsync(DateTime startedAt, string cycleId, bool scenarioPassed,
        string failStage, string failReason, string evidenceText)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            slice = "M8 C5 slice-2 — single kill-9 restart mid-half-day with equality",
            card = "C5 day-integration exit slice: 2 villagers, 1 restart, schedule/profession/inventory/ledger equality",
            path = "live bridge seam: HandleVillageDayCycleScenario → VillageDayCycle.Run (slice-1 composer, unchanged)",
            scenario = VillageDayCycle.ScenarioName,
            cycleId,
            bots = new[] { FarmerBot, CrafterBot },
            verdict = scenarioPassed ? "PASS" : "FAIL",
            failStage,
            failReason,
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN",
            source_sha = E2eStack.SourceRevision,
            restarted_at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            asserted_rows = new[]
            {
                "playerbot_metadata (B4 direct): character/personality/profession/has_home/home world+zone+xyz/schedule(JSON: anchors+lastPhase)/behavior/planner — byte-equal",
                "playerbot_audit (B4 sink): (id, character_id, audit_json) per bot, id-ordered — byte-equal (created_at excluded: clock, not state)",
                "characters: id/account/name/level/money/money2/world + position (M51 epsilon) — equal",
                "accounts: labor — equal",
                "items (economy shape): (slot_type, template, count) multiset per bot — equal"
            },
            evidence = evidenceText.Length > 4000 ? evidenceText[..4000] + "…(truncated)" : evidenceText
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "village-day-restart-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --------------------------------------------------------------- cleanup

    /// <summary>Removes every row this run created (scoped strictly by the bot
    /// character id chain + managed usernames), leaving the shared stack clean.</summary>
    private static async Task CleanupAsync()
    {
        try
        {
            foreach (var username in BotUsernames)
            {
                var charIds = ResolveCharacterIds(username);
                if (charIds.Count > 0)
                {
                    using var conn = E2eStack.OpenDb("aaemu_game");
                    foreach (var sql in new[]
                             {
                                 "DELETE FROM playerbot_audit WHERE character_id = @id",
                                 "DELETE FROM playerbot_metadata WHERE character_id = @id",
                                 "DELETE FROM doodads WHERE owner_id = @id",
                                 "DELETE FROM items WHERE owner = @id",
                                 "DELETE FROM item_containers WHERE owner_id = @id",
                             })
                    {
                        foreach (var charId in charIds)
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = sql;
                            cmd.Parameters.AddWithValue("@id", charId);
                            try { await cmd.ExecuteNonQueryAsync(); } catch { /* FK-tolerant */ }
                        }
                    }
                }

                using var conn2 = E2eStack.OpenDb("aaemu_game");
                foreach (var sql in new[]
                         {
                             "DELETE FROM quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM completed_quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username)"
                         })
                {
                    using var cmd2 = conn2.CreateCommand();
                    cmd2.CommandText = sql;
                    cmd2.Parameters.AddWithValue("@username", username);
                    try { await cmd2.ExecuteNonQueryAsync(); } catch { /* FK-tolerant */ }
                }

                using var loginConn = E2eStack.OpenDb("aaemu_login");
                using var delUser = loginConn.CreateCommand();
                delUser.CommandText = "DELETE FROM users WHERE username = @username";
                delUser.Parameters.AddWithValue("@username", username);
                await delUser.ExecuteNonQueryAsync();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[village-day-restart] cleanup failed (non-fatal): {e.Message}");
        }
    }

    private static List<uint> ResolveCharacterIds(string username)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.id FROM characters c JOIN aaemu_login.users u ON c.account_id = u.id WHERE u.username = @username";
        cmd.Parameters.AddWithValue("@username", username);
        var ids = new List<uint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetUInt32(0));
        return ids;
    }
}
