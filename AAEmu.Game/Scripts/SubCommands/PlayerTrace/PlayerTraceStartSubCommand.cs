using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

#nullable enable

namespace AAEmu.Game.Scripts.SubCommands.PlayerTrace;

public class PlayerTraceStartSubCommand : SubCommandBase
{
    public PlayerTraceStartSubCommand()
    {
        Title = "[PlayerTrace Start]";
        Description = "Start recording an action trace (/trace start [scenario] or /trace start <char> [scenario])";
        CallPrefix = $"{CommandManager.CommandPrefix}playertrace start";
        AddParameter(new StringSubCommandParameter("targetOrScenario", "targetOrScenario", false));
        AddParameter(new StringSubCommandParameter("scenarioName", "scenarioName", false));
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        Character? targetChar = null;
        string scenario = "manual";

        if (args.Length == 0)
        {
            // /trace start -> trace self
            targetChar = character as Character;
        }
        else if (args.Length == 1)
        {
            // /trace start <arg>
            // Check if <arg> is a character name or id in world
            var lookup = uint.TryParse(args[0], out var id)
                ? WorldManager.Instance.GetCharacterById(id)
                : WorldManager.Instance.GetCharacter(args[0]);

            if (lookup != null)
            {
                targetChar = lookup;
            }
            else
            {
                // Not a character in world -> treat as scenario name for SELF
                targetChar = character as Character;
                scenario = args[0];
            }
        }
        else
        {
            // /trace start <target> <scenario...>
            var lookup = uint.TryParse(args[0], out var id)
                ? WorldManager.Instance.GetCharacterById(id)
                : WorldManager.Instance.GetCharacter(args[0]);

            if (lookup != null)
            {
                targetChar = lookup;
                scenario = string.Join("_", args.Skip(1));
            }
            else
            {
                // Target not found as character -> assume self, join all args as scenario name
                targetChar = character as Character;
                scenario = string.Join("_", args);
            }
        }

        if (targetChar == null)
        {
            SendColorMessage(messageOutput, Color.Red, "[PlayerTrace] No target character found in world.");
            return;
        }

        var (success, message, _) = PlayerTraceService.Instance.StartTrace(targetChar.Id, targetChar.Name, scenario);
        SendColorMessage(messageOutput, success ? Color.LawnGreen : Color.Yellow, $"[PlayerTrace] {message}");
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var rawArgs = parameters.Values
            .Select(v => v.As<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();

        Execute(character, triggerArgument, rawArgs, messageOutput);
    }
}
