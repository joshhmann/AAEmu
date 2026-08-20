using System.Linq;
using System.Numerics;

using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Server-side implementation of the M5 gameplay actor contract (v1).
///
/// Execution boundary rules (ROADMAP M5):
///  - At most ONE request runs at a time (single-writer rule). A request
///    while Running is Rejected(StateTransition, "busy"). Stop() is the
///    only request allowed while busy — it interrupts the active one.
///  - Every terminal transition emits an <see cref="ActorAuditRecord"/>.
///  - Retries are deduped through the <see cref="ActorEffectLedger"/>: an
///    explicit idempotency key whose prior attempt may have executed
///    (Completed/Interrupted/TimedOut) is never re-executed; the duplicate
///    is Rejected(StateTransition) pre-flight. Every action supports a
///    timeout budget (§17 reason: Move → Navigation, else Starvation).
///  - All execution goes through normal gameplay services: movement rides
///    the client-authored unit-movement model (CSMoveUnitPacket's
///    UnitMoveType path via VehicleMovementModel — position apply +
///    SCOneUnitMovementPacket broadcast + transform finalize; the same
///    model family DriveVehicle rides), targeting sets Unit.CurrentTarget,
///    casting calls Character.UseSkill (the exact learned-skill branch
///    CSStartSkillPacket uses). Observe reads the region graph + character
///    state — no packets.
///
/// Threading: NOT thread-safe by itself. The scheduler's per-bot execution
/// lease (IPlayerBotScheduler) guarantees at most one in-flight step per
/// bot, and the M5 A1 marshal executes every step on the single execution
/// boundary (the game-loop thread) — the actor is driven from exactly one
/// execution context at a time.
/// </summary>
public class GameplayActor : IGameplayActor
{
    /// <summary>Arrival radius for Move legs (same checkpoint model as Simulation.RangeToCheckPoint).</summary>
    public const float ArrivalRadius = 0.5f;

    /// <summary>Default navigation budget for a Move request.</summary>
    public static readonly TimeSpan DefaultMoveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Max audit records retained (newest last).</summary>
    private const int MaxTraceRecords = 512;

    private readonly List<ActorAuditRecord> _trace = [];
    private readonly ActorEffectLedger _ledger = new();
    private ActorRequest? _active;

    public uint ActorId => Character.ObjId;

    public Character Character { get; }

    public ActorRequest? ActiveRequest => _active;

    public IReadOnlyList<ActorAuditRecord> AuditTrace => _trace;

