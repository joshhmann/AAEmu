using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class TestSlave : ICommand
{
    public string[] CommandNames { get; set; } = ["testslave", "test_slave"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "";
    }

    public string GetCommandHelpText()
    {
        return "Spawns a test slave";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        // Spawn through the same SlaveManager.Create path as the retail
        // item-based summon (/slave spawn): it names the slave from its
        // template (a null Name crashes G2C serialization with
        // ArgumentNullException in PacketStream.Write(string) — the client
        // sees this as a packet error), equips the template's initial item
        // pack (clothes/parts), spawns bound doodads, and applies bonuses.
        // Hand-rolling a Slave here skipped ALL of that.
        character.ParentWorld.SlaveManager.Create(character, null, 73);
    }
}
