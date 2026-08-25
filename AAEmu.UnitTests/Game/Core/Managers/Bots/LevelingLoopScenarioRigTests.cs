using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.NPChar;
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

    [Test]
    public async Task LevelingLoop_UnsupportedObjectiveType_FailsClosedNamingMissingPrimitive()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.SeedItemTemplate(Quest5650SupplyItemId); // 5650's accept-supply grant
        var (_, session) = GameplayActorTestRig.CreateActor("pb-leveling-gap");
        var character = session.Character;
        character.Level = 50; // quest 5650 start-component gate [50..∞]
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        character.Quests!.SetCompletedQuestFlag(5552, true); // kind-31 prereq of 5650

        SpawnHubNpc(session, 1313, new Vector3(1, 0, 0)); // canonical offerer of 5650

        var result = LevelingLoopScenario.Run(character, new LevelingLoopScenario.LoopOptions
        {
            BandMin = 40,
            BandMax = 60,
            MaxLinks = 1
        });

        // FAIL-CLOSED: the talk-group objective is not honestly achievable
        // with the current primitives, so the loop stops and NAMES the gap.
        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage.StartsWith("OBJECTIVES")).IsTrue();
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.WrongDecision);
        await Assert.That(result.FailReason.Contains("QuestActObjTalkNpcGroup")).IsTrue();
        await Assert.That(result.FailReason.Contains("missing talk-credit contract action")).IsTrue();

        // No fake progress: the quest was accepted (real engine state) but
        // never advanced, turned in, or dropped.
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(5650)).IsTrue();
        await Assert.That(result.TraceRecords.Count(r => r.Action == ActorActionType.AcceptQuest)).IsEqualTo(1);
        await Assert.That(result.TraceRecords.Any(r => r.Action is ActorActionType.TurnInQuest
            or ActorActionType.AutoTurnIn)).IsFalse();
        await Assert.That(character.Quests.HasQuestCompleted(5650)).IsFalse();
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
