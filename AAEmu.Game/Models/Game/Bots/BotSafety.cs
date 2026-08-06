using System.Numerics;

using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Why a bot's behavior stopped. First reason wins (abort semantics);
/// Reset() clears it.
/// </summary>
public enum BotStopReason
{
    None = 0,

    /// <summary>Position stopped changing while a path target was set (6.2 stuck detection).</summary>
    Stuck,

    /// <summary>A single navigation leg exceeded its tick budget (6.2 navigation timeout).</summary>
    NavigationTimeout,

    /// <summary>Bot left the permitted safe zone while doing work (6.2 world-state guard).</summary>
    OutOfBounds,

    /// <summary>Inventory ran out of free slots (6.2 inventory-full handling).</summary>
    InventoryFull,

    /// <summary>Total tick budget for the bot session exceeded (6.1 tick budget accounting).</summary>
    TickBudgetExceeded,

    /// <summary>Operator/controller requested a stop.</summary>
    ManualStop
}

/// <summary>
/// Safety monitor for headless bots (M6-light safety layer, additive).
///
/// Implements the M6.2 safety-FIRST slice: stuck detection, navigation
/// timeout, safe-zone bounds, inventory-full handling, tick budget, and
/// the combat gate. The combat gate is the ONLY path to combat: it is off
/// by default and can only be granted while the behavior controller is in
/// quest-drive (the quest engine is the only legitimate combat demand).
///
/// The monitor observes ticks and latches the FIRST stop reason; it never
/// mutates the character or the world itself — the behavior controller
/// reacts (safe return / idle).
/// </summary>
public class BotSafetyMonitor
{
    /// <summary>The bot's home position — the center of its safe zone.</summary>
    public Vector3 HomePosition { get; }

    /// <summary>Safe-zone radius around home. Work (roam/quest) outside it stops.</summary>
    public float SafeRadius { get; set; } = 50f;

    /// <summary>Consecutive below-epsilon ticks with a path target = stuck.</summary>
    public int StuckThresholdTicks { get; set; } = 5;

    /// <summary>Movement smaller than this per tick counts as "not moving".</summary>
    public float StuckEpsilon { get; set; } = 0.05f;

    /// <summary>Max ticks spent walking one navigation leg before aborting.</summary>
    public int NavigationTimeoutTicks { get; set; } = 200;

    /// <summary>Total ticks the bot may run before the session stops.</summary>
    public int TickBudget { get; set; } = 10_000;

    /// <summary>Stop when free inventory slots fall to this value or below.</summary>
    public int InventoryFreeSlotsThreshold { get; set; } = 0;

    public BotStopReason StopReason { get; private set; } = BotStopReason.None;

    /// <summary>Total observed ticks (work + return) in this session.</summary>
    public int TicksElapsed { get; private set; }

    private Vector3? _lastPosition;
    private int _stuckTicks;
    private Vector3? _lastTarget;
    private int _navTicks;

    public BotSafetyMonitor(Vector3 homePosition)
    {
        HomePosition = homePosition;
    }

    public bool IsStopped => StopReason != BotStopReason.None;

    /// <summary>Combat gate — off by default; quest-drive is the only grantor.</summary>
    public bool CombatAllowed { get; private set; }

    /// <summary>Only quest-drive may call this (enforced by PlayerBotBehaviorController.TryGrantCombat).</summary>
    public void GrantCombat() => CombatAllowed = true;

    public void RevokeCombat() => CombatAllowed = false;

    /// <summary>Combat is only legal while the bot is not stopped AND the gate is open.</summary>
    public bool CanEngageCombat => CombatAllowed && !IsStopped;

    /// <summary>Operator/controller stop — bypasses observation entirely.</summary>
    public void RequestStop(BotStopReason reason)
    {
        if (reason == BotStopReason.None)
            return;
        if (StopReason == BotStopReason.None)
            StopReason = reason;
    }

    /// <summary>Clears the latched reason (session resume). Never auto-called.</summary>
    public void Reset()
    {
        StopReason = BotStopReason.None;
        _stuckTicks = 0;
        _navTicks = 0;
        _lastPosition = null;
        _lastTarget = null;
    }

    /// <summary>
    /// Observes a WORK tick (roam or quest activity). Full guard set:
    /// bounds, stuck, navigation timeout, inventory, tick budget.
    /// <paramref name="pathTarget"/> is null when no navigation leg is active
    /// (stuck/nav checks only apply to legs).
    /// </summary>
    public void ObserveWorkTick(Vector3 position, Vector3? pathTarget, int freeInventorySlots)
    {
        if (IsStopped)
            return;

        TicksElapsed++;

        // World-state guard: no bot work outside the safe zone.
        if (MathUtil.CalculateDistance(HomePosition, position, false) > SafeRadius)
        {
            StopReason = BotStopReason.OutOfBounds;
            return;
        }

        // Resource bound: inventory-full handling.
        if (freeInventorySlots <= InventoryFreeSlotsThreshold)
        {
            StopReason = BotStopReason.InventoryFull;
            return;
        }

        // Navigation leg bookkeeping (stuck + timeout).
        if (pathTarget is { } target)
        {
            if (_lastTarget == null || !_lastTarget.Equals(target))
            {
                _lastTarget = target;
                _navTicks = 0;
            }

            _navTicks++;
            if (_navTicks > NavigationTimeoutTicks)
            {
                StopReason = BotStopReason.NavigationTimeout;
                return;
            }

            if (_lastPosition is { } last)
            {
                var moved = MathUtil.CalculateDistance(last, position, true);
                if (moved < StuckEpsilon)
                    _stuckTicks++;
                else
                    _stuckTicks = 0;

                if (_stuckTicks >= StuckThresholdTicks)
                {
                    StopReason = BotStopReason.Stuck;
                    return;
                }
            }

            _lastPosition = position;
        }
        else
        {
            _lastTarget = null;
            _navTicks = 0;
            _stuckTicks = 0;
            _lastPosition = null;
        }

        // Session tick budget.
        if (TicksElapsed > TickBudget)
            StopReason = BotStopReason.TickBudgetExceeded;
    }

    /// <summary>
    /// Observes a RETURN-HOME tick. Bounds are intentionally NOT enforced —
    /// the return leg is the safe-return escape hatch (6.2 "safe return");
    /// a bot may always walk home, even from outside its safe zone. Stuck
    /// and nav-timeout checks are also skipped (a slow-but-steady return
    /// must not abort); inventory + tick budget still apply.
    /// </summary>
    public void ObserveReturnTick(Vector3 position, int freeInventorySlots)
    {
        if (IsStopped)
            return;

        TicksElapsed++;

        if (freeInventorySlots <= InventoryFreeSlotsThreshold)
        {
            StopReason = BotStopReason.InventoryFull;
            return;
        }

        if (TicksElapsed > TickBudget)
            StopReason = BotStopReason.TickBudgetExceeded;
    }
}
