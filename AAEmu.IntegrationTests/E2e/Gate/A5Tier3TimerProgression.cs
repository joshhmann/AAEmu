using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// A5 (b2) business-state progression for the Tier-3 dormant-timer soak:
/// while the world holds 1,000 dormant specs and 0 embodied, wall-clock
/// engine timers must still advance observable business state — planted
/// crops grow phase → mature, and path-loop transfers keep moving.
///
/// Shape (no engine change — DB-direct + existing bridge only):
///   SETUP  (pre-restart): 2 sunflower canaries through the REAL plant path
///            (real client CSCreateDoodadPacket → CreatePlayerDoodad) + 1
///            travel canary selected from the live 'transfers' dump.
///   SAMPLE (per-sample, fenced): harvest via direct MySQL SELECT on
///            `doodads`, travel via the existing read-only 'transfers'
///            bridge command. Sentinels on read failure, never failed
///            per-sample.
///   END    (once): ValidateTimerProgression beside the DB-writes check,
///            appending to the SAME failures list (passed semantics
///            unchanged).
///   BOUNDED: Probe_A5Tier3RestartConservesDormantTimers covers the boot
///            path (SpawnManager + ApplyLoadedState + InitDoodad re-arm)
///            without the six-hour window.
/// </summary>
public partial class A5Tier3AcceptanceProbeTests
{
    // Sunflower (해바라기) canary — chain verified against the canonical
    // compact.sqlite3 (md5 78b3bdbf038db3b927056106efdf91af):
    //   item_spawn_doodads: seed item 15671 -> doodad almighty 2271
    //     (max_stack_size 100, use_skill 25536 consume_lp 1; group-12 field
    //     crop, climate None so no 0.73 bonus is EXPECTED — but the due time
    //     is derived empirically from the planted row, never assumed).
    //   doodad_phase_funcs: 4391 --Growth 607--> 4504 --Growth 608--> 4505
    //     (doodad_func_growths: 607 delay=1,440,000 next=4504;
    //     608 delay=12,960,000 next=4505).
    //   4505 --Timer 1334--> 10226 (doodad_func_timers: delay=201,600,000;
    //     DoodadFuncTimer does NOT divide by GrowthRate (DoodadFuncTimer.cs),
    //     so the wither fires 201.6M ms = 56 h after maturing at ANY rate —
    //     a matured canary always holds 4505 through the 6 h window).
    //   (Use 1039 -> 4506 carries the harvest loot: 4505 is harvestable.)
    // 6 h leg rate 3 -> due 80 min after plant (~70+ min into the window,
    // leaving restart headroom: a slow boot only eats margin, never the
    // contract); restart leg rate 120 -> first fire 12 s (plant-phase row
    // read is deterministic), total due 120 s (any real restart downtime
    // lapses it, so boot catch-up is guaranteed observable).
    // Rates are explicit (E2E_GROWTH_RATE, E2eStack writes it into the
    // isolated Config.Local.json); the due time itself is derived from the
    // planted row's own plant/growth times, never from the rate.
    private const uint TimerCanarySeedItemId = 15671;
    private const uint TimerCanaryDoodadId = 2271;
    private const int TimerCanaryPlantGroupId = 4391;
    private const int TimerCanaryMatureGroupId = 4505;
    private const double TimerCanaryFirstDelayMs = 1_440_000; // Growth 607
    private const double TimerCanaryTotalDelayMs = 14_400_000; // 607 + 608
    // Total-to-first ratio: due = plant + 10x(first growth - plant).
    private const double TotalToFirstDelayRatio =
        TimerCanaryTotalDelayMs / TimerCanaryFirstDelayMs;
    private const int TimerCanaryCount = 2;
    private const double SixHourGrowthRate = 3.0;
    private const double RestartGrowthRate = 120.0;
    // Legal chain in FORWARD order (index increase = forward; the restart
    // rule rejects backward jumps, not just unknown phases).
    private static readonly int[] TimerCanaryValidPhases = [4391, 4504, 4505];
    // Due contract, minutes past the anchor (review-accepted, never relaxed
    // silently): 60-120 INTO the measured window (post-quiescence rebase);
    // the pre-restart setup bound is coarser ([45,150] past setup) because
    // the setup-to-window gap (restart+boot+discovery+quiescence, ~3-15 min)
    // is not yet measured there.
    private const double WindowDueMinIntoMin = 60;
    private const double WindowDueMaxIntoMin = 120;
    private const double SetupDueMinIntoMin = 45;
    private const double SetupDueMaxIntoMin = 150;
    // Climate shrinks growth delays by float 0.73 (DoodadFuncGrowth.cs:23),
    // so a climate-matching row implies rate/0.73, not ratex0.73.
    private const double ClimateDelayFactor = 0.73;
    // MySQL DATETIME stores whole seconds: on an 8-min first-phase span
    // that is ~0.2% — 2% tolerance is generous without hiding a wrong rate.
    private const double RateRatioTolerance = 0.02;

    private sealed record TimerCanaryPos(double X, double Y, double Z);

    private sealed record HarvestCanarySetup(
        uint DbId, int StartPhase, DateTime PlantUtc, DateTime GrowthUtc, DateTime DueUtc);

    private sealed record TravelCanarySetup(
        ushort TlId, string Name, TimerCanaryPos Pos0,
        double DispMinM, double ObservedMaxM);

    private sealed record TimerCanarySetup(
        HarvestCanarySetup[] Harvest, TravelCanarySetup Travel, double GrowthRate);

    private sealed record CanaryDoodadRow(
        uint DbId, int Phase, DateTime PlantUtc, DateTime GrowthUtc, DateTime PhaseUtc);

    private sealed record HarvestCanaryEnd(
        uint DbId, int StartPhase, DateTime PlantUtc, DateTime StartGrowthUtc, DateTime DueUtc,
        int EndPhase, DateTime EndGrowthUtc, DateTime EndUtc, string? Failure);

    private sealed record TravelCanaryEnd(
        ushort TlId, string Name, TimerCanaryPos? Pos0, TimerCanaryPos? PosEnd,
        double DisplacementM, double InWindowMaxM, int InWindowSamples,
        double DispMinM, double ObservedMaxM, string? Failure);

    private sealed record TimerProgressionEnd(HarvestCanaryEnd[] Harvest, TravelCanaryEnd Travel);

    // Canary doodad ids planted by this run (setup runs inside try, so the
    // probe finally cannot see locals — tracked here for post-run deletion;
    // the ownership cleanup only covers account/character rows).
    private static readonly List<uint> s_timerCanaryDbIds = new();

    // ------------------------------------------------------------------ pure

