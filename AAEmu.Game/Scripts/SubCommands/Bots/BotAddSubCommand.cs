using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot add &lt;name&gt; — provision a bot via the production HeadlessSession
/// path (idempotent: an existing row owned by the GM bot account is adopted).
/// </summary>
public class BotAddSubCommand : SubCommandBase
{
    public BotAddSubCommand()
    {
        Title = "[Bot Add]";
        Description = "Add a player bot by name (provisions via the production path, idempotent)";
        CallPrefix = $"{CommandManager.CommandPrefix}bot add";
        AddParameter(new StringSubCommandParameter("name", "name", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var name = parameters["name"].As<string>();
        var result = BotAdminService.FromContainer().Add(name);
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
