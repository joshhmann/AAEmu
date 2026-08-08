using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Bot step executor seam — the unit of work the scheduler's bounded worker
/// pool runs. The M5 actor contract (<c>IGameplayActor</c>) adapts into this
/// seam via <see cref="GameplayActorStepExecutor"/> (one actor Tick per
/// wake; live requests keep the scan cadence, idle bots go dormant). The
/// pre-M5 <see cref="UnwiredBotStepExecutor"/> placeholder remains only as
/// a historical fail-closed default and is no longer DI-wired.
///
/// Contract: the executor runs ONE AI step for the bot and MUST honor
/// <paramref name="cancellationToken"/> (step timeout / scheduler shutdown).
/// It runs on a scheduler worker thread — no per-bot threads, no TickManager
/// subscriptions (spec §4-5, §21-5). The scheduler guarantees at most one
/// in-flight step per bot (execution lease), so implementations never need
/// their own per-bot concurrency guard.
/// </summary>
public interface IBotStepExecutor
{
    /// <summary>
    /// Executes one step for the given bot runtime.
    /// </summary>
    /// <returns>
    /// Delay until the bot's next wake, or <c>null</c> to put the bot to
    /// sleep (dormant — it only wakes again via an explicit
    /// <see cref="IPlayerBotScheduler.Wake"/> / WakeAt / WakeAfter call).
    /// A non-positive delay is clamped by the scheduler to the scan interval.
    /// </returns>
    Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken);
}
