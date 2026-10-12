using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Xunit;

using static AAEmu.IntegrationTests.E2e.BotShortRunEvidence;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Adopt-only natural level10 starter-body quest-purpose acceptance. One
/// pre-START enrollment wake, passive observation and one exact own interrupt.
/// No seeded quest, assigned goal/route, grants, teleport, save or restart.
/// </summary>
[Collection("e2e")]
public class BotShortRunTests
{
    private const double PollSeconds = 1;
    private const double FlushSeconds = 325;
    private sealed record Failure(string Scope, string Phase, DateTime AtUtc, string Type, string Message, int? HttpStatus = null);
    internal sealed record DurableSnapshot(uint CharacterId, int Level, long Experience, long Money,
        Dictionary<uint, long> Items, HashSet<uint> CompletedQuests, int ActiveQuestRows);
    internal sealed record Reward(uint QuestId, long Experience, long Money, Dictionary<uint, long> Items,
        bool Supported, string? UnsupportedReason);
    internal sealed record Persistence(bool Proved, string Scope, string? Reason);
    internal sealed record QuestBodyDurable(DateTime SnapshotStartedAtUtc, DateTime ObservedAtUtc,
        JsonElement Character, JsonElement CharacterFloatMirrors, JsonElement Account, JsonElement[] Abilities,
        JsonElement[] Items, string SqlMode, string SessionTimeZone);
    internal sealed record QuestSqlColumn(string Table, string Name, string DataType, string ColumnType, int? DatePrecision);
    internal sealed record QuestSqlCodec(QuestSqlColumn[] Columns, string SqlMode, string SessionTimeZone,
        string SystemTimeZone, bool RoundFraction, JsonElement Environment, JsonElement[] CastProbes, JsonElement AccountTrigger);
    private static readonly string[] CharacterFloatNames = ["x", "y", "z", "roll", "pitch", "yaw"];
    private static readonly Dictionary<string, string> CharacterFloatAliases =
        CharacterFloatNames.ToDictionary(n => "__quest_purpose_float_" + n, n => n, StringComparer.Ordinal);
    private static readonly (string Db, string Live)[] ItemColumns =
    [
        ("id","id"), ("type","type"), ("template_id","template"), ("container_id","containerId"),
        ("slot_type","slotType"), ("slot","slot"), ("count","count"), ("details","persistedDetails"),
        ("lifespan_mins","lifespanMins"), ("made_unit_id","madeUnitId"), ("unsecure_time","unsecureTime"),
        ("unpack_time","unpackTime"), ("owner","owner"), ("grade","grade"), ("flags","flags"),
        ("created_at","createdAt"), ("ucc","ucc"), ("expire_time","expireTime"),
        ("expire_online_minutes","expireOnlineMinutes"), ("charge_time","chargeTime"), ("charge_count","chargeCount")
    ];

    [Fact]
    [Trait("Category", "e2e")]
    public Task QuestPurposeHeadless15Min() => RunHeadless(productive: false);

    [Fact]
    [Trait("Category", "e2e")]
    public Task ProductiveQuestHeadless15Min() => RunHeadless(productive: true);

    [Fact]
    [Trait("Category", "e2e")]
    public Task QuestContinuationHeadless15Min() => RunHeadless(productive: true, continuation: true);

