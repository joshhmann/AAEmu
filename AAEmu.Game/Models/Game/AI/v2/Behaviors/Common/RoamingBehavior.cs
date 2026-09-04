using System.Numerics;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.AI.Utils;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class RoamingBehavior : BaseCombatBehavior
{
    private Vector3 _targetRoamPosition = Vector3.Zero;
    private DateTime _nextRoaming;
    private bool _enter;
    private Vector3 _lastPosition;
    private DateTime _lastProgressTime;
    private DateTime _roamStartTime;

    public override void Enter()
    {
        Ai.Owner.InterruptSkills();
        Ai.Owner.CurrentGameStance = GameStanceType.Relaxed;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Idle;
        _enter = true;
    }

    public override void Tick(TimeSpan delta)
    {
        if (!_enter)
            return; // not initialized yet Enter()

        if (CheckAggression())
            return;

        CheckAlert();

        if (_targetRoamPosition.Equals(Vector3.Zero) && DateTime.UtcNow > _nextRoaming)
        {
            UpdateRoaming();
            if (!_targetRoamPosition.Equals(Vector3.Zero))
            {
                var curPos = Ai.Owner.Transform.World.Position;
                if (MathUtil.CalculateDistance(curPos, _targetRoamPosition, true) < 1.0f)
                {
                    _targetRoamPosition = Vector3.Zero;
                    _nextRoaming = DateTime.UtcNow.AddSeconds(Random.Shared.Next(3, 7));
                    return;
                }

                _roamStartTime = DateTime.UtcNow;
                _lastPosition = curPos;
                _lastProgressTime = DateTime.UtcNow;
                Ai.Owner.BroadcastPacket(new SCUnitModelPostureChangedPacket(Ai.Owner, Ai.Owner.AnimActionId, false), false);
            }
        }

        if (_targetRoamPosition.Equals(Vector3.Zero))
            return;

        var moveSpeed = Ai.GetRealMovementSpeed(Ai.Owner.BaseMoveSpeed);
        var moveFlags = Ai.GetRealMovementFlags(moveSpeed);
        moveSpeed *= delta.Milliseconds / 1000.0;
        var reached = Ai.Owner.MoveTowards(_targetRoamPosition, (float)moveSpeed, moveFlags);

        var currentPos = Ai.Owner.Transform.World.Position;
        var dist = MathUtil.CalculateDistance(currentPos, _targetRoamPosition, true);

        if (MathUtil.CalculateDistance(currentPos, _lastPosition, true) > 0.05f)
        {
            _lastPosition = currentPos;
            _lastProgressTime = DateTime.UtcNow;
        }

        var isStuck = (DateTime.UtcNow - _lastProgressTime).TotalSeconds >= 1.5;
        var isTimedOut = (DateTime.UtcNow - _roamStartTime).TotalSeconds >= 6.0;

        if (reached || dist < 1.0f || isStuck || isTimedOut)
        {
            Ai.Owner.StopMovement();
            _targetRoamPosition = Vector3.Zero;
            _nextRoaming = DateTime.UtcNow.AddSeconds(Random.Shared.Next(3, 7));
            Ai.Owner.BroadcastPacket(new SCUnitModelPostureChangedPacket(Ai.Owner, Ai.Owner.AnimActionId, true), false);
        }
    }

    public override void Exit()
    {
        Ai.Owner.StopMovement();
        _targetRoamPosition = Vector3.Zero;
        _enter = false;
    }

    private void UpdateRoaming()
    {
        // TODO : Group member handling
        _targetRoamPosition = AiUtils.CalcNextRoamingPosition(Ai);
    }
}
