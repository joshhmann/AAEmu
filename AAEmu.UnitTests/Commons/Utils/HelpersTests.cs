
using AAEmu.Commons.Utils;

namespace AAEmu.UnitTests.Commons.Utils;

public class HelpersTests
{
    [Test]
    public async Task UnixTime_2026Timestamp_DoesNotClampToMaxValue()
    {
        // Regression for BUG-010: the old range check compared against DateTime.MaxValue.Second (59),
        // so any unix-seconds value > 59 decoded to DateTime.MaxValue (e.g. 1785894127s -> MaxValue),
        // corrupting timer-quest persistence (Quest.ReadData -> Helpers.UnixTime(long)).
        // Arrange
        const long timestamp = 1785894127; // 2026-08-05 in unix seconds

        // Act
        var result = Helpers.UnixTime(timestamp);

        // Assert
        await Assert.That(result).IsEqualTo(DateTime.UnixEpoch.AddSeconds(timestamp));
        await Assert.That(result.Year).IsEqualTo(2026);
        await Assert.That(result).IsNotEqualTo(DateTime.MaxValue);
    }

    [Test]
    public async Task UnixTime_TimestampGreaterThan59Seconds_DecodesToRealDate()
    {
        // Arrange
        const long timestamp = 100; // 1970-01-01 00:01:40 — old code clamped this to MaxValue

        // Act
        var result = Helpers.UnixTime(timestamp);

        // Assert
        await Assert.That(result).IsEqualTo(DateTime.UnixEpoch.AddSeconds(100));
        await Assert.That(result).IsNotEqualTo(DateTime.MaxValue);
    }

    [Test]
    public async Task UnixTime_RoundTrips_2026Timestamp()
    {
        // Arrange
        const long timestamp = 1785894127;

        // Act
        var roundTrip = Helpers.UnixTime(Helpers.UnixTime(timestamp));

        // Assert
        await Assert.That(roundTrip).IsEqualTo(timestamp);
    }

    [Test]
    public async Task UnixTime_MaxUnixTimeSeconds_IsStillDecodable()
    {
        // Arrange — largest unix-seconds value AddSeconds can represent
        var maxUnixTimeSeconds = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;

        // Act
        var result = Helpers.UnixTime(maxUnixTimeSeconds);

        // Assert
        await Assert.That(result).IsNotEqualTo(DateTime.MaxValue);
        await Assert.That(Helpers.UnixTime(result)).IsEqualTo(maxUnixTimeSeconds);
    }

    [Test]
    public async Task UnixTime_TimestampBeyondDateTimeMax_ReturnsMaxValue_WithoutThrowing()
    {
        // Arrange — one second past the maximum representable value
        const long timestamp = 253402300800;

        // Act
        var result = Helpers.UnixTime(timestamp);

        // Assert
        await Assert.That(result).IsEqualTo(DateTime.MaxValue);
    }

    [Test]
    public async Task UnixTime_DateTimeMaxValue_RoundTrips()
    {
        // Act
        var roundTrip = Helpers.UnixTime(Helpers.UnixTime(DateTime.MaxValue));

        // Assert
        await Assert.That(roundTrip).IsEqualTo(DateTime.MaxValue);
    }

    [Test]
    public async Task UnixTime_Negative_ReturnsMinValue()
    {
        // Act
        var result = Helpers.UnixTime(-1);

        // Assert
        await Assert.That(result).IsEqualTo(DateTime.MinValue);
    }

    [Test]
    public async Task UnixTime_Zero_ReturnsUnixEpoch()
    {
        // Act
        var result = Helpers.UnixTime(0);

        // Assert
        await Assert.That(result).IsEqualTo(DateTime.UnixEpoch);
        await Assert.That(Helpers.UnixTime(DateTime.UnixEpoch)).IsEqualTo(0);
    }
}
