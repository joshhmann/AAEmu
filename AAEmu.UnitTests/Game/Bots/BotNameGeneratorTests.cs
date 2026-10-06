using System.Text.RegularExpressions;

using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// In-world character naming for provisioned bots. The pool is DATA
/// (Data/Bots/bot_names.json), so these tests assert the contracts that make a
/// generated name usable in the world — valid for the name field, distinct
/// across a roster, reproducible across boots, and never colliding with a name
/// already registered in the live registry.
/// </summary>
[NotInParallel]
public class BotNameGeneratorTests
{
    /// <summary>NameManager's default character-name rule (NameManager.cs:70).</summary>
    private static readonly Regex NameRegex = new(@"^[a-zA-Z0-9а-яА-Я]{1,18}$");

    // --------------------------------------------------------------- pool

    [Test]
    public async Task Pool_IsNonEmpty_AndEveryNameSatisfiesTheCharacterNameRule()
    {
        var pool = BotNameGenerator.Pool;
        await Assert.That(pool.Count).IsGreaterThan(0);

        foreach (var name in pool)
        {
            await Assert.That(NameRegex.IsMatch(name)).IsTrue();
            await Assert.That(name).IsEqualTo(name.Trim()); // no stray whitespace
        }
    }

    [Test]
    public async Task Pool_HasNoDuplicates_IgnoringCase()
    {
        var pool = BotNameGenerator.Pool;
        await Assert.That(pool.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(pool.Count);
    }

    [Test]
    public async Task Pool_NamesReadAsPeople_NotAsTestFixtures()
    {
        // The regression this whole change exists for: presence bots used to be
        // Citizen01/Citizen02. Nothing in the pool may look like that again.
        foreach (var name in BotNameGenerator.Pool)
            await Assert.That(name.StartsWith("Citizen", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Pool_IsLargeEnoughForALargeRoster()
    {
        // Distinct roster slots must be able to get distinct names without the
        // collision walk running dry; the demo bound is 10 by default.
        await Assert.That(BotNameGenerator.Pool.Count).IsGreaterThanOrEqualTo(64);
    }

    // -------------------------------------------------------------- naming

    [Test]
    public async Task NoPrefix_TwoRosterSlots_GetDifferentNames()
    {
        var first = BotNameGenerator.NameFor(0, null);
        var second = BotNameGenerator.NameFor(1, null);

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task NoPrefix_RosterSlots_ArePairwiseDistinct_WithoutAnyProbe()
    {
        var pool = BotNameGenerator.Pool;
        var names = Enumerable.Range(0, pool.Count).Select(i => BotNameGenerator.NameFor(i, null)).ToList();

        await Assert.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(pool.Count);
    }

    [Test]
    public async Task SameRoster_NamesAreReproducibleAcrossRuns()
    {
        // Determinism is the restart-idempotency contract: a hard-killed lane
        // comes back up with the SAME citizens, adopting the rows the previous
        // boot created, instead of orphaning them under fresh names.
        for (var i = 0; i < 10; i++)
            await Assert.That(BotNameGenerator.NameFor(i, null)).IsEqualTo(BotNameGenerator.NameFor(i, null));
    }

    [Test]
    public async Task ConfiguredPrefix_KeepsLegacyPrefixedIndexShape()
    {
        await Assert.That(BotNameGenerator.NameFor(0, "Citizen")).IsEqualTo("Citizen01");
        await Assert.That(BotNameGenerator.NameFor(2, "Citizen")).IsEqualTo("Citizen03");
        await Assert.That(BotNameGenerator.NameFor(0, "Villager")).IsEqualTo("Villager01");
    }

    [Test]
    public async Task ConfiguredPrefix_Wins_AndIgnoresThePool()
    {
        // A pinned prefix means the caller asked for that exact shape; the pool
        // must not leak in even when every pool name is "taken".
        var allTaken = BotNameGenerator.Pool.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await Assert.That(BotNameGenerator.NameFor(0, "Citizen", allTaken.Contains)).IsEqualTo("Citizen01");
    }

    [Test]
    public async Task BlankPrefix_IsTreatedAsUnconfigured()
    {
        var expected = BotNameGenerator.NameFor(0, null);
        await Assert.That(BotNameGenerator.NameFor(0, "")).IsEqualTo(expected);
        await Assert.That(BotNameGenerator.NameFor(0, "   ")).IsEqualTo(expected);
    }

    // ---------------------------------------------------------- collisions

    [Test]
    public async Task NameAlreadyRegistered_IsSteppedPast_NotReused()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BotNameGenerator.NameFor(0, null) };

        var second = BotNameGenerator.NameFor(0, null, taken.Contains);

        await Assert.That(second).IsNotEqualTo(BotNameGenerator.NameFor(0, null));
        await Assert.That(taken.Contains(second)).IsFalse();
    }

    [Test]
    public async Task CollidingNames_AreSteppedPastUntilUnclaimed()
    {
        // Simulate a registry that already holds the first N pool entries — the
        // generator must still hand out names that are not registered.
        var pool = BotNameGenerator.Pool;
        var claimed = pool.Take(10).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = BotNameGenerator.NameFor(0, null, claimed.Contains);

        await Assert.That(claimed.Contains(name)).IsFalse();
        await Assert.That(pool.Contains(name)).IsTrue();
    }

    [Test]
    public async Task EveryNameStillValid_WhenTheWholePoolIsAlreadyTaken()
    {
        // Degenerate saturation: the walk exhausts. The fallback must still be a
        // VALID character name rather than a duplicate or an exception.
        var allTaken = BotNameGenerator.Pool.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = BotNameGenerator.NameFor(0, null, allTaken.Contains);

        await Assert.That(NameRegex.IsMatch(name)).IsTrue();
        await Assert.That(allTaken.Contains(name)).IsFalse();
    }

    [Test]
    public async Task NegativeIndex_StillYieldsAValidDistinctName()
    {
        var name = BotNameGenerator.NameFor(-1, null);
        await Assert.That(NameRegex.IsMatch(name)).IsTrue();
        await Assert.That(name).IsNotEqualTo(BotNameGenerator.NameFor(0, null));
    }

    // ----------------------------------------------------- roster coverage

    [Test]
    public async Task GeneratedName_ForRosterSlot_IsAlwaysPoolMember_AndValid()
    {
        // A roster that fits the pool: every slot gets a distinct pool name, no
        // fallback. This is the regime the presence demo runs in.
        var pool = BotNameGenerator.Pool;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pool.Count; i++)
        {
            var name = BotNameGenerator.NameFor(i, null, taken.Contains);
            await Assert.That(NameRegex.IsMatch(name)).IsTrue();
            await Assert.That(pool.Contains(name)).IsTrue();
            await Assert.That(taken.Add(name)).IsTrue(); // never repeats inside one roster
        }
    }

    [Test]
    public async Task RosterLargerThanPool_StillNeverRepeatsARealName()
    {
        // Saturation regime: past the pool size the walk exhausts and the
        // index-suffixed fallback takes over. Distinctness must still hold.
        var pool = BotNameGenerator.Pool;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pool.Count * 4; i++)
        {
            var name = BotNameGenerator.NameFor(i, null, taken.Contains);
            await Assert.That(NameRegex.IsMatch(name)).IsTrue();
            await Assert.That(taken.Add(name)).IsTrue();
        }
    }
}