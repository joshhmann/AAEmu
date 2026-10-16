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
/// <param name="WhyNot">
/// The top refusal reason seen while scanning — the first denial that carried a
/// reason, i.e. the highest-priority one. Null when nothing refused or no
/// refusal explained itself.
/// </param>
/// <param name="Candidates">
/// Allowances in the DECIDING priority band (0 when no band yielded). Band-scoped,
/// not the registered-module count, so a trace row says how many options the
/// winner actually beat.
/// </param>
/// <param name="Rejections">Refusals counted while scanning up to and including the deciding band.</param>
public sealed record BotArbitration(BotArbitrationOutcome Outcome, BotActivity? Activity = null, string? WhyNot = null, int Candidates = 0, int Rejections = 0)
{
    public static BotArbitration None { get; } = new(BotArbitrationOutcome.None);
    public static BotArbitration Unchanged(BotActivity activity, int candidates = 0, int rejections = 0, string? whyNot = null) => new(BotArbitrationOutcome.Unchanged, activity, whyNot, candidates, rejections);
}

/// <summary>
/// G3-B3 goal arbitration seam: collects the registered
/// <see cref="IBotActivityModule"/>s and picks the ONE active activity per
/// bot per wake. Deterministic — highest priority wins, then drawn motivation
/// inside that priority band, then registration order; no randomness, no LLM
/// anything.
///
/// Semantics:
///  - Zero modules → strictly inert (<see cref="BotArbitrationOutcome.None"/>),
///    no state is touched. This is the fail-closed default for deployments
///    that register nothing.
///  - PRIORITY IS THE SAFETY ORDERING. Modules are asked in priority bands
///    (highest first) and the first band that yields an allowance decides the
///    wake; lower bands are never asked. A module's
///    <see cref="BotActivityDecision.Weight"/> only ranks the allowances of
///    its OWN band, so a heavy low-priority module can never outrank
///    survival/recovery.
///  - COMMITMENT: while the incumbent activity is still offered by its own
///    module in the deciding band, it keeps it unless a band-mate outbids it
///    by the preemption margin (25%, floor 1). Commitment is therefore
///    within-band only — it never delays a higher-priority band.
///  - The ACTIVE ACTIVITY NAME (not the module) is the arbiter's unit of
///    change: a schedule module moving "schedule.work" → "schedule.rest"
///    counts as a transition even though the module itself is unchanged.
///  - Single-active enforcement: at most one entry per bot in
///    <see cref="_activeActivity"/>; every activation atomically replaces it.
///  - Transitions are logged ONCE (Info, old → new + winning module +
///    motivation + the band's candidate scores + the top refusal reason);
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

        var rejected = 0;
        string? topWhyNot = null;

        // Priority bands: the snapshot is pre-sorted by priority desc, so one
        // pass groups equal priorities. The FIRST band that yields an allowance
        // decides the wake; every lower band is never asked, so priority stays
        // the safety ordering and an expensive eligibility probe below a winner
        // stays unrun.
        var bandStart = 0;
        while (bandStart < snapshot.Count)
        {
            var bandPriority = snapshot[bandStart].Priority;
            var bandEnd = bandStart;
            while (bandEnd < snapshot.Count && snapshot[bandEnd].Priority == bandPriority)
                bandEnd++;

            // Allocated only when this band actually yields a candidate.
            List<BandCandidate>? band = null;
            for (var i = bandStart; i < bandEnd; i++)
            {
                var module = snapshot[i];
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
                    rejected++;
                    continue;
                }

                if (!decision.CanActivate)
                {
                    rejected++;
                    // First denial that carries a reason is the highest-priority
                    // refusal; a later, lower-priority reason never replaces it.
                    topWhyNot ??= decision.WhyNot;
                    continue;
                }

                var activityName = decision.ActivityName;
                if (string.IsNullOrEmpty(activityName))
                {
                    Logger.Warn("BotGoalArbiter: module {Module} allowed activation without an activity name — skipped",
                        module.Name);
                    rejected++;
                    continue;
                }

                (band ??= new List<BandCandidate>(bandEnd - bandStart))
                    .Add(new BandCandidate(module, activityName, decision.Weight, i));
            }

            if (band != null)
                return Decide(bot, activeName, context, band, rejected, topWhyNot);

            bandStart = bandEnd;
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

        return new BotArbitration(BotArbitrationOutcome.NoCandidate, null, topWhyNot, 0, rejected);
    }

    /// <summary>Decides the wake from the allowance of ONE priority band.</summary>
    private BotArbitration Decide(PlayerBotRuntime bot, string? activeName, BotActivityContext context,
        List<BandCandidate> band, int rejected, string? topWhyNot)
    {
        // Rank inside the band: drawn motivation first, then registration order —
        // the pre-existing deterministic tie-break.
        band.Sort(static (a, b) =>
        {
            var byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : a.Index.CompareTo(b.Index);
        });

        var best = band[0];

        // Commitment: while the incumbent's own module still offers the
        // incumbent activity in this band, it keeps it — unless a band-mate
        // outbids it by the preemption margin. Only a band-mate can take the
        // activity away: a heavier lower-priority module never reaches here, so
        // survival/recovery precedence is untouched.
        var incumbent = -1;
        for (var i = 0; i < band.Count; i++)
        {
            if (band[i].ActivityName != activeName)
                continue;
            incumbent = i;
            break;
        }

        if (incumbent >= 0)
        {
            var held = band[incumbent];
            if (incumbent == 0 || best.Weight < held.Weight + PreemptionMargin(held.Weight))
                return BotArbitration.Unchanged(new BotActivity(held.ActivityName, held.Module.Name),
                    band.Count, rejected, topWhyNot);
        }

        // No separate "the winning name is already active" case: if band[0] carried
        // the active name the incumbent lookup above would have found it at index 0
        // and held it, so reaching here means the name really changes.

        // Transition: replace the single active activity, apply once, log once.
        _activeActivity[bot.CharacterId] = best.ActivityName;
        Interlocked.Increment(ref _transitions);

        BotActivity activity;
        try
        {
            activity = best.Module.Activate(context);
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
                best.Module.Name, bot.CharacterId);
            throw;
        }

        Logger.Info("BotGoalArbiter: bot {CharacterId} activity {Old} -> {New} (module {Module}, motivation {Weight}; candidates [{Candidates}]; refused {Rejections}{WhyNot})",
            bot.CharacterId, activeName ?? "(none)", activity.Name, best.Module.Name, best.Weight,
            string.Join(", ", band.Select(static c => $"{c.Module.Name}={c.Weight}")), rejected,
            topWhyNot == null ? string.Empty : $" — {topWhyNot}");
        return new BotArbitration(BotArbitrationOutcome.Activated, activity, topWhyNot, band.Count, rejected);
    }

    private const int PreemptionMarginPercent = 25;

    /// <summary>
    /// Margin a band-mate must beat the incumbent's motivation by before it
    /// takes the activity away (25%, floor 1) — mirrors the combat target
    /// scorer's challenger margin so a marginal preference cannot thrash the
    /// choice wake after wake.
    /// </summary>
    private static int PreemptionMargin(int weight) => Math.Max(1, (int)((long)weight * PreemptionMarginPercent / 100));

    /// <summary>One allowance inside a priority band, with its drawn motivation.</summary>
    private readonly record struct BandCandidate(IBotActivityModule Module, string ActivityName, int Weight, int Index);

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
