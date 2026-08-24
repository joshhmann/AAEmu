using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Utils;
using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Outcome of a trade confirm (lock + ok) attempt — the observable result
/// surface for the confirm half of the handshake.
/// </summary>
public enum TradeConfirmResult
{
    /// <summary>The actor has no active trade session.</summary>
    NotInTrade,

    /// <summary>Neither side has locked the offer, so ok cannot be recorded.</summary>
    NotLocked,

    /// <summary>
    /// A receiver lacks inventory space for the swap. The trade was
    /// canceled (fail-closed) BEFORE any item or money moved.
    /// </summary>
    RefusedNoSpace,

    /// <summary>This side's ok is recorded; the counterpart has not confirmed yet.</summary>
    OkedAwaitingOther,

    /// <summary>Both sides confirmed; items and money changed hands.</summary>
    Finished
}

/// <summary>
/// One line on a trade window: up to <see cref="Count"/> units taken from the
/// putter's stack instance <see cref="Item"/>. A partial entry (Count less
/// than the stack's Count) splits at finish time; a full entry moves the
/// whole instance.
/// </summary>
public class TradeItemEntry(Item item, int count)
{
    public Item Item { get; } = item;

    /// <summary>Units offered from the source stack.</summary>
    public int Count { get; set; } = count;
}

public class TradeTemplate
{
    public uint Id { get; set; }
    public uint OwnerObjId { get; set; }
    public uint TargetObjId { get; set; }
    public bool LockOwner { get; set; }
    public bool LockTarget { get; set; }
    public bool OkOwner { get; set; }
    public bool OkTarget { get; set; }
    public List<TradeItemEntry> OwnerItems { get; set; }
    public List<TradeItemEntry> TargetItems { get; set; }
    public int OwnerMoneyPutup { get; set; }
    public int TargetMoneyPutup { get; set; }
}

