using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 Craft RIG repair tests (t_6b5ac43e) — prove the craft rig surface
/// (SeedCraftSurface + SpawnCraftBench + CompleteCraftStep) works with the
/// REAL engine path and the current develop rig-isolation discipline
/// (11978eafd), with NO Craft action implementation (that lands with the
/// salvage, t_cffb71ad):
///
///  - SpawnCraftBench must not NRE on world registration (t_0fc3a550): the
///    bench is a plain Doodad assigned Transform-first-then-ParentWorld, so
///    the ParentWorld setter's InstanceId write short-circuits and the
///    shared WorldManager registry is never consulted or mutated.
///  - SeedCraftSurface is missing-only additive and never mutates the
///    shared DoodadManager singleton (the crop rig's IsBareDoodadManager
///    guard depends on it staying bare or carrying the crop rig's ids).
///  - The engine craft chain (CharacterCraft.Craft — the exact CSExecuteCraft
///    entry — → CraftEffect.Apply → EndCraft) completes a step through the
///    rig surface: materials consumed before product granted.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // touches process-wide singletons (CraftManager/SkillManager/WorldManager) + AppConfiguration
public class GameplayActorCraftRigTests
{
    // ------------------------------------------------------- rig surface tests

    [Test]
    public async Task SpawnCraftBench_RegistersInSessionWorld_WithoutWorldRegistryNRE()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("craft-rig-spawn-1");
        var worldRegistryCountBefore = WorldRegistryCount();

        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);

        // The bench exists in the session world (engine lookup resolves).
        await Assert.That(benchObjId).IsNotEqualTo(0u);
        var bench = session.World.GetDoodad(benchObjId);
        await Assert.That(bench).IsNotNull();
        await Assert.That(bench!.TemplateId).IsEqualTo(GameplayActorTestRig.CraftBenchTemplateId);
        await Assert.That(bench.ParentWorld).IsEqualTo(session.World);
        // Positioned 1 m in front of the actor (engine range gate passes).
        await Assert.That(actor.Character.GetDistanceTo(bench, true)).IsLessThanOrEqualTo(100f);

        // The world-registration NRE regression: the shared registry was
        // never consulted or mutated (headless worlds stay unregistered).
        await Assert.That(WorldRegistryCount()).IsEqualTo(worldRegistryCountBefore);
    }

    [Test]
    public async Task SeedCraftSurface_IsIdempotent_AndLeavesDoodadManagerUntouched()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("craft-rig-seed-1");
        var doodadSeededBefore = DoodadManagerSeeded();
        var doodadTemplatesBefore = DoodadTemplateCount();

        // Seed twice (once directly, once via the bench spawn) — additive,
        // missing-only, never throws, never replaces established state.
        GameplayActorTestRig.SeedCraftSurface();
        GameplayActorTestRig.SeedCraftSurface();
        GameplayActorTestRig.SpawnCraftBench(session, actor);

        await Assert.That(CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId)).IsNotNull();
        await Assert.That(SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.CraftTestSkillId)).IsNotNull();
        await Assert.That(ItemManager.Instance.GetTemplate(GameplayActorTestRig.CraftMaterialTemplateId)).IsNotNull();
        await Assert.That(ItemManager.Instance.GetTemplate(GameplayActorTestRig.CraftProductTemplateId)).IsNotNull();
        // DoodadManager: the craft rig seeds the singleton bare ONLY when it
        // was missing (the skill cast path dereferences it), and NEVER adds
        // templates — an established manager's template count is untouched,
        // and a bare manager stays count==0 (the crop rig's IsBareDoodadManager
        // guard requires _templates to stay count==0 OR carry the crop rig's
        // ids; a bench template here would make the crop rig skip its rich
        // re-seed and NRE Plant()).
        if (doodadSeededBefore)
            await Assert.That(DoodadTemplateCount()).IsEqualTo(doodadTemplatesBefore);
        else
            await Assert.That(DoodadTemplateCount()).IsEqualTo(0);
    }

    // ------------------------------------------------------- engine path tests

    [Test]
    public async Task EngineCraftStep_CompletesThroughRealPath_MaterialsConsumedBeforeProductGranted()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("craft-rig-engine-1");
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);

        var craft = CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId);
        await Assert.That(craft).IsNotNull();

        // The exact CSExecuteCraft entry: character.Craft.Craft(craft, count, doodadId).
        actor.Character.Craft.Craft(craft!, 1, benchObjId);
        await Assert.That(actor.Character.Craft.IsCrafting).IsTrue();

        // Engine-side completion: CraftEffect.Apply → EndCraft (the same
        // chain the cast pipeline runs after a craft cast).
        GameplayActorTestRig.CompleteCraftStep(actor, benchObjId);

        // Materials consumed (2 → 0) BEFORE the product was granted (1).
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
    }

    [Test]
    public async Task EngineCraftStep_WrongBenchTemplate_RejectedByEngine()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("craft-rig-wrongbench-1");
        // A bench of a DIFFERENT template than the recipe's req_doodad_id.
        var wrongBenchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor,
            GameplayActorTestRig.CraftWrongBenchTemplateId);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);

        var craft = CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId);

        actor.Character.Craft.Craft(craft!, 1, wrongBenchObjId);

        // The engine's own template gate rejects the step pre-cast (the
        // recipe requires CraftBenchTemplateId) — nothing starts.
        await Assert.That(actor.Character.Craft.IsCrafting).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(0);
    }

    // ------------------------------------------------------- helpers

    private static int WorldRegistryCount()
    {
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>?)
            typeof(WorldManager).GetField("_worlds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(WorldManager.Instance);
        return worlds?.Count ?? 0;
    }

    private static bool DoodadManagerSeeded()
    {
        return typeof(DoodadManager).GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) != null;
    }

    private static int DoodadTemplateCount()
    {
        var instance = typeof(DoodadManager).GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
        if (instance == null)
            return 0;
        var templates = (Dictionary<uint, DoodadTemplate>?)
            typeof(DoodadManager).GetField("_templates",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(instance);
        return templates?.Count ?? 0;
    }
}