    private async Task RunHeadless(bool productive, bool continuation = false)
    {
        var optIn = continuation ? "BOT_CONTINUATION_RUN" : productive ? "BOT_PRODUCTIVE_RUN" : "BOT_SHORT_RUN";
        if (Environment.GetEnvironmentVariable(optIn) != "1")
        {
            Assert.Skip($"{optIn}=1 and an already-ready owned lane are required.");
            return;
        }
        RequirePrivateLane();
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        // This exact name must also be present in the privately allocated Game
        // process. A shared/default bridge never permits scheduler shutdown.
        var botName = Environment.GetEnvironmentVariable("E2E_QUEST_END_BOT");
        Assert.True(!string.IsNullOrWhiteSpace(botName), "Explicit E2E_QUEST_END_BOT required in runner and owned Game process");
        var scenario = continuation ? "quest-continuation" : productive ? "productive-quest" : "quest-purpose";
        var directory = Path.Combine(E2eStack.E2eRoot, "logs", scenario + "-" + runId);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var reportPath = Path.Combine(directory, "report.json");
        var identityPath = Path.Combine(directory, "own-provision.json");
        var markerPath = Path.Combine(E2eStack.E2eRoot, "runner-cap-fault.json");
        var observationsPath = Path.Combine(directory, "observations.jsonl");
        using var observations = new StreamWriter(new FileStream(observationsPath, FileMode.CreateNew));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(observationsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var budget = new NeedsFarmLoopE2eTests.FarmEvidenceBudget(
            NeedsFarmLoopE2eTests.MaximumHarnessEvidenceBytes, NeedsFarmLoopE2eTests.ReservedEvidenceBytes);
        var failures = new List<Failure>();
        var fixture = Stopwatch.StartNew();
        var watch = new Stopwatch();
        var phase = "FIXTURE_BASELINE";
        string? endReason = null;
        uint characterId = 0;
        var provisionRequested = false;
        var capFailed = false;
        var suppressedCleanupRecords = 0;
        BotDriveClient? bridge = null;
        HttpClient? control = null;
        BotShortRunEvidence? proof = null;
        NeedsFarmLoopE2eTests.FarmDurable? before = null, afterRelease = null, afterFlush = null;
        QuestBodyDurable? bodyBefore = null, bodyAfterRelease = null, bodyAfterFlush = null;
        QuestSqlCodec? sqlCodec = null;
        JsonElement? start = null, end = null;
        JsonElement? latestOwnedCopy = null;
        Guid? auditAfterTraceId = null;
        DateTime? latestCopyCapturedAtUtc = null;
        bool latestRawPersisted = false, latestProofAccepted = false;
        string? latestCopyFault = null;
        var wakeCount = 0;
        var interruptRequests = 0;
        var registryRemoved = false;
        var sessionReleased = false;
        var registryAbsent = false;
        var releaseProved = false;
        var flushProved = false;
        double waitSeconds = 0;
        DateTime? waitStartedAtUtc = null;
        DateTime? endUtc = null;
        BotShortRunEndCapture.Result? endPreparation = null;
        string? canonicalMd5 = null, runtimeMd5 = null;
        var canonicalPath = Path.Combine(E2eStack.RepoRoot, "AAEmu.Game/Data/compact.sqlite3");
        object? configuration = null;
        void Fault(Exception ex) => failures.Add(new(phase, phase, DateTime.UtcNow,
            ex.GetType().Name, ex.Message, ex is HttpRequestException { StatusCode: { } status } ? (int)status : null));
        void Marker()
        {
            if (phase is not ("FIXTURE_BASELINE" or "OBSERVE") || !File.Exists(markerPath)) return;
            endReason = "RUNNER_CAP_FAULT_FAILED_END";
            throw new InvalidDataException("RUNNER_CAP_FAULT");
        }
        CancellationTokenSource Deadline()
        {
            Marker();
            var remaining = watch.IsRunning ? proof?.RemainingSeconds(watch.Elapsed.TotalSeconds)
                ?? Math.Min(MaximumSeconds - watch.Elapsed.TotalSeconds, NoProgressSeconds - watch.Elapsed.TotalSeconds)
                : phase == "FIXTURE_BASELINE" ? 360 - fixture.Elapsed.TotalSeconds : 5;
            if (remaining <= 0) throw new TimeoutException("Declared phase deadline reached");
            return new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(BotShortRunObservationBudget.OperationSeconds, remaining)));
        }
        void Identity(string state)
        {
            var text = JsonSerializer.Serialize(new { state, recordedAtUtc = DateTime.UtcNow,
                e2eRoot = Path.GetFullPath(E2eStack.E2eRoot), runId, freshProvision = true, name = botName,
                characterId, reportPath, role = "id0 is intent only; never guessed cleanup authority" });
            budget.ReserveFinal(text);
            File.WriteAllText(identityPath + ".tmp", text, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(identityPath + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(identityPath + ".tmp", identityPath, overwrite: true);
        }
        bool Raw(object request, object output)
        {
            if (capFailed) { suppressedCleanupRecords++; return false; }
            try
            {
                var line = JsonSerializer.Serialize(new { phase, recordedAtUtc = DateTime.UtcNow, request, output });
                if (Encoding.UTF8.GetByteCount(line) > NeedsFarmLoopE2eTests.MaximumResponseBytes + 4096)
                    throw new InvalidDataException("HARNESS_RECORD_BYTE_BOUND");
                budget.ReserveRaw(line);
                observations.WriteLine(line);
                observations.Flush();
                return true;
            }
            catch (InvalidDataException ex)
            {
                capFailed = true;
                endReason = "HARNESS_EVIDENCE_CAP_FAILED_END";
                if (phase is "FIXTURE_BASELINE" or "OBSERVE") throw;
                suppressedCleanupRecords++;
                Fault(ex);
                return false;
            }
        }
        async Task<JsonElement> Bridge(object request)
        {
            using var deadline = Deadline();
            if (phase == "OBSERVE") latestCopyFault = "Latest observe did not yield a validated full owned passive+SQL copy";
            var result = await bridge!.CallAsync(JsonSerializer.Serialize(request), 5000, deadline.Token);
            // Bind the returned own ID before cap-sensitive evidence writes.
            if (provisionRequested && characterId == 0 && UInt(result, "id") != 0
                && request is not null && phase == "FIXTURE_BASELINE")
            {
                characterId = UInt(result, "id");
                Identity("returned-binding");
            }
            if (phase is "OBSERVE" or "PRE_END_CAPTURE")
            {
                var begin = Time(result, "snapshotStartedAtUtc");
                var copiedEnd = Time(result, "observedAtUtc");
                Require(characterId != 0 && UInt(result, "id") == characterId && begin.HasValue && copiedEnd.HasValue
                    && begin <= copiedEnd && Long(result, "enrollmentGeneration") >= 0, "Own passive copy identity/interval unavailable");
                Require(result.TryGetProperty("actorObjId", out var actorObject) && actorObject.ValueKind == JsonValueKind.Number
                    && actorObject.TryGetUInt32(out _) && result.TryGetProperty("actorCharId", out var actorCharacter)
                    && actorCharacter.ValueKind == JsonValueKind.Number && actorCharacter.TryGetUInt32(out _), "Own actor identity fields unavailable");
                Require(UInt(result, "actorObjId") == 0
                    ? UInt(result, "actorCharId") == 0 || UInt(result, "actorCharId") == characterId
                    : UInt(result, "actorCharId") == characterId, "Passive actor character is not owned");
                if (proof != null)
                {
                    Require(Long(result, "enrollmentGeneration") == proof.Generation, "Passive enrollment generation changed");
                    if (proof.JoinedAt.HasValue)
                        Require(UInt(result, "actorObjId") == proof.ActorId && UInt(result, "actorCharId") == characterId,
                            "Passive joined actor identity changed");
                }
                Require(result.TryGetProperty("fullCharacter", out var fullCharacter) && fullCharacter.ValueKind == JsonValueKind.Object
                    && UInt(fullCharacter, "id") == characterId, "Full owned live character unavailable");
                foreach (var name in new[] { "actorAudit", "activeQuests", "questCompletion", "items", "objectiveItems",
                    "ownItems", "completedQuests", "abilities", "actabilities" })
                    Require(result.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array,
                        "Full owned passive rows unavailable: " + name);
                var audit = result.GetProperty("actorAudit");
                Require(audit.GetArrayLength() == 0 || UInt(result, "actorObjId") != 0
                    && UInt(audit[audit.GetArrayLength() - 1], "actor_id") == UInt(result, "actorObjId")
                    && Id(audit[audit.GetArrayLength() - 1], "trace_id").HasValue, "Owned actual audit tail unavailable");
                var persisted = await ReadQuestBodyDurable(characterId, sqlCodec!, deadline.Token);
                Require(persisted.SnapshotStartedAtUtc >= copiedEnd && persisted.ObservedAtUtc >= persisted.SnapshotStartedAtUtc,
                    "Separate full owned SQL copy interval invalid");
                var values = result.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                values["ownedSqlCopy"] = JsonSerializer.SerializeToElement(persisted);
                values["fullAccount"] = persisted.Account;
                values["fullAccountSqlStartedAtUtc"] = JsonSerializer.SerializeToElement(persisted.SnapshotStartedAtUtc);
                values["fullAccountSqlObservedAtUtc"] = JsonSerializer.SerializeToElement(persisted.ObservedAtUtc);
                values["fullAccountProof"] = JsonSerializer.SerializeToElement(
                    "Owned read-only game-account SQL row; separate copied interval, not passive cache or atomic gameplay snapshot");
                result = JsonSerializer.SerializeToElement(values);
                if (phase == "OBSERVE")
                {
                    latestOwnedCopy = result;
                    latestCopyCapturedAtUtc = DateTime.UtcNow;
                    latestRawPersisted = false;
                    latestProofAccepted = false;
                    latestCopyFault = null;
                }
            }
            var rawPersisted = Raw(request, result);
            if (phase == "OBSERVE") latestRawPersisted = rawPersisted;
            if (phase == "PRE_END_CAPTURE") Require(rawPersisted, "Prepared END raw evidence unavailable");
            return result;
        }
        async Task<JsonElement> Api(HttpMethod method, string path, object? body = null)
        {
            using var deadline = Deadline();
            using var request = new HttpRequestMessage(method, path);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await control!.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Raw(new { method = method.Method, path, body, authentication = "X-Auth-Token present; omitted" }, result);
            return result;
        }
        async Task<NeedsFarmLoopE2eTests.FarmDurable> Durable()
        {
            using var deadline = Deadline();
            var result = await NeedsFarmLoopE2eTests.ReadFarmDurable(characterId, deadline.Token);
            Raw(new { query = "full owned durable snapshot", characterId }, result);
            return result;
        }
        bool ObjectiveProved() => proof != null && (continuation
            ? QuestContinuationEvidence.Proved(proof)
            : productive ? ProductiveQuestEvidence.Proved(proof) : proof.PurposeProved);
        string? StopReason(double elapsed)
        {
            if (!productive) return proof!.StopReason(elapsed);
            if (proof!.EvidenceFault != null) return "EVIDENCE_FAULT";
            if (ObjectiveProved()) return continuation ? "QUEST_CONTINUATION_COMPLETE" : "PRODUCTIVE_QUEST_COMPLETE";
            if (elapsed >= BotShortRunEvidence.MaximumSeconds) return "MAXIMUM_WINDOW";
            if (elapsed - proof.LastProvedProgressSeconds >= BotShortRunEvidence.NoProgressSeconds)
                return "NO_PROVED_PROGRESS";
            return null;
        }
        string? OperationStopReason()
        {
            var elapsed = watch.Elapsed.TotalSeconds;
            return BotShortRunObservationBudget.StopReason(elapsed, proof!.LastProvedProgressSeconds,
                StopReason(elapsed));
        }
        try
        {
            RequirePrivateLane();
            AdoptLaneDbPassword();
            Require(Environment.GetEnvironmentVariable("AAEMU_PRESENCE_IDLE_WAKE") == "1"
                && Environment.GetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED") == "1"
                && Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL") == "1", "Quest bootstrap, idle wake and control must be ON");
            foreach (var flag in new[] { "AAEMU_NEEDS_FARM_ENABLED", "AAEMU_PRESENCE_DEMO", "AAEMU_PRESENCE_HUNT",
                "AAEMU_PRESENCE_BUTCHER", "AAEMU_BOT_SCHEDULES_ENABLED", "AAEMU_BOT_CHATTER_ENABLED",
                "AAEMU_FISHING_CONTEST_ENABLED", "AAEMU_BOT_AUTO_RESTORE" })
                Require(Environment.GetEnvironmentVariable(flag) == "0", "Other gameplay producer must be explicitly OFF: " + flag);
            var token = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN");
            Require(!string.IsNullOrWhiteSpace(token), "Private control authentication required");
            control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2eStack.WebApiPort}"),
                Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = NeedsFarmLoopE2eTests.MaximumResponseBytes };
            control.DefaultRequestHeaders.Add("X-Auth-Token", token);
            var ready = await Api(HttpMethod.Get, "/api/bots/status");
            Require(Bool(ready, "Success") == true && ready.GetProperty("Bots").ValueKind == JsonValueKind.Array, "Authenticated registry unavailable");
            Require(ready.GetProperty("Bots").GetArrayLength() == 0, "Final scheduler shutdown requires an initially empty owned registry");
            using (var deadline = Deadline())
                bridge = await Task.Run(() => new BotDriveClient(E2eStack.BridgePort,
                    NeedsFarmLoopE2eTests.MaximumResponseBytes), deadline.Token).WaitAsync(deadline.Token);
            Require(Bool(await Bridge(new { cmd = "ping" }), "pong") == true, "Adopted bridge not ready");
            canonicalMd5 = Md5(canonicalPath);
            runtimeMd5 = Md5(E2eStack.RuntimeSqlite);
            Require(canonicalMd5 == runtimeMd5, "Canonical/runtime mismatch");
            using (var deadline = Deadline()) sqlCodec = await ReadQuestSqlCodec(deadline.Token);
            Raw(new { query = "deployed read-only schema/mode/timezone/trigger/parameter CAST corroboration" }, sqlCodec);
            var settings = ReadWorldSettings();
            Require(settings.AutoSaveMinutes > 0 && settings.AutoSaveMinutes * 60 + 5 <= FlushSeconds,
                "Autosave exceeds fixed ordinary persistence margin");
            configuration = new { settings.ExpRate, settings.AutoSaveMinutes, flags = E2eStack.CapabilityManifest(),
                provisioningLevel = 10, starterBody = "ordinary existing level10 starter body; disclosed, not earned",
                spawn = "natural", questBootstrap = "ON", idleWake = "ON5s", otherGameplayProducers = "OFF",
                noAssignedQuestGoalRoute = true, noTeleportGrantKillLootDrive = true };
            using (var deadline = Deadline())
                await NeedsFarmLoopE2eTests.RequireFreshFarmName(botName, deadline.Token);
            Identity("before-dispatch-intent");
            provisionRequested = true;
            var provision = await Bridge(new { cmd = "provision", bot = botName, fresh = true, level = 10 });
            Require(characterId != 0 && Long(provision, "level") == 10, "Fresh level10 identity unavailable");
            before = await Durable();
            Require(before.Level == 10 && before.CompletedQuests.Length == 0 && before.Items.Count > 0,
                "Natural starter baseline unavailable or historical character adopted");
            using (var deadline = Deadline()) bodyBefore = await ReadQuestBodyDurable(characterId, sqlCodec!, deadline.Token);
            Raw(new { query = "full owned character/account/abilities baseline" }, bodyBefore);
            wakeCount++;
            await Bridge(new { cmd = "quest", bot = botName, sub = "wake", waitMs = 1000 });
            var enrollment = await Bridge(new { cmd = "quest", bot = botName, sub = "observe" });
            Require(UInt(enrollment, "id") == characterId && Bool(enrollment, "enrollmentRegistered") == true
                && Bool(enrollment, "enrollmentSchedulerRunning") == true
                && Bool(enrollment, "enrollmentIdleWakeEnabled") == true && Bool(enrollment, "withholdTurnIn") == false,
                "Ordinary enrollment capability unavailable");
            Require(Number(enrollment, "idleWakeIntervalSeconds") == 5 && Bool(enrollment, "homesteadEnabled") == false,
                "Actual idle5 cadence and homestead OFF are required");
            phase = "OBSERVE";
            watch.Start();
            start = await Bridge(new { cmd = "quest", bot = botName, sub = "observe" });
            proof = new(characterId, start.Value, protocolReady: wakeCount == 1);
            latestProofAccepted = proof.EvidenceFault == null && proof.JoinedAt.HasValue;
            if (latestRawPersisted && latestProofAccepted)
            {
                var audit = start.Value.GetProperty("actorAudit");
                if (audit.GetArrayLength() > 0) auditAfterTraceId = Id(audit[audit.GetArrayLength() - 1], "trace_id")!.Value;
            }
            while ((endReason = OperationStopReason()) == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(PollSeconds, proof.RemainingSeconds(watch.Elapsed.TotalSeconds))));
                Marker();
                if ((endReason = OperationStopReason()) != null) break;
                object request = auditAfterTraceId.HasValue
                    ? new { cmd = "quest", bot = botName, sub = "observe", auditAfterTraceId = auditAfterTraceId.Value }
                    : new { cmd = "quest", bot = botName, sub = "observe" };
                var observation = await Bridge(request);
                if ((endReason = StopReason(watch.Elapsed.TotalSeconds)) != null) break;
                proof.Observe(watch.Elapsed.TotalSeconds, observation);
                latestProofAccepted = proof.EvidenceFault == null && proof.JoinedAt.HasValue
                    && proof.ActorId == UInt(observation, "actorObjId") && proof.Generation == Long(observation, "enrollmentGeneration");
                if (latestRawPersisted && latestProofAccepted)
                {
                    var audit = observation.GetProperty("actorAudit");
                    if (audit.GetArrayLength() > 0) auditAfterTraceId = Id(audit[audit.GetArrayLength() - 1], "trace_id")!.Value;
                }
                if ((endReason = StopReason(watch.Elapsed.TotalSeconds)) != null) break;
                if (proof.ControlTraceId.HasValue && !proof.ControlCompleted)
                {
                    if ((endReason = OperationStopReason()) != null) break;
                    proof.ObserveControl(await Api(HttpMethod.Get, $"/api/actors/actions/{proof.ControlTraceId.Value:D}"));
                    if ((endReason = StopReason(watch.Elapsed.TotalSeconds)) != null) break;
                }
                var candidate = proof.GetInterruptCandidate(observation);
                if (candidate == null || (productive && candidate.QuestId != 251)) continue;
                if ((endReason = OperationStopReason()) != null) break;
                proof.DeclareInterrupt(candidate, DateTime.UtcNow);
                interruptRequests++;
                var acknowledgment = await Api(HttpMethod.Post, "/api/actors/interrupt",
                    new { bot = characterId.ToString(CultureInfo.InvariantCulture), traceId = candidate.VictimTraceId.ToString("D") });
                Require(Bool(acknowledgment, "success") == true && Id(acknowledgment, "trace_id").HasValue,
                    "Own interrupt acknowledgment unavailable");
                proof.AcknowledgeInterrupt(Id(acknowledgment, "trace_id")!.Value);
            }
            if (proof.EvidenceFault != null) throw new InvalidDataException(proof.EvidenceFault);
        }
        catch (Exception ex) { endReason ??= "FIRST_FAULT_FAILED_END"; Fault(ex); }
        finally
        {
            watch.Stop();
            // Freeze only after new work and owned movement stop. A failed
            // preparation has no substitute END; cleanup still runs below.
            phase = "PRE_END_PREPARATION";
            try
            {
                Require(characterId != 0 && proof?.JoinedAt != null, "Owned joined actor unavailable for END preparation");
                bridge?.Dispose();
                bridge = null;
                using (var deadline = Deadline())
                    bridge = await Task.Run(() => new BotDriveClient(E2eStack.BridgePort,
                        NeedsFarmLoopE2eTests.MaximumResponseBytes), deadline.Token).WaitAsync(deadline.Token);
                using var endDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                endPreparation = await BotShortRunEndCapture.CaptureAsync(characterId, proof!.ActorId, proof.Generation,
                    () => Bridge(new { cmd = "quest", bot = botName, sub = "prepare-end", generation = proof.Generation }),
                    () => Api(HttpMethod.Post, "/api/actors/stop", new { bot = characterId.ToString(CultureInfo.InvariantCulture) }),
                    id => Api(HttpMethod.Get, $"/api/actors/actions/{id:D}"),
                    async () =>
                    {
                        phase = "PRE_END_CAPTURE";
                        return await Bridge(new { cmd = "quest", bot = botName, sub = "observe" });
                    }, endDeadline.Token);
                end = endPreparation.Copy;
                endUtc = DateTime.UtcNow;
            }
            catch (Exception ex) { Fault(ex); }
            phase = "POST_END_OWNED_CLEANUP";
            if (characterId != 0)
            {
                try
                {
                    registryRemoved = Bool(await Api(HttpMethod.Post, "/api/bots/remove",
                        new { nameOrId = characterId.ToString(CultureInfo.InvariantCulture) }), "Success") == true;
                    Require(registryRemoved, "Own registry remove failed");
                }
                catch (Exception ex) { Fault(ex); }
                try
                {
                    bridge?.Dispose();
                    bridge = null;
                    using (var deadline = Deadline())
                        bridge = await Task.Run(() => new BotDriveClient(E2eStack.BridgePort,
                            NeedsFarmLoopE2eTests.MaximumResponseBytes), deadline.Token).WaitAsync(deadline.Token);
                    var released = await Bridge(new { cmd = "deactivate", bot = botName });
                    sessionReleased = Bool(released, "removed") == true && UInt(released, "id") == characterId;
                    Require(sessionReleased, "Own persistent session release failed");
                }
                catch (Exception ex) { Fault(ex); }
                try
                {
                    var status = await Api(HttpMethod.Get, "/api/bots/status");
                    Require(Bool(status, "Success") == true && status.GetProperty("Bots").ValueKind == JsonValueKind.Array, "Registry absence unavailable");
                    registryAbsent = !Array(status, "Bots").Any(r => UInt(r, "Id") == characterId);
                    Require(registryAbsent, "Own registry remains enrolled");
                }
                catch (Exception ex) { Fault(ex); }
            }
            else if (provisionRequested) Fault(new InvalidOperationException("Provision intent unbound: no guessed bot cleanup"));
            if (provisionRequested)
            {
                try
                {
                    phase = "POST_END_AFTER_RELEASE";
                    Require(characterId != 0, "Unbound identity: full afterRelease unavailable; no guessed query");
                    afterRelease = await Durable();
                    using (var deadline = Deadline()) bodyAfterRelease = await ReadQuestBodyDurable(characterId, sqlCodec!, deadline.Token);
                    Raw(new { query = "full owned character/account/abilities/items afterRelease" }, bodyAfterRelease);
                    releaseProved = end.HasValue && sqlCodec != null
                        && MatchesQuestDurable(afterRelease, end.Value, sqlCodec, bodyAfterRelease)
                        && MatchesQuestBody(bodyAfterRelease, end.Value, sqlCodec, endUtc!.Value);
                    Require(releaseProved, "AfterRelease full frozen-END conservation UNKNOWN");
                }
                catch (Exception ex) { Fault(ex); }
                try
                {
                    phase = "POST_END_ORDINARY_PERSISTENCE";
                    waitStartedAtUtc = DateTime.UtcNow;
                    var margin = Stopwatch.StartNew();
                    while (true)
                    {
                        var remaining = FlushSeconds - margin.Elapsed.TotalSeconds;
                        if (remaining <= 0) break;
                        await Task.Delay(TimeSpan.FromSeconds(remaining));
                    }
                    waitSeconds = margin.Elapsed.TotalSeconds;
                }
                catch (Exception ex) { Fault(ex); }
                finally
                {
                    try
                    {
                        phase = "POST_END_AFTER_FLUSH";
                        Require(characterId != 0, "Unbound identity: full afterFlush unavailable; no guessed query");
                        afterFlush = await Durable();
                        using (var deadline = Deadline()) bodyAfterFlush = await ReadQuestBodyDurable(characterId, sqlCodec!, deadline.Token);
                        Raw(new { query = "full owned character/account/abilities/items afterFlush" }, bodyAfterFlush);
                        flushProved = end.HasValue && sqlCodec != null
                            && MatchesQuestDurable(afterFlush, end.Value, sqlCodec, bodyAfterFlush)
                            && MatchesQuestBody(bodyAfterFlush, end.Value, sqlCodec, endUtc!.Value);
                        Require(flushProved, "AfterFlush full frozen-END conservation UNKNOWN");
                        Require(Md5(canonicalPath) == canonicalMd5 && Md5(E2eStack.RuntimeSqlite) == runtimeMd5,
                            "Canonical/runtime identity changed");
                    }
                    catch (Exception ex) { Fault(ex); }
                }
            }
            bridge?.Dispose();
            control?.Dispose();
        }
        if (File.Exists(markerPath)) Fault(new InvalidDataException("Runner fault marker: no promotion; cleanup retained"));
        var passed = ObjectiveProved() && releaseProved && flushProved
            && registryRemoved && sessionReleased && registryAbsent && wakeCount == 1 && interruptRequests == 1
            && waitSeconds >= FlushSeconds && failures.Count == 0 && end.HasValue
            && latestRawPersisted && latestProofAccepted && latestCopyFault == null;
        var endCopyProvenance = end.HasValue ? new
        {
            source = "Fresh full owned passive+SQL END after isolated scheduler shutdown and exact completed ordinary Stop; no gameplay credit",
            rawPersisted = endPreparation != null, preparationProved = endPreparation != null,
            gameplayCopyCapturedAtUtc = latestCopyCapturedAtUtc,
            gameplayRawPersisted = latestRawPersisted, gameplayProofAccepted = latestProofAccepted,
            gameplayCopyFault = latestCopyFault, id = UInt(end.Value, "id"), actorCharId = UInt(end.Value, "actorCharId"),
            actorObjId = UInt(end.Value, "actorObjId"), enrollmentGeneration = Long(end.Value, "enrollmentGeneration"),
            snapshotStartedAtUtc = Time(end.Value, "snapshotStartedAtUtc"), observedAtUtc = Time(end.Value, "observedAtUtc"),
            sqlSnapshotStartedAtUtc = Time(end.Value.GetProperty("ownedSqlCopy"), "SnapshotStartedAtUtc"),
            sqlObservedAtUtc = Time(end.Value.GetProperty("ownedSqlCopy"), "ObservedAtUtc"),
            progressCredit = "NOT_PROGRESS_CREDIT: final preparation never enters gameplay evidence consumer"
        } : null;
        var report = JsonSerializer.Serialize(new
        {
            scenario, runId, botName, characterId, passed, endReason, endUtc,
            endPreparation = endPreparation is { } prepared
                ? new { prepared.StopTraceId, prepared.Scheduler, prepared.Stop } : null,
            frozenEndRetained = end.HasValue, rawPersisted = latestRawPersisted, proofAccepted = latestProofAccepted,
            endCopyProvenance, latestCopyFault,
            autonomousSeconds = watch.Elapsed.TotalSeconds, configuration,
            deadlines = new { bootExternalSeconds = 360, fixtureSeconds = 360, observeSeconds = 900,
                noAuthoritativeProgressSeconds = 300, apiSeconds = 5, pollSeconds = 1, flushSeconds = 325 },
            bounds = new { maximumResponseBytes = NeedsFarmLoopE2eTests.MaximumResponseBytes,
                harnessBytes = NeedsFarmLoopE2eTests.MaximumHarnessEvidenceBytes,
                reserveBytes = NeedsFarmLoopE2eTests.ReservedEvidenceBytes, budget.RawBytes, budget.ReservedBytes,
                externalPipesBytes = 64L * 1024 * 1024, externalRunnerBytes = 8L * 1024 * 1024, capFailed, suppressedCleanupRecords },
            waitStartedAtUtc, waitSeconds, before, start, end, afterRelease, afterFlush,
            bodyBefore, bodyAfterRelease, bodyAfterFlush,
            sqlCodec,
            bodyReleaseDifferences = end.HasValue && bodyAfterRelease != null && sqlCodec != null
                ? QuestBodyDifferences(bodyAfterRelease, end.Value, sqlCodec, endUtc!.Value) : null,
            bodyFlushDifferences = end.HasValue && bodyAfterFlush != null && sqlCodec != null
                ? QuestBodyDifferences(bodyAfterFlush, end.Value, sqlCodec, endUtc!.Value) : null,
            releaseBookkeeping = end.HasValue && bodyAfterRelease != null && sqlCodec != null
                ? QuestBookkeepingEvidence(bodyAfterRelease, end.Value, sqlCodec, endUtc!.Value) : null,
            flushBookkeeping = end.HasValue && bodyAfterFlush != null && sqlCodec != null
                ? QuestBookkeepingEvidence(bodyAfterFlush, end.Value, sqlCodec, endUtc!.Value) : null,
            work = proof?.QuestWork, recoveries = proof?.Recoveries, evidenceFault = proof?.EvidenceFault,
            productiveRootQuest = productive ? 251u : (uint?)null,
            productiveCompletionProved = productive && proof != null && ProductiveQuestEvidence.Proved(proof),
            continuationQuest = continuation ? 324u : (uint?)null,
            continuationCompletionProved = continuation && ObjectiveProved(),
            completedGoals = proof?.CompletedGoals,
            wakeCount, interruptRequests, registryRemoved, sessionReleased, registryAbsent, releaseProved, flushProved,
            provenance = new { source = E2eStack.MeasuredSourceIdentity, E2eStack.PublishSourceIdentity,
                testAssemblySha256 = Sha256(typeof(BotShortRunTests).Assembly.Location),
                runtimeGameSha256 = Sha256(Path.Combine(E2eStack.RuntimeGameDir, "AAEmu.Game.dll")),
                scope = "Runner frozen build receipts join measured dirty sources to adopted binaries" },
            canonicalMd5, runtimeMd5, identityPath, markerPath, failures,
            scope = continuation
                ? "Unchanged productive251 proof, then independently accepted supplied-item324, ordinary NPC report/completion and fresh distinct acceptance beyond251/324; frozen-END release/325sflush. Level10 starter setup; no324 harvesting, further quest completion, restart/general loop/H claim."
                : productive
                ? "One independently accepted Q251 with request-parented Loot numeric/item gain, same-root interruption/recovery, actual ordinary Q251 completion/turn-in then distinct fresh quest acceptance and unchanged full frozen-END durability. Seeded natural level10 body; no subsequent quest completion, restart, general loop or H claim."
                : "Immutable fallback Move purpose, own Running interrupt, fresh autonomous reselection, request-parented Loot numeric objective+item gain and full frozen-END durability. No Q251 completion/restart/general gameplay claim."
        }, Indented);
        try { budget.ReserveFinal(report); }
        catch (InvalidDataException ex)
        {
            passed = false;
            releaseProved = false;
            flushProved = false;
            var originalEndReason = endReason;
            endReason = "FINAL_REPORT_CAP_FAILED_END";
            report = JsonSerializer.Serialize(new { scenario, passed, characterId, endReason,
                originalEndReason, originalFault = failures.FirstOrDefault(), endUtc, frozenEndRetained = false,
                rawPersisted = latestRawPersisted, proofAccepted = latestProofAccepted, endCopyProvenance, latestCopyFault,
                scope = "NOT_PROVED: full frozen-END values/custody unavailable in bounded final report; conservation UNKNOWN",
                releaseProved, flushProved, registryRemoved, sessionReleased, registryAbsent, waitSeconds, capFailed,
                reportBoundFault = new { type = ex.GetType().Name, message = ex.Message, atUtc = DateTime.UtcNow }, failures });
            budget.ReserveFinal(report);
        }
        await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(reportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine((continuation ? "QUEST-CONTINUATION-REPORT " : productive ? "PRODUCTIVE-QUEST-REPORT " : "QUEST-PURPOSE-REPORT ") + reportPath);
        Assert.True(passed, $"{scenario} NOT_PROVED ({endReason}); {reportPath}");
    }

    internal static bool OverallProved(bool purpose, bool persistence, bool cleanup, bool noInfrastructureFault)
        => purpose && persistence && cleanup && noInfrastructureFault;


    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static async Task<QuestSqlCodec> ReadQuestSqlCodec(CancellationToken token)
    {
        var effective = ReadEffectiveConfiguration();
        try
        {
            Require(effective["Connections:MySQLProvider:Host"] == E2eStack.DbHost
                && effective.GetValue<int>("Connections:MySQLProvider:Port") == E2eStack.DbPort
                && effective["Connections:MySQLProvider:User"] == "root"
                && effective["Connections:MySQLProvider:Database"] == "aaemu_game"
                && effective["Connections:MySQLProvider:Password"] == E2eStack.DbPassword,
                "Frozen writer configuration differs from exact owned reader endpoint/identity");
        }
        finally { (effective as IDisposable)?.Dispose(); }
        token.ThrowIfCancellationRequested();
        using var connection = new MySql.Data.MySqlClient.MySqlConnection(new MySql.Data.MySqlClient.MySqlConnectionStringBuilder
        {
            Server = E2eStack.DbHost, Port = (uint)E2eStack.DbPort, UserID = "root",
            Password = E2eStack.DbPassword, Database = "aaemu_game", ConnectionTimeout = 5, TreatTinyAsBoolean = false
        }.ConnectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT @@sql_mode AS sql_mode,@@session.time_zone AS session_timezone,"
            + "@@system_time_zone AS system_timezone,UTC_TIMESTAMP(6) AS utc_now,NOW(6) AS session_now,"
            + "@@global.sql_mode AS global_sql_mode,@@global.time_zone AS global_timezone";
        JsonElement environment;
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("SQL environment unobservable");
            environment = JsonSerializer.SerializeToElement(new { sql_mode = reader.GetString(0),
                session_timezone = reader.GetString(1), system_timezone = reader.GetString(2),
                utc_now = reader.GetDateTime(3), session_now = reader.GetDateTime(4),
                provider = typeof(MySql.Data.MySqlClient.MySqlConnection).Assembly.FullName, treatTinyAsBoolean = false,
                global_sql_mode = reader.GetString(5), global_timezone = reader.GetString(6),
                writerSource = "AAEmu.Commons/Utils/DB/MySQL.cs43-61 default session; verified frozen own endpoint/root user; credentials omitted" });
        }
        var zone = String(environment, "session_timezone");
        var systemZone = String(environment, "system_timezone");
        Require(zone == String(environment, "global_timezone")
            && String(environment, "sql_mode") == String(environment, "global_sql_mode"),
            "Default writer session/global SQL mode/timezone correspondence unproved");
        Require((zone is "+00:00" or "UTC" || zone == "SYSTEM" && systemZone is "UTC" or "Etc/UTC")
            && Time(environment, "utc_now") == Time(environment, "session_now"), "SQL UTC datetime interpretation unproved");
        var tables = new[] { "characters", "accounts", "abilities", "items", "quests", "completed_quests", "actabilities" };
        command.CommandText = "SELECT TABLE_NAME,COLUMN_NAME,DATA_TYPE,COLUMN_TYPE,DATETIME_PRECISION "
            + "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ("
            + string.Join(",", tables.Select((_, i) => "@t" + i)) + ") ORDER BY TABLE_NAME,ORDINAL_POSITION";
        for (var i = 0; i < tables.Length; i++) command.Parameters.AddWithValue("@t" + i, tables[i]);
        var columns = new List<QuestSqlColumn>();
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (columns.Count >= 256) throw new InvalidDataException("SQL_SCHEMA_ROW_BOUND");
                columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)));
            }
        Require(tables.All(t => columns.Any(c => c.Table == t))
            && columns.Select(c => (c.Table, c.Name)).Distinct().Count() == columns.Count, "SQL declared schema missing or ambiguous");
        command.Parameters.Clear();
        command.CommandText = "SELECT ACTION_TIMING,EVENT_MANIPULATION,ACTION_STATEMENT "
            + "FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=DATABASE() AND EVENT_OBJECT_TABLE=@table";
        command.Parameters.AddWithValue("@table", "accounts");
        var triggers = new List<JsonElement>();
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (triggers.Count >= 16) throw new InvalidDataException("SQL_TRIGGER_ROW_BOUND");
                triggers.Add(JsonSerializer.SerializeToElement(new { timing = reader.GetString(0),
                    operation = reader.GetString(1), statement = reader.GetString(2) }));
            }
        Require(triggers.Count == 1 && String(triggers[0], "timing") == "BEFORE"
            && String(triggers[0], "operation") == "UPDATE"
            && new string(String(triggers[0], "statement").Where(c => !char.IsWhiteSpace(c) && c != '`' && c != ';').ToArray())
                .Equals("SETNEW.last_updated=UTC_TIMESTAMP()", StringComparison.OrdinalIgnoreCase),
            "Deployed accounts.last_updated owned-write trigger unproved");
        var round = !String(environment, "sql_mode").Split(',').Contains("TIME_TRUNCATE_FRACTIONAL", StringComparer.Ordinal);
        var probes = new List<JsonElement>();
        var samples = new[] { new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).AddTicks(1499999),
            new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).AddTicks(5000000),
            new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).AddTicks(9999999), DateTime.MinValue };
        foreach (var precision in columns.Where(c => c.DataType == "datetime").Select(c => c.DatePrecision).Distinct())
        {
            Require(precision is >= 0 and <= 6, "Deployed DATETIME precision unknown");
            foreach (var sample in samples)
            {
                command.Parameters.Clear();
                command.CommandText = $"SELECT CAST(@value AS DATETIME(6)),CAST(@value AS DATETIME({precision}))";
                command.Parameters.AddWithValue("@value", sample);
                using var reader = await command.ExecuteReaderAsync(token);
                Require(await reader.ReadAsync(token) && !reader.IsDBNull(0) && !reader.IsDBNull(1), "DATETIME parameter CAST unsupported");
                var bound = reader.GetDateTime(0);
                var saved = reader.GetDateTime(1);
                Require(bound.Ticks == sample.Ticks / 10 * 10 && saved.Ticks == SaveDateTime(sample, precision!.Value, round).Ticks,
                    "Provider/schema/sql_mode DATETIME codec differs from corroborated source interpretation");
                probes.Add(JsonSerializer.SerializeToElement(new { source = sample, bound, saved, precision, round }));
            }
        }
        var codec = new QuestSqlCodec(columns.ToArray(), String(environment, "sql_mode"), zone, systemZone,
            round, environment, probes.ToArray(), triggers[0]);
        Require(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(codec)) <= NeedsFarmLoopE2eTests.MaximumResponseBytes,
            "SQL_CODEC_RESPONSE_BYTE_BOUND");
        return codec;
    }

    private static DateTime SaveDateTime(DateTime value, int precision, bool round)
    {
        var ticks = value.Ticks / 10 * 10; // Actual bound DATETIME(6) corroborated by deployed CAST probes.
        var unit = precision switch { 0 => 10000000L, 1 => 1000000L, 2 => 100000L, 3 => 10000L,
            4 => 1000L, 5 => 100L, 6 => 10L, _ => throw new InvalidDataException("Unknown DATETIME precision") };
        return new DateTime(checked((ticks + (round ? unit / 2 : 0)) / unit * unit), DateTimeKind.Unspecified);
    }

    private static async Task<QuestBodyDurable> ReadQuestBodyDurable(uint characterId, QuestSqlCodec codec, CancellationToken token)
    {
        var begin = DateTime.UtcNow;
        Require(HasCharacterFloatSchema(codec), "Owned character requires exactly six declared FLOAT columns and collision-free mirrors");
        using var connection = new MySql.Data.MySqlClient.MySqlConnection(new MySql.Data.MySqlClient.MySqlConnectionStringBuilder
        {
            Server = E2eStack.DbHost, Port = (uint)E2eStack.DbPort, UserID = "root",
            Password = E2eStack.DbPassword, Database = "aaemu_game", ConnectionTimeout = 5, TreatTinyAsBoolean = false
        }.ConnectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT @@sql_mode,@@session.time_zone;SELECT c.*,"
            + "CAST(c.x AS DOUBLE) AS __quest_purpose_float_x,CAST(c.y AS DOUBLE) AS __quest_purpose_float_y,"
            + "CAST(c.z AS DOUBLE) AS __quest_purpose_float_z,CAST(c.roll AS DOUBLE) AS __quest_purpose_float_roll,"
            + "CAST(c.pitch AS DOUBLE) AS __quest_purpose_float_pitch,CAST(c.yaw AS DOUBLE) AS __quest_purpose_float_yaw "
            + "FROM characters c WHERE c.id=@id;"
            + "SELECT a.* FROM accounts a JOIN characters c ON c.account_id=a.account_id WHERE c.id=@id;"
            + "SELECT * FROM abilities WHERE owner=@id ORDER BY id;SELECT * FROM items WHERE owner=@id ORDER BY id";
        command.Parameters.AddWithValue("@id", characterId);
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Owned SQL session unobservable");
        var mode = reader.GetString(0);
        var zone = reader.GetString(1);
        Require(mode == codec.SqlMode && zone == codec.SessionTimeZone, "Owned SQL reader codec changed");
        if (await reader.ReadAsync(token) || !await reader.NextResultAsync(token))
            throw new InvalidDataException("Owned SQL session result ambiguous or incomplete");
        var bytes = 0;
        var floatValues = new Dictionary<string, double>(CharacterFloatNames.Length, StringComparer.Ordinal);
        JsonElement CopyRow(bool characterRow)
        {
            var values = new Dictionary<string, object?>(reader.FieldCount - (characterRow ? CharacterFloatNames.Length : 0));
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (characterRow && CharacterFloatAliases.TryGetValue(name, out var field))
                {
                    Require(!reader.IsDBNull(i) && reader.GetFieldType(i) == typeof(double), "Owned FLOAT mirror must be typed DOUBLE");
                    var value = reader.GetDouble(i);
                    Require(IsExactFloatMirror(value) && floatValues.TryAdd(field, value), "Owned FLOAT mirror is nonfinite, inexact or duplicated");
                    continue;
                }
                if (!reader.IsDBNull(i) && reader.GetFieldType(i) == typeof(byte[])
                    && reader.GetBytes(i, 0, null, 0, 0) > 65536)
                    throw new InvalidDataException("DURABLE_BLOB_BYTE_BOUND");
                values.Add(name, reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            var json = JsonSerializer.SerializeToElement(values);
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(json.GetRawText()));
            if (bytes > NeedsFarmLoopE2eTests.MaximumResponseBytes) throw new InvalidDataException("DURABLE_RESPONSE_BYTE_BOUND");
            return json;
        }
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Owned full character missing");
        var character = CopyRow(true);
        var mirrors = JsonSerializer.SerializeToElement(floatValues);
        bytes = checked(bytes + Encoding.UTF8.GetByteCount(mirrors.GetRawText()));
        Require(bytes <= NeedsFarmLoopE2eTests.MaximumResponseBytes, "DURABLE_RESPONSE_BYTE_BOUND");
        Require(HasDeclaredColumns(character, "characters", codec) && HasCharacterFloatMirrors(mirrors, codec)
            && UInt(character, "id") == characterId, "Owned full character/mirrors unavailable");
        if (await reader.ReadAsync(token) || !await reader.NextResultAsync(token) || !await reader.ReadAsync(token))
            throw new InvalidDataException("Owned full character/account ambiguous or missing");
        var account = CopyRow(false);
        Require(HasDeclaredColumns(account, "accounts", codec)
            && UInt(character, "account_id") == UInt(account, "account_id"), "Owned full account identity/columns unavailable");
        if (await reader.ReadAsync(token) || !await reader.NextResultAsync(token))
            throw new InvalidDataException("Owned full account/abilities ambiguous or missing");
        var abilities = new List<JsonElement>();
        while (await reader.ReadAsync(token))
        {
            if (abilities.Count >= 256) throw new InvalidDataException("DURABLE_ABILITY_ROW_BOUND");
            var row = CopyRow(false);
            Require(HasDeclaredColumns(row, "abilities", codec) && UInt(row, "owner") == characterId, "Owned full ability identity/columns unavailable");
            abilities.Add(row);
        }
        if (!await reader.NextResultAsync(token)) throw new InvalidDataException("Owned full items missing result");
        var items = new List<JsonElement>();
        while (await reader.ReadAsync(token))
        {
            if (items.Count >= 256) throw new InvalidDataException("DURABLE_ITEM_ROW_BOUND");
            var row = CopyRow(false);
            Require(HasDeclaredColumns(row, "items", codec) && UInt(row, "owner") == characterId, "Owned full item identity/columns unavailable");
            items.Add(row);
        }
        return new(begin, DateTime.UtcNow, character, mirrors, account, abilities.ToArray(), items.ToArray(), mode, zone);
    }

    internal sealed record QuestBookkeeping(string Table, string Column, JsonElement? Before, JsonElement? After,
        DateTime LowerBoundUtc, DateTime UpperBoundUtc, bool Proved, string Provenance);

    internal static QuestBookkeeping[] QuestBookkeepingEvidence(QuestBodyDurable after, JsonElement end,
        QuestSqlCodec codec, DateTime frozenEndUtc)
    {
        var proofs = new List<QuestBookkeeping>(2);
        foreach (var pair in new[] { (Table: "characters", Projection: "fullCharacter", Column: "updated_at", Row: after.Character),
            (Table: "accounts", Projection: "fullAccount", Column: "last_updated", Row: after.Account) })
        {
            var column = codec.Columns.SingleOrDefault(c => c.Table == pair.Table && c.Name == pair.Column);
            var before = end.TryGetProperty(pair.Projection, out var projection) && projection.ValueKind == JsonValueKind.Object
                && projection.TryGetProperty(pair.Column, out var original) ? original : default;
            var stored = pair.Row.TryGetProperty(pair.Column, out var final) ? final : default;
            var precision = column?.DatePrecision;
            var lower = precision is >= 0 and <= 6
                ? SaveDateTime(frozenEndUtc, pair.Table == "accounts" ? 0 : precision.Value,
                    pair.Table == "characters" && codec.RoundFraction) : frozenEndUtc;
            var upper = precision is >= 0 and <= 6
                ? SaveDateTime(after.ObservedAtUtc, pair.Table == "accounts" ? 0 : precision.Value,
                    pair.Table == "characters" && codec.RoundFraction) : after.ObservedAtUtc;
            var same = column != null && SameQuestColumn(stored, before, column, codec);
            var bounded = column?.DataType == "datetime" && precision is >= 0 and <= 6
                && before.ValueKind == JsonValueKind.String && before.TryGetDateTime(out _)
                && stored.ValueKind == JsonValueKind.String && stored.TryGetDateTime(out var at)
                && at.Ticks >= lower.Ticks && at.Ticks <= upper.Ticks
                && at.Ticks == SaveDateTime(at, precision.Value, false).Ticks
                && (pair.Table != "accounts" || at.Ticks % TimeSpan.TicksPerSecond == 0)
                && after.SnapshotStartedAtUtc >= frozenEndUtc && after.ObservedAtUtc >= after.SnapshotStartedAtUtc
                && UInt(after.Character, "id") == UInt(end, "id")
                && UInt(after.Character, "account_id") == UInt(after.Account, "account_id");
            proofs.Add(new(pair.Table, pair.Column, before.ValueKind == JsonValueKind.Undefined ? null : before,
                stored.ValueKind == JsonValueKind.Undefined ? null : stored, lower, upper, same || bounded,
                pair.Table == "characters"
                    ? "Character.Save assigns UTC now (Character.cs2783); corroborated deployed DATETIME codec and exact owned END→release/read interval"
                    : "Deployed observed BEFORE UPDATE SET NEW.last_updated=UTC_TIMESTAMP() trigger; UTC_TIMESTAMP() precision0; exact owned END→release/read interval"));
        }
        return proofs.ToArray();
    }

    internal static string[] QuestBodyDifferences(QuestBodyDurable after, JsonElement end,
        QuestSqlCodec codec, DateTime frozenEndUtc)
    {
        var differences = new List<string>();
        var mirrorsValid = HasCharacterFloatMirrors(after.CharacterFloatMirrors, codec);
        if (!mirrorsValid) differences.Add("Mandatory six exact owned FLOAT mirrors or declared FLOAT schema unavailable");
        if (after.SqlMode != codec.SqlMode || after.SessionTimeZone != codec.SessionTimeZone
            || after.SnapshotStartedAtUtc < frozenEndUtc || after.ObservedAtUtc < after.SnapshotStartedAtUtc)
            differences.Add("Post-release reader codec or full copy interval differs from corroborated domain");
        var bookkeeping = QuestBookkeepingEvidence(after, end, codec, frozenEndUtc);
        if (!end.TryGetProperty("ownedSqlCopy", out var sqlCopy) || sqlCopy.ValueKind != JsonValueKind.Object
            || String(sqlCopy, "SqlMode") != codec.SqlMode || String(sqlCopy, "SessionTimeZone") != codec.SessionTimeZone
            || Time(sqlCopy, "SnapshotStartedAtUtc") < Time(end, "observedAtUtc")
            || Time(sqlCopy, "ObservedAtUtc") < Time(sqlCopy, "SnapshotStartedAtUtc")
            || Time(sqlCopy, "ObservedAtUtc") > frozenEndUtc
            || Time(sqlCopy, "SnapshotStartedAtUtc") == null || Time(sqlCopy, "ObservedAtUtc") == null
            || Time(end, "fullAccountSqlStartedAtUtc") != Time(sqlCopy, "SnapshotStartedAtUtc")
            || Time(end, "fullAccountSqlObservedAtUtc") != Time(sqlCopy, "ObservedAtUtc")
            || !sqlCopy.TryGetProperty("Character", out var sqlOwner) || sqlOwner.ValueKind != JsonValueKind.Object
            || UInt(sqlOwner, "id") != UInt(end, "id")
            || !HasDeclaredColumns(sqlOwner, "characters", codec)
            || !sqlCopy.TryGetProperty("CharacterFloatMirrors", out var sqlMirrors) || !HasCharacterFloatMirrors(sqlMirrors, codec)
            || !sqlCopy.TryGetProperty("Account", out var sqlAccount) || sqlAccount.ValueKind != JsonValueKind.Object
            || UInt(sqlOwner, "account_id") != UInt(sqlAccount, "account_id")
            || !end.TryGetProperty("fullAccount", out var account) || !JsonElement.DeepEquals(account, sqlAccount))
            differences.Add("Distinct frozen-END owned SQL copy interval unavailable or invalid");
        foreach (var pair in new[] { (Table: "characters", Name: "fullCharacter", Row: after.Character),
            (Table: "accounts", Name: "fullAccount", Row: after.Account) })
        {
            if (!end.TryGetProperty(pair.Name, out var before) || before.ValueKind != JsonValueKind.Object
                || !HasDeclaredColumns(pair.Row, pair.Table, codec))
            {
                differences.Add(pair.Name + ": missing full row or deployed column set");
                continue;
            }
            foreach (var field in pair.Row.EnumerateObject())
            {
                var available = before.TryGetProperty(field.Name, out var old);
                // These two DB-only fields have no live Character property.
                // Keep their distinct owned SQL copy provenance, never overlay it onto a live field.
                if (!available && pair.Table == "characters" && field.Name is "deleted" or "reported_as_bot_count"
                    && sqlCopy.ValueKind == JsonValueKind.Object && sqlCopy.TryGetProperty("Character", out var sqlCharacter)
                    && sqlCharacter.ValueKind == JsonValueKind.Object)
                    available = sqlCharacter.TryGetProperty(field.Name, out old);
                var column = codec.Columns.Single(c => c.Table == pair.Table && c.Name == field.Name);
                var stored = field.Value;
                if (pair.Table == "characters" && column.DataType == "float")
                {
                    if (!mirrorsValid || field.Value.ValueKind != JsonValueKind.Number
                        || !field.Value.TryGetDouble(out var raw) || !double.IsFinite(raw))
                    {
                        differences.Add("fullCharacter." + field.Name + ": raw column/exact FLOAT mirror unavailable");
                        continue;
                    }
                    stored = after.CharacterFloatMirrors.GetProperty(field.Name);
                }
                if (!available || (!SameQuestColumn(stored, old, column, codec)
                    && !bookkeeping.Any(b => b.Table == pair.Table && b.Column == field.Name && b.Proved)))
                    differences.Add(pair.Name + "." + field.Name + ": END=" + (available ? old.GetRawText() : "UNOBSERVED")
                        + "; durable direct=" + field.Value.GetRawText() + "; compared save-domain=" + stored.GetRawText()
                        + "; UNKNOWN unaccounted difference");
            }
            foreach (var field in before.EnumerateObject())
                if (!pair.Row.TryGetProperty(field.Name, out _)) differences.Add(pair.Name + "." + field.Name + ": durable column missing");
        }
        if (Long(after.Account, "labor") != Long(end, "labor"))
            differences.Add("Account labor differs from authoritative END labor; no regeneration allowance");
        if (!end.TryGetProperty("abilities", out var abilities) || abilities.ValueKind != JsonValueKind.Array
            || !SameQuestRows(after.Abilities, abilities.EnumerateArray().ToArray(), "abilities", "id", codec))
            differences.Add("Full abilities keys/rows differ");
        return differences.ToArray();
    }

    internal static bool MatchesQuestBody(QuestBodyDurable after, JsonElement end, QuestSqlCodec codec, DateTime frozenEndUtc)
        => QuestBodyDifferences(after, end, codec, frozenEndUtc).Length == 0;

    internal static bool MatchesQuestDurable(NeedsFarmLoopE2eTests.FarmDurable durable, JsonElement end,
        QuestSqlCodec codec, QuestBodyDurable body)
    {
        if (durable.CharacterId != UInt(end, "id") || durable.AccountId != UInt(end, "accountId")
            || durable.Level != Long(end, "level") || durable.Experience != Long(end, "experience")
            || durable.Money != Long(end, "money") || durable.Labor != Long(end, "labor")
            || durable.Crops.Count != 0 || durable.Houses.Length != 0
            || !HasDeclaredColumns(body.Character, "characters", codec)
            || UInt(body.Character, "id") != durable.CharacterId || UInt(body.Character, "account_id") != durable.AccountId
            || !HasCharacterFloatMirrors(body.CharacterFloatMirrors, codec)
            || !end.TryGetProperty("saveDomain", out var domain) || domain.ValueKind != JsonValueKind.Object
            || durable.WorldId != UInt(domain, "worldId") || durable.ZoneId != UInt(domain, "zoneId")
            || Number(domain, "x") is not { } x || Number(domain, "y") is not { } y || Number(domain, "z") is not { } z
            || BitConverter.SingleToInt32Bits((float)body.CharacterFloatMirrors.GetProperty("x").GetDouble()) != BitConverter.SingleToInt32Bits((float)x)
            || BitConverter.SingleToInt32Bits((float)body.CharacterFloatMirrors.GetProperty("y").GetDouble()) != BitConverter.SingleToInt32Bits((float)y)
            || BitConverter.SingleToInt32Bits((float)body.CharacterFloatMirrors.GetProperty("z").GetDouble()) != BitConverter.SingleToInt32Bits((float)z)) return false;
        foreach (var name in new[] { "ownItems", "activeQuests", "completedQuests", "actabilities" })
            if (!end.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array) return false;
        var items = new Dictionary<ulong, JsonElement>();
        foreach (var row in Array(end, "ownItems"))
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != ItemColumns.Length
                || UInt(row, "owner") != durable.CharacterId || !row.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out var itemId) || !items.TryAdd(itemId, row)
                || ItemColumns.Any(p => !row.TryGetProperty(p.Live, out _))) return false;
        }
        if (items.Count != durable.Items.Count || body.Items.Length != durable.Items.Count) return false;
        var seenItems = new HashSet<ulong>();
        foreach (var row in body.Items)
        {
            if (!HasDeclaredColumns(row, "items", codec) || row.EnumerateObject().Count() != ItemColumns.Length
                || !row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                || !id.TryGetUInt64(out var itemId) || !seenItems.Add(itemId) || !items.TryGetValue(itemId, out var live)
                || ItemColumns.Any(p => !row.TryGetProperty(p.Db, out var saved)
                    || !SameQuestColumn(saved, live.GetProperty(p.Live),
                        codec.Columns.Single(c => c.Table == "items" && c.Name == p.Db), codec))) return false;
        }
        if (!durable.Items.Select(i => i.Id).ToHashSet().SetEquals(seenItems)) return false;
        // Farm rows remain retained provenance; the non-folding supplemental
        // provider is the actual declared-column truth (grade is numeric even TINYINT(1)).
        foreach (var saved in durable.Items)
        {
            var actual = body.Items.Single(i => i.GetProperty("id").GetUInt64() == saved.Id);
            if (saved.Template != UInt(actual, "template_id") || saved.Count != Long(actual, "count")
                || !SameBytes(saved.Details, String(actual, "details"))) return false;
        }
        var actabilities = Array(end, "actabilities").ToArray();
        if (actabilities.Any(a => a.ValueKind != JsonValueKind.Object || a.EnumerateObject().Count() != 3)
            || actabilities.Select(a => UInt(a, "id")).Distinct().Count() != actabilities.Length
            || !durable.Actabilities.SequenceEqual(actabilities.Select(a => new NeedsFarmLoopEvidence.Actability(
                UInt(a, "id"), Long(a, "point"), Long(a, "step"))).OrderBy(a => a.Id))) return false;
        var quests = new List<JsonElement>();
        foreach (var row in Array(end, "activeQuests"))
        {
            if (!Enum.TryParse<AAEmu.Game.Models.Game.Quests.Static.QuestStatus>(String(row, "status"), out var status)
                || !Enum.IsDefined(status) || UInt(row, "owner") != durable.CharacterId) return false;
            quests.Add(JsonSerializer.SerializeToElement(new { id = UInt(row, "id"), template_id = UInt(row, "questId"),
                owner = UInt(row, "owner"), status = (int)status, data = String(row, "data") }));
        }
        var blocks = new List<JsonElement>();
        foreach (var row in Array(end, "completedQuests"))
        {
            if (UInt(row, "owner") != durable.CharacterId) return false;
            blocks.Add(JsonSerializer.SerializeToElement(new { id = UInt(row, "blockId"),
                owner = UInt(row, "owner"), data = String(row, "data") }));
        }
        return SameQuestRows(durable.Quests.Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray(),
                quests.ToArray(), "quests", "id", codec)
            && SameQuestRows(durable.CompletedQuests.Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray(),
                blocks.ToArray(), "completed_quests", "id", codec);
    }

    private static bool HasCharacterFloatSchema(QuestSqlCodec codec)
    {
        var mask = 0;
        foreach (var column in codec.Columns)
        {
            if (column.Table != "characters") continue;
            if (column.Name.StartsWith("__quest_purpose_float_", StringComparison.OrdinalIgnoreCase)) return false;
            if (column.DataType != "float") continue;
            var index = System.Array.IndexOf(CharacterFloatNames, column.Name);
            if (index < 0 || (mask & (1 << index)) != 0) return false;
            mask |= 1 << index;
        }
        return mask == (1 << CharacterFloatNames.Length) - 1;
    }

    private static bool IsExactFloatMirror(double value)
        => double.IsFinite(value) && float.IsFinite((float)value)
            && BitConverter.DoubleToInt64Bits((double)(float)value) == BitConverter.DoubleToInt64Bits(value);

    private static bool HasCharacterFloatMirrors(JsonElement mirrors, QuestSqlCodec codec)
    {
        if (!HasCharacterFloatSchema(codec) || mirrors.ValueKind != JsonValueKind.Object
            || mirrors.EnumerateObject().Count() != CharacterFloatNames.Length) return false;
        foreach (var name in CharacterFloatNames)
            if (!mirrors.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number) || !IsExactFloatMirror(number)) return false;
        return true;
    }


    private static bool HasDeclaredColumns(JsonElement row, string table, QuestSqlCodec codec)
    {
        if (row.ValueKind != JsonValueKind.Object) return false;
        var names = row.EnumerateObject().Select(p => p.Name).ToArray();
        var columns = codec.Columns.Where(c => c.Table == table).Select(c => c.Name).ToArray();
        return columns.Length > 0 && names.Length == names.Distinct().Count()
            && columns.Length == columns.Distinct().Count() && names.ToHashSet().SetEquals(columns);
    }

    internal static bool SameQuestRows(JsonElement[] saved, JsonElement[] expected, string table, string key, QuestSqlCodec codec)
    {
        if (!codec.Columns.Any(c => c.Table == table && c.Name == key)
            || saved.Length != expected.Length || saved.Any(r => !HasDeclaredColumns(r, table, codec))
            || expected.Any(r => !HasDeclaredColumns(r, table, codec))) return false;
        var byKey = new Dictionary<ulong, JsonElement>(expected.Length);
        foreach (var row in expected)
            if (row.GetProperty(key).ValueKind != JsonValueKind.Number || !row.GetProperty(key).TryGetUInt64(out var id)
                || !byKey.TryAdd(id, row)) return false;
        var seen = new HashSet<ulong>();
        foreach (var row in saved)
        {
            if (row.GetProperty(key).ValueKind != JsonValueKind.Number || !row.GetProperty(key).TryGetUInt64(out var id)
                || !seen.Add(id) || !byKey.TryGetValue(id, out var candidate)
                || row.EnumerateObject().Any(p => !SameQuestColumn(p.Value, candidate.GetProperty(p.Name),
                    codec.Columns.Single(c => c.Table == table && c.Name == p.Name), codec))) return false;
        }
        return true;
    }

    private static bool SameBytes(string? saved, string? actual)
    {
        if (saved == null || actual == null) return saved == actual;
        try { return Convert.FromBase64String(saved).AsSpan().SequenceEqual(Convert.FromBase64String(actual)); }
        catch (FormatException) { return false; }
    }

    internal static bool SameQuestColumn(JsonElement saved, JsonElement actual, QuestSqlColumn column, QuestSqlCodec codec)
    {
        if (saved.ValueKind == JsonValueKind.Undefined || actual.ValueKind == JsonValueKind.Undefined) return false;
        if (column.Table == "characters" && column.Name == "online_time")
        {
            if (column.DataType != "int" || column.ColumnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase)
                || saved.ValueKind != JsonValueKind.Number || actual.ValueKind != JsonValueKind.Number
                || !saved.TryGetDecimal(out var stored) || !actual.TryGetDecimal(out var seconds) || seconds < 0
                || stored < 0 || stored > int.MaxValue || decimal.Truncate(stored) != stored) return false;
            var rounded = decimal.Round(seconds, 0, MidpointRounding.AwayFromZero);
            return rounded <= int.MaxValue && stored == checked((int)rounded);
        }
        if (saved.ValueKind == JsonValueKind.Null || actual.ValueKind == JsonValueKind.Null)
            return saved.ValueKind == actual.ValueKind; // No NULL↔MinValue guess.
        if (column.DataType == "datetime")
            return column.DatePrecision is >= 0 and <= 6 && saved.ValueKind == JsonValueKind.String
                && actual.ValueKind == JsonValueKind.String && saved.TryGetDateTime(out var storedDate)
                && actual.TryGetDateTime(out var sourceDate)
                && storedDate.Ticks == SaveDateTime(sourceDate, column.DatePrecision.Value, codec.RoundFraction).Ticks;
        if (column.DataType is "blob" or "tinyblob" or "mediumblob" or "longblob" or "binary" or "varbinary")
            return saved.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.String
                && SameBytes(saved.GetString(), actual.GetString());
        if (column.DataType == "float")
            return saved.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number
                && saved.TryGetSingle(out var sf) && actual.TryGetSingle(out var af) && float.IsFinite(sf) && float.IsFinite(af)
                && BitConverter.SingleToInt32Bits(sf) == BitConverter.SingleToInt32Bits(af);
        if (column.DataType == "double")
            return saved.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number
                && saved.TryGetDouble(out var sd) && actual.TryGetDouble(out var ad) && double.IsFinite(sd) && double.IsFinite(ad)
                && BitConverter.DoubleToInt64Bits(sd) == BitConverter.DoubleToInt64Bits(ad);
        if (column.DataType is "tinyint" or "smallint" or "mediumint" or "int" or "bigint")
        {
            var boolean = column.Table == "characters" && column.Name is "died_in_pvp" or "died_in_pvp_war_zone" or "auto_use_aapoint";
            if (boolean && actual.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return saved.ValueKind == JsonValueKind.Number && saved.TryGetInt64(out var bit)
                    && bit == (actual.GetBoolean() ? 1 : 0);
            return saved.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number
                && saved.TryGetDecimal(out var sn) && actual.TryGetDecimal(out var an)
                && decimal.Truncate(sn) == sn && decimal.Truncate(an) == an && sn == an;
        }
        return column.DataType is "varchar" or "char" or "text" or "tinytext" or "mediumtext" or "longtext"
            && saved.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.String
            && saved.GetString() == actual.GetString();
    }


    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void RequirePrivateLane()
    {
        var root = Environment.GetEnvironmentVariable("E2E_ROOT");
        Require(!string.IsNullOrWhiteSpace(root) && Path.GetFullPath(root) != "/root/aaemu-e2e",
            "An explicitly owned isolated E2E_ROOT is required; never adopt the shared lane");
        Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPOSE_PROJECT_NAME")),
            "An explicit owned compose project is required");
        foreach (var name in new[] { "E2E_INTERNAL_PORT", "E2E_LOGIN_PORT", "E2E_GAME_PORT", "E2E_STREAM_PORT",
                     "E2E_WEBAPI_PORT", "E2E_BRIDGE_PORT", "E2E_DB_PORT" })
            Require(int.TryParse(Environment.GetEnvironmentVariable(name), out var port) && port > 0,
                $"Explicit owned lane port {name} is required");
    }

    internal static void AdoptLaneDbPassword()
    {
        var envFile = Path.Combine(E2eStack.E2eRoot, ".env");
        foreach (var line in File.ReadLines(envFile))
            if (line.StartsWith("DB_PASSWORD=", StringComparison.Ordinal))
            {
                var password = line["DB_PASSWORD=".Length..].Trim();
                Require(password.Length > 0, "Owned lane DB credential is empty");
                typeof(E2eStack).GetProperty("DbPassword")!.SetValue(null, password);
                return;
            }
        throw new InvalidOperationException("Owned lane DB credential is missing");
    }

    internal static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    internal static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static Microsoft.Extensions.Configuration.IConfigurationRoot ReadEffectiveConfiguration()
    {
        // Match Program.BuildConfiguration's actual file order/environment.
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(E2eStack.RuntimeGameDir, "Config.json"), optional: true);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(E2eStack.RuntimeGameDir, "Configurations"),
                     "*.json", SearchOption.AllDirectories).Order())
            builder.AddJsonFile(path, optional: true);
        builder.AddJsonFile(Path.Combine(E2eStack.RuntimeGameDir, "Config.Local.json"), optional: true)
            .AddUserSecrets(typeof(AAEmu.Game.Core.Managers.Bots.ActorAuditRecord).Assembly, optional: true)
            .AddEnvironmentVariables();
        return builder.Build();
    }

    private static (double ExpRate, double AutoSaveMinutes) ReadWorldSettings()
    {
        var root = ReadEffectiveConfiguration();
        try
        {
            return (root.GetValue<double?>("World:ExpRate")
                    ?? throw new InvalidOperationException("Effective World.ExpRate is unobserved"),
                root.GetValue<double?>("World:AutoSaveInterval")
                    ?? throw new InvalidOperationException("Effective World.AutoSaveInterval is unobserved"));
        }
        finally { (root as IDisposable)?.Dispose(); }
    }





    internal static Persistence VerifyDurable(DurableSnapshot? before, DurableSnapshot? after,
        IReadOnlyList<Reward> rewards, HashSet<uint> provedCompletions)
    {
        if (before == null || after == null || before.CharacterId != after.CharacterId)
            return new(false, "UNKNOWN", "Owned before/after DB identity is unavailable");
        if (provedCompletions.Count == 0 || !provedCompletions.IsSubsetOf(after.CompletedQuests)
            || provedCompletions.Any(before.CompletedQuests.Contains))
            return new(false, "NOT_PROVED", "Fresh live completions are absent/already historical in durable bits");
        var newBits = after.CompletedQuests.Except(before.CompletedQuests).ToHashSet();
        if (!newBits.SetEquals(provedCompletions))
            return new(false, "UNKNOWN", "Unobserved durable completions prevent exact reward attribution");
        if (rewards.Any(r => !r.Supported) || !rewards.Select(r => r.QuestId).ToHashSet().SetEquals(provedCompletions)
            || rewards.Count != provedCompletions.Count)
            return new(false, "UNKNOWN", "Canonical rewards are missing/ambiguous/outside declared semantics");
        if (after.Experience - before.Experience != rewards.Sum(r => r.Experience)
            || after.Money - before.Money != rewards.Sum(r => r.Money))
            return new(false, "NOT_PROVED", "XP/money are not conserved against proved canonical rewards");
        var expected = new Dictionary<uint, long>(before.Items);
        foreach (var reward in rewards)
            foreach (var item in reward.Items) expected[item.Key] = expected.GetValueOrDefault(item.Key) + item.Value;
        if (expected.Count != after.Items.Count || expected.Any(item => after.Items.GetValueOrDefault(item.Key, -1) != item.Value))
            return new(false, "NOT_PROVED", "Starter/item quantities are not conserved against proved canonical rewards");
        return new(true, "PROVEN", null);
    }
}
