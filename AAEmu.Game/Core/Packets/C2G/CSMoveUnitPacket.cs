using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSMoveUnitPacket() : GamePacket(CSOffsets.CSMoveUnitPacket, 1)
{
    public override PacketLogLevel LogLevel => PacketLogLevel.Off;

    private uint _objId;
    private MoveType _moveType;

    public override void Read(PacketStream stream)
    {
        _objId = stream.ReadBc();

        var type = (MoveTypeEnum)stream.ReadByte();
        _moveType = MoveType.GetType(type);
        stream.Read(_moveType);
    }

    public override void Execute()
    {
        // _moveType.Flags
        // 0x02 : Moving
        // 0x04 : Stopping (released movement keys)
        // 0x06 : Jumping
        // 0x40 : Standing on something
        /*
        Logger.Debug("CSMoveUnitPacket(" + _moveType.Type + ") \nScType: " + _moveType.ScType + " - Flags: " +
                   _moveType.Flags.ToString("X") + " - " +
                   "Phase: " + _moveType.Phase + " - Time: " + _moveType.Time + " - " +
                   "Sender: " + Connection.ActiveChar.Name + " (" + Connection.ActiveChar.ObjId + ") - " +
                   "Obj: " + (WorldManager.Instance.GetBaseUnit(_objId)?.Name ?? "<null>") + " (" + _objId +
                   ") \n" +
                   "XYZ: " + _moveType.X.ToString("F1") + " , " + _moveType.Y.ToString("F1") + " , " +
                   _moveType.Z.ToString("F1") + " - " +
                   "Rot: " + _moveType.RotationX.ToString() + " , " + _moveType.RotationY.ToString() + " , " +
                   _moveType.RotationZ.ToString() + " - " +
                   "VelXYZ: " + _moveType.VelX.ToString("F1") + " , " + _moveType.VelY.ToString("F1") + " , " +
                   _moveType.VelZ.ToString("F1")
        );
        */

        var character = Connection.ActiveChar;

        if (character == null) return;
        character.LastPacketActivityTime = DateTime.UtcNow;

        // if movement is forbidden when teleporting to instances, then to exit
        if (character.DisabledSetPosition) return;

        var targetUnit = character.ParentWorld.GetBaseUnit(_objId);

        // Invalid Object ?
        if (targetUnit == null)
        {
            // TODO по какой то причине объект удалили из региона, наверное нужно его как то вернуть назад 
            // TODO for some reason the object has been removed from the region, you probably need to get it back somehow
            Logger.Warn($"Invalid target {_objId} from {character.Name}");
            return;
        }

        // We are not controlling our main character
        switch (_moveType)
        {
            case ShipRequestMoveType srmt:
                {
                    // TODO: Validate if we are in the driver seat
                    // We are controlling a ship
                    // Logger.Debug("ShipRequestMoveType - Throttle: {0} - Steering {1}", srmt.Throttle, srmt.Steering);
                    if (targetUnit is not Slave ship)
                        return;

                    // TODO: Validate if targetUnit is actually a ship

                    ship.ThrottleRequest = srmt.Throttle;
                    ship.SteeringRequest = srmt.Steering;

                    // Make sure driver is attached to the ship
                    character.Transform.Parent = ship.Transform;
                    // Actual movement and sending of packets is handle by the Physics Engine
                    break;
                }
            case VehicleMoveType vmt:
                {
                    if (targetUnit is not Slave car)
                        return;

                    // Client-authored vehicle movement — the shared engine
                    // path (driver attach + position apply + broadcast +
                    // finalize). The M5 actor contract (DriveVehicle) drives
                    // through the SAME model, so no code path fakes a
                    // vehicle Transform.
                    VehicleMovementModel.ApplySlaveMove(character, car, vmt);
                    break;
                }
            case UnitMoveType dmt:
                {
                    // Client-authored unit movement — the shared engine path
                    // (mate/pet handling + sticky-parent tracking + position
                    // apply + broadcast + finalize + fall damage).
                    VehicleMovementModel.ApplyUnitMove(character, targetUnit, dmt);
                    break;
                }
            default:
                Logger.Warn($"Unknown MoveType: {_moveType} by {character.Name} for {targetUnit.Name}");
                break;
        }
    }

    public override string Verbose()
    {
        return " - " + (_moveType?.Type.ToString() ?? "none") + " " + (Connection.ActiveChar.ParentWorld.GetGameObject(_objId)?.DebugName() ?? "(" + _objId + ")");
    }
}
