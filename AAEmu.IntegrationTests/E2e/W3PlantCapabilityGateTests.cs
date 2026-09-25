using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// WAVE B / W3 — PLANT capability gate (POST /api/actors/plant, NOT autonomy).
///
/// Call path under test (code-verified 2026-09-23):
///   POST /api/actors/plant {bot, seedItemTemplateId, x, y, z, zRot, scale, idempotencyKey}
///     → BotActionController.Plant → BotActionSpec(Plant, Destination, PlantActionParams)
///     → BotActionCommandQueue.ExecuteKind (case Plant) → IGameplayActor.Plant
///     → GameplayActor.Plant: finite position → seed in the actor's own bag
///       → ItemManager.GetDoodadIdFromItem + doodad template exists
///       → PublicFarmManager.InPublicFarm/GetFarmType → CanPlace (type + farm_groups count)
///       → HousingManager.GetHouseAtLocation/AllowedToInteract (farm/house zero the cost)
///       → labor gate (skill 25536 consume_lp) → ChangeLabor(-cost)
///       → DoodadManager.CreatePlayerDoodad (consumes one seed, doodad.Save) →
///       Complete(doodad.ObjId) (result payload = the live doodad's objId).
///
/// PASS needs ALL of:
///   1. a Completed OPEN-GROUND plant of seed 15659 at the bot's own position:
///      result payload = a live doodad objId, live doodad template 2259 in
///      phase 4379 with a nonzero dbId, a MySQL `doodads` row
///      (template_id 2259 / current_phase_id 4379 / farm_type 0) and bag
///      15659 exactly −1, labor exactly −1 (the seed skill's ConsumeLaborPower
///      — self-proving, because a farm/house cell would have zeroed it),
///      money + bank money + warehouse byte-unchanged;
///   2. a Completed PUBLIC-FARM plant of the same seed at a probed Farm cell
///      with labor ALREADY 0 (so the completion itself proves the farm
///      zeroing) and the DB row's farm_type bound to Farm (1);
///   3. FIVE refusal legs, each REQUIRED Rejected from the ENGINE gate under
///      test with a strictly zero delta on money / bank money / labor / whole
///      bag map / whole warehouse map and NOT a dedupe rejection:
///        (a) unstocked seed 15646 (plantable, absent from the bag)
///            → "seed item 15646 not found in inventory";
///        (b) bagged non-seed 7992 (no item_spawn_doodads row)
///            → "is not a plantable seed";
///        (c) illegal position — standing IN the public Farm cell, seed 15647
///            (doodad 2247 belongs to NO farm group)
///            → "not allowed on public farm Farm";
///        (d) insufficient labor — the leg-1 plant drained 1 → 0, a second
///            open-ground plant (skill cost 1) → "insufficient labor";
///        (e) farm count cap — 9 further legal Farm plants reach the canonical
///            farm_groups cap 10, the 11th → "not allowed on public farm Farm"
///            with the planted count HELD at 10 (CanPlace's cap branch reuses
///            the type message, so the count is the discriminant).
///
/// FIXTURE (data-verified against compact.sqlite3, 2026-09-23):
///   seed 15659 감자 씨앗 → item_spawn_doodads(129, 15659, 2259) → doodad 2259,
///   group 12, seed phase 4379 (`doodad_func_groups` 4379 → almighty 2259);
///   use skill 25536 초본 식생 심기 with consume_lp = 1;
///   farm_group_doodads(4, farm_group_id 1 'Farm', 2259, 15659) and
///   farm_groups(1, '공용 농장', count 10) — the cap;
///   15646 볍씨 → 2246 IS farm-group 1 (plantable ⇒ its refusal is the
///   missing-bag-item gate); 15647 귀리 씨앗 → 2247 is in NO farm group
///   (⇒ "not allowed on public farm Farm" on a Farm cell); 7992 감자 has no
///   item_spawn_doodads row at all (⇒ "not a plantable seed");
///   PublicFarmManager._farmZones maps subzones 966/998 (공용 농장) → FarmType.Farm.
///   The Nuian spawn sits ~2 km from the nearest Farm subzone (beyond the
///   soil probe's 500 m cap), so the open-ground leg runs at spawn and the
///   Farm site is staged at the same Lilyut farm-966 anchor the needs-farm
///   loop uses — probe + `farm place` only, never a gameplay verb.
///
/// LANE: adopt-only. Never EnsureUp, never RestartGameServer — a cold lane is
/// an honest SETUP/lane-down FAIL, never a rebuild. The stale-proc guard
/// SIGTERMs (then SIGKILLs) any listener on the shared WebApi port whose cwd
/// is outside this lane's runtime dir before the adopt probes run.
///
/// BRIDGE POSTURE: probe/stage/observe ONLY. The gate drives charPos / soil
/// probe / farm place / farm rig / mail stock / farm status+find — it NEVER
/// calls a bridge verb that mirrors the capability under test (no bridge
/// `farm plant`, no direct doodad mutation; the ONLY plant is the HTTP verb).
///
/// CLAIM (only): the production actor Plant capability creates a live 2259
/// crop doodad in phase 4379 from a bagged seed at a legal position, consumes
/// exactly one seed, charges exactly the seed skill's labor cost on open
/// ground and zero on a public Farm cell, persists the row, and five
/// independent ENGINE gates refuse a plant with a strictly zero delta.
/// NOT claimed: harvest/watering/rot chains, autonomy or the needs-farm loop,
/// the economy loop, farm ownership/expiry guards beyond the placement gate,
/// cell occupancy (no such engine gate exists — CreatePlayerDoodad has no
/// overlap check), or wire CSCreateDoodadPacket parity.
/// </summary>
[Collection("e2e")]
public sealed class W3PlantCapabilityGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "W3Plant" + Stamp;
    private static readonly string BotAccount = ("w3plant" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    // Canonical 1.2 ids (compact.sqlite3 verified): seed 15659 → doodad 2259
    // in seedling phase 4379 via skill 25536 (consume_lp 1); the three refusal
    // seeds/items are the independent engine gates.
    private const uint PotatoSeed15659 = 15659;
    private const uint RiceSeed15646 = 15646;
    private const uint Potato7992 = 7992;
    private const uint OatSeed15647 = 15647;
    private const uint CropDoodad2259 = 2259;
    private const uint IllegalDoodad2247 = 2247;
    private const uint SeedlingPhase4379 = 4379;
    private const uint PlantSkill25536 = 25536;
    private const int PlantLaborCost = 1;
    private const int RiggedSeeds = 3;
    private const int RiggedLabor = 1;
    /// <summary>farm_groups(1, '공용 농장').count — the canonical Farm cap
    /// enforced by PublicFarmManager.CanPlace (GetFarmGroupMaxCount).</summary>
    private const int FarmGroupMaxCount = 10;
    /// <summary>doodads.farm_type for a public-Farm placement (FarmType.Farm).</summary>
    private const int FarmTypeFarm = 1;
    /// <summary>Seed restock for the cap walk: the disclosed fixture op that
    /// keeps the walk from being limited by the initial bag (the walk length
    /// itself is derived from the OBSERVED planted count, so the cap boundary
    /// can never drift; the rig is a staging op, never the verb under test).</summary>
    private const int SeedRestock = 12;
    /// <summary>The Lilyut farm-966 anchor the needs-farm loop stages at (the
    /// Nuian spawn is ~2 km from the nearest Farm subzone).</summary>
    private const float FarmAnchorX = 12440f;
    private const float FarmAnchorY = 15390f;
    private const float FarmAnchorZ = 160f;
    /// <summary>The lane's autonomy probe: a fresh account must not already
    /// hold quest 251 after the enroll wake.</summary>
    private const uint AutonomyQuest251 = 251;
    private const int ActionDeadlineSeconds = 30;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string ReportPath => Path.Combine(EvidenceDir, "w3-plant-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PlantSeed_OpenGroundAndPublicFarm_CompletedDoodadsAndFiveRefusalsZeroDelta()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var execSw = new Stopwatch();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# W3 plant capability gate (open ground + public Farm + five engine refusals) — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        const string claimText =
            "production GameplayActor.Plant (POST /api/actors/plant) creates a live 2259 crop doodad in phase 4379 from a bagged seed at a legal position, consumes exactly one seed, charges exactly the seed skill's labor cost (25536 = 1) on open ground and zero on a public Farm cell with farm_type bound in the persisted row, and five independent engine gates refuse a plant with a strictly zero delta on money/bank/labor/bag/warehouse";
        object? failingCondition = null;
        var cleanupSummary = "not-attempted";

        uint charId = 0;
        uint botObjId = 0;
        var openProbe = "UNAVAILABLE";
        var farmProbe = "UNAVAILABLE";
        var farmProbeAfter = "UNAVAILABLE";
        var siteKind = "UNCLASSIFIED";
        var soilPoint = "UNAVAILABLE";
        var openPos = "UNAVAILABLE";
        ObsSnap? start = null;
        ObsSnap? postOpen = null;
        ObsSnap? postFarm = null;
        DoodadRow? openRow = null;
        DoodadRow? farmRow = null;
        var openObjId = 0u;
        var openDbId = 0u;
        var openPhase = 0u;
        var farmObjId = 0u;
        var farmDbId = 0u;
        var farmPhase = 0u;
        long plantedFinal = -1;
        var restockEcho = -1;
        var refusals = new List<RefusalLeg>();

        void Fail(string v, string b, object? condition, string detail, double ms)
        {
            verdict = v;
            failBoundary = b;
            failingCondition = condition;
            Leg("gate", false, detail, ms);
            Assert.Fail($"W3 {v} at {b}: {detail}");
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

            // ---- ENROLL: the wake both registers+activates the live runtime
            // the /api/actors queue resolves by name AND proves no autonomy
            // pre-empted this leg (probe-only, no gameplay command). ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var enrollId = GetUInt(enroll, "id");
            if (enrollId != 0)
                charId = enrollId;
            var stepped = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, AutonomyQuest251);
            Leg("enroll", !activeBefore,
                $"stepped={stepped} charId={charId} activeBefore={activeBefore} (must be False: no autonomy claim)",
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
            botObjId = ReadBotObjId(bridge);

            // ---- WEBAPI ROUTE: the shared :WebApiPort is Kestrel
            // SO_REUSEPORT across lane game servers, so each fresh TCP
            // connection hashes to ONE lane. The correct backend is the one
            // that accepts an enqueue for OUR bot name — bounded re-hash,
            // never a verdict. ----
            http = await RouteToBotAsync(BotName, evidence);
            if (http == null)
            {
                failBoundary = "SETUP/webapi-route";
                Assert.Fail("no WebApi backend accepted an enqueue for our bot after 12 hashed attempts (disabled or foreign lane)");
            }

            // ---- FIXTURE (PRE-START, disclosed): rig 3 potato seeds + labor 1
            // (labor 1 is deliberate: the open-ground plant must drain it
            // 1 -> 0, which both proves the exact cost and arms the
            // insufficient-labor refusal leg). ----
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

            // ---- FIXTURE: classify the site at charPos (probe only). The
            // open-ground leg only means something when the bot is NOT in a
            // public farm: a Farm cell would zero the labor cost and make the
            // -1 assertion vacuous. ----
            var openSiteSw = Stopwatch.StartNew();
            var soilOpen = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{BotName}\",\"radius\":150,\"step\":5}}", 60_000);
            openProbe = soilOpen.ToString();
            var openOnSoil = soilOpen.TryGetProperty("onSoil", out var ose) && ose.GetBoolean();
            siteKind = openOnSoil ? "publicFarm" : "open";
            Leg("site-open-ground", !openOnSoil,
                $"onSoil={openOnSoil} (REQUIRE False: an open-ground START is the only way the labor delta proves the skill cost) probe=[{openProbe}]",
                openSiteSw.Elapsed.TotalSeconds);
            if (openOnSoil)
            {
                Fail("UNKNOWN/HARNESS/SITE-NOT-CLASSIFIED", "HARNESS/site-not-classified",
                    new { boundary = "HARNESS/site-not-classified", site = "open-ground", probe = openProbe, interpretation = "the bot's START position is inside a public farm, so the open-ground labor-cost leg cannot be observed; the harness never classified an open-ground site" },
                    $"bot starts ON farm soil (onSoil=true) — open-ground leg unobservable",
                    openSiteSw.Elapsed.TotalSeconds);
            }

            // ---- START SNAPSHOT (ONE authoritative read, immediately
            // pre-verb). Post-START deltas are behavior evidence, never setup
            // failure. BagItemCounts/LaborPower/Money are the consequence
            // surface (the DB row + bridge status only corroborate). ----
            var startSw = Stopwatch.StartNew();
            start = await ReadObsAsync(http, BotName, evidence, "start");
            var startPos = ReadPosVec(bridge);
            openPos = startPos.Display;
            var plantedAtStart = CountPlantedFarmDoodads(charId);
            evidence.AppendLine(
                $"- START: charId={charId} botObjId={botObjId} seed={PotatoSeed15659} doodad={CropDoodad2259} phase={SeedlingPhase4379} " +
                $"laborCost={PlantLaborCost} site=[{siteKind}] pos=[{openPos}] plantedFarmDoodads={plantedAtStart}");
            evidence.AppendLine($"- START obs: {start.Summary}");
            Leg("start-snapshot",
                start.Labor == RiggedLabor && start.SeedCount >= RiggedSeeds && plantedAtStart == 0 && !float.IsNaN(startPos.X),
                $"labor={start.Labor} (REQUIRE {RiggedLabor}) bag15659={start.SeedCount} (REQUIRE >= {RiggedSeeds}) " +
                $"plantedFarmDoodads={plantedAtStart} (REQUIRE 0) charPos=[{openPos}] money={start.Money} bank={start.BankMoney} " +
                $"bag=[{start.BagDump}] warehouse=[{start.BankDump}]",
                startSw.Elapsed.TotalSeconds);
            if (start.Labor != RiggedLabor || start.SeedCount < RiggedSeeds || plantedAtStart != 0 || float.IsNaN(startPos.X))
            {
                Fail("UNKNOWN/HARNESS/START-SNAPSHOT", "SETUP/rig-ineffective",
                    new { boundary = "SETUP/rig-ineffective", labor = start.Labor, bag15659 = start.SeedCount, plantedFarmDoodads = plantedAtStart, charPos = openPos, expectedLabor = RiggedLabor, expectedSeeds = RiggedSeeds, interpretation = "the single authoritative START read disagrees with the rig echo (or the position is unreadable), so no verb delta could be attributed to the plant" },
                    $"START snapshot unusable: labor={start.Labor} bag15659={start.SeedCount} plantedFarm={plantedAtStart} pos=[{openPos}]",
                    startSw.Elapsed.TotalSeconds);
            }
            setupSeconds = setupSw.Elapsed.TotalSeconds;

            // ================= VERB SURFACE (the capability under test) =====

            // Verifies ONE legal plant's consequences against the pre-verb
            // snapshot and records the leg. Never issues a second verb.
            async Task<ObsSnap> VerifyPlantAsync(string leg, uint seed, string key, uint resultObjId, string expectSite,
                int expectLaborDelta, int expectFarmType, ObsSnap before, double ms)
            {
                var live = ReadDoodad(bridge, resultObjId);
                var row = live.DbId != 0 ? TryReadDoodadRow(live.DbId) : null;
                var after = await ReadObsAsync(http!, BotName, evidence, $"post-{leg}");
                var seedDelta = after.SeedCount - before.SeedCount;
                var laborDelta = after.Labor - before.Labor;
                var moneyDelta = after.Money - before.Money;
                var bankDelta = after.BankMoney - before.BankMoney;
                // The bag MAY move — by exactly the one consumed seed stack.
                // Compare the containers with the seed template excluded
                // (the seed delta itself is asserted at :395), so a correct
                // plant is observable without contradicting that assertion.
                var bankBagEqual = after.BankDump == before.BankDump;
                var bagOnlySeedMoved = BagTextExcept(after.Bag, seed) == BagTextExcept(before.Bag, seed);

                evidence.AppendLine(
                    $"- PLANT[{leg}] key={key} objId={resultObjId} live=[found={live.Found} template={live.Template} phase={live.Phase} dbId={live.DbId} pos={live.X:0.0},{live.Y:0.0},{live.Z:0.0}] " +
                    $"row=[template={row?.TemplateId} phase={row?.CurrentPhaseId} farmType={row?.FarmType} owner={row?.OwnerId}] " +
                    $"seed {before.SeedCount}->{after.SeedCount} labor {before.Labor}->{after.Labor} money Δ{moneyDelta} bank Δ{bankDelta} site={expectSite}");

                if (resultObjId == 0 || !live.Found)
                {
                    Fail("FAIL-BEHAVIOR/NO-DOODAD-CREATED", "PLANT/no-doodad-created",
                        new { boundary = "PLANT/no-doodad-created", leg, seed, key, resultObjId, liveFound = live.Found, detail = "the Completed plant handed back no live doodad objId — no 2259 crop exists in the world" },
                        $"{leg}: no live doodad created (objId={resultObjId}, found={live.Found})",
                        ms);
                }
                if (live.Template != CropDoodad2259 || live.Phase != SeedlingPhase4379)
                {
                    Fail("FAIL-BEHAVIOR/WRONG-TEMPLATE-OR-PHASE", "PLANT/wrong-template-or-phase",
                        new { boundary = "PLANT/wrong-template-or-phase", leg, seed, objId = resultObjId, template = live.Template, phase = live.Phase, expectedTemplate = CropDoodad2259, expectedPhase = SeedlingPhase4379, detail = "the planted doodad is not the canonical potato crop in its seedling phase" },
                        $"{leg}: doodad template={live.Template} (REQUIRE {CropDoodad2259}) phase={live.Phase} (REQUIRE {SeedlingPhase4379})",
                        ms);
                }
                if (live.DbId == 0 || row == null)
                {
                    Fail("FAIL-BEHAVIOR/DB-ROW-MISSING", "PLANT/db-row-missing",
                        new { boundary = "PLANT/db-row-missing", leg, objId = resultObjId, dbId = live.DbId, detail = "DoodadManager.CreatePlayerDoodad persists the crop (doodad.Save) — the plant left no doodads row" },
                        $"{leg}: no doodads row for objId={resultObjId} (dbId={live.DbId})",
                        ms);
                }
                if (row.TemplateId != CropDoodad2259 || row.CurrentPhaseId != SeedlingPhase4379)
                {
                    Fail("FAIL-BEHAVIOR/WRONG-TEMPLATE-OR-PHASE", "PLANT/wrong-template-or-phase",
                        new { boundary = "PLANT/wrong-template-or-phase", leg, dbId = live.DbId, rowTemplate = row.TemplateId, rowPhase = row.CurrentPhaseId, expectedTemplate = CropDoodad2259, expectedPhase = SeedlingPhase4379, detail = "the persisted row does not carry the canonical template/seedling phase" },
                        $"{leg}: row template={row.TemplateId} phase={row.CurrentPhaseId} (REQUIRE {CropDoodad2259}/{SeedlingPhase4379})",
                        ms);
                }
                if (seedDelta != -1 || (row.ItemTemplateId != 0 && row.ItemTemplateId != seed))
                {
                    Fail("FAIL-BEHAVIOR/SEED-NOT-CONSUMED", "PLANT/seed-not-consumed",
                        new { boundary = "PLANT/seed-not-consumed", leg, seed, bagBefore = before.SeedCount, bagAfter = after.SeedCount, seedDelta, rowItemTemplate = row.ItemTemplateId, detail = "a plant MUST consume exactly one seed through the engine's ConsumeItem path" },
                        $"{leg}: bag 15659 {before.SeedCount}->{after.SeedCount} (delta {seedDelta}, REQUIRE -1)",
                        ms);
                }
                if (laborDelta != expectLaborDelta)
                {
                    // A zero delta where the skill's cost was expected means a
                    // farm/house cell zeroed the charge: the PLANT capability
                    // demonstrably worked (doodad + row + consumed seed), only
                    // the site classification was wrong — a harness gap, never
                    // a behavior verdict. Any other mismatch stays behavior.
                    if (expectSite == "open" && expectLaborDelta != 0 && laborDelta == 0)
                    {
                        Fail("UNKNOWN/HARNESS/SITE-NOT-CLASSIFIED", "HARNESS/site-not-classified",
                            new { boundary = "HARNESS/site-not-classified", leg, site = expectSite, laborBefore = before.Labor, laborAfter = after.Labor, laborDelta, expected = expectLaborDelta, objId = resultObjId, interpretation = "the open-ground plant charged no labor, so the placement cell was inside a public farm or a house footprint (both zero the cost) — the site was misclassified; the plant itself completed, so this is a harness gap, never a capability verdict" },
                            $"{leg}: labor delta {laborDelta} (expected {expectLaborDelta}) at a site classified open — a farm/house cell zeroed the cost",
                            ms);
                    }
                    Fail("FAIL-BEHAVIOR/LABOR-DELTA", "PLANT/labor-delta",
                        new { boundary = "PLANT/labor-delta", leg, site = expectSite, laborBefore = before.Labor, laborAfter = after.Labor, laborDelta, expected = expectLaborDelta, detail = "open-ground plants charge the seed skill's ConsumeLaborPower (25536 = 1); public-farm/house placements zero it" },
                        $"{leg}: labor {before.Labor}->{after.Labor} (delta {laborDelta}, REQUIRE {expectLaborDelta})",
                        ms);
                }
                if (row.FarmType != expectFarmType)
                {
                    Fail("FAIL-BEHAVIOR/FARM-TYPE-NOT-BOUND", "PLANT/farm-type-not-bound",
                        new { boundary = "PLANT/farm-type-not-bound", leg, site = expectSite, rowFarmType = row.FarmType, expected = expectFarmType, detail = "the placement must persist the farm type it was judged against (Invalid=0 off-farm, Farm=1 on a public Farm cell)" },
                        $"{leg}: row farm_type={row.FarmType} (REQUIRE {expectFarmType}) at site {expectSite}",
                        ms);
                }
                if (moneyDelta != 0 || bankDelta != 0 || !bankBagEqual || !bagOnlySeedMoved)
                {
                    Fail("FAIL-BEHAVIOR/COLLATERAL-DELTA", "PLANT/collateral-delta",
                        new { boundary = "PLANT/collateral-delta", leg, moneyDelta, bankDelta, seed, bagBefore = before.BagDump, bagAfter = after.BagDump, bagBeforeMinusSeed = BagTextExcept(before.Bag, seed), bagAfterMinusSeed = BagTextExcept(after.Bag, seed), bankBefore = before.BankDump, bankAfter = after.BankDump, detail = "a plant must move only the seed stack and labor — never copper, the bank, or the warehouse" },
                        $"{leg}: collateral moved (money Δ{moneyDelta} bank Δ{bankDelta} bankBagEqual={bankBagEqual} bagDiffBeyondSeed=[{BagTextExcept(before.Bag, seed)}]->[{BagTextExcept(after.Bag, seed)}] seed={seed})",
                        ms);
                }
                return after;
            }

            // ---- LEG 1: the canonical verb on OPEN GROUND (labor 1 -> 0) ----
            execSw.Start();
            var openLegSw = Stopwatch.StartNew();
            var openKey = $"w3-plant-{BotAccount}-open";
            var openCall = await PlantAsync(http, BotName, PotatoSeed15659, startPos, openKey);
            if (openCall.Poll.ValueKind != JsonValueKind.Object)
            {
                Fail("UNKNOWN/HARNESS/ACTION-DEADLINE", "RUN/action-deadline",
                    new { boundary = "RUN/action-deadline", leg = "plant-open-ground", trace = openCall.Trace.ToString(), budgetSeconds = ActionDeadlineSeconds, interpretation = openCall.Timeout },
                    $"open-ground plant never reached a terminal state within {ActionDeadlineSeconds}s (trace {openCall.Trace})",
                    openLegSw.Elapsed.TotalSeconds);
            }
            var openState = GetStr(openCall.Poll, "state");
            var openFailure = GetStr(openCall.Poll, "failure");
            var openDetail = GetStr(openCall.Poll, "detail");
            var openActorEcho = GetUInt(openCall.Poll, "actor_id");
            openObjId = GetUInt(openCall.Poll, "result_payload");
            if (openState == "Interrupted")
            {
                Fail("FAIL-BEHAVIOR/INTERRUPTED-PERSISTENCE", "PLANT/interrupted-persistence",
                    new { boundary = "PLANT/interrupted-persistence", leg = "plant-open-ground", trace = openCall.Trace.ToString(), detail = openDetail, interpretation = "the engine applied the placement in memory but the doodads persistence write failed — the plant outcome is ambiguous" },
                    $"open-ground plant Interrupted at the persistence boundary: {openDetail}",
                    openLegSw.Elapsed.TotalSeconds);
            }
            if (openState != "Completed")
            {
                Fail("FAIL-BEHAVIOR/NO-CAPABILITY", "PLANT/no-doodad-created",
                    new { boundary = "PLANT/no-doodad-created", leg = "plant-open-ground", seed = PotatoSeed15659, pos = openPos, site = siteKind, state = openState, failure = openFailure, detail = openDetail, trace = openCall.Trace.ToString(), expected = "Completed", interpretation = "a bagged seed, a legal open-ground position and the seed skill's labor were all present — the plant MUST complete" },
                    $"open-ground plant state={openState} failure=[{openFailure}] detail=[{openDetail}] (REQUIRE Completed)",
                    openLegSw.Elapsed.TotalSeconds);
            }
            var openTrace = openCall.Trace.ToString();
            postOpen = await VerifyPlantAsync("plant-open-ground", PotatoSeed15659, openKey, openObjId, "open",
                -PlantLaborCost, 0, start, openLegSw.Elapsed.TotalSeconds);
            var openLive = ReadDoodad(bridge, openObjId);
            openDbId = openLive.DbId;
            openPhase = openLive.Phase;
            openRow = openDbId != 0 ? TryReadDoodadRow(openDbId) : null;
            if (openActorEcho != botObjId)
            {
                Fail("UNKNOWN/HARNESS/RESULT-MISMATCH", "PLANT/result-mismatch",
                    new { boundary = "PLANT/result-mismatch", leg = "plant-open-ground", actorIdEcho = openActorEcho, botObjId, charId, objId = openObjId, trace = openTrace, interpretation = "the completed result payload contradicts the independently observed acting character; the observed deltas are exact so the behavior is not failed, but two authoritative surfaces disagree" },
                    $"actor_id={openActorEcho} (expected the acting bot objId {botObjId}; charId {charId})",
                    openLegSw.Elapsed.TotalSeconds);
            }
            Leg("plant-open-ground", true,
                $"state={openState} objId={openObjId} dbId={openDbId} template={openLive.Template} phase={openPhase} farmType={openRow?.FarmType} " +
                $"seed {start.SeedCount}->{postOpen.SeedCount} labor {start.Labor}->{postOpen.Labor} (delta -{PlantLaborCost} exact) detail=[{openDetail}] trace={openTrace}",
                openLegSw.Elapsed.TotalSeconds);

            // ---- REFUSAL LEGS: each REQUIRED Rejected AND zero-delta ----
            async Task RunRefusalLegAsync(string leg, uint seed, string expectSubstring, string expectSite, int? requireLabor, string setupNote)
            {
                var pre = await ReadObsAsync(http!, BotName, evidence, $"pre-{leg}");
                if (requireLabor.HasValue && pre.Labor != requireLabor.Value)
                {
                    Fail("UNKNOWN/HARNESS/LABOR-REGEN-DRIFT", "HARNESS/labor-regen-drift",
                        new { boundary = "HARNESS/labor-regen-drift", leg, labor = pre.Labor, required = requireLabor.Value, interpretation = "the refusal leg's precondition (an exact labor balance) drifted between legs — the leg could complete instead of refusing, so its outcome is not attributable to the gate under test" },
                        $"{leg}: labor={pre.Labor} (REQUIRE {requireLabor.Value} before the leg)",
                        execSw.Elapsed.TotalSeconds);
                }
                // Site precondition: the leg's refusal must be attributable to
                // the gate under test, so the position classification is
                // re-proved immediately before the verb (a drift onto/off the
                // Farm cell would silently swap the gate that fires).
                var siteCheck = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{BotName}\",\"radius\":5,\"step\":5}}", 30_000);
                var onSoilNow = siteCheck.TryGetProperty("onSoil", out var osnEl) && osnEl.GetBoolean();
                if (onSoilNow != string.Equals(expectSite, "publicFarm", StringComparison.Ordinal))
                {
                    Fail("UNKNOWN/HARNESS/SITE-NOT-CLASSIFIED", "HARNESS/site-not-classified",
                        new { boundary = "HARNESS/site-not-classified", leg, expectedSite = expectSite, onSoil = onSoilNow, probe = siteCheck.ToString(), interpretation = "the bot's site changed between staging and the refusal leg, so the leg no longer arms the gate under test" },
                        $"{leg}: site drift — expected {expectSite}, onSoil={onSoilNow} (probe=[{siteCheck}])",
                        execSw.Elapsed.TotalSeconds);
                }
                var pos = ReadPosVec(bridge);
                var key = $"w3-{leg}-{BotAccount}";
                var call = await PlantAsync(http!, BotName, seed, pos, key);
                RefusalLeg record;
                if (call.Poll.ValueKind != JsonValueKind.Object)
                {
                    record = new RefusalLeg(leg, seed, 0, "TimedOut", "", call.Timeout, 0, 0, 0, 0, false,
                        PostObserved: false, DedupeRejection: false, Expectation: expectSubstring, DetailMatches: false,
                        Trace: call.Trace.ToString());
                    refusals.Add(record);
                    Fail("UNKNOWN/HARNESS/ACTION-DEADLINE", "RUN/action-deadline",
                        new { boundary = "RUN/action-deadline", leg, trace = record.Trace, budgetSeconds = ActionDeadlineSeconds, interpretation = "refusal leg never reached a terminal state inside the poll budget" },
                        $"{leg}: never terminal within {ActionDeadlineSeconds}s (trace {record.Trace})",
                        execSw.Elapsed.TotalSeconds);
                }
                var state = GetStr(call.Poll, "state");
                var failure = GetStr(call.Poll, "failure");
                var detail = GetStr(call.Poll, "detail");
                var after = await ReadObsAsync(http!, BotName, evidence, $"post-{leg}");
                var countedDuring = CountPlantedFarmDoodads(charId);
                record = new RefusalLeg(leg, seed, (int)countedDuring, state, failure, detail,
                    after.Money - pre.Money,
                    after.Labor - pre.Labor,
                    after.SeedCount - pre.SeedCount,
                    CountOf(after.Bag, seed) - CountOf(pre.Bag, seed),
                    after.BagDump == pre.BagDump && after.BankDump == pre.BankDump && after.BankMoney == pre.BankMoney,
                    PostObserved: true,
                    DedupeRejection: detail.Contains("duplicate idempotency key", StringComparison.Ordinal),
                    Expectation: expectSubstring,
                    DetailMatches: detail.Contains(expectSubstring, StringComparison.OrdinalIgnoreCase),
                    Trace: call.Trace.ToString());
                refusals.Add(record);
                evidence.AppendLine(
                    $"- REFUSAL[{leg}] site={expectSite} seed={seed} key={key} state={state} failure=[{failure}] detail=[{detail}] " +
                    $"{setupNote} delta money={record.MoneyDelta} labor={record.LaborDelta} seed15659={record.Seed15659Delta} plantSeed={record.PlantSeedDelta} " +
                    $"balancesEqual={record.BagsEqual} plantedFarm={record.PlantedFarmCount} money {pre.Money}->{after.Money} bank {pre.BankMoney}->{after.BankMoney} " +
                    $"labor {pre.Labor}->{after.Labor} bag=[{pre.BagDump}]->[{after.BagDump}] trace={record.Trace}");

                if (record.State == "Completed")
                {
                    Fail("FAIL-BEHAVIOR/REFUSAL-COMPLETED", "REFUSAL/completed",
                        new { boundary = "REFUSAL/completed", leg, seed, site = expectSite, state = record.State, detail = record.Detail, interpretation = "a refusal leg completed — the engine gate under test is missing (see the leg note for the precondition that was armed)" },
                        $"{leg}: Completed where Rejected was required (detail=[{record.Detail}])",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.DedupeRejection)
                {
                    Fail("UNKNOWN/HARNESS/DEDUPE-KEY-COLLISION", "REFUSAL/dedupe-not-engine",
                        new { boundary = "REFUSAL/dedupe-not-engine", leg, key, detail = record.Detail, interpretation = "the Rejected state came from the idempotency gate, not from the engine gate under test — this is NOT a refusal proof" },
                        $"{leg}: Rejected by the IDEMPOTENCY gate, not the engine gate (key {key})",
                        execSw.Elapsed.TotalSeconds);
                }
                if (record.MoneyDelta != 0 || record.LaborDelta != 0 || record.Seed15659Delta != 0
                    || record.PlantSeedDelta != 0 || !record.BagsEqual)
                {
                    Fail("FAIL-BEHAVIOR/ZERO-DELTA-VIOLATED", "REFUSAL/zero-delta-violated",
                        new { boundary = "REFUSAL/zero-delta-violated", leg, seed, moneyDelta = record.MoneyDelta, laborDelta = record.LaborDelta, seed15659Delta = record.Seed15659Delta, plantSeedDelta = record.PlantSeedDelta, balancesEqual = record.BagsEqual, detail = record.Detail, interpretation = "a refused plant moved money/labor/an item stack/the warehouse — the engine gate mutated state before refusing" },
                        $"{leg}: refused but delta money={record.MoneyDelta} labor={record.LaborDelta} seed15659={record.Seed15659Delta} plantSeed={record.PlantSeedDelta} balancesEqual={record.BagsEqual}",
                        execSw.Elapsed.TotalSeconds);
                }
                if (!record.DetailMatches)
                {
                    Fail("UNKNOWN/HARNESS/REFUSAL-WRONG-REASON", "REFUSAL/wrong-reason",
                        new { boundary = "REFUSAL/wrong-reason", leg, seed, expected = expectSubstring, detail = record.Detail, state = record.State, failure = record.Failure, interpretation = "the leg was Rejected for a reason other than the gate under test; this is not a proof of that gate" },
                        $"{leg}: Rejected but detail=[{record.Detail}] does not name [{expectSubstring}]",
                        execSw.Elapsed.TotalSeconds);
                }
                Leg(leg, true,
                    $"Rejected failure=[{record.Failure}] detail=[{record.Detail}] site={expectSite} delta money={record.MoneyDelta} labor={record.LaborDelta} seed15659={record.Seed15659Delta} plantSeed={record.PlantSeedDelta} zero-delta=true plantedFarm={record.PlantedFarmCount}",
                    0);
            }

            // (d) insufficient labor: leg 1 drained 1 -> 0 and an open-ground
            // plant costs the seed skill's 1 labor — the gate must refuse
            // BEFORE any consumption (packet line 76-86 ordering).
            await RunRefusalLegAsync("refuse-insufficient-labor", PotatoSeed15659, "insufficient labor", "open",
                requireLabor: 0, setupNote: "precondition: leg-1 drained labor 1->0");

            // ---- FIXTURE: classify + stage the PUBLIC FARM site -----------
            // The Nuian spawn is ~2 km from the nearest Farm subzone (beyond
            // the probe's 500 m cap), so the harness stages at the Lilyut
            // farm-966 anchor the needs-farm loop uses, probes for a legal
            // 15659 soil cell and places the bot ON it (probe/place staging
            // only — never a gameplay verb).
            var farmSiteSw = Stopwatch.StartNew();
            var anchorProbe = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat(FarmAnchorX)},\"y\":{FormatFloat(FarmAnchorY)},\"z\":{FormatFloat(FarmAnchorZ)}}}",
                30_000);
            var anchorGround = anchorProbe.TryGetProperty("groundZ", out var gEl) && gEl.ValueKind == JsonValueKind.Number ? gEl.GetSingle() : 0f;
            var anchorZ = anchorGround != 0f ? anchorGround : FarmAnchorZ;
            bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat(FarmAnchorX)},\"y\":{FormatFloat(FarmAnchorY)},\"z\":{FormatFloat(anchorZ)}}}",
                30_000);
            var soilNear = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{BotName}\",\"radius\":150,\"step\":5}}", 60_000);
            farmProbe = soilNear.ToString();
            var soilFound = soilNear.TryGetProperty("found", out var sfEl) && sfEl.GetBoolean();
            var soilX = GetFloat(soilNear, "soilX");
            var soilY = GetFloat(soilNear, "soilY");
            var soilZ = GetFloat(soilNear, "soilZ");
            if (!soilFound || float.IsNaN(soilX))
            {
                Fail("UNKNOWN/HARNESS/SITE-NOT-CLASSIFIED", "HARNESS/site-not-classified",
                    new { boundary = "HARNESS/site-not-classified", site = "public-farm", anchor = new { FarmAnchorX, FarmAnchorY, anchorZ }, probe = farmProbe, interpretation = "no legal 15659 Farm cell was found around the staging anchor, so neither the farm-zeroed labor leg nor the two position/cap refusals can be observed" },
                    $"no public-Farm soil found within 150 m of the anchor (probe=[{farmProbe}])",
                    farmSiteSw.Elapsed.TotalSeconds);
            }
            soilPoint = FormattableString.Invariant($"{soilX:0.0},{soilY:0.0},{soilZ:0.0}");
            bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat(soilX)},\"y\":{FormatFloat(soilY)},\"z\":{FormatFloat(soilZ)}}}",
                30_000);
            var reSoil = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{BotName}\",\"radius\":5,\"step\":5}}", 30_000);
            farmProbeAfter = reSoil.ToString();
            var onFarmSoil = reSoil.TryGetProperty("onSoil", out var fsEl) && fsEl.GetBoolean();
            siteKind = onFarmSoil ? "publicFarm" : "UNCLASSIFIED";
            Leg("site-public-farm", onFarmSoil,
                $"anchor=[{FarmAnchorX:0},{FarmAnchorY:0} z={anchorZ:0.0}] nearestSoil=[{soilPoint}] placed->onSoil={onFarmSoil} (REQUIRE True) probeAfter=[{farmProbeAfter}]",
                farmSiteSw.Elapsed.TotalSeconds);
            if (!onFarmSoil)
            {
                Fail("UNKNOWN/HARNESS/SITE-NOT-CLASSIFIED", "HARNESS/site-not-classified",
                    new { boundary = "HARNESS/site-not-classified", site = "public-farm", soilPoint, probeAfter = farmProbeAfter, interpretation = "the bot could not be placed on a legal 15659 Farm cell, so the farm-placement legs cannot be observed; this is a harness classification gap, never a capability verdict" },
                    $"bot not on farm soil after placement ([{farmProbeAfter}])",
                    farmSiteSw.Elapsed.TotalSeconds);
            }

            // ---- LEG 2: the canonical verb on the PUBLIC FARM cell --------
            // labor is 0 here and the Farm branch zeroes the cost, so a
            // Completed plant is itself the proof that the farm cell zeroed
            // the charge (any nonzero cost would refuse).
            var farmLegSw = Stopwatch.StartNew();
            var farmPos = ReadPosVec(bridge);
            var farmKey = $"w3-plant-{BotAccount}-farm";
            var farmCall = await PlantAsync(http, BotName, PotatoSeed15659, farmPos, farmKey);
            if (farmCall.Poll.ValueKind != JsonValueKind.Object)
            {
                Fail("UNKNOWN/HARNESS/ACTION-DEADLINE", "RUN/action-deadline",
                    new { boundary = "RUN/action-deadline", leg = "plant-public-farm", trace = farmCall.Trace.ToString(), budgetSeconds = ActionDeadlineSeconds, interpretation = farmCall.Timeout },
                    $"public-farm plant never reached a terminal state within {ActionDeadlineSeconds}s (trace {farmCall.Trace})",
                    farmLegSw.Elapsed.TotalSeconds);
            }
            var farmState = GetStr(farmCall.Poll, "state");
            var farmFailure = GetStr(farmCall.Poll, "failure");
            var farmDetail = GetStr(farmCall.Poll, "detail");
            var farmActorEcho = GetUInt(farmCall.Poll, "actor_id");
            farmObjId = GetUInt(farmCall.Poll, "result_payload");
            if (farmState == "Interrupted")
            {
                Fail("FAIL-BEHAVIOR/INTERRUPTED-PERSISTENCE", "PLANT/interrupted-persistence",
                    new { boundary = "PLANT/interrupted-persistence", leg = "plant-public-farm", trace = farmCall.Trace.ToString(), detail = farmDetail, interpretation = "the engine applied the placement in memory but the doodads persistence write failed — the plant outcome is ambiguous" },
                    $"public-farm plant Interrupted at the persistence boundary: {farmDetail}",
                    farmLegSw.Elapsed.TotalSeconds);
            }
            if (farmState != "Completed")
            {
                Fail("FAIL-BEHAVIOR/NO-CAPABILITY", "PLANT/no-doodad-created",
                    new { boundary = "PLANT/no-doodad-created", leg = "plant-public-farm", seed = PotatoSeed15659, pos = farmPos.Display, site = siteKind, soil = soilPoint, labor = postOpen?.Labor, state = farmState, failure = farmFailure, detail = farmDetail, trace = farmCall.Trace.ToString(), expected = "Completed", interpretation = "a legal public-Farm cell with a bagged seed MUST complete with the labor cost zeroed by the farm branch" },
                    $"public-farm plant state={farmState} failure=[{farmFailure}] detail=[{farmDetail}] (REQUIRE Completed)",
                    farmLegSw.Elapsed.TotalSeconds);
            }
            postFarm = await VerifyPlantAsync("plant-public-farm", PotatoSeed15659, farmKey, farmObjId, "publicFarm",
                0, FarmTypeFarm, postOpen!, farmLegSw.Elapsed.TotalSeconds);
            var farmLive = ReadDoodad(bridge, farmObjId);
            farmDbId = farmLive.DbId;
            farmPhase = farmLive.Phase;
            farmRow = farmDbId != 0 ? TryReadDoodadRow(farmDbId) : null;
            if (farmActorEcho != botObjId)
            {
                Fail("UNKNOWN/HARNESS/RESULT-MISMATCH", "PLANT/result-mismatch",
                    new { boundary = "PLANT/result-mismatch", leg = "plant-public-farm", actorIdEcho = farmActorEcho, botObjId, charId, objId = farmObjId, trace = farmCall.Trace.ToString(), interpretation = "the completed result payload contradicts the independently observed acting character; the observed deltas are exact so the behavior is not failed, but two authoritative surfaces disagree" },
                    $"actor_id={farmActorEcho} (expected the acting bot objId {botObjId}; charId {charId})",
                    farmLegSw.Elapsed.TotalSeconds);
            }
            Leg("plant-public-farm", true,
                $"state={farmState} objId={farmObjId} dbId={farmDbId} template={farmLive.Template} phase={farmPhase} farmType={farmRow?.FarmType} " +
                $"seed {postOpen!.SeedCount}->{postFarm.SeedCount} labor {postOpen.Labor}->{postFarm.Labor} (delta 0 — the Farm cell zeroes the cost while the bag still pays one seed) " +
                $"detail=[{farmDetail}] trace={farmCall.Trace}",
                farmLegSw.Elapsed.TotalSeconds);

            // (a) unstocked seed: 15646 볍씨 IS plantable (doodad 2246, farm
            // group 1) but was never stocked — the bag gate must refuse.
            await RunRefusalLegAsync("refuse-unstocked-seed", RiceSeed15646, "not found in inventory", "publicFarm",
                requireLabor: 0, setupNote: "precondition: 15646 is plantable but absent from the bag");

            // (b) bagged non-seed: 7992 감자 has no item_spawn_doodads row, so
            // the mapping gate (not the bag gate) must refuse.
            var stockNonSeed = bridge.Call(
                $"{{\"cmd\":\"mail\",\"op\":\"stock\",\"bot\":\"{BotName}\",\"itemTemplate\":{Potato7992},\"count\":1}}", 30_000);
            evidence.AppendLine($"- fixture mail stock 7992: [{stockNonSeed}]");
            await RunRefusalLegAsync("refuse-not-a-seed", Potato7992, "not a plantable seed", "publicFarm",
                requireLabor: 0, setupNote: "precondition: 7992 is in the bag with no item_spawn_doodads row");

            // (c) illegal position: standing IN the public Farm cell, seed
            // 15647 (doodad 2247 belongs to NO farm group) is refused by
            // CanPlace's type branch.
            var stockIllegal = bridge.Call(
                $"{{\"cmd\":\"mail\",\"op\":\"stock\",\"bot\":\"{BotName}\",\"itemTemplate\":{OatSeed15647},\"count\":1}}", 30_000);
            evidence.AppendLine($"- fixture mail stock 15647: [{stockIllegal}]");
            await RunRefusalLegAsync("refuse-illegal-position", OatSeed15647, "not allowed on public farm Farm", "publicFarm",
                requireLabor: 0, setupNote: $"precondition: doodad {IllegalDoodad2247} is in no farm_group_doodads row and the bot stands on [{soilPoint}]");

            // (e) farm count cap: walk the character's planted Farm crop count
            // up to farm_groups(1).count = 10 with legal plants, then the 11th
            // is refused. The walk length is derived from the OBSERVED count
            // (legs 1-2 already landed one Farm crop) so an off-by-one can
            // never turn the cap boundary into a false behavior failure.
            // CanPlace reuses the SAME message for the cap and the type
            // branch, so the held count is this leg's discriminant.
            var capSw = Stopwatch.StartNew();
            var capStarted = CountPlantedFarmDoodads(charId);
            if (capStarted < 0)
            {
                Fail("UNKNOWN/HARNESS/RESULT-MISMATCH", "HARNESS/planted-count-unavailable",
                    new { boundary = "HARNESS/planted-count-unavailable", interpretation = "the planted public-Farm crop count (the exact surface PublicFarmManager.CanPlace counts) could not be read, so the cap boundary cannot be armed precisely" },
                    "planted public-Farm doodad count unreadable",
                    capSw.Elapsed.TotalSeconds);
            }
            var fillsNeeded = (int)(FarmGroupMaxCount - capStarted);
            var restockNeeded = Math.Max(fillsNeeded, 1);
            var restock = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{BotName}\",\"seeds\":{SeedRestock},\"calves\":0,\"labor\":0}}", 60_000);
            restockEcho = GetInt(restock, "seeds");
            Leg("cap-seed-restock", restockEcho >= restockNeeded && fillsNeeded >= 0,
                $"disclosed fixture restock: farm rig seeds={SeedRestock} → bag15659={restockEcho} (REQUIRE >= {restockNeeded}); plantedFarm={capStarted} → fillsNeeded={fillsNeeded} to reach the cap {FarmGroupMaxCount}",
                capSw.Elapsed.TotalSeconds);
            if (restockEcho < restockNeeded || fillsNeeded < 0)
            {
                failBoundary = "SETUP/rig-ineffective";
                verdict = "UNKNOWN/HARNESS/RIG-INEFFECTIVE";
                failingCondition = new { boundary = failBoundary, restockEcho, restockNeeded, plantedFarmDoodads = capStarted, cap = FarmGroupMaxCount, interpretation = "the disclosed cap-walk restock (or the observed planted count) left the walk unable to reach the canonical cap" };
                Assert.Fail($"cap-walk restock ineffective (seeds={restockEcho}, plantedFarm={capStarted})");
            }
            var capBefore = await ReadObsAsync(http, BotName, evidence, "pre-cap-fill");
            for (var i = 1; i <= fillsNeeded; i++)
            {
                bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"place\",\"bot\":\"{BotName}\",\"x\":{FormatFloat(soilX)},\"y\":{FormatFloat(soilY)},\"z\":{FormatFloat(soilZ)}}}",
                    30_000);
                var fillPos = ReadPosVec(bridge);
                var fillKey = $"w3-plant-{BotAccount}-cap{i}";
                var fillCall = await PlantAsync(http, BotName, PotatoSeed15659, fillPos, fillKey);
                var fillState = fillCall.Poll.ValueKind == JsonValueKind.Object ? GetStr(fillCall.Poll, "state") : "TimedOut";
                var fillDetail = fillCall.Poll.ValueKind == JsonValueKind.Object ? GetStr(fillCall.Poll, "detail") : fillCall.Timeout;
                var fillObj = GetUInt(fillCall.Poll, "result_payload");
                var counted = CountPlantedFarmDoodads(charId);
                if (fillState != "Completed")
                {
                    Fail("FAIL-BEHAVIOR/CAP-FILL-REFUSED", "PLANT/cap-fill-refused",
                        new { boundary = "PLANT/cap-fill-refused", leg = $"cap-fill-{i}", plantIndex = i, fillsNeeded, state = fillState, detail = fillDetail, plantedFarmDoodads = counted, cap = FarmGroupMaxCount, interpretation = $"legal Farm plant {i} of {fillsNeeded} was refused below the canonical cap — the engine refused a placement that farm_groups(1).count allows" },
                        $"cap-fill plant {i}/{fillsNeeded} state={fillState} detail=[{fillDetail}] plantedFarm={counted} (cap {FarmGroupMaxCount})",
                        capSw.Elapsed.TotalSeconds);
                }
                var fillLive = ReadDoodad(bridge, fillObj);
                evidence.AppendLine($"- cap-fill {i}/{fillsNeeded}: objId={fillObj} template={fillLive.Template} phase={fillLive.Phase} plantedFarmDoodads={counted}");
                if (fillLive.Template != CropDoodad2259 || fillLive.Phase != SeedlingPhase4379 || counted > FarmGroupMaxCount)
                {
                    Fail("FAIL-BEHAVIOR/WRONG-TEMPLATE-OR-PHASE", "PLANT/wrong-template-or-phase",
                        new { boundary = "PLANT/wrong-template-or-phase", leg = $"cap-fill-{i}", template = fillLive.Template, phase = fillLive.Phase, plantedFarmDoodads = counted, expectedTemplate = CropDoodad2259, expectedPhase = SeedlingPhase4379, interpretation = "a cap-fill plant produced a doodad that is not the canonical seedling crop (or the cap was exceeded)" },
                        $"cap-fill {i}: template={fillLive.Template} phase={fillLive.Phase} plantedFarm={counted}",
                        capSw.Elapsed.TotalSeconds);
                }
            }
            var capReached = CountPlantedFarmDoodads(charId);
            var capObs = await ReadObsAsync(http, BotName, evidence, "post-cap-fill");
            Leg("cap-fill", capReached == FarmGroupMaxCount,
                $"{fillsNeeded} legal Farm plants landed; plantedFarmDoodads {capStarted}->{capReached} (REQUIRE {FarmGroupMaxCount} = farm_groups(1).count) bag15659 {capBefore.SeedCount}->{capObs.SeedCount} (each plant −1)",
                capSw.Elapsed.TotalSeconds);
            if (capReached != FarmGroupMaxCount)
            {
                Fail("FAIL-BEHAVIOR/CAP-FILL-REFUSED", "PLANT/cap-fill-refused",
                    new { boundary = "PLANT/cap-fill-refused", plantedFarmDoodads = capReached, cap = FarmGroupMaxCount, fillsNeeded, interpretation = "the character's public-Farm crop count did not reach the canonical cap after the fill walk, so the cap refusal cannot be observed (or the count surface is not the one CanPlace reads)" },
                    $"plantedFarmDoodads={capReached} (REQUIRE {FarmGroupMaxCount})",
                    capSw.Elapsed.TotalSeconds);
            }
            await RunRefusalLegAsync("refuse-cap-count", PotatoSeed15659, "not allowed on public farm Farm", "publicFarm",
                requireLabor: 0, setupNote: $"precondition: the character holds exactly {FarmGroupMaxCount} public-Farm crops (cap reached)");
            plantedFinal = CountPlantedFarmDoodads(charId);
            Leg("cap-count-held", plantedFinal == FarmGroupMaxCount,
                $"plantedFarmDoodads={plantedFinal} (REQUIRE {FarmGroupMaxCount} held: the capped plant must not have created a doodad)",
                0);
            if (plantedFinal != FarmGroupMaxCount)
            {
                Fail("FAIL-BEHAVIOR/ZERO-DELTA-VIOLATED", "REFUSAL/zero-delta-violated",
                    new { boundary = "REFUSAL/zero-delta-violated", plantedFarmDoodads = plantedFinal, cap = FarmGroupMaxCount, interpretation = "the capped plant mutated the planted-crop count despite being Rejected" },
                    $"plantedFarmDoodads={plantedFinal} (REQUIRE {FarmGroupMaxCount} — the refused plant created a doodad)",
                    execSw.Elapsed.TotalSeconds);
            }

            execSeconds = execSw.Elapsed.TotalSeconds;
            passed = true;
            verdict = "PASS";
            Leg("gate", true,
                $"PASS: open-ground plant {openObjId} (db {openDbId}) 2259/{SeedlingPhase4379} seed −1 labor −{PlantLaborCost}; public-Farm plant {farmObjId} (db {farmDbId}) farm_type={farmRow?.FarmType} seed −1 labor Δ0; {refusals.Count} refusals all engine-Rejected with zero delta",
                execSeconds);
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
                var doodadsCleanup = CleanupDoodads(charId);
                E2eStack.CleanupBotRows(BotAccount);
                cleanupSummary = $"{doodadsCleanup}; CleanupBotRows({BotAccount})";
            }
            catch (Exception ex)
            {
                cleanupSummary = $"cleanup failed ({ex.GetType().Name}: {ex.Message})";
            }
            try
            {
                await WriteReportAsync(passed, verdict, failBoundary, claimText, failingCondition, legs, evidence.ToString(),
                    new
                    {
                        bot = BotName,
                        botAccount = BotAccount,
                        charId,
                        botObjId,
                        seedTemplate = PotatoSeed15659,
                        doodadTemplate = CropDoodad2259,
                        seedlingPhase = SeedlingPhase4379,
                        plantSkill = PlantSkill25536,
                        laborCost = PlantLaborCost,
                        riggedSeeds = RiggedSeeds,
                        riggedLabor = RiggedLabor,
                        seedRestock = restockEcho,
                        farmGroupMaxCount = FarmGroupMaxCount,
                        siteKind,
                        openProbe,
                        farmProbe,
                        farmProbeAfter,
                        soilPoint,
                        charPos = openPos,
                        openGround = new
                        {
                            objId = openObjId,
                            dbId = openDbId,
                            phaseObserved = openPhase,
                            rowTemplate = openRow?.TemplateId,
                            rowPhase = openRow?.CurrentPhaseId,
                            rowFarmType = openRow?.FarmType,
                            laborBefore = start?.Labor,
                            laborAfter = postOpen?.Labor,
                            bagBefore = start?.SeedCount,
                            bagAfter = postOpen?.SeedCount
                        },
                        publicFarm = new
                        {
                            objId = farmObjId,
                            dbId = farmDbId,
                            phaseObserved = farmPhase,
                            rowTemplate = farmRow?.TemplateId,
                            rowPhase = farmRow?.CurrentPhaseId,
                            rowFarmType = farmRow?.FarmType,
                            laborBefore = postOpen?.Labor,
                            laborAfter = postFarm?.Labor,
                            bagBefore = postOpen?.SeedCount,
                            bagAfter = postFarm?.SeedCount
                        },
                        plantedFarmCount = plantedFinal,
                        start = start == null ? null : new { start.Money, start.BankMoney, start.Labor, start.SeedCount, start.Pos, start.BagDump, start.BankDump },
                        postOpen = postOpen == null ? null : new { postOpen.Money, postOpen.BankMoney, postOpen.Labor, postOpen.SeedCount, postOpen.Pos, postOpen.BagDump, postOpen.BankDump },
                        postFarm = postFarm == null ? null : new { postFarm.Money, postFarm.BankMoney, postFarm.Labor, postFarm.SeedCount, postFarm.Pos, postFarm.BagDump, postFarm.BankDump },
                        refusals = refusals.Select(r => new
                        {
                            r.Leg, r.Seed, r.PlantedFarmCount, r.State, r.Failure, r.Detail,
                            r.MoneyDelta, r.LaborDelta, r.Seed15659Delta, r.PlantSeedDelta, r.BagsEqual,
                            r.PostObserved, r.DedupeRejection, r.Expectation, r.DetailMatches, r.Trace
                        }).ToList(),
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
        Dictionary<uint, int> Bag, Dictionary<uint, int> Bank, string Pos)
    {
        public string BagDump => BagText(Bag);
        public string BankDump => BagText(Bank);
        public string Summary =>
            FormattableString.Invariant($"money={Money} bank={BankMoney} labor={Labor} bag15659={SeedCount} bag=[{BagDump}] warehouse=[{BankDump}] pos={Pos}");
    }

    /// <summary>
    /// One refusal leg's observed outcome. Deltas are post-call minus
    /// pre-call on the SAME observation surface; every one MUST be zero.
    /// </summary>
    private sealed record RefusalLeg(
        string Leg, uint Seed, int PlantedFarmCount,
        string State, string Failure, string Detail,
        long MoneyDelta, int LaborDelta, int Seed15659Delta, int PlantSeedDelta, bool BagsEqual,
        bool PostObserved, bool DedupeRejection, string Expectation, bool DetailMatches, string Trace);

    /// <summary>A world position read from the bridge charPos op.</summary>
    private readonly record struct PosVec(float X, float Y, float Z)
    {
        public string Display => float.IsNaN(X) ? "UNAVAILABLE" : FormattableString.Invariant($"{X:0.0},{Y:0.0},{Z:0.0}");
    }

    /// <summary>A live doodad read through the bridge farm status op (read-only).</summary>
    private readonly record struct LiveDoodad(bool Found, uint Template, uint Phase, uint DbId, float X, float Y, float Z);

    /// <summary>One persisted `doodads` row, projected to the fields this gate
    /// judges (identity + placement binding).</summary>
    private sealed record DoodadRow(uint Id, int OwnerId, uint TemplateId, uint CurrentPhaseId, int FarmType,
        float X, float Y, float Z, ulong ItemId, uint ItemTemplateId);

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

        return new ObsSnap(money, bank, labor, seed,
            ReadBag(obs, "BagItemCounts"), ReadBag(obs, "BankItemCounts"), pos);
    }

    /// <summary>Full template→count map from one ActorObservation container
    /// (Newtonsoft CLR shape: keyed by template id).</summary>
    private static Dictionary<uint, int> ReadBag(JsonElement observation, string field)
    {
        var map = new Dictionary<uint, int>();
        if (!observation.TryGetProperty(field, out var bag) || bag.ValueKind != JsonValueKind.Object)
            return map;
        foreach (var entry in bag.EnumerateObject())
        {
            if (uint.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var template)
                && entry.Value.TryGetInt32(out var count))
                map[template] = count;
        }
        return map;
    }

    private static int CountOf(Dictionary<uint, int> bag, uint template)
        => bag.TryGetValue(template, out var count) ? count : 0;

    /// <summary>Canonical, order-independent rendering of a template→count map
    /// so two snapshots compare byte-for-byte (a refusal must leave the WHOLE
    /// container unchanged, not just the named template).</summary>
    private static string BagText(Dictionary<uint, int> bag)
        => bag.Count == 0
            ? "empty"
            : string.Join(";", bag.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>Same canonical rendering with ONE template excluded, so a
    /// successful verb (which legitimately moves that one stack) can still
    /// prove the rest of the container is byte-identical. A template that
    /// dropped to zero is rendered as absent on both sides (the seed-delta
    /// check owns its count).</summary>
    private static string BagTextExcept(Dictionary<uint, int> bag, uint excluded)
        => BagText(bag.Where(kv => kv.Key != excluded).ToDictionary(kv => kv.Key, kv => kv.Value));

    // ---- bridge probes (read-only / staging only) ---------------------------

    private static LiveDoodad ReadDoodad(BotDriveClient bridge, uint objId)
    {
        if (objId == 0)
            return new LiveDoodad(false, 0, 0, 0, 0f, 0f, 0f);
        try
        {
            var status = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"status\",\"bot\":\"{BotName}\",\"objIds\":[{objId}]}}", 30_000);
            var entry = status.GetProperty("doodads").EnumerateArray().First();
            return new LiveDoodad(
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
            return new LiveDoodad(false, 0, 0, 0, 0f, 0f, 0f);
        }
    }

    private static PosVec ReadPosVec(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            return new PosVec((float)GetDbl(el, "x"), (float)GetDbl(el, "y"), (float)GetDbl(el, "z"));
        }
        catch
        {
            return new PosVec(float.NaN, float.NaN, float.NaN);
        }
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

    // ---- MySQL consequence surface ------------------------------------------

    /// <summary>The character's planted public-Farm crop count — the exact
    /// surface PublicFarmManager.CanPlace's cap branch counts (this character's
    /// 2259 doodads bound to FarmType.Farm). -1 when the read fails.</summary>
    private static long CountPlantedFarmDoodads(uint charId)
    {
        if (charId == 0)
            return -1;
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM doodads WHERE owner_id = @owner AND template_id = @template AND farm_type = @farm";
            cmd.Parameters.AddWithValue("@owner", charId);
            cmd.Parameters.AddWithValue("@template", CropDoodad2259);
            cmd.Parameters.AddWithValue("@farm", FarmTypeFarm);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch
        {
            return -1;
        }
    }

    private static DoodadRow? TryReadDoodadRow(uint dbId)
    {
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, owner_id, template_id, current_phase_id, farm_type, x, y, z, item_id, item_template_id " +
                "FROM doodads WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", dbId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new DoodadRow(
                reader.GetUInt32(0),
                reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                reader.GetUInt32(2),
                reader.GetUInt32(3),
                reader.GetInt32(4),
                reader.GetFloat(5),
                reader.GetFloat(6),
                reader.GetFloat(7),
                reader.GetUInt64(8),
                reader.GetUInt32(9));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes every doodad this run planted (the run's own rows only:
    /// owner_id is the run's fresh character id).</summary>
    private static string CleanupDoodads(uint charId)
    {
        if (charId == 0)
            return "doodads cleanup skipped (no charId)";
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM doodads WHERE owner_id = @owner";
            cmd.Parameters.AddWithValue("@owner", charId);
            return $"DELETE FROM doodads WHERE owner_id={charId} ({cmd.ExecuteNonQuery()} row(s))";
        }
        catch (Exception ex)
        {
            return $"doodads cleanup failed ({ex.GetType().Name}: {ex.Message})";
        }
    }

    // ---- JSON readers -------------------------------------------------------

    private static uint GetUInt(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static float GetFloat(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var n) ? n : float.NaN;

    private static double GetDbl(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;

    private static string GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string FormatFloat(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

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
                Console.WriteLine("[w3-plant] " + line);
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
                    Console.WriteLine("[w3-plant] " + kline);
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

    /// <summary>
    /// POST /api/actors/plant (the capability under test) + poll to terminal.
    /// A poll timeout is returned as data (empty Poll + the timeout message),
    /// never thrown — the caller labels it RUN/action-deadline.
    /// </summary>
    private static async Task<(JsonElement Poll, Guid Trace, string Timeout)> PlantAsync(
        HttpClient http, string botName, uint seed, PosVec pos, string key)
    {
        var body = FormattableString.Invariant(
            $"{{\"bot\":\"{botName}\",\"seedItemTemplateId\":{seed},\"x\":{FormatFloat(pos.X)},\"y\":{FormatFloat(pos.Y)},\"z\":{FormatFloat(pos.Z)},\"zRot\":0,\"scale\":1,\"idempotencyKey\":\"{key}\"}}");
        var res = await PostJsonAsync(http, "/api/actors/plant", body);
        var trace = res.GetProperty("trace_id").GetGuid();
        try
        {
            return (await PollTerminalAsync(http, trace, TimeSpan.FromSeconds(ActionDeadlineSeconds)), trace, "");
        }
        catch (TimeoutException ex)
        {
            return (default, trace, ex.Message);
        }
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
                scenario = "w3-plant-capability",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim,
                notClaimed = new[]
                {
                    "harvest / watering / rot / uproot chains",
                    "autonomy and the needs-farm loop",
                    "the economy loop (buy/sell)",
                    "farm ownership/expiry guards beyond the placement gate (24 h defense timer)",
                    "cell occupancy (no such engine gate exists — DoodadManager.CreatePlayerDoodad has no overlap check; PublicFarmManager.CanPlace gates type + farm_groups.count only)",
                    "wire CSCreateDoodadPacket parity"
                },
                callPath = "POST /api/actors/plant {bot, seedItemTemplateId, x, y, z, zRot, scale, idempotencyKey} → BotActionController.Plant → BotActionSpec(Plant, Destination, PlantActionParams) → BotActionCommandQueue.ExecuteKind (case Plant) → IGameplayActor.Plant → GameplayActor.Plant (finite pos → seed in bag → GetDoodadIdFromItem + template exists → PublicFarmManager.InPublicFarm/GetFarmType → CanPlace type + farm_groups count → HousingManager.GetHouseAtLocation/AllowedToInteract → labor gate (skill 25536 consume_lp 1) → ChangeLabor(-cost) → DoodadManager.CreatePlayerDoodad (ConsumeItem + InitDoodad + Spawn + Save) → Complete(doodad.ObjId)) ; observation = POST /api/actors/observe (ActorObservation: Money/BankMoney/LaborPower/BagItemCounts/BankItemCounts/Position) with bridge farm status/find + MySQL doodads rows as independent corroboration — the gate adds no gameplay verb of its own",
                fixtureData = new
                {
                    seed = "15659 감자 씨앗 → items.use_skill_id 25536 (consume_lp 1) → item_spawn_doodads(129, 15659, 2259)",
                    doodad = "2259 감자 (doodad_almighties group 12); seedling phase 4379 → (doodad_func_groups 4379 almond_almighty 2259)",
                    farm = "farm_group_doodads(4, farm_group_id 1, 2259, 15659); farm_groups(1, '공용 농장', count 10); PublicFarmManager._farmZones {966, 998 → FarmType.Farm}",
                    refusals = "(a) 15646 볍씨 plantable (doodad 2246 IS farm group 1) but unstocked ⇒ bag gate; (b) 7992 감자 bagged with NO item_spawn_doodads row ⇒ mapping gate; (c) standing on a Farm cell with 15647 귀리 씨앗 (doodad 2247 in NO farm group) ⇒ CanPlace type gate; (d) labor drained 1→0 by leg 1, open-ground plant costs 1 ⇒ labor gate; (e) 10 legal Farm plants reach farm_groups(1).count, the 11th ⇒ CanPlace cap gate (same message as the type branch — the held count is the discriminant)",
                    staging = "the Nuian spawn is ~2 km from the nearest Farm subzone (beyond the probe's 500 m cap), so the Farm site is staged at the Lilyut farm-966 anchor via bridge farm place + soil probe, and the cap walk re-places on the probed soil cell (staging only, never a gameplay verb)"
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
            // (logs/w3-plant-report.<utc>.json); the bare w3-plant-report.json
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
            // Evidence write is best-effort; the xUnit verdict is authoritative.
            Console.WriteLine($"W3-PLANT-REPORT-ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}
