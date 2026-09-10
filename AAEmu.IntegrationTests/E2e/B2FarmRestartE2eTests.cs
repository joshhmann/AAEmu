using System.Globalization;
using System.Text.Json;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// B2 FARM-01 R run (ROADMAP B2 card): a growing crop (potato loop —
/// seed 15659 → doodad 2259) and livestock (dairy calf 2672, via calf
/// item 16225) survive a kill -9 restart with growth-timer state intact,
/// and the post-restart harvest still yields through the REAL
/// DoodadFuncCropHarvest / FruitPick → loot chain.
///
/// Flow on an isolated E2E lane (own E2E_ROOT/ports/DB/compose project):
/// provision a persistent headless bot → rig (potato seeds + calf items +
/// labor) → plant crop + calf via GameplayActor.Plant (the exact
/// CSCreateDoodadPacket engine path, DoodadManager.CreatePlayerDoodad) →
/// real save pass → snapshot MySQL doodad rows (template / phase /
/// plant+growth+phase times / transform / owner + item links) → kill -9
/// ONLY the game process (PID-handle kill, never pkill) → reboot with the
/// server-started gate → re-read and require the crop byte-equal and the
/// calf timer-intact (plant_time never rewritten — the M3b-1 class — with
/// forward-only phase catch-up) → re-adopt the bot → resolve live ObjIds
/// by stable DbId → harvest the crop via GameplayActor.Harvest and require
/// a real potato yield → poll the calf for resumed growth. Evidence JSON
/// lands in $E2E_ROOT/logs/b2-farm-restart-report.json.
///
/// A failure here is a genuine farm-persistence defect and is reported as
/// such (rows/logs in the evidence), never papered over. H stays UNKNOWN:
/// scripted-actor / bot-functional evidence only.
/// </summary>
[Collection("e2e")]
public class B2FarmRestartE2eTests
{
    private const string BotName = "b2farm";
    private const string BotUsername = "bot_managed_" + BotName;

    // Canonical 1.2 ids (verified against compact.sqlite3):
    //   potato seed 15659 (감자 씨앗) → crop doodad 2259 (감자):
    //     4379 seedling → 4456 small → 4457 mature → harvest skill 13980 →
    //     4458 looting (LootPack 129) → 4459 final (deleted);
    //   dairy calf item 16225 (젖소 송아지) → livestock doodad 2672:
    //     5780 calf → 5781 growing calf → 12774 mature → 5782 cow.
    private const uint PotatoSeedItemId = 15659;
    private const uint CalfItemId = 16225;
    private const uint PotatoDoodadId = 2259;
    private const uint CalfDoodadId = 2672;
    private const uint PotatoItemId = 7992;
    private const uint GoldenPotatoItemId = 19887;

    private const uint CropSeedlingPhase = 4379;
    private const uint CropSmallPhase = 4456;
    private const uint CropMaturePhase = 4457;
    private const uint CalfStartPhase = 5780;
    private const uint CalfGrowPhase = 5781;
    private const uint CalfMatureInterimPhase = 12774;
    private const uint CowPhase = 5782;

    private static readonly uint[] CropChain = [CropSeedlingPhase, CropSmallPhase, CropMaturePhase];
    // Forward growth order for the calf (12774 resolves to the cow via the
    // canonical ratio change; hungry/sick branches never appear on a fed,
    // unstressed E2E calf but are accepted as forward progress, never regress).
    private static readonly uint[] CalfChain = [CalfStartPhase, CalfGrowPhase, CalfMatureInterimPhase, CowPhase];

