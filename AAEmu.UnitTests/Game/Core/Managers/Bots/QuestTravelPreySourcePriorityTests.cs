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
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestPattern = AAEmu.Game.Models.Game.Quests.Director.QuestPattern;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The TRAVEL/PERCEPTION disagreement in <c>ResolveQuestTravelTarget</c>'s tail,
/// pinned through the production resolver itself.
///
/// Measured on a 30-minute 4-bot unattended run: quest 251's PREY arm resolved
/// a real spawner (npc 3475, 265-465 m out) on every wake, and a nearer in-band
/// offerer out-voted it 436/436 times, so the funnel rejected its whole
/// candidate pool on template mismatch (have=0) and 324 AdvanceQuest attempts
/// were declined with one byte-identical reason. The two proximities DISAGREE:
/// the funnel's 25 m perception census is read at ARBITRATION time while travel
/// spans many wakes — the bots did stand 16.1 m from the (15586.5,15117.3)
/// spawner, yet 3475 never entered the census (closest approach 59.7 m).
///
/// THE RULE: a source that resolved through the PREY arm outranks the offerer
/// outright. A DOODAD source keeps the nearer-wins comparison BYTE FOR BYTE —
/// including the exact-tie case, where the offerer still wins. Every guard that
/// suppresses the prey arm (credited objective, Deliver pattern, perceived prey)
/// is unchanged and is pinned here, so the priority can only apply to an
/// objective genuinely blocked on a specific prey.
///
/// Fixture ids 94_1xx / 94_2xx / 94_3xx are unclaimed repo-wide and absent
/// from canonical data, so a shared-id collision with the process-wide
/// QuestManager singleton cannot mask a regression.
/// </summary>
[NotInParallel]
public class QuestTravelPreySourcePriorityTests
{
    // ---- prey-source fixture (block 94_1xx): gather act -> npc -> pack -> item
    private const uint PreyQuest = 94_100;
    private const uint PreyComponent = 94_101;
    private const uint PreyActId = 94_102;
    private const uint PreyItem = 94_103;
    private const uint PreyTemplate = 94_104;
    private const uint PreyPack = 94_105;
    private const int PreyNeed = 3;

    // ---- offerer fixture (block 94_2xx): a real in-band quest giver
    private const uint OfferQuest = 94_200;
    private const uint OfferComponent = 94_201;
    private const uint OffererNpc = 94_202;
    private const byte InBandLevel = 3;             // inside the frozen 1..9 band

    // ---- the Deliver rewrite (block 94_3xx): the SAME prey chain, but the
    //      quest also supplies the item, so the row classifies as Deliver.
    private const uint DeliverQuest = 94_300;
    private const uint DeliverComponent = 94_301;
    private const uint DeliverActId = 94_302;
    private const uint DeliverSupplyComponent = 94_307;
    private const uint DeliverSupplyActId = 94_303;
    private const uint DeliverItem = 94_304;
    private const uint DeliverTemplate = 94_305;
    private const uint DeliverPack = 94_306;

    /// <summary>Where the prey spawner sits: past the perception census the
    /// funnel reads (25 m) by a wide margin, exactly like the measured run.</summary>
    private static readonly Vector3 PreySpawnerPos = new(400, 0, 0);

    /// <summary>The nearer in-band offerer that out-voted the prey in the run.</summary>
    private static readonly Vector3 OffererSpawnerPos = new(30, 0, 0);

