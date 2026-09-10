using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M9.5 fishing contest (locked launch activity, ROADMAP §M9.5 exit test):
/// one scheduled event runs start → finish with bot participants plus at
/// least one human-or-M5-stand-in, auditable results (entries, scores,
/// winner, rewards settled in the ledger), and the world outside the event
/// observed within G1 budgets. Built as a B3 activity module's driven
/// behavior: the <see cref="FishingContestActivityModule"/> arbitrates WHO
/// fishes, this composer runs WHAT the event does.
///
/// Composition only: every fishing leg calls the EXISTING
/// <see cref="GameplayActor.CastAt"/> action unchanged (the same
/// Skill.Use + SkillCastPositionTarget seam CSStartSkillPacket's Pos branch
/// drives — live skill 21571 / plot 809 per the FISH-01 dossier
/// scorecard-explorations/mechanics/fishing-domain.md §3; rigs override with
/// the seeded TestPosSkillId minimal plot, the same runtime surface), and
/// reward settlement rides the EXISTING
/// <see cref="GameplayActor.DepositMoney"/> path after a scripted prize
/// grant on the ordinary Character money pool (ItemTaskType.Fishing — the
/// canonical fishing money task; no parallel economy implementation,
/// AGENTS.md #9). No new engine path, no parallel catch implementation.
///
/// Default-OFF surface: static callable with no tick subscription, no
/// bootstrap, no background work — plus an explicit <see cref="Enabled"/>
/// kill-switch (default false). Inert unless a caller opts in. Reporting is
/// a canned record (<see cref="FishingContestReport"/>, LLM LAST): fixed
/// fields only, no model, no chat, no procedural text.
///
/// Scoring (deterministic, identical live and rig): fish items gained first,
/// then completed casts, then lowest actor ObjId. The rig's minimal plot
/// grants no loot, so rig scores degenerate to completed-casts + ObjId
/// order — the rule is the same, the loot leg differentiates live (where
/// plot 809 rolls real loot packs). A contest with zero completed casts
/// fails closed at FISH: there is no winner when nothing was caught.
///
/// One ContestId is one scheduled occurrence: settlement is claimed once in
/// <c>s_settledContests</c> BEFORE the prize grant lands, so a duplicate run
/// of the same occurrence is refused without moving money (no double pay).
/// A new occurrence uses a new ContestId.
///
/// Sports stratum (FISH-01, explicit): RECORDED-OPEN, never wired here.
/// <see cref="SportsStratumVerdict"/> names the orphaned seams
/// (SpawnFishEffect unreachable — zero actual_type rows; DoodadFuncCatch
/// stub; DoodadFuncFishSchool returns false; Convert/BuyFishItem/BuyFishModel
/// stubs). Scores ride the basic CastAt path only. Claiming this contest
/// closes the sports stratum is rejected — see the M9.5 card.
/// </summary>
public static class FishingContestScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "m9.5-fishing-contest";

    /// <summary>
    /// Default-OFF kill switch. The composer refuses every run unless a
    /// caller opts in — the M8 static-composer default-OFF stance.
    /// </summary>
    public static bool Enabled { get; set; } = false;

    /// <summary>Live canonical fishing skill: 21571 낚시하기, plot 809 (FISH-01 dossier §2).</summary>
    public const uint LiveFishingSkillId = 21571;

    /// <summary>Live canonical bait: 27142 Wriggling Worm (skill_reagents 2381, dossier §2).</summary>
    public const uint LiveWormItemId = 27142;

    /// <summary>
    /// G1-budget event ceiling: at most this many contestants per occurrence
    /// (M8 C5 exit: 25 embodied within G1 budgets). Larger rosters refuse at
    /// BUDGET before any cast runs.
    /// </summary>
    public const int MaxContestants = 25;

    /// <summary>
    /// Sports-stratum verdict carried on every report: this slice records
    /// the stratum open (see the class doc). A wiring slice would replace
    /// this constant with wired-with-assertions evidence as a named sub-leg.
    /// </summary>
    public const string SportsStratumVerdict = "recorded-open";

    /// <summary>Settled occurrence ids (ContestId → winner ActorId). The no-double-pay claim.</summary>
    private static readonly ConcurrentDictionary<string, uint> s_settledContests = new();

    /// <summary>One scheduled occurrence's parameters.</summary>
    public sealed record ContestOptions
    {
        /// <summary>Idempotency namespace for this occurrence's legs; one id = one event.</summary>
        public string ContestId { get; init; } = "m9.5-fishing-01";

        /// <summary>
        /// Position-target fishing skill (live default <see cref="LiveFishingSkillId"/>;
        /// rigs pass the seeded TestPosSkillId minimal-plot stand-in).
        /// </summary>
        public uint FishingSkillId { get; init; } = LiveFishingSkillId;

        /// <summary>
        /// Bait template excluded from the fish-gain count (live default
        /// <see cref="LiveWormItemId"/>; rigs pass their worm-slot template).
        /// </summary>
        public uint ReagentItemTemplateId { get; init; } = LiveWormItemId;

        /// <summary>Water position every entrant casts at (bot has no facing semantics; yaw 0).</summary>
        public Vector3 CastPosition { get; init; } = new(0f, 0f, 100f);

        /// <summary>Winner prize in copper, granted then banked via DepositMoney (must be &gt; 0).</summary>
        public long WinnerRewardCopper { get; init; } = 1000;

        /// <summary>Scheduled window open (inclusive).</summary>
        public DateTimeOffset OpensAt { get; init; } = DateTimeOffset.MinValue;

        /// <summary>Scheduled window close (inclusive).</summary>
        public DateTimeOffset ClosesAt { get; init; } = DateTimeOffset.MaxValue;

        /// <summary>Clock seam (tests pin the window deterministically).</summary>
        public Func<DateTimeOffset>? UtcNow { get; init; }

        /// <summary>World-pressure seam (tests force High to prove the budget hold).</summary>
        public Func<ServerPressure>? PressureProbe { get; init; }
    }

    /// <summary>One entrant: an ordinary bot actor plus its event role.</summary>
    public sealed record ContestEntrant(GameplayActor Actor, bool IsHumanOrStandIn)
    {
        /// <summary>Display name (the character name at run time).</summary>
        public string Name => Actor?.Character?.Name ?? "(null)";
    }

    /// <summary>One fished entry: the cast outcome plus observable deltas.</summary>
    public sealed record ContestEntry(
        uint ActorId,
        string Name,
        bool IsHumanOrStandIn,
        Guid CastTraceId,
        string CastState,
        string CastDetail,
        int CompletedCasts,
        int FishGained,
        int WormsConsumed,
        int LaborBefore,
        int LaborAfter);

    /// <summary>Ordinary money-pool snapshot (inventory + bank).</summary>
    public sealed record ContestMoneySnapshot(long Money, long BankMoney);

    /// <summary>Per-stage ledger entry: observable state before → after.</summary>
    public sealed record ContestLedgerEntry(
        string Stage, uint ActorId, ContestMoneySnapshot Before, ContestMoneySnapshot After)
    {
        public long MoneyDelta => After.Money - Before.Money;
        public long BankDelta => After.BankMoney - Before.BankMoney;
    }

    /// <summary>
    /// The explicit contest ledger. Totals come from ACTION RESULTS and the
    /// ordinary Character money pool; the Reconcile* members derive the
    /// VERIFY criteria — and double as the unit-testable reconciliation law
    /// (rig tests corrupt a settled ledger and assert the law fails).
    /// </summary>
    public sealed class FishingContestLedger
    {
        public List<ContestLedgerEntry> Entries { get; } = [];

        public uint WinnerActorId { get; set; }

        public long WinnerRewardCopper { get; set; }

        /// <summary>
        /// The payout law: exactly one SETTLE entry, banking exactly the
        /// reward with a zero net inventory delta (grant then deposit — a
        /// duplicated grant or a double deposit breaks it).
        /// </summary>
        public BotScenarioRunner.CriterionVerdict ReconcileWinnerPayout()
        {
            var settles = Entries.Where(e => e.Stage == "SETTLE").ToList();
            var ok = settles.Count == 1
                && settles[0].ActorId == WinnerActorId
                && settles[0].BankDelta == WinnerRewardCopper
                && settles[0].MoneyDelta == 0;
            return new BotScenarioRunner.CriterionVerdict("ledger-winner-payout", ok,
                ok
                    ? $"winner {WinnerActorId} banked exactly {WinnerRewardCopper}c (inventory net 0 — no dup, no leak)"
                    : $"payout MISMATCH: {settles.Count} SETTLE entries" +
                      (settles.Count == 1 ? $" bank Δ {settles[0].BankDelta}c / inventory Δ {settles[0].MoneyDelta}c vs reward {WinnerRewardCopper}c" : ""));
        }

        /// <summary>The losers law: fishing legs move no money; only the winner settles.</summary>
        public BotScenarioRunner.CriterionVerdict ReconcileLosersZero()
        {
            var bad = Entries.Where(e => e.Stage != "SETTLE" && (e.MoneyDelta != 0 || e.BankDelta != 0)).ToList();
            var foreignSettle = Entries.Any(e => e.Stage == "SETTLE" && e.ActorId != WinnerActorId);
            var ok = bad.Count == 0 && !foreignSettle;
            return new BotScenarioRunner.CriterionVerdict("ledger-losers-zero", ok,
                ok
                    ? "non-settle legs moved no money; no foreign SETTLE entry"
                    : $"money moved outside the winner settlement ({bad.Count} legs, foreign settle: {foreignSettle})");
        }

        /// <summary>The stage-sum law: Σ ledger deltas equal the single reward — catches a skipped mutation.</summary>
        public BotScenarioRunner.CriterionVerdict ReconcileStageSums()
        {
            var bankSum = Entries.Sum(e => e.BankDelta);
            var moneySum = Entries.Sum(e => e.MoneyDelta);
            var ok = bankSum == WinnerRewardCopper && moneySum == 0;
            return new BotScenarioRunner.CriterionVerdict("ledger-stage-sums-reconcile", ok,
                ok
                    ? $"Σ bank Δ {bankSum}c == reward {WinnerRewardCopper}c; Σ inventory Δ {moneySum}c == 0 ({Entries.Count} entries)"
                    : $"ledger MISMATCH: Σ bank Δ {bankSum}c vs reward {WinnerRewardCopper}c; Σ inventory Δ {moneySum}c vs 0");
        }
    }

    /// <summary>
    /// Canned contest record (LLM LAST): fixed fields only. The audit trail
    /// is the legs' own <see cref="ActorAuditRecord"/> entries plus the
    /// returned result.
    /// </summary>
    public sealed record FishingContestReport(
        string ContestId,
        string Scenario,
        bool Passed,
        int EntrantCount,
        int BotCount,
        int StandInCount,
        IReadOnlyList<string> ScoreLines,
        string WinnerName,
        uint WinnerActorId,
        long RewardCopper,
        long WinnerBankDelta,
        bool LedgerReconciled,
        int ParticipantCeiling,
        string Pressure,
        string BudgetVerdict,
        string SportsStratum,
        string FailStage,
        string FailReason);

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class FishingContestResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public List<ContestEntry> Entries { get; init; } = [];
        public ContestEntry? Winner { get; init; }
        public FishingContestLedger Ledger { get; init; } = new();
        public FishingContestReport? Report { get; init; }
    }

    /// <summary>
    /// Runs one scheduled contest occurrence: roster → schedule → budget →
    /// fish (one CastAt per entrant) → score → settle (grant + DepositMoney)
    /// → verify (ledger laws + audit completeness). Fail-closed at every
    /// leg: a rejection stops the event, records the reason, and never
    /// moves money it should not.
    /// </summary>
    public static FishingContestResult Run(IReadOnlyList<ContestEntrant> entrants, ContestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        options ??= new ContestOptions();

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var entries = new List<ContestEntry>();
        var ledger = new FishingContestLedger { WinnerRewardCopper = options.WinnerRewardCopper };

        FishingContestResult Finish(
            bool passed, string failStage, ActorFailureReason? failure, string failReason,
            ContestEntry? winner = null, long winnerBankDelta = 0, bool ledgerReconciled = false,
            string pressure = "unknown", string budgetVerdict = "")
        {
            var scoreLines = entries
                .Select(e => $"{e.Name} actor={e.ActorId} standin={e.IsHumanOrStandIn} " +
                    $"casts={e.CompletedCasts} fish={e.FishGained} worms={e.WormsConsumed} " +
                    $"labor={e.LaborBefore}->{e.LaborAfter} cast={e.CastState}")
                .ToList();
            var bots = entrants.Count(e => e.Actor != null && !e.IsHumanOrStandIn);
            var standIns = entrants.Count(e => e.Actor != null && e.IsHumanOrStandIn);
            return new FishingContestResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                Entries = entries,
                Winner = winner,
                Ledger = ledger,
                Report = new FishingContestReport(
                    options.ContestId, ScenarioName, passed,
                    entries.Count, bots, standIns,
                    scoreLines,
                    winner?.Name ?? "", winner?.ActorId ?? 0,
                    options.WinnerRewardCopper, winnerBankDelta, ledgerReconciled,
                    MaxContestants, pressure, budgetVerdict,
                    SportsStratumVerdict, failStage, failReason)
            };
        }

        // ---- PRECHECK: kill-switch, options, roster ----
        if (!Enabled)
        {
            const string disabled = "fishing contest disabled (FishingContestScenario.Enabled off)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, disabled));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", disabled));
            Logger.Warn("[{Scenario}] {Contest}: PRECHECK {Reason}", ScenarioName, options.ContestId, disabled);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, disabled);
        }

        if (string.IsNullOrWhiteSpace(options.ContestId))
        {
            const string noId = "contest id must be non-empty (one id = one scheduled occurrence)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, noId));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", noId));
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, noId);
        }

        if (options.FishingSkillId == 0)
        {
            var noSkill = "fishing skill id must be non-zero";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, noSkill));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", noSkill));
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, noSkill);
        }

        if (options.WinnerRewardCopper <= 0 || options.WinnerRewardCopper > int.MaxValue)
        {
            var badReward = $"winner reward {options.WinnerRewardCopper}c must be positive and fit int (engine money legs take int)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, badReward));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", badReward));
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, badReward);
        }

        if (s_settledContests.ContainsKey(options.ContestId))
        {
            var settled = $"contest {options.ContestId} already settled — duplicate occurrence refused (one id = one event)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, settled));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", settled));
            Logger.Warn("[{Scenario}] {Contest}: PRECHECK {Reason}", ScenarioName, options.ContestId, settled);
            return Finish(false, "PRECHECK", ActorFailureReason.StateTransition, settled);
        }

        if (entrants.Any(e => e.Actor == null))
        {
            const string nullActor = "every entrant must carry an actor (null entrant refused)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, nullActor));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", nullActor));
            return Finish(false, "ROSTER", ActorFailureReason.RejectedAction, nullActor);
        }

        if (entrants.Select(e => e.Actor.ActorId).Distinct().Count() != entrants.Count)
        {
            const string dupActor = "every entrant must be a distinct actor (same actor entered twice refused)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, dupActor));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", dupActor));
            return Finish(false, "ROSTER", ActorFailureReason.RejectedAction, dupActor);
        }

        var hasBots = entrants.Any(e => !e.IsHumanOrStandIn);
        var hasStandIn = entrants.Any(e => e.IsHumanOrStandIn);
        if (!hasBots || !hasStandIn)
        {
            var roster = $"contest needs bot participants AND at least one human-or-stand-in (bots: {hasBots}, stand-in: {hasStandIn})";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", false, roster));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("ROSTER", 0, "Rejected", "", roster));
            Logger.Warn("[{Scenario}] {Contest}: ROSTER {Reason}", ScenarioName, options.ContestId, roster);
            return Finish(false, "ROSTER", ActorFailureReason.RejectedAction, roster);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("roster-has-bots-and-standin", true,
            $"{entrants.Count} entrants ({entrants.Count(e => !e.IsHumanOrStandIn)} bots + {entrants.Count(e => e.IsHumanOrStandIn)} stand-in)"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"contest {options.ContestId}"));

        // ---- SCHEDULE: the event runs only inside its window ----
        var now = options.UtcNow?.Invoke() ?? DateTimeOffset.UtcNow;
        if (now < options.OpensAt || now > options.ClosesAt)
        {
            var closed = $"contest {options.ContestId} not open (now {now:o} outside [{options.OpensAt:o}, {options.ClosesAt:o}])";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("schedule-open", false, closed));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("SCHEDULE", 0, "Rejected", "", closed));
            Logger.Warn("[{Scenario}] {Contest}: SCHEDULE {Reason}", ScenarioName, options.ContestId, closed);
            return Finish(false, "SCHEDULE", ActorFailureReason.RejectedAction, closed);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("schedule-open", true,
            $"contest open (now {now:o} inside window)"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("SCHEDULE", 0, "Completed", "", "window open"));

        // ---- BUDGET: G1 event ceiling + pressure hold (world-budget observance) ----
        var pressure = options.PressureProbe?.Invoke() ?? ServerPressure.Healthy;
        var budgetVerdict = $"{entrants.Count}/{MaxContestants} contestants, pressure {pressure}";
        if (entrants.Count > MaxContestants)
        {
            var over = $"roster {entrants.Count} exceeds the G1 event ceiling {MaxContestants} — refused before any cast";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("budget-within-g1-ceiling", false, over));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("BUDGET", 0, "Rejected", "", over));
            Logger.Warn("[{Scenario}] {Contest}: BUDGET {Reason}", ScenarioName, options.ContestId, over);
            return Finish(false, "BUDGET", ActorFailureReason.RejectedAction, over, pressure: pressure.ToString(), budgetVerdict: budgetVerdict);
        }

        if (pressure >= ServerPressure.High)
        {
            var held = $"held: server pressure {pressure} (world-budget observance — the event never runs the world hot)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("budget-within-g1-ceiling", false, held));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("BUDGET", 0, "Held", "", held));
            Logger.Warn("[{Scenario}] {Contest}: BUDGET {Reason}", ScenarioName, options.ContestId, held);
            return Finish(false, "BUDGET", ActorFailureReason.RejectedAction, held, pressure: pressure.ToString(), budgetVerdict: budgetVerdict);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("budget-within-g1-ceiling", true, budgetVerdict));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("BUDGET", 0, "Completed", "", budgetVerdict));

        // ---- FISH: one CastAt per entrant through the existing action ----
        foreach (var entrant in entrants)
        {
            var actor = entrant.Actor;
            var bagBefore = SnapshotBag(actor);
            var moneyBefore = new ContestMoneySnapshot(actor.Character.Money, actor.Character.Money2);
            var laborBefore = actor.Character.LaborPower;

            var cast = actor.CastAt(options.FishingSkillId, options.CastPosition,
                idempotencyKey: $"{options.ContestId}-cast-{actor.ActorId}");
            traceRecords.Add(actor.AuditTrace.Last());

            var completed = cast.State == ActorLifecycleState.Completed ? 1 : 0;
            var bagAfter = SnapshotBag(actor);
            var fishGained = FishGains(bagBefore, bagAfter, options.ReagentItemTemplateId);
            var wormsConsumed = Math.Max(0, CountIn(bagBefore, options.ReagentItemTemplateId) - CountIn(bagAfter, options.ReagentItemTemplateId));
            var entry = new ContestEntry(
                actor.ActorId, entrant.Name, entrant.IsHumanOrStandIn,
                cast.TraceId, cast.State.ToString(), cast.Detail ?? "",
                completed, fishGained, wormsConsumed,
                laborBefore, actor.Character.LaborPower);
            entries.Add(entry);
            ledger.Entries.Add(new ContestLedgerEntry(
                $"FISH-{entrant.Name}", actor.ActorId, moneyBefore,
                new ContestMoneySnapshot(actor.Character.Money, actor.Character.Money2)));
            stages.Add(Stage($"FISH-{entrant.Name}", cast, $"casts={completed} fish={fishGained} worms={wormsConsumed}"));
        }

        var completions = entries.Sum(e => e.CompletedCasts);
        var castsOk = completions > 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("casts-executed", castsOk,
            castsOk
                ? $"{completions}/{entries.Count} entrants completed their cast through the real CastAt path"
                : $"no entrant completed a cast ({string.Join("; ", entries.Select(e => $"{e.Name}: {e.CastState} ({e.CastDetail})"))}) — no winner when nothing was caught"));
        if (!castsOk)
        {
            var reason = $"all {entries.Count} casts rejected — event failed closed with no settlement";
            Logger.Warn("[{Scenario}] {Contest}: FISH {Reason}", ScenarioName, options.ContestId, reason);
            return Finish(false, "FISH", ActorFailureReason.RejectedAction, reason,
                pressure: pressure.ToString(), budgetVerdict: budgetVerdict);
        }

        // ---- SCORE: fish gained, then completed casts, then lowest ActorId ----
        var winner = entries
            .OrderByDescending(e => e.FishGained)
            .ThenByDescending(e => e.CompletedCasts)
            .ThenBy(e => e.ActorId)
            .First();
        ledger.WinnerActorId = winner.ActorId;
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
            "SCORE", entries.Count, "Completed", $"{winner.Name} ({winner.ActorId})",
            $"fish {winner.FishGained}, casts {winner.CompletedCasts} — deterministic rule (fish, casts, lowest actor)"));
        Logger.Info("[{Scenario}] {Contest}: winner {Winner} ({ActorId}) fish {Fish} casts {Casts}",
            ScenarioName, options.ContestId, winner.Name, winner.ActorId, winner.FishGained, winner.CompletedCasts);

        // ---- SETTLE: claim the occurrence, grant the prize, bank it ----
        if (!s_settledContests.TryAdd(options.ContestId, winner.ActorId))
        {
            var dup = $"contest {options.ContestId} already settled — duplicate settlement refused (no money moved)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("ledger-winner-payout", false, dup));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("SETTLE", 0, "Rejected", "", dup));
            Logger.Warn("[{Scenario}] {Contest}: SETTLE {Reason}", ScenarioName, options.ContestId, dup);
            return Finish(false, "SETTLE", ActorFailureReason.StateTransition, dup, winner,
                pressure: pressure.ToString(), budgetVerdict: budgetVerdict);
        }

        var winnerActor = entrants.First(e => e.Actor.ActorId == winner.ActorId).Actor;
        var settleBefore = new ContestMoneySnapshot(winnerActor.Character.Money, winnerActor.Character.Money2);
        // Prize grant on the ordinary Character money pool (ItemTaskType.Fishing —
        // the canonical fishing money task): the contest funds the prize, the
        // engine pool carries it — never a parallel currency.
        winnerActor.Character.AddMoney(SlotType.Inventory, (int)options.WinnerRewardCopper, ItemTaskType.Fishing);
        var deposit = winnerActor.DepositMoney(options.WinnerRewardCopper,
            idempotencyKey: $"{options.ContestId}-settle-{winner.ActorId}");
        traceRecords.Add(winnerActor.AuditTrace.Last());
        var settleAfter = new ContestMoneySnapshot(winnerActor.Character.Money, winnerActor.Character.Money2);
        ledger.Entries.Add(new ContestLedgerEntry("SETTLE", winner.ActorId, settleBefore, settleAfter));
        stages.Add(Stage("SETTLE", deposit, $"winner {winner.Name} banks {options.WinnerRewardCopper}c"));
        if (deposit.State != ActorLifecycleState.Completed)
        {
            var reason = $"winner settlement failed: {deposit.Detail} (grant landed on the winner record — auditable, never silent)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("ledger-winner-payout", false, reason));
            Logger.Warn("[{Scenario}] {Contest}: SETTLE {Reason}", ScenarioName, options.ContestId, reason);
            return Finish(false, "SETTLE", deposit.Failure, reason, winner,
                settleAfter.BankMoney - settleBefore.BankMoney,
                pressure: pressure.ToString(), budgetVerdict: budgetVerdict);
        }

        // ---- VERIFY: ledger laws + audit completeness ----
        var payout = ledger.ReconcileWinnerPayout();
        var losers = ledger.ReconcileLosersZero();
        var sums = ledger.ReconcileStageSums();
        criteria.Add(payout);
        criteria.Add(losers);
        criteria.Add(sums);
        var ledgerOk = payout.Passed && losers.Passed && sums.Passed;

        var expectedTraces = entrants.Count + 1;
        var auditOk = traceRecords.Count == expectedTraces;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("audit-complete", auditOk,
            auditOk
                ? $"{traceRecords.Count} audit records for {entrants.Count} casts + 1 settlement"
                : $"audit MISMATCH: {traceRecords.Count} records vs {expectedTraces} legs"));
        if (!ledgerOk || !auditOk)
        {
            var failed = string.Join("; ", criteria.Where(c => !c.Passed).Select(c => c.Name));
            var reason = $"contest ledger failed verification: {failed}";
            Logger.Warn("[{Scenario}] {Contest}: VERIFY {Reason}", ScenarioName, options.ContestId, reason);
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition, reason, winner,
                settleAfter.BankMoney - settleBefore.BankMoney, ledgerOk,
                pressure.ToString(), budgetVerdict);
        }

        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("VERIFY", 0, "Completed", "", "ledger reconciled, audit complete"));
        Logger.Info("[{Scenario}] {Contest}: PASS winner {Winner} banked {Reward}c, ledger {Entries} entries",
            ScenarioName, options.ContestId, winner.Name, options.WinnerRewardCopper, ledger.Entries.Count);
        return Finish(true, "", null, "", winner,
            settleAfter.BankMoney - settleBefore.BankMoney, true,
            pressure.ToString(), budgetVerdict);
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));

    private static Dictionary<uint, int> SnapshotBag(GameplayActor actor)
    {
        var snapshot = new Dictionary<uint, int>();
        var items = actor.Character.Inventory?.Bag.Items;
        if (items == null)
            return snapshot;
        foreach (var item in items)
        {
            if (item == null)
                continue;
            snapshot.TryGetValue(item.TemplateId, out var count);
            snapshot[item.TemplateId] = count + item.Count;
        }

        return snapshot;
    }

    private static int CountIn(Dictionary<uint, int> snapshot, uint templateId)
        => snapshot.TryGetValue(templateId, out var count) ? count : 0;

    /// <summary>
    /// Fish gained: every positive per-template delta except the bait
    /// template (consumed worms are evidence, never score).
    /// </summary>
    private static int FishGains(Dictionary<uint, int> before, Dictionary<uint, int> after, uint reagentTemplateId)
    {
        var gained = 0;
        foreach (var (templateId, countAfter) in after)
        {
            if (templateId == reagentTemplateId)
                continue;
            var delta = countAfter - CountIn(before, templateId);
            if (delta > 0)
                gained += delta;
        }

        return gained;
    }
}
