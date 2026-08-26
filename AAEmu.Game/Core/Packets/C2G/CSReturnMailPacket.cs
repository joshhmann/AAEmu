using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

// Opcode 0x0a2 is STRONGLY_INFERRED, pending live-client capture. Evidence chain:
// client UI X2Mail:ReturnMailById verbatim in decompiled mailbox scripts
// (1.2 game_pak x2ui/mailbox/mail/read_mail.lua:991-1009); slot arithmetic over the
// contiguous C2S mail block 0x098..0x0a3 leaves 0x0a2 as the only free slot, with
// Delete=0x0a1 and ReportSpam=0x0a3 occupied. See CSOffsets.CSReturnMailPacket.
public class CSReturnMailPacket() : GamePacket(CSOffsets.CSReturnMailPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var mailId = stream.ReadInt64();

        Logger.Debug("ReturnMail, mailId: {0}", mailId);
        Connection.ActiveChar.Mails.ReturnMail(mailId);
    }
}
