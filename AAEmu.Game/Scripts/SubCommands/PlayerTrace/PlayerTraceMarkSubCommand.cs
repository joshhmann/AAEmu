using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.PlayerTrace;

public class PlayerTraceMarkSubCommand : SubCommandBase
{
    public PlayerTraceMarkSubCommand()
    {
        Title = "[PlayerTrace Mark]";
        Description = "Insert a note or checkpoint into the active trace (/trace mark <note>)";
        CallPrefix = $"{CommandManager.CommandPrefix}playertrace mark";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var label = args.Length > 0 ? string.Join(" ", args) : "checkpoint";
        var (success, message) = PlayerTraceService.Instance.RecordMark(label);
        SendColorMessage(messageOutput, success ? Color.LawnGreen : Color.Yellow, $"[PlayerTrace] {message}");
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        Execute(character, triggerArgument, [], messageOutput);
    }
}
