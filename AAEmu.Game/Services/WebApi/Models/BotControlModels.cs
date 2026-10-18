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

/// <summary>
/// POST /api/bots/assign — operator assignment (plan T1): one activity
/// (<c>assigned.travel</c>), one destination and one bounded expiry for one
/// running bot. Expiry is optional; the registry applies its own default and
/// bounds when it is absent or out of range.
/// </summary>
public sealed record AssignBotRequest(string? NameOrId, string? Activity, float? X, float? Y, float? Z, int? ExpirySeconds);

/// <summary>
/// POST /api/bots/assign response — the accepted assignment exactly as the
/// registry recorded it, so the caller sees the effective expiry and state
/// rather than an echo of what it asked for.
/// </summary>
public sealed record BotAssignmentResponse(bool Success, string Message, BotAssignment? Assignment = null);

/// <summary>POST /api/bots/host/stop — disarm the bot host (optional reason; body may be empty).</summary>
public sealed record StopBotHostRequest(string? Reason);

/// <summary>
/// POST /api/bots/host/stop response — the converging-teardown report: what
/// the one stop owner actually did (queued steps dropped, bots deactivated,
/// refusals by character id).
/// </summary>
public sealed record BotHostStopResponse(bool Success, string Message, BotHostTeardownReport Report);
