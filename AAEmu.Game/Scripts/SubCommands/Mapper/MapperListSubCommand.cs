using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Mapper;

/// <summary>
/// /mapper list — list all saved routes in Data/Routes.
/// </summary>
public class MapperListSubCommand : SubCommandBase
{
    public MapperListSubCommand()
    {
        Title = "[Mapper List]";
        Description = "List all saved routes";
        CallPrefix = $"{CommandManager.CommandPrefix}mapper list";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var routes = DevMapperService.Instance.ListRoutes();
        if (routes.Count == 0)
        {
            SendColorMessage(messageOutput, Color.Yellow, "[Mapper] No saved routes found in Data/Routes.");
            return;
        }

        SendColorMessage(messageOutput, Color.Cyan, $"[Mapper] Saved routes ({routes.Count}):");
        foreach (var route in routes)
        {
            var data = DevMapperService.Instance.GetRoute(route);
            if (data != null)
            {
                SendColorMessage(messageOutput, Color.LawnGreen,
                    $"  • {route} — {data.WaypointCount} waypoints, {data.ActionCount} actions, {data.TotalDistance:F1}m (by {data.Author})");
            }
            else
            {
                SendColorMessage(messageOutput, Color.White, $"  • {route}");
            }
        }
    }
}
