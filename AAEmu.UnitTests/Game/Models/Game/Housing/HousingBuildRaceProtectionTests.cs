using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

/// <summary>
/// C11 / FIX-2 build-race protection (m3-canonical-audit §6 gap 6): two simultaneous
/// placements of the same plot must not BOTH succeed.
///
/// Drives the REAL engine path — HousingManager.Build, exactly what
/// CSCreateHousePacket invokes — from two threads through the M5.2 actor harness
/// (GameplayActor.BuildHouse → HousingManager.Build). The engine serializes the
/// check-then-insert critical section under _housesLock: the first placement
/// registers its house, so the loser's placement validation sees the occupied plot
/// and refuses (OverlapHouse → error packet → post-state verification rejects).
///
/// Adjacent-boundary companion (the M3a 16 m precedent): two CONCURRENT placements
/// at LEGAL adjacent plots (16 m ≥ 7.5+7.5 garden radii) must BOTH succeed — the
/// race lock serializes, it must not over-reject legitimate claims.
///
/// Persistence-boundary honesty: Build's House.Save() MySQL failure is swallowed by
/// design (catch → log → retry next tick); the race guarantee proven here is the
/// IN-MEMORY registry (_houses/_housesTl), which is the source of truth every later
/// validation reads. The persistence-level write is outside the lock by intent
/// (documented at the call site) — a crash between insert and save loses the row,
/// which is the pre-existing M3b recovery surface, not a race window.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel] // process-wide MySQL.SetConfiguration + singleton state + canonical HousingGameData (M5.2 convention)
public class HousingBuildRaceProtectionTests
{
    /// <summary>Canonical 1.2 w_solzreed_1 zone key (faction 148 NuiaAlliance; groups 1+14 allow category 1).</summary>
    private const uint SolzreedZoneKey = 9;
    private const uint SolzreedZoneId = 9;
    private const FactionsEnum SolzreedFaction = FactionsEnum.NuiaAlliance;

    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    /// <summary>M3a adjacent-homestead precedent: 16 m apart (required minimum 15 m = 7.5+7.5 garden radii).</summary>
    private const float AdjacentOffset = 16f;

    private WorldConfig _previousWorldConfig;
    private object _previousHousingGameData;
    private HashSet<uint> _houseIdsAtSetup = [];

    [Before(Test)]
    public void SetUp()
    {
        // 14-day protection keeps fresh houses out of the tax-due mail path
        // (unwired headless) — same tail control as the M5.2 build tests.
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 14 };

        GameplayActorTestRig.SeedHouseBuildSurface();
        LoadRealHousingGameData();

        _houseIdsAtSetup = HousingManager.Instance.GetAllHouses().Select(h => h.Id).ToHashSet();

