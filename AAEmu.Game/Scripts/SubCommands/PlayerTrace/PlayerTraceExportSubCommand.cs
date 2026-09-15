using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.PlayerTrace;

public class PlayerTraceExportSubCommand : SubCommandBase
{
    public PlayerTraceExportSubCommand()
    {
        Title = "[PlayerTrace Export]";
        Description = "Show the file location and summary of the active or latest trace";
        CallPrefix = $"{CommandManager.CommandPrefix}playertrace export";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var (isActive, charName, scenario, elapsedMs, events, filePath) = PlayerTraceService.Instance.GetStatus();

        if (string.IsNullOrEmpty(filePath))
        {
            SendColorMessage(messageOutput, Color.Yellow, "[PlayerTrace] No trace file has been recorded in this session.");
            return;
        }

        SendColorMessage(messageOutput, Color.Cyan,
            $"[PlayerTrace] File: {filePath} | Target: {charName} | Events: {events} | Active: {isActive}");
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        Execute(character, triggerArgument, [], messageOutput);
    }
}
