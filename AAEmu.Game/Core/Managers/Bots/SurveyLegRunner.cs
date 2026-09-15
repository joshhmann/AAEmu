using System.Numerics;
using System.Text.Json;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// One surveyed leg: walk outcome plus the sensed points along it.
/// </summary>
public sealed record SurveyLegResult(
    string RouteName,
    bool WalkSucceeded,
    string WalkDetail,
    IReadOnlyList<SurveyPoint> Points,
    string? RouteJsonPath,
    string? SurveyJsonPath);

/// <summary>
/// Survey-leg harness: record + walk + sense + stop for a single road leg.
/// Preconditions (caller-owned): the bot is already in the target world and
/// instance, near <paramref name="from"/>. Route files are world-implicit —
/// cross-zone guides must run one leg per zone, never one file across zones.
/// Baked-coarse Z must never reach this runner; both endpoints arrive either
/// bot-measured or clamped through the probe delegate below.
/// </summary>
public static class SurveyLegRunner
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Endpoint clamp: probe authoritative terrain for the TARGET zone, keep
    /// the input Z when the probe returns 0 (no data — never fabricate).
    /// Live callers pass
    /// <c>(p, zone) => WorldManager.GetReferenceHeight(null, p.X, p.Y, p.Z, zone)</c>.
    /// </summary>
    public static Vector3 ClampEndpoint(Vector3 input, uint zoneId, Func<Vector3, uint, float> probe)
    {
        float sampled;
        try
        {
            sampled = probe(input, zoneId);
        }
        catch
        {
            return input;
        }
        if (sampled == 0f)
            return input;
        return MathF.Abs(sampled - input.Z) > 0.05f
            ? new Vector3(input.X, input.Y, sampled)
            : input;
    }

    public static SurveyLegResult Run(
        IGameplayActor actor,
        string routeName,
        Vector3 from,
        Vector3 to,
        Func<Vector3, uint, float> clampHeight,
        Action<SurveyPoint>? onPoint = null,
        float senseEveryM = 5f,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var character = actor.Character;
        var zoneId = character.Transform.ZoneId;
        var start = ClampEndpoint(from, zoneId, clampHeight);
        var target = ClampEndpoint(to, zoneId, clampHeight);

        var session = DevMapperService.Instance.Start(character, routeName);
        if (!session.Success)
            return new SurveyLegResult(routeName, false, session.Message, [], null, null);

        var senses = new BotSurveySenses(actor);
        var points = new List<SurveyPoint>();
        var lastSense = character.Transform.World.Position;

        var budget = timeout ?? TimeSpan.FromSeconds(15);
        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        var request = actor.NavigateTo(target, 5f, budget);
        while (!request.IsTerminal && Environment.TickCount64 < deadline)
        {
            actor.Tick(TimeSpan.FromMilliseconds(50));
            var pos = character.Transform.World.Position;
            var dx = pos.X - lastSense.X;
            var dy = pos.Y - lastSense.Y;
            if (dx * dx + dy * dy >= senseEveryM * senseEveryM)
            {
                try
                {
                    var point = senses.SenseWaypoint();
                    points.Add(point);
                    onPoint?.Invoke(point);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"[Survey] Sense failed at ({pos.X:F1}, {pos.Y:F1}) — walk continues.");
                }
                lastSense = pos;
            }
            Thread.Sleep(5);
        }

        var stop = DevMapperService.Instance.Stop(character.Id);
        var succeeded = request.State == ActorLifecycleState.Completed;
        var surveyPath = WriteSurveyArtifact(stop.RouteName, points);

        Logger.Info($"[Survey] Leg '{stop.RouteName}': walk {(succeeded ? "VALIDATED" : "REFUTED")} " +
                    $"({request.State}: {request.Detail}), {points.Count} sensed points.");
        return new SurveyLegResult(stop.RouteName, succeeded,
            $"{request.State}: {request.Detail}", points, stop.JsonPath, surveyPath);
    }

    private static string? WriteSurveyArtifact(string routeName, List<SurveyPoint> points)
    {
        try
        {
            Directory.CreateDirectory(DevMapperService.Instance.RoutesDirectory);
            var path = Path.Combine(DevMapperService.Instance.RoutesDirectory, $"{routeName}.survey.json");
            File.WriteAllText(path, JsonSerializer.Serialize(points,
                new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"[Survey] Failed to write survey artifact for '{routeName}'.");
            return null;
        }
    }
}
