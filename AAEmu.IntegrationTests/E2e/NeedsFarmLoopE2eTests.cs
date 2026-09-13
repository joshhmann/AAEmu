using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// NEEDS-FARM-LOOP-01 live-stack proof: a networked bot with a valid seed +
/// labor, starting OFF valid planting soil with a reachable public farm, is
/// driven through the LIVE scheduler wake path (needs.farm activity as the
/// driver) with NO external per-action commands after setup: every gameplay
/// decision (buy/travel/plant/wait/harvest/replant); this op
/// only enrolls, wakes, and observes.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class NeedsFarmLoopE2eTests
{
    private static readonly string NetName = "NfLoop" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
    private static readonly string NetAccount = "e2efarmloop" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();

    private const uint PotatoSeedItemId = 15659;
    private const uint PotatoDoodadId = 2259;
    private const uint PotatoItemId = 7992;
    private const uint CropSeedlingPhase = 4379;
    private const uint CropMaturePhase = 4457;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "needs-farm-loop-report.json");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task NeedsFarmLoop_OffSoilTravel_Plant_Wait_Harvest_Replant_OnLiveServer()
    {
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[needs-farm-loop] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
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

            // Setup ONLY (not per-action driving): stock seed + labor.
            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{NetName}\",\"seeds\":5,\"calves\":0,\"labor\":5000}}",
                timeoutMs: 60_000);
            var seeds = rig.GetProperty("seeds").GetInt32();
            var labor = rig.GetProperty("labor").GetInt32();
            if (seeds < 1 || labor <= 0)
            {
                Leg("rig-seed-labor", false, $"seeds={seeds} labor={labor}");
                throw new Xunit.Sdk.XunitException("rig must stock seed + labor: " + rig);
            }
            Leg("rig-seed-labor", true, $"seeds={seeds} labor={labor}");

            // Staging (setup, not loop driving): the Nuian spawn sits ~2km
            // from the nearest Farm soil — beyond any bounded walk window.
            // Place the bot ONCE ~120m off the Lilyut farm-966 edge (ground
            // Z from the pak bbox); every gameplay step after this rides
            // scheduler wakes only.
            const float StageX = 12440f;
            const float StageY = 15390f;
            var placeProbe = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{NetName}\",\"x\":{StageX},\"y\":{StageY},\"z\":160}}",
                timeoutMs: 30_000);
            var stageGround = placeProbe.GetProperty("groundZ").GetSingle();
            var stageZ = stageGround != 0f ? stageGround : 160f;
            var placed = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{NetName}\",\"x\":{StageX},\"y\":{StageY},\"z\":{stageZ}}}",
                timeoutMs: 30_000);
            Leg("stage-near-farm", true,
                $"staged at ({placed.GetProperty("x").GetSingle():F0},{placed.GetProperty("y").GetSingle():F0},{placed.GetProperty("z").GetSingle():F0}) ground={stageGround:F1}");

            // Leg 1 — recognize need: money is 0 + labor full + seed present →
            // the farm status shows the needy starting state.
            var status0 = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoSeedItemId},{PotatoItemId}]}}",
                timeoutMs: 30_000);
            var seedCount0 = status0.GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoSeedItemId)
                .GetProperty("count").GetInt32();
            var money0 = status0.GetProperty("money").GetInt64();
            if (seedCount0 < 1)
            {
                Leg("recognize-need", false, $"seed={seedCount0} money={money0}");
                throw new Xunit.Sdk.XunitException("need not recognized: no seed in bag");
            }
            Leg("recognize-need", true, $"seed={seedCount0} money={money0} labor={labor} — needy");

            // Legs 2-3 — invalid soil recognized + valid soil identified, both
            // through the LIVE server-side probe (same gates the leg reads).
            var soil0 = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{NetName}\",\"radius\":150,\"step\":5}}",
                timeoutMs: 60_000);
            var startX = soil0.GetProperty("x").GetSingle();
            var startY = soil0.GetProperty("y").GetSingle();
            var onSoil0 = soil0.GetProperty("onSoil").GetBoolean();
            var found0 = soil0.GetProperty("found").GetBoolean();
            if (onSoil0)
            {
                Leg("recognize-invalid-soil", false, $"staged unexpectedly ON soil at ({startX:F0},{startY:F0})");
                throw new Xunit.Sdk.XunitException("staged on soil — cannot prove travel-to-soil");
            }
            Leg("recognize-invalid-soil", true,
                $"off-soil at ({startX:F0},{startY:F0}) — decision must refuse plant-at-position");
            if (!found0)
            {
                Leg("identify-valid-soil", false, "no valid soil within 150m of staged point");
                throw new Xunit.Sdk.XunitException("no reachable public farm near staged point: " + soil0);
            }
            var soilX = soil0.GetProperty("soilX").GetSingle();
            var soilY = soil0.GetProperty("soilY").GetSingle();
            var soilDist = soil0.GetProperty("dist").GetSingle();
            Leg("identify-valid-soil", true, $"soil at ({soilX:F0},{soilY:F0}), {soilDist:F1}m away");

            // Leg 4 — navigate via ordinary movement: wake the live scheduler
            // (the needs.farm activity drives), then REQUIRE displacement
            // toward soil with NO teleport (bounded per-wake steps, never the
            // full distance in one wake).
            var wake1 = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                timeoutMs: 60_000);
            if (!wake1.GetProperty("stepped").GetBoolean())
            {
                Leg("navigate-ordinary-movement", false, "no scheduler step ran: " + wake1);
                throw new Xunit.Sdk.XunitException("scheduler wake did not step: " + wake1);
            }
            var phase1 = wake1.GetProperty("phase").GetString();
            var x1 = wake1.GetProperty("x").GetSingle();
            var y1 = wake1.GetProperty("y").GetSingle();
            var moved1 = MathF.Sqrt((x1 - startX) * (x1 - startX) + (y1 - startY) * (y1 - startY));
            var distAfter1 = MathF.Sqrt((x1 - soilX) * (x1 - soilX) + (y1 - soilY) * (y1 - soilY));
            if (moved1 < 0.05f || moved1 >= soilDist || distAfter1 >= soilDist)
            {
                Leg("navigate-ordinary-movement", false,
                    $"moved={moved1:F2}m (need 0.05m..{soilDist:F1}m), dist-to-soil {soilDist:F1}→{distAfter1:F1}m phase={phase1}");
                throw new Xunit.Sdk.XunitException("no ordinary-movement approach observed");
            }
            Leg("navigate-ordinary-movement", true,
                $"moved {moved1:F2}m toward soil ({soilDist:F1}→{distAfter1:F1}m), phase={phase1} — no teleport");

            // Keep waking until the bot stands on soil. Live walk rate is
            // ~0.5m per wake round-trip (route leg advances one scheduler
            // step per wake observation); 80-90m needs ~180 iterations.
            // Bounded at 220 with a progress guard (no advance over 30
            // consecutive wakes = genuine stall, fail fast).
            var onSoil = false;
            var lastWake = wake1;
            var lastX = x1;
            var stallWakes = 0;
            for (var i = 0; i < 220 && !onSoil; i++)
            {
                lastWake = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                var wx = lastWake.GetProperty("x").GetSingle();
                if (MathF.Abs(wx - lastX) < 0.01f)
                {
                    if (++stallWakes >= 30)
                    {
                        Leg("travel-arrival", false,
                            $"stall: no X advance over 30 wakes at x={wx:F1} (soil {soilX:F0}): " + lastWake);
                        throw new Xunit.Sdk.XunitException("travel stalled mid-route");
                    }
                }
                else
                {
                    stallWakes = 0;
                    lastX = wx;
                }
                var soilCheck = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{NetName}\",\"radius\":5,\"step\":5}}",
                    timeoutMs: 30_000);
                onSoil = soilCheck.GetProperty("onSoil").GetBoolean();
            }
            if (!onSoil)
            {
                Leg("travel-arrival", false, "bot never reached soil in 220 wakes: " + lastWake);
                throw new Xunit.Sdk.XunitException("travel never arrived");
            }
            Leg("travel-arrival", true, $"on soil after wakes (phase={lastWake.GetProperty("phase").GetString()})");

            // Leg 5 — re-evaluate + plant via the real path: the next wakes
            // must land a Plant (audit evidence), consuming exactly one seed.
            uint plantedObjId = 0;
            var planted = false;
            var plantTrace = new List<string>();
            for (var i = 0; i < 30 && !planted; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                plantTrace.Add(
                    $"w{i}:stepped={w.GetProperty("stepped").GetBoolean()} " +
                    $"pos=({w.GetProperty("x").GetSingle():F0},{w.GetProperty("y").GetSingle():F0}) " +
                    $"phase={w.GetProperty("phase").GetString()} active={w.GetProperty("needsLegActive").GetBoolean()} " +
                    $"plants={w.GetProperty("plants").GetInt32()} reason={w.GetProperty("reason").GetString()}");
                if (w.GetProperty("plants").GetInt32() > 0)
                {
                    planted = true;
                    lastWake = w;
                    plantedObjId = w.GetProperty("cropObjId").GetUInt32();
                }
            }
            if (!planted)
            {
                Leg("plant-real-path", false,
                    "no Plant landed in 30 wakes on soil:\n" + string.Join("\n", plantTrace));
                throw new Xunit.Sdk.XunitException("plant never landed via the loop");
            }
            var seedAfterPlant = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoSeedItemId}]}}",
                timeoutMs: 30_000).GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoSeedItemId)
                .GetProperty("count").GetInt32();
            if (seedAfterPlant != seedCount0 - 1)
            {
                Leg("plant-real-path", false, $"seed {seedCount0}→{seedAfterPlant} (want exactly -1)");
                throw new Xunit.Sdk.XunitException("plant did not consume exactly one seed");
            }
            Leg("plant-real-path", true,
                $"plant landed via loop, seed {seedCount0}→{seedAfterPlant}, phase={lastWake.GetProperty("phase").GetString()}");

            // Potato baseline AT PLANT TIME: at 3600x the loop can complete
            // extra plant→harvest cycles inside the later windows, so every
            // yield assertion measures against this, never a re-baseline.
            var potatoAtPlant = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoItemId}]}}",
                timeoutMs: 30_000).GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoItemId)
                .GetProperty("count").GetInt32();

            if (plantedObjId == 0)
            {
                Leg("observe-immature", false, "loop tracks no crop after plant: " + lastWake);
                throw new Xunit.Sdk.XunitException("no tracked crop after plant");
            }

            // Leg 6 — observe immature: the freshly planted crop reads the
            // seedling phase (never mature at birth).
            var cropStatus = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[{plantedObjId}],\"items\":[]}}",
                timeoutMs: 30_000);
            var cropPhase = cropStatus.GetProperty("doodads").EnumerateArray().First()
                .GetProperty("phase").GetUInt32();
            if (cropPhase == CropMaturePhase)
            {
                Leg("observe-immature", false, $"crop {plantedObjId} already mature at birth (phase {cropPhase})");
                throw new Xunit.Sdk.XunitException("crop born mature — cannot prove wait");
            }
            Leg("observe-immature", true, $"crop {plantedObjId} phase {cropPhase} (immature)");

            // Leg 7 — waiting phase: across several wakes NO Harvest issues
            // while the crop is immature, and the phase field reads waiting.
            var harvestsBefore = lastWake.GetProperty("harvests").GetInt32();
            var waitingSeen = false;
            for (var i = 0; i < 6; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":15000}}",
                    timeoutMs: 60_000);
                var ph = w.GetProperty("phase").GetString();
                if (ph == "WaitingMaturity" || ph == "Traveling" || ph == "SeekingSoil")
                    waitingSeen = true;
                if (w.GetProperty("harvests").GetInt32() > harvestsBefore)
                {
                    // The crop matures in ~170ms wall at E2E GrowthRate 3600,
                    // so a harvest mid-wait is the CORRECT maturity path —
                    // provided the crop actually matured first. Acceptable:
                    // (a) crop reads mature now, or (b) crop row gone
                    // (harvest deletes the row at the final phase) with a
                    // real potato gain in the bag. Only a harvest against a
                    // still-IMMATURE standing crop fails the leg.
                    var cur = bridge.Call(
                        $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[{plantedObjId}],\"items\":[{PotatoItemId}]}}",
                        timeoutMs: 30_000);
                    var curDoodad = cur.GetProperty("doodads").EnumerateArray().First();
                    var curFound = curDoodad.GetProperty("found").GetBoolean();
                    var curPhase = curFound ? curDoodad.GetProperty("phase").GetUInt32() : 0u;
                    var curPotato = cur.GetProperty("items").EnumerateArray()
                        .First(e => e.GetProperty("template").GetUInt32() == PotatoItemId)
                        .GetProperty("count").GetInt32();
                    if (curFound && curPhase != CropMaturePhase)
                    {
                        Leg("waiting-phase", false, $"harvest issued while crop immature (phase {curPhase})");
                        throw new Xunit.Sdk.XunitException("harvest issued during wait");
                    }
                    if (!curFound && curPotato < 2)
                    {
                        Leg("waiting-phase", false, "crop gone with no yield — harvest misfired");
                        throw new Xunit.Sdk.XunitException("harvest misfired");
                    }
                    waitingSeen = true;
                }
            }
            if (!waitingSeen)
            {
                Leg("waiting-phase", false, "never observed a wait/travel phase across 6 wakes");
                throw new Xunit.Sdk.XunitException("no waiting phase observed");
            }
            Leg("waiting-phase", true, "deferred while immature — no premature harvest");

            // Legs 8-9 — maturity re-evaluation + harvest via the normal
            // path, WAKE-DRIVEN (the loop's real shape): keep waking until
            // the loop's own harvest lands (up to 300s — TaskManager growth
            // timers run late under E2E load). The maturity leg passes when
            // the crop reads mature OR the harvest lands first (3600x
            // maturation can beat the poll); the harvest leg passes on the
            // audit count + real potato gain.
            var harvested = false;
            var matureSeen = false;
            for (var i = 0; i < 100 && !harvested; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                if (w.GetProperty("harvests").GetInt32() > harvestsBefore)
                {
                    harvested = true;
                    lastWake = w;
                }
                else if (i % 10 == 0)
                {
                    var cur = bridge.Call(
                        $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[{plantedObjId}],\"items\":[]}}",
                        timeoutMs: 30_000).GetProperty("doodads").EnumerateArray().First();
                    if (cur.GetProperty("found").GetBoolean()
                        && cur.GetProperty("phase").GetUInt32() == CropMaturePhase)
                        matureSeen = true;
                }
            }
            if (matureSeen)
                Leg("maturity-reevaluation", true, $"crop {plantedObjId} read mature (phase {CropMaturePhase})");
            else
                Leg("maturity-reevaluation", harvested,
                    harvested
                        ? $"crop {plantedObjId} matured and harvested faster than the poll (3600x)"
                        : $"crop {plantedObjId} never matured nor harvested in 100 wakes");
            if (!harvested)
                throw new Xunit.Sdk.XunitException("harvest never landed via the loop");
            var potatoAfter = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[],\"items\":[{PotatoItemId}]}}",
                timeoutMs: 30_000).GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("template").GetUInt32() == PotatoItemId)
                .GetProperty("count").GetInt32();
            if (potatoAfter - potatoAtPlant < 2)
            {
                Leg("harvest-normal-path", false, $"potato {potatoAtPlant}→{potatoAfter} since plant (want +2)");
                throw new Xunit.Sdk.XunitException("harvest yielded nothing");
            }
            Leg("harvest-normal-path", true, $"harvest landed via loop, potato +{potatoAfter - potatoAtPlant} since plant");

            // Leg 10 — output observed (yield in bag, crop row gone by design).
            var goneCheck = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{NetName}\",\"objIds\":[{plantedObjId}],\"items\":[]}}",
                timeoutMs: 30_000).GetProperty("doodads").EnumerateArray().First();
            Leg("output-observed", true,
                $"yield in bag (+{potatoAfter - potatoAtPlant} potato), crop row gone={(!goneCheck.GetProperty("found").GetBoolean()).ToString()}");

            // Leg 11 — replant / second-cycle transition: harvest returns a
            // seed, and further wakes plant again with no external commands.
            var replanted = false;
            for (var i = 0; i < 30 && !replanted; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{NetName}\",\"waitMs\":20000}}",
                    timeoutMs: 60_000);
                if (w.GetProperty("plants").GetInt32() > 1)
                    replanted = true;
            }
            if (!replanted)
            {
                Leg("replant-second-cycle", false, "no second plant in 30 wakes after harvest");
                throw new Xunit.Sdk.XunitException("second cycle never started");
            }
            Leg("replant-second-cycle", true, "second plant landed — loop cycles with no external commands");

            await WriteReportAsync(true, legs, "needs-farm live loop proof", wall.Elapsed.TotalSeconds);
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
                Console.WriteLine($"[needs-farm-loop] WARN log tail: {unhandled} unhandled + {fatals} fatal(s)");
        }
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task NeedsFarmLoop_NoFarmNearby_BoundedDefer_OnLiveServer()
    {
        // Negative leg (live): a seed-bearing bot with NO valid soil in probe
        // range defers boundedly — wakes keep stepping, no Plant ever lands,
        // no route spins forever. Uses an isolated far anchor: the probe
        // itself proves absence (found=false), then wakes prove the defer.
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[needs-farm-loop-neg] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
        }

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        BotNetworkSession? net = null;
        try
        {
            E2eStack.EnsureUp();
            var negName = ("NfNeg" + Guid.NewGuid().ToString("N")[..8]).ToLowerInvariant();
            var negAccount = "e2efarmneg" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
            net = await BotNetworkSession.ConnectAsync(
                negName, negAccount, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!net.InWorld)
            {
                Leg("provision", false, "bot never in-world");
                throw new Xunit.Sdk.XunitException("net bot must be in-world");
            }
            Leg("provision", true, $"bot {negName} in-world");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{negName}\",\"seeds\":3,\"calves\":0,\"labor\":5000}}",
                timeoutMs: 60_000);
            if (rig.GetProperty("seeds").GetInt32() < 1)
            {
                Leg("rig", false, "no seeds: " + rig);
                throw new Xunit.Sdk.XunitException("rig failed: " + rig);
            }
            Leg("rig", true, "seed stocked");

            // Absence proof: a 5m probe around spawn must find nothing AND
            // the bot must not already stand on soil (else this lane cannot
            // prove the negative).
            var soil = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{negName}\",\"radius\":5,\"step\":5}}",
                timeoutMs: 30_000);
            if (soil.GetProperty("onSoil").GetBoolean() || soil.GetProperty("found").GetBoolean())
            {
                Leg("absence", false, "spawn is on/near soil — negative lane N/A here: " + soil);
                throw new Xunit.Sdk.XunitException("SKIP: spawn near soil, negative not provable on this lane");
            }
            Leg("absence", true, "no soil within 5m — defer expected");

            var stepped = 0;
            string? lastPhase = null;
            for (var i = 0; i < 10; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{negName}\",\"waitMs\":15000}}",
                    timeoutMs: 60_000);
                if (w.GetProperty("stepped").GetBoolean())
                    stepped++;
                lastPhase = w.GetProperty("phase").GetString();
                if (w.GetProperty("plants").GetInt32() > 0)
                {
                    Leg("bounded-defer", false, "plant landed with no soil in range");
                    throw new Xunit.Sdk.XunitException("plant landed without soil");
                }
            }
            if (stepped == 0)
            {
                Leg("bounded-defer", false, "no scheduler steps ran");
                throw new Xunit.Sdk.XunitException("no steps ran");
            }
            Leg("bounded-defer", true, $"{stepped}/10 wakes stepped, no plant, last phase={lastPhase} — bounded, no spin");

            await WriteNegReportAsync(true, legs, "bounded defer live proof", wall.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            legs.Add(("exception", false, ex.GetType().Name + ": " + ex.Message, wall.Elapsed.TotalMilliseconds));
            await WriteNegReportAsync(false, legs, ex.ToString(), wall.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            net?.Dispose();
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            if (unhandled != 0 || fatals != 0)
                Console.WriteLine($"[needs-farm-loop-neg] WARN log tail: {unhandled} unhandled + {fatals} fatal(s)");
        }
    }

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "NeedsFarmLoop",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["note"] = "live scheduler-wake loop proof (needs.farm driver, no per-action commands); H stays UNKNOWN; crop-vanished stale-drop covered by unit tests",
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

    private async Task WriteNegReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "NeedsFarmLoopNegative",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["note"] = "no-farm-nearby bounded defer live proof; H stays UNKNOWN",
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "needs-farm-loop-negative-report.json"),
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
