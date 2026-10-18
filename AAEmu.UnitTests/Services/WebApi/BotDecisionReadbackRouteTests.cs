using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Services.WebApi.Controllers;

using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.DependencyInjection;

using NetCoreServer;

using TUnit.Core;

namespace AAEmu.UnitTests.Services.WebApi;

/// <summary>
/// T7 operator decision read-back, driven through the REAL read-back surface
/// (plan §7, audit L2.7): a real <see cref="BotGoalArbiter"/> elects, the real
/// <see cref="BotGoalArbiterStepExecutor"/> keeps the decision, the real
/// <see cref="BotControlController"/> brain route is invoked with a real
/// request through a real service provider, and the JSON an operator receives
/// is read back for both facts — why this activity this wake, and whether the
/// work in force was commanded or the bot's own.
///
/// The arbitration module is a rig module (a real one needs a live world), and
/// the decorator's inner step is a stub: neither decides what is under test,
/// which is that the decision reaches the operator. The assignment read-back
/// (T1) is read from the same response to prove T7 added to it rather than
/// replacing it.
/// </summary>
[NotInParallel]
public sealed class BotDecisionReadbackRouteTests
{
    private const string Token = "readback-route-token";

    private string? _oldEnabled;
    private string? _oldToken;

    [Before(Test)]
    public void EnableApi()
    {
        _oldEnabled = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL");
        _oldToken = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
    }

