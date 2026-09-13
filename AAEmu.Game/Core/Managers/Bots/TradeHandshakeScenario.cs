using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// TRADE-01 live scenario: two ordinary Characters drive the real
/// TradeManager path — offer → put-up → lock+ok on both sides → item swap
/// executes inside ConfirmTrade. Conservation (grant − put-up + received)
/// is the proof.
/// </summary>
public static class TradeHandshakeScenario
{
    public const string ScenarioName = "trade-handshake";

    public sealed record HandshakeOptions(
        uint ItemTemplateId = 0,
        int ItemCount = 1,
        string CycleId = "trade-live");

    public static BotScenarioRunner.ScenarioRunResult Run(
        Character aliceCharacter, Character bobCharacter, HandshakeOptions? options = null)
    {
        options ??= new HandshakeOptions();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request)
            => new(name, 1, request.State.ToString(), request.TargetId.ToString(), request.Detail ?? "");

        BotScenarioRunner.ScenarioRunResult Fail(string stage, ActorFailureReason? failure, string reason)
            => new()
            {
                Template = ScenarioName,
                Passed = false,
                FailStage = stage,
                Failure = failure,
                FailReason = reason,
                RigNotes = [],
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                ActorRequests = traceRecords.Count
            };

        try
        {
            var alice = new GameplayActor(aliceCharacter);
            var bob = new GameplayActor(bobCharacter);
            var aliceBefore = aliceCharacter.Inventory.GetItemsCount(options.ItemTemplateId);
            var bobBefore = bobCharacter.Inventory.GetItemsCount(options.ItemTemplateId);

            var offer = alice.TradeOffer(bobCharacter.ObjId, $"{options.CycleId}-offer");
            traceRecords.Add(alice.AuditTrace.Last());
            stages.Add(Stage("OFFER", offer));
            if (offer.State != ActorLifecycleState.Completed)
                return Fail("OFFER", offer.Failure, $"offer {offer.State}: {offer.Detail ?? "no detail"}");

            var putup = alice.TradePutup(options.ItemTemplateId, options.ItemCount, $"{options.CycleId}-putup");
            traceRecords.Add(alice.AuditTrace.Last());
            stages.Add(Stage("PUTUP", putup));
            if (putup.State != ActorLifecycleState.Completed)
                return Fail("PUTUP", putup.Failure, $"putup {putup.State}: {putup.Detail ?? "no detail"}");

            var lockAlice = alice.TradeLockOk($"{options.CycleId}-lock-alice");
            traceRecords.Add(alice.AuditTrace.Last());
            stages.Add(Stage("LOCK-ALICE", lockAlice));
            if (lockAlice.State != ActorLifecycleState.Completed)
                return Fail("LOCK-ALICE", lockAlice.Failure, $"lock {lockAlice.State}: {lockAlice.Detail ?? "no detail"}");

            var lockBob = bob.TradeLockOk($"{options.CycleId}-lock-bob");
            traceRecords.Add(bob.AuditTrace.Last());
            stages.Add(Stage("LOCK-BOB", lockBob));
            if (lockBob.State != ActorLifecycleState.Completed)
                return Fail("LOCK-BOB", lockBob.Failure, $"lock {lockBob.State}: {lockBob.Detail ?? "no detail"}");

            var aliceAfter = aliceCharacter.Inventory.GetItemsCount(options.ItemTemplateId);
            var bobAfter = bobCharacter.Inventory.GetItemsCount(options.ItemTemplateId);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("alice-conserved",
                aliceAfter == aliceBefore - options.ItemCount,
                $"alice {aliceBefore} → {aliceAfter} (put up {options.ItemCount})"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("bob-received",
                bobAfter == bobBefore + options.ItemCount,
                $"bob {bobBefore} → {bobAfter} (received {options.ItemCount})"));

            var passed = criteria.All(c => c.Passed);
            return new BotScenarioRunner.ScenarioRunResult
            {
                Template = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                Failure = passed ? null : ActorFailureReason.WrongDecision,
                FailReason = passed ? "" : string.Join("; ", criteria.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")),
                RigNotes = [],
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                ActorRequests = traceRecords.Count
            };
        }
        catch (Exception ex)
        {
            return Fail("RUN", ActorFailureReason.FidelityError, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
