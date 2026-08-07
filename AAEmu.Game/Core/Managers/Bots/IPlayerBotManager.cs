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
    /// Tears down an Active bot through the lifecycle service and retains the
    /// record as Deactivated (diagnostics/audit). Refused when not Active.
    /// </summary>
    /// <param name="characterId">Registry key (Character.Id).</param>
    /// <param name="reason">Why the bot is being deactivated (forwarded to the lifecycle service, retained on the record).</param>
    /// <returns>True when the lifecycle service tore the bot down; false otherwise.</returns>
    bool Deactivate(uint characterId, string reason);

    /// <summary>Registry lookup. Returns false when the id is unknown.</summary>
    bool TryGet(uint characterId, out PlayerBotRuntime? runtime);

    /// <summary>
    /// Drops a non-Active record from the registry (deactivate first — an
    /// Active bot is refused so no embodied bot can leak out of the manager).
    /// </summary>
    bool Remove(uint characterId);

    /// <summary>Snapshot of all registry entries.</summary>
    IReadOnlyList<PlayerBotRuntime> GetAll();

    /// <summary>Snapshot of currently embodied (Active) entries — the scheduler's feed.</summary>
    IReadOnlyList<PlayerBotRuntime> GetActive();

    /// <summary>Current registry size (Registered + Active + Deactivated).</summary>
    int Count { get; }

    /// <summary>Current embodied bot count.</summary>
    int ActiveCount { get; }

    /// <summary>Thread-safe diagnostics snapshot (state counts + cumulative counters).</summary>
    PlayerBotDiagnostics GetDiagnostics();
}
