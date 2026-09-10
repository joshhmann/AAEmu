using AAEmu.Game.Models.Game.Crime;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// J1 sentence calculator (M9 substrate slice J1): pure shadow of
/// <c>TrialData.CalculateJailTime</c> (TrialData.cs:85-132) for bot-side
/// prediction. Murder 20 / Theft 8 / Assault 0, sub-30 victim x10, infamy
/// multiplier, pirate flat 40.
///
/// Slice boundaries (hard): NO <c>TrialData</c> diff, NO threshold changes
/// (50/3000 locked), NO <c>PrisonManager</c>, no singleton lookups — pure
/// static math, inert unless a caller drives <see cref="Calculate"/>.
///
/// Integer semantics mirror the engine exactly: the infamy term is
/// <c>(1 + (infamyPoint / 1000))</c> with int division, and the victim guard
/// is <c>victim?.Level &lt; 30</c>, so an unknown (null) victim never
/// multiplies — hence <see cref="SentenceInput.VictimLevel"/> is nullable.
/// </summary>
public static class SentenceCalculator
{
    /// <summary>Flat pirate sentence, mirrors the engine's pirate branch.</summary>
    public const int PirateFlatSentenceMinutes = 40;

    /// <summary>Sub-30 victim score multiplier, mirrors the engine's level-difference penalty.</summary>
    public const int Sub30VictimMultiplier = 10;

    /// <summary>Victim level below which the multiplier applies.</summary>
    public const int LowVictimLevelThreshold = 30;

    /// <summary>One evidence entry: crime kind plus the victim's level (null = unknown victim).</summary>
    public sealed record SentenceInput(CrimeKind Kind, int? VictimLevel);

    /// <summary>
    /// Calculates jail time in minutes, shadowing <c>TrialData.CalculateJailTime</c>.
    /// Deterministic: same inputs always yield the same sentence.
    /// </summary>
    public static int Calculate(IReadOnlyList<SentenceInput> evidence, int infamyPoint, bool isPirate)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var defaultMinutes = 0;
        foreach (var input in evidence)
        {
            var score = input.Kind switch
            {
                CrimeKind.Murder => 20,
                CrimeKind.Theft => 8,
                _ => 0,
            };
            // Lifted `<` on int?: null victim is false — mirrors `victim?.Level < 30`.
            if (input.VictimLevel < LowVictimLevelThreshold)
            {
                score *= Sub30VictimMultiplier;
            }
            defaultMinutes += score;
        }
        var jailTime = defaultMinutes * (1 + (infamyPoint / 1000));
        if (isPirate)
        {
            jailTime = PirateFlatSentenceMinutes;
        }
        return jailTime;
    }
}
