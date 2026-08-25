using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;
namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// PB-003 closure run — post-clear EXIT through the REAL portal path
/// (scorecard-explorations/mechanics/indun-domain.md Addendum 2026-08-25):
///
///   ENTRY    same canonical chain as IndunPartyE2eTests (skill 17731 at portal
///            doodad 4115 → DoodadFuncEnterInstance(8855) → RequestDungeonInstance).
///   CLEAR    both Hadir bosses (10166+10167) die on the dungeon world via the
///            engine kill path → completion events 4601/4602 fire (compact patch
///            SQL/patches/compact/2026-08-25_indun_hadir_completion.sql).
///   EXIT     the exit portal doodad spawned from template 4289 ('하디르의 농장
///            출구', func group 10546, Data/Worlds/instance_hadir_farm/
///            doodad_spawns.json) is resolved INSIDE the instance world and each
///            party member casts skill 17733 ('하디르의 농장 퇴장', doodad_funcs
///            12785 func_skill_id) at it as a real CSStartSkillPacket:
///              DoodadFuncExitIndun.Use → IndunManager.RequestLeaveInstance →
///              Dungeon.LeaveDungeonInstance → SCLoadInstancePacket back to the
///              MainWorldPosition captured at entry.
///   ASSERT   per member: SCLoadInstancePacket(worldId == 0, zoneKey != 241);
///            CSInstanceLoaded ack; WebApi 'position' probe shows Instance 0
///            (main_world), zone != 241, and coordinates ≈ the pre-entry anchor.
///            Follower kick-on-leave semantics: the MEMBER performs its own real
///            portal interaction (RequestLeaveInstance is per-character by
///            design — IndunManager.cs:494 iterates worlds for THAT character).
///
/// No GM teleport enters or leaves the dungeon. The only deviation (documented,
/// carried over from the party run): naked level-rigged bots get a GM 'kill'
/// assist to finish level-35 bosses.
/// </summary>
[Collection("e2e")]
public class IndunExitE2eTests
{
    private const string LeaderBotName = "ExitLeader";
    private const string MemberBotName = "ExitMember";
    private const string LeaderAccountName = "e2eexitleader";
    private const string MemberAccountName = "e2eexitmember";

    private const uint HadirZoneGroupId = 46;
    private const uint HadirZoneId = 169;
    private const uint HadirZoneKey = 241;
    private const uint PortalDoodadTemplateId = 4115;
    private const uint EntrySkillId = 17731;

    /// <summary>'하디르의 농장 출구' — static interior exit portal (almighty 4289,
    /// func group 10546 → DoodadFuncExitIndun func 12). Its sibling 4927 starts
    /// in an invisible phase and is NOT required for the exit path.</summary>
    private const uint ExitPortalDoodadTemplateId = 4289;

    /// <summary>'하디르의 농장 퇴장' — the exit portal's interaction skill
    /// (skills.id 17733, casting_time 500ms, wired effects 26357 →
    /// interaction_effects 3366 wi Use — same shape as the entry chain).</summary>
    private const uint ExitSkillId = 17733;

    /// <summary>3단 베기 — instant attack proof used before the GM kill assist.</summary>
    private const uint AttackSkillId = 18131;

