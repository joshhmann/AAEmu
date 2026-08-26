using AAEmu.Commons.Network.Core;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Core.Managers.World;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Models.Game.World.Zones;
using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Buffs;


using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// PB-007 seam rig: flagged same-faction skill damage through the REAL
/// Skill.Use → ApplyEffects → DamageEffect.Apply chain.
///
/// Drives a minimal-but-real 18131-shaped template graph (Hostile target type,
/// Target-selection AoE radius, Hostile relation filter, friendly+non-friendly
/// DamageEffect with CheckCrime) between two same-faction headless characters.
///
/// Proven contracts:
///   - ForceAttack-flagged attacker DEALS DAMAGE to a Friendly-relation victim
///     (the ForceAttack exception must survive the AoE relation filter and the
///     DamageEffect CanAttack safeguard), and Retribution applies.
///   - Damage-immune Friendly targets remain protected while Retribution applies.
///     Assault/evidence persistence is intentionally outside this DB-free rig.
///   - Unflagged attacker in a Peace conflict zone: acquisition refuses (NoTarget)
///     — Peace-state protection unchanged (ZONE-01).
///   - Hostile-relation attacker: damage lands unchanged.
/// </summary>
[NotInParallel]
public class PvpAggressionSeamRigTests
{
    private const uint TestZoneKey = 0x5A5A_0002;
    private const uint TestZoneGroupId = 0x5A7B;
    private const uint SeamSkillId = 918131;

    // ------------------------------------------------------------------ rig helpers

    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public System.Net.IPAddress Ip => System.Net.IPAddress.Loopback;
        public uint SessionId => 1;
        public System.Net.Sockets.Socket Socket => null;

        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private static readonly Dictionary<uint, PacketCaptureSession> s_captureByCharId = new();

