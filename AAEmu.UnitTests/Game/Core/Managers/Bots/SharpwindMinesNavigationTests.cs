using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PB-001 tests for Sharpwind Mines (Cuttingwind Deadmines) 3D interior tunnel corridor navigation,
/// covering .path and .json route loading, 3D elevation continuity, obstacle avoidance detours
/// through dungeon hazards, and full corridor traversal.
/// </summary>
[NotInParallel]
public class SharpwindMinesNavigationTests
{
    [Before(Test)]
    public void SetUp()
    {
        DevMapperService.Instance.RoutesDirectory = Path.Combine("Data", "Routes");
        DevMapperService.Instance.PathsDirectory = Path.Combine("Data", "Path");
        ObstacleManager.Instance.Clear();
        ObstacleManager.Instance.DataDirectory = Path.Combine("Data", "Navigation");
    }

    [After(Test)]
    public void Cleanup()
    {
        ObstacleManager.Instance.Clear();
    }

    [Test]
    public async Task AiPathsManager_LoadsSharpwindMinesPath_WithFull3DCorridor()
    {
        var points = AiPathsManager.Instance.LoadAiPathPoints("dungeon_sharpwind_mines");

        await Assert.That(points).IsNotNull();
        await Assert.That(points.Count).IsGreaterThanOrEqualTo(200);

        // Entrance starts at ~(718.7, 329.9, 168.3)
        var start = points.First().Position;
        await Assert.That(Vector3.Distance(start, new Vector3(718.7f, 329.9f, 168.3f))).IsLessThan(1.0f);

        // Iron bridge crosses at ~(679.0, 326.5, 166.9)
        var nearBridge = points.Any(p => Vector3.Distance(p.Position, new Vector3(679.0f, 326.5f, 166.9f)) < 3.5f);
        await Assert.That(nearBridge).IsTrue();

        // Boss 1: Wera at ~(503.4, 327.6, 166.0)
        var nearWera = points.Any(p => Vector3.Distance(p.Position, new Vector3(503.4f, 327.6f, 166.0f)) < 3.5f);
        await Assert.That(nearWera).IsTrue();

        // Boss 2: Ogre at ~(547.3, 409.6, 153.9)
        var nearOgre = points.Any(p => Vector3.Distance(p.Position, new Vector3(547.3f, 409.6f, 153.9f)) < 3.5f);
        await Assert.That(nearOgre).IsTrue();

        // Boss 3: Okaphe at ~(611.3, 642.1, 140.0)
        var end = points.Last().Position;
        await Assert.That(Vector3.Distance(end, new Vector3(611.3f, 642.1f, 140.0f))).IsLessThan(1.0f);

        // All points have valid finite 3D coordinates and smooth spacing (<= 3.5m)
        for (var i = 1; i < points.Count; i++)
        {
            var p1 = points[i - 1].Position;
            var p2 = points[i].Position;
            var stepDist = Vector3.Distance(p1, p2);
            await Assert.That(float.IsFinite(p2.X) && float.IsFinite(p2.Y) && float.IsFinite(p2.Z)).IsTrue();
            await Assert.That(stepDist).IsLessThanOrEqualTo(3.5f);
            await Assert.That(p2.Z).IsGreaterThanOrEqualTo(135.0f);
            await Assert.That(p2.Z).IsLessThanOrEqualTo(175.0f);
        }
    }

    [Test]
    public async Task DevMapperService_LoadsSharpwindMinesRoute_WithNamedBossWaypoints()
    {
        var route = DevMapperService.Instance.GetRoute("dungeon_sharpwind_mines");

        await Assert.That(route).IsNotNull();
        await Assert.That(route!.RouteName).IsEqualTo("dungeon_sharpwind_mines");
        await Assert.That(route.TotalDistance).IsGreaterThan(500f);
        await Assert.That(route.WaypointCount).IsGreaterThanOrEqualTo(200);

        var labels = route.Actions.Where(a => !string.IsNullOrEmpty(a.Label)).Select(a => a.Label).ToList();
        await Assert.That(labels).Contains("entrance");
        await Assert.That(labels).Contains("iron_bridge");
        await Assert.That(labels).Contains("boss_wera");
        await Assert.That(labels).Contains("boss_ogre");
        await Assert.That(labels).Contains("hazard_powder_kegs");
        await Assert.That(labels).Contains("boss_okaphe");
    }

