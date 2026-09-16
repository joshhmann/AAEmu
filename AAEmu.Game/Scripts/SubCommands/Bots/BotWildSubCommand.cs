#nullable enable

using System.Drawing;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot wild &lt;list|go|nearest&gt; — secret wild tree farms and illegal farming operations.
/// Subcommands:
///   list                     - lists all known secret wild tree farm POIs
///   go &lt;botName&gt; [poiId]       - sends bot to a secret wilderness farm (nearest if poiId omitted)
///   nearest [botName]        - shows the nearest secret tree farm to the bot or player
/// </summary>
public class BotWildSubCommand : SubCommandBase
{
    public BotWildSubCommand()
    {
        Title = "[Bot Wild]";
        Description = "Manage secret wild tree farms and illegal farming: list, go <botName> [poiId], nearest [botName]";
        CallPrefix = $"{CommandManager.CommandPrefix}bot wild";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            SendColorMessage(messageOutput, Color.Yellow, "Usage: /bot wild <list|go|nearest> [options]");
            SendColorMessage(messageOutput, Color.White, "  list                     - lists all cataloged illegal tree farm POIs");
            SendColorMessage(messageOutput, Color.White, "  go <botName> [poiId]       - sends bot to a secret wild farm");
            SendColorMessage(messageOutput, Color.White, "  nearest [botName]        - locates the nearest secret farm");
            return;
        }

        var action = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (action)
        {
            case "list":
                ExecuteList(messageOutput);
                break;
            case "go":
                ExecuteGo(subArgs, messageOutput);
                break;
            case "nearest":
                ExecuteNearest(character, subArgs, messageOutput);
                break;
            default:
                SendColorMessage(messageOutput, Color.Red, $"Unknown action '{action}'. Valid actions: list, go, nearest.");
                break;
        }
    }

    private static IWildFarmPoiRegistry GetRegistry()
    {
        var sp = SingletonContainer.ServiceProvider;
        return sp?.GetService<IWildFarmPoiRegistry>() ?? new WildFarmPoiRegistry();
    }

    private void ExecuteList(IMessageOutput messageOutput)
    {
        var registry = GetRegistry();
        var pois = registry.GetAllPois();
        SendColorMessage(messageOutput, Color.LightGreen, $"[Wild Farms] {pois.Count} cataloged wilderness POIs:");
        for (var i = 0; i < pois.Count; i++)
        {
            var p = pois[i];
            SendColorMessage(messageOutput, Color.White, $"  [{i + 1}] {p.Id} ({p.X:F1}, {p.Y:F1}, {p.Z:F1}) — {p.Label}");
            if (!string.IsNullOrWhiteSpace(p.Description))
                SendColorMessage(messageOutput, Color.Gray, $"      ↳ {p.Description}");
        }
    }

    private void ExecuteGo(string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot wild go <botName> [poiId]");
            return;
        }

        var botName = args[0];
        var poiId = args.Length >= 2 ? args[1] : null;

        var registry = GetRegistry();
        var allPois = registry.GetAllPois();
        if (allPois.Count == 0)
        {
            SendColorMessage(messageOutput, Color.Red, "No wild farm POIs configured.");
            return;
        }

        WildFarmPoi? targetPoi = null;
        if (!string.IsNullOrWhiteSpace(poiId))
        {
            targetPoi = allPois.FirstOrDefault(p => p.Id.Equals(poiId, StringComparison.OrdinalIgnoreCase));
            if (targetPoi == null)
            {
                SendColorMessage(messageOutput, Color.Red, $"Wild farm POI '{poiId}' not found. Use '/bot wild list' to see available IDs.");
                return;
            }
        }
        else
        {
            // Resolve nearest to bot if bot exists
            var sp = SingletonContainer.ServiceProvider;
            var botManager = sp?.GetService<IPlayerBotManager>();
            var bot = botManager?.GetAll().FirstOrDefault(r => r.Character.Name.Equals(botName, StringComparison.OrdinalIgnoreCase));
            if (bot != null)
            {
                var pos = bot.Character.Transform.World.Position;
                targetPoi = registry.FindNearest(pos, 50000f);
            }

            targetPoi ??= allPois[0];
        }

        var targetPos = new Vector3(targetPoi.X, targetPoi.Y, targetPoi.Z);
        var adminService = BotAdminService.FromContainer();
        var result = adminService.Go(botName, targetPos);

        if (result.Success)
        {
            SendColorMessage(messageOutput, Color.LawnGreen,
                $"[Wild Farm] Bot '{botName}' dispatched to secret farm: {targetPoi.Label} ({targetPos.X:F1}, {targetPos.Y:F1}, {targetPos.Z:F1})");
        }
        else
        {
            SendColorMessage(messageOutput, Color.Red, $"[Wild Farm] Relocate failed: {result.Message}");
        }
    }

    private void ExecuteNearest(ICharacter character, string[] args, IMessageOutput messageOutput)
    {
        var registry = GetRegistry();
        Vector3 origin;
        string originName;

        if (args.Length >= 1)
        {
            var botName = args[0];
            var sp = SingletonContainer.ServiceProvider;
            var botManager = sp?.GetService<IPlayerBotManager>();
            var bot = botManager?.GetAll().FirstOrDefault(r => r.Character.Name.Equals(botName, StringComparison.OrdinalIgnoreCase));
            if (bot == null)
            {
                SendColorMessage(messageOutput, Color.Red, $"Bot '{botName}' not found.");
                return;
            }
            origin = bot.Character.Transform.World.Position;
            originName = $"Bot '{botName}'";
        }
        else if (character is Character pc)
        {
            origin = pc.Transform.World.Position;
            originName = $"Player '{pc.Name}'";
        }
        else
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot wild nearest [botName]");
            return;
        }

        var nearest = registry.FindNearest(origin, 50000f);
        if (nearest == null)
        {
            SendColorMessage(messageOutput, Color.Yellow, "No wild farms within range.");
            return;
        }

        var dist = MathF.Sqrt(MathF.Pow(nearest.X - origin.X, 2) + MathF.Pow(nearest.Y - origin.Y, 2));
        SendColorMessage(messageOutput, Color.LawnGreen,
            $"[Wild Farm] Closest to {originName} ({origin.X:F1}, {origin.Y:F1}):");
        SendColorMessage(messageOutput, Color.White,
            $"  {nearest.Label} ({nearest.Id}) — {dist:F1}m away at ({nearest.X:F1}, {nearest.Y:F1}, {nearest.Z:F1})");
    }
}
