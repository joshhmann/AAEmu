#nullable enable

using System.Collections.Immutable;

using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// The stuck root-cause vocabulary for experiment 1 (test-side only, never
/// production). The wider DISPATCHED-STUCK taxonomy names eight classes (path
/// invalid, scheduler stalled, actor request dropped, target moved, stale
/// observation, interzone failed, physically blocked, reissued same request);
/// this first experiment covers the three the travel brain's frozen state can
/// already separate, and every other shape reads as
/// <see cref="Unknown"/> — out of scope, never mislabelled.
/// </summary>
public enum JevStuckRootCause
{
    /// <summary>Healthy, unarmed, or a class this experiment does not cover.</summary>
    Unknown = 0,

    /// <summary>A moving (unit) target served through a stale snapshot: the probe
    /// is still spending its one re-resolve, the target is provably gone, or a
    /// live follow leg is being re-issued on drift.</summary>
    StalePerceptionPursuit = 1,

    /// <summary>The destination itself would not walk: a failed leg being
    /// re-selected (recovery), or the repath budget spent and the destination
    /// abandoned unreachable.</summary>
    PathInvalid = 2,

    /// <summary>An armed intent producing no verb, no terminal, and no leg
    /// activity over a streak of wakes — the scheduler side went silent, not
    /// the world.</summary>
    SchedulerSilence = 3,
}

/// <summary>
/// One frozen stuck/recovery wake: the travel inputs exactly as recorded, the
/// verdict the live wake reached, and the caller-supplied count of consecutive
/// verb-less wakes behind it. Every field is frozen — the classifier reads no
/// world, no actor, and no clock.
/// </summary>
public readonly record struct JevStuckWake(
    string WakeId,
    TravelBrainInputs Inputs,
    TravelDecision Decision,
    int ConsecutiveSilentWakes);

/// <summary>One corpus row: a frozen stuck/recovery wake plus its fixture
/// expected root-cause label (a test fixture, never a model call).</summary>
public readonly record struct JevLabeledStuckWake(JevStuckWake Wake, JevStuckRootCause Expected);

/// <summary>
/// The experiment-1 classifier (test-side only): a pure function of frozen
/// wake state to <see cref="JevStuckRootCause"/>. Precedence is deliberate —
/// failed-leg evidence outranks target kind (a repathed unit leg names the
/// path cause, not the target), and the silence arm reads history the
/// per-wake telemetry conflates away (an identical Hold verdict is a blip on
/// its first wake and scheduler silence on its third).
/// </summary>
public static class JevStuckClassifier
{
    /// <summary>
    /// Consecutive verb-less wakes at or above which an armed-but-silent intent
    /// counts as scheduler silence. A single silent wake is a blip
    /// (<see cref="JevStuckRootCause.Unknown"/>), never a verdict.
    /// </summary>
    public const int SchedulerSilenceMinStreak = 2;

    public static JevStuckRootCause Classify(in JevStuckWake wake)
    {
        var inputs = wake.Inputs;
        var decision = wake.Decision;

        // 1. Path-invalid: the destination was abandoned unreachable, or a
        // failed leg is being re-selected (the recovery shape of the same cause).
        if (decision.Terminal == TravelTerminal.Unreachable
            || decision.Reason is TravelReason.RepathExhausted or TravelReason.Repathed)
            return JevStuckRootCause.PathInvalid;

        // 2. Stale-perception pursuit: a UNIT target served through a stale
        // snapshot. The reason check carries today's brain; the arm disjuncts
        // keep the probe/follow arms covered if the brain ever names a new
        // reason on them.
        if (inputs.TargetKind == TravelTargetKind.Unit
            && (decision.Reason is TravelReason.TargetUnresolved or TravelReason.TargetGone or TravelReason.DriftRetrack
                || decision.Arm == TravelArm.TargetProbe
                || (decision.Arm == TravelArm.Follow && !inputs.TargetResolved)))
            return JevStuckRootCause.StalePerceptionPursuit;

        // 3. Scheduler-silence: the intent is armed but nothing is asked for,
        // nothing terminated, and no leg is (or was) in flight — over a streak.
        // An unarmed wake is never silence: nothing was scheduled.
        if (inputs.HasIntent
            && !decision.HasVerb
            && decision.Terminal == TravelTerminal.None
            && !inputs.LegLive
            && inputs.LegOutcome == TravelLegOutcome.None
            && wake.ConsecutiveSilentWakes >= SchedulerSilenceMinStreak)
            return JevStuckRootCause.SchedulerSilence;

        return JevStuckRootCause.Unknown;
    }
}

/// <summary>One harness row: the fixture label against the classifier's verdict.</summary>
public sealed record JevStuckRecoveryResult(string WakeId, JevStuckRootCause Expected, JevStuckRootCause Predicted)
{
    /// <summary>True when the classifier agreed with the fixture label.</summary>
    public bool Matched => Expected == Predicted;
}

/// <summary>The comparison report: every row plus the accuracy contract.</summary>
public sealed record JevStuckRecoveryReport(ImmutableArray<JevStuckRecoveryResult> Results)
{
    /// <summary>Rows in the corpus.</summary>
    public int Total => Results.Length;

    /// <summary>Rows where the classifier agreed with the fixture label.</summary>
    public int Correct
    {
        get
        {
            var correct = 0;
            foreach (var result in Results)
            {
                if (result.Matched)
                    correct++;
            }
            return correct;
        }
    }

    /// <summary>Fraction of rows classified as labelled (1.0 on an empty corpus).</summary>
    public double Accuracy => Total == 0 ? 1.0 : (double)Correct / Total;

    /// <summary>Rows carrying the given fixture label.</summary>
    public int TotalFor(JevStuckRootCause cause)
    {
        var total = 0;
        foreach (var result in Results)
        {
            if (result.Expected == cause)
                total++;
        }
        return total;
    }

    /// <summary>Rows carrying the given fixture label that classified correctly.</summary>
    public int CorrectFor(JevStuckRootCause cause)
    {
        var correct = 0;
        foreach (var result in Results)
        {
            if (result.Expected == cause && result.Matched)
                correct++;
        }
        return correct;
    }
}

/// <summary>
/// The STUCK/RECOVERY comparison harness (test-side utility, never
/// production): runs <see cref="JevStuckClassifier"/> over a corpus of frozen
/// stuck/recovery wakes and scores it against the fixture labels. No model
/// calls, no world reads — the offline proof that disagreement mining can
/// separate classes the per-wake telemetry conflates.
/// </summary>
public static class JevStuckRecoveryHarness
{
    public static JevStuckRecoveryReport Run(IReadOnlyList<JevLabeledStuckWake> corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        var builder = ImmutableArray.CreateBuilder<JevStuckRecoveryResult>(corpus.Count);
        foreach (var labeled in corpus)
            builder.Add(new JevStuckRecoveryResult(
                labeled.Wake.WakeId, labeled.Expected, JevStuckClassifier.Classify(labeled.Wake)));
        return new JevStuckRecoveryReport(builder.ToImmutable());
    }
}
