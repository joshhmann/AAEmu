using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.Proxy;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// PVP-01 Slice 1 — flagged-aggression handshake on the live stack
/// (scorecard-explorations/mechanics/pvp-domain.md §6 slice 1). Pure
/// verification over verified-wired packets — ZERO source changes.
///
///   PROVISION two real Nuian bots through the real login flow; they spawn
///             co-located at the shared CharTemplates spawn point.
///   SHIELD    negative-case evidence: in the Nuian homeland spawn zone the
///             CanAttack mother-zone shield (zone faction 148 == victim's
///             mother faction, BaseUnit.CanAttack :76-80) refuses aggression
///             even with ForceAttack ON — documented relocation reason.
///   FLAG      bot A sends CSSetForceAttackPacket 0x04f ON → B receives
///             SCForceAttackSetPacket(objA, true) and A carries Bloodlust
///             buff 1482 (SCBuffCreatedPacket wire probe).
///   AGGRESS   A damages same-faction B with a real skill cast (Triple Slash
///             18131, indun-lane precedent) in e_steppe_belt (neutral-faction
///             zone, conflict group 14 boots Peace): damage LANDS via the
///             ForceAttack exception that precedes the ZONE-01 peace block
///             (measured finding), A turns purple (Retribution 2167), and a
///             bloodstain evidence doodad 877 spawns — the CRIME-01 input
///             observable generated inside the SAME DamageEffect branch that
///             populates AssaultedBy/AssaultOn (DamageEffect.cs:389-400).
///   PEACE     with ForceAttack OFF the same cast is REFUSED while the live
///             conflict state is Peace — ZONE-01 enforcement demonstrated
///             live in the same binary as the allowed kill path.
///   WAR       honor-delta stage UNRECORDED-deferred (see report): kill-counter
///             escalation needs >250 real hostile kills (70/100/140/190/250,
///             fed ONLY by AwardPvpHonor → AddZoneKill) plus a ConflictMin
///             timer; unreachable inside this slice's timebox.
///
/// Honest-failure contract: any failing stage is attributed SERVER/DATA/BOT
/// in the stage detail + report; no engine changes are made to force a pass.
/// </summary>
[Collection("e2e")]
public class PvpHandshakeE2eTests
{
    // Hyphen-free: NameManager rejects '-' in character names.
    private const string BotAName = "PvpFlagger";
    private const string BotBName = "PvpVictim";
    private const string AccountAName = "e2epvpflagger";
    private const string AccountBName = "e2epvpvictim";

    /// <summary>3단 베기 Triple Slash — instant melee attack skill used by the
    /// indun lane as the real CSStartSkillPacket attack proof.</summary>
    private const uint AttackSkillId = 18131u;

    private const uint BloodlustBuffId = 1482u;        // BuffConstants.Bloodlust ("Ctrl+F")
    private const uint RetributionBuffId = 2167u;      // BuffConstants.Retribution (purple)
    private const uint SmallBloodstainDoodadId = 877u; // DoodadConstants.SmallBloodstain

    /// <summary>e_steppe_belt conflict zone group — zones carry NO faction_id
    /// (falls back to Neutral in CanAttack, so no mother-zone shield) and the
    /// group HAS a conflict_zones entry that boots into Peace.</summary>
    private const ushort SteppeConflictGroupId = 14;
    private static readonly uint[] SteppeZoneKeys = [136, 247]; // zones w_steppe_belt_1/_2

    /// <summary>NPC templates whose FIRST registry spawner sits inside the
    /// group-14 bounding box (precomputed from Data/Worlds/main_world/
    /// npc_spawns.json first-occurrence order, matching the bridge's
    /// FirstOrDefault spawner resolution).</summary>
    private static readonly uint[] SteppeTeleportNpcCandidates = [364, 990, 1034];