    public GameplayActor(Character character)
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
    }

    #region Observe

    public ActorObservation Observe()
    {
        // REQ-M5.3-7 (carries REQ-M5-10): every action executes only on the
        // A1 marshal seam (the game-loop thread). Observe reads world state
        // that is only consistent on the seam — the same debug thread-affinity
        // assertion family as MoveTo/Stop.
        ExecutionBoundary.AssertOnExecutionThread("Observe");

        var observation = new ActorObservation
        {
            ActorId = ActorId,
            Position = Character.Transform.World.Position,
            CurrentTargetObjId = Character.CurrentTarget?.ObjId ?? 0,
            Hp = Character.Hp,
            MaxHp = Character.MaxHp,
            Mp = Character.Mp,
            MaxMp = Character.MaxMp,
            NearbyCharacterObjIds = WorldManager.GetAround<Character>(Character, 25f).Select(c => c.ObjId).ToList(),
            NearbyNpcObjIds = WorldManager.GetAround<Npc>(Character, 25f).Select(n => n.ObjId).ToList(),
            NearbyDoodadObjIds = WorldManager.GetAround<Doodad>(Character, 25f).Select(d => d.ObjId).ToList(),
            ActiveQuestIds = Character.Quests?.ActiveQuests.Keys.ToList() ?? []
        };

        // Observe is a query, not a mutation: it completes immediately and
        // still emits the audit record (every action emits one).
        var request = NewRequest(ActorActionType.Observe, 0);
        request.Accept("observe");
        request.Start("query");
        Finish(request, request.Complete(observation));
        return observation;
    }

    #endregion

    #region Actions

    public ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
        // REQ-M5.3-7 (carries REQ-M5-10): every action executes only on the
        // A1 marshal seam (the game-loop thread). This debug thread-affinity
        // assertion fires when the action runs off the boundary — a
        // controller may enqueue a wake but never mutate a Character off
        // the game loop.
        ExecutionBoundary.AssertOnExecutionThread("MoveTo");

        var request = NewRequest(ActorActionType.Move, 0, destination, timeout: timeout ?? DefaultMoveTimeout, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "move"))
            return request;

        if (speed <= 0f)
            return Reject(request, ActorFailureReason.RejectedAction, "speed must be positive");
        if (!destination.IsFinite())
            return Reject(request, ActorFailureReason.RejectedAction, "destination must be finite");
        return StartMove(request, destination, speed);
    }

    /// <summary>
    /// Sets up the movement leg for an already-accepted Move request and
    /// starts it Running (or completes immediately when already there).
    /// </summary>
    private ActorRequest StartMove(ActorRequest request, Vector3 destination, float speed)
    {
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, destination, false) <= ArrivalRadius
            && Math.Abs(Character.Transform.World.Position.Z - destination.Z) <= ArrivalRadius)
        {
            // The no-op leg still walks the full lifecycle: a Completed
            // record must always carry Requested → Accepted → Running →
            // Completed (the scenario lifecycle law), so the actor never
            // completes a move without entering Running.
            request.Start("walking");
            return Complete(request, "already at destination");
        }

        _moveTarget = destination;
        _moveSpeed = speed;
        request.Start("walking");
        return request;
    }

    public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
        // REQ-M5.3-7 — see MoveTo.
        ExecutionBoundary.AssertOnExecutionThread("MoveToUnit");

        var request = NewRequest(ActorActionType.Move, targetObjId, timeout: timeout ?? DefaultMoveTimeout, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "move"))
            return request;

        var unit = ResolveUnit(targetObjId);
        if (unit == null)
            return Reject(request, ActorFailureReason.RejectedAction, "target unit not found");
        return StartMove(request, unit.Transform.World.Position, speed);
    }

    public ActorRequest Stop()
    {
        // REQ-M5.3-7 — see MoveTo.
        ExecutionBoundary.AssertOnExecutionThread("Stop");

        var request = NewRequest(ActorActionType.Stop, 0);
        if (!request.Accept("stop"))
            return request; // defensive; should never happen

        // Interrupt whatever is running (if anything), then complete the stop.
        if (_active is { IsTerminal: false })
        {
            // A walking Move is halted mid-leg: emit the canonical Stopping
            // broadcast so observers see the standstill (dossier §1.6 — the
            // M6 "frozen bot" bug class). Non-movement interrupts broadcast
            // nothing.
            if (_active.Action == ActorActionType.Move && _moveTarget != null)
                BroadcastStop();
            InterruptActive("stop requested");
        }
        request.Start("interrupting");
        Finish(request, request.Complete(detail: "stopped"));
        return request;
    }

    public ActorRequest SetTarget(uint targetObjId)
    {
        // REQ-M5.3-7 (carries REQ-M5-10): every action executes only on the
        // A1 marshal seam — SetTarget mutates Character/world state.
        ExecutionBoundary.AssertOnExecutionThread("SetTarget");

        var request = NewRequest(ActorActionType.Target, targetObjId);
        if (!TryBegin(request, "target"))
            return request;

        var unit = ResolveUnit(targetObjId);
        if (unit == null)
            return Reject(request, ActorFailureReason.RejectedAction, "target not found in world");

        Character.CurrentTarget = unit;

        // REQ-M5.3-5: same resolve -> assign -> broadcast order the engine's
        // CSChangeTargetPacket.Read performs — observers must see the bot's
        // target change. The actor never clears targets (0/unknown objIds are
        // rejected above), so no clear-target branch is needed, and the
        // Rejected path mutates nothing / emits nothing.
        Character.BroadcastPacket(
            new SCTargetChangedPacket(Character.ObjId, Character.CurrentTarget?.ObjId ?? 0),
            true);

        return Complete(request, $"targeting {unit.ObjId}");
    }

    public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
    {
        // REQ-M5.3-7 (carries REQ-M5-10): every action executes only on the
        // A1 marshal seam — Cast mutates Character/world state.
        ExecutionBoundary.AssertOnExecutionThread("Cast");

        var request = NewRequest(ActorActionType.Cast, targetObjId, skillId: skillId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "cast"))
            return request;

        // Validation gate 1: the skill template must exist.
        var template = SkillManager.Instance.GetSkillTemplate(skillId);
        if (template == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"unknown skill {skillId}");

        // Validation gate 2: the character must actually know the skill
        // (learned, default/common, or a variant of one) — same rule the
        // CSStartSkillPacket learned-skill branch applies.
        var known = Character.Skills?.Skills.ContainsKey(skillId) == true
                    || Character.Skills?.IsVariantOfSkill(skillId) == true
                    || SkillManager.Instance.IsDefaultSkill(skillId)
                    || SkillManager.Instance.IsCommonSkill(skillId);
        if (!known)
            return Reject(request, ActorFailureReason.RejectedAction, $"skill {skillId} not learned");

        // Validation gate 3: the target must resolve to a unit we can cast at.
        var target = ResolveUnit(targetObjId);
        if (target == null)
            return Reject(request, ActorFailureReason.RejectedAction, "cast target not found in world");

        request.Start($"casting {skillId} on {target.ObjId}");

        // Execute through the REAL engine path — the same call the
        // CSStartSkillPacket learned-skill branch makes.
        var result = Character.UseSkill(skillId, target);
        if (result == SkillResult.Success)
            return Complete(request, result, $"skill {skillId} cast succeeded");
        return Reject(request, ActorFailureReason.RejectedAction, $"skill {skillId} refused: {result}");
    }

    public bool Interrupt(Guid traceId)
    {
        if (_active == null || _active.TraceId != traceId || _active.IsTerminal)
            return false;
        InterruptActive("interrupted by controller");
        return true;
    }

    #endregion

    #region Quest actions (M5 vocabulary — real engine paths)

    private PlayerBotController? _questController;

    /// <summary>
    /// Quest ops compose around the shared PlayerBotController, which is
    /// itself a thin wrapper over the ordinary character's quest surfaces
    /// (CharacterQuests.AddQuest / UnitEvents / QuestManager.DoReportEvents).
    /// No bot-only quest state is created here.
    /// </summary>
    private PlayerBotController QuestController => _questController ??= new PlayerBotController(Character);

    public ActorRequest AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.AcceptQuest, questId,
            payload: new QuestAcceptParams(acceptorType, acceptorId), idempotencyKey: idempotencyKey);
        request.AddQuestContext(questId);
        if (!TryBegin(request, "accept quest"))
            return request;

        // Quest-credit idempotency marker (ROADMAP M5): a quest that was
        // ALREADY accepted by a prior attempt (fresh-key retry after a
        // timeout ambiguity) must never re-enter AddQuest. The engine's own
        // duplicate check would refuse it too, but the ledger probe makes
        // the refusal pre-flight and explicit — the audit record shows no
        // Running transition.
        if (_ledger.IsEffectApplied(ActorIdempotency.EffectKey("questcredit", questId, "accept"))
            && Character.Quests?.ActiveQuests.ContainsKey(questId) == true)
            return Reject(request, ActorFailureReason.StateTransition,
                $"quest {questId} accept credit already applied (duplicate accept refused pre-flight)");

        request.Start($"accepting quest {questId} via {acceptorType}/{acceptorId}");
        var accepted = QuestController.AcceptQuest(questId, acceptorType, acceptorId);
        if (accepted)
        {
            // Record the accept credit AFTER the engine applied it, so a
            // fresh-key retry can prove the credit already landed.
            _ledger.RecordEffect(ActorIdempotency.EffectKey("questcredit", questId, "accept"), request.TraceId);
            return Complete(request, accepted, $"quest {questId} accepted ({acceptorType}/{acceptorId})");
        }
        return Reject(request, ActorFailureReason.RejectedAction,
            $"quest {questId} accept refused by engine gate ({acceptorType}/{acceptorId})");
    }

    public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.AdvanceQuest, questId, idempotencyKey: idempotencyKey);
        request.AddQuestContext(questId);
        if (!TryBegin(request, "advance quest"))
            return request;

        var quest = Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"quest {questId} not active (cannot advance)");

        request.Start($"advancing quest {questId} (step {quest.Step}, status {quest.Status})");
        _ = quest.RunCurrentStep();
        return Complete(request, true,
            $"quest {questId} advanced (step {quest.Step}, status {quest.Status})");
    }

    public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null)
        => TurnIn(questId, ActorActionType.TurnInQuest, npcObjId, selectedReward, idempotencyKey);

    public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null)
        => TurnIn(questId, ActorActionType.TurnInDoodad, doodadObjId, selectedReward, idempotencyKey);

    public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null)
        => TurnIn(questId, ActorActionType.AutoTurnIn, 0, selectedReward, idempotencyKey);

    /// <summary>
    /// Turn-in through the real packet path (QuestManager.DoReportEvents),
    /// then the same step-machine advances the world pipeline performs after
    /// a report event. The world target must resolve when one is given;
    /// 0 (auto turn-in) always resolves.
    /// </summary>
    private ActorRequest TurnIn(uint questId, ActorActionType action, uint targetObjId, int selectedReward, string? idempotencyKey)
    {
        var request = NewRequest(action, questId, payload: new QuestTurnInParams(targetObjId, selectedReward), idempotencyKey: idempotencyKey);
        request.AddQuestContext(questId);
        if (!TryBegin(request, "turn in"))
            return request;

        var quest = Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"quest {questId} not active (cannot turn in)");

        if (action != ActorActionType.AutoTurnIn && targetObjId != 0)
        {
            var target = ResolveUnit(targetObjId);
            if (target == null)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"turn-in target {targetObjId} not found in world (quest {questId})");
        }

        request.Start($"turning in quest {questId} (target {targetObjId}, selected {selectedReward})");
        switch (action)
        {
            case ActorActionType.TurnInQuest:
                _ = QuestController.ReportTurnIn(questId, targetObjId, selectedReward);
                break;
            case ActorActionType.TurnInDoodad:
                _ = QuestController.ReportDoodadTurnIn(questId, targetObjId, selectedReward);
                break;
            default:
                _ = QuestController.AutoTurnIn(questId, selectedReward);
                break;
        }

        // The report event drives the step machine; the world pipeline's
        // post-event evaluations (the QuestManager evaluation queue) are the
        // last legs — completion drops the quest from ActiveQuests (terminal
        // state, correct engine behavior). Drain the same evaluations while
        // the step machine still advances, bounded — a turn-in can take more
        // than one pass (report → Ready → Reward → completed+drop) and each
        // pass is the engine's own evaluation. Stopping on a false advance
        // keeps a not-ready quest (objectives unmet) from being force-advanced.
        var guard = 0;
        while (Character.Quests?.ActiveQuests.ContainsKey(questId) == true && guard++ < 8)
        {
            if (!quest.RunCurrentStep())
                break;
        }

        var completed = Character.Quests?.HasQuestCompleted(questId) == true;
        if (completed)
        {
            // Reward idempotency marker: recorded AFTER the reward landed so
            // a fresh-key retry can prove the reward was already credited.
            _ledger.RecordEffect(ActorIdempotency.EffectKey("questcredit", questId, "reward"), request.TraceId);
            return Complete(request, completed, $"quest {questId} completed by turn-in");
        }
        return Complete(request, completed, $"quest {questId} turn-in executed (still active)");
    }

    #endregion

    #region B1 actions (M5 vocabulary — real engine paths)

    /// <summary>Maximum flat distance for an Interact request (doodad interaction range).</summary>
    public const float MaxInteractRange = 25f;

    public ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Interact, doodadObjId, skillId: skillId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "interact"))
            return request;

        var doodad = Character.ParentWorld?.GetDoodad(doodadObjId);
        if (doodad == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"doodad {doodadObjId} not found in world");
        if (skillId != 0 && SkillManager.Instance.GetSkillTemplate(skillId) == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"unknown interaction skill {skillId}");
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, doodad.Transform.World.Position, false) > MaxInteractRange)
            return Reject(request, ActorFailureReason.RejectedAction, $"doodad {doodadObjId} out of interaction range");
        // The engine's own #1443 guard: doodads scheduled for despawn refuse
        // interaction. Mirror it pre-flight so the refusal is a Rejected
        // instead of a silent engine no-op.
        if (doodad.Despawn > DateTime.MinValue)
            return Reject(request, ActorFailureReason.RejectedAction, $"doodad {doodadObjId} scheduled for despawn");

        request.Start($"interacting with doodad {doodadObjId} (skill {skillId})");

        // The real engine path: the same Doodad.Use call interaction skills
        // (Alchemy/Butcher/CraftStart/…) and the CSLootOpenBagPacket
        // func-driven branch make. Phase advancement happens inside.
        doodad.Use(Character, skillId);
        return Complete(request, true, $"doodad {doodadObjId} interacted (phase {doodad.FuncGroupId})");
    }

    public ActorRequest Loot(uint lootOwnerObjId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Loot, lootOwnerObjId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "loot"))
            return request;

        var owner = Character.ParentWorld?.GetBaseUnit(lootOwnerObjId);
        if (owner == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"loot owner {lootOwnerObjId} not found in world");
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, owner.Transform.World.Position, false) > LootingContainer.MaxLootingRange)
            return Reject(request, ActorFailureReason.RejectedAction, $"loot owner {lootOwnerObjId} out of loot range");

        var container = owner.LootingContainer;
        if (container.Items.Count <= 0)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"nothing to loot from {lootOwnerObjId} (empty or already looted)");

        var before = container.Items.Count;
        request.Start($"looting {lootOwnerObjId} (bag entries {before})");

        // The exact call CSLootOpenBagPacket makes with lootAll=true. The
        // engine removes each granted entry (TryReserveLootItem), so a retry
        // after success sees an empty container and grants nothing.
        container.OpenBag(Character, owner, lootAll: true);

        var granted = before - container.Items.Count;
        return Complete(request, granted, granted > 0
            ? $"looted {granted} item(s) from {lootOwnerObjId}"
            : $"nothing to loot from {lootOwnerObjId} (empty or already looted)");
    }

    public ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.UseItem, itemTemplateId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "use item"))
            return request;

        // 1. Resolve the item through NORMAL inventory services — the same
        //    template lookup the client's use-item path performs. Only the
        //    character's own inventory bag is usable.
        var inventory = Character.Inventory;
        if (inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        inventory.Bag.GetAllItemsByTemplate(itemTemplateId, -1, out var items, out _);
        var item = items.FirstOrDefault();
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} not found in inventory");

        // 2. Usage rules: the item must carry a use skill and the skill
        //    template must exist (same gate the SkillItem packet branch
        //    relies on — a template-less use would silently no-op there).
        var itemTemplate = item.Template;
        if (itemTemplate == null || itemTemplate.UseSkillId == 0)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} is not usable (no use skill)");
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(itemTemplate.UseSkillId);
        if (skillTemplate == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"item {itemTemplateId} use skill {itemTemplate.UseSkillId} not found");

        // Explicit targets must resolve; the default is self (most item uses
        // are self-targeted).
        Unit target = Character;
        if (targetObjId != 0)
        {
            target = ResolveUnit(targetObjId);
            if (target == null)
                return Reject(request, ActorFailureReason.RejectedAction, $"use target {targetObjId} not found in world");
        }

        request.Start($"using item {itemTemplateId} (instance {item.Id}, skill {itemTemplate.UseSkillId})");

        // 3. Apply through the REAL gameplay pipeline — the exact path the
        //    CSStartSkillPacket SkillItem branch takes: Skill.Use with a
        //    SkillItem caster. The engine evaluates requirements, cooldown,
        //    GCD, mana and reagents through the ordinary inventory; no
        //    bot-only resource path (spec §8 / AGENTS.md #9-#10). A refusal
        //    (CooldownTime, LackMana, …) happens BEFORE any consumption.
        //    NOTE: the 3-arg ctor is required — it sets Type = SkillCasterType.Item,
        //    which the engine's GetInitialTarget relies on to skip the
        //    unit-lookup hackfix (a parameterless SkillItem defaults to
        //    SkillCasterType.Unit and NoTargets through GetUnit(0)).
        var skill = new Skill(skillTemplate);
        var caster = new SkillItem(Character.ObjId, item.Id, item.TemplateId);
        var castTarget = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
        castTarget.ObjId = target.ObjId;
        var result = skill.Use(Character, caster, castTarget, null, false, out _);
        if (result != SkillResult.Success)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} use refused by engine: {result}");

        // 4. Cast-time completion: casting-time skills (e.g. the farm-wagon
        //    summon scroll's 5 s cast) apply their effects through a
        //    CastTask scheduled on the task scheduler. That scheduler is
        //    unreliable for headless bots (silent cancellation, late fires —
        //    observed live: the cast landed minutes late or never). Drive
        //    the cast synchronously instead — the exact call the CastTask
        //    would make — then mark the skill cancelled so the stray
        //    scheduled task bails out (CastTask checks Skill.Cancelled)
        //    instead of double-casting.
        if (Character.SkillTask != null)
        {
            Character.SkillTask = null;
            skill.Cast(Character, caster, target, castTarget, null);
            skill.Cancelled = true;
        }

        // 5. Record the applied-effect fingerprint (B1 idempotency layer):
        //    correlation for the M8 economic audit. The request-level key
        //    dedupe is the PRIMARY retry guard — a same-key retry is
        //    rejected pre-flight, so the item can never be consumed twice
        //    by a retry; the item charge count is the engine-true backstop.
        _ledger.RecordEffect(ActorIdempotency.EffectKey("itemuse", itemTemplateId, item.Id.ToString()), request.TraceId);
        return Complete(request, result, $"item {itemTemplateId} used (skill {itemTemplate.UseSkillId})");
    }

    public ActorRequest Equip(uint itemTemplateId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Equip, itemTemplateId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "equip"))
            return request;

        var inventory = Character.Inventory;
        if (inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        // 1. Resolve the item through NORMAL inventory services — the same
        //    template lookup the client's move path performs. Only the
        //    character's own bag is a valid equip source.
        inventory.Bag.GetAllItemsByTemplate(itemTemplateId, -1, out var items, out _);
        var item = items.FirstOrDefault();
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} not found in bag");

        // Equip-credit idempotency marker (DepositItem pattern): when a
        // prior attempt already moved this exact instance out of the bag,
        // the equip is already done — refuse pre-flight. (The conjunctive
        // check keeps a legitimately re-acquired item retryable.)
        if (_ledger.IsEffectApplied(ActorIdempotency.EffectKey("equip", itemTemplateId, item.Id.ToString()))
            && inventory.Bag.GetItemByItemId(item.Id) == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"item {itemTemplateId} (instance {item.Id}) already equipped (duplicate refused pre-flight)");

        // 2. Target slot through the engine's own slot table: the first
        //    EMPTY allowed slot, else the first allowed slot — the client's
        //    equip-over-occupied swap semantics (SplitOrMoveItem moves the
        //    occupant back to the vacated bag slot).
        var allowedSlots = EquipmentContainer.GetAllowedGearSlots(item.Template);
        if (allowedSlots.Count == 0)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} is not equippable");
        var targetSlot = allowedSlots.FirstOrDefault(s => inventory.Equipment.GetItemBySlot((int)s) == null, allowedSlots[0]);

        request.Start($"equipping item {itemTemplateId} (instance {item.Id}) into {targetSlot}");

        // 3. REAL engine path — the exact call CSSwapItemsPacket makes for
        //    an Inventory→Equipment move: Inventory.SplitOrMoveItem with the
        //    SwapItems task type. The engine's EquipmentContainer.CanAccept
        //    validates slot compatibility BEFORE anything moves; a refusal
        //    moves nothing. (The engine has no level gate on this path.)
        if (!inventory.SplitOrMoveItem(ItemTaskType.SwapItems, item.Id, SlotType.Inventory, (byte)item.Slot,
                0, SlotType.Equipment, (byte)targetSlot))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"equip of item {itemTemplateId} into {targetSlot} refused by engine");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("equip", itemTemplateId, item.Id.ToString()), request.TraceId);
        return Complete(request, true, $"equipped item {itemTemplateId} into {targetSlot}");
    }

    public ActorRequest Mount(uint mateObjId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Mount, mateObjId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "mount"))
            return request;

        var mateManager = Character.ParentWorld?.MateManager;
        if (mateManager == null)
            return Reject(request, ActorFailureReason.RejectedAction, "no mate manager in world");

        // 1. Already mounted → StateTransition (mount-state discipline). A
        //    retry that got past the key gate is refused here before any
        //    engine call, so the mount state can never flip twice.
        if (mateManager.GetIsMounted(Character.ObjId, out _) != null)
            return Reject(request, ActorFailureReason.StateTransition, "already mounted");

        // 2. The target mount must be an active mate in the normal registry,
        //    owned by the actor, with a free driver seat.
        var mate = mateManager.GetActiveMateByMateObjId(mateObjId);
        if (mate == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"mount {mateObjId} not found or not active");
        if (mate.OwnerObjId != Character.ObjId)
            return Reject(request, ActorFailureReason.RejectedAction, $"mount {mateObjId} not owned by actor");
        if (!mate.Passengers.TryGetValue(AttachPointKind.Driver, out var driverSeat) || driverSeat._objId != 0)
            return Reject(request, ActorFailureReason.RejectedAction, $"mount {mateObjId} driver seat unavailable");

        request.Start($"mounting mate {mate.ObjId} (tl {mate.TlId})");

        // 3. Real engine path — the same MountMate the CSMountMatePacket
        //    handler drives (character-based entry; packets no-op headless).
        if (!mateManager.MountMate(Character, mate.TlId, AttachPointKind.Driver, AttachUnitReason.None))
            return Reject(request, ActorFailureReason.RejectedAction, $"mount {mateObjId} refused by engine");

        // 4. Post-state verification: the engine must have attached the rider.
        if (mateManager.GetIsMounted(Character.ObjId, out _) == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"mount {mateObjId} did not take effect");

        return Complete(request, true, $"mounted mate {mate.ObjId}");
    }

    public ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Dismount, mateObjId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "dismount"))
            return request;

        var mateManager = Character.ParentWorld?.MateManager;
        if (mateManager == null)
            return Reject(request, ActorFailureReason.RejectedAction, "no mate manager in world");

        // 1. Not mounted → StateTransition (nothing to dismount). A retry
        //    after a successful dismount is refused here — the state can
        //    never flip back by re-running the request.
        var mate = mateManager.GetIsMounted(Character.ObjId, out var attachPoint);
        if (mate == null)
            return Reject(request, ActorFailureReason.StateTransition, "not mounted");
        if (mateObjId != 0 && mate.ObjId != mateObjId)
            return Reject(request, ActorFailureReason.StateTransition,
                $"mounted on mate {mate.ObjId}, not {mateObjId}");

        request.Start($"dismounting mate {mate.ObjId} (tl {mate.TlId}, seat {attachPoint})");

        // 2. Real engine path — the exact UnMountMate the CSUnMountMatePacket
        //    handler uses (already character-based).
        mateManager.UnMountMate(Character, mate.TlId, attachPoint, AttachUnitReason.None);

        // 3. Post-state verification: the engine must have detached the rider.
        if (mateManager.GetIsMounted(Character.ObjId, out _) != null)
            return Reject(request, ActorFailureReason.RejectedAction, "dismount did not take effect");

        return Complete(request, true, $"dismounted mate {mate.ObjId}");
    }

    public ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Drive, vehicleObjId, destination, timeout: timeout ?? DefaultMoveTimeout, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "drive"))
            return request;

        if (speed <= 0f)
            return Reject(request, ActorFailureReason.RejectedAction, "speed must be positive");
        if (!destination.IsFinite())
            return Reject(request, ActorFailureReason.RejectedAction, "destination must be finite");

        // 1. The target must resolve to a driveable vehicle (Slave ground
        //    vehicle or Mate mount) in the actor's world.
        var vehicle = Character.ParentWorld?.GetBaseUnit(vehicleObjId);
        if (vehicle is not (Slave or Mate))
            return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {vehicleObjId} not found in world");

        // 2. Driver-seat preflight — the engine is never re-entered without
        //    the seat, so a retry cannot start a second drive.
        var inDriverSeat = vehicle switch
        {
            Slave slave => slave.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var driver) && driver == Character,
            Mate mate => mate.Passengers.TryGetValue(AttachPointKind.Driver, out var seat) && seat._objId == Character.ObjId,
            _ => false
        };
        if (!inDriverSeat)
            return Reject(request, ActorFailureReason.StateTransition, $"not in driver seat of vehicle {vehicleObjId}");

        if (MathUtil.CalculateDistance(vehicle.Transform.World.Position, destination, false) <= ArrivalRadius
            && Math.Abs(vehicle.Transform.World.Position.Z - destination.Z) <= ArrivalRadius)
        {
            // Full lifecycle on the no-op leg too (Requested → Accepted →
            // Running → Completed) — a Completed drive never skips Running.
            request.Start($"driving vehicle {vehicle.ObjId} ({vehicle.Name})");
            return Complete(request, "already at destination");
        }

        _driveTarget = destination;
        _driveSpeed = speed;
        _driveVehicle = vehicle;
        request.Start($"driving vehicle {vehicle.ObjId} ({vehicle.Name})");
        return request;
    }

    /// <summary>
    /// Generic recover skill used by the world trade-pack pickup path —
    /// the same constant CSLootOpenBagPacket routes pack-style pickups
    /// through (11361). Housing-crate recover (15309) stays on the normal
    /// doodad interaction path and is intentionally NOT a pack pickup.
    /// </summary>
    public const uint GenericRecoverItemSkillId = 11361u;

    public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.PackPickup, doodadObjId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "pack pickup"))
            return request;

        if (Character.Inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        var doodad = Character.ParentWorld?.GetDoodad(doodadObjId);
        if (doodad == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"placed pack doodad {doodadObjId} not found in world");
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, doodad.Transform.World.Position, false) > MaxInteractRange)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"placed pack doodad {doodadObjId} out of interaction range");

        // The same routing rule CSLootOpenBagPacket applies: only a doodad
        // whose CURRENT phase carries a DoodadFuncRecoverItem with the
        // generic world recover skill is a recoverable trade pack. Housing
        // crate recover (15309) and other RecoverItem doodads are not
        // pack pickups — rejecting them here keeps the action vocabulary
        // honest instead of hijacking the engine's other recover paths.
        var recoverable = doodad.CurrentFuncs.Any(func =>
            func.FuncType == "DoodadFuncRecoverItem" && func.SkillId == GenericRecoverItemSkillId);
        if (!recoverable)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"doodad {doodadObjId} is not a recoverable trade pack (no DoodadFuncRecoverItem/{GenericRecoverItemSkillId} on phase {doodad.FuncGroupId})");

        // Engine pre-flight mirror: RecoverItem refuses to run when the
        // backpack slot cannot accept the pack (a carried pack, or a
        // glider with no bag space). Reject pre-flight with the taxonomy
        // reason instead of a silent engine error-packet no-op.
        if (!Character.Inventory.CanReplaceGliderInBackpackSlot())
            return Reject(request, ActorFailureReason.StateTransition,
                "backpack slot occupied — take off the carried pack/glider before picking up");

        var packItemId = doodad.ItemId;
        var packTemplateId = doodad.ItemTemplateId;
        request.Start($"picking up placed pack {doodadObjId} (item {packItemId}, template {packTemplateId})");

        // The REAL engine path — the exact call CSLootOpenBagPacket makes
        // for pack-style pickup. DoodadFuncRecoverItem grants the pack back
        // into the Backpack slot (IsAutoEquipTradePack → TakeoffBackpack +
        // AddOrMoveExistingItem) and clears the doodad's item refs; the
        // System-container check inside the func refuses a re-grant when
        // somebody already picked the pack up; RecoverItem then deletes the
        // doodad. All engine-true — no direct container writes here.
        new RecoverItem().Execute(Character, null, doodad, null, GenericRecoverItemSkillId, 0, null);

        // Post-state verification: the pack must have left the System
        // container and be equipped in the Backpack slot. The engine path
        // signals refusal only via error packets (silent headless), so the
        // container transition is the completion proof — and the retry
        // proof: a fresh-key retry finds the doodad gone (deleted) or the
        // item no longer in a System container, and grants nothing.
        var packItem = ItemManager.Instance.GetItemByItemId(packItemId);
        if (packItem == null
            || packItem._holdingContainer?.ContainerType != SlotType.Equipment
            || Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id != packItemId)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"pack pickup from {doodadObjId} did not take effect (already picked up or slot unavailable)");

        // Applied-effect fingerprint (M8 economic-audit correlation): the
        // pack instance is now actor-carried. The request-level key dedupe
        // is the PRIMARY retry guard; the deleted doodad is the
        // engine-true backstop.
        _ledger.RecordEffect(ActorIdempotency.EffectKey("packpickup", packTemplateId, packItemId.ToString()), request.TraceId);
        return Complete(request, packItemId,
            $"picked up placed pack {doodadObjId} (item {packItemId}, template {packTemplateId})");
    }

    public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.PutDown, packItemTemplateId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "put down pack"))
            return request;

        if (Character.Inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        // 1. The pack must be carried in the Backpack equipment slot — the
        //    state PackPickup / pack crafting leave it in, and the exact
        //    lookup PutDownBackpackEffect performs on the SkillItem caster
        //    (Inventory.Equipment.GetItemByItemId). A pack in the bag is
        //    not placeable.
        var pack = Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        if (pack == null || pack.TemplateId != packItemTemplateId)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"trade pack {packItemTemplateId} not carried in the backpack slot");
        if (!ItemManager.Instance.IsAutoEquipTradePack(packItemTemplateId))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"item {packItemTemplateId} is not an auto-equip trade pack");

        // 2. Usage rules: the pack must carry a put-down use skill and the
        //    skill template must exist (the same gate the SkillItem packet
        //    branch relies on — a template-less use would silently no-op).
        var packTemplate = pack.Template;
        if (packTemplate == null || packTemplate.UseSkillId == 0)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"trade pack {packItemTemplateId} has no put-down use skill");
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(packTemplate.UseSkillId);
        if (skillTemplate == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"trade pack {packItemTemplateId} put-down skill {packTemplate.UseSkillId} not found");

        request.Start($"putting down trade pack {packItemTemplateId} (instance {pack.Id}, skill {packTemplate.UseSkillId})");

        // 3. Apply through the REAL gameplay pipeline — the exact path the
        //    CSStartSkillPacket SkillItem branch takes: Skill.Use with a
        //    SkillItem caster (3-arg ctor sets SkillCasterType.Item, which
        //    the engine's GetInitialTarget relies on). The effect
        //    (PutDownBackpackEffect) moves the pack into the System
        //    container and spawns the placed-pack doodad through the
        //    normal doodad spawn services.
        var skill = new Skill(skillTemplate);
        var caster = new SkillItem(Character.ObjId, pack.Id, pack.TemplateId);
        var castTarget = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
        castTarget.ObjId = Character.ObjId;
        var result = skill.Use(Character, caster, castTarget, null, false, out _);
        if (result != SkillResult.Success)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"trade pack {packItemTemplateId} put-down refused by engine: {result}");

        // 3b. Emulator-gap bridge: plot-only put-down skills (pack 26488 →
        // skill 20412, plot 5 — 사방치기) return Success at Skill.Use's
        // PlotOnly branch BEFORE the skill's effect list applies, and the
        // plot executor only fires its own plot_effects (projectile
        // visuals), so the pack would never leave the Backpack slot. Apply
        // the skill's OWN PutDownBackpackEffect directly — the exact engine
        // effect retail applies at the plot step — with the same call shape
        // the plot executor uses. Unit-world fixture packs carry no plot
        // (the effect already applied synchronously inside Use), so this
        // branch is a no-op there.
        if (Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == pack.Id
            && skillTemplate.Effects?.FirstOrDefault(e => e.Template is PutDownBackpackEffect) is { Template: PutDownBackpackEffect putDownEffect })
        {
            var effectCaster = new SkillItem(Character.ObjId, pack.Id, pack.TemplateId);
            var effectTarget = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
            effectTarget.ObjId = Character.ObjId;
            putDownEffect.Apply(Character, effectCaster, Character, effectTarget,
                new CastPlot(skillTemplate.Plot?.Id ?? 0, (ushort)skill.TlId, 0, skillTemplate.Id),
                new EffectSource(skill), null, DateTime.UtcNow);
        }

        // 4. Post-state verification: PutDownBackpackEffect early-returns
        //    (public-farm exclusion, house permission, invalid item)
        //    WITHOUT failing the skill — the pack must have LEFT the
        //    Backpack slot (moved to the System container). That move is
        //    also the retry-proof state: a retry finds no pack in the slot
        //    and is refused pre-flight, so the pack can never be placed
        //    twice.
        //
        //    LIVE note: the real pack put-down skills (e.g. 20412 for pack
        //    26488) are plot-only (plot 5 — 사방치기): Skill.Use dispatches
        //    the plot via Task.Run and returns Success immediately, and the
        //    put-down effect (SpecialEffect 37) lands ~1.8 s later when the
        //    plot's direction events fire. Unit-world fixture packs carry no
        //    plot, so the effect is synchronous there. Wait for the async
        //    plot by holding the request Running and polling the slot in
        //    Tick (the same pattern as the craft-queue drain).
        if (Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == pack.Id)
        {
            _pendingPutDownPackId = pack.Id;
            return request;
        }

        // Applied-effect fingerprint (M8 economic-audit correlation): the
        // pack instance transitioned to a placed pack.
        _ledger.RecordEffect(ActorIdempotency.EffectKey("packputdown", packItemTemplateId, pack.Id.ToString()), request.TraceId);
        return Complete(request, true,
            $"trade pack {packItemTemplateId} placed (instance {pack.Id}, now in System container)");
    }

    public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.LoadPackOntoVehicle, slaveObjId,
            payload: new LoadPackOntoVehicleParams(placedPackDoodadObjId), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "load pack onto vehicle"))
            return request;

        if (Character.Inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        // 1. The vehicle must resolve in the actor's own world (the same
        //    registry the slave packet handlers use).
        var slave = Character.ParentWorld?.GetSlaveByObjId(slaveObjId);
        if (slave == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"vehicle {slaveObjId} not found in world");
        if (slave.Hp <= 0)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"vehicle {slaveObjId} is dead");

        // 2. Range — the same adjacency gate the engine applies (retail
        //    load happens at the vehicle's side).
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, slave.Transform.World.Position, false) > PackVehicleService.MaxLoadRange)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"vehicle {slaveObjId} out of interaction range");

        // 3. The placed-pack doodad must resolve pre-flight (the engine
        //    maps an unresolvable doodad to PlacedPackNotFound).
        Doodad? placedPack = null;
        if (placedPackDoodadObjId is { } doodadObjId)
        {
            placedPack = Character.ParentWorld?.GetDoodad(doodadObjId);
            if (placedPack == null)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"placed pack doodad {doodadObjId} not found in world");
            // Retry-proof state: an already-attached doodad is refused
            // pre-flight so the pack can never be loaded twice.
            if (placedPack.ParentObjId != 0 || placedPack.AttachPoint != AttachPointKind.None)
                return Reject(request, ActorFailureReason.StateTransition,
                    $"placed pack doodad {doodadObjId} is already attached");
        }
        else
        {
            // 3b. Carried-pack pre-flight (the same checks the engine path
            //     performs — the PutDown contract shape): the pack must be
            //     in the Backpack equipment slot and be a real auto-equip
            //     trade pack. Rejections land BEFORE the request starts so
            //     no Running work is recorded for them.
            var carriedPack = Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (carriedPack == null)
                return Reject(request, ActorFailureReason.RejectedAction,
                    "no trade pack carried in the backpack slot");
            if (carriedPack is not Backpack || !ItemManager.Instance.IsAutoEquipTradePack(carriedPack.TemplateId))
                return Reject(request, ActorFailureReason.RejectedAction,
                    "carried item is not a trade pack");
        }

        request.Start($"loading pack onto vehicle {slaveObjId} (obj {slave.ObjId}, model {slave.ModelId}, {(placedPackDoodadObjId is null ? "carried" : $"placed {placedPackDoodadObjId}")})");

        // 4. The REAL gameplay path — PackVehicleService drives the engine:
        //    container moves through the ordinary inventory, the pack
        //    doodad through DoodadManager.Create/Spawn, the snap onto the
        //    cargo point through the SlaveManager attach seam (model
        //    attach-point data). No manual attachment, no direct Transform
        //    write, no GM/reflection/DB shortcut.
        var result = placedPackDoodadObjId is null
            ? PackVehicleService.TryLoadCarriedPack(Character, slave, out var carried)
            : PackVehicleService.TryLoadPlacedPack(Character, slave, placedPack!, out carried);
        var data = carried;

        switch (result)
        {
            case PackVehicleService.PackLoadResult.Success:
                _ledger.RecordEffect(ActorIdempotency.EffectKey("packload", slave.TemplateId,
                    data?.PackItem?.Id.ToString() ?? "placed"), request.TraceId);
                return Complete(request, data,
                    $"pack loaded onto vehicle {slaveObjId} at cargo point {data?.AttachPoint} (item {data?.PackItem?.Id})");
            case PackVehicleService.PackLoadResult.DeadSlave:
                return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {slaveObjId} is dead");
            case PackVehicleService.PackLoadResult.OutOfRange:
                return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {slaveObjId} out of interaction range");
            case PackVehicleService.PackLoadResult.NotACargoVehicle:
                return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {slaveObjId} has no cargo points");
            case PackVehicleService.PackLoadResult.CargoFull:
                return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {slaveObjId} cargo is full");
            case PackVehicleService.PackLoadResult.NoCarriedPack:
                return Reject(request, ActorFailureReason.RejectedAction, "no trade pack carried in the backpack slot");
            case PackVehicleService.PackLoadResult.NotATradePack:
                return Reject(request, ActorFailureReason.RejectedAction, "carried item is not a trade pack");
            case PackVehicleService.PackLoadResult.PlacedPackNotFound:
                return Reject(request, ActorFailureReason.RejectedAction, "placed pack not found in world");
            case PackVehicleService.PackLoadResult.PlacedPackOutOfRange:
                return Reject(request, ActorFailureReason.RejectedAction, "placed pack out of interaction range");
            case PackVehicleService.PackLoadResult.PlacedPackAlreadyAttached:
                return Reject(request, ActorFailureReason.StateTransition, "placed pack is already attached");
            case PackVehicleService.PackLoadResult.PlacedPackNotRecoverable:
                return Reject(request, ActorFailureReason.RejectedAction, "placed pack is not a recoverable trade pack");
            default:
                return Reject(request, ActorFailureReason.RejectedAction, "engine refused the pack load");
        }
    }

    #endregion

    #region M5.1 vehicle actions (B2 — the vehicle/transfer manager surface)

    /// <summary>Maximum flat distance for a BoardVehicle request (boarding range).</summary>
    public const float MaxBoardRange = 25f;

    public ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.BoardVehicle, vehicleObjId,
            payload: new BoardVehicleParams(attachPoint), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "board vehicle"))
            return request;

        // 1. Already-boarded → StateTransition (the engine is never
        //    re-entered). Covers all three vehicle families: a slave
        //    registry attachment, a transfer seat bond, and an equipped
        //    glider. A retry that got past the key gate is refused here
        //    before any engine call, so boarding state can never flip twice.
        if (Character.ParentWorld?.SlaveManager.GetIsMounted(Character.ObjId, out _) != null)
            return Reject(request, ActorFailureReason.StateTransition, "already boarded on a slave");
        if (Character.Bonding != null)
            return Reject(request, ActorFailureReason.StateTransition, $"already seated on doodad {Character.Bonding.ObjId}");
        if (GetEquippedGlider() != null)
            return Reject(request, ActorFailureReason.StateTransition, "glider already equipped");

        // 2. Resolve the target through the normal vehicle/transfer manager
        //    registries (SlaveManager first, then TransferManager, then the
        //    glider item surface). Each family uses its own real engine path.
        var slave = Character.ParentWorld?.SlaveManager.GetSlaveByObjId(vehicleObjId);
        if (slave != null)
            return BoardSlave(request, slave, attachPoint);

        var transfer = Character.ParentWorld?.TransferManager.GetTransfers().FirstOrDefault(t => t.ObjId == vehicleObjId);
        if (transfer != null)
            return BoardTransferSeat(request, transfer, attachPoint);

        return BoardGlider(request, vehicleObjId);
    }

    /// <summary>
    /// Boards a slave vehicle (ships, farm wagons, tanks, machines) through
    /// SlaveManager.BindSlave — the exact call CSBindSlavePacket (driver)
    /// and DoodadFuncAttachment's ship branch (passenger) make.
    /// </summary>
    private ActorRequest BoardSlave(ActorRequest request, Slave slave, AttachPointKind attachPoint)
    {
        // Canonical gates the engine itself enforces (dossier §4.2): a dead
        // vehicle refuses boarding (324), the driver seat is locked to the
        // summoner while the Owner's-Mark buff is up (97), and an occupied
        // attach point is refused (96-family). Mirror them pre-flight so the
        // refusals are explicit Rejected records instead of silent no-ops.
        if (slave.IsDead)
            return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {slave.ObjId} is destroyed");
        if (slave.AttachedCharacters.ContainsKey(attachPoint))
            return Reject(request, ActorFailureReason.RejectedAction, $"attach point {attachPoint} on vehicle {slave.ObjId} is occupied");
        if (attachPoint == AttachPointKind.Driver
            && slave.Buffs.CheckBuff((uint)BuffConstants.OwnersMark)
            && slave.Summoner?.ObjId != Character.ObjId)
            return Reject(request, ActorFailureReason.RejectedAction, $"driver seat of vehicle {slave.ObjId} is locked to its owner");

        if (MathUtil.CalculateDistance(Character.Transform.World.Position, slave.Transform.World.Position, false) > MaxBoardRange)
            return Reject(request, ActorFailureReason.RejectedAction, $"vehicle {slave.ObjId} out of boarding range");

        request.Start($"boarding slave {slave.ObjId} (tl {slave.TlId}, seat {attachPoint})");

        // 3. Real engine path — the same BindSlave the CSBindSlavePacket
        //    handler and DoodadFuncAttachment's ship branch drive.
        Character.ParentWorld!.SlaveManager.BindSlave(Character, slave.ObjId, attachPoint, AttachUnitReason.BoardTransfer);

        // 4. Post-state verification: the engine must have attached the
        //    character at exactly the requested seat.
        var mounted = Character.ParentWorld.SlaveManager.GetIsMounted(Character.ObjId, out var actualPoint);
        if (mounted?.ObjId != slave.ObjId || actualPoint != attachPoint)
            return Reject(request, ActorFailureReason.RejectedAction, $"board of slave {slave.ObjId} did not take effect");

        // 5. Record the applied-effect fingerprint (B1 idempotency layer):
        //    the character is attached at a seat of the slave. A same-key
        //    retry is refused pre-flight (TryBegin), so the engine is never
        //    re-entered; a fresh-key retry is refused by the already-boarded
        //    gate above. Either way the slave can never be boarded twice.
        _ledger.RecordEffect(ActorIdempotency.EffectKey("board", slave.ObjId, attachPoint.ToString()), request.TraceId);
        return Complete(request, true, $"boarded slave {slave.ObjId} at {attachPoint}");
    }

    /// <summary>
    /// Boards a route-carriage transfer seat through the real bond path —
    /// the same interaction a passenger boarding a route carriage performs:
    /// the seat's DoodadFuncAttachment func row (resolved by the seeded
    /// interaction skill) drives Seat.LoadPassenger + BondDoodad + transform
    /// parenting + SCBondDoodadPacket (DoodadFuncAttachment.Use).
    /// </summary>
    private ActorRequest BoardTransferSeat(ActorRequest request, Transfer transfer, AttachPointKind attachPoint)
    {
        // The seat doodad whose attachment func row targets the requested
        // attach point. The transfer's AttachedDoodads is the same registry
        // the engine's transfer spawner populates.
        var seat = transfer.AttachedDoodads.FirstOrDefault(d =>
        {
            var funcs = DoodadManager.Instance.GetFuncsForGroup(d.FuncGroupId);
            return funcs.Any(f => f.FuncType == "DoodadFuncAttachment"
                && DoodadManager.Instance.GetFuncTemplate(f.FuncId, f.FuncType) is DoodadFuncAttachment
                {
                    AttachPointId: var point
                } && point == attachPoint);
        });
        if (seat == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"transfer {transfer.ObjId} has no {attachPoint} seat doodad");

        // Mirror the engine's own occupancy gate: Seat.LoadPassenger returns
        // -1 when the requested seat is full — refuse pre-flight instead of
        // a silent no-op.
        if (transfer.AttachedCharacters.Contains(Character))
            return Reject(request, ActorFailureReason.StateTransition, "already seated on this transfer");

        if (MathUtil.CalculateDistance(Character.Transform.World.Position, transfer.Transform.World.Position, false) > MaxBoardRange)
            return Reject(request, ActorFailureReason.RejectedAction, $"transfer {transfer.ObjId} out of boarding range");

        request.Start($"boarding transfer {transfer.ObjId} at seat {attachPoint} (seat doodad {seat.ObjId})");

        // 3. Real engine path — DoodadFuncAttachment.Use through the doodad
        //    interaction pipeline (the CSStartInteractionPacket → Doodad.Use
        //    chain). The interaction skill is the func row's own SkillId —
        //    DoodadManager.GetFunc resolves the row by that skill exactly
        //    like the client's seat interaction does.
        var attachmentFunc = DoodadManager.Instance.GetFuncsForGroup(seat.FuncGroupId)
            .First(f => f.FuncType == "DoodadFuncAttachment");
        seat.Use(Character, attachmentFunc.SkillId);

        // 4. Post-state verification: the bond must have landed on this seat.
        if (Character.Bonding?.ObjId != seat.ObjId)
            return Reject(request, ActorFailureReason.RejectedAction, $"board of transfer {transfer.ObjId} did not take effect");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("board", transfer.ObjId, attachPoint.ToString()), request.TraceId);
        return Complete(request, true, $"boarded transfer {transfer.ObjId} at {attachPoint} (seat doodad {seat.ObjId})");
    }

    /// <summary>
    /// Boards a glider by equipping it into the Backpack slot through the
    /// ordinary inventory path (SplitOrMoveItem — the CSSwapItemsPacket
    /// equip call). vehicleObjId addresses the glider ITEM TEMPLATE (the
    /// client's glider is an inventory item; deploy/fly is the item's use
    /// skill, a separate action).
    /// </summary>
    private ActorRequest BoardGlider(ActorRequest request, uint itemTemplateId)
    {
        if (Character.Inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        // The glider must be a real BackpackType.Glider item in the bag.
        var item = Character.Inventory.Bag.Items.FirstOrDefault(i =>
            i.TemplateId == itemTemplateId && i.Template is BackpackTemplate { BackpackType: BackpackType.Glider });
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"no glider {itemTemplateId} in inventory");

        request.Start($"boarding glider {itemTemplateId} (instance {item.Id})");

        // Real engine path — equip into the Backpack slot through the
        // ordinary inventory move (the CSSwapItemsPacket equip call). The
        // item carries its own Slot/SlotType, so the source is addressed
        // exactly like the packet handler addresses it.
        if (!Character.Inventory.SplitOrMoveItem(ItemTaskType.SwapItems, item.Id, item.SlotType, (byte)item.Slot,
                0, SlotType.Equipment, (byte)EquipmentItemSlot.Backpack))
            return Reject(request, ActorFailureReason.RejectedAction, $"glider {itemTemplateId} equip refused by engine");

        // 4. Post-state verification: the glider must sit in the Backpack slot.
        if (GetEquippedGlider()?.Id != item.Id)
            return Reject(request, ActorFailureReason.RejectedAction, $"glider {itemTemplateId} equip did not take effect");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("board", itemTemplateId, item.Id.ToString()), request.TraceId);
        return Complete(request, true, $"boarded glider {itemTemplateId} (instance {item.Id})");
    }

    public ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.UnboardVehicle, vehicleObjId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "unboard vehicle"))
            return request;

        var slaveManager = Character.ParentWorld?.SlaveManager;

        // 1. Slave path — the CSDiscardSlavePacket call.
        AttachPointKind attachPoint = AttachPointKind.None;
        var slave = slaveManager?.GetIsMounted(Character.ObjId, out attachPoint) ?? null;
        if (slave != null)
        {
            if (vehicleObjId != 0 && slave.ObjId != vehicleObjId)
                return Reject(request, ActorFailureReason.StateTransition,
                    $"mounted on slave {slave.ObjId}, not {vehicleObjId}");

            request.Start($"unboarding slave {slave.ObjId} (tl {slave.TlId}, seat {attachPoint})");

            // 2. Real engine path — the exact UnbindSlave the
            //    CSDiscardSlavePacket handler drives.
            slaveManager!.UnbindSlave(Character, slave.TlId, AttachUnitReason.SlaveBinding);

            // 3. Post-state verification: the engine must have detached the rider.
            if (slaveManager.GetIsMounted(Character.ObjId, out _) != null)
                return Reject(request, ActorFailureReason.RejectedAction, "unboard of slave did not take effect");

            _ledger.RecordEffect(ActorIdempotency.EffectKey("unboard", slave.ObjId), request.TraceId);
            return Complete(request, true, $"unboarded slave {slave.ObjId}");
        }

        // 2. Transfer seat — the CSUnbondDoodadPacket path (Seat.UnLoadPassenger
        //    + Bonding clear + transform detach + SCUnbondDoodadPacket).
        if (Character.Bonding != null)
        {
            var bonding = Character.Bonding;
            var seat = bonding.GetOwner();
            if (vehicleObjId != 0 && seat.ParentObjId != vehicleObjId)
                return Reject(request, ActorFailureReason.StateTransition,
                    $"seated on doodad {seat.ObjId}, not transfer {vehicleObjId}");

            request.Start($"unboarding seat doodad {seat.ObjId} (transfer {seat.ParentObjId})");

            seat.Seat.UnLoadPassenger(Character, seat.ObjId); // free the place
            bonding.SetOwner(null);
            Character.Bonding = null;
            Character.Transform.Parent = null;
            Character.BroadcastPacket(new SCUnbondDoodadPacket(Character.ObjId, Character.Id, seat.ObjId), true);

            if (Character.Bonding != null)
                return Reject(request, ActorFailureReason.RejectedAction, "unboard of transfer seat did not take effect");

            _ledger.RecordEffect(ActorIdempotency.EffectKey("unboard", seat.ParentObjId), request.TraceId);
            return Complete(request, true, $"unboarded transfer seat {seat.ObjId}");
        }

        // 3. Glider path — Inventory.TakeoffBackpack (unequips the Backpack slot).
        var glider = GetEquippedGlider();
        if (glider != null)
        {
            if (vehicleObjId != 0 && glider.TemplateId != vehicleObjId)
                return Reject(request, ActorFailureReason.StateTransition,
                    $"glider {glider.TemplateId} equipped, not {vehicleObjId}");

            request.Start($"unboarding glider {glider.TemplateId} (instance {glider.Id})");

            if (!Character.Inventory!.TakeoffBackpack(ItemTaskType.SwapItems, true))
                return Reject(request, ActorFailureReason.RejectedAction, "glider takeoff refused by engine");
            if (GetEquippedGlider() != null)
                return Reject(request, ActorFailureReason.RejectedAction, "glider takeoff did not take effect");

            _ledger.RecordEffect(ActorIdempotency.EffectKey("unboard", glider.TemplateId), request.TraceId);
            return Complete(request, true, $"unboarded glider {glider.TemplateId}");
        }

        // 4. Not boarded → StateTransition (nothing to unboard). A retry
        //    after a successful unboard is refused here — the state can
        //    never flip back by re-running the request.
        return Reject(request, ActorFailureReason.StateTransition, "not boarded on any vehicle");
    }

    /// <summary>The glider equipped in the Backpack slot, or null.</summary>
    private Backpack? GetEquippedGlider()
    {
        var item = Character.Inventory?.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        return item is Backpack { Template: BackpackTemplate { BackpackType: BackpackType.Glider } } glider ? glider : null;
    }

    #endregion

    #region M5.1 trade actions (Buy/Sell — real engine trade paths)

    /// <summary>Merchant shop interaction range (the packet's 3m NPC-shop check).</summary>
    public const float MaxShopRange = 3f;

    /// <summary>Auction listing fee cap (the engine's MaxListingFee — 100g).</summary>
    public const int MaxListingFee = 1_000_000;

    public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Buy, merchantNpcObjId,
            payload: new BuyParams(itemTemplateId, count), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "buy"))
            return request;

        // 1. The target must be a live merchant NPC with a goods pack (the
        //    packet's npc.Template.Merchant + MerchantPackId gate).
        var npc = Character.ParentWorld?.GetNpc(merchantNpcObjId);
        if (npc == null || npc.Template == null || !npc.Template.Merchant || npc.Template.MerchantPackId == 0)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"merchant {merchantNpcObjId} not found or not a merchant");

        // 2. Shop range — the packet's 3m check (SendErrorMessage(TooFarAway)
        //    in the packet becomes a Rejected here).
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, npc.Transform.World.Position) > MaxShopRange)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"merchant {merchantNpcObjId} out of shop range");

        // 3. The merchant's pack must actually sell the requested template
        //    (packet: pack == null || !pack.SellsItem(itemId) → skip).
        var pack = NpcManager.Instance.GetGoods(npc.Template.MerchantPackId);
        if (pack == null || !pack.SellsItem(itemTemplateId))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"merchant {merchantNpcObjId} does not sell item {itemTemplateId}");

        // 4. Count must be positive and the template must exist (the packet
        //    dereferences template.Price; the actor fails closed instead).
        if (count <= 0)
            return Reject(request, ActorFailureReason.RejectedAction, "count must be positive");
        var template = ItemManager.Instance.GetTemplate(itemTemplateId);
        if (template == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"unknown item template {itemTemplateId}");

        // 5. Money gate (money pool only; honor/vocation currency is out of
        //    the v1 surface). The packet's check is buggy (uses && instead
        //    of ||); the actor performs the correct pre-flight so the engine
        //    is never entered without funds.
        var money = (long)template.Price * count;
        if (money > Character.Money)
            return Reject(request, ActorFailureReason.RejectedAction, $"not enough money ({money} needed)");
        if (money > int.MaxValue)
            return Reject(request, ActorFailureReason.RejectedAction, "purchase total exceeds currency range");

        request.Start($"buying {count} x item {itemTemplateId} from merchant {merchantNpcObjId} (pack {npc.Template.MerchantPackId})");

        // 6. Real engine path — the packet's exact calls: grant the item,
        //    then charge the money. Both are ordinary inventory/currency
        //    services; no direct DB or GM path.
        if (!Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.StoreBuy, itemTemplateId, count, -1))
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} grant refused by engine");
        if (!Character.ChangeMoney(SlotType.Inventory, -(int)money))
            return Reject(request, ActorFailureReason.RejectedAction, $"currency transfer refused by engine ({money})");

        // 7. Effect fingerprint for the M8 audit (retry correlation: the
        //    request-key dedupe is the primary retry guard; the fingerprint
        //    proves the purchase landed).
        _ledger.RecordEffect(ActorIdempotency.EffectKey("tradebuy", itemTemplateId, $"{merchantNpcObjId}:{count}"), request.TraceId);
        return Complete(request, money, $"bought {count} x item {itemTemplateId} for {money}");
    }

    public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Sell, merchantNpcObjId,
            payload: new SellParams(itemId), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "sell"))
            return request;

        // 1. The target must be a live merchant NPC (packet: npc.Template.Merchant).
        var npc = Character.ParentWorld?.GetNpc(merchantNpcObjId);
        if (npc == null || npc.Template == null || !npc.Template.Merchant)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"merchant {merchantNpcObjId} not found or not a merchant");

        // 2. The item must exist in the actor's OWN inventory (bag or
        //    equipment — the same containers the packet reads slots from).
        var item = Character.Inventory.Bag.GetItemByItemId(itemId)
                   ?? Character.Inventory.Equipment.GetItemByItemId(itemId);
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemId} not found in inventory");

        // 3. The template must be sellable (packet: !item.Template.Sellable → skip).
        if (item.Template == null || !item.Template.Sellable)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemId} is not sellable");

        // 4. Refund formula — the packet's exact computation.
        var gradeTemplate = ItemManager.Instance.GetGradeTemplate(item.Grade);
        if (gradeTemplate == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemId} has unknown grade {item.Grade}");
        var refund = (int)(item.Template.Refund * gradeTemplate.RefundMultiplier / 100f) * item.Count;

        request.Start($"selling item {itemId} (template {item.TemplateId}, count {item.Count}) to merchant {merchantNpcObjId}");

        // 5. Real engine path — the packet's exact calls: move the item into
        //    BuyBackItems (which REMOVES it from the bag — the engine-true
        //    idempotency backstop: a retry after success finds no item),
        //    mark the DB row for deletion, then pay the refund.
        if (!Character.BuyBackItems.AddOrMoveExistingItem(ItemTaskType.StoreSell, item))
            return Reject(request, ActorFailureReason.RejectedAction, $"sell of item {itemId} refused by engine (buyback)");
        ItemManager.Instance.MarkItemForDbDeletion(item.Id);
        if (!Character.ChangeMoney(SlotType.Inventory, refund))
            return Reject(request, ActorFailureReason.RejectedAction, $"refund transfer refused by engine ({refund})");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("tradesell", item.TemplateId, itemId.ToString()), request.TraceId);
        return Complete(request, refund, $"sold item {itemId} for {refund}");
    }

    public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.AuctionPost, 0,
            payload: new AuctionPostParams(itemId, startPrice, buyoutPrice, duration), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "post auction"))
            return request;

        // 1. The item must be in the actor's OWN inventory — listing is a
        //    transfer of ownership, so a foreign item is Rejected pre-flight
        //    (the packet trusts the client; the actor fails closed).
        var item = Character.Inventory.Bag.GetItemByItemId(itemId)
                   ?? Character.Inventory.Equipment.GetItemByItemId(itemId);
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemId} not found in inventory");

        // 2. Price terms: non-negative, at least one positive, defined duration.
        if (startPrice < 0 || buyoutPrice < 0)
            return Reject(request, ActorFailureReason.RejectedAction, "prices must be non-negative");
        if (startPrice == 0 && buyoutPrice == 0)
            return Reject(request, ActorFailureReason.RejectedAction, "at least one price must be positive");
        if (!Enum.IsDefined(typeof(AuctionDuration), duration))
            return Reject(request, ActorFailureReason.RejectedAction, $"invalid auction duration {duration}");

        // 3. Listing fee pre-flight — the engine's own formula (buyout × 1% ×
        //    (duration+1), capped). The engine bails SILENTLY when the fee is
        //    unaffordable (CanNotPutupMoney + return); the actor converts that
        //    into a clean Rejected before entering the engine.
        var fee = (int)(buyoutPrice * 0.01 * ((int)duration + 1));
        if (fee > MaxListingFee)
            fee = MaxListingFee;
        if (fee > Character.Money)
            return Reject(request, ActorFailureReason.RejectedAction, $"not enough money for listing fee ({fee})");

        request.Start($"posting item {itemId} on auction (start {startPrice}, buyout {buyoutPrice}, {duration})");

        // 4. Real engine path — the exact call CSAuctionPostPacket makes
        //    (npcId/npcId2 are unused by the engine; the auction house is a
        //    global service). The engine moves the item into
        //    AuctionAttachments (the engine-true idempotency backstop: a
        //    retry after success finds no item), deducts the fee and
        //    registers the lot.
        AuctionManager.Instance.PostLotOnAuction(Character, 0, 0, itemId, startPrice, buyoutPrice, duration);

        // 5. Post-state verification: the lot must actually be registered.
        var lot = AuctionManager.Instance.AuctionLots.Values.FirstOrDefault(l =>
            l.Item?.Id == itemId && l.ClientId == Character.Id);
        if (lot == null)
            return Reject(request, ActorFailureReason.RejectedAction, "auction listing did not take effect");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("tradeauctionpost", 0, itemId.ToString()), request.TraceId);
        return Complete(request, lot.Id, $"listed item {itemId} on auction (lot {lot.Id})");
    }

    public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.AuctionBuy, 0,
            payload: new AuctionBuyParams(lotId, price), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "buy auction"))
            return request;

        // 1. The lot must exist (the packet's GetAuctionLotFromId gate).
        var lot = AuctionManager.Instance.AuctionLots.GetValueOrDefault(lotId);
        if (lot == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"auction lot {lotId} not found (already sold or expired)");

        // 2. Buy-now terms: the lot must carry a buyout and the offer must
        //    meet it (the packet's buy-now branch: bid.Money >= DirectMoney
        //    && DirectMoney != 0). Below-buyout offers are the BID branch —
        //    this surface is purchase, not bidding, so they are Rejected.
        if (lot.DirectMoney <= 0)
            return Reject(request, ActorFailureReason.RejectedAction, $"auction lot {lotId} has no buyout price");
        if (price < lot.DirectMoney)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"offer {price} below buyout {lot.DirectMoney} (purchase requires buy-now)");

        // 3. Money gate pre-flight — REQUIRED: the engine's buy-now branch
        //    calls player.SubtractMoney and IGNORES its return before
        //    removing the lot, so an unaffordable purchase would grant the
        //    item without payment. The actor refuses before the engine call.
        if (Character.Money < lot.DirectMoney)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"not enough money for buyout {lot.DirectMoney}");

        request.Start($"buying auction lot {lotId} (item {lot.Item?.TemplateId}) for {lot.DirectMoney}");

        // 4. Real engine path — the exact call CSBidAuctionPacket makes. The
        //    buy-now branch deducts the buyout and removes the lot; delivery
        //    of the item is the engine's own mail path.
        AuctionManager.Instance.BidOnAuctionLot(Character, 0, 0, lot, new AuctionBid
        {
            LotId = lotId,
            WorldId = (byte)(Character.Transform?.WorldId ?? 0),
            BidderId = Character.Id,
            BidderName = Character.Name,
            Money = price
        });

        // 5. Post-state verification: the engine must have removed the lot
        //    (buy-now is terminal — the engine-true idempotency backstop: a
        //    retry after success finds no lot and cannot double-buy).
        if (AuctionManager.Instance.AuctionLots.ContainsKey(lotId))
            return Reject(request, ActorFailureReason.RejectedAction, "auction purchase did not take effect");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("tradeauctionbuy", 0, lotId.ToString()), request.TraceId);
        return Complete(request, lot.DirectMoney, $"bought auction lot {lotId} for {lot.DirectMoney}");
    }

    public ActorRequest Plant(uint seedItemTemplateId, Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Plant, seedItemTemplateId,
            payload: new PlantParams(position, zRot, scale), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "plant"))
            return request;

        if (!position.IsFinite())
            return Reject(request, ActorFailureReason.RejectedAction, "plant position must be finite");

        // 1. The seed must be in the actor's own bag (normal inventory
        //    lookup — the same resolution the client's use-item path does).
        var inventory = Character.Inventory;
        if (inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        inventory.Bag.GetAllItemsByTemplate(seedItemTemplateId, -1, out var seedItems, out _);
        var seedItem = seedItems.FirstOrDefault();
        if (seedItem == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"seed item {seedItemTemplateId} not found in inventory");

        // 2. The item must be a plantable seed/young-tree: resolve the
        //    doodad template id from the canonical item_spawn_doodads
        //    mapping (the same data the client's placement UI reads) and
        //    require the doodad template to exist.
        var doodadId = ItemManager.Instance.GetDoodadIdFromItem(seedItemTemplateId);
        if (doodadId == 0)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"item {seedItemTemplateId} is not a plantable seed (no item_spawn_doodads row)");
        if (DoodadManager.Instance.GetTemplate(doodadId) == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"doodad template {doodadId} not found in game data");

        var world = Character.ParentWorld;
        if (world == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no world");

        // 3. Placement gates — mirror CSCreateDoodadPacket exactly.
        //    Labor cost comes from the seed item's use skill (packet line
        //    37-42); public-farm and owned-land placement zero it (packet
        //    lines 44-73).
        var laborCost = 0;
        var useSkill = SkillManager.Instance.GetSkillTemplate(seedItem.Template?.UseSkillId ?? 0);
        if (useSkill != null)
            laborCost = useSkill.ConsumeLaborPower;

        var farmType = PublicFarmManager.Instance.InPublicFarm(world.Template, position)
            ? PublicFarmManager.Instance.GetFarmType(world, position)
            : FarmType.Invalid;
        if (farmType != FarmType.Invalid)
        {
            if (!PublicFarmManager.Instance.CanPlace(Character, farmType, doodadId))
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"doodad {doodadId} not allowed on public farm {farmType}");
            laborCost = 0;
        }

        var house = HousingManager.Instance.GetHouseAtLocation(position.X, position.Y);
        if (house != null)
        {
            if (!house.AllowedToInteract(Character))
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"no permission to plant on house {house.Id}");
            laborCost = 0;
        }

        // Labor gate (packet line 76-86: insufficient labor refuses before
        // any consumption).
        if (Character.LaborPower < laborCost)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"insufficient labor ({Character.LaborPower} < {laborCost})");
        if (laborCost != 0)
            Character.ChangeLabor((short)-laborCost, 0);

        request.Start($"planting doodad {doodadId} from seed {seedItemTemplateId} at {position}");

        // 4. THE real engine path — the same CreatePlayerDoodad call the
        //    CSCreateDoodadPacket handler makes (line 89). The engine
        //    consumes the seed item (ItemUse + ConsumeItem per mapped item
        //    template), binds to the house when one is at the position,
        //    spawns the growing-crop doodad and persists it (Doodad.Save).
        //    No bot-side consumption, no direct DB access.
        Doodad doodad;
        try
        {
            doodad = DoodadManager.Instance.CreatePlayerDoodad(Character, doodadId,
                position.X, position.Y, position.Z, zRot, scale, seedItem.Id, farmType);
        }
        catch (MySql.Data.MySqlClient.MySqlException ex)
        {
            // Persistence-boundary failure: the engine landed the placement
            // in-memory (seed consumed, crop doodad spawned) but the
            // Doodad.Save() write failed. This is deliberately NOT a
            // Rejected — the B1 invariant is "Rejected ⇒ nothing applied",
            // and here the effect WAS applied and the outcome is ambiguous.
            // Interrupted locks the idempotency key (the same rule as
            // Interrupted/TimedOut after a timeout ambiguity), so a
            // same-key retry is refused pre-flight and one logical plant
            // can never consume its seed twice. The engine-true backstop
            // (seed gone from the bag) covers fresh-key retries.
            return Interrupt(request,
                $"planting doodad {doodadId} failed at the persistence boundary: {ex.Message}");
        }

        if (doodad == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"engine refused to plant doodad {doodadId}");

        // 5. Record the applied effect (crop doodad spawned from the seed)
        //    for M8 audit correlation. The request-level key dedupe is the
        //    PRIMARY retry guard; the engine-true seed consumption is the
        //    backstop (a new-key retry finds no seed).
        _ledger.RecordEffect(ActorIdempotency.EffectKey("plant", doodadId, seedItemTemplateId.ToString()), request.TraceId);
        return Complete(request, doodad.ObjId,
            $"planted doodad {doodad.ObjId} (template {doodadId}) from seed {seedItemTemplateId}");
    }

    public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.HouseBuild, designId,
            payload: new HouseBuildParams(position, zRot), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "build house"))
            return request;

        if (!position.IsFinite())
            return Reject(request, ActorFailureReason.RejectedAction, "build position must be finite");

        // 1. The design item must be in the actor's own bag (the same
        //    resolution the client's housing UI performs before it sends
        //    CSCreateHousePacket with the item's instance id).
        var inventory = Character.Inventory;
        if (inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        inventory.Bag.GetAllItemsByTemplate(designItemTemplateId, -1, out var designItems, out _);
        var designItem = designItems.FirstOrDefault();
        if (designItem == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"design item {designItemTemplateId} not found in inventory");

        // 2. The design id must resolve to a canonical housing template
        //    (HousingManager.Build's GetTemplate is silently
        //    null-tolerant — the validator would reject, but the actor
        //    refuses pre-flight with a taxonomy reason instead of an
        //    unobservable engine no-op).
        var houseTemplate = HousingGameData.Instance.GetTemplate(designId);
        if (houseTemplate == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"unknown house design {designId}");

        // 3. Connection gate — the real Build path is connection-mediated
        //    (the CSCreateHousePacket handler's connection; every engine
        //    refusal is an error packet on it). Headless characters
        //    without a network connection get Rejected (the same rule as
        //    Mount).
        var connection = Character.Connection;
        if (connection == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                "no game connection (the house-build path is connection-mediated)");

        // 4. Tax gate pre-flight — mirror the packet's tax branch exactly
        //    (HousingManager.Build): the engine checks affordability and
        //    refuses SILENTLY via an error packet, so the actor computes
        //    the same numbers through the engine's own
        //    CalculateBuildingTaxInfo and refuses with a taxonomy reason
        //    before the engine call. Nothing is consumed pre-flight.
        HousingManager.Instance.CalculateBuildingTaxInfo(Character.AccountId, houseTemplate, true,
            out var totalTaxAmountDue, out _, out _, out _, out _);
        if (FeaturesManager.Fsets?.Check(Models.Game.Features.Feature.taxItem) == true)
        {
            var userTaxCount = inventory.GetItemsCount(SlotType.Inventory, Item.TaxCertificate);
            var userBoundTaxCount = inventory.GetItemsCount(SlotType.Inventory, Item.BoundTaxCertificate);
            var totalCertsCost = (int)Math.Ceiling(totalTaxAmountDue / 10000f);
            if (totalCertsCost > userTaxCount + userBoundTaxCount)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"not enough tax certificates ({totalCertsCost} required)");
        }
        else if (totalTaxAmountDue > Character.Money)
        {
            return Reject(request, ActorFailureReason.RejectedAction,
                $"not enough money for the house tax ({totalTaxAmountDue} required)");
        }

        request.Start($"building house design {designId} (design item {designItem.Id}) at {position}");

        // 5. THE real engine path — the same HousingManager.Build call the
        //    CSCreateHousePacket handler makes. The engine enforces the
        //    canonical placement rules (land zone / faction / category /
        //    houseless-only / overlap via HousingPlacementValidator, then
        //    the polygon layer), charges the tax, consumes the design
        //    item, creates the house in construction state and registers
        //    it. No bot-side placement, no direct DB, no Transform/ZoneId
        //    shortcut.
        var housesBefore = HousingManager.Instance.GetAllHouses();
        try
        {
            HousingManager.Instance.Build(connection, designId,
                position.X, position.Y, position.Z, zRot,
                designItem.Id, 0, 0, false);
        }
        catch (Exception ex)
        {
            // Execution began and the engine threw. The placement may or
            // may not have been applied — the outcome is ambiguous, so
            // Interrupted locks the idempotency key (the same rule as
            // Plant's persistence boundary; a same-key retry is refused
            // pre-flight and the design item is never consumed twice).
            return Interrupt(request,
                $"building house {designId} failed inside the engine: {ex.Message}");
        }

        // 6. Post-state verification: the engine signals refusals via
        //    error packets (invisible headless), so the applied-effect
        //    proof is a NEW house registered under the actor (absent from
        //    the pre-call snapshot). No new house → the engine rejected
        //    the placement at one of its gates.
        var newHouse = HousingManager.Instance.GetAllHouses()
            .FirstOrDefault(h => h.OwnerId == Character.Id && housesBefore.All(b => b.Id != h.Id));
        if (newHouse == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                "engine refused the house placement (zone/category/overlap/ownership/tax gate)");

        // 7. Record the applied effect (house registered from the design)
        //    for M8 audit correlation. The request-level key dedupe is the
        //    PRIMARY retry guard; the engine-true design-item consumption
        //    is the backstop (a new-key retry finds no design item).
        _ledger.RecordEffect(ActorIdempotency.EffectKey("housebuild", designId, designItemTemplateId.ToString()), request.TraceId);
        return Complete(request, newHouse.Id,
            $"built house {newHouse.Id} (design {designId}) at {position} — construction step {newHouse.CurrentStep}");
    }

    #endregion

    #region M5.1 economy actions (Deposit/Withdraw — real engine paths)

    public ActorRequest DepositMoney(long amount, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.DepositMoney, 0, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "deposit money"))
            return request;

        if (amount <= 0)
            return Reject(request, ActorFailureReason.RejectedAction, "amount must be positive");
        // The engine's money-transfer path is int32 (the packets read
        // Int32); balances are long but a single transfer is capped.
        if (amount > int.MaxValue)
            return Reject(request, ActorFailureReason.RejectedAction, "amount exceeds the engine transfer limit (int32)");

        // Currency-credit idempotency marker (ROADMAP M5): when a prior
        // attempt already applied the deposit (recorded AFTER the engine
        // move) and the inventory balance can no longer cover the amount,
        // the deposit is already done — refuse pre-flight so the audit
        // record shows no Running transition (AcceptQuest pattern).
        if (_ledger.IsEffectApplied(ActorIdempotency.EffectKey("currency", 0, $"deposit:{amount}"))
            && Character.Money < amount)
            return Reject(request, ActorFailureReason.StateTransition,
                $"deposit of {amount} copper already applied (duplicate refused pre-flight)");

        // Balance mirror pre-flight (the engine's own check — the same
        // refusal the packet path produces, surfaced as a clean Rejected
        // before any engine call; the engine call below remains the
        // authoritative backstop, e.g. after ledger eviction).
        if (Character.Money < amount)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"deposit of {amount} copper refused by engine (insufficient balance)");

        request.Start($"depositing {amount} copper into bank");

        // REAL engine path — the exact call CSDepositMoneyPacket makes.
        // The engine validates the inventory balance (SendErrorMessage +
        // false when insufficient); the balance is the engine-true backstop
        // for fresh-key retries after a timeout ambiguity.
        if (!Character.ChangeMoney(SlotType.Inventory, SlotType.Bank, (int)amount))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"deposit of {amount} copper refused by engine (insufficient balance)");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("currency", 0, $"deposit:{amount}"), request.TraceId);
        return Complete(request, amount, $"deposited {amount} copper into bank");
    }

    public ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.WithdrawMoney, 0, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "withdraw money"))
            return request;

        if (amount <= 0)
            return Reject(request, ActorFailureReason.RejectedAction, "amount must be positive");
        // The engine's money-transfer path is int32 (the packets read
        // Int32); balances are long but a single transfer is capped.
        if (amount > int.MaxValue)
            return Reject(request, ActorFailureReason.RejectedAction, "amount exceeds the engine transfer limit (int32)");

        if (_ledger.IsEffectApplied(ActorIdempotency.EffectKey("currency", 0, $"withdraw:{amount}"))
            && Character.Money2 < amount)
            return Reject(request, ActorFailureReason.StateTransition,
                $"withdrawal of {amount} copper already applied (duplicate refused pre-flight)");

        // Balance mirror pre-flight (the engine's own check — see DepositMoney).
        if (Character.Money2 < amount)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"withdrawal of {amount} copper refused by engine (insufficient bank balance)");

        request.Start($"withdrawing {amount} copper from bank");

        // REAL engine path — the exact call CSWithdrawMoneyPacket makes.
        if (!Character.ChangeMoney(SlotType.Bank, SlotType.Inventory, (int)amount))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"withdrawal of {amount} copper refused by engine (insufficient bank balance)");

        _ledger.RecordEffect(ActorIdempotency.EffectKey("currency", 0, $"withdraw:{amount}"), request.TraceId);
        return Complete(request, amount, $"withdrew {amount} copper from bank");
    }

    public ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.DepositItem, itemTemplateId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "deposit item"))
            return request;

        var inventory = Character.Inventory;
        if (inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        // Resolve the stack through NORMAL inventory services (the same
        // lookup the client's move path performs) — first bag stack of the
        // template.
        inventory.Bag.GetAllItemsByTemplate(itemTemplateId, -1, out var items, out _);
        var item = items.FirstOrDefault();
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} not found in bag");

        // Item-credit idempotency marker: when a prior attempt already
        // moved this exact item instance and the bag no longer holds it,
        // the deposit is already done — refuse pre-flight. (The conjunctive
        // check keeps a legitimately re-acquired item — withdrawn and
        // re-deposited later — retryable.)
        if (_ledger.IsEffectApplied(ActorIdempotency.EffectKey("deposit", itemTemplateId, item.Id.ToString()))
            && inventory.Bag.GetItemByItemId(item.Id) == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"item {itemTemplateId} (instance {item.Id}) already deposited (duplicate refused pre-flight)");

        // Target slot: a same-template stack with room (the engine's
        // doMerge branch), else the first empty bank slot (doMoveAllToEmpty).
        var targetSlot = FindContainerTargetSlot(inventory.Warehouse, item);
        if (targetSlot < 0)
            return Reject(request, ActorFailureReason.RejectedAction, "bank is full");

        request.Start($"depositing item {itemTemplateId} (instance {item.Id}, count {item.Count}) into bank");

        // REAL engine path — the exact call CSSwapItemsPacket makes for an
        // Inventory→Bank move: Inventory.SplitOrMoveItem (whole stack).
        // The engine validates the source item, slot, container acceptance
        // and target capacity; a refusal happens BEFORE any item moves.
        if (!inventory.SplitOrMoveItem(ItemTaskType.SwapItems, item.Id, SlotType.Inventory, (byte)item.Slot,
                0, SlotType.Bank, (byte)targetSlot))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"deposit of item {itemTemplateId} refused by engine");

        var moved = item.Count;
        _ledger.RecordEffect(ActorIdempotency.EffectKey("deposit", itemTemplateId, item.Id.ToString()), request.TraceId);
        return Complete(request, moved, $"deposited {moved} of item {itemTemplateId} into bank");
    }

    public ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.WithdrawItem, itemTemplateId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "withdraw item"))
            return request;

        var inventory = Character.Inventory;
        if (inventory == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character has no inventory");

        inventory.Warehouse.GetAllItemsByTemplate(itemTemplateId, -1, out var items, out _);
        var item = items.FirstOrDefault();
        if (item == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"item {itemTemplateId} not found in bank");

        if (_ledger.IsEffectApplied(ActorIdempotency.EffectKey("withdraw", itemTemplateId, item.Id.ToString()))
            && inventory.Warehouse.GetItemByItemId(item.Id) == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"item {itemTemplateId} (instance {item.Id}) already withdrawn (duplicate refused pre-flight)");

        var targetSlot = FindContainerTargetSlot(inventory.Bag, item);
        if (targetSlot < 0)
            return Reject(request, ActorFailureReason.RejectedAction, "inventory is full");

        request.Start($"withdrawing item {itemTemplateId} (instance {item.Id}, count {item.Count}) from bank");

        // REAL engine path — the exact call CSSwapItemsPacket makes for a
        // Bank→Inventory move.
        if (!inventory.SplitOrMoveItem(ItemTaskType.SwapItems, item.Id, SlotType.Bank, (byte)item.Slot,
                0, SlotType.Inventory, (byte)targetSlot))
            return Reject(request, ActorFailureReason.RejectedAction,
                $"withdrawal of item {itemTemplateId} refused by engine");

        var moved = item.Count;
        _ledger.RecordEffect(ActorIdempotency.EffectKey("withdraw", itemTemplateId, item.Id.ToString()), request.TraceId);
        return Complete(request, moved, $"withdrew {moved} of item {itemTemplateId} from bank");
    }

    /// <summary>
    /// Target slot for a container move: a same-template stack with room
    /// (the engine's doMerge branch — the client's stack-merge behavior),
    /// else the first empty slot (doMoveAllToEmpty). -1 when the target
    /// container is full.
    /// </summary>
    private static int FindContainerTargetSlot(ItemContainer container, Item item)
    {
        if (item.Template.MaxCount > 1)
        {
            var existing = container.Items.FirstOrDefault(i =>
                i.TemplateId == item.TemplateId && i.Count < item.Template.MaxCount);
            if (existing != null)
                return existing.Slot;
        }

        return container.GetUnusedSlot(-1);
    }

    #endregion

    #region M5.1 harvest action (real engine path)

    /// <summary>
    /// Loot func types that mark a crop phase as harvestable: the phase's
    /// DoodadFuncUse advances into one of these, meaning the interaction
    /// yields items. The canonical 1.2 crop loop (potato) advances mature →
    /// looting (DoodadFuncLootPack); the actor resolves the harvest skill
    /// from whichever loot-producing chain the current phase leads to.
    /// </summary>
    private static readonly string[] LootFuncTypes =
    [
        "DoodadFuncLootPack",
        "DoodadFuncLootItem",
        "DoodadFuncHarvest",
        "DoodadFuncCropHarvest",
        "DoodadFuncFruitPick",
        "DoodadFuncCerealHarvest"
    ];

    public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Harvest, doodadObjId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "harvest"))
            return request;

        var doodad = Character.ParentWorld?.GetDoodad(doodadObjId);
        if (doodad == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"doodad {doodadObjId} not found in world");
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, doodad.Transform.World.Position, false) > MaxInteractRange)
            return Reject(request, ActorFailureReason.RejectedAction, $"doodad {doodadObjId} out of interaction range");
        if (doodad.Despawn > DateTime.MinValue)
            return Reject(request, ActorFailureReason.RejectedAction, $"doodad {doodadObjId} scheduled for despawn");

        // Data-driven harvestability: the crop's CURRENT phase must carry a
        // DoodadFuncUse whose skill advances into a loot phase — that func's
        // skill IS the harvest interaction. Immature phases (seedling/small)
        // carry watering/uproot funcs whose next phases have no loot func, so
        // they resolve to no harvest skill → StateTransition (not harvestable
        // in this phase). An already-harvested crop is deleted by the final
        // phase, so the world lookup above already failed for it.
        if (!TryResolveHarvestSkill(doodad, out var harvestSkillId))
            return Reject(request, ActorFailureReason.StateTransition,
                $"doodad {doodadObjId} not harvestable in phase {doodad.FuncGroupId} (no loot-linked interaction func)");

        var phaseBefore = doodad.FuncGroupId;
        var yieldBefore = InventoryUnitCount();
        request.Start($"harvesting doodad {doodadObjId} (phase {phaseBefore}, skill {harvestSkillId})");

        // The REAL engine path: the same doodad.Use(caster, skill) chain the
        // client's harvest interaction drives. Inside this single call the
        // phase machine runs the whole crop loop synchronously (proven by
        // CropHarvestLoopTests): mature → looting (DoodadFuncLootPack grants
        // the pack through the ordinary inventory grant path) → final →
        // doodad deleted (plot reset). No bot-only resource creation; labor
        // consumption, if any, happens inside the engine's own skill path.
        doodad.Use(Character, harvestSkillId);

        // Post-state verification: the crop must be gone (final phase deletes
        // it) or at least advanced. An unchanged phase means the engine
        // refused the interaction (permissions/conditions) — surface that as
        // a Rejected instead of a silent no-op.
        var after = Character.ParentWorld?.GetDoodad(doodadObjId);
        if (after != null && after.FuncGroupId == phaseBefore)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"doodad {doodadObjId} harvest refused by engine (phase {phaseBefore} unchanged)");

        var yieldDelta = InventoryUnitCount() - yieldBefore;
        _ledger.RecordEffect(ActorIdempotency.EffectKey("harvest", doodadObjId), request.TraceId);
        return Complete(request, yieldDelta,
            $"harvested doodad {doodadObjId} (phase {phaseBefore} → {(after == null ? "deleted" : after.FuncGroupId.ToString())}, yield {yieldDelta} unit(s))");
    }

    /// <summary>
    /// Resolves the harvest interaction skill for a crop doodad from its
    /// CURRENT phase funcs (data-driven, canonical 1.2 shape): the phase's
    /// DoodadFuncUse whose skill advances into a loot phase. The canonical
    /// potato chain resolves 4457 (mature) → func 5887 → skill 13980 (작물
    /// 수확) → 4458 (looting, DoodadFuncLootPack 129). False when the phase
    /// has no loot-linked interaction (immature/terminal states).
    /// </summary>
    private static bool TryResolveHarvestSkill(Doodad doodad, out uint harvestSkillId)
    {
        harvestSkillId = 0;
        var funcs = DoodadManager.Instance.GetFuncsForGroup(doodad.FuncGroupId);
        foreach (var func in funcs)
        {
            if (func.FuncType != "DoodadFuncUse" || func.SkillId == 0)
                continue;
            if (func.NextPhase <= 0)
                continue;

            var nextFuncs = DoodadManager.Instance.GetFuncsForGroup((uint)func.NextPhase);
            if (nextFuncs.Any(f => LootFuncTypes.Contains(f.FuncType)))
            {
                harvestSkillId = func.SkillId;
                return true;
            }
        }

        return false;
    }

    /// <summary>Total item units in the actor's inventory bag (yield measurement).</summary>
    private int InventoryUnitCount()
        => Character.Inventory?.Bag.Items.Sum(i => i.Count) ?? 0;

    #endregion

    #region M5.1 craft action (real engine path)

    /// <summary>Default craft budget (one engine step: cast + queue drain).</summary>
    public static readonly TimeSpan DefaultCraftTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Mirror of CharacterCraft.DefaultCraftRange for skills that define no max range.</summary>
    private const float DefaultCraftRange = 5f;

    /// <summary>
    /// Bag-state snapshot taken right before the engine craft step starts:
    /// material counts (the success signal — EndCraft consumes materials
    /// BEFORE granting any product) and product counts (the granted delta).
    /// Null while no craft request awaits its queue drain.
    /// </summary>
    private Dictionary<uint, int>? _craftMaterialSnapshot;
    private Dictionary<uint, int>? _craftProductSnapshot;

    public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Craft, craftId,
            payload: new CraftParams(doodadObjId), timeout: timeout ?? DefaultCraftTimeout, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "craft"))
            return request;

        // Validation gate 1: the recipe must exist — the same manager the
        // CSExecuteCraft packet handler resolves through.
        var craft = CraftManager.Instance.GetCraftById(craftId);
        if (craft == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"unknown craft {craftId}");

        // Validation gate 2: the ENGINE craft queue must be idle — the
        // CSExecuteCraft guard. A re-entry while a queue is active would
        // overwrite CurrentCraft/Count/DoodadId mid-step, so it is refused
        // here pre-flight (StateTransition — the queue belongs to the
        // engine, not to this actor).
        var craftSurface = Character.Craft;
        if (craftSurface == null)
            return Reject(request, ActorFailureReason.RejectedAction, "character craft surface not initialized");
        if (craftSurface.IsCraftQueueActive)
            return Reject(request, ActorFailureReason.StateTransition, "craft queue already active");

        // Validation gate 3: the recipe's skill template must exist.
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(craft.SkillId);
        if (skillTemplate == null)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"craft {craftId} references missing skill {craft.SkillId}");

        // Validation gate 4: materials must be in the BAG — the engine's
        // scope rule (bank/equipment materials are not consumable for
        // crafting).
        var hasMaterials = craft.CraftMaterials.Count == 0 || craft.CraftMaterials.All(m =>
            Character.Inventory.GetItemsCount(SlotType.Inventory, m.ItemId) >= m.Amount);
        if (!hasMaterials)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"craft {craftId} materials not present in bag");

        // Validation gate 5: workbench integrity when the recipe's skill
        // targets doodads — exists, correct template (ReqDoodadId), in
        // range. Mirrors the CharacterCraft.Craft gates so the refusal is a
        // clean Rejected instead of an engine error-message no-op.
        if (skillTemplate.TargetType == SkillTargetType.Doodad)
        {
            var doodad = Character.ParentWorld?.GetDoodad(doodadObjId);
            if (doodad == null)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"craft bench {doodadObjId} not found in world");
            if (craft.ReqDoodadId > 0 && doodad.TemplateId != craft.ReqDoodadId)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"craft {craftId} requires bench template {craft.ReqDoodadId} (found {doodad.TemplateId})");
            var maxRange = skillTemplate.MaxRange > 0 ? skillTemplate.MaxRange : DefaultCraftRange;
            if (MathUtil.CalculateDistance(Character.Transform.World.Position, doodad.Transform.World.Position, false) > maxRange)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"craft bench {doodadObjId} out of range");
        }

        // Validation gate 6: labor — the engine's own EndCraft gate (same
        // adjusted cost formula), pre-flighted so a step that could never
        // complete is refused before the engine queue starts (EndCraft's
        // labor refusal would otherwise burn the queue slot with a
        // "fictitious" step).
        var laborCost = new Skill(skillTemplate).GetLaborCost(Character);
        if (Character.LaborPower < laborCost)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"craft {craftId} requires {laborCost} labor (has {Character.LaborPower})");

        // Validation gate 7: the trade-pack level gate — the engine's own
        // Craft() check (canonical 1.2: packs require level 10 to craft).
        if (craft.ResultsInBackpack && Character.Level < AppConfiguration.Instance.Specialty.MinLevelToCraftSell)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"craft {craftId} requires level {AppConfiguration.Instance.Specialty.MinLevelToCraftSell} (trade-pack recipe)");

        // Bag-state snapshot taken right before the engine craft step
        // starts: material counts (the success signal — EndCraft consumes
        // materials BEFORE granting any product) and product counts (the
        // granted delta). The outcome check can then distinguish a
        // completed step (materials consumed) from an engine-side refusal
        // (nothing consumed).
        _craftMaterialSnapshot = craft.CraftMaterials.ToDictionary(m => m.ItemId,
            m => GetCraftItemCount(m.ItemId));
        _craftProductSnapshot = craft.CraftProducts.GroupBy(p => p.ItemId)
            .ToDictionary(g => g.Key, g => GetCraftItemCount(g.Key));

        request.Start($"crafting {craftId} (bench {doodadObjId}, skill {craft.SkillId})");

        // The REAL engine entry: the exact call CSExecuteCraft makes
        // (count=1 — one engine step). The queue runs through the normal
        // skill pipeline (CraftTask → cast → CraftEffect.Apply → EndCraft);
        // Tick() observes the queue drain and completes the request.
        Character.Craft.Craft(craft, 1, doodadObjId);
        return request;
    }

    /// <summary>
    /// Engine-true count of one item template on the character: the bag
    /// (the engine's scope rule — materials must be in the bag) PLUS the
    /// Backpack equipment slot, because EndCraft grants trade packs into
    /// Equipment.Backpack, not the bag. A pack-only delta would otherwise
    /// read as 0 and the grant row would vanish from the CraftResult.
    /// </summary>
    private int GetCraftItemCount(uint itemId)
    {
        var count = Character.Inventory.GetItemsCount(SlotType.Inventory, itemId);
        var pack = Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        if (pack != null && pack.TemplateId == itemId)
            count += pack.Count;
        return count;
    }

    private void ClearCraftSnapshot()
    {
        _craftMaterialSnapshot = null;
        _craftProductSnapshot = null;
    }

    #endregion

    #region Tick / movement

    private Vector3? _moveTarget;
    private float _moveSpeed;
    private Vector3? _driveTarget;
    private float _driveSpeed;
    private BaseUnit? _driveVehicle;
    private ulong? _pendingPutDownPackId;

    public void Tick(TimeSpan elapsed)
    {
        if (_active is not { IsTerminal: false } request)
            return;

        request.AddElapsed(elapsed);

        // Timeout enforcement on EVERY action that carries a budget — not
        // just movement. The §17 reason maps per action kind (Move/Drive →
        // Navigation; everything else → Starvation, budget exhaustion).
        if (request.Timeout is { } budget && request.Elapsed > budget)
        {
            // A Move that exhausts its budget halts mid-leg — observers
            // must see the standstill (dossier §1.6).
            if (request.Action is ActorActionType.Move && _moveTarget != null)
                BroadcastStop();
            Finish(request, request.Expire(ActorTimeoutPolicy.ReasonFor(request.Action),
                request.Action is ActorActionType.Move or ActorActionType.Drive ? "navigation budget exceeded" : "action budget exceeded"));
            ClearMovementState();
            ClearCraftSnapshot();
            return;
        }

        // Craft completion: the request is Running while the engine craft
        // queue is active. When the queue drains, the engine step finished
        // (EndCraft ran — products granted + materials consumed, or the
        // engine refused mid-step). Outcome is read from the bag snapshot
        // taken before the step.
        if (request.Action == ActorActionType.Craft && _craftMaterialSnapshot != null)
        {
            var craft = CraftManager.Instance.GetCraftById(request.TargetId);
            if (craft == null)
            {
                ClearCraftSnapshot();
                Finish(request, request.Reject(ActorFailureReason.RejectedAction,
                    $"craft {request.TargetId} vanished from manager"));
                return;
            }

            if (Character.Craft?.IsCraftQueueActive == true)
                return; // engine step still running — keep waiting

            // Queue drained. Success signal: every material row was
            // consumed (EndCraft consumes BEFORE granting, so consumption
            // proves the step executed). A rate-failed product row still
            // counts as a completed step (canonical behavior).
            var consumedAll = craft.CraftMaterials.All(m =>
                _craftMaterialSnapshot.GetValueOrDefault(m.ItemId, 0)
                - GetCraftItemCount(m.ItemId) >= m.Amount);

            if (!consumedAll)
            {
                ClearCraftSnapshot();
                Finish(request, request.Reject(ActorFailureReason.RejectedAction,
                    $"craft {request.TargetId} step refused by engine (materials not consumed)"));
                return;
            }

            var granted = _craftProductSnapshot
                .Where(kv => GetCraftItemCount(kv.Key) > kv.Value)
                .Select(kv => new CraftProductGrant(kv.Key,
                    GetCraftItemCount(kv.Key) - kv.Value))
                .ToList();
            // Record the applied-effect fingerprint (B1 idempotency layer):
            // correlation for the M8 economic audit — the trace that
            // crafted this recipe. The request-level key dedupe is the
            // PRIMARY retry guard; the consumed materials are the
            // engine-true backstop (a fresh-key retry finds nothing left).
            _ledger.RecordEffect(ActorIdempotency.EffectKey("craft", request.TargetId), request.TraceId);
            ClearCraftSnapshot();
            Finish(request, request.Complete(new CraftResult(request.TargetId, granted),
                $"craft {request.TargetId} step completed (materials consumed, {granted.Count} product row(s) granted)"));
            return;
        }

        // Put-down completion: the live pack put-down skills are plot-only
        // (the engine dispatches the plot via Task.Run and returns Success
        // immediately; the effect lands ~1.8 s later). The request stays
        // Running while Tick polls the Backpack slot; when the async effect
        // moves the pack into the System container, the placement is done.
        if (request.Action == ActorActionType.PutDown && _pendingPutDownPackId is { } pendingPackId)
        {
            var slotPack = Character.Inventory?.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (slotPack == null || slotPack.Id != pendingPackId)
            {
                _pendingPutDownPackId = null;
                _ledger.RecordEffect(ActorIdempotency.EffectKey("packputdown", request.TargetId, pendingPackId.ToString()), request.TraceId);
                Finish(request, request.Complete(true,
                    detail: $"trade pack {request.TargetId} placed (instance {pendingPackId}, async plot effect applied)"));
            }
            return;
        }

        if (request.Action == ActorActionType.Move && _moveTarget is { } destination)
        {
            var position = Character.Transform.World.Position;
            var flatDistance = MathUtil.CalculateDistance(position, destination, false);
            var zDistance = Math.Abs(destination.Z - position.Z);

            if (flatDistance <= ArrivalRadius && zDistance <= ArrivalRadius)
            {
                // Leg ended — observers must see the halt (dossier §1.6).
                BroadcastStop();
                Finish(request, request.Complete(detail: "arrived"));
                _moveTarget = null;
                return;
            }

            var step = Math.Min(_moveSpeed * (float)Math.Max(elapsed.TotalSeconds, 0.05), flatDistance);
            if (flatDistance > 0.0001f)
            {
                var angle = (float)MathUtil.CalculateAngleFrom(position, destination).DegToRad();
                var (newX, newY) = MathUtil.AddDistanceToFront(step, position.X, position.Y, angle);
                var fraction = step / flatDistance;
                var newZ = position.Z + (destination.Z - position.Z) * fraction;
                ApplyCharacterMove(new Vector3(newX, newY, newZ));
            }
            else
            {
                var dir = destination.Z >= position.Z ? 1f : -1f;
                var zStep = Math.Min(step, zDistance);
                ApplyCharacterMove(new Vector3(position.X, position.Y, position.Z + dir * zStep));
            }
            return;
        }

        if (request.Action == ActorActionType.Drive && _driveVehicle is { } vehicle && _driveTarget is { } driveDestination)
        {
            var position = vehicle.Transform.World.Position;
            var flatDistance = MathUtil.CalculateDistance(position, driveDestination, false);
            var zDistance = Math.Abs(driveDestination.Z - position.Z);

            if (flatDistance <= ArrivalRadius && zDistance <= ArrivalRadius)
            {
                Finish(request, request.Complete(detail: "arrived"));
                ClearDriveState();
                return;
            }

            var step = Math.Min(_driveSpeed * (float)Math.Max(elapsed.TotalSeconds, 0.05), flatDistance);
            Vector3 next;
            if (flatDistance > 0.0001f)
            {
                var angle = (float)MathUtil.CalculateAngleFrom(position, driveDestination).DegToRad();
                var (newX, newY) = MathUtil.AddDistanceToFront(step, position.X, position.Y, angle);
                var fraction = step / flatDistance;
                next = new Vector3(newX, newY, position.Z + (driveDestination.Z - position.Z) * fraction);
            }
            else
            {
                var dir = driveDestination.Z >= position.Z ? 1f : -1f;
                next = new Vector3(position.X, position.Y, position.Z + dir * Math.Min(step, zDistance));
            }

            // Player-equivalent drive: every leg is applied through the
            // client-authored vehicle movement model (the CSMoveUnitPacket
            // engine path) — position set + SCOneUnitMovementPacket
            // broadcast + FinalizeTransform. The vehicle Transform is never
            // assigned directly here.
            ApplyVehicleMove(vehicle, next);
        }
    }

    /// <summary>
    /// Applies one walk leg through the client-authored unit-movement model
    /// — the exact engine path CSMoveUnitPacket's UnitMoveType case executes
    /// for the character itself (position apply + SCOneUnitMovementPacket
    /// broadcast + transform finalize). Replaces the v1 silent Transform
    /// write (the bare SetPosition, no broadcast): observers see real
    /// movement broadcasts, and the character rides the same movement-model
    /// family as DriveVehicle (REQ-M5.3-3).
    /// </summary>
    private void ApplyCharacterMove(Vector3 next)
    {
        var from = Character.Transform.World.Position;
        var angle = (float)MathUtil.CalculateAngleFrom(from, next).DegToRad();
        VehicleMovementModel.ApplyUnitMove(Character, Character,
            VehicleMovementModel.BuildCharacterMove(next, angle, _moveSpeed));
    }

    /// <summary>
    /// Emits the canonical Stopping broadcast at the character's current
    /// position (dossier §1.6 — Blink/TeleportToUnit shape): zero velocity
    /// + Stopping flag, so observers' clients snap the character to a
    /// standstill whenever a Move leg ends for any reason (arrival, budget
    /// expiry, Stop). Without it an observer who saw the walk keeps the
    /// character walking forever (the M6 "frozen bot" bug class).
    /// </summary>
    private void BroadcastStop()
    {
        var position = Character.Transform.World.Position;
        var rotZ = Character.Transform.Local.ToRollPitchYawSBytesMovement().Item3;
        Character.BroadcastPacket(
            new SCOneUnitMovementPacket(Character.ObjId,
                VehicleMovementModel.BuildStopMove(position, rotZ)), true);
    }

    /// <summary>
    /// Applies one drive leg through the shared client-authored movement
    /// model. Slave ground vehicles move via VehicleMoveType (rotation
    /// shorts + velocity), Mates via UnitMoveType — the exact payloads a
    /// client driver/rider would send, applied by the SAME engine path
    /// CSMoveUnitPacket executes.
    /// </summary>
    private void ApplyVehicleMove(BaseUnit vehicle, Vector3 next)
    {
        var from = vehicle.Transform.World.Position;
        var angle = (float)MathUtil.CalculateAngleFrom(from, next);
        switch (vehicle)
        {
            case Slave slave:
                VehicleMovementModel.ApplySlaveMove(Character, slave,
                    VehicleMovementModel.BuildVehicleMove(next, (angle - 90).DegToRad(), _driveSpeed));
                break;
            case Mate mate:
                VehicleMovementModel.ApplyUnitMove(Character, mate,
                    VehicleMovementModel.BuildUnitMove(next, angle.DegToRad(), _driveSpeed));
                break;
        }
    }

    private void ClearMovementState()
    {
        _moveTarget = null;
        _pendingPutDownPackId = null;
        ClearDriveState();
    }

    private void ClearDriveState()
    {
        _driveTarget = null;
        _driveVehicle = null;
    }

    #endregion

    #region Internals

    private ActorRequest NewRequest(ActorActionType action, uint targetId,
        Vector3? destination = null, uint skillId = 0, TimeSpan? timeout = null, object? payload = null,
        string? idempotencyKey = null)
        => new(action, targetId, destination, skillId, timeout, payload, idempotencyKey);

    /// <summary>
    /// Single-writer gate: accepts the request as the new active one, or
    /// rejects it with StateTransition("busy") when another request is live.
    /// The busy request still walks the full lifecycle (Requested → Accepted
    /// → Rejected) so its audit record shows the complete transition log.
    /// </summary>
    private bool TryBegin(ActorRequest request, string what)
    {
        if (_active is { IsTerminal: false })
        {
            request.Accept(what);
            Reject(request, ActorFailureReason.StateTransition, $"actor busy with {_active.Action}");
            return false;
        }

        request.Accept(what);

        // Idempotency gate: an explicit key whose prior attempt ended in a
        // state that may have executed (Completed/Interrupted/TimedOut) is
        // NEVER re-executed — the duplicate is rejected pre-flight and its
        // audit record shows no Running transition (the roadmap's "retries
        // and timeouts cannot duplicate" guarantee). Rejected attempts are
        // retryable (v1 rejections all occur before engine execution). The
        // refusal is flagged so it cannot replace the locked outcome.
        if (!string.IsNullOrEmpty(request.IdempotencyKey)
            && _ledger.TryGetOutcome(request.IdempotencyKey, out var prior)
            && prior.Result != ActorLifecycleState.Rejected)
        {
            request.IsDedupeRejection = true;
            Reject(request, ActorFailureReason.StateTransition,
                $"duplicate idempotency key '{request.IdempotencyKey}' — original trace {prior.TraceId} " +
                $"already {prior.Result}; retry is not re-executed");
            return false;
        }

        _active = request;
        return true;
    }

    private void InterruptActive(string detail)
    {
        if (_active is { IsTerminal: false } request)
        {
            Finish(request, request.Interrupt(detail));
        }
        ClearMovementState();
        // An interrupted craft request stops watching the engine queue; the
        // queue itself is engine truth and keeps running (the step either
        // lands or not — a fresh-key retry is protected by the consumed
        // materials / active-queue gates).
        ClearCraftSnapshot();
    }

    private ActorRequest Complete(ActorRequest request, string detail)
        => Complete(request, null, detail);

    private ActorRequest Complete(ActorRequest request, object? result, string detail)
    {
        Finish(request, request.Complete(result, detail));
        return request;
    }

    private ActorRequest Reject(ActorRequest request, ActorFailureReason reason, string detail)
    {
        Finish(request, request.Reject(reason, detail));
        return request;
    }

    /// <summary>
    /// Terminal <see cref="ActorLifecycleState.Interrupted"/> for a request
    /// whose execution STARTED but could not confirm its outcome (engine
    /// persistence-boundary failure, controller interrupt). Interrupted
    /// locks the idempotency key — a same-key retry is refused pre-flight
    /// because the effect may have been applied.
    /// </summary>
    private ActorRequest Interrupt(ActorRequest request, string detail)
    {
        Finish(request, request.Interrupt(detail));
        return request;
    }

    private void Finish(ActorRequest request, bool transitioned)
    {
        if (!transitioned || request == null || !request.IsTerminal)
            return;
        // A dedupe refusal is not an attempt of its own: it must not
        // replace the original (possibly locked) outcome under the key.
        if (!request.IsDedupeRejection)
            _ledger.TryRecordOutcome(request.IdempotencyKey, request.TraceId, request.State, request.Failure);
        _trace.Add(new ActorAuditRecord(
            request.TraceId, ActorId, request.Action, request.TargetId,
            request.RequestedAtUtc, request.StartedAtUtc, request.CompletedAtUtc,
            request.State, request.Failure, request.Detail, request.StateChanges.ToList()));
        if (_trace.Count > MaxTraceRecords)
            _trace.RemoveRange(0, _trace.Count - MaxTraceRecords);
        if (ReferenceEquals(_active, request))
            _active = null;
    }

    public ActorAuditRecord? FindByKey(string idempotencyKey)
        => _ledger.TryGetOutcome(idempotencyKey, out var entry)
            ? _trace.LastOrDefault(r => r.TraceId == entry.TraceId)
            : null;

    private Unit? ResolveUnit(uint objId)
    {
        if (objId == 0)
            return null;
        // Real engine lookup: the owning world instance's unit registry
        // (WorldInstance._units — populated by AddObject for NPCs/characters).
        return Character.ParentWorld?.GetUnit(objId);
    }

    #endregion
}

/// <summary>Vector3.IsFinite extension (System.Numerics has no built-in).</summary>
internal static class VectorMath
{
    public static bool IsFinite(this Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>
/// §17 reason mapping for action timeouts. Every action supports a timeout
/// budget; the taxonomy reason is per action kind: movement timeouts are
/// Navigation failures, every other action that exceeds its budget is
/// Starvation (resource/budget exhaustion). No new reasons — only spec §17
/// vocabulary.
/// </summary>
public static class ActorTimeoutPolicy
{
    public static ActorFailureReason ReasonFor(ActorActionType action)
        => action is ActorActionType.Move or ActorActionType.Drive ? ActorFailureReason.Navigation : ActorFailureReason.Starvation;
}
