#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Result of a GOAP planning pass.
/// </summary>
public sealed record GoapPlanResult(
    bool Success,
    IReadOnlyList<IGoapAction> Actions,
    float TotalCost,
    int NodesExpanded,
    string? FailureReason = null)
{
    public static GoapPlanResult Failed(string reason, int nodesExpanded = 0)
        => new(false, Array.Empty<IGoapAction>(), 0f, nodesExpanded, reason);

    public static GoapPlanResult EmptyGoalAlreadySatisfied()
        => new(true, Array.Empty<IGoapAction>(), 0f, 0, "Goal is already satisfied in the current world state.");
}
