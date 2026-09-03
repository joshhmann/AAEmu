using System.Drawing;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Mapper;

/// <summary>
/// /mapper stop — finalize Manual Walk Mode and export JSON route + .path file.
/// </summary>
public class MapperStopSubCommand : SubCommandBase
{
    public MapperStopSubCommand()
    {
        Title = "[Mapper Stop]";
        Description = "Finalize Manual Walk Mode, saving JSON and .path route files";
        CallPrefix = $"{CommandManager.CommandPrefix}mapper stop";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        var summary = DevMapperService.Instance.Stop(character.Id);
        SendColorMessage(messageOutput, summary.Success ? Color.LawnGreen : Color.Red, summary.Message);
    }
}
