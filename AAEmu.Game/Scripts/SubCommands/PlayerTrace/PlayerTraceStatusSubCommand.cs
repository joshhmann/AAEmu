using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.PlayerTrace;

public class PlayerTraceStatusSubCommand : SubCommandBase
{
    public PlayerTraceStatusSubCommand()
    {
        Title = "[PlayerTrace Status]";
        Description = "Check the current status and metrics of the player action tracer";
        CallPrefix = $"{CommandManager.CommandPrefix}playertrace status";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var (isActive, charName, scenario, elapsedMs, events, filePath) = PlayerTraceService.Instance.GetStatus();

        if (!isActive)
        {
            SendColorMessage(messageOutput, Color.Yellow, "[PlayerTrace] Tracer is currently IDLE (no active trace).");
            return;
        }

        SendColorMessage(messageOutput, Color.Cyan,
            $"[PlayerTrace] ACTIVE | Target: {charName} | Scenario: {scenario} | Elapsed: {elapsedMs}ms | Events: {events} | File: {filePath}");
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        Execute(character, triggerArgument, [], messageOutput);
    }
}
