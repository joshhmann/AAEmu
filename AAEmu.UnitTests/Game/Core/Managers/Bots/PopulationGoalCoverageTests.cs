using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Population coverage: fairness math, empty vacuity, starvation shape.
/// </summary>
[NotInParallel]
public class PopulationGoalCoverageTests
{
    [Test]
    public async Task JainFairness_EqualWork_IsOne()
    {
        await Assert.That(PopulationGoalCoverage.JainFairness([5, 5, 5, 5])).IsEqualTo(1.0);
    }

    [Test]
    public async Task JainFairness_OneStarved_DropsBelowOne()
    {
        var fair = PopulationGoalCoverage.JainFairness([10, 10, 10, 10]);
        var skewed = PopulationGoalCoverage.JainFairness([30, 0, 0, 0]);

        await Assert.That(skewed).IsLessThan(fair);
        await Assert.That(skewed).IsGreaterThan(0.0);
    }

    [Test]
    public async Task JainFairness_Empty_IsVacuousOne()
    {
        await Assert.That(PopulationGoalCoverage.JainFairness([])).IsEqualTo(1.0);
        await Assert.That(PopulationGoalCoverage.Empty().FairnessIndex).IsEqualTo(1.0);
    }
}
