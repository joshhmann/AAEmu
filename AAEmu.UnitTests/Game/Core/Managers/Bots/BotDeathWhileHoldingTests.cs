using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;
using Portal = AAEmu.Game.Models.Game.Portal;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;
/// <summary>
/// Death-while-holding (M6.2 death watch): when the bot dies mid-hold, the
/// death-watch entry releases the runtime's owned hold plus the actor's live
/// leg (the survival-flee supersede discipline), so the first post-respawn
/// turn re-decides from the fresh listing instead of resuming the stale
/// pre-death leg from the portal position. Death is not a step failure: the
/// death-watch path keeps ok=true and the fault streak stays flat.
/// </summary>
[NotInParallel]
public class BotDeathWhileHoldingTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    [After(Test)]
    public void TearDown()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
        TravelIntentStore.ClearAll();
    }

    /// <summary>
    /// (a) Death mid-hold releases the hold: after the death release, the
    /// runtime no longer owns the pre-death request and the actor's live leg
    /// is interrupted (audited), so nothing can resume the same request.
    /// </summary>
    [Test]
    public async Task DeathMidHold_ReleasesHold_AndInterruptsLiveLeg()
    {
        var (executor, actor, runtime, clock) = CreateQuestRig("death-hold",
            "quest.progress", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(owned)).IsTrue();

        // Death entry: the scheduler's death-watch path unwinds through the
        // executor — runtime observation plus actor-side preemption.
        executor.ReleaseQuestHoldOnDeath(runtime.CharacterId, "bot died");

        await Assert.That(owned.IsTerminal).IsTrue();
        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(actor.ActiveRequest is { IsTerminal: false }).IsFalse();
        await Assert.That(questRuntime.OwnsRunningRequest(actor.ActiveRequest)).IsFalse();
        var retired = actor.AuditTrace.Where(r => r.TraceId == owned.TraceId).ToList();
        await Assert.That(retired.Count).IsEqualTo(1);
        await Assert.That(retired[0].Result).IsEqualTo(ActorLifecycleState.Interrupted);
    }

    /// <summary>
    /// (b) Post-respawn turn re-decides from the fresh listing: after death
    /// release + resurrection (10% HP through the real path), the next quest
    /// wake dispatches a NEW request against the still-pinned quarry — never
    /// the same reference as the stale pre-death leg — and the runtime owns
    /// the new leg.
    /// </summary>
    [Test]
    public async Task PostRespawnTurn_Redecides_DoesNotResumeStaleLeg()
    {
        var (executor, actor, runtime, clock) = CreateQuestRig("death-respawn",
            "quest.progress", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();

        // Death + release (the death-watch entry), then resurrection through
        // the real engine path. The release retires the live leg through the
        // actor and drops the runtime hold; the death itself pins the bot's
        // own pose at the corpse (no portal relocation — the pursuit is
        // unit-relative, so the dead stop is the wrong-place proof: the old
        // leg ended where the target no longer is).
        actor.Character.Hp = 0;
        executor.ReleaseQuestHoldOnDeath(runtime.CharacterId, "bot died");
        CharacterResurrection.Resurrect(actor.Character, inPlace: false, _ => new Portal { X = 0, Y = 0, Z = 0 });
        // Full recovery before the next turn (bandages at the shrine): the
        // fresh listing re-resolves the still-pinned quarry at full hp, so
        // the wake is a calm quest re-decision — never a flee, never the
        // stale request.
        actor.Character.Hp = actor.Character.MaxHp;
        await Assert.That(actor.Character.IsDead).IsFalse();
        // Wake 1 post-respawn settles the death audit (the Target leg
        // re-points the pin through SetTarget — a terminal, not a hold).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        // Wake 2 re-decides: a fresh pursuit Move, never the stale reference,
        // owned by the runtime across subsequent turns.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var next = actor.ActiveRequest;
        await Assert.That(next is { IsTerminal: false }).IsTrue();
        await Assert.That(next).IsNotSameReferenceAs(owned);
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(next)).IsTrue();
    }

    /// <summary>
    /// (c) Death does not consume terminals or record failures: the release
    /// leaves no Completed row for the pre-death trace and no failure detail.
    /// </summary>
    [Test]
    public async Task DeathRelease_ConsumesNoTerminal_RecordsNoFailure()
    {
        var (executor, actor, runtime, clock) = CreateQuestRig("death-clean",
            "quest.progress", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false }).IsTrue();

        actor.Character.Hp = 0;
        executor.ReleaseQuestHoldOnDeath(runtime.CharacterId, "bot died");

        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.LastFailure).IsNull();
        var ownedRows = actor.AuditTrace.Where(r => r.TraceId == owned.TraceId).ToList();
        await Assert.That(ownedRows.Count).IsEqualTo(1);
        await Assert.That(ownedRows[0].Result).IsEqualTo(ActorLifecycleState.Interrupted);
    }

    /// <summary>
    /// (d) No-op when the quest leg never ticked: bots with no executor
    /// state or no quest runtime release nothing and throw nothing.
    /// </summary>
    [Test]
    public async Task DeathRelease_WithoutQuestRuntime_IsNoOp()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("death-noop");
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor,
            TimeProvider = new FakeTimeProvider(),
            ActiveActivityProvider = _ => "quest.progress",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
            GroundHeightProvider = (_, _) => 0f,
        };
        var runtime = new PlayerBotRuntime(actor.Character, "rig");

        executor.ReleaseQuestHoldOnDeath(runtime.CharacterId, "bot died");

        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out _)).IsFalse();
    }

    // ------------------------------------------------------------ rig (QuestRunningOwnershipTests shape)

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock)
        CreateQuestRig(string name, string activity, Vector3 actorPos, Vector3 preyPos)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        actor.Character.Hp = actor.Character.MaxHp;

        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var preyObjId = session.SpawnNpc(Boar3475);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, preyPos);
        region.AddObject(prey);
        prey.Region = region;

        actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        actor.Character.CurrentTarget = prey;

        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            RoamSpeed = 2f,
            EnableWildlifeHunt = true,
            EnableWildlifeButcher = true,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => activity,
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        return (executor, actor, runtime, clock);
    }

    private static void SeedQuest251GatherAct()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(Quest251, out var template))
        {
            template = new QuestTemplate { Id = Quest251, Level = 2 };
            questTemplates[Quest251] = template;
        }
        if (template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == Meat4058))
            return;

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        const uint progressComponentId = 2510901;
        if (!componentTemplates.TryGetValue(progressComponentId, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = progressComponentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[progressComponentId] = component;
        }
        if (!template.Components.ContainsKey(progressComponentId))
            template.Components[progressComponentId] = component;
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ItemId == Meat4058))
        {
            var act = new QuestActObjItemGather(component)
            {
                ActId = GatherActId,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = GatherNeed,
                ItemId = Meat4058,
            };
            component.ActTemplates.Add(act);
            var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
                .GetField("_actTemplatesByDetailType", flags)!.GetValue(manager)!;
            if (!actsByType.TryGetValue(nameof(QuestActObjItemGather), out var acts))
            {
                acts = [];
                actsByType[nameof(QuestActObjItemGather)] = acts;
            }
            acts[616] = act;
        }
    }

    private static void SeedBoarLootLink()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(Pack4530, out var pack) || pack.Loots.All(l => l.ItemId != Meat4058))
        {
            var loot = new Loot
            {
                Id = Pack4530,
                Group = 0,
                ItemId = Meat4058,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = Pack4530,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[Pack4530] = new LootPack
                {
                    Id = Pack4530,
                    Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
                    Groups = [],
                    ActabilityGroups = [],
                    GroupCount = 1,
                };
            }
            else
            {
                pack.Loots.Add(loot);
                if (pack.LootsByGroupNo.TryGetValue(0, out var group))
                    group.Add(loot);
                else
                    pack.LootsByGroupNo[0] = [loot];
            }
        }

        QuestFixtureRow.InvalidateAll();
    }
}
