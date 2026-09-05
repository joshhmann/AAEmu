using System.Runtime.CompilerServices;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Debug thread-affinity assertion for the M5 execution boundary (ROADMAP
/// §M5 exit test: "a debug thread-affinity assertion proves zero
/// Character/world mutation off the single execution boundary").
///
/// The single execution boundary is the game-loop tick thread. All bot step
/// execution (and therefore every Character/Transform mutation a bot
/// behavior performs) must run on that thread; controllers may enqueue
/// wakes but may not mutate a Character concurrently.
///
/// Two assertion layers:
///  - <see cref="AssertOnExecutionThread"/> — per bot step. Called at the
///    top of step execution; fires when the step runs off the registered
///    boundary thread. This is the primary proof and is compiled in ALL
///    configurations (Release included), so the gate itself proves the rule.
///  - <see cref="AssertTransformWrite"/> — per Transform write. Position/
///    Rotation writes on <see cref="AAEmu.Game.Models.Game.World.Transform.PositionAndRotation"/>
///    call this; it only asserts while a bot step is executing
///    (<see cref="EnterBotStep"/> / <see cref="ExitBotStep"/> scope), so
///    normal gameplay writes (spawning, packet handlers, loading) are never
///    flagged — only bot-driven mutation.
///
/// State model: the boundary pin and bot-step depth are
/// <see cref="AsyncLocal{T}"/> — scoped to the execution context that set
/// them. The drain re-pins the boundary to ITS thread on every call, so the
/// assertion always compares step execution against the thread that is
/// currently running the drain (async continuations may hop threads; the
/// pin follows the drain, and the violation counter catches any step that
/// does NOT run inside the drain). Tests can pin an explicit boundary via
/// <see cref="SetExecutionThreadForTest"/> (sticky — the drain won't
/// overwrite it). The violation counter is process-wide (Interlocked);
/// tests compare deltas.
///
/// AsyncLocal also makes the write hook stronger: if a step executor ever
/// hops to a continuation on a different thread (async misbehavior), the
/// flowed context still carries the boundary while the thread differs — the
/// write is flagged as off-boundary.
/// </summary>
public static class ExecutionBoundary
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Managed thread ids are ≥ 1; 0 = not pinned (in this context).</summary>
    private static readonly AsyncLocal<int> ExecutionThreadIdField = new();

    /// <summary>True when the test pinned an explicit boundary (drain must not overwrite it).</summary>
    private static readonly AsyncLocal<bool> TestPinnedField = new();

    /// <summary>Bot-step nesting depth (per execution context).</summary>
    private static readonly AsyncLocal<int> BotStepDepth = new();

    private static long _violations;

    /// <summary>The currently registered execution boundary thread (0 = none).</summary>
    public static int ExecutionThreadId => ExecutionThreadIdField.Value;

    /// <summary>True once the boundary thread has been pinned (first drain call or test pin).</summary>
    public static bool IsRegistered => ExecutionThreadIdField.Value != 0;

    /// <summary>True when the calling thread IS the pinned execution boundary.</summary>
    public static bool IsExecutionThread
        => ExecutionThreadIdField.Value == Environment.CurrentManagedThreadId;

    /// <summary>True while a bot step is executing (scope for the write-level assertion).</summary>
    public static bool IsInsideBotStep => BotStepDepth.Value > 0;

    /// <summary>Total execution-boundary violations observed (monotonic; tests compare deltas).</summary>
    public static long ViolationCount => Volatile.Read(ref _violations);

    /// <summary>
    /// Pins the calling thread as the single execution boundary. Called by
    /// the marshal drain on EVERY entry — the boundary is "the thread that is
    /// running the drain right now" (async continuations in tests hop
    /// threads; the pin follows the drain, and any execution that is NOT
    /// inside the drain fires the assertion). A test-pinned boundary is
    /// sticky and wins over the drain.
    /// </summary>
    public static void RegisterExecutionThread()
    {
        if (!TestPinnedField.Value)
            ExecutionThreadIdField.Value = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Fires when the calling thread is not the execution boundary. Logs an
    /// error and increments <see cref="ViolationCount"/>. Compiled in all
    /// configurations — this is the gate's proof.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertOnExecutionThread(string what)
    {
        var boundary = ExecutionThreadIdField.Value;
        if (boundary == 0 || boundary == Environment.CurrentManagedThreadId)
            return;

        Interlocked.Increment(ref _violations);
        Logger.Error(
            "EXECUTION BOUNDARY VIOLATION: {What} ran on thread {Thread}; the single execution boundary is thread {Boundary} — Character/world mutation off the game loop (ROADMAP §M5).",
            what, Environment.CurrentManagedThreadId, boundary);
    }

    /// <summary>
    /// Enters bot-step scope: Transform writes assert the boundary thread
    /// until <see cref="ExitBotStep"/>. Always compiled (the depth check is a
    /// single AsyncLocal read; no cost when no step is running).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterBotStep()
    {
        BotStepDepth.Value++;
        AssertOnExecutionThread("bot step execution");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExitBotStep()
    {
        BotStepDepth.Value--;
    }

    /// <summary>
    /// Write-level assertion called from <c>PositionAndRotation</c> Position/
    /// Rotation setters. Only asserts while a bot step is executing; normal
    /// gameplay writes are outside the bot boundary's scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertTransformWrite()
    {
        if (BotStepDepth.Value == 0)
            return;
        AssertOnExecutionThread("Transform write inside bot step");
    }

    /// <summary>
    /// Starts threadpool work WITHOUT the caller's execution scope.
    /// <c>Task.Run</c> captures the ambient <see cref="ExecutionContext"/> —
    /// a bot step's <see cref="EnterBotStep"/> scope would flow onto the
    /// worker thread and any Transform write there would trip
    /// <see cref="AssertTransformWrite"/> as a false off-boundary violation
    /// (live signature: skill-plot effects landing ~2 s after a bot's cast).
    /// Engine dispatches that are independent work (skill plots) must use
    /// this so the step scope never leaks onto pool threads.
    /// </summary>
    internal static Task RunUnscoped(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(work);
    }

    /// <summary>Test seam: pins an explicit boundary thread (sticky — the drain won't overwrite it). 0 = unpinned.</summary>
    public static void SetExecutionThreadForTest(int threadId)
    {
        TestPinnedField.Value = true;
        ExecutionThreadIdField.Value = threadId;
    }

    /// <summary>Test seam: clears this context's boundary and step scope (violations are left for delta comparisons).</summary>
    public static void ResetForTest()
    {
        TestPinnedField.Value = false;
        ExecutionThreadIdField.Value = 0;
        BotStepDepth.Value = 0;
    }
}
