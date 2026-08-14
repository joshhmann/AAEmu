using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

/// <summary>
/// FIX-1 (t_afbf7cb7): livestock interaction funcs — feed / dairy collect /
/// shear / butcher — were log-only stubs. This suite pins the implemented
/// behaviors against the canonical 1.2 chains (real compact.sqlite3 rows,
/// verified 2026-08-11):
///
///   dairy calf 2672: 5780 calf → [growth 791: 12,348,000 ms] → 5781 →
///                    [growth 792: 111,132,000 ms] → 12774 → [ratio 853 →
///                    5782 cow] → Use 497 (skill 20595 사료 먹이기) → 5786
///                    happy cow → Use 501 (skill 13800 가축 젖짜기) → 8436
///                    milked cow → LootPack 81 (pack 6392) → milk 8055;
///                    Use 498 (skill 13972 도축하기) → 5790 butchered →
///                    LootPack 79 (pack 6390) → beef 8048
///   sheep 518:      5649 woolly → Use 1282 (skill 13802 가축 털뽑기) → 384
///                    sheared (regrow term 60,000 ms) → back to 5649;
///                   butcher Use 1283 (skill 13970) → 640 → mutton 8052
///   QA feed doodad 299 (group 47): DoodadFuncFeed consumes feed item 797
///
/// Growth + restart recovery are pinned separately by
/// PhaseStateRestartRecoveryTests (8/8) — do not regress those paths.
/// </summary>
[NotInParallel] // touches process-wide singletons (t_4f11a519 pattern)
[ParallelLimiter<LivestockSequentialParallelLimit>] // t_f3700374: [NotInParallel] does NOT serialize within a class
public class LivestockInteractionTests
{
    // ---- Dairy calf 2672 (canonical chain) ----
    internal const uint DairyCalfDoodadId = 2672;   // 젖소 송아지
    internal const uint CalfStartPhase = 5780;      // 작은 송아지 (start) — growth 791
    internal const uint CalfGrowPhase = 5781;       // 송아지 — growth 792
    internal const uint MatureCowInterimPhase = 12774; // mature cow (ratio 853 → 5782)
    internal const uint CowPhase = 5782;            // 젖소 (milking/butcher interactions)
    internal const uint HappyCowPhase = 5786;       // 행복한 젖소
    internal const uint MilkedCowPhase = 8436;      // 착유된 젖소 → LootPack 81
    internal const uint ButcheredCowPhase = 5790;   // 도축된 젖소 → LootPack 79
    internal const uint ButcherFinalPhase = 9907;   // butcher loot tail
    internal const uint MilkedFinalPhase = 17669;   // milk loot tail

    internal const uint FeedSkillId = 20595;        // 사료 먹이기
    internal const uint MilkSkillId = 13800;        // 가축 젖짜기
    internal const uint ButcherSkillId = 13972;     // 도축하기

    internal const uint MilkItemId = 8055;          // 우유
    internal const uint BeefItemId = 8048;          // 소고기
    internal const uint LeatherItemId = 8007;       // 생가죽
    internal const uint MilkLootPackId = 6392;      // pack 81 (milked cow)
    internal const uint BeefLootPackId = 6390;      // pack 79 (butchered cow)

    // ---- Sheep 518 (canonical shear chain) ----
    internal const uint SheepDoodadId = 518;        // 양
    internal const uint SheepWoollyPhase = 5649;    // woolly sheep (start)
    internal const uint SheepShearedPhase = 384;    // sheared sheep (sheep.shear)
    internal const uint SheepButcherLootPhase = 640;// → mutton loot → 641
    internal const uint SheepButcherFinalPhase = 641;
    internal const uint SheepShearSkillId = 13802;  // 가축 털뽑기
    internal const uint SheepButcherSkillId = 13970;// 도축하기
    internal const int SheepShearTermMs = 60_000;   // doodad_func_shears.shear_term
    internal const uint MuttonItemId = 8052;        // 양고기

    // Sheep dairy/butcher func rows (groups 628/626 are trimmed from the
    // compact DB; the func rows are real and their next_phase targets are
    // canonical) — used for the direct func behavior tests.
    internal const uint SheepDairyPhase = 628;      // DoodadFuncDairyCollect 2 → 629
    internal const uint SheepDairyTargetPhase = 629;
    internal const uint SheepButcherDirectPhase = 626; // DoodadFuncButcher 20 → 640

    // ---- QA feed doodad 299 (almighty "QA 배고픔/먹이주기", group 47) ----
    internal const uint FeedDoodadId = 299;
    internal const uint FeedPhase = 47;             // kind=1 start
    internal const uint FeedItemId = 797;           // 고등어 (feed item ×1)

    // Synthetic regrow timer on the sheared phase: the canonical full sheep
    // chain regrows wool after the shear term; the compact DB trims that
    // timer, so the rig restores it with the canonical 60,000 ms delay.
    internal const uint SheepRegrowTimerFuncId = 65_001;

