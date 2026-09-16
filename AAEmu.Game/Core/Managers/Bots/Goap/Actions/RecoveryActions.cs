#nullable enable

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers.Bots.Goap.Actions;

/// <summary>
/// Consumes an edible food or drink item from inventory to recover health and mana.
/// </summary>
public sealed class ConsumeFoodAction : GoapActionBase
{
    public uint FoodTemplateId { get; init; }

    public ConsumeFoodAction(uint foodTemplateId = 8219, float baseCost = 1.0f)
        : base("ConsumeFoodItem", baseCost)
    {
        FoodTemplateId = foodTemplateId;

        WithPrecondition(BotWorldState.HasEdibleFood);

        WithEffect(BotWorldState.LowHealth, false);
        WithEffect(BotWorldState.LowMana, false);
    }

    public override bool CheckPreconditions(in BotWorldState currentState)
    {
        if (currentState.Has(BotWorldState.InCombat))
            return false;

        return base.CheckPreconditions(currentState);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.UseItem(FoodTemplateId);
    }
}

/// <summary>
/// Enters sitting posture (Character.Stance = UnitStance.Sit) to activate authentic 2.0x regeneration.
/// </summary>
public sealed class SitRestAction : GoapActionBase
{
    public SitRestAction(float baseCost = 1.5f)
        : base("SitDownToRegen", baseCost)
    {
        WithEffect(BotWorldState.IsSitting);
        WithEffect(BotWorldState.Recovered);
    }

    public override bool CheckPreconditions(in BotWorldState currentState)
    {
        if (currentState.Has(BotWorldState.InCombat) || currentState.Has(BotWorldState.IsSitting))
            return false;

        return true;
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        bot.Character.Stance = UnitStance.Sit;
        return null;
    }
}

/// <summary>
/// Transitions out of sitting posture back to standing once recovery is complete.
/// </summary>
public sealed class StandUpAction : GoapActionBase
{
    public StandUpAction(float baseCost = 0.5f)
        : base("StandUpFromRest", baseCost)
    {
        WithPrecondition(BotWorldState.IsSitting);
        WithPrecondition(BotWorldState.Recovered);

        WithEffect(BotWorldState.IsSitting, false);
        WithEffect(BotWorldState.LowHealth, false);
        WithEffect(BotWorldState.LowMana, false);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        bot.Character.Stance = UnitStance.Stand;
        return null;
    }
}

/// <summary>
/// Drinks a health or mana potion during combat or emergency recovery.
/// </summary>
public sealed class DrinkPotionAction : GoapActionBase
{
    public uint PotionTemplateId { get; init; }

    public DrinkPotionAction(uint potionTemplateId = 1710, float baseCost = 0.8f)
        : base("DrinkEmergencyPotion", baseCost)
    {
        PotionTemplateId = potionTemplateId;

        WithEffect(BotWorldState.LowHealth, false);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.UseItem(PotionTemplateId);
    }
}
