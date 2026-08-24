using System.Collections.Concurrent;

using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M5 adapter for the scheduler's <see cref="IBotStepExecutor"/> seam (slice
/// #8): runs one actor step per scheduler wake for the bot's character.
///
/// The scheduler guarantees at most one in-flight step per bot (execution
/// lease), so this adapter needs no per-bot concurrency guard of its own —
/// it drives the actor (which is likewise single-writer) from exactly one
/// worker at a time.
///
/// Step semantics:
///  - One <see cref="IGameplayActor.Tick"/> per step with the wall-clock
///    time since the bot's previous step (movement legs advance by
///    speed × elapsed; timeout accounting uses the same clock).
///  - The return value tells the scheduler when to wake the bot next:
///    while the actor has a live (non-terminal) request, the bot keeps
///    waking on the scheduler's scan cadence; once the actor is idle the
///    bot goes dormant (null) — it only wakes again via an explicit
///    <see cref="IPlayerBotScheduler.Wake"/>/WakeAt/WakeAfter call from a
///    controller. No idle spin, exactly the spec §4 wake model.
///
/// DI note: this replaces <see cref="UnwiredBotStepExecutor"/> in Program.cs;
/// it is the designed M5 landing point for that seam.
/// </summary>
public sealed class GameplayActorStepExecutor : IBotStepExecutor
{
    /// <summary>Max elapsed reported per step (clamp against scheduler stalls).</summary>
    public static readonly TimeSpan MaxStepElapsed = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<uint, IGameplayActor> _actors = [];
    private readonly ConcurrentDictionary<uint, DateTime> _lastStepUtc = [];

    // Cached completed results (see BotRoamStepExecutor — no per-wake Task churn).
    private static readonly Task<TimeSpan?> DormantTask = Task.FromResult<TimeSpan?>(null);
    private Task<TimeSpan?>? _cadenceTask;

    /// <summary>Step cadence reported to the scheduler while a request is live.</summary>
    public TimeSpan ActiveCadence { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Clock for elapsed accounting (tests inject FakeTimeProvider).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Actor factory seam (tests inject a recording actor; production uses
    /// the real <see cref="GameplayActor"/> over the bot's character).
    /// </summary>
    public Func<Character, IGameplayActor> ActorFactory { get; init; } = c => new GameplayActor(c);

    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = TimeProvider.GetUtcNow().UtcDateTime;
        if (!_actors.TryGetValue(bot.CharacterId, out var actor))
        {
            actor = ActorFactory(bot.Character);
            _actors[bot.CharacterId] = actor;
        }

        // Elapsed since the bot's previous step (first step = one cadence).
        var elapsed = _lastStepUtc.TryGetValue(bot.CharacterId, out var last)
            ? now - last
            : ActiveCadence;
        _lastStepUtc[bot.CharacterId] = now;
        if (elapsed > MaxStepElapsed)
            elapsed = MaxStepElapsed;

        actor.Tick(elapsed);

        // Live request → keep waking on the scan cadence; idle → dormant.
        var live = actor.ActiveRequest is { IsTerminal: false };
        return live
            ? (_cadenceTask ??= Task.FromResult<TimeSpan?>(ActiveCadence))
            : DormantTask;
    }
}
