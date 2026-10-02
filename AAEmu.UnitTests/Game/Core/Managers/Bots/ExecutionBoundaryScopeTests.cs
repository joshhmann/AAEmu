using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Proves the skill-plot dispatch scope fix (live M5 §M5 violation:
/// "Transform write inside bot step ran on thread 47; the single execution
/// boundary is thread 24"). <c>Task.Run</c> captures the ambient
/// <see cref="ExecutionContext"/> — a bot step's AsyncLocal scope flowed onto
/// the plot thread, so plot effects (knockback/blink/teleport) landing ~2 s
/// after a bot's cast tripped the boundary's write assertion.
/// <see cref="ExecutionBoundary.RunUnscoped"/> is the dispatch seam that
/// stops the leak; these tests pin the mechanism from both sides.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class ExecutionBoundaryScopeTests
{
    [Test]
    public async Task RunUnscoped_DoesNotFlowBotStepScope_OntoWorker()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        try
        {
            ExecutionBoundary.EnterBotStep();
            var before = ExecutionBoundary.ViolationCount;

            var workerSawStep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ExecutionBoundary.RunUnscoped(() =>
            {
                // Mirrors the live path: a plot effect writing a Transform on
                // the plot thread calls this same assertion.
                ExecutionBoundary.AssertTransformWrite();
                workerSawStep.TrySetResult(ExecutionBoundary.IsInsideBotStep);
                return Task.CompletedTask;
            });

            var sawStep = await workerSawStep.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(sawStep).IsFalse();
            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(before);
        }
        finally
        {
            ExecutionBoundary.ExitBotStep();
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task RawTaskRun_DoesFlowBotStepScope_OntoWorker()
    {
        // Control: documents the .NET mechanism behind the live violation —
        // a bare Task.Run (the pre-fix Skill.cs dispatch shape) carries the
        // step scope onto the worker and the write assertion fires there.
        //
        // The worker is pinned to a DEDICATED thread (not the pool) on purpose.
        // TUnit runs test bodies on pool threads, and a Task.Run dispatched from
        // a pool thread lands back on that SAME pool thread ~72-91% of the time
        // (measured: 273/300, 234/300, 216/300, 232/300 across four processes).
        // When it does, the worker IS the boundary, so AssertTransformWrite
        // correctly no-ops and the violation counter does not move — the
        // assertion below was a coin flip on runtime thread scheduling, not on
        // the scope-flow behaviour it claims to pin. A dedicated thread makes
        // "the worker is not the boundary" a fact rather than a hope, so the
        // test fails only when scope flow genuinely breaks.
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        try
        {
            ExecutionBoundary.EnterBotStep();
            var before = ExecutionBoundary.ViolationCount;

            var workerSawStep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerThread = new Thread(() =>
            {
                // Explicit flow: a bare Task.Run flows ExecutionContext by
                // default, and so does a new Thread — that IS the mechanism
                // under test, so the control keeps it.
                ExecutionBoundary.AssertTransformWrite();
                workerSawStep.TrySetResult(ExecutionBoundary.IsInsideBotStep);
            })
            { IsBackground = true };
            workerThread.Start();

            var sawStep = await workerSawStep.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(sawStep).IsTrue()
                .Because("a bare off-pool dispatch must carry the bot-step scope onto the worker");
            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(before + 1)
                .Because("a Transform write inside the flowed bot step, on a thread that is not the " +
                         "execution boundary, must register exactly one violation");
        }
        finally
        {
            ExecutionBoundary.ExitBotStep();
            ExecutionBoundary.ResetForTest();
        }
    }
}
