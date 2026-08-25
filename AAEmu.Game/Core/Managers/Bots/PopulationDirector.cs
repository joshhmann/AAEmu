using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

using Microsoft.Extensions.DependencyInjection;

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
    private readonly Func<IReadOnlyList<Character>> _humanSnapshotProvider;
    private readonly ITickManager _tickManager;

    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<uint, BotFidelity> _fidelity = new();

    // Proximity hysteresis (G2-A3): per-bot last-sweep target + how many
    // consecutive sweeps it has held. A transition needs a streak of 2.
    // Mutated only under _stateLock.
    private readonly Dictionary<uint, (BotFidelity Target, int Streak)> _proximityStreak = [];

    // Metrics counters (Interlocked; Volatile.Read for snapshots).
    private long _totalTransitionsApplied;
    private long _totalTransitionsRejected;
    private long _totalWakes;
    private long _totalSleeps;
    private long _totalPressureSweeps;
    private long _totalProximitySweeps;
    private long _totalProximityUpgrades;
    private long _totalProximityDemotions;
    private int _pressureBand;
    private int _started;
    private bool _proximityFailureLoggedThisEpisode;

    public PopulationDirector(
        IPlayerBotManager manager,
        IPlayerBotScheduler scheduler,
        IBotTransitionSafetyProbe? safety = null,
        IPressureProbe? pressureProbe = null,
        PopulationDirectorOptions? options = null,
        Func<Character, uint>? zoneResolver = null,
        Func<Character, string?>? activityResolver = null,
        Func<IReadOnlyList<Character>>? humanSnapshotProvider = null,
        ITickManager? tickManager = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _safety = safety ?? new BotTransitionSafetyProbe();
        _pressureProbe = pressureProbe ?? new SchedulerPressureProbe(scheduler, manager);
        _options = options ?? new PopulationDirectorOptions();
        _zoneResolver = zoneResolver ?? (c => c.Transform?.ZoneId ?? 0);
        _activityResolver = activityResolver ?? (_ => null);
        _humanSnapshotProvider = humanSnapshotProvider ?? DefaultHumanSnapshot;
        _tickManager = tickManager ?? TickManager.Instance;
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
        => ScanEmbodiedInZone(zoneId, int.MaxValue);

    /// <inheritdoc />
    public int EmbodiedOnActivity(string activity)
        => ScanEmbodiedOnActivity(activity, int.MaxValue);

    /// <summary>
    /// Budgeted embodied-bot scan for a zone: breaks early once <paramref name="budget"/>
    /// matching bots are found. The density gate only needs "at or above cap?", so the
    /// saturated case (a wake storm into a capped zone) costs O(budget) instead of O(N)
    /// per wake — the wake-storm path is O(N·cap) overall, not O(N²). Non-dormant
    /// entries are the only ones resolved against the manager registry.
    /// </summary>
    private int ScanEmbodiedInZone(uint zoneId, int budget)
    {
        var count = 0;
        foreach (var (botId, fidelity) in _fidelity)
        {
            if (fidelity == BotFidelity.Dormant)
                continue;
            if (_manager.TryGet(botId, out var runtime) && _zoneResolver(runtime!.Character) == zoneId)
            {
                count++;
                if (count >= budget)
                    break;
            }
        }

        return count;
    }

    /// <summary>Budgeted embodied-bot scan for an activity (see <see cref="ScanEmbodiedInZone"/>).</summary>
    private int ScanEmbodiedOnActivity(string activity, int budget)
    {
        var count = 0;
        foreach (var (botId, fidelity) in _fidelity)
        {
            if (fidelity == BotFidelity.Dormant)
                continue;
            if (_manager.TryGet(botId, out var runtime) && _activityResolver(runtime!.Character) == activity)
            {
                count++;
                if (count >= budget)
                    break;
            }
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
            TotalPressureSweeps: Volatile.Read(ref _totalPressureSweeps),
            TotalProximitySweeps: Volatile.Read(ref _totalProximitySweeps),
            TotalProximityUpgrades: Volatile.Read(ref _totalProximityUpgrades),
            TotalProximityDemotions: Volatile.Read(ref _totalProximityDemotions));
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
        if (zoneCap >= 0 && ScanEmbodiedInZone(zoneId, zoneCap) >= zoneCap)
            return FidelityTransitionResult.DensityCapZoneReached;

        var activity = _activityResolver(character);
        if (activity != null)
        {
            var activityCap = _options.ActivityDensityCaps.TryGetValue(activity, out var ac)
                ? ac
                : _options.DefaultActivityCap;
            if (activityCap >= 0 && ScanEmbodiedOnActivity(activity, activityCap) >= activityCap)
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

    #region Proximity fidelity tiers (G2-A3)

    /// <summary>True between a successful gated <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _started) == 1;

    /// <summary>
    /// Subscribes the proximity sweep to the game-loop tick every
    /// ProximitySweepIntervalMs (inline, useAsync: false — the same seam
    /// PlayerBotScheduler/BotChatterService use). A strict no-op while the
    /// feature gate is off; safe to call repeatedly.
    /// </summary>
    /// <returns>True when the driver is now running; false when disabled.</returns>
    public bool Start()
    {
        if (!_options.EnableProximityFidelity)
        {
            Logger.Debug("PopulationDirector proximity driver disabled (Bots.EnableProximityFidelity / AAEMU_BOT_PROXIMITY_FIDELITY unset) — inert");
            return false;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
            return true;

        _tickManager.OnTick.Subscribe(
            ProximityTick,
            TimeSpan.FromMilliseconds(_options.ProximitySweepIntervalMs),
            useAsync: false,
            name: "PopulationDirector.ProximitySweep");
        Logger.Info(
            "PopulationDirector proximity driver started: full ≤ {Full}m, reduced ≤ {Reduced}m, sweep {Interval}ms",
            _options.FullProximityRadiusM, _options.ReducedProximityRadiusM, _options.ProximitySweepIntervalMs);
        return true;
    }

    /// <summary>Unsubscribes the proximity sweep. Safe when never started.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        try
        {
            _tickManager.OnTick.UnSubscribe(ProximityTick);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "PopulationDirector: failed to unsubscribe proximity sweep tick");
        }

        return Task.CompletedTask;
    }

    private void ProximityTick(TimeSpan delta) => RefreshProximityFidelity();

    /// <summary>
    /// One proximity sweep. Internal so tests drive sweeps deterministically
    /// without starting the tick subscription. Never throws into gameplay paths.
    /// </summary>
    public void RefreshProximityFidelity()
    {
        // Enabled-gate only: production callers reach this through the Start()
        // tick subscription (which itself refuses to arm while disabled), and
        // test rigs pump sweeps directly.
        if (!_options.EnableProximityFidelity)
            return;

        try
        {
            RunProximitySweep();
            _proximityFailureLoggedThisEpisode = false; // clean pass ends the log-once episode
        }
        catch (Exception ex)
        {
            // Fail-safe: never throw into gameplay paths.
            if (!_proximityFailureLoggedThisEpisode)
            {
                _proximityFailureLoggedThisEpisode = true;
                Logger.Error(ex, "PopulationDirector proximity sweep failed — fidelity tiers unchanged for this sweep");
            }
        }
    }

    private void RunProximitySweep()
    {
        Interlocked.Increment(ref _totalProximitySweeps);

        // A3 fix: the pressure demotion policy finally runs — once per sweep.
        RefreshPressure();

        var humans = _humanSnapshotProvider();

        // Pass 1 (lock-free reads): classify each EMBODIED bot's target tier.
        // The manager registry is the source of truth here — a bot that has
        // never been assigned a fidelity still has an implicit tier (Dormant)
        // and must be driven by proximity like any other.
        var decisions = new List<(uint Id, BotFidelity Current, BotFidelity Target)>(_manager.Count);
        foreach (var runtime in _manager.GetAll())
        {
            if (runtime.State != PlayerBotState.Active || runtime.Character?.Transform == null)
                continue; // not embodied or not positioned — skip

            var botId = runtime.CharacterId;
            decisions.Add((botId, GetFidelity(botId), ComputeTargetTier(runtime.Character, humans)));
        }

        // Pass 2 (locked): advance hysteresis streaks, collect transitions due.
        List<(uint Id, BotFidelity Target)>? due = null;
        lock (_stateLock)
        {
            foreach (var (botId, _, target) in decisions)
            {
                if (_proximityStreak.TryGetValue(botId, out var entry) && entry.Target == target)
                    _proximityStreak[botId] = (target, entry.Streak + 1);
                else
                    _proximityStreak[botId] = (target, 1);

                // Hysteresis: the distance condition must hold for TWO
                // consecutive sweeps before a transition is attempted.
                if (_proximityStreak[botId].Streak >= 2)
                    (due ??= []).Add((botId, target));
            }

            // Prune bots that left the registry this sweep.
            if (_proximityStreak.Count > decisions.Count)
            {
                var seen = new HashSet<uint>();
                foreach (var (botId, _, _) in decisions)
                    seen.Add(botId);
                var stale = new List<uint>();
                foreach (var botId in _proximityStreak.Keys)
                    if (!seen.Contains(botId))
                        stale.Add(botId);
                foreach (var botId in stale)
                    _proximityStreak.Remove(botId);
            }
        }

        // Pass 3 (outside the state lock): single-step along the ladder. Every
        // path runs through TrySetFidelity/Wake/Sleep semantics — safety gate,
        // pressure bands and density caps all still apply.
        if (due == null)
            return;

        foreach (var (botId, target) in due)
        {
            var current = GetFidelity(botId);
            if (current == target)
                continue; // already there

            FidelityTransitionResult result;
            var reason = $"proximity-tier-{target.ToString().ToLowerInvariant()}";
            if (target > current)
            {
                result = current == BotFidelity.Dormant
                    ? Wake(botId, reason) // re-arms scheduler stepping
                    : TrySetFidelity(botId, BotFidelity.Full, reason);
                if (result == FidelityTransitionResult.Applied)
                    Interlocked.Increment(ref _totalProximityUpgrades);
            }
            else
            {
                result = current == BotFidelity.Reduced
                    ? Sleep(botId, reason)
                    : TrySetFidelity(botId, BotFidelity.Reduced, reason);
                if (result == FidelityTransitionResult.Applied)
                    Interlocked.Increment(ref _totalProximityDemotions);
            }

            if (result != FidelityTransitionResult.Applied &&
                result != FidelityTransitionResult.NoChange &&
                result != FidelityTransitionResult.UnknownBot)
                Logger.Debug("PlayerBot proximity step refused for {CharacterId}: {Result} ({Target})",
                    botId, result, target);
        }
    }

    /// <summary>
    /// Nearest-human tier for one bot: any human ≤ FullProximityRadiusM → Full;
    /// else ≤ ReducedProximityRadiusM → Reduced; else → Dormant. Squared
    /// Euclidean compare, allocation-free per candidate.
    /// </summary>
    private BotFidelity ComputeTargetTier(Character bot, IReadOnlyList<Character> humans)
    {
        var botWorld = bot.ParentWorld;
        var botPos = bot.Transform.ComputeWorldPosition();

        var fullSq = _options.FullProximityRadiusM * _options.FullProximityRadiusM;
        var reducedSq = _options.ReducedProximityRadiusM * _options.ReducedProximityRadiusM;
        var nearestSq = float.MaxValue;

        foreach (var human in humans)
        {
            if (human == null || human.Id == bot.Id || human.Transform == null)
                continue;
            if (botWorld != null && !ReferenceEquals(human.ParentWorld, botWorld))
                continue; // cross-world distances are meaningless

            var d = Vector3.DistanceSquared(botPos, human.Transform.ComputeWorldPosition());
            if (d < nearestSq)
                nearestSq = d;
        }

        if (nearestSq <= fullSq)
            return BotFidelity.Full;
        if (nearestSq <= reducedSq)
            return BotFidelity.Reduced;
        return BotFidelity.Dormant;
    }

    /// <summary>
    /// Default human source: everyone currently in ANY world who is NOT a
    /// registered bot. Humans only — bots never escalate each other.
    /// </summary>
    private IReadOnlyList<Character> DefaultHumanSnapshot()
    {
        var all = WorldManager.Instance.GetAllCharacters();
        if (all.Count == 0)
            return [];

        List<Character>? humans = null;
        foreach (var character in all)
        {
            if (character == null || _manager.TryGet(character.Id, out _))
                continue; // registered bots are never "humans" for proximity purposes
            humans ??= [];
            humans.Add(character);
        }

        return humans ?? [];
    }

    #endregion

    private PlayerBotRuntime? runtimeOf(uint characterId)
        => _manager.TryGet(characterId, out var runtime) ? runtime : null;
}

/// <summary>
/// Proximity driver bootstrap (G2-A3): arms the director's TickManager-driven
/// proximity sweep when the game server boots with proximity fidelity enabled
/// ("Bots"."EnableProximityFidelity" in Config.Local.json / Config.json, or
/// AAEMU_BOT_PROXIMITY_FIDELITY=1). Follows the BotChatterBootstrap precedent:
/// runs at assembly load, waits for the DI container, then calls
/// <see cref="PopulationDirector.Start"/>. When disabled (the default) both
/// the bootstrap and the driver are strict no-ops: no tick subscription, no
/// sweeps, zero behavior change.
/// </summary>
internal static class PopulationDirectorProximityBootstrap
{
    private static NLog.Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    [ModuleInitializer]
    internal static void Init()
    {
        if (!PopulationDirectorOptions.ReadProximityEnabledFlag())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for DI (the director is registered during Program init).
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                if (SingletonContainer.ServiceProvider == null)
                    return;

                var director = SingletonContainer.ServiceProvider
                    .GetRequiredService<PopulationDirector>();
                if (director.Start())
                    Logger.Info("PopulationDirectorBootstrap: proximity fidelity armed");
            }
            catch (Exception ex)
            {
                // The fidelity driver must never take the server down.
                Logger.Error(ex, "PopulationDirectorProximityBootstrap failed");
            }
        });
    }
}
