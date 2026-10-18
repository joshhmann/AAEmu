using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;

using Microsoft.Extensions.Time.Testing;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The Fighting mode owner (player-ai-mode-layer.md). Live run 11 (E325): every
/// module denied while the bot was in battle, the arbiter elected "(none)",
/// nothing fought back and the battle never ended. These tests pin that a bot
/// being attacked elects self-defence, that the executor fights the attacker
/// through the shared engagement step, and that leaving the fight releases it.
/// </summary>
[NotInParallel]
public class SelfDefenceTests
{
    private const uint BoarTemplateId = 3475;

    /// <summary>A Free-mode module that, like every real one, denies in battle.</summary>
    private sealed class FreeOnlyModule(string name, int priority, string activity) : IBotActivityModule
    {
        public string Name => name;
        public int Priority => priority;
        public IReadOnlyList<string> DeclaredActivities => [activity];

        public BotActivityDecision CanActivate(BotActivityContext context) => context.Bot.Character.IsInBattle
            ? BotActivityDecision.Deny("bot in battle")
            : BotActivityDecision.Allow(activity);

        public BotActivity Activate(BotActivityContext context) => new(activity, Name);
    }

    private static (GameplayActor Actor, HeadlessSession Session, Npc Boar) CreateBotAndBoar(string name, float distance)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        // The quest combat rig's boar shape: a real templated NPC in the
        // actor's region (QuestCombatKiteTravelTests.SpawnBoar).
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
        var region = session.World.GetRegionByPos(Vector3.Zero)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        var boarObjId = session.SpawnNpc(BoarTemplateId);
        var boar = session.World.GetNpc(boarObjId)!;
        boar.Template = new NpcTemplate { Id = BoarTemplateId, Scale = 1f };
        boar.Hp = 100;
        boar.MaxHp = 100;
        boar.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, new Vector3(distance, 0, 0));
        region.AddObject(boar);
        boar.Region = region;
        return (actor, session, boar);
    }

    private static void Attack(Npc boar, Character bot)
    {
        boar.CurrentTarget = bot;
        boar.IsInBattle = true;
        bot.IsInBattle = true;
    }

    private static BotRoamStepExecutor CreateExecutor(GameplayActor actor, Func<string?> activity, Npc boar, FakeTimeProvider clock) => new()
    {
        ActorFactory = _ => actor,
        TimeProvider = clock,
        ActiveCadence = TimeSpan.FromMilliseconds(100),
        ActiveActivityProvider = _ => activity(),
        NearbyNpcProvider = (_, _) => [boar],
        NearbyCharacterProvider = (_, _) => [],
    };

    [Test]
    public async Task Attackers_NpcTargetingOrHatingTheBot_IsAnAttacker_DeadOrIndifferentIsNot()
    {
        var (actor, _, boar) = CreateBotAndBoar("defend-attackers", 2f);
        var bot = actor.Character;
        await Assert.That(BotAttackers.IsAttacker(bot, boar)).IsFalse();

        // Turning to look (alert) is not an attack; a target in battle is.
        boar.CurrentTarget = bot;
        await Assert.That(BotAttackers.IsAttacker(bot, boar)).IsFalse();
        boar.IsInBattle = true;
        await Assert.That(BotAttackers.IsAttacker(bot, boar)).IsTrue();
        await Assert.That(BotAttackers.FindNearest(bot, 45f, [boar], [])).IsSameReferenceAs(boar);
        // A held attacker beyond reach is not chased by the current-target shortcut.
        bot.CurrentTarget = boar;
        await Assert.That(BotAttackers.FindNearest(bot, 1f, [boar], [])).IsNull();
        bot.CurrentTarget = null;
        boar.IsInBattle = false;

        boar.CurrentTarget = null;
        boar.AggroTable.TryAdd(bot.ObjId, new Aggro(bot));
        await Assert.That(BotAttackers.IsAttacker(bot, boar)).IsTrue();

        boar.Hp = 0;
        await Assert.That(BotAttackers.IsAttacker(bot, boar)).IsFalse();
        await Assert.That(BotAttackers.FindNearest(bot, 45f, [boar], [])).IsNull();
    }

    [Test]
    public async Task Module_OnlyAllowsWhileInBattleWithAnAttackerInReach()
    {
        var (actor, _, boar) = CreateBotAndBoar("defend-module", 2f);
        var bot = actor.Character;
        Npc? attacker = null;
        var module = new SelfDefenceActivityModule(_ => attacker);
        var context = new BotActivityContext { Bot = new PlayerBotRuntime(bot, "rig") };

        await Assert.That(module.CanActivate(context).CanActivate).IsFalse();
        bot.IsInBattle = true;
        await Assert.That(module.CanActivate(context).CanActivate).IsFalse();
        attacker = boar;
        var decision = module.CanActivate(context);
        await Assert.That(decision.CanActivate).IsTrue();
        await Assert.That(decision.ActivityName).IsEqualTo(SelfDefenceActivityModule.ActivityName);
        await Assert.That(BotActivityDispatchSurface.Resolve(decision.ActivityName))
            .IsEqualTo(BotActivityCarrier.Defend);
    }

    [Test]
    public async Task Arbiter_BotUnderAttack_WithoutAnOwnerElectsNothing_WithSelfDefenceFightsBack()
    {
        var (actor, _, boar) = CreateBotAndBoar("defend-arbiter", 2f);
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        Attack(boar, actor.Character);
        IBotActivityModule[] freeModules =
        [
            new FreeOnlyModule("Quest", 58, "quest.progress"),
            new FreeOnlyModule("Roam", 50, "presence.roam"),
            new FreeOnlyModule("Idle", 0, "idle"),
        ];

        // The run-11 shape: every module denies in battle, nothing is elected.
        var without = new BotGoalArbiter(freeModules).Arbitrate(runtime, 12f);
        await Assert.That(without.Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);

        var with = new BotGoalArbiter([.. freeModules, new SelfDefenceActivityModule(_ => boar)])
            .Arbitrate(runtime, 12f);
        await Assert.That(with.Activity?.Name).IsEqualTo(SelfDefenceActivityModule.ActivityName);
    }

    [Test]
    public async Task Executor_DefendElected_AttackerInRange_AutoAttacksIt()
    {
        var (actor, _, boar) = CreateBotAndBoar("defend-swing", 2f);
        Attack(boar, actor.Character);
        var clock = new FakeTimeProvider();
        var executor = CreateExecutor(actor, () => SelfDefenceActivityModule.ActivityName, boar, clock);
        var runtime = new PlayerBotRuntime(actor.Character, "rig");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(boar.ObjId);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack && r.TargetId == boar.ObjId))
            .IsTrue();
    }

    [Test]
    public async Task Executor_DefendElected_DistantAttacker_ChasesIt()
    {
        var (actor, _, boar) = CreateBotAndBoar("defend-chase", 20f);
        Attack(boar, actor.Character);
        var clock = new FakeTimeProvider();
        var executor = CreateExecutor(actor, () => SelfDefenceActivityModule.ActivityName, boar, clock);
        var runtime = new PlayerBotRuntime(actor.Character, "rig");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest?.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(actor.ActiveRequest?.MoveOwner).IsEqualTo("OTHER:self-defence");
    }

    [Test]
    public async Task Executor_DefendNoLongerElected_ReleasesTheFight()
    {
        var (actor, _, boar) = CreateBotAndBoar("defend-release", 2f);
        Attack(boar, actor.Character);
        var clock = new FakeTimeProvider();
        string? elected = SelfDefenceActivityModule.ActivityName;
        var executor = CreateExecutor(actor, () => elected, boar, clock);
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(boar.ObjId);

        elected = null;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(actor.Character.IsAutoAttack).IsFalse();
    }

    [Test]
    public async Task Executor_DefendingWithARouteArmed_DoesNotWalkOffAlongTheRoute()
    {
        // Review P1: between swings the actor is idle, and the route step used
        // to issue the next waypoint, walking the bot out of its own fight.
        var (actor, _, boar) = CreateBotAndBoar("defend-route", 2f);
        Attack(boar, actor.Character);
        var clock = new FakeTimeProvider();
        var executor = CreateExecutor(actor, () => SelfDefenceActivityModule.ActivityName, boar, clock);
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        executor.SetRoamRoute(actor.Character, new BotPath([new Vector3(80, 0, 0)], BotPath.LoopMode.Loop));

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack && r.TargetId == boar.ObjId))
            .IsTrue();
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move, MoveOwner: "ROAM" })
            .IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move && r.MoveOwner == "ROAM"))
            .IsFalse();
    }

    [Test]
    public async Task Validation_ModuleSetWithoutAFightingOwner_ReportsTheMode()
    {
        var report = BotActivityDispatchValidation.Validate([new FreeOnlyModule("Roam", 50, PresenceRoamActivityModule.ActivityName)]);
        await Assert.That(report.Gaps.Any(g => g.ModuleName == "(mode Fighting)")).IsTrue();

        var covered = BotActivityDispatchValidation.Validate(
        [
            new FreeOnlyModule("Roam", 50, PresenceRoamActivityModule.ActivityName),
            new SelfDefenceActivityModule(_ => null),
        ]);
        await Assert.That(covered.IsClean).IsTrue();
    }
}
