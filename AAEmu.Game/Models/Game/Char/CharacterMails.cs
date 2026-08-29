using AAEmu.Commons.Exceptions;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterMails
{
    private Character Self { get; set; }
    public CountUnreadMail UnreadMailCount { get; set; }

    public CharacterMails(Character self)
    {
        Self = self;

        UnreadMailCount = new CountUnreadMail
        {
            Sent = 0,
        };
        UnreadMailCount.ResetReceived();
    }

    public void OpenMailbox()
    {
        var total = 0;
        foreach (var m in MailManager.Instance.GetCurrentMailList(Self.Id))
        {
            if (m.Value.Header.SenderId == Self.Id && m.Value.Header.ReceiverId == Self.Id)
            {
                Self.SendPacket(new SCMailListPacket(false, [m.Value.Header]));
                total++;
            }
            else if (m.Value.Header.SenderId == Self.Id)
            {
                Self.SendPacket(new SCMailListPacket(true, [m.Value.Header]));
                total++;
            }
            else if (m.Value.Header.ReceiverId == Self.Id)
            {
                Self.SendPacket(new SCMailListPacket(false, [m.Value.Header]));
                total++;
            }
        }
        Self.SendPacket(new SCMailListEndPacket(total, 0));
    }

    public void ReadMail(bool isSent, long id)
    {
        if (MailManager.Instance._allPlayerMails.TryGetValue(id, out var mail))
        {
            // Ownership guard (C2S entry points CSReadMailPacket and the sent-tab
            // variant): the sent tab only shows mails we sent, the receive tab only
            // mails addressed to us. Mirrors the CSTakeAttachmentSequentially
            // "check for hackers" refusal.
            var ownsEntry = isSent
                ? mail.Header.SenderId == Self.Id
                : mail.Header.ReceiverId == Self.Id;
            if (!ownsEntry)
            {
                Self.SendErrorMessage(ErrorMessageType.MailInvalid);
                return;
            }

            if (mail.Header.Status == MailStatus.Unread && !isSent)
            {
                UnreadMailCount.UpdateReceived(mail.MailType, -1);
                mail.OpenDate = DateTime.UtcNow;
                mail.Header.Status = MailStatus.Read;
                mail.IsDelivered = true;
            }
            Self.SendPacket(new SCMailBodyPacket(false, isSent, mail.Body, true, UnreadMailCount));
            Self.SendPacket(new SCMailStatusUpdatedPacket(isSent, id, mail.Header.Status));
            SendUnreadMailCount();
        }
    }

    public void SendUnreadMailCount()
    {
        Self.SendPacket(new SCCountUnreadMailPacket(UnreadMailCount));
    }

    public MailResult SendMailToPlayer(MailType mailType, string receiverName, string title, string text, byte attachments, int money0, int money1, int money2, long extra, List<(SlotType, byte)> itemSlots)
    {

        if (string.IsNullOrWhiteSpace(receiverName) || NameManager.Instance.GetCharacterId(receiverName) == 0)
        {
            return MailResult.UnableToFindRecipient;
        }

        var mail = new MailPlayerToPlayer(Self, receiverName) {
            MailType = mailType,
            Title = title,
            Header = {
                Attachments = attachments,
                Extra = extra
                },
            Body =
            {
                Text = text,
                SendDate = DateTime.UtcNow,
                RecvDate = DateTime.UtcNow
            }
        };

        mail.AttachMoney(money0, money1, money2);

        // First verify source items, and add them to the attachments of body
        if (!mail.PrepareAttachmentItems(itemSlots))
        {
            // Self.SendErrorMessage(ErrorMessageType.MailInvalidItem);
            return MailResult.InvalidSlot;
        }

        // With attachments in place, we can calculate the send fee
        var mailFee = mail.GetMailFee();
        if (mailFee + money0 > Self.Money)
        {
            // Self.SendErrorMessage(ErrorMessageType.MailNotEnoughMoney);
            return MailResult.InsufficientCoins;
        }

        if (!mail.FinalizeAttachments())
            return MailResult.InvalidSlot; // Should never fail at this point

        // Add delay if not a normal snail mail
        if (mailType == MailType.Normal)
            mail.Body.RecvDate = DateTime.UtcNow + MailManager.NormalMailDelay;

        // Send it
        if (mail.Send())
        {
            Self.SendPacket(new SCMailSentPacket(mail.Header, itemSlots.ToArray()));
            // Take the fee
            Self.SubtractMoney(SlotType.Inventory, mailFee + money0);
            return MailResult.Success;
        }
        else
        {
            return MailResult.MailErrorOccurred;
        }
    }

    public bool GetAttached(long mailId, bool takeMoney, bool takeItems, bool takeAllSelected, ulong specifiedItemId = 0)
    {
        var res = true;
        if (MailManager.Instance._allPlayerMails.TryGetValue(mailId, out var thisMail))
        {
            // Ownership guard (C2S entry points CSTakeAttachmentItemPacket,
            // CSTakeAttachmentMoneyPacket, and CSTakeAllAttachmentItemPacket):
            // only the current receiver may loot attachments. Mirrors the
            // CSTakeAttachmentSequentially "check for hackers" refusal.
            if (thisMail.Header.ReceiverId != Self.Id)
            {
                Self.SendErrorMessage(ErrorMessageType.MailInvalid);
                return false;
            }

            var tookMoney = false;
            if (thisMail.MailType == MailType.AucOffSuccess && thisMail.Body.CopperCoins > 0 && takeMoney)
            {
                if (Self.LaborPower < 1)
                {
                    Self.SendErrorMessage(ErrorMessageType.NotEnoughLaborPower);
                    takeMoney = false;
                }
                else
                {
                    Self.ChangeLabor(-1, (int)ActabilityType.Commerce);
                }
            }
            if (thisMail.Body.CopperCoins > 0 && takeMoney)
            {
                Self.ChangeMoney(SlotType.Inventory, thisMail.Body.CopperCoins);
                thisMail.Body.CopperCoins = 0;
                thisMail.Header.Attachments -= 1;
                tookMoney = true;
            }

            var itemSlotList = new List<ItemIdAndLocation>();
            // Check if items need to be taken, and add them to a list
            if (takeItems)
            {
                // COD payment check (MailType.Charged): receiver must have enough money to pay the COD charge
                if (thisMail.MailType == MailType.Charged && thisMail.Header.Extra > 0)
                {
                    var codCost = (int)thisMail.Header.Extra;
                    if (Self.Money < codCost)
                    {
                        Self.SendErrorMessage(ErrorMessageType.MailNotEnoughMoney);
                        return false;
                    }
                }

                var toRemove = new List<Item>();
                foreach (var itemAttachment in thisMail.Body.Attachments)
                {
                    // if not our specified item, skip this slot
                    if (specifiedItemId > 0 && itemAttachment.Id != specifiedItemId)
                        continue;

                    // Sanity-check
                    if (itemAttachment.Id != 0)
                    {
                        // Free Space Check
                        if (Self.Inventory.Bag.SpaceLeftForItem(itemAttachment, out var foundItems) >= itemAttachment.Count)
                        {
                            Item stackItem = null;
                            // Check if we can stack the item onto an existing one
                            if (itemAttachment.Template.MaxCount > 1 && foundItems.Count > 0)
                            {
                                foreach (var fi in foundItems)
                                {
                                    if (fi.Count + itemAttachment.Count <= fi.Template.MaxCount)
                                    {
                                        stackItem = fi;
                                        break;
                                    }
                                }
                            }

                            var itemIdAndLocation = new ItemIdAndLocation
                            {
                                Id = itemAttachment.Id,
                                SlotType = itemAttachment.SlotType,
                                Slot = (byte)itemAttachment.Slot
                            };

                            // Move item to player inventory
                            if (Self.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Mail, itemAttachment, stackItem?.Slot ?? -1))
                            {
                                itemSlotList.Add(itemIdAndLocation);
                                thisMail.Header.Attachments -= 1;
                                toRemove.Add(itemAttachment);
                            }
                            else
                            {
                                // Should technically never fail because of previous free slot check
                                throw new GameException("GetAttachmentFailedAddToBag");
                            }
                        }
                        else
                        {
                            // Bag Full
                            Self.SendErrorMessage(ErrorMessageType.BagFull);
                            res = false;
                        }
                    }
                }
                // Removed those marked to be taken
                foreach (var ia in toRemove)
                {
                    thisMail.Body.Attachments.Remove(ia);
                    thisMail.IsDirty = true;
                }

                // If COD mail items were looted, deduct the payment from receiver and send payment mail to original sender
                if (thisMail.MailType == MailType.Charged && thisMail.Header.Extra > 0 && itemSlotList.Count > 0)
                {
                    var codCost = (int)thisMail.Header.Extra;
                    Self.SubtractMoney(SlotType.Inventory, codCost);
                    thisMail.Header.Extra = 0;

                    if (thisMail.Header.SenderId > 0 && !string.IsNullOrWhiteSpace(thisMail.Header.SenderName))
                    {
                        var paymentMail = new BaseMail
                        {
                            Title = $"[COD] {thisMail.Title}",
                            ReceiverName = thisMail.Header.SenderName,
                            MailType = MailType.Normal,
                            Header =
                            {
                                Status = MailStatus.Unread,
                                SenderId = 0,
                                SenderName = ".system",
                                ReceiverId = thisMail.Header.SenderId,
                                Attachments = 1
                            },
                            Body =
                            {
                                Text = $"COD payment from {Self.Name}",
                                CopperCoins = codCost,
                                SendDate = DateTime.UtcNow,
                                RecvDate = DateTime.UtcNow
                            }
                        };
                        MailManager.Instance.Send(paymentMail);
                    }
                }
            }
            // Mark taken items

            // Send attachments taken packets (if needed)
            // Money
            if (tookMoney)
            {
                Self.SendPacket(new SCAttachmentTakenPacket(mailId, true, false, takeAllSelected, []));
                thisMail.IsDirty = true;
            }

            // Items
            if (itemSlotList.Count > 0)
            {
                /*
                 * ZeromusXYZ:
                 * Splitting this packet up to be sent one by one fixes delivery issue in cases where not everything is delivered at once,
                 * like full bag, manual item grabbing.
                 * It's kind of silly, but I don't have a better solution for it
                */
                foreach (var iSlot in itemSlotList)
                {
                    var dummyItemSlotList = new List<ItemIdAndLocation>
                    {
                        iSlot
                    };
                    Self.SendPacket(new SCAttachmentTakenPacket(mailId, false, false, takeAllSelected, dummyItemSlotList));
                    thisMail.IsDirty = true;
                }
            }

            // Mark mail as read in case we took at least one item from it
            if (thisMail.Header.Status == MailStatus.Unread && (tookMoney || itemSlotList.Count > 0))
            {
                thisMail.Header.Status = MailStatus.Read;
                UnreadMailCount.UpdateReceived(thisMail.MailType, -1);
                Self.SendPacket(new SCMailStatusUpdatedPacket(false, mailId, MailStatus.Read));
                SendUnreadMailCount();
                thisMail.IsDirty = true;
            }

            // TODO: Make sure attachment settings and mail info is sent back correctly
            // taking all attachments sometimes doesn't enable the delete button when getting attachments using "GetAllSelected"

            // TODO: if source player is online, update their mail info (sent tab)
        }

        return res;
    }

    public void DeleteMail(long id, bool isSent)
    {
        // Ownership guard (C2S entry point CSDeleteMailPacket): only the current
        // receiver may delete an entry from the received tab, and sender from sent tab.
        // Mirrors the CSTakeAttachmentSequentially "check for hackers" refusal.
        if (MailManager.Instance._allPlayerMails.TryGetValue(id, out var mail))
        {
            var ownsEntry = isSent
                ? mail.Header.SenderId == Self.Id
                : mail.Header.ReceiverId == Self.Id;

            if (!ownsEntry)
            {
                Self.SendErrorMessage(ErrorMessageType.MailInvalid);
                return;
            }

            if (isSent)
            {
                Self.SendPacket(new SCMailDeletedPacket(true, id, false, UnreadMailCount));
                MailManager.Instance.DeleteMail(id);
                return;
            }

            if (mail.Header.Attachments <= 0)
            {
                // ReSharper disable ConditionIsAlwaysTrueOrFalse
                if (mail.Header.Status != MailStatus.Read)
                {
                    UnreadMailCount.UpdateReceived(mail.MailType, -1);
                    Self.SendPacket(new SCMailDeletedPacket(false, id, true, UnreadMailCount));
                }
                else
                {
                    Self.SendPacket(new SCMailDeletedPacket(false, id, false, UnreadMailCount));
                }
                // ReSharper enable ConditionIsAlwaysTrueOrFalse
                MailManager.Instance.DeleteMail(id);
            }
        }
    }

    public void ReturnMail(long id)
    {
        // Ownership/status validation and attachment-safe bouncing live in MailManager
        MailManager.Instance.ReturnMail(Self, id);
    }
}
