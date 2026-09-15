using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

/// <summary>
/// NPC-INTERACTION-01: data-driven per-NPC interaction skills (npc_interaction_sets /
/// npc_interactions) are appended AFTER the hardcoded CSStartInteractionPacket option.
/// The client executes the first entry regardless, so NPCs without a set stay byte-identical.
/// </summary>
[NotInParallel] // seeds the process-wide NpcManager singleton
public class CSStartInteractionPacketTests
{
    // High set ids: never collide with compact.sqlite3 sets or other rigs' seeds.
    private const int TestSetId = 900_101;
    private const uint DefaultOption = 0; // quest-NPC default from CSStartInteractionPacket
    private const uint FirstSetSkill = 21366;
    private const uint SecondSetSkill = 21521;

    private static NpcManager EnsureNpcManager()
    {
        var field = typeof(Singleton<NpcManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Cannot locate Singleton<NpcManager>.s_instance");
        if (field.GetValue(null) is not NpcManager manager)
        {
            manager = new NpcManager(
                Mock.Of<IObjectIdManager>().Object,
                Mock.Of<IModelManager>().Object,
                Mock.Of<IFactionManager>().Object,
                Mock.Of<IItemManager>().Object,
                Mock.Of<IAIManager>().Object);
            field.SetValue(null, manager);
        }
        return manager;
    }

    private static void SeedInteractionSet(int setId, params uint[] skillIds)
    {
        var manager = EnsureNpcManager();
        var dictField = typeof(NpcManager).GetField("<NpcInteractionSkills>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Cannot locate NpcManager.NpcInteractionSkills backing field");
        var dict = (Dictionary<int, List<uint>>)dictField.GetValue(manager)!;
        dict[setId] = [.. skillIds];
    }

    /// <summary>
    /// Mirrors SCNpcInteractionSkillListPacket.Write field order to decode the exact
    /// skill list the client would receive.
    /// </summary>
    private static uint[] DecodeSkillList(byte[] bytes)
    {
        var stream = new PacketStream(bytes);
        stream.ReadBc(); // npcObjId
        stream.ReadBc(); // objId
        stream.ReadInt32(); // extraInfo
        stream.ReadInt32(); // pickId
        stream.ReadByte(); // mouseButton
        var count = stream.ReadInt32();
        var skills = new uint[count];
        for (var i = 0; i < count; i++)
            skills[i] = stream.ReadUInt32();
        return skills;
    }

    [Test]
    public async Task Npc_WithInteractionSet_YieldsHardcodedFirst_PlusDataSkills_OnTheWire()
    {
        SeedInteractionSet(TestSetId, FirstSetSkill, SecondSetSkill);
        var template = new NpcTemplate { Id = 90_001, NpcInteractionSetId = TestSetId };

        var skillList = NpcManager.Instance.BuildInteractionSkillList(DefaultOption, template.NpcInteractionSetId);
        var packet = new SCNpcInteractionSkillListPacket(0x6001, 0, 0, 0, 0, 0, skillList);
        var decoded = DecodeSkillList(packet.Write(new PacketStream()).GetBytes());

        await Assert.That(decoded.Length).IsEqualTo(3);
        await Assert.That(decoded[0]).IsEqualTo(DefaultOption);
        await Assert.That(decoded[1]).IsEqualTo(FirstSetSkill);
        await Assert.That(decoded[2]).IsEqualTo(SecondSetSkill);
    }

    [Test]
    public async Task Npc_WithoutInteractionSet_YieldsExactlyTheOldSingleOption()
    {
        EnsureNpcManager();
        var template = new NpcTemplate { Id = 90_002, NpcInteractionSetId = 0 };

        var skillList = NpcManager.Instance.BuildInteractionSkillList(DefaultOption, template.NpcInteractionSetId);
        var packet = new SCNpcInteractionSkillListPacket(0x6001, 0, 0, 0, 0, 0, skillList);
        var decoded = DecodeSkillList(packet.Write(new PacketStream()).GetBytes());

        await Assert.That(decoded.Length).IsEqualTo(1);
        await Assert.That(decoded[0]).IsEqualTo(DefaultOption);
    }

    [Test]
    public async Task UnknownInteractionSetId_IsIgnoredSafely()
    {
        EnsureNpcManager();
        const int unknownSetId = 900_102; // never seeded

        await Assert.That(NpcManager.Instance.GetNpcInteractionSkills(unknownSetId)).IsEmpty();
        var skillList = NpcManager.Instance.BuildInteractionSkillList(DefaultOption, unknownSetId);

        await Assert.That(skillList.Length).IsEqualTo(1);
        await Assert.That(skillList[0]).IsEqualTo(DefaultOption);
    }
}
