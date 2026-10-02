using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class SusManagerTests
{
    [Test]
    public async Task Constructor_WithMockedWorldManager_DoesNotThrow()
    {
        var mockWorldManager = Mock.Of<IWorldManager>();

        var manager = new SusManager(mockWorldManager.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockWorldManager);
    }

    [Test]
    public async Task ResetAnalyzePlayerDeltaMovement_RemovesOnlyTheNamedPlayersEntry()
    {
        // The real contract: the reset exists so a teleport does not leave a
        // stale last-position making the next movement sample look like a
        // speed hack. That is observable on LastPlayerPositions itself.
        //
        // The old version called the reset on an instance whose map was empty
        // and asserted only Mock.VerifyNoOtherCalls — which passes trivially,
        // and stayed GREEN with the method gutted to `{ }`.
        var manager = new SusManager(Mock.Of<IWorldManager>().Object);
        const uint resetPlayerId = 42u;
        const uint otherPlayerId = 43u;

        // Seed the map the way AnalyzePlayerDeltaMovement would, then check the
        // reset removes exactly one entry.
        var positions = GetLastPlayerPositions(manager);
        positions[resetPlayerId] = (Vector3.One, 0f);
        positions[otherPlayerId] = (new Vector3(2, 2, 2), 0f);

        // Act
        manager.ResetAnalyzePlayerDeltaMovement(resetPlayerId);

        // Assert
        await Assert.That(positions.ContainsKey(resetPlayerId)).IsFalse()
            .Because("the reset must drop the named player's last position so the next sample re-baselines");
        await Assert.That(positions.ContainsKey(otherPlayerId)).IsTrue()
            .Because("the reset is per-player; other players' baselines must survive");
        await Assert.That(positions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ResetAnalyzePlayerDeltaMovement_OnUntrackedPlayer_IsANoOp()
    {
        // The negative half: a reset for a player with no entry must not throw
        // and must not remove anybody ELSE's entry.
        var manager = new SusManager(Mock.Of<IWorldManager>().Object);
        var positions = GetLastPlayerPositions(manager);
        positions[43u] = (new Vector3(2, 2, 2), 0f);

        manager.ResetAnalyzePlayerDeltaMovement(999u);

        await Assert.That(positions.ContainsKey(43u)).IsTrue();
        await Assert.That(positions.Count).IsEqualTo(1);
    }

    /// <summary>
    /// LastPlayerPositions is a private auto-property on SusManager; the test
    /// reads it by reflection rather than adding a production accessor purely
    /// to make itself assertable.
    /// </summary>
    private static Dictionary<uint, (System.Numerics.Vector3 pos, float skipTime)> GetLastPlayerPositions(SusManager manager)
    {
        var prop = typeof(SusManager).GetProperty(
            "LastPlayerPositions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Dictionary<uint, (System.Numerics.Vector3 pos, float skipTime)>)prop.GetValue(manager)!;
    }
}