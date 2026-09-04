using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class DevMapperServiceTests
{
    private string _testRoutesDir = null!;
    private string _testPathsDir = null!;

    [Before(Test)]
    public void SetUp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "aaemu-devmapper-tests-" + Guid.NewGuid().ToString("N"));
        _testRoutesDir = Path.Combine(tempRoot, "Routes");
        _testPathsDir = Path.Combine(tempRoot, "Path");
        Directory.CreateDirectory(_testRoutesDir);
        Directory.CreateDirectory(_testPathsDir);

        DevMapperService.Instance.RoutesDirectory = _testRoutesDir;
        DevMapperService.Instance.PathsDirectory = _testPathsDir;
        DevMapperService.Instance.MinWaypointDistance = 1.5f;
        DevMapperService.Instance.MinYawDeltaRadians = 0.35f;
    }

    [After(Test)]
    public void TearDown()
    {
        DevMapperService.Instance.RoutesDirectory = Path.Combine("Data", "Routes");
        DevMapperService.Instance.PathsDirectory = Path.Combine("Data", "Path");
        try
        {
            if (Directory.Exists(_testRoutesDir))
                Directory.Delete(Path.GetDirectoryName(_testRoutesDir)!, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static Character CreateTestChar(uint id, string name, Vector3 pos)
    {
        var ch = new Character(new UnitCustomModelParams()) { Id = id, Name = name };
        ch.Transform.Local.SetPosition(pos.X, pos.Y, pos.Z, 0, 0, 0);
        return ch;
    }

    [Test]
    public async Task Start_CreatesSessionAndInitialWaypoint()
    {
        var character = CreateTestChar(9101, "mapper-start-test", new Vector3(100f, 200f, 50f));

        var summary = DevMapperService.Instance.Start(character, "solzreed_hub");

        await Assert.That(summary.Success).IsTrue();
        await Assert.That(summary.RouteName).IsEqualTo("solzreed_hub");
        await Assert.That(summary.WaypointCount).IsEqualTo(1);
        await Assert.That(DevMapperService.Instance.IsRecording(character.Id)).IsTrue();

        DevMapperService.Instance.Stop(character.Id);
    }

    [Test]
    public async Task RecordPosition_AppliesCompactionRules()
    {
        var character = CreateTestChar(9102, "mapper-compaction-test", new Vector3(100f, 200f, 50f));

        DevMapperService.Instance.Start(character, "compaction_route");

        // Small step (0.5m) -> should NOT record a new waypoint
        DevMapperService.Instance.RecordPosition(character.Id, new Vector3(100.5f, 200f, 50f), 0f);

        // Large step (2.0m) -> SHOULD record
        DevMapperService.Instance.RecordPosition(character.Id, new Vector3(102.5f, 200f, 50f), 0f);

        // Large turn (> 20 deg) at 0.6m -> SHOULD record
        DevMapperService.Instance.RecordPosition(character.Id, new Vector3(102.5f, 200.6f, 50f), 1.0f);

        var stopSummary = DevMapperService.Instance.Stop(character.Id);

        await Assert.That(stopSummary.Success).IsTrue();
        // Initial + Large step + Large turn = 3 waypoints
        await Assert.That(stopSummary.WaypointCount).IsEqualTo(3);
        await Assert.That(stopSummary.TotalDistance).IsGreaterThan(2f);
    }

    [Test]
    public async Task RecordActions_Interacts_Talks_AndMarks()
    {
        var character = CreateTestChar(9103, "mapper-actions-test", new Vector3(50f, 60f, 10f));

        DevMapperService.Instance.Start(character, "action_route");

        DevMapperService.Instance.RecordInteract(character.Id, 555, 1234, new Vector3(51f, 60f, 10f), 101);
        DevMapperService.Instance.RecordTalk(character.Id, 666, 4321, new Vector3(55f, 60f, 10f));
        DevMapperService.Instance.RecordMark(character.Id, "gate_entrance", new Vector3(58f, 60f, 10f), 0.5f);

        var stopSummary = DevMapperService.Instance.Stop(character.Id);

        await Assert.That(stopSummary.Success).IsTrue();
        await Assert.That(stopSummary.ActionCount).IsEqualTo(3);
        await Assert.That(File.Exists(stopSummary.JsonPath!)).IsTrue();
        await Assert.That(File.Exists(stopSummary.PathFilePath!)).IsTrue();

        var loaded = DevMapperService.Instance.GetRoute("action_route");
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Actions.Count).IsEqualTo(4); // 1 initial waypoint + 3 actions
        await Assert.That(loaded.Actions.Any(a => a.ActionType == MapperActionType.InteractDoodad && a.TemplateId == 1234)).IsTrue();
        await Assert.That(loaded.Actions.Any(a => a.ActionType == MapperActionType.TalkNpc && a.TemplateId == 4321)).IsTrue();
        await Assert.That(loaded.Actions.Any(a => a.ActionType == MapperActionType.Mark && a.Label == "gate_entrance")).IsTrue();
    }

    [Test]
    public async Task Stop_ProducesValidPathFile()
    {
        var character = CreateTestChar(9104, "mapper-path-file-test", new Vector3(10f, 20f, 5f));

        DevMapperService.Instance.Start(character, "valid_path_file");
        DevMapperService.Instance.RecordPosition(character.Id, new Vector3(15f, 20f, 5f), 0f);
        DevMapperService.Instance.RecordPosition(character.Id, new Vector3(20f, 20f, 5f), 0f);

        var stopSummary = DevMapperService.Instance.Stop(character.Id);
        var lines = await File.ReadAllLinesAsync(stopSummary.PathFilePath!);

        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0].StartsWith("|10.00|20.00|5.00")).IsTrue();
        await Assert.That(lines[1].StartsWith("|15.00|20.00|5.00")).IsTrue();
        await Assert.That(lines[2].StartsWith("|20.00|20.00|5.00")).IsTrue();
    }

    [Test]
    public async Task ReplayRoute_DrivesActorThroughWaypoints()
    {
        var actor = new TestRecordingActor();

        var route = new MapperRouteData
        {
            RouteName = "test_replay",
            Actions =
            [
                new MapperActionRecord { ActionType = MapperActionType.Waypoint, X = 5f, Y = 0f, Z = 0f },
                new MapperActionRecord { ActionType = MapperActionType.Mark, Label = "halfway", X = 5f, Y = 0f, Z = 0f },
                new MapperActionRecord { ActionType = MapperActionType.InteractDoodad, TargetObjId = 55, X = 6f, Y = 0f, Z = 0f },
                new MapperActionRecord { ActionType = MapperActionType.TalkNpc, TargetObjId = 77, X = 7f, Y = 0f, Z = 0f },
                new MapperActionRecord { ActionType = MapperActionType.Waypoint, X = 10f, Y = 0f, Z = 0f }
            ]
        };

        var result = DevMapperService.Instance.ReplayRoute(actor, route, speed: 10f);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.CompletedActions).IsEqualTo(5);
        await Assert.That(actor.Executed).Contains("nav:5,0");
        await Assert.That(actor.Executed).Contains("interact:55");
        await Assert.That(actor.Executed).Contains("talk:77");
        await Assert.That(actor.Executed).Contains("nav:10,0");
        await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, new Vector3(10f, 0f, 0f))).IsLessThanOrEqualTo(0.01f);
    }

    private sealed class TestRecordingActor : IGameplayActor
    {
        public uint ActorId => 9999;
        public Character Character { get; } = new(new UnitCustomModelParams()) { Id = 9999, Name = "ReplayBot" };
        public ActorRequest? ActiveRequest => null;
        public IReadOnlyList<ActorAuditRecord> AuditTrace => [];
        public List<string> Executed { get; } = [];

        public ActorObservation Observe() => new() { ActorId = ActorId, CurrentTargetObjId = 0 };
        public ActorRequest SetTarget(uint targetObjId) => Unsupported();

        public ActorRequest NavigateTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            Executed.Add($"nav:{destination.X},{destination.Y}");
            Character.Transform.Local.SetPosition(destination.X, destination.Y, destination.Z, 0, 0, 0);
            var req = new ActorRequest(ActorActionType.Move, 0, destination, 0, null);
            req.Accept("test");
            req.Start("test");
            req.Complete("arrived");
            return req;
        }

        public ActorRequest NavigateToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            Executed.Add($"navunit:{targetObjId}");
            var req = new ActorRequest(ActorActionType.Move, targetObjId, null, 0, null);
            req.Accept("test");
            req.Start("test");
            req.Complete("arrived");
            return req;
        }

        public ActorRequest InteractWith(uint doodadObjId, string? idempotencyKey = null)
        {
            Executed.Add($"interact:{doodadObjId}");
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, 0, null);
            req.Accept("test");
            req.Start("test");
            req.Complete("interacted");
            return req;
        }

        public ActorRequest Talk(uint npcObjId, string? idempotencyKey = null)
        {
            Executed.Add($"talk:{npcObjId}");
            var req = new ActorRequest(ActorActionType.Talk, npcObjId, null, 0, null);
            req.Accept("test");
            req.Start("test");
            req.Complete("talked");
            return req;
        }

        public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
        {
            Executed.Add($"cast:{skillId}");
            var req = new ActorRequest(ActorActionType.Cast, targetObjId, null, skillId, null);
            req.Accept("test");
            req.Start("test");
            req.Complete("casted");
            return req;
        }

        private static ActorRequest Unsupported() => throw new NotSupportedException();
        public ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Stop() => Unsupported();
        public ActorRequest CastAt(uint skillId, Vector3 position, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Loot(uint lootOwnerObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Equip(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyInvite(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyAccept(string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionCreate(string name, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionInvite(string invitedName, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionAccept(AAEmu.Game.Models.StaticValues.FactionsEnum expeditionId, uint inviterId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionLeave(string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeOffer(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradePutup(uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeLockOk(string? idempotencyKey = null) => Unsupported();
        public ActorRequest Mount(uint mateObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null) => Unsupported();
        public ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Plant(uint seedItemTemplateId, Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public bool Interrupt(Guid traceId) => false;
        public ActorRequest AcceptQuest(uint questId, AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverQuests(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverSelfQuests(string? idempotencyKey = null) => Unsupported();
        public ActorRequest PlayCinema(uint cinemaId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest SellSpecialty(uint merchantNpcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Repair(uint blacksmithNpcObjId, ulong itemId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AAEmu.Game.Models.Game.Auction.AuctionDuration duration, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null) => Unsupported();
        public ActorAuditRecord? FindByKey(string idempotencyKey) => null;
        public void Tick(TimeSpan elapsed) { }
    }
}
