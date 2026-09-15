using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.PlayerTrace;

public class PlayerTraceStopSubCommand : SubCommandBase
{
    public PlayerTraceStopSubCommand()
    {
        Title = "[PlayerTrace Stop]";
        Description = "Stop the active player action trace and finalize the JSONL file";
        CallPrefix = $"{CommandManager.CommandPrefix}playertrace stop";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var (success, message, _) = PlayerTraceService.Instance.StopTrace();
        SendColorMessage(messageOutput, success ? Color.LawnGreen : Color.Yellow, $"[PlayerTrace] {message}");
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        Execute(character, triggerArgument, [], messageOutput);
    }
}
