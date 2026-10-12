using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class GameplayActorUseItemPacketTests
{
    [Test]
    [Arguments(0)]
    [Arguments(5000)] // Canonical item 7738 / skill 11596 has this cast time.
    public async Task UseItem_CastTimeOrInstant_SendsCompleteFiredPacketAndConsumesOnce(int castTime)
    {
        // Seed a minimal real-engine skill/reagent fixture, rather than assigning
        // a quest or injecting effects. Capture the ordinary GameConnection wire.
        var (actor, session) = GameplayActorTestRig.CreateActor($"item-wire-{castTime}");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestItemUseSkillId);
        var previousCastTime = template.CastingTime;
        var capture = new CaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        var queue = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(TaskManager.Instance)!;
        var previousTasks = queue.Keys.ToHashSet();
        try
        {
            template.CastingTime = castTime;

            var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "item-wire");

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
            // This mandatory byte is omitted by the broken manual Cast(null).
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)SkillObjectType.None);
            await Assert.That(wire.ReadInt16()).IsEqualTo((short)10); // effect delay
            await Assert.That(wire.ReadInt16()).IsEqualTo((short)10); // channel time
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)0);
            await Assert.That(wire.ReadUInt32()).IsEqualTo(0u); // no fire animation in fixture
            await Assert.That(wire.ReadByte()).IsEqualTo((byte)0);
            await Assert.That(wire.LeftBytes).IsEqualTo(0);
            await Assert.That(actor.Character.SkillTask).IsNull();

            var newCastTasks = queue.Where(x => !previousTasks.Contains(x.Key))
                .Select(x => x.Value).OfType<CastTask>().ToArray();
            await Assert.That(newCastTasks.Length).IsEqualTo(castTime > 0 ? 1 : 0);
            foreach (var scheduled in newCastTasks)
            {
                // A late callback must not fire or consume a second time.
                await Assert.That(scheduled.Skill.Cancelled).IsTrue();
                scheduled.Execute();
            }
            var retry = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "item-wire");
            await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
            await Assert.That(FiredPackets(capture).Count()).IsEqualTo(1);
        }
        finally
        {
            template.CastingTime = previousCastTime;
            actor.Character.Connection = null;
            foreach (var task in queue.Where(x => !previousTasks.Contains(x.Key)).Select(x => x.Value).ToArray())
                TaskManager.Instance.Cancel(task);
        }
    }

    private static IEnumerable<byte[]> FiredPackets(CaptureSession capture)
        => capture.Packets.Where(bytes => bytes.Length >= 8
            && BitConverter.ToUInt16(bytes, 6) == SCOffsets.SCSkillFiredPacket);

    private sealed class CaptureSession : ISession
    {
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
