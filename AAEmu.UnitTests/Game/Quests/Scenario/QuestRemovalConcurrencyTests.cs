using System.Threading;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestRemovalConcurrencyTests
{
    private static Quest CreateQuest()
    {
        QuestScenarioDriver.SeedSingletons();
        return QuestScenarioDriver.BuildQuest(QuestScenarioManifest.Load("""
            {"questId":93001,"name":"quest removal concurrency",
             "acceptor":{"type":"Npc","id":13453},
             "template":{"level":1,"components":[
               {"kind":"Start","id":930011,"acts":[]}]},"stages":[]}
            """));
    }

    [Test]
    public async Task DropQuest_ConcurrentCallWhileCleanupRuns_CleansUpOnlyOnce()
    {
        var quest = CreateQuest();
        var character = (Character)quest.Owner;
        var component = quest.Template.Components.Values.First();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cleanup = new BlockingCleanup(component, entered, release);
        component.ActTemplates.Add(cleanup);
        character.Quests.ActiveQuests.TryAdd(quest.TemplateId, quest);

        var first = Task.Run(() => character.Quests.DropQuest(quest.TemplateId, false));
        Task? second = null;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("first removal did not reach cleanup");
            second = Task.Run(() => character.Quests.DropQuest(quest.TemplateId, false));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.That(cleanup.Calls).IsEqualTo(1);
        await Assert.That(character.Quests.HasQuest(quest.TemplateId)).IsFalse();
    }

    [Test]
    public async Task CompletedFlags_ConcurrentDifferentBitsInSameBlock_AllRetained()
    {
        var character = (Character)CreateQuest().Owner;
        const int iterations = 20000;
        // Pre-existing blocks expose simultaneous read/modify/write on the same word.
        for (var i = 0; i < iterations; i++)
            character.Quests.SetCompletedQuestFlag((uint)(i * 64), false);
        using var barrier = new Barrier(2);
        void Complete(int bit)
        {
            for (var i = 0; i < iterations; i++)
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("completion peer did not arrive");
                character.Quests.SetCompletedQuestFlag((uint)(i * 64 + bit), true);
            }
        }
        await Task.WhenAll(Task.Run(() => Complete(0)), Task.Run(() => Complete(1)));
        var missing = Enumerable.Range(0, iterations).Count(i =>
            !character.Quests.HasQuestCompleted((uint)(i * 64)) ||
            !character.Quests.HasQuestCompleted((uint)(i * 64 + 1)));
        await Assert.That(missing).IsEqualTo(0);
    }

    [Test]
    public async Task CompletedFlag_ReturnedPacketBody_RemainsStableAfterLaterCompletion()
    {
        var character = (Character)CreateQuest().Owner;
        var first = character.Quests.SetCompletedQuestFlag(64, true);
        character.Quests.SetCompletedQuestFlag(65, true);
        await Assert.That(first.Body.Get(0)).IsTrue();
        await Assert.That(first.Body.Get(1)).IsFalse();
        await Assert.That(character.Quests.HasQuestCompleted(65)).IsTrue();
    }

    private sealed class BlockingCleanup(QuestComponentTemplate component,
        ManualResetEventSlim entered, ManualResetEventSlim release) : QuestActTemplate(component)
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public override void QuestCleanup(Quest quest)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("test did not release first cleanup");
            }
        }
    }
}
