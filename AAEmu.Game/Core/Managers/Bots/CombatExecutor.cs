#nullable enable

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Bounded combat executor — the owner of WAIT and RETRY timing around the
/// engine's existing learned-skill gate inside <see cref="Skill.Use"/>
/// (ROADMAP P1 "Bounded combat executor prerequisite: learned-skill GCD").
///
/// Why this exists (the reverted-attempt cause): the learned-skill gate is
/// only consulted when <c>bypassGcd</c> is false. <c>Unit.UseSkill</c>
/// hardcodes <c>bypassGcd: true</c>, so a cast routed through
/// <see cref="IGameplayActor.Cast"/> never trips the GCD arm — and forcing
/// the gate there (the reverted change, recorded in STATUS.md) made
/// no-time-advance scenario runners burn one hunt-budget attempt per
/// stationary refusal without ever becoming legal. Timing therefore belongs
/// to a bounded executor that (a) rides the SAME no-bypass call shape the
/// client's learned-skill branch uses, and (b) never spends an attempt while
/// its own clock has not advanced past the wait it owns.
///
/// Contract:
///  - One logical cast = one <see cref="Begin"/>, advanced by
///    <see cref="Tick"/> until <see cref="CombatCastOutcome.IsTerminal"/>.
///  - <see cref="SkillResult.CooldownTime"/> is the ONLY retryable engine
///    result (it is a timing refusal). Every other engine result is an
///    AUTHORITATIVE terminal refusal: it is reported as-is and never
///    retried. Attempts are bounded by <see cref="MaxAttempts"/> and the
///    whole wait is bounded by <see cref="WaitBudget"/>, both measured on
///    <see cref="TimeProvider"/> — the executor clock, not the engine's.
///  - Zero attempts are spent while the executor clock is stationary: a
///    <see cref="Tick"/> that lands before <see cref="CombatCastOutcome.NextAttemptAtUtc"/>
///    returns <see cref="CombatCastStatus.Waiting"/> without entering the
///    engine (deterministic-time evidence).
///  - A repeat of a request whose explicit idempotency key already applied
///    its effect is answered from the <see cref="ActorEffectLedger"/> with
///    <see cref="CombatCastOutcome.DuplicateSuppressed"/> and ZERO engine
///    entries — retries can never duplicate an effect.
///  - The engine call is the client-parity call: a <see cref="Skill"/> built
///    from the real template, a <c>SkillCasterUnit</c> carrying the caster
///    objId, a <c>SkillCastUnitTarget</c> carrying the target objId, and
///    <c>bypassGcd: false</c> — exactly what <c>CSStartSkillPacket</c>'s
///    learned-skill branch passes. No packet is fabricated and no packet
///    path is modified.
///
/// Not in scope (frozen): GOAP micro-actions, actor-level enforcement
/// (<see cref="GameplayActor.Cast"/> keeps its single-shot, no-wait
/// behavior), harvest/purchase/planting, and any new gameplay service.
/// </summary>
public sealed class CombatExecutor
{
    /// <summary>Max engine entries for one logical cast (1 initial + retries).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Executor-owned wait between a timing refusal and the next attempt.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Overall wait budget for one logical cast, measured on the executor clock.</summary>
    public TimeSpan WaitBudget { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Clock that owns every wait/retry decision (tests inject a fake provider).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Applied-effect ledger — the duplicate-suppression surface for explicit keys.</summary>
    public ActorEffectLedger Ledger { get; init; } = new();

    /// <summary>Engine entries this executor has made (bounded-wait evidence; monotonic).</summary>
    public int EngineEntries { get; private set; }

    /// <summary>Attempts spent by the pending logical cast (0 when idle).</summary>
    public int Attempts => _pending?.Attempts ?? 0;

    /// <summary>True while a logical cast is in flight.</summary>
    public bool IsPending => _pending != null;

    private PendingCast? _pending;

    /// <summary>
    /// Starts one logical cast of a unit-target learned skill and runs the
    /// first attempt immediately (the request is due at the current executor
    /// time). A duplicate of an already-applied explicit key completes
    /// without entering the engine.
    /// </summary>
    public CombatCastOutcome Begin(Character caster, uint skillId, uint targetObjId, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(caster);

        var now = TimeProvider.GetUtcNow().UtcDateTime;

        // Single-cast rule: one logical cast in flight. A request arriving
        // while another is pending is refused WITHOUT disturbing the pending
        // cast (its wait, attempts and ledger effect stay intact).
        if (_pending != null)
            return new CombatCastOutcome(
                CombatCastStatus.Refused, skillId, targetObjId, _pending.LastEngineResult,
                Attempts: 0, DuplicateSuppressed: false,
                Detail: $"executor busy with skill {_pending.SkillId} on target {_pending.TargetObjId}",
                NextAttemptAtUtc: _pending.NextAttemptAtUtc);

        // Duplicate gate: an explicit key that already applied its effect is
        // never re-executed. Answered from the ledger — the engine is not
        // entered, so the effect cannot be applied twice.
        if (!string.IsNullOrEmpty(idempotencyKey)
            && Ledger.IsEffectApplied(EffectFingerprint(skillId, idempotencyKey)))
        {
            _pending = null;
            return new CombatCastOutcome(
                CombatCastStatus.Completed, skillId, targetObjId, SkillResult.Success,
                Attempts: 0, DuplicateSuppressed: true,
                Detail: $"duplicate idempotency key '{idempotencyKey}' — effect already applied; engine not entered",
                NextAttemptAtUtc: null);
        }

        _pending = new PendingCast(caster, skillId, targetObjId, idempotencyKey, now);
        return Tick();
    }

    /// <summary>
    /// Advances the pending cast. Enters the engine only when the executor's
    /// own wait has elapsed (first attempt is due immediately) and its
    /// attempt/budget bounds still allow it. Never blocks.
    /// </summary>
    public CombatCastOutcome Tick()
    {
        var pending = _pending;
        if (pending == null)
            return CombatCastOutcome.Idle;

        ExecutionBoundary.AssertOnExecutionThread("CombatExecutor.Tick");

        var now = TimeProvider.GetUtcNow().UtcDateTime;

        // Executor-owned wait. A stationary clock spends NOTHING: the wait is
        // the executor's, so time that does not advance costs no attempt.
        if (now < pending.NextAttemptAtUtc)
            return Outcome(pending, CombatCastStatus.Waiting, pending.LastEngineResult,
                $"waiting until {pending.NextAttemptAtUtc:O} before attempt {pending.Attempts + 1}");

        // Bounds are checked BEFORE entering the engine, so an exhausted
        // budget is an authoritative terminal refusal, not a spent attempt.
        if (pending.Attempts >= MaxAttempts)
            return Terminate(pending, CombatCastStatus.Refused,
                $"retry budget exhausted after {pending.Attempts} attempt(s); last engine result {pending.LastEngineResult}");

        if (now - pending.StartedAtUtc >= WaitBudget)
            return Terminate(pending, CombatCastStatus.Refused,
                $"wait budget {WaitBudget.TotalMilliseconds:0}ms exhausted with time advancing; last engine result {pending.LastEngineResult}");

        // Pre-flight parity with the client's learned-skill branch: only a
        // skill the character can actually route is supported. A routing
        // failure is terminal and never consumes an attempt.
        var unsupported = Unsupported(caster: pending.Caster, skillId: pending.SkillId, targetObjId: pending.TargetObjId);
        if (unsupported != null)
            return Terminate(pending, CombatCastStatus.Refused, unsupported);

        var result = UseThroughLearnedSkillGate(pending.Caster, pending.SkillId, pending.TargetObjId);
        pending.Attempts++;
        pending.LastEngineResult = result;
        EngineEntries++;

        if (result == SkillResult.Success)
        {
            // The applied effect is fingerprinted under the caller's key (a
            // fresh correlation id stands in for the attempt's trace), so a
            // repeat of that key is answered from the ledger, never by a
            // second engine entry.
            if (!string.IsNullOrEmpty(pending.IdempotencyKey))
                Ledger.RecordEffect(EffectFingerprint(pending.SkillId, pending.IdempotencyKey), pending.TraceId);
            return Terminate(pending, CombatCastStatus.Completed,
                $"skill {pending.SkillId} cast on {pending.TargetObjId} in {pending.Attempts} attempt(s)",
                engineResult: SkillResult.Success);
        }

        // The GCD/cooldown refusal is the only retryable outcome: the engine
        // is telling us to wait, and waiting is exactly what this executor
        // owns. Everything else is an authoritative refusal.
        if (result == SkillResult.CooldownTime)
        {
            ScheduleRetry(pending, now);
            if (pending.Attempts >= MaxAttempts)
                return Terminate(pending, CombatCastStatus.Refused,
                    $"retry budget exhausted after {pending.Attempts} attempt(s); last engine result {result}");
            return Outcome(pending, CombatCastStatus.Waiting, result,
                $"engine refused with {result}; retrying after {RetryBackoff.TotalMilliseconds:0}ms of executor time");
        }

        return Terminate(pending, CombatCastStatus.Refused, $"engine refused with {result}");
    }

    /// <summary>Drops the pending cast without entering the engine again.</summary>
    public void Cancel()
    {
        _pending = null;
    }

    private void ScheduleRetry(PendingCast pending, DateTime now)
        => pending.NextAttemptAtUtc = now + RetryBackoff;

    private static CombatCastOutcome Outcome(PendingCast pending, CombatCastStatus status,
        SkillResult? engineResult, string? detail)
        => new(status, pending.SkillId, pending.TargetObjId, engineResult, pending.Attempts,
            DuplicateSuppressed: false, Detail: detail, NextAttemptAtUtc: pending.NextAttemptAtUtc);

    private CombatCastOutcome Terminate(PendingCast pending, CombatCastStatus status, string? detail,
        SkillResult? engineResult = null)
    {
        _pending = null;
        // A non-Success engine result is the authoritative refusal reason; the
        // caller-supplied detail describes the executor's own bound.
        var effectiveEngineResult = engineResult ?? pending.LastEngineResult;
        var text = effectiveEngineResult is null or SkillResult.Success
            ? detail
            : $"{effectiveEngineResult}: {detail}";
        return new CombatCastOutcome(status, pending.SkillId, pending.TargetObjId,
            effectiveEngineResult, pending.Attempts, DuplicateSuppressed: false,
            Detail: text, NextAttemptAtUtc: null);
    }

    /// <summary>
    /// The client-parity engine call — the exact shape <c>CSStartSkillPacket</c>'s
    /// learned-skill branch uses: real template, `SkillCasterUnit` with the
    /// caster objId, `SkillCastUnitTarget` with the target objId, and
    /// `bypassGcd: false` so the engine's real learned-skill gate
    /// (GlobalCooldown / SkillLastUsed / per-skill cooldown) applies.
    /// </summary>
    private static SkillResult UseThroughLearnedSkillGate(Character caster, uint skillId, uint targetObjId)
    {
        var skill = new Skill(SkillManager.Instance.GetSkillTemplate(skillId), caster);

        var casterCaster = SkillCaster.GetByType(SkillCasterType.Unit);
        casterCaster.ObjId = caster.ObjId;

        var targetCaster = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
        targetCaster.ObjId = targetObjId;

        return skill.Use(caster, casterCaster, targetCaster, null, false, out _);
    }

    /// <summary>
    /// Routing rule of the client's learned-skill branch (the same rule
    /// <see cref="GameplayActor.Cast"/> applies): the template must exist and
    /// the character must know it — learned, a variant of a learned skill, or
    /// a default/common skill. Returns null when the skill is supported.
    /// </summary>
    private static string? Unsupported(Character caster, uint skillId, uint targetObjId)
    {
        if (SkillManager.Instance.GetSkillTemplate(skillId) == null)
            return $"unknown skill {skillId}";

        var known = caster.Skills?.Skills.ContainsKey(skillId) == true
                    || caster.Skills?.IsVariantOfSkill(skillId) == true
                    || SkillManager.Instance.IsDefaultSkill(skillId)
                    || SkillManager.Instance.IsCommonSkill(skillId);
        if (!known)
            return $"skill {skillId} not learned";

        if (caster.ParentWorld?.GetUnit(targetObjId) == null)
            return $"cast target {targetObjId} not found in world";

        return null;
    }

    private static string EffectFingerprint(uint skillId, string idempotencyKey)
        => ActorIdempotency.EffectKey("skillcast", skillId, idempotencyKey);

    private sealed class PendingCast(Character caster, uint skillId, uint targetObjId,
        string? idempotencyKey, DateTime startedAtUtc)
    {
        public Character Caster { get; } = caster;
        public uint SkillId { get; } = skillId;
        public uint TargetObjId { get; } = targetObjId;
        public string? IdempotencyKey { get; } = idempotencyKey;
        public DateTime StartedAtUtc { get; } = startedAtUtc;

        /// <summary>Correlation id of the logical cast (the ledger's effect owner).</summary>
        public Guid TraceId { get; } = Guid.NewGuid();
        public DateTime NextAttemptAtUtc { get; set; } = startedAtUtc;
        public int Attempts { get; set; }
        public SkillResult? LastEngineResult { get; set; }
    }
}

/// <summary>Lifecycle status of one bounded combat-executor cast.</summary>
public enum CombatCastStatus : byte
{
    /// <summary>No logical cast is in flight.</summary>
    Idle,

