using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// WAVE B / W4 — SELL capability + BuyBack non-persistence gate (selling TO a
/// live merchant, NOT autonomy).
///
/// Call path under test (code-verified 2026-09-23):
///   POST /api/actors/sell {bot, merchantNpcObjId, itemId, idempotencyKey}
///     → BotActionController.Sell → BotActionSpec(Sell, SellActionParams)
///     → BotActionCommandQueue.ExecuteKind (case BotActionKind.Sell) →
///       IGameplayActor.Sell
///     → GameplayActor.Sell: live-NPC + Template.Merchant gate →
///       item in OWN Bag/Equipment by INSTANCE id → Template.Sellable →
///       grade template → refund = (int)(Refund * gradeMult / 100f) * Count →
///       BuyBackItems.AddOrMoveExistingItem(StoreSell) (REMOVES the whole stack
///       from the bag) → ItemManager.MarkItemForDbDeletion(item.Id) →
///       ChangeMoney(SlotType.Inventory, refund) → SCSoldItemListPacket →
///       Complete(refund) (result payload = the paid copper).
///
/// PASS needs ALL of:
///   1. one Completed SELL of a GENUINE harvest-derived 7992 stack (potato,
///      refund 10, sellable 't') to live merchant 8522 → observed money +
///      refundExpected EXACTLY (refundExpected computed from the runtime
///      compact.sqlite3 refund/refund_multiplier inputs and the OBSERVED
///      instance count, never from the dice) + the instance's whole stack gone
///      from the bag (bag 7992 → 0, not count-1) + bank money / warehouse map /
///      labor byte-unchanged + result payload == refundExpected and
///      actor_id == bot world objId;
///   2. BuyBack NON-PERSISTENCE: the instance HAD a persisted items row before
///      the sell (bridge `save`), and after the sell + a bridge `save` the row
///      is GONE (COUNT(*) FROM items WHERE id = <instance> == 0) and does not
///      reappear on a second save, while characters.money == the post-sell
///      balance;
///   3. the SAME idempotency key retried → Rejected by the DEDUPE gate
///      ("duplicate idempotency key") with a zero delta;
///   4. THREE refusal legs, each REQUIRED Rejected with ZERO delta on money /
///      bag map / bank map / bank money / labor:
///        (a) a foreign itemId (never in the inventory) → "not found in inventory";
///        (b) the unsellable 1252 instance (items.sellable = 'f') → "is not sellable";
///        (c) the bot's own objId as the target (not a merchant) → "not found or not a merchant";
///   5. a 30 m FAR-MERCHANT leg (W1 far-stage pattern) that is EXPECTED to
///      COMPLETE — see the FINDING below.
///
/// FINDING (code-verified, reported as a finding, NEVER as a capability proof):
///   Sell has NO range gate. <c>CSSellItemsPacket</c> checks only the merchant
///   template and <c>GameplayActor.Sell</c> steps 1–5 carry no distance check —
///   there is no sell counterpart to the Buy path's
///   <c>GameplayActor.MaxShopRange</c> (3 m). A far-merchant refusal therefore
///   CANNOT be produced. W4 instead runs the 30 m leg and asserts
///   Completed + exact refund, labelled
///   <c>SELL/range-gate-absent-finding</c>: the leg's PASS is evidence of the
///   missing gate, and it is recorded in the report's findings, never claimed
///   as a refusal or as the sell capability itself.
///
/// FIXTURE (disclosed; bridge seams only — the gate NEVER issues a bridge verb
/// that mirrors the verb under test, i.e. the production Sell is only ever
/// reached through POST /api/actors/sell):
///   farm rig {seeds:3, labor:5000} → farm plant {seed 15659} (fixture crop,
///   the W2 seam) → maturity poll to phase 4457 → farm place RE-APPROACH onto
///   the crop's live position (closes the bridge-harvest 25 m interaction
///   gate) → farm harvest → a genuine harvest-derived 7992 stack (dice-gated
///   count, LootRate-independent group) → mail stock {1252, count:1} (the
///   unsellable leg) → mail rig {money:10000} (a known copper balance) →
///   drive teleportToNpc 8522 (the merchant spawns through the world's normal
///   spawn path; W1 proved it resolves at 30 m).
///   ITEM INSTANCES are read from the bridge `mail inv` projection (instance
///   itemId + count + grade) because Sell is keyed by INSTANCE id, not
///   template id.
///
/// REFUND FORMULA (read from the runtime compact.sqlite3, the same data
/// ItemManager loads): items.refund(7992) = 10, item_grades.refund_multiplier
/// [observed grade] — refund = (int)(refund * multiplier / 100f) * count, the
/// packet's exact int-truncating expression.
///
/// LANE: adopt-only. Never EnsureUp, never RestartGameServer — a cold lane is
/// an honest SETUP/lane-down FAIL, never a rebuild. The stale-proc guard
/// SIGTERMs (then SIGKILLs) any listener on the shared WebApi port whose cwd
/// is outside this lane's runtime dir before the adopt probes run.
///
/// CLAIM (only): the production actor Sell capability pays exactly the packet's
/// refund for a real harvest-derived stack, removes that whole stack from the
/// bag, persists money while the BuyBack row is deleted (non-persistence), is
/// dedupe-guarded on a same-key retry, and three independent engine refusals
/// conserve every observed balance.
/// NOT claimed: buy, buyback re-purchase, wire CSSellItemsPacket parity,
/// restart/relogin buyback wipe (B6 territory), multi-item atomicity, autonomy.
/// </summary>
[Collection("e2e")]
public sealed class W4SellCapabilityGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "W4Sell" + Stamp;
    private static readonly string BotAccount = ("w4sell" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    // Canonical 1.2 ids (compact.sqlite3 verified): merchant 8522 → pack 171
    // sells seed 15659; 7992 (potato) is sellable 't' refund 10; 1252
    // (부드러운 꼬리털) is sellable 'f' refund 0.
    private const uint MerchantTemplate8522 = 8522;
    private const uint Potato7992 = 7992;
    private const uint Unsellable1252 = 1252;
    private const uint PotatoSeed15659 = 15659;
    private const uint CropDoodad2259 = 2259;
    private const uint SeedlingPhase4379 = 4379;
    private const uint MaturePhase4457 = 4457;
    private const int RiggedSeeds = 3;
    private const int RiggedLabor = 5000;
    private const int FundedMoney = 10_000;
    /// <summary>An instance id that can never exist: sell must refuse it as
    /// "not found in inventory" (also reused as the payload of the non-merchant
    /// leg, where the merchant gate must fire FIRST).</summary>
    private const ulong ForeignItemId = 999_999_999UL;
    private const int SlotTypeInventory = 2;

    /// <summary>Maturity budget (fixed 660s), identical to the W2 seam: the
    /// 4379→4457 chain is two growth legs (60s + 540s at GrowthRate 1.0) and an
    /// adopt-only lane boots outside E2eStack, so no World.GrowthRate override
    /// is written. 660s = 600s chain + 60s poll margin.</summary>
    private const int MaturityBudgetMs = 660_000;
    /// <summary>The engine's own bridge-harvest interaction gate (25 m flat) —
    /// used only to prove the fixture re-approach closed it before the bridge
    /// `farm harvest` seam runs. Sell itself has NO range gate (see FINDING).</summary>
    private const float MaxHarvestGateM = AAEmu.Game.Core.Managers.Bots.GameplayActor.MaxInteractRange;
    /// <summary>The BUY path's shop gate. Recorded as the SELL counterfactual:
    /// Sell has no such check, so the 30 m leg is expected to COMPLETE.</summary>
    private const float MaxShopRangeM = AAEmu.Game.Core.Managers.Bots.GameplayActor.MaxShopRange;
    private const float FarOffsetM = 30f;
    private const int ActionDeadlineSeconds = 60;
    private const int WakeWaitMs = 1000;
    /// <summary>The lane's autonomy probe: a fresh account must not already
    /// hold quest 251 after the enroll wake — an active quest means autonomy
    /// pre-empted the explicit leg (never this gate's claim).</summary>
    private const uint AutonomyQuest251 = 251;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "w4-sell-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task SellHarvestedStack_ExactRefundBuyBackNonPersistence_RefusalsZeroDeltaAndRangeFinding()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var execSw = new Stopwatch();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# W4 sell capability + BuyBack non-persistence gate — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);

        BotNetworkSession? session = null;
        BotDriveClient? laneBridge = null;
        HttpClient? http = null;
        var setupSeconds = 0.0;
        var execSeconds = 0.0;
        var passed = false;
        var verdict = "UNKNOWN";
        var failBoundary = "";
        var claim = "production GameplayActor.Sell pays exactly the packet refund (int truncation) for a real harvest-derived 7992 stack to live merchant 8522, removes that whole stack from the bag, persists money while deleting the BuyBack row (non-persistence), dedupe-refuses a same-key retry, and three independent engine refusals conserve every observed balance";
        object? failingCondition = null;
        var findings = new List<string>();
        string cleanupSummary = "not-attempted";

        uint charId = 0;
        uint botObjId = 0;
        uint merchantObjId = 0;
        var merchantPos = "UNAVAILABLE";
        var charPos = "UNAVAILABLE";
        var merchantX = double.NaN;
        var merchantY = double.NaN;
        var merchantZ = double.NaN;
        var startDist = double.NaN;

        // ---- farm fixture state ----
        uint cropObjId = 0;
        uint cropDbId = 0;
        uint plantedTemplate = 0;
        uint plantedPhase = 0;
        var phaseTrail = new List<uint>();
        var maturitySeconds = 0.0;
        var growthRate = double.NaN;
        var harvestState = "";
        var harvestDetail = "";
        var harvestYield = int.MinValue;
        var reapproachPos = "UNAVAILABLE";
        var reapproachDist = double.NaN;

        // ---- instance state ----
        var harvestInstanceItemId = 0UL;
        var harvestCount = 0;
        var harvestGrade = -1;
        var unsellableInstanceItemId = 0UL;
        var unsellableCount = 0;
        long refundDb = long.MinValue;
        long multiplierDb = long.MinValue;
        long refundExpected = long.MinValue;

        ObsSnap? startObs = null;
        ObsSnap? postSell = null;
        var sellState = "NOT-ATTEMPTED";
        var sellFailure = "NONE";
        var sellDetail = "";
        var sellAudit = "UNAVAILABLE";
        long refundObserved = long.MinValue;
        uint actorIdEcho = 0;
        var moneyAfterSell = long.MinValue;
        var bagInstanceGone = false;

        var preSaveState = "NOT-ATTEMPTED";
        var itemRowCountPreSell = -1L;
        var itemRowCountAfterSave = -1L;
        var itemRowCountAfterSecondSave = -1L;
        var persistedMoneyAfterSave = long.MinValue;

        var retryState = "";
        var retryFailure = "";
        var retryDetail = "";
        var retryRefusalKind = "UNOBSERVED";
        var refusals = new List<SellLegRecord>();

        var farItemId = 0UL;
        var farCount = 0;
        var farGrade = -1;
        long farRefundExpected = long.MinValue;
        long farRefundObserved = long.MinValue;
        var farState = "";
        var farDetail = "";
        var farPos = "UNAVAILABLE";
        var farDist = double.NaN;
        var farMoneyDelta = long.MinValue;

        void Fail(string v, string b, object? condition, string detail, double ms)
        {
            verdict = v;
            failBoundary = b;
            failingCondition = condition;
            Leg("gate", false, detail, ms);
            Assert.Fail($"W4 {v} at {b}: {detail}");
        }

        try
        {
            // ---- ADOPT (probe-only; never EnsureUp/Restart — see class doc) ----
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = KillForeignWebApiListeners();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "warm lane answering (WebApi TCP + bridge ping)" : "lane cold — refusing to rebuild (see class doc)",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "SETUP/lane-down";
                Assert.Fail("warm lane not answering; refusing to rebuild per workstream constraints");
            }
            // LANE PASSWORD ADOPT: mirrors EnsureEnvFile's DB_PASSWORD read
            // (E2eStack.cs) without rebuilding the stack.
            AdoptLaneDbPassword();

            var bridge = new BotDriveClient(E2eStack.BridgePort);
            laneBridge = bridge;

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
            charId = session.CharacterId;
            Leg("enter-world", true, $"inWorld={session.InWorld} charId={charId}", setupSw.Elapsed.TotalSeconds);

            // ---- ENROLL: registers/activates the runtime the /api/actors
            // queue needs; the wake is probe-only (no gameplay command). ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 60_000);
            var enrollId = GetUInt(enroll, "id");
            if (enrollId != 0)
                charId = enrollId;
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, AutonomyQuest251);
            Leg("enroll", charId != 0 && !activeBefore,
                $"charId={charId} activeBefore={activeBefore} (must be False: no autonomy claim)",
                enrollSw.Elapsed.TotalSeconds);
            if (charId == 0)
            {
                failBoundary = "SETUP/enroll-unresolved";
                Assert.Fail("enroll wake returned no character id — the /api/actors queue cannot address this bot");
            }
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest 251 already active after enroll wake — autonomy pre-empted the explicit leg; re-run with a fresh account");
            }

            // ---- WEBAPI ROUTE: the shared :WebApiPort is Kestrel
            // SO_REUSEPORT across lane game servers, so each fresh TCP
            // connection hashes to ONE lane. The correct backend is the one
            // that accepts an enqueue for OUR bot name (only the lane holding
            // our live session knows it) — bounded re-hash, never a verdict.
            http = await RouteToBotAsync(BotName, evidence);
            if (http == null)
            {
                failBoundary = "SETUP/webapi-route";
                Assert.Fail("no WebApi backend accepted an enqueue for our bot after 12 hashed attempts (disabled or foreign lane)");
            }

            // ---- FIXTURE (PRE-START, disclosed): a known copper balance ----
            var fixSw = Stopwatch.StartNew();
            var moneyRig = bridge.Call(
                $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"money\":{FundedMoney}}}", 30_000);
            var rigMoney = moneyRig.TryGetProperty("money", out var rmEl) ? rmEl.GetInt64() : long.MinValue;
            Leg("fixture-money", rigMoney == FundedMoney,
                $"mail rig money={FundedMoney} (bridge reported {rigMoney})", fixSw.Elapsed.TotalSeconds);
            if (rigMoney != FundedMoney)
            {
                failBoundary = "SETUP/rig-refused";
                Assert.Fail($"mail rig money={FundedMoney} not honored (bridge reported {rigMoney})");
            }

            // ---- FIXTURE: rig seeds + labor for the W2 plant seam ----
            var rigSw = Stopwatch.StartNew();
            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"seeds\":{RiggedSeeds},\"calves\":0,\"labor\":{RiggedLabor}}}",
                60_000);
            var riggedSeeds = GetInt(rig, "seeds");
            var riggedLabor = GetInt(rig, "labor");
            Leg("fixture-rig", riggedSeeds >= RiggedSeeds && riggedLabor == RiggedLabor,
                $"seeds={riggedSeeds} (REQUIRE >= {RiggedSeeds}) labor={riggedLabor} (REQUIRE {RiggedLabor})",
                rigSw.Elapsed.TotalSeconds);
            if (riggedSeeds < RiggedSeeds || riggedLabor != RiggedLabor)
            {
                failBoundary = "SETUP/rig-ineffective";
                Assert.Fail($"fixture rig ineffective (seeds={riggedSeeds} labor={riggedLabor})");
            }

            // ---- FIXTURE: plant the crop through the BRIDGE farm seam (the
            // production GameplayActor.Plant path B2/B4/W2 use). POST
            // /api/actors/plant is Wave B's W3 claim and is deliberately NOT
            // exercised; this crop exists ONLY to produce a genuine
            // harvest-derived 7992 stack for the sell gate. ----
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
                    interpretation = "the bridge plant seam produced no 2259 crop in phase 4379 — the fixture never reached the pre-maturity chain; refusing to observe a sell"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: objId={cropObjId} template={plantedTemplate} phase={plantedPhase} detail=[{plantDetail}]");
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
                    phaseTrail.Add(0);
                    break;
                }
                if (phaseTrail[^1] != crop.Phase)
                    phaseTrail.Add(crop.Phase);
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
                    interpretation = "the fixture crop never reached mature phase 4457 inside the budget (or its phase trail regressed) — the harvest precondition for a genuine 7992 stack is unproven"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: objId={cropObjId} trail=[{string.Join("->", phaseTrail)}] in {maturitySeconds:0.0}s");
            }

            // ---- FIXTURE: RE-APPROACH (harness precondition, never a behavior
            // claim). The minutes-long maturity wait lets lane autonomy walk
            // the bot off the plant position, which would leave the crop
            // outside the BRIDGE HARVEST's own 25 m interaction gate
            // (GameplayActor.MaxInteractRange) and the fixture harvest would be
            // engine-refused on range. The harness re-places the bot onto the
            // crop's LIVE position through the bridge `farm place` staging op
            // and PROVES the flat distance before harvesting. ----
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
                $"crop=[{approachCrop.X:0.0},{approachCrop.Y:0.0},{approachCrop.Z:0.0}] place->[{reapproachPos}] verifiedCrop=[{verifiedCrop.X:0.0},{verifiedCrop.Y:0.0}] flat={reapproachDist:0.00}m (REQUIRE <= {MaxHarvestGateM:0.0}m bridge-harvest gate) placeEcho=[{place}]",
                reapproachSw.Elapsed.TotalSeconds);
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
                    interpretation = "the bot could not be brought inside the bridge-harvest interaction gate after the maturity wait — the fixture harvest would be engine-refused on range, so this is a harness precondition failure, never a capability verdict"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: objId={cropObjId} charPos=[{reapproachPos}] cropPos=[{approachCrop.X:0.###},{approachCrop.Y:0.###}] flat={reapproachDist:0.00}m");
            }

            // ---- FIXTURE: harvest the mature crop through the BRIDGE farm
            // seam (a fixture, NOT the verb under test — the verb under test is
            // Sell) so the sold 7992 stack is GENUINELY harvest-derived. ----
            var harvestSw = Stopwatch.StartNew();
            var harvest = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"harvest\",\"bot\":\"{BotName}\",\"objId\":{cropObjId}}}", 60_000);
            harvestState = GetStr(harvest, "state");
            harvestDetail = GetStr(harvest, "detail");
            harvestYield = GetInt(harvest, "yield");
            Leg("fixture-harvest", harvestState == "Completed",
                $"bridgeHarvestVia=farm-harvest objId={cropObjId} state={harvestState} yield={harvestYield} phaseAfter={GetUInt(harvest, "phaseAfter")} deleted={harvest.TryGetProperty("deleted", out var dlEl) && dlEl.GetBoolean()} detail=[{harvestDetail}]",
                harvestSw.Elapsed.TotalSeconds);
            if (harvestState != "Completed")
            {
                failBoundary = "HARNESS/harvest-not-completed";
                verdict = "UNKNOWN/HARNESS/harvest-not-completed";
                failingCondition = new
                {
                    boundary = failBoundary,
                    cropObjId,
                    state = harvestState,
                    detail = harvestDetail,
                    interpretation = "the bridge harvest seam never produced a harvest-derived stack — the W4 sell fixture precondition is unproven"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: state={harvestState} detail=[{harvestDetail}]");
            }

            // ---- FIXTURE: the harvest-derived INSTANCE (Sell is keyed by
            // instance id, not template id) + its grade. ----
            var stackSw = Stopwatch.StartNew();
            var harvested = FindBagInstance(bridge, Potato7992, 0UL);
            harvestInstanceItemId = harvested.ItemId;
            harvestCount = harvested.Count;
            harvestGrade = harvested.Grade;
            Leg("fixture-harvest-stack", harvested.Found && harvestCount >= 1,
                $"harvestDerivedInstance itemId={harvestInstanceItemId} template={Potato7992} count={harvestCount} grade={harvestGrade} (REQUIRE count >= 1; loots 65672 min 2 max 4)",
                stackSw.Elapsed.TotalSeconds);
            if (!harvested.Found || harvestCount < 1)
            {
                failBoundary = "HARNESS/instance-not-found";
                verdict = "UNKNOWN/HARNESS/instance-not-found";
                failingCondition = new
                {
                    boundary = failBoundary,
                    template = Potato7992,
                    bagInstance = harvested,
                    harvestYield,
                    interpretation = "no harvest-derived 7992 bag instance was resolvable by instance id — Sell cannot be aimed at an instance; fixture precondition gap, never a behavior verdict"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: template={Potato7992} harvestYield={harvestYield}");
            }

            // ---- FIXTURE: the unsellable 1252 instance (leg b). ----
            var stockSw = Stopwatch.StartNew();
            var stock = bridge.Call(
                $"{{\"cmd\":\"mail\",\"op\":\"stock\",\"bot\":\"{BotName}\",\"itemTemplate\":{Unsellable1252},\"count\":1}}", 30_000);
            var unsellable = FindBagInstance(bridge, Unsellable1252, 0UL);
            unsellableInstanceItemId = unsellable.ItemId;
            unsellableCount = unsellable.Count;
            Leg("fixture-unsellable", unsellable.Found && unsellableCount >= 1,
                $"unsellableVia=mail-stock template={Unsellable1252} (items.sellable='f') itemId={unsellableInstanceItemId} count={unsellableCount} stockEcho=[{stock}]",
                stockSw.Elapsed.TotalSeconds);
            if (!unsellable.Found || unsellableCount < 1)
            {
                failBoundary = "HARNESS/unsellable-rig-ineffective";
                verdict = "UNKNOWN/HARNESS/unsellable-rig-ineffective";
                failingCondition = new
                {
                    boundary = failBoundary,
                    template = Unsellable1252,
                    bagInstance = unsellable,
                    interpretation = "the unsellable 1252 fixture instance never landed in the bag — the 'is not sellable' refusal leg cannot be staged"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: template={Unsellable1252}");
            }

            // ---- FIXTURE: stand ON the live merchant's spawner (the normal
            // spawn path teleportToNpc uses), then poll the live objId. ----
            var merchantSw = Stopwatch.StartNew();
            var tp = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{MerchantTemplate8522}}}", 60_000);
            merchantX = GetDbl(tp, "x");
            merchantY = GetDbl(tp, "y");
            merchantZ = GetDbl(tp, "z");
            merchantPos = FormattableString.Invariant($"{merchantX:0.0},{merchantY:0.0},{merchantZ:0.0}");
            merchantObjId = PollNpcObjId(bridge, MerchantTemplate8522, 30);
            if (merchantObjId == 0)
            {
                failBoundary = "HARNESS/merchant-unresolved";
                Leg("fixture-merchant", false,
                    $"merchant template {MerchantTemplate8522} never materialized (objId=0 after 30s poll)",
                    merchantSw.Elapsed.TotalSeconds);
                Assert.Fail($"merchant NPC template {MerchantTemplate8522} never spawned in the live world");
            }
            charPos = ReadPos(bridge);
            startDist = FlatDist(charPos, merchantPos);
            botObjId = ReadBotObjId(bridge);
            Leg("fixture-merchant", true,
                $"merchantObjId={merchantObjId} template={MerchantTemplate8522} spawner=[{merchantPos}] char=[{charPos}] flat={startDist:0.00}m botObjId={botObjId} (NOTE: Sell has NO shop-range gate — this distance is context, never a precondition; the BUY gate is {MaxShopRangeM:0.0}m)",
                merchantSw.Elapsed.TotalSeconds);

            // ---- START SNAPSHOT (ONE authoritative read, immediately
            // pre-verb) + the refund FORMULA INPUTS read from the runtime
            // compact.sqlite3 (the same data ItemManager loads). ----
            var startSw = Stopwatch.StartNew();
            startObs = await ReadObsAsync(http, BotName, evidence, "start");
            refundDb = SqliteScalarLong("SELECT refund FROM items WHERE id = $item", ("$item", Potato7992));
            multiplierDb = SqliteScalarLong("SELECT refund_multiplier FROM item_grades WHERE id = $grade", ("$grade", harvestGrade));
            var refundInputsOk = refundDb != long.MinValue && multiplierDb != long.MinValue;
            if (refundInputsOk)
            {
                // The packet's EXACT expression: (int)(Refund * gradeMult / 100f) * Count.
                var perUnit = (int)((int)refundDb * (int)multiplierDb / 100f);
                refundExpected = (long)perUnit * harvestCount;
            }
            Leg("start-snapshot", refundInputsOk,
                $"instance itemId={harvestInstanceItemId} template={Potato7992} count={harvestCount} grade={harvestGrade} refundDb={refundDb} refundMultiplierDb={multiplierDb} refundExpected={refundExpected} " +
                $"charId={charId} botObjId={botObjId} merchant={merchantObjId}/{MerchantTemplate8522} flat={startDist:0.00}m",
                startSw.Elapsed.TotalSeconds);
            evidence.AppendLine($"- START obs: {startObs.Summary}");
            if (!refundInputsOk)
            {
                failBoundary = "HARNESS/refund-inputs-unavailable";
                verdict = "UNKNOWN/HARNESS/refund-inputs-unavailable";
                failingCondition = new
                {
                    boundary = failBoundary,
                    grade = harvestGrade,
                    refundDb,
                    refundMultiplierDb = multiplierDb,
                    interpretation = "items.refund / item_grades.refund_multiplier were not resolvable from the runtime compact.sqlite3 — the exact-refund claim cannot be computed independently of the server"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: grade={harvestGrade} refund={refundDb} multiplier={multiplierDb}");
            }

            // ---- BUYBACK PRE-CONDITION (disclosed harness step): a real save
            // pass FIRST, so the instance provably HAS a persisted items row.
            // Without it, "the row is gone after the sell" would be vacuous
            // (the row might never have been written). ----
            var preSaveSw = Stopwatch.StartNew();
            var preSave = bridge.Call("{\"cmd\":\"save\"}", 180_000);
            preSaveState = preSave.TryGetProperty("saved", out var psEl) && psEl.GetBoolean() ? "saved" : "no-pass";
            itemRowCountPreSell = QueryItemRowCount(harvestInstanceItemId);
            var preRowOk = preSaveState == "saved" && itemRowCountPreSell >= 1;
            Leg("buyback-pre-sell-row", preRowOk,
                $"bridgeSave={preSaveState} items row COUNT(*) for instance {harvestInstanceItemId} = {itemRowCountPreSell} (REQUIRE >= 1: the instance must HAVE a persisted row so its post-sell deletion is a real proof)",
                preSaveSw.Elapsed.TotalSeconds);
            if (!preRowOk)
            {
                failBoundary = "HARNESS/row-absent-pre-sell";
                verdict = "UNKNOWN/HARNESS/row-absent-pre-sell";
                failingCondition = new
                {
                    boundary = failBoundary,
                    instanceItemId = harvestInstanceItemId,
                    saveState = preSaveState,
                    itemRowCountPreSell,
                    interpretation = "the harvested instance had no persisted items row before the sell (or the save pass never landed), so a post-sell row absence cannot prove BuyBack non-persistence"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: instance={harvestInstanceItemId} save={preSaveState} rowCount={itemRowCountPreSell}");
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- VERB: POST /api/actors/sell (the capability under test) ----
            execSw.Start();
            var sellKey = $"w4-sell-{BotAccount}";
            var sellBody = $"{{\"bot\":\"{BotName}\",\"merchantNpcObjId\":{merchantObjId},\"itemId\":{harvestInstanceItemId},\"idempotencyKey\":\"{sellKey}\"}}";
            var sell = await PostJsonAsync(http, "/api/actors/sell", sellBody);
            var sellTrace = sell.GetProperty("trace_id").GetGuid();
            JsonElement poll;
            try
            {
                poll = await PollTerminalAsync(http, sellTrace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
            }
            catch (TimeoutException ex)
            {
                Fail("UNKNOWN/HARNESS/ACTION-DEADLINE", "RUN/action-deadline",
                    new { boundary = "RUN/action-deadline", trace = sellTrace.ToString(), budgetSeconds = ActionDeadlineSeconds, interpretation = ex.Message },
                    $"sell action never reached a terminal state within {ActionDeadlineSeconds}s (trace {sellTrace})",
                    execSw.Elapsed.TotalSeconds);
                return;
            }
            sellState = GetStr(poll, "state");
            sellFailure = GetStr(poll, "failure");
            sellDetail = GetStr(poll, "detail");
            actorIdEcho = GetUInt(poll, "actor_id");
            sellAudit = poll.TryGetProperty("audit", out var auditEl) ? auditEl.ToString() : "UNAVAILABLE (no audit payload)";
            if (poll.TryGetProperty("result_payload", out var rpEl) && rpEl.ValueKind == JsonValueKind.Number && rpEl.TryGetInt64(out var refund))
                refundObserved = refund;
            else if (rpEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                refundObserved = long.MinValue;

            if (sellState != "Completed")
            {
                Fail("FAIL-BEHAVIOR/NO-CAPABILITY", "SELL/refused",
                    new { boundary = "SELL/refused", state = sellState, failure = sellFailure, detail = sellDetail, trace = sellTrace.ToString(), merchantObjId, instanceItemId = harvestInstanceItemId, template = Potato7992, count = harvestCount, grade = harvestGrade, expected = "Completed" },
                    $"sell state={sellState} failure=[{sellFailure}] detail=[{sellDetail}] (REQUIRE Completed)",
                    execSw.Elapsed.TotalSeconds);
            }
            Leg("sell-verb", true,
                $"state={sellState} detail=[{sellDetail}] refundPayload={refundObserved} trace={sellTrace}",
                execSw.Elapsed.TotalSeconds);

            // ---- OBSERVE-ONLY CONSEQUENCES (no further gameplay verb) ----
            postSell = await ReadObsAsync(http, BotName, evidence, "post-sell");
            moneyAfterSell = postSell.Money;
            var postInstance = FindBagInstance(bridge, Potato7992, 0UL);
            bagInstanceGone = postInstance.ItemId != harvestInstanceItemId
                && !BagContainsInstance(bridge, harvestInstanceItemId);
            evidence.AppendLine($"- POST obs: {postSell.Summary}; bag 7992 instance {harvestInstanceItemId} present={!bagInstanceGone}");

            if (postSell.Money != startObs.Money + refundExpected)
            {
                Fail("FAIL-BEHAVIOR/REFUND-NOT-EXACT", "SELL/refund-not-exact",
                    new { boundary = "SELL/refund-not-exact", moneyBefore = startObs.Money, moneyAfter = postSell.Money, observedRefund = postSell.Money - startObs.Money, refundExpected, refundInputs = new { refundDb, multiplierDb, count = harvestCount, grade = harvestGrade }, interpretation = "the paid refund must equal (int)(items.refund * item_grades.refund_multiplier / 100f) * count for the OBSERVED instance count/grade" },
                    $"money {startObs.Money} -> {postSell.Money}; REQUIRE exactly {startObs.Money + refundExpected} (refund {postSell.Money - startObs.Money}, expected {refundExpected})",
                    execSw.Elapsed.TotalSeconds);
            }
            if (postSell.PotatoCount != startObs.PotatoCount - harvestCount || !bagInstanceGone)
            {
                Fail("FAIL-BEHAVIOR/BAG-NOT-CONSUMED", "SELL/bag-not-consumed",
                    new { boundary = "SELL/bag-not-consumed", template = Potato7992, bagBefore = startObs.PotatoCount, bagAfter = postSell.PotatoCount, stackCount = harvestCount, instanceItemId = harvestInstanceItemId, instanceStillInBag = !bagInstanceGone, bagBeforeMap = startObs.BagMap, bagAfterMap = postSell.BagMap, interpretation = "Sell moves the WHOLE instance (every unit of the stack) into BuyBack; a count-1 delta or a surviving instance means the wrong quantity left the bag" },
                    $"bag 7992 {startObs.PotatoCount} -> {postSell.PotatoCount} (REQUIRE -{harvestCount}); instance {harvestInstanceItemId} still in bag={!bagInstanceGone}",
                    execSw.Elapsed.TotalSeconds);
            }
            if (postSell.BankMoney != startObs.BankMoney || postSell.Labor != startObs.Labor || postSell.BankMap != startObs.BankMap)
            {
                Fail("FAIL-BEHAVIOR/COLLATERAL-DELTA", "SELL/collateral-delta",
                    new { boundary = "SELL/collateral-delta", bankBefore = startObs.BankMoney, bankAfter = postSell.BankMoney, laborBefore = startObs.Labor, laborAfter = postSell.Labor, bankMapBefore = startObs.BankMap, bankMapAfter = postSell.BankMap, interpretation = "a sale must move only the sold instance out of the bag and the refund into inventory copper" },
                    $"bank/labor/warehouse moved: bank {startObs.BankMoney}->{postSell.BankMoney} labor {startObs.Labor}->{postSell.Labor}",
                    execSw.Elapsed.TotalSeconds);
            }
            // actor_id echoes the acting actor's world objId (GameplayActor.ActorId
            // => Character.ObjId), NOT the session charId.
            if (refundObserved != refundExpected || actorIdEcho != botObjId)
            {
                Fail("UNKNOWN/HARNESS/RESULT-MISMATCH", "SELL/result-mismatch",
                    new { boundary = "SELL/result-mismatch", refundObserved, refundExpected, actorIdEcho, botObjId, charId, interpretation = "the completed result payload contradicts the independently observed effect (refund and/or acting character); the observed money delta is exact so the behavior is not failed, but two authoritative surfaces disagree" },
                    $"result payload refund={refundObserved} (expected {refundExpected}) actor_id={actorIdEcho} (botObjId {botObjId})",
                    execSw.Elapsed.TotalSeconds);
            }
            Leg("sell-outcome", true,
                $"money {startObs.Money}->{postSell.Money} (exact +{refundExpected}) bag7992 {startObs.PotatoCount}->{postSell.PotatoCount} instanceGone={bagInstanceGone} " +
                $"bank {startObs.BankMoney}->{postSell.BankMoney} labor {startObs.Labor}->{postSell.Labor} refundPayload={refundObserved}",
                0);

            // ---- BUYBACK NON-PERSISTENCE: save → the instance's row is DELETED
            // (ItemManager.MarkItemForDbDeletion queued it) while the new money
            // balance IS persisted; a second save must not resurrect the row
            // (BuyBack is a SlotType.None container and the item's SlotType
            // cannot be re-attained, so the insert loop skips it). ----
            var postSaveSw = Stopwatch.StartNew();
            var postSave = bridge.Call("{\"cmd\":\"save\"}", 180_000);
            var postSaveState = postSave.TryGetProperty("saved", out var ps2El) && ps2El.GetBoolean() ? "saved" : "no-pass";
            itemRowCountAfterSave = QueryItemRowCount(harvestInstanceItemId);
            persistedMoneyAfterSave = QueryPersistedMoney(charId);
            var secondSave = bridge.Call("{\"cmd\":\"save\"}", 180_000);
            var secondSaveState = secondSave.TryGetProperty("saved", out var ps3El) && ps3El.GetBoolean() ? "saved" : "no-pass";
            itemRowCountAfterSecondSave = QueryItemRowCount(harvestInstanceItemId);
            Leg("buyback-post-sell-save", itemRowCountAfterSave == 0,
                $"bridgeSave={postSaveState} items row COUNT(*) for instance {harvestInstanceItemId} = {itemRowCountAfterSave} (REQUIRE 0: MarkItemForDbDeletion → ItemManager.Save DELETE) characters.money={persistedMoneyAfterSave} (REQUIRE {moneyAfterSell})",
                postSaveSw.Elapsed.TotalSeconds);
            if (itemRowCountAfterSave != 0)
            {
                Fail("FAIL-BEHAVIOR/ROW-SURVIVED-SAVE", "SELL/row-survived-save",
                    new { boundary = "SELL/row-survived-save", instanceItemId = harvestInstanceItemId, itemRowCountPreSell, itemRowCountAfterSave, saveState = postSaveState, interpretation = "the sold instance's persisted items row survived a save pass after MarkItemForDbDeletion — a relogin would resurrect the item (BuyBack must NOT persist)" },
                    $"items row for instance {harvestInstanceItemId} still present after save (COUNT(*)={itemRowCountAfterSave})",
                    execSw.Elapsed.TotalSeconds);
            }
            if (persistedMoneyAfterSave != moneyAfterSell)
            {
                Fail("FAIL-BEHAVIOR/MONEY-NOT-PERSISTED", "SELL/money-not-persisted",
                    new { boundary = "SELL/money-not-persisted", charactersMoney = persistedMoneyAfterSave, moneyAfterSell, refundExpected, interpretation = "the refund must be persisted by the save pass; a stale characters.money loses the sale on relogin" },
                    $"characters.money={persistedMoneyAfterSave} REQUIRE {moneyAfterSell}",
                    execSw.Elapsed.TotalSeconds);
            }
            Leg("buyback-second-save", itemRowCountAfterSecondSave == 0,
                $"bridgeSave={secondSaveState} items row COUNT(*) for instance {harvestInstanceItemId} = {itemRowCountAfterSecondSave} (REQUIRE 0: no resurrection on a second pass)",
                0);
            if (itemRowCountAfterSecondSave != 0)
            {
                Fail("FAIL-BEHAVIOR/ROW-SURVIVED-SAVE", "SELL/row-survived-save",
                    new { boundary = "SELL/row-survived-save", instanceItemId = harvestInstanceItemId, itemRowCountAfterSecondSave, saveState = secondSaveState, interpretation = "the instance row was re-inserted by a later save pass — BuyBack items must never be written back" },
                    $"items row for instance {harvestInstanceItemId} reappeared on a second save (COUNT(*)={itemRowCountAfterSecondSave})",
                    execSw.Elapsed.TotalSeconds);
            }

            // ---- SAME-KEY RETRY: the dedupe gate must refuse it pre-flight
            // (the engine backstop is that the item already left the bag). ----
            var retrySw = Stopwatch.StartNew();
            var retryPre = await ReadObsAsync(http, BotName, evidence, "pre-retry");
            var retry = await PostJsonAsync(http, "/api/actors/sell", sellBody);
            var retryTrace = retry.GetProperty("trace_id").GetGuid();
            try
            {
                var retryPoll = await PollTerminalAsync(http, retryTrace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
                retryState = GetStr(retryPoll, "state");
                retryFailure = GetStr(retryPoll, "failure");
                retryDetail = GetStr(retryPoll, "detail");
            }
            catch (TimeoutException ex)
            {
                retryState = "TimedOut";
                retryDetail = $"poll deadline: {ex.Message}";
            }
            var retryPost = await ReadObsAsync(http, BotName, evidence, "post-retry");
            var retryIsDedupe = retryState == "Rejected"
                && retryDetail.Contains("duplicate idempotency key", StringComparison.OrdinalIgnoreCase);
            retryRefusalKind = retryState != "Rejected"
                ? "completed"
                : retryIsDedupe
                    ? "dedupe"
                    : retryDetail.Contains("not found in inventory", StringComparison.OrdinalIgnoreCase)
                        ? "engine-backstop/bag-gone"
                        : $"other/{retryFailure}";
            var retryZeroDelta = retryPost.Money == retryPre.Money
                && retryPost.PotatoCount == retryPre.PotatoCount
                && retryPost.Labor == retryPre.Labor
                && retryPost.BankMoney == retryPre.BankMoney
                && retryPost.BagMap == retryPre.BagMap
                && retryPost.BankMap == retryPre.BankMap;
            Leg("sell-retry-same-key", retryIsDedupe && retryZeroDelta,
                $"state={retryState} failure=[{retryFailure}] detail=[{retryDetail}] kind={retryRefusalKind} key={sellKey} trace={retryTrace} zeroDelta={retryZeroDelta}",
                retrySw.Elapsed.TotalSeconds);
            if (retryState == "Completed")
            {
                Fail("FAIL-BEHAVIOR/RETRY-RE-EXECUTED", "REFUSAL/completed",
                    new { boundary = "REFUSAL/completed", key = sellKey, state = retryState, detail = retryDetail, moneyBefore = retryPre.Money, moneyAfter = retryPost.Money, interpretation = "a same-key retry of a Completed sell must be dedupe-refused; completing again would pay the refund twice" },
                    $"same-key retry Completed (detail=[{retryDetail}]) — dedupe gate did not refuse",
                    retrySw.Elapsed.TotalSeconds);
            }
            if (!retryIsDedupe)
            {
                Fail("UNKNOWN/HARNESS/DEDUPE-NOT-OBSERVED", "REFUSAL/dedupe-not-observed",
                    new { boundary = "REFUSAL/dedupe-not-observed", key = sellKey, state = retryState, failure = retryFailure, detail = retryDetail, kind = retryRefusalKind, interpretation = "the retry was refused, but NOT by the idempotency-key dedupe gate (the engine bag lookup answered instead) — the dedupe guarantee is unproven at this boundary, never a capability verdict" },
                    $"same-key retry kind={retryRefusalKind} (REQUIRE the dedupe gate: 'duplicate idempotency key')",
                    retrySw.Elapsed.TotalSeconds);
            }
            if (!retryZeroDelta)
            {
                Fail("FAIL-BEHAVIOR/ZERO-DELTA-VIOLATED", "REFUSAL/zero-delta-violated",
                    new { boundary = "REFUSAL/zero-delta-violated", leg = "retry-same-key", moneyBefore = retryPre.Money, moneyAfter = retryPost.Money, bagBefore = retryPre.BagMap, bagAfter = retryPost.BagMap, laborBefore = retryPre.Labor, laborAfter = retryPost.Labor, interpretation = "a dedupe-refused retry moved money/items/labor/warehouse — conservation violated" },
                    $"dedupe-refused retry moved balances (money {retryPre.Money}->{retryPost.Money} bag [{retryPre.BagMap}]->[{retryPost.BagMap}])",
                    retrySw.Elapsed.TotalSeconds);
            }

            // ---- REFUSAL LEGS: each REQUIRED Rejected AND zero-delta ----
            async Task RunLegAsync(string leg, uint targetObjId, ulong itemId, string expectSubstring)
            {
                var pre = await ReadObsAsync(http!, BotName, evidence, $"pre-{leg}");
                var key = $"w4-{leg}-{BotAccount}";
                var body = $"{{\"bot\":\"{BotName}\",\"merchantNpcObjId\":{targetObjId},\"itemId\":{itemId},\"idempotencyKey\":\"{key}\"}}";
                var res = await PostJsonAsync(http!, "/api/actors/sell", body);
                var trace = res.GetProperty("trace_id").GetGuid();
                SellLegRecord record;
                try
                {
                    var legPoll = await PollTerminalAsync(http!, trace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
                    var after = await ReadObsAsync(http!, BotName, evidence, $"post-{leg}");
                    record = new SellLegRecord(
                        leg, targetObjId, itemId, key,
                        GetStr(legPoll, "state"), GetStr(legPoll, "failure"), GetStr(legPoll, "detail"),
                        after.Money - pre.Money, after.PotatoCount - pre.PotatoCount, after.Labor - pre.Labor,
                        after.BagMap == pre.BagMap && after.BankMap == pre.BankMap && after.BankMoney == pre.BankMoney,
                        PostObserved: true,
                        DedupeRejection: GetStr(legPoll, "detail").Contains("duplicate idempotency key", StringComparison.Ordinal),
                        Expectation: expectSubstring,
                        DetailMatches: GetStr(legPoll, "detail").Contains(expectSubstring, StringComparison.OrdinalIgnoreCase),
                        Trace: trace.ToString());
                }
                catch (TimeoutException ex)
                {
                    record = new SellLegRecord(leg, targetObjId, itemId, key,
                        "TimedOut", "", $"poll deadline: {ex.Message}", 0, 0, 0, false,
                        PostObserved: false, DedupeRejection: false, Expectation: expectSubstring, DetailMatches: false,
                        Trace: trace.ToString());
                }
                refusals.Add(record);

                if (!record.PostObserved)
                {
                    Fail("UNKNOWN/HARNESS/ACTION-DEADLINE", "RUN/action-deadline",
                        new { boundary = "RUN/action-deadline", leg, trace = record.Trace, budgetSeconds = ActionDeadlineSeconds, interpretation = "refusal leg never reached a terminal state inside the poll budget" },
                        $"{leg}: never terminal within {ActionDeadlineSeconds}s (trace {record.Trace})",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.State == "Completed")
                {
                    Fail("FAIL-BEHAVIOR/REFUSAL-COMPLETED", "REFUSAL/completed",
                        new { boundary = "REFUSAL/completed", leg, targetObjId, itemId, state = record.State, detail = record.Detail, interpretation = "a refusal leg completed; the engine gate under test is missing" },
                        $"{leg}: Completed where Rejected was required (detail=[{record.Detail}])",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.DedupeRejection)
                {
                    Fail("UNKNOWN/HARNESS/DEDUPE-KEY-COLLISION", "REFUSAL/dedupe-not-engine",
                        new { boundary = "REFUSAL/dedupe-not-engine", leg, key = record.IdempotencyKey, detail = record.Detail, interpretation = "the Rejected state came from the idempotency gate, not from the engine gate under test — this is NOT a refusal proof" },
                        $"{leg}: Rejected by the IDEMPOTENCY gate, not the engine gate (key {record.IdempotencyKey})",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.MoneyDelta != 0 || record.PotatoDelta != 0 || record.LaborDelta != 0 || !record.BalancesEqual)
                {
                    Fail("FAIL-BEHAVIOR/ZERO-DELTA-VIOLATED", "REFUSAL/zero-delta-violated",
                        new { boundary = "REFUSAL/zero-delta-violated", leg, moneyDelta = record.MoneyDelta, potatoDelta = record.PotatoDelta, laborDelta = record.LaborDelta, balancesEqual = record.BalancesEqual, detail = record.Detail, interpretation = "a refused sale moved money/items/labor/warehouse — conservation violated" },
                        $"{leg}: refused but delta money={record.MoneyDelta} potato={record.PotatoDelta} labor={record.LaborDelta} balancesEqual={record.BalancesEqual}",
                        execSw.Elapsed.TotalSeconds);
                }
                if (!record.DetailMatches)
                {
                    Fail("UNKNOWN/HARNESS/REFUSAL-WRONG-REASON", "REFUSAL/wrong-reason",
                        new { boundary = "REFUSAL/wrong-reason", leg, expected = expectSubstring, detail = record.Detail, interpretation = "the leg was Rejected for a reason other than the gate under test; this is not a proof of that gate" },
                        $"{leg}: Rejected but detail=[{record.Detail}] does not name [{expectSubstring}]",
                        execSw.Elapsed.TotalSeconds);
                }
                Leg(leg, true,
                    $"Rejected failure=[{record.Failure}] detail=[{record.Detail}] delta money={record.MoneyDelta} potato={record.PotatoDelta} labor={record.LaborDelta} zero-delta={record.BalancesEqual && record.MoneyDelta == 0 && record.PotatoDelta == 0 && record.LaborDelta == 0}",
                    0);
            }

            // (a) a foreign itemId — never a member of the bag.
            await RunLegAsync("refuse-foreign-item", merchantObjId, ForeignItemId, "not found in inventory");

            // (b) the unsellable 1252 instance (items.sellable = 'f').
            await RunLegAsync("refuse-unsellable", merchantObjId, unsellableInstanceItemId, "is not sellable");

            // (c) a non-merchant target (our own character objId). The merchant
            // gate is step 1, so the item id is deliberately irrelevant here.
            if (botObjId == 0)
            {
                failBoundary = "HARNESS/bot-objid-unavailable";
                Assert.Fail("bridge never reported our own objId; cannot stage the non-merchant refusal leg");
            }
            await RunLegAsync("refuse-nonmerchant", botObjId, ForeignItemId, "not a merchant");

            // ---- FAR LEG (30 m): FINDING, never a refusal. Sell has NO range
            // gate (GameplayActor.Sell steps 1–5 and CSSellItemsPacket carry no
            // distance check — there is no sell counterpart to the Buy path's
            // MaxShopRange 3 m). A fresh sellable instance is stocked via the
            // bridge (fixture; the sold stack is gone), the bot is placed 30 m
            // off the merchant, and the leg is EXPECTED to COMPLETE with the
            // exact refund. Its PASS therefore documents the ABSENT gate. ----
            var farSw = Stopwatch.StartNew();
            var farStock = bridge.Call(
                $"{{\"cmd\":\"mail\",\"op\":\"stock\",\"bot\":\"{BotName}\",\"itemTemplate\":{Potato7992},\"count\":1}}", 30_000);
            var farItem = FindBagInstance(bridge, Potato7992, 0UL);
            farItemId = farItem.ItemId;
            farCount = farItem.Count;
            farGrade = farItem.Grade;
            var farMultiplier = SqliteScalarLong("SELECT refund_multiplier FROM item_grades WHERE id = $grade", ("$grade", farGrade));
            if (farItem.Found && refundDb != long.MinValue && farMultiplier != long.MinValue)
                farRefundExpected = (long)(int)((int)refundDb * (int)farMultiplier / 100f) * farCount;
            if (!farItem.Found || farRefundExpected == long.MinValue)
            {
                failBoundary = "HARNESS/instance-not-found";
                verdict = "UNKNOWN/HARNESS/instance-not-found";
                failingCondition = new
                {
                    boundary = failBoundary,
                    farItemStockEcho = farStock.ToString(),
                    farItem = farItem,
                    farRefundExpected,
                    interpretation = "the far-leg sellable instance was not resolvable by instance id — the range FINDING probe cannot be staged"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: farItem={farItem} expected={farRefundExpected}");
            }
            var farPre = await ReadObsAsync(http, BotName, evidence, "pre-far");
            var farPlace = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat((float)(merchantX + FarOffsetM))},\"y\":{FormatFloat((float)merchantY)},\"z\":{FormatFloat((float)merchantZ)}}}",
                30_000);
            farPos = ReadPos(bridge);
            farDist = FlatDist(farPos, merchantPos);
            evidence.AppendLine($"- far-stage: place -> [{farPos}] merchant=[{merchantPos}] flat={farDist:0.00}m (REQUIRE > {MaxShopRangeM:0.0}m) placeEcho=[{farPlace}]");
            if (double.IsNaN(farDist) || farDist <= MaxShopRangeM)
            {
                failBoundary = "HARNESS/far-stage-not-out-of-range";
                verdict = "UNKNOWN/HARNESS/far-stage-not-out-of-range";
                failingCondition = new
                {
                    boundary = failBoundary,
                    merchantPos,
                    charPos = farPos,
                    flatDist = farDist,
                    maxShopRange = MaxShopRangeM,
                    interpretation = "the far staging did not move the bot outside the BUY shop gate — the absent-gate finding cannot be observed at a distance where the Buy path would refuse"
                };
                Assert.Fail($"W4 {verdict} at {failBoundary}: flat={farDist:0.00}m (REQUIRE > {MaxShopRangeM:0.0}m)");
            }
            var farKey = $"w4-far-{BotAccount}";
            var farBody = $"{{\"bot\":\"{BotName}\",\"merchantNpcObjId\":{merchantObjId},\"itemId\":{farItemId},\"idempotencyKey\":\"{farKey}\"}}";
            var farPost = await PostJsonAsync(http, "/api/actors/sell", farBody);
            var farTrace = farPost.GetProperty("trace_id").GetGuid();
            try
            {
                var farPoll = await PollTerminalAsync(http, farTrace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
                farState = GetStr(farPoll, "state");
                farDetail = GetStr(farPoll, "detail");
                if (farPoll.TryGetProperty("result_payload", out var frEl) && frEl.ValueKind == JsonValueKind.Number && frEl.TryGetInt64(out var frefund))
                    farRefundObserved = frefund;
            }
            catch (TimeoutException ex)
            {
                farState = "TimedOut";
                farDetail = $"poll deadline: {ex.Message}";
            }
            var farPostObs = await ReadObsAsync(http, BotName, evidence, "post-far");
            farMoneyDelta = farPostObs.Money - farPre.Money;
            var farOk = farState == "Completed"
                && farMoneyDelta == farRefundExpected
                && farRefundObserved == farRefundExpected;
            Leg("far-leg-30m", farOk,
                $"flat={farDist:0.00}m (BUY gate {MaxShopRangeM:0.0}m) state={farState} detail=[{farDetail}] moneyDelta={farMoneyDelta} (REQUIRE +{farRefundExpected}) payload={farRefundObserved}",
                farSw.Elapsed.TotalSeconds);
            if (!farOk)
            {
                findings.Add($"SELL/range-gate-absent-finding CONTRADICTED: far leg state={farState} detail=[{farDetail}] moneyDelta={farMoneyDelta} expected=+{farRefundExpected} — a range gate appeared where the code audit (GameplayActor.Sell steps 1-5, CSSellItemsPacket) found none");
                Fail("FAIL-BEHAVIOR/RANGE-GATE-UNEXPECTED", "SELL/range-gate-absent-finding",
                    new { boundary = "SELL/range-gate-absent-finding", flatDist = farDist, maxShopRange = MaxShopRangeM, state = farState, detail = farDetail, farMoneyDelta, farRefundExpected, interpretation = "the code audit found NO range gate in the Sell path, so a 30 m sale must Complete; this outcome contradicts the finding — re-audit GameplayActor.Sell and CSSellItemsPacket before trusting either" },
                    $"far leg at {farDist:0.00}m: state={farState} detail=[{farDetail}] moneyDelta={farMoneyDelta} (expected Completed with +{farRefundExpected})",
                    farSw.Elapsed.TotalSeconds);
            }
            findings.Add($"SELL/range-gate-absent-finding: Sell has NO range gate — a 30 m sale of instance {farItemId} to merchant {merchantObjId} COMPLETED with the exact refund +{farRefundExpected} (the BUY path refuses at >{MaxShopRangeM:0.0}m). Reported as a FINDING, never as a capability proof.");

            execSeconds = execSw.Elapsed.TotalSeconds;
            passed = true;
            verdict = "PASS";
            Leg("gate", true,
                $"PASS: Completed sell of harvest-derived {Potato7992} stack {harvestInstanceItemId} (count {harvestCount}, grade {harvestGrade}) for exactly {refundExpected} (money {startObs.Money}->{postSell.Money}) + BuyBack row deleted on save ({itemRowCountPreSell}->{itemRowCountAfterSave}, money persisted {persistedMoneyAfterSave}) + same-key retry dedupe-refused + {refusals.Count} refusals all Rejected with zero delta + 30m range-gate-absent FINDING",
                execSw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(failBoundary))
            {
                verdict = "UNKNOWN/HARNESS/UNEXPECTED";
                failBoundary = "HARNESS/unexpected";
                failingCondition = new { boundary = failBoundary, exception = ex.GetType().Name, message = ex.Message, interpretation = "the gate threw outside any labeled boundary check" };
            }
            evidence.AppendLine($"- EXCEPTION {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            totalWall.Stop();
            try
            {
                E2eStack.CleanupBotRows(BotAccount);
                cleanupSummary = $"CleanupBotRows({BotAccount})";
            }
            catch (Exception ex)
            {
                cleanupSummary = $"cleanup failed ({ex.GetType().Name}: {ex.Message})";
            }
            try
            {
                await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, findings, legs, evidence.ToString(),
                    new
                    {
                        bot = BotName,
                        botAccount = BotAccount,
                        charId,
                        botObjId,
                        merchantTemplate = MerchantTemplate8522,
                        merchantObjId,
                        merchantPos,
                        charPos,
                        merchantFlatDistM = double.IsNaN(startDist) ? (double?)null : startDist,
                        maxShopRangeBuy = MaxShopRangeM,
                        crop = new
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
                            maturitySeconds,
                            reapproachPos,
                            reapproachDist,
                            harvestVia = "bridge-farm-harvest",
                            harvestState,
                            harvestDetail,
                            harvestYield
                        },
                        soldInstance = new
                        {
                            instanceItemId = harvestInstanceItemId,
                            templateId = Potato7992,
                            count = harvestCount,
                            grade = harvestGrade,
                            refundPerUnitInputs = new { refund = refundDb, refundMultiplier = multiplierDb },
                            refundExpected,
                            refundObserved,
                            bagInstanceGone,
                            bagPotatoBefore = startObs?.PotatoCount,
                            bagPotatoAfter = postSell?.PotatoCount
                        },
                        moneyBefore = startObs?.Money,
                        moneyAfter = postSell?.Money,
                        actorIdEcho,
                        sellState,
                        sellFailure,
                        sellDetail,
                        sellTraceAudit = sellAudit,
                        buyBack = new
                        {
                            preSaveState,
                            itemRowCountPreSell,
                            itemRowCountAfterSave,
                            itemRowCountAfterSecondSave,
                            persistedMoneyAfterSave,
                            moneyAfterSell
                        },
                        retry = new { key = $"w4-sell-{BotAccount}", state = retryState, failure = retryFailure, detail = retryDetail, kind = retryRefusalKind },
                        unsellableInstance = new { instanceItemId = unsellableInstanceItemId, templateId = Unsellable1252, count = unsellableCount },
                        farLeg = new { farItemId, templateId = Potato7992, count = farCount, grade = farGrade, farPos, farDistM = double.IsNaN(farDist) ? (double?)null : farDist, state = farState, detail = farDetail, refundExpected = farRefundExpected, refundObserved = farRefundObserved, moneyDelta = farMoneyDelta },
                        refusals = refusals.Select(r => new
                        {
                            r.Leg, r.TargetObjId, r.ItemId, r.IdempotencyKey, r.State, r.Failure, r.Detail,
                            r.MoneyDelta, r.PotatoDelta, r.LaborDelta, r.BalancesEqual, r.PostObserved, r.DedupeRejection,
                            r.Expectation, r.DetailMatches, r.Trace
                        }).ToList(),
                        findings,
                        cleanup = cleanupSummary
                    },
                    setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            }
            finally
            {
                session?.Dispose();
                laneBridge?.Dispose();
                http?.Dispose();
                Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
                Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
            }
        }
    }

    // ---- observation surface -------------------------------------------------

    /// <summary>One authoritative observation snapshot (the consequence surface).</summary>
    private sealed record ObsSnap(
        long Money, long BankMoney, int Labor, int PotatoCount,
        string BagMap, string BankMap, string Pos, string Raw)
    {
        public string Summary =>
            FormattableString.Invariant($"money={Money} bank={BankMoney} labor={Labor} bag7992={PotatoCount} bag=[{BagMap}] warehouse=[{BankMap}] pos={Pos}");
    }

    /// <summary>
    /// One refusal leg's observed outcome. Deltas are post-call minus
    /// pre-call on the SAME observation surface; every one MUST be zero.
    /// </summary>
    private sealed record SellLegRecord(
        string Leg, uint TargetObjId, ulong ItemId, string IdempotencyKey,
        string State, string Failure, string Detail,
        long MoneyDelta, int PotatoDelta, int LaborDelta, bool BalancesEqual,
        bool PostObserved, bool DedupeRejection, string Expectation, bool DetailMatches, string Trace);

    /// <summary>A bag instance projection (Sell is keyed by INSTANCE id).</summary>
    private readonly record struct BagInstance(bool Found, ulong ItemId, uint TemplateId, int Count, int Grade);

    /// <summary>
    /// POST /api/actors/observe → poll to terminal → the ActorObservation
    /// result payload. Observe is a query on the same execution boundary as
    /// the verb under test, so it is the authoritative read; no gameplay
    /// mutation rides this path.
    /// </summary>
    private static async Task<ObsSnap> ReadObsAsync(HttpClient http, string botName, StringBuilder evidence, string tag)
    {
        var res = await PostJsonAsync(http, "/api/actors/observe", $"{{\"bot\":\"{botName}\"}}");
        var trace = res.GetProperty("trace_id").GetGuid();
        var poll = await PollTerminalAsync(http, trace, TimeSpan.FromSeconds(30));
        if (GetStr(poll, "state") != "Completed")
            throw new InvalidOperationException($"observe '{tag}' state={GetStr(poll, "state")} failure=[{GetStr(poll, "failure")}]");
        if (!poll.TryGetProperty("result_payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"observe '{tag}' returned no result_payload");
        var snap = Snapshot(payload);
        evidence.AppendLine($"- observe[{tag}] trace={trace}: {snap.Summary}");
        return snap;
    }

    private static ObsSnap Snapshot(JsonElement obs)
    {
        var potato = 0;
        if (obs.TryGetProperty("BagItemCounts", out var bag) && bag.ValueKind == JsonValueKind.Object
            && bag.TryGetProperty(Potato7992.ToString(CultureInfo.InvariantCulture), out var pEl)
            && pEl.TryGetInt32(out var pCount))
            potato = pCount;

        var pos = "UNAVAILABLE";
        if (obs.TryGetProperty("Position", out var p) && p.ValueKind == JsonValueKind.Object)
            pos = FormattableString.Invariant($"{GetDbl(p, "X"):0.0},{GetDbl(p, "Y"):0.0},{GetDbl(p, "Z"):0.0}");

        long money = 0, bank = 0;
        if (obs.TryGetProperty("Money", out var mEl) && mEl.TryGetInt64(out var mv))
            money = mv;
        if (obs.TryGetProperty("BankMoney", out var bEl) && bEl.TryGetInt64(out var bv))
            bank = bv;
        var labor = GetInt(obs, "LaborPower");

        var raw = obs.GetRawText();
        if (raw.Length > 700)
            raw = raw[..700] + "…";

        return new ObsSnap(money, bank, labor, potato,
            NormalizeBag(obs, "BagItemCounts"), NormalizeBag(obs, "BankItemCounts"), pos, raw);
    }

    /// <summary>
    /// Canonical, order-independent rendering of a template→count map so two
    /// snapshots can be compared byte-for-byte (a refusal must leave the WHOLE
    /// container unchanged, not just the named template).
    /// </summary>
    private static string NormalizeBag(JsonElement obs, string field)
    {
        if (!obs.TryGetProperty(field, out var bag) || bag.ValueKind != JsonValueKind.Object)
            return "ABSENT";
        var rows = new List<(uint Template, int Count)>();
        foreach (var prop in bag.EnumerateObject())
        {
            if (!uint.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var template))
                continue;
            rows.Add((template, prop.Value.TryGetInt32(out var n) ? n : 0));
        }
        rows.Sort((a, b) => a.Template.CompareTo(b.Template));
        return string.Join(";", rows.Select(r => $"{r.Template}={r.Count}"));
    }

    // ---- bridge probes (read-only / staging only) ---------------------------

    /// <summary>
    /// The bag instance for a template, read from the bridge `mail inv`
    /// projection (instance itemId + count + grade). <paramref name="excludeItemId"/>
    /// skips a known instance (0 = no exclusion).
    /// </summary>
    private static BagInstance FindBagInstance(BotDriveClient bridge, uint templateId, ulong excludeItemId)
    {
        try
        {
            var inv = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"inv\",\"bot\":\"{BotName}\"}}", 30_000);
            foreach (var item in inv.GetProperty("items").EnumerateArray())
            {
                if (!item.TryGetProperty("templateId", out var tEl) || tEl.GetUInt32() != templateId)
                    continue;
                if (item.TryGetProperty("slotType", out var stEl) && stEl.GetInt32() != SlotTypeInventory)
                    continue;
                var id = item.TryGetProperty("itemId", out var idEl) && idEl.TryGetUInt64(out var iv) ? iv : 0UL;
                if (id == 0 || (excludeItemId != 0 && id == excludeItemId))
                    continue;
                return new BagInstance(true, id,
                    templateId,
                    item.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0,
                    item.TryGetProperty("grade", out var gEl) ? gEl.GetInt32() : -1);
            }
        }
        catch
        {
        }
        return new BagInstance(false, 0, templateId, 0, -1);
    }

    /// <summary>True when the instance id is still a member of the bag (the
    /// BuyBack container is NOT the bag, so a sold instance must not appear).</summary>
    private static bool BagContainsInstance(BotDriveClient bridge, ulong itemId)
    {
        try
        {
            var inv = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"inv\",\"bot\":\"{BotName}\"}}", 30_000);
            foreach (var item in inv.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("itemId", out var idEl) && idEl.TryGetUInt64(out var iv) && iv == itemId)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private readonly record struct CropState(bool Found, uint Template, uint Phase, uint DbId, float X, float Y, float Z);

    /// <summary>Live doodad read through the bridge farm seam (read-only).</summary>
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

    /// <summary>Effective lane growth rate (evidence only), mirroring W2.</summary>
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
        return 1.0;
    }

    private static uint PollNpcObjId(BotDriveClient bridge, uint templateId, int seconds)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < deadline)
        {
            var objId = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{templateId}}}", 30_000)
                .GetProperty("objId").GetUInt32();
            if (objId != 0)
                return objId;
            Thread.Sleep(1000);
        }
        return 0;
    }

    private static uint ReadBotObjId(BotDriveClient bridge)
    {
        try
        {
            return bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{BotName}\"}}", 30_000)
                .TryGetProperty("objId", out var el) ? el.GetUInt32() : 0u;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadPos(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            return FormattableString.Invariant($"{GetDbl(el, "x"):0.0},{GetDbl(el, "y"):0.0},{GetDbl(el, "z"):0.0}");
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    private static double FlatDist(string a, string b)
    {
        var pa = a.Split(',');
        var pb = b.Split(',');
        if (pa.Length != 3 || pb.Length != 3)
            return double.NaN;
        if (!double.TryParse(pa[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
            || !double.TryParse(pa[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay)
            || !double.TryParse(pb[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bx)
            || !double.TryParse(pb[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var by))
            return double.NaN;
        return Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
    }

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

    // ---- persistence probes (MySQL items row + characters money) -------------

    private static long QueryItemRowCount(ulong itemId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? -1L : Convert.ToInt64(raw);
    }

    private static long QueryPersistedMoney(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT money FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? long.MinValue : Convert.ToInt64(raw);
    }

    /// <summary>
    /// One scalar from the lane's RUNTIME compact.sqlite3 (the data the game
    /// itself loaded) — the refund-formula inputs are read here, never assumed.
    /// long.MinValue when the row/column is absent.
    /// </summary>
    private static long SqliteScalarLong(string sql, params (string Name, long Value)[] args)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={E2eStack.RuntimeSqlite}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            var raw = cmd.ExecuteScalar();
            return raw == null || raw is DBNull ? long.MinValue : Convert.ToInt64(raw);
        }
        catch
        {
            return long.MinValue;
        }
    }

    // ---- JSON readers -------------------------------------------------------

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static float GetFloat(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var n) ? n : 0f;

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- lane adopt (stale-proc guard + probes) -----------------------------

    /// <summary>
    /// Stale-lane guard: before the gate adopts a warm lane, ANY process
    /// LISTENING on the shared WebApi port whose cwd is NOT this lane's
    /// E2E_ROOT runtime dir is a foreign leftover (a sibling lane or an
    /// orphaned prior run) that would answer the adopt probes for the wrong
    /// stack. Such a listener is SIGTERM'd, then SIGKILL'd after 10 s if it
    /// is still alive. Only listeners are considered, so the current lane's
    /// own pids (which appear as CLIENTS on 127.0.0.1:PORT) are never
    /// touched; the log line is emitted for every kill and returned as
    /// evidence.
    /// </summary>
    private static string KillForeignWebApiListeners()
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
                Console.WriteLine("[w4-sell] " + line);
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
                    Console.WriteLine("[w4-sell] " + kline);
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

    // ---- HTTP surface -------------------------------------------------------

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    /// <summary>
    /// Shared-port route selection: every lane game server binds
    /// *:WebApiPort (Kestrel SO_REUSEPORT), so each fresh TCP connection
    /// hashes to ONE lane. The RIGHT backend for this gate is the one that
    /// accepts an enqueue for OUR bot name — only the lane holding our live
    /// session knows it, so a successful enqueue is the same-lane proof (an
    /// observe is a pure query and mutates nothing). Bounded re-hash; null
    /// when no backend accepts it (SETUP/webapi-route, never a verdict).
    /// </summary>
    private static async Task<HttpClient?> RouteToBotAsync(string botName, StringBuilder evidence)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var client = NewClient();
            try
            {
                using var content = new StringContent($"{{\"bot\":\"{botName}\"}}", Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("/api/actors/observe", content);
                var text = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode && text.Contains("trace_id", StringComparison.Ordinal))
                {
                    evidence.AppendLine($"- webapi route attempt {attempt}: enqueue accepted for '{botName}' (same-lane backend)");
                    return client;
                }
                var note = text.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                    ? "bot control API disabled on this backend"
                    : text.Length > 160 ? text[..160] : text;
                evidence.AppendLine($"- webapi route attempt {attempt}: {(int)response.StatusCode} {note} (re-hashing)");
                client.Dispose();
            }
            catch (Exception ex)
            {
                evidence.AppendLine($"- webapi route attempt {attempt}: {ex.GetType().Name}: {ex.Message} (retrying)");
                client.Dispose();
            }
            await Task.Delay(1000);
        }
        return null;
    }

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
        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout.TotalSeconds:0}s");
    }

    // ---- K4 report ----------------------------------------------------------

    private async Task WriteReportAsync(bool passed, string verdict, string failBoundary, string claim, object? failingCondition,
        List<string> findings, List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText, object fixture,
        double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "w4-sell-capability",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[]
                {
                    "buy (W1 owns it)",
                    "buyback re-purchase",
                    "wire CSSellItemsPacket parity",
                    "restart/relogin buyback wipe (B6 territory)",
                    "multi-item atomicity",
                    "autonomy"
                },
                callPath = "fixture: bridge mail rig (money) + bridge farm rig/plant + read-only farm status polls + bridge farm place RE-APPROACH (closes the bridge-harvest 25 m gate) + bridge farm harvest (a genuine 7992 stack) + bridge mail stock (unsellable 1252) + drive teleportToNpc 8522; verb: POST /api/actors/sell {bot, merchantNpcObjId, itemId, idempotencyKey} → BotActionController.Sell → BotActionSpec(Sell, SellActionParams) → BotActionCommandQueue.ExecuteKind (case BotActionKind.Sell) → GameplayActor.Sell (live NPC + Template.Merchant → item by INSTANCE id in Bag/Equipment → Template.Sellable → grade template → refund = (int)(Refund * gradeMult / 100f) * Count → BuyBackItems.AddOrMoveExistingItem(StoreSell) removes the whole stack → ItemManager.MarkItemForDbDeletion → ChangeMoney(Inventory, refund) → SCSoldItemListPacket → Complete(refund)); persistence proof = bridge save → MySQL items row COUNT(*) + characters.money; observation = POST /api/actors/observe + bridge mail inv/char; the gate adds no sell verb of its own. FINDING: the Sell path carries NO range gate (no counterpart to the Buy path's MaxShopRange), so the 30 m leg is expected to COMPLETE — reported as a finding, never as a refusal or a capability proof.",
                failingCondition,
                findings,
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
            // (logs/w4-sell-report.<utc>.json); the bare w4-sell-report.json is
            // only a latest-pointer COPY refreshed here (copy, not symlink, so
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
            // Evidence write is best-effort; the xUnit verdict is authoritative.
            Console.WriteLine($"W4-SELL-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
