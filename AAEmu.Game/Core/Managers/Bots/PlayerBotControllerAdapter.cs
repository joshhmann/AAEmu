using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M5 contract adapter over the M2b pilot controller (ARCHITECTURE_REVIEW
/// deliverable 3-5: "PlayerBotController becomes its first adapter").
///
/// The pilot PlayerBotController already drives REAL engine paths (quest
/// accept/progress/turn-in through CharacterQuests + QuestManager). This
/// adapter exposes that controller through the <see cref="IGameplayActor"/>
/// contract — the M5 action surface (Observe/Move/Stop/Target/Cast) plus
/// the full quest-drive surface — so behavior layers and later tests speak
/// ONE vocabulary and every action lands on the actor's lifecycle + audit
/// trace. The controller itself is untouched; composition, no rewrite.
///
/// Single-writer rule (ROADMAP M5): all world/character mutation flows
/// through the actor (ActiveRequest gate). The quest surface delegates to
/// the pilot's engine paths unchanged.
/// </summary>
public sealed class PlayerBotControllerAdapter : IGameplayActor
{
    /// <summary>The wrapped pilot controller (quest engine paths).</summary>
    public PlayerBotController Controller { get; }

    /// <summary>The actor contract implementation (lifecycle + audit + movement/target/cast).</summary>
    public GameplayActor Actor { get; }

    public PlayerBotControllerAdapter(PlayerBotController controller)
    {
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Actor = new GameplayActor(controller.Character);
    }

    #region IGameplayActor delegation

    public uint ActorId => Actor.ActorId;
    public Character Character => Actor.Character;
    public ActorRequest? ActiveRequest => Actor.ActiveRequest;
    public IReadOnlyList<ActorAuditRecord> AuditTrace => Actor.AuditTrace;

    public ActorObservation Observe() => Actor.Observe();

    public ActorRequest MoveTo(System.Numerics.Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        => Actor.MoveTo(destination, speed, timeout, idempotencyKey);

    public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        => Actor.MoveToUnit(targetObjId, speed, timeout, idempotencyKey);

    public ActorRequest Stop() => Actor.Stop();

    public ActorRequest SetTarget(uint targetObjId) => Actor.SetTarget(targetObjId);

    public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
        => Actor.Cast(skillId, targetObjId, idempotencyKey);

    public bool Interrupt(Guid traceId) => Actor.Interrupt(traceId);

    public ActorRequest AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null)
        => Actor.AcceptQuest(questId, acceptorType, acceptorId, idempotencyKey);

    public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null)
        => Actor.AdvanceQuest(questId, idempotencyKey);

    public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null)
        => Actor.TurnInQuest(questId, npcObjId, selectedReward, idempotencyKey);

    public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null)
        => Actor.TurnInAtDoodad(questId, doodadObjId, selectedReward, idempotencyKey);

    public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null)
        => Actor.AutoTurnInQuest(questId, selectedReward, idempotencyKey);

    public ActorRequest Interact(uint targetObjId, string? idempotencyKey = null)
        => Actor.Interact(targetObjId, idempotencyKey);

    public ActorRequest Loot(uint corpseObjId, string? idempotencyKey = null)
        => Actor.Loot(corpseObjId, idempotencyKey);

    public ActorRequest UseItem(uint itemId, string? idempotencyKey = null)
        => Actor.UseItem(itemId, idempotencyKey);

    public ActorRequest Mount(uint mountObjId, string? idempotencyKey = null)
        => Actor.Mount(mountObjId, idempotencyKey);

    public ActorRequest Dismount(string? idempotencyKey = null)
        => Actor.Dismount(idempotencyKey);

    public ActorAuditRecord? FindByKey(string idempotencyKey) => Actor.FindByKey(idempotencyKey);

    public void Tick(TimeSpan elapsed) => Actor.Tick(elapsed);

    #endregion

    #region Quest-drive surface (pilot engine paths, unchanged)

    /// <summary>
    /// Raw bool accept (the pilot controller's convenience surface — the
    /// contract's validated <see cref="IGameplayActor.AcceptQuest"/> is the
    /// preferred entry). Renamed from AcceptQuest when the contract gained
    /// the validated request overload.
    /// </summary>
    public bool TryAcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId)
        => Controller.AcceptQuest(questId, acceptorType, acceptorId);

    public bool AcceptFromNpc(uint questId, uint npcObjId)
        => Controller.AcceptFromNpc(questId, npcObjId);

    public bool AcceptFromDoodad(uint questId, uint doodadObjId)
        => Controller.AcceptFromDoodad(questId, doodadObjId);

    public bool IsActive(uint questId) => Controller.IsActive(questId);

    public bool HasCompleted(uint questId) => Controller.HasCompleted(questId);

    public Quest ActiveQuest(uint questId) => Controller.ActiveQuest(questId);

    public void Advance(uint questId) => Controller.Advance(questId);

    public void KillNpc(uint npcId, int count = 1) => Controller.KillNpc(npcId, count);

    public void KillNpcGroup(uint monsterGroupId, int count = 1)
        => Controller.KillNpcGroup(monsterGroupId, count);

    public void GatherItem(uint questId, uint itemId, int count = 1)
        => Controller.GatherItem(questId, itemId, count);

    public void UseItem(uint itemId, int times = 1) => Controller.UseItem(itemId, times);

    public void TalkToNpc(uint questId, uint npcId) => Controller.TalkToNpc(questId, npcId);

    public void InteractWithDoodad(uint doodadId, int times = 1)
        => Controller.InteractWithDoodad(doodadId, times);

    public void EnterSphere(uint questId, uint componentId)
        => Controller.EnterSphere(questId, componentId);

    public void ExpressEmotion(uint npcId, uint emotionId)
        => Controller.ExpressEmotion(npcId, emotionId);

    public void LevelUp() => Controller.LevelUp();

    public void AggroNpc(uint npcId) => Controller.AggroNpc(npcId);

    public void ZoneKill(uint zoneGroupId) => Controller.ZoneKill(zoneGroupId);

    public void CinemaStarted(uint cinemaId) => Controller.CinemaStarted(cinemaId);

    public void CinemaEnded(uint cinemaId) => Controller.CinemaEnded(cinemaId);

    public Quest ReportTurnIn(uint questId, uint npcObjId, int selectedReward = -1)
        => Controller.ReportTurnIn(questId, npcObjId, selectedReward);

    public Quest ReportDoodadTurnIn(uint questId, uint doodadObjId, int selectedReward = -1)
        => Controller.ReportDoodadTurnIn(questId, doodadObjId, selectedReward);

    public Quest AutoTurnIn(uint questId, int selectedReward = -1)
        => Controller.AutoTurnIn(questId, selectedReward);

    public void StockInventory(uint itemTemplateId, int count, byte grade = 0)
        => Controller.StockInventory(itemTemplateId, count, grade);

    public int InventoryCount(uint itemTemplateId) => Controller.InventoryCount(itemTemplateId);

    #endregion
}
