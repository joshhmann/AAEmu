using System.Drawing;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Scripts.SubCommands.Mapper;

/// <summary>
/// /mapper play &lt;bot_name&gt; &lt;route_name&gt; — command an active bot
/// to replay a recorded manual walk route.
/// </summary>
public class MapperPlaySubCommand : SubCommandBase
{
    public MapperPlaySubCommand()
    {
        Title = "[Mapper Play]";
        Description = "Command an active bot to replay a recorded route";
        CallPrefix = $"{CommandManager.CommandPrefix}mapper play";
        AddParameter(new StringSubCommandParameter("bot_name", "bot_name", true));
        AddParameter(new StringSubCommandParameter("route_name", "route_name", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var botName = parameters["bot_name"].As<string>();
        var routeName = parameters["route_name"].As<string>();

        var route = DevMapperService.Instance.GetRoute(routeName);
        if (route == null)
        {
            SendColorMessage(messageOutput, Color.Red, $"[Mapper] Route '{routeName}' not found.");
            return;
        }

        var sp = SingletonContainer.ServiceProvider;
        var botManager = sp?.GetService<IPlayerBotManager>();
        var botRuntime = botManager?.GetActive().FirstOrDefault(b => b.Character.Name.Equals(botName, StringComparison.OrdinalIgnoreCase));
        if (botRuntime == null)
        {
            SendColorMessage(messageOutput, Color.Red, $"[Mapper] Active bot '{botName}' not found.");
            return;
        }

        var actor = new GameplayActor(botRuntime.Character);
        Task.Run(() =>
        {
            var result = DevMapperService.Instance.ReplayRoute(actor, route);
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, $"[Mapper] Replay for {botName}: {result.Message}");
        });

        SendColorMessage(messageOutput, Color.Cyan,
            $"[Mapper] Dispatched replay of '{routeName}' ({route.Actions.Count} actions) to bot '{botName}'.");
    }
}