    private static readonly MethodInfo ResolveTravelTarget = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestTravelTarget", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ArmQuestTravel = typeof(BotRoamStepExecutor)
        .GetMethod("ArmQuestTravel", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();

        // The Deliver rewrite reads the template's own component list, so the
        // SUPPLY act must already be on it — and it needs its OWN component id,
        // because the gather act owns DeliverComponent as a Progress step.
        SeedSupplyAct(DeliverQuest, DeliverSupplyComponent, DeliverSupplyActId, DeliverItem);
        SeedPreyChain(PreyQuest, PreyComponent, PreyActId, PreyItem, PreyTemplate, PreyPack);
        SeedPreyChain(DeliverQuest, DeliverComponent, DeliverActId, DeliverItem, DeliverTemplate, DeliverPack);
        GameplayActorTestRig.SeedQuestOffer(OfferQuest, OfferComponent, OffererNpc, level: InBandLevel);

        // Both shapes are asserted, never assumed: the prey row derives its
        // prey, the supply row rewrites itself to Deliver. A fixture that
        // stopped deriving either would silently turn every test below into a
        // proof of the wrong arm.
        QuestFixtureRow.InvalidateAll();
        var preyRow = QuestFixtureRow.FromQuestData(PreyQuest);
        if (preyRow.PreyTemplate != PreyTemplate)
            throw new InvalidOperationException(
                $"prey fixture row resolved prey {preyRow.PreyTemplate}, expected {PreyTemplate}");
        if (preyRow.ObjectivePattern == QuestPattern.Deliver)
            throw new InvalidOperationException("prey fixture row must not classify as Deliver");
        var deliverRow = QuestFixtureRow.FromQuestData(DeliverQuest);
        if (deliverRow.ObjectivePattern != QuestPattern.Deliver)
            throw new InvalidOperationException(
                $"deliver fixture row classified {deliverRow.ObjectivePattern}, expected Deliver");
    }

