using System.Linq;
using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
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
///  - All execution goes through normal gameplay services: movement applies
///    the ordinary Transform (same facility Simulation.MoveTo / the M2b
///    pilot use), targeting sets Unit.CurrentTarget, casting calls
///    Character.UseSkill (the exact learned-skill branch CSStartSkillPacket
///    uses). Observe reads the region graph + character state — no packets.
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
            return Complete(request, "already at destination");

        _moveTarget = destination;
        _moveSpeed = speed;
        request.Start("walking");
        return request;
    }

    public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
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
        var request = NewRequest(ActorActionType.Stop, 0);
        if (!request.Accept("stop"))
            return request; // defensive; should never happen

        // Interrupt whatever is running (if anything), then complete the stop.
        if (_active is { IsTerminal: false })
            InterruptActive("stop requested");
        request.Start("interrupting");
        Finish(request, request.Complete(detail: "stopped"));
        return request;
    }

    public ActorRequest SetTarget(uint targetObjId)
    {
        var request = NewRequest(ActorActionType.Target, targetObjId);
        if (!TryBegin(request, "target"))
            return request;

        var unit = ResolveUnit(targetObjId);
        if (unit == null)
            return Reject(request, ActorFailureReason.RejectedAction, "target not found in world");

        Character.CurrentTarget = unit;
        return Complete(request, $"targeting {unit.ObjId}");
    }

    public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
    {
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

        // 4. Record the applied-effect fingerprint (B1 idempotency layer):
        //    correlation for the M8 economic audit. The request-level key
        //    dedupe is the PRIMARY retry guard — a same-key retry is
        //    rejected pre-flight, so the item can never be consumed twice
        //    by a retry; the item charge count is the engine-true backstop.
        _ledger.RecordEffect(ActorIdempotency.EffectKey("itemuse", itemTemplateId, item.Id.ToString()), request.TraceId);
        return Complete(request, result, $"item {itemTemplateId} used (skill {itemTemplate.UseSkillId})");
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

        // 4. Post-state verification: PutDownBackpackEffect early-returns
        //    (public-farm exclusion, house permission, invalid item)
        //    WITHOUT failing the skill — the pack must have LEFT the
        //    Backpack slot (moved to the System container). That move is
        //    also the retry-proof state: a retry finds no pack in the slot
        //    and is refused pre-flight, so the pack can never be placed
        //    twice.
        if (Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == pack.Id)
            return Reject(request, ActorFailureReason.RejectedAction,
                $"trade pack {packItemTemplateId} put-down did not take effect (engine refused placement)");

        // Applied-effect fingerprint (M8 economic-audit correlation): the
        // pack instance transitioned to a placed pack.
        _ledger.RecordEffect(ActorIdempotency.EffectKey("packputdown", packItemTemplateId, pack.Id.ToString()), request.TraceId);
        return Complete(request, true,
            $"trade pack {packItemTemplateId} placed (instance {pack.Id}, now in System container)");
    }

    #endregion

    #region Tick / movement

    private Vector3? _moveTarget;
    private float _moveSpeed;

    public void Tick(TimeSpan elapsed)
    {
        if (_active is not { IsTerminal: false } request)
            return;

        request.AddElapsed(elapsed);

        // Timeout enforcement on EVERY action that carries a budget — not
        // just movement. The §17 reason maps per action kind (Move →
        // Navigation; everything else → Starvation, budget exhaustion).
        if (request.Timeout is { } budget && request.Elapsed > budget)
        {
            Finish(request, request.Expire(ActorTimeoutPolicy.ReasonFor(request.Action),
                request.Action == ActorActionType.Move ? "navigation budget exceeded" : "action budget exceeded"));
            _moveTarget = null;
            return;
        }

        if (request.Action == ActorActionType.Move && _moveTarget is { } destination)
        {
            var position = Character.Transform.World.Position;
            var flatDistance = MathUtil.CalculateDistance(position, destination, false);
            var zDistance = Math.Abs(destination.Z - position.Z);

            if (flatDistance <= ArrivalRadius && zDistance <= ArrivalRadius)
            {
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
                ApplyPosition(new Vector3(newX, newY, newZ));
            }
            else
            {
                var dir = destination.Z >= position.Z ? 1f : -1f;
                var zStep = Math.Min(step, zDistance);
                ApplyPosition(new Vector3(position.X, position.Y, position.Z + dir * zStep));
            }
        }
    }

    private void ApplyPosition(Vector3 next)
    {
        var transform = Character.Transform;
        var angle = (float)MathUtil.CalculateAngleFrom(transform.World.Position, next);
        transform.Local.SetRotationDegree(0f, 0f, angle - 90);
        transform.Local.SetPosition(next);
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
        _moveTarget = null;
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
        => action == ActorActionType.Move ? ActorFailureReason.Navigation : ActorFailureReason.Starvation;
}
