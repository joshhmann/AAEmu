using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot to &lt;name&gt; — relocate a bot's patrol home to the issuing GM's
/// position (the live-ops "bring the bot to me" form of /bot go).
/// </summary>
public class BotToSubCommand : SubCommandBase
{
    public BotToSubCommand()
    {
        Title = "[Bot To]";
        Description = "Relocate a bot to your position (bring the bot to you)";
        CallPrefix = $"{CommandManager.CommandPrefix}bot to";
        AddParameter(new StringSubCommandParameter("name", "name", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var name = parameters["name"].As<string>();
        var gmPosition = ((Character)character).Transform.World.Position;

        var result = BotAdminService.FromContainer().Go(name, gmPosition);
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