    private WorldConfig _previousWorldConfig;
    private GameplayActor _actor;
    private HeadlessSession _session;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig(); // GrowthRate 1.0

        LivestockInteractionRig.Seed();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    /// <summary>
    /// Builds the actor for ONE test. The name MUST be unique per test:
    /// ItemManager.GetItemContainerForCharacter registers bags in a global
    /// registry keyed by character id, so a shared name shares the bag across
    /// tests (t_4f11a519-class singleton contamination).
    /// </summary>
    private void SetupActor(string name)
        => (_actor, _session) = GameplayActorTestRig.CreateActor(name);

    /// <summary>
    /// DoodadFuncFeed: with the feed item in the bag the interaction consumes
    /// exactly the configured count (doodad_func_feeds: item 797 ×1) and
    /// stays in the phase (canonical feed rows wire next_phase = -1).
    /// </summary>
    [Test]
    public async Task Feed_WithFeedItemInBag_ConsumesItem_AndStaysInPhase()
    {
        SetupActor("livestock-feed");
        var doodad = NewLivestockDoodad(FeedDoodadId, FeedPhase);
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, FeedItemId, 3);

        doodad.Use(_actor.Character, FeedSkillId); // client always sends an interaction skill
        await Assert.That(BagCount(FeedItemId)).IsEqualTo(2);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(FeedPhase); // -1 = no phase move

        doodad.Use(_actor.Character, FeedSkillId);
        await Assert.That(BagCount(FeedItemId)).IsEqualTo(1);

