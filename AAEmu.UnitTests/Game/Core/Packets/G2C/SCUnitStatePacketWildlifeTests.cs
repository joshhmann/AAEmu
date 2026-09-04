using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCUnitStatePacketWildlifeTests
{
    [Test]
    public async Task SCUnitStatePacket_WildlifeNpc_PreservesModelParamsNone_AndAvoidsSpherePlaceholder()
    {
        var template = new NpcTemplate
        {
            Id = 3492,
            ModelId = 316,
            FactionId = FactionsEnum.Monstrosity,
            CharRaceId = 0,
            Scale = 1.0f,
            Level = 1,
            ModelParams = new UnitCustomModelParams(UnitCustomModelType.None)
        };

        var npc = new Npc
        {
            ObjId = 55555,
            TemplateId = 3492,
            Id = 3492,
            Template = template,
            ModelId = 316,
            ModelParams = template.ModelParams
        };

        var packet = new SCUnitStatePacket(npc);
        var stream = packet.Write(new PacketStream());
        var bytes = stream.GetBytes();

        // Must not mutate ModelParams to Skin (the defect that turned wildlife into giant spheres)
        await Assert.That(npc.ModelParams.Type).IsEqualTo(UnitCustomModelType.None);
        await Assert.That(template.ModelParams.Type).IsEqualTo(UnitCustomModelType.None);
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }
}
