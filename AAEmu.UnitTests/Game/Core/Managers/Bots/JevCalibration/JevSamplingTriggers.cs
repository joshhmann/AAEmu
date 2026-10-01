#nullable enable

using System.Collections.Immutable;

using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// The sampling trigger set: pure predicates over ALREADY-FROZEN wake state.
/// Every predicate reads only the frozen inputs, the frozen decision, or a
/// caller-supplied history of frozen verdict tokens — never the world, never
/// an actor, never the clock. A firing trigger marks the wake as worth sending
/// to the teacher for review; a silent trigger costs nothing.
/// </summary>
public static class JevSamplingTriggers
{
    /// <summary>Trigger names carried on the disagreement record.</summary>
    public const string TightSpreadTrigger = "tight-spread";
    /// <summary>Trigger names carried on the disagreement record.</summary>
    public const string RetriedTrigger = "retried";
    /// <summary>Trigger names carried on the disagreement record.</summary>
    public const string RecoveryPathTrigger = "recovery-path";
    /// <summary>Trigger names carried on the disagreement record.</summary>
    public const string OscillationTrigger = "oscillation";

    /// <summary>
    /// Score gap at or below which a ranked competition counts as tight: the
    /// winner beat its rival by less than one proximity step
    /// (<see cref="CombatTargetScorer.ProximityWeight"/> is 20/m), so a one-metre
    /// perception wobble could have flipped the commitment.
    /// </summary>
    public const int TightScoreSpreadMaxGap = 50;

    /// <summary>Trailing window the oscillation predicate reads.</summary>
    public const int OscillationWindow = 4;

    // ------------------------------------------------------------ tight spread

    /// <summary>
    /// True when the frozen candidate set holds at least two measured rows
    /// whose ranked scores land within <paramref name="maxGap"/> of each other:
    /// the commitment was ambiguous, so the teacher should see it.
    /// Rows the scorer itself would skip (unmeasured, objId 0) are skipped here
    /// too, so the predicate reads exactly what the decision read.
    /// </summary>
    public static bool TightScoreSpread(in CombatBrainInputs inputs, int maxGap = TightScoreSpreadMaxGap)
    {
        var best = int.MinValue;
        var second = int.MinValue;
        var measured = 0;
        for (var i = 0; i < inputs.Candidates.Count; i++)
        {
            var candidate = inputs.Candidates[i];
            if (!candidate.IsMeasured || candidate.ObjId == 0)
                continue;
            measured++;
            var score = CombatTargetScorer.Score(candidate, inputs.SelfLevel, CombatBrain.AggroBandM);
            if (score > best)
            {
                second = best;
                best = score;
            }
            else if (score > second)
            {
                second = score;
            }
        }
        return measured >= 2 && best - second <= maxGap;
    }

    // ------------------------------------------------------------ repeated retries

    /// <summary>
    /// True when the frozen travel inputs show a retry already happened: a
    /// re-resolve was spent on the moving target or a failed leg was repathed.
    /// </summary>
    public static bool TravelRetried(in TravelBrainInputs inputs)
        => inputs.PriorResolveAttempts > 0 || inputs.PriorRepathCount > 0;

    /// <summary>
    /// True when the frozen needs inputs show a retry already happened: soil
    /// resolve discards or crop approach discards were spent.
    /// </summary>
    public static bool NeedsRetried(in NeedsBrainInputs inputs)
        => inputs.SoilAttempts > 0 || inputs.CropApproachAttempts > 0;

    /// <summary>
    /// True when the frozen combat inputs show a re-competition wake: a live
    /// incumbent whose commitment window elapsed while rivals are present, so
    /// the ranked competition actually ran against it this wake. This mirrors
    /// the decision's own <c>shouldCompete</c> arm: an incumbent with no rivals
    /// to compete with is not a retry.
    /// </summary>
    public static bool CombatRecommitted(in CombatBrainInputs inputs)
        => inputs.IncumbentObjId != 0
           && inputs.IncumbentValid
           && !inputs.CommitmentInForce
           && inputs.Candidates.Count > 0;

    // ------------------------------------------------------------ unseen recovery paths

    /// <summary>
    /// True when the frozen travel decision took a rarely-travelled recovery
    /// arm: the retreat anchor or one of the three abandonment terminals.
    /// </summary>
    public static bool TravelRecoveryPath(in TravelDecision decision)
        => decision.Arm == TravelArm.Retreat
           || decision.Terminal is TravelTerminal.WrongWorld or TravelTerminal.TargetGone or TravelTerminal.Unreachable;

