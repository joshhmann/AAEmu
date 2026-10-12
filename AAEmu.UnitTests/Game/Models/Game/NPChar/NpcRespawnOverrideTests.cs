using System.Reflection;
using System.Text.Json;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public class NpcRespawnOverrideTests
{
    private WorldConfig _previous;

    [Before(Test)]
    public void Before() => _previous = AppConfiguration.Instance.World;

    [After(Test)]
    public void After() => AppConfiguration.Instance.World = _previous;

    private static (Npc Npc, NpcSpawner Spawner, SpawnManager Manager) Create(
        NpcGradeType grade, int originalDelay, int normalOverride = 15)
    {
        var (_, session) = GameplayActorTestRig.CreateActor("normal-respawn-interval");
        AppConfiguration.Instance.World = new WorldConfig
        {
            NpcRespawnNormalOverrideSeconds = normalOverride
        };
        session.World.SpawnManager = new SpawnManager(session.World);
        var npc = session.World.GetNpc(session.SpawnNpc(3475u))!;
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(npc, session.World);
        npc.Template = new NpcTemplate { Id = 3475u, NpcGradeId = grade, Scale = 1f };
        // Backdated death tests the native queue's due-time gate without sleeping.
        // The fixture does not perform a kill or grant loot/quest credit.
        npc.DeadTime = DateTime.UtcNow.AddSeconds(-16);
        var spawner = new NpcSpawner
        {
            ParentWorld = session.World,
            SpawnerId = 3372,
            UnitId = 3475,
            RespawnTime = originalDelay,
            Template = new NpcSpawnerTemplate(3372u, 3475u)
            {
                MaxPopulation = 3,
                SpawnDelayMin = originalDelay,
                SpawnDelayMax = originalDelay
            }
        };
        npc.Spawner = spawner;
        spawner.SpawnedNpcs[spawner.SpawnerId] = [npc];
        return (npc, spawner, session.World.SpawnManager);
    }

    private static HashSet<GameObject> Ready(SpawnManager manager)
        => (HashSet<GameObject>)typeof(SpawnManager)
            .GetMethod("GetRespawnsReady", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, null)!;

    [Test]
    [Arguments(NpcGradeType.Normal, 48, 15)]
    [Arguments(NpcGradeType.Normal, 90, 15)]
    [Arguments(NpcGradeType.Weak, 48, 15)]
    [Arguments(NpcGradeType.Elite, 45, 45)]
    [Arguments(NpcGradeType.BossA, 45, 45)]
    [Arguments(NpcGradeType.BossB, 45, 45)]
    [Arguments(NpcGradeType.BossC, 45, 45)]
    [Arguments(NpcGradeType.Strong, 45, 45)]
    [Arguments(NpcGradeType.BossS, 45, 45)]
    public async Task NativeDeathSchedule_ChangesOnlyOrdinaryGrades(
        NpcGradeType grade, int originalDelay, int expectedDelay)
    {
        var (npc, spawner, manager) = Create(grade, originalDelay);
        spawner.DoDespawn(npc);
        await Assert.That(npc.Respawn).IsEqualTo(npc.DeadTime.AddSeconds(expectedDelay));
        await Assert.That(Ready(manager).Contains(npc)).IsEqualTo(expectedDelay == 15);
        await Assert.That(spawner.RespawnTime).IsEqualTo(originalDelay);
        await Assert.That(spawner.Template.SpawnDelayMin).IsEqualTo((float)originalDelay);
        await Assert.That(spawner.Template.SpawnDelayMax).IsEqualTo((float)originalDelay);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task DisabledOverride_PreservesAuthoredSchedule(int normalOverride)
    {
        var (npc, spawner, manager) = Create(NpcGradeType.Normal, 48, normalOverride);
        spawner.DoDespawn(npc);
        await Assert.That(npc.Respawn).IsEqualTo(npc.DeadTime.AddSeconds(48));
        await Assert.That(Ready(manager)).DoesNotContain(npc);
    }

    [Test]
    public async Task MixedSpawner_PreservesOriginalSchedule()
    {
        var (npc, spawner, manager) = Create(NpcGradeType.Normal, 48);
        spawner.Template.Npcs.Add(new NpcSpawnerNpc(3372u, 3484u));
        spawner.DoDespawn(npc);
        await Assert.That(npc.Respawn).IsEqualTo(npc.DeadTime.AddSeconds(48));
        await Assert.That(Ready(manager)).DoesNotContain(npc);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DisabledOrFullSpawner_DoesNotSchedule(bool disabled)
    {
        var (npc, spawner, manager) = Create(NpcGradeType.Normal, disabled ? 0 : 48);
        if (!disabled)
        {
            spawner.Template.MaxPopulation = 1;
            spawner.SpawnedNpcs[3373] = [new Npc()];
        }
        var before = npc.Respawn;
        spawner.DoDespawn(npc);
        await Assert.That(npc.Respawn).IsEqualTo(before);
        await Assert.That(Ready(manager)).DoesNotContain(npc);
    }

    [Test]
    [Arguments("missing-members")]
    [Arguments("group")]
    [Arguments("mismatch")]
    [Arguments("unknown-grade")]
    [Arguments("unknown-template")]
    public async Task UncertainDefinition_PreservesAuthoredSchedule(string definition)
    {
        var (npc, spawner, manager) = Create(NpcGradeType.Normal, 48);
        switch (definition)
        {
            case "missing-members": spawner.Template.Npcs.Clear(); break;
            case "group": spawner.Template.Npcs[0].MemberType = "NpcGroup"; break;
            case "mismatch": spawner.Template.Npcs[0].MemberId = 3484; break;
            case "unknown-grade": npc.Template.NpcGradeId = (NpcGradeType)0; break;
            case "unknown-template": npc.Template.Id = 0; break;
        }
        spawner.DoDespawn(npc);
        await Assert.That(npc.Respawn).IsEqualTo(npc.DeadTime.AddSeconds(48));
        await Assert.That(Ready(manager)).DoesNotContain(npc);
    }

    [Test]
    [Arguments(NpcGradeType.Normal, 15)]
    [Arguments(NpcGradeType.BossA, 48)]
    public async Task NativeImmediateDespawn_UsesSameProtectedInterval(NpcGradeType grade, int delay)
    {
        var (npc, spawner, manager) = Create(grade, 48);
        var before = DateTime.UtcNow;
        spawner.DespawnWithRespawn(npc);
        var after = DateTime.UtcNow;
        await Assert.That(npc.Respawn >= before.AddSeconds(delay)
            && npc.Respawn <= after.AddSeconds(delay)).IsTrue();
        npc.Respawn = DateTime.UtcNow.AddSeconds(-1);
        await Assert.That(Ready(manager)).Contains(npc);
        await Assert.That(spawner.RespawnTime).IsEqualTo(48);
    }

    [Test]
    [Arguments(NpcGradeType.Normal, 15, 48, 15)]
    [Arguments(NpcGradeType.Normal, 15, 0, 20)]
    [Arguments(NpcGradeType.Normal, 0, 48, 20)]
    [Arguments(NpcGradeType.BossA, 15, 48, 20)]
    public async Task EmptyCorpse_UsesIntervalWithoutChangingSharedCleanup(
        NpcGradeType grade, int normalOverride, int originalDelay, int expectedDelay)
    {
        var (npc, spawner, _) = Create(grade, originalDelay, normalOverride);
        npc.Hp = 0;
        var before = DateTime.UtcNow;
        spawner.DoDespawn(npc);
        var after = DateTime.UtcNow;
        await Assert.That(npc.Despawn >= before.AddSeconds(expectedDelay)
            && npc.Despawn <= after.AddSeconds(expectedDelay)).IsTrue();
        await Assert.That(spawner.DespawnTime).IsEqualTo(20);
        await Assert.That((bool)typeof(NpcSpawner).GetMethod("HasCorpse",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(spawner, null)!).IsTrue();
    }

    [Test]
    [Arguments(NpcGradeType.Normal)]
    [Arguments(NpcGradeType.BossA)]
    public async Task CorpseWithLoot_KeepsFullOriginalRetention(NpcGradeType grade)
    {
        var (npc, spawner, _) = Create(grade, 48);
        var loot = new LootingContainer(npc);
        loot.Items.Add(0, new LootingContainerItemEntry { Owner = loot, ItemIndex = 0 });
        typeof(Npc).GetProperty("LootingContainer")!.SetValue(npc, loot);
        var before = DateTime.UtcNow;
        spawner.DoDespawn(npc);
        var after = DateTime.UtcNow;
        var retained = 20 + LootingContainer.LootDespawnExtensionTime;
        await Assert.That(npc.Despawn >= before.AddSeconds(retained)
            && npc.Despawn <= after.AddSeconds(retained)).IsTrue();
        await Assert.That(loot.Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(NpcGradeType.Normal, 15, 10, 15)]
    [Arguments(NpcGradeType.Normal, 0, 10, 20)]
    [Arguments(NpcGradeType.BossA, 15, 10, 20)]
    [Arguments(NpcGradeType.Normal, 15, 30, 2)]
    public async Task NativeEmptyLootUpdate_PreservesGraceAndProtectedGrades(
        NpcGradeType grade, int normalOverride, int age, int expectedDelay)
    {
        var (npc, spawner, _) = Create(grade, 48, normalOverride);
        var loot = new LootingContainer(npc);
        var created = DateTime.UtcNow.AddSeconds(-age);
        typeof(LootingContainer).GetProperty("CreationTime",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(loot, created);
        var ownerType = typeof(LootingContainer).GetProperty("LootOwnerType",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        ownerType.SetValue(loot, Enum.Parse(ownerType.PropertyType, "Npc"));
        var before = DateTime.UtcNow;
        typeof(LootingContainer).GetMethod("UpdateLootState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loot, null);
        var after = DateTime.UtcNow;
        if (age == 30)
            await Assert.That(npc.Despawn >= before.AddSeconds(expectedDelay)
                && npc.Despawn <= after.AddSeconds(expectedDelay)).IsTrue();
        else
            await Assert.That(npc.Despawn).IsEqualTo(created.AddSeconds(expectedDelay));
        await Assert.That(spawner.DespawnTime).IsEqualTo(20);
    }

    [Test]
    public async Task ConfigBinding_DefaultsToDisabled_AndAcceptsExplicitInterval()
    {
        await Assert.That(new WorldConfig().NpcRespawnNormalOverrideSeconds).IsEqualTo(0);
        var bound = JsonSerializer.Deserialize<WorldConfig>("""{"NpcRespawnNormalOverrideSeconds":15}""")!;
        await Assert.That(bound.NpcRespawnNormalOverrideSeconds).IsEqualTo(15);
        await Assert.That(bound.NpcRespawnMinSeconds).IsEqualTo(90);
        await Assert.That(bound.NpcRespawnMinSecondsElite).IsEqualTo(300);
    }
}
