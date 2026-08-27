namespace AAEmu.Game.Services.WebApi.Models;

// ------------------------------------------------------------------ requests
// Control-plane action request bodies (M5 contract actions). Field names are
// the wire contract; every endpoint maps 1:1 to an IGameplayActor action.
// `bot` accepts a bot name or numeric character id — the same addressing the
// management surface (t_2ea94a20) uses.

/// <summary>POST /api/actors/observe — observation snapshot (direct server-state query).</summary>
public sealed record ObserveRequest(string? Bot);

/// <summary>POST /api/actors/move — bounded walk to an absolute position.</summary>
public sealed record MoveRequest(string? Bot, float? X, float? Y, float? Z, float? Speed, int? TimeoutSec, string? IdempotencyKey);

/// <summary>POST /api/actors/move_to_unit — walk to a unit's current position.</summary>
public sealed record MoveToUnitRequest(string? Bot, uint? TargetObjId, float? Speed, int? TimeoutSec, string? IdempotencyKey);

/// <summary>POST /api/actors/stop — interrupt the running request (no-op when idle).</summary>
public sealed record StopRequest(string? Bot);

/// <summary>POST /api/actors/target — set the actor's current target.</summary>
public sealed record TargetRequest(string? Bot, uint? TargetObjId);

/// <summary>POST /api/actors/cast — cast a known skill at a unit.</summary>
public sealed record CastRequest(string? Bot, uint? SkillId, uint? TargetObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/interact — interact with a doodad (skillId 0 = skill-less branch).</summary>
public sealed record InteractRequest(string? Bot, uint? DoodadObjId, uint? SkillId, string? IdempotencyKey);

/// <summary>POST /api/actors/loot — loot a corpse/bag owner (loot-all).</summary>
public sealed record LootRequest(string? Bot, uint? LootOwnerObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/use_item — use an inventory item (targetObjId 0 = self).</summary>
public sealed record UseItemRequest(string? Bot, uint? ItemTemplateId, uint? TargetObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/mount — mount an owned mate.</summary>
public sealed record MountRequest(string? Bot, uint? MateObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/dismount — dismount (mateObjId 0 = current mount).</summary>
public sealed record DismountRequest(string? Bot, uint? MateObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/accept_quest — quest acceptance through the real AddQuest gate.</summary>
public sealed record AcceptQuestRequest(string? Bot, uint? QuestId, string? AcceptorType, uint? AcceptorId, string? IdempotencyKey);

/// <summary>POST /api/actors/advance_quest — one step-machine advance on an active quest.</summary>
public sealed record AdvanceQuestRequest(string? Bot, uint? QuestId, string? IdempotencyKey);

/// <summary>POST /api/actors/turn_in_quest — turn-in at an NPC.</summary>
public sealed record TurnInQuestRequest(string? Bot, uint? QuestId, uint? NpcObjId, int? SelectedReward, string? IdempotencyKey);

/// <summary>POST /api/actors/turn_in_doodad — turn-in at a doodad.</summary>
public sealed record TurnInDoodadRequest(string? Bot, uint? QuestId, uint? DoodadObjId, int? SelectedReward, string? IdempotencyKey);

/// <summary>POST /api/actors/auto_turn_in — auto-complete turn-in (no world target).</summary>
public sealed record AutoTurnInRequest(string? Bot, uint? QuestId, int? SelectedReward, string? IdempotencyKey);

/// <summary>POST /api/actors/interrupt — cancel a running request by its API trace id.</summary>
public sealed record InterruptRequest(string? Bot, string? TraceId);
/// <summary>POST /api/actors/discover_quests — discover offers from an NPC or doodad.</summary>
public sealed record DiscoverQuestsRequest(string? Bot, uint? TargetObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/discover_self_quests — discover item/sphere/level offers for the actor.</summary>
public sealed record DiscoverSelfQuestsRequest(string? Bot, string? IdempotencyKey);

/// <summary>POST /api/actors/interact_with — use a doodad's derived interaction skill.</summary>
public sealed record InteractWithRequest(string? Bot, uint? DoodadObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/talk — credit an NPC talk through the normal quest event path.</summary>
public sealed record TalkRequest(string? Bot, uint? NpcObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/equip — equip a bagged item by template through the normal inventory path.</summary>
public sealed record EquipRequest(string? Bot, uint? ItemTemplateId, string? IdempotencyKey);

/// <summary>POST /api/actors/deposit_money — deposit copper from inventory into bank.</summary>
public sealed record DepositMoneyRequest(string? Bot, long? Amount, string? IdempotencyKey);

/// <summary>POST /api/actors/withdraw_money — withdraw copper from bank into inventory.</summary>
public sealed record WithdrawMoneyRequest(string? Bot, long? Amount, string? IdempotencyKey);

/// <summary>POST /api/actors/deposit_item — deposit an item stack from bag into bank.</summary>
public sealed record DepositItemRequest(string? Bot, uint? ItemTemplateId, string? IdempotencyKey);

/// <summary>POST /api/actors/withdraw_item — withdraw an item stack from bank into bag.</summary>
public sealed record WithdrawItemRequest(string? Bot, uint? ItemTemplateId, string? IdempotencyKey);

/// <summary>POST /api/actors/plant — plant a seed or young tree at a world position.</summary>
public sealed record PlantRequest(string? Bot, uint? SeedItemTemplateId, float? X, float? Y, float? Z, float? ZRot, float? Scale, string? IdempotencyKey);

/// <summary>POST /api/actors/harvest — harvest a mature crop doodad.</summary>
public sealed record HarvestRequest(string? Bot, uint? DoodadObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/craft — craft a recipe at a workbench doodad.</summary>
public sealed record CraftRequest(string? Bot, uint? CraftId, uint? DoodadObjId, string? IdempotencyKey);

/// <summary>POST /api/actors/buy — buy an item from an NPC merchant.</summary>
public sealed record BuyRequest(string? Bot, uint? MerchantNpcObjId, uint? ItemTemplateId, int? Count, string? IdempotencyKey);

/// <summary>POST /api/actors/sell — sell an item to an NPC merchant.</summary>
public sealed record SellRequest(string? Bot, uint? MerchantNpcObjId, ulong? ItemId, string? IdempotencyKey);

// ----------------------------------------------------------------- responses

// Action responses are shaped in the controller as lowercase JSON (JObject):
// enqueue → { success, message, trace_id, bot, action, state } where the
// caller polls lifecycle transitions by trace_id
// (GET /api/actors/actions/{traceId}); poll/trace → { trace_id, actor_id,
// bot, action, state, failure, detail, requested_at, started_at,
// completed_at, state_changes, audit, result_payload }.
