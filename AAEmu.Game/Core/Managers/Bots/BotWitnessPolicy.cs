using AAEmu.Game.Models.Game.Crime;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// J1 bot witness decision (M9 substrate slice J1).
/// </summary>
public enum BotWitnessDecision
{
    Report,
    Defer,
    Ignore
}

/// <summary>
/// J1 bot witness preference (M9 substrate slice J1): what the witness would
/// like to do. Preference NEVER overrides legality — see <see cref="BotWitnessPolicy"/>.
/// </summary>
public enum BotWitnessPreference
{
    Report,
    Defer,
    Ignore
}

/// <summary>
/// Fail-closed policy outcome: a decision plus a stable, non-empty reason.
/// </summary>
public sealed record BotWitnessVerdict(BotWitnessDecision Decision, string Reason);

/// <summary>
/// J1 witness policy (M9 substrate slice J1): Report/Defer/Ignore with reason,
/// legality-before-preference. The legality guards run first and ignore the
/// witness's preference entirely:
/// <list type="number">
/// <item>self-report (reporter == criminal) → Ignore, mirrors the
/// <c>CrimeManager.ReportCrime</c> own-crime guard;</item>
/// <item>invalid crime kind → Ignore;</item>
/// <item>no evidence yet → Defer (lawful to wait);</item>
/// <item>otherwise the witness preference is honored verbatim.</item>
/// </list>
/// Pure static math, no singletons — deterministic for identical inputs.
/// </summary>
public static class BotWitnessPolicy
{
    /// <summary>
    /// Decides what a bot witness does about an observed crime.
    /// </summary>
    public static BotWitnessVerdict Decide(uint reporterId, uint criminalId, CrimeKind kind, bool evidencePresent, BotWitnessPreference preference = BotWitnessPreference.Report)
    {
        if (reporterId == criminalId)
        {
            return new BotWitnessVerdict(BotWitnessDecision.Ignore, "self-report refused");
        }
        if (kind == CrimeKind.Invalid)
        {
            return new BotWitnessVerdict(BotWitnessDecision.Ignore, "invalid crime kind");
        }
        if (!evidencePresent)
        {
            return new BotWitnessVerdict(BotWitnessDecision.Defer, "awaiting evidence");
        }
        return preference switch
        {
            BotWitnessPreference.Report => new BotWitnessVerdict(BotWitnessDecision.Report, "lawful report"),
            BotWitnessPreference.Defer => new BotWitnessVerdict(BotWitnessDecision.Defer, "witness defers"),
            _ => new BotWitnessVerdict(BotWitnessDecision.Ignore, "witness declines"),
        };
    }
}
