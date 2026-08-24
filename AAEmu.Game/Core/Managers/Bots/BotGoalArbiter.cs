using System.Collections.Concurrent;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>What one <see cref="IBotGoalArbiter.Arbitrate"/> pass decided.</summary>
public enum BotArbitrationOutcome : byte
{
    /// <summary>The arbiter has no modules — inert by design (zero-regression default).</summary>
    None = 0,

    /// <summary>A module won with a NEW activity — its Activate side effects ran.</summary>
    Activated = 1,

    /// <summary>The winning activity is unchanged from the previous wake — steady state, no side effects.</summary>
    Unchanged = 2,

    /// <summary>No module could activate — the world is left exactly as it was.</summary>
    NoCandidate = 3
}

/// <summary>Result of one arbitration pass.</summary>
public sealed record BotArbitration(BotArbitrationOutcome Outcome, BotActivity? Activity = null, string? WhyNot = null)
{
    public static BotArbitration None { get; } = new(BotArbitrationOutcome.None);
    public static BotArbitration Unchanged(BotActivity activity) => new(BotArbitrationOutcome.Unchanged, activity);
}

/// <summary>
/// G3-B3 goal arbitration seam: collects the registered
/// <see cref="IBotActivityModule"/>s and picks the ONE active activity per
/// bot per wake. Deterministic — highest priority wins, ties break by
/// registration order; no randomness, no LLM anything.
///
/// Semantics:
///  - Zero modules → strictly inert (<see cref="BotArbitrationOutcome.None"/>),
///    no state is touched. This is the fail-closed default for deployments
///    that register nothing.
///  - The ACTIVE ACTIVITY NAME (not the module) is the arbiter's unit of
///    change: a schedule module moving "schedule.work" → "schedule.rest"
///    counts as a transition even though the module itself is unchanged.
///  - Single-active enforcement: at most one entry per bot in
///    <see cref="_activeActivity"/>; every activation atomically replaces it.
///  - Transitions are logged ONCE (Info, old → new + winning module);
///    steady-state wakes log nothing.
///
/// Concurrency: production arbitration runs on the single execution
/// boundary (the game-loop thread, via the scheduler's marshal drain), but
/// the arbiter stays safe under concurrent registration anyway (lock around
/// the module list; concurrent dictionary for per-bot memory).
/// </summary>
public interface IBotGoalArbiter
{
    /// <summary>Registers a module (re-sorts by priority desc, stable). Call at startup.</summary>
    void Register(IBotActivityModule module);

    /// <summary>Registered module count (0 = the arbiter is inert).</summary>
    int ModuleCount { get; }

    /// <summary>
    /// One arbitration pass for the bot: pick the winner, enforce
    /// single-active, activate on change only.
    /// </summary>
    BotArbitration Arbitrate(PlayerBotRuntime bot, float gameHour);

    /// <summary>The currently active activity name for a bot (null = none).</summary>
    string? GetActiveActivity(uint characterId);

    /// <summary>Drops a bot's active-activity memory (deactivation cleanup) — the world is not touched.</summary>
    void Forget(uint characterId);

    /// <summary>Cumulative activity transitions since construction (diagnostics).</summary>
    long TransitionCount { get; }

    /// <summary>Cumulative arbitration passes since construction (diagnostics).</summary>
    long ArbitrationCount { get; }
}

