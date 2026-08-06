using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Additive quest-drive-only player bot controller (M2b / M6.0-lite slice).
///
/// Drives an ordinary Character through the real quest engine:
///   - accept  -> CharacterQuests.AddQuest  (real gates: level, race,
///               quest-completion chains, repeatable/daily re-accept checks)
///   - progress -> the engine's own UnitEvents surface (the same handlers the
///               world interaction pipeline fires: kills, gathers, talks,
///               interactions, cinema, report events)
///   - turn-in -> QuestManager.DoReportEvents (the exact path the
///               CSCompleteQuestContextPacket client packet takes)
///
/// The controller holds NO quest state of its own beyond a read-only view of
/// the character's real state; every mutation flows through normal gameplay
/// services. No bot-only quest state, no direct DB writes, no quest-engine
/// bypass (AGENTS.md #9/#10).
/// </summary>
public class PlayerBotController
{
    public Character Character { get; }

    public PlayerBotController(Character character)
    {
        Character = character;
    }

    #region Accept / state

    /// <summary>Real accept gate (repeatability checks included).</summary>
    public bool AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId)
        => Character.Quests.AddQuest(questId, false, acceptorType, acceptorId);

    public bool AcceptFromNpc(uint questId, uint npcObjId)
        => Character.Quests.AddQuestFromNpc(questId, npcObjId);

    public bool AcceptFromDoodad(uint questId, uint doodadObjId)
        => Character.Quests.AddQuestFromDoodad(questId, doodadObjId);

    public bool IsActive(uint questId) => Character.Quests.HasQuest(questId);

    public bool HasCompleted(uint questId) => Character.Quests.HasQuestCompleted(questId);

    public Quest ActiveQuest(uint questId) => Character.Quests.ActiveQuests.GetValueOrDefault(questId);

    /// <summary>
    /// Runs the step machine once on the given quest (the same evaluation the
    /// engine performs after events; safe to call repeatedly).
    /// </summary>
    public void Advance(uint questId)
    {
        var quest = ActiveQuest(questId);
        if (quest != null)
            _ = quest.RunCurrentStep();
    }

    #endregion

    #region Objective progress (engine event surface)

    public void KillNpc(uint npcId, int count = 1)
        => Character.Events.OnMonsterHunt(Character, new OnMonsterHuntArgs { NpcId = npcId, Count = (uint)count });

    public void KillNpcGroup(uint monsterGroupId, int count = 1)
        => Character.Events.OnMonsterGroupHunt(Character, new OnMonsterGroupHuntArgs { NpcId = monsterGroupId, Count = (uint)count });

    public void GatherItem(uint questId, uint itemId, int count = 1)
        => Character.Events.OnItemGather(Character, new OnItemGatherArgs { QuestId = questId, ItemId = itemId, Count = count });

    public void UseItem(uint itemId, int times = 1)
    {
        for (var i = 0; i < times; i++)
            Character.Events.OnItemUse(Character, new OnItemUseArgs { ItemId = itemId });
    }

    public void TalkToNpc(uint questId, uint npcId)
        => Character.Events.OnTalkMade(Character, new OnTalkMadeArgs { QuestId = questId, NpcId = npcId, SourcePlayer = Character });

    public void InteractWithDoodad(uint doodadId, int times = 1)
    {
        for (var i = 0; i < times; i++)
            Character.Events.OnInteraction(Character, new OnInteractionArgs { DoodadId = doodadId, SourcePlayer = Character });
    }

    public void EnterSphere(uint questId, uint componentId)
        => Character.Events.OnEnterSphere(Character, new OnEnterSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = questId, ComponentId = componentId, Radius = 100f }
        });

    public void ExpressEmotion(uint npcId, uint emotionId)
        => Character.Events.OnExpressFire(Character, new OnExpressFireArgs { NpcId = npcId, EmotionId = emotionId });

    public void LevelUp()
        => Character.Events.OnLevelUp(Character, new OnLevelUpArgs());

    public void AggroNpc(uint npcId)
        => Character.Events.OnAggro(Character, new OnAggroArgs { NpcId = npcId });

    public void ZoneKill(uint zoneGroupId)
        => Character.Events.OnZoneKill(Character, new OnZoneKillArgs { ZoneGroupId = zoneGroupId, Killer = Character, Victim = Character });

    public void CinemaStarted(uint cinemaId)
        => Character.Events.OnCinemaStarted(Character, new OnCinemaStartedArgs { CinemaId = cinemaId });

    public void CinemaEnded(uint cinemaId)
        => Character.Events.OnCinemaEnded(Character, new OnCinemaEndedArgs { CinemaId = cinemaId });

    #endregion

    #region Turn-in (real packet path)

    /// <summary>
    /// Turns a quest in at an NPC — the exact path CSCompleteQuestContextPacket
    /// uses. Returns the quest after the reward step ran (null when not active).
    /// </summary>
    public Quest ReportTurnIn(uint questId, uint npcObjId, int selectedReward = -1)
    {
        QuestManager.Instance.DoReportEvents(Character, questId, npcObjId, 0, selectedReward);
        return ActiveQuest(questId);
    }

    /// <summary>Turn-in at a doodad (real packet path).</summary>
    public Quest ReportDoodadTurnIn(uint questId, uint doodadObjId, int selectedReward = -1)
    {
        QuestManager.Instance.DoReportEvents(Character, questId, 0, doodadObjId, selectedReward);
        return ActiveQuest(questId);
    }

    /// <summary>Auto-complete (no NPC/doodad target) — the real packet path's third branch.</summary>
    public Quest AutoTurnIn(uint questId, int selectedReward = -1)
    {
        QuestManager.Instance.DoReportEvents(Character, questId, 0, 0, selectedReward);
        return ActiveQuest(questId);
    }

    #endregion

    #region Inventory (normal gameplay services)

    /// <summary>Stocks items through ItemManager.Create + AcquireDefaultItem
    /// (the same acquisition path the engine uses for quest supplies).</summary>
    public void StockInventory(uint itemTemplateId, int count, byte grade = 0)
    {
        Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.QuestSupplyItems, itemTemplateId, count, grade);
    }

    public int InventoryCount(uint itemTemplateId)
        => Character.Inventory.GetItemsCount(itemTemplateId);

    #endregion
}
