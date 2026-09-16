#nullable enable

using System.Collections.Concurrent;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Scheduler seam adapter implementing <see cref="IBotStepExecutor"/> for autonomous GOAP bot execution.
///
/// Drives the autonomous decision cycle (sense -> arbitrate -> plan -> execute -> verify) on each
/// scheduler wake without creating separate background threads, busy-waits, or parallel loops.
/// All actions pass strictly through <see cref="IGameplayActor"/> validation.
/// </summary>
public sealed class GoapBotStepExecutor : IBotStepExecutor
{
    private static readonly Task<TimeSpan?> DormantTask = Task.FromResult<TimeSpan?>(null);

    private readonly ConcurrentDictionary<uint, IGameplayActor> _actors = [];
    private readonly ConcurrentDictionary<uint, IGoapPlanRunner> _runners = [];
    private readonly ConcurrentDictionary<uint, BotContext> _contexts = [];
    private readonly ConcurrentDictionary<uint, DateTime> _lastStepUtc = [];

    public TimeSpan ActiveCadence { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Func<Character, IGameplayActor> ActorFactory { get; init; } = c => new GameplayActor(c);
    public Func<IGoapPlanRunner> RunnerFactory { get; init; } = () => new GoapPlanRunner();
    public Func<BotContext> ContextFactory { get; init; } = () => new BotContext();

    public IGoapPlanRunner GetRunner(uint characterId) => _runners.GetOrAdd(characterId, _ => RunnerFactory());
    public BotContext GetContext(uint characterId) => _contexts.GetOrAdd(characterId, _ => ContextFactory());
    public IGameplayActor GetActor(uint characterId, Character character) => _actors.GetOrAdd(characterId, _ => ActorFactory(character));

    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bot);

        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var actor = GetActor(bot.CharacterId, bot.Character);
        var runner = GetRunner(bot.CharacterId);
        var context = GetContext(bot.CharacterId);

        var elapsed = _lastStepUtc.TryGetValue(bot.CharacterId, out var last)
            ? now - last
            : ActiveCadence;
        _lastStepUtc[bot.CharacterId] = now;
        if (elapsed > TimeSpan.FromSeconds(1))
            elapsed = TimeSpan.FromSeconds(1);

        // 1. Tick GOAP decision & plan execution engine
        bool planActive = runner.Tick(bot, actor, context);

        // 2. Advance actor execution boundary (movement legs, timeouts, effect processing)
        actor.Tick(elapsed);

        // 3. Cadence decision: if an actor request is live or plan is executing, wake on scan cadence
        bool requestLive = actor.ActiveRequest is { IsTerminal: false };
        bool shouldStayAwake = planActive || requestLive;

        return shouldStayAwake
            ? Task.FromResult<TimeSpan?>(ActiveCadence)
            : DormantTask;
    }
}
