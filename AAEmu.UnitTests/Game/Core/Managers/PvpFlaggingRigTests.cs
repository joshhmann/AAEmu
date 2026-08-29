using System.Collections.Concurrent;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Models.Game.World.Zones;

using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// PVP-01 (partial) headless verification rig: player-kill flagging semantics
/// around <see cref="Character.DoDie"/> — HostileFactionKills/HonorPoint awards
/// on hostile kills, zone-state gating of honor, friendly-fire crime-evidence
/// generation, and the end-to-end peace-protection predicate through
/// BaseUnit.CanAttack (0482ba3f0 integration).
///
/// Engine entry points exercised (all REAL code paths):
///   - Character.DoDie death block (CharacterCombat.cs ~112-188)
///   - AwardPvpHonor / CollectAssists (CharacterCombat.cs)
///   - CrimeManager.GenerateEvidenceFromKill → DoodadManager.CreatePlayerDoodad
///   - BaseUnit.CanAttack zone-conflict gate (ZoneConflict.BlocksPvpDamage)
///
/// Scope notes:
///   - CRIME POINTS ARE NOT AWARDED ON THE KILL ITSELF — verified by reading
///     the engine: the death block only spawns the LargeBloodstain evidence
///     doodad; crime points land exclusively through the REPORT flow
///     (CrimeManager.ReportCrime → AddCrimePoints). Reporting needs a live
///     doodad-interaction session and stays out of scope here.
///   - The evidence-doodad chain runs real code up to Doodad.Save(), whose
///     terminal MySQL write fails headless (the ExpeditionManagerRigTests
///     SwallowTerminalSave convention — exactly that MySqlException is
///     swallowed AFTER asserting the spawned state).
///
/// Engine bugs FOUND while verifying (documented, deliberately NOT fixed —
/// out of this rig's ownership):
///   1. CrimeManager.GenerateEvidenceFromDamage / GenerateEvidenceFromKill
///      (CrimeManager.cs:~300/~337): the `if (criminal is null)` guard sits
///      AFTER `criminal.GetOwnerCharacter()` already dereferenced the
///      parameter — the guard can never fire; a null criminal would NRE one
///      line earlier. Currently unreachable from Character.DoDie (killer is
///      null-checked first), but the guard is dead code with ReSharper's
///      warning suppressed inline.
///   2. CrimeManager.GenerateEvidenceFromDamage (~lines 305-317): the
///      nearby-duplicate-bloodstain suppression block is commented out —
///      repeated friendly-fire damage ticks spawn unbounded SmallBloodstain
///      doodads around the victim (kill path unaffected; damage path spams).
///   3. Doodad.Data setter auto-persists (Doodad.cs:243 calls Save() when
///      IsPersistent) — CreatePlayerDoodad sets IsPersistent BEFORE
///      InitDoodad/Spawn/AddPlayerDoodad, so setting `doodad.Data =
///      customData` (DoodadManager.cs:3007) performs a SYNCHRONOUS DB write
///      of a half-initialized, not-yet-spawned doodad. Any DB failure there
///      propagates out of CrimeManager.GenerateEvidenceFromKill into
///      Character.DoDie's friendly-fire branch and aborts the REST of the
///      death block (trade-pack drop, aggro/assault-list cleanup, wanted-
///      arrest handling) AFTER the victim is already dead. This rig asserts
///      the observed engine behavior (exception from the Data setter chain,
///      no in-world evidence doodad); fixing the ordering is owner territory.
/// </summary>
[NotInParallel]
public class PvpFlaggingRigTests
{
    private const uint TestZoneKey = 0x5A5A_0001;
    private const uint TestZoneGroupId = 0x5A79;

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

    /// <summary>Capture-and-force singleton swap; dispose restores the previous instance.</summary>
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

    private static (GameplayActor Killer, GameplayActor Victim, HeadlessSession Session) CreatePair(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (killerActor, session) = GameplayActorTestRig.CreateActor(name + "-killer");
        var (victimActor, _) = GameplayActorTestRig.CreateActor(name + "-victim");
        GameplayActorTestRig.JoinActorWorld(session, victimActor);

        var killer = killerActor.Character;
        var victim = victimActor.Character;
        foreach (var c in new[] { killer, victim })
        {
            c.Hp = c.MaxHp;
            c.Mp = c.MaxMp;
            Conn(c); // capture-backed connections — nothing reaches a network
        }
        return (killerActor, victimActor, session);
    }