public class TradeManager(ITradeIdManager tradeIdManager, IWorldManager worldManager) : Singleton<TradeManager>, ITradeManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<uint, TradeTemplate> _trades = [];

    /// <summary>
    /// Maximum player-to-player trade distance in metres. NOTE: no canonical
    /// 1.2 data table defines a trade range; 5 m mirrors the engine's other
    /// adjacency conventions (DefaultCraftRange / AI GreetRange).
    /// </summary>
    public const float MaxTradeRange = 5f;

    private uint GetTradeId(uint objId)
    {
        if (_trades.Count > 0)
        {
            foreach (var (key, value) in _trades)
            {
                if (value.OwnerObjId.Equals(objId)) return key;
                if (value.TargetObjId.Equals(objId)) return key;
            }
        }

        return 0;
    }

    private bool IsTrading(uint objId)
    {
        var tradeId = GetTradeId(objId);
        if (tradeId == 0) return false;

        CancelTrade(objId, 0, tradeId); // TODO - reason?
        return true;
    }

    private void UnlockTrade(Character owner, Character target, uint tradeId)
    {
        if (!_trades[tradeId].LockOwner && !_trades[tradeId].LockTarget) return;

        _trades[tradeId].LockOwner = false;
        _trades[tradeId].LockTarget = false;
        _trades[tradeId].OkOwner = false;
        _trades[tradeId].OkTarget = false;
        owner.SendPacket(new SCTradeLockUpdatePacket(false, false));
        target.SendPacket(new SCTradeLockUpdatePacket(false, false));
        Logger.Info("Trade Id:{0} Lockers opened and Ok undone.", tradeId);
    }

    private static bool IsInRange(Character a, Character b)
        => MathUtil.CalculateDistance(a.Transform.World.Position, b.Transform.World.Position, true) <= MaxTradeRange;

    // ------------------------------------------------------------------ queries

    /// <summary>True when objId participates in an active trade session.</summary>
    public bool IsInTrade(uint objId) => GetTradeId(objId) != 0;

    /// <summary>True when either side of objId's active trade has locked the offer.</summary>
    public bool IsTradeLocked(uint objId)
        => _trades.TryGetValue(GetTradeId(objId), out var trade) && (trade.LockOwner || trade.LockTarget);

    /// <summary>The entries objId currently has on its side of the trade window.</summary>
    public IReadOnlyList<TradeItemEntry> GetPutUpItems(uint objId)
    {
        if (!_trades.TryGetValue(GetTradeId(objId), out var trade))
            return [];
        return trade.OwnerObjId.Equals(objId) ? trade.OwnerItems : trade.TargetItems;
    }

    // ------------------------------------------------------------------ handshake

    public void CanStartTrade(Character owner, Character target) => TryCanStartTrade(owner, target);

    /// <summary>
    /// Void-returning <see cref="CanStartTrade"/> with an outcome — the
    /// packet handler ignores it; programmatic callers (bot actors) use it
    /// as the refusal signal (the offer itself leaves no queryable state).
    /// Faction/PvP gating intentionally NOT applied: canonical 1.2 rules are
    /// not cheaply derivable from data, so only distance is enforced here.
    /// </summary>
    public bool TryCanStartTrade(Character owner, Character target)
    {
        if (owner == null || target == null || ReferenceEquals(owner, target)) return false;
        if (IsTrading(owner.ObjId) || IsTrading(target.ObjId)) return false;
        if (!IsInRange(owner, target))
        {
            Logger.Info("{0}({1}) is too far from {2}({3}) to trade.", owner.Name, owner.ObjId, target.Name, target.ObjId);
            return false;
        }

        Logger.Info("{0}({1}) is trying to trade with {2}({3}).", owner.Name, owner.ObjId, target.Name, target.ObjId);
        target.SendPacket(new SCCanStartTradePacket(owner.ObjId));
        return true;
    }

    public void StartTrade(Character owner, Character target) => TryStartTrade(owner, target);

    /// <summary>Void-returning <see cref="StartTrade"/> with an outcome.</summary>
    public bool TryStartTrade(Character owner, Character target)
    {
        if (owner == null || target == null || ReferenceEquals(owner, target)) return false;
        if (IsTrading(owner.ObjId) || IsTrading(target.ObjId)) return false;
        if (!IsInRange(owner, target))
        {
            Logger.Info("{0}({1}) is too far from {2}({3}) to start trading.", owner.Name, owner.ObjId, target.Name, target.ObjId);
            return false;
        }

        var nextId = tradeIdManager.GetNextId();
        var template = new TradeTemplate
        {
            Id = nextId,
            OwnerObjId = owner.ObjId,
            TargetObjId = target.ObjId,
            LockOwner = false,
            LockTarget = false,
            OkOwner = false,
            OkTarget = false,
            OwnerItems = [],
            TargetItems = [],
            OwnerMoneyPutup = 0,
            TargetMoneyPutup = 0

        };
        _trades.Add(nextId, template);

        Logger.Info("Trade Id:{4} started between {0}({1}) - {2}({3}).", owner.Name, owner.ObjId, target.Name, target.ObjId, nextId);
        owner.SendPacket(new SCTradeStartedPacket(target.ObjId));
        target.SendPacket(new SCTradeStartedPacket(owner.ObjId));
        return true;
    }

    public void CancelTrade(uint objId, int reason, uint tradeId = 0u)
    {
        // TODO - All reasons.
        tradeId = tradeId == 0 ? GetTradeId(objId) : tradeId;
        if (tradeId == 0)
        {
            worldManager.GetCharacterByObjId(objId)?.SendPacket(new SCTradeCanceledPacket(reason, true));
            return;
        }

        if (!_trades.Remove(tradeId, out var trade))
            return; // already gone — nothing left to cancel

        var owner = worldManager.GetCharacterByObjId(trade.OwnerObjId);
        var target = worldManager.GetCharacterByObjId(trade.TargetObjId);

        Logger.Info("Trade Id:{4} between {0}({1}) - {2}({3}) is canceled.", owner?.Name, trade.OwnerObjId, target?.Name, trade.TargetObjId, tradeId);
        var causedByMe = trade.OwnerObjId.Equals(objId);
        owner?.SendPacket(new SCTradeCanceledPacket(reason, causedByMe));
        target?.SendPacket(new SCTradeCanceledPacket(reason, !causedByMe));
    }

    // ----------------------------------------------------------------- trade window

    public void AddItem(Character character, SlotType slotType, byte slot, int amount)
    {
        var tradeId = GetTradeId(character.ObjId);
        var item = character.Inventory.GetItem(slotType, slot);
        if (tradeId != 0 && item != null && amount > 0 && amount <= item.Count)
        {
            var isOwnerWhoAdd = _trades[tradeId].OwnerObjId.Equals(character.ObjId);
            var owner = worldManager.GetCharacterByObjId(_trades[tradeId].OwnerObjId);
            var target = worldManager.GetCharacterByObjId(_trades[tradeId].TargetObjId);

            // Count-split support: the entry records how many units of the
            // stack go to the OTHER side; the inventory itself is not touched
            // until the trade finishes (a cancel must leave it untouched).
            // Re-putup of the same instance updates the offered count instead
            // of stacking duplicate lines.
            var entries = isOwnerWhoAdd ? _trades[tradeId].OwnerItems : _trades[tradeId].TargetItems;
            var existing = entries.FirstOrDefault(e => e.Item.Id == item.Id);
            if (existing != null) existing.Count = amount;
            else entries.Add(new TradeItemEntry(item, amount));

            if (isOwnerWhoAdd)
            {
                Logger.Info("Trade Id:{0} {1}({2}) added item ({3}-{4}) Amount: {5}.", tradeId, owner.Name, owner.ObjId, slotType, slot, amount);
                owner.SendPacket(new SCTradeItemPutupPacket(slotType, slot, amount));
                target.SendPacket(new SCOtherTradeItemPutupPacket(item));
            }
            else
            {
                Logger.Info("Trade Id:{0} {1}({2}) added item ({3}-{4}) Amount: {5}.", tradeId, target.Name, target.ObjId, slotType, slot, amount);
                owner.SendPacket(new SCOtherTradeItemPutupPacket(item));
                target.SendPacket(new SCTradeItemPutupPacket(slotType, slot, amount));
            }

            // If trade was Locked, unlock both
            UnlockTrade(owner, target, tradeId);
        }
        else
        {
            CancelTrade(character.ObjId, 0, tradeId); // TODO - Reason
        }
    }

    public void AddMoney(Character character, int moneyAmount)
    {
        var tradeId = GetTradeId(character.ObjId);
        if (tradeId != 0 && character.Money >= moneyAmount)
        {
            var isOwnerWhoAdd = _trades[tradeId].OwnerObjId.Equals(character.ObjId);
            var owner = worldManager.GetCharacterByObjId(_trades[tradeId].OwnerObjId);
            var target = worldManager.GetCharacterByObjId(_trades[tradeId].TargetObjId);
            if (isOwnerWhoAdd)
            {
                Logger.Info("Trade Id:{0} {1}({2}) changed Money: {3}.", tradeId, owner.Name, owner.ObjId, moneyAmount);
                _trades[tradeId].OwnerMoneyPutup = moneyAmount;
                owner.SendPacket(new SCTradeMoneyPutupPacket(moneyAmount));
                target.SendPacket(new SCOtherTradeMoneyPutupPacket(moneyAmount));
            }
            else
            {
                Logger.Info("Trade Id:{0} {1}({2}) changed Money: {3}.", tradeId, target.Name, target.ObjId, moneyAmount);
                _trades[tradeId].TargetMoneyPutup = moneyAmount;
                owner.SendPacket(new SCOtherTradeMoneyPutupPacket(moneyAmount));
                target.SendPacket(new SCTradeMoneyPutupPacket(moneyAmount));
            }

            // If trade was Locked, unlock both
            UnlockTrade(owner, target, tradeId);
        }
        else
        {
            CancelTrade(character.ObjId, 0, tradeId); // TODO - Reason
        }
    }

    public void RemoveItem(Character character, SlotType slotType, byte slot)
    {
        var tradeId = GetTradeId(character.ObjId);
        var item = character.Inventory.GetItem(slotType, slot);
        if (tradeId != 0 && item != null)
        {
            var isOwnerWhoAdd = _trades[tradeId].OwnerObjId.Equals(character.ObjId);
            var owner = worldManager.GetCharacterByObjId(_trades[tradeId].OwnerObjId);
            var target = worldManager.GetCharacterByObjId(_trades[tradeId].TargetObjId);
            if (isOwnerWhoAdd)
            {
                Logger.Info("Trade Id:{0} {1}({2}) tookdown item ({3}-{4}).", tradeId, owner.Name, owner.ObjId, slotType, slot);
                _trades[tradeId].OwnerItems.RemoveAll(e => e.Item.Id == item.Id);
                owner.SendPacket(new SCTradeItemTookdownPacket(slotType, slot));
                target.SendPacket(new SCOtherTradeItemTookdownPacket(item));
            }
            else
            {
                Logger.Info("Trade Id:{0} {1}({2}) tookdown item ({3}-{4}).", tradeId, target.Name, target.ObjId, slotType, slot);
                _trades[tradeId].TargetItems.RemoveAll(e => e.Item.Id == item.Id);
                owner.SendPacket(new SCOtherTradeItemTookdownPacket(item));
                target.SendPacket(new SCTradeItemTookdownPacket(slotType, slot));
            }

            // If trade was Locked, unlock both
            UnlockTrade(owner, target, tradeId);
        }
        else
        {
            CancelTrade(character.ObjId, 0, tradeId); // TODO - Reason
        }
    }

    public void LockTrade(Character character, bool _lock)
    {
        var tradeId = GetTradeId(character.ObjId);
        if (tradeId != 0)
        {
            var isOwnerWhoAdd = _trades[tradeId].OwnerObjId.Equals(character.ObjId);

            // Check if already locked
            if (isOwnerWhoAdd && _trades[tradeId].LockOwner && _lock) return;
            if (!isOwnerWhoAdd && _trades[tradeId].LockTarget && _lock) return;

            var owner = worldManager.GetCharacterByObjId(_trades[tradeId].OwnerObjId);
            var target = worldManager.GetCharacterByObjId(_trades[tradeId].TargetObjId);

            if (!_lock)
            {
                _trades[tradeId].LockOwner = false;
                _trades[tradeId].LockTarget = false;
                Logger.Info("Trade Id:{0} {1}({2}) - {3}({4}) unlocked trade.", tradeId, owner.Name, owner.ObjId, target.Name, target.ObjId);
            }
            else if (isOwnerWhoAdd)
            {

                _trades[tradeId].LockOwner = true;
                Logger.Info("Trade Id:{0} {1}({2}) locked trade.", tradeId, owner.Name, owner.ObjId);
            }
            else
            {
                _trades[tradeId].LockTarget = true;
                Logger.Info("Trade Id:{0} {1}({2}) locked trade.", tradeId, target.Name, target.ObjId);
            }

            owner.SendPacket(new SCTradeLockUpdatePacket(_trades[tradeId].LockOwner, _trades[tradeId].LockTarget));
            target.SendPacket(new SCTradeLockUpdatePacket(_trades[tradeId].LockTarget, _trades[tradeId].LockOwner));
        }
        else
        {
            CancelTrade(character.ObjId, 0, tradeId); // TODO - Reason
        }
    }

    public void OkTrade(Character character) => ConfirmTrade(character);

    /// <summary>
    /// Records this side's ok (the CSTradeOkPacket path) and finishes the
    /// trade when both sides confirmed. Fail-closed: the inventory-space
    /// gate runs BEFORE any mutation, and a refused confirmation cancels the
    /// trade exactly once without corrupting the registry.
    /// </summary>
    public TradeConfirmResult ConfirmTrade(Character character)
    {
        var tradeId = GetTradeId(character.ObjId);
        if (tradeId == 0)
        {
            CancelTrade(character.ObjId, 0); // keeps the stray-ok client sync behavior
            return TradeConfirmResult.NotInTrade;
        }

        var trade = _trades[tradeId];
        var isOwnerWhoAdd = trade.OwnerObjId.Equals(character.ObjId);

        var owner = worldManager.GetCharacterByObjId(trade.OwnerObjId);
        var target = worldManager.GetCharacterByObjId(trade.TargetObjId);
        if (owner == null || target == null)
        {
            CancelTrade(character.ObjId, 0, tradeId);
            return TradeConfirmResult.RefusedNoSpace;
        }

        if (isOwnerWhoAdd)
        {
            trade.OkOwner = true;
            Logger.Info("Trade Id:{0} {1}({2}) ok trade.", tradeId, owner.Name, owner.ObjId);
        }
        else
        {
            trade.OkTarget = true;
            Logger.Info("Trade Id:{0} {1}({2}) ok trade.", tradeId, target.Name, target.ObjId);
        }

        // Send ok status
        owner.SendPacket(new SCTradeOkUpdatePacket(trade.OkOwner, trade.OkTarget));
        target.SendPacket(new SCTradeOkUpdatePacket(trade.OkTarget, trade.OkOwner));

        // If both locked AND both ok, finish the trade (canonical: a
        // one-sided lock or a one-sided ok must never be enough — the
        // previous !a && !b lock shape let a single locked side finish the
        // whole trade; the ok flags are recorded above so either side can
        // confirm first and await the counterpart).
        if (!(trade.LockOwner && trade.LockTarget && trade.OkOwner && trade.OkTarget))
            return TradeConfirmResult.OkedAwaitingOther;

        // Check inventory space BEFORE touching anything. One fail-closed
        // cancellation — never a partial cancel followed by a registry hit
        // on a removed id. Entries needing one slot each is a conservative
        // upper bound (partial splits can merge into existing stacks).
        if (owner.Inventory.FreeSlotCount(SlotType.Inventory) < trade.TargetItems.Count ||
            target.Inventory.FreeSlotCount(SlotType.Inventory) < trade.OwnerItems.Count)
        {
            CancelTrade(trade.OwnerObjId, 0, tradeId);
            return TradeConfirmResult.RefusedNoSpace;
        }

        return FinishTrade(owner, target, tradeId)
            ? TradeConfirmResult.Finished
            : TradeConfirmResult.RefusedNoSpace;
    }

    /// <summary>Exchanges money and items; returns false when the trade was refused instead.</summary>
    private bool FinishTrade(Character owner, Character target, uint tradeId)
    {
        if (!_trades.TryGetValue(tradeId, out var tradeInfo))
        {
            Logger.Warn("FinishTrade called for missing trade Id:{0} — ignored.", tradeId);
            return false;
        }

        // Validate Money (custom client protection)
        if (tradeInfo.OwnerMoneyPutup > owner.Money)
        {
            CancelTrade(owner.ObjId, 0, tradeId); // Reason?
            Logger.Error($"{owner.Name} ({owner.Id}) is putting up more money for trade than have {tradeInfo.OwnerMoneyPutup} > {owner.Money}, possible exploit or modified client!");
            return false;
        }
        if (tradeInfo.TargetMoneyPutup > target.Money)
        {
            CancelTrade(target.ObjId, 0, tradeId); // Reason?
            Logger.Error($"{target.Name} ({target.Id}) is putting up more money for trade than have {tradeInfo.TargetMoneyPutup} > {target.Money}, possible exploit or modified client!");
            return false;
        }

        var hasErrors = 0;
        var tasksOwner = new List<ItemTask>();
        var tasksTarget = new List<ItemTask>();

        // Handle Money from Owner
        if (tradeInfo.OwnerMoneyPutup > 0)
        {
            owner.Money -= tradeInfo.OwnerMoneyPutup;
            tasksOwner.Add(new MoneyChange(-tradeInfo.OwnerMoneyPutup));
            target.Money += tradeInfo.OwnerMoneyPutup;
            tasksTarget.Add(new MoneyChange(tradeInfo.OwnerMoneyPutup));
        }

        // Handle Money from Target
        if (tradeInfo.TargetMoneyPutup > 0)
        {
            owner.Money += tradeInfo.TargetMoneyPutup;
            tasksOwner.Add(new MoneyChange(tradeInfo.TargetMoneyPutup));
            target.Money -= tradeInfo.TargetMoneyPutup;
            tasksTarget.Add(new MoneyChange(-tradeInfo.TargetMoneyPutup));
        }

        // Handle Items from Owner → Target receives
        foreach (var entry in tradeInfo.OwnerItems)
            MoveTradeEntry(entry, owner, target, tasksOwner, tasksTarget, ref hasErrors);
        // Handle Items from Target → Owner receives
        foreach (var entry in tradeInfo.TargetItems)
            MoveTradeEntry(entry, target, owner, tasksTarget, tasksOwner, ref hasErrors);

        // Trade complete, remove ID and send item task packets
        _trades.Remove(tradeId);
        owner.SendPacket(new SCTradeMadePacket(ItemTaskType.Trade, tasksOwner, []));
        target.SendPacket(new SCTradeMadePacket(ItemTaskType.Trade, tasksTarget, []));
        Logger.Info($"Trade Id:{tradeId} finished. Owner {owner.Name} ({owner.Id}) Items/Money: {tradeInfo.OwnerItems.Count}/{tradeInfo.OwnerMoneyPutup} <=> Target {target.Name} ({target.Id}) Items/Money: {tradeInfo.TargetItems.Count}/{tradeInfo.TargetMoneyPutup}");
        if (hasErrors > 0)
        {
            Logger.Error($"{hasErrors}item(s) could not be trade for tradeId: {tradeId} between {owner.Name} ({owner.Id}) and {target.Name} ({target.Id}), possible exploit or modified client!");
        }

        return hasErrors == 0;
    }

    /// <summary>
    /// Moves one trade-window entry from its putter to the receiving
    /// character. Whole-stack entries move the item instance (the exact
    /// AddOrMoveExistingItem call the mail attachment path uses; the
    /// remove/add tasks ride each side's single SCTradeMadePacket).
    /// Partial entries split: the sender's source stack is reduced by the
    /// offered count (its own sync packet) and the receiver is granted a
    /// fresh stack of that count through AcquireDefaultItem (which emits
    /// its own detailed packet — it knows whether it merged into an
    /// existing stack or created new slots).
    /// </summary>
    private void MoveTradeEntry(TradeItemEntry entry, Character sender, Character receiver,
        List<ItemTask> tasksSender, List<ItemTask> tasksReceiver, ref int hasErrors)
    {
        var item = entry.Item;

        // Partial split — offer fewer units than the source stack holds
        if (entry.Count < item.Count)
        {
            item.Count -= entry.Count;
            sender.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.Trade,
                [new ItemCountUpdate(item, -entry.Count)], []));

            if (!receiver.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Trade, item.TemplateId, entry.Count, item.Grade))
                hasErrors++;
            return;
        }

        // Whole instance moves to the receiver
        if (receiver.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Invalid, item))
        {
            tasksSender.Add(new ItemRemove(item));
            tasksReceiver.Add(new ItemAdd(item));
        }
        else
        {
            hasErrors++;
        }
    }
}
