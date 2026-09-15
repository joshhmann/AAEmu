using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot restore [all|name|id] — rematerialize and activate dormant or deactivated
/// player bots from MySQL / registry back into the world.
/// </summary>
public class BotRestoreSubCommand : SubCommandBase
{
    public BotRestoreSubCommand()
    {
        Title = "[Bot Restore]";
        Description = "Restore dormant or deactivated player bot(s) into the world (all or by name/id)";
        CallPrefix = $"{CommandManager.CommandPrefix}bot restore";
        AddParameter(new StringSubCommandParameter("target", "all||name||id", false));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var target = GetOptionalParameterValue<string>(parameters, "target", null);
        var result = BotAdminService.FromContainer().Restore(target);
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
