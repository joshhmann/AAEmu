using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Replan taxonomy covers all trigger kinds with bounded debounce.
/// </summary>
[NotInParallel]
public class ReplanTaxonomyTests
{
    [Test]
    public async Task Mappings_CoverAllTriggerKinds()
    {
        var kinds = Enum.GetValues<ReplanTaxonomy.TriggerKind>();
        var mapped = ReplanTaxonomy.Mappings().Select(m => m.Trigger).ToHashSet();

        foreach (var kind in kinds)
            await Assert.That(mapped.Contains(kind)).IsTrue();
    }

    [Test]
    public async Task Mappings_FailureTriggers_CarryBackoff()
    {
        var map = ReplanTaxonomy.Mappings().ToDictionary(m => m.Trigger);

        await Assert.That(map[ReplanTaxonomy.TriggerKind.GoalImpossible].Debounce).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(map[ReplanTaxonomy.TriggerKind.PlanRepeatedlyFails].Debounce).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(map[ReplanTaxonomy.TriggerKind.GoalCompleted].Debounce).IsEqualTo(TimeSpan.Zero);
    }
}
