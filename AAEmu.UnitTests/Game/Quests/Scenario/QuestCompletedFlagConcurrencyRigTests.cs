using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// t_ca4683e1: SetCompletedQuestFlag check-then-act Dictionary race (golden-path
/// blocker found by t_cca63225).
///
/// The completion path is drivable concurrently: the game tick's quest-evaluation
/// queue (~1ms schedule after every event) and any other thread that drives
/// RunCurrentStep (the E2E bridge) can both enter the GoToNextStep Reward case
/// (NewQuestCode.cs:139-151). Both threads then hit
/// CharacterQuests.SetCompletedQuestFlag -> TryGetValue miss -> new CompletedQuest
/// -> Dictionary.Add, and the loser throws
/// `System.ArgumentException: An item with the same key has already been added`
/// (observed live on quest 330, block key 5, game-restart.log:600-609).
///
/// Two rigs pin the fix:
///   1. Flag-level hammer (deterministic fail-before): two barrier-synced threads
///      each complete FRESH blocks (questId = i*64 => block i) so every iteration
///      re-enters the miss window of the SAME key. On the pre-fix code this throws
///      on the first iterations; the fix must complete 2000 iterations with zero
///      exceptions, exactly one block per key, and every bit set.
///   2. Full engine path: build a Start->Progress(empty)->Ready->Reward quest via
///      the scenario machinery, drive it to the Reward rest, then TWO threads call
///      quest.RunCurrentStep() simultaneously (eval-queue + direct drive). No throw,
///      quest completes exactly once (flag set, one completed block, quest dropped,
///      terminal Step=Drop/Status=Dropped per the pilot's completion-path fact).
/// </summary>
[NotInParallel]
[ParallelLimiter<SequentialParallelLimit>]
public class QuestCompletedFlagConcurrencyRigTests
{
    private const int HammerIterations = 2000;

    // Mirrors the REAL quest-330 crash shape (Start -> empty Progress -> Ready report
    // -> Reward). The quest id is @@QID@@ so each drive iteration lands on a
    // fresh completed-block (id/64) - the double-entry race only fires on a block's FIRST add.
    // NOTE: the Reward step is deliberately act-less. This rig pins the CARD's scope -
    // the completion path (GoToNextStep Reward case: SetCompletedQuestFlag + DropQuest).
    // A SupplyItem reward act would ALSO run from both threads (RunComponents before
    // GoToNextStep), racing the RIG's uninitialized item container (NRE in
    // ItemContainer.ApplyBindRules) - that is the item-container concurrency domain
    // (t_3fdd6ac3), not this card's flag/drop path.
    private const string RewardShapeManifestJson = """
    {
      "questId": @@QID@@,
      "name": "two-thread reward drive rig (quest 330 crash mirror)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 1,
        "components": [
          { "kind": "Start", "id": @@C1@@, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": @@C1@@ } ] },
          { "kind": "Progress", "id": @@C2@@, "acts": [] },
          { "kind": "Ready", "id": @@C3@@, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": @@C3@@ } ] },
          { "kind": "Reward", "id": @@C4@@, "acts": [] }
        ]
      }
    }
    """;

    private static string BuildManifestJson(uint questId)
    {
        return RewardShapeManifestJson
            .Replace("@@QID@@", questId.ToString())
            .Replace("@@C1@@", (questId + 1).ToString())
            .Replace("@@C2@@", (questId + 2).ToString())
            .Replace("@@C3@@", (questId + 3).ToString())
            .Replace("@@C4@@", (questId + 4).ToString());
    }

    /// <summary>
    /// Deterministic fail-before: two barrier-locked threads race SetCompletedQuestFlag
    /// on a fresh completed-block every iteration. Pre-fix code throws ArgumentException
    /// (duplicate key) within the first iterations; the fixed code must complete all
    /// iterations with no exception, exactly one block per key and all bits set.
    /// </summary>
    [Test]
    public async Task TwoThreads_CompleteFreshBlocks_NoThrow_ExactlyOneBlockPerKey_AllBitsSet()
    {
        QuestScenarioDriver.SeedSingletons();
        var character = BuildRigCharacter();

        var exceptions = new ConcurrentQueue<Exception>();
        var failed = false;
        using var barrier = new Barrier(2);

        void Work()
        {
            for (var i = 0; i < HammerIterations; i++)
            {
                try
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    return; // barrier broken - bail, the other thread reports
                }

                if (failed)
                    continue; // peer already hit the race - keep the barrier alive, do no work

                try
                {
                    character.Quests.SetCompletedQuestFlag((uint)(i * 64), true);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                    failed = true;
                }
            }
        }

        var thread1 = new Thread(Work);
        var thread2 = new Thread(Work);
        thread1.Start();
        thread2.Start();
        thread1.Join();
        thread2.Join();

        await Assert.That(exceptions, "no thread may throw: the check-then-act block-add race is gone").IsEmpty();

