namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Outcome of a fidelity transition attempt (<see cref="IPopulationDirector"/>,
/// spec §11). Rejections are specific so the rig (and operators) can tell
/// exactly which safety-gate condition or policy blocked the transition.
/// </summary>
public enum FidelityTransitionResult
{
    /// <summary>The transition was applied.</summary>
    Applied,

    /// <summary>The bot is already at the requested fidelity.</summary>
    NoChange,

    /// <summary>The character id is not known to the bot registry.</summary>
    UnknownBot,

    /// <summary>Non-adjacent jump refused (ladder is Dormant→Reduced→Full one step at a time).</summary>
    NonAdjacentTransition,

    /// <summary>Downgrade blocked: bot is in combat.</summary>
    BlockedInCombat,

    /// <summary>Downgrade blocked: bot is attached to a Slave.</summary>
    BlockedAttachedToSlave,

    /// <summary>Downgrade blocked: bot is carrying a trade pack.</summary>
    BlockedCarryingTradePack,

    /// <summary>Downgrade blocked: bot is in trial.</summary>
    BlockedInTrial,

    /// <summary>Downgrade blocked: bot is grouped with a human.</summary>
    BlockedGroupedWithHuman,

    /// <summary>Downgrade blocked: bot is mid-save.</summary>
    BlockedSaving,

    /// <summary>Wake/upgrade refused: density cap for the zone was reached.</summary>
    DensityCapZoneReached,

    /// <summary>Wake/upgrade refused: density cap for the activity was reached.</summary>
    DensityCapActivityReached,

    /// <summary>Wake/upgrade refused: server pressure band too high.</summary>
    PressureTooHigh,

    /// <summary>The scheduler refused to accept the wake (e.g. not started).</summary>
    SchedulerRefused
}
