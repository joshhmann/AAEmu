using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
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

    public ActorRequest NavigateTo(System.Numerics.Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        => Actor.NavigateTo(destination, speed, timeout, idempotencyKey);

    public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        => Actor.MoveToUnit(targetObjId, speed, timeout, idempotencyKey);

    public ActorRequest DriveVehicle(uint vehicleObjId, System.Numerics.Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        => Actor.DriveVehicle(vehicleObjId, destination, speed, timeout, idempotencyKey);

    public ActorRequest Stop() => Actor.Stop();

    public ActorRequest SetTarget(uint targetObjId) => Actor.SetTarget(targetObjId);

    public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
        => Actor.Cast(skillId, targetObjId, idempotencyKey);

    public ActorRequest CastAt(uint skillId, System.Numerics.Vector3 position, string? idempotencyKey = null)
        => Actor.CastAt(skillId, position, idempotencyKey);

    public ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null)
        => Actor.Interact(doodadObjId, skillId, idempotencyKey);

    public ActorRequest Loot(uint lootOwnerObjId, string? idempotencyKey = null)
        => Actor.Loot(lootOwnerObjId, idempotencyKey);

    public ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null)
        => Actor.UseItem(itemTemplateId, targetObjId, idempotencyKey);

    public ActorRequest Equip(uint itemTemplateId, string? idempotencyKey = null)
        => Actor.Equip(itemTemplateId, idempotencyKey);

    public ActorRequest PartyInvite(uint targetCharacterObjId, string? idempotencyKey = null)
        => Actor.PartyInvite(targetCharacterObjId, idempotencyKey);

    public ActorRequest PartyAccept(string? idempotencyKey = null)
        => Actor.PartyAccept(idempotencyKey);

    public ActorRequest ExpeditionCreate(string name, string? idempotencyKey = null)
        => Actor.ExpeditionCreate(name, idempotencyKey);

    public ActorRequest ExpeditionInvite(string invitedName, string? idempotencyKey = null)
        => Actor.ExpeditionInvite(invitedName, idempotencyKey);

    public ActorRequest ExpeditionAccept(Models.StaticValues.FactionsEnum expeditionId, uint inviterId, string? idempotencyKey = null)
        => Actor.ExpeditionAccept(expeditionId, inviterId, idempotencyKey);

    public ActorRequest ExpeditionLeave(string? idempotencyKey = null)
        => Actor.ExpeditionLeave(idempotencyKey);

    public ActorRequest TradeOffer(uint targetCharacterObjId, string? idempotencyKey = null)
        => Actor.TradeOffer(targetCharacterObjId, idempotencyKey);

    public ActorRequest TradePutup(uint itemTemplateId, int count, string? idempotencyKey = null)
        => Actor.TradePutup(itemTemplateId, count, idempotencyKey);

    public ActorRequest TradeLockOk(string? idempotencyKey = null)
        => Actor.TradeLockOk(idempotencyKey);

    public ActorRequest Mount(uint mateObjId, string? idempotencyKey = null)
        => Actor.Mount(mateObjId, idempotencyKey);

    public ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null)
        => Actor.Dismount(mateObjId, idempotencyKey);

    public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null)
        => Actor.Craft(craftId, doodadObjId, timeout, idempotencyKey);

    public ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null)
        => Actor.BoardVehicle(vehicleObjId, attachPoint, idempotencyKey);

    public ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null)
        => Actor.UnboardVehicle(vehicleObjId, idempotencyKey);

    public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null)
        => Actor.Harvest(doodadObjId, idempotencyKey);

    public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null)
        => Actor.PackPickup(doodadObjId, idempotencyKey);

    public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null)
        => Actor.PutDown(packItemTemplateId, idempotencyKey);

    public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null)
        => Actor.LoadPackOntoVehicle(slaveObjId, placedPackDoodadObjId, idempotencyKey);

    public ActorRequest Plant(uint seedItemTemplateId, System.Numerics.Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null)
        => Actor.Plant(seedItemTemplateId, position, zRot, scale, idempotencyKey);

    public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, System.Numerics.Vector3 position, float zRot = 0f, string? idempotencyKey = null)
        => Actor.BuildHouse(designId, designItemTemplateId, position, zRot, idempotencyKey);

    public ActorRequest DepositMoney(long amount, string? idempotencyKey = null)
        => Actor.DepositMoney(amount, idempotencyKey);

    public ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null)
        => Actor.WithdrawMoney(amount, idempotencyKey);

    public ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null)
        => Actor.DepositItem(itemTemplateId, idempotencyKey);

    public ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null)
        => Actor.WithdrawItem(itemTemplateId, idempotencyKey);

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

    public ActorRequest DiscoverQuests(uint targetObjId, string? idempotencyKey = null)
        => Actor.DiscoverQuests(targetObjId, idempotencyKey);

    public ActorRequest InteractWith(uint doodadObjId, string? idempotencyKey = null)
        => Actor.InteractWith(doodadObjId, idempotencyKey);

    public ActorRequest Talk(uint npcObjId, string? idempotencyKey = null)
        => Actor.Talk(npcObjId, idempotencyKey);

    public ActorRequest DiscoverSelfQuests(string? idempotencyKey = null)
        => Actor.DiscoverSelfQuests(idempotencyKey);

    public ActorRequest PlayCinema(uint cinemaId, string? idempotencyKey = null)
        => Actor.PlayCinema(cinemaId, idempotencyKey);


    public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null)
        => Actor.Buy(merchantNpcObjId, itemTemplateId, count, idempotencyKey);

    public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null)
        => Actor.Sell(merchantNpcObjId, itemId, idempotencyKey);
    public ActorRequest SellSpecialty(uint merchantNpcObjId, string? idempotencyKey = null)
        => Actor.SellSpecialty(merchantNpcObjId, idempotencyKey);

    public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, Models.Game.Auction.AuctionDuration duration, string? idempotencyKey = null)
        => Actor.PostAuction(itemId, startPrice, buyoutPrice, duration, idempotencyKey);

    public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null)
        => Actor.BuyAuction(lotId, price, idempotencyKey);

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
