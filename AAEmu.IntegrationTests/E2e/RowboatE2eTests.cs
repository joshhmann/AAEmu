using System.Globalization;
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
/// SHIPS-01 Slice 1 — ROWBOAT live-stack E2E (scorecard-explorations/mechanics/ships-domain.md §9):
/// answers the owner question "are boats working?" with real wire evidence.
///
///   SUMMON   the ACTUAL player-facing path, no GM commands: bot stocks summon
///            scroll item 15817 ('나룻배 소환 주문서', item_summon_slaves →
///            slave_id 15) through the bridge stock op (real bag insert), then
///            casts its use skill 15802 as a REAL CSStartSkillPacket with
///            SkillCasterType.Item + the scroll's instance id:
///              Skill.Use → effect 29581 → SpecialEffect 10822
///              (SpecialEffectType.SpawnSlave = 60) → SpawnSlave.Execute →
///              SlaveManager.Create(owner, skillData) → SummonSlaveTemplate
///              slave 15 '나룻배' (model 129). The boat branch places the hull
///              at water level and scans up to 50 m+LOA in front for depth ≥
///              MassBoxSizeZ - MassCenterZ + 1 (SlaveManager.cs:419-462).
///   POSITION open-sea placement through teleportToNpc 12640 ('먼바다 눈알
///            괴물' — the UNIQUE npc_spawns entry at z=0.61, i.e. IN the ocean).
///   BIND     CSBindSlavePacket(tlId) → SlaveManager.BindSlave binds Driver,
///            broadcasts SCUnitAttachedPacket + SCSlaveBoundPacket.
///   HELM     CSMoveUnitPacket ShipRequestMoveType (MoveTypeEnum.ShipRequest=4)
///            {throttle, steering} sbytes → handler sets ThrottleRequest/
///            SteeringRequest; physics thread smooths them into Throttle/
///            Steering ONLY while a driver is attached (PhysicsManager
///            BoatPhysicsTick) and broadcasts authoritative SCOneUnitMovement-
///            Packet(Ship) frames. Asserts: stream flows FOR THE SLAVE,
///            displacement beyond threshold in a consistent heading, speed
///            sane vs ship_models id 17 (mass 800, velocity 8.0), and yaw-rate
///            SIGN FLIPS when steering input flips.
///   UNBIND   CSDiscardSlavePacket(tlId) → UnbindSlave → SCUnitDetachedPacket.
///   DESPAWN  CSDespawnSlavePacket(slaveObjId) → TryDespawnOwnedSlave → Delete
///            → SCSlaveDespawnPacket + SCSlaveRemovedPacket; PhysicsManager
///            RemoveShip must run (game.log Debug lines via AAEMU_E2E_LOG_
///            LEVEL=Debug) and NO further Ship movement frames may flow for
///            the despawned objId — no leaked physics body.
///
/// Honest-failure contract: any stage that fails writes a blocker report with
/// stage attribution (BOT-SIDE vs SERVER vs DATA) under $E2E_ROOT/logs instead
/// of forcing a pass.
/// </summary>
[Collection("e2e")]
public class RowboatE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names (InvalidCharacters).
    private const string BotName = "RowBoater";
    private const string AccountName = "e2erowboater";

    private const uint SummonScrollItemId = 15817u; // items.use_skill_id = 15802, item_summon_slaves.slave_id = 15
    private const uint SummonSkillId = 15802u;      // '공간의 문을 여는 중...' cast 5s → SpawnSlave special effect
    private const uint RowboatSlaveTemplateId = 15; // slaves.id 15 '나룻배', model_id 129, kind Boat
    private const float RowboatModelVelocity = 8.0f; // ship_models row for model 129 chain: velocity 8.0, mass 800

    /// <summary>'굶주린 가루다' — npc_spawns template whose FIRST registry spawner
    /// sits at open-sea level (3090.0, 29778.0, z≈0.05; both spawns in water).
    /// teleportToNpc resolves FirstOrDefault(UnitId), so the first-entry position is what we get.</summary>
    private const uint SeaNpcTemplateId = 13763u;

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    private sealed record SlaveSpawn(ushort TlId, uint ObjId);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Summon_Bind_HelmSteer_Despawn_Rowboat_OnLiveServer_EndToEnd()
    {
        var stages = new List<(string Stage, bool Ok, string Detail)>();
        void Record(string stage, bool ok, string detail)
        {
            stages.Add((stage, ok, detail));
            Console.WriteLine($"[rowboat] {(ok ? "PASS" : "FAIL")} {stage}: {detail}");
        }

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

        // Own the wire from here on (SCSlaveCreated / movement / bound evidence).
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

            // ------------------------------------------------- STAGE 1a STOCK
            // Real bag insert through the engine's AcquireDefaultItem path.
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"stock\",\"item\":{SummonScrollItemId},\"count\":1}}");
            bridge.Call("{\"cmd\":\"save\"}", 180_000);
            var scrollInstanceId = QuerySummonScrollInstanceId(bot.CharacterId);
            Record("STOCK-SCROLL", scrollInstanceId > 0,
                $"summon scroll item {SummonScrollItemId} stocked; persisted instance id {scrollInstanceId}");

            // ------------------------------------------------ STAGE 1b SEASIDE
            // Open-ocean placement (unique sea-monster spawner at z≈0.61), then
            // sample the ACTUAL landed position through the persistence path.
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{SeaNpcTemplateId}}}");
            var landed = ForceSaveAndSample(bridge, bot.CharacterId);
            var atSeaLevel = landed.Z is < 5f and > -5f;
            Record("POSITION-OPEN-SEA", atSeaLevel,
                $"bot teleported to open-sea spawner {SeaNpcTemplateId}, landed ({landed.X:F1},{landed.Y:F1},{landed.Z:F2}) — " +
                (atSeaLevel ? "sea-level Z confirms in-water placement" : "Z not near sea level — boat spawn depth scan may fail"));

            // -------------------------------------------------- STAGE 2 SUMMON
            // The exact player-facing use-item cast: CSStartSkillPacket with a
            // SkillItem caster carrying the SCROLL INSTANCE id (server resolves
            // SkillSourceItem from it and requires skill == template.UseSkillId).
            _ = link.DrainAll();
            link.SendGameFrame(CSOffsets.CSStartSkillPacket, 1, body =>
            {
                body.Write(SummonSkillId);
                body.Write((byte)2);                    // SkillCasterType.Item
                WriteBc(body, charObjId);               // SkillCaster.ObjId
                body.Write(scrollInstanceId);           // SkillItem.ItemId (u64)
                body.Write(SummonScrollItemId);         // SkillItem.ItemTemplateId
                body.Write((byte)0);                    // SkillItem.Type1
                body.Write(0u);                         // SkillItem.Type2
                body.Write((byte)1);                    // SkillCastTargetType.Position
                body.Write(Helpers.ConvertLongX(landed.X));
                body.Write(Helpers.ConvertLongY(landed.Y));
                body.Write(landed.Z);
                body.Write(0f);                         // PosRot
                WriteBc(body, 0u);
                WriteBc(body, 0u);
                body.Write((byte)0);                    // flag: SkillObjectType.None
            });

            var createdBody = WaitForFrame(link, SCOffsets.SCSlaveCreatedPacket, 30_000);
            if (createdBody == null)
            {
                var logTail = ReadLogTail(logOffset, "StartSkill");
                var blocker = WriteBlockerReport(stages, logOffset,
                    $"SUMMON: CSStartSkillPacket(skill {SummonSkillId}, SkillItem item {scrollInstanceId}) produced NO " +
                    $"SCSlaveCreatedPacket within 30s (5s cast + margin). StartSkill log lines appended: " +
                    $"{logTail.Count}. Layer attribution: SERVER (skill/effect/SlaveManager chain) unless the " +
                    $"item instance failed to resolve BOT-SIDE.");
                Assert.Fail($"Rowboat never spawned. Blocker report: {blocker}");
            }

            var spawn = ParseSlaveCreated(createdBody);
            Record("SUMMON-SCSLAVE-CREATED", spawn.ObjId != 0 && spawn.TlId != 0,
                $"SCSlaveCreatedPacket: owner(bc)={ReadBc(createdBody, 0)} tl={spawn.TlId} slaveObj={spawn.ObjId} " +
                $"creator='{ReadString(createdBody, 17)}'");

            // Water-spawn proof: the first authoritative Ship frame for THIS
            // slave must sit at water level, displaced in front of the bot
            // (the boat branch clamps the in-front offset to ≥5 m and scans
            // for depth ≥ minDepth).
            var firstShipFrame = WaitForShipFrame(link, spawn.ObjId, 20_000);
            if (firstShipFrame == null)
            {
                var blocker = WriteBlockerReport(stages, logOffset,
                    $"SUMMON-PHYSICS: slave {spawn.ObjId} (tl {spawn.TlId}) created but NO SCOneUnitMovementPacket" +
                    "(Ship) frame arrived within 20s — the Jitter2 physics thread never replicated the hull. " +
                    "Layer attribution: SERVER (physics thread init / AddShip).");
                Assert.Fail($"No ship physics stream. Blocker report: {blocker}");
            }

            var spawnPos = DecodeShipFrame(firstShipFrame);
            var atWaterLevel = MathF.Abs(spawnPos.Z) < 3f;
            var spawnDisplacement = Dist((spawnPos.X, spawnPos.Y, spawnPos.Z), (landed.X, landed.Y, landed.Z));
            Record("SUMMON-WATER-SPAWN", atWaterLevel,
                $"first Ship frame for slave {spawn.ObjId}: pos ({spawnPos.X:F1},{spawnPos.Y:F1},{spawnPos.Z:F2}) " +
                $"{spawnDisplacement:F1} m from bot, Z≈water level: {atWaterLevel}");
            var addShipLines = CountLogTailMatches(logOffset, "AddShip ");
            Record("PHYSICS-ADDSHIP", addShipLines > 0,
                $"game.log appended {addShipLines} 'AddShip' line(s) — hull registered with the Jitter2 world");

            // ----------------------------------------------------- STAGE 3 BIND
            link.SendGameFrame(CSOffsets.CSBindSlavePacket, 1, body => body.Write(spawn.TlId));

            var attached = WaitForFrame(link, SCOffsets.SCUnitAttachedPacket, 10_000);
            var bound = WaitForFrame(link, SCOffsets.SCSlaveBoundPacket, 10_000);
            if (attached == null || bound == null)
            {
                var blocker = WriteBlockerReport(stages, logOffset,
                    $"BIND: CSBindSlavePacket(tl={spawn.TlId}) produced " +
                    $"SCUnitAttachedPacket={(attached != null)} SCSlaveBoundPacket={(bound != null)} within 10s. " +
                    "Layer attribution: SERVER (BindSlave gates: dead-slave / OwnersMark ownership).");
                Assert.Fail($"Driver bind not confirmed on the wire. Blocker report: {blocker}");
            }

            var attachUnit = ReadBc(attached, 0);
            var attachPoint = attached[3];
            var attachSlave = ReadBc(attached, 4);
            var boundMaster = BitConverter.ToUInt32(bound, 0);
            var boundSlave = ReadBc(bound, 4);
            var bindOk = attachUnit == charObjId && attachPoint == 1 /* Driver */ &&
                         attachSlave == spawn.ObjId &&
                         boundMaster == bot.CharacterId && boundSlave == spawn.ObjId;
            Record("BIND-DRIVER", bindOk,
                $"SCUnitAttached(unit={attachUnit} point={attachPoint} slave={attachSlave}), " +
                $"SCSlaveBound(master charId={boundMaster} slave={boundSlave})");

            // ------------------------------------------ STAGE 4a HELM FORWARD
            var forward = await SailWindow(link, spawn.ObjId, throttle: 100, steering: 0, seconds: 15);
            var fwdDist = Dist(forward.LastPos, forward.FirstPos);
            var fwdSpeedMax = forward.MaxSpeed;
            var speedSane = forward.MaxSpeed <= RowboatModelVelocity * 1.5f; // 8.0 cap × wind mul ≤ +15% + slack
            var movedEnough = fwdDist > 6f;
            Record("HELM-FORWARD", forward.FrameCount >= 20 && movedEnough,
                $"throttle=100 over {forward.Duration:F1}s: {forward.FrameCount} Ship frames, displacement {fwdDist:F1} m " +
                $"({forward.FirstPos.X:F0},{forward.FirstPos.Y:F0})→({forward.LastPos.X:F0},{forward.LastPos.Y:F0}), " +
                $"maxSpeed {fwdSpeedMax:F2} m/s (model cap ~{RowboatModelVelocity} m/s)");

            // ------------------------------- STAGE 4b STEER REVERSAL SIGN TEST
            var steerPort = await SailWindow(link, spawn.ObjId, throttle: 80, steering: 100, seconds: 9);
            var steerStbd = await SailWindow(link, spawn.ObjId, throttle: 80, steering: -100, seconds: 9);
            var portRate = HeadingRateDegPerSec(steerPort);
            var stbdRate = HeadingRateDegPerSec(steerStbd);
            var signFlip = portRate * stbdRate < 0 && MathF.Abs(portRate) > 0.5f && MathF.Abs(stbdRate) > 0.5f;
            Record("HELM-STEER-SIGN-FLIP", signFlip,
                $"steering +100 → yaw rate {portRate:F2}°/s, steering −100 → {stbdRate:F2}°/s " +
                "(Boat-class max ≈ 4.35°/s per ShipController table)");

            var sailOk = forward.FrameCount >= 20 && movedEnough && speedSane && signFlip;
            if (!sailOk)
            {
                var why = !movedEnough
                    ? $"displacement only {fwdDist:F1} m in {forward.Duration:F0}s of full throttle"
                    : !signFlip
                        ? $"yaw rates did not flip sign (+100→{portRate:F2}°/s, −100→{stbdRate:F2}°/s)"
                        : $"unphysical speed {fwdSpeedMax:F2} m/s";
                var physErrors = CountLogTailMatches(logOffset, "PhysicsThread Error");
                var blocker = WriteBlockerReport(stages, logOffset,
                    $"HELM: {why}. FrameCount forward={forward.FrameCount}, maxSpeed={fwdSpeedMax:F2}. " +
                    $"PhysicsThread error lines in game.log tail: {physErrors}. Layer attribution: " +
                    (physErrors > 0 ? "SERVER (physics exceptions)" :
                     forward.FrameCount < 20 ? "SERVER (no/broken replication stream)" :
                     "DATA or SERVER (ship tuning / input smoothing)"));
                Assert.Fail($"Helm control failed: {why}. Blocker report: {blocker}");
            }

            // --------------------------------------------------- STAGE 5 UNBIND
            _ = link.DrainAll();
            link.SendGameFrame(CSOffsets.CSDiscardSlavePacket, 1, body => body.Write(spawn.TlId));
            var detached = WaitForFrame(link, SCOffsets.SCUnitDetachedPacket, 10_000);
            var detachUnit = detached != null ? ReadBc(detached, 0) : 0u;
            Record("UNBIND", detached != null && detachUnit == charObjId,
                detached != null
                    ? $"SCUnitDetachedPacket(unit={detachUnit}, reason={detached[^1]})"
                    : "NO SCUnitDetachedPacket within 10s");

            // -------------------------------------------------- STAGE 6 DESPAWN
            link.SendGameFrame(CSOffsets.CSDespawnSlavePacket, 1, body => WriteBc(body, spawn.ObjId));
            var despawned = WaitForFrame(link, SCOffsets.SCSlaveDespawnPacket, 10_000);
            var removed = WaitForFrame(link, SCOffsets.SCSlaveRemovedPacket, 10_000);
            var despawnOk = despawned != null && ReadBc(despawned, 0) == spawn.ObjId;
            Record("DESPAWN-WIRE", despawnOk,
                despawned != null
                    ? $"SCSlaveDespawnPacket(obj={ReadBc(despawned, 0)}), SCSlaveRemovedPacket present: {(removed != null)}"
                    : "NO SCSlaveDespawnPacket within 10s (owner/range/combat gate refused?)");

            // Leak asserts: no trailing Ship frames for the dead objId, and the
            // physics registry released the hull (RemoveShip Debug line).
            await Task.Delay(6_000);
            var leakedFrames = CountShipFrames(link.DrainAll(), spawn.ObjId);
            var removeShipLines = CountLogTailMatches(logOffset, "RemoveShip ");
            var leakFree = leakedFrames == 0 && removeShipLines > 0;
            Record("NO-LEAKED-PHYSICS-BODY", leakFree,
                $"{leakedFrames} Ship frames flowed for despawned obj {spawn.ObjId} after a 6s quiet window; " +
                $"'RemoveShip' game.log lines: {removeShipLines} (AddShip was {addShipLines})");

            // No unhandled server errors during the whole run.
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            var physicsErrors = CountLogTailMatches(logOffset, "PhysicsThread Error");
            Record("LOG-HYGIENE", unhandled == 0 && fatals == 0 && physicsErrors == 0,
                $"game-log tail: {unhandled} unhandled, {fatals} fatal, {physicsErrors} physics-thread error(s)");

            var allOk = stages.All(s => s.Ok);
            var reportPath = WriteReport(charObjId, spawn, landed, spawnPos, forward, steerPort, steerStbd,
                stages, addShipLines, removeShipLines);
            Console.WriteLine($"[rowboat] VERDICT {(allOk ? "PASS" : "PARTIAL/FAIL")} — report: {reportPath}");

            Assert.True(allOk,
                "rowboat E2E had failing stages: " +
                string.Join("; ", stages.Where(s => !s.Ok).Select(s => $"{s.Stage}: {s.Detail}")));
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

    // ------------------------------------------------------------- stages

    private sealed record ShipSample(float X, float Y, float Z, short RotZ, sbyte Steering, sbyte Throttle);

    private sealed record SailRun(
        int FrameCount, float Duration, ShipSample FirstPos, ShipSample LastPos,
        float MaxSpeed, IReadOnlyList<ShipSample> Samples);

    /// <summary>
    /// Sends ShipRequest(throttle, steering) once per second for
    /// <paramref name="seconds"/> while collecting the slave's Ship stream.
    /// </summary>
    private static async Task<SailRun> SailWindow(BotTcpLink link, uint slaveObjId, sbyte throttle, sbyte steering, int seconds)
    {
        var samples = new List<ShipSample>();
        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(seconds);

        while (DateTime.UtcNow < deadline)
        {
            SendShipRequest(link, slaveObjId, throttle, steering);
            await Task.Delay(250);
            CollectShipSamples(link.DrainAll(), slaveObjId, samples);
        }

        CollectShipSamples(link.DrainAll(), slaveObjId, samples);

        if (samples.Count == 0)
            return new SailRun(0, (float)(DateTime.UtcNow - start).TotalSeconds,
                new ShipSample(0, 0, 0, 0, 0, 0), new ShipSample(0, 0, 0, 0, 0, 0), 0, samples);

        var maxSpeed = 0f;
        for (var i = 1; i < samples.Count; i++)
        {
            var dt = 0.25f; // sampling cadence floor — speeds are sanity-checked, not integrated
            var d = Dist(samples[i], samples[i - 1]);
            var v = d / dt;
            if (v > maxSpeed && d < 10f) // ignore teleport-scale jumps (packet bursts after GC pauses)
                maxSpeed = v;
        }

        return new SailRun(samples.Count, (float)(DateTime.UtcNow - start).TotalSeconds,
            samples[0], samples[^1], maxSpeed, samples);
    }

    private static void SendShipRequest(BotTcpLink link, uint slaveObjId, sbyte throttle, sbyte steering)
    {
        link.SendGameFrame(CSOffsets.CSMoveUnitPacket, 1, body =>
        {
            WriteBc(body, slaveObjId);
            body.Write((byte)4);                 // MoveTypeEnum.ShipRequest
            body.Write((uint)Environment.TickCount); // Time
            body.Write((byte)0x02);              // Flags: Moving
            body.Write(throttle);
            body.Write(steering);
        });
    }

    private static void CollectShipSamples(List<(ushort Type, byte[] Body)> frames, uint slaveObjId, List<ShipSample> into)
    {
        foreach (var frame in frames)
        {
            if (frame.Type != SCOffsets.SCOneUnitMovementPacket || frame.Body.Length < 44)
                continue;
            if (frame.Body[3] != 3) // MoveTypeEnum.Ship
                continue;
            if (ReadBc(frame.Body, 0) != slaveObjId)
                continue;
            into.Add(DecodeShipFrame(frame.Body));
        }
    }

    private static int CountShipFrames(List<(ushort Type, byte[] Body)> frames, uint slaveObjId)
    {
        var n = 0;
        foreach (var frame in frames)
        {
            if (frame.Type != SCOffsets.SCOneUnitMovementPacket || frame.Body.Length < 10)
                continue;
            if (frame.Body[3] != 3)
                continue;
            if (ReadBc(frame.Body, 0) == slaveObjId)
                n++;
        }

        return n;
    }

    /// <summary>bc(obj) type(u8) Time(u32) Flags(u8) Pos(9b) Vel(i16×3) Rot(i16×3).</summary>
    private static ShipSample DecodeShipFrame(byte[] body)
    {
        var pos = Helpers.ConvertPosition(body[9..18]);
        var rotZ = (short)(body[28] | (body[29] << 8)); // rotation block starts at 24; Z is third short
        var steering = (sbyte)body[42];
        var throttle = (sbyte)body[43];
        return new ShipSample(pos.x, pos.y, pos.z, rotZ, steering, throttle);
    }

    private static byte[] WaitForShipFrame(BotTcpLink link, uint slaveObjId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type != SCOffsets.SCOneUnitMovementPacket || frame.Body.Length < 18)
                    continue;
                if (frame.Body[3] != 3)
                    continue;
                if (ReadBc(frame.Body, 0) == slaveObjId)
                    return frame.Body;
            }

            Thread.Sleep(25);
        }

        return null;
    }

    /// <summary>
    /// Signed heading change (deg) across a window, from the chord bearings of
    /// its first and second halves, normalized to ±180° — divided by window
    /// duration. The SIGN CONTRAST between a +steer and −steer window is the
    /// steer-reversal evidence; the magnitude sanity-checks against the
    /// Boat-class yaw table (~4.35°/s max).
    /// </summary>
    private static float HeadingRateDegPerSec(SailRun run)
    {
        var s = run.Samples;
        if (s.Count < 4)
            return 0f;

        static (float dx, float dy) Chord(IReadOnlyList<ShipSample> list, int i0, int i1)
            => (list[i1].X - list[i0].X, list[i1].Y - list[i0].Y);

        var half = s.Count / 2;
        var (ax, ay) = Chord(s, 0, half);
        var (bx, by) = Chord(s, half, s.Count - 1);
        if (MathF.Abs(ax) + MathF.Abs(ay) < 0.5f || MathF.Abs(bx) + MathF.Abs(by) < 0.5f)
            return 0f; // not enough translation for bearing evidence

        var b1 = MathF.Atan2(ay, ax) * 180f / MathF.PI;
        var b2 = MathF.Atan2(by, bx) * 180f / MathF.PI;
        var delta = b2 - b1;
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        return delta / MathF.Max(run.Duration, 1f);
    }

    // --------------------------------------------------------------- frames

    private static SlaveSpawn ParseSlaveCreated(byte[] body)
        => new(ReadU16(body, 3), ReadBc(body, 5));

    /// <summary>body = bc(owner) u16 tlId bc(slaveObjId) bool i64 unk string creator.</summary>
    private static string ReadString(byte[] body, int offset)
    {
        if (offset + 2 > body.Length)
            return "";
        var len = ReadU16(body, offset);
        if (len == 0 || offset + 2 + len > body.Length)
            return "";
        return Encoding.UTF8.GetString(body, offset + 2, len);
    }

    private static ushort ReadU16(byte[] body, int offset)
        => (ushort)(body[offset] | (body[offset + 1] << 8));

    private static byte[] WaitForFrame(BotTcpLink link, ushort type, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type != type)
                    continue;
                return frame.Body;
            }

            Thread.Sleep(25);
        }

        return null;
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

    // ---------------------------------------------------------- persistence

    private static ulong QuerySummonScrollInstanceId(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM items WHERE owner = @owner AND template_id = @tpl ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@owner", characterId);
        cmd.Parameters.AddWithValue("@tpl", SummonScrollItemId);
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? 0ul : Convert.ToUInt64(result);
    }

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

    // -------------------------------------------------------------- helpers

    private static float Dist(ShipSample a, ShipSample b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
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

    private static List<string> ReadLogTail(long startOffset, string marker)
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
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    lines.Add(line);
        }
        catch (IOException)
        {
        }

        return lines;
    }

    // -------------------------------------------------------------- reports


    private static string WriteReport(uint charObjId, SlaveSpawn spawn,
        (float X, float Y, float Z) landed, ShipSample firstShip,
        SailRun forward, SailRun? steerPort, SailRun? steerStbd,
        List<(string Stage, bool Ok, string Detail)> stages,
        int addShipLines, int removeShipLines)
    {
        var report = new
        {
            scenario = "rowboat-e2e",
            milestone = "SHIPS-01 slice 1 (live stack)",
            verdict = stages != null && stages.All(s => s.Ok) ? "PASS" : "FAIL/PARTIAL",
            bot = BotName,
            account = AccountName,
            characterObjId = charObjId,
            summonItem = new { id = SummonScrollItemId, useSkill = SummonSkillId, slaveTemplate = RowboatSlaveTemplateId },
            summonPosition = new { landed.X, landed.Y, landed.Z },
            slave = new { spawn?.TlId, spawn?.ObjId },
            firstShipFrame = firstShip == null ? null : (object)new { firstShip.X, firstShip.Y, firstShip.Z },
            forwardRun = forward == null ? null : (object)new
            {
                frames = forward.FrameCount,
                durationS = forward.Duration,
                first = new { forward.FirstPos.X, forward.FirstPos.Y },
                last = new { forward.LastPos.X, forward.LastPos.Y },
                displacementM = Dist(forward.LastPos, forward.FirstPos),
                maxSpeedMps = forward.MaxSpeed,
                shipModelCapMps = RowboatModelVelocity
            },
            steerPortRun = steerPort == null ? null : (object)new { frames = steerPort.FrameCount, durationS = steerPort.Duration },
            steerStbdRun = steerStbd == null ? null : (object)new { frames = steerStbd.FrameCount, durationS = steerStbd.Duration },
            physicsRegistry = new { addShipLines, removeShipLines },
            stages = stages?.Select(s => new { stage = s.Stage, ok = s.Ok, detail = s.Detail })
        };

        var path = Path.Combine(EvidenceDir, "rowboat-e2e-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string WriteBlockerReport(List<(string Stage, bool Ok, string Detail)> stages,
        long logOffset, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ROWBOAT live-stack E2E — BLOCKER");
        sb.AppendLine();
        sb.AppendLine($"- date: {DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- stack: {E2eStack.E2eRoot} (login :{E2eStack.LoginPort}, game :{E2eStack.GamePort}, bridge :{E2eStack.BridgePort})");
        sb.AppendLine($"- reason: {reason}");
        sb.AppendLine();
        sb.AppendLine("## Stages executed before the failure");
        sb.AppendLine();
        foreach (var (stage, ok, detail) in stages)
            sb.AppendLine($"- **{(ok ? "PASS" : "FAIL")}** {stage}: {detail}");
        sb.AppendLine();
        sb.AppendLine("## Layer attribution discipline");
        sb.AppendLine();
        sb.AppendLine("- BOT-SIDE: packet shape/sequence mistakes by this driver (would show as decode warnings, no handler side effects)");
        sb.AppendLine("- SERVER: handler/manager/physics defects (packets accepted but expected G2C evidence missing)");
        sb.AppendLine("- DATA: missing/mis-tuned rows (items, slaves, ship_models, spawns)");
        sb.AppendLine();

        var startLines = ReadLogTail(logOffset, "StartSkill");
        if (startLines.Count > 0)
        {
            sb.AppendLine("## game.log 'StartSkill' lines appended during the run (first 20)");
            sb.AppendLine();
            foreach (var line in startLines.Take(20))
                sb.AppendLine($"    {line}");
            sb.AppendLine();
        }

        var physErrors = ReadLogTail(logOffset, "PhysicsThread Error");
        if (physErrors.Count > 0)
        {
            sb.AppendLine("## PhysicsThread errors (first 10)");
            sb.AppendLine();
            foreach (var line in physErrors.Take(10))
                sb.AppendLine($"    {line}");
            sb.AppendLine();
        }

        var path = Path.Combine(EvidenceDir, "rowboat-e2e-BLOCKER.md");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
