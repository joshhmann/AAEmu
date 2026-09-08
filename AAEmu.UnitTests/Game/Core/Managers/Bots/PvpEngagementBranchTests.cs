using AAEmu.Commons.Network.Core;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills.Templates;

using Microsoft.Extensions.Time.Testing;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Conflict PvP branch rig (war-horn model, executor side): while the
/// arbiter holds a conflict.* activity, hostile players preempt wildlife.
/// Fail-pre discipline: engagement tests FAIL if the bot ignores a live
/// hostile, the negative tests FAIL if it engages without activity or
/// against a refused target, and the damage test FAILS if no Completed
/// cast lands (victim HP unchanged).
/// </summary>
[NotInParallel]
public class PvpEngagementBranchTests
{
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

    private static void Conn(Character c)
    {
        c.Connection = new GameConnection(new PacketCaptureSession()) { ActiveChar = c };
    }

    private static (GameplayActor Attacker, GameplayActor Victim, HeadlessSession Session) CreatePair(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (attacker, session) = GameplayActorTestRig.CreateActor(name + "-attacker");
        var (victim, _) = GameplayActorTestRig.CreateActor(name + "-victim");
        GameplayActorTestRig.JoinActorWorld(session, victim);
        Conn(attacker.Character);
        Conn(victim.Character);
        return (attacker, victim, session);
    }

    private static (BotRoamStepExecutor Executor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateExecutor(
        GameplayActor attacker,
        string? activity,
        Func<Character, Character, bool>? canAttack,
        Func<Character, float, IEnumerable<Character>>? nearby)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var runtime = new PlayerBotRuntime(attacker.Character, "rig");
        var clock = new FakeTimeProvider();

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => attacker,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            ActiveActivityProvider = activity == null ? null : (_ => activity),
            CanAttackPlayer = canAttack,
            NearbyCharacterProvider = nearby,
        };
        return (executor, runtime, clock);
    }

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

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);

    private static void SetSingletonInstance(Type singletonBase, object instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, instance);

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);
    }

    private static IDisposable SeedBareZoneManager()
    {
        var zoneManager = new ZoneManager(Mock.Of<AAEmu.Game.Core.Managers.World.IWorldManager>().Object);
        SetField(zoneManager, "_zoneIdToKey", new Dictionary<uint, uint>());
        SetField(zoneManager, "_zones", new Dictionary<uint, Zone>());
        SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>());
        SetField(zoneManager, "_conflicts", new Dictionary<ushort, ZoneConflict>());
        SetField(zoneManager, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
        SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        return SingletonSwap.Install(typeof(Singleton<ZoneManager>), zoneManager);
    }

    [Test]
    public async Task PvpBranch_ConflictActivity_DistantHostile_MovesToTarget()
    {
        var (attacker, victim, _) = CreatePair("pvp-move");
        GameplayActorTestRig.SetPosition(attacker, new System.Numerics.Vector3(0, 0, 0));
        GameplayActorTestRig.SetPosition(victim, new System.Numerics.Vector3(20, 0, 0));
        var (executor, runtime, clock) = CreateExecutor(attacker, "conflict.23163",
            canAttack: (_, _) => true, nearby: (_, _) => [victim.Character]);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        await Assert.That(attacker.Character.CurrentTarget).IsNotNull();
        await Assert.That(attacker.Character.CurrentTarget!.ObjId).IsEqualTo(victim.Character.ObjId);
        await Assert.That(attacker.ActiveRequest).IsNotNull();
        await Assert.That(attacker.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);
    }

    [Test]
    public async Task PvpBranch_InMeleeRange_CastsAndDamages()
    {
        using var combat = HalcyonaSkirmishRigTests.SeedSkirmishCombat();
        using var zones = SeedBareZoneManager();
        var (attacker, victim, _) = CreatePair("pvp-cast");
        GameplayActorTestRig.SetPosition(attacker, new System.Numerics.Vector3(0, 0, 0));
        GameplayActorTestRig.SetPosition(victim, new System.Numerics.Vector3(2, 0, 0));
        attacker.Character.Skills.Skills.Remove(GameplayActorTestRig.TestSkillId);
        attacker.Character.Skills.AddSkill(new SkillTemplate { Id = HalcyonaSkirmishScenario.DefaultCastSkillId }, 1, false);
        // Alliance factions (canonical Hostile pair): the real DamageEffect
        // path resolves relations through the seeded FactionManager.
        var (nuia, haranya) = HalcyonaSkirmishScenario.SeedAllianceFactions();
        attacker.Character.Faction = nuia;
        victim.Character.Faction = haranya;
        var hpBefore = victim.Character.Hp;
        var (executor, runtime, clock) = CreateExecutor(attacker, "conflict.23163",
            canAttack: (_, _) => true, nearby: (_, _) => [victim.Character]);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var casts = attacker.AuditTrace
            .Where(r => r.Action == ActorActionType.Cast && r.TargetId == victim.Character.ObjId)
            .ToList();
        await Assert.That(casts.Any(r => r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(victim.Character.Hp).IsLessThan(hpBefore);
    }

    [Test]
    public async Task PvpBranch_NoConflictActivity_IgnoresHostile()
    {
        var (attacker, victim, _) = CreatePair("pvp-quiet");
        GameplayActorTestRig.SetPosition(attacker, new System.Numerics.Vector3(0, 0, 0));
        GameplayActorTestRig.SetPosition(victim, new System.Numerics.Vector3(2, 0, 0));
        var (executor, runtime, clock) = CreateExecutor(attacker, null,
            canAttack: (_, _) => true, nearby: (_, _) => [victim.Character]);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(attacker.Character.CurrentTarget).IsNull();
        var casts = attacker.AuditTrace
            .Where(r => r.Action == ActorActionType.Cast && r.TargetId == victim.Character.ObjId)
            .ToList();
        await Assert.That(casts).IsEmpty();
    }

    [Test]
    public async Task PvpBranch_NonAttackable_Skipped()
    {
        var (attacker, victim, _) = CreatePair("pvp-refused");
        GameplayActorTestRig.SetPosition(attacker, new System.Numerics.Vector3(0, 0, 0));
        GameplayActorTestRig.SetPosition(victim, new System.Numerics.Vector3(2, 0, 0));
        var (executor, runtime, clock) = CreateExecutor(attacker, "conflict.23163",
            canAttack: (_, _) => false, nearby: (_, _) => [victim.Character]);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(attacker.Character.CurrentTarget).IsNull();
    }

    [Test]
    public async Task PvpBranch_DeadTarget_Cleared()
    {
        var (attacker, victim, _) = CreatePair("pvp-dead");
        GameplayActorTestRig.SetPosition(attacker, new System.Numerics.Vector3(0, 0, 0));
        GameplayActorTestRig.SetPosition(victim, new System.Numerics.Vector3(2, 0, 0));
        victim.Character.Hp = 0;
        var (executor, runtime, clock) = CreateExecutor(attacker, "conflict.23163",
            canAttack: (_, _) => true, nearby: (_, _) => [victim.Character]);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(attacker.Character.CurrentTarget).IsNull();
    }
}
