using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// J1 theft→footprint→report→points→sentence slice (M9 substrate slice J1):
/// canned footprint with owner preserved, ledger points + event entry,
/// sentence-table match against the <c>TrialData</c> math, witness policy
/// matrix with self-report guard, determinism. The composer/store under test
/// touch zero singletons on every asserted path; the live engine tail
/// (DoodadManager spawns, CrimeIdManager ids, MySQL) stays behind the
/// default-OFF gate and is NOT exercised here (fail-pre honest).
/// </summary>
public class TheftJusticeTests
{
    private const uint CriminalId = 9;
    private const uint VictimId = 7;
    private const uint ReporterId = 5;

    private static TheftReportComposer.TheftSeedOptions Seed() => new()
    {
        CriminalId = CriminalId,
        VictimId = VictimId,
        ReporterId = ReporterId,
        DoodadTemplateId = 14898,
        CrimeValue = 5,
        VictimLevel = 40,
        Message = "Report #1"
    };

    [Test]
    public async Task ComposeFootprint_OwnedDoodad_FootprintPreservesOwner()
    {
        var footprint = TheftReportComposer.TryComposeFootprint(Seed());

        await Assert.That(footprint).IsNotNull();
        await Assert.That(footprint!.OwnerId).IsEqualTo(CriminalId);
        await Assert.That(footprint.VictimId).IsEqualTo(VictimId);
        await Assert.That(footprint.DoodadTemplateId).IsEqualTo(14898u);

        // Fail-closed: foreign/unowned doodads yield null on the canned path.
        await Assert.That(TheftReportComposer.TryComposeFootprint(DoodadOwnerType.System, VictimId, CriminalId, 14898)).IsNull();
        await Assert.That(TheftReportComposer.TryComposeFootprint(DoodadOwnerType.Housing, VictimId, CriminalId, 14898)).IsNull();

        // Fail-closed on the live seam guards: gate OFF by default, and with the
        // gate on, null args + foreign doodads return before any engine state.
        await Assert.That(TheftReportComposer.Enabled).IsFalse();
        await Assert.That(TheftReportComposer.ComposeLiveFootprint(null, null)).IsNull();
        await Assert.That(TheftReportComposer.ReportLiveTheft(null, null, 0, 0, 0, string.Empty)).IsNull();
        try
        {
            TheftReportComposer.Enabled = true;
            var foreign = new Doodad { OwnerType = DoodadOwnerType.System, OwnerId = VictimId };
            await Assert.That(TheftReportComposer.ComposeLiveFootprint(null, foreign)).IsNull();
            await Assert.That(TheftReportComposer.ComposeLiveFootprint(null, null)).IsNull();
        }
        finally
        {
            TheftReportComposer.Enabled = false;
        }
        await Assert.That(TheftReportComposer.Enabled).IsFalse();
    }

    [Test]
    public async Task ApplyReport_TheftValue_CrimePointsAccumulateWithEventEntry()
    {
        var ledger = new TheftReportComposer.CannedTheftLedger();
        var report = TheftReportComposer.TryComposeReport(Seed());

        await Assert.That(report).IsNotNull();
        ledger.ApplyReport(report!);

        // CrimePoint += CrimeValue, plus one CrimeEvents-mirror entry.
        await Assert.That(ledger.CrimePoint).IsEqualTo((short)5);
        await Assert.That(ledger.InfamyPoint).IsEqualTo(5);
        await Assert.That(ledger.Events.Count).IsEqualTo(1);
        await Assert.That(ledger.Events[0].Criminal).IsEqualTo(CriminalId);
        await Assert.That(ledger.Events[0].Victim).IsEqualTo(VictimId);
        await Assert.That(ledger.Events[0].Reporter).IsEqualTo(ReporterId);
        await Assert.That(ledger.Events[0].Kind).IsEqualTo(CrimeKind.Theft);
        await Assert.That(ledger.Events[0].CrimeValue).IsEqualTo((short)5);

        // Second report accumulates on top of the first.
        ledger.ApplyReport(report!);
        await Assert.That(ledger.CrimePoint).IsEqualTo((short)10);
        await Assert.That(ledger.Events.Count).IsEqualTo(2);

        // Fail-pre: the REAL CrimeManager.AddCrimePoints tail is private and
        // singleton/world-gated, so no unit test can drive it — this canned
        // ledger mirrors its observable contract (entry + clamped +=) only.
    }

