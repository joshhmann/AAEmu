using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Branch 1b's PREY arm: an objective whose source is the prey NPC rather than
/// a doodad still has to be carried to a spawner, or the prey never enters the
/// 25 m perception census and the objective funnel rejects its whole candidate
/// pool on template mismatch.
///
/// Measured on a 30-minute 4-bot run: the gather act named no
/// highlight_doodad_id, so both doodad arms no-op'd, the resolver returned
/// false on every wake, the bot was never carried to the prey ~51 m away, and
/// 288 consecutive AdvanceQuest attempts were declined with one byte-identical
/// reason string.
///
/// PRECEDENCE, and the regression this class exists to protect: an objective
/// that DOES have a doodad source must keep resolving to that doodad's
/// spawner. The prey arm is reached only when no doodad arm resolved, so the
/// doodad path is structurally unreachable from it.
///
/// Fixture ids 97_0xx are unclaimed repo-wide and absent from canonical data,
/// so a shared-id collision with the process-wide QuestManager singleton cannot
/// mask a regression.
/// </summary>
[NotInParallel]
public class QuestObjectiveSourcePreyTravelTests
{
    // ---- doodad-source fixture (block 97_0xx): the regression pin
    private const uint DoodadQuest = 97_001;
    private const uint DoodadComponent = 97_002;
    private const uint DoodadActId = 97_003;
    private const uint DoodadItem = 97_004;
    private const uint DoodadTemplate = 97_005;
    private const uint DoodadGroup = 97_006;
    private const uint DoodadFunc = 97_007;
    private const int DoodadNeed = 3;

    // ---- prey-source fixture: a gather act whose source is an NPC, reached
    //      through the loot chain (npc -> pack -> item), never a doodad.
    private const uint PreyQuest = 97_010;
    private const uint PreyComponent = 97_011;
    private const uint PreyActId = 97_012;
    private const uint PreyItem = 97_013;
    private const uint PreyTemplate = 97_014;
    private const uint PreyPack = 97_015;
    private const int PreyNeed = 3;

    /// <summary>A quest whose gather act names NO loot chain at all: no prey
    /// template can be derived for it, so no arm can resolve a source.</summary>
    private const uint SourcelessQuest = 97_020;
    private const uint SourcelessComponent = 97_021;
    private const uint SourcelessActId = 97_022;
    private const uint SourcelessItem = 97_023;
    private const int SourcelessNeed = 3;
    /// <summary>A walkable spawner for a template no active quest names — the
    /// "spawner tables exist, this quest's source is not among them" case.</summary>
    private const uint UnrelatedNpc = 97_025;

