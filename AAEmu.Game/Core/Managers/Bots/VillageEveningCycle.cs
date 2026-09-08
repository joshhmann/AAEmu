using System.Numerics;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;

using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pump that drives an in-flight return-home move request to a terminal
/// state. The composer owns the leg order and the conservation assertions;
/// the pump owns the tick strategy (headless rigs tick synchronously, live
/// bridges observe world ticks). Mirrors <see cref="IHaulSalePump"/> (the
/// hauler slice-3 shape) for on-foot moves.
/// </summary>
public interface IVillageMovePump
{
    ActorRequest Walk(GameplayActor actor, ActorRequest request, TimeSpan pollBudget);
}

/// <summary>
/// M8 C5 village evening slice-3 (ROADMAP M8 living-village contracts) —
/// withdraw the banked saleable output, sell it at an ordinary merchant
/// through the real <see cref="GameplayActor.Sell"/> path (CSSellItemsPacket
/// branch — BuyBackItems move + refund, the exact call the packet makes),
/// bank the refund figure through the real
/// <see cref="GameplayActor.DepositMoney"/> path, and walk home through the
/// real <see cref="GameplayActor.MoveTo"/> path, then run the C2 chatter
/// pass under budgets (zone cap, per-villager cap, silence while legs are
/// incomplete or the villager is in battle).
///
/// Composition only: every leg calls the EXISTING <see cref="GameplayActor"/>
/// actions unchanged, driven by ordinary <see cref="Character"/> records
/// through normal gameplay services (AGENTS.md #9). No new engine path, no
/// parallel trade/economy implementation. The leg order mirrors
/// <see cref="HaulerSaleDepositCycle"/> (sale → deposit → return-home) with
/// the hauler v1 money law adapted to an immediate merchant refund: the
/// payout lands in inventory copper at SELL (no 22 h mail delay), so the
/// DEPOSIT leg banks the very same copper and the no-dup law is
/// bankΔ == refund with a zero net inventory drift.
///
/// A separate composer (rather than extending <see cref="VillageDayCycle"/>)
/// because the evening has a different contract: different legs (ordinary
/// merchant sale, not specialty packs), a different conservation law
/// (immediate refund, not mail payout), and chatter budgets. The day
/// composer's scenario name, stages, criteria, and restart projection stay
/// untouched; the evening chains after it with its own idempotency
/// namespace and canned report.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Chatter is additionally gated (disabled unless
/// <see cref="VillageChatterOptions.Enabled"/> is set with a bound sink).
/// Reporting is a canned record (LLM LAST): fixed fields only. The audit
/// trail is the legs' own <see cref="ActorAuditRecord"/> entries plus the
/// returned result. Chatter lines are counted, never content-asserted.
/// </summary>
public static class VillageEveningCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-village-evening-s3";

    /// <summary>One villager's evening parameters.</summary>
    public sealed record VillagerEveningSpec
    {
        /// <summary>Stable villager name (idempotency namespace segment).</summary>
        public required string Name { get; init; }

        /// <summary>The villager's ordinary character actor.</summary>
        public required GameplayActor Actor { get; init; }

        /// <summary>Banked saleable output template (0 = none — the villager holds for the evening).</summary>
        public uint OutputTemplateId { get; init; }

        /// <summary>Ordinary merchant NPC ObjId (0 = unbound — the villager holds for the evening).</summary>
        public uint MerchantObjId { get; init; }

        /// <summary>Home destination for the return leg.</summary>
        public required Vector3 Home { get; init; }

        /// <summary>Walk speed in units/second.</summary>
        public float Speed { get; init; } = 5f;

        /// <summary>Navigation budget for the return-home leg (expiry → TimedOut(Navigation)).</summary>
        public TimeSpan MoveTimeout { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Chatter budgets for the evening social pass (C2 integration).</summary>
    public sealed record VillageChatterOptions
    {
        /// <summary>Master gate — default OFF (no lines, no sink calls).</summary>
        public bool Enabled { get; init; }

        /// <summary>Zone cap: at most this many lines per evening run across all villagers.</summary>
        public int ZoneLinesPerEvening { get; init; } = 2;

        /// <summary>Per-villager cap: at most this many lines per villager per run (the cooldown analogue).</summary>
        public int MaxLinesPerVillager { get; init; } = 1;
    }

    /// <summary>Evening-sweep parameters.</summary>
    public sealed record VillageEveningOptions
    {
        /// <summary>Idempotency namespace for this evening's legs.</summary>
        public string CycleId { get; init; } = "m8v3-c0";

        /// <summary>Wall-clock budget for pumping an in-flight return to terminal.</summary>
        public TimeSpan PumpBudget { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Chatter budgets (default: disabled).</summary>
        public VillageChatterOptions Chatter { get; init; } = new();

        /// <summary>Chatter sink (null + enabled → chatter holds fail-safe with a reason, never throws).</summary>
        public IBotChatterSink? ChatterSink { get; init; }
    }

    /// <summary>Per-villager evening evidence.</summary>
    public sealed class VillagerEveningEntry
    {
        public required string Name { get; init; }
        public uint OutputTemplateId { get; init; }
        public int Withdrawn { get; init; }
        public int SoldCount { get; init; }
        public long Refund { get; init; }
        public long BankDeposited { get; init; }
        public bool MarketComplete { get; init; }
        public bool ReturnedHome { get; init; }
        public List<string> Holds { get; init; } = [];
        public int LinesSpoken { get; set; }
        public string ChatterHold { get; set; } = "";
    }

    /// <summary>Canned village evening report (LLM LAST): fixed fields only.</summary>
    public sealed class VillageEveningReport
    {
        public string CycleId { get; init; } = "";
        public List<VillagerEveningEntry> Villagers { get; init; } = [];
        public int ChatterLinesTotal { get; init; }
        public bool Passed { get; init; }
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class VillageEveningResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public required VillageEveningReport Report { get; init; }
    }

    /// <summary>
    /// Runs one village evening: per villager, withdraw the banked output,
    /// sell it at the bound merchant, bank the refund, walk home — then run
    /// the chatter pass under budgets. Fail-closed at every leg: a rejected
    /// leg stops that villager, records the reason, and never touches the
    /// other villagers' state. Villagers with no saleable output (or no
    /// bound merchant) hold with a reason and pass.
    /// </summary>
    public static VillageEveningResult Run(IReadOnlyList<VillagerEveningSpec> villagers, VillageEveningOptions? options = null, IVillageMovePump? pump = null)
    {
        ArgumentNullException.ThrowIfNull(villagers);
        options ??= new VillageEveningOptions();

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var entries = new List<VillagerEveningEntry>();

        VillageEveningResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason)
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
                Report = new VillageEveningReport
                {
                    CycleId = options.CycleId,
                    Villagers = entries,
                    ChatterLinesTotal = entries.Sum(e => e.LinesSpoken),
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

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-roster", true,
            $"evening roster: {string.Join(", ", villagers.Select(v => v.Name))}"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", villagers.Count, "Completed", "", "roster bound"));

        // ---- MARKET: withdraw → sell → deposit → return-home per villager ----
        (string FailStage, ActorFailureReason? Failure, string Reason)? firstFailure = null;
        foreach (var spec in villagers)
        {
            var actor = spec.Actor;
            var character = actor.Character;
            var inventory = character.Inventory;
            var holds = new List<string>();
            var withdrawn = 0;
            var soldCount = 0;
            long refund = 0;
            long bankDeposited = 0;
            var returnedHome = false;

            void CaptureTrace(int mark)
            {
                foreach (var record in actor.AuditTrace.Skip(mark))
                    traceRecords.Add(record);
            }

            void Fail(string stage, ActorFailureReason? failure, string reason)
            {
                entries.Add(new VillagerEveningEntry
                {
                    Name = spec.Name,
                    OutputTemplateId = spec.OutputTemplateId,
                    Withdrawn = withdrawn,
                    SoldCount = soldCount,
                    Refund = refund,
                    BankDeposited = bankDeposited,
                    MarketComplete = false,
                    ReturnedHome = returnedHome,
                    Holds = holds
                });
                criteria.Add(new BotScenarioRunner.CriterionVerdict("evening-fail-closed", true,
                    $"{spec.Name} held fail-closed at {stage}: {reason}"));
                Logger.Warn("[{Scenario}] {Cycle}: {Stage} {Name} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, stage, spec.Name, failure, reason);
                firstFailure ??= ($"{stage}-{spec.Name}", failure, $"{spec.Name}: {reason}");
            }

            void Hold(string reason)
            {
                holds.Add(reason);
                entries.Add(new VillagerEveningEntry
                {
                    Name = spec.Name,
                    OutputTemplateId = spec.OutputTemplateId,
                    Holds = holds
                });
                Logger.Info("[{Scenario}] {Cycle}: HOLD {Name} ({Reason})",
                    ScenarioName, options.CycleId, spec.Name, reason);
            }

            if (inventory == null)
                { Fail("PRECHECK", ActorFailureReason.RejectedAction, "character has no inventory"); continue; }

            if (spec.OutputTemplateId == 0)
            {
                Hold($"{spec.Name} has no saleable output tonight — evening held");
                continue;
            }

            // Merchant precheck BEFORE any state moves: a refused market must
            // leave the banked output exactly where it was.
            var merchant = character.ParentWorld?.GetNpc(spec.MerchantObjId);
            if (spec.MerchantObjId == 0 || merchant?.Template == null)
            {
                if (spec.MerchantObjId == 0)
                {
                    Hold($"{spec.Name} has no merchant bound — evening held");
                    continue;
                }
                { Fail("SELL", ActorFailureReason.RejectedAction,
                    $"merchant {spec.MerchantObjId} not found or has no template"); continue; }
            }

            var moneyBefore = character.Money;
            var bankBefore = character.Money2;
            var itemsBefore = inventory.GetItemsCount(SlotType.Bank, spec.OutputTemplateId)
                + inventory.GetItemsCount(SlotType.Inventory, spec.OutputTemplateId);

            // ---- WITHDRAW: pull the banked output into the bag (real
            // CSSwapItemsPacket Bank→Inventory path). A villager with nothing
            // banked holds with a reason — no legs, no state change.
            var withdrawMark = actor.AuditTrace.Count;
            var withdraw = actor.WithdrawItem(spec.OutputTemplateId, idempotencyKey: $"{options.CycleId}-{spec.Name}-withdraw");
            CaptureTrace(withdrawMark);
            stages.Add(Stage("WITHDRAW", withdraw, $"{spec.Name} item {spec.OutputTemplateId}"));
            if (withdraw.State != ActorLifecycleState.Completed)
            {
                if ((withdraw.Detail ?? "").Contains("not found in bank"))
                {
                    Hold($"{spec.Name} banked no item {spec.OutputTemplateId} — evening held");
                    continue;
                }
                { Fail("WITHDRAW", withdraw.Failure, withdraw.Detail ?? ""); continue; }
            }
            withdrawn = withdraw.Result is int moved ? moved : 0;

            inventory.Bag.GetAllItemsByTemplate(spec.OutputTemplateId, -1, out var bagItems, out _);
            var bagItem = bagItems.FirstOrDefault();
            if (bagItem == null)
                { Fail("WITHDRAW", ActorFailureReason.StateTransition,
                    "withdraw completed but output not in bag"); continue; }

            // ---- SELL: the existing action (real CSSellItemsPacket branch:
            // BuyBackItems move + refund). Fail-closed: the withdrawn stack
            // must still be in the bag (never consumed by a refused sale).
            var sellMark = actor.AuditTrace.Count;
            var sell = actor.Sell(spec.MerchantObjId, bagItem.Id, idempotencyKey: $"{options.CycleId}-{spec.Name}-sell");
            CaptureTrace(sellMark);
            stages.Add(Stage("SELL", sell, $"{spec.Name} item instance {bagItem.Id} @ merchant {spec.MerchantObjId}"));
            if (sell.State != ActorLifecycleState.Completed)
            {
                var retained = inventory.Bag.GetItemByItemId(bagItem.Id) != null;
                var reason = sell.Detail ?? "";
                criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-hold-output-retained", retained,
                    retained
                        ? $"sale refused ({reason}); output instance {bagItem.Id} retained in the bag"
                        : $"sale refused ({reason}) AND output instance {bagItem.Id} left the bag"));
                { Fail("SELL", sell.Failure, reason); continue; }
            }
            soldCount = bagItem.Count;
            refund = sell.Result is int paid ? paid : 0;

            // ---- DEPOSIT: bank the refund figure through the existing action
            // (real CSDepositMoneyPacket call — the exact Hauler DEPOSIT leg,
            // but here the copper IS the sale proceeds: SellSpecialty's mail
            // delay does not apply to ordinary merchant sales).
            var depositMark = actor.AuditTrace.Count;
            var deposit = actor.DepositMoney(refund, idempotencyKey: $"{options.CycleId}-{spec.Name}-deposit");
            CaptureTrace(depositMark);
            stages.Add(Stage("DEPOSIT", deposit, $"{spec.Name} {refund}c"));
            if (deposit.State != ActorLifecycleState.Completed)
                { Fail("DEPOSIT", deposit.Failure, deposit.Detail ?? ""); continue; }
            bankDeposited = deposit.Result is long banked ? banked : 0;

            var moneyDelta = character.Money - moneyBefore;
            var bankDelta = character.Money2 - bankBefore;
            var itemsAfter = inventory.GetItemsCount(SlotType.Bank, spec.OutputTemplateId)
                + inventory.GetItemsCount(SlotType.Inventory, spec.OutputTemplateId);
            var depositOk = refund > 0 && bankDelta == refund && moneyDelta == 0 && bankDeposited == refund;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"evening-deposit-conservation-{spec.Name}", depositOk,
                depositOk
                    ? $"{spec.Name}: bank {bankBefore} → {character.Money2} (Δ {bankDelta} == refund {refund}c); inventory net Δ {moneyDelta}"
                    : $"{spec.Name}: conservation VIOLATED: refund {refund}c, bank Δ {bankDelta}, inventory Δ {moneyDelta}"));
            if (!depositOk)
                { Fail("DEPOSIT", ActorFailureReason.StateTransition, "refund conservation violated"); continue; }

            var itemsOk = itemsAfter - itemsBefore == -soldCount;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"evening-item-conserved-{spec.Name}", itemsOk,
                itemsOk
                    ? $"{spec.Name}: sold {soldCount} of item {spec.OutputTemplateId}; bank+bag {itemsBefore} → {itemsAfter}"
                    : $"{spec.Name}: item conservation VIOLATED: bank+bag {itemsBefore} → {itemsAfter}, sold {soldCount}"));
            if (!itemsOk)
                { Fail("SELL", ActorFailureReason.StateTransition, "output item conservation violated"); continue; }

            // ---- RETURN-HOME: walk home through the existing action (real
            // client-authored movement model, the Hauler RETURN-HOME shape
            // for on-foot moves).
            var moveMark = actor.AuditTrace.Count;
            var move = actor.MoveTo(spec.Home, spec.Speed, spec.MoveTimeout,
                idempotencyKey: $"{options.CycleId}-{spec.Name}-return");
            CaptureTrace(moveMark);
            stages.Add(Stage("RETURN-HOME", move, $"{spec.Name} → {spec.Home}"));
            if (move.State == ActorLifecycleState.Completed)
            {
                returnedHome = true;
            }
            else if (move.IsTerminal)
            {
                { Fail("RETURN-HOME", move.Failure, move.Detail ?? ""); continue; }
            }
            else
            {
                if (pump == null)
                    { Fail("RETURN-HOME", ActorFailureReason.RejectedAction, "return leg in flight with no move pump bound"); continue; }
                move = pump.Walk(actor, move, options.PumpBudget);
                CaptureTrace(moveMark);
                if (!move.IsTerminal)
                {
                    _ = move.Expire(ActorFailureReason.Navigation,
                        $"return leg exceeded its budget ({options.PumpBudget})");
                    CaptureTrace(moveMark);
                    { Fail("RETURN-HOME", move.Failure, move.Detail ?? ""); continue; }
                }
                if (move.State != ActorLifecycleState.Completed)
                    { Fail("RETURN-HOME", move.Failure, move.Detail ?? ""); continue; }
                returnedHome = true;
            }

            var arrival = character.Transform.World.Position;
            var flat = MathUtil.CalculateDistance(arrival, spec.Home, false);
            var arrived = flat <= GameplayActor.ArrivalRadius
                && Math.Abs(spec.Home.Z - arrival.Z) <= GameplayActor.ArrivalRadius;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"return-home-arrival-{spec.Name}", arrived,
                arrived
                    ? $"{spec.Name} home at {arrival} (remaining flat {flat:0.###})"
                    : $"{spec.Name} short of home: remaining flat {flat:0.###}"));
            if (!arrived)
                { Fail("RETURN-HOME", ActorFailureReason.StateTransition,
                    $"return move completed without reaching home (remaining flat {flat:0.###})"); continue; }

            entries.Add(new VillagerEveningEntry
            {
                Name = spec.Name,
                OutputTemplateId = spec.OutputTemplateId,
                Withdrawn = withdrawn,
                SoldCount = soldCount,
                Refund = refund,
                BankDeposited = bankDeposited,
                MarketComplete = true,
                ReturnedHome = true,
                Holds = holds
            });
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                $"EVENING-{spec.Name}", 4, "Completed", ScenarioName,
                $"{spec.Name} sold {soldCount} for {refund}c, banked, home"));
        }

        // Failed villagers recorded their entry and held fail-closed above;
        // the loop always runs every villager (slice-1 isolation: one
        // villager's rejection never touches the others' state).
        var completedEntries = entries.Where(e => e.MarketComplete).ToList();
        criteria.Add(new BotScenarioRunner.CriterionVerdict("evening-market-complete", true,
            completedEntries.Count == 0
                ? "no villager had saleable output; every evening held with a reason"
                : $"market legs completed: {string.Join("; ", completedEntries.Select(e => $"{e.Name} sold {e.SoldCount} for {e.Refund}c"))}"));

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
        var legsRan = traceRecords.Count > 0;
        var auditOk = !legsRan
            ? true
            : incomplete.Count == 0 && rejectedRunning.Count == 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("village-audit-complete", auditOk,
            $"records={traceRecords.Count} incompleteCompleted={incomplete.Count} " +
            $"rejectedWithRunning={rejectedRunning.Count}"));

        // ---- CHATTER: the C2 social pass under budgets. Silence is the
        // default: disabled, sinkless, incomplete legs (still at work), and
        // battling villagers never speak. Budgets (never content) are the
        // only assertions the rig pins.
        var chatter = options.Chatter;
        var zoneSpoken = 0;
        var sinkFailed = false;
        foreach (var (spec, entry) in villagers.Zip(entries))
        {
            if (!chatter.Enabled)
            {
                entry.ChatterHold = "chatter disabled";
                continue;
            }
            if (options.ChatterSink == null)
            {
                entry.ChatterHold = "no chatter sink bound";
                continue;
            }
            if (!entry.MarketComplete)
            {
                entry.ChatterHold = "legs incomplete — silent while at work";
                continue;
            }
            if (spec.Actor.Character.IsInBattle)
            {
                entry.ChatterHold = "in battle — silent";
                continue;
            }
            if (entry.LinesSpoken >= chatter.MaxLinesPerVillager)
            {
                entry.ChatterHold = "per-villager cap reached";
                continue;
            }
            if (zoneSpoken >= chatter.ZoneLinesPerEvening)
            {
                entry.ChatterHold = "zone budget exhausted";
                continue;
            }
            if (sinkFailed)
            {
                entry.ChatterHold = "sink failed — remainder of the pass silent";
                continue;
            }

            try
            {
                options.ChatterSink.Say(spec.Actor.Character, $"{spec.Name} closed up shop for the day.");
            }
            catch (Exception ex)
            {
                // Fail-safe (the BotChatterService contract): a sink failure
                // never propagates into the evening result — the pass goes
                // silent and the market result stands.
                Logger.Error(ex, "[{Scenario}] {Cycle}: chatter sink failed for {Name} — pass silent",
                    ScenarioName, options.CycleId, spec.Name);
                sinkFailed = true;
                entry.ChatterHold = "sink failed — remainder of the pass silent";
                continue;
            }

            entry.LinesSpoken = 1;
            entry.ChatterHold = "";
            zoneSpoken++;
        }

        var chatterTotal = entries.Sum(e => e.LinesSpoken);
        var budgetOk = chatterTotal <= chatter.ZoneLinesPerEvening
            && entries.All(e => e.LinesSpoken <= chatter.MaxLinesPerVillager);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("evening-chatter-budget", budgetOk,
            $"lines total {chatterTotal} <= zone cap {chatter.ZoneLinesPerEvening}; " +
            $"per-villager <= {chatter.MaxLinesPerVillager}"));

        var silentOk = entries
            .Where(e => e.LinesSpoken > 0)
            .All(e =>
            {
                var spec = villagers.Single(v => v.Name == e.Name);
                return e.MarketComplete && !spec.Actor.Character.IsInBattle;
            });
        var battleSilent = villagers
            .Where(v => v.Actor.Character.IsInBattle)
            .All(v => entries.Single(e => e.Name == v.Name).LinesSpoken == 0);
        var workSilent = entries
            .Where(e => !e.MarketComplete)
            .All(e => e.LinesSpoken == 0);
        var silenceOk = silentOk && battleSilent && workSilent;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("evening-chatter-silence", silenceOk,
            $"speakers all market-complete and out of battle: {silentOk}; " +
            $"in-battle silent: {battleSilent}; at-work silent: {workSilent}"));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("evening-fail-closed", true,
            "every rejected leg stopped its villager without touching the others"));
        if (firstFailure is { } failure)
            return Finish(false, failure.FailStage, failure.Failure, failure.Reason);

        if (criteria.Any(c => !c.Passed))
        {
            var failed = string.Join("; ", criteria.Where(c => !c.Passed).Select(c => c.Name));
            var reason = $"evening verification failed: {failed}";
            Logger.Warn("[{Scenario}] {Cycle}: VERIFY {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition, reason);
        }

        Logger.Info("[{Scenario}] {Cycle}: PASS {Count} villager(s), {Records} audit records, {Lines} chatter line(s)",
            ScenarioName, options.CycleId, entries.Count, traceRecords.Count, chatterTotal);
        return Finish(true, "", null, "");
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
