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
/// Phase 1 Brain Inspector: the read-only diagnostic projection
/// (<see cref="BotBrainProjection"/>) renders production-owned state
/// verbatim — identity, wake/decision sequences, lifecycle, position,
/// funnel tallies, live request, block reasons, bounded history — without
/// deciding, owning state, or inferring behavior.
///
/// These tests pin the projection's OBSERVABLE contract against recorded
/// evidence shapes (the G3 quest-observe payload: per-bot wake counter
/// growth, DiscoverQuests tallies, the DECIDE <c>lastDetail</c> bracket
/// block) — not a live lane. What is deliberately NOT tested: that the
/// projection forwards fields (implementation). What IS tested: the values
/// a dashboard consumer reads for each production state.
/// </summary>
public class BotBrainProjectionTests
{
    /// <summary>Minimal actor: empty observation, idle, empty trace.</summary>
    private sealed class BrainFixtureActor : IGameplayActor
    {
        public uint ActorId => 7;
        public Character Character => null!;
        public ActorRequest? ActiveRequest => null;
        public IReadOnlyList<ActorAuditRecord> AuditTrace => [];
        public void SetPendingDecision(string? goal, string? policy, int candidates, int rejections, string? seed) { }
        public void SetPendingCycleId(string? cycleId) { }
        public void SetPendingWakeSequence(long? wakeSequence) { }
        public bool PreemptCurrent(string reason) => false;
        public ActorObservation Observe() => new() { ActorId = ActorId };
        private static ActorRequest Unsupported() => throw new NotSupportedException();
        public ActorRequest SetTarget(uint targetObjId) => Unsupported();
        public ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest NavigateTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest NavigateToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Stop() => Unsupported();
        public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest CastAt(uint skillId, Vector3 position, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoAttack(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest StopAutoAttack(string? idempotencyKey = null) => Unsupported();
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
        public ActorRequest DismissMate(uint tlId = 0, string? idempotencyKey = null) => Unsupported();
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
        public ActorRequest InteractNpc(uint npcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverSelfQuests(string? idempotencyKey = null) => Unsupported();
        public ActorRequest PlayCinema(uint cinemaId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest SellSpecialty(uint merchantNpcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Repair(uint blacksmithNpcObjId, ulong itemId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null) => Unsupported();
        public ActorAuditRecord? FindByKey(string idempotencyKey) => null;
        public void Tick(TimeSpan elapsed) { }
    }

    private static BotBehaviorRuntime BlockedRuntime(string cycleId = "quest-10-777")
    {
        var runtime = new BotBehaviorRuntime(() => DateTimeOffset.UtcNow);
        var landed = runtime.Tick(new BrainFixtureActor(), null,
            new QuestDecisionScenario.QuestOptions { CycleId = cycleId }, "quest.progress");
        if (landed || runtime.Status != BotBehaviorStatus.Blocked)
            throw new InvalidOperationException($"fixture did not land Blocked (status={runtime.Status})");
        return runtime;
    }

    private static ActorAuditRecord DiscoverRow(long wakeSeq, string cycleId,
        ActorLifecycleState result = ActorLifecycleState.Completed, string? detail = null)
        => new(Guid.NewGuid(), 7, ActorActionType.DiscoverQuests, 2425u,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, result, null, detail, [],
            DecisionCycleId: cycleId, WakeSequence: wakeSeq);

    private static BotBrainProjection.BotBrainSnapshot CaptureBlocked(
        BotBehaviorRuntime runtime,
        IReadOnlyList<ActorAuditRecord>? trace = null,
        ActorRequest? live = null,
        long? wakeSequence = 92)
        => BotBrainProjection.Capture(10, "BrainBot",
            14536.6f, 11117.1f, 110.8f, 42u, 1u, 0u, 0u, null,
            wakeSequence, "quest.progress", runtime, live, trace ?? [],
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: "no walkable quest target (nothing ready-unspawned, nothing in-band to discover)");

    /// <summary>
    /// A sweep-empty wake projects the full inspector contract: identity,
    /// wake/decision sequences, activity/behavior/phase, position, typed
    /// funnel tallies with the first-zero reason, and a status line that
    /// names the block (never a bare "Blocked").
    /// </summary>
    [Test]
    public async Task Capture_BlockedWake_ProjectsInspectorContract()
    {
        var runtime = BlockedRuntime();
        var snap = CaptureBlocked(runtime);

        await Assert.That(snap.CharacterId).IsEqualTo(10u);
        await Assert.That(snap.Name).IsEqualTo("BrainBot");
        await Assert.That(snap.WakeSequence).IsEqualTo(92L);
        await Assert.That(snap.DecisionCycleId).IsEqualTo("quest-10-777");
        await Assert.That(snap.BehaviorInstanceId).IsEqualTo("quest-10-777");
        await Assert.That(snap.Activity).IsEqualTo("quest.progress");
        await Assert.That(snap.Behavior).IsEqualTo("quest");
        await Assert.That(snap.Phase).IsEqualTo("no-legal-work");
        await Assert.That(snap.Status).IsEqualTo(nameof(BotBehaviorStatus.Blocked));
        await Assert.That(snap.X).IsEqualTo(14536.6f);
        await Assert.That(snap.ZoneId).IsEqualTo(42u);
        await Assert.That(snap.WorldId).IsEqualTo(1u);
        await Assert.That(snap.Funnel).IsNotNull();
        await Assert.That(snap.Funnel!.SweptTargets).IsEqualTo(0);
        await Assert.That(snap.Funnel.Offerings).IsEqualTo(0);
        await Assert.That(snap.Funnel.InBand).IsEqualTo(0);
        await Assert.That(snap.Funnel.Legal).IsEqualTo(0);
        await Assert.That(snap.Funnel.FirstZero).IsEqualTo("sweep-empty");
        await Assert.That(snap.SelectedAction).IsNull();
        await Assert.That(snap.WorkSelected).IsFalse();
        await Assert.That(snap.LastFailure).IsNull();
        await Assert.That(snap.StatusLine.Contains("sweep-empty")).IsTrue();
        await Assert.That(snap.StatusLine.Contains("no legal quest proposal")).IsTrue();
        await Assert.That(snap.LiveAction).IsNull();
        await Assert.That(snap.LiveTraceId).IsNull();
    }

    /// <summary>
    /// The funnel bracket parses for every first-zero shape the quest leg
    /// records; text without a bracket (other fail stages) parses to null
    /// rather than a defaulted zero row.
    /// </summary>
    [Test]
    public async Task Funnel_ParsesAllFirstZeroShapes()
    {
        var noOfferings = BotBrainProjection.BotQuestFunnel.TryParseFailDetail(
            "no legal quest proposal: empty [swept=3 targets=111,222 offerings=0 inBand=0 legal=0 firstZero=no-offerings]");
        await Assert.That(noOfferings).IsNotNull();
        await Assert.That(noOfferings!.SweptTargets).IsEqualTo(3);
        await Assert.That(noOfferings.SweptTargetIds).IsEqualTo("111,222");
        await Assert.That(noOfferings.FirstZero).IsEqualTo("no-offerings");

        var filtered = BotBrainProjection.BotQuestFunnel.TryParseFailDetail(
            "no legal quest proposal: empty [swept=2 targets=9 offerings=4 inBand=1 legal=0 firstZero=all-filtered]");
        await Assert.That(filtered).IsNotNull();
        await Assert.That(filtered!.Offerings).IsEqualTo(4);
        await Assert.That(filtered.InBand).IsEqualTo(1);
        await Assert.That(filtered.FirstZero).IsEqualTo("all-filtered");

        await Assert.That(BotBrainProjection.BotQuestFunnel.TryParseFailDetail("EXECUTE Starvation left the terminal surface")).IsNull();
        await Assert.That(BotBrainProjection.BotQuestFunnel.TryParseFailDetail(null)).IsNull();
        await Assert.That(BotBrainProjection.BotQuestFunnel.TryParseFailDetail("")).IsNull();
    }
    /// <summary>
    /// A rejection-bearing DECIDE keeps the funnel: the nested [cands=N reject=...]
    /// block rides the outcomes= token sanitized (parens, not brackets) so the
    /// outer DECIDE bracket is still the last bracket pair the parser scans.
    /// </summary>
    [Test]
    public async Task Funnel_ParsesRejectionBearingDecide()
    {
        var decide =
            "no legal quest proposal: empty [swept=1 targets=2425 rawIds=2425 offerings=1 inBand=0 legal=0 firstZero=all-filtered" +
            " raw=1 trunc=0 actor=(1.0,2.0,3.0) zone=42 region=1" +
            " outcomes=2425:Completed:offers=330L9_discovered_1_quest(s)_at_Npc_3492_(cands=2_reject=251:REQ_FAIL_START_383)_" +
            " q251tgt_raw=1 q251tgt_rel=1 q251tgt_legal=1 q251tgt_selected=- q251tgt_objective=ok q251tgt_source=ok q251tgt_relevance=true]";
        var funnel = BotBrainProjection.BotQuestFunnel.TryParseFailDetail(decide);
        await Assert.That(funnel).IsNotNull();
        await Assert.That(funnel!.SweptTargets).IsEqualTo(1);
        await Assert.That(funnel.Offerings).IsEqualTo(1);
        await Assert.That(funnel.Legal).IsEqualTo(0);
        await Assert.That(funnel.FirstZero).IsEqualTo("all-filtered");
    }
    /// <summary>
    /// The pre-fix production shape (raw nested brackets) loses the funnel:
    /// LastIndexOf('[') lands on the inner [cands= open, so swept/firstZero
    /// never parse — pinned so a future re-introduction of raw brackets fails.
    /// </summary>
    [Test]
    public async Task Funnel_RawNestedBrackets_LoseTheFunnel()
    {
        var rawNested =
            "no legal quest proposal: empty [swept=1 targets=2425 rawIds=2425 offerings=1 inBand=0 legal=0 firstZero=all-filtered" +
            " raw=1 trunc=0 actor=(1.0,2.0,3.0) zone=42 region=1" +
            " outcomes=2425:Completed:offers=330L9_discovered_1_quest(s)_at_Npc_3492_[cands=2_reject=251:REQ_FAIL_START_383]_" +
            " q251tgt_raw=1 q251tgt_rel=1 q251tgt_legal=1 q251tgt_selected=- q251tgt_objective=ok q251tgt_source=ok q251tgt_relevance=true]";
        await Assert.That(BotBrainProjection.BotQuestFunnel.TryParseFailDetail(rawNested)).IsNull();
    }


    /// <summary>
    /// History is bounded (newest 20 of 25, execution order) and each row
    /// carries the recorded join keys plus the recorded outcome — the shape
    /// the G3 observe payload reports as discover tallies + lastDetail.
    /// </summary>
    [Test]
    public async Task Capture_History_IsBoundedWithJoinKeys()
    {
        var runtime = BlockedRuntime();
        var rows = Enumerable.Range(68, 25)
            .Select(n => DiscoverRow(n, $"quest-10-{n}",
                n == 92 ? ActorLifecycleState.Rejected : ActorLifecycleState.Completed,
                n == 92 ? "busy at entry" : null))
            .ToList();
        var snap = CaptureBlocked(runtime, rows);

        await Assert.That(snap.History.Count).IsEqualTo(20);
        await Assert.That(snap.History[0].WakeSequence).IsEqualTo(73L);
        await Assert.That(snap.History[^1].WakeSequence).IsEqualTo(92L);
        await Assert.That(snap.History[^1].DecisionCycleId).IsEqualTo("quest-10-92");
        await Assert.That(snap.History[^1].Action).IsEqualTo(nameof(ActorActionType.DiscoverQuests));
        await Assert.That(snap.History[^1].Result).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(snap.History[^1].Reason).IsEqualTo("busy at entry");
        await Assert.That(snap.History[0].Reason).IsNull();
    }

    /// <summary>
    /// A bot the behavior never ticked projects NotStarted/idle with null
    /// decision identity — activity falls back to the arbiter's electing
    /// activity, and the status line says what is known (never bare).
    /// </summary>
    [Test]
    public async Task Capture_NeverTicked_ProjectsIdleWithArbiterActivity()
    {
        var snap = BotBrainProjection.Capture(11, "IdleBot",
            0f, 0f, 0f, 0u, 0u, 0u, 0u, null,
            null, "needs.rest", null, null, [],
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: "");

        await Assert.That(snap.Status).IsEqualTo(nameof(BotBehaviorStatus.NotStarted));
        await Assert.That(snap.Phase).IsEqualTo("idle");
        await Assert.That(snap.DecisionCycleId).IsNull();
        await Assert.That(snap.WakeSequence).IsNull();
        await Assert.That(snap.Activity).IsEqualTo("needs.rest");
        await Assert.That(snap.Funnel).IsNull();
        await Assert.That(snap.StatusLine.Contains("never ticked")).IsTrue();
        await Assert.That(snap.NextWakeUtc).IsNull();
    }

    /// <summary>
    /// A live (non-terminal) request projects its trace/action/state; once
    /// terminal the live columns clear back to idle — the running/idle
    /// discriminator the dashboard polls.
    /// </summary>
    [Test]
    public async Task Capture_LiveRequest_ProjectsTraceActionState()
    {
        var runtime = BlockedRuntime();
        var live = new ActorRequest(ActorActionType.AcceptQuest, 251u, null, 0, null);
        live.Accept("test accept");
        live.Start("test start");
        var running = CaptureBlocked(runtime, [], live);

        await Assert.That(running.LiveAction).IsEqualTo(nameof(ActorActionType.AcceptQuest));
        await Assert.That(running.LiveState).IsEqualTo(nameof(ActorLifecycleState.Running));
        await Assert.That(running.LiveTraceId).IsEqualTo(live.TraceId);
        await Assert.That(running.StatusLine.Contains("running")).IsTrue();

        live.Complete();
        var idle = CaptureBlocked(runtime, [], live);
        await Assert.That(idle.LiveAction).IsNull();
        await Assert.That(idle.LiveTraceId).IsNull();
    }

    /// <summary>
    /// Fleet buckets are exclusive by precedence (blocked › questing ›
    /// farming › combat › roaming › idle) and sum to the registered total.
    /// </summary>
    [Test]
    public async Task CaptureFleet_BucketsByPrecedence()
    {
        var inputs = new[]
        {
            new BotBrainProjection.BotFleetInput(true, true, false, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, false, false, true),
            new BotBrainProjection.BotFleetInput(true, true, false, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, false, false, false),
            new BotBrainProjection.BotFleetInput(true, false, true, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, false, false, false),
            new BotBrainProjection.BotFleetInput(true, false, false, BotRoamStepExecutor.NeedsFarmLoopPhase.WaitingMaturity, false, false, false),
            new BotBrainProjection.BotFleetInput(true, false, false, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, true, false, false),
            new BotBrainProjection.BotFleetInput(true, false, false, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, false, true, false),
            new BotBrainProjection.BotFleetInput(true, false, false, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, false, false, false),
            new BotBrainProjection.BotFleetInput(false, false, false, BotRoamStepExecutor.NeedsFarmLoopPhase.Idle, false, false, false),
        };
        var fleet = BotBrainProjection.CaptureFleet(inputs, null);

        await Assert.That(fleet.Active).IsEqualTo(7);
        await Assert.That(fleet.Dormant).IsEqualTo(1);
        await Assert.That(fleet.Blocked).IsEqualTo(1);
        await Assert.That(fleet.Questing).IsEqualTo(1);
        await Assert.That(fleet.Farming).IsEqualTo(2);
        await Assert.That(fleet.Combat).IsEqualTo(1);
        await Assert.That(fleet.Roaming).IsEqualTo(1);
        await Assert.That(fleet.Idle).IsEqualTo(1);
        await Assert.That(fleet.Scheduler).IsNull();
    }
    /// <summary>
    /// The inspector snapshot renders to a dashboard-consumable JSON shape:
    /// identity, sequences, lifecycle, funnel, status line, and the bounded
    /// history all survive serialization with values intact.
    /// </summary>
    [Test]
    public async Task Snapshot_SerializesToDashboardShape()
    {
        var runtime = BlockedRuntime();
        var rows = Enumerable.Range(68, 25).Select(n => DiscoverRow(n, $"quest-10-{n}")).ToList();
        var snap = CaptureBlocked(runtime, rows);
        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(snap));
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("CharacterId").GetUInt32()).IsEqualTo(10u);
        await Assert.That(root.GetProperty("WakeSequence").GetInt64()).IsEqualTo(92L);
        await Assert.That(root.GetProperty("DecisionCycleId").GetString()).IsEqualTo("quest-10-777");
        await Assert.That(root.GetProperty("Phase").GetString()).IsEqualTo("no-legal-work");
        await Assert.That(root.GetProperty("Funnel").GetProperty("FirstZero").GetString()).IsEqualTo("sweep-empty");
        await Assert.That(root.GetProperty("StatusLine").GetString()!.Contains("sweep-empty")).IsTrue();
        await Assert.That(root.GetProperty("History").GetArrayLength()).IsEqualTo(20);
    }
}
