using NLog;
using NLog.Config;
using NLog.Targets;

using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.NPChar;

/// <summary>
/// PB-005 remedies C+A — contract of <see cref="NpcGroundingPolicy"/> and its use from
/// NpcSpawnerNpc.SpawnNpc:
/// - positive offsets at or above 2 m clamp to terrain;
/// - sub-threshold and negative offsets preserve source z (cave/interior limitation);
/// - fly/swim (CanFly) and intentional-floater-whitelisted templates are exempt;
/// - every clamp logs a throttled warning naming template + coords;
/// - whitelist decisions match the audit's evidence (aerial/water/structure IN,
///   frozen-z Hasla batch OUT).
/// </summary>
public class NpcGroundingPolicyTests
{
    // ------------------------------------------------------------- clamp core

    [Test]
    public async Task ResolveSpawnZ_SevereFloat_ClampsToGround()
    {
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, spawnerZ: 538.70f, groundZ: 355.15f, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
        await Assert.That(resolvedZ).IsEqualTo(355.15f);
    }

    [Test]
    public async Task ResolveSpawnZ_SevereSubmersion_PreservesSourceZ()
    {
        // Raw terrain cannot distinguish a cave/interior floor from bad submerged data.
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, spawnerZ: 320.76f, groundZ: 591.01f, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.KeptSourceZ);
        await Assert.That(resolvedZ).IsEqualTo(320.76f);
    }

    [Test]
    public async Task ResolveSpawnZ_SubThresholdDelta_KeepsSourceZ()
    {
        // 1.9 m offset: below default 2 m severity — roads/decks make this legitimate.
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, spawnerZ: 254.60f, groundZ: 252.70f, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.KeptSourceZ);
        await Assert.That(resolvedZ).IsEqualTo(254.60f);
    }

    [Test]
    public async Task ResolveSpawnZ_ExactlyAtThreshold_Clamps()
    {
        // Policy: deltas BELOW severity keep source z; delta == severity clamps.
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, spawnerZ: 254.70f, groundZ: 252.70f, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
        await Assert.That(resolvedZ).IsEqualTo(252.70f);
    }

    [Test]
    public async Task ResolveSpawnZ_FlyOrSwim_NeverClamped()
    {
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: true, spawnerZ: 538.70f, groundZ: 355.15f, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.Exempted);
        await Assert.That(resolvedZ).IsEqualTo(538.70f);
    }

    [Test]
    public async Task ResolveSpawnZ_NoUsableGroundSample_KeepsSourceZ()
    {
        // GetHeight returns the 0 sentinel on exception / out-of-bounds cell; snapping to it
        // would drop the NPC into the void.
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, spawnerZ: 538.70f, groundZ: 0f, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.NoGroundSample);
        await Assert.That(resolvedZ).IsEqualTo(538.70f);
    }

    // ------------------------------------------------------------- whitelist

    [Test]
    public async Task Whitelist_AerialAndWaterSpecies_AreExempt()
    {
        // Purple Falcon (61 severe spawns, movement_id != 2), Ocean Razorbeak (+100 m ocean-surface drift)
        foreach (var tpl in new uint[] { 1243, 8616, 10820, 8608, 8609 })
            await Assert.That(NpcGroundingPolicy.IsIntentionalFloater(tpl)).IsTrue();
    }

    [Test]
    public async Task Whitelist_StructureDwellers_AreExempt()
    {
        // Two Crowns decks / Salphira temple floor / Seven Bridges / Blacksail ship / docks & markets
        foreach (var tpl in new uint[] { 11849, 11844, 11915, 10142, 5000, 2921, 872, 2408 })
            await Assert.That(NpcGroundingPolicy.IsIntentionalFloater(tpl)).IsTrue();
    }

    [Test]
    public async Task Whitelist_FrozenZHaslaBatch_NotExempt_ClampMustFixIt()
    {
        // e_hasla Citizens/Maid + Ravra carry frozen z=538.x over 355–430 terrain (audit §5a):
        // genuinely bad data — must remain subject to the clamp.
        foreach (var tpl in new uint[] { 12335, 12336, 12337, 12340, 12341, 12339, 9631 })
        {
            await Assert.That(NpcGroundingPolicy.IsIntentionalFloater(tpl)).IsFalse();
            var action = NpcGroundingPolicy.ResolveSpawnZ(tpl, canFly: false, spawnerZ: 538.70f, groundZ: 355.15f, out _);
            await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
        }
    }

    [Test]
    public async Task Whitelist_GuardSentryFamilies_NotExempt()
    {
        // Their severe mass carries the same Hasla frozen-z signature (z=538.6 flat across
        // scattered coords); elsewhere they measure terrain-grounded so the clamp is a no-op.
        foreach (var tpl in new uint[] { 10791, 10793, 8175, 8176, 8172, 8179, 8242, 8243 })
            await Assert.That(NpcGroundingPolicy.IsIntentionalFloater(tpl)).IsFalse();
    }

    [Test]
    public async Task Whitelist_PlainGroundNpcs_NotExempt()
    {
        foreach (var tpl in new uint[] { 10082, 7054, 2573, 8030, 9703 })
            await Assert.That(NpcGroundingPolicy.IsIntentionalFloater(tpl)).IsFalse();
    }

    // ------------------------------------------------------------- threshold

[Test]
    public async Task ClampSeverity_IsExactlyTwoMeters()
    {
        await Assert.That(NpcGroundingPolicy.ClampSeverityM).IsEqualTo(2f);
    }


    // ------------------------------------------------------------- warning telemetry

    [Test]
    [NotInParallel]
    public async Task ReportClamp_EmitsWarningNamingTemplateAndCoords_AndThrottles()
    {
        var target = new MemoryTarget { Layout = "${level:uppercase=true}|${logger}|${message}" };
        var previousConfig = LogManager.Configuration;
        LogManager.Configuration = new LoggingConfiguration();
        LogManager.Configuration.AddRuleForAllLevels(target, "AAEmu.Game.Models.Game.NPChar.NpcGroundingPolicy");
        LogManager.ReconfigExistingLoggers();

        try
        {
            NpcGroundingPolicy.ResetWarnThrottleForTests(TimeSpan.FromMinutes(1));
            target.Logs.Clear();

            NpcGroundingPolicy.ReportClamp(12335, x: 30011.74f, y: 8709.93f, spawnerZ: 538.70f, groundZ: 355.15f);
            NpcGroundingPolicy.ReportClamp(12339, x: 29966.42f, y: 8730.85f, spawnerZ: 538.80f, groundZ: 366.28f);

            await Assert.That(target.Logs.Count).IsEqualTo(1); // second call inside window suppressed
            var line = target.Logs[0];
            await Assert.That(line).Contains("355.1");


            // Window elapsed -> next emission aggregates the suppressed count.
            NpcGroundingPolicy.ExpireWarnThrottleForTests();
            NpcGroundingPolicy.ReportClamp(9631, x: 28904.67f, y: 7679.68f, spawnerZ: 614.70f, groundZ: 472.59f);
            await Assert.That(target.Logs.Count).IsEqualTo(2);
            await Assert.That(target.Logs[1]).Contains("1 further clamps suppressed");
        }
        finally
        {
            LogManager.Configuration = previousConfig ?? new LoggingConfiguration();
            LogManager.ReconfigExistingLoggers();
            NpcGroundingPolicy.ResetWarnThrottleForTests();
        }
    }
}
