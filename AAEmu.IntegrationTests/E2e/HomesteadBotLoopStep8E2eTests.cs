using System.Globalization;
using System.Text;
using System.Text.Json;

using AAEmu.Game.Core.Managers;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Step-8 FINAL EXIT — ONE real bot loop on an isolated live lane, with no
/// fixture repair during the run.
///
/// The named loop (one bot, one continuous session, ordinary services only):
///
///   1. kit-opt-in        — the ONE labeled grant, BEFORE the loop (step-3 contract)
///   2. claim-place-plot  — SurveyAndPlacePlotAction → GameplayActor.BuildHouse
///                          → HousingManager.Build (house row, step 0)
///   3. observe           — the observation layer reports HasLandPlot
///   4. construct-plot    — ConstructPlotAction → GameplayActor.Interact(houseObjId, 18553)
///                          → Character.UseSkill → CraftEffect Building → House.AddBuildAction
///                          → CurrentStep -1 (the loop's second action)
///   5. recovery          — one NAMED failure + observed recovery:
///                          the same-key constructRetry is refused pre-flight
///                          (idempotency) while the frame state is unchanged,
///                          and a no-frame construct dispatches no request —
///                          both are refusals the loop recovers from, not repairs
///   6. repeat            — a SECOND full pass on a fresh bot/plot: claim + construct
///   7. restart           — RestartGameServer (Shipyard/B1 precedent: PID-handle
///                          kill of this lane's game tree only, never pkill),
///                          then the house row + current_step re-read from MySQL
///                          byte-equal to the pre-restart snapshot
///   8. conservation      — design/certs/money/labor reconciled end-to-end from
///                          the live pre/post values
///
/// Seeded/labeled, disclosed: the kit grant (step 3's opt-in) and ONE `homestead
/// move` positioning BEFORE the loop, both mirroring the needs-farm `farm place`
/// setup precedent. NO in-run repair: no GM grants, no money injection, no
/// house-row writes, no state overrides, no request completion. The reach leg is
/// asserted (the frame must be within MaxInteractRange at dispatch) rather than
/// assumed; the seam carries no movement drive.
///
/// Layer L (live authenticated server). H (human/client feel) stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class HomesteadBotLoopStep8E2eTests
{
    private const uint ScarecrowHousingId = 267;
    private const uint ScarecrowDesignItemId = 15596;
    private const uint BoundTaxCertItemId = 31892;
    private const int RequiredLaborForConstruct = 10;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BotLoop_ClaimConstructRecoverRepeatRestart_NoFixtureRepair()
    {
        AssertIsolatedLane();
        E2eStack.EnsureUp();

        var legs = new List<object>();
        var startedAt = DateTime.UtcNow;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);

        // ---- pass 1: real login flow (the Build path is connection-mediated)
        var botA = "Step8LoopA" + Guid.NewGuid().ToString("N")[..6];
        var acctA = "e2estep8a" + Guid.NewGuid().ToString("N")[..7];
        using var netA = await BotNetworkSession.ConnectAsync(
            botA, acctA, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        Assert.True(netA.InWorld, "pass-1 bot never reached in-world through the real login flow");
        var charA = netA.CharacterId;

        // ---- leg 1: labeled kit opt-in (the only grant; before the loop)
        var kitA = Call(bridge, new { cmd = "homestead", op = "kit", bot = botA });
        legs.Add(new
        {
            leg = "1-kit-opt-in",
            bot = botA,
            designs = kitA.GetProperty("designs").GetInt32(),
            certs = kitA.GetProperty("certs").GetInt32(),
            requiredCerts = kitA.GetProperty("requiredCerts").GetInt32()
        });
        var certsAfterKitA = kitA.GetProperty("certs").GetInt32();
        var requiredCertsA = kitA.GetProperty("requiredCerts").GetInt32();
        Assert.Equal(1, kitA.GetProperty("designs").GetInt32());
        Assert.Equal(requiredCertsA, certsAfterKitA);
        Assert.True(requiredCertsA > 0, "canonical certificate requirement did not resolve on the live server");

        // ---- setup (disclosed): move to a LEGAL placement position, once
        var spotsA = Call(bridge, new { cmd = "homestead", op = "positions", bot = botA });
        Assert.True(spotsA.GetProperty("count").GetInt32() > 0,
            "no housing area on this world accepts design 267 — the loop cannot place a plot");
        // The claim action builds at the NEAREST legal area's centroid, so the
        // setup step positions the bot at that area (the same coordinate the
        // action's own resolver will choose) — not an invented point.
        var spotA = spotsA.GetProperty("areas")[0];
        var placeA = Call(bridge, new
        {
            cmd = "homestead", op = "move", bot = botA,
            x = spotA.GetProperty("x").GetSingle(),
            y = spotA.GetProperty("y").GetSingle(),
            z = spotA.GetProperty("z").GetSingle()
        });
        legs.Add(new
        {
            leg = "2-setup-position",
            kind = "labeled setup (not a traversal proof)",
            legalAreas = spotsA.GetProperty("legalAreas").GetInt32(),
            reportedAreas = spotsA.GetProperty("count").GetInt32(),
            areaId = spotA.GetProperty("areaId").GetUInt32(),
            x = placeA.GetProperty("x").GetSingle(),
            y = placeA.GetProperty("y").GetSingle(),
            z = placeA.GetProperty("z").GetSingle()
        });

        var beforeA = Call(bridge, new { cmd = "homestead", op = "observe", bot = botA });
        Assert.False(beforeA.GetProperty("hasLandPlot").GetBoolean(), "bot already owned a plot before the claim");
        Assert.False(beforeA.GetProperty("plotConstructed").GetBoolean(), "bot already had a constructed plot before the claim");
        var laborBeforeA = beforeA.GetProperty("labor").GetInt32();
        var goldBeforeA = beforeA.GetProperty("gold").GetInt64();

        // ---- leg 3: claim-place-plot (the first action)
        var claimA = Call(bridge, new
        {
            cmd = "homestead", op = "claim", bot = botA, key = "step8-live-claim-1"
        }, 180_000);
        var claimStateA = claimA.GetProperty("state").GetString();
        var houseA = claimA.GetProperty("newHouses").GetArrayLength() > 0
            ? claimA.GetProperty("newHouses")[0]
            : default;
        legs.Add(new
        {
            leg = "3-claim-place-plot",
            state = claimStateA,
            failure = claimA.GetProperty("failure").GetString(),
            detail = claimA.GetProperty("detail").GetString(),
            targetId = claimA.GetProperty("targetId").GetUInt32(),
            houseId = claimA.GetProperty("newHouses").GetArrayLength() > 0 ? houseA.GetProperty("houseId").GetUInt32() : 0u,
            houseTemplate = claimA.GetProperty("newHouses").GetArrayLength() > 0 ? houseA.GetProperty("template").GetUInt32() : 0u,
            stepAfterClaim = claimA.GetProperty("newHouses").GetArrayLength() > 0 ? houseA.GetProperty("currentStep").GetInt32() : int.MinValue,
            designs = claimA.GetProperty("designs").GetInt32(),
            certs = claimA.GetProperty("certs").GetInt32(),
            money = claimA.GetProperty("money").GetInt64(),
            moneyBefore = claimA.GetProperty("moneyBefore").GetInt64(),
            ownedHouses = claimA.GetProperty("ownedHouses").GetInt32()
        });
        Assert.Equal("Completed", claimStateA);
        Assert.Equal(ScarecrowHousingId, claimA.GetProperty("targetId").GetUInt32());
        Assert.Equal(1, claimA.GetProperty("newHouses").GetArrayLength());
        Assert.Equal(0, houseA.GetProperty("currentStep").GetInt32());
        Assert.Equal(0, claimA.GetProperty("designs").GetInt32());
        Assert.Equal(certsAfterKitA - requiredCertsA, claimA.GetProperty("certs").GetInt32());
        Assert.Equal(claimA.GetProperty("moneyBefore").GetInt64(), claimA.GetProperty("money").GetInt64());

        // ---- leg 4: observe — the observation layer agrees with the live world
        var afterClaimA = Call(bridge, new { cmd = "homestead", op = "observe", bot = botA });
        legs.Add(new
        {
            leg = "4-observe-hasLandPlot",
            hasLandPlot = afterClaimA.GetProperty("hasLandPlot").GetBoolean(),
            plotConstructed = afterClaimA.GetProperty("plotConstructed").GetBoolean(),
            observedHouses = afterClaimA.GetProperty("observedHouses").GetInt32(),
            frameHouseId = afterClaimA.GetProperty("frameHouseId").GetUInt32(),
            frameObjId = afterClaimA.GetProperty("frameObjId").GetUInt32(),
            frameDistance = afterClaimA.GetProperty("frameDistance").GetSingle(),
            currentStep = afterClaimA.GetProperty("currentStep").GetInt32()
        });
        Assert.True(afterClaimA.GetProperty("hasLandPlot").GetBoolean(),
            "live observation did not report the owned plot");
        Assert.False(afterClaimA.GetProperty("plotConstructed").GetBoolean(),
            "plot reported constructed before the construct action ever ran");
        Assert.Equal(1, afterClaimA.GetProperty("observedHouses").GetInt32());
        Assert.True(afterClaimA.GetProperty("frameObjId").GetUInt32() > 0,
            "no live house frame resolved for the construct action");
        // Reach leg: the frame must already be inside the action's own range.
        var reachA = afterClaimA.GetProperty("frameDistance").GetSingle();
        Assert.True(reachA >= 0f && reachA <= 25f,
            $"frame is {reachA:F1}m away — outside the engine's 25m interaction range (reach leg unproved)");

        // ---- leg 5: recovery — a NAMED failure the loop recovers from
        // 5a. same-key retry: the engine's idempotency guard must refuse it
        //     while the world stays unchanged (no duplicate house/effect).
        var housesBeforeRetry = afterClaimA.GetProperty("observedHouses").GetInt32();
        var retryA = Call(bridge, new
        {
            cmd = "homestead", op = "claimRetry", bot = botA, key = "step8-live-claim-1"
        }, 60_000);
        var retryObserveA = Call(bridge, new { cmd = "homestead", op = "observe", bot = botA });
        legs.Add(new
        {
            leg = "5a-recovery-same-key-retry",
            state = retryA.GetProperty("state").GetString(),
            failure = retryA.GetProperty("failure").GetString(),
            detail = retryA.GetProperty("detail").GetString(),
            ownedHouses = retryA.GetProperty("ownedHouses").GetInt32(),
            designs = retryA.GetProperty("designs").GetInt32(),
            observedHousesAfter = retryObserveA.GetProperty("observedHouses").GetInt32(),
            recovered = retryObserveA.GetProperty("hasLandPlot").GetBoolean()
        });
        Assert.NotEqual("Completed", retryA.GetProperty("state").GetString());
        Assert.Equal(housesBeforeRetry, retryObserveA.GetProperty("observedHouses").GetInt32());
        Assert.True(retryObserveA.GetProperty("hasLandPlot").GetBoolean(),
            "the retry failure cost the bot its plot — the loop did not recover");

        // 5b. a second, independent named failure: a bot with a design but no
        //     plot frame has nothing legal to construct, so ConstructPlotAction
        //     dispatches NO request (the deleted stand-in branch would have
        //     fabricated one). The loop continues because the world is intact.
        var botNoFrame = "Step8NoFrame" + Guid.NewGuid().ToString("N")[..4];
        var acctNoFrame = "e2estep8nf" + Guid.NewGuid().ToString("N")[..6];
        using (var netNoFrame = await BotNetworkSession.ConnectAsync(
                   botNoFrame, acctNoFrame, "e2e-secret",
                   "127.0.0.1", E2eStack.LoginPort,
                   "127.0.0.1", E2eStack.GamePort,
                   "127.0.0.1", E2eStack.StreamPort))
        {
            Assert.True(netNoFrame.InWorld, "no-frame bot never reached in-world");
            var noFrameConstruct = Call(bridge, new
            {
                cmd = "homestead", op = "construct", bot = botNoFrame, key = "step8-noframe"
            }, 60_000);
            var noFrameObserve = Call(bridge, new { cmd = "homestead", op = "observe", bot = botNoFrame });
            legs.Add(new
            {
                leg = "5b-recovery-no-frame",
                dispatched = noFrameConstruct.GetProperty("dispatched").GetBoolean(),
                state = noFrameConstruct.GetProperty("state").GetString(),
                detail = noFrameConstruct.GetProperty("detail").GetString(),
                hasLandPlot = noFrameObserve.GetProperty("hasLandPlot").GetBoolean(),
                observedHouses = noFrameObserve.GetProperty("observedHouses").GetInt32()
            });
            Assert.False(noFrameConstruct.GetProperty("dispatched").GetBoolean(),
                "a bot with no plot frame produced a construct request — a stand-in target was used");
            Assert.False(noFrameObserve.GetProperty("hasLandPlot").GetBoolean());
            Assert.Equal(0, noFrameObserve.GetProperty("observedHouses").GetInt32());
        }

        // ---- leg 6: construct-plot — the SECOND action closing the loop.
        //
        // ENGINE FINDING (recorded, not worked around): GameplayActor's interact
        // gate admits 25 m (MaxInteractRange) while skill 18553's own engine
        // MaxRange is 10 m, so a construct dispatched from beyond 10 m is refused
        // by the skill pipeline with TooFarRange (observed live: log line
        // "TooFarRange targetDist=13.45, maxRangeCheck=10 ... Skill 18553").
        // This loop therefore dispatches from inside the skill's range; the
        // reach leg is judged against the skill gate below, not the actor gate.
        var constructA = Call(bridge, new
        {
            cmd = "homestead", op = "construct", bot = botA, key = "step8-live-construct-1"
        }, 180_000);
        // The request completing is NOT the postcondition: skill 18553 carries a
        // 10s cast time, so CraftEffect lands asynchronously on the game loop.
        // Observed completion is the polled live projection, never the request state.
        var afterConstructA = await WaitForConstructedAsync(bridge, botA, 90_000);
        legs.Add(new
        {
            leg = "6-construct-plot",
            state = constructA.GetProperty("state").GetString(),
            failure = constructA.GetProperty("failure").GetString(),
            detail = constructA.GetProperty("detail").GetString(),
            targetObjId = constructA.GetProperty("targetObjId").GetUInt32(),
            skillId = constructA.GetProperty("skillId").GetUInt32(),
            stepBefore = constructA.GetProperty("stepBefore").GetInt32(),
            stepAtRequestCompletion = constructA.GetProperty("stepAfter").GetInt32(),
            laborBefore = constructA.GetProperty("laborBefore").GetInt32(),
            labor = constructA.GetProperty("labor").GetInt32(),
            frameDistanceAtDispatch = afterClaimA.GetProperty("frameDistance").GetSingle(),
            buildSkillRange = afterClaimA.GetProperty("buildSkillRange").GetInt32(),
            plotConstructed = afterConstructA.GetProperty("plotConstructed").GetBoolean(),
            currentStep = afterConstructA.GetProperty("currentStep").GetInt32()
        });
        Assert.True(constructA.GetProperty("dispatched").GetBoolean(),
            "construct action dispatched no request on a bot that owns an unfinished frame");
        Assert.Equal("Completed", constructA.GetProperty("state").GetString());
        // The request targeted the RESOLVED frame ObjId, not the skill id.
        Assert.Equal(afterClaimA.GetProperty("frameObjId").GetUInt32(),
            constructA.GetProperty("targetObjId").GetUInt32());
        Assert.Equal(18553u, constructA.GetProperty("skillId").GetUInt32());
        Assert.Equal(0, constructA.GetProperty("stepBefore").GetInt32());
        Assert.True(afterConstructA.GetProperty("plotConstructed").GetBoolean(),
            "the observation layer did not report PlotConstructed after the construct action");
        Assert.Equal(-1, afterConstructA.GetProperty("currentStep").GetInt32());

        // ---- leg 7: conservation (pass 1), from live values only
        var laborAfterA = afterConstructA.GetProperty("labor").GetInt32();
        legs.Add(new
        {
            leg = "7-conservation-pass1",
            laborBefore = laborBeforeA,
            laborAfter = laborAfterA,
            laborDelta = laborBeforeA - laborAfterA,
            engineChargeLp = RequiredLaborForConstruct,
            status = "PROVED this run — observed net delta equals the canonical consume_lp (10) exactly. " +
                     "Measurement limitation: only the NET delta is observable live, so a coincident " +
                     "Unchained regen tick (10 LP/5 min) would mask the charge in principle; the charge " +
                     "itself is independently pinned at layer A against skills.consume_lp.",
            goldBefore = goldBeforeA,
            goldAfter = afterConstructA.GetProperty("gold").GetInt64(),
            designsBefore = 1,
            designsAfter = constructA.GetProperty("designs").GetInt32(),
            certsAfterKit = certsAfterKitA,
            certsAfterClaim = claimA.GetProperty("certs").GetInt32(),
            requiredCerts = requiredCertsA
        });
        Assert.Equal(0, claimA.GetProperty("designs").GetInt32());              // the claim consumed the design
        Assert.Equal(0, constructA.GetProperty("designs").GetInt32());          // construction consumes no design
        Assert.Equal(goldBeforeA, afterConstructA.GetProperty("gold").GetInt64());
        // LP: the engine charges skill 18553's consume_lp (10) in Skill.EndSkill,
        // asynchronously with the 10s cast. The live observation is the NET delta
        // across the construct leg, which must equal the canonical charge; a
        // coincident Unchained regen tick (10 LP / 5 min) would mask it, so the
        // charge is also pinned at layer A against skills.consume_lp.
        var laborDeltaA = laborBeforeA - laborAfterA;
        Assert.True(laborDeltaA >= 0,
            $"labor rose by {-laborDeltaA} across construction — the engine cannot grant labor without a regen tick");
        Assert.True(laborDeltaA == RequiredLaborForConstruct,
            $"observed LP delta {laborDeltaA} != the canonical consume_lp {RequiredLaborForConstruct} " +
            "(a coincident regen tick would show a smaller net delta)");

        // ---- leg 8: repeat — a SECOND full pass on a fresh bot/plot
        var botB = "Step8LoopB" + Guid.NewGuid().ToString("N")[..6];
        var acctB = "e2estep8b" + Guid.NewGuid().ToString("N")[..7];
        using var netB = await BotNetworkSession.ConnectAsync(
            botB, acctB, "e2e-secret",
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);
        Assert.True(netB.InWorld, "pass-2 bot never reached in-world");
        var charB = netB.CharacterId;

        var kitB = Call(bridge, new { cmd = "homestead", op = "kit", bot = botB });
        Assert.Equal(1, kitB.GetProperty("designs").GetInt32());

        var spotsB = Call(bridge, new { cmd = "homestead", op = "positions", bot = botB });
        Assert.True(spotsB.GetProperty("count").GetInt32() >= 2,
            "fewer than two engine-legal housing areas for design 267 — the repeat pass cannot place a distinct plot");
        // The claim action resolves the NEAREST legal area centroid, so the repeat
        // pass must stand nearest a DIFFERENT legal area than pass 1 used (the
        // first one is now occupied, and the engine's overlap rule — housing 267's
        // garden radius 4.0 => >= 8 m — would refuse a second plot there).
        var spotB = PickAreaOtherThan(spotsB,
            placeA.GetProperty("x").GetSingle(), placeA.GetProperty("y").GetSingle());
        Call(bridge, new
        {
            cmd = "homestead", op = "move", bot = botB,
            x = spotB.GetProperty("x").GetSingle(),
            y = spotB.GetProperty("y").GetSingle(),
            z = spotB.GetProperty("z").GetSingle()
        });

        var claimB = Call(bridge, new
        {
            cmd = "homestead", op = "claim", bot = botB, key = "step8-live-claim-2"
        }, 180_000);
        var claimStateB = claimB.GetProperty("state").GetString();
        var houseB = claimB.GetProperty("newHouses").GetArrayLength() > 0 ? claimB.GetProperty("newHouses")[0] : default;
        var houseBId = claimB.GetProperty("newHouses").GetArrayLength() > 0 ? houseB.GetProperty("houseId").GetUInt32() : 0u;
        Assert.Equal("Completed", claimStateB);
        Assert.Equal(1, claimB.GetProperty("newHouses").GetArrayLength());
        Assert.Equal(0, houseB.GetProperty("currentStep").GetInt32());

        // Same reach condition as pass 1: the construction dispatch must originate
        // inside skill 18553's own 10 m cast range (see the engine finding above).
        var frameObserveB = Call(bridge, new { cmd = "homestead", op = "observe", bot = botB });
        Assert.True(frameObserveB.GetProperty("frameObjId").GetUInt32() > 0,
            "pass 2 placed no live frame for the construct action");
        Assert.True(frameObserveB.GetProperty("frameDistance").GetSingle()
                    <= frameObserveB.GetProperty("buildSkillRange").GetInt32(),
            $"pass 2 frame is {frameObserveB.GetProperty("frameDistance").GetSingle():F1}m away — " +
            "outside skill 18553's cast range; the construct would be refused");
        var constructB = Call(bridge, new
        {
            cmd = "homestead", op = "construct", bot = botB, key = "step8-live-construct-2"
        }, 180_000);
        var afterConstructB = await WaitForConstructedAsync(bridge, botB, 90_000);
        legs.Add(new
        {
            leg = "8-repeat-second-pass",
            claimState = claimStateB,
            houseId = houseBId,
            stepAfterClaim = houseB.GetProperty("currentStep").GetInt32(),
            constructState = constructB.GetProperty("state").GetString(),
            stepAtRequestCompletion = constructB.GetProperty("stepAfter").GetInt32(),
            plotConstructed = afterConstructB.GetProperty("plotConstructed").GetBoolean(),
            currentStep = afterConstructB.GetProperty("currentStep").GetInt32()
        });
        Assert.Equal("Completed", constructB.GetProperty("state").GetString());
        Assert.True(afterConstructB.GetProperty("plotConstructed").GetBoolean(),
            "the repeat pass did not reach PlotConstructed");
        Assert.Equal(-1, afterConstructB.GetProperty("currentStep").GetInt32());

        // ---- pre-restart durable snapshot (MySQL, read-only)
        //
        // The engine's own save pass first (the B1 housing precedent): the row is
        // written on a house SaveManager tick, not synchronously with the action,
        // so an unsaved snapshot would read a stale current_step and the restart
        // comparison would misattribute a persistence lag to a restart defect.
        using (var saveBridge = new BotDriveClient(E2eStack.BridgePort))
        {
            var saved = saveBridge.Call("{\"cmd\":\"save\"}", 120_000);
            Assert.True(saved.GetProperty("saved").GetBoolean(), "bridge save pass did not complete");
        }
        var preA = SnapshotHouse(houseA.GetProperty("houseId").GetUInt32());
        var preB = SnapshotHouse(houseBId);
        Assert.Equal(-1, preA.CurrentStep);
        Assert.Equal(-1, preB.CurrentStep);
        Assert.Equal(charA, preA.Owner);
        Assert.Equal(charB, preB.Owner);

        // ---- leg 9: restart proof (PID-handle kill of THIS lane's game tree)
        var killedPid = E2eStack.RestartGameServer();
        Assert.True(killedPid > 0, "no game process was killed — cannot claim a restart");
        Assert.False(Directory.Exists($"/proc/{killedPid}"),
            $"killed game pid {killedPid} still exists after the restart gate");

        var postA = SnapshotHouse(houseA.GetProperty("houseId").GetUInt32());
        var postB = SnapshotHouse(houseBId);
        legs.Add(new
        {
            leg = "9-restart-byte-equal",
            killedPid,
            pass1 = DescribeRestart(preA, postA),
            pass2 = DescribeRestart(preB, postB),
            pass1ByteEqual = preA == postA,
            pass2ByteEqual = preB == postB
        });
        Assert.Equal(preA, postA);
        Assert.Equal(preB, postB);
        Assert.Equal(-1, postA.CurrentStep);
        Assert.Equal(-1, postB.CurrentStep);
        Assert.Equal(charA, postA.Owner);
        Assert.Equal(charB, postB.Owner);

        // ---- leg 10: post-restart live re-read — the engine re-loaded the
        //      finished frames from the DB and the in-world bot re-observes
        //      the constructed plot (durable state, not just a row).
        //
        // The restart killed this lane's game process, so pass 1's session died
        // with it. The SAME character (same account, same bot name) re-enters
        // through the real login flow: the observation layer then reads the
        // engine's post-load world, which is what "durable state" means here.
        using (var bridgeAfter = new BotDriveClient(E2eStack.BridgePort))
        {
            using var netAfter = await BotNetworkSession.ConnectAsync(
                botA, acctA, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(netAfter.InWorld, "post-restart bot never reached in-world");
            Assert.Equal(charA, netAfter.CharacterId);

            var reloadedA = Call(bridgeAfter, new { cmd = "homestead", op = "observe", bot = botA });
            legs.Add(new
            {
                leg = "10-post-restart-live-reread",
                bot = botA,
                characterId = netAfter.CharacterId,
                observedHouses = reloadedA.GetProperty("observedHouses").GetInt32(),
                hasLandPlot = reloadedA.GetProperty("hasLandPlot").GetBoolean(),
                plotConstructed = reloadedA.GetProperty("plotConstructed").GetBoolean(),
                currentStep = reloadedA.GetProperty("currentStep").GetInt32(),
                unfinishedFrameObjId = reloadedA.GetProperty("frameObjId").GetUInt32(),
                ownedHouseDbId = reloadedA.GetProperty("ownedHouseDbId").GetUInt32(),
                ownedHouseObjId = reloadedA.GetProperty("ownedHouseObjId").GetUInt32(),
                ownedHouseStep = reloadedA.GetProperty("ownedHouseStep").GetInt32()
            });
            Assert.True(reloadedA.GetProperty("hasLandPlot").GetBoolean(),
                "the reloaded world did not report the bot's plot after restart");
            Assert.True(reloadedA.GetProperty("plotConstructed").GetBoolean(),
                "the reloaded world did not report the constructed plot after restart");
            Assert.Equal(-1, reloadedA.GetProperty("currentStep").GetInt32());
            // Durable object, not just a row: the reloaded engine registered the
            // house as a live world object again, and the unfinished-frame
            // resolver correctly reports NOTHING because the plot is finished.
            Assert.True(reloadedA.GetProperty("ownedHouseObjId").GetUInt32() > 0,
                "the reloaded world did not register the owned house as a live object");
            Assert.Equal(preA.Id, reloadedA.GetProperty("ownedHouseDbId").GetUInt32());
            Assert.Equal(0u, reloadedA.GetProperty("frameObjId").GetUInt32());
        }

        var report = new
        {
            scenario = "step8-bot-loop-final-exit",
            lane = new
            {
                root = E2eStack.E2eRoot,
                dbPort = E2eStack.DbPort,
                loginPort = E2eStack.LoginPort,
                gamePort = E2eStack.GamePort,
                composeProject = E2eStack.ComposeProject
            },
            sourceRevision = E2eStack.SourceRevision,
            startedAtUtc = startedAt.ToString("O", CultureInfo.InvariantCulture),
            bots = new { pass1 = new { name = botA, characterId = charA }, pass2 = new { name = botB, characterId = charB } },
            layer = "L (live authenticated server); H stays UNKNOWN",
            fixtureNote = "labeled kit opt-in + one `homestead move` setup positioning BEFORE the loop " +
                          "(same disclosed-setup shape as the needs-farm `farm place` op); no in-run repair " +
                          "(no GM grants, no money injection, no labor injection, no house-row writes, no overrides, " +
                          "no request completion)",
            legs
        };
        Directory.CreateDirectory(EvidenceDir);
        var path = Path.Combine(EvidenceDir, "step8-bot-loop-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(EvidenceDir, "step8-bot-loop-report.md"), RenderMarkdown(report));
        Console.WriteLine($"[step8] LOOP PASS — report {path}");
    }

    // --------------------------------------------------------------- helpers

    private sealed record HouseRow(uint Id, uint AccountId, uint Owner, uint TemplateId,
        float X, float Y, float Z, int CurrentStep, int CurrentAction, uint FactionId);

    private static HouseRow SnapshotHouse(uint houseId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, account_id, owner, template_id, x, y, z, current_step, current_action, faction_id " +
            "FROM housings WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", houseId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"housings row {houseId} missing");
        return new HouseRow(
            reader.GetUInt32(0), reader.GetUInt32(1), reader.GetUInt32(2), reader.GetUInt32(3),
            reader.GetFloat(4), reader.GetFloat(5), reader.GetFloat(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetUInt32(9));
    }

    /// <summary>
    /// Picks the nearest engine-legal housing area whose centroid is a different
    /// PLACE from the one pass 1 used. The claim action resolves the nearest legal
    /// area's centroid, so a second distinct plot requires the repeat bot to stand
    /// nearest a different area (the engine's overlap rule — housing 267's garden
    /// radius 4.0 => >= 8 m separation — refuses a repeat plot on the same spot).
    /// Distinctness is measured on centroids, the coordinate the resolver actually
    /// builds at; both candidates come from the engine's own rule data.
    /// </summary>
    private static JsonElement PickAreaOtherThan(JsonElement positions, float usedX, float usedY,
        float minSeparation = 8f)
    {
        foreach (var area in positions.GetProperty("areas").EnumerateArray())
        {
            var dx = area.GetProperty("x").GetSingle() - usedX;
            var dy = area.GetProperty("y").GetSingle() - usedY;
            if (MathF.Sqrt((dx * dx) + (dy * dy)) >= minSeparation)
                return area;
        }
        throw new Xunit.Sdk.XunitException(
            $"no engine-legal housing area with a centroid >= {minSeparation}m from " +
            $"({usedX:F1},{usedY:F1}) — repeat pass cannot place a distinct plot " +
            $"(count={positions.GetProperty("count").GetInt32()})");
    }

    /// <summary>
    /// Polls the LIVE observation projection until the plot reports constructed.
    /// Skill 18553 carries a 10-second cast time and CraftEffect lands on the
    /// game loop, so the effect is asynchronous: the request reaching Completed
    /// is not the world-state postcondition. Bounded, and the last observation
    /// is returned (never a fabricated success) if the window expires.
    /// </summary>
    private static async Task<JsonElement> WaitForConstructedAsync(
        BotDriveClient bridge, string bot, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var last = Call(bridge, new { cmd = "homestead", op = "observe", bot });
        while (Environment.TickCount64 < deadline)
        {
            if (last.GetProperty("plotConstructed").GetBoolean())
                return last;
            await Task.Delay(500);
            last = Call(bridge, new { cmd = "homestead", op = "observe", bot });
        }
        return last;
    }

    private static object DescribeRestart(HouseRow pre, HouseRow post) => new
    {
        houseId = pre.Id,
        ownerBefore = pre.Owner,
        ownerAfter = post.Owner,
        templateBefore = pre.TemplateId,
        templateAfter = post.TemplateId,
        stepBefore = pre.CurrentStep,
        stepAfter = post.CurrentStep,
        x = post.X,
        y = post.Y,
        z = post.Z
    };

    /// <summary>
    /// One bridge round-trip. <see cref="BotDriveClient.Call(JsonElement,int)"/> already
    /// unwraps the {ok,data} envelope and throws on a bridge refusal, so the payload
    /// it returns IS the op's data object.
    /// </summary>
    private static JsonElement Call(BotDriveClient bridge, object request, int timeoutMs = 30_000)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        return bridge.Call(doc.RootElement, timeoutMs);
    }

    private static string RenderMarkdown(object report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Step 8 — final-exit bot loop (live lane)");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static void AssertIsolatedLane()
    {
        Assert.False(string.Equals(E2eStack.E2eRoot, "/root/aaemu-e2e", StringComparison.Ordinal),
            "step-8 L run refuses the shared default E2E_ROOT (isolated lane required)");
        Assert.NotEqual(3306, E2eStack.DbPort);
        Assert.Equal("127.0.0.1", E2eStack.GameHost);
    }
}
