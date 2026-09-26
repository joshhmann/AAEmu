using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests.Director;

using QuestAcceptorType = AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The offering-RANK hook, at the surface a consumer observes it through
/// (<see cref="QuestBehavior.AcceptRank"/> plus the candidate bound): band is
/// the primary key (byte-identical to the frozen rule), the reward and
/// personality hooks ride the selector's own second key so they can only break
/// band ties, and the bound drops the worst offers after ranking.
/// </summary>
public class QuestBrainRankTests
{
    private static QuestOffering Offering(uint questId, byte level)
        => new(questId, level, QuestAcceptorType.Npc, 3512);

    private static QuestFixtureRow Row(uint questId, uint rewardItem)
        => new(questId, 0, 0, 0, 0, 0, 0, rewardItem);

    /// <summary>
    /// Band is primary: a lower-level offering inside the band outranks a
    /// higher-level one, and the band term is exactly the frozen
    /// <c>AcceptPriority + (BandMax - Level)</c>.
    /// </summary>
    [Test]
    public async Task PriorityIsTheFrozenBandTerm()
    {
        var opts = new QuestDecisionScenario.QuestOptions { BandMin = 1, BandMax = 9 };

        var (lowPriority, _) = QuestBehavior.AcceptRank(opts, Offering(4438, 4), Row(4438, 0));
        var (highPriority, _) = QuestBehavior.AcceptRank(opts, Offering(4415, 7), Row(4415, 0));

        await Assert.That(lowPriority).IsEqualTo(opts.AcceptPriority + 5);
        await Assert.That(highPriority).IsEqualTo(opts.AcceptPriority + 2);
        await Assert.That(lowPriority).IsGreaterThan(highPriority);

        // An out-of-band-high offer (should never be proposed) clamps at the
        // floor instead of going negative.
        var (clamped, _) = QuestBehavior.AcceptRank(opts, Offering(1, 20), Row(1, 0));
        await Assert.That(clamped).IsEqualTo(opts.AcceptPriority);
    }

    /// <summary>
    /// The reward hook breaks a BAND TIE only: two equal-level offers rank by
    /// reward (the rewarding one first) with the default policy, and the
    /// personality weight is neutral at its default (0 unless the offering
    /// carries a reward).
    /// </summary>
    [Test]
    public async Task RewardHookBreaksBandTies_PersonalityIsUniformByDefault()
    {
        var opts = new QuestDecisionScenario.QuestOptions { BandMax = 9 };

        var (_, rewarding) = QuestBehavior.AcceptRank(opts, Offering(4438, 7), Row(4438, 15_596));
        var (_, plain) = QuestBehavior.AcceptRank(opts, Offering(4415, 7), Row(4415, 0));
        await Assert.That(rewarding).IsGreaterThan(plain);
        await Assert.That(plain).IsEqualTo(0);

        // With the reward hook off, both are band-equal and weight-neutral: the
        // reward hook is the ONLY thing that ordered them.
        var noHook = opts with { PreferRewardingOffers = false };
        var (_, rewardingOff) = QuestBehavior.AcceptRank(noHook, Offering(4438, 7), Row(4438, 15_596));
        await Assert.That(rewardingOff).IsEqualTo(0);
    }

    /// <summary>
    /// The personality hook adds to the same second key and is clamped to the
    /// contract bound, so a personality layer can bias choice but can never
    /// exceed the selector's own clamp.
    /// </summary>
    [Test]
    public async Task PersonalityHookRidesTheWeightAndClamps()
    {
        var opts = new QuestDecisionScenario.QuestOptions { BandMax = 9, AcceptPersonalityWeight = 5 };

        var (_, weight) = QuestBehavior.AcceptRank(opts, Offering(4438, 7), Row(4438, 0));
        await Assert.That(weight).IsEqualTo(5);

        var over = opts with { AcceptPersonalityWeight = 10_000 };
        var (_, clamped) = QuestBehavior.AcceptRank(over, Offering(4438, 7), Row(4438, 15_596));
        await Assert.That(clamped).IsEqualTo(BotDecisionProposal.MaxPersonalityWeight);
    }

    /// <summary>
    /// Neither hook can lift a lower-band offer above a higher-band one: the
    /// priority comparison dominates the weight pair for every combination.
    /// </summary>
    [Test]
    public async Task HooksNeverOutrankTheBandTerm()
    {
        var opts = new QuestDecisionScenario.QuestOptions
        {
            BandMax = 9,
            AcceptPersonalityWeight = BotDecisionProposal.MaxPersonalityWeight
        };

        // Worst-case lifting: the higher-level offer is maximally hooked, the
        // lower-level one carries none.
        var (lowerPriority, _) = QuestBehavior.AcceptRank(opts, Offering(4438, 4), Row(4438, 0));
        var (higherPriority, hookedWeight) = QuestBehavior.AcceptRank(opts, Offering(4415, 8), Row(4415, 15_596));

        await Assert.That(lowerPriority).IsGreaterThan(higherPriority);
        await Assert.That(hookedWeight).IsGreaterThan(0); // the hooks DID apply…
        await Assert.That(lowerPriority).IsGreaterThan(higherPriority); // …and band still won
    }
}
