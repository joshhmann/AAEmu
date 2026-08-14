using System.Numerics;

using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Action vocabulary of the gameplay actor contract (M5 v1 — slice #8 of the
/// PlayerBot scale review, ARCHITECTURE_REVIEW deliverable 10).
///
/// v2 surface (B1, ROADMAP §M5): Observe · Move · Stop · Target · Cast ·
/// Interact · Loot · UseItem · Mount/Dismount · AcceptQuest · TurnInQuest.
/// The M5.1 economic extension has landed: Plant/Harvest, PackPickup/PutDown,
/// Buy/Sell, Deposit/Withdraw, and BoardVehicle/UnboardVehicle — the
/// vehicle/transfer manager surface (slaves, route-carriage seats, glider
/// equip). Craft lands in a later slice; the lifecycle, rejection taxonomy
/// and audit machinery below are final.
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
    /// Boards a vehicle through the REAL engine path — the vehicle/transfer
    /// manager surface (NOT the mate path covered by B1 Mount/Dismount).
    /// Resolves the target to one of the engine's vehicle families:
    ///  - Slave (ships, farm wagons, tanks, machines — SlaveManager
    ///    registry): SlaveManager.BindSlave — the exact call
    ///    CSBindSlavePacket (driver) and DoodadFuncAttachment's ship branch
    ///    (passenger) make. attachPoint selects the seat.
    ///  - Transfer (route carriage — TransferManager registry): the seat
    ///    doodad bond path (DoodadFuncAttachment: Seat.LoadPassenger +
    ///    BondDoodad + transform parenting + SCBondDoodadPacket) — the
    ///    same interaction a passenger boarding a route carriage performs.
    ///  - Glider item template (BackpackType.Glider in inventory): equips
    ///    the glider into the Backpack slot through the ordinary inventory
    ///    path (SplitOrMoveItem) — the real 1.2 "board a glider" step
    ///    (deploy/fly is the item's use skill, a separate action).
    /// Already-boarded is Rejected(StateTransition) — the engine is never
    /// re-entered, so a retry cannot double-board.
    /// </summary>
    ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null);

    /// <summary>
    /// Unboards from a vehicle through the real engine path:
    ///  - Slave → SlaveManager.UnbindSlave (the CSDiscardSlavePacket call)
    ///  - Transfer seat → Seat.UnLoadPassenger + Bonding clear (the
    ///    CSUnbondDoodadPacket path)
    ///  - Glider → Inventory.TakeoffBackpack (unequips the Backpack slot)
    /// Not-boarded is Rejected(StateTransition) — retries cannot
    /// double-unboard.
    /// </summary>
    ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null);

    /// <summary>
    /// Harvests a mature crop through the real engine path — the same
    /// doodad.Use(caster, harvestSkill) chain the client's harvest
    /// interaction drives (mature phase → DoodadFuncUse → looting phase →
    /// DoodadFuncLootPack yield → final phase → doodad deleted). The harvest
    /// skill is resolved DATA-DRIVEN from the doodad's current phase funcs:
    /// the phase's DoodadFuncUse whose skill leads into a loot phase is the
    /// harvest interaction (canonical potato: mature 4457 carries func 5887 /
    /// skill 13980 → looting 4458 carries DoodadFuncLootPack 129). No crop
    /// ids are hardcoded in the actor.
    ///
    /// Validation gates: doodad resolves (Rejected(RejectedAction)), in range
    /// (Rejected(RejectedAction)), not scheduled for despawn (the engine's own
    /// #1443 guard), and the current phase is harvestable — a crop that is not
    /// mature (seedling/small phases carry only watering/uproot funcs, no loot
    /// link) is Rejected(StateTransition). After the engine call the doodad is
    /// verified gone-or-advanced; an unchanged phase means the engine refused
    /// (permissions/conditions) → Rejected(RejectedAction).
    ///
    /// Idempotency: the engine deletes the crop on the final phase, so a
    /// fresh-key retry after success resolves no doodad and grants nothing;
    /// same-key retries are rejected pre-flight by the ActorEffectLedger. The
    /// yield effect is also recorded on the ledger (harvest:&lt;doodadObjId&gt;)
    /// after it lands for correlation. Retries/timeouts cannot double-yield.
    /// </summary>
    ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null);

    /// <summary>
    /// Crafts ONE engine step through the real engine path
    /// (CharacterCraft.Craft — the exact call CSExecuteCraft makes, with
    /// count=1). Validates: recipe exists in CraftManager, engine craft
    /// queue idle (the CSExecuteCraft guard — a re-entry would overwrite
    /// the queue mid-step), recipe skill template exists, materials present
    /// in the bag (the engine's scope rule), workbench exists/template
    /// matches/in range (when the skill targets doodads), labor, and the
    /// trade-pack level gate. The request stays Running while the engine
    /// craft queue is active and completes when the queue drains (the
    /// normal skill pipeline's CraftEffect → EndCraft ran): Completed with
    /// a <see cref="CraftResult"/> when materials were consumed, Rejected
    /// when the engine refused the step mid-flight. Retries cannot
    /// duplicate items/labor: a same-key retry is refused pre-flight by the
    /// effect ledger, and a fresh-key retry after a completed step finds
    /// the materials consumed (engine-true backstop). Payload:
    /// <see cref="CraftParams"/>.
    /// </summary>
    ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null);

    /// <summary>
    /// Drives a boarded vehicle (Slave ground vehicle or Mate mount) to an
    /// absolute world position through the client-authored vehicle movement
    /// model — the SAME engine path a client driver's CSMoveUnitPacket
    /// executes (driver attach + position apply + SCOneUnitMovementPacket
    /// broadcast + FinalizeTransform via VehicleMovementModel). The vehicle
    /// Transform is NEVER assigned by the actor: every leg is applied through
    /// the shared model, so observers see real movement broadcasts and
    /// passengers/packs follow the vehicle.
    ///
    /// Preconditions (pre-flight, engine never re-entered without them):
    ///  - the objId resolves to a Slave or Mate in the actor's world —
    ///    otherwise Rejected(RejectedAction, "vehicle not found"),
    ///  - the actor occupies the DRIVER seat (Slave.AttachedCharacters[Driver]
    ///    / Mate.Passengers[Driver]) — otherwise
    ///    Rejected(StateTransition, "not in driver seat"),
    ///  - speed positive and destination finite.
    ///
    /// Completes on arrival (ArrivalRadius 0.5f); TimedOut(Navigation) when
    /// the budget expires. Composes with BoardVehicle (driver seat) and
    /// LoadPackOntoVehicle (cargo) for the Phase 2 farm → craft → pack →
    /// drive → unload → sell route.
    /// </summary>
    ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null);

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
    /// Loads a trade pack onto a vehicle's cargo point through the REAL
    /// gameplay path (PackVehicleService → SlaveManager attach seam) with
    /// retail snap-to-cargo-point behavior: the pack doodad parents to the
    /// slave's transform and its local position/rotation are taken from the
    /// model's attach-point data. Two pack sources:
    ///  - carried pack (placedPackDoodadObjId == null): the pack in the
    ///    Backpack equipment slot moves into the System container and a new
    ///    pack doodad spawns attached to the slave;
    ///  - placed pack (placedPackDoodadObjId set): the standing pack doodad
    ///    re-parents to the slave and snaps onto the free cargo point.
    /// Validates: the vehicle resolves and is alive, is in interaction
    /// range, is a cargo vehicle (pack-storage-box bindings on its slave
    /// template — canonical 1.2), and has a free cargo point (not occupied
    /// by another pack). Rejections: unknown/dead vehicle, out of range,
    /// not a cargo vehicle, cargo full, no carried pack, not a trade pack,
    /// placed pack not found / out of range / not recoverable → RejectedAction;
    /// already-attached placed pack or duplicate idempotency key →
    /// StateTransition. Engine-true idempotency: after a carried load the
    /// Backpack slot is empty (retry finds no pack); after a placed load the
    /// doodad is attached (retry is refused StateTransition); a full vehicle
    /// refuses further packs. Payload: <see cref="LoadPackOntoVehicleParams"/>.
    /// </summary>
    ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null);

    /// <summary>
    /// Plants a seed/young-tree item at a world position through the REAL
    /// engine path — the same DoodadManager.CreatePlayerDoodad call the
    /// CSCreateDoodadPacket handler makes. The actor resolves the doodad
    /// template id from the seed item (item_spawn_doodads), mirrors the
    /// packet's placement gates (use-skill labor cost, public-farm
    /// CanPlace, owned-land AllowedToInteract), charges labor, and the
    /// engine consumes the seed and spawns the growing-crop doodad.
    /// Rejections: missing seed → RejectedAction; item not plantable
    /// (no doodad mapping) → RejectedAction; unknown doodad template →
    /// RejectedAction; public-farm refusal → RejectedAction; no
    /// permission on owned land → RejectedAction; insufficient labor →
    /// RejectedAction. A MySQL write failure inside the engine's
    /// persistence tail (Doodad.Save) INTERRUPTS the request (the
    /// placement landed in-memory but the outcome is unconfirmed) —
    /// Interrupted locks the idempotency key so a same-key retry is
    /// refused pre-flight and the seed is never consumed twice. The seed
    /// is consumed inside the engine call, so a fresh-key retry after
    /// success finds no seed — retries cannot double-plant. Payload:
    /// <see cref="PlantParams"/>.
    /// </summary>
    ActorRequest Plant(uint seedItemTemplateId, Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null);

    /// <summary>
    /// Starts building a house design at a world position through the REAL
    /// engine path — the same HousingManager.Build call the
    /// CSCreateHousePacket handler makes. The actor resolves the design
    /// item INSTANCE from its own bag by template (the client holds the
    /// item and sends its instance id), mirrors the packet's tax gate
    /// (CalculateBuildingTaxInfo + gold/certificate affordability via the
    /// engine's own computation), and the engine enforces the canonical
    /// placement rules (land zone / faction / category / houseless-only /
    /// overlap, then the polygon layer), charges tax, consumes the design
    /// item, creates the house in construction state (CurrentStep 0 for
    /// multi-step designs) and registers it. Rejections: design item not
    /// in inventory → RejectedAction; unknown design → RejectedAction;
    /// no game connection (the real path is connection-mediated) →
    /// RejectedAction; insufficient money/certificates for the tax →
    /// RejectedAction; engine refusal (zone/category/overlap/ownership/
    /// tax gate — silent error packets) → RejectedAction detected by
    /// post-state verification. A thrown engine exception after Start
    /// INTERRUPTS the request (execution began, outcome ambiguous) and
    /// locks the idempotency key. Idempotency: the design item is
    /// consumed inside the engine call, so a fresh-key retry finds no
    /// item and is refused pre-flight — one logical build can never
    /// consume its design twice; the request-key dedupe is the primary
    /// retry guard. Payload: <see cref="HouseBuildParams"/>.
    /// </summary>
    ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null);

    /// Deposits copper from the inventory into the bank through the real
    /// engine path (Character.ChangeMoney — the exact call
    /// CSDepositMoneyPacket makes). The engine validates the inventory
    /// balance and refuses when insufficient, so a fresh-key retry after a
    /// timeout ambiguity lands on Rejected(RejectedAction) instead of a
    /// second deposit — the balance is the engine-true backstop.
    /// </summary>
    ActorRequest DepositMoney(long amount, string? idempotencyKey = null);

    /// <summary>
    /// Withdraws copper from the bank into the inventory through the real
    /// engine path (Character.ChangeMoney — the exact call
    /// CSWithdrawMoneyPacket makes). Engine-validated the same way as
    /// <see cref="DepositMoney"/>.
    /// </summary>
    ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null);

    /// <summary>
    /// Deposits an item stack from the inventory bag into the bank
    /// warehouse through the real engine container-move path
    /// (Inventory.SplitOrMoveItem — the exact call CSSwapItemsPacket makes
    /// for Inventory→Bank moves; whole stack). Resolves the first bag
    /// stack of the template; unknown templates are
    /// Rejected(RejectedAction, "not found in bag"). After a successful
    /// deposit the source stack is gone, so a fresh-key retry finds
    /// nothing to move — retries cannot double-deposit.
    /// </summary>
    ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null);

    /// <summary>
    /// Withdraws an item stack from the bank warehouse into the inventory
    /// bag through the real engine container-move path
    /// (Inventory.SplitOrMoveItem — the exact call CSSwapItemsPacket makes
    /// for Bank→Inventory moves; whole stack). Mirror of
    /// <see cref="DepositItem"/>.
    /// </summary>
    ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null);

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

    /// <summary>
    /// Buys goods from a merchant NPC through the REAL engine path — the
    /// same CSBuyItemsPacket branch: validates the NPC merchant + its goods
    /// pack (NpcManager.GetGoods), grants the item through
    /// ItemContainer.AcquireDefaultItem and charges money through
    /// Character.ChangeMoney (the packet's exact calls). The pack must sell
    /// the requested template; price = template.Price × count. Currency is
    /// the ordinary money pool only (no honor/vocation currency in v1).
    /// Rejections: merchant not found / not a merchant / not selling the
    /// item / insufficient money / non-positive count. Idempotency: the
    /// money gate is engine-true (a retry after a successful buy has less
    /// money; insufficient funds are Rejected), and the request-key dedupe
    /// is the primary retry guard — a same-key retry is never re-executed.
    /// Payload: <see cref="BuyParams"/>.
    /// </summary>
    ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null);

    /// <summary>
    /// Sells an item to a merchant NPC through the REAL engine path — the
    /// same CSSellItemsPacket branch: validates the NPC merchant, moves the
    /// item into the character's BuyBackItems container (the exact
    /// AddOrMoveExistingItem call), marks the DB row for deletion, and pays
    /// the refund through Character.ChangeMoney. The item must be in the
    /// actor's own inventory (Bag or Equipment) and template Sellable.
    /// Engine-true idempotency: the engine MOVES the item out of the bag on
    /// success, so a fresh-key retry finds no item and is Rejected — the
    /// item can never be sold twice. Payload: <see cref="SellParams"/>.
    /// </summary>
    ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null);

    /// <summary>
    /// Lists an item on the auction house through the REAL engine path —
    /// the same CSAuctionPostPacket call (AuctionManager.PostLotOnAuction):
    /// validates the item is in the actor's inventory, computes the listing
    /// fee (buyout × 1% × (duration+1), capped), and the engine moves the
    /// item into AuctionAttachments, deducts the fee and registers the lot.
    /// Rejections: item not owned / invalid prices / fee unaffordable.
    /// Engine-true idempotency: the item leaves the bag on success, so a
    /// fresh-key retry finds no item and cannot double-list. Payload:
    /// <see cref="AuctionPostParams"/>.
    /// </summary>
    ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration, string? idempotencyKey = null);

    /// <summary>
    /// Purchases an auction lot at the buy-now price through the REAL
    /// engine path — the same CSBidAuctionPacket call (AuctionManager.
    /// BidOnAuctionLot buy-now branch): validates the lot exists and has a
    /// buyout, pre-flights the money gate (the engine's SubtractMoney
    /// return is ignored — the actor refuses insufficient funds BEFORE the
    /// engine call so no lot can be granted without payment), then the
    /// engine deducts the buyout and removes the lot (mail delivery is the
    /// engine's own). Engine-true idempotency: the lot is removed on
    /// purchase, so a fresh-key retry finds no lot and cannot double-buy.
    /// Payload: <see cref="AuctionBuyParams"/>.
    /// </summary>
    ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null);

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
    PutDown = 16,

    /// <summary>Merchant buy through CSBuyItemsPacket (real engine trade path).</summary>
    Buy = 17,

    /// <summary>Merchant sell through CSSellItemsPacket (real engine trade path).</summary>
    Sell = 18,

    /// <summary>Auction listing through CSAuctionPostPacket / AuctionManager.PostLotOnAuction.</summary>
    AuctionPost = 19,

    /// <summary>Auction buy-now purchase through CSBidAuctionPacket / AuctionManager.BidOnAuctionLot.</summary>
    AuctionBuy = 20,

    /// <summary>Seed planting through DoodadManager.CreatePlayerDoodad (CSCreateDoodadPacket path).</summary>
    Plant = 21,

    /// <summary>House construction through HousingManager.Build (CSCreateHousePacket path).</summary>
    HouseBuild = 22,

    /// <summary>
    /// Vehicle driving through the client-authored vehicle movement model
    /// (VehicleMovementModel — the CSMoveUnitPacket path).
    /// </summary>
    Drive = 23,

    /// <summary>Trade-pack → vehicle cargo loading through PackVehicleService (real gameplay path, snap-to-cargo-point).</summary>
    LoadPackOntoVehicle = 24,

    /// <summary>Bank deposit of copper through Character.ChangeMoney (CSDepositMoneyPacket path).</summary>
    DepositMoney = 25,

    /// <summary>Bank withdrawal of copper through Character.ChangeMoney (CSWithdrawMoneyPacket path).</summary>
    WithdrawMoney = 26,

    /// <summary>Bank deposit of an item stack through Inventory.SplitOrMoveItem (CSSwapItemsPacket path).</summary>
    DepositItem = 27,

    /// <summary>Bank withdrawal of an item stack through Inventory.SplitOrMoveItem (CSSwapItemsPacket path).</summary>
    WithdrawItem = 28,

    /// <summary>Crop harvest through Doodad.Use(caster, harvestSkill) (real engine path, data-driven skill).</summary>
    Harvest = 29,

    /// <summary>Vehicle boarding through the vehicle/transfer managers (M5.1 — slaves, transfers, gliders).</summary>
    BoardVehicle = 30,

    /// <summary>Vehicle unboarding through the vehicle/transfer managers (M5.1 — slave unbind, seat unbond, glider takeoff).</summary>
    UnboardVehicle = 31,

    /// <summary>
    /// One engine craft step through CharacterCraft.Craft (the CSExecuteCraft
    /// path, count=1) — M5.1 economy surface.
    /// </summary>
    Craft = 32
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
