using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// BACKTRACK Phase 2 (t_b4f455b0) — M3a/M4 economic replay rig: the curated
/// farm → craft → pack → vehicle → trade → bank route driven through the
/// M5.1 + B1 CONTRACT ACTIONS on the fixture world, with conservation
/// (items/currency/labor) + lifecycle asserts — the deterministic gate
/// evidence for the replay (the live E2E hook drives the same scenario with
/// canonical ids against the real stack).
///
/// Fixture mapping (the fixture surfaces the M5.1 family tests already
/// exercise, plus the REAL potato crop loop so the farm segment runs the
/// canonical plant → grow → harvest chain):
///   - farm: potato seed 15659 (real, CropHarvestLoopRig surface) bought
///     from the rig merchant, planted through the contract Plant (the
///     engine's MySQL Save() tail is unreachable in unit tests → the plant
///     lands as Interrupted-at-boundary WITH the effect applied — the
///     scenario's documented acceptance for the persistence tail);
///   - craft: fixture recipe 99001 (materials = harvested potato 7992 ×3 →
///     product = the fixture auto-equip trade pack 92001) at the rig bench;
///   - pack: PutDown/PackPickup of 92001 through the fixture pack surface;
///   - vehicle: the rig cargo slave (Board/Drive through the real
///     SlaveManager + movement model; the fixture world carries no real
///     summon-scroll item data, so the pump injects the slave and the LIVE
///     hook exercises the real UseItem summon path);
///   - trade/bank: rig merchant buy/sell + deposit/withdraw round trips.
///
/// H stays UNKNOWN — proxy/bot-functional evidence only.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // process-wide MySQL.SetConfiguration + singleton state
public class M3aM4ReplayScenarioRigTests
{
    // ---- fixture route ids ----------------------------------------------
    private const uint RigSeedItemId = CropHarvestLoopTests.PotatoSeedItemId;      // 15659 (real potato seed)
    private const int RigSeedCount = 3;
    private const uint RigHarvestItemId = CropHarvestLoopTests.PotatoItemId;        // 7992 (potato — craft material)
    private const uint RigPackItemId = GameplayActorTestRig.PackTemplateId;         // 92001 (fixture auto-equip trade pack)
    private const uint RigPlacedDoodadId = GameplayActorTestRig.PlacedPackDoodadTemplateId; // 92201
    private const uint RigCertificateItemId = 88_102;                               // fixture sellable cert item
    private const uint RigCraftId = 99_001;                                         // fixture pack recipe
    private const uint RigCraftSkillId = 99_002;
    private const int RigCraftMaterialAmount = 3;

    private static readonly Vector3 RigFarmOrigin = new(1000f, 1000f, 100f);

    [Before(Test)]
    public void SetUp()
    {
        // Doodad.Save() must fail FAST and deterministically headless (the
        // PlantActionsTests convention): a dead port turns the MySQL write
        // into an immediate MySqlException instead of a localhost:3306
        // attempt (which could hit a real dev MySQL).
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        RestoreMovementSingletons(); // sibling suites must never observe the swap
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
    }

