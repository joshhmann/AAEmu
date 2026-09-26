using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// NeedsBrainPlanner: the LIVE ADAPTER against the real headless actor — the soil
/// surface read through the SHARED bounded spiral, the seed count from the bag,
/// the tracked crop's resolve + the shared crop rule + the shared maturity read,
/// the crop distance, and the destination-authority contract.
///
/// The pure chain is covered by <c>NeedsBrainTests</c>; what is pinned here is the
/// adapter's own honesty contract: an unreadable surface reads as the named hold
/// (never a fabricated destination), the DESTINATION is always the bounded spiral's
/// own output and a caller-supplied destination is ignored unless the caller
/// explicitly says the spiral already ran, a live immature crop is waited on, a
/// mature crop is harvested, and the harvest-just-landed flag reaches the decision.
/// </summary>
[NotInParallel]
public class NeedsBrainPlannerTests
{
    private static readonly Vector3 Here = new(1000f, 1000f, 100f);

    /// <summary>
    /// Headless worlds all carry instance id 1, so the shared
    /// <c>WorldManager._worlds</c> registry would let two tests' doodads land in one
    /// world and resolve each other's crops (the <c>NeedsFarmModuleTests</c>
    /// unique-world convention). Each test gets its own registered world, identity
    /// guarded on the way out so a sibling lane's same-id world is never dropped.
    /// </summary>
    private static uint s_nextWorldId = 0xC000_0000;

