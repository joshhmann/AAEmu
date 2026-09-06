using System.Reflection;
using System.Text;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;
using AAEmu.Game.Models.Game.Units.Movements;
using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Q6 — PB-002 bounded interzone loop LIVE proof (existing loop, no new content).
/// Two TCP actors on one live lane stack (M2b precedent: bridge `drive` ops
/// resolve the bot through its live connection's ActiveChar, so the quest bot
/// itself is a real TCP session — auto-created account, character through the
/// real create handler):
///  - quest bot: Solzreed discovery (data-driven, IDs recorded) → highway
///    traverse on its OWN raw CSMoveUnitPacket frames (TransferRide precedent)
///    while the observer captures the broadcasts → Dewstone chain 44/328/48/55
///    with reward deltas → 328 straddles a mid-route clean restart (M2b
///    reconnect-same-character pattern, no dup/loss).
///  - observer: second TCP session parked near the hunt ground, owning frames
///    for the wire tap (TransferRide ownership pattern).
/// H stays UNKNOWN. Rulings: no new bridge verbs; clean restart only;
/// charState asserts present fields only (money/HP/MP/death absent — gaps).
/// </summary>
[Collection("e2e")]
public class InterzoneLoopE2eTests
{
    private const string BotName = "Q6LoopBot";
    private const string BotAccount = "q6loopbot";
    private const string ChainBotName = "Q6ChainBot";
    private const string ChainAccount = "q6chainbot";
    private const string ObserverBot = "Q6Observer";
    private const string ObserverAccount = "q6observer";
    private const string Password = "e2e-secret";
    // Canonical prerequisite lattice (unit_reqs CompleteQuestContext):
    // 44 (level only) -> 328 (needs 44) -> 918 (level 15) -> 71 (needs 918)
    // -> 48 (needs 71) -> 55 (needs 48). Quests 918/71 are the contract's
    // hidden prerequisites (run-14/15 evidence).
    private static readonly uint[] ChainQuests = [44, 328, 918, 71, 48, 55];


    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");
    private static string ReportPath => Path.Combine(EvidenceDir, "q6-interzone-loop-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task InterzoneLoop_OnLiveServer_CompletesDewstoneChainEndToEnd()
    {
        E2eStack.EnsureUp();
        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        var evidence = new StringBuilder();
        evidence.AppendLine($"# Q6 interzone loop live proof — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        var legs = new List<(string Leg, bool Passed, string Detail)>();
        var reported = false;
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg}: {detail}");
        }
        var bridge = new BotDriveClient(E2eStack.BridgePort);
        BotNetworkSession? session = null;
        BotNetworkSession? chainSession = null;
        BotNetworkSession? observer = null;
        using var pingCts = new CancellationTokenSource();
        try
        {
            // ---- quest bot: real TCP enter-world (bridge resolves via connection) ----
            E2eStack.CleanupBotRows(BotAccount, ObserverAccount);
            session = await ConnectBotAsync(BotName, BotAccount);
            var botObjId = (uint)CharState(bridge, BotName).GetProperty("objId").GetUInt32();
            Leg("enter-world", session.InWorld && botObjId != 0,
                $"inWorld={session.InWorld} objId={botObjId}");
            Assert.True(session.InWorld && botObjId != 0, "quest bot must enter the live world");

            // ---- observer TCP session + frame ownership ----
            observer = await ConnectBotAsync(ObserverBot, ObserverAccount);
            var observerLink = GetGameLink(observer);
            StopBackgroundLoops(observer);
            _ = Task.Run(() => PingLoopAsync(observerLink, pingCts.Token));
            var questLink = GetGameLink(session);
            var wireTap = new WireTap(observerLink, botObjId);
            using var pumpCts = new CancellationTokenSource();
            var pumpTask = Task.Run(async () =>
            {
                while (!pumpCts.IsCancellationRequested)
                {
                    try { wireTap.Pump(); } catch { }
                    await Task.Delay(500, pumpCts.Token).ContinueWith(_ => { });
                }
            });

            // ---- leg 1: Solzreed discovery (data-driven, IDs recorded) ----
            var solzreed = DiscoverSolzreed(bridge, out var observedIds, evidence, wireTap);
            Leg("solzreed-exhaust", solzreed,
                $"observed quest IDs: [{string.Join(",", observedIds)}]");
            var level = BridgeLevel(bridge);
            Leg("l10-gate", level >= 10, $"level={level}");
            Assert.True(solzreed && level >= 10, "Solzreed must exhaust into the L10 gate");

            // ---- leg 2: highway traverse on the quest bot's OWN wire frames ----
            var (moved, moveDetail) = await HighwayTraverseOnWire(bridge, session, evidence);
            wireTap.Pump();
            var (wireFrames, wireDetail) = wireTap.Stop();
            pumpCts.Cancel();
            try { await pumpTask; } catch { }
            Leg("highway-traverse", moved, moveDetail);
            Leg("wire-locomotion", wireFrames > 0,
                $"SCOneUnitMovement broadcasts for objId {botObjId}: {wireFrames} ({wireDetail})");
            Assert.True(moved, "wire traverse must show position progress: " + moveDetail);

            // ---- legs 3-7: Dewstone chain on a FRESH chain bot ----
            // Rationale (disclosed handoff): the discovery marathon fills bot
            // 1's bag with dozens of distinct reward templates, and quest 44's
            // SUPPLY stage needs free slots (run-9 evidence). A live player
            // would vendor; no bridge verb discards, so the chain runs on a
            // fresh character — same account family, empty bag, same engine.
            E2eStack.CleanupBotRows(ChainAccount);
            chainSession = await ConnectBotAsync(ChainBotName, ChainAccount);
            var chainOk = true;
            chainOk &= DriveChainQuest(bridge, ChainBotName, 44, evidence);
            var manifest328 = LoadManifest(328);
            var prepared = E2eQuestDriver.PrepareQuest(bridge, ChainBotName, manifest328, manifest328.Level);
            var invBefore = InvSnapshot(bridge, ChainBotName, manifest328);
            evidence.AppendLine($"  328 prepared={prepared} invBefore=[{string.Join(",", invBefore)}]");
            chainOk &= prepared;
            Assert.True(chainOk, "44 + 328-prepare must hold before restart:\n" + evidence);

            // Restart kills all sockets (M2b pattern): save, dispose, restart,
            // fresh bridge, reconnect SAME accounts, assert same character.
            // Bot 1 (discovery/wire) is done — only the chain bot returns.
            // Deterministic persistence point (M3b/B4 seam): the bridge save
            // runs the REAL SaveManager.DoSave pass synchronously, so the
            // prepared 328 step-state is on disk before the process dies.
            var questCharId = chainSession.CharacterId;
            bridge.Call("{\"cmd\":\"save\"}", 180_000);
            evidence.AppendLine("  pre-restart save forced (SaveManager.DoSave via bridge)");
            session.Dispose();
            session = null;
            chainSession.Dispose();
            chainSession = null;
            observer.Dispose();
            observer = null;
            pingCts.Cancel();
            bridge.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(30));
            bridge = new BotDriveClient(E2eStack.BridgePort);
            chainSession = await ConnectBotAsync(ChainBotName, ChainAccount);
            Leg("reconnect-same-character", chainSession.CharacterId == questCharId,
                $"charId before={questCharId} after={chainSession.CharacterId}");
            Assert.True(chainSession.CharacterId == questCharId, "restart must preserve the character");
            observer = await ConnectBotAsync(ObserverBot, ObserverAccount);

            var resumed = E2eQuestDriver.ResumePreparedQuest(bridge, ChainBotName, manifest328);
            var invAfter = InvSnapshot(bridge, ChainBotName, manifest328);
            var stable = invAfter.Count >= invBefore.Count
                && invBefore.Where((v, i) => invAfter[i] == v).Count() == invBefore.Count;
            var restartOk = resumed.Passed && stable;
            evidence.AppendLine($"  328 resumed={resumed.Passed} invStable={stable}");
            Leg("restart-resume", restartOk, $"resumed={resumed.Passed} invStable={stable}");
            chainOk &= restartOk;
            chainOk &= DriveChainQuest(bridge, ChainBotName, 918, evidence);
            chainOk &= DriveChainQuest(bridge, ChainBotName, 71, evidence);
            chainOk &= DriveChainQuest(bridge, ChainBotName, 48, evidence);
            chainOk &= DriveChainQuest(bridge, ChainBotName, 55, evidence);
            Assert.True(chainOk, "Dewstone chain + restart-resume must hold:\n" + evidence);

            // ---- leg 8: death/recovery watch ----
            var (deathSeen, deathDetail) = await DeathRecoveryLeg(bridge, ChainBotName, chainSession, observer, evidence);
            Leg("death-recovery", deathSeen, deathDetail);

            // ---- log-tail scan (spike convention) ----
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            Leg("log-tail", unhandled == 0 && fatals == 0,
                $"{unhandled} unhandled + {fatals} fatal(s)");

            var allOk = legs.All(l => l.Passed);
            await WriteReportAsync(allOk, legs, evidence.ToString());
            reported = true;
            Assert.True(allOk, "Q6 interzone loop FAIL:\n" + evidence + $"\nReport: {ReportPath}");
        }
        finally
        {
            pingCts.Cancel();
            session?.Dispose();
            chainSession?.Dispose();
            observer?.Dispose();
            bridge.Dispose();
            // The pass-path above already wrote the verdict; only record a
            // failure here when the run never reached it (exception mid-leg).
            if (!reported)
                await WriteReportAsync(false, legs, evidence.ToString());
        }
    }
    private static async Task<BotNetworkSession> ConnectBotAsync(string bot, string account)
    {
        var session = await BotNetworkSession.ConnectAsync(
            bot, account, Password,
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        if (!session.InWorld)
        {
            session.Dispose();
            throw new InvalidOperationException($"{bot}: real login flow did not reach in-world");
        }
        return session;
    }

    // Discovery-driven leveling: t1..t15 manifest tiers in order; within a
    // tier, passes until two consecutive quiet passes. Stops when the live
    // level reaches 10 (the gate). Quest IDs are RECORDED, never prescribed.
    private static bool DiscoverSolzreed(BotDriveClient bridge, out List<uint> observed, StringBuilder evidence, WireTap tap)
    {
        observed = [];
        for (var tier = 1; tier <= 15; tier++)
        {
            var dir = ManifestDir($"t{tier}");
            if (!Directory.Exists(dir)) continue;
            var skipped = 0;
            var tierFiles = new List<E2eQuestManifest>();
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                if (TryLoadManifest(f, out var manifest) && manifest is not null)
                    tierFiles.Add(manifest);
                else
                    skipped++;
            }
            var files = tierFiles.OrderBy(m => m.Level).ThenBy(m => m.QuestId).ToList();
            if (skipped > 0) evidence.AppendLine($"  tier t{tier}: skipped {skipped} acceptor-less manifest(s)");
            var passesWithoutOffer = 0;
            for (var pass = 0; pass < 3 && passesWithoutOffer < 2; pass++)
            {
                var offeredThisPass = 0;
                foreach (var manifest in files)
                {
                    if (observed.Contains(manifest.QuestId)) continue;
                    if (E2eQuestDriver.HasCompleted(bridge, BotName, manifest.QuestId)) continue;
                    try
                    {
                        var accept = bridge.Call(
                            $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"accept\",\"quest\":{manifest.QuestId},\"acceptor\":\"{manifest.AcceptorType}\",\"acceptorId\":{manifest.AcceptorId}}}",
                            timeoutMs: 60_000);
                        if (!accept.GetProperty("accepted").GetBoolean()) continue;
                        observed.Add(manifest.QuestId);
                        offeredThisPass++;
                        if (observed.Count <= 60)
                            evidence.AppendLine($"  offered+driving {manifest.QuestId} ({manifest.Name})");
                        E2eQuestDriver.DriveQuest(bridge, BotName, manifest, BridgeLevel(bridge));
                    }
                    catch (Exception ex)
                    {
                        // Live-state refusal mid-drive (e.g. bag-full stock):
                        // record + skip, keep discovering. A gate stuck below
                        // L10 fails honestly at the assert, with data.
                        evidence.AppendLine($"  skipped {manifest.QuestId} ({manifest.Name}): {ex.GetType().Name}");
                        continue;
                    }
                    finally { tap.Pump(); }
                }
                passesWithoutOffer = offeredThisPass == 0 ? passesWithoutOffer + 1 : 0;
                if (BridgeLevel(bridge) >= 10) break;
            }
            if (BridgeLevel(bridge) >= 10) break;
        }
        return observed.Count > 0 && BridgeLevel(bridge) >= 10;
    }

    private static void TeleportToAcceptor(BotDriveClient bridge, string bot, E2eQuestManifest manifest)
    {
        if (!string.Equals(manifest.AcceptorType, "Npc", StringComparison.OrdinalIgnoreCase))
            return;
        bridge.Call(
            $"{{\"cmd\":\"drive\",\"bot\":\"{bot}\",\"op\":\"teleportToNpc\",\"npc\":{manifest.AcceptorId}}}",
            timeoutMs: 60_000);
        // Spawn-radius world: poll until the acceptor NPC actually spawns
        // (mirrors the driver's own ReportNpc poll — a fixed sleep races
        // the spawn tick).
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            var objId = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{bot}\",\"op\":\"npcObjId\",\"npc\":{manifest.AcceptorId}}}",
                timeoutMs: 30_000).GetProperty("objId").GetUInt32();
            if (objId != 0) return;
            Thread.Sleep(1000);
        }
    }

    private static bool DriveChainQuest(BotDriveClient bridge, string bot, uint questId, StringBuilder evidence)
    {
        var manifest = LoadManifest(questId);
        TeleportToAcceptor(bridge, bot, manifest);
        var before = RewardSnapshot(bridge, bot, manifest);
        var result = E2eQuestDriver.DriveQuest(bridge, bot, manifest, manifest.Level);
        if (!result.Passed && manifest.Stages.Count > 1 && result.FailStage == manifest.Stages[0].Name)
        {
            // Live timing transient: the server settled the first stage ahead
            // of the calibrated read (right step, status ahead — e.g. supply
            // auto-completed between accept and the START poll). Resume from
            // the next stage instead of failing a healthy quest.
            var first = result.Stages.FirstOrDefault(s => s.Stage == result.FailStage);
            if (first is not null && manifest.Stages[0].ExpectStep == first.StepObserved)
            {
                evidence.AppendLine($"  quest-{questId}: fast-forwarding from {manifest.Stages[1].Name} (settled ahead: {first.StatusObserved})");
                result = E2eQuestDriver.ResumeFromStage(bridge, bot, manifest, manifest.Stages[1].Name, result.Stages);
            }
        }
        var completed = E2eQuestDriver.HasCompleted(bridge, bot, questId);
        var after = RewardSnapshot(bridge, bot, manifest);
        var delta = after.Zip(before, (a, b) => a - b).ToList();
        // Reward deltas apply where the manifest/template declares item
        // rewards (44 selective, 48/918 supply). 71/55 declare none
        // (XP/money-only — unassertable: charState carries neither, recorded
        // bridge gap), so completion alone is their evidence.
        var rewarded = !delta.Any() || delta.Any(d => d > 0);
        evidence.AppendLine($"  quest-{questId}: completed={completed} rewardDelta=[{string.Join(",", delta)}] drivePassed={result.Passed} failStage={result.FailStage} failReason={result.FailReason}");
        if (!result.Passed) evidence.AppendLine("  drive trace: " + result.ReproTrace());
        return completed && rewarded;
    }

    // Wire tap: counts SCOneUnitMovementPacket broadcasts for one objId by
    // draining the observer's owned game link (TransferRide frame pattern).
    // Combat extension: SCCombatEngaged (0x85, Bc id), SCUnitDamaged (0xa7,
    // counted ambient — layout too deep for cheap parse), SCUnitPoints
    // (0xba: Bc id + precise HP/MP) give live HP telemetry for the tap's
    // objId — engagement evidence and death corroboration on the wire.
    private sealed class WireTap(BotTcpLink link, uint objId)
    {
        private int _frames;
        private int _combatFrames;
        private int _minHp = int.MaxValue;
        private string _detail = "no frames yet";
        public (int Frames, string Detail) Stop() => (_frames, _detail);
        public int CombatFrames => _combatFrames;
        public int MinHp => _minHp;

        public void Pump()
        {
            foreach (var (type, body) in link.DrainAll())
            {
                try
                {
                    var stream = new PacketStream(body);
                    if (type == SCOffsets.SCOneUnitMovementPacket)
                    {
                        if (stream.ReadBc() == objId) _frames++;
                    }
                    else if (type == SCOffsets.SCCombatEngagedPacket)
                    {
                        if (stream.ReadBc() == objId) _combatFrames++;
                    }
                    else if (type == SCOffsets.SCUnitDamagedPacket)
                    {
                        _combatFrames++;
                    }
                    else if (type == SCOffsets.SCUnitPointsPacket)
                    {
                        if (stream.ReadBc() == objId)
                        {
                            var hp = stream.ReadInt32();
                            if (hp < _minHp) _minHp = hp;
                        }
                    }
                }
                catch { /* torn frame under load — skip */ }
            }
            _detail = $"{_frames} movement broadcast(s) captured";
        }
    }

    // Highway traverse on the quest bot's OWN raw move frames (sender = own
    // character — legitimate client movement, TransferRide precedent).
    // Positioning at the chain-44 acceptor corridor is disclosed setup, not
    // traversal proof; the measured segment is pure locomotion. Saddle-mount
    // (mate) stays a recorded gap: no live mate exists for this bot.
    private static async Task<(bool, string)> HighwayTraverseOnWire(
        BotDriveClient bridge, BotNetworkSession session, StringBuilder evidence)
    {
        var manifest44 = LoadManifest(44);
        bridge.Call(
            $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{manifest44.AcceptorId}}}",
            timeoutMs: 60_000);
        // Co-location is disclosed setup (not traversal proof): the observer
        // must share the corridor's broadcast region to witness locomotion.
        // Bridge ops resolve networked characters by name (no provision needed).
        bridge.Call(
            $"{{\"cmd\":\"drive\",\"bot\":\"{ObserverBot}\",\"op\":\"teleportToNpc\",\"npc\":{manifest44.AcceptorId}}}",
            timeoutMs: 60_000);
        var start = CharPos(bridge, BotName);
        var link = GetGameLink(session);
        var objId = (uint)CharState(bridge, BotName).GetProperty("objId").GetUInt32();
        const int frames = 60;
        for (var i = 0; i < frames; i++)
        {
            var step = (i + 1) * 2f;
            var move = VehicleMovementModel.BuildCharacterMove(
                new System.Numerics.Vector3(start.X + step, start.Y, start.Z), 0f, 5f);
            link.SendGameFrame(CSOffsets.CSMoveUnitPacket, 1, body =>
            {
                body.WriteBc(objId);
                body.Write((byte)MoveTypeEnum.Unit);
                move.Write(body);
            });
            await Task.Delay(100);
        }
        var end = CharPos(bridge, BotName);
        var dist = MathF.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));
        evidence.AppendLine($"  traverse {frames} wire frames: ({start.X:0.#},{start.Y:0.#}) -> ({end.X:0.#},{end.Y:0.#}) = {dist:0.#}m");
        return (dist > 5f, $"displacement={dist:0.#}m over {frames} frames");
    }


    // Death/recovery by VERIFIED-retaliating mob (canonical-data setup, live
    // combat): engagement-gated candidate pool — teleport + aggro per mob,
    // require wire combat evidence (engaged/damaged broadcasts, HP telemetry)
    // within 2 min, abort + re-pick fast on passive mobs. The chosen mob then
    // gets the full death watch (shrine jump or HP-zero on the wire) and the
    // genuine client rez + recovery moves. Recovery floors (70% MaxHp/Mp)
    // are UNASSERTABLE: charState carries no HP/MP — recorded gap.
    private static readonly uint[] DeathProbeCandidates = [14312, 13737, 13517, 13451, 12756];
    private static async Task<(bool, string)> DeathRecoveryLeg(
        BotDriveClient bridge, string bot, BotNetworkSession session, BotNetworkSession observerSession, StringBuilder evidence)
    {
        var botObjId = (uint)CharState(bridge, bot).GetProperty("objId").GetUInt32();
        var observerLink = GetGameLink(observerSession);
        StopBackgroundLoops(observerSession);
        using var pingCts = new CancellationTokenSource();
        _ = Task.Run(() => PingLoopAsync(observerLink, pingCts.Token));
        var tap = new WireTap(observerLink, botObjId);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                try { tap.Pump(); } catch { }
                await Task.Delay(500, pumpCts.Token).ContinueWith(_ => { });
            }
        });
        try
        {
            // ---- engagement gate: first candidate with wire combat evidence wins ----
            uint? engaged = null;
            foreach (var npcId in DeathProbeCandidates)
            {
                var combatBefore = tap.CombatFrames;
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{bot}\",\"op\":\"teleportToNpc\",\"npc\":{npcId}}}", timeoutMs: 60_000);
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{ObserverBot}\",\"op\":\"teleportToNpc\",\"npc\":{npcId}}}", timeoutMs: 60_000);
                // Pre-weaken through the genuine rez path (no IsDead guard:
                // packet-rez on a live bot resets HP to 10%, the documented
                // live-client restore level). Fully disclosed acceleration.
                GetGameLink(session).SendGameFrame(CSOffsets.CSResurrectCharacterPacket, 1, body => body.Write(false));
                await Task.Delay(TimeSpan.FromSeconds(5));
                bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{bot}\",\"op\":\"aggro\",\"npc\":{npcId}}}", timeoutMs: 60_000);
                var gateDeadline = Environment.TickCount64 + 120_000;
                while (Environment.TickCount64 < gateDeadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    if (tap.CombatFrames > combatBefore) break;
                }
                var gained = tap.CombatFrames - combatBefore;
                evidence.AppendLine($"  death probe candidate {npcId}: combatFrames+={gained} minHp={tap.MinHp}");
                if (gained > 0) { engaged = npcId; break; }
            }
            if (engaged is null)
                return (false, "UNOBSERVED — no candidate retaliated in 2 min each (pool exhausted; trail in evidence)");
            // ---- death watch on the engaged mob ----
            evidence.AppendLine($"  death probe engaged mob {engaged}, watching 25 min for a shrine jump or HP-zero");
            var before = CharPos(bridge, bot);
            Pos? shrine = null;
            var deadline = Environment.TickCount64 + 1_500_000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var pos = CharPos(bridge, bot);
                var jump = MathF.Sqrt((pos.X - before.X) * (pos.X - before.X) + (pos.Y - before.Y) * (pos.Y - before.Y));
                if (jump > 300f) { shrine = pos; break; }
                if (tap.MinHp == 0) { shrine = CharPos(bridge, bot); break; }
            }
            if (shrine is null)
            {
                var last = CharPos(bridge, bot);
                var lateJump = MathF.Sqrt((last.X - before.X) * (last.X - before.X) + (last.Y - before.Y) * (last.Y - before.Y));
                evidence.AppendLine($"  death watch timed out; final displacement={lateJump:0.#}m minHp={tap.MinHp}");
                if (lateJump < 300f && tap.MinHp != 0)
                    return (false, "UNOBSERVED — engaged mob did not finish the kill in 25 min (trail in evidence)");
                shrine = last;
            }
            evidence.AppendLine($"  death observed: shrine=({shrine.X:0.#},{shrine.Y:0.#},{shrine.Z:0.#})");
            // Recovery is MANUAL on the live path: rez through the genuine
            // client packet, then move on own wire frames (connection
            // persists across death). 10% packet-path restore vs the card's
            // 70% bot-path floors — recorded distinction.
            var link = GetGameLink(session);
            var objId = (uint)CharState(bridge, bot).GetProperty("objId").GetUInt32();
            link.SendGameFrame(CSOffsets.CSResurrectCharacterPacket, 1, body => body.Write(false));
            await Task.Delay(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 10; i++)
            {
                var move = VehicleMovementModel.BuildCharacterMove(
                    new System.Numerics.Vector3(shrine.X + (i + 1) * 2f, shrine.Y, shrine.Z), 0f, 5f);
                link.SendGameFrame(CSOffsets.CSMoveUnitPacket, 1, body =>
                {
                    body.WriteBc(objId);
                    body.Write((byte)MoveTypeEnum.Unit);
                    move.Write(body);
                });
                await Task.Delay(100);
            }
            var end = CharPos(bridge, bot);
            var dist = MathF.Sqrt((end.X - shrine.X) * (end.X - shrine.X) + (end.Y - shrine.Y) * (end.Y - shrine.Y));
            var journalKept = E2eQuestDriver.HasCompleted(bridge, bot, 55);
            evidence.AppendLine($"  recovery: post-shrine displacement={dist:0.#}m journal55kept={journalKept}");
            return (dist > 2f && journalKept,
                $"mob={engaged} shrine=({shrine.X:0.#},{shrine.Y:0.#}) postMove={dist:0.#}m journalKept={journalKept} (70% floors unassertable — bridge gap)");
        }
        finally
        {
            pumpCts.Cancel();
            try { await pumpTask; } catch { }
        }
    }

    private static int BridgeLevel(BotDriveClient bridge)
        => CharState(bridge, BotName).GetProperty("level").GetInt32();

    private static JsonElement CharState(BotDriveClient bridge, string bot)
        => bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{bot}\",\"op\":\"charState\"}}", timeoutMs: 30_000);
    private sealed record Pos(float X, float Y, float Z);
    private static Pos CharPos(BotDriveClient bridge, string bot)
    {
        var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{bot}\",\"op\":\"charPos\"}}", timeoutMs: 30_000);
        return new Pos(
            el.GetProperty("x").GetSingle(),
            el.GetProperty("y").GetSingle(),
            el.GetProperty("z").GetSingle());
    }

    private static List<int> RewardSnapshot(BotDriveClient bridge, string bot, E2eQuestManifest manifest)
    {
        // Declared expects plus template selective-reward candidates (44's
        // REWARD stage declares no ExpectRewardItems; the selective items
        // live in the manifest template Reward acts).
        var ids = manifest.Stages.SelectMany(s => s.ExpectRewardItems).Select(r => r.ItemId).ToList();
        ids.AddRange(SelectiveRewardIds(manifest.QuestId));
        return ids.Distinct().Select(id => E2eQuestDriver.InvCount(bridge, bot, id)).ToList();
    }

    private static List<uint> SelectiveRewardIds(uint questId)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(ManifestDir("t7"), $"{questId}.json")));
            var ids = new List<uint>();
            if (!doc.RootElement.TryGetProperty("template", out var template))
                return ids;
            if (!template.TryGetProperty("components", out var components))
                return ids;
            foreach (var component in components.EnumerateArray())
            {
                if (component.TryGetProperty("kind", out var kind) && kind.GetString() != "Reward")
                    continue;
                if (!component.TryGetProperty("acts", out var acts)) continue;
                foreach (var act in acts.EnumerateArray())
                {
                    if (act.TryGetProperty("itemId", out var itemId))
                        ids.Add(itemId.GetUInt32());
                }
            }
            return ids;
        }
        catch { return []; }
    }

    private static List<int> InvSnapshot(BotDriveClient bridge, string bot, E2eQuestManifest manifest)
        => RewardSnapshot(bridge, bot, manifest);

    private static E2eQuestManifest LoadManifest(uint questId)
    {
        foreach (var tier in new[] { "t7", "t1" })
        {
            var path = Path.Combine(ManifestDir(tier), $"{questId}.json");
            if (File.Exists(path)) return E2eQuestManifest.LoadFromFile(path);
        }
        throw new FileNotFoundException($"no committed manifest for quest {questId}");
    }

    private static string ManifestDir(string tier)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", tier);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"manifest dir missing: {path}");
        return path;
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

    private static async Task PingLoopAsync(BotTcpLink link, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5_000, ct);
                if (!link.Connected) break;
                link.SendGameFrame(PPOffsets.PingPacket, 2, body =>
                {
                    body.Write(0L);
                    body.Write(0L);
                    body.Write(0u);
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private static int CountLogTailMatches(long startOffset, string marker)
    {
        var path = GameLogPath;
        if (!File.Exists(path)) return 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var count = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.Contains(marker, StringComparison.Ordinal)) count++;
        return count;
    }
    private static bool TryLoadManifest(string path, out E2eQuestManifest? manifest)
    {
        try
        {
            manifest = E2eQuestManifest.LoadFromFile(path);
            return true;
        }
        catch (Exception)
        {
            manifest = null;
            return false;
        }
    }

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail)> legs, string evidence)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = "q6-interzone-loop",
            milestone = "Q6 PB-002 bounded interzone loop",
            verdict = passed ? "PASS" : "FAIL",
            legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail }).ToList(),
            note = "H UNKNOWN (no feel verdict). Gaps: saddle-mount (no live mate); charState money/HP/MP/death fields; death leg UNOBSERVED v1; kill-9 follow-up.",
            evidence
        };
        await File.WriteAllTextAsync(ReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
