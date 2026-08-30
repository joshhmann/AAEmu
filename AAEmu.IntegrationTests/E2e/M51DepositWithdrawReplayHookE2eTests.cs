using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M5.1 deposit/withdraw replay hook (t_7c224245): a REAL game server
/// boots in the deployment-shaped testing environment (same binaries, same
/// MySQL, same config precedence), a bot is provisioned through the shared
/// lifecycle, and the deposit-withdraw-cycle scenario drives the M5.1 economy
/// actions through the REAL engine paths on the live server:
///
///   - Character.ChangeMoney — the exact calls CSDepositMoneyPacket /
///     CSWithdrawMoneyPacket make (engine-validated balances);
///   - Inventory.SplitOrMoveItem — the exact call CSSwapItemsPacket makes
///     for Inventory↔Bank container moves (whole stack).
///
/// Every economy event must complete as an actor-contract request (the
/// runner refuses non-Completed events with their §17 reason), and the
/// template's acceptance criteria verify the final bank balance (600) and
/// per-container item quantities (round trip).
///
/// This is the replay hook Phase 2 (M3a/M4 economic replay) builds on:
/// recorded deposit/withdraw sequences replayed through the same scenario
/// machinery against a live world.
/// </summary>
[Collection("e2e")]
public class M51DepositWithdrawReplayHookE2eTests
{
    private const string BotName = "ReplayM5101";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task DepositWithdrawCycle_OnLiveServer_Completes_BalancesVerified()
    {
        E2eStack.EnsureUp();

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var payload = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"deposit-withdraw-cycle\",\"bot\":\"{BotName}\"}}",
            timeoutMs: 120_000);

        // The scenario verdict is machine-readable: template name, pass
        // flag, fail stage/reason, per-stage verdicts, criteria verdicts.
        Assert.True(payload.TryGetProperty("template", out var templateProp)
                    && templateProp.GetString() == "deposit-withdraw-cycle",
            "scenario must report the deposit-withdraw-cycle template: " + payload);

        var passed = payload.GetProperty("passed").GetBoolean();
        Assert.True(passed, "deposit-withdraw-cycle FAILED on the live server:\n" +
                            payload.GetProperty("evidence").GetString());

        // All four economy steps ran with one Completed actor request each.
        // NOTE: the bridge serializes the verdict records with the default
        // JsonSerializer (PascalCase preserved): stages[i].EventsFired,
        // criteria[i].Passed / criteria[i].Detail.
        Assert.Equal(4, payload.GetProperty("stages").GetArrayLength());
        Assert.All(Enumerable.Range(0, 4),
            i => Assert.Equal(1, payload.GetProperty("stages")[i].GetProperty("EventsFired").GetInt32()));

        // At least four actor-contract requests were recorded on the trace.
        Assert.True(payload.GetProperty("actorRequests").GetInt32() >= 4,
            "expected >= 4 actor requests, got " + payload.GetProperty("actorRequests"));

        // Acceptance criteria all pass (bank money 600, bag 5 / bank 0 of
        // item 15589 — the exact post-cycle balances).
        var criteria = payload.GetProperty("criteria");
        Assert.True(criteria.GetArrayLength() >= 3, "expected 3 acceptance criteria");
        Assert.All(Enumerable.Range(0, criteria.GetArrayLength()),
            i => Assert.True(criteria[i].GetProperty("Passed").GetBoolean(),
                "criterion failed: " + criteria[i].GetProperty("Detail").GetString()));

        // The trace records exist on the world side: the run result exposes
        // the actor request count; per-request records are visible through
        // the control-plane trace endpoint (read-only surface).
        Assert.NotNull(payload.GetProperty("evidence"));
        Assert.Contains("Verdict: PASS", payload.GetProperty("evidence").GetString());
    }
}
