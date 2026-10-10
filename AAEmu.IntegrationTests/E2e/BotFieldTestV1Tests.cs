using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// BOT FIELD TEST v1 harness (test-file only, NO game source changes).
///
/// START-config (lane-provided, disclosed — the test only READS these, never sets them):
///   AAEMU_PRESENCE_IDLE_WAKE=1        (5s self-re-arm presence wake; the ONLY wake source post-START)
///   AAEMU_QUEST_BOOTSTRAP_ENABLED=1   (quest bootstrap on)
///   AAEMU_QUESTREMOVE_DIAG=1          (remover witness logging, witness-only)
///
/// PROTOCOL (approved design — this file implements it exactly):
///  - Fresh timestamp account, CleanupBotRows pre-START, BotNetworkSession.ConnectAsync
///    real login, natural spawn (NO teleport). First boundary snapshot = START.
///  - Post-START: READ-ONLY polls only. charPos+charState every 10s (~270 polls);
///    quest observe (NO arc, NO reporter) + mail inv + mail char every 30s (~90);
///    bridge metrics + boundary snapshot + log-tail every 60s (~45);
///    direct-MySQL snapshots at START/T+15/T+30/END.
///    NEVER quest-wake/farm-wake/scheduler-Wake (each would be DEBUG-assistance).
///    NEVER gameplay drive ops (accept/advance/kill/gather/talk/interact/teleport/
///    levelUp/setLevel/addExp/stock/cast/loot/report).
///  - 45 min autonomous (2700s) + setup/teardown. Stop early ONLY on STALL
///    (recorded) or lane fault (bridge down — LANE-FAULT, not a bot verdict).
///  - Owned-only teardown: session dispose (no bridge deactivate — it only
///    covers provisioned sessions, refused for real-TCP bots).
///    Never containers/volumes/foreign rows.
///
/// Explicit gate run (same convention as the soak gates): skipped unless
/// BOT_FIELDTEST_V1=1, so a plain suite invocation never starts a 45-min lane run.
[Collection("e2e")]
public class BotFieldTestV1Tests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "FieldV1" + Stamp;
    private static readonly string BotAccount = ("fieldv1" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";

    // ---- schedule ----
    private const double AutonomousSeconds = 2700.0; // 45 min
    private const double FastPeriodS = 10.0;   // charPos + charState (~270 polls)
    private const double MidPeriodS = 30.0;    // quest observe + mail inv + mail char (~90)
    private const double SlowPeriodS = 60.0;   // metrics + boundary + log-tail (~45)
    private const double StallWindowS = 300.0; // sliding window, zero progress => STALL
    private const double DisplaceThresholdM = 3.0; // meaningful-progress displacement floor
    private const double JumpIgnoreM = 500.0;  // flat cumulative: ignore teleports/spawn jumps
    private const double CruiseSpeedMps = 2.5; // feasibility model cruise speed
    private static readonly double[] DbSnapshotMarksS = [0.0, 900.0, 1800.0]; // START/T+15/T+30 (+END)

    // Post-START allowlist: every bridge op the harness may issue. Anything else
    // counts as an intervention (must stay 0) and is refused by Ro().
    private static readonly HashSet<string> AllowOps = new(StringComparer.Ordinal)
    {
        "drive:charPos", "drive:charState", "drive:questState", "drive:isActive",
        "drive:hasCompleted", "drive:invCount", "drive:npcObjId", "drive:npcState",
        "drive:doodadObjId", "quest:observe", "mail:inv", "mail:char",
        "metrics:metrics",
    };

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "fieldtest-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameErrorLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Error.log");
    private static string ArchiveDir => "/tmp/fieldtest-evidence";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BotFieldTestV1Autonomous45Min()
    {
        if (Environment.GetEnvironmentVariable("BOT_FIELDTEST_V1") != "1")
        {
            Assert.Skip("BOT_FIELDTEST_V1=1 is required for the 45-min autonomous bot field test (explicit lane run).");
            return;
        }

        var totalWall = Stopwatch.StartNew();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# BOT FIELD TEST v1 (single fresh bot {BotName}) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine("- START-config (lane-provided, disclosed): AAEMU_PRESENCE_IDLE_WAKE=1 + AAEMU_QUEST_BOOTSTRAP_ENABLED=1 + AAEMU_QUESTREMOVE_DIAG=1. Post-START: read-only polls only; the ONLY wake source is the lane's own 5s self-re-arm.");

        BotNetworkSession? session = null;
        BotDriveClient? bridge = null;
        var verdict = "UNKNOWN";
        string failBoundary = "";
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        void Leg(string leg, bool ok, string detail, double ms)
        {
            legs.Add((leg, ok, detail, ms));
            evidence.AppendLine($"- [{(ok ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        // ---- metric series ----
        var posSeries = new List<object>();
        var questSeries = new List<object>();
        var econSeries = new List<object>();
        var killSeries = new List<object>();
        var recoverySeries = new List<object>();
        var boundarySnapshots = new List<object>();
        var observeHistory = new List<object>();
        var dbSnapshots = new List<object>();
        var logTails = new List<object>();
        var progressEvents = new List<(double T, string Kind, string Detail)>();
        var wedges = new List<object>();
        var interventions = 0;
        var wedgeOpenS = -1.0;

        uint charId = 0;
        double autonomousS = 0, displacementM = 0;
        double? lastX = null, lastY = null, lastZ = null;
        var bagByTemplate = new Dictionary<uint, long>();
        long lastMoney = -1;
        int lastCompletedCount = -1, lastActiveCount = -1;
        var lastQuestSigById = new Dictionary<uint, string>();
        int lastHp = -1, lastMp = -1;
        string lastActivity = "";
        long lastActionCount = -1;
        var pinnedTargets = new Dictionary<uint, string>(); // objId -> first-seen context
        var deadTargets = new HashSet<uint>();
        int kills = 0, recoveryEdges = 0;
        var questDeltas = 0;
        var econDeltas = 0;
        string stallDetail = "";
        object? classification = null;
        string endReason = "COMPLETE";
        double lastProgressS = 0;
        var lastGatesS = 0.0; // feasibility gate term (0: harness does not stage gates post-START)

        void Progress(double t, string kind, string detail)
        {
            progressEvents.Add((t, kind, detail));
            lastProgressS = t;
            if (wedgeOpenS >= 0)
            {
                wedges.Add(new { startS = Math.Round(wedgeOpenS, 1), endS = Math.Round(t, 1), resumedBy = kind });
                wedgeOpenS = -1;
            }
        }

        // Read-only bridge call. Refuses anything outside the allowlist (counts an
        // intervention and throws — the post-START phase must stay at 0).
        JsonElement Ro(string cmd, string op, string json, int timeoutMs = 30_000)
        {
            if (bridge == null)
                throw new InvalidOperationException("lane bridge unavailable");
            var key = $"{cmd}:{op}";
            if (!AllowOps.Contains(key))
            {
                interventions++;
                throw new InvalidOperationException($"intervention refused post-START: {key}");
            }
            return bridge.Call(json, timeoutMs);
        }

        static double Flat(double x0, double y0, double x1, double y1)
        {
            var dx = x1 - x0; var dy = y1 - y0;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static double Gd(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;
        static long Gl(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;
        static uint Gu(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;
        static string Gs(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        try
        {
            // ---- ADOPT lane (pre-booted isolated stack; never EnsureUp) ----
            var adoptSw = Stopwatch.StartNew();
            AdoptLaneDbPassword();
            bridge = new BotDriveClient(E2eStack.BridgePort);
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "isolated lane answering (bridge ping)" : "lane cold — refusing to rebuild",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                verdict = "LANE-FAULT"; failBoundary = "SETUP/lane-down"; endReason = "LANE-FAULT";
                Assert.Fail("isolated fieldtest lane not answering; refusing to rebuild mid-run");
            }

            // ---- pre-START: fresh account rows + real login + natural spawn ----
            var setupSw = Stopwatch.StartNew();
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
                Assert.Fail("bot must enter the live world through the real login flow (natural spawn, no teleport)");
            }
            charId = session.CharacterId;
            Leg("enter-world", true, $"inWorld charId={charId} (natural spawn; no teleport)", setupSw.Elapsed.TotalSeconds);

            // ---- START: first boundary snapshot ----
            var startSw = Stopwatch.StartNew();
            boundarySnapshots.Add(SnapBoundary(bridge!, "START"));
            dbSnapshots.Add(SnapDb("START", charId));
            var startPos = Ro("drive", "charPos", $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}");
            var startState = Ro("drive", "charState", $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}");
            lastX = Gd(startPos, "x"); lastY = Gd(startPos, "y"); lastZ = Gd(startPos, "z");
            lastHp = (int)Gl(startState, "hp"); lastMp = (int)Gl(startState, "mp");
            lastActionCount = Gl(startState, "activeQuests");
            Progress(0, "start", $"spawn=({lastX:0.0},{lastY:0.0}) hp={lastHp} mp={lastMp}");
            Leg("start", true, $"START boundary recorded (charId={charId})", startSw.Elapsed.TotalSeconds);

            // ---- autonomous phase: read-only polls ----
            var autoSw = Stopwatch.StartNew();
            var nextFast = 0.0; var nextMid = 0.0; var nextSlow = 0.0;
            var nextDbMark = 1; // DbSnapshotMarksS[0] taken at START
            string? stallVerdict = null;
            var serverLogOff = 0L; var errorLogOff = 0L;
            var coveringAction = ""; // feasibility: action covering the current still window

            while (true)
            {
                var t = autoSw.Elapsed.TotalSeconds;
                autonomousS = t;
                if (t >= AutonomousSeconds)
                    break;

                // ---- 10s: charPos + charState ----
                if (t >= nextFast)
                {
                    nextFast = t + FastPeriodS;
                    try
                    {
                        var pos = Ro("drive", "charPos", $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}");
                        var st = Ro("drive", "charState", $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}");
                        var x = Gd(pos, "x"); var y = Gd(pos, "y"); var z = Gd(pos, "z");
                        if (!double.IsNaN(x) && lastX.HasValue)
                        {
                            var d = Flat(lastX.Value, lastY!.Value, x, y);
                            if (d <= JumpIgnoreM)
                            {
                                displacementM += d;
                                if (d >= DisplaceThresholdM)
                                    Progress(t, "displacement", $"flat+{d:0.1}m cum={displacementM:0.0}m");
                            }
                            else
                                posSeries.Add(new { tS = Math.Round(t, 1), note = $"jump-ignored {d:0.0}m" });
                        }
                        lastX = double.IsNaN(x) ? lastX : x;
                        lastY = double.IsNaN(y) ? lastY : y;
                        lastZ = double.IsNaN(z) ? lastZ : z;
                        var hp = (int)Gl(st, "hp"); var mp = (int)Gl(st, "mp");
                        if ((hp != lastHp || mp != lastMp) && lastHp >= 0)
                        {
                            recoveryEdges++;
                            recoverySeries.Add(new { tS = Math.Round(t, 1), hp, mp, wasHp = lastHp, wasMp = lastMp });
                            Progress(t, "recovery", $"hp {lastHp}->{hp} mp {lastMp}->{mp}");
                        }
                        lastHp = hp; lastMp = mp;
                        posSeries.Add(new
                        {
                            tS = Math.Round(t, 1),
                            x = Math.Round(x, 1), y = Math.Round(y, 1),
                            zoneId = Gu(pos, "zoneId"),
                            hp, mp,
                            cumDisplacementM = Math.Round(displacementM, 1)
                        });
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException)
                    {
                        verdict = "LANE-FAULT"; failBoundary = "AUTONOMOUS/bridge-down"; endReason = "LANE-FAULT";
                        evidence.AppendLine($"- bridge lost at T+{t:0}s ({ex.GetType().Name}) — LANE-FAULT, not a bot verdict");
                        break;
                    }
                }

                // ---- 30s: quest observe (NO arc, NO reporter) + mail inv + mail char ----
                if (t >= nextMid)
                {
                    nextMid = t + MidPeriodS;
                    try
                    {
                        var obs = Ro("quest", "observe", $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                        observeHistory.Add(new { tS = Math.Round(t, 1), raw = obs.ToString() });
                        var completedCount = (int)Gl(obs, "completedCount");
                        var activeCount = (int)Gl(obs, "activeCount");
                        if (lastCompletedCount >= 0 && completedCount != lastCompletedCount)
                        {
                            questDeltas++;
                            Progress(t, "quest-completed", $"completedCount {lastCompletedCount}->{completedCount}");
                        }
                        if (lastActiveCount >= 0 && activeCount != lastActiveCount)
                        {
                            questDeltas++;
                            Progress(t, "quest-active", $"active {lastActiveCount}->{activeCount}");
                        }
                        lastCompletedCount = completedCount; lastActiveCount = activeCount;
                        if (obs.TryGetProperty("activeQuests", out var aq) && aq.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var q in aq.EnumerateArray())
                            {
                                var qid = Gu(q, "questId");
                                var sig = $"{Gs(q, "status")}/{Gs(q, "step")}/{q}";
                                if (lastQuestSigById.TryGetValue(qid, out var prev) && prev != sig)
                                {
                                    questDeltas++;
                                    Progress(t, "quest-change", $"quest {qid} {prev} -> {sig[..Math.Min(sig.Length, 120)]}");
                                }
                                lastQuestSigById[qid] = sig;
                            }
                        }
                        var activity = Gs(obs, "arbiterActivity");
                        if (activity != lastActivity && !string.IsNullOrEmpty(activity))
                            Progress(t, "activity", $"arbiter {lastActivity} -> {activity}");
                        if (activity.Contains("recovery.rest", StringComparison.Ordinal) && lastActivity != activity)
                        {
                            recoveryEdges++;
                            recoverySeries.Add(new { tS = Math.Round(t, 1), activity });
                        }
                        lastActivity = activity;
                        var actionCount = Gl(obs, "questActionCount");
                        if (lastActionCount >= 0 && actionCount > lastActionCount)
                            Progress(t, "action-counter", $"questActionCount {lastActionCount}->{actionCount}");
                        lastActionCount = actionCount;
                        coveringAction = $"{Gs(obs, "liveAction")}/{Gs(obs, "liveState")}/{Gs(obs, "questTravelReason")}";
                        // Pin bot-pinned kill targets: currentTargetObjId only (never free-scan).
                        var pinned = Gu(obs, "currentTargetObjId");
                        if (pinned != 0 && !pinnedTargets.ContainsKey(pinned) && !deadTargets.Contains(pinned))
                            pinnedTargets[pinned] = $"T+{t:0}s observe";
                        questSeries.Add(new
                        {
                            tS = Math.Round(t, 1),
                            activeCount, completedCount,
                            activity = string.IsNullOrEmpty(activity) ? "none" : activity,
                            actionCount, pinnedTarget = pinned, coveringAction
                        });

                        // kills: bot-pinned targets alive -> dead via read-only npcState
                        foreach (var objId in pinnedTargets.Keys.ToList())
                        {
                            if (deadTargets.Contains(objId))
                                continue;
                            try
                            {
                                var ns = Ro("drive", "npcState",
                                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}");
                                var alive = ns.TryGetProperty("alive", out var al) && al.ValueKind == JsonValueKind.True;
                                if (!alive)
                                {
                                    deadTargets.Add(objId); kills++;
                                    killSeries.Add(new { tS = Math.Round(t, 1), objId, templateId = Gu(ns, "templateId") });
                                    Progress(t, "kill", $"pinned objId {objId} alive->dead");
                                }
                            }
                            catch { /* read miss on despawned target: not a kill, not a fault */ }
                        }

                        // mail inv + mail char (read-only economy scan)
                        try
                        {
                            var inv = Ro("mail", "inv", $"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"inv\"}}");
                            var money = Ro("mail", "char", $"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"char\"}}");
                            var m = Gl(money, "money");
                            if (lastMoney >= 0 && m != lastMoney)
                            {
                                econDeltas++;
                                Progress(t, "money", $"money {lastMoney}->{m}");
                            }
                            lastMoney = m;
                            var seen = new Dictionary<uint, long>();
                            if (inv.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                                foreach (var it in items.EnumerateArray())
                                {
                                    var tpl = Gu(it, "templateId");
                                    var n = Gl(it, "count");
                                    if (n < 0) n = 0;
                                    seen.TryGetValue(tpl, out var c);
                                    seen[tpl] = c + n;
                                }
                            foreach (var kv in seen)
                                if (!bagByTemplate.TryGetValue(kv.Key, out var prevC) || prevC != kv.Value)
                                {
                                    econDeltas++;
                                    Progress(t, "bag", $"template {kv.Key} {prevC}->{kv.Value}");
                                }
                            foreach (var kv in bagByTemplate.Keys.ToList())
                                if (!seen.ContainsKey(kv))
                                {
                                    econDeltas++;
                                    Progress(t, "bag", $"template {kv} removed");
                                }
                            bagByTemplate = seen;
                            econSeries.Add(new { tS = Math.Round(t, 1), money = m, bagTemplates = seen.Count });
                        }
                        catch { /* economy scan miss: recorded, not fatal */ }
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException)
                    {
                        verdict = "LANE-FAULT"; failBoundary = "AUTONOMOUS/bridge-down"; endReason = "LANE-FAULT";
                        evidence.AppendLine($"- bridge lost at T+{t:0}s ({ex.GetType().Name}) — LANE-FAULT, not a bot verdict");
                        break;
                    }
                }

                // ---- 60s: bridge metrics + boundary snapshot + log-tail ----
                if (t >= nextSlow)
                {
                    nextSlow = t + SlowPeriodS;
                    try
                    {
                        var metrics = Ro("metrics", "metrics", "{\"cmd\":\"metrics\"}", 15_000);
                        boundarySnapshots.Add(SnapBoundary(bridge!, $"T+{t:0}s"));
                        var (sl, el) = TailLogs(ref serverLogOff, ref errorLogOff);
                        logTails.Add(new { tS = Math.Round(t, 1), serverLines = sl, errorLines = el });
                        evidence.AppendLine($"- T+{t:0}s metrics=[{Trunc(metrics.ToString(), 160)}] disp={displacementM:0.0}m questsΔ={questDeltas} econΔ={econDeltas} kills={kills} rec={recoveryEdges}");
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException)
                    {
                        verdict = "LANE-FAULT"; failBoundary = "AUTONOMOUS/bridge-down"; endReason = "LANE-FAULT";
                        evidence.AppendLine($"- bridge lost at T+{t:0}s ({ex.GetType().Name}) — LANE-FAULT, not a bot verdict");
                        break;
                    }
                }

                // ---- direct-MySQL snapshots at T+15 / T+30 ----
                if (nextDbMark < DbSnapshotMarksS.Length && t >= DbSnapshotMarksS[nextDbMark])
                {
                    dbSnapshots.Add(SnapDb($"T+{t:0}s", charId));
                    nextDbMark++;
                }

                // ---- stall detector: 300s sliding window ----
                var windowStart = t - StallWindowS;
                var recent = progressEvents.Where(e => e.T >= windowStart).ToList();
                if (t >= StallWindowS && recent.Count == 0)
                {
                    // Feasibility: is a covering long action still plausibly running?
                    // T_exp = D_flat/2.5 + gates + slack(max(60s, 0.5*T_exp)).
                    var stillS = t - lastProgressS;
                    var baseExp = displacementM / CruiseSpeedMps + lastGatesS;
                    var slack = Math.Max(60.0, 0.5 * baseExp);
                    var tExp = baseExp + slack;
                    var covered = stillS <= tExp && !string.IsNullOrEmpty(coveringAction)
                        && !coveringAction.StartsWith("//", StringComparison.Ordinal);
                    if (!covered)
                    {
                        if (wedgeOpenS < 0)
                            wedgeOpenS = lastProgressS;
                        stallDetail = $"no meaningful-progress event in [{windowStart:0}s,{t:0}s] (300s window); " +
                            $"still={stillS:0}s T_exp={tExp:0}s (D_flat={displacementM:0.0}m gates={lastGatesS:0}s slack={slack:0}s) " +
                            $"coveringAction=[{coveringAction}]";
                        stallVerdict = $"STALL@T+{t:0}s";
                        endReason = "STALL";
                        evidence.AppendLine($"- FIRST STALL at T+{t:0}s: {stallDetail}");
                        break;
                    }
                }

                await Task.Delay(1000);
            }
            autonomousS = autoSw.Elapsed.TotalSeconds;

            // ---- END boundary + MySQL END snapshot ----
            boundarySnapshots.Add(SnapBoundary(bridge!, "END"));
            dbSnapshots.Add(SnapDb("END", charId));

            if (endReason == "STALL" && stallVerdict != null)
            {
                failBoundary = "AUTONOMOUS/stall";
                classification = new
                {
                    taxonomy = "PLACEHOLDER (director fills taxonomy)",
                    OBSERVED = stallDetail,
                    EXPECTED = "continuous meaningful progress (quest change / completedCount+ / ≥3m displacement / bag-or-money delta / recovery edge / activity change / action-counter increment) at least every 300s, or a covering long action within feasibility T_exp",
                    FIRST_FAILED_PREDICATE = "exists meaningful-progress event in [T-300s, T]",
                    OWNER = "PLACEHOLDER (director assigns)",
                    ROOT_CAUSE = "PLACEHOLDER (director diagnoses from data)",
                    SMALLEST_FIX = "PLACEHOLDER (director proposes)",
                };
                verdict = stallVerdict;
                Leg("autonomous", false, stallDetail, autonomousS);
            }
            else if (endReason == "COMPLETE")
            {
                verdict = $"COMPLETE-T+{autonomousS:0}s";
                Leg("autonomous", true,
                    $"45-min window survived: disp={displacementM:0.0}m questsΔ={questDeltas} econΔ={econDeltas} kills={kills} rec={recoveryEdges} interventions={interventions}",
                    autonomousS);
            }
            // LANE-FAULT legs already recorded at the break site.
            Leg("interventions", interventions == 0, $"non-allowlist post-START ops={interventions} (must be 0)", 0);
        }
        catch (Exception ex)
        {
            if (verdict == "UNKNOWN") { verdict = "LANE-FAULT"; endReason = "LANE-FAULT"; }
            if (string.IsNullOrEmpty(failBoundary)) failBoundary = "AUTONOMOUS/exception";
            evidence.AppendLine($"- harness exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // ---- owned-only teardown: archive owned logs pre-teardown, then
            // dispose the owned TCP session. No bridge deactivate: it only covers
            // provisioned PersistentBotSessions (bridge: "not provisioned" for
            // real-TCP bots) — same as the G3/G4/G5/G6/G7a TryReleaseBot pattern,
            // which records cleanup=UNAVAILABLE for networked characters.
            // Never containers/volumes/foreign rows.
            var archived = new List<object>();
            try
            {
                Directory.CreateDirectory(ArchiveDir);
                CopyLog(GameLogPath, "Server.log", evidence, archived);
                CopyLog(GameErrorLogPath, "Error.log", evidence, archived);
            }
            catch (Exception ex) { evidence.AppendLine($"- log archival failed ({ex.GetType().Name})"); }
            var release = "UNAVAILABLE (networked char has no bridge release op; TCP session disposed, registry entry left in place)";
            try { session?.Dispose(); } catch { }
            evidence.AppendLine($"- teardown (owned-only): release={release}; session disposed; containers/volumes/foreign rows untouched");
            await WriteReportAsync(verdict, failBoundary, endReason, legs, evidence.ToString(),
                new
                {
                    bot = BotName, account = BotAccount, characterId = charId,
                    scenario = "bot-fieldtest-v1",
                    timings = new { autonomousSeconds = Math.Round(autonomousS, 1), wallSeconds = Math.Round(totalWall.Elapsed.TotalSeconds, 1) },
                    series = new
                    {
                        autonomousSeconds = Math.Round(autonomousS, 1),
                        displacementM = Math.Round(displacementM, 1),
                        questDeltas, econDeltas, kills, recoveryEdges, interventions,
                        wedges, progressEventCount = progressEvents.Count,
                        positions = posSeries, quests = questSeries, economy = econSeries,
                        killEvents = killSeries, recoveries = recoverySeries,
                    },
                    legsRecordOnly = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                    boundarySnapshots, observeHistory, dbSnapshots, logTails,
                    firstFailure = classification,
                    provenance = new
                    {
                        presenceIdleWake = "AAEMU_PRESENCE_IDLE_WAKE=1 (lane START-config, 5s self-re-arm, disclosed)",
                        bootstrap = "AAEMU_QUEST_BOOTSTRAP_ENABLED=1 (lane START-config, disclosed)",
                        questRemoveDiag = "AAEMU_QUESTREMOVE_DIAG=1 (witness-only, disclosed)",
                        sourceRevision = E2eStack.SourceRevision,
                        runtimeSqliteMd5 = E2eStack.RuntimeSqliteMd5(),
                        canonicalSqliteMd5 = E2eStack.CanonicalSqliteMd5,
                    },
                    archivedLogs = archived,
                    teardown = new { release },
                });
        }

        evidence.AppendLine($"- verdict: {verdict}");
    }

    // ---- helpers (read-only; never gameplay-performing) ----

    private object SnapBoundary(BotDriveClient b, string phase)
    {
        try
        {
            var pos = b.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            var st = b.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);
            return new
            {
                phase,
                atUtc = DateTime.UtcNow.ToString("o"),
                x = Gd(pos, "x"), y = Gd(pos, "y"), z = Gd(pos, "z"),
                zoneId = Gu(pos, "zoneId"),
                level = Gl(st, "level"), hp = Gl(st, "hp"), maxHp = Gl(st, "maxHp"),
                mp = Gl(st, "mp"), maxMp = Gl(st, "maxMp"),
                activeQuests = Gl(st, "activeQuests"),
            };
        }
        catch (Exception ex) { return new { phase, error = ex.GetType().Name }; }
    }

    private static object SnapDb(string mark, uint charId)
    {
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            long money = -1; int level = -1; double x = double.NaN, y = double.NaN, z = double.NaN;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT money, level, x, y, z FROM characters WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", charId);
                using var r = cmd.ExecuteReader();
                if (r.Read()) { money = r.GetInt64(0); level = r.GetInt32(1); x = r.GetFloat(2); y = r.GetFloat(3); z = r.GetFloat(4); }
            }
            var quests = new List<object>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT template_id, status FROM quests WHERE owner = @id ORDER BY template_id";
                cmd.Parameters.AddWithValue("@id", charId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) quests.Add(new { quest = r.GetUInt32(0), status = r.GetInt32(1) });
            }
            var completed = new List<object>();
            using (var cmd = conn.CreateCommand())
            {
                // completed_quests schema is (id=64-bit block, data=blob, owner):
                // quest N sets bit (N%64) in block (N/64) — same as q256 lane.
                cmd.CommandText = "SELECT `id`, `data` FROM completed_quests WHERE `owner` = @id ORDER BY `id`";
                cmd.Parameters.AddWithValue("@id", charId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) completed.Add(new { block = r.GetUInt32(0), data = Convert.ToHexString((byte[])r.GetValue(1)) });
            }
            var items = new List<object>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT template_id, SUM(count) c FROM items WHERE owner = @id GROUP BY template_id ORDER BY template_id";
                cmd.Parameters.AddWithValue("@id", charId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) items.Add(new { template = r.GetUInt32(0), count = r.GetInt64(1) });
            }
            return new { mark, atUtc = DateTime.UtcNow.ToString("o"), money, level, x, y, z, quests, completed, items };
        }
        catch (Exception ex) { return new { mark, error = ex.GetType().Name + ":" + ex.Message }; }
    }

    private (int ServerLines, int ErrorLines) TailLogs(ref long serverOff, ref long errorOff)
    {
        return (TailCount(GameLogPath, ref serverOff), TailCount(GameErrorLogPath, ref errorOff));
    }

    private static int TailCount(string path, ref long offset)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset > fs.Length) offset = 0;
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var n = 0;
            while (sr.ReadLine() != null) n++;
            offset = fs.Length;
            return n;
        }
        catch { return -1; }
    }

    private static void CopyLog(string source, string destName, StringBuilder evidence, List<object> archivedFiles)
    {
        try
        {
            if (File.Exists(source))
            {
                var dest = Path.Combine(ArchiveDir, destName);
                File.Copy(source, dest, overwrite: true);
                evidence.AppendLine($"- archived {source} -> {dest} ({new FileInfo(dest).Length} bytes)");
                archivedFiles.Add(new { source, dest, status = "archived", bytes = new FileInfo(dest).Length });
            }
            else
            {
                evidence.AppendLine($"- {destName} MISSING at {source}");
                archivedFiles.Add(new { source, status = "MISSING" });
            }
        }
        catch (Exception ex)
        {
            evidence.AppendLine($"- {destName} archive failed ({ex.GetType().Name}: {ex.Message})");
            archivedFiles.Add(new { source, status = $"FAILED:{ex.GetType().Name}" });
        }
    }

    private static void AdoptLaneDbPassword()
    {
        var envFile = Path.Combine(E2eStack.E2eRoot, ".env");
        foreach (var line in File.ReadAllLines(envFile))
        {
            if (!line.StartsWith("DB_PASSWORD="))
                continue;
            var pwd = line["DB_PASSWORD=".Length..].Trim();
            if (string.IsNullOrEmpty(pwd))
                throw new InvalidOperationException($"lane {envFile} has an empty DB_PASSWORD");
            typeof(E2eStack).GetProperty("DbPassword")!.SetValue(null, pwd);
            return;
        }
        throw new InvalidOperationException($"lane {envFile} has no DB_PASSWORD entry");
    }

    private static async Task<bool> ProbeLaneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new BotDriveClient(E2eStack.BridgePort);
                if (probe.Call("{\"cmd\":\"ping\"}", 3000).GetProperty("pong").GetBoolean())
                    return true;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
    }

    private static double Gd(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;
    private static long Gl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;
    private static uint Gu(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static string Trunc(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";

    private async Task WriteReportAsync(string verdict, string failBoundary, string endReason,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText, object fixture)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "bot-fieldtest-v1",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                endReason,
                notClaimed = new[] { "any named quest chain", "combat/loot paths", "scheduler internals" },
                fixture,
                evidence = evidenceText,
            };
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
            var stampedPath = Path.Combine(EvidenceDir,
                "fieldtest-report." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, ReportPath, overwrite: true);
            Directory.CreateDirectory(ArchiveDir);
            File.WriteAllText(Path.Combine(ArchiveDir, "report.json"), reportJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FIELDTEST-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
