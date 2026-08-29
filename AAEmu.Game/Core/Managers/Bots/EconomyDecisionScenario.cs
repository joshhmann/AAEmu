using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M5 policy-extension consumer #1 — ECONOMY: proposal-driven choice among
/// legal economy actions (buy / deposit-money / withdraw-money /
/// deposit-item / withdraw-item) through the EXISTING <see cref="IGameplayActor"/>
/// actions and ordinary Character services.
///
/// Decision discipline (the M5 contract):
///   - perception rides <see cref="BotObservedContext.Capture"/> (Observe —
///     direct server-state query, no packets);
///   - hard legality is evaluated BEFORE preference, and every hard
///     precondition reads ONLY the immutable observation context
///     (Money/BankMoney/BagItemCounts/BankItemCounts). Merchant resolution
///     and shop-range are ordinary service reads performed at perception
///     time (the same gates the Buy engine path pre-flights); the buy
///     proposal is only offered when the merchant is valid;
///   - selection is deterministic (fixed priority, then tie-break key);
///   - dispatch calls the existing actor methods only — no new gameplay
///     path, no direct DB / Transform / ZoneId / GM / reflection shortcuts;
///   - the terminal postcondition is evaluated against the terminal
///     observation capture.
///
/// SellSpecialty is deliberately NOT offered here: it needs the heavy
/// Specialty/Zone/Mail/Name/Character singleton surface (the
/// SeedHaulerSurfaces rig), which is out of scope for this bounded consumer.
/// </summary>
public static class EconomyDecisionScenario
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "m5-economy-decision";

    /// <summary>Scenario parameters. Defaults = the rig trade surface ids.</summary>
    public sealed record EconomyOptions
    {
        /// <summary>Merchant NPC objId the buy/sell candidates target.</summary>
        public uint MerchantNpcObjId { get; init; }

        /// <summary>Item template the buy candidate purchases.</summary>
        public uint BuyItemTemplateId { get; init; }

        /// <summary>Quantity the buy candidate purchases.</summary>
        public int BuyCount { get; init; } = 1;

        /// <summary>Item template the deposit-item / withdraw-item candidates move.</summary>
        public uint ItemTemplateId { get; init; }

        /// <summary>Copper the deposit-money candidate moves into the bank.</summary>
        public long DepositAmount { get; init; }

        /// <summary>Copper the withdraw-money candidate moves into the inventory.</summary>
        public long WithdrawAmount { get; init; }

        /// <summary>Policy version stamped on every proposal.</summary>
        public string PolicyVersion { get; init; } = "economy-v1";

        // ---- fixed priorities (policy; personality stays 0) ----
        public int BuyPriority { get; init; } = 20;
        public int DepositMoneyPriority { get; init; } = 10;
        public int DepositItemPriority { get; init; } = 8;
        public int WithdrawMoneyPriority { get; init; } = 5;
        public int WithdrawItemPriority { get; init; } = 4;
    }

    /// <summary>Structured run result — decision-path evidence attached.</summary>
    public sealed class EconomyRunResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<string> Notes { get; init; } = [];
        /// <summary>The actor's full audit trace, in execution order.</summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        /// <summary>The action the deterministic selector chose (null = no legal proposal).</summary>
        public ActorActionType? SelectedAction { get; init; }
        /// <summary>Why each non-selected candidate was rejected (legality-before-preference evidence).</summary>
        public IReadOnlyList<BotProposalRejection> Rejections { get; init; } = [];
        public string SelectionExplanation { get; init; } = "";
        /// <summary>Whether the selected proposal's terminal postcondition held.</summary>
        public bool ExpectedPostconditionSatisfied { get; init; }

        public string Evidence()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Scenario: {Scenario}");
            sb.AppendLine($"Verdict: {(Passed ? "PASS" : "FAIL")}" +
                          (FailStage.Length > 0 ? $" at {FailStage}" : "") +
                          (Failure is { } f ? $" ({f})" : "") +
                          (FailReason.Length > 0 ? $" — {FailReason}" : ""));
            sb.AppendLine($"- selection: {SelectedAction?.ToString() ?? "none"} — {SelectionExplanation}");
            foreach (var r in Rejections)
                sb.AppendLine($"- rejection: {r.Proposal.Goal} — {r.Reason}");
            foreach (var note in Notes)
                sb.AppendLine($"- note: {note}");
            foreach (var s in Stages)
                sb.AppendLine($"- stage {s.Stage}: {s.Advance}, step={s.StepObserved}, status={s.StatusObserved}");
            foreach (var c in Criteria)
                sb.AppendLine($"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}");
            foreach (var t in TraceRecords)
                sb.AppendLine($"- trace: {t.Action}({t.TargetId})→{t.Result}{(t.Failure is { } fr ? $"/{fr}" : "")}");
            return sb.ToString();
        }
    }

    /// <summary>Runs one decision cycle on an embodied character.</summary>
    public static EconomyRunResult Run(Character character, EconomyOptions? options = null)
    {
        var opts = options ?? new EconomyOptions();
        var actor = new GameplayActor(character);
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var notes = new List<string>();

        try
        {
            // ---------------------------------------------------- 1. PERCEIVE
            var context = BotObservedContext.Capture(actor);

            // Merchant validity is an ordinary service read at perception time
            // (the same gates the Buy engine path pre-flights: live merchant
            // NPC, goods pack sells the item, shop range). The buy proposal is
            // only offered when the merchant is valid.
            var merchantValid = TryResolveMerchant(actor, opts.MerchantNpcObjId, opts.BuyItemTemplateId, out var buyPrice);

            // ---------------------------------------------------- 2. DECIDE
            var proposals = new List<BotDecisionProposal>();
            if (merchantValid)
            {
                proposals.Add(new BotDecisionProposal(
                    goal: "economy.buy",
                    action: ActorActionType.Buy,
                    targetId: opts.MerchantNpcObjId,
                    expectedPostcondition: new BotProposalPostcondition(
                        $"bag holds at least {opts.BuyCount} of item {opts.BuyItemTemplateId}",
                        observed => observed.BagItemCounts.TryGetValue(opts.BuyItemTemplateId, out var c) && c >= opts.BuyCount),
                    idempotencyKey: $"economy:{character.Id}:1:buy",
                    timeout: TimeSpan.FromSeconds(30),
                    rationale: "buy goods from the merchant",
                    policyVersion: opts.PolicyVersion,
                    priority: opts.BuyPriority,
                    tieBreakKey: $"buy:{opts.BuyItemTemplateId}",
                    payload: new BuyParams(opts.BuyItemTemplateId, opts.BuyCount),
                    hardPreconditions:
                    [
                        new BotProposalPrecondition("not-enough-money",
                            observed => observed.Money >= (long)buyPrice * opts.BuyCount)
                    ]));
            }

            proposals.Add(new BotDecisionProposal(
                goal: "economy.deposit-money",
                action: ActorActionType.DepositMoney,
                targetId: 0,
                expectedPostcondition: new BotProposalPostcondition(
                    $"bank holds at least {opts.DepositAmount} copper",
                    observed => observed.BankMoney >= opts.DepositAmount),
                idempotencyKey: $"economy:{character.Id}:1:deposit-money",
                timeout: TimeSpan.FromSeconds(30),
                rationale: "deposit copper into the bank",
                policyVersion: opts.PolicyVersion,
                priority: opts.DepositMoneyPriority,
                tieBreakKey: "deposit-money",
                payload: opts.DepositAmount,
                hardPreconditions:
                [
                    new BotProposalPrecondition("amount-positive", observed => opts.DepositAmount > 0),
                    new BotProposalPrecondition("not-enough-money", observed => observed.Money >= opts.DepositAmount)
                ]));

            proposals.Add(new BotDecisionProposal(
                goal: "economy.withdraw-money",
                action: ActorActionType.WithdrawMoney,
                targetId: 0,
                expectedPostcondition: new BotProposalPostcondition(
                    $"inventory holds at least {opts.WithdrawAmount} copper",
                    observed => observed.Money >= opts.WithdrawAmount),
                idempotencyKey: $"economy:{character.Id}:1:withdraw-money",
                timeout: TimeSpan.FromSeconds(30),
                rationale: "withdraw copper from the bank",
                policyVersion: opts.PolicyVersion,
                priority: opts.WithdrawMoneyPriority,
                tieBreakKey: "withdraw-money",
                payload: opts.WithdrawAmount,
                hardPreconditions:
                [
                    new BotProposalPrecondition("amount-positive", observed => opts.WithdrawAmount > 0),
                    new BotProposalPrecondition("not-enough-bank", observed => observed.BankMoney >= opts.WithdrawAmount)
                ]));

            proposals.Add(new BotDecisionProposal(
                goal: "economy.deposit-item",
                action: ActorActionType.DepositItem,
                targetId: opts.ItemTemplateId,
                expectedPostcondition: new BotProposalPostcondition(
                    $"bank holds at least one of item {opts.ItemTemplateId}",
                    observed => observed.BankItemCounts.TryGetValue(opts.ItemTemplateId, out var c) && c > 0),
                idempotencyKey: $"economy:{character.Id}:1:deposit-item",
                timeout: TimeSpan.FromSeconds(30),
                rationale: "deposit an item stack into the bank",
                policyVersion: opts.PolicyVersion,
                priority: opts.DepositItemPriority,
                tieBreakKey: $"deposit-item:{opts.ItemTemplateId}",
                hardPreconditions:
                [
                    new BotProposalPrecondition("item-in-bag",
                        observed => observed.BagItemCounts.TryGetValue(opts.ItemTemplateId, out var c) && c > 0)
                ]));

            proposals.Add(new BotDecisionProposal(
                goal: "economy.withdraw-item",
                action: ActorActionType.WithdrawItem,
                targetId: opts.ItemTemplateId,
                expectedPostcondition: new BotProposalPostcondition(
                    $"bag holds at least one of item {opts.ItemTemplateId}",
                    observed => observed.BagItemCounts.TryGetValue(opts.ItemTemplateId, out var c) && c > 0),
                idempotencyKey: $"economy:{character.Id}:1:withdraw-item",
                timeout: TimeSpan.FromSeconds(30),
                rationale: "withdraw an item stack from the bank",
                policyVersion: opts.PolicyVersion,
                priority: opts.WithdrawItemPriority,
                tieBreakKey: $"withdraw-item:{opts.ItemTemplateId}",
                hardPreconditions:
                [
                    new BotProposalPrecondition("item-in-bank",
                        observed => observed.BankItemCounts.TryGetValue(opts.ItemTemplateId, out var c) && c > 0)
                ]));

            var decision = BotDecisionSelector.Select(context, proposals);
            if (!decision.HasProposal)
            {
                return Fail("DECIDE", ActorFailureReason.WrongDecision,
                    $"no legal economy proposal: {decision.Explanation}", actor, stages, criteria, notes, decision);
            }

            // ---------------------------------------------------- 3. EXECUTE
            var execution = BotDecisionCycle.Execute(actor, context, decision.Proposal!,
                static (gameplayActor, proposal) =>
                {
                    switch (proposal.Action)
                    {
                        case ActorActionType.Buy:
                            var buy = (BuyParams)proposal.Payload!;
                            return gameplayActor.Buy(proposal.TargetId, buy.ItemTemplateId, buy.Count, proposal.IdempotencyKey);
                        case ActorActionType.DepositMoney:
                            return gameplayActor.DepositMoney((long)proposal.Payload!, proposal.IdempotencyKey);
                        case ActorActionType.WithdrawMoney:
                            return gameplayActor.WithdrawMoney((long)proposal.Payload!, proposal.IdempotencyKey);
                        case ActorActionType.DepositItem:
                            return gameplayActor.DepositItem(proposal.TargetId, proposal.IdempotencyKey);
                        case ActorActionType.WithdrawItem:
                            return gameplayActor.WithdrawItem(proposal.TargetId, proposal.IdempotencyKey);
                        default:
                            throw new InvalidOperationException($"economy decision cannot dispatch {proposal.Action}");
                    }
                });
            var request = execution.Request;
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                request.Action.ToString(), 1, request.State.ToString(), request.TargetId.ToString(), request.Detail ?? ""));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("dispatch-completed",
                request.State == ActorLifecycleState.Completed,
                $"{request.Action}: {request.Detail ?? request.State.ToString()}"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("terminal-postcondition-satisfied",
                execution.ExpectedPostconditionSatisfied,
                execution.Proposal.ExpectedPostcondition.Description));
            if (request.State != ActorLifecycleState.Completed || !execution.ExpectedPostconditionSatisfied)
            {
                return Fail("EXECUTE", request.Failure ?? ActorFailureReason.RejectedAction,
                    $"{request.Action} {request.State}: {request.Detail ?? execution.Proposal.ExpectedPostcondition.Description}",
                    actor, stages, criteria, notes, decision, execution);
            }

            // ---------------------------------------------------- 4. VERIFY
            var traceComplete = actor.AuditTrace.Count > 0
                && actor.AuditTrace.All(r => r.Result == ActorLifecycleState.Completed
                    && r.StateChanges.Any(s => s.Contains("Requested"))
                    && r.StateChanges.Any(s => s.Contains("Accepted"))
                    && r.StateChanges.Any(s => s.Contains("Completed")));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", traceComplete,
                $"completed records {actor.AuditTrace.Count(r => r.Result == ActorLifecycleState.Completed)}/{actor.AuditTrace.Count}"));

            var passed = criteria.All(c => c.Passed);
            return new EconomyRunResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                Failure = passed ? null : ActorFailureReason.WrongDecision,
                FailReason = passed ? "" : string.Join("; ", criteria.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")),
                Stages = stages,
                Criteria = criteria,
                Notes = notes,
                TraceRecords = [.. actor.AuditTrace],
                SelectedAction = decision.Proposal!.Action,
                Rejections = decision.Rejections,
                SelectionExplanation = decision.Explanation,
                ExpectedPostconditionSatisfied = execution.ExpectedPostconditionSatisfied
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor, stages, criteria, notes, null);
        }
    }

    /// <summary>
    /// Perception-time merchant gate (ordinary service reads — the same gates
    /// the Buy engine path pre-flights): live merchant NPC with a goods pack,
    /// the pack sells the item, and the actor is within shop range.
    /// </summary>
    private static bool TryResolveMerchant(GameplayActor actor, uint merchantNpcObjId, uint itemTemplateId, out int price)
    {
        price = 0;
        var npc = actor.Character.ParentWorld?.GetNpc(merchantNpcObjId);
        if (npc == null || npc.Template == null || !npc.Template.Merchant || npc.Template.MerchantPackId == 0)
            return false;
        if (MathUtil.CalculateDistance(actor.Character.Transform.World.Position, npc.Transform.World.Position) > GameplayActor.MaxShopRange)
            return false;
        var pack = NpcManager.Instance.GetGoods(npc.Template.MerchantPackId);
        if (pack == null || !pack.SellsItem(itemTemplateId))
            return false;
        var template = ItemManager.Instance.GetTemplate(itemTemplateId);
        if (template == null)
            return false;
        price = template.Price;
        return true;
    }

    private static EconomyRunResult Fail(
        string stage, ActorFailureReason reason, string detail,
        GameplayActor actor,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        List<string> notes,
        BotDecisionSelection? decision,
        BotDecisionExecution? execution = null)
        => new()
        {
            Scenario = ScenarioName,
            Passed = false,
            FailStage = stage,
            Failure = reason,
            FailReason = detail,
            Stages = stages,
            Criteria = criteria,
            Notes = notes,
            TraceRecords = [.. actor.AuditTrace],
            SelectedAction = decision?.Proposal?.Action,
            Rejections = decision?.Rejections ?? [],
            SelectionExplanation = decision?.Explanation ?? "",
            ExpectedPostconditionSatisfied = execution?.ExpectedPostconditionSatisfied ?? false
        };
}
