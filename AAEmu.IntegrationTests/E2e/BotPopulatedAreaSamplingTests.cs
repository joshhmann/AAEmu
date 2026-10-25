using System.Diagnostics;
using System.Net;
using System.Text;

using System.Text.Json;
using Xunit;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.IntegrationTests.E2e;

public class BotPopulatedAreaSamplingTests
{
    [Fact]
    public async Task FiftyBotsAreAllSampledWithAtMostFourRequestsAtOnce()
    {
        var active = 0;
        var peak = 0;
        using var handler = new Handler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (current > previous && Interlocked.CompareExchange(ref peak, current, previous) != previous);
            try
            {
                await Task.Delay(20, token);
                return Json("{\"X\":1,\"Y\":2,\"Z\":3,\"Activity\":\"roam\"}");
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var client = Client(handler);
        var watches = Watches(50);
        var seconds = await BotPopulatedAreaE2eTests.SampleAsync(client, watches);
        Assert.InRange(peak, 2, 4);
        Assert.Equal(0, active);
        Assert.True(seconds > 0);
        Assert.All(watches.Values, watch =>
        {
            Assert.Equal(1, watch.Observations);
            Assert.Equal(0, watch.FailedObservations);
        });
    }

    [Fact]
    public async Task ServerAndMalformedResponsesRemainMissingObservations()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/bots/brain/bot1" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "/api/bots/brain/bot2" => Json("invalid json"),
            "/api/bots/brain/bot3" => Json("{\"Activity\":\"roam\"}"),
            _ => Json("{\"X\":1e100,\"Y\":0,\"Z\":0}"),
        }));
        using var client = Client(handler);
        var watches = Watches(4);
        await BotPopulatedAreaE2eTests.SampleAsync(client, watches);
        Assert.All(watches.Values, watch =>
        {
            Assert.Equal(0, watch.Observations);
            Assert.Equal(1, watch.FailedObservations);
            Assert.Equal(0, watch.TimedOutObservations);
            Assert.False(watch.HasContinuedWorkEvidence(DateTime.UtcNow));
        });
    }

    [Fact]
    public async Task UnresponsiveRequestIsCancelledAndReportedWithoutWaitingForClientTimeout()
    {
        var cancelled = false;
        using var handler = new Handler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Json("{}");
        });
        using var client = Client(handler);
        client.Timeout = TimeSpan.FromSeconds(60);
        var watches = Watches(1);
        var elapsed = Stopwatch.StartNew();
        await BotPopulatedAreaE2eTests.SampleAsync(client, watches);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "Observer exceeded its five-second request budget.");
        Assert.True(cancelled);
        Assert.Equal(0, watches["bot1"].Observations);
        Assert.Equal(1, watches["bot1"].FailedObservations);
        Assert.Equal(1, watches["bot1"].TimedOutObservations);
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("item")]
    [InlineData("experience")]
    [InlineData("completion")]
    public void StationaryNativeGainsCountWhileRawStandingTimeRemainsVisible(string gain)
    {
        var start = DateTime.UtcNow;
        var watch = new BotPopulatedAreaE2eTests.Watch { Name = "gatherer", StartedUtc = start };
        for (var i = 0; i <= 7; i++)
        {
            var now = start.AddSeconds(i * 50);
            var facts = Facts(now, gain == "objective" ? i : 0, gain == "item" ? i : 0,
                gain == "experience" ? i : 0, gain == "completion" ? i : 0);
            watch.Observe((1, 2, 3), "quest", now, facts);
        }
        Assert.Equal(350, watch.LongestStillSeconds);
        Assert.True(watch.HasContinuedWorkEvidence(start.AddSeconds(350)));
        Assert.Equal(7, watch.Samples.Count(s => s.ObservedGain));
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("missing")]
    [InlineData("new-quest")]
    [InlineData("stale")]
    public void StationaryActionsAndUncertainFactsCannotHideLackOfProgress(string kind)
    {
        var start = DateTime.UtcNow;
        var watch = new BotPopulatedAreaE2eTests.Watch { Name = "idle", StartedUtc = start };
        for (var i = 0; i <= 7; i++)
        {
            var now = start.AddSeconds(i * 50);
            var facts = kind == "missing" ? null
                : Facts(kind == "stale" ? start : now, kind is "new-quest" or "stale" ? i : 0,
                    questId: kind == "new-quest" ? (uint)i + 1 : 1);
            watch.Observe((1, 2, 3), i % 2 == 0 ? "Completed" : "Interact", now, facts);
        }
        Assert.False(watch.HasContinuedWorkEvidence(start.AddSeconds(350)));
        Assert.DoesNotContain(watch.Samples, s => s.ObservedGain);
    }

    [Fact]
    public void UsefulWorkDoesNotHideAnUnobservedFiveMinuteGap()
    {
        var start = DateTime.UtcNow;
        var watch = new BotPopulatedAreaE2eTests.Watch { Name = "gap", StartedUtc = start };
        watch.Observe((1, 2, 3), "quest", start, Facts(start));
        watch.Observe((1, 2, 3), "quest", start.AddSeconds(301), Facts(start.AddSeconds(301), xp: 1));
        Assert.False(watch.HasContinuedWorkEvidence(start.AddSeconds(301)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateMovementOrGainCannotEraseAnAlreadyObservedIdleInterval(bool movement)
    {
        var start = DateTime.UtcNow;
        var watch = new BotPopulatedAreaE2eTests.Watch { Name = "late", StartedUtc = start };
        for (var i = 0; i <= 6; i++)
            watch.Observe((1, 2, 3), "Interact", start.AddSeconds(i * 50), Facts(start.AddSeconds(i * 50)));
        watch.Observe((movement ? 10 : 1, 2, 3), "quest", start.AddSeconds(301), Facts(start.AddSeconds(301), xp: movement ? 0 : 1));
        Assert.False(watch.HasContinuedWorkEvidence(start.AddSeconds(301)));
        Assert.Equal(301, watch.LongestWithoutUsefulWorkSeconds);
    }

    [Fact]
    public async Task ExistingBrainRequestRetainsNativeFactsAndReceiveTime()
    {
        var facts = Facts(DateTime.UtcNow, objective: 2, items: 3);
        using var handler = new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        { X = 1, Y = 2, Z = 3, Activity = "quest", GameplayProgress = facts }))));
        using var client = Client(handler);
        var watches = Watches(1);
        await BotPopulatedAreaE2eTests.SampleAsync(client, watches);
        var sample = Assert.Single(watches["bot1"].Samples);
        Assert.Equal(facts.ObservedAtUtc, sample.GameplayProgress!.ObservedAtUtc);
        Assert.Equal(2, sample.GameplayProgress.ActiveQuests[0].Objectives[0]);
        Assert.Equal(3, sample.GameplayProgress.ObjectiveItemCounts![4058]);
        Assert.True(sample.ReceivedUtc >= facts.ObservedAtUtc.UtcDateTime);
        Assert.False(sample.ObservedGain); // First sight supplies a baseline, not progress.
    }

    private static BotQuestLoopObservation.Snapshot Facts(DateTime now, int objective = 0, int items = 0,
        int xp = 0, int completed = 0, uint questId = 1) => new()
    {
        ObservedAtUtc = new DateTimeOffset(now), CharacterId = 1, CharacterName = "test", Level = 5,
        Experience = xp, CompletedCount = completed, Money = 0, X = 1, Y = 2, Z = 3, ZoneId = 1,
        QuestLegActive = true, ArcCompleted = [], ObjectiveItemCounts = new Dictionary<uint, int> { [4058] = items },
        ActiveQuests = [new() { QuestId = questId, Status = "Progress", Step = "Progress", Objectives = [objective] }],
    };

    private static Dictionary<string, BotPopulatedAreaE2eTests.Watch> Watches(int count)
        => Enumerable.Range(1, count).ToDictionary(i => $"bot{i}",
            i => new BotPopulatedAreaE2eTests.Watch { Name = $"bot{i}", StartedUtc = DateTime.UtcNow });

    private static HttpClient Client(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("http://localhost") };

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
