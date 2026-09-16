#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Reusable base class for GOAP actions with fluent configuration.
/// </summary>
public class GoapActionBase : IGoapAction
{
    public string Name { get; }
    public float BaseCost { get; set; }
    public BotWorldState Preconditions { get; protected set; }
    public BotWorldState Effects { get; protected set; }

    public GoapActionBase(string name, float baseCost = 1.0f)
    {
        Name = name;
        BaseCost = baseCost;
        Preconditions = BotWorldState.Empty;
        Effects = BotWorldState.Empty;
    }

    public GoapActionBase WithPrecondition(ulong flag, bool value = true)
    {
        Preconditions = Preconditions.With(flag, value);
        return this;
    }

    public GoapActionBase WithLaborPrecondition(ushort labor)
    {
        Preconditions = Preconditions.WithLabor(labor);
        return this;
    }

    public GoapActionBase WithGoldPrecondition(uint gold)
    {
        Preconditions = Preconditions.WithGold(gold);
        return this;
    }

    public GoapActionBase WithEffect(ulong flag, bool value = true)
    {
        Effects = Effects.With(flag, value);
        return this;
    }

    public GoapActionBase WithLaborCost(ushort labor)
    {
        Effects = Effects.WithLabor(labor);
        return this;
    }

    public GoapActionBase WithGoldCost(uint gold)
    {
        Effects = Effects.WithGold(gold);
        return this;
    }

    public virtual float CalculateCost(PlayerBotRuntime? bot, in BotWorldState currentState) => BaseCost;

    public virtual bool CheckPreconditions(in BotWorldState currentState) => currentState.Satisfies(Preconditions);

    public virtual BotWorldState ApplyEffects(in BotWorldState currentState) => currentState.Apply(Effects);

    public virtual ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null) => null;

    public virtual GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        // 1. Observed world state genuinely satisfies the effects -> SUCCESS
        if (observedState.Satisfies(Effects))
            return GoapActionStatus.Succeeded;

        // 2. Actor request reached failure/rejected terminal state -> FAILED
        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        // 3. Otherwise still in-flight or waiting for world propagation
        return GoapActionStatus.Running;
    }

    public override string ToString() => $"Action[{Name}, Cost={BaseCost:F1}]";
}
