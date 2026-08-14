using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Parameterized bot test rig (P1 t_5efae4f1 — Josh's test-template vision,
/// 2026-08-08): "set bots up to certain level, skills, prerequisites for
/// quests having them test things."
///
/// A template declares EVERYTHING the bot needs to exist as a living test
/// harness: the rig (level, race/class, ability set + levels, learned
/// skills, starting items, pre-seeded quest state, zone + position), the
/// scenario it executes (target quest + acceptor + ordered drive stages),
/// and the acceptance criteria that decide PASS/FAIL.
///
/// Templates are reusable scenario definitions: the same template can
/// provision any number of bots (unit rig, gate stage, future MCP control)
/// and every run produces the same structured verdict. The bot is the
/// harness; the template is the test.
///
/// All rigging flows through NORMAL gameplay surfaces (the pilot's
/// discipline): level/abilities via the ordinary character record,
/// skills via CharacterSkills.AddSkill, items via AcquireDefaultItem,
/// quest state via the engine's own AddQuest / SetCompletedQuestFlag APIs.
/// No bot-only state, no direct DB writes, no quest-engine bypass.
/// </summary>
public sealed class BotScenarioTemplate
{
    /// <summary>Template id (library key, e.g. "level22-gate").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable purpose (rendered into evidence).</summary>
    public string Description { get; init; } = "";

    /// <summary>Character race (race gates + mother faction on the character).</summary>
    public Race Race { get; init; } = Race.Nuian;

    public Gender Gender { get; init; } = Gender.Male;

    /// <summary>Starting level (level gates evaluate against it).</summary>
    public byte Level { get; init; } = 1;

    /// <summary>The three ability trees (Ability1..3 — the "class" of the bot).</summary>
    public List<AbilityType> AbilityTrees { get; init; } = [];

    /// <summary>Ability exp rig: each entry is raised to at least this level
    /// via the ordinary ability surface (CharacterAbilities).</summary>
    public Dictionary<AbilityType, byte> AbilityLevels { get; init; } = [];

    /// <summary>Skills learned at rig time via CharacterSkills.AddSkill (the
    /// normal learn path).</summary>
    public List<uint> Skills { get; init; } = [];

    /// <summary>Starting inventory via AcquireDefaultItem (normal items path).</summary>
    public List<ScenarioStockItem> StartingItems { get; init; } = [];

    /// <summary>Quest state rigged BEFORE the drive: accepted quests, quests
    /// driven to Ready, and completed flags (kind-31 prerequisites).</summary>
    public List<QuestStateRig> QuestStates { get; init; } = [];

    /// <summary>Zone the bot is placed in (character Transform.ZoneId).</summary>
    public uint ZoneId { get; init; }

    /// <summary>World position (character Transform.Local.Position).</summary>
    public Vector3? Position { get; init; }

    /// <summary>Rigged starting copper (ordinary character record — the bank
    /// deposit/withdraw actions read/write the same balance the client
    /// sees).</summary>
    public long Money { get; init; }

    /// <summary>The scenario drive: EXACTLY ONE of the quest drive or the
    /// economy replay drive. Quest templates carry
    /// <see cref="QuestDriveSpec"/>; M5.1 economy templates carry
    /// <see cref="EconomyDriveSpec"/>.</summary>
    public QuestDriveSpec? Drive { get; init; }

    /// <summary>
    /// Economy replay drive (M5.1, t_7c224245 — the Phase 2 M3a/M4
    /// economic-replay hook): ordered steps of Deposit/Withdraw events
    /// fired through the actor contract (real engine paths), each verified
    /// Completed before the next step runs.
    /// </summary>
    public EconomyDriveSpec? EconomyDrive { get; init; }

    /// <summary>Negative gate probes run BEFORE the drive (each must be
    /// REFUSED by the engine or the template fails).</summary>
    public List<ScenarioGateCheck> GateChecks { get; init; } = [];

    /// <summary>Acceptance criteria verified AFTER the drive (each must
    /// hold or the template fails with a §17 reason).</summary>
    public List<ScenarioCriterion> Criteria { get; init; } = [];
}

/// <summary>One starting-item entry (normal items path).</summary>
public sealed record ScenarioStockItem(uint ItemId, int Count, byte Grade = 0);

/// <summary>Pre-seeded quest state (engine surfaces only).</summary>
public enum BotQuestPreState : byte
{
    /// <summary>Quest in ActiveQuests (accepted through the real gate).</summary>
    Accepted = 1,

    /// <summary>Accepted + advanced to Ready (engine step machine).</summary>
    Ready = 2,

    /// <summary>Completed flag set (SetCompletedQuestFlag — kind-31 prereq chains).</summary>
    Completed = 3,
}

/// <summary>One quest-state rig entry.</summary>
public sealed record QuestStateRig(uint QuestId, BotQuestPreState State);

/// <summary>
/// The scenario definition: which quest, accepted how, and the ordered
/// drive stages. Stage semantics mirror the calibrated scenario harness:
/// fire the stage's events, then ONE step-machine advance, then record.
/// </summary>
public sealed class QuestDriveSpec
{
    public required uint QuestId { get; init; }

    /// <summary>QuestAcceptorType name ("Npc"/"Item"/"Doodad"/"Sphere").</summary>
    public string AcceptorType { get; init; } = nameof(QuestAcceptorType.Npc);

    public uint AcceptorId { get; init; }

    /// <summary>Ordered drive stages (START/SUPPLY/PROGRESS/READY/REWARD…).</summary>
    public List<QuestDriveStage> Stages { get; init; } = [];
}

/// <summary>One drive stage: events to fire, then one advance.</summary>
public sealed class QuestDriveStage
{
    public string Name { get; init; } = "";

    public List<ScenarioEvent> Events { get; init; } = [];
}

/// <summary>One quest drive stage's events and the post-event advance.</summary>
public sealed class EconomyDriveSpec
{
    /// <summary>Ordered economy steps (DepositMoney / WithdrawMoney /
    /// DepositItem / WithdrawItem events through the actor contract).</summary>
    public List<EconomyDriveStep> Steps { get; init; } = [];
}

/// <summary>One economy replay step: events fired, then verified
/// Completed before the next step runs.</summary>
public sealed class EconomyDriveStep
{
    public string Name { get; init; } = "";

    public List<ScenarioEvent> Events { get; init; } = [];
}

/// <summary>
/// One world event (the same vocabulary the world interaction pipeline
/// fires — PlayerBotController event surface). Report events resolve their
/// target through the scenario world adapter and drive the REAL turn-in
/// path (DoReportEvents).
/// </summary>
public sealed class ScenarioEvent
{
    /// <summary>Event type: MonsterHunt, MonsterGroupHunt, ItemGather,
    /// ItemGroupGather, ItemUse, ItemGroupUse, Talk, TalkNpcGroup,
    /// Interaction, EnterSphere, Craft, ReportNpc, ReportDoodad,
    /// ReportJournal, ExpressFire, LevelUp, Aggro, ZoneKill,
    /// CinemaStarted, CinemaEnded, DepositMoney, WithdrawMoney,
    /// DepositItem, WithdrawItem (M5.1 economy events fired through the
    /// actor contract).</summary>
    public required string Type { get; init; }

    public uint NpcId { get; init; }

    public uint ItemId { get; init; }

    public uint ItemGroupId { get; init; }

    public uint NpcGroupId { get; init; }

    public uint DoodadId { get; init; }

    public uint EmotionId { get; init; }

    public uint ZoneGroupId { get; init; }

    public uint ComponentId { get; init; }

    public uint CraftId { get; init; }

    public uint CinemaId { get; init; }

    /// <summary>Event count (RC-4 classes credit +1 per event — loop count times).</summary>
    public int Count { get; init; } = 1;

    /// <summary>Report selection index (ReportNpc/ReportDoodad; -1 = default).</summary>
    public int Selected { get; init; } = -1;

    /// <summary>Copper amount (DepositMoney/WithdrawMoney events).</summary>
    public long Amount { get; init; }
}

/// <summary>
/// A negative gate probe: the engine MUST refuse something on this bot
/// state (accept below a level gate, accept without a completed prereq,
/// accept below an ability gate). A probe that ACCEPTS fails the template
/// — the gate is the thing under test.
/// </summary>
public abstract record ScenarioGateCheck(string Name);

/// <summary>Accept must be REFUSED when the character level &lt; RefusedBelow.</summary>
public sealed record LevelGateCheck(string Name, uint QuestId, string AcceptorType, uint AcceptorId, byte RefusedBelow)
    : ScenarioGateCheck(Name);

/// <summary>Accept must be REFUSED when the given ability is below RefusedBelow.</summary>
public sealed record AbilityGateCheck(string Name, uint QuestId, string AcceptorType, uint AcceptorId,
    AbilityType Ability, byte RefusedBelow) : ScenarioGateCheck(Name);

/// <summary>Accept must be REFUSED while the prereq quest is NOT completed.</summary>
public sealed record PrereqGateCheck(string Name, uint QuestId, string AcceptorType, uint AcceptorId,
    uint PrereqQuestId) : ScenarioGateCheck(Name);

/// <summary>
/// A post-drive acceptance criterion (positive check). Every criterion must
/// hold for the template to PASS.
/// </summary>
public abstract record ScenarioCriterion(string Name);

/// <summary>Quest is no longer active AND its completed flag is set.</summary>
public sealed record QuestCompletedCriterion(string Name, uint QuestId) : ScenarioCriterion(Name);

/// <summary>Quest is NOT in ActiveQuests (terminal state check).</summary>
public sealed record QuestNotActiveCriterion(string Name, uint QuestId) : ScenarioCriterion(Name);

/// <summary>Character level is at least the given level.</summary>
public sealed record LevelAtLeastCriterion(string Name, byte Level) : ScenarioCriterion(Name);

/// <summary>Inventory holds at least Count of the item.</summary>
public sealed record ItemHeldCriterion(string Name, uint ItemId, int Count) : ScenarioCriterion(Name);

/// <summary>The character knows the skill (CharacterSkills).</summary>
public sealed record SkillKnownCriterion(string Name, uint SkillId) : ScenarioCriterion(Name);

/// <summary>The given ability is at least the given level (GetAbilityLevel).</summary>
public sealed record AbilityLevelCriterion(string Name, AbilityType Ability, byte Level) : ScenarioCriterion(Name);

/// <summary>Re-accept after completion must be REFUSED by the engine
/// (repeatable/daily gate — the daily-cycle semantics under test).</summary>
public sealed record ReAcceptRefusedCriterion(string Name, uint QuestId, string AcceptorType, uint AcceptorId)
    : ScenarioCriterion(Name);

/// <summary>Bank (Money2) balance equals the expected copper amount
/// (M5.1 deposit/withdraw acceptance).</summary>
public sealed record BankMoneyCriterion(string Name, long Expected) : ScenarioCriterion(Name);

/// <summary>A named container holds exactly the expected count of a
/// template (M5.1 deposit/withdraw acceptance — distinguishes bag vs bank
/// holdings, which the total ItemHeldCriterion cannot).</summary>
public sealed record ContainerItemCriterion(string Name, SlotType Container, uint ItemId, int Count)
    : ScenarioCriterion(Name);
