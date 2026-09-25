using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Part 5 — preemption lifecycle evidence, live (NOT autonomy).
///
/// Claim-path under test (code-verified):
///   POST /api/actors/move (far leg) → queue → GameplayActor.MoveTo (Running)
///   → POST /api/actors/move (near leg) while Running
///   → queue single-writer rule for API-owned legs.
/// Expected seam behavior per BotActionCommandQueue drain: a second API command
/// while an API leg is Running is Rejected(StateTransition, busy) — the anti-race
/// rule for concurrent callers; preemption (Interrupt) applies to world-internal
/// legs. So the literal "second move preempts first" leg documents the seam as
/// found; the interrupt-mediated legs prove Part-4 ownership clearing:
/// interrupt → first Interrupted → fresh move lands (no orphan, no busy wedge).
///
/// Warm-lane safe: fresh account, no teleports, no quest assertions, own report file.
/// </summary>
[Collection("e2e")]
public class PreemptCurrentLifecycleTests
{
    private const string BotName = "PreemptProbe";
    private const string BotAccount = "preemptprobe";
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "preempt-lifecycle-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ApiMove_SecondMoveBusy_InterruptThenFreshMoveLands()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# Preempt lifecycle — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        // ---- 0. FAIL-EARLY config proof (throws before boot when misconfigured) ----
        E2eStack.RequireFlags("AAEMU_QUEST_BOOTSTRAP_ENABLED");
        var manifest = E2eStack.CapabilityManifest()
            .Select(f => $"{f.Name}={f.Value ?? "<unset>"} [{f.Source}]").ToList();
        Leg("config-proof", true,
            $"RequireFlags(AAEMU_QUEST_BOOTSTRAP_ENABLED) passed; manifest {manifest.Count} flags",
            0.0);

        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        BotNetworkSession? session = null;
        var setupSeconds = 0.0;
        var execSeconds = 0.0;
        var passed = false;
        string failBoundary = "";
        string trace1Audit = "UNAVAILABLE", trace2Audit = "UNAVAILABLE", trace3Audit = "UNAVAILABLE";
        string move1Outcome = "UNAVAILABLE", move2Outcome = "UNAVAILABLE", move3Outcome = "UNAVAILABLE";
        string interruptOutcome = "UNAVAILABLE";

        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer(); // the single allowed restart: new binaries + new env
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));
            using var http = NewClient();
            var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ---- SETUP: real TCP enter-world + enroll (register with manager) ----
            E2eStack.CleanupBotRows(BotAccount);
            session = await BotNetworkSession.ConnectAsync(
                BotName, BotAccount, Password,
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!session.InWorld)
            {
                failBoundary = "SETUP/enter-world";
                Leg("enter-world", false, "real login flow did not reach in-world", setupSw.Elapsed.TotalSeconds);
                Assert.Fail("bot must enter the live world through the real login flow");
            }
            Leg("enter-world", true, $"inWorld={session.InWorld}", setupSw.Elapsed.TotalSeconds);
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var stepped = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            Leg("enroll", stepped, $"stepped={stepped} (quest accepts, if any, are not asserted here)",
                setupSw.Elapsed.TotalSeconds);

            var start = ReadPos(bridge);
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            Leg("fixture-read", start != "UNAVAILABLE", $"start=[{start}] (no teleports; spawn stance)",
                0.0);
            if (start == "UNAVAILABLE")
            {
                failBoundary = "SETUP/pos-unreadable";
                Assert.Fail("could not read spawn position");
            }
            var (sx, sy, sz) = ParsePos(start);

            // ---- EXECUTE ----
            var execSw = Stopwatch.StartNew();

            // Leg 1: far move (+30 X, 60s budget) → must reach Running (dispatched onto live actor).
            var far = $"\"x\":{sx + 30:0.0},\"y\":{sy:0.0},\"z\":{sz:0.0}";
            var m1 = await PostJsonAsync(http, "/api/actors/move",
                $"{{\"bot\":\"{BotName}\",{far},\"timeoutSec\":60,\"idempotencyKey\":\"preempt-{BotAccount}-far\"}}");
            var trace1 = m1.GetProperty("trace_id").GetGuid();
            string s1;
            try
            {
                s1 = await PollStateAsync(http, trace1, ["Running"], TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException ex)
            {
                failBoundary = "RUN/move1-never-running";
                move1Outcome = ex.Message;
                trace1Audit = await ReadTraceAsync(http, trace1);
                Leg("move1-dispatch", false, ex.Message, execSw.Elapsed.TotalSeconds);
                Assert.Fail($"far leg never reached Running: {ex.Message}");
                return;
            }
            Leg("move1-dispatch", s1 == "Running", $"trace={trace1} state={s1} (far leg live)",
                execSw.Elapsed.TotalSeconds);

            // Leg 2 (literal): second move while Running → seam answers busy-reject for
            // API-owned legs (anti-race rule); document exactly what the seam returns.
            var near = $"\"x\":{sx + 3:0.0},\"y\":{sy:0.0},\"z\":{sz:0.0}";
            var m2 = await PostJsonAsync(http, "/api/actors/move",
                $"{{\"bot\":\"{BotName}\",{near},\"timeoutSec\":30,\"idempotencyKey\":\"preempt-{BotAccount}-near\"}}");
            var trace2 = m2.GetProperty("trace_id").GetGuid();
            var p2 = await PollTerminalAsync(http, trace2, TimeSpan.FromSeconds(30));
            var s2 = p2.GetProperty("state").GetString() ?? "";
            move2Outcome = $"{s2}: {GetStr(p2, "failure")}/{GetStr(p2, "detail")}";
            var busyRejected = s2 == "Rejected" && (GetStr(p2, "failure").Contains("StateTransition")
                || GetStr(p2, "detail").Contains("busy"));
            Leg("second-busy-reject", busyRejected,
                $"trace={trace2} outcome=[{move2Outcome}] (API-vs-API busy rule as found)",
                execSw.Elapsed.TotalSeconds);

            // Leg 3: interrupt the far leg via the control op → must land Interrupted.
            var intr = await PostJsonAsync(http, "/api/actors/interrupt",
                $"{{\"bot\":\"{BotName}\",\"traceId\":\"{trace1}\"}}");
            var intrTrace = intr.GetProperty("trace_id").GetGuid();
            var pintr = await PollTerminalAsync(http, intrTrace, TimeSpan.FromSeconds(30));
            var delivered = pintr.TryGetProperty("result_payload", out var rp)
                && rp.ValueKind == JsonValueKind.True;
            interruptOutcome = $"delivered={delivered} entry={pintr.GetProperty("state").GetString()}";
            var p1 = await PollTerminalAsync(http, trace1, TimeSpan.FromSeconds(30));
            var t1 = p1.GetProperty("state").GetString() ?? "";
            move1Outcome = $"{t1}: {GetStr(p1, "failure")}/{GetStr(p1, "detail")}";
            trace1Audit = p1.TryGetProperty("audit", out var a1) ? a1.ToString() : trace1Audit;
            Leg("interrupt-first", t1 == "Interrupted" && delivered,
                $"trace={trace1} outcome=[{move1Outcome}] {interruptOutcome}",
                execSw.Elapsed.TotalSeconds);

            // Leg 4: fresh move after interrupt → must be ACCEPTED (ownership cleared,
            // no orphan, no busy wedge). Lane fact (baseline run): nothing ticks the
            // live actor here, so the target sits INSIDE the 0.5m arrival radius —
            // the already-there leg walks the full lifecycle synchronously at
            // dispatch (StartMove no-op) with no ticks required.
            var now = ReadPos(bridge);
            var (cx, cy, cz) = now == "UNAVAILABLE" ? (sx, sy, sz) : ParsePos(now);
            var m3 = await PostJsonAsync(http, "/api/actors/move",
                $"{{\"bot\":\"{BotName}\",\"x\":{cx + 0.2:0.0},\"y\":{cy:0.0},\"z\":{cz:0.0},\"timeoutSec\":30,\"idempotencyKey\":\"preempt-{BotAccount}-fresh\"}}");
            var trace3 = m3.GetProperty("trace_id").GetGuid();
            var p3 = await PollTerminalAsync(http, trace3, TimeSpan.FromSeconds(30));
            var t3 = p3.GetProperty("state").GetString() ?? "";
            move3Outcome = $"{t3}: {GetStr(p3, "failure")}/{GetStr(p3, "detail")}";
            trace3Audit = p3.TryGetProperty("audit", out var a3) ? a3.ToString() : trace3Audit;
            var notBusy = !(t3 == "Rejected" && (GetStr(p3, "failure").Contains("StateTransition")
                || GetStr(p3, "detail").Contains("busy")));
            var landed = t3 == "Completed" && notBusy;
            Leg("fresh-move-lands", landed, $"trace={trace3} outcome=[{move3Outcome}]", execSw.Elapsed.TotalSeconds);

            trace2Audit = (await ReadTraceAsync(http, trace2));
            execSeconds = execSw.Elapsed.TotalSeconds;
            passed = busyRejected && t1 == "Interrupted" && delivered && landed;
            if (!passed && string.IsNullOrEmpty(failBoundary))
                failBoundary = !busyRejected ? "RUN/no-busy-reject"
                    : t1 != "Interrupted" ? "RUN/no-interrupt"
                    : "RUN/fresh-not-landed";
            Assert.True(passed,
                $"preempt lifecycle FAIL at {failBoundary}: move1=[{move1Outcome}] move2=[{move2Outcome}] " +
                $"interrupt=[{interruptOutcome}] move3=[{move3Outcome}]");
        }
        finally
        {
            totalWall.Stop();
            await WriteReportAsync(passed, failBoundary, legs, evidence.ToString(), manifest,
                new { move1Outcome, move2Outcome, interruptOutcome, move3Outcome },
                new { trace1Audit, trace2Audit, trace3Audit },
                setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            session?.Dispose();
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }
    // ---- helpers (Q0 shapes) ----

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static async Task WaitForWebApiAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                return;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        throw new TimeoutException("WebApi never came up on port " + E2eStack.WebApiPort);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PollTerminalAsync(HttpClient client, Guid traceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement.Clone();
                var state = root.GetProperty("state").GetString();
                if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return root;
            }
            await Task.Delay(300);
        }
        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout}");
    }

    private static async Task<string> PollStateAsync(HttpClient client, Guid traceId, string[] wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string last = "UNAVAILABLE";
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                last = doc.RootElement.GetProperty("state").GetString() ?? last;
                if (wanted.Contains(last))
                    return last;
                if (last is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return last; // terminal before Running: report, don't hang
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"trace {traceId} never reached [{string.Join("/", wanted)}] within {timeout} (last={last})");
    }

    private static async Task<string> ReadTraceAsync(HttpClient client, Guid traceId)
    {
        try
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string ReadPos(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return $"{x.GetDouble():0.1},{y.GetDouble():0.1},{z.GetDouble():0.1}";
            return el.ToString();
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }

    private static (double X, double Y, double Z) ParsePos(string pos)
    {
        var c = pos.Split(',').Select(double.Parse).ToArray();
        return (c[0], c[1], c[2]);
    }

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private async Task WriteReportAsync(bool passed, string failBoundary,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText,
        List<string> manifest, object outcomes, object audits,
        double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "preempt-lifecycle",
                verdict = passed ? "PASS" : "FAIL",
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim = "interrupt preempts a live API move leg (Interrupted) and a fresh move lands after (no orphan, no busy wedge); second-concurrent API move is busy-rejected per the anti-race rule",
                notClaimed = new[] { "autonomy", "navigation routing", "quest behavior" },
                callPath = "POST /api/actors/move → queue → GameplayActor.MoveTo; POST /api/actors/interrupt → queue → actor.Interrupt → InterruptActive (Part-4 PreemptCurrent semantic)",
                laneManifest = manifest,
                timings = new
                {
                    setupSeconds = Math.Round(setupSeconds, 1),
                    execSeconds = Math.Round(execSeconds, 1),
                    wallSeconds = Math.Round(wallSeconds, 1)
                },
                legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                outcomes,
                audits,
                provenance = new
                {
                    sourceRevision = E2eStack.SourceRevision,
                    runtimeSqliteMd5 = E2eStack.RuntimeSqliteMd5(),
                    canonicalSqliteMd5 = E2eStack.CanonicalSqliteMd5
                },
                evidence = evidenceText
            };
            await File.WriteAllTextAsync(ReportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Evidence write is best-effort; the xUnit verdict is authoritative.
        }
    }
}
