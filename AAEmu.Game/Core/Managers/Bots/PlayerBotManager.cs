using System.Collections.Concurrent;

using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Player bot registry + lifecycle coordinator (slice #5). See
/// <see cref="IPlayerBotManager"/> for the contract. Concurrency model:
/// ConcurrentDictionary for membership + per-entry lock for state
/// transitions — different bots never contend, and a double
/// activate/deactivate on the same bot is rejected under the entry lock.
/// </summary>
public sealed class PlayerBotManager : IPlayerBotManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotLifecycleService _lifecycle;
    private readonly ConcurrentDictionary<uint, PlayerBotRuntime> _registry = new();

    // Cumulative counters (Interlocked; Volatile.Read for snapshots).
    private long _totalSpawns;
    private long _totalActivations;
    private long _totalDeactivations;
    private long _failedSpawns;
    private long _failedActivations;
    private long _failedDeactivations;

    public PlayerBotManager(IPlayerBotLifecycleService lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public bool Spawn(Character character, string owner)
    {
        ArgumentNullException.ThrowIfNull(character);

        var runtime = new PlayerBotRuntime(character, owner);
        if (!_registry.TryAdd(character.Id, runtime))
        {
            Interlocked.Increment(ref _failedSpawns);
            Logger.Warn("PlayerBot spawn refused: character {CharacterId} already registered", character.Id);
            return false;
        }

        Interlocked.Increment(ref _totalSpawns);
        Logger.Debug("PlayerBot spawned: character {CharacterId} ({CharacterName}) owned by {Owner}",
            character.Id, character.Name, owner);
        return true;
    }

    public bool Activate(uint characterId, object? botContext, string owner)
    {
        if (!_registry.TryGetValue(characterId, out var runtime))
        {
            Interlocked.Increment(ref _failedActivations);
            Logger.Warn("PlayerBot activation refused: character {CharacterId} not registered", characterId);
            return false;
        }

        lock (runtime.Sync)
        {
            if (runtime.State is not (PlayerBotState.Registered or PlayerBotState.Deactivated))
            {
                Interlocked.Increment(ref _failedActivations);
                Logger.Warn("PlayerBot activation refused: character {CharacterId} is {State}",
                    characterId, runtime.State);
                return false;
            }

            try
            {
                if (!_lifecycle.ActivateHeadless(runtime.Character, botContext))
                {
                    Interlocked.Increment(ref _failedActivations);
                    Logger.Warn("PlayerBot activation failed: lifecycle refused character {CharacterId}",
                        characterId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedActivations);
                Logger.Error(ex, "PlayerBot activation failed: lifecycle threw for character {CharacterId}",
                    characterId);
                return false;
            }

            runtime.State = PlayerBotState.Active;
            runtime.Owner = owner;
            runtime.ActivatedAtUtc = DateTime.UtcNow;
            runtime.DeactivatedAtUtc = null;
            runtime.LastDeactivateReason = null;
        }

        Interlocked.Increment(ref _totalActivations);
        Logger.Debug("PlayerBot activated: character {CharacterId} owned by {Owner}", characterId, owner);
        return true;
    }

    public bool Deactivate(uint characterId, string reason)
    {
        if (!_registry.TryGetValue(characterId, out var runtime))
        {
            Interlocked.Increment(ref _failedDeactivations);
            Logger.Warn("PlayerBot deactivation refused: character {CharacterId} not registered", characterId);
            return false;
        }

        lock (runtime.Sync)
        {
            if (runtime.State != PlayerBotState.Active)
            {
                Interlocked.Increment(ref _failedDeactivations);
                Logger.Warn("PlayerBot deactivation refused: character {CharacterId} is {State}",
                    characterId, runtime.State);
                return false;
            }

            try
            {
                if (!_lifecycle.Deactivate(runtime.Character, reason))
                {
                    Interlocked.Increment(ref _failedDeactivations);
                    Logger.Warn("PlayerBot deactivation failed: lifecycle refused character {CharacterId}",
                        characterId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedDeactivations);
                Logger.Error(ex, "PlayerBot deactivation failed: lifecycle threw for character {CharacterId}",
                    characterId);
                return false;
            }

            runtime.State = PlayerBotState.Deactivated;
            runtime.Owner = string.Empty;
            runtime.DeactivatedAtUtc = DateTime.UtcNow;
            runtime.LastDeactivateReason = reason;
        }

        Interlocked.Increment(ref _totalDeactivations);
        Logger.Debug("PlayerBot deactivated: character {CharacterId} ({Reason})", characterId, reason);
        return true;
    }

    public bool TryGet(uint characterId, out PlayerBotRuntime? runtime)
        => _registry.TryGetValue(characterId, out runtime);

    public bool Remove(uint characterId)
    {
        if (!_registry.TryGetValue(characterId, out var runtime))
            return false;

        lock (runtime.Sync)
        {
            // Never let an embodied bot leak out of the manager: deactivate first.
            if (runtime.State == PlayerBotState.Active)
                return false;

            return _registry.TryRemove(characterId, out _);
        }
    }

    public IReadOnlyList<PlayerBotRuntime> GetAll() => [.. _registry.Values];

    public IReadOnlyList<PlayerBotRuntime> GetActive()
        => _registry.Values.Where(r => r.State == PlayerBotState.Active).ToList();

    public int Count => _registry.Count;

    public int ActiveCount => _registry.Values.Count(r => r.State == PlayerBotState.Active);

    public PlayerBotDiagnostics GetDiagnostics() => new(
        Registered: _registry.Values.Count(r => r.State == PlayerBotState.Registered),
        Active: _registry.Values.Count(r => r.State == PlayerBotState.Active),
        Deactivated: _registry.Values.Count(r => r.State == PlayerBotState.Deactivated),
        TotalSpawns: Volatile.Read(ref _totalSpawns),
        TotalActivations: Volatile.Read(ref _totalActivations),
        TotalDeactivations: Volatile.Read(ref _totalDeactivations),
        FailedSpawns: Volatile.Read(ref _failedSpawns),
        FailedActivations: Volatile.Read(ref _failedActivations),
        FailedDeactivations: Volatile.Read(ref _failedDeactivations));
}
