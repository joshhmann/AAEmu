using System.Drawing;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot go &lt;name&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; — relocate a bot's patrol home
/// to explicit coordinates (terrain-clamped, post-hotfix coords).
/// </summary>
public class BotGoSubCommand : SubCommandBase
{
    public BotGoSubCommand()
    {
        Title = "[Bot Go]";
        Description = "Relocate a bot's patrol home to x y z (terrain-clamped)";
        CallPrefix = $"{CommandManager.CommandPrefix}bot go";
        AddParameter(new StringSubCommandParameter("name", "name", true));
        AddParameter(new NumericSubCommandParameter<float>("x", "x", true));
        AddParameter(new NumericSubCommandParameter<float>("y", "y", true));
        AddParameter(new NumericSubCommandParameter<float>("z", "z", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var name = parameters["name"].As<string>();
        var x = parameters["x"].As<float>();
        var y = parameters["y"].As<float>();
        var z = parameters["z"].As<float>();

        var result = BotAdminService.FromContainer().Go(name, new Vector3(x, y, z));
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
