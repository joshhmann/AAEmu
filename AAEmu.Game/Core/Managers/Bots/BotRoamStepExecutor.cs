using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Roam-driven step executor for player bots (integration card t_6bad0654 —
/// the living loop's behavior layer, Option A visibility).
///
/// Per scheduler wake this executor:
///   1. issues the next <see cref="IGameplayActor.MoveTo"/> leg when the
///      actor is idle and the bot has a route (BotPath waypoint loop), and
///   2. ticks the M5 actor (advances the leg through the ordinary Transform),
///   3. applies the Option A visibility layer:
///        a. ground clamp — Z is snapped to the heightmap via
///           WorldManager.GetReferenceHeight (the Simulation.cs:394 pattern),
///        b. throttled movement broadcast — SCOneUnitMovementPacket is
///           broadcast to around-units at ~4-6 Hz (reduced vs the NPC 10 Hz
///           cadence) so real clients see the bot walking.
///
/// The scheduler's per-bot execution lease guarantees at most one in-flight
/// step per bot, so this executor needs no per-bot concurrency guard — it
/// drives the actor (single-writer) from exactly one worker at a time.
///
/// DI note: this replaces <see cref="GameplayActorStepExecutor"/> as the
/// production IBotStepExecutor wiring in Program.cs. Bots WITHOUT a roam
/// route behave exactly like the plain actor executor (tick-only); the roam
/// drive + visibility is additive per-route.
/// </summary>
public sealed class BotRoamStepExecutor : IBotStepExecutor
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Max elapsed reported per step (clamp against scheduler stalls).</summary>
    public static readonly TimeSpan MaxStepElapsed = TimeSpan.FromSeconds(1);

    /// <summary>Step cadence reported while a request is live.</summary>
    public TimeSpan ActiveCadence { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Minimum interval between movement broadcasts (default = 5 Hz cap).</summary>
    public TimeSpan BroadcastInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Clock for elapsed accounting + broadcast throttle (tests inject FakeTimeProvider).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Actor factory seam (tests inject a recording actor).</summary>
    public Func<Character, IGameplayActor> ActorFactory { get; init; } = c => new GameplayActor(c);

    /// <summary>Walk speed for roam legs (m/s — walking pace, matches ActorFlags walk).</summary>
    public float RoamSpeed { get; init; } = 2.5f;

    /// <summary>Per-leg navigation budget (longer than the actor default so a full route leg never times out mid-walk).</summary>
    public TimeSpan RoamLegTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private sealed class BotRoamState
    {
        public required IGameplayActor Actor { get; init; }
        public BotPath? Path { get; set; }
        public ActorRequest? PendingLeg { get; set; }
        public DateTime LastBroadcastUtc { get; set; } = DateTime.MinValue;
        public Vector3? LastBroadcastPosition { get; set; }
    }

    private readonly ConcurrentDictionary<uint, BotRoamState> _states = [];
    private readonly ConcurrentDictionary<uint, DateTime> _lastStepUtc = [];

    /// <summary>
    /// Assigns a roam route to a bot. The route is walked as consecutive
    /// MoveTo legs (Loop mode = patrol forever). Passing null clears the route
    /// (bot returns to tick-only / dormant behavior). Creates the per-bot
    /// state on first assignment (the coordinator arms routes BEFORE the
    /// scheduler's first wake, so the state must exist pre-step). Keyed by
    /// <paramref name="character"/>.Id — the same key the scheduler uses
    /// (PlayerBotRuntime.CharacterId).
    /// </summary>
    public void SetRoamRoute(Character character, BotPath? path)
    {
        ArgumentNullException.ThrowIfNull(character);

        var characterId = character.Id;
        if (!_states.TryGetValue(characterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(character) };
            _states[characterId] = state;
        }

        state.Path = path;
        if (path != null)
            Logger.Info("Roam route assigned: bot {CharacterId} — {Count} waypoints ({Mode})",
                characterId, path.Waypoints.Count, path.Mode);
    }

    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = TimeProvider.GetUtcNow().UtcDateTime;
        if (!_states.TryGetValue(bot.CharacterId, out var state))
        {
            state = new BotRoamState { Actor = ActorFactory(bot.Character) };
            _states[bot.CharacterId] = state;
        }

        var actor = state.Actor;

        // 1. Issue the next leg when idle and a route is active. The issued
        // leg is remembered as PendingLeg — GameplayActor clears its active
        // request the moment it reaches a terminal state, so the executor
        // can only observe a completion through the leg reference itself.
        if (actor.ActiveRequest is not { IsTerminal: false } && state.Path is { IsFinished: false })
        {
            var target = state.Path.CurrentTarget;
            var leg = actor.MoveTo(target, RoamSpeed, RoamLegTimeout);
            state.PendingLeg = leg;
            if (leg.IsTerminal && leg.State != ActorLifecycleState.Completed)
            {
                Logger.Warn("Roam leg rejected for bot {CharacterId}: {State} ({Reason}) — advancing route",
                    bot.CharacterId, leg.State, leg.Detail);
                _ = state.Path.Move(bot.Character.Transform.World.Position); // advance past the unreachable point
                state.PendingLeg = null; // already advanced here
            }
        }

        // 2. Tick the actor (advances the active leg through the Transform).
        var elapsed = _lastStepUtc.TryGetValue(bot.CharacterId, out var last)
            ? now - last
            : ActiveCadence;
        _lastStepUtc[bot.CharacterId] = now;
        if (elapsed > MaxStepElapsed)
            elapsed = MaxStepElapsed;

        actor.Tick(elapsed);

        // 2b. Route advance on arrival: when the pending Move leg reached a
        // terminal state (arrived / already-at-destination — immediately or
        // during this tick), move the route to the next waypoint so the bot
        // keeps walking (Loop wraps, Once finishes). Without this the
        // executor would re-issue the SAME waypoint forever — the bot
        // freezes after one leg.
        if (state.PendingLeg is { IsTerminal: true, Action: ActorActionType.Move }
            && state.Path is { IsFinished: false })
        {
            _ = state.Path.Move(bot.Character.Transform.World.Position);
            state.PendingLeg = null;
        }

        // 3a. Ground clamp — the Simulation.cs:394 pattern: after movement,
        // snap Z to the heightmap so bots walk ON the terrain, never under it.
        var position = bot.Character.Transform.World.Position;
        var clampedZ = WorldManager.Instance.GetReferenceHeight(
            null, position.X, position.Y, position.Z, bot.Character.Transform.ZoneId);
        if (clampedZ != 0f && Math.Abs(clampedZ - position.Z) > 0.05f)
        {
            bot.Character.Transform.Local.SetPosition(position.X, position.Y, clampedZ);
            position = bot.Character.Transform.World.Position;
        }

        // 3b. Throttled movement broadcast (4-6 Hz). BroadcastPacket sends to
        // around-units (real clients near the bot); the bot's own SendPacket
        // no-ops at the null-safe sink (no connection).
        if (now - state.LastBroadcastUtc >= BroadcastInterval)
        {
            if (state.LastBroadcastPosition is { } lastPos &&
                Vector3.Distance(lastPos, position) > 0.01f)
            {
                var moveType = BuildMoveType(bot.Character, position);
                bot.Character.BroadcastPacket(new SCOneUnitMovementPacket(bot.Character.ObjId, moveType), true);
            }

            state.LastBroadcastUtc = now;
            state.LastBroadcastPosition = position;
        }

        // Live request → keep waking on the scan cadence. A bot with an
        // unfinished route also stays alive in the instant between legs —
        // the next step issues the next leg (returning dormant here would
        // make the scheduler stop waking the bot, freezing it after one
        // leg mid-route). Only a route-less or finished-route bot goes
        // dormant.
        var live = actor.ActiveRequest is { IsTerminal: false };
        var routeActive = state.Path is { IsFinished: false };
        return Task.FromResult<TimeSpan?>(live || routeActive ? ActiveCadence : null);
    }

    /// <summary>
    /// Builds the movement payload for the broadcast — the exact shape
    /// Simulation.cs uses for NPCs (position, velocity from facing, rotation
    /// bytes, walk flags/stance/alertness). ActorFlags 5 = walking.
    /// </summary>
    private static UnitMoveType BuildMoveType(Character character, Vector3 position)
    {
        var moveType = (UnitMoveType)MoveType.GetType(MoveTypeEnum.Unit);
        var angle = MathUtil.CalculateAngleFrom(character.Transform.World.Position, position);
        var (velX, velY) = MathUtil.AddDistanceToFront(4000, 0, 0, (float)angle.DegToRad());

        moveType.X = position.X;
        moveType.Y = position.Y;
        moveType.Z = position.Z;
        moveType.VelX = (short)velX;
        moveType.VelY = (short)velY;
        moveType.RotationX = 0;
        moveType.RotationY = 0;
        moveType.RotationZ = 0;
        moveType.ActorFlags = 5; // 5-walk
        moveType.Flags = 0;
        moveType.DeltaMovement = [0, 63, 0];
        moveType.Stance = GameStanceType.Relaxed;   // IDLE = 0x1 (Npc.CurrentGameStance is NPC-only; characters idle in Relaxed)
        moveType.Alertness = MoveTypeAlertness.Idle; // IDLE = 0x0
        moveType.Time = (uint)(DateTime.UtcNow - DateTime.UtcNow.Date).TotalMilliseconds;
        return moveType;
    }
}
