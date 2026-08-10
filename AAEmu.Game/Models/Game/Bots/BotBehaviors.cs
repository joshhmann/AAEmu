using System.Numerics;

using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Explicit bot behavior states (M6.3 slice: idle, roam, quest-drive, return).
/// Quest-drive is the PRIMARY mode: while quest work is pending it preempts
/// roam/idle; return restores the bot to its safe zone.
/// </summary>
public enum BotBehaviorState
{
    /// <summary>Nothing to do — bot stands at its position.</summary>
    Idle,

    /// <summary>Walking a bounded waypoint route inside the safe zone.</summary>
    Roam,

    /// <summary>Quest work in progress — the controller's primary mode (preempts roam).</summary>
    QuestDrive,

    /// <summary>Walking back to home (safe return — escape hatch from any stop).</summary>
    Return
}

/// <summary>
/// Small explicit behavior stack: higher states preempt lower ones; the
/// stack is what makes quest-drive preempt roam and return preempt both.
/// </summary>
public class BotBehaviorStack
{
    private readonly Stack<BotBehaviorState> _stack = [];

    /// <summary>The active state (Idle when the stack is empty).</summary>
    public BotBehaviorState Current => _stack.Count > 0 ? _stack.Peek() : BotBehaviorState.Idle;

    public int Depth => _stack.Count;

    public void Push(BotBehaviorState state)
    {
        if (state == BotBehaviorState.Idle)
            return; // Idle is the empty-stack state, never pushed
        _stack.Push(state);
    }

    /// <summary>Pops the active state; returns what was popped (Idle when empty).</summary>
    public BotBehaviorState Pop() => _stack.Count > 0 ? _stack.Pop() : BotBehaviorState.Idle;

    /// <summary>Empties the stack (immediate idle).</summary>
    public void Reset() => _stack.Clear();
}

/// <summary>
/// Behavior controller for headless bots (M6-light, additive).
///
/// Orchestrates the behavior stack around the M2b PlayerBotController:
///   - safety first (M6.2): every work tick passes through BotSafetyMonitor;
///     a stop aborts work and safe-returns the bot home (manual stops idle
///     in place)
///   - quest-drive primary (M6.3): while QuestWorkPending, the stack forces
///     QuestDrive and runs one QuestDriveStep per tick; roam yields
///   - roam is bounded: TryStartRoam rejects any route whose waypoints lie
///     outside the safe zone
///   - return is the escape hatch: bounds are not enforced on the way home
///
/// The controller holds NO quest state and performs NO gameplay mutations
/// itself — movement goes through the ordinary Character Transform (the
/// same facility Simulation.MoveTo uses), quest work goes through the
/// PlayerBotController's real engine paths. Composition rule intact
/// (AGENTS.md #9/#10).
/// </summary>
public class PlayerBotBehaviorController
{
    public PlayerBotController Bot { get; }
    public BotSafetyMonitor Safety { get; }
    public BotBehaviorStack Stack { get; } = new();

    /// <summary>True while quest work remains — quest-drive is primary while set.</summary>
    public bool QuestWorkPending { get; private set; }

    /// <summary>
    /// Executes one unit of quest work per tick; return true while more
    /// quest work remains. The M2b quest driver (PlayerbotQuestDriver)
    /// is the natural implementation — the behavior layer only sequences.
    /// </summary>
    public Func<bool>? QuestDriveStep { get; set; }

    private BotPath? _path;
    private BotPath? _returnPath;

    public PlayerBotBehaviorController(PlayerBotController bot, Vector3 homePosition)
    {
        Bot = bot;
        Safety = new BotSafetyMonitor(homePosition);
    }

    public BotBehaviorState CurrentState => Stack.Current;
    public bool IsStopped => Safety.IsStopped;
    public BotStopReason StopReason => Safety.StopReason;
    public BotPath? CurrentPath => _path;
    public Vector3 Position => Bot.Character.Transform.World.Position;

    #region Transitions

    /// <summary>
    /// Starts a bounded roam. Rejected (returns false) when the bot is
    /// stopped or when any waypoint lies outside the safe zone.
    /// </summary>
    public bool TryStartRoam(BotPath path)
    {
        if (Safety.IsStopped)
            return false;
        if (!path.AllWaypointsWithin(Safety.HomePosition, Safety.SafeRadius))
            return false;

        _path = path;
        _returnPath = null;
        if (Stack.Current != BotBehaviorState.Roam)
            Stack.Push(BotBehaviorState.Roam);
        return true;
    }

    /// <summary>Safe-return: walks the bot back to home, then resumes the stack below.</summary>
    public void RequestReturnHome() => StartReturnHome();

    /// <summary>Sets quest-work pending (quest-drive primary mode).</summary>
    public void SetQuestWork(bool pending) => QuestWorkPending = pending;

