using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// WAVE A / W1 — BUY capability + conservation gate (buying FROM a merchant,
/// NOT autonomy).
///
/// Call path under test (code-verified 2026-09-23):
///   POST /api/actors/buy {bot, merchantNpcObjId, itemTemplateId, count, idempotencyKey}
///     → BotActionController.Buy → BotActionSpec(Buy, BuyActionParams)
///     → BotActionCommandQueue.ExecuteKind → IGameplayActor.Buy
///     → GameplayActor.Buy: live-NPC + Template.Merchant + MerchantPackId gate
///       → MaxShopRange 3 m flat gate → pack.SellsItem gate →
///       count/template gates → template.Price*count ≤ Character.Money →
///       Bag.AcquireDefaultItem(StoreBuy) then ChangeMoney(-money) →
///       Complete(money) (result payload = the charged copper).
///
/// PASS needs ALL of:
///   1. one Completed BUY of 4 x potato seed 15659 from live seed merchant
///      8522 (pack 171, price 25) → observed money 10000 → 9900 EXACTLY,
///      bag 15659 +4, bank money + labor + warehouse byte-unchanged;
///   2. FOUR refusal legs, each REQUIRED Rejected with ZERO delta on money /
///      bag map / bank map / labor:
///        (a) template 7992 is not sold by pack 171 → "does not sell item";
///        (b) insolvent (rig money 10) → "not enough money";
///        (c) the bot's own objId is not a merchant → "not found or not a merchant";
///        (d) the bot is placed 30 m off the merchant → "out of shop range"
///            (a Completed here is the range gate missing — a real defect).
///
/// FIXTURE (data-verified against compact.sqlite3, 2026-09-23):
///   npcs 8522 = merchant 't', merchants(row 1006) → merchant_pack_id 171,
///   merchant_goods 2662 → item 15659; items 15659 price 25 / refund 12.
///   Item 7992 is NOT in pack 171 (no pack sells it) — the refusal fixture.
///   npc_spawners 9522 ("8522. 제인") is the spawner teleportToNpc lands on,
///   so the bot stands ON the spawner → inside the 3 m shop gate.
///
/// LANE: adopt-only. Never EnsureUp, never RestartGameServer — a cold lane is
/// an honest SETUP/lane-down FAIL, never a rebuild. The stale-proc guard
/// SIGTERMs (then SIGKILLs) any listener on the shared WebApi port whose cwd
/// is outside this lane's runtime dir before the adopt probes run.
///
/// BRIDGE POSTURE: probe/stage/observe ONLY. The gate drives teleportToNpc /
/// npcObjId / charPos / mail rig|char|inv / farm place — it NEVER calls a
/// bridge verb that mirrors the capability under test (no actor Buy path
/// other than the HTTP verb, no wire CSBuyItemsPacket).
///
/// CLAIM (only): the production actor Buy capability charges exactly
/// price*count from a real live merchant and grants exactly count units, with
/// four independent engine refusals conserving every observed balance.
/// NOT claimed: sell, buyback, wire CSBuyItemsPacket parity, autonomy,
/// multi-line atomicity (R2 — B6 owns that).
/// </summary>
[Collection("e2e")]
public sealed class W1BuyVerbGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "W1Buy" + Stamp;
    private static readonly string BotAccount = ("w1buy" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    // Canonical 1.2 ids (compact.sqlite3 verified): merchant 8522 → pack 171
    // sells seed 15659 at 25 copper; 7992 (potato) is not in pack 171.
    private const uint MerchantTemplate8522 = 8522;
    private const uint MerchantPack171 = 171;
    private const uint PotatoSeed15659 = 15659;
    private const uint NonStocked7992 = 7992;
    private const int SeedPrice = 25;
    private const int BuyCount = 4;
    private const int ChargeExpected = SeedPrice * BuyCount;          // 100
    private const int FundedMoney = 10_000;
    private const int AfterBuyMoney = FundedMoney - ChargeExpected;   // 9_900
    private const int InsolventMoney = 10;
    private const float MaxShopRangeM = 3f;
    private const float FarOffsetM = 30f;
    private const uint AutonomySentinelQuest = 251;
    private const int ActionDeadlineSeconds = 30;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "w1-buy-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BuyFromMerchant_ExactChargeAndGrant_RefusalsZeroDelta()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var execSw = new Stopwatch();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# W1 buy capability + conservation gate — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        var claim = "production GameplayActor.Buy charges exactly price*count from live merchant 8522 (pack 171) and grants exactly count units, and four independent engine refusals conserve every observed balance";
        object? failingCondition = null;
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
        ObsSnap? preBuy = null;
        ObsSnap? postBuy = null;
        long chargeObserved = long.MinValue;
        uint actorIdEcho = 0;
        var buyState = "NOT-ATTEMPTED";
        var buyFailure = "NONE";
        var buyDetail = "";
        var buyAudit = "UNAVAILABLE";
        var packCorroboration = "ABSENT";
        var refusals = new List<RefusalLeg>();

        void Fail(string v, string b, object? condition, string detail, double ms)
        {
            verdict = v;
            failBoundary = b;
            failingCondition = condition;
            Leg("gate", false, detail, ms);
            Assert.Fail($"W1 {v} at {b}: {detail}");
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

            // ---- ENROLL at the merchant (before staging): the wake both
            // registers+activates the live runtime the /api/actors queue
            // resolves by name AND proves no autonomy pre-empted this leg.
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var enrollId = GetUInt(enroll, "id");
            if (enrollId != 0)
                charId = enrollId;
            var stepped = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, AutonomySentinelQuest);
            Leg("enroll", !activeBefore,
                $"stepped={stepped} charId={charId} activeBefore={activeBefore} (must be False: no autonomy claim)",
                enrollSw.Elapsed.TotalSeconds);
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

            // ---- FIXTURE (PRE-START, disclosed): fund 10_000 copper, then
            // stand ON the live seed merchant's spawner (the normal spawn path
            // the bridge teleportToNpc uses ⇒ inside the 3 m shop gate).
            var fixSw = Stopwatch.StartNew();
            var rig = bridge.Call(
                $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"money\":{FundedMoney}}}", 30_000);
            var rigMoney = rig.TryGetProperty("money", out var rmEl) ? rmEl.GetInt64() : long.MinValue;
            if (rigMoney != FundedMoney)
            {
                failBoundary = "SETUP/rig-refused";
                Assert.Fail($"mail rig money={FundedMoney} not honored (bridge reported {rigMoney})");
            }

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
                    fixSw.Elapsed.TotalSeconds);
                Assert.Fail($"merchant NPC template {MerchantTemplate8522} never spawned in the live world");
            }
            charPos = ReadPos(bridge);
            startDist = FlatDist(charPos, merchantPos);
            var staged = !double.IsNaN(startDist) && startDist <= MaxShopRangeM;
            Leg("fixture-merchant", staged,
                $"merchantObjId={merchantObjId} template={MerchantTemplate8522} spawner=[{merchantPos}] char=[{charPos}] flat={startDist:0.00}m (REQUIRE <= {MaxShopRangeM:0.0}m)",
                fixSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                Fail("UNKNOWN/HARNESS/MERCHANT-OUT-OF-RANGE", "HARNESS/merchant-out-of-range",
                    new { boundary = "HARNESS/merchant-out-of-range", merchantObjId, merchantPos, charPos, flatDist = startDist, maxShopRange = MaxShopRangeM, interpretation = "the fixture never stood inside the 3 m shop gate; the verb would be refused correctly, so this is not a capability verdict" },
                    $"bot not inside the shop gate at START (flat={startDist:0.00}m)", fixSw.Elapsed.TotalSeconds);
            }
            botObjId = ReadBotObjId(bridge);

            // ---- START SNAPSHOT (ONE authoritative read, immediately
            // pre-verb). Post-START deltas are behavior evidence, never setup
            // failure. BagItemCounts/LaborPower/Money are the consequence
            // surface (traces only corroborate).
            preBuy = await ReadObsAsync(http, BotName, evidence, "pre-buy");
            var preSeed = preBuy.SeedCount;
            evidence.AppendLine(
                $"- START: charId={charId} botObjId={botObjId} merchant={merchantObjId}/{MerchantTemplate8522} pack={MerchantPack171} " +
                $"chargeExpected={ChargeExpected} afterMoneyExpected={AfterBuyMoney} afterSeedExpected={preSeed + BuyCount}");
            evidence.AppendLine($"- START obs: {preBuy.Summary}");
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ---- VERB: POST /api/actors/buy (the capability under test) ----
            execSw.Start();
            var buyKey = $"w1-buy-{BotAccount}";
            var buyBody = $"{{\"bot\":\"{BotName}\",\"merchantNpcObjId\":{merchantObjId},\"itemTemplateId\":{PotatoSeed15659},\"count\":{BuyCount},\"idempotencyKey\":\"{buyKey}\"}}";
            var buy = await PostJsonAsync(http, "/api/actors/buy", buyBody);
            var buyTrace = buy.GetProperty("trace_id").GetGuid();
            JsonElement poll;
            try
            {
                poll = await PollTerminalAsync(http, buyTrace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
            }
            catch (TimeoutException ex)
            {
                Fail("UNKNOWN/HARNESS/ACTION-DEADLINE", "RUN/action-deadline",
                    new { boundary = "RUN/action-deadline", trace = buyTrace.ToString(), budgetSeconds = ActionDeadlineSeconds, interpretation = ex.Message },
                    $"buy action never reached a terminal state within {ActionDeadlineSeconds}s (trace {buyTrace})",
                    execSw.Elapsed.TotalSeconds);
                return;
            }
            buyState = GetStr(poll, "state");
            buyFailure = GetStr(poll, "failure");
            buyDetail = GetStr(poll, "detail");
            actorIdEcho = GetUInt(poll, "actor_id");
            buyAudit = poll.TryGetProperty("audit", out var auditEl) ? auditEl.ToString() : "UNAVAILABLE (no audit payload)";
            if (poll.TryGetProperty("result_payload", out var rpEl) && rpEl.ValueKind == JsonValueKind.Number && rpEl.TryGetInt64(out var charge))
                chargeObserved = charge;
            else if (rpEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                chargeObserved = long.MinValue;

            if (buyState != "Completed")
            {
                Fail("FAIL-BEHAVIOR/NO-CAPABILITY", "BUY/refused",
                    new { boundary = "BUY/refused", state = buyState, failure = buyFailure, detail = buyDetail, trace = buyTrace.ToString(), merchantObjId, template = PotatoSeed15659, count = BuyCount, expected = "Completed" },
                    $"buy state={buyState} failure=[{buyFailure}] detail=[{buyDetail}] (REQUIRE Completed)",
                    execSw.Elapsed.TotalSeconds);
            }
            Leg("buy-verb", true, $"state={buyState} detail=[{buyDetail}] chargePayload={chargeObserved} trace={buyTrace}",
                execSw.Elapsed.TotalSeconds);

            // ---- OBSERVE-ONLY CONSEQUENCES (no further gameplay verb) ----
            postBuy = await ReadObsAsync(http, BotName, evidence, "post-buy");
            var mailMoney = ReadMailMoney(bridge);
            var invSeed = ReadInvSeedCount(bridge);
            evidence.AppendLine($"- POST obs: {postBuy.Summary} (mail char money={mailMoney}, mail inv seed15659={invSeed})");

            packCorroboration = buyAudit.Contains($"(pack {MerchantPack171})", StringComparison.Ordinal) ? "FOUND" : "ABSENT";
            if (buyAudit.Contains("pack", StringComparison.OrdinalIgnoreCase) && packCorroboration == "ABSENT")
                packCorroboration = "PACK-MENTION-WITHOUT-EXPECTED-PACK";

            if (postBuy.Money < 0)
            {
                Fail("FAIL-BEHAVIOR/NEGATIVE-MONEY", "BUY/negative-money",
                    new { boundary = "BUY/negative-money", moneyBefore = preBuy.Money, moneyAfter = postBuy.Money, chargeExpected = ChargeExpected, interpretation = "copper balance went negative — conservation violated at the currency layer" },
                    $"money went NEGATIVE: {preBuy.Money} -> {postBuy.Money}", execSw.Elapsed.TotalSeconds);
            }
            if (postBuy.Money != AfterBuyMoney)
            {
                Fail("FAIL-BEHAVIOR/MONEY-NOT-EXACT", "BUY/money-not-exact",
                    new { boundary = "BUY/money-not-exact", moneyBefore = preBuy.Money, moneyAfter = postBuy.Money, chargeExpected = ChargeExpected, expectedAfter = AfterBuyMoney, observedCharge = preBuy.Money - postBuy.Money },
                    $"money {preBuy.Money} -> {postBuy.Money}; REQUIRE exactly {AfterBuyMoney} (charge {preBuy.Money - postBuy.Money}, expected {ChargeExpected})",
                    execSw.Elapsed.TotalSeconds);
            }
            if (postBuy.SeedCount != preBuy.SeedCount + BuyCount)
            {
                Fail("FAIL-BEHAVIOR/NO-ITEM-GRANT", "BUY/no-item-grant",
                    new { boundary = "BUY/no-item-grant", template = PotatoSeed15659, seedBefore = preBuy.SeedCount, seedAfter = postBuy.SeedCount, expectedDelta = BuyCount, bagBefore = preBuy.BagMap, bagAfter = postBuy.BagMap, mailInvSeed = invSeed },
                    $"bag 15659 {preBuy.SeedCount} -> {postBuy.SeedCount}; REQUIRE +{BuyCount}",
                    execSw.Elapsed.TotalSeconds);
            }
            if (postBuy.BankMoney != preBuy.BankMoney || postBuy.Labor != preBuy.Labor || postBuy.BankMap != preBuy.BankMap)
            {
                Fail("FAIL-BEHAVIOR/COLLATERAL-DELTA", "BUY/collateral-delta",
                    new { boundary = "BUY/collateral-delta", bankBefore = preBuy.BankMoney, bankAfter = postBuy.BankMoney, laborBefore = preBuy.Labor, laborAfter = postBuy.Labor, bankMapBefore = preBuy.BankMap, bankMapAfter = postBuy.BankMap, interpretation = "a purchase must move only inventory copper and the bought stack" },
                    $"bank/labor/warehouse moved: bank {preBuy.BankMoney}->{postBuy.BankMoney} labor {preBuy.Labor}->{postBuy.Labor}",
                    execSw.Elapsed.TotalSeconds);
            }
            // actor_id echoes the acting actor's world objId (GameplayActor.ActorId
            // => Character.ObjId), NOT the session charId.
            if (chargeObserved != ChargeExpected || actorIdEcho != botObjId)
            {
                Fail("UNKNOWN/HARNESS/RESULT-MISMATCH", "BUY/result-mismatch",
                    new { boundary = "BUY/result-mismatch", chargeObserved, chargeExpected = ChargeExpected, actorIdEcho, charId, packCorroboration, interpretation = "the completed result payload contradicts the independently observed effect (charge and/or acting character); the observed money delta is exact so the behavior is not failed, but two authoritative surfaces disagree" },
                    $"result payload charge={chargeObserved} (expected {ChargeExpected}) actor_id={actorIdEcho} (charId {charId})",
                    execSw.Elapsed.TotalSeconds);
            }
            Leg("buy-outcome", true,
                $"money {preBuy.Money}->{postBuy.Money} (exact {AfterBuyMoney}) seed 15659 {preBuy.SeedCount}->{postBuy.SeedCount} " +
                $"bank {preBuy.BankMoney}->{postBuy.BankMoney} labor {preBuy.Labor}->{postBuy.Labor} chargePayload={chargeObserved} pack={packCorroboration}",
                0);

            // ---- REFUSAL LEGS: each REQUIRED Rejected AND zero-delta ----
            async Task RunLegAsync(string leg, uint targetObjId, uint template, int count, string expectSubstring, bool farLeg)
            {
                var pre = await ReadObsAsync(http!, BotName, evidence, $"pre-{leg}");
                var key = $"w1-{leg}-{BotAccount}";
                var body = $"{{\"bot\":\"{BotName}\",\"merchantNpcObjId\":{targetObjId},\"itemTemplateId\":{template},\"count\":{count},\"idempotencyKey\":\"{key}\"}}";
                var res = await PostJsonAsync(http!, "/api/actors/buy", body);
                var trace = res.GetProperty("trace_id").GetGuid();
                RefusalLeg record;
                try
                {
                    var legPoll = await PollTerminalAsync(http!, trace, TimeSpan.FromSeconds(ActionDeadlineSeconds));
                    var after = await ReadObsAsync(http!, BotName, evidence, $"post-{leg}");
                    record = new RefusalLeg(
                        leg, targetObjId, template, count, key,
                        GetStr(legPoll, "state"), GetStr(legPoll, "failure"), GetStr(legPoll, "detail"),
                        after.Money - pre.Money, after.SeedCount - pre.SeedCount, after.Labor - pre.Labor,
                        after.BagMap == pre.BagMap && after.BankMap == pre.BankMap && after.BankMoney == pre.BankMoney,
                        PostObserved: true,
                        DedupeRejection: GetStr(legPoll, "detail").Contains("duplicate idempotency key", StringComparison.Ordinal),
                        Expectation: expectSubstring,
                        DetailMatches: GetStr(legPoll, "detail").Contains(expectSubstring, StringComparison.OrdinalIgnoreCase),
                        Trace: trace.ToString());
                }
                catch (TimeoutException ex)
                {
                    record = new RefusalLeg(leg, targetObjId, template, count, key,
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
                        new { boundary = "REFUSAL/completed", leg, targetObjId, template, count, state = record.State, detail = record.Detail, interpretation = farLeg ? "BUY/range-gate-missing: a merchant 30 m away completed a purchase — the 3 m shop gate did not refuse" : "a refusal leg completed; the engine gate under test is missing" },
                        $"{leg}: Completed where Rejected was required (detail=[{record.Detail}]) — {(farLeg ? "BUY/range-gate-missing" : "gate missing")}",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.DedupeRejection)
                {
                    Fail("UNKNOWN/HARNESS/DEDUPE-KEY-COLLISION", "REFUSAL/dedupe-not-engine",
                        new { boundary = "REFUSAL/dedupe-not-engine", leg, key = record.IdempotencyKey, detail = record.Detail, interpretation = "the Rejected state came from the idempotency gate, not from the engine gate under test — this is NOT a refusal proof" },
                        $"{leg}: Rejected by the IDEMPOTENCY gate, not the engine gate (key {record.IdempotencyKey})",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.MoneyDelta != 0 || record.SeedDelta != 0 || record.LaborDelta != 0 || !record.BagEqual)
                {
                    Fail("FAIL-BEHAVIOR/ZERO-DELTA-VIOLATED", "REFUSAL/zero-delta-violated",
                        new { boundary = "REFUSAL/zero-delta-violated", leg, moneyDelta = record.MoneyDelta, seedDelta = record.SeedDelta, laborDelta = record.LaborDelta, balancesEqual = record.BagEqual, detail = record.Detail, interpretation = "a refused purchase moved money/items/labor/warehouse — conservation violated" },
                        $"{leg}: refused but delta money={record.MoneyDelta} seed={record.SeedDelta} labor={record.LaborDelta} balancesEqual={record.BagEqual}",
                        execSw.Elapsed.TotalSeconds);
                }
                if (!record.DetailMatches)
                {
                    if (record.Detail.Contains("out of shop range", StringComparison.OrdinalIgnoreCase))
                    {
                        Fail("UNKNOWN/HARNESS/MERCHANT-OUT-OF-RANGE", "HARNESS/merchant-out-of-range",
                            new { boundary = "HARNESS/merchant-out-of-range", leg, detail = record.Detail, interpretation = "the verb refused CORRECTLY on range — the fixture drifted outside the 3 m gate before this leg" },
                            $"{leg}: refused on range, not on the gate under test (fixture drifted)",
                            execSw.Elapsed.TotalSeconds);
                    }
                    Fail("UNKNOWN/HARNESS/REFUSAL-WRONG-REASON", "REFUSAL/wrong-reason",
                        new { boundary = "REFUSAL/wrong-reason", leg, expected = expectSubstring, detail = record.Detail, interpretation = "the leg was Rejected for a reason other than the gate under test; this is not a proof of that gate" },
                        $"{leg}: Rejected but detail=[{record.Detail}] does not name [{expectSubstring}]",
                        execSw.Elapsed.TotalSeconds);
                }
                Leg(leg, true,
                    $"Rejected failure=[{record.Failure}] detail=[{record.Detail}] delta money={record.MoneyDelta} seed={record.SeedDelta} labor={record.LaborDelta} zero-delta=true",
                    0);
            }

            // (a) non-stocked template on a live merchant in range.
            await RunLegAsync("refuse-nonstocked", merchantObjId, NonStocked7992, 1, "does not sell item", farLeg: false);

            // (b) insolvent: rig 10 copper, buy 1 x seed (25 needed) → refused,
            // money stays 10, seeds unchanged; then re-fund to the post-buy
            // balance so the remaining legs compare against a known state.
            var poorSw = Stopwatch.StartNew();
            var poor = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"money\":{InsolventMoney}}}", 30_000);
            var poorMoney = poor.TryGetProperty("money", out var pmEl) ? pmEl.GetInt64() : long.MinValue;
            if (poorMoney != InsolventMoney)
            {
                failBoundary = "SETUP/rig-refused";
                Assert.Fail($"mail rig money={InsolventMoney} not honored (bridge reported {poorMoney})");
            }
            await RunLegAsync("refuse-insolvent", merchantObjId, PotatoSeed15659, 1, "not enough money", farLeg: false);
            var reFund = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"money\":{AfterBuyMoney}}}", 30_000);
            var reFundMoney = reFund.TryGetProperty("money", out var rfEl) ? rfEl.GetInt64() : long.MinValue;
            Leg("re-fund", reFundMoney == AfterBuyMoney,
                $"money restored to {reFundMoney} (REQUIRE {AfterBuyMoney}) so later legs have a known balance",
                poorSw.Elapsed.TotalSeconds);
            if (reFundMoney != AfterBuyMoney)
            {
                failBoundary = "SETUP/rig-refused";
                Assert.Fail($"re-fund to {AfterBuyMoney} not honored (bridge reported {reFundMoney})");
            }

            // (c) a non-merchant target (our own character objId).
            if (botObjId == 0)
            {
                failBoundary = "HARNESS/bot-objid-unavailable";
                Assert.Fail("bridge never reported our own objId; cannot stage the non-merchant refusal leg");
            }
            await RunLegAsync("refuse-nonmerchant", botObjId, PotatoSeed15659, 1, "not a merchant", farLeg: false);

            // (d) > 3 m shop range: place 30 m off the merchant (teleport-shaped
            // staging op), REQUIRE the moved distance before the verb.
            var farSw = Stopwatch.StartNew();
            var place = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat((float)(merchantX + FarOffsetM))},\"y\":{FormatFloat((float)merchantY)},\"z\":{FormatFloat((float)merchantZ)}}}",
                30_000);
            var farPos = ReadPos(bridge);
            var farDist = FlatDist(farPos, merchantPos);
            evidence.AppendLine($"- far-stage: place -> [{farPos}] merchant=[{merchantPos}] flat={farDist:0.00}m (REQUIRE > {MaxShopRangeM:0.0}m) placeEcho=[{place}]");
            if (double.IsNaN(farDist) || farDist <= MaxShopRangeM)
            {
                Fail("UNKNOWN/HARNESS/MERCHANT-OUT-OF-RANGE", "HARNESS/merchant-out-of-range",
                    new { boundary = "HARNESS/merchant-out-of-range", merchantPos, charPos = farPos, flatDist = farDist, maxShopRange = MaxShopRangeM, interpretation = "the far staging did not move the bot outside the 3 m gate; the range refusal cannot be observed" },
                    $"bot still inside the shop gate after far staging (flat={farDist:0.00}m)",
                    farSw.Elapsed.TotalSeconds);
            }
            Leg("far-stage", true, $"flat={farDist:0.00}m (REQUIRE > {MaxShopRangeM:0.0}m)", farSw.Elapsed.TotalSeconds);
            await RunLegAsync("refuse-outofrange", merchantObjId, PotatoSeed15659, 1, "out of shop range", farLeg: true);

            execSeconds = execSw.Elapsed.TotalSeconds;
            passed = true;
            verdict = "PASS";
            Leg("gate", true,
                $"PASS: Completed buy 4 x {PotatoSeed15659} for exactly {ChargeExpected} (money {preBuy.Money}->{postBuy.Money}, bag seed {preBuy.SeedCount}->{postBuy.SeedCount}) + {refusals.Count} refusals all Rejected with zero delta",
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
                await WriteReportAsync(passed, verdict, failBoundary, claim, failingCondition, legs, evidence.ToString(),
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
                        packId = MerchantPack171,
                        template = PotatoSeed15659,
                        price = SeedPrice,
                        count = BuyCount,
                        chargeExpected = ChargeExpected,
                        afterMoneyExpected = AfterBuyMoney,
                        preBuy = preBuy == null ? null : new { preBuy.Money, preBuy.BankMoney, preBuy.Labor, preBuy.SeedCount, preBuy.BagMap, preBuy.BankMap, preBuy.Pos },
                        postBuy = postBuy == null ? null : new { postBuy.Money, postBuy.BankMoney, postBuy.Labor, postBuy.SeedCount, postBuy.BagMap, postBuy.BankMap, postBuy.Pos },
                        chargeObserved,
                        actorIdEcho,
                        buyState,
                        buyFailure,
                        buyDetail,
                        packCorroboration,
                        startDistM = double.IsNaN(startDist) ? (double?)null : startDist,
                        refusals = refusals.Select(r => new
                        {
                            r.Leg, r.MerchantObjId, r.Template, r.Count, r.IdempotencyKey, r.State, r.Failure, r.Detail,
                            r.MoneyDelta, r.SeedDelta, r.LaborDelta, r.BagEqual, r.PostObserved, r.DedupeRejection,
                            r.Expectation, r.DetailMatches, r.Trace
                        }).ToList(),
                        buyTraceAudit = buyAudit,
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
        long Money, long BankMoney, int Labor, int SeedCount,
        string BagMap, string BankMap, string Pos, string Raw)
    {
        public string Summary =>
            FormattableString.Invariant($"money={Money} bank={BankMoney} labor={Labor} bag15659={SeedCount} bag=[{BagMap}] warehouse=[{BankMap}] pos={Pos}");
    }

    /// <summary>
    /// One refusal leg's observed outcome. Deltas are post-call minus
    /// pre-call on the SAME observation surface; every one MUST be zero.
    /// </summary>
    private sealed record RefusalLeg(
        string Leg, uint MerchantObjId, uint Template, int Count, string IdempotencyKey,
        string State, string Failure, string Detail,
        long MoneyDelta, int SeedDelta, int LaborDelta, bool BagEqual,
        bool PostObserved, bool DedupeRejection, string Expectation, bool DetailMatches, string Trace);

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
        var seed = 0;
        if (obs.TryGetProperty("BagItemCounts", out var bag) && bag.ValueKind == JsonValueKind.Object
            && bag.TryGetProperty(PotatoSeed15659.ToString(CultureInfo.InvariantCulture), out var seedEl)
            && seedEl.TryGetInt32(out var seedCount))
            seed = seedCount;

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

        return new ObsSnap(money, bank, labor, seed,
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

    private static long ReadMailMoney(BotDriveClient bridge)
    {
        try
        {
            return bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{BotName}\"}}", 30_000)
                .TryGetProperty("money", out var el) ? el.GetInt64() : long.MinValue;
        }
        catch
        {
            return long.MinValue;
        }
    }

    private static int ReadInvSeedCount(BotDriveClient bridge)
    {
        try
        {
            var inv = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"inv\",\"bot\":\"{BotName}\"}}", 30_000);
            var total = 0;
            foreach (var item in inv.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("templateId", out var tEl) && tEl.GetUInt32() == PotatoSeed15659)
                    total += item.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0;
            }
            return total;
        }
        catch
        {
            return -1;
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

    private static string FormatFloat(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- JSON readers -------------------------------------------------------

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static double GetDbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- lane adopt (stale-proc guard + probes) -----------------------------

    /// <summary>
    /// Stale-lane guard: before the gate adopts a warm lane, ANY process
    /// LISTENING on the shared WebApi port whose cwd is NOT this lane's
    /// E2E_ROOT runtime dir is a foreign leftover (a sibling lane or an
    /// orphaned prior run) and would answer the adopt probes for the wrong
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
                Console.WriteLine("[w1-buy] " + line);
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
                    Console.WriteLine("[w1-buy] " + kline);
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
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText, object fixture,
        double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "w1-buy-capability",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[] { "sell", "buyback", "wire CSBuyItemsPacket parity", "autonomy", "multi-line atomicity (R2)" },
                callPath = "POST /api/actors/buy {bot, merchantNpcObjId, itemTemplateId, count, idempotencyKey} → BotActionController.Buy → BotActionSpec(Buy, BuyActionParams) → BotActionCommandQueue.ExecuteKind → IGameplayActor.Buy → GameplayActor.Buy (live NPC + Template.Merchant + MerchantPackId → MaxShopRange 3m flat → pack.SellsItem → count/template → price*count ≤ Money → Bag.AcquireDefaultItem(StoreBuy) → ChangeMoney(-money) → Complete(charged)) ; observation = POST /api/actors/observe (ActorObservation: Money/BankMoney/LaborPower/BagItemCounts/BankItemCounts/Position) with bridge mail char/inv as independent corroboration — the gate adds no gameplay verb of its own",
                fixtureData = new
                {
                    merchant = "npc 8522 (merchant 't') → merchants(1006) → merchant_pack_id 171 (merchant_packs '<씨앗>')",
                    spawner = "npc_spawners 9522 '8522. 제인' (teleportToNpc lands the bot ON it ⇒ inside the 3 m shop gate)",
                    item = "15659 감자 씨앗 price 25 refund 12 (merchant_goods 2662 → pack 171)",
                    refusalItem = "7992 감자 is NOT member of pack 171 (no pack sells it) ⇒ 'does not sell item'",
                    refusals = "(a) 7992 non-stocked, (b) insolvent (10 copper vs 25 needed), (c) target = our own character objId (not a merchant), (d) 30 m away ⇒ out of shop range"
                },
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
            // (logs/w1-buy-report.<utc>.json); the bare w1-buy-report.json is
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
            Console.WriteLine($"W1-BUY-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
