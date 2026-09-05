using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// A5 (b2) business-state progression for the Tier-3 dormant-timer soak:
/// while the world holds 1,000 dormant specs and 0 embodied, wall-clock
/// engine timers must still advance observable business state — planted
/// crops grow phase → mature, and path-loop transfers keep moving.
///
/// Shape (no engine change — DB-direct + existing bridge only):
///   SETUP  (pre-restart): 2 sunflower canaries through the REAL plant path
///            (real client CSCreateDoodadPacket → CreatePlayerDoodad) + 1
///            travel canary selected from the live 'transfers' dump.
///   SAMPLE (per-sample, fenced): harvest via direct MySQL SELECT on
///            `doodads`, travel via the existing read-only 'transfers'
///            bridge command. Sentinels on read failure, never failed
///            per-sample.
///   END    (once): ValidateTimerProgression beside the DB-writes check,
///            appending to the SAME failures list (passed semantics
///            unchanged).
///   BOUNDED: Probe_A5Tier3RestartConservesDormantTimers covers the boot
///            path (SpawnManager + ApplyLoadedState + InitDoodad re-arm)
///            without the six-hour window.
/// </summary>
public partial class A5Tier3AcceptanceProbeTests
{
    // Sunflower (해바라기) canary — chain traced from the canonical
    // compact.sqlite3 game data (item_spawn_doodads + doodad groups/phase
    // funcs + growth/timer delays):
    //   seed item 15671 -> doodad almighty 2271 (group-12 field crop,
    //   climate None=0, so DoodadHasMatchingClimate is always false and no
    //   0.73 bonus applies — deterministic sizing).
    //   plant group 4391 --Growth 607 (1,440,000 ms)--> 4504
    //                    --Growth 608 (12,960,000 ms)--> 4505 MATURE
    //            (Use 1039 -> 4506 carries DoodadFuncLootPack: harvestable).
    //   The 4505 wither Timer 1334 (201,600,000 ms = 15.5 h at 3600x) never
    //   fires inside the 6 h window, so a matured canary holds 4505.
    //   Total growth delay 14,400,000 ms / GrowthRate 3600 = 4,000 s = 66.7
    //   min, so dueUtc falls ~67 min into the 360-min window (60-120 target).
    // Delay is ms wall-clock divided by World.GrowthRate (DoodadFuncGrowth).
    private const uint TimerCanarySeedItemId = 15671;
    private const uint TimerCanaryDoodadId = 2271;
    private const int TimerCanaryMatureGroupId = 4505;
    private const double TimerCanaryTotalDelayMs = 14_400_000;
    private const int TimerCanaryCount = 2;
    private const double FallbackGrowthRate = 3600; // E2eStack.GameLocalConfig World.GrowthRate

    private sealed record TimerCanaryPos(double X, double Y, double Z);

    private sealed record HarvestCanarySetup(
        uint DbId, int StartPhase, DateTime PlantUtc, DateTime GrowthUtc, DateTime DueUtc);

    private sealed record TravelCanarySetup(
        ushort TlId, string Name, TimerCanaryPos Pos0, int StartPathIndex,
        double DispMinM, double ObservedMaxM);

    private sealed record TimerCanarySetup(
        HarvestCanarySetup[] Harvest, TravelCanarySetup Travel, double GrowthRate);

    private sealed record CanaryDoodadRow(
        uint DbId, int Phase, DateTime PlantUtc, DateTime GrowthUtc, DateTime PhaseUtc);

    private sealed record HarvestCanaryEnd(
        uint DbId, int StartPhase, DateTime PlantUtc, DateTime StartGrowthUtc, DateTime DueUtc,
        int EndPhase, DateTime EndGrowthUtc, DateTime EndUtc, string? Failure);

    private sealed record TravelCanaryEnd(
        ushort TlId, TimerCanaryPos Pos0, TimerCanaryPos? PosEnd, int PathIndexDelta,
        double DisplacementM, double DispMinM, double ObservedMaxM, string? Failure);

    private sealed record TimerProgressionEnd(HarvestCanaryEnd[] Harvest, TravelCanaryEnd Travel);

    // Canary doodad ids planted by this run (setup runs inside try, so the
    // probe finally cannot see locals — tracked here for post-run deletion;
    // the ownership cleanup only covers account/character rows).
    private static readonly List<uint> s_timerCanaryDbIds = new();

