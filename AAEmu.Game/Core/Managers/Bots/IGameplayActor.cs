using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Action vocabulary of the gameplay actor contract (M5 v1 — slice #8 of the
/// PlayerBot scale review, ARCHITECTURE_REVIEW deliverable 10).
///
/// v2 surface (B1, ROADMAP §M5): Observe · Move · Stop · Target · Cast ·
/// Interact · Loot · UseItem · Mount/Dismount · AcceptQuest · TurnInQuest.
/// The M5.1 economic extension (Plant/Harvest/Craft/PackPickup/BoardVehicle/
/// Buy-Sell/Deposit-Withdraw) lands in a later slice; the lifecycle,
/// rejection taxonomy and audit machinery below are final.
///
/// Contract rules (ROADMAP M5, spec §16-17):
///  - Actions are VALIDATED gameplay requests. Every request tracks the
///    lifecycle Requested → Accepted → Running → Completed | Rejected(reason)
///    | Interrupted(reason) | TimedOut.
///  - Rejection reasons use the spec §17 taxonomy (never "bot got stuck"):
///    WrongDecision / Navigation / RejectedAction / StateTransition /
///    Persistence / Starvation / FidelityError.
///  - Every action emits a structured audit record
///    {trace_id, actor_id, action, target_id, requested_at, started_at,
///    completed_at, result, state_changes} — the M8 economic audit backbone.
///  - At most ONE request runs per actor (single-writer rule). A second
///    request while Running is Rejected(StateTransition, "busy") — never
///    queued in v1 (enqueueing lands with the M6.1 scheduler wiring).
///  - Retry dedupe: an explicit idempotencyKey marks a request as a retry;
///    a key whose prior attempt may have executed (Completed/Interrupted/
///    TimedOut) is never re-executed — the duplicate is
///    Rejected(StateTransition) pre-flight (ROADMAP M5 idempotency rule).
///    Every action accepts a timeout budget; expiry maps to spec §17
///    (Move → Navigation, otherwise Starvation).
///  - Execution invokes normal gameplay services only. No direct DB writes,
///    no bot-only resource creation, no packet fabrication (spec §8:
///    Observe is a direct server-state query).
/// </summary>
public interface IGameplayActor
{
    /// <summary>The embodied character's object id (audit actor_id).</summary>
    uint ActorId { get; }

    /// <summary>The ordinary Character record this actor drives.</summary>
    Character Character { get; }

    /// <summary>The currently active request, or null when idle.</summary>
    ActorRequest? ActiveRequest { get; }

    /// <summary>Structured trace of every request (newest last, bounded).</summary>
    IReadOnlyList<ActorAuditRecord> AuditTrace { get; }

    /// <summary>
    /// Observation snapshot — direct server-state query (region lists,
    /// WorldManager, character state). NO packets (spec §8).
    /// Emits an audit record (Observe, Completed).
    /// </summary>
    ActorObservation Observe();

    /// <summary>
    /// Requests a bounded walk to an absolute world position. Advances per
    /// Tick() through the ordinary Transform (the same facility
    /// Simulation.MoveTo / the pilot use). Completes on arrival
    /// (ArrivalRadius 0.5f); TimedOut(Navigation) when the budget expires.
    /// </summary>
    ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null);

    /// <summary>Move to a unit's current position (resolved at request time).</summary>
    ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null);

    /// <summary>
    /// Stops the actor. Interrupts the running request (Interrupted,
    /// detail "stop requested") and completes itself. No-op when idle.
    /// </summary>
    ActorRequest Stop();

    /// <summary>
    /// Sets the actor's current target through the real engine path
    /// (Unit.CurrentTarget). Validates the objId resolves to a world unit;
    /// unknown targets are Rejected(RejectedAction).
    /// </summary>
    ActorRequest SetTarget(uint targetObjId);

    /// <summary>
    /// Casts a skill through the real engine path (Character.UseSkill —
    /// the same call CSStartSkillPacket's learned-skill branch makes).
    /// Validates: skill template exists, character knows the skill, target
    /// resolves. Engine refusal maps to Rejected(RejectedAction).
    /// </summary>
    ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null);

    /// <summary>
    /// Interacts with a doodad through the real engine path (Doodad.Use —
    /// the same call the interaction skills / Interactions make). skillId 0
    /// executes the skill-less loot-func branch (LootItem/LootPack/
    /// Cutdowning); a nonzero interaction skill executes the skill branch.
    /// Validates: doodad resolves, skill template exists (when given), and
    /// the doodad is not scheduled for despawn (the engine's own #1443
    /// guard). Doodads advance their phase machine inside Use; a retry
    /// against the new phase is a fresh interaction, never a re-run.
    /// </summary>
    ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null);

    /// <summary>
    /// Loots a corpse/bag owner through the real engine path
    /// (LootingContainer.OpenBag with lootAll — the exact call
    /// CSLootOpenBagPacket makes). The engine removes each granted entry
    /// (TryReserveLootItem), so a retry after a successful loot finds an
    /// empty container and grants nothing — retries cannot duplicate loot.
    /// </summary>
    ActorRequest Loot(uint lootOwnerObjId, string? idempotencyKey = null);

    /// <summary>
    /// Uses an inventory item through the real engine path — the exact
    /// CSStartSkillPacket SkillItem branch: skill.Use with a SkillItem
    /// caster (reagent validation + consumption + OnItemUse quest events).
    /// Validates: item present in inventory, item has a use skill, skill
    /// template exists, target resolves (0 = self). Item consumption by the
    /// engine makes retries land on Rejected("no item …") instead of a
    /// second use.
    /// </summary>
    ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null);

    /// <summary>
    /// Mounts a mate through the real engine path (MateManager.MountMate —
    /// the CSMountMatePacket call). Requires the character's real
    /// GameConnection (the packet path resolves the rider from
    /// connection.ActiveChar); headless pilots without a network connection
    /// get Rejected(RejectedAction). Already-mounted is
    /// Rejected(StateTransition) — the engine is never re-entered, so a
    /// retry cannot double-mount.
    /// </summary>
    ActorRequest Mount(uint mateObjId, string? idempotencyKey = null);

    /// <summary>
    /// Dismounts through the real engine path (MateManager.UnMountMate —
    /// the CSUnMountMatePacket call). mateObjId 0 = whatever the actor is
    /// currently riding. Not-mounted is Rejected(StateTransition) — retries
    /// cannot double-dismount.
    /// </summary>
    ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null);

    /// <summary>
    /// Picks up a placed trade pack through the real engine path
    /// (RecoverItem.Execute — the exact call CSLootOpenBagPacket makes for
    /// pack-style pickup with the generic world recover skill 11361).
    /// Validates: the doodad resolves, is a recoverable pack doodad (its
    /// current phase carries a DoodadFuncRecoverItem with the generic
    /// recover skill), is in interaction range, and the actor's backpack
    /// slot can accept the pack (a carried pack blocks pickup with
    /// Rejected(StateTransition)). The engine grants the pack back into
    /// the Backpack equipment slot and deletes the doodad; the post-state
    /// container transition is the completion proof (the engine signals
    /// refusal only via error packets). Retries cannot duplicate packs:
    /// after a success the doodad is gone, and the System-container check
    /// inside DoodadFuncRecoverItem refuses a re-grant.
    /// </summary>
    ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null);

    /// <summary>
    /// Puts down the carried trade pack through the real engine path — the
    /// pack item's use skill (item_template.use_skill_id) via the exact
    /// CSStartSkillPacket SkillItem branch: Skill.Use with a SkillItem
    /// caster, whose PutDownBackpackEffect moves the pack from the
    /// Backpack equipment slot into the System container and spawns the
    /// placed-pack doodad. Validates: pack carried in the backpack slot,
    /// is an auto-equip trade pack, put-down skill exists. Engine refusals
    /// (public-farm exclusion, house permission, invalid item) leave the
    /// pack in the slot and are detected by post-state verification —
    /// Rejected(RejectedAction) with the placement proof absent. The move
    /// to the System container is the retry-proof state: a retry finds no
    /// pack in the slot and is refused pre-flight, so the pack can never
    /// be placed twice.
    /// </summary>
    ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null);

    /// <summary>
    /// Cancels a running request by trace id. Returns false when no request
    /// with that id is active (idempotent — retries cannot double-interrupt).
    /// </summary>
    bool Interrupt(Guid traceId);

    /// <summary>
    /// Requests quest acceptance through the REAL engine gate
    /// (CharacterQuests.AddQuest — level, race, quest-completion chains,
    /// repeatable/daily re-accept checks all evaluate). Acceptance executes
    /// synchronously: Completed(accepted=true) on success,
    /// Rejected(RejectedAction) with the gate detail when the engine
    /// refuses. Payload: <see cref="QuestAcceptParams"/>.
    /// </summary>
    ActorRequest AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null);

    /// <summary>
    /// Advances the quest step machine ONE stage (the same RunCurrentStep
    /// evaluation the engine performs after world events). Completed when
    /// the step ran; Rejected(StateTransition, "quest not active") when the
    /// quest is not in ActiveQuests (terminal quests included).
    /// </summary>
    ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null);

    /// <summary>
    /// Turn-in at an NPC — the exact path CSCompleteQuestContextPacket
    /// takes (QuestManager.DoReportEvents), followed by the same single
    /// step-machine advance the world pipeline performs. The npcObjId must
    /// resolve to a live NPC in the owning world; an unresolvable objId is
    /// Rejected(RejectedAction). Payload: <see cref="QuestTurnInParams"/>.
    /// </summary>
    ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null);

    /// <summary>
    /// Turn-in at a doodad (DoReportEvents doodad branch). Payload:
    /// <see cref="QuestTurnInParams"/>.
    /// </summary>
    ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null);

    /// <summary>
    /// Auto-complete turn-in (DoReportEvents third branch — no world
    /// target required). Payload: <see cref="QuestTurnInParams"/>.
    /// </summary>
    ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null);

    /// Idempotency correlation lookup: the audit record of the terminal
    /// attempt recorded under an explicit idempotency key, or null when the
    /// key was never used (or its record was evicted). Lets a controller
    /// correlate a retry/timeout back to the original outcome instead of
    /// re-executing.
    /// </summary>
    ActorAuditRecord? FindByKey(string idempotencyKey);

    /// <summary>
    /// Advances the active request one step (movement legs, timeout
    /// accounting). Safe to call with no active request. Driven by the
    /// scheduler worker (IBotStepExecutor seam) or a test loop.
    /// </summary>
    void Tick(TimeSpan elapsed);
}