    private static GameConnection Conn(Character c)
    {
        var capture = new PacketCaptureSession();
        var conn = new GameConnection(capture) { ActiveChar = c };
        c.Connection = conn;
        s_captureByCharId[c.Id] = capture;
        return conn;
    }

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);

    private sealed class SingletonSwap(Type singletonBase) : IDisposable
    {
        private readonly Type _singletonBase = singletonBase;
        private readonly object? _previous = GetSingletonInstance(singletonBase);

        public static SingletonSwap Install(Type singletonBase, object replacement)
        {
            var swap = new SingletonSwap(singletonBase);
            swap._singletonBase
                .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, replacement);
            return swap;
        }

        public void Dispose() => _singletonBase
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previous);
    }

    private sealed class CompositeSwap(params IDisposable[] swaps) : IDisposable
    {
        public void Dispose()
        {
            foreach (var s in swaps) s.Dispose();
        }
    }

    private static void SetField(object target, string fieldName, object value)
        => target.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);

    /// <summary>Builds the 18131-shaped template graph: Target-selection AoE melee skill with one crime-checked DamageEffect.</summary>
    private static SkillTemplate BuildSeamSkillTemplate()
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
            Friendly = true, // mirrors skills 18131's damage effect row (friendly=t, non_friendly=t)
            NonFriendly = true,
            Chance = 100,
            Front = true, // both set → no positional constraint (matches live data)
            Back = true,
            ApplicationMethod = SkillEffectApplicationMethod.Target,
        };

        return new SkillTemplate
        {
            Id = SeamSkillId,
            CastingTime = 0,
            EffectDelay = 0,      // inline ApplyEffects — keeps TaskManager out of the seam
            UseAnimTime = false,
            CooldownTime = 0,
            ManaCost = 0,
            MinRange = 0,
            MaxRange = 4,
            TargetType = SkillTargetType.Hostile,
            TargetSelection = SkillTargetSelection.Target,
            TargetRelation = SkillTargetRelation.Hostile,
            TargetAreaRadius = 2, // forces the FilterAoeUnits path under suspicion in PB-007
            TargetAreaCount = 20,
            LevelRuleNoConsideration = true,
            DamageTypeId = (uint)DamageType.Melee,
            Effects = [effect],
        };
    }

    /// <summary>Seeds a mock-backed SkillManager carrying ONLY the seam template graph (+ Retribution buff).</summary>
    private static IDisposable SeedSkillManager(SkillTemplate template)
    {
        var manager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetField(manager, "_skills", new Dictionary<uint, SkillTemplate> { [template.Id] = template });
        // Mirrors compact.sqlite3 buffs row 2167 ("나쁜사람"): 30 s,
        // stack_rule_id=1 (BuffStackRule.Refresh), max_stack=1, kind_id=2 (Bad).
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
        return new CompositeSwap(
            SingletonSwap.Install(typeof(Singleton<SkillManager>), manager),
            SeedEffectTaskManager());
    }

    /// <summary>Provides the real scheduling seam without starting a game loop.</summary>
    private static IDisposable SeedEffectTaskManager()
        => SingletonSwap.Install(
            typeof(Singleton<EffectTaskManager>),
            new EffectTaskManager(Mock.Of<ITaskManager>().Object));

    /// <summary>Empty UnitRequirementsGameData: every skill resolves to "no requirements".</summary>
    private static IDisposable SeedEmptyUnitRequirements()
    {
        var req = new UnitRequirementsGameData();
        // REAL canonical data through the official Load path — requirement stores
        // are private auto-properties that only Load() initializes.
        req.Load(AAEmu.Game.Utils.DB.SQLite.CreateConnection());
        return SingletonSwap.Install(typeof(Singleton<UnitRequirementsGameData>), req);
    }


    private static IDisposable SeedConflictZone(TestableZoneConflict conflict)
    {
        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        SetField(zoneManager, "_zoneIdToKey", new Dictionary<uint, uint>());
        SetField(zoneManager, "_zones", new Dictionary<uint, Zone>
        {
            [TestZoneKey] = new()
            {
                Id = TestZoneKey,
                ZoneKey = TestZoneKey,
                GroupId = TestZoneGroupId,
                FactionId = FactionsEnum.Neutral
            }
        });
        SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>());
        SetField(zoneManager, "_conflicts", new Dictionary<ushort, ZoneConflict> { [(ushort)TestZoneGroupId] = conflict });
        SetField(zoneManager, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
        SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        return SingletonSwap.Install(typeof(Singleton<ZoneManager>), zoneManager);
    }

    /// <summary>Seeds a mock-backed FactionManager so CanAttack's zone-faction fallback resolves.</summary>
    private static IDisposable SeedFactionManager()
    {
        var fm = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        SetField(fm, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>());
        SetField(fm, "_relations", new List<FactionRelation>());
        return SingletonSwap.Install(typeof(Singleton<FactionManager>), fm);
    }

    /// <summary>
    /// Seeds an ItemManager whose _config is initialized — ApplyEffects' target
    /// durability block reads GetDurabilityDecrementChance() for Character targets.
    /// Missing-only: never replaces an already-seeded (possibly real) instance.
    /// </summary>
    private static IDisposable EnsureItemManagerConfig()
    {
        var cfgField = typeof(ItemManager).GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var existing = GetSingletonInstance(typeof(Singleton<ItemManager>));
        if (existing == null)
        {
            // ItemManager is a DI singleton (no parameterless ctor): build it the
            // same way GameplayActorTestRig.SeedBaseSurface does, so ordering
            // between this seam rig and the actor rig never matters.
            ItemIdManager.Instance.Initialize(true);
            existing = new ItemManager(
                Mock.Of<ISkillManager>().Object,
                ItemIdManager.Instance,
                Mock.Of<IContainerIdManager>().Object,
                Mock.Of<ILocalizationManager>().Object,
                Mock.Of<ITaskManager>().Object,
                Mock.Of<IWorldManager>().Object);
            SetField(existing, "_templates", new Dictionary<uint, AAEmu.Game.Models.Game.Items.Templates.ItemTemplate>());
            SetField(existing, "_removedItems", new List<ulong>());
            SetField(existing, "_allItems", new System.Collections.Concurrent.ConcurrentDictionary<ulong, Item>());
            GameplayActorTestRig.SeedSingleton(typeof(Singleton<ItemManager>), existing);
        }
        if (cfgField.GetValue(existing) == null)
            cfgField.SetValue(existing, new ItemConfig());
        return new CompositeSwap();
    }

    private static TestableZoneConflict CreateConflict(ZoneConflictType state)
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(state);
        return conflict;
    }

    private static (GameplayActor Attacker, GameplayActor Victim, HeadlessSession Session) CreatePair(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (attackerActor, session) = GameplayActorTestRig.CreateActor(name + "-atk");
        var (victimActor, _) = GameplayActorTestRig.CreateActor(name + "-vic");
        GameplayActorTestRig.JoinActorWorld(session, victimActor);

        var attacker = attackerActor.Character;
        var victim = victimActor.Character;
        foreach (var c in new[] { attacker, victim })
        {
            c.Level = 40; // skill effect level gates use the CASTER's level
            c.Hp = c.MaxHp;
            c.Mp = c.Mp == 0 ? c.MaxMp : c.Mp;
            Conn(c); // capture-backed connections — nothing reaches a network
        }
        return (attackerActor, victimActor, session);
    }

    private static void SetZone(GameplayActor actor, uint zoneKey)
        => typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_zoneId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, zoneKey);

    /// <summary>
    /// Avoids invoking the persistence-backed bloodstain path in this DB-free rig.
    /// Live evidence generation is covered by the PB-007 instrumented run.
    /// </summary>
    private static void SeedExistingAssault(Character attacker, Character victim)
        => victim.AssaultedBy.Add(attacker.Id);

    /// <summary>Initializes requirement stores on WHATEVER UnitRequirementsGameData instance is live.</summary>
    private static void EnsureRequirementsStores(UnitRequirementsGameData req)
    {
        if (req.GetType().GetProperty("_unitReqs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(req) is null)
            req.Load(AAEmu.Game.Utils.DB.SQLite.CreateConnection());
    }

    /// <summary>Casts the seam skill through the REAL Skill.Use pipeline.</summary>
    private static SkillResult CastSeamSkill(GameplayActor attacker, GameplayActor victim)
    {
        EnsureRequirementsStores(UnitRequirementsGameData.Instance);
        var skill = new Skill(BuildSeamSkillTemplate());
        return skill.Use(
            attacker.Character,
            new SkillCasterUnit(attacker.Character.ObjId),
            new SkillCastUnitTarget(victim.Character.ObjId),
            null,
            true,
            out _);
    }


    // ------------------------------------------------------- wire observability (PB-007 residual)

    /// <summary>
    /// GamePacket.Encode prefixes a two-byte frame length, then DD <level>
    /// [hash count when level==1] <typeid:u16le>; SCBuffCreatedPacket is
    /// typeId 0x0B6 at level 1 → bytes 02..07 DD 01 00 00 B6 00.
    /// </summary>
    private static int CapturedBuffCreatedCount(Character owner)
        => s_captureByCharId.TryGetValue(owner.Id, out var capture)
            ? capture.CapturedPackets.Count(p => p.Length >= 8
                && p[2] == 0xDD && p[3] == 0x01 && p[4] == 0x00 && p[5] == 0x00
                && p[6] == 0xB6 && p[7] == 0x00)
            : 0;

    private static bool CapturedBuffCreated(Character owner)
        => CapturedBuffCreatedCount(owner) > 0;

    // ------------------------------------------------------------------ tests

    [Test]
    public async Task Use_FlaggedSameFaction_DamageLandsAndRetributionApplies()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var skillSwap = SeedSkillManager(BuildSeamSkillTemplate());
        using var reqSwap = SeedEmptyUnitRequirements();
        using var factionSwap = SeedFactionManager();
        using var itemSwap = EnsureItemManagerConfig();

        var (attacker, victim, session) = CreatePair("pb7");
        _ = session;

        // Same non-root faction → relation Friendly; ForceAttack makes it attackable.
        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9107 };
        attacker.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(attacker, TestZoneKey);
        SetZone(victim, TestZoneKey);

        attacker.Character.ForceAttack = true;
        SeedExistingAssault(attacker.Character, victim.Character);
        var hpBefore = victim.Character.Hp;
        var result = CastSeamSkill(attacker, victim);

        await Assert.That(result).IsEqualTo(SkillResult.Success);
        await Assert.That(victim.Character.Hp).IsLessThan(hpBefore);
        // Existing assault state keeps this DB-free seam away from evidence persistence.
        await Assert.That(attacker.Character.Buffs.CheckBuff((uint)BuffConstants.Retribution)).IsTrue();
    }

    [Test]
    public async Task Use_FlaggedSameFaction_VictimDamageImmune_RetributionStillApplies()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var skillSwap = SeedSkillManager(BuildSeamSkillTemplate());
        using var reqSwap = SeedEmptyUnitRequirements();
        using var factionSwap = SeedFactionManager();
        using var itemSwap = EnsureItemManagerConfig();

        var (attacker, victim, session) = CreatePair("pb7immune");
        _ = session;

        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9110 };
        attacker.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(attacker, TestZoneKey);
        SetZone(victim, TestZoneKey);
        attacker.Character.ForceAttack = true;
        SeedExistingAssault(attacker.Character, victim.Character);

        // Mirror login buff 2423 ("LoggedOn"): full all-type damage immunity.
        var immuneTemplate = new BuffTemplate
        {
            Id = 902423,
            Duration = 20000,
            MeleeImmune = true,
            SpellImmune = true,
            RangedImmune = true,
            SiegeImmune = true
        };
        var buffsField = typeof(SkillManager).GetField("_buffs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var buffsDict = (Dictionary<uint, BuffTemplate>)buffsField.GetValue(SkillManager.Instance)!;
        buffsDict[902423] = immuneTemplate;
        victim.Character.Buffs.AddBuff(new Buff(
            victim.Character, victim.Character,
            new SkillCasterUnit(victim.Character.ObjId),
            immuneTemplate, null, DateTime.UtcNow));

        var hpBefore = victim.Character.Hp;
        var result = CastSeamSkill(attacker, victim);

        await Assert.That(victim.Character.Hp).IsEqualTo(hpBefore);
        await Assert.That(attacker.Character.Buffs.CheckBuff((uint)BuffConstants.Retribution)).IsTrue();
    }

    [Test]
    public async Task Use_UnflaggedSameFaction_InPeaceZone_IsRefused()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var skillSwap = SeedSkillManager(BuildSeamSkillTemplate());
        using var reqSwap = SeedEmptyUnitRequirements();
        using var factionSwap = SeedFactionManager();
        using var itemSwap = EnsureItemManagerConfig();

        var (attacker, victim, session) = CreatePair("pb7peace");
        _ = session;

        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9108 };
        attacker.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(attacker, TestZoneKey);
        SetZone(victim, TestZoneKey);
        attacker.Character.ForceAttack = false;
        var hpBefore = victim.Character.Hp;
        var result = CastSeamSkill(attacker, victim);

        await Assert.That(result).IsEqualTo(SkillResult.NoTarget);
        await Assert.That(victim.Character.Hp).IsEqualTo(hpBefore);
    }

    [Test]
    public async Task Use_HostileRelation_DamageLandsUnchanged()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var skillSwap = SeedSkillManager(BuildSeamSkillTemplate());
        using var reqSwap = SeedEmptyUnitRequirements();
        using var factionSwap = SeedFactionManager();
        using var itemSwap = EnsureItemManagerConfig();

        var (attacker, victim, session) = CreatePair("pb7hostile");
        _ = session;

        // Root-hostile faction vs non-root faction → relation Hostile regardless of flag.
        attacker.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9109 };
        SetZone(attacker, TestZoneKey);
        SetZone(victim, TestZoneKey);

        var hpBefore = victim.Character.Hp;
        var result = CastSeamSkill(attacker, victim);

        await Assert.That(result).IsEqualTo(SkillResult.Success);
        await Assert.That(victim.Character.Hp).IsLessThan(hpBefore);
    }

    [Test]
    public async Task Use_ImmuneCrime_RetributionBuffCreatedObservedOnAttackerWire()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var skillSwap = SeedSkillManager(BuildSeamSkillTemplate());
        using var reqSwap = SeedEmptyUnitRequirements();
        using var factionSwap = SeedFactionManager();
        using var itemSwap = EnsureItemManagerConfig();

        var (attacker, victim, session) = CreatePair("pb7wire");
        _ = session;

        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9111 };
        attacker.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(attacker, TestZoneKey);
        SetZone(victim, TestZoneKey);
        attacker.Character.ForceAttack = true;
        SeedExistingAssault(attacker.Character, victim.Character);

        var immuneTemplate = new BuffTemplate
        {
            Id = 902424,
            Duration = 20000,
            MeleeImmune = true,
            SpellImmune = true,
            RangedImmune = true,
            SiegeImmune = true
        };
        var buffsField = typeof(SkillManager).GetField("_buffs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var buffsDict = (Dictionary<uint, BuffTemplate>)buffsField.GetValue(SkillManager.Instance)!;
        buffsDict[902424] = immuneTemplate;
        victim.Character.Buffs.AddBuff(new Buff(
            victim.Character, victim.Character,
            new SkillCasterUnit(victim.Character.ObjId),
            immuneTemplate, null, DateTime.UtcNow));

        var result = CastSeamSkill(attacker, victim);

        await Assert.That(result).IsEqualTo(SkillResult.Success);
        await Assert.That(attacker.Character.Buffs.CheckBuff((uint)BuffConstants.Retribution)).IsTrue();
        await Assert.That(CapturedBuffCreated(attacker.Character)).IsTrue();
    }

    [Test]
    public async Task Use_RepeatedCrimeWhileRetributionHeld_RefreshStaysApplied()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var skillSwap = SeedSkillManager(BuildSeamSkillTemplate());
        using var reqSwap = SeedEmptyUnitRequirements();
        using var factionSwap = SeedFactionManager();
        using var itemSwap = EnsureItemManagerConfig();

        var (attacker, victim, session) = CreatePair("pb7refresh");
        _ = session;

        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9112 };
        attacker.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(attacker, TestZoneKey);
        SetZone(victim, TestZoneKey);
        attacker.Character.ForceAttack = true;
        SeedExistingAssault(attacker.Character, victim.Character);

        var first = CastSeamSkill(attacker, victim);
        await Assert.That(first).IsEqualTo(SkillResult.Success);
        await Assert.That(attacker.Character.Buffs.CheckBuff((uint)BuffConstants.Retribution)).IsTrue();
        var createdAfterFirst = CapturedBuffCreatedCount(attacker.Character);
        await Assert.That(createdAfterFirst).IsGreaterThan(0);

        // The canonical row uses stack_rule_id=1 (BuffStackRule.Refresh).
        var second = CastSeamSkill(attacker, victim);
        await Assert.That(second).IsEqualTo(SkillResult.Success);
        await Assert.That(attacker.Character.Buffs.CheckBuff((uint)BuffConstants.Retribution)).IsTrue();
        await Assert.That(CapturedBuffCreatedCount(attacker.Character)).IsGreaterThan(createdAfterFirst);
    }

    private static ZoneConflictType CreatePeace() => ZoneConflictType.Peace;
}
