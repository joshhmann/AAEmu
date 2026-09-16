#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// A high-level goal in the GOAP architecture.
/// Encapsulates the desired target world state and dynamic utility evaluation.
/// </summary>
public class GoapGoal
{
    public string Name { get; }
    public float BasePriority { get; set; }
    public BotWorldState DesiredState { get; protected set; }

    public GoapGoal(string name, float basePriority = 10f)
    {
        Name = name;
        BasePriority = basePriority;
        DesiredState = BotWorldState.Empty;
    }

    public GoapGoal WithCondition(ulong flag, bool value = true)
    {
        DesiredState = DesiredState.With(flag, value);
        return this;
    }

    public GoapGoal WithLabor(ushort labor)
    {
        DesiredState = DesiredState.WithLabor(labor);
        return this;
    }

    public GoapGoal WithGold(uint gold)
    {
        DesiredState = DesiredState.WithGold(gold);
        return this;
    }

    /// <summary>
    /// Computes dynamic priority/utility based on current bot context.
    /// Higher values take precedence in goal selection.
    /// </summary>
    public virtual float CalculatePriority(PlayerBotRuntime? bot, in BotWorldState currentState) => BasePriority;

    /// <summary>
    /// Checks if this goal is currently satisfied in the given world state.
    /// </summary>
    public virtual bool IsSatisfied(in BotWorldState state) => state.Satisfies(DesiredState);

    public override string ToString() => $"Goal[{Name}, Priority={BasePriority:F1}]";
}
