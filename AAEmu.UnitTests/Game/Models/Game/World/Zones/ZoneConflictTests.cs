using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.UnitTests.Game.Models.Game.World.Zones;

/// <summary>
/// Test double that records state transitions without touching the
/// TaskManager / WorldManager singletons (headless-safe).
/// </summary>
public sealed class TestableZoneConflict : ZoneConflict
{
    public List<ZoneConflictType> BroadcastStates { get; } = [];

    public TestableZoneConflict() : base(new ZoneGroup())
    {
    }

    public override void SendSwitchZoneState()
    {
        BroadcastStates.Add(CurrentZoneState);
    }

    /// <summary>Exposes the protected setter for arranging test states.</summary>
    public void ForceState(ZoneConflictType state) => SetState(state);
}

public class ZoneConflictTests
{
    private static TestableZoneConflict CreateZone(int[] numKills = null)
    {
        var conflict = new TestableZoneConflict();
        if (numKills != null)
            for (var i = 0; i < numKills.Length; i++)
                conflict.NumKills[i] = numKills[i];
        return conflict;
    }

    #region Kill-counter escalation

    [Test]
    public async Task AddZoneKill_TensionWithKillCounter_EscalatesThroughStages()
    {
        var conflict = CreateZone([10, 50, 100, 200, 400]);

        // Each batch crosses exactly one threshold — one stage per batch
        for (var i = 0; i < 11; i++)
            conflict.AddZoneKill();
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Danger);

