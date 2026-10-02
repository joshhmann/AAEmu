
using AAEmu.Commons.Utils;

namespace AAEmu.UnitTests.Commons.Utils;

public class PrimeFinderTests
{
    [Test]
    public async Task NextPrime_ShouldReturnNextPrime_WhenCapacityIsNotPrime()
    {
        // Arrange
        PrimeFinder.Init();
        var desiredCapacity = 100;

        // Act
        var result = PrimeFinder.NextPrime(desiredCapacity);

        // Assert
        await Assert.That(result).IsGreaterThanOrEqualTo(101);
    }

    [Test]
    public async Task NextPrime_ShouldReturnLargerPrime_WhenCapacityIsPrime()
    {
        // Arrange
        PrimeFinder.Init();
        var desiredCapacity = 97;

        // Act
        var result = PrimeFinder.NextPrime(desiredCapacity);

        // Assert
        await Assert.That(result).IsGreaterThanOrEqualTo(98);
        await Assert.That(result).IsNotEqualTo(97);
    }

    [Test]
    [Arguments(1)]
    [Arguments(10)]
    [Arguments(50)]
    [Arguments(100)]
    [Arguments(500)]
    [Arguments(1000)]
    public async Task NextPrime_ShouldReturnValidPrime_WhenGivenVariousCapacities(int capacity)
    {
        // Arrange
        PrimeFinder.Init();

        // Act
        var result = PrimeFinder.NextPrime(capacity);

        // Assert
        await Assert.That(result).IsGreaterThanOrEqualTo(capacity);
    }

    [Test]
    public async Task NextPrime_ShouldReturnAtLeastGivenCapacity()
    {
        // Arrange
        PrimeFinder.Init();
        var desiredCapacity = 1000;

        // Act
        var result = PrimeFinder.NextPrime(desiredCapacity);

        // Assert
        await Assert.That(result >= desiredCapacity).IsTrue();
    }

    // TWO tests were removed here rather than kept, on the rule that a test
    // which cannot fail is worse than no test. Both probed NextPrime across the
    // whole table and asserted only "answer > capacity", and both stayed GREEN
    // with Init's Array.Sort deleted — measured, not assumed: an unsorted table
    // still answers greater-than for most probes by luck, so the assertion never
    // fired. An "Init is idempotent" variant (probe -> Init x3 -> re-probe ->
    // compare) survived BOTH the latch-removal and sort-removal mutations,
    // because re-sorting an already-sorted array is a no-op.
    //
    // What remains is the assertion that actually fails: the sortedness
    // invariant Array.BinarySearch depends on.

    /// <summary>
    /// The ordering invariant itself, stated directly so an unsorted table is
    /// caught even where NextPrime happens to answer the same value by luck:
    /// Init's sort must leave the table strictly ascending.
    /// </summary>
    [Test]
    public async Task Init_LeavesThePrimeTableStrictlyAscending()
    {
        PrimeFinder.Init();

        // Read the sorted table back out of the type by round-tripping every
        // probe: a strictly ascending table makes the sequence of answers
        // non-decreasing across strictly increasing probes.
        var probes = ProbeCapacities().ToList();
        var answers = new List<long>();
        foreach (var capacity in probes)
        {
            try
            {
                answers.Add(PrimeFinder.NextPrime(capacity));
            }
            catch (OverflowException)
            {
                break;
            }
        }

        for (var i = 1; i < answers.Count; i++)
        {
            await Assert.That(answers[i]).IsGreaterThanOrEqualTo(answers[i - 1])
                .Because($"answers must be non-decreasing across increasing capacities " +
                         $"({answers[i - 1]} then {answers[i]} at probe {probes[i]}) — " +
                         "a descending step means Init did not sort the table");
        }
    }

    /// <summary>
    /// Probe points across the whole table, including capacities past the first
    /// inversion so an unsorted table is observable.
    ///
    /// The sweep is computed in long and clamped to int.MaxValue: a
    /// `c *= 2` on an int wraps negative past 2^30, which would feed the
    /// comparison a capacity smaller than the previous probe and manufacture a
    /// false "descending" violation.
    /// </summary>
    private static IEnumerable<int> ProbeCapacities()
    {
        foreach (var capacity in new[] { 1, 5, 97, 100, 433, 1_000, 65_537, 1_000_003 })
            yield return capacity;

        // Log-spaced sweep up to the largest int the table can answer.
        for (long c = 2_000_000; c < int.MaxValue; c *= 2)
            yield return (int)c;
    }

    [Test]
    public async Task NextPrime_ShouldThrowOverflowException_WhenCapacityExceedsMaximum()
    {
        // Arrange
        PrimeFinder.Init();
        var desiredCapacity = int.MaxValue;

        // Act & Assert
        var exception = Assert.Throws<OverflowException>(() => PrimeFinder.NextPrime(desiredCapacity));
        await Assert.That(exception.Message).Contains("exceeds maximum available prime");
    }

}
