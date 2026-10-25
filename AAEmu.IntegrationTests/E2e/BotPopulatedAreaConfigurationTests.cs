using Xunit;

namespace AAEmu.IntegrationTests.E2e;

public class BotPopulatedAreaConfigurationTests
{
    [Theory]
    [InlineData(null, 6)]
    [InlineData("", 6)]
    [InlineData(" ", 6)]
    [InlineData("2", 2)]
    [InlineData("10", 10)]
    [InlineData("50", 50)]
    public void RequestedPopulationIsNotSilentlyReduced(string? value, int expected)
        => Assert.Equal(expected, BotPopulatedAreaE2eTests.ParseBotCount(value));

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("51")]
    [InlineData("500")]
    [InlineData("invalid")]
    [InlineData("10.5")]
    [InlineData("2147483648")]
    public void UnsupportedPopulationIsRefused(string value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BotPopulatedAreaE2eTests.ParseBotCount(value));

    private static readonly DateTime Start = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
    private static BotPopulatedAreaE2eTests.Watch NewWatch()
        => new() { Name = "ObservedBot", StartedUtc = Start };

    [Fact]
    public void NoSuccessfulPositionChecksCannotPass()
    {
        var watch = NewWatch();
        watch.FailedObservations = 10;
        Assert.False(watch.HasContinuedWorkEvidence(Start.AddSeconds(150)));
    }

    [Fact]
    public void OnePositionCheckCannotProveMovement()
    {
        var watch = NewWatch();
        watch.Observe((10, 0, 0), "roam", Start.AddSeconds(15));
        Assert.False(watch.HasContinuedWorkEvidence(Start.AddSeconds(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BlindStallWindowAtStartMiddleOrEndCannotPass(int boundary)
    {
        var watch = NewWatch();
        var first = boundary == 0 ? 300 : 0;
        var second = boundary == 1 ? 300 : first + 15;
        watch.Observe((0, 0, 0), "roam", Start.AddSeconds(first));
        watch.Observe((10, 0, 0), "roam", Start.AddSeconds(second));
        var end = Start.AddSeconds(second + (boundary == 2 ? 300 : 1));
        watch.FinishObservation(end);
        Assert.False(watch.HasContinuedWorkEvidence(end));
        Assert.True(watch.LongestObservationGapSeconds >= 300);
    }

    [Fact]
    public void ObservedStationaryBotCannotPass()
    {
        var watch = NewWatch();
        for (var seconds = 0; seconds <= 300; seconds += 15)
            watch.Observe((10, 0, 0), "quest.travel", Start.AddSeconds(seconds));
        Assert.False(watch.HasContinuedWorkEvidence(Start.AddSeconds(300)));
        Assert.Equal(300, watch.LongestStillSeconds);
    }

    [Fact]
    public void BriefMissingCheckDoesNotEraseEarlierStillness()
    {
        var watch = NewWatch();
        watch.Observe((10, 0, 0), "quest.travel", Start);
        watch.FailedObservations++;
        watch.Observe((10, 0, 0), "quest.travel", Start.AddSeconds(30));
        watch.Observe((12, 0, 0), "roam", Start.AddSeconds(45));
        watch.FinishObservation(Start.AddSeconds(46));
        Assert.True(watch.HasContinuedWorkEvidence(Start.AddSeconds(46)));
        Assert.Equal(30, watch.LongestStillSeconds);
        Assert.Equal(30, watch.LongestObservationGapSeconds);
        Assert.Equal(3, watch.Observations);
        Assert.Equal(1, watch.FailedObservations);
    }
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void InvalidCoordinateCannotCountAsMovement(float coordinate)
    {
        var watch = NewWatch();
        Assert.Throws<FormatException>(() => watch.Observe((coordinate, 0, 0), "roam", Start));
        Assert.Equal(0, watch.Observations);
        Assert.False(watch.HasContinuedWorkEvidence(Start));
    }
}
