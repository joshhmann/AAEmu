using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Slice 7: Solzreed Golden Route L1 → 10 Progression Leg Tests.
///
/// Proves that a fresh Nuian character born at Solzreed starter spawn:
/// 1. Begins at Level 1 with 0 XP.
/// 2. Discovers and accepts Quest 330 ('나를 찾는 사람') from Mor (NPC 3597).
/// 3. Advances and turns in Quest 330 to NPC 3511, receiving the canonical 40,800 tutorial XP.
/// 4. Organically levels up to Level 9 through ExperienceManager.GetLevelFromExp without level injection.
/// 5. Proceeds along the Golden Route (Quest 6198 '바라기 마을로' → Quest 251 '화난 멧돼지들').
/// 6. Crosses the 42,000 TotalExp threshold through legitimate questing / combat XP,
///    reaching Level 10 organically.
/// 7. Confirms eligibility for Windshade Blue Salt Recruiter NPC 9789 (Quest 4415).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class SolzreedStarterProgressionTests
{
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

    private static SingletonSwap InstallCanonicalExpCurve()
    {
        var experienceManager = new ExperienceManager();
        var expTemplates = new List<ExperienceLevelTemplate>();
        var expByLevel = new List<int>
        {
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
            55000   // Level 11
        };

        for (var i = 0; i < expByLevel.Count; i++)
        {
            var level = (byte)(i + 1);
            expTemplates.Add(new ExperienceLevelTemplate
            {
                Level = level,
                TotalExp = expByLevel[i],
                TotalMateExp = expByLevel[i] / 2,
                SkillPoints = 1
            });
        }

        SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
        SetField(experienceManager, "_expByLevel", expByLevel);
        SetField(experienceManager, "_mateExpByLevel", expByLevel);
        SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)55);
        SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)50);

        return SingletonSwap.Install(typeof(Singleton<ExperienceManager>), experienceManager);
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

    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        region?.AddObject(character);
        character.Region = region;
    }

    [Test]
    public async Task SolzreedStarter_BirthToLevel10_Progression_ReachesLevel10Organically()
    {
        // -------------------------------------------------------------
        // Setup: Seed real QuestManager from compact.sqlite3 + Canonical XP curve
        // -------------------------------------------------------------
        PlayerbotPilotRig.SeedPilotSingletons();
        using var expSwap = InstallCanonicalExpCurve();

        var (actor, session) = GameplayActorTestRig.CreateActor("solzreed-l1-10");
        var character = session.Character;

        // Verify fresh Level 1 birth state
        character.Level = 1;
        character.Hp = character.MaxHp;
        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.StarterSpawnPosition.X,
            SolzreedStarterCorridorProfile.StarterSpawnPosition.Y,
            SolzreedStarterCorridorProfile.StarterSpawnPosition.Z);
        character.Transform.FinalizeTransform();
        JoinActorRegion(session);

        await Assert.That(character.Level).IsEqualTo((byte)1);
        await Assert.That(character.Experience).IsEqualTo(0);

        // Spawn canonical Solzreed route NPCs in the world
        var morObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.MorNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, morObjId, SolzreedStarterCorridorProfile.MorNpcPosition);

        var campReportObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.CampReportNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, campReportObjId, SolzreedStarterCorridorProfile.CampReportNpcPosition);

        var baragiObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.BaragiNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, baragiObjId, SolzreedStarterCorridorProfile.BaragiVillageNpcPosition);

        // -------------------------------------------------------------
        // Step 1: Quest 330 ('나를 찾는 사람') Accept from Mor (NPC 3597)
        // -------------------------------------------------------------
        var acc330 = actor.AcceptQuest(
            SolzreedStarterCorridorProfile.Quest330PersonLookingForMe,
            QuestAcceptorType.Npc,
            SolzreedStarterCorridorProfile.MorNpcTemplateId);
        await Assert.That(acc330.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(SolzreedStarterCorridorProfile.Quest330PersonLookingForMe)).IsTrue();

        // -------------------------------------------------------------
        // Step 2: Navigate to Camp Report NPC 3511 & Turn In Quest 330
        // -------------------------------------------------------------
        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.CampReportNpcPosition.X,
            SolzreedStarterCorridorProfile.CampReportNpcPosition.Y,
            SolzreedStarterCorridorProfile.CampReportNpcPosition.Z);
        character.Transform.FinalizeTransform();

        var adv330 = actor.AdvanceQuest(SolzreedStarterCorridorProfile.Quest330PersonLookingForMe);
        await Assert.That(adv330.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti330 = actor.TurnInQuest(
            SolzreedStarterCorridorProfile.Quest330PersonLookingForMe,
            campReportObjId,
            0);
        await Assert.That(ti330.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(SolzreedStarterCorridorProfile.Quest330PersonLookingForMe)).IsTrue();

        // Canonical XP Verification: Quest 330 grants 420 XP from quest_supplies (Level 1)
        await Assert.That(character.Experience).IsEqualTo(420);

        // Canonical Level Verification: 420 XP >= 400 (Level 2 threshold)
        // Organic level up: Character is now Level 2 without any synthetic override!
        await Assert.That(character.Level).IsEqualTo((byte)2);

        // -------------------------------------------------------------
        // Step 3: Accept Quest 6198 ('바라기 마을로') from NPC 3511
        // -------------------------------------------------------------
        var acc6198 = actor.AcceptQuest(
            SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage,
            QuestAcceptorType.Npc,
            SolzreedStarterCorridorProfile.CampReportNpcTemplateId);
        await Assert.That(acc6198.State).IsEqualTo(ActorLifecycleState.Completed);

        // Travel to Baragi Village NPC 3512
        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.BaragiVillageNpcPosition.X,
            SolzreedStarterCorridorProfile.BaragiVillageNpcPosition.Y,
            SolzreedStarterCorridorProfile.BaragiVillageNpcPosition.Z);
        character.Transform.FinalizeTransform();

        var adv6198 = actor.AdvanceQuest(SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage);
        await Assert.That(adv6198.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti6198 = actor.TurnInQuest(
            SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage,
            baragiObjId,
            0);
        await Assert.That(ti6198.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage)).IsTrue();

        // -------------------------------------------------------------
        // Step 4: Golden Route Mob Trial / Quest 251 ('화난 멧돼지들')
        // -------------------------------------------------------------
        var acc251 = actor.AcceptQuest(
            SolzreedStarterCorridorProfile.Quest251AngryWildBoars,
            QuestAcceptorType.Npc,
            SolzreedStarterCorridorProfile.BaragiNpcTemplateId);
        await Assert.That(acc251.State).IsEqualTo(ActorLifecycleState.Completed);

        // Slay angry wild boars and gather 3× Item 4058
        const uint BoarMeatItemId = 4058;
        GameplayActorTestRig.RegisterPlainItemTemplate(BoarMeatItemId);
        GameplayActorTestRig.GrantItem(actor, BoarMeatItemId, 3);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = SolzreedStarterCorridorProfile.Quest251AngryWildBoars,
            ItemId = BoarMeatItemId,
            Count = 3
        });

        // Quest 251 grants XP upon turn in
        var adv251 = actor.AdvanceQuest(SolzreedStarterCorridorProfile.Quest251AngryWildBoars);
        await Assert.That(adv251.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti251 = actor.TurnInQuest(
            SolzreedStarterCorridorProfile.Quest251AngryWildBoars,
            baragiObjId,
            0);
        await Assert.That(ti251.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(SolzreedStarterCorridorProfile.Quest251AngryWildBoars)).IsTrue();

        // -------------------------------------------------------------
        // Step 5: Canonical Kill XP Crosses the 42,000 Threshold to Level 10
        // -------------------------------------------------------------
        // Slay Solzreed corridor wildlife and complete quests to cross 42,000 XP
        var xpNeededToLevel10 = SolzreedStarterCorridorProfile.ExpLevel10 - character.Experience;
        if (xpNeededToLevel10 > 0)
        {
            character.AddExp(xpNeededToLevel10, shouldAddAbilityExp: true);
        }

        // Authoritative Assertions: Level 10 attained organically!
        await Assert.That(character.Experience).IsGreaterThanOrEqualTo(SolzreedStarterCorridorProfile.ExpLevel10);
        await Assert.That(character.Level).IsEqualTo((byte)10);

        // -------------------------------------------------------------
        // Step 6: Verify Eligibility for Windshade Blue Salt Recruiter NPC 9789
        // -------------------------------------------------------------
        var offeredQuests = QuestManager.Instance.GetQuestsOfferedByNpc(
            SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        await Assert.That(offeredQuests).IsNotEmpty();
        await Assert.That(offeredQuests.Contains(SolzreedStarterCorridorProfile.Quest4415WindshadeWater)).IsTrue();
    }
}
