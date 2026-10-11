using Xunit;

namespace AAEmu.IntegrationTests.E2e;

// Contract-only supplied validated Work records. No parser, gameplay, server,
// database, assigned bot goal or injected gameplay credit is exercised here.
public class ProductiveQuestEvidenceTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    private static BotShortRunEvidence.Work Work(int trace, uint quest, string action,
        double requested, double completed)
    {
        return new BotShortRunEvidence.Work(completed, new Guid(trace, 0, 0, new byte[8]), quest,
            action, Start.AddSeconds(requested), Start.AddSeconds(completed),
            "validated-quest-purpose", "quest-v1", $"validated-cycle-{trace}");
    }

    private static BotShortRunEvidence.Work[] Chain(string completion = "TurnInQuest")
    {
        return [Work(1, 251, "AcceptQuest", 1, 2), Work(2, 251, "Loot", 3, 4),
            Work(3, 251, completion, 5, 6), Work(4, 601, "AcceptQuest", 7, 8),
            Work(5, 251, "Loot", 4.1, 4.2), Work(6, 251, "Loot", 4.3, 4.4)];
    }

    private static bool Proved(BotShortRunEvidence.Work[] work, bool purpose = true,
        uint recoveryQuest = 251)
    {
        return ProductiveQuestEvidence.Proved(work,
            work.Where(w => w.Action is "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn").ToArray(),
            purpose, recoveryQuest);
    }

    [Theory]
    [InlineData("TurnInQuest")]
    [InlineData("TurnInDoodad")]
    [InlineData("AutoTurnIn")]
    public void SameProductiveQuestCompletesThenDistinctQuestAccepted_Proved(string completion)
    {
        Assert.True(Proved(Chain(completion)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialRequiredLootFollowedByCompletion_NotProved(int gains)
    {
        var remaining = gains;
        var work = Chain().Where(w => w.Action != "Loot" || remaining-- > 0).ToArray();
        Assert.False(Proved(work));
    }

    [Fact]
    public void ListOrderDoesNotReplaceRequestTime_Proved()
    {
        Assert.True(Proved(Chain().Reverse().ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingRequiredRequest_NotProved(int missing)
    {
        Assert.False(Proved(Chain().Where((_, index) => index != missing).ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EffectBelongsToAnotherQuest_NotProved(int changed)
    {
        var work = Chain();
        work[changed] = work[changed] with { QuestId = 6198 };
        Assert.False(Proved(work));
    }

    [Theory]
    [InlineData(251u)]
    [InlineData(0u)]
    public void FollowUpIsSameQuestOrUnknown_NotProved(uint next)
    {
        var work = Chain();
        work[3] = work[3] with { QuestId = next };
        Assert.False(Proved(work));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    public void NextRequestStartsAtOrBeforePriorCompletion_NotProved(int index, double requested)
    {
        var work = Chain();
        work[index] = work[index] with { RequestedAt = Start.AddSeconds(requested) };
        Assert.False(Proved(work));
    }

    [Fact]
    public void ExistingPurposeProofMissing_NotProved()
    {
        Assert.False(Proved(Chain(), purpose: false));
    }

    [Fact]
    public void ExistingRecoveryBelongsToAnotherRoot_NotProved()
    {
        Assert.False(Proved(Chain(), recoveryQuest: 6198));
    }

    [Fact]
    public void DuplicateRequestIdentity_NotProved()
    {
        var work = Chain();
        work[3] = work[3] with { TraceId = work[2].TraceId };
        Assert.False(Proved(work));
    }

    [Fact]
    public void RequestCompletesBeforeItStarts_NotProved()
    {
        var work = Chain();
        work[3] = work[3] with { CompletedAt = Start.AddSeconds(6) };
        Assert.False(Proved(work));
    }

    [Fact]
    public void DuplicateCompletionIdentity_NotProved()
    {
        var work = Chain();
        Assert.False(ProductiveQuestEvidence.Proved(work, [work[2], work[2]], true, 251));
    }

    [Fact]
    public void CompletionAbsentFromValidatedCompletionList_NotProved()
    {
        Assert.False(ProductiveQuestEvidence.Proved(Chain(), [], true, 251));
    }

    [Fact]
    public void CompletionRecordReinterpretedFromAnotherRequest_NotProved()
    {
        var work = Chain();
        var changed = work[2] with { TraceId = new Guid(99, 0, 0, new byte[8]) };
        Assert.False(ProductiveQuestEvidence.Proved(work, [changed], true, 251));
    }
}
