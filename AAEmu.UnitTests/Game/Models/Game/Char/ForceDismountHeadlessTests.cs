using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// ForceDismount null-connection defect (P0 hotfix t_468d6360): prod CT 133
/// logged a TickManager+TickEventHandler NRE loop (93 occurrences in 3h,
/// stack Character.ForceDismount → LeaveWorldTask → ActiveRegionTick →
/// TickEventHandler.Invoke) that froze the world tick and blocked Josh's
/// in-game sighting of the roaming citizen bots.
///
/// Root cause chain (confirmed by reading the code):
///  1. Headless bot characters have NO Connection — ActivateHeadless →
///     EnterWorld(character, connection: null) (CharacterLifecycleService.cs:100).
///  2. WorldManager.ActiveRegionTick iterates GetAllCharacters() — bots are
///     in _characters via TryAddCharacter — and calls OnActiveRegionTick.
///  3. Character.OnActiveRegionTick → CheckPlayerInactivity: bots never
///     receive packets, so LastPacketActivityTime goes stale after the 2-min
///     window → LeaveWorldTask(null, CharacterSelect, this) fires.
///  4. LeaveWorldTask → activeChar.ForceDismount() → Character.cs:2126
///     `Connection.ActiveChar.Bonding?.GetOwner()` — Connection is null on
///     headless bots → NullReferenceException. The NRE aborts the world tick
///     iteration mid-loop (TickEventHandler catches + logs it), so movement
///     broadcasts stop for every entity — the "no movement in-world" report.
///
/// Fix layers (both in Character.cs):
///  - ForceDismount / ForceDismountAndDespawn: use `this` instead of
///    `Connection.ActiveChar` (identical for connected chars; null-safe for
///    headless ones — Bonding is null, chair block skipped).
///  - CheckPlayerInactivity: skip characters with no Connection — the
///    inactivity sweep is for detecting crashed/silent PLAYER clients; a
///    headless bot's lifecycle is owned by the bot manager, not the sweep.
///    Without this guard the bot would be force-leave'd (despawned) every 2
///    minutes even after the NRE fix.
///
/// Rig mirrors the production bot shape: real WorldInstance with MateManager
/// + SlaveManager assigned (prod assigns them at world creation), Connection
/// left null, no attachment state. Fail-before: ForceDismount NREs at
/// :2126; pass-after: returns false with no throw.
/// </summary>
public class ForceDismountHeadlessTests
{
    private const uint BotObjId = 0x1001;

    private static Character BuildHeadlessBotCharacter()
    {
        var character = new Character(new UnitCustomModelParams())
        {
            Id = 1,
            Name = "Citizen01",
            Level = 1,
            Race = Race.Nuian,
            Gender = Gender.Male
        };
        character.ObjId = BotObjId;

        // Prod world shape: MateManager + SlaveManager are assigned right
        // after world creation (WorldManager.cs:528 area), so GetIsMounted
        // lookups return null instead of NREing on the manager being null.
        var world = new WorldInstance(new WorldTemplate
        {
            Id = 0,
            Name = "headless_world",
            ZoneKeys = [],
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        }, 0, true, 1);
        world.MateManager = new MateManager(world);
        world.SlaveManager = new SlaveManager(world);
        // Set the parent world field directly — the public ParentWorld setter
        // loops through Transform.InstanceId → WorldManager.Instance.GetWorld,
        // a DI singleton absent from the hermetic rig (same pattern as
        // HeadlessSession.SetParentWorld).
        typeof(GameObject).GetField("_parentWorld",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(character, world);

        // Connection deliberately left null — the headless bot shape
        // (ActivateHeadless passes connection: null and never assigns one).
        return character;
    }

    [Test]
    public async Task ForceDismount_HeadlessBot_NoConnection_DoesNotThrow()
    {
        var bot = BuildHeadlessBotCharacter();

        // Was NRE: Character.cs:2126 `Connection.ActiveChar.Bonding?.GetOwner()`
        // — Connection is null on headless-provisioned bots (prod stack:
        // ForceDismount → LeaveWorldTask → ActiveRegionTick, 93×/3h).
        var result = bot.ForceDismount();

        await Assert.That(result).IsFalse();
        // Bonding is never assigned for headless bots — the chair-detach
        // block must be skipped entirely, not reached via a null Connection.
        await Assert.That(bot.Bonding).IsNull();
    }

    [Test]
    public async Task ForceDismountAndDespawn_HeadlessBot_NoConnection_DoesNotThrow()
    {
        var bot = BuildHeadlessBotCharacter();

        // Same null-Connection class at Character.cs:2143
        // (`Connection.ActiveChar.ObjId`) — sibling method, same fix.
        var result = bot.ForceDismountAndDespawn();

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CheckPlayerInactivity_HeadlessBot_StaleActivity_DoesNotSweep()
    {
        var bot = BuildHeadlessBotCharacter();
        // Bots never receive packets (no client), so LastPacketActivityTime
        // goes stale past the 2-minute window — the exact trigger that fired
        // LeaveWorldTask → ForceDismount → NRE on prod every ~2 minutes.
        bot.LastPacketActivityTime = DateTime.UtcNow.AddMinutes(-5);

        var method = typeof(Character).GetMethod("CheckPlayerInactivity",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CheckPlayerInactivity not found");

        // Before the guard: the sweep resets LastPacketActivityTime (line
        // 3015) and calls LeaveWorldTask(null, ...) — observable mutation +
        // NRE inside ForceDismount. After the guard: early return, no state
        // mutation, no leave attempt.
        method.Invoke(bot, [TimeSpan.FromSeconds(1)]);

        await Assert.That(bot.LastPacketActivityTime < DateTime.UtcNow.AddMinutes(-4)).IsTrue();
    }
}
