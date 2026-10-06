
using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;


namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// Bot provisioning must hand a bot a Level/Experience pair that agrees from
/// the moment the Character object exists.
///
/// Defect this pins: both bot creation sites in <see cref="HeadlessSession"/>
/// set <c>Level</c> through an object initializer but never <c>Experience</c>,
/// whose setter is private — so it silently stayed at the CLR default 0.
/// <c>AddExp</c> then resolves the level with
/// <c>GetLevelFromExp(newExperience, Level /* minLevel */)</c>, whose binary
/// search starts at the NEXT level's cumulative total (level 5 starts the
/// window at level 6's 10,000 XP), so no grant below 10,000 could ever move
/// the bot off its starting level even after a full unattended run banked
/// thousands of XP. Experience is now seeded from the level table at
/// provisioning time, which is what makes an ordinary quest reward cumulative
/// toward the next level.
///
/// Hermetic: the canonical XP curve is installed as a swapped-in
/// ExperienceManager, no MySQL, no world server, no wall-clock.
/// </summary>
[NotInParallel]
public class BotProvisioningExperienceTests
{
    /// <summary>
    /// Canonical player total_exp by level 1..12 (compact.sqlite3 `levels`:
    /// L1=0, L5=6000, L6=10000, L7=15400) — the same curve the shipped
    /// SqliteExperienceLevelTemplateLoader reads at boot. Level 12 is the last
    /// row so the out-of-range degenerate case can name a level past the table.
    /// </summary>
    private static readonly int[] CanonicalTotalExpByLevel =
    [
        0,      // Level 1
        400,    // Level 2
        1400,   // Level 3
        3200,   // Level 4
        6000,   // Level 5
        10000,  // Level 6
        15400,  // Level 7
        22400,  // Level 8
        31200,  // Level 9
        42000,  // Level 10
        55000,  // Level 11
        70400   // Level 12
    ];

    /// <summary>Highest level this rig's curve can answer (level 12).</summary>
    private const byte LastCurveLevel = 12;

    private sealed class SingletonSwap : IDisposable
    {
        private readonly Type _singletonBase;
        private readonly object? _previous;

        private SingletonSwap(Type singletonBase)
        {
            _singletonBase = singletonBase;
            _previous = GetSingletonInstance(singletonBase);
        }

        public static SingletonSwap Install(Type singletonBase, object replacement)
        {
            var swap = new SingletonSwap(singletonBase);
            SetSingleton(singletonBase, replacement);
            return swap;
        }

        public void Dispose() => SetSingleton(_singletonBase, _previous);
    }

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    private static void SetSingleton(Type singletonBase, object? instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }

