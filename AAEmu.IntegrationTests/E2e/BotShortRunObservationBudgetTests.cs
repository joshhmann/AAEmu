using Xunit;

namespace AAEmu.IntegrationTests.E2e;

public class BotShortRunObservationBudgetTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(295, 0)]
    [InlineData(895, 700)]
    public void CompleteOperationFitsBeforeBothCaps(double elapsed, double progress)
        => Assert.Null(BotShortRunObservationBudget.StopReason(elapsed, progress, null));

    [Theory]
    [InlineData(899.999, 700, "MAXIMUM_WINDOW_OPERATION_MARGIN")]
    [InlineData(895.001, 700, "MAXIMUM_WINDOW_OPERATION_MARGIN")]
    [InlineData(299.999, 0, "NO_PROVED_PROGRESS_OPERATION_MARGIN")]
    [InlineData(895.001, 600, "MAXIMUM_WINDOW_OPERATION_MARGIN")]
    public void TinyPositiveRoomRefusesNewOperation(double elapsed, double progress, string reason)
        => Assert.Equal(reason, BotShortRunObservationBudget.StopReason(elapsed, progress, null));

    [Fact]
    public void DelayMustResampleRatherThanReuseEarlierAdmission()
    {
        Assert.Null(BotShortRunObservationBudget.StopReason(294.5, 0, null));
        Assert.Equal("NO_PROVED_PROGRESS_OPERATION_MARGIN",
            BotShortRunObservationBudget.StopReason(295.5, 0, null));
    }

    [Fact]
    public void AuthoritativeProgressMovesOnlyNoProgressCap()
    {
        Assert.NotNull(BotShortRunObservationBudget.StopReason(299, 0, null));
        Assert.Null(BotShortRunObservationBudget.StopReason(299, 298, null));
        Assert.Equal("MAXIMUM_WINDOW_OPERATION_MARGIN",
            BotShortRunObservationBudget.StopReason(899, 898, null));
    }

    [Theory]
    [InlineData("EVIDENCE_FAULT")]
    [InlineData("FIRST_FAULT_FAILED_END")]
    [InlineData("PRODUCTIVE_QUEST_COMPLETE")]
    [InlineData("MAXIMUM_WINDOW")]
    [InlineData("NO_PROVED_PROGRESS")]
    public void AdmittedFaultOrExistingVerdictIsNeverReclassified(string reason)
        => Assert.Equal(reason, BotShortRunObservationBudget.StopReason(900.1, 0, reason));
}