    // ------------------------------------------------------------------ pure

    private static double DisplacementM(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var dz = z1 - z0;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Pins the LOOSE travel lower bound from the 10-minute setup
    /// observation: proposal 50 m when the transfer shows healthy motion
    /// (≥100 m per 10 min), scaled with observed motion otherwise, 5 m floor
    /// so a frozen transfer still fails while a slow-but-alive one passes.
    /// </summary>
    private static double PinTravelDispMinM(double observedMaxM)
        => observedMaxM >= 100 ? 50 : Math.Clamp(observedMaxM * 0.5, 5, 50);

    /// <summary>EXACT harvest rule: the phase must have changed, the window
    /// must have run past due, and the end phase must be the mature group.
    /// Returns null on pass, else the failure line.</summary>
    private static string? CheckHarvestProgression(
        int startPhase, int endPhase, DateTime endUtc, DateTime dueUtc, int matureGroup)
    {
        if (endPhase == startPhase)
            return $"harvest canary phase unchanged at {endPhase} — growth timer never advanced it";
        if (endUtc < dueUtc)
            return $"harvest canary ended at {endUtc:O} before due {dueUtc:O} — window too short to judge";
        if (endPhase != matureGroup)
            return $"harvest canary ended in phase {endPhase}, expected mature group {matureGroup}";
        return null;
    }

    /// <summary>LOOSE travel lower bound: a path-index step OR enough
    /// displacement. pathIndexDelta -1 = unknown (the bridge does not emit
    /// PathPointIndex) — the rule then rests on displacement alone.</summary>
    private static string? CheckTravelProgression(int pathIndexDelta, double displacementM, double dispMinM)
    {
        if (pathIndexDelta >= 1)
            return null;
        if (displacementM >= dispMinM)
            return null;
        return $"travel canary stalled: pathIndexDelta={pathIndexDelta}, " +
               $"displacement={displacementM:F1}m < {dispMinM:F1}m";
    }

    /// <summary>Restart-conservation rule: the row must survive with its
    /// plant_time intact, AND either the pending timer kept ticking down
    /// (same phase, same growth_time, smaller remainder) or the boot
    /// catch-up fired (phase advanced via ApplyLoadedState + InitDoodad
    /// re-arm).</summary>
    private static string? CheckRestartConservation(
        int phase0, DateTime plant0, DateTime growth0, DateTime read0Utc,
        int phase1, DateTime plant1, DateTime growth1, DateTime read1Utc)
    {
        if (plant1 != plant0)
            return $"canary plant_time changed across restart ({plant0:O} -> {plant1:O}) — row not preserved";
        if (phase1 != phase0)
            return null; // catch-up fired on boot
        if (growth1 != growth0)
            return $"canary growth_time rewritten without a phase change ({growth0:O} -> {growth1:O})";
        var left0 = (growth0 - read0Utc).TotalMilliseconds;
        var left1 = (growth1 - read1Utc).TotalMilliseconds;
        if (left1 <= 0)
            return "canary growth timer expired across restart without firing — catch-up lost";
        if (!(left1 < left0))
            return $"canary TimeLeft did not decrease across restart ({left0:F0}ms -> {left1:F0}ms)";
        return null;
    }

    // ------------------------------------------------------------------ facts

    [Fact]
    public void TimerProgression_Harvest_PassesOnExactMature()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.Null(CheckHarvestProgression(
            4391, TimerCanaryMatureGroupId, due.AddHours(5), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Harvest_FailsWhenPhaseUnchanged()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CheckHarvestProgression(
            4391, 4391, due.AddHours(5), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Harvest_FailsWhenEndingBeforeDue()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CheckHarvestProgression(
            4391, TimerCanaryMatureGroupId, due.AddMinutes(-1), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Harvest_FailsWhenWrongMatureGroup()
    {
        var due = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CheckHarvestProgression(
            4391, 4504, due.AddHours(5), due, TimerCanaryMatureGroupId));
    }

    [Fact]
    public void TimerProgression_Travel_PassesOnPathIndexDelta()
    {
        Assert.Null(CheckTravelProgression(2, 0, 50));
    }

    [Fact]
    public void TimerProgression_Travel_PassesOnDisplacement()
    {
        Assert.Null(CheckTravelProgression(-1, 120, 50));
    }

    [Fact]
    public void TimerProgression_Travel_FailsWhenFrozen()
    {
        Assert.NotNull(CheckTravelProgression(-1, 0.3, 50));
        Assert.NotNull(CheckTravelProgression(0, 4.9, 50));
    }

    [Fact]
    public void TimerProgression_DispMin_PinnedFromObservation()
    {
        Assert.Equal(50, PinTravelDispMinM(300)); // healthy motion: proposal
        Assert.Equal(50, PinTravelDispMinM(100));
        Assert.Equal(30, PinTravelDispMinM(60)); // slow-but-alive scales down
        Assert.Equal(5, PinTravelDispMinM(0)); // frozen: floor keeps failing it
        Assert.Equal(5, PinTravelDispMinM(2));
    }

    [Fact]
    public void TimerProgression_Displacement_IsEuclidean()
    {
        Assert.Equal(5, DisplacementM(0, 0, 0, 3, 4, 0), precision: 9);
    }

    [Fact]
    public void TimerRestart_PreservedPendingPasses()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddMinutes(6.7);
        Assert.Null(CheckRestartConservation(
            4391, plant, growth, plant.AddMinutes(1),
            4391, plant, growth, plant.AddMinutes(4)));
    }

    [Fact]
    public void TimerRestart_CatchUpPasses()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(CheckRestartConservation(
            4391, plant, plant.AddMinutes(6.7), plant.AddMinutes(1),
            4504, plant, plant.AddMinutes(66.7), plant.AddMinutes(10)));
    }

    [Fact]
    public void TimerRestart_FailsWhenRowRewrittenOrExpired()
    {
        var plant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var growth = plant.AddMinutes(6.7);
        Assert.NotNull(CheckRestartConservation(
            4391, plant, growth, plant.AddMinutes(1),
            4391, plant.AddSeconds(1), growth, plant.AddMinutes(4)));
        Assert.NotNull(CheckRestartConservation(
            4391, plant, growth, plant.AddMinutes(1),
            4391, plant, growth, plant.AddMinutes(7)));
    }

    // ------------------------------------------------------------------ setup

    private static double ReadLiveGrowthRate()
    {
        try
        {
            var path = Path.Combine(E2eStack.RuntimeGameDir, "Config.Local.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("World", out var world) &&
                world.TryGetProperty("GrowthRate", out var rate) &&
                rate.GetDouble() > 0)
                return rate.GetDouble();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[a5t3-sixhour] growth-rate read failed ({ex.Message}) — using {FallbackGrowthRate}");
        }
        return FallbackGrowthRate;
    }

    private static JsonElement BridgeCall(string json, int timeoutMs = 15000)
    {
        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        return bridge.Call(json, timeoutMs);
    }

    private static async Task<TimerCanarySetup> SetupTimerCanariesAsync(CancellationToken cancellationToken)
    {
        s_timerCanaryDbIds.Clear();
        var growthRate = ReadLiveGrowthRate();
        var planted = await PlantTimerCanariesAsync(TimerCanaryCount, cancellationToken);

        var harvest = planted.Select(p => new HarvestCanarySetup(
            p.DbId, p.StartPhase, p.PlantUtc, p.GrowthUtc,
            p.PlantUtc.AddMilliseconds(TimerCanaryTotalDelayMs / growthRate))).ToArray();
        foreach (var h in harvest)
        {
            s_timerCanaryDbIds.Add(h.DbId);
            Console.WriteLine($"[a5t3-sixhour] harvest canary dbId={h.DbId} phase0={h.StartPhase} " +
                              $"plant={h.PlantUtc:O} due={h.DueUtc:O} " +
                              $"({(h.DueUtc - h.PlantUtc).TotalMinutes:F1} min at GrowthRate {growthRate})");
        }

        var travel = await SelectTravelCanaryAsync(cancellationToken);
        Console.WriteLine($"[a5t3-sixhour] travel canary tlId={travel.TlId} ({travel.Name}) " +
                          $"pos0=({travel.Pos0.X:F0},{travel.Pos0.Y:F0},{travel.Pos0.Z:F0}) " +
                          $"dispMin={travel.DispMinM:F0}m (observed {travel.ObservedMaxM:F0}m/10min)");
        return new TimerCanarySetup(harvest, travel, growthRate);
    }

    private sealed record PlantedTimerCanary(uint DbId, int StartPhase, DateTime PlantUtc, DateTime GrowthUtc);

    /// <summary>Plants canaries through the REAL plant path: the planter
    /// (human session) stocks seeds via the bridge, items are flushed with
    /// the existing 'save' command, instance ids come from DB-direct, and
    /// each seed is placed with a real CSCreateDoodadPacket over the
    /// planter's own authenticated link. Disconnects before returning.</summary>
    private static async Task<List<PlantedTimerCanary>> PlantTimerCanariesAsync(
        int count, CancellationToken cancellationToken)
    {
        using var planter = await ConnectHumanAsync();
        try
        {
            var pos = BridgeCall(
                "{\"cmd\":\"drive\",\"bot\":\"" + HumanChar + "\",\"op\":\"charPos\"}");
            var px = pos.GetProperty("x").GetSingle();
            var py = pos.GetProperty("y").GetSingle();
            var pz = pos.GetProperty("z").GetSingle();

            BridgeCall("{\"cmd\":\"drive\",\"bot\":\"" + HumanChar +
                       "\",\"op\":\"stock\",\"item\":" + TimerCanarySeedItemId + ",\"count\":" + count + "}");
            var inv = BridgeCall("{\"cmd\":\"drive\",\"bot\":\"" + HumanChar +
                                 "\",\"op\":\"invCount\",\"item\":" + TimerCanarySeedItemId + "}");
            if (inv.GetProperty("count").GetInt32() < count)
                throw new InvalidOperationException(
                    $"planter bag holds {inv.GetProperty("count").GetInt32()} seeds, need {count}");

            // Flush the stocked rows to MySQL (DoSave persists dirty item
            // containers) so the DB-direct instance-id read below sees them.
            BridgeCall("{\"cmd\":\"save\"}", 60000);

            var charId = ReadPlanterCharacterId();
            var itemIds = await ReadSeedItemIdsAsync(charId, count, cancellationToken);
            var maxDoodadId = ReadMaxDoodadId();

            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                planter.SendCreateDoodad(
                    TimerCanaryDoodadId, px + 4 * (i + 1), py, pz, itemIds[i]);
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            List<CanaryDoodadRow> rows = new();
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows = FindPlantedCanaries(maxDoodadId);
                if (rows.Count >= count)
                    break;
                await Task.Delay(2000, cancellationToken);
            }
            if (rows.Count < count)
                throw new InvalidOperationException(
                    $"planted {count} canaries through the real path but only {rows.Count} doodad rows " +
                    $"appeared (template {TimerCanaryDoodadId} newer than id {maxDoodadId}) — " +
                    "suspect labor gate or seed consumption");

            var planted = new List<PlantedTimerCanary>();
            foreach (var row in rows.Take(count))
            {
                if (row.Phase == TimerCanaryMatureGroupId)
                    throw new InvalidOperationException(
                        $"canary doodad {row.DbId} planted already mature (phase {row.Phase}) — " +
                        "the EXACT rule needs a start phase below mature");
                planted.Add(new PlantedTimerCanary(row.DbId, row.Phase, row.PlantUtc, row.GrowthUtc));
            }
            return planted;
        }
        finally
        {
            planter.Disconnect();
        }
    }

    private static uint ReadPlanterCharacterId()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c.id FROM characters c " +
                          "JOIN aaemu_login.users u ON u.id = c.account_id " +
                          "WHERE u.username = @u LIMIT 1";
        cmd.Parameters.AddWithValue("@u", HumanAccount);
        var value = cmd.ExecuteScalar()
            ?? throw new InvalidOperationException("planter character row missing for account " + HumanAccount);
        return Convert.ToUInt32(value);
    }