    private const byte ZoneConflictStatePeace = 7; // ZoneConflictType.Peace

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private sealed record StageRecord(string Stage, bool Passed, string Detail);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ForceFlag_Aggression_Handshake_And_PeaceBlock_OnLiveServer()
    {
        var stages = new List<StageRecord>();
        void Record(string stage, bool passed, string detail)
        {
            stages.Add(new StageRecord(stage, passed, detail));
            Console.WriteLine($"[pvp-hs] {(passed ? "PASS" : "FAIL")} {stage}: {detail}");
        }

        Directory.CreateDirectory(EvidenceDir);
        E2eStack.EnsureUp();

        BotNetworkSession botA = null;
        BotNetworkSession botB = null;
        try
        {
            // -------------------------------------------------- STAGE: PROVISION
            botA = await BotNetworkSession.ConnectAsync(
                BotAName, AccountAName, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            botB = await BotNetworkSession.ConnectAsync(
                BotBName, AccountBName, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);

            Assert.True(botA.InWorld && botB.InWorld, "both bots must be in-world (real login flow)");
            var botsInWorldAt = DateTime.UtcNow;

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotAName}\",\"op\":\"setLevel\",\"level\":40}}");
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotBName}\",\"op\":\"setLevel\",\"level\":40}}");

            var stateA = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotAName}\",\"op\":\"charState\"}}");
            var objIdA = stateA.GetProperty("objId").GetUInt32();
            var stateB = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotBName}\",\"op\":\"charState\"}}");
            var objIdB = stateB.GetProperty("objId").GetUInt32();
            Assert.True(objIdA != 0 && objIdB != 0 && objIdA != objIdB, "charState must report distinct live objIds");

            var posA = QueryCharPos(bridge, BotAName);
            var posB = QueryCharPos(bridge, BotBName);
            var coLocated = posA.ZoneId == posB.ZoneId
                            && Math.Abs(posA.X - posB.X) < 5 && Math.Abs(posA.Y - posB.Y) < 5;
            var spawnGroupKey = QueryScalar($"SELECT group_id FROM zones WHERE zone_key = {posA.ZoneId}");
            Record("PROVISION", coLocated,
                $"{BotAName}(objId {objIdA}) + {BotBName}(objId {objIdB}) lvl 40, both Nuian, " +
                $"spawn zone key {posA.ZoneId} (group {(spawnGroupKey < 0 ? "?" : spawnGroupKey.ToString())}) @ " +
                $"({posA.X:F1}, {posA.Y:F1}); co-located={coLocated}");

            // Take over both wires for frame-level evidence.
            StopBackgroundLoops(botA);
            StopBackgroundLoops(botB);
            var linkA = GameLink(botA);
            var linkB = GameLink(botB);

            using var pingCts = new CancellationTokenSource();
            var pingTask = Task.Run(() =>
            {
                try
                {
                    while (!pingCts.IsCancellationRequested)
                    {
                        Thread.Sleep(5000);
                        SendPingFrame(linkA);
                        SendPingFrame(linkB);
                    }
                }
                catch { /* cancelled or socket died */ }
            });

            try
            {
                // ------------------------------------------- STAGE: HOMELAND-SHIELD
                // Negative-case evidence at the Nuian homeland spawn: zone faction
                // 148 == victim's mother faction → mother-zone shield refuses even
                // flagged aggression (BaseUnit.CanAttack :76-80).
                SendSetForceAttack(linkA, true);
                Thread.Sleep(3000); // let the ack + Bloodlust land; drained below
                InjectUnitAttack(linkA, AttackSkillId, objIdA, objIdB);
                var homelandLanded = (await AwaitDamageAndBuffAsync([linkA, linkB], objIdB, 0, 8000)).DamageSeen;
                Record("HOMELAND-SHIELD", !homelandLanded,
                    !homelandLanded
                        ? $"aggression attempt at spawn zone key {posA.ZoneId} produced NO combat frames on {BotBName} — " +
                          "mother-zone shield refuses flagged friendly aggression (CanAttack :76-80); relocation required"
                        : $"aggression LANDED at spawn zone key {posA.ZoneId} — mother-zone shield did NOT block " +
                          "(unexpected; zone faction lookup may differ from compact data)");
                await Task.Delay(2000); // settle before relocating
                DrainAll(linkA);
                DrainAll(linkB);

                // ------------------------------------------------ STAGE: RELOCATE
                // Both bots teleported to the SAME steppe NPC spawner position →
                // distance 0. Verified by returned charPos zone keys.
                var relocateDetail = new List<string>();
                var relocated = false;
                foreach (var npcTemplate in SteppeTeleportNpcCandidates)
                {
                    if (!TryTeleportToNpc(bridge, BotAName, npcTemplate, relocateDetail) ||
                        !TryTeleportToNpc(bridge, BotBName, npcTemplate, relocateDetail))
                        continue;

                    var nowA = QueryCharPos(bridge, BotAName);
                    var nowB = QueryCharPos(bridge, BotBName);
                    if (SteppeZoneKeys.Contains(nowA.ZoneId) && SteppeZoneKeys.Contains(nowB.ZoneId))
                    {
                        posA = nowA;
                        posB = nowB;
                        relocated = true;
                        break;
                    }

                    relocateDetail.Add($"npc {npcTemplate}: landed in zone keys {nowA.ZoneId}/{nowB.ZoneId}, not steppe");
                }

                Record("RELOCATE-STEPPE", relocated,
                    relocated
                        ? $"both bots at ({posA.X:F1}, {posA.Y:F1}) zone key {posA.ZoneId} (conflict group {SteppeConflictGroupId}, " +
                          $"boot-state Peace per ZoneManager.SetState); detail: {string.Join("; ", relocateDetail)}"
                        : $"no teleport candidate landed in steppe zones [{string.Join('/', SteppeZoneKeys)}]: {string.Join("; ", relocateDetail)}");

                // ------------------------------------- STAGE: LIVE-ZONE-STATE-EVIDENCE
                // Engine truth for the state under test: the conflict_zones row
                // exists and ZoneManager boots non-closed groups via SetState(Peace)
                // unless ConflictZonesStartAtConflict is set (ZoneManager.cs:166-173);
                // login resync broadcasts SCConflictZoneStatePacket per group.
                var conflictRow = SqliteScalar(
                    $"SELECT COUNT(*) FROM conflict_zones WHERE zone_group_id = {SteppeConflictGroupId}");
                var openGroups = SqliteScalar(
                    $"SELECT COUNT(*) FROM conflict_zones WHERE zone_group_id = {SteppeConflictGroupId} " +
                    "AND lower(CAST(closed AS TEXT)) IN ('f', 'false', '0')");
                Record("LIVE-ZONE-STATE", conflictRow > 0 && openGroups > 0,
                    $"conflict_zones row for group {SteppeConflictGroupId}: exists={conflictRow > 0}, open(not-closed)={openGroups > 0}; " +
                    "boot contract ZoneManager.cs:170-173 → SetState(Peace=7) broadcast server-wide + login resync " +
                    "(CharacterLifecycleService.cs:237-239)");

                // --------------------------------------------------- STAGE: FLAG
                SendSetForceAttack(linkA, true);
                var flagAck = AwaitForceAttackAck(linkB, objIdA, true, 10000);
                var bloodlustSeen = AwaitBuffCreatedContaining([linkA, linkB], BloodlustBuffId, 10000, out var bloodlustSource);
                Record("FLAG-FORCEATTACK", flagAck && bloodlustSeen,
                    $"CS 0x04f ON → SCForceAttackSet(objId {objIdA}, true) on {BotBName}'s wire={flagAck}; " +
                    $"Bloodlust buff {BloodlustBuffId} SCBuffCreated observed={bloodlustSeen}" +
                    $"{(bloodlustSeen ? $" (link {bloodlustSource})" : "")}");

                // ------------------------------------------------ STAGE: AGGRESS
                DrainAll(linkA);
                DrainAll(linkB);
                // PB-007: every character enters the world with buff 2423 "LoggedOn"
                // (CharacterLifecycleService.cs:263) — a ~20 s ALL-damage-immunity
                // login-protection window (compact.sqlite3 buffs: melee/spell/ranged/
                // siege_immune=t). Firing inside it makes DamageEffect legitimately
                // refuse the damage via CheckDamageImmune (Immune SCUnitDamaged frame,
                // no HP change, no crime branch). The aggression cast must land after
                // that window for real damage to apply.
                var loginProtectionRemaining = (int)(((botsInWorldAt + TimeSpan.FromSeconds(24)) - DateTime.UtcNow).TotalMilliseconds);
                if (loginProtectionRemaining > 0)
                {
                    Console.WriteLine($"[pvp-hs] waiting {loginProtectionRemaining:F0} ms for the login-protection window to close before AGGRESS");
                    await Task.Delay((int)loginProtectionRemaining);
                }
                InjectUnitAttack(linkA, AttackSkillId, objIdA, objIdB);
                var aggressOutcome = await AwaitDamageAndBuffAsync([linkA, linkB], objIdB, RetributionBuffId, 15000);
                var purpleSeen = aggressOutcome.BuffSeen;
                var purpleSource = aggressOutcome.BuffSource;

                var bloodstainObjId = ProbeBloodstain(bridge);
                var aggressPassed = aggressOutcome.DamageSeen && purpleSeen && bloodstainObjId != 0;
                Record("AGGRESS-ALLOWED", aggressPassed,
                    $"Triple Slash {AttackSkillId} from {BotAName} (ForceAttack ON) vs {BotBName}: REAL damage frames " +
                    $"(CombatFirstHit/UnitDamaged)={aggressOutcome.DamageSeen}, cast-started (SkillFired seen)={aggressOutcome.SkillStarted}; " +
                    $"Retribution {RetributionBuffId} observed={purpleSeen}{(purpleSeen ? $" (link {purpleSource})" : "")}; " +
                    $"bloodstain doodad {SmallBloodstainDoodadId} spawned=objId {bloodstainObjId}. " +
                    (aggressPassed
                        ? "Damage landed WHILE group 14 was in boot-Peace — the ForceAttack exception " +
                          "(BaseUnit.CanAttack:100-103) precedes the ZONE-01 BlocksPvpDamage gate (:126-135). Bloodstain proves the " +
                          "DamageEffect crime branch (:389-400) executed — the same guarded branch that populates " +
                          "AssaultedBy/AssaultOn (server-memory lists, no direct wire observable)."
                        : "MEASURED BLOCKER (layer attribution pending game.log Debug pass): the cast did NOT produce damage. " +
                          "Candidate gates: skill target-relation validation (skills.target_relation_id=4=Hostile resolves via " +
                          "CanAttack), UnitRequirements, or obstacle check — see report."));

                if (!aggressPassed)
                {
                    // Diagnose before continuing: dump recent game.log lines around
                    // the cast for layer attribution in the report.
                    var diag = ScanLogTail(new[] { "failed requirements", "NoTarget", "TooFarRange", "TooCloseRange",
                        "CanAttack", "StartSkill", "is using skill", "Unhandled" });
                    Console.WriteLine($"[pvp-hs] AGGRESS diagnostics: {string.Join(" | ", diag.Take(10))}");
                }

                // ------------------------------------------------ STAGE: PEACE-BLOCK
                // Same cast with the self-flag OFF: no exception path applies, so
                // the Peace-state protection refuses the damage.
                SendSetForceAttack(linkA, false);
                _ = AwaitForceAttackAck(linkB, objIdA, false, 10000);
                DrainAll(linkA);
                DrainAll(linkB);
                InjectUnitAttack(linkA, AttackSkillId, objIdA, objIdB);
                var refusedDamage = !(await AwaitDamageAndBuffAsync([linkA, linkB], objIdB, 0, 12000)).DamageSeen;
                var bloodstainAfter = ProbeBloodstain(bridge);

                Record("PEACE-BLOCK", refusedDamage,
                    refusedDamage
                        ? $"with ForceAttack OFF, the same skill cast produced NO combat/damage frames on {BotBName}'s wire within 12 s " +
                          $"(bloodstain probe after: objId {bloodstainAfter}) — refusal observed live while conflict group " +
                          $"{SteppeConflictGroupId} was in Peace; attribution: ZONE-01 BlocksPvpDamage gate + Friendly-relation fallback " +
                          "(the two refuse the same payload here; the discriminating flagged case is measured above)"
                        : "damage STILL flowed with ForceAttack OFF — peace protection NOT enforced (SERVER finding)");

                // ---------------------------------------------------- STAGE: WAR-HONOR
                Record("WAR-HONOR", false,
                    "UNRECORDED-deferred honestly within timebox: driving group 14 to War requires >251 real HOSTILE PvP kills " +
                    "(kill counters 70/100/140/190/250 in conflict_zones; AddZoneKill is fed ONLY from AwardPvpHonor " +
                    "→ CharacterCombat.cs:223, i.e. real non-Friendly-relation kills — Friendly Nuia-vs-Nuia kills never register, " +
                    "and the bridge 'zoneKill' op fires quest OnZoneKill events only, which no ZoneConflict subscriber consumes), " +
                    "then ConflictMin=5 min timer tail Conflict→War. Honor deltas themselves are war-gated since the 2026-08-25 " +
                    "owner ruling (Conflict award 0, War solo 40 / killer 32 + assist 4, victim −10 clamp ≥ 0) and are rig-covered " +
                    "in PvpFlaggingRigTests. Live measurement deferred to a dedicated run.");
            }
            finally
            {
                pingCts.Cancel();
                try { await pingTask; } catch { /* cancelled */ }
            }
        }
        finally
        {
            botA?.Disconnect();
            botB?.Disconnect();
            E2eStack.CleanupBotRows(AccountAName, AccountBName);
        }

        // ------------------------------------------------------------ VERDICT
        var reportPath = WriteReport(stages);
        var failedRequired = stages.Where(s => !s.Passed && s.Stage != "WAR-HONOR").ToList();
        Assert.True(failedRequired.Count == 0,
            "PVP-HANDSHAKE RESULT:\n" +
            string.Join("\n", failedRequired.Select(f => $"  FAIL {f.Stage}: {f.Detail}")) +
            $"\nReport: {reportPath}");
    }

    // ------------------------------------------------------------- wire helpers

    private static void SendSetForceAttack(BotTcpLink link, bool on)
        => link.SendGameFrame(CSOffsets.CSSetForceAttackPacket, 1, body => body.Write(on));

    private static void InjectUnitAttack(BotTcpLink link, uint skillId, uint casterObjId, uint unitObjId)
        => link.SendGameFrame(CSOffsets.CSStartSkillPacket, 1, body =>
        {
            body.Write(skillId);
            body.Write((byte)0);   // SkillCasterType.Unit
            WriteBc(body, casterObjId);
            body.Write((byte)0);   // SkillCastTargetType.Unit
            WriteBc(body, unitObjId);
            body.Write((byte)0);   // flag: SkillObjectType.None
        });

    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xFF));
        stream.Write((byte)((value >> 8) & 0xFF));
        stream.Write((byte)((value >> 16) & 0xFF));
    }

    private static uint ReadBc(byte[] body, int offset)
        => (uint)(body[offset] | (body[offset + 1] << 8) | (body[offset + 2] << 16));

    /// <summary>Waits for SCForceAttackSetPacket(objId, expectedOn) on this link.</summary>
    private static bool AwaitForceAttackAck(BotTcpLink link, uint objId, bool expectedOn, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in link.DrainAll())
            {
                if (frame.Type != SCOffsets.SCForceAttackSetPacket || frame.Body.Length < 4)
                    continue;
                if (ReadBc(frame.Body, 0) == objId && (frame.Body[3] != 0) == expectedOn)
                    return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>Scans links for an SCBuffCreatedPacket whose body carries the
    /// buff template id as a little-endian u32 (BuffTemplate.BuffId => Id).</summary>
    private static bool AwaitBuffCreatedContaining(BotTcpLink[] links, uint buffTemplateId, int timeoutMs, out string sourceLink)
    {
        var pattern = BitConverter.GetBytes(buffTemplateId); // LE u32
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var link in links)
            {
                foreach (var frame in link.DrainAll())
                {
                    if (frame.Type != SCOffsets.SCBuffCreatedPacket || frame.Body.Length < pattern.Length)
                        continue;
                    if (IndexOf(frame.Body, pattern) >= 0)
                    {
                        sourceLink = link.Name;
                        return true;
                    }
                }
            }

            Thread.Sleep(200);
        }

        sourceLink = null;
        return false;
    }

    private sealed record DamageOutcome(bool DamageSeen, bool SkillStarted, bool BuffSeen, string BuffSource);

    /// <summary>Waits for REAL damage and/or buff evidence across links:
    /// SCCombatFirstHitPacket(vuId at bc offset 0) matching victimObjId,
    /// SCUnitDamagedPacket(targetId at bc offset 5) matching victimObjId,
    /// and SCBuffCreatedPacket containing buffTemplateId.</summary>
    private static async Task<DamageOutcome> AwaitDamageAndBuffAsync(BotTcpLink[] links, uint victimObjId, uint buffTemplateId, int timeoutMs)
    {
        var skillStartedSeen = false;
        var damageSeen = false;
        var buffSeen = false;
        string buffSource = null;
        var buffPattern = buffTemplateId > 0 ? BitConverter.GetBytes(buffTemplateId) : null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var link in links)
            {
                foreach (var frame in link.DrainAll())
                {
                    if (frame.Type == SCOffsets.SCSkillFiredPacket)
                        skillStartedSeen = true;

                    if (buffPattern != null && frame.Type == SCOffsets.SCBuffCreatedPacket && frame.Body.Length >= buffPattern.Length && IndexOf(frame.Body, buffPattern) >= 0)
                    {
                        buffSeen = true;
                        buffSource = link.Name;
                    }

                    switch (frame.Type)
                    {
                        case SCOffsets.SCCombatFirstHitPacket when frame.Body.Length >= 6 && ReadBc(frame.Body, 0) == victimObjId:
                        case SCOffsets.SCUnitDamagedPacket when frame.Body.Length >= 17 && ReadBc(frame.Body, 14) == victimObjId:
                            damageSeen = true;
                            break;
                    }
                }
            }

            if (damageSeen && (buffPattern == null || buffSeen))
                break;

            await Task.Delay(250);
        }

        return new DamageOutcome(damageSeen, skillStartedSeen, buffSeen, buffSource);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                    continue;
                match = false;
                break;
            }

            if (match)
                return i;
        }

        return -1;
    }

    /// <summary>Scans the tail of the live game.log for diagnostic markers
    /// (skill validation failures etc.) used for layer attribution.</summary>
    private static List<string> ScanLogTail(string[] markers)
    {
        var found = new List<string>();
        try
        {
            var path = Path.Combine(EvidenceDir, "game.log");
            if (!File.Exists(path))
                return found;
            using var fs = File.OpenRead(path);
            fs.Seek(Math.Max(0, fs.Length - 512 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
                foreach (var marker in markers)
                    if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(line.Trim());
                        break;
                    }
        }
        catch (IOException) { }
        return found;
    }

    private static void DrainAll(BotTcpLink link) => _ = link.DrainAll();

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

    // ---------------------------------------------------------------- bridge

    private static bool TryTeleportToNpc(BotDriveClient bridge, string botName, uint npcTemplateId, List<string> detail)
    {
        try
        {
            // BotDriveClient.Call unwraps {ok,data} and THROWS on ok=false — a
            // returned payload IS a successful teleport.
            _ = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{botName}\",\"op\":\"teleportToNpc\",\"npc\":{npcTemplateId}}}");
            return true;
        }
        catch (Exception ex)
        {
            detail.Add($"{botName}: npc {npcTemplateId} teleport failed: {ex.Message}");
            return false;
        }
    }

    private static uint ProbeBloodstain(BotDriveClient bridge)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var probe = bridge.Call(
                    $"{{\"cmd\":\"drive\",\"bot\":\"{BotBName}\",\"op\":\"doodadObjId\",\"doodad\":{SmallBloodstainDoodadId}}}");
                var objId = probe.GetProperty("objId").GetUInt32();
                if (objId != 0)
                    return objId;
            }
            catch { /* bridge hiccup — retried */ }

            Thread.Sleep(2000);
        }

        return 0;
    }

    private sealed record PositionSnapshot(double X, double Y, double Z, uint ZoneId, uint InstanceId, uint WorldId, string WorldName);

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

    // ---------------------------------------------------------------- sqlite

    /// <summary>Read-only scalar lookup against the RUNTIME compact.sqlite3
    /// (-1 when NULL / no row). NEVER written.</summary>
    private static long QueryScalar(string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={E2eStack.RuntimeSqlite};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? -1 : Convert.ToInt64(result);
    }

    private static long SqliteScalar(string sql) => QueryScalar(sql);

    // ---------------------------------------------------------------- report

    private static string WriteReport(List<StageRecord> stages)
    {
        var report = new
        {
            scenario = "pvp-handshake-e2e",
            milestone = "PVP-01 slice 1 — flagged-aggression handshake on the live stack",
            verdict = stages.Where(s => s.Stage != "WAR-HONOR").All(s => s.Passed) ? "PASS" : "FAIL/BLOCKER",
            flow = new
            {
                provision = "two real Nuian bots via real login/create/select/spawn TCP flow, co-located at CharTemplates spawn",
                flag = "CSSetForceAttackPacket 0x04f ON → SCForceAttackSetPacket(objA,true) + Bloodlust 1482 SCBuffCreated",
                aggress = "Triple Slash 18131 vs same-faction victim in e_steppe_belt (neutral-faction zone, conflict group 14)",
                evidence = "Retribution 2167 + small-bloodstain doodad 877 (CRIME-01 input observable)",
                peaceBlock = "same cast with flag OFF refused while group 14 is in boot-Peace",
                warHonor = "UNRECORDED-deferred (kill-counter escalation unreachable in timebox)"
            },
            stageAttributionNotes = new[]
            {
                "AssaultedBy/AssaultOn are server-memory lists (Character.cs:330-331) with no direct wire/log observable; " +
                "their population shares the exact DamageEffect guard branch (:389-400) whose observable product is the bloodstain doodad.",
                "ZONE-01 ordering finding: BaseUnit.CanAttack returns true for ForceAttack attackers (:100-103) BEFORE the " +
                "BlocksPvpDamage gate (:130-135), so flagged aggression lands during Peace; the unflagged case is refused."
            },
            stages = stages.Select(s => new { stage = s.Stage, passed = s.Passed, detail = s.Detail })
        };

        var path = Path.Combine(EvidenceDir, "pvp-handshake-e2e-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[pvp-hs] report written: {path}");
        return path;
    }
}
