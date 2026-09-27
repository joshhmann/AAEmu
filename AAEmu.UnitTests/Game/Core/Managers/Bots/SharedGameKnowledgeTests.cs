using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Shared knowledge reads live QuestManager data: 4438 offer/report
/// linkage, unknown ids fail closed, canonical Tier 0 ids pinned.
/// </summary>
[NotInParallel]
public class SharedGameKnowledgeTests
{
    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
    }

    [Test]
    public async Task QuestOfferers_4438_ContainsNpc9789()
    {
        await Assert.That(SharedGameKnowledge.QuestOfferers(4438)).Contains(9789u);
    }

    [Test]
    public async Task QuestReporters_4438_ContainsNpc9789()
    {
        await Assert.That(SharedGameKnowledge.QuestReporters(4438)).Contains(9789u);
    }

    [Test]
    public async Task QuestStartLevel_4438_IsSeven()
    {
        await Assert.That(SharedGameKnowledge.QuestStartLevel(4438)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task UnknownQuest_FailsClosedEmpty()
    {
        await Assert.That(SharedGameKnowledge.QuestOfferers(0xDEADu)).IsEmpty();
        await Assert.That(SharedGameKnowledge.QuestReporters(0xDEADu)).IsEmpty();
        await Assert.That(SharedGameKnowledge.QuestStartLevel(0xDEADu)).IsEqualTo((byte)0);
        await Assert.That(SharedGameKnowledge.ProgressPrerequisites(0xDEADu)).IsEmpty();
    }

    [Test]
    public async Task Tier0CanonicalIds_Pinned()
    {
        await Assert.That(SharedGameKnowledge.PotatoSeedItemId).IsEqualTo(15659u);
        await Assert.That(SharedGameKnowledge.PotatoItemId).IsEqualTo(7992u);
        await Assert.That(SharedGameKnowledge.ScarecrowDesignQuestId).IsEqualTo(4438u);
        await Assert.That(SharedGameKnowledge.ScarecrowDesignItemId).IsEqualTo(15596u);
    }

    [Test]
    public async Task TreeSaplingIdentity_IsNotThePotatoSeed()
    {
        // The removed alias made 15659 satisfy HasTreeSaplings and get planted as a
        // tree. Sapling identity is its own canonical row (4862, 코르크참나무 묘목,
        // ItemCategory.Saplings), never the potato seed.
        await Assert.That(SharedGameKnowledge.TreeSaplingItemId).IsEqualTo(4862u);
        await Assert.That(SharedGameKnowledge.TreeSaplingItemId)
            .IsNotEqualTo(SharedGameKnowledge.PotatoSeedItemId);
        await Assert.That(SharedGameKnowledge.TreeSaplingUnitPrice).IsEqualTo(400u);
    }

    [Test]
    public async Task IsTreeSapling_UnknownOrUnloadedTemplate_FailsClosed()
    {
        // The predicate reads the engine's live template registry; an id the
        // registry has never seen is not a sapling (and never a seed by default).
        await Assert.That(SharedGameKnowledge.IsTreeSapling(0xDEADu)).IsFalse();
    }
}
