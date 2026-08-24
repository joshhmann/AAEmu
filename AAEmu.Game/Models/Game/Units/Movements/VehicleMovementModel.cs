using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Models.Game.Units.Movements;

/// <summary>
/// The client-authored vehicle movement model — the single engine path a
/// client driver's CSMoveUnitPacket executes for ground vehicles (Slaves)
/// and mounts (Mates): position apply + SCOneUnitMovementPacket broadcast +
/// transform finalize.
///
/// The M5 gameplay actor contract (DriveVehicle) drives vehicles through the
/// SAME methods a real client packet triggers — the actor is a
/// player-equivalent driver, not a transform-writer. No actor code ever
/// assigns a vehicle Transform directly; every leg flows through this model
/// so observers see real movement broadcasts.
///
/// CSMoveUnitPacket delegates its VehicleMoveType and UnitMoveType cases
/// here; the methods below ARE the handler code (behavior-preserving
/// extraction — a client's packet and the actor contract share one path).
/// </summary>
public static class VehicleMovementModel
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Applies a client-authored VehicleMoveType for a ground vehicle (Slave).
    /// The exact engine path CSMoveUnitPacket's VehicleMoveType case executes
    /// for a client driver: attaches the driver, applies position + rotation,
    /// broadcasts SCOneUnitMovementPacket and finalizes the transform tree so
    /// passengers/packs follow the vehicle.
    /// </summary>
    /// <param name="driver">The character in the driver seat (the packet's sender).</param>
    /// <param name="car">The slave vehicle being driven.</param>
    /// <param name="vmt">The client-authored move payload (position, rotation, velocity).</param>
    public static void ApplySlaveMove(Character driver, Slave car, VehicleMoveType vmt)
    {
        var (rotDegX, rotDegY, rotDegZ) = MathUtil.GetSlaveRotationInDegrees(vmt.RotationX, vmt.RotationY, vmt.RotationZ);

        // Make sure driver is attached to car
        driver.Transform.Parent = car.Transform;
        car.Transform.Local.SetPosition(vmt.X, vmt.Y, vmt.Z, rotDegX, rotDegY, rotDegZ);
        car.BroadcastPacket(new SCOneUnitMovementPacket(car.ObjId, vmt), true);
        car.Transform.FinalizeTransform(); // Propagate position updates to all children
    }

    /// <summary>
    /// Applies a client-authored UnitMoveType for a unit (mounts/Mates and
    /// characters). The exact engine path CSMoveUnitPacket's UnitMoveType
    /// case executes for a client: pet XP/effect handling, sticky-parent
    /// tracking, position apply, SCOneUnitMovementPacket broadcast, transform
    /// finalize and fall-velocity damage.
    /// </summary>
    /// <param name="driver">The packet's sending character (Connection.ActiveChar).</param>
    /// <param name="targetUnit">The unit being moved (a Mate when riding a mount).</param>
    /// <param name="dmt">The client-authored move payload.</param>
    public static void ApplyUnitMove(Character driver, BaseUnit targetUnit, UnitMoveType dmt, bool broadcast = true)
    {
        // Its moving Pets, handle Pet XP for moving
        if (targetUnit is Mate mate)
        {
            // Pet moved
            RemoveEffects(targetUnit, dmt);

            // TODO: Check if we're the owner, or allowed to otherwise control this pet
            if (dmt.VelX != 0 || dmt.VelY != 0)
                mate.StartUpdateXp(driver);
            else
                mate.StopUpdateXp();

            foreach (var (_, passengerInfo) in mate.Passengers)
            {
                var passenger = WorldManager.Instance.GetCharacterByObjId(passengerInfo._objId);
                if (passenger != null)
                {
                    // passenger.Transform = mate.Transform.CloneDetached(passenger);
                    RemoveEffects(passenger, dmt);
                }
            }
        }

        // If controlling character, but it's riding something, sync parent with the mount
        if (targetUnit is Character player)
        {
            // TODO : check target has Telekinesis buff if target is a player
            // Just forward it to the packet, not safe for exploits/hacking
            // We moved
            RemoveEffects(player, dmt);

            if (player.IsRiding)
            {
                // Если мы сидим на питомце и Parent = null, насильно спешиваем персонажа для предотвращения сбоя клиента
                // If we are sitting on a pet and Parent = null, we force it on there to prevent client crashing
                if (player.Transform.Parent == null)
                {
                    var mate2 = driver.ParentWorld.MateManager.GetActiveMates(player.Id).FirstOrDefault();
                    if (mate2 != null)
                    {
                        player.Transform.Parent = mate2.Transform;
                    }
                }
                // We're riding a pet, we don't care about the rest of this function
                // If we're riding the pet, we should only care about the pet's movement
                Logger.Debug($"{targetUnit.Name} IsRiding, ignoring movement request");
                return;
            }

            // Player moved
            player.SetPlayerMoved();
        }

        var isStandingOnObject = dmt.Flags.HasFlag(MoveTypeFlags.StandingOnObject);
        // Don't know why, but we need to Ignore GcId 1, it probably has some special meaning like "current parent"
        var parentObject = isStandingOnObject && dmt.GcId > 1
            ? driver.ParentWorld.GetBaseUnit(dmt.GcId)
            : null;
        var isSticky = ((MoveTypeActorFlags)dmt.ActorFlags).HasFlag(MoveTypeActorFlags.HangingFromObject);

        if (targetUnit.Transform.Parent != null && parentObject == null)
        {
            // No longer standing on object?
            var oldParentObj = targetUnit.Transform.Parent.GameObject?.ObjId ?? 0;
            targetUnit.Transform.Parent = null;

            driver.SendDebugMessage(
                $"|cFF884444{targetUnit.Name} ({targetUnit.ObjId}) no longer standing on Object {oldParentObj} " +
                $"@ x{dmt.X:F1} y{dmt.Y:F1} z{dmt.Z:F1} || World: {targetUnit.Transform.World}|r");
        }
        else if (targetUnit.Transform.Parent == null && parentObject != null)
        {
            // Standing on a new object ?
            targetUnit.Transform.Parent = parentObject.Transform;

            driver.SendDebugMessage(
                $"|cFF448844{targetUnit.Name} ({targetUnit.ObjId}) standing on Object {parentObject.Name} ({parentObject.ObjId}) " +
                $"@ x{dmt.X:F1} y{dmt.Y:F1} z{dmt.Z:F1} || World: {targetUnit.Transform.World}|r");
        }
        else if (targetUnit.Transform.Parent is { GameObject: not null } &&
                 parentObject != null &&
                 targetUnit.Transform.Parent.GameObject.ObjId != parentObject.ObjId)
        {
            // Changed to standing on different object ?
            targetUnit.Transform.Parent = parentObject.Transform;

            driver.SendDebugMessage(
                $"|cFF448888{targetUnit.Name} ({targetUnit.ObjId}) moved to standing on new Object {parentObject.Name} ({parentObject.ObjId}) " +
                $"@ x{dmt.X:F1} y{dmt.Y:F1} z{dmt.Z:F1} || World: {targetUnit.Transform.World}|r");
        }

        // If ActorFlag 0x40 is no longer set, it means we're no longer climbing/holding onto something
        if (targetUnit.Transform.StickyParent != null && !isSticky && !IsBoardedOnTransfer(targetUnit))
            targetUnit.Transform.StickyParent = null;

        // Actually update the position
        targetUnit.Transform.Local.SetPosition(dmt.X, dmt.Y, dmt.Z,
            (float)MathUtil.ConvertDirectionToRadian(dmt.RotationX),
            (float)MathUtil.ConvertDirectionToRadian(dmt.RotationY),
            (float)MathUtil.ConvertDirectionToRadian(dmt.RotationZ));
        if (broadcast)
            targetUnit.BroadcastPacket(new SCOneUnitMovementPacket(targetUnit.ObjId, dmt), true);
        targetUnit.Transform.FinalizeTransform();

        // Handle Fall Velocity
        if (dmt.FallVel > 0 && targetUnit is Unit unit)
        {
            _ = unit.DoFallDamage(dmt.FallVel);
        }
    }

    /// <summary>
    /// Builds the VehicleMoveType a client driver would send for a ground
    /// vehicle at <paramref name="position"/>, facing <paramref name="yawRadians"/>
    /// (radians) and moving at <paramref name="speed"/> m/s. Velocity =
    /// m/s × 2048, same as PhysicsManager's broadcast shape.
    ///
    /// Rotation: the packet carries the quaternion shorts (X, Y, Z); a
    /// heading-only quaternion is (0, 0, sin(yaw/2), cos(yaw/2)), so the
    /// yaw short lives in RotationZ. The handler decodes those shorts back
    /// to (roll=0, pitch=0, yaw) radians — the same Z-axis heading the
    /// walking model applies to characters (Rotation.Z), so the server
    /// transform faces the travel direction.
    /// </summary>
    public static VehicleMoveType BuildVehicleMove(Vector3 position, float yawRadians, float speed)
    {
        var moveType = (VehicleMoveType)MoveType.GetType(MoveTypeEnum.Vehicle);
        var (velX, velY) = MathUtil.AddDistanceToFront(speed * 2048f, 0, 0, yawRadians);

        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = (short)velX;
        moveType.VelY = (short)velY;
        moveType.VelZ = 0;
        moveType.RotationX = 0;
        moveType.RotationY = 0;
        moveType.RotationZ = (short)(MathF.Sin(yawRadians * 0.5f) / 0.00003052f);
        moveType.AngVelX = 0f;
        moveType.AngVelY = 0f;
        moveType.AngVelZ = 0f;
        moveType.Steering = 0f;
        moveType.WheelAngVel = []; // cart/wagon has "no wheels"
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }

    /// <summary>
    /// Builds the UnitMoveType a client rider would send for a mount at
    /// <paramref name="position"/>, moving at <paramref name="speed"/> m/s in
    /// the given direction (radians). Same shape BotRoamStepExecutor uses for
    /// player-bot movement broadcasts (walk flags/stance/alertness).
    /// </summary>
    public static UnitMoveType BuildUnitMove(Vector3 position, float yawRadians, float speed)
    {
        var moveType = (UnitMoveType)MoveType.GetType(MoveTypeEnum.Unit);
        var (velX, velY) = MathUtil.AddDistanceToFront(speed * 2048f, 0, 0, yawRadians);

        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = (short)velX;
        moveType.VelY = (short)velY;
        moveType.RotationX = 0;
        moveType.RotationY = 0;
        moveType.RotationZ = 0;
        moveType.ActorFlags = 5; // 5-walk
        moveType.Flags = 0;
        moveType.DeltaMovement = [0, 63, 0];
        moveType.Stance = GameStanceType.Relaxed;   // IDLE = 0x1
        moveType.Alertness = MoveTypeAlertness.Idle; // IDLE = 0x0
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }

    /// <summary>
    /// Builds the UnitMoveType a client would send for its OWN character
    /// walking (CSMoveUnitPacket, UnitMoveType case). Same walk shape as
    /// <see cref="BuildUnitMove"/> (velocity, walk flags/stance/alertness)
    /// plus the facing rotation bytes — the Simulation.cs:397-409 pattern:
    /// the rotation short encodes the travel heading so the character's
    /// transform (and observers) faces the movement direction instead of
    /// snapping to 0 on every leg.
    /// </summary>
    /// <summary>Shared walking delta payload (0,63,0) — read-only at
    /// serialization, safe to share across moveType instances.</summary>
    private static readonly sbyte[] WalkDelta = [0, 63, 0];

    public static UnitMoveType BuildCharacterMove(Vector3 position, float yawRadians, float speed)
    {
        var moveType = (UnitMoveType)MoveType.GetType(MoveTypeEnum.Unit);
        var (velX, velY) = MathUtil.AddDistanceToFront(speed * 2048f, 0, 0, yawRadians);

        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = (short)velX;
        moveType.VelY = (short)velY;
        moveType.RotationX = 0;
        moveType.RotationY = 0;
        moveType.RotationZ = MathUtil.ConvertDegreeToSByteDirection(yawRadians.RadToDeg() - 90);
        moveType.ActorFlags = 5; // 5-walk
        moveType.Flags = 0;
        moveType.DeltaMovement = WalkDelta;
        moveType.Stance = GameStanceType.Relaxed;   // IDLE = 0x1
        moveType.Alertness = MoveTypeAlertness.Idle; // IDLE = 0x0
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }

    /// <summary>
    /// Builds the canonical locomotion-reset payload a client sends when it
    /// releases movement keys: zero velocity + <see cref="MoveTypeFlags.Stopping"/>
    /// at the given position, facing preserved. Same shape Blink.cs:65-79 and
    /// TeleportToUnit.cs:74-83 use for teleport-style resets (dossier §1.6) —
    /// the broadcast that snaps observers' clients to a standstill.
    /// </summary>
    /// <param name="position">The halt position (the character's final world position).</param>
    /// <param name="rotationZ">Current facing byte (Transform.Local.ToRollPitchYawSBytesMovement().Item3).</param>
    public static UnitMoveType BuildStopMove(Vector3 position, sbyte rotationZ)
    {
        var moveType = (UnitMoveType)MoveType.GetType(MoveTypeEnum.Unit);

        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = 0;
        moveType.VelY = 0;
        moveType.VelZ = 0;
        moveType.RotationX = 0;
        moveType.RotationY = 0;
        moveType.RotationZ = rotationZ;
        moveType.Flags = MoveTypeFlags.Stopping; // 0x04 — released movement keys
        moveType.DeltaMovement = [0, 0, 0];      // empty — no gait delta
        moveType.Stance = GameStanceType.Relaxed;   // IDLE = 0x1
        moveType.Alertness = MoveTypeAlertness.Idle; // IDLE = 0x0
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }

    private static void RemoveEffects(BaseUnit unit, MoveType moveType)
    {
        if (moveType.VelX != 0 || moveType.VelY != 0 || moveType.VelZ != 0)
            unit.Buffs.TriggerRemoveOn(BuffRemoveOn.Move);
    }

    private static bool IsBoardedOnTransfer(BaseUnit unit)
    {
        return unit is Character character &&
               unit.Transform.StickyParent?.GameObject is Transfer transfer &&
               transfer.AttachedCharacters.Contains(character);
    }
}
