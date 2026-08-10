using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot here [name] — spawn/provision a bot at the issuing GM's position.
/// The name is auto-generated (Bot01..) when omitted.
/// </summary>
public class BotHereSubCommand : SubCommandBase
{
    public BotHereSubCommand()
    {
        Title = "[Bot Here]";
        Description = "Spawn a bot at your position. Optional name, auto-generated when omitted";
        CallPrefix = $"{CommandManager.CommandPrefix}bot here";
        AddParameter(new StringSubCommandParameter("name", "name", false));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var name = GetOptionalParameterValue<string>(parameters, "name", null);
        var gmPosition = ((Character)character).Transform.World.Position;

        var result = BotAdminService.FromContainer().Here(gmPosition, name);
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
