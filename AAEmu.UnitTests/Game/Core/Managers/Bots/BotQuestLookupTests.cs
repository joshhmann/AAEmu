using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The quest-giver and prey-spawner searches ask about every NPC template in
/// the world. Populated-area run 6 measured them at up to 400 ms because each
/// template re-scanned the quest data. The all-at-once lookups that replace
/// those scans must give exactly the per-template answers.
/// </summary>
[NotInParallel]
public class BotQuestLookupTests
{
    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
    }

    [Test]
    public async Task OffersByNpc_MatchesThePerNpcLookup_IncludingOrderAndUnknownNpcs()
    {
        const uint giverA = 96_501; const uint giverB = 96_502; const uint nobody = 96_503;
        GameplayActorTestRig.SeedQuestOffer(96_511, 96_521, giverA, level: 1);
        GameplayActorTestRig.SeedQuestOffer(96_512, 96_522, giverA, level: 1);
        GameplayActorTestRig.SeedQuestOffer(96_513, 96_523, giverB, level: 1);

        var table = QuestManager.Instance.GetQuestOffersByNpc();

        await Assert.That(table[giverA]).IsEquivalentTo(new uint[] { 96_511, 96_512 });
        await Assert.That(table.ContainsKey(nobody)).IsFalse();
        await Assert.That(QuestManager.Instance.GetQuestsOfferedByNpc(nobody)).IsEmpty();
        foreach (var (npc, quests) in table)
        {
            var single = QuestManager.Instance.GetQuestsOfferedByNpc(npc);
            await Assert.That(quests.SequenceEqual(single)).IsTrue()
                .Because($"npc {npc}: table [{string.Join(",", quests)}] vs single [{string.Join(",", single)}]");
        }
    }

    [Test]
    public async Task HuntSourceMatcher_MatchesThePerTemplateCheck()
    {
        const uint quest = 96_531; const uint prey = 96_541;
        GameplayActorTestRig.SeedQuestHunt(quest, 96_532, 96_533, 96_534,
            offerNpcTemplateId: 96_542, huntNpcTemplateId: prey, reportNpcTemplateId: 96_543);
        QuestFixtureRow.InvalidateAll();
        var hunt = QuestManager.Instance.GetTemplate(quest)!
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).OfType<QuestActObjMonsterHunt>().Single();
        var fixture = QuestFixtureRow.FromQuestData(quest, hunt);

        var isSource = QuestObjectiveTargetSelector.QuestNpcSourceMatcher(fixture);

        await Assert.That(isSource(prey)).IsTrue();
        foreach (var template in new uint[] { prey, prey + 1, 96_542, 0 })
            await Assert.That(isSource(template))
                .IsEqualTo(QuestObjectiveTargetSelector.IsQuestNpcSource(fixture, template))
                .Because($"template {template}");
    }
}
