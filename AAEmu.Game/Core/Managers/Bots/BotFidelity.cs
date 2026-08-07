namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Bot fidelity tier (spec §11, ARCHITECTURE_REVIEW deliverable 10 slice #9).
/// PopulationDirector is the ONLY authority that assigns these.
///
/// The ladder is Dormant → Reduced → Full; transitions are single-step only
/// (adjacent states). Downgrades (Full→Reduced, Reduced→Dormant) are gated by
/// <see cref="IBotTransitionSafetyProbe"/> — never downgrade a bot that is in
/// combat / attached to a Slave / carrying a trade pack / in trial / grouped
/// with a human / saving (spec §11 list).
///
/// Abstract (spec's fourth tier) is DEFERRED per the review — no code path
/// supports it today; Dormant↔Reduced is sufficient for M6.0.
/// </summary>
public enum BotFidelity : byte
{
    /// <summary>Character row known to the registry, not embodied in any world. Near-zero runtime cost.</summary>
    Dormant = 0,

    /// <summary>Embodied, tick-light presence (the scheduled/DB-driven substate).</summary>
    Reduced = 1,

    /// <summary>Embodied with full presence (full activity opt-in — H1 semantics).</summary>
    Full = 2
}