        // Build's persistence tail (house.Save) must fail FAST headless
        // (dead port → immediate MySqlException, swallowed by the engine).
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
        AppConfiguration.Instance.World = _previousWorldConfig;
        var housingField = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        housingField?.SetValue(null, _previousHousingGameData);
        RemoveEngineHouses();
    }

    // ================================================================ THE RACE

    [Test]
    public async Task Build_TwoSimultaneousPlacements_SameSpot_ExactlyOneSucceeds()
    {
        var (actorA, sessionA) = CreateActor("race-player-a");
        var (actorB, sessionB) = CreateActor("race-player-b");
        GameplayActorTestRig.StockItem(sessionA, GameplayActorTestRig.TestDesignItemTemplateId, 1);
        GameplayActorTestRig.StockItem(sessionB, GameplayActorTestRig.TestDesignItemTemplateId, 1);
        // Cover the second-house-on-account tax branch regardless of account
        // assignment — the gate under test is the placement race, not tax.
        actorA.Character.Money = 10_000_000;
        actorB.Character.Money = 10_000_000;

        // Both players finalize placement of the SAME plot at the SAME moment.
        using var barrier = new Barrier(2);
        var taskA = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return actorA.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
                GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);
        });
        var taskB = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return actorB.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
                GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);
        });
        await Assert.That(Task.WaitAll([taskA, taskB], TimeSpan.FromSeconds(30))).IsTrue();

        var results = new[] { taskA.Result, taskB.Result };

        // EXACTLY ONE success: the atomic check-then-insert lets the winner
        // register; the loser's validation runs after the insert (lock order)
        // and sees the occupied plot.
        var winners = results.Where(r => r.State == ActorLifecycleState.Completed).ToList();
        var losers = results.Where(r => r.State == ActorLifecycleState.Rejected).ToList();
        await Assert.That(winners.Count).IsEqualTo(1);
        await Assert.That(losers.Count).IsEqualTo(1);

        // Exactly one NEW house registered at the contested spot.
        var newHouses = HousingManager.Instance.GetAllHouses()
            .Where(h => !_houseIdsAtSetup.Contains(h.Id)).ToList();
        await Assert.That(newHouses.Count).IsEqualTo(1);
        await Assert.That(newHouses[0].OwnerId)
            .IsEqualTo(results.IndexOf(winners[0]) == 0 ? actorA.Character.Id : actorB.Character.Id);

        // The winner paid exactly one design item; the loser paid NOTHING
        // (its rejection happened at placement validation, before consumption).
        var winnerActor = ReferenceEquals(winners[0], taskA.Result) ? actorA : actorB;
        var loserActor = ReferenceEquals(winnerActor, actorA) ? actorB : actorA;
        await Assert.That(BagCount(winnerActor)).IsEqualTo(0);
        await Assert.That(BagCount(loserActor)).IsEqualTo(1);
        await Assert.That(FindOwnedHouse(loserActor.Character.Id)).IsNull();

        // The loser's refusal came from an ENGINE gate (silent error packet →
        // post-state verification rejected), not an actor pre-flight.
        await Assert.That(losers[0].Detail?.Contains("engine refused")).IsTrue();
    }

    [Test]
    public async Task Build_TwoSimultaneousPlacements_AdjacentPlots_BothSucceed()
    {
        // Boundary companion: the race lock serializes placements, it must NOT
        // over-reject legal adjacent claims (16 m ≥ 15 m required spacing — the
        // M3a adjacent-homestead precedent).
        var (actorA, sessionA) = CreateActor("race-adjacent-a");
        var (actorB, sessionB) = CreateActor("race-adjacent-b");
        GameplayActorTestRig.StockItem(sessionA, GameplayActorTestRig.TestDesignItemTemplateId, 1);
        GameplayActorTestRig.StockItem(sessionB, GameplayActorTestRig.TestDesignItemTemplateId, 1);
        actorA.Character.Money = 10_000_000;
        actorB.Character.Money = 10_000_000;

        var positionB = new Vector3(TestPosition.X + AdjacentOffset, TestPosition.Y, TestPosition.Z);

        using var barrier = new Barrier(2);
        var taskA = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return actorA.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
                GameplayActorTestRig.TestDesignItemTemplateId, TestPosition);
        });
        var taskB = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return actorB.BuildHouse(GameplayActorTestRig.TestHouseDesignId,
                GameplayActorTestRig.TestDesignItemTemplateId, positionB);
        });
        await Assert.That(Task.WaitAll([taskA, taskB], TimeSpan.FromSeconds(30))).IsTrue();

        await Assert.That(taskA.Result.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(taskB.Result.State).IsEqualTo(ActorLifecycleState.Completed);

        // Two homesteads stand side by side — one per player, one design item consumed each.
        var newHouses = HousingManager.Instance.GetAllHouses()
            .Where(h => !_houseIdsAtSetup.Contains(h.Id)).ToList();
        await Assert.That(newHouses.Count).IsEqualTo(2);
        await Assert.That(newHouses.Any(h => h.OwnerId == actorA.Character.Id)).IsTrue();
        await Assert.That(newHouses.Any(h => h.OwnerId == actorB.Character.Id)).IsTrue();
        await Assert.That(BagCount(actorA)).IsEqualTo(0);
        await Assert.That(BagCount(actorB)).IsEqualTo(0);
    }

    // ================================================================ rig helpers (M5.2 shape)

    private static (GameplayActor Actor, HeadlessSession Session) CreateActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        typeof(WorldInstance).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(session.World.Id, session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);

        GameplayActorTestRig.WireHouseZone(session, SolzreedZoneKey, new Zone
        {
            Id = SolzreedZoneId,
            Name = "w_solzreed_1",
            FactionId = SolzreedFaction
        });
        GameplayActorTestRig.AttachConnection(actor);
        return (actor, session);
    }

    private static uint s_nextWorldId = 0x6000_0000;

    private void RemoveEngineHouses()
    {
        var manager = HousingManager.Instance;
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        foreach (var id in houses.Keys.Where(id => !_houseIdsAtSetup.Contains(id)).ToList())
            houses.Remove(id);
        foreach (var kv in housesTl.Where(kv => !_houseIdsAtSetup.Contains(kv.Value.Id)).ToList())
            housesTl.Remove(kv.Key);
    }

    private static House FindOwnedHouse(uint ownerId)
        => HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.OwnerId == ownerId);

    private static int BagCount(GameplayActor actor)
    {
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(GameplayActorTestRig.TestDesignItemTemplateId, -1, out _, out var count);
        return count;
    }

    // --- canonical data ---------------------------------------------------

    private static string CanonicalDbPath
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(baseDir, "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(baseDir, "..", "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3")
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException("compact.sqlite3 not found in any expected test layout");
        }
    }

    /// <summary>
    /// Loads the canonical housing data via the real loader (M5.2 pattern),
    /// including the TaxationsManager join HousingGameData.PostLoad reads.
    /// </summary>
    private void LoadRealHousingGameData()
    {
        var field = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousHousingGameData = field?.GetValue(null);
        var gameData = new HousingGameData();
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            gameData.Load(connection);

            if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<TaxationsManager>)))
            {
                var taxations = new TaxationsManager { taxations = new Dictionary<uint, Taxation>() };
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, tax FROM taxations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    taxations.taxations[Convert.ToUInt32(reader.GetValue(0))] = new Taxation
                    {
                        Id = Convert.ToUInt32(reader.GetValue(0)),
                        Tax = Convert.ToUInt32(reader.GetValue(1))
                    };
                GameplayActorTestRig.SeedSingleton(typeof(Singleton<TaxationsManager>), taxations);
            }
        }

        gameData.PostLoad();
        field?.SetValue(null, gameData);
    }
}
