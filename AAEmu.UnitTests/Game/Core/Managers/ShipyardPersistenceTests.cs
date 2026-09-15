using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.StaticValues;
using AaEmuTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Shipyard frame persistence round-trip (slice M): persist → fresh-manager reload →
/// state equality, template-missing rows skipped, ceremony-in-flight rows inert.
/// Uses an in-memory <see cref="IShipyardFrameStore"/> fake — no MySQL/Docker.
/// A full kill-restart E2E proof (DominionRestartPersistenceE2eTests pattern) is follow-up.
/// </summary>
[NotInParallel]
public class ShipyardPersistenceTests
{
    private const uint TemplateId = 91;
    private const ulong FrameId = 7;
    private const uint OwnerId = 4242;
    private const string OwnerName = "shipwright";
    private const uint ZoneId = 42;

    private sealed class FakeFrameStore : IShipyardFrameStore
    {
        public readonly Dictionary<ulong, ShipyardFrameRow> Rows = [];
        public IReadOnlyList<ShipyardFrameRow> LoadAll() => Rows.Values.ToList();
        public void Upsert(ShipyardFrameRow row) => Rows[row.FrameId] = row;
        public void Delete(ulong frameId) => Rows.Remove(frameId);
    }

    private static ShipyardsTemplate BuildTemplate() => new()
    {
        Id = TemplateId,
        Name = "RigClipper",
        MainModelId = 900,
        ItemId = 95001,
        CeremonyAnimTime = 12000,
        OriginItemId = 95000,
        TaxationId = 1,
        ShipyardSteps = new Dictionary<int, ShipyardSteps>
        {
            [0] = new ShipyardSteps { Id = 1, ShipyardId = TemplateId, Step = 0, ModelId = 901, SkillId = 17002, NumActions = 3, MaxHp = 100 },
            [1] = new ShipyardSteps { Id = 2, ShipyardId = TemplateId, Step = 1, ModelId = 902, SkillId = 17003, NumActions = 2, MaxHp = 200 }
        }
    };

