using System.Globalization;
using System.Text.Json;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// B1 HOUSING-01 R run (ROADMAP B1 card): claimed house + constructed
/// structure + placed décor survive a kill -9 restart with byte-equality
/// (owner, position, deco-limit state) over the REAL HousingManager.Build
/// path.
///
/// Flow on an isolated E2E lane (own E2E_ROOT/ports/DB/compose project):
/// provision a persistent headless bot → rig (money + canonical design item
/// 21166 → house 172 + deco item 8229 → design 62) → build via
/// GameplayActor.BuildHouse → HousingManager.Build (the exact
/// CSCreateHousePacket call, M3a validator included) → construct to
/// CurrentStep -1 via House.AddBuildAction (the CraftEffect Building
/// transition) → decorate via HousingManager.DecorateHouse (the
/// CSDecorateHousePacket path, DecoLimitEvaluator gate included) → real
/// save pass → snapshot MySQL (housings row + all house doodads + deco
/// counts) → kill -9 ONLY the game process (PID-handle kill, never pkill)
/// → reboot with the server-started gate → re-read and require BYTE-EQUAL
/// rows. Evidence JSON lands in $E2E_ROOT/logs/b1-housing-restart-report.json.
///
/// A failure here is a genuine housing-persistence defect and is reported as
/// such (rows/logs in the evidence), never papered over. H stays UNKNOWN:
/// scripted-actor / bot-functional evidence only.
/// </summary>
[Collection("e2e")]
public class B1HousingRestartE2eTests
{
    private const string BotName = "b1housing";
    private const string BotUsername = "bot_managed_" + BotName;

    private const uint HouseDesignId = 172;
    private const uint DecoDoodadTemplateId = 1256;

    private sealed record HouseSnapshot(uint Id, uint AccountId, uint Owner, uint CoOwner, uint TemplateId,
        string Name, float X, float Y, float Z, float Yaw, float Pitch, float Roll,
        int CurrentStep, int CurrentAction, int Permission,
        DateTime PlaceDate, DateTime ProtectedUntil, uint FactionId, uint SellTo, long SellPrice, int AllowRecover);

    private sealed record DoodadSnapshot(uint Id, int OwnerId, int OwnerType, int AttachPoint,
        uint TemplateId, uint CurrentPhaseId, DateTime PlantTime, DateTime GrowthTime, DateTime PhaseTime,
        float X, float Y, float Z, float Roll, float Pitch, float Yaw, float Scale,
        ulong ItemId, uint HouseId, uint ParentDoodad, uint ItemTemplateId, uint ItemContainerId, int Data, int FarmType);

