using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// CharacterAbilities defensive behavior: the ctor seeds Fight(1)..Love(10) only
/// (CharacterAbilities.cs:17-22). AbilityType.General == 0 (Ability.cs:5) is never
/// seeded, and ability1..3 come from the client create packet / DB with no
/// server-side validation — so exp-granting paths must skip unseeded abilities
/// instead of throwing KeyNotFoundException (census REWARD:Fail 250/6578/6600/6615
/// via QuestActSupplyExp -> Character.AddExp -> AddActiveExp).
/// </summary>
[NotInParallel] // touches the shared ExperienceManager singleton — same convention as QuestActCheckSphereTests
public class CharacterAbilitiesTests
{
    private object _previousExperienceManager;

    [Before(Test)]
    public void SetUp()
    {
        // AddActiveExp reads ExperienceManager.Instance.MaxPlayerLevel — seed the
        // singleton with a loaded manager (same reflection rig as QuestScenarioDriver).
        var instanceField = typeof(Singleton<ExperienceManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousExperienceManager = instanceField?.GetValue(null);
        var experienceManager = new ExperienceManager();
        var mockLoader = Mock.Of<IExperienceLevelTemplateLoader>();
        mockLoader.Load().Returns([
            new ExperienceLevelTemplate { Level = 1, TotalExp = 0, TotalMateExp = 0, SkillPoints = 1 },
            new ExperienceLevelTemplate { Level = 2, TotalExp = 100, TotalMateExp = 50, SkillPoints = 2 },
            new ExperienceLevelTemplate { Level = 3, TotalExp = 200, TotalMateExp = 100, SkillPoints = 3 },
            new ExperienceLevelTemplate { Level = 4, TotalExp = 400, TotalMateExp = 200, SkillPoints = 4 },
            new ExperienceLevelTemplate { Level = 5, TotalExp = 800, TotalMateExp = 400, SkillPoints = 5 }
        ]);
        experienceManager.Load(mockLoader.Object, 5, 5);
        instanceField?.SetValue(null, experienceManager);
    }

    [After(Test)]
    public void TearDown()
    {
        var instanceField = typeof(Singleton<ExperienceManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        instanceField?.SetValue(null, _previousExperienceManager);
    }

    private static CharacterAbilities CreateAbilities(AbilityType ability1, AbilityType ability2 = AbilityType.None, AbilityType ability3 = AbilityType.None)
    {
        var character = new CharacterMock
        {
            Ability1 = ability1,
            Ability2 = ability2,
            Ability3 = ability3
        };
        character.Abilities = new CharacterAbilities(character);
        return character.Abilities;
    }

    [Test]
    public async Task AddActiveExp_Ability1General_NoThrow()
    {
        // Arrange — General(0) is never seeded by the ctor; a client sending
        // ability1 = 0 (or the harness default) hits this path via quest exp rewards.
        var abilities = CreateAbilities(AbilityType.General);

        // Act — fail-before: Abilities[Owner.Ability1] -> KeyNotFoundException 'General'
        abilities.AddActiveExp(100);

        // Assert — General stayed unseeded (exp skipped), no throw
        await Assert.That(abilities.Abilities.ContainsKey(AbilityType.General)).IsFalse();
    }

    [Test]
    public async Task AddActiveExp_Ability2General_NoThrow()
    {
        var abilities = CreateAbilities(AbilityType.Fight, AbilityType.General);

        abilities.AddActiveExp(100);

        await Assert.That(abilities.Abilities.ContainsKey(AbilityType.General)).IsFalse();
    }

    [Test]
    public async Task AddActiveExp_Ability3General_NoThrow()
    {
        var abilities = CreateAbilities(AbilityType.Fight, AbilityType.Illusion, AbilityType.General);

        abilities.AddActiveExp(100);

        await Assert.That(abilities.Abilities.ContainsKey(AbilityType.General)).IsFalse();
    }

    [Test]
    public async Task AddActiveExp_AllSlotsNone_NoThrow()
    {
        var abilities = CreateAbilities(AbilityType.None);

        abilities.AddActiveExp(100);

        await Assert.That(abilities.Abilities[AbilityType.Fight].Exp).IsEqualTo(0);
    }

    [Test]
    public async Task AddActiveExp_SeededAbility_GrantsExp()
    {
        // Control — the guard must not swallow exp for real (seeded) abilities
        var abilities = CreateAbilities(AbilityType.Fight);

        abilities.AddActiveExp(100);

        await Assert.That(abilities.Abilities[AbilityType.Fight].Exp).IsEqualTo(100);
    }

    [Test]
    public async Task AddActiveExp_SeededAbility_CapsAtMaxLevelExp()
    {
        var abilities = CreateAbilities(AbilityType.Fight);

        // MaxPlayerLevel = 5 -> maxLevelExp = 800 (see SetUp)
        abilities.AddActiveExp(1000);

        await Assert.That(abilities.Abilities[AbilityType.Fight].Exp).IsEqualTo(800);
    }
}