    private static ShipyardManager BuildManager(IShipyardFrameStore store, out Mock<ITaskManager> tasks)
    {
        var taskManager = Mock.Of<ITaskManager>();
        taskManager.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).Returns(true);
        tasks = taskManager;
        var worldManager = Mock.Of<IWorldManager>();
        return new ShipyardManager(
            taskManager.Object,
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IShipyardIdManager>().Object,
            worldManager.Object,
            Mock.Of<ITaxationsManager>().Object,
            Mock.Of<ISkillManager>().Object,
            store);
    }

    /// <summary>
    /// Unit.OnZoneChange resolves ZoneManager.Instance, which has no parameterless
    /// ctor (DI-only). Seed an empty instance so zone-bearing reloads degrade to
    /// unknown-zone no-ops instead of throwing; restored after the test.
    /// </summary>
    private static Action SeedZoneManager()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        var zones = new ZoneManager(Mock.Of<IWorldManager>().Object);
        // Fresh ZoneManagers have null lookup dicts until Load; empty ones make
        // unknown-zone lookups no-ops instead of NREs.
        foreach (var name in new[] { "_zoneIdToKey", "_zones", "_groups", "_conflicts", "_groupBannedTags", "_climateElem" })
            typeof(ZoneManager).GetField(name, flags)!.SetValue(zones, Activator.CreateInstance(typeof(ZoneManager).GetField(name, flags)!.FieldType));
        field.SetValue(null, zones);
        return () => field.SetValue(null, previous);
    }

    private static Shipyard BuildLiveFrame(ShipyardsTemplate template)
    {
        // Step 0 (3 actions) done + 1 action into step 1: CurrentStep 1, NumAction 1, CurrentAction 4.
        var frame = new Shipyard { Template = template, Level = 30, Name = OwnerName };
        for (var i = 0; i < 4; i++)
            frame.AddBuildAction();
        frame.Hp = 1234;
        frame.ShipyardData = new ShipyardData
        {
            Id = FrameId,
            TemplateId = TemplateId,
            X = 100.5f,
            Y = 200.25f,
            Z = 10.0f,
            zRot = 1.5f,
            MoneyAmount = 0,
            Actions = 4,
            Type = template.OriginItemId,
            OwnerName = OwnerName,
            Type2 = OwnerId,
            Type3 = FactionsEnum.NuiaAlliance,
            Spawned = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc),
            ObjId = 55,
            Hp = 1234,
            Step = 1
        };
        frame.Transform.ZoneId = ZoneId;
        return frame;
    }

    [Test]
    public async Task PersistReload_RoundTripsBuildState()
    {
        var restoreZone = SeedZoneManager();
        try
        {
            var store = new FakeFrameStore();
            var template = BuildTemplate();
            var manager = BuildManager(store, out _);
            manager._shipyardsTemplate.Add(TemplateId, template);
            manager.PersistShipyard(BuildLiveFrame(template));

            // Fresh manager, same store: the only source is the persisted row.
            var reloaded = BuildManager(store, out _);
            reloaded._shipyardsTemplate.Add(TemplateId, template);
            reloaded.LoadPlacedFrames();

            var frame = reloaded.GetShipyard((uint)FrameId);
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.ShipyardData.TemplateId).IsEqualTo(TemplateId);
            await Assert.That(frame.ShipyardData.Step).IsEqualTo(1);
            await Assert.That(frame.ShipyardData.Actions).IsEqualTo(4);
            await Assert.That(frame.ShipyardData.OwnerName).IsEqualTo(OwnerName);
            await Assert.That(frame.ShipyardData.Type2).IsEqualTo(OwnerId);
            await Assert.That(frame.ShipyardData.Type3).IsEqualTo(FactionsEnum.NuiaAlliance);
            await Assert.That(frame.ShipyardData.X).IsEqualTo(100.5f);
            await Assert.That(frame.ShipyardData.Y).IsEqualTo(200.25f);
            await Assert.That(frame.ShipyardData.Z).IsEqualTo(10.0f);
            await Assert.That(frame.ShipyardData.zRot).IsEqualTo(1.5f);
            await Assert.That(frame.ShipyardData.Spawned).IsEqualTo(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
            await Assert.That(frame.ShipyardData.Hp).IsEqualTo(1234);
            await Assert.That(frame.CurrentStep).IsEqualTo(1);
            await Assert.That(frame.NumAction).IsEqualTo(1);
            await Assert.That(frame.Transform.ZoneId).IsEqualTo(ZoneId);
            // Pre-restart ObjIds die with the session; fresh ones are assigned at SpawnAll.
            await Assert.That(frame.ObjId).IsEqualTo(0u);
            await Assert.That(frame.ShipyardData.ObjId).IsEqualTo(0u);
        }
        finally
        {
            restoreZone();
        }
    }

    [Test]
    public async Task Reload_SkipsUnknownTemplateRow()
    {
        var restoreZone = SeedZoneManager();
        try
        {
            var store = new FakeFrameStore();
            var template = BuildTemplate();
            var manager = BuildManager(store, out _);
            manager._shipyardsTemplate.Add(TemplateId, template);
            manager.PersistShipyard(BuildLiveFrame(template));
            store.Upsert(new ShipyardFrameRow(99, 999, OwnerId, OwnerName, 148, 0, 0, 100, 0, 0, 0, 0, 0, DateTime.UtcNow));

            var reloaded = BuildManager(store, out _);
            reloaded._shipyardsTemplate.Add(TemplateId, template);
            reloaded.LoadPlacedFrames();

            await Assert.That(reloaded.GetShipyard((uint)FrameId)).IsNotNull();
            await Assert.That(reloaded.GetShipyard(99)).IsNull();
        }
        finally
        {
            restoreZone();
        }
    }

    [Test]
    public async Task Reload_SentinelRow_RestoresInertWithoutGrant()
    {
        var restoreZone = SeedZoneManager();
        try
        {
            var store = new FakeFrameStore();
            store.Upsert(new ShipyardFrameRow(FrameId, TemplateId, OwnerId, OwnerName, 148, 1000, 5, 500,
                100.5f, 200.25f, 10.0f, 1.5f, ZoneId, new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)));

            var reloaded = BuildManager(store, out var tasks);
            reloaded._shipyardsTemplate.Add(TemplateId, BuildTemplate());
            reloaded.LoadPlacedFrames();

            var frame = reloaded.GetShipyard((uint)FrameId);
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.ShipyardData.Step).IsEqualTo(1000);
            await Assert.That(frame.CurrentStep).IsEqualTo(-1);

            // A later completion call (owner interaction, duplicate ceremony task)
            // must be a no-op: no scroll grant (which always schedules the
            // ShipyardCompleteTask), no state change.
            reloaded.ShipyardCompletedTask(frame);
            tasks.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).WasCalled(Times.Never);
            await Assert.That(frame.ShipyardData.Step).IsEqualTo(1000);
        }
        finally
        {
            restoreZone();
        }
    }
}
