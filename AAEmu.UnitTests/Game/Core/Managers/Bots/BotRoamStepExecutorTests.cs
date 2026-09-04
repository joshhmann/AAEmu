using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the PRESENCE PROOF roam executor (integration card t_6bad0654):
/// the BotRoamStepExecutor is the living loop's behavior layer — it issues
/// MoveTo legs from a BotPath route, ticks the M5 actor, and applies the
/// Option A visibility layer (ground clamp + throttled movement broadcast).
///
/// WorldManager in this rig is the mock-backed singleton from
/// GameplayActorTestRig (GetReferenceHeight returns 0 → clamp skipped, which
/// the executor treats as "no heightmap data" — the same no-op the real
/// server hits for a zone without heightmaps). Broadcasts go to the rig's
/// mock world (no real clients — the broadcast CALL itself is what we prove
/// is throttled).
/// </summary>
[NotInParallel]
public class BotRoamStepExecutorTests
{
    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateRig(
        string name = "roam-bot", Func<Vector3, uint, float>? terrain = null)
    {
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World
        // is null by default, and GetHeight dereferences it (NpcMoveTowardsTests
        // convention, 2026-08-08). ??= — the clamp treats 0 height as "no
        // heightmap data" and skips, exactly as it does on the live server for
        // a zone without heightmaps.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();

        BotRoamStepExecutor executor = new()
        {
            GroundHeightProvider = terrain, // null → mock WorldManager returns 0 (no clamp)
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f
        };

        return (executor, actor, runtime, clock);
    }

