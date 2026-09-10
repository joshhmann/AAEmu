using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using static AAEmu.Game.Core.Managers.Bots.FishingContestScenario;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M9.5 fishing-contest scenario rig (locked launch activity exit test):
/// scheduled event start → finish with bot participants + human-or-stand-in,
/// auditable results (entries, scores, winner, ledger-settled rewards),
/// world-budget observance.
///
/// Fail-pre discipline: every refusal test FAILS if its gate is removed
/// (the event would run casts / move money it must not), and every pass
/// test pins exact ledger/audit numbers a broken settlement would change.
/// All fishing legs run through the REAL <see cref="GameplayActor.CastAt"/>
/// path with the seeded TestPosSkillId minimal plot (the same plot runtime
/// live 21571/plot 809 executes through — only the event payload differs).
/// The sports stratum stays recorded-open: no test touches SpawnFishEffect,
/// DoodadFuncCatch, DoodadFuncFishSchool, or the Convert/BuyFish seams.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class FishingContestScenarioTests
{
    private static readonly Vector3 WaterPosition = new(50f, 75f, 100f);

    /// <summary>Unique high-base world ids (CastAt-test discipline: no collision with sibling lanes).</summary>
    private static uint s_nextWorldId = 0x5100_0000;

    private static FishingContestScenario.ContestOptions Options(
        string contestId, DateTimeOffset now,
        DateTimeOffset? opensAt = null, DateTimeOffset? closesAt = null,
        Func<ServerPressure>? pressureProbe = null) => new()
        {
            ContestId = contestId,
            FishingSkillId = GameplayActorTestRig.TestPosSkillId,
            ReagentItemTemplateId = GameplayActorTestRig.TestItemTemplateId,
            CastPosition = WaterPosition,
            WinnerRewardCopper = 1000,
            OpensAt = opensAt ?? now.AddMinutes(-5),
            ClosesAt = closesAt ?? now.AddMinutes(5),
            UtcNow = () => now,
            PressureProbe = pressureProbe ?? (() => ServerPressure.Healthy),
        };

    private void RegisterWorldForPosCast(HeadlessSession session)
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(WorldInstance)
            .GetField("<Id>k__BackingField", Flags)!
            .SetValue(session.World, (uint)System.Threading.Interlocked.Increment(ref s_nextWorldId));
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager)
                .GetField("_worlds", Flags)!
                .GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorlds.Add(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    private readonly List<WorldInstance> _registeredWorlds = [];

    [After(Test)]
    public void TearDown()
    {
        const System.Reflection.BindingFlags Flags2 = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager)
                .GetField("_worlds", Flags2)!
                .GetValue(WorldManager.Instance)!;
        foreach (var world in _registeredWorlds)
        {
            if (worlds.TryGetValue(world.Id, out var registered) && ReferenceEquals(registered, world))
                worlds.TryRemove(world.Id, out _);
        }
        _registeredWorlds.Clear();
        FishingContestScenario.Enabled = false;
    }

    /// <summary>Creates a contest caster that knows the seeded pos-target skill, stocked with worms.</summary>
    private (GameplayActor Actor, HeadlessSession Session) CreateContestCaster(string name, int worms)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorldForPosCast(session);
        actor.Character.Skills.AddSkill(new SkillTemplate { Id = GameplayActorTestRig.TestPosSkillId }, 1, false);
        if (worms > 0)
            GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, worms);
        return (actor, session);
    }

    /// <summary>Best-effort isolation: lets background plot states end so nothing bleeds across tests.</summary>
    private static async Task WaitForPlots(IEnumerable<GameplayActor> actors)
    {
        var guard = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < guard)
        {
            if (actors.All(a => a.Character.ActivePlotState == null))
                return;
            await Task.Delay(25);
        }
    }

    [Test]
    public async Task Run_DisabledContest_RefusedBeforeAnyCast()
    {
        FishingContestScenario.Enabled = false;
        var (a1, _) = CreateContestCaster("m95-dis-1", 3);
        var (a2, _) = CreateContestCaster("m95-dis-2", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, true)],
            Options("m95-dis", now));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PRECHECK");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Fail-pre: without the kill-switch gate the casts would run — the
        // audit must show zero CastAt records and untouched balances.
        await Assert.That(a1.AuditTrace.Count + a2.AuditTrace.Count).IsEqualTo(0);
        await Assert.That(a1.Character.Money + a2.Character.Money).IsEqualTo(0);
    }

    [Test]
    public async Task Run_ScheduleClosed_RefusedBeforeAnyCast()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-sch-1", 3);
        var (a2, _) = CreateContestCaster("m95-sch-2", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, true)],
            Options("m95-sch", now, opensAt: now.AddHours(1), closesAt: now.AddHours(2)));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("SCHEDULE");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(a1.AuditTrace.Count + a2.AuditTrace.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Run_RosterWithoutStandIn_Refused()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-ros-1", 3);
        var (a2, _) = CreateContestCaster("m95-ros-2", 3);
        var now = DateTimeOffset.UtcNow;

        // Fail-pre: without the roster gate two bots alone would fish a
        // "contest" with no human-or-stand-in — the M9.5 exit test requires one.
        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, false)],
            Options("m95-ros", now));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("ROSTER");
        await Assert.That(a1.AuditTrace.Count + a2.AuditTrace.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Run_RosterWithoutBots_Refused()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-rosb-1", 3);
        var (a2, _) = CreateContestCaster("m95-rosb-2", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, true), new ContestEntrant(a2, true)],
            Options("m95-rosb", now));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("ROSTER");
        await Assert.That(a1.AuditTrace.Count + a2.AuditTrace.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Run_FullContest_ScoresWinnerAndSettlesLedger()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-full-1", 3);
        var (a2, _) = CreateContestCaster("m95-full-2", 3);
        var (a3, _) = CreateContestCaster("m95-full-3", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, false), new ContestEntrant(a3, true)],
            Options("m95-full", now));

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Entries.Count).IsEqualTo(3);
        // Every stocked entrant completes through the real CastAt path; the
        // minimal plot grants no loot, so fish stays 0 rig-side (live plot
        // 809 rolls real loot packs — same scoring rule, live differentiation).
        foreach (var entry in result.Entries)
        {
            await Assert.That(entry.CompletedCasts).IsEqualTo(1);
            await Assert.That(entry.CastState).IsEqualTo("Completed");
            await Assert.That(entry.FishGained).IsEqualTo(0);
        }
        // Deterministic tie-break: all scores equal → lowest actor ObjId wins.
        var expectedWinner = new[] { a1.ActorId, a2.ActorId, a3.ActorId }.Min();
        await Assert.That(result.Winner).IsNotNull();
        await Assert.That(result.Winner!.ActorId).IsEqualTo(expectedWinner);
        // Ledger-settled rewards: winner banks exactly the prize with zero
        // net inventory drift (grant then deposit); losers untouched.
        var winnerActor = result.Winner.ActorId == a1.ActorId ? a1 : result.Winner.ActorId == a2.ActorId ? a2 : a3;
        await Assert.That(winnerActor.Character.Money2).IsEqualTo(1000);
        await Assert.That(winnerActor.Character.Money).IsEqualTo(0);
        foreach (var loser in new[] { a1, a2, a3 }.Where(a => a.ActorId != expectedWinner))
        {
            await Assert.That(loser.Character.Money).IsEqualTo(0);
            await Assert.That(loser.Character.Money2).IsEqualTo(0);
        }
        // Audit completeness: 3 casts + 1 settlement, every leg on the trace.
        await Assert.That(result.TraceRecords.Count).IsEqualTo(4);
        await Assert.That(result.Criteria.All(c => c.Passed)).IsTrue();
        // Canned record: fixed fields, sports stratum honestly open.
        var report = result.Report!;
        await Assert.That(report.EntrantCount).IsEqualTo(3);
        await Assert.That(report.BotCount).IsEqualTo(2);
        await Assert.That(report.StandInCount).IsEqualTo(1);
        await Assert.That(report.SportsStratum).IsEqualTo(FishingContestScenario.SportsStratumVerdict);
        await Assert.That(report.SportsStratum).IsEqualTo("recorded-open");
        await Assert.That(report.ParticipantCeiling).IsEqualTo(FishingContestScenario.MaxContestants);
        await Assert.That(report.Pressure).IsEqualTo("Healthy");
        await Assert.That(report.LedgerReconciled).IsTrue();
        await Assert.That(report.RewardCopper).IsEqualTo(1000);
        await Assert.That(report.WinnerActorId).IsEqualTo(expectedWinner);

        await WaitForPlots([a1, a2, a3]);
    }

    [Test]
    public async Task Run_RejectedCastScoresZero_CompleterWins()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-zero-1", 3);
        var (a2, _) = CreateContestCaster("m95-zero-2", 0); // starved: no worms
        var (a3, _) = CreateContestCaster("m95-zero-3", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, false), new ContestEntrant(a3, true)],
            Options("m95-zero", now));

        await Assert.That(result.Passed).IsTrue();
        var starved = result.Entries.Single(e => e.ActorId == a2.ActorId);
        await Assert.That(starved.CompletedCasts).IsEqualTo(0);
        await Assert.That(starved.CastState).IsEqualTo("Rejected");
        // The reagent pre-flight gate ran (the FISH-01 worm leg, observed as refusal).
        await Assert.That(starved.CastDetail.Contains("missing reagent")).IsTrue();
        // A rejected cast never wins while a completed cast exists.
        await Assert.That(result.Winner).IsNotNull();
        await Assert.That(result.Winner!.ActorId).IsNotEqualTo(a2.ActorId);
        await Assert.That(result.Winner.CompletedCasts).IsEqualTo(1);

        await WaitForPlots([a1, a2, a3]);
    }

    [Test]
    public async Task Run_AllCastsRejected_FailsClosedWithNoSettlement()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-allrej-1", 0);
        var (a2, _) = CreateContestCaster("m95-allrej-2", 0);
        var now = DateTimeOffset.UtcNow;

        // Fail-pre: without the completions gate the contest would crown a
        // winner (and pay the prize) when nothing was caught.
        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, true)],
            Options("m95-allrej", now));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("FISH");
        await Assert.That(result.Winner).IsNull();
        await Assert.That(a1.Character.Money + a1.Character.Money2 + a2.Character.Money + a2.Character.Money2).IsEqualTo(0);
    }

    [Test]
    public async Task Run_OverG1Ceiling_RefusedBeforeAnyCast()
    {
        FishingContestScenario.Enabled = true;
        var actors = new List<GameplayActor>();
        for (var i = 0; i < FishingContestScenario.MaxContestants + 1; i++)
        {
            var (actor, _) = CreateContestCaster($"m95-cap-{i:D2}", 1);
            actors.Add(actor);
        }
        var entrants = actors.Select((a, i) => new ContestEntrant(a, i % 2 == 0)).ToList();
        var now = DateTimeOffset.UtcNow;

        // Fail-pre: without the ceiling gate 26 contestants would fish —
        // the M8 C5 exit caps events at 25 embodied within G1 budgets.
        var result = FishingContestScenario.Run(entrants, Options("m95-cap", now));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("BUDGET");
        await Assert.That(actors.Sum(a => a.AuditTrace.Count)).IsEqualTo(0);
    }

    [Test]
    public async Task Run_HighPressure_HeldBeforeAnyCast()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-pres-1", 3);
        var (a2, _) = CreateContestCaster("m95-pres-2", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, true)],
            Options("m95-pres", now, pressureProbe: () => ServerPressure.High));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("BUDGET");
        await Assert.That(result.FailReason.Contains("pressure")).IsTrue();
        await Assert.That(a1.AuditTrace.Count + a2.AuditTrace.Count).IsEqualTo(0);
        await Assert.That(result.Report!.Pressure).IsEqualTo("High");
    }

    [Test]
    public async Task Run_DuplicateContestId_RefusedWithoutMovingMoney()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-dup-1", 3);
        var (a2, _) = CreateContestCaster("m95-dup-2", 3);
        var now = DateTimeOffset.UtcNow;
        var entrants = new List<ContestEntrant> { new(a1, false), new(a2, true) };

        var first = FishingContestScenario.Run(entrants, Options("m95-dup", now));
        await Assert.That(first.Passed).IsTrue();
        var winnerActor = first.Winner!.ActorId == a1.ActorId ? a1 : a2;
        await Assert.That(winnerActor.Character.Money2).IsEqualTo(1000);

        // Fail-pre: without the settlement claim the same occurrence would
        // grant + bank the prize a second time (bank 2000).
        var second = FishingContestScenario.Run(entrants, Options("m95-dup", now));

        await Assert.That(second.Passed).IsFalse();
        await Assert.That(second.FailReason.Contains("already settled")).IsTrue();
        await Assert.That(winnerActor.Character.Money2).IsEqualTo(1000);
        await Assert.That(winnerActor.Character.Money).IsEqualTo(0);

        await WaitForPlots([a1, a2]);
    }

    [Test]
    public async Task Run_LedgerCorruption_FailsReconciliation()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-cor-1", 3);
        var (a2, _) = CreateContestCaster("m95-cor-2", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, true)],
            Options("m95-cor", now));
        await Assert.That(result.Passed).IsTrue();

        // Fail-pre: the stage-sum law must not be tautological — a forged
        // second payout breaks it.
        result.Ledger.Entries.Add(new FishingContestScenario.ContestLedgerEntry(
            "SETTLE", 999999,
            new FishingContestScenario.ContestMoneySnapshot(0, 0),
            new FishingContestScenario.ContestMoneySnapshot(0, 1000)));
        var sums = result.Ledger.ReconcileStageSums();

        await Assert.That(sums.Passed).IsFalse();
        await Assert.That(sums.Detail.Contains("MISMATCH")).IsTrue();

        await WaitForPlots([a1, a2]);
    }

    [Test]
    public async Task Run_SportsStratum_RecordedOpenOnEveryPass()
    {
        FishingContestScenario.Enabled = true;
        var (a1, _) = CreateContestCaster("m95-sport-1", 3);
        var (a2, _) = CreateContestCaster("m95-sport-2", 3);
        var now = DateTimeOffset.UtcNow;

        var result = FishingContestScenario.Run(
            [new ContestEntrant(a1, false), new ContestEntrant(a2, true)],
            Options("m95-sport", now));

        await Assert.That(result.Passed).IsTrue();
        // The contest never claims sports-stratum closure: the verdict is
        // recorded-open, and every scoring cast rode the basic CastAt action
        // (the audit trace holds CastAt legs only — no sports seam exists in
        // the actor vocabulary to invoke).
        await Assert.That(result.Report!.SportsStratum).IsEqualTo("recorded-open");
        await Assert.That(result.TraceRecords.Count(r => r.Action == ActorActionType.CastAt)).IsEqualTo(2);
        foreach (var entry in result.Entries)
            await Assert.That(entry.CastTraceId).IsNotEqualTo(Guid.Empty);

        await WaitForPlots([a1, a2]);
    }
}
