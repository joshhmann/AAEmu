using System.Text.Json;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// JSON manifest describing one runnable quest scenario for the M1-5 quest scenario
/// harness (AAEmu.UnitTests). One manifest = one quest = one driven lifecycle run:
/// START -> PROGRESS -> READY -> REWARD -> PERSIST.
///
/// MANIFEST FORMAT (documented here; the loader is permissive: unknown fields are
/// ignored, missing optional fields fall back to defaults):
///
/// {
///   "questId": 1119,                          // QuestContextId (template id)
///   "name": "Arcum Iris",                     // optional, human-readable label
///   "acceptor": { "type": "Npc", "id": 2237 },// how the quest is started. type is a
///                                             // QuestAcceptorType name (Unknown/Npc/
///                                             // Doodad/Sphere/Item/Skill/Buff/Kill)
///   "template": {                             // optional template parts. When present,
///     "level": 3,                             //   the driver builds the QuestTemplate
///     "components": [                         //   from these parts (no QuestManager.Load).
///       { "kind": "Start", "id": 5734,        // kind is a QuestComponentKind name
///         "acts": [                           // (Start/Supply/Progress/Fail/Ready/Reward)
///           { "type": "QuestActConAcceptNpc", // act type = production act class name
///             "npcId": 2237 }                 // act params: per-type JSON fields
///         ] }
///     ]
///   },
///   "guard": { "npcId": 6059, "alive": true },// optional world rig: spawn this guard NPC
///                                             // (alive or dead) so QuestActCheckGuard acts
///                                             // can resolve it without a world server
///   "stages": [                               // ordered lifecycle stages; the driver runs
///     { "name": "START",                      // each stage's events, then evaluates the
///       "events": [                           // step machine, then checks "expect".
///         { "type": "MonsterHunt", "npcId": 1000, "count": 1 }
///       ],
///       "expect": {                           // all fields optional; absent = no check
///         "step": "Ready",                    // expected QuestComponentKind name
///         "status": "Ready",                  // expected QuestStatus name
///         "objectives": [0, 0, 0, 0, 0],      // expected objective counters (5)
///         "rewardItems": [ { "itemId": 18792, "count": 1 } ], // expected inventory after REWARD
///         "completed": true,                  // expected completed-quest flag
///         "persistRoundTrip": true,           // expected WriteData->ReadData round-trip
///         "failPathWired": true               // expected CheckTimer act or Fail component
///       }
///     }
///   ]
/// }
///
/// SUPPORTED ACT TYPES (driver factory): QuestActConAcceptNpc/Kill/Doodad,
/// QuestActConReportNpc/Doodad/Journal, QuestActObjMonsterHunt/MonsterGroupHunt,
/// QuestActObjItemGather/ItemGroupGather/ItemGroupUse/ItemUse(internal, skipped)/
/// Talk/TalkNpcGroup/Interaction/Sphere/Craft/Level/ZoneMonsterHunt/ExpressFire,
/// QuestActCheckGuard/CheckSphere/CheckTimer, QuestActSupplyItem/Copper/Exp/
/// RemoveItem/SelectiveItem.
///
/// SUPPORTED EVENT TYPES (stage "events"): MonsterHunt, MonsterGroupHunt,
/// ItemGather, ItemGroupGather, ItemUse, ItemGroupUse, Talk, TalkNpcGroup,
/// Interaction, EnterSphere, Craft, ReportNpc, ReportDoodad, ReportJournal,
/// ExpressFire, LevelUp, Aggro, ZoneKill.
///
/// The "PERSIST" stage is special: the driver snapshots quest.WriteData() after
/// every non-terminal stage, then (on PERSIST) builds a fresh quest from the same
/// template/acceptor, feeds it ReadData(snapshot) and asserts the round-trip
/// (byte-identical WriteData plus step/acceptor/componentId/objective equality).
/// </summary>
public class QuestScenarioManifest
{
    public uint QuestId { get; set; }
    public string Name { get; set; } = "";
    public uint ZoneId { get; set; }
    public uint CategoryId { get; set; }
    public bool LetItDone { get; set; }
    public bool Selective { get; set; }
    public int Score { get; set; }
    /// <summary>Family label for the runnability report (golden-zone / kill-accept / check-guard / item-group).</summary>
    public string Family { get; set; } = "";
    public QuestAcceptorShape Acceptor { get; set; } = new();
    public QuestTemplateShape Template { get; set; } = new();
    public QuestGuardShape Guard { get; set; }
    /// <summary>Items pre-placed in the rigged inventory before the quest starts (acceptor item, gather objectives).</summary>
    public List<QuestRewardItemShape> Inventory { get; set; } = [];
    /// <summary>Item groups to seed into QuestManager._groupItems (BUG-009 read path).</summary>
    public QuestGroupsShape Groups { get; set; } = new();
    /// <summary>Selected selective-reward index (1-based, mirrors the loader's ThisSelectiveIndex).</summary>
    public int SelectedRewardIndex { get; set; }
    /// <summary>When present, the harness reports the quest as SKIP-with-reason without running it.</summary>
    public QuestSkipShape Skip { get; set; }
    public List<QuestStageShape> Stages { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static QuestScenarioManifest Load(string json)
    {
        return JsonSerializer.Deserialize<QuestScenarioManifest>(json, JsonOptions)
               ?? throw new InvalidOperationException("Scenario manifest parsed to null");
    }

    public static QuestScenarioManifest LoadFromFile(string path)
    {
        return Load(File.ReadAllText(path));
    }
}

public class QuestAcceptorShape
{
    /// <summary>QuestAcceptorType name, e.g. "Npc" or "Kill".</summary>
    public string Type { get; set; } = "Npc";
    public uint Id { get; set; }
}

public class QuestTemplateShape
{
    public byte Level { get; set; }
    public List<QuestComponentShape> Components { get; set; } = [];
}

public class QuestComponentShape
{
    /// <summary>QuestComponentKind name, e.g. "Start", "Progress", "Ready", "Reward".</summary>
    public string Kind { get; set; } = "Progress";
    public uint Id { get; set; }
    /// <summary>Raw act objects; each must carry a "type" field, params are act-specific.</summary>
    public List<JsonElement> Acts { get; set; } = [];
}

public class QuestGuardShape
{
    public uint NpcId { get; set; }
    public bool Alive { get; set; } = true;
}

public class QuestGroupsShape
{
    /// <summary>Item group id -> member item ids (seeded into QuestManager._groupItems).</summary>
    public Dictionary<uint, List<uint>> ItemGroups { get; set; } = [];
    /// <summary>Monster group id -> member npc ids (seeded into QuestManager._groupNpcs).</summary>
    public Dictionary<uint, List<uint>> NpcGroups { get; set; } = [];
}

public class QuestSkipShape
{
    /// <summary>Why the harness does not drive this quest (broken refs, harness gaps, ...).</summary>
    public string Reason { get; set; } = "";
}

public class QuestStageShape
{
    /// <summary>Stage name: START, PROGRESS, READY, REWARD, PERSIST (case-insensitive).</summary>
    public string Name { get; set; } = "";
    /// <summary>Raw event objects; each must carry a "type" field.</summary>
    public List<JsonElement> Events { get; set; } = [];
    public QuestExpectShape Expect { get; set; } = new();
}

public class QuestExpectShape
{
    /// <summary>Expected QuestComponentKind name after this stage (optional).</summary>
    public string Step { get; set; }
    /// <summary>Expected QuestStatus name after this stage (optional).</summary>
    public string Status { get; set; }
    /// <summary>Expected objective counters (optional, up to 5 entries).</summary>
    public int[] Objectives { get; set; }
    /// <summary>Expected items present in the inventory after this stage (optional).</summary>
    public List<QuestRewardItemShape> RewardItems { get; set; }
    /// <summary>Expected completed-quest flag (optional).</summary>
    public bool? Completed { get; set; }
    /// <summary>Expected WriteData->ReadData round-trip (optional; only meaningful on PERSIST).</summary>
    public bool? PersistRoundTrip { get; set; }
    /// <summary>Expected fail path (CheckTimer act or Fail component) to be wired (optional).</summary>
    public bool? FailPathWired { get; set; }

    public bool HasAnyExpectation => Step != null || Status != null || Objectives != null
        || RewardItems != null || Completed != null || PersistRoundTrip != null || FailPathWired != null;
}

public class QuestRewardItemShape
{
    public uint ItemId { get; set; }
    public int Count { get; set; } = 1;
}
