using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

using TUnit.Core.Interfaces;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Wildlife slice 4 (ButcherLeg, Option A livestock-only): the roam executor's
/// opportunistic livestock-butcher loop works on EXISTING livestock doodad
/// chains only — canonical cow 5782 → butchered 5790 → LootPack 79 → beef
/// 8048 (real compact.sqlite3 rows via <see cref="LivestockInteractionRig"/>).
/// NPC corpses never become doodads (B stays gated — no corpse→doodad
/// pipeline is built or exercised anywhere here).
///
/// ButcherSkillResolver is deliberately left null so the leg must resolve the
/// butcher skill data-driven (no assumed ids); the failing-before analogue is
/// the flag-off run (same rig, leg gated → no Interact), which each positive
/// test's first step pins.
/// </summary>
[NotInParallel] // touches process-wide singletons (LivestockInteractionRig pattern)
[ParallelLimiter<SequentialParallelLimit>] // housing-lane limiter: serialize against house/pack tests sharing WorldManager/singleton state (stacked limiters do not compile)
public class BotRoamButcherLegTests
{
    private GameplayActor _actor = null!;
    private HeadlessSession _session = null!;
    private bool _butcherSkillSeededByUs;
    private bool _butcherSkillExisted;
    private readonly List<SkillEffect> _butcherSkillPriorEffects = [];

    [Before(Test)]
    public void SetUp()
    {
        // Missing-only (BotRoamStepExecutorTests precedent): never swap the
        // shared config, so this fixture opens no global-config race window.
        AppConfiguration.Instance.World ??= new WorldConfig();
        LivestockInteractionRig.Seed();
    }

    [After(Test)]
    public void TearDown()
    {
        RestoreButcherSkill();
    }

