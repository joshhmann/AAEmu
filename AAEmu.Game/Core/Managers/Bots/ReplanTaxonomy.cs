namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Event-driven replanning taxonomy (autonomy Wave C): the meaningful
/// events that should trigger reconsideration, mapped onto the existing
/// PlayerBotScheduler wake primitives (Wake/WakeAt/WakeAfter) with bounded
/// debounce so replanning never hot-loops.
///
/// Today replanning is wake-driven only (every scheduler wake re-arbitrates);
/// this taxonomy names the events a future planner subscribes to, the wake
/// each maps to, and the debounce that keeps it bounded. No behavior change —
/// the mapping documents the contract the scheduler already honors.
/// </summary>
public static class ReplanTaxonomy
{
    /// <summary>Replan trigger kinds.</summary>
    public enum TriggerKind : byte
    {
        GoalCompleted = 0,
        GoalImpossible = 1,
        QuestCompleted = 2,
        LevelGained = 3,
        InventoryFull = 4,
        CropsMature = 5,
        BotDied = 6,
        CapabilityUnlocked = 7,
        PlanRepeatedlyFails = 8,
        PressureChanged = 9,
        SchedulePhaseChanged = 10,
    }

    /// <summary>How a trigger maps onto scheduler wakes.</summary>
    public sealed record TriggerMapping(
        TriggerKind Trigger,
        string WakeCall,
        TimeSpan Debounce,
        string Reason);

    /// <summary>Canonical trigger → wake mapping with debounce.</summary>
    public static IReadOnlyList<TriggerMapping> Mappings() =>
    [
        new(TriggerKind.GoalCompleted, nameof(PlayerBotScheduler.Wake), TimeSpan.Zero, "goal done — replan now"),
        new(TriggerKind.GoalImpossible, nameof(PlayerBotScheduler.WakeAfter), TimeSpan.FromSeconds(30), "impossible — back off before retry"),
        new(TriggerKind.QuestCompleted, nameof(PlayerBotScheduler.Wake), TimeSpan.Zero, "quest done — pursue next"),
        new(TriggerKind.LevelGained, nameof(PlayerBotScheduler.Wake), TimeSpan.Zero, "new offers may unlock"),
        new(TriggerKind.InventoryFull, nameof(PlayerBotScheduler.Wake), TimeSpan.FromSeconds(5), "sell/deposit before more work"),
        new(TriggerKind.CropsMature, nameof(PlayerBotScheduler.Wake), TimeSpan.Zero, "harvest window open"),
        new(TriggerKind.BotDied, nameof(PlayerBotScheduler.WakeAfter), TimeSpan.FromSeconds(10), "death recovery then replan"),
        new(TriggerKind.CapabilityUnlocked, nameof(PlayerBotScheduler.Wake), TimeSpan.Zero, "new actions available"),
        new(TriggerKind.PlanRepeatedlyFails, nameof(PlayerBotScheduler.WakeAfter), TimeSpan.FromMinutes(1), "repeated failure — cool down"),
        new(TriggerKind.PressureChanged, nameof(PlayerBotScheduler.Wake), TimeSpan.FromSeconds(15), "world budget shifted"),
        new(TriggerKind.SchedulePhaseChanged, nameof(PlayerBotScheduler.Wake), TimeSpan.Zero, "phase behavior may differ"),
    ];
}