    private sealed record DoodadSnapshot(uint Id, int OwnerId, int OwnerType, int AttachPoint,
        uint TemplateId, uint CurrentPhaseId, DateTime PlantTime, DateTime GrowthTime, DateTime PhaseTime,
        float X, float Y, float Z, float Roll, float Pitch, float Yaw, float Scale,
        ulong ItemId, uint HouseId, uint ParentDoodad, uint ItemTemplateId, uint ItemContainerId, int Data, int FarmType);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task B2_GrowingCropAndCalfViaRealPlantPath_SurviveKill9_TimerIntact_AndHarvestYields()
    {
        AssertIsolatedLane();
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        uint charId = 0;
        uint cropDbId = 0, calfDbId = 0;
        var killedPid = 0;
        var preCropPhase = 0u;
        var preCalfPhase = 0u;
        var postCropPhase = 0u;
        var postCalfPhase = 0u;
        var harvestYield = 0;
        var potatoDelta = 0;
        var calfResumedPhase = 0u;

        try
        {
            // ------------------------------------------------- 1. REAL PATHS
            // Provision → rig → Plant crop + calf through the live bridge
            // farm seam (GameplayActor.Plant → CreatePlayerDoodad → Save).
            uint cropObjId, calfObjId;
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                var prov = bridge.Call(
                    $"{{\"cmd\":\"provision\",\"bot\":\"{BotName}\",\"fresh\":true,\"level\":10}}",
                    timeoutMs: 120_000);
                charId = prov.GetProperty("id").GetUInt32();
                Assert.True(charId > 0, "provision returned no character id");

                var rig = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"seeds\":5,\"calves\":2,\"labor\":5000}}",
                    timeoutMs: 60_000);
                Assert.True(rig.GetProperty("seeds").GetInt32() >= 2, "rig stocked no potato seeds");
                Assert.True(rig.GetProperty("calves").GetInt32() >= 1, "rig stocked no calf items");

                var plantCrop = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"plant\",\"bot\":\"{BotName}\",\"seed\":{PotatoSeedItemId}}}",
                    timeoutMs: 120_000);
                cropObjId = plantCrop.GetProperty("objId").GetUInt32();
                cropDbId = plantCrop.GetProperty("dbId").GetUInt32();
                Assert.True(cropObjId > 0 && cropDbId > 0,
                    $"crop plant completed with no doodad (detail={plantCrop.GetProperty("detail").GetString()})");
                Assert.Equal(PotatoDoodadId, plantCrop.GetProperty("template").GetUInt32());
                Console.WriteLine($"[b2-farm] planted crop obj {cropObjId} (db {cropDbId})");

