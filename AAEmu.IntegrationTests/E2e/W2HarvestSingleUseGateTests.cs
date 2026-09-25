using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// W2 gate (HARVEST single-use) — the canonical harvest verb takes ONE mature
/// potato crop through the real engine path and the crop is single-use.
///
/// Production chain under test: fixture plants a real crop doodad through the
/// bridge farm seam (GameplayActor.Plant → DoodadManager.CreatePlayerDoodad)
/// at the bot's own position, growth carries 4379 (seedling) → 4457 (mature),
/// the harness then re-approaches the crop (bridge `farm place`) so the engine's
/// 25 m interaction gate is closed at START, and then it issues the canonical
/// verb ONCE:
///   POST /api/actors/harvest → BotActionSpec(Harvest) →
///   BotActionCommandQueue → GameplayActor.Harvest → resolve/range/despawn/
///   phase-skill pre-validation → doodad.Use(Character, 13980) (mature phase
///   4457 → DoodadFuncUse 5887 → 4458 DoodadFuncLootPack 129 → pack 6452 →
///   4459 → doodad deleted) → ChangeLabor(-1) → post-state verify →
///   Complete(yieldDelta).
///
/// PASS needs ALL of: Completed + the phase actually advanced (4457 →
/// 4458-or-deleted) + labor −1 EXACTLY + canonical pack yield (potato 7992 ≥
/// +2 ALWAYS — ungated group 1; seed 15659 ≥ +1 and golden 19887 ≥ +0 REQUIRED
/// only when the lane's effective LootRate is at/above
/// <see cref="HighLootRateCutoff"/>, because those groups are actability-dice
/// gated and ~3.5% rolls at the production default 1.0 — below the cutoff they
/// are evidence-only and a missed roll is UNKNOWN/HARNESS, never a behavior
/// FAIL) + money/bank unchanged + every observed bag delta a gain + a SECOND
/// harvest of the SAME ObjId with a FRESH idempotency key engine-Rejected
/// (RejectedAction "not found in world" or StateTransition "not harvestable in
/// phase …") with a strictly zero delta on labor/bag/money.
///
/// HARNESS (observed, never asserted as behavior): bridge farm rig (seeds=5,
/// labor=5000) + bridge farm plant (seed 15659) + read-only farm status/find
/// polls + POST /api/actors/observe + drive charPos polls + a bridge `farm
/// place` RE-APPROACH teleport after the minutes-long maturity wait (the lane's
/// autonomy can walk the bot off the plant position; the harness re-places it
/// onto the crop's live position so the engine's 25 m harvest gate is closed
/// before START — a staging op, never the verb under test, and a failed
/// re-approach is HARNESS/reapproach, never a behavior verdict). The harness
/// NEVER issues the mirror op of the verb under test: no `farm harvest`, no
/// direct doodad mutation, no POST /api/actors/plant (that is Wave B's claim).
///
/// LANE: adopts the warm lane (stale-proc guard → WebApi TCP + bridge ping
/// probe → lane DB password adopt). NO EnsureUp, NO RestartGameServer — a cold
/// lane is an honest SETUP/lane-down FAIL, never a rebuild.
///
/// CLAIM (only): the production harvest capability takes a mature crop once,
/// advances its phase, spends exactly the skill's labor cost and grants the
/// canonical pack, and the second call is refused by the ENGINE with zero
/// delta. NOT claimed: plant capability, watering/rot/wilt chains, livestock
/// funcs (log-only stubs), autonomy/loop, a second crop cycle.
/// </summary>
[Collection("e2e")]
public class W2HarvestSingleUseGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "W2Harvest" + Stamp;
    private static readonly string BotAccount = ("w2harvest" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private const uint CropDoodad2259 = 2259;
    private const uint PotatoSeed15659 = 15659;
    private const uint Potato7992 = 7992;
    private const uint GoldenPotato19887 = 19887;
    private const uint SeedlingPhase4379 = 4379;
    private const uint MaturePhase4457 = 4457;
    private const uint LootingPhase4458 = 4458;
    private const uint HarvestSkill13980 = 13980;
    private const int HarvestLaborCost = 1;
    private const int RiggedSeeds = 5;
    private const int RiggedLabor = 5000;
    /// <summary>Canonical pack 6452 lower bounds. Potato (group 1) has NO
    /// loot_actability_groups row, so it is exempt from the actability dice
    /// gate in LootPack.GeneratePackNewV2 and MUST always drop — REQUIRED at
    /// every LootRate. Seed 15659 (group 3, max_dice 355) and golden 19887
    /// (group 2, max_dice 345) ARE actability-gated: actDice =
    /// floor(Random(0,10000) / (lootDropRate * World.LootRate)), refused when
    /// actDice * actLevelMultiplier > max_dice. At the production default
    /// LootRate 1.0 (an adopt-only lane: no World.LootRate key) those are
    /// ~3.55% / ~3.45% per harvest, so requiring them would fail ~96% of
    /// correct runs. E2eStack instead writes World.LootRate 100 (E2eStack.cs:481,
    /// rationale at :458-465: dividing the actability dice by LootRate restores
    /// the reliable yield a leveled farmer gets on live 1.2), which is why
    /// B2/B4 could prove >=+1 / >=+0. The >= thresholds below therefore apply
    /// only at/above <see cref="HighLootRateCutoff"/>; below it the gated drops
    /// are evidence-only (see the post-first-yield leg).</summary>
    private const int MinPotatoYield = 2;
    private const int MinSeedYield = 1;
    private const int MinGoldenYield = 0;
    /// <summary>Effective LootRate at/above which the actability-gated pack
    /// groups (seed/golden) are REQUIRED to drop. E2eStack boots at 100; the
    /// production default is 1.0.</summary>
    private const double HighLootRateCutoff = 10.0;

    /// <summary>Maturity budget (fixed 660s): adopt-only lanes boot outside E2eStack so no World.GrowthRate override is written (Configurations.GrowthRate defaults 1.0). The 4379→4457 chain is TWO growth legs, not one: doodad_func_growths id 583 delay 60000ms (60s) reaches 4456, then id 584 delay 540000ms (540s) reaches 4457 — 60s + 540s = 600s at rate 1.0. The prior 600s budget counted only the last leg and left ZERO margin, so it stalled at 4456 exactly as the crop reached 4457; 660s = 600s chain + 60s poll margin. HARNESS/crop-not-mature now fires only on true stall/regression.</summary>
    private const int MaturityBudgetMs = 660_000;
    /// <summary>The engine's own harvest/interaction range gate, read from the
    /// production constant so the fixture re-approach is judged against the
    /// real refusal threshold (<see cref="AAEmu.Game.Core.Managers.Bots.GameplayActor.MaxInteractRange"/> = 25 m flat).</summary>
    private const float MaxHarvestGateM = AAEmu.Game.Core.Managers.Bots.GameplayActor.MaxInteractRange;
    private const int ActionDeadlineSeconds = 60;
    private const int WakeWaitMs = 1000;
    /// <summary>The lane's autonomy probe: a fresh account must not already
    /// hold quest 251 after the enroll wake — an active quest means autonomy
    /// pre-empted the explicit leg (never this gate's claim).</summary>
    private const uint AutonomyQuest251 = 251;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "w2-harvest-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task MatureCropHarvestsOnceWithLaborYieldAndEngineRefusedSecondCall()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# W2 harvest single-use gate (bridge-planted mature crop → one canonical harvest) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        BotNetworkSession? session = null;
        BotDriveClient? laneBridge = null;
        var setupSeconds = 0.0;
        var execSeconds = 0.0;
        var passed = false;
        string verdict = "UNKNOWN";
        string failBoundary = "";
        string claim = "";
        object? failingCondition = null;

        uint charId = 0;
        uint cropObjId = 0;
        uint cropDbId = 0;
        uint plantedTemplate = 0;
        uint plantedPhase = 0;
        var phaseTrail = new List<uint>();
        var maturitySeconds = 0.0;
        var growthRate = double.NaN;
        var lootRate = double.NaN;
        var reapproachPos = "UNAVAILABLE";
        var reapproachDist = double.NaN;

        var startPhase = 0u;
        var startLabor = -1;
        long startMoney = -1, startBank = -1;
        var startBag = new Dictionary<uint, int>();
        var startPos = "UNAVAILABLE";

        var firstCallState = "";
        var firstCallFailure = "";
        var firstCallDetail = "";
        var firstCallTrace = Guid.Empty;
        var firstYieldPayload = int.MinValue;
        var endPhaseOrDeleted = "UNOBSERVED";

        var laborAfter = -1;
        long moneyAfter = -1, bankAfter = -1;
        var bagAfter = new Dictionary<uint, int>();

        var secondCallState = "";
        var secondCallFailure = "";
        var secondCallDetail = "";
        var secondCallTrace = Guid.Empty;
        var secondKey = "";
        var singleUseRefusalKind = "UNOBSERVED";

        var laborFinal = -1;
        long moneyFinal = -1, bankFinal = -1;
        var bagFinal = new Dictionary<uint, int>();

        try
        {
            // ---- ADOPT (probe-only; never EnsureUp/Restart — see class doc) ----
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = GuardSharedWebApiPort();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "warm lane answering (WebApi TCP + bridge ping)" : "lane cold — refusing to rebuild",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "SETUP/lane-down";
                Assert.Fail("warm lane not answering; refusing to rebuild per workstream constraints");
            }

            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);
            laneBridge = bridge;
            // Shared-port routing: every lane game server binds the same WebApi
            // port, so each TCP connection hashes to ONE lane. A fresh
            // HttpClient re-hashes; keep the first client whose backend answers
            // enabled (G7c route-proof pattern).
            using var http = await EnsureWebApiClientAsync(evidence);

            // ---- SETUP: real TCP enter-world (timed separately) ----
            E2eStack.CleanupBotRows(BotAccount);
            session = await BotNetworkSession.ConnectAsync(
                BotName, BotAccount, Password,
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!session.InWorld)
            {
                failBoundary = "SETUP/enter-world";
                Leg("enter-world", false, "real login flow did not reach in-world", setupSw.Elapsed.TotalSeconds);
                Assert.Fail("bot must enter the live world through the real login flow");
            }
            Leg("enter-world", true, $"inWorld={session.InWorld}", setupSw.Elapsed.TotalSeconds);

            // ---- ENROLL: registers/activates the runtime the /api/actors
            // queue needs; the wake is probe-only (no gameplay command). ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 60_000);
            charId = GetUInt(enroll, "id");
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, AutonomyQuest251);
            Leg("enroll", !activeBefore,
                $"charId={charId} activeBefore={activeBefore} (must be False: no autonomy claim)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest 251 already active after enroll wake — autonomy pre-empted the explicit leg; re-run with a fresh account");
            }
            if (charId == 0)
            {
                failBoundary = "SETUP/enroll-unresolved";
                Assert.Fail("enroll wake returned no character id — the /api/actors queue cannot address this bot");
            }

            // ---- FIXTURE (disclosed, bridge ops only): rig seeds + labor ----
            var fixSw = Stopwatch.StartNew();
            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"seeds\":{RiggedSeeds},\"calves\":0,\"labor\":{RiggedLabor}}}",
                60_000);
            var riggedSeeds = GetInt(rig, "seeds");
            var riggedLabor = GetInt(rig, "labor");
            Leg("fixture-rig", riggedSeeds >= RiggedSeeds && riggedLabor == RiggedLabor,
                $"seeds={riggedSeeds} (REQUIRE >= {RiggedSeeds}) labor={riggedLabor} (REQUIRE {RiggedLabor})",
                fixSw.Elapsed.TotalSeconds);
            if (riggedSeeds < RiggedSeeds || riggedLabor != RiggedLabor)
            {
                failBoundary = "SETUP/rig-ineffective";
                Assert.Fail($"fixture rig ineffective (seeds={riggedSeeds} labor={riggedLabor})");
            }

            // ---- FIXTURE: plant the crop through the BRIDGE farm seam (the
            // production GameplayActor.Plant path B2/B4 use). POST
            // /api/actors/plant is Wave B's claim and is deliberately NOT
            // exercised. The crop lands at the bot's own position; the flat
            // harvest range gate (25 m) is re-established explicitly AFTER the
            // maturity wait by the RE-APPROACH leg below, because lane autonomy
            // can move the bot while the crop grows. ----
            var plantSw = Stopwatch.StartNew();
            var plant = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"plant\",\"bot\":\"{BotName}\",\"seed\":{PotatoSeed15659}}}",
                120_000);
            cropObjId = GetUInt(plant, "objId");
            cropDbId = GetUInt(plant, "dbId");
            plantedTemplate = GetUInt(plant, "template");
            plantedPhase = GetUInt(plant, "phase");
            var plantDetail = GetStr(plant, "detail");
            var plantedOk = cropObjId != 0 && cropDbId != 0
                && plantedTemplate == CropDoodad2259 && plantedPhase == SeedlingPhase4379;
            Leg("fixture-plant", plantedOk,
                $"plantedVia=bridge-farm-plant objId={cropObjId} dbId={cropDbId} template={plantedTemplate} (REQUIRE {CropDoodad2259}) phase={plantedPhase} (REQUIRE {SeedlingPhase4379}) detail=[{plantDetail}]",
                plantSw.Elapsed.TotalSeconds);
            if (!plantedOk)
            {
                failBoundary = "HARNESS/planted-wrong-template";
                verdict = "UNKNOWN/HARNESS/planted-wrong-template";
                failingCondition = new
                {
                    boundary = failBoundary,
                    plantedVia = "bridge-farm-plant",
                    cropObjId,
                    cropDbId,
                    template = plantedTemplate,
                    phase = plantedPhase,
                    detail = plantDetail,
                    interpretation = "the bridge plant seam produced no 2259 crop in phase 4379 — the fixture never reached the pre-maturity chain; refusing to observe a harvest"
                };
                Assert.Fail($"W2 {verdict} at {failBoundary}: objId={cropObjId} template={plantedTemplate} phase={plantedPhase} detail=[{plantDetail}]");
            }

            // ---- FIXTURE: wait for maturity (4457) inside the budget,
            // recording a FORWARD-ONLY phase trail from 4379. ----
            var matureSw = Stopwatch.StartNew();
            phaseTrail.Add(plantedPhase);
            var reachedMature = false;
            var phaseRegressed = false;
            var maturityDeadline = Environment.TickCount64 + MaturityBudgetMs;
            while (Environment.TickCount64 < maturityDeadline)
            {
                var crop = ReadCrop(bridge, cropObjId);
                if (!crop.Found)
                {
                    // A crop that vanishes before maturity is a fixture loss, not
                    // a harvest outcome: the maturity poll simply never lands.
                    phaseTrail.Add(0);
                    break;
                }
                if (phaseTrail[^1] != crop.Phase)
                    phaseTrail.Add(crop.Phase);
                // Forward-only: any observed phase below its predecessor is a
                // fixture-chain regression (recorded, and it fails the leg).
                for (var i = 1; i < phaseTrail.Count; i++)
                    if (phaseTrail[i] < phaseTrail[i - 1])
                        phaseRegressed = true;
                if (crop.Phase == MaturePhase4457)
                {
                    reachedMature = true;
                    break;
                }
                Thread.Sleep(1000);
            }
            maturitySeconds = matureSw.Elapsed.TotalSeconds;
            growthRate = ReadEffectiveGrowthRate();
            lootRate = ReadEffectiveLootRate();
            Leg("fixture-mature", reachedMature && !phaseRegressed,
                $"phaseTrail=[{string.Join("->", phaseTrail)}] (REQUIRE forward-only to {MaturePhase4457}) maturitySeconds={maturitySeconds:0.0}s growthRate={growthRate}",
                maturitySeconds);
            if (!reachedMature || phaseRegressed)
            {
                failBoundary = "HARNESS/crop-not-mature";
                verdict = "UNKNOWN/HARNESS/crop-not-mature";
                failingCondition = new
                {
                    boundary = failBoundary,
                    cropObjId,
                    cropDbId,
                    phaseTrail,
                    phaseRegressed,
                    maturitySeconds,
                    budgetSeconds = MaturityBudgetMs / 1000.0,
                    growthRate,
                    interpretation = "the fixture crop never reached mature phase 4457 inside the budget (or its phase trail regressed) — the harvest precondition is unproven; refusing to observe a harvest"
                };
                Assert.Fail($"W2 {verdict} at {failBoundary}: objId={cropObjId} trail=[{string.Join("->", phaseTrail)}] in {maturitySeconds:0.0}s");
            }

            // ---- RE-APPROACH (harness precondition, never a behavior claim).
            // The maturity wait is minutes long, and the lane's autonomy
            // (BotGoalArbiter → QuestBootstrap quest.progress → QUEST_TRAVEL)
            // can walk the bot off the plant position while it waits, leaving
            // the crop OUTSIDE the engine's harvest gate (GameplayActor
            // MaxInteractRange, 25 m flat) — the crop matures, but the verb is
            // then refused on range by the ENGINE and no harvest behavior can
            // be observed. The harness therefore re-places the bot onto the
            // crop's LIVE position through the bridge `farm place` teleport
            // staging op (the W1 far-stage / B2 needs-farm pattern; a staging
            // op, not the verb under test) and re-reads charPos + crop pos to
            // PROVE the flat distance is inside the gate before START. A
            // re-approach that cannot close the gate is HARNESS/reapproach.
            var reapproachSw = Stopwatch.StartNew();
            var approachCrop = ReadCrop(bridge, cropObjId);
            var place = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat(approachCrop.X)},\"y\":{FormatFloat(approachCrop.Y)},\"z\":{FormatFloat(approachCrop.Z)}}}",
                30_000);
            reapproachPos = ReadPos(bridge);
            var verifiedCrop = ReadCrop(bridge, cropObjId);
            reapproachDist = FlatDistToCrop(reapproachPos, verifiedCrop.X, verifiedCrop.Y);
            var reapproachOk = verifiedCrop.Found && !double.IsNaN(reapproachDist)
                && reapproachDist <= MaxHarvestGateM;
            Leg("crop-reapproach", reapproachOk,
                $"crop=[{approachCrop.X:0.0},{approachCrop.Y:0.0},{approachCrop.Z:0.0}] place->[{reapproachPos}] verifiedCrop=[{verifiedCrop.X:0.0},{verifiedCrop.Y:0.0}] flat={reapproachDist:0.00}m (REQUIRE <= {MaxHarvestGateM:0.0}m engine gate) placeEcho=[{place}]",
                reapproachSw.Elapsed.TotalSeconds);
            evidence.AppendLine($"- RE-APPROACH crop x/y from bridge status=[{approachCrop.X:0.###},{approachCrop.Y:0.###}] flat={reapproachDist:0.00}m (engine gate {MaxHarvestGateM:0.0}m)");
            if (!reapproachOk)
            {
                failBoundary = "HARNESS/reapproach";
                verdict = "UNKNOWN/HARNESS/reapproach";
                failingCondition = new
                {
                    boundary = failBoundary,
                    cropObjId,
                    cropPos = new { approachCrop.X, approachCrop.Y, approachCrop.Z },
                    charPos = reapproachPos,
                    flatDist = reapproachDist,
                    maxGate = MaxHarvestGateM,
                    interpretation = "the bot could not be brought inside the engine's harvest interaction gate after the maturity wait — the verb would be refused on range, so this is a harness precondition failure, never a capability verdict"
                };
                Assert.Fail($"W2 {verdict} at {failBoundary}: objId={cropObjId} charPos=[{reapproachPos}] cropPos=[{approachCrop.X:0.###},{approachCrop.Y:0.###}] flat={reapproachDist:0.00}m");
            }

            // ---- START SNAPSHOT (ONE authoritative read, immediately
            // pre-verb). Crop gone or off-mature at START = fixture loss. ----
            var startSw = Stopwatch.StartNew();
            var startCrop = ReadCrop(bridge, cropObjId);
            startPhase = startCrop.Phase;
            var startObs = await ObserveAsync(http, "start");
            startLabor = GetInt(startObs, "LaborPower");
            startMoney = GetInt64(startObs, "Money");
            startBank = GetInt64(startObs, "BankMoney");
            startBag = ReadBag(startObs);
            startPos = ReadPos(bridge);
            var startDist = FlatDistToCrop(startPos, startCrop.X, startCrop.Y);
            var startOk = startCrop.Found && startPhase == MaturePhase4457 && startLabor >= HarvestLaborCost
                && !double.IsNaN(startDist) && startDist <= MaxHarvestGateM;
            Leg("start-snapshot", startOk,
                $"crop found={startCrop.Found} template={startCrop.Template} phase={startPhase} (REQUIRE {MaturePhase4457}) dbId={startCrop.DbId} charId={charId} charPos=[{startPos}] flat={startDist:0.00}m (REQUIRE <= {MaxHarvestGateM:0.0}m engine gate) labor={startLabor} (skill {HarvestSkill13980} cost {HarvestLaborCost}) money={startMoney} bank={startBank} bag=[{BagText(startBag)}]",
                startSw.Elapsed.TotalSeconds);
            evidence.AppendLine($"- START phaseTrail=[{string.Join("->", phaseTrail)}] startPhase={startPhase} cropDbId={cropDbId} (plant dbId={cropDbId}) plantedPhase={plantedPhase}");
            if (!startOk)
            {
                failBoundary = "HARNESS/crop-gone-at-start";
                verdict = "UNKNOWN/HARNESS/crop-gone-at-start";
                failingCondition = new
                {
                    boundary = failBoundary,
                    cropObjId,
                    cropDbId,
                    found = startCrop.Found,
                    phase = startPhase,
                    startLabor,
                    charPos = startPos,
                    flatDist = startDist,
                    maxGate = MaxHarvestGateM,
                    interpretation = "the crop left the mature state (or left the world), or the bot was outside the engine's harvest gate, before START — the harvest precondition drifted; refusing to observe the verb"
                };
                Assert.Fail($"W2 {verdict} at {failBoundary}: objId={cropObjId} found={startCrop.Found} phase={startPhase} flat={startDist:0.00}m");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- EXECUTE: the ONE canonical verb under test ----
            var execSw = Stopwatch.StartNew();
            var firstKey = $"w2-harvest-{BotAccount}-1";
            var firstBody = $"{{\"bot\":\"{BotName}\",\"doodadObjId\":{cropObjId},\"idempotencyKey\":\"{firstKey}\"}}";
            JsonElement firstPoll;
            try
            {
                var firstPost = await PostJsonAsync(http, "/api/actors/harvest", firstBody);
                firstCallTrace = firstPost.GetProperty("trace_id").GetGuid();
                firstPoll = await PollTerminalAsync(http, firstCallTrace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
            }
            catch (TimeoutException ex)
            {
                verdict = "FAIL-BEHAVIOR/ACTION-TIMEOUT";
                failBoundary = "RUN/action-deadline";
                failingCondition = new { boundary = failBoundary, cropObjId, message = ex.Message, interpretation = "the harvest action never reached a terminal state inside the deadline" };
                Leg("harvest-first", false, ex.Message, execSw.Elapsed.TotalSeconds);
                Assert.Fail($"W2 {verdict} at {failBoundary}: {ex.Message}");
                return;
            }
            firstCallState = firstPoll.GetProperty("state").GetString() ?? "";
            firstCallFailure = GetStr(firstPoll, "failure");
            firstCallDetail = GetStr(firstPoll, "detail");
            if (firstPoll.TryGetProperty("result_payload", out var yp)
                && yp.ValueKind == JsonValueKind.Number && yp.TryGetInt32(out var yv))
                firstYieldPayload = yv;
            var completed = firstCallState == "Completed" && string.IsNullOrEmpty(firstCallFailure);
            Leg("harvest-first", completed,
                $"state={firstCallState} failure=[{firstCallFailure}] detail=[{firstCallDetail}] result_payload={firstYieldPayload} trace={firstCallTrace}",
                execSw.Elapsed.TotalSeconds);
            if (!completed)
            {
                verdict = "FAIL-BEHAVIOR/HARVEST-REFUSED";
                failBoundary = "HARVEST/refused";
                failingCondition = new { boundary = failBoundary, cropObjId, startPhase, state = firstCallState, failure = firstCallFailure, detail = firstCallDetail, interpretation = "the canonical harvest of a mature crop was refused / never completed" };
                Assert.Fail($"W2 {verdict} at {failBoundary}: {firstCallState}/{firstCallFailure} {firstCallDetail}");
            }

            // ---- POST-1 observations (observe-only reads; no further verbs) ----
            var postOneSw = Stopwatch.StartNew();
            var endCrop = ReadCrop(bridge, cropObjId);
            endPhaseOrDeleted = endCrop.Found ? endCrop.Phase.ToString(CultureInfo.InvariantCulture) : "deleted";
            var phaseAdvanced = !endCrop.Found || endCrop.Phase != startPhase;
            var postOneObs = await ObserveAsync(http, "post-first");
            laborAfter = GetInt(postOneObs, "LaborPower");
            moneyAfter = GetInt64(postOneObs, "Money");
            bankAfter = GetInt64(postOneObs, "BankMoney");
            bagAfter = ReadBag(postOneObs);
            evidence.AppendLine($"- POST-1: cropFound={endCrop.Found} phase={endPhaseOrDeleted} labor {startLabor}->{laborAfter} money {startMoney}->{moneyAfter} bank {startBank}->{bankAfter} bag [{BagText(startBag)}]->[{BagText(bagAfter)}]");

            var laborDelta = startLabor >= 0 && laborAfter >= 0 ? laborAfter - startLabor : int.MinValue;
            var potatoDelta = CountOf(bagAfter, Potato7992) - CountOf(startBag, Potato7992);
            var seedDelta = CountOf(bagAfter, PotatoSeed15659) - CountOf(startBag, PotatoSeed15659);
            var goldenDelta = CountOf(bagAfter, GoldenPotato19887) - CountOf(startBag, GoldenPotato19887);
            var moneyUnchanged = moneyAfter == startMoney && bankAfter == startBank;
            var allGains = AllDeltasAreGains(startBag, bagAfter, out var lossTemplate, out var lossDelta);
            // LootRate-aware yield expectation. Potato (ungated group) is
            // REQUIRED at every rate; the actability-gated seed/golden groups are
            // REQUIRED only on an E2eStack-boosted lane (LootRate >= cutoff) and
            // evidence-only below it (see MinPotatoYield doc). A NEGATIVE delta on
            // any of them is still a foreign-verb-delta failure, and the
            // conservation leg independently forbids any bag loss.
            var highLootRate = lootRate >= HighLootRateCutoff;
            var yieldOk = potatoDelta >= MinPotatoYield
                && (!highLootRate || (seedDelta >= MinSeedYield && goldenDelta >= MinGoldenYield));
            var gatedYieldUnproven = !highLootRate
                && (seedDelta < MinSeedYield || goldenDelta < MinGoldenYield);
            var laborVoidConfig = LaborConsumptionDisabled();

            Leg("post-first-transition", phaseAdvanced,
                $"phase {startPhase} -> {endPhaseOrDeleted} (REQUIRE 4458-or-deleted) traceDetail=[{firstCallDetail}]",
                postOneSw.Elapsed.TotalSeconds);
            Leg("post-first-labor", laborDelta == -HarvestLaborCost,
                $"labor {startLabor}->{laborAfter} delta={laborDelta} (REQUIRE {HarvestLaborCost * -1} EXACT) laborVoidConfig={laborVoidConfig}",
                0);
            Leg("post-first-yield", yieldOk,
                $"potato7992 {CountOf(startBag, Potato7992)}->{CountOf(bagAfter, Potato7992)} (delta {potatoDelta}, REQUIRE >= +{MinPotatoYield} always: ungated group 1) seed15659 delta={seedDelta} ({(highLootRate ? $"REQUIRE >= +{MinSeedYield}" : "evidence-only: actability-gated, LootRate < cutoff")}) golden19887 delta={goldenDelta} ({(highLootRate ? $"REQUIRE >= +{MinGoldenYield}" : "evidence-only: actability-gated, LootRate < cutoff")}) lootRate={lootRate} highLootRate={highLootRate} payload={firstYieldPayload}",
                0);
            Leg("post-first-conservation", moneyUnchanged && allGains,
                $"moneyDelta={moneyAfter - startMoney} bankDelta={bankAfter - startBank} (REQUIRE both 0) bagLoss={(lossTemplate == 0 ? "NONE" : $"{lossTemplate}:{lossDelta}")}",
                0);

            if (!phaseAdvanced)
            {
                verdict = "FAIL-BEHAVIOR/NO-PHASE-TRANSITION";
                failBoundary = "HARVEST/no-phase-transition";
                failingCondition = new { boundary = failBoundary, cropObjId, startPhase, endPhaseOrDeleted, detail = firstCallDetail, interpretation = "the crop phase did not advance after a Completed harvest — the engine interaction did not run the phase machine" };
            }
            else if (laborDelta == 0 && laborVoidConfig)
            {
                verdict = "UNKNOWN/CONFIG/labor-void";
                failBoundary = "UNKNOWN/CONFIG/labor-void";
                failingCondition = new { boundary = failBoundary, cropObjId, startLabor, laborAfter, laborDelta, interpretation = "labor did not move and the lane config disables labor consumption (Labor.DisableConsumption=true) — a CONFIG verdict, never a behavior verdict" };
            }
            else if (laborDelta != -HarvestLaborCost)
            {
                verdict = "FAIL-BEHAVIOR/LABOR-NOT-EXACT";
                failBoundary = "HARVEST/labor-not-exact";
                failingCondition = new { boundary = failBoundary, cropObjId, startLabor, laborAfter, laborDelta, expected = -HarvestLaborCost, interpretation = "harvest must spend exactly the skill's ConsumeLaborPower (13980 = 1) — no more, no less" };
            }
            else if (!moneyUnchanged || !allGains)
            {
                // Evaluated BEFORE the yield branches: a bag template LOSING
                // units (including a negative seed/golden delta) or money/bank
                // moving is an isolation failure, and must never be masked by
                // the low-LootRate evidence-only path.
                verdict = "FAIL-BEHAVIOR/FOREIGN-VERB-DELTA";
                failBoundary = "ISOLATION/foreign-verb-delta";
                failingCondition = new { boundary = failBoundary, cropObjId, moneyDelta = moneyAfter - startMoney, bankDelta = bankAfter - startBank, lossTemplate, lossDelta, bagBefore = BagText(startBag), bagAfter = BagText(bagAfter), interpretation = "money/bank moved, or a bag template LOST units, across a harvest — every observed delta must be a gain and money must not move" };
            }
            else if (!yieldOk)
            {
                verdict = "FAIL-BEHAVIOR/NO-YIELD";
                failBoundary = "HARVEST/no-yield";
                failingCondition = new { boundary = failBoundary, cropObjId, potatoDelta, seedDelta, goldenDelta, lootRate, highLootRate, expected = new { potato = $">=+{MinPotatoYield} (always, ungated group 1)", seed = highLootRate ? $">=+{MinSeedYield}" : "evidence-only (actability-gated, low LootRate)", golden = highLootRate ? $">=+{MinGoldenYield}" : "evidence-only (actability-gated, low LootRate)" }, bagBefore = BagText(startBag), bagAfter = BagText(bagAfter), interpretation = "the canonical pack 6452 yield never landed where it is REQUIRED: potato is ungated and must always drop, and on an E2eStack-boosted lane (LootRate >= cutoff) the actability-gated seed/golden must land too (B2/B4-proven at LootRate 100)" };
            }
            else if (gatedYieldUnproven)
            {
                // Low-LootRate lane: potato landed (ungated), but the
                // actability-gated groups did not roll this harvest (~3.5% at
                // LootRate 1.0). The canonical-yield part of the claim cannot be
                // proven here and the engine is NOT at fault — a HARNESS
                // precondition gap, never a behavior verdict.
                verdict = "UNKNOWN/HARNESS/low-lootrate-yield-unproven";
                failBoundary = "HARVEST/low-lootrate-yield-unproven";
                failingCondition = new { boundary = failBoundary, cropObjId, potatoDelta, seedDelta, goldenDelta, lootRate, cutoff = HighLootRateCutoff, expected = new { potato = $">=+{MinPotatoYield} (always, ungated group 1)", seed = ">=+1 ONLY at LootRate >= cutoff (actability-gated group 3, max_dice 355)", golden = ">=+0 ONLY at LootRate >= cutoff (actability-gated group 2, max_dice 345)" }, bagBefore = BagText(startBag), bagAfter = BagText(bagAfter), interpretation = "the actability-gated pack groups (seed/golden) are ~3.5% rolls at this lane's production LootRate, so the canonical-yield claim is unproven — the harvest itself passed (Completed, phase advanced, labor exact); re-run on an E2eStack-boosted lane (LootRate 100) to prove the gated yield" };
            }
            else
            {
                // ---- SINGLE-USE LEG: same ObjId, FRESH key ----
                var secondSw = Stopwatch.StartNew();
                secondKey = $"w2-harvest-{BotAccount}-2-fresh";
                var secondBody = $"{{\"bot\":\"{BotName}\",\"doodadObjId\":{cropObjId},\"idempotencyKey\":\"{secondKey}\"}}";
                JsonElement secondPoll;
                try
                {
                    var secondPost = await PostJsonAsync(http, "/api/actors/harvest", secondBody);
                    secondCallTrace = secondPost.GetProperty("trace_id").GetGuid();
                    secondPoll = await PollTerminalAsync(http, secondCallTrace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
                }
                catch (TimeoutException ex)
                {
                    secondCallState = "TimedOut";
                    secondCallDetail = ex.Message;
                    secondPoll = default;
                }
                if (secondPoll.ValueKind == JsonValueKind.Object)
                {
                    secondCallState = secondPoll.GetProperty("state").GetString() ?? "";
                    secondCallFailure = GetStr(secondPoll, "failure");
                    secondCallDetail = GetStr(secondPoll, "detail");
                }

                var isRejected = secondCallState == "Rejected";
                var isDedupe = isRejected && secondCallDetail.Contains("duplicate idempotency key", StringComparison.OrdinalIgnoreCase);
                var engineDoodadGone = isRejected && !isDedupe
                    && secondCallFailure == "RejectedAction"
                    && secondCallDetail.Contains("not found in world", StringComparison.Ordinal);
                var engineNotHarvestable = isRejected && !isDedupe
                    && secondCallFailure == "StateTransition"
                    && secondCallDetail.Contains("not harvestable in phase", StringComparison.Ordinal);
                singleUseRefusalKind = !isRejected
                    ? "completed"
                    : isDedupe
                        ? "dedupe-non-proof"
                        : engineDoodadGone
                            ? "RejectedAction/doodad-gone"
                            : engineNotHarvestable
                                ? "StateTransition/not-harvestable"
                                : $"other/{secondCallFailure}";

                var finalObs = await ObserveAsync(http, "post-second");
                laborFinal = GetInt(finalObs, "LaborPower");
                moneyFinal = GetInt64(finalObs, "Money");
                bankFinal = GetInt64(finalObs, "BankMoney");
                bagFinal = ReadBag(finalObs);
                var zeroDelta = laborFinal == laborAfter
                    && moneyFinal == moneyAfter && bankFinal == bankAfter
                    && BagsEqual(bagAfter, bagFinal);

                Leg("single-use-second-call", engineDoodadGone || engineNotHarvestable,
                    $"state={secondCallState} failure=[{secondCallFailure}] detail=[{secondCallDetail}] kind={singleUseRefusalKind} freshKey={secondKey} trace={secondCallTrace}",
                    secondSw.Elapsed.TotalSeconds);
                Leg("single-use-zero-delta", zeroDelta,
                    $"labor {laborAfter}->{laborFinal} money {moneyAfter}->{moneyFinal} bank {bankAfter}->{bankFinal} bag [{BagText(bagAfter)}]->[{BagText(bagFinal)}] (REQUIRE byte-equal)",
                    0);

                if (!isRejected)
                {
                    verdict = "FAIL-BEHAVIOR/NOT-SINGLE-USE";
                    failBoundary = "HARVEST/not-single-use";
                    failingCondition = new { boundary = failBoundary, cropObjId, startPhase, endPhaseOrDeleted, secondState = secondCallState, secondFailure = secondCallFailure, secondDetail = secondCallDetail, interpretation = "a second harvest of the SAME doodad with a FRESH key was not engine-Rejected — the crop is reusable" };
                }
                else if (isDedupe)
                {
                    verdict = "FAIL-BEHAVIOR/SINGLE-USE-BY-DEDUPE-ONLY";
                    failBoundary = "HARVEST/single-use-by-dedupe-only";
                    failingCondition = new { boundary = failBoundary, cropObjId, secondDetail = secondCallDetail, interpretation = "the refusal came from the idempotency-key DEDUPE gate, not from the engine's doodad lookup/phase check — this is NOT a single-use proof (fresh key was supplied)" };
                }
                else if (!(engineDoodadGone || engineNotHarvestable))
                {
                    verdict = "FAIL-BEHAVIOR/SINGLE-USE-REFUSAL-UNCLASSIFIED";
                    failBoundary = "HARVEST/single-use-refusal-unclassified";
                    failingCondition = new { boundary = failBoundary, cropObjId, secondState = secondCallState, secondFailure = secondCallFailure, secondDetail = secondCallDetail, interpretation = "the second call was Rejected, but not by the engine's doodad-gone or not-harvestable-in-phase path — the single-use proof is unclassified" };
                }
                else if (!zeroDelta)
                {
                    verdict = "FAIL-BEHAVIOR/POST-REFUSAL-DELTA";
                    failBoundary = "HARVEST/post-refusal-delta";
                    failingCondition = new { boundary = failBoundary, cropObjId, laborBefore = laborAfter, laborAfter = laborFinal, moneyBefore = moneyAfter, moneyAfter = moneyFinal, bankBefore = bankAfter, bankAfter = bankFinal, bagBefore = BagText(bagAfter), bagAfter = BagText(bagFinal), interpretation = "a refused second harvest must leave labor/bag/money byte-equal to the post-first-harvest state" };
                }
                else
                {
                    passed = true;
                    verdict = "PASS-BEHAVIOR";
                    failBoundary = "none";
                    claim = $"mature 2259 crop {cropObjId} (db {cropDbId}) harvested EXACTLY once: Completed, phase {startPhase} → {endPhaseOrDeleted}, labor {startLabor}→{laborAfter} (Δ-{HarvestLaborCost} exact), yield potato7992 +{potatoDelta} / seed15659 +{seedDelta} / golden19887 +{goldenDelta} (lootRate={lootRate}{((lootRate >= HighLootRateCutoff) ? "" : " — gated groups evidence-only below cutoff")}), money/bank unchanged; second call on the same ObjId with a fresh key engine-Rejected ({singleUseRefusalKind}) with a strictly zero delta";
                    failingCondition = new { boundary = "none", cropObjId, cropDbId, startPhase, endPhaseOrDeleted, laborBefore = startLabor, laborAfter, potatoDelta, seedDelta, goldenDelta, singleUseRefusalKind, secondKey };
                }
            }

            execSeconds = execSw.Elapsed.TotalSeconds;
            Leg("gate", passed,
                $"{verdict} at {failBoundary}: crop={cropObjId} phase {startPhase}->{endPhaseOrDeleted} labor {startLabor}->{laborAfter} potato+{potatoDelta} second={singleUseRefusalKind}",
                execSeconds);
            Assert.True(passed, $"W2 {verdict} at {failBoundary}: crop={cropObjId}");
        }
        catch (Exception ex) when (string.IsNullOrEmpty(failBoundary))
        {
            verdict = "UNKNOWN/HARNESS/exception";
            failBoundary = "HARNESS/unexpected";
            failingCondition = new { boundary = failBoundary, type = ex.GetType().Name, message = ex.Message, interpretation = "an unexpected harness exception aborted the gate before a terminal classification" };
            evidence.AppendLine($"- UNEXPECTED: {ex.GetType().Name}: {ex.Message}");
            Assert.Fail($"W2 {verdict} at {failBoundary}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            totalWall.Stop();
            string cleanupSummary;
            try
            {
                try { session?.Dispose(); } catch { }
                cleanupSummary = TryReleaseBot(laneBridge, BotName, charId);
            }
            catch (Exception ex)
            {
                cleanupSummary = $"UNAVAILABLE (release threw {ex.GetType().Name}: {ex.Message})";
            }
            try
            {
                await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
                    new
                    {
                        plantedVia = "bridge-farm-plant",
                        seedItem = PotatoSeed15659,
                        cropTemplate = CropDoodad2259,
                        cropObjId,
                        cropDbId,
                        plantedTemplate,
                        plantedPhase,
                        phaseTrail,
                        growthRate,
                        lootRate,
                        maturitySeconds,
                        reapproachPos,
                        reapproachDist,
                        startPhase,
                        endPhaseOrDeleted,
                        start = new
                        {
                            charId,
                            charPos = startPos,
                            labor = startLabor,
                            money = startMoney,
                            bankMoney = startBank,
                            bag = BagText(startBag),
                            expectedSkill = HarvestSkill13980,
                            expectedLaborCost = HarvestLaborCost
                        },
                        postFirst = new
                        {
                            labor = laborAfter,
                            money = moneyAfter,
                            bankMoney = bankAfter,
                            bag = BagText(bagAfter),
                            harvestYieldPayload = firstYieldPayload
                        },
                        firstCall = new { state = firstCallState, failure = firstCallFailure, detail = firstCallDetail, traceId = firstCallTrace },
                        secondCall = new { state = secondCallState, failure = secondCallFailure, detail = secondCallDetail, traceId = secondCallTrace, idempotencyKey = secondKey },
                        singleUseRefusalKind,
                        postSecond = new
                        {
                            labor = laborFinal,
                            money = moneyFinal,
                            bankMoney = bankFinal,
                            bag = BagText(bagFinal)
                        },
                        cleanupSummary
                    },
                    setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(EvidenceDir);
                    File.WriteAllText(ReportPath + ".fallback.json",
                        $"{{\"scenario\":\"w2-harvest-single-use\",\"verdict\":\"{verdict}\",\"failBoundary\":\"{failBoundary}\",\"reportError\":\"{ex.GetType().Name}: {ex.Message}\",\"legs\":{legs.Count}}}");
                }
                catch { }
            }
            Console.WriteLine($"W2-GATE verdict={verdict} boundary={failBoundary} crop={cropObjId} " +
                $"phase={startPhase}->{endPhaseOrDeleted} labor={startLabor}>{laborAfter} second={singleUseRefusalKind} legs={legs.Count}");
        }
    }

    // ---- observation helpers ------------------------------------------------

    private static async Task<JsonElement> ObserveAsync(HttpClient http, string tag)
    {
        var body = $"{{\"bot\":\"{BotName}\"}}";
        var post = await PostJsonAsync(http, "/api/actors/observe", body);
        var trace = post.GetProperty("trace_id").GetGuid();
        var poll = await PollTerminalAsync(http, trace, TimeSpan.FromSeconds(30));
        if (!poll.TryGetProperty("result_payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"observe ({tag}) returned no result_payload: {poll}");
        return payload;
    }

    /// <summary>Full bag map from an ActorObservation payload (Newtonsoft CLR
    /// shape: BagItemCounts keyed by template id).</summary>
    private static Dictionary<uint, int> ReadBag(JsonElement observationPayload)
    {
        var bag = new Dictionary<uint, int>();
        if (!observationPayload.TryGetProperty("BagItemCounts", out var map) || map.ValueKind != JsonValueKind.Object)
            return bag;
        foreach (var entry in map.EnumerateObject())
        {
            if (uint.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var template)
                && entry.Value.TryGetInt32(out var count))
                bag[template] = count;
        }
        return bag;
    }

    private static int CountOf(Dictionary<uint, int> bag, uint template)
        => bag.TryGetValue(template, out var count) ? count : 0;

    private static bool BagsEqual(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (template, count) in a)
            if (!b.TryGetValue(template, out var other) || other != count)
                return false;
        return true;
    }

    /// <summary>True when every template in <paramref name="after"/> is >= the
    /// pre value (no template lost units). Reports the first loss.</summary>
    private static bool AllDeltasAreGains(Dictionary<uint, int> before, Dictionary<uint, int> after,
        out uint lossTemplate, out int lossDelta)
    {
        lossTemplate = 0;
        lossDelta = 0;
        foreach (var (template, count) in after)
        {
            var prior = CountOf(before, template);
            if (count < prior)
            {
                lossTemplate = template;
                lossDelta = count - prior;
                return false;
            }
        }
        return true;
    }

    private static string BagText(Dictionary<uint, int> bag)
        => bag.Count == 0
            ? "empty"
            : string.Join(",", bag.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

    // ---- fixture probe helpers (read-only) ---------------------------------

    private readonly record struct CropState(bool Found, uint Template, uint Phase, uint DbId, float X, float Y, float Z);

    /// <summary>Live doodad read through the bridge farm seam (read-only). The
    /// farm status op projects the doodad's world position (x/y/z), so the
    /// re-approach leg can target the crop's ACTUAL position rather than a
    /// remembered one.</summary>
    private static CropState ReadCrop(BotDriveClient bridge, uint objId)
    {
        try
        {
            var status = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{BotName}\",\"objIds\":[{objId}]}}", 30_000);
            var entry = status.GetProperty("doodads").EnumerateArray().First();
            return new CropState(
                entry.GetProperty("found").GetBoolean(),
                GetUInt(entry, "template"),
                GetUInt(entry, "phase"),
                GetUInt(entry, "dbId"),
                GetFloat(entry, "x"),
                GetFloat(entry, "y"),
                GetFloat(entry, "z"));
        }
        catch
        {
            return new CropState(false, 0, 0, 0, 0f, 0f, 0f);
        }
    }

    /// <summary>Effective lane growth rate (evidence only): E2E_GROWTH_RATE
    /// when set, else World.GrowthRate from the adopted lane's game config.
    /// Adopt-only lanes boot outside E2eStack so no override is written and the
    /// key is absent — report the production default 1.0 (Configurations.GrowthRate),
    /// not NaN, so maturitySeconds/growthRate evidence stays interpretable.
    /// Never a gate leg.</summary>
    private static double ReadEffectiveGrowthRate()
    {
        var raw = Environment.GetEnvironmentVariable("E2E_GROWTH_RATE");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var envRate))
            return envRate;
        foreach (var name in new[] { "Config.Local.json", "Config.json" })
        {
            try
            {
                var path = Path.Combine(E2eStack.RuntimeGameDir, name);
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("World", out var world)
                    && world.TryGetProperty("GrowthRate", out var rate)
                    && rate.ValueKind == JsonValueKind.Number
                    && rate.TryGetDouble(out var value))
                    return value;
            }
            catch
            {
            }
        }
        // No key on an adopt-only lane means the production default applies.
        return 1.0;
    }

    /// <summary>Effective lane LootRate (drives the actability-gated pack
    /// groups). E2E_LOOT_RATE when set, else World.LootRate from the adopted
    /// lane's game config, else the production default 1.0
    /// (Configurations.LootRate) — never NaN, so the yield expectations stay
    /// interpretable on an adopt-only lane that booted outside E2eStack.
    /// Mirrors <see cref="ReadEffectiveGrowthRate"/>.</summary>
    private static double ReadEffectiveLootRate()
    {
        var raw = Environment.GetEnvironmentVariable("E2E_LOOT_RATE");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var envRate))
            return envRate;
        foreach (var name in new[] { "Config.Local.json", "Config.json" })
        {
            try
            {
                var path = Path.Combine(E2eStack.RuntimeGameDir, name);
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("World", out var world)
                    && world.TryGetProperty("LootRate", out var rate)
                    && rate.ValueKind == JsonValueKind.Number
                    && rate.TryGetDouble(out var value))
                    return value;
            }
            catch
            {
            }
        }
        // No key on an adopt-only lane means the production default applies.
        return 1.0;
    }

    /// <summary>True when the adopted lane disables labor consumption — a
    /// zero labor delta is then UNKNOWN/CONFIG/labor-void, never behavior.</summary>
    private static bool LaborConsumptionDisabled()
    {
        foreach (var name in new[] { "Config.Local.json", "Config.json" })
        {
            try
            {
                var path = Path.Combine(E2eStack.RuntimeGameDir, name);
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Labor", out var labor)
                    && labor.TryGetProperty("DisableConsumption", out var flag)
                    && flag.ValueKind == JsonValueKind.True)
                    return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private static string ReadPos(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return FormattableString.Invariant($"{x.GetDouble():F1},{y.GetDouble():F1},{z.GetDouble():F1}");
            return el.ToString();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    /// <summary>Flat (XY) distance from a "x,y,z" charPos string to a crop's
    /// world X/Y, in the same units as the engine's MaxInteractRange gate.
    /// NaN when the charPos string is unavailable or malformed.</summary>
    private static double FlatDistToCrop(string charPos, float cropX, float cropY)
    {
        var parts = charPos.Split(',');
        if (parts.Length != 3)
            return double.NaN;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
            return double.NaN;
        return Math.Sqrt(((px - cropX) * (px - cropX)) + ((py - cropY) * (py - cropY)));
    }

    private static string FormatFloat(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- adopt-only lane helpers (G7c/G2 shapes) ---------------------------

    /// <summary>
    /// Stale-proc guard: before the gate adopts the warm lane, ANY process
    /// LISTENING on the shared WebApi port whose cwd is NOT this lane's
    /// E2E_ROOT runtime dir is a foreign leftover (a sibling lane or an
    /// orphaned prior run) that would answer the adopt probes for the wrong
    /// stack. Such a listener is SIGTERM'd, then SIGKILL'd after 10 s if it
    /// is still alive. Only LISTENERS are considered, so the current lane's
    /// own pids are never touched; every kill is recorded as evidence.
    /// </summary>
    private static string GuardSharedWebApiPort()
    {
        var laneRoot = Path.GetFullPath(Path.Combine(E2eStack.E2eRoot, "runtime"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var summary = new List<string>();
        foreach (var pid in ListenersOnPort(E2eStack.WebApiPort))
        {
            try
            {
                var cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget;
                if (cwd == null)
                    continue;
                var full = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);
                var inThisLane = full.Equals(laneRoot, StringComparison.Ordinal)
                    || full.StartsWith(laneRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inThisLane)
                    continue;
                var cmdline = "";
                try { cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
                var line = $"stale-proc-guard: pid={pid} listening :{E2eStack.WebApiPort} cwd={full} (not this lane {laneRoot}) cmd=[{cmdline}] — SIGTERM";
                Console.WriteLine("[w2] " + line);
                summary.Add(line);
                using (var term = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}")
                       { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
                {
                    term!.WaitForExit(5_000);
                }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var killed = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (!Directory.Exists($"/proc/{pid}"))
                    {
                        killed = true;
                        break;
                    }
                    Thread.Sleep(500);
                }
                if (!killed)
                {
                    var kline = $"stale-proc-guard: pid={pid} alive 10s after SIGTERM — SIGKILL";
                    Console.WriteLine("[w2] " + kline);
                    summary.Add(kline);
                    using var hard = Process.Start(new ProcessStartInfo("kill", $"-KILL {pid}")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    hard!.WaitForExit(5_000);
                }
            }
            catch (Exception ex)
            {
                summary.Add($"stale-proc-guard: pid={pid} check/kill failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
        return summary.Count == 0 ? "none" : string.Join(" | ", summary);
    }

    /// <summary>
    /// PIDs holding a LISTEN socket on <paramref name="port"/> (IPv4/IPv6),
    /// read from /proc/net/tcp{,6} + inode → /proc/*/fd. Never matches a mere
    /// outbound client connection, so the current lane's own pids cannot be
    /// returned by accident.
    /// </summary>
    private static List<int> ListenersOnPort(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                var lines = File.ReadAllLines(table);
                for (var i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                        continue;
                    if (!parts[3].Equals("0A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var local = parts[1];
                    var colon = local.LastIndexOf(':');
                    if (colon < 0)
                        continue;
                    if (!int.TryParse(local[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) || p != port)
                        continue;
                    inodes.Add(parts[9]);
                }
            }
            catch
            {
            }
        }
        var pids = new List<int>();
        if (inodes.Count == 0)
            return pids;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
                continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var link = new FileInfo(fd).LinkTarget;
                    if (link == null || !link.StartsWith("socket:[", StringComparison.Ordinal))
                        continue;
                    var inode = link["socket:[".Length..].TrimEnd(']');
                    if (inodes.Contains(inode))
                    {
                        pids.Add(pid);
                        break;
                    }
                }
            }
            catch
            {
            }
        }
        return pids;
    }

    private static async Task<bool> ProbeLaneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                if (bridge.Call("{\"cmd\":\"ping\"}", 3000).GetProperty("pong").GetBoolean())
                    return true;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
    }

    private static void AdoptLaneDbPassword()
    {
        var envFile = Path.Combine(E2eStack.E2eRoot, ".env");
        foreach (var line in File.ReadAllLines(envFile))
        {
            if (!line.StartsWith("DB_PASSWORD="))
                continue;
            var pwd = line["DB_PASSWORD=".Length..].Trim();
            if (string.IsNullOrEmpty(pwd))
                throw new InvalidOperationException($"lane {envFile} has an empty DB_PASSWORD");
            typeof(E2eStack).GetProperty("DbPassword")!.SetValue(null, pwd);
            return;
        }
        throw new InvalidOperationException($"lane {envFile} has no DB_PASSWORD entry");
    }

    /// <summary>
    /// Shared-port routing: every lane game server binds the shared WebApi port
    /// (SO_REUSEPORT), so each TCP connection hashes to ONE lane. A fresh
    /// HttpClient re-hashes; keep the first client whose backend answers
    /// enabled. Correct-lane delivery is proven by the bridge-visible fixture
    /// legs (the planted crop and the observe snapshots must agree).
    /// </summary>
    private static async Task<HttpClient> EnsureWebApiClientAsync(StringBuilder evidence)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var client = NewClient();
            try
            {
                using var response = await client.GetAsync("/api/bots");
                var text = await response.Content.ReadAsStringAsync();
                if (text.Contains("Bot control API is disabled", StringComparison.Ordinal))
                {
                    evidence.AppendLine($"- webapi route attempt {attempt}: disabled backend (re-hashing)");
                    client.Dispose();
                    await Task.Delay(1000);
                    continue;
                }
                evidence.AppendLine($"- webapi route attempt {attempt}: enabled backend");
                return client;
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- webapi route attempt {attempt}: {ex.GetType().Name} (retrying)");
                client.Dispose();
                await Task.Delay(1000);
            }
        }
        throw new InvalidOperationException($"no enabled WebApi backend reachable on the shared {E2eStack.WebApiPort} port after 12 hashed attempts");
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static string TryReleaseBot(BotDriveClient? bridge, string botName, uint characterId)
    {
        if (bridge == null)
            return "UNAVAILABLE (no bridge handle; TCP session disposed, registry entry left in place)";
        try
        {
            var res = bridge.Call($"{{\"cmd\":\"deactivate\",\"bot\":\"{botName}\"}}", 30_000);
            if (res.TryGetProperty("removed", out var removed) && removed.GetBoolean())
                return $"RELEASED via bridge deactivate (charId={characterId})";
            if (res.TryGetProperty("error", out var err))
                return $"UNAVAILABLE (bridge deactivate refused for networked char '{botName}' charId={characterId}: {err.GetString()}; TCP disposed, registry entry left in place)";
            return $"UNAVAILABLE (bridge deactivate unexpected shape for networked char '{botName}' charId={characterId}: {res}; TCP disposed, registry entry left in place)";
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE (bridge deactivate threw {ex.GetType().Name} for networked char '{botName}' charId={characterId}; TCP disposed, registry entry left in place)";
        }
    }

    // ---- HTTP helpers -------------------------------------------------------

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PollTerminalAsync(HttpClient client, Guid traceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement.Clone();
                var state = root.GetProperty("state").GetString();
                if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return root;
            }
            await Task.Delay(300);
        }
        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout}");
    }

    // ---- JSON readers -------------------------------------------------------

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : 0;

    private static float GetFloat(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var n) ? n : 0f;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long GetInt64(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- report (K4 artifact preservation) ---------------------------------

    private async Task WriteReportAsync(bool passed, string verdict, string failBoundary, string claim, object? failingCondition,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText, object fixture,
        double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "w2-harvest-single-use",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[]
                {
                    "plant capability (POST /api/actors/plant is Wave B's claim)",
                    "watering/rot/wilt chains",
                    "livestock funcs (log-only stubs)",
                    "autonomy/loop",
                    "a second crop cycle"
                },
                callPath = "fixture: bridge farm rig + bridge farm plant (GameplayActor.Plant → DoodadManager.CreatePlayerDoodad) + read-only farm status/find polls + bridge farm place RE-APPROACH (closes the engine's 25 m interaction gate after the maturity wait); verb: POST /api/actors/harvest → BotActionSpec(Harvest) → BotActionCommandQueue → GameplayActor.Harvest → resolve/range/despawn/phase-skill pre-validation → doodad.Use(Character, 13980) (4457 → DoodadFuncUse 5887 → 4458 DoodadFuncLootPack 129 → pack 6452 → 4459 → deleted) → ChangeLabor(-1) → post-state verify → Complete(yieldDelta); the gate adds no other verbs. YIELD RULE: pack 6452 potato (group 1) is ungated in LootPack.GeneratePackNewV2 and MUST always drop; seed (group 3) / golden (group 2) are actability-dice gated (~3.5% at LootRate 1.0) and are REQUIRED only at effective LootRate >= 10 (E2eStack writes World.LootRate 100)",
                failingCondition,
                timings = new
                {
                    setupSeconds = Math.Round(setupSeconds, 1),
                    execSeconds = Math.Round(execSeconds, 1),
                    wallSeconds = Math.Round(wallSeconds, 1)
                },
                legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                fixture,
                provenance = new
                {
                    sourceRevision = E2eStack.SourceRevision,
                    runtimeSqliteMd5 = E2eStack.RuntimeSqliteMd5(),
                    canonicalSqliteMd5 = E2eStack.CanonicalSqliteMd5
                },
                evidence = evidenceText
            };
            // K4 artifact preservation: every run writes a new timestamped file
            // (logs/w2-harvest-report.<utc>.json); the bare w2-harvest-report.json
            // is only a latest-pointer COPY refreshed here (copy, not symlink, so
            // the pointer survives stamped-file cleanup and needs no privilege).
            // A rerun therefore never destroys a prior report.
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
            var stampedPath = Path.Combine(EvidenceDir,
                Path.GetFileNameWithoutExtension(ReportPath) + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, ReportPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"W2-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
