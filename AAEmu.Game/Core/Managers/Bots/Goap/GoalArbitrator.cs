#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Deterministic Goal Arbitrator enforcing the strict priority hierarchy:
/// Emergency Survival (Recover)
///   ↓
/// Immediate Threat / Combat (DefendSelf)
///   ↓
/// Existing Committed Primary Intent (e.g. PlantWildFarm)
///   ↓
/// Available Gameplay Routines (PlantWildFarm, HarvestWildFarm, Hunt)
///   ↓
/// Ambient Roaming (Roam)
/// </summary>
public sealed class GoalArbitrator : IGoalArbitrator
{
    public static readonly GoapGoal GoalRecover = new GoapGoal("Recover", 100f)
        .WithCondition(BotWorldState.Recovered);

    public static readonly GoapGoal GoalDefendSelf = new GoapGoal("DefendSelf", 90f)
        .WithCondition(BotWorldState.HasLooted);

    public static readonly GoapGoal GoalPlantWildFarm = new GoapGoal("PlantWildFarm", 60f)
        .WithCondition(BotWorldState.SecretGrovePlanted);

    public static readonly GoapGoal GoalHarvestWildFarm = new GoapGoal("HarvestWildFarm", 50f)
        .WithCondition(BotWorldState.BagFull);

    public static readonly GoapGoal GoalHunt = new GoapGoal("Hunt", 40f)
        .WithCondition(BotWorldState.HasLooted);

    public static readonly GoapGoal GoalRoam = new GoapGoal("Roam", 10f)
        .WithCondition(BotWorldState.NearContinentalRoad);

    public GoapGoal? ArbitrateGoal(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        BotContext context,
        GoapGoal? currentPrimaryGoal)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(context);

        // 1. Emergency Survival (Low HP out of combat)
        if (observedState.Has(BotWorldState.LowHealth) && !observedState.Has(BotWorldState.InCombat))
        {
            return GoalRecover;
        }

        // 2. Immediate Threat / Combat Interrupt
        if (observedState.Has(BotWorldState.InCombat) ||
            (observedState.Has(BotWorldState.HasActiveTarget) && observedState.Has(BotWorldState.TargetIsHostile) && !observedState.Has(BotWorldState.TargetDead)))
        {
            return GoalDefendSelf;
        }

        // 3. Existing Committed Intent (preserve primary goal if still achievable)
        if (currentPrimaryGoal != null && IsGoalAchievable(currentPrimaryGoal, observedState, context))
        {
            // If already satisfied, don't keep returning it
            if (!currentPrimaryGoal.IsSatisfied(observedState))
            {
                return currentPrimaryGoal;
            }
        }

        // 4. Available Gameplay Routines
        // A) Harvest mature grove
        if (context.Memory.HasMatureGroves(context.GetUtcNow()) && observedState.Labor >= 15)
        {
            return GoalHarvestWildFarm;
        }

        // B) Plant wild farm
        if (observedState.Labor >= 10 && (observedState.Gold >= 50 || observedState.Has(BotWorldState.HasTreeSaplings))
            && !context.Memory.TargetWildFarmPoiInvalidated
            && (observedState.Has(BotWorldState.HasTreeSaplings) || context.Memory.GetActionFailures("BuyTreeSaplings") <= context.MaxActionRetries))
        {
            return GoalPlantWildFarm;
        }

        // C) Hunting if hostile target sighted
        if (observedState.Has(BotWorldState.HasActiveTarget))
        {
            return GoalHunt;
        }

        // 5. Ambient Roaming fallback
        return GoalRoam;
    }

    public bool IsGoalAchievable(
        GoapGoal goal,
        in BotWorldState observedState,
        BotContext context)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(context);

        // If destination or POI was invalidated
        if (goal.Name == "PlantWildFarm")
        {
            if (context.Memory.TargetWildFarmPoiInvalidated)
                return false;

            // Check if bot was permanently blocked by merchant purchase failure (after all retries exhausted)
            if (context.Memory.GetActionFailures("BuyTreeSaplings") > context.MaxActionRetries
                && !observedState.Has(BotWorldState.HasTreeSaplings))
            {
                return false;
            }

            // Must have minimum labor or gold to ever finish
            if (observedState.Labor < 10 && !observedState.Has(BotWorldState.HasTreeSaplings))
                return false;

            return true;
        }

        if (goal.Name == "HarvestWildFarm")
        {
            return context.Memory.HasActiveGroves && observedState.Labor >= 15;
        }

        if (goal.Name == "Recover")
        {
            return !observedState.Has(BotWorldState.InCombat);
        }

        if (goal.Name == "DefendSelf")
        {
            return true;
        }

        return true;
    }
}
