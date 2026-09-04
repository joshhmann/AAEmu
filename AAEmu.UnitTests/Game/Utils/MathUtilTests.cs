using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Utils;

/// <summary>
/// Tests for MathUtil class
/// </summary>
public class MathUtilTests
{
    [Test]
    public async Task CalculateAngleFrom_WithSamePoints_ReturnsZero()
    {
        // Arrange
        const float x1 = 0, y1 = 0;
        const float x2 = 0, y2 = 0;

        // Act
        var result = MathUtil.CalculateAngleFrom(x1, y1, x2, y2);

        // Assert
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task CalculateAngleFrom_WithPointOnXAxis_ReturnsZero()
    {
        // Arrange
        const float x1 = 0, y1 = 0;
        const float x2 = 10, y2 = 0;

        // Act
        var result = MathUtil.CalculateAngleFrom(x1, y1, x2, y2);

        // Assert
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task CalculateAngleFrom_WithPointOnYAxis_Returns90Degrees()
    {
        // Arrange
        const float x1 = 0, y1 = 0;
        const float x2 = 0, y2 = 10;

        // Act
        var result = MathUtil.CalculateAngleFrom(x1, y1, x2, y2);

        // Assert
        await Assert.That(result).IsEqualTo(90).Within(0.00001f);
    }

    [Test]
    public async Task CalculateAngleFrom_WithNegativeXAxis_Returns180Degrees()
    {
        // Arrange
        const float x1 = 0, y1 = 0;
        const float x2 = -10, y2 = 0;

        // Act
        var result = MathUtil.CalculateAngleFrom(x1, y1, x2, y2);

        // Assert
        await Assert.That(result).IsEqualTo(180).Within(0.00001f);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(90, 32)]
    [Arguments(180, 64)]
    [Arguments(270, -32)]
    [Arguments(359, 0)]
    public async Task ConvertDegreeToSByteDirection_ValidDegrees_ReturnsExpectedDirection(double degree, sbyte expected)
    {
        // Act
        var result = MathUtil.ConvertDegreeToSByteDirection(degree);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(28, 78.75)]
    [Arguments(56, 157.5)]
    [Arguments(85, 239.0625)]
    [Arguments(113, 317.8125)]
    public async Task ConvertSbyteDirectionToDegree_ValidDirections_ReturnsExpectedDegree(sbyte direction, float expected)
    {
        // Act
        var result = MathUtil.ConvertSbyteDirectionToDegree(direction);

        // Assert
        await Assert.That(result).IsEqualTo(expected).Within(0.00001f);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 57.29578)]
    [Arguments(3.14159, 180)]
    [Arguments(6.28318, 360)]
    public async Task RadToDeg_ValidRadians_ReturnsExpectedDegrees(float radians, float expected)
    {
        // Act
        var result = radians.RadToDeg();

        // Assert
        await Assert.That(result).IsEqualTo(expected).Within(0.005f);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(57.29578, 1)]
    [Arguments(180, 3.14159)]
    [Arguments(360, 6.28318)]
    public async Task DegToRad_ValidDegrees_ReturnsExpectedRadians(float degrees, float expected)
    {
        // Act
        var result = degrees.DegToRad();

        // Assert
        await Assert.That(result).IsEqualTo(expected).Within(0.005f);
    }

    [Test]
    public async Task CalculateDistance_2DAnd3D_ReturnsAccurateDistances()
    {
        var v1 = new System.Numerics.Vector3(0, 0, 0);
        var v2 = new System.Numerics.Vector3(3, 4, 12);

        // 2D: sqrt(3^2 + 4^2) = 5
        var dist2D = MathUtil.CalculateDistance(v1, v2, includeZAxis: false);
        await Assert.That(dist2D).IsEqualTo(5f).Within(0.0001f);

        // 3D: sqrt(3^2 + 4^2 + 12^2) = 13
        var dist3D = MathUtil.CalculateDistance(v1, v2, includeZAxis: true);
        await Assert.That(dist3D).IsEqualTo(13f).Within(0.0001f);

        // Squared
        var distSq2D = MathUtil.DistanceSqVectors(v1, v2, includeZAxis: false);
        await Assert.That(distSq2D).IsEqualTo(25f).Within(0.0001f);

        var distSq3D = MathUtil.DistanceSqVectors(v1, v2, includeZAxis: true);
        await Assert.That(distSq3D).IsEqualTo(169f).Within(0.0001f);
    }

    [Test]
    public async Task FilterWithinRadius2D_SIMD_MatchesExpectedEntities()
    {
        // 25 sample points (exercising AVX2 8-wide loop, 4-wide loop, and scalar remainder)
        var xs = new float[25];
        var ys = new float[25];

        for (int i = 0; i < 25; i++)
        {
            xs[i] = i * 2.0f; // 0, 2, 4, 6, 8, ...
            ys[i] = 0.0f;
        }

        // Origin at (0, 0), radius 10 (radiusSq = 100). Points with x in [0, 10]:
        // i = 0 (x=0), 1 (x=2), 2 (x=4), 3 (x=6), 4 (x=8), 5 (x=10). Total = 6 points.
        var matched = new int[25];
        int count = MathUtil.FilterWithinRadius2D(xs, ys, 0.0f, 0.0f, 100.0f, matched);

        await Assert.That(count).IsEqualTo(6);
        for (int i = 0; i < 6; i++)
        {
            await Assert.That(matched[i]).IsEqualTo(i);
        }
    }

    [Test]
    public async Task FilterWithinRadius2D_With250Entities_MatchesScalarBaseline()
    {
        const int count = 250;
        var xs = new float[count];
        var ys = new float[count];
        var rng = new Random(42);

        for (int i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
            ys[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
        }

        float originX = 12.5f;
        float originY = -34.2f;
        float maxRadius = 45.0f;
        float maxRadSq = maxRadius * maxRadius;

        // Scalar baseline
        var expected = new List<int>();
        for (int i = 0; i < count; i++)
        {
            float dx = xs[i] - originX;
            float dy = ys[i] - originY;
            if ((dx * dx + dy * dy) <= maxRadSq)
            {
                expected.Add(i);
            }
        }

        // SIMD kernel
        var actual = new int[count];
        int actualCount = MathUtil.FilterWithinRadius2D(xs, ys, originX, originY, maxRadSq, actual);

        await Assert.That(actualCount).IsEqualTo(expected.Count);
        for (int i = 0; i < actualCount; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }

    [Test]
    public async Task Benchmark_FilterWithinRadius2D_250Entities()
    {
        const int entityCount = 250;
        const int iterations = 50_000;
        var xs = new float[entityCount];
        var ys = new float[entityCount];
        var rng = new Random(1337);

        for (int i = 0; i < entityCount; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
            ys[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
        }

        float originX = 5.0f;
        float originY = 5.0f;
        float maxRadius = 35.0f;
        float maxRadSq = maxRadius * maxRadius;

        var matchesBuffer = new int[entityCount];

        // Warmup
        for (int i = 0; i < 1000; i++)
        {
            MathUtil.FilterWithinRadius2D(xs, ys, originX, originY, maxRadSq, matchesBuffer);
        }

        // Benchmark SIMD
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int simdTotalMatches = 0;
        for (int iter = 0; iter < iterations; iter++)
        {
            simdTotalMatches += MathUtil.FilterWithinRadius2D(xs, ys, originX, originY, maxRadSq, matchesBuffer);
        }
        sw.Stop();
        long simdTicks = sw.ElapsedTicks;
        double simdMs = sw.Elapsed.TotalMilliseconds;

        // Benchmark Scalar baseline
        sw.Restart();
        int scalarTotalMatches = 0;
        for (int iter = 0; iter < iterations; iter++)
        {
            int count = 0;
            for (int i = 0; i < entityCount; i++)
            {
                float dx = xs[i] - originX;
                float dy = ys[i] - originY;
                if ((dx * dx + dy * dy) <= maxRadSq)
                {
                    matchesBuffer[count++] = i;
                }
            }
            scalarTotalMatches += count;
        }
        sw.Stop();
        long scalarTicks = sw.ElapsedTicks;
        double scalarMs = sw.Elapsed.TotalMilliseconds;

        double speedup = (double)scalarTicks / simdTicks;
        Console.WriteLine($"[BENCHMARK 250-Entities over 50k iterations]");
        Console.WriteLine($"  Scalar : {scalarMs:F2} ms ({scalarTicks} ticks) -> {(scalarMs * 1_000_000 / iterations / entityCount):F2} ns per entity");
        Console.WriteLine($"  SIMD   : {simdMs:F2} ms ({simdTicks} ticks) -> {(simdMs * 1_000_000 / iterations / entityCount):F2} ns per entity");
        Console.WriteLine($"  Speedup: {speedup:F2}x with AVX2 Vector256");

        await Assert.That(simdTotalMatches).IsEqualTo(scalarTotalMatches);
    }
}


