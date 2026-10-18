using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Player bot registry + lifecycle coordinator (slice #5 of the PlayerBot
/// scale review — ARCHITECTURE_REVIEW deliverable 10, spec §3).
///
/// Responsibilities (spec §3): registry, spawn/activate/deactivate, lookup,
/// runtime ownership, diagnostics. NOT the scheduler (due-time execution is
/// IPlayerBotScheduler) and NOT the fidelity authority (PopulationDirector).
///
/// Bots are ordinary Character records (AGENTS.md #9): the manager keys its
/// registry by Character.Id and delegates embodiment/teardown to
/// <see cref="IPlayerBotLifecycleService"/> so activation goes through the
/// same path humans use (slice #3 extraction).
/// </summary>
public interface IPlayerBotManager
{
    /// <summary>
    /// Registers a bot character with the manager (state: Registered).
    /// The character must NOT already be registered — duplicates are refused.
    /// </summary>
    /// <param name="character">Ordinary character record for the bot.</param>
    /// <param name="owner">Subsystem claiming runtime ownership (e.g. "population-director", "scheduler").</param>
    /// <returns>True when registered; false when the id is already known.</returns>
    bool Spawn(Character character, string owner);

    /// <summary>
    /// Embodies a registered (or previously deactivated) bot through the
    /// lifecycle service. Refused when the bot is already Active or unknown.
    /// </summary>
    /// <param name="characterId">Registry key (Character.Id).</param>
    /// <param name="botContext">Opaque context forwarded to the lifecycle service (may be null).</param>
    /// <param name="owner">Subsystem claiming runtime ownership for the active state.</param>
    /// <returns>True when the lifecycle service embodied the bot; false otherwise.</returns>
    bool Activate(uint characterId, object? botContext, string owner);

    /// <summary>
    /// Closes admission for an Active bot, drains the admitted step, and retires
    /// its exact native work before physical leave/save. The boundary handoff
    /// does not hold the entry lock; exact ownership is revalidated before save.
    /// Success retains Deactivated for audit. Retirement/leave failure retains
    /// DeactivationFailed, never reopens admission or retries. Refused when not Active.
    /// </summary>
    /// <param name="characterId">Registry key (Character.Id).</param>
    /// <param name="reason">Why the bot is being deactivated (forwarded to the lifecycle service, retained on the record).</param>
    /// <returns>True when the lifecycle service tore the bot down; false otherwise.</returns>
    bool Deactivate(uint characterId, string reason);

    /// <summary>
    /// Closes admission and retires native work for the exact headless Character
    /// before ordinary physical leave with the supplied reason. It releases the
    /// entry lock only for retirement, then revalidates owner/Character/generation
    /// and connection/world identity before save and exact optional cache release.
    /// Missing owners are reserved as Deactivating (generation 0), never activated;
    /// a mismatched cached embodiment refuses retirement. Already absent-world
    /// cache-only leave performs no second save and is not native-retirement proof.
    /// Failure retains a non-admissible owner; connected humans and foreign
    /// Characters are refused. savedThisCall reports a normal physical-service
    /// return, not independently verified DB durability.
    /// </summary>
    bool LeaveHeadlessCharacter(Character expected, CharacterLifecycleReason reason, out bool savedThisCall,
        Func<bool>? releaseCachedSession = null);

    /// <summary>Registry lookup. Returns false when the id is unknown.</summary>
    bool TryGet(uint characterId, out PlayerBotRuntime? runtime);

    /// <summary>
    /// Drops only a Registered or Deactivated record (deactivate first).
    /// Active, Deactivating and DeactivationFailed claims are refused.
    /// </summary>
    bool Remove(uint characterId);

    /// <summary>Snapshot of all registry entries.</summary>
    IReadOnlyList<PlayerBotRuntime> GetAll();

    /// <summary>Snapshot of currently embodied (Active) entries — the scheduler's feed.</summary>
    IReadOnlyList<PlayerBotRuntime> GetActive();

    /// <summary>Current registry size, including in-progress and failed quiesced owners.</summary>
    int Count { get; }

    /// <summary>Current embodied bot count.</summary>
    int ActiveCount { get; }

    /// <summary>Thread-safe diagnostics snapshot (state counts + cumulative counters).</summary>
    PlayerBotDiagnostics GetDiagnostics();
}