    /// <summary>Waiting on the executor's own clock; no engine entry spent.</summary>
    Waiting,

    /// <summary>The engine applied the skill (or the effect was already applied for this key).</summary>
    Completed,

    /// <summary>Authoritative terminal refusal — the engine said no for a non-timing reason, or a bound expired.</summary>
    Refused
}

/// <summary>
/// Result of one bounded cast observation. <see cref="Attempts"/> is the
/// number of ENGINE entries spent by the logical cast; a
/// <see cref="CombatCastStatus.Waiting"/> outcome produced while time is
/// stationary reports the unchanged count (zero-attempt evidence).
/// </summary>
/// <param name="Status">Lifecycle status.</param>
/// <param name="SkillId">Skill requested.</param>
/// <param name="TargetObjId">Unit target objId requested.</param>
/// <param name="EngineResult">
/// Last engine <see cref="SkillResult"/> observed (null before the first
/// engine entry). For a timing refusal it is <see cref="SkillResult.CooldownTime"/>;
/// for a <see cref="CombatCastStatus.Completed"/> it is <see cref="SkillResult.Success"/>.
/// </param>
/// <param name="Attempts">Engine entries spent by this logical cast.</param>
/// <param name="DuplicateSuppressed">True when the answer came from the effect ledger with zero engine entries.</param>
/// <param name="Detail">Human-readable detail (never "bot got stuck").</param>
/// <param name="NextAttemptAtUtc">Executor-clock time the next attempt is due (null when terminal).</param>
public sealed record CombatCastOutcome(
    CombatCastStatus Status,
    uint SkillId,
    uint TargetObjId,
    SkillResult? EngineResult,
    int Attempts,
    bool DuplicateSuppressed,
    string? Detail,
    DateTime? NextAttemptAtUtc)
{
    /// <summary>Terminal outcomes are final: no further attempt will be spent.</summary>
    public bool IsTerminal => Status is CombatCastStatus.Completed or CombatCastStatus.Refused;

    /// <summary>Idle sentinel (no logical cast in flight).</summary>
    public static CombatCastOutcome Idle { get; } =
        new(CombatCastStatus.Idle, 0, 0, null, 0, false, null, null);
}
