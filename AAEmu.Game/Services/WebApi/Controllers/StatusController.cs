using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using NetCoreServer;

namespace AAEmu.Game.Services.WebApi.Controllers;

/// <summary>
/// Status controller for the WebApi
/// </summary>
internal class StatusController : BaseController
{
    // Anchored so it cannot prefix-match other endpoints whose URL ends in
    // "/status" (e.g. GET /api/bots/status — the unanchored form collided
    // with the bot control status route, t_2ea94a20).
    [WebApiGet("^/status$")]
    public HttpResponse GetStatus(HttpRequest request)
    {
        var playerCount = WorldManager.Instance.GetAllCharacters().Count();
        var serverUptime = new TimeSpan(0, 0, Program.UpTime);
        var responseBody = $"Server uptime: {serverUptime}<br/>" +
                           $"Players online: {playerCount}<br/>" +
                           $"Number of TaskManager Jobs: {TaskManager.Instance.GetQueueCount()}<br/>";

        return OkHtml(responseBody);
    }
}
