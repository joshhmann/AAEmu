using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The type of action or landmark recorded along a manual walk route.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MapperActionType
{
    Waypoint = 0,
    Mark = 1,
    InteractDoodad = 2,
    TalkNpc = 3,
    CastSkill = 4,
    Mount = 5,
    Dismount = 6
}

/// <summary>
/// A single action record along a recorded route.
/// </summary>
public sealed record MapperActionRecord
{
    public MapperActionType ActionType { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float Yaw { get; init; }
    public string? Label { get; init; }
    public uint TargetObjId { get; init; }
    public uint TemplateId { get; init; }
    public uint SkillId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
}

/// <summary>
/// Complete structured route data serialized to JSON.
/// </summary>
public sealed record MapperRouteData
{
    public string RouteName { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public float TotalDistance { get; init; }
    public int WaypointCount { get; init; }
    public int ActionCount { get; init; }
    public List<MapperActionRecord> Actions { get; init; } = [];
}

/// <summary>
/// Result summary returned when a recording session completes.
/// </summary>
public sealed record MapperSessionSummary(
    bool Success,
    string Message,
    string RouteName,
    int WaypointCount,
    int ActionCount,
    float TotalDistance,
    string? JsonPath = null,
    string? PathFilePath = null);

/// <summary>
/// In-progress recording session for a character.
/// </summary>
internal sealed class MapperSession
{
    public uint CharacterId { get; }
    public string RouteName { get; }
    public string Author { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public List<MapperActionRecord> Actions { get; } = [];
    public Vector3 LastPosition { get; set; }
    public float LastYaw { get; set; }
    public float TotalDistance { get; set; }
    public int WaypointCount { get; set; }
    public int ActionCount { get; set; }

    public MapperSession(uint characterId, string routeName, string author, Vector3 startPos, float yaw)
    {
        CharacterId = characterId;
        RouteName = routeName;
        Author = author;
        LastPosition = startPos;
        LastYaw = yaw;

        // Record initial waypoint
        Actions.Add(new MapperActionRecord
        {
            ActionType = MapperActionType.Waypoint,
            X = startPos.X,
            Y = startPos.Y,
            Z = startPos.Z,
            Yaw = yaw,
            Label = "start"
        });
        WaypointCount = 1;
    }
}

/// <summary>
/// Dev Mapper & Action Recorder service — tracks manual walk routes in-game,
/// recording waypoints with distance/angle compaction, doodad interactions,
/// NPC talks, and custom marks. Exports dual JSON and .path route files.
/// </summary>
public class DevMapperService : Singleton<DevMapperService>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly object _lock = new();
    private readonly Dictionary<uint, MapperSession> _activeSessions = [];
    private volatile bool _hasActiveSessions;

    public string RoutesDirectory { get; set; } = Path.Combine("Data", "Routes");
    public string PathsDirectory { get; set; } = Path.Combine("Data", "Path");

    public float MinWaypointDistance { get; set; } = 1.5f;
    public float MinYawDeltaRadians { get; set; } = 0.35f; // ~20 degrees

    public bool IsRecording(uint characterId)
    {
        if (!_hasActiveSessions)
            return false;

        lock (_lock)
        {
            return _activeSessions.ContainsKey(characterId);
        }
    }

    public MapperSessionSummary Start(ICharacter character, string routeName)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(routeName))
                return new MapperSessionSummary(false, "Route name cannot be empty.", "", 0, 0, 0f);

            var cleanName = SanitizeRouteName(routeName);
            if (_activeSessions.ContainsKey(character.Id))
                return new MapperSessionSummary(false, $"Character is already recording route '{_activeSessions[character.Id].RouteName}'.", "", 0, 0, 0f);

            var pos = character.Transform.World.Position;
            var yaw = character.Transform.World.Rotation.Z;
            var session = new MapperSession(character.Id, cleanName, character.Name, pos, yaw);
            _activeSessions[character.Id] = session;
            _hasActiveSessions = true;

