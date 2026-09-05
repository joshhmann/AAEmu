using System.Collections.Concurrent;
using System.Diagnostics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;

using Portal = AAEmu.Game.Models.Game.Portal;

using Microsoft.Extensions.Time.Testing;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M6.2 death watch (death/resurrection — the 6.2 safety item that did not
/// exist): a dead bot (IsDead ⇔ Hp &lt;= 0) gets no work steps; the
/// scheduler polls the corpse and resurrects it through the real
/// CharacterResurrection path once ResurrectDelay elapses, then normal
/// stepping resumes. The rig mirrors PlayerBotSchedulerTests (FakeTimeProvider,
/// manual scan pumps, drain-driven waits). Portal lookup is injected with an
/// X == 0 portal so the server-side relocation move (Character.SetPosition —
/// needs a registry-resident world) stays out of the unit rig; relocation is
/// exercised on the live stack.
/// </summary>
[NotInParallel]
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
public class PlayerBotSchedulerDeathWatchTests
{
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(Character character, object? botContext) => true;
        public bool Deactivate(Character character, string reason) => true;
    }

    private sealed class RecordingExecutor : IBotStepExecutor
    {
        public ConcurrentQueue<uint> Starts { get; } = [];
        public int CountStarts(uint botId) => Starts.Count(id => id == botId);

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            Starts.Enqueue(bot.CharacterId);
            return Task.FromResult<TimeSpan?>(null);
        }
    }

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(DateTime.UtcNow);
        public DateTime Now => Time.GetUtcNow().UtcDateTime;
        public RecordingExecutor Executor { get; } = new();
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }

        public Rig(bool resurrectionEnabled = true, int resurrectDelayMs = 5000, int deathPollMs = 1000)
        {
            // Character.MaxHp/Mp compute through FormulaManager — seed the
            // shared singletons (idempotent, safe in any suite ordering).
            GameplayActorTestRig.Seed();
            Manager = new PlayerBotManager(new RecordingLifecycle());
            Scheduler = new PlayerBotScheduler(Manager, Executor, new PlayerBotSchedulerOptions
            {
                ScanInterval = TimeSpan.FromHours(1), // background loop inert; cycles pumped manually
                SubscribeToTickManager = false,
                ResurrectionEnabled = resurrectionEnabled,
                ResurrectDelay = TimeSpan.FromMilliseconds(resurrectDelayMs),
                DeathPollInterval = TimeSpan.FromMilliseconds(deathPollMs),
                // X == 0 → no server-side relocation in the rig (the same
                // condition the packet path uses to pick its broadcast).
                PortalResolver = _ => new Portal { X = 0, Y = 0, Z = 0 },
            }, Time);
            Scheduler.Start();
        }

        /// <summary>Adds an ACTIVE bot; alive by default, dead when alive=false (Hp 0/100).</summary>
        public uint AddBot(uint id, bool alive = true)
        {
            var character = new Character(new UnitCustomModelParams()) { Id = id, Name = $"bot{id}" };
            character.MaxHp = 100;
            character.MaxMp = 50;
            character.Hp = alive ? 100 : 0;
            character.Mp = alive ? 50 : 0;
            Manager.Spawn(character, "rig");
            Manager.Activate(id, null, "rig");
            return id;
        }

        public Character CharacterOf(uint id)
            => Manager.TryGet(id, out var runtime) ? runtime!.Character : throw new InvalidOperationException("no runtime");

        public void Pump() => Scheduler.RunScanCycle();

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                Scheduler.DrainTickQueue();
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("rig wait condition not met");
                await Task.Delay(5);
            }
        }

        /// <summary>Advances the fake clock, pumps a scan cycle and drains the marshal queue.</summary>
        public async Task AdvanceAndPumpAsync(TimeSpan delta)
        {
            Time.Advance(delta);
            Pump();
            // The worker pool marshals asynchronously — give it a beat, then
            // drain on the test thread (the simulated game loop).
            await Task.Delay(20);
            Scheduler.DrainTickQueue();
            await Task.Delay(20);
            Scheduler.DrainTickQueue();
        }

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();
    }

    [Test]
    public async Task DeadBot_GetsNoWorkSteps_AndResurrectsAfterDelay()
    {
        using var rig = new Rig(resurrectDelayMs: 5000, deathPollMs: 1000);
        var bot = rig.AddBot(1, alive: false);

        // Wake immediately: the step is skipped (dead), death watch starts polling.
        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Pump();
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));
        rig.Scheduler.DrainTickQueue();

        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(0);
        await Assert.That(rig.CharacterOf(bot).IsDead).IsTrue();
        await Assert.That(rig.Scheduler.GetMetrics().TotalResurrections).IsEqualTo(0);

        // Before the delay elapses: still dead, still no work.
        await rig.AdvanceAndPumpAsync(TimeSpan.FromMilliseconds(2000));
        await Assert.That(rig.CharacterOf(bot).IsDead).IsTrue();
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(0);

        // Past the delay: the death watch resurrects (10% HP/MP through the
        // real CharacterResurrection path) and clears the death state.
        await rig.AdvanceAndPumpAsync(TimeSpan.FromMilliseconds(3100));
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalResurrections == 1);

        var character = rig.CharacterOf(bot);
        await Assert.That(character.IsDead).IsFalse();
        // 10% restore through the real path — MaxHp/Mp are formula-computed
        // (seeded FormulaManager), so assert the ratio against the live max.
        await Assert.That(character.Hp).IsEqualTo((int)(character.MaxHp * 0.1));
        await Assert.That(character.Mp).IsEqualTo((int)(character.MaxMp * 0.1));
    }

    [Test]
    public async Task ResurrectedBot_ResumesNormalStepping()
    {
        using var rig = new Rig(resurrectDelayMs: 1000, deathPollMs: 200);
        var bot = rig.AddBot(1, alive: false);

        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Pump();
        // Drive the death watch forward: both the poll cadence and the
        // delay comparison run on the fake clock, so advance it between
        // pumps until the watch resurrects the bot.
        for (var i = 0; i < 20 && rig.Scheduler.GetMetrics().TotalResurrections == 0; i++)
            await rig.AdvanceAndPumpAsync(TimeSpan.FromMilliseconds(300));
        await Assert.That(rig.Scheduler.GetMetrics().TotalResurrections).IsEqualTo(1);

        // Alive again: the next due wake runs a normal work step.
        await rig.AdvanceAndPumpAsync(TimeSpan.FromMilliseconds(300));
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) >= 1);
        await Assert.That(rig.CharacterOf(bot).IsDead).IsFalse();
    }

    [Test]
    public async Task ResurrectionDisabled_DeadBotKeepsWorking_Undisturbed()
    {
        using var rig = new Rig(resurrectionEnabled: false);
        var bot = rig.AddBot(1, alive: false);

        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        // The death watch is off: steps execute as before (pre-watch
        // behavior), no resurrection happens.
        await Assert.That(rig.CharacterOf(bot).IsDead).IsTrue();
        await Assert.That(rig.Scheduler.GetMetrics().TotalResurrections).IsEqualTo(0);
    }

    [Test]
    public async Task Resurrect_InPlace_UsesResurrectPercents_AndClearsPvpFlags()
    {
        // CharacterResurrection core, direct: the in-place (player-res) path
        // restores by ResurrectHp/MpPercent and applies NO debuffs.
        GameplayActorTestRig.Seed(); // MaxHp/Mp compute through FormulaManager
        var character = new Character(new UnitCustomModelParams()) { Id = 42, Name = "res-test" };
        character.MaxHp = 1000;
        character.MaxMp = 200;
        character.Hp = 0;
        character.Mp = 0;
        character.ResurrectHpPercent = 50;
        character.ResurrectMpPercent = 25;
        character.DiedInPvp = true;

        var portal = CharacterResurrection.Resurrect(character, inPlace: true,
            closestPortalResolver: _ => new Portal { X = 0 });

        await Assert.That(character.Hp).IsEqualTo((int)(character.MaxHp * 0.5));
        await Assert.That(character.Mp).IsEqualTo((int)(character.MaxMp * 0.25));
        await Assert.That(character.ResurrectHpPercent).IsEqualTo(1u);
        await Assert.That(character.DiedInPvp).IsFalse();
        await Assert.That(portal.X).IsEqualTo(0f);
    }

    [Test]
    public async Task Resurrect_PvE_PortalPath_RestoresTenPercent_AndResetsBreath()
    {
        GameplayActorTestRig.Seed();
        var character = new Character(new UnitCustomModelParams()) { Id = 43, Name = "res-test-2" };
        character.MaxHp = 800;
        character.MaxMp = 100;
        character.Hp = 0;
        character.Mp = 0;
        character.IsUnderWater = true;
        character.Breath = 0;

        CharacterResurrection.Resurrect(character, inPlace: false,
            closestPortalResolver: _ => new Portal { X = 0 });

        await Assert.That(character.Hp).IsEqualTo((int)(character.MaxHp * 0.1));
        await Assert.That(character.Mp).IsEqualTo((int)(character.MaxMp * 0.1));
        await Assert.That(character.IsUnderWater).IsFalse();
        await Assert.That(character.Breath).IsEqualTo(character.LungCapacity);
    }
}