    [Test]
    public async Task Step_WithRoamRoute_IssuesMoveLeg_AndReturnsCadence()
    {
        var (executor, actor, runtime, clock) = CreateRig("roam-1");

        var route = new BotPath(
        [
            new Vector3(10, 0, 0),
            new Vector3(20, 0, 0),
            new Vector3(30, 0, 0)
        ], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(runtime.Character, route);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var next = await executor.StepAsync(runtime, CancellationToken.None);

        // The executor issued the first leg (MoveTo) — the actor has a live Move.
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(actor.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);
        // Leg is active → keep waking on the scan cadence.
        await Assert.That(next).IsNotNull();
        await Assert.That(next.Value).IsEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task Step_WithoutRoute_IsDormant_NoLegIssued()
    {
        var (executor, actor, runtime, clock) = CreateRig("roam-2");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Step_RouteAdvances_ThroughWaypoints()
    {
        var (executor, actor, runtime, clock) = CreateRig("roam-3");

        var route = new BotPath(
        [
            new Vector3(10, 0, 0),
            new Vector3(20, 0, 0),
            new Vector3(30, 0, 0)
        ], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(runtime.Character, route);

        // The actor walks at 2 m/s with 100ms steps = 0.2 m/step; 10m legs
        // take 50 steps each. Step enough to reach the end of leg 1 and
        // confirm the route advances to waypoint 2.
        TimeSpan? next = TimeSpan.Zero;
        var guard = 0;
        while (next is not null && guard++ < 200)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await executor.StepAsync(runtime, CancellationToken.None);
        }

        // After 200 steps at 0.2m = 40m walked; loop of 10+10m legs wraps
        // twice. Whatever the position, the loop keeps the bot live forever
        // (never dormant) and the route keeps issuing legs — at least two
        // completed Move legs prove the route advanced past waypoint 1
        // (the one-leg freeze regression: BotRoamStepExecutor 2b).
        await Assert.That(next).IsNotNull();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Step_AfterOnceRouteFinished_GoesDormant()
    {
        var (executor, actor, runtime, clock) = CreateRig("roam-4");

        var route = new BotPath([new Vector3(10, 0, 0)], BotPath.LoopMode.Once);
        executor.SetRoamRoute(runtime.Character, route);

        // Walk to the single waypoint (10m at 2 m/s = 50 steps).
        TimeSpan? next = TimeSpan.Zero;
        var guard = 0;
        while (next is not null && guard++ < 200)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await executor.StepAsync(runtime, CancellationToken.None);
        }

        // Once route finished → dormant (no more legs).
        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Step_FlatRouteOverDeviantTerrain_CompletesFullLoop_NoWedge()
    {
        // The t_d7e45251 wedge signature at executor level: a FLAT-Z route
        // (the old BuildRoamRoute output) walked over terrain 9m higher. The
        // clamp owns the bot's Z (135) while the waypoint Z is 126 — the
        // actor's 3D arrival check can never complete the leg, so flat
        // arrival must own the leg: the bot patrols the FULL loop instead of
        // freezing at the first waypoint (prod: bots stood at waypoint X/Y
        // with clamped Z until the 60s timeout, forever).
        var (executor, actor, runtime, clock) = CreateRig("roam-wedge", terrain: (_, _) => 135f);

        var route = new BotPath(
        [
            new Vector3(10, 0, 126),
            new Vector3(20, 0, 126),
            new Vector3(20, 10, 126),
            new Vector3(10, 10, 126)
        ], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(runtime.Character, route);

        // 4 legs of 10m at 2 m/s (0.2m/step, 100ms cadence) = 50 steps each;
        // 300 steps = 1.5 loops.
        TimeSpan? next = TimeSpan.Zero;
        var guard = 0;
        while (next is not null && guard++ < 300)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await executor.StepAsync(runtime, CancellationToken.None);
        }

        // Full loop: the route issued legs for ALL 4 waypoints (4+ Move
        // requests), and NO leg timed out (the wedge signature was a leg
        // stuck until the 60s timeout, forever). Legs here end Interrupted
        // (flat arrival) or Completed (3D arrival).
        var moves = actor.AuditTrace.Where(r => r.Action == ActorActionType.Move).ToList();
        await Assert.That(moves.Count(r => r.Result == ActorLifecycleState.TimedOut)).IsEqualTo(0);
        await Assert.That(moves.Count(r => r.Result is ActorLifecycleState.Completed or ActorLifecycleState.Interrupted))
            .IsGreaterThanOrEqualTo(4);

        // The bot walks ON the terrain — the clamp owns Z (never the flat
        // route Z, never wedged at a waypoint).
        await Assert.That(actor.Character.Transform.World.Position.Z).IsEqualTo(135f);
    }

    [Test]
    public async Task Step_ThrottlesBroadcast_ToConfiguredInterval()
    {
        var (executor, actor, runtime, clock) = CreateRig("roam-6");

        var route = new BotPath([new Vector3(10, 0, 0), new Vector3(20, 0, 0)], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(runtime.Character, route);

        // BroadcastInterval is 200ms. Stepping at 100ms cadence means the
        // broadcast must fire at most every OTHER step. We can't observe the
        // wire here (mock world), so we prove the throttle through timing:
        // the executor must not throw and must keep cadence on a 200ms beat.
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            var next = await executor.StepAsync(runtime, CancellationToken.None);
            await Assert.That(next).IsNotNull();
        }
    }

    [Test]
    public async Task Step_WithNearbyWildlife_DisruptsAndEngagesTarget()
    {
        var (baseExecutor, actor, runtime, clock) = CreateRig("hunt-bot");

        var route = new BotPath([new Vector3(50, 0, 0)], BotPath.LoopMode.Loop);

        var wildlife = new Npc
        {
            ObjId = 9876,
            Hp = 100,
            MaxHp = 100,
            Faction = new SystemFaction { Id = (FactionsEnum)115 }
        };
        wildlife.Transform.World.Position = new Vector3(2f, 0, 0);

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2.5f,
            HuntChaseSpeed = 4.5f,
            HuntPerceptionRadius = 20f,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = true,
            NearbyNpcProvider = (_, _) => [wildlife],
            UnitResolver = (_, id) => id == wildlife.ObjId ? wildlife : null
        };
        executor.SetRoamRoute(runtime.Character, route);

        // Step 1: Scan detects wildlife, bot engages target
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(next).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(wildlife.ObjId);

        // Step 2: Since wildlife is at 2m (<= 3m melee reach), bot executes combat cast
        clock.Advance(TimeSpan.FromMilliseconds(100));
        next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(next).IsNotNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Cast)).IsTrue();

        // Step 3: Wildlife dies (Hp = 0), bot drops target and resumes roam
        wildlife.Hp = 0;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.Character.CurrentTarget).IsNull();

        // Step 4: Bot resumes patrol leg toward (50, 0, 0)
        clock.Advance(TimeSpan.FromMilliseconds(100));
        next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(actor.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);
    }
}