    /// <summary>
    /// Seeds the ZoneManager singleton (party-rig precedent) with ONE zone at
    /// the given key whose group carries the given conflict state machine.
    /// The previous singleton is restored by the returned swap's Dispose.
    /// </summary>
    private static SingletonSwap SeedConflictZone(TestableZoneConflict conflict)
    {
        var zoneManager = new ZoneManager(Mock.Of<AAEmu.Game.Core.Managers.World.IWorldManager>().Object);
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

    /// <summary>Puts a character into the seeded test zone WITHOUT the public setter's OnZoneChange side effects.</summary>
    private static void SetZone(GameplayActor actor, uint zoneKey)
        => typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_zoneId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, zoneKey);

    /// <summary>Kills the victim through the REAL Character.DoDie death block.</summary>
    private static Exception? Kill(GameplayActor victim, GameplayActor killer)
    {
        try
        {
            victim.Character.Hp = 0;
            victim.Character.DoDie(killer.Character, KillReason.Damage);
            return null;
        }
        catch (MySqlException ex)
        {
            // Expected terminal-save failure of the evidence-doodad chain
            // (Doodad.Save → MySQL) — headless unit env has no DB. In-memory
            // state (spawned doodad, flags, counters) is already mutated.
            return ex;
        }
    }

    // ------------------------------------------------------------------ a. hostile-faction kill

