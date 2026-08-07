namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>Why a flush is happening. Logged + used by tests to assert flush policy.</summary>
public enum BotFlushReason : byte
{
    /// <summary>Periodic batched flush of dirty metadata (no per-step writes).</summary>
    Periodic = 0,

    /// <summary>Bot deactivated — metadata must persist immediately, not batched.</summary>
    Deactivate = 1,

    /// <summary>Fidelity downgrade (PopulationDirector transition) — state persists before the downgrade takes effect.</summary>
    Downgrade = 2,

    /// <summary>Server shutdown — final mandatory flush of everything pending.</summary>
    Shutdown = 3
}

/// <summary>
/// Dirty-flagged bot metadata persistence (ARCHITECTURE_REVIEW deliverable 3 #7 / H4).
///
/// Contract:
///   * Metadata is only ever written from a flush — never per-AI-step. Callers
///     mutate <see cref="BotMetadataRecord"/> values and MarkDirty; the manager
///     decides when bytes hit the DB (periodic batch, or mandatory on
///     deactivate/downgrade/shutdown).
///   * FlushAsync(characterId, ...) is the MANDATORY path: deactivation and
///     downgrade call sites (IPlayerBotManager.Deactivate, PopulationDirector
///     transitions) MUST call it with the matching reason before the bot state
///     changes. This interface is the enforcement point for that rule.
///   * ShutdownAsync() is the final flush; the game process hooks it via
///     BotPersistenceBootstrap so nothing pending is lost on exit.
///
/// No third save lifecycle: gameplay state (character row, inventory, quests)
/// rides the normal Character persistence (SaveManager + leave-save). This
/// manager only ever touches the additive playerbot_* tables.
/// </summary>
public interface IBotPersistence
{
    /// <summary>Gets the metadata record for a bot, creating it with defaults if absent.</summary>
    BotMetadataRecord GetOrCreate(uint characterId, uint accountId = 0);

    /// <summary>Gets the metadata record for a bot, or null when never touched this run.</summary>
    BotMetadataRecord? Get(uint characterId);

    /// <summary>True when a record exists for the bot.</summary>
    bool IsRegistered(uint characterId);

    /// <summary>Marks one or more metadata domains dirty (cheap; no I/O).</summary>
    void MarkDirty(uint characterId, BotMetadataDomain domain);

    /// <summary>True when the bot has any pending dirty domain.</summary>
    bool IsDirty(uint characterId);

    /// <summary>Number of metadata records held in memory.</summary>
    int RegisteredCount { get; }

    /// <summary>Number of records with at least one pending dirty domain.</summary>
    int DirtyRecordCount { get; }

    /// <summary>Completed flush cycles (periodic + mandatory) since start.</summary>
    long TotalFlushCycles { get; }

    /// <summary>
    /// Immediately persists one bot's dirty domains (mandatory path for
    /// deactivate/downgrade). Returns the number of statements executed
    /// (0 when nothing was dirty).
    /// </summary>
    Task<int> FlushAsync(uint characterId, BotFlushReason reason, CancellationToken ct = default);

    /// <summary>
    /// Batched flush of every dirty record in one connection/transaction
    /// (the periodic path). Returns the number of statements executed.
    /// </summary>
    Task<int> FlushAllAsync(BotFlushReason reason, CancellationToken ct = default);

    /// <summary>
    /// Reads a bot's metadata back from the playerbot_* tables (boot-time
    /// restore for future slices). Never touches the in-memory registry.
    /// </summary>
    Task<BotMetadataRecord> RestoreAsync(uint characterId, CancellationToken ct = default);

    /// <summary>
    /// Mandatory final flush: stops the periodic timer, then flushes
    /// everything pending. Idempotent; safe to call multiple times.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);
}
