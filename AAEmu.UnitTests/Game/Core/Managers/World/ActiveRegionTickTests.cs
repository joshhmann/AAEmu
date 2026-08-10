using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// H2 (P0 gate) rig — ActiveRegionTick must be time-budgeted so no single pass can
/// starve the tick loop (#1491). Fail-before: with 400 slow characters the old
/// synchronous full iteration runs 4x+ over the 100ms budget and blocks the tick
/// thread. Pass-after: the budgeted pass defers the remainder and completes across
/// passes, and the TickManager subscription dispatches async.
/// </summary>
public class ActiveRegionTickTests
{
    private const int SlowCharacterCount = 400;
    private const int BudgetMs = 100;

    /// <summary>Character whose region tick costs ~1ms of CPU — synthetic per-head load.</summary>
    private sealed class SlowCharacter : Character
    {
        public int TickCount { get; private set; }

        public SlowCharacter()
            : base(new UnitCustomModelParams())
        {
        }

        public override void OnActiveRegionTick(TimeSpan delta)
        {
            Thread.Sleep(1);
            TickCount++;
        }
    }

    [Test]
    public async Task ActiveRegionTick_UnderSyntheticLoad_RespectsBudget()
    {
        // Arrange — 400 characters, ~1ms per region tick = ~400ms of work per full pass
        var (manager, _) = CreateLoadedManager(SlowCharacterCount);
        var regionTick = GetRegionTickMethod();

        // Act
        var sw = Stopwatch.StartNew();
        regionTick.Invoke(manager, [TimeSpan.FromSeconds(1)]);
        sw.Stop();

        // Assert — a single pass must stay near the 100ms budget, never 4x over
        await Assert.That(sw.ElapsedMilliseconds <= BudgetMs * 3)
            .IsTrue()
            .Because($"one ActiveRegionTick pass must respect the {BudgetMs}ms budget under load; actual {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task ActiveRegionTick_OverBudgetLoad_DefersRemainderToNextPass()
    {
        // Arrange
        var (manager, characters) = CreateLoadedManager(SlowCharacterCount);
        var regionTick = GetRegionTickMethod();

        // Act — a single pass under over-budget load
        regionTick.Invoke(manager, [TimeSpan.FromSeconds(1)]);

        // Assert — not everything may be ticked in one pass; the remainder is deferred
        var tickedAfterFirstPass = characters.Values.OfType<SlowCharacter>().Count(c => c.TickCount > 0);
        await Assert.That(tickedAfterFirstPass < SlowCharacterCount)
            .IsTrue()
            .Because($"an over-budget pass must defer the remainder; ticked {tickedAfterFirstPass}/{SlowCharacterCount} in pass 1");
    }

    [Test]
    public async Task ActiveRegionTick_OverBudgetLoad_CompletesAcrossPasses()
    {
        // Arrange
        var (manager, characters) = CreateLoadedManager(SlowCharacterCount);
        var regionTick = GetRegionTickMethod();

        // Act — keep passing until every character has been ticked at least once
        var passes = 0;
        while (characters.Values.OfType<SlowCharacter>().Any(c => c.TickCount == 0) && passes < 50)
        {
            regionTick.Invoke(manager, [TimeSpan.FromSeconds(1)]);
            passes++;
        }

        // Assert — deferred work completes across passes; no character is starved forever
        await Assert.That(passes < 50)
            .IsTrue()
            .Because($"all {SlowCharacterCount} characters must be ticked within a bounded number of passes; stopped at pass {passes}");
        await Assert.That(characters.Values.OfType<SlowCharacter>().All(c => c.TickCount > 0)).IsTrue();
    }

    [Test]
    public async Task WorldManager_Initialize_SubscribesActiveRegionTickWithAsyncDispatch()
    {
        // Arrange — a real TickManager so the subscription wiring is exercised
        var tickManager = new TickManager();
        var (manager, characters) = CreateLoadedManager(SlowCharacterCount, tickManager);

        // Act
        manager.Initialize();

        // The world has 400 slow characters; Invoke must dispatch async and return fast
        var sw = Stopwatch.StartNew();
        tickManager.OnTick.Invoke();
        sw.Stop();

        await Assert.That(sw.ElapsedMilliseconds < BudgetMs * 3)
            .IsTrue()
            .Because($"the ActiveRegionTick subscription must dispatch async so Invoke stays within budget; actual {sw.ElapsedMilliseconds}ms");

        // The async pass must actually run and start ticking characters
        await Task.Delay(400);
        await Assert.That(characters.Values.OfType<SlowCharacter>().Any(c => c.TickCount > 0))
            .IsTrue()
            .Because("the async-dispatched pass must execute and tick at least one character");
    }

    #region Helpers

    private static (WorldManager Manager, ConcurrentDictionary<uint, Character> Characters) CreateLoadedManager(int characterCount, ITickManager tickManager = null)
    {
        var manager = new WorldManager(
            tickManager ?? Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        var characters = new ConcurrentDictionary<uint, Character>();
        for (uint i = 1; i <= characterCount; i++)
            characters[i] = new SlowCharacter();
        SetPrivateField(manager, "_characters", characters);
        SetPrivateField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>());
        return (manager, characters);
    }

    private static MethodInfo GetRegionTickMethod()
    {
        return typeof(WorldManager).GetMethod("ActiveRegionTick", BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (field == null)
            throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    #endregion
}
