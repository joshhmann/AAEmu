using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class GameplayActorUseItemPacketTests
{
    [Test]
    public async Task UseItem_DelayedCast_RemainsPendingWithoutEarlyConsumption()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-pending-native-cast");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestItemUseSkillId);
        var previousCastTime = template.CastingTime;
        var queue = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(TaskManager.Instance)!;
        var previousTasks = queue.Keys.ToHashSet();
        try
        {
            template.CastingTime = 5000;
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(ReferenceEquals(actor.ActiveRequest, request)).IsTrue();
            actor.Tick(TimeSpan.FromSeconds(1));
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
            template.CastingTime = previousCastTime;
            foreach (var task in queue.Where(x => !previousTasks.Contains(x.Key)).Select(x => x.Value).ToArray())
                TaskManager.Instance.Cancel(task);
        }
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1500, 0)] // Ordinary bread-shaped casting time.
    [Arguments(0, 200)] // Instant cast can still have scheduled effects.
    public async Task UseItem_OrdinaryCallbacks_SendCompleteFiredPacketAndConsumeOnce(int castTime, int effectDelay)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor($"item-wire-{castTime}-{effectDelay}");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(castTime, effectDelay);
        using var scheduler = new NativeScheduler();
        var capture = new CaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        try
        {
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "item-wire");
            if (castTime > 0 || effectDelay > 0)
            {
                await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
                await Assert.That(ReferenceEquals(actor.ActiveRequest, request)).IsTrue();
                await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);
                var busy = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "item-wire");
                await Assert.That(busy.State).IsEqualTo(ActorLifecycleState.Rejected);
                await Assert.That(ReferenceEquals(actor.ActiveRequest, request)).IsTrue();
                await scheduler.WaitForTerminal(actor, request);
            }

            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            var fired = FiredPackets(capture).Single();
            var wire = new PacketStream(fired);
            await Assert.That(wire.ReadUInt16()).IsEqualTo((ushort)(fired.Length - 2));
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)0xdd);
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)1);
            wire.ReadByte(); // hash
            wire.ReadByte(); // packet count
            await Assert.That(wire.ReadUInt16()).IsEqualTo(SCOffsets.SCSkillFiredPacket);
            await Assert.That(wire.ReadUInt32()).IsEqualTo(GameplayActorTestRig.TestItemUseSkillId);
            wire.ReadUInt16(); // skill timeline ID
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)SkillCasterType.Item);
            await Assert.That(wire.ReadBc()).IsEqualTo(actor.Character.ObjId);
            await Assert.That(wire.ReadUInt64()).IsGreaterThan(0UL);
            await Assert.That(wire.ReadUInt32()).IsEqualTo(GameplayActorTestRig.TestItemTemplateId);
            wire.ReadByte(); // item caster Type1
            wire.ReadUInt32(); // item caster Type2
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)SkillCastTargetType.Unit);
            await Assert.That(wire.ReadBc()).IsEqualTo(actor.Character.ObjId);
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)SkillObjectType.None);
            await Assert.That(wire.ReadInt16()).IsEqualTo((short)(effectDelay / 10 + 10));
            await Assert.That(wire.ReadInt16()).IsEqualTo((short)10); // channel time
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)0);
            await Assert.That(wire.ReadUInt32()).IsEqualTo(0u);
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)0);
            await Assert.That(wire.LeftBytes).IsEqualTo(0);
            await Assert.That(actor.Character.SkillTask).IsNull();

            var retry = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "item-wire");
            await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(250));
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            await Assert.That(FiredPackets(capture).Count()).IsEqualTo(1);
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
            actor.Character.Connection = null;
        }
    }

    [Test]
    public async Task UseItem_InstantHeal_CompletesAfterOrdinaryEffect()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-instant-heal");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(0, 0);
        fixture.AddEffect(new HealEffect { UseFixedHeal = true, FixedMin = 990, FixedMax = 990 });
        var before = Math.Max(1, actor.Character.MaxHp / 3);
        actor.Character.Hp = before; // Initial damaged-vital fixture, not completion.

        var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
        await Assert.That(actor.Character.Hp).IsEqualTo(Math.Min(actor.Character.MaxHp, before + 990));
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task UseItem_DelayedBuff_CompletesApplicationNotVitalRecovery()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-delayed-buff");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(1500, 0);
        using var scheduler = new NativeScheduler();
        const uint buffId = 98_188;
        var buff = new BuffTemplate { Id = buffId, Duration = 30_000, Tick = 2000 };
        fixture.AddTickHeal(buff, 98_539, 600);
        fixture.AddEffect(new BuffEffect { Chance = 100, Buff = buff });
        var before = Math.Max(1, actor.Character.MaxHp / 3);
        actor.Character.Hp = before;
        try
        {
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(actor.Character.Buffs.CheckBuff(buffId)).IsFalse();
            await Assert.That(actor.Character.Hp).IsEqualTo(before);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);

            await scheduler.WaitForTerminal(actor, request);

            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
            await Assert.That(actor.Character.Buffs.CheckBuff(buffId)).IsTrue();
            await Assert.That(actor.Character.Hp).IsEqualTo(before);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(2150));
            await Assert.That(actor.Character.Hp).IsGreaterThan(before);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            actor.Character.Buffs.RemoveBuff(buffId);
            var afterRemoval = actor.Character.Hp;
            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(2150));
            await Assert.That(actor.Character.Buffs.CheckBuff(buffId)).IsFalse();
            await Assert.That(actor.Character.Hp).IsEqualTo(afterRemoval);
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
            actor.Character.Buffs.RemoveBuff(buffId);
        }
    }

    [Test]
    [Arguments(150, 0)]
    [Arguments(0, 150)]
    public async Task UseItem_Preempt_CancelsOnlyOwnedDeferredWork(int castTime, int effectDelay)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-preempt");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(castTime, effectDelay);
        using var scheduler = new NativeScheduler();
        var capture = new CaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        try
        {
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "owned-item");
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(actor.PreemptCurrent("combat preempt")).IsTrue();
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
            await Assert.That(actor.ActiveRequest).IsNull();
            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(400));
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);
            await Assert.That(FiredPackets(capture).Count()).IsEqualTo(castTime > 0 ? 0 : 1);
            await Assert.That(actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "owned-item").State)
                .IsEqualTo(ActorLifecycleState.Rejected);
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
            actor.Character.Connection = null;
        }
    }

    [Test]
    [Arguments("native-stop")]
    [Arguments("native-task-cancel")]
    [Arguments("death")]
    [Arguments("timeout")]
    public async Task UseItem_NativeCancellationOrBudget_IsTerminalWithoutConsumption(string interruption)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor($"item-{interruption}");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(150, 0);
        using var scheduler = new NativeScheduler();
        try
        {
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "cancelled-item");
            var nativeTask = actor.Character.SkillTask!;
            switch (interruption)
            {
                case "native-stop":
                    nativeTask.Skill.Stop(actor.Character);
                    break;
                case "native-task-cancel":
                    TaskManager.Instance.Cancel(nativeTask);
                    break;
                case "death":
                    actor.Character.Hp = 0; // Native death condition before the cast, not a completion signal.
                    break;
                case "timeout":
                    actor.Tick(GameplayActor.DefaultItemUseTimeout + TimeSpan.FromSeconds(1));
                    break;
            }
            actor.Tick(TimeSpan.Zero);
            await Assert.That(request.State).IsEqualTo(interruption == "timeout"
                ? ActorLifecycleState.TimedOut : ActorLifecycleState.Interrupted);
            await Assert.That(actor.ActiveRequest).IsNull();
            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(400));
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);
            await Assert.That(actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "cancelled-item").State)
                .IsEqualTo(ActorLifecycleState.Rejected);
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
        }
    }

    [Test]
    [Arguments(150, 0)]
    [Arguments(0, 150)]
    public async Task UseItem_ExpiredBudget_PreventsNativeCallbackEvenWithoutActorTicks(int castTime, int effectDelay)
    {
        var (_, session) = GameplayActorTestRig.CreateActor("item-native-budget");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var actor = new GameplayActor(session.Character) { TimeProvider = clock };
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(castTime, effectDelay);
        using var scheduler = new NativeScheduler();
        try
        {
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "expired-native");
            clock.Advance(GameplayActor.DefaultItemUseTimeout + TimeSpan.FromSeconds(1));

            await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(400));

            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(2);
            actor.Tick(TimeSpan.Zero);
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
            await Assert.That(actor.ActiveRequest).IsNull();
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
        }
    }

    [Test]
    public async Task UseItem_CompletionCallbackFault_DoesNotReportCompletedOrReplay()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-callback-fault");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(150, 0);
        using var scheduler = new NativeScheduler();
        try
        {
            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "faulted-item");
            var calls = 0;
            actor.Character.SkillTask!.Skill.Callback = () =>
            {
                calls++;
                throw new InvalidOperationException("fixture native callback fault");
            };

            await scheduler.WaitForTerminal(actor, request);

            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
            await Assert.That(request.Detail?.Contains("failed")).IsTrue();
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            await Assert.That(actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "faulted-item").State)
                .IsEqualTo(ActorLifecycleState.Rejected);
        }
        finally
        {
            actor.PreemptCurrent("test cleanup");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UseItem_ForeignNativeSkill_IsNotReplacedAndKeepsOrdinaryCallbackOrder(bool stop)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-foreign-native");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(150, 0);
        using var scheduler = new NativeScheduler();
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestItemUseSkillId);
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(GameplayActorTestRig.TestItemTemplateId, -1, out var items, out _);
        var item = items.First();
        var skill = new Skill(template);
        var calls = 0;
        var cancelledAtCallback = true;
        skill.Callback = () =>
        {
            calls++;
            cancelledAtCallback = skill.Cancelled;
        };
        try
        {
            var nativeResult = skill.Use(actor.Character, new SkillItem(actor.Character.ObjId, item.Id, item.TemplateId),
                new SkillCastUnitTarget(actor.Character.ObjId), new SkillObject(), false, out _);
            await Assert.That(nativeResult).IsEqualTo(SkillResult.Success);
            var nativeTask = actor.Character.SkillTask;
            var refused = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
            await Assert.That(refused.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(refused.Failure).IsEqualTo(ActorFailureReason.StateTransition);
            await Assert.That(ReferenceEquals(actor.Character.SkillTask, nativeTask)).IsTrue();
            await Assert.That(skill.Cancelled).IsFalse();
            if (stop)
                skill.Stop(actor.Character);

            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(400));

            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(cancelledAtCallback).IsFalse();
            await Assert.That(skill.Cancelled).IsEqualTo(stop);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId))
                .IsEqualTo(stop ? 2 : 1);
        }
        finally
        {
            if (actor.Character.SkillTask != null)
                skill.Stop(actor.Character);
        }
    }

    [Test]
    public async Task UseItem_OrdinaryCooldownRefusal_DoesNotConsumeOrFireAgain()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("item-cooldown");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        using var fixture = new ItemSkillFixture(0, 0, 90_000);
        var capture = new CaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        try
        {
            var original = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            var refusal = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
            await Assert.That(refusal.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(refusal.Detail?.Contains("CooldownTime")).IsTrue();
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            await Assert.That(FiredPackets(capture).Count()).IsEqualTo(1);
        }
        finally
        {
            actor.Character.Connection = null;
        }
    }

    private sealed class ItemSkillFixture : IDisposable
    {
        private readonly SkillTemplate _template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestItemUseSkillId);
        private readonly int _castTime;
        private readonly int _effectDelay;
        private readonly int _cooldownTime;
        private readonly List<SkillEffect> _effects;
        private readonly FieldInfo _typesField = typeof(SkillManager).GetField("_types", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private readonly FieldInfo _effectsField = typeof(SkillManager).GetField("_effects", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private readonly FieldInfo _buffsField = typeof(SkillManager).GetField("_buffs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private readonly object? _previousTypes;
        private readonly object? _previousEffects;
        private readonly object? _previousBuffs;

        public ItemSkillFixture(int castTime, int effectDelay, int cooldownTime = 0)
        {
            _castTime = _template.CastingTime;
            _effectDelay = _template.EffectDelay;
            _cooldownTime = _template.CooldownTime;
            _effects = _template.Effects.ToList();
            _previousTypes = _typesField.GetValue(SkillManager.Instance);
            _previousEffects = _effectsField.GetValue(SkillManager.Instance);
            _previousBuffs = _buffsField.GetValue(SkillManager.Instance);
            _template.CastingTime = castTime;
            _template.EffectDelay = effectDelay;
            _template.CooldownTime = cooldownTime;
            _template.Effects.Clear();
        }

        public void AddEffect(EffectTemplate effect)
            => _template.Effects.Add(new SkillEffect
            {
                Template = effect,
                EndLevel = byte.MaxValue,
                Friendly = true,
                NonFriendly = true,
                Chance = 100,
                AlwaysHit = true,
                ApplicationMethod = SkillEffectApplicationMethod.Target
            });

        public void AddTickHeal(BuffTemplate buff, uint effectId, int amount)
        {
            var types = _previousTypes is Dictionary<uint, EffectType> previousTypes
                ? new Dictionary<uint, EffectType>(previousTypes) : new Dictionary<uint, EffectType>();
            var effects = _previousEffects is Dictionary<string, Dictionary<uint, EffectTemplate>> previousEffects
                ? new Dictionary<string, Dictionary<uint, EffectTemplate>>(previousEffects)
                : new Dictionary<string, Dictionary<uint, EffectTemplate>>();
            var heals = effects.TryGetValue(nameof(HealEffect), out var previousHeals)
                ? new Dictionary<uint, EffectTemplate>(previousHeals) : new Dictionary<uint, EffectTemplate>();
            heals[effectId] = new HealEffect { Id = effectId, UseFixedHeal = true, FixedMin = amount, FixedMax = amount };
            effects[nameof(HealEffect)] = heals;
            types[effectId] = new EffectType { Id = effectId, ActualId = effectId, Type = nameof(HealEffect) };
            var buffs = _previousBuffs is Dictionary<uint, BuffTemplate> previousBuffs
                ? new Dictionary<uint, BuffTemplate>(previousBuffs) : new Dictionary<uint, BuffTemplate>();
            buffs[buff.Id] = buff;
            _buffsField.SetValue(SkillManager.Instance, buffs);
            _typesField.SetValue(SkillManager.Instance, types);
            _effectsField.SetValue(SkillManager.Instance, effects);
            buff.TickEffects.Add(new TickEffect { EffectId = effectId });
        }

        public void Dispose()
        {
            _template.CastingTime = _castTime;
            _template.EffectDelay = _effectDelay;
            _template.CooldownTime = _cooldownTime;
            _template.Effects.Clear();
            _template.Effects.AddRange(_effects);
            _typesField.SetValue(SkillManager.Instance, _previousTypes);
            _effectsField.SetValue(SkillManager.Instance, _previousEffects);
            _buffsField.SetValue(SkillManager.Instance, _previousBuffs);
        }
    }

    // Isolated ordinary scheduler: the native tick event dispatches TaskManager,
    // which dispatches its actual queued callbacks. No Execute/EndSkill injection.
    internal sealed class NativeScheduler : IDisposable
    {
        private readonly FieldInfo _singleton = typeof(Singleton<TaskManager>)
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object _previous;
        private readonly FieldInfo _effectSingleton = typeof(Singleton<EffectTaskManager>)
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? _previousEffectManager;
        private readonly TickManager _ticks = new();
        private readonly TaskManager _manager;

        public NativeScheduler()
        {
            _previous = _singleton.GetValue(null)!;
            _manager = new TaskManager(_ticks);
            _singleton.SetValue(null, _manager);
            _previousEffectManager = _effectSingleton.GetValue(null);
            _effectSingleton.SetValue(null, new EffectTaskManager(_manager));
            _manager.Start();
        }

        public async Task WaitForTerminal(GameplayActor actor, ActorRequest request)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!request.IsTerminal && DateTime.UtcNow < deadline)
            {
                _ticks.OnTick.Invoke();
                await Task.Delay(20);
                actor.Tick(TimeSpan.FromMilliseconds(20));
            }
        }

        public async Task PumpFor(GameplayActor? actor, TimeSpan duration)
        {
            var deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                _ticks.OnTick.Invoke();
                await Task.Delay(20);
                actor?.Tick(TimeSpan.FromMilliseconds(20));
            }
        }

        public void Dispose()
        {
            var queue = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
                .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_manager)!;
            foreach (var task in queue.Values)
                _manager.Cancel(task);
            _singleton.SetValue(null, _previous);
            _effectSingleton.SetValue(null, _previousEffectManager);
        }
    }

    private static IEnumerable<byte[]> FiredPackets(CaptureSession capture)
        => capture.Packets.Where(bytes => bytes.Length >= 8
            && BitConverter.ToUInt16(bytes, 6) == SCOffsets.SCSkillFiredPacket);

    private sealed class CaptureSession : ISession
    {
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Enqueue(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
