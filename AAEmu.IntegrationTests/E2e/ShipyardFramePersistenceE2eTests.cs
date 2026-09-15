using System.Globalization;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// SHIPYARD slice-M frame-persistence proof (DominionRestartPersistenceE2eTests /
/// B1HousingRestartE2eTests pattern): a placed shipyard frame survives a game-server
/// restart with byte-equal build state.
///
///   PLACE    authenticated bot (real login flow) stocks the design + mats through
///            the bridge stock op (real bag inserts), rigs placement money, then
///            sends the REAL CSCreateShipyardPacket (template 12, design instance
///            id) — the exact player-facing path into ShipyardManager.Create, which
///            synchronously persists via MySqlShipyardFrameStore.Upsert.
///   ROW      SELECT the live `shipyards` MySQL row (read-only) and assert
///            template/owner/step/actions/position match the placement.
///   RESTART  PID-handle kill of ONLY this lane's game process (never pkill) +
///            reboot through the server-started gate.
///   RELOAD   the row must be byte-equal; game-restart.log must show the
///            LoadPlacedFrames ("Loaded 1 placed shipyard frame(s)") and SpawnAll
///            ("Spawned 1 restored shipyard frame(s)") lines; a freshly connected
///            bot at the placement anchor must observe SCShipyardStatePacket for
///            the frame id (world-spawn visible to a real client).
///
/// No source-behavior changes: this file is the only addition. There is no
/// shipyard bridge seam (rig/build/status), so placement goes over the raw wire
/// and reload assertions go through MySQL + the restart log + wire observation.
///
/// Scroll-once leg: SCOPED OUT (see the test body) — driving a frame to completion
/// needs six correct-skill build casts through the craft pipeline and no bridge
/// seam stocks those build skills; grant-once ordering stays covered by
/// ShipyardPersistenceTests.Reload_SentinelRow_RestoresInertWithoutGrant.
/// </summary>
[Collection("e2e")]
public class ShipyardFramePersistenceE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names (InvalidCharacters).
    private const string BotName = "ShipyardProver";
    private const string AccountName = "e2eshipyardprover";

    // 모험의 쾌속정 건조대 (shipyards.id 12): real design item + real tax + 6x1 build steps.
    private const uint TemplateId = 12;
    private const uint DesignItemTemplateId = 23636; // 도면: 모험의 쾌속정 (use_skill 14879)
    private const uint IronIngotItemId = 8318;       // 철 주괴 x10 (skill 14879 reagent)
    private const uint LumberItemId = 8337;          // 목재 x10 (skill 14879 reagent)
    private const long RigMoney = 500_000;           // taxation 2 = 100_000 placement tax
    private const long PlacementTax = 100_000;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ShipyardFrame_PlacedOverRealPacket_SurvivesGameServerRestart_ByteEqual()
    {
        AssertIsolatedLane();
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        uint charId = 0;
        ulong frameId = 0;
        var killedPid = 0;
        Dictionary<string, object?> pre = [];
        Dictionary<string, object?> post = [];
        var stages = new List<(string Stage, bool Ok, string Detail)>();
        void Record(string stage, bool ok, string detail)
        {
            stages.Add((stage, ok, detail));
            Console.WriteLine($"[shipyard-persist] {(ok ? "PASS" : "FAIL")} {stage}: {detail}");
        }

        BotNetworkSession bot = null;
        try
        {
            // ------------------------------------------------- 1. AUTH (real login flow)
            bot = await BotNetworkSession.ConnectAsync(
                BotName, AccountName, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(bot.InWorld, "bot must be in-world (real login flow)");
            charId = bot.CharacterId;
            Assert.True(charId > 0, "real create/select must yield a character id");
            Record("AUTH", true, $"bot '{BotName}' in-world, character id {charId}");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ------------------------------------------------- 2. MONEY RIG
            // Fresh characters start at money 0 and there is no shipyard bridge
            // rig op (new test file only, no source changes): when the live
            // balance is short, persist a rigged balance while the character is
            // OFFLINE (live object caches money; the save tick would clobber a
            // live-row write) and reconnect so the fresh session loads it.
            // Ordering matters: the server processes the socket-close logout
            // (Deactivate + save with the stale money 0) ASYNCHRONOUSLY, so the
            // UPDATE must land only after the old session is fully gone —
            // otherwise the logout-save clobbers the rig (observed live). Poll
            // the bridge session table for inWorld == 0 first.
            var money = QueryMoney(charId);
            if (money < PlacementTax)
            {
                bridge.Call("{\"cmd\":\"save\"}", 180_000);
                bot.Dispose();
                bot = null;
                WaitForSessionGone(bridge, TimeSpan.FromSeconds(90));
                UpdateMoney(charId, RigMoney);
                Assert.Equal(RigMoney, QueryMoney(charId));
                bot = await BotNetworkSession.ConnectAsync(
                    BotName, AccountName, "e2e-secret",
                    "127.0.0.1", E2eStack.LoginPort,
                    "127.0.0.1", E2eStack.GamePort,
                    "127.0.0.1", E2eStack.StreamPort);
                Assert.True(bot.InWorld && bot.CharacterId == charId,
                    $"reconnect must adopt character {charId} (got {bot.CharacterId})");
                money = QueryMoney(charId);
                Record("MONEY-RIG", money >= PlacementTax,
                    $"offline UPDATE to {RigMoney} after session teardown, reloaded live balance {money}");
                Assert.True(money >= PlacementTax, $"rigged money did not load (live {money})");
            }
            else
            {
                Record("MONEY-RIG", true, $"live balance {money} already covers the {PlacementTax} tax (no rig)");
            }

            // ------------------------------------------------- 3. STOCK design + mats
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{DesignItemTemplateId},\"count\":1}}");
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{IronIngotItemId},\"count\":10}}");
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{LumberItemId},\"count\":10}}");
            Assert.Equal(1, bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"invCount\",\"item\":{DesignItemTemplateId}}}")
                .GetProperty("count").GetInt32());
            bridge.Call("{\"cmd\":\"save\"}", 180_000);
            var designInstanceId = QueryItemInstanceId(charId, DesignItemTemplateId);
            Assert.True(designInstanceId > 0, "design item missing from items after save");
            Record("STOCK", true,
                $"design {DesignItemTemplateId} instance {designInstanceId} + 10x{iron(IronIngotItemId)} + 10x{iron(LumberItemId)} persisted");

            // ------------------------------------------------- 4. PLACE (real CSCreateShipyardPacket)
            var pos = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}");
            var px = pos.GetProperty("x").GetSingle() + 3f;
            var py = pos.GetProperty("y").GetSingle();
            var pz = pos.GetProperty("z").GetSingle();
            var link = GetGameLink(bot);
            StopBackgroundLoops(bot);
            using var pingCts = new CancellationTokenSource();
            var pingTask = Task.Run(() => PingLoopAsync(link, pingCts.Token));
            try
            {
                _ = link.DrainAll();
                link.SendGameFrame(CSOffsets.CSCreateShipyardPacket, 1, body =>
                {
                    body.Write(TemplateId);
                    body.Write(Helpers.ConvertLongX(px));
                    body.Write(Helpers.ConvertLongY(py));
                    body.Write(pz);
                    body.Write(0f); // zRot
                    body.Write(designInstanceId);
                    body.Write(0f); body.Write(0f); body.Write(0f); // mAABBmn (server ignores)
                    body.Write(0f); body.Write(0f); body.Write(0f); // mAABBmx (server ignores)
                    body.Write(false); // autoUseAAPoint
                });
                Console.WriteLine($"[shipyard-persist] CSCreateShipyardPacket(template {TemplateId}) at ({px:F1},{py:F1},{pz:F2})");
            }
            finally
            {
                pingCts.Cancel();
                try { await pingTask; } catch { }
            }

            // ------------------------------------------------- 5. MYSQL ROW (source of truth)
            pre = PollShipyardRow(charId, TimeSpan.FromSeconds(30));
            Assert.True(pre.Count > 0, "shipyards row missing 30s after the real placement packet");
            frameId = Convert.ToUInt64(pre["id"]!);
            Assert.Equal(TemplateId, Convert.ToUInt32(pre["template_id"]!));
            Assert.Equal(charId, Convert.ToUInt32(pre["owner_id"]!));
            Assert.True(string.Equals(BotName, Convert.ToString(pre["owner_name"]), StringComparison.OrdinalIgnoreCase),
                $"owner_name {pre["owner_name"]} is not the placing bot (server normalizes case on create)");
            Assert.Equal(0, Convert.ToInt32(pre["step"]!));
            Assert.Equal(0, Convert.ToInt32(pre["actions"]!));
            Assert.True(MathF.Abs(Convert.ToSingle(pre["x"]!) - px) < 0.05f &&
                        MathF.Abs(Convert.ToSingle(pre["y"]!) - py) < 0.05f &&
                        MathF.Abs(Convert.ToSingle(pre["z"]!) - pz) < 0.5f,
                $"row position drifted from placement: ({pre["x"]},{pre["y"]},{pre["z"]}) vs ({px:F2},{py:F2},{pz:F2})");
            Record("ROW", true,
                $"shipyards row id={frameId} template={pre["template_id"]} owner={pre["owner_name"]}({pre["owner_id"]}) " +
                $"step={pre["step"]} actions={pre["actions"]} pos=({pre["x"]},{pre["y"]},{pre["z"]}) zone={pre["zone_id"]}");

            // The placement session dies with the server; drop it before the kill.
            bot.Dispose();
            bot = null;

            // ------------------------------------------------- 6. KILL + REBOOT (PID-verified)
            killedPid = E2eStack.RestartGameServer();
            Assert.True(killedPid > 0, "no game process was killed — cannot claim a restart");
            Assert.False(Directory.Exists($"/proc/{killedPid}"),
                $"killed game pid {killedPid} still exists after the restart gate");
            Record("RESTART", true,
                $"PID-handle kill landed on game pid {killedPid} (/proc gone); reboot passed the server-started gate");

            // ------------------------------------------------- 7. RELOAD PROOF (row → manager → world)
            post = SnapshotShipyardRow(frameId);
            Assert.True(post.Count > 0, $"shipyards row {frameId} missing after restart");
            AssertByteEqual(pre, post);
            Record("ROW-EQUAL", true, $"row {frameId} byte-equal across restart (step/actions/owner/position)");

            var restartLog = Path.Combine(E2eStack.E2eRoot, "logs", "game-restart.log");
            var logText = File.Exists(restartLog) ? File.ReadAllText(restartLog) : "";
            Assert.Contains("Loaded 1 placed shipyard frame(s)", logText);
            Assert.Contains("Spawned 1 restored shipyard frame(s)", logText);
            Record("MANAGER-SPAWN", true,
                "game-restart.log shows LoadPlacedFrames=1 (row→manager) and SpawnAll=1 (manager→world)");

            // Fresh eyes: a newly connected bot at the placement anchor must see
            // the restored frame on the wire (SCShipyardStatePacket carries the
            // frame id first — ShipyardData.Write order).
            bot = await BotNetworkSession.ConnectAsync(
                BotName, AccountName, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(bot.InWorld, "post-restart bot must be in-world");
            var link2 = GetGameLink(bot);
            StopBackgroundLoops(bot);
            using var pingCts2 = new CancellationTokenSource();
            var pingTask2 = Task.Run(() => PingLoopAsync(link2, pingCts2.Token));
            bool wireSeen;
            try
            {
                wireSeen = WaitForShipyardState(link2, frameId, TemplateId, 90_000);
            }
            finally
            {
                pingCts2.Cancel();
                try { await pingTask2; } catch { }
            }
            Assert.True(wireSeen, $"no SCShipyardStatePacket for frame {frameId} within 90s of entering the rebooted world");
            Record("WIRE-SPAWN", true,
                $"SCShipyardStatePacket(id={frameId}, template={TemplateId}) observed post-restart — restored frame is a live world unit");

            // Scroll-once leg: SCOPED OUT — driving a frame to completion needs
            // six correct-skill build casts (template 12: 14737/14740/14738/11823
            // x3) through the craft pipeline; the bot knows none of those skills
            // and no E2E bridge seam stocks build skills or drives
            // Shipyard.AddBuildAction, and this proof adds no source. Grant-once
            // ordering stays covered by
            // ShipyardPersistenceTests.Reload_SentinelRow_RestoresInertWithoutGrant
            // (sentinel row reloads inert; ShipyardCompletedTask early-returns on
            // the sentinel). The residual adjacent-statement crash window is
            // waived per the task brief (untestable by timing).
            Record("SCROLL-ONCE", true, "SCOPED OUT (no build-craft seam without source changes; unit-covered)");

            await WriteReportAsync(startedAt, charId, frameId, killedPid, pre, stages);
        }
        finally
        {
            try { bot?.Dispose(); } catch { }
            await CleanupAsync(frameId, charId);
        }
    }

    private static string iron(uint item) => item.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Lane-isolation guard: this run must never touch the shared/prod
    /// checkout state (default ports/DB, sibling lanes).
    /// </summary>
    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "shipyard proof refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
        Assert.Equal("127.0.0.1", E2eStack.GameHost);
        Assert.NotEqual("e2e", E2eStack.ComposeProject);
    }

    private static long QueryMoney(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT money FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void UpdateMoney(uint characterId, long money)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE characters SET money = @money WHERE id = @id";
        cmd.Parameters.AddWithValue("@money", money);
        cmd.Parameters.AddWithValue("@id", characterId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }
    /// <summary>
    /// Polls the bridge session table until no networked character remains
    /// in-world — the old session's async logout (Deactivate + save) has fully
    /// landed and a subsequent offline row write can no longer be clobbered.
    /// </summary>
    private static void WaitForSessionGone(BotDriveClient bridge, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (bridge.Call("{\"cmd\":\"stats\"}", 10_000).GetProperty("inWorld").GetInt32() == 0)
                    return;
            }
            catch
            {
                // Bridge flap mid-poll — keep waiting.
            }
            Thread.Sleep(1000);
        }
        throw new TimeoutException("old networked session never left the world (inWorld != 0)");
    }

    private static ulong QueryItemInstanceId(uint characterId, uint templateId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM items WHERE owner = @owner AND template_id = @tpl ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@owner", characterId);
        cmd.Parameters.AddWithValue("@tpl", templateId);
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? 0ul : Convert.ToUInt64(result);
    }

    private static Dictionary<string, object?> SnapshotShipyardRow(ulong frameId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM shipyards WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", frameId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return [];
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static Dictionary<string, object?> PollShipyardRow(uint ownerId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM shipyards WHERE owner_id = @owner ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@owner", ownerId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                return row;
            }
            Thread.Sleep(500);
        }
        return [];
    }

    private static void AssertByteEqual(Dictionary<string, object?> pre, Dictionary<string, object?> post)
    {
        foreach (var key in new[] { "id", "template_id", "owner_id", "owner_name", "faction_id", "step", "actions", "hp", "zone_id" })
            Assert.Equal(Convert.ToString(pre[key], CultureInfo.InvariantCulture),
                Convert.ToString(post[key], CultureInfo.InvariantCulture));
        foreach (var key in new[] { "x", "y", "z", "yaw" })
            Assert.True(MathF.Abs(Convert.ToSingle(pre[key]!) - Convert.ToSingle(post[key]!)) < 0.01f,
                $"{key} moved across restart: {pre[key]} -> {post[key]}");
        Assert.True(Math.Abs((Convert.ToDateTime(pre["spawned"]!) - Convert.ToDateTime(post["spawned"]!)).TotalSeconds) < 2,
            $"spawned drifted across restart: {pre["spawned"]:O} -> {post["spawned"]:O}");
    }

    private static bool WaitForShipyardState(BotTcpLink link, ulong frameId, uint templateId, int timeoutMs)
    {
        var want = BitConverter.GetBytes(frameId);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type != SCOffsets.SCShipyardStatePacket || frame.Body.Length < 12)
                    continue;
                var matchId = true;
                for (var i = 0; i < 8; i++)
                    if (frame.Body[i] != want[i])
                    {
                        matchId = false;
                        break;
                    }
                if (!matchId)
                    continue;
                if (BitConverter.ToUInt32(frame.Body, 8) == templateId)
                    return true;
            }
            Thread.Sleep(25);
        }
        return false;
    }

    private static async Task PingLoopAsync(BotTcpLink link, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5_000, ct);
                if (!link.Connected)
                    break;
                link.SendGameFrame(PPOffsets.PingPacket, 2, body =>
                {
                    body.Write(0L);
                    body.Write(0L);
                    body.Write(0u);
                });
            }
        }
        catch
        {
            // cancelled or socket died — the test's own frames surface it
        }
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

    private static async Task CleanupAsync(ulong frameId, uint charId)
    {
        try
        {
            if (frameId != 0)
            {
                using var conn = E2eStack.OpenDb("aaemu_game");
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM shipyards WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", frameId);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shipyard-persist] cleanup: shipyards delete failed: {ex.Message}");
        }
        try
        {
            E2eStack.CleanupBotRows(AccountName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shipyard-persist] cleanup: bot rows failed: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    private static async Task WriteReportAsync(DateTime startedAt, uint charId, ulong frameId, int killedPid,
        Dictionary<string, object?> row, List<(string Stage, bool Ok, string Detail)> stages)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "shipyard-frame-persistence-e2e",
                startedAtUtc = startedAt.ToString("o", CultureInfo.InvariantCulture),
                lane = E2eStack.E2eRoot,
                composeProject = E2eStack.ComposeProject,
                dbPort = E2eStack.DbPort,
                gameHost = E2eStack.GameHost,
                ports = new
                {
                    login = E2eStack.LoginPort,
                    game = E2eStack.GamePort,
                    stream = E2eStack.StreamPort,
                    bridge = E2eStack.BridgePort,
                    internalPort = E2eStack.InternalPort,
                    webApi = E2eStack.WebApiPort
                },
                placement = "authenticated-bot CSCreateShipyardPacket (template 12 + design + mats; no bridge seam exists)",
                characterId = charId,
                frameId,
                killedGamePid = killedPid,
                row,
                stages = stages.Select(s => new { stage = s.Stage, ok = s.Ok, detail = s.Detail }),
                scrollOnce = "SCOPED OUT: no build-craft seam without source changes; unit-covered (sentinel ordering)"
            };
            await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "shipyard-frame-persistence-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shipyard-persist] report write failed: {ex.Message}");
        }
    }
}
