using System.Numerics;
using System.Reflection;

using NLog;
using NLog.Config;
using NLog.Targets;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The combat outcome record must describe what ACTUALLY dispatched.
///
/// A lane run recorded 43 consecutive <c>QuestObjectiveCombatDiag</c> lines that
/// all claimed <c>verb=AutoAttack state=Running delta=0</c> while every trace id
/// resolved to a <c>COMBAT_KITE_MOVE</c> Move leg: the verb was hardcoded and the
/// delta defaulted to 0 for an HP tally that was never taken, so an unread
/// spacing move was indistinguishable from an attack that landed for nothing.
///
/// These tests drive the real leg through <see cref="QuestDecisionScenario.Run"/>
/// on the headless actor (the engine verbs execute) and read the emitted line.
/// </summary>
[NotInParallel]
public class QuestCombatDiagTruthfulTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static readonly MemoryTarget Target = new() { Layout = "${message}" };
    private static LoggingConfiguration _previousConfig;

    [Before(HookType.Class)]
    public static void InstallLogCapture()
    {
        // NLog's configuration is process-global, so the swap is scoped to the
        // whole class run and restored after it (per-test swaps race).
        _previousConfig = LogManager.Configuration;
        var config = new LoggingConfiguration();
        config.AddRuleForAllLevels(Target, "AAEmu.Game.Core.Managers.Bots.QuestBehavior");
        LogManager.Configuration = config;
        LogManager.ReconfigExistingLoggers();
        Target.Logs.Clear();
    }

    [After(HookType.Class)]
    public static void RestoreLogConfig()
    {
        LogManager.Configuration = _previousConfig ?? new LoggingConfiguration();
        LogManager.ReconfigExistingLoggers();
        Target.Logs.Clear();
    }

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
        SeedCombatSurface();
        Target.Logs.Clear();
    }

    [After(Test)]
    public void TearDown()
    {
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
    }

    /// <summary>
    /// A SPACING wake that dispatches a Move must be recorded as a Move. The
    /// kited ranged actor is crowded inside its band floor, so the combat brain
    /// asks for spacing and the leg dispatches a position leg.
    /// </summary>
    [Test]
    public async Task ADispatchedMove_RecordsMove_NotAnAttack()
    {
        var (actor, _, _, _, _) = CreateCrowdedRangedActor("diag-move");

        // Wake 1 settles the range hold; wake 2 is the settled spacing wake.
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "diag-move-1" });
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "diag-move-2" });

        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Move);

        var line = CombatLine("diag-move-2");
        await Assert.That(line).IsNotNull();
        await Assert.That(line).Contains("verb=Move");
        await Assert.That(line).DoesNotContain("verb=AutoAttack");
        // The HP tallies were never taken for a spacing leg, so they are
        // absent — NOT a zero that reads as "an attack that dealt no damage".
        await Assert.That(line).Contains("hpBefore=NA");
        await Assert.That(line).Contains("delta=NA");
        await Assert.That(line).DoesNotContain("delta=0");
    }

    /// <summary>
    /// A real attack records the attack verb AND a delta computed from both
    /// observed tallies — the same leg, in range and assigned.
    /// </summary>
    [Test]
    public async Task ADispatchedAutoAttack_RecordsTheRealDelta()
    {
        var (actor, session, npcObjId) = CreateMeleeActor("diag-attack");
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 100;
        npc.MaxHp = 100;

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "diag-atk-1" });
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "diag-atk-2" });

        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);

        var line = CombatLine("diag-atk-2");
        await Assert.That(line).IsNotNull();
        await Assert.That(line).Contains("verb=AutoAttack");
        // Both tallies were observed on the attack arm, so the delta is a
        // number the gate can read — never NA, never a stand-in.
        await Assert.That(line).DoesNotContain("hpBefore=NA");
        await Assert.That(line).DoesNotContain("delta=NA");
        await Assert.That(line).Contains("delta=");
    }

    /// <summary>
    /// A swing whose damage is DEFERRED must not read as "dealt zero".
    ///
    /// Ranged auto-attack carries an effect speed, so the engine schedules
    /// the effects and the target's HP has not fallen yet when the dispatch
    /// line is written. Re-reading npc.Hp there samples the pre-strike value
    /// and renders <c>delta=0</c> — which is exactly how a working ranged
    /// weapon was read as dealing no damage at all.
    ///
    /// The contract pinned here: a consumer can tell "damage is pending" from
    /// "damage was zero". Pre-fix this line carries <c>delta=0</c> and
    /// <c>damage=none</c>; post-fix it must carry the pending marker.
    ///
    /// TaskManager drains only from the live tick manager, so the deferred
    /// damage cannot actually land inside a unit test — and that is exactly
    /// the state this asserts: the damage is genuinely still pending when the
    /// line is written, not merely unmeasured.
    /// </summary>
    [Test]
    public async Task ADeferredSwing_ReportsPendingDamage_NotZero()
    {
        var (actor, session, npcObjId) = CreateMeleeActor("diag-deferred");
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 100;
        npc.MaxHp = 100;

        // Give the auto-attack skill a non-zero effect speed, exactly the
        // shipped ranged template shape (effect_speed=40 → a deferred swing).
        // Restore it afterwards so no later test inherits the mutation.
        var skills = (Dictionary<uint, SkillTemplate>)GameplayActorTestRig.GetField(
            SkillManager.Instance, "_skills");
        var autoAttackSkillId = CombatDecisionTree.BasicMeleeAutoAttackSkillId;
        var template = skills[autoAttackSkillId];
        var priorSpeed = template.EffectSpeed;
        try
        {
            template.EffectSpeed = 40f;

            _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "diag-def-1" });
            var second = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "diag-def-2" });

            await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);

            var line = CombatLine("diag-def-2");
            await Assert.That(line).IsNotNull();
            // The damage is scheduled but NOT landed: the honest marker is a
            // pending state, and the record must say which state it is.
            await Assert.That(line).Contains("damage=pending");
            // A zero here is the defect this line exists to prevent.
            await Assert.That(line).DoesNotContain("delta=0");
            await Assert.That(line).Contains("delta=pending");
        }
        finally
        {
            template.EffectSpeed = priorSpeed;
        }
    }

    // ------------------------------------------------------------ fixture

    private static (GameplayActor Actor, HeadlessSession Session, uint BoarObjId)
        CreateMeleeActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Hp = actor.Character.MaxHp;
        var here = new Vector3(100f, 100f, 10f);
        var boarObjId = SpawnBoar(session, actor, here, here + new Vector3(2f, 0f, 0f));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        return (actor, session, boarObjId);
    }

    private static (GameplayActor Actor, HeadlessSession Session, Vector3 Here, Vector3 Threat, uint BoarObjId)
        CreateCrowdedRangedActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        // A ranged role: inside its floor it cannot shoot, so the brain asks for
        // spacing and the leg dispatches the kite Move.
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;

        var here = new Vector3(100f, 100f, 10f);
        var boarObjId = SpawnBoar(session, actor, here, here + new Vector3(2f, 0f, 0f));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        return (actor, session, here, here + new Vector3(2f, 0f, 0f), boarObjId);
    }

    private static string? CombatLine(string cycleId)
        => Target.Logs.FirstOrDefault(l => l.Contains("QuestObjectiveCombatDiag") && l.Contains($"cycle={cycleId} "));

    private static void Activate251(Character character)
    {
        character.Quests.ActiveQuests[Quest251] = new Quest(character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
    }

    private static uint SpawnBoar(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = session.SpawnNpc(Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return npcObjId;
    }

    private static void SeedCombatSurface()
    {
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    private static void SeedQuest251GatherAct()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
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
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || !rows.Any(r => r.LootPackId == Pack4530))
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