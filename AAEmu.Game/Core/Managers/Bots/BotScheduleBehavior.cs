using System.Numerics;

using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Visible-behavior seam for C1 Schedules v1: how a resolved phase becomes
/// movement. Production (<see cref="RoamRouteScheduleBehavior"/>) reuses the
/// EXISTING presence movement surface — <see cref="BotRoamStepExecutor"/>
/// routes (stuck-detection Move legs + ground clamp + throttled broadcast) —
/// so schedules add no parallel movement system (AGENTS.md #9/#10).
///
///   - Work  → <see cref="ResumeRoam"/>: the ordinary roam loop around the
///     bot's work anchor (= the current roam area — unchanged behavior).
///   - Rest / Home / Travel → <see cref="MoveToAnchor"/>: a single bounded
///     Move leg toward home or the work anchor; on arrival the route
///     finishes and the bot idles there.
/// </summary>
public interface IBotScheduleBehavior
{
    /// <summary>Arms the normal roam/presence loop around the bot's work anchor.</summary>
    void ResumeRoam(PlayerBotRuntime bot, string scheduleJson, Vector3 fallbackCenter);

    /// <summary>Walks the bot toward an anchor point (home / work center); idles on arrival.</summary>
    void MoveToAnchor(PlayerBotRuntime bot, Vector3 target);
}

/// <summary>
/// Production behavior: routes through the DI-registered
/// <see cref="BotRoamStepExecutor"/>. The Work phase replays the roam-loop
/// descriptor recorded in the bot's schedule JSON (center/radius/seed); the
/// other phases arm a single-leg <see cref="BotPath.PathTo"/> walk.
/// </summary>
public sealed class RoamRouteScheduleBehavior : IBotScheduleBehavior
{
    private readonly BotRoamStepExecutor _stepExecutor;
    private readonly Func<Vector3, uint, float>? _groundHeightProvider;

    public RoamRouteScheduleBehavior(BotRoamStepExecutor stepExecutor,
        Func<Vector3, uint, float>? groundHeightProvider = null)
    {
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _groundHeightProvider = groundHeightProvider;
    }

    public void ResumeRoam(PlayerBotRuntime bot, string scheduleJson, Vector3 fallbackCenter)
    {
        ArgumentNullException.ThrowIfNull(bot);

        // Rebuild the SAME deterministic patrol route the coordinator armed
        // at provision time (same descriptor keys → same waypoints).
        var center = fallbackCenter;
        var radius = 30f;
        var seed = (int)(bot.CharacterId % 8);
        if (BotSchedulePayload.TryReadRoamDescriptor(scheduleJson, out var storedCenter, out var storedRadius, out var storedSeed))
        {
            center = storedCenter;
            radius = storedRadius;
            seed = storedSeed;
        }

        var route = BotPresenceCoordinator.BuildRoamRoute(
            center, radius, seed, bot.Character.Transform.ZoneId, _groundHeightProvider);
        _stepExecutor.SetRoamRoute(bot.Character, route);
    }

    public void MoveToAnchor(PlayerBotRuntime bot, Vector3 target)
    {
        ArgumentNullException.ThrowIfNull(bot);
        _stepExecutor.SetRoamRoute(bot.Character, BotPath.PathTo(target));
    }
}
