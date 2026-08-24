using System.Reflection;
using System.Text;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// TRANSFER-01 live-stack proof: a REAL game server boots (same binaries,
/// same MySQL, same config precedence as prod), a REAL networked bot enters
/// the world through the real login flow (BotNetworkSession — real login
/// auth + cookie + create/select/spawn), and boards a REAL spawned transfer
/// (ferries/carriages spawn from Data/Worlds/main_world/transfer_spawns.json
/// through SpawnManager at boot) via the REAL C2G packet path:
///
///   CSBoardingTransferPacket (offset 0x067, tl + attach point) →
///   transfer resolve by TlId → seat doodad with matching
///   DoodadFuncAttachment.AttachPointId → Seat.LoadPassenger +
///   BondDoodad + transform parenting + SCBondDoodadPacket.
///
/// Target selection is NOT blind: the read-only bridge "transfers" command
/// dumps every live transfer (tlId, objId, name, position) with its seat
/// benches resolved from AttachedDoodads × DoodadFuncAttachment templates
/// ({doodadObjId, attachPoint, bondKind}). The test picks a transfer WITH an
/// attachment seat — preferring a bondable one (BondKind &gt; BondInvalid) —
/// and sends exactly ONE boarding packet with that tlId + attach point over
/// the bot's own authenticated game link. No TlId scanning.
///
/// Ride proof: the bot sends NO movement packets (it is static), so any
/// server-side displacement can only come from riding. Character positions
/// are sampled through the ordinary persistence path (bridge save trigger →
/// MySQL characters.x/y/z) on a 12s cadence and must follow the transfer
/// between two samples. Disembark mirrors the UnboardVehicle transfer branch
/// exactly: CSUnbondDoodadPacket drives Seat.UnLoadPassenger + Bonding clear
/// + transform detach + SCUnbondDoodadPacket, and must leave the character
/// at the transfer's CURRENT position (no snap-back to the boarding spot).
///
/// Honest-failure contract: if no live transfer carries an attachment seat,
/// or the chosen seat still refuses the bond, the test writes a precise
/// blocker report — WITH the full live-transfer dump inline — under
/// $E2E_ROOT/logs instead of forcing a pass.
/// </summary>
[Collection("e2e")]
public class TransferRideE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names (InvalidCharacters).
    private const string BotName = "TransferRider";
    private const string AccountName = "e2etransferrider";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    /// <summary>How long the ride leg may run before giving up (env-overridable).</summary>
    private static int MaxRideSeconds
    {
        get
        {
            return int.TryParse(Environment.GetEnvironmentVariable("E2E_TRANSFER_RIDE_SECONDS"), out var v) && v > 0
                ? v
                : 300;
        }
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Board_RideRouteSegment_Disembark_OnLiveServer_EndToEnd()
    {
        E2eStack.EnsureUp();

        // Log-tail baseline: the exception scan covers only what the run appends.
        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        Directory.CreateDirectory(EvidenceDir);

        using var bot = await BotNetworkSession.ConnectAsync(
            BotName, AccountName, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

        Assert.True(bot.InWorld, "bot must be in-world (real login flow)");
        Assert.True(bot.CharacterId > 0, "real create/select must yield a character id");

        // Take over the wire: stop the session's background drain loop so THIS
        // test owns frame reads (SCBondDoodad / SCUnbondDoodad / movement),
        // and run our own ping keep-alive against the 30s dead-account sweep.
        var link = GetGameLink(bot);
        StopBackgroundLoops(bot);
        using var pingCts = new CancellationTokenSource();
        var pingTask = Task.Run(() => PingLoopAsync(link, pingCts.Token));

        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // The character ObjId is needed for the unbond packet (ReadBc).
            var charState = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}");
            var charObjId = charState.GetProperty("objId").GetUInt32();
            Assert.True(charObjId != 0, "charState must report the live character objId");

            // Live-transfer presence check: the booted world must have spawned
            // route transfers (SpawnManager boot line), otherwise selection is
            // meaningless and that alone is a blocker.
            var spawnedTransfers = ReadSpawnedTransferCount();
            Assert.True(spawnedTransfers > 0,
                "the booted world spawned 0 Transfers — transfer spawners are missing from the world data");

            // ------------------------------------------- LIVE-TRANSFER DUMP
            // Read-only walk of TransferManager.GetTransfers() →
            // AttachedDoodads → DoodadFuncAttachment templates.
            var dumpData = bridge.Call("{\"cmd\":\"transfers\"}");
            var dumpJson = JsonSerializer.Serialize(dumpData, new JsonSerializerOptions { WriteIndented = true });
            var dumpPath = Path.Combine(EvidenceDir, "transfer-ride-transfers-dump.json");
            File.WriteAllText(dumpPath, dumpJson);
            Console.WriteLine($"[transfer-ride] live-transfer dump ({dumpPath}):");
            Console.WriteLine(dumpJson);

            var transfers = ParseTransfers(dumpData);
            Console.WriteLine($"[transfer-ride] registry holds {transfers.Count} transfer entries " +
                              $"({transfers.Count(t => t.Seats.Count > 0)} with attachment seats)");

            // ------------------------------------------------------ SELECT
            // A transfer that actually carries an attachment seat; prefer a
            // BONDABLE seat (BondKind > BondInvalid) so the ride path (not
            // the ship BindSlave branch) is exercised.
            TransferInfo chosen = null;
            SeatInfo chosenSeat = null;
            foreach (var t in transfers.Where(t => t.Seats.Count > 0))
            {
                var bondable = t.Seats.FirstOrDefault(s => s.BondKind is not ("None" or "BondInvalid"));
                if (bondable == null)
                    continue;
                chosen = t;
                chosenSeat = bondable;
                break;
            }

            if (chosen == null)
            {
                chosen = transfers.FirstOrDefault(t => t.Seats.Count > 0);
                chosenSeat = chosen?.Seats[0];
            }

            if (chosen == null || chosenSeat == null)
            {
                var blockerPath = WriteBlockerReport(transfers, dumpJson, logOffset,
                    "no live transfer entry carries any DoodadFuncAttachment seat " +
                    "(AttachedDoodads × func templates are empty across the whole registry)");
                Assert.Fail(
                    $"No live transfer with an attachment seat exists ({transfers.Count} entries dumped). " +
                    $"Blocker report: {blockerPath}");
            }

            // Handler semantics check: CSBoardingTransferPacket resolves by
            // GetTransfers().FirstOrDefault(TlId == tl). If another entry
            // sharing our TlId precedes us in the registry, the handler will
            // look for seats on THAT entry instead of ours — surface this in
            // evidence either way.
            var resolvesToChosen = transfers
                .Where(t => t.TlId == chosen.TlId)
                .First().ObjId == chosen.ObjId;
            Console.WriteLine(
                $"[transfer-ride] selected transfer '{chosen.Name}' tl={chosen.TlId} obj={chosen.ObjId} " +
                $"seat={chosenSeat.DoodadObjId} ap={chosenSeat.AttachPoint} kind={chosenSeat.BondKind}; " +
                "TlId-first-resolve=" + (resolvesToChosen ? "yes" : "NO (another entry shadows it)"));

            var preBoard = ForceSaveAndSample(bridge, bot.CharacterId);

            // ------------------------------------------------------- BOARD
            // One targeted packet — no scan: exact tlId + exact attach point.
            link.SendGameFrame(CSOffsets.CSBoardingTransferPacket, 1, body =>
            {
                body.Write(chosen.TlId);
                body.Write(chosenSeat.AttachPoint);
            });

            var bond = WaitForFrame(link, SCOffsets.SCBondDoodadPacket, 15_000);
            if (bond == null)
            {
                var blockerPath = WriteBlockerReport(transfers, dumpJson, logOffset,
                    $"CSBoardingTransferPacket(tl={chosen.TlId}, ap={chosenSeat.AttachPoint}) on transfer " +
                    $"'{chosen.Name}' (obj {chosen.ObjId}, seat doodad {chosenSeat.DoodadObjId}) produced NO " +
                    $"SCBondDoodadPacket within 15s — the handler silently refused (resolve-by-TlId lands on " +
                    $"{(resolvesToChosen ? "the chosen entry" : "a DIFFERENT entry that shares the TlId and shadows it")})");
                Assert.Fail($"Chosen seat did not bond. Blocker report: {blockerPath}");
            }

            var (bondUnitObjId, bondAp, bondSeatObjId) = ParseBond(bond);
            Assert.Equal(charObjId, bondUnitObjId);
            Assert.Equal(chosenSeat.DoodadObjId, bondSeatObjId);
            Console.WriteLine($"[transfer-ride] BOARD hit confirmed on the wire: unit={bondUnitObjId} " +
                              $"ap={bondAp} seat={bondSeatObjId}");

            // -------------------------------------------------------- RIDE
            // The bot never sends movement packets — any displacement below is
            // the carriage carrying it. Sample through the ordinary save path
            // on a 12s cadence.
            var streams = new Dictionary<uint, MovementTrack>();
            CollectMovementFrames(link, streams);

            var samples = new List<(DateTime At, float X, float Y, float Z)> { (DateTime.UtcNow, preBoard.X, preBoard.Y, preBoard.Z) };
            var rideDeadline = DateTime.UtcNow.AddSeconds(MaxRideSeconds);
            while (DateTime.UtcNow < rideDeadline)
            {
                await Task.Delay(12_000);
                CollectMovementFrames(link, streams);
                var sample = ForceSaveAndSample(bridge, bot.CharacterId);
                samples.Add((DateTime.UtcNow, sample.X, sample.Y, sample.Z));

                if (Dist(sample, preBoard) > 15f)
                    break; // two samples prove the follow — stop riding
            }

            var lastRide = samples[^1];
            var rideDisplacement = Dist((lastRide.X, lastRide.Y, lastRide.Z), preBoard);
            Assert.True(samples.Count >= 2, "at least two boarded position samples are required");
            Assert.True(rideDisplacement > 10f,
                $"the rider did not follow the transfer between two samples (displacement {rideDisplacement:F1} m, " +
                $"{samples.Count} samples over {(lastRide.At - samples[0].At).TotalSeconds:F0}s) — " +
                $"position samples: {FormatSamples(samples)}");

            // ---------------------------------------------------- DISEMBARK
            // The UnboardVehicle transfer branch as a client packet:
            // CSUnbondDoodadPacket(charObjId, seatObjId) → Seat.UnLoadPassenger
            // + Bonding clear + transform detach + SCUnbondDoodadPacket.
            link.SendGameFrame(CSOffsets.CSUnbondDoodadPacket, 1, body =>
            {
                WriteBc(body, charObjId);
                WriteBc(body, chosenSeat.DoodadObjId);
            });

            var unbond = WaitForFrame(link, SCOffsets.SCUnbondDoodadPacket, 10_000);
            var unbondCharObjId = 0u;
            var unbondSeatObjId = 0u;
            if (unbond != null && unbond.Length >= 10)
            {
                // Wire layout: bc(charObjId)[0..2] + u32(characterId)[3..6] + bc(seatObjId)[7..9]
                unbondCharObjId = ReadBc(unbond, 0);
                unbondSeatObjId = ReadBc(unbond, 7);
            }

            Assert.True(unbond != null, "no SCUnbondDoodadPacket after CSUnbondDoodadPacket — detachment not confirmed on the wire");
            Assert.Equal(charObjId, unbondCharObjId);
            Assert.Equal(chosenSeat.DoodadObjId, unbondSeatObjId);

            // Detached AT the current transfer position: close to where the
            // ride left the character, far away from the boarding point.
            CollectMovementFrames(link, streams);
            var final = ForceSaveAndSample(bridge, bot.CharacterId);
            var driftSinceLastSample = Dist(final, (lastRide.X, lastRide.Y, lastRide.Z));
            var snapBack = Dist(final, preBoard);

            Assert.True(driftSinceLastSample < 25f,
                $"post-unboard position drifted {driftSinceLastSample:F1} m past the last boarded sample — unexpected teleport");
            Assert.True(snapBack > 15f,
                $"post-unboard position snapped back toward the boarding point (only {snapBack:F1} m away) — " +
                "detachment did not happen at the transfer's current position");

            // ---------------------------------------------------- EVIDENCE
            var reportPath = WritePassReport(chosen, chosenSeat, charObjId, preBoard, samples, final,
                streams, spawnedTransfers, resolvesToChosen);
            Console.WriteLine($"[transfer-ride] PASS — report: {reportPath}");

            // No unhandled exceptions in the game-log tail the run appended.
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            Assert.True(unhandled == 0 && fatals == 0,
                $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the transfer ride run");
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { /* cancelled */ }
            bot.Disconnect();

            // Cycle hygiene: wipe the bot's rows so reruns start fresh.
            E2eStack.CleanupBotRows(AccountName);
        }
    }

    // -------------------------------------------------- live-transfer dump

    private sealed record SeatInfo(uint DoodadObjId, uint DoodadTemplateId, byte AttachPoint, string BondKind);

    private sealed record TransferInfo(
        uint WorldId, ushort TlId, uint ObjId, string Name,
        float X, float Y, float Z, List<SeatInfo> Seats);

    private static List<TransferInfo> ParseTransfers(JsonElement dumpData)
    {
        var result = new List<TransferInfo>();
        foreach (var t in dumpData.GetProperty("transfers").EnumerateArray())
        {
            var seats = new List<SeatInfo>();
            foreach (var s in t.GetProperty("seats").EnumerateArray())
            {
                seats.Add(new SeatInfo(
                    s.GetProperty("doodadObjId").GetUInt32(),
                    s.GetProperty("doodadTemplateId").GetUInt32(),
                    s.GetProperty("attachPoint").GetByte(),
                    s.GetProperty("bondKind").GetString() ?? ""));
            }

            result.Add(new TransferInfo(
                t.GetProperty("worldId").GetUInt32(),
                t.GetProperty("tlId").GetUInt16(),
                t.GetProperty("objId").GetUInt32(),
                t.GetProperty("name").GetString() ?? "",
                t.GetProperty("position").GetProperty("x").GetSingle(),
                t.GetProperty("position").GetProperty("y").GetSingle(),
                t.GetProperty("position").GetProperty("z").GetSingle(),
                seats));
        }

        return result;
    }

    // ------------------------------------------------------------- movement

    private sealed class MovementTrack
    {
        public int Count;
        public float FirstX, FirstY, FirstZ;
        public float LastX, LastY, LastZ;
        public DateTime FirstAt, LastAt;
    }

    /// <summary>
    /// Drains queued frames and tracks Transfer-type movement streams
    /// (SCOneUnitMovementPacket, MoveTypeEnum.Transfer = 5): wire evidence of
    /// the carriages moving along their routes while we ride.
    /// </summary>
    private static void CollectMovementFrames(BotTcpLink link, IDictionary<uint, MovementTrack> streams)
    {
        foreach (var frame in link.DrainAll())
        {
            if (frame.Type != SCOffsets.SCOneUnitMovementPacket || frame.Body.Length < 18)
                continue;

            var body = frame.Body;
            if (body[3] != 5) // MoveTypeEnum.Transfer
                continue;

            var objId = ReadBc(body, 0);
            var pos = Helpers.ConvertPosition(body[9..18]);
            var now = DateTime.UtcNow;

            if (!streams.TryGetValue(objId, out var track))
            {
                streams[objId] = new MovementTrack
                {
                    Count = 1,
                    FirstX = pos.x, FirstY = pos.y, FirstZ = pos.z,
                    LastX = pos.x, LastY = pos.y, LastZ = pos.z,
                    FirstAt = now, LastAt = now
                };
                continue;
            }

            track.Count++;
            track.LastX = pos.x;
            track.LastY = pos.y;
            track.LastZ = pos.z;
            track.LastAt = now;
        }
    }

    // --------------------------------------------------------------- frames

    private static bool TryTakeFrame(BotTcpLink link, ushort type, out byte[] body)
    {
        foreach (var frame in link.DrainAll())
        {
            if (frame.Type != type)
                continue;
            body = frame.Body;
            return true;
        }

        body = null;
        return false;
    }

    private static byte[] WaitForFrame(BotTcpLink link, ushort type, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (TryTakeFrame(link, type, out var body))
                return body;
            Thread.Sleep(25);
        }

        return null;
    }

    /// <summary>body = bc(unitObjId) byte(attachPoint) bc(seatObjId) byte(kind) i32 i32.</summary>
    private static (uint UnitObjId, byte AttachPoint, uint SeatObjId) ParseBond(byte[] body)
    {
        if (body.Length < 7)
            return (0, 0, 0);
        return (ReadBc(body, 0), body[3], ReadBc(body, 4));
    }

    /// <summary>bc = 24-bit little-endian (PacketStream.WriteBc/ReadBc).</summary>
    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xFF));
        stream.Write((byte)((value >> 8) & 0xFF));
        stream.Write((byte)((value >> 16) & 0xFF));
    }

    private static uint ReadBc(byte[] body, int offset)
        => (uint)(body[offset] | (body[offset + 1] << 8) | (body[offset + 2] << 16));

    // ----------------------------------------------------------- keep-alive

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

    // -------------------------------------------- session plumbing (reflect)

    /// <summary>The authenticated game link owned by the networked bot session.</summary>
    private static BotTcpLink GetGameLink(BotNetworkSession session)
        => (BotTcpLink)typeof(BotNetworkSession)
            .GetField("_game", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    /// <summary>
    /// Cancels the session's keep-alive/drain loops so the test owns every
    /// incoming frame (the drain loop would swallow the bond/unbond evidence).
    /// </summary>
    private static void StopBackgroundLoops(BotNetworkSession session)
    {
        if (typeof(BotNetworkSession)
                .GetField("_keepAliveCts", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) is CancellationTokenSource cts)
            cts.Cancel();
    }

    // ---------------------------------------------------------- persistence

    private static (float X, float Y, float Z) ForceSaveAndSample(BotDriveClient bridge, uint characterId)
    {
        // Deterministic persistence point: the bridge save command runs the
        // REAL SaveManager.DoSave pass synchronously; characters.x/y/z come
        // from Transform.World.Position (Character.Save).
        bridge.Call("{\"cmd\":\"save\"}", 180_000);
        return QueryCharacterPosition(characterId);
    }

    private static (float X, float Y, float Z) QueryCharacterPosition(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT x, y, z FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"character {characterId} has no row to sample");
        return (reader.GetFloat(0), reader.GetFloat(1), reader.GetFloat(2));
    }

    // -------------------------------------------------------------- helpers

    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string FormatSamples(List<(DateTime At, float X, float Y, float Z)> samples)
        => string.Join(" → ", samples.Select(s => $"({s.X:F1},{s.Y:F1})"));

    /// <summary>"Spawning N Transfers in world ..." from the boot log.</summary>
    private static int ReadSpawnedTransferCount()
    {
        try
        {
            if (!File.Exists(GameLogPath))
                return 0;
            foreach (var line in File.ReadLines(GameLogPath))
                if (line.Contains("Transfers in world") && line.Contains("Spawning "))
                {
                    var start = line.IndexOf("Spawning ", StringComparison.Ordinal) + "Spawning ".Length;
                    var end = line.IndexOf(' ', start);
                    if (int.TryParse(line[start..end], out var n) && n > 0)
                        return n;
                }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    /// <summary>Counts marker lines in the game-log bytes appended since <paramref name="startOffset"/>.</summary>
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

    /// <summary>Collects BoardingTransfer diagnostics the run appended to the game log.</summary>
    private static List<string> ReadBoardingLogTail(long startOffset)
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(GameLogPath))
                return lines;
            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return lines;
            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
                if (line.Contains("BoardingTransfer", StringComparison.OrdinalIgnoreCase))
                    lines.Add(line);
        }
        catch (IOException)
        {
        }

        return lines;
    }

    // -------------------------------------------------------------- reports

    private static string WritePassReport(TransferInfo transfer, SeatInfo seat, uint charObjId,
        (float X, float Y, float Z) preBoard,
        List<(DateTime At, float X, float Y, float Z)> samples,
        (float X, float Y, float Z) final,
        Dictionary<uint, MovementTrack> streams,
        int spawnedTransfers, bool resolvesToChosen)
    {
        var report = new
        {
            scenario = "transfer-ride",
            milestone = "TRANSFER-01 boarding (live stack)",
            verdict = "PASS",
            bot = BotName,
            account = AccountName,
            characterObjId = charObjId,
            transferTlId = transfer.TlId,
            transferObjId = transfer.ObjId,
            transferName = transfer.Name,
            attachPoint = seat.AttachPoint,
            seatBondKind = seat.BondKind,
            seatDoodadObjId = seat.DoodadObjId,
            seatDoodadTemplateId = seat.DoodadTemplateId,
            tlIdResolvesToChosenEntry = resolvesToChosen,
            transfersSpawnedAtBoot = spawnedTransfers,
            boardingPosition = new { preBoard.X, preBoard.Y, preBoard.Z },
            positionSamples = samples.Select(s => new { at = s.At.ToString("o"), s.X, s.Y, s.Z }),
            rideDisplacementM = Dist((samples[^1].X, samples[^1].Y, samples[^1].Z), preBoard),
            finalPosition = new { final.X, final.Y, final.Z },
            finalVsLastSampleM = Dist(final, (samples[^1].X, samples[^1].Y, samples[^1].Z)),
            finalVsBoardingM = Dist(final, preBoard),
            transferMovementStreams = streams.Values.Select(t => new
            {
                updates = t.Count,
                first = new { t.FirstX, t.FirstY, t.FirstZ },
                last = new { t.LastX, t.LastY, t.LastZ },
                firstAt = t.FirstAt.ToString("o"),
                lastAt = t.LastAt.ToString("o")
            }).ToArray(),
            note = "target selected from the live bridge 'transfers' dump (read-only); " +
                   "board via injected CSBoardingTransferPacket over the bot's own authenticated game link; " +
                   "disembark via CSUnbondDoodadPacket (the UnboardVehicle transfer-branch path); rider carries " +
                   "no self-movement, so MySQL-sampled displacement is the transfer carrying it"
        };

        var path = Path.Combine(EvidenceDir, "transfer-ride-e2e-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string WriteBlockerReport(List<TransferInfo> transfers, string dumpJson,
        long logOffset, string reason)
    {
        var boardingLines = ReadBoardingLogTail(logOffset);
        var sb = new StringBuilder();
        sb.AppendLine("# TRANSFER-01 live-stack E2E — BLOCKER");
        sb.AppendLine();
        sb.AppendLine($"- date: {DateTime.UtcNow:o}");
        sb.AppendLine($"- stack: {E2eStack.E2eRoot} (login :{E2eStack.LoginPort}, game :{E2eStack.GamePort}, bridge :{E2eStack.BridgePort})");
        sb.AppendLine($"- reason: {reason}");
        sb.AppendLine($"- live registry entries dumped: {transfers.Count} " +
                      $"({transfers.Count(t => t.Seats.Count > 0)} carry attachment seats)");
        sb.AppendLine($"- game.log 'BoardingTransfer' lines appended by the run: {boardingLines.Count}");
        sb.AppendLine("- full dump also saved to: transfer-ride-transfers-dump.json (same directory)");
        sb.AppendLine();

        sb.AppendLine("## Live-transfer registry (bridge 'transfers' dump)");
        sb.AppendLine();
        sb.AppendLine("| tlId | objId | name | pos(x,y) | seats (doodad/ap/kind) |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var t in transfers)
        {
            var seats = t.Seats.Count == 0
                ? "<none>"
                : string.Join("; ", t.Seats.Select(s => $"doodad {s.DoodadObjId}/ap{s.AttachPoint}/{s.BondKind}"));
            sb.AppendLine($"| {t.TlId} | {t.ObjId} | {t.Name} | ({t.X:F0},{t.Y:F0}) | {seats} |");
        }

        sb.AppendLine();

        // Structural diagnosis when seats exist but sit on a TlId-shadowed entry.
        var shadowed = transfers.Where(t => t.Seats.Count > 0).Any(t =>
            !ReferenceEquals(transfers.FirstOrDefault(x => x.TlId == t.TlId), t));
        if (shadowed)
        {
            sb.AppendLine("## Diagnosis hint: TlId shadowing");
            sb.AppendLine();
            sb.AppendLine("At least one seat-carrying transfer entry does NOT win the " +
                          "GetTransfers().FirstOrDefault(TlId == tl) resolve: both parts of a bound carriage " +
                          "(master motor + boarding part) SHARE the master's TlId, the master is registered FIRST " +
                          "(TransferManager.Create), and the seat benches attach to the CHILD part — so the " +
                          "handler looks for seats on the seatless master and silently returns. Single-part " +
                          "transfers would bond; the live spawn table appears to have none.");
            sb.AppendLine();
        }

        if (boardingLines.Count > 0)
        {
            sb.AppendLine("## Handler refusals observed during the run (first 50)");
            foreach (var line in boardingLines.Take(50))
                sb.AppendLine($"    {line}");
            sb.AppendLine();
        }

        sb.AppendLine("## Raw bridge 'transfers' response");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(dumpJson);
        sb.AppendLine("```");

        var path = Path.Combine(EvidenceDir, "transfer-ride-e2e-BLOCKER.md");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