    /// <summary>
    /// Combat gate: only legal while quest-drive is the active state with
    /// work pending — the quest engine is the only legitimate combat demand
    /// (no bot-initiated combat).
    /// </summary>
    public bool TryGrantCombat()
    {
        if (Stack.Current != BotBehaviorState.QuestDrive || !QuestWorkPending)
            return false;
        Safety.GrantCombat();
        return true;
    }

    /// <summary>Operator stop — aborts work; behavior returns home or idles.</summary>
    public void Stop(BotStopReason reason) => Safety.RequestStop(reason);

    /// <summary>Resumes the bot after a stop (clears the latched reason).</summary>
    public void Resume()
    {
        Safety.Reset();
        _returnPath = null;
    }

    #endregion

    #region Tick

    /// <summary>
    /// One behavior tick. Order: stop handling → safety observation →
    /// quest-drive (primary) → state dispatch.
    /// </summary>
    public void Tick()
    {
        var position = Position;
        var freeSlots = Bot.Character.Inventory.Bag.FreeSlotCount;

        // Already stopped: only the return leg may continue; otherwise apply
        // stop handling now (covers stops latched between ticks).
        if (Safety.IsStopped)
        {
            if (Stack.Current == BotBehaviorState.Return)
                DriveReturn(position);
            else
                HandleStop();
            return;
        }

        // Observe the tick through the safety monitor.
        if (Stack.Current == BotBehaviorState.Return)
        {
            Safety.ObserveReturnTick(position, freeSlots);
        }
        else
        {
            // Only an active roam leg is navigation; quest work is not a
            // navigation leg (its paused roam leg must not accrue nav time).
            var target = Stack.Current == BotBehaviorState.Roam ? _path?.CurrentTarget : null;
            Safety.ObserveWorkTick(position, target, freeSlots);
        }

        // Stop handling: abort work — safe return unless the operator stopped us.
        if (Safety.IsStopped)
        {
            HandleStop();
            return;
        }

        // Quest-drive primary mode: pending quest work preempts everything.
        if (QuestWorkPending)
        {
            if (Stack.Current != BotBehaviorState.QuestDrive)
                Stack.Push(BotBehaviorState.QuestDrive);

            var moreWork = QuestDriveStep?.Invoke() ?? false;
            if (!moreWork)
            {
                QuestWorkPending = false;
                Stack.Pop();
            }

            return;
        }

        // Dispatch the active state.
        switch (Stack.Current)
        {
            case BotBehaviorState.Roam:
                DriveRoam(position);
                break;
            case BotBehaviorState.Return:
                DriveReturn(position);
                break;
            case BotBehaviorState.Idle:
            case BotBehaviorState.QuestDrive:
                // QuestDrive without pending work is transient (popped above).
                break;
        }
    }

    #endregion

    #region Internals

    private void DriveRoam(Vector3 position)
    {
        if (_path == null)
        {
            Stack.Pop();
            return;
        }

        ApplyPosition(_path.Move(position));

        if (_path.IsFinished)
            Stack.Pop(); // route done → back to the state below (typically Idle)
    }

    private void DriveReturn(Vector3 position)
    {
        _returnPath ??= BotPath.PathTo(Safety.HomePosition, _path?.MaxStepPerTick ?? 5f);
        ApplyPosition(_returnPath.Move(position));

        if (_returnPath.IsFinished)
        {
            _returnPath = null;
            Stack.Pop(); // home again → back to the state below (work stays paused while stopped)
        }
    }

    /// <summary>
    /// Abort handling for a latched stop: manual stops idle in place; every
    /// other reason safe-returns home. A bot that is already home idles —
    /// otherwise the return leg would re-push forever (home + latched stop).
    /// </summary>
    private void HandleStop()
    {
        if (Safety.StopReason == BotStopReason.ManualStop)
        {
            Stack.Reset();
            return;
        }

        if (MathUtil.CalculateDistance(Position, Safety.HomePosition, false) <= BotPath.ArrivalRadiusDefault)
        {
            Stack.Reset(); // already home — nothing to return to
            return;
        }

        StartReturnHome();
    }

    private void StartReturnHome()
    {
        if (Stack.Current != BotBehaviorState.Return)
            Stack.Push(BotBehaviorState.Return);
        _returnPath = null;
    }

    /// <summary>Applies the next position through the ordinary Transform (same facility as Simulation.MoveTo).</summary>
    private void ApplyPosition(Vector3 next)
    {
        var transform = Bot.Character.Transform;
        // Same facing math as Simulation.MoveTo: CalculateAngleFrom returns
        // degrees and SetRotationDegree expects degrees.
        var angle = (float)MathUtil.CalculateAngleFrom(transform.World.Position, next);
        transform.Local.SetRotationDegree(0f, 0f, angle - 90);
        transform.Local.SetPosition(next);
    }

    #endregion
}
