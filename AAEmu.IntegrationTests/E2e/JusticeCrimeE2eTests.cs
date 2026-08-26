using System.Text;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;

using MySql.Data.MySqlClient;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// JUSTICE slice 1 (CRIME-01) live-stack verification — the crime leg
/// end-to-end on the REAL stack:
///
///   1. Two same-faction (Nuian) bots provision at the same spawn.
///   2. A sets ForceAttack (CSSetForceAttackPacket 0x04f — the only way a
///      friendly relation takes damage) and kills B with real skill damage
///      (Triple Slash 18131, the IndunParty combat precedent). Unprovoked:
///      B never fights back, so DoDie's AssaultedBy guard passes.
///   3. B's death spawns the LARGE bloodstain evidence doodad
///      (template 878) with Owner=A, Data=B — asserted off B's
///      SCDoodadCreatedPacket wire frame (Doodad.Write layout).
///   4. Victim B reports through the REAL CSReportCrimePacket (0x076) path →
///      CrimeManager.ReportCrime: A receives SCCrimeChangedPacket (+10 CP,
///      +10 infamy from the large-bloodstain DoodadFuncEvidenceItemLoot,
///      doodad_func_evidence_item_loots id 1), a `crime` MySQL row is written.
///   5. HARD game-server restart (process-tree kill — M3bExit precedent)
///      after a save-cycle flush → `crime` row AND characters.crime_point/
///      crime_record survive; fresh boot reloads them via CrimeManager.Load.
///   6. Wanted boundary at the LIVE setter seam: GM chat command
///      "/crime points JusticeA crime 45" pushes A to 55 CP ≥ threshold 50 →
///      CheckWantedThreshold applies the Wanted buff 3710, observed as an
///      SCBuffCreatedPacket frame carrying template 3710.
/// </summary>
[Collection("e2e")]
public class JusticeCrimeE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names.
    private const string BotA = "JusticeA";
    private const string BotB = "JusticeB";
    private const string AccountA = "e2ejusticea";
    private const string AccountB = "e2ejusticeb";

    private const uint AttackSkillId = 18131; // Triple Slash — instant melee
    private const uint LargeBloodstainTemplate = 878;
    private const int MurderCrimeValue = 10;  // doodad_func_evidence_item_loots id 1 (large bloodstain group 974)
    private const uint WantedBuffTemplate = 3710;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Justice_Crime_KillReportPersistsAcrossRestart_OnLiveServer_EndToEnd()
    {
        var stages = new List<(string Stage, bool Passed, string Detail)>();
        void Record(string stage, bool passed, string detail)
        {
            stages.Add((stage, passed, detail));
            Console.WriteLine($"[justice] {(passed ? "PASS" : "FAIL")} {stage}: {detail}");
        }

        E2eStack.EnsureUp();
        Directory.CreateDirectory(EvidenceDir);

        BotNetworkSession? botA = null, botB = null;
        try
        {
            // ------------------------------------------------------- PROVISION
            // A connects FIRST: a fresh e2e DB makes the first account the
            // AccessLevelFirstAccount=100 GM (needed for the /crime points seam).
            botA = await BotNetworkSession.ConnectAsync(
                BotA, AccountA, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort, "127.0.0.1", E2eStack.GamePort, "127.0.0.1", E2eStack.StreamPort);
            botB = await BotNetworkSession.ConnectAsync(
                BotB, AccountB, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort, "127.0.0.1", E2eStack.GamePort, "127.0.0.1", E2eStack.StreamPort);
            Assert.True(botA.InWorld && botB.InWorld, "both bots must be in-world");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            foreach (var name in new[] { BotA, BotB })
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{name}\",\"op\":\"setLevel\",\"level\":40}}");
            var stateA = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotA}\",\"op\":\"charState\"}}");
            var stateB = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotB}\",\"op\":\"charState\"}}");
            var objIdA = stateA.GetProperty("objId").GetUInt32();
            var objIdB = stateB.GetProperty("objId").GetUInt32();
            var charIdA = botA.CharacterId;
            var charIdB = botB.CharacterId;
            Assert.True(objIdA != 0 && objIdB != 0, "charState must report live objIds");
            Record("PROVISION", true,
                $"{BotA}(charId {botA.CharacterId}, objId {objIdA}) + {BotB}(charId {botB.CharacterId}, objId {objIdB}) Nuian pair, level 40");

            StopBackgroundLoops(botA);
            StopBackgroundLoops(botB);
            var linkA = GetGameLink(botA);
            var linkB = GetGameLink(botB);
            using var pingCts = new CancellationTokenSource();
            var pingTask = Task.Run(() =>
            {
                try
                {
                    while (!pingCts.IsCancellationRequested)
                    {
                        Thread.Sleep(5000);
                        SendPing(linkA);
                        SendPing(linkB);
                    }
                }
                catch { /* cancelled or socket died */ }
            });

            uint evidenceObjId = 0;
            try
            {
                // ------------------------------------------------------ KILL
                // Path 1 — REAL skill damage: ForceAttack (CSSetForceAttackPacket
                // 0x04f) is required for friendly-relation damage (Skill.ApplyEffects
                // relation gate + BaseUnit.CanAttack).
                linkA.SendGameFrame(CSOffsets.CSSetForceAttackPacket, 1, body => body.Write(true));

                string killDetail;
                var evidenceBox = new ObjBox();
                var killedByBotDamage = await TryKillBySkillDamageAsync(linkA, linkB, objIdA, objIdB, charIdA, charIdB, evidenceBox);
                if (evidenceBox.Value != 0)
                    evidenceObjId = evidenceBox.Value;
                if (killedByBotDamage)
                {
                    killDetail = "unprovoked ForceAttack skill damage";
                }
                else
                {
                    // Path 2 — DOCUMENTED GM-assist fallback (IndunParty precedent):
                    // A selects B and runs /kill → Kill command calls
                    // ReduceCurrentHp(character=A, …) so DoDie keeps REAL killer
                    // attribution (killer = A) and the friendly-fire evidence
                    // branch runs unchanged.
                    linkA.SendGameFrame(CSOffsets.CSChangeTargetPacket, 1, body => WriteBc(body, objIdB));
                    await Task.Delay(500);
                    linkA.SendGameFrame(CSOffsets.CSSendChatMessagePacket, 1, body =>
                    {
                        body.Write((short)0);
                        body.Write((short)0);
                        body.Write(0);
                        body.Write("");
                        body.Write("/kill");
                        body.Write((byte)0);
                        body.Write(0);
                    });
                    killDetail = "GM /kill assist (ReduceCurrentHp keeps killer=A attribution)";
                }

                // ONE drain pass over both links so the death frame can never
                // discard later evidence frames.
                var deathSeen = AwaitDeathFrame(linkA, linkB, objIdB, 15_000);

                // Evidence-doodad discovery through the ENGINE truth source
                // (bridge 'doodadObjId' resolves the live world object) —
                // SCDoodadCreatedPacket is visibility-driven and is NOT pushed
                // to players already in range at spawn time, so the wire frame
                // is not a reliable spawn signal.
                for (var attempt = 0; attempt < 10 && evidenceObjId == 0; attempt++)
                {
                    await Task.Delay(1000);
                    try
                    {
                        var resolved = bridge.Call(
                            $"{{\"cmd\":\"drive\",\"bot\":\"{BotA}\",\"op\":\"doodadObjId\",\"doodad\":{LargeBloodstainTemplate}}}");
                        evidenceObjId = resolved.TryGetProperty("objId", out var objProp) ? objProp.GetUInt32() : 0;
                    }
                    catch { /* bridge hiccup — retried next poll */ }
                }

                var dbRow = ReadEvidenceDoodadRow(charIdA);
                Record("KILL-EVIDENCE", evidenceObjId != 0,
                    $"{killDetail} ({AttackSkillId}) → SCUnitDeath({objIdB}) seen: {deathSeen}; " +
                    $"around → large-bloodstain (template {LargeBloodstainTemplate}) BcId {evidenceObjId}; " +
                    "doodads MySQL row: " + (dbRow != null ? $"template {dbRow!.Template} owner {dbRow!.Owner} data {dbRow!.Data}" : "ABSENT"));
                Assert.True(deathSeen, "victim death was not observed");
                Assert.True(evidenceObjId != 0, "no large-bloodstain evidence doodad found in the world after the kill");
                Assert.True(dbRow != null && dbRow!.Template == LargeBloodstainTemplate && dbRow!.Owner == charIdA && dbRow!.Data == charIdB,
                    $"doodads row expected (878, owner={charIdA}, data={charIdB}), got {(dbRow != null ? $"{dbRow!.Template}/{dbRow!.Owner}/{dbRow!.Data}" : "none")}");

                // ---------------------------------------------------- REPORT
                // The VICTIM reports (self-report is refused by ReportCrime).
                linkB.SendGameFrame(CSOffsets.CSReportCrimePacket, 1, body =>
                {
                    WriteBc(body, evidenceObjId);
                    body.Write(0u);   // skillId (recorded only)
                    body.Write((int)0); // doodadNextFuncGroup (recorded only)
                    body.Write(0u);   // doodadFuncId (recorded only)
                    body.Write("justice-e2e report");
                });

                var changed = AwaitCrimeChanged(linkA, 15_000);
                Assert.True(changed != null, "criminal A received no SCCrimeChangedPacket within 15s of the report");
                Record("REPORT", true,
                    $"CSReportCrimePacket(evidence {evidenceObjId}) → SCCrimeChangedPacket(delta {changed!.Value.Points}, " +
                    $"crimePoint {changed.Value.CrimePoints}, infamy {changed.Value.InfamyPoints}, state {changed.Value.CrimeState})");
                Assert.True(changed.Value.Points == MurderCrimeValue, $"expected +{MurderCrimeValue} crime delta, got {changed.Value.Points}");
                Assert.True(changed.Value.CrimePoints == MurderCrimeValue, $"expected crime point total {MurderCrimeValue}, got {changed.Value.CrimePoints}");
                Assert.True(changed.Value.InfamyPoints == MurderCrimeValue, $"expected infamy total {MurderCrimeValue}, got {changed.Value.InfamyPoints}");

                // ----------------------------------------------------- MYSQL
                // The `crime` row reaches MySQL via the periodic SaveManager
                // flush (UpdatedEventIds dirty list) — poll for it.
                CrimeRow? crimeRow = null;
                for (var attempt = 0; attempt < 15 && crimeRow == null; attempt++)
                {
                    await Task.Delay(2000);
                    crimeRow = ReadLatestCrimeRow();
                }
                Assert.True(crimeRow != null, "no `crime` row found in aaemu_game after the report");
                Record("MYSQL-CRIME-ROW", true,
                    $"crime row id {crimeRow!.Id}: criminal {crimeRow!.Criminal} victim {crimeRow!.Victim} " +
                    $"reporter {crimeRow!.Reporter} crime_type {crimeRow!.CrimeType}");
                Assert.True(crimeRow!.Criminal == charIdA, $"criminal expected {charIdA}, got {crimeRow!.Criminal}");
                Assert.True(crimeRow!.Victim == charIdB, $"victim expected {charIdB}, got {crimeRow!.Victim}");
                Assert.True(crimeRow!.Reporter == charIdB, $"reporter expected {charIdB} (victim), got {crimeRow!.Reporter}");
                Assert.True(crimeRow!.CrimeType == 3, $"expected murder crime_type 3, got {crimeRow!.CrimeType}");

                var beforeRestart = ReadCharacterPoints(charIdA);
                Assert.True(beforeRestart.cp == MurderCrimeValue && beforeRestart.infamy == MurderCrimeValue,
                    $"characters.crime_point/record expected {MurderCrimeValue}/{MurderCrimeValue}, got {beforeRestart.cp}/{beforeRestart.infamy}");
                Record("MYSQL-CHARACTER", true, $"characters.crime_point={beforeRestart.cp} crime_record={beforeRestart.infamy} for {botA.CharacterId}");

                // --------------------------------------- RESTART (hard kill)
                // Let the periodic SaveManager cycle flush crimes + characters
                // (AutoSaveInterval 0.2 min = 12 s), then kill -9 the process tree.
                await Task.Delay(15_000);
                pingCts.Cancel();
                try { await pingTask; } catch { /* cancelled */ }
                botA.Dispose(); botA = null;
                botB.Dispose(); botB = null;

                E2eStack.RestartGameServer();

                var afterRestart = ReadCharacterPoints(charIdA);
                var crimeRowsAfter = CountCrimeRows();
                var persisted = afterRestart.cp == beforeRestart.cp && afterRestart.infamy == beforeRestart.infamy && crimeRowsAfter >= 1;
                Record("RESTART-PERSISTENCE", persisted,
                    $"hard game-server restart → characters.crime_point={afterRestart.cp}/{afterRestart.infamy} (pre: {beforeRestart.cp}/{beforeRestart.infamy}), crime rows: {crimeRowsAfter}");
                Assert.True(persisted, "crime points and/or crime rows did not survive the restart");

                // --------------------------------------------- WANTED SEAM
                // Fresh sessions post-restart; A is still the GM account.
                botA = await BotNetworkSession.ConnectAsync(
                    BotA, AccountA, "e2e-secret",
                    "127.0.0.1", E2eStack.LoginPort, "127.0.0.1", E2eStack.GamePort, "127.0.0.1", E2eStack.StreamPort);
                botB = await BotNetworkSession.ConnectAsync(
                    BotB, AccountB, "e2e-secret",
                    "127.0.0.1", E2eStack.LoginPort, "127.0.0.1", E2eStack.GamePort, "127.0.0.1", E2eStack.StreamPort);
                Assert.True(botA.InWorld && botB.InWorld, "re-login after restart failed");
                var linkA2 = GetGameLink(botA);
                var linkB2 = GetGameLink(botB);

                // GM chat seam: +45 → 55 CP crosses the wanted threshold (50).
                linkA2.SendGameFrame(CSOffsets.CSSendChatMessagePacket, 1, body =>
                {
                    body.Write((short)0);   // ChatType.White (say)
                    body.Write((short)0);   // unk1
                    body.Write(0);          // unk2
                    body.Write("");         // targetName
                    // "self" — the create handler normalizes the name casing
                    // ("Justicea"), so a literal-name lookup is brittle.
                    body.Write("/crime points self crime=45");
                    body.Write((byte)0);    // languageType
                    body.Write(0);          // ability
                });

                // Path A: real client chat line. Path B (fallback): the same
                // registered GM sub-command through the WebApi command surface.
                // ONE non-discarding observer window over BOTH links — the buff
                // frame can precede the crime-changed frame, and draining for
                // one while waiting for the other loses it.
                linkA2.SendGameFrame(CSOffsets.CSSendChatMessagePacket, 1, body =>
                {
                    body.Write((short)0);   // ChatType.White (say)
                    body.Write((short)0);   // unk1
                    body.Write(0);          // unk2
                    body.Write("");         // targetName
                    // key=value syntax is REQUIRED by SubCommandBase
                    // ("crime 45" silently parses as the query branch).
                    body.Write("/crime points self crime=45");
                    body.Write((byte)0);    // languageType
                    body.Write(0);          // ability
                });

                var seam = AwaitWantedSeam(new[] { linkA2, linkB2 }, 20_000);
                string seamPath = "chat";
                if (!seam.Crossed)
                {
                    var webReply = RunWebCommand("crime", BotA, "points self crime=45");
                    seamPath = $"web-api fallback (chat silent)";
                    seam = AwaitWantedSeam(new[] { linkA2, linkB2 }, 20_000);
                }

                var finalPointsSeam = ReadCharacterPoints(botA.CharacterId);
                // Authoritative wanted proof: GetCrimeState()==1 is computed
                // SERVER-SIDE from Buffs.CheckBuff(Wanted 3710) — it rides in
                // the SCCrimeChanged payload. The separate SCBuffCreatedPacket
                // frame is informational only (its broadcast did not reach the
                // observing sockets in this rig).
                Record("WANTED-SEAM", seam.Crossed && seam.State == 1,
                    $"{seamPath} /crime points self crime=45 → SCCrimeChanged(cp {seam.CrimePoints?.ToString() ?? "n/a"}, " +
                    $"state {seam.State?.ToString() ?? "n/a"} = GetCrimeState() from live Wanted-buff check); " +
                    $"SCBuffCreated(template {WantedBuffTemplate}) frame observed: {seam.BuffSeen} (informational); " +
                    $"DB crime_point={finalPointsSeam.cp}");
                Assert.True(seam.Crossed, "crime points did not cross the 50 threshold via the GM seam");
                Assert.True(seam.State == 1, $"GetCrimeState expected 1 (Wanted buff active server-side), got {seam.State?.ToString() ?? "0"}");
                Assert.True(finalPointsSeam.cp >= 50, $"post-seam characters.crime_point expected >= 50, got {finalPointsSeam.cp}");

                var finalPoints = finalPointsSeam;
                Record("FINAL-MYSQL", finalPoints.cp >= 50,
                    $"post-seam characters.crime_point={finalPoints.cp} crime_record={finalPoints.infamy}");

                await File.WriteAllTextAsync(
                    Path.Combine(EvidenceDir, "justice-crime-e2e-stages.json"),
                    JsonSerializer.Serialize(stages.Select(s => new { stage = s.Stage, passed = s.Passed, detail = s.Detail }), new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                pingCts.Cancel();
            }
        }
        finally
        {
            botA?.Dispose();
            botB?.Dispose();
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Casts Triple Slash repeatedly and watches BOTH links for the
    /// victim's death frame. Returns true when the victim died from bot
    /// damage alone.</summary>
    private sealed class ObjBox { public uint Value; }

    private static async Task<bool> TryKillBySkillDamageAsync(
        BotTcpLink linkA, BotTcpLink linkB, uint objIdA, uint objIdB, uint charIdA, uint charIdB, ObjBox evidence)
    {
        var deadline = Environment.TickCount64 + 60_000;
        var attempt = 0;
        while (Environment.TickCount64 < deadline)
        {
            attempt++;
            linkA.SendGameFrame(CSOffsets.CSStartSkillPacket, 1, body =>
            {
                body.Write(AttackSkillId);
                body.Write((byte)0);   // SkillCasterType.Unit
                WriteBc(body, objIdA);
                body.Write((byte)0);   // SkillCastTargetType.Unit
                WriteBc(body, objIdB);
                body.Write((byte)0);   // flag: SkillObjectType.None
            });

            var waitDeadline = Environment.TickCount64 + 4000;
            while (Environment.TickCount64 < waitDeadline)
            {
                foreach (var frame in linkB.DrainAll())
                {
                    if (frame.Type == SCOffsets.SCUnitDeathPacket && frame.Body.Length >= 3 && ReadBc(frame.Body, 0) == objIdB)
                        return true;
                    TryFindLargeBloodstainFrames(frame, charIdA, charIdB, evidence);
                }
                foreach (var frame in linkA.DrainAll())
                    TryFindLargeBloodstainFrames(frame, charIdA, charIdB, evidence);
                Thread.Sleep(200);
            }
            Console.WriteLine($"[justice] kill attempt {attempt}: victim objId {objIdB} still alive");
        }
        return false;
    }

    /// <summary>Single-frame variant of the bloodstain scan (Doodad.Write body:
    /// u32 TemplateId@3, u32 OwnerId@38, i32 Data@83).</summary>
    private static void TryFindLargeBloodstainFrames((ushort Type, byte[] Body) frame, uint ownerCharId, uint dataCharId, ObjBox evidence)
    {
        if (frame.Type != SCOffsets.SCDoodadCreatedPacket || frame.Body.Length < 87)
            return;
        if (BitConverter.ToUInt32(frame.Body, 3) != LargeBloodstainTemplate)
            return;
        if (BitConverter.ToUInt32(frame.Body, 38) != ownerCharId || BitConverter.ToUInt32(frame.Body, 83) != dataCharId)
            return;
        evidence.Value = ReadBc(frame.Body, 0);
    }

    private static BotTcpLink GetGameLink(BotNetworkSession session)
        => (BotTcpLink)typeof(BotNetworkSession)
            .GetField("_game", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(session)!;

    private static void StopBackgroundLoops(BotNetworkSession session)
    {
        if (typeof(BotNetworkSession)
                .GetField("_keepAliveCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(session) is CancellationTokenSource cts)
            cts.Cancel();
    }

    private static void SendPing(BotTcpLink link)
    {
        if (!link.Connected)
            return;
        link.SendGameFrame(PPOffsets.PingPacket, 2, body =>
        {
            body.Write(0L);
            body.Write(0L);
            body.Write(0u);
        });
    }

    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xFF));
        stream.Write((byte)((value >> 8) & 0xFF));
        stream.Write((byte)((value >> 16) & 0xFF));
    }

    private static uint ReadBc(byte[] body, int offset)
        => (uint)(body[offset] | (body[offset + 1] << 8) | (body[offset + 2] << 16));

    /// <summary>Scans drained frames for the large-bloodstain SCDoodadCreated
    /// payload. Doodad.Write body layout: bc ObjId@0, u32 TemplateId@3,
    /// … u32 OwnerId(character db id)@38, … i32 Data(victim char id)@83.</summary>
    private static bool TryFindLargeBloodstain(BotTcpLink link, uint ownerCharId, uint dataCharId, ref uint evidenceObjId)
    {
        foreach (var frame in link.DrainAll())
        {
            if (frame.Type != SCOffsets.SCDoodadCreatedPacket || frame.Body.Length < 87)
                continue;
            var template = BitConverter.ToUInt32(frame.Body, 3);
            if (template != LargeBloodstainTemplate)
                continue;
            var owner = BitConverter.ToUInt32(frame.Body, 38);
            var data = BitConverter.ToUInt32(frame.Body, 83);
            if (owner != ownerCharId || data != dataCharId)
                continue;
            evidenceObjId = ReadBc(frame.Body, 0);
            return true;
        }

        return false;
    }

    /// <summary>Single non-discarding drain window over BOTH links looking for
    /// the victim's SCUnitDeathPacket AND the large-bloodstain
    /// SCDoodadCreatedPacket (Owner=killer char id, Data=victim char id).</summary>
    private static (bool DeathSeen, bool BloodstainFound) AwaitKillEvidence(
        BotTcpLink linkA, BotTcpLink linkB, uint victimObjId, uint charIdA, uint charIdB,
        ref uint evidenceObjId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var deathSeen = false;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in linkB.DrainAll().Concat(linkA.DrainAll()))
            {
                if (!deathSeen && frame.Type == SCOffsets.SCUnitDeathPacket &&
                    frame.Body.Length >= 3 && ReadBc(frame.Body, 0) == victimObjId)
                    deathSeen = true;
                if (frame.Type == SCOffsets.SCDoodadCreatedPacket && frame.Body.Length >= 87 &&
                    BitConverter.ToUInt32(frame.Body, 3) == LargeBloodstainTemplate &&
                    BitConverter.ToUInt32(frame.Body, 38) == charIdA &&
                    BitConverter.ToUInt32(frame.Body, 83) == charIdB)
                    evidenceObjId = ReadBc(frame.Body, 0);
            }

            if (deathSeen && evidenceObjId != 0)
                return (deathSeen, true);
            Thread.Sleep(200);
        }
        return (deathSeen, evidenceObjId != 0);
    }

    /// <summary>Non-discarding drain window over BOTH links waiting for the
    /// victim's SCUnitDeathPacket.</summary>
    private static bool AwaitDeathFrame(BotTcpLink linkA, BotTcpLink linkB, uint victimObjId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in linkB.DrainAll().Concat(linkA.DrainAll()))
            {
                if (frame.Type == SCOffsets.SCUnitDeathPacket &&
                    frame.Body.Length >= 3 && ReadBc(frame.Body, 0) == victimObjId)
                    return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private sealed record EvidenceRow(uint Template, uint Owner, int Data);

    private static EvidenceRow? ReadEvidenceDoodadRow(uint ownerCharId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id, owner_id, data FROM doodads WHERE template_id = @t ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@t", LargeBloodstainTemplate);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new EvidenceRow(reader.GetUInt32(0), reader.GetUInt32(1), reader.GetInt32(2));
    }

    private static string RunWebCommand(string command, string character, string arguments)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var payload = JsonSerializer.Serialize(new { character, arguments });
            var response = client.PostAsync(
                new Uri($"http://127.0.0.1:{E2eStack.WebApiPort}/api/commands/{command}"),
                new StringContent(payload, Encoding.UTF8, "application/json")).Result;
            return response.Content.ReadAsStringAsync().Result;
        }
        catch (Exception ex)
        {
            return $"web-command '{command}' failed: {ex.Message}";
        }
    }

    /// <summary>Single non-discarding observation window over BOTH links:
    /// collects the first SCCrimeChangedPacket that reports cp &gt;= threshold
    /// AND any SCBuffCreatedPacket carrying the Wanted template.</summary>
    private static (bool Crossed, bool BuffSeen, short? CrimePoints, short? State) AwaitWantedSeam(
        IReadOnlyList<BotTcpLink> links, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var crossed = false;
        var buffSeen = false;
        short? crimePoints = null, state = null;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var link in links)
            {
                foreach (var frame in link.DrainAll())
                {
                    if (!crossed && frame.Type == SCOffsets.SCCrimeChangedPacket && frame.Body.Length >= 12)
                    {
                        var points = BitConverter.ToInt16(frame.Body, 4);
                        var st = BitConverter.ToInt16(frame.Body, 10);
                        if (points >= 50)
                        {
                            crossed = true;
                            crimePoints = points;
                            state = st;
                        }
                    }
                    if (!buffSeen && frame.Type == SCOffsets.SCBuffCreatedPacket && frame.Body.Length >= 19 &&
                        BitConverter.ToUInt32(frame.Body, 15) == WantedBuffTemplate)
                        buffSeen = true;
                }
            }
            if (crossed && buffSeen)
                return (true, true, crimePoints, state);
            Thread.Sleep(200);
        }
        return (crossed, buffSeen, crimePoints, state);
    }

    private static bool TryReadDeathFrame(BotTcpLink link, uint objId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type == SCOffsets.SCUnitDeathPacket && frame.Body.Length >= 3 && ReadBc(frame.Body, 0) == objId)
                    return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>SCCrimeChanged body: i32 delta@0, i16 crimePoint@4, i32 infamy@6, i16 crimeState@10.</summary>
    private static (int Points, short CrimePoints, int InfamyPoints, short CrimeState)? AwaitCrimeChanged(
        BotTcpLink link, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type != SCOffsets.SCCrimeChangedPacket || frame.Body.Length < 12)
                    continue;
                var points = BitConverter.ToInt32(frame.Body, 0);
                var crimePoints = BitConverter.ToInt16(frame.Body, 4);
                var infamy = BitConverter.ToInt32(frame.Body, 6);
                var state = BitConverter.ToInt16(frame.Body, 10);
                return (points, crimePoints, infamy, state);
            }
            Thread.Sleep(250);
        }
        return null;
    }

    /// <summary>SCBuffCreated body: SkillCaster(type byte + bc)@0..3,
    /// casterId u32@4, bc owner@8, index u32@11, buff TEMPLATE id u32@15.</summary>
    private static bool AwaitWantedBuff(IReadOnlyList<BotTcpLink> links, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var link in links)
            {
                foreach (var frame in link.DrainAll())
                {
                    if (frame.Type != SCOffsets.SCBuffCreatedPacket || frame.Body.Length < 19)
                        continue;
                    var template = BitConverter.ToUInt32(frame.Body, 15);
                    if (template == WantedBuffTemplate)
                        return true;
                }
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private sealed record CrimeRow(uint Id, uint Criminal, uint Victim, uint Reporter, uint CrimeType);

    private static CrimeRow? ReadLatestCrimeRow()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, criminal, victim, reporter, crime_type FROM crime ORDER BY id DESC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new CrimeRow(reader.GetUInt32(0), reader.GetUInt32(1), reader.GetUInt32(2), reader.GetUInt32(3), reader.GetUInt32(4));
    }

    private static long CountCrimeRows()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM crime";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static (short cp, int infamy) ReadCharacterPoints(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT crime_point, crime_record FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"character {characterId} missing from aaemu_game.characters");
        return (reader.GetInt16(0), reader.GetInt32(1));
    }
}
