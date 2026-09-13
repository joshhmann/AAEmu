using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.CommonFarm.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// N1 static composer (M9 substrate slice N1): one needs-driven decision
/// cycle routing Harvest-vs-Buy-vs-Rest through EXISTING
/// <see cref="IGameplayActor"/> actions only.
///
/// Decision discipline (the M5 contract, <see cref="EconomyDecisionScenario"/>
/// precedent):
///   - perception rides <see cref="BotObservedContext.Capture"/> (Observe —
///     direct server-state query, no packets); the needs snapshot is built
///     from that immutable context plus the caller's item-id mapping;
///   - hard legality is evaluated BEFORE preference, and every hard
///     precondition reads ONLY the immutable observation context and the
///     options (never live world reads — range, liveness, and existence stay
///     the engine's own fail-closed gates at dispatch);
///   - selection is deterministic (the fixed-priority
///     <see cref="BotDecisionSelector"/> with personality weight 0 —
///     Personality is never read or mutated);
///   - dispatch calls the existing actor methods only — Harvest, Buy, Craft,
///     Plant, Sell, Stop — no new gameplay path, no direct DB / Transform /
///     ZoneId / GM / reflection shortcuts;
///   - urgency at or below the rest line offers Rest alone; otherwise Work
///     candidates (Craft, then Plant, then Buy, then Harvest, then Sell) are
///     offered with the always-legal Rest fallback, so an empty or impossible
///     work set idles instead of throwing. Sell is the earn leg: below every
///     other work candidate, above Rest — it only wins when Buy is refused
///     for funds and a sellable surplus sits in the bag.
///
/// Rest rides <see cref="IGameplayActor.Stop"/> (the existing halt action —
/// there is no separate rest action in the v1 vocabulary). Craft is a
/// candidate, not a separate path: its labor precondition refuses BEFORE
/// dispatch, so a step that could never complete never burns the engine
/// craft queue slot.
///
/// Slice boundaries (hard): NO tick service, NO persistence, NO hunger
/// simulation, NO labor-regen/economy-price changes, NO Personality
/// mutation, NO new columns, no <c>CrimeManager</c>/<c>TrialManager</c>
/// touches, no DI registration — inert library code (default-OFF by
/// construction: nothing drives it unless a caller runs it).
/// </summary>
public static class NeedsDecisionScenario
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "n1-needs-decision";

    /// <summary>
    /// Scenario parameters. Defaults configure no work target, so a default
    /// run refuses every work candidate and idles (never throws).
    /// BuyUnitPrice is caller-provided (the engine revalidates the exact
    /// template price at dispatch); CraftLaborCost must match the recipe
    /// skill's labor cost (the engine revalidates the adjusted cost).
    /// </summary>
    public sealed record NeedsOptions
    {
        /// <summary>Idempotency namespace for this run's legs.</summary>
        public string CycleId { get; init; } = "n1";

        /// <summary>Policy version stamped on every proposal.</summary>
        public string PolicyVersion { get; init; } = "needs-v1";
        /// <summary>Live crop/lumber doodad objId the harvest candidate targets (0 = unconfigured).</summary>
        public uint HarvestDoodadObjId { get; init; }

        /// <summary>Seed item template the plant candidate sows (0 = unconfigured).</summary>
        public uint PlantSeedItemTemplateId { get; init; }

        /// <summary>World position the plant candidate sows at. Null = unconfigured.</summary>
        public Vector3? PlantPosition { get; init; }

        /// <summary>Merchant NPC objId the buy candidate targets (0 = unconfigured).</summary>
        public uint MerchantNpcObjId { get; init; }

        /// <summary>Item template the buy candidate purchases (0 = unconfigured).</summary>
        public uint BuyItemTemplateId { get; init; }

        /// <summary>Quantity the buy candidate purchases.</summary>
        public int BuyCount { get; init; } = 1;

        /// <summary>Copper price per unit the buy candidate expects (0 = unconfigured).</summary>
        public long BuyUnitPrice { get; init; }

        /// <summary>Craft recipe id the craft candidate runs (0 = unconfigured).</summary>
        public uint CraftId { get; init; }

        /// <summary>Craft bench doodad objId (0 = unconfigured).</summary>
        public uint CraftBenchObjId { get; init; }

        /// <summary>Craft material item template id.</summary>
        public uint CraftMaterialItemId { get; init; }

        /// <summary>Craft material units required per step.</summary>
        public int CraftMaterialAmount { get; init; } = 2;

        /// <summary>Labor the craft step costs (the refusal line).</summary>
        public int CraftLaborCost { get; init; } = 10;

        /// <summary>Bag template counted as food for the FoodNeed proxy (0 = none held).</summary>
        public uint FoodItemTemplateId { get; init; }

        /// <summary>Bag template counted as lumber (0 = none held).</summary>
        public uint LumberItemTemplateId { get; init; }

        /// <summary>Labor scale the snapshot is measured against.</summary>
        public int LaborCap { get; init; } = BotNeedsEvaluator.DefaultLaborCap;

        /// <summary>Need targets and the rest line.</summary>
        public BotNeedsThresholds Thresholds { get; init; } = new();
        // ---- fixed priorities (policy; personality weight stays 0) ----
        public int CraftPriority { get; init; } = 30;
        public int PlantPriority { get; init; } = 25;
        public int BuyPriority { get; init; } = 20;
        public int HarvestPriority { get; init; } = 10;
        // Earn leg: below every other work candidate, above Rest (0). Only
        // wins when higher work is refused and a sellable surplus is held.
        public int SellPriority { get; init; } = 5;
        /// <summary>Merchant NPC objId the sell candidate targets (0 = unconfigured).</summary>
        public uint SellMerchantNpcObjId { get; init; }
        /// <summary>Bag template the sell candidate liquidates (0 = none held).</summary>
        public uint SellSurplusItemTemplateId { get; init; }
    }

    /// <summary>Structured run result — decision-path evidence attached.</summary>
    public sealed class NeedsRunResult
    {
        public required string Scenario { get; init; }
        public bool WorkSelected { get; init; }
        public BotNeedKind DominantNeed { get; init; }
        public double Urgency { get; init; }
        public BotNeeds Needs { get; init; } = BotNeedsEvaluator.Evaluate(null);
        /// <summary>The action the deterministic selector chose (null = run failed before selection).</summary>
        public ActorActionType? SelectedAction { get; init; }
        /// <summary>The dispatched request (null when nothing was dispatched).</summary>
        public ActorRequest? Request { get; init; }
        /// <summary>Why each non-selected candidate was refused (legality-before-preference evidence).</summary>
        public IReadOnlyList<BotProposalRejection> Rejections { get; init; } = [];
        public string Explanation { get; init; } = "";
        /// <summary>Whether the selected proposal's terminal postcondition held.</summary>
        public bool ExpectedPostconditionSatisfied { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        /// <summary>The actor's full audit trace, in execution order.</summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
    }

    /// <summary>Runs one needs-driven decision cycle on a live actor.</summary>
    public static NeedsRunResult Run(GameplayActor actor, NeedsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var opts = options ?? new NeedsOptions();

        try
        {
            // ---------------------------------------------------- 1. PERCEIVE
            var context = BotObservedContext.Capture(actor);
            var snapshot = new BotNeedsSnapshot(
                Money: context.Money,
                LaborPower: context.LaborPower,
                LaborCap: opts.LaborCap,
                FoodCount: BagCountOf(context, opts.FoodItemTemplateId),
                LumberCount: BagCountOf(context, opts.LumberItemTemplateId));
            var needs = BotNeedsEvaluator.Evaluate(snapshot, opts.Thresholds);

            // ---------------------------------------------------- 2. DECIDE
            var foodBefore = snapshot.FoodCount;
            var moneyBefore = snapshot.Money;
            var materialBefore = BagCountOf(context, opts.CraftMaterialItemId);
            var buyItemBefore = BagCountOf(context, opts.BuyItemTemplateId);
            var seedBefore = BagCountOf(context, opts.PlantSeedItemTemplateId);
            // Perception-time plant placement gate (the same legality the Plant
            // engine path pre-flights — public-farm membership plus the
            // CommonFarmGameData allowlist the CanPlace gate reads — resolved
            // here through ordinary service reads, so the plant preconditions
            // below close over the result and never touch live world state
            // during selection; the engine revalidates placement fail-closed
            // at dispatch).
            var (plantOnPublicFarm, plantDoodadAllowed) = ResolvePlantFarmGate(actor, opts);
            var proposals = new List<BotDecisionProposal>
            {
                RestProposal(actor, opts, moneyBefore)
            };
            if (needs.Urgency > opts.Thresholds.RestUrgency)
            {
                proposals.Add(CraftProposal(actor, opts, materialBefore));
                proposals.Add(PlantProposal(actor, opts, seedBefore, plantOnPublicFarm, plantDoodadAllowed));
                proposals.Add(BuyProposal(actor, opts, buyItemBefore));
                proposals.Add(HarvestProposal(actor, opts, foodBefore));
                proposals.Add(SellProposal(actor, opts, moneyBefore));
            }
            var decision = BotDecisionSelector.Select(context, proposals);
            if (!decision.HasProposal)
            {
                return Fail("DECIDE", ActorFailureReason.WrongDecision,
                    $"no legal needs proposal: {decision.Explanation}", actor, needs, null, decision.Rejections);
            }

            // ---------------------------------------------------- 3. EXECUTE
            var execution = BotDecisionCycle.Execute(actor, context, decision.Proposal!,
                static (gameplayActor, proposal) => Dispatch(gameplayActor, proposal));
            var request = execution.Request;
            if (!request.IsTerminal)
            {
                return Fail("EXECUTE", ActorFailureReason.Starvation,
                    $"{request.Action} left the terminal surface (no tick service in N1)",
                    actor, needs, decision.Proposal.Action, decision.Rejections, request);
            }

            var workSelected = decision.Proposal.Action != ActorActionType.Stop;
            return new NeedsRunResult
            {
                Scenario = ScenarioName,
                WorkSelected = workSelected,
                DominantNeed = needs.DominantNeed,
                Urgency = needs.Urgency,
                Needs = needs,
                SelectedAction = decision.Proposal.Action,
                Request = request,
                Rejections = decision.Rejections,
                Explanation = decision.Explanation,
                ExpectedPostconditionSatisfied = execution.ExpectedPostconditionSatisfied,
                TraceRecords = [.. actor.AuditTrace]
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor,
                BotNeedsEvaluator.Evaluate(null), null, []);
        }
    }
    private static BotDecisionProposal CraftProposal(GameplayActor actor, NeedsOptions opts, int materialBefore)
        => new(
            goal: "needs.craft",
            action: ActorActionType.Craft,
            targetId: opts.CraftId,
            expectedPostcondition: new BotProposalPostcondition(
                $"craft {opts.CraftId} consumes item {opts.CraftMaterialItemId} below the {materialBefore} before the step",
                observed => BagCountOf(observed, opts.CraftMaterialItemId) < materialBefore),
            idempotencyKey: $"needs:{actor.ActorId}:{opts.CycleId}:craft",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "work off the lumber/material need at the bench",
            policyVersion: opts.PolicyVersion,
            priority: opts.CraftPriority,
            tieBreakKey: $"craft:{opts.CraftId}",
            payload: new CraftParams(opts.CraftBenchObjId),
            hardPreconditions:
            [
                new BotProposalPrecondition("craft-configured", _ => opts.CraftId != 0),
                new BotProposalPrecondition("bench-configured", _ => opts.CraftBenchObjId != 0),
                new BotProposalPrecondition("materials-present",
                    observed => BagCountOf(observed, opts.CraftMaterialItemId) >= opts.CraftMaterialAmount),
                new BotProposalPrecondition("labor-sufficient",
                    observed => observed.LaborPower >= opts.CraftLaborCost)
            ]);
    private static BotDecisionProposal PlantProposal(GameplayActor actor, NeedsOptions opts, int seedBefore, bool onPublicFarm, bool doodadAllowed)
        => new(
            goal: "needs.plant",
            action: ActorActionType.Plant,
            targetId: opts.PlantSeedItemTemplateId,
            expectedPostcondition: new BotProposalPostcondition(
                $"plant consumes seed item {opts.PlantSeedItemTemplateId} below the {seedBefore} before the step",
                observed => BagCountOf(observed, opts.PlantSeedItemTemplateId) < seedBefore),
            idempotencyKey: $"needs:{actor.ActorId}:{opts.CycleId}:plant",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "sow the seed off the food/gold need",
            policyVersion: opts.PolicyVersion,
            priority: opts.PlantPriority,
            tieBreakKey: $"plant:{opts.PlantSeedItemTemplateId}",
            payload: opts.PlantPosition is { } position ? new PlantParams(position, 0f, 1f) : null,
            hardPreconditions:
            [
                new BotProposalPrecondition("seed-configured", _ => opts.PlantSeedItemTemplateId != 0),
                new BotProposalPrecondition("position-configured", _ => opts.PlantPosition is { } position && position.IsFinite()),
                new BotProposalPrecondition("seed-present",
                    observed => BagCountOf(observed, opts.PlantSeedItemTemplateId) > 0),
                new BotProposalPrecondition("on-public-farm", _ => onPublicFarm),
                new BotProposalPrecondition("farm-allows-doodad", _ => doodadAllowed)
            ]);
    /// <summary>
    /// Perception-time plant placement gate (ordinary service reads — the same
    /// gates the <see cref="IGameplayActor.Plant"/> engine path pre-flights:
    /// public-farm membership via <c>PublicFarmManager.InPublicFarm</c> /
    /// <c>GetFarmType</c> plus the <c>CommonFarmGameData</c> allowlist the
    /// <c>CanPlace</c> gate reads for the doodad type). The engine revalidates
    /// placement (including the per-farm count cap) fail-closed at dispatch;
    /// this gate only mirrors the farm-membership and doodad-type legs so an
    /// off-farm or disallowed plant is refused BEFORE preference and the
    /// decision falls through to buy/harvest/rest. <c>CanPlace</c> itself is
    /// never called here because it emits error packets on failure (a
    /// decision-time side effect).
    /// </summary>
    private static (bool OnPublicFarm, bool DoodadAllowed) ResolvePlantFarmGate(GameplayActor actor, NeedsOptions opts)
    {
        if (opts.PlantSeedItemTemplateId == 0 || opts.PlantPosition is not { } position || !position.IsFinite())
            return (false, false);
        var world = actor.Character.ParentWorld;
        if (world == null)
            return (false, false);
        if (!PublicFarmManager.Instance.InPublicFarm(world.Template, position))
            return (false, false);
        var farmType = PublicFarmManager.Instance.GetFarmType(world, position);
        if (farmType == FarmType.Invalid)
            return (false, false);
        var doodadId = ItemManager.Instance.GetDoodadIdFromItem(opts.PlantSeedItemTemplateId);
        if (doodadId == 0)
            return (true, false);
        return (true, CommonFarmGameData.Instance.GetAllowedDoodads(farmType).Contains(doodadId));
    }
    private static int BagCountOf(BotObservedContext context, uint itemTemplateId)
        => itemTemplateId != 0 && context.BagItemCounts.TryGetValue(itemTemplateId, out var count) ? count : 0;

    private static BotDecisionProposal BuyProposal(GameplayActor actor, NeedsOptions opts, int itemBefore)
        => new(
            goal: "needs.buy",
            action: ActorActionType.Buy,
            targetId: opts.MerchantNpcObjId,
            expectedPostcondition: new BotProposalPostcondition(
                $"bag holds at least {itemBefore + opts.BuyCount} of item {opts.BuyItemTemplateId}",
                observed => BagCountOf(observed, opts.BuyItemTemplateId) >= itemBefore + opts.BuyCount),
            idempotencyKey: $"needs:{actor.ActorId}:{opts.CycleId}:buy",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "buy goods off the food/material need",
            policyVersion: opts.PolicyVersion,
            priority: opts.BuyPriority,
            tieBreakKey: $"buy:{opts.BuyItemTemplateId}",
            payload: new BuyParams(opts.BuyItemTemplateId, opts.BuyCount),
            hardPreconditions:
            [
                new BotProposalPrecondition("merchant-configured", _ => opts.MerchantNpcObjId != 0),
                new BotProposalPrecondition("item-configured", _ => opts.BuyItemTemplateId != 0),
                new BotProposalPrecondition("count-positive", _ => opts.BuyCount > 0),
                new BotProposalPrecondition("price-configured", _ => opts.BuyUnitPrice > 0),
                new BotProposalPrecondition("funds-sufficient",
                    observed => observed.Money >= opts.BuyUnitPrice * opts.BuyCount)
            ]);

    private static BotDecisionProposal SellProposal(GameplayActor actor, NeedsOptions opts, long moneyBefore)
    {
        // The item INSTANCE resolves at perception time from the live bag
        // (the Sell engine path addresses Item.Id, not the template): the
        // first sellable stack of the surplus template. Template Sellable +
        // grade-template presence mirror the engine's own fail-closed gates
        // so an unsellable or unpriced surplus is refused BEFORE preference
        // and the decision falls through to rest. The engine revalidates
        // ownership, sellability, and the refund at dispatch.
        var sellItemId = ResolveSellableItem(actor, opts.SellSurplusItemTemplateId);
        return new(
            goal: "needs.sell",
            action: ActorActionType.Sell,
            targetId: opts.SellMerchantNpcObjId,
            expectedPostcondition: new BotProposalPostcondition(
                $"sell pays copper above the {moneyBefore} before the step",
                observed => observed.Money > moneyBefore),
            idempotencyKey: $"needs:{actor.ActorId}:{opts.CycleId}:sell",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "sell the surplus off the gold need",
            policyVersion: opts.PolicyVersion,
            priority: opts.SellPriority,
            tieBreakKey: $"sell:{opts.SellSurplusItemTemplateId}",
            payload: sellItemId != 0 ? new SellParams(sellItemId) : null,
            hardPreconditions:
            [
                new BotProposalPrecondition("sell-merchant-configured", _ => opts.SellMerchantNpcObjId != 0),
                new BotProposalPrecondition("surplus-configured", _ => opts.SellSurplusItemTemplateId != 0),
                // The seed itself is never the surplus: selling the seed the
                // loop needs to plant would un-plant the farm goal.
                new BotProposalPrecondition("surplus-not-seed",
                    _ => opts.SellSurplusItemTemplateId != opts.PlantSeedItemTemplateId
                        && opts.SellSurplusItemTemplateId != opts.BuyItemTemplateId),
                new BotProposalPrecondition("sellable-surplus-present", _ => sellItemId != 0)
            ]);
    }
    /// <summary>
    /// Perception-time sellable lookup: the first bag stack of the surplus
    /// template whose template is Sellable with a known grade refund (the
    /// same gates the <see cref="IGameplayActor.Sell"/> engine path
    /// pre-flights). Returns the item instance id, 0 when no sellable stack
    /// is held. The engine revalidates ownership and pricing at dispatch.
    /// </summary>
    private static ulong ResolveSellableItem(GameplayActor actor, uint surplusTemplateId)
    {
        if (surplusTemplateId == 0)
            return 0;
        try
        {
            var bag = actor.Character.Inventory?.Bag;
            if (bag == null)
                return 0;
            foreach (var item in bag.GetItemsSnapshot())
            {
                if (item == null || item.TemplateId != surplusTemplateId)
                    continue;
                if (item.Template == null || !item.Template.Sellable)
                    return 0;
                if (ItemManager.Instance.GetGradeTemplate(item.Grade) == null)
                    return 0;
                return item.Id;
            }
        }
        catch
        {
            return 0;
        }
        return 0;
    }
    private static BotDecisionProposal HarvestProposal(GameplayActor actor, NeedsOptions opts, int foodBefore)
        => new(
            goal: "needs.harvest",
            action: ActorActionType.Harvest,
            targetId: opts.HarvestDoodadObjId,
            expectedPostcondition: opts.FoodItemTemplateId == 0
                ? new BotProposalPostcondition("harvest dispatched (no food proxy configured)",
                    _ => true)
                : new BotProposalPostcondition(
                    $"bag holds more of food item {opts.FoodItemTemplateId} than the {foodBefore} before harvest",
                    observed => BagCountOf(observed, opts.FoodItemTemplateId) > foodBefore),
            idempotencyKey: $"needs:{actor.ActorId}:{opts.CycleId}:harvest",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "harvest the crop off the food/gold need",
            policyVersion: opts.PolicyVersion,
            priority: opts.HarvestPriority,
            tieBreakKey: $"harvest:{opts.HarvestDoodadObjId}",
            payload: null,
            hardPreconditions:
            [
                new BotProposalPrecondition("harvest-target-configured", _ => opts.HarvestDoodadObjId != 0)
            ]);

    private static BotDecisionProposal RestProposal(GameplayActor actor, NeedsOptions opts, long moneyBefore)
        => new(
            goal: "needs.rest",
            action: ActorActionType.Stop,
            targetId: 0,
            expectedPostcondition: new BotProposalPostcondition(
                "rest moves no copper",
                observed => observed.Money == moneyBefore),
            idempotencyKey: $"needs:{actor.ActorId}:{opts.CycleId}:rest",
            timeout: TimeSpan.FromSeconds(30),
            rationale: "needs met — idle",
            policyVersion: opts.PolicyVersion,
            priority: 0,
            tieBreakKey: "rest",
            payload: null);

    private static ActorRequest Dispatch(IGameplayActor gameplayActor, BotDecisionProposal proposal)
    {
        // Addressing rides the proposal itself (TargetId mirrors the work
        // target for the audit trail; craft/buy extras ride the payload, the
        // EconomyDecisionScenario pattern) — the switch below can only name
        // an existing actor action.
        return proposal.Action switch
        {
            ActorActionType.Craft when proposal.Payload is CraftParams craft => gameplayActor.Craft(
                proposal.TargetId, craft.DoodadObjId, null, proposal.IdempotencyKey),
            ActorActionType.Plant when proposal.Payload is PlantParams plant => gameplayActor.Plant(
                proposal.TargetId, plant.Position, plant.ZRot, plant.Scale, proposal.IdempotencyKey),
            ActorActionType.Buy when proposal.Payload is BuyParams buy => gameplayActor.Buy(
                proposal.TargetId, buy.ItemTemplateId, buy.Count, proposal.IdempotencyKey),
            ActorActionType.Sell when proposal.Payload is SellParams sell => gameplayActor.Sell(
                proposal.TargetId, sell.ItemId, proposal.IdempotencyKey),
            ActorActionType.Harvest => gameplayActor.Harvest(
                proposal.TargetId, proposal.IdempotencyKey),
            ActorActionType.Stop => gameplayActor.Stop(),
            _ => throw new InvalidOperationException($"needs decision cannot dispatch {proposal.Action}")
        };
    }

    private static NeedsRunResult Fail(
        string stage, ActorFailureReason reason, string detail,
        GameplayActor actor,
        BotNeeds needs,
        ActorActionType? selected,
        IReadOnlyList<BotProposalRejection> rejections,
        ActorRequest? request = null)
        => new()
        {
            Scenario = ScenarioName,
            WorkSelected = false,
            DominantNeed = needs.DominantNeed,
            Urgency = needs.Urgency,
            Needs = needs,
            SelectedAction = selected,
            Request = request,
            Rejections = rejections,
            Explanation = detail,
            ExpectedPostconditionSatisfied = false,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            TraceRecords = [.. actor.AuditTrace]
        };
}
