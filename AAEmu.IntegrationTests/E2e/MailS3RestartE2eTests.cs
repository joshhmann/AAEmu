using System.Reflection;
using System.Text.Json;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Items;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// MAIL-01 S3: real player-to-player mail with an equipment instance and
/// copper. The item is sent by CSSendMailPacket, the game process is killed
/// and restarted, and the receiver uses CSListMail/CSReadMail/
/// CSTakeAttachmentSequentially/CSDeleteMail over an authenticated game link.
/// No mail or item state is written by the test.
/// </summary>
[Collection("e2e")]
public sealed class MailS3RestartE2eTests
{
    private const string SenderBot = "Mails3Sender";
    private const string ReceiverBot = "Mails3Receiver";
    private const string SenderAccount = "mails3sender";
    private const string ReceiverAccount = "mails3receiver";
    private const string Password = "e2e-secret";

    // Canonical compact.sqlite3 equipment template: first-step dagger.
    private const uint EquipmentTemplateId = 5318;
    private const int InitialMoney = 10_000;
    private const int AttachedCopper = 1_234;
    private const int SlotTypeInventory = 2;
    private const int NormalFee = 50 + 30; // one item + money => 2 attachments
    private const int SlotTypeMail = 5;
    private const int MailStatusUnread = 0;
    private const int MailStatusRead = 1;
    private const int MailTypeNormal = 1;

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets()
    {
        var startedAt = DateTime.UtcNow;
        var stages = new List<string>();
        BotNetworkSession? sender = null;
        BotNetworkSession? receiver = null;
        uint senderId = 0, receiverId = 0;
        ulong itemId = 0;
        long mailId = 0;

        try
        {
            E2eStack.EnsureUp();
            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            Call(bridge, "delay", "{\"cmd\":\"mail\",\"op\":\"delay\",\"seconds\":1}", stages);

            stages.Add("connect-before-send");
            sender = await ConnectAsync(SenderBot, SenderAccount);
            // Provision B through the real login flow, then leave it offline
            // before send. This makes the post-restart unread count observe
            // one delivery, not an online-notification plus login replay.
            receiver = await ConnectAsync(ReceiverBot, ReceiverAccount);
            StopBackgroundLoops(sender);
            StopBackgroundLoops(receiver);
            Drain(GetGameLink(sender));
            Drain(GetGameLink(receiver));
            receiver.Dispose();
            receiver = null;

            var rig = Call(bridge, "rig-sender", $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{SenderBot}\",\"money\":{InitialMoney},\"itemTemplate\":{EquipmentTemplateId},\"count\":1,\"grade\":3,\"durability\":77,\"runeId\":1234,\"temperPhysical\":3,\"temperMagical\":4}}", stages);
            senderId = rig.GetProperty("id").GetUInt32();
            itemId = rig.GetProperty("itemId").GetUInt64();
            Assert.True(itemId > 0);
            Assert.Equal(3, rig.GetProperty("grade").GetInt32());
            var source = InventoryItem(bridge, SenderBot, itemId, stages);
            Assert.Equal(itemId, source.GetProperty("itemId").GetUInt64());
            Assert.Equal(EquipmentTemplateId, source.GetProperty("templateId").GetUInt32());
            Assert.Equal(3, source.GetProperty("grade").GetInt32());
            Assert.Equal(77, source.GetProperty("durability").GetInt32());
            Assert.Equal(1234u, source.GetProperty("runeId").GetUInt32());
            Assert.Equal(3, source.GetProperty("temperPhysical").GetInt32());
            Assert.Equal(4, source.GetProperty("temperMagical").GetInt32());
            var detailB64 = source.GetProperty("detailB64").GetString();
            Assert.False(string.IsNullOrEmpty(detailB64), "equipment detail blob must be present");

            var mailbox = Call(bridge, "mailbox-sender", $"{{\"cmd\":\"mail\",\"op\":\"mailbox\",\"bot\":\"{SenderBot}\"}}", stages);
            var doodadObjId = mailbox.GetProperty("doodadObjId").GetUInt32();
            Assert.True(doodadObjId > 0);
            Assert.True(mailbox.GetProperty("withinRange").GetBoolean());

            stages.Add("send-cssendmail");
            var senderLink = GetGameLink(sender);
            senderLink.SendGameFrame(CSOffsets.CSSendMailPacket, 1, body =>
            {
                body.Write((byte)MailTypeNormal);
                body.Write("Mails3receiver");
                body.Write(0u); // client status/unknown
                body.Write("S3 equipment");
                body.Write("restart fidelity");
                body.Write((byte)2); // item + copper; server recounts from payload
                body.Write(AttachedCopper);
                body.Write(0); // silver
                body.Write(0); // gold
                body.Write(0L); // extra
                body.Write((byte)2); // SlotType.Inventory on the wire
                body.Write(source.GetProperty("slot").GetByte());
                for (var i = 1; i < 10; i++)
                {
                    body.Write((byte)0);
                    body.Write((byte)0);
                }
                WriteBc(body, doodadObjId);
            });
            var sendResult = senderLink.ReadAnyOf([SCOffsets.SCMailSentPacket, SCOffsets.SCMailFailedPacket], 20_000);
            Assert.True(sendResult.Type == SCOffsets.SCMailSentPacket, $"mail send failed body={Convert.ToHexString(sendResult.Body)}");

            var senderAfterSend = Call(bridge, "sender-after-send", $"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{SenderBot}\"}}", stages);
            Assert.Equal(InitialMoney - AttachedCopper - NormalFee, senderAfterSend.GetProperty("money").GetInt64());
            var senderMails = WaitForMail(bridge, SenderBot, m => m.GetProperty("senderId").GetUInt32() == senderId && string.Equals(m.GetProperty("receiverName").GetString(), ReceiverBot, StringComparison.OrdinalIgnoreCase), stages);
            var sentMail = senderMails.Single();
            mailId = sentMail.GetProperty("id").GetInt64();
            var sentAttachment = sentMail.GetProperty("attachments").EnumerateArray().Single();
            Assert.Equal(itemId, sentAttachment.GetProperty("itemId").GetUInt64());
            Assert.Equal(SlotTypeMail, sentAttachment.GetProperty("slotType").GetInt32());
            Assert.Equal(detailB64, sentAttachment.GetProperty("detailB64").GetString());

            Call(bridge, "save-before-restart", "{\"cmd\":\"save\"}", stages, 180_000);
            Assert.Equal(SlotTypeMail, QueryItem(itemId).SlotType);
            Assert.Equal(senderId, QueryItem(itemId).OwnerId);
            Assert.NotNull(QueryMail(mailId));

            var beforeRestart = QueryItem(itemId);
            sender.Dispose();
            sender = null;
            receiver = null;

            stages.Add("kill-9-restart");
            E2eStack.RestartGameServer();
            using var bridgeAfter = new BotDriveClient(E2eStack.BridgePort);
            sender = await ConnectAsync(SenderBot, SenderAccount);
            receiver = await ConnectAsync(ReceiverBot, ReceiverAccount);
            StopBackgroundLoops(sender);
            StopBackgroundLoops(receiver);
            Drain(GetGameLink(sender));
            Drain(GetGameLink(receiver));

            receiverId = receiver.CharacterId;
            stages.Add("reload-mail-and-slot-mail");
            var receivedBeforeRead = WaitForMail(bridgeAfter, ReceiverBot,
                m => m.GetProperty("id").GetInt64() == mailId && m.GetProperty("status").GetInt32() == MailStatusUnread,
                stages);
            var receivedAttachment = receivedBeforeRead.Single().GetProperty("attachments").EnumerateArray().Single();
            Assert.Equal(itemId, receivedAttachment.GetProperty("itemId").GetUInt64());
            Assert.Equal(EquipmentTemplateId, receivedAttachment.GetProperty("templateId").GetUInt32());
            Assert.Equal(SlotTypeMail, receivedAttachment.GetProperty("slotType").GetInt32());
            Assert.Equal(3, receivedAttachment.GetProperty("grade").GetInt32());
            Assert.Equal(1, receivedAttachment.GetProperty("count").GetInt32());
            Assert.Equal(77, receivedAttachment.GetProperty("durability").GetInt32());
            Assert.Equal(1234u, receivedAttachment.GetProperty("runeId").GetUInt32());
            Assert.Equal(3, receivedAttachment.GetProperty("temperPhysical").GetInt32());
            Assert.Equal(4, receivedAttachment.GetProperty("temperMagical").GetInt32());
            Assert.Equal(detailB64, receivedAttachment.GetProperty("detailB64").GetString());
            var afterRestartRow = QueryItem(itemId);
            Assert.Equal(beforeRestart.Id, afterRestartRow.Id);
            Assert.Equal(receiverId, afterRestartRow.OwnerId);
            Assert.Equal(beforeRestart.SlotType, afterRestartRow.SlotType);
            Assert.Equal(beforeRestart.TemplateId, afterRestartRow.TemplateId);
            Assert.Equal(beforeRestart.Count, afterRestartRow.Count);
            Assert.Equal(beforeRestart.Grade, afterRestartRow.Grade);
            Assert.True(beforeRestart.Details.SequenceEqual(afterRestartRow.Details));
            var receiverBeforeRead = Call(bridgeAfter, "receiver-unread", $"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{ReceiverBot}\"}}", stages);
            var receiverMoneyBeforeTake = receiverBeforeRead.GetProperty("money").GetInt64();
            Assert.Equal(1, receiverBeforeRead.GetProperty("unread").GetProperty("received").GetInt32());

            var receiverLink = GetGameLink(receiver);
            stages.Add("list-and-read-real-mail");
            receiverLink.SendGameFrame(CSOffsets.CSListMailPacket, 1, _ => { });
            _ = receiverLink.ReadFrameUntil(SCOffsets.SCMailListEndPacket, 20_000);
            receiverLink.SendGameFrame(CSOffsets.CSReadMailPacket, 1, body =>
            {
                body.Write(false); // received tab
                body.Write(mailId);
            });
            _ = receiverLink.ReadFrameUntil(SCOffsets.SCMailBodyPacket, 20_000);
            var receivedAfterRead = WaitForMail(bridgeAfter, ReceiverBot, m => m.GetProperty("id").GetInt64() == mailId, stages).Single();
            Assert.Equal(MailStatusRead, receivedAfterRead.GetProperty("status").GetInt32());
            var receiverAfterRead = Call(bridgeAfter, "receiver-read", $"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{ReceiverBot}\"}}", stages);
            Assert.Equal(0, receiverAfterRead.GetProperty("unread").GetProperty("received").GetInt32());

            stages.Add("take-sequential-real-mail");
            receiverLink.SendGameFrame(CSOffsets.CSTakeAttachmentSequentially, 1, body => body.Write(mailId));
            _ = receiverLink.ReadFrameUntil(SCOffsets.SCAttachmentTakenPacket, 20_000);
            var receivedAfterTake = WaitForMail(bridgeAfter, ReceiverBot, m => m.GetProperty("id").GetInt64() == mailId, stages).Single();
            Assert.Equal(0, receivedAfterTake.GetProperty("attachmentsHeader").GetInt32());
            Assert.Empty(receivedAfterTake.GetProperty("attachments").EnumerateArray());
            Assert.Equal(receiverMoneyBeforeTake + AttachedCopper, Call(bridgeAfter, "receiver-copper", $"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{ReceiverBot}\"}}", stages).GetProperty("money").GetInt64());
            var receivedItem = InventoryItem(bridgeAfter, ReceiverBot, itemId, stages);
            Assert.Equal(itemId, receivedItem.GetProperty("itemId").GetUInt64());
            Assert.Equal(SlotTypeInventory, receivedItem.GetProperty("slotType").GetInt32());
            Assert.Equal(EquipmentTemplateId, receivedItem.GetProperty("templateId").GetUInt32());
            Assert.Equal(3, receivedItem.GetProperty("grade").GetInt32());
            Assert.Equal(77, receivedItem.GetProperty("durability").GetInt32());
            Assert.Equal(1234u, receivedItem.GetProperty("runeId").GetUInt32());
            Assert.Equal(detailB64, receivedItem.GetProperty("detailB64").GetString());
            Call(bridgeAfter, "save-after-take", "{\"cmd\":\"save\"}", stages, 180_000);
            var postTakeRow = QueryItem(itemId);
            Assert.Equal(receiverId, postTakeRow.OwnerId);
            Assert.Equal(SlotTypeInventory, postTakeRow.SlotType);

            stages.Add("delete-real-mail");
            receiverLink.SendGameFrame(CSOffsets.CSDeleteMailPacket, 1, body =>
            {
                body.Write(mailId);
                body.Write(false); // received tab
            });
            _ = receiverLink.ReadFrameUntil(SCOffsets.SCMailDeletedPacket, 20_000);
            Assert.DoesNotContain(EnumerateMails(bridgeAfter, ReceiverBot, stages), m => m.GetProperty("id").GetInt64() == mailId);
            Call(bridgeAfter, "save-after-delete", "{\"cmd\":\"save\"}", stages, 180_000);
            Assert.Null(QueryMail(mailId));
            stages.Add("PASS");
        }
        finally
        {
            sender?.Dispose();
            receiver?.Dispose();
            E2eStack.CleanupBotRows(SenderAccount, ReceiverAccount);
            WriteEvidence(startedAt, senderId, receiverId, itemId, mailId, stages);
        }
    }

