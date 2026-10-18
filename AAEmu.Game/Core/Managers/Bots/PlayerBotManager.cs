using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers.World;
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
    private readonly ICharacterLifecycleService _ordinaryLifecycle;
    private readonly ConcurrentDictionary<uint, PlayerBotRuntime> _registry = new();

    // Cumulative counters (Interlocked; Volatile.Read for snapshots).
    private long _totalSpawns;
    private long _totalActivations;
    private long _totalDeactivations;
    private long _failedSpawns;
    private long _failedActivations;
    private long _failedDeactivations;

    // Activation generation sequence (process-wide monotonic counter; gen 0
    // reserved never-valid). Issued inside the entry lock on success only.
    private long _generationSequence;
    public PlayerBotManager(IPlayerBotLifecycleService lifecycle, ICharacterLifecycleService? ordinaryLifecycle = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _ordinaryLifecycle = ordinaryLifecycle ?? CharacterLifecycleService.Instance;
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
            if (!_registry.TryGetValue(characterId, out var current) || !ReferenceEquals(current, runtime)
                || runtime.State is not (PlayerBotState.Registered or PlayerBotState.Deactivated))
            {
                Interlocked.Increment(ref _failedActivations);
                Logger.Warn("PlayerBot activation refused: character {CharacterId} is {State}",
                    characterId, runtime.State);
                return false;
            }

            if (runtime.Character.Connection != null)
            {
                Interlocked.Increment(ref _failedActivations);
                Logger.Warn("PlayerBot activation refused: character {CharacterId} holds a live connection",
                    characterId);
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
            runtime.Generation = (ulong)Interlocked.Increment(ref _generationSequence);
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

        // Native steps are synchronous and own this same entry lock. A self-leave
        // cannot save before its caller finishes producing ordinary effects.
        if (ExecutionBoundary.IsInsideBotStep && Monitor.IsEntered(runtime.Sync))
        {
            Interlocked.Increment(ref _failedDeactivations);
            return false;
        }

        Character character;
        Character? worldCharacter;
        ulong generation;
        uint objectId;
        lock (runtime.Sync)
        {
            if (!_registry.TryGetValue(characterId, out var current) || !ReferenceEquals(current, runtime)
                || runtime.State != PlayerBotState.Active || runtime.Character.Id != characterId)
            {
                Interlocked.Increment(ref _failedDeactivations);
                Logger.Warn("PlayerBot deactivation refused: character {CharacterId} is {State}",
                    characterId, runtime.State);
                return false;
            }

            character = runtime.Character;
            worldCharacter = WorldManager.Instance.GetCharacterById(characterId);
            if (character.Connection != null
                || (worldCharacter != null && !ReferenceEquals(worldCharacter, character)))
            {
                Interlocked.Increment(ref _failedDeactivations);
                return false;
            }

            generation = runtime.Generation;
            objectId = character.ObjId;
            runtime.State = PlayerBotState.Deactivating;
            runtime.LastDeactivateReason = reason;
        }

        // Do not hold Sync while waiting: the existing game-loop drain needs it
        // to reject earlier queued steps and retire this exact embodiment.
        if (!TryRetireNativeWork(runtime, generation, reason))
            return false;

        lock (runtime.Sync)
        {
            if (!TryCompleteNativeLeaveUnderLock(runtime, character, generation, worldCharacter, objectId, reason))
                return false;

            runtime.State = PlayerBotState.Deactivated;
            runtime.Owner = string.Empty;
            runtime.DeactivatedAtUtc = DateTime.UtcNow;
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
            if (runtime.State is not (PlayerBotState.Registered or PlayerBotState.Deactivated))
                return false;

            return ((ICollection<KeyValuePair<uint, PlayerBotRuntime>>)_registry)
                .Remove(new KeyValuePair<uint, PlayerBotRuntime>(characterId, runtime));
        }
    }

    /// <inheritdoc />
    public bool LeaveHeadlessCharacter(Character expected, CharacterLifecycleReason reason, out bool savedThisCall,
        Func<bool>? releaseCachedSession = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        savedThisCall = false;
        if (expected.Connection != null)
            return false;
        var characterId = expected.Id;
        var ordinaryReservation = false;
        if (!_registry.TryGetValue(characterId, out var runtime))
        {
            runtime = new PlayerBotRuntime(expected, "headless-leave") { State = PlayerBotState.Deactivating };
            if (!_registry.TryAdd(characterId, runtime))
                return false; // A losing exact-owner claim never falls back to physical leave.
            ordinaryReservation = true;
        }
        if (ExecutionBoundary.IsInsideBotStep && Monitor.IsEntered(runtime.Sync))
            return false;

        Character? worldCharacter;
        ulong generation;
        uint objectId;
        bool activeDeactivation;
        var reasonText = reason.ToString();
        lock (runtime.Sync)
        {
            if (!_registry.TryGetValue(characterId, out var current) || !ReferenceEquals(current, runtime)
                || !ReferenceEquals(runtime.Character, expected) || expected.Id != characterId)
                return false;
            if (expected.Connection != null)
            {
                if (ordinaryReservation)
                    ((ICollection<KeyValuePair<uint, PlayerBotRuntime>>)_registry)
                        .Remove(new KeyValuePair<uint, PlayerBotRuntime>(characterId, runtime));
                return false;
            }
            if (!ordinaryReservation && runtime.State is not (PlayerBotState.Active or PlayerBotState.Deactivated))
                return false;
            worldCharacter = WorldManager.Instance.GetCharacterById(characterId);
            if (worldCharacter != null && !ReferenceEquals(worldCharacter, expected))
            {
                if (ordinaryReservation)
                    ((ICollection<KeyValuePair<uint, PlayerBotRuntime>>)_registry)
                        .Remove(new KeyValuePair<uint, PlayerBotRuntime>(characterId, runtime));
                return false;
            }

            activeDeactivation = !ordinaryReservation && runtime.State == PlayerBotState.Active;
            runtime.LastDeactivateReason = reasonText;
            if (!ordinaryReservation && !activeDeactivation && worldCharacter != null)
                return FailDeactivationUnderLock(runtime, "Deactivated owner still has a live embodiment");

            runtime.State = PlayerBotState.Deactivating;
            if (!activeDeactivation && worldCharacter == null)
            {
                // Already absent: cache release must not cause a second save or
                // claim that native retirement was independently observed.
                return ReleaseHeadlessClaimUnderLock(runtime, releaseCachedSession);
            }

            // A never-activated ordinary reservation retains generation 0.
            // Retirement must refuse any cached Character/generation mismatch.
            generation = runtime.Generation;
            objectId = expected.ObjId;
        }

        if (!TryRetireNativeWork(runtime, generation, reasonText))
            return false;

        lock (runtime.Sync)
        {
            if (!TryCompleteNativeLeaveUnderLock(runtime, expected, generation, worldCharacter, objectId,
                    reasonText, reason))
                return false;

            savedThisCall = true;
            if (activeDeactivation)
                Interlocked.Increment(ref _totalDeactivations);
            return ReleaseHeadlessClaimUnderLock(runtime, releaseCachedSession);
        }
    }

    private bool TryRetireNativeWork(PlayerBotRuntime runtime, ulong generation, string reason)
    {
        try
        {
            if (_lifecycle.TryRetireNativeWork(runtime, generation, reason))
                return true;
        }
        catch (Exception ex)
        {
            lock (runtime.Sync)
                return FailDeactivationUnderLock(runtime, "Native retirement threw", ex);
        }

        lock (runtime.Sync)
            return FailDeactivationUnderLock(runtime, "Native retirement was refused or abandoned");
    }

    private bool TryCompleteNativeLeaveUnderLock(PlayerBotRuntime runtime, Character expected, ulong generation,
        Character? expectedWorldCharacter, uint expectedObjectId, string reason,
        CharacterLifecycleReason? ordinaryReason = null)
    {
        try
        {
            if (!_registry.TryGetValue(runtime.CharacterId, out var current) || !ReferenceEquals(current, runtime)
                || !ReferenceEquals(runtime.Character, expected) || expected.Id != runtime.CharacterId
                || runtime.Generation != generation || runtime.State != PlayerBotState.Deactivating
                || expected.Connection != null || expected.ObjId != expectedObjectId
                || !ReferenceEquals(WorldManager.Instance.GetCharacterById(runtime.CharacterId), expectedWorldCharacter))
                return FailDeactivationUnderLock(runtime, "Exact retirement owner changed before physical leave");

            // The supplied ordinary reason must not be translated to Logout by
            // the bot adapter. Both paths arrive here only after retirement.
            if (ordinaryReason.HasValue)
                _ordinaryLifecycle.Deactivate(expected, ordinaryReason.Value);
            else if (!_lifecycle.Deactivate(expected, reason))
                return FailDeactivationUnderLock(runtime, "Physical lifecycle refused");
            return true;
        }
        catch (Exception ex)
        {
            return FailDeactivationUnderLock(runtime, "Physical leave failed", ex);
        }
    }

    private bool ReleaseHeadlessClaimUnderLock(PlayerBotRuntime runtime, Func<bool>? releaseCachedSession)
    {
        try
        {
            // Admission stays closed through exact cache release and removal.
            if (releaseCachedSession != null && !releaseCachedSession())
                return FailDeactivationUnderLock(runtime, "Exact cache release refused");
            if (!((ICollection<KeyValuePair<uint, PlayerBotRuntime>>)_registry)
                .Remove(new KeyValuePair<uint, PlayerBotRuntime>(runtime.CharacterId, runtime)))
                return FailDeactivationUnderLock(runtime, "Exact registry claim removal refused");
            return true;
        }
        catch (Exception ex)
        {
            return FailDeactivationUnderLock(runtime, "Exact headless cache release failed", ex);
        }
    }

    private bool FailDeactivationUnderLock(PlayerBotRuntime runtime, string failure, Exception? exception = null)
    {
        runtime.State = PlayerBotState.DeactivationFailed;
        Interlocked.Increment(ref _failedDeactivations);
        if (exception != null)
            Logger.Error(exception, "PlayerBot deactivation failed: character {CharacterId} ({Failure})",
                runtime.CharacterId, failure);
        else
            Logger.Warn("PlayerBot deactivation failed: character {CharacterId} ({Failure})",
                runtime.CharacterId, failure);
        return false;
    }

    public IReadOnlyList<PlayerBotRuntime> GetAll() => [.. _registry.Values];

    public IReadOnlyList<PlayerBotRuntime> GetActive()
        => _registry.Values.Where(r => r.State == PlayerBotState.Active).ToList();

    public int Count => _registry.Count;

    public int ActiveCount => _registry.Values.Count(r => r.State == PlayerBotState.Active);

    public PlayerBotDiagnostics GetDiagnostics()
    {
        var registered = 0;
        var active = 0;
        var deactivated = 0;
        var deactivating = 0;
        var deactivationFailed = 0;
        // One weakly consistent enumeration, not an atomic global transaction.
        foreach (var entry in _registry)
        {
            switch (entry.Value.State)
            {
                case PlayerBotState.Registered: registered++; break;
                case PlayerBotState.Active: active++; break;
                case PlayerBotState.Deactivated: deactivated++; break;
                case PlayerBotState.Deactivating: deactivating++; break;
                case PlayerBotState.DeactivationFailed: deactivationFailed++; break;
            }
        }
        return new PlayerBotDiagnostics(
            Registered: registered,
            Active: active,
            Deactivated: deactivated,
            TotalSpawns: Volatile.Read(ref _totalSpawns),
            TotalActivations: Volatile.Read(ref _totalActivations),
            TotalDeactivations: Volatile.Read(ref _totalDeactivations),
            FailedSpawns: Volatile.Read(ref _failedSpawns),
            FailedActivations: Volatile.Read(ref _failedActivations),
            FailedDeactivations: Volatile.Read(ref _failedDeactivations))
        {
            Deactivating = deactivating,
            DeactivationFailed = deactivationFailed
        };
    }

}
