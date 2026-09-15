using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// A single machine-readable semantic trace record outputted to JSONL.
/// </summary>
public sealed record PlayerTraceRecord
{
    [JsonPropertyName("ts")]
    public string Ts { get; init; } = string.Empty;

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("character_id")]
    public uint CharacterId { get; init; }

    [JsonPropertyName("character_name")]
    public string CharacterName { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

/// <summary>
/// Observational server-side player action tracer for capturing human golden traces
/// and comparing them against PlayerBot execution.
/// </summary>
public class PlayerTraceService : Singleton<PlayerTraceService>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private volatile bool _isActive;
    private uint _tracedCharacterId;
    private string _tracedCharacterName = string.Empty;
    private string _scenarioName = string.Empty;
    private Stopwatch? _stopwatch;
    private StreamWriter? _writer;
    private string? _filePath;
    private int _eventCount;

    // Movement sampling state
    private Vector3 _lastRecordedPos = Vector3.Zero;
    private float _lastRecordedYaw;
    private DateTime _lastMoveSampleUtc = DateTime.MinValue;
    private bool _wasMoving;

    public bool IsActive => _isActive;
    public uint TracedCharacterId => _tracedCharacterId;
    public string TracedCharacterName => _tracedCharacterName;
    public string ScenarioName => _scenarioName;
    public string? ActiveFilePath => _filePath;
    public int EventCount => _eventCount;

    /// <summary>
    /// Starts tracing a selected character by domain ID and name.
    /// </summary>
    public (bool Success, string Message, string? FilePath) StartTrace(uint characterId, string characterName, string scenarioName = "unspecified")
    {
        lock (_lock)
        {
            if (_isActive)
            {
                return (false, $"Trace already active for character '{_tracedCharacterName}' ({_tracedCharacterId}). Stop it first.", _filePath);
            }

            try
            {
                var baseDir = Path.Combine(Directory.GetCurrentDirectory(), "traces", "player-actions");
                Directory.CreateDirectory(baseDir);

                var sanitizedScenario = string.Join("_", scenarioName.Split(Path.GetInvalidFileNameChars()));
                var sanitizedChar = string.Join("_", characterName.Split(Path.GetInvalidFileNameChars()));
                var fileName = $"{sanitizedScenario}__{sanitizedChar}__{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl";
                _filePath = Path.Combine(baseDir, fileName);

                _writer = new StreamWriter(new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };

                _tracedCharacterId = characterId;
                _tracedCharacterName = characterName;
                _scenarioName = scenarioName;
                _eventCount = 0;
                _stopwatch = Stopwatch.StartNew();
                _wasMoving = false;
                _lastMoveSampleUtc = DateTime.MinValue;
                _isActive = true;

                RecordRaw(characterId, "lifecycle", "trace_started", new
                {
                    Scenario = scenarioName,
                    CharacterName = characterName,
                    CharacterId = characterId,
                    ServerTimeUtc = DateTime.UtcNow.ToString("o")
                });

                Logger.Info($"[PlayerTrace] Started trace for '{characterName}' ({characterId}) -> {_filePath}");
                return (true, $"Started trace for '{characterName}' -> {_filePath}", _filePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[PlayerTrace] Failed to start trace: {ex.Message}");
                _writer?.Dispose();
                _writer = null;
                _isActive = false;
                return (false, $"Failed to start trace: {ex.Message}", null);
            }
        }
    }

    /// <summary>
    /// Stops the currently active trace.
    /// </summary>
    public (bool Success, string Message, string? FilePath) StopTrace()
    {
        lock (_lock)
        {
            if (!_isActive)
            {
                return (false, "No player trace is currently active.", null);
            }

            try
            {
                RecordRaw(_tracedCharacterId, "lifecycle", "trace_stopped", new
                {
                    TotalEvents = _eventCount + 1,
                    ElapsedMs = _stopwatch?.ElapsedMilliseconds ?? 0,
                    ServerTimeUtc = DateTime.UtcNow.ToString("o")
                });

                _stopwatch?.Stop();
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;

                var finalPath = _filePath;
                var eventCount = _eventCount;
                var charName = _tracedCharacterName;

                _isActive = false;
                _tracedCharacterId = 0;
                _tracedCharacterName = string.Empty;
                _scenarioName = string.Empty;
                _eventCount = 0;

                Logger.Info($"[PlayerTrace] Stopped trace for '{charName}'. {eventCount} events recorded to {finalPath}");
                return (true, $"Stopped trace for '{charName}'. {eventCount} events written to: {finalPath}", finalPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[PlayerTrace] Error stopping trace: {ex.Message}");
                _isActive = false;
                _eventCount = 0;
                return (false, $"Error stopping trace: {ex.Message}", _filePath);
            }
        }
    }

    /// <summary>
    /// Inserts a free-text human/tester marker into the trace chronology.
    /// </summary>
    public (bool Success, string Message) RecordMark(string label)
    {
        if (!_isActive)
        {
            return (false, "Cannot record marker: no trace currently active.");
        }

        RecordRaw(_tracedCharacterId, "marker", "mark", new { Label = label });
        return (true, $"Recorded marker: '{label}'");
    }

    /// <summary>
    /// Gets current status of the tracer.
    /// </summary>
    public (bool IsActive, string CharacterName, string Scenario, long ElapsedMs, int Events, string? FilePath) GetStatus()
    {
        lock (_lock)
        {
            return (
                _isActive,
                _tracedCharacterName,
                _scenarioName,
                _stopwatch?.ElapsedMilliseconds ?? 0,
                _eventCount,
                _filePath
            );
        }
    }

    /// <summary>
    /// Direct, thread-safe write to trace output.
    /// </summary>
    public void RecordRaw(uint characterId, string category, string eventName, object? data = null)
    {
        if (!_isActive || characterId != _tracedCharacterId)
            return;

        lock (_lock)
        {
            if (!_isActive || characterId != _tracedCharacterId || _writer == null)
                return;

            try
            {
                var record = new PlayerTraceRecord
                {
                    Ts = DateTime.UtcNow.ToString("o"),
                    ElapsedMs = _stopwatch?.ElapsedMilliseconds ?? 0,
                    CharacterId = characterId,
                    CharacterName = _tracedCharacterName,
                    Category = category,
                    Event = eventName,
                    Data = data
                };

                var line = JsonSerializer.Serialize(record, JsonOptions);
                _writer.WriteLine(line);
                _eventCount++;
            }
            catch
            {
                // Never allow trace serialization failures to disrupt server or gameplay
            }
        }
    }

    #region Specific Semantic Hooks

    public void RecordPacketIn(Character character, GamePacket packet)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;

        // Operator chat filter: exclude slash commands from player action traces
        if (packet is CSSendChatMessagePacket chat && chat.IsCommand)
            return;

        object? details = ExtractInboundPacketDetails(packet);
        RecordRaw(character.Id, "packet", "packet_in", new
        {
            Packet = packet.GetType().Name,
            Opcode = $"0x{packet.TypeId:X3}",
            Details = details
        });
    }

    public void RecordPacketOut(Character character, GamePacket packet)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;

        object? details = ExtractOutboundPacketDetails(packet);
        RecordRaw(character.Id, "packet", "packet_out", new
        {
            Packet = packet.GetType().Name,
            Opcode = $"0x{packet.TypeId:X3}",
            Details = details
        });
    }

    public void RecordMovementSample(Character character, bool isMoving, Vector3 pos, float yaw, Vector3? velocity = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;

        var now = DateTime.UtcNow;

        // State change: begin or stop
        if (isMoving != _wasMoving)
        {
            _wasMoving = isMoving;
            _lastMoveSampleUtc = now;
            _lastRecordedPos = pos;
            _lastRecordedYaw = yaw;

            RecordRaw(character.Id, "movement", isMoving ? "move_begin" : "move_stop", new
            {
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                Yaw = yaw,
                VelX = velocity?.X ?? 0f,
                VelY = velocity?.Y ?? 0f,
                VelZ = velocity?.Z ?? 0f
            });
            return;
        }

        // Throttled sample while moving (every 250ms or significant rotation/distance)
        if (isMoving && (now - _lastMoveSampleUtc).TotalMilliseconds >= 250)
        {
            _lastMoveSampleUtc = now;
            _lastRecordedPos = pos;
            _lastRecordedYaw = yaw;

            RecordRaw(character.Id, "movement", "movement_update", new
            {
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                Yaw = yaw,
                VelX = velocity?.X ?? 0f,
                VelY = velocity?.Y ?? 0f,
                VelZ = velocity?.Z ?? 0f
            });
        }
    }

    public void RecordSkill(Character character, string skillEvent, uint skillId, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        var pos = character.Transform.World.Position;
        var yaw = character.Transform.World.Rotation.Z;
        var target = character.CurrentTarget;
        float? range = target != null ? Vector3.Distance(pos, target.Transform.World.Position) : null;
        RecordRaw(character.Id, "skill", skillEvent, new
        {
            SkillId = skillId,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Yaw = yaw,
            TargetObjId = target?.ObjId,
            TargetType = target?.GetType().Name,
            Range = range,
            Details = details
        });
    }

    public void RecordInteraction(Character character, string interactionEvent, uint targetObjId, string targetType, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        var pos = character.Transform.World.Position;
        var yaw = character.Transform.World.Rotation.Z;
        Vector3? targetPos = null;
        float? range = null;
        var targetObj = (AAEmu.Game.Models.Game.World.GameObject?)character.ParentWorld?.GetUnit(targetObjId)
            ?? character.ParentWorld?.GetDoodad(targetObjId);
        if (targetObj != null)
        {
            targetPos = targetObj.Transform.World.Position;
            range = Vector3.Distance(pos, targetPos.Value);
        }
        RecordRaw(character.Id, "interaction", interactionEvent, new
        {
            TargetObjId = targetObjId,
            TargetType = targetType,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Yaw = yaw,
            TargetX = targetPos?.X,
            TargetY = targetPos?.Y,
            TargetZ = targetPos?.Z,
            Range = range,
            Details = details
        });
    }

    public void RecordWorld(Character character, string worldEvent, uint objId, uint templateId, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        var pos = character.Transform.World.Position;
        var yaw = character.Transform.World.Rotation.Z;
        RecordRaw(character.Id, "world", worldEvent, new
        {
            ObjId = objId,
            TemplateId = templateId,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Yaw = yaw,
            Details = details
        });
    }

    public void RecordRefusal(Character character, string actionType, uint actionId, string reason, uint errorValue, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        var pos = character.Transform.World.Position;
        var yaw = character.Transform.World.Rotation.Z;
        var target = character.CurrentTarget;
        float? range = target != null ? Vector3.Distance(pos, target.Transform.World.Position) : null;
        RecordRaw(character.Id, "refusal", $"{actionType}_refused", new
        {
            ActionType = actionType,
            ActionId = actionId,
            Reason = reason,
            ErrorValue = errorValue,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Yaw = yaw,
            TargetObjId = target?.ObjId,
            Range = range,
            Details = details
        });
    }

    public void RecordResource(Character character, string resourceEvent, string resourceType, long delta, long currentVal, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        RecordRaw(character.Id, "resource", resourceEvent, new
        {
            ResourceType = resourceType,
            Delta = delta,
            CurrentValue = currentVal,
            Details = details
        });
    }

    public void RecordTarget(Character character, string targetEvent, uint targetObjId, string? targetType = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        RecordRaw(character.Id, "targeting", targetEvent, new
        {
            TargetObjId = targetObjId,
            TargetType = targetType ?? "None"
        });
    }

    #endregion

    #region Packet Detail Extractors

    private static object? ExtractInboundPacketDetails(GamePacket packet)
    {
        try
        {
            return packet switch
            {
                CSCreateDoodadPacket d => new
                {
                    d.DoodadId,
                    d.Position.X,
                    d.Position.Y,
                    d.Position.Z,
                    Yaw = d.ZRot,
                    d.Scale,
                    d.ItemId
                },
                CSStartInteractionPacket i => new
                {
                    i.NpcObjId,
                    i.TargetObjId,
                    i.ExtraInfo,
                    i.PickId,
                    i.MouseButton
                },
                CSBuyItemsPacket b => new
                {
                    b.NpcObjId,
                    b.DoodadObjId,
                    b.NBuy,
                    b.NBuyBack,
                    Items = b.BoughtItems.Select(item => new { item.ItemId, item.Grade, item.Count }).ToList()
                },
                CSSellItemsPacket s => new
                {
                    s.NpcObjId,
                    Items = s.SoldItems.Select(item => new { item.ItemId, item.TemplateId, item.Count }).ToList()
                },
                CSSendChatMessagePacket m => new
                {
                    m.Message,
                    m.IsCommand
                },
                CSRepairAllEquipmentsPacket _ => new { Action = "repair_all" },
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

#pragma warning disable CA1859
    private static object? ExtractOutboundPacketDetails(GamePacket packet)
    {
        try
        {
            return packet switch
            {
                SCSkillStartedPacket s => new
                {
                    s.BaseCastTimeDiv10,
                    s.RealCastTimeDiv10,
                    CastSynergy = s.CastSynergy
                },
                SCOneUnitMovementPacket _ => null,
                SCUnitMovementsPacket _ => null,
                SCItemTaskSuccessPacket _ => null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
#pragma warning restore CA1859

    #endregion
}
