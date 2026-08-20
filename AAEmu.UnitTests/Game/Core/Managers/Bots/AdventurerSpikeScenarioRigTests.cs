using System.Numerics;
using System.Reflection;
using System.Text;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 gating-spike rig tests (ROADMAP M7: "a scoped spike — one adventurer
/// clearing a short quest chain end-to-end — gates scheduling"): the
/// adventurer-spike scenario (quest 250 — Solzreed fox cull: accept at
/// notice-board doodad 5047, kill 3× fox npc 3492, loot, auto-complete)
/// runs end-to-end on the canonical rig through the M5 IGameplayActor
/// contract ONLY, and the run emits the MACHINE-READABLE trace:
/// scorecard-explorations/generated/m7-adventurer-spike.jsonl — one
/// ActorAuditRecord.ToJson() per line — plus a human evidence block in
/// scorecard-explorations/generated/m7-adventurer-spike.md.
///
/// Surface: the pilot rig seeds the REAL QuestManager (canonical
/// compact.sqlite3 — quest 250's real accept gates: mother-faction
/// kind-42/148, level 1) via M1M2ReplayScenarioRigTests.SeedReplaySurface,
/// then GameplayActorTestRig provides the character/world surface. The
/// kill seam is DOCUMENTED rig-faked damage: the rig runtime applies the
/// killing blow through the REAL QuestManager.DoOnMonsterHuntEvents entry
/// point (the exact call Npc.DoDie makes for a character killer) — bare
/// fixture NPCs carry no template/AI/spawner scaffolding for a full DoDie.
/// Real damage happens in the E2E (AdventurerSpikeE2eTests). Quest credit
/// flows through the real event surface either way.
///
/// Deterministic evidence (no wall-clock in the headers; the JSONL lines
/// carry the real server timestamps from the audit records — those ARE the
/// trace). H stays UNKNOWN — scripted evidence is proxy only.
/// </summary>
[NotInParallel]
public class AdventurerSpikeScenarioRigTests
{
    /// <summary>Fixture hunting ground: a short straight-line leg from the spawn (12 m).</summary>
    private static readonly Vector3 HuntingGround = new(12, 0, 0);

    /// <summary>First fixture fox objId (above the session's 1000+ range and the actor ids).</summary>
    private const uint FirstFoxObjId = 0x6000;

    /// <summary>
    /// Fixture world adapter: the notice board spawns on demand (the rig's
    /// SpawnDoodad); the foxes are spawned around the hunting ground up
    /// front (HeadlessSession.SpawnNpc dedupes by template id, so the three
    /// foxes need direct Npc creation) and placed into the region graph so
    /// the real Observe region query (WorldManager.GetAround) sees them.
    /// </summary>
    internal sealed class SpikeFixtureWorldAdapter : BotScenarioRunner.IScenarioWorldAdapter
    {
        private readonly HeadlessSession _session;
        private readonly int _foxCount;
        private readonly bool _foxesAlive;
        private readonly Vector3 _foxOffset;

        public SpikeFixtureWorldAdapter(HeadlessSession session, int foxCount, bool foxesAlive = true, Vector3 foxOffset = default)
        {
            _session = session;
            _foxCount = foxCount;
            _foxesAlive = foxesAlive;
            _foxOffset = foxOffset;
            SpawnFoxes();
        }

        private void SpawnFoxes()
        {
            for (var i = 0; i < _foxCount; i++)
            {
                var position = HuntingGround + _foxOffset + new Vector3((i % 2 == 0 ? 1.5f : -1.5f), (i - 1) * 1.5f, 0);
                var fox = new Npc
                {
                    ObjId = FirstFoxObjId + (uint)i,
                    TemplateId = AdventurerSpikeScenario.FoxNpcTemplateId,
                    Hp = _foxesAlive ? 100 : 0,
                    MaxHp = 100,
                    // Minimal template: Npc.AnimActionId reads
                    // Template.NpcPostureSets when the character's movement
                    // places it into the foxes' region (AddVisibleObject →
                    // SCUnitStatePacket ctor) — a template-less fox NREs
                    // there. Faction stays null: bare rig NPCs read
                    // attackable through BaseUnit.CanAttack (recon-verified).
                    Template = new NpcTemplate { Id = AdventurerSpikeScenario.FoxNpcTemplateId, Scale = 1f }
                };
                _session.World.AddObject(fox);
                fox.Transform.Local.SetPosition(position);
                // Region membership so the contract's Observe (region graph,
                // WorldManager.GetAround) sees the fixture foxes — the rig
                // world has a region grid but AddObject alone never joins it.
                var region = _session.World.GetRegionByPos(position);
                if (region != null)
                {
                    region.AddObject(fox);
                    fox.Region = region;
                }
            }
        }

        public uint ResolveNpcObjId(uint npcTemplateId)
            => npcTemplateId == AdventurerSpikeScenario.FoxNpcTemplateId && _foxCount > 0
                ? FirstFoxObjId
                : 0;

        public uint ResolveDoodadObjId(uint doodadTemplateId) => _session.SpawnDoodad(doodadTemplateId);
    }

    /// <summary>
    /// Fixture runtime: drives in-flight requests deterministically (no
    /// wall clock — actor.Tick), applies the documented synthetic kill
    /// through the REAL QuestManager.DoOnMonsterHuntEvents credit path (the
    /// exact call Npc.DoDie makes for a character killer), and seeds the
    /// corpse's loot container through the rig's real container surface.
    /// </summary>
    internal sealed class RigSpikeRuntime(bool seedLoot, bool healOnRecovery = true) : AdventurerSpikeScenario.ISpikeRuntime
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(20));
                Thread.Sleep(1);
            }
            return request;
        }

        public bool TargetDown(Npc target) => target.Hp <= 0;

        public bool EnsureKillCredit(GameplayActor actor, Npc target)
        {
            // Rig-faked damage (documented): bare fixture NPCs carry no
            // template/AI/spawner scaffolding for a full Npc.DoDie, so the
            // killing blow is applied through the REAL quest-credit entry
            // point DoDie uses for a character killer — group/zone/
            // kill-accept fanout included. Real damage happens in the E2E.
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, target);
            target.Hp = 0; // down — the alive filter excludes it from reselection
            return true;
        }

        public void PrepareLootCorpse(Npc corpse)
        {
            if (seedLoot)
                GameplayActorTestRig.SeedLootContainer(corpse, (GameplayActorTestRig.TestItemTemplateId, 1));
        }

        public void RecoveryTick(Character character)
        {
            // Rig-faked regen (documented): the rig has no game-loop regen
            // task, so each recovery tick restores 10% of max — the shape of
            // out-of-combat recovery. Real regen/potion healing happens live.
            // healOnRecovery=false drives the exhaustion (fail-closed) test.
            if (healOnRecovery && character.MaxHp > 0)
                character.Hp = Math.Min(character.MaxHp, character.Hp + Math.Max(1, character.MaxHp / 10));
            Thread.Sleep(1);
        }
    }

    private static AdventurerSpikeScenario.SpikeOptions RigOptions(bool lootOptional = true, uint[]? castRotation = null)
        => new()
        {
            // Canonical quest-250 ids — the rig runs the REAL quest template
            // (pilot-seeded QuestManager), only the combat skill is a fixture
            // (real damage skills need real game data — the E2E's surface).
            CastRotation = castRotation ?? [GameplayActorTestRig.TestSkillId],
            HuntingGround = HuntingGround,
            LootOptional = lootOptional
        };

    /// <summary>
    /// E-M7-1: the full chain completes green on the rig — accept → travel
    /// → hunt ×3 (Observe → target → cast through the contract; kill credit
    /// through real DoOnMonsterHuntEvents) → loot (seeded corpses) →
    /// auto-complete — with stage order + lifecycle completeness read from
    /// the trace records.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_FullChain_CompletesWithMachineReadableTrace()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7spike");
        session.Character.Level = 10;

        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 3),
            new RigSpikeRuntime(seedLoot: true), RigOptions());

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "M7 adventurer spike FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.FailStage, "no fail stage on a pass").IsEmpty();

        // Stage order: accept (approach + accept + observe) → travel →
        // three hunt rounds (cast, kill, loot, advance) → completion check.
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames[0]).IsEqualTo("ACCEPT-APPROACH");
        await Assert.That(stageNames).Contains("ACCEPT");
        await Assert.That(stageNames).Contains("TRAVEL");
        await Assert.That(stageNames.Count(s => s == "HUNT-KILL")).IsEqualTo(3);
        await Assert.That(stageNames.Count(s => s == "LOOT")).IsEqualTo(3);
        await Assert.That(stageNames.Last()).IsEqualTo("COMPLETE-OBSERVE");
        var acceptIndex = stageNames.IndexOf("ACCEPT");
        var travelIndex = stageNames.IndexOf("TRAVEL");
        var firstKillIndex = stageNames.IndexOf("HUNT-KILL");
        await Assert.That(acceptIndex < travelIndex && travelIndex < firstKillIndex).IsTrue();

        // Every request/transition/result is in the trace records, and
        // every record belongs to this actor.
        await Assert.That(result.TraceRecords.Count).IsGreaterThan(0);
        foreach (var record in result.TraceRecords)
        {
            await Assert.That(record.TraceId).IsNotEqualTo(Guid.Empty);
            await Assert.That(record.ActorId).IsEqualTo(session.Character.ObjId);
        }

        // The contract vocabulary the chain must contain: quest accept,
        // movement, targeting, casts, loots, observes.
        var actions = result.TraceRecords.Select(r => r.Action).ToList();
        await Assert.That(actions.Count(a => a == ActorActionType.AcceptQuest)).IsEqualTo(1);
        await Assert.That(actions.Count(a => a == ActorActionType.Move)).IsGreaterThan(0);
        await Assert.That(actions.Count(a => a == ActorActionType.Cast)).IsEqualTo(3);
        await Assert.That(actions.Count(a => a == ActorActionType.Loot)).IsEqualTo(3);
        await Assert.That(actions.Count(a => a == ActorActionType.Observe)).IsGreaterThan(0);

        // All criteria green (the evidence block carries the verdicts).
        var failed = result.Criteria.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail).ToList();
        await Assert.That(failed, "all spike criteria must pass: " + string.Join("; ", failed)).IsEmpty();

        // Quest 250 really completed: flag set, not active (engine state).
        await Assert.That(session.Character.Quests.HasQuestCompleted(AdventurerSpikeScenario.FoxQuestId)).IsTrue();
        await Assert.That(session.Character.Quests.HasQuest(AdventurerSpikeScenario.FoxQuestId)).IsFalse();
    }

    /// <summary>
    /// Seeded-but-unlearned fixture skill for the rotation-fallback test.
    /// 90010 — the 9000x fixture range is shared across suites (90001/90002
    /// rig, 90003 M53 cooldown); never reuse a sibling's id: this suite
    /// mutates the template's AbilityId, which would poison their casts.
    /// </summary>
    private const uint UnlearnedSkillId = 90010;

    /// <summary>
    /// E-M7-2: rotation fallback — the primary skill is seeded but never
    /// learned (the engine Rejects "not learned"), so the hunt executes
    /// the fallback cast and the chain still completes.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_PrimarySkillRejected_FallbackCastExecutes()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();

        // The primary must be genuinely UNLEARNED: the rig's 90001/90002
        // templates share AbilityId/AbilityLevel 0, which CharacterSkills.
        // IsVariantOfSkill reads as "known variant" — so the fallback probe
        // seeds its own template on an ability tree the actor does not have
        // (AbilityId 42 ≠ the actor's Fight/Magic/Will and ≠ 90001's 0).
        GameplayActorTestRig.SeedSkillTemplate(UnlearnedSkillId);
        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        skills[UnlearnedSkillId].AbilityId = (AbilityType)42;

        var (_, session) = GameplayActorTestRig.CreateActor("m7spike-rot");
        session.Character.Level = 10;

        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 3),
            new RigSpikeRuntime(seedLoot: true),
            RigOptions(castRotation: [UnlearnedSkillId, GameplayActorTestRig.TestSkillId]));

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "rotation-fallback run FAILED:\n" + result.Evidence()).IsTrue();

        var casts = result.TraceRecords.Where(r => r.Action == ActorActionType.Cast).ToList();
        // The skill id is in the record detail ("skill 90010 refused: ..." /
        // "skill 90001 cast succeeded") — the audit record carries no SkillId field.
        var rejectedPrimary = casts.Where(r => r.Detail?.Contains($"skill {UnlearnedSkillId}") == true
                                               && r.Result == ActorLifecycleState.Rejected).ToList();
        var completedFallback = casts.Where(r => r.Detail?.Contains($"skill {GameplayActorTestRig.TestSkillId}") == true
                                                 && r.Result == ActorLifecycleState.Completed).ToList();
        await Assert.That(rejectedPrimary.Count, "primary skill must be Rejected once per kill").IsEqualTo(3);
        await Assert.That(completedFallback.Count, "fallback cast must execute once per kill").IsEqualTo(3);
        await Assert.That(rejectedPrimary[0].Detail ?? "", "refusal reason").Contains("not learned");
    }

    /// <summary>
    /// E-M7-3: no hostile visible — the hunt loop's bounded re-observe
    /// retries exhaust and the failure classifies as WrongDecision (spec
    /// §17), never "bot got stuck".
    /// </summary>
    [Test]
    public async Task AdventurerSpike_NoHostileVisible_FailsWrongDecision()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7spike-nohostile");
        session.Character.Level = 10;

        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 0),
            new RigSpikeRuntime(seedLoot: true), RigOptions());

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "no-hostile run must fail").IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("HUNT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.WrongDecision);
        // Accept + travel still completed through the contract before the hunt failed.
        var actions = result.TraceRecords.Select(r => r.Action).ToList();
        await Assert.That(actions).Contains(ActorActionType.AcceptQuest);
        await Assert.That(actions).Contains(ActorActionType.Move);
    }

    /// <summary>
    /// E-M7-4: empty-corpse loot — quest 250's objectives are kills only,
    /// so a Rejected(empty) loot is tolerated and RECORDED as evidence (the
    /// run stays green; the loot criterion carries the refusal detail).
    /// </summary>
    [Test]
    public async Task AdventurerSpike_EmptyCorpseLoot_ToleratedAndRecorded()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7spike-loot");
        session.Character.Level = 10;

        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 3),
            new RigSpikeRuntime(seedLoot: false), // corpses stay empty
            RigOptions(lootOptional: true));

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "empty-corpse run FAILED:\n" + result.Evidence()).IsTrue();

        var loots = result.TraceRecords.Where(r => r.Action == ActorActionType.Loot).ToList();
        await Assert.That(loots.Count).IsEqualTo(3);
        foreach (var loot in loots)
        {
            await Assert.That(loot.Result).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(loot.Detail ?? "", "empty-corpse refusal").Contains("nothing to loot");
        }

        // The tolerated refusals are recorded in the loot criterion detail.
        var lootCriterion = result.Criteria.FirstOrDefault(c => c.Name == "loot-recorded");
        await Assert.That(lootCriterion, "loot criterion must exist").IsNotNull();
        await Assert.That(lootCriterion!.Detail).Contains("Rejected");
    }

    private static bool s_evidenceInitialized;
    private static readonly object s_evidenceLock = new();

    /// <summary>
    /// Writes the machine-readable JSONL trace (one audit record per line —
    /// the M5 {trace_id, actor_id, action, target_id, requested_at,
    /// started_at, completed_at, result, state_changes} shape) plus the
    /// human evidence block. Same convention as
    /// M53CoreSurfaceExitScenarioRigTests.WriteTraceEvidence.
    /// </summary>
    internal static void WriteTraceEvidence(BotScenarioRunner.ScenarioRunResult result)
    {
        lock (s_evidenceLock)
        {
            var repoRoot = RepoRoot();
            var jsonlPath = Path.Combine(repoRoot, "scorecard-explorations", "generated", "m7-adventurer-spike.jsonl");
            var mdPath = Path.Combine(repoRoot, "scorecard-explorations", "generated", "m7-adventurer-spike.md");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);

            // Machine-readable trace: one JSON object per request line.
            var sb = new StringBuilder();
            foreach (var record in result.TraceRecords)
                sb.AppendLine(record.ToJson());
            File.WriteAllText(jsonlPath, sb.ToString());

            // Human evidence block.
            var md = new StringBuilder();
            if (!s_evidenceInitialized)
            {
                s_evidenceInitialized = true;
                if (File.Exists(mdPath))
                    File.Delete(mdPath);
                md.AppendLine("# M7 adventurer spike — one bot clears the Solzreed fox cull (quest 250)");
                md.AppendLine();
                md.AppendLine("> Generated by AdventurerSpikeScenarioRigTests (deterministic — no wall-clock).");
                md.AppendLine("> Machine-readable trace: `m7-adventurer-spike.jsonl` (one ActorAuditRecord per line).");
                md.AppendLine("> Chain: accept (doodad 5047) → travel → hunt 3× fox (npc 3492: Observe → target →");
                md.AppendLine("> cast rotation) → loot → auto-complete — through the M5 IGameplayActor contract only.");
                md.AppendLine("> Rig kill = documented rig-faked damage through REAL DoOnMonsterHuntEvents;");
                md.AppendLine("> real damage happens in the E2E. H stays UNKNOWN — proxy/bot-functional evidence only.");
                md.AppendLine();
            }
            md.AppendLine("```");
            md.AppendLine(result.Evidence());
            md.AppendLine("```");
            md.AppendLine();
            File.AppendAllText(mdPath, md.ToString());
            Console.WriteLine("m7 adventurer spike trace written to " + jsonlPath);
        }
    }

    /// <summary>
    /// E-M7-3 (Adventurer v1 heal/retreat): a bot wounded below the sustain
    /// threshold BEFORE engaging retreats from the threat, recovers (rig
    /// regen fake — 10%/tick), and re-engages to complete the chain. The
    /// SUSTAIN-RETREAT stage must precede the first kill.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_Sustain_BelowThreshold_RetreatsRecoversReengages()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7sustain");
        session.Character.Level = 10;
        session.Character.Hp = 30; // 30% < the 0.35 sustain threshold

        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 3),
            new RigSpikeRuntime(seedLoot: true), RigOptions());

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "sustain spike FAILED:\n" + result.Evidence()).IsTrue();
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames).Contains("SUSTAIN-RETREAT");
        await Assert.That(stageNames.IndexOf("SUSTAIN-RETREAT") < stageNames.IndexOf("HUNT-KILL")).IsTrue();
        await Assert.That(session.Character.Hp).IsGreaterThan(30); // recovered mid-run
        await Assert.That(session.Character.Quests.HasQuestCompleted(AdventurerSpikeScenario.FoxQuestId)).IsTrue();
    }

    /// <summary>
    /// E-M7-4: when a heal item is configured AND bagged, recovery attempts
    /// it through the real UseItem contract path (SUSTAIN-HEAL stage; the
    /// fixture item is not a potion — the use may legitimately Reject, the
    /// contract attempt is the assertion) and regen remains the fallback.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_Sustain_HealItem_AttemptedThroughContractWhenBagged()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7sustainheal");
        session.Character.Level = 10;
        session.Character.Hp = 30;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);

        var options = RigOptions() with { HealItemTemplateId = GameplayActorTestRig.TestItemTemplateId };
        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 3),
            new RigSpikeRuntime(seedLoot: true), options);

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "sustain-heal spike FAILED:\n" + result.Evidence()).IsTrue();
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames).Contains("SUSTAIN-RETREAT");
        await Assert.That(stageNames).Contains("SUSTAIN-HEAL");
    }

    /// <summary>
    /// E-M7-5: recovery that never reaches the resume threshold fails the
    /// run CLOSED — SUSTAIN stage, Starvation classification (never
    /// "bot got stuck"), no fake completion.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_Sustain_ExhaustedRecovery_FailsClosedStarvation()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7sustainfail");
        session.Character.Level = 10;
        session.Character.Hp = 10; // 10% — never recovers with healing off

        var options = RigOptions() with { SustainMaxRounds = 3 };
        var result = AdventurerSpikeScenario.Run(
            session.Character, new SpikeFixtureWorldAdapter(session, foxCount: 3),
            new RigSpikeRuntime(seedLoot: true, healOnRecovery: false), options);

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("SUSTAIN");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.Starvation);
        await Assert.That(session.Character.Quests.HasQuestCompleted(AdventurerSpikeScenario.FoxQuestId)).IsFalse();
    }

    /// <summary>
    /// Position-recording runtime wrapper (distance-maintenance evidence):
    /// delegates to the rig runtime and snapshots the character position
    /// after every driven Move leg, so tests can assert WHERE a range leg
    /// actually stopped (band edge vs on top of the unit).
    /// </summary>
    internal sealed class PositionRecordingRuntime(AdventurerSpikeScenario.ISpikeRuntime inner) : AdventurerSpikeScenario.ISpikeRuntime
    {
        /// <summary>(target id, destination, end position) per driven Move leg.</summary>
        public List<(uint TargetId, Vector3? Destination, Vector3 End)> Moves { get; } = [];

        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
        {
            var driven = inner.Drive(actor, request, maxWait);
            if (request.Action == ActorActionType.Move)
                Moves.Add((request.TargetId, request.Destination, actor.Character.Transform.World.Position));
            return driven;
        }

        public bool TargetDown(Npc target) => inner.TargetDown(target);
        public bool EnsureKillCredit(GameplayActor actor, Npc target) => inner.EnsureKillCredit(actor, target);
        public void PrepareLootCorpse(Npc corpse) => inner.PrepareLootCorpse(corpse);
        public void RecoveryTick(Character character) => inner.RecoveryTick(character);
    }

    /// <summary>
    /// E-M7-6: ranged standoff back-off — with StandoffMin 5 / EngageRange 8
    /// and no hunting ground (travel = MoveToUnit straight onto fox 0), the
    /// bot lands inside the minimum band and must BACK OFF to the band edge
    /// before casting: exactly one non-unit Move leg (the back-off) ends
    /// ~7.5 m (EngageRange − 0.5 arrival slack, ± the 0.5 m arrival radius)
    /// from the fox, and HUNT-RANGE-BACK precedes the first kill.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_RangeKeep_TooClose_BacksOffToBandEdge()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7rangeback");
        session.Character.Level = 10;
        // Full vitals: MaxHp computes from Level via FormulaManager (~600 at
        // 10) while CreateActor seeds Hp=100 — the sustain loop would fire
        // mid-run and its retreat would corrupt the range geometry.
        session.Character.Hp = session.Character.MaxHp;

        var adapter = new SpikeFixtureWorldAdapter(session, foxCount: 3);
        var runtime = new PositionRecordingRuntime(new RigSpikeRuntime(seedLoot: true));
        var options = RigOptions() with { HuntingGround = null, StandoffMin = 5f, EngageRange = 8f };
        var result = AdventurerSpikeScenario.Run(session.Character, adapter, runtime, options);

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "range-back spike FAILED:\n" + result.Evidence()).IsTrue();
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames).Contains("HUNT-RANGE-BACK");
        await Assert.That(stageNames.IndexOf("HUNT-RANGE-BACK") < stageNames.IndexOf("HUNT-KILL")).IsTrue();

        // The back-off is the leg right after travel (MoveToUnit onto fox
        // 0: TargetId = fox, Destination null). It must be a band-point
        // MoveTo ending [7.5 − 0.6, 7.5 + 0.6] from fox 0 — NOT on top of
        // the unit (a melee close-in would end within 0.5 m).
        var fox0 = session.World.GetNpc(FirstFoxObjId);
        var travelIndex = runtime.Moves.FindIndex(m => m.TargetId == FirstFoxObjId && m.Destination == null);
        await Assert.That(travelIndex, "travel leg (MoveToUnit fox 0) recorded").IsGreaterThanOrEqualTo(0);
        var backOff = runtime.Moves[travelIndex + 1];
        await Assert.That(backOff.TargetId, "back-off is a band-point MoveTo").IsEqualTo(0u);
        await Assert.That(backOff.Destination.HasValue, "back-off is a band-point MoveTo").IsTrue();
        var stopDistance = Vector3.Distance(backOff.End, fox0.Transform.World.Position);
        await Assert.That(stopDistance, "back-off must stop at the band edge").IsGreaterThan(6.9f);
        await Assert.That(stopDistance, "back-off must stop at the band edge").IsLessThan(8.1f);
    }

    /// <summary>
    /// E-M7-7: ranged close-in stops at the band edge — foxes spawned 20 m
    /// past the hunting ground, so after travel the nearest fox is ~18.6 m
    /// out (&gt; EngageRange 8). A melee loop (MoveToUnit) would land within
    /// 0.5 m of the unit; the ranged loop must close only to the band edge
    /// (~7.5 m). Asserted on the first post-travel Move leg's end position.
    /// </summary>
    [Test]
    public async Task AdventurerSpike_RangeKeep_TooFar_ClosesToBandEdgeNotUnit()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m7rangeclose");
        session.Character.Level = 10;
        session.Character.Hp = session.Character.MaxHp; // see E-M7-6 — full vitals keep sustain out of the geometry

        var adapter = new SpikeFixtureWorldAdapter(session, foxCount: 3, foxOffset: new Vector3(0, 20, 0));
        var runtime = new PositionRecordingRuntime(new RigSpikeRuntime(seedLoot: true));
        var options = RigOptions() with { StandoffMin = 5f, EngageRange = 8f };
        var result = AdventurerSpikeScenario.Run(session.Character, adapter, runtime, options);

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "range-close spike FAILED:\n" + result.Evidence()).IsTrue();
        var stageNames = result.Stages.Select(s => s.Stage).ToList();
        await Assert.That(stageNames).Contains("HUNT-CLOSE");
        await Assert.That(stageNames).DoesNotContain("HUNT-RANGE-BACK");

        // The leg right after travel (MoveTo the hunting ground) is the
        // ranged close-in: it must be a band-point MoveTo (Destination set —
        // a melee MoveToUnit carries none) ending [6.9, 8.1] m from fox 0 —
        // a MoveToUnit close-in would end within 0.5 m of the unit.
        var fox0 = session.World.GetNpc(FirstFoxObjId);
        var travelIndex = runtime.Moves.FindIndex(m => m.Destination == HuntingGround);
        await Assert.That(travelIndex, "travel leg (MoveTo hunting ground) recorded").IsGreaterThanOrEqualTo(0);
        var closeIn = runtime.Moves[travelIndex + 1];
        await Assert.That(closeIn.Destination.HasValue, "ranged close-in is a band-point MoveTo, not MoveToUnit onto the fox").IsTrue();
        var stopDistance = Vector3.Distance(closeIn.End, fox0.Transform.World.Position);
        await Assert.That(stopDistance, "close-in must stop at the band edge").IsGreaterThan(6.9f);
        await Assert.That(stopDistance, "close-in must stop at the band edge").IsLessThan(8.1f);
    }

    /// <summary>Worktree-tolerant repo root (M53 pattern; accepts .git dir OR file).</summary>
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
}
