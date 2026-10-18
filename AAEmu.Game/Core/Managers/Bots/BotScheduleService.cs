using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using Microsoft.Extensions.DependencyInjection;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// C1 Schedules v1 (M8/G4) — game-time-driven daily schedule engine for
/// persistent bots. Tick-driven on the game loop (the same
/// <see cref="ITickManager"/> subscription pattern BotChatterService uses),
/// gated OFF by default ("Bots"."EnableSchedules" /
/// AAEMU_BOT_SCHEDULES_ENABLED): while the gate is off the service is a
/// strict no-op — no tick subscription, no metadata writes, zero behavior.
///
/// Per scan, each ACTIVE embodied bot gets its phase resolved from the
/// server GAME clock (TimeManager hours) and its stored/template
/// <see cref="BotDailyAnchors"/> via <see cref="BotScheduleResolver"/>
/// (boundary hysteresis built in). On a phase CHANGE only:
///   - the transition is logged ONCE,
///   - visible behavior is applied through <see cref="IBotScheduleBehavior"/>
///     (Rest/Home → walk home + idle; Work → normal roam; Travel → walk the
///     leg toward that leg's destination), and
///   - anchors + last phase are persisted ADDITIVELY into the existing
///     playerbot_metadata.schedule JSON blob through the B4 write-through
///     path (<see cref="PlayerBotMetadataStore.RecordSchedule"/>) — no new
///     columns, no migration.
///
/// Fail-safe contract: no exception from the enumeration/behavior/persistence
/// ever propagates into gameplay paths — the first failure of an episode is
/// logged once and the tick ends quietly.
///
/// One writer per route (plan T8, audit L1-g): a phase transition is applied
/// only while no elected activity owns the bot's route
/// (<see cref="IBotGoalArbiter.GetRouteOwner"/> — the arbiter's live
/// single-active authority, minus the terminal idle fallback and the schedule
/// layer's own phases). While work is in force the transition is DEFERRED: the
/// route, the phase memory and the persisted lastPhase all stay as they were,
/// so the transition is retried on the next scan and lands when the work ends.
/// </summary>
public sealed class BotScheduleService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotManager _manager;
    private readonly BotScheduleOptions _options;
    private readonly IBotScheduleBehavior _behavior;
    private readonly ITickManager _tickManager;
    private readonly Func<float> _gameHourProvider;
    private readonly Func<uint, PlayerBotMetadata> _metadataProvider;
    private readonly Action<uint, string> _scheduleWriter;

    // Plan T8 ownership authority: the arbiter's live single-active activity.
    // Lazy so the service (and the schedule module that depends on it) can be
    // constructed before the arbiter, which is built from the modules.
    private readonly Lazy<IBotGoalArbiter>? _arbiter;

    // Per-bot phase memory. Mutated only on the execution boundary (the
    // game-loop tick) in production; tests pump RunTick directly.
    private readonly ConcurrentDictionary<uint, BotSchedulePhase> _phaseByBot = [];

    // Per-bot deferred transitions (plan T8): the phase that is waiting and the
    // activity that currently owns the route. Written when a transition is
    // deferred, cleared when that bot's transition next applies or resolves
    // back to the applied phase.
    private readonly ConcurrentDictionary<uint, BotScheduleDeferral> _deferrals = [];

    private int _started;
    private bool _failureLoggedThisEpisode;
    private long _transitionCount;
    private long _deferralCount;

    public BotScheduleService(
        IPlayerBotManager manager,
        BotScheduleOptions? options = null,
        ITickManager? tickManager = null,
        IBotScheduleBehavior? behavior = null,
        Func<float>? gameHourProvider = null,
        Func<uint, PlayerBotMetadata>? metadataProvider = null,
        Action<uint, string>? scheduleWriter = null,
        Lazy<IBotGoalArbiter>? arbiter = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _options = options ?? new BotScheduleOptions();
        _behavior = behavior ?? new RoamRouteScheduleBehavior(
            BotRoamStepExecutorHolder.Instance.Value);
        _tickManager = tickManager ?? TickManager.Instance;
        _gameHourProvider = gameHourProvider ?? DefaultGameHourProvider;
        _metadataProvider = metadataProvider ?? DefaultMetadataProvider;
        _scheduleWriter = scheduleWriter ?? DefaultScheduleWriter;
        _arbiter = arbiter;
    }

    /// <summary>True between a successful gated <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _started) == 1;

    /// <summary>Cumulative phase transitions applied since construction (diagnostics).</summary>
    public long TransitionCount => Volatile.Read(ref _transitionCount);

    /// <summary>
    /// Cumulative phase transitions DEFERRED because an elected activity owned
    /// the bot's route (plan T8, audit L1-g) — diagnostics. Counted once per
    /// deferral episode, not once per scan.
    /// </summary>
    public long DeferralCount => Volatile.Read(ref _deferralCount);

    /// <summary>The resolved options snapshot.</summary>
    public BotScheduleOptions Options => _options;

    /// <summary>
    /// Subscribes the phase scan to the game-loop tick (inline, useAsync:
    /// false — the same seam PlayerBotScheduler/BotChatterService use). A
    /// strict no-op while the feature gate is off; safe to call repeatedly.
    /// </summary>
    /// <returns>True when the service is now running; false when disabled.</returns>
    public bool Start()
    {
        if (!_options.Enabled)
        {
            Logger.Debug("BotScheduleService disabled (Bots.EnableSchedules / AAEMU_BOT_SCHEDULES_ENABLED unset) — inert");
            return false;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
            return true;

        _tickManager.OnTick.Subscribe(ScanTick, _options.ScanInterval, useAsync: false, name: "BotScheduleService.Scan");
        Logger.Info(
            "BotScheduleService started: scan {ScanInterval}s, hysteresis {Hysteresis:F2}h, travel legs {Travel:F2}h",
            _options.ScanInterval.TotalSeconds, _options.HysteresisHours, _options.TravelDurationHours);
        return true;
    }

    /// <summary>Unsubscribes the phase scan. Safe when never started.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        try
        {
            _tickManager.OnTick.UnSubscribe(ScanTick);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "BotScheduleService: failed to unsubscribe scan tick");
        }

        return Task.CompletedTask;
    }

    /// <summary>Test/observability surface: the last resolved phase per bot.</summary>
    internal IReadOnlyDictionary<uint, BotSchedulePhase> SnapshotPhases() => _phaseByBot;

    /// <summary>
    /// Test/observability surface: the phase transitions waiting right now, per
    /// bot — the pending phase and the activity that owns the route (plan T8).
    /// An entry appears when a transition is deferred and disappears when that
    /// bot's transition next applies or resolves back to the applied phase.
    /// </summary>
    internal IReadOnlyDictionary<uint, BotScheduleDeferral> SnapshotDeferrals() => _deferrals;

    /// <summary>Tick subscriber entry (game-loop thread).</summary>
    private void ScanTick(TimeSpan delta) => RunTick();

    /// <summary>
    /// One phase-resolution pass. Internal so tests drive ticks
    /// deterministically without starting the tick subscription.
    /// </summary>
    internal void RunTick()
    {
        if (!_options.Enabled)
            return;

        try
        {
            TickOnce();
            _failureLoggedThisEpisode = false; // clean pass ends the log-once episode
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex, "BotScheduleService scan failed — schedules suppressed for this tick");
        }
    }

    private void TickOnce()
    {
        var hour = _gameHourProvider();

        foreach (var runtime in _manager.GetActive())
        {
            if (runtime.State != PlayerBotState.Active)
                continue;

            var bot = runtime.Character;
            if (bot == null || bot.IsInBattle)
                continue; // never re-route a bot mid-combat

            var metadata = _metadataProvider(runtime.CharacterId);
            var scheduleJson = metadata.Schedule;
            var anchors = ResolveAnchors(scheduleJson);

            // Phase memory: in-process first, then the persisted lastPhase
            // (restart continuity), else unknown → the resolver's first
            // resolution takes the base phase without hysteresis.
            BotSchedulePhase? previous = _phaseByBot.TryGetValue(runtime.CharacterId, out var tracked)
                ? tracked
                : BotSchedulePayload.TryReadLastPhase(scheduleJson, out var persisted)
                    ? persisted
                    : null;

            var next = BotScheduleResolver.Resolve(
                anchors, hour, previous, _options.HysteresisHours, _options.TravelDurationHours);

            if (previous == next)
            {
                _phaseByBot[runtime.CharacterId] = next;
                if (_deferrals.TryRemove(runtime.CharacterId, out var dropped))
                {
                    // The pending phase's window closed before the route was
                    // released, so the retry resolves back to the phase the bot
                    // is already in: the deferral is dropped, not queued (T8).
                    Logger.Info(
                        "BotScheduleService: bot {CharacterId} deferred transition to {Pending} dropped at game hour {Hour:F2} — its window closed before the route was released",
                        runtime.CharacterId, dropped.Pending, hour);
                }

                continue; // steady state — nothing visible happens per tick
            }

            // Plan T8 (audit L1-g): ONE writer per route. While an elected
            // activity owns this bot's route, the schedule defers instead of
            // overwriting it: the route, the phase memory and the persisted
            // lastPhase all stay as they were, so every scan re-resolves the
            // same transition and it lands as soon as the work ends. A deferral
            // is NOT queued: if the pending phase's own window closes first, the
            // next resolve returns the phase the bot is already in and the
            // steady-state branch below drops the deferral (with a log line)
            // instead of applying a phase that is no longer the current one. No
            // arbiter (hermetic rigs, or a host that did not register
            // Lazy<IBotGoalArbiter>) means nobody owns the route — the pre-T8
            // unconditional behavior.
            var owner = _arbiter?.Value.GetRouteOwner(runtime.CharacterId);
            if (owner is not null)
            {
                DeferTransition(runtime.CharacterId, next, owner, hour);
                continue;
            }

            _phaseByBot[runtime.CharacterId] = next;
            _deferrals.TryRemove(runtime.CharacterId, out _);

            Interlocked.Increment(ref _transitionCount);
            Logger.Info(
                "BotScheduleService: bot {CharacterId} phase {Old} -> {New} at game hour {Hour:F2}",
                runtime.CharacterId, previous?.ToString() ?? "(unknown)", next, hour);

            ApplyPhase(runtime, next, anchors, hour, scheduleJson, metadata);

            // Persist anchors + last phase ADDITIVELY through the B4
            // write-through path (the store never throws; a DB failure keeps
            // the row dirty for the SaveManager tick).
            var merged = BotSchedulePayload.WithRuntimeState(scheduleJson, anchors, next);
            _scheduleWriter(runtime.CharacterId, merged);
        }
    }

    private void ApplyPhase(PlayerBotRuntime runtime, BotSchedulePhase phase,
        BotDailyAnchors anchors, float hour, string scheduleJson, PlayerBotMetadata metadata)
    {
        var bot = runtime.Character!;
        var home = ResolveHome(bot, metadata);

        switch (phase)
        {
            case BotSchedulePhase.Work:
                // Normal roam/presence behavior around the work anchor — the
                // pre-schedule behavior, unchanged.
                _behavior.ResumeRoam(runtime, scheduleJson, home);
                break;

            case BotSchedulePhase.Rest:
            case BotSchedulePhase.Home:
                // Walk HOME and idle there (Rest) / social idle near it (Home).
                _behavior.MoveToAnchor(runtime, home);
                break;

            case BotSchedulePhase.Travel:
                // Walk the leg toward THIS leg's destination: morning legs
                // head to the work anchor, evening legs head home.
                var toWork = BotScheduleResolver.IsMorningTravel(anchors, hour, _options.TravelDurationHours);
                _behavior.MoveToAnchor(runtime, toWork ? ResolveWorkCenter(scheduleJson, home) : home);
                break;
        }
    }

    /// <summary>
    /// Records one deferred phase transition (plan T8): the pending phase, the
    /// activity that owns the route, and — once per deferral episode — the
    /// counter and the log line. Nothing is applied and nothing is persisted;
    /// every scan retries the transition for as long as the clock still
    /// resolves that phase. A deferral whose own window closes first is dropped
    /// by the steady-state branch (with a log line), not queued.
    /// </summary>
    private void DeferTransition(uint characterId, BotSchedulePhase pending, string owner, float hour)
    {
        var deferral = new BotScheduleDeferral(pending, owner);
        var newEpisode = !_deferrals.TryGetValue(characterId, out var existing) || existing != deferral;
        _deferrals[characterId] = deferral;

        if (!newEpisode)
            return; // same pending phase, same owner — already counted and logged

        Interlocked.Increment(ref _deferralCount);
        Logger.Info(
            "BotScheduleService: bot {CharacterId} phase transition to {Pending} deferred at game hour {Hour:F2} — route owned by elected work ({Owner})",
            characterId, pending, hour, owner);
    }

    private static BotDailyAnchors ResolveAnchors(string scheduleJson) =>
        BotSchedulePayload.TryReadAnchors(scheduleJson, out var stored) && stored.IsValid
            ? stored
            : BotDailyAnchors.Template; // B4-shape rows (no anchors key) run the template

    private static Vector3 ResolveHome(Character bot, PlayerBotMetadata metadata) =>
        metadata.HasHome
            ? new Vector3(metadata.HomeX, metadata.HomeY, metadata.HomeZ)
            : bot.Transform.World.Position; // v1 fallback: wherever the bot stands acts as its home anchor

    private static Vector3 ResolveWorkCenter(string scheduleJson, Vector3 fallback) =>
        BotSchedulePayload.TryReadRoamDescriptor(scheduleJson, out var center, out _, out _)
            ? center
            : fallback;

    private static float DefaultGameHourProvider()
    {
        var time = TimeManager.Instance.GetTime;
        var normalized = time % 24f;
        return normalized < 0f ? normalized + 24f : normalized;
    }

    private static PlayerBotMetadata DefaultMetadataProvider(uint characterId) =>
        PlayerBotMetadataStore.Instance.GetForRead(characterId);

    private static void DefaultScheduleWriter(uint characterId, string scheduleJson) =>
        PlayerBotMetadataStore.Instance.RecordSchedule(characterId, scheduleJson);

    private void LogFailureOnce(Exception ex, string message)
    {
        if (_failureLoggedThisEpisode)
            return;
        _failureLoggedThisEpisode = true;
        Logger.Error(ex, message);
    }

    /// <summary>
    /// Deferred production executor lookup: DI owns the singleton
    /// <see cref="BotRoamStepExecutor"/>; resolving it lazily keeps the
    /// hermetic test ctor (which passes its own behavior) free of any
    /// singleton touch.
    /// </summary>
    private static class BotRoamStepExecutorHolder
    {
        internal static readonly Lazy<BotRoamStepExecutor> Instance = new(() =>
            SingletonContainer.ServiceProvider?.GetService<BotRoamStepExecutor>()
            ?? throw new InvalidOperationException(
                "BotRoamStepExecutor is not registered — inject an IBotScheduleBehavior explicitly."));
    }
}

/// <summary>
/// One phase transition the schedule is holding back (plan T8, audit L1-g):
/// the phase it wants to apply and the activity that currently owns the bot's
/// route. Surfaced through <see cref="BotScheduleService.SnapshotDeferrals"/>
/// so the ownership decision is observable, not just inferred from a missing
/// transition.
/// </summary>
/// <param name="Pending">The phase the resolver wants and the schedule is deferring.</param>
/// <param name="Owner">The elected activity holding the route (arbiter activity name).</param>
internal sealed record BotScheduleDeferral(BotSchedulePhase Pending, string Owner);
