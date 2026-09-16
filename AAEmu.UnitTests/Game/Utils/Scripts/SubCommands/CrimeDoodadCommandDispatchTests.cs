using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Scripts.Commands;

namespace AAEmu.UnitTests.Game.Utils.Scripts.SubCommands;

public class CrimeDoodadCommandDispatchTests
{
    [Test]
    public async Task CrimeAndDoodadCommands_ResolveUnderDistinctKeys()
    {
        var crime = new CrimeCmd();
        var doodad = new DoodadCmd();
        crime.OnLoad();
        doodad.OnLoad();
        try
        {
            ICommand crimeResolved = CommandManager.Instance.GetCommandInterfaceByName("crime");
            ICommand doodadResolved = CommandManager.Instance.GetCommandInterfaceByName("doodad");
            await Assert.That(ReferenceEquals(crimeResolved, crime)).IsTrue();
            await Assert.That(ReferenceEquals(doodadResolved, doodad)).IsTrue();
        }
        finally
        {
            CommandManager.Instance.Clear();
        }
    }

    [Test]
    public async Task CrimeCommand_AdvertisesReachableCrimeKey()
    {
        var crime = new CrimeCmd();
        await Assert.That(crime.CommandNames[0]).IsEqualTo("crime");
        await Assert.That(crime.CallPrefix).IsEqualTo($"{CommandManager.CommandPrefix}crime");
        await Assert.That(crime.GetCommandLineHelp().Contains("create")).IsTrue();
    }
}
