using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PB-002 second half — AUTONOMOUS LEVELING slice rig tests.
///
/// Proves a bot can run ONE REAL quest-chain segment by PERCEIVING offers
/// itself (discover → pick lowest-level offering in band → accept → pursue
/// objectives data-driven from the quest template → turn-in → re-discover),
/// never by following a scripted chain list:
///
///   - chain under test (canonical 1.2 compact.sqlite3): quest 254
///     (accept Npc 3515 → report Npc 3516, unit_reqs Level ≥ 2) chains into
///     quest 255 (start component 695: kind-31 CompleteQuestContext(254) +
///     Level ≥ 3; accept/report Npc 3516; ItemGather item 13713 ×1 from
///     highlight doodad 678). Completing 254 through the REAL engine is
///     what opens 255 — the loop must find it by its own next sweep.
///   - XP progression signal: level-based quest_supplies (L4 = 620 exp,
///     L5 = 680 exp @ ExpRate 1.0) land through the real turn-in path.
///   - fail-closed cases: no discoverable offerings in band → Starvation;
///     an objective type this slice cannot honestly pursue (canonical
///     quest 5650, QuestActObjTalkNpcGroup) stops the loop naming the
///     missing primitive — progress is NEVER faked.
///
/// Rig discipline: REAL QuestManager + UnitRequirementsGameData from
/// canonical data (pilot rig); fixture NPCs/doodads region-joined so the
/// contract's Observe (region graph) perceives them — the same convention
/// as AdventurerSpikeScenarioRigTests. compact.sqlite3 read-only.
/// </summary>
[NotInParallel]
public class LevelingLoopScenarioRigTests
{
    private uint _nextObjId = 0x71000;

    // Rig loot-func ids for the gather source (fixture range, collision-free).
    private const uint DollLootGroupId = 90_810;
    private const uint DollLootFuncId = 90_811;
    /// <summary>quest_act_supply_items 3847 — quest 5650 grants item 29054 on accept.</summary>
    private const uint Quest5650SupplyItemId = 29_054;

    private const uint Quest269Id = 269;
    private const uint Quest270Id = 270;
    private const uint Quest269NpcTemplateId = 5436;
    private const uint Quest270NpcTemplateId = 3526;
    private const uint Quest270DoodadTemplateId = 687;
    private const uint Quest270StartPhase = 161;
    private const uint Quest270UsedPhase = 304;
    private const uint Quest270TorchItemId = 3899;
    private const uint Quest270HayItemId = 3900;
    private static readonly uint[] Quest269TargetNpcTemplateIds = [3466, 3467, 3468, 3465, 3462];
    /// <summary>Canonical kill-accept quest: Start QuestActConAcceptNpcKill (NPC 4843),
    /// Progress MonsterHunt 4843 ×8, Reward AutoComplete+Copper — no Ready step,
    /// auto-completes (quest 329/1652 shape). Level 12 → 1110 exp quest supply.</summary>
    private const uint Quest1947Id = 1947;
    private const uint Quest1947KillNpcTemplateId = 4843;
    private const int Quest1947HuntCount = 8;
    private const uint Quest1947Exp = 1_110;
    /// <summary>Canonical NPC template with ZERO accept acts of any kind (no AcceptNpc /
    /// AcceptNpcKill / AcceptDoodad rows) — the fail-closed control target.</summary>
    private const uint NoOfferNpcTemplateId = 7669;

    /// <summary>Canonical component-only quest (census 2026-08-29): 6109
    /// "입관심사원 윈 처치" — engage NPC 14364 (level 52) auto-starts it on
    /// first aggro (EngageCombatGiveQuestId); Start = QuestActConAcceptComponent
    /// only (no AcceptNpc / AcceptNpcKill acts); Progress = MonsterHunt
    /// npc 14364 ×1; Reward = SupplyExp 125400 + AutoComplete. Start gate:
    /// unit_reqs Level ≥ 50.</summary>
    private const uint Quest6109Id = 6109;
    private const uint Quest6109EngageNpcTemplateId = 14364;
    private const int Quest6109Exp = 125_400;

    /// <summary>
    /// Temporarily installs canonical SkillManager/DoodadManager data while
    /// preserving the suite's process-wide singleton surface for other rigs.
    /// </summary>
    private sealed class CanonicalInteractionDataScope : IDisposable
    {
        private readonly SkillManager? _previousSkillManager = SkillManager.PeekInstance;
        private readonly DoodadManager? _previousDoodadManager = DoodadManager.PeekInstance;

        public CanonicalInteractionDataScope()
        {
            try
            {
                var skillManager = new SkillManager(
                    Mock.Of<IAnimationManager>().Object,
                    Mock.Of<IPlotManager>().Object);
                SetSingleton(typeof(Singleton<SkillManager>), skillManager);
                skillManager.Load();

                var objectIdManager = Mock.Of<IObjectIdManager>();
                objectIdManager.GetNextId().Returns(0x72000u);
                var housingManager = Mock.Of<IHousingManager>();
                var doodadManager = new DoodadManager(
                    objectIdManager.Object,
                    Mock.Of<IDoodadIdManager>().Object,
                    ItemManager.Instance,
                    new Lazy<IHousingManager>(() => housingManager.Object),
                    Mock.Of<ISusManager>().Object);
                SetSingleton(typeof(Singleton<DoodadManager>), doodadManager);
                doodadManager.Load();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            SetSingleton(typeof(Singleton<DoodadManager>), _previousDoodadManager);
            SetSingleton(typeof(Singleton<SkillManager>), _previousSkillManager);
        }
    }

