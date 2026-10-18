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
/// position; the caller (BotRoamStepExecutor / schedule behaviors) applies
/// it through the ordinary Transform, exactly like Simulation.MoveTo does
/// for NPCs.
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
    /// <param name="position">Current position.</param>
    /// <param name="flatArrival">
    /// When true, a waypoint counts as reached on FLAT distance alone (the
    /// Simulation RangeToCheckPoint model) — for ground-clamped walkers
    /// where an external clamp owns Z and the waypoint Z may differ from
    /// the clamped Z (BotRoamStepExecutor; t_d7e45251). Default false keeps
    /// the 3D arrival (flat AND Z within ArrivalRadius).
    /// </param>
    public Vector3 Move(Vector3 position, bool flatArrival = false)
    {
        if (IsFinished)
            return position;

        var target = CurrentTarget;

        // Flat distance to the current waypoint (arrival model mirrors
        // Simulation's RangeToCheckPoint; the Z gate is the 3D option).
        var flatDistance = MathUtil.CalculateDistance(position, target, false);
        var zDistance = Math.Abs(target.Z - position.Z);

        if (flatDistance <= ArrivalRadius && (flatArrival || zDistance <= ArrivalRadius))
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
            // Pure vertical movement (waypoint directly above/below; only
            // reachable under the default 3D arrival).
            var zStep = Math.Min(MaxStepPerTick, zDistance);
            var dir = target.Z >= position.Z ? 1f : -1f;
            next = new Vector3(position.X, position.Y, position.Z + dir * zStep);
        }

        // Arrival on landing: a step that lands within the arrival radius of
        // the waypoint completes the leg in the SAME call (an exact step onto
        // a waypoint must not need a second tick to "arrive").
        if (MathUtil.CalculateDistance(next, target, false) <= ArrivalRadius &&
            (flatArrival || Math.Abs(target.Z - next.Z) <= ArrivalRadius))
            return Arrive();

        return next;
    }

    /// <summary>
    /// Advances past the current waypoint WITHOUT requiring the walker to stand
    /// inside <see cref="ArrivalRadius"/>. This is the escape hatch for a
    /// waypoint the bot cannot reach (code review 2026-10-16, F1): the route's
    /// own leg exhausted its walk budget short of the waypoint, and leaving the
    /// cursor there would make every later wake re-issue the same unreachable
    /// target. Bookkeeping is exactly <see cref="Arrive"/>'s (mode wrap, and a
    /// finished Once route ends), so a skipped waypoint behaves like a reached
    /// one for every later step; the caller drives movement itself.
    /// </summary>
    public void SkipCurrentWaypoint() => Arrive();

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

    /// <summary>Safety margin a chunked leg leaves on the envelope (see <see cref="PathTo(Vector3, Vector3, float, float, float, float)"/>).</summary>
    internal const double TravelChunkEnvelopeMargin = 0.5d;

    /// <summary>
    /// Route from <paramref name="from"/> to <paramref name="target"/> as a
    /// MULTI-WAYPOINT walk whenever the two lie further apart than
    /// <paramref name="chunkMeters"/>. Intermediate waypoints sit exactly
    /// <paramref name="chunkMeters"/> from their predecessor and the LAST
    /// waypoint is <paramref name="target"/> itself, so the route's total
    /// length is the straight-line distance with no rounding drift. The
    /// ordinary multi-waypoint machinery then does the rest: the route layer
    /// issues one <c>MoveTo</c> leg per waypoint and <see cref="Arrive"/>
    /// advances the index under <see cref="LoopMode.Once"/> until the last one,
    /// which sets <see cref="IsFinished"/>.
    ///
    /// Inside one chunk this returns the SAME single-leg route
    /// <see cref="PathTo(Vector3, float, float)"/> builds — the near case is
    /// byte-identical to the unwalkable-leg behaviour, not merely equivalent.
    ///
    /// WHY THIS EXISTS (quest travel). Measured on a 30-minute 4-bot
    /// unattended run: 57 of 101 travel legs (56%) died
    /// <c>result=TimedOut failure=Navigation elapsed=60.1s detail=navigation
    /// budget exceeded</c>, each re-dispatching the identical destination every
    /// 60 s, while destinations ranged 25.9-904.0 m from the bot's LIVE
    /// position and the single leg inside 31.8 m completed. The budget was
    /// never the bug — the budget correctly halted a leg that was asked to
    /// walk further than one leg can walk. The fix is in ROUTE CONSTRUCTION,
    /// not in movement: nothing here touches <c>MoveTo</c>, the leg timeout,
    /// or the movement integration, and NO PATHFINDING is implied — the route
    /// is still the straight line to the target, only subdivided. Chunking
    /// does NOT route around terrain, obstacles or water; a blocked straight
    /// line stays blocked, it now fails in bounded legs.
    /// </summary>
    /// <param name="from">Where the route starts — the bot's position at arm time.</param>
    /// <param name="target">Final destination — always preserved exactly.</param>
    /// <param name="chunkMeters">
    /// Leg length to subdivide at: the honest reach of ONE navigation leg.
    /// Values outside [arrivalRadius, envelopeMeters] are clamped into that
    /// range, so a zero or absurd chunk yields a finite, non-empty route.
    /// </param>
    /// <param name="envelopeMeters">Hard bound on any emitted leg length; non-positive disables chunking (single-leg route).</param>
    /// <param name="maxStepPerTick">Bounded movement step, unchanged from <see cref="PathTo(Vector3, float, float)"/>.</param>
    /// <param name="arrivalRadius">Waypoint arrival radius, unchanged from <see cref="PathTo(Vector3, float, float)"/>.</param>
    public static BotPath PathTo(Vector3 from, Vector3 target, float chunkMeters, float envelopeMeters,
        float maxStepPerTick = 5f, float arrivalRadius = ArrivalRadiusDefault)
    {
        var total = MathUtil.CalculateDistance(from, target, true);
        // A zero-length or sub-arrival destination has nothing to walk, a
        // non-positive envelope cannot bound a leg, and an envelope SHORTER than
        // the arrival radius has no legal chunk at all (clamping there would make
        // the lower bound exceed the upper one and throw): all three are the
        // single-leg route, which lands inside one ArrivalRadius on the first
        // Move. The third case is a real caller shape — the chunk derives from
        // RoamSpeed x RoamLegTimeout, so any budget under ~1 s lands here.
        if (!(envelopeMeters > 0f) || !(chunkMeters > 0f) || total < arrivalRadius
            || envelopeMeters < arrivalRadius)
            return PathTo(target, maxStepPerTick, arrivalRadius);

        var chunk = Math.Clamp(chunkMeters, arrivalRadius, envelopeMeters);
        if (total <= chunk)
            return PathTo(target, maxStepPerTick, arrivalRadius);

        // Uniform subdivision along the straight line. `walked` accumulates
        // the exact same chunk the loop bound is tested against, so every
        // emitted leg is <= chunk by construction — no epsilon fudging.
        var waypoints = new List<Vector3>();
        var walked = 0f;
        while (walked + chunk < total)
        {
            walked += chunk;
            waypoints.Add(from + (target - from) * (walked / total));
        }
        waypoints.Add(target);
        return new BotPath(waypoints, LoopMode.Once, arrivalRadius, maxStepPerTick);
    }

    /// <summary>
    /// Builds an analytical smooth circle route around center with dense waypoints for continuous curvature.
    /// </summary>
    public static BotPath BuildCircle(Vector3 center, float radius, int samples = 32)
    {
        var waypoints = new List<Vector3>(samples);
        for (var i = 0; i < samples; i++)
        {
            var theta = (float)(i * 2 * Math.PI / samples);
            var x = center.X + MathF.Cos(theta) * radius;
            var y = center.Y + MathF.Sin(theta) * radius;
            waypoints.Add(new Vector3(x, y, center.Z));
        }
        return new BotPath(waypoints, LoopMode.Loop, ArrivalRadiusDefault, radius * 0.2f);
    }

    /// <summary>
    /// Builds a straight line back-and-forth route between two endpoints.
    /// </summary>
    public static BotPath BuildStraightLine(Vector3 start, Vector3 end)
    {
        return new BotPath([end, start], LoopMode.PingPong, ArrivalRadiusDefault);
    }
}