        for (var i = 0; i < 45; i++)
            conflict.AddZoneKill();
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Dispute);

        for (var i = 0; i < 60; i++)
            conflict.AddZoneKill();
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Unrest);

        for (var i = 0; i < 100; i++)
            conflict.AddZoneKill();
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Crisis);
    }

    [Test]
    public async Task AddZoneKill_ReachingConflict_ResetsCounterAndIgnoresFurtherKills()
    {
        var conflict = CreateZone([1, 1, 1, 1, 1]);

        // Push through all five escalation thresholds into Conflict
        for (var i = 0; i < 6; i++)
            conflict.AddZoneKill();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);

        // Conflict+ states ignore the kill counter entirely
        conflict.AddZoneKill();
        conflict.AddZoneKill();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);
        await Assert.That(conflict.KillCount).IsEqualTo(0u);
    }

    [Test]
    public async Task AddZoneKill_NoKillCounter_DoesNotEscalate()
    {
        var conflict = CreateZone(); // ocean zone: no kill-counter mechanic

        for (var i = 0; i < 10; i++)
            conflict.AddZoneKill();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Tension);
        await Assert.That(conflict.BroadcastStates).IsEmpty();
    }

    #endregion

    #region Timer-driven cycle

    [Test]
    public async Task ForceNextState_WarWithPeaceMin_AdvancesToPeace()
    {
        var conflict = CreateZone([70, 70, 70, 70, 70]);
        conflict.ConflictMin = 5;
        conflict.WarMin = 80;
        conflict.PeaceMin = 120;
        conflict.ForceState(ZoneConflictType.War);

        conflict.ForceNextState();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Peace);
    }

    [Test]
    public async Task ForceNextState_WarWithoutPeaceMin_ReturnsToConflict()
    {
        var conflict = CreateZone();
        conflict.PeaceMin = 0;
        conflict.WarMin = 10;
        conflict.ForceState(ZoneConflictType.War);

        conflict.ForceNextState();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);
    }

    [Test]
    public async Task ForceNextState_PeaceWithKillCounter_ReturnsToTension()
    {
        var conflict = CreateZone([70, 70, 70, 70, 70]);
        conflict.PeaceMin = 120;
        conflict.ForceState(ZoneConflictType.Peace);

        conflict.ForceNextState();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Tension);
        // Escalation stages have no timer until kills drive them
        await Assert.That(conflict.NextStateTime).IsEqualTo(DateTime.MinValue);
    }

    [Test]
    public async Task ForceNextState_PeaceOceanZone_CyclesBackToConflict()
    {
        var conflict = CreateZone(); // no kill counter
        conflict.ConflictMin = 5;
        conflict.PeaceMin = 240;
        conflict.ForceState(ZoneConflictType.Peace);

        conflict.ForceNextState();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);
        await Assert.That(conflict.NextStateTime).IsGreaterThanOrEqualTo(DateTime.UtcNow.AddMinutes(4));
    }

    [Test]
    public async Task SetState_ConflictWarPeace_SchedulesTimedTransition()
    {
        var before = DateTime.UtcNow;
        var conflict = CreateZone([70, 70, 70, 70, 70]);
        conflict.ConflictMin = 5;
        conflict.WarMin = 80;
        conflict.PeaceMin = 120;

        conflict.ForceState(ZoneConflictType.Tension);
        await Assert.That(conflict.NextStateTime).IsEqualTo(DateTime.MinValue);

        // Conflict → War → Peace each schedule their data-driven durations
        conflict.ForceState(ZoneConflictType.Conflict);
        await Assert.That(conflict.NextStateTime).IsGreaterThanOrEqualTo(before.AddMinutes(5));

        conflict.ForceState(ZoneConflictType.War);
        await Assert.That(conflict.NextStateTime).IsGreaterThanOrEqualTo(DateTime.UtcNow.AddMinutes(79));
        await Assert.That(conflict.NextStateTime).IsLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(81));

        conflict.ForceState(ZoneConflictType.Peace);
        await Assert.That(conflict.NextStateTime).IsGreaterThanOrEqualTo(DateTime.UtcNow.AddMinutes(119));
        await Assert.That(conflict.NextStateTime).IsLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(121));
    }

    [Test]
    public async Task StateTransitions_BroadcastEachChange()
    {
        // Kill-counter escalation in the pre-Conflict stages broadcasts every change;
        // Conflict/War/Peace ignore the counter (no further broadcasts from kills).
        var conflict = CreateZone([10, 50, 100, 200, 400]);

        for (var i = 0; i < 11; i++)
            conflict.AddZoneKill();
        await Assert.That(conflict.BroadcastStates).Count().IsEqualTo(1);
        await Assert.That(conflict.BroadcastStates[0]).IsEqualTo(ZoneConflictType.Danger);

        for (var i = 0; i < 49; i++)
            conflict.AddZoneKill();

        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Dispute);
        await Assert.That(conflict.BroadcastStates).Count().IsEqualTo(2);
        await Assert.That(conflict.BroadcastStates[1]).IsEqualTo(ZoneConflictType.Dispute);

        // At War the kill counter is ignored — only SetState itself broadcasts
        var countBeforeWar = conflict.BroadcastStates.Count;
        conflict.ForceState(ZoneConflictType.War);
        conflict.AddZoneKill();
        conflict.AddZoneKill();

        await Assert.That(conflict.BroadcastStates).Count().IsEqualTo(countBeforeWar + 1);
        await Assert.That(conflict.BroadcastStates[^1]).IsEqualTo(ZoneConflictType.War);
    }

    #endregion

    #region Peace protection (C6 / ZONE-01 enforcement predicate)

    [Test]
    public async Task IsPeaceProtectionActive_OnlyTrueInPeaceState()
    {
        foreach (var state in new[]
                 {
                     ZoneConflictType.Tension, ZoneConflictType.Danger, ZoneConflictType.Dispute,
                     ZoneConflictType.Unrest, ZoneConflictType.Crisis, ZoneConflictType.Conflict,
                     ZoneConflictType.War
                 })
        {
            var conflict = CreateZone();
            conflict.ForceState(state);
            await Assert.That(conflict.IsPeaceProtectionActive).IsFalse();
        }

        var peace = CreateZone();
        peace.ForceState(ZoneConflictType.Peace);
        await Assert.That(peace.IsPeaceProtectionActive).IsTrue();
    }

    [Test]
    public async Task BlocksPvpDamage_PeaceState_BlocksNonHostile_AllowsHostile()
    {
        var conflict = CreateZone();
        conflict.ForceState(ZoneConflictType.Peace);

        await Assert.That(conflict.BlocksPvpDamage(RelationState.Neutral)).IsTrue();
        await Assert.That(conflict.BlocksPvpDamage(RelationState.Friendly)).IsTrue();
        await Assert.That(conflict.BlocksPvpDamage(RelationState.Hostile)).IsFalse();
    }

    [Test]
    public async Task BlocksPvpDamage_NonPeaceStates_AllowAllRelations()
    {
        foreach (var state in new[]
                 {
                     ZoneConflictType.Tension, ZoneConflictType.Crisis,
                     ZoneConflictType.Conflict, ZoneConflictType.War
                 })
        {
            var conflict = CreateZone();
            conflict.ForceState(state);

            await Assert.That(conflict.BlocksPvpDamage(RelationState.Neutral)).IsFalse();
            await Assert.That(conflict.BlocksPvpDamage(RelationState.Friendly)).IsFalse();
            await Assert.That(conflict.BlocksPvpDamage(RelationState.Hostile)).IsFalse();
        }
    }

    [Test]
    public async Task BlocksPvpDamage_NullConflict_NeverBlocks()
    {
        // Zones without a conflict entry must fail open to current behavior
        await Assert.That(ZoneConflict.BlocksPvpDamage(null, RelationState.Neutral)).IsFalse();
        await Assert.That(ZoneConflict.BlocksPvpDamage(null, RelationState.Friendly)).IsFalse();
        await Assert.That(ZoneConflict.BlocksPvpDamage(null, RelationState.Hostile)).IsFalse();
    }

    #endregion
}
