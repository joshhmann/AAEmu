namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// One verb key's verification gate: the status, the gate that proved it, and
/// the evidence artifact that proof lives in. The pair is carried so a plan's
/// <c>UNPROVEN-VERB</c> failure can name the gate that is missing (or the gate
/// that owns the verb but is still Open) — fixture evidence, never a claim.
/// </summary>
public readonly record struct VerbGate(
    string VerbKey,
    VerbGateStatus Status,
    string GateId,
    string EvidencePath)
{
    /// <summary>True when the verb is proven (FROZEN or FRESH) and may be planned.</summary>
    public bool IsGreen => Status is VerbGateStatus.Frozen or VerbGateStatus.Fresh;

    /// <summary>
    /// The gate for a key no gate owns: Open, no gate id, no evidence — the
    /// fail-closed shape <c>UNPROVEN-VERB ... gate=absent evidence=-</c> reports.
    /// </summary>
    public static VerbGate Absent(string? verbKey) => new(verbKey ?? "", VerbGateStatus.Open, "", "");
}