/// <summary>M5 v1 action vocabulary.</summary>
public enum ActorActionType : byte
{
    Observe = 0,
    Move = 1,
    Stop = 2,
    Target = 3,
    Cast = 4,

    /// <summary>Quest acceptance through the real AddQuest gate.</summary>
    AcceptQuest = 5,

    /// <summary>One step-machine advance on an active quest.</summary>
    AdvanceQuest = 6,

    /// <summary>Turn-in at an NPC (real packet path).</summary>
    TurnInQuest = 7,

    /// <summary>Turn-in at a doodad (real packet path).</summary>
    TurnInDoodad = 8,

    /// <summary>Auto-complete turn-in (real packet path third branch).</summary>
    AutoTurnIn = 9,

    /// <summary>Doodad interaction through Doodad.Use (real engine path).</summary>
    Interact = 10,

    /// <summary>Loot a corpse/bag through LootingContainer.OpenBag (real engine path).</summary>
    Loot = 11,

    /// <summary>Item use through the CSStartSkillPacket SkillItem branch.</summary>
    UseItem = 12,

    /// <summary>Mate mounting through MateManager.MountMate (CSMountMatePacket path).</summary>
    Mount = 13,

    /// <summary>Mate dismounting through MateManager.UnMountMate (CSUnMountMatePacket path).</summary>
    Dismount = 14,

