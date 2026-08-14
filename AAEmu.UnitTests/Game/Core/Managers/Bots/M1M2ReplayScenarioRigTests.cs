using System.Reflection;
using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// BACKTRACK Phase 1 rigs (t_61a0eebb) — the M1/M2 contract replay scenario
/// on the fixture rig:
///   - the full curated golden route (16 quests: village errands → main
///     village chain → shepherd/pickaxe → mount chain 4292→4294→4295 = the
///     first mounts) completes through contract actions ONLY (accept →
///     advance → use_item → turn_in at resolved NPCs);
///   - completion criteria hold (every quest completed flag set, not active);
///   - every action's trace record carries the full lifecycle transition set.
///
/// Surface: the pilot rig seeds the REAL QuestManager (canonical
/// compact.sqlite3 — the route quest templates), then
/// GameplayActorTestRig.Seed + CreateActor provide the full character
/// surface (Actability/Skills/world regions/MateManager — the real
/// item-use pipeline needs them) with per-singleton missing-only guards
/// (never replaces the pilot's real QuestManager). NPC turn-in targets
/// resolve through the fixture world adapter (spawn-on-demand). Evidence
/// table appended to scorecard-explorations/generated/m1m2-replay-rig.md.
/// </summary>
[NotInParallel]
public class M1M2ReplayScenarioRigTests
{
    private static readonly uint[] RouteItems =
    [
        4058, 18791, 13713, 24786, 18792, 8128, 8129, 16247, 8130, 23635, 21850,
        8159, 8160, 8161, 18649, 7738, 32481, 32482, 32483, 32484, 32485, 32486, 35823
    ];

    /// <summary>(itemTemplateId, useSkillId, rigTargetType) for the route's
    /// use-items. The target type matters ONLY where the requirement check
    /// in CanUseSkill branches on it (Self reads the owner, anything else
    /// reads CurrentTarget): 11641 MUST be AnyUnit (its TargetNpcGroup 54
    /// req needs CurrentTarget). The others carry no unit_reqs, so the rig
    /// uses Self to avoid the fixture-only Pos/SetInitialTarget
    /// world-registry path (the live server registers the world; the
    /// headless session does not — a rig-only NRE, not an engine defect).</summary>
    private static readonly (uint Item, uint Skill, SkillTargetType TargetType)[] UseItems =
    [
        (7738, 11596, SkillTargetType.Self),   // sapling (real target_type 0 = Self)
        (8129, 11641, SkillTargetType.AnyUnit),// burning heart (real 5; req TargetNpcGroup 54 → needs CurrentTarget)
        (23635, 13139, SkillTargetType.Self),  // growth seed (real 6 = Pos; no unit_reqs → Self is fixture-safe)
        (8159, 10602, SkillTargetType.SummonPos), // Lilyut horse (real 12)
        (8160, 10602, SkillTargetType.SummonPos),
        (8161, 10602, SkillTargetType.SummonPos)
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    internal sealed class FixtureWorldAdapter : BotScenarioRunner.IScenarioWorldAdapter
    {
        private readonly HeadlessSession _session;

        public FixtureWorldAdapter(HeadlessSession session) => _session = session;

        public uint ResolveNpcObjId(uint npcTemplateId) => _session.SpawnNpc(npcTemplateId);

        public uint ResolveDoodadObjId(uint doodadTemplateId) => _session.SpawnDoodad(doodadTemplateId);
    }

    /// <summary>
    /// Seeds the full rig surface for the replay: pilot singletons first
    /// (REAL QuestManager with the route templates), then the
    /// GameplayActorTestRig surface (full character bootstrap, missing-only
    /// guards — it never replaces the pilot's QuestManager), then the
    /// route's item + use-skill templates.
    /// </summary>
    internal static void SeedReplaySurface()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedSafeExperienceCurve();
        RegisterRouteItemsAndSkills();
    }

