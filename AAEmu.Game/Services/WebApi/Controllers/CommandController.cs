using AAEmu.Game.Core.Managers;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AAEmu.Game.Core.Managers.World;
using NetCoreServer;
using AAEmu.Game.Services.WebApi.Models;

namespace AAEmu.Game.Services.WebApi.Controllers;

/// <summary>
/// Generic GM verb route. It runs a command line as any online character, so it
/// carries the same fail-closed operator gate as the bot control routes
/// (layer-audit L1-k): 404 while the operator API is disabled, 401 on a missing
/// or wrong token. The gate is checked before the request body is parsed.
/// </summary>
internal class CommandController : BaseController
{
    [WebApiPost("/api/commands/([^/]+)")]
    public HttpResponse ExecuteCommand(HttpRequest request, MatchCollection matches)
    {
        if (CheckGate(request) is { } gateError)
            return gateError;

        var commandName = matches[0].Groups[1].Value;
        if (string.IsNullOrWhiteSpace(commandName))
            return BadRequestJson(new ErrorModel("Command name is required"));

        var jsonBody = JsonSerializer.Deserialize<JsonElement>(request.Body);
        var commandCharacter = jsonBody.GetProperty("character").GetString();
        if (string.IsNullOrWhiteSpace(commandCharacter))
            return BadRequestJson(new ErrorModel("Character name is required"));

        var commandArguments = jsonBody.GetProperty("arguments").GetString();
        if (string.IsNullOrWhiteSpace(commandArguments))
            return BadRequestJson(new ErrorModel("Command line is required"));

        var commandLine = $"{commandName} {commandArguments}";

        var character = WorldManager.Instance.GetCharacter(commandCharacter);
        if (character == null)
            return BadRequestJson(new ErrorModel($"Character \"{commandCharacter}\" not found"));

        CommandManager.Instance.Handle(character, commandLine, out var messageOutput);

        var commandResult = new
        {
            commandLine,
            commandCharacter,
            messageOutput.Messages,
            messageOutput.ErrorMessages
        };

        return OkJson(commandResult);
    }

    /// <summary>Gate: null when authorized, otherwise the error response to return.</summary>
    private static HttpResponse? CheckGate(HttpRequest request)
    {
        if (!BotControlSettings.IsEnabled())
            return JsonResponse(HttpStatusCode.NotFound, new ErrorModel("Command API is disabled"));

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
}
