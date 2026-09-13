using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// COMBAT-HEAL-01 live-stack proof: a REAL game server boots, ONE networked
/// bot enters through the real login flow, takes an honest engine wound
/// (Unit.ReduceCurrentHp — packets + death logic, no DamageEffect so the
/// login-protection window does not apply), then drinks a recovery potion
/// through the REAL GameplayActor.UseItem contract path (Skill.Use with a
/// SkillItem caster — the exact CSStartSkillPacket SkillItem branch).
/// HP-before/after deltas are the proof.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class RecoveryHealingE2eTests
{
    private static readonly string NetName = "HealNet" + Guid.NewGuid().ToString("N")[..8];
    private static readonly string NetAccount = "e2ehealnet" + Guid.NewGuid().ToString("N")[..8];

    // L10 potion (skill 11715, fixed 990 heal — full heal_effect chain loads).
    private const uint HealPotionTemplate = 8515;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "recovery-heal-report.json");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task RecoveryPotion_OnLiveServer_RestoresWoundedHp()
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
            var stock = bridge.Call("{\"cmd\":\"mail\",\"bot\":\"" + NetName + "\",\"op\":\"stock\"," +
                "\"itemTemplate\":" + HealPotionTemplate + ",\"count\":1}", 30_000);
            Assert.True(stock.TryGetProperty("count", out var stocked) && stocked.GetInt32() == 1,
                "potion must be stocked: " + stock);
            var full = bridge.Call("{\"cmd\":\"drive\",\"bot\":\"" + NetName + "\",\"op\":\"charState\"}", 30_000);
            var maxHp = full.GetProperty("maxHp").GetInt32();
            var hpFull = full.GetProperty("hp").GetInt32();
            // Fresh bots spawn at partial HP — the baseline is whatever the
            // engine reports, not full. The wound→heal delta is the proof.
            Assert.True(maxHp > 0 && hpFull > 0 && hpFull <= maxHp, "bot must report sane HP: " + full);
            legs.Add(("rig-potion", true, $"potion stocked, HP {hpFull}/{maxHp}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ wound: half MaxHp
            var woundAmount = Math.Max(1, maxHp / 2);
            var wound = bridge.Call("{\"cmd\":\"mail\",\"bot\":\"" + NetName + "\",\"op\":\"wound\"," +
                "\"amount\":" + woundAmount + "}", 30_000);
            Assert.True(wound.TryGetProperty("wounded", out var w) && w.GetBoolean(),
                "wound must reduce HP: " + wound);
            var hpWounded = wound.GetProperty("hpAfter").GetInt32();
            Assert.True(hpWounded < hpFull, $"HP must drop: {hpFull} → {hpWounded}");
            legs.Add(("wound", true, $"HP {hpFull} → {hpWounded}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ heal: real UseItem path
            var use = bridge.Call("{\"cmd\":\"mail\",\"bot\":\"" + NetName + "\",\"op\":\"use\"," +
                "\"itemTemplate\":" + HealPotionTemplate + "}", 60_000);
            Assert.True(use.TryGetProperty("state", out var useState) && useState.GetString() == "Completed",
                "heal must complete: " + use);
            var hpHealed = use.GetProperty("hpAfter").GetInt32();
            Assert.True(hpHealed > hpWounded,
                $"HP must rise after the potion: wounded {hpWounded} → healed {hpHealed} " +
                $"(max {maxHp}): {use}");
            legs.Add(("heal", true, $"HP {hpWounded} → {hpHealed}/{maxHp} (+{hpHealed - hpWounded})", wall.Elapsed.TotalMilliseconds));

            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            Assert.True(unhandled == 0 && fatals == 0,
                $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the heal run");

            await WriteReportAsync(true, legs, $"potion {HealPotionTemplate} restored {hpHealed - hpWounded} HP ({hpWounded} → {hpHealed}/{maxHp})", wall.Elapsed.TotalSeconds);
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
        var report = new
        {
            test = "RecoveryHeal",
            revision = E2eStack.SourceRevision,
            passed,
            legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, ms = l.Ms }).ToArray(),
            evidence,
            wallSeconds
        };
        await File.WriteAllTextAsync(ReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
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