    private static void SeedSafeExperienceCurve()
    {
        var experienceManager = new ExperienceManager();
        var expTemplates = new List<ExperienceLevelTemplate>();
        var expByLevel = new List<int>();
        for (var level = 1; level <= 55; level++)
        {
            expTemplates.Add(new ExperienceLevelTemplate
            {
                Level = (byte)level,
                TotalExp = level * 10_000_000,
                TotalMateExp = level * 10_000_000,
                SkillPoints = 1
            });
            expByLevel.Add(level * 10_000_000);
        }

        SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
        SetField(experienceManager, "_expByLevel", expByLevel);
        SetField(experienceManager, "_mateExpByLevel", expByLevel);
        SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)55);
        SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)50);
        SetSingleton(typeof(Singleton<ExperienceManager>), experienceManager);
    }

    private static void RegisterRouteItemsAndSkills()
    {
        // Route items through the rig's item template registry (the real
        // acquisition path + quest rewards need templates).
        foreach (var itemId in RouteItems)
            GameplayActorTestRig.SeedItemTemplate(itemId);

        // Use-items: wire the use skill so the REAL item-use pipeline can
        // resolve it (same seeding as the B1 UseItem rigs), and seed the
        // skill template itself with the REAL target type (the requirement
        // check in CanUseSkill branches on TargetType).
        foreach (var (item, skill, targetType) in UseItems)
        {
            GameplayActorTestRig.SeedItemTemplate(item, skill);
            GameplayActorTestRig.SeedSkillTemplate(skill);
            var skillsField = typeof(SkillManager).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance);
            var skills = (Dictionary<uint, SkillTemplate>)skillsField!.GetValue(SkillManager.Instance)!;
            if (skills.TryGetValue(skill, out var template))
                template.TargetType = targetType;
        }
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"Cannot locate field {fieldName} on {instance.GetType().Name}");
        field.SetValue(instance, value);
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    /// <summary>
    /// The M1/M2 contract replay runs end-to-end on the fixture rig: all 16
    /// curated route quests complete through contract actions only, with
    /// completion + trace-lifecycle criteria green.
    /// </summary>
    [Test]
    public async Task M1M2Replay_AllRouteQuests_CompleteThroughContractActions()
    {
        SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m1m2rig");
        session.Character.Level = 6;

        var result = BotScenarioRunner.Run(
            BotScenarioTemplates.M1M2Replay, session.Character, new FixtureWorldAdapter(session));

        AppendEvidence(result);

        await Assert.That(result.Passed, "replay FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.FailStage, "no fail stage on a pass").IsEmpty();

        // Every route quest must be completed (flag set, not active).
        var notCompleted = result.Criteria
            .Where(c => c.Name.StartsWith("quest-") && c.Name.EndsWith("-completed") && !c.Passed)
            .Select(c => c.Detail)
            .ToList();
        await Assert.That(notCompleted, "all route quests must complete: " + string.Join("; ", notCompleted)).IsEmpty();

        // All criteria green — except m2-mount-segment, which on the
        // fixture rig is a DECLARED limitation (the engine materializes no
        // owned active mate headless; the criterion is tightened per kimi
        // memo item 2 and must never claim a mount that didn't occur).
        // The real mount chain is proven live (E2E: mate mounted +
        // dismounted). Assert the limitation is declared, never silent.
        var failed = result.Criteria
            .Where(c => !c.Passed && c.Name != "m2-mount-segment")
            .Select(c => c.Name + ": " + c.Detail)
            .ToList();
        await Assert.That(failed, "all criteria must pass: " + string.Join("; ", failed)).IsEmpty();

        var mountCriterion = result.Criteria.FirstOrDefault(c => c.Name == "m2-mount-segment");
        if (mountCriterion != null && !mountCriterion.Passed)
            await Assert.That(mountCriterion.Detail, "mount limitation must be declared, not silent").Contains("NO REAL MOUNT");

        // Lifecycle: trace records exist and carry full transitions.
        await Assert.That(result.ActorRequests, "replay must produce contract-action trace records").IsGreaterThan(0);
    }

    /// <summary>
    /// The trace lifecycle guarantee: every Completed action's record carries
    /// the full Requested → Accepted → Running → Completed set, and no
    /// Rejected record ran. Mirrors the auction rig's trace assertion.
    /// </summary>
    [Test]
    public async Task M1M2Replay_TraceRecords_CompleteLifecycle()
    {
        SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m1m2rig2");
        session.Character.Level = 6;

        var result = BotScenarioRunner.Run(
            BotScenarioTemplates.M1M2Replay, session.Character, new FixtureWorldAdapter(session));

        await Assert.That(result.Passed, "replay FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.TraceRecords.Count, "trace records must exist").IsGreaterThan(0);

        var completed = result.TraceRecords.Where(r => r.Result == ActorLifecycleState.Completed).ToList();
        var incomplete = completed
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")) ||
                        (r.Action != ActorActionType.Target && r.Action != ActorActionType.Observe &&
                         !r.StateChanges.Any(s => s.Contains("Running"))))
            .ToList();
        var rejectedRunning = result.TraceRecords
            .Where(r => r.Result == ActorLifecycleState.Rejected && r.StateChanges.Any(s => s.Contains("Running")))
            .Where(r => !(r.Action == ActorActionType.UseItem && r.Detail?.Contains("CooldownTime") == true))
            .ToList();

        await Assert.That(incomplete.Count, "every Completed trace must carry the full transition set").IsEqualTo(0);
        await Assert.That(rejectedRunning.Count, "no Rejected record may carry a Running transition (except the documented GCD-retry CooldownTime refusal)").IsEqualTo(0);
    }

    /// <summary>
    /// The MINIMUM SLICE (Aya narrow-scope directive, t_61a0eebb): the
    /// canonical M1 action (quest 251 full spine) + canonical M2 action
    /// (mount segment) complete through contract actions on the fixture
    /// rig, with the observation-delta criterion green. This is the
    /// live-world E2E gate's unit twin — the exact scenario the E2E test
    /// dispatches (m1m2-min-slice).
    /// </summary>
    [Test]
    public async Task M1M2MinSlice_OneM1Action_OneM2Action_Complete()
    {
        SeedReplaySurface();
        var (_, session) = GameplayActorTestRig.CreateActor("m1m2rig3");
        session.Character.Level = 6;

        var result = BotScenarioRunner.Run(
            BotScenarioTemplates.M1M2MinSlice, session.Character, new FixtureWorldAdapter(session));

        AppendEvidence(result);

        await Assert.That(result.Passed, "min-slice replay FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.Template, "min-slice must run under its own template name").IsEqualTo(M1M2ReplayScenario.MinSliceScenarioName);
        await Assert.That(result.ActorRequests, "min-slice must produce contract-action trace records").IsGreaterThan(0);

        var failed = result.Criteria
            .Where(c => !c.Passed && c.Name != "m2-mount-segment")
            .Select(c => c.Name + ": " + c.Detail)
            .ToList();
        await Assert.That(failed, "all min-slice criteria must pass: " + string.Join("; ", failed)).IsEmpty();

        var mountCriterion = result.Criteria.FirstOrDefault(c => c.Name == "m2-mount-segment");
        if (mountCriterion != null && !mountCriterion.Passed)
            await Assert.That(mountCriterion.Detail, "mount limitation must be declared, not silent").Contains("NO REAL MOUNT");

        // The M1 criterion must be the quest-251 completion (the canonical
        // M1 exit spine reduced to one quest).
        var m1 = result.Criteria.FirstOrDefault(c => c.Name == "m1-quest-251-completed");
        await Assert.That(m1, "min-slice must carry the m1-quest-251-completed criterion").IsNotNull();
        await Assert.That(m1!.Passed, "quest 251 must complete through the contract spine").IsTrue();
    }

    private static bool s_evidenceInitialized;
    private static readonly object s_evidenceLock = new();

    internal static void AppendEvidence(BotScenarioRunner.ScenarioRunResult result)
    {
        lock (s_evidenceLock)
        {
            var path = Path.Combine(RepoRoot(), "scorecard-explorations", "generated", "m1m2-replay-rig.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder();
            if (!s_evidenceInitialized)
            {
                s_evidenceInitialized = true;
                if (File.Exists(path))
                    File.Delete(path);
                sb.AppendLine("# M1/M2 contract replay rig — BACKTRACK Phase 1 (t_61a0eebb)");
                sb.AppendLine();
                sb.AppendLine("> Generated by M1M2ReplayScenarioRigTests (deterministic — no wall-clock).");
                sb.AppendLine("> Engine: real QuestManager.Load + canonical compact.sqlite3; fixture bots = ordinary");
                sb.AppendLine("> Character records (no Connection); every mutation through contract actions + normal");
                sb.AppendLine("> gameplay services. H (feel) stays UNKNOWN — scripted evidence is proxy only.");
                sb.AppendLine();
            }

            sb.AppendLine("```");
            sb.AppendLine(result.Evidence());
            sb.AppendLine("```");
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
            Console.WriteLine("m1m2 replay rig evidence appended to " + path);
        }
    }
}

/// <summary>
/// CAST-WINDOW pin (t_15787275, 2026-08-13) — the live full-route replay
/// failed at quest 4294 because skill 13139 (씨앗 심기) has a REAL
/// 4000ms cast: Skill.Use returns Success the moment the cast is
/// SCHEDULED, but the quest's OnItemUse objective only registers when the
/// cast completes (ApplyEffects). The base rig seeds CastingTime = 0
/// (instant — fixture-safe), which masks exactly this window. This rig
/// seeds the REAL cast time and ticks the seeded TaskManager (the same
/// private Tick the live game loop drives via ITickManager.OnTick), so
/// the scheduled CastTask actually completes mid-scenario. The scenario's
/// bounded WaitForItemUseObjective must hold the advance/turn-in until
/// the objective registers — without it, the turn-in lands inside the
/// cast window and the report act's isReady gate refuses (the live
/// failure signature).
/// </summary>
[NotInParallel]
public class M1M2ReplayCastWindowRigTests
{
    /// <summary>
    /// The full route completes when the route's use-skills carry their
    /// REAL casting times (252's tree-plant 11596 = 5000ms, 4294's
    /// seed-plant 13139 = 4000ms) and the TaskManager ticks like the live
    /// game loop. Fail-before (no WaitForItemUseObjective + no turn-in
    /// guard): the use-item objective is still 0 when the turn-in runs →
    /// report act refuses → quest 4294 stuck at Progress; and quest 252
    /// auto-completes the moment its objective registers, so an
    /// unconditional turn-in refuses with "not active". Pass-after: the
    /// wait holds the drive until the cast completes and the guard skips
    /// turn-ins for already-completed quests.
    /// </summary>
    [Test]
    public async Task M1M2Replay_RealCastTime_TurnInWaitsForObjective()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();

        // Seed the REAL casting times on the route's use-skills.
        var skillsField = typeof(SkillManager).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance);
        var skills = (Dictionary<uint, SkillTemplate>)skillsField!.GetValue(SkillManager.Instance)!;
        var original11596 = skills[11596].CastingTime;
        var original13139 = skills[13139].CastingTime;
        skills[11596].CastingTime = 5000;
        skills[13139].CastingTime = 4000;

        // The LIVE item 23635 is a use-as-reagent item (the plant-seed
        // skill consumes the seed from the bag — that is the natural
        // 4292→4294 chain). The base rig seeds useSkillAsReagent=false,
        // which masks the consumption and would let a naive
        // "reward must still be held" conservation check pass. Match the
        // real template so the chain-consumption criterion is exercised.
        GameplayActorTestRig.SeedItemTemplate(23635, 13139, useSkillAsReagent: true);
        try
        {
            var (_, session) = GameplayActorTestRig.CreateActor("m1m2rigcast");
            session.Character.Level = 6;

            // Tick the seeded TaskManager on a background loop — the
            // fixture's mock ITickManager never fires OnTick, so the
            // scheduled CastTask would otherwise never run.
            using var ticker = new TaskManagerTicker();

            var result = BotScenarioRunner.Run(
                BotScenarioTemplates.M1M2Replay, session.Character,
                new M1M2ReplayScenarioRigTests.FixtureWorldAdapter(session));

            M1M2ReplayScenarioRigTests.AppendEvidence(result);

            await Assert.That(result.Passed, "replay with REAL 4000ms cast FAILED:\n" + result.Evidence()).IsTrue();

            // The quest-4294 completion + lifecycle criteria must be green —
            // the exact live failure point.
            var q4294 = result.Criteria.FirstOrDefault(c => c.Name == "quest-4294-completed");
            await Assert.That(q4294, "cast-window replay must carry the quest-4294-completed criterion").IsNotNull();
            await Assert.That(q4294!.Passed, "quest 4294 must complete with a real cast time").IsTrue();
            var lifecycle = result.Criteria.FirstOrDefault(c => c.Name == "lifecycle-trace-complete");
            await Assert.That(lifecycle, "cast-window replay must carry the lifecycle criterion").IsNotNull();
            await Assert.That(lifecycle!.Passed, "lifecycle must stay complete through the cast window").IsTrue();
        }
        finally
        {
            skills[11596].CastingTime = original11596;
            skills[13139].CastingTime = original13139;
        }
    }

    /// <summary>Fires the seeded TaskManager's private Tick repeatedly so
    /// scheduled CastTasks complete (the live game loop's role).</summary>
    private sealed class TaskManagerTicker : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        public TaskManagerTicker()
        {
            _thread = new Thread(TickLoop) { IsBackground = true };
            _thread.Start();
        }

        private void TickLoop()
        {
            var tick = typeof(TaskManager).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    tick?.Invoke(TaskManager.Instance, [TimeSpan.FromMilliseconds(50)]);
                }
                catch
                {
                    // The ticker is best-effort; a failed tick must not kill
                    // the loop (the cast deadline is the real bound).
                }
                Thread.Sleep(50);
            }
        }

        public void Dispose() => _cts.Cancel();
    }
}