    [Test]
    public async Task DoDie_HostileKillInWarZone_AwardsKillCountAndHonorAndVictimPenalty()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.War));
        var (killer, victim, session) = CreatePair("pvp-war");
        _ = session;

        // Root-hostile faction → relation resolves to Hostile immediately
        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9101 };
        SetZone(victim, TestZoneKey);
        victim.Character.HonorPoint = 50;

        var ex = Kill(victim, killer);
        await Assert.That(ex).IsNull(); // hostile branch touches no persistence

        await Assert.That(killer.Character.HostileFactionKills).IsEqualTo(1u);
        await Assert.That(killer.Character.HonorGainedInCombat).IsEqualTo(40u); // War base 40 × rate 1.0
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(40);

        // Victim PvP-death markers + War-zone honor loss (clamped share of 10)
        await Assert.That(victim.Character.DiedInPvp).IsTrue();
        await Assert.That(victim.Character.DiedInPvpWarZone).IsTrue();
        await Assert.That(victim.Character.HonorPoint).IsEqualTo(40);
        await Assert.That(victim.Character.IsDead).IsTrue();
    }

    [Test]
    public async Task DoDie_HostileKillInPeaceZone_CountsKillButAwardsNoHonor()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        var (killer, victim, session) = CreatePair("pvp-peace");
        _ = session;

        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9101 };
        SetZone(victim, TestZoneKey);

        var ex = Kill(victim, killer);
        await Assert.That(ex).IsNull();

        // Flagging still happens outside Conflict/War…
        await Assert.That(killer.Character.HostileFactionKills).IsEqualTo(1u);
        await Assert.That(victim.Character.DiedInPvp).IsTrue();
        // …but honor is gated to Conflict/War zones, and no War-zone death penalty applies.
        await Assert.That(killer.Character.HonorGainedInCombat).IsEqualTo(0u);
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(0);
        await Assert.That(victim.Character.DiedInPvpWarZone).IsFalse();
    }

    [Test]
    public async Task DoDie_HostileKillInConflictZone_CountsKillButAwardsNoHonor()
    {
        // War-gating owner ruling 2026-08-25 ("keep it korean"): RU official 2.9 notes —
        // kills during Conflict award 0 honor; honor flows in War zones only.
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Conflict));
        var (killer, victim, session) = CreatePair("pvp-conflict");
        _ = session;

        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9103 };
        SetZone(victim, TestZoneKey);

        var ex = Kill(victim, killer);
        await Assert.That(ex).IsNull();

        // Flagging/kill counting still happens in a Conflict zone…
        await Assert.That(killer.Character.HostileFactionKills).IsEqualTo(1u);
        await Assert.That(victim.Character.DiedInPvp).IsTrue();
        // …but the award is WAR-GATED: zero honor delta in Conflict.
        await Assert.That(killer.Character.HonorGainedInCombat).IsEqualTo(0u);
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(0);
        // No War-zone death penalty applies outside War.
        await Assert.That(victim.Character.DiedInPvpWarZone).IsFalse();
    }

    [Test]
    public async Task DoDie_HostileKillInWarZoneWithOnlineAssist_SplitsKiller32AndAssist4()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.War));
        var (killer, victim, session) = CreatePair("pvp-split");
        _ = session;

        // Third player inside the 30-s damage window → online-assist path.
        var (assistActor, _) = GameplayActorTestRig.CreateActor("pvp-split-assist");
        GameplayActorTestRig.JoinActorWorld(session, assistActor);
        Conn(assistActor.Character);
        assistActor.Character.IsOnline = true;

        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9104 };
        SetZone(victim, TestZoneKey);

        // Record the assistant in the victim's rolling damage history — the same seam
        // DamageEffect feeds on every hit — without running the full skill pipeline.
        SetField(victim.Character, "_pvpDamageHistory",
            new ConcurrentDictionary<uint, DateTime> { [assistActor.Character.Id] = DateTime.UtcNow });

        var ex = Kill(victim, killer);
        await Assert.That(ex).IsNull();

        // INFERRED split of the RU-official 40 base: 32 killer + 4 per online assist.
        await Assert.That(killer.Character.HonorGainedInCombat).IsEqualTo(32u);
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(32);
        await Assert.That(assistActor.Character.HonorGainedInCombat).IsEqualTo(4u);
        await Assert.That(assistActor.Character.HonorPoint).IsEqualTo(4);
    }

    [Test]
    public async Task DoDie_KillEscalation_DrivesZoneFromTensionThroughStagesToConflictAndWar()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        // Cumulative 1-kill-per-stage escalation thresholds: [1, 2, 3, 4, 5]
        var conflict = new TestableZoneConflict();
        for (var i = 0; i < 5; i++)
            conflict.NumKills[i] = i + 1;
        conflict.ForceState(ZoneConflictType.Tension);

        using var zoneSwap = SeedConflictZone(conflict);
        var (killer, victim, session) = CreatePair("pvp-escalate");
        _ = session;

        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9120 };
        SetZone(victim, TestZoneKey);
        victim.Character.HonorPoint = 50;

        // Stage 1: Kill 1 -> KillCount=1 <= 1 (Tension), Kill 2 -> KillCount=2 > 1 (Danger)
        Kill(victim, killer);
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Tension);
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(0);

        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Danger);
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(0);

        // Stage 2: Kill 3 -> KillCount=3 > 2 (Dispute)
        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Dispute);

        // Stage 3: Kill 4 -> KillCount=4 > 3 (Unrest)
        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Unrest);

        // Stage 4: Kill 5 -> KillCount=5 > 4 (Crisis)
        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Crisis);

        // Stage 5: Kill 6 -> KillCount=6 > 5 (Conflict)
        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);
        await Assert.That(conflict.CurrentZoneState).IsEqualTo(ZoneConflictType.Conflict);
        await Assert.That(conflict.KillCount).IsEqualTo(0u);
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(0);

        // Advance to War
        conflict.ForceState(ZoneConflictType.War);
        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);

        // In War: kill awards 40 honor to killer, victim loses 10 honor
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(40);
        await Assert.That(victim.Character.DiedInPvpWarZone).IsTrue();
        await Assert.That(victim.Character.HonorPoint).IsEqualTo(40);

        // Advance to Peace
        conflict.ForceState(ZoneConflictType.Peace);
        victim.Character.DiedInPvpWarZone = false;
        victim.Character.Hp = victim.Character.MaxHp;
        Kill(victim, killer);

        // In Peace: no honor awarded, DiedInPvpWarZone false
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(40);
        await Assert.That(victim.Character.DiedInPvpWarZone).IsFalse();
    }

    [Test]
    public async Task DoDie_MultiParticipantAssists_ComprehensiveDamageHealAndCc()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        GameplayActorTestRig.SeedBuffTemplate(9801);
        GameplayActorTestRig.SeedBuffTemplate(9802);
        var ccTemplate = new BuffTemplate { Id = 9801, Kind = BuffKind.Bad, Stun = true };
        var nonCcTemplate = new BuffTemplate { Id = 9802, Kind = BuffKind.Bad };
        var buffDict = (Dictionary<uint, BuffTemplate>)typeof(SkillManager)
            .GetField("_buffs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        buffDict[9801] = ccTemplate;
        buffDict[9802] = nonCcTemplate;

        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.War));
        var (killer, victim, session) = CreatePair("pvp-multi-assist");

        // Assistant 1: Damage within 30s
        var (dmgAssist, _) = GameplayActorTestRig.CreateActor("pvp-assist-dmg");
        GameplayActorTestRig.JoinActorWorld(session, dmgAssist);
        Conn(dmgAssist.Character);
        dmgAssist.Character.IsOnline = true;

        // Assistant 2: Heal killer within 30s
        var (healAssist, _) = GameplayActorTestRig.CreateActor("pvp-assist-heal");
        GameplayActorTestRig.JoinActorWorld(session, healAssist);
        Conn(healAssist.Character);
        healAssist.Character.IsOnline = true;

        // Assistant 3: Active CC debuff (Stun) on victim
        var (ccAssist, _) = GameplayActorTestRig.CreateActor("pvp-assist-cc");
        GameplayActorTestRig.JoinActorWorld(session, ccAssist);
        Conn(ccAssist.Character);
        ccAssist.Character.IsOnline = true;

        // Non-assistant 4: Stale damage (>30s ago)
        var (staleAssist, _) = GameplayActorTestRig.CreateActor("pvp-assist-stale");
        GameplayActorTestRig.JoinActorWorld(session, staleAssist);
        Conn(staleAssist.Character);
        staleAssist.Character.IsOnline = true;

        // Non-assistant 5: Non-CC debuff (no stun/root/sleep/silence/crippled)
        var (nonCcAssist, _) = GameplayActorTestRig.CreateActor("pvp-assist-noncc");
        GameplayActorTestRig.JoinActorWorld(session, nonCcAssist);
        Conn(nonCcAssist.Character);
        nonCcAssist.Character.IsOnline = true;

        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9121 };
        SetZone(victim, TestZoneKey);
        victim.Character.HonorPoint = 50;

        // Set up damage history
        SetField(victim.Character, "_pvpDamageHistory", new ConcurrentDictionary<uint, DateTime>
        {
            [dmgAssist.Character.Id] = DateTime.UtcNow.AddSeconds(-5),
            [staleAssist.Character.Id] = DateTime.UtcNow.AddSeconds(-35)
        });

        // Set up heal history on killer
        SetField(killer.Character, "_pvpHealHistory", new ConcurrentDictionary<uint, DateTime>
        {
            [healAssist.Character.Id] = DateTime.UtcNow.AddSeconds(-10),
            [killer.Character.Id] = DateTime.UtcNow.AddSeconds(-2) // killer himself excluded
        });

        // Set up CC debuff on victim
        var ccBuff = new Buff(victim.Character, ccAssist.Character, new SkillCasterUnit(ccAssist.Character.ObjId), ccTemplate, null, DateTime.UtcNow);
        victim.Character.Buffs.AddBuff(ccBuff);

        // Set up non-CC debuff on victim
        var nonCcBuff = new Buff(victim.Character, nonCcAssist.Character, new SkillCasterUnit(nonCcAssist.Character.ObjId), nonCcTemplate, null, DateTime.UtcNow);
        victim.Character.Buffs.AddBuff(nonCcBuff);

        var ex = Kill(victim, killer);
        await Assert.That(ex).IsNull();

        // 3 qualifying online assistants (dmg, heal, cc):
        // Killer gets 32, each assistant gets 4
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(32);
        await Assert.That(killer.Character.HonorGainedInCombat).IsEqualTo(32u);

        await Assert.That(dmgAssist.Character.HonorPoint).IsEqualTo(4);
        await Assert.That(dmgAssist.Character.HonorGainedInCombat).IsEqualTo(4u);

        await Assert.That(healAssist.Character.HonorPoint).IsEqualTo(4);
        await Assert.That(healAssist.Character.HonorGainedInCombat).IsEqualTo(4u);

        await Assert.That(ccAssist.Character.HonorPoint).IsEqualTo(4);
        await Assert.That(ccAssist.Character.HonorGainedInCombat).IsEqualTo(4u);

        await Assert.That(staleAssist.Character.HonorPoint).IsEqualTo(0);
        await Assert.That(nonCcAssist.Character.HonorPoint).IsEqualTo(0);

        // Victim War-zone penalty
        await Assert.That(victim.Character.HonorPoint).IsEqualTo(40);
    }

    [Test]
    public async Task DoDie_AllAssistsOffline_RevertsToSoloHonorAward()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.World.PvpHonorRate = 1.0;

        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.War));
        var (killer, victim, session) = CreatePair("pvp-offline-assist");

        var (offlineAssist, _) = GameplayActorTestRig.CreateActor("pvp-assist-offline");
        GameplayActorTestRig.JoinActorWorld(session, offlineAssist);
        Conn(offlineAssist.Character);
        offlineAssist.Character.IsOnline = false; // Offline!

        killer.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9122 };
        SetZone(victim, TestZoneKey);

        SetField(victim.Character, "_pvpDamageHistory", new ConcurrentDictionary<uint, DateTime>
        {
            [offlineAssist.Character.Id] = DateTime.UtcNow.AddSeconds(-5)
        });

        var ex = Kill(victim, killer);
        await Assert.That(ex).IsNull();

        // When all assistants are offline, killer receives full solo 40 honor
        await Assert.That(killer.Character.HonorPoint).IsEqualTo(40);
        await Assert.That(killer.Character.HonorGainedInCombat).IsEqualTo(40u);
        await Assert.That(offlineAssist.Character.HonorPoint).IsEqualTo(0);
    }

    [Test]
    public async Task DoDie_ConsecutiveDeaths_EscalatesRespawnWaitTimeAndResetsAfterInterval()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        var (killer, victim, session) = CreatePair("pvp-respawn");
        _ = session;
        _ = killer;

        // 1st death: 15s (15000 ms)
        victim.Character.DoDie(null, KillReason.Damage);
        await Assert.That(victim.Character.RezWaitDuration).IsEqualTo(15000);

        // 2nd death: 30s (30000 ms)
        victim.Character.Hp = victim.Character.MaxHp;
        victim.Character.DoDie(null, KillReason.Damage);
        await Assert.That(victim.Character.RezWaitDuration).IsEqualTo(30000);

        // 3rd death: 60s (60000 ms)
        victim.Character.Hp = victim.Character.MaxHp;
        victim.Character.DoDie(null, KillReason.Damage);
        await Assert.That(victim.Character.RezWaitDuration).IsEqualTo(60000);

        // 4th death: 90s (90000 ms)
        victim.Character.Hp = victim.Character.MaxHp;
        victim.Character.DoDie(null, KillReason.Damage);
        await Assert.That(victim.Character.RezWaitDuration).IsEqualTo(90000);

        // Simulate 6 minutes passing without dying
        SetField(victim.Character, "_lastDeathTime", DateTime.UtcNow.AddMinutes(-6));

        // 5th death: counter reset -> back to 15s (15000 ms)
        victim.Character.Hp = victim.Character.MaxHp;
        victim.Character.DoDie(null, KillReason.Damage);
        await Assert.That(victim.Character.RezWaitDuration).IsEqualTo(15000);
    }

    // ------------------------------------------------------------------ b. friendly-fire crime evidence

    [Test]
    public async Task DoDie_FriendlyFireKill_GeneratesLargeBloodstainEvidenceDoodad()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var crimeSwap = ForceSeedRealCrimeManager();

        var (killer, victim, session) = CreatePair("pvp-ff");

        // Same non-root faction → relation Friendly → the friendly-fire branch
        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9105 };
        killer.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(victim, TestZoneKey);

        Exception? ex;
        using (RegisterWorldForDoodadCreation(session))
        {
            ex = Kill(victim, killer);
        }

        // BUG DOCUMENTATION (class header #3): the evidence chain throws from
        // Doodad.Data's auto-Save (via CreatePlayerDoodad) BEFORE the doodad is
        // ever spawned — the exception reaching DoDie proves the friendly-fire
        // branch invoked CrimeManager.GenerateEvidenceFromKill.
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.GetType()).IsEqualTo(typeof(MySqlException));
        await Assert.That(ex.StackTrace.Contains("CreatePlayerDoodad")).IsTrue();
        await Assert.That(victim.Character.IsDead).IsTrue();

        // Engine-true consequence: when the mid-creation save fails, the
        // evidence doodad never reaches the world (nothing reportable exists).
        await Assert.That(session.World.GetAllDoodads().Any(d => d.TemplateId == DoodadConstants.LargeBloodstain))
            .IsFalse();
    }

    [Test]
    public async Task DoDie_FriendlyFireRetaliationKill_SuppressesEvidenceAndClearsAssaultLists()
    {
        using var zoneSwap = SeedConflictZone(CreateConflict(ZoneConflictType.Peace));
        using var crimeSwap = ForceSeedRealCrimeManager();

        var (killer, victim, session) = CreatePair("pvp-ret");

        var sharedFaction = new SystemFaction { Id = (FactionsEnum)9106 };
        killer.Character.Faction = sharedFaction;
        victim.Character.Faction = sharedFaction;
        SetZone(victim, TestZoneKey);

        // Mirror the DamageEffect assault recording (DamageEffect.cs:392-396):
        // the victim had ALREADY assaulted the eventual killer → the kill is
        // retaliation and must NOT generate crime evidence.
        victim.Character.AssaultedBy.Add(killer.Character.Id);
        killer.Character.AssaultOn.Add(victim.Character.Id);

        Exception? ex;
        using (RegisterWorldForDoodadCreation(session))
        {
            ex = Kill(victim, killer);
        }

        // No evidence attempted → no terminal save failure
        await Assert.That(ex).IsNull();
        await Assert.That(victim.Character.IsDead).IsTrue();
        await Assert.That(session.World.GetAllDoodads().Any(d => d.TemplateId == DoodadConstants.LargeBloodstain))
            .IsFalse();

        // Death clears the assault bookkeeping (ClearAllAggro → ClearAssaultList)
        await Assert.That(victim.Character.AssaultedBy).IsEmpty();
        await Assert.That(killer.Character.AssaultOn).IsEmpty();
    }

    /// <summary>
    /// Forces REAL CrimeManager + DoodadManager singletons (mocked edges,
    /// seeded LargeBloodstain template) so the friendly-fire evidence chain
    /// runs engine-true up to its terminal MySQL write. The headless session
    /// world must be registered in the shared WorldManager registry for the
    /// duration of the kill (CreatePlayerDoodad's Transform.InstanceId
    /// round-trip resolves through it) — see RegisterWorldForDoodadCreation.
    /// </summary>
    private static IDisposable ForceSeedRealCrimeManager()
    {
        var nextObjId = 0x6000_0000u;
        var objectIdManager = Mock.Of<AAEmu.Game.Core.Managers.Id.IObjectIdManager>();
        objectIdManager.GetNextId().Returns(() => nextObjId++);

        var itemManager = Mock.Of<IItemManager>();
        itemManager.GetItemIdsFromDoodad(Any<uint>()).Returns([]);

        var doodadManager = new DoodadManager(
            objectIdManager.Object,
            Mock.Of<IDoodadIdManager>().Object,
            itemManager.Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);
        SetField(doodadManager, "_templates",
            new Dictionary<uint, DoodadTemplate>
            {
                // Climate.Any short-circuits the climate lookup; no func groups keeps phase handling inert
                [DoodadConstants.LargeBloodstain] = new() { Id = DoodadConstants.LargeBloodstain, ClimateId = Climate.Any, FuncGroups = [] }
            });
        SetField(doodadManager, "_funcsByGroups", new Dictionary<uint, List<AAEmu.Game.Models.Game.DoodadObj.DoodadFunc>>());
        SetField(doodadManager, "_funcsById", new Dictionary<uint, AAEmu.Game.Models.Game.DoodadObj.DoodadFunc>());
        SetField(doodadManager, "_funcTemplates",
            new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>());

        // Doodad.Save allocates its DbId through the STATIC DoodadIdManager.Instance —
        // initialize it once (its own DB read failure is internally swallowed).
        DoodadIdManager.Instance.Initialize();

        var crimeSwap = SingletonSwap.Install(typeof(Singleton<CrimeManager>), new CrimeManager());
        var doodadSwap = SingletonSwap.Install(typeof(Singleton<DoodadManager>), doodadManager);
        return new CompositeSwap(crimeSwap, doodadSwap);
    }

    /// <summary>
    /// Registers a headless world in the shared WorldManager instance registry
    /// so engine paths that round-trip Transform.InstanceId resolve it.
    /// Removal is conditional (only when the slot still holds OUR world) to
    /// stay safe under full-suite parallelism.
    /// </summary>
    private static IDisposable RegisterWorldForDoodadCreation(HeadlessSession session)
    {
        var worlds = (ConcurrentDictionary<uint, AAEmu.Game.Models.Game.World.WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(WorldManager.Instance)!;
        worlds[session.World.Id] = session.World;
        return new CompositeSwap(new RemoveWorldOnDispose(worlds, session.World));
    }

    private sealed class RemoveWorldOnDispose(
        ConcurrentDictionary<uint, AAEmu.Game.Models.Game.World.WorldInstance> worlds,
        AAEmu.Game.Models.Game.World.WorldInstance world) : IDisposable
    {
        public void Dispose()
        {
            if (worlds.TryGetValue(world.Id, out var current) && ReferenceEquals(current, world))
                worlds.TryRemove(world.Id, out _);
        }
    }

    private sealed class CompositeSwap(params IDisposable[] swaps) : IDisposable
    {
        public void Dispose()
        {
            foreach (var swap in swaps)
                swap.Dispose();
        }
    }

    private static TestableZoneConflict CreateConflict(ZoneConflictType state)
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(state);
        return conflict;
    }

    // ------------------------------------------------------------------ c. peace protection through CanAttack

    [Test]
    public async Task CanAttack_PeaceZoneProtection_IntegratesWithFlaggingPaths()
    {
        var conflict = CreateConflict(ZoneConflictType.Peace);
        using var zoneSwap = SeedConflictZone(conflict);

        // FactionManager surface for CanAttack's zone-faction resolution
        var factionManager = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        SetField(factionManager, "_systemFactions",
            new Dictionary<FactionsEnum, SystemFaction>
            {
                [FactionsEnum.Neutral] = new() { Id = FactionsEnum.Neutral, Name = "Neutral" }
            });
        using var factionSwap = SingletonSwap.Install(typeof(Singleton<FactionManager>), factionManager);

        var (neutralAttacker, victim, session) = CreatePair("pvp-canatk");
        _ = session;

        // Distinct non-root factions → Neutral relation
        neutralAttacker.Character.Faction = new SystemFaction { Id = (FactionsEnum)9201 };
        victim.Character.Faction = new SystemFaction { Id = (FactionsEnum)9202 };
        SetZone(victim, TestZoneKey);

        // A hostile-relation third party bypasses peace protection (pirate-style)
        var hostile = GameplayActorTestRig.CreateActor("pvp-canatk-hostile").Actor;
        GameplayActorTestRig.JoinActorWorld(session, hostile);
        hostile.Character.Faction = new SystemFaction { Id = FactionsEnum.Hostile };

        // Peace shields NON-hostile players end-to-end through the real CanAttack
        await Assert.That(neutralAttacker.Character.CanAttack(victim.Character)).IsFalse();
        // …while flagged-hostile relations stay attackable in the same Peace zone.
        await Assert.That(hostile.Character.CanAttack(victim.Character)).IsTrue();

        // Outside Peace (Tension/War/…) the zone-conflict GATE lifts, but the
        // underlying relation gate still refuses Neutral→Neutral attacks —
        // zone state only ever ADDS protection, it never enables PvP by itself.
        conflict.ForceState(ZoneConflictType.War);
        await Assert.That(neutralAttacker.Character.CanAttack(victim.Character)).IsFalse();
        await Assert.That(hostile.Character.CanAttack(victim.Character)).IsTrue();

        // Zones without a conflict entry: the peace gate fails OPEN (matches
        // ZoneConflictTests.BlocksPvpDamage_NullConflict) — the composite
        // predicate then reduces to the pure relation check.
        var conflicts = (Dictionary<ushort, ZoneConflict>)typeof(ZoneManager)
            .GetField("_conflicts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(GetSingletonInstance(typeof(Singleton<ZoneManager>)))!;
        conflicts.Clear();
        await Assert.That(neutralAttacker.Character.CanAttack(victim.Character)).IsFalse();
        await Assert.That(hostile.Character.CanAttack(victim.Character)).IsTrue();
    }
}
