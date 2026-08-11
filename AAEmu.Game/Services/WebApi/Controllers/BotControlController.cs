using System.Net;
using System.Numerics;
using System.Text.Json;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Services.WebApi.Models;
using NetCoreServer;
using NLog;

namespace AAEmu.Game.Services.WebApi.Controllers;

/// <summary>
/// Bot control API (P1 t_2ea94a20) — programmatic management surface over
/// the SAME BotAdminService core the /bot GM commands use (one control core,
/// two frontends): list / add / remove / relocate / status.
///
/// POSTURE: disabled by default — every route returns 404 unless the API is
/// explicitly enabled (AAEMU_BOT_CTRL=1 env or Config "Bots"."EnableBotControl"),
/// and every request must carry the shared secret in the X-Auth-Token header
/// (AAEMU_BOT_CTRL_TOKEN env — the env-secret contract). All mutations run
/// inside the game process on the normal bot manager + lifecycle — no
/// parallel bot path, single execution boundary (M5 A1).
/// </summary>
internal class BotControlController : BaseController
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [WebApiGet("^/api/bots$")]
    public HttpResponse List(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var bots = BotAdminService.FromContainer().ListStatus();
            return OkJson(new BotControlResponse(true, $"{bots.Count} registered", bots));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: list failed");
        }
    }

    [WebApiGet("^/api/bots/status$")]
    public HttpResponse Status(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var bots = BotAdminService.FromContainer().ListStatus();
            var active = bots.Count(b => b.State == PlayerBotState.Active.ToString());
            return OkJson(new BotControlResponse(true, $"{bots.Count} registered ({active} active)", bots));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: status failed");
        }
    }

    [WebApiPost("^/api/bots$")]
    public HttpResponse Add(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<AddBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Name))
                return BadRequestJson(new ErrorModel("name is required"));

            var home = body.X.HasValue && body.Y.HasValue && body.Z.HasValue
                ? new Vector3(body.X.Value, body.Y.Value, body.Z.Value)
                : (Vector3?)null;
            var result = BotAdminService.FromContainer().Add(body.Name, home);
            return OkJson(new BotControlResponse(result.Success, result.Message));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: add failed");
        }
    }

    [WebApiPost("^/api/bots/remove$")]
    public HttpResponse Remove(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<RemoveBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.NameOrId))
                return BadRequestJson(new ErrorModel("nameOrId is required"));

            var result = BotAdminService.FromContainer().Remove(body.NameOrId);
            return OkJson(new BotControlResponse(result.Success, result.Message));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: remove failed");
        }
    }

    [WebApiPost("^/api/bots/relocate$")]
    public HttpResponse Relocate(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<RelocateBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.NameOrId))
                return BadRequestJson(new ErrorModel("nameOrId is required"));
            if (!body.X.HasValue || !body.Y.HasValue || !body.Z.HasValue)
                return BadRequestJson(new ErrorModel("x, y and z are required"));

            var result = BotAdminService.FromContainer().Go(
                body.NameOrId, new Vector3(body.X.Value, body.Y.Value, body.Z.Value));
            return OkJson(new BotControlResponse(result.Success, result.Message));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: relocate failed");
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Gate: null when authorized, otherwise the error response to return.</summary>
    private static HttpResponse? CheckGate(HttpRequest request)
    {
        if (!BotControlSettings.IsEnabled())
            return JsonResponse(HttpStatusCode.NotFound, new ErrorModel("Bot control API is disabled"));

        if (!BotControlSettings.TokenMatches(GetHeader(request, "X-Auth-Token")))
            return JsonResponse(HttpStatusCode.Unauthorized, new ErrorModel("Missing or invalid X-Auth-Token"));

        return null;
    }

    /// <summary>Case-insensitive header lookup (NetCoreServer stores headers as an indexed list).</summary>
    private static string GetHeader(HttpRequest request, string name)
    {
        for (var i = 0; i < request.Headers; i++)
        {
            var (key, value) = request.Header(i);
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return string.Empty;
    }

    private static T? Deserialize<T>(HttpRequest request)
        => string.IsNullOrWhiteSpace(request.Body)
            ? default
            : JsonSerializer.Deserialize<T>(request.Body, JsonOpts);

    private static HttpResponse Error(Exception ex, string logMessage)
    {
        Logger.Error(ex, logMessage);
        return JsonResponse(HttpStatusCode.InternalServerError, new ErrorModel(ex.Message));
    }
}
