using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Ordinary death/container/inventory/quest-event proof, not autonomous combat.
/// Actors, level20, canonical quest acceptance and a reference-row loot mirror
/// are fixture setup. Damage tagging and HP-zero death are scripted explicitly.
/// No quest objective or completion is injected. NPC XP/respawn are outside this
/// test: the level gap skips XP and no spawner is installed.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestCompetingLootTests
{
    private const uint Boar = 3475;
    private const uint Meat = 4058;
    private const uint Pack = 4530;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task OrdinaryDeathSharedQuestLoot_OneRecipientAndNoLosingRetryCredit(bool party, bool taggerFirst)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (tagger, session) = GameplayActorTestRig.CreateActor("compete-tagger");
        var (other, _) = GameplayActorTestRig.CreateActor("compete-other");
        GameplayActorTestRig.JoinActorWorld(session, other);
        tagger.Character.Level = other.Character.Level = 20;
        await Assert.That(tagger.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(other.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        if (party)
        {
            await Assert.That(tagger.PartyInvite(other.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(other.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
            // Test shared-container conservation under an ordinary real-party
            // FreeForAll rule; do not infer rotation/master/roll behavior.
            var rule = TeamManager.Instance.GetActiveTeamByUnit(tagger.Character.Id)!.LootingRule;
            rule.LootMethod = LootingRuleMethod.FreeForAll;
            rule.MinimumGrade = 0;
            rule.RollForBindOnPickup = false;
        }
        await Assert.That(tagger.Character.InParty).IsEqualTo(party);
        await Assert.That(other.Character.InParty).IsEqualTo(party);

        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        npc.CharacterTagging.AddTagger(tagger.Character, int.MaxValue / 4);
        await Assert.That(npc.CharacterTagging.Tagger).IsEqualTo(tagger.Character);
        await Assert.That(npc.CharacterTagging.TagTeam != 0).IsEqualTo(party);
        npc.Hp = 0; // Scripted death service input; no earned-kill claim.
        npc.DoDie(tagger.Character, KillReason.Damage);
        await Assert.That(npc.CharacterTagging.Tagger).IsNull();
        await Assert.That(npc.CharacterTagging.TagTeam).IsEqualTo(0u);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        await Assert.That(npc.LootingContainer.Items.Values.Single().Item.TemplateId).IsEqualTo(Meat);
        await Assert.That(npc.LootingContainer.Items.Values.Single().Item.Count).IsEqualTo(1);

        if (!party && !taggerFirst)
        {
            var retained = npc.LootingContainer.Items.Values.Single();
            var otherBefore = other.Character.Quests.ActiveQuests[251].Objectives[0];
            await Assert.That(npc.LootingContainer.TryTakeLoot(other.Character, retained.ItemIndex, retained, true)).IsFalse();
            var foreignOpen = other.Loot(npc.ObjId);
            await Assert.That(foreignOpen.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That((int)foreignOpen.Result!).IsEqualTo(0);
            await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
            await Assert.That(GameplayActorTestRig.BagCount(other, Meat)).IsEqualTo(0);
            await Assert.That(other.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(otherBefore);
        }
        var winner = !party || taggerFirst ? tagger : other;
        var loser = !party || taggerFirst ? other : tagger;
        var winnerQuest = winner.Character.Quests.ActiveQuests[251];
        var loserQuest = loser.Character.Quests.ActiveQuests[251];
        var winnerBefore = winnerQuest.Objectives[0];
        var loserBefore = loserQuest.Objectives[0];
        await Assert.That(winner.Loot(npc.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(winner, Meat)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(loser, Meat)).IsEqualTo(0);
        await Assert.That(winnerQuest.Objectives[0]).IsEqualTo(winnerBefore + 1);
        await Assert.That(loserQuest.Objectives[0]).IsEqualTo(loserBefore);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        await Assert.That(loser.Loot(npc.ObjId).State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(winner.Loot(npc.ObjId).State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(GameplayActorTestRig.BagCount(winner, Meat) + GameplayActorTestRig.BagCount(loser, Meat)).IsEqualTo(1);
        await Assert.That(loserQuest.Objectives[0]).IsEqualTo(loserBefore);
        await Assert.That(winnerQuest.Objectives[0]).IsEqualTo(winnerBefore + 1);
    }

    [Test]
    public async Task Canonical4058RequiresActive251_UnauthorizedOpenDoesNotConsumeOrCredit()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, session) = GameplayActorTestRig.CreateActor("compete-eligible");
        var (withoutQuest, _) = GameplayActorTestRig.CreateActor("compete-noquest");
        GameplayActorTestRig.JoinActorWorld(session, withoutQuest);
        owner.Character.Level = withoutQuest.Character.Level = 20;
        await Assert.That(owner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(withoutQuest.Character.Quests.HasQuest(251)).IsFalse();
        await MakeFreeForAllParty(owner, withoutQuest);
        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        npc.CharacterTagging.AddTagger(owner.Character, int.MaxValue / 4);
        npc.Hp = 0;
        npc.DoDie(owner.Character, KillReason.Damage);
        var before = owner.Character.Quests.ActiveQuests[251].Objectives[0];
        var refused = withoutQuest.Loot(npc.ObjId);
        // Ordinary public Loot may complete with zero container delta; it is
        // not proof of a grant. Assert recipient inventory and retained entry.
        await Assert.That(refused.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)refused.Result!).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(withoutQuest, Meat)).IsEqualTo(0);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        await Assert.That(owner.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before);
        await Assert.That(owner.Loot(npc.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(owner, Meat)).IsEqualTo(1);
        await Assert.That(owner.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before + 1);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OrdinaryPublicLootAfter180Seconds_AdmitsForeign251ButStillRequiresQuest()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, session) = GameplayActorTestRig.CreateActor("compete-public-owner");
        var (foreign, _) = GameplayActorTestRig.CreateActor("compete-public-other");
        var (noQuest, _) = GameplayActorTestRig.CreateActor("compete-public-noquest");
        GameplayActorTestRig.JoinActorWorld(session, foreign);
        GameplayActorTestRig.JoinActorWorld(session, noQuest);
        owner.Character.Level = foreign.Character.Level = noQuest.Character.Level = 20;
        await Assert.That(owner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(foreign.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        npc.CharacterTagging.AddTagger(owner.Character, int.MaxValue / 4);
        npc.Hp = 0;
        npc.DoDie(owner.Character, KillReason.Damage);
        var container = npc.LootingContainer;
        await Assert.That(container.CanMakePublic()).IsFalse();
        var denied = foreign.Loot(npc.ObjId);
        await Assert.That((int)denied.Result!).IsEqualTo(0);
        await Assert.That(container.Items.Count).IsEqualTo(1);
        // Time is fixture input, not a181-second sleep or an invented public flag.
        typeof(LootingContainer).GetProperty("CreationTime", PrivateInstance)!
            .SetValue(container, DateTime.UtcNow.AddSeconds(-181));
        await Assert.That(container.CanMakePublic()).IsTrue();
        container.MakeLootPublic();
        await Assert.That(noQuest.Character.Quests.HasQuest(251)).IsFalse();
        var noQuestOpen = noQuest.Loot(npc.ObjId);
        await Assert.That((int)noQuestOpen.Result!).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(noQuest, Meat)).IsEqualTo(0);
        await Assert.That(container.Items.Count).IsEqualTo(1);
        var ownerBefore = owner.Character.Quests.ActiveQuests[251].Objectives[0];
        var foreignBefore = foreign.Character.Quests.ActiveQuests[251].Objectives[0];
        await Assert.That(foreign.Loot(npc.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(1);
        await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(foreignBefore + 1);
        await Assert.That(owner.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(ownerBefore);
        await Assert.That(container.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StableCharacterIdAlternativeInstance_RetainsSnapshottedLootEntitlement()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, session) = GameplayActorTestRig.CreateActor("compete-id-owner");
        var (replacement, _) = GameplayActorTestRig.CreateActor("compete-id-replacement");
        owner.Character.Level = replacement.Character.Level = 20;
        await Assert.That(owner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(replacement.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        npc.CharacterTagging.AddTagger(owner.Character, int.MaxValue / 4);
        npc.Hp = 0;
        npc.DoDie(owner.Character, KillReason.Damage);
        // Explicit identity substitution only: not an actual persisted relog.
        // Do not register duplicate IDs in the world/character managers.
        var originalId = replacement.Character.Id;
        try
        {
            replacement.Character.Id = owner.Character.Id;
            await Assert.That(ReferenceEquals(owner.Character, replacement.Character)).IsFalse();
            await Assert.That(owner.Character.ObjId == replacement.Character.ObjId).IsFalse();
            var entry = npc.LootingContainer.Items.Values.Single();
            var before = replacement.Character.Quests.ActiveQuests[251].Objectives[0];
            await Assert.That(npc.LootingContainer.TryTakeLoot(replacement.Character, entry.ItemIndex, entry, true)).IsTrue();
            await Assert.That(GameplayActorTestRig.BagCount(replacement, Meat)).IsEqualTo(1);
            await Assert.That(GameplayActorTestRig.BagCount(owner, Meat)).IsEqualTo(0);
            await Assert.That(replacement.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before + 1);
            await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        }
        finally { replacement.Character.Id = originalId; }
    }

    private static async Task MakeFreeForAllParty(GameplayActor owner, GameplayActor member)
    {
        await Assert.That(owner.PartyInvite(member.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(member.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
        var rule = TeamManager.Instance.GetActiveTeamByUnit(owner.Character.Id)!.LootingRule;
        rule.LootMethod = LootingRuleMethod.FreeForAll;
        rule.MinimumGrade = 0;
        rule.RollForBindOnPickup = false;
    }

    [Test]
    public async Task GeneratedForeignLootAfterDeath_HoldsWithoutBanking_ThenPublicAllowsNativeCredit()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, session) = GameplayActorTestRig.CreateActor("compete-brain-owner");
        var (foreign, _) = GameplayActorTestRig.CreateActor("compete-brain-foreign");
        GameplayActorTestRig.JoinActorWorld(session, foreign);
        owner.Character.Level = foreign.Character.Level = 20;
        owner.Character.Hp = owner.Character.MaxHp;
        foreign.Character.Hp = foreign.Character.MaxHp;
        await Assert.That(owner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(foreign.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        npc.CharacterTagging.AddTagger(owner.Character, int.MaxValue / 4);
        npc.Hp = 0;
        npc.DoDie(owner.Character, KillReason.Damage);
        await Assert.That(npc.CharacterTagging.Tagger).IsNull();
        await Assert.That(npc.CharacterTagging.TagTeam).IsEqualTo(0u);
        var foreignBefore = foreign.Character.Quests.ActiveQuests[251].Objectives[0];
        var held = PrepareLoot(foreign, npc);
        await Assert.That(held.Inputs.Ownership).IsEqualTo(LootOwnership.Foreign);
        await Assert.That(held.Decision.Arm).IsEqualTo(LootArm.Ownership);
        await Assert.That(held.Decision.Reason).IsEqualTo(LootReason.OwnershipForeign);
        await Assert.That(held.Decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(LootBrainPlanner.PublishSkip(held, DateTime.UtcNow)).IsFalse();
        await Assert.That(LootLedger.TryGet(foreign.ActorId, npc.ObjId, out _)).IsFalse();
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(0);
        await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(foreignBefore);
        var own = PrepareLoot(owner, npc);
        await Assert.That(LootBrain.MayLoot(own.Inputs.Ownership)).IsTrue();
        await Assert.That(own.Decision.Arm).IsEqualTo(LootArm.Emit);

        MakePublicAfterFixtureTime(npc.LootingContainer);
        var publicPrepared = PrepareLoot(foreign, npc);
        await Assert.That(publicPrepared.Decision.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(publicPrepared.Decision.Verb).IsEqualTo(LootVerb.Loot);
        await Assert.That(foreign.Loot(publicPrepared, "compete-brain-public").State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(1);
        await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(foreignBefore + 1);
        await Assert.That(GameplayActorTestRig.BagCount(owner, Meat)).IsEqualTo(0);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PreparedRawCorpseCannotBypassLaterGeneratedPermission_RejectedKeyCanRetryAfterPublic()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, session) = GameplayActorTestRig.CreateActor("compete-stale-owner");
        var (foreign, _) = GameplayActorTestRig.CreateActor("compete-stale-foreign");
        GameplayActorTestRig.JoinActorWorld(session, foreign);
        owner.Character.Level = foreign.Character.Level = 20;
        owner.Character.Hp = owner.Character.MaxHp;
        foreign.Character.Hp = foreign.Character.MaxHp;
        await Assert.That(owner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(foreign.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        npc.Hp = 0;
        // Explicit raw legacy container setup: no generated entitlement snapshot.
        // No recipient inventory/quest credit is seeded.
        GameplayActorTestRig.SeedLootContainer(npc, (Meat, 1));
        var stale = PrepareLoot(foreign, npc);
        await Assert.That(stale.Decision.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(stale.IsCurrent).IsTrue();
        npc.CharacterTagging.AddTagger(owner.Character, int.MaxValue / 4);
        npc.DoDie(owner.Character, KillReason.Damage); // Same NPC/token, new native permission.
        await Assert.That(npc.CharacterTagging.Tagger).IsNull();
        const string key = "compete-stale-permission";
        var before = foreign.Character.Quests.ActiveQuests[251].Objectives[0];
        var refused = foreign.Loot(stale, key);
        await Assert.That(refused.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(refused.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(refused.StateChanges.Any(s => s.StartsWith("Running", StringComparison.Ordinal))).IsFalse();
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(0);
        await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before);
        await Assert.That(LootLedger.TryGet(foreign.ActorId, npc.ObjId, out _)).IsFalse();
        MakePublicAfterFixtureTime(npc.LootingContainer);
        var current = PrepareLoot(foreign, npc);
        await Assert.That(current.Decision.Arm).IsEqualTo(LootArm.Emit);
        var retry = foreign.Loot(current, key);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(retry.TraceId == refused.TraceId).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(1);
        await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before + 1);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        await Assert.That(foreign.AuditTrace.Count(a => a.TraceId == refused.TraceId)).IsEqualTo(1);
        await Assert.That(foreign.AuditTrace.Count(a => a.TraceId == retry.TraceId)).IsEqualTo(1);
    }

    private static LootBrainPlanner.Prepared PrepareLoot(GameplayActor actor, Npc npc)
        => LootBrainPlanner.Prepare(actor, BotObservedContext.Capture(actor),
            new LootBrainPlanner.Request(npc.ObjId, Boar, 0, false, false,
                LootBrain.DefaultSafeRadiusM, DateTime.UtcNow));

    [Test]
    public async Task RealQuestDispatcherDeniedPermission_DoesNotBank_AndPublicSameCorpseReadmitsOnce()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (owner, session) = GameplayActorTestRig.CreateActor("compete-dispatch-owner");
        var (foreign, _) = GameplayActorTestRig.CreateActor("compete-dispatch-foreign");
        GameplayActorTestRig.JoinActorWorld(session, foreign);
        owner.Character.Level = foreign.Character.Level = 20;
        owner.Character.Hp = owner.Character.MaxHp;
        foreign.Character.Hp = foreign.Character.MaxHp;
        await Assert.That(owner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(foreign.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        var npc = session.World.GetNpc(session.SpawnNpc(Boar))!;
        AttachForDeath(npc, session.World);
        GameplayActorTestRig.SetPosition(foreign, System.Numerics.Vector3.Zero);
        GameplayActorTestRig.SetNpcPosition(session, npc.ObjId, new System.Numerics.Vector3(2, 0, 0));
        var region = session.World.GetRegionByPos(foreign.Character.Transform.World.Position)
            ?? throw new InvalidOperationException("Native fixture region missing.");
        region.AddObject(foreign.Character);
        foreign.Character.Region = region;
        region.AddObject(npc);
        npc.Region = region;
        npc.IsVisible = true;
        npc.Hp = 0;
        GameplayActorTestRig.SeedLootContainer(npc, (Meat, 1));
        var before = foreign.Character.Quests.ActiveQuests[251].Objectives[0];
        var publicationOccurred = false;
        try
        {
            await Assert.That(QuestBehavior.IsLootDispatched(foreign.ActorId, npc.ObjId)).IsFalse();
            await Assert.That(LootLedger.TryGet(foreign.ActorId, npc.ObjId, out _)).IsFalse();
            // QuestBehavior emits its real proposal before asking this existing
            // discovery provider for offers. Script a permission publication at
            // that boundary; selection and dispatch remain the production path.
            var denied = QuestDecisionScenario.Run(foreign, (_, _) =>
            {
                publicationOccurred = true;
                npc.CharacterTagging.AddTagger(owner.Character, int.MaxValue / 4);
                npc.DoDie(owner.Character, KillReason.Damage);
                return [];
            }, new QuestDecisionScenario.QuestOptions { CycleId = "compete-dispatch-denied" });
            await Assert.That(publicationOccurred).IsTrue();
            await Assert.That(denied.SelectedAction).IsEqualTo(ActorActionType.Loot);
            await Assert.That(denied.Request!.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(denied.Request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
            await Assert.That(denied.Request.StateChanges.Any(s => s.StartsWith("Running", StringComparison.Ordinal))).IsFalse();
            await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(0);
            await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before);
            await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
            await Assert.That(QuestBehavior.IsLootDispatched(foreign.ActorId, npc.ObjId)).IsFalse();
            await Assert.That(LootLedger.TryGet(foreign.ActorId, npc.ObjId, out _)).IsFalse();

            MakePublicAfterFixtureTime(npc.LootingContainer);
            var publicRun = QuestDecisionScenario.Run(foreign, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "compete-dispatch-public" });
            await Assert.That(publicRun.SelectedAction).IsEqualTo(ActorActionType.Loot);
            await Assert.That(publicRun.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(publicRun.Request.IdempotencyKey).IsEqualTo(denied.Request.IdempotencyKey);
            await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(1);
            await Assert.That(foreign.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before + 1);
            await Assert.That(QuestBehavior.IsLootDispatched(foreign.ActorId, npc.ObjId)).IsTrue();
            await Assert.That(LootLedger.Read(foreign.ActorId, npc.ObjId).Disposition).IsEqualTo(LootDisposition.Take);
            await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
            QuestDecisionScenario.Run(foreign, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "compete-dispatch-after" });
            await Assert.That(foreign.AuditTrace.Count(a => a.Action == ActorActionType.Loot
                && a.Result == ActorLifecycleState.Completed)).IsEqualTo(1);
            await Assert.That(GameplayActorTestRig.BagCount(foreign, Meat)).IsEqualTo(1);
        }
        finally
        {
            LootLedger.Forget(foreign.ActorId, npc.ObjId);
            // Cleanup only, never a behavioral surrogate: drop this fresh actor's
            // bounded scenario cache entries without clearing other test actors.
            var sync = typeof(QuestBehavior).GetField("PursuitSync", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            lock (sync)
            {
                foreach (var name in new[] { "LastCorpse", "LastApproachIssue", "LastStopHold" })
                    ((System.Collections.IDictionary)typeof(QuestBehavior).GetField(name,
                        BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Remove(foreign.ActorId);
                ((System.Collections.IDictionary)typeof(QuestBehavior).GetField("LootedCorpses",
                    BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Remove((foreign.ActorId, npc.ObjId));
            }
        }
    }

    private static void MakePublicAfterFixtureTime(LootingContainer container)
    {
        typeof(LootingContainer).GetProperty("CreationTime", PrivateInstance)!
            .SetValue(container, DateTime.UtcNow.AddSeconds(-181));
        if (!container.CanMakePublic())
            throw new InvalidOperationException("Fixture-aged generated loot must permit ordinary public transition.");
        container.MakeLootPublic();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task NativeQuestChoiceSkipsNearForeignCorpse_ForFartherAuthorizedOne(bool alreadyPinned, bool rememberedStop)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var fixture = new LootReferenceFixture();
        GameplayActorTestRig.ForceSeedTeamManager();
        var (bot, session) = GameplayActorTestRig.CreateActor("compete-census-own");
        var (foreignOwner, _) = GameplayActorTestRig.CreateActor("compete-census-other");
        GameplayActorTestRig.JoinActorWorld(session, foreignOwner);
        bot.Character.Level = foreignOwner.Character.Level = 20;
        bot.Character.Hp = bot.Character.MaxHp;
        foreignOwner.Character.Hp = foreignOwner.Character.MaxHp;
        await Assert.That(bot.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(foreignOwner.AcceptQuest(251, QuestAcceptorType.Npc, 3512).State).IsEqualTo(ActorLifecycleState.Completed);
        var near = session.World.GetNpc(session.SpawnNpc(Boar))!;
        // SpawnNpc intentionally returns the existing same-template instance;
        // register a second native NPC explicitly, as the corpse-repin fixtures do.
        var far = new Npc { ObjId = near.ObjId + 0x60000u, TemplateId = Boar, Hp = 100, MaxHp = 100 };
        AttachForDeath(near, session.World);
        GameplayActorTestRig.SetPosition(bot, System.Numerics.Vector3.Zero);
        GameplayActorTestRig.SetNpcPosition(session, near.ObjId, new System.Numerics.Vector3(1, 0, 0));
        var region = session.World.GetRegionByPos(bot.Character.Transform.World.Position)
            ?? throw new InvalidOperationException("Native fixture region missing.");
        region.AddObject(bot.Character);
        bot.Character.Region = region;
        region.AddObject(near);
        near.Region = region;
        near.IsVisible = true;
        near.Hp = 0;
        bot.Character.CurrentTarget = null;
        var before = bot.Character.Quests.ActiveQuests[251].Objectives[0];
        try
        {
            if (rememberedStop)
            {
                near.Hp = 100;
                await Assert.That(bot.SetTarget(near.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
                // The normal live pursuit Stop, not reflected memory, establishes
                // the remembered near target before death clears its native tags.
                var stopped = QuestDecisionScenario.Run(bot, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "compete-census-remembered-stop" });
                await Assert.That(stopped.SelectedAction).IsEqualTo(ActorActionType.Stop);
                await Assert.That(stopped.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
                await Assert.That(GameplayActorTestRig.BagCount(bot, Meat)).IsEqualTo(0);
                await Assert.That(bot.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before);
                near.CharacterTagging.AddTagger(foreignOwner.Character, int.MaxValue / 4);
                near.Hp = 0;
                near.DoDie(foreignOwner.Character, KillReason.Damage);
            }
            else if (alreadyPinned)
            {
                GameplayActorTestRig.SeedLootContainer(near, (Meat, 1));
                // Establish the pin through real proposal emission, then publish
                // foreign eligibility before normal dispatch. No pin is injected.
                var denied = QuestDecisionScenario.Run(bot, (_, _) =>
                {
                    near.CharacterTagging.AddTagger(foreignOwner.Character, int.MaxValue / 4);
                    near.DoDie(foreignOwner.Character, KillReason.Damage);
                    return [];
                }, new QuestDecisionScenario.QuestOptions { CycleId = "compete-census-pin-denied" });
                await Assert.That(denied.SelectedAction).IsEqualTo(ActorActionType.Loot);
                await Assert.That(denied.Request!.State).IsEqualTo(ActorLifecycleState.Rejected);
                await Assert.That(denied.Request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
                await Assert.That(QuestBehavior.TryGetCorpse(bot.ActorId, out var pinned)).IsTrue();
                await Assert.That(pinned!.ObjId).IsEqualTo(near.ObjId);
                await Assert.That(QuestBehavior.IsLootDispatched(bot.ActorId, near.ObjId)).IsFalse();
                await Assert.That(LootLedger.TryGet(bot.ActorId, near.ObjId, out _)).IsFalse();
                await Assert.That(GameplayActorTestRig.BagCount(bot, Meat)).IsEqualTo(0);
                await Assert.That(bot.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before);
            }
            else
            {
                near.CharacterTagging.AddTagger(foreignOwner.Character, int.MaxValue / 4);
                near.DoDie(foreignOwner.Character, KillReason.Damage);
            }
            // The alternative does not exist during the denied pin setup.
            session.World.AddObject(far);
            AttachForDeath(far, session.World);
            GameplayActorTestRig.SetNpcPosition(session, far.ObjId, new System.Numerics.Vector3(2, 0, 0));
            region.AddObject(far);
            far.Region = region;
            far.IsVisible = true;
            far.CharacterTagging.AddTagger(bot.Character, int.MaxValue / 4);
            far.Hp = 0;
            far.DoDie(bot.Character, KillReason.Damage);
            await Assert.That(near.LootingContainer.Items.Count).IsEqualTo(1);
            await Assert.That(far.LootingContainer.Items.Count).IsEqualTo(1);
            var selected = QuestDecisionScenario.Run(bot, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "compete-census-two" });
            await Assert.That(selected.SelectedAction).IsEqualTo(ActorActionType.Loot);
            await Assert.That(selected.Request!.TargetId).IsEqualTo(far.ObjId);
            await Assert.That(selected.Request.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(bot, Meat)).IsEqualTo(1);
            await Assert.That(bot.Character.Quests.ActiveQuests[251].Objectives[0]).IsEqualTo(before + 1);
            await Assert.That(GameplayActorTestRig.BagCount(foreignOwner, Meat)).IsEqualTo(0);
            await Assert.That(near.LootingContainer.Items.Count).IsEqualTo(1);
            await Assert.That(far.LootingContainer.Items.Count).IsEqualTo(0);
            await Assert.That(QuestBehavior.IsLootDispatched(bot.ActorId, near.ObjId)).IsFalse();
            await Assert.That(LootLedger.TryGet(bot.ActorId, near.ObjId, out _)).IsFalse();
        }
        finally
        {
            LootLedger.Forget(bot.ActorId, near.ObjId);
            LootLedger.Forget(bot.ActorId, far.ObjId);
            var sync = typeof(QuestBehavior).GetField("PursuitSync", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            lock (sync)
            {
                foreach (var name in new[] { "LastCorpse", "LastApproachIssue", "LastStopHold" })
                    ((System.Collections.IDictionary)typeof(QuestBehavior).GetField(name,
                        BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Remove(bot.ActorId);
                var lootMemory = (System.Collections.IDictionary)typeof(QuestBehavior).GetField("LootedCorpses",
                    BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                lootMemory.Remove((bot.ActorId, near.ObjId));
                lootMemory.Remove((bot.ActorId, far.ObjId));
            }
        }
    }

    private static void AttachForDeath(Npc npc, AAEmu.Game.Models.Game.World.WorldInstance world)
    {
        // Same native death-fixture attachment as NpcAggroRaceTests.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform).GetField("_instanceId", PrivateInstance)!
            .SetValue(npc.Transform, world.Id);
        typeof(AAEmu.Game.Models.Game.World.GameObject).GetField("_parentWorld", PrivateInstance)!.SetValue(npc, world);
        npc.Template = new NpcTemplate { Id = Boar, Scale = 1f };
        npc.Level = 1;
        npc.Buffs = new Buffs(npc);
        npc.CharacterTagging = new Tagging(npc);
    }

    private sealed class LootReferenceFixture : IDisposable
    {
        private readonly FieldInfo _packsField = typeof(LootGameData).GetField("_lootPacks", PrivateInstance)!;
        private readonly FieldInfo _mapsField = typeof(ItemManager).GetField("_lootPackDroppingNpc", PrivateInstance)!;
        private readonly FieldInfo _teamField = typeof(Singleton<TeamManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? _oldPacks;
        private readonly object? _oldMaps;
        private readonly object? _oldTeam;
        private readonly Dictionary<uint, LootPack> _packs;
        private readonly Dictionary<uint, List<LootPackDroppingNpc>> _maps;
        private readonly LootPack? _oldPack;
        private readonly List<LootPackDroppingNpc>? _oldMapping;
        private readonly uint _oldQuest;
        private readonly double _oldRate;

        public LootReferenceFixture()
        {
            GameplayActorTestRig.SeedItemTemplate(Meat);
            _oldQuest = ItemManager.Instance.GetTemplate(Meat).LootQuestId;
            // Verified canonical items4058.loot_quest_id=251, not invented rights.
            ItemManager.Instance.GetTemplate(Meat).LootQuestId = 251;
            AppConfiguration.Instance.World ??= new WorldConfig();
            _oldRate = AppConfiguration.Instance.World.LootRate;
            AppConfiguration.Instance.World.LootRate = 1;
            _oldPacks = _packsField.GetValue(LootGameData.Instance);
            _oldMaps = _mapsField.GetValue(ItemManager.Instance);
            _oldTeam = _teamField.GetValue(null);
            _packs = _oldPacks as Dictionary<uint, LootPack> ?? [];
            _maps = _oldMaps as Dictionary<uint, List<LootPackDroppingNpc>> ?? [];
            _packs.TryGetValue(Pack, out _oldPack);
            _maps.TryGetValue(Boar, out _oldMapping);
            // Exact reference-row10014 mirror. Optional packs are omitted;
            // this fixture isolates the one shared quest-item entry.
            var row = new Loot { Id = 10014, Group = 0, ItemId = Meat, DropRate = 10_000_000,
                MinAmount = 1, MaxAmount = 1, LootPackId = Pack, GradeId = 0, AlwaysDrop = false };
            _packs[Pack] = new LootPack { Id = Pack, Loots = [row], LootsByGroupNo = new() { [0] = [row] },
                Groups = [], ActabilityGroups = [], GroupCount = 1 };
            _maps[Boar] = [new LootPackDroppingNpc { Id = 2093, NpcId = Boar, LootPackId = Pack, DefaultPack = true }];
            _packsField.SetValue(LootGameData.Instance, _packs);
            _mapsField.SetValue(ItemManager.Instance, _maps);
            QuestFixtureRow.Invalidate(251);
        }

        public void Dispose()
        {
            if (_oldPack == null) _packs.Remove(Pack); else _packs[Pack] = _oldPack;
            if (_oldMapping == null) _maps.Remove(Boar); else _maps[Boar] = _oldMapping;
            _packsField.SetValue(LootGameData.Instance, _oldPacks);
            _mapsField.SetValue(ItemManager.Instance, _oldMaps);
            _teamField.SetValue(null, _oldTeam);
            ItemManager.Instance.GetTemplate(Meat).LootQuestId = _oldQuest;
            AppConfiguration.Instance.World.LootRate = _oldRate;
            QuestFixtureRow.Invalidate(251);
        }
    }
}
