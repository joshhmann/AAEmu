using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Services.WebApi.Models;
using Microsoft.Extensions.DependencyInjection;
using NetCoreServer;
using NLog;

namespace AAEmu.Game.Services.WebApi.Controllers;

/// <summary>
/// Bot control API (P1 t_2ea94a20) — programmatic management surface over
/// the SAME BotAdminService core the /bot GM commands use (one control core,
/// two frontends): list / add / remove / relocate / status.
///
/// POSTURE: disabled by default — every route returns 404 unless the API is
/// explicitly enabled (AAEMU_BOT_CTRL=1 env or Config "Bots"."EnableBotControl"),
/// and every request must carry the shared secret in the X-Auth-Token header
/// (AAEMU_BOT_CTRL_TOKEN env — the env-secret contract). All mutations run
/// inside the game process on the normal bot manager + lifecycle — no
/// parallel bot path, single execution boundary (M5 A1).
/// </summary>
internal class BotControlController : BaseController
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [WebApiGet("^/api/bots$")]
    public HttpResponse List(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var bots = BotAdminService.FromContainer().ListStatus();
            return OkJson(new BotControlResponse(true, $"{bots.Count} registered", bots));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: list failed");
        }
    }

    [WebApiGet("^/api/bots/status$")]
    public HttpResponse Status(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var bots = BotAdminService.FromContainer().ListStatus();
            var active = bots.Count(b => b.State == PlayerBotState.Active.ToString());
            return OkJson(new BotControlResponse(true, $"{bots.Count} registered ({active} active)", bots));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: status failed");
        }
    }

    [WebApiPost("^/api/bots$")]
    public HttpResponse Add(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<AddBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Name))
                return BadRequestJson(new ErrorModel("name is required"));

            var home = body.X.HasValue && body.Y.HasValue && body.Z.HasValue
                ? new Vector3(body.X.Value, body.Y.Value, body.Z.Value)
                : (Vector3?)null;
            var result = BotAdminService.FromContainer().Add(body.Name, home);
            return OkJson(new BotControlResponse(result.Success, result.Message));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: add failed");
        }
    }

    [WebApiPost("^/api/bots/remove$")]
    public HttpResponse Remove(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<RemoveBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.NameOrId))
                return BadRequestJson(new ErrorModel("nameOrId is required"));

            var result = BotAdminService.FromContainer().Remove(body.NameOrId);
            return OkJson(new BotControlResponse(result.Success, result.Message));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: remove failed");
        }
    }

    [WebApiPost("^/api/bots/relocate$")]
    public HttpResponse Relocate(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<RelocateBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.NameOrId))
                return BadRequestJson(new ErrorModel("nameOrId is required"));
            if (!body.X.HasValue || !body.Y.HasValue || !body.Z.HasValue)
                return BadRequestJson(new ErrorModel("x, y and z are required"));

            var result = BotAdminService.FromContainer().Go(
                body.NameOrId, new Vector3(body.X.Value, body.Y.Value, body.Z.Value));
            return OkJson(new BotControlResponse(result.Success, result.Message));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: relocate failed");
        }
    }

    /// <summary>
    /// Operator assignment plane (plan T1): hand ONE running bot a bounded
    /// activity — today <c>assigned.travel</c>, one destination and one expiry.
    /// The assignment lands in the ONE registry that the arbitration band, the
    /// executor's travel leg, the brain read-back and the E2E bridge all share;
    /// the arbiter elects it on the next wake, so the assignment band preempts
    /// the bot's ordinary activity instead of competing with it. Same gate as
    /// the rest of this surface (disabled by default, token required).
    /// Body: {"nameOrId":"Bot","activity":"assigned.travel","x":..,"y":..,"z":..,"expirySeconds":600}
    /// </summary>
    [WebApiPost("^/api/bots/assign$")]
    public HttpResponse Assign(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<AssignBotRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.NameOrId))
                return BadRequestJson(new ErrorModel("nameOrId is required"));
            if (string.IsNullOrWhiteSpace(body.Activity))
                return BadRequestJson(new ErrorModel("activity is required"));
            if (!body.X.HasValue || !body.Y.HasValue || !body.Z.HasValue)
                return BadRequestJson(new ErrorModel("x, y and z are required"));

            var manager = SingletonContainer.ServiceProvider?.GetService<IPlayerBotManager>();
            var registry = SingletonContainer.ServiceProvider?.GetService<IBotAssignmentRegistry>();
            if (manager == null || registry == null)
                return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    new ErrorModel("bot manager/assignment registry unavailable in DI"));
            if (!TryResolveBot(manager, body.NameOrId, out var runtime) || runtime == null)
                return JsonResponse(HttpStatusCode.NotFound, new ErrorModel($"unknown bot '{body.NameOrId}'"));

            var expiry = TimeSpan.FromSeconds(body.ExpirySeconds is > 0
                ? body.ExpirySeconds.Value
                : BotAssignmentRegistry.DefaultExpirySeconds);
            var assignment = registry.Accept(
                runtime.CharacterId, body.Activity, body.X.Value, body.Y.Value, body.Z.Value,
                expiry, "operator-api");
            if (assignment == null)
                return BadRequestJson(new ErrorModel(
                    $"unsupported activity '{body.Activity}' (supported: {BotAssignmentActivities.Travel})"));

            Logger.Info("BotControl: assignment accepted for {Bot} ({CharacterId}) — {Activity} to ({X:F0},{Y:F0},{Z:F0}) until {Expires:O}",
                runtime.Character.Name, runtime.CharacterId, assignment.Activity,
                assignment.X, assignment.Y, assignment.Z, assignment.ExpiresUtc);
            return OkJson(new BotAssignmentResponse(true,
                $"assignment accepted for {runtime.Character.Name}: {assignment.Activity} until {assignment.ExpiresUtc:O}",
                assignment));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: assign failed");
        }
    }

    /// <summary>
    /// A1 stop owner: disarm the bot host through the ONE production owner —
    /// the scheduler every arming path (presence bootstrap, proximity
    /// bootstrap, admin ArmRoam, control host) already starts. It asks every
    /// enrolled bot to leave through the ordinary manager/leave path while the
    /// host is still running (native retirement needs the running execution
    /// boundary), then stops the scheduler, drops and COUNTS the queued steps
    /// the stopped boundary can no longer run (releasing their leases), and
    /// re-checks for bots that landed un-safely-deactivated, so a stopped host
    /// never leaves a bot Active with its native work abandoned.
    /// Idempotent; same gate as the rest of this surface (disabled by default,
    /// token required). Body may be empty; optional {"reason": "..."}.
    /// </summary>
    [WebApiPost("^/api/bots/host/stop$")]
    public HttpResponse StopHost(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var scheduler = SingletonContainer.ServiceProvider?.GetService<IPlayerBotScheduler>();
            if (scheduler == null)
                return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    new ErrorModel("bot scheduler unavailable in DI"));

            var body = Deserialize<StopBotHostRequest>(request);
            var reason = string.IsNullOrWhiteSpace(body?.Reason) ? "api-host-stop" : body.Reason.Trim();
            var report = scheduler.Disarm(reason);
            Logger.Info("BotControl: host stop ({Reason}) — {Report}", reason, report);
            return OkJson(new BotHostStopResponse(report.Converged, report.ToString(), report));
        }
        catch (JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: host stop failed");
        }
    }

    /// <summary>
    /// Brain Inspector fleet view (Phase 1 diagnostics): state/leg counts
    /// over all registered bots plus the already-available scheduler metrics
    /// passthrough. Read-only: flag reads + metric snapshot, no wakes, no
    /// mutations. Same gate as the rest of this surface (disabled by default,
    /// token required).
    /// </summary>
    [WebApiGet("^/api/bots/brain$")]
    public HttpResponse BrainFleet(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var sp = SingletonContainer.ServiceProvider;
            var manager = sp?.GetService<IPlayerBotManager>();
            var scheduler = sp?.GetService<IPlayerBotScheduler>();
            var executor = sp?.GetService<BotRoamStepExecutor>();
            if (manager == null || scheduler == null || executor == null)
                return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    new ErrorModel("bot scheduler/registry unavailable in DI"));
            var inputs = new List<BotBrainProjection.BotFleetInput>();
            foreach (var runtime in manager.GetAll())
            {
                var state = executor.GetBotState(runtime.CharacterId);
                executor.TryGetQuestRuntime(runtime.CharacterId, out var quest);
                var character = state?.Actor.Character ?? runtime.Character;
                inputs.Add(new BotBrainProjection.BotFleetInput(
                    runtime.State == PlayerBotState.Active,
                    state?.QuestLegActive ?? false,
                    state?.NeedsLegActive ?? false,
                    state?.NeedsFarmPhase ?? BotRoamStepExecutor.NeedsFarmLoopPhase.Idle,
                    (state?.TargetNpcObjId ?? 0) != 0
                        || (state?.TargetPlayerObjId ?? 0) != 0
                        || (state?.TargetButcherDoodadObjId ?? 0) != 0
                        || character.CurrentTarget != null,
                    state?.Path is { IsFinished: false },
                    quest?.Status == BotBehaviorStatus.Blocked));
            }
            return OkJson(BotBrainProjection.CaptureFleet(inputs, scheduler.GetMetrics()));
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: brain fleet failed");
        }
    }

    /// <summary>
    /// Brain Inspector per-bot view (Phase 1 diagnostics): one bounded
    /// read-only projection over production-owned state (wake counter,
    /// behavior runtime, live actor request + audit trace, leg/travel flags).
    /// The dashboard renders this verbatim — it never decides, owns state,
    /// mutates Character, or re-reads Character to infer behavior. Unknown
    /// bot → 404 JSON (never an empty projection).
    /// </summary>
    [WebApiGet("^/api/bots/brain/([^/]+)$")]
    public HttpResponse BrainInspect(HttpRequest request, MatchCollection matches)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var nameOrId = matches[0].Groups[1].Value;
            var sp = SingletonContainer.ServiceProvider;
            var manager = sp?.GetService<IPlayerBotManager>();
            var executor = sp?.GetService<BotRoamStepExecutor>();
            if (manager == null || executor == null)
                return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    new ErrorModel("bot scheduler/registry unavailable in DI"));
            if (!TryResolveBot(manager, nameOrId, out var runtime) || runtime == null)
                return JsonResponse(HttpStatusCode.NotFound,
                    new ErrorModel($"unknown bot '{nameOrId}'"));
            var state = executor.GetBotState(runtime.CharacterId);
            executor.TryGetQuestRuntime(runtime.CharacterId, out var quest);
            var decorator = sp?.GetService<BotGoalArbiterStepExecutor>();
            var arbiter = sp?.GetService<IBotGoalArbiter>();
            var acting = state?.Actor.Character ?? runtime.Character;
            var pos = acting.Transform.World.Position;
            var target = acting.CurrentTarget;
            var travel = state?.QuestTravelTarget;
            // Operator assignment read-back (plan T1): the last assignment the
            // registry recorded for this bot — live or already ended, with the
            // reason it ended. No second inspection route.
            var assignment = sp?.GetService<IBotAssignmentRegistry>() is { } registry
                && registry.TryGet(runtime.CharacterId, out var recorded)
                    ? recorded
                    : null;
            return OkJson(BotBrainProjection.Capture(
                runtime.CharacterId,
                runtime.Character.Name,
                pos.X, pos.Y, pos.Z,
                acting.Transform.ZoneId,
                acting.ParentWorld?.Id ?? 0u,
                acting.Transform.InstanceId,
                target?.ObjId ?? 0u,
                target?.GetType().Name,
                decorator?.GetWakeSequence(runtime.CharacterId),
                arbiter?.GetActiveActivity(runtime.CharacterId),
                quest,
                state?.Actor.ActiveRequest,
                state?.Actor.AuditTrace,
                state?.QuestLegActive ?? false,
                travel?.X, travel?.Y, travel?.Z,
                state?.QuestTravelReason,
                assignment: assignment,
                // Operator decision read-back (plan T7): the last arbitration
                // decision this decorator saw — outcome, elected activity, the
                // arbiter's own refusal reason and the counts — on the same
                // surface that already carries the assignment read-back.
                decision: decorator?.GetLastDecision(runtime.CharacterId)) with
            {
                GameplayProgress = BotQuestLoopObservation.Capture(acting, state?.QuestLegActive ?? false),
            });
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: brain inspect failed");
        }
    }

    /// Brain tuning read (GET /api/bots/tuning): every tunable with its
    /// canonical default, current effective value, override (null = default
    /// wins), previous value, revision, and when last observed. Read-only:
    /// no wakes, no mutations. Same gate as the rest of this surface.
    /// </summary>
    [WebApiGet("^/api/bots/tuning$")]
    public HttpResponse BrainTuningGet(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            return OkJson(BotBrainTuningStore.Snapshot());
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: brain tuning get failed");
        }
    }

    /// <summary>
    /// Brain tuning write (POST /api/bots/tuning): partial update — only the
    /// JSON body's present fields change; explicit null clears that override
    /// back to default. Invalid values → 400 with the bound, never silently
    /// accepted. Applies next-wake; Login/Game/bot/quest state untouched.
    /// </summary>
    [WebApiPost("^/api/bots/tuning$")]
    public HttpResponse BrainTuningPost(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            BotBrainTuningPatch patch;
            try
            {
                patch = BotBrainTuningStore.ParsePatch(request.Body ?? "{}");
            }
            catch (JsonException)
            {
                return BadRequestJson(new ErrorModel("Invalid JSON body"));
            }
            var error = BotBrainTuning.Validate(patch);
            if (error != null)
                return BadRequestJson(new ErrorModel(error));
            var (tuning, revision, changed) = BotBrainTuningStore.Apply(patch);
            Logger.Info("BotControl: brain tuning {Changed} to revision {Revision} (safe={Safe}, unresolved={Unresolved}, planFail={PlanFail}, stall={Stall}, leash={Leash})",
                changed ? "updated" : "no-op", revision,
                tuning.LootSafeRadiusM?.ToString() ?? "default",
                tuning.GiveUpAfterUnresolvedWakes?.ToString() ?? "default",
                tuning.GiveUpAfterPlanFailures?.ToString() ?? "default",
                tuning.CombatStallWakes?.ToString() ?? "default",
                tuning.LeashEdgeFraction?.ToString() ?? "default");
            return OkJson(BotBrainTuningStore.Snapshot());
        }
        catch (Exception ex)
        {
            return Error(ex, "BotControl: brain tuning post failed");
        }
    }

    /// <summary>Resolves a bot by numeric id or case-insensitive name (the BotAdminService precedent).</summary>
    private static bool TryResolveBot(IPlayerBotManager manager, string nameOrId, out PlayerBotRuntime? runtime)
    {
        if (uint.TryParse(nameOrId, out var id) && manager.TryGet(id, out runtime) && runtime != null)
            return true;
        runtime = manager.GetAll().FirstOrDefault(r =>
            r.Character.Name.Equals(nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));
        return runtime != null;
    }


    /// <summary>Gate: null when authorized, otherwise the error response to return.</summary>
    private static HttpResponse? CheckGate(HttpRequest request)
    {
        if (!BotControlSettings.IsEnabled())
            return JsonResponse(HttpStatusCode.NotFound, new ErrorModel("Bot control API is disabled"));

        if (!BotControlSettings.TokenMatches(GetHeader(request, "X-Auth-Token")))
            return JsonResponse(HttpStatusCode.Unauthorized, new ErrorModel("Missing or invalid X-Auth-Token"));

        return null;
    }

    /// <summary>Case-insensitive header lookup (NetCoreServer stores headers as an indexed list).</summary>
    private static string GetHeader(HttpRequest request, string name)
    {
        for (var i = 0; i < request.Headers; i++)
        {
            var (key, value) = request.Header(i);
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return string.Empty;
    }

    private static T? Deserialize<T>(HttpRequest request)
        => string.IsNullOrWhiteSpace(request.Body)
            ? default
            : JsonSerializer.Deserialize<T>(request.Body, JsonOpts);

    private static HttpResponse Error(Exception ex, string logMessage)
    {
        Logger.Error(ex, logMessage);
        return JsonResponse(HttpStatusCode.InternalServerError, new ErrorModel(ex.Message));
    }
}