    private sealed record HousingSnapshot(HouseSnapshot House, List<DoodadSnapshot> Doodads,
        int DecoCount, int BoundCount, Dictionary<uint, int> PerTemplate);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task B1_HouseConstructedAndDecoratedViaRealBuildPath_SurvivesKill9_ByteEqual()
    {
        AssertIsolatedLane();
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        uint charId = 0, houseId = 0;
        ushort houseTlId = 0;
        var attempts = 0;
        var killedPid = 0;

        try
        {
            // ------------------------------------------------- 1. REAL PATHS
            // Provision → rig → BuildHouse (HousingManager.Build) →
            // AddBuildAction construction → DecorateHouse, all in-process on
            // the live server through the E2E bridge housing seam.
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                var prov = bridge.Call(
                    $"{{\"cmd\":\"provision\",\"bot\":\"{BotName}\",\"fresh\":true,\"level\":10}}",
                    timeoutMs: 120_000);
                charId = prov.GetProperty("id").GetUInt32();
                Assert.True(charId > 0, "provision returned no character id");

                var rig = bridge.Call(
                    $"{{\"cmd\":\"housing\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"money\":10000000}}",
                    timeoutMs: 60_000);
                Assert.True(rig.GetProperty("designs").GetInt32() > 0, "rig stocked no design items");
                Assert.True(rig.GetProperty("decos").GetInt32() > 0, "rig stocked no deco items");

                var build = bridge.Call(
                    $"{{\"cmd\":\"housing\",\"op\":\"build\",\"bot\":\"{BotName}\"}}",
                    timeoutMs: 300_000);
                houseId = build.GetProperty("houseId").GetUInt32();
                houseTlId = (ushort)build.GetProperty("houseTlId").GetUInt32();
                attempts = build.GetProperty("attempts").GetInt32();
                Assert.True(houseId > 0 && houseTlId > 0,
                    $"build completed with no house (attempts={attempts})");
                Console.WriteLine($"[b1-housing] built house {houseId} (tl {houseTlId}) in {attempts} attempt(s)");

                var construct = bridge.Call(
                    $"{{\"cmd\":\"housing\",\"op\":\"construct\",\"bot\":\"{BotName}\",\"houseTlId\":{houseTlId}}}",
                    timeoutMs: 60_000);
                Assert.Equal(-1, construct.GetProperty("currentStep").GetInt32());
                Console.WriteLine($"[b1-housing] constructed to step -1 ({construct.GetProperty("applied").GetInt32()} actions)");

                var decorate = bridge.Call(
                    $"{{\"cmd\":\"housing\",\"op\":\"decorate\",\"bot\":\"{BotName}\",\"houseTlId\":{houseTlId}}}",
                    timeoutMs: 60_000);
                Assert.True(decorate.GetProperty("decorated").GetBoolean(), "engine refused the decoration");
                Assert.True(decorate.GetProperty("doodadObjId").GetUInt32() > 0, "no deco doodad spawned");
                Console.WriteLine("[b1-housing] décor placed (design 62 / doodad 1256)");

                var saveAck = bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
                Assert.True(saveAck.GetProperty("saved").GetBoolean(),
                    "bridge save pass did not complete before the kill");
            }

            // ------------------------------------------------- 2. PRE SNAPSHOT
            var pre = SnapshotHousing(houseId);
            AssertHousingShape(pre, charId, "pre-restart");

            // --------------------------------------------------- 3. KILL -9
            // PID-handle kill of ONLY our lane's game process (never pkill).
            killedPid = E2eStack.RestartGameServer();
            Assert.True(killedPid > 0, "no game process was killed — cannot claim a kill -9 restart");
            Assert.False(Directory.Exists($"/proc/{killedPid}"),
                $"killed game pid {killedPid} still exists after the restart gate");
            Console.WriteLine($"[b1-housing] kill -9 landed on game pid {killedPid}; reboot passed the server-started gate");

            // ------------------------------------------------ 4. POST ASSERTS
            var post = SnapshotHousing(houseId);
            AssertRestartIntact(pre, post, charId);
            Console.WriteLine("[b1-housing] POST PASS (house + structure + décor byte-equal, no loss/dup)");
        }
        finally
        {
            await CleanupAsync(houseId, charId);
        }

