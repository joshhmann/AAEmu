using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot remove &lt;name|id&gt; — deactivate via lifecycle + leave-save and drop
/// the registry entry. Idempotent (unknown name/id returns a friendly error).
/// </summary>
public class BotRemoveSubCommand : SubCommandBase
{
    public BotRemoveSubCommand()
    {
        Title = "[Bot Remove]";
        Description = "Remove a player bot by name or id (deactivate + leave-save, idempotent)";
        CallPrefix = $"{CommandManager.CommandPrefix}bot remove";
        AddParameter(new StringSubCommandParameter("name", "name|id", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var nameOrId = parameters["name"].As<string>();
        var result = BotAdminService.FromContainer().Remove(nameOrId);
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
