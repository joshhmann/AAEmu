using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 Harvest contract tests (t_f9b19050) — the economy action through
/// the REAL engine path: the same doodad.Use(caster, harvestSkill) chain
/// the client's harvest interaction drives on a mature crop (potato loop,
/// canonical 1.2 ids from CropHarvestLoopTests).
///
/// Acceptance:
///  - Full lifecycle + §17 failure taxonomy (RejectedAction for world/range/
///    despawn/engine-refusal, StateTransition for not-harvestable-in-phase
///    and for duplicate-key retries).
///  - Retry tests prove the harvest does NOT execute twice: a same-key
///    retry is rejected pre-flight by the ActorEffectLedger (no Running
///    transition), and a fresh-key retry after a successful harvest finds
///    the crop deleted by the final phase — no duplicate crop yield.
///  - Every request emits the structured trace record (ActorAuditRecord,
///    ToJson shape {trace_id, actor_id, action, target_id, requested_at,
///    started_at, completed_at, result, state_changes}).
/// Contract tests only — no controller involved (spec §17 split).
/// </summary>
[NotInParallel] // touches process-wide singletons (CropHarvestLoopRig.Seed) + AppConfiguration
public class GameplayActorHarvestTests
{
    private WorldConfig _previousWorldConfig;

    /// <summary>
    /// Pre-class snapshot of HousingManager._houses, restored at class end.
    /// The shared crop rig (CropHarvestLoopRig.Seed → SeedHousingManager)
    /// registers house 77 (AccountId 0) into the process-wide registry; rig
    /// characters also default to AccountId 0, so the sibling HouseBuild
    /// class's engine-path pre-flight (CalculateBuildingTaxInfo) then counts
    /// house 77 as the account's FIRST house and every build on a default
    /// (0-money) character rejects with "not enough money for the house tax
    /// (300000 required)" (t_234da01a interference finding — the tax detail
    /// is the 1:1 reproduction). Restoring the registry keeps this class
    /// order-safe: HouseBuild's rig re-seeds an empty registry (tax 0) and
    /// the crop family re-adds house 77 via its own Seed on later runs.
    /// </summary>
    private static Dictionary<uint, House> _housesBeforeClass;

    [Before(Class)]
    public static void BeforeClass()
    {
        if (GameplayActorTestRig.SingletonSeeded(typeof(Singleton<HousingManager>)))
            _housesBeforeClass = HousingManager.Instance.GetAllHouses().ToDictionary(h => h.Id);
        else
            _housesBeforeClass = null; // this class seeds it — restore to empty
    }

