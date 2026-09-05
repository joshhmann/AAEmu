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
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        try
        {
            ExecutionBoundary.EnterBotStep();
            var before = ExecutionBoundary.ViolationCount;

            var workerSawStep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() =>
            {
                ExecutionBoundary.AssertTransformWrite();
                workerSawStep.TrySetResult(ExecutionBoundary.IsInsideBotStep);
            });

            var sawStep = await workerSawStep.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(sawStep).IsTrue();
            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(before + 1);
        }
        finally
        {
            ExecutionBoundary.ExitBotStep();
            ExecutionBoundary.ResetForTest();
        }
    }
}
