using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// AGRICULTURE-BUTCHER-01 live-stack proof: a REAL game server boots, ONE
/// networked bot enters through the real login flow, rigs a dairy calf
/// through the farm bridge, plants it through the REAL GameplayActor.Plant
/// path, waits out the E2E-compressed growth (GrowthRate 3600: 3.4 h +
/// 30.9 h canonical → ~35 s live), butchers the mature cow through the
/// farm interact op (real GameplayActor.Interact → Doodad.Use chain, the
/// implemented DoodadFuncButcher path), and harvests beef. Phase advance
/// to 5790 + beef 8048 in the bag is the proof.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class LivestockButcherE2eTests
{
    private static readonly string NetName = "ButcherNet" + Guid.NewGuid().ToString("N")[..8];
    private static readonly string NetAccount = "e2ebutchernet" + Guid.NewGuid().ToString("N")[..8];

    private const uint CalfItemTemplate = 16225;
    private const uint MatureCowInterimPhase = 12774;
    private const uint CowPhase = 5782;
    private const uint ButcheredPhase = 5790;
    private const uint ButcherFinalPhase = 9907;
    private const uint ButcherSkillId = 13972;
    private const uint BeefItemTemplate = 8048;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "agriculture-butcher-report.json");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Butcher_MatureCow_AdvancesPhaseAndGrantsBeef()
    {
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();
            var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

            using var net = await BotNetworkSession.ConnectAsync(
                NetName, NetAccount, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(net.InWorld, "net bot must be in-world");
            legs.Add(("provision", true, "networked bot in-world", wall.Elapsed.TotalMilliseconds));

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            bridge.Call("{\"cmd\":\"drive\",\"bot\":\"" + NetName + "\",\"op\":\"setLevel\",\"level\":10}", 30_000);

            // ------------------------------------------------ rig calf + labor
            var rig = bridge.Call("{\"cmd\":\"farm\",\"bot\":\"" + NetName + "\",\"op\":\"rig\"," +
                "\"seeds\":0,\"calves\":2,\"labor\":5000}", 30_000);
            Assert.True(rig.TryGetProperty("calves", out var calves) && calves.GetInt32() >= 1,
                "rig must stock a calf: " + rig);
            legs.Add(("rig-calf", true, "calf stocked", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ plant the calf
            var plant = bridge.Call("{\"cmd\":\"farm\",\"bot\":\"" + NetName + "\",\"op\":\"plant\"," +
                "\"seed\":" + CalfItemTemplate + ",\"dx\":2,\"dy\":0}", 60_000);
            Assert.True(plant.TryGetProperty("objId", out var objEl) && objEl.GetUInt32() != 0,
                "plant must return a crop objId: " + plant);
            var cropObjId = objEl.GetUInt32();
            legs.Add(("plant", true, $"calf planted objId {cropObjId}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ wait for growth
            // 12774 is the transient mature phase (ratio 853 → 5782 inside
            // one task tick) — accept either as grown.
            var grew = false;
            var growDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
            uint phase = 0;
            while (DateTime.UtcNow < growDeadline)
            {
                await Task.Delay(5000);
                var status = bridge.Call("{\"cmd\":\"farm\",\"bot\":\"" + NetName + "\",\"op\":\"status\"," +
                    "\"objIds\":[" + cropObjId + "],\"items\":[]}", 30_000);
                var doodads = status.GetProperty("doodads").EnumerateArray().ToList();
                if (doodads.Count == 0 || !doodads[0].TryGetProperty("phase", out var phEl))
                    continue;
                phase = phEl.GetUInt32();
                if (phase == CowPhase || phase == MatureCowInterimPhase)
                {
                    grew = true;
                    break;
                }
            }
            Assert.True(grew, $"calf must grow to cow phase {CowPhase} (via {MatureCowInterimPhase}) within 4 min, last phase {phase}");
            legs.Add(("grow", true, $"calf → cow phase {phase}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ butcher via interact
            var butcher = bridge.Call("{\"cmd\":\"farm\",\"bot\":\"" + NetName + "\",\"op\":\"interact\"," +
                "\"objId\":" + cropObjId + ",\"skill\":" + ButcherSkillId + "}", 60_000);
            Assert.True(butcher.TryGetProperty("state", out var bState) && bState.GetString() == "Completed",
                "butcher interact must complete: " + butcher);
            var phaseAfter = butcher.TryGetProperty("phase", out var paEl) ? paEl.GetUInt32() : 0;
            Assert.True(phaseAfter == ButcheredPhase || phaseAfter == ButcherFinalPhase,
                $"cow must advance to butchered phase {ButcheredPhase} (loot tail {ButcherFinalPhase}), got {phaseAfter}: {butcher}");
            legs.Add(("butcher", true, $"phase {phase} → {phaseAfter}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ beef in the bag
            var inv = bridge.Call("{\"cmd\":\"mail\",\"bot\":\"" + NetName + "\",\"op\":\"inv\"}", 30_000);
            var beef = inv.GetProperty("items").EnumerateArray()
                .FirstOrDefault(i => i.GetProperty("templateId").GetUInt32() == BeefItemTemplate);
            Assert.True(beef.ValueKind != JsonValueKind.Undefined,
                "beef must land in the bag: " + inv);
            var beefCount = beef.GetProperty("count").GetInt32();
            Assert.True(beefCount >= 1, $"beef count must be ≥ 1, got {beefCount}");
            legs.Add(("beef", true, $"beef 8048 x{beefCount} in bag", wall.Elapsed.TotalMilliseconds));

            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            Assert.True(unhandled == 0 && fatals == 0,
                $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the butcher run");

            await WriteReportAsync(true, legs, $"cow {CowPhase} → {phaseAfter} (butchered {ButcheredPhase}, loot tail {ButcherFinalPhase}), beef x{beefCount}", wall.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            legs.Add(("exception", false, ex.GetType().Name + ": " + ex.Message, wall.Elapsed.TotalMilliseconds));
            await WriteReportAsync(false, legs, ex.ToString(), wall.Elapsed.TotalSeconds);
            throw;
        }
    }

    private static async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "AgricultureButcher",
            ["revision"] = E2eStack.SourceRevision,
            ["passed"] = passed,
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
            ["wallSeconds"] = wallSeconds,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