    [After(Class)]
    public static void AfterClass()
    {
        if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<HousingManager>)))
            return;
        var housesField = typeof(HousingManager).GetField("_houses",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        housesField?.SetValue(HousingManager.Instance, _housesBeforeClass ?? new Dictionary<uint, House>());
    }

    /// <summary>
    /// The headless session world is created with instance id 1 for EVERY
    /// actor (HeadlessSession.CreateWorld). WorldManager._worlds is keyed by
    /// that id and TryAdd is first-wins: the first test's world registers,
    /// every later test's world silently fails to register. The actor's
    /// Harvest resolves the crop through Character.ParentWorld.GetDoodad,
    /// and the rig's doodad transform resolves ParentWorld through
    /// WorldManager.GetWorld(instanceId) — a collision sends the crop to
    /// test 1's world and every later lookup fails. Assign a unique instance
    /// id per test (same high-base pattern as GameplayActorTestRig's
    /// _nextWorldInstanceId).
    ///
    /// NOTE (t_234da01a): the base is 0x6000_0000, NOT 0x4000_0000 — the
    /// sibling M5.1 rigs own 0x4000_0000 (Plant) and 0x5000_0000
    /// (HouseBuild) with process-wide first-wins registration; sharing a
    /// base would let this class's worlds win the registry slots and strand
    /// every later Plant/HouseBuild test's world (crops/houses land in the
    /// wrong world).
    /// </summary>
    private static uint _nextWorldInstanceId = 0x6000_0000;

    [Before(Test)]
    public void SetUp()
    {
        // DoodadFuncGrowth reads World.GrowthRate (same pattern as
        // CropHarvestLoopTests); provide a benign config for this class.
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();

        CropHarvestLoopRig.Seed();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task Harvest_MatureCrop_YieldsPackAndResetsPlot()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-1");
        var crop = PlantMatureCrop(actor, session);

        var request = actor.Harvest(crop.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // Canonical pack 6452: 2-4 potato + 1 golden potato + 1 seed.
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsLessThanOrEqualTo(4);
        await Assert.That(BagCount(actor, CropHarvestLoopTests.GoldenPotatoItemId)).IsEqualTo(1);
        // Yield measured by the actor (result payload) matches the pack size.
        var yieldResult = request.Result is int yield ? yield : -1;
        await Assert.That(yieldResult).IsGreaterThanOrEqualTo(4);
        // Plot reset: the final phase deleted the crop.
        await Assert.That(session.World.GetDoodad(crop.ObjId)).IsNull();
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task Harvest_ImmatureCrop_RejectedStateTransition_NoYield()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-2");
        // Plant but do NOT grow — the crop sits in the seedling phase (4379),
        // whose funcs (watering 13625 / uproot 13789) lead to no loot phase.
        StockSeed(actor);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        PositionAtActor(actor, crop);
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SeedlingPhase);

        var request = actor.Harvest(crop.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("not harvestable in phase")).IsTrue();
        // Nothing yielded, crop untouched.
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsEqualTo(0);
        await Assert.That(session.World.GetDoodad(crop.ObjId)).IsNotNull();
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.SeedlingPhase);
    }

    [Test]
    public async Task Harvest_UnknownDoodad_RejectedWithRejectedAction()
    {
        var (actor, _) = CreateActorOnUniqueWorld("m51-harvest-3");

        var request = actor.Harvest(0x7FFF_FFFF);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Harvest);
    }

    [Test]
    public async Task Harvest_OutOfRange_RejectedWithRejectedAction()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-4");
        var crop = PlantMatureCrop(actor, session);
        // Move the crop beyond interaction range (the actor stays at origin).
        crop.Transform.Local.SetPosition(new Vector3(1000f, 0f, 0f));

        var request = actor.Harvest(crop.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("out of interaction range")).IsTrue();
        // Engine never entered: no yield, phase unchanged.
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsEqualTo(0);
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);
    }

    [Test]
    public async Task Harvest_DespawnScheduled_RejectedBeforeEngine()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-5");
        var crop = PlantMatureCrop(actor, session);
        crop.Despawn = DateTime.UtcNow.AddSeconds(5);

        var request = actor.Harvest(crop.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("scheduled for despawn")).IsTrue();
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsEqualTo(0);
    }

    [Test]
    public async Task Harvest_RetrySameKey_RejectedPreFlight_NoDoubleYield()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-6");
        var crop = PlantMatureCrop(actor, session);

        var original = actor.Harvest(crop.ObjId, idempotencyKey: "harvest:potato-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        var potatoAfterFirst = BagCount(actor, CropHarvestLoopTests.PotatoItemId);

        // Controller-level retry with the SAME key (timeout ambiguity): the
        // ActorEffectLedger refuses pre-flight — no Running transition, so
        // the engine path is never re-entered and no second yield can land.
        var retry = actor.Harvest(crop.ObjId, idempotencyKey: "harvest:potato-1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsEqualTo(potatoAfterFirst);
        // The refusal never replaced the locked outcome: a THIRD retry is still refused.
        var third = actor.Harvest(crop.ObjId, idempotencyKey: "harvest:potato-1");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task Harvest_FreshKeyRetryAfterSuccess_Rejected_CropGone_NoDoubleYield()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-7");
        var crop = PlantMatureCrop(actor, session);

        var original = actor.Harvest(crop.ObjId);
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        var potatoAfterFirst = BagCount(actor, CropHarvestLoopTests.PotatoItemId);
        var goldenAfterFirst = BagCount(actor, CropHarvestLoopTests.GoldenPotatoItemId);
        var seedAfterFirst = BagCount(actor, CropHarvestLoopTests.PotatoSeedItemId);

        // A fresh-key retry (no caller correlation) resolves the crop object
        // id — the engine deleted the doodad on the final phase, so the world
        // lookup fails and nothing is granted a second time.
        var retry = actor.Harvest(crop.ObjId, idempotencyKey: "harvest:potato-fresh-key");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("not found in world")).IsTrue();
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsEqualTo(potatoAfterFirst);
        await Assert.That(BagCount(actor, CropHarvestLoopTests.GoldenPotatoItemId)).IsEqualTo(goldenAfterFirst);
        await Assert.That(BagCount(actor, CropHarvestLoopTests.PotatoSeedItemId)).IsEqualTo(seedAfterFirst);
        // Ledger correlation: the fresh-key retry's own outcome is a Rejected
        // (the crop was gone) — the key maps to a non-executing attempt.
        var retryRecord = actor.FindByKey("harvest:potato-fresh-key");
        await Assert.That(retryRecord).IsNotNull();
        await Assert.That(retryRecord!.Result).IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task Harvest_TraceRecord_EmittedWithFullShape()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-harvest-8");
        var crop = PlantMatureCrop(actor, session);

        var request = actor.Harvest(crop.ObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Harvest);
        await Assert.That(record.TargetId).IsEqualTo(crop.ObjId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
        // Structured wire shape (ROADMAP M5 field names).
        var json = record.ToJson();
        await Assert.That(json).Contains("\"trace_id\"");
        await Assert.That(json).Contains("\"actor_id\"");
        await Assert.That(json).Contains("\"action\":\"Harvest\"");
        await Assert.That(json).Contains("\"target_id\"");
        await Assert.That(json).Contains("\"requested_at\"");
        await Assert.That(json).Contains("\"started_at\"");
        await Assert.That(json).Contains("\"completed_at\"");
        await Assert.That(json).Contains("\"result\":\"Completed\"");
        await Assert.That(json).Contains("\"state_changes\"");
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Creates an actor on a session world with a UNIQUE world instance id
    /// (see <see cref="_nextWorldInstanceId"/>): the world's Id is patched to
    /// the unique value and the character's transform instance id follows, so
    /// the rig's doodad ParentWorld resolution (WorldManager.GetWorld) lands
    /// on THIS world instead of the first test's. The world Id backing field
    /// is compiler-generated for the init-only primary-ctor property.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);

        var uniqueWorldId = _nextWorldInstanceId++;
        var worldIdField = typeof(WorldInstance).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        worldIdField?.SetValue(session.World, uniqueWorldId);
        // Character transform instance id must match the patched world id so
        // the doodad clone resolves ParentWorld through GetWorld(uniqueId).
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, uniqueWorldId);

        return (actor, session);
    }

    /// <summary>Stocks one seed, plants, repositions the crop onto the actor,
    /// and grows it to the mature phase through the REAL scheduled growth
    /// tasks (no wall clock).</summary>
    private static Doodad PlantMatureCrop(GameplayActor actor, HeadlessSession session)
    {
        StockSeed(actor);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        PositionAtActor(actor, crop);
        // Seedling → small → mature via the armed growth tasks.
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as AAEmu.Game.Models.Tasks.Doodads.DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        return crop;
    }

    private static void StockSeed(GameplayActor actor)
        => actor.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);

    /// <summary>The actor spawns at the world origin; the rig plants at
    /// (1000,1000,100). Move the crop onto the actor so the interaction-range
    /// gate passes (the B1 Interact tests use the same repositioning).</summary>
    private static void PositionAtActor(GameplayActor actor, Doodad crop)
        => crop.Transform.Local.SetPosition(actor.Character.Transform.World.Position);

    private static int BagCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);
}