        await WriteReportAsync(startedAt, charId, houseId, houseTlId, attempts, killedPid);
    }

    /// <summary>
    /// Lane-isolation guard: this run must never touch the shared/prod
    /// checkout state (.165 prod, default ports/DB, sibling lanes).
    /// </summary>
    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "B1 R-run refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
        Assert.Equal("127.0.0.1", E2eStack.GameHost);
        Assert.NotEqual("e2e", E2eStack.ComposeProject);
    }

    // -------------------------------------------------------------- snapshots

    private static HousingSnapshot SnapshotHousing(uint houseId)
    {
        HouseSnapshot house;
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, account_id, owner, co_owner, template_id, name, x, y, z, yaw, pitch, roll, " +
                    "current_step, current_action, permission, place_date, protected_until, faction_id, " +
                    "sell_to, sell_price, allow_recover FROM housings WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", houseId);
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read(), $"housings row {houseId} missing");
                house = new HouseSnapshot(
                    reader.GetUInt32(0), reader.GetUInt32(1), reader.GetUInt32(2), reader.GetUInt32(3),
                    reader.GetUInt32(4), reader.GetString(5),
                    reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8),
                    reader.GetFloat(9), reader.GetFloat(10), reader.GetFloat(11),
                    reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14),
                    reader.GetDateTime(15), reader.GetDateTime(16),
                    reader.GetUInt32(17), reader.GetUInt32(18), reader.GetInt64(19), reader.GetInt32(20));
            }

            var doodads = new List<DoodadSnapshot>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
                    "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, " +
                    "house_id, parent_doodad, item_template_id, item_container_id, data, farm_type " +
                    "FROM doodads WHERE house_id = @houseId ORDER BY id";
                cmd.Parameters.AddWithValue("@houseId", houseId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    doodads.Add(new DoodadSnapshot(
                        reader.GetUInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                        reader.GetUInt32(4), reader.GetUInt32(5),
                        reader.GetDateTime(6), reader.GetDateTime(7), reader.GetDateTime(8),
                        reader.GetFloat(9), reader.GetFloat(10), reader.GetFloat(11),
                        reader.GetFloat(12), reader.GetFloat(13), reader.GetFloat(14), reader.GetFloat(15),
                        reader.GetUInt64(16), reader.GetUInt32(17), reader.GetUInt32(18),
                        reader.GetUInt32(19), reader.GetUInt32(20), reader.GetInt32(21), reader.GetInt32(22)));
                }
            }

            var perTemplate = doodads.GroupBy(d => d.TemplateId)
                .ToDictionary(g => g.Key, g => g.Count());
            return new HousingSnapshot(house, doodads,
                DecoCount: doodads.Count(d => d.AttachPoint == 0),
                BoundCount: doodads.Count(d => d.AttachPoint != 0),
                PerTemplate: perTemplate);
        }
    }

    // ------------------------------------------------------------- assertions

    /// <summary>Baseline shape of the live housing state BEFORE the kill.</summary>
    private static void AssertHousingShape(HousingSnapshot snap, uint charId, string phase)
    {
        Assert.Equal(HouseDesignId, snap.House.TemplateId);
        Assert.Equal(charId, snap.House.Owner);
        Assert.Equal(-1, snap.House.CurrentStep); // constructed structure, not a bare claim
        Assert.True(snap.Doodads.Count > 0, $"[{phase}] no doodads rows for house {snap.House.Id}");
        Assert.True(snap.Doodads.Any(d => d.TemplateId == DecoDoodadTemplateId),
            $"[{phase}] placed décor (doodad {DecoDoodadTemplateId}) missing on house {snap.House.Id}");
        Assert.True(snap.BoundCount > 0,
            $"[{phase}] no bound structure doodads on house {snap.House.Id}");
        Assert.All(snap.Doodads, d => Assert.Equal(snap.House.Id, d.HouseId));
    }

    /// <summary>
    /// THE restart assertion: every persisted column of the claimed house,
    /// its constructed structure, and its placed décor must be byte-equal
    /// after the kill -9 boot (±2s DATETIME, float epsilon). Any divergence
    /// here IS the persistence defect.
    /// </summary>
    private static void AssertRestartIntact(HousingSnapshot pre, HousingSnapshot post, uint charId)
    {
        AssertHousingShape(post, charId, "post-restart");

        var h1 = pre.House;
        var h2 = post.House;
        Assert.Equal(h1.Id, h2.Id); // SAME row — no dup, no re-claim rewrite
        Assert.Equal(h1.AccountId, h2.AccountId);
        Assert.Equal(h1.Owner, h2.Owner);
        Assert.Equal(h1.CoOwner, h2.CoOwner);
        Assert.Equal(h1.TemplateId, h2.TemplateId);
        Assert.Equal(h1.Name, h2.Name);
        Assert.True(MathF.Abs(h1.X - h2.X) < 0.01f &&
                    MathF.Abs(h1.Y - h2.Y) < 0.01f &&
                    MathF.Abs(h1.Z - h2.Z) < 0.01f,
            $"housings row position moved across restart: ({h1.X},{h1.Y},{h1.Z}) → ({h2.X},{h2.Y},{h2.Z})");
        Assert.True(MathF.Abs(h1.Yaw - h2.Yaw) < 0.01f &&
                    MathF.Abs(h1.Pitch - h2.Pitch) < 0.01f &&
                    MathF.Abs(h1.Roll - h2.Roll) < 0.01f,
            "housings row rotation moved across restart");
        Assert.Equal(h1.CurrentStep, h2.CurrentStep); // construction state conserved
        Assert.Equal(h1.CurrentAction, h2.CurrentAction);
        Assert.Equal(h1.Permission, h2.Permission);
        AssertNear(h1.PlaceDate, h2.PlaceDate, "place_date");
        AssertNear(h1.ProtectedUntil, h2.ProtectedUntil, "protected_until");
        Assert.Equal(h1.FactionId, h2.FactionId);
        Assert.Equal(h1.SellTo, h2.SellTo);
        Assert.Equal(h1.SellPrice, h2.SellPrice);
        Assert.Equal(h1.AllowRecover, h2.AllowRecover);

        // Deco-limit state: the exact counts the evaluator consumes.
        Assert.Equal(pre.Doodads.Count, post.Doodads.Count);
        Assert.Equal(pre.DecoCount, post.DecoCount);
        Assert.Equal(pre.BoundCount, post.BoundCount);
        Assert.Equal(pre.PerTemplate.Count, post.PerTemplate.Count);
        foreach (var (template, count) in pre.PerTemplate)
        {
            Assert.True(post.PerTemplate.TryGetValue(template, out var postCount),
                $"doodad template {template} vanished over the restart");
            Assert.Equal(count, postCount);
        }

        foreach (var d1 in pre.Doodads)
        {
            var d2 = post.Doodads.SingleOrDefault(d => d.Id == d1.Id);
            Assert.True(d2 != null,
                $"house doodads row {d1.Id} (template {d1.TemplateId}) vanished over the restart");
            Assert.Equal(d1.OwnerId, d2.OwnerId);
            Assert.Equal(d1.OwnerType, d2.OwnerType);
            Assert.Equal(d1.AttachPoint, d2.AttachPoint);
            Assert.Equal(d1.TemplateId, d2.TemplateId);
            Assert.Equal(d1.CurrentPhaseId, d2.CurrentPhaseId);
            AssertNear(d1.PlantTime, d2.PlantTime, $"doodad {d1.Id} plant_time");
            AssertNear(d1.GrowthTime, d2.GrowthTime, $"doodad {d1.Id} growth_time");
            AssertNear(d1.PhaseTime, d2.PhaseTime, $"doodad {d1.Id} phase_time");
            Assert.True(MathF.Abs(d1.X - d2.X) < 0.001f &&
                        MathF.Abs(d1.Y - d2.Y) < 0.001f &&
                        MathF.Abs(d1.Z - d2.Z) < 0.001f,
                $"doodad {d1.Id} local position clobbered over restart");
            Assert.True(MathF.Abs(d1.Roll - d2.Roll) < 0.001f &&
                        MathF.Abs(d1.Pitch - d2.Pitch) < 0.001f &&
                        MathF.Abs(d1.Yaw - d2.Yaw) < 0.001f,
                $"doodad {d1.Id} local rotation clobbered over restart");
            Assert.True(MathF.Abs(d1.Scale - d2.Scale) < 0.001f, $"doodad {d1.Id} scale changed");
            Assert.Equal(d1.ItemId, d2.ItemId);
            Assert.Equal(d1.HouseId, d2.HouseId);
            Assert.Equal(d1.ParentDoodad, d2.ParentDoodad);
            Assert.Equal(d1.ItemTemplateId, d2.ItemTemplateId);
            Assert.Equal(d1.ItemContainerId, d2.ItemContainerId);
            Assert.Equal(d1.Data, d2.Data);
            Assert.Equal(d1.FarmType, d2.FarmType);
        }
    }

    private static void AssertNear(DateTime expected, DateTime actual, string what)
    {
        Assert.True(Math.Abs((actual - expected).TotalSeconds) < 2,
            $"{what} clobbered over restart: stored {actual:O}, pre {expected:O}");
    }

    // ---------------------------------------------------------------- report

    private async Task WriteReportAsync(DateTime startedAt, uint charId, uint houseId,
        ushort houseTlId, int attempts, int killedPid)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            card = "B1 HOUSING-01 R run · ROADMAP restart-persistence row",
            path = "real engine paths: GameplayActor.BuildHouse → HousingManager.Build (CSCreateHousePacket) + House.AddBuildAction (CraftEffect Building) + HousingManager.DecorateHouse (CSDecorateHousePacket, DecoLimitEvaluator)",
            lane = new
            {
                root = E2eStack.E2eRoot,
                dbPort = E2eStack.DbPort,
                gameHost = E2eStack.GameHost,
                composeProject = E2eStack.ComposeProject,
                loginPort = E2eStack.LoginPort,
                gamePort = E2eStack.GamePort,
            },
            sourceRevision = E2eStack.SourceRevision,
            bot = BotName,
            characterId = charId,
            houseId,
            houseTlId,
            buildAttempts = attempts,
            killedGamePid = killedPid,
            verdict = "PASS",
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN",
            restarted_at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            asserted_rows = new[]
            {
                "housings (by id): owner/position/rotation/current_step/current_action/permission/place_date/protected_until/faction/sell/allow_recover",
                "doodads (house_id=house DbId): owner/attach/template/phase/plant+growth+phase times/local transform/scale/item links/parent/data",
                "deco-limit state: total/deco/bound counts + per-template counts",
            },
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "b1-housing-restart-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --------------------------------------------------------------- cleanup

    /// <summary>Removes every row this run created (scoped strictly by the
    /// house id + bot character chain), leaving the lane DB clean.</summary>
    private static async Task CleanupAsync(uint houseId, uint charId)
    {
        try
        {
            if (houseId > 0 || charId > 0)
            {
                using var conn = E2eStack.OpenDb("aaemu_game");
                foreach (var (sql, param) in new[]
                         {
                             ("DELETE FROM doodads WHERE house_id = @id OR owner_id = @charId", true),
                             ("DELETE FROM housings WHERE id = @id", true),
                             ("DELETE FROM items WHERE owner = @charId", true),
                             ("DELETE FROM item_containers WHERE owner_id = @charId", true),
                         })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@id", houseId);
                    cmd.Parameters.AddWithValue("@charId", charId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            using var conn2 = E2eStack.OpenDb("aaemu_game");
            foreach (var sql in new[]
                     {
                         "DELETE FROM quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                         "DELETE FROM completed_quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                         "DELETE FROM playerbot_metadata WHERE character_id IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                         "DELETE FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username)"
                     })
            {
                using var cmd2 = conn2.CreateCommand();
                cmd2.CommandText = sql;
                cmd2.Parameters.AddWithValue("@username", BotUsername);
                try { await cmd2.ExecuteNonQueryAsync(); } catch { /* FK-tolerant, mirrors shared helper */ }
            }

            using var loginConn = E2eStack.OpenDb("aaemu_login");
            using var delUser = loginConn.CreateCommand();
            delUser.CommandText = "DELETE FROM users WHERE username = @username";
            delUser.Parameters.AddWithValue("@username", BotUsername);
            await delUser.ExecuteNonQueryAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[b1-housing] cleanup failed (non-fatal): {e.Message}");
        }
    }
}