    /// <summary>
    /// True when the frozen survival decision took the recovery demand path
    /// (wounded, no fight on — the verdict the out-of-combat module acts on).
    /// There is no travel-style retry counter on the survival inputs, so this
    /// arm is the survival layer's only sampling signal.
    /// </summary>
    public static bool SurvivalRecoveryPath(in SurvivalBrainDecision decision)
        => decision.Verdict == SurvivalVerdict.Recover;

    /// <summary>
    /// True when the frozen needs decision took a rarely-travelled recovery
    /// arm: the landed-harvest re-evaluation, or a hold after a resolve budget
    /// was spent.
    /// </summary>
    public static bool NeedsRecoveryPath(in NeedsBrainDecision decision)
        => decision.Verdict == NeedsVerdict.Replant
           || decision.Reason is NeedsReason.SoilResolveBudgetSpent or NeedsReason.CropUnreachable;

    /// <summary>
    /// True when the frozen combat decision took a rarely-travelled recovery
    /// arm: the disengage (break contact) or the heal.
    /// </summary>
    public static bool CombatRecoveryPath(in CombatBrainDecision decision)
        => decision.Arm is CombatArm.Disengage or CombatArm.Heal;

    // ------------------------------------------------------------ oscillation

    /// <summary>
    /// True when the trailing verdict-token history flip-flops between exactly
    /// two verdicts (A-B-A...): the wake is oscillating instead of settling.
    /// Reads only the caller-supplied frozen tokens — never a live log.
    /// Needs at least three tokens (A-B-A is the smallest alternation); longer
    /// histories are judged on their trailing <paramref name="window"/>.
    /// </summary>
    public static bool Oscillating(IReadOnlyList<string> recentVerdictTokens, int window = OscillationWindow)
    {
        ArgumentNullException.ThrowIfNull(recentVerdictTokens);
        if (recentVerdictTokens.Count < 3)
            return false;
        var start = Math.Max(0, recentVerdictTokens.Count - Math.Max(window, 3));
        var count = recentVerdictTokens.Count - start;
        if (count < 3)
            return false;
        var first = recentVerdictTokens[start];
        var second = recentVerdictTokens[start + 1];
        if (string.Equals(first, second, StringComparison.Ordinal))
            return false;
        for (var i = start; i < start + count; i++)
        {
            var want = (i - start) % 2 == 0 ? first : second;
            if (!string.Equals(recentVerdictTokens[i], want, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------ collectors

    /// <summary>Collects the trigger names firing for one frozen travel wake.</summary>
    public static ImmutableArray<string> CollectTravel(
        in TravelBrainInputs inputs, in TravelDecision decision, IReadOnlyList<string>? recentVerdictTokens = null)
        => Collect(
            (TravelRetried(inputs), RetriedTrigger),
            (TravelRecoveryPath(decision), RecoveryPathTrigger),
            (recentVerdictTokens is not null && Oscillating(recentVerdictTokens), OscillationTrigger));

    /// <summary>Collects the trigger names firing for one frozen survival wake.</summary>
    public static ImmutableArray<string> CollectSurvival(
        in SurvivalBrainDecision decision, IReadOnlyList<string>? recentVerdictTokens = null)
        => Collect(
            (SurvivalRecoveryPath(decision), RecoveryPathTrigger),
            (recentVerdictTokens is not null && Oscillating(recentVerdictTokens), OscillationTrigger));

    /// <summary>Collects the trigger names firing for one frozen needs wake.</summary>
    public static ImmutableArray<string> CollectNeeds(
        in NeedsBrainInputs inputs, in NeedsBrainDecision decision, IReadOnlyList<string>? recentVerdictTokens = null)
        => Collect(
            (NeedsRetried(inputs), RetriedTrigger),
            (NeedsRecoveryPath(decision), RecoveryPathTrigger),
            (recentVerdictTokens is not null && Oscillating(recentVerdictTokens), OscillationTrigger));

    /// <summary>Collects the trigger names firing for one frozen combat wake.</summary>
    public static ImmutableArray<string> CollectCombat(
        in CombatBrainInputs inputs, in CombatBrainDecision decision, IReadOnlyList<string>? recentVerdictTokens = null)
        => Collect(
            (TightScoreSpread(inputs), TightSpreadTrigger),
            (CombatRecommitted(inputs), RetriedTrigger),
            (CombatRecoveryPath(decision), RecoveryPathTrigger),
            (recentVerdictTokens is not null && Oscillating(recentVerdictTokens), OscillationTrigger));

    private static ImmutableArray<string> Collect(params (bool Fired, string Name)[] triggers)
    {
        var builder = ImmutableArray.CreateBuilder<string>(triggers.Length);
        foreach (var (fired, name) in triggers)
        {
            if (fired)
                builder.Add(name);
        }
        return builder.ToImmutable();
    }
}
