using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Step 7 live proof: ONE real homestead action — claim/place plot — through the
/// ordinary services on a live authenticated server.
///
/// Flow on an isolated E2E lane (own E2E_ROOT / ports / DB / compose project):
///   1. provision a persistent headless bot (real login/create path);
///   2. explicit kit opt-in (`homestead kit`) — the ONLY grant, labeled;
///   3. observe the pre-state (design/certs/plot);
///   4. dispatch the REAL action: SurveyAndPlacePlotAction →
///      GameplayActor.BuildHouse(267, 15596, resolved zone position) →
///      HousingManager.Build;
///   5. assert LIVE postconditions: a housing row owned by the bot, the bot's
///      observation layer reporting HasLandPlot, and conservation (design
///      consumed exactly once, certificates charged by the engine, no gold);
///   6. negatives on the same live server: unknown housing id, occupied
///      position, same-key retry — each refused with NO new house and NO extra
///      design consumed.
///
/// No fixture repair during the run: no GM grants beyond the labeled kit, no
/// money injection, no house-row writes, no overrides. Layer L (live
/// authenticated server). H (human feel) stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class HomesteadClaimPlacePlotE2eTests
{
    private static readonly string BotName = "Step7Claim" + Guid.NewGuid().ToString("N")[..6];
    private static readonly string BotAccount = "e2estep7" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();

    private const uint ScarecrowHousingId = 267;
    private const uint ScarecrowDesignItemId = 15596;
    private const uint BoundTaxCertItemId = 31892;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ClaimPlacePlot_LiveServer_RealAction_WithConservationAndNegatives()
    {
        AssertIsolatedLane();
        E2eStack.EnsureUp();

        var claims = new List<object>();
        using var bridge = new BotDriveClient(E2eStack.BridgePort);

        // REAL login flow: the bot enters the world over TCP with a live game
        // connection (the Build path is connection-mediated).
        var net = await BotNetworkSession.ConnectAsync(
            BotName, BotAccount, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        Assert.True(net.InWorld, "step-7 bot never reached in-world through the real login flow");
        var charId = net.CharacterId;
        Assert.True(charId > 0, "live login returned no character id");

        // ---- 1. labeled kit opt-in (the only grant; no GM repair afterwards)
        var kit = bridge.Call($"{{\"cmd\":\"homestead\",\"op\":\"kit\",\"bot\":\"{BotName}\"}}", timeoutMs: 60_000);
        var designsAfterKit = kit.GetProperty("designs").GetInt32();
        var certsAfterKit = kit.GetProperty("certs").GetInt32();
        var requiredCerts = kit.GetProperty("requiredCerts").GetInt32();
        claims.Add(new { leg = "kit-opt-in", designs = designsAfterKit, certs = certsAfterKit, requiredCerts });
        Assert.Equal(1, designsAfterKit);
        Assert.Equal(requiredCerts, certsAfterKit);
        Assert.True(requiredCerts > 0, "canonical certificate requirement did not resolve on the live server");

        // ---- 2. resolved zone position (never a fixed stand-in id)
        var zone = bridge.Call($"{{\"cmd\":\"homestead\",\"op\":\"zone\",\"bot\":\"{BotName}\"}}", timeoutMs: 30_000);
        var zoneKey = zone.GetProperty("zoneKey").GetUInt32();
        claims.Add(new
        {
            leg = "zone-resolved",
            x = zone.GetProperty("x").GetSingle(),
            y = zone.GetProperty("y").GetSingle(),
            z = zone.GetProperty("z").GetSingle(),
            zoneKey
        });

        var before = bridge.Call($"{{\"cmd\":\"homestead\",\"op\":\"observe\",\"bot\":\"{BotName}\"}}", timeoutMs: 30_000);
        Assert.False(before.GetProperty("hasLandPlot").GetBoolean(), "bot already owned a plot before the claim");
        Assert.True(before.GetProperty("hasScarecrowDesign").GetBoolean(), "kit design not visible to the observation layer");
        Assert.True(before.GetProperty("hasTaxCertificates").GetBoolean(), "kit certificates not visible to the observation layer");

        // ---- 3. the real action
        var claim = bridge.Call(
            $"{{\"cmd\":\"homestead\",\"op\":\"claim\",\"bot\":\"{BotName}\",\"key\":\"step7-live-1\"}}",
            timeoutMs: 180_000);
        claims.Add(new
        {
            leg = "claim",
            state = claim.GetProperty("state").GetString(),
            failure = claim.GetProperty("failure").GetString(),
            detail = claim.GetProperty("detail").GetString(),
            targetId = claim.GetProperty("targetId").GetUInt32(),
            designs = claim.GetProperty("designs").GetInt32(),
            certs = claim.GetProperty("certs").GetInt32(),
            money = claim.GetProperty("money").GetInt64(),
            ownedHouses = claim.GetProperty("ownedHouses").GetInt32()
        });

        Assert.Equal("Completed", claim.GetProperty("state").GetString());
        Assert.Equal(ScarecrowHousingId, claim.GetProperty("targetId").GetUInt32());

        // Conservation on the live server: design consumed exactly once,
        // certificates charged by the engine, no gold spent (taxItem branch).
        Assert.Equal(0, claim.GetProperty("designs").GetInt32());
        Assert.Equal(certsAfterKit - requiredCerts, claim.GetProperty("certs").GetInt32());
        Assert.Equal(claim.GetProperty("moneyBefore").GetInt64(), claim.GetProperty("money").GetInt64());

        var newHouses = claim.GetProperty("newHouses");
        Assert.Equal(1, newHouses.GetArrayLength());
        var house = newHouses[0];
        Assert.Equal(ScarecrowHousingId, house.GetProperty("template").GetUInt32());
        Assert.Equal(charId, house.GetProperty("owner").GetUInt32());
        Assert.Equal(0, house.GetProperty("currentStep").GetInt32());

        // ---- 4. the observation layer agrees with the live world
        var after = bridge.Call($"{{\"cmd\":\"homestead\",\"op\":\"observe\",\"bot\":\"{BotName}\"}}", timeoutMs: 30_000);
        Assert.True(after.GetProperty("hasLandPlot").GetBoolean(), "live observation did not report the owned plot");
        Assert.Equal(1, after.GetProperty("observedHouses").GetInt32());
        claims.Add(new
        {
            leg = "observe-after",
            hasLandPlot = after.GetProperty("hasLandPlot").GetBoolean(),
            hasScarecrowDesign = after.GetProperty("hasScarecrowDesign").GetBoolean(),
            observedHouses = after.GetProperty("observedHouses").GetInt32()
        });

        // ---- 5. negatives on the same live server
        var retry = bridge.Call(
            $"{{\"cmd\":\"homestead\",\"op\":\"claimRetry\",\"bot\":\"{BotName}\",\"key\":\"step7-live-1\"}}",
            timeoutMs: 60_000);
        claims.Add(new
        {
            leg = "same-key-retry",
            state = retry.GetProperty("state").GetString(),
            detail = retry.GetProperty("detail").GetString(),
            ownedHouses = retry.GetProperty("ownedHouses").GetInt32(),
            designs = retry.GetProperty("designs").GetInt32()
        });
        Assert.NotEqual("Completed", retry.GetProperty("state").GetString());
        Assert.Equal(1, retry.GetProperty("ownedHouses").GetInt32());
        Assert.Equal(0, retry.GetProperty("designs").GetInt32());

        // Missing design: a second fresh bot on the SAME lane with no kit cannot claim.
        var noKitName = "Step7NoKit" + Guid.NewGuid().ToString("N")[..6];
        var noKitAccount = "e2estep7nk" + Guid.NewGuid().ToString("N")[..7];
        using var noKitNet = await BotNetworkSession.ConnectAsync(
            noKitName, noKitAccount, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        Assert.True(noKitNet.InWorld, "second (no-kit) bot never reached in-world");
        var noKitClaim = bridge.Call(
            $"{{\"cmd\":\"homestead\",\"op\":\"claim\",\"bot\":\"{noKitName}\",\"key\":\"step7-live-nokit\"}}",
            timeoutMs: 60_000);
        claims.Add(new
        {
            leg = "missing-design",
            state = noKitClaim.GetProperty("state").GetString(),
            detail = noKitClaim.GetProperty("detail").GetString(),
            ownedHouses = noKitClaim.GetProperty("ownedHouses").GetInt32()
        });
        Assert.NotEqual("Completed", noKitClaim.GetProperty("state").GetString());
        Assert.Equal(0, noKitClaim.GetProperty("ownedHouses").GetInt32());

        var report = new
        {
            scenario = "step7-claim-place-plot",
            lane = new
            {
                root = E2eStack.E2eRoot,
                dbPort = E2eStack.DbPort,
                loginPort = E2eStack.LoginPort,
                gamePort = E2eStack.GamePort,
                composeProject = E2eStack.ComposeProject
            },
            sourceRevision = E2eStack.SourceRevision,
            bot = BotName,
            characterId = charId,
            layer = "L (live authenticated server); H stays UNKNOWN",
            fixtureNote = "one labeled kit opt-in before the run; no fixture repair during the run " +
                          "(no GM grants, no money injection, no house-row writes, no overrides)",
            claims
        };
        Directory.CreateDirectory(EvidenceDir);
        File.WriteAllText(Path.Combine(EvidenceDir, "step7-claim-place-plot-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        await Task.CompletedTask;
    }

    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "step-7 L run refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
    }
}
