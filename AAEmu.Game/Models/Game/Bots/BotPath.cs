using System.Numerics;

using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Waypoint route for headless bots (M6-light roam layer, additive).
///
/// Pure movement math over the EXISTING world facilities — MathUtil
/// (CalculateDistance / AddDistanceToFront / CalculateAngleFrom) and the
/// Transform model the NPC route system (Units/Route/Simulation) already
/// uses. No parallel movement system, no packets, no pathfinding stack:
/// a route is an ordered list of waypoints walked with bounded per-tick
/// steps, with the same checkpoint/arrival model Simulation uses
/// (RangeToCheckPoint 0.5f, nearest-checkpoint indexing).
///
/// The route never writes the character itself — it returns the next
/// position; the caller (PlayerBotBehaviorController) applies it through
/// the ordinary Transform, exactly like Simulation.MoveTo does for NPCs.
/// </summary>
public class BotPath
{
    public enum LoopMode
    {
        /// <summary>Walk the route once, then finish.</summary>
        Once,

        /// <summary>Walk the route, then wrap to the start (patrol loop).</summary>
        Loop,

        /// <summary>Walk to the end, then back to the start, forever.</summary>
        PingPong
    }

    /// <summary>Default arrival radius (same checkpoint model as Simulation.RangeToCheckPoint 0.5f).</summary>
    public const float ArrivalRadiusDefault = 0.5f;

    private readonly List<Vector3> _waypoints;
    private bool _forward = true;

    public BotPath(IEnumerable<Vector3> waypoints, LoopMode mode = LoopMode.Once,
        float arrivalRadius = ArrivalRadiusDefault, float maxStepPerTick = 5f)
    {
        _waypoints = waypoints.ToList();
        Mode = mode;
        ArrivalRadius = arrivalRadius;
        MaxStepPerTick = maxStepPerTick;

        if (_waypoints.Count == 0)
            throw new ArgumentException("A bot route needs at least one waypoint.", nameof(waypoints));
        if (arrivalRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(arrivalRadius), "Arrival radius must be positive.");
        if (maxStepPerTick <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxStepPerTick), "Max step per tick must be positive.");
    }

    /// <summary>All waypoints on the route (read-only view).</summary>
    public IReadOnlyList<Vector3> Waypoints => _waypoints;

    public LoopMode Mode { get; }

    /// <summary>Distance under which a waypoint counts as reached (same model as Simulation.RangeToCheckPoint).</summary>
    public float ArrivalRadius { get; }

    /// <summary>Hard cap on how far a single Move call may travel (bounded movement).</summary>
    public float MaxStepPerTick { get; }

    /// <summary>Index of the waypoint currently being walked toward.</summary>
    public int CurrentIndex { get; private set; }

    /// <summary>True when a Once route has reached its final waypoint.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>The waypoint the bot is currently walking toward.</summary>
    public Vector3 CurrentTarget => _waypoints[CurrentIndex];

    /// <summary>
    /// Advances one tick from <paramref name="position"/> along the route
    /// and returns the new position. Movement is bounded by MaxStepPerTick;
    /// Z is interpolated proportionally (flat X/Y via the engine's
    /// AddDistanceToFront — the same facility Simulation.MoveTo uses).
    /// </summary>
    public Vector3 Move(Vector3 position)
    {
        if (IsFinished)
            return position;

        var target = CurrentTarget;

        // 3D distance to the current waypoint (arrival model mirrors Simulation).
        var flatDistance = MathUtil.CalculateDistance(position, target, false);
        var zDistance = Math.Abs(target.Z - position.Z);

        if (flatDistance <= ArrivalRadius && zDistance <= ArrivalRadius)
            return Arrive();

        var step = Math.Min(MaxStepPerTick, flatDistance);
        Vector3 next;

        if (flatDistance > 0.0001f)
        {
            // Horizontal movement through the engine's existing facility.
            var angle = (float)MathUtil.CalculateAngleFrom(position, target).DegToRad();
            var (newX, newY) = MathUtil.AddDistanceToFront(step, position.X, position.Y, angle);
            var fraction = step / flatDistance;
            var newZ = position.Z + (target.Z - position.Z) * fraction;
            next = new Vector3(newX, newY, newZ);
        }
        else
        {
            // Pure vertical movement (waypoint directly above/below).
            var zStep = Math.Min(MaxStepPerTick, zDistance);
            var dir = target.Z >= position.Z ? 1f : -1f;
            next = new Vector3(position.X, position.Y, position.Z + dir * zStep);
        }

        // Arrival on landing: a step that lands within the arrival radius of
        // the waypoint completes the leg in the SAME call (an exact step onto
        // a waypoint must not need a second tick to "arrive").
        if (MathUtil.CalculateDistance(next, target, false) <= ArrivalRadius &&
            Math.Abs(target.Z - next.Z) <= ArrivalRadius)
            return Arrive();

        return next;
    }

    private Vector3 Arrive()
    {
        switch (Mode)
        {
            case LoopMode.Once:
                if (CurrentIndex >= _waypoints.Count - 1)
                {
                    IsFinished = true;
                    return _waypoints[^1];
                }

                CurrentIndex++;
                return _waypoints[CurrentIndex - 1];

            case LoopMode.Loop:
                CurrentIndex = (CurrentIndex + 1) % _waypoints.Count;
                return _waypoints[(CurrentIndex - 1 + _waypoints.Count) % _waypoints.Count];

            case LoopMode.PingPong:
                if (_forward && CurrentIndex >= _waypoints.Count - 1)
                {
                    _forward = false;
                    CurrentIndex--;
                }
                else if (!_forward && CurrentIndex <= 0)
                {
                    _forward = true;
                    CurrentIndex++;
                }
                else
                {
                    CurrentIndex += _forward ? 1 : -1;
                }

                return _waypoints[(CurrentIndex + (_forward ? -1 : 1) + _waypoints.Count) % _waypoints.Count];

            default:
                throw new ArgumentOutOfRangeException(nameof(Mode));
        }
    }

    /// <summary>
    /// Bounded-roam guard: true only when every waypoint lies within the
    /// flat radius of <paramref name="center"/> (the bot's safe zone).
    /// The safety monitor rejects any route that fails this check.
    /// </summary>
    public bool AllWaypointsWithin(Vector3 center, float radius)
    {
        foreach (var waypoint in _waypoints)
        {
            if (MathUtil.CalculateDistance(center, waypoint, false) > radius)
                return false;
        }

        return true;
    }

    /// <summary>A straight single-leg route to a target (used for return-home).</summary>
    public static BotPath PathTo(Vector3 target, float maxStepPerTick = 5f, float arrivalRadius = ArrivalRadiusDefault)
        => new([target], LoopMode.Once, arrivalRadius, maxStepPerTick);
}
