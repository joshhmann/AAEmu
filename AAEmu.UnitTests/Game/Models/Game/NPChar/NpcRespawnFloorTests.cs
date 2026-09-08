using System.Text.Json;

using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Floor tests for <see cref="NpcSpawnerNpc.ApplyRespawnFloor"/> (npc-respawn-floors).
///
/// Respawn delay trace under test:
/// <c>NpcSpawnerNpc.SpawnNpc</c> rolls
/// <c>Spawner.RespawnTime = Rand.Next(Template.SpawnDelayMin, Template.SpawnDelayMax)</c>,
/// then <c>ApplyRespawnFloor</c> runs, and later <c>NpcSpawner.DoDespawn</c> schedules
/// <c>npc.Respawn = deathTime.AddSeconds(RespawnTime)</c> — so the floored value is what
/// the world actually waits. The floor only ever raises: spawners whose authored delays
/// exceed <see cref="WorldConfig.NpcRespawnPlaceholderThreshold"/> return early.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // seeds the shared AppConfiguration singleton — same convention as NpcMoveTowardsTests
public class NpcRespawnFloorTests
{
    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task Placeholder10s_NormalGrade_RaisedToNormalFloor()
    {
        var npc = CreateNpc(10f, 10f, 10, NpcGradeType.Normal);

        NpcSpawnerNpc.ApplyRespawnFloor(npc);

        await Assert.That(npc.Spawner.RespawnTime).IsEqualTo(90);
    }

    [Test]
    public async Task Placeholder10s_EliteGrade_RaisedToEliteFloor()
    {
        var npc = CreateNpc(10f, 10f, 10, NpcGradeType.Strong);

        NpcSpawnerNpc.ApplyRespawnFloor(npc);

        await Assert.That(npc.Spawner.RespawnTime).IsEqualTo(300);
    }

    [Test]
    public async Task Authored235s_PassesThroughUnchanged()
    {
        var npc = CreateNpc(235f, 235f, 235, NpcGradeType.Normal);

        NpcSpawnerNpc.ApplyRespawnFloor(npc);

        await Assert.That(npc.Spawner.RespawnTime).IsEqualTo(235);
    }

    [Test]
    public async Task AuthoredAboveThreshold_NeverLoweredByFloor()
    {
        // Even a floor above the authored value must not shorten a real timer:
        // authored spawners return before the floor is consulted.
        AppConfiguration.Instance.World.NpcRespawnMinSeconds = 1000;
        var npc = CreateNpc(600f, 600f, 600, NpcGradeType.Normal);

        NpcSpawnerNpc.ApplyRespawnFloor(npc);

        await Assert.That(npc.Spawner.RespawnTime).IsEqualTo(600);
    }

    [Test]
    public async Task WorldConfig_Defaults_MatchDocumentedFloors()
    {
        var cfg = new WorldConfig();

        await Assert.That(cfg.NpcRespawnMinSeconds).IsEqualTo(90);
        await Assert.That(cfg.NpcRespawnMinSecondsElite).IsEqualTo(300);
        await Assert.That(cfg.NpcRespawnEliteMinGrade).IsEqualTo(7);
        await Assert.That(cfg.NpcRespawnPlaceholderThreshold).IsEqualTo(10f);
    }

    [Test]
    public async Task WorldConfig_BindsFromJson_AndFallsBackToDefaultsWhenAbsent()
    {
        // Keys present in World.json bind by name ...
        var bound = JsonSerializer.Deserialize<WorldConfig>(
            """{"NpcRespawnMinSeconds":120,"NpcRespawnMinSecondsElite":400,"NpcRespawnEliteMinGrade":8,"NpcRespawnPlaceholderThreshold":12.5}""");

        await Assert.That(bound.NpcRespawnMinSeconds).IsEqualTo(120);
        await Assert.That(bound.NpcRespawnMinSecondsElite).IsEqualTo(400);
        await Assert.That(bound.NpcRespawnEliteMinGrade).IsEqualTo(8);
        await Assert.That(bound.NpcRespawnPlaceholderThreshold).IsEqualTo(12.5f);

        // ... while absent keys keep the property-initializer defaults.
        var empty = JsonSerializer.Deserialize<WorldConfig>("{}");

        await Assert.That(empty.NpcRespawnMinSeconds).IsEqualTo(90);
        await Assert.That(empty.NpcRespawnMinSecondsElite).IsEqualTo(300);
        await Assert.That(empty.NpcRespawnEliteMinGrade).IsEqualTo(7);
        await Assert.That(empty.NpcRespawnPlaceholderThreshold).IsEqualTo(10f);
    }

    private static Npc CreateNpc(float delayMin, float delayMax, int respawnTime, NpcGradeType grade)
    {
        return new Npc
        {
            Template = new NpcTemplate { NpcGradeId = grade },
            Spawner = new NpcSpawner
            {
                Template = new NpcSpawnerTemplate { SpawnDelayMin = delayMin, SpawnDelayMax = delayMax },
                RespawnTime = respawnTime,
            },
        };
    }
}
