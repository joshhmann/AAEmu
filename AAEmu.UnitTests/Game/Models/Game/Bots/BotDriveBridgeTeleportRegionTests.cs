using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using BotDriveBridge = AAEmu.Game.Models.Game.Bots.BotDriveBridge;
using Character = AAEmu.Game.Models.Game.Char.Character;
using GameObject = AAEmu.Game.Models.Game.World.GameObject;
using HeadlessSession = AAEmu.Game.Models.Game.Bots.HeadlessSession;
using WorldConfig = AAEmu.Game.Models.Game.WorldConfig;

using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.Bots;

/// <summary>
/// PB-006 regression seam: BotDriveBridge teleports mutated
/// <c>Transform.Local.Position</c> directly, which left the character
/// registered in its PREVIOUS region. Every proximity broadcast at the
/// destination (ship SCOneUnitMovementPacket included) then resolved zero
/// receivers — units stopped replicating to the teleported character even
/// though the physics thread ticked and BroadcastPacket executed.
///
/// These tests defend the contract of
/// <see cref="BotDriveBridge.TeleportWithRegionSync"/>: after a test-control
/// teleport the character MUST be discoverable through the normal
/// <c>WorldManager.GetAround</c> receiver resolution from its new position.
/// </summary>
[NotInParallel] // mutates the global WorldManager._worlds registry (scoped add/remove)
public class BotDriveBridgeTeleportRegionTests
{
    /// <summary>Scoped world registration — same pattern as TransformAllocationTests.</summary>
    private static async Task WithRegisteredWorldAsync(HeadlessSession session, Func<Task> body)
    {
        var worldsField = typeof(WorldManager).GetField("_worlds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)worldsField.GetValue(WorldManager.Instance)!;
        worlds.TryAdd(session.World.Id, session.World);
        try
        {
            await body();
        }
        finally
        {
            worlds.TryRemove(session.World.Id, out _);
        }
    }

    /// <summary>
    /// Re-parents an actor onto ANOTHER actor's headless world so two
    /// characters share one region grid (mirrors the rig's WireMateToWorld
    /// bypass: pre-set the transform instance-id backing field so
    /// Region.AddObject never resolves the shared registry).
    /// </summary>
    private static void ShareWorld(Character character, HeadlessSession session)
    {
        typeof(GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(character, session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(character.Transform, session.World.Id);
    }

    private static (Character Character, HeadlessSession Session) CreateCharacter(string name)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        actor.Character.Transform.FinalizeTransform();
        return (actor.Character, session);
    }

    private static void PlaceAt(Character character, float x, float y)
    {
        character.Transform.Local.Position = new Vector3(x, y, 0f);
        character.Transform.FinalizeTransform();
        WorldManager.Instance.AddVisibleObject(character);
    }

    public const float DestX = 1500f; // sector (23, ...) of the 2x2-cell (2048 m) rig world
    public const float DestY = 1500f;

    [Test]
    public async Task RawPositionMutation_CharacterRemainsInStaleRegion_BroadcastsCannotReachIt()
    {
        // Reproduces the PB-006 defect shape: direct Transform mutation is NOT
        // enough — region membership stays behind, so a unit standing right
        // next to the character's new coordinates gets ZERO broadcast receivers.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (a, sessionA) = CreateCharacter("teleport-stale-a");
        var (b, _) = CreateCharacter("teleport-stale-b");
        ShareWorld(b, sessionA);

        await WithRegisteredWorldAsync(sessionA, async () =>
        {
            PlaceAt(b, DestX, DestY);          // the "boat": registered at the destination
            PlaceAt(a, 100f, 100f);            // character starts far away

            // The OLD bridge op behavior: raw position write only — no
            // FinalizeTransform / AddVisibleObject ever runs for an idle
            // character, so nothing re-registers its region.
            a.Transform.Local.Position = new Vector3(DestX + 2f, DestY, 0f);

            var aroundB = new List<Character>();
            WorldManager.GetAround(b, aroundB);

            await Assert.That(a.Region.Id).IsNotEqualTo(b.Region.Id)
                .Because("a raw Transform.Local.Position write must leave the character registered " +
                         "in its stale source region — this is exactly how the rowboat E2E lost the " +
                         "SCOneUnitMovementPacket stream (receivers=0 for every physics tick)");
            await Assert.That(aroundB.Contains(a)).IsFalse()
                .Because("with stale region membership the destination-side broadcast receiver " +
                         "resolution must NOT find the character");
        });
    }

    [Test]
    public async Task TeleportWithRegionSync_CharacterReRegistered_DestinationBroadcastsResolveIt()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (a, sessionA) = CreateCharacter("teleport-sync-a");
        var (b, _) = CreateCharacter("teleport-sync-b");
        ShareWorld(b, sessionA);

        await WithRegisteredWorldAsync(sessionA, async () =>
        {
            PlaceAt(b, DestX, DestY);
            PlaceAt(a, 100f, 100f);

            // The FIXED bridge behavior.
            BotDriveBridge.TeleportWithRegionSync(a, new Vector3(DestX + 2f, DestY, 0f));

            await Assert.That(a.Region.Id).IsEqualTo(b.Region.Id)
                .Because("after the region-synced teleport the character must be a member of the " +
                         "destination region");

            var aroundB = new List<Character>();
            WorldManager.GetAround(b, aroundB);
            await Assert.That(aroundB.Contains(a)).IsTrue()
                .Because("units at the destination (the ship broadcasting SCOneUnitMovementPacket " +
                         "every physics tick) must resolve the teleported character as a receiver " +
                         "through the normal WorldManager.GetAround path");
        });
    }
}
