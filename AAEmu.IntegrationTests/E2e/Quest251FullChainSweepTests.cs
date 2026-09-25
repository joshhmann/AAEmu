using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// Full-chain SWEEP for quest 251 (harness/test only, NO game source changes).
///
/// Design (sequenced driver, ONE new file, self-contained — your-call option):
/// rather than editing the frozen gate files (Q0/G1/G2/G3-G8c frozen) to share
/// helpers, this sweep re-implements each gate's CORE assertion sequence in one
/// [Fact] with ONE fresh bot, reusing each gate's proven fixture pattern and
/// NOTHING beyond it. Distillations from the gate originals are disclosed
/// inline (G4 template proof via ObjId equality instead of funnel-log parse;
/// G7b/G7c signal scan via decide-history + light game-log grep instead of the
/// gates' full funnel scanners; G8b owner-proof kept via the same log grep).
///
/// Link order and doctrine:
///   LOGIN (setup) → G1 move probe (pre-chain, state-free explicit verb) →
///   G3 autonomous accept 251 at live 3512 → G4 select live 3475 → G5 pursue
///   to ≤3.0m + Stop → G6 one AutoAttack + HP fall → G7a kill + teardown →
///   G7b corpse named, zero loot → G7c loot once + grant → (repeat kill/loot
///   to meat ×3) G7d credit → Ready held → G8a reporter stable WITHHELD ×10
///   ticks → G8b return + InteractNpc dialogue → G8c withhold released at
///   START → turn-in + reward 18791 → G2 talk probe (post-chain, quest 532,
///   so 532 can never hijack the 251 arbiter mid-chain).
///   Q0 verdict is DERIVED from the G3 accept evidence (same engine AddQuest
///   path; Q0's explicit-POST form is frozen-proven and not re-runnable
///   mid-chain without destroying G3's activeBefore==False precondition —
///   no abandon/drop op exists on the driver surface).
///
/// Moving-START doctrine: every teleport/setLevel/refill/place/stop is
/// PRE-link staging, disclosed per link; post-STAGE each link is observe-only
/// (wake + observe + read-only npcState/npcObjId/charPos/inv/mails polls).
/// Withhold discipline: armed ONLY where its gates armed it (G8a, kept through
/// G8b, asserted through G8c staging) and released EXACTLY where G8c releases
/// it (G8c START via withhold OFF). No withhold anywhere in G3-G7d.
/// Stop-at-first-fail: the first link whose predicate fails records its
/// boundary, writes the report, and Assert.Fails — no production fix here.
[Collection("e2e")]
public class Quest251FullChainSweepTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "Q251Sweep" + Stamp;
    private static readonly string BotAccount = ("q251sweep" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    private const uint Quest251 = 251;
    private const uint GiverReporter3512 = 3512;
    private const uint Boar3475 = 3475;
    private const uint BoarMeat4058 = 4058;
    private const uint Reward18791 = 18791;
    private const int MeatRequired = 3;
    private const int FixtureLevel = 10;
    private const int WakeWaitMs = 30000;

    // G1 probe constants (G1 gate: +12 X, speed 2.0, timeout 20s).
    private const uint G1StageNpc2425 = 2425;
    private const double G1StepX = 12.0;
    // G2 probe constants (G2 gate: quest 532, accept 2425, talk 2426, level 30).
    private const uint Quest532 = 532;
    private const uint G2AcceptNpc2425 = 2425;
    private const uint G2TalkNpc2426 = 2426;
    private const int G2FixtureLevel = 30;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "q251-sweep-report.json");
    private static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "runtime", "game", "Logs", "Server.log");
    private static string GameRestartLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Sweep251LoginToRewardOnSingleBot()
    {
        var totalWall = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# Quest-251 full-chain sweep (single fresh bot {BotName}) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        evidence.AppendLine("- SWEEP, not a gate: each link reuses its frozen gate's core assertion with that gate's fixture pattern only; stop at the first failing link, no production fix.");

        BotNetworkSession? session = null;
        var passed = false;
        string verdict = "UNKNOWN";
        string failBoundary = "";
        string claim = "";
        object? failingCondition = null;
        uint ourCharacterId = 0;
        var decideHistory = new List<string>();
        string g3AcceptEvidence = "";
        var setupSeconds = 0.0;

        void Leg(string leg, bool ok, string detail, double ms)
        {
            legs.Add((leg, ok, detail, ms));
            evidence.AppendLine($"- [{(ok ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        void NoteDecide(string d)
        {
            if (!string.IsNullOrEmpty(d))
                decideHistory.Add(d);
        }

        async Task<HttpClient> AcquireWebApiAsync()
        {
            // Route acquisition wrapped in Fail(): a dead :1280 BotControl route
            // records HARNESS/webapi-route WITH a report JSON instead of escaping.
            try { return await EnsureWebApiClientAsync(evidence); }
            catch (Exception ex)
            {
                Fail("HARNESS", "webapi-route", new { error = ex.GetType().Name + ": " + ex.Message }, "no enabled BotControl backend on the shared :1280 route; explicit-verb probe cannot run");
                throw new InvalidOperationException("unreachable: Fail did not throw");
            }
        }

        string Fail(string link, string boundary, object cond, string detail)
        {
            failBoundary = link + "/" + boundary;
            failingCondition = cond;
            verdict = "FAIL-BEHAVIOR";
            Leg("gate-" + link, false, detail, 0);
            evidence.AppendLine($"- FIRST-FAIL: {failBoundary}: {detail}");
            WriteReport();
            Assert.Fail($"Q251-SWEEP {verdict} at {failBoundary}: {detail}");
            return failBoundary;
        }

        void WriteReport()
        {
            try
            {
                Directory.CreateDirectory(EvidenceDir);
                var payload = new
                {
                    test = "Q251-SWEEP",
                    bot = BotName,
                    account = BotAccount,
                    utc = DateTime.UtcNow.ToString("o"),
                    verdict,
                    failBoundary,
                    claim,
                    failingCondition,
                    charId = ourCharacterId,
                    g3AcceptEvidence,
                    legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                    decideHistory = decideHistory.Take(120).ToList(),
                    evidence = evidence.ToString(),
                    totalSeconds = Math.Round(totalWall.Elapsed.TotalSeconds, 1),
                };
                // K4 artifact preservation: timestamped file per run + bare report as latest-pointer copy (same scheme as the gate WriteReportAsync sites).
                var sweepJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                var sweepStamped = Path.Combine(EvidenceDir,
                    Path.GetFileNameWithoutExtension(ReportPath) + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
                File.WriteAllText(sweepStamped, sweepJson);
                File.Copy(sweepStamped, ReportPath, overwrite: true);
                Console.WriteLine($"Q251-SWEEP verdict={verdict} boundary={failBoundary} legs={legs.Count} report={ReportPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Q251-SWEEP report write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        JsonElement Wake(BotDriveClient bridge, int waitMs, int timeoutMs = 120_000)
        {
            var el = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{waitMs}}}", timeoutMs);
            NoteDecide(GetStr(el, "questDecideDetail"));
            return el;
        }

        JsonElement Observe(BotDriveClient bridge, uint? reporter = null)
        {
            var q = reporter.HasValue ? $",\"reporter\":{reporter.Value}" : "";
            var el = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"{q}}}", 60_000);
            NoteDecide(GetStr(el, "questDecideDetail"));
            return el;
        }

        try
        {
            // ---- ADOPT (probe-only; never rebuild) ----
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = KillForeignWebApiListeners();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk, laneOk ? "warm lane answering (bridge ping)" : "lane cold — refusing to rebuild", adoptSw.Elapsed.TotalSeconds);
            if (!laneOk) { Fail("HARNESS", "lane-down", new { }, "warm lane not answering; refusing to rebuild per lane constraints"); }

            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ---- LOGIN (setup) ----
            var setupSw = Stopwatch.StartNew();
            E2eStack.CleanupBotRows(BotAccount);
            session = await BotNetworkSession.ConnectAsync(
                BotName, BotAccount, Password,
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!session.InWorld)
                Fail("HARNESS", "enter-world", new { }, "real login flow did not reach in-world");
            Leg("enter-world", true, $"inWorld={session.InWorld}", setupSw.Elapsed.TotalSeconds);

            var enroll = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            ourCharacterId = GetUInt(enroll, "id");
            var gameLogOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            var gameRestartLogOffset = File.Exists(GameRestartLogPath) ? new FileInfo(GameRestartLogPath).Length : 0;
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("enroll", !activeBefore, $"charId={ourCharacterId} name={BotName} activeBefore={activeBefore} (REQUIRE False)", setupSw.Elapsed.TotalSeconds);
            if (activeBefore)
                Fail("HARNESS", "autonomy-preempted", new { ourCharacterId }, "quest already active after enroll wake; re-run with a fresh account");


            // ---- Shared pre-chain fixture (G3 pattern): level 10 + refill + teleport to 3512 ----
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);
            {
                var rsw = Stopwatch.StartNew();
                double hpFrac = double.NaN, mpFrac = double.NaN;
                try
                {
                    bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"itemTemplate\":8518,\"count\":1}}", 30_000);
                    bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"use\",\"itemTemplate\":8518}}", 30_000);
                    Thread.Sleep(2000);
                    bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"itemTemplate\":8519,\"count\":1}}", 30_000);
                    bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"use\",\"itemTemplate\":8519}}", 30_000);
                }
                catch (Exception ex) { evidence.AppendLine($"- refill call failed ({ex.GetType().Name}: {ex.Message})"); }
                var deadline = Environment.TickCount64 + 15_000;
                while (Environment.TickCount64 < deadline)
                {
                    try
                    {
                        var cs = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);
                        var hp = GetInt(cs, "hp"); var maxHp = GetInt(cs, "maxHp");
                        var mp = GetInt(cs, "mp"); var maxMp = GetInt(cs, "maxMp");
                        if (maxHp > 0) hpFrac = (double)hp / maxHp;
                        if (maxMp > 0) mpFrac = (double)mp / maxMp;
                        if (hpFrac >= 0.95 && mpFrac >= 0.95) break;
                    }
                    catch { break; }
                    Thread.Sleep(1000);
                }
                var ok = hpFrac >= 0.95 && mpFrac >= 0.95;
                Leg("fixture-refill", ok, $"hpFrac={hpFrac:0.000} mpFrac={mpFrac:0.000} (REQUIRE both >=0.95)", rsw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("HARNESS", "refill-ineffective", new { hpFrac, mpFrac }, "fixture refill ineffective; refusing autonomy START");
            }
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverReporter3512}}}", 60_000);
            var giverObjId = PollNpcObjId(bridge, GiverReporter3512, 45);
            if (giverObjId == 0)
            {
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{GiverReporter3512}}}", 60_000);
                giverObjId = PollNpcObjId(bridge, GiverReporter3512, 45);
            }
            Leg("fixture-giver", giverObjId != 0, $"reporter 3512 objId={giverObjId} (REQUIRE live)", 0);
            if (giverObjId == 0)
                Fail("HARNESS", "giver-absent", new { }, "reporter/giver 3512 never materialized after teleport retry");
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- LINK G3: autonomous accept 251 at live 3512 (ONE wake, then observe) ----
            {
                var sw = Stopwatch.StartNew();
                var preStart = Observe(bridge);
                var actionsPre = GetInt(preStart, "questActionCount");
                JsonElement obs;
                try { obs = Wake(bridge, WakeWaitMs); }
                catch { Thread.Sleep(2000); obs = Observe(bridge); }
                try { obs = Observe(bridge); } catch { }
                var activeAfter = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
                var accepts = GetInt(obs, "accepts");
                var lastA = GetStr(obs, "lastAction");
                var lastR = GetStr(obs, "lastResult");
                var actionsPost = GetInt(obs, "questActionCount");
                g3AcceptEvidence = $"active={activeAfter} accepts={accepts} last={lastA}/{lastR} actions={actionsPre}->{actionsPost}";
                var ok = activeAfter && accepts >= 1 && lastA == "AcceptQuest" && lastR == "Completed";
                Leg("G3-accept", ok, g3AcceptEvidence + " (REQUIRE active + accepts>=1 + last AcceptQuest/Completed)", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G3", accepts == 0 && actionsPost == actionsPre ? "no-quest-activity" : "leg-dispatched-no-accept",
                        new { activeAfter, accepts, lastA, lastR, actionsPre, actionsPost }, "autonomous accept of 251 at 3512 failed its G3 assertion");
            }

            // ---- LINK G4: select live 3475 via SetTarget (teleport fixture per G4, ≤45m) ----
            uint targetObjId = 0;
            {
                var sw = Stopwatch.StartNew();
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{Boar3475}}}", 60_000);
                var liveBoar = PollNpcObjId(bridge, Boar3475);
                var charPos = ReadPos(bridge);
                var spawner = ReadNpcPos(bridge, liveBoar);
                var flatToSpawner = FlatDist(charPos, spawner);
                Leg("G4-stage", liveBoar != 0 && flatToSpawner <= 45.0, $"live3475={liveBoar} flat={flatToSpawner:0.0}m (REQUIRE live + <=45m)", sw.Elapsed.TotalSeconds);
                if (liveBoar == 0 || flatToSpawner > 45.0)
                    Fail("HARNESS", "G4-stage-unresolved", new { liveBoar, flatToSpawner }, "G4 staging: no live 3475 in range");
                JsonElement obs;
                try { obs = Wake(bridge, WakeWaitMs); }
                catch { Thread.Sleep(2000); obs = Observe(bridge); }
                var decide = GetStr(obs, "questDecideDetail");
                var m = Regex.Match(decide, @"landed Target \(targeting (\d+)\)");
                var selected = m.Success ? uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                // Distilled template proof (documented): the funnel-log parse of
                // the frozen G4 gate is replaced by ObjId equality with the live
                // nearest 3475 poll — the leg's nearest+ObjId tiebreak selects
                // the nearest live 3475, which is what the poll returns.
                var ok = selected != 0 && selected == liveBoar;
                targetObjId = selected != 0 ? selected : liveBoar;
                Leg("G4-select", ok, $"selected={selected} live3475={liveBoar} (REQUIRE equal, via SetTarget) decide=[{Trim(decide, 200)}]", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G4", selected == 0 ? "no-selection" : "stale-or-foreign-selection", new { selected, liveBoar, decide }, "objective-target leg failed its G4 assertion");
            }

            // ---- LINK G5: pursue to ≤3.0m + Stop, zero combat (place +15m fixture per G5) ----
            int meatAtG5 = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            {
                var sw = Stopwatch.StartNew();
                var spawner = SpawnerOf(bridge, Boar3475);
                PlaceAt(bridge, spawner, 15.0);
                var stagedDist = FlatDist(ReadPos(bridge), spawner);
                Leg("G5-stage", stagedDist > 10.0 && stagedDist < 20.0, $"staged dist={stagedDist:0.0}m (REQUIRE 10-20m per G5 fixture)", sw.Elapsed.TotalSeconds);
                if (!(stagedDist > 10.0 && stagedDist < 20.0))
                    Fail("HARNESS", "G5-stage-range", new { stagedDist }, "G5 place fixture missed the 10-20m band");
                var deadline = Environment.TickCount64 + 300_000;
                double minDist = double.MaxValue;
                bool sawMove = false, sawStop = false;
                var combatHits = new List<string>();
                var decideMark = decideHistory.Count;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    try { obs = Observe(bridge); } catch { }
                    var d = FlatDist(ReadPos(bridge), CurrentTargetPos(bridge, targetObjId, spawner));
                    if (!double.IsNaN(d) && d < minDist) minDist = d;
                    foreach (var row in decideHistory.Skip(decideMark))
                    {
                        if (row.StartsWith("landed Move", StringComparison.Ordinal)) sawMove = true;
                        if (row.StartsWith("landed Stop", StringComparison.Ordinal)) sawStop = true;
                        foreach (Match cm in Regex.Matches(row, @"landed (Cast|AutoAttack|Loot)\b"))
                            combatHits.Add(cm.Groups[1].Value);
                    }
                    decideMark = decideHistory.Count;
                    if (minDist <= 3.0 && sawStop) break;
                }
                var meatNow = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var ok = minDist <= 3.0 && sawStop && combatHits.Count == 0 && meatNow == meatAtG5;
                Leg("G5-pursue", ok, $"start={stagedDist:0.0}m min={minDist:0.0}m (REQUIRE <=3.0) move={sawMove} stop={sawStop} (REQUIRE) combat=[{string.Join(",", combatHits)}] (REQUIRE empty) meat={meatAtG5}->{meatNow}", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G5",
                        minDist > 3.0 ? "range-never-reached" : !sawStop ? "no-stop" : combatHits.Count != 0 ? "combat-detected" : "meat-changed",
                        new { stagedDist, minDist, sawMove, sawStop, combatHits, meatAtG5, meatNow }, "pursuit leg failed its G5 assertion");
            }

            // ---- LINK G6: one AutoAttack + HP fall, never Cast (place +2m fixture per G6) ----
            int g6StartHp = -1;
            {
                var sw = Stopwatch.StartNew();
                var spawner = SpawnerOf(bridge, Boar3475);
                var dNow = FlatDist(ReadPos(bridge), CurrentTargetPos(bridge, targetObjId, spawner));
                if (double.IsNaN(dNow) || dNow < 1.0 || dNow > 3.0)
                {
                    PlaceAt(bridge, spawner, 2.0);
                    dNow = FlatDist(ReadPos(bridge), spawner);
                }
                Leg("G6-stage", dNow >= 1.0 && dNow <= 3.0, $"dist={dNow:0.0}m (REQUIRE 1-3m per G6 fixture)", sw.Elapsed.TotalSeconds);
                if (!(dNow >= 1.0 && dNow <= 3.0))
                    Fail("HARNESS", "G6-stage-range", new { dNow }, "G6 place fixture missed the 1-3m band");
                g6StartHp = ReadNpcHp(bridge, targetObjId);
                var qs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                Leg("G6-start", g6StartHp > 0 && qs.Active, $"target={targetObjId} hp={g6StartHp} quest={qs.Step}/{qs.Status} active={qs.Active}", sw.Elapsed.TotalSeconds);
                if (!(g6StartHp > 0 && qs.Active))
                    Fail("HARNESS", "G6-start-unusable", new { targetObjId, g6StartHp, qs }, "G6 START outside the combat window");
                var deadline = Environment.TickCount64 + 300_000;
                var decideMark = decideHistory.Count;
                int autoCount = 0, hpMin = g6StartHp;
                var castHits = 0;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    try { obs = Observe(bridge); } catch { }
                    foreach (var row in decideHistory.Skip(decideMark))
                    {
                        foreach (Match am in Regex.Matches(row, @"landed AutoAttack \(([^)]*)\)"))
                        {
                            autoCount++;
                            evidence.AppendLine($"- G6 landed AutoAttack: [{am.Groups[1].Value}]");
                        }
                        if (Regex.IsMatch(row, @"landed Cast\b")) castHits++;
                    }
                    decideMark = decideHistory.Count;
                    var hp = ReadNpcHp(bridge, targetObjId);
                    if (hp >= 0 && hp < hpMin) hpMin = hp;
                    if (autoCount >= 1 && hpMin < g6StartHp) break;
                }
                var meatNow = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var ok = autoCount == 1 && hpMin < g6StartHp && castHits == 0 && meatNow == meatAtG5;
                Leg("G6-combat", ok, $"auto={autoCount} (REQUIRE exactly 1) hp={g6StartHp}->{hpMin} (REQUIRE fall) cast={castHits} (REQUIRE 0) meat={meatAtG5}->{meatNow}", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G6",
                        autoCount == 0 ? "no-proposal" : autoCount > 1 ? "multi-attack" : hpMin >= g6StartHp ? "hp-no-fall" : castHits != 0 ? "cast-detected" : "meat-changed",
                        new { autoCount, g6StartHp, hpMin, castHits, meatAtG5, meatNow }, "combat leg failed its G6 assertion");
            }

            // ---- LINK G7a: sustain to Hp==0 + teardown (IsAutoAttack false), deltas 0 ----
            long moneyAtKill = -1;
            {
                var sw = Stopwatch.StartNew();
                moneyAtKill = ReadMoney(bridge);
                var deadline = Environment.TickCount64 + 600_000;
                var decideMark = decideHistory.Count;
                bool dead = false;
                int deathWake = 0, wakes = 0, lastHp = g6StartHp;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    wakes++;
                    try { obs = Observe(bridge); } catch { }
                    var hp = ReadNpcHp(bridge, targetObjId);
                    if (hp >= 0) lastHp = hp;
                    if (hp == 0) { dead = true; deathWake = wakes; break; }
                    if (hp == -1)
                    {
                        // ObjId gone: despawn corroboration only if last HP was low/dead.
                        var re = PollNpcObjId(bridge, Boar3475, 3);
                        if (re != targetObjId && lastHp <= 0) { dead = true; deathWake = wakes; break; }
                    }
                }
                if (!dead)
                    Fail("G7a", "no-kill", new { targetObjId, lastHp, wakes }, "kill watch timed out without authoritative Hp==0");
                // Teardown watch: short wakes to IsAutoAttack false.
                var tDeadline = Environment.TickCount64 + 120_000;
                bool tornDown = false;
                var postDeathOurs = new List<string>();
                int tWakes = 0;
                while (Environment.TickCount64 < tDeadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, 5000, 60_000); }
                    catch { Thread.Sleep(1000); continue; }
                    tWakes++;
                    try { obs = Observe(bridge); } catch { }
                    foreach (var row in decideHistory.Skip(decideMark))
                        foreach (Match am in Regex.Matches(row, @"landed AutoAttack \(([^)]*)\)"))
                            if (am.Groups[1].Value.Contains(targetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                                postDeathOurs.Add(am.Groups[1].Value);
                    decideMark = decideHistory.Count;
                    if (!GetBool(obs, "isAutoAttack")) { tornDown = true; break; }
                }
                var meatNow = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var moneyNow = ReadMoney(bridge);
                var ok = dead && tornDown && postDeathOurs.Count == 0 && meatNow == meatAtG5 && moneyNow == moneyAtKill;
                Leg("G7a-kill", ok, $"target={targetObjId} hp->{0} at wake#{deathWake} tornDown={tornDown} (REQUIRE IsAutoAttack false) postDeathOurs={postDeathOurs.Count} (REQUIRE 0) meat={meatAtG5}->{meatNow} money={moneyAtKill}->{moneyNow}", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G7a",
                        !tornDown ? "not-torn-down" : postDeathOurs.Count != 0 ? "post-death-landing" : "credit-leak",
                        new { targetObjId, deathWake, tornDown, postDeathOurs, meatAtG5, meatNow, moneyAtKill, moneyNow }, "kill leg failed its G7a assertion");
            }

            // ---- LINK G7b: corpse named quest-side, zero loot, quest Progress ----
            {
                var sw = Stopwatch.StartNew();
                var hpAtStart = ReadNpcHp(bridge, targetObjId);
                var qsAtStart = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                if (hpAtStart != 0 || !qsAtStart.Active)
                    Fail("HARNESS", "corpse-gone", new { targetObjId, hpAtStart, qsAtStart }, "corpse START outside the observable window (hp!=0 or quest inactive); never a loot failure");
                Leg("G7b-start", true, $"corpse={targetObjId} hp={hpAtStart} quest={qsAtStart.Step}/{qsAtStart.Status} active={qsAtStart.Active}", sw.Elapsed.TotalSeconds);
                var deadline = Environment.TickCount64 + 180_000;
                var decideMark = decideHistory.Count;
                var detectionHits = new List<string>();
                var lootHits = new List<string>();
                int wakes = 0;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    wakes++;
                    try { obs = Observe(bridge); } catch { }
                    foreach (var row in decideHistory.Skip(decideMark))
                    {
                        if (row.Contains(targetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                            && (row.Contains("lootable", StringComparison.OrdinalIgnoreCase) || row.Contains("corpse", StringComparison.OrdinalIgnoreCase) || row.Contains("loot", StringComparison.OrdinalIgnoreCase)))
                            detectionHits.Add(row);
                        foreach (Match lm in Regex.Matches(row, @"landed Loot\b"))
                            lootHits.Add(lm.Value);
                    }
                    decideMark = decideHistory.Count;
                    if (detectionHits.Count >= 1) break;
                }
                // Light game-log corroboration (same ReadQuestLines pattern as the gates).
                var logHits = ReadLogLines(gameLogOffset, gameRestartLogOffset, ourCharacterId, $"corpse={targetObjId}");
                foreach (var lh in logHits.Where(l => l.Contains("lootable", StringComparison.OrdinalIgnoreCase)))
                    detectionHits.Add("LOG: " + lh);
                var meatNow = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var qsEnd = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var ok = detectionHits.Count >= 1 && lootHits.Count == 0 && meatNow == meatAtG5 && qsEnd.Active;
                Leg("G7b-corpse", ok, $"detection={detectionHits.Count} (REQUIRE >=1 naming {targetObjId}) loot={lootHits.Count} (REQUIRE 0) meat={meatAtG5}->{meatNow} quest={qsEnd.Step}/{qsEnd.Status}", sw.Elapsed.TotalSeconds);
                foreach (var d in detectionHits.Take(5)) evidence.AppendLine($"- G7b detection: [{Trim(d, 220)}]");
                if (!ok)
                    Fail("G7b",
                        detectionHits.Count == 0 ? "unobserved" : lootHits.Count != 0 ? "dispatched" : "credit-leak",
                        new { targetObjId, detectionHits = detectionHits.Take(5).ToList(), lootHits, meatAtG5, meatNow, qsEnd }, "corpse leg failed its G7b assertion");
            }

            // ---- LINK G7c: loot ONCE + conservation grant, quest still Progress (no credit assert) ----
            int meatAfterFirstLoot = -1;
            {
                var sw = Stopwatch.StartNew();
                var deadline = Environment.TickCount64 + 180_000;
                var decideMark = decideHistory.Count;
                int lootCount = 0;
                bool lootCompleted = false;
                while (Environment.TickCount64 < deadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, 2000, 60_000); }
                    catch { Thread.Sleep(1000); continue; }
                    try { obs = Observe(bridge); } catch { }
                    foreach (var row in decideHistory.Skip(decideMark))
                    {
                        foreach (Match lm in Regex.Matches(row, @"landed Loot \(([^)]*)\)"))
                        {
                            if (lm.Groups[1].Value.Contains(targetObjId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                            {
                                lootCount++;
                                if (lm.Groups[1].Value.Contains("Completed", StringComparison.Ordinal)) lootCompleted = true;
                                evidence.AppendLine($"- G7c landed Loot: [{lm.Groups[1].Value}]");
                            }
                            else
                            {
                                lootCount++;
                                evidence.AppendLine($"- G7c FOREIGN landed Loot: [{lm.Groups[1].Value}]");
                            }
                        }
                        if (Regex.IsMatch(row, @"landed Cast\b"))
                            Fail("G7c", "cast-detected", new { row }, "Cast landed during the loot watch");
                    }
                    decideMark = decideHistory.Count;
                    if (lootCount >= 1) break;
                }
                meatAfterFirstLoot = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var qsEnd = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                // Distilled conservation (documented): the frozen G7c gate's exact
                // grant==containerBefore-remaining equality needs its funnel-log
                // scanner; the sweep asserts the observable conservation core —
                // exactly one canonical Loot for OUR corpse + Completed + every
                // bag delta a gain (meat>=before, money flat) + quest Progress.
                var moneyNow = ReadMoney(bridge);
                var ok = lootCount == 1 && lootCompleted && meatAfterFirstLoot >= meatAtG5 && moneyNow == moneyAtKill && qsEnd.Active;
                Leg("G7c-loot", ok, $"loot={lootCount} (REQUIRE exactly 1, OUR {targetObjId}) completed={lootCompleted} meat={meatAtG5}->{meatAfterFirstLoot} (REQUIRE gain-or-proven-dry) money={moneyAtKill}->{moneyNow} quest={qsEnd.Step}/{qsEnd.Status}", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G7c",
                        lootCount == 0 ? "not-dispatched" : lootCount > 1 ? "double-dispatch" : !lootCompleted ? "not-completed" : meatAfterFirstLoot < meatAtG5 ? "gains" : "not-progress",
                        new { targetObjId, lootCount, lootCompleted, meatAtG5, meatAfterFirstLoot, moneyAtKill, moneyNow, qsEnd }, "loot leg failed its G7c assertion");
            }

            // ---- LINK G7d: repeat kill/loot to meat ×3 → objectives 3 → Ready, held ----
            {
                var sw = Stopwatch.StartNew();
                var cycles = 0;
                const int maxCycles = 4;
                var meat = meatAfterFirstLoot;
                while (meat < MeatRequired && cycles < maxCycles - 1)
                {
                    cycles++;
                    // Per-cycle fixture (G7d pattern): fresh-stage ~2m + settle +
                    // observe-only drive to G6 end state + kill + corpse re-take + loot.
                    bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{Boar3475}}}", 60_000);
                    var spawner = SpawnerOf(bridge, Boar3475);
                    PlaceAt(bridge, spawner, 2.0);
                    var fresh = PollNpcObjId(bridge, Boar3475);
                    var hp = ReadNpcHp(bridge, fresh);
                    if (fresh == 0 || hp <= 0) { evidence.AppendLine($"- G7d cycle{cycles}: no fresh live boar (fresh={fresh} hp={hp}); consuming spare"); continue; }
                    // Drive to G6 end state: wakes until AutoAttack landed + HP fallen.
                    var dDeadline = Environment.TickCount64 + 300_000;
                    var dMark = decideHistory.Count;
                    bool auto = false;
                    int hpMin = hp;
                    while (Environment.TickCount64 < dDeadline)
                    {
                        try { Wake(bridge, WakeWaitMs); } catch { Thread.Sleep(2000); continue; }
                        try { Observe(bridge); } catch { }
                        foreach (var row in decideHistory.Skip(dMark))
                            if (Regex.IsMatch(row, @"landed AutoAttack \(([^)]*)\)")) auto = true;
                        dMark = decideHistory.Count;
                        var h = ReadNpcHp(bridge, fresh);
                        if (h >= 0 && h < hpMin) hpMin = h;
                        if (auto && hpMin < hp) break;
                    }
                    if (!auto) { evidence.AppendLine($"- G7d cycle{cycles}: drive never reached G6 end state; consuming spare"); continue; }
                    // Kill watch.
                    var kDeadline = Environment.TickCount64 + 600_000;
                    bool dead = false;
                    while (Environment.TickCount64 < kDeadline)
                    {
                        try { Wake(bridge, WakeWaitMs); } catch { Thread.Sleep(2000); continue; }
                        try { Observe(bridge); } catch { }
                        if (ReadNpcHp(bridge, fresh) == 0) { dead = true; break; }
                    }
                    if (!dead) { evidence.AppendLine($"- G7d cycle{cycles}: no kill; consuming spare"); continue; }
                    // Loot watch.
                    var lDeadline = Environment.TickCount64 + 180_000;
                    var lMark = decideHistory.Count;
                    bool looted = false;
                    while (Environment.TickCount64 < lDeadline)
                    {
                        try { Wake(bridge, 2000, 60_000); } catch { Thread.Sleep(1000); continue; }
                        try { Observe(bridge); } catch { }
                        foreach (var row in decideHistory.Skip(lMark))
                            if (Regex.IsMatch(row, @"landed Loot \(([^)]*)\)")) looted = true;
                        lMark = decideHistory.Count;
                        if (looted) break;
                    }
                    meat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                    evidence.AppendLine($"- G7d cycle{cycles}: corpse={fresh} looted={looted} meat={meat}");
                    if (meat >= MeatRequired) break;
                }
                var qs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var ready = IsReady(qs);
                var hasCompleted = E2eQuestDriver.HasCompleted(bridge, BotName, Quest251);
                var meatFinal = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var ok = meatFinal >= MeatRequired && qs.Objectives.Length > 0 && qs.Objectives[0] >= MeatRequired && ready && qs.Active && !hasCompleted;
                Leg("G7d-credit", ok, $"meat trajectory {meatAtG5}->{meatFinal} (REQUIRE >={MeatRequired}) objectives=[{string.Join(",", qs.Objectives)}] quest={qs.Step}/{qs.Status} (REQUIRE Ready) active={qs.Active} completed={hasCompleted} (REQUIRE False: still held)", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G7d",
                        meatFinal < MeatRequired ? "no-grant" : qs.Objectives.Length == 0 || qs.Objectives[0] < MeatRequired ? "no-event" : !ready ? "no-progress" : "not-held",
                        new { meatAtG5, meatFinal, qs, hasCompleted }, "credit leg failed its G7d assertion");
            }

            // ---- LINK G8a: reporter resolves stably while TurnIn withheld (withhold ON here) ----
            uint epochReporter = 0;
            {
                var sw = Stopwatch.StartNew();
                var repProbe = Observe(bridge, GiverReporter3512);
                var repX = GetDbl(repProbe, "reporterX"); var repY = GetDbl(repProbe, "reporterY"); var repZ = GetDbl(repProbe, "reporterZ");
                if (double.IsNaN(repX) || double.IsNaN(repY) || double.IsNaN(repZ))
                    Fail("HARNESS", "G8a-reporter-position", new { }, "reporter 3512 position unavailable in observe probe");
                PlaceAtCoords(bridge, repX + 18.0, repY, repZ);
                var holdObs = Observe(bridge, GiverReporter3512);
                var holdReporter = GetUInt(holdObs, "reporterObjId");
                if (holdReporter == 0) holdReporter = PollNpcObjId(bridge, GiverReporter3512, 10);
                var holdDist = GetDbl(holdObs, "reporterDistM");
                var holdTemplate = GetTemplate(bridge, holdReporter);
                var holdOk = holdReporter != 0 && holdTemplate == GiverReporter3512 && holdDist > 10.0 && holdDist < 25.0;
                Leg("G8a-stage", holdOk, $"reporter={holdReporter} template={holdTemplate} dist={holdDist:0.0}m (REQUIRE live + 3512 + 10-25m ONCE, inside sweep)", sw.Elapsed.TotalSeconds);
                if (!holdOk)
                    Fail("HARNESS", "G8a-stage-hold", new { holdReporter, holdTemplate, holdDist }, "G8a staging missed the inside-sweep band");
                // WITHHOLD ON (G8a pattern: armed AFTER staging so no fixture
                // wake runs Ready-unprotected; stays ON through G8b and G8c staging).
                var whResp = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"withhold\",\"on\":true}}", 30_000);
                var whArmed = whResp.TryGetProperty("withholdTurnIn", out var whEl) && whEl.ValueKind == JsonValueKind.True;
                Leg("G8a-withhold-arm", whArmed, $"withholdTurnIn={whArmed} (REQUIRED: Ready survives only while ON)", sw.Elapsed.TotalSeconds);
                if (!whArmed)
                    Fail("HARNESS", "withhold-refused", new { }, "withhold ON refused at G8a arm");
                epochReporter = holdReporter;
                var moneyBefore = ReadMoney(bridge);
                const int tickQuorum = 10;
                const int maxWakes = 40;
                var winDeadline = Environment.TickCount64 + 150_000;
                int ticks = 0, wakes = 0;
                var dMark = decideHistory.Count;
                while (ticks < tickQuorum && wakes < maxWakes && Environment.TickCount64 < winDeadline)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    wakes++;
                    var decide = GetStr(obs, "questDecideDetail");
                    var stepped = obs.TryGetProperty("stepped", out var stEl) && stEl.ValueKind == JsonValueKind.True;
                    if (string.IsNullOrEmpty(decide) || !stepped) { Thread.Sleep(5000); continue; }
                    ticks++;
                    var mRep = Regex.Match(decide, @"reporter=(\d+)");
                    var namedRep = mRep.Success ? uint.Parse(mRep.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    if (!decide.StartsWith("WITHHELD: withheld turn-in TurnInQuest quest 251", StringComparison.Ordinal) || namedRep != epochReporter)
                    {
                        // Respawn re-baselines the epoch (G8a pattern): only a
                        // flap while the old ObjId is still live is fatal.
                        var oldLive = PollNpcObjId(bridge, GiverReporter3512, 3) == epochReporter;
                        if (namedRep != epochReporter && oldLive)
                            Fail("G8a", "flap-replacement", new { epochReporter, namedRep, decide }, "reporter ObjId flapping while the old ObjId is still live");
                        if (namedRep != 0) { epochReporter = namedRep; evidence.AppendLine($"- G8a epoch re-baselined to reporter={epochReporter}"); }
                        else
                            Fail("G8a", "no-proposal", new { ticks, decide }, "tick wake without the WITHHELD TurnInQuest-251 proposal");
                    }
                    var qs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                    if (!qs.Active || !IsReady(qs))
                        Fail("G8a", "quest-lost", new { ticks, qs }, "251 not Ready/active on a withheld tick with no turn-in explaining it");
                    foreach (var row in decideHistory.Skip(dMark))
                        if (Regex.IsMatch(row, @"^landed (TurnInQuest|TurnInDoodad|AutoTurnIn|Move|Cast|Loot|InteractNpc|AcceptQuest|AdvanceQuest)"))
                            Fail("G8a", "leaked-dispatch", new { ticks, row }, "gameplay verb landed while withhold must hold");
                    dMark = decideHistory.Count;
                }
                var moneyNow = ReadMoney(bridge);
                var ok = ticks >= tickQuorum && moneyNow == moneyBefore;
                Leg("G8a-resolve", ok, $"ticks={ticks}/{tickQuorum} (REQUIRE quorum) reporter={epochReporter} stable money={moneyBefore}->{moneyNow} (REQUIRE flat)", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G8a", ticks == 0 ? "no-ticks" : "quorum-missed", new { ticks, wakes, epochReporter }, "reporter-resolve window closed without tick quorum");
            }

            // ---- LINK G8b: return leg closes to ≤25m, then InteractNpc dialogue (withhold stays ON) ----
            {
                var sw = Stopwatch.StartNew();
                var repProbe = Observe(bridge, GiverReporter3512);
                var repX = GetDbl(repProbe, "reporterX"); var repY = GetDbl(repProbe, "reporterY"); var repZ = GetDbl(repProbe, "reporterZ");
                if (double.IsNaN(repX) || double.IsNaN(repY) || double.IsNaN(repZ))
                    Fail("HARNESS", "G8b-reporter-position", new { }, "reporter 3512 position unavailable in observe probe");
                PlaceAtCoords(bridge, repX + 35.0, repY, repZ);
                var startObs = Observe(bridge, GiverReporter3512);
                var startDist = GetDbl(startObs, "reporterDistM");
                var startReporter = GetUInt(startObs, "reporterObjId");
                if (startReporter == 0) startReporter = PollNpcObjId(bridge, GiverReporter3512, 10);
                var startTemplate = GetTemplate(bridge, startReporter);
                var startQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var whEcho = startObs.TryGetProperty("withholdTurnIn", out var swh) && swh.ValueKind == JsonValueKind.True;
                // One-shot preempt Stop (staging, G8b pattern) + bounded idle verify.
                try
                {
                    using var http = await EnsureWebApiClientAsync(evidence);
                    using var sc = new System.Net.Http.StringContent($"{{\"bot\":\"{BotName}\"}}", Encoding.UTF8, "application/json");
                    using var sresp = await http.PostAsync("/api/actors/stop", sc);
                    evidence.AppendLine($"- G8b preempt: POST /api/actors/stop -> {(int)sresp.StatusCode}");
                }
                catch (Exception ex) { evidence.AppendLine($"- G8b preempt attempt failed ({ex.GetType().Name}: {ex.Message})"); }
                var idleDeadline = Environment.TickCount64 + 15_000;
                bool idle = false;
                while (Environment.TickCount64 < idleDeadline)
                {
                    try
                    {
                        var io = Observe(bridge);
                        if (!(GetStr(io, "liveAction") == "Move" && GetStr(io, "liveState") == "Running")) { idle = true; break; }
                    }
                    catch { break; }
                    Thread.Sleep(1000);
                }
                var stageOk = startReporter != 0 && startTemplate == GiverReporter3512 && startDist > 25.5 && startDist < 60.0
                    && startQs.Active && IsReady(startQs) && whEcho && idle;
                Leg("G8b-stage", stageOk, $"reporter={startReporter} template={startTemplate} dist={startDist:0.0}m (REQUIRE 25.5-60, OUTSIDE gate) quest=Ready/active withhold={whEcho} idle={idle}", sw.Elapsed.TotalSeconds);
                if (!stageOk)
                    Fail("HARNESS", "G8b-stage-unusable", new { startReporter, startTemplate, startDist, startQs, whEcho, idle }, "G8b START requirements unmet");
                var winDeadline = Environment.TickCount64 + 300_000;
                const int maxWakes = 80;
                const int confirmWakes = 3;
                int wakes = 0, confirm = 0;
                double minDist = startDist;
                bool sawReturnMove = false, sawStop = false, dialogue = false;
                var dMark = decideHistory.Count;
                var turnInsBefore = GetInt(startObs, "turnIns");
                var completedBefore = GetInt(startObs, "completedCount");
                var meatBefore = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var rewardBefore = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
                double endDist = startDist;
                while (Environment.TickCount64 < winDeadline && wakes < maxWakes)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    wakes++;
                    string decide;
                    try { var o2 = Observe(bridge, GiverReporter3512); decide = GetStr(o2, "questDecideDetail"); endDist = GetDbl(o2, "reporterDistM"); }
                    catch { decide = GetStr(obs, "questDecideDetail"); }
                    if (!double.IsNaN(endDist) && endDist < minDist) minDist = endDist;
                    foreach (var row in decideHistory.Skip(dMark))
                    {
                        if (row.StartsWith("landed Move", StringComparison.Ordinal)) sawReturnMove = true;
                        if (row.StartsWith("landed Stop", StringComparison.Ordinal)) sawStop = true;
                        if (row.StartsWith("landed InteractNpc", StringComparison.Ordinal)
                            && row.Contains("dialogue", StringComparison.Ordinal) && row.Contains("3512", StringComparison.Ordinal))
                            dialogue = true;
                        if (Regex.IsMatch(row, @"^landed (TurnInQuest|TurnInDoodad|AutoTurnIn)"))
                            Fail("G8b", "leaked-dispatch", new { wakes, row }, "turn-in landed while withhold must hold through G8b");
                        foreach (Match fm in Regex.Matches(row, @"landed (Cast|Loot|AcceptQuest|AdvanceQuest)\b"))
                            Fail("G8b", "leaked-dispatch", new { wakes, foreign = fm.Groups[1].Value }, "foreign gameplay verb landed during return");
                    }
                    dMark = decideHistory.Count;
                    if (dialogue) { confirm++; if (confirm >= confirmWakes) break; }
                    else confirm = 0;
                }
                var endObs = Observe(bridge, GiverReporter3512);
                var endQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var meatNow = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var rewardNow = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
                var ok = dialogue && minDist <= 25.0 && endDist <= 25.0 && endDist < startDist
                    && endQs.Active && IsReady(endQs)
                    && GetInt(endObs, "turnIns") == turnInsBefore && GetInt(endObs, "completedCount") == completedBefore
                    && meatNow == meatBefore && rewardNow == rewardBefore;
                // BotMoveStart owner=RETURN_MOVE_TO_UNIT proof (same log-grep pattern as the gates).
                var ownerLines = ReadLogLines(gameLogOffset, gameRestartLogOffset, ourCharacterId, "RETURN_MOVE_TO_UNIT")
                    .Where(l => l.Contains("BotMoveStart", StringComparison.Ordinal)).ToList();
                Leg("G8b-return", ok && ownerLines.Count >= 1, $"dist {startDist:0.0}->{minDist:0.0}m end={endDist:0.0}m (REQUIRE >25 -> <=25, end<start) move={sawReturnMove} stop={sawStop} dialogue={dialogue} (REQUIRE) ownerLines={ownerLines.Count} (REQUIRE >=1) quest=Ready/active meat={meatBefore}->{meatNow} reward={rewardBefore}->{rewardNow}", sw.Elapsed.TotalSeconds);
                evidence.AppendLine($"- G8b owner proof lines: {ownerLines.Count}");
                if (!ok || ownerLines.Count == 0)
                    Fail("G8b",
                        !dialogue ? "no-dialogue" : minDist > 25.0 ? "no-progress" : !sawReturnMove ? "no-leg" : ownerLines.Count == 0 ? "no-owner-proof" : "quest-lost",
                        new { startDist, minDist, endDist, sawReturnMove, sawStop, dialogue, ownerLines = ownerLines.Count, endQs, meatBefore, meatNow }, "return leg failed its G8b assertion");
            }

            // ---- LINK G8c: withhold released at START → turn-in + reward 18791 ----
            {
                var sw = Stopwatch.StartNew();
                var repProbe = Observe(bridge, GiverReporter3512);
                var repX = GetDbl(repProbe, "reporterX"); var repY = GetDbl(repProbe, "reporterY"); var repZ = GetDbl(repProbe, "reporterZ");
                if (double.IsNaN(repX) || double.IsNaN(repY) || double.IsNaN(repZ))
                    Fail("HARNESS", "G8c-reporter-position", new { }, "reporter 3512 position unavailable in observe probe");
                PlaceAtCoords(bridge, repX + 12.0, repY, repZ);
                var startObs = Observe(bridge, GiverReporter3512);
                var startReporter = GetUInt(startObs, "reporterObjId");
                if (startReporter == 0) startReporter = PollNpcObjId(bridge, GiverReporter3512, 10);
                var startDist = GetDbl(startObs, "reporterDistM");
                var startTemplate = GetTemplate(bridge, startReporter);
                var startQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var startTurnIns = GetInt(startObs, "turnIns");
                var startCompleted = GetInt(startObs, "completedCount");
                var startMoney = ReadMoney(bridge);
                var startMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var startReward = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
                var startMailReward = CountMailReward(bridge);
                var startHasCompleted = E2eQuestDriver.HasCompleted(bridge, BotName, Quest251);
                var startWithhold = startObs.TryGetProperty("withholdTurnIn", out var gswh) && gswh.ValueKind == JsonValueKind.True;
                // 18791 baselines are RECORDED, never required zero (G8c pattern:
                // the character-creation starter kit already carries 18791).
                var stageOk = startReporter != 0 && startTemplate == GiverReporter3512 && startDist > 2.0 && startDist < 25.0
                    && startQs.Active && IsReady(startQs) && !startHasCompleted && startTurnIns == 0 && startMeat == MeatRequired && startWithhold;
                Leg("G8c-stage", stageOk, $"reporter={startReporter} template={startTemplate} dist={startDist:0.0}m (REQUIRE 2-25m) quest=Ready/active completed={startHasCompleted} turnIns={startTurnIns} meat={startMeat} reward={startReward}/mail={startMailReward} withhold={startWithhold} (REQUIRE ON through staging)", sw.Elapsed.TotalSeconds);
                if (!stageOk)
                    Fail("HARNESS", "G8c-stage-unusable", new { startReporter, startTemplate, startDist, startQs, startHasCompleted, startTurnIns, startMeat, startWithhold }, "G8c START requirements unmet");
                // START: release the withhold (EXACTLY where G8c releases it), then observe-only.
                var relResp = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"withhold\",\"on\":false}}", 30_000);
                var released = relResp.TryGetProperty("withholdTurnIn", out var relEl) && relEl.ValueKind == JsonValueKind.False;
                Leg("G8c-release", released, $"withholdTurnIn=false echoed={released} (the G8c START moment)", sw.Elapsed.TotalSeconds);
                if (!released)
                    Fail("G8c", "release-refused", new { }, "withhold OFF refused at G8c START");
                var winDeadline = Environment.TickCount64 + 180_000;
                const int maxWakes = 24;
                const int confirmWakes = 3;
                int wakes = 0, confirm = 0;
                var dMark = decideHistory.Count;
                var landedTurnIn = new List<string>();
                var completedTurnIn = new List<string>();
                var foreign = new List<string>();
                while (Environment.TickCount64 < winDeadline && wakes < maxWakes)
                {
                    JsonElement obs;
                    try { obs = Wake(bridge, WakeWaitMs); }
                    catch { Thread.Sleep(2000); continue; }
                    wakes++;
                    try { Observe(bridge, GiverReporter3512); } catch { }
                    foreach (var row in decideHistory.Skip(dMark))
                    {
                        if (row.StartsWith("landed TurnInQuest", StringComparison.Ordinal))
                        {
                            landedTurnIn.Add(row);
                            if (row.Contains("completed by turn-in", StringComparison.Ordinal)) completedTurnIn.Add(row);
                        }
                        foreach (Match fm in Regex.Matches(row, @"landed (Talk|Cast|Loot|AcceptQuest|AdvanceQuest)\b"))
                            if (!foreign.Contains(fm.Groups[1].Value)) foreign.Add(fm.Groups[1].Value);
                    }
                    dMark = decideHistory.Count;
                    if (completedTurnIn.Count >= 1) { confirm++; if (confirm >= confirmWakes) break; }
                    else confirm = 0;
                }
                var endObs = Observe(bridge, GiverReporter3512);
                var endQs = E2eQuestDriver.QuestState(bridge, BotName, Quest251);
                var endTurnIns = GetInt(endObs, "turnIns");
                var endMeat = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
                var endReward = E2eQuestDriver.InvCount(bridge, BotName, Reward18791);
                var endMailReward = CountMailReward(bridge);
                var endHasCompleted = E2eQuestDriver.HasCompleted(bridge, BotName, Quest251);
                string rewardRoute = endReward - startReward >= 1 ? "bag" : endMailReward - startMailReward >= 1 ? "mail" : "UNRESOLVED";
                var ok = completedTurnIn.Count >= 1 && endHasCompleted && !endQs.Active
                    && endTurnIns - startTurnIns == 1 && rewardRoute != "UNRESOLVED" && endMeat == 0 && foreign.Count == 0;
                Leg("G8c-turnin", ok, $"turnIn={completedTurnIn.Count}/{landedTurnIn.Count} (REQUIRE completed-by-turn-in >=1) completed={endHasCompleted} active={endQs.Active} turnIns={startTurnIns}->{endTurnIns} reward bag {startReward}->{endReward} mail {startMailReward}->{endMailReward} via {rewardRoute} meat {startMeat}->{endMeat} (REQUIRE 3->0) foreign=[{string.Join(",", foreign)}]", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G8c",
                        completedTurnIn.Count == 0 ? "no-dispatch" : !endHasCompleted ? "quest-not-completed" : endQs.Active ? "still-active"
                        : endTurnIns - startTurnIns != 1 ? "turnins" : rewardRoute == "UNRESOLVED" ? "missing" : endMeat != 0 ? "cleanup" : "foreign-verb",
                        new { landed = landedTurnIn.Count, completed = completedTurnIn.Count, endHasCompleted, endQsActive = endQs.Active, startTurnIns, endTurnIns, startReward, endReward, startMailReward, endMailReward, startMeat, endMeat, foreign }, "turn-in leg failed its G8c assertion");
            }

            // ---- LINK G1: explicit +12m move probe (post-chain, state-free; G1 pattern verbatim; runs after G8c so a :1280 route outage cannot blank the autonomous-chain verdicts) ----
            {
                var sw = Stopwatch.StartNew();
                using var http = await AcquireWebApiAsync();
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{G1StageNpc2425}}}", 60_000);
                var stagedNpc = PollNpcObjId(bridge, G1StageNpc2425);
                var startPos = ReadPos(bridge);
                if (stagedNpc == 0)
                    Fail("HARNESS", "G1-stage-unresolved", new { startPos }, "G1 staging NPC 2425 never materialized");
                var s = ParsePos(startPos);
                var tx = s.X + G1StepX;
                var moveBody = $"{{\"bot\":\"{BotName}\",\"x\":{tx:F1},\"y\":{s.Y:F1},\"z\":{s.Z:F1},\"speed\":2.0,\"timeoutSec\":20,\"idempotencyKey\":\"q251sweep-g1-{BotAccount}\"}}";
                var move = await PostJsonAsync(http, "/api/actors/move", moveBody);
                var traceId = move.GetProperty("trace_id").GetGuid();
                var poll = await PollTerminalAsync(http, traceId, TimeSpan.FromSeconds(30));
                var state = poll.GetProperty("state").GetString() ?? "";
                var endPos = ReadPos(bridge);
                var e = ParsePos(endPos);
                var flat = Math.Sqrt((e.X - tx) * (e.X - tx) + (e.Y - s.Y) * (e.Y - s.Y));
                var dz = Math.Abs(e.Z - s.Z);
                var ok = state == "Completed" && flat <= 1.0 && dz <= 1.0;
                Leg("G1-move", ok, $"state={state} flat={flat:0.00}m (REQUIRE <=1.0) dz={dz:0.00}m (REQUIRE <=1.0) start=[{startPos}] target=[{tx:F1},{s.Y:F1},{s.Z:F1}] end=[{endPos}]", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G1", state != "Completed" ? "move-not-completed" : "no-convergence", new { state, flat, dz, startPos, endPos }, "explicit +12m move leg failed its G1 assertion");
            }

            // ---- LINK G2: explicit talk probe (post-chain, quest 532 — cannot hijack 251) ----
            {
                var sw = Stopwatch.StartNew();
                using var http = await AcquireWebApiAsync();
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{G2FixtureLevel}}}", 30_000);
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{G2AcceptNpc2425}}}", 60_000);
                var giver = PollNpcObjId(bridge, G2AcceptNpc2425);
                if (giver == 0)
                    Fail("HARNESS", "G2-stage-unresolved", new { }, "G2 accept NPC 2425 never materialized");
                var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest532},\"acceptorType\":\"Npc\",\"acceptorId\":{G2AcceptNpc2425},\"idempotencyKey\":\"q251sweep-g2a-{BotAccount}\"}}";
                var accept = await PostJsonAsync(http, "/api/actors/accept_quest", acceptBody);
                var acceptPoll = await PollTerminalAsync(http, accept.GetProperty("trace_id").GetGuid(), TimeSpan.FromSeconds(30));
                var acceptOk = (acceptPoll.GetProperty("state").GetString() ?? "") == "Completed" && E2eQuestDriver.IsQuestActive(bridge, BotName, Quest532);
                Leg("G2-accept", acceptOk, $"532 explicit accept state + active (fixture for the talk verb)", sw.Elapsed.TotalSeconds);
                if (!acceptOk)
                    Fail("G2", "accept-refused", new { }, "explicit accept of 532 refused; talk fixture unusable");
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{G2TalkNpc2426}}}", 60_000);
                var talkNpc = PollNpcObjId(bridge, G2TalkNpc2426);
                if (talkNpc == 0)
                    Fail("HARNESS", "G2-talk-stage", new { }, "G2 talk NPC 2426 never materialized");
                var questBefore = ReadQuestState(bridge, Quest532);
                var talkBody = $"{{\"bot\":\"{BotName}\",\"npcObjId\":{talkNpc},\"idempotencyKey\":\"q251sweep-g2t-{BotAccount}\"}}";
                var talk = await PostJsonAsync(http, "/api/actors/talk", talkBody);
                var talkPoll = await PollTerminalAsync(http, talk.GetProperty("trace_id").GetGuid(), TimeSpan.FromSeconds(30));
                var talkState = talkPoll.GetProperty("state").GetString() ?? "";
                var questAfter = ReadQuestState(bridge, Quest532);
                var delta = questBefore != questAfter && !questBefore.StartsWith("UNAVAILABLE", StringComparison.Ordinal) && !questAfter.StartsWith("UNAVAILABLE", StringComparison.Ordinal);
                var ok = talkState == "Completed" && delta;
                Leg("G2-talk", ok, $"state={talkState} (REQUIRE Completed) delta={delta} (REQUIRE quest snapshot change) npc={talkNpc}", sw.Elapsed.TotalSeconds);
                if (!ok)
                    Fail("G2", talkState != "Completed" ? "engine-refused" : "no-observed-delta", new { talkState, questBefore, questAfter }, "talk verb failed its G2 assertion");
            }

            // ---- Q0 DERIVED verdict (documented, not re-runnable mid-chain) ----
            Leg("Q0-accept-derived", true, $"DERIVED-PASS via G3 evidence [{g3AcceptEvidence}]: same engine AddQuest path fired; Q0's explicit-POST form is frozen-proven and would destroy G3 preconditions if re-run", 0);

            passed = true;
            verdict = "PASS-BEHAVIOR";
            failBoundary = "none";
            claim = $"single bot {BotName} (charId={ourCharacterId}) ran the full 251 chain under production autonomy: G1 move + G3 accept + G4 select + G5 pursue + G6 combat + G7a kill + G7b corpse + G7c loot + G7d credit-to-Ready + G8a withheld-resolve + G8b return-dialogue + G8c turn-in-reward + G2 talk, Q0 derived";
            Leg("gate", true, verdict + ": " + claim, 0);
            WriteReport();
            Assert.True(passed);
        }
        finally
        {
            Console.WriteLine($"Q251-SWEEP verdict={verdict} boundary={failBoundary} legs={legs.Count} total={totalWall.Elapsed.TotalSeconds:0.0}s report={ReportPath}");
            try { session?.Dispose(); } catch { }
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
        }
    }

    // ---------------- helpers (copied shapes from the frozen gates; this file only) ----------------

    private static bool IsReady(E2eQuestDriver.QuestStateSnapshot qs)
        => string.Equals(qs.Step, "Ready", StringComparison.Ordinal)
        || string.Equals(qs.Status, "Ready", StringComparison.Ordinal);

    private static string Trim(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static long ReadMoney(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
            if (el.TryGetProperty("money", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out var n)) return n;
        }
        catch { }
        return -1;
    }

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int CountMailReward(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"mails\"}}", 30_000);
            if (!el.TryGetProperty("mails", out var mails) || mails.ValueKind != JsonValueKind.Array)
                return -1;
            var total = 0;
            foreach (var m in mails.EnumerateArray())
            {
                if (!m.TryGetProperty("attachments", out var atts) || atts.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in atts.EnumerateArray())
                {
                    if (a.TryGetProperty("templateId", out var t) && t.TryGetUInt32(out var tid) && tid == Reward18791
                        && a.TryGetProperty("count", out var c) && c.TryGetInt32(out var n))
                        total += n;
                }
            }
            return total;
        }
        catch
        {
            return -1;
        }
    }

    private static uint PollNpcObjId(BotDriveClient bridge, uint templateId, int seconds = 30)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var objId = bridge.Call(
                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{templateId}}}",
                    30_000).GetProperty("objId").GetUInt32();
                if (objId != 0)
                    return objId;
            }
            catch { }
            Thread.Sleep(1000);
        }
        return 0;
    }

    private static uint GetTemplate(BotDriveClient bridge, uint objId)
    {
        if (objId == 0)
            return 0;
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            return el.TryGetProperty("templateId", out var v) && v.TryGetUInt32(out var n) ? n : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int ReadNpcHp(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            return el.TryGetProperty("hp", out var v) && v.TryGetInt32(out var n) ? n : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string ReadNpcPos(BotDriveClient bridge, uint objId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcState\",\"npcObjId\":{objId}}}", 30_000);
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return FormattableString.Invariant($"{x.GetDouble():F1},{y.GetDouble():F1},{z.GetDouble():F1}");
        }
        catch { }
        return "";
    }

    private static string ReadPos(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return FormattableString.Invariant($"{x.GetDouble():F1},{y.GetDouble():F1},{z.GetDouble():F1}");
            return el.ToString();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    private static string ReadQuestState(BotDriveClient bridge, uint questId)
    {
        try
        {
            var qs = E2eQuestDriver.QuestState(bridge, BotName, questId);
            return FormattableString.Invariant($"{qs.Step}/{qs.Status}/[{string.Join(",", qs.Objectives)}]/{qs.Active}");
        }
        catch
        {
            return "UNAVAILABLE (read failed)";
        }
    }

    private static (double X, double Y, double Z) ParsePos(string pos)
    {
        var parts = pos.Split(',');
        if (parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return (x, y, z);
        return (double.NaN, double.NaN, double.NaN);
    }

    private static double FlatDist(string a, string b)
    {
        var pa = ParsePos(a);
        var pb = ParsePos(b);
        if (double.IsNaN(pa.X) || double.IsNaN(pb.X))
            return double.NaN;
        return Math.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y));
    }

    private static string SpawnerOf(BotDriveClient bridge, uint templateId)
    {
        try
        {
            var resp = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{templateId}}}", 60_000);
            if (resp.TryGetProperty("x", out var stx) && resp.TryGetProperty("y", out var sty) && resp.TryGetProperty("z", out var stz))
                return FormattableString.Invariant($"{stx.GetDouble():F1},{sty.GetDouble():F1},{stz.GetDouble():F1}");
        }
        catch { }
        return ReadPos(bridge);
    }

    private static void PlaceAt(BotDriveClient bridge, string spawner, double dx)
    {
        var sp = ParsePos(spawner);
        PlaceAtCoords(bridge, sp.X + dx, sp.Y, sp.Z);
    }

    private static void PlaceAtCoords(BotDriveClient bridge, double x, double y, double z)
    {
        bridge.Call(FormattableString.Invariant(
            $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"place\",\"x\":{x:F1},\"y\":{y:F1},\"z\":{z:F1}}}"), 60_000);
    }

    private static string CurrentTargetPos(BotDriveClient bridge, uint objId, string fallback)
    {
        var pos = ReadNpcPos(bridge, objId);
        return string.IsNullOrEmpty(pos) ? fallback : pos;
    }

    private static List<string> ReadLogLines(long gameLogOffset, long gameRestartLogOffset, uint charId, string needle)
    {
        var lines = new List<string>();
        foreach (var (path, offset) in new[] { (GameLogPath, gameLogOffset), (GameRestartLogPath, gameRestartLogOffset) })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < offset)
                    continue;
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Contains(needle, StringComparison.Ordinal)
                        && (charId == 0 || line.Contains($"char={charId}", StringComparison.Ordinal)))
                        lines.Add(line);
                }
            }
            catch
            {
            }
        }
        return lines;
    }

    /// <summary>
    /// Stale-lane guard: before the gate adopts a warm lane, ANY process
    /// LISTENING on the shared WebApi port whose cwd is NOT this lane's
    /// E2E_ROOT runtime dir is a foreign leftover (a sibling lane or an
    /// orphaned prior run) and would answer the adopt probes for the wrong
    /// stack. Such a listener is SIGTERM'd, then SIGKILL'd after 10 s if it
    /// is still alive. Only listeners are considered, so the current lane's
    /// own pids (which appear as CLIENTS on 127.0.0.1:PORT) are never
    /// touched; the log line is emitted for every kill and returned as
    /// evidence.
    /// </summary>
    private static string KillForeignWebApiListeners()
    {
        var laneRoot = Path.GetFullPath(Path.Combine(E2eStack.E2eRoot, "runtime"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var summary = new List<string>();
        foreach (var pid in ListenersOnPort(E2eStack.WebApiPort))
        {
            try
            {
                var cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget;
                if (cwd == null)
                    continue;
                var full = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);
                var inThisLane = full.Equals(laneRoot, StringComparison.Ordinal)
                    || full.StartsWith(laneRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inThisLane)
                    continue;
                var cmdline = "";
                try { cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
                var line = $"stale-proc-guard: pid={pid} listening :{E2eStack.WebApiPort} cwd={full} (not this lane {laneRoot}) cmd=[{cmdline}] — SIGTERM";
                Console.WriteLine("[q251] " + line);
                summary.Add(line);
                using (var term = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}")
                       { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
                {
                    term!.WaitForExit(5_000);
                }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var killed = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (!Directory.Exists($"/proc/{pid}"))
                    {
                        killed = true;
                        break;
                    }
                    Thread.Sleep(500);
                }
                if (!killed)
                {
                    var kline = $"stale-proc-guard: pid={pid} alive 10s after SIGTERM — SIGKILL";
                    Console.WriteLine("[q251] " + kline);
                    summary.Add(kline);
                    using var hard = Process.Start(new ProcessStartInfo("kill", $"-KILL {pid}")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    hard!.WaitForExit(5_000);
                }
            }
            catch (Exception ex)
            {
                summary.Add($"stale-proc-guard: pid={pid} check/kill failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
        return summary.Count == 0 ? "none" : string.Join(" | ", summary);
    }

    /// <summary>
    /// PIDs holding a LISTEN socket on <paramref name="port"/> (IPv4/IPv6),
    /// read from /proc/net/tcp{,6} + inode → /proc/*/fd. Never matches a mere
    /// outbound client connection, so the current lane's own pids cannot be
    /// returned by accident.
    /// </summary>
    private static List<int> ListenersOnPort(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                var lines = File.ReadAllLines(table);
                for (var i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                        continue;
                    if (!parts[3].Equals("0A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var local = parts[1];
                    var colon = local.LastIndexOf(':');
                    if (colon < 0)
                        continue;
                    if (!int.TryParse(local[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) || p != port)
                        continue;
                    inodes.Add(parts[9]);
                }
            }
            catch
            {
            }
        }
        var pids = new List<int>();
        if (inodes.Count == 0)
            return pids;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
                continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var link = new FileInfo(fd).LinkTarget;
                    if (link == null || !link.StartsWith("socket:[", StringComparison.Ordinal))
                        continue;
                    var inode = link["socket:[".Length..].TrimEnd(']');
                    if (inodes.Contains(inode))
                    {
                        pids.Add(pid);
                        break;
                    }
                }
            }
            catch
            {
            }
        }
        return pids;
    }

    private static async Task<bool> ProbeLaneAsync(TimeSpan timeout)
    {
        // Adopt-only: the lane bridge is the lane proof (E2E_BRIDGE_PORT).
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                if (bridge.Call("{\"cmd\":\"ping\"}", 3000).GetProperty("pong").GetBoolean())
                    return true;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
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
        throw new InvalidOperationException($"lane {envFile} has no DB_PASSWORD");
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static async Task<HttpClient> EnsureWebApiClientAsync(StringBuilder evidence)
    {
        // Shared-port route selection (G7c pattern): first client whose backend answers enabled.
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var client = NewClient();
            try
            {
                using var response = await client.GetAsync("/api/bots");
                var text = await response.Content.ReadAsStringAsync();
                if (text.Contains("Bot control API is disabled", StringComparison.Ordinal))
                {
                    evidence.AppendLine($"- webapi route attempt {attempt}: disabled backend (re-hashing)");
                    client.Dispose();
                    await Task.Delay(1000);
                    continue;
                }
                evidence.AppendLine($"- webapi route attempt {attempt}: enabled backend");
                return client;
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- webapi route attempt {attempt}: {ex.GetType().Name} (retrying)");
                client.Dispose();
                await Task.Delay(1000);
            }
        }
        throw new InvalidOperationException("no enabled WebApi backend reachable on the shared 1280 port after 12 hashed attempts");
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient http, string route, string body)
    {
        using var content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content);
        var text = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
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
                var state = doc.RootElement.GetProperty("state").GetString() ?? "";
                if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return doc.RootElement.Clone();
            }
            await Task.Delay(300);
        }
        throw new TimeoutException($"action {traceId} not terminal within {timeout.TotalSeconds:0}s");
    }
}
