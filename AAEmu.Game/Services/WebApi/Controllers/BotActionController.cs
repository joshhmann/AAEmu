using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Services.WebApi.Models;

using NetCoreServer;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

using NLog;

namespace AAEmu.Game.Services.WebApi.Controllers;

/// <summary>
/// Bot control-plane ACTION API (M5 stage 3, t_7b6d7a4b) — the contract-backed
/// surface that formalizes BotDriveBridge semantics. Endpoints map 1:1 to the
/// M5 CONTRACT ACTIONS (t_659f891f / IGameplayActor): observe, move, interact,
/// accept_quest, turn_in, loot, use_item, mount, … every request is a
/// VALIDATED action with the full lifecycle, and trace exposes the audit
/// trail.
///
/// Contract rules (this surface):
///  - ENQUEUE-ONLY: POSTs accept commands into the lifecycle queue and return
///    a trace id immediately; the caller polls GET /api/actors/actions/{id}
///    for lifecycle transitions. Execution happens on the A1 execution
///    boundary (game-loop thread) — API threads NEVER touch a Character,
///    an actor or the world (no Character access, no packet fabrication, no
///    DB reads — spec §8).
///  - TOKEN AUTH: same gate as the management surface — disabled by default
///    (AAEMU_BOT_CTRL=1/true or Config Bots.EnableBotControl), every request
///    needs the shared secret in X-Auth-Token (AAEMU_BOT_CTRL_TOKEN).
///  - NO ADMIN VERBS: no GM ops, no bot add/remove/list/relocate — those
///    stay on /api/bots (t_2ea94a20). This surface only drives registered,
///    active bots.
///  - CRASH ISOLATION: no world locks held by clients; a disconnected caller
///    leaves only a queued command that completes or times out server-side.
/// </summary>
internal class BotActionController : BaseController
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerSettings ResultSettings = new()
    {
        Converters = [new StringEnumConverter()]
    };

    private static BotActionCommandQueue Queue => BotActionCommandQueue.FromContainer();

    // ------------------------------------------------------------- actions

    [WebApiPost("^/api/actors/observe$")]
    public HttpResponse Observe(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<ObserveRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            return EnqueueResponse(body.Bot, new BotActionSpec(BotActionKind.Observe));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "observe failed");
        }
    }

    [WebApiPost("^/api/actors/move$")]
    public HttpResponse Move(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<MoveRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.X.HasValue || !body.Y.HasValue || !body.Z.HasValue)
                return BadRequestJson(new ErrorModel("x, y and z are required"));
            if (!float.IsFinite(body.X.Value) || !float.IsFinite(body.Y.Value) || !float.IsFinite(body.Z.Value))
                return BadRequestJson(new ErrorModel("x, y and z must be finite"));

            var spec = new BotActionSpec(
                BotActionKind.Move,
                Destination: new System.Numerics.Vector3(body.X.Value, body.Y.Value, body.Z.Value),
                Timeout: TimeoutOrNull(body.TimeoutSec),
                IdempotencyKey: body.IdempotencyKey,
                Payload: new MoveActionParams(body.Speed ?? 5f));
            return EnqueueResponse(body.Bot, spec);
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "move failed");
        }
    }

    [WebApiPost("^/api/actors/move_to_unit$")]
    public HttpResponse MoveToUnit(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<MoveToUnitRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.TargetObjId.HasValue || body.TargetObjId.Value == 0)
                return BadRequestJson(new ErrorModel("targetObjId is required"));

            var spec = new BotActionSpec(
                BotActionKind.MoveToUnit,
                TargetId: body.TargetObjId.Value,
                Timeout: TimeoutOrNull(body.TimeoutSec),
                IdempotencyKey: body.IdempotencyKey,
                Payload: new MoveActionParams(body.Speed ?? 5f));
            return EnqueueResponse(body.Bot, spec);
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "move_to_unit failed");
        }
    }

    [WebApiPost("^/api/actors/stop$")]
    public HttpResponse Stop(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<StopRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            return EnqueueResponse(body.Bot, new BotActionSpec(BotActionKind.Stop));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "stop failed");
        }
    }

    [WebApiPost("^/api/actors/target$")]
    public HttpResponse Target(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<TargetRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.TargetObjId.HasValue || body.TargetObjId.Value == 0)
                return BadRequestJson(new ErrorModel("targetObjId is required"));
            return EnqueueResponse(body.Bot, new BotActionSpec(BotActionKind.Target, TargetId: body.TargetObjId.Value));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "target failed");
        }
    }

    [WebApiPost("^/api/actors/cast$")]
    public HttpResponse Cast(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<CastRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.SkillId.HasValue || body.SkillId.Value == 0)
                return BadRequestJson(new ErrorModel("skillId is required"));
            if (!body.TargetObjId.HasValue || body.TargetObjId.Value == 0)
                return BadRequestJson(new ErrorModel("targetObjId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.Cast, TargetId: body.TargetObjId.Value, SkillId: body.SkillId.Value,
                    IdempotencyKey: body.IdempotencyKey));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "cast failed");
        }
    }

    [WebApiPost("^/api/actors/interact$")]
    public HttpResponse Interact(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<InteractRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.DoodadObjId.HasValue || body.DoodadObjId.Value == 0)
                return BadRequestJson(new ErrorModel("doodadObjId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.Interact, TargetId: body.DoodadObjId.Value,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new InteractActionParams(body.SkillId ?? 0)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "interact failed");
        }
    }

    [WebApiPost("^/api/actors/loot$")]
    public HttpResponse Loot(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<LootRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.LootOwnerObjId.HasValue || body.LootOwnerObjId.Value == 0)
                return BadRequestJson(new ErrorModel("lootOwnerObjId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.Loot, TargetId: body.LootOwnerObjId.Value,
                    IdempotencyKey: body.IdempotencyKey));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "loot failed");
        }
    }

    [WebApiPost("^/api/actors/use_item$")]
    public HttpResponse UseItem(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<UseItemRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.ItemTemplateId.HasValue || body.ItemTemplateId.Value == 0)
                return BadRequestJson(new ErrorModel("itemTemplateId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.UseItem, TargetId: body.ItemTemplateId.Value,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new ItemUseActionParams(body.TargetObjId ?? 0)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "use_item failed");
        }
    }

    [WebApiPost("^/api/actors/mount$")]
    public HttpResponse Mount(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<MountRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.MateObjId.HasValue || body.MateObjId.Value == 0)
                return BadRequestJson(new ErrorModel("mateObjId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.Mount, TargetId: body.MateObjId.Value,
                    IdempotencyKey: body.IdempotencyKey));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "mount failed");
        }
    }

    [WebApiPost("^/api/actors/dismount$")]
    public HttpResponse Dismount(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<DismountRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.Dismount,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new DismountActionParams(body.MateObjId ?? 0)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "dismount failed");
        }
    }

    [WebApiPost("^/api/actors/accept_quest$")]
    public HttpResponse AcceptQuest(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<AcceptQuestRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.QuestId.HasValue || body.QuestId.Value == 0)
                return BadRequestJson(new ErrorModel("questId is required"));
            if (string.IsNullOrWhiteSpace(body.AcceptorType))
                return BadRequestJson(new ErrorModel("acceptorType is required (Npc/Doodad/Sphere/Item/Skill/Buff/Kill)"));
            if (!Enum.TryParse<QuestAcceptorType>(body.AcceptorType, true, out var acceptorType) || acceptorType == QuestAcceptorType.Unknown)
                return BadRequestJson(new ErrorModel($"unknown acceptorType '{body.AcceptorType}'"));
            if (!body.AcceptorId.HasValue)
                return BadRequestJson(new ErrorModel("acceptorId is required"));

            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.AcceptQuest, TargetId: body.QuestId.Value,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new QuestAcceptParams(acceptorType, body.AcceptorId.Value)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "accept_quest failed");
        }
    }

    [WebApiPost("^/api/actors/advance_quest$")]
    public HttpResponse AdvanceQuest(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<AdvanceQuestRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.QuestId.HasValue || body.QuestId.Value == 0)
                return BadRequestJson(new ErrorModel("questId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.AdvanceQuest, TargetId: body.QuestId.Value,
                    IdempotencyKey: body.IdempotencyKey));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "advance_quest failed");
        }
    }

    [WebApiPost("^/api/actors/turn_in_quest$")]
    public HttpResponse TurnInQuest(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<TurnInQuestRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.QuestId.HasValue || body.QuestId.Value == 0)
                return BadRequestJson(new ErrorModel("questId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.TurnInQuest, TargetId: body.QuestId.Value,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new QuestTurnInParams(body.NpcObjId ?? 0, body.SelectedReward ?? -1)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "turn_in_quest failed");
        }
    }

    [WebApiPost("^/api/actors/turn_in_doodad$")]
    public HttpResponse TurnInDoodad(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<TurnInDoodadRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.QuestId.HasValue || body.QuestId.Value == 0)
                return BadRequestJson(new ErrorModel("questId is required"));
            if (!body.DoodadObjId.HasValue || body.DoodadObjId.Value == 0)
                return BadRequestJson(new ErrorModel("doodadObjId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.TurnInDoodad, TargetId: body.QuestId.Value,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new QuestTurnInParams(body.DoodadObjId.Value, body.SelectedReward ?? -1)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "turn_in_doodad failed");
        }
    }

    [WebApiPost("^/api/actors/auto_turn_in$")]
    public HttpResponse AutoTurnIn(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<AutoTurnInRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (!body.QuestId.HasValue || body.QuestId.Value == 0)
                return BadRequestJson(new ErrorModel("questId is required"));
            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.AutoTurnIn, TargetId: body.QuestId.Value,
                    IdempotencyKey: body.IdempotencyKey,
                    Payload: new QuestTurnInParams(0, body.SelectedReward ?? -1)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "auto_turn_in failed");
        }
    }

    [WebApiPost("^/api/actors/interrupt$")]
    public HttpResponse Interrupt(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var body = Deserialize<InterruptRequest>(request);
            if (body == null || string.IsNullOrWhiteSpace(body.Bot))
                return BadRequestJson(new ErrorModel("bot is required"));
            if (string.IsNullOrWhiteSpace(body.TraceId))
                return BadRequestJson(new ErrorModel("traceId is required"));
            if (!Guid.TryParse(body.TraceId, out var traceId))
                return BadRequestJson(new ErrorModel("invalid traceId"));

            return EnqueueResponse(body.Bot,
                new BotActionSpec(BotActionKind.Interrupt, Payload: new InterruptActionParams(traceId)));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequestJson(new ErrorModel("Invalid JSON body"));
        }
        catch (Exception ex)
        {
            return Error(ex, "interrupt failed");
        }
    }

    // ------------------------------------------------------------- reads

    /// <summary>Poll one command's lifecycle by its API trace id (async response channel).</summary>
    [WebApiGet("^/api/actors/actions/([0-9a-fA-F-]{36})$")]
    public HttpResponse GetAction(HttpRequest request, MatchCollection matches)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var traceId = matches[0].Groups[1].Value;
            if (!Guid.TryParse(traceId, out var guid))
                return BadRequestJson(new ErrorModel("invalid trace id"));

            if (!Queue.TryGetSnapshot(guid, out var snapshot))
                return JsonResponse(HttpStatusCode.NotFound, new ErrorModel($"no action with trace id {traceId}"));

            return OkJson(BuildActionJson(snapshot));
        }
        catch (Exception ex)
        {
            return Error(ex, "action poll failed");
        }
    }

    /// <summary>Control-plane audit trail for one bot (newest first, bounded).</summary>
    [WebApiGet("^/api/actors/trace(\\?.*)?$")]
    public HttpResponse Trace(HttpRequest request)
    {
        var gate = CheckGate(request);
        if (gate != null)
            return gate;
        try
        {
            var query = ParseQueryString(request.Url);
            var bot = query["bot"];
            if (string.IsNullOrWhiteSpace(bot))
                return BadRequestJson(new ErrorModel("bot query parameter is required (name or character id)"));

            var limit = BotActionCommandQueue.DefaultTraceLimit;
            if (!string.IsNullOrWhiteSpace(query["limit"]) && int.TryParse(query["limit"], out var parsed))
                limit = Math.Clamp(parsed, 1, BotActionCommandQueue.MaxTraceLimit);

            if (!Queue.TryResolveBotId(bot, out var characterId, out _, out var error))
                return JsonResponse(HttpStatusCode.NotFound, new ErrorModel(error));

            var snapshots = Queue.TraceFor(characterId, limit);
            return OkJson(new
            {
                success = true,
                message = $"{snapshots.Count} trace record(s) for '{bot}'",
                trace = snapshots.Select(BuildActionJson)
            });
        }
        catch (Exception ex)
        {
            return Error(ex, "trace failed");
        }
    }

    // ---------------------------------------------------------------- helpers

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
            : System.Text.Json.JsonSerializer.Deserialize<T>(request.Body, JsonOpts);

    private static TimeSpan? TimeoutOrNull(int? timeoutSec)
        => timeoutSec is > 0 ? TimeSpan.FromSeconds(timeoutSec.Value) : null;

    private HttpResponse EnqueueResponse(string bot, BotActionSpec spec)
    {
        var result = Queue.Enqueue(bot, spec);
        if (!result.Ok)
            return JsonResponse(HttpStatusCode.NotFound, new JObject
            {
                ["success"] = false,
                ["message"] = result.Error,
                ["bot"] = bot,
                ["action"] = spec.Kind.ToString()
            });

        // Wire shape (lowercase, mirrors the poll response): the caller
        // polls lifecycle transitions by trace_id — execution is server-side
        // and survives client disconnect.
        return OkJson(new JObject
        {
            ["success"] = true,
            ["message"] = "accepted",
            ["trace_id"] = result.TraceId,
            ["bot"] = result.BotName,
            ["action"] = spec.Kind.ToString(),
            ["state"] = nameof(ActorLifecycleState.Requested)
        });
    }

    /// <summary>
    /// Shapes a snapshot into the wire JSON. Field names mirror the B1 audit
    /// record (ActorAuditRecord.ToJson) plus the live lifecycle state and the
    /// result payload; the embedded audit object is the actor's own record
    /// (its trace id is the request's id — the API trace id is the entry's).
    /// </summary>
    private static JObject BuildActionJson(BotActionSnapshot snapshot)
    {
        return new JObject
        {
            ["trace_id"] = snapshot.TraceId,
            ["actor_id"] = snapshot.ActorId,
            ["bot"] = snapshot.BotName,
            ["action"] = snapshot.Action,
            ["state"] = snapshot.State,
            ["failure"] = snapshot.Failure,
            ["detail"] = snapshot.Detail,
            ["requested_at"] = snapshot.RequestedAtUtc,
            ["started_at"] = snapshot.StartedAtUtc,
            ["completed_at"] = snapshot.CompletedAtUtc,
            ["state_changes"] = new JArray(snapshot.StateChanges),
            ["audit"] = snapshot.AuditJson != null ? JObject.Parse(snapshot.AuditJson) : null,
            ["result_payload"] = snapshot.Result != null
                ? JToken.FromObject(snapshot.Result, Newtonsoft.Json.JsonSerializer.Create(ResultSettings))
                : null
        };
    }

    private static HttpResponse Error(Exception ex, string logMessage)
    {
        Logger.Error(ex, logMessage);
        return JsonResponse(HttpStatusCode.InternalServerError, new ErrorModel(ex.Message));
    }
}
