using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Siege;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// B5 DOMINION-01 S run — declare → tax-rate change round-trip → phase-cron
/// advance (Peace→Declare→Warmup→Siege→Payoff announcements) → kill-9
/// persistence, against grounded reference data (siege_zones 6 /
/// siege_settings 11 / siege_plans 158) and the additive
/// aaemu_game.dominions table, inside the stated soak budget.
///
/// Legs (one ordered [Fact]; each leg records PASS/FAIL numbers into the
/// evidence report):
///   0. GROUND — canonical compact.sqlite3 md5 + live manager counts
///      6/11/158 + boot load line.
///   1. DECLARE — bridge declare seam (the exact DominionManager.Declare call
///      the DeclareDominion special effect makes) → MySQL row assert.
///   2. TAX — CSUpdateDominionTaxRatePacket wire-codec mirror (id UInt16 +
///      taxRate Int32, the exact two reads the C2G handler performs) →
///      tax-rate change through the persist path (declare upsert) → MySQL row
///      + SCDominionTaxRatePacket echo-codec assert. The owner-gated live C2G
///      send (authenticated owner client in a seeded expedition) is NOT
///      covered here — no expedition seed path exists inside this budget;
///      recorded as follow-up. The pure allow/refuse matrix runs in-lane as
///      DominionScheduleTests TaxRate_* unit tests.
///   3. PHASE — live bridge phase snapshot (the same GetCurrentPhase the 15 s
///      cron announces) cross-checked against the boot "Siege phase:" log
///      lines for all 6 zones → ≥70 s stability watch (≥4 cron ticks) →
///      SCSiegeAlertPacket echo-codec assert. A real-time week advance is
///      outside any S budget; the full Peace→Declare→Warmup→Siege interval
///      math plus payoff anchors run in-lane as DominionScheduleTests phase
///      unit tests against the real schedule shape.
///   4. KILL-9 — explicit SIGKILL of the PID-verified game process (no pkill),
///      /proc-confirmed exit, MySQL row intact, reboot, reload equality.
///
/// Siege combat is explicitly descoped: this file MUST NOT gain combat
/// assertions — any combat assertion here FAILS scope.
///
/// Isolation: this test only touches the lane selected by E2E_ROOT +
/// E2E_*_PORT + COMPOSE_PROJECT_NAME (own ports/DB volume/compose project).
/// Kills are PID-scoped to processes whose /proc cwd sits under this lane's
/// E2E_ROOT — unscoped pkill is banned (2026-09-06 incident rule).
/// </summary>
[Collection("e2e")]
public class DominionSoakCycleE2eTests
{
    private const uint ZoneGroup = 33; // o_salpimari (siege_zones id=1)
    private const uint ExpeditionId = 424243;
    private const string ExpeditionName = "DominionSRun";
    private const int TaxRateInitial = 37;
    private const int TaxRateChanged = 55;
    private const string CanonicalSqliteMd5 = "78b3bdbf038db3b927056106efdf91af";

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private readonly List<Dictionary<string, object?>> _trace = [];

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Dominion_SoakCycle_Declare_Tax_Phase_Kill9_Persist()
    {
        var startedAt = DateTime.UtcNow;
        var legNotes = new Dictionary<string, object?>();
        E2eStack.EnsureUp();

        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ------------------------------------------------- LEG 0 · GROUND
            var runtimeMd5 = Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(E2eStack.RuntimeSqlite)));
            Assert.Equal(CanonicalSqliteMd5, E2eStack.CanonicalSqliteMd5);
            Assert.Equal(CanonicalSqliteMd5, runtimeMd5);
            var phase0 = Trace(bridge, "phase-ground", "{\"cmd\":\"dominion\",\"op\":\"phase\"}");
            Assert.Equal(6, ((JsonElement)phase0["siegeZones"]!).GetInt32());
            Assert.Equal(11, ((JsonElement)phase0["siegeSettings"]!).GetInt32());
            Assert.Equal(158, ((JsonElement)phase0["siegePlans"]!).GetInt32());
            var bootLog = ReadLog("game.log");
            Assert.Contains("Loaded 6 siege zones, 11 siege settings, 158 siege plans", bootLog);
            legNotes["ground"] = "md5 ok; live 6/11/158; boot load line present";

            // ------------------------------------------------ LEG 1 · DECLARE
            var declareStarted = DateTime.UtcNow;
            var declare = Trace(bridge, "declare",
                $"{{\"cmd\":\"dominion\",\"op\":\"declare\",\"zoneGroup\":{ZoneGroup}," +
                $"\"expeditionId\":{ExpeditionId},\"expeditionName\":\"{ExpeditionName}\",\"taxRate\":{TaxRateInitial}}}");
            Assert.Equal(ZoneGroup, (uint)ToLong(declare["declared"]!));
            var row1 = SnapshotDominionRow();
            var declareLatency = DateTime.UtcNow - declareStarted;
            Assert.True(row1 != null, "dominions row missing after declare");
            Assert.True(declareLatency < TimeSpan.FromSeconds(5),
                $"declare→row latency {declareLatency.TotalSeconds:F1}s exceeds 5s budget");
            AssertDominionRow(row1!, ExpeditionId, ExpeditionName, TaxRateInitial, startedAt);
            legNotes["declare"] = $"row in {declareLatency.TotalMilliseconds:F0}ms";

            // ------------------------------------ LEG 2 · TAX-RATE ROUND-TRIP
            // Wire-codec mirror of CSUpdateDominionTaxRatePacket.Read: the
            // handler performs exactly ReadUInt16 (id) then ReadInt32
            // (taxRate) on the client bytes (CSUpdateDominionTaxRatePacket.cs).
            var csBytes = new PacketStream();
            csBytes.Write((ushort)ZoneGroup);
            csBytes.Write(TaxRateChanged);
            csBytes.Rollback();
            Assert.Equal(ZoneGroup, csBytes.ReadUInt16());
            Assert.Equal(TaxRateChanged, csBytes.ReadInt32());

            // Tax-rate change through the persist path (declare upsert =
            // INSERT ... ON DUPLICATE KEY UPDATE, the same PersistDominion
            // the tax handler calls after validation).
            var change = Trace(bridge, "tax-change",
                $"{{\"cmd\":\"dominion\",\"op\":\"declare\",\"zoneGroup\":{ZoneGroup}," +
                $"\"expeditionId\":{ExpeditionId},\"expeditionName\":\"{ExpeditionName}\",\"taxRate\":{TaxRateChanged}}}");
            Assert.Equal(ZoneGroup, (uint)ToLong(change["declared"]!));
            var row2 = SnapshotDominionRow();
            Assert.True(row2 != null, "dominions row missing after tax change");
            Assert.Equal(TaxRateChanged, ToLong(row2!["tax_rate"]!));
            var listed = FindListed(bridge, "list-after-tax-change", ZoneGroup);
            Assert.Equal(TaxRateChanged, listed.GetProperty("taxRate").GetInt32());

            // Echo-codec: what the owner client receives on SCDominionTaxRate.
            var echo = new SCDominionTaxRatePacket((ushort)ZoneGroup, TaxRateChanged).Write(new PacketStream());
            echo.Rollback();
            Assert.Equal(ZoneGroup, echo.ReadUInt16());
            Assert.Equal(TaxRateChanged, echo.ReadInt32());
            legNotes["tax"] = $"cs-codec ok; store {TaxRateInitial}->{TaxRateChanged}; sc-echo ok";

            // ------------------------------------------------ LEG 3 · PHASE
            var phasesA = Trace(bridge, "phase-before-watch", "{\"cmd\":\"dominion\",\"op\":\"phase\"}");
            var zonesA = ((JsonElement)phasesA["zones"]!).EnumerateArray().ToList();
            Assert.Equal(6, zonesA.Count);
            var bootPhases = ParsePhaseLines(ReadLog("game.log"));
            foreach (var z in zonesA)
            {
                var zg = z.GetProperty("zoneGroupId").GetUInt32();
                var name = z.GetProperty("phaseName").GetString();
                Assert.True(bootPhases.TryGetValue(zg, out var logged) && logged == name,
                    $"zone_group {zg}: live phase {name} missing/mismatched in boot log");
                // Null cycle Sunday (mid-week peace) implies Peace; the
                // converse need not hold (rotation can idle a zone in-window).
                if (z.GetProperty("cycleSunday").ValueKind == JsonValueKind.Null)
                    Assert.Equal("Peace", name);
                if (z.TryGetProperty("anchors", out var anchors) && anchors.ValueKind != JsonValueKind.Null)
                {
                    var siegeMinutes = anchors.GetProperty("siegeMinutes").GetDouble();
                    Assert.Equal(90.0, siegeMinutes);
                    Assert.Equal(DayOfWeek.Sunday, anchors.GetProperty("payoff").GetDateTime().DayOfWeek);
                }
            }

            // Alert-codec: the cron's SCSiegeAlertPacket broadcast shape.
            var alert = new SCSiegeAlertPacket((ushort)ZoneGroup, (byte)SiegePhase.Siege).Write(new PacketStream());
            alert.Rollback();
            Assert.Equal(ZoneGroup, alert.ReadUInt16());
            Assert.Equal((byte)SiegePhase.Siege, alert.ReadByte());

            // Stability watch: ≥70 s covers ≥4 ticks at the 15 s cadence.
            var payoffBefore = CountPayoffLines(ReadLog("game.log"));
            await Task.Delay(TimeSpan.FromSeconds(75));
            var phasesB = Trace(bridge, "phase-after-watch", "{\"cmd\":\"dominion\",\"op\":\"phase\"}");
            var phasesAByZone = zonesA.ToDictionary(z => z.GetProperty("zoneGroupId").GetUInt32(),
                z => z.GetProperty("phaseName").GetString());
            var zonesB = ((JsonElement)phasesB["zones"]!).EnumerateArray().ToList();
            foreach (var z in zonesB)
            {
                var zg = z.GetProperty("zoneGroupId").GetUInt32();
                Assert.True(phasesAByZone.TryGetValue(zg, out var before)
                    && before == z.GetProperty("phaseName").GetString(),
                    $"zone_group {zg} changed phase mid-watch ({before} -> {z.GetProperty("phaseName").GetString()}); " +
                    "a real schedule boundary inside the 75s window is a re-run, not a pass");
            }
            var watchLog = ReadLog("game.log");
            var watchLines = watchLog.Split('\n');
            var dominionErrors = watchLines
                .Where(l => l.Contains("[ERROR]", StringComparison.Ordinal)
                    && l.Contains("DominionManager", StringComparison.Ordinal)).ToList();
            Assert.True(dominionErrors.Count == 0,
                "DominionManager ERROR lines: " + string.Join(" | ", dominionErrors.Take(5)));
            var totalErrorLines = watchLines.Count(l => l.Contains("[ERROR]", StringComparison.Ordinal));
            var payoffAfter = CountPayoffLines(watchLog);
            Assert.True(payoffAfter - payoffBefore <= 1,
                $"payoff announced {payoffAfter - payoffBefore}x during watch (dedup allows ≤1)");
            legNotes["phase"] = $"6/6 boot-log match; 75s stable; alert-codec ok; payoff-dedup ok; [ERROR] lines total={totalErrorLines} (dominion=0)";

            // ------------------------------------------------ LEG 4 · KILL-9
            var listedBeforeKill = FindListed(bridge, "list-before-kill", ZoneGroup);
            var declaredAtBefore = listedBeforeKill.GetProperty("declaredAt").GetDateTime();
            var gamePid = FindLaneGamePid();
            Assert.True(gamePid > 0, "no lane-scoped game process found for kill-9");
            Kill9AndConfirmExit(gamePid);
            var rowAfterKill = SnapshotDominionRow();
            Assert.True(rowAfterKill != null, "dominions row lost across SIGKILL");
            Assert.Equal(TaxRateChanged, ToLong(rowAfterKill!["tax_rate"]!));

            E2eStack.RestartGameServer();
            using var bridge2 = new BotDriveClient(E2eStack.BridgePort);
            var after = Trace(bridge2, "list-after-restart", "{\"cmd\":\"dominion\",\"op\":\"list\"}");
            var restored = ((JsonElement)after["dominions"]!).EnumerateArray()
                .FirstOrDefault(d => d.GetProperty("zoneGroupId").GetUInt32() == ZoneGroup);
            Assert.True(restored.ValueKind != JsonValueKind.Undefined,
                $"zone_group {ZoneGroup} missing from the manager store after kill-9 restart");
            Assert.Equal(ExpeditionId, restored.GetProperty("expeditionId").GetUInt32());
            Assert.Equal(ExpeditionName, restored.GetProperty("expeditionName").GetString());
            Assert.Equal(TaxRateChanged, restored.GetProperty("taxRate").GetInt32());
            Assert.True(Math.Abs((restored.GetProperty("declaredAt").GetDateTime() - declaredAtBefore).TotalSeconds) < 2,
                "declared_at drifted across kill-9 restart");
            var restartLog = ReadLog("game-restart.log");
            Assert.Contains("1 declared dominion(s)", restartLog);
            Assert.Contains("Loaded 6 siege zones, 11 siege settings, 158 siege plans", restartLog);
            var newPid = FindLaneGamePid();
            legNotes["kill9"] = $"SIGKILL pid {gamePid} confirmed; reload exact; reboot pid {newPid}";

            await WriteEvidenceAsync(startedAt, legNotes, gamePid, newPid);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dominion-soak] FAILED: {ex}");
            throw;
        }
    }

    private static void AssertDominionRow(Dictionary<string, object?> row,
        uint expeditionId, string expeditionName, int taxRate, DateTime startedAt)
    {
        Assert.Equal(expeditionId, ToLong(row["expedition_id"]!));
        Assert.Equal(expeditionName, row["expedition_name"]);
        Assert.Equal(taxRate, ToLong(row["tax_rate"]!));
        var declaredAt = (DateTime)row["declared_at"]!;
        Assert.True(declaredAt >= startedAt.AddMinutes(-1) && declaredAt <= DateTime.UtcNow.AddMinutes(1),
            $"declared_at out of plausible window: {declaredAt:O}");
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

    private JsonElement FindListed(BotDriveClient bridge, string label, uint zoneGroup)
    {
        var list = Trace(bridge, label, "{\"cmd\":\"dominion\",\"op\":\"list\"}");
        var found = ((JsonElement)list["dominions"]!).EnumerateArray()
            .FirstOrDefault(d => d.GetProperty("zoneGroupId").GetUInt32() == zoneGroup);
        Assert.True(found.ValueKind != JsonValueKind.Undefined,
            $"zone_group {zoneGroup} missing from manager store at '{label}'");
        return found;
    }

    private static string ReadLog(string name)
    {
        var path = Path.Combine(EvidenceDir, name);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static Dictionary<uint, string> ParsePhaseLines(string log)
    {
        // "Siege phase: zone_group {g} (template {t}) -> {Phase}"
        var phases = new Dictionary<uint, string>();
        foreach (var line in log.Split('\n'))
        {
            var i = line.IndexOf("Siege phase: zone_group ", StringComparison.Ordinal);
            if (i < 0)
                continue;
            var rest = line[(i + "Siege phase: zone_group ".Length)..];
            var sp = rest.IndexOf(' ');
            var arrow = rest.IndexOf("-> ", StringComparison.Ordinal);
            if (sp < 0 || arrow < 0)
                continue;
            if (uint.TryParse(rest[..sp], NumberStyles.Integer, CultureInfo.InvariantCulture, out var zg))
                phases[zg] = rest[(arrow + 3)..].Trim();
        }
        return phases;
    }

    private static int CountPayoffLines(string log)
        => log.Split('\n').Count(l => l.Contains("-> Payoff", StringComparison.Ordinal));

    /// <summary>
    /// PID-scoped lane lookup (the KillStaleServers discipline): dotnet
    /// processes running AAEmu.Game.dll whose /proc cwd sits under this
    /// lane's E2E_ROOT. Never matches prod/sibling stacks or this test host.
    /// </summary>
    private static int FindLaneGamePid()
    {
        var root = Path.GetFullPath(E2eStack.E2eRoot) + Path.DirectorySeparatorChar;
        foreach (var proc in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                var cmdline = File.ReadAllText($"/proc/{proc.Id}/cmdline").Replace('\0', ' ');
                if (!cmdline.Contains("AAEmu.Game.dll", StringComparison.Ordinal))
                    continue;
                var cwd = new FileInfo($"/proc/{proc.Id}/cwd").LinkTarget;
                if (cwd == null)
                    continue;
                var full = Path.GetFullPath(cwd);
                if (full.StartsWith(root, StringComparison.Ordinal) || full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                    return proc.Id;
            }
            catch
            {
                // Raced exit — ignore.
            }
        }
        return 0;
    }

    private static void Kill9AndConfirmExit(int pid)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill", $"-9 {pid}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        kill.WaitForExit(10_000);
        Assert.Equal(0, kill.ExitCode);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists($"/proc/{pid}"))
                return;
            Thread.Sleep(500);
        }
        Assert.Fail($"pid {pid} still alive 15s after kill -9");
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
        Console.WriteLine($"[dominion-soak] {label}: {data}");
        return dict;
    }

    private static long ToLong(object value) => value switch
    {
        JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt64(),
        JsonElement e => long.Parse(e.ToString(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
    };

    private async Task WriteEvidenceAsync(DateTime startedAt,
        Dictionary<string, object?> legs, int killedPid, int rebootPid)
    {
        Directory.CreateDirectory(EvidenceDir);

        var tracePath = Path.Combine(EvidenceDir, "dominion-soak-cycle-trace.jsonl");
        var lines = _trace.Select(entry => JsonSerializer.Serialize(entry));
        await File.WriteAllLinesAsync(tracePath, lines, Encoding.UTF8);

        var report = new
        {
            gate = "B5 DOMINION-01 S run",
            verdict = "PASS",
            scope = "declare → tax-rate change → phase-cron → kill-9 persistence; siege combat descoped (zero combat assertions)",
            sourceRevision = E2eStack.SourceRevision,
            lane = new
            {
                e2eRoot = E2eStack.E2eRoot,
                composeProject = E2eStack.ComposeProject,
                loginPort = E2eStack.LoginPort,
                gamePort = E2eStack.GamePort,
                streamPort = E2eStack.StreamPort,
                bridgePort = E2eStack.BridgePort,
                internalPort = E2eStack.InternalPort,
                webApiPort = E2eStack.WebApiPort,
                dbPort = E2eStack.DbPort
            },
            budget = new
            {
                actors = "0 live clients / 0 bots (bridge + MySQL + log evidence)",
                wallClock = "≤45 min",
                cronWatch = "≥70 s (≥4 ticks @ 15 s)",
                restarts = "exactly 1 (SIGKILL, PID-verified) + reboot",
                declareRowLatency = "≤5 s",
                scopeTripwire = "any combat assertion = scope FAIL"
            },
            startedAt = startedAt.ToString("O"),
            finishedAt = DateTime.UtcNow.ToString("O"),
            zoneGroup = ZoneGroup,
            expeditionId = ExpeditionId,
            expeditionName = ExpeditionName,
            taxRateInitial = TaxRateInitial,
            taxRateChanged = TaxRateChanged,
            killedGamePid = killedPid,
            rebootGamePid = rebootPid,
            legs
        };
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDir, "dominion-soak-cycle-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
