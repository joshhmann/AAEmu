using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using GoapBotContext = AAEmu.Game.Core.Managers.Bots.Goap.BotContext;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Housing;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Step 7 focus: the ONE homestead action bound to real object identity —
/// claim/place plot (<see cref="SurveyAndPlacePlotAction"/> →
/// <c>GameplayActor.BuildHouse(267, 15596, resolved-zone-position)</c> →
/// <c>HousingManager.Build</c>).
///
/// What this proves (layer A, contract):
///  1. the canonical identity is real, not a stand-in — housing 267 resolves in
///     compact.sqlite3 and design item 15596 is the item_housings row that maps
///     to it;
///  2. the action's request is the ordinary engine path against a RESOLVED
///     position (no fixed target id), and the engine's own postconditions hold
///     (house registered under the bot, construction step 0);
///  3. the design source is the step-3 opt-in kit only — the action's own
///     fixture override layer stays closed on a production run;
///  4. negatives cannot falsely succeed and consume nothing on refusal:
///     missing design, unknown housing id, occupied position, retry.
///
/// Layer L (live authenticated server) rides the E2E lane; this class is A only.
/// H (human feel) is UNKNOWN.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HomesteadClaimPlacePlotTests
{
    // Canonical identities (compact.sqlite3, AAEmu.Game/Data):
    //   housings row 267 → name '밀짚모자 허수아비 텃밭', category 16, garden_radius 4.0,
    //     taxation_id 8, heavy_tax 't', one housing_build_steps row (step 0, skill 18553)
    //   item_housings row (id 58): item_id 15596 → design_id 267
    //   taxations row 8 → tax 50,000 copper ('소형 텃밭 (8m x 8m)')
    private const uint ScarecrowHousingId = 267;
    private const uint ScarecrowDesignItemId = 15596;
    private const uint ScarecrowPlotWeeklyTaxCopper = 50_000;
    private const uint ScarecrowPlotBuildSkillId = 18553;

    private const uint SolzreedZoneKey = 9;
    private const uint SolzreedZoneId = 9;
    private const FactionsEnum SolzreedFaction = FactionsEnum.NuiaAlliance;

    /// <summary>Claim position inside the canonical w_solzreed_1 zone (the rig world
    /// grid is 2x2 cells; the live-corridor fallback coordinate is outside it, so the
    /// test supplies the zone-resolved position the action's own resolver returns).</summary>
    private static readonly Vector3 PlotPosition = new(1000f, 1000f, 100f);

    private WorldConfig _previousWorldConfig;
    private object _previousHousingGameData;
    private bool _previousTaxItem;
    private readonly List<WorldInstance> _registeredWorlds = [];
    private readonly List<uint> _houseIdsAdded = [];
    private HashSet<uint> _houseIdsAtSetup = [];

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 14 };

        GameplayActorTestRig.SeedHouseBuildSurface();
        // Real tax branch: certificates, not gold — the same branch a live
        // server runs (FeaturesManager default fset has taxItem set).
        _previousTaxItem = FeaturesManager.Fsets.Check(AAEmu.Game.Models.Game.Features.Feature.taxItem);
        FeaturesManager.Fsets.Set(AAEmu.Game.Models.Game.Features.Feature.taxItem, true);

        LoadRealHousingGameData();
        EnsureRequiredItemTemplates();
        _houseIdsAtSetup = HousingManager.Instance.GetAllHouses().Select(h => h.Id).ToHashSet();
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        FeaturesManager.Fsets.Set(AAEmu.Game.Models.Game.Features.Feature.taxItem, _previousTaxItem);
        UnregisterWorlds();
        MySQL.SetConfiguration(null);
        AppConfiguration.Instance.World = _previousWorldConfig;
        typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, _previousHousingGameData);
        RemoveHouses();
    }

    // ============================================================ canonical identity (no stand-in)

    [Test]
    public async Task CanonicalIdentity_Housing267_AndDesignItem15596_Resolve_FromCompactSqlite()
    {
        var template = HousingGameData.Instance.GetTemplate(ScarecrowHousingId);

        await Assert.That(template).IsNotNull();
        await Assert.That(template!.CategoryId).IsEqualTo(16u);
        await Assert.That(template.GardenRadius).IsEqualTo(4f);
        await Assert.That(template.Taxation).IsNotNull();
        await Assert.That(template.Taxation!.Tax).IsEqualTo(ScarecrowPlotWeeklyTaxCopper);
        // One construction step driven by the canonical build skill — the plot is
        // a real placeable design, not a decoration template.
        await Assert.That(template.BuildSteps.Count).IsEqualTo(1);
        await Assert.That(template.BuildSteps.Values.Single().SkillId).IsEqualTo(ScarecrowPlotBuildSkillId);
        // The design ITEM the claim consumes is the one item_housings maps to 267.
        await Assert.That(CanonicalDesignItemFor(ScarecrowHousingId)).IsEqualTo(ScarecrowDesignItemId);
    }

    [Test]
    public async Task ConstructPlotAction_LaborRequirement_MatchesCanonicalConsumeLp()
    {
        // The construct action gates on ConstructPlotAction.RequiredLabor; the
        // engine charges the skill's own consume_lp in Skill.EndSkill. If the two
        // drift the planner admits a construction the engine will not pay for (or
        // reserves the wrong amount), so the constant is pinned to the canonical
        // data row rather than to a literal.
        uint consumeLp;
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT consume_lp FROM skills WHERE id = $skill";
            cmd.Parameters.AddWithValue("$skill", ScarecrowPlotBuildSkillId);
            var value = cmd.ExecuteScalar();
            await Assert.That(value).IsNotNull();
            consumeLp = Convert.ToUInt32(value);
        }

        await Assert.That(consumeLp).IsEqualTo((uint)ConstructPlotAction.RequiredLabor);
    }

    [Test]
    public async Task ConstructPlotAction_WithoutOwnedFrame_DispatchesNoRequest()
    {
        // The action's target is its OWN live house frame. With no frame there is
        // nothing legal to construct, so it must dispatch NO request — a bare
        // Interact(skillId) stand-in would send the construction skill as if it
        // were an object id, and any engine-side resolution of that would be an
        // accident rather than a bound target.
        var (actor, _, runtime, _) = CreateKittedActor("construct-noframe", grantKit: false);

        var request = new ConstructPlotAction().CreateActorRequest(runtime, actor);

        await Assert.That(request).IsNull();
        await Assert.That(ConstructPlotAction.ResolveOwnedUnfinishedFrame(actor.Character.Id)).IsNull();
    }

    [Test]
    public async Task ConstructPlotAction_OwnedFrame_TargetsTheFrameObjIdWithTheBuildSkill()
    {
        // With a real unfinished frame the request must carry the FRAME's ObjId as
        // its target and the canonical build skill as its skill — the identity the
        // engine's Interact resolves and the skill pipeline then executes.
        var (actor, _, runtime, _) = CreateKittedActor("construct-frame");
        AddRegisteredHouse(actor.Character, actor.Character.Id, PlotPosition, ScarecrowHousingId);
        var frame = ConstructPlotAction.ResolveOwnedUnfinishedFrame(actor.Character.Id);
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.CurrentStep).IsEqualTo(0);

        var request = new ConstructPlotAction().CreateActorRequest(runtime, actor);

        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Action).IsEqualTo(ActorActionType.Interact);
        await Assert.That(request.TargetId).IsEqualTo(frame.ObjId);
        await Assert.That(request.SkillId).IsEqualTo(ScarecrowPlotBuildSkillId);
    }

    [Test]
    public async Task RequiredCertificates_DeriveFromCanonicalTax_NotALiteral()
    {
        // one week (50,000) + deposit (2 × 50,000) = 150,000 copper at the
        // engine's own 10,000-copper certificate value = 15 certificates.
        var required = AcquireScarecrowAction.RequiredFirstPlacementTaxCertificates();

        await Assert.That(required).IsEqualTo(15);
        await Assert.That(required * AcquireScarecrowAction.TaxCertificateCopperValue)
            .IsGreaterThanOrEqualTo((int)ScarecrowPlotWeeklyTaxCopper * 3);
    }

    // ============================================================ positive path — the real action

    [Test]
    public async Task SurveyAndPlacePlot_KittedBot_PlacesPlotThroughRealEnginePath_WithLivePostconditions()
    {
        var (actor, session, runtime, context) = CreateKittedActor("claim-happy");
        var designBefore = GameplayActorTestRig.BagCount(actor, ScarecrowDesignItemId);
        var certsBefore = GameplayActorTestRig.BagCount(actor, AcquireScarecrowAction.TaxCertificateTemplateId);
        var moneyBefore = actor.Character.Money;
        await Assert.That(designBefore).IsEqualTo(1);
        await Assert.That(certsBefore).IsEqualTo(AcquireScarecrowAction.RequiredFirstPlacementTaxCertificates());

        // The ACTION builds the request (not the test): resolved zone position,
        // canonical design pair — prove the wiring under test is the one that runs.
        var request = new SurveyAndPlacePlotAction(PlotPosition).CreateActorRequest(runtime, actor);

        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Action).IsEqualTo(ActorActionType.HouseBuild);
        await Assert.That(request.TargetId).IsEqualTo(ScarecrowHousingId);
        await Assert.That(((HouseBuildParams)request.Payload!).Position)
            .IsEqualTo(TravelToHousingZoneAction.ResolveNearestHousingZone(actor.Character, PlotPosition));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed)
            .Because($"failure={request.Failure} detail={request.Detail} changes={string.Join("|", request.StateChanges)}");

        // Engine postconditions: a house row owned by the bot, in construction state.
        var house = HousingManager.Instance.GetAllHouses().Single(h => h.OwnerId == actor.Character.Id);
        await Assert.That(house.TemplateId).IsEqualTo(ScarecrowHousingId);
        await Assert.That(house.CurrentStep).IsEqualTo(0);

        // Conservation: the design item is consumed exactly once and the tax branch
        // (taxItem on) takes exactly the derived certificate count — never gold.
        await Assert.That(GameplayActorTestRig.BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actor, AcquireScarecrowAction.TaxCertificateTemplateId)).IsEqualTo(0);
        await Assert.That(actor.Character.Money).IsEqualTo(moneyBefore);

        // The observation layer the planner reads agrees with the world.
        var observed = new BotWorldStateProvider().Project(runtime, context);
        await Assert.That(observed.Has(BotWorldState.HasLandPlot)).IsTrue();
        await Assert.That(observed.Has(BotWorldState.HasScarecrowDesign)).IsFalse();

        // The action reports Succeeded off that projection, not off its own override.
        var status = new SurveyAndPlacePlotAction(PlotPosition).EvaluateStatus(runtime, observed, request, context);
        await Assert.That(status).IsEqualTo(GoapActionStatus.Succeeded);
    }

    [Test]
    public async Task SurveyAndPlacePlot_WithoutDesign_IsRejectedAndConsumesNothing()
    {
        // The design source is the opt-in kit alone: a bot that never received it
        // cannot claim, and the refusal is terminal (not a Running hang).
        var (actor, _, runtime, _) = CreateKittedActor("claim-nodesign", grantKit: false);

        var request = new SurveyAndPlacePlotAction(PlotPosition).CreateActorRequest(runtime, actor);

        await Assert.That(request!.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("not found in inventory")).IsTrue();
        await Assert.That(HousingManager.Instance.GetAllHouses().Any(h => h.OwnerId == actor.Character.Id)).IsFalse();
        await Assert.That(actor.Character.Money).IsEqualTo(MoneyAfterKit);
    }

    [Test]
    public async Task BuildHouse_UnknownHousingId_IsRejectedAndConsumesNothing()
    {
        var (actor, _, _, _) = CreateKittedActor("claim-badid");

        var request = new GameplayActor(actor.Character)
            .BuildHouse(999_999, ScarecrowDesignItemId, PlotPosition, idempotencyKey: "claim-badid");

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("unknown house design")).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(1);
        await Assert.That(HousingManager.Instance.GetAllHouses().Any(h => h.OwnerId == actor.Character.Id)).IsFalse();
    }

    [Test]
    public async Task BuildHouse_OccupiedPosition_EngineRefuses_DesignNotConsumed()
    {
        var (actor, _, _, _) = CreateKittedActor("claim-occupied");
        // Another account's house already stands on the spot (garden 4.0 + 4.0
        // = 8 m required; distance 0 → overlap).
        AddRegisteredHouse(actor.Character, ownerId: 999_997, PlotPosition, templateId: ScarecrowHousingId);

        var request = new GameplayActor(actor.Character)
            .BuildHouse(ScarecrowHousingId, ScarecrowDesignItemId, PlotPosition, idempotencyKey: "claim-occupied");

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("engine refused")).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(1);
        await Assert.That(HousingManager.Instance.GetAllHouses().Count(h => h.TemplateId == ScarecrowHousingId)).IsEqualTo(1);
    }

    [Test]
    public async Task BuildHouse_SameKeyRetry_RefusedAfterCompletion_NoDuplicateHouse()
    {
        var (actor, _, _, _) = CreateKittedActor("claim-retry");

        // Retry rides the SAME actor instance — the shape the plan runner uses
        // (it re-dispatches through the runtime's actor, whose ledger holds the key).
        var gameActor = new GameplayActor(actor.Character);
        var first = gameActor.BuildHouse(ScarecrowHousingId, ScarecrowDesignItemId, PlotPosition,
            idempotencyKey: "claim-retry-1");
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(0);

        var retry = gameActor.BuildHouse(ScarecrowHousingId, ScarecrowDesignItemId, PlotPosition,
            idempotencyKey: "claim-retry-1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail!.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(HousingManager.Instance.GetAllHouses().Count(h => h.OwnerId == actor.Character.Id)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(0);
    }

    [Test]
    public async Task BuildHouse_FreshKeyRetryWithoutDesign_EngineBackstopRefuses_NoDuplicateHouse()
    {
        // The engine-true backstop: a retry that is NOT key-deduped (new key,
        // e.g. a re-planned dispatch) still cannot place twice because the design
        // item is gone. This is the duplicate-effect guard for the real failure
        // mode (plan reset), not for a caller mistake.
        var (actor, _, _, _) = CreateKittedActor("claim-freshkey");

        var gameActor = new GameplayActor(actor.Character);
        var first = gameActor.BuildHouse(ScarecrowHousingId, ScarecrowDesignItemId, PlotPosition,
            idempotencyKey: "claim-fresh-1");
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        var second = gameActor.BuildHouse(ScarecrowHousingId, ScarecrowDesignItemId, PlotPosition,
            idempotencyKey: "claim-fresh-2");

        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Detail!.Contains("not found in inventory")).IsTrue();
        await Assert.That(HousingManager.Instance.GetAllHouses().Count(h => h.OwnerId == actor.Character.Id)).IsEqualTo(1);
    }

    // ============================================================ fixture boundary

    [Test]
    public async Task ClaimAction_ProductionRun_IgnoresFixtureOverrides()
    {
        // A seeded override with the fixture gate CLOSED (production shape) must
        // not make the claim succeed — the action reads only the live projection.
        var (actor, _, runtime, context) = CreateKittedActor("claim-override", grantKit: false);
        context.Memory.HasScarecrowDesignOverride = true;
        context.Memory.HasTaxCertificatesOverride = true;
        context.Memory.HasLandPlotOverride = true;
        await Assert.That(context.Memory.FixtureOverridesEnabled).IsFalse();

        var request = new SurveyAndPlacePlotAction(PlotPosition).CreateActorRequest(runtime, actor);

        await Assert.That(request!.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(HousingManager.Instance.GetAllHouses().Any(h => h.OwnerId == actor.Character.Id)).IsFalse();
    }

    // ============================================================ helpers

    /// <summary>Money every kitted actor carries (tax is paid in certificates on this branch).</summary>
    private const long MoneyAfterKit = 25_000;

    private (GameplayActor Actor, HeadlessSession Session, PlayerBotRuntime Runtime,
        GoapBotContext Context)
        CreateKittedActor(string name, bool grantKit = true)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        // Hermetic tax identity: account 0 is shared process-wide, so the engine's
        // first-house rule would read other lanes' houses.
        actor.Character.AccountId = NextAccountId();
        RigWorld(session);
        GameplayActorTestRig.WireHouseZone(session, SolzreedZoneKey, new Zone
        {
            Id = SolzreedZoneId,
            Name = "w_solzreed_1",
            FactionId = SolzreedFaction
        });
        GameplayActorTestRig.AttachConnection(actor);
        actor.Character.Connection!.AccountId = actor.Character.AccountId;
        actor.Character.Money = MoneyAfterKit;
        actor.Character.LaborPower = 500;
        actor.Character.Inventory.Bag.Items.Clear();
        if (grantKit)
            HeadlessSession.GrantStarterHomesteadKit(actor.Character);
        return (actor, session, new PlayerBotRuntime(actor.Character, "step7-tests"), new GoapBotContext());
    }

    private static uint s_nextAccountId = 0x00B7_0000;
    private static uint NextAccountId() => s_nextAccountId++;

    private static uint s_nextWorldId = 0x5A00_0000;

    private void RigWorld(HeadlessSession session)
    {
        typeof(WorldInstance).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World)
            && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision at 0x{session.World.Id:X8}");
        _registeredWorlds.Add(session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    private void UnregisterWorlds()
    {
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(WorldManager.Instance)!;
        foreach (var world in _registeredWorlds)
        {
            if (worlds.TryGetValue(world.Id, out var registered) && ReferenceEquals(registered, world))
                worlds.TryRemove(world.Id, out _);
        }
        _registeredWorlds.Clear();
    }

    private void AddRegisteredHouse(Character owner, uint ownerId, Vector3 position, uint templateId)
    {
        var template = HousingGameData.Instance.GetTemplate(templateId);
        var house = new House
        {
            Id = 0x9500u + (uint)_houseIdsAdded.Count + 1,
            ObjId = 0x9500u + (uint)_houseIdsAdded.Count + 1,
            TlId = (ushort)(0x9500 + _houseIdsAdded.Count + 1),
            Template = template,
            TemplateId = template.Id,
            OwnerId = ownerId,
            CoOwnerId = ownerId,
            AccountId = owner.AccountId,
            Name = $"preplaced_{_houseIdsAdded.Count + 1}",
            Permission = HousingPermission.Private,
            AllowRecover = true,
            PlaceDate = DateTime.UtcNow,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
        house.Transform = new Transform(house, null, position, Vector3.Zero);
        house.Transform.InstanceId = owner.ParentWorld.Id;
        house.CurrentStep = template.BuildSteps.Count > 0 ? 0 : -1;

        var manager = HousingManager.Instance;
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        houses[house.Id] = house;
        housesTl[house.TlId] = house;
        _houseIdsAdded.Add(house.Id);
    }

    private void RemoveHouses()
    {
        var manager = HousingManager.Instance;
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        foreach (var id in _houseIdsAdded)
            houses.Remove(id);
        foreach (var id in houses.Keys.Where(id => !_houseIdsAtSetup.Contains(id)).ToList())
            houses.Remove(id);
        foreach (var key in housesTl.Where(kv => !_houseIdsAtSetup.Contains(kv.Value.Id)).Select(kv => kv.Key).ToList())
            housesTl.Remove(key);
        _houseIdsAdded.Clear();
    }

    /// <summary>Reads the canonical item_housings mapping straight from the data file the
    /// engine loads — the design item the claim must consume for this housing id.</summary>
    private static uint CanonicalDesignItemFor(uint housingId)
    {
        using var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT item_id FROM item_housings WHERE design_id = $design";
        cmd.Parameters.AddWithValue("$design", housingId);
        return Convert.ToUInt32(cmd.ExecuteScalar());
    }

    private static string CanonicalDbPath
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(baseDir, "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3")
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new FileNotFoundException("compact.sqlite3 not found in any expected test layout");
        }
    }

    /// <summary>Canonical housing data + the two item templates the claim touches, loaded
    /// through the real loaders (the same join the engine performs at boot).</summary>
    private void LoadRealHousingGameData()
    {
        var field = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousHousingGameData = field?.GetValue(null);
        var gameData = new HousingGameData();
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            gameData.Load(connection);

            if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<TaxationsManager>)))
            {
                var taxations = new TaxationsManager { taxations = new Dictionary<uint, Taxation>() };
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, tax FROM taxations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    taxations.taxations[Convert.ToUInt32(reader.GetValue(0))] = new Taxation
                    {
                        Id = Convert.ToUInt32(reader.GetValue(0)),
                        Tax = Convert.ToUInt32(reader.GetValue(1))
                    };
                }
                GameplayActorTestRig.SeedSingleton(typeof(Singleton<TaxationsManager>), taxations);
            }
        }
        gameData.PostLoad();
        field?.SetValue(null, gameData);
    }

    private static void EnsureRequiredItemTemplates()
    {
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;

        void Ensure(uint id, string name, int maxCount)
        {
            if (!templates.TryGetValue(id, out var template))
            {
                template = new ItemTemplate { Id = id, Name = name, FixedGrade = -1 };
                templates[id] = template;
            }
            template.MaxCount = maxCount;
        }

        Ensure(ScarecrowDesignItemId, "Straw Hat Scarecrow Garden Design", 1);
        Ensure(AcquireScarecrowAction.TaxCertificateTemplateId, "Bound Tax Certificate", 10_000);
    }
}
