using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The first three scenario templates (P1 t_5efae4f1) — the template library
/// starts as a code-defined registry; JSON definition files land with the
/// control-plane surface.
///
/// Every quest id, acceptor, gate value and event here is grounded in the
/// canonical compact.sqlite3 (md5 78b3bdbf…, r208022) and the calibrated
/// census manifests — the templates re-encode the same calibrated drive
/// shapes the scenario harness PROVES runnable (168: t8 PASS, 5531: t14
/// PASS, 1959: t4 PASS, pilot-driven 10/10).
///
/// Data facts per template (verified 2026-08-11):
///  - 168  창고에 생긴 일 — Start comp 2403: accept NPC 641 + unit_reqs
///    Level(22,0) + MotherFaction(148) + CompleteQuestContext(348); Ready
///    comp 2407: report NPC 639; Reward comp 2408. So the LEVEL GATE is 22
///    (the quest_contexts LEVEL=26 is the display level — the gate under
///    test is the unit_req).
///  - 5531 "ability check test" — Start comp 23884: accept NPC 13497 +
///    unit_reqs Ability(1/7/10 = Fight/Magic/Love, level 50); Ready comp
///    23886: report NPC 13497; Reward comp 23885. Literally built for this.
///  - 1959 장작을 모아보세요 (cat-34 daily, detail_id=6 Task) — Start comp
///    9789: ConAcceptComponent + AcceptItemGain(item 8017); Progress comp
///    9790: ItemGather(15589 ×3); Reward comp 9791: AutoComplete; unit_req
///    CompleteQuestContext(1958). REPEATABLE='f' + Task detail ⇒ the
///    completed flag survives ResetDailyQuests (daily family = 7/9/10/11),
///    so re-accept stays refused — the Task(6) daily-cycle semantics.
/// </summary>
public static class BotScenarioTemplates
{
    /// <summary>
    /// (a) Level-22 quest gating check — quest 168: the engine's Level unit
    /// requirement must refuse a level-21 character and admit a level-22
    /// one; the bot then completes the quest through the real paths.
    /// </summary>
    public static BotScenarioTemplate Level22QuestGate { get; } = new()
    {
        Name = "level22-gate",
        Description = "Level-22 quest gating: engine refuses quest 168 below level 22, admits at 22, bot completes it.",
        Race = Race.Nuian,
        Gender = Gender.Male,
        Level = 22,
        // Nuian mother faction (148) is the default for a Nuian character;
        // the kind-31 chain needs quest 348 completed (engine flag API).
        QuestStates =
        [
            new QuestStateRig(348, BotQuestPreState.Completed)
        ],
        GateChecks =
        [
            new LevelGateCheck("level-gate-168", QuestId: 168, "Npc", AcceptorId: 641, RefusedBelow: 22)
        ],
        Drive = new QuestDriveSpec
        {
            QuestId = 168,
            AcceptorType = "Npc",
            AcceptorId = 641,
            Stages =
            [
                new QuestDriveStage { Name = "START", Events = [] },
                new QuestDriveStage
                {
                    Name = "READY",
                    Events =
                    [
                        new ScenarioEvent { Type = "ReportNpc", NpcId = 639, Selected = 0 }
                    ]
                },
                new QuestDriveStage { Name = "REWARD", Events = [] }
            ]
        },
        Criteria =
        [
            new QuestCompletedCriterion("quest-168-completed", 168),
            new LevelAtLeastCriterion("level-at-22", 22)
        ]
    };

