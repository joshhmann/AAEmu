#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// A discrete, atomic action in the GOAP action space.
/// Defines preconditions required to execute, effects produced upon completion,
/// and dynamic execution cost.
/// </summary>
public interface IGoapAction
{
    /// <summary>Human-readable identifier of the action.</summary>
    string Name { get; }

    /// <summary>Static baseline traversal cost for planning.</summary>
    float BaseCost { get; }

    /// <summary>World state conditions required before this action can begin.</summary>
    BotWorldState Preconditions { get; }

    /// <summary>World state alterations produced when this action succeeds.</summary>
    BotWorldState Effects { get; }

    /// <summary>
    /// Computes the dynamic execution cost given current bot context and world state.
    /// Incorporates spatial distance, risk, resource expenditures, etc.
    /// </summary>
    float CalculateCost(PlayerBotRuntime? bot, in BotWorldState currentState);

    /// <summary>
    /// Evaluates whether this action can be executed from the given world state.
    /// </summary>
    bool CheckPreconditions(in BotWorldState currentState);

    /// <summary>
    /// Applies the effects of this action onto the given world state.
    /// </summary>
    BotWorldState ApplyEffects(in BotWorldState currentState);

    /// <summary>
    /// Translates this planned action into a concrete, validated request for the server's
    /// IGameplayActor execution boundary (optional/null if simulated or compound).
    /// </summary>
    ActorRequest? CreateActorRequest(PlayerBotRuntime bot);
}