    /// <summary>
    /// The butcher loop is gated: same rig, leg disabled → the scan never
    /// runs, no Interact is issued, and the patrol leg still goes out (zero
    /// success-path change when the flag is off).
    /// </summary>
    [Test]
    public async Task Butcher_DisabledByDefault_LeavesLivestockAlone()
    {
        SetupActor("butcher-leg-off");
        var cow = NewLivestockDoodad(
            LivestockInteractionTests.DairyCalfDoodadId,
            LivestockInteractionTests.CowPhase,
            new Vector3(2000f, 2000f, 100f));
        GameplayActorTestRig.SetPosition(_actor, new Vector3(2005f, 2000f, 100f));

        var runtime = new PlayerBotRuntime(_actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => _actor,
            TimeProvider = clock,
            EnableWildlifeHunt = false,
            EnableWildlifeButcher = false,
            NearbyDoodadProvider = (_, _) => [cow],
            DoodadResolver = (_, id) => id == cow.ObjId ? cow : null
        };
        executor.SetRoamRoute(runtime.Character,
            new BotPath([new Vector3(2050f, 2000f, 100f)], BotPath.LoopMode.Loop));

        clock.Advance(TimeSpan.FromMilliseconds(1200));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(_actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact)).IsFalse();
        await Assert.That(cow.FuncGroupId).IsEqualTo(LivestockInteractionTests.CowPhase);
    }

    /// <summary>
    /// Selectivity pin: a sheep standing on a non-butcherable phase (384
    /// sheared — loot func only, no butcher Use-skill) is scanned but never
    /// engaged: no Interact, phase untouched.
    /// </summary>
    [Test]
    public async Task Butcher_SkipsNonButcherablePhase()
    {
        SetupActor("butcher-leg-sheared");
        var sheep = NewLivestockDoodad(
            LivestockInteractionTests.SheepDoodadId,
            LivestockInteractionTests.SheepShearedPhase,
            new Vector3(2000f, 2000f, 100f));
        GameplayActorTestRig.SetPosition(_actor, new Vector3(2005f, 2000f, 100f));

        var runtime = new PlayerBotRuntime(_actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => _actor,
            TimeProvider = clock,
            EnableWildlifeHunt = false,
            EnableWildlifeButcher = true,
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            NearbyDoodadProvider = (_, _) => [sheep],
            DoodadResolver = (_, id) => id == sheep.ObjId ? sheep : null
        };

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(_actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact)).IsFalse();
        await Assert.That(sheep.FuncGroupId).IsEqualTo(LivestockInteractionTests.SheepShearedPhase);
    }

    /// <summary>
    /// End-to-end through the REAL chain with the data-driven default
    /// resolver: scan engages the cow (step 1, no Interact yet), step 2 fires
    /// Interact and the cow runs 5782 → 5790 → 9907 with beef landing in the
    /// bag (CowButcher_YieldsBeef contract, driven by the leg). Step 3 drops
    /// the target and goes dormant (no route, nothing live).
    /// </summary>
    [Test]
    public async Task Butcher_WhenEnabled_ButchersCowThroughRealChain()
    {
        SetupActor("butcher-leg-cow");
        var cow = NewLivestockDoodad(
            LivestockInteractionTests.DairyCalfDoodadId,
            LivestockInteractionTests.CowPhase,
            new Vector3(2000f, 2000f, 100f));
        GameplayActorTestRig.SetPosition(_actor, new Vector3(2005f, 2000f, 100f));
        await Assert.That(_actor.Character.ParentWorld?.GetDoodad(cow.ObjId)).IsNotNull();
        SeedButcherSkill(); // canonical shape: 13972 rides the Butcher interaction (wi 20)

        var runtime = new PlayerBotRuntime(_actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => _actor,
            TimeProvider = clock,
            EnableWildlifeHunt = false,
            EnableWildlifeButcher = true,
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            NearbyDoodadProvider = (_, _) => [cow],
            DoodadResolver = (_, id) => id == cow.ObjId ? cow : null
        };

        // Step 1: scan engages — target acquired, no interaction yet.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var next = await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(next).IsNotNull();
        await Assert.That(_actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact)).IsFalse();
        await Assert.That(cow.FuncGroupId).IsEqualTo(LivestockInteractionTests.CowPhase);
        // Step 2: in range (5m) → Interact through the real engine path.
        // Target clears; with no route and nothing live the step goes dormant.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        next = await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(next).IsNull();
        await Assert.That(_actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact)).IsTrue();
        await Assert.That(cow.FuncGroupId).IsEqualTo(LivestockInteractionTests.ButcherFinalPhase);
        await Assert.That(BagCount(LivestockInteractionTests.BeefItemId)).IsGreaterThanOrEqualTo(14);

        // Step 3: target cleared, nothing live, no route → dormant.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        next = await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(next).IsNull();
    }

    /// <summary>
    /// Builds the actor for ONE test. The name MUST be unique per test:
    /// ItemManager.GetItemContainerForCharacter registers bags in a global
    /// registry keyed by character id (t_4f11a519-class contamination).
    /// </summary>
    private void SetupActor(string name)
        => (_actor, _session) = GameplayActorTestRig.CreateActor(name);

    /// <summary>
    /// Spawns a livestock doodad standing on the given phase at the given
    /// position (LivestockInteractionTests.NewLivestockDoodad shape, direct
    /// phase placement — no growth timers driven).
    /// </summary>
    private Doodad NewLivestockDoodad(uint templateId, uint phaseId, Vector3 position)
    {
        RegisterWorld(_session.World);
        var doodad = DoodadManager.Instance.Create(_session.World, 0, templateId, null, true)
            ?? throw new InvalidOperationException($"DoodadManager.Create returned null for template {templateId} — is the rig seeded?");
        doodad.Transform = _actor.Character.Transform.CloneDetached(doodad);
        doodad.Transform.InstanceId = _session.World.Id;
        doodad.Transform.Local.SetPosition(position);
        doodad.IsPersistent = false; // unit tests: no MySQL save tail
        doodad.FuncGroupId = phaseId;
        doodad.InitDoodad(); // schedule the phase funcs for the phase
        doodad.Spawn();
        _session.World.SpawnManager?.AddPlayerDoodad(doodad);
        _session.World.AddObject(doodad); // join the mock-world lookup (GetDoodad)
        return doodad;
    }

    /// <summary>
    /// Seeds the canonical skill-pipeline shape for the cow butcher skill
    /// (13972 도축하기 rides the Butcher world interaction, wi 20 — verified
    /// in compact.sqlite3; feed/milk/shear skills ride Use, wi 19). The base
    /// rig seeds bare templates without effects, so the data-driven resolver
    /// needs this one canonical row. The prior state is snapshotted and
    /// restored in TearDown — the shared template never leaks the row.
    /// </summary>
    private void SeedButcherSkill()
    {
        var skills = (Dictionary<uint, SkillTemplate>)GameplayActorTestRig.GetField(SkillManager.Instance, "_skills");
        if (!_butcherSkillSeededByUs)
        {
            _butcherSkillExisted = skills.TryGetValue(LivestockInteractionTests.ButcherSkillId, out var prior);
            if (prior != null)
            {
                _butcherSkillPriorEffects.Clear();
                _butcherSkillPriorEffects.AddRange(prior.Effects);
            }
        }
        if (!skills.TryGetValue(LivestockInteractionTests.ButcherSkillId, out var template))
        {
            template = new SkillTemplate
            {
                Id = LivestockInteractionTests.ButcherSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0
            };
            skills[LivestockInteractionTests.ButcherSkillId] = template;
        }
        if (!template.Effects.Any(e =>
                e?.Template is InteractionEffect interaction
                && interaction.WorldInteraction == WorldInteractionType.Butcher))
        {
            template.Effects.Add(new SkillEffect
            {
                Template = new InteractionEffect { WorldInteraction = WorldInteractionType.Butcher },
                Friendly = true,
                NonFriendly = true,
                Chance = 100
            });
        }
        _butcherSkillSeededByUs = true;
    }

    /// <summary>
    /// Restores the exact pre-test state of the shared butcher skill entry:
    /// our template is removed when we created it, otherwise its effect list
    /// is reset to the snapshotted contents (in place — object identity kept).
    /// </summary>
    private void RestoreButcherSkill()
    {
        if (!_butcherSkillSeededByUs)
            return;
        _butcherSkillSeededByUs = false;
        var skills = (Dictionary<uint, SkillTemplate>)GameplayActorTestRig.GetField(SkillManager.Instance, "_skills");
        if (!_butcherSkillExisted)
        {
            skills.Remove(LivestockInteractionTests.ButcherSkillId);
            return;
        }
        if (skills.TryGetValue(LivestockInteractionTests.ButcherSkillId, out var template))
        {
            template.Effects.Clear();
            template.Effects.AddRange(_butcherSkillPriorEffects);
        }
        _butcherSkillPriorEffects.Clear();
    }

    private static void RegisterWorld(WorldInstance world)
    {
        if (world.Regions == null)
        {
            world.Regions = new Region[
                world.Template.CellX * WorldManager.SECTORS_PER_CELL,
                world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        }
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(world.Id, world);
    }

    private int BagCount(uint templateId)
        => _actor.Character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);
}
