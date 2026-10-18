using AAEmu.Game.Core.Managers.Bots;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The operator assignment store (plan T1, gap N2): ONE registry behind the HTTP
/// route, the E2E bridge, the arbitration band and the executor's travel leg.
/// These tests pin the contract those four readers depend on — an unknown
/// activity is refused instead of silently ignored, every assignment is bounded
/// by an expiry, expiry is evaluated on read with a recorded reason, and a
/// terminal assignment never moves again (a late leg verdict cannot rewrite
/// history).
/// </summary>
[NotInParallel]
public class BotAssignmentRegistryTests
{
    private const uint BotId = 0x0040_1001u;

    [Test]
    public async Task Accept_UnknownActivity_IsRefusedAndNothingIsStored()
    {
        var registry = new BotAssignmentRegistry();

        var accepted = registry.Accept(BotId, "assigned.dance", 1f, 2f, 3f, TimeSpan.FromSeconds(60));

        await Assert.That(accepted).IsNull();
        await Assert.That(registry.TryGet(BotId, out var stored)).IsFalse();
        await Assert.That(stored).IsNull();
    }

    [Test]
    public async Task Accept_BoundsTheExpiryToTheSupportedRange()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var registry = new BotAssignmentRegistry { UtcNow = () => now };

        var overlong = registry.Accept(BotId, BotAssignmentActivities.Travel, 1f, 2f, 3f, TimeSpan.FromHours(5));
        await Assert.That(overlong).IsNotNull();
        await Assert.That((overlong!.ExpiresUtc - overlong.AcceptedUtc).TotalSeconds)
            .IsEqualTo(BotAssignmentRegistry.MaxExpirySeconds);

        var unbounded = registry.Accept(BotId, BotAssignmentActivities.Travel, 1f, 2f, 3f, TimeSpan.Zero);
        await Assert.That(unbounded).IsNotNull();
        await Assert.That((unbounded!.ExpiresUtc - unbounded.AcceptedUtc).TotalSeconds)
            .IsEqualTo(BotAssignmentRegistry.DefaultExpirySeconds);
    }

    [Test]
    public async Task TryGetLive_ExpiredAssignment_ReportsExpiredWithTheReason()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var registry = new BotAssignmentRegistry { UtcNow = () => now };
        registry.Accept(BotId, BotAssignmentActivities.Travel, 5f, 0f, 5f, TimeSpan.FromSeconds(60), "operator-api");

        await Assert.That(registry.TryGetLive(BotId, out var live)).IsTrue();
        await Assert.That(live!.State).IsEqualTo(BotAssignmentState.Accepted);
        await Assert.That(live.RequestedBy).IsEqualTo("operator-api");

        now = now.AddSeconds(61);

        await Assert.That(registry.TryGetLive(BotId, out var expired)).IsFalse();
        await Assert.That(registry.TryGet(BotId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Expired);
        await Assert.That(recorded.LastReason).Contains("expired after 60s");
    }

    [Test]
    public async Task TryGet_ExpiredLiveAssignment_ReportsExpiredWithoutATick()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var registry = new BotAssignmentRegistry { UtcNow = () => now };
        registry.Accept(BotId, BotAssignmentActivities.Travel, 5f, 0f, 5f, TimeSpan.FromSeconds(30));

        now = now.AddSeconds(31);

        // For a bot nobody is ticking (wake hook refused, scheduler stopped) the
        // operator's read-back is the ONLY reader: it has to apply the same lazy
        // expiry the executor does instead of reporting a past ExpiresUtc as live
        // (code review 2026-10-16, P3-4).
        await Assert.That(registry.TryGet(BotId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Expired);
        await Assert.That(recorded.LastReason).Contains("expired after 30s");
        await Assert.That(registry.TryGetLive(BotId, out _)).IsFalse();
    }

    [Test]
    public async Task TerminalAssignment_IsNotRewrittenByALaterVerdict()
    {
        var registry = new BotAssignmentRegistry();
        registry.Accept(BotId, BotAssignmentActivities.Travel, 1f, 0f, 1f, TimeSpan.FromSeconds(60));

        registry.Complete(BotId, "arrived (0.10m from the destination)");
        registry.Fail(BotId, "a late leg verdict");

        await Assert.That(registry.TryGet(BotId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Completed);
        await Assert.That(recorded.LastReason).IsEqualTo("arrived (0.10m from the destination)");
    }

    [Test]
    public async Task MarkActive_OnABotWithoutAnAssignment_IsANoOp()
    {
        var registry = new BotAssignmentRegistry();

        registry.MarkActive(0x0040_9999u, "elected by the arbiter");

        await Assert.That(registry.TryGet(0x0040_9999u, out var stored)).IsFalse();
        await Assert.That(stored).IsNull();
    }

    [Test]
    public async Task Accept_ReplacesALiveAssignment()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var registry = new BotAssignmentRegistry { UtcNow = () => now };
        registry.Accept(BotId, BotAssignmentActivities.Travel, 1f, 0f, 1f, TimeSpan.FromSeconds(60));
        registry.MarkActive(BotId, "elected by the arbiter");

        var replacement = registry.Accept(BotId, BotAssignmentActivities.Travel, 9f, 0f, 9f, TimeSpan.FromSeconds(120));

        await Assert.That(replacement).IsNotNull();
        await Assert.That(replacement!.State).IsEqualTo(BotAssignmentState.Accepted);
        await Assert.That(replacement.X).IsEqualTo(9f);
        await Assert.That((replacement.ExpiresUtc - replacement.AcceptedUtc).TotalSeconds).IsEqualTo(120d);
        await Assert.That(registry.TryGetLive(BotId, out var live)).IsTrue();
        await Assert.That(live!.X).IsEqualTo(9f);
    }

    [Test]
    public async Task Accept_WakesTheBotExactlyOnce()
    {
        var woken = new List<uint>();
        var registry = new BotAssignmentRegistry { Wake = woken.Add };

        registry.Accept(BotId, BotAssignmentActivities.Travel, 1f, 0f, 1f, TimeSpan.FromSeconds(60));
        // A refused activity is not an assignment and must not wake anything.
        registry.Accept(BotId, "assigned.unsupported", 1f, 0f, 1f, TimeSpan.FromSeconds(60));

        await Assert.That(woken.Count).IsEqualTo(1);
        await Assert.That(woken[0]).IsEqualTo(BotId);
    }

    [Test]
    public async Task Accept_AWakeHookThatThrows_StillAcceptsTheAssignment()
    {
        var registry = new BotAssignmentRegistry
        {
            Wake = _ => throw new InvalidOperationException("the scheduler is stopped"),
        };

        var accepted = registry.Accept(BotId, BotAssignmentActivities.Travel, 1f, 0f, 1f, TimeSpan.FromSeconds(60));

        await Assert.That(accepted).IsNotNull();
        await Assert.That(accepted!.State).IsEqualTo(BotAssignmentState.Accepted);
        await Assert.That(registry.TryGetLive(BotId, out _)).IsTrue();
    }
}
