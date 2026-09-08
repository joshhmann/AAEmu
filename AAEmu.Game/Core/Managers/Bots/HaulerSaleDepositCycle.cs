using System.Linq;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

using NLog;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pump that drives an in-flight return-home drive request to a terminal
/// state. The composer owns the leg order and the conservation assertions;
/// the pump owns the tick strategy (headless rigs tick synchronously, live
/// bridges observe world ticks). Mirrors <see cref="IHaulDrivePump"/> (the
/// slice-2 shape).
/// </summary>
public interface IHaulSalePump
{
    ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget);
}

/// <summary>
/// M8 C4 hauler/trader v1 slice-3 — specialty sale → deposit → return-home
/// composer: unload the pack off the cargo point through the real
/// <see cref="GameplayActor.PackPickup"/> path, sell it at the specialty
/// gold trader through the real <see cref="GameplayActor.SellSpecialty"/>
/// path (SpecialtyManager.SellSpecialty — the exact call
/// CSSellBackpackGoodsPacket makes), bank the proceeds figure through the
/// real <see cref="GameplayActor.DepositMoney"/> path, and drive the empty
/// wagon home through the real <see cref="GameplayActor.DriveVehicle"/> path.
///
/// Composition only: every leg calls the EXISTING <see cref="GameplayActor"/>
/// actions unchanged, driven by an ordinary <see cref="Character"/> through
/// normal gameplay services (AGENTS.md #9). No new engine path, no parallel
/// pack/vehicle/economy implementation. Builds on slice-1
/// (<see cref="HaulerPackCraftLoadCycle"/>) and slice-2
/// (<see cref="HaulerDriveRouteCycle"/>): the pack is already loaded on the
/// vehicle and the actor already holds the driver seat when this cycle runs;
/// crafting, boarding, loading, and the outbound drive are NOT re-driven
/// here.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Vendoring and restart legs are later slices. Reporting is a canned record
/// (LLM LAST): fixed fields only. The audit trail is the legs' own
/// <see cref="ActorAuditRecord"/> entries plus the returned result.
///
/// Proceeds note (honest — the canonical 1.2 mail delay): the specialty
/// payout travels as reward MAIL (in transit ~22 h, never inventory money),
/// and no TakeMail actor action exists yet, so the DEPOSIT leg banks the
/// proceeds FIGURE from operating cash (bank delta == mail payout, inventory
/// delta == −payout). Mail-take → deposit of the very same copper is a later
/// slice. The full-leg ledger pins the no-dup law instead: mailΔ + bankΔ +
/// inventoryΔ == payout (a duplicated sale or a double deposit breaks it).
/// </summary>
public static class HaulerSaleDepositCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-hauler-sale-deposit";

    /// <summary>Sale labor per pack — SellSpecialty's ChangeLabor(-60, Commerce).</summary>
    public const int SellLaborCostPerPack = 60;

    /// <summary>Scenario parameters.</summary>
    public sealed record HaulerSaleDepositOptions
    {
        /// <summary>Idempotency namespace for this cycle's legs.</summary>
        public string CycleId { get; init; } = "m8c4-s0";

        /// <summary>Loaded cargo vehicle slave ObjId (driver seat already held).</summary>
        public required uint SlaveObjId { get; init; }

        /// <summary>Specialty gold trader NPC ObjId (pack sold within 2.5 m).</summary>
        public required uint GoldTraderObjId { get; init; }

        /// <summary>Home destination for the empty wagon (single return leg).</summary>
        public required Vector3 Home { get; init; }

        /// <summary>Drive speed in units/second.</summary>
        public float Speed { get; init; } = 10f;

        /// <summary>Navigation budget for the return-home leg (expiry → TimedOut(Navigation)).</summary>
        public TimeSpan DriveTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Wall-clock budget for driving the in-flight return to terminal.</summary>
        public TimeSpan PumpBudget { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class HaulerSaleDepositResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public ulong PackItemId { get; init; }
        public int BasePrice { get; init; }
        public long Payout { get; init; }
        public long BankDeposited { get; init; }
        public int LaborCharged { get; init; }
        public Vector3 ArrivalPosition { get; init; }
        public float DistanceRemaining { get; init; }
    }

    /// <summary>
    /// Runs one sale→deposit→return cycle: precheck the composition state
    /// (known vehicle, driver seat held, known gold trader, exactly one pack
    /// either aboard or already carried), unload the pack off its cargo
    /// point, sell it at the gold trader, bank the proceeds figure, drive
    /// the empty wagon home. Fail-closed at every leg: a rejection stops the
    /// cycle, records the reason, and never deletes, duplicates, or moves
    /// anything it should not.
    /// </summary>
    public static HaulerSaleDepositResult Run(GameplayActor actor, HaulerSaleDepositOptions options, IHaulSalePump pump)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        HaulerSaleDepositResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            ulong packItemId = 0, int basePrice = 0, long payout = 0, long bankDeposited = 0, int laborCharged = 0,
            Vector3 arrivalPosition = default, float distanceRemaining = 0f)
        {
            return new HaulerSaleDepositResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                PackItemId = packItemId,
                BasePrice = basePrice,
                Payout = payout,
                BankDeposited = bankDeposited,
                LaborCharged = laborCharged,
                ArrivalPosition = arrivalPosition,
                DistanceRemaining = distanceRemaining
            };
        }

        // Audit records land on terminal transitions only — capture by
        // count-mark so nothing is missed and nothing is duplicated.
        void CaptureTrace(int mark)
        {
            foreach (var record in actor.AuditTrace.Skip(mark))
                traceRecords.Add(record);
        }

        var character = actor.Character;
        var inventory = character.Inventory;
        if (inventory == null)
        {
            const string noInventory = "character has no inventory";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, noInventory));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", noInventory));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, noInventory);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, noInventory);
        }

        // ---- PRECHECK: known vehicle (the engine's own GetBaseUnit
        // resolution, the slice-2 drive-compatible lookup), driver seat
        // held, known gold trader, exactly one pack ----
        var vehicle = character.ParentWorld?.GetBaseUnit(options.SlaveObjId) as Slave;
        if (vehicle == null)
        {
            var reason = $"vehicle {options.SlaveObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        if (!vehicle.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var seated) || !ReferenceEquals(seated, character))
        {
            var reason = $"not in driver seat of vehicle {options.SlaveObjId} — board first";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.StateTransition, reason);
        }

        var trader = character.ParentWorld?.GetNpc(options.GoldTraderObjId);
        if (trader?.Template == null)
        {
            var reason = $"gold trader {options.GoldTraderObjId} not found or has no template";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var carried = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        var aboard = vehicle.AttachedDoodads.Where(d => d.ItemId > 0).ToList();
        ulong packItemId = 0;
        var needsUnload = false;
        if (carried != null && aboard.Count == 0)
        {
            packItemId = carried.Id;
        }
        else if (carried == null && aboard.Count == 1)
        {
            packItemId = aboard[0].ItemId;
            needsUnload = true;
        }
        else
        {
            var reason = carried != null
                ? $"ambiguous pack state: pack {carried.Id} carried AND {aboard.Count} pack(s) aboard vehicle {options.SlaveObjId}"
                : aboard.Count == 0
                    ? "no trade pack carried or aboard — nothing to sell"
                    : $"{aboard.Count} packs aboard vehicle {options.SlaveObjId} — multi-pack sale is out of scope for v1";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", true,
            $"vehicle {options.SlaveObjId} present, driver seat held, trader {options.GoldTraderObjId} present, " +
            $"pack instance {packItemId} {(needsUnload ? "aboard" : "carried")}"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"pack {packItemId}"));

        var moneyBefore = character.Money;
        var bankBefore = character.Money2;
        var laborBefore = character.LaborPower;
        var mailsBefore = SpecialtyMailCopper(character.Id);

        // ---- UNLOAD: pick the pack off its cargo point (RecoverItem — the
        // exact CSLootOpenBagPacket pack-pickup path). Skipped when the pack
        // is already carried (the composer never moves what is already home).
        if (needsUnload)
        {
            var cargoDoodad = aboard[0];
            var unloadMark = actor.AuditTrace.Count;
            var unload = actor.PackPickup(cargoDoodad.ObjId, idempotencyKey: $"{options.CycleId}-unload");
            CaptureTrace(unloadMark);
            stages.Add(Stage("UNLOAD", unload, $"cargo doodad {cargoDoodad.ObjId}"));
            if (unload.State != ActorLifecycleState.Completed)
            {
                // Fail-closed: the pack must still be aboard (still attached
                // AND still owned by the System container — never deleted).
                var stillAttached = vehicle.AttachedDoodads.Any(d => d.ItemId == packItemId);
                var stillOwned = inventory.SystemContainer.GetItemByItemId(packItemId) != null;
                var retained = stillAttached && stillOwned;
                var reason = unload.Detail ?? "";
                criteria.Add(new BotScenarioRunner.CriterionVerdict("unload-completed", false, reason));
                criteria.Add(new BotScenarioRunner.CriterionVerdict("unload-fail-closed-pack-retained", retained,
                    retained
                        ? $"unload refused ({reason}); pack instance {packItemId} retained aboard slave {options.SlaveObjId}"
                        : $"unload refused ({reason}) AND pack instance {packItemId} left slave {options.SlaveObjId} (attached: {stillAttached}, owned: {stillOwned})"));
                Logger.Warn("[{Scenario}] {Cycle}: UNLOAD {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, unload.State, unload.Failure, reason);
                return Finish(false, "UNLOAD", unload.Failure, reason, packItemId);
            }

            var recovered = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == packItemId;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("unload-pack-recovered", recovered,
                recovered
                    ? $"pack instance {packItemId} recovered into the Backpack slot"
                    : $"pack instance {packItemId} NOT carried after unload"));
            if (!recovered)
                return Finish(false, "UNLOAD", ActorFailureReason.StateTransition,
                    "pack pickup did not restore the Backpack slot", packItemId);
        }
        else
        {
            criteria.Add(new BotScenarioRunner.CriterionVerdict("unload-pack-recovered", true,
                $"pack instance {packItemId} already carried — no unload needed"));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("UNLOAD", 0, "Completed", "", "already carried"));
        }

        // ---- SELL: the existing action (real SpecialtyManager.SellSpecialty).
        // Fidelity repair (EconomyDayCycleScenario hauler-leg precedent):
        // ChangeLabor(-60, Commerce) indexes the Commerce actability
        // directly; live characters always carry it, headless rigs may not.
        character.Actability.Actabilities.TryAdd((uint)ActabilityType.Commerce,
            new Actability(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce }));

        // The documented payout law needs the ratio BEFORE the sale (the
        // pack must still be carried for the lookup — same read order as the
        // EconomyDayCycleScenario SELL-GOLD leg).
        var priceRatio = SpecialtyManager.Instance.GetRatioForSpecialty(character);

        var sellMark = actor.AuditTrace.Count;
        var sell = actor.SellSpecialty(options.GoldTraderObjId, idempotencyKey: $"{options.CycleId}-sell");
        CaptureTrace(sellMark);
        stages.Add(Stage("SELL", sell, $"pack {packItemId} @ trader {options.GoldTraderObjId}"));
        if (sell.State != ActorLifecycleState.Completed)
        {
            // Fail-closed hold: the pack must still be carried (never
            // consumed by a refused sale) and no proceeds mail may exist
            // (never paid without consuming). The Persistence failure is the
            // engine's own no-dup-proceeds guard (price without consumption);
            // surfacing it here keeps a retry from selling twice.
            var retained = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == packItemId;
            var mailDelta = SpecialtyMailCopper(character.Id) - mailsBefore;
            var noProceeds = mailDelta == 0;
            var reason = sell.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-completed", false, reason));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-hold-pack-retained", retained,
                retained
                    ? $"sale refused ({reason}); pack instance {packItemId} retained in the backpack slot"
                    : $"sale refused ({reason}) AND pack instance {packItemId} left the backpack slot"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-hold-no-proceeds", noProceeds,
                noProceeds
                    ? $"sale refused ({reason}); no proceeds mail created"
                    : $"sale refused ({reason}) AND {mailDelta}c of proceeds mail created without consuming the pack"));
            Logger.Warn("[{Scenario}] {Cycle}: SELL {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, sell.State, sell.Failure, reason);
            return Finish(false, "SELL", sell.Failure, reason, packItemId);
        }

        var basePrice = sell.Result is int salePrice ? salePrice : 0;

        // The documented payout law (SellSpecialty, gold trader — coin id 0,
        // no ÷10000 conversion): payout == round(base × ratio% × 1.05 interest).
        var finalNoInterest = basePrice * (priceRatio / 100f);
        var expectedPayout = (long)Math.Round(finalNoInterest + finalNoInterest * 0.05f);
        var payout = SpecialtyMailCopper(character.Id) - mailsBefore;
        var packConsumed = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) == null;
        var payoutOk = payout == expectedPayout && payout > 0 && packConsumed;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-completed", true, sell.Detail ?? "sold"));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-payout-formula", payoutOk,
            payoutOk
                ? $"payout mail {payout}c == round(base {basePrice} × {priceRatio}% × 1.05) = {expectedPayout}c; pack consumed"
                : $"payout MISMATCH: mail delta {payout}c vs expected {expectedPayout}c (base {basePrice}, ratio {priceRatio}%), packConsumed={packConsumed}"));
        if (!payoutOk)
            return Finish(false, "SELL", ActorFailureReason.StateTransition,
                "specialty payout formula violated", packItemId, basePrice);

        var laborAfterSell = character.LaborPower;
        var laborCharged = laborBefore - laborAfterSell;
        var laborOk = laborCharged == SellLaborCostPerPack;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("sell-labor-conserved", laborOk,
            $"labor {laborBefore} → {laborAfterSell} (charged {laborCharged}, expected {SellLaborCostPerPack})"));
        if (!laborOk)
            return Finish(false, "SELL", ActorFailureReason.StateTransition,
                $"specialty sale labor delta {laborCharged} != {SellLaborCostPerPack}",
                packItemId, basePrice, payout);

        // ---- DEPOSIT: bank the proceeds figure through the existing action
        // (real Character.ChangeMoney — the exact CSDepositMoneyPacket call).
        // See the proceeds note on the class: the mail itself is in transit,
        // so this banks the same copper figure from operating cash.
        var depositMark = actor.AuditTrace.Count;
        var deposit = actor.DepositMoney(payout, idempotencyKey: $"{options.CycleId}-deposit");
        CaptureTrace(depositMark);
        stages.Add(Stage("DEPOSIT", deposit, $"{payout}c"));
        if (deposit.State != ActorLifecycleState.Completed)
        {
            var reason = deposit.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("deposit-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: DEPOSIT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, deposit.State, deposit.Failure, reason);
            return Finish(false, "DEPOSIT", deposit.Failure, reason, packItemId, basePrice, payout, 0, laborCharged);
        }

        var moneyAfterDeposit = character.Money;
        var bankAfterDeposit = character.Money2;
        var bankDelta = bankAfterDeposit - bankBefore;
        var moneyDelta = moneyAfterDeposit - moneyBefore;
        var depositOk = bankDelta == payout && moneyDelta == -payout;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("deposit-completed", true, deposit.Detail ?? "deposited"));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("deposit-conservation", depositOk,
            depositOk
                ? $"bank {bankBefore} → {bankAfterDeposit} (Δ {bankDelta} == payout {payout}c); inventory Δ {moneyDelta}"
                : $"bank conservation VIOLATED: bank Δ {bankDelta} vs payout {payout}c, inventory Δ {moneyDelta}"));
        if (!depositOk)
            return Finish(false, "DEPOSIT", ActorFailureReason.StateTransition,
                "bank conservation violated", packItemId, basePrice, payout, bankDelta, laborCharged);

        // The no-dup law across the money legs: every copper created by the
        // sale is accounted exactly once (mail in transit + banked − advanced
        // operating cash == payout). A duplicated sale or a double deposit
        // breaks this equation.
        var ledgerOk = payout + bankDelta + moneyDelta == payout && bankDelta == payout;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("full-leg-ledger", ledgerOk,
            ledgerOk
                ? $"mail Δ {payout}c + bank Δ {bankDelta}c + inventory Δ {moneyDelta}c == payout {payout}c (no dup, no leak)"
                : $"ledger MISMATCH: mail Δ {payout}c + bank Δ {bankDelta}c + inventory Δ {moneyDelta}c != payout {payout}c"));
        if (!ledgerOk)
            return Finish(false, "DEPOSIT", ActorFailureReason.StateTransition,
                "full-leg ledger violated", packItemId, basePrice, payout, bankDelta, laborCharged);

        // ---- RETURN-HOME: drive the empty wagon home through the existing
        // action (real client-authored movement model, the slice-2 shape).
        var driveMark = actor.AuditTrace.Count;
        var drive = actor.DriveVehicle(options.SlaveObjId, options.Home, options.Speed,
            options.DriveTimeout, idempotencyKey: $"{options.CycleId}-return");
        CaptureTrace(driveMark);
        stages.Add(Stage("RETURN-HOME", drive, $"slave {options.SlaveObjId} → {options.Home}"));
        var shortCircuit = drive.State == ActorLifecycleState.Completed;
        if (drive.State == ActorLifecycleState.Running)
        {
            drive = pump.Drive(actor, drive, options.PumpBudget);
            CaptureTrace(driveMark);
            if (!drive.IsTerminal)
            {
                // The pump refused to advance the leg — hold with the §17
                // Navigation reason, the slice-2 close-in idiom. Never reroute.
                _ = drive.Expire(ActorFailureReason.Navigation, $"return leg exceeded its budget ({options.PumpBudget})");
                CaptureTrace(driveMark);
            }
        }

        if (drive.State != ActorLifecycleState.Completed)
        {
            var reason = drive.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("return-home-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: RETURN-HOME {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, drive.State, drive.Failure, reason);
            var rest = MathUtil.CalculateDistance(vehicle.Transform.World.Position, options.Home, false);
            return Finish(false, "RETURN-HOME", drive.Failure, reason,
                packItemId, basePrice, payout, bankDelta, laborCharged,
                vehicle.Transform.World.Position, rest);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("return-home-completed", true, drive.Detail ?? "home"));

        var arrival = vehicle.Transform.World.Position;
        var flat = MathUtil.CalculateDistance(arrival, options.Home, false);
        var zGap = Math.Abs(options.Home.Z - arrival.Z);
        var arrived = flat <= GameplayActor.ArrivalRadius && zGap <= GameplayActor.ArrivalRadius;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("return-home-arrival", arrived,
            arrived
                ? $"arrived at {arrival} (remaining flat {flat:0.###})"
                : $"short of home: remaining flat {flat:0.###}, z gap {zGap:0.###}"));
        if (!arrived)
            return Finish(false, "RETURN-HOME", ActorFailureReason.StateTransition,
                $"return drive completed without reaching home (remaining flat {flat:0.###})",
                packItemId, basePrice, payout, bankDelta, laborCharged, arrival, flat);

        // The wagon comes home empty: the sold pack was consumed, and the
        // return leg never touches cargo — any attachment is a defect.
        var stragglers = vehicle.AttachedDoodads.Count(d => d.ItemId > 0);
        var emptyOk = stragglers == 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("return-home-wagon-empty", emptyOk,
            emptyOk
                ? $"wagon {options.SlaveObjId} home empty at {arrival}"
                : $"{stragglers} pack(s) still attached to slave {options.SlaveObjId} after the sale"));
        if (!emptyOk)
            return Finish(false, "RETURN-HOME", ActorFailureReason.StateTransition,
                "pack(s) still attached after the sale",
                packItemId, basePrice, payout, bankDelta, laborCharged, arrival, flat);

        if (shortCircuit)
        {
            // Already-home is a completed no-op only when nothing moved; the
            // arrival proof above already covers the position.
            criteria.Add(new BotScenarioRunner.CriterionVerdict("return-home-already-there", true,
                $"already home at {arrival}"));
        }

        Logger.Info("[{Scenario}] {Cycle}: PASS pack {Pack} sold (base {Base}, payout {Payout}c, banked), wagon {Slave} home",
            ScenarioName, options.CycleId, packItemId, basePrice, payout, options.SlaveObjId);
        return Finish(true, "", null, "", packItemId, basePrice, payout, bankDelta, laborCharged, arrival, flat);
    }

    /// <summary>Σ CopperCoins over a character's mails — the same read surface
    /// AuctionHouseScenario uses for mail money. AllPlayerMails (not
    /// GetCurrentMailList) because the specialty payout mail's RecvDate sits a
    /// canonical 22 h in the future and GetCurrentMailList only returns
    /// delivered mail.</summary>
    private static long SpecialtyMailCopper(uint characterId)
        => MailManager.Instance.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == characterId)
            .Sum(m => m.Body.CopperCoins);

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
