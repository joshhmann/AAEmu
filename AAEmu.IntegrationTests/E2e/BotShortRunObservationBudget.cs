namespace AAEmu.IntegrationTests.E2e;

/// <summary>Admit a complete existing operation budget before either observation cap.</summary>
internal static class BotShortRunObservationBudget
{
    internal const double OperationSeconds = 5;

    internal static string? StopReason(double elapsedSeconds, double lastProgressSeconds, string? existingReason)
    {
        // Actual evidence faults, success and expired hard caps keep their verdict.
        if (existingReason != null) return existingReason;
        var maximumRemaining = BotShortRunEvidence.MaximumSeconds - elapsedSeconds;
        var progressRemaining = BotShortRunEvidence.NoProgressSeconds - (elapsedSeconds - lastProgressSeconds);
        if (Math.Min(maximumRemaining, progressRemaining) >= OperationSeconds) return null;
        return maximumRemaining <= progressRemaining
            ? "MAXIMUM_WINDOW_OPERATION_MARGIN" : "NO_PROVED_PROGRESS_OPERATION_MARGIN";
    }
}