        doodad.Use(_actor.Character, FeedSkillId);
        await Assert.That(BagCount(FeedItemId)).IsEqualTo(0);
    }

    /// <summary>
    /// DoodadFuncFeed: without the feed item the interaction is refused with
    /// the client "not_enough_item" error and nothing is consumed or changed.
    /// </summary>
    [Test]
    public async Task Feed_WithoutFeedItem_Refuses_AndConsumesNothing()
    {
        SetupActor("livestock-feed-empty");
        var doodad = NewLivestockDoodad(FeedDoodadId, FeedPhase);

        doodad.Use(_actor.Character, FeedSkillId);

        await Assert.That(BagCount(FeedItemId)).IsEqualTo(0);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(FeedPhase);
    }

    /// <summary>
    /// Canonical feed interaction on the dairy calf: the client feed skill
    /// (20595 사료 먹이기) advances the calf 5780 → 5781 and the next growth
    /// stage is scheduled.
    /// </summary>
    [Test]
    public async Task FeedInteraction_OnDairyCalf_AdvancesGrowthPhase()
    {
        SetupActor("livestock-calf-feed");
        var calf = NewLivestockDoodad(DairyCalfDoodadId, CalfStartPhase);
        await Assert.That(calf.FuncTask).IsTypeOf<DoodadFuncGrowthTask>(); // growth 791 scheduled

        calf.Use(_actor.Character, FeedSkillId);

        await Assert.That(calf.FuncGroupId).IsEqualTo(CalfGrowPhase);
        await Assert.That(calf.FuncTask).IsTypeOf<DoodadFuncGrowthTask>(); // growth 792 scheduled
    }

    /// <summary>
    /// DoodadFuncDairyCollect: the collect func advances the animal to its
    /// milked phase; the milk yield comes from the loot funcs on that phase
    /// (canonical: happy cow 5786 → milked cow 8436 → LootPack 81 → milk).
    /// </summary>
    [Test]
    public async Task DairyCollect_AdvancesToMilkedPhase()
    {
        SetupActor("livestock-dairy");
        var sheep = NewLivestockDoodad(SheepDoodadId, SheepDairyPhase);

        sheep.Use(_actor.Character, MilkSkillId); // interaction skill resolves the SkillId==0 func

        await Assert.That(sheep.FuncGroupId).IsEqualTo(SheepDairyTargetPhase);
    }

    /// <summary>
    /// DoodadFuncButcher: butchering advances the animal to its
    /// butchered/loot phase, where the loot funcs grant the meat (sheep:
    /// butcher → 640 → mutton 8052; cow: → 5790 → beef 8048).
    /// </summary>
    [Test]
    public async Task Butcher_AdvancesToButcheredPhase_AndYieldsMutton()
    {
        SetupActor("livestock-sheep-butcher");
        var sheep = NewLivestockDoodad(SheepDoodadId, SheepButcherDirectPhase);

        sheep.Use(_actor.Character, SheepButcherSkillId);

        // The butcher func advances to 640, whose loot funcs run in the same
        // Use() call (crop-harvest pattern) — mutton lands and the chain
        // continues to the loot tail 641.
        await Assert.That(sheep.FuncGroupId).IsEqualTo(SheepButcherFinalPhase);
        await Assert.That(BagCount(MuttonItemId)).IsGreaterThanOrEqualTo(1);
        await Assert.That(BagCount(MuttonItemId)).IsLessThanOrEqualTo(2);
    }

    /// <summary>
    /// DoodadFuncShear: shearing advances the sheep to its sheared phase and
    /// publishes the canonical shear term (60,000 ms) as the regrow deadline.
    /// </summary>
    [Test]
    public async Task Shear_AdvancesToShearedPhase_AndPublishesShearTerm()
    {
        SetupActor("livestock-shear");
        var sheep = NewLivestockDoodad(SheepDoodadId, SheepWoollyPhase);

        sheep.Use(_actor.Character, SheepShearSkillId);

        await Assert.That(sheep.FuncGroupId).IsEqualTo(SheepShearedPhase);

        // ShearTerm published as the regrow deadline (~60 s out)
        var remaining = (sheep.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsGreaterThan(55_000);
        await Assert.That(remaining).IsLessThanOrEqualTo(SheepShearTermMs + 2_000);

        // The sheared phase carries the regrow timer (delay = shear term)
        await Assert.That(sheep.FuncTask).IsTypeOf<DoodadFuncTimerTask>();
    }

    /// <summary>
    /// Sheep shear term loop: the regrow timer on the sheared phase reverts
    /// the sheep to its woolly phase after the term — a player can shear again.
    /// </summary>
    [Test]
    public async Task SheepShearTerm_RegrowTimer_RevertsToWoollyPhase()
    {
        SetupActor("livestock-regrow");
        var sheep = NewLivestockDoodad(SheepDoodadId, SheepWoollyPhase);
        sheep.Use(_actor.Character, SheepShearSkillId);
        await Assert.That(sheep.FuncGroupId).IsEqualTo(SheepShearedPhase);

        (sheep.FuncTask as DoodadFuncTimerTask)?.Execute();

        await Assert.That(sheep.FuncGroupId).IsEqualTo(SheepWoollyPhase);
    }

    /// <summary>
    /// E2E dairy loop — place calf → grow (both growth stages) → mature cow →
    /// feed → happy cow → milk → milk 8055 lands in the bag (7-9 per pack
    /// 6392), all inside the canonical chain. Mirrors the potato harvest E2E.
    /// </summary>
    [Test]
    public async Task DairyMilkLoop_PlaceCalf_Grow_Milk_YieldMilk()
    {
        SetupActor("livestock-milk");
        var calf = NewLivestockDoodad(DairyCalfDoodadId, CalfStartPhase);
        await Assert.That(calf.FuncGroupId).IsEqualTo(CalfStartPhase);

        // grow: 5780 → 5781 (12,348,000 ms task) → 12774 (111,132,000 ms task).
        // 12774 is transient: its ratio-change 853 (9160/10000, always) moves
        // the doodad to the cow phase 5782 inside the same task execution.
        (calf.FuncTask as DoodadFuncGrowthTask)?.Execute();
        await Assert.That(calf.FuncGroupId).IsEqualTo(CalfGrowPhase);
        (calf.FuncTask as DoodadFuncGrowthTask)?.Execute();
        await Assert.That(calf.FuncGroupId).IsEqualTo(CowPhase);

        // feed (20595) → happy cow 5786
        calf.Use(_actor.Character, FeedSkillId);
        await Assert.That(calf.FuncGroupId).IsEqualTo(HappyCowPhase);

        // milk (13800) → 8436 → LootPack 81 → milk 8055 (7-9) → 17669
        calf.Use(_actor.Character, MilkSkillId);
        await Assert.That(calf.FuncGroupId).IsEqualTo(MilkedFinalPhase);
        await Assert.That(BagCount(MilkItemId)).IsGreaterThanOrEqualTo(7);
        await Assert.That(BagCount(MilkItemId)).IsLessThanOrEqualTo(9);
    }

    /// <summary>
    /// E2E butcher — cow → butcher skill (13972 도축하기) → 5790 butchered →
    /// LootPack 79 (pack 6390) → beef 8048 ×14-16 (+ leather, +1 milk).
    /// </summary>
    [Test]
    public async Task CowButcher_YieldsBeef()
    {
        SetupActor("livestock-beef");
        var cow = NewLivestockDoodad(DairyCalfDoodadId, CalfStartPhase);
        (cow.FuncTask as DoodadFuncGrowthTask)?.Execute(); // → 5781
        (cow.FuncTask as DoodadFuncGrowthTask)?.Execute(); // → 12774 → 5782 (ratio)
        await Assert.That(cow.FuncGroupId).IsEqualTo(CowPhase);

        cow.Use(_actor.Character, ButcherSkillId);

        await Assert.That(cow.FuncGroupId).IsEqualTo(ButcherFinalPhase);
        await Assert.That(BagCount(BeefItemId)).IsGreaterThanOrEqualTo(14);
        await Assert.That(BagCount(BeefItemId)).IsLessThanOrEqualTo(16);
        await Assert.That(BagCount(LeatherItemId)).IsGreaterThanOrEqualTo(33);
        await Assert.That(BagCount(LeatherItemId)).IsLessThanOrEqualTo(37);
        await Assert.That(BagCount(MilkItemId)).IsEqualTo(1); // pack 6390 group 3
    }

    private Doodad NewLivestockDoodad(uint templateId, uint phaseId)
    {
        RegisterWorld(_session.World);
        var doodad = DoodadManager.Instance.Create(_session.World, 0, templateId, null, true)
            ?? throw new InvalidOperationException($"DoodadManager.Create returned null for template {templateId} — is the rig seeded?");
        doodad.Transform = _actor.Character.Transform.CloneDetached(doodad);
        doodad.Transform.InstanceId = _session.World.Id;
        doodad.Transform.Local.SetPosition(2000f, 2000f, 100f);
        doodad.IsPersistent = false; // unit tests: no MySQL save tail
        doodad.FuncGroupId = phaseId;
        doodad.InitDoodad(); // schedule the phase funcs (growth/timer) for the phase
        doodad.Spawn();
        _session.World.SpawnManager?.AddPlayerDoodad(doodad);
        return doodad;
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

/// <summary>
/// Serializes tests in this class: every test builds its actor from the same
/// character name, so within-class parallelism would share the session
/// character and bag (t_f3700374 pattern — unique name avoids the CS0104
/// ambiguity of the Housing rig's SequentialParallelLimit).
/// </summary>
public sealed class LivestockSequentialParallelLimit : IParallelLimit
{
    public int Limit => 1;
}

/// <summary>
/// Deterministic stand-in for DoodadFuncRatioChange func 853 (mature cow
/// 12774 → cow 5782, canonical Ratio 9160/10000). The real template rolls
/// PhaseRatio = Random.Shared.Next(0, 10000) on EVERY phase-func execution
/// (Doodad.DoPhaseFuncs), so the canonical chain flakes ~8% of the time
/// (t_4132ea07). This pin mirrors the real success branch — set
/// OverridePhase to NextPhase and stop — without touching product code.
/// Funcs 854/855 stay canonical; no test drives their phases (5787/5788).
/// </summary>
public sealed class AlwaysFireRatioChange : DoodadFuncRatioChange
{
    public override bool Use(BaseUnit caster, Doodad owner)
    {
        owner.OverridePhase = NextPhase;
        return true;
    }
}

/// <summary>
/// Additive rig for the livestock interaction suite. Builds on
/// CropHarvestLoopRig (base surface) and PhaseStateRestartRecoveryRig (which
/// may already have registered the calf chain) — every insert is
/// missing-only / additive so full-suite ordering cannot break it.
/// </summary>
public static class LivestockInteractionRig
{
    private static bool s_seeded;

    public static void Seed()
    {
        lock (typeof(LivestockInteractionRig))
        {
            // CropHarvestLoopRig.Seed() re-heals additively on EVERY call
            // (t_3c33557d) — must not be skipped after the first test.
            CropHarvestLoopRig.Seed(); // base surface (missing-only)
            SeedItemTemplates();
            SeedLootPacks();
            SeedDoodadManager();       // additive TryAdd merges

            s_seeded = true;
        }
    }

    private static void SeedItemTemplates()
    {
        var manager = ItemManager.Instance;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(manager, "_templates");
        foreach (var templateId in new[]
                 {
                     LivestockInteractionTests.FeedItemId,       // 797 고등어
                     LivestockInteractionTests.MilkItemId,       // 8055 우유
                     LivestockInteractionTests.BeefItemId,       // 8048 소고기
                     LivestockInteractionTests.LeatherItemId,    // 8007 생가죽
                     LivestockInteractionTests.MuttonItemId,     // 8052 양고기
                     (uint)29_507                                  // pack 6392 group 2
                 })
        {
            templates.TryAdd(templateId, new ItemTemplate { Id = templateId, MaxCount = 100 });
        }
    }

    /// <summary>
    /// Real loots rows for the canonical packs (drop_rate 1 → loader
    /// normalizes to 10,000,000 = always drop; seeded here in normalized form):
    ///   6390 (butcher cow): beef 8048 ×14-16, leather 8007 ×33-37, milk 8055 ×1
    ///   6392 (milked cow):  milk 8055 ×7-9, item 29507 ×1
    /// </summary>
    private static void SeedLootPacks()
    {
        var instance = LootGameData.Instance;
        var packs = (Dictionary<uint, LootPack>)GetField(instance, "_lootPacks");
        if (packs == null)
        {
            packs = [];
            SetField(instance, "_lootPacks", packs);
        }

        LootPack MakePack(uint packId, params (uint id, uint group, uint itemId, int min, int max)[] loots)
        {
            var lootList = loots.Select(l => new Loot
            {
                Id = l.id, Group = l.group, ItemId = l.itemId, DropRate = 10_000_000,
                MinAmount = l.min, MaxAmount = l.max, LootPackId = packId,
                GradeId = 0, AlwaysDrop = false
            }).ToList();
            return new LootPack
            {
                Id = packId,
                Loots = lootList,
                LootsByGroupNo = lootList.GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.ToList()),
                Groups = [], ActabilityGroups = [], GroupCount = lootList.Max(l => l.Group)
            };
        }

        packs.TryAdd(LivestockInteractionTests.BeefLootPackId, MakePack(
            LivestockInteractionTests.BeefLootPackId,
            (65_512, 1, LivestockInteractionTests.BeefItemId, 14, 16),
            (65_514, 2, LivestockInteractionTests.LeatherItemId, 33, 37),
            (65_517, 3, LivestockInteractionTests.MilkItemId, 1, 1)));

        packs.TryAdd(LivestockInteractionTests.MilkLootPackId, MakePack(
            LivestockInteractionTests.MilkLootPackId,
            (65_516, 1, LivestockInteractionTests.MilkItemId, 7, 9),
            (73_503, 2, 29_507, 1, 1)));
    }

    private static void SeedDoodadManager()
    {
        var manager = DoodadManager.Instance;
        var templates = (Dictionary<uint, DoodadTemplate>)GetField(manager, "_templates");
        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)GetField(manager, "_funcsByGroups");
        var funcsById = (Dictionary<uint, DoodadFunc>)GetField(manager, "_funcsById");
        var phaseFuncs = (Dictionary<uint, List<DoodadPhaseFunc>>)GetField(manager, "_phaseFuncs");
        var phaseFuncTemplates = (Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>)GetField(manager, "_phaseFuncTemplates");
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)GetField(manager, "_funcTemplates");

        SeedDairyChain(templates, funcsByGroups, funcsById, phaseFuncs, phaseFuncTemplates, funcTemplates);
        SeedSheepChain(templates, funcsByGroups, funcsById, phaseFuncs, phaseFuncTemplates, funcTemplates);
        SeedFeedDoodad(templates, funcsByGroups, funcsById, funcTemplates);
    }

    private static void SeedDairyChain(
        Dictionary<uint, DoodadTemplate> templates,
        Dictionary<uint, List<DoodadFunc>> funcsByGroups,
        Dictionary<uint, DoodadFunc> funcsById,
        Dictionary<uint, List<DoodadPhaseFunc>> phaseFuncs,
        Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>> phaseFuncTemplates,
        Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> funcTemplates)
    {
        const uint doodadId = LivestockInteractionTests.DairyCalfDoodadId;

        // Template with all chain phases (merge if PhaseStateRestartRecoveryRig already seeded the calf)
        var template = templates.GetValueOrDefault(doodadId);
        if (template == null)
        {
            template = new DoodadTemplate { Id = doodadId, GrowthTime = 0, TotalDoodadGrowthTime = 0, FuncGroups = [] };
            templates[doodadId] = template;
        }
        AddGroups(template, (LivestockInteractionTests.CalfStartPhase, DoodadFuncGroups.DoodadFuncGroupKind.Start),
            (LivestockInteractionTests.CalfGrowPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.MatureCowInterimPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.CowPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.HappyCowPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.MilkedCowPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.ButcheredCowPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.ButcherFinalPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.MilkedFinalPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal));

        // doodad_funcs rows (real ids, real skill gating)
        DoodadFunc F(uint key, uint groupId, uint funcId, string funcType, int nextPhase, uint skillId) => new()
        {
            FuncKey = key, GroupId = groupId, FuncId = funcId, FuncType = funcType,
            NextPhase = nextPhase, SkillId = skillId
        };
        AddFuncs(funcsByGroups, funcsById, [
            F(4887, LivestockInteractionTests.CalfStartPhase, 495, "DoodadFuncUse", (int)LivestockInteractionTests.CalfGrowPhase, LivestockInteractionTests.FeedSkillId),
            F(4888, LivestockInteractionTests.CalfGrowPhase, 496, "DoodadFuncUse", 12_786, 15_426),
            F(4889, LivestockInteractionTests.CowPhase, 497, "DoodadFuncUse", (int)LivestockInteractionTests.HappyCowPhase, LivestockInteractionTests.FeedSkillId),
            F(4890, LivestockInteractionTests.CowPhase, 498, "DoodadFuncUse", (int)LivestockInteractionTests.ButcheredCowPhase, LivestockInteractionTests.ButcherSkillId),
            F(4898, LivestockInteractionTests.HappyCowPhase, 501, "DoodadFuncUse", (int)LivestockInteractionTests.MilkedCowPhase, LivestockInteractionTests.MilkSkillId),
            F(18_874, LivestockInteractionTests.HappyCowPhase, 4712, "DoodadFuncUse", 5791, LivestockInteractionTests.ButcherSkillId),
            F(8815, LivestockInteractionTests.ButcheredCowPhase, 79, "DoodadFuncLootPack", (int)LivestockInteractionTests.ButcherFinalPhase, 0),
            F(8817, LivestockInteractionTests.MilkedCowPhase, 81, "DoodadFuncLootPack", (int)LivestockInteractionTests.MilkedFinalPhase, 0)
        ]);

        // doodad_func_uses (all skill_id NULL in data → no skill cast)
        AddFuncTemplates(funcTemplates, "DoodadFuncUse", (495, new DoodadFuncUse()), (496, new DoodadFuncUse()),
            (497, new DoodadFuncUse()), (498, new DoodadFuncUse()), (501, new DoodadFuncUse()), (4712, new DoodadFuncUse()));
        AddFuncTemplates(funcTemplates, "DoodadFuncLootPack",
            (79, new DoodadFuncLootPack { LootPackId = LivestockInteractionTests.BeefLootPackId }),
            (81, new DoodadFuncLootPack { LootPackId = LivestockInteractionTests.MilkLootPackId }));

        // doodad_phase_funcs rows (real ids + params)
        DoodadPhaseFunc P(uint groupId, uint funcId, string funcType) => new() { GroupId = groupId, FuncId = funcId, FuncType = funcType };
        AddPhaseFuncs(phaseFuncs, [
            P(LivestockInteractionTests.CalfStartPhase, 791, "DoodadFuncGrowth"),
            P(LivestockInteractionTests.CalfStartPhase, 189, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.CalfGrowPhase, 792, "DoodadFuncGrowth"),
            P(LivestockInteractionTests.CalfGrowPhase, 191, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.MatureCowInterimPhase, 1345, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.MatureCowInterimPhase, 853, "DoodadFuncRatioChange"),
            P(LivestockInteractionTests.MatureCowInterimPhase, 854, "DoodadFuncRatioChange"),
            P(LivestockInteractionTests.MatureCowInterimPhase, 855, "DoodadFuncRatioChange"),
            P(LivestockInteractionTests.MatureCowInterimPhase, 6598, "DoodadFuncTimer"),
            P(LivestockInteractionTests.CowPhase, 192, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.CowPhase, 21, "DoodadFuncHouseFarm"),
            P(LivestockInteractionTests.CowPhase, 3946, "DoodadFuncTimer"),
            P(LivestockInteractionTests.HappyCowPhase, 195, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.HappyCowPhase, 1220, "DoodadFuncTimer"),
            P(LivestockInteractionTests.HappyCowPhase, 22, "DoodadFuncHouseFarm"),
            P(LivestockInteractionTests.MilkedCowPhase, 3297, "DoodadFuncTimer"),
            P(LivestockInteractionTests.MilkedCowPhase, 517, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.ButcheredCowPhase, 3295, "DoodadFuncTimer"),
            P(LivestockInteractionTests.ButcheredCowPhase, 1341, "DoodadFuncAnimate")
        ]);

        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncGrowth",
            (791, new DoodadFuncGrowth { Delay = 12_348_000, StartScale = 1000, EndScale = 1000, NextPhase = (int)LivestockInteractionTests.CalfGrowPhase }),
            (792, new DoodadFuncGrowth { Delay = 111_132_000, StartScale = 1000, EndScale = 1000, NextPhase = (int)LivestockInteractionTests.MatureCowInterimPhase }));
        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncTimer",
            (3946, new DoodadFuncTimer { Delay = 345_600_000, NextPhase = 5783 }),
            (1220, new DoodadFuncTimer { Delay = 345_600_000, NextPhase = (int)LivestockInteractionTests.CowPhase }),
            (6598, new DoodadFuncTimer { Delay = 500, NextPhase = (int)LivestockInteractionTests.CowPhase }),
            (3295, new DoodadFuncTimer { Delay = 180_000, NextPhase = (int)LivestockInteractionTests.ButcherFinalPhase }),
            (3297, new DoodadFuncTimer { Delay = 180_000, NextPhase = (int)LivestockInteractionTests.MilkedFinalPhase }));
        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncRatioChange",
            (853, new AlwaysFireRatioChange { NextPhase = (int)LivestockInteractionTests.CowPhase }),
            (854, new DoodadFuncRatioChange { Ratio = 560, NextPhase = 5787 }),
            (855, new DoodadFuncRatioChange { Ratio = 280, NextPhase = 5788 }));
        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncAnimate",
            (189, new DoodadFuncAnimate { Name = "calf_small", PlayOnce = false }),
            (191, new DoodadFuncAnimate { Name = "calf", PlayOnce = false }),
            (192, new DoodadFuncAnimate { Name = "cow", PlayOnce = false }),
            (195, new DoodadFuncAnimate { Name = "happy_cow", PlayOnce = false }),
            (517, new DoodadFuncAnimate { Name = "milked", PlayOnce = false }),
            (1341, new DoodadFuncAnimate { Name = "butchered", PlayOnce = false }),
            (1345, new DoodadFuncAnimate { Name = "mature", PlayOnce = false }));
        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncHouseFarm",
            (21, new DoodadFuncHouseFarm { ItemCategoryId = 0 }), (22, new DoodadFuncHouseFarm { ItemCategoryId = 0 }));
    }

    private static void SeedSheepChain(
        Dictionary<uint, DoodadTemplate> templates,
        Dictionary<uint, List<DoodadFunc>> funcsByGroups,
        Dictionary<uint, DoodadFunc> funcsById,
        Dictionary<uint, List<DoodadPhaseFunc>> phaseFuncs,
        Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>> phaseFuncTemplates,
        Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> funcTemplates)
    {
        const uint doodadId = LivestockInteractionTests.SheepDoodadId;

        var template = templates.GetValueOrDefault(doodadId);
        if (template == null)
        {
            template = new DoodadTemplate { Id = doodadId, GrowthTime = 0, TotalDoodadGrowthTime = 0, FuncGroups = [] };
            templates[doodadId] = template;
        }
        AddGroups(template,
            (LivestockInteractionTests.SheepWoollyPhase, DoodadFuncGroups.DoodadFuncGroupKind.Start),
            (LivestockInteractionTests.SheepShearedPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (639, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.SheepButcherLootPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.SheepButcherFinalPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.SheepDairyPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.SheepDairyTargetPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
            (LivestockInteractionTests.SheepButcherDirectPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal));

        DoodadFunc F(uint key, uint groupId, uint funcId, string funcType, int nextPhase, uint skillId) => new()
        {
            FuncKey = key, GroupId = groupId, FuncId = funcId, FuncType = funcType,
            NextPhase = nextPhase, SkillId = skillId
        };
        AddFuncs(funcsByGroups, funcsById, [
            F(6797, LivestockInteractionTests.SheepWoollyPhase, 1282, "DoodadFuncUse", (int)LivestockInteractionTests.SheepShearedPhase, LivestockInteractionTests.SheepShearSkillId),
            F(6798, LivestockInteractionTests.SheepWoollyPhase, 1283, "DoodadFuncUse", (int)LivestockInteractionTests.SheepButcherLootPhase, LivestockInteractionTests.SheepButcherSkillId),
            F(6799, 639, 1284, "DoodadFuncUse", (int)LivestockInteractionTests.SheepButcherLootPhase, LivestockInteractionTests.SheepButcherSkillId),
            F(2036, LivestockInteractionTests.SheepButcherLootPhase, 729, "DoodadFuncLootItem", (int)LivestockInteractionTests.SheepButcherFinalPhase, 0),
            F(724, LivestockInteractionTests.SheepButcherLootPhase, 310, "DoodadFuncLootItem", (int)LivestockInteractionTests.SheepButcherFinalPhase, 0),
            F(705, LivestockInteractionTests.SheepDairyPhase, 2, "DoodadFuncDairyCollect", (int)LivestockInteractionTests.SheepDairyTargetPhase, 0),
            F(2764, LivestockInteractionTests.SheepButcherDirectPhase, 20, "DoodadFuncButcher", (int)LivestockInteractionTests.SheepButcherLootPhase, 0)
        ]);

        AddFuncTemplates(funcTemplates, "DoodadFuncUse",
            (1282, new DoodadFuncUse()), (1283, new DoodadFuncUse()), (1284, new DoodadFuncUse()));
        AddFuncTemplates(funcTemplates, "DoodadFuncLootItem",
            (729, new DoodadFuncLootItem { ItemId = LivestockInteractionTests.LeatherItemId, CountMin = 1, CountMax = 2, Percent = 1000, RemainTime = 100_000, GroupId = 1 }),
            (310, new DoodadFuncLootItem { ItemId = LivestockInteractionTests.MuttonItemId, CountMin = 1, CountMax = 2, Percent = 10_000, RemainTime = 100_000, GroupId = 1 }));
        AddFuncTemplates(funcTemplates, "DoodadFuncDairyCollect",
            (2, new DoodadFuncDairyCollect()));
        AddFuncTemplates(funcTemplates, "DoodadFuncButcher",
            (20, new DoodadFuncButcher { CorpseModel = "same" }));

        DoodadPhaseFunc P(uint groupId, uint funcId, string funcType) => new() { GroupId = groupId, FuncId = funcId, FuncType = funcType };
        AddPhaseFuncs(phaseFuncs, [
            P(LivestockInteractionTests.SheepWoollyPhase, 25, "DoodadFuncTimer"),
            P(LivestockInteractionTests.SheepWoollyPhase, 338, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.SheepShearedPhase, LivestockInteractionTests.SheepRegrowTimerFuncId, "DoodadFuncTimer"),
            P(639, 25, "DoodadFuncTimer"),
            P(639, 338, "DoodadFuncAnimate"),
            P(LivestockInteractionTests.SheepButcherLootPhase, 6567, "DoodadFuncTimer")
        ]);

        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncTimer",
            (25, new DoodadFuncTimer { Delay = 1_800_000, NextPhase = (int)LivestockInteractionTests.SheepWoollyPhase }),
            (LivestockInteractionTests.SheepRegrowTimerFuncId, new DoodadFuncTimer { Delay = LivestockInteractionTests.SheepShearTermMs, NextPhase = (int)LivestockInteractionTests.SheepWoollyPhase }),
            (6567, new DoodadFuncTimer { Delay = 180_000, NextPhase = (int)LivestockInteractionTests.SheepButcherFinalPhase }));
        AddPhaseFuncTemplates(phaseFuncTemplates, "DoodadFuncAnimate",
            (338, new DoodadFuncAnimate { Name = "sheep_idle", PlayOnce = false }));
    }

    private static void SeedFeedDoodad(
        Dictionary<uint, DoodadTemplate> templates,
        Dictionary<uint, List<DoodadFunc>> funcsByGroups,
        Dictionary<uint, DoodadFunc> funcsById,
        Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> funcTemplates)
    {
        const uint doodadId = LivestockInteractionTests.FeedDoodadId;
        var template = templates.GetValueOrDefault(doodadId);
        if (template == null)
        {
            template = new DoodadTemplate { Id = doodadId, GrowthTime = 0, TotalDoodadGrowthTime = 0, FuncGroups = [] };
            templates[doodadId] = template;
        }
        AddGroups(template, (LivestockInteractionTests.FeedPhase, DoodadFuncGroups.DoodadFuncGroupKind.Start));

        // real row: (68, 47, 6, 'DoodadFuncFeed', -1, 0) — feed 6 = item 797 ×1, no phase move
        AddFuncs(funcsByGroups, funcsById, [
            new DoodadFunc
            {
                FuncKey = 68, GroupId = LivestockInteractionTests.FeedPhase, FuncId = 6,
                FuncType = "DoodadFuncFeed", NextPhase = -1, SkillId = 0
            }
        ]);
        AddFuncTemplates(funcTemplates, "DoodadFuncFeed",
            (6, new DoodadFuncFeed { ItemId = LivestockInteractionTests.FeedItemId, Count = 1 }));
    }

    private static void AddGroups(DoodadTemplate template, params (uint id, DoodadFuncGroups.DoodadFuncGroupKind kind)[] groups)
    {
        foreach (var (id, kind) in groups)
        {
            if (template.FuncGroups.All(g => g.Id != id))
                template.FuncGroups.Add(new DoodadFuncGroups { Id = id, Almighty = template.Id, GroupKindId = kind });
        }
    }

    private static void AddFuncs(
        Dictionary<uint, List<DoodadFunc>> funcsByGroups,
        Dictionary<uint, DoodadFunc> funcsById,
        params DoodadFunc[] funcs)
    {
        foreach (var func in funcs)
        {
            if (!funcsByGroups.TryGetValue(func.GroupId, out var group))
            {
                group = [];
                funcsByGroups[func.GroupId] = group;
            }
            if (group.All(f => f.FuncKey != func.FuncKey))
                group.Add(func);
            funcsById.TryAdd(func.FuncKey, func);
        }
    }

    private static void AddFuncTemplates(
        Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> funcTemplates,
        string type,
        params (uint id, DoodadFuncTemplate template)[] entries)
    {
        if (!funcTemplates.TryGetValue(type, out var inner))
        {
            inner = [];
            funcTemplates[type] = inner;
        }
        foreach (var (id, template) in entries)
            inner.TryAdd(id, template);
    }

    private static void AddPhaseFuncs(Dictionary<uint, List<DoodadPhaseFunc>> phaseFuncs, params DoodadPhaseFunc[] funcs)
    {
        foreach (var func in funcs)
        {
            if (!phaseFuncs.TryGetValue(func.GroupId, out var group))
            {
                group = [];
                phaseFuncs[func.GroupId] = group;
            }
            if (group.All(f => f.FuncId != func.FuncId || f.FuncType != func.FuncType))
                group.Add(func);
        }
    }

    private static void AddPhaseFuncTemplates(
        Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>> phaseFuncTemplates,
        string type,
        params (uint id, DoodadPhaseFuncTemplate template)[] entries)
    {
        if (!phaseFuncTemplates.TryGetValue(type, out var inner))
        {
            inner = [];
            phaseFuncTemplates[type] = inner;
        }
        foreach (var (id, template) in entries)
            inner.TryAdd(id, template);
    }

    private static object GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target);

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}
