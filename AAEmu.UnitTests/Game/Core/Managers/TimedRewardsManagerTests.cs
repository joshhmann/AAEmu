using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Labor regeneration: Unchained defaults (uniform premium-grade), the
/// VanillaRetail tier table, cap clamping / UnlimitedCap, offline tick math,
/// Initialize idempotency and the startup wiring that schedules it.
///
/// Retail-source citation for the VanillaRetail values:
/// scorecard-explorations/generated/formula-corroboration-2026-08-25.md L1–L4
/// (archeage.fandom.com/wiki/Labor_Points).
/// </summary>
[NotInParallel] // swaps AppConfiguration.Instance.Labor — process-wide static default
public class TimedRewardsManagerTests
{
    private LaborConfig _previousLabor;
    private WorldConfig _previousWorld;

    [Before(Test)]
    public void SetUp()
    {
        _previousLabor = AppConfiguration.Instance.Labor;
        _previousWorld = AppConfiguration.Instance.World;
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.Labor = _previousLabor;
        AppConfiguration.Instance.World = _previousWorld;
    }

    // ------------------------------------------------------------- Unchained

    [Test]
    public async Task Unchained_Defaults_AreUniformPremiumGrade()
    {
        var labor = new LaborConfig();

        await Assert.That(labor.Mode).IsEqualTo(LaborRegenMode.Unchained);
        await Assert.That(labor.TickMinutes).IsEqualTo(5);

        // One rate for every account, online AND offline.
        await Assert.That(labor.GetOnlineTickAmount(isPremium: false)).IsEqualTo(10);
        await Assert.That(labor.GetOnlineTickAmount(isPremium: true)).IsEqualTo(10);
        await Assert.That(labor.GetOfflineTickAmount(isPremium: false)).IsEqualTo(10);
        await Assert.That(labor.GetOfflineTickAmount(isPremium: true)).IsEqualTo(10);

        // One cap for every account.
        await Assert.That(labor.GetCap(isPremium: false)).IsEqualTo(5000);
        await Assert.That(labor.GetCap(isPremium: true)).IsEqualTo(5000);
    }

    [Test]
    public async Task Unchained_ModeFlags_DefaultOff()
    {
        var labor = new LaborConfig();

        await Assert.That(labor.UnlimitedCap).IsFalse();
        await Assert.That(labor.DisableConsumption).IsFalse();
    }

    // --------------------------------------------------------- VanillaRetail

    [Test]
    public async Task VanillaRetail_ReproducesRetailTierTable()
    {
        var labor = new LaborConfig { Mode = LaborRegenMode.VanillaRetail };

        // Online: free 5 per 5 min, patron 10 per 5 min.
        await Assert.That(labor.GetOnlineTickAmount(isPremium: false)).IsEqualTo(5);
        await Assert.That(labor.GetOnlineTickAmount(isPremium: true)).IsEqualTo(10);

        // Offline: patron-only at the same rate; free earns nothing.
        await Assert.That(labor.GetOfflineTickAmount(isPremium: false)).IsEqualTo(0);
        await Assert.That(labor.GetOfflineTickAmount(isPremium: true)).IsEqualTo(10);

        // Caps: free 2000, patron 5000.
        await Assert.That(labor.GetCap(isPremium: false)).IsEqualTo(2000);
        await Assert.That(labor.GetCap(isPremium: true)).IsEqualTo(5000);
    }

    [Test]
    public async Task GetMaxLabor_RoutesThroughConfiguredCaps()
    {
        AppConfiguration.Instance.Labor = new LaborConfig(); // Unchained default
        await Assert.That(TimedRewardsManager.GetMaxLabor(isPremium: false)).IsEqualTo((short)5000);
        await Assert.That(TimedRewardsManager.GetMaxLabor(isPremium: true)).IsEqualTo((short)5000);

        AppConfiguration.Instance.Labor = new LaborConfig { Mode = LaborRegenMode.VanillaRetail };
        await Assert.That(TimedRewardsManager.GetMaxLabor(isPremium: false)).IsEqualTo((short)2000);
        await Assert.That(TimedRewardsManager.GetMaxLabor(isPremium: true)).IsEqualTo((short)5000);
    }

    // ----------------------------------------------------------------- caps