    private static async Task<BotNetworkSession> ConnectAsync(string bot, string account)
        => await BotNetworkSession.ConnectAsync(bot, account, Password,
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

    private static JsonElement Call(BotDriveClient bridge, string stage, string request, List<string> stages, int timeoutMs = 30_000)
    {
        stages.Add(stage);
        return bridge.Call(request, timeoutMs);
    }

    private static JsonElement InventoryItem(BotDriveClient bridge, string bot, ulong itemId, List<string> stages)
    {
        var items = Call(bridge, $"inv-{bot}", $"{{\"cmd\":\"mail\",\"op\":\"inv\",\"bot\":\"{bot}\"}}", stages).GetProperty("items");
        return items.EnumerateArray().Single(i => i.GetProperty("itemId").GetUInt64() == itemId);
    }

    private static List<JsonElement> EnumerateMails(BotDriveClient bridge, string bot, List<string> stages)
        => Call(bridge, $"mails-{bot}", $"{{\"cmd\":\"mail\",\"op\":\"mails\",\"bot\":\"{bot}\"}}", stages)
            .GetProperty("mails").EnumerateArray().Select(m => m.Clone()).ToList();

    private static List<JsonElement> WaitForMail(BotDriveClient bridge, string bot, Func<JsonElement, bool> predicate, List<string> stages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var mails = EnumerateMails(bridge, bot, stages);
            if (mails.Any(predicate))
                return mails.Where(predicate).ToList();
            Thread.Sleep(250);
        }
        throw new TimeoutException($"mail for {bot} did not reach expected state");
    }