    private readonly List<WorldInstance> _registeredWorlds = [];

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        CropHarvestLoopRig.Seed();
    }

    [After(Test)]
    public void TearDown()
    {
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(WorldManager.Instance);
        foreach (var world in _registeredWorlds)
        {
            if (worlds?.TryGetValue(world.Id, out var registered) == true && ReferenceEquals(registered, world))
                worlds.TryRemove(world.Id, out _);
        }
        _registeredWorlds.Clear();
        ExecutionBoundary.ResetForTest();
    }

    /// <summary>Actor on its OWN registered world, so its crops resolve only through it.</summary>
    private (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        typeof(WorldInstance)
            .GetField("<Id>k__BackingField",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(WorldManager.Instance);
        if (worlds != null
            && !worlds.TryAdd(session.World.Id, session.World)
            && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorlds.Add(session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, session.World.Id);
        return (actor, session);
    }

    private static NeedsBrainPlanner.Request Request(
        Vector3 position = default,
        uint seedTemplate = BotRoamStepExecutor.NeedsFarmSeedItemTemplateId)
        => NeedsBrainPlanner.Request.Bare(position == default ? Here : position, seedTemplate);

    // ------------------------------------------------------------ the fail-closed surface

    [Test]
    public async Task UnreadableSurface_ResolvesNoDestinationAndHoldsNamed()
    {
        // The soil probe answers NOTHING (the surface could not be read). The
        // adapter must NOT fabricate a destination — the whole farm loop holds with
        // a named reason instead of walking the bot at a made-up point.
        var (actor, _) = CreateActorOnUniqueWorld("needs-soil-unreadable");
        GameplayActorTestRig.SetPosition(actor, Here);
        GameplayActorTestRig.GrantItem(actor, BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            SoilProbe = (_, _) => null,
            GroundZ = (_, candidate) => candidate
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.SoilUnreadable);
        await Assert.That(prepared.Decision.HasDestination).IsFalse();
        await Assert.That(prepared.SoilDestination).IsNull();
        await Assert.That(prepared.SoilProbesReadable).IsEqualTo(0);
        await Assert.That(prepared.HasLeg).IsFalse();
        await Assert.That(prepared.SoilUnreadable).IsTrue();
    }

    [Test]
    public async Task CallerSuppliedDestination_IsIgnoredUnlessTheSpiralAlreadyRan()
    {
        // The destination authority contract: with SoilDestinationResolved FALSE
        // (the ordinary caller) a caller-supplied destination is ignored — the
        // spiral runs and produces the point. Only an explicit "the spiral already
        // ran" honours the supplied point, so a fixture can map a SURFACE but never
        // invent a DESTINATION.
        var (actor, _) = CreateActorOnUniqueWorld("needs-dest-authority");
        GameplayActorTestRig.SetPosition(actor, Here);
        GameplayActorTestRig.GrantItem(actor, BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);
        var fake = new Vector3(9999f, 9999f, 9999f);
        var realSoil = Here + new Vector3(10f, 0f, 0f);

        var ignored = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            SoilProbe = (_, candidate) => MathUtil.CalculateDistance(candidate, realSoil, false) <= 1f,
            GroundZ = (_, candidate) => candidate,
            SoilDestination = (_, _) => fake,
            SoilDestinationResolved = false
        });

        await Assert.That(ignored.IsTravel).IsTrue();
        await Assert.That(ignored.Decision.Verb).IsEqualTo(NeedsVerb.MoveTo);
        await Assert.That(ignored.SoilDestination).IsEqualTo(realSoil);
        await Assert.That(ignored.SoilDestination).IsNotEqualTo(fake);
    }

    [Test]
    public async Task SeedOffSoil_WithReadableCandidatesButNoSoil_SeeksSoil()
    {
        // The CONTRAST that pins the fail-closed direction: the probe ANSWERS every
        // candidate and none is plantable — a genuine absence, named "no soil
        // nearby", which the caller may re-resolve later. This is exactly the fact
        // the unreadable-surface case above must NOT claim.
        var (actor, _) = CreateActorOnUniqueWorld("needs-search-empty");
        GameplayActorTestRig.SetPosition(actor, Here);
        GameplayActorTestRig.GrantItem(actor, BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            SoilProbe = (_, _) => false,
            GroundZ = (_, candidate) => candidate
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.SeekSoil);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.NoSoilNearby);
        await Assert.That(prepared.SoilProbesReadable).IsGreaterThan(0);
        await Assert.That(prepared.SoilUnreadable).IsFalse();
    }

    // ------------------------------------------------------------ the spiral destination

    [Test]
    public async Task SeedOffSoil_WalksADestinationTheBoundedSpiralFound()
    {
        // No fixture-injected destination: the probe maps a surface 20 m out, and
        // the SHARED spiral produces the destination — the verdict asks for the
        // existing MoveTo verb to that point.
        var (actor, _) = CreateActorOnUniqueWorld("needs-seek-spiral");
        GameplayActorTestRig.SetPosition(actor, Here);
        GameplayActorTestRig.GrantItem(actor, BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);
        var soil = Here + new Vector3(20f, 0f, 0f);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            SoilProbe = (_, candidate) => MathUtil.CalculateDistance(candidate, soil, false) <= 1f,
            GroundZ = (_, candidate) => candidate
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.SoilResolved);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(NeedsVerb.MoveTo);
        await Assert.That(prepared.SoilDestination).IsNotNull();
        await Assert.That(prepared.SoilProbesReadable).IsGreaterThan(0);
        await Assert.That(NeedsBrainPlanner.TryGetTravelTarget(prepared, out var target)).IsTrue();
        await Assert.That(MathUtil.CalculateDistance(target, soil, false) <= 1f).IsTrue();
    }

    [Test]
    public async Task OnValidSoil_PlantsInsteadOfWalking()
    {
        var (actor, _) = CreateActorOnUniqueWorld("needs-plant-on-soil");
        GameplayActorTestRig.SetPosition(actor, Here);
        GameplayActorTestRig.GrantItem(actor, BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            SoilProbe = (_, candidate) => MathUtil.CalculateDistance(candidate, Here, false) <= 1f,
            GroundZ = (_, candidate) => candidate
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.Plant);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(NeedsVerb.Plant);
        await Assert.That(prepared.Decision.HasDestination).IsFalse();
        await Assert.That(NeedsBrainPlanner.TryGetTravelTarget(prepared, out _)).IsFalse();
    }

    [Test]
    public async Task NoSeedOnHand_DoesNotResolveSoilAtAll()
    {
        // The seed count gates the soil question: with no seed the adapter must not
        // even run the spiral, and the wake names the seed fact (the buy leg's).
        var (actor, _) = CreateActorOnUniqueWorld("needs-seedless");
        GameplayActorTestRig.SetPosition(actor, Here);
        var probes = 0;

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            SoilProbe = (_, _) => { probes++; return false; },
            GroundZ = (_, candidate) => candidate
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.SeedAbsent);
        await Assert.That(prepared.SoilDestination).IsNull();
        await Assert.That(probes).IsEqualTo(1); // only the underfoot read
    }

    // ------------------------------------------------------------ the crop loop

    [Test]
    public async Task LiveImmatureTrackedCrop_WaitsAndIssuesNoHarvest()
    {
        var (actor, session) = CreateActorOnUniqueWorld("needs-wait-immature");
        GameplayActorTestRig.SetPosition(actor, Here);
        var crop = PlantCrop(actor, session, Here + new Vector3(2f, 0f, 0f));

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            TrackedCropObjId = crop.ObjId,
            TrackedCropTemplateId = crop.TemplateId
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.WaitMaturity);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.CropImmature);
        await Assert.That(prepared.Decision.CropObjId).IsEqualTo(crop.ObjId);
        await Assert.That(prepared.Crop).IsNotNull();
        await Assert.That(prepared.HasLeg).IsFalse();
    }

    [Test]
    public async Task MatureTrackedCrop_HarvestsTheSameTrack()
    {
        // The maturity wait → harvest transition through the adapter: the SAME
        // tracked objId, grown to its mature phase, flips the verdict to Harvest.
        var (actor, session) = CreateActorOnUniqueWorld("needs-harvest-mature");
        GameplayActorTestRig.SetPosition(actor, Here);
        var crop = PlantCrop(actor, session, Here + new Vector3(2f, 0f, 0f));

        var waiting = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            TrackedCropObjId = crop.ObjId,
            TrackedCropTemplateId = crop.TemplateId
        });
        GrowToMature(crop);
        var harvested = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            TrackedCropObjId = crop.ObjId,
            TrackedCropTemplateId = crop.TemplateId
        });

        await Assert.That(waiting.Decision.Verdict).IsEqualTo(NeedsVerdict.WaitMaturity);
        await Assert.That(harvested.Decision.Verdict).IsEqualTo(NeedsVerdict.Harvest);
        await Assert.That(harvested.Decision.Reason).IsEqualTo(NeedsReason.CropMature);
        await Assert.That(harvested.Decision.Verb).IsEqualTo(NeedsVerb.Harvest);
        await Assert.That(harvested.IsHarvest).IsTrue();
        await Assert.That(harvested.Decision.DistanceM).IsLessThanOrEqualTo(NeedsBrain.DefaultHarvestRangeM);
    }

    [Test]
    public async Task MatureTrackedCropOutOfRange_ApproachesWithItsLivePosition()
    {
        var (actor, session) = CreateActorOnUniqueWorld("needs-crop-approach");
        GameplayActorTestRig.SetPosition(actor, Here);
        var crop = PlantCrop(actor, session, Here + new Vector3(30f, 0f, 0f));
        GrowToMature(crop);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            TrackedCropObjId = crop.ObjId,
            TrackedCropTemplateId = crop.TemplateId
        });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.CropApproach);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(NeedsVerb.MoveTo);
        await Assert.That(prepared.Decision.Destination).IsEqualTo(crop.Transform.World.Position);
    }

    [Test]
    public async Task HarvestJustLanded_ReachesTheDecisionThroughTheAdapter()
    {
        var (actor, _) = CreateActorOnUniqueWorld("needs-replant");
        GameplayActorTestRig.SetPosition(actor, Here);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with { HarvestJustLanded = true });

        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.Replant);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.HarvestCompleted);
        await Assert.That(prepared.HasLeg).IsFalse();
    }

    [Test]
    public async Task TrackedCropWithAStaleObjId_FallsThroughToTheSeedArms()
    {
        // A tracked objId the world no longer resolves is provably stale: the
        // adapter drops it and the wake falls through (never a wait on a ghost).
        var (actor, _) = CreateActorOnUniqueWorld("needs-crop-stale");
        GameplayActorTestRig.SetPosition(actor, Here);
        GameplayActorTestRig.GrantItem(actor, BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);

        var prepared = NeedsBrainPlanner.Prepare(actor, Request() with
        {
            TrackedCropObjId = 0xDEAD_BEEF,
            TrackedCropTemplateId = CropHarvestLoopTests.PotatoDoodadId,
            SoilProbe = (_, _) => false,
            GroundZ = (_, candidate) => candidate
        });

        await Assert.That(prepared.Inputs.CropState).IsEqualTo(NeedsCropState.Stale);
        await Assert.That(prepared.Decision.Verdict).IsEqualTo(NeedsVerdict.SeekSoil);
        await Assert.That(prepared.Decision.Reason).IsEqualTo(NeedsReason.NoSoilNearby);
    }

    // ------------------------------------------------------------------ helpers

    private static Doodad PlantCrop(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        CropHarvestLoopRig.Seed();
        if (actor.Character.Inventory.GetItemsCount(CropHarvestLoopTests.PotatoSeedItemId) == 0)
            GameplayActorTestRig.GrantItem(actor, CropHarvestLoopTests.PotatoSeedItemId, 3);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(position);
        return crop;
    }

    /// <summary>
    /// Drives the real growth task chain to the mature phase (the deterministic
    /// harness the crop-loop tests use: seedling → small → mature).
    /// </summary>
    private static void GrowToMature(Doodad crop)
    {
        while (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
        {
            if (crop.FuncTask is DoodadFuncGrowthTask growth)
                growth.Execute();
            else
                break;
        }
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
    }
}