            Logger.Info($"[Mapper] Started manual walk session '{cleanName}' for character {character.Name} ({character.Id}) at {pos}");
            return new MapperSessionSummary(true, $"Manual Walk Mode started for '{cleanName}'. Tracing waypoints & actions...", cleanName, 1, 0, 0f);
        }
    }

    public void RecordPosition(uint characterId, Vector3 newPos, float yaw)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(characterId, out var session))
                return;

            var dist = Vector3.Distance(session.LastPosition, newPos);
            var yawDelta = Math.Abs(session.LastYaw - yaw);

            // Record if distance threshold met or significant bearing change
            if (dist >= MinWaypointDistance || (dist >= 0.5f && yawDelta >= MinYawDeltaRadians))
            {
                session.TotalDistance += dist;
                session.LastPosition = newPos;
                session.LastYaw = yaw;
                session.WaypointCount++;

                session.Actions.Add(new MapperActionRecord
                {
                    ActionType = MapperActionType.Waypoint,
                    X = newPos.X,
                    Y = newPos.Y,
                    Z = newPos.Z,
                    Yaw = yaw
                });
            }
        }
    }

    public void RecordInteract(uint characterId, uint doodadObjId, uint templateId, Vector3 pos, uint skillId = 0)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(characterId, out var session))
                return;

            session.ActionCount++;
            session.Actions.Add(new MapperActionRecord
            {
                ActionType = MapperActionType.InteractDoodad,
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                TargetObjId = doodadObjId,
                TemplateId = templateId,
                SkillId = skillId,
                Label = $"doodad_{templateId}"
            });

            Logger.Debug($"[Mapper] Recorded doodad interact {templateId} (obj {doodadObjId}) for session '{session.RouteName}'");
        }
    }

    public void RecordTalk(uint characterId, uint npcObjId, uint npcTemplateId, Vector3 pos)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(characterId, out var session))
                return;

            session.ActionCount++;
            session.Actions.Add(new MapperActionRecord
            {
                ActionType = MapperActionType.TalkNpc,
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                TargetObjId = npcObjId,
                TemplateId = npcTemplateId,
                Label = $"npc_{npcTemplateId}"
            });

            Logger.Debug($"[Mapper] Recorded NPC talk {npcTemplateId} (obj {npcObjId}) for session '{session.RouteName}'");
        }
    }

    public bool RecordMark(uint characterId, string label, Vector3 pos, float yaw)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(characterId, out var session))
                return false;

            session.ActionCount++;
            session.Actions.Add(new MapperActionRecord
            {
                ActionType = MapperActionType.Mark,
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                Yaw = yaw,
                Label = label
            });

            return true;
        }
    }

    public MapperSessionSummary Stop(uint characterId)
    {
        MapperSession? session;
        lock (_lock)
        {
            if (!_activeSessions.Remove(characterId, out session))
                return new MapperSessionSummary(false, "No active recording session found for character.", "", 0, 0, 0f);
            _hasActiveSessions = _activeSessions.Count > 0;
        }

        try
        {
            Directory.CreateDirectory(RoutesDirectory);
            Directory.CreateDirectory(PathsDirectory);

            var jsonPath = Path.Combine(RoutesDirectory, $"{session.RouteName}.json");
            var pathFilePath = Path.Combine(PathsDirectory, $"{session.RouteName}.path");

            var routeData = new MapperRouteData
            {
                RouteName = session.RouteName,
                Author = session.Author,
                CreatedAt = session.StartedAt,
                TotalDistance = session.TotalDistance,
                WaypointCount = session.WaypointCount,
                ActionCount = session.ActionCount,
                Actions = session.Actions
            };

            // 1. Export JSON action graph
            var json = JsonSerializer.Serialize(routeData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);

            // 2. Export standard .path file (only waypoints and positions)
            var pathLines = session.Actions
                .Where(a => a.ActionType is MapperActionType.Waypoint or MapperActionType.Mark)
                .Select(a => $"|{a.X:F2}|{a.Y:F2}|{a.Z:F4}|");
            File.WriteAllLines(pathFilePath, pathLines);

            Logger.Info($"[Mapper] Saved route '{session.RouteName}': {session.WaypointCount} waypoints, {session.ActionCount} actions, {session.TotalDistance:F1}m");
            return new MapperSessionSummary(
                true,
                $"Route '{session.RouteName}' saved successfully! ({session.WaypointCount} waypoints, {session.ActionCount} actions, {session.TotalDistance:F1}m)",
                session.RouteName,
                session.WaypointCount,
                session.ActionCount,
                session.TotalDistance,
                jsonPath,
                pathFilePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[Mapper] Failed to save route '{session.RouteName}': {ex.Message}");
            return new MapperSessionSummary(false, $"Error saving route files: {ex.Message}", session.RouteName, session.WaypointCount, session.ActionCount, session.TotalDistance);
        }
    }

    public bool CancelSession(uint characterId)
    {
        lock (_lock)
        {
            var removed = _activeSessions.Remove(characterId);
            if (removed)
                _hasActiveSessions = _activeSessions.Count > 0;
            return removed;
        }
    }

    public IReadOnlyList<string> ListRoutes()
    {
        if (!Directory.Exists(RoutesDirectory))
            return [];

        return Directory.GetFiles(RoutesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    public MapperRouteData? GetRoute(string routeName)
    {
        var cleanName = SanitizeRouteName(routeName);
        var jsonPath = Path.Combine(RoutesDirectory, $"{cleanName}.json");
        if (!File.Exists(jsonPath))
            return null;

        var json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<MapperRouteData>(json);
    }

    public MapperReplayResult ReplayRoute(IGameplayActor actor, MapperRouteData route, float speed = 5f, TimeSpan? actionTimeout = null)
    {
        var timeout = actionTimeout ?? TimeSpan.FromSeconds(15);
        var completed = 0;

        foreach (var action in route.Actions)
        {
            switch (action.ActionType)
            {
                case MapperActionType.Waypoint:
                {
                    var req = actor.NavigateTo(action.Position, speed, timeout);
                    DriveToTerminal(actor, req, timeout);
                    if (req.State != ActorLifecycleState.Completed)
                    {
                        return new MapperReplayResult(false, $"Waypoint failed at ({action.X:F1}, {action.Y:F1}): {req.Detail}", completed, route.Actions.Count);
                    }
                    completed++;
                    break;
                }

                case MapperActionType.InteractDoodad:
                {
                    var interact = actor.InteractWith(action.TargetObjId);
                    if (interact.State != ActorLifecycleState.Completed)
                    {
                        Logger.Warn($"[Mapper] Replay interact with doodad {action.TargetObjId} returned {interact.State}: {interact.Detail}");
                    }
                    completed++;
                    break;
                }

                case MapperActionType.TalkNpc:
                {
                    var talk = actor.Talk(action.TargetObjId);
                    if (talk.State != ActorLifecycleState.Completed)
                    {
                        Logger.Warn($"[Mapper] Replay talk with NPC {action.TargetObjId} returned {talk.State}: {talk.Detail}");
                    }
                    completed++;
                    break;
                }

                case MapperActionType.CastSkill:
                {
                    var cast = actor.Cast(action.SkillId, action.TargetObjId);
                    DriveToTerminal(actor, cast, timeout);
                    completed++;
                    break;
                }

                case MapperActionType.Mark:
                {
                    Logger.Info($"[Mapper] Replay reached milestone: '{action.Label}'");
                    completed++;
                    break;
                }

                default:
                    completed++;
                    break;
            }
        }

        return new MapperReplayResult(true, $"Route '{route.RouteName}' replayed successfully ({completed}/{route.Actions.Count} actions).", completed, route.Actions.Count);
    }

    private static void DriveToTerminal(IGameplayActor actor, ActorRequest request, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!request.IsTerminal && Environment.TickCount64 < deadline)
        {
            if (actor is GameplayActor concrete)
                concrete.Tick(TimeSpan.FromMilliseconds(50));
            Thread.Sleep(5);
        }
    }

    private static string SanitizeRouteName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c) && c != ' ').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "route" : sanitized;
    }
}

public sealed record MapperReplayResult(bool Success, string Message, int CompletedActions, int TotalActions);