    [Test]
    public async Task ObstacleManager_LoadsCuttingwindDeadmines_IndexesDungeonHazards()
    {
        ObstacleManager.Instance.Clear();
        var catalogPath = Path.Combine("Data", "Navigation", "cuttingwind_deadmine_obstacles.json");
        ObstacleManager.Instance.LoadObstaclesFromFile(catalogPath);

        await Assert.That(ObstacleManager.Instance.TotalObstacleCount).IsEqualTo(14);

        // Lion Statue (5602) at (489.65, 323.05, 168.0)
        var nearbyStatue = ObstacleManager.Instance.GetNearbyObstacles(new Vector3(489.65f, 323.05f, 168.0f), 1.0f);
        await Assert.That(nearbyStatue.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(nearbyStatue[0].TemplateId).IsEqualTo(5602u);

        // Powder kegs in cavern corridor near (617, 460, 149.3)
        var nearbyKegs = ObstacleManager.Instance.GetNearbyObstacles(new Vector3(617.2f, 460.3f, 149.3f), 2.0f);
        await Assert.That(nearbyKegs.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(nearbyKegs.Any(k => k.TemplateId == 5282u)).IsTrue();
    }

    [Test]
    public async Task ObstacleDetour_AroundPowderKegHazard_ComputesValidBypass()
    {
        ObstacleManager.Instance.Clear();
        var catalogPath = Path.Combine("Data", "Navigation", "cuttingwind_deadmine_obstacles.json");
        ObstacleManager.Instance.LoadObstaclesFromFile(catalogPath);

        // Travel segment directly traversing the powder keg obstacle at (617.1, 458.9, 149.3)
        var from = new Vector3(617.1f, 450.0f, 149.3f);
        var to = new Vector3(617.1f, 468.0f, 149.3f);

        var intersects = ObstacleManager.Instance.IntersectsObstacle(from, to);
        await Assert.That(intersects).IsTrue();

        var detour = ObstacleManager.Instance.FindDetour(from, to, margin: 1.0f);
        await Assert.That(detour.Count).IsGreaterThanOrEqualTo(2);

        // Destination reached
        var last = detour.Last();
        await Assert.That(last.X).IsEqualTo(to.X);
        await Assert.That(last.Y).IsEqualTo(to.Y);

        // Bypass waypoint is safe (not blocked)
        foreach (var wp in detour.Take(detour.Count - 1))
        {
            var blocked = ObstacleManager.Instance.IsBlocked(wp);
            await Assert.That(blocked).IsFalse();
        }
    }

    [Test]
    public async Task GameplayActor_NavigatesSharpwindMinesCorridor_EndToEndWithoutStuck()
    {
        ObstacleManager.Instance.Clear();
        var (actor, session) = GameplayActorTestRig.CreateActor("sharpwind-interior-runner");
        session.World.Template.GeoData = null;
        actor.BroadcastMovement = false;

        // Start at dungeon entrance
        var entrance = new Vector3(718.7f, 329.9f, 168.3f);
        GameplayActorTestRig.SetPosition(actor, entrance);

        // Milestones through the 3D corridor:
        var milestones = new[]
        {
            new Vector3(679.0f, 326.5f, 166.9f), // Iron Bridge
            new Vector3(503.4f, 327.6f, 166.0f), // Boss 1 (Wera)
            new Vector3(547.3f, 409.6f, 153.9f), // Boss 2 (Ogre)
            new Vector3(611.3f, 642.1f, 140.0f)  // Boss 3 (Okaphe)
        };

        foreach (var milestone in milestones)
        {
            var req = actor.NavigateTo(milestone, speed: 10f);
            await Assert.That(req.State).IsEqualTo(ActorLifecycleState.Running);

            var guard = 0;
            while (req.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 200)
            {
                actor.Tick(TimeSpan.FromSeconds(1));
            }

            await Assert.That(req.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(req.Detail).IsEqualTo("arrived");
            await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, milestone)).IsLessThan(0.01f);
        }
    }
}
