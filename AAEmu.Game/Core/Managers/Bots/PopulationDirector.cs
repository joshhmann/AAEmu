using System.Collections.Concurrent;

using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// PopulationDirector v1 (slice #9). See <see cref="IPopulationDirector"/>.
///
/// Fidelity state lives here (the director is the ONLY authority). Transitions
/// are single-step along the Dormant→Reduced→Full ladder; downgrades run the
/// spec §11 safety gate via <see cref="IBotTransitionSafetyProbe"/>; wakes and
/// escalations pass pressure + density checks. Pressure sweeps demote
/// Full→Reduced→Dormant as the band rises, always gate-respecting (a bot in
/// combat is never forced down).
///
/// Concurrency: a single state lock guards check-then-set transitions and the
/// pressure sweep; fidelity reads are lock-free via ConcurrentDictionary.
/// </summary>
public sealed class PopulationDirector : IPopulationDirector
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotManager _manager;
    private readonly IPlayerBotScheduler _scheduler;
    private readonly IBotTransitionSafetyProbe _safety;
    private readonly IPressureProbe _pressureProbe;
    private readonly PopulationDirectorOptions _options;
    private readonly Func<Character, uint> _zoneResolver;
    private readonly Func<Character, string?> _activityResolver;

    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<uint, BotFidelity> _fidelity = new();

    // Metrics counters (Interlocked; Volatile.Read for snapshots).
    private long _totalTransitionsApplied;
    private long _totalTransitionsRejected;
    private long _totalWakes;
    private long _totalSleeps;
    private long _totalPressureSweeps;
    private int _pressureBand;

    public PopulationDirector(
        IPlayerBotManager manager,
        IPlayerBotScheduler scheduler,
        IBotTransitionSafetyProbe? safety = null,
        IPressureProbe? pressureProbe = null,
        PopulationDirectorOptions? options = null,
        Func<Character, uint>? zoneResolver = null,
        Func<Character, string?>? activityResolver = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _safety = safety ?? new BotTransitionSafetyProbe();
        _pressureProbe = pressureProbe ?? new SchedulerPressureProbe(scheduler, manager);
        _options = options ?? new PopulationDirectorOptions();
        _zoneResolver = zoneResolver ?? (c => c.Transform?.ZoneId ?? 0);
        _activityResolver = activityResolver ?? (_ => null);
    }

    /// <inheritdoc />
    public BotFidelity GetFidelity(uint characterId)
        => _fidelity.TryGetValue(characterId, out var f) ? f : BotFidelity.Dormant;

    /// <inheritdoc />
    public ServerPressure Pressure => (ServerPressure)Volatile.Read(ref _pressureBand);

    /// <inheritdoc />
    public int EmbodiedCount
        => _fidelity.Values.Count(f => f != BotFidelity.Dormant);

    /// <inheritdoc />
    public int EmbodiedInZone(uint zoneId)
    {
        var count = 0;
        foreach (var (botId, fidelity) in _fidelity)
        {
            if (fidelity == BotFidelity.Dormant)
                continue;
            if (_manager.TryGet(botId, out var runtime) && _zoneResolver(runtime!.Character) == zoneId)
                count++;
        }

        return count;
    }

    /// <inheritdoc />
    public int EmbodiedOnActivity(string activity)
    {
        var count = 0;
        foreach (var (botId, fidelity) in _fidelity)
        {
            if (fidelity == BotFidelity.Dormant)
                continue;
            if (_manager.TryGet(botId, out var runtime) && _activityResolver(runtime!.Character) == activity)
                count++;
        }

        return count;
    }

    /// <inheritdoc />
    public FidelityTransitionResult TrySetFidelity(uint characterId, BotFidelity target, string reason)
    {
        if (!_manager.TryGet(characterId, out var runtime))
            return FidelityTransitionResult.UnknownBot;

        lock (_stateLock)
        {
            var current = GetFidelity(characterId);
            if (current == target)
                return FidelityTransitionResult.NoChange;

            // Ladder: single steps only (Dormant→Reduced→Full).
            if (Math.Abs((int)target - (int)current) > 1)
                return FidelityTransitionResult.NonAdjacentTransition;

            // Downgrade (Full→Reduced / Reduced→Dormant): spec §11 safety gate.
            if (target < current)
            {
                var gate = CheckSafetyGate(runtime!.Character);
                if (gate != FidelityTransitionResult.Applied)
                {
                    Interlocked.Increment(ref _totalTransitionsRejected);
                    return gate;
                }
            }
            else
            {
                // Upgrade paths: pressure + density policy.
                var pressureResult = CheckPressureForTarget(target);
                if (pressureResult != FidelityTransitionResult.Applied)
                {
                    Interlocked.Increment(ref _totalTransitionsRejected);
                    return pressureResult;
                }

                var densityResult = CheckDensity(runtime!.Character, target);
                if (densityResult != FidelityTransitionResult.Applied)
                {
                    Interlocked.Increment(ref _totalTransitionsRejected);
                    return densityResult;
                }
            }

            _fidelity[characterId] = target;
            Interlocked.Increment(ref _totalTransitionsApplied);
            Logger.Debug("PlayerBot fidelity {CharacterId}: {From} → {To} ({Reason})",
                characterId, current, target, reason);
            return FidelityTransitionResult.Applied;
        }
    }

    /// <inheritdoc />
    public ServerPressure RefreshPressure()
    {
        var sample = _pressureProbe.Sample();
        var band = Classify(sample);

        lock (_stateLock)
        {
            Volatile.Write(ref _pressureBand, (int)band);
            Interlocked.Increment(ref _totalPressureSweeps);

            // Pressure sweep: demote Full→Reduced first, then Reduced→Dormant,
            // always gate-respecting (blocked bots stay put — never forced).
            if (band >= _options.DemoteFullAtOrAbove)
            {
                foreach (var (botId, fidelity) in _fidelity.ToArray())
                {
                    if (fidelity != BotFidelity.Full)
                        continue;
                    var runtime = runtimeOf(botId);
                    if (runtime == null)
                        continue; // left the registry — skip
                    var gate = CheckSafetyGate(runtime.Character);
                    if (gate != FidelityTransitionResult.Applied)
                        continue; // in combat etc — keep Full, do not force
                    _fidelity[botId] = BotFidelity.Reduced;
                    Interlocked.Increment(ref _totalTransitionsApplied);
                    Logger.Info("Pressure sweep ({Band}): demoted {CharacterId} Full → Reduced", band, botId);
                }
            }

            if (band >= _options.DemoteReducedAtOrAbove)
            {
                foreach (var (botId, fidelity) in _fidelity.ToArray())
                {
                    if (fidelity != BotFidelity.Reduced)
                        continue;
                    var runtime = runtimeOf(botId);
                    if (runtime == null)
                        continue;
                    var gate = CheckSafetyGate(runtime.Character);
                    if (gate != FidelityTransitionResult.Applied)
                        continue;
                    _fidelity[botId] = BotFidelity.Dormant;
                    Interlocked.Increment(ref _totalTransitionsApplied);
                    Logger.Info("Pressure sweep ({Band}): demoted {CharacterId} Reduced → Dormant", band, botId);
                }
            }
        }

        return band;
    }

    /// <inheritdoc />
    public FidelityTransitionResult Wake(uint characterId, string reason)
    {
        if (!_manager.TryGet(characterId, out var runtime))
            return FidelityTransitionResult.UnknownBot;

        // Dormant bot waking = upgrade to Reduced (full policy path).
        if (GetFidelity(characterId) == BotFidelity.Dormant)
        {
            var upgrade = TrySetFidelity(characterId, BotFidelity.Reduced, reason);
            if (upgrade != FidelityTransitionResult.Applied)
                return upgrade;
        }

        if (!_scheduler.Wake(characterId))
        {
            Interlocked.Increment(ref _totalTransitionsRejected);
            return FidelityTransitionResult.SchedulerRefused;
        }

        Interlocked.Increment(ref _totalWakes);
        return FidelityTransitionResult.Applied;
    }

    /// <inheritdoc />
    public FidelityTransitionResult Sleep(uint characterId, string reason)
    {
        if (!_manager.TryGet(characterId, out var runtime))
            return FidelityTransitionResult.UnknownBot;

        lock (_stateLock)
        {
            var current = GetFidelity(characterId);
            if (current == BotFidelity.Dormant)
                return FidelityTransitionResult.NoChange;

            var gate = CheckSafetyGate(runtime!.Character);
            if (gate != FidelityTransitionResult.Applied)
            {
                Interlocked.Increment(ref _totalTransitionsRejected);
                return gate;
            }

            _fidelity[characterId] = BotFidelity.Dormant;
            Interlocked.Increment(ref _totalTransitionsApplied);
            Interlocked.Increment(ref _totalSleeps);
            Logger.Debug("PlayerBot sleep: {CharacterId} → Dormant ({Reason})", characterId, reason);
            return FidelityTransitionResult.Applied;
        }
    }

    /// <inheritdoc />
    public PopulationDirectorMetrics GetMetrics()
    {
        var dormant = 0;
        var reduced = 0;
        var full = 0;
        foreach (var f in _fidelity.Values)
        {
            switch (f)
            {
                case BotFidelity.Dormant: dormant++; break;
                case BotFidelity.Reduced: reduced++; break;
                case BotFidelity.Full: full++; break;
            }
        }

        return new PopulationDirectorMetrics(
            DormantCount: dormant,
            ReducedCount: reduced,
            FullCount: full,
            Pressure: Pressure,
            TotalTransitionsApplied: Volatile.Read(ref _totalTransitionsApplied),
            TotalTransitionsRejected: Volatile.Read(ref _totalTransitionsRejected),
            TotalWakes: Volatile.Read(ref _totalWakes),
            TotalSleeps: Volatile.Read(ref _totalSleeps),
            TotalPressureSweeps: Volatile.Read(ref _totalPressureSweeps));
    }

    /// <summary>Runs the spec §11 safety gate; returns Applied or the specific block.</summary>
    private FidelityTransitionResult CheckSafetyGate(Character character)
    {
        if (_safety.IsInCombat(character))
            return FidelityTransitionResult.BlockedInCombat;
        if (_safety.IsAttachedToSlave(character))
            return FidelityTransitionResult.BlockedAttachedToSlave;
        if (_safety.IsCarryingTradePack(character))
            return FidelityTransitionResult.BlockedCarryingTradePack;
        if (_safety.IsInTrial(character))
            return FidelityTransitionResult.BlockedInTrial;
        if (_safety.IsGroupedWithHuman(character))
            return FidelityTransitionResult.BlockedGroupedWithHuman;
        if (_safety.IsSaving(character))
            return FidelityTransitionResult.BlockedSaving;
        return FidelityTransitionResult.Applied;
    }

    private FidelityTransitionResult CheckPressureForTarget(BotFidelity target)
    {
        var band = Pressure;
        if (target == BotFidelity.Reduced && band >= _options.RefuseWakeAtOrAbove)
            return FidelityTransitionResult.PressureTooHigh;
        if (target == BotFidelity.Full && band >= _options.RefuseEscalationAtOrAbove)
            return FidelityTransitionResult.PressureTooHigh;
        return FidelityTransitionResult.Applied;
    }

    private FidelityTransitionResult CheckDensity(Character character, BotFidelity target)
    {
        // Density only gates embodiment (Dormant→Reduced). Escalation to Full
        // does not add an embodied bot, so caps do not apply there.
        if (target != BotFidelity.Reduced)
            return FidelityTransitionResult.Applied;

        var zoneId = _zoneResolver(character);
        var zoneCap = _options.ZoneDensityCaps.TryGetValue(zoneId, out var zc)
            ? zc
            : _options.DefaultZoneCap;
        if (zoneCap >= 0 && EmbodiedInZone(zoneId) >= zoneCap)
            return FidelityTransitionResult.DensityCapZoneReached;

        var activity = _activityResolver(character);
        if (activity != null)
        {
            var activityCap = _options.ActivityDensityCaps.TryGetValue(activity, out var ac)
                ? ac
                : _options.DefaultActivityCap;
            if (activityCap >= 0 && EmbodiedOnActivity(activity) >= activityCap)
                return FidelityTransitionResult.DensityCapActivityReached;
        }

        return FidelityTransitionResult.Applied;
    }

    /// <summary>Classifies a sample into a pressure band (highest triggered band wins).</summary>
    private ServerPressure Classify(PressureSample s)
    {
        if (s.WorkerUtilization >= _options.CriticalUtilization ||
            s.DueQueueDepth >= _options.CriticalQueueDepth ||
            s.AverageWakeLatencyMs >= _options.CriticalLatencyMs ||
            (s.TickDurationP95Ms ?? 0d) >= _options.CriticalTickDurationMs ||
            (s.RegionTickDurationMs ?? 0d) >= _options.CriticalRegionTickMs)
            return ServerPressure.Critical;

        if (s.WorkerUtilization >= _options.HighUtilization ||
            s.DueQueueDepth >= _options.HighQueueDepth ||
            s.AverageWakeLatencyMs >= _options.HighLatencyMs ||
            (s.TickDurationP95Ms ?? 0d) >= _options.HighTickDurationMs ||
            (s.RegionTickDurationMs ?? 0d) >= _options.HighRegionTickMs)
            return ServerPressure.High;

        if (s.WorkerUtilization >= _options.PressureUtilization ||
            s.DueQueueDepth >= _options.PressureQueueDepth ||
            s.AverageWakeLatencyMs >= _options.PressureLatencyMs)
            return ServerPressure.Pressure;

        return ServerPressure.Healthy;
    }

    private PlayerBotRuntime? runtimeOf(uint characterId)
        => _manager.TryGet(characterId, out var runtime) ? runtime : null;
}
