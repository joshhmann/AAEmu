using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 party-spike rig: one REAL party (leader + 2 members — ordinary
/// Characters sharing one TeamManager party and world) takes down ONE
/// fixture elite NPC end-to-end through the M5 IGameplayActor contract
/// ONLY. Composition under test: PARTY-GATE (engine registry truth) →
/// interleaved RALLY legs → ENGAGE/assist target copying → coordinated
/// round-robin hunt (per-member sustain FIRST, standoff band, shared-target
/// burst rotation) → kill credit.
///
/// Kill seam: DOCUMENTED pre-authorized synthetic kill-credit convention
/// (identical to AdventurerSpikeScenarioRigTests.RigSpikeRuntime): the rig
/// runtime applies the killing blow through the REAL
/// QuestManager.DoOnMonsterHuntEvents entry point (the exact call
/// Npc.DoDie makes for a character killer) because bare fixture NPCs carry
/// no template/AI/spawner scaffolding for a full DoDie. Real damage is the
/// E2E's job (PartySpikeE2eTests).
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class PartySpikeScenarioRigTests
{
    /// <summary>Fixture elite objId (above the session's 1000+ range and the actor id range).</summary>
    private const uint EliteObjId = 0x7000;

    /// <summary>Rally staging distance from the leader (members spawn here).</summary>
    private static readonly Vector3 MemberOffset = new(20, 0, 0);

    /// <summary>Elite position relative to the leader.</summary>
    private static readonly Vector3 EliteOffset = new(12, 0, 0);

    private sealed class RigPartySpikeRuntime(bool canKill = true) : PartySpikeScenario.IPartySpikeRuntime
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(50));
                Thread.Sleep(1);
            }
            return request;
        }

        public bool TargetDown(Npc target) => target.Hp <= 0;

        public bool EnsureKillCredit(GameplayActor actor, Npc target)
        {
            if (!canKill)
                return false; // bounded-rounds fail-closed probe (the elite never goes down)
            // Rig-faked damage (documented convention — see class doc): the
            // REAL quest-credit entry point DoDie uses for a character
            // killer, group/zone fanout included; then mark the fixture down.
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, target);
            target.Hp = 0;
            return true;
        }

        public void RecoveryTick(Character character)
        {
            // Rig-faked regen (documented): 10% of max per beat — the shape
            // of out-of-combat recovery; real regen/potion healing is proven
            // on the live stack.
            if (character.MaxHp > 0)
                character.Hp = Math.Min(character.MaxHp, character.Hp + Math.Max(1, character.MaxHp / 10));
            Thread.Sleep(1);
        }
    }

    /// <summary>Full adult vitals on every party character (keeps sustain out of geometry tests).</summary>
    private static void FillVitals(Character character)
    {
        character.Level = 10;
        if (character.MaxHp > 0)
            character.Hp = character.MaxHp;
    }

    /// <summary>
    /// Spawns the fixture elite into the host world: a bare rig NPC (minimal
    /// template — Npc.AnimActionId reads Template.NpcPostureSets when movement
    /// places characters into its region; faction stays null so BaseUnit.
    /// CanAttack reads attackable — recon-verified adventurer-spike convention).
    /// </summary>
    private static uint SpawnElite(HeadlessSession session, uint npcTemplateId, Vector3 position,
        int hp = 5000)
    {
        var elite = new Npc
        {
            ObjId = EliteObjId,
            TemplateId = npcTemplateId,
            Hp = hp,
            MaxHp = hp,
            Template = new NpcTemplate { Id = npcTemplateId, Scale = 1f }
        };
        session.World.AddObject(elite);
        elite.Transform.Local.SetPosition(position);
        return elite.ObjId;
    }

    /// <summary>
    /// Builds a REAL 3-character party on the rig: three actors in the
    /// LEADER's world, joined through the contract (PartyInvite/PartyAccept),
    /// exactly like PartyFollowAssistScenarioRigTests.CreateParty but for
    /// leader + two members.
    /// </summary>
    private static (GameplayActor Leader, GameplayActor Member1, GameplayActor Member2, HeadlessSession HostSession)
        CreateParty(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, hostSession) = GameplayActorTestRig.CreateActor(name + "-leader");
        var (member1, _) = GameplayActorTestRig.CreateActor(name + "-m1");
        var (member2, _) = GameplayActorTestRig.CreateActor(name + "-m2");
        GameplayActorTestRig.JoinActorWorld(hostSession, member1);
        GameplayActorTestRig.JoinActorWorld(hostSession, member2);

        // Rig precondition (fail loud, not soft): the party must really form
        // through the contract before the scenario runs.
        if (leader.PartyInvite(member1.Character.ObjId).State != ActorLifecycleState.Completed)
            throw new InvalidOperationException("rig: member1 party invite failed");
        if (member1.PartyAccept().State != ActorLifecycleState.Completed)
            throw new InvalidOperationException("rig: member1 party accept failed");
        if (leader.PartyInvite(member2.Character.ObjId).State != ActorLifecycleState.Completed)
            throw new InvalidOperationException("rig: member2 party invite failed");
        if (member2.PartyAccept().State != ActorLifecycleState.Completed)
            throw new InvalidOperationException("rig: member2 party accept failed");

        return (leader, member1, member2, hostSession);
    }

    private static PartySpikeScenario.PartySpikeOptions RigOptions(uint eliteTemplateId) => new()
    {
        // Rig combat surface: the seeded fixture skill (real Character.UseSkill
        // gate headless); real damage skills need real game data — the E2E's job.
        EliteNpcTemplateId = eliteTemplateId,
        CastRotation = [GameplayActorTestRig.TestSkillId],
        HealItemTemplateId = 0 // pure-regen default; the heal-item test opts in explicitly
    };

    [Test]
    public async Task PartySpike_RealPartyOfThree_RalliesAssistsAndKillsElite()
    {
        var (leader, member1, member2, host) = CreateParty("m7ps");
        foreach (var actor in new[] { leader, member1, member2 })
            FillVitals(actor.Character);
        leader.Character.Transform.Local.SetPosition(Vector3.Zero);
        member1.Character.Transform.Local.SetPosition(MemberOffset);
        member2.Character.Transform.Local.SetPosition(new Vector3(0, 20, 0));
        var eliteObjId = SpawnElite(host, PartySpikeScenario.DefaultEliteNpcTemplateId,
            leader.Character.Transform.World.Position + EliteOffset);

        var result = PartySpikeScenario.Run(
            new[] { leader.Character, member1.Character, member2.Character },
            new RigPartySpikeRuntime(), RigOptions(PartySpikeScenario.DefaultEliteNpcTemplateId));

        await Assert.That(result.Passed, "party spike FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.FailStage, "no fail stage on a pass").IsEmpty();

        // Stage coverage: RALLY (both members) → ENGAGE → ASSIST (both) → HUNT-KILL.
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames.Count(s => s == "RALLY")).IsEqualTo(2);
        await Assert.That(stageNames).Contains("ENGAGE");
        await Assert.That(stageNames.Count(s => s == "ASSIST")).IsEqualTo(2);
        await Assert.That(stageNames.Count(s => s == "HUNT-KILL")).IsEqualTo(1);
        var engageIndex = stageNames.IndexOf("ENGAGE");
        var firstKillIndex = stageNames.IndexOf("HUNT-KILL");
        await Assert.That(engageIndex >= 0 && engageIndex < firstKillIndex).IsTrue();

        // All THREE characters ended up targeting the shared elite.
        foreach (var actor in new[] { leader, member1, member2 })
            await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(eliteObjId);

        // The merged trace carries records from all THREE actors (multi-actor
        // execution-order merge — the M7 party contract).
        var tracedActors = result.TraceRecords.Select(r => r.ActorId).Distinct().ToList();
        await Assert.That(tracedActors.Count).IsEqualTo(3);
        await Assert.That(tracedActors).Contains(leader.ActorId);
        await Assert.That(tracedActors).Contains(member1.ActorId);
        await Assert.That(tracedActors).Contains(member2.ActorId);

        // Contract vocabulary: moves (rally/close), targets, casts.
        var actions = result.TraceRecords.Select(r => r.Action).ToList();
        await Assert.That(actions.Count(a => a == ActorActionType.Cast)).IsGreaterThan(0);
        await Assert.That(actions.Count(a => a == ActorActionType.Target)).IsEqualTo(3);

        // All criteria green + the elite is really down.
        var failed = result.Criteria.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail).ToList();
        await Assert.That(failed, "all party-spike criteria must pass: " + string.Join("; ", failed)).IsEmpty();
        await Assert.That(host.World.GetNpc(EliteObjId)!.Hp).IsLessThanOrEqualTo(0);
    }

    [Test]
    public async Task PartySpike_CharactersNotInOneParty_FailsClosedBeforeMutation()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (leader, hostSession) = GameplayActorTestRig.CreateActor("m7ps-gate-a");
        var (member1, _) = GameplayActorTestRig.CreateActor("m7ps-gate-b");
        var (member2, _) = GameplayActorTestRig.CreateActor("m7ps-gate-c");
        GameplayActorTestRig.JoinActorWorld(hostSession, member1);
        GameplayActorTestRig.JoinActorWorld(hostSession, member2);
        foreach (var actor in new[] { leader, member1, member2 })
        {
            FillVitals(actor.Character);
            actor.Character.Transform.Local.SetPosition(Vector3.Zero);
        }

        var result = PartySpikeScenario.Run(
            new[] { leader.Character, member1.Character, member2.Character },
            new RigPartySpikeRuntime(), RigOptions(PartySpikeScenario.DefaultEliteNpcTemplateId));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PARTY-GATE");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(result.TraceRecords).IsEmpty();
        // No mutation happened anywhere in the world.
        await Assert.That(leader.Character.CurrentTarget).IsNull();
        await Assert.That(member1.Character.CurrentTarget).IsNull();
        await Assert.That(member2.Character.CurrentTarget).IsNull();
    }

    [Test]
    public async Task PartySpike_EliteNeverDies_FailsClosedOnRoundBudget()
    {
        var (leader, member1, member2, host) = CreateParty("m7ps-starve");
        foreach (var actor in new[] { leader, member1, member2 })
            FillVitals(actor.Character);
        leader.Character.Transform.Local.SetPosition(Vector3.Zero);
        SpawnElite(host, PartySpikeScenario.DefaultEliteNpcTemplateId, new Vector3(12, 0, 0));

        var options = RigOptions(PartySpikeScenario.DefaultEliteNpcTemplateId) with { MaxHuntRounds = 3 };
        var result = PartySpikeScenario.Run(
            new[] { leader.Character, member1.Character, member2.Character },
            new RigPartySpikeRuntime(canKill: false), options);

        // §17 fail-closed: bounded-rounds exhaustion is Starvation at HUNT —
        // the leash-reset window was missed, never a fake completion.
        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("HUNT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.Starvation);
        await Assert.That(result.Stages.Select(s => s.Stage)).DoesNotContain("HUNT-KILL");
        await Assert.That(result.FailReason).Contains("leash-reset window");
        await Assert.That(host.World.GetNpc(EliteObjId)!.Hp).IsGreaterThan(0);
    }

    [Test]
    public async Task PartySpike_MemberBelowThreshold_SustainsWithBaggedPotionBeforeEngaging()
    {
        var (leader, member1, member2, host) = CreateParty("m7ps-sustain");
        foreach (var actor in new[] { leader, member1, member2 })
            FillVitals(actor.Character);
        leader.Character.Transform.Local.SetPosition(Vector3.Zero);
        member1.Character.Transform.Local.SetPosition(MemberOffset);
        member2.Character.Transform.Local.SetPosition(new Vector3(0, 20, 0));
        SpawnElite(host, PartySpikeScenario.DefaultEliteNpcTemplateId, new Vector3(12, 0, 0));

        // Seed member1 BELOW the sustain threshold with the bagged fixture
        // heal item (the real UseItem contract path attempts it once per
        // recovery round; rig-faked regen remains the documented fallback).
        var wounded = member1.Character;
        wounded.Hp = (int)(wounded.MaxHp * 0.2f);
        var hpBefore = wounded.Hp;
        GameplayActorTestRig.StockItem(host, GameplayActorTestRig.TestItemTemplateId, 2);

        var options = RigOptions(PartySpikeScenario.DefaultEliteNpcTemplateId) with
        {
            HealItemTemplateId = GameplayActorTestRig.TestItemTemplateId
        };
        var result = PartySpikeScenario.Run(
            new[] { leader.Character, member1.Character, member2.Character },
            new RigPartySpikeRuntime(), options);

        await Assert.That(result.Passed, "sustain spike FAILED:\n" + result.Evidence()).IsTrue();

        // The wounded member sustained BEFORE any kill: retreat + heal
        // attempt precede HUNT-KILL in the merged execution order.
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames).Contains("SUSTAIN-RETREAT");
        await Assert.That(stageNames).Contains("SUSTAIN-HEAL");
        await Assert.That(stageNames.IndexOf("SUSTAIN-RETREAT") < stageNames.IndexOf("HUNT-KILL")).IsTrue();

        // Recovery really applied to the WOUNDED member only (per-member
        // sustain — the other two never dropped below the threshold).
        await Assert.That(wounded.Hp).IsGreaterThan(hpBefore);
        await Assert.That(wounded.Hp / (float)wounded.MaxHp).IsGreaterThanOrEqualTo(0.8f);
        await Assert.That(leader.Character.Hp).IsEqualTo(leader.Character.MaxHp);
    }
}
