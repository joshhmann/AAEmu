#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AAEmu.IntegrationTests.E2e;

// Native runner/control evidence only: no game services, fixture grants or world actions.
public sealed class LoopDevelopmentRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aaemu-loop-" + Guid.NewGuid().ToString("N"));
    private static readonly LoopDevelopmentRun.Identity Identity = new("food-control", "private-db", 42, "retained-bot");
    private static readonly LoopDevelopmentRun.Source Source = new(new string('a', 40), "source-original",
        new string('b', 64), new string('c', 64), new string('d', 32), new string('d', 32));
    private static readonly string[] Portions = ["acquire", "prepare", "use"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static JsonElement State(int money = 90, long itemId = 123) => JsonSerializer.SerializeToElement(new
    {
        money, items = new[] { new { id = itemId, count = 10 } }, pending = false
    });
    private static JsonElement Carry() => JsonSerializer.SerializeToElement(new { purchases = 1 });
    private string PathFor(string name) => Path.Combine(_root, name);
    private LoopDevelopmentRun Start(string name, string mode = "development") => LoopDevelopmentRun.Begin(
        PathFor(name), Identity, Source, Portions, mode, State());
    private LoopDevelopmentRun Failed(bool quiescent = true, bool durable = true)
    {
        var run = Start("failed");
        run.Complete("acquire");
        run.Fail("prepare did not produce the expected material");
        run.Finish(State(), Carry(), quiescent, durable, nativePassed: false);
        return run;
    }
    private LoopDevelopmentRun Resume(LoopDevelopmentRun previous, JsonElement? state = null,
        LoopDevelopmentRun.Identity? identity = null, LoopDevelopmentRun.Source? source = null,
        Func<LoopDevelopmentRun.Receipt, string?>? reconcile = null) => LoopDevelopmentRun.Begin(
        PathFor("retry"), identity ?? Identity, source ?? Source, Portions, "development", state ?? State(),
        previous.CheckpointPath, previous.CheckpointSha256, reconcile ?? (_ => null));

    [Fact]
    public void Resume_FixedCode_ContinuesFailedPortionWithoutAcceptingWholeLoop()
    {
        var previous = Failed();
        var original = File.ReadAllBytes(previous.CheckpointPath);
        var fixedSource = Source with { PublishSourceIdentity = "source-fixed", TestAssemblySha256 = new string('e', 64) };
        var retry = Resume(previous, source: fixedSource);
        Assert.Equal("prepare", retry.CurrentPortion);
        Assert.Throws<InvalidOperationException>(() => retry.Complete("acquire"));
        retry.Complete("prepare");
        Assert.Equal("use", retry.CurrentPortion);
        retry.Complete("use");
        var receipt = retry.Finish(State(), Carry(), true, true, true);
        Assert.Equal("DEVELOPMENT_PASS", receipt.Status);
        Assert.Equal(previous.RunId, receipt.Predecessor!.RunId);
        Assert.Equal(fixedSource, receipt.Source);
        Assert.False(receipt.Resumable);
        Assert.Equal(original, File.ReadAllBytes(previous.CheckpointPath));
    }

    [Fact]
    public void Fail_LaterFailure_PreservesFirstFailureAndStopsCompletion()
    {
        var run = Start("first");
        run.Fail("first failure");
        run.Fail("later failure");
        Assert.Throws<InvalidOperationException>(() => run.Complete("acquire"));
        var receipt = run.Finish(State(), Carry(), true, true, true);
        Assert.Equal("first failure", receipt.FirstFailure!.Reason);
        Assert.Equal("acquire", receipt.FirstFailure.Portion);
        Assert.Equal("FAIL", receipt.Status);
    }

    [Theory]
    [InlineData(89, 123)]
    [InlineData(90, 124)]
    public void Resume_ChangedMoneyOrItemIdentity_RefusesBeforeDomainCallback(int money, long itemId)
    {
        var previous = Failed();
        var called = false;
        Assert.Throws<InvalidDataException>(() => Resume(previous, State(money, itemId), reconcile: _ =>
        {
            called = true;
            return null;
        }));
        Assert.False(called);
        Assert.False(Directory.Exists(PathFor("retry")));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("character")]
    [InlineData("bot")]
    [InlineData("scenario")]
    public void Resume_ChangedFixtureIdentity_Refuses(string changed)
    {
        var previous = Failed();
        var identity = changed switch
        {
            "database" => Identity with { DatabaseId = "other-db" },
            "character" => Identity with { CharacterId = 43 },
            "bot" => Identity with { BotName = "other-bot" },
            _ => Identity with { Scenario = "other-scenario" }
        };
        Assert.Throws<InvalidDataException>(() => Resume(previous, identity: identity));
        Assert.False(Directory.Exists(PathFor("retry")));
    }

    [Fact]
    public void Resume_AmbiguousPartialEffect_RefusesDespiteEqualSavedState()
    {
        var previous = Failed();
        var error = Assert.Throws<InvalidDataException>(() => Resume(previous,
            reconcile: _ => "paid action settlement is unknown"));
        Assert.Contains("paid action settlement is unknown", error.Message);
        Assert.False(Directory.Exists(PathFor("retry")));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Resume_UnsettledOrUnverifiedState_Refuses(bool quiescent, bool durable)
    {
        var previous = Failed(quiescent, durable);
        Assert.Throws<InvalidDataException>(() => Resume(previous));
    }

    [Fact]
    public void Resume_ChangedCheckpointBytes_Refuses()
    {
        var previous = Failed();
        File.AppendAllText(previous.CheckpointPath, " ");
        Assert.Throws<InvalidDataException>(() => Resume(previous));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("portions")]
    public void Resume_MissingMetadata_RefusesEvenWithMatchingHash(string field)
    {
        var previous = Failed();
        var receipt = JsonSerializer.Deserialize<LoopDevelopmentRun.Receipt>(File.ReadAllText(previous.CheckpointPath), JsonOptions)!;
        receipt = field == "source" ? receipt with { Source = null! } : receipt with { Portions = null! };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        var path = PathFor("malformed.json");
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => LoopDevelopmentRun.Begin(PathFor("retry"), Identity, Source,
            Portions, "development", State(), path, Convert.ToHexString(SHA256.HashData(bytes)), _ => null));
    }

    [Fact]
    public void Resume_ReferenceDataChanged_Refuses()
    {
        var previous = Failed();
        Assert.Throws<InvalidDataException>(() => Resume(previous, source: Source with
        {
            CanonicalMd5 = new string('f', 32), RuntimeMd5 = new string('f', 32)
        }));
    }

    [Fact]
    public void Resume_MissingReconciliation_Refuses()
    {
        var previous = Failed();
        Assert.Throws<InvalidDataException>(() => LoopDevelopmentRun.Begin(PathFor("retry"), Identity, Source,
            Portions, "development", State(), previous.CheckpointPath, previous.CheckpointSha256));
    }

    [Fact]
    public void Acceptance_CheckpointInput_RefusesBeforeCreatingOutput()
    {
        var previous = Failed();
        Assert.Throws<InvalidDataException>(() => LoopDevelopmentRun.Begin(PathFor("acceptance"), Identity, Source,
            Portions, "acceptance", State(), previous.CheckpointPath, previous.CheckpointSha256, _ => null));
        Assert.False(Directory.Exists(PathFor("acceptance")));
    }

    [Fact]
    public void Acceptance_FreshUninterruptedRun_RequiresEveryObservedPortion()
    {
        var incomplete = Start("incomplete", "acceptance");
        incomplete.Complete("acquire");
        Assert.Equal("FAIL", incomplete.Finish(State(), Carry(), true, true, true).Status);
        var fresh = Start("fresh", "acceptance");
        foreach (var portion in Portions) fresh.Complete(portion);
        var receipt = fresh.Finish(State(), Carry(), true, true, true);
        Assert.Equal("PASS", receipt.Status);
        Assert.Null(receipt.Predecessor);
        Assert.Null(receipt.FirstFailure);
    }

    [Fact]
    public void Finish_SecondAttempt_CannotRewriteEvidence()
    {
        var previous = Failed();
        var original = File.ReadAllBytes(previous.CheckpointPath);
        Assert.Throws<InvalidOperationException>(() => previous.Finish(State(), Carry(), true, true, true));
        Assert.Equal(original, File.ReadAllBytes(previous.CheckpointPath));
        Assert.Throws<InvalidDataException>(() => Start("failed"));
    }

    [Fact]
    public void Finish_MissingObservedState_CannotClaimAcceptance()
    {
        var run = Start("missing-state", "acceptance");
        foreach (var portion in Portions) run.Complete(portion);
        Assert.Equal("FAIL", run.Finish(null, null, true, true, true).Status);
    }

    [Fact]
    public void Resume_ChangedPortionOrder_Refuses()
    {
        var previous = Failed();
        Assert.Throws<InvalidDataException>(() => LoopDevelopmentRun.Begin(PathFor("retry"), Identity, Source,
            ["acquire", "use", "prepare"], "development", State(), previous.CheckpointPath,
            previous.CheckpointSha256, _ => null));
    }

    [Fact]
    public async Task Begin_ConcurrentAttempts_OnlyOneClaimsTheDirectory()
    {
        using var barrier = new Barrier(2);
        var invocationId = Guid.NewGuid().ToString("N");
        LoopDevelopmentRun? Attempt()
        {
            barrier.SignalAndWait();
            try
            {
                return LoopDevelopmentRun.Begin(PathFor("shared"), Identity, Source, Portions,
                    "development", State(), invocationId: invocationId);
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
        }
        var attempts = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
        var winner = Assert.Single(attempts.Where(attempt => attempt != null))!;
        var progress = JsonSerializer.Deserialize<LoopDevelopmentRun.Receipt>(
            File.ReadAllText(Path.Combine(PathFor("shared"), "loop-progress.json")), JsonOptions)!;
        Assert.Equal(winner.RunId, progress.RunId);
        Assert.Equal("RUNNING", progress.Status);
    }

    [Fact]
    public void Resume_OversizedCheckpoint_RefusesBeforeCreatingOutput()
    {
        var previous = Failed();
        using (var stream = File.OpenWrite(previous.CheckpointPath))
            stream.SetLength(LoopDevelopmentRun.MaximumCheckpointBytes + 1);
        Assert.Throws<InvalidDataException>(() => Resume(previous));
        Assert.False(Directory.Exists(PathFor("retry")));
    }

    [Fact]
    public void Resume_ReusedSupervisorInvocation_Refuses()
    {
        var previous = Failed();
        Assert.Throws<InvalidDataException>(() => LoopDevelopmentRun.Begin(PathFor("retry"), Identity, Source,
            Portions, "development", State(), previous.CheckpointPath, previous.CheckpointSha256,
            _ => null, invocationId: previous.RunId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