    private static double DisplacementM(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var dz = z1 - z0;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Pins the LOOSE travel lower bound from the 10-minute setup
    /// observation: proposal 50 m when the transfer shows healthy motion
    /// (≥100 m per 10 min), scaled with observed motion otherwise, 5 m floor
    /// so a frozen transfer still fails while a slow-but-alive one passes.
    /// </summary>
    private static double PinTravelDispMinM(double observedMaxM)
        => observedMaxM >= 100 ? 50 : Math.Clamp(observedMaxM * 0.5, 5, 50);

    /// <summary>EXACT harvest rule: the phase must have changed, the window
    /// must have run past due, and the end phase must be the mature group.
    /// Returns null on pass, else the failure line.</summary>
    private static string? CheckHarvestProgression(
        int startPhase, int endPhase, DateTime endUtc, DateTime dueUtc, int matureGroup)
    {
        if (endPhase == startPhase)
            return $"harvest canary phase unchanged at {endPhase} — growth timer never advanced it";
        if (endUtc < dueUtc)
            return $"harvest canary ended at {endUtc:O} before due {dueUtc:O} — window too short to judge";
        if (endPhase != matureGroup)
            return $"harvest canary ended in phase {endPhase}, expected mature group {matureGroup}";
        return null;
    }

    /// <summary>LOOSE travel lower bound, displacement only: the transfer's
    /// max in-window excursion from its first in-window sighting must clear
    /// the pinned minimum. No path-index guessing — the bridge does not emit
    /// PathPointIndex.</summary>
    private static string? CheckTravelProgression(double inWindowMaxM, double dispMinM)
    {
        if (inWindowMaxM >= dispMinM)
            return null;
        return $"travel canary stalled: in-window max excursion={inWindowMaxM:F1}m < {dispMinM:F1}m";
    }

    /// <summary>Empirical mature-due from the planted row's own times, in the
    /// plant phase (4391): due = plant + 10x(first growth - plant). Absorbs
    /// the live GrowthRate AND any climate factor — no assumed rate.</summary>
    private static DateTime ComputeDueUtc(DateTime plantUtc, DateTime firstGrowthUtc)
        => plantUtc + TimeSpan.FromMilliseconds(
            (firstGrowthUtc - plantUtc).TotalMilliseconds * TotalToFirstDelayRatio);

    /// <summary>Due contract: due must land [minInto,maxInto] minutes past
    /// the anchor (window start for the accepted 60-120 leg; setup time for
    /// the coarser pre-restart bound). Returns null when justified.</summary>
    private static string? CheckDueBand(
        DateTime dueUtc, DateTime anchorUtc, double minIntoMin, double maxIntoMin, string anchorName)
    {
        var intoMin = (dueUtc - anchorUtc).TotalMinutes;
        if (intoMin < minIntoMin || intoMin > maxIntoMin)
            return $"harvest canary due {dueUtc:O} is {intoMin:F1} min past {anchorName} ({anchorUtc:O}) — contract is [{minIntoMin:F0},{maxIntoMin:F0}] min";
        return null;
    }

    /// <summary>Effective-rate cross-check: effective = baseDelay/span, so a
    /// climate-matching row implies rate/0.73, never ratex0.73. Anything
    /// outside bare-or-climate (tight tolerance: DB whole-second timestamps
    /// on minute-scale spans) means the configured override did not take
    /// effect. Fail-fast, never a pass.</summary>
    private static string? CheckRateRatio(double effectiveRate, double fileRate)
    {
        var ratio = effectiveRate / fileRate;
        if (Math.Abs(ratio - 1) <= RateRatioTolerance ||
            Math.Abs(ratio - 1 / ClimateDelayFactor) <= RateRatioTolerance)
            return null;
        return $"planted row implies GrowthRate {effectiveRate:F2} but the live config file says {fileRate:F2} (ratio {ratio:F2}, expected ~1 or ~{1 / ClimateDelayFactor:F2} with climate) — the configured override did not take effect";
    }

    /// <summary>Restart-conservation rule for the BOUNDED probe. The row must
    /// survive with plant_time intact, the end phase must be a STRICTLY
    /// FORWARD step in the canary chain (backward jumps fail, not just
    /// unknown phases), growth_time must be recomputed (re-arm proof), and
    /// the restart must have finished at/after due (else catch-up could not
    /// have fired). A still-pending timer returns a consistency report that
    /// is NEVER a pass — only an observed forward transition passes.</summary>
    private static string? CheckRestartConservation(
        int phase0, DateTime plant0, DateTime growth0, DateTime read0Utc,
        int phase1, DateTime plant1, DateTime growth1, DateTime read1Utc,
        DateTime dueUtc, int[] phaseChain)
    {
        if (plant1 != plant0)
            return $"canary plant_time changed across restart ({plant0:O} -> {plant1:O}) — row not preserved";
        var i0 = Array.IndexOf(phaseChain, phase0);
        var i1 = Array.IndexOf(phaseChain, phase1);
        if (i0 < 0)
            return $"canary start phase {phase0} is not a legal canary phase — expected one of [{string.Join(",", phaseChain)}]";
        if (i1 < 0)
            return $"canary phase {phase1} after restart is not a legal canary phase — expected one of [{string.Join(",", phaseChain)}]";
        if (read1Utc < dueUtc)
            return $"restart finished at {read1Utc:O} before due {dueUtc:O} — catch-up could not have fired across the downtime";
        if (i1 <= i0)
        {
            if (i1 < i0)
                return $"canary phase moved BACKWARD {phase0} -> {phase1} across restart — row corrupted";
            if (growth1 != growth0)
                return $"canary growth_time rewritten without a phase change ({growth0:O} -> {growth1:O})";
            var left0 = (growth0 - read0Utc).TotalMilliseconds;
            var left1 = (growth1 - read1Utc).TotalMilliseconds;
            if (left1 <= 0)
                return "canary growth timer expired across restart without firing — catch-up lost";
            if (!(left1 < left0))
                return $"canary TimeLeft did not decrease across restart ({left0:F0}ms -> {left1:F0}ms)";
            return $"canary still pending in {phase0} after restart with a consistent remainder ({left0:F0}ms -> {left1:F0}ms) — consistency only: NO forward transition observed, leg fails";
        }
        if (growth1 == growth0)
            return $"canary phase changed {phase0} -> {phase1} but growth_time was not recomputed — timer did not re-arm";
        return null; // forward step past due with a recomputed timer = observed catch-up
    }

    // ------------------------------------------------------------------ facts

    [Fact]
    public void TimerProgression_Harvest_PassesOnExactMature()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.Null(CheckHarvestProgression(
            4391, TimerCanaryMatureGroupId, due.AddHours(5), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Harvest_FailsWhenPhaseUnchanged()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CheckHarvestProgression(
            4391, 4391, due.AddHours(5), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Harvest_FailsWhenEndingBeforeDue()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CheckHarvestProgression(
            4391, TimerCanaryMatureGroupId, due.AddMinutes(-1), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Harvest_FailsWhenWrongMatureGroup()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CheckHarvestProgression(
            4391, 4504, due.AddHours(5), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Travel_PassesOnDisplacement()
    {
        Assert.Null(CheckTravelProgression(120, 50));
    }

    [Fact]
    public void TimerProgression_Travel_FailsWhenFrozen()
    {
        Assert.NotNull(CheckTravelProgression(0.3, 50));
        Assert.NotNull(CheckTravelProgression(4.9, 50));
    }

    [Fact]
    public void TimerProgression_DispMin_PinnedFromObservation()
    {
        Assert.Equal(50, PinTravelDispMinM(300)); // healthy motion: proposal
        Assert.Equal(50, PinTravelDispMinM(100));
        Assert.Equal(30, PinTravelDispMinM(60)); // slow-but-alive scales down
        Assert.Equal(5, PinTravelDispMinM(0)); // frozen: floor keeps failing it
        Assert.Equal(5, PinTravelDispMinM(2));
    }

    [Fact]
    public void TimerSizing_PinnedRate_FallsInsideWindow()
    {
        // The 6h leg pins SixHourGrowthRate: due must land 60-120 min after
        // plant. Guards the ms-vs-s unit error that sized 4 s as 67 min.
        var dueMin = TimerCanaryTotalDelayMs / SixHourGrowthRate / 60000.0;
        Assert.InRange(dueMin, 60, 120);
    }

    [Fact]
    public void TimerSizing_RestartRate_LapsesAcrossRestart()
    {
        // The restart leg pins RestartGrowthRate so the first fire (12 s)
        // lands after the plant-phase row read but the total due (~120 s)
        // always lapses inside the restart downtime: boot catch-up is
        // guaranteed observable, never maturing during setup.
        var firstSec = TimerCanaryFirstDelayMs / RestartGrowthRate / 1000.0;
        var dueSec = TimerCanaryTotalDelayMs / RestartGrowthRate / 1000.0;
        Assert.InRange(firstSec, 5, 60);
        Assert.InRange(dueSec, 60, 300);
    }

    [Fact]
    public void TimerDue_Empirical_IsTenTimesFirstPhase()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(plant.AddMinutes(80), ComputeDueUtc(plant, plant.AddMinutes(8)));
    }

    [Fact]
    public void TimerDueBand_EnforcesIntoWindowContract()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Accepted contract: 60-120 min INTO the window.
        Assert.NotNull(CheckDueBand(start.AddSeconds(4), start, WindowDueMinIntoMin, WindowDueMaxIntoMin, "window start"));
        Assert.NotNull(CheckDueBand(start.AddMinutes(59), start, WindowDueMinIntoMin, WindowDueMaxIntoMin, "window start"));
        Assert.Null(CheckDueBand(start.AddMinutes(75), start, WindowDueMinIntoMin, WindowDueMaxIntoMin, "window start"));
        Assert.Null(CheckDueBand(start.AddMinutes(120), start, WindowDueMinIntoMin, WindowDueMaxIntoMin, "window start"));
        Assert.NotNull(CheckDueBand(start.AddMinutes(121), start, WindowDueMinIntoMin, WindowDueMaxIntoMin, "window start"));
        // Coarser pre-restart setup bound (gap not yet measured).
        Assert.NotNull(CheckDueBand(start.AddSeconds(4), start, SetupDueMinIntoMin, SetupDueMaxIntoMin, "setup"));
        Assert.Null(CheckDueBand(start.AddMinutes(80), start, SetupDueMinIntoMin, SetupDueMaxIntoMin, "setup"));
        Assert.NotNull(CheckDueBand(start.AddMinutes(200), start, SetupDueMinIntoMin, SetupDueMaxIntoMin, "setup"));
    }

    [Fact]
    public void TimerRateRatio_AcceptsBareAndClimateFactors()
    {
        Assert.Null(CheckRateRatio(3.0, 3.0));
        // Climate shrinks the span: effective = base/span = rate/0.73.
        Assert.Null(CheckRateRatio(3.0 / 0.73, 3.0));
        // The OLD inverted branch (rate x 0.73) is a wrong rate, not climate.
        Assert.NotNull(CheckRateRatio(3.0 * 0.73, 3.0));
        Assert.NotNull(CheckRateRatio(3600.0, 3.0)); // override never applied
    }

    [Fact]
    public void TimerRateRatio_ToleratesSecondPrecisionTimestamps()
    {
        // DB whole-second quantization on an 8-min span: 479 s observed vs
        // 480 s true is 0.2% — passes; a 10 s skew (2.1%) fails.
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var spanOk = (plant.AddSeconds(479) - plant).TotalMilliseconds;
        Assert.Null(CheckRateRatio(TimerCanaryFirstDelayMs / spanOk, 3.0));
        var spanBad = (plant.AddSeconds(470) - plant).TotalMilliseconds;
        Assert.NotNull(CheckRateRatio(TimerCanaryFirstDelayMs / spanBad, 3.0));
    }

    [Fact]
    public void TimerResolve_ResolvesByNameOnly()
    {
        using var doc = JsonDocument.Parse("""
            {"transfers": [
              {"tlId": 5, "name": "Alpha", "position": {"x": 1, "y": 2, "z": 3}},
              {"tlId": 7, "name": "Beta", "position": {"x": 10, "y": 20, "z": 30}}
            ]}
            """);
        Assert.Equal(new TimerCanaryPos(10, 20, 30), ResolveTravelPos(doc.RootElement, "Beta"));
        Assert.Null(ResolveTravelPos(doc.RootElement, "Zzz"));
    }

    [Fact]
    public void TimerResolve_IgnoresReassignedTlId()
    {
        // After a restart the old tlId names a DIFFERENT vehicle: tlId-only
        // tracking would follow the wrong one. Name-only resolve refuses.
        using var doc = JsonDocument.Parse("""
            {"transfers": [
              {"tlId": 5, "name": "Gamma", "position": {"x": 1, "y": 2, "z": 3}},
              {"tlId": 9, "name": "Beta", "position": {"x": 10, "y": 20, "z": 30}}
            ]}
            """);
        Assert.Equal(new TimerCanaryPos(10, 20, 30), ResolveTravelPos(doc.RootElement, "Beta"));
        Assert.Null(ResolveTravelPos(doc.RootElement, "Kappa"));
    }

    [Fact]
    public void TimerResolve_DuplicateName_ResolvesNothing()
    {
        // A duplicate stable name after setup is ambiguity, not evidence:
        // resolve nothing (setup itself fails on != 1 hits).
        using var doc = JsonDocument.Parse("""
            {"transfers": [
              {"tlId": 5, "name": "Alpha", "position": {"x": 1, "y": 2, "z": 3}},
              {"tlId": 6, "name": "Alpha", "position": {"x": 4, "y": 5, "z": 6}}
            ]}
            """);
        Assert.Null(ResolveTravelPos(doc.RootElement, "Alpha"));
        Assert.Equal(2, CountNameOccurrences(doc.RootElement, "Alpha"));
        Assert.Equal(0, CountNameOccurrences(doc.RootElement, "Zzz"));
    }

    [Fact]
    public void TimerRestart_PendingNeverPasses()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddMinutes(10);
        var due = plant.AddMinutes(6.7);
        // Consistent remainder, restart finished past due — still NOT a
        // pass: no forward transition was observed, only wallclock
        // subtraction on one row.
        var reason = CheckRestartConservation(
            4391, plant, growth, plant.AddMinutes(1),
            4391, plant, growth, plant.AddMinutes(7),
            due, TimerCanaryValidPhases);
        Assert.NotNull(reason);
        Assert.Contains("NO forward transition", reason);
    }

    [Fact]
    public void TimerRestart_CatchUpPasses()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var due = plant.AddMinutes(6.7);
        Assert.Null(CheckRestartConservation(
            4391, plant, plant.AddMinutes(6.7), plant.AddMinutes(1),
            4504, plant, plant.AddMinutes(66.7), plant.AddMinutes(10),
            due, TimerCanaryValidPhases));
    }

    [Fact]
    public void TimerRestart_FailsWhenRowRewrittenOrExpired()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddMinutes(6.7);
        var due = plant.AddMinutes(6.7);
        Assert.NotNull(CheckRestartConservation(
            4391, plant, growth, plant.AddMinutes(1),
            4391, plant.AddSeconds(1), growth, plant.AddMinutes(4),
            due, TimerCanaryValidPhases));
        Assert.NotNull(CheckRestartConservation(
            4391, plant, growth, plant.AddMinutes(1),
            4391, plant, growth, plant.AddMinutes(7),
            due, TimerCanaryValidPhases));
    }

    [Fact]
    public void TimerRestart_ArbitraryPhaseJump_Fails()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddSeconds(4);
        var due = plant.AddSeconds(4);
        Assert.NotNull(CheckRestartConservation(
            4391, plant, growth, plant.AddSeconds(1),
            9999, plant, growth.AddHours(1), plant.AddMinutes(4),
            due, TimerCanaryValidPhases));
    }