                var plantCalf = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"plant\",\"bot\":\"{BotName}\",\"seed\":{CalfItemId},\"dx\":4}}",
                    timeoutMs: 120_000);
                calfObjId = plantCalf.GetProperty("objId").GetUInt32();
                calfDbId = plantCalf.GetProperty("dbId").GetUInt32();
                Assert.True(calfObjId > 0 && calfDbId > 0,
                    $"calf plant completed with no doodad (detail={plantCalf.GetProperty("detail").GetString()})");
                Assert.Equal(CalfDoodadId, plantCalf.GetProperty("template").GetUInt32());
                Console.WriteLine($"[b2-farm] planted calf obj {calfObjId} (db {calfDbId})");

                // The live TaskManager does not fire the 3600×-compressed
                // growth tasks in wall-clock microseconds (scenario pumps
                // allow up to 180 s for the same reason): wait for the crop
                // to reach the mature phase before the kill so the
                // pre-restart row is the harvestable one. The calf keeps its
                // slow clock and is still mid-growth at the kill.
                WaitForLivePhase(bridge, cropDbId, CropMaturePhase, TimeSpan.FromSeconds(180));
                var saveAck = bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
                Assert.True(saveAck.GetProperty("saved").GetBoolean(),
                    "bridge save pass did not complete before the kill");
            }

            // ------------------------------------------------- 2. PRE SNAPSHOT
            var preCrop = SnapshotDoodad(cropDbId);
            var preCalf = SnapshotDoodad(calfDbId);
            AssertFarmShape(preCrop, preCalf, charId, "pre-restart");
            preCropPhase = preCrop.CurrentPhaseId;
            preCalfPhase = preCalf.CurrentPhaseId;
            // At the E2E growth rate (3600×) the 60 s + 9 min crop cycle
            // matures in ~170 ms, so the crop is mature long before the
            // kill; the calf (3.4 s + 30.9 s) is still mid-growth.
            Assert.Equal(CropMaturePhase, preCropPhase);
            Assert.Contains(preCalfPhase, CalfChain);
            // --------------------------------------------------- 3. KILL -9
            // PID-handle kill of ONLY our lane's game process (never pkill).
            killedPid = E2eStack.RestartGameServer();
            Assert.True(killedPid > 0, "no game process was killed — cannot claim a kill -9 restart");
            Assert.False(Directory.Exists($"/proc/{killedPid}"),
                $"killed game pid {killedPid} still exists after the restart gate");
            Console.WriteLine($"[b2-farm] kill -9 landed on game pid {killedPid}; reboot passed the server-started gate");

            // ------------------------------------------------ 4. POST ASSERTS
            var postCrop = SnapshotDoodad(cropDbId);
            var postCalf = SnapshotDoodad(calfDbId);
            postCropPhase = postCrop.CurrentPhaseId;
            postCalfPhase = postCalf.CurrentPhaseId;
            AssertCropRestartIntact(preCrop, postCrop, charId);
            AssertCalfRestartIntact(preCalf, postCalf, charId);
            Console.WriteLine($"[b2-farm] POST PASS (crop byte-equal phase {postCropPhase}; calf {preCalfPhase} → {postCalfPhase} timer-intact)");

            // --------------------------------------- 5. POST-RESTART HARVEST
            // Re-adopt the bot on the fresh boot (the bridge registry is
            // in-memory and died with the old process), resolve the live
            // ObjIds by stable DbId, and harvest through the REAL
            // DoodadFuncCropHarvest/FruitPick → loot chain.
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                var adopt = bridge.Call(
                    $"{{\"cmd\":\"provision\",\"bot\":\"{BotName}\",\"fresh\":false}}",
                    timeoutMs: 120_000);
                Assert.Equal(charId, adopt.GetProperty("id").GetUInt32());

                var liveCropObj = FindLiveObjId(bridge, cropDbId, "crop");
                var liveCalfObj = FindLiveObjId(bridge, calfDbId, "calf");
                Assert.True(liveCropObj > 0, $"crop db {cropDbId} not in the reloaded world");
                Assert.True(liveCalfObj > 0, $"calf db {calfDbId} not in the reloaded world");

                var before = BagCounts(bridge, [PotatoItemId, GoldenPotatoItemId, PotatoSeedItemId]);
                var harvest = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"harvest\",\"bot\":\"{BotName}\",\"objId\":{liveCropObj}}}",
                    timeoutMs: 120_000);
                Assert.Equal("Completed", harvest.GetProperty("state").GetString());
                harvestYield = harvest.GetProperty("yield").GetInt32();
                Assert.True(harvest.GetProperty("deleted").GetBoolean(),
                    $"harvested crop obj {liveCropObj} not deleted (phaseAfter={harvest.GetProperty("phaseAfter").GetUInt32()})");
                var after = BagCounts(bridge, [PotatoItemId, GoldenPotatoItemId, PotatoSeedItemId]);
                potatoDelta = after[PotatoItemId] - before[PotatoItemId];
                Assert.True(harvestYield >= 2,
                    $"harvest yielded {harvestYield} unit(s), expected ≥ 2 (detail={harvest.GetProperty("detail").GetString()})");
                Assert.True(potatoDelta >= 2,
                    $"potato 7992 delta {potatoDelta}, expected ≥ 2 (canonical pack 6452 minimum)");
                Console.WriteLine($"[b2-farm] harvest yield {harvestYield} unit(s), potato +{potatoDelta}, golden +{after[GoldenPotatoItemId] - before[GoldenPotatoItemId]}");

                // The harvested crop row is gone by engine design (final
                // phase deletes the doodad); the calf row must still stand.
                AssertDoodadRowGone(cropDbId);
                var calfStillThere = SnapshotDoodad(calfDbId);
                Assert.Equal(CalfDoodadId, calfStillThere.TemplateId);

                // --------------------------------- 6. CALF GROWTH RESUMPTION
                // The reloaded growth task must still be driving the calf:
                // either it already caught up during the downtime (phase
                // ahead of the post-restart read) or it advances under poll.
                calfResumedPhase = await WaitForCalfProgressAsync(bridge, calfDbId, postCalfPhase, TimeSpan.FromSeconds(120));
                Console.WriteLine($"[b2-farm] calf growth resumed (phase {postCalfPhase} → {calfResumedPhase})");
            }
        }
        finally
        {
            await CleanupAsync(charId, cropDbId, calfDbId);
        }

        await WriteReportAsync(startedAt, charId, cropDbId, calfDbId,
            preCropPhase, preCalfPhase, postCropPhase, postCalfPhase,
            harvestYield, potatoDelta, calfResumedPhase, killedPid);
    }

    /// <summary>
    /// Lane-isolation guard: this run must never touch the shared/prod
    /// checkout state (.165 prod, default ports/DB, sibling lanes).
    /// </summary>
    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "B2 R-run refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
        Assert.Equal("127.0.0.1", E2eStack.GameHost);
        Assert.NotEqual("e2e", E2eStack.ComposeProject);
    }

    // -------------------------------------------------------------- snapshots

    private static DoodadSnapshot SnapshotDoodad(uint dbId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
            "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, " +
            "house_id, parent_doodad, item_template_id, item_container_id, data, farm_type " +
            "FROM doodads WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", dbId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"doodads row {dbId} missing");
        return new DoodadSnapshot(
            reader.GetUInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetUInt32(4), reader.GetUInt32(5),
            reader.GetDateTime(6), reader.GetDateTime(7), reader.GetDateTime(8),
            reader.GetFloat(9), reader.GetFloat(10), reader.GetFloat(11),
            reader.GetFloat(12), reader.GetFloat(13), reader.GetFloat(14), reader.GetFloat(15),
            reader.GetUInt64(16), reader.GetUInt32(17), reader.GetUInt32(18),
            reader.GetUInt32(19), reader.GetUInt32(20), reader.GetInt32(21), reader.GetInt32(22));
    }

    private static void AssertDoodadRowGone(uint dbId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM doodads WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", dbId);
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    private static uint FindLiveObjId(BotDriveClient bridge, uint dbId, string what)
    {
        var find = bridge.Call(
            $"{{\"cmd\":\"farm\",\"op\":\"find\",\"bot\":\"{BotName}\",\"dbIds\":[{dbId}]}}",
            timeoutMs: 60_000);
        var entry = find.GetProperty("doodads").EnumerateArray().First();
        Assert.True(entry.GetProperty("found").GetBoolean(), $"{what} db {dbId} not in the live world");
        return entry.GetProperty("objId").GetUInt32();
    }

    private static Dictionary<uint, int> BagCounts(BotDriveClient bridge, uint[] templates)
    {
        var status = bridge.Call(
            $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{BotName}\",\"objIds\":[],\"items\":[{string.Join(",", templates)}]}}",
            timeoutMs: 60_000);
        return status.GetProperty("items").EnumerateArray()
            .ToDictionary(e => e.GetProperty("template").GetUInt32(), e => e.GetProperty("count").GetInt32());
    }

    private static async Task<uint> WaitForCalfProgressAsync(BotDriveClient bridge, uint calfDbId, uint postPhase, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var find = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"find\",\"bot\":\"{BotName}\",\"dbIds\":[{calfDbId}]}}",
                timeoutMs: 60_000);
            var entry = find.GetProperty("doodads").EnumerateArray().First();
            Assert.True(entry.GetProperty("found").GetBoolean(), $"calf db {calfDbId} vanished from the live world");
            var livePhase = entry.GetProperty("phase").GetUInt32();
            // Forward progress past the post-restart read, or already at/near
            // the cow (caught up during the downtime — the timer catch-up).
            if (livePhase != postPhase || livePhase == CowPhase || livePhase == CalfMatureInterimPhase)
                return livePhase;
            await Task.Delay(2000);
        }
        var last = SnapshotDoodad(calfDbId);
        Assert.Fail($"calf db {calfDbId} growth timer dead: phase stuck at {postPhase} for {budget.TotalSeconds}s post-restart " +
            $"(row phase {last.CurrentPhaseId}, growth {last.GrowthTime:O}, phase {last.PhaseTime:O})");
        return 0u; // unreachable
    }

    /// <summary>Polls the live world until a doodad reaches a phase.</summary>
    private static void WaitForLivePhase(BotDriveClient bridge, uint dbId, uint wantPhase, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var find = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"find\",\"bot\":\"{BotName}\",\"dbIds\":[{dbId}]}}",
                timeoutMs: 60_000);
            var entry = find.GetProperty("doodads").EnumerateArray().First();
            Assert.True(entry.GetProperty("found").GetBoolean(), $"doodad db {dbId} vanished from the live world");
            if (entry.GetProperty("phase").GetUInt32() == wantPhase)
                return;
            Thread.Sleep(1000);
        }
        var row = SnapshotDoodad(dbId);
        Assert.Fail($"doodad db {dbId} never reached phase {wantPhase} within {budget.TotalSeconds}s " +
            $"(row phase {row.CurrentPhaseId}, growth {row.GrowthTime:O}, phase {row.PhaseTime:O})");
    }

    // ------------------------------------------------------------- assertions

    /// <summary>Baseline shape of the live farm state BEFORE the kill.</summary>
    private static void AssertFarmShape(DoodadSnapshot crop, DoodadSnapshot calf, uint charId, string phase)
    {
        Assert.Equal(PotatoDoodadId, crop.TemplateId);
        Assert.Equal(CalfDoodadId, calf.TemplateId);
        Assert.Equal(charId, (uint)crop.OwnerId);
        Assert.Equal(charId, (uint)calf.OwnerId);
        Assert.Contains(crop.CurrentPhaseId, CropChain);
        Assert.Contains(calf.CurrentPhaseId, CalfChain);
        Assert.True((DateTime.UtcNow - crop.PlantTime).TotalMinutes < 30, $"[{phase}] crop plant_time not recent: {crop.PlantTime:O}");
        Assert.True((DateTime.UtcNow - calf.PlantTime).TotalMinutes < 30, $"[{phase}] calf plant_time not recent: {calf.PlantTime:O}");
        // MySQL DATETIME is second-precision while the E2E growth rate
        // (3600×) compresses the seedling/small clocks to milliseconds, so
        // a just-planted row legitimately reads growth_time == plant_time.
        // Timer-intact is proven post-restart, not here.
        Assert.True(crop.GrowthTime >= crop.PlantTime, $"[{phase}] crop growth clock missing");
        Assert.True(calf.GrowthTime >= calf.PlantTime, $"[{phase}] calf growth clock missing");
    }
    /// <summary>
    /// THE crop restart assertion: every persisted column of the mature crop
    /// must be byte-equal after the kill -9 boot (±2s DATETIME, float
    /// epsilon). The mature phase carries only the 48 h rot timer, so no
    /// engine write may have touched the row. Any divergence here IS the
    /// persistence defect.
    /// </summary>
    private static void AssertCropRestartIntact(DoodadSnapshot pre, DoodadSnapshot post, uint charId)
    {
        Assert.Equal(pre.Id, post.Id); // SAME row — no dup, no re-plant rewrite
        Assert.Equal((uint)pre.OwnerId, charId);
        Assert.Equal(pre.OwnerId, post.OwnerId);
        Assert.Equal(pre.OwnerType, post.OwnerType);
        Assert.Equal(pre.AttachPoint, post.AttachPoint);
        Assert.Equal(PotatoDoodadId, post.TemplateId);
        Assert.Equal(pre.CurrentPhaseId, post.CurrentPhaseId);
        Assert.Equal(CropMaturePhase, post.CurrentPhaseId);
        AssertNear(pre.PlantTime, post.PlantTime, "crop plant_time");
        AssertNear(pre.GrowthTime, post.GrowthTime, "crop growth_time");
        AssertNear(pre.PhaseTime, post.PhaseTime, "crop phase_time");
        Assert.True(MathF.Abs(pre.X - post.X) < 0.001f &&
                    MathF.Abs(pre.Y - post.Y) < 0.001f &&
                    MathF.Abs(pre.Z - post.Z) < 0.001f,
            $"crop position clobbered over restart: ({pre.X},{pre.Y},{pre.Z}) → ({post.X},{post.Y},{post.Z})");
        Assert.True(MathF.Abs(pre.Scale - post.Scale) < 0.001f, "crop scale changed");
        Assert.Equal(pre.ItemId, post.ItemId);
        Assert.Equal(pre.HouseId, post.HouseId);
        Assert.Equal(pre.ParentDoodad, post.ParentDoodad);
        Assert.Equal(pre.ItemTemplateId, post.ItemTemplateId);
        Assert.Equal(pre.ItemContainerId, post.ItemContainerId);
        Assert.Equal(pre.Data, post.Data);
        Assert.Equal(pre.FarmType, post.FarmType);
    }

    /// <summary>
    /// THE livestock restart assertion: the calf row must still stand with
    /// its identity, position and clock base intact (plant_time never
    /// rewritten at boot — the M3b-1 class), while its phase may only move
    /// FORWARD along the canonical growth chain (the overdue growth timer
    /// legitimately catches up during the reboot downtime). A regressed or
    /// clobbered clock here IS the persistence defect.
    /// </summary>
    private static void AssertCalfRestartIntact(DoodadSnapshot pre, DoodadSnapshot post, uint charId)
    {
        Assert.Equal(pre.Id, post.Id); // SAME row — no dup, no re-spawn rewrite
        Assert.Equal((uint)pre.OwnerId, charId);
        Assert.Equal(pre.OwnerId, post.OwnerId);
        Assert.Equal(pre.OwnerType, post.OwnerType);
        Assert.Equal(CalfDoodadId, post.TemplateId);
        AssertNear(pre.PlantTime, post.PlantTime, "calf plant_time");
        Assert.True(MathF.Abs(pre.X - post.X) < 0.001f &&
                    MathF.Abs(pre.Y - post.Y) < 0.001f &&
                    MathF.Abs(pre.Z - post.Z) < 0.001f,
            $"calf position clobbered over restart: ({pre.X},{pre.Y},{pre.Z}) → ({post.X},{post.Y},{post.Z})");
        Assert.Equal(pre.ItemTemplateId, post.ItemTemplateId);
        Assert.Equal(pre.HouseId, post.HouseId);
        Assert.Equal(pre.Data, post.Data);
        Assert.Equal(pre.FarmType, post.FarmType);

        var preIdx = Array.IndexOf(CalfChain, pre.CurrentPhaseId);
        var postIdx = Array.IndexOf(CalfChain, post.CurrentPhaseId);
        Assert.True(preIdx >= 0, $"pre-restart calf phase {pre.CurrentPhaseId} outside the canonical chain");
        Assert.True(postIdx >= 0, $"post-restart calf phase {post.CurrentPhaseId} outside the canonical chain ({pre.CurrentPhaseId} → {post.CurrentPhaseId})");
        Assert.True(postIdx >= preIdx,
            $"calf growth REGRESSED over the restart: {pre.CurrentPhaseId} → {post.CurrentPhaseId}");
        if (postIdx == preIdx)
        {
            // Same phase: the clock base must be untouched (the boot load
            // never writes the row — ApplyLoadedState invariant).
            AssertNear(pre.GrowthTime, post.GrowthTime, "calf growth_time");
            AssertNear(pre.PhaseTime, post.PhaseTime, "calf phase_time");
        }
        else
        {
            // Caught up during the downtime: the new phase clock must be
            // monotonic off the old one, never reset to boot time.
            Assert.True(post.PhaseTime >= pre.PhaseTime.AddSeconds(-2),
                $"calf phase clock reset over restart: {pre.PhaseTime:O} → {post.PhaseTime:O}");
        }
    }

    private static void AssertNear(DateTime expected, DateTime actual, string what)
    {
        Assert.True(Math.Abs((actual - expected).TotalSeconds) < 2,
            $"{what} clobbered over restart: stored {actual:O}, pre {expected:O}");
    }

    // ---------------------------------------------------------------- report

    private async Task WriteReportAsync(DateTime startedAt, uint charId, uint cropDbId, uint calfDbId,
        uint preCropPhase, uint preCalfPhase, uint postCropPhase, uint postCalfPhase,
        int yield, int potatoDelta, uint calfResumedPhase, int killedPid)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            card = "B2 FARM-01 R run · ROADMAP restart-persistence row",
            path = "real engine paths: GameplayActor.Plant → DoodadManager.CreatePlayerDoodad (CSCreateDoodadPacket) + GameplayActor.Harvest → Doodad.Use (DoodadFuncCropHarvest/FruitPick → loot)",
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
            cropDbId,
            calfDbId,
            ids = new
            {
                potatoSeed = PotatoSeedItemId,
                potatoDoodad = PotatoDoodadId,
                calfItem = CalfItemId,
                calfDoodad = CalfDoodadId,
                potato = PotatoItemId,
                goldenPotato = GoldenPotatoItemId,
            },
            preRestart = new { cropPhase = preCropPhase, calfPhase = preCalfPhase },
            postRestart = new { cropPhase = postCropPhase, calfPhase = postCalfPhase },
            harvest = new { yield, potatoDelta },
            calfResumedPhase,
            killedGamePid = killedPid,
            verdict = "PASS",
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN",
            restarted_at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            asserted_rows = new[]
            {
                "doodads (crop id): owner/attach/template/phase/plant+growth+phase times/position/scale/item links/parent/data/farm — byte-equal",
                "doodads (calf id): identity/position/clock-base intact, phase forward-only along 5780→5781→12774→5782",
                "post-restart harvest: Completed + deleted + potato 7992 delta ≥ 2 (canonical pack 6452 minimum)",
                "calf growth resumption: live phase advances past the post-restart read (or already caught up)",
            },
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "b2-farm-restart-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --------------------------------------------------------------- cleanup

    /// <summary>Removes every row this run created (scoped strictly by the
    /// bot character chain + the two farm DbIds), leaving the lane DB clean.</summary>
    private static async Task CleanupAsync(uint charId, uint cropDbId, uint calfDbId)
    {
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            foreach (var (sql, id, cid) in new[]
                     {
                         ("DELETE FROM doodads WHERE id = @id", cropDbId, 0u),
                         ("DELETE FROM doodads WHERE id = @id", calfDbId, 0u),
                         ("DELETE FROM doodads WHERE owner_id = @charId", 0u, charId),
                     })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@charId", cid);
                await cmd.ExecuteNonQueryAsync();
            }
            foreach (var sql in new[]
                     {
                         "DELETE FROM items WHERE owner = @charId",
                         "DELETE FROM item_containers WHERE owner_id = @charId",
                     })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@charId", charId);
                await cmd.ExecuteNonQueryAsync();
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
            Console.WriteLine($"[b2-farm] cleanup failed (non-fatal): {e.Message}");
        }
    }
}
