using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// CharacterLifecycleService rig (ARCHITECTURE_REVIEW H3 extraction, slice 3).
///
/// The full ActivateHuman / ActivateHeadless path needs a booted server
/// (MySQL + game-data singletons — Character.Load hits MySQL), so this rig
/// locks the hermetic contract: singleton resolvability from the packet path
/// (Activator.CreateInstance, no DI), preserved null guards, and the
/// no-packet headless sink. Live-path evidence rides the M2b E2E golden
/// route (human character entry) and the slice-4 provisioning round-trip
/// (headless, t_302b67bf).
/// </summary>
public class CharacterLifecycleServiceTests
{
    [Test]
    public async Task Instance_ResolvesWithoutDI()
    {
        // The packet path constructs handlers via Activator.CreateInstance —
        // the service must resolve standalone like every Singleton<T> manager.
        await Assert.That(CharacterLifecycleService.Instance).IsNotNull();
        await Assert.That(CharacterLifecycleService.Instance).IsAssignableTo<ICharacterLifecycleService>();
    }

    [Test]
    public void Deactivate_NullCharacter_DoesNotThrow()
    {
        // Null guard preserved from GameConnection.SaveAndRemoveFromWorld.
        CharacterLifecycleService.Instance.Deactivate(null!, CharacterLifecycleReason.Disconnect);
    }

    [Test]
    public void CharacterWithoutConnection_SendPacket_DoesNotThrow()
    {
        // Headless contract: a Connection-less Character is a no-op sender
        // (Unit.SendPacket → Connection?.SendPacket, Unit.cs:801-804), so the
        // gameplay engine's packets never reach a client during headless
        // activation — no fake client or network socket required.
        var character = new Character(new UnitCustomModelParams());

        character.SendPacket(new CSSelectCharacterPacket());
    }
}