    [After(Test)]
    public void RestoreApi()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", _oldEnabled);
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", _oldToken);
    }

    #region Rig

    private sealed class StubModule : IBotActivityModule
    {
        public string Name => "Farm";
        public int Priority => 10;
        public BotActivityDecision Decision { get; init; } = BotActivityDecision.Deny("not asked");

        public BotActivityDecision CanActivate(BotActivityContext context) => Decision;

        public BotActivity Activate(BotActivityContext context) => new(Decision.ActivityName!, Name);
    }

    /// <summary>The read-back never depends on the inner step's result.</summary>
    private sealed class StubInner : IBotStepExecutor
    {
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
            => Task.FromResult<TimeSpan?>(null);
    }

    /// <summary>
    /// One bot, wired the way the game host wires it: real registry, real
    /// scheduler, real roam executor (for the route's state lookup), real
    /// arbiter, real decorator, real assignment registry.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceProvider? _previousProvider;

        public PlayerBotRuntime Runtime { get; }
        public IBotAssignmentRegistry Assignments { get; } = new BotAssignmentRegistry();

        public Harness(string botName, uint characterId, BotActivityDecision decision)
        {
            PlayerbotPilotRig.SeedPilotSingletons();
            var session = HeadlessSession.Create(characterId, botName, 1, Race.Nuian);
            var roam = new BotRoamStepExecutor();
            PlayerBotScheduler scheduler = null!;
            var manager = new PlayerBotManager(new PlayerBotLifecycleAdapter(
                new CharacterLifecycleService(), new Lazy<PlayerBotScheduler>(() => scheduler)));
            scheduler = new PlayerBotScheduler(manager, roam,
                new PlayerBotSchedulerOptions { SubscribeToTickManager = false });
            var arbiter = new BotGoalArbiter([new StubModule { Decision = decision }]);
            var decorator = new BotGoalArbiterStepExecutor(arbiter, new StubInner());

            _provider = new ServiceCollection()
                .AddSingleton<IPlayerBotManager>(manager)
                .AddSingleton<IPlayerBotScheduler>(scheduler)
                .AddSingleton(roam)
                .AddSingleton<IBotGoalArbiter>(arbiter)
                .AddSingleton<BotGoalArbiterStepExecutor>(decorator)
                .AddSingleton<IBotStepExecutor>(decorator)
                .AddSingleton(Assignments)
                .BuildServiceProvider();
            _previousProvider = SingletonContainer.ServiceProvider;
            SingletonContainer.ServiceProvider = _provider;

            if (!manager.Spawn(session.Character, "t7-readback-route-tests"))
                throw new InvalidOperationException("the bot manager refused the test bot");
            if (!manager.TryGet(characterId, out var runtime) || runtime == null)
                throw new InvalidOperationException("the bot manager did not register the test bot");
            Runtime = runtime;
        }

        /// <summary>The route resolves the decorator from DI, so read it back the same way.</summary>
        public BotGoalArbiterStepExecutor Decorator => _provider.GetRequiredService<BotGoalArbiterStepExecutor>();

        public void Dispose()
        {
            SingletonContainer.ServiceProvider = _previousProvider;
            _provider.Dispose();
        }
    }

    /// <summary>
    /// Steps the real decorator once (a real wake), then serves the bot's brain
    /// through the real route with a real authenticated request.
    /// </summary>
    private static async Task<JsonElement> ReadBackAsync(
        string botName, uint characterId, BotActivityDecision decision, bool acceptTravelAssignment)
    {
        using var harness = new Harness(botName, characterId, decision);
        if (acceptTravelAssignment)
        {
            var accepted = harness.Assignments.Accept(characterId, BotAssignmentActivities.Travel,
                100f, 200f, 300f, TimeSpan.FromMinutes(5), "t7-readback-route-tests");
            if (accepted == null)
                throw new InvalidOperationException("the assignment plane refused travel");
        }

        await harness.Decorator.StepAsync(harness.Runtime, CancellationToken.None);

        var path = $"/api/bots/brain/{botName}";
        var request = new HttpRequest("GET", path, "HTTP/1.1");
        request.SetHeader("X-Auth-Token", Token);
        var matches = Regex.Matches(path, "^/api/bots/brain/([^/]+)$");
        var method = typeof(BotControlController).GetMethod(nameof(BotControlController.BrainInspect))
            ?? throw new InvalidOperationException("the brain route method is gone");
        var response = (HttpResponse)method.Invoke(new BotControlController(), [request, matches])!;

        if (response.Status != (int)HttpStatusCode.OK)
            throw new InvalidOperationException($"GET {path} → {response.Status}: {response.Body}");
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.Clone();
    }

    private static string? Text(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>The assignment read-back is an object when the registry holds a record for the bot.</summary>
    private static bool HasAssignment(JsonElement el)
        => el.TryGetProperty("Assignment", out var value) && value.ValueKind == JsonValueKind.Object;

    #endregion

    /// <summary>
    /// The gap the audit names (L2.7): nobody could activate, and the operator
    /// reads the arbiter's OWN refusal text with the counts. Nothing is in
    /// force, so no origin is claimed.
    /// </summary>
    [Test]
    public async Task BrainRoute_NoCandidate_ReportsTheArbiterReasonAndNoOrigin()
    {
        var snapshot = await ReadBackAsync("ReadbackRefusal", 911001u,
            BotActivityDecision.Deny("no ripe crop in reach"), acceptTravelAssignment: true);

        await Assert.That(Text(snapshot, "DecisionOutcome")).IsEqualTo(nameof(BotArbitrationOutcome.NoCandidate));
        await Assert.That(Text(snapshot, "DecisionActivity"), "nothing was elected").IsNull();
        await Assert.That(Text(snapshot, "DecisionReason")).IsEqualTo("no ripe crop in reach");
        await Assert.That(Number(snapshot, "DecisionCandidates")).IsEqualTo(0);
        await Assert.That(Number(snapshot, "DecisionRejections")).IsEqualTo(1);
        await Assert.That(Text(snapshot, "Activity")).IsNull();
        await Assert.That(snapshot.TryGetProperty("WorkOrigin", out var origin),
            "the origin field is reported even when it names nothing").IsTrue();
        await Assert.That(origin.ValueKind, "nothing is running, so there is no side to name")
            .IsEqualTo(JsonValueKind.Null);
        await Assert.That(HasAssignment(snapshot),
            "T1's assignment read-back is still served beside it").IsTrue();
    }

    /// <summary>
    /// The operator asked for exactly this work and this work is what is
    /// running: the same surface says so.
    /// </summary>
    [Test]
    public async Task BrainRoute_LiveAssignmentInForce_ReportsCommanded()
    {
        var snapshot = await ReadBackAsync("ReadbackCommanded", 911002u,
            BotActivityDecision.Allow(BotAssignmentActivities.Travel), acceptTravelAssignment: true);

        await Assert.That(Text(snapshot, "DecisionOutcome")).IsEqualTo(nameof(BotArbitrationOutcome.Activated));
        await Assert.That(Text(snapshot, "DecisionActivity")).IsEqualTo(BotAssignmentActivities.Travel);
        await Assert.That(Text(snapshot, "Activity")).IsEqualTo(BotAssignmentActivities.Travel);
        await Assert.That(Text(snapshot, "DecisionReason"), "nothing refused this wake").IsNull();
        await Assert.That(Number(snapshot, "DecisionCandidates")).IsEqualTo(1);
        await Assert.That(Number(snapshot, "DecisionRejections")).IsEqualTo(0);
        await Assert.That(Text(snapshot, "WorkOrigin")).IsEqualTo(BotWorkOrigin.Commanded);
        await Assert.That(HasAssignment(snapshot)).IsTrue();
    }

    /// <summary>
    /// A live assignment for OTHER work does not make the running work
    /// commanded — the bot's own election is named as such.
    /// </summary>
    [Test]
    public async Task BrainRoute_AssignmentForAnotherActivity_ReportsAutonomous()
    {
        var snapshot = await ReadBackAsync("ReadbackAutonomous", 911003u,
            BotActivityDecision.Allow("farm.work"), acceptTravelAssignment: true);

        await Assert.That(Text(snapshot, "Activity")).IsEqualTo("farm.work");
        await Assert.That(Text(snapshot, "DecisionActivity")).IsEqualTo("farm.work");
        await Assert.That(Text(snapshot, "WorkOrigin")).IsEqualTo(BotWorkOrigin.Autonomous);
        await Assert.That(HasAssignment(snapshot),
            "the operator's pending assignment is still visible").IsTrue();
    }

    /// <summary>A bot with no assignment at all is simply the bot's own work.</summary>
    [Test]
    public async Task BrainRoute_NoAssignment_ReportsAutonomous()
    {
        var snapshot = await ReadBackAsync("ReadbackUnassigned", 911004u,
            BotActivityDecision.Allow("farm.work"), acceptTravelAssignment: false);

        await Assert.That(Text(snapshot, "WorkOrigin")).IsEqualTo(BotWorkOrigin.Autonomous);
        await Assert.That(snapshot.TryGetProperty("Assignment", out var assignment), "no assignment was ever recorded")
            .IsTrue();
        await Assert.That(assignment.ValueKind).IsEqualTo(JsonValueKind.Null);
    }
}