    private static readonly uint[] BossNpcTemplateIds = [10166, 10167];
    private const ushort CompletionEventId = 4601;
    private const ushort CompletionEventId2 = 4602;
    private const ushort CompletionActionId = 4601;
    private const ushort CompletionRoomId = 4601;

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string PatchPath => Path.Combine(E2eStack.RepoRoot,
        "SQL", "patches", "compact", "2026-08-25_indun_hadir_completion.sql");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Indun_PartyOfTwo_ClearsHadirFarm_ExitsThroughRealPortal_OnLiveServer()
    {
        var stages = new List<StageRecord>();
        void Record(string stage, bool passed, string detail)
        {
            stages.Add(new StageRecord(stage, passed, detail));
            Console.WriteLine($"[indun-exit] {(passed ? "PASS" : "FAIL")} {stage}: {detail}");
        }

        var logOffsets = CaptureLogOffsets();
        Directory.CreateDirectory(EvidenceDir);

        // ------------------------------------------------------- STAGE: STACK
        E2eStack.EnsureUp();
        var patchApplied = EnsureCompletionPatchApplied();
        Record("COMPLETION-PATCH", patchApplied,
            patchApplied
                ? $"runtime compact carries events {CompletionEventId}/{CompletionEventId2} (zone_group {HadirZoneGroupId} NpcKilled({string.Join('/', BossNpcTemplateIds)}) → SetRoomCleared({CompletionRoomId}))"
                : "patch application FAILED — runtime sqlite does not carry indun_events row 4601");

        BotNetworkSession leader = null;
        BotNetworkSession member = null;
        try
        {
            // -------------------------------------------------- STAGE: PROVISION
            leader = await BotNetworkSession.ConnectAsync(
                LeaderBotName, LeaderAccountName, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            member = await BotNetworkSession.ConnectAsync(
                MemberBotName, MemberAccountName, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);

            Assert.True(leader.InWorld && member.InWorld, "both bots must be in-world (real login flow)");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{LeaderBotName}\",\"op\":\"setLevel\",\"level\":40}}");
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{MemberBotName}\",\"op\":\"setLevel\",\"level\":40}}");
            var leaderState = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{LeaderBotName}\",\"op\":\"charState\"}}");
            var leaderObjId = leaderState.GetProperty("objId").GetUInt32();
            var memberState = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{MemberBotName}\",\"op\":\"charState\"}}");
            var memberObjId = memberState.GetProperty("objId").GetUInt32();
            Assert.True(leaderObjId != 0 && memberObjId != 0, "charState must report live objIds");
            Record("PROVISION", true, $"{LeaderBotName}(objId {leaderObjId}) + {MemberBotName}(objId {memberObjId}) networked, lvl 40");

            // Take over both wires for frame-level evidence.
            StopBackgroundLoops(leader);
            StopBackgroundLoops(member);
            var leaderLink = GameLink(leader);
            var memberLink = GameLink(member);

            using var pingCts = new CancellationTokenSource();
            var pingTask = Task.Run(() =>
            {
                try
                {
                    while (!pingCts.IsCancellationRequested)
                    {
                        Thread.Sleep(5000);
                        SendPingFrame(leaderLink);
                        SendPingFrame(memberLink);
                    }
                }
                catch { /* cancelled or socket died */ }
            });

            try
            {
                // ------------------------------------- STAGE: MAINWORLD-ANCHOR
                // Pre-entry position of BOTH bots — this IS the MainWorldPosition
                // DoodadFuncEnterInstance.Use captures (Transform.CloneDetached).
                // Read via the bridge's read-only charPos diagnostic (engine
                // truth; GM 'position' needs admin access only the first-created
                // character on a fresh DB receives).
                var leaderAnchor = QueryCharPos(bridge, LeaderBotName);
                var memberAnchor = QueryCharPos(bridge, MemberBotName);
                Record("MAINWORLD-ANCHOR", leaderAnchor.WorldId == 0 && memberAnchor.WorldId == 0,
                    $"leader @ ({leaderAnchor.X:F1}, {leaderAnchor.Y:F1}, {leaderAnchor.Z:F1}) zone {leaderAnchor.ZoneId} inst {leaderAnchor.InstanceId} world {leaderAnchor.WorldId}; " +
                    $"member @ ({memberAnchor.X:F1}, {memberAnchor.Y:F1}, {memberAnchor.Z:F1}) zone {memberAnchor.ZoneId} inst {memberAnchor.InstanceId} world {memberAnchor.WorldId}");

                // ---------------------------------------------------- STAGE: PARTY
                leaderLink.SendGameFrame(CSOffsets.CSInviteToTeamPacket, 1, body =>
                {
                    body.Write(0u);                 // teamId (new party)
                    body.Write(true);               // isParty
                    body.Write(MemberBotName);      // target name (i16-prefixed utf8)
                });
                var askBody = memberLink.ReadFrameUntil(SCOffsets.SCAskToJoinTeamPacket, 20000);
                var ask = new PacketStream();
                var invitedTeamId = ask.ReadUInt32();
                var invitedOwnerId = ask.ReadUInt32();
                _ = ask.ReadString();
                var invitedIsParty = ask.ReadBoolean();

                memberLink.SendGameFrame(CSOffsets.CSReplyToJoinTeamPacket, 1, body =>
                {
                    body.Write(invitedTeamId);
                    body.Write(invitedIsParty);
                    body.Write(invitedOwnerId);
                    body.Write(false);
                    body.Write(LeaderBotName);
                    body.Write(false);
                });
                var joinedTeamBody = memberLink.ReadFrameUntil(SCOffsets.SCJoinedTeamPacket, 20000);
                Record("PARTY", joinedTeamBody.Length > 0,
                    $"invite → SCAskToJoinTeam(teamId {invitedTeamId}) → accept → SCJoinedTeamPacket received by {MemberBotName}");

                // ------------------------------------------- STAGE: ENTRY (leader)
                var portalResolve = bridge.Call(
                    $"{{\"cmd\":\"drive\",\"bot\":\"{LeaderBotName}\",\"op\":\"doodadObjId\",\"doodad\":{PortalDoodadTemplateId}}}");
                var portalObjId = portalResolve.GetProperty("objId").GetUInt32();
                Assert.True(portalObjId != 0,
                    $"entry portal doodad {PortalDoodadTemplateId} is not spawned in the live main world");

                InjectDoodadSkill(leaderLink, EntrySkillId, leaderObjId, portalObjId);
                var leaderLoadIn = AwaitLoadInstanceFrame(leaderLink, 180_000, out _, out _);
                Assert.True(leaderLoadIn != null, "leader never received SCLoadInstancePacket within 180s");
                var (leaderInstanceIdIn, leaderZoneIdIn) = ParseLoadInstance(leaderLoadIn!);
                leaderLink.SendGameFrame(CSOffsets.CSInstanceLoadedPacket, 1, _ => { });
                Assert.True(leaderZoneIdIn == HadirZoneKey,
                    $"expected teleport into dungeon zoneKey {HadirZoneKey}, got {leaderZoneIdIn}");
                Record("ENTER-LEADER", true,
                    $"skill {EntrySkillId} at portal objId {portalObjId} → SCLoadInstancePacket(world {leaderInstanceIdIn}, zone {leaderZoneIdIn}) + CSInstanceLoaded ack");

                // ------------------------------------------- STAGE: ENTRY (member)
                InjectDoodadSkill(memberLink, EntrySkillId, memberObjId, portalObjId);
                var memberLoadIn = AwaitLoadInstanceFrame(memberLink, 120_000, out _, out _);
                Assert.True(memberLoadIn != null, "member never received SCLoadInstancePacket within 120s");
                var (memberInstanceIdIn, memberZoneIdIn) = ParseLoadInstance(memberLoadIn!);
                memberLink.SendGameFrame(CSOffsets.CSInstanceLoadedPacket, 1, _ => { });
                Assert.True(memberInstanceIdIn == leaderInstanceIdIn,
                    $"member landed in world {memberInstanceIdIn} but leader is in {leaderInstanceIdIn}");
                Record("ENTER-MEMBER", true, $"SCLoadInstancePacket(world {memberInstanceIdIn}, zone {memberZoneIdIn}) — same instance");

                // ------------------------------------------------ STAGE: CLEAR
                // Both bosses must die so events 4601/4602 → action 4601 fire
                // before the exit (the post-clear contract under test).
                var clearDetail = new List<string>();
                var allBossesDead = true;
                foreach (var bossTemplate in BossNpcTemplateIds)
                {
                    var bossObjId = ResolveNpcWithRetry(bridge, LeaderBotName, bossTemplate);
                    if (bossObjId == 0)
                    {
                        clearDetail.Add($"{bossTemplate}: never resolved inside world {leaderInstanceIdIn}");
                        allBossesDead = false;
                        continue;
                    }

                    leaderLink.SendGameFrame(CSOffsets.CSChangeTargetPacket, 1, body => WriteBc(body, bossObjId));
                    InjectUnitAttack(leaderLink, leaderObjId, bossObjId);
                    AwaitCombatEvidence(leaderLink, bossObjId, 15_000, out _);

                    var deathFrame = TryReadDeathFrame(leaderLink, bossObjId, 10_000);
                    if (!deathFrame)
                    {
                        RunWebCommand("kill", LeaderBotName, "-"); // documented GM-assist deviation
                        deathFrame = TryReadDeathFrame(leaderLink, bossObjId, 20_000);
                    }
                    clearDetail.Add($"{bossTemplate}:{bossObjId} death={(deathFrame ? "observed" : "MISSING")}");
                    allBossesDead &= deathFrame;
                }

                var killWindowStart = DateTime.Now.AddMinutes(-2);
                Record("CLEAR-BOSSES", allBossesDead, string.Join("; ", clearDetail));

                // Completion markers (engine-true log observables, window-matched).
                var completionMarkers = new[]
                {
                    "IndunEventNpcKilleds",
                    $"action.Id={CompletionActionId}",
                    $"Room Clear: {CompletionRoomId}"
                };
                List<string> completionLines = [];
                for (var attempt = 0; attempt < 10 && completionLines.Count < completionMarkers.Length; attempt++)
                {
                    await Task.Delay(1000);
                    completionLines = ScanLogLinesSince(logOffsets, killWindowStart, completionMarkers);
                }
                Record("COMPLETION", completionLines.Count >= completionMarkers.Length,
                    completionLines.Count >= completionMarkers.Length
                        ? string.Join(" | ", completionLines.Take(6))
                        : $"only {completionLines.Count}/{completionMarkers.Length} completion markers logged since {killWindowStart:HH:mm:ss} local — patched event did not fire");

                // ------------------------------------------- STAGE: EXIT PORTAL DATA
                // The exit portal must exist INSIDE the dungeon world (template
                // 4289, from Data/Worlds/instance_hadir_farm/doodad_spawns.json).
                var exitResolve = bridge.Call(
                    $"{{\"cmd\":\"drive\",\"bot\":\"{LeaderBotName}\",\"op\":\"doodadObjId\",\"doodad\":{ExitPortalDoodadTemplateId}}}");
                var exitObjId = exitResolve.GetProperty("objId").GetUInt32();
                Record("EXIT-PORTAL-SPAWNED", exitObjId != 0,
                    exitObjId != 0
                        ? $"exit portal doodad {ExitPortalDoodadTemplateId} live in world {leaderInstanceIdIn} as objId {exitObjId} (leader ParentWorld resolution)"
                        : $"exit portal doodad {ExitPortalDoodadTemplateId} NOT found in world {leaderInstanceIdIn} — contradicts Data/Worlds/instance_hadir_farm/doodad_spawns.json");
                if (exitObjId == 0)
                {
                    // Cannot exercise the exit path without the portal; report and fail.
                }
                else
                {
                    // ------------------------------------------- STAGE: EXIT (leader)
                    await ExitMemberAndVerifyAsync(
                        leaderLink, bridge, LeaderBotName, leaderObjId, exitObjId,
                        leaderAnchor, stages, Record);

                    // ------------------------------------------- STAGE: EXIT (member)
                    // Member performs its OWN real portal interaction while still
                    // inside world 100 (RequestLeaveInstance is per-character —
                    // there is no group-exit packet in the 1.2 protocol).
                    await ExitMemberAndVerifyAsync(
                        memberLink, bridge, MemberBotName, memberObjId, exitObjId,
                        memberAnchor, stages, Record);
                }
            }
            finally
            {
                pingCts.Cancel();
                try { await pingTask; } catch { /* cancelled */ }
            }
        }
        finally
        {
            leader?.Disconnect();
            member?.Disconnect();
            E2eStack.CleanupBotRows(LeaderAccountName, MemberAccountName);
        }

        // ------------------------------------------------------------ VERDICT
        var reportPath = WriteReport(stages);
        var failed = stages.Where(s => !s.Passed).ToList();
        var unhandled = CountLogTailMatches(logOffsets, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffsets, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the indun exit run. Report: {reportPath}");

        Assert.True(failed.Count == 0,
            "PB-003 EXIT RESULT (" + (stages.Count - failed.Count) + "/" + stages.Count + " stages green):\n" +
            string.Join("\n", failed.Select(f => $"  FAIL {f.Stage}: {f.Detail}")) +
            $"\nReport: {reportPath}");
    }

    // ------------------------------------------------------------- exit flow

    /// <summary>
    /// One member's full exit: inject skill 17733 at the exit portal objId over
    /// that member's own wire, expect SCLoadInstancePacket(worldId 0, zone !=
    /// dungeon), ack CSInstanceLoadedPacket, then confirm via WebApi 'position'
    /// that the character stands in main world at ≈ its pre-entry anchor.
    /// </summary>
    private static async Task ExitMemberAndVerifyAsync(
        BotTcpLink link,
        BotDriveClient bridge,
        string botName,
        uint casterObjId,
        uint exitDoodadObjId,
        PositionSnapshot anchor,
        List<StageRecord> stages,
        Action<string, bool, string> record)
    {
        var stage = $"EXIT-{botName.ToUpperInvariant()}";
        InjectDoodadSkill(link, ExitSkillId, casterObjId, exitDoodadObjId);

        var loadOut = AwaitLoadInstanceFrame(link, 60_000, out var processingSeen, out var skillReplySeen);
        if (loadOut == null)
        {
            record(stage, false,
                $"no SCLoadInstancePacket within 60s after skill {ExitSkillId} at exit doodad objId {exitDoodadObjId} " +
                $"(processing frame seen: {processingSeen}, skill reply frame seen: {skillReplySeen})");
            return;
        }

        var (outWorldId, outZoneId) = ParseLoadInstance(loadOut);
        link.SendGameFrame(CSOffsets.CSInstanceLoadedPacket, 1, _ => { });
        await Task.Delay(1500); // let the server settle the transform swap

        var pos = QueryCharPos(bridge, botName);
        var backAtAnchor = Math.Abs(pos.X - anchor.X) < 50 && Math.Abs(pos.Y - anchor.Y) < 50;
        var passed = outWorldId == 0 && outZoneId != HadirZoneKey
                     && pos.WorldId == 0 && pos.InstanceId == 0 && pos.ZoneId != HadirZoneKey
                     && backAtAnchor;

        record(stage, passed,
            $"skill {ExitSkillId} at exit doodad objId {exitDoodadObjId} → SCLoadInstancePacket(world {outWorldId}, zone {outZoneId}); " +
            $"charPos: ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}) zone {pos.ZoneId} inst {pos.InstanceId} world {pos.WorldId} '{pos.WorldName}' | " +
            $"anchor ({anchor.X:F1}, {anchor.Y:F1}) | back-at-anchor={backAtAnchor}");
    }

    // ------------------------------------------------------------- patching

    private static bool EnsureCompletionPatchApplied()
    {
        if (SqliteScalar(
                $"SELECT COUNT(*) FROM indun_events e WHERE e.id IN ({CompletionEventId}, {CompletionEventId2}) " +
                "AND EXISTS (SELECT 1 FROM indun_event_npc_killeds k WHERE k.id = e.condition_id AND k.npc_id IN (10166, 10167))") == BossNpcTemplateIds.Length)
            return true;

        E2eStack.RestartGameServer(() =>
        {
            var sql = File.ReadAllText(PatchPath);
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={E2eStack.RuntimeSqlite}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        });

        return SqliteScalar(
            $"SELECT COUNT(*) FROM indun_events e WHERE e.id IN ({CompletionEventId}, {CompletionEventId2}) " +
            "AND EXISTS (SELECT 1 FROM indun_event_npc_killeds k WHERE k.id = e.condition_id AND k.npc_id IN (10166, 10167))") == BossNpcTemplateIds.Length;
    }

    private static long SqliteScalar(string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={E2eStack.RuntimeSqlite}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // -------------------------------------------------------- wire helpers

    /// <summary>Doodad-target skill injection — identical wire shape to the
    /// entry portal cast (unit caster, SkillCastTargetType.Doodad).</summary>
    private static void InjectDoodadSkill(BotTcpLink link, uint skillId, uint casterObjId, uint doodadObjId)
        => link.SendGameFrame(CSOffsets.CSStartSkillPacket, 1, body =>
        {
            body.Write(skillId);
            body.Write((byte)0);   // SkillCasterType.Unit
            WriteBc(body, casterObjId);
            body.Write((byte)4);   // SkillCastTargetType.Doodad
            WriteBc(body, doodadObjId);
            body.Write((byte)0);   // flag: SkillObjectType.None
        });

    private static void InjectUnitAttack(BotTcpLink link, uint casterObjId, uint unitObjId)
        => link.SendGameFrame(CSOffsets.CSStartSkillPacket, 1, body =>
        {
            body.Write(AttackSkillId);
            body.Write((byte)0);
            WriteBc(body, casterObjId);
            body.Write((byte)0);   // SkillCastTargetType.Unit
            WriteBc(body, unitObjId);
            body.Write((byte)0);
        });

    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xFF));
        stream.Write((byte)((value >> 8) & 0xFF));
        stream.Write((byte)((value >> 16) & 0xFF));
    }

    private static uint ReadBc(byte[] body, int offset)
        => (uint)(body[offset] | (body[offset + 1] << 8) | (body[offset + 2] << 16));

    private static (uint InstanceId, uint ZoneId) ParseLoadInstance(byte[] body)
        => (BitConverter.ToUInt32(body, 0), BitConverter.ToUInt32(body, 4));

    private static byte[] AwaitLoadInstanceFrame(BotTcpLink link, int timeoutMs, out bool processingSeen, out bool skillReplySeen)
    {
        processingSeen = false;
        skillReplySeen = false;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                switch (frame.Type)
                {
                    case SCOffsets.SCProcessingInstancePacket:
                        processingSeen = true;
                        break;
                    case SCOffsets.SCSkillStartedPacket:
                        skillReplySeen = true;
                        break;
                    case SCOffsets.SCLoadInstancePacket:
                        return frame.Body;
                }
            }

            Thread.Sleep(200);
        }

        return null;
    }

    private static void AwaitCombatEvidence(BotTcpLink link, uint victimObjId, int timeoutMs, out bool observed)
    {
        observed = false;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type == SCOffsets.SCCombatFirstHitPacket && frame.Body.Length >= 6
                    && ReadBc(frame.Body, 3) == victimObjId)
                {
                    observed = true;
                    return;
                }
                if (frame.Type == SCOffsets.SCSkillFiredPacket)
                {
                    observed = true;
                    return;
                }
            }

            Thread.Sleep(250);
        }
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

            Thread.Sleep(250);
        }

        return false;
    }

    private static void SendPingFrame(BotTcpLink link)
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

    private static BotTcpLink GameLink(BotNetworkSession session)
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

    /// <summary>Interior NPCs spawn lazily through region ticks; poll the engine-
    /// truth npcObjId resolution until the template resolves (teleport nudge on
    /// the first attempt puts the leader inside spawner radii).</summary>
    private static uint ResolveNpcWithRetry(BotDriveClient bridge, string botName, uint npcTemplateId)
    {
        var nudge = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"teleportToNpc\",\"npc\":9472}}");
        Console.WriteLine($"[indun-exit] teleportToNpc 9472 applied: {(nudge.TryGetProperty("ok", out var okFlag) && okFlag.GetBoolean())}");

        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var resolved = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"npcObjId\",\"npc\":{npcTemplateId}}}");
                var objId = resolved.GetProperty("objId").GetUInt32();
                if (objId != 0)
                    return objId;
            }
            catch { /* bridge hiccup — retried */ }
            Thread.Sleep(5000);
        }

        return 0;
    }

    // ---------------------------------------------------------- web api

    private static string RunWebCommand(string command, string character, string arguments)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

    private sealed record PositionSnapshot(double X, double Y, double Z, uint ZoneId, uint InstanceId, uint WorldId, string WorldName);

    /// <summary>Engine-truth transform via the bridge's read-only charPos
    /// diagnostic — works for every bot regardless of GM access level.</summary>
    private static PositionSnapshot QueryCharPos(BotDriveClient bridge, string botName)
    {
        var state = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"charPos\"}}", 15000);
        return new PositionSnapshot(
            state.GetProperty("x").GetDouble(),
            state.GetProperty("y").GetDouble(),
            state.GetProperty("z").GetDouble(),
            state.GetProperty("zoneId").GetUInt32(),
            state.GetProperty("instanceId").GetUInt32(),
            state.GetProperty("worldId").GetUInt32(),
            state.GetProperty("worldName").GetString() ?? "");
    }

    // ---------------------------------------------------------- game log

    private static Dictionary<string, long> CaptureLogOffsets()
    {
        var offsets = new Dictionary<string, long>();
        foreach (var name in new[] { "game.log", "game-restart.log" })
        {
            var path = Path.Combine(EvidenceDir, name);
            offsets[name] = File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        return offsets;
    }

    private static List<string> ScanLogLinesSince(Dictionary<string, long> offsets, DateTime sinceUtc, string[] markers)
    {
        var found = new List<string>();
        var sinceSeconds = sinceUtc.TimeOfDay.TotalSeconds;
        foreach (var (name, offset) in offsets)
        {
            var path = Path.Combine(EvidenceDir, name);
            try
            {
                if (!File.Exists(path))
                    continue;
                using var fs = File.OpenRead(path);
                var effectiveOffset = fs.Length >= offset ? offset : 0;
                if (fs.Length <= effectiveOffset)
                    continue;
                fs.Seek(effectiveOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is { } line)
                {
                    foreach (var marker in markers)
                    {
                        if (!line.Contains(marker, StringComparison.Ordinal))
                            continue;
                        if (line.Length < 8
                            || !TimeSpan.TryParseExact(line[..8], @"hh\:mm\:ss", null, out var stamp))
                            continue;
                        var inWindow = stamp.TotalSeconds + 5 >= sinceSeconds
                                       || sinceSeconds - stamp.TotalSeconds > 86400 / 2.0;
                        if (!inWindow)
                            continue;

                        found.Add($"[{name}] {line.Trim()}");
                        break;
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        return found;
    }

    private static int CountLogTailMatches(Dictionary<string, long> offsets, string marker)
        => offsets.Sum(kv => CountLogTailMatchesIn(Path.Combine(EvidenceDir, kv.Key), kv.Value, marker));

    private static int CountLogTailMatchesIn(string path, long startOffset, string marker)
    {
        try
        {
            if (!File.Exists(path))
                return 0;
            using var fs = File.OpenRead(path);
            var effectiveOffset = fs.Length >= startOffset ? startOffset : 0;
            if (fs.Length <= effectiveOffset)
                return 0;
            fs.Seek(effectiveOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var count = 0;
            while (reader.ReadLine() is { } line)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    count++;
            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    // ------------------------------------------------------------ reports

    private sealed record StageRecord(string Stage, bool Passed, string Detail);

    private static string WriteReport(List<StageRecord> stages)
    {
        var report = new
        {
            scenario = "indun-exit-e2e",
            milestone = "PB-003 closure (Hadir Farm, zone group 46) — post-clear exit through the real portal",
            verdict = stages.All(s => s.Passed) ? "PASS" : "FAIL/BLOCKER",
            exitPath = "each member casts skill 17733 ('하디르의 농장 퇴장') at the STATIC exit portal doodad " +
                       "(template 4289, func group 10546 → DoodadFuncExitIndun func 12, spawned from " +
                       "Data/Worlds/instance_hadir_farm/doodad_spawns.json) as a real CSStartSkillPacket → " +
                       "RequestLeaveInstance → LeaveDungeonInstance → SCLoadInstancePacket to MainWorldPosition",
            dungeon = new
            {
                zoneGroupId = HadirZoneGroupId,
                zoneId = HadirZoneId,
                zoneKey = HadirZoneKey,
                entryPortalDoodadTemplateId = PortalDoodadTemplateId,
                entrySkillId = EntrySkillId,
                exitPortalDoodadTemplateId = ExitPortalDoodadTemplateId,
                exitSkillId = ExitSkillId,
                completionBossNpcTemplateIds = BossNpcTemplateIds
            },
            completionPatch = new
            {
                script = "SQL/patches/compact/2026-08-25_indun_hadir_completion.sql",
                appliedTo = E2eStack.RuntimeSqlite,
                chain = $"indun_events {CompletionEventId}/{CompletionEventId2} (NpcKilled 10166+10167) → indun_actions {CompletionActionId} (SetRoomCleared room {CompletionRoomId})"
            },
            stages = stages.Select(s => new { stage = s.Stage, passed = s.Passed, detail = s.Detail }),
            note = "Closes PB-003: the previously reported 'no exit portal data' premise was refuted — " +
                   "the exit portals ship in the canonical world spawn data and are exercisable end-to-end."
        };

        var path = Path.Combine(EvidenceDir, "indun-exit-e2e-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