    private static BotTcpLink GetGameLink(BotNetworkSession session)
        => (BotTcpLink)typeof(BotNetworkSession)
            .GetField("_game", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    private static void StopBackgroundLoops(BotNetworkSession session)
    {
        if (typeof(BotNetworkSession)
                .GetField("_keepAliveCts", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) is CancellationTokenSource cts)
            cts.Cancel();
    }

    private static void Drain(BotTcpLink link) => _ = link.DrainAll();

    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xff));
        stream.Write((byte)((value >> 8) & 0xff));
        stream.Write((byte)((value >> 16) & 0xff));
    }

    private sealed record ItemRow(ulong Id, uint OwnerId, int SlotType, byte Slot, uint TemplateId, int Count, byte Grade, byte[] Details);
    private sealed record MailRow(long Id, uint ReceiverId, byte Status, long Attachment0, int MoneyAmount1);

    private static ItemRow QueryItem(ulong id)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, owner, slot_type, slot, template_id, count, grade, details FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), $"items row {id} missing");
        return new ItemRow(r.GetUInt64(0), r.GetUInt32(1), r.GetInt32(2), Convert.ToByte(r.GetValue(3)), r.GetUInt32(4), r.GetInt32(5), Convert.ToByte(r.GetValue(6)), (byte[])r.GetValue(7));
    }

    private static MailRow? QueryMail(long id)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, receiver_id, status, attachment0, money_amount_1 FROM mails WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new MailRow(r.GetInt64(0), r.GetUInt32(1), Convert.ToByte(r.GetValue(2)), r.IsDBNull(3) ? 0 : r.GetInt64(3), r.GetInt32(4)) : null;
    }

    private static void WriteEvidence(DateTime startedAt, uint senderId, uint receiverId, ulong itemId, long mailId, List<string> stages)
    {
        try
        {
            var path = Path.Combine(E2eStack.E2eRoot, "logs", "mail-s3-restart-e2e-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                scenario = "MAIL-01 S3",
                startedAt,
                finishedAt = DateTime.UtcNow,
                senderId,
                receiverId,
                itemId,
                mailId,
                stages
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Evidence writing must not hide the real E2E assertion failure.
        }
    }
}
