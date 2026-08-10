using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot list — registry + embodied state (name, id, fidelity, position).
/// </summary>
public class BotListSubCommand : SubCommandBase
{
    public BotListSubCommand()
    {
        Title = "[Bot List]";
        Description = "List all registered player bots with id, fidelity and position";
        CallPrefix = $"{CommandManager.CommandPrefix}bot list";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var result = BotAdminService.FromContainer().List();
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
