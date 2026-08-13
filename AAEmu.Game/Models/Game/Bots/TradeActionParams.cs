using AAEmu.Game.Models.Game.Auction;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// M5.1 Buy/Sell action payloads (ROADMAP §M5.1, t_8741b03d) — execution
/// inputs for the four trade actions. The ActorRequest.TargetId carries the
/// world target where one exists (merchant NPC objId); the payloads carry
/// economic identifiers (item template / item instance / lot ids, which
/// are ulong and cannot fit TargetId's uint). Payloads are execution inputs
/// only, never serialized into audit output (same rule as the B1 quest
/// payloads).
/// </summary>

/// <summary>Merchant buy: the item template + quantity to purchase.</summary>
/// <param name="ItemTemplateId">Item template id (the pack's stock item).</param>
/// <param name="Count">Number of units to buy.</param>
public sealed record BuyParams(uint ItemTemplateId, int Count);

/// <summary>Merchant sell: the item INSTANCE id to sell (ulong — lives in the payload).</summary>
/// <param name="ItemId">Item instance id (Item.Id).</param>
public sealed record SellParams(ulong ItemId);

/// <summary>Auction listing: item instance + price terms.</summary>
/// <param name="ItemId">Item instance id to list.</param>
/// <param name="StartPrice">Starting bid.</param>
/// <param name="BuyoutPrice">Buy-now price (0 = auction-only listing).</param>
/// <param name="Duration">Listing duration.</param>
public sealed record AuctionPostParams(ulong ItemId, int StartPrice, int BuyoutPrice, AuctionDuration Duration);

/// <summary>Auction purchase: the lot to buy now + the offered price.</summary>
/// <param name="LotId">Auction lot id.</param>
/// <param name="Price">Offered price — must be ≥ the lot's buyout for the
/// buy-now branch; below-buyout offers are Rejected (this surface is
/// purchase, not bidding).</param>
public sealed record AuctionBuyParams(ulong LotId, int Price);
