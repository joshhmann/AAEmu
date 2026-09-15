using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Progression cursor: Tier 0 path order, satisfaction rules, persistence
/// round-trip, fail-closed invalid JSON.
/// </summary>
[NotInParallel]
public class BotProgressionCursorTests
{
    [Test]
    public async Task Tier0Path_OrdersDesignBeforeSeedBeforeYield()
    {
        var path = BotProgressionCursor.Tier0Path();

        await Assert.That(path.Count).IsEqualTo(4);
        await Assert.That(path[0].QuestId).IsEqualTo(4438u);
        await Assert.That(path[1].Copper).IsEqualTo(100L);
        await Assert.That(path[2].ItemTemplateId).IsEqualTo(15659u);
        await Assert.That(path[3].ItemTemplateId).IsEqualTo(7992u);
    }

    [Test]
    public async Task Advance_SatisfiedMilestones_SkipsInOrder()
    {
        var cursor = new BotProgressionCursor.CursorState(BotProgressionCursor.Tier0Path(), 0, "fresh");

        var advanced = BotProgressionCursor.Advance(cursor, copper: 500, level: 7,
            new Dictionary<uint, int>(), new HashSet<uint> { 4438 }, "wake");

        await Assert.That(advanced.Position).IsEqualTo(2);
        await Assert.That(advanced.Current?.ItemTemplateId).IsEqualTo(15659u);
    }

    [Test]
    public async Task Advance_UnsatisfiedHead_StaysPut()
    {
        var cursor = new BotProgressionCursor.CursorState(BotProgressionCursor.Tier0Path(), 0, "fresh");

        var advanced = BotProgressionCursor.Advance(cursor, copper: 0, level: 1,
            new Dictionary<uint, int>(), new HashSet<uint>(), "wake");

        await Assert.That(advanced.Position).IsEqualTo(0);
        await Assert.That(advanced.UpdatedReason).IsEqualTo("fresh");
    }

    [Test]
    public async Task RoundTrip_SerializeDeserialize_PreservesPosition()
    {
        var cursor = new BotProgressionCursor.CursorState(BotProgressionCursor.Tier0Path(), 2, "wake-3");

        var restored = BotProgressionCursor.Deserialize(BotProgressionCursor.Serialize(cursor));

        await Assert.That(restored.Position).IsEqualTo(2);
        await Assert.That(restored.Milestones.Count).IsEqualTo(4);
        await Assert.That(restored.UpdatedReason).IsEqualTo("wake-3");
    }

    [Test]
    public async Task Deserialize_EmptyOrCorrupt_FallsBackToFreshPath()
    {
        var empty = BotProgressionCursor.Deserialize("");
        var corrupt = BotProgressionCursor.Deserialize("{not json");

        await Assert.That(empty.Position).IsEqualTo(0);
        await Assert.That(empty.Milestones.Count).IsEqualTo(4);
        await Assert.That(corrupt.Position).IsEqualTo(0);
        await Assert.That(corrupt.UpdatedReason).IsEqualTo("unparseable");
    }
}
