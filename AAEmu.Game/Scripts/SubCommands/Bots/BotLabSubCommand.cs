using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot lab &lt;name&gt; &lt;mode&gt; [hz] — command a bot into a specific movement test mode
/// (circle, line, ramp, roam) at the given broadcast frequency (5, 10, or 20 Hz), or toggle telemetry.
/// </summary>
public class BotLabSubCommand : SubCommandBase
{
    public BotLabSubCommand()
    {
        Title = "[Bot Lab]";
        Description = "Run a bot movement test (circle, line, ramp, roam, telemetry) at specified hz";
        CallPrefix = $"{CommandManager.CommandPrefix}bot lab";
        AddParameter(new StringSubCommandParameter("name", "name", true));
        AddParameter(new StringSubCommandParameter("mode", "circle||line||ramp||roam||telemetry", true));
        AddParameter(new NumericSubCommandParameter<int>("hz", "broadcast hz (e.g. 5, 10, 20)", false));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var name = parameters["name"].As<string>();
        var mode = parameters["mode"].As<string>();
        var hz = parameters.TryGetValue("hz", out var hzVal) ? hzVal.As<int>() : 10;

        var result = BotAdminService.FromContainer().Lab(name, mode, hz);
        foreach (var line in result.Message.Split('\n'))
        {
            SendColorMessage(messageOutput, result.Success ? Color.LawnGreen : Color.Red, line);
        }
    }
}
