using System.Collections.Concurrent;
using System.Diagnostics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PopulationDirector rig (slice #9): fidelity ladder + spec §11 safety gate +
/// spec §14 pressure policy + spec §15 density caps.
///
/// Real PlayerBotManager + real PlayerBotScheduler (FakeTimeProvider, manual
/// scan pumps, real worker pool), stubbed safety probe (settable per-condition
/// flags) and stubbed pressure probe (settable sample) so every gate condition
/// and every pressure band is deterministic. Zone/activity resolve through
/// rig dictionaries so density caps are exact.
/// </summary>
public class PopulationDirectorTests
{
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(Character character, object? botContext) => true;

        public bool Deactivate(Character character, string reason) => true;
    }

    /// <summary>Instant executor: returns null (dormant) — never delays, never reschedules.</summary>
    private sealed class NullExecutor : IBotStepExecutor
    {
        public ConcurrentQueue<uint> Starts { get; } = [];

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            Starts.Enqueue(bot.CharacterId);
            return Task.FromResult<TimeSpan?>(null);
        }
    }

    /// <summary>Settable per-condition safety probe (spec §11 gate).</summary>
    private sealed class StubSafetyProbe : IBotTransitionSafetyProbe
    {
        public bool InCombat { get; set; }
        public bool AttachedToSlave { get; set; }
        public bool CarryingTradePack { get; set; }
        public bool InTrial { get; set; }
        public bool GroupedWithHuman { get; set; }
        public bool Saving { get; set; }

        /// <summary>Per-bot overrides: botId → true means the condition is hot FOR THAT BOT.</summary>
        public HashSet<uint> InCombatBots { get; } = [];
        public HashSet<uint> AttachedToSlaveBots { get; } = [];

        public bool IsInCombat(Character character)
            => InCombat || InCombatBots.Contains(character.Id);

        public bool IsAttachedToSlave(Character character)
            => AttachedToSlave || AttachedToSlaveBots.Contains(character.Id);

        public bool IsCarryingTradePack(Character character) => CarryingTradePack;
        public bool IsInTrial(Character character) => InTrial;
        public bool IsGroupedWithHuman(Character character) => GroupedWithHuman;
        public bool IsSaving(Character character) => Saving;
    }

    /// <summary>Settable pressure probe (spec §14).</summary>
    private sealed class StubPressureProbe : IPressureProbe
    {
        public PressureSample SampleValue { get; set; } = PressureSample.Empty;

        public PressureSample Sample() => SampleValue;
    }

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(DateTime.UtcNow);
        public NullExecutor Executor { get; } = new();
        public StubSafetyProbe Safety { get; } = new();
        public StubPressureProbe PressureProbe { get; } = new();
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }
        public PopulationDirector Director { get; }

        /// <summary>Zone per bot id (resolver seam).</summary>
        public Dictionary<uint, uint> Zones { get; } = [];

        /// <summary>Activity per bot id (resolver seam).</summary>
        public Dictionary<uint, string?> Activities { get; } = [];

        /// <summary>Counts zone-resolver invocations (scan-work measurement seam).</summary>
        public long ZoneResolverCalls { get; set; }

        /// <summary>Counts activity-resolver invocations (scan-work measurement seam).</summary>
        public long ActivityResolverCalls { get; set; }

        public Rig(Action<PopulationDirectorOptions>? configureOptions = null)
        {
            Manager = new PlayerBotManager(new RecordingLifecycle());
            var schedulerOptions = new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1), // loop inert; cycles pumped manually
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false, // never touch the process-wide tick in tests
            };
            Scheduler = new PlayerBotScheduler(Manager, Executor, schedulerOptions, Time);
            Scheduler.Start();

            var directorOptions = new PopulationDirectorOptions();
            configureOptions?.Invoke(directorOptions);
            Director = new PopulationDirector(
                Manager,
                Scheduler,
                Safety,
                PressureProbe,
                directorOptions,
                zoneResolver: c =>
                {
                    ZoneResolverCalls++;
                    return Zones.TryGetValue(c.Id, out var z) ? z : 0;
                },
                activityResolver: c =>
                {
                    ActivityResolverCalls++;
                    return Activities.TryGetValue(c.Id, out var a) ? a : null;
                });
        }

        /// <summary>Spawns + activates a bot through the real manager registry.</summary>
        public uint AddActiveBot(uint id, string name = "bot")
        {
            // M6.2 death watch: rig bots must be ALIVE (Hp/MaxHp default to
            // 0, which is IsDead — the watch would intercept every step).
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 }, "rig");
            Manager.Activate(id, null, "rig");
            return id;
        }

        public void Pump() => Scheduler.RunScanCycle();

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                // M5 A1: worker threads only marshal steps to the execution
                // queue; the drain (the boundary) is what executes them.
                Scheduler.DrainTickQueue();
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("rig wait condition not met");
                await Task.Delay(5);
            }
        }

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();
    }

    #region Transition ladder (spec §11)

    [Test]
    public async Task AdjacentUpgrades_Apply_DormantToReducedToFull()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "test"))
            .IsEqualTo(FidelityTransitionResult.Applied);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Full, "test"))
            .IsEqualTo(FidelityTransitionResult.Applied);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Full);
    }

    [Test]
    public async Task NonAdjacentJumps_AreRejected()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Full, "jump"))
            .IsEqualTo(FidelityTransitionResult.NonAdjacentTransition);

        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "step");
        rig.Director.TrySetFidelity(bot, BotFidelity.Full, "step");
        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "jump"))
            .IsEqualTo(FidelityTransitionResult.NonAdjacentTransition);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Full);
    }

    [Test]
    public async Task UnknownBot_IsRejected()
    {
        using var rig = new Rig();

        await Assert.That(rig.Director.TrySetFidelity(999, BotFidelity.Reduced, "test"))
            .IsEqualTo(FidelityTransitionResult.UnknownBot);
        await Assert.That(rig.Director.Wake(999, "test"))
            .IsEqualTo(FidelityTransitionResult.UnknownBot);
        await Assert.That(rig.Director.Sleep(999, "test"))
            .IsEqualTo(FidelityTransitionResult.UnknownBot);
    }

    [Test]
    public async Task SameFidelity_IsNoChange()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "same"))
            .IsEqualTo(FidelityTransitionResult.NoChange);
    }

    #endregion

    #region Safety gate (spec §11 verbatim: combat / slave / trade pack / trial / human group / saving)

    [Test]
    public async Task Downgrade_BlockedInCombat()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Director.TrySetFidelity(bot, BotFidelity.Full, "up");
        rig.Safety.InCombat = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "down"))
            .IsEqualTo(FidelityTransitionResult.BlockedInCombat);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Full);
        await Assert.That(rig.Director.Sleep(bot, "sleep"))
            .IsEqualTo(FidelityTransitionResult.BlockedInCombat);
    }

    [Test]
    public async Task Downgrade_BlockedAttachedToSlave()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Safety.AttachedToSlave = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "down"))
            .IsEqualTo(FidelityTransitionResult.BlockedAttachedToSlave);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);
    }

    [Test]
    public async Task Downgrade_BlockedCarryingTradePack()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Safety.CarryingTradePack = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "down"))
            .IsEqualTo(FidelityTransitionResult.BlockedCarryingTradePack);
    }

    [Test]
    public async Task Downgrade_BlockedInTrial()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Safety.InTrial = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "down"))
            .IsEqualTo(FidelityTransitionResult.BlockedInTrial);
    }

    [Test]
    public async Task Downgrade_BlockedGroupedWithHuman()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Safety.GroupedWithHuman = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "down"))
            .IsEqualTo(FidelityTransitionResult.BlockedGroupedWithHuman);
    }

    [Test]
    public async Task Downgrade_BlockedSaving()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Safety.Saving = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "down"))
            .IsEqualTo(FidelityTransitionResult.BlockedSaving);
    }

    [Test]
    public async Task Upgrade_IsNeverBlockedBySafetyGate()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        // All six gate conditions hot — upgrades must still pass (gate guards downgrades only).
        rig.Safety.InCombat = true;
        rig.Safety.AttachedToSlave = true;
        rig.Safety.CarryingTradePack = true;
        rig.Safety.InTrial = true;
        rig.Safety.GroupedWithHuman = true;
        rig.Safety.Saving = true;

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up"))
            .IsEqualTo(FidelityTransitionResult.Applied);
        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Full, "up"))
            .IsEqualTo(FidelityTransitionResult.Applied);
    }

    [Test]
    public async Task Downgrade_Applied_WhenGateClear()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");

        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Dormant, "down"))
            .IsEqualTo(FidelityTransitionResult.Applied);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
    }

    #endregion

    #region Pressure policy (spec §14) → wake/sleep

    private static PressureSample BandedSample(ServerPressure band) => band switch
    {
        ServerPressure.Pressure => new PressureSample(0.60d, 60, 0, 0, 200d, 0),
        ServerPressure.High => new PressureSample(0.80d, 120, 0, 0, 800d, 0),
        ServerPressure.Critical => new PressureSample(0.95d, 300, 0, 0, 1500d, 0),
        _ => PressureSample.Empty,
    };

    [Test]
    public async Task HighPressure_RefusesNewWakes()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.PressureProbe.SampleValue = BandedSample(ServerPressure.High);
        rig.Director.RefreshPressure();
        await Assert.That(rig.Director.Pressure).IsEqualTo(ServerPressure.High);

        // Dormant → Reduced (a wake) refused at High (RefuseWakeAtOrAbove default High).
        await Assert.That(rig.Director.Wake(bot, "pressure-test"))
            .IsEqualTo(FidelityTransitionResult.PressureTooHigh);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
    }

    [Test]
    public async Task HighPressure_Sweep_DemotesFullToReduced_NotDormant()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");
        rig.Director.TrySetFidelity(bot, BotFidelity.Full, "up");

        rig.PressureProbe.SampleValue = BandedSample(ServerPressure.High);
        rig.Director.RefreshPressure();

        // High demotes Full→Reduced (DemoteFullAtOrAbove=High) but NOT
        // Reduced→Dormant (DemoteReducedAtOrAbove=Critical).
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);
        await Assert.That(rig.Director.Pressure).IsEqualTo(ServerPressure.High);
    }

    [Test]
    public async Task CriticalPressure_Sweep_DemotesReducedToDormant()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");

        rig.PressureProbe.SampleValue = BandedSample(ServerPressure.Critical);
        rig.Director.RefreshPressure();

        await Assert.That(rig.Director.Pressure).IsEqualTo(ServerPressure.Critical);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
        await Assert.That(rig.Director.EmbodiedCount).IsEqualTo(0);
    }

    [Test]
    public async Task PressureSweep_RespectsSafetyGate_BotInCombatStaysFull()
    {
        using var rig = new Rig();
        var combatBot = rig.AddActiveBot(1);
        var clearBot = rig.AddActiveBot(2);
        rig.Director.TrySetFidelity(combatBot, BotFidelity.Reduced, "up");
        rig.Director.TrySetFidelity(combatBot, BotFidelity.Full, "up");
        rig.Director.TrySetFidelity(clearBot, BotFidelity.Reduced, "up");
        rig.Director.TrySetFidelity(clearBot, BotFidelity.Full, "up");
        rig.Safety.InCombatBots.Add(combatBot); // ONLY bot 1 is in combat

        rig.PressureProbe.SampleValue = BandedSample(ServerPressure.Critical);
        rig.Director.RefreshPressure();

        // The in-combat bot may never be forced down — it survives the full
        // Critical sweep (Full→Reduced pass blocked, Reduced→Dormant pass never
        // reached). The clear bot is demoted through both passes to Dormant.
        await Assert.That(rig.Director.GetFidelity(combatBot)).IsEqualTo(BotFidelity.Full);
        await Assert.That(rig.Director.GetFidelity(clearBot)).IsEqualTo(BotFidelity.Dormant);
    }

    [Test]
    public async Task PressureSweep_WithClearGate_DemotesOnlyEligible()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Director.TrySetFidelity(botA, BotFidelity.Reduced, "up");
        rig.Director.TrySetFidelity(botA, BotFidelity.Full, "up");
        rig.Director.TrySetFidelity(botB, BotFidelity.Reduced, "up");

        rig.PressureProbe.SampleValue = BandedSample(ServerPressure.High);
        rig.Director.RefreshPressure();

        await Assert.That(rig.Director.GetFidelity(botA)).IsEqualTo(BotFidelity.Reduced); // Full→Reduced
        await Assert.That(rig.Director.GetFidelity(botB)).IsEqualTo(BotFidelity.Reduced); // untouched at High
    }

    [Test]
    public async Task HealthyPressure_WakeSucceeds_AndSchedulerExecutes()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.PressureProbe.SampleValue = PressureSample.Empty;
        rig.Director.RefreshPressure();
        await Assert.That(rig.Director.Pressure).IsEqualTo(ServerPressure.Healthy);

        await Assert.That(rig.Director.Wake(bot, "wake-test"))
            .IsEqualTo(FidelityTransitionResult.Applied);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);

        // The scheduler accepted the wake: pump a scan cycle → executor runs.
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.Starts.Contains(bot));
        await Assert.That(rig.Director.GetMetrics().TotalWakes).IsEqualTo(1);
    }

    [Test]
    public async Task Sleep_GateRespecting_SucceedsWhenClear()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");

        await Assert.That(rig.Director.Sleep(bot, "night"))
            .IsEqualTo(FidelityTransitionResult.Applied);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
        await Assert.That(rig.Director.GetMetrics().TotalSleeps).IsEqualTo(1);
    }

    #endregion

    #region Density caps (spec §15) — zone + activity

    [Test]
    public async Task ZoneDensityCap_BlocksWakeBeyondCap()
    {
        using var rig = new Rig(cfg =>
        {
            cfg.ZoneDensityCaps[101] = 1;
        });
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Zones[botA] = 101;
        rig.Zones[botB] = 101;

        await Assert.That(rig.Director.TrySetFidelity(botA, BotFidelity.Reduced, "up"))
            .IsEqualTo(FidelityTransitionResult.Applied);

        // Second bot into the same zone → cap reached.
        await Assert.That(rig.Director.TrySetFidelity(botB, BotFidelity.Reduced, "up"))
            .IsEqualTo(FidelityTransitionResult.DensityCapZoneReached);
        await Assert.That(rig.Director.GetFidelity(botB)).IsEqualTo(BotFidelity.Dormant);
    }

    [Test]
    public async Task ZoneDensityCap_OtherZonesUnaffected()
    {
        using var rig = new Rig(cfg =>
        {
            cfg.ZoneDensityCaps[101] = 1;
        });
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Zones[botA] = 101;
        rig.Zones[botB] = 102;

        rig.Director.TrySetFidelity(botA, BotFidelity.Reduced, "up");
        await Assert.That(rig.Director.TrySetFidelity(botB, BotFidelity.Reduced, "up"))
            .IsEqualTo(FidelityTransitionResult.Applied);
    }

    [Test]
    public async Task ActivityDensityCap_BlocksWakeBeyondCap()
    {
        using var rig = new Rig(cfg =>
        {
            cfg.ActivityDensityCaps["trade"] = 1;
        });
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Activities[botA] = "trade";
        rig.Activities[botB] = "trade";

        rig.Director.TrySetFidelity(botA, BotFidelity.Reduced, "up");
        await Assert.That(rig.Director.TrySetFidelity(botB, BotFidelity.Reduced, "up"))
            .IsEqualTo(FidelityTransitionResult.DensityCapActivityReached);
    }

    [Test]
    public async Task EscalationToFull_NotDensityCapped_ButPressureCapped()
    {
        using var rig = new Rig(cfg =>
        {
            cfg.ZoneDensityCaps[101] = 1;
        });
        var bot = rig.AddActiveBot(1);
        rig.Zones[bot] = 101;
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "up");

        // Escalation does not add an embodied bot → density cap does not apply.
        await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Full, "up"))
            .IsEqualTo(FidelityTransitionResult.Applied);

        // But escalation is pressure-capped (RefuseEscalationAtOrAbove=Pressure).
        rig.PressureProbe.SampleValue = BandedSample(ServerPressure.Pressure);
        rig.Director.RefreshPressure();
        await Assert.That(rig.Director.Pressure).IsEqualTo(ServerPressure.Pressure);

        var bot2 = rig.AddActiveBot(3);
        rig.Director.TrySetFidelity(bot2, BotFidelity.Reduced, "up");
        await Assert.That(rig.Director.TrySetFidelity(bot2, BotFidelity.Full, "up"))
            .IsEqualTo(FidelityTransitionResult.PressureTooHigh);
    }

    #endregion

    #region Metrics

    [Test]
    public async Task Metrics_ReflectFidelityCountsAndCounters()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Director.TrySetFidelity(botA, BotFidelity.Reduced, "up");
        rig.Director.TrySetFidelity(botA, BotFidelity.Full, "up");
        rig.Director.TrySetFidelity(botB, BotFidelity.Reduced, "up");
        rig.Safety.InCombat = true;
        rig.Director.TrySetFidelity(botA, BotFidelity.Reduced, "down"); // blocked

        var m = rig.Director.GetMetrics();
        await Assert.That(m.FullCount).IsEqualTo(1);
        await Assert.That(m.ReducedCount).IsEqualTo(1);
        await Assert.That(m.DormantCount).IsEqualTo(0);
        await Assert.That(m.Embodied).IsEqualTo(2);
        await Assert.That(m.TotalTransitionsApplied).IsEqualTo(3);
        await Assert.That(m.TotalTransitionsRejected).IsEqualTo(1);
        await Assert.That(m.Pressure).IsEqualTo(ServerPressure.Healthy);
    }

    #endregion

    #region Wake-storm scan budget (O(N²) → O(N·cap))

    [Test]
    public async Task WakeStormIntoCappedZone_ScanIsBudgeted_NotQuadratic()
    {
        const int zoneCap = 25;
        const int otherZoneBots = 200;
        const int wakeAttempts = 200;
        using var rig = new Rig(cfg =>
        {
            cfg.ZoneDensityCaps[101] = zoneCap;
        });

        // Seed 25 embodied bots IN zone 101 (zone is exactly at cap) with the
        // lowest ids — ConcurrentDictionary enumerates in bucket order, so the
        // matching entries cluster at the front of the scan.
        for (uint id = 1; id <= zoneCap; id++)
        {
            var bot = rig.AddActiveBot(id);
            rig.Zones[bot] = 101;
            rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "seed");
        }
        // 200 embodied bots in OTHER zones — an unbudgeted scan must walk past
        // every one of them per wake.
        for (uint id = 101; id < 101 + otherZoneBots; id++)
        {
            var bot = rig.AddActiveBot(id);
            rig.Zones[bot] = 999;
            rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "seed");
        }

        rig.ZoneResolverCalls = 0;

        // Wake storm: every attempt fails (zone at cap). Each blocked wake runs
        // the density scan; with break-early the scan stops at the cap-th in-zone
        // bot instead of walking all N non-dormant entries.
        for (uint id = 10001; id < 10001 + wakeAttempts; id++)
        {
            var bot = rig.AddActiveBot(id);
            rig.Zones[bot] = 101;
            await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "storm"))
                .IsEqualTo(FidelityTransitionResult.DensityCapZoneReached);
        }

        var totalResolverCalls = rig.ZoneResolverCalls;
        var unbudgetedSignature = (long)wakeAttempts * (zoneCap + otherZoneBots);

        // O(N²) signature would be one full scan per wake (225 resolver calls ×
        // 200 wakes = 45,000); the budgeted scan must stay well below half of that.
        await Assert.That(totalResolverCalls < unbudgetedSignature / 2)
            .IsTrue()
            .Because($"wake-storm density scan must be < O(N²) (< {unbudgetedSignature / 2} resolver calls, saw {totalResolverCalls})");

        // Per-wake scan work is bounded by O(cap), not O(N): with the cap at 25,
        // one wake must not cost more than ~3× cap resolver calls (bucket-order
        // collisions with non-matching bots included).
        await Assert.That(totalResolverCalls <= wakeAttempts * (zoneCap * 3))
            .IsTrue()
            .Because($"per-wake scan work must be O(cap) (≤ {wakeAttempts * (zoneCap * 3)}, saw {totalResolverCalls})");
    }

    [Test]
    public async Task WakeStormIntoCappedActivity_ScanIsBudgeted_NotQuadratic()
    {
        const int activityCap = 20;
        const int otherActivityBots = 150;
        const int wakeAttempts = 150;
        using var rig = new Rig(cfg =>
        {
            cfg.ActivityDensityCaps["trade"] = activityCap;
        });

        // Zone caps are uncapped here so only the ACTIVITY gate can reject — this
        // isolates the activity scan as the measured path.
        for (uint id = 1; id <= activityCap; id++)
        {
            var bot = rig.AddActiveBot(id);
            rig.Activities[bot] = "trade";
            rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "seed");
        }
        for (uint id = 101; id < 101 + otherActivityBots; id++)
        {
            var bot = rig.AddActiveBot(id);
            rig.Activities[bot] = "harvest";
            rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "seed");
        }

        rig.ZoneResolverCalls = 0;
        rig.ActivityResolverCalls = 0;

        for (uint id = 10001; id < 10001 + wakeAttempts; id++)
        {
            var bot = rig.AddActiveBot(id);
            rig.Activities[bot] = "trade";
            await Assert.That(rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "storm"))
                .IsEqualTo(FidelityTransitionResult.DensityCapActivityReached);
        }

        var totalResolverCalls = rig.ActivityResolverCalls;
        var unbudgetedSignature = (long)wakeAttempts * (activityCap + otherActivityBots);

        await Assert.That(totalResolverCalls < unbudgetedSignature / 2)
            .IsTrue()
            .Because($"activity wake-storm scan must be < O(N²) (< {unbudgetedSignature / 2} resolver calls, saw {totalResolverCalls})");
        await Assert.That(totalResolverCalls <= wakeAttempts * (activityCap * 3))
            .IsTrue()
            .Because($"per-wake activity scan work must be O(cap) (≤ {wakeAttempts * (activityCap * 3)}, saw {totalResolverCalls})");
    }

    #endregion
}