    [Test]
    public async Task ComputeGrant_ClampsAtTierCaps_AdditionsOnly()
    {
        var labor = new LaborConfig { Mode = LaborRegenMode.VanillaRetail };

        // Under cap → full grant (free 5/tick at 100).
        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: false, 100, 5)).IsEqualTo(5);
        // Free near 2000 cap → partial grant.
        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: false, 1997, 5)).IsEqualTo(3);
        // Patron near 5000 cap → partial grant.
        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: true, 4998, 5)).IsEqualTo(2);
        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: false, 1997, 5)).IsEqualTo(3);
        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: false, 2100, 5)).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeGrant_UnlimitedCap_BypassesClamping()
    {
        var labor = new LaborConfig { Mode = LaborRegenMode.VanillaRetail, UnlimitedCap = true };

        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: false, 1999, 100)).IsEqualTo(100);
        await Assert.That(TimedRewardsManager.ComputeGrant(labor, isPremium: true, 4999, 100)).IsEqualTo(100);
    }

    // -------------------------------------------------------------- offline

    [Test]
    public async Task OfflineTicks_FloorToCadence_Unchanged()
    {
        // floor-to-tick model preserved: 47 min at a 5-min cadence = 9 ticks.
        await Assert.That(TimedRewardsManager.ComputeOfflineTicks(TimeSpan.FromMinutes(47), 5)).IsEqualTo(9);
        await Assert.That(TimedRewardsManager.ComputeOfflineTicks(TimeSpan.FromMinutes(45), 5)).IsEqualTo(9);
        await Assert.That(TimedRewardsManager.ComputeOfflineTicks(TimeSpan.FromMinutes(4), 5)).IsEqualTo(0);
    }

    [Test]
    public async Task OfflineMath_NowReachableWithNonzeroDefaults()
    {
        // Unchained defaults: everyone accrues 10 × 9 = 90 over a 47-min absence.
        var unchained = new LaborConfig();
        var ticks = TimedRewardsManager.ComputeOfflineTicks(TimeSpan.FromMinutes(47), unchained.TickMinutes);
        await Assert.That(unchained.GetOfflineTickAmount(false) * ticks).IsEqualTo(90);
        await Assert.That(unchained.GetOfflineTickAmount(true) * ticks).IsEqualTo(90);

        // VanillaRetail: patron 90, free 0.
        var vanilla = new LaborConfig { Mode = LaborRegenMode.VanillaRetail };
        await Assert.That(vanilla.GetOfflineTickAmount(true) * ticks).IsEqualTo(90);
        await Assert.That(vanilla.GetOfflineTickAmount(false) * ticks).IsEqualTo(0);
    }

    // ------------------------------------------------------- initialization

    [Test]
    public void Initialize_IsIdempotent_DoubleStartSchedulesOnce()
    {
        var taskManager = Mock.Of<ITaskManager>();
        var manager = new TimedRewardsManager(taskManager.Object);

        manager.Initialize();
        manager.Initialize();

        taskManager.Schedule(
            Any<AAEmu.Game.Models.Tasks.Task>(),
            Any<TimeSpan?>(),
            Any<TimeSpan?>(),
            Any<int>()).WasCalled(Times.Once);
    }

    [Test]
    public async Task Startup_Orchestrator_IncludesTimedRewardsManagerInInitializeStage()
    {
        // GameService Stage 4 runs ManagerOrchestrator.RunInitializeAsync() over
        // every IInitializable manager. ITimedRewardsManager : IInitializable,
        // so the regen tick must be part of the orchestrated boot batches.
        var services = new ServiceCollection();
        var taskManager = Mock.Of<ITaskManager>();
        services.AddSingleton<ITaskManager>(_ => taskManager.Object);
        services.AddSingleton<TimedRewardsManager>();

        var provider = services.BuildServiceProvider();
        var orchestrator = new ManagerOrchestrator(provider, services);

        var participating = orchestrator.BuildBatches<IInitializable>()
            .SelectMany(b => b)
            .ToList();

        if (participating.All(m => m.GetType() != typeof(TimedRewardsManager)))
            throw new Exception(
                $"participating=[{string.Join(", ", participating.Select(m => m.GetType().Name))}] " +
                $"directCheck={typeof(IInitializable).IsAssignableFrom(typeof(TimedRewardsManager))}");

        await Assert.That(true).IsTrue();
    }
}

