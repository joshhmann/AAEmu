using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.Game.Services.WebApi.Models;

/// <summary>POST /api/bots — add a bot (name required; optional spawn home x/y/z).</summary>
public sealed record AddBotRequest(string? Name, float? X, float? Y, float? Z);

/// <summary>POST /api/bots/remove — remove by bot name or numeric id.</summary>
public sealed record RemoveBotRequest(string? NameOrId);

/// <summary>POST /api/bots/relocate — move a bot's patrol home (terrain-clamped).</summary>
public sealed record RelocateBotRequest(string? NameOrId, float? X, float? Y, float? Z);

/// <summary>
/// Uniform success envelope for the bot control API. <see cref="Bots"/> is
/// populated by the list/status endpoints (structured BotAdminService
/// snapshot — the same core the /bot GM commands call).
/// </summary>
public sealed record BotControlResponse(bool Success, string Message, IReadOnlyList<BotStatusRecord>? Bots = null);