        var prop = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        prop?.SetValue(target, value);
    }

    /// <summary>
    /// Swaps in the canonical player exp curve. Unlike the level×100M rigs, this
    /// curve is the real one — a bug that only shows up against real thresholds
    /// (a 10,000-XP level-6 gate behind a 6,000-XP level-5 start) would be
    /// invisible behind a fixture curve that no reward can cross.
    /// </summary>
    private static SingletonSwap InstallCanonicalExpCurve()
    {
        var experienceManager = new ExperienceManager();
        var expTemplates = new List<ExperienceLevelTemplate>();
        var expByLevel = new List<int>(CanonicalTotalExpByLevel.Length);
        var mateExpByLevel = new List<int>(CanonicalTotalExpByLevel.Length);

        for (var i = 0; i < CanonicalTotalExpByLevel.Length; i++)
        {
            var level = (byte)(i + 1);
            expTemplates.Add(new ExperienceLevelTemplate
            {
                Level = level,
                TotalExp = CanonicalTotalExpByLevel[i],
                TotalMateExp = CanonicalTotalExpByLevel[i],
                SkillPoints = 1
            });
            expByLevel.Add(CanonicalTotalExpByLevel[i]);
            mateExpByLevel.Add(CanonicalTotalExpByLevel[i]);
        }

        SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
        SetField(experienceManager, "_expByLevel", expByLevel);
        SetField(experienceManager, "_mateExpByLevel", mateExpByLevel);
        SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)CanonicalTotalExpByLevel.Length);
        SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)CanonicalTotalExpByLevel.Length);

        return SingletonSwap.Install(typeof(Singleton<ExperienceManager>), experienceManager);
    }

    /// <summary>
    /// The singletons HeadlessSession.Create resolves through the ordinary
    /// Character construction path (Inventory ctor → ContainerIdManager +
    /// ItemManager.GetItemContainerForCharacter). Missing-only seeding: an
    /// established singleton is never replaced, so run order cannot be
    /// inverted by this rig.
    /// </summary>
    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        // Inventory.OnAcquiredItem fires QuestManager.DoItemsAcquiredEvents —
        // a data-free manager makes the hook a no-op.
        SetSingletonIfMissing(typeof(Singleton<QuestManager>),
            new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        // NO forceReset: Initialize(true) rewinds the container-ID sequence and
        // later tests would share bags. First-init-wins keeps ids unique.
        ContainerIdManager.Instance.Initialize(false);
        // The production builder resolves FactionManager.Instance.GetFaction
        // from its object initializer, and ends with character.Hp = MaxHp /
        // Mp = MaxMp — both read FormulaManager, which returns a null formula
        // until loaded (MaxHp then NREs). Both managers read the SHIPPED
        // compact.sqlite3: no MySQL, no server.
        SetSingletonIfMissing(typeof(Singleton<FactionManager>), new FactionManager(Mock.Of<ILocalizationManager>().Object));
        FactionManager.Instance.Load();
        FormulaManager.Instance.Load();
        // AddExp multiplies the delta by World.ExpRate (default 1.0 makes the XP
        // arithmetic below the literal quest values); the production builder
        // also reads AppConfiguration.Instance.Account.AccessLevelDefault,
        // which is null in a headless process with no config load.
        AppConfiguration.Instance.World ??= new WorldConfig();
        AppConfiguration.Instance.Account ??= new AccountConfig();
        // The production builder equips the template body parts, which calls
        // ItemManager.Create over the canonical race body-part template ids.
        SeedItemTemplates(ItemManager.Instance);
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return;
        field.SetValue(null, instance);
    }

    private static ItemManager BuildFixtureItemManager()
    {
        var itemIdManager = Mock.Of<IItemIdManager>();
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            itemIdManager.Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        SetFieldIfNull(itemManager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
        SetFieldIfNull(itemManager, "_removedItems", new List<ulong>());
        SetFieldIfNull(itemManager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        // Created by Load only; GetTemplate reads it unguarded.
        SetFieldIfNull(itemManager, "_templates", new Dictionary<uint, ItemTemplate>());

        uint nextItemId = 9_500_001;
        itemIdManager.GetNextId().Returns(() => nextItemId++);

        return itemManager;
    }

    /// <summary>
    /// Adds bare ItemTemplate rows for whatever ids the production builder's
    /// body-part equip asks for. Additive only — an id a sibling rig already
    /// seeded keeps its richer template. Returns the ids this call added so the
    /// caller can scope the seeding.
    /// </summary>
    private static void SeedItemTemplates(ItemManager itemManager, params uint[] templateIds)
    {
        var templates = typeof(ItemManager)
            .GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(itemManager) as Dictionary<uint, ItemTemplate>;
        if (templates == null)
            return;

        foreach (var id in templateIds)
        {
            if (!templates.ContainsKey(id))
                templates[id] = new ItemTemplate { Id = id, MaxCount = 1 };
        }
    }

    private static void SetFieldIfNull(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(instance) == null)
            field?.SetValue(instance, value);
    }

    // ================================================================ 1. seeded == table

    [Test]
    [Arguments((byte)1)]
    [Arguments((byte)5)]
    [Arguments((byte)6)]
    public async Task FixtureCreate_SeedsExperience_ToTheLevelTablesCumulativeXp(byte level)
    {
        // The regression itself: a bot born at <level> must carry the table's
        // total for that level, NOT the CLR default 0.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var session = HeadlessSession.Create(4_300_001u, $"ExpSeed{level}", level);
        var character = session.Character;

        await Assert.That(character.Level).IsEqualTo(level);
        await Assert.That(character.Experience)
            .IsEqualTo(ExperienceManager.Instance.GetExpForLevel(level));
    }

    [Test]
    [Arguments((byte)1)]
    [Arguments((byte)5)]
    [Arguments((byte)6)]
    public async Task ProductionBuild_SeedsExperience_ToTheLevelTablesCumulativeXp(byte level)
    {
        // Same contract on the PRODUCTION builder (the path a real managed bot
        // account goes through), not just the DB-free fixture.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var character = InvokeBuildProvisionedCharacter(level);

        await Assert.That(character.Level).IsEqualTo(level);
        await Assert.That(character.Experience)
            .IsEqualTo(ExperienceManager.Instance.GetExpForLevel(level));
    }

    // ================================================================ 2. consistency

    [Test]
    [Arguments((byte)1)]
    [Arguments((byte)5)]
    [Arguments((byte)6)]
    public async Task SeededBot_IsSelfConsistent_LevelFromExperienceRoundTrips(byte level)
    {
        // Not just two fields that happen to be equal: the engine's own level
        // lookup, fed the seeded pair, must land back on the same level. This is
        // what AddExp consults on every grant.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var character = HeadlessSession.Create(4_300_002u, $"ExpRoundTrip{level}", level).Character;

        var resolved = ExperienceManager.Instance.GetLevelFromExp(character.Experience, character.Level, out _);
        await Assert.That(resolved).IsEqualTo(level);
    }

    // ================================================================ 3. a bot CAN level

    [Test]
    [Arguments(420)]   // level-1 quest reward
    [Arguments(620)]   // level-4 quest reward
    [Arguments(800)]   // level-7 quest reward
    public async Task SeededLevel5Bot_LevelsTo6_OnceQuestXpCrossesTenThousand(int questExp)
    {
        // The whole point: from level 5 a bot now accumulates toward level 6's
        // 10,000 threshold. Pre-fix, Experience was 0 and every one of these
        // grants left the bot at level 5 forever.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var character = HeadlessSession.Create(4_300_003u, "ExpLadder", 5).Character;
        var expAtLevel5 = character.Experience;
        var grants = (10_000 - expAtLevel5 + questExp - 1) / questExp; // ceil

        await Assert.That(expAtLevel5).IsEqualTo(6_000);
        for (var i = 0; i < grants; i++)
        {
            character.AddExp(questExp, shouldAddAbilityExp: false);
            if (character.Level == 6)
                break;
        }

        await Assert.That(character.Level).IsEqualTo((byte)6);
        await Assert.That(character.Experience).IsGreaterThanOrEqualTo(10_000);
    }

    [Test]
    public async Task SeededLevel5Bot_AQuestRewardAloneIsNotEnough_ItAccumulates()
    {
        // Pins the mechanism rather than the headline: one canonical reward from
        // the seeded level-5 bot stays level 5 and banks the XP toward 10,000 —
        // the pre-fix state (Experience 0) banked the XP but could never use it.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var character = HeadlessSession.Create(4_300_004u, "ExpAccumulates", 5).Character;

        character.AddExp(800, shouldAddAbilityExp: false);

        await Assert.That(character.Level).IsEqualTo((byte)5);
        await Assert.That(character.Experience).IsEqualTo(6_800);
    }

    [Test]
    public async Task SeededLevel5Bot_TenGrantOfEightHundred_CrossesToLevel6()
    {
        // Ten level-7-sized rewards = 8,000 on top of the seeded 6,000 = 14,000,
        // i.e. past level 6's 10,000 but short of level 7's 15,400: the bot lands
        // on 6, having crossed exactly one threshold.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var character = HeadlessSession.Create(4_300_005u, "ExpLadder10", 5).Character;

        for (var i = 0; i < 10; i++)
            character.AddExp(800, shouldAddAbilityExp: false);

        await Assert.That(character.Level).IsEqualTo((byte)6);
        await Assert.That(character.Experience).IsEqualTo(14_000);
    }

    // ================================================================ 4. load path untouched

    [Test]
    [Arguments((byte)5, 0)]
    [Arguments((byte)5, 5_999)]
    [Arguments((byte)7, 15_399)]
    public async Task LoadPathRepair_StillRaisesExperience_UnderItsLevel(byte level, int storedExp)
    {
        // ValidateAndFixExpAndLevel is the DB-load path's own repair and is
        // unchanged by this fix: a row whose stored exp is below its level's
        // total is RAISED to that total. Existing saved rows depend on it, so
        // pin it — an already-provisioned bot row is repaired by THIS, not by
        // the provisioning seed.
        using var expSwap = InstallCanonicalExpCurve();

        var character = new Character(new UnitCustomModelParams()) { Id = 4_300_006u, Name = "LoadedRow" };
        character.InitializeLevelAndExperience(level);
        // Simulate a row written by a build that never seeded exp: level kept,
        // exp below the level's total.
        SetField(character, "_experience", storedExp);

        InvokeValidateAndFixExpAndLevel(character);

        await Assert.That(character.Level).IsEqualTo(level);
        await Assert.That(character.Experience)
            .IsEqualTo(ExperienceManager.Instance.GetExpForLevel(level));
    }

    // ================================================================ 5. degenerate

    [Test]
    [Arguments((byte)0)]        // no level row at all
    [Arguments((byte)12)]       // last curve row — a real level, seeded normally
    [Arguments((byte)13)]       // one past the last curve row
    [Arguments((byte)99)]
    [Arguments(byte.MaxValue)]  // the byte ceiling
    public async Task DegenerateLevel_SeedsWithoutThrowing_AndStaysNonNegative(byte level)
    {
        // Levels the level table cannot answer must not throw and must not leave
        // a nonsensical experience: level 0 has no row → 0, a level inside the
        // table seeds its own cumulative XP, and a level PAST the table falls
        // back to the level-1 floor rather than seeding an unlevel 0.
        using var expSwap = InstallCanonicalExpCurve();
        SeedFixtureSingletons();

        var character = HeadlessSession.Create(4_300_007u, $"ExpDegenerate{level}", level).Character;

        await Assert.That(character.Level).IsEqualTo(level);
        await Assert.That(character.Experience).IsGreaterThanOrEqualTo(0);
        var expected = level == 0 ? 0
            : level > LastCurveLevel
                ? ExperienceManager.Instance.GetExpForLevel(1)
                : ExperienceManager.Instance.GetExpForLevel(level);
        await Assert.That(character.Experience).IsEqualTo(expected);
    }

    // ================================================================ private production builders

    /// <summary>
    /// Calls the production builder (the one <c>Provision</c> uses) through
    /// reflection: it is private, and making it public for a test would widen
    /// the production surface for nothing. The rig supplies the one template
    /// argument it needs.
    /// </summary>
    private static Character InvokeBuildProvisionedCharacter(byte level)
    {
        var method = typeof(HeadlessSession).GetMethod("BuildProvisionedCharacter",
            BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException("HeadlessSession.BuildProvisionedCharacter not found");

        var template = new CharacterTemplate
        {
            Race = Race.Nuian,
            Gender = Gender.Male,
            ModelId = 10,
            FactionId = FactionsEnum.NuiaAlliance,
            NumInventorySlot = 50,
            NumBankSlot = 50,
            SpawnPosition = new WorldSpawnPosition(),
            ReturnDistrictId = 1,
            ResurrectionDistrictId = 1
        };

        // The builder equips BotAppearanceDefaults' Nuian-male body parts, so
        // those templates must exist for ItemManager.Create to hand back an item.
        SeedItemTemplates(ItemManager.Instance, 19_838, 24_133, 536);

        try
        {
            return (Character)method.Invoke(null,
                [4_300_010u, 1u, $"ProdExpSeed{level}", Race.Nuian, Gender.Male, level, template, null])!;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    private static void InvokeValidateAndFixExpAndLevel(Character character)
    {
        var method = typeof(Character).GetMethod("ValidateAndFixExpAndLevel",
            BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("Character.ValidateAndFixExpAndLevel not found");
        try
        {
            method.Invoke(character, null);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }
}