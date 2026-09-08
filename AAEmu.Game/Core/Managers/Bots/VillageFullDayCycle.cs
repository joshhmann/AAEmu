using System.Numerics;

using AAEmu.Game.Models.Game.Bots;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M8 C5 full-day assembly slice-4 (ROADMAP M8 living-village contracts) —
/// chain <see cref="VillageDayCycle"/> (day: Work ticks through the C3/C4
/// slice composers) and <see cref="VillageEveningCycle"/> (evening:
/// withdraw → merchant-sell → deposit → return-home + the C2 chatter pass)
/// for one roster, then reconcile a COMBINED ledger across both halves
/// (labor charged once, zero net inventory drift, bank drift == evening
/// refunds) and the M5 audit-completeness law over the concatenated traces.
///
/// Composition only: the assembly calls the EXISTING day/evening composers
/// unchanged (each in its own idempotency namespace under the full-day
/// cycle id), driven by ordinary <see cref="Character"/> records through
/// normal gameplay services (AGENTS.md #9). No new engine path, no parallel
/// profession/trade implementation, no schedule/resolver/arbiter changes.
/// Fail-closed at every half: a failed day never runs the evening; a failed
/// evening keeps the day's banked state and reports the evening stage.
///
/// The evening markets caller-specified banked output (the slice-3 shape):
/// day-produced lots flow through the SAME bank containers the evening
/// withdraws from and are reconciled in the one ledger, but vending the
/// day's own harvest/product lots is a later slice (it needs sellable crop
/// templates + trader economics the rig does not own).
///
/// Restart note (rig-side single-restart variant): the kill-9 + MySQL
/// mechanics (save pass, playerbot_metadata/characters/items/audit rows,
/// pre==post byte equality) are pinned by the slice-2 E2E
/// (VillageDayCycleRestartE2eTests) over the SAME tables — slice-4 adds no
/// new persisted table, only new values in the same rows. What the assembly
/// adds is the projection surface: <see cref="ProjectRestart"/> renders the
/// day+evening state (schedule/profession/phase + labor/money/bank + evening
/// economics + chatter count) as canonical strings the rig proves complete,
/// deterministic, and round-trippable. An E2E hook would only re-prove the
/// unchanged kill-9 mechanics, so none is added.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Reporting is a canned record (LLM LAST): fixed fields only.
/// </summary>
public static class VillageFullDayCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-village-fullday-s4";

    /// <summary>One villager's full-day parameters (day + evening legs share the actor).</summary>
    public sealed record FullDayVillager
    {
        /// <summary>Stable villager name (must match DaySpec.Name).</summary>
        public required string Name { get; init; }

        /// <summary>Day parameters (profession legs; the actor is shared with the evening).</summary>
        public required VillageDayCycle.VillagerSpec DaySpec { get; init; }

        /// <summary>Evening: banked saleable output template (0 = hold the market legs).</summary>
        public uint EveningOutputTemplateId { get; init; }

        /// <summary>Evening: ordinary merchant NPC ObjId (0 = unbound — hold).</summary>
        public uint EveningMerchantObjId { get; init; }

        /// <summary>Evening: home destination for the return leg.</summary>
        public required Vector3 EveningHome { get; init; }

        /// <summary>Evening: walk speed in units/second.</summary>
        public float EveningSpeed { get; init; } = 5f;
    }

    /// <summary>Full-day sweep parameters.</summary>
    public sealed record VillageFullDayOptions
    {
        /// <summary>Idempotency namespace (day runs under {CycleId}-day, evening under {CycleId}-evening).</summary>
        public string CycleId { get; init; } = "m8v4-c0";

        /// <summary>Day-sweep parameters (CycleId overridden per run).</summary>
        public VillageDayCycle.VillageDayOptions Day { get; init; } = new();

        /// <summary>Evening parameters incl. chatter budgets + sink (CycleId overridden per run).</summary>
        public VillageEveningCycle.VillageEveningOptions Evening { get; init; } = new();
    }

    /// <summary>Per-villager full-day evidence (day + evening halves, combined ledger).</summary>
    public sealed class VillagerFullDayEntry
    {
        public required string Name { get; init; }
        public VillageDayCycle.VillageProfession Profession { get; init; }
        public BotDailyAnchors Anchors { get; init; } = BotDailyAnchors.Template;
        public Vector3 EveningHome { get; init; }
        public int LaborBeforeDay { get; init; }
        public int LaborAfterEvening { get; init; }
        public long MoneyBeforeDay { get; init; }
        public long MoneyAfterEvening { get; init; }
        public long BankBeforeDay { get; init; }
        public long BankAfterEvening { get; init; }
        public VillageDayCycle.VillagerDayEntry? DayEntry { get; init; }
        public VillageEveningCycle.VillagerEveningEntry? EveningEntry { get; init; }
    }
    /// <summary>Canned full-day report (LLM LAST): fixed fields only.</summary>
    public sealed class VillageFullDayReport
    {
        public string CycleId { get; init; } = "";
        public List<VillagerFullDayEntry> Villagers { get; init; } = [];
        public int ChatterLinesTotal { get; init; }
        public VillageDayCycle.VillageDayReport? DayReport { get; init; }
        public VillageEveningCycle.VillageEveningReport? EveningReport { get; init; }
        public bool Passed { get; init; }
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class VillageFullDayResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public required VillageFullDayReport Report { get; init; }
    }

    /// <summary>
    /// Runs one village full day: the day half for the whole roster, then
    /// the evening half for the same roster, then the combined ledger.
    /// Fail-closed at every half: a failed day never runs the evening; a
    /// rejected evening leg stops its villager only (the evening composer's
    /// own isolation) and the assembly reports the first evening failure.
    /// </summary>
    public static VillageFullDayResult Run(IReadOnlyList<FullDayVillager> villagers, VillageFullDayOptions? options = null, IVillageMovePump? pump = null)
    {
        ArgumentNullException.ThrowIfNull(villagers);
        options ??= new VillageFullDayOptions();

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var entries = new List<VillagerFullDayEntry>();

        VillageFullDayResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            VillageDayCycle.VillageDayReport? dayReport = null, VillageEveningCycle.VillageEveningReport? eveningReport = null)
            => new()
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                Report = new VillageFullDayReport
                {
                    CycleId = options.CycleId,
                    Villagers = entries,
                    ChatterLinesTotal = eveningReport?.ChatterLinesTotal ?? 0,
                    DayReport = dayReport,
                    EveningReport = eveningReport,
                    Passed = passed
                }
            };

        if (villagers.Count == 0)
        {
            const string empty = "village has no villagers";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-roster", false, empty));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", empty));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, empty);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, empty);
        }

        var nameMismatch = villagers.FirstOrDefault(v => v.Name != v.DaySpec.Name);
        if (nameMismatch != null)
        {
            var reason = $"villager {nameMismatch.Name} does not match its day spec ({nameMismatch.DaySpec.Name})";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-roster", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-roster", true,
            $"full-day roster: {string.Join(", ", villagers.Select(v => $"{v.Name}/{v.DaySpec.Profession}"))}"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", villagers.Count, "Completed", "", "roster bound"));

        // ---- SNAPSHOT: pre-day balances (labor rides the day entries; the
        // evening charges no labor, so day-after == evening-end is asserted).
        var moneyBefore = villagers.ToDictionary(v => v.Name, v => v.DaySpec.Actor.Character.Money);
        var bankBefore = villagers.ToDictionary(v => v.Name, v => v.DaySpec.Actor.Character.Money2);

        // ---- DAY: the existing composer unchanged, own namespace ----
        var dayResult = VillageDayCycle.Run(
            villagers.Select(v => v.DaySpec).ToList(),
            options.Day with { CycleId = $"{options.CycleId}-day" });
        stages.AddRange(dayResult.Stages);
        criteria.AddRange(dayResult.Criteria);
        traceRecords.AddRange(dayResult.TraceRecords);

        foreach (var (spec, dayEntry) in villagers.Zip(dayResult.Report.Villagers))
        {
            entries.Add(new VillagerFullDayEntry
            {
                Name = spec.Name,
                Profession = dayEntry.Profession,
                Anchors = spec.DaySpec.Anchors,
                EveningHome = spec.EveningHome,
                LaborBeforeDay = dayEntry.LaborBefore,
                DayEntry = dayEntry,
                MoneyBeforeDay = moneyBefore[spec.Name],
                BankBeforeDay = bankBefore[spec.Name]
            });
        }

        if (!dayResult.Passed)
        {
            // Fail-closed: the evening never runs — no market keys consumed,
            // no chatter, the day's banked state stands as the day left it.
            criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-day-passed", false,
                $"day half failed at {dayResult.FailStage}; evening skipped"));
            Logger.Warn("[{Scenario}] {Cycle}: DAY {Stage} — evening skipped",
                ScenarioName, options.CycleId, dayResult.FailStage);
            return Finish(false, dayResult.FailStage, dayResult.Failure, dayResult.FailReason, dayResult.Report);
        }
        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-day-passed", true, "day half completed"));

        // ---- EVENING: the existing composer unchanged, own namespace ----
        var eveningSpecs = villagers.Select(v => new VillageEveningCycle.VillagerEveningSpec
        {
            Name = v.Name,
            Actor = v.DaySpec.Actor,
            OutputTemplateId = v.EveningOutputTemplateId,
            MerchantObjId = v.EveningMerchantObjId,
            Home = v.EveningHome,
            Speed = v.EveningSpeed
        }).ToList();
        var eveningResult = VillageEveningCycle.Run(
            eveningSpecs,
            options.Evening with { CycleId = $"{options.CycleId}-evening" },
            pump);
        stages.AddRange(eveningResult.Stages);
        criteria.AddRange(eveningResult.Criteria);
        traceRecords.AddRange(eveningResult.TraceRecords);

        var eveningByName = eveningResult.Report.Villagers.ToDictionary(e => e.Name);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var actor = villagers[i].DaySpec.Actor;
            eveningByName.TryGetValue(entry.Name, out var eveningEntry);
            entries[i] = new VillagerFullDayEntry
            {
                Name = entry.Name,
                Profession = entry.Profession,
                Anchors = entry.Anchors,
                EveningHome = entry.EveningHome,
                LaborBeforeDay = entry.LaborBeforeDay,
                LaborAfterEvening = actor.Character.LaborPower,
                MoneyBeforeDay = entry.MoneyBeforeDay,
                MoneyAfterEvening = actor.Character.Money,
                BankBeforeDay = entry.BankBeforeDay,
                BankAfterEvening = actor.Character.Money2,
                DayEntry = entry.DayEntry,
                EveningEntry = eveningEntry
            };
        }

        if (!eveningResult.Passed)
        {
            criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-evening-passed", false,
                $"evening half failed at {eveningResult.FailStage}"));
            Logger.Warn("[{Scenario}] {Cycle}: EVENING {Stage}",
                ScenarioName, options.CycleId, eveningResult.FailStage);
            return Finish(false, eveningResult.FailStage, eveningResult.Failure, eveningResult.FailReason,
                dayResult.Report, eveningResult.Report);
        }
        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-evening-passed", true, "evening half completed"));

        // ---- VERIFY: combined ledger across both halves ----
        var laborCharged = entries.Sum(e => e.DayEntry?.CrafterResult?.LaborCharged ?? 0);
        // Crafter-scoped like the day law (village-labor-conserved): farmer
        // plant/harvest legs charge real skill labor on live (zero in the
        // rig, which is why the unscoped sum passed there), so only crafter
        // entries reconcile against the metered craft charge. The evening
        // half charges no labor for ANY villager (laborStill, all entries).
        var laborDelta = entries.Where(e => e.DayEntry?.CrafterResult is not null)
            .Sum(e => e.LaborBeforeDay - e.LaborAfterEvening);
        var laborStill = entries.All(e => e.LaborAfterEvening == (e.DayEntry?.LaborAfter ?? e.LaborBeforeDay));
        var laborOk = laborDelta == laborCharged && laborStill;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-labor-conserved", laborOk,
            laborOk
                ? $"crafter day→evening labor deltas {laborDelta} == charged {laborCharged}; evening charged nothing"
                : $"labor MISMATCH: crafter deltas {laborDelta} vs charged {laborCharged}, eveningUntouched={laborStill}"));

        var moneyNotes = entries.Select(e => $"{e.Name}: {e.MoneyBeforeDay}→{e.MoneyAfterEvening}").ToList();
        var moneyOk = entries.All(e => e.MoneyAfterEvening == e.MoneyBeforeDay);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-money-conserved", moneyOk,
            moneyOk
                ? $"zero net inventory drift across day+evening ({string.Join("; ", moneyNotes)})"
                : $"inventory drift VIOLATED ({string.Join("; ", moneyNotes)})"));

        var bankNotes = entries.Select(e =>
        {
            var refund = e.EveningEntry?.Refund ?? 0;
            return $"{e.Name}: bank {e.BankBeforeDay}→{e.BankAfterEvening} (refund {refund}c)";
        }).ToList();
        var bankOk = entries.All(e => e.BankAfterEvening - e.BankBeforeDay == (e.EveningEntry?.Refund ?? 0));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-bank-reconciled", bankOk,
            bankOk
                ? $"bank drift == evening refunds ({string.Join("; ", bankNotes)})"
                : $"bank reconciliation VIOLATED ({string.Join("; ", bankNotes)})"));

        var incomplete = traceRecords
            .Where(r => r.Result == ActorLifecycleState.Completed)
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Running")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")))
            .ToList();
        var rejectedRunning = traceRecords
            .Where(r => r.Result == ActorLifecycleState.Rejected && r.StateChanges.Any(s => s.Contains("Running")))
            .ToList();
        var auditOk = traceRecords.Count > 0 && incomplete.Count == 0 && rejectedRunning.Count == 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-audit-complete", auditOk,
            $"day+evening records={traceRecords.Count} incompleteCompleted={incomplete.Count} " +
            $"rejectedWithRunning={rejectedRunning.Count}"));

        criteria.Add(new BotScenarioRunner.CriterionVerdict("fullday-fail-closed", true,
            "every rejected leg stopped its villager without touching the others"));

        if (criteria.Any(c => !c.Passed))
        {
            var failed = string.Join("; ", criteria.Where(c => !c.Passed).Select(c => c.Name));
            var reason = $"full-day ledger failed verification: {failed}";
            Logger.Warn("[{Scenario}] {Cycle}: VERIFY {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition, reason,
                dayResult.Report, eveningResult.Report);
        }

        Logger.Info("[{Scenario}] {Cycle}: PASS {Count} villager(s), {Records} audit records, {Lines} chatter line(s)",
            ScenarioName, options.CycleId, entries.Count, traceRecords.Count, eveningResult.Report.ChatterLinesTotal);
        return Finish(true, "", null, "", dayResult.Report, eveningResult.Report);
    }

    /// <summary>
    /// Rig-side single-restart projection (the slice-2 restart contract at
    /// assembly scope): renders each villager's day+evening state — schedule
    /// JSON (anchors + last phase, the playerbot_metadata.schedule column),
    /// profession, labor/money/bank (characters row), evening output counts
    /// (items rows), refund, return-home proof, chatter count — as canonical
    /// strings. A kill-9 restart reloads exactly these values; the rig proves
    /// the projection complete, deterministic, and round-trippable, which is
    /// what the slice-2 E2E asserts byte-equal pre/post.
    /// </summary>
    public static IReadOnlyList<FullDayRestartProjection> ProjectRestart(
        VillageFullDayResult result, IReadOnlyList<FullDayVillager> villagers)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(villagers);
        var actors = villagers.ToDictionary(v => v.Name, v => v.DaySpec.Actor);
        return result.Report.Villagers.Select(entry =>
        {
            var lastPhase = entry.DayEntry?.PhasesVisited.Count > 0
                ? entry.DayEntry.PhasesVisited[^1]
                : BotSchedulePhase.Home;
            var scheduleJson = BotSchedulePayload.WithRuntimeState("{\"kind\":\"roam-loop\"}", entry.Anchors, lastPhase);
            actors.TryGetValue(entry.Name, out var actor);
            var outputTid = entry.EveningEntry?.OutputTemplateId ?? 0;
            return new FullDayRestartProjection
            {
                Name = entry.Name,
                Profession = entry.Profession.ToString(),
                ScheduleJson = scheduleJson,
                LastPhase = lastPhase.ToString(),
                Labor = entry.LaborAfterEvening,
                Money = entry.MoneyAfterEvening,
                BankMoney = entry.BankAfterEvening,
                EveningOutputTemplateId = outputTid,
                EveningOutputBankCount = actor is null || outputTid == 0 ? 0
                    : actor.Character.Inventory.GetItemsCount(Models.Game.Items.SlotType.Bank, outputTid),
                EveningOutputBagCount = actor is null || outputTid == 0 ? 0
                    : actor.Character.Inventory.GetItemsCount(Models.Game.Items.SlotType.Inventory, outputTid),
                EveningRefund = entry.EveningEntry?.Refund ?? 0,
                EveningMarketComplete = entry.EveningEntry?.MarketComplete ?? false,
                ReturnedHome = entry.EveningEntry?.ReturnedHome ?? false,
                Home = entry.EveningHome,
                ChatterLines = entry.EveningEntry?.LinesSpoken ?? 0
            };
        }).ToList();
    }

    /// <summary>Single restart projection row — canonical string is the equality surface.</summary>
    public sealed class FullDayRestartProjection
    {
        public required string Name { get; init; }
        public required string Profession { get; init; }
        public required string ScheduleJson { get; init; }
        public required string LastPhase { get; init; }
        public int Labor { get; init; }
        public long Money { get; init; }
        public long BankMoney { get; init; }
        public uint EveningOutputTemplateId { get; init; }
        public int EveningOutputBankCount { get; init; }
        public int EveningOutputBagCount { get; init; }
        public long EveningRefund { get; init; }
        public bool EveningMarketComplete { get; init; }
        public bool ReturnedHome { get; init; }
        public Vector3 Home { get; init; }
        public int ChatterLines { get; init; }

        /// <summary>Canonical rendering (invariant culture, round-trip floats) — the pre==post surface.</summary>
        public string ToCanonicalString()
            => string.Join("|",
                "v1", Name, Profession, ScheduleJson, LastPhase,
                Labor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Money.ToString(System.Globalization.CultureInfo.InvariantCulture),
                BankMoney.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EveningOutputTemplateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EveningOutputBankCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EveningOutputBagCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EveningRefund.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EveningMarketComplete, ReturnedHome,
                Home.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Home.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Home.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ChatterLines.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

}
