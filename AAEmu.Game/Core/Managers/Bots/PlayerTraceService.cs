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

// PlayerTrace JSONL schema (additive versions, old keys never change):
//
//   v1 — ts / elapsed_ms / character_id / character_name / category / event / data.
//
//   v2 — `state`: a compact CONSEQUENCE snapshot attached to every trace event
//        that represents an action outcome (skill cast results, doodad use,
//        resource money/labor changes, loot/item grants, quest accept and
//        completion, mount/dismount, refusals). Fields: hp, maxHp, mp, maxMp,
//        money, labor, bagTotal, bagSlots, bagFreeSlots, bagDelta, questId,
//        questActive, questStep, questStatus, questObjectives, targetObjId,
//        isMounted, pos{x,y,z}.
//
//        Every field is read through the SAME ordinary server accessors the M5
//        observation snapshot (GameplayActor.Observe / ActorObservation) uses —
//        no new engine queries, no packet injection, no engine mutation. A read
//        that fails or does not apply to the event is written as an EXPLICIT
//        JSON null: unknown stays unknown, nothing is fabricated or defaulted.
//
//        The bag is summarised as a cheap diff-friendly triple (bagTotal = all
//        item units, bagSlots = occupied entries, bagFreeSlots) instead of a
//        full per-template map, so outcome lines stay compact; consumers diff
//        bagTotal between consecutive events to recover the bag delta of any
//        event, and loot events carry the exact bagDelta of the grant.
//
//        Consumers that predate v2 simply ignore the extra `state` key.

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Position triple carried inside <see cref="TraceStateSnapshot"/>.
/// </summary>
public sealed record TraceStatePos
{
    [JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public float X { get; init; }

    [JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public float Y { get; init; }

    [JsonPropertyName("z"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public float Z { get; init; }
}

/// <summary>
/// Compact server-side consequence snapshot (trace schema v2) attached to
/// action-outcome events. See the schema note at the top of this file: all
/// fields are read through ordinary server accessors at event time, and every
/// unreadable / inapplicable field is serialized as an explicit null.
/// </summary>
public sealed record TraceStateSnapshot
{
    [JsonPropertyName("hp"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Hp { get; init; }

    [JsonPropertyName("maxHp"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? MaxHp { get; init; }

    [JsonPropertyName("mp"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Mp { get; init; }

    [JsonPropertyName("maxMp"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? MaxMp { get; init; }

    /// <summary>Inventory copper balance (Character.Money).</summary>
    [JsonPropertyName("money"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? Money { get; init; }

    /// <summary>Current labor power (Character.LaborPower).</summary>
    [JsonPropertyName("labor"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public short? Labor { get; init; }

    /// <summary>Total item units currently in the bag container (Inventory.Bag).</summary>
    [JsonPropertyName("bagTotal"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BagTotal { get; init; }

    /// <summary>Occupied bag entries (item stacks) in the bag container.</summary>
    [JsonPropertyName("bagSlots"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BagSlots { get; init; }

    /// <summary>Free bag slots (ItemContainer.FreeSlotCount).</summary>
    [JsonPropertyName("bagFreeSlots"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BagFreeSlots { get; init; }

    /// <summary>
    /// Exact item-unit change of THIS event when the caller knows it (loot /
    /// item grants, null for coin-only grants); null when the event does not
    /// itself state a bag delta — diff <see cref="BagTotal"/> instead.
    /// </summary>
    [JsonPropertyName("bagDelta"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BagDelta { get; init; }

    /// <summary>Quest context id this event concerns (null when none applies).</summary>
    [JsonPropertyName("questId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? QuestId { get; init; }

    /// <summary>True when the quest is (still) in ActiveQuests at event time.</summary>
    [JsonPropertyName("questActive"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? QuestActive { get; init; }

    /// <summary>Quest step (QuestComponentKind) at event time.</summary>
    [JsonPropertyName("questStep"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? QuestStep { get; init; }

    /// <summary>Quest status (QuestStatus) at event time.</summary>
    [JsonPropertyName("questStatus"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? QuestStatus { get; init; }

    /// <summary>Objective counters of the quest's current step at event time.</summary>
    [JsonPropertyName("questObjectives"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int[]? QuestObjectives { get; init; }

    /// <summary>Current target objId (0 = no target).</summary>
    [JsonPropertyName("targetObjId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? TargetObjId { get; init; }

    /// <summary>True when mounted on an active mate (BotMountManager.IsMounted).</summary>
    [JsonPropertyName("isMounted"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsMounted { get; init; }

    /// <summary>World position at event time (Transform.World.Position).</summary>
    [JsonPropertyName("pos"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TraceStatePos? Pos { get; init; }
}

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

    /// <summary>
    /// Schema v2 consequence snapshot (null on events that carry no consequence
    /// state, e.g. lifecycle/packet/marker/movement rows — those keep the v1
    /// shape plus their own payloads).
    /// </summary>
    [JsonPropertyName("state")]
    public TraceStateSnapshot? State { get; init; }
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
    /// Direct, thread-safe write to trace output (schema v1 call shape — no state).
    /// </summary>
    public void RecordRaw(uint characterId, string category, string eventName, object? data = null)
        => RecordRaw(characterId, category, eventName, data, state: null);

    /// <summary>
    /// Direct, thread-safe write to trace output, with an optional schema v2
    /// consequence snapshot. Both entry points share this body so no event can
    /// accidentally bypass the state slot.
    /// </summary>
    public void RecordRaw(uint characterId, string category, string eventName, object? data, TraceStateSnapshot? state)
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
                    Data = data,
                    State = state
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

    /// <summary>
    /// Builds the schema v2 consequence snapshot for <paramref name="character"/>
    /// at event time. Every field is read through the same ordinary server
    /// accessors the M5 observation snapshot uses
    /// (<see cref="GameplayActor.Observe"/> / <see cref="ActorObservation"/>):
    /// Character vitals, Money, LaborPower, Inventory.Bag, Quests.ActiveQuests,
    /// CurrentTarget, BotMountManager.IsMounted, Transform.World.Position.
    ///
    /// A read that throws, or a field that does not apply to this event, is left
    /// as an explicit null — the tracer never fabricates, infers, or defaults a
    /// value it could not actually read. This method performs no engine query
    /// beyond those ordinary accessors and mutates nothing (tracing only).
    /// </summary>
    /// <param name="character">Character the event concerns (null = nothing readable).</param>
    /// <param name="questId">Quest context this event concerns, when applicable.</param>
    /// <param name="bagDelta">Exact bag delta of this event, when the caller knows it.</param>
    internal static TraceStateSnapshot CaptureState(Character? character, uint? questId = null, int? bagDelta = null)
    {
        if (character == null)
        {
            // No actor to read: every field stays explicitly unknown.
            return new TraceStateSnapshot { QuestId = questId, BagDelta = bagDelta };
        }

        int? hp = null, maxHp = null, mp = null, maxMp = null;
        long? money = null;
        short? labor = null;
        int? bagTotal = null, bagSlots = null, bagFreeSlots = null;
        uint? targetObjId = null;
        bool? isMounted = null;
        TraceStatePos? pos = null;
        bool? questActive = null;
        string? questStep = null, questStatus = null;
        int[]? questObjectives = null;

        try { hp = character.Hp; } catch { /* unknown stays unknown */ }
        try { maxHp = character.MaxHp; } catch { /* unknown stays unknown */ }
        try { mp = character.Mp; } catch { /* unknown stays unknown */ }
        try { maxMp = character.MaxMp; } catch { /* unknown stays unknown */ }
        try { money = character.Money; } catch { /* unknown stays unknown */ }
        try { labor = character.LaborPower; } catch { /* unknown stays unknown */ }
        try { targetObjId = character.CurrentTarget?.ObjId ?? 0; } catch { /* unknown stays unknown */ }
        try { isMounted = BotMountManager.IsMounted(character); } catch { /* unknown stays unknown */ }
        try
        {
            var worldPos = character.Transform.World.Position;
            pos = new TraceStatePos { X = worldPos.X, Y = worldPos.Y, Z = worldPos.Z };
        }
        catch { /* unknown stays unknown */ }

        try
        {
            var bag = character.Inventory?.Bag;
            if (bag != null)
            {
                var items = bag.GetItemsSnapshot();
                var total = 0;
                foreach (var item in items)
                    total += item.Count;
                bagTotal = total;
                bagSlots = items.Count;
                bagFreeSlots = bag.FreeSlotCount;
            }
        }
        catch { /* unknown stays unknown */ }

        if (questId.HasValue)
        {
            try
            {
                var quest = character.Quests?.ActiveQuests.GetValueOrDefault(questId.Value);
                questActive = quest != null;
                if (quest != null)
                {
                    questStep = quest.Step.ToString();
                    questStatus = quest.Status.ToString();
                    questObjectives = quest.GetObjectives(quest.Step);
                }
            }
            catch { /* unknown stays unknown */ }
        }

        return new TraceStateSnapshot
        {
            Hp = hp,
            MaxHp = maxHp,
            Mp = mp,
            MaxMp = maxMp,
            Money = money,
            Labor = labor,
            BagTotal = bagTotal,
            BagSlots = bagSlots,
            BagFreeSlots = bagFreeSlots,
            BagDelta = bagDelta,
            QuestId = questId,
            QuestActive = questActive,
            QuestStep = questStep,
            QuestStatus = questStatus,
            QuestObjectives = questObjectives,
            TargetObjId = targetObjId,
            IsMounted = isMounted,
            Pos = pos
        };
    }

    /// <summary>
    /// Total item units currently held in the character's bag container — the
    /// cheap diff-friendly half of the consequence snapshot. Returns null when
    /// the container is unreadable (explicit unknown).
    /// </summary>
    internal static int? BagItemUnits(Character? character)
    {
        try
        {
            var bag = character?.Inventory?.Bag;
            if (bag == null)
                return null;
            var total = 0;
            foreach (var item in bag.GetItemsSnapshot())
                total += item.Count;
            return total;
        }
        catch
        {
            return null;
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
        }, ConsequencePacketState(character, packet));
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
        }, ConsequencePacketState(character, packet));
    }

    /// <summary>
    /// Consequence snapshot for the small set of packets whose handling IS an
    /// action outcome (quest accept / turn-in / step / completion, loot,
    /// mount/dismount, skill cast). Every other packet row stays state-free —
    /// packet volume is dominated by movement/ping traffic where a snapshot
    /// would be pure noise.
    ///
    /// Packet handling runs inside <c>Decode</c> before this hook, so the state
    /// read here is the post-action state of the opcode that just executed.
    /// </summary>
    private static TraceStateSnapshot? ConsequencePacketState(Character character, GamePacket packet)
    {
        switch (packet)
        {
            case CSStartQuestContextPacket or
                 CSCompleteQuestContextPacket or
                 CSTryQuestCompleteAsLetItDonePacket or
                 CSDropQuestContextPacket or
                 SCQuestContextStartedPacket or
                 SCQuestContextUpdatedPacket or
                 SCQuestContextCompletedPacket or
                 SCQuestContextResetPacket:
            case CSLootOpenBagPacket or CSLootItemPacket or SCLootItemTookPacket or SCLootBagDataPacket:
            case CSMountMatePacket or CSUnMountMatePacket or CSRemoveMatePacket:
            case CSStartSkillPacket or SCSkillFiredPacket or SCSkillEndedPacket:
                return CaptureState(character);
            default:
                return null;
        }
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
        }, CaptureState(character));
    }

    /// <summary>
    /// Records an interaction outcome (doodad use, mount/dismount, merchant
    /// traffic, dialogue) and attaches the schema v2 consequence snapshot.
    /// <paramref name="bagDelta"/> carries the exact bag change of this event
    /// when the caller knows it (loot/pickup); null otherwise (diff BagTotal).
    /// </summary>
    public void RecordInteraction(Character character, string interactionEvent, uint targetObjId, string targetType, object? details = null,
        int? bagDelta = null)
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
        }, CaptureState(character, bagDelta: bagDelta));
    }

    /// <summary>
    /// Records a quest outcome (accept / turn-in / step change / completion)
    /// with the quest's live consequence state: active flag, step, status and
    /// the objective counters of the current step.
    /// </summary>
    public void RecordQuest(Character character, string questEvent, uint questId, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        RecordRaw(character.Id, "quest", questEvent, new
        {
            QuestId = questId,
            Details = details
        }, CaptureState(character, questId: questId));
    }

    /// <summary>
    /// Records a loot/item grant outcome with the exact bag delta of the grant
    /// alongside the post-grant consequence snapshot. <paramref name="bagDelta"/>
    /// is null for coin-only grants and when the container was unreadable.
    /// </summary>
    public void RecordLoot(Character character, string lootEvent, uint sourceObjId, uint itemTemplateId, int count, int? bagDelta, object? details = null)
    {
        if (!_isActive || character.Id != _tracedCharacterId) return;
        RecordRaw(character.Id, "loot", lootEvent, new
        {
            SourceObjId = sourceObjId,
            ItemTemplateId = itemTemplateId,
            Count = count,
            Details = details
        }, CaptureState(character, bagDelta: bagDelta));
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
        }, CaptureState(character));
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
        }, CaptureState(character));
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
        }, CaptureState(character));
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