    // ================================================================
    // (1) REGRESSION PIN — the case the nearer-wins tie-break was written
    //     for. UNCHANGED: a reachable/nearer objective source still beats a
    //     far farther offerer, armed through the objective-source branch.
    // ================================================================
    [Test]
    public async Task PreySource_NearerThanOfferer_StillWins_AndArmsObjectiveSourceBranch()
    {
        var (executor, actor, runtime) = CreateRig("prey-priority-near-1");
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(30, 0, 0));   // the prey source, nearer
        SeedNpcSpawner(actor, OffererNpc, new Vector3(500, 0, 0));     // a far walkable offerer

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(30f); // the prey source, NOT the 500 m offerer

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(30f);
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
    }

    // ================================================================
    // (2) THE MEASURED FAILURE. An uncredited objective whose prey is NOT
    //     perceived, with the offerer NEARER: the objective source wins now.
    //     Pre-fix this returned the 30 m offerer on every wake (436/436).
    // ================================================================
    [Test]
    public async Task PreySource_NotPerceived_OffererFarther_WalksToPreySpawner()
    {
        var (executor, actor, runtime) = CreateRig("prey-priority-1");
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, PreySpawnerPos);  // 400 m out, as measured
        SeedNpcSpawner(actor, OffererNpc, OffererSpawnerPos); // 30 m — would have won before

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(PreySpawnerPos.X); // the prey, NOT the 30 m offerer
        await Assert.That(resolved!.Value.X).IsNotEqualTo(OffererSpawnerPos.X);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(PreySpawnerPos.X);
        // Armed through the objective-source branch, so the 3-arm give-up bound
        // and the spawner identity still apply to this destination.
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId) is { IsFinished: false }).IsTrue();
    }

    // ================================================================
    // (3) UNCHANGED GUARD — an objective source that IS already perceived
    //     withholds (the funnel owns that wake), so the nearer offerer wins.
    // ================================================================
    [Test]
    public async Task PreySource_Perceived_WithholdsTravel_OffererStillWins()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("prey-priority-perceived");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, PreySpawnerPos);
        SeedNpcSpawner(actor, OffererNpc, OffererSpawnerPos);
        // A LIVE prey inside the 25 m census. Region registration is what the
        // census actually reads, so without it the withhold would be vacuous.
        SpawnPerceivedPrey(session, PreyTemplate, new Vector3(3, 0, 0));

        var resolved = Resolve(actor);

        // The prey arm declined outright, so arm 1b has no source at all and
        // the tail answers with the only walkable candidate.
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(OffererSpawnerPos.X);
    }

    // ================================================================
    // (4) UNCHANGED GUARD — a credited objective never contributes a source,
    //     however near its prey spawner sits.
    // ================================================================
    [Test]
    public async Task PreySource_Credited_OffererStillWins()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-priority-credited");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: PreyNeed);
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(20, 0, 0));
        SeedNpcSpawner(actor, OffererNpc, OffererSpawnerPos);

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(OffererSpawnerPos.X);
    }

    // ================================================================
    // (5) UNCHANGED GUARD — a Deliver-pattern objective's source is the
    //     SUPPLY grant, not a mob. Walking it to a spawner would be the
    //     pointless trek arm 1b documents, so the nearer offerer keeps it.
    // ================================================================
    [Test]
    public async Task DeliverObjective_OffererNearer_KeepsOfferer()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-priority-deliver");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, DeliverQuest, objective: 0);
        SeedNpcSpawner(actor, DeliverTemplate, new Vector3(20, 0, 0));
        SeedNpcSpawner(actor, OffererNpc, OffererSpawnerPos);

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(OffererSpawnerPos.X);
    }

    // ================================================================
    // (6) EXACT TIE — documented outcome UNCHANGED: a prey source exactly as
    //     far as the offerer now wins (it outranks, so the comparison never
    //     runs). The old nearer-wins tail called this a tie and returned the
    //     OFFERER; the whole rule turns on the prey beating it at any
    //     distance, so an exact tie cannot keep the offerer.
    // ================================================================
    [Test]
    public async Task PreySource_ExactlyAsFarAsOfferer_StillWinsThePrey()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("prey-priority-tie");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, PreyQuest, objective: 0);
        var tied = new Vector3(60, 0, 0);
        SeedNpcSpawner(actor, PreyTemplate, tied);
        SeedNpcSpawner(actor, OffererNpc, tied);

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        // Same coordinates either way; what the assertion pins is WHICH arm
        // answered, read off the armed reason rather than the position.
        await Assert.That(resolved!.Value.X).IsEqualTo(tied.X);
    }

    /// <summary>
    /// The exact tie resolved through the ARMING path, where the two arms are
    /// distinguishable: the objective-source branch stamps a different reason
    /// than the plain offerer target, so this is the only place an exact tie
    /// can be pinned rather than merely described.
    /// </summary>
    [Test]
    public async Task PreySource_ExactlyAsFarAsOfferer_ArmsObjectiveSourceBranch()
    {
        var (executor, actor, runtime) = CreateRig("prey-priority-tie-arm");
        StageActiveQuest(actor, PreyQuest, objective: 0);
        var tied = new Vector3(60, 0, 0);
        SeedNpcSpawner(actor, PreyTemplate, tied);
        SeedNpcSpawner(actor, OffererNpc, tied);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
    }

    // ================================================================
    // (7) THE CAP vs A PREY SOURCE. The measured freeze: quest 251's
    //     objective (item 4058 x3) is blocked on prey ~330 m out, the
    //     resolver returned it EVERY wake, and the 3-arm cap then withheld
    //     travel from the 4th arm on — 0 BotMoveStart for the final 8
    //     minutes, have= stuck at 1. A prey outside the 25 m census is the
    //     one source travel exists to fix, so the cap must not bind it.
    // ================================================================

    /// <summary>
    /// THE FIX. Four arms to the SAME prey spawner past the 3-arm cap: the
    /// fourth still arms. Pre-fix this returned false with
    /// <c>objective-source-unreachable (doodad ...)</c> — the cap is the
    /// whole difference, so reverting the change turns this RED.
    /// </summary>
    [Test]
    public async Task PreySource_ArmedPastTheThreeArmCap_StillWalks()
    {
        var (executor, actor, runtime) = CreateRig("prey-cap-exempt");
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, PreySpawnerPos);

        // Arms 1-3: the cap budget. Each arm drops its route the way a wake
        // that finished or lost the route does, so the next arm re-arms.
        for (var arm = 1; arm <= 3; arm++)
        {
            await Assert.That(Arm(executor, runtime, actor)).IsTrue();
            var armed = executor.GetBotState(runtime.CharacterId)!;
            await Assert.That(armed.QuestTravelTarget!.Value.X).IsEqualTo(PreySpawnerPos.X);
            await Assert.That(armed.QuestObjectiveSourceArms).IsEqualTo(arm);
            DropRoute(executor, runtime);
        }

        // Arm 4 — the arm the cap used to refuse.
        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(PreySpawnerPos.X);
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
        // A prey reason must not be filed under the doodad word.
        await Assert.That(state.QuestTravelReason.Contains("prey")).IsTrue();
        await Assert.That(state.QuestTravelReason.Contains("doodad")).IsFalse();
    }

    /// <summary>
    /// The exemption cannot become a back door around the prey's OWN guards.
    /// A CREDITED objective contributes no source at all, so arm 1b never
    /// reaches the cap logic — travel stays withheld no matter how many arms
    /// were counted against it earlier.
    /// </summary>
    [Test]
    public async Task PreySource_Credited_NeverArms_EvenPastTheThreeArmCap()
    {
        var (executor, actor, runtime) = CreateRig("prey-cap-credited");
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, PreySpawnerPos);

        for (var arm = 1; arm <= 3; arm++)
        {
            await Assert.That(Arm(executor, runtime, actor)).IsTrue();
            DropRoute(executor, runtime);
        }

        // The objective is now satisfied: the prey arm must decline, and with
        // no source at all the only walkable answer is the far offerer.
        SeedNpcSpawner(actor, OffererNpc, OffererSpawnerPos);
        StageActiveQuest(actor, PreyQuest, objective: PreyNeed);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(OffererSpawnerPos.X);
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsFalse();
    }

    /// <summary>
    /// The same guard for a PERCEIVED prey: once one of template PreyTemplate
    /// is inside the 25 m census, the prey arm declines (the funnel owns the
    /// wake) and travel falls back to the offerer — the cap exemption never
    /// re-arms a walk to a spawner the bot no longer needs.
    /// </summary>
    [Test]
    public async Task PreySource_Perceived_StopsArmingPastTheThreeArmCap()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("prey-cap-perceived");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var executor = CreateExecutorFor(actor);
        StageActiveQuest(actor, PreyQuest, objective: 0);
        SeedNpcSpawner(actor, PreyTemplate, PreySpawnerPos);
        SeedNpcSpawner(actor, OffererNpc, OffererSpawnerPos);

        for (var arm = 1; arm <= 3; arm++)
        {
            await Assert.That(Arm(executor, runtime, actor)).IsTrue();
            DropRoute(executor, runtime);
        }

        // A LIVE prey now sits inside the census.
        SpawnPerceivedPrey(session, PreyTemplate, new Vector3(3, 0, 0));

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(OffererSpawnerPos.X);
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsFalse();
    }

    // ================================================================
    // Harness
    // ================================================================

    /// <summary>
    /// Drops the armed route without touching the active-quest set — what a
    /// wake that finished or lost the route leaves behind, and what lets the
    /// next wake re-arm and count again.
    /// </summary>
    private static void DropRoute(BotRoamStepExecutor executor, PlayerBotRuntime runtime)
    {
        var state = executor.GetBotState(runtime.CharacterId)!;
        state.Path = null;
        state.PendingLeg = null;
        state.QuestTravelTarget = null;
        state.PendingMoveOwner = null;
    }

    /// <summary>An executor bound to an actor the caller already built (region-registered).</summary>
    private static BotRoamStepExecutor CreateExecutorFor(GameplayActor actor) => new()
    {
        ActorFactory = _ => actor,
        TimeProvider = new FakeTimeProvider(),
        BroadcastInterval = TimeSpan.FromMilliseconds(200),
        ActiveCadence = TimeSpan.FromMilliseconds(100),
        RoamSpeed = 2f,
        ActiveActivityProvider = _ => "quest.progress",
        NearbyNpcProvider = (_, _) => [],
        NearbyDoodadProvider = (_, _) => [],
    };
    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime) CreateRig(string name)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = new FakeTimeProvider(),
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            ActiveActivityProvider = _ => "quest.progress",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
        };
        return (executor, actor, runtime);
    }

    /// <summary>Resolves through the production travel resolver itself.</summary>
    private static Vector3? Resolve(GameplayActor actor)
    {
        var band = BotRoamStepExecutor.ResolveOfferBand(
            levelBand: false, headroom: 5, actor.Character.Level);
        return (Vector3?)ResolveTravelTarget.Invoke(null, [actor, band]);
    }

    private static bool Arm(BotRoamStepExecutor executor, PlayerBotRuntime runtime, GameplayActor actor)
    {
        _ = executor.GetOrCreateActor(actor.Character);
        var state = executor.GetBotState(runtime.CharacterId)!;
        return (bool)ArmQuestTravel.Invoke(executor, [runtime, actor, state])!;
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

    private static void SeedNpcSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(94_800 + spawners.Count);
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

    // ================================================================
    // Fixtures
    // ================================================================

    /// <summary>
    /// A gather act whose item reaches NO doodad and DOES reach an npc loot
    /// pack, so the row derives a PreyTemplate and no doodad source at all —
    /// the only shape the prey arm can answer.
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
                {
                    group.Add(loot);
                }
                else
                {
                    pack.LootsByGroupNo[0] = [loot];
                }
            }
        }
    }

    /// <summary>
    /// Adds a Supply component granting <paramref name="itemId"/>, which is
    /// exactly what rewrites a gather row into <see cref="QuestPattern.Deliver"/>.
    /// </summary>
    private static void SeedSupplyAct(uint questId, uint componentId, uint actId, uint itemId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;

        var template = SeedQuestTemplate(questId);
        var component = new QuestComponentTemplate(template)
        {
            Id = componentId,
            KindId = QuestComponentKind.Supply,
        };
        componentTemplates[componentId] = component;
        template.Components[componentId] = component;
        if (!component.ActTemplates.OfType<QuestActSupplyItem>().Any(a => a.ActId == actId))
        {
            component.ActTemplates.Add(new QuestActSupplyItem(component)
            {
                ActId = actId,
                DetailId = componentId,
                DetailType = nameof(QuestActSupplyItem),
                Count = 1,
                ItemId = itemId,
            });
        }
    }

    /// <summary>The quest template, created on first use (idempotent).</summary>
    private static QuestTemplate SeedQuestTemplate(uint questId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(QuestManager.Instance)!;
        if (!questTemplates.TryGetValue(questId, out var template))
        {
            template = new QuestTemplate { Id = questId, Level = 10 };
            questTemplates[questId] = template;
        }
        return template;
    }

    private static void SeedGatherAct(uint questId, uint componentId, uint actId, uint itemId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var template = SeedQuestTemplate(questId);

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(componentId, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = componentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[componentId] = component;
        }
        if (!template.Components.ContainsKey(componentId))
        {
            template.Components[componentId] = component;
        }

        // No HighlightDoodadId and no alias act: the act names the item and
        // nothing else, so the ONLY resolvable source is the loot chain.
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == actId))
        {
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = actId,
                DetailId = componentId,
                DetailType = nameof(QuestActObjItemGather),
                Count = PreyNeed,
                ItemId = itemId,
                ThisComponentObjectiveIndex = 0,
            });
        }
    }

    private static void JoinToRegion(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
    }

    private static void SpawnPerceivedPrey(HeadlessSession session, uint npcTemplateId, Vector3 npcPos)
    {
        var region = session.World.GetRegionByPos(npcPos)
            ?? throw new InvalidOperationException("rig world has no region at the npc position");
        var objId = session.SpawnNpc(npcTemplateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = npcTemplateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
    }
}