    [Test]
    public async Task M3aM4EconomicReplay_OnFixtureWorld_PassesConservationAndLifecycle()
    {
        // ---------------------------------------------------------- surfaces
        CropHarvestLoopRig.Seed();          // real potato chain (seed 15659 → crop 2259 → yield 7992)
        SeedDoodadIdManager();              // Doodad.Save() row-id allocation (missing-only init)
        GameplayActorTestRig.SeedPackSurface();     // fixture pack 92001 + put-down + recoverable doodad
        GameplayActorTestRig.SeedCargoPackSurface();// fixture cargo pack surface
        GameplayActorTestRig.SeedSlaveCargoSurface();// fixture cargo slave surface (cargo points 9-12)
        GameplayActorTestRig.SeedTradeSurface();    // merchant + grades + buy/sell items
        SeedFixtureCraft();                         // fixture recipe 99001: 7992×3 → 92001
        SeedSellableHarvestYield();                 // real potato IS sellable (items 7992: price 100, sellable 't')

        var (actor, session) = GameplayActorTestRig.CreateActor("m3a4-rig");
        RigWorld(session); // register the fixture world (PlantActionsTests pattern)
        GameplayActorTestRig.SetPosition(actor, RigFarmOrigin);
        GameplayActorTestRig.SetMoney(actor, 100_000);
        actor.Character.LaborPower = M3aM4ReplayScenario.DefaultLaborPool;
        SeedMovementSingletons(); // SusManager/ModelManager — the DriveVehicle-tests pattern

        // Fixture merchant sells the seed (potato seed 15659) — the farm's
        // Buy stage buys from it. Merchants spawn at the rig origin; move
        // them onto the actor so the 3m shop-range gate passes.
        var merchantPackId = GameplayActorTestRig.SeedMerchantPack(RigSeedItemId);
        var seedMerchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1001, packId: merchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, seedMerchantObjId, RigFarmOrigin);
        // The general merchant sells the certificate (fixture item 88102).
        var generalPackId = GameplayActorTestRig.SeedMerchantPack(RigCertificateItemId);
        var generalMerchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1002, packId: generalPackId);
        GameplayActorTestRig.SetNpcPosition(session, generalMerchantObjId, RigFarmOrigin);

        // Craft bench (fixture doodad 1m in front of the actor).
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);

        var world = new FixtureReplayWorld(seedMerchantObjId, generalMerchantObjId);
        var pump = new FixtureReplayPump(session, actor, benchObjId);

        var options = new M3aM4ReplayScenario.ReplayOptions
        {
            MilletSeedItemId = RigSeedItemId,
            MilletSeedCount = RigSeedCount,
            PoppySeedItemId = RigSeedItemId,
            PoppySeedCount = RigSeedCount,
            FarmOrigin = RigFarmOrigin,
            PlotSpacing = 2f,
            PackCraftId = RigCraftId,
            MilletMaterialItemId = RigHarvestItemId,
            MilletMaterialAmount = RigCraftMaterialAmount,
            PoppyMaterialItemId = RigHarvestItemId,
            PoppyMaterialAmount = 0,
            CertificateItemId = RigCertificateItemId,
            PackItemTemplateId = RigPackItemId,
            PlacedPackDoodadTemplateId = RigPlacedDoodadId,
            FarmWagonSummonScrollItemId = 0,
            SeedMerchantNpcTemplateId = 1001,
            GeneralMerchantNpcTemplateId = 1002,
            RigLevel = 10,
            SeedMoney = 100_000,
            LaborTolerance = 0, // no labor regen in the unit world — exact assert
            CropMaturityTimeout = TimeSpan.FromSeconds(5),
            ActionPumpTimeout = TimeSpan.FromSeconds(10)
        };

        var result = M3aM4ReplayScenario.Run(actor.Character, world, pump, options);

        if (!result.Passed)
        {
            var bag = actor.Character.Inventory.Bag.GetItemsSnapshot();
            var warehouse = actor.Character.Inventory.Warehouse.GetItemsSnapshot();
            TestContext.Current!.OutputWriter.WriteLine(
                $"replay FAILED at {result.FailStage} ({result.Failure}): {result.FailReason}\n" +
                string.Join("\n", result.Criteria.Select(c => $"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}")) +
                "\nRIG NOTES:\n" + string.Join("\n", result.RigNotes) +
                "\nBAG: " + string.Join(", ", bag.Select(i => $"{i.TemplateId} x{i.Count}(id {i.Id})")) +
                "\nWAREHOUSE: " + string.Join(", ", warehouse.Select(i => $"{i.TemplateId} x{i.Count}(id {i.Id})")));
        }

        await Assert.That(result.Passed).IsTrue();

        // The route drove the full action vocabulary (≥ 12 trace records).
        await Assert.That(result.ActorRequests).IsGreaterThanOrEqualTo(12);
        // Pack conservation + currency + labor + lifecycle criteria all green.
        await Assert.That(result.Criteria.Any(c => c.Name == "pack-conserved" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "currency-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "labor-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "lifecycle-trace-complete" && c.Passed)).IsTrue();
    }

    /// <summary>
    /// The CropHarvestLoop surface seeds the potato item template WITHOUT the
    /// trade fields (its tests never sell). The real item is sellable with a
    /// refund (items 7992: price 100, sellable 't') — patch the fixture
    /// template additively (missing-only) so the Sell contract stage runs
    /// the real engine path on the harvest yield.
    /// </summary>
    [Test]
    public async Task Plant_Probe_ContractPlantConsumesSeedInFixtureWorld()
    {
        // The seed-conservation criterion observed 6 seeds surviving 6
        // interrupted plants — probe whether the contract Plant consumes the
        // seed in THIS surface set (CropHarvestLoopRig vs the
        // PlantActionsTests SeedPlantSurface set), mirroring their
        // Plant_SeedOnUnclaimedLand_ChargesLaborExactlyOnce shape.
        CropHarvestLoopRig.Seed();
        SeedDoodadIdManager();

        var (actor, session) = GameplayActorTestRig.CreateActor("m3a4-probe");
        RigWorld(session);
        GameplayActorTestRig.SetPosition(actor, RigFarmOrigin);
        GameplayActorTestRig.SetMoney(actor, 100_000);
        actor.Character.LaborPower = 2000;

        GameplayActorTestRig.StockItem(session, RigSeedItemId, 3);
        var before = actor.Character.Inventory.GetItemsCount(RigSeedItemId);
        var plant = actor.Plant(RigSeedItemId, RigFarmOrigin, idempotencyKey: "m3a4-probe-plant");
        var after = actor.Character.Inventory.GetItemsCount(RigSeedItemId);

        TestContext.Current!.OutputWriter.WriteLine(
            $"probe: seed {RigSeedItemId} before={before} after={after} state={plant.State} " +
            $"detail={plant.Detail} result={plant.Result}");

        // The PlantActionsTests surface consumes exactly one seed per plant
        // (their assertion: 3 → 2 after one interrupted plant).
        await Assert.That(after).IsEqualTo(before - 1);
    }

    private static void SeedSellableHarvestYield()
    {
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        if (templates.TryGetValue(RigHarvestItemId, out var potato))
        {
            potato.Sellable = true;
            potato.Refund = 100; // real value (items.refund for 7992)
        }
    }

    /// <summary>
    /// Seeds the fixture pack recipe: 3 × harvested potato (7992) → 1 ×
    /// fixture trade pack (92001) at the rig bench, skill 99002 (doodad
    /// target, labor 10). Additive to the shared CraftManager (missing-only).
    /// </summary>
    private static void SeedFixtureCraft()
    {
        GameplayActorTestRig.SeedCraftSurface(); // base craft surface (bench + skill pipeline)

        var craftManager = CraftManager.Instance;
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(craftManager)!;
        if (!crafts.ContainsKey(RigCraftId))
        {
            crafts[RigCraftId] = new Craft
            {
                Id = RigCraftId,
                SkillId = RigCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = RigHarvestItemId, Amount = RigCraftMaterialAmount }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = RigPackItemId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(RigCraftSkillId))
        {
            skills[RigCraftSkillId] = new SkillTemplate
            {
                Id = RigCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = 10,
                ActabilityGroupId = 0,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }
    }

    /// <summary>
    /// Doodad.Save() allocates the row id via DoodadIdManager BEFORE the
    /// MySQL write; the replay rig points MySQL at a dead port so the write
    /// fails fast and deterministically (PlantActionsTests convention), and
    /// the id manager must be initialized to reach it (missing-only,
    /// t_6bad0654 discipline).
    /// </summary>
    private static void SeedDoodadIdManager()
    {
        var freeIdsField = typeof(AAEmu.Game.Utils.IdManager).GetField("_freeIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (freeIdsField?.GetValue(AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance) == null)
            AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance.Initialize(false);
    }

    /// <summary>
    /// Gives the fixture world a UNIQUE high-base instance id (headless
    /// session worlds are born as instance id 1 — an unregistered id-1 world
    /// makes DoodadManager.CreatePlayerDoodad's
    /// WorldManager.GetWorld(InstanceId) resolve null and the
    /// Transform.set_InstanceId → GameObject.set_ParentWorld chain NREs) and
    /// registers it, then attaches a SpawnManager (CanPlace's
    /// GetCommonFarmDoodads + the engine's AddPlayerDoodad tail dereference
    /// it — production worlds get one at creation). Same registration shape
    /// as GameplayActorPlantActionsTests.RigWorld (0x4000_0000) and
    /// CropHarvestLoopRig.RegisterWorld; unique base 0x5000_0000 so suites
    /// never collide.
    /// </summary>
    private static uint s_nextWorldId = 0x5000_0000;

    private static void RigWorld(HeadlessSession session)
    {
        typeof(WorldInstance).GetField("<Id>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(session.World.Id, session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);

        // CreateActor pinned the character's _instanceId to the session
        // world's ORIGINAL id via the backing field (the registry bypass).
        // The engine's CreatePlayerDoodad does
        // doodad.Transform = character.Transform.CloneDetached(doodad), and
        // the clone ctor resolves GameObject.ParentWorld =
        // WorldManager.GetWorld(InstanceId) — a stale original-id copy would
        // resolve null (the id-1 world was renamed away) and NRE. Same
        // backing-field bypass pattern as CreateActor.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>World adapter: merchant NPCs resolve to the rig-spawned ones
    /// (template ids 1001/1002 map to their live objIds).</summary>
    private sealed class FixtureReplayWorld(uint seedMerchantObjId, uint generalMerchantObjId)
        : BotScenarioRunner.IScenarioWorldAdapter
    {
        public uint ResolveNpcObjId(uint npcTemplateId)
            => npcTemplateId switch
            {
                1001 => seedMerchantObjId,
                1002 => generalMerchantObjId,
                _ => 0
            };

        public uint ResolveDoodadObjId(uint doodadTemplateId) => 0;
    }

    // ------------------------------------------------- movement singletons

    private static object? _previousSusManager;
    private static object? _previousModelManager;

    /// <summary>
    /// FinalizeTransform runs delta-movement analysis through SusManager
    /// every 5s of accumulated movement, and Character.SetPosition consults
    /// ModelManager when the character is attached to a Slave (deck-height
    /// probe). The headless test process has no DI — seed both singletons
    /// the way GameplayActorDriveVehicleTests does (AFTER the rig's Seed()
    /// has populated WorldManager). Restored in TearDown so sibling suites
    /// never observe the swap.
    /// </summary>
    private static void SeedMovementSingletons()
    {
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new SusManager(WorldManager.Instance));

        _previousModelManager = typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        var modelManager = new ModelManager();
        typeof(ModelManager)
            .GetField("_modelTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<uint, ModelType>());
        typeof(ModelManager)
            .GetField("_models", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>>());
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, modelManager);
    }

    private void RestoreMovementSingletons()
    {
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousSusManager);
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousModelManager);
    }

    /// <summary>
    /// Fixture pump: drives in-flight requests deterministically (no wall
    /// clock) — craft completion through the real CraftEffect chain (the
    /// same step the rig's CompleteCraftStep applies), crop maturity through
    /// the armed engine growth tasks (DoodadFuncGrowthTask.Execute — the
    /// Harvest rig's own no-wall-clock path), and the wagon is injected
    /// (the fixture world has no real summon-scroll item data).
    /// </summary>
    private sealed class FixtureReplayPump(HeadlessSession session, GameplayActor actor, uint benchObjId)
        : M3aM4ReplayScenario.IScenarioPump
    {
        private bool _craftStepApplied;

        public ActorRequest Drive(GameplayActor a, ActorRequest request, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                a.Tick(TimeSpan.FromMilliseconds(20));
                if (request.Action == ActorActionType.Craft && !_craftStepApplied
                    && a.Character.Craft is { IsCraftQueueActive: true })
                {
                    // The engine craft queue drains when the skill pipeline
                    // completes its step — apply the REAL CraftEffect (the
                    // exact chain CharacterCraft's CraftTask runs) once.
                    var bench = a.Character.ParentWorld?.GetDoodad(benchObjId);
                    var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                    effect.Apply(a.Character, null, bench, null,
                        new CastSkill(RigCraftSkillId, 0), new EffectSource(), null, DateTime.UtcNow);
                    _craftStepApplied = true;
                }

                Thread.Sleep(5);
            }

            return request;
        }

        public bool WaitForCropMaturity(Character character, uint cropObjId, TimeSpan maxWait)
        {
            // The unit world materializes crops at the mature phase (see
            // ProvisionCropAtBoundary) — the first poll resolves harvestable.
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                var crop = character.ParentWorld?.GetDoodad(cropObjId);
                if (crop == null)
                    return false;
                if (M3aM4ReplayScenario.IsHarvestable(crop))
                    return true;
                Thread.Sleep(5);
            }

            return false;
        }

        public uint? TrySummonVehicle(Character character)
            => GameplayActorTestRig.SummonCargoSlave(session, actor,
                GameplayActorTestRig.SlaveObjId + 0x100).ObjId;

        public uint? ProvisionPlacedPackDoodad(Character character)
        {
            // The contract PutDown moved the pack into the System container
            // (engine anti-dupe invariant) but its doodad spawn tail is
            // unreachable headless (the shared pack-rig surface deliberately
            // leaves the placed template absent). Materialize the placed
            // doodad through the SAME fixture the pack tests use
            // (PlacePackDoodad — func surface only, zero sibling impact) so
            // the PackPickup/RecoverItem chain drives REAL engine paths.
            //
            // At LOAD time the pack is CARRIED again (the pickup moved it to
            // the Backpack slot) — a placed doodad linked to a carried pack
            // would duplicate the instance, so drive the REAL PutDown
            // contract action once more (the unit world's put-down boundary)
            // to move the pack back into the System container before linking.
            var carried = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (carried != null && carried.TemplateId == GameplayActorTestRig.PackTemplateId)
            {
                // The engine's GCD gate (Skill.cs:135 — 150ms between skill
                // uses) refuses the rapid re-place: the first put-down was
                // the last skill use and this one fires within the window.
                // Space the re-place like a real actor would (the live pump
                // spaces actions by real time anyway).
                Thread.Sleep(200);
                var rePlace = actor.PutDown(GameplayActorTestRig.PackTemplateId, idempotencyKey: "m3a4-putdown-reload");
                if (rePlace.State != ActorLifecycleState.Completed)
                    return null;
            }
            var pack = character.Inventory.SystemContainer.GetItemsSnapshot()
                .FirstOrDefault(i => i.TemplateId == GameplayActorTestRig.PackTemplateId);
            if (pack == null)
                return null;

            var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);
            var doodad = session.World.GetDoodad(doodadObjId);
            if (doodad == null)
                return null;

            // FIXTURE-WORLD FIDELITY REPAIR: the Doodad constructor defaults
            // AttachPoint to AttachPointKind.System (Doodad.cs:371), and the
            // fixture PlacePackDoodad never resets it — the open-field placed
            // pack the REAL PutDownBackpackEffect spawns carries
            // AttachPoint=None / ParentObjId=0 (the M4-2 open-field seed's
            // exact row shape). Normalize to the real-world state so the
            // LoadPackOntoVehicle pre-flight ("already attached") evaluates
            // the way it does on a live server. No engine code touched.
            doodad.AttachPoint = AttachPointKind.None;
            doodad.ParentObjId = 0;
            return doodadObjId;
        }

        public uint? ProvisionCropAtBoundary(Character character, Vector3 position)
        {
            // The contract Plant ran its full gate chain (labor + seed
            // consumption + engine placement attempt) but Doodad.Save()'s
            // MySQL tail cannot run in the unit world — the request lands
            // Interrupted with the crop never spawned. Materialize the
            // in-world crop through the SAME fixture plant path the accepted
            // Harvest rig uses, AT THE MATURE PHASE:
            //   - IsPersistent=false skips the Save tail;
            //   - the phase is set directly to the harvestable group (the
            //     rig's chain: seedling 4379 → small 4456 → mature 4457),
            //     because wall-clock growth timers cannot run deterministically
            //     in the unit world — the LIVE pump waits on the real engine
            //     timers (E2E stack boosts GrowthRate); the unit world
            //     declares the grow wait's outcome. This also makes the pump
            //     immune to a live TaskManager tick (seeded by a sibling
            //     suite) that would otherwise fire the armed growth chain
            //     and run the crop's full lifecycle mid-run.
            var world = character.ParentWorld;
            if (world == null)
                return null;
            var doodad = DoodadManager.Instance.Create(world, 0, CropHarvestLoopTests.PotatoDoodadId, character, true);
            doodad.IsPersistent = false;
            // The rig's DoodadManager object-id mock returns the FIXED id
            // 0x200000 for every doodad — six crops sharing one id would
            // first-wins in the world registry and only one could ever be
            // found. Re-assign a unique objId (the same fixture pattern as
            // HeadlessSession.SpawnNpc) so every provisioned crop resolves.
            doodad.ObjId = NextCropObjId();
            doodad.Transform = character.Transform.CloneDetached(doodad);
            doodad.Transform.Local.SetPosition(position.X, position.Y, position.Z);
            doodad.PlantTime = DateTime.UtcNow;
            // Mature phase directly (the setter loads the chain's funcs from
            // the seeded DoodadManager — the same pattern as
            // PlacePackDoodad). No InitDoodad: it would re-arm growth tasks
            // (tick race) and reset the phase to the seedling start group.
            doodad.FuncGroupId = CropHarvestLoopTests.MaturePhase;
            // Headless registry bypass (the CreateActor/PlacePackDoodad
            // pattern — the same one the ACCEPTED pack rig uses, proven
            // stable under full-suite singleton churn): pin the world
            // backing fields directly and register through the public
            // AddObject surface, instead of the
            // Transform.set_InstanceId → WorldManager.GetWorld resolution
            // chain that a sibling suite's singleton swap can break.
            typeof(AAEmu.Game.Models.Game.World.GameObject)
                .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(doodad, world);
            typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
                .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(doodad.Transform, world.Id);
            world.AddObject(doodad);
            world.SpawnManager?.AddPlayerDoodad(doodad);
            return doodad.ObjId;
        }

        private static uint s_nextCropObjId = 0x6000_0000;

        private static uint NextCropObjId() => s_nextCropObjId++;
    }
}