public sealed class BotGoalArbiter : IBotGoalArbiter
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly object _modulesLock = new();
    private List<IBotActivityModule> _modules;

    // Per-bot single-active memory: characterId → activity name.
    private readonly ConcurrentDictionary<uint, string> _activeActivity = [];

    private long _transitions;
    private long _arbitrations;

    public BotGoalArbiter(IEnumerable<IBotActivityModule>? modules = null)
    {
        _modules = modules?.ToList() ?? [];
        SortModules();
    }

    /// <inheritdoc />
    public int ModuleCount
    {
        get
        {
            lock (_modulesLock)
                return _modules.Count;
        }
    }

    /// <inheritdoc />
    public long TransitionCount => Volatile.Read(ref _transitions);

    /// <inheritdoc />
    public long ArbitrationCount => Volatile.Read(ref _arbitrations);

    /// <inheritdoc />
    public void Register(IBotActivityModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        lock (_modulesLock)
        {
            _modules.Add(module);
            SortModules();
        }
    }

    /// <inheritdoc />
    public BotArbitration Arbitrate(PlayerBotRuntime bot, float gameHour)
    {
        ArgumentNullException.ThrowIfNull(bot);

        List<IBotActivityModule> snapshot;
        lock (_modulesLock)
        {
            // Inert fast-path: no modules, no per-wake snapshot allocation
            // (this runs on EVERY scheduler wake — the steady-state cost of a
            // deployment that registers nothing must be zero).
            if (_modules.Count == 0)
            {
                Interlocked.Increment(ref _arbitrations);
                return BotArbitration.None;
            }

            snapshot = [.. _modules];
        }

        Interlocked.Increment(ref _arbitrations);

        var activeName = _activeActivity.TryGetValue(bot.CharacterId, out var current) ? current : null;
        var context = new BotActivityContext
        {
            Bot = bot,
            GameHour = gameHour,
            ActiveActivity = activeName,
        };

        foreach (var module in snapshot)
        {
            BotActivityDecision decision;
            try
            {
                decision = module.CanActivate(context);
            }
            catch (Exception ex)
            {
                // A broken module must never take gameplay down — skip it.
                Logger.Error(ex, "BotGoalArbiter: module {Module} CanActivate failed for bot {CharacterId} — skipped",
                    module.Name, bot.CharacterId);
                continue;
            }

            if (!decision.CanActivate)
                continue;

            var activityName = decision.ActivityName;
            if (string.IsNullOrEmpty(activityName))
            {
                Logger.Warn("BotGoalArbiter: module {Module} allowed activation without an activity name — skipped",
                    module.Name);
                continue;
            }

            if (activityName == activeName)
                return BotArbitration.Unchanged(new BotActivity(activityName, module.Name));

            // Transition: replace the single active activity, apply once, log once.
            _activeActivity[bot.CharacterId] = activityName;
            Interlocked.Increment(ref _transitions);

            BotActivity activity;
            try
            {
                activity = module.Activate(context);
            }
            catch (Exception ex)
            {
                // Side effects never landed — restore the previous activity so
                // a later wake can retry cleanly, then surface the failure.
                if (activeName == null)
                    _activeActivity.TryRemove(bot.CharacterId, out _);
                else
                    _activeActivity[bot.CharacterId] = activeName;
                Interlocked.Decrement(ref _transitions);
                Logger.Error(ex, "BotGoalArbiter: module {Module} Activate failed for bot {CharacterId}",
                    module.Name, bot.CharacterId);
                throw;
            }

            Logger.Info("BotGoalArbiter: bot {CharacterId} activity {Old} -> {New} (module {Module})",
                bot.CharacterId, activeName ?? "(none)", activity.Name, module.Name);
            return new BotArbitration(BotArbitrationOutcome.Activated, activity);
        }

        // Nobody can act: drop the stale memory so the next successful
        // candidate activates cleanly. The WORLD is left untouched.
        if (activeName != null)
        {
            _activeActivity.TryRemove(bot.CharacterId, out _);
            Interlocked.Increment(ref _transitions);
            Logger.Info("BotGoalArbiter: bot {CharacterId} activity {Old} -> (none) — no module could activate",
                bot.CharacterId, activeName);
        }

        return new BotArbitration(BotArbitrationOutcome.NoCandidate);
    }

    /// <inheritdoc />
    public string? GetActiveActivity(uint characterId) =>
        _activeActivity.TryGetValue(characterId, out var name) ? name : null;

    /// <inheritdoc />
    public void Forget(uint characterId) => _activeActivity.TryRemove(characterId, out _);

    private void SortModules()
    {
        // Stable OrderByDescending: equal priorities keep registration order.
        _modules = _modules.OrderByDescending(static m => m.Priority).ToList();
    }
}
