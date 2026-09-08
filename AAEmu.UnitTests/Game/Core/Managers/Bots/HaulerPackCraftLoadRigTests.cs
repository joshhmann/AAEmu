using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Units;
 using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C4 hauler/trader v1 slice-1 (ROADMAP M8 living-village contracts) — pack
/// craft → load through <see cref="HaulerPackCraftLoadCycle"/>: craft a trade
/// pack through the real CharacterCraft → CraftEffect chain (the pack lands in
/// the Backpack slot via TryAddNewItem's auto-equip routing), board the cargo
/// vehicle through the real SlaveManager.BindSlave path, load the pack onto
/// the first free cargo point through the real PackVehicleService carried-pack
/// chain (System-container move → DoodadManager.Create → cargo-point snap).
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (phantom pack on missing inputs, craft over an occupied slot, lost pack on
/// refused load, duplicate pack on same-key retry) and PASSES on the composed
/// real paths. Raw engine rejections (unknown recipe, no carried pack, full
/// cargo) are already pinned by GameplayActorCraftTests /
/// GameplayActorLoadPackOntoVehicleTests — these tests pin the COMPOSER's
/// fail-closed order and conservation, not the engine gates.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HaulerPackCraftLoadRigTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Fixture pack recipe: 1 × potato (7992) → 1 × fixture cargo pack
    // (264901) at the rig bench, skill with labor 10 (the
    // EconomyDayCycleScenarioRigTests.SeedFixturePackCraft shape, own ids).
    private const uint PackCraftId = 99_023;
    private const uint PackCraftSkillId = 99_024;
    private const int PackCraftLaborCost = 10;
    private const uint PackMaterialItemId = CropHarvestLoopTests.PotatoItemId;

    private static uint s_nextWorldId = 0x6100_0000; // fresh base: 0x6000 loadpack / 0x6001 housing-race / 0x6002 harvest / 0x8000 farmer

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.SeedCargoPackSurface();
        GameplayActorTestRig.SeedCraftSurface();
        SeedFixturePackRecipe();
        GameplayActorTestRig.SeedItemTemplate(PackMaterialItemId);
        SeedEquipSurface();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        if (_registeredWorld != null)
        {
            var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(WorldManager.Instance)!;
            if (worlds.TryGetValue(_registeredWorld.Id, out var registered) && ReferenceEquals(registered, _registeredWorld))
                worlds.TryRemove(_registeredWorld.Id, out _);
            _registeredWorld = null;
        }
    }

    [Test]
    public async Task CraftLoad_HappyPath_ConservesMaterialsLaborAndPack()
    {
        var (actor, _, benchObjId, slave) = CreateRig("m8c4-t1-happy", 0x3200u);
        GameplayActorTestRig.GrantItem(actor, PackMaterialItemId, 1);

        var result = HaulerPackCraftLoadCycle.Run(actor, Options("m8c4-t1", benchObjId, slave.ObjId), new HaulCraftPump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "pack-craft-completed", "pack-craft-materials-conserved",
                     "pack-craft-product-granted", "pack-craft-labor-conserved",
                     "board-completed", "load-completed", "load-conservation"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Materials: exactly one consumed; labor: exactly the recipe cost.
        await Assert.That(GameplayActorTestRig.BagCount(actor, PackMaterialItemId)).IsEqualTo(0);
        await Assert.That(result.MaterialsConsumed).IsEqualTo(1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - PackCraftLaborCost));
        await Assert.That(result.LaborCharged).IsEqualTo(PackCraftLaborCost);

        // Pack: out of the slot, into the System container, attached with an
        // item link on a cargo point (9-12) — exactly once.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(result.PackItemId)).IsNotNull();
        var attached = slave.AttachedDoodads.FirstOrDefault(d => d.ItemId == result.PackItemId);
        await Assert.That(attached).IsNotNull();
        await Assert.That((int)attached!.AttachPoint).IsGreaterThanOrEqualTo(9);
        await Assert.That(result.AttachPoint).IsNotNull();
        await Assert.That(result.AttachPoint!.Value).IsEqualTo(attached.AttachPoint);
    }

    [Test]
    public async Task Craft_MissingInputs_FailsClosed_NoPhantomPack()
    {
        var (actor, _, benchObjId, slave) = CreateRig("m8c4-t1-noinput", 0x3210u);
        // No materials granted — the recipe needs one potato.

        var result = HaulerPackCraftLoadCycle.Run(actor, Options("m8c4-t1-noinput", benchObjId, slave.ObjId), new HaulCraftPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PACK-CRAFT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Nothing created, nothing consumed, nothing boarded, nothing attached.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(slave.AttachedDoodads.Count).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(actor.Character.ParentWorld!.SlaveManager.GetIsMounted(actor.Character.ObjId, out _)).IsNull();
    }

    [Test]
    public async Task Precheck_OccupiedBackpackSlot_FailsClosed_NoMutation()
    {
        var (actor, _, benchObjId, slave) = CreateRig("m8c4-t1-occupied", 0x3220u);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var incumbent = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!;
        GameplayActorTestRig.GrantItem(actor, PackMaterialItemId, 1);

        var result = HaulerPackCraftLoadCycle.Run(actor, Options("m8c4-t1-occupied", benchObjId, slave.ObjId), new HaulCraftPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PRECHECK");
        // The incumbent pack is untouched, materials unconsumed, labor
        // unspent, nobody boarded, nothing attached.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!.Id).IsEqualTo(incumbent.Id);
        await Assert.That(GameplayActorTestRig.BagCount(actor, PackMaterialItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(slave.AttachedDoodads.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Load_CargoFull_HoldsWithReason_PackRetained()
    {
        var (actor, _, benchObjId, slave) = CreateRig("m8c4-t1-full", 0x3230u);
        for (var i = 0; i < 4; i++)
        {
            slave.AttachedDoodads.Add(new Doodad
            {
                ObjId = 0x3300u + (uint)i,
                AttachPoint = (AttachPointKind)((int)AttachPointKind.Cannon0 + i),
                ItemId = 0x3400u + (uint)i,
                ItemTemplateId = GameplayActorTestRig.CargoPackTemplateId
            });
        }
        GameplayActorTestRig.GrantItem(actor, PackMaterialItemId, 1);

        var result = HaulerPackCraftLoadCycle.Run(actor, Options("m8c4-t1-full", benchObjId, slave.ObjId), new HaulCraftPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("LOAD");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // The crafted pack stayed in the Backpack slot (never deleted, never
        // attached); the four incumbents are untouched.
        var retained = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(retained).IsNotNull();
        await Assert.That(retained!.TemplateId).IsEqualTo(GameplayActorTestRig.CargoPackTemplateId);
        await Assert.That(slave.AttachedDoodads.Count).IsEqualTo(4);
        await Assert.That(result.Criteria.Any(c => c.Name == "load-fail-closed-pack-retained" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task Cycle_SameKeyRetry_NeverDuplicatesPack()
    {
        var (actor, _, benchObjId, slave) = CreateRig("m8c4-t1-retry", 0x3240u);
        GameplayActorTestRig.GrantItem(actor, PackMaterialItemId, 1);

        var first = HaulerPackCraftLoadCycle.Run(actor, Options("m8c4-t1-retry", benchObjId, slave.ObjId), new HaulCraftPump());
        await Assert.That(first.Passed).IsTrue();

        var retry = HaulerPackCraftLoadCycle.Run(actor, Options("m8c4-t1-retry", benchObjId, slave.ObjId), new HaulCraftPump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("PACK-CRAFT");
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        // Exactly one pack in the world: one attachment with the first run's
        // item link, one System-container row, nothing consumed twice.
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId == first.PackItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(first.PackItemId)).IsNotNull();
        await Assert.That(GameplayActorTestRig.BagCount(actor, PackMaterialItemId)).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(100 - PackCraftLaborCost));
    }

    // ------------------------------------------------------------ rig below

    private (GameplayActor actor, HeadlessSession session, uint benchObjId, Slave slave) CreateRig(string name, uint slaveObjId)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, slaveObjId);
        return (actor, session, benchObjId, slave);
    }

    private HaulerPackCraftLoadCycle.HaulerPackCraftLoadOptions Options(string cycle, uint benchObjId, uint slaveObjId)
        => new()
        {
            CycleId = cycle,
            PackCraftId = PackCraftId,
            PackMaterialItemId = PackMaterialItemId,
            PackMaterialAmount = 1,
            BenchObjId = benchObjId,
            SlaveObjId = slaveObjId
        };

    private void RegisterWorld(HeadlessSession session)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(WorldInstance).GetField("<Id>k__BackingField", Flags)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", Flags)!
            .GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorld = session.World;
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>
    /// Seeds the fixture pack recipe: 1 × potato → 1 × fixture cargo pack at
    /// the rig bench (missing-only, the SeedFixtureCraft pattern).
    /// </summary>
    private static void SeedFixturePackRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(PackCraftId))
        {
            crafts[PackCraftId] = new Craft
            {
                Id = PackCraftId,
                SkillId = PackCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = PackMaterialItemId, Amount = 1 }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = GameplayActorTestRig.CargoPackTemplateId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(PackCraftSkillId))
        {
            skills[PackCraftSkillId] = new SkillTemplate
            {
                Id = PackCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = PackCraftLaborCost,
                ActabilityGroupId = 0,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }
    }

    private static void SeedEquipSurface()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var skillManager = SkillManager.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(skillManager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(skillManager, Activator.CreateInstance(dictType));
            }
        }

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        var itemGameData = ItemGameData.Instance;
        if (typeof(ItemGameData).GetField("_itemGradeBuffs", flags)?.GetValue(itemGameData) == null)
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!.SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
    }

    /// <summary>
    /// Headless craft pump: ticks the actor and applies the REAL CraftEffect
    /// once the engine queue is active (the FixtureCyclePump craft half).
    /// </summary>
    private sealed class HaulCraftPump : IHaulCraftPump
    {
        public ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            var applied = false;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(20));
                if (!applied && actor.Character.Craft is { IsCraftQueueActive: true })
                {
                    var bench = actor.Character.ParentWorld?.GetDoodad(benchObjId);
                    var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                    effect.Apply(actor.Character, null, bench, null,
                        new CastSkill(skillId, 0), new EffectSource(), null, DateTime.UtcNow);
                    applied = true;
                }
            }

            return request;
        }
    }
}