    private static async Task<List<ulong>> ReadSeedItemIdsAsync(
        uint charId, int count, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM items WHERE template_id = @t AND `owner` = @o " +
                              "ORDER BY id DESC LIMIT " + count;
            cmd.Parameters.AddWithValue("@t", TimerCanarySeedItemId);
            cmd.Parameters.AddWithValue("@o", charId);
            using var reader = cmd.ExecuteReader();
            var ids = new List<ulong>();
            while (reader.Read())
                ids.Add(reader.GetUInt64(0));
            if (ids.Count >= count)
                return ids;
            // The save pass may not have flushed yet — trigger one more.
            BridgeCall("{\"cmd\":\"save\"}", 60000);
            await Task.Delay(2000, cancellationToken);
        }
        throw new InvalidOperationException(
            $"only found stocked seed {TimerCanarySeedItemId} rows for character {charId} after two save flushes");
    }

    private static uint ReadMaxDoodadId()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM doodads";
        return Convert.ToUInt32(cmd.ExecuteScalar());
    }

    private static List<CanaryDoodadRow> FindPlantedCanaries(uint minId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, current_phase_id, plant_time, growth_time, phase_time " +
                          "FROM doodads WHERE template_id = @t AND id > @min ORDER BY id";
        cmd.Parameters.AddWithValue("@t", TimerCanaryDoodadId);
        cmd.Parameters.AddWithValue("@min", minId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<CanaryDoodadRow>();
        while (reader.Read())
        {
            rows.Add(new CanaryDoodadRow(
                reader.GetUInt32(0),
                reader.GetInt32(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
        }
        return rows;
    }

    private static CanaryDoodadRow? ReadCanaryDoodadRow(uint dbId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, current_phase_id, plant_time, growth_time, phase_time " +
                          "FROM doodads WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", dbId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new CanaryDoodadRow(
            reader.GetUInt32(0),
            reader.GetInt32(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    private static void DeleteTimerCanaryDoodads()
    {
        if (s_timerCanaryDbIds.Count == 0)
            return;
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < s_timerCanaryDbIds.Count; i++)
            {
                var name = $"@id{i}";
                cmd.Parameters.AddWithValue(name, s_timerCanaryDbIds[i]);
                names.Add(name);
            }
            cmd.CommandText = $"DELETE FROM doodads WHERE id IN ({string.Join(", ", names)})";
            var removed = cmd.ExecuteNonQuery();
            Console.WriteLine($"[a5t3-sixhour] removed {removed} timer-canary doodad rows");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[a5t3-sixhour] canary doodad cleanup skipped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            s_timerCanaryDbIds.Clear();
        }
    }

    // ------------------------------------------------------------------ travel

    private static Dictionary<ushort, (string Name, TimerCanaryPos Pos)> ParseTransferPositions(JsonElement dump)
    {
        var result = new Dictionary<ushort, (string, TimerCanaryPos)>();
        if (!dump.TryGetProperty("transfers", out var transfers) ||
            transfers.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("transfers bridge dump has no 'transfers' array");
        foreach (var t in transfers.EnumerateArray())
        {
            if (!t.TryGetProperty("tlId", out var tl) ||
                !t.TryGetProperty("position", out var p) ||
                !p.TryGetProperty("x", out var x) ||
                !p.TryGetProperty("y", out var y) ||
                !p.TryGetProperty("z", out var z))
                continue;
            var tlId = tl.GetUInt16();
            if (result.ContainsKey(tlId))
                continue; // first entry wins (matches the boarding resolve order)
            var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            result[tlId] = (name, new TimerCanaryPos(x.GetSingle(), y.GetSingle(), z.GetSingle()));
        }
        return result;
    }

    private static (int PathIndex, TimerCanaryPos? Pos) ReadTravelCanary(ushort tlId)
    {
        var dump = BridgeCall("{\"cmd\":\"transfers\"}");
        var positions = ParseTransferPositions(dump);
        if (!positions.TryGetValue(tlId, out var found))
            return (-1, null);
        // PathPointIndex is not emitted by CollectLiveTransfers today;
        // parsed opportunistically so the rule tightens itself if it appears.
        foreach (var t in dump.GetProperty("transfers").EnumerateArray())
        {
            if (!t.TryGetProperty("tlId", out var tl) || tl.GetUInt16() != tlId)
                continue;
            if (t.TryGetProperty("pathPointIndex", out var pi) || t.TryGetProperty("PathPointIndex", out pi))
                return (pi.GetInt32(), found.Pos);
        }
        return (-1, found.Pos);
    }

    private static async Task<TravelCanarySetup> SelectTravelCanaryAsync(CancellationToken cancellationToken)
    {
        var first = ParseTransferPositions(BridgeCall("{\"cmd\":\"transfers\"}"));
        await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
        var second = ParseTransferPositions(BridgeCall("{\"cmd\":\"transfers\"}"));

        ushort chosen = 0;
        var bestDisp = 0.0;
        string chosenName = "";
        foreach (var (tlId, entry) in second)
        {
            if (!first.TryGetValue(tlId, out var prev))
                continue;
            var disp = DisplacementM(prev.Pos.X, prev.Pos.Y, prev.Pos.Z, entry.Pos.X, entry.Pos.Y, entry.Pos.Z);
            if (disp < 1)
                continue; // held at a path stop — not a motion canary
            if (chosen == 0 || disp > bestDisp || (disp == bestDisp && tlId < chosen))
            {
                chosen = tlId;
                bestDisp = disp;
                chosenName = entry.Name;
            }
        }
        if (chosen == 0)
            throw new InvalidOperationException(
                $"no moving transfer found for the travel canary ({second.Count} dumped, none displaced >1m in 60s)");
        var pos0 = second[chosen].Pos;
        Console.WriteLine($"[a5t3-sixhour] travel canary candidate tlId={chosen} ({chosenName}) " +
                          $"moving {bestDisp:F1}m/60s — observing 10 min to pin DISP_MIN");

        // 10-minute observation: track the max displacement from pos0 so
        // DISP_MIN is pinned from this transfer's own path speed.
        var observedMax = 0.0;
        var observeUntil = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < observeUntil)
        {
            var remaining = observeUntil - DateTime.UtcNow;
            await Task.Delay(remaining < TimeSpan.FromSeconds(60) ? remaining : TimeSpan.FromSeconds(60),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var current = ParseTransferPositions(BridgeCall("{\"cmd\":\"transfers\"}"));
            if (current.TryGetValue(chosen, out var entry))
            {
                var disp = DisplacementM(pos0.X, pos0.Y, pos0.Z, entry.Pos.X, entry.Pos.Y, entry.Pos.Z);
                if (disp > observedMax)
                    observedMax = disp;
            }
        }
        return new TravelCanarySetup(chosen, chosenName,
            new TimerCanaryPos(pos0.X, pos0.Y, pos0.Z), -1,
            PinTravelDispMinM(observedMax), observedMax);
    }

    // -------------------------------------------------------------- validation

    /// <summary>Called ONCE at the end of the soak, beside the DB-writes
    /// check, appending to the SAME failures list. Harvest rule is EXACT
    /// (phase changed + window ran past due + end phase is the mature
    /// group); travel rule is the LOOSE lower bound (path-index step OR
    /// displacement past the pinned DISP_MIN).</summary>
    private static TimerProgressionEnd ValidateTimerProgression(
        TimerCanarySetup canaries, List<string> failures)
    {
        var endUtc = DateTime.UtcNow;
        var harvestEnds = new List<HarvestCanaryEnd>();
        foreach (var c in canaries.Harvest)
        {
            CanaryDoodadRow? row = null;
            string? readError = null;
            try
            {
                row = ReadCanaryDoodadRow(c.DbId);
            }
            catch (Exception ex)
            {
                readError = $"harvest canary doodad {c.DbId} unreadable at end of soak: {ex.GetType().Name}: {ex.Message}";
            }
            string? failure = readError
                ?? (row is null
                    ? $"harvest canary doodad {c.DbId} has no row at end of soak — persistence lost"
                    : CheckHarvestProgression(
                        c.StartPhase, row.Phase, endUtc, c.DueUtc, TimerCanaryMatureGroupId));
            if (failure != null)
                AddFailure(failures, failure);
            harvestEnds.Add(new HarvestCanaryEnd(
                c.DbId, c.StartPhase, c.PlantUtc, c.GrowthUtc, c.DueUtc,
                row?.Phase ?? -1,
                row?.GrowthUtc ?? DateTime.MinValue, endUtc, failure));
        }

        string? travelFailure;
        TimerCanaryPos? posEnd = null;
        var pathIndexDelta = -1;
        double displacement = -1;
        try
        {
            var (endIndex, endPos) = ReadTravelCanary(canaries.Travel.TlId);
            if (endPos is null)
            {
                travelFailure = $"travel canary tlId={canaries.Travel.TlId} missing from the transfers dump at end of soak";
            }
            else
            {
                posEnd = new TimerCanaryPos(endPos.X, endPos.Y, endPos.Z);
                if (canaries.Travel.StartPathIndex >= 0 && endIndex >= 0)
                    pathIndexDelta = endIndex - canaries.Travel.StartPathIndex;
                displacement = DisplacementM(
                    canaries.Travel.Pos0.X, canaries.Travel.Pos0.Y, canaries.Travel.Pos0.Z,
                    endPos.X, endPos.Y, endPos.Z);
                travelFailure = CheckTravelProgression(pathIndexDelta, displacement, canaries.Travel.DispMinM);
            }
        }
        catch (Exception ex)
        {
            travelFailure = $"travel canary tlId={canaries.Travel.TlId} unreadable at end of soak: {ex.GetType().Name}: {ex.Message}";
        }
        if (travelFailure != null)
            AddFailure(failures, travelFailure);

        var travel = new TravelCanaryEnd(
            canaries.Travel.TlId,
            new TimerCanaryPos(canaries.Travel.Pos0.X, canaries.Travel.Pos0.Y, canaries.Travel.Pos0.Z),
            posEnd, pathIndexDelta, displacement,
            canaries.Travel.DispMinM, canaries.Travel.ObservedMaxM, travelFailure);
        return new TimerProgressionEnd(harvestEnds.ToArray(), travel);
    }

    // ------------------------------------------------------- restart leg (b2)

    /// <summary>
    /// Bounded restart-conservation leg: plant one canary through the REAL
    /// plant path, record the row times, restart, then assert the row is
    /// preserved AND the timer kept its meaning (still pending with a
    /// smaller remainder, or the boot catch-up fired). Covers the SpawnManager
    /// boot load + ApplyLoadedState + InitDoodad re-arm without the 6 h soak.
    /// </summary>
    [Fact]
    public async Task Probe_A5Tier3RestartConservesDormantTimers()
    {
        if (Environment.GetEnvironmentVariable("A5_TIER3_TIMER_RESTART") != "1")
        {
            Assert.Skip("A5_TIER3_TIMER_RESTART=1 is required for the bounded restart-conservation stage.");
            return;
        }

        var ownedNames = new List<string> { HumanAccount };
        E2eStack.EnsureUp();
        var ownershipBefore = E2eStack.SnapshotOwnedRows(ownedNames);
        s_timerCanaryDbIds.Clear();
        try
        {
            ClearFeatureEnv();
            var planted = await PlantTimerCanariesAsync(1, TestContext.Current.CancellationToken);
            var before = planted[0];
            s_timerCanaryDbIds.Add(before.DbId);
            var read0Utc = DateTime.UtcNow;
            Console.WriteLine($"[a5t3-restart] canary dbId={before.DbId} phase0={before.StartPhase} " +
                              $"plant={before.PlantUtc:O} growth={before.GrowthUtc:O}");

            E2eStack.RestartGameServer();
            WaitBoot(cancellationToken: TestContext.Current.CancellationToken);
            // Catch-up tasks (1 ms re-arm when the timer lapsed mid-restart)
            // fire on the game loop right after boot markers.
            await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var after = ReadCanaryDoodadRow(before.DbId);
            var read1Utc = DateTime.UtcNow;
            Assert.NotNull(after);
            Console.WriteLine($"[a5t3-restart] after: phase={after!.Phase} " +
                              $"plant={after.PlantUtc:O} growth={after.GrowthUtc:O}");
            var reason = CheckRestartConservation(
                before.StartPhase, before.PlantUtc, before.GrowthUtc, read0Utc,
                after.Phase, after.PlantUtc, after.GrowthUtc, read1Utc);
            Assert.True(reason is null, reason);
        }
        finally
        {
            try
            {
                DeleteTimerCanaryDoodads();
                var ownershipAfter = E2eStack.SnapshotOwnedRows(ownedNames);
                var ownedRows = E2eStack.FindNewOwnedRows(ownershipBefore, ownershipAfter);
                E2eStack.CleanupOwnedRows(ownedRows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[a5t3-restart] ownership cleanup skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
