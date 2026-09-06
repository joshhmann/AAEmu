using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Q4 — M5-B1 loot grant proof, LIVE hunt-leg caller-delta artifact.
///
/// A real TCP bot (live enter-world through the real login flow) holds
/// quest 251 (the <c>loot_quest_id</c> gate on item 4058 boar meat), kills a
/// live Solzreed boar (npc 3475) with real cast damage through the M5
/// contract, and loots the corpse through the M5 contract. The CALLER's bag
/// + money deltas are the grant proof — never the container count
/// (<c>GameplayActor.Loot</c> returns container ENTRIES, not units).
///
/// Three outcomes told apart per the Q4 contract: caller grant (deltas match
/// the generated pack) vs no-op vs foreign take. This leg covers grant +
/// retry-after-success (empty observation, nothing granted twice); the
/// foreign-take shape is rig-proven (<c>GameplayActorLootGrantTests</c>).
///
/// No quest-free substitution: the killer holds quest 251 live (accepted at
/// NPC 2425 through the real accept gate), so pack 4530 generates the meat
/// through the real <c>GenerateLoot</c> chain. H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class Q4LiveHuntLootE2eTests
{
    private const string BotName = "Q4LootBot";
    private const string BotAccount = "q4lootbot";
    private const string Password = "e2e-secret";

    private const uint Quest251 = 251;
    private const uint AcceptorNpc2425 = 2425;
    private const uint BoarTemplate3475 = 3475;
    private const uint BoarPack4530 = 4530;
    private const uint BoarMeat4058 = 4058;
    private const uint Skill18131 = 18131;
    private const uint Skill18134 = 18134;
    private const int HuntLevel = 10;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");
    private static string ReportPath => Path.Combine(EvidenceDir, "q4-live-hunt-loot-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task LiveHuntLoot_CallerDeltasMatchGeneratedPack()
    {
        var totalWall = Stopwatch.StartNew();
        E2eStack.EnsureUp();
        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# Q4 live hunt-leg caller-delta proof — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0}s): {detail}");
        }

        var bridge = new BotDriveClient(E2eStack.BridgePort);
        BotNetworkSession? session = null;

        // Cross-check accumulators (test-side observations vs op-reported deltas).
        var hunt = new { casts = 0, targetObjId = 0u, hpFirst = 0, hpLast = 0 };
        var grant = new { state = "", granted = 0, containerBefore = 0, remaining = 0, meatDelta = 0, moneyDelta = 0L, bagDelta = "" };
        var retry = new { state = "", granted = 0, meatDelta = 0, moneyDelta = 0L };

        try
        {
            // ---- leg 1: live enter-world ----
            var sw = Stopwatch.StartNew();
            E2eStack.CleanupBotRows(BotAccount);
            session = await ConnectBotAsync();
            var botObjId = CharState(bridge).GetProperty("objId").GetUInt32();
            var entered = session.InWorld && botObjId != 0;
            Leg("enter-world", entered, $"inWorld={session.InWorld} objId={botObjId}", sw.Elapsed.TotalSeconds);
            Assert.True(entered, "bot must enter the live world");

            // ---- leg 2: hold quest 251 (the 4058 loot gate) ----
            sw.Restart();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{HuntLevel}}}", 30_000);
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{AcceptorNpc2425}}}", 60_000);
            var acceptorObjId = PollNpcObjId(bridge, AcceptorNpc2425);
            var accept = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"accept\",\"quest\":{Quest251},\"acceptor\":\"Npc\",\"acceptorId\":{AcceptorNpc2425}}}",
                60_000);
            var accepted = accept.TryGetProperty("accepted", out var accEl) && accEl.GetBoolean();
            var held = accepted && E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("quest-hold", held,
                $"accept251={accepted} acceptorObjId={acceptorObjId} active={E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251)}",
                sw.Elapsed.TotalSeconds);
            Assert.True(held, "quest 251 must be held live (no quest-free substitution):\n" + evidence);

            // ---- leg 3: hunt setup — teleport to the boar ground, pin a live boar ----
            sw.Restart();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{BoarTemplate3475}}}", 60_000);
            var targetObjId = PollNpcObjId(bridge, BoarTemplate3475);
            var meatBefore = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var moneyBefore = MailMoney(bridge);
            var setupOk = targetObjId != 0;
            Leg("hunt-setup", setupOk,
                $"boarObjId={targetObjId} meatBefore={meatBefore} moneyBefore={moneyBefore}", sw.Elapsed.TotalSeconds);
            Assert.True(setupOk, "a live boar must materialize:\n" + evidence);

            // ---- leg 4: real kill — cast rotation until the pinned boar drops ----
            sw.Restart();
            var (killed, killDetail, casts, hpFirst, hpLast) = await HuntKillAsync(bridge, targetObjId);
            hunt = new { casts, targetObjId, hpFirst, hpLast };
            Leg("hunt-kill", killed, killDetail, sw.Elapsed.TotalSeconds);
            Assert.True(killed, "the pinned boar must die to real cast damage (no synthetic credit):\n" + evidence);

            // ---- leg 5: grant — loot the corpse, caller deltas are the proof ----
            sw.Restart();
            var meatMid = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var moneyMid = MailMoney(bridge);
            var loot = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"loot\",\"npcObjId\":{targetObjId}}}", 60_000);
            var lootState = loot.GetProperty("state").GetString() ?? "";
            var granted = loot.GetProperty("granted").GetInt32();
            var containerBefore = loot.GetProperty("containerBefore").GetInt32();
            var remaining = loot.GetProperty("remaining").GetInt32();
            var moneyDeltaOp = loot.GetProperty("moneyDelta").GetInt64();
            var bagDeltaEl = loot.GetProperty("bagDelta");
            var meatDeltaOp = 0;
            foreach (var entry in bagDeltaEl.EnumerateArray())
                if (entry.GetProperty("template").GetUInt32() == BoarMeat4058)
                    meatDeltaOp = entry.GetProperty("delta").GetInt32();
            var meatAfter = E2eQuestDriver.InvCount(bridge, BotName, BoarMeat4058);
            var moneyAfter = MailMoney(bridge);
            grant = new
            {
                state = lootState,
                granted,
                containerBefore,
                remaining,
                meatDelta = meatDeltaOp,
                moneyDelta = moneyDeltaOp,
                bagDelta = bagDeltaEl.ToString()
            };
            // Conservation: granted entries == container before-after; every
            // bag delta is a gain; op deltas cross-check against test-side reads.
            var grantOk = lootState == "Completed"
                && containerBefore >= 1
                && granted == containerBefore - remaining
                && remaining == 0
                && meatDeltaOp >= 1
                && meatAfter - meatMid == meatDeltaOp
                && moneyAfter - moneyMid == moneyDeltaOp
                && moneyDeltaOp >= 0
                && AllGains(bagDeltaEl);
            Leg("grant", grantOk,
                $"state={lootState} granted={granted} container {containerBefore}->0 " +
                $"meatDelta={meatDeltaOp} (x-check {meatMid}->{meatAfter}) " +
                $"moneyDelta={moneyDeltaOp} (x-check {moneyMid}->{moneyAfter}) bagDelta={bagDeltaEl}",
                sw.Elapsed.TotalSeconds);
            Assert.True(grantOk, "caller deltas must match the generated pack:\n" + evidence);

            // ---- leg 6: retry after success — empty observation, nothing twice ----
            sw.Restart();
            var retryEl = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"loot\",\"npcObjId\":{targetObjId}}}", 60_000);
            var retryState = retryEl.GetProperty("state").GetString() ?? "";
            var retryGranted = retryEl.GetProperty("granted").GetInt32();
            var retryBagEmpty = retryEl.GetProperty("bagDelta").GetArrayLength() == 0;
            var retryMoneyZero = retryEl.GetProperty("moneyDelta").GetInt64() == 0;
            retry = new
            {
                state = retryState,
                granted = retryGranted,
                meatDelta = 0,
                moneyDelta = retryEl.GetProperty("moneyDelta").GetInt64()
            };
            var retryOk = (retryState == "Rejected" || (retryState == "Completed" && retryGranted == 0))
                && retryBagEmpty && retryMoneyZero;
            Leg("retry-empty", retryOk,
                $"state={retryState} granted={retryGranted} bagEmpty={retryBagEmpty} moneyZero={retryMoneyZero} " +
                $"detail={retryEl.GetProperty("detail").GetString()}",
                sw.Elapsed.TotalSeconds);
            Assert.True(retryOk, "retry after success must grant nothing:\n" + evidence);
        }
        finally
        {
            session?.Dispose();
            bridge.Dispose();
        }

        var passed = legs.All(l => l.Passed);
        await WriteReportAsync(passed, legs, evidence.ToString(), hunt, grant, retry,
            totalWall.Elapsed.TotalSeconds, logOffset);

        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the hunt leg");
        Assert.True(passed, "Q4 live hunt-leg FAIL:\n" + evidence + $"\nReport: {ReportPath}");
    }

    // Hunts the pinned boar with the real Fight rotation until its HP reads
    // zero. Damage may land a tick after UseSkill returns (effect delay), so
    // every cast's hpBefore is also a death observation — the loop exits on
    // either edge. Bounded: no kill inside the budget fails honestly.
    private static async Task<(bool Killed, string Detail, int Casts, int HpFirst, int HpLast)> HuntKillAsync(
        BotDriveClient bridge, uint targetObjId)
    {
        var skill = Skill18131;
        var casts = 0;
        var hpFirst = -1;
        var hpLast = -1;
        var hpMin = int.MaxValue;
        var consecutiveRejects = 0;
        var reanchors = 0;
        const int maxCasts = 60;
        while (casts < maxCasts)
        {
            JsonElement cast;
            try
            {
                cast = bridge.Call(
                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"cast\",\"skill\":{skill},\"npcObjId\":{targetObjId}}}",
                    60_000);
            }
            catch (Exception ex)
            {
                await Task.Delay(1200);
                if (++consecutiveRejects > 8)
                    return (false, $"cast transport failures x{consecutiveRejects}: {ex.GetType().Name} {ex.Message}", casts, hpFirst, hpLast);
                continue;
            }
            if (!cast.TryGetProperty("state", out _))
                return (false, $"cast op error (no state): {cast}", casts, hpFirst, hpLast);
            var state = cast.GetProperty("state").GetString() ?? "";
            var hpBefore = cast.TryGetProperty("hpBefore", out var hb) ? hb.GetInt32() : -1;
            var hpAfter = cast.TryGetProperty("hpAfter", out var ha) ? ha.GetInt32() : -1;
            var alive = !cast.TryGetProperty("targetAlive", out var al) || al.GetBoolean();
            if (hpFirst < 0 && hpBefore >= 0) hpFirst = hpBefore;
            if (hpBefore >= 0 && hpBefore < hpMin) hpMin = hpBefore;
            if (hpAfter >= 0) { hpLast = hpAfter; if (hpAfter < hpMin) hpMin = hpAfter; }
            // Death observed on either edge (post-effect damage lands late).
            if (hpBefore == 0 || hpAfter == 0 || !alive)
                return (true, $"boar {targetObjId} down after {casts + 1} cast(s) (hp {hpFirst}->{hpMin}->0, skill {skill})", casts + 1, hpFirst, 0);
            casts++;
            if (state == "Rejected")
            {
                var detail = cast.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                if (detail.Contains("not learned", StringComparison.OrdinalIgnoreCase) && skill == Skill18131)
                {
                    skill = Skill18134; // rotation fallback, same as the spike
                    consecutiveRejects = 0;
                    continue;
                }
                if (++consecutiveRejects > 8 && reanchors < 3)
                {
                    // Roaming prey out of the 4 m melee band (spike run-4
                    // signature): re-anchor at the spawner, keep the SAME
                    // pinned target — never re-pick credit.
                    reanchors++;
                    consecutiveRejects = 0;
                    bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{BoarTemplate3475}}}", 60_000);
                    await Task.Delay(2000);
                    continue;
                }
                if (consecutiveRejects > 16)
                    return (false, $"cast refused x{consecutiveRejects} (last: {detail}) — kill unobserved", casts, hpFirst, hpLast);
            }
            else
            {
                consecutiveRejects = 0;
            }
            await Task.Delay(1200);
        }
        return (false, $"kill budget exhausted ({maxCasts} casts, hp {hpFirst}->{hpLast}) — kill unobserved", casts, hpFirst, hpLast);
    }

    private static bool AllGains(JsonElement bagDelta)
    {
        foreach (var entry in bagDelta.EnumerateArray())
            if (entry.GetProperty("delta").GetInt32() <= 0)
                return false;
        return true;
    }

    private static async Task<BotNetworkSession> ConnectBotAsync()
    {
        var session = await BotNetworkSession.ConnectAsync(
            BotName, BotAccount, Password,
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        if (!session.InWorld)
        {
            session.Dispose();
            throw new InvalidOperationException($"{BotName}: real login flow did not reach in-world");
        }
        return session;
    }

    private static JsonElement CharState(BotDriveClient bridge)
        => bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);

    private static long MailMoney(BotDriveClient bridge)
        => bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{BotName}\",\"op\":\"char\"}}", 30_000)
            .GetProperty("money").GetInt64();

    // Spawn-radius world: poll until the NPC actually materializes through
    // the normal spawn path (a fixed sleep races the spawn tick).
    private static uint PollNpcObjId(BotDriveClient bridge, uint templateId, int seconds = 30)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < deadline)
        {
            var objId = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{templateId}}}",
                30_000).GetProperty("objId").GetUInt32();
            if (objId != 0)
                return objId;
            Thread.Sleep(1000);
        }
        return 0;
    }

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, object hunt, object grant, object retry, double wallSeconds, long logOffset)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = "q4-live-hunt-loot",
            milestone = "Q4 M5-B1 loot grant proof (live hunt-leg caller-delta artifact)",
            verdict = passed ? "PASS" : "FAIL",
            quest = Quest251,
            acceptorNpc = AcceptorNpc2425,
            preyTemplate = BoarTemplate3475,
            preyPack = BoarPack4530,
            meatItem = BoarMeat4058,
            substitution = "none — killer held quest 251 live (real accept at NPC 2425); pack 4530 default generation through the real GenerateLoot chain",
            legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
            hunt,
            grant,
            retry,
            wallSeconds = Math.Round(wallSeconds, 1),
            provenance = new
            {
                sourceRevision = E2eStack.SourceRevision,
                runtimeSqliteMd5 = E2eStack.RuntimeSqliteMd5(),
                canonicalSqliteMd5 = E2eStack.CanonicalSqliteMd5,
                lane = new
                {
                    root = E2eStack.E2eRoot,
                    project = E2eStack.ComposeProject,
                    loginPort = E2eStack.LoginPort,
                    gamePort = E2eStack.GamePort,
                    streamPort = E2eStack.StreamPort,
                    bridgePort = E2eStack.BridgePort,
                    internalPort = E2eStack.InternalPort,
                    webApiPort = E2eStack.WebApiPort,
                    dbPort = E2eStack.DbPort
                }
            },
            note = "H UNKNOWN (no feel verdict). Pack 8055 pouch is RNG-gated (not asserted per non-goal: eligibility/range/conservation only). " +
                   "Full-bag partial-grant is rig-proven, not exercised live. Log-tail unhandled/FATAL scan covers only this run's appended bytes.",
            evidence
        };
        await File.WriteAllTextAsync(ReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int CountLogTailMatches(long startOffset, string marker)
    {
        try
        {
            if (!File.Exists(GameLogPath))
                return 0;
            using var fs = new FileStream(GameLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= startOffset)
                return 0;
            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var count = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    count++;
            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
