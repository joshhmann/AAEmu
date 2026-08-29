using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.StaticValues;
namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 decision contract tests: immutable perception, hard legality before
/// preference, bounded personality weighting, deterministic tie-breaks, and
/// explainable terminal-cycle metadata.
/// </summary>
public class BotDecisionProposalTests
{
    private sealed class RecordingActor : IGameplayActor
    {
        private uint _target;

        public uint ActorId => 42;
        public Character Character => null!;
        public ActorRequest? ActiveRequest => null;
        public IReadOnlyList<ActorAuditRecord> AuditTrace => [];

        public ActorObservation Observe() => new()
        {
            ActorId = ActorId,
            CurrentTargetObjId = _target
        };

        public ActorRequest SetTarget(uint targetObjId)
        {
            _target = targetObjId;
            var request = new ActorRequest(ActorActionType.Target, targetObjId, null, 0, null);
            request.Accept("recording target");
            request.Start("recording target");
            request.Complete();
            return request;
        }

        private static ActorRequest Unsupported() => throw new NotSupportedException();
        public ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest NavigateTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Stop() => Unsupported();
        public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest CastAt(uint skillId, Vector3 position, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Loot(uint lootOwnerObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Equip(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyInvite(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyAccept(string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionCreate(string name, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionInvite(string invitedName, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionAccept(FactionsEnum expeditionId, uint inviterId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionLeave(string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeOffer(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradePutup(uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeLockOk(string? idempotencyKey = null) => Unsupported();
        public ActorRequest Mount(uint mateObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null) => Unsupported();
        public ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Plant(uint seedItemTemplateId, Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public bool Interrupt(Guid traceId) => false;
        public ActorRequest AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverQuests(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest InteractWith(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Talk(uint npcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverSelfQuests(string? idempotencyKey = null) => Unsupported();
        public ActorRequest PlayCinema(uint cinemaId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest SellSpecialty(uint merchantNpcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null) => Unsupported();
        public ActorAuditRecord? FindByKey(string idempotencyKey) => null;
        public void Tick(TimeSpan elapsed) { }
    }
    private static BotObservedContext Context(IReadOnlyList<uint>? activeQuests = null) => new()
    {
        ActorId = 7,
        Position = Vector3.Zero,
        MaxHp = 100,
        Hp = 100,
        ActiveQuestIds = activeQuests ?? []
    };

    private static BotDecisionProposal Proposal(
        string key,
        int priority,
        int personalityWeight = 0,
        string tieBreak = "",
        IEnumerable<BotProposalPrecondition>? preconditions = null) => new(
            goal: "test.goal",
            action: ActorActionType.Observe,
            targetId: 0,
            expectedPostcondition: new BotProposalPostcondition("observation remains available", _ => true),
            idempotencyKey: key,
            timeout: TimeSpan.FromSeconds(1),
            rationale: $"proposal {key}",
            policyVersion: "test-v1",
            priority: priority,
            personalityWeight: personalityWeight,
            tieBreakKey: tieBreak,
            hardPreconditions: preconditions);

    [Test]
    public async Task Context_FromObservation_CopiesMutableLists()
    {
        var nearbyNpcs = new List<uint> { 11 };
        var source = new ActorObservation { ActorId = 7, NearbyNpcObjIds = nearbyNpcs };

        var context = BotObservedContext.From(source);
        nearbyNpcs.Add(12);

        await Assert.That(context.NearbyNpcObjIds).IsEquivalentTo(new[] { 11u });
    }

    [Test]
    public async Task Selector_FiltersHardPreconditionsBeforePriority()
    {
        var illegal = Proposal("illegal", priority: 100,
            preconditions: [new BotProposalPrecondition("quest-free", _ => false)]);
        var legal = Proposal("legal", priority: 1);

        var result = BotDecisionSelector.Select(Context(), [illegal, legal]);

        await Assert.That(result.Proposal).IsSameReferenceAs(legal);
        await Assert.That(result.Rejections.Count).IsEqualTo(1);
        await Assert.That(result.Rejections[0].Reason).Contains("quest-free");
        await Assert.That(result.Explanation).Contains("test.goal/Observe");
    }

    [Test]
    public async Task Selector_PriorityWinsAndTieBreakIsStable()
    {
        var low = Proposal("low", priority: 9, personalityWeight: 100);
        var second = Proposal("second", priority: 10, tieBreak: "b");
        var first = Proposal("first", priority: 10, tieBreak: "a");

        var result = BotDecisionSelector.Select(Context(), [low, second, first]);

        await Assert.That(result.Proposal).IsSameReferenceAs(first);
        await Assert.That(low.PersonalityWeight).IsEqualTo(BotDecisionProposal.MaxPersonalityWeight);
    }
    [Test]
    public async Task Selector_RejectsUnboundedCandidateSet()
    {
        var proposals = Enumerable.Range(0, BotDecisionSelector.MaxCandidates + 1)
            .Select(index => Proposal(index.ToString(), index))
            .ToArray();

        var result = BotDecisionSelector.Select(Context(), proposals);

        await Assert.That(result.HasProposal).IsFalse();
        await Assert.That(result.Explanation).Contains("candidate bound exceeded");
    }
    [Test]
    public async Task DecisionCycle_DispatchesActorAction_AndHonorsTerminalPostcondition()
    {
        var actor = new RecordingActor();
        const uint targetObjId = 99;
        var observation = BotObservedContext.Capture(actor);
        var proposal = new BotDecisionProposal(
            goal: "target-nearby-npc",
            action: ActorActionType.Target,
            targetId: targetObjId,
            expectedPostcondition: new BotProposalPostcondition(
                $"current target is {targetObjId}",
                terminal => terminal.CurrentTargetObjId == targetObjId),
            idempotencyKey: "decision-cycle-target-1",
            timeout: TimeSpan.FromSeconds(1),
            rationale: "target the perceived nearby NPC",
            policyVersion: "test-v1");

        var execution = BotDecisionCycle.Execute(
            actor, observation, proposal,
            static (gameplayActor, selected) => gameplayActor.SetTarget(selected.TargetId));

        await Assert.That(execution.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(execution.TerminalObservation).IsNotNull();

        await Assert.That(execution.TerminalObservation!.CurrentTargetObjId).IsEqualTo(targetObjId);
        await Assert.That(execution.ExpectedPostconditionSatisfied).IsTrue();
    }

}
