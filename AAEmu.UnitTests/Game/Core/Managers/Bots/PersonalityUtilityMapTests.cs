using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Personality weights stay bounded, unknown maps to zeros, same input is deterministic.
/// </summary>
[NotInParallel]
public class PersonalityUtilityMapTests
{
    [Test]
    public async Task For_Farmer_PrefersFarmWithinBounds()
    {
        var weights = PersonalityUtilityMap.For("farmer");

        await Assert.That(weights.FarmWeight).IsGreaterThan(0);
        await Assert.That(weights.FarmWeight).IsLessThanOrEqualTo(20);
        await Assert.That(weights.CombatWeight).IsEqualTo(0);
    }

    [Test]
    public async Task For_Unknown_MapsToZeros()
    {
        var weights = PersonalityUtilityMap.For("definitely-not-an-archetype");

        await Assert.That(weights).IsEqualTo(new PersonalityUtilityMap.GoalWeights());
    }

    [Test]
    public async Task For_CaseInsensitive_MatchesArchetype()
    {
        await Assert.That(PersonalityUtilityMap.For("Guard")).IsEqualTo(PersonalityUtilityMap.For("guard"));
    }

    [Test]
    public async Task For_AllWeights_StayWithinBounds()
    {
        foreach (var key in new[] { "farmer", "merchant", "guard", "pirate", "cheerful", "greedy", "lawful", "paranoid", "", "  " })
        {
            var weights = PersonalityUtilityMap.For(key);
            foreach (var value in new[] { weights.QuestWeight, weights.FarmWeight, weights.CombatWeight, weights.TradeWeight, weights.CraftWeight, weights.ExploreWeight })
                await Assert.That(Math.Abs(value)).IsLessThanOrEqualTo(20);
        }
    }
}
