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
/// reason, i.e. the highest-priority one — except that an ownerless allowance
/// (plan T9, audit L3-a) replaces it: a module electing work no dispatch owner
/// performs is a broken wiring contract, not a preference, so the operator's
/// read-back must be able to see it. Null when nothing refused or no refusal
/// explained itself.
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

    /// <summary>
    /// The activity that owns this bot's roam route, or null when nothing does
    /// (plan T8, audit L1-g). This is <see cref="GetActiveActivity"/> filtered
    /// through <see cref="BotRouteOwnership"/>: the terminal idle fallback
    /// clears the route instead of owning one and the schedule layer's own
    /// "schedule.*" activities ARE the schedule's route, so both report null and
    /// a lower-authority writer may arm a route. Every other live activity is
    /// work in force whose route must survive a schedule phase change.
    /// </summary>
    string? GetRouteOwner(uint characterId);

    /// <summary>
    /// How many allowances were refused because nothing in the bot carries the
    /// elected activity (plan T9, audit L3-a "dispatcher-or-deny"). Zero on a
    /// healthy deployment: a non-zero value means a module elects work no
    /// dispatch owner performs, which <see cref="BotActivityDispatchValidation"/>
    /// reports at startup.
    /// </summary>
    long OwnerlessRefusalCount { get; }

    /// <summary>Drops a bot's active-activity memory (deactivation cleanup) — the world is not touched.</summary>
    void Forget(uint characterId);

    /// <summary>
    /// Leg-outcome return path (plan T4, audit L3-b): the executor reports one
    /// leg's terminal outcome. The arbiter stamps it with the module that owns
    /// the activity the leg was issued under and hands it to that module's next
    /// <see cref="IBotActivityModule.CanActivate"/> through
    /// <see cref="BotActivityContext.LastLegOutcome"/>, exactly once.
    ///
    /// A report is dropped when the bot has no active activity (there is no
    /// module to hand it to) or when the leg's own activity stamp no longer
    /// matches the active one (the fact belongs to work that is over). The
    /// arbiter decides nothing from an outcome — it is the carrier, not the
    /// policy.
    /// </summary>
    void ReportLegOutcome(BotLegOutcome outcome);

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

    // Per-bot single-active memory: characterId → the activity name AND the
    // module that owns it. The name stays the arbiter's unit of change; the
    // module identity is what lets the arbiter ask the OUTGOING module to
    // release its work before a new activity takes over (the retirement
    // contract — see IBotActivityModule.Deactivate).
    private readonly ConcurrentDictionary<uint, BotActivityIdentity> _activeActivity = [];

    // T4 return path: one leg outcome per bot, waiting for the first election
    // after the executor reported it. Stamped with the owner at report time so
    // the fact reaches the module that owned the leg — not whoever happens to
    // be active when the next wake runs. One entry per bot, O(bots), like the
    // active-activity memory above; cleared as it is handed over, on an
    // activity change and on Forget.
    private readonly ConcurrentDictionary<uint, PendingLegOutcome> _pendingLegOutcome = [];

    // Plan T6 (audit L1.3 / L4-b / L4-c): the bot's durable continuity record —
    // the goal the last election put in force, and the goal that election
    // interrupted. Read once per bot (the restart / re-enrollment path) and kept
    // so the record is handed to the module that lost the goal exactly once. One
    // entry per bot, O(bots), like the two memories above; dropped on Forget.
    private readonly ConcurrentDictionary<uint, ContinuityState> _continuity = [];

    private long _transitions;
    private long _arbitrations;

    // Plan T9 (audit L3-a): allowances refused because nothing carries the
    // elected activity. Expected to stay 0; the startup validation reports the
    // wiring gap that would make it climb.
    private long _ownerlessRefusals;

    /// <summary>
    /// Reads a bot's durable continuity record (null = nothing was ever
    /// recorded, or no store is wired). Production wires this to the bot
    /// metadata store's <c>planner_state</c> column; leaving it null keeps the
    /// pre-T6 behaviour, where a restart rebuilds the bot from nothing.
    /// </summary>
    public Func<uint, BotGoalContinuity?>? ContinuityProvider { get; set; }

    /// <summary>
    /// Records an election outcome durably (the elected goal plus the goal it
    /// replaced, if any). Production wires this beside the provider; null = no
    /// persistence. Called only when the outcome CHANGES — a steady-state wake
    /// writes nothing, so this never runs per tick.
    /// </summary>
    public Action<uint, BotGoalContinuity>? ContinuitySink { get; set; }

    /// <summary>
    /// Answers "does anything in the bot carry this elected activity?" (plan T9,
    /// audit L3-a "dispatcher-or-deny"). Production wires this to
    /// <see cref="BotActivityDispatchSurface.IsCarried"/>; leaving it null keeps
    /// the pre-T9 behaviour, where a module's allowance is trusted without a
    /// dispatch owner.
    ///
    /// A module whose allowance names work nobody performs is refused here with
    /// a reason (and counted in <see cref="OwnerlessRefusalCount"/>) instead of
    /// being reported <c>Activated</c> and silently doing something else.
    /// </summary>
    public Func<string, bool>? ActivityCarrier { get; set; }

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
    public long OwnerlessRefusalCount => Volatile.Read(ref _ownerlessRefusals);

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

        var activeName = _activeActivity.TryGetValue(bot.CharacterId, out var current) ? current.ActivityName : null;
        var context = new BotActivityContext
        {
            Bot = bot,
            GameHour = gameHour,
            ActiveActivity = activeName,
        };

        // Plan T6: the durable record is read once per bot (the first election
        // after this process started, or after the bot was forgotten) so a
        // restart hands the module that lost the goal the same fact the wake
        // that dropped it did. Never per wake: the state caches the read.
        var continuity = ReadContinuity(bot.CharacterId);

        var rejected = 0;
        string? topWhyNot = null;

        // Priority bands: the snapshot is pre-sorted by priority desc, so one
        // pass groups equal priorities. The FIRST band that yields an allowance
        // decides the wake; every lower band is never asked, so priority stays
        // the safety ordering and an expensive eligibility probe below a winner
        // stays unrun.
        BotStepProfile.Mark("arbiter");
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
                    decision = module.CanActivate(ModuleContextFor(bot.CharacterId, module, context, continuity));
                    BotStepProfile.Mark(module.Name);
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

                // Plan T9 (audit L3-a "dispatcher-or-deny"): an allowance is only
                // honoured when something in the bot actually performs the
                // activity. A module that elects work no dispatch owner carries is
                // refused — naming the missing owner — instead of being reported
                // Activated while the executor silently does something else. The
                // reason is allowed to REPLACE an earlier refusal: an ownerless
                // election is a broken wiring contract, not a preference, so the
                // operator's read-back must be able to see it.
                if (ActivityCarrier is { } carrier && !carrier(activityName))
                {
                    Interlocked.Increment(ref _ownerlessRefusals);
                    rejected++;
                    topWhyNot = $"no dispatch owner for activity '{activityName}'";
                    Logger.Warn(
                        "BotGoalArbiter: module {Module} allowed activity {Activity} but no dispatch owner carries it — refused for bot {CharacterId}",
                        module.Name, activityName, bot.CharacterId);
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
        // candidate activates cleanly. The WORLD is left untouched, except
        // that the outgoing module is still asked to release what it owns —
        // an activity that nobody replaced must not keep a live leg running.
        if (activeName != null)
        {
            var dropped = _activeActivity.TryGetValue(bot.CharacterId, out var outgoing)
                ? outgoing
                : (BotActivityIdentity?)null;
            RetireOutgoing(bot, dropped, gameHour);
            _activeActivity.TryRemove(bot.CharacterId, out _);
            DropStaleLegOutcome(bot.CharacterId);
            // The goal is gone and nobody replaced it: record it durably, so the
            // drop is visible to the module that owned it (and survives a
            // restart) instead of vanishing with this process's memory.
            RecordContinuity(bot.CharacterId, new BotGoalContinuity(
                null, null, dropped?.ActivityName, dropped?.ModuleName,
                "no module could activate", DateTime.UtcNow));
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

        // Transition: retire the outgoing activity's module, replace the single
        // active activity, apply once, log once.
        //
        // Retirement runs BEFORE the incoming module activates (plan T1): the
        // outgoing owner releases its live work — its own actor request, its
        // armed route — so the new activity never starts on top of a leg that
        // is still walking. A refusal is handed to the incoming module through
        // the context instead of being swallowed.
        var outgoing = _activeActivity.TryGetValue(bot.CharacterId, out var previous)
            ? previous
            : (BotActivityIdentity?)null;

        // An activity that continues inside the SAME owner is not a supersede:
        // the module keeps its own work and only the activity name changes
        // (schedule.work -> schedule.rest), so it must not be asked to release
        // what it is about to keep running.
        var retirement = outgoing is { } owner && owner.ModuleName != best.Module.Name
            ? RetireOutgoing(bot, outgoing, context.GameHour)
            : null;
        _activeActivity[bot.CharacterId] = new BotActivityIdentity(best.ActivityName, best.Module.Name);
        DropStaleLegOutcome(bot.CharacterId);
        Interlocked.Increment(ref _transitions);

        // Plan T6: why this election ended the previous goal. The rename test is
        // the OWNER's name, not a null retirement — RetireOutgoing also returns
        // null when the outgoing module is no longer registered, and that is a
        // supersede by a different module, not a rename.
        var interruptionReason = outgoing == null
            ? "first election for this bot"
            : outgoing.Value.ModuleName == best.Module.Name
                ? $"same owner {best.Module.Name}, activity renamed to {best.ActivityName}"
                : retirement is { Retired: false }
                    ? $"superseded by {best.ActivityName} ({retirement.Reason})"
                    : $"superseded by {best.ActivityName}";

        var activateContext = retirement == null
            ? context
            : new BotActivityContext
            {
                Bot = context.Bot,
                GameHour = context.GameHour,
                ActiveActivity = context.ActiveActivity,
                Retirement = retirement,
            };

        BotActivity activity;
        try
        {
            activity = best.Module.Activate(activateContext);
            BotStepProfile.Mark("activate");
        }
        catch (Exception ex)
        {
            // Side effects never landed — restore the previous activity so
            // a later wake can retry cleanly, then surface the failure.
            if (activeName == null || outgoing == null)
                _activeActivity.TryRemove(bot.CharacterId, out _);
            else
                _activeActivity[bot.CharacterId] = outgoing.Value;
            Interlocked.Decrement(ref _transitions);
            Logger.Error(ex, "BotGoalArbiter: module {Module} Activate failed for bot {CharacterId}",
                best.Module.Name, bot.CharacterId);
            throw;
        }

        // Plan T6: record the outcome change durably — the goal now in force plus
        // the goal this election ended and why. Written only here and on the
        // no-candidate drop below, never on a steady-state wake, and only once
        // the transition has landed: an Activate failure above rolls the election
        // back, so the record must not describe a goal that never started.
        RecordContinuity(bot.CharacterId, new BotGoalContinuity(
            best.ActivityName,
            best.Module.Name,
            outgoing?.ActivityName,
            outgoing?.ModuleName,
            interruptionReason,
            DateTime.UtcNow));

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

    /// <summary>
    /// A leg outcome waiting for the election that follows it, stamped with the
    /// module that owned the leg when it ended. The activity name is not stored
    /// separately: a leg whose activity stamp no longer matches the active one is
    /// refused on arrival (<see cref="ReportLegOutcome"/>) and an activity change
    /// clears the entry, so a pending outcome always belongs to the activity the
    /// bot is still on (<c>BotActivityContext.ActiveActivity</c>).
    /// </summary>
    private readonly record struct PendingLegOutcome(string ModuleName, BotLegOutcome Outcome);

    /// <inheritdoc />
    public void ReportLegOutcome(BotLegOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // No active activity = nobody owned the leg = nobody to tell. Dropping it
        // keeps the store to one entry per bot and never resurrects an activity.
        if (!_activeActivity.TryGetValue(outcome.CharacterId, out var owner))
            return;

        // The leg says which activity issued it. If the bot has left that
        // activity in the meantime, the fact belongs to work that is over: the
        // module that took the bot over must not be handed somebody else's leg.
        // The comparison is by activity name, so a module that renames its own
        // activity (a schedule phase, a conflict zone) drops its own pending leg
        // too — the stamp names the activity the leg ran under, not the module.
        // A null stamp (no activity provider wired) carries no such claim and is
        // delivered as before.
        if (outcome.ActivityName is { } legActivity
            && !string.Equals(legActivity, owner.ActivityName, StringComparison.Ordinal))
            return;

        _pendingLegOutcome[outcome.CharacterId] = new PendingLegOutcome(owner.ModuleName, outcome);
    }

    /// <summary>
    /// The context one module is asked with: the wake context, plus the T4 leg
    /// outcome when this module owned the leg it reports, plus the T6 continuity
    /// record when this module is the one that lost the goal. Each is handed
    /// over exactly once — the entry is removed as it is handed over, so a
    /// module that ignores it, or throws on it, has still received it once and
    /// no later wake can see it again. The plain context is reused untouched
    /// when there is nothing to hand over (the common case), so both return
    /// paths cost nothing on an ordinary wake.
    /// </summary>
    private BotActivityContext ModuleContextFor(uint characterId, IBotActivityModule module,
        BotActivityContext context, BotGoalContinuity? continuity)
    {
        var legOutcome = TakePendingLegOutcome(characterId, module.Name);
        var record = continuity == null ? null : ContinuityFor(characterId).TakeFor(module.Name);

        if (legOutcome == null && record == null)
            return context;

        return new BotActivityContext
        {
            Bot = context.Bot,
            GameHour = context.GameHour,
            ActiveActivity = context.ActiveActivity,
            Retirement = context.Retirement,
            LastLegOutcome = legOutcome,
            Continuity = record,
        };
    }

    /// <summary>
    /// Takes the pending leg outcome for the module that owned the leg, or null
    /// for any other module (which is asked with the plain context and leaves
    /// the entry alone — the owner may still be asked later this wake).
    /// </summary>
    private BotLegOutcome? TakePendingLegOutcome(uint characterId, string moduleName)
    {
        if (!_pendingLegOutcome.TryGetValue(characterId, out var pending) || pending.ModuleName != moduleName)
            return null;

        _pendingLegOutcome.TryRemove(characterId, out _);
        return pending.Outcome;
    }

    /// <summary>The bot's continuity state — one per bot, like the memories above.</summary>
    private ContinuityState ContinuityFor(uint characterId) =>
        _continuity.GetOrAdd(characterId, static _ => new ContinuityState());

    /// <summary>
    /// The durable record this bot's wake should see (null when nothing was ever
    /// recorded, or when no provider is wired — the pre-T6 behaviour). The state
    /// caches the read, so the store is asked once per bot per process.
    /// </summary>
    private BotGoalContinuity? ReadContinuity(uint characterId) =>
        ContinuityFor(characterId).Read(ContinuityProvider, characterId);

    /// <summary>
    /// Remembers an election outcome and persists it. A store that throws is
    /// logged and swallowed: the bot's goal is already decided, and persistence
    /// failing must not take the wake down (the store leaves the row dirty and
    /// retries on the next save).
    /// </summary>
    private void RecordContinuity(uint characterId, BotGoalContinuity record)
    {
        ContinuityFor(characterId).Record(record);

        var sink = ContinuitySink;
        if (sink == null)
            return;

        try
        {
            sink(characterId, record);
        }
        catch (Exception ex)
        {
            Logger.Warn("BotGoalArbiter: recording continuity for bot {CharacterId} threw {Type}: {Message}",
                characterId, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// Per-bot continuity memory (plan T6): the record read from or written to
    /// the bot metadata store, and whether the module that lost the goal has yet
    /// been handed it. The read happens once — on the first election in this
    /// process, or after <see cref="Forget"/> — so the restart path costs one
    /// store lookup, not one per wake.
    ///
    /// A record is handed over only to the module named as the interrupted
    /// owner, and only when that module is NOT the owner of the goal now in
    /// force: when one module merely renamed its own activity (schedule.work ->
    /// schedule.rest) it kept the work and has nothing to learn, while a module
    /// that lost the goal to a different module is the one that needs the fact.
    /// </summary>
    private sealed class ContinuityState
    {
        private readonly object _lock = new();
        private BotGoalContinuity? _record;
        private bool _read;
        private bool _pending;

        /// <summary>The record for this bot, reading the store at most once.</summary>
        public BotGoalContinuity? Read(Func<uint, BotGoalContinuity?>? provider, uint characterId)
        {
            lock (_lock)
            {
                if (_read)
                    return _record;

                _read = true;

                if (provider == null)
                    return null;

                try
                {
                    _record = provider(characterId);
                }
                catch (Exception ex)
                {
                    // A damaged store or row must not stop the bot from acting:
                    // the wake proceeds as if nothing had ever been recorded.
                    Logger.Warn("BotGoalArbiter: reading continuity for bot {CharacterId} threw {Type}: {Message}",
                        characterId, ex.GetType().Name, ex.Message);
                    _record = null;
                }

                _pending = HasLoser(_record);
                return _record;
            }
        }

        /// <summary>Remembers an outcome change, making its loser the next handoff.</summary>
        public void Record(BotGoalContinuity record)
        {
            lock (_lock)
            {
                _record = record;
                _read = true;
                _pending = HasLoser(record);
            }
        }

        /// <summary>The record for the module that lost the goal, handed over once; null for any other module.</summary>
        public BotGoalContinuity? TakeFor(string moduleName)
        {
            lock (_lock)
            {
                if (!_pending || _record?.InterruptedModule != moduleName)
                    return null;

                _pending = false;
                return _record;
            }
        }

        private static bool HasLoser(BotGoalContinuity? record) =>
            record?.InterruptedModule != null && record.ModuleName != record.InterruptedModule;
    }

    /// <summary>
    /// Drops a leg outcome the activity change just made stale: it was stamped
    /// for the activity the bot is leaving, so no later wake may deliver it.
    /// </summary>
    private void DropStaleLegOutcome(uint characterId) => _pendingLegOutcome.TryRemove(characterId, out _);

    /// <inheritdoc />
    public string? GetActiveActivity(uint characterId) =>
        _activeActivity.TryGetValue(characterId, out var identity) ? identity.ActivityName : null;

    /// <inheritdoc />
    public string? GetRouteOwner(uint characterId)
    {
        var active = GetActiveActivity(characterId);
        return BotRouteOwnership.OwnsRoute(active) ? active : null;
    }

    /// <inheritdoc />
    public void Forget(uint characterId)
    {
        _activeActivity.TryRemove(characterId, out _);
        DropStaleLegOutcome(characterId);
        // Plan T6: the in-memory record goes with the bot (audit L4-b — this
        // memory used to grow forever). The durable row stays: if the same
        // character is enrolled again, its first election reads the record back
        // and the module that lost the goal is told once more.
        _continuity.TryRemove(characterId, out _);
    }

    /// <summary>One activity plus the module that owns it (the retirement contract needs the owner).</summary>
    private readonly record struct BotActivityIdentity(string ActivityName, string ModuleName);

    /// <summary>
    /// Asks the module that owned the outgoing activity to release its work
    /// (<see cref="IBotActivityModule.Deactivate"/>). Returns null when nothing
    /// was superseded or the owning module is gone (unregistered); a refusal or
    /// a throw is reported as <see cref="BotActivityRetirement.Retired"/> false
    /// with the reason, so the caller can pass it on instead of claiming the
    /// superseded work was released.
    /// </summary>
    private BotActivityRetirement? RetireOutgoing(PlayerBotRuntime bot, BotActivityIdentity? outgoing, float gameHour)
    {
        if (outgoing == null)
            return null;

        var module = FindModule(outgoing.Value.ModuleName);
        if (module == null)
            return null;

        var context = new BotActivityContext
        {
            Bot = bot,
            GameHour = gameHour,
            ActiveActivity = outgoing.Value.ActivityName,
        };

        bool retired;
        string reason;
        try
        {
            retired = module.Deactivate(context);
            reason = retired ? "released its work" : "refused to release its work";
        }
        catch (Exception ex)
        {
            // A module that throws on teardown must not take the wake down: the
            // refusal is recorded and the new activity still activates.
            retired = false;
            reason = $"Deactivate threw {ex.GetType().Name}: {ex.Message}";
            Logger.Error(ex, "BotGoalArbiter: module {Module} Deactivate failed for bot {CharacterId}",
                module.Name, bot.CharacterId);
        }

        if (!retired)
            Logger.Warn("BotGoalArbiter: bot {CharacterId} activity {Old} was NOT retired by module {Module} — {Reason}",
                bot.CharacterId, outgoing.Value.ActivityName, module.Name, reason);

        return new BotActivityRetirement(module.Name, outgoing.Value.ActivityName, retired, reason);
    }

    /// <summary>The registered module with this diagnostics name, or null when it is gone.</summary>
    private IBotActivityModule? FindModule(string moduleName)
    {
        lock (_modulesLock)
            return _modules.FirstOrDefault(m => m.Name == moduleName);
    }

    private void SortModules()
    {
        // Stable OrderByDescending: equal priorities keep registration order.
        _modules = _modules.OrderByDescending(static m => m.Priority).ToList();
    }
}
