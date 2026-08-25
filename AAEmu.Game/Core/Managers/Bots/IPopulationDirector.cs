using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// PopulationDirector v1 (ARCHITECTURE_REVIEW deliverable 10 slice #9,
/// spec §11/§14/§15). The ONLY fidelity authority for player bots:
///
/// - <b>Fidelity assignment</b> (spec §11): Dormant/Reduced/Full per bot,
///   single-step ladder transitions, Abstract deferred.
/// - <b>Transition safety gate</b> (spec §11, verbatim): never downgrade while
///   in combat / attached to a Slave / carrying a trade pack / in trial /
///   grouped with a human / saving — evaluated via
///   <see cref="IBotTransitionSafetyProbe"/>.
/// - <b>Adaptive pressure control</b> (spec §14): classifies an
///   <see cref="IPressureProbe"/> sample into HEALTHY/PRESSURE/HIGH/CRITICAL
///   and drives wake/sleep: refuses new wakes and escalations at high bands,
///   and demotes Full→Reduced→Dormant as pressure rises (gate-respecting).
/// - <b>Density caps</b> (spec §15): per-zone and per-activity embodied-bot
///   limits consulted before any wake/upgrade.
///
/// The director does NOT own embodiment — it consumes the
/// <see cref="IPlayerBotManager"/> registry (resolve runtime at transition
/// time) and <see cref="IPlayerBotScheduler"/> for wake decisions. Scheduler
/// internals stay untouched (separate card).
/// </summary>
public interface IPopulationDirector
{
    /// <summary>Current fidelity of a registered bot (Dormant when unknown/unregistered).</summary>
    BotFidelity GetFidelity(uint characterId);

    /// <summary>
    /// Attempts a single-step fidelity transition for a registered bot.
    /// Downgrades run the safety gate; wake/upgrade targets additionally pass
    /// density and pressure checks. Non-adjacent jumps are refused (ladder).
    /// </summary>
    FidelityTransitionResult TrySetFidelity(uint characterId, BotFidelity target, string reason);

    /// <summary>
    /// Refreshes the pressure sample and applies the pressure policy sweep
    /// (demotions at high bands). Returns the newly classified band.
    /// </summary>
    ServerPressure RefreshPressure();

    /// <summary>Last classified pressure band (initial: Healthy).</summary>
    ServerPressure Pressure { get; }

    /// <summary>
    /// Requests an immediate wake for a bot. Waking a Dormant bot is an
    /// upgrade to Reduced, which honors pressure bands (RefuseWakeAtOrAbove)
    /// and density caps; an already-embodied bot (Reduced/Full) is re-woken
    /// directly (a step nudge — it is already counted against pressure/density).
    /// </summary>
    FidelityTransitionResult Wake(uint characterId, string reason);

    /// <summary>Requests a sleep (immediate downgrade to Dormant, gate-respecting).</summary>
    FidelityTransitionResult Sleep(uint characterId, string reason);

    /// <summary>Current embodied count (Reduced + Full).</summary>
    int EmbodiedCount { get; }

    /// <summary>Embodied bots in the given zone (Reduced + Full).</summary>
    int EmbodiedInZone(uint zoneId);

    /// <summary>Embodied bots on the given activity (Reduced + Full).</summary>
    int EmbodiedOnActivity(string activity);

    /// <summary>Thread-safe metrics snapshot (fidelity counts, pressure, transition counters).</summary>
    PopulationDirectorMetrics GetMetrics();

    /// <summary>
    /// Runs ONE proximity-fidelity sweep (G2-A3): classifies each registered
    /// bot's target tier from the nearest HUMAN (never another bot), then moves
    /// it along the ladder one step per sweep toward that target — Dormant→
    /// Reduced via Wake, Reduced→Full via TrySetFidelity, demotions via
    /// TrySetFidelity/Sleep (gate-respecting). A target must hold for TWO
    /// consecutive sweeps before a transition is attempted (hysteresis). Also
    /// runs <see cref="RefreshPressure"/> once per sweep. Inert while
    /// EnableProximityFidelity is off.
    /// </summary>
    void RefreshProximityFidelity();
}
