using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// NEEDS-FARM-EARN-01 live-stack proof: a networked bot with ZERO seeded
/// copper, NO seed, and a sellable harvest surplus staged next to a live
/// seed merchant is driven through the LIVE scheduler wake path
/// (needs.farm activity as the driver) with NO external per-action commands
/// after setup: the earn leg sells the surplus through the real Sell path
/// (Money rises), the next re-evaluation buys seed 15659 with the earned
/// copper, then the existing loop takes over (travel/plant). This op only
/// enrolls, wakes, and observes.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class NeedsFarmEarnLoopE2eTests
{
    private static readonly string NetName = "NfEarn" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
    private static readonly string NetAccount = "e2efarmearn" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();

    private const uint PotatoSeedItemId = 15659;
    private const uint PotatoItemId = 7992;
    private const long SeedPrice = 25;

    /// <summary>
    /// Canonical seed merchants: merchants.merchant_pack_id = 171 sells
    /// 15659 (merchant_goods). npc_id is the NPC template id the bridge
    /// teleportToNpc/npcObjId ops resolve.
    /// </summary>
    private static readonly uint[] SeedMerchantCandidates =
    [
        // 8522 leads: the B6 merchant conservation test proves it spawns live.
        8522, 7564, 8624, 8625, 5098, 8508, 1061, 4341, 4346, 4348,
        5421, 8633, 8656, 8675, 8688, 8690, 1064, 8707, 1511, 8047
    ];
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "needs-farm-earn-loop-report.json");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task NeedsFarmEarnLoop_BrokeSeedless_EarnThenBuyThenPlant_OnLiveServer()
    {
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[needs-farm-earn-loop] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
        }

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        BotNetworkSession? net = null;
        Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", "1");
        try
        {
            E2eStack.EnsureUp();
            // The flag is read at game-process boot: restart so the live
            // server carries it (the BotControlApi pattern — shared stack
            // boots without it).
            E2eStack.RestartGameServer();
            E2eStack.EnsureUp();

            net = await BotNetworkSession.ConnectAsync(
                NetName, NetAccount, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!net.InWorld)
            {
                Leg("provision-networked", false, "bot never reached in-world");
                throw new Xunit.Sdk.XunitException("net bot must be in-world");
            }
            Leg("provision-networked", true, $"bot {NetName} in-world via real login flow");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // Setup ONLY (not per-action driving): no seed, labor stocked.
            // Money is NEVER granted — the earn must start from zero.
            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{NetName}\",\"seeds\":0,\"calves\":0,\"labor\":5000}}",
                timeoutMs: 60_000);
            if (rig.GetProperty("seeds").GetInt32() != 0)
            {
                Leg("rig-no-seed", false, "rig stocked seeds: " + rig);
                throw new Xunit.Sdk.XunitException("rig must stock no seed: " + rig);
            }
            Leg("rig-no-seed", true, $"seeds=0 labor={rig.GetProperty("labor").GetInt32()}");

            // Sellable harvest surplus through the networked path (the same
            // embodiment the loop wakes drive — no persistent-registry
            // split-brain): 3x canonical potato (live items.refund = 10
            // copper each at grade 0). Money is NEVER granted.
            var stock = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"stock\",\"item\":{PotatoItemId},\"count\":3}}",
                timeoutMs: 30_000);
            if (!stock.TryGetProperty("stocked", out var stocked) || !stocked.GetBoolean())
            {
                Leg("rig-surplus", false, "surplus stock refused: " + stock);
                throw new Xunit.Sdk.XunitException("surplus stock failed: " + stock);
            }
            Leg("rig-surplus", true, "3x potato stocked via networked path, no copper granted");

            // Zero seeded money is the whole point: the earn starts from
            // exactly 0 copper with no seed and a sellable surplus.
            var status0 = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoSeedItemId},{PotatoItemId}]}}",
                timeoutMs: 30_000);
            var money0 = status0.GetProperty("money").GetInt64();
            var seed0 = status0.GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoSeedItemId)
                .GetProperty("count").GetInt32();
            var potato0 = status0.GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoItemId)
                .GetProperty("count").GetInt32();
            if (money0 != 0 || seed0 != 0 || potato0 < 3)
            {
                Leg("rig-broke-seedless", false,
                    $"money={money0} (want 0) seed={seed0} (want 0) potato={potato0} (want >=3)");
                throw new Xunit.Sdk.XunitException("earn must start broke + seedless with surplus");
            }
            Leg("rig-broke-seedless", true,
                $"money=0 seed=0 potato={potato0} — broke, seedless, surplus held");

            // Merchant discovery (setup, not loop driving): the buy AND the
            // earn leg both need the in-range seed merchant — find a live
            // pack-171 seller with reachable farm soil, nearest soil wins
            // (shorter travel after the buy).
            uint merchantTemplate = 0;
            var merchantX = 0f;
            var merchantY = 0f;
            var merchantSoilDist = float.MaxValue;
            foreach (var candidate in SeedMerchantCandidates)
            {
                JsonElement landed;
                try
                {
                    landed = bridge.Call(
                        $"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"teleportToNpc\",\"npc\":{candidate}}}",
                        timeoutMs: 30_000);
                }
                catch
                {
                    continue;
                }
                if (landed.TryGetProperty("x", out var lx)) merchantX = lx.GetSingle();
                if (landed.TryGetProperty("y", out var ly)) merchantY = ly.GetSingle();
                // The spawner existing is not enough: the NPC must SPAWN live
                // (the B6 precedent polls up to 90s — spawn follows player
                // proximity on the spawner tick, never instantly).
                if (!PollMerchantSpawned(bridge, candidate, TimeSpan.FromSeconds(60)))
                    continue;
                var soil = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{NetName}\",\"radius\":150,\"step\":5}}",
                    timeoutMs: 60_000);
                if (!soil.GetProperty("found").GetBoolean())
                    continue;
                var dist = soil.GetProperty("dist").GetSingle();
                if (dist < merchantSoilDist)
                {
                    merchantSoilDist = dist;
                    merchantTemplate = candidate;
                }
            }
            if (merchantTemplate == 0)
            {
                Leg("merchant-near-soil", false, "no live pack-171 seed merchant with soil in 150m");
                throw new Xunit.Sdk.XunitException("no earn-capable merchant found live");
            }
            Leg("merchant-near-soil", true,
                $"npc {merchantTemplate} live, farm soil {merchantSoilDist:F1}m away");

            // Stage next to the merchant (shop range is 3m): return there and
            // stand 2m off so both the earn Sell and the seed Buy dispatch.
            var back = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"teleportToNpc\",\"npc\":{merchantTemplate}}}",
                timeoutMs: 30_000);
            var mx = back.GetProperty("x").GetSingle();
            var my = back.GetProperty("y").GetSingle();
            var mz = back.GetProperty("z").GetSingle();
            var placeProbe = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{NetName}\",\"x\":{mx + 2f},\"y\":{my},\"z\":{mz}}}",
                timeoutMs: 30_000);
            var stageGround = placeProbe.GetProperty("groundZ").GetSingle();
            var stageZ = stageGround != 0f ? stageGround : mz;
            var placed = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{NetName}\",\"x\":{mx + 2f},\"y\":{my},\"z\":{stageZ}}}",
                timeoutMs: 30_000);
            Leg("stage-next-to-merchant", true,
                $"staged at ({placed.GetProperty("x").GetSingle():F0},{placed.GetProperty("y").GetSingle():F0}) " +
                $"~2m from seed merchant {merchantTemplate}");
            // Diagnostic: is the seed merchant actually spawned live? (A
            // despawned/absent merchant reads exactly like a range miss —
            // Stop every wake with sells=0.)
            var npcResolve = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"npcObjId\",\"npc\":{merchantTemplate}}}",
                timeoutMs: 30_000);
            var merchantObjId = npcResolve.TryGetProperty("objId", out var oidEl) ? oidEl.GetUInt32() : 0u;
            Leg("merchant-spawned-live", merchantObjId != 0,
                merchantObjId != 0
                    ? $"seed merchant {merchantTemplate} live as objId {merchantObjId}"
                    : $"seed merchant {merchantTemplate} NOT spawned near the staged bot: " + npcResolve);
            // Leg 1 — earn: wake the live scheduler until Money rises with
            // seed still 0 (the Sell leg liquidating the surplus, never a
            // scripted grant — money moves only through the real engine path).
            var money = 0L;
            var seed = 0;
            var potato = potato0;
            var earnTrace = new List<string>();
            for (var i = 0; i < 10 && money <= 0; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                var s = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoSeedItemId},{PotatoItemId}]}}",
                    timeoutMs: 30_000);
                money = s.GetProperty("money").GetInt64();
                seed = s.GetProperty("items").EnumerateArray()
                    .First(e => e.GetProperty("template").GetUInt32() == PotatoSeedItemId)
                    .GetProperty("count").GetInt32();
                potato = s.GetProperty("items").EnumerateArray()
                    .First(e => e.GetProperty("template").GetUInt32() == PotatoItemId)
                    .GetProperty("count").GetInt32();
                earnTrace.Add($"w{i}:money={money} seed={seed} potato={potato} " +
                    $"phase={w.GetProperty("phase").GetString()} active={w.GetProperty("needsLegActive").GetBoolean()} " +
                    $"sells={w.GetProperty("sells").GetInt32()} sellState={w.GetProperty("lastSellState").GetString()} " +
                    $"sellDetail={w.GetProperty("lastSellDetail").GetString()} reason={w.GetProperty("reason").GetString()}");
            }
            if (money <= 0 || seed != 0)
            {
                Leg("earn-sell-real-path", false,
                    $"no earn in 10 wakes (money={money} seed={seed}):\n" + string.Join("\n", earnTrace));
                throw new Xunit.Sdk.XunitException("earn never landed via the loop");
            }
            Leg("earn-sell-real-path", true,
                $"money 0→{money} with seed still 0 (potato {potato0}→{potato}) — surplus sold, not granted");
            var bought = false;
            var buyTrace = new List<string>();
            for (var i = 0; i < 30 && !bought; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                var s = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoSeedItemId},{PotatoItemId}]}}",
                    timeoutMs: 30_000);
                seed = s.GetProperty("items").EnumerateArray()
                    .First(e => e.GetProperty("template").GetUInt32() == PotatoSeedItemId)
                    .GetProperty("count").GetInt32();
                var m = s.GetProperty("money").GetInt64();
                buyTrace.Add($"w{i}:money={m} seed={seed} phase={w.GetProperty("phase").GetString()}");
                if (seed > 0)
                {
                    bought = true;
                    if (m != money - SeedPrice)
                    {
                        Leg("buy-seed-earned-copper", false,
                            $"seed bought but money {money}→{m} (want exactly -{SeedPrice})");
                        throw new Xunit.Sdk.XunitException("buy did not charge exactly the seed price");
                    }
                    money = m;
                }
            }
            if (!bought)
            {
                Leg("buy-seed-earned-copper", false,
                    "no seed bought in 30 wakes after earn:\n" + string.Join("\n", buyTrace));
                throw new Xunit.Sdk.XunitException("buy never landed after earn");
            }
            Leg("buy-seed-earned-copper", true,
                $"seed 0→{seed} paid {SeedPrice}c from earned copper (money now {money})");

            // Leg 3 — the existing loop takes over: seed + needs.farm wakes
            // travel to soil and plant with no external commands (the travel
            // + plant path the NeedsFarmLoop test already proves — here it
            // runs on earned seed).
            var soilHere = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{NetName}\",\"radius\":5,\"step\":5}}",
                timeoutMs: 30_000);
            if (!soilHere.GetProperty("onSoil").GetBoolean())
            {
                var arrived = false;
                string lastPhase = "";
                var stallWakes = 0;
                var lastX = placed.GetProperty("x").GetSingle();
                for (var i = 0; i < 150 && !arrived; i++)
                {
                    var w = bridge.Call(
                        $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                        timeoutMs: 60_000);
                    lastPhase = w.GetProperty("phase").GetString() ?? "";
                    var wx = w.GetProperty("x").GetSingle();
                    if (MathF.Abs(wx - lastX) < 0.01f)
                    {
                        if (++stallWakes >= 30)
                        {
                            Leg("takeover-travel", false, $"stall at x={wx:F1} phase={lastPhase}");
                            throw new Xunit.Sdk.XunitException("takeover travel stalled");
                        }
                    }
                    else
                    {
                        stallWakes = 0;
                        lastX = wx;
                    }
                    var check = bridge.Call(
                        $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{NetName}\",\"radius\":5,\"step\":5}}",
                        timeoutMs: 30_000);
                    arrived = check.GetProperty("onSoil").GetBoolean();
                }
                if (!arrived)
                {
                    Leg("takeover-travel", false, $"never reached soil in 150 wakes (last {lastPhase})");
                    throw new Xunit.Sdk.XunitException("takeover travel never arrived");
                }
                Leg("takeover-travel", true, $"on soil after wakes (phase={lastPhase})");
            }
            else
            {
                Leg("takeover-travel", true, "staged merchant stands on soil — no travel needed");
            }

            var planted = false;
            var plantTrace = new List<string>();
            for (var i = 0; i < 30 && !planted; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                plantTrace.Add($"w{i}:phase={w.GetProperty("phase").GetString()} plants={w.GetProperty("plants").GetInt32()}");
                if (w.GetProperty("plants").GetInt32() > 0)
                    planted = true;
            }
            if (!planted)
            {
                Leg("takeover-plant", false,
                    "no Plant landed in 30 wakes on soil:\n" + string.Join("\n", plantTrace));
                throw new Xunit.Sdk.XunitException("existing loop did not take over");
            }
            var seedAfter = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoSeedItemId}]}}",
                timeoutMs: 30_000).GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoSeedItemId)
                .GetProperty("count").GetInt32();
            if (seedAfter != seed - 1)
            {
                Leg("takeover-plant", false, $"seed {seed}→{seedAfter} (want exactly -1)");
                throw new Xunit.Sdk.XunitException("plant did not consume exactly one earned seed");
            }
            Leg("takeover-plant", true,
                $"plant landed via loop on earned seed ({seed}→{seedAfter}) — existing loop owns the farm now");

            await WriteReportAsync(true, legs, "needs-farm earn-then-buy-then-plant live proof", wall.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            legs.Add(("exception", false, ex.GetType().Name + ": " + ex.Message, wall.Elapsed.TotalMilliseconds));
            await WriteReportAsync(false, legs, ex.ToString(), wall.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", null);
            net?.Dispose();
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            if (unhandled != 0 || fatals != 0)
                Console.WriteLine($"[needs-farm-earn-loop] WARN log tail: {unhandled} unhandled + {fatals} fatal(s)");
        }
    }

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "NeedsFarmEarnLoop",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["note"] = "broke (0 seeded copper) + seedless bot earns via real Sell, buys seed 15659 with earned copper, existing loop takes over (plant); H stays UNKNOWN",
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    /// <summary>Polls until the seed merchant template resolves to a live
    /// objId near the bot (the B6 PollNpcObjIdAsync precedent — spawn follows
    /// player proximity on the spawner tick, never instantly).</summary>
    private bool PollMerchantSpawned(BotDriveClient bridge, uint npcTemplate, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var response = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"npcObjId\",\"npc\":{npcTemplate}}}",
                timeoutMs: 30_000);
            if (response.TryGetProperty("objId", out var objEl) && objEl.GetUInt32() != 0)
                return true;
            Thread.Sleep(2000);
        }
        return false;
    }

    private static int CountLogTailMatches(long startOffset, string marker)
    {
        if (!File.Exists(GameLogPath))
            return 0;
        var count = 0;
        using var stream = new FileStream(GameLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}