    /// <summary>
    /// (b) Skill/ability-prerequisite quest — quest 5531 ("ability check
    /// test"): accept requires Fight/Magic/Love (abilities 1/7/10) at level
    /// 50. The engine must refuse a bot whose abilities are below the gate
    /// and admit one rigged to 50 — then complete the quest.
    /// </summary>
    public static BotScenarioTemplate AbilityPrerequisiteGate { get; } = new()
    {
        Name = "ability-gate",
        Description = "Ability-prerequisite quest: engine refuses quest 5531 with abilities below 50, admits at 50, bot completes it.",
        Race = Race.Nuian,
        Gender = Gender.Male,
        Level = 50,
        AbilityTrees = [AbilityType.Fight, AbilityType.Magic, AbilityType.Love],
        // Rig the three gated abilities to 50 (the census t10 rig discipline:
        // exp saturation, never the wrapping GetExpForLevel path).
        AbilityLevels = new Dictionary<AbilityType, byte>
        {
            [AbilityType.Fight] = 50,
            [AbilityType.Magic] = 50,
            [AbilityType.Love] = 50
        },
        GateChecks =
        [
            new AbilityGateCheck("ability-gate-5531", QuestId: 5531, "Npc", AcceptorId: 13497,
                AbilityType.Fight, RefusedBelow: 50)
        ],
        Drive = new QuestDriveSpec
        {
            QuestId = 5531,
            AcceptorType = "Npc",
            AcceptorId = 13497,
            Stages =
            [
                new QuestDriveStage { Name = "START", Events = [] },
                new QuestDriveStage
                {
                    Name = "READY",
                    Events =
                    [
                        new ScenarioEvent { Type = "ReportNpc", NpcId = 13497, Selected = 0 }
                    ]
                },
                new QuestDriveStage { Name = "REWARD", Events = [] }
            ]
        },
        Criteria =
        [
            new QuestCompletedCriterion("quest-5531-completed", 5531),
            new AbilityLevelCriterion("fight-50", AbilityType.Fight, 50),
            new AbilityLevelCriterion("magic-50", AbilityType.Magic, 50),
            new AbilityLevelCriterion("love-50", AbilityType.Love, 50)
        ]
    };

    /// <summary>
    /// (c) Repeatable/daily quest cycle — cat-34 style with the Task(6)
    /// semantics (quest 1959, detail_id=6): completes once through the real
    /// paths; the engine refuses re-accept (REPEATABLE='f' + completed
    /// flag); the Task detail is NOT in the daily-reset family, so the flag
    /// survives ResetDailyQuests and re-accept stays refused — the character
    /// cycle is the daily board's honest loop.
    /// </summary>
    public static BotScenarioTemplate Cat34DailyCycle { get; } = new()
    {
        Name = "cat34-daily",
        Description = "Cat-34 daily cycle (detail Task=6): quest 1959 completes, re-accept refused, survives daily reset — the character cycle.",
        Race = Race.Nuian,
        Gender = Gender.Male,
        Level = 10,
        // kind-31 prereq: the cat-34 board's predecessor quest.
        QuestStates =
        [
            new QuestStateRig(1958, BotQuestPreState.Completed)
        ],
        // Gather objectives hydrate from ACTUAL inventory (gather acts read
        // the bag) — pre-stock the objective item through the normal items
        // path, exactly like the calibrated t4 manifest drive.
        StartingItems =
        [
            new ScenarioStockItem(15589, 3)
        ],
        GateChecks =
        [
            new PrereqGateCheck("prereq-1958", QuestId: 1959, "Npc", AcceptorId: 0, PrereqQuestId: 1958)
        ],
        Drive = new QuestDriveSpec
        {
            QuestId = 1959,
            AcceptorType = "Npc",
            AcceptorId = 0,
            Stages =
            [
                new QuestDriveStage { Name = "START", Events = [] },
                new QuestDriveStage
                {
                    Name = "PROGRESS",
                    Events =
                    [
                        new ScenarioEvent { Type = "ItemGather", ItemId = 15589, Count = 3 }
                    ]
                },
                new QuestDriveStage { Name = "REWARD", Events = [] }
            ]
        },
        Criteria =
        [
            new QuestCompletedCriterion("quest-1959-completed", 1959),
            new QuestNotActiveCriterion("quest-1959-not-active", 1959),
            new ReAcceptRefusedCriterion("reaccept-refused", 1959, "Item", AcceptorId: 8017)
        ]
    };

