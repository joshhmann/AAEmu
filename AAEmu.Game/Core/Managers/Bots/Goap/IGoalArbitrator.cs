#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Lightweight, deterministic goal arbitrator determining bot intent and interrupt priorities.
/// </summary>
public interface IGoalArbitrator
{
    /// <summary>
    /// Evaluates current bot state and context to determine if an interrupt goal is required,
    /// or arbitrates a new primary goal when no active intent exists.
    /// </summary>
    GoapGoal? ArbitrateGoal(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        BotContext context,
        GoapGoal? currentPrimaryGoal);

    /// <summary>
    /// Checks whether an existing committed primary goal remains valid and achievable from the current state.
    /// </summary>
    bool IsGoalAchievable(
        GoapGoal goal,
        in BotWorldState observedState,
        BotContext context);
}
