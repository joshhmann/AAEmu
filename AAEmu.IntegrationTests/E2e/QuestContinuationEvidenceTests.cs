using Xunit;

namespace AAEmu.IntegrationTests.E2e;

// Supplied validated records only; no server, items, credit or bot decisions.
public class QuestContinuationEvidenceTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private static BotShortRunEvidence.Work Work(int trace, uint quest, string action, double time)
        => new(time, new Guid(trace, 0, 0, new byte[8]), quest, action,
            Start.AddSeconds(time), Start.AddSeconds(time + 0.5),
            "validated-quest-purpose", "quest-v1", $"validated-cycle-{trace}");
    private static BotShortRunEvidence.Work[] Chain()
        => [Work(1, 251, "TurnInQuest", 1), Work(2, 324, "AcceptQuest", 2),
            Work(3, 324, "TurnInQuest", 3), Work(4, 325, "AcceptQuest", 4)];
    private static bool Proved(BotShortRunEvidence.Work[] work, bool productive = true)
        => QuestContinuationEvidence.Proved(work, work.Where(w => w.Action == "TurnInQuest").ToArray(), productive);

    [Fact]
    public void IndependentSuppliedDeliveryCompletesThenFreshGoal_Proved()
        => Assert.True(Proved(Chain()));

    [Fact]
    public void ProductivePredecessorNotProved_NotProved()
        => Assert.False(Proved(Chain(), false));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingRequiredLeg_NotProved(int index)
        => Assert.False(Proved(Chain().Where((_, i) => i != index).ToArray()));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WrongQuestAtRequiredLeg_NotProved(int index)
    {
        var work = Chain();
        work[index] = work[index] with { QuestId = 6198 };
        Assert.False(Proved(work));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(251u)]
    [InlineData(324u)]
    public void UnknownOrPreviouslyCountedNextQuest_NotProved(uint next)
    {
        var work = Chain();
        work[3] = work[3] with { QuestId = next };
        Assert.False(Proved(work));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RequestAtPreviousTerminalIsNotFresh_NotProved(int index)
    {
        var work = Chain();
        work[index] = work[index] with { RequestedAt = work[index - 1].CompletedAt };
        Assert.False(Proved(work));
    }

    [Theory]
    [InlineData("AdvanceQuest")]
    [InlineData("AutoTurnIn")]
    [InlineData("TurnInDoodad")]
    public void DifferentActionDoesNotProveNpcReport_NotProved(string action)
    {
        var work = Chain();
        work[2] = work[2] with { Action = action };
        Assert.False(Proved(work));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CompletionMissingFromOneValidatedList_NotProved(int missingList)
    {
        var work = Chain();
        var completions = work.Where(w => w.Action == "TurnInQuest").ToArray();
        if (missingList == 0) work = work.Where(w => w.TraceId != completions[1].TraceId).ToArray();
        else completions = completions.Take(1).ToArray();
        Assert.False(QuestContinuationEvidence.Proved(work, completions, true));
    }

    [Fact]
    public void CompletionTupleChangedBetweenLists_NotProved()
    {
        var work = Chain();
        var completions = work.Where(w => w.Action == "TurnInQuest").ToArray();
        completions[1] = completions[1] with { Cycle = "another-cycle" };
        Assert.False(QuestContinuationEvidence.Proved(work, completions, true));
    }

    [Fact]
    public void DuplicateTrace_NotProved()
    {
        var work = Chain();
        work[3] = work[3] with { TraceId = work[2].TraceId };
        Assert.False(Proved(work));
    }

    [Fact]
    public void UnknownTrace_NotProved()
    {
        var work = Chain();
        work[2] = work[2] with { TraceId = Guid.Empty };
        Assert.False(Proved(work));
    }

    [Fact]
    public void InvertedRequestInterval_NotProved()
    {
        var work = Chain();
        work[2] = work[2] with { RequestedAt = work[2].CompletedAt.AddSeconds(1) };
        Assert.False(Proved(work));
    }

    [Fact]
    public void ListOrderCannotReplaceTemporalOrder_Proved()
        => Assert.True(Proved(Chain().Reverse().ToArray()));
}