    /// <summary>
    /// Lane D auction-house conservation scenario (t_52b2b084) — the first
    /// scripted fleet consumer. The template's quest Drive is a placeholder
    /// (never executed): BotScenarioRunner routes this name to
    /// <see cref="AuctionHouseScenario"/> before the quest machinery.
    /// Declared BEFORE the Library so static-init ordering keeps the
    /// dictionary initializer from reading a null property.
    /// </summary>
    public static BotScenarioTemplate AuctionHouseConservation { get; } = new()
    {
        Name = AuctionHouseScenario.ScenarioName,
        Description = "Auction-house conservation: a fleet posts and buys lots through the contract actions; items/currency conserved (documented engine sinks only), per-action trace complete.",
        Race = Race.Nuian,
        Gender = Gender.Male,
        Level = 1,
        Drive = new QuestDriveSpec
        {
            QuestId = 0,
            AcceptorType = nameof(QuestAcceptorType.Npc),
            AcceptorId = 0,
            Stages = []
        }
    };

    /// <summary>
    /// BACKTRACK Phase 1 (t_61a0eebb): M1/M2 contract replay — the curated
    /// Solzreed golden route (16 quests through the first-mount chain)
    /// driven headless through IGameplayActor CONTRACT ACTIONS ONLY
    /// (accept/advance/use_item/turn_in/auto_turn_in/mount). The runner
    /// routes this name to <see cref="M1M2ReplayScenario"/> before the
    /// quest machinery — the same dispatch pattern as the auction-house
    /// scenario. Drive placeholder never executes.
    /// </summary>
    public static BotScenarioTemplate M1M2Replay { get; } = new()
    {
        Name = M1M2ReplayScenario.ScenarioName,
        Description = "BACKTRACK Phase 1: M1 route + M2 baseline replay — curated Solzreed golden route through contract actions only; proxy/bot-functional evidence, H stays UNKNOWN.",
        Race = Race.Nuian,
        Gender = Gender.Male,
        Level = 6,
        Drive = new QuestDriveSpec
        {
            QuestId = 0,
            AcceptorType = nameof(QuestAcceptorType.Npc),
            AcceptorId = 0,
            Stages = []
        }
    };

    /// <summary>
    /// BACKTRACK Phase 1 (t_61a0eebb) — MINIMUM SLICE template (Aya's
    /// narrow-scope directive): ONE canonical M1 action (quest 251 full
    /// spine) + ONE M2 action (mount segment) through the control-plane
    /// API end-to-end, with request/response traces + bot-side observation
    /// deltas as the evidence packet. Same dispatch pattern as the full
    /// replay; the runner routes this name to
    /// <see cref="M1M2ReplayScenario.RunMinSlice"/>.
    /// </summary>
    public static BotScenarioTemplate M1M2MinSlice { get; } = new()
    {
        Name = M1M2ReplayScenario.MinSliceScenarioName,
        Description = "BACKTRACK Phase 1 min slice: one canonical M1 action (quest 251 accept→advance→turn-in) + one M2 action (mount segment) through the control-plane API, with trace + observation evidence; H stays UNKNOWN.",
        Race = Race.Nuian,
        Gender = Gender.Male,
        Level = 6,
        Drive = new QuestDriveSpec
        {
            QuestId = 0,
            AcceptorType = nameof(QuestAcceptorType.Npc),
            AcceptorId = 0,
            Stages = []
        }
    };

    /// <summary>
    /// The library — templates by name.
    /// </summary>
    public static IReadOnlyDictionary<string, BotScenarioTemplate> Library { get; } =
        new Dictionary<string, BotScenarioTemplate>(StringComparer.Ordinal)
        {
            [Level22QuestGate.Name] = Level22QuestGate,
            [AbilityPrerequisiteGate.Name] = AbilityPrerequisiteGate,
            [Cat34DailyCycle.Name] = Cat34DailyCycle,
            [AuctionHouseConservation.Name] = AuctionHouseConservation,
            [M1M2Replay.Name] = M1M2Replay,
            [M1M2MinSlice.Name] = M1M2MinSlice
        };

    public static BotScenarioTemplate? Get(string name)
        => Library.TryGetValue(name, out var template) ? template : null;
}