        // Exactly one completed block per key (no lost/duplicated blocks) and the
        // flag observable on the public surface for every completed quest id.
        var blocks = GetCompletedBlocks(character);
        await Assert.That(blocks.Count, "exactly one completed block per quest block id").IsEqualTo(HammerIterations);
        var missing = Enumerable.Range(0, HammerIterations)
            .Where(i => !character.Quests.HasQuestCompleted((uint)(i * 64)))
            .ToList();
        await Assert.That(missing, "every hammered quest id must read as completed").IsEmpty();
    }

    /// <summary>
    /// Full engine path: the quest rests at Reward, then two threads drive
    /// RunCurrentStep simultaneously (the eval-queue + direct-drive shape that
    /// crashed quest 330 live). Must not throw, must complete exactly once:
    /// flag set, one completed block, quest dropped, terminal Drop/Dropped state.
    /// </summary>
    [Test]
    public async Task TwoThreads_DriveRewardStep_NoThrow_CompletedExactlyOnce()
    {
        QuestScenarioDriver.SeedSingletons();

        for (var run = 0; run < 10; run++)
        {
            var questId = 9300u + (uint)(run * 64); // fresh block per drive: 9300/64=145, 9364/64=146, ...
            var manifest = QuestScenarioManifest.Load(BuildManifestJson(questId));
            QuestScenarioDriver.RegisterManifestItems(manifest);

            var quest = QuestScenarioDriver.BuildQuest(manifest);
            var character = (Character)quest.Owner;

            // Accept (mirror the driver's AcceptQuest): StartQuest + ActiveQuests
            // registration + first step evaluation. Rests at Progress.
            if (!quest.StartQuest())
                throw new InvalidOperationException("StartQuest() returned false");
            character.Quests.ActiveQuests.Add(quest.TemplateId, quest);
            quest.RunCurrentStep();

            // Advance through the empty Progress (auto-pass) to Ready.
            quest.RunCurrentStep();

            // Fire the report event (the Ready act's handler), then advance to the
            // Reward rest - Status=Completed, exactly like the live quest-330 crash
            // frame (NewQuestCode.cs:143 via GoToNextStep Reward case).
            character.Events.OnReportNpc(character, new OnReportNpcArgs
            {
                QuestId = quest.TemplateId,
                NpcId = 13453,
                Selected = 0
            });
            quest.RunCurrentStep();

            await Assert.That(quest.Step, "quest must rest at Reward before the double drive").IsEqualTo(QuestComponentKind.Reward);

            // THE double drive: eval queue + direct bridge, both entering the Reward
            // case on the same quest object at the same time.
            var exceptions = new ConcurrentQueue<Exception>();
            using var start = new ManualResetEventSlim(false);
            void Drive()
            {
                try
                {
                    start.Wait();
                    quest.RunCurrentStep();
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }

            var thread1 = new Thread(Drive);
            var thread2 = new Thread(Drive);
            thread1.Start();
            thread2.Start();
            start.Set();
            thread1.Join();
            thread2.Join();

            await Assert.That(exceptions, $"run {run}: double reward drive must not throw").IsEmpty();
            await Assert.That(character.Quests.HasQuestCompleted(questId), $"run {run}: quest must be completed").IsTrue();

            var blocks = GetCompletedBlocks(character);
            await Assert.That(blocks.Count, $"run {run}: exactly one completed block (actual keys: [{string.Join(",", blocks.Keys)}])").IsEqualTo(1);
            await Assert.That(blocks[questId / 64].Body.Get((int)(questId % 64)), $"run {run}: completed bit must be set").IsTrue();

            // The quest must have been dropped exactly once (idempotent double-entry):
            // no longer active, terminal Drop/Dropped state, quest id released once.
            await Assert.That(character.Quests.ActiveQuests.ContainsKey(questId), $"run {run}: quest must be dropped from active").IsFalse();
            await Assert.That(quest.Step, $"run {run}: terminal step").IsEqualTo(QuestComponentKind.Drop);
            await Assert.That(quest.Status, $"run {run}: terminal status").IsEqualTo(QuestStatus.Dropped);
        }
    }

    private static Character BuildRigCharacter()
    {
        var manifest = QuestScenarioManifest.Load(BuildManifestJson(9500u));
        QuestScenarioDriver.RegisterManifestItems(manifest);
        var quest = QuestScenarioDriver.BuildQuest(manifest);
        return (Character)quest.Owner;
    }

    /// <summary>
    /// Reads the private CompletedQuests backing field. READ-ONLY - never seeds it
    /// (the field type must stay the production Dictionary; see the t_3fdd6ac3
    /// reflection-rig pitfall about re-seeding collection fields).
    /// </summary>
    private static Dictionary<uint, CompletedQuest> GetCompletedBlocks(Character character)
    {
        var field = typeof(CharacterQuests).GetField("<CompletedQuests>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (Dictionary<uint, CompletedQuest>)field.GetValue(character.Quests);
    }

    /// <summary>
    /// Serializes the tests of this class: both rigs re-seed the process-wide
    /// scenario singletons (QuestScenarioDriver.SeedSingletons), so they must
    /// never run concurrently - [NotInParallel] alone does NOT serialize within
    /// a class (TUnit pitfall, t_4f11a519 class).
    /// </summary>
    public class SequentialParallelLimit : IParallelLimit
    {
        public int Limit => 1;
    }
}
