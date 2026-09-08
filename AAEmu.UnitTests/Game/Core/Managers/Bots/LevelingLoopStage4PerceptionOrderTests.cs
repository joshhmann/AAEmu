using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Stage 4 perception-order probes (investigate-first, probes only — NO production edits).
///
/// Mechanism under test (verified): a single perception snapshot feeds
/// PursueObjectives (LevelingLoopScenario.cs:644) and TurnIn (:649);
/// first-wins npcByTemplate.TryAdd pinning (:717); per-NPC offerings are
/// quest-sorted (GameplayActor.cs:990) but cross-NPC order is encounter
/// order (Region.cs:446-450, swap-remove churn :94-109). Chain under test:
/// quest 321 (Yuno 440 → hunt Goblin Marauders 4941 → report Yuno 440)
/// then 173 (Yuno 440 → report Bryce 501) then 188
/// (seeds at LevelingLoopScenario.cs:228-239).
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class LevelingLoopStage4PerceptionOrderTests
{
    private uint _nextObjId = 0x73000;

    /// <summary>Region-joined fixture NPC so Observe's GetAround sees it.</summary>
    private uint SpawnHubNpc(HeadlessSession session, uint templateId, Vector3 position)
    {
        var npc = new Npc
        {
            ObjId = _nextObjId++,
            TemplateId = templateId,
            Hp = 100,
            MaxHp = 100,
            Template = new NpcTemplate { Id = templateId, Scale = 1f }
        };
        session.World.AddObject(npc);
        npc.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region != null)
        {
            region.AddObject(npc);
            npc.Region = region;
        }

        return npc.ObjId;
    }

    /// <summary>
    /// Joins the fixture CHARACTER to its region grid — CreateActor registers
    /// the character with the world but AddObject alone never joins the
    /// region graph, so Observe's WorldManager.GetAround (obj.Region guard)
    /// would see nothing.
    /// </summary>
    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        region?.AddObject(character);
        character.Region = region;
    }

    /// <summary>Seeds one corpse's loot so the Loot contract action grants an item.</summary>
    private static void SeedCorpseLoot(HeadlessSession session, uint npcObjId)
    {
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId)!,
            (GameplayActorTestRig.TestItemTemplateId, 1));
    }

    private sealed class RigKillSeam : LevelingLoopScenario.IKillCreditSeam
    {
        public bool TryKill(GameplayActor actor, Npc target)
        {
            if (target.Hp <= 0)
                return true;
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, target);
            target.Hp = 0;
            return true;
        }
    }

    /// <summary>
    /// Duplicate-Yuno probe: two template-440 Yunos are spawned, then the
    /// region-first one (first-spawned = encounter-order head, Region.cs:446-450
    /// insertion order) is despawned from the region graph before the run, so
    /// the surviving Yuno is the only live acceptor. The chained 321 → 173 run
    /// must complete both links through the live acceptor.
    /// Pre-fix expectation: TURN-IN Navigation/RejectedAction failure (recorded
    /// honestly below — passes pre-fix, kept as characterization).
    /// </summary>
    [Test]
    public async Task DuplicateYuno_ResolvesLiveAcceptor()
    {
        var (_, session) = GameplayActorTestRig.CreateActor("pb-stage4-dupe-yuno");
        var character = session.Character;
        character.Level = 24;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(174, true);
        character.Quests!.SetCompletedQuestFlag(181, true);
        character.Quests!.SetCompletedQuestFlag(182, true);

        GameplayActorTestRig.SeedQuestHunt(
            LevelingLoopScenario.SeedMarianopleQuestPastureHuntId, // 321
            1340, 2012, 1342,
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            LevelingLoopScenario.SeedMarianopleGoblinMarauderNpcTemplateId, // 4941
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            huntCount: 1,
            level: 24);

        GameplayActorTestRig.SeedQuestDelivery(
            LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId, // 173
            2131, 2132,
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, // 501
            level: 24);

        // Two Yunos offering the same quests; the region-first (first-spawned)
        // one is despawned from the region graph before the run — Observe never
        // sees it, so the survivor is the only pinned acceptor.
        var yunoFirstObjId = SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, new Vector3(2f, 0f, 0f));
        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, new Vector3(4f, 0f, 0f));
        var mobObjId = SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleGoblinMarauderNpcTemplateId, new Vector3(6f, 0f, 0f));
        SeedCorpseLoot(session, mobObjId);
        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, new Vector3(10f, 0f, 0f));

        var staleYuno = session.World.GetNpc(yunoFirstObjId)!;
        staleYuno.Region?.RemoveObject(staleYuno);
        staleYuno.Region = null;

        var opts = new LevelingLoopScenario.LoopOptions
        {
            AdaptiveBand = true,
            BandMin = 20,
            BandMax = 26,
            MaxLinks = 2,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        };

        var result = LevelingLoopScenario.Run(character, opts, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"Duplicate-Yuno probe failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Links.Count).IsEqualTo(2);
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedMarianopleQuestPastureHuntId)).IsTrue();
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId)).IsTrue();
    }

    /// <summary>
    /// Out-of-bubble reporter probe: Bryce 501 stands beyond the 25m Observe
    /// radius (GameplayActor.cs:230-232) at accept time, so the accept-time
    /// snapshot cannot pin him. The 173 link must complete via a fresh
    /// pre-TurnIn sweep.
    /// Pre-fix expectation: fail "report NPC 501 not among perceived targets"
    /// (actual outcome recorded honestly on run).
    /// </summary>
    [Test]
    public async Task OutOfBubbleReporter_Reperceives()
    {
        var (_, session) = GameplayActorTestRig.CreateActor("pb-stage4-far-bryce");
        var character = session.Character;
        character.Level = 24;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(LevelingLoopScenario.SeedMarianopleQuestPastureHuntId, true);
        character.Quests!.SetCompletedQuestFlag(181, true);

        GameplayActorTestRig.SeedQuestDelivery(
            LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId, // 173
            2131, 2132,
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, // 501
            level: 24);

        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, new Vector3(2f, 0f, 0f));
        // Beyond the 25m Observe bubble at accept time.
        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, new Vector3(40f, 0f, 0f));

        var opts = new LevelingLoopScenario.LoopOptions
        {
            AdaptiveBand = true,
            BandMin = 20,
            BandMax = 26,
            MaxLinks = 1,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        };

        var result = LevelingLoopScenario.Run(character, opts, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"Out-of-bubble reporter probe failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId);
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId)).IsTrue();
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId)).IsFalse();
    }

    /// <summary>
    /// Chained-order pin probe: both Stage 4 links sit at the same level, so
    /// the decision contract (lowest level, then lowest quest id —
    /// BotDecisionProposal.cs:283-288) must pick 173 before 321. Pins the
    /// order so future regressions fail loudly.
    /// </summary>
    [Test]
    public async Task ChainedOrder_PinDecisionOrder()
    {
        var (_, session) = GameplayActorTestRig.CreateActor("pb-stage4-pin-order");
        var character = session.Character;
        character.Level = 24;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(174, true);
        character.Quests!.SetCompletedQuestFlag(181, true);
        character.Quests!.SetCompletedQuestFlag(182, true);

        GameplayActorTestRig.SeedQuestHunt(
            LevelingLoopScenario.SeedMarianopleQuestPastureHuntId, // 321
            1340, 2012, 1342,
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            LevelingLoopScenario.SeedMarianopleGoblinMarauderNpcTemplateId, // 4941
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            huntCount: 1,
            level: 24);

        GameplayActorTestRig.SeedQuestDelivery(
            LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId, // 173
            2131, 2132,
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, // 501
            level: 24);

        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, new Vector3(2f, 0f, 0f));
        var mobObjId = SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleGoblinMarauderNpcTemplateId, new Vector3(6f, 0f, 0f));
        SeedCorpseLoot(session, mobObjId);
        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, new Vector3(10f, 0f, 0f));

        var opts = new LevelingLoopScenario.LoopOptions
        {
            AdaptiveBand = true,
            BandMin = 20,
            BandMax = 26,
            MaxLinks = 2,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        };

        var result = LevelingLoopScenario.Run(character, opts, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"Chained-order pin probe failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Links.Count).IsEqualTo(2);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId);
        await Assert.That(result.Links[1].QuestId).IsEqualTo(LevelingLoopScenario.SeedMarianopleQuestPastureHuntId);
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedMarianopleQuestPastureHuntId)).IsTrue();
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId)).IsTrue();
    }

    /// <summary>
    /// Budget-exhaustion probe: Bryce 501 is spawned in the world but
    /// detached from the region graph (the DuplicateYuno stale-NPC shape),
    /// so no Perceive sweep can ever pin him while GetNpcByTemplateId still
    /// resolves him as a travel destination. The TurnIn travel leg must
    /// exhaust its bounded budget and fail closed Navigation — terminating,
    /// never looping.
    /// </summary>
    [Test]
    public async Task UnreachableReporter_ExhaustsTravelBudgetAndFailsNavigation()
    {
        var (_, session) = GameplayActorTestRig.CreateActor("pb-stage4-unreachable-bryce");
        var character = session.Character;
        character.Level = 24;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(LevelingLoopScenario.SeedMarianopleQuestPastureHuntId, true);
        character.Quests!.SetCompletedQuestFlag(181, true);

        GameplayActorTestRig.SeedQuestDelivery(
            LevelingLoopScenario.SeedMarianopleQuestGuardDeliveryId, // 173
            2131, 2132,
            LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, // 440
            LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, // 501
            level: 24);

        SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleYunoNpcTemplateId, new Vector3(2f, 0f, 0f));
        var bryceObjId = SpawnHubNpc(session, LevelingLoopScenario.SeedMarianopleBryceNpcTemplateId, new Vector3(40f, 0f, 0f));

        // Detach Bryce from the region graph: Perceive never sees him, but
        // the world registry still resolves him — every leg walks, none pins.
        var bryce = session.World.GetNpc(bryceObjId)!;
        bryce.Region?.RemoveObject(bryce);
        bryce.Region = null;

        var opts = new LevelingLoopScenario.LoopOptions
        {
            AdaptiveBand = true,
            BandMin = 20,
            BandMax = 26,
            MaxLinks = 1,
            MaxTurnInTravelLegs = 2,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        };

        var result = LevelingLoopScenario.Run(character, opts, new RigKillSeam());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("TURN-IN");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.Navigation);
    }
}
