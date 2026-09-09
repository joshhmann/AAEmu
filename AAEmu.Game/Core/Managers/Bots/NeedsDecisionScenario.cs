using AAEmu.Game.Models.Game.Bots;

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
///     Stop — no new gameplay path, no direct DB / Transform / ZoneId / GM /
///     reflection shortcuts;
///   - urgency at or below the rest line offers Rest alone; otherwise Work
///     candidates (Craft, then Buy, then Harvest) are offered with the
///     always-legal Rest fallback, so an empty or impossible work set idles
///     instead of throwing.
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
        public int BuyPriority { get; init; } = 20;
        public int HarvestPriority { get; init; } = 10;
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
            var proposals = new List<BotDecisionProposal>
            {
                RestProposal(actor, opts, moneyBefore)
            };
            if (needs.Urgency > opts.Thresholds.RestUrgency)
            {
                proposals.Add(CraftProposal(actor, opts, materialBefore));
                proposals.Add(BuyProposal(actor, opts, buyItemBefore));
                proposals.Add(HarvestProposal(actor, opts, foodBefore));
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
            ActorActionType.Buy when proposal.Payload is BuyParams buy => gameplayActor.Buy(
                proposal.TargetId, buy.ItemTemplateId, buy.Count, proposal.IdempotencyKey),
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