/// <summary>
/// DisableConsumption at the Character.ChangeLabor consume seam:
/// negative changes become no-ops; positive grants pass through untouched.
/// </summary>
[NotInParallel] // mutates AppConfiguration.Instance static default + ExperienceManager singleton
public class ChangeLaborConsumptionTests
{
    private LaborConfig _previousLabor;
    private WorldConfig _previousWorld;
    private object _previousExperienceManager;
    private object _previousAccountManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousLabor = AppConfiguration.Instance.Labor;
        _previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World ??= new WorldConfig(); // AddExp reads World.ExpRate

        // AddExp → GetLevelFromExp binary-searches the exp table (QuestScenarioDriver rig).
        var instanceField = typeof(Singleton<ExperienceManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousExperienceManager = instanceField?.GetValue(null);
        var experienceManager = new ExperienceManager();
        var mockLoader = Mock.Of<IExperienceLevelTemplateLoader>();
        mockLoader.Load().Returns(
        [
            new ExperienceLevelTemplate { Level = 1, TotalExp = 0, TotalMateExp = 0, SkillPoints = 1 },
            new ExperienceLevelTemplate { Level = 2, TotalExp = 1_000_000, TotalMateExp = 0, SkillPoints = 2 },
            new ExperienceLevelTemplate { Level = 3, TotalExp = 2_000_000, TotalMateExp = 0, SkillPoints = 3 },
            new ExperienceLevelTemplate { Level = 4, TotalExp = 3_000_000, TotalMateExp = 0, SkillPoints = 4 },
            new ExperienceLevelTemplate { Level = 5, TotalExp = 4_000_000, TotalMateExp = 0, SkillPoints = 5 }
        ]);
        experienceManager.Load(mockLoader.Object, 5, 5);
        instanceField?.SetValue(null, experienceManager);

        FormulaManager.Instance.Load(); // idempotent; real ExpByLaborPower formula from canonical data

        // The Character ctor resolves AccountManager.Instance headless — seed a
        // mock-backed instance (same reflection rig as ExperienceManager above).
        var accountField = typeof(Singleton<AccountManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousAccountManager = accountField?.GetValue(null);
        if (_previousAccountManager == null)
            accountField?.SetValue(null, new AccountManager(Mock.Of<ITickManager>().Object, Mock.Of<ITimedRewardsManager>().Object));
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.Labor = _previousLabor;
        AppConfiguration.Instance.World = _previousWorld;
        var instanceField = typeof(Singleton<ExperienceManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        instanceField?.SetValue(null, _previousExperienceManager);
        var accountField = typeof(Singleton<AccountManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        accountField?.SetValue(null, _previousAccountManager);
    }

    private static Character CreateCharacter()
    {
        var character = new Character(new UnitCustomModelParams()) { Id = 1, Name = "labor-seam", Level = 10 };
        character.Abilities = new CharacterAbilities(character); // AddActiveExp needs the ability slots
        return character;
    }

    [Test]
    public async Task DisableConsumption_SpendBecomesNoOp()
    {
        AppConfiguration.Instance.Labor = new LaborConfig { DisableConsumption = true };
        var character = CreateCharacter();
        character.LaborPower = 1000;

        character.ChangeLabor(-5, 0); // would normally drain + roll the XP formula

        await Assert.That(character.LaborPower).IsEqualTo((short)1000);
    }

    [Test]
    public async Task ConsumptionEnabled_DeductsExactlyTheCost()
    {
        AppConfiguration.Instance.Labor = new LaborConfig(); // vanilla byte-equal behavior when flags unset
        var character = CreateCharacter();
        character.LaborPower = 1000;

        character.ChangeLabor(-5, 0);

        await Assert.That(character.LaborPower).IsEqualTo((short)995);
    }

    [Test]
    public async Task DisableConsumption_GrantsStillPassThrough()
    {
        AppConfiguration.Instance.Labor = new LaborConfig { DisableConsumption = true };
        var character = CreateCharacter();
        character.LaborPower = 1000;

        character.ChangeLabor(+25, 0); // e.g. QuestActSupplyLp / GM addlabor

        await Assert.That(character.LaborPower).IsEqualTo((short)1025);
    }
}