    [Test]
    public async Task SentenceCalculator_EngineTable_MatchesTrialDataMath()
    {
        // Base table: Murder 20 / Theft 8 / Assault 0.
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Theft, 40)], 0, false)).IsEqualTo(8);
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Murder, 40)], 0, false)).IsEqualTo(20);
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Assault, 40)], 0, false)).IsEqualTo(0);
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.None, 40)], 0, false)).IsEqualTo(0);

        // Sub-30 victim x10; unknown (null) victim never multiplies (mirrors `victim?.Level < 30`).
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Theft, 12)], 0, false)).IsEqualTo(80);
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Murder, 12)], 0, false)).IsEqualTo(200);
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Theft, null)], 0, false)).IsEqualTo(8);

        // Infamy multiplier uses INT division: 999 -> x1 (float math would give ~x2).
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Theft, 40)], 999, false)).IsEqualTo(8);
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Theft, 40)], 1500, false)).IsEqualTo(16);

        // Pirate flat 40 regardless of evidence or infamy.
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Murder, 12)], 5000, true)).IsEqualTo(40);
        await Assert.That(SentenceCalculator.Calculate([], 0, true)).IsEqualTo(40);

        // Multi-evidence sum then multiply: (20 + 80) x (1 + 500/1000=0) = 100.
        await Assert.That(SentenceCalculator.Calculate([new(CrimeKind.Murder, 40), new(CrimeKind.Theft, 12)], 500, false)).IsEqualTo(100);
    }

    [Test]
    public async Task WitnessPolicy_Matrix_LegalityBeforePreferenceWithSelfReportGuard()
    {
        // Self-report guard: reporter == criminal → Ignore even with Report preference
        // (mirrors the CrimeManager.ReportCrime own-crime guard).
        var self = BotWitnessPolicy.Decide(CriminalId, CriminalId, CrimeKind.Theft, true, BotWitnessPreference.Report);
        await Assert.That(self.Decision).IsEqualTo(BotWitnessDecision.Ignore);
        await Assert.That(string.IsNullOrWhiteSpace(self.Reason)).IsFalse();

        // Invalid kind → Ignore regardless of preference.
        var invalid = BotWitnessPolicy.Decide(ReporterId, CriminalId, CrimeKind.Invalid, true, BotWitnessPreference.Report);
        await Assert.That(invalid.Decision).IsEqualTo(BotWitnessDecision.Ignore);
        await Assert.That(string.IsNullOrWhiteSpace(invalid.Reason)).IsFalse();

        // No evidence yet → Defer even when the witness prefers Report.
        var waiting = BotWitnessPolicy.Decide(ReporterId, CriminalId, CrimeKind.Theft, false, BotWitnessPreference.Report);
        await Assert.That(waiting.Decision).IsEqualTo(BotWitnessDecision.Defer);
        await Assert.That(string.IsNullOrWhiteSpace(waiting.Reason)).IsFalse();

        // Lawful + evidence: preference honored verbatim.
        var report = BotWitnessPolicy.Decide(ReporterId, CriminalId, CrimeKind.Theft, true, BotWitnessPreference.Report);
        await Assert.That(report.Decision).IsEqualTo(BotWitnessDecision.Report);
        var defer = BotWitnessPolicy.Decide(ReporterId, CriminalId, CrimeKind.Theft, true, BotWitnessPreference.Defer);
        await Assert.That(defer.Decision).IsEqualTo(BotWitnessDecision.Defer);
        var decline = BotWitnessPolicy.Decide(ReporterId, CriminalId, CrimeKind.Theft, true, BotWitnessPreference.Ignore);
        await Assert.That(decline.Decision).IsEqualTo(BotWitnessDecision.Ignore);

        // Policy gates the canned composer too: self-report seed composes nothing.
        var selfSeed = Seed() with { ReporterId = CriminalId };
        await Assert.That(TheftReportComposer.TryComposeReport(selfSeed)).IsNull();
    }

    [Test]
    public async Task Slice_SameSeed_DeterministicOutputs()
    {
        static (TheftReportComposer.TheftFootprint? Footprint, TheftReportComposer.TheftReport? Report, int Sentence, BotWitnessVerdict Verdict) Run()
        {
            var seed = Seed();
            var footprint = TheftReportComposer.TryComposeFootprint(seed);
            var report = TheftReportComposer.TryComposeReport(seed);
            var sentence = SentenceCalculator.Calculate([new(CrimeKind.Theft, seed.VictimLevel)], 0, false);
            var verdict = BotWitnessPolicy.Decide(seed.ReporterId, seed.CriminalId, CrimeKind.Theft, true);
            return (footprint, report, sentence, verdict);
        }

        var first = Run();
        var second = Run();

        await Assert.That(first.Footprint).IsNotNull();
        await Assert.That(second.Footprint).IsNotNull();
        await Assert.That(second.Footprint).IsEqualTo(first.Footprint);
        await Assert.That(second.Report).IsEqualTo(first.Report);
        await Assert.That(second.Sentence).IsEqualTo(first.Sentence);
        await Assert.That(second.Sentence).IsEqualTo(8);
        await Assert.That(second.Verdict).IsEqualTo(first.Verdict);
        await Assert.That(second.Verdict.Decision).IsEqualTo(BotWitnessDecision.Report);
    }
}
