using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;

using NLog;

namespace AAEmu.Game.Models.Game.Mails;

public class MailForAuction : BaseMail
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private uint _buyerId;
    private readonly uint _sellerId;
    private readonly Item _item;
    private readonly string _itemName;
    private readonly int _itemBuyoutPrice;
    private int _sellerShare;
    private readonly int _listingFee;
    private int _tradeTaxFee;

    private static readonly string AuctionName = "Auctioneer";
    // TODO: verify title names
    private static readonly string TitleSold = "Successful Auction Notice";
    private static readonly string TitleNotSold = "Failed Auction Notice";
    private static readonly string TitleBidWin = "Succesfull Purchase";
    private static readonly string TitleBidLost = "Failed Bid Notice";
    private static readonly string TitleCancel = "Cancelled Auction Notice";

    // Mail examples for 1.2

    public MailForAuction(Item itemToSell, uint sellerId, int buyoutPrice, int listingFee) : base()
    {
        _buyerId = 0;
        _sellerId = sellerId;
        _item = itemToSell;
        _itemBuyoutPrice = buyoutPrice;
        _sellerShare = 0;
        _listingFee = listingFee;
        _tradeTaxFee = 0;
        _itemName = LocalizationManager.Instance.Get("items", "name", _item.TemplateId, "Item:" + itemToSell.TemplateId.ToString());

        // Correct types and name will be set in finalize functions
        MailType = MailType.InvalidMailType;
        Header.SenderId = 0;
        Header.SenderName = AuctionName; // Name changes depending on type of mail

        Body.RecvDate = DateTime.UtcNow; // These mails should always be instant
    }

    /// <summary>
    /// Please refactor AH to not use this fucntion, only finalize with FinalizeForBidFail() or face null exceptions
    /// </summary>
    /// <param name="itemTemplateIdToSell"></param>
    /// <param name="sellerId"></param>
    /// <param name="buyoutPrice"></param>
    /// <param name="listingFee"></param>
    public MailForAuction(uint itemTemplateIdToSell, uint sellerId, int buyoutPrice, int listingFee) : base()
    {
        _buyerId = 0;
        _sellerId = sellerId;
        _item = null;
        _itemBuyoutPrice = buyoutPrice;
        _sellerShare = 0;
        _listingFee = listingFee;
        _tradeTaxFee = 0;
        _itemName = LocalizationManager.Instance.Get("items", "name", itemTemplateIdToSell, "Item:" + itemTemplateIdToSell.ToString());

        // Correct types and name will be set in finalize functions
        MailType = MailType.InvalidMailType;
        Header.SenderId = 0;
        Header.SenderName = AuctionName; // Name changes depending on type of mail

        Body.RecvDate = DateTime.UtcNow; // These mails should always be instant
    }

    /// <summary>
    /// Prepare mail for the person who is buying the item
    /// </summary>
    /// <returns></returns>
    public bool FinalizeForSaleBuyer(uint buyerId)
    {
        // /testmail 16 .auctionBidWin AHBidWin "body('My Sold Item', 7, 400000)"
        _buyerId = buyerId;

        var nameBuyer = NameManager.Instance.GetCharacterName(_buyerId);
        if (nameBuyer == null)
            return false;

        Header.SenderName = ".auctionBidWin";
        ReceiverName = nameBuyer;

        MailType = MailType.AucBidWin;
        Title = TitleBidWin;
        Header.ReceiverId = _buyerId;

        Body.Text = string.Format("body('{0}', {1}, {2})", _itemName, _item.Count, _itemBuyoutPrice);
        // The sold instance was listed OUT of the seller's Auction container
        // (PostLotOnAuction → AuctionAttachments.AddOrMoveExistingItem), so it
        // must be relocated into the BUYER's Mail container — stamping SlotType
        // alone would persist container_id = the seller's listing container and
        // the reboot would re-home the bought item as a SlotType.Auction orphan
        // owned by the buyer.
        AttachItemForReturn(_item, _buyerId);

        return true;
    }

    /// <summary>
    /// Prepare mail for the person selling the item
    /// </summary>
    /// <returns></returns>
    public bool FinalizeForSaleSeller(int sellerShare, int tradeTaxFee)
    {
        // /testmail 14 .auctionOffSuccess AHBuy "body('My Sold Item',7, 364000, 400000, 40000, 4000)"
        _sellerShare = sellerShare;
        _tradeTaxFee = tradeTaxFee;

        var nameSeller = NameManager.Instance.GetCharacterName(_sellerId);
        if (nameSeller == null)
            return false;

        Header.SenderName = ".auctionOffSuccess";
        Header.ReceiverId = _sellerId;
        ReceiverName = nameSeller;

        MailType = MailType.AucOffSuccess;
        Title = TitleSold;

        Body.Text = string.Format("body('{0}', {1}, {2}, {3}, {4}, {5})",
            _itemName, _item.Count, _sellerShare, _itemBuyoutPrice, _tradeTaxFee, _listingFee);

        AttachMoney(sellerShare);

        return true;
    }

    /// <summary>
    /// Hands <paramref name="item"/> to a returning mail as an attachment,
    /// relocating it into the receiver's MAIL container.
    ///
    /// Stamping SlotType alone is NOT enough: ItemManager.Save persists
    /// container_id from item._holdingContainer, so an instance left in its
    /// listing container reloads (ItemManager.LoadUserItems →
    /// ItemContainer.AddOrMoveExistingItem) with SlotType rewritten from THAT
    /// container's type — the returned item would come back as a
    /// SlotType.Auction orphan after a reboot. The relocation must happen on
    /// the SAME instance (no template mint) so the enchant/durability/details
    /// survive, exactly as MailPlayerToPlayer.FinalizeAttachments does for
    /// player mail.
    ///
    /// Auction mail is created server-side while the seller may be OFFLINE, so
    /// the receiver's container is resolved by owner id through
    /// ItemManager.GetItemContainerForCharacter (the same owner-id path
    /// AuctionController uses for an offline client) rather than through a live
    /// Character.Inventory reference.
    /// </summary>
    private void AttachItemForReturn(Item item, uint receiverId)
    {
        // The receiving character may be OFFLINE (auction mail is created
        // server-side), so the MAIL container is resolved by owner id through
        // the manager — the same owner-id path AuctionController uses — never
        // through a live Character.Inventory reference. PeekInstance (not
        // Instance) keeps headless rigs that have no ItemManager loadable: they
        // hold no containers at all, so there is nothing to relocate into.
        var mailContainer = ItemManager.PeekInstance
            ?.GetItemContainerForCharacter(receiverId, SlotType.Mail, null, 0);
        if (mailContainer == null)
        {
            Logger.Warn(
                $"Auction mail to {receiverId}: no ItemManager mail container available (headless rig) — " +
                $"item {item.Id} ({item.TemplateId}) keeps container {item._holdingContainer?.ContainerId ?? 0}");
            item.OwnerId = receiverId;
            item.SlotType = SlotType.Mail;
        }
        else if (!mailContainer.AddOrMoveExistingItem(ItemTaskType.Invalid, item))
        {
            // Mail containers are unlimited, so this should never fail. If it
            // somehow does, adopt the instance where it stands and log loudly —
            // never wedge the return or lose the item.
            Logger.Error(
                $"Auction mail to {receiverId}: could not move item {item.Id} ({item.TemplateId}) into the mail container " +
                $"(container {mailContainer.ContainerId}) — leaving it in container {item._holdingContainer?.ContainerId ?? 0}, " +
                "the row will persist with its current container");
            item.OwnerId = receiverId;
            item.SlotType = SlotType.Mail;
        }

        // The container relocation above is what makes the persisted row reload
        // as Mail; this list is what the mail itself carries (mails.attachmentN
        // and the wire body).
        Body.Attachments.Add(item);
    }

    /// <summary>
    /// Prepare mail for returning item to owner because of cancel
    /// </summary>
    /// <returns></returns>
    public bool FinalizeForCancel()
    {
        // /testmail 13 .auctionOffCancel AHCancel "body('My Sold Item',7)"

        var nameSeller = NameManager.Instance.GetCharacterName(_sellerId);
        if (nameSeller == null)
            return false;

        Header.SenderName = ".auctionOffCancel";
        Header.ReceiverId = _sellerId;
        ReceiverName = nameSeller;

        MailType = MailType.AucOffCancel;
        Title = TitleCancel;

        Body.Text = string.Format("body('{0}', {1})", _itemName, _item.Count);
        AttachItemForReturn(_item, _sellerId);

        return true;
    }

    /// <summary>
    /// Prepare mail for returning item to owner because of expired
    /// </summary>
    /// <returns></returns>
    public bool FinalizeForFail()
    {
        // /testmail 15 .auctionOffFail AHFail "body('My Sold Item',7)"

        var nameSeller = NameManager.Instance.GetCharacterName(_sellerId);
        if (nameSeller == null)
            return false;

        Header.SenderName = ".auctionOffFail";
        Header.ReceiverId = _sellerId;
        ReceiverName = nameSeller;

        MailType = MailType.AucOffFail;
        Title = TitleNotSold;

        Body.Text = string.Format("body('{0}', {1})", _itemName, _item.Count);
        AttachItemForReturn(_item, _sellerId);

        return true;
    }

    /// <summary>
    /// Prepare mail for the person who was outbid
    /// </summary>
    /// <returns></returns>
    public bool FinalizeForBidFail(uint previousBuyerId, int previousBid)
    {
        // /testmail 17 .auctionBidFail AHBidFail "body('My Sold Item')"
        _buyerId = previousBuyerId;

        var nameBuyer = NameManager.Instance.GetCharacterName(_buyerId);
        if (nameBuyer == null)
            return false;

        Header.SenderName = ".auctionBidFail";
        Header.ReceiverId = _buyerId;
        ReceiverName = nameBuyer;

        MailType = MailType.AucOffFail;
        Title = TitleBidLost;

        Body.Text = string.Format("body('{0}')", _itemName);

        AttachMoney(previousBid);

        return true;
    }
}