    private static readonly MethodInfo ResolveObjectiveSource = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestObjectiveSource", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        SeedDoodadQuestShape();
        SeedPreyQuestShape();
        SeedSourcelessQuestShape();
    }

    // =================================================================
    // (1) REGRESSION PIN — a doodad source still resolves to the DOODAD
    //     spawner, byte-identically. The most important test in the class.
    // =================================================================

    [Test]
    public async Task DoodadSource_ResolvesToDoodadSpawner_NotThePreySpawner()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-doodad-pin");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, DoodadQuest, objective: 0);
        // BOTH sources are walkable and at different places. If the prey arm
        // could preempt, the nearer prey spawner would win and this fails.
        SeedDoodadSpawner(actor, DoodadTemplate, new Vector3(30, 0, 0));
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(5, 0, 0));

        var (resolved, position, template, _) = Resolve(actor);

        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(DoodadTemplate);
        await Assert.That(position.X).IsEqualTo(30f);
    }

    /// <summary>
    /// The doodad arm's own perception short-circuit is untouched: a perceived
    /// granting doodad means the gather leg owns the wake, so the resolver
    /// falls through exactly as before — and a prey spawner sitting right there
    /// still does not get walked to, because the row IS credited-by-perception.
    /// </summary>
    [Test]
    public async Task DoodadSource_PerceivedDoodad_NeverFallsThroughToPreySpawner()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("prey-doodad-perceived");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        StageActiveQuest(actor, DoodadQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(5, 0, 0));
        // No doodad spawner at all: the doodad arm cannot resolve, but a LIVE
        // granting doodad in the census means the gather leg owns this wake.
        GameplayActorTestRig.SpawnGatherDoodad(session, DoodadTemplate, DoodadGroup, DoodadFunc,
            DoodadItem, new Vector3(2, 0, 0));

        var (resolved, _, template, _) = Resolve(actor);

        // The row has no derived prey template (its item has no loot chain), so
        // the prey arm has nothing to walk to either — the assertion that
        // matters is that resolution did NOT happen against the prey spawner.
        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    // =================================================================
    // (2) THE FIX — no doodad source, known prey template -> NPC spawner.
    // =================================================================

    [Test]
    public async Task PreySource_NoDoodadSource_ResolvesToPreyNpcSpawner()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-fix");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(51, 0, 0));

        // Precondition: the row really has no doodad source, or this test
        // would be proving the doodad arm again instead of the fix.
        var row = QuestFixtureRow.FromQuestData(PreyQuest);
        await Assert.That(QuestBehavior.HasGatherTemplates(row)).IsFalse();
        await Assert.That(row.PreyTemplate).IsEqualTo(PreyTemplate);

        var (resolved, position, template, _) = Resolve(actor);

        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(PreyTemplate);
        await Assert.That(position.X).IsEqualTo(51f);
    }

    // =================================================================
    // (5) The resolution is driven by the FIXTURE's derived prey template,
    //     not by any literal: an id that appears nowhere in canonical data
    //     is still the one requested.
    // =================================================================

    /// <summary>
    /// Nothing about the prey arm is keyed to a specific id. Two quests whose
    /// gather items derive DIFFERENT prey templates each walk to their OWN
    /// spawner — so the walk follows the row's derived value, and a second
    /// unclaimed synthetic id resolves just as the first does.
    /// </summary>
    [Test]
    public async Task PreySource_ResolutionFollowsTheFixturesDerivedPreyTemplate()
    {
        const uint secondItem = 97_030;
        const uint secondPreyTemplate = 97_031;
        const uint secondPack = 97_032;
        const uint secondQuest = 97_033;
        const uint secondComponent = 97_034;
        const uint secondActId = 97_035;
        SeedPreyChain(secondQuest, secondComponent, secondActId, secondItem, secondPreyTemplate, secondPack);

        var (actor, _) = GameplayActorTestRig.CreateActor("prey-derived");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(40, 0, 0));
        SeedNpcSpawner(actor, secondPreyTemplate, new Vector3(70, 0, 0));

        // The FIRST quest id in quest-id order derives the FIRST prey.
        StageActiveQuest(actor, PreyQuest, objective: 0);
        StageActiveQuest(actor, secondQuest, objective: 0);

        var (resolved, position, template, _) = Resolve(actor);
        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(PreyTemplate);
        await Assert.That(position.X).IsEqualTo(40f);

        // Drop the lower-id quest: now the ONLY prey template left is the
        // second, never-walked-to-before one, and it is what gets requested.
        actor.Character.Quests.ActiveQuests.TryRemove(PreyQuest, out _);
        QuestFixtureRow.InvalidateAll();

        var (resolved2, position2, template2, _) = Resolve(actor);
        await Assert.That(resolved2).IsTrue();
        await Assert.That(template2).IsEqualTo(secondPreyTemplate);
        await Assert.That(position2.X).IsEqualTo(70f);
    }

    /// <summary>
    /// A perceived prey means the objective funnel owns the wake — travel must
    /// never preempt it. Same discipline the doodad arms already follow.
    /// </summary>
    [Test]
    public async Task PreySource_PerceivedPrey_WithholdsTravel()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("prey-perceived");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        // Both the actor and the prey must sit in the region graph: the 25 m
        // census (WorldManager.GetAround) reads regions, so an un-registered
        // actor would see nothing and the test would pass vacuously.
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(51, 0, 0));
        SpawnPerceivedPrey(session, PreyTemplate, new Vector3(3, 0, 0));

        var (resolved, _, template, _) = Resolve(actor);

        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    /// <summary>
    /// LIVENESS, not identity: a DEAD prey-template NPC standing in the
    /// perception census must NOT suppress the prey arm.
    ///
    /// Measured on a 4-bot live run: all four bots reached boar 44326
    /// together and killed it; char 1 lawfully looted it and the other three
    /// read <c>loot=[validate=foreign-claim]</c>. While that corpse sat in
    /// the census, PerceivedPrey matched it on template identity alone and
    /// withheld the prey source, so quest travel resolved to the NEARER
    /// OFFERER instead — the bots walked away from the objective they were one
    /// corpse from. The objective funnel never selected anything those wakes
    /// (<c>rejects=[dead=1]</c>, <c>selected=-</c>), so the give-up streak
    /// re-earned its threshold (<c>stage=SELECT detail=no-target raw=1
    /// relevant=1</c>, wakes=10) and Advance withdrew behind
    /// <c>give-up-threshold</c> with have=0. Removing the corpse from the
    /// census restored the prey spawner destination on the very next arm,
    /// which is the oscillation this test pins shut.
    ///
    /// The predicate's own contract already excludes this case: it claims to
    /// be true when the selector "would already consider it a candidate", and
    /// the selector rejects <c>Hp &lt;= 0</c> as <c>dead</c>
    /// (QuestObjectiveTargetSelector.cs:181-186).
    /// </summary>
    [Test]
    public async Task PreySource_DeadPreyInCensus_DoesNotSuppressTravel()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("prey-perceived-corpse");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(51, 0, 0));
        // Same template, same census, same 3 m: only Hp differs from the live
        // suppression test above, so any difference in the verdict is
        // attributable to liveness alone.
        SpawnPerceivedCorpse(session, PreyTemplate, new Vector3(3, 0, 0));

        var (resolved, position, template, isPrey) = Resolve(actor);

        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(PreyTemplate);
        await Assert.That(position.X).IsEqualTo(51f);
        // Still a PREY source, so it keeps outranking the offerer in
        // ResolveQuestTravelTarget and stays exempt from the arm cap.
        await Assert.That(isPrey).IsTrue();
    }

    /// <summary>
    /// The live half of the pair, kept byte-for-byte beside its corpse twin:
    /// a LIVE perceived prey still means the objective funnel owns the wake,
    /// so travel must still withhold. Without this the fix would read as
    /// "remove the short-circuit" rather than "read its own contract".
    /// </summary>
    [Test]
    public async Task PreySource_LivePreyInCensus_StillSuppressesTravel()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("prey-perceived-live-pin");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(51, 0, 0));
        SpawnPerceivedPrey(session, PreyTemplate, new Vector3(3, 0, 0));

        var (resolved, _, template, _) = Resolve(actor);

        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    // =================================================================
    // (3) Fail-closed preserved: neither doodad source nor a resolvable prey.
    // =================================================================

    /// <summary>
    /// A gather act whose item reaches no loot chain derives no prey template,
    /// so no source can be resolved and the resolver declines exactly as it
    /// always has — even with a walkable NPC spawner sitting in the world.
    /// </summary>
    [Test]
    public async Task NeitherDoodadNorResolvablePrey_ReturnsFalse()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-failclosed");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, SourcelessQuest, objective: 0);
        // A walkable spawner exists, but it is not this quest's source.
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(20, 0, 0));

        var row = QuestFixtureRow.FromQuestData(SourcelessQuest);
        await Assert.That(row.PreyTemplate).IsEqualTo(0u);

        var (resolved, _, template, _) = Resolve(actor);

        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    // =================================================================
    // (4) A prey template with no spawner entry resolves to nothing and
    //     does not throw.
    // =================================================================

    [Test]
    public async Task PreyWithNoSpawner_ResolvesNothing_WithoutThrowing()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-nospawner");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        // Deliberately NO spawner for PreyTemplate. The resolver must read the
        // lookup as "unknown" and fall through, never throw. Touch the spawner
        // tables so the world is genuinely walkable-shaped — the failure mode
        // under test is "no spawner for THIS prey", not "no tables at all"
        // (that is the separate outermost case below).
        SeedNpcSpawner(actor, UnrelatedNpc, new Vector3(70, 0, 0));
        await Assert.That(actor.Character.ParentWorld!.SpawnManager!.GetAllSpawners().Values
            .SelectMany(s => s).Any(s => s.UnitId == PreyTemplate)).IsFalse();

        var (resolved, _, template, _) = Resolve(actor);

        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    /// <summary>
    /// A world with no spawner tables at all is the outermost fail-closed
    /// case: the prey arm must not dereference what arm 1 never dereferenced.
    /// </summary>
    [Test]
    public async Task PreyWithNoSpawnerTables_ResolvesNothing_WithoutThrowing()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-notables");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        await Assert.That(actor.Character.ParentWorld!.SpawnManager).IsNull();

        var (resolved, _, template, _) = Resolve(actor);

        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    /// <summary>
    /// A credited objective withholds travel whatever its source is: the
    /// prey arm reads the SAME credit gate the gather arm does.
    /// </summary>
    [Test]
    public async Task CreditedPreyObjective_WithholdsTravel()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-credited");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: PreyNeed);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(51, 0, 0));

        var (resolved, _, template, _) = Resolve(actor);

        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
    }

    // ================================================================
    // Harness
    // ================================================================

    private static (bool Resolved, Vector3 Position, uint Template, bool IsPrey) Resolve(GameplayActor actor)
    {
        var args = new object?[] { actor, default(Vector3), 0u, false };
        var resolved = (bool)ResolveObjectiveSource.Invoke(null, args)!;
        return (resolved, (Vector3)args[1]!, (uint)args[2]!, (bool)args[3]!);
    }

    private static void StageActiveQuest(GameplayActor actor, uint questId, int objective)
    {
        var template = QuestManager.Instance.GetTemplate(questId)
            ?? throw new InvalidOperationException($"quest template {questId} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        quest.Objectives = [objective, 0, 0, 0, 0];
        actor.Character.Quests.ActiveQuests[questId] = quest;
        QuestFixtureRow.InvalidateAll();
    }

    // ================================================================
    // Fixtures
    // ================================================================

    /// <summary>
    /// The regression-pin shape: a gather act whose item is granted by a
    /// DOODAD loot func, so the row derives a GatherDoodadTemplate and the
    /// doodad arm is the one that must resolve.
    /// </summary>
    private static void SeedDoodadQuestShape()
    {
        // Both inversion surfaces must be established BEFORE any row is
        // derived: QuestFixtureRow reads them through Singleton.PeekInstance,
        // which yields an empty source set (doodad 0 / prey 0) when either is
        // not yet seeded.
        var _ = LootGameData.Instance;
        SeedGatherAct(DoodadQuest, DoodadComponent, DoodadActId, DoodadItem);
        GameplayActorTestRig.SeedGatherDoodadSource(DoodadTemplate, DoodadGroup, DoodadFunc, DoodadItem);
        QuestFixtureRow.InvalidateAll();
        var row = QuestFixtureRow.FromQuestData(DoodadQuest);
        if (row.GatherDoodadTemplate != DoodadTemplate)
            throw new InvalidOperationException(
                $"fixture row resolved doodad {row.GatherDoodadTemplate}, expected {DoodadTemplate}");
    }

    /// <summary>
    /// The fixed shape: a gather act whose item reaches NO doodad loot func and
    /// DOES reach an npc loot pack, so the row derives a PreyTemplate and no
    /// doodad source at all — exactly the act shape the resolver could not serve.
    /// </summary>
    private static void SeedPreyQuestShape()
        => SeedPreyChain(PreyQuest, PreyComponent, PreyActId, PreyItem, PreyTemplate, PreyPack);

    private static void SeedSourcelessQuestShape()
        => SeedGatherAct(SourcelessQuest, SourcelessComponent, SourcelessActId, SourcelessItem);

    private static void SeedGatherAct(uint questId, uint componentId, uint actId, uint itemId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var questTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate>)
            typeof(QuestManager).GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(questId, out var template))
        {
            template = new AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate { Id = questId, Level = 10 };
            questTemplates[questId] = template;
        }

        var componentTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.QuestComponentTemplate>)
            typeof(QuestManager).GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(componentId, out var component))
        {
            component = new AAEmu.Game.Models.Game.Quests.QuestComponentTemplate(template)
            {
                Id = componentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[componentId] = component;
        }
        if (!template.Components.ContainsKey(componentId))
            template.Components[componentId] = component;

        // No HighlightDoodadId, and no quest_act_obj_alias_id: the act names the
        // item and nothing else, so the ONLY resolvable source is the loot chain.
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == actId))
        {
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = actId,
                DetailId = componentId,
                DetailType = nameof(QuestActObjItemGather),
                Count = 3,
                ItemId = itemId,
                ThisComponentObjectiveIndex = 0,
            });
        }
    }

    /// <summary>
    /// Stages the npc -&gt; pack -&gt; item chain the row's PreyTemplate is
    /// inverted from, so the fixture's prey is derived, never hand-typed.
    /// </summary>
    private static void SeedPreyChain(uint questId, uint componentId, uint actId, uint itemId,
        uint preyTemplate, uint preyPack)
    {
        SeedGatherAct(questId, componentId, actId, itemId);

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var _ = LootGameData.Instance;

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(preyTemplate, out var rows) || rows.All(r => r.LootPackId != preyPack))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = preyTemplate,
                NpcId = preyTemplate,
                LootPackId = preyPack,
                DefaultPack = true,
            });
            map[preyTemplate] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(preyPack, out var pack) || pack.Loots.All(l => l.ItemId != itemId))
        {
            var loot = new Loot
            {
                Id = preyPack,
                Group = 0,
                ItemId = itemId,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = preyPack,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[preyPack] = new LootPack
                {
                    Id = preyPack,
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
        var row = QuestFixtureRow.FromQuestData(questId);
        if (row.PreyTemplate != preyTemplate)
            throw new InvalidOperationException(
                $"fixture row resolved prey {row.PreyTemplate}, expected {preyTemplate}");
    }

    private static void SeedNpcSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_600 + spawners.Count);
        spawners[key] =
        [
            new NpcSpawner
            {
                Id = key,
                UnitId = npcTemplateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            }
        ];
    }

    private static void SeedDoodadSpawner(GameplayActor actor, uint doodadTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, DoodadSpawner>)typeof(SpawnManager)
            .GetProperty("DoodadSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_700 + spawners.Count);
        spawners[key] = new DoodadSpawner
        {
            Id = key,
            UnitId = doodadTemplateId,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
        };
    }

    /// <summary>
    /// Joins the actor's character to the region graph at
    /// <paramref name="position"/>. The 25 m perception census reads REGIONS
    /// (WorldManager.GetAround), so without this the census sees nothing and
    /// any "the source was already perceived" assertion passes vacuously.
    /// </summary>
    private static void JoinToRegion(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
    }

    /// <summary>
    /// Drops a LIVE prey npc inside the 25 m perception census. Region
    /// registration is what the census actually reads (WorldManager.GetAround),
    /// so the resolver sees the prey as perceived exactly as the objective
    /// funnel would — which is what must withhold travel.
    /// </summary>
    private static void SpawnPerceivedPrey(HeadlessSession session, uint npcTemplateId, Vector3 npcPos)
        => SpawnPerceivedNpc(session, npcTemplateId, npcPos, alive: true);

    /// <summary>
    /// The DEAD half of <see cref="SpawnPerceivedPrey"/>: a corpse of the
    /// fixture's prey template inside the same 25 m census. Region
    /// registration is identical, so the only variable under test is liveness.
    /// </summary>
    private static void SpawnPerceivedCorpse(HeadlessSession session, uint npcTemplateId, Vector3 npcPos)
        => SpawnPerceivedNpc(session, npcTemplateId, npcPos, alive: false);

    private static void SpawnPerceivedNpc(HeadlessSession session, uint npcTemplateId, Vector3 npcPos, bool alive)
    {
        var region = session.World.GetRegionByPos(npcPos)
            ?? throw new InvalidOperationException("rig world has no region at the npc position");
        var objId = session.SpawnNpc(npcTemplateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = npcTemplateId, Scale = 1f };
        npc.Hp = alive ? 100 : 0;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
    }

}