using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Mapper;

/// <summary>
/// /mapper walk &lt;name&gt; — enter Manual Walk Mode to trace waypoints,
/// turns, interactions, and actions along a path.
/// </summary>
public class MapperWalkSubCommand : SubCommandBase
{
    public MapperWalkSubCommand()
    {
        Title = "[Mapper Walk]";
        Description = "Enter Manual Walk Mode to record movement and actions into a route";
        CallPrefix = $"{CommandManager.CommandPrefix}mapper walk";
        AddParameter(new StringSubCommandParameter("name", "name", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var name = parameters["name"].As<string>();
        var summary = DevMapperService.Instance.Start(character, name);
        SendColorMessage(messageOutput, summary.Success ? Color.LawnGreen : Color.Red, summary.Message);
    }
}