    private static void SetSingleton(Type singletonBase, object? instance)
    {
        var field = singletonBase.GetField("s_instance",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    /// <summary>Region-joined fixture NPC so Observe's GetAround sees it.</summary>
    private uint SpawnHubNpc(HeadlessSession session, uint templateId, Vector3 position,
        uint engageCombatGiveQuestId = 0)
    {
        var npc = new Npc
        {
            ObjId = _nextObjId++,
            TemplateId = templateId,
            Hp = 100,
            MaxHp = 100,
            Template = new NpcTemplate { Id = templateId, Scale = 1f, EngageCombatGiveQuestId = engageCombatGiveQuestId }
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
    /// would see nothing (same fix as the spike adapter's foxes).
    /// </summary>
    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        region?.AddObject(character); // InstanceId pre-set by CreateActor → registry no-op
        character.Region = region;
    }

    private static void SeedQuest269To270Items()
    {
        GameplayActorTestRig.SeedItemTemplate(18_791); // quest 269 completion reward
        GameplayActorTestRig.SeedItemTemplate(Quest270TorchItemId);
        GameplayActorTestRig.SeedItemTemplate(Quest270HayItemId);
    }

    private static uint SpawnQuest270Doodad(HeadlessSession session, Vector3 position)
    {
        var objId = session.SpawnDoodadFromTemplate(Quest270DoodadTemplateId);
        if (objId == 0)
            throw new InvalidOperationException(
                $"canonical doodad template {Quest270DoodadTemplateId} was not loaded");

        session.World.GetDoodad(objId)!.Transform.Local.SetPosition(position);
        return objId;
    }

    private static QuestActObjInteraction GetQuest270Interaction()
    {
        return QuestManager.Instance.GetTemplate(Quest270Id)!
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(component => component.ActTemplates)
            .OfType<QuestActObjInteraction>()
            .Single();
    }

    /// <summary>
    /// Region-joined gather-source doodad: TEMPLATE id = the canonical Zeni
    /// doll (678 — highlight_doodad_id of gather act 373) so the loop's
    /// data-driven resolution matches; phase group carries a skill-less
    /// DoodadFuncLootItem granting item 13713 through the real inventory
    /// acquisition path (which fires the engine's own OnItemGather credit).
    /// </summary>
    private uint SpawnGatherSource(HeadlessSession session, Vector3 position)
    {
        GameplayActorTestRig.SeedItemTemplate(LevelingLoopScenario.SeedGatherItemTemplateId);
        GameplayActorTestRig.SeedDoodadLootInteraction(DollLootGroupId, DollLootFuncId,
            LevelingLoopScenario.SeedGatherItemTemplateId);

        var objId = session.SpawnDoodad(LevelingLoopScenario.SeedGatherSourceDoodadTemplateId);
        var doodad = session.World.GetDoodad(objId)!;
        doodad.FuncGroupId = DollLootGroupId;
        // DoFunc → HasOnlyGroupKindStart reads Template.FuncGroups; an empty
        // list keeps the one-shot loot doodad alive (Doodad.cs start-only rule).
        doodad.Template = new DoodadTemplate
        {
            Id = LevelingLoopScenario.SeedGatherSourceDoodadTemplateId,
            FuncGroups = []
        };
        doodad.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region != null)
        {
            region.AddObject(doodad);
            doodad.Region = region;
        }

        return objId;
    }

    private static uint SpawnWorkbenchDoodad(HeadlessSession session, uint templateId, Vector3 position)
    {
        var objId = session.SpawnDoodad(templateId);
        var doodad = session.World.GetDoodad(objId)!;
        doodad.Template = new DoodadTemplate
        {
            Id = templateId,
            FuncGroups = []
        };
        doodad.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region != null)
        {
            region.AddObject(doodad);
            doodad.Region = region;
        }

        return objId;
    }

    /// <summary>
    /// Deterministic evidence writer (m7-adventurer-spike convention): the
    /// PASSING main run emits its machine-readable audit trace + human
    /// evidence block into scorecard-explorations/generated/.
    /// </summary>
    private static void WriteTraceEvidence(LevelingLoopScenario.LoopRunResult result)
    {
        var repoRoot = RepoRoot();
        var generated = Path.Combine(repoRoot, "scorecard-explorations", "generated");
        Directory.CreateDirectory(generated);

        var sb = new System.Text.StringBuilder();
        foreach (var record in result.TraceRecords)
            sb.AppendLine(record.ToJson());
        File.WriteAllText(Path.Combine(generated, "leveling-loop-2026-08-25.jsonl"), sb.ToString());

        File.WriteAllText(Path.Combine(generated, "leveling-loop-2026-08-25.md"),
            "# Leveling loop — first autonomous quest-chain segment (2026-08-25)\n\n" +
            "> Generated by LevelingLoopScenarioRigTests (deterministic — no wall-clock).\n" +
            "> Machine-readable trace: `leveling-loop-2026-08-25.jsonl` (one ActorAuditRecord per line).\n\n" +
            result.Evidence());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    [Test]
    public async Task LevelingLoop_TwoChainedSolzreedQuests_CompletesUnprompted()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-loop");
        var character = session.Character;
        character.Level = 3; // 254 gate ≥2, 255 gate ≥3 (real unit_reqs rows)
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // World seed (ids cited in LevelingLoopScenario doc): offerer 3515,
        // hub 3516, two Zeni-doll sources. NOTHING ELSE tells the loop what
        // to do — no quest id crosses the scenario boundary below.
        SpawnHubNpc(session, LevelingLoopScenario.SeedOffererNpcTemplateId, new Vector3(2, 0, 0));
        SpawnHubNpc(session, LevelingLoopScenario.SeedHubNpcTemplateId, new Vector3(-2, 0, 0));
        SpawnGatherSource(session, new Vector3(1, 1, 0));
        SpawnGatherSource(session, new Vector3(-1, 1, 0));

        var result = LevelingLoopScenario.Run(character);

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");
        WriteTraceEvidence(result);

        // Two chained quests completed unprompted, discovered in band order.
        await Assert.That(result.Links.Count).IsEqualTo(2);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(LevelingLoopScenario.SeedQuestDeliveryId); // 254
        await Assert.That(result.Links[1].QuestId).IsEqualTo(LevelingLoopScenario.SeedQuestGatherId);   // 255
        await Assert.That(result.Links[0].AcceptorTemplateId).IsEqualTo(LevelingLoopScenario.SeedOffererNpcTemplateId);
        await Assert.That(result.Links[1].AcceptorTemplateId).IsEqualTo(LevelingLoopScenario.SeedHubNpcTemplateId);
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedQuestDeliveryId)).IsTrue();
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedQuestGatherId)).IsTrue();

        // XP progression signal — level-based quest supplies through the REAL
        // turn-in path: quest 254 (LEVEL 4) = 620 exp, 255 (LEVEL 5) = 680 exp.
        await Assert.That(result.Links[0].ExperienceAfter - result.Links[0].ExperienceBefore).IsEqualTo(620);
        await Assert.That(result.Links[1].ExperienceAfter - result.Links[1].ExperienceBefore).IsEqualTo(680);
        await Assert.That(result.Links[1].ExperienceBefore).IsGreaterThanOrEqualTo(result.Links[0].ExperienceAfter);

        // Audit-trace action sequence: perception PRECEDES every decision,
        // and each link runs accept → (pursuit) → turn-in in order.
        var trace = result.TraceRecords;
        var firstAccept254 = IndexOfFirst(trace, ActorActionType.AcceptQuest, LevelingLoopScenario.SeedQuestDeliveryId);
        var turnIn254 = IndexOfFirst(trace, ActorActionType.TurnInQuest, LevelingLoopScenario.SeedQuestDeliveryId);
        var firstAccept255 = IndexOfFirst(trace, ActorActionType.AcceptQuest, LevelingLoopScenario.SeedQuestGatherId);
        var turnIn255 = IndexOfFirst(trace, ActorActionType.TurnInQuest, LevelingLoopScenario.SeedQuestGatherId);
        var firstDiscover = IndexOfFirst(trace, ActorActionType.DiscoverQuests, 0);
        var interact = IndexOfFirst(trace, ActorActionType.InteractWith, 0);
        var advance255 = IndexOfFirst(trace, ActorActionType.AdvanceQuest, LevelingLoopScenario.SeedQuestGatherId);

        await Assert.That(firstDiscover).IsGreaterThanOrEqualTo(0);
        await Assert.That(firstAccept254).IsGreaterThan(firstDiscover);           // perceived BEFORE chosen
        await Assert.That(turnIn254).IsGreaterThan(firstAccept254);
        await Assert.That(firstAccept255).IsGreaterThan(turnIn254);               // re-discovery AFTER link 1 closed
        await Assert.That(interact).IsGreaterThan(firstAccept255);                // gather pursuit inside link 2
        await Assert.That(advance255).IsGreaterThan(interact);
        await Assert.That(turnIn255).IsGreaterThan(advance255);

        // The second link was found by PERCEPTION (a fresh sweep), not by
        // carry-over: a DiscoverQuests record sits between the two accepts.
        var discoversBetween =
            trace.Skip(turnIn254).Take(firstAccept255 - turnIn254)
                .Count(r => r.Action == ActorActionType.DiscoverQuests);
        await Assert.That(discoversBetween).IsGreaterThan(0);
    }

    /// <summary>
    /// E-ITEM-USE-1: canonical quest 252 "숲 되살리기" (accept NPC 7653,
    /// Progress item-use act 1600/detail 43, ItemId 7738 ×1, auto-complete).
    /// Acceptance supplies the canonical seed through the real quest path;
    /// UseItem then consumes it and credits OnItemUse through the engine.
    /// </summary>
    [Test]
    public async Task LevelingLoop_SeededItemUse_AutoCompletesThroughUseItem()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        // The replay rig supplies the canonical item-use skill 11596 and
        // its real effect chain; this slice binds the canonical quest item
        // to that loaded skill and still drives it via UseItem.
        GameplayActorTestRig.SeedItemTemplate(7738, 11596);
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-item-use");
        var character = session.Character;
        character.Level = 3; // quest 252 gate ≥3
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(251, true);
        SpawnHubNpc(session, 7653, new Vector3(1, 0, 0));

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 1,
            BandMax = 10,
            MaxLinks = 1
        });

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(252u);
        await Assert.That(result.Links[0].Pursuit).Contains(nameof(QuestActObjItemUse));
        await Assert.That(character.Quests!.HasQuestCompleted(252u)).IsTrue();
        await Assert.That(character.Inventory!.GetItemsCount(7738)).IsEqualTo(0);

        var trace = result.TraceRecords;
        var accept = IndexOfFirst(trace, ActorActionType.AcceptQuest, 252u);
        var use = FirstAtLeast(trace, ActorActionType.UseItem, accept + 1);
        await Assert.That(accept).IsGreaterThanOrEqualTo(0);
        await Assert.That(use).IsGreaterThan(accept);
    }

    /// <summary>
    /// Canonical PB-002 interaction chain: quest 269 unlocks quest 270, whose
    /// supplied torch/hay drive skill 11229 against rabbit-burrow doodad 687.
    /// The real skill effects consume both items, move phase 161 → 304, emit
    /// OnInteraction credit, and permit the ordinary report turn-in.
    /// </summary>
    [Test]
    public async Task LevelingLoop_Quest269To270_CompletesCanonicalInteraction()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();
        SeedQuest269To270Items();

        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-interaction");
        var character = session.Character;
        character.Level = 8;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        SpawnHubNpc(session, Quest269NpcTemplateId, new Vector3(-2f, 0f, 0f));
        SpawnHubNpc(session, Quest270NpcTemplateId, new Vector3(2f, 0f, 0f));
        for (var i = 0; i < Quest269TargetNpcTemplateIds.Length; i++)
        {
            SpawnHubNpc(session, Quest269TargetNpcTemplateIds[i],
                new Vector3(1f + i * 0.2f, -1f, 0f));
        }

        var doodadObjId = SpawnQuest270Doodad(session, new Vector3(1f, 1f, 0f));
        var doodad = session.World.GetDoodad(doodadObjId)!;
        await Assert.That(doodad.FuncGroupId).IsEqualTo(Quest270StartPhase);

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 8,
            BandMax = 8,
            MaxLinks = 2,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        }, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Select(link => link.QuestId))
            .IsEquivalentTo([Quest269Id, Quest270Id]);
        await Assert.That(result.Links[1].Pursuit).Contains(nameof(QuestActObjInteraction));
        await Assert.That(character.Quests!.HasQuestCompleted(Quest269Id)).IsTrue();
        await Assert.That(character.Quests.HasQuestCompleted(Quest270Id)).IsTrue();
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(Quest270Id)).IsFalse();
        await Assert.That(doodad.FuncGroupId).IsEqualTo(Quest270UsedPhase);
        await Assert.That(character.Inventory.GetItemsCount(Quest270TorchItemId)).IsEqualTo(0);
        await Assert.That(character.Inventory.GetItemsCount(Quest270HayItemId)).IsEqualTo(0);

        var trace = result.TraceRecords;
        var accept269 = IndexOfFirst(trace, ActorActionType.AcceptQuest, Quest269Id);
        var turnIn269 = IndexOfFirst(trace, ActorActionType.TurnInQuest, Quest269Id);
        var accept270 = IndexOfFirst(trace, ActorActionType.AcceptQuest, Quest270Id);
        var interact = IndexOfFirst(trace, ActorActionType.InteractWith, doodadObjId);
        var advance270 = IndexOfFirst(trace, ActorActionType.AdvanceQuest, Quest270Id);
        var turnIn270 = IndexOfFirst(trace, ActorActionType.TurnInQuest, Quest270Id);
        await Assert.That(turnIn269).IsGreaterThan(accept269);
        await Assert.That(accept270).IsGreaterThan(turnIn269);
        await Assert.That(interact).IsGreaterThan(accept270);
        await Assert.That(advance270).IsGreaterThan(interact);
        await Assert.That(turnIn270).IsGreaterThan(advance270);
    }

    [Test]
    public async Task InteractWith_Quest270WithoutSuppliedItems_FailsWithoutCredit()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();
        SeedQuest269To270Items();

        var (actor, session) = GameplayActorTestRig.CreateActor("pb-interaction-no-items");
        actor.Character.Level = 8;
        actor.Character.Quests!.SetCompletedQuestFlag(Quest269Id, true);
        var accept = actor.AcceptQuest(Quest270Id, QuestAcceptorType.Npc, Quest270NpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        actor.Character.Inventory.ConsumeItem(
            null, ItemTaskType.QuestRemoveSupplies, Quest270TorchItemId, 1, null);
        actor.Character.Inventory.ConsumeItem(
            null, ItemTaskType.QuestRemoveSupplies, Quest270HayItemId, 1, null);

        var doodadObjId = SpawnQuest270Doodad(session, Vector3.Zero);
        var interaction = GetQuest270Interaction();
        var quest = actor.Character.Quests.ActiveQuests[Quest270Id];

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(interaction.GetObjective(quest)).IsEqualTo(0);
        await Assert.That(session.World.GetDoodad(doodadObjId)!.FuncGroupId)
            .IsEqualTo(Quest270StartPhase);
    }

    [Test]
    public async Task InteractWith_Quest270WrongPhase_FailsWithoutCredit()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();
        SeedQuest269To270Items();

        var (actor, session) = GameplayActorTestRig.CreateActor("pb-interaction-wrong-phase");
        actor.Character.Level = 8;
        actor.Character.Quests!.SetCompletedQuestFlag(Quest269Id, true);
        var accept = actor.AcceptQuest(Quest270Id, QuestAcceptorType.Npc, Quest270NpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var doodadObjId = SpawnQuest270Doodad(session, Vector3.Zero);
        var doodad = session.World.GetDoodad(doodadObjId)!;
        doodad.FuncGroupId = Quest270UsedPhase;
        var interaction = GetQuest270Interaction();
        var quest = actor.Character.Quests.ActiveQuests[Quest270Id];

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(interaction.GetObjective(quest)).IsEqualTo(0);
        await Assert.That(actor.Character.Inventory.GetItemsCount(Quest270TorchItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.Inventory.GetItemsCount(Quest270HayItemId)).IsEqualTo(1);
    }

    [Test]
    public async Task LevelingLoop_NoDiscoverableOfferingsInBand_FailsStarvationWithReason()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-starve");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // A perceivable NPC whose ONLY canonically-offered quest (5650,
        // offered by Npc 1313 among others) sits behind a Level-50 gate —
        // discovery must hide it (fail-closed equality with AddQuest), so
        // the honest loop starves instead of inventing work.
        SpawnHubNpc(session, 1313, new Vector3(1, 0, 0));

        var result = LevelingLoopScenario.Run(character);

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PERCEIVE");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.Starvation);
        await Assert.That(result.FailReason.Contains("band [1..9]")).IsTrue();
        await Assert.That(result.FailReason.Contains("no discoverable quest offerings")).IsTrue();

        // Nothing was faked: no quest accepted, nothing turned in.
        await Assert.That(character.Quests!.ActiveQuests.Count).IsEqualTo(0);
        await Assert.That(result.TraceRecords.Any(r => r.Action == ActorActionType.AcceptQuest)).IsFalse();
    }

    /// <summary>
    /// Fail-closed control: canonical quest 5490 (accept NPC 13472, level 1,
    /// Progress QuestActObjItemGroupGather) remains outside this slice because no
    /// item-group source resolution primitive is composed.
    /// </summary>
    [Test]
    public async Task LevelingLoop_UnsupportedObjectiveType_FailsClosedNamingMissingPrimitive()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.EnsureSphereGameData();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-gap");
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        SpawnHubNpc(session, 13472, new Vector3(1, 0, 0));

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 1,
            BandMax = 1,
            MaxLinks = 1
        });

        if (!result.FailStage.StartsWith("OBJECTIVES", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"unexpected fail stage {result.FailStage} ({result.Failure}): {result.FailReason}");

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage.StartsWith("OBJECTIVES")).IsTrue();
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.WrongDecision);
        await Assert.That(result.FailReason.Contains(nameof(QuestActObjItemGroupGather))).IsTrue();
        await Assert.That(result.FailReason.Contains("missing item-group source resolution")).IsTrue();

        // No fake progress: the quest was accepted but never advanced,
        // turned in, or dropped.
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(5490)).IsTrue();
        await Assert.That(result.TraceRecords.Count(r => r.Action == ActorActionType.AcceptQuest)).IsEqualTo(1);
        await Assert.That(result.TraceRecords.Any(r => r.Action is ActorActionType.TurnInQuest
            or ActorActionType.AutoTurnIn)).IsFalse();
        await Assert.That(character.Quests.HasQuestCompleted(5490)).IsFalse();
    }

    /// <summary>
    /// E-SPHERE-1: composed SPHERE objective — canonical quest 1372
    /// (offered by NPC 2279 at Level 10, Progress = QuestActObjSphere act 9225 → sphere 457 / component 6499;
    /// report NPC 5789). The bot perceives the offer, accepts, approaches the sphere location,
    /// triggers OnEnterSphere, and turns in at NPC 5789 through the real engine path.
    /// </summary>
    [Test]
    public async Task LevelingLoop_Quest1372_CompletesSphereObjective()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.EnsureSphereGameData();

        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-sphere");
        var character = session.Character;
        character.Level = 10;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // Seed sphere location at (2, 0, 0) for component 6499 (quest 1372)
        GameplayActorTestRig.SeedQuestSphere(1372, 6499, new Vector3(2, 0, 0), 10f);

        SpawnHubNpc(session, 2279, new Vector3(1, 0, 0));  // Offerer NPC
        SpawnHubNpc(session, 5789, new Vector3(2, 0, 0));  // Report NPC

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 10,
            BandMax = 10,
            MaxLinks = 1
        });

        if (!result.Passed)
            throw new InvalidOperationException($"LevelingLoop failed at {result.FailStage}: {result.FailReason}");

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(character.Quests!.HasQuestCompleted(1372)).IsTrue();
    }

    /// <summary>
    /// E-CINEMA-1: composed CINEMA objective — canonical quest 6041
    /// (offered by NPC 14317 at Level 1, Progress = QuestActObjCinema act 35557 → cinema 154;
    /// auto-completes). The bot perceives the offer, accepts, plays the cinema, and auto-completes.
    /// </summary>
    [Test]
    public async Task LevelingLoop_Quest6041_CompletesCinemaObjective()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();

        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-cinema");
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // Mark completed flags on prerequisite and sibling quests offered by NPC 14317 so quest 6041 is chosen
        character.Quests!.SetCompletedQuestFlag(6040, true); // kind-31 prerequisite for 6041
        foreach (var siblingQuestId in new uint[] { 6039, 6043, 6044, 6046, 6054, 6055, 6056 })
            character.Quests!.SetCompletedQuestFlag(siblingQuestId, true);

        SpawnHubNpc(session, 14317, new Vector3(1, 0, 0)); // Offerer NPC

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 1,
            BandMax = 1,
            MaxLinks = 1
        });

        if (!result.Passed)
            throw new InvalidOperationException($"LevelingLoop failed at {result.FailStage}: {result.FailReason}");

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(character.Quests!.HasQuestCompleted(6041)).IsTrue();
    }

    /// <summary>
    /// E-CRAFT-1: composed CRAFT objective — canonical quest 6024
    /// (offered by NPC 1884 at Level 10, Progress = QuestActObjCraft act 35540 → craft 5462, workbench 559;
    /// auto-completes). The bot perceives the offer, accepts, crafts at the workbench, and auto-completes.
    /// </summary>
    [Test]
    public async Task LevelingLoop_Quest6024_CompletesCraftObjective()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedCraftSurface();

        // Seed materials and product for craft 5462: item 8343 x 3, item 8327 x 1 -> product 4052 x 3
        GameplayActorTestRig.SeedItemTemplate(8343);
        GameplayActorTestRig.SeedItemTemplate(8327);
        GameplayActorTestRig.SeedItemTemplate(4052);

        // Populate craft 5462 in CraftManager
        var crafts = (Dictionary<uint, AAEmu.Game.Models.Game.Crafts.Craft>)GameplayActorTestRig.GetField(CraftManager.Instance, "_crafts");
        crafts[5462] = new AAEmu.Game.Models.Game.Crafts.Craft
        {
            Id = 5462,
            SkillId = GameplayActorTestRig.CraftTestSkillId,
            ReqDoodadId = 559,
            ActabilityLimit = 0,
            CraftMaterials = [
                new AAEmu.Game.Models.Game.Crafts.CraftMaterial { ItemId = 8343, Amount = 3 },
                new AAEmu.Game.Models.Game.Crafts.CraftMaterial { ItemId = 8327, Amount = 1 }
            ],
            CraftProducts = [
                new AAEmu.Game.Models.Game.Crafts.CraftProduct { ItemId = 4052, Amount = 3, Rate = 100 }
            ]
        };

        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-craft");
        var character = session.Character;
        character.Level = 10;
        character.Hp = character.MaxHp;
        character.LaborPower = 100;
        JoinActorRegion(session);

        // Mark completed flags on prerequisite and sibling quests offered by NPC 1884 so quest 6024 is chosen
        character.Quests!.SetCompletedQuestFlag(1582, true); // kind-31 prerequisite for 6024
        character.Quests!.SetCompletedQuestFlag(1588, true);
        character.Quests!.SetCompletedQuestFlag(1638, true);

        // Grant required craft materials to inventory
        character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.QuestSupplyItems, 8343, 3);
        character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.QuestSupplyItems, 8327, 1);

        SpawnHubNpc(session, 1884, new Vector3(1, 0, 0)); // Offerer NPC

        // Spawn Masonry workbench doodad 559 in region
        SpawnWorkbenchDoodad(session, 559, new Vector3(2, 0, 0));

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 10,
            BandMax = 10,
            MaxLinks = 1
        });

        if (!result.Passed)
            throw new InvalidOperationException($"LevelingLoop failed at {result.FailStage}: {result.FailReason}");

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(character.Quests!.HasQuestCompleted(6024)).IsTrue();
    }

    /// <summary>
    /// E-TALK-1: composed GROUP TALK objective — canonical quest 5650
    /// "밤의 이야기꾼" (offered by NPC 1313, Level ≥ 50 + prereq 5552;
    /// Progress = QuestActObjTalkNpcGroup act 33756 → group 528 containing
    /// NPCs [13041, 13064, ...]; auto-completes). The bot perceives the offer,
    /// accepts, approaches perceived target NPC 13041, talks via IGameplayActor.Talk,
    /// and auto-completes unprompted through the real engine path.
    /// </summary>
    [Test]
    public async Task LevelingLoop_SeededGroupTalk_AutoCompletesWithTalkPursuit()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.SeedItemTemplate(Quest5650SupplyItemId); // 5650's accept-supply grant
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-group-talk");
        var character = session.Character;
        character.Level = 50; // quest 5650 gate ≥ 50
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(5552, true); // kind-31 prereq of 5650

        SpawnHubNpc(session, 1313, new Vector3(1, 0, 0));   // offerer
        SpawnHubNpc(session, 13041, new Vector3(3, 0, 0));  // member of group 528

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 40,
            BandMax = 60,
            MaxLinks = 1
        });

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(5650u);
        await Assert.That(result.Links[0].Pursuit).Contains(nameof(QuestActObjTalkNpcGroup));
        await Assert.That(character.Quests!.HasQuestCompleted(5650u)).IsTrue();
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(5650u)).IsFalse();

        // Audit subsequence: accept 5650 → Talk → AutoTurnIn/Advance
        var trace = result.TraceRecords;
        var accept5650 = IndexOfFirst(trace, ActorActionType.AcceptQuest, 5650u);
        var talk = FirstAtLeast(trace, ActorActionType.Talk, accept5650 + 1);
        await Assert.That(accept5650).IsGreaterThanOrEqualTo(0);
        await Assert.That(talk).IsGreaterThan(accept5650);
    }

    /// <summary>
    /// Rig kill seam (documented rig-faked damage, adventurer-spike
    /// convention): bare fixture NPCs carry no template/AI/spawner
    /// scaffolding for a full Npc.DoDie, so the killing blow is applied
    /// through the REAL QuestManager.DoOnMonsterHuntEvents entry point —
    /// the exact call DoDie makes for a character killer (group/zone/
    /// kill-accept fanout included). Real damage is the live stack's job.
    /// </summary>
    private sealed class RigKillSeam : LevelingLoopScenario.IKillCreditSeam
    {
        public bool TryKill(GameplayActor actor, Npc target)
        {
            if (target.Hp <= 0)
                return true; // real damage already downed it — nothing to fake
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, target);
            target.Hp = 0; // down — the alive filter excludes it from reselection
            return true;
        }
    }

    /// <summary>
    /// Region-joined quest board doodad (ConAcceptDoodad channel) —
    /// perceivable by Observe + DiscoverQuests like any notice board.
    /// </summary>
    private uint SpawnBoardDoodad(HeadlessSession session, uint doodadTemplateId, Vector3 position)
    {
        var objId = session.SpawnDoodad(doodadTemplateId);
        var doodad = session.World.GetDoodad(objId)!;
        // DoFunc → HasOnlyGroupKindStart reads Template.FuncGroups; an empty
        // list keeps the fixture doodad alive headless (Doodad.cs start-only rule).
        doodad.Template = new DoodadTemplate { Id = doodadTemplateId, FuncGroups = [] };
        doodad.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region != null)
        {
            region.AddObject(doodad);
            doodad.Region = region;
        }

        return objId;
    }

    /// <summary>Seeds one corpse's loot so the Loot contract action grants an item.</summary>
    private static void SeedCorpseLoot(HeadlessSession session, uint npcObjId)
    {
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId)!,
            (GameplayActorTestRig.TestItemTemplateId, 1));
    }

    /// <summary>
    /// E-HUNT-1: the composed GROUP hunt leg — canonical Solzreed bear cull,
    /// quest 329 "불곰을 조심해!" (accept at board doodad 5048, Level ≥ 2 +
    /// mother faction; Progress = MonsterGroupHunt act 150 → group 153 ×3
    /// over npcs
    /// 7674/7648; NO Ready component → auto-completes). The bot perceives
    /// the in-band offering itself at the BOARD, accepts, hunts the
    /// perceived bears DATA-DRIVEN from the act's monster group (membership
    /// via QuestManager.CheckGroupNpc; SetTarget → cast rotation → kill
    /// credit through the REAL DoOnMonsterHuntEvents via the rig seam →
    /// Loot each corpse), and completes unprompted with XP through the real
    /// completion path and the full audit subsequence.
    /// </summary>
    [Test]
    public async Task LevelingLoop_SeededGroupHuntBoard_CompletesAcceptHuntAutoCompleteUnprompted()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-group-hunt");
        var character = session.Character;
        character.Level = 2; // 329 gate ≥2 (real unit_reqs row)
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // World seed (ids cited in LevelingLoopScenario doc): the bear-cull
        // board and 16 fixtures across the two group-150 bear templates —
        // every kill credit lands on a DISTINCT alive npc (no respawner
        // scaffolding headless).
        SpawnBoardDoodad(session, LevelingLoopScenario.SeedGroupHuntBoardDoodadTemplateId, new Vector3(2, 0, 0));
        uint[] bearTemplates =
            [LevelingLoopScenario.SeedGroupHuntTargetNpcTemplateA, LevelingLoopScenario.SeedGroupHuntTargetNpcTemplateB];
        var bearObjIds = new List<uint>();
        for (var i = 0; i < 16; i++)
        {
            var position = new Vector3(1 + (i % 4) * 1.0f, -1 - (i / 4) * 1.0f, 0); // 1–4 m out
            var objId = SpawnHubNpc(session, bearTemplates[i % bearTemplates.Length], position);
            bearObjIds.Add(objId);
            SeedCorpseLoot(session, objId);
        }

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            MaxLinks = 1,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        }, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        // One link completed unprompted: discovered AT THE BOARD, accepted,
        // hunted, auto-completed.
        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(LevelingLoopScenario.SeedQuestGroupHuntId); // 329
        await Assert.That(result.Links[0].Pursuit).Contains(nameof(QuestActObjMonsterGroupHunt));
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedQuestGroupHuntId)).IsTrue();
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(LevelingLoopScenario.SeedQuestGroupHuntId)).IsFalse();

        // XP progression signal — LEVEL-4 quest supply through the REAL completion path = 620 exp.
        await Assert.That(result.Links[0].ExperienceAfter - result.Links[0].ExperienceBefore).IsEqualTo(620);

        // Audit-trace subsequence: perceive → accept → SetTarget → Cast →
        // Loot, in execution order. Auto-completion drops the quest from
        // ActiveQuests on the objective advance (the engine's own terminal
        // path), so no TurnIn record exists — nothing was faked.
        var trace = result.TraceRecords;
        var firstDiscover = IndexOfFirst(trace, ActorActionType.DiscoverQuests, 0);
        var accept329 = IndexOfFirst(trace, ActorActionType.AcceptQuest, LevelingLoopScenario.SeedQuestGroupHuntId);
        var firstTarget = FirstAtLeast(trace, ActorActionType.Target, accept329 + 1);
        var firstCast = FirstAtLeast(trace, ActorActionType.Cast, firstTarget + 1);
        var firstLoot = FirstAtLeast(trace, ActorActionType.Loot, firstCast + 1);

        await Assert.That(firstDiscover).IsGreaterThanOrEqualTo(0);
        await Assert.That(accept329).IsGreaterThan(firstDiscover); // perceived BEFORE chosen
        await Assert.That(firstTarget).IsGreaterThan(accept329);   // hostile selection from perception
        await Assert.That(firstCast).IsGreaterThan(firstTarget);   // SetTarget precedes the rotation
        await Assert.That(firstLoot).IsGreaterThan(firstCast);     // corpse looted after the kill
        // Kill credits flowed through the REAL event path exactly once per
        // bear (3 credits for group 153 ×3): one Loot attempt per distinct
        // corpse, with the real container-grant path proven Completed.
        await Assert.That(trace.Count(r => r.Action == ActorActionType.Loot)).IsEqualTo(3);
        await Assert.That(trace.Count(r => r.Action == ActorActionType.Loot && r.Result == ActorLifecycleState.Completed))
            .IsGreaterThanOrEqualTo(1);
    }



    /// <summary>
    /// E-HUNT-2: single-template MONSTER HUNT selection branch — canonical
    /// quest 1652 "난폭한 선돌 수호자 퇴치" (board doodad 8055, Level ≥ 3 +
    /// mother faction; Progress = MonsterHunt npc 7673 ×3; auto-completes).
    /// Targets match the ACT'S NpcId directly among perceived hostiles.
    /// </summary>
    [Test]
    public async Task LevelingLoop_SeededSingleTemplateHunt_AutoCompletesWithKillPursuit()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-board-hunt");
        var character = session.Character;
        character.Level = 3; // 1652 gate ≥3 (real unit_reqs row)
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // World seed: the notice board (doodad 8055) and 3 warden fixtures.
        SpawnBoardDoodad(session, LevelingLoopScenario.SeedBoardDoodadTemplateId, new Vector3(2, 0, 0));
        var wardenObjIds = new List<uint>();
        foreach (var position in new[] { new Vector3(4, -1, 0), new Vector3(-4, -1, 0), new Vector3(0, -5, 0) })
        {
            var objId = SpawnHubNpc(session, LevelingLoopScenario.SeedBoardHuntTargetNpcTemplateId, position);
            wardenObjIds.Add(objId);
            SeedCorpseLoot(session, objId);
        }

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            MaxLinks = 1,
            CastRotation = [GameplayActorTestRig.TestSkillId]
        }, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(LevelingLoopScenario.SeedQuestBoardHuntId); // 1652
        await Assert.That(result.Links[0].Pursuit).Contains(nameof(QuestActObjMonsterHunt));
        await Assert.That(character.Quests!.HasQuestCompleted(LevelingLoopScenario.SeedQuestBoardHuntId)).IsTrue();
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(LevelingLoopScenario.SeedQuestBoardHuntId)).IsFalse();

        // XP signal — LEVEL-5 quest supply = 680 exp through the real completion path.
        await Assert.That(result.Links[0].ExperienceAfter - result.Links[0].ExperienceBefore).IsEqualTo(680);

        // Audit subsequence: accept at the board → SetTarget → Cast → Loot per corpse.
        var trace = result.TraceRecords;
        var accept1652 = IndexOfFirst(trace, ActorActionType.AcceptQuest, LevelingLoopScenario.SeedQuestBoardHuntId);
        var firstTarget = FirstAtLeast(trace, ActorActionType.Target, accept1652 + 1);
        var firstCast = FirstAtLeast(trace, ActorActionType.Cast, firstTarget + 1);
        await Assert.That(accept1652).IsGreaterThanOrEqualTo(0);
        await Assert.That(firstTarget).IsGreaterThan(accept1652);
        await Assert.That(firstCast).IsGreaterThan(firstTarget);
        await Assert.That(trace.Count(r => r.Action == ActorActionType.Loot && r.Result == ActorLifecycleState.Completed))
            .IsGreaterThanOrEqualTo(3);
    }

    /// <summary>
    /// Self-discovery channel: quest offered through an item in the actor's inventory
    /// bag is perceived via DiscoverSelfQuests, accepted with QuestAcceptorType.Item,
    /// and completed through the leveling loop.
    /// </summary>
    [Test]
    public async Task LevelingLoop_SelfDiscoveryChannel_AcceptsAndCompletesQuestOfferedByItem()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        using var canonicalData = new CanonicalInteractionDataScope();
        GameplayActorTestRig.Seed();

        var (actor, session) = GameplayActorTestRig.CreateActor("pb-leveling-item-discovery");
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        character.Quests!.SetCompletedQuestFlag(6040, true);
        GameplayActorTestRig.SeedQuestItemOffer(6041, 26005, GameplayActorTestRig.DiscoverySelfItemTemplateId, level: 1);
        GameplayActorTestRig.GiveBagItem(actor, GameplayActorTestRig.DiscoverySelfItemTemplateId, 1);

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            MaxLinks = 1,
            BandMin = 1,
            BandMax = 1
        });

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(6041u);
        await Assert.That(character.Quests!.HasQuestCompleted(6041)).IsTrue();

        var trace = result.TraceRecords;
        var discoverSelf = IndexOfFirst(trace, ActorActionType.DiscoverSelfQuests, 0);
        var accept = IndexOfFirst(trace, ActorActionType.AcceptQuest, 6041);
        await Assert.That(discoverSelf).IsGreaterThanOrEqualTo(0);
        await Assert.That(accept).IsGreaterThan(discoverSelf);
    }

    /// <summary>
    /// KILL channel (census 2026-08-29): a perceived hostile NPC whose death
    /// auto-starts a quest (Start QuestActConAcceptNpcKill) is discovered
    /// with acceptor QuestAcceptorType.Kill, accepted, the hunt target
    /// pursued (SetTarget → cast rotation → kill credit through the REAL
    /// DoOnMonsterHuntEvents via the rig seam → Loot), and the quest
    /// auto-completes — the ordinary engine terminal. Canonical quest 1947
    /// (kill-accept + hunt NPC 4843 ×8, no Ready step → auto-complete);
    /// NPC 4843 carries NO other accept acts, so the offering is pure.
    /// </summary>
    [Test]
    public async Task LevelingLoop_KillDiscoveryChannel_AcceptsHuntsAndCompletesQuest1947()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-kill-discovery");
        var character = session.Character;
        character.Level = 12; // 1947 start gate ≥8 (real unit_reqs row)
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // World seed: the kill-acceptor NPC template itself (4843) — the
        // bot perceives the target AND hunts its own kind; every kill credit
        // lands on a DISTINCT alive npc (no respawner scaffolding headless).
        for (var i = 0; i < Quest1947HuntCount; i++)
        {
            var position = new Vector3(2 + (i % 3) * 1.0f, -1 - (i / 3) * 1.0f, 0); // 2–4 m out
            var objId = SpawnHubNpc(session, Quest1947KillNpcTemplateId, position);
            SeedCorpseLoot(session, objId);
        }

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            MaxLinks = 1,
            BandMin = 12,
            BandMax = 12,
            CastRotation = [GameplayActorTestRig.TestSkillId],
            MaxHuntRounds = 64
        }, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(Quest1947Id);
        await Assert.That(result.Links[0].Pursuit).Contains(nameof(QuestActObjMonsterHunt));
        await Assert.That(character.Quests!.HasQuestCompleted(Quest1947Id)).IsTrue();
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(Quest1947Id)).IsFalse();

        // XP progression signal — LEVEL-12 quest supply = 1110 exp through the real completion path.
        await Assert.That(result.Links[0].ExperienceAfter - result.Links[0].ExperienceBefore).IsEqualTo(Quest1947Exp);

        // Audit-trace subsequence: perceive → accept with the KILL acceptor →
        // SetTarget → Cast → Loot, in execution order. Auto-completion drops
        // the quest on the objective advance — no TurnIn record exists.
        var trace = result.TraceRecords;
        var accept1947 = IndexOfFirst(trace, ActorActionType.AcceptQuest, Quest1947Id);
        var firstTarget = FirstAtLeast(trace, ActorActionType.Target, accept1947 + 1);
        var firstCast = FirstAtLeast(trace, ActorActionType.Cast, firstTarget + 1);
        var firstLoot = FirstAtLeast(trace, ActorActionType.Loot, firstCast + 1);
        await Assert.That(accept1947).IsGreaterThanOrEqualTo(0);
        await Assert.That(firstTarget).IsGreaterThan(accept1947); // discovered BEFORE target selection
        await Assert.That(firstCast).IsGreaterThan(firstTarget);   // SetTarget precedes the rotation
        await Assert.That(firstLoot).IsGreaterThan(firstCast);     // corpse looted after the kill
        // Accepted through the KILL acceptor triple (not an NPC talk offer):
        // the audit detail renders the acceptor enum short name — "Kill/4843".
        await Assert.That(trace[accept1947].Detail).Contains("Kill/4843");
    }

    /// <summary>
    /// Fail-closed control (KILL channel): an NPC template with ZERO accept
    /// acts of any kind (7669 — no AcceptNpc / AcceptNpcKill / AcceptDoodad
    /// rows in compact.sqlite3) must be PERCEIVED but yield NO offering, so
    /// the honest loop starves instead of inventing a kill-gated quest —
    /// killing it must never start anything.
    /// </summary>
    [Test]
    public async Task LevelingLoop_KillChannel_NoOfferNpc_FailsStarvationWithoutFakeAccept()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-kill-control");
        var character = session.Character;
        character.Level = 12;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        SpawnHubNpc(session, NoOfferNpcTemplateId, new Vector3(1, 0, 0));

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            MaxLinks = 1,
            BandMin = 12,
            BandMax = 12
        }, new RigKillSeam());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PERCEIVE");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.Starvation);

        // Nothing was faked: no quest accepted, nothing turned in.
        await Assert.That(character.Quests!.ActiveQuests.Count).IsEqualTo(0);
        await Assert.That(result.TraceRecords.Any(r => r.Action == ActorActionType.AcceptQuest)).IsFalse();
    }

    /// <summary>
    /// COMPONENT channel (census 2026-08-29): the engine's engage-combat
    /// auto-start path (Unit.AddUnitAggro first-aggro block →
    /// CharacterQuests.AddQuestFromNpc → AddQuest with QuestAcceptorType.Npc
    /// + template id) starts a component-only quest with NO discoverable
    /// accept acts. The loop's fourth perception channel surfaces the
    /// auto-started quest from ActiveQuests and pursues + turns it in
    /// WITHOUT an explicit accept dispatch. Canonical quest 6109 (engage
    /// NPC 14364, MonsterHunt 14364 ×1, auto-complete).
    /// </summary>
    [Test]
    public async Task LevelingLoop_EngageCombatAutoStart_CompletesComponentOnlyQuest6109()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-engage-auto");
        var character = session.Character;
        character.Level = 50; // 6109 start gate ≥50 (real unit_reqs row)
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        // The level-50 level-up quest 6375 (gate: ExceptCompleteQuestContext
        // 6215) is discoverable at this level and would win the DECIDE step
        // over the auto-started 6109 — complete 6215 so 6375's gate closes
        // and the loop's only in-band quest is the auto-started 6109.
        character.Quests!.SetCompletedQuestFlag(6215, true);

        // World seed: the engage NPC itself (14364) — first aggro auto-starts
        // quest 6109 through the REAL engine path, then the loop hunts it.
        var npcObjId = SpawnHubNpc(session, Quest6109EngageNpcTemplateId, new Vector3(2, 0, 0),
            engageCombatGiveQuestId: Quest6109Id);
        SeedCorpseLoot(session, npcObjId);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.AddUnitAggro(AggroKind.Damage, character, 1); // the real engage
        await Assert.That(character.Quests!.HasQuest(Quest6109Id)).IsTrue();

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            MaxLinks = 1,
            BandMin = 51,
            BandMax = 51,
            CastRotation = [GameplayActorTestRig.TestSkillId],
            MaxHuntRounds = 64
        }, new RigKillSeam());

        if (!result.Passed)
            throw new InvalidOperationException(
                $"loop failed at {result.FailStage} ({result.Failure}): {result.FailReason}\n{result.Evidence()}");

        await Assert.That(result.Links.Count).IsEqualTo(1);
        await Assert.That(result.Links[0].QuestId).IsEqualTo(Quest6109Id);
        await Assert.That(result.Links[0].Pursuit).Contains(nameof(QuestActObjMonsterHunt));
        await Assert.That(character.Quests!.HasQuestCompleted(Quest6109Id)).IsTrue();
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(Quest6109Id)).IsFalse();

        // XP signal — the REAL SupplyExp act (125400) through the reward step.
        await Assert.That(result.Links[0].ExperienceAfter - result.Links[0].ExperienceBefore)
            .IsGreaterThanOrEqualTo(Quest6109Exp);

        // No explicit accept was dispatched — the quest was already active
        // (auto-started by the engine), so no AcceptQuest record exists.
        await Assert.That(result.TraceRecords.Any(r =>
            r.Action == ActorActionType.AcceptQuest && r.TargetId == Quest6109Id)).IsFalse();
        await Assert.That(result.Notes.Any(n => n.Contains("auto-started"))).IsTrue();
    }

    /// <summary>
    /// Direct engine-path proof of the component channel: first aggro on an
    /// EngageCombatGiveQuestId NPC auto-starts the quest with the Npc
    /// acceptor triple (QuestAcceptorType.Npc + template id), then the
    /// ordinary hunt credit + step machine completes it.
    /// </summary>
    [Test]
    public async Task EngageCombat_AutoStart_StartsComponentOnlyQuestWithNpcAcceptor()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-engage-direct");
        var character = session.Character;
        character.Level = 50;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        var npcObjId = SpawnHubNpc(session, Quest6109EngageNpcTemplateId, new Vector3(2, 0, 0),
            engageCombatGiveQuestId: Quest6109Id);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.AddUnitAggro(AggroKind.Damage, character, 1);

        // Auto-started with the Npc acceptor triple (not a discoverable offer).
        await Assert.That(character.Quests!.HasQuest(Quest6109Id)).IsTrue();
        var quest = character.Quests.ActiveQuests[Quest6109Id];
        await Assert.That(quest.QuestAcceptorType).IsEqualTo(QuestAcceptorType.Npc);
        await Assert.That(quest.AcceptorId).IsEqualTo(Quest6109EngageNpcTemplateId);

        // Hunt credit through the REAL event path, then the step machine:
        // first advance Progress→Reward, second runs the Reward step
        // (SupplyExp + AutoComplete → completed + dropped).
        QuestManager.Instance.DoOnMonsterHuntEvents(character, npc);
        _ = quest.RunCurrentStep();
        _ = quest.RunCurrentStep();
        await Assert.That(character.Quests.HasQuestCompleted(Quest6109Id)).IsTrue();
    }

    /// <summary>
    /// Fail-closed control (COMPONENT channel): an NPC with NO
    /// EngageCombatGiveQuestId (7669) must start NOTHING on first aggro —
    /// the auto-start gate is the template field, and the loop must not
    /// invent a quest where the engine starts none.
    /// </summary>
    [Test]
    public async Task EngageCombat_NoEngageQuestId_FailsClosedStartsNothing()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-engage-control");
        var character = session.Character;
        character.Level = 50;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        var npcObjId = SpawnHubNpc(session, NoOfferNpcTemplateId, new Vector3(1, 0, 0));
        var npc = session.World.GetNpc(npcObjId)!;
        npc.AddUnitAggro(AggroKind.Damage, character, 1);

        await Assert.That(character.Quests!.ActiveQuests.Count).IsEqualTo(0);
    }


    /// <summary>Index of the first record of the action type at or after start.</summary>
    private static int FirstAtLeast(IReadOnlyList<ActorAuditRecord> trace, ActorActionType action, int start)
    {
        for (var i = Math.Max(0, start); i < trace.Count; i++)
        {
            if (trace[i].Action == action)
                return i;
        }

        return -1;
    }

    /// <summary>Index of the first audit record matching action (+ target when targetId > 0).</summary>
    private static int IndexOfFirst(IReadOnlyList<ActorAuditRecord> trace, ActorActionType action, uint targetId)
    {
        for (var i = 0; i < trace.Count; i++)
        {
            if (trace[i].Action == action && (targetId == 0 || trace[i].TargetId == targetId))
                return i;
        }

        return -1;
    }
}
