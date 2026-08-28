using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units.Static;

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
    /// would see nothing (same fix as the spike adapter's foxes).
    /// </summary>
    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        region?.AddObject(character); // InstanceId pre-set by CreateActor → registry no-op
        character.Region = region;
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
    /// Fail-closed control: canonical quest 64 (accept NPC 5931,
    /// Progress interaction act 372) remains outside this slice because no
    /// interaction-credit composition is wired.
    /// </summary>
    [Test]
    public async Task LevelingLoop_UnsupportedObjectiveType_FailsClosedNamingMissingPrimitive()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-gap");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);

        SpawnHubNpc(session, 5931, new Vector3(1, 0, 0)); // canonical offerer of 64

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 1,
            BandMax = 10,
            MaxLinks = 1
        });

        // FAIL-CLOSED: the interaction objective is not honestly achievable
        // with the current primitives, so the loop stops and names the gap.
        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage.StartsWith("OBJECTIVES")).IsTrue();
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.WrongDecision);
        await Assert.That(result.FailReason.Contains("QuestActObjInteraction")).IsTrue();
        await Assert.That(result.FailReason.Contains("missing world-interaction credit composition")).IsTrue();

        // No fake progress: the quest was accepted (real engine state) but
        // never advanced, turned in, or dropped.
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(64)).IsTrue();
        await Assert.That(result.TraceRecords.Count(r => r.Action == ActorActionType.AcceptQuest)).IsEqualTo(1);
        await Assert.That(result.TraceRecords.Any(r => r.Action is ActorActionType.TurnInQuest
            or ActorActionType.AutoTurnIn)).IsFalse();
        await Assert.That(character.Quests.HasQuestCompleted(64)).IsFalse();
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
