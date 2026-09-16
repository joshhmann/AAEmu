#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Planner interface finding lowest-cost action sequence from start state to goal state.
/// </summary>
public interface IGoapPlanner
{
    GoapPlanResult Plan(
        PlayerBotRuntime? bot,
        in BotWorldState startState,
        GoapGoal goal,
        IReadOnlyList<IGoapAction> actions);
}

/// <summary>
/// High-performance A* search planner for Goal-Oriented Action Planning (GOAP).
/// Finds the minimal-cost sequence of actions from a start state to a goal state.
/// Guaranteed real-time performance through compact bitwise states, admissible heuristics,
/// and iteration budgets.
/// </summary>
public sealed class GoapPlanner : IGoapPlanner
{
    public const int DefaultMaxExpansions = 500;
    private readonly int _maxExpansions;

    public GoapPlanner(int maxExpansions = DefaultMaxExpansions)
    {
        _maxExpansions = Math.Max(10, maxExpansions);
    }

    /// <summary>
    /// Searches for the lowest-cost action plan from <paramref name="startState"/> to satisfy <paramref name="goal"/>.
    /// </summary>
    /// <param name="bot">Optional bot runtime context for dynamic cost evaluations.</param>
    /// <param name="startState">Current bot world state.</param>
    /// <param name="goal">Target goal to satisfy.</param>
    /// <param name="actions">Available action library.</param>
    /// <returns>GoapPlanResult containing ordered actions on success, or failure diagnostics.</returns>
    public GoapPlanResult Plan(
        PlayerBotRuntime? bot,
        in BotWorldState startState,
        GoapGoal goal,
        IReadOnlyList<IGoapAction> actions)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(actions);

        // Immediate check: goal already satisfied
        if (goal.IsSatisfied(startState))
            return GoapPlanResult.EmptyGoalAlreadySatisfied();

        if (actions.Count == 0)
            return GoapPlanResult.Failed("Action library is empty.");

        // Estimate min baseline cost across all actions for heuristic weighting
        var minActionCost = 0.5f;
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i].BaseCost > 0f && actions[i].BaseCost < minActionCost)
                minActionCost = actions[i].BaseCost;
        }

        var openSet = new PriorityQueue<PlanNode, float>();
        var bestCostToState = new Dictionary<ulong, float>();

        var startNode = new PlanNode(null, null, startState, 0f, CalculateHeuristic(startState, goal.DesiredState, minActionCost));
        openSet.Enqueue(startNode, startNode.FCost);
        bestCostToState[startState.Flags] = 0f;

        var nodesExpanded = 0;

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            nodesExpanded++;

            if (goal.IsSatisfied(current.State))
            {
                // Found path! Reconstruct sequence from start to goal
                return ReconstructPlan(current, nodesExpanded);
            }

            if (nodesExpanded >= _maxExpansions)
            {
                return GoapPlanResult.Failed(
                    $"Plan search exceeded expansion budget ({_maxExpansions} nodes).",
                    nodesExpanded);
            }

            // Expand neighboring valid actions
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                if (!action.CheckPreconditions(current.State))
                    continue;

                var nextState = action.ApplyEffects(current.State);

                // Skip if applying this action produces no state change (avoids useless loops)
                if (nextState.Equals(current.State))
                    continue;

                var stepCost = Math.Max(0.01f, action.CalculateCost(bot, current.State));
                var tentativeGCost = current.GCost + stepCost;

                // Prune if we've already reached this state configuration with lower or equal cost
                if (bestCostToState.TryGetValue(nextState.Flags, out var recordedCost) && tentativeGCost >= recordedCost)
                    continue;

                bestCostToState[nextState.Flags] = tentativeGCost;

                var hCost = CalculateHeuristic(nextState, goal.DesiredState, minActionCost);
                var neighborNode = new PlanNode(current, action, nextState, tentativeGCost, hCost);
                openSet.Enqueue(neighborNode, neighborNode.FCost);
            }
        }

        return GoapPlanResult.Failed("No valid plan could satisfy the goal.", nodesExpanded);
    }

    /// <summary>
    /// Admissible heuristic: computes Hamming distance of unsatisfied bit flags multiplied by min action cost,
    /// plus normalized labor/gold deficit penalty.
    /// </summary>
    private static float CalculateHeuristic(in BotWorldState state, in BotWorldState desiredState, float minActionCost)
    {
        // Diff bits where desiredState specifies a mask
        var diff = (state.Flags ^ desiredState.Flags) & desiredState.Mask;
        var unsatisfiedBits = BitOperations.PopCount(diff);

        var h = unsatisfiedBits * minActionCost;

        if (desiredState.Labor > state.Labor)
            h += (desiredState.Labor - state.Labor) * 0.01f;

        if (desiredState.Gold > state.Gold)
            h += (desiredState.Gold - state.Gold) * 0.001f;

        return h;
    }

    private static GoapPlanResult ReconstructPlan(PlanNode goalNode, int nodesExpanded)
    {
        var actionsList = new List<IGoapAction>();
        var curr = goalNode;

        while (curr.Action != null)
        {
            actionsList.Add(curr.Action);
            curr = curr.Parent!;
        }

        actionsList.Reverse();
        return new GoapPlanResult(true, actionsList, goalNode.GCost, nodesExpanded);
    }

    private sealed class PlanNode
    {
        public readonly PlanNode? Parent;
        public readonly IGoapAction? Action;
        public readonly BotWorldState State;
        public readonly float GCost;
        public readonly float HCost;
        public float FCost => GCost + HCost;

        public PlanNode(PlanNode? parent, IGoapAction? action, in BotWorldState state, float gCost, float hCost)
        {
            Parent = parent;
            Action = action;
            State = state;
            GCost = gCost;
            HCost = hCost;
        }
    }
}
