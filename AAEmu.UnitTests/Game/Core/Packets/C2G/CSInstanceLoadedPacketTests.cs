using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

/// <summary>
/// Lane A incident fix #1 (mirage-duel desync 2026-09-15): CSInstanceLoadedPacket
/// cleared DisabledSetPosition and re-sent unit state + time-of-day but never
/// repopulated visible objects — unlike CSTeleportEndedPacket, which calls
/// WorldManager.ResendVisibleObjectsToCharacter. Any instance entry whose client
/// state went stale stranded the client with no NPCs/doodads/players and no
/// retry. Read now mirrors the teleport-ended resend.
/// </summary>
[NotInParallel] // seeds process-wide singletons + the shared WorldManager character registry
public class CSInstanceLoadedPacketTests
{
    private const uint DoodadObjId = 0x7001;
    private const uint DoodadTemplateId = 777_001;

    [Test]
    public async Task Read_ResendsVisibleDoodads_AlongsideUnitStateAndTimeOfDay()
    {
        SeedPacketSurface();
        var (actor, session) = GameplayActorTestRig.CreateActor("instance-loaded-resend");
        var character = actor.Character;
        character.DisabledSetPosition = true;

        // Place the character and a nearby doodad in the same region so the
        // resend neighborhood has real data to re-send.
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        await Assert.That(region).IsNotNull();
        region!.AddObject(character);
        character.Region = region;
        var doodad = SpawnDoodadAtCharacter(session, character);
        region.AddObject(doodad);
        doodad.Region = region;

        var capture = new PacketCaptureSession();
        var connection = new GameConnection(capture) { ActiveChar = character };
        character.Connection = connection;

        Deliver(new CSInstanceLoadedPacket(), connection, new PacketStream());

        var opcodes = CapturedOpcodes(capture);
        // Pre-existing behavior is preserved ...
        await Assert.That(opcodes).Contains(SCOffsets.SCUnitStatePacket);
        await Assert.That(opcodes).Contains(SCOffsets.SCDetailedTimeOfDayPacket);
        await Assert.That(character.DisabledSetPosition).IsFalse();
        // ... and the neighborhood is re-sent (absent pre-fix).
        await Assert.That(opcodes).Contains(SCOffsets.SCDoodadsCreatedPacket);
    }

    [Test]
    public async Task Read_NullActiveChar_IsSafeNoOp()
    {
        var capture = new PacketCaptureSession();
        var connection = new GameConnection(capture);

        Deliver(new CSInstanceLoadedPacket(), connection, new PacketStream());

        await Assert.That(capture.CapturedPackets).IsEmpty();
    }

    /// <summary>Delivers a client payload through the real decode seam (MerchantRigTests.Deliver).</summary>
    private static void Deliver(GamePacket packet, GameConnection connection, PacketStream payload)
    {
        // PacketBase<T>.Connection has a public setter (protected getter).
        packet.Connection = connection;
        packet.Read(payload);
    }

    private static Doodad SpawnDoodadAtCharacter(HeadlessSession session, Character character)
    {
        var doodad = new Doodad { ObjId = DoodadObjId, TemplateId = DoodadTemplateId };
        // Headless-registry bypass (MerchantRigTests.SpawnDoodadAtActor): pre-set the
        // backing fields so Region.AddObject's InstanceId assignment no-ops instead of
        // re-entering the shared WorldManager registry.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(doodad.Transform, session.World.Id);
        typeof(GameObject)
            .GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(doodad, session.World);
        // 1 m from the character — comfortably inside the same region.
        var pos = character.Transform.World.Position;
        doodad.Transform.Local.SetPosition(new Vector3(pos.X + 1f, pos.Y, pos.Z));
        session.World.AddObject(doodad);
        return doodad;
    }

    private static List<ushort> CapturedOpcodes(PacketCaptureSession capture)
    {
        var opcodes = new List<ushort>();
        foreach (var bytes in capture.CapturedPackets)
        {
            try
            {
                var stream = new PacketStream();
                stream.Write(bytes);
                stream.ReadUInt16(); // length prefix
                stream.ReadByte();   // 0xdd
                stream.ReadByte();   // level
                stream.ReadByte();   // hash
                stream.ReadByte();   // count
                opcodes.Add(stream.ReadUInt16()); // TypeId
            }
            catch
            {
                // malformed capture — skip
            }
        }

        return opcodes;
    }

    /// <summary>
    /// Seeds the DI singletons SCUnitStatePacket.Write touches for a Character
    /// (mirrors SCUnitStatePacketVisualOptionsTests.SeedPacketSurface). Per-singleton
    /// missing-only guards — never replaces an established singleton.
    /// </summary>
    private static void SeedPacketSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<SkillManager>)))
        {
            SeedSingleton(typeof(Singleton<SkillManager>),
                new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
        }

        var manager = SkillManager.Instance;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(manager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(manager, Activator.CreateInstance(dictType));
            }
        }
        var buffs = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!.GetValue(manager)!;
        foreach (var id in new[] { 8000011u, 8000012u })
        {
            if (!buffs.ContainsKey(id))
                buffs[id] = new BuffTemplate { Id = id, Duration = 1, Kind = BuffKind.Good };
        }

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        if (!SingletonSeeded(typeof(Singleton<EffectTaskManager>)))
        {
            SeedSingleton(typeof(Singleton<EffectTaskManager>),
                new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        }
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) != null;

    private static void SeedSingleton(Type singletonBase, object instance)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, instance);
}
