using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Mapper;

/// <summary>
/// /mapper mark &lt;label&gt; — drop a custom tagged landmark at current position.
/// </summary>
public class MapperMarkSubCommand : SubCommandBase
{
    public MapperMarkSubCommand()
    {
        Title = "[Mapper Mark]";
        Description = "Drop a tagged landmark at current position during Manual Walk Mode";
        CallPrefix = $"{CommandManager.CommandPrefix}mapper mark";
        AddParameter(new StringSubCommandParameter("label", "label", true));
    }

    public override void Execute(ICharacter character, string triggerArgument,
        IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var label = parameters["label"].As<string>();
        var success = DevMapperService.Instance.RecordMark(
            character.Id, label, character.Transform.World.Position, character.Transform.World.Rotation.Z);

        if (success)
            SendColorMessage(messageOutput, Color.LawnGreen, $"[Mapper] Marked landmark: '{label}' at current position.");
        else
            SendColorMessage(messageOutput, Color.Red, "[Mapper] Not currently recording. Use /mapper walk <name> first.");
    }
}
