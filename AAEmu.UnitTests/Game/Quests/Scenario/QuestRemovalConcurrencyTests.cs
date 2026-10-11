using System.Threading;
using System.Reflection;
using AAEmu.Game.Core.Managers;

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
    public async Task DropQuest_WhileCleanupRuns_ReacceptRefusedUntilRemovalFinishes()
    {
        var quest = CreateQuest();
        var character = (Character)quest.Owner;
        var templatesField = typeof(QuestManager).GetField("_questTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var templates = (Dictionary<uint, QuestTemplate>)templatesField.GetValue(QuestManager.Instance)!;
        templates[quest.TemplateId] = (QuestTemplate)quest.Template;
        var component = quest.Template.Components.Values.First();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cleanup = new BlockingCleanup(component, entered, release);
        component.ActTemplates.Add(cleanup);
        character.Quests.ActiveQuests.TryAdd(quest.TemplateId, quest);
        var removal = Task.Run(() => character.Quests.DropQuest(quest.TemplateId, false));
        bool accepted;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("removal did not reach cleanup");
            accepted = character.Quests.AddQuest(quest.TemplateId);
        }
        finally
        {
            release.Set();
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            component.ActTemplates.Remove(cleanup);
        }
        await Assert.That(accepted).IsFalse();
        await Assert.That(cleanup.Calls).IsEqualTo(1);
        await Assert.That(character.Quests.HasQuest(quest.TemplateId)).IsFalse();
        await Assert.That(character.Quests.AddQuest(quest.TemplateId)).IsTrue();
        character.Quests.DropQuest(quest.TemplateId, false);
    }

    [Test]
    public async Task DropQuest_CleanupThrows_RemovalClaimReleasedForLaterRetry()
    {
        var quest = CreateQuest();
        var character = (Character)quest.Owner;
        var cleanup = new FailOnceCleanup(quest.Template.Components.Values.First());
        quest.Template.Components.Values.First().ActTemplates.Add(cleanup);
        character.Quests.ActiveQuests.TryAdd(quest.TemplateId, quest);
        var failed = false;
        try
        {
            character.Quests.DropQuest(quest.TemplateId, false);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        await Assert.That(failed).IsTrue();
        await Assert.That(character.Quests.HasQuest(quest.TemplateId)).IsTrue();
        character.Quests.DropQuest(quest.TemplateId, false);
        await Assert.That(character.Quests.HasQuest(quest.TemplateId)).IsFalse();
        await Assert.That(cleanup.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task DropQuest_OldRemovalFinishing_ReplacementCanBeRemovedIndependently()
    {
        var quest = CreateQuest();
        var character = (Character)quest.Owner;
        var templatesField = typeof(QuestManager).GetField("_questTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var templates = (Dictionary<uint, QuestTemplate>)templatesField.GetValue(QuestManager.Instance)!;
        templates[quest.TemplateId] = (QuestTemplate)quest.Template;
        character.Quests.ActiveQuests.TryAdd(quest.TemplateId, quest);
        var removedLock = typeof(CharacterQuests).GetField("_removedLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(character.Quests)!;
        Task? removal = null;
        var accepted = false;
        var replacementStillActive = true;
        Monitor.Enter(removedLock);
        try
        {
            removal = Task.Run(() => character.Quests.DropQuest(quest.TemplateId, false));
            if (!SpinWait.SpinUntil(() => !character.Quests.HasQuest(quest.TemplateId), TimeSpan.FromSeconds(5)))
                throw new TimeoutException("old quest was not removed before bookkeeping");
            accepted = character.Quests.AddQuest(quest.TemplateId);
            character.Quests.DropQuest(quest.TemplateId, false);
            replacementStillActive = character.Quests.HasQuest(quest.TemplateId);
        }
        finally
        {
            Monitor.Exit(removedLock);
            if (removal != null)
                await removal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.That(accepted).IsTrue();
        await Assert.That(replacementStillActive).IsFalse();
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

    private sealed class FailOnceCleanup(QuestComponentTemplate component) : QuestActTemplate(component)
    {
        public int Calls { get; private set; }
        public override void QuestCleanup(Quest quest)
        {
            if (++Calls == 1)
                throw new InvalidOperationException("named cleanup failure");
        }
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
