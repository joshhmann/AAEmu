using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

/// <summary>
/// SCUnitModelPostureChangedPacket serialization: the posture section (type=4 ActorModelState)
/// must carry a sit-range anim id that the 1.2 client can actually play for the NPC's race/gender
/// (remapped via SitPoseFallback). Stand poses must serialize with their original id.
/// </summary>
public class SCUnitModelPostureChangedPacketTests
{
    private static Npc MakeNpc(Race race, Gender gender, uint animActionId)
    {
        var template = new NpcTemplate
        {
            Race = (byte)race,
            Gender = (byte)gender,
        };
        template.NpcPostureSets.Add(new NpcPosture { AnimActionId = animActionId, StartTodTime = 0 });
        return new Npc { Template = template, ObjId = 0x1234 };
    }

    private static (byte postureType, bool isLooted, uint animActionId, bool activate) ReadPosture(PacketStream stream)
    {
        var postureType = stream.ReadByte();
        var isLooted = stream.ReadBoolean();
        // postureType 4 (ActorModelState) -> uint animActionId + bool activate
        var animActionId = postureType == (byte)ModelPostureType.ActorModelState ? stream.ReadUInt32() : 0;
        var activate = postureType == (byte)ModelPostureType.ActorModelState && stream.ReadBoolean();
        return (postureType, isLooted, animActionId, activate);
    }

    [Test]
    public async Task Write_ElfMale_SitLean_SerializesRemappedChairRest()
    {
        // 26 (fist_pos_sit_lean_idle) has no elf (hariharan) assets -> must serialize 141 (chair_rest)
        var npc = MakeNpc(Race.Elf, Gender.Male, 26);
        var packet = new SCUnitModelPostureChangedPacket(npc, 26, true);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(npc.ObjId);
        var (postureType, isLooted, animActionId, activate) = ReadPosture(stream);
        await Assert.That(postureType).IsEqualTo((byte)ModelPostureType.ActorModelState);
        await Assert.That(isLooted).IsFalse();
        await Assert.That(animActionId).IsEqualTo(141u);
        await Assert.That(activate).IsTrue();
    }

    [Test]
    public async Task Write_NuianFemale_ChairSnooze_SerializesRemappedChairRest()
    {
        // 160 (fist_pos_sit_chair_snooze_idle) has no assets at all -> 141 (chair_rest)
        var npc = MakeNpc(Race.Nuian, Gender.Female, 160);
        var packet = new SCUnitModelPostureChangedPacket(npc, 160, true);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(npc.ObjId);
        var (postureType, _, animActionId, _) = ReadPosture(stream);
        await Assert.That(postureType).IsEqualTo((byte)ModelPostureType.ActorModelState);
        await Assert.That(animActionId).IsEqualTo(141u);
    }

    [Test]
    public async Task Write_ElfMale_CrouchInvestigation_SerializesRemappedFurniturerepair()
    {
        // 70 (fist_pos_sit_crouch_investigation_idle) has no assets -> 224 (crouch_furniturerepair, all males)
        var npc = MakeNpc(Race.Elf, Gender.Male, 70);
        var packet = new SCUnitModelPostureChangedPacket(npc, 70, true);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(npc.ObjId);
        var (postureType, _, animActionId, _) = ReadPosture(stream);
        await Assert.That(postureType).IsEqualTo((byte)ModelPostureType.ActorModelState);
        await Assert.That(animActionId).IsEqualTo(224u);
    }

    [Test]
    public async Task Write_NuianMale_StandPose_SerializesOriginalId()
    {
        // 100 (fist_pos_stn_armor_dealer_idle) is a stand pose -> no remap, id must pass through
        var npc = MakeNpc(Race.Nuian, Gender.Male, 100);
        var packet = new SCUnitModelPostureChangedPacket(npc, 100, true);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(npc.ObjId);
        var (postureType, _, animActionId, _) = ReadPosture(stream);
        await Assert.That(postureType).IsEqualTo((byte)ModelPostureType.ActorModelState);
        await Assert.That(animActionId).IsEqualTo(100u);
    }

    [Test]
    public async Task Write_NuianMale_PlayableChair_SerializesOriginalId()
    {
        // 87 (fist_pos_sit_chair_nursery_dealer_idle) has nuian male assets -> unchanged
        var npc = MakeNpc(Race.Nuian, Gender.Male, 87);
        var packet = new SCUnitModelPostureChangedPacket(npc, 87, true);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(npc.ObjId);
        var (postureType, _, animActionId, _) = ReadPosture(stream);
        await Assert.That(postureType).IsEqualTo((byte)ModelPostureType.ActorModelState);
        await Assert.That(animActionId).IsEqualTo(87u);
    }
}
