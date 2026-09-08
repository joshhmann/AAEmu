using System.Linq;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;

using NLog;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M8 Crafter v1 slice-2 — merchant purchase + currency leg composer: buys
/// every recipe material row from a merchant through the real
/// <see cref="GameplayActor.Buy"/> path (CSBuyItemsPacket branch), stages the
/// purchased rows into the bank through the real
/// <see cref="GameplayActor.DepositItem"/> path, then delegates to the slice-1
/// <see cref="CrafterWorkstationCycle"/> (withdraw → craft → store) under the
/// same cycle id, and finally asserts currency conservation (money delta ==
/// sum of charged prices, no phantom currency, no phantom inputs).
///
/// Composition only: the BUY/STAGE legs call the EXISTING
/// <see cref="GameplayActor"/> actions unchanged, driven by an ordinary
/// <see cref="Character"/> through normal gameplay services (AGENTS.md #9).
/// No new engine path, no parallel trade implementation.
///
/// Default-OFF surface: static callable with no tick subscription, no
/// bootstrap, no background work — inert unless a caller invokes it. Sale,
/// vendoring, and pack output remain later slices. Reporting is a canned
/// record (LLM LAST): fixed fields only. The audit trail is the legs' own
/// <see cref="ActorAuditRecord"/> entries plus the returned result.
///
/// Fail-closed at every leg: a rejected buy (unknown merchant, out of shop
/// range, merchant does not sell the row, short funds) stops the cycle before
/// anything is crafted; a refused stage (deposit) leaves the purchased rows
/// in the bag (never deleted, never vended).
/// </summary>
public static class CrafterMerchantCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-crafter-merchant";

    /// <summary>Scenario parameters.</summary>
    public sealed record CrafterMerchantOptions
    {
        /// <summary>Idempotency namespace for this cycle's legs (shared with the inner slice-1 run; key suffixes never collide).</summary>
        public string CycleId { get; init; } = "m8cm-c0";

        /// <summary>Craft recipe id (materials → product, rate 100).</summary>
        public required uint CraftId { get; init; }

        /// <summary>Craft bench doodad ObjId (recipe ReqDoodad match).</summary>
        public required uint BenchObjId { get; init; }

        /// <summary>Merchant NPC ObjId selling every recipe material row.</summary>
        public required uint MerchantNpcObjId { get; init; }

        /// <summary>Budget for driving the in-flight craft to terminal.</summary>
        public TimeSpan CraftTimeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>Canned merchant report (LLM LAST): fixed fields only.</summary>
    public sealed class CrafterMerchantReport
    {
        public string CycleId { get; init; } = "";
        public uint CraftId { get; init; }
        public uint BenchObjId { get; init; }
        public uint MerchantNpcObjId { get; init; }
        public Dictionary<uint, int> Bought { get; init; } = new();
        public long BuyTotal { get; init; }
        public long MoneyBefore { get; init; }
        public long MoneyAfter { get; init; }
        public long MoneySpent { get; init; }
        public Dictionary<uint, int> Staged { get; init; } = new();
        public List<string> Holds { get; init; } = [];
        public Dictionary<uint, int> ProductStored { get; init; } = new();
        public Dictionary<uint, int> MaterialsConsumed { get; init; } = new();
        public int LaborCharged { get; init; }
        public bool Passed { get; init; }
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class CrafterMerchantResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public long BuyTotal { get; init; }
        public long MoneySpent { get; init; }
        public Dictionary<uint, int> MaterialsConsumed { get; init; } = new();
        public int LaborCharged { get; init; }
        public CrafterMerchantReport? Report { get; init; }
    }

    /// <summary>
    /// Runs one buy → stage → (slice-1 withdraw → craft → store) cycle:
    /// buys every recipe material row from the merchant, stages the rows
    /// into the bank, delegates to slice-1 under the same cycle id, then
    /// verifies currency conservation.
    /// </summary>
    public static CrafterMerchantResult Run(GameplayActor actor, CrafterMerchantOptions options, ICrafterPump pump)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var bought = new Dictionary<uint, int>();
        var staged = new Dictionary<uint, int>();
        var holds = new List<string>();
        long buyTotal = 0;

        CrafterMerchantResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            Dictionary<uint, int>? materialsConsumed = null, int laborCharged = 0,
            Dictionary<uint, int>? productStored = null, long moneyBefore = 0, long moneyAfter = 0)
        {
            var spent = moneyBefore - moneyAfter;
            return new CrafterMerchantResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                BuyTotal = buyTotal,
                MoneySpent = spent,
                MaterialsConsumed = materialsConsumed ?? new Dictionary<uint, int>(),
                LaborCharged = laborCharged,
                Report = new CrafterMerchantReport
                {
                    CycleId = options.CycleId,
                    CraftId = options.CraftId,
                    BenchObjId = options.BenchObjId,
                    MerchantNpcObjId = options.MerchantNpcObjId,
                    Bought = bought,
                    BuyTotal = buyTotal,
                    MoneyBefore = moneyBefore,
                    MoneyAfter = moneyAfter,
                    MoneySpent = spent,
                    Staged = staged,
                    Holds = holds,
                    ProductStored = productStored ?? new Dictionary<uint, int>(),
                    MaterialsConsumed = materialsConsumed ?? new Dictionary<uint, int>(),
                    LaborCharged = laborCharged,
                    Passed = passed
                }
            };
        }

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

        // ---- PRECHECK: known recipe (material rows + prices drive the BUY legs) ----
        var craft = CraftManager.Instance.GetCraftById(options.CraftId);
        if (craft == null)
        {
            var reason = $"unknown craft {options.CraftId}";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", true,
            $"craft {options.CraftId}, merchant {options.MerchantNpcObjId} targeted"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"craft {options.CraftId}"));

        var moneyBefore = character.Money;
        var expectedTotal = craft.CraftMaterials.Sum(row =>
            (long)(ItemManager.Instance.GetTemplate(row.ItemId)?.Price ?? 0) * row.Amount);

        // ---- BUY: every recipe material row from the merchant ----
        foreach (var row in craft.CraftMaterials)
        {
            var mark = actor.AuditTrace.Count;
            var buy = actor.Buy(options.MerchantNpcObjId, row.ItemId, row.Amount,
                idempotencyKey: $"{options.CycleId}-buy-{row.ItemId}");
            CaptureTrace(mark);
            stages.Add(Stage($"BUY-{row.ItemId}", buy, $"material {row.ItemId} x{row.Amount}"));
            if (buy.State != ActorLifecycleState.Completed)
            {
                // Fail-closed: unknown/out-of-range merchant, row not sold,
                // short funds — nothing crafted, nothing consumed. Rows
                // already bought stay in the bag (never deleted).
                var reason = $"buy of material {row.ItemId} x{row.Amount} held: {buy.Detail}";
                holds.Add(reason);
                criteria.Add(new BotScenarioRunner.CriterionVerdict("buy-completed", false, reason));
                Logger.Warn("[{Scenario}] {Cycle}: BUY {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, buy.State, buy.Failure, reason);
                return Finish(false, "BUY", buy.Failure, reason, moneyBefore: moneyBefore, moneyAfter: character.Money);
            }
            bought[row.ItemId] = row.Amount;
            buyTotal += buy.Result is long paid ? paid : 0;
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("buy-completed", true,
            $"bought {bought.Count} material row(s) for {buyTotal}"));
        var priceOk = buyTotal == expectedTotal;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("buy-price-exact", priceOk,
            $"charged {buyTotal}, template prices sum {expectedTotal}"));
        if (!priceOk)
            return Finish(false, "BUY", ActorFailureReason.StateTransition,
                $"charged {buyTotal} != template price sum {expectedTotal}",
                moneyBefore: moneyBefore, moneyAfter: character.Money);

        // ---- STAGE: the purchased rows into the bank for the slice-1 run ----
        foreach (var row in craft.CraftMaterials)
        {
            var mark = actor.AuditTrace.Count;
            var deposit = actor.DepositItem(row.ItemId, idempotencyKey: $"{options.CycleId}-stage-{row.ItemId}");
            CaptureTrace(mark);
            stages.Add(Stage($"STAGE-{row.ItemId}", deposit, $"material {row.ItemId}"));
            if (deposit.State != ActorLifecycleState.Completed)
            {
                // Hold with reason: the purchased rows stay in the bag (never
                // deleted, never vended); the slice-1 run is never entered.
                var hold = $"stage of material {row.ItemId} held: {deposit.Detail}";
                holds.Add(hold);
                criteria.Add(new BotScenarioRunner.CriterionVerdict("stage-completed", false, deposit.Detail ?? ""));
                Logger.Warn("[{Scenario}] {Cycle}: STAGE {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, deposit.State, deposit.Failure, hold);
                return Finish(false, "STAGE", deposit.Failure, hold, moneyBefore: moneyBefore, moneyAfter: character.Money);
            }
            staged[row.ItemId] = deposit.Result is int moved ? moved : 0;
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("stage-completed", true,
            $"staged {staged.Count} material row(s) into the bank"));

        // ---- SLICE-1: withdraw → craft → store under the same cycle id ----
        // (BUY/STAGE key suffixes never collide with slice-1's
        // withdraw-/craft/store- suffixes, so same-key retry dedupe composes.)
        var inner = CrafterWorkstationCycle.Run(actor, new CrafterWorkstationCycle.CrafterWorkstationOptions
        {
            CycleId = options.CycleId,
            CraftId = options.CraftId,
            BenchObjId = options.BenchObjId,
            CraftTimeout = options.CraftTimeout
        }, pump);
        stages.AddRange(inner.Stages);
        criteria.AddRange(inner.Criteria);
        traceRecords.AddRange(inner.TraceRecords);
        holds.AddRange(inner.Report?.Holds ?? []);
        if (!inner.Passed)
        {
            // Fail-closed passthrough: purchased rows sit in the bank or the
            // bag (never deleted); currency conservation still holds below.
            Logger.Warn("[{Scenario}] {Cycle}: {Stage} {Failure} {Reason}",
                ScenarioName, options.CycleId, inner.FailStage, inner.Failure, inner.FailReason);
            return Finish(false, inner.FailStage, inner.Failure, inner.FailReason,
                inner.MaterialsConsumed, inner.LaborCharged, inner.Report?.ProductStored,
                moneyBefore, character.Money);
        }

        // ---- VERIFY: currency conservation (money delta == sum of prices) ----
        var moneyAfter = character.Money;
        var spent = moneyBefore - moneyAfter;
        var currencyOk = spent == buyTotal;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("currency-conserved", currencyOk,
            $"money {moneyBefore} → {moneyAfter} (spent {spent}, charged {buyTotal})"));
        if (!currencyOk)
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition,
                $"money delta {spent} != charged {buyTotal}",
                inner.MaterialsConsumed, inner.LaborCharged, inner.Report?.ProductStored,
                moneyBefore, moneyAfter);

        var phantomOk = craft.CraftMaterials.All(row =>
            inventory.GetItemsCount(SlotType.Inventory, row.ItemId) == 0);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("no-phantom-inputs", phantomOk,
            phantomOk
                ? "bought rows fully consumed; no phantom inputs linger in the bag"
                : "unconsumed material rows linger in the bag after store"));
        if (!phantomOk)
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition,
                "unconsumed material rows linger in the bag after store",
                inner.MaterialsConsumed, inner.LaborCharged, inner.Report?.ProductStored,
                moneyBefore, moneyAfter);

        Logger.Info("[{Scenario}] {Cycle}: PASS bought {Rows} row(s) for {Total}, craft {Craft} stored",
            ScenarioName, options.CycleId, bought.Count, buyTotal, options.CraftId);
        return Finish(true, "", null, "",
            inner.MaterialsConsumed, inner.LaborCharged, inner.Report?.ProductStored,
            moneyBefore, moneyAfter);
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
