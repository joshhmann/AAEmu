using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// FISH-01 basic-fishing live verification (dossier
/// scorecard-explorations/mechanics/fishing-domain.md §5, S–M slice):
/// a REAL networked bot casts the REAL position-target fishing skill
/// 21571 (plot 809, target_type=Pos) against the live stack and the full
/// canonical loop is observed through engine-true observables:
///
///   CSStartSkillPacket (unit caster + SkillCastTargetType.Position body —
///   the exact wire shape the 1.2 client sends for a Pos-target cast) →
///   Skill.Use → Template.Plot(809).RunAsync →
///     채널링 시작 applies SpecialEffect 10880 (ApplyReagents → worm 27142
///     consumed via skill_reagents) →
///     확률 poll loop → 성공/실패 branch →
///     낚아올리기 applies SpecialEffect 10860 (FishingLoot → zone-group loot
///     pack) + 10861 (ConsumeLaborPower → accounts.labor −5, fishing
///     actability group 7 XP via ChangeLabor).
///
/// Positioning honesty: NO bridge drive op reaches CastAt or an arbitrary
/// position (the drive vocabulary has accept/kill/teleportToNpc/stock/
/// setLevel/… only — noted as the contract gap this test documents), so the
/// cast itself rides DIRECT packet injection over the bot's own authenticated
/// game link (the TransferRideE2eTests pattern). The bot is placed lakeside
/// through the real 'teleportToNpc' op (NPC 3480's spawner cluster sits on
/// the White Arden freshwater shore); the cast TARGET is the nearest fish-
/// school spawn position from the booted world's own doodad_spawns.json,
/// with the stand-off distance recorded in evidence.
///
/// Per-cast outcome classification (all observables are engine-true):
///   refused            — SCSkillStartedPacket error reply, no plot frames
///   plot-not-started   — no worm consumption, no channeling evidence
///   no-bite            — worm consumed + labor −5 + NO new items (the 실패
///                        branch of plot 809; ~75% per canonical chance)
///   BITE               — worm consumed + labor −5 + loot items granted
///
/// Evidence report (per-cast trace records: labor/worm/loot deltas + wire
/// frame tallies) lands under $E2E_ROOT/logs per convention.
/// </summary>
[Collection("e2e")]
public class FishingVerificationE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names.
    private const string BotName = "Fisherman";
    private const string AccountName = "e2efisherman";

    /// <summary>낚시하기 — canonical 1.2 fishing skill (target_type=Pos, plot 809).</summary>
    private const uint FishingSkillId = 21571;

    /// <summary>꿈틀꿈틀 지렁이 — skill_reagents row 2381: 1x per cast.</summary>
    private const uint WormItemId = 27142;

    /// <summary>대나무 낚싯대 — canonical category-145 rod (server does not gate on it; bagged for fidelity).</summary>
    private const uint RodItemId = 27308;

    /// <summary>Fishing actability group (skills.actability_group_id = 7).</summary>
    private const uint FishingActabilityId = 7;

    /// <summary>Lakeside NPC whose FIRST spawner (SpawnManager insertion order = file order) sits on the school's shore.</summary>
    private const uint ShoreNpcTemplateId = 3480u;

    private const ushort FreshwaterSchoolTemplateId = 6447;
    private const ushort SaltwaterSchoolTemplateId = 6448;

    private const int MaxCastAttempts = 20;
    private const int CastWindowMs = 11_000; // 1500ms casting + 6500ms channeling + poll margin

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    private static int MaxAttempts =>
        int.TryParse(Environment.GetEnvironmentVariable("E2E_FISHING_ATTEMPTS"), out var v) && v > 0 ? v : MaxCastAttempts;

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Cast_FishingSkillAtWater_BiteLoop_OnLiveServer_EndToEnd()
    {
        E2eStack.EnsureUp();

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        Directory.CreateDirectory(EvidenceDir);

        using var bot = await BotNetworkSession.ConnectAsync(
            BotName, AccountName, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

        Assert.True(bot.InWorld, "bot must be in-world (real login flow)");
        Assert.True(bot.CharacterId > 0, "real create/select must yield a character id");

        // Take over the wire so THIS test owns frame reads (plot/skill evidence).
        var link = GetGameLink(bot);
        StopBackgroundLoops(bot);
        using var pingCts = new CancellationTokenSource();
        var pingTask = Task.Run(() => PingLoopAsync(link, pingCts.Token));

        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            var charState = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}");
            var charObjId = charState.GetProperty("objId").GetUInt32();
            Assert.True(charObjId != 0, "charState must report the live character objId");

            // ------------------------------------------------------- RIG
            // Level >= 10, rod in bag, worm stock — all through REAL drive ops.
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":10}}");
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{RodItemId},\"count\":1}}");
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{WormItemId},\"count\":60}}");
            var riggedState = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}");
            Assert.True(riggedState.GetProperty("level").GetInt32() >= 10,
                $"setLevel did not take effect (level {riggedState.GetProperty("level").GetInt32()})");

            // Fish-school presence in the LIVE world (read-only resolve by
            // template id — the same registry FishSchoolManager indexes).
            var schoolObjId = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"doodadObjId\",\"doodad\":{FreshwaterSchoolTemplateId}}}");
            Assert.True(schoolObjId.GetProperty("objId").GetUInt32() != 0,
                $"no fish-school doodad {FreshwaterSchoolTemplateId} spawned in the live world");

            // -------------------------------------------------- POSITION
            // Lakeside placement through the REAL teleportToNpc op (the same
            // facility quest E2E uses), then sample the ACTUAL landed position
            // through the ordinary persistence path.
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{ShoreNpcTemplateId}}}");
            var landed = ForceSaveAndSample(bridge, bot.CharacterId);

            var schools = ParseSpawns(SchoolsPath(), FreshwaterSchoolTemplateId, SaltwaterSchoolTemplateId);
            Assert.True(schools.Count > 0, "booted world data carries no fish-school spawns");
            var school = schools.OrderBy(s => Dist(s.Pos, landed)).First();            var standOff = Dist(school.Pos, landed);
            Console.WriteLine($"[fishing] bot landed ({landed.X:F1},{landed.Y:F1},{landed.Z:F1}); " +
                              $"nearest school {school.TemplateId} at ({school.Pos.X:F1},{school.Pos.Y:F1},{school.Pos.Z:F1}) " +
                              $"obj={schoolObjId.GetProperty("objId").GetUInt32()} stand-off {standOff:F1} m");
            Assert.True(standOff <= 120f,
                $"nearest fish school is {standOff:F1} m away after teleport-to-shore — positioning assumption broken");

            var waterTarget = school.Pos;

            // ------------------------------------------------ BASELINES
            ForceSaveAndSample(bridge, bot.CharacterId);
            var baselineItems = DumpItemRowsDistinct(bot.CharacterId);
            var labor0 = QueryAccountLabor(AccountName);
            var actability0 = QueryActabilityPoints(bot.CharacterId);

            // ----------------------------------------------- CAST LOOP
            var traces = new List<CastTrace>();
            CastTrace? bite = null;
            CastTrace? lastTrace = null;

            for (var attempt = 1; attempt <= MaxAttempts && bite == null; attempt++)
            {
                var laborBefore = QueryAccountLabor(AccountName);
                var wormBefore = BridgeInvCount(bridge, WormItemId);

                link.SendGameFrame(CSOffsets.CSStartSkillPacket, 1, body =>
                {
                    body.Write(FishingSkillId);
                    body.Write((byte)0);          // SkillCasterType.Unit
                    WriteBc(body, charObjId);
                    body.Write((byte)1);          // SkillCastTargetType.Position
                    body.Write(Helpers.ConvertLongX(waterTarget.X));
                    body.Write(Helpers.ConvertLongY(waterTarget.Y));
                    body.Write(waterTarget.Z);
                    body.Write(0f);               // PosRot
                    WriteBc(body, 0u);            // ObjId1
                    WriteBc(body, 0u);            // ObjId2
                    body.Write((byte)0);          // flag: SkillObjectType.None
                });

                var frames = await CollectFrames(link, CastWindowMs);

                await Task.Delay(500); // let the terminal ChangeLabor UPDATE land
                var laborAfter = QueryAccountLabor(AccountName);
                var wormAfter = BridgeInvCount(bridge, WormItemId);

                var trace = new CastTrace
                {
                    Attempt = attempt,
                    LaborBefore = laborBefore,
                    LaborAfter = laborAfter,
                    WormBefore = wormBefore,
                    WormAfter = wormAfter,
                    Frames = frames
                };
                traces.Add(trace);
                lastTrace = trace;

                Console.WriteLine($"[fishing] cast #{attempt}: labor {laborBefore}->{laborAfter}, " +
                                  $"worm {wormBefore}->{wormAfter}, frames {frames.SkillStarted}/{frames.PlotEvent}/" +
                                  $"{frames.PlotEnded}/{frames.ChannelingStopped}");

                // Candidate resolution (labor charged on BOTH terminal branches):
                // confirm a bite via the ordinary persistence path (loot diff).
                if (wormAfter < wormBefore && laborAfter < laborBefore)
                {
                    ForceSaveAndSample(bridge, bot.CharacterId);
                    var itemsNow = DumpItemRowsDistinct(bot.CharacterId);
                    var gained = itemsNow.Where(k => !baselineItems.Contains(k)).ToList();
                    trace.NewItemTemplates = gained;
                    if (gained.Count > 0)
                        bite = trace;
                }
            }

            // ------------------------------------------------ VERDICT
            var reportPath = WriteReport(bot.CharacterId, waterTarget, school.TemplateId, standOff, landed, traces, labor0, actability0, bite != null);

            if (bite != null)
            {
                // Canonical assertions on the BITE cast: labor −5, fishing
                // actability XP granted, worm consumed, loot gained.
                var laborDrop = bite.LaborBefore - bite.LaborAfter;
                Assert.True(laborDrop is > 2 and < 12,
                    $"bite labor delta {laborDrop} outside the expected −5 (+ regen noise) — traces: {FormatTraces(traces)}");
                var actabilityNow = QueryActabilityPoints(bot.CharacterId);
                Assert.True(actabilityNow > actability0,
                    $"fishing actability points did not increase ({actability0} -> {actabilityNow}) despite labor consumption");
                Assert.NotEmpty(bite.NewItemTemplates);
                Assert.True(bite.WormAfter < bite.WormBefore, "worm was not consumed on the bite cast");

                Console.WriteLine($"[fishing] PASS after {traces.Count} cast(s) — loot templates [{string.Join(',', bite.NewItemTemplates)}], report: {reportPath}");
            }
            else
            {
                // Precise failure classification across ALL attempts — never a
                // bare fail. If the plot runtime itself broke (the dossier
                // flags PlotCondition.Variable), surface stage + reason and stop.
                var classification = ClassifyAllNoBite(traces);
                var logFindings = ScanGameLogForPlotErrors(logOffset);
                var detail =
                    $"no bite in {traces.Count} casts. Classification: {classification}. " +
                    $"Plot-error log lines appended by the run: {(logFindings.Count > 0 ? string.Join(" | ", logFindings.Take(5)) : "none")}. " +
                    $"Report: {reportPath}";
                var plotRuntimeBroken = traces.All(t => t.WormAfter == t.WormBefore)
                    || logFindings.Count > 0;
                Assert.True(!plotRuntimeBroken, "PLOT-RUNTIME FINDING (deliverable per task): " + detail);
                Assert.Fail(detail);
            }

            // No unhandled exceptions in the game-log tail the run appended.
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            Assert.True(unhandled == 0 && fatals == 0,
                $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the fishing run");
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { /* cancelled */ }
            bot.Disconnect();
            E2eStack.CleanupBotRows(AccountName);
        }
    }

    // -------------------------------------------------------- cast tracing

    private sealed class CastTrace
    {
        public int Attempt;
        public long LaborBefore;
        public long LaborAfter;
        public int WormBefore;
        public int WormAfter;
        public FrameTally Frames = new();
        public List<uint> NewItemTemplates = [];
    }

    private sealed class FrameTally
    {
        public int SkillStarted;
        public int SkillFired;
        public int SkillEnded;
        public int PlotEvent;
        public int PlotEnded;
        public int CastingStopped;
        public int ChannelingStopped;
    }

    private static async Task<FrameTally> CollectFrames(BotTcpLink link, int windowMs)
    {
        var tally = new FrameTally();
        var deadline = Environment.TickCount64 + windowMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                switch (frame.Type)
                {
                    case SCOffsets.SCSkillStartedPacket: tally.SkillStarted++; break;
                    case SCOffsets.SCSkillFiredPacket: tally.SkillFired++; break;
                    case SCOffsets.SCSkillEndedPacket: tally.SkillEnded++; break;
                    case SCOffsets.SCPlotEventPacket: tally.PlotEvent++; break;
                    case SCOffsets.SCPlotEndedPacket: tally.PlotEnded++; break;
                    case SCOffsets.SCPlotCastingStoppedPacket: tally.CastingStopped++; break;
                    case SCOffsets.SCPlotChannelingStoppedPacket: tally.ChannelingStopped++; break;
                }
            }
            await Task.Delay(100);
        }
        return tally;
    }

    /// <summary>Maps the all-attempts-no-bite outcome onto the plot-809 stage that failed.</summary>
    private static string ClassifyAllNoBite(List<CastTrace> traces)
    {
        if (traces.Count == 0)
            return "no casts recorded";
        if (traces.All(t => t.Frames.SkillStarted > 0 && t.Frames.PlotEvent == 0))
            return "every cast got a skill reply but ZERO SCPlotEventPacket — plot 809 never started (Skill.Use plot dispatch)";
        if (traces.All(t => t.WormAfter == t.WormBefore))
            return "worm never consumed — plot 809 never reached the channeling-start event (채널링 시작 / ApplyReagents 10880)";
        if (traces.All(t => t.LaborAfter >= t.LaborBefore))
            return "worm consumed but labor NEVER dropped — plot 809 stalled inside channeling (확률 poll loop / chance branch never resolved)";
        return "casts resolved (labor consumed) but the success branch (성공→낚아올리기→FishingLoot 10860) never fired in " +
               $"{traces.Count} casts — consistent with the canonical no-bite chance never rolling, or the variable/chance gate misbranching";
    }

    // ------------------------------------------------------ world data

    private sealed record SpawnPoint(uint TemplateId, (float X, float Y, float Z) Pos);

    private static string WorldsDir => Path.Combine(E2eStack.RuntimeGameDir, "Data", "Worlds", "main_world");

    private static string SchoolsPath() => Path.Combine(WorldsDir, "doodad_spawns.json");

    private static List<SpawnPoint> ParseSpawns(string path, params ushort[] templateIds)
    {
        // The world spawn files carry minor JSON dialect issues (trailing
        // commas); parse structurally with a regex instead of a strict parser.
        var rx = new Regex(
            @"\{\s*""UnitId"":\s*(\d+),\s*""Position"":\s*\{\s*""X"":\s*(-?[\d.eE+]+),\s*""Y"":\s*(-?[\d.eE+]+),\s*""Z"":\s*(-?[\d.eE+]+)",
            RegexOptions.Compiled);
        var wanted = templateIds.Select(t => (uint)t).ToHashSet();
        var result = new List<SpawnPoint>();
        if (!File.Exists(path))
            return result;
        foreach (var m in rx.Matches(File.ReadAllText(path)))
        {
            var match = (Match)m;
            var id = uint.Parse(match.Groups[1].Value);
            if (!wanted.Contains(id))
                continue;
            result.Add(new SpawnPoint(id, (
                float.Parse(match.Groups[2].Value),
                float.Parse(match.Groups[3].Value),
                float.Parse(match.Groups[4].Value))));
        }
        return result;
    }

    // --------------------------------------------------------- MySQL probes

    private static long QueryAccountLabor(string accountName)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT a.labor FROM accounts a JOIN aaemu_login.users u ON a.account_id = u.id WHERE u.username = @name";
        cmd.Parameters.AddWithValue("@name", accountName);
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? 0 : Convert.ToInt64(raw);
    }

    private static long QueryActabilityPoints(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT point FROM actabilities WHERE owner = @owner AND id = @id";
        cmd.Parameters.AddWithValue("@owner", characterId);
        cmd.Parameters.AddWithValue("@id", FishingActabilityId);
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? 0 : Convert.ToInt64(raw);
    }

    private static HashSet<uint> DumpItemRowsDistinct(uint characterId)
        => E2eStack.DumpItemRows(characterId).ToHashSet();

    private static (float X, float Y, float Z) ForceSaveAndSample(BotDriveClient bridge, uint characterId)
    {
        bridge.Call("{\"cmd\":\"save\"}", 180_000);
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT x, y, z FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"character {characterId} has no row to sample");
        return (reader.GetFloat(0), reader.GetFloat(1), reader.GetFloat(2));
    }

    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ------------------------------------------------------------- bridge

    private static int BridgeInvCount(BotDriveClient bridge, uint itemTemplateId)
        => bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"invCount\",\"item\":{itemTemplateId}}}")
            .GetProperty("count").GetInt32();

    // -------------------------------------------------------------- frames

    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xFF));
        stream.Write((byte)((value >> 8) & 0xFF));
        stream.Write((byte)((value >> 16) & 0xFF));
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
                    body.Write(0L); // tPhy
                    body.Write(0L); // ping
                    body.Write(0u); // local
                });
            }
        }
        catch
        {
            // cancelled or socket died — the test's own frames will surface it
        }
    }

    // ------------------------------------------------ session plumbing

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

    // ---------------------------------------------------------- game log

    private static List<string> ScanGameLogForPlotErrors(long startOffset)
    {
        var markers = new[] { "[Plot Effects Error]", "Main Loop Error" };
        var found = new List<string>();
        try
        {
            if (!File.Exists(GameLogPath))
                return found;
            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return found;
            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
                if (markers.Any(line.Contains))
                    found.Add(line.Trim());
        }
        catch (IOException)
        {
        }
        return found;
    }

    private static int CountLogTailMatches(long startOffset, string marker)
    {
        try
        {
            if (!File.Exists(GameLogPath))
                return 0;
            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return 0;
            fs.Seek(startOffset, SeekOrigin.Begin);
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

    // ------------------------------------------------------------ reports

    private static string FormatTraces(List<CastTrace> traces)
        => string.Join("; ", traces.Select(t =>
            $"#{t.Attempt} labor {t.LaborBefore}->{t.LaborAfter} worm {t.WormBefore}->{t.WormAfter} " +
            $"frames {t.Frames.SkillStarted}/{t.Frames.PlotEvent}/{t.Frames.PlotEnded}" +
            (t.NewItemTemplates.Count > 0 ? $" loot [{string.Join(',', t.NewItemTemplates)}]" : "")));

    private static string WriteReport(uint characterId, (float X, float Y, float Z) waterTarget, uint waterTemplateId, float standOff,
        (float X, float Y, float Z) landed, List<CastTrace> traces, long labor0, long actability0, bool bit)
    {
        var report = new
        {
            scenario = "basic-fishing-verification",
            milestone = "FISH-01 S-M slice (live stack)",
            verdict = bit ? "PASS" : "NO-BITE-CLASSIFIED",
            bot = BotName,
            account = AccountName,
            characterId = characterId,
            skillId = FishingSkillId,
            plotId = 809,
            rodItemId = RodItemId,
            wormItemId = WormItemId,
            fishingActabilityId = FishingActabilityId,
            waterTarget = new { TemplateId = waterTemplateId, waterTarget.X, waterTarget.Y, waterTarget.Z },
            botLandedPosition = new { landed.X, landed.Y, landed.Z },
            schoolStandOffM = standOff,
            accountLaborBaseline = labor0,
            fishingActabilityBaseline = actability0,
            attempts = traces.Count,
            bites = bit ? 1 : 0,
            casts = traces.Select(t => new
            {
                attempt = t.Attempt,
                laborDelta = t.LaborAfter - t.LaborBefore,
                wormDelta = t.WormAfter - t.WormBefore,
                lootTemplatesGained = t.NewItemTemplates,
                frames = new
                {
                    scSkillStarted = t.Frames.SkillStarted,
                    scSkillFired = t.Frames.SkillFired,
                    scSkillEnded = t.Frames.SkillEnded,
                    scPlotEvent = t.Frames.PlotEvent,
                    scPlotEnded = t.Frames.PlotEnded,
                    scPlotCastingStopped = t.Frames.CastingStopped,
                    scPlotChannelingStopped = t.Frames.ChannelingStopped
                },
                outcome =
                    t.NewItemTemplates.Count > 0 ? "BITE"
                    : t.WormAfter < t.WormBefore && t.LaborAfter < t.LaborBefore ? "no-bite (plot 809 실패 branch)"
                    : t.WormAfter < t.WormBefore ? "resolved-through-channeling-labor-unobserved"
                    : t.Frames.PlotEvent > 0 ? "plot-started-no-reagent-consumption"
                    : "plot-not-started"
            }),
            note = "bridge drive vocabulary has NO cast/CastAt or arbitrary-position op (documented gap); " +
                   "cast injected as a direct CSStartSkillPacket (unit caster + Position cast target) over the " +
                   "bot's own authenticated game link; labor/actability/worm/loot sampled through engine-true " +
                   "surfaces (MySQL accounts.labor + actabilities, bridge invCount, items-table diff after save)"
        };

        var path = Path.Combine(EvidenceDir, "fishing-e2e-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var sb = new StringBuilder();
        sb.AppendLine("# FISH-01 basic-fishing live verification");
        sb.AppendLine();
        sb.AppendLine($"- date: {DateTime.UtcNow:o}");
        sb.AppendLine($"- verdict: {report.verdict} ({traces.Count} casts, bites: {(bit ? 1 : 0)})");
        sb.AppendLine($"- water target: school template {waterTemplateId} at ({waterTarget.X:F1},{waterTarget.Y:F1},{waterTarget.Z:F1}), stand-off {standOff:F1} m");
        sb.AppendLine($"- report json: {path}");
        sb.AppendLine();
        sb.AppendLine(FormatTraces(traces));
        File.WriteAllText(Path.Combine(EvidenceDir, "fishing-e2e-summary.md"), sb.ToString());
        return path;
    }
}