    /// <summary>Trade-pack pickup through RecoverItem (CSLootOpenBagPacket pack path).</summary>
    PackPickup = 15,

    /// <summary>Trade-pack put-down through the pack's use skill (CSStartSkillPacket SkillItem branch).</summary>
    PutDown = 16
}

/// <summary>Lifecycle of a single actor request.</summary>
public enum ActorLifecycleState : byte
{
    Requested = 0,
    Accepted = 1,
    Running = 2,
    Completed = 3,
    Rejected = 4,
    Interrupted = 5,
    TimedOut = 6
}

/// <summary>
/// Spec §17 failure taxonomy — the ONLY rejection vocabulary. A bot failure
/// must always resolve to one of these; "bot got stuck" is never a reason.
/// </summary>
public enum ActorFailureReason : byte
{
    None = 0,

    /// <summary>Controller chose an action invalid for the situation.</summary>
    WrongDecision = 1,

    /// <summary>Movement/navigation failure (unreachable leg, nav timeout).</summary>
    Navigation = 2,

    /// <summary>A gameplay service refused the action (unknown skill/target, gate, engine refusal).</summary>
    RejectedAction = 3,

    /// <summary>Illegal lifecycle transition (e.g. busy, dead, wrong state).</summary>
    StateTransition = 4,

    /// <summary>A persistence write/flush failed.</summary>
    Persistence = 5,

    /// <summary>Resource/budget exhaustion (tick budget, queue backlog).</summary>
    Starvation = 6,

    /// <summary>Fidelity policy violation (e.g. combat while not allowed).</summary>
    FidelityError = 7
}
