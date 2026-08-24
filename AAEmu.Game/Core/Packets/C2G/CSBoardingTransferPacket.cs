using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSBoardingTransferPacket() : GamePacket(CSOffsets.CSBoardingTransferPacket, 1)
{
    private ushort _tl;
    private byte _ap;

    public override void Read(PacketStream stream)
    {
        _tl = stream.ReadUInt16();
        _ap = stream.ReadByte();
    }

    public override void Execute()
    {
        var character = Connection.ActiveChar;
        if (character?.ParentWorld == null)
            return;

        Logger.Debug("BoardingTransfer, Tl: {0}, Ap: {1}", _tl, _ap);

        // Fail closed when already seated — no mutation.
        if (character.Bonding != null)
            return;

        // Resolve the transfer by TlId (both parts of a multi-part carriage
        // share the master's TlId).
        var transfer = character.ParentWorld.TransferManager.GetTransfers()
            .FirstOrDefault(t => t.TlId == _tl);
        if (transfer == null)
            return;

        // The seat doodad whose DoodadFuncAttachment func row targets the
        // requested attach point — the same seat-interaction engine path a
        // doodad interaction takes (Doodad.Use → DoodadFuncAttachment.Use →
        // Seat.LoadPassenger + BondDoodad + transform parenting +
        // SCBondDoodadPacket). A full seat makes LoadPassenger return -1 and
        // Use is a silent no-op there; we verify below and refuse.
        var seat = transfer.AttachedDoodads.FirstOrDefault(d =>
            DoodadManager.Instance.GetFuncsForGroup(d.FuncGroupId).Any(f =>
                f.FuncType == "DoodadFuncAttachment"
                && DoodadManager.Instance.GetFuncTemplate(f.FuncId, f.FuncType) is DoodadFuncAttachment
                {
                    AttachPointId: var point
                } && point == (AttachPointKind)_ap));
        if (seat == null)
            return;

        var attachmentFunc = DoodadManager.Instance.GetFuncsForGroup(seat.FuncGroupId)
            .First(f => f.FuncType == "DoodadFuncAttachment");
        seat.Use(character, attachmentFunc.SkillId);

        if (character.Bonding?.ObjId != seat.ObjId)
            Logger.Warn("BoardingTransfer refused for {0} on transfer {1} seat {2}", character.Name, transfer.ObjId, seat.ObjId);
    }
}
