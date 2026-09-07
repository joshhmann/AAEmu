using System.Numerics;
using System.Collections.Concurrent;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Utils.DB;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.World.Zones;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Halcyona skirmish rig (Q8 WAR-HONOR acceptance seed): Nuia-alliance (148)
/// vs Haranya-alliance (149) fireteams fighting real Triple Slash casts
/// through the real DoDie honor path in a seeded War-zone conflict.
/// Fail-pre discipline: each test FAILS if casts don't land, honor doesn't
/// flow, kills don't count, or the alliance relation isn't Hostile.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HalcyonaSkirmishRigTests
{
    private const uint TestZoneKey = 0x5A5A_0002;
    private const uint TestZoneGroupId = 0x5A7A;
    private const uint AssistSwordTemplateId = 90_025;

    private sealed class PacketCaptureSession : ISession
    {
        public System.Net.IPAddress Ip => System.Net.IPAddress.Loopback;
        public uint SessionId => 1;
        public System.Net.Sockets.Socket Socket => null!;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null!;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private static GameConnection Conn(Character c)
    {
        var conn = new GameConnection(new PacketCaptureSession()) { ActiveChar = c };
        c.Connection = conn;
        return conn;
    }

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);

    private static void SetSingletonInstance(Type singletonBase, object instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, instance);

    private sealed class SingletonSwap : IDisposable
    {
        private readonly Type _singletonBase;
        private readonly object? _previous;

        private SingletonSwap(Type singletonBase)
        {
            _singletonBase = singletonBase;
            _previous = GetSingletonInstance(singletonBase);
        }

        public static SingletonSwap Install(Type singletonBase, object replacement)
        {
            var swap = new SingletonSwap(singletonBase);
            SetSingletonInstance(singletonBase, replacement);
            return swap;
        }

        public void Dispose() => SetSingletonInstance(_singletonBase, _previous!);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);
    }

    /// <summary>
    /// Seeded zone scope: the manager serves a War-conflict zone for any key
    /// registered via <see cref="ZoneScope.RegisterZone"/>. Reflection-pinning
    /// Transform._zoneId does NOT stick (region bookkeeping re-derives it),
    /// so tests register the fighters' ACTUAL runtime zone keys instead of
    /// assuming a pinned key.
    /// </summary>
    private sealed class ZoneScope : IDisposable
    {
        private readonly IDisposable _swap;
        private readonly TestableZoneConflict _conflict;
        private readonly Dictionary<uint, Zone> _zones;

        public ZoneScope(TestableZoneConflict conflict)
        {
            _conflict = conflict;
            var zoneManager = new ZoneManager(Mock.Of<AAEmu.Game.Core.Managers.World.IWorldManager>().Object);
            SetField(zoneManager, "_zoneIdToKey", new Dictionary<uint, uint>());
            _zones = new Dictionary<uint, Zone>
            {
                [TestZoneKey] = new()
                {
                    Id = TestZoneKey,
                    ZoneKey = TestZoneKey,
                    GroupId = TestZoneGroupId,
                    FactionId = FactionsEnum.Neutral
                }
            };
            SetField(zoneManager, "_zones", _zones);
            SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>());
            SetField(zoneManager, "_conflicts", new Dictionary<ushort, ZoneConflict> { [(ushort)TestZoneGroupId] = conflict });
            SetField(zoneManager, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
            SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
            _swap = SingletonSwap.Install(typeof(Singleton<ZoneManager>), zoneManager);
        }

        public TestableZoneConflict Conflict => _conflict;

        public void RegisterZone(uint zoneKey)
        {
            _zones[zoneKey] = new Zone
            {
                Id = zoneKey,
                ZoneKey = zoneKey,
                GroupId = TestZoneGroupId,
                FactionId = FactionsEnum.Neutral
            };
        }

        public void Dispose() => _swap.Dispose();
    }

    private sealed class CompositeSwap(params IDisposable[] swaps) : IDisposable
    {
        public void Dispose()
        {
            foreach (var s in swaps)
                s.Dispose();
        }
    }

    /// Seeds a mock-backed SkillManager carrying a functional fixed-damage
    /// Triple Slash (inline ApplyEffects, zero cooldown) plus the Retribution
    /// buff, empty unit requirements, and a mock effect-task manager.
    /// Mirrors the PvpAggressionSeamRigTests seam graph; restored by
    /// the returned swap's Dispose.
    internal static IDisposable SeedSkirmishCombat()
    {
        var damageEffect = new DamageEffect
        {
            Id = 931218,
            DamageType = DamageType.Melee,
            UseFixedDamage = true,
            FixedMin = 100,
            FixedMax = 100,
            WeaponSlotId = -1,
            CheckCrime = true,
        };
        var effect = new SkillEffect
        {
            EffectId = 920529,
            Template = damageEffect,
            StartLevel = 1,
            EndLevel = 99,
            Friendly = true,
            NonFriendly = true,
            Chance = 100,
            Front = true,
            Back = true,
            ApplicationMethod = SkillEffectApplicationMethod.Target,
        };
        var template = new SkillTemplate
        {
            Id = HalcyonaSkirmishScenario.DefaultCastSkillId,
            CastingTime = 0,
            EffectDelay = 0,
            UseAnimTime = false,
            CooldownTime = 0,
            ManaCost = 0,
            MinRange = 0,
            MaxRange = 4,
            TargetType = SkillTargetType.Hostile,
            TargetSelection = SkillTargetSelection.Target,
            TargetRelation = SkillTargetRelation.Hostile,
            LevelRuleNoConsideration = true,
            DamageTypeId = (uint)DamageType.Melee,
            Effects = [effect],
        };
        var manager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetField(manager, "_skills", new Dictionary<uint, SkillTemplate> { [template.Id] = template });
        SetField(manager, "_buffs", new Dictionary<uint, BuffTemplate>
        {
            [(uint)BuffConstants.Retribution] = new()
            {
                Id = (uint)BuffConstants.Retribution,
                Duration = 30000,
                StackRule = BuffStackRule.Refresh,
                MaxStack = 1,
                Kind = BuffKind.Bad
            }
        });
        var req = new UnitRequirementsGameData();
        req.Load(SQLite.CreateConnection());
        var (nuiaFaction, haranyaFaction) = HalcyonaSkirmishScenario.SeedAllianceFactions();
        return new CompositeSwap(
            SingletonSwap.Install(typeof(Singleton<SkillManager>), manager),
            SingletonSwap.Install(typeof(Singleton<EffectTaskManager>),
                new EffectTaskManager(Mock.Of<ITaskManager>().Object)),
            SingletonSwap.Install(typeof(Singleton<UnitRequirementsGameData>), req),
            SeedAllianceFactionManager(nuiaFaction, haranyaFaction));
    }

    /// <summary>
    /// Seeds a FactionManager carrying the two alliance factions (with their
    /// mutual Hostile relations) so relation resolution never touches the DI
    /// singleton initializer (which throws headless). Mirrors the seam rig's
    /// mock-backed manager pattern.
    /// </summary>
    private static IDisposable SeedAllianceFactionManager(SystemFaction nuia, SystemFaction haranya)
    {
        var manager = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        var neutral = new SystemFaction { Id = FactionsEnum.Neutral, MotherId = FactionsEnum.Neutral };
        SetField(manager, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>
        {
            [nuia.Id] = nuia,
            [haranya.Id] = haranya,
            [neutral.Id] = neutral,
        });
        SetField(manager, "_relations", new List<FactionRelation>());
        return SingletonSwap.Install(typeof(Singleton<FactionManager>), manager);
    }


    private static GameplayActor CreateFighter(string name, HeadlessSession session, Vector3 position)
    {
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.JoinActorWorld(session, actor);
        var c = actor.Character;
        c.Hp = c.MaxHp;
        c.Mp = c.MaxMp;
        Conn(c);
        GameplayActorTestRig.SetPosition(actor, position);
        c.Skills.AddSkill(new SkillTemplate { Id = HalcyonaSkirmishScenario.DefaultCastSkillId }, 1, false);
        return actor;
    }
    private static (List<GameplayActor> Nuia, List<GameplayActor> Haranya, HeadlessSession Session) CreateFireteams(
        string name, int teamSize)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (_, session) = GameplayActorTestRig.CreateActor(name + "-host");
        var nuia = new List<GameplayActor>();
        var haranya = new List<GameplayActor>();
        for (var i = 0; i < teamSize; i++)
        {
            nuia.Add(CreateFighter($"{name}-nuia-{i}", session, new Vector3(i * 0.5f, 0f, 0f)));
            haranya.Add(CreateFighter($"{name}-hara-{i}", session, new Vector3(2f + i * 0.5f, 0f, 0f)));
        }
        return (nuia, haranya, session);
    }

    [Test]
    public async Task AllianceFactions_NuiaVsHaranya_ResolveHostileBothDirections()
    {
        var (nuia, haranya) = HalcyonaSkirmishScenario.SeedAllianceFactions();

        await Assert.That(nuia.Relations[HalcyonaSkirmishScenario.HaranyaAlliance].State).IsEqualTo(RelationState.Hostile);
        await Assert.That(haranya.Relations[HalcyonaSkirmishScenario.NuiaAlliance].State).IsEqualTo(RelationState.Hostile);
    }

    [Test]
    public async Task Skirmish_3v3WarZone_BothSidesScoreKillsAndHonor()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        using var combat = SeedSkirmishCombat();
        var (nuia, haranya, _) = CreateFireteams("halcy-3v3", 3);
        foreach (var f in nuia.Concat(haranya)) zone.RegisterZone(f.Character.Transform.ZoneId);

        var result = HalcyonaSkirmishScenario.Run(nuia, haranya,
            new HalcyonaSkirmishScenario.SkirmishOptions { MaxRounds = 120 });

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.NuiaKills).IsGreaterThan(0);
        await Assert.That(result.HonorGained.Values.Sum(h => (long)h)).IsGreaterThan(0);
    }

    [Test]
    public async Task Skirmish_PeaceZone_CountsKillsButAwardsNoHonor()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.Peace);
        using var zone = new ZoneScope(conflict);
        using var combat = SeedSkirmishCombat();
        var (nuia, haranya, _) = CreateFireteams("halcy-peace", 2);
        foreach (var f in nuia.Concat(haranya)) zone.RegisterZone(f.Character.Transform.ZoneId);

        var result = HalcyonaSkirmishScenario.Run(nuia, haranya,
            new HalcyonaSkirmishScenario.SkirmishOptions { MaxRounds = 120, RespawnFallen = false });

        await Assert.That(result.NuiaKills + result.HaranyaKills).IsGreaterThan(0);
        await Assert.That(result.HonorGained.Values.All(h => h == 0)).IsTrue();
    }

    [Test]
    public async Task Skirmish_RespawnFallen_RecyclesFightersAcrossWaves()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        using var combat = SeedSkirmishCombat();
        var (nuia, haranya, _) = CreateFireteams("halcy-wave", 2);
        foreach (var f in nuia.Concat(haranya)) zone.RegisterZone(f.Character.Transform.ZoneId);

        var result = HalcyonaSkirmishScenario.Run(nuia, haranya,
            new HalcyonaSkirmishScenario.SkirmishOptions { MaxRounds = 120, RespawnFallen = true });

        // 2v2 with respawn must produce more kills than bodies (waves recycled).
        await Assert.That(result.NuiaKills + result.HaranyaKills).IsGreaterThanOrEqualTo(4);
    }
}
