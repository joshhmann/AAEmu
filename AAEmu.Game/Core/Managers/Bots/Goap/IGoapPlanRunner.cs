#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Per-bot runtime manager owning the active GOAP plan, action step lifecycle,
/// interrupt monitoring, effect verification, and telemetry.
/// </summary>
public interface IGoapPlanRunner
{
    GoapGoal? PrimaryGoal { get; }
    GoapGoal? InterruptGoal { get; }
    GoapGoal? ActiveGoal { get; }
    GoapPlanResult? ActivePlan { get; }
    int CurrentActionIndex { get; }
    IGoapAction? CurrentAction { get; }
    GoapActionStatus CurrentActionStatus { get; }
    ActorRequest? CurrentActorRequest { get; }

    /// <summary>
    /// Executes one tick of plan monitoring, state projection, action execution, and effect verification.
    /// Returns true if an action or request is currently live and active.
    /// </summary>
    bool Tick(PlayerBotRuntime bot, IGameplayActor actor, BotContext context);

    /// <summary>
    /// Forces immediate invalidation of the active plan due to world state changes or interrupts.
    /// </summary>
    void InvalidatePlan(string reason, BotContext context);

    /// <summary>
    /// Explicitly updates the bot's primary intent.
    /// </summary>
    void SetPrimaryGoal(GoapGoal? goal, BotContext context);
}
