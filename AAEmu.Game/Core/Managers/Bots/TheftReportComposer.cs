using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// J1 theft→footprint→report composer (M9 substrate slice J1): drives the REAL
/// reachable engine seams — <see cref="CrimeManager.GenerateEvidenceFromTheft"/>
/// + <see cref="CrimeManager.ReportCrime"/> — behind a default-OFF static gate,
/// with a canned record path for deterministic unit proof.
///
/// Slice boundaries (hard): NO <c>CrimeManager</c>/<c>TrialManager</c>/
/// <c>Character</c>/packet edits, NO <c>PrisonManager</c>, NO threshold changes
/// (50/3000 locked). Composition only: the live seam methods delegate to the
/// engine's public API; the canned path touches zero singletons.
///
/// Fail-pre honest: the live seam methods compile-reference the engine seams
/// (reachability proof) but are NOT exercised in unit tests — invoking them
/// needs a running world (DoodadManager spawns, CrimeIdManager ids, MySQL).
/// Unit tests prove the canned contract plus the live guards that return
/// before any engine state is touched (gate OFF, null args, foreign/unowned
/// doodad → null fail-closed).
/// </summary>
public static class TheftReportComposer
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "j1-theft-footprint-report";

    /// <summary>
    /// Default-OFF kill switch. The composer is inert unless a caller opts in —
    /// mirroring the R1 composer's "composition only, no tick subscription" stance.
    /// </summary>
    public static bool Enabled { get; set; } = false;

    /// <summary>Seed parameters for a canned theft scenario.</summary>
    public sealed record TheftSeedOptions
    {
        public uint CriminalId { get; init; }
        public uint VictimId { get; init; }
        public uint ReporterId { get; init; }
        public uint DoodadTemplateId { get; init; }
        public short CrimeValue { get; init; } = 5;
        public int? VictimLevel { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Canned footprint: the criminal is preserved as owner (engine footprints
    /// are criminal-owned), the doodad owner as victim, template carried over.
    /// </summary>
    public sealed record TheftFootprint(uint OwnerId, uint VictimId, uint DoodadTemplateId);

    /// <summary>
    /// Canned crime report mirroring the observable <c>CrimeEvent</c> fields the
    /// engine's <c>ReportCrime</c> tail produces (criminal/victim/reporter/kind/value).
    /// </summary>
    public sealed record TheftReport(uint Criminal, uint Victim, uint Reporter, CrimeKind Kind, short CrimeValue, string Message);

    /// <summary>
    /// Canned ledger mirroring the observable contract of the engine
    /// <c>ReportCrime</c> tail: one <c>CrimeEvents</c> entry plus criminal
    /// point accumulation with <c>Character.AddCrime</c> clamping (short ceiling,
    /// zero floor on both crime and infamy points).
    /// </summary>
    public sealed class CannedTheftLedger
    {
        public short CrimePoint { get; private set; }
        public int InfamyPoint { get; private set; }
        public List<TheftReport> Events { get; } = [];

        public void ApplyReport(TheftReport report)
        {
            ArgumentNullException.ThrowIfNull(report);
            var newAmount = CrimePoint + report.CrimeValue;
            CrimePoint = newAmount > short.MaxValue ? short.MaxValue : newAmount < 0 ? (short)0 : (short)newAmount;
            InfamyPoint = Math.Max(0, InfamyPoint + report.CrimeValue);
            Events.Add(report);
        }
    }

    /// <summary>
    /// Composes a canned footprint. Fail-closed: foreign/unowned doodads
    /// (anything not character-owned) yield null, mirroring the engine's
    /// <c>GenerateEvidenceFromTheft</c> owner guard.
    /// </summary>
    public static TheftFootprint? TryComposeFootprint(DoodadOwnerType ownerType, uint ownerId, uint criminalId, uint doodadTemplateId)
    {
        if (ownerType != DoodadOwnerType.Character)
        {
            return null;
        }
        return new TheftFootprint(criminalId, ownerId, doodadTemplateId);
    }

    /// <summary>
    /// Composes a canned footprint from seed options (character-owned by construction).
    /// </summary>
    public static TheftFootprint? TryComposeFootprint(TheftSeedOptions seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return TryComposeFootprint(DoodadOwnerType.Character, seed.VictimId, seed.CriminalId, seed.DoodadTemplateId);
    }

    /// <summary>
    /// Composes a canned theft report, policy-gated: anything the
    /// <see cref="BotWitnessPolicy"/> does not accept as a report yields null.
    /// </summary>
    public static TheftReport? TryComposeReport(TheftSeedOptions seed, BotWitnessPreference preference = BotWitnessPreference.Report)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var verdict = BotWitnessPolicy.Decide(seed.ReporterId, seed.CriminalId, CrimeKind.Theft, evidencePresent: true, preference);
        if (verdict.Decision != BotWitnessDecision.Report)
        {
            return null;
        }
        return new TheftReport(seed.CriminalId, seed.VictimId, seed.ReporterId, CrimeKind.Theft, seed.CrimeValue, seed.Message);
    }

    /// <summary>
    /// Live seam: footprint via the real <c>CrimeManager.GenerateEvidenceFromTheft</c>.
    /// Default-OFF; fail-closed on foreign/unowned doodads before any engine state is touched.
    /// </summary>
    public static Doodad? ComposeLiveFootprint(Character? criminal, Doodad? stolenDoodad)
    {
        if (!Enabled)
        {
            return null;
        }
        if (criminal is null || stolenDoodad is null)
        {
            return null;
        }
        if (stolenDoodad.OwnerType != DoodadOwnerType.Character)
        {
            return null;
        }
        return CrimeManager.Instance.GenerateEvidenceFromTheft(criminal, stolenDoodad);
    }

    /// <summary>
    /// Live seam: theft report via the real <c>CrimeManager.ReportCrime</c>.
    /// Default-OFF; policy-gated (self-report guard) before delegating.
    /// </summary>
    public static CrimeEvent? ReportLiveTheft(Character? reporter, Doodad? evidence, uint usedSkillId, int doodadNextFuncGroup, uint doodadFuncId, string message)
    {
        if (!Enabled)
        {
            return null;
        }
        if (reporter is null || evidence is null)
        {
            return null;
        }
        var verdict = BotWitnessPolicy.Decide(reporter.Id, evidence.OwnerId, CrimeKind.Theft, evidencePresent: true);
        if (verdict.Decision != BotWitnessDecision.Report)
        {
            return null;
        }
        return CrimeManager.Instance.ReportCrime(reporter, evidence, usedSkillId, doodadNextFuncGroup, doodadFuncId, message);
    }
}
