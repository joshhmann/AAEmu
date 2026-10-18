namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// G3-B3 wiring seam: the least invasive arbitration point — an
/// <see cref="IBotStepExecutor"/> DECORATOR. Every scheduler wake first runs
/// one goal-arbitration pass for the bot (<see cref="IBotGoalArbiter.Arbitrate"/>:
/// pick the single active activity, activate on change only), then delegates
/// the actual step to the inner executor
/// (<see cref="BotRoamStepExecutor"/> — the M5 actor/roam surface, unchanged).
///
/// Zero scheduler changes: the PlayerBotScheduler keeps consuming the same
/// <see cref="IBotStepExecutor"/> seam; DI swaps this decorator in as the
/// production binding. With ZERO registered modules the arbiter is inert and
/// this executor is a pure pass-through — byte-for-byte today's behavior.
/// </summary>
public sealed class BotGoalArbiterStepExecutor : IBotStepExecutor
{
    private readonly IBotGoalArbiter _arbiter;
    private readonly IBotStepExecutor _inner;
    private readonly Func<float> _gameHourProvider;

    /// <summary>
    /// Phase 1 wake identity: per-bot scheduler-wake sequence. Incremented
    /// once per <see cref="StepAsync"/> call per bot (one wake = one step,
    /// including inert pass-through wakes), staged onto the bot's actor for
    /// the inner step's requests. One entry per bot — the same key space as
    /// the arbiter's active-activity memory — so memory is O(bots) with no
    /// per-wake storage and no gameplay effect (a staged number, consumed
    /// once by the next request like the arbitration stamp).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, long> _wakeSequence = new();

    /// <summary>
    /// Last arbitration decision per bot (plan T7 operator read-back). Same
    /// key space and overwrite-next-wake semantics as <see cref="_wakeSequence"/>:
    /// one entry per bot, O(bots), no per-wake growth.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, BotWakeDecision> _lastDecision = new();

    /// <summary>Latest wake sequence for a bot (0 = never stepped through this decorator).</summary>
    public long GetWakeSequence(uint botId)
        => _wakeSequence.TryGetValue(botId, out var seq) ? seq : 0;

    /// <summary>
    /// The last arbitration decision this decorator saw for a bot (plan T7;
    /// audit L2.7), or null when it never stepped the bot. Read-back only: the
    /// operator surface answers "why this activity this wake" from it. One
    /// entry per bot, overwritten every wake — the same key space and cost as
    /// <see cref="_wakeSequence"/>, with no gameplay effect.
    /// </summary>
    public BotWakeDecision? GetLastDecision(uint botId)
        => _lastDecision.TryGetValue(botId, out var decision) ? decision : null;

    public BotGoalArbiterStepExecutor(IBotGoalArbiter arbiter, IBotStepExecutor inner,
        Func<float>? gameHourProvider = null)
    {
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gameHourProvider = gameHourProvider ?? BotActivityGameClock.Hour;
    }

    /// <inheritdoc />
    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bot);

        // One arbitration pass per wake. Steady state costs one CanActivate
        // probe per module and applies nothing; transitions arm routes once.
        // NoCandidate leaves the world untouched — the inner step still runs,
        // so any motion the bot already has simply continues.
        var arbitration = _arbiter.Arbitrate(bot, _gameHourProvider());
        // Plan T5 (audit L4-a remainder): this decorator does NOT retire an
        // outgoing activity. The executor's own read of the election releases
        // the state an activity owned (recovery at BotRoamStepExecutor
        // "recovery superseded or unsafe"), so a per-wake release here was a
        // second, duplicate owner for the same state in the same wake — the
        // supersede retirement has exactly one owner.
        // Phase 1 wake identity: one increment per wake per bot, staged for
        // the inner step's next request (same overwrite-next-wake semantics
        // as the arbitration stamp — an idle wake never leaks forward).
        var wakeSeq = _wakeSequence.AddOrUpdate(bot.CharacterId, 1, (_, seq) => seq + 1);
        _lastDecision[bot.CharacterId] = Describe(arbitration);
        StageArbitrationTrace(bot, arbitration);
        StageWakeSequence(bot, wakeSeq);

        return _inner.StepAsync(bot, cancellationToken);
    }

    /// <summary>
    /// Typed inner-executor read for death-watch unwinding (the scheduler's
    /// death entry releases the quest running-hold through the live roam
    /// executor). False when the inner executor is any other shape — the
    /// caller's switch then ignores that executor. Never creates, ticks, or
    /// mutates; behavior and cadence are untouched.
    public bool InnerIs<T>(out T? inner) where T : class, IBotStepExecutor
    {
        inner = _inner as T;
        return inner is not null;
    }

    /// <summary>
    /// Minimal decision trace (prereq Part 2): stamps the arbitration
    /// outcome — selected activity + candidate/rejection counts — for the
    /// next request the inner step creates, so the audit record answers
    /// "why this activity this wake". Reuses existing record fields only: the
    /// per-module WhyNot reasons have no actor-record column, so the transition
    /// log and the operator read-back (<see cref="GetLastDecision"/>) are where
    /// they can be read. Skipped when the arbiter is inert (None: zero
    /// modules — the pass-through must stay byte-identical) or when the
    /// inner executor is not the live-actor host (no second actor is ever
    /// created for tracing). A staged stamp is always overwritten next
    /// wake, so an idle wake never leaks stale context forward.
    /// </summary>
    private void StageArbitrationTrace(PlayerBotRuntime bot, BotArbitration arbitration)
    {
        if (arbitration.Outcome == BotArbitrationOutcome.None)
            return;
        if (_inner is not BotRoamStepExecutor roam)
            return;
        var activity = arbitration.Outcome switch
        {
            BotArbitrationOutcome.Activated or BotArbitrationOutcome.Unchanged => arbitration.Activity,
            _ => null
        };
        roam.GetOrCreateActor(bot.Character).SetPendingDecision(
            activity?.Name,
            activity != null ? $"module:{activity.ModuleName}" : null,
            arbitration.Candidates,
            arbitration.Rejections,
            seed: null, questId: null);
    }

    /// <summary>
    /// Stages the per-bot wake sequence for the inner step's next request.
    /// Unlike the arbitration stamp this is NOT skipped when the arbiter is
    /// inert — unattributed wakes are exactly what this exists to identify.
    /// The only guard is the live-actor host check (never a second actor).
    /// A staged sequence is overwritten next wake, so an idle wake never
    /// leaks stale identity forward.
    /// </summary>
    private void StageWakeSequence(PlayerBotRuntime bot, long wakeSeq)
    {
        if (_inner is not BotRoamStepExecutor roam)
            return;
        roam.GetOrCreateActor(bot.Character).SetPendingWakeSequence(wakeSeq);
    }

    /// <summary>
    /// The operator read-back of one arbitration result (plan T7): the outcome,
    /// the activity it put in force, the arbiter's own refusal text and the
    /// candidate/refusal counts. Nothing is derived or phrased here — a wake
    /// where every module allowed carries a null reason.
    /// </summary>
    private static BotWakeDecision Describe(BotArbitration arbitration)
        => new(
            arbitration.Outcome.ToString(),
            arbitration.Activity?.Name,
            arbitration.WhyNot,
            arbitration.Candidates,
            arbitration.Rejections);
}
