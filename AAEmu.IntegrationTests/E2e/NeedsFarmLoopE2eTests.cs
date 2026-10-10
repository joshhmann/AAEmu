using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

using static AAEmu.IntegrationTests.E2e.BotShortRunEvidence;
using static AAEmu.IntegrationTests.E2e.BotShortRunTests;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Seeded public cultivation/repeat, adopt-only: one enrollment wake, then
/// passive observation and one own approach interruption. Two actual harvests
/// are required. The older negative externally-woken diagnostic is separate.
/// </summary>
[Collection("e2e")]
public class NeedsFarmLoopE2eTests
{

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task NeedsFarmLoop_SeededPublicCultivation_TwoHarvests_OnLiveServer()
    {
        if (Environment.GetEnvironmentVariable("BOT_AUTONOMOUS_FARM") != "1")
        {
            Assert.Skip("BOT_AUTONOMOUS_FARM=1 and an already-ready owned lane are required.");
            return;
        }
        RequirePrivateLane();
        AdoptLaneDbPassword();
        var runId = Environment.GetEnvironmentVariable("AAEMU_AUTONOMOUS_FARM_RUN_ID")
            ?? "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        if (!System.Text.RegularExpressions.Regex.IsMatch(runId, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$"))
            throw new InvalidOperationException("Farm run ID must be a fresh leaf name, not a path");
        var directory = Path.Combine(E2eStack.RepoRoot, ".omp/state/artifacts/autonomous-farm-2026-10-09", runId);
        var reportPath = Path.Combine(directory, "report.json");
        var identityPath = Path.Combine(directory, "own-provision.json");
        var runnerFaultPath = Path.Combine(E2eStack.E2eRoot, "runner-cap-fault.json");
        if (File.Exists(reportPath) || File.Exists(identityPath) || File.Exists(Path.Combine(directory, "observations.jsonl"))
            || File.Exists(Path.Combine(directory, "actor-audit.jsonl")))
            throw new InvalidOperationException("Farm run evidence already exists; never overwrite another invocation");
        Directory.CreateDirectory(directory);
        using var observations = new StreamWriter(Path.Combine(directory, "observations.jsonl"));
        using var audits = new StreamWriter(Path.Combine(directory, "actor-audit.jsonl"));
        var previousAudit = new HashSet<Guid>();
        var botName = "Af" + Guid.NewGuid().ToString("N")[..12];
        uint characterId = 0;
        BotDriveClient? bridge = null;
        HttpClient? control = null;
        NeedsFarmLoopEvidence? proof = null;
        FarmDurable? body = null, baseline = null, afterRelease = null, afterFlush = null;
        JsonElement? unseededBodySnapshot = null, beforeEnrollment = null, startSnapshot = null, endSnapshot = null;
        object? canonical = null, provenance = null;
        var failures = new List<object>();
        var fixture = Stopwatch.StartNew();
        var watch = new Stopwatch();
        var phase = "FIXTURE_BASELINE";
        string? endReason = null;
        DateTime? startUtc = null, endUtc = null;
        double persistenceSeconds = 0;
        DateTime? persistenceWaitStartedAtUtc = null;
        double persistenceWaitSeconds = 0;
        var enrollmentWakes = 0;
        var interruptRequests = 0;
        var registryRemoved = false;
        var sessionReleased = false;
        var registryAbsent = false;
        var durableProved = false;
        var finalDatabaseAbsences = new List<object>();
        string? canonicalMd5 = null, runtimeMd5 = null;
        var evidenceBudget = new FarmEvidenceBudget(MaximumHarnessEvidenceBytes, ReservedEvidenceBytes);
        var evidenceCapFailed = false;
        var runnerCapFaultDetected = false;
        var provisionRequested = false;
        var suppressedCleanupRecords = 0;

        void CheckRunnerFault()
        {
            if (phase is not ("FIXTURE_BASELINE" or "OBSERVE") || !File.Exists(runnerFaultPath)) return;
            runnerCapFaultDetected = true;
            endReason = "RUNNER_CAP_FAULT_FAILED_END";
            throw new InvalidDataException("RUNNER_CAP_FAULT: no further live evidence may be promoted");
        }
        void OwnIdentity(string state)
        {
            var record = JsonSerializer.Serialize(new { state, recordedAtUtc = DateTime.UtcNow,
                e2eRoot = Path.GetFullPath(E2eStack.E2eRoot), runId, freshProvision = true, name = botName, characterId,
                role = "exact own headless provision; characterId=0 is intent only, never cleanup authority", reportPath });
            evidenceBudget.ReserveFinal(record);
            var temporary = identityPath + ".tmp";
            File.WriteAllText(temporary, record, new UTF8Encoding(false));
            File.Move(temporary, identityPath, overwrite: true);
        }

        void Require(bool condition, string predicate)
        {
            if (!condition) throw new InvalidOperationException(predicate);
        }
        void Fault(Exception ex) => failures.Add(new { phase, atUtc = DateTime.UtcNow, type = ex.GetType().Name, firstFailedPredicate = ex.Message });
        CancellationTokenSource Deadline()
        {
            CheckRunnerFault();
            var seconds = watch.IsRunning ? proof?.RemainingSeconds(watch.Elapsed.TotalSeconds) ?? 0
                : phase == "FIXTURE_BASELINE" ? 360 - fixture.Elapsed.TotalSeconds : 5;
            if (seconds <= 0) throw new TimeoutException("Declared phase deadline reached");
            return new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(5, seconds)));
        }
        async Task WaitOrdinaryPersistence()
        {
            // Called only AFTER the first afterRelease DB attempt/check (or
            // unbound-identity fallback), never measured from frozen END.
            persistenceWaitStartedAtUtc = DateTime.UtcNow;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var remaining = persistenceSeconds - clock.Elapsed.TotalSeconds;
                if (remaining <= 0) break;
                await Task.Delay(TimeSpan.FromSeconds(remaining));
            }
            persistenceWaitSeconds = clock.Elapsed.TotalSeconds;
        }
        void Raw(object request, object output)
        {
            if (evidenceCapFailed) { suppressedCleanupRecords++; return; }
            void Write(StreamWriter writer, string line)
            {
                if (Encoding.UTF8.GetByteCount(line) > MaximumResponseBytes + 4096)
                    throw new InvalidDataException("HARNESS_RECORD_BYTE_BOUND");
                evidenceBudget.ReserveRaw(line);
                writer.WriteLine(line);
            }
            try
            {
                Write(observations, JsonSerializer.Serialize(new { phase, recordedAtUtc = DateTime.UtcNow, request, output }));
                observations.Flush();
                if (output is not JsonElement json || !json.TryGetProperty("actorAudit", out var rows) || rows.ValueKind != JsonValueKind.Array) return;
                var current = new HashSet<Guid>();
                foreach (var row in rows.EnumerateArray())
                {
                    var id = Id(row, "trace_id");
                    if (id.HasValue && current.Add(id.Value) && !previousAudit.Contains(id.Value))
                        Write(audits, JsonSerializer.Serialize(new { observedAtUtc = Time(json, "observedAtUtc"), audit = row }));
                }
                audits.Flush();
                previousAudit = current;
            }
            catch (InvalidDataException ex)
            {
                evidenceCapFailed = true;
                endReason = "HARNESS_EVIDENCE_CAP_FAILED_END";
                // Never let logging exhaustion disable ordinary owned cleanup.
                if (phase is "FIXTURE_BASELINE" or "OBSERVE") throw;
                suppressedCleanupRecords++;
                Fault(ex);
            }
        }
        async Task<JsonElement> Bridge(object request)
        {
            using var deadline = Deadline();
            var result = await bridge!.CallAsync(JsonSerializer.Serialize(request), 5000, deadline.Token);
            Raw(request, result);
            return result;
        }
        async Task<JsonElement> Api(HttpMethod method, string path, object? bodyValue = null)
        {
            using var deadline = Deadline();
            using var request = new HttpRequestMessage(method, path);
            if (bodyValue != null) request.Content = new StringContent(JsonSerializer.Serialize(bodyValue), Encoding.UTF8, "application/json");
            using var response = await control!.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Raw(new { method = method.Method, path, body = bodyValue, authentication = "X-Auth-Token present; value omitted" }, result);
            return result;
        }
        async Task<FarmDurable> Durable()
        {
            using var deadline = Deadline();
            var result = await ReadFarmDurable(characterId, deadline.Token);
            Raw(new { query = "owned character/account/items/persistent crops", characterId }, result);
            return result;
        }
        async Task ObserveCropDatabase()
        {
            foreach (var life in proof!.Lifetimes)
            {
                if (life.DatabaseCreationObserved && (life.Harvest == null || life.DatabaseDeletionObserved)) continue;
                using var deadline = Deadline();
                var row = await ReadFarmCrop(life.Creation.DbId, deadline.Token);
                Raw(new { query = "crop lifetime", life.PlantTraceId, life.Creation.DbId }, row is null ? new { absent = true } : (object)row);
                if (life.Harvest != null) proof.RecordDatabaseDeletion(life.PlantTraceId, row == null);
                else
                {
                    Require(row != null, "DATABASE_CROP_CREATION_MISSING");
                    proof.RecordDatabaseCreation(row!.DbId, row.OwnerId, row.Template, row.PlantTime, row.HouseId, row.FarmType);
                }
            }
        }
        try
        {
            Require(Environment.GetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED") == "1"
                && Environment.GetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED") == "0"
                && Environment.GetEnvironmentVariable("AAEMU_PRESENCE_IDLE_WAKE") == "1",
                "DECLARED_GATES: farm/idle-wake on, quest bootstrap off before boot");
            var token = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN");
            Require(!string.IsNullOrWhiteSpace(token), "Private bot-control token is missing");
            control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2eStack.WebApiPort}"),
                Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaximumResponseBytes };
            control.DefaultRequestHeaders.Add("X-Auth-Token", token);
            var readiness = await Api(HttpMethod.Get, "/api/bots/status");
            Require(Bool(readiness, "Success") == true && readiness.GetProperty("Bots").ValueKind == JsonValueKind.Array,
                "Already-ready authenticated registry required; harness never boots a lane");
            using (var deadline = Deadline())
                bridge = await Task.Run(() => new BotDriveClient(E2eStack.BridgePort, MaximumResponseBytes), deadline.Token).WaitAsync(deadline.Token);
            Require(Bool(await Bridge(new { cmd = "ping" }), "pong") == true, "Already-ready bridge required");
            var canonicalPath = Path.Combine(E2eStack.RepoRoot, "AAEmu.Game/Data/compact.sqlite3");
            canonicalMd5 = Md5(canonicalPath);
            runtimeMd5 = Md5(E2eStack.RuntimeSqlite);
            Require(canonicalMd5 == runtimeMd5, "Canonical/runtime crop data differ");
            using (var deadline = Deadline()) canonical = await ReadFarmCanonical(canonicalPath, deadline.Token);
            var effective = ReadEffectiveConfiguration();
            try
            {
                var autoSaveMinutes = effective.GetValue<double?>("World:AutoSaveInterval") ?? throw new InvalidOperationException("Effective autosave is unobserved");
                persistenceSeconds = autoSaveMinutes * 60 + 25;
                Require(double.IsFinite(persistenceSeconds) && Math.Abs(autoSaveMinutes - 5) < 0.001
                    && Math.Abs(persistenceSeconds - 325) < 0.001,
                    "Declared ordinary autosave5min plus25s margin must be325s");
            }
            finally { (effective as IDisposable)?.Dispose(); }
            provenance = new { source = E2eStack.MeasuredSourceIdentity, E2eStack.PublishSourceIdentity,
                testAssemblySha256 = Sha256(typeof(NeedsFarmLoopE2eTests).Assembly.Location),
                runtimeGameSha256 = Sha256(Path.Combine(E2eStack.RuntimeGameDir, "AAEmu.Game.dll")),
                canonicalPath, canonicalMd5, runtimeMd5,
                scope = "Dirty source identities and DLL hashes; runner publish receipt joins source to runtime" };
            using (var deadline = Deadline()) await RequireFreshFarmName(botName, deadline.Token);
            provisionRequested = true;
            OwnIdentity("ProvisionRequestedAfterVerifiedFreshName");
            var provisionRequest = new { cmd = "provision", bot = botName, fresh = true, level = 1 };
            JsonElement provision;
            using (var deadline = Deadline())
                provision = await bridge.CallAsync(JsonSerializer.Serialize(provisionRequest), 5000, deadline.Token);
            var returnedId = UInt(provision, "id");
            Require(returnedId != 0 && Long(provision, "level") == 1 && String(provision, "name") == botName,
                "FRESH_PROVISION_IDENTITY_BINDING_FAULT: do not clean an absent or mismatched returned identity");
            characterId = returnedId;
            // Bind the returned identity before any log/cap failure can obscure
            // ownership. The runner can clean this exact bot if finally is lost.
            OwnIdentity("ProvisionReturned");
            Raw(provisionRequest, provision);
            // Passive cached body/default-account witness BEFORE labor or seed
            // staging. This does not enroll, activate, wake or create an actor.
            unseededBodySnapshot = await Bridge(new { cmd = "farm", op = "needs", op2 = "observe", bot = botName,
                objIds = System.Array.Empty<uint>() });
            body = await Durable();
            Require(body.CharacterId == UInt(unseededBodySnapshot.Value, "id")
                && body.AccountId == UInt(unseededBodySnapshot.Value, "accountId")
                && body.Labor == Long(unseededBodySnapshot.Value, "labor")
                && Bool(unseededBodySnapshot.Value, "enrollmentRegistered") == false
                && Bool(unseededBodySnapshot.Value, "actorAvailable") == false,
                "PRE_RIG_ORDINARY_ACCOUNT_DEFAULT_CACHE_DB_AGREEMENT");
            await Bridge(new { cmd = "farm", op = "rig", bot = botName, seeds = 3, calves = 0, labor = 100 });
            // Disclosed fixture staging, never repeated after enrollment/START.
            var placed = await Bridge(new { cmd = "farm", op = "place", bot = botName, x = 12440, y = 15390, z = 160 });
            var ground = Number(placed, "groundZ");
            if (ground is > 0 && Math.Abs(Number(placed, "z")!.Value - ground.Value) > 0.01)
                await Bridge(new { cmd = "farm", op = "place", bot = botName, x = 12440, y = 15390, z = ground.Value });
            var soil = await Bridge(new { cmd = "farm", op = "soil", bot = botName, radius = 150, step = 5 });
            Require(Bool(soil, "onSoil") == false && Bool(soil, "found") == true && Number(soil, "dist") is > 4,
                "Fixture must be off soil with a reachable public farm; do not repair it during observation");
            beforeEnrollment = await Bridge(new { cmd = "farm", op = "needs", op2 = "observe", bot = botName, objIds = System.Array.Empty<uint>() });
            Require(Bool(beforeEnrollment.Value, "enrollmentRegistered") == false && Bool(beforeEnrollment.Value, "stateAvailable") == false
                && Bool(beforeEnrollment.Value, "actorAvailable") == false && Long(beforeEnrollment.Value, "level") == 1
                && Long(beforeEnrollment.Value, "money") == 0 && Long(beforeEnrollment.Value, "labor") == 100
                && Number(beforeEnrollment.Value, "dropRateMul") == 0 && Number(beforeEnrollment.Value, "growthRate") == 10
                && Number(beforeEnrollment.Value, "lootRate") == 1 && Bool(beforeEnrollment.Value, "laborConsumptionDisabled") == false
                && Number(beforeEnrollment.Value, "autoSaveMinutes") is { } serverAutoSave
                && Math.Abs(serverAutoSave * 60 + 25 - persistenceSeconds) < 0.001
                && String(beforeEnrollment.Value, "needsFarmEnvironment") == "1"
                && String(beforeEnrollment.Value, "questBootstrapEnvironment") == "0" && Bool(beforeEnrollment.Value, "homesteadEnabled") == false,
                "PRESTART_BODY_NEEDS_RATES_GATES_OR_PASSIVE_ABSENCE");
            // Wait for ordinary persistence, not cmd:save. Boot readiness was a
            // separate runner-owned 360s phase; this fixture has its own 360s.
            var persistenceDeadline = fixture.Elapsed.TotalSeconds + persistenceSeconds;
            do
            {
                baseline = await Durable();
                if (MatchesFarmSnapshot(baseline, beforeEnrollment.Value)) break;
                Require(fixture.Elapsed.TotalSeconds < Math.Min(360, persistenceDeadline), "PRESTART_ORDINARY_PERSISTENCE_TIMEOUT");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1, Math.Min(360, persistenceDeadline) - fixture.Elapsed.TotalSeconds)));
            } while (true);
            Require(baseline.Crops.Count == 0, "PRESTART_OWNED_CROP_BASELINE_NOT_EMPTY");
            RequireFarmSetupBody(body, baseline);
            // Keep both the BEFORE-enrollment baseline and AFTER-wake snapshot.
            // A Move started in this wake is disclosed as pre-START, never
            // relabelled. Any pre-START Plant/Harvest prevents acceptance.
            enrollmentWakes++;
            startSnapshot = await Bridge(new { cmd = "farm", op = "needs", op2 = "wake", bot = botName, waitMs = 1000 });
            startUtc = DateTime.UtcNow;
            fixture.Stop();
            proof = new NeedsFarmLoopEvidence(characterId, beforeEnrollment.Value, startUtc.Value);
            phase = "OBSERVE";
            watch.Start();
            CheckRunnerFault();
            proof.Observe(0, startSnapshot.Value);
            CheckRunnerFault();
            while ((endReason = proof.StopReason(watch.Elapsed.TotalSeconds)) == null)
            {
                using (var deadline = Deadline()) await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                var observation = await Bridge(new { cmd = "farm", op = "needs", op2 = "observe", bot = botName, objIds = proof.WatchedObjIds.ToArray() });
                CheckRunnerFault();
                proof.Observe(watch.Elapsed.TotalSeconds, observation);
                CheckRunnerFault();
                // Second applied gain is the stopping point: no SQL, third
                // choice, control read or extra observation before removal.
                if ((endReason = proof.StopReason(watch.Elapsed.TotalSeconds)) != null) break;
                await ObserveCropDatabase();
                if ((endReason = proof.StopReason(watch.Elapsed.TotalSeconds)) != null) break;
                if (proof.ControlTraceId.HasValue && !proof.ControlCompleted)
                    proof.ObserveControl(await Api(HttpMethod.Get, $"/api/actors/actions/{proof.ControlTraceId.Value:D}"));
                if ((endReason = proof.StopReason(watch.Elapsed.TotalSeconds)) != null) break;
                var candidate = proof.GetInterruptCandidate();
                if (candidate == null) continue;
                proof.DeclareInterrupt(candidate);
                interruptRequests++;
                Raw(new { stimulus = "one own active approach Move interruption", candidate }, new { declaredAtUtc = DateTime.UtcNow });
                var ack = await Api(HttpMethod.Post, "/api/actors/interrupt",
                    new { bot = characterId.ToString(CultureInfo.InvariantCulture), traceId = candidate.VictimTraceId.ToString("D") });
                Require(Bool(ack, "success") == true && Id(ack, "trace_id").HasValue, "INTERRUPT_ACK_REQUIRED");
                proof.AcknowledgeInterrupt(Id(ack, "trace_id")!.Value);
            }
            if (proof.EvidenceFault != null) throw new InvalidOperationException(proof.EvidenceFault);
        }
        catch (Exception ex)
        {
            endReason ??= proof?.StopReason(watch.Elapsed.TotalSeconds) ?? "INFRASTRUCTURE_OR_SETUP_FAILURE";
            Fault(ex);
        }
        finally
        {
            endUtc = DateTime.UtcNow;
            endSnapshot = proof?.LastObservation ?? startSnapshot;
            watch.Stop();
            // Freeze evidence first; no post-END observation can promote a gate.
            phase = "POST_END_OWNED_CLEANUP";
            if (characterId != 0)
            {
                try
                {
                    registryRemoved = Bool(await Api(HttpMethod.Post, "/api/bots/remove",
                        new { nameOrId = characterId.ToString(CultureInfo.InvariantCulture) }), "Success") == true;
                    Require(registryRemoved, "OWNED_REGISTRY_REMOVE_FAILED");
                }
                catch (Exception ex) { Fault(ex); }
                try
                {
                    bridge?.Dispose();
                    bridge = null;
                    using (var deadline = Deadline())
                        bridge = await Task.Run(() => new BotDriveClient(E2eStack.BridgePort, MaximumResponseBytes), deadline.Token).WaitAsync(deadline.Token);
                    var released = await Bridge(new { cmd = "deactivate", bot = botName });
                    sessionReleased = Bool(released, "removed") == true && UInt(released, "id") == characterId;
                    Require(sessionReleased, "OWNED_HEADLESS_RELEASE_FAILED");
                    var status = await Api(HttpMethod.Get, "/api/bots/status");
                    Require(Bool(status, "Success") == true && status.GetProperty("Bots").ValueKind == JsonValueKind.Array, "REGISTRY_ABSENCE_UNOBSERVABLE");
                    registryAbsent = !Array(status, "Bots").Any(r => UInt(r, "Id") == characterId);
                    Require(registryAbsent, "OWNED_REGISTRY_STILL_PRESENT");
                }
                catch (Exception ex) { Fault(ex); }
                var releaseProved = false;
                var flushProved = false;
                phase = "POST_END_AFTER_RELEASE";
                try
                {
                    if (characterId != 0) afterRelease = await Durable();
                    if (proof?.LiveAccepted == true && baseline != null && endSnapshot.HasValue && afterRelease != null)
                    {
                        RequireFarmDurableStage(baseline, afterRelease, endSnapshot.Value, proof);
                        releaseProved = true;
                    }
                }
                catch (Exception ex) { Fault(ex); }
                // A failed first read/check must not skip the separate ordinary
                // persistence margin or the second authoritative comparison.
                phase = "POST_END_ORDINARY_PERSISTENCE";
                await WaitOrdinaryPersistence();
                phase = "POST_END_AFTER_FLUSH";
                try
                {
                    if (characterId != 0) afterFlush = await Durable();
                    if (proof?.LiveAccepted == true && baseline != null && endSnapshot.HasValue && afterFlush != null)
                    {
                        RequireFarmDurableStage(baseline, afterFlush, endSnapshot.Value, proof);
                        flushProved = true;
                        foreach (var life in proof.Lifetimes)
                        {
                            using var deadline = Deadline();
                            var remaining = await ReadFarmCrop(life.Creation.DbId, deadline.Token);
                            var receipt = new { life.PlantTraceId, life.Creation.DbId, readAtUtc = DateTime.UtcNow, absent = remaining == null };
                            finalDatabaseAbsences.Add(receipt);
                            Raw(new { query = "post-END crop DB absence", life.PlantTraceId, life.Creation.DbId }, receipt);
                            Require(remaining == null, "DURABLE_LIFETIME_ID_STILL_PRESENT");
                        }
                        Require(Md5(Path.Combine(E2eStack.RepoRoot, "AAEmu.Game/Data/compact.sqlite3")) == canonicalMd5
                            && Md5(E2eStack.RuntimeSqlite) == runtimeMd5, "CANONICAL_RUNTIME_IDENTITY_CHANGED");
                        durableProved = releaseProved && flushProved;
                    }
                }
                catch (Exception ex) { Fault(ex); }
            }
            else if (provisionRequested)
            {
                Fault(new InvalidOperationException("OWNED_PROVISION_IDENTITY_UNBOUND: intent alone authorizes no bot cleanup"));
                phase = "POST_END_UNBOUND_ORDINARY_PERSISTENCE";
                await WaitOrdinaryPersistence();
            }
            bridge?.Dispose();
            control?.Dispose();
        }
        // A supervisor fault published during cleanup still forbids final
        // promotion, but must never interrupt cleanup or its persistence wait.
        if (File.Exists(runnerFaultPath) && !runnerCapFaultDetected)
        {
            runnerCapFaultDetected = true;
            phase = "FINAL_EVIDENCE";
            endReason = "RUNNER_CAP_FAULT_FAILED_END";
            Fault(new InvalidDataException("RUNNER_CAP_FAULT: published during post-END cleanup/persistence"));
        }
        var passed = proof?.LiveAccepted == true && durableProved && registryRemoved && sessionReleased && registryAbsent
            && enrollmentWakes == 1 && interruptRequests == 1 && failures.Count == 0;
        var report = JsonSerializer.Serialize(new
        {
            mission = "seeded public cultivation/repeat; parent autonomous cultivation capability, not general Stage 2",
            passed, implementation = "fresh live invocation required; supplied-observation tests are not gameplay proof",
            botName, characterId, phase, startUtc, endUtc, endReason, autonomousSeconds = watch.Elapsed.TotalSeconds,
            deadlines = new { runnerBootReadinessSeconds = 360, fixtureBaselineSeconds = 360, observationSeconds = 900,
                noAuthoritativeProgressSeconds = 300, perCallSeconds = 5, ordinaryPersistenceSeconds = persistenceSeconds },
            persistenceWaitStartedAtUtc, persistenceWaitSeconds,
            bounds = new { maximumResponseBytes = MaximumResponseBytes, maximumHarnessEvidenceBytes = MaximumHarnessEvidenceBytes,
                reservedEvidenceBytes = ReservedEvidenceBytes, evidenceBudget.RawBytes, evidenceBudget.ReservedBytes,
                evidenceCapFailed, runnerCapFaultDetected, suppressedCleanupRecords,
                runnerContract = "supervisor64MiB pipes + runner8MiB metadata; combined live bundle256MiB, no copied unbounded NLog sidefiles" },
            setup = new { level = 1, seeds = 3, labor = 100, calves = 0, growthRate = 10, lootRate = 1,
                questBootstrap = "explicitly disabled", homestead = "actual module Enabled=false",
                placement = "pre-enrollment Lilyut public-farm vicinity; ground correction only before START", enrollmentWakes, interruptRequests },
            canonical, provenance, body, unseededBodySnapshot, baseline, beforeEnrollment, startSnapshot, endSnapshot,
            lifetimes = proof?.Lifetimes, recoveryProved = proof?.RecoveryProved,
            controlTraceId = proof?.ControlTraceId, controlCompleted = proof?.ControlCompleted, evidenceFault = proof?.EvidenceFault,
            controlRequestedAtUtc = proof?.ControlRequestedAtUtc, controlCompletedAtUtc = proof?.ControlCompletedAtUtc,
            victimInterruptedAtUtc = proof?.VictimInterruptedAtUtc, identityPath, runnerFaultPath,
            afterRelease, afterFlush, finalDatabaseAbsences, durableProved, registryRemoved, sessionReleased, registryAbsent, failures,
            rangeScope = "3D/horizontal immature and mature ranges are actual paired observations. Harvest range uses post-harvest actor and last observed crop position, not an atomic action-time sample. Canonical skill13980 max_range=4 is reported, not a new actor rule; enforcement/cast/client parity unproved.",
            stoppingPoint = "Freeze and remove own bot immediately at second observed applied harvest gain; no third choice/crop or post-END effect promotion",
            nonGoals = "earned seed acquisition, private homestead, quest-to-farm strategic handoff, cross-domain goal selection, H, restart and general Stage 2"
        }, new JsonSerializerOptions { WriteIndented = true });
        try { evidenceBudget.ReserveFinal(report); }
        catch (InvalidDataException ex)
        {
            passed = false;
            evidenceCapFailed = true;
            endReason = "FINAL_REPORT_CAP_FAILED_END";
            Fault(ex);
            report = JsonSerializer.Serialize(new { mission = "seeded public cultivation/repeat", passed = false,
                botName, characterId, startUtc, endUtc, endReason, registryRemoved, sessionReleased, registryAbsent,
                evidenceCapFailed, runnerCapFaultDetected, firstFailedPredicate = ex.Message,
                details = "Raw bounded evidence retained; oversized final projection cannot promote acceptance", failures });
            evidenceBudget.ReserveFinal(report);
        }
        await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(false));
        Console.WriteLine("AUTONOMOUS-FARM-REPORT " + reportPath);
        Assert.True(passed, $"Bounded farming acceptance failed ({endReason}); {reportPath}");
    }

    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const long MaximumHarnessEvidenceBytes = 184L * 1024 * 1024, ReservedEvidenceBytes = 4L * 1024 * 1024;
    internal sealed record FarmItem(ulong Id, uint Template, int SlotType, int Slot, long Count, string? Details, int Grade, uint Flags, string Metadata);
    internal sealed record FarmCrop(uint DbId, uint OwnerId, uint Template, uint Phase, DateTime PlantTime, uint HouseId, int FarmType);
    internal sealed record FarmDurable(uint CharacterId, uint AccountId, long Labor, long Level, long Experience, long Money,
        double X, double Y, double Z, uint WorldId, uint ZoneId, List<FarmItem> Items, List<FarmCrop> Crops,
        NeedsFarmLoopEvidence.Actability[] Actabilities, string[] Quests, string[] CompletedQuests, string[] Houses);

    // One farm-run budget shared by both logs and reserved final/identity files.
    // Reservation occurs before writes. Cleanup may continue after exhaustion.
    internal sealed class FarmEvidenceBudget(long maximumBytes, long reservedBytes)
    {
        internal long RawBytes { get; private set; }
        internal long ReservedBytes { get; private set; }
        internal void ReserveRaw(string line)
        {
            var bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (bytes > maximumBytes - reservedBytes - RawBytes)
                throw new InvalidDataException("HARNESS_EVIDENCE_CAP: raw byte reservation exhausted");
            RawBytes += bytes;
        }
        internal void ReserveFinal(string content)
        {
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (bytes > reservedBytes - ReservedBytes)
                throw new InvalidDataException("HARNESS_EVIDENCE_CAP: final/identity reservation exhausted");
            ReservedBytes += bytes;
        }
    }

    private static MySqlConnection FarmConnection() => new(new MySqlConnectionStringBuilder
    {
        Server = E2eStack.DbHost, Port = (uint)E2eStack.DbPort, UserID = "root",
        Password = E2eStack.DbPassword, Database = "aaemu_game", ConnectionTimeout = 5,
    }.ConnectionString);

    internal static async Task RequireFreshFarmName(string name, CancellationToken token)
    {
        using var connection = FarmConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT (SELECT COUNT(*) FROM characters WHERE name=@name)+(SELECT COUNT(*) FROM aaemu_login.users WHERE username=@account)";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@account", AAEmu.Game.Core.Managers.BotAccountProvisioningService.ManagedUsernamePrefix + name.ToLowerInvariant());
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("OWNERSHIP: proposed fresh character/account already exists; never adopt or wipe it");
    }

    private static FarmCrop CropRow(System.Data.Common.DbDataReader reader) => new(
        Convert.ToUInt32(reader.GetValue(0)), Convert.ToUInt32(reader.GetValue(1)), Convert.ToUInt32(reader.GetValue(2)),
        Convert.ToUInt32(reader.GetValue(3)), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
        Convert.ToUInt32(reader.GetValue(5)), Convert.ToInt32(reader.GetValue(6)));

    private static async Task<FarmCrop?> ReadFarmCrop(uint dbId, CancellationToken token)
    {
        using var connection = FarmConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT id,owner_id,template_id,current_phase_id,plant_time,house_id,farm_type FROM doodads WHERE id=@id";
        command.Parameters.AddWithValue("@id", dbId);
        using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? CropRow(reader) : null;
    }

    internal static async Task<FarmDurable> ReadFarmDurable(uint characterId, CancellationToken token)
    {
        using var connection = FarmConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT c.account_id,a.labor,c.level,c.experience,c.money,CAST(c.x AS DOUBLE) AS x,CAST(c.y AS DOUBLE) AS y,CAST(c.z AS DOUBLE) AS z,c.world_id,c.zone_id FROM characters c JOIN accounts a ON a.account_id=c.account_id WHERE c.id=@id;"
            + "SELECT * FROM items WHERE owner=@id ORDER BY id;"
            + "SELECT id,owner_id,template_id,current_phase_id,plant_time,house_id,farm_type FROM doodads WHERE owner_id=@id ORDER BY id;"
            + "SELECT id,point,step FROM actabilities WHERE owner=@id ORDER BY id;"
            + "SELECT id,template_id,data,status,owner FROM quests WHERE owner=@id ORDER BY id;"
            + "SELECT id,data,owner FROM completed_quests WHERE owner=@id ORDER BY id;"
            + "SELECT * FROM housings WHERE owner=@id ORDER BY id";
        command.Parameters.AddWithValue("@id", characterId);
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("OWNED_DURABLE_CHARACTER_MISSING");
        var account = Convert.ToUInt32(reader.GetValue(0));
        var labor = Convert.ToInt64(reader.GetValue(1));
        var level = Convert.ToInt64(reader.GetValue(2));
        var experience = Convert.ToInt64(reader.GetValue(3));
        var money = Convert.ToInt64(reader.GetValue(4));
        var x = reader.GetDouble(5); var y = reader.GetDouble(6); var z = reader.GetDouble(7);
        var world = Convert.ToUInt32(reader.GetValue(8)); var zone = Convert.ToUInt32(reader.GetValue(9));
        var capturedBytes = 0;
        void AccountRow(string serialized)
        {
            capturedBytes = checked(capturedBytes + Encoding.UTF8.GetByteCount(serialized));
            if (capturedBytes > MaximumResponseBytes) throw new InvalidDataException("DURABLE_RESPONSE_BYTE_BOUND");
        }
        async Task<string[]> ReadOwnedRows(string table)
        {
            if (!await reader.NextResultAsync(token)) throw new InvalidOperationException("DURABLE_" + table + "_UNOBSERVABLE");
            var rows = new List<string>();
            while (await reader.ReadAsync(token))
            {
                if (rows.Count >= 256) throw new InvalidDataException("DURABLE_OWNED_ROW_BOUND: " + table);
                var row = FarmDatabaseRow(reader);
                AccountRow(row);
                rows.Add(row);
            }
            return rows.ToArray();
        }
        if (!await reader.NextResultAsync(token)) throw new InvalidOperationException("DURABLE_ITEMS_UNOBSERVABLE");
        var items = new List<FarmItem>();
        var itemId = reader.GetOrdinal("id"); var template = reader.GetOrdinal("template_id");
        var slotType = reader.GetOrdinal("slot_type"); var slot = reader.GetOrdinal("slot"); var count = reader.GetOrdinal("count");
        var details = reader.GetOrdinal("details"); var grade = reader.GetOrdinal("grade"); var flags = reader.GetOrdinal("flags");
        while (await reader.ReadAsync(token))
        {
            if (items.Count >= 256) throw new InvalidDataException("DURABLE_OWNED_ROW_BOUND: items");
            var metadata = FarmDatabaseRow(reader);
            AccountRow(metadata);
            items.Add(new FarmItem(Convert.ToUInt64(reader.GetValue(itemId)), Convert.ToUInt32(reader.GetValue(template)),
                Convert.ToInt32(reader.GetValue(slotType)), Convert.ToInt32(reader.GetValue(slot)), Convert.ToInt64(reader.GetValue(count)),
                reader.IsDBNull(details) ? null : Convert.ToBase64String((byte[])reader.GetValue(details)),
                Convert.ToInt32(reader.GetValue(grade)), Convert.ToUInt32(reader.GetValue(flags)), metadata));
        }
        if (!await reader.NextResultAsync(token)) throw new InvalidOperationException("DURABLE_CROPS_UNOBSERVABLE");
        var crops = new List<FarmCrop>();
        while (await reader.ReadAsync(token))
        {
            if (crops.Count >= 16) throw new InvalidDataException("DURABLE_OWNED_ROW_BOUND: crops");
            crops.Add(CropRow(reader));
        }
        if (!await reader.NextResultAsync(token)) throw new InvalidOperationException("DURABLE_ACTABILITIES_UNOBSERVABLE");
        var abilities = new List<NeedsFarmLoopEvidence.Actability>();
        while (await reader.ReadAsync(token))
        {
            if (abilities.Count >= 256) throw new InvalidDataException("DURABLE_OWNED_ROW_BOUND: actabilities");
            abilities.Add(new(Convert.ToUInt32(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2))));
        }
        var quests = await ReadOwnedRows("QUESTS");
        var completedQuests = await ReadOwnedRows("COMPLETED_QUESTS");
        var houses = await ReadOwnedRows("HOUSES");
        return new(characterId, account, labor, level, experience, money, x, y, z, world, zone,
            items, crops, abilities.ToArray(), quests, completedQuests, houses);
    }

    private static string FarmDatabaseRow(System.Data.Common.DbDataReader reader)
    {
        var fields = new Dictionary<string, object?>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!reader.IsDBNull(i) && reader.GetFieldType(i) == typeof(byte[]) && reader.GetBytes(i, 0, null, 0, 0) > 65536)
                throw new InvalidDataException("DURABLE_BLOB_BYTE_BOUND");
            fields.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
        }
        return JsonSerializer.Serialize(fields);
    }

    internal static bool MatchesFarmSnapshot(FarmDurable durable, JsonElement snapshot)
    {
        if (durable.CharacterId != UInt(snapshot, "id") || durable.AccountId != UInt(snapshot, "accountId")
            || durable.Labor != Long(snapshot, "labor") || durable.Money != Long(snapshot, "money")
            || durable.Level != Long(snapshot, "level") || durable.Experience != Long(snapshot, "experience")
            || durable.WorldId != UInt(snapshot, "worldId") || durable.ZoneId != UInt(snapshot, "zoneId")
            || Number(snapshot, "x") is not { } x || Number(snapshot, "y") is not { } y || Number(snapshot, "z") is not { } z
            || Math.Abs(durable.X - x) > 0.01 || Math.Abs(durable.Y - y) > 0.01 || Math.Abs(durable.Z - z) > 0.01
            || !snapshot.TryGetProperty("actabilities", out var abilities) || abilities.ValueKind != JsonValueKind.Array) return false;
        var actual = abilities.EnumerateArray()
            .Select(a => new NeedsFarmLoopEvidence.Actability(UInt(a, "id"), Long(a, "point"), Long(a, "step"))).OrderBy(a => a.Id);
        if (!durable.Actabilities.SequenceEqual(actual) || !snapshot.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return false;
        var watched = new HashSet<uint>();
        foreach (var row in items.EnumerateArray())
        {
            var template = UInt(row, "template");
            if (template is not (NeedsFarmLoopEvidence.Seed or NeedsFarmLoopEvidence.Potato or NeedsFarmLoopEvidence.Auxiliary)
                || !watched.Add(template) || Long(row, "count") < 0
                || durable.Items.Where(i => i.Template == template).Sum(i => i.Count) != Long(row, "count")) return false;
        }
        return watched.Count == 3;
    }

    internal static void RequireFarmConservation(FarmDurable before, FarmDurable after, NeedsFarmLoopEvidence proof)
    {
        var allowed = new HashSet<uint> { NeedsFarmLoopEvidence.Seed, NeedsFarmLoopEvidence.Potato, NeedsFarmLoopEvidence.Auxiliary };
        if (!before.Items.Where(i => !allowed.Contains(i.Template)).SequenceEqual(after.Items.Where(i => !allowed.Contains(i.Template))))
            throw new InvalidOperationException("UNRELATED_BODY_OR_INVENTORY_CHANGED");
        if (!before.Quests.SequenceEqual(after.Quests) || !before.CompletedQuests.SequenceEqual(after.CompletedQuests))
            throw new InvalidOperationException("OWNED_QUEST_STATE_CHANGED");
        if (!before.Houses.SequenceEqual(after.Houses)) throw new InvalidOperationException("OWNED_HOUSE_STATE_CHANGED");
        long Count(FarmDurable state, uint item) => state.Items.Where(i => i.Template == item).Sum(i => i.Count);
        var harvests = proof.Lifetimes.Select(l => l.Harvest!).ToArray();
        if (after.Money != before.Money || after.Labor != before.Labor - 2
            || Count(after, NeedsFarmLoopEvidence.Seed) != Count(before, NeedsFarmLoopEvidence.Seed) - 2 + harvests.Sum(h => h.ReturnedSeeds)
            || Count(after, NeedsFarmLoopEvidence.Potato) != Count(before, NeedsFarmLoopEvidence.Potato) + harvests.Sum(h => h.PotatoGain)
            || Count(after, NeedsFarmLoopEvidence.Auxiliary) != Count(before, NeedsFarmLoopEvidence.Auxiliary) + harvests.Sum(h => h.AuxiliaryGain))
            throw new InvalidOperationException("OBSERVED_FARM_CONSERVATION_MISMATCH");
    }

    internal static void RequireFarmSetupBody(FarmDurable body, FarmDurable seeded)
    {
        if (!body.Items.Where(i => i.Template != NeedsFarmLoopEvidence.Seed)
            .SequenceEqual(seeded.Items.Where(i => i.Template != NeedsFarmLoopEvidence.Seed))
            || body.Money != seeded.Money || body.Level != seeded.Level || body.Experience != seeded.Experience
            || !body.Actabilities.SequenceEqual(seeded.Actabilities))
            throw new InvalidOperationException("SEEDING_CHANGED_UNRELATED_BODY_OR_ITEM_INSTANCE_METADATA");
        if (!body.Quests.SequenceEqual(seeded.Quests) || !body.CompletedQuests.SequenceEqual(seeded.CompletedQuests)
            || !body.Houses.SequenceEqual(seeded.Houses))
            throw new InvalidOperationException("SEEDING_CHANGED_OWNED_QUEST_OR_HOUSE_STATE");
    }

    internal static void RequireFarmDurableStage(FarmDurable before, FarmDurable after, JsonElement frozenEnd, NeedsFarmLoopEvidence proof)
    {
        if (!MatchesFarmSnapshot(after, frozenEnd)) throw new InvalidOperationException("DURABLE_FROZEN_END_RESOURCE_XP_ACTABILITY_OR_POSITION");
        if (after.Crops.Count != 0) throw new InvalidOperationException("DURABLE_CROP_DELETION_OR_POST_END_EXTRA_CROP");
        RequireFarmConservation(before, after, proof);
    }

    private static async Task<object> ReadFarmCanonical(string path, CancellationToken token)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT p.doodad_func_group_id,g.delay,g.next_phase FROM doodad_phase_funcs p JOIN doodad_func_growths g ON g.id=p.actual_func_id WHERE p.doodad_func_group_id IN (4379,4456) AND p.actual_func_type='DoodadFuncGrowth' ORDER BY p.id";
        var phases = new List<(long Phase, long Delay, long Next)>();
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) phases.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        if (!phases.SequenceEqual(new[] { (4379L, 60000L, 4456L), (4456L, 540000L, 4457L) }))
            throw new InvalidOperationException("CANONICAL_ORDINARY_GROWTH_CHAIN_CHANGED");
        command.CommandText = "SELECT consume_lp,casting_time,max_range FROM skills WHERE id=13980";
        using var skill = await command.ExecuteReaderAsync(token);
        if (!await skill.ReadAsync(token) || skill.GetInt64(0) != 1 || skill.GetInt64(1) != 4000 || skill.GetDouble(2) != 4)
            throw new InvalidOperationException("CANONICAL_HARVEST_SKILL_CHANGED");
        return new { sourceId = "compact.sqlite3", sourcePath = path, version = "1.2 r208022 (retained archaeology catalog)",
            confidence = "fresh direct read plus retained catalog-first MCP receipts", seed = 15659, crop = 2259, output = 7992,
            growth = phases.Select(p => new { p.Phase, delayMs = p.Delay, p.Next }), growthRate = 10,
            ordinaryMaturitySeconds = new { climateMatch = phases.Sum(p => p.Delay) / 10000d * 0.73, noClimateMatch = phases.Sum(p => p.Delay) / 10000d },
            harvestSkill = 13980, harvestLabor = 1, authoredCastMs = 4000, canonicalMaxRangeMetres = 4,
            primaryYield = "7992 x 2..4, LootRate1 and actual DropRateMul0", optionalSeedReturn = "not required" };
    }

    [Fact]
    [Trait("Category", "e2e")]
    public async Task NeedsFarmLoop_NoFarmNearby_BoundedDefer_OnLiveServer()
    {
        // Negative leg (live): a seed-bearing bot with NO valid soil in probe
        // range defers boundedly — wakes keep stepping, no Plant ever lands,
        // no route spins forever. Uses an isolated far anchor: the probe
        // itself proves absence (found=false), then wakes prove the defer.
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        void Leg(string leg, bool passed, string detail)
        {
            legs.Add((leg, passed, detail, wall.Elapsed.TotalMilliseconds));
            Console.WriteLine($"[needs-farm-loop-neg] {(passed ? "PASS" : "FAIL")} {leg}: {detail}");
        }

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
        BotNetworkSession? net = null;
        try
        {
            E2eStack.EnsureUp();
            var negName = ("NfNeg" + Guid.NewGuid().ToString("N")[..8]).ToLowerInvariant();
            var negAccount = "e2efarmneg" + Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
            net = await BotNetworkSession.ConnectAsync(
                negName, negAccount, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!net.InWorld)
            {
                Leg("provision", false, "bot never in-world");
                throw new Xunit.Sdk.XunitException("net bot must be in-world");
            }
            Leg("provision", true, $"bot {negName} in-world");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            var rig = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"rig\",\"bot\":\"{negName}\",\"seeds\":3,\"calves\":0,\"labor\":5000}}",
                timeoutMs: 60_000);
            if (rig.GetProperty("seeds").GetInt32() < 1)
            {
                Leg("rig", false, "no seeds: " + rig);
                throw new Xunit.Sdk.XunitException("rig failed: " + rig);
            }
            Leg("rig", true, "seed stocked");

            // Absence proof: a 5m probe around spawn must find nothing AND
            // the bot must not already stand on soil (else this lane cannot
            // prove the negative).
            var soil = bridge.Call(
                $"{{\"cmd\":\"farm\",\"op\":\"soil\",\"bot\":\"{negName}\",\"radius\":5,\"step\":5}}",
                timeoutMs: 30_000);
            if (soil.GetProperty("onSoil").GetBoolean() || soil.GetProperty("found").GetBoolean())
            {
                Leg("absence", false, "spawn is on/near soil — negative lane N/A here: " + soil);
                throw new Xunit.Sdk.XunitException("SKIP: spawn near soil, negative not provable on this lane");
            }
            Leg("absence", true, "no soil within 5m — defer expected");

            var stepped = 0;
            string? lastPhase = null;
            for (var i = 0; i < 10; i++)
            {
                var w = bridge.Call(
                    $"{{\"cmd\":\"farm\",\"op\":\"needs\",\"bot\":\"{negName}\",\"waitMs\":15000}}",
                    timeoutMs: 60_000);
                if (w.GetProperty("stepped").GetBoolean())
                    stepped++;
                lastPhase = w.GetProperty("phase").GetString();
                if (w.GetProperty("plants").GetInt32() > 0)
                {
                    Leg("bounded-defer", false, "plant landed with no soil in range");
                    throw new Xunit.Sdk.XunitException("plant landed without soil");
                }
            }
            if (stepped == 0)
            {
                Leg("bounded-defer", false, "no scheduler steps ran");
                throw new Xunit.Sdk.XunitException("no steps ran");
            }
            Leg("bounded-defer", true, $"{stepped}/10 wakes stepped, no plant, last phase={lastPhase} — bounded, no spin");

            await WriteNegReportAsync(true, legs, "bounded defer live proof", wall.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            legs.Add(("exception", false, ex.GetType().Name + ": " + ex.Message, wall.Elapsed.TotalMilliseconds));
            await WriteNegReportAsync(false, legs, ex.ToString(), wall.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            net?.Dispose();
            var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
            var fatals = CountLogTailMatches(logOffset, "|FATAL|");
            if (unhandled != 0 || fatals != 0)
                Console.WriteLine($"[needs-farm-loop-neg] WARN log tail: {unhandled} unhandled + {fatals} fatal(s)");
        }
    }


    private async Task WriteNegReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "NeedsFarmLoopNegative",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["note"] = "no-farm-nearby bounded defer live proof; H stays UNKNOWN",
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "needs-farm-loop-negative-report.json"),
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int CountLogTailMatches(long startOffset, string marker)
    {
        if (!File.Exists(GameLogPath))
            return 0;
        var count = 0;
        using var stream = new FileStream(GameLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}