    [Fact]
    public void TimerRestart_BackwardJump_Fails()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddMinutes(66.7);
        var due = plant.AddMinutes(6.7);
        // 4505 -> 4391 with recomputed growth passed the old
        // validPhases.Contains check — forward order rejects it.
        Assert.NotNull(CheckRestartConservation(
            4505, plant, growth, plant.AddMinutes(1),
            4391, plant, plant.AddMinutes(6.7), plant.AddMinutes(70),
            due, TimerCanaryValidPhases));
    }

    [Fact]
    public void TimerRestart_FinishedBeforeDue_Fails()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var due = plant.AddMinutes(120);
        // Even a forward phase step proves nothing when the restart
        // finished before due: catch-up could not have fired.
        Assert.NotNull(CheckRestartConservation(
            4391, plant, plant.AddMinutes(6.7), plant.AddMinutes(1),
            4504, plant, plant.AddMinutes(66.7), plant.AddMinutes(10),
            due, TimerCanaryValidPhases));
    }

    [Fact]
    public void TimerRestart_PhaseChangeWithoutRecompute_Fails()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddSeconds(4);
        var due = plant.AddSeconds(4);
        Assert.NotNull(CheckRestartConservation(
            4391, plant, growth, plant.AddSeconds(1),
            4504, plant, growth, plant.AddMinutes(4),
            due, TimerCanaryValidPhases));
    }

    /// <summary>Pins the isolated-run growth rate BEFORE EnsureUp writes the
    /// game Config.Local.json (E2eStack rewrites it every boot). An operator
    /// export wins; otherwise the leg's explicit default is set and logged.
    /// Must run before EnsureUp — afterwards it only affects the NEXT boot.
    /// Returns the previous value for finally-restore (no cross-test leak).</summary>
    private static string? PinGrowthRateEnv(double want, string leg)
    {
        var cur = Environment.GetEnvironmentVariable("E2E_GROWTH_RATE");
        if (string.IsNullOrWhiteSpace(cur))
        {
            Environment.SetEnvironmentVariable("E2E_GROWTH_RATE",
                want.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine($"[a5t3] {leg}: E2E_GROWTH_RATE unset -> default {want} (isolated E2E root only; operator export wins)");
        }
        else
        {
            Console.WriteLine($"[a5t3] {leg}: E2E_GROWTH_RATE operator override {cur} (leg wants {want}) — compatibility verified empirically at setup");
        }
        return cur;
    }

    private static void RestoreGrowthRateEnv(string? previous, string leg)
    {
        Environment.SetEnvironmentVariable("E2E_GROWTH_RATE", previous);
        Console.WriteLine($"[a5t3] {leg}: E2E_GROWTH_RATE restored to {(previous ?? "<unset>")}");
    }

    private static double ReadLiveGrowthRate()
    {
        // Strict: EnsureUp ALWAYS writes this file, so a missing/unparseable
        // rate is broken infra — fail clearly, never a silent fallback rate.
        var path = Path.Combine(E2eStack.RuntimeGameDir, "Config.Local.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rate = doc.RootElement.GetProperty("World").GetProperty("GrowthRate").GetDouble();
        if (!double.IsFinite(rate) || rate <= 0)
            throw new InvalidOperationException(
                $"live Config.Local.json World.GrowthRate={rate} is not positive finite ({path})");
        return rate;
    }

    private static JsonElement BridgeCall(string json, int timeoutMs = 15000)
    {
        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        return bridge.Call(json, timeoutMs);
    }

    private static async Task<TimerCanarySetup> SetupTimerCanariesAsync(CancellationToken cancellationToken)
    {
        s_timerCanaryDbIds.Clear();
        // Travel FIRST (11 min observation), THEN plant just before the
        // restart, so the plant-phase row read is clean and the
        // plant-to-window gap is only restart+boot+quiescence.
        var travel = await SelectTravelCanaryAsync(cancellationToken);
        Console.WriteLine($"[a5t3-sixhour] travel canary tlId={travel.TlId} ({travel.Name}) " +
                          $"pos0=({travel.Pos0.X:F0},{travel.Pos0.Y:F0},{travel.Pos0.Z:F0}) " +
                          $"dispMin={travel.DispMinM:F0}m (observed {travel.ObservedMaxM:F0}m/10min)");

        var fileRate = ReadLiveGrowthRate();
        var planted = await PlantTimerCanariesAsync(TimerCanaryCount, cancellationToken);

        var harvest = planted.Select(p =>
        {
            if (p.StartPhase != TimerCanaryPlantGroupId)
                throw new InvalidOperationException(
                    $"harvest canary dbId={p.DbId} planted in phase {p.StartPhase}, expected plant group {TimerCanaryPlantGroupId} — the empirical due derivation needs the chain start");
            var spanMs = (p.GrowthUtc - p.PlantUtc).TotalMilliseconds;
            if (spanMs <= 0)
                throw new InvalidOperationException(
                    $"harvest canary dbId={p.DbId} has non-positive first-phase span ({spanMs:F0}ms) — cannot derive a due time");
            var effectiveRate = TimerCanaryFirstDelayMs / spanMs;
            var ratio = CheckRateRatio(effectiveRate, fileRate);
            if (ratio != null)
                throw new InvalidOperationException(ratio);
            var due = ComputeDueUtc(p.PlantUtc, p.GrowthUtc);
            var band = CheckDueBand(due, DateTime.UtcNow,
                SetupDueMinIntoMin, SetupDueMaxIntoMin, "setup");
            if (band != null)
                throw new InvalidOperationException(band);
            Console.WriteLine($"[a5t3-sixhour] harvest canary dbId={p.DbId} phase0={p.StartPhase} " +
                              $"plant={p.PlantUtc:O} due={due:O} " +
                              $"({(due - p.PlantUtc).TotalMinutes:F1} min; effective rate {effectiveRate:F2} vs file {fileRate:F2})");
            return new HarvestCanarySetup(p.DbId, p.StartPhase, p.PlantUtc, p.GrowthUtc, due);
        }).ToArray();
        return new TimerCanarySetup(harvest, travel, fileRate);
    }

    private sealed record PlantedTimerCanary(uint DbId, int StartPhase, DateTime PlantUtc, DateTime GrowthUtc);

    /// <summary>Plants canaries through the REAL plant path: the planter
    /// (human session) stocks seeds via the bridge, items are flushed with
    /// the existing 'save' command, instance ids come from DB-direct, and
    /// each seed is placed with a real CSCreateDoodadPacket over the
    /// planter's own authenticated link. Disconnects before returning.</summary>
    private static async Task<List<PlantedTimerCanary>> PlantTimerCanariesAsync(
        int count, CancellationToken cancellationToken)
    {
        using var planter = await ConnectHumanAsync();
        try
        {
            var pos = BridgeCall(
                "{\"cmd\":\"drive\",\"bot\":\"" + HumanChar + "\",\"op\":\"charPos\"}");
            var px = pos.GetProperty("x").GetSingle();
            var py = pos.GetProperty("y").GetSingle();
            var pz = pos.GetProperty("z").GetSingle();

            BridgeCall("{\"cmd\":\"drive\",\"bot\":\"" + HumanChar +
                       "\",\"op\":\"stock\",\"item\":" + TimerCanarySeedItemId + ",\"count\":" + count + "}");
            var inv = BridgeCall("{\"cmd\":\"drive\",\"bot\":\"" + HumanChar +
                                 "\",\"op\":\"invCount\",\"item\":" + TimerCanarySeedItemId + "}");
            if (inv.GetProperty("count").GetInt32() < count)
                throw new InvalidOperationException(
                    $"planter bag holds {inv.GetProperty("count").GetInt32()} seeds, need {count}");

            // Flush the stocked rows to MySQL (DoSave persists dirty item
            // containers) so the DB-direct instance-id read below sees them.
            BridgeCall("{\"cmd\":\"save\"}", 60000);

            var charId = ReadPlanterCharacterId();
            // Stocked seeds stack (15671 max_stack_size 100): count=2 yields
            // ONE row with count 2, while invCount reports the total. The
            // engine consumes 1 per CreatePlayerDoodad, so one stack id
            // serves every plant of this run.
            var seedItemId = await ReadSeedStackItemIdAsync(charId, count, cancellationToken);
            var maxDoodadId = ReadMaxDoodadId();

            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                planter.SendCreateDoodad(
                    TimerCanaryDoodadId, px + 4 * (i + 1), py, pz, seedItemId);
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            List<CanaryDoodadRow> rows = new();
            while (DateTime.UtcNow < deadline)
            {
                rows = FindPlantedCanaries(maxDoodadId, charId);
                if (rows.Count >= count)
                    break;
                await Task.Delay(2000, cancellationToken);
            }
            if (rows.Count < count)
                throw new InvalidOperationException(
                    $"planted {count} canaries through the real path but only {rows.Count} doodad rows " +
                    $"appeared (template {TimerCanaryDoodadId} newer than id {maxDoodadId}) — " +
                    "suspect labor gate or seed consumption");

            // Track every discovered row IMMEDIATELY: a later per-row
            // validation may throw, and the finally must still delete
            // partial plantings.
            foreach (var row in rows.Take(count))
                s_timerCanaryDbIds.Add(row.DbId);

            var planted = new List<PlantedTimerCanary>();
            foreach (var row in rows.Take(count))
            {
                if (row.Phase == TimerCanaryMatureGroupId)
                    throw new InvalidOperationException(
                        $"canary doodad {row.DbId} planted already mature (phase {row.Phase}) — " +
                        "the EXACT rule needs a start phase below mature");
                planted.Add(new PlantedTimerCanary(row.DbId, row.Phase, row.PlantUtc, row.GrowthUtc));
            }
            return planted;
        }
        finally
        {
            planter.Disconnect();
        }
    }

    private static uint ReadPlanterCharacterId()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c.id FROM characters c " +
                          "JOIN aaemu_login.users u ON u.id = c.account_id " +
                          "WHERE u.username = @u LIMIT 1";
        cmd.Parameters.AddWithValue("@u", HumanAccount);
        var value = cmd.ExecuteScalar()
            ?? throw new InvalidOperationException("planter character row missing for account " + HumanAccount);
        return Convert.ToUInt32(value);
    }

    /// <summary>Newest seed stack for the planter with a quantity guard: the
    /// engine consumes 1 per CreatePlayerDoodad from the named stack, so the
    /// newest row's own count must cover every plant of this run — any
    /// newest row will not do.</summary>
    private static async Task<ulong> ReadSeedStackItemIdAsync(
        uint charId, int needed, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, `count` FROM items WHERE template_id = @t AND `owner` = @o " +
                              "ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@t", TimerCanarySeedItemId);
            cmd.Parameters.AddWithValue("@o", charId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var id = reader.GetUInt64(0);
                var count = reader.GetInt32(1);
                if (count >= needed)
                    return id;
                throw new InvalidOperationException(
                    $"newest seed {TimerCanarySeedItemId} stack (item {id}) holds {count}, need {needed} plants — refusing to plant short");
            }
            // The save pass may not have flushed yet — trigger one more.
            BridgeCall("{\"cmd\":\"save\"}", 60000);
            await Task.Delay(2000, cancellationToken);
        }
        throw new InvalidOperationException(
            $"no stocked seed {TimerCanarySeedItemId} row for character {charId} after two save flushes");
    }

    private static uint ReadMaxDoodadId()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM doodads";
        return Convert.ToUInt32(cmd.ExecuteScalar());
    }

    private static List<CanaryDoodadRow> FindPlantedCanaries(uint minId, uint ownerId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        // Owned rows only (CreatePlayerDoodad stamps OwnerId = planter
        // character): template+maxId alone would also match stale rows from
        // a crashed run or another lane's crops.
        cmd.CommandText = "SELECT id, current_phase_id, plant_time, growth_time, phase_time " +
                          "FROM doodads WHERE template_id = @t AND id > @min AND owner_id = @o ORDER BY id";
        cmd.Parameters.AddWithValue("@t", TimerCanaryDoodadId);
        cmd.Parameters.AddWithValue("@min", minId);
        cmd.Parameters.AddWithValue("@o", ownerId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<CanaryDoodadRow>();
        while (reader.Read())
        {
            rows.Add(new CanaryDoodadRow(
                reader.GetUInt32(0),
                reader.GetInt32(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
        }
        return rows;
    }

    private static CanaryDoodadRow? ReadCanaryDoodadRow(uint dbId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, current_phase_id, plant_time, growth_time, phase_time " +
                          "FROM doodads WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", dbId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new CanaryDoodadRow(
            reader.GetUInt32(0),
            reader.GetInt32(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    private static void DeleteTimerCanaryDoodads()
    {
        if (s_timerCanaryDbIds.Count == 0)
            return;
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < s_timerCanaryDbIds.Count; i++)
            {
                var name = $"@id{i}";
                cmd.Parameters.AddWithValue(name, s_timerCanaryDbIds[i]);
                names.Add(name);
            }
            cmd.CommandText = $"DELETE FROM doodads WHERE id IN ({string.Join(", ", names)})";
            var removed = cmd.ExecuteNonQuery();
            Console.WriteLine($"[a5t3-sixhour] removed {removed} timer-canary doodad rows");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[a5t3-sixhour] canary doodad cleanup skipped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            s_timerCanaryDbIds.Clear();
        }
    }

    // ------------------------------------------------------------------ travel

    private static Dictionary<ushort, (string Name, TimerCanaryPos Pos)> ParseTransferPositions(JsonElement dump)
    {
        var result = new Dictionary<ushort, (string, TimerCanaryPos)>();
        if (!dump.TryGetProperty("transfers", out var transfers) ||
            transfers.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("transfers bridge dump has no 'transfers' array");
        foreach (var t in transfers.EnumerateArray())
        {
            if (!t.TryGetProperty("tlId", out var tl) ||
                !t.TryGetProperty("position", out var p) ||
                !p.TryGetProperty("x", out var x) ||
                !p.TryGetProperty("y", out var y) ||
                !p.TryGetProperty("z", out var z))
                continue;
            var tlId = tl.GetUInt16();
            if (result.ContainsKey(tlId))
                continue; // first entry wins (matches the boarding resolve order)
            var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            result[tlId] = (name, new TimerCanaryPos(x.GetSingle(), y.GetSingle(), z.GetSingle()));
        }
        return result;
    }

    /// <summary>Resolves the travel canary from a transfers dump by stable
    /// NAME only: exactly one nonempty-name match resolves, anything else
    /// (missing, empty, or duplicate after setup) is explicit unavailable
    /// evidence — never an unrelated position. The pre-restart tlId is
    /// report metadata only: TlIdManager reissues runtime ids per boot, so
    /// a tlId fallback would track the wrong vehicle after a restart.</summary>
    private static TimerCanaryPos? ResolveTravelPos(JsonElement dump, string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        TimerCanaryPos? found = null;
        var hits = 0;
        foreach (var t in dump.GetProperty("transfers").EnumerateArray())
        {
            if (!t.TryGetProperty("name", out var n) ||
                !string.Equals(n.GetString(), name, StringComparison.Ordinal))
                continue;
            if (!t.TryGetProperty("position", out var p) ||
                !p.TryGetProperty("x", out var x) ||
                !p.TryGetProperty("y", out var y) ||
                !p.TryGetProperty("z", out var z))
                continue;
            hits++;
            found = new TimerCanaryPos(x.GetSingle(), y.GetSingle(), z.GetSingle());
        }
        return hits == 1 ? found : null;
    }

    /// <summary>Counts transfers carrying a name in a transfers dump.
    /// Setup requires exactly 1: duplicate names must fail setup rather
    /// than first-wins resolving the wrong vehicle.</summary>
    private static int CountNameOccurrences(JsonElement dump, string name)
    {
        var count = 0;
        foreach (var t in dump.GetProperty("transfers").EnumerateArray())
        {
            if (t.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), name, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static TimerCanaryPos? ReadTravelCanary(string name)
    {
        var dump = BridgeCall("{\"cmd\":\"transfers\"}");
        return ResolveTravelPos(dump, name);
    }

    private static async Task<TravelCanarySetup> SelectTravelCanaryAsync(CancellationToken cancellationToken)
    {
        var first = ParseTransferPositions(BridgeCall("{\"cmd\":\"transfers\"}"));
        await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
        var secondDump = BridgeCall("{\"cmd\":\"transfers\"}");
        var second = ParseTransferPositions(secondDump);

        ushort chosen = 0;
        var bestDisp = 0.0;
        string chosenName = "";
        foreach (var (tlId, entry) in second)
        {
            if (!first.TryGetValue(tlId, out var prev))
                continue;
            var disp = DisplacementM(prev.Pos.X, prev.Pos.Y, prev.Pos.Z, entry.Pos.X, entry.Pos.Y, entry.Pos.Z);
            if (disp < 1)
                continue; // held at a path stop — not a motion canary
            if (chosen == 0 || disp > bestDisp || (disp == bestDisp && tlId < chosen))
            {
                chosen = tlId;
                bestDisp = disp;
                chosenName = entry.Name;
            }
        }
        if (chosen == 0)
            throw new InvalidOperationException(
                $"no moving transfer found for the travel canary ({second.Count} dumped, none displaced >1m in 60s)");
        // The stable identity is the NAME (tlIds are reissued per boot):
        // it must be unambiguous, else setup fails rather than tracking
        // the wrong vehicle.
        var nameHits = CountNameOccurrences(secondDump, chosenName);
        if (nameHits != 1)
            throw new InvalidOperationException(
                $"travel canary name '{chosenName}' (tlId={chosen}) occurs {nameHits}x in the transfers dump — ambiguous stable identity, refusing to track");
        var pos0 = second[chosen].Pos;
        Console.WriteLine($"[a5t3-sixhour] travel canary candidate tlId={chosen} ({chosenName}) " +
                          $"moving {bestDisp:F1}m/60s — observing 10 min to pin DISP_MIN");

        // 10-minute observation: track the max displacement from pos0 so
        // DISP_MIN is pinned from this transfer's own path speed.
        var observedMax = 0.0;
        var observeUntil = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < observeUntil)
        {
            var remaining = observeUntil - DateTime.UtcNow;
            await Task.Delay(remaining < TimeSpan.FromSeconds(60) ? remaining : TimeSpan.FromSeconds(60),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var current = ParseTransferPositions(BridgeCall("{\"cmd\":\"transfers\"}"));
            if (current.TryGetValue(chosen, out var entry))
            {
                var disp = DisplacementM(pos0.X, pos0.Y, pos0.Z, entry.Pos.X, entry.Pos.Y, entry.Pos.Z);
                if (disp > observedMax)
                    observedMax = disp;
            }
        }
        return new TravelCanarySetup(chosen, chosenName,
            new TimerCanaryPos(pos0.X, pos0.Y, pos0.Z),
            PinTravelDispMinM(observedMax), observedMax);
    }
    // -------------------------------------------------------------- validation

    /// <summary>Called ONCE at the end of the soak, beside the DB-writes
    /// check, appending to the SAME failures list. Harvest rule is EXACT
    /// (phase changed + window ran past the rebased due + end phase is the
    /// mature group); travel rule is the LOOSE lower bound (max in-window
    /// excursion past the pinned DISP_MIN). Travel is measured ONLY from
    /// post-restart window samples of the same stable identity (name-first
    /// resolve): pre-restart positions never enter the verdict, so a loop
    /// return cannot false-fail and a respawn reset cannot false-pass.</summary>
    private static TimerProgressionEnd ValidateTimerProgression(
        TimerCanarySetup canaries, IReadOnlyList<DormantTimerSample> samples, List<string> failures)
    {
        var endUtc = DateTime.UtcNow;
        var harvestEnds = new List<HarvestCanaryEnd>();
        foreach (var c in canaries.Harvest)
        {
            CanaryDoodadRow? row = null;
            string? readError = null;
            try
            {
                row = ReadCanaryDoodadRow(c.DbId);
            }
            catch (Exception ex)
            {
                readError = $"harvest canary doodad {c.DbId} unreadable at end of soak: {ex.GetType().Name}: {ex.Message}";
            }
            string? failure = readError
                ?? (row is null
                    ? $"harvest canary doodad {c.DbId} has no row at end of soak — persistence lost"
                    : CheckHarvestProgression(
                        c.StartPhase, row.Phase, endUtc, c.DueUtc, TimerCanaryMatureGroupId));
            if (failure != null)
                AddFailure(failures, failure);
            harvestEnds.Add(new HarvestCanaryEnd(
                c.DbId, c.StartPhase, c.PlantUtc, c.GrowthUtc, c.DueUtc,
                row?.Phase ?? -1,
                row?.GrowthUtc ?? DateTime.MinValue, endUtc, failure));
        }

        string? travelFailure;
        TimerCanaryPos? pos0 = null;
        TimerCanaryPos? posEnd = null;
        var inWindowMax = -1.0;
        var inWindowSamples = 0;
        foreach (var s in samples)
        {
            if (s.TransferPos is null)
                continue;
            inWindowSamples++;
            pos0 ??= s.TransferPos;
            posEnd = s.TransferPos;
            var disp = DisplacementM(
                pos0.X, pos0.Y, pos0.Z,
                s.TransferPos.X, s.TransferPos.Y, s.TransferPos.Z);
            if (disp > inWindowMax)
                inWindowMax = disp;
        }
        if (pos0 is null || posEnd is null || inWindowMax < 0)
        {
            travelFailure = $"travel canary '{canaries.Travel.Name}' (tlId={canaries.Travel.TlId}) never resolved in {samples.Count} window samples — cannot judge motion";
        }
        else
        {
            travelFailure = CheckTravelProgression(inWindowMax, canaries.Travel.DispMinM);
            if (travelFailure != null)
                travelFailure += $" (first in-window [{pos0.X:F0},{pos0.Y:F0},{pos0.Z:F0}] -> last [{posEnd.X:F0},{posEnd.Y:F0},{posEnd.Z:F0}] over {inWindowSamples} sightings)";
        }
        if (travelFailure != null)
            AddFailure(failures, travelFailure);

        var displacement = pos0 is null || posEnd is null ? -1 :
            DisplacementM(pos0.X, pos0.Y, pos0.Z, posEnd.X, posEnd.Y, posEnd.Z);
        var travel = new TravelCanaryEnd(
            canaries.Travel.TlId, canaries.Travel.Name, pos0, posEnd,
            displacement, inWindowMax, inWindowSamples,
            canaries.Travel.DispMinM, canaries.Travel.ObservedMaxM, travelFailure);
        return new TimerProgressionEnd(harvestEnds.ToArray(), travel);
    }

    // ------------------------------------------------------- restart leg (b2)

    /// <summary>Re-anchors the harvest dues after the pre-window restart:
    /// the row must have survived (else fail-fast — persistence lost), and
    /// the authoritative due is recomputed from live state — phase 4391
    /// still pending keeps plant + 10x(first growth - plant); phase 4504
    /// (first fire lapsed across setup/restart) is due at its own
    /// growth_time. Anything else pre-window is a fail-fast, never a pass.
    /// The due must then land inside the measured window with margin.</summary>
    private static TimerCanarySetup RebaseCanaryDue(TimerCanarySetup canaries, TimeSpan window)
    {
        // Rebase runs at the top of the soak, before the window Stopwatch
        // starts: `now` IS the window start for contract purposes.
        var now = DateTime.UtcNow;
        // The rate MUST survive the pre-window restart: E2eStack rewrites
        // Config.Local.json only in EnsureUp, never in RestartGameServer, so
        // the post-restart file rate must equal the setup rate. Anything
        // else fails before the burn — never a pass on a wrong rate. The
        // verified rate is what the report prints beside the observed dues.
        var fileRate = ReadLiveGrowthRate();
        if (fileRate != canaries.GrowthRate)
            throw new InvalidOperationException(
                $"live Config.Local.json GrowthRate changed across the pre-window restart ({canaries.GrowthRate} -> {fileRate}) — the pinned rate did not persist");
        Console.WriteLine($"[a5t3-sixhour] growth rate verified post-restart: file {fileRate} (effective rate per canary logged at setup)");
        var harvest = canaries.Harvest.Select(c =>
        {
            var row = ReadCanaryDoodadRow(c.DbId)
                ?? throw new InvalidOperationException(
                    $"harvest canary doodad {c.DbId} has no row after the pre-window restart — persistence lost");
            DateTime due;
            if (row.Phase == TimerCanaryPlantGroupId)
            {
                if (row.GrowthUtc != c.GrowthUtc)
                    throw new InvalidOperationException(
                        $"harvest canary doodad {c.DbId} still in plant phase but growth_time was rewritten across restart ({c.GrowthUtc:O} -> {row.GrowthUtc:O})");
                due = ComputeDueUtc(row.PlantUtc, row.GrowthUtc);
            }
            else if (row.Phase == 4504)
            {
                if (row.GrowthUtc == c.GrowthUtc)
                    throw new InvalidOperationException(
                        $"harvest canary doodad {c.DbId} advanced to 4504 across restart without a recomputed growth_time — timer did not re-arm");
                due = row.GrowthUtc;
            }
            else
            {
                throw new InvalidOperationException(
                    $"harvest canary doodad {c.DbId} in phase {row.Phase} before the window (expected {TimerCanaryPlantGroupId} or 4504) — rate too fast or wrong chain?");
            }
            var band = CheckDueBand(due, now, WindowDueMinIntoMin, WindowDueMaxIntoMin, "window start");
            if (band != null)
                throw new InvalidOperationException(band);
            Console.WriteLine($"[a5t3-sixhour] harvest canary dbId={c.DbId} rebased: phase={row.Phase} due={due:O} " +
                              $"({(due - now).TotalMinutes:F1} min into the {window.TotalMinutes:F0}-min window)");
            return new HarvestCanarySetup(c.DbId, row.Phase, row.PlantUtc, row.GrowthUtc, due);
        }).ToArray();
        return new TimerCanarySetup(harvest, canaries.Travel, canaries.GrowthRate);
    }

    /// <summary>
    /// Bounded restart-conservation leg: plant one canary through the REAL
    /// plant path at RestartGrowthRate (first fire ~12 s, total due ~120 s),
    /// record the row, restart, wait until past due (bounded), then assert
    /// the row is preserved AND boot catch-up fired to a strictly forward
    /// phase with a recomputed growth_time. A still-pending row fails — it
    /// is consistency info, never a pass. Covers the SpawnManager boot load
    /// + ApplyLoadedState + InitDoodad re-arm without the 6 h soak.
    /// </summary>
    [Fact]
    public async Task Probe_A5Tier3RestartConservesDormantTimers()
    {
        if (Environment.GetEnvironmentVariable("A5_TIER3_TIMER_RESTART") != "1")
        {
            Assert.Skip("A5_TIER3_TIMER_RESTART=1 is required for the bounded restart-conservation stage.");
            return;
        }

        var ownedNames = new List<string> { HumanAccount };
        var growthRatePrev = PinGrowthRateEnv(RestartGrowthRate, "restart");
        try
        {
            E2eStack.EnsureUp();
            var ownershipBefore = E2eStack.SnapshotOwnedRows(ownedNames);
            s_timerCanaryDbIds.Clear();
            try
            {
                ClearFeatureEnv();
                var planted = await PlantTimerCanariesAsync(1, TestContext.Current.CancellationToken);
                var before = planted[0];
                // At RestartGrowthRate the first fire is 12 s out: the row read
                // here must still be the plant phase. Anything later means the
                // timer already ran during setup and this leg cannot observe a
                // restart crossing — fail fast, never fake it.
                if (before.StartPhase != TimerCanaryPlantGroupId)
                    throw new InvalidOperationException(
                        $"restart canary dbId={before.DbId} already in phase {before.StartPhase} before the restart (expected plant group {TimerCanaryPlantGroupId}) — matured during setup; lower the restart rate and rerun");
                var due = ComputeDueUtc(before.PlantUtc, before.GrowthUtc);
                var read0Utc = DateTime.UtcNow;
                Console.WriteLine($"[a5t3-restart] canary dbId={before.DbId} phase0={before.StartPhase} " +
                                  $"plant={before.PlantUtc:O} growth={before.GrowthUtc:O} due={due:O}");
                E2eStack.RestartGameServer();
                WaitBoot(cancellationToken: TestContext.Current.CancellationToken);
                var bootReadyUtc = DateTime.UtcNow;
                // Observe only once BOTH the boot settle (15 s for the 1 ms
                // catch-up re-arm to fire on the game loop) AND the due (+15 s
                // margin) have passed — bounded: if the deadline hits first the
                // verdict below fails on read1 < due (or pending), never passing
                // a row that could not have caught up.
                var observeAtUtc = bootReadyUtc.AddSeconds(15) > due.AddSeconds(15)
                    ? bootReadyUtc.AddSeconds(15)
                    : due.AddSeconds(15);
                var waitDeadlineUtc = bootReadyUtc.AddMinutes(15);
                while (DateTime.UtcNow < observeAtUtc && DateTime.UtcNow < waitDeadlineUtc)
                    await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                var after = ReadCanaryDoodadRow(before.DbId);
                var read1Utc = DateTime.UtcNow;
                Assert.NotNull(after);
                Console.WriteLine($"[a5t3-restart] after: phase={after!.Phase} " +
                                  $"plant={after.PlantUtc:O} growth={after.GrowthUtc:O}");
                var reason = CheckRestartConservation(
                    before.StartPhase, before.PlantUtc, before.GrowthUtc, read0Utc,
                    after.Phase, after.PlantUtc, after.GrowthUtc, read1Utc,
                    due, TimerCanaryValidPhases);
                Assert.True(reason is null, reason);
            }
            finally
            {
                try
                {
                    DeleteTimerCanaryDoodads();
                    var ownershipAfter = E2eStack.SnapshotOwnedRows(ownedNames);
                    var ownedRows = E2eStack.FindNewOwnedRows(ownershipBefore, ownershipAfter);
                    E2eStack.CleanupOwnedRows(ownedRows);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[a5t3-restart] ownership cleanup skipped: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            RestoreGrowthRateEnv(growthRatePrev, "restart");
        }
    }
}
