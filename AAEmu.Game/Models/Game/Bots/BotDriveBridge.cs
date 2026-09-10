using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Network;
using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// M2b-E2E bot drive bridge — additive test-control surface (AGENTS.md #9/#10).
///
/// A loopback-only JSON/TCP control channel that executes
/// <see cref="PlayerBotController"/> ops on bot characters that entered the
/// world through the REAL login flow (real GameConnection over the real
/// network path). The bridge NEVER creates sessions, NEVER writes quest state
/// directly, and NEVER bypasses the quest engine — every mutation flows
/// through the same surfaces the pilot uses: CharacterQuests.AddQuest, the
/// UnitEvents engine surface, and QuestManager.DoReportEvents (the exact path
/// CSCompleteQuestContextPacket takes).
///
/// DISABLED BY DEFAULT. Enabled only when the runtime Config.Local.json sets
/// "Bots": { "EnableE2EBridge": true } (or the E2E_BRIDGE_ENABLED env var is
/// 1/true); prod config never sets it. Port: "Bots"."E2EBridgePort" /
/// E2E_BRIDGE_PORT (default 1260), bound to 127.0.0.1 only.
/// </summary>
public sealed class BotDriveBridge
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static BotDriveBridge Instance { get; } = new();

    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private int _port = 1260;

    public bool IsRunning { get; private set; }

    private BotDriveBridge()
    {
    }

    /// <summary>
    /// Test-control teleport that keeps the world's region bookkeeping intact.
    /// PB-006 root cause: mutating <c>Transform.Local.Position</c> alone left
    /// the character registered in its PREVIOUS Region, so every proximity
    /// broadcast in the destination area (SCOneUnitMovementPacket from ships,
    /// NPC/bot movement, chat-range packets) resolved ZERO receivers for it —
    /// units visibly stopped replicating to the teleported character. Normal
    /// movement re-registers through GameObject.SetPosition → AddVisibleObject;
    /// this helper restores exactly that handoff (region membership + neighbor
    /// visibility) after the raw position write. ZoneId is kept as a parameter
    /// because every call site pairs the position with a zone override.
    /// </summary>
    internal static void TeleportWithRegionSync(Character character, System.Numerics.Vector3 position, uint? zoneId = null)
    {
        character.Transform.Local.Position = position;
        if (zoneId.HasValue)
            character.Transform.ZoneId = zoneId.Value;
        WorldManager.Instance.AddVisibleObject(character);
    }

    /// <summary>
    /// Reads config and starts the listener when enabled. Safe to call from
    /// the assembly-load bootstrap; no-ops when disabled or already running.
    /// </summary>
    public void TryStart()
    {
        if (IsRunning)
            return;

        if (!ReadConfig())
            return;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            IsRunning = true;
            Logger.Info($"E2E bot drive bridge listening on 127.0.0.1:{_port} (test control surface — disabled in prod config)");
            ArmSoakScheduler();
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "E2E bot drive bridge failed to start");
        }
    }

    /// <summary>
    /// Soak-lane scheduler arming (slice #6 clearance): the soak lane drives
    /// bots synchronously past the scheduler (see <see cref="CollectGateMetrics"/>),
    /// so nothing ever called <see cref="IPlayerBotScheduler.Start"/> here and
    /// the lane reported isRunning=false/totalStepsRun=0. Starting the
    /// scheduler does NOT change synchronous driving — with no Wake calls the
    /// scan loop, workers and tick drain idle empty — but it makes the
    /// scheduler live for presence/admin-enrolled bots and turns
    /// metrics.scheduler into a real liveness signal. Idempotent; a failure
    /// is WARN-only and never breaks the bridge.
    /// </summary>
    private static void ArmSoakScheduler()
    {
        try
        {
            SingletonContainer.ServiceProvider?.GetService<IPlayerBotScheduler>()?.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "E2E bridge: soak scheduler arming failed (scheduler signals stay invalid)");
        }
    }

    private bool ReadConfig()
    {
        var enabled = false;
        var port = 1260;

        // Env overrides first (docker/compose friendly).
        var envEnabled = Environment.GetEnvironmentVariable("E2E_BRIDGE_ENABLED");
        if (envEnabled is "1" or "true" or "True")
            enabled = true;
        var envPort = Environment.GetEnvironmentVariable("E2E_BRIDGE_PORT");
        if (int.TryParse(envPort, out var parsedPort) && parsedPort is > 0 and < 65536)
            port = parsedPort;

        // Config file next: Config.Local.json, then Config.json (machine-specific
        // overrides win — the same precedence the host config uses).
        foreach (var fileName in new[] { "Config.Local.json", "Config.json" })
        {
            var path = Path.Combine(FileManager.AppPath, fileName);
            if (!File.Exists(path))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Bots", out var bots) &&
                    bots.ValueKind == JsonValueKind.Object)
                {
                    if (bots.TryGetProperty("EnableE2EBridge", out var flag) &&
                        flag.ValueKind == JsonValueKind.True)
                    {
                        enabled = true;
                    }

                    if (bots.TryGetProperty("E2EBridgePort", out var p) &&
                        p.TryGetInt32(out var cfgPort) && cfgPort is > 0 and < 65536)
                    {
                        port = cfgPort;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "E2E bridge: failed to read {Path}", path);
            }
        }

        _port = port;
        return enabled;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "E2E bridge accept error");
                break;
            }

            _ = Task.Run(() => ServeClientAsync(client, ct));
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                    break;

                string response;
                try
                {
                    response = HandleCommand(line);
                }
                catch (Exception ex)
                {
                    // Gate observability: log the FULL stack — a bridge op NRE
                    // must be diagnosable from the server log, not just echoed
                    // as a one-line error string to the test.
                    Logger.Error(ex, "E2E bridge command failed: {Line}", line);
                    response = Err($"bridge error: {ex.GetType().Name}: {ex.Message}");
                }

                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "E2E bridge client session ended: {Message}", ex.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    private string HandleCommand(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var cmd = root.GetProperty("cmd").GetString();

        switch (cmd)
        {
            case "ping":
                return Ok(new { pong = true, bridgePort = _port });
            case "stats":
                return Ok(CollectStats());
            case "metrics":
                return Ok(CollectGateMetrics());
            case "transfers":
                return Ok(new { transfers = CollectLiveTransfers() });
            case "drive":
                return HandleDrive(root);
            case "save":
                return HandleSave(root);
            case "scenario":
                return HandleScenario(root);
            case "provision":
                return HandleProvision(root);
            case "deactivate":
                return HandleDeactivate(root);
            case "auction":
                return HandleAuctionOp(root);
            case "dominion":
                return HandleDominionOp(root);
            case "mail":
                return HandleMailOp(root);
            case "seedDormant":
                return HandleSeedDormant(root);
            case "farm":
                return HandleFarmOp(root);
            case "housing":
                return HandleHousingOp(root);
            default:
                return Err($"unknown cmd '{cmd}'");
        }
    }

    private object CollectStats()
    {
        var connections = GameConnectionTable.Instance.GetConnections();
        return new
        {
            connections = connections.Count,
            inWorld = connections.Count(c => c.ActiveChar != null),
            accounts = AccountManager.Instance.Count()
        };
    }

    /// <summary>
    /// Gate-harness metrics surface (test seam — additive, no behavior
    /// change). Returns whatever the running server actually exposes:
    /// TickManager duration metrics + ActiveRegionTick budget stats (H2),
    /// PlayerBotScheduler wake-latency metrics (slice #6), PopulationDirector
    /// fidelity counts (slice #9). Missing systems report null — the gate
    /// runner treats absent instrumentation as a gate condition (e.g. stage 25
    /// hard-stops when H2 metrics are missing), never as a silent pass.
    /// </summary>
    private object CollectGateMetrics()
    {
        // H2 — TickManager duration metrics (p50/p95/max + per-subscriber).
        object tick = null;
        try
        {
            var m = TickManager.Instance.GetTickMetrics();
            tick = new
            {
                available = true,
                subscriberCount = m.SubscriberCount,
                invokeSampleCount = m.InvokeSampleCount,
                invokeP50Ms = m.InvokeP50Ms,
                invokeP95Ms = m.InvokeP95Ms,
                invokeMaxMs = m.InvokeMaxMs,
                subscribers = m.Subscribers.ToDictionary(
                    kv => kv.Key,
                    kv => new { kv.Value.SampleCount, kv.Value.P50Ms, kv.Value.P95Ms, kv.Value.MaxMs })
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: tick snapshot unavailable");
            tick = new { available = false, error = ex.Message };
        }

        // H2 — ActiveRegionTick per-pass budget stats.
        object regionTick = null;
        try
        {
            var s = WorldManager.Instance.RegionTickStats;
            regionTick = new
            {
                available = true,
                charactersTotal = s.CharactersTotal,
                characterSnapshotMs = s.CharacterSnapshotMs,
                spawnerScanMs = s.SpawnerScanMs,
                charactersProcessed = s.CharactersProcessed,
                matesProcessed = s.MatesProcessed,
                slavesProcessed = s.SlavesProcessed,
                spawnersTotal = s.SpawnersTotal,
                spawnersProcessed = s.SpawnersProcessed,
                elapsedMs = s.ElapsedMs,
                budgetMs = WorldManager.ActiveRegionTickBudgetMs
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: region tick stats unavailable");
            regionTick = new { available = false, error = ex.Message };
        }

        // Slice #6 — PlayerBotScheduler wake-latency metrics (null when the
        // scheduler isn't registered in DI, e.g. a build without slice #6).
        // The soak lane drives bots synchronously past the scheduler: every
        // bridge command executes GameplayActor legs inline and returns the
        // result in the same command, so async scheduler cadence can never
        // drive it — bridge-driven bots are never Woken and never run
        // scheduler steps. TryStart arms the scheduler (live scan/workers/
        // tick-drain, idle when no bot is enrolled) so isRunning is a real
        // liveness signal, but only presence/admin-enrolled bots produce
        // steps. Gate precondition: wake-latency/due-depth/utilization are
        // valid ONLY when signalsValid (isRunning && totalStepsRun > 0); a
        // zero step count INVALIDATES the signals, never passes them.
        object scheduler = null;
        try
        {
            var s = SingletonContainer.ServiceProvider?.GetService<IPlayerBotScheduler>();
            if (s != null)
            {
                var m = s.GetMetrics();
                scheduler = new
                {
                    available = true,
                    isRunning = s.IsRunning,
                    workerCount = m.WorkerCount,
                    activeWorkers = m.ActiveWorkers,
                    dueQueueDepth = m.DueQueueDepth,
                    eventQueueDepth = m.EventQueueDepth,
                    inFlight = m.InFlight,
                    totalStepsRun = m.TotalStepsRun,
                    totalStepsSkipped = m.TotalStepsSkipped,
                    signalsValid = s.IsRunning && m.TotalStepsRun > 0,
                    totalStepsFailed = m.TotalStepsFailed,
                    totalStepsTimedOut = m.TotalStepsTimedOut,
                    avgWakeLatencyMs = m.AverageWakeLatencyMs,
                    maxWakeLatencyMs = m.MaxWakeLatencyMs,
                    workerUtilization = m.WorkerUtilization,

                    // G2-A3: synchronized-cadence exposure — bots popped per
                    // wake-scan cycle (last) and worst cycle ever seen.
                    lastCycleDue = m.LastCycleDue,
                    maxCycleDue = m.MaxCycleDue
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: scheduler metrics unavailable");
            scheduler = new { available = false, error = ex.Message };
        }

        // Slice #9 — PopulationDirector fidelity counts (null when absent).
        object population = null;
        try
        {
            var p = SingletonContainer.ServiceProvider?.GetService<IPopulationDirector>();
            if (p != null)
            {
                var m = p.GetMetrics();

                // G2-A5 acceptance instrumentation: true-dormancy registry
                // counters + materialization latency percentiles (nulls when
                // the registry is absent, e.g. a build without slice A5).
                object dormancy = null;
                try
                {
                    var reg = SingletonContainer.ServiceProvider?.GetService<DormantBotRegistry>();
                    if (reg != null)
                    {
                        var lat = reg.GetMaterializationLatency();
                        dormancy = new
                        {
                            dormantSpecs = reg.ListSpecs().Count,
                            totalMaterializations = m.TotalMaterializations,
                            totalDematerializations = m.TotalDematerializations,
                            materializeCount = lat.SampleCount,
                            materializeP50Ms = lat.P50Ms,
                            materializeP95Ms = lat.P95Ms,
                            materializeP99Ms = lat.P99Ms,
                            materializeMaxMs = lat.MaxMs
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "gate metrics: dormant registry snapshot unavailable");
                }
                // G2-A3 storm instrumentation: fidelity-transition wall-clock
                // percentiles + proximity-sweep wall-clock percentiles, from
                // the concrete director (registry-latency precedent).
                object transitions = null;
                object sweep = null;
                try
                {
                    if (p is PopulationDirector director)
                    {
                        var tl = director.GetTransitionLatency();
                        transitions = new
                        {
                            count = tl.SampleCount,
                            p50Ms = tl.P50Ms,
                            p95Ms = tl.P95Ms,
                            p99Ms = tl.P99Ms,
                            maxMs = tl.MaxMs
                        };
                        var sl = director.GetProximitySweepLatency();
                        sweep = new
                        {
                            count = sl.SampleCount,
                            p50Ms = sl.P50Ms,
                            p95Ms = sl.P95Ms,
                            p99Ms = sl.P99Ms,
                            maxMs = sl.MaxMs
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "gate metrics: director latency snapshots unavailable");
                }

                population = new
                {
                    available = true,
                    dormant = m.DormantCount,
                    reduced = m.ReducedCount,
                    full = m.FullCount,
                    embodied = m.Embodied,
                    pressure = m.Pressure.ToString(),
                    transitionsApplied = m.TotalTransitionsApplied,
                    transitionsRejected = m.TotalTransitionsRejected,
                    totalMaterializations = m.TotalMaterializations,
                    totalDematerializations = m.TotalDematerializations,
                    dormancy,
                    transitions,
                    sweep
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: population metrics unavailable");
            population = new { available = false, error = ex.Message };
        }

        // M3b — SaveManager autosave duration metrics (autosave p95 < 2s at
        // gate scale). Ring-buffer percentiles over DoSave wall-clock.
        object save = null;
        try
        {
            var m = SaveManager.Instance.GetSaveMetrics();
            save = new
            {
                available = true,
                sampleCount = m.SampleCount,
                p50Ms = m.P50Ms,
                p95Ms = m.P95Ms,
                maxMs = m.MaxMs,
                skipCount = m.SkipCount
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: save metrics unavailable");
            save = new { available = false, error = ex.Message };
        }
        // A5 — per-iteration physics telemetry (loop gap, sleep overshoot,
        // Step duration, broadcast duration, workload counts). The sampler is
        // off by default in prod config, which is why the soak lane saw
        // available=false every cycle despite the physics loop calling
        // Record: Record early-returns while disabled. The first metrics poll
        // arms sampling (sticky, test-lane only), so subsequent per-cycle
        // snapshots carry real percentiles + body/ship/force counts.
        object physics = null;
        try
        {
            // A5 soak physics is the default (main) world's physics thread.
            // Prefer the default instance; fall back to the first world only
            // when the default is not loaded yet (GetWorld logs FATAL per miss).
            var worlds = WorldManager.Instance.GetWorlds();
            var world = worlds.FirstOrDefault(w => w.Id == WorldManager.DefaultInstanceId)
                ?? worlds.FirstOrDefault();
            var telemetry = world?.Physics?.Telemetry;
            if (telemetry != null)
            {
                telemetry.EnsureEnabled();
                var m = telemetry.Snapshot();
                physics = new
                {
                    available = m.Available,
                    worldId = world?.Id,
                    sampleCount = m.SampleCount,
                    loopGapP50Ms = m.LoopGapP50Ms,
                    loopGapP95Ms = m.LoopGapP95Ms,
                    loopGapMaxMs = m.LoopGapMaxMs,
                    sleepOvershootP50Ms = m.SleepOvershootP50Ms,
                    sleepOvershootP95Ms = m.SleepOvershootP95Ms,
                    sleepOvershootMaxMs = m.SleepOvershootMaxMs,
                    stepP50Ms = m.StepP50Ms,
                    stepP95Ms = m.StepP95Ms,
                    stepMaxMs = m.StepMaxMs,
                    broadcastP50Ms = m.BroadcastP50Ms,
                    broadcastP95Ms = m.BroadcastP95Ms,
                    broadcastMaxMs = m.BroadcastMaxMs,
                    pendingActionsMax = m.PendingActionsMax,
                    bodiesMax = m.BodiesMax,
                    shipsMax = m.ShipsMax,
                    forcesMax = m.ForcesMax
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: physics telemetry unavailable");
            physics = new { available = false, error = ex.Message };
        }
        // DB statement volume (MySqlStatementCounters over the provider
        // connector-net activities — TOTAL statements, not writes: MySql.Data
        // omits statement text/rows for user DML, so no read/write split is
        // possible here. DbWritesAvailable stays false until call-site write
        // instrumentation exists; the gate must NOT treat statement volume
        // as write volume.
        object db = null;
        try
        {
            var s = MySqlStatementCounters.Snapshot();
            db = new
            {
                available = true,
                statements = s.Statements,
                failedStatements = s.FailedStatements,
                gameStatements = s.GameStatements,
                loginStatements = s.LoginStatements,
                otherStatements = s.OtherStatements,
                dbWritesAvailable = false,
                dbWritesReason = "provider activities carry no statement text or rows-affected; write split needs call-site instrumentation"
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: db statement counters unavailable");
            db = new { available = false, error = ex.Message };
        }

        return new
        {
            tick,
            regionTick,
            scheduler,
            save,
            physics,
            db,
            uptimeMs = Environment.TickCount64
        };
    }

    /// <summary>
    /// Read-only live-transfer dump (test seam): walks every world's
    /// <see cref="TransferManager.GetTransfers"/> and, per transfer, its
    /// AttachedDoodads seat benches resolved against
    /// <see cref="DoodadManager.GetFuncsForGroup"/> /
    /// <see cref="DoodadManager.GetFuncTemplate"/> DoodadFuncAttachment
    /// templates. No state is touched — the exact registry + template data
    /// CSBoardingTransferPacket consults.
    /// </summary>
    private object[] CollectLiveTransfers()
    {
        var result = new List<object>();
        foreach (var world in WorldManager.Instance.GetWorlds())
        {
            foreach (var transfer in world.TransferManager.GetTransfers())
            {
                var seats = new List<object>();
                foreach (var doodad in transfer.AttachedDoodads)
                {
                    foreach (var func in DoodadManager.Instance.GetFuncsForGroup(doodad.FuncGroupId))
                    {
                        if (func.FuncType != "DoodadFuncAttachment")
                            continue;
                        if (DoodadManager.Instance.GetFuncTemplate(func.FuncId, func.FuncType)
                            is not DoodadFuncAttachment attachment)
                            continue;
                        seats.Add(new
                        {
                            doodadObjId = doodad.ObjId,
                            doodadTemplateId = doodad.TemplateId,
                            attachPoint = (byte)attachment.AttachPointId,
                            bondKind = attachment.BondKindId.ToString()
                        });
                    }
                }

                result.Add(new
                {
                    worldId = world.Id,
                    tlId = transfer.TlId,
                    objId = transfer.ObjId,
                    name = transfer.Name,
                    position = new
                    {
                        x = transfer.Transform.World.Position.X,
                        y = transfer.Transform.World.Position.Y,
                        z = transfer.Transform.World.Position.Z
                    },
                    seats = seats.ToArray()
                });
            }
        }

        return result.ToArray();
    }

    private string HandleDrive(JsonElement root)
    {
        var botName = root.GetProperty("bot").GetString();
        var op = root.GetProperty("op").GetString();
        if (string.IsNullOrWhiteSpace(botName) || string.IsNullOrWhiteSpace(op))
            return Err("drive requires 'bot' and 'op'");

        // Bots are REAL networked sessions: only characters that entered the
        // world through the real login flow (GameConnection with an ActiveChar)
        // are drivable. The bridge never fabricates sessions. Character names
        // are matched case-insensitively (the server normalizes names on
        // create — "bot1c1" becomes "Bot1c1").
        var connections = GameConnectionTable.Instance.GetConnections();
        var connection = connections
            .FirstOrDefault(c => c.ActiveChar != null &&
                string.Equals(c.ActiveChar.Name, botName, StringComparison.OrdinalIgnoreCase));
        if (connection?.ActiveChar == null)
        {
            // Self-diagnosing miss: dump what the table actually holds so a
            // mid-drive session loss (M2b-E2E restart266, run29) identifies
            // itself — connection removed vs ActiveChar null vs name drift.
            var table = string.Join("; ",
                connections.Select(c => $"{c.Id}:{(c.ActiveChar == null ? "<no-char>" : c.ActiveChar.Name)}({c.State})"));
            return Err($"bot '{botName}' is not in the world (no active networked session) — table [{table}]");
        }

        var character = connection.ActiveChar;
        var controller = new PlayerBotController(character);

        var quest = GetUInt(root, "quest");
        switch (op)
        {
            case "accept":
            {
                var acceptorType = Enum.Parse<QuestAcceptorType>(
                    root.GetProperty("acceptor").GetString() ?? "Unknown", ignoreCase: true);
                return Ok(new { accepted = controller.AcceptQuest(quest, acceptorType, GetUInt(root, "acceptorId")) });
            }
            case "advance":
                controller.Advance(quest);
                return Ok(new { advanced = true });
            case "kill":
                controller.KillNpc(GetUInt(root, "npc"), GetInt(root, "count", 1));
                return Ok(new { fired = true });
            case "killGroup":
                controller.KillNpcGroup(GetUInt(root, "npc"), GetInt(root, "count", 1));
                return Ok(new { fired = true });
            case "gather":
                controller.GatherItem(quest, GetUInt(root, "item"), GetInt(root, "count", 1));
                return Ok(new { fired = true });
            case "useItem":
                controller.UseItem(GetUInt(root, "item"), GetInt(root, "times", 1));
                return Ok(new { fired = true });
            case "talk":
                controller.TalkToNpc(quest, GetUInt(root, "npc"));
                return Ok(new { fired = true });
            case "interact":
                controller.InteractWithDoodad(GetUInt(root, "doodad"), GetInt(root, "times", 1));
                return Ok(new { fired = true });
            case "enterSphere":
                controller.EnterSphere(quest, GetUInt(root, "component"));
                return Ok(new { fired = true });
            case "express":
                controller.ExpressEmotion(GetUInt(root, "npc"), GetUInt(root, "emotion"));
                return Ok(new { fired = true });
            case "levelUp":
                controller.LevelUp();
                return Ok(new { fired = true });
            case "aggro":
                controller.AggroNpc(GetUInt(root, "npc"));
                return Ok(new { fired = true });
            case "zoneKill":
                controller.ZoneKill(GetUInt(root, "zoneGroup"));
                return Ok(new { fired = true });
            case "cinemaStarted":
                controller.CinemaStarted(GetUInt(root, "cinema"));
                return Ok(new { fired = true });
            case "cinemaEnded":
                controller.CinemaEnded(GetUInt(root, "cinema"));
                return Ok(new { fired = true });
            case "report":
            {
                // Real turn-in at a REAL world NPC: resolve the template id to a
                // live NPC objId (the exact path DoReportEvents validates).
                var npcTemplate = GetUInt(root, "npc");
                var npc = character.ParentWorld.GetNpcByTemplateId(npcTemplate);
                if (npc == null)
                    return Err($"report: NPC template {npcTemplate} not spawned in the live world (no objId to turn in at)");
                _ = controller.ReportTurnIn(quest, npc.ObjId, GetInt(root, "selected", -1));
                return Ok(new { reported = true, npcObjId = npc.ObjId });
            }
            case "reportDoodad":
            {
                var doodadTemplate = GetUInt(root, "doodad");
                var doodad = character.ParentWorld.GetAllDoodads()
                    .FirstOrDefault(d => d.TemplateId == doodadTemplate);
                if (doodad == null)
                    return Err($"reportDoodad: doodad template {doodadTemplate} not spawned in the live world");
                _ = controller.ReportDoodadTurnIn(quest, doodad.ObjId, GetInt(root, "selected", -1));
                return Ok(new { reported = true, doodadObjId = doodad.ObjId });
            }
            case "teleportToNpc":
            {
                // Test-control positioning: the live world only spawns NPCs
                // within a player's radius (NpcSpawner.IsPlayerInSpawnRadius),
                // so a static bot would never see its turn-in NPCs. Move the
                // bot to the NPC's spawner position — the world then spawns
                // the NPC through its NORMAL spawn path. No quest state is
                // touched; the report op still requires a real spawned objId.
                var npcTemplate = GetUInt(root, "npc");
                var spawner = character.ParentWorld.SpawnManager.GetAllSpawners()
                    .SelectMany(s => s.Value)
                    .FirstOrDefault(s => s.UnitId == npcTemplate);
                if (spawner == null)
                    return Err($"teleportToNpc: no spawner found for NPC template {npcTemplate}");
                TeleportWithRegionSync(character,
                    new System.Numerics.Vector3(spawner.Position.X, spawner.Position.Y, spawner.Position.Z),
                    spawner.Position.ZoneId);
                character.MarkDirty(); // position changed — persist on the next save cycle
                return Ok(new
                {
                    x = spawner.Position.X,
                    y = spawner.Position.Y,
                    z = spawner.Position.Z,
                    zoneId = spawner.Position.ZoneId,
                    spawnerId = spawner.SpawnerId
                });
            }
            case "npcObjId":
            {
                var npc = character.ParentWorld.GetNpcByTemplateId(GetUInt(root, "npc"));
                return Ok(new { objId = npc?.ObjId ?? 0u });
            }
            case "doodadObjId":
            {
                var doodad = character.ParentWorld.GetAllDoodads()
                    .FirstOrDefault(d => d.TemplateId == GetUInt(root, "doodad"));
                return Ok(new { objId = doodad?.ObjId ?? 0u });
            }
            case "autoTurnIn":
                _ = controller.AutoTurnIn(quest, GetInt(root, "selected", -1));
                return Ok(new { reported = true });
            case "stock":
                controller.StockInventory(GetUInt(root, "item"), GetInt(root, "count", 1));
                return Ok(new { stocked = true });
            case "setLevel":
                character.Level = (byte)GetInt(root, "level", 1);
                return Ok(new { level = character.Level });
            case "questState":
            {
                var activeQuest = controller.ActiveQuest(quest);
                if (activeQuest == null)
                    return Ok(new { active = false });
                return Ok(new
                {
                    active = true,
                    step = activeQuest.Step.ToString(),
                    status = activeQuest.Status.ToString(),
                    objectives = activeQuest.Objectives
                });
            }
            case "invCount":
                return Ok(new { count = controller.InventoryCount(GetUInt(root, "item")) });
            case "isActive":
                return Ok(new { active = controller.IsActive(quest) });
            case "hasCompleted":
                return Ok(new { completed = controller.HasCompleted(quest) });
            case "charState":
                return Ok(new
                {
                    name = character.Name,
                    level = character.Level,
                    objId = character.ObjId,
                    connectionId = connection.Id,
                    state = connection.State.ToString(),
                    activeQuests = character.Quests.ActiveQuests.Count
                });
            case "cast":
            {
                // Q4 live hunt-leg seam (E2E-ONLY, additive): real offensive
                // cast through the M5 contract on the live networked
                // character — the same call the CSStartSkillPacket
                // learned-skill branch makes (GameplayActor.Cast). Target HP
                // is sampled around the cast so the runner can observe the
                // kill without a second verb.
                var skillId = GetUInt(root, "skill");
                if (skillId == 0)
                    return Err("cast requires 'skill'");
                var castObjId = GetUInt(root, "npcObjId");
                Npc? target = null;
                if (castObjId != 0)
                {
                    target = character.ParentWorld.GetNpc(castObjId);
                }
                else
                {
                    var castTemplate = GetUInt(root, "npc");
                    if (castTemplate == 0)
                        return Err("cast requires 'npcObjId' or 'npc' (template id)");
                    var here = character.Transform.World.Position;
                    target = character.ParentWorld.GetAllNpcs()
                        .Where(n => n.TemplateId == castTemplate && n.Hp > 0)
                        .OrderBy(n => (n.Transform.World.Position - here).LengthSquared())
                        .FirstOrDefault();
                    if (target == null)
                        return Err($"cast: no live NPC of template {castTemplate} in world");
                }
                if (target == null)
                    return Err($"cast: target objId {castObjId} not in world (despawned?)");
                var hpBefore = target.Hp;
                var cast = new GameplayActor(character).Cast(skillId, target.ObjId);
                return Ok(new
                {
                    state = cast.State.ToString(),
                    result = cast.Result?.ToString(),
                    failure = cast.Failure.ToString(),
                    detail = cast.Detail ?? "",
                    targetObjId = target.ObjId,
                    targetTemplate = target.TemplateId,
                    hpBefore,
                    hpAfter = target.Hp,
                    targetAlive = target.Hp > 0
                });
            }
            case "loot":
            {
                // Q4 live hunt-leg seam (E2E-ONLY, additive): caller-delta
                // loot grant through the M5 contract — the exact call
                // CSLootOpenBagPacket makes with lootAll=true
                // (GameplayActor.Loot). The caller's bag + money deltas are
                // diffed atomically server-side: the runner asserts caller
                // deltas, never container counts (Result counts container
                // ENTRIES, not units).
                var lootObjId = GetUInt(root, "npcObjId");
                if (lootObjId == 0)
                    return Err("loot requires 'npcObjId'");
                var lootOwnerBefore = character.ParentWorld?.GetBaseUnit(lootObjId);
                var containerBefore = lootOwnerBefore?.LootingContainer.Items.Count ?? -1;
                var moneyBefore = character.Money;
                var bagBefore = CountBagByTemplate(character);
                var loot = new GameplayActor(character).Loot(lootObjId);
                var bagAfter = CountBagByTemplate(character);
                var delta = new List<object>();
                foreach (var template in bagBefore.Keys.Concat(bagAfter.Keys).Distinct().OrderBy(t => t))
                {
                    bagBefore.TryGetValue(template, out var b);
                    bagAfter.TryGetValue(template, out var a);
                    if (a != b)
                        delta.Add(new { template, before = b, after = a, delta = a - b });
                }
                var remaining = character.ParentWorld?.GetBaseUnit(lootObjId)?.LootingContainer.Items.Count ?? -1;
                return Ok(new
                {
                    state = loot.State.ToString(),
                    granted = loot.Result is int g ? g : 0,
                    failure = loot.Failure.ToString(),
                    detail = loot.Detail ?? "",
                    containerBefore,
                    remaining,
                    moneyBefore,
                    moneyAfter = character.Money,
                    moneyDelta = character.Money - moneyBefore,
                    bagDelta = delta.ToArray()
                });
            }
            case "charPos":
                // Read-only diagnostic (PB-003 exit E2E): engine-truth transform
                // for the bot, independent of GM command access levels.
                return Ok(new
                {
                    x = character.Transform.World.Position.X,
                    y = character.Transform.World.Position.Y,
                    z = character.Transform.World.Position.Z,
                    zoneId = character.Transform.ZoneId,
                    instanceId = character.Transform.InstanceId,
                    worldId = character.ParentWorld?.Id ?? 0u,
                    worldName = character.ParentWorld?.Template.Name ?? string.Empty
                });
            default:
                return Err($"unknown drive op '{op}'");
        }
    }

    /// <summary>
    /// M3b test-hardening seam (t_1329a833): deterministic save trigger.
    /// Marks every loaded house dirty — the same flag real gameplay
    /// mutations set (nothing is written until the save pass runs) — then
    /// executes the REAL save path (<see cref="SaveManager.DoSave"/>). This
    /// guarantees the save pass holds a real transaction with real housings
    /// writes even in a world where nothing changed through gameplay: under
    /// A4 dirty-tracking a clean world's autosave executes zero statements,
    /// so no InnoDB transaction is ever visible to observe a mid-save kill.
    /// The M3b exit test holds a row lock so the pass blocks in flight for
    /// the kill observation; the response only returns after the pass
    /// completes, so the test fires this command fire-and-forget.
    /// </summary>
    private string HandleSave(JsonElement root)
    {
        var housesDirtied = 0;
        foreach (var house in HousingManager.Instance.GetAllHouses())
        {
            house.IsDirty = true;
            housesDirtied++;
        }

        // DoSave returns false only while another pass is already running
        // (SaveManager._isSaving — including a pass blocked on a slow/pool-
        // starved DB acquire); retry so this trigger always lands a pass.
        // With the test's row lock held, the pass blocks in-flight and this
        // call returns only after the game dies or the lock is released.
        var saved = false;
        var attempts = 0;
        for (; attempts < 30 && !saved; attempts++)
        {
            saved = SaveManager.Instance.DoSave(true);
            if (!saved)
                Thread.Sleep(500);
        }
        Logger.Info("E2E save trigger: dirtied {Houses} house(s), DoSave pass {Result} after {Attempts} attempt(s)",
            housesDirtied, saved ? "ran" : "never ran", attempts);
        if (!saved)
            Logger.Warn("E2E save trigger: DoSave never landed a pass in 15s (persistent _isSaving) — dirtied {Houses} house(s) remain pending the next tick", housesDirtied);
        return Ok(new { saved, housesDirtied });
    }

    private static string Ok(object data)
        => JsonSerializer.Serialize(new { ok = true, data });

    private static string Err(string error)
        => JsonSerializer.Serialize(new { ok = false, error });

    #region Scenario templates (P1 t_5efae4f1 — gate-harness scenario stage)

    /// <summary>
    /// Live-world adapter for the scenario runner: turn-in targets resolve
    /// through the REAL world (spawned NPCs from spawners). When a target
    /// NPC is not currently spawned, teleport the bot to the NPC's spawner
    /// position — the world then spawns it through its NORMAL spawn path
    /// (the same facility the "teleportToNpc" drive op uses). The runner
    /// still fails the stage when the target cannot be resolved at all.
    /// </summary>
    private sealed class LiveScenarioWorldAdapter : BotScenarioRunner.IScenarioWorldAdapter
    {
        private readonly Character _character;

        public LiveScenarioWorldAdapter(Character character) => _character = character;

        public uint ResolveNpcObjId(uint npcTemplateId)
        {
            if (npcTemplateId == 0)
                return 0;

            var world = _character.ParentWorld;
            var npc = world.GetNpcByTemplateId(npcTemplateId);
            if (npc != null)
                return npc.ObjId;

            // Prefer the NORMAL spawn path: move to the spawner so the world
            // spawns it through the NpcSpawner proximity logic, then poll for
            // the materialized NPC (spawns are async — spawn tick + radius
            // cache; the same 20s poll the E2E quest driver uses).
            var spawner = world.SpawnManager.GetAllSpawners()
                .SelectMany(s => s.Value)
                .FirstOrDefault(s => s.UnitId == npcTemplateId);
            if (spawner != null)
            {
                TeleportWithRegionSync(_character,
                    new System.Numerics.Vector3(spawner.Position.X, spawner.Position.Y, spawner.Position.Z),
                    spawner.Position.ZoneId);

                var deadline = Environment.TickCount64 + 20_000;
                while (Environment.TickCount64 < deadline)
                {
                    var spawned = world.GetNpcByTemplateId(npcTemplateId);
                    if (spawned != null)
                        return spawned.ObjId;
                    Thread.Sleep(1000);
                }

                Logger.Warn("scenario: NPC {NpcId} spawner {SpawnerId} blocked (schedule/cooldown) — direct-spawn fallback as report target",
                    npcTemplateId, spawner.SpawnerId);
            }
            else
            {
                Logger.Warn("scenario: NPC {NpcId} has NO spawner in the booted world data (main_world/npc_spawns.json) — direct-spawn as report target (world-data gap, not quest-engine defect)",
                    npcTemplateId);
            }

            // The quest report act validates the NPC TEMPLATE id only — the
            // spawner schedule / world placement is world simulation, not
            // quest-engine semantics. Use the REAL engine factory (the same
            // NpcManager.Create the spawner path calls — template, faction,
            // model all attached) so the NPC is a fully-formed world unit
            // (a template-less Npc would NRE the TimeManager time-of-day
            // scan on the next time change).
            var fallbackNpc = NpcManager.Instance.Create(world, 0, npcTemplateId);
            if (fallbackNpc == null)
                return 0; // template missing — the runner fails with a clear reason
            world.AddObject(fallbackNpc);
            return fallbackNpc.ObjId;
        }

        public uint ResolveDoodadObjId(uint doodadTemplateId)
        {
            if (doodadTemplateId == 0)
                return 0;
            var world = _character.ParentWorld;
            return world.GetAllDoodads().FirstOrDefault(d => d.TemplateId == doodadTemplateId)?.ObjId ?? 0;
        }
    }

    /// <summary>
    /// Runs a scenario template on a PROVISIONED bot (real managed account +
    /// character rows through HeadlessSession.Provision, embodied through
    /// the shared lifecycle) and returns the structured verdict. Request:
    /// {"cmd":"scenario","template":"level22-gate","bot":"tpl-l22-01"}.
    /// The bot name is optional (defaults to the template name).
    ///
    /// Templates are FRESH RIGS by default ("fresh": true): prior runs'
    /// persisted quest state (active quests, completed flags) would poison
    /// the accept gates, so the bot's rows + registry entry are wiped
    /// before provisioning. Pass "fresh": false to adopt a prior boot's row
    /// (restart-idempotency, server-reboot scenario). The bot is
    /// deactivated after the run.
    /// </summary>
    private string HandleScenario(JsonElement root)
    {
        var templateName = root.GetProperty("template").GetString();

        // Multi-actor seam (ROADMAP M7 hardening #1): templates that drive
        // SEVERAL provisioned bots (party follow+assist) cannot run through
        // the single-session template runner below — they own their own
        // provisioning + execution flow.
        if (templateName == PartyFollowAssistScenario.ScenarioName)
            return HandlePartyFollowAssistScenario(root);
        if (templateName == PartySpikeScenario.ScenarioName)
            return HandlePartySpikeScenario(root);
        if (templateName == VillageDayCycle.ScenarioName)
            return HandleVillageDayCycleScenario(root);
        if (templateName == VillageFullDayCycle.ScenarioName)
            return HandleVillageFullDayScenario(root);

        var template = templateName != null ? BotScenarioTemplates.Get(templateName) : null;
        if (template == null)
            return Err($"scenario: unknown template '{templateName}' (library: {string.Join(", ", BotScenarioTemplates.Library.Keys)}, {PartyFollowAssistScenario.ScenarioName}, {PartySpikeScenario.ScenarioName})");

        var botName = (root.TryGetProperty("bot", out var b) && b.GetString() is { Length: > 0 } bn
            ? bn
            : "tpl" + template.Name.Replace("-", "")).NormalizeName();
        var username = BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant();

        // Fresh-rig contract: wipe prior rows unless the caller opts into
        // adoption (server-reboot idempotency).
        var fresh = !root.TryGetProperty("fresh", out var freshEl) || freshEl.GetBoolean();
        if (fresh)
        {
            try
            {
                EnsureFreshBotRow(botName, username);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "scenario '{Template}': fresh wipe failed for '{Bot}' — continuing with adoption semantics", templateName, botName);
            }
        }

        HeadlessSession session;
        try
        {
            // Combat templates opt into appearance provisioning: the
            // per-class starting equipment (ApplyStartingEquipment — the
            // human create path) is what gives weapon-scaling skills real
            // damage. Everyone else keeps the plain provision shape.
            session = template.ProvisionWithAppearance
                ? HeadlessSession.Provision(username,
                    new BotAppearanceSpec(template.Race, template.Gender,
                        ClassAbility: template.AbilityTrees.Count > 0 ? template.AbilityTrees[0] : null,
                        Name: botName),
                    template.Level)
                : HeadlessSession.Provision(username, botName, template.Race, template.Gender, template.Level);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': provisioning failed for '{Bot}'", templateName, botName);
            return Err($"scenario: provisioning failed for '{botName}': {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var result = BotScenarioRunner.Run(template, session.Character, new LiveScenarioWorldAdapter(session.Character));
            var payload = new
            {
                template = result.Template,
                passed = result.Passed,
                failStage = result.FailStage,
                failure = result.Failure?.ToString(),
                failReason = result.FailReason,
                gates = result.Gates,
                stages = result.Stages,
                criteria = result.Criteria,
                traceRecords = result.TraceRecords.Select(r => r.ToJson()).ToList(),
                actorRequests = result.ActorRequests,
                rigNotes = result.RigNotes,
                // Per-action audit records (M5 trace contract shape via
                // ActorAuditRecord.ToJson — real server timestamps). The
                // deterministic evidence block intentionally carries no
                // wall-clock; the structured trace is the timestamped
                // artifact (evidence hygiene t_6e2725b5).
                trace = result.TraceRecords
                    .Select(r => JsonSerializer.Deserialize<JsonElement>(r.ToJson()))
                    .ToArray(),
                evidence = result.Evidence(),
                character = new
                {
                    name = session.Character.Name,
                    level = session.Character.Level,
                    objId = session.Character.ObjId
                }
            };
            Logger.Info("scenario '{Template}': {Verdict} on '{Bot}' ({Stage}{Failure})",
                templateName, result.Passed ? "PASS" : "FAIL", botName,
                result.FailStage, result.Failure is { } f ? $", {f}" : "");
            return Ok(payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': run crashed on '{Bot}'", templateName, botName);
            return Err($"scenario: run crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                CharacterLifecycleService.Instance.Deactivate(session.Character, CharacterLifecycleReason.Logout);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "scenario '{Template}': deactivate failed for '{Bot}'", templateName, botName);
            }
        }
    }

    /// <summary>
    /// Multi-actor execution seam (ROADMAP M7 hardening #1): runs
    /// <see cref="PartyFollowAssistScenario"/> on TWO real provisioned bots
    /// through the live E2E bridge. Request (all fields optional except
    /// "template"):
    ///
    ///   {"cmd":"scenario","template":"m7-party-follow-assist",
    ///    "leader":"m7pfa-leader","member":"m7pfa-member",
    ///    "npc":3492,
    ///    "followDistance":3.0,"moveSpeed":5.0,"moveTimeoutSeconds":30}
    ///
    /// Flow: fresh-wipe both bot rows → provision leader + member as plain
    /// headless sessions (shared <see cref="ProvisionBotParty"/> machinery:
    /// wipe → provision → shared-world convergence poll → explicit Spawn)
    /// → form the party through the CONTRACT
    /// (<see cref="GameplayActor.PartyInvite"/> + <see cref="GameplayActor.PartyAccept"/>,
    /// the exact rig path) → position the leader at the target NPC's spawner
    /// via <see cref="LiveScenarioWorldAdapter"/> and offset the member by
    /// ~+20 X so the FOLLOW leg actually drives → give the leader its target →
    /// run the scenario through its default overload (LivePartyRuntime) →
    /// return the same structured payload envelope as the single-bot runner,
    /// with a `characters` array for BOTH bots and `party` info from
    /// TeamManager. Both characters are deactivated afterwards.
    /// </summary>
    private string HandlePartyFollowAssistScenario(JsonElement root)
    {
        var leaderName = (root.TryGetProperty("leader", out var l) && l.GetString() is { Length: > 0 } ln
            ? ln
            : "m7pfa-leader").NormalizeName();
        var memberName = (root.TryGetProperty("member", out var m) && m.GetString() is { Length: > 0 } mn
            ? mn
            : "m7pfa-member").NormalizeName();
        var npcTemplateId = GetUInt(root, "npc");
        if (npcTemplateId == 0)
            npcTemplateId = 3492u; // Solzreed fox — the M7 spike target

        const byte level = 20; // combat is not under test — a sane adult level

        var provisionError = ProvisionBotParty(
            PartyFollowAssistScenario.ScenarioName, [leaderName, memberName], level, out var sessions);
        if (provisionError != null)
            return provisionError;

        var leaderSession = sessions[0];
        var memberSession = sessions[1];
        var leaderChar = leaderSession.Character;
        var memberChar = memberSession.Character;

        try
        {
            // --------------------------------------------------- PARTY FORM
            // The CONTRACT path — the exact calls the M7 rig makes. Both
            // actions post-check observable outcomes themselves; verify team
            // membership through the engine registry before running.
            var invite = new GameplayActor(leaderChar).PartyInvite(memberChar.ObjId);
            if (invite.State != ActorLifecycleState.Completed)
                return Err($"scenario: party invite failed ({invite.State}: {invite.Detail ?? "no detail"})");
            var accept = new GameplayActor(memberChar).PartyAccept();
            if (accept.State != ActorLifecycleState.Completed)
                return Err($"scenario: party accept failed ({accept.State}: {accept.Detail ?? "no detail"})");

            var team = TeamManager.Instance.GetActiveTeamByUnit(leaderChar.Id);
            if (team == null || !team.IsParty || !team.IsMember(memberChar.Id) || team.OwnerId != leaderChar.Id)
                return Err($"scenario: party did not form (team {(team == null ? "<null>" : team.Id.ToString())}, " +
                           $"owner {team?.OwnerId.ToString() ?? "<null>"}, expected owner {leaderChar.Id})");

            // ---------------------------------------------------- POSITION
            // Leader at the target NPC's spawner (the adapter resolves/spawns
            // the NPC through the NORMAL spawn path); member offset ~+20 X so
            // distanceBefore > FollowDistance and the FOLLOW leg drives.
            var npcObjId = new LiveScenarioWorldAdapter(leaderChar).ResolveNpcObjId(npcTemplateId);
            if (npcObjId == 0)
                return Err($"scenario: could not resolve a live objId for NPC template {npcTemplateId}");

            TeleportWithRegionSync(memberChar, leaderChar.Transform.Local.Position +
                new System.Numerics.Vector3(20f, 0f, 0f), leaderChar.Transform.ZoneId);

            var setTarget = new GameplayActor(leaderChar).SetTarget(npcObjId);
            if (setTarget.State != ActorLifecycleState.Completed)
                return Err($"scenario: leader SetTarget({npcObjId}) failed ({setTarget.State}: {setTarget.Detail ?? "no detail"})");

            // -------------------------------------------------------- RUN
            var options = new PartyFollowAssistScenario.PartyOptions
            {
                FollowDistance = root.TryGetProperty("followDistance", out var fdEl) && fdEl.TryGetSingle(out var fd) ? fd : 3f,
                MoveSpeed = root.TryGetProperty("moveSpeed", out var msEl) && msEl.TryGetSingle(out var ms) ? ms : 5f,
                MoveTimeout = TimeSpan.FromSeconds(
                    root.TryGetProperty("moveTimeoutSeconds", out var mtEl) && mtEl.TryGetInt32(out var mt) && mt > 0 ? mt : 30)
            };

            var result = PartyFollowAssistScenario.Run(leaderChar, memberChar, options);

            // Party truth AFTER the run (the scenario never disbands).
            var finalTeam = TeamManager.Instance.GetActiveTeamByUnit(leaderChar.Id);

            var payload = new
            {
                template = result.Template,
                passed = result.Passed,
                failStage = result.FailStage,
                failure = result.Failure?.ToString(),
                failReason = result.FailReason,
                gates = result.Gates,
                stages = result.Stages,
                criteria = result.Criteria,
                traceRecords = result.TraceRecords.Select(r => r.ToJson()).ToList(),
                actorRequests = result.ActorRequests,
                rigNotes = result.RigNotes,
                trace = result.TraceRecords
                    .Select(r => JsonSerializer.Deserialize<JsonElement>(r.ToJson()))
                    .ToArray(),
                evidence = result.Evidence(),
                characters = new[]
                {
                    // id = characters.id (the TeamManager.OwnerId key); objId =
                    // the live world object id. They are different namespaces.
                    new { name = leaderChar.Name, level = leaderChar.Level, objId = leaderChar.ObjId, id = leaderChar.Id },
                    new { name = memberChar.Name, level = memberChar.Level, objId = memberChar.ObjId, id = memberChar.Id }
                },
                party = new
                {
                    teamId = finalTeam?.Id ?? 0u,
                    ownerId = finalTeam?.OwnerId ?? 0u
                }
            };
            Logger.Info("scenario '{Template}': {Verdict} on '{Leader}'/'{Member}' ({Stage}{Failure})",
                PartyFollowAssistScenario.ScenarioName, result.Passed ? "PASS" : "FAIL",
                leaderName, memberName, result.FailStage, result.Failure is { } f ? $", {f}" : "");
            return Ok(payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': run crashed on '{Leader}'/'{Member}'",
                PartyFollowAssistScenario.ScenarioName, leaderName, memberName);
            return Err($"scenario: run crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeactivateParty(PartyFollowAssistScenario.ScenarioName, sessions);
        }
    }

    /// <summary>
    /// Multi-actor execution seam (ROADMAP M7 — the party spike): runs
    /// <see cref="PartySpikeScenario"/> on THREE real provisioned bots
    /// (leader + 2 members) against ONE elite group encounter through the
    /// live E2E bridge. Request (all fields optional except "template"):
    ///
    ///   {"cmd":"scenario","template":"m7-party-spike",
    ///    "leader":"m7ps-leader","member1":"m7ps-m1","member2":"m7ps-m2",
    ///    "npc":1870,
    ///    "followDistance":3.0,"moveSpeed":5.0,"moveTimeoutSeconds":30,
    ///    "sustainThreshold":0.35,"resumeThreshold":0.8,"maxHuntRounds":150}
    ///
    /// Flow: shared <see cref="ProvisionBotParty"/> machinery (wipe →
    /// provision → convergence → Spawn for ALL THREE bots) → stock the heal
    /// potion (item 8518, verified direct-heal HealEffect row) into every
    /// bag via the ordinary <see cref="PlayerBotController.StockInventory"/>
    /// acquisition path → form the party through the CONTRACT (invite +
    /// accept per member; the per-target invitation keys make members #2/#3
    /// a verbatim reuse of member #1's flow) → position the leader at the
    /// elite's spawner via <see cref="LiveScenarioWorldAdapter"/> and offset
    /// both members ~+20 m so RALLY legs actually drive → run the scenario
    /// through its default overload (LivePartySpikeRuntime) → return the
    /// same structured payload envelope as the follow/assist runner with a
    /// 3-entry `characters` array and `party` info from TeamManager. All
    /// three characters are deactivated afterwards.
    /// </summary>
    private string HandlePartySpikeScenario(JsonElement root)
    {
        string ReadName(string field, string fallback)
            => (root.TryGetProperty(field, out var el) && el.GetString() is { Length: > 0 } value
                ? value
                : fallback).NormalizeName();

        var leaderName = ReadName("leader", "m7ps-leader");
        var member1Name = ReadName("member1", "m7ps-m1");
        var member2Name = ReadName("member2", "m7ps-m2");
        var npcTemplateId = GetUInt(root, "npc");
        if (npcTemplateId == 0)
            npcTemplateId = PartySpikeScenario.DefaultEliteNpcTemplateId; // level-13 Strong elite — the M7 group encounter

        const byte level = 20; // clears potion 8518's level gate; combat balance is the encounter's job

        var provisionError = ProvisionBotParty(
            PartySpikeScenario.ScenarioName, [leaderName, member1Name, member2Name], level, out var sessions);
        if (provisionError != null)
            return provisionError;

        var characters = sessions.Select(s => s.Character).ToArray();
        var leaderChar = characters[0];

        try
        {
            // ------------------------------------------------------- SUPPLIES
            // Stock the verified direct-heal potion into EVERY bag through
            // the ordinary quest-supply acquisition path — each bot runs its
            // OWN sustain loop (aggro splits across attackers).
            foreach (var character in characters)
                new PlayerBotController(character).StockInventory(
                    new PartySpikeScenario.PartySpikeOptions().HealItemTemplateId,
                    PartySpikeScenario.DefaultHealPotionCount);

            // ------------------------------------------------------ PARTY FORM
            // The CONTRACT path, per member — AskToJoin/ReplyToJoinTeam key
            // invitations per target, so members #2/#3 are a verbatim reuse
            // of member #1's flow.
            foreach (var member in characters.Skip(1))
            {
                var invite = new GameplayActor(leaderChar).PartyInvite(member.ObjId);
                if (invite.State != ActorLifecycleState.Completed)
                    return Err($"scenario: party invite for '{member.Name}' failed ({invite.State}: {invite.Detail ?? "no detail"})");
                var accept = new GameplayActor(member).PartyAccept();
                if (accept.State != ActorLifecycleState.Completed)
                    return Err($"scenario: party accept from '{member.Name}' failed ({accept.State}: {accept.Detail ?? "no detail"})");
            }

            var team = TeamManager.Instance.GetActiveTeamByUnit(leaderChar.Id);
            if (team == null || !team.IsParty || team.OwnerId != leaderChar.Id ||
                characters.Any(c => !team.IsMember(c.Id)))
                return Err($"scenario: party did not form (team {(team == null ? "<null>" : team.Id.ToString())}, " +
                           $"owner {team?.OwnerId.ToString() ?? "<null>"}, expected owner {leaderChar.Id})");

            // -------------------------------------------------------- POSITION
            // Leader at the elite's spawner (the adapter resolves/spawns the
            // NPC through the NORMAL spawn path); members offset ~+20 m so
            // distanceBefore > FollowDistance and the RALLY legs drive.
            var npcObjId = new LiveScenarioWorldAdapter(leaderChar).ResolveNpcObjId(npcTemplateId);
            if (npcObjId == 0)
                return Err($"scenario: could not resolve a live objId for NPC template {npcTemplateId}");

            var offsets = new[]
            {
                new System.Numerics.Vector3(20f, 0f, 0f),
                new System.Numerics.Vector3(0f, 20f, 0f)
            };
            for (var i = 1; i < characters.Length; i++)
            {
                TeleportWithRegionSync(characters[i],
                    leaderChar.Transform.Local.Position + offsets[(i - 1) % offsets.Length],
                    leaderChar.Transform.ZoneId);
            }

            // ----------------------------------------------------------- RUN
            var options = new PartySpikeScenario.PartySpikeOptions
            {
                EliteNpcTemplateId = npcTemplateId,
                FollowDistance = root.TryGetProperty("followDistance", out var fdEl) && fdEl.TryGetSingle(out var fd) ? fd : 3f,
                MoveSpeed = root.TryGetProperty("moveSpeed", out var msEl) && msEl.TryGetSingle(out var ms) ? ms : 5f,
                MoveTimeout = TimeSpan.FromSeconds(
                    root.TryGetProperty("moveTimeoutSeconds", out var mtEl) && mtEl.TryGetInt32(out var mt) && mt > 0 ? mt : 30),
                SustainThreshold = root.TryGetProperty("sustainThreshold", out var stEl) && stEl.TryGetSingle(out var st) ? st : 0.35f,
                ResumeThreshold = root.TryGetProperty("resumeThreshold", out var rtEl) && rtEl.TryGetSingle(out var rt) ? rt : 0.8f,
                MaxHuntRounds = root.TryGetProperty("maxHuntRounds", out var mrEl) && mrEl.TryGetInt32(out var mr) && mr > 0 ? mr : 150
            };

            var result = PartySpikeScenario.Run(characters, options);

            // Party truth AFTER the run (the scenario never disbands).
            var finalTeam = TeamManager.Instance.GetActiveTeamByUnit(leaderChar.Id);

            var payload = new
            {
                template = result.Template,
                passed = result.Passed,
                failStage = result.FailStage,
                failure = result.Failure?.ToString(),
                failReason = result.FailReason,
                gates = result.Gates,
                stages = result.Stages,
                criteria = result.Criteria,
                traceRecords = result.TraceRecords.Select(r => r.ToJson()).ToList(),
                actorRequests = result.ActorRequests,
                rigNotes = result.RigNotes,
                trace = result.TraceRecords
                    .Select(r => JsonSerializer.Deserialize<JsonElement>(r.ToJson()))
                    .ToArray(),
                evidence = result.Evidence(),
                characters = characters.Select(c =>
                    // id = characters.id (the TeamManager.OwnerId key); objId =
                    // the live world object id. They are different namespaces.
                    new { name = c.Name, level = c.Level, objId = c.ObjId, id = c.Id }).ToArray(),
                party = new
                {
                    teamId = finalTeam?.Id ?? 0u,
                    ownerId = finalTeam?.OwnerId ?? 0u
                }
            };
            Logger.Info("scenario '{Template}': {Verdict} on '{Leader}'+'{M1}'/'{M2}' ({Stage}{Failure})",
                PartySpikeScenario.ScenarioName, result.Passed ? "PASS" : "FAIL",
                leaderName, member1Name, member2Name, result.FailStage, result.Failure is { } f ? $", {f}" : "");
            return Ok(payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': run crashed on '{Leader}'/'{M1}'/'{M2}'",
                PartySpikeScenario.ScenarioName, leaderName, member1Name, member2Name);
            return Err($"scenario: run crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeactivateParty(PartySpikeScenario.ScenarioName, sessions);
        }
    }

    /// <summary>
    /// M8 economy-loop v0 execution seam: runs
    /// <see cref="EconomyDayCycleScenario"/> on ONE real provisioned bot
    /// through the live E2E bridge. Request (all fields optional except
    /// "template"):
    ///
    ///   {"cmd":"scenario","template":"m8-economy-cycle-v0",
    ///    "bot":"m8economy","fresh":true,"cycles":1,
    ///    "deposit":"proceeds","fixedAmount":0,
    ///    "hauler":false}
    ///
    /// "hauler": true extends each cycle with the trade-pack + vehicle leg
    /// (PACK-CRAFT → SUMMON → BOARD → LOAD → DRIVE → UNBOARD → UNLOAD →
    /// SELL-GOLD at the specialty gold trader; payout asserted by formula
    /// against the created mail, labor −60/pack).
    ///
    /// Flow: fresh-wipe the bot row → plain headless provisioning → run the
    /// day cycle(s) through its default overload (LiveCyclePump) → return
    /// the same structured payload envelope as the single-bot runner, with a
    /// one-entry `characters` array and a `ledger` block (observable
    /// character state captured BEFORE deactivation: money / bank / labor /
    /// per-template bag and bank counts) — the pre-restart expectation the
    /// E2E restart-reconciliation test asserts against MySQL. The character
    /// is deactivated afterwards.
    /// </summary>
    private string HandleEconomyDayCycleScenario(JsonElement root)
    {
        var botName = (root.TryGetProperty("bot", out var b) && b.GetString() is { Length: > 0 } bn
            ? bn
            : "m8economy").NormalizeName();
        var username = BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant();

        var options = new EconomyDayCycleScenario.CycleOptions
        {
            Cycles = root.TryGetProperty("cycles", out var cyEl) && cyEl.TryGetInt32(out var cy) && cy > 0 ? cy : 1,
            Mode = root.TryGetProperty("deposit", out var depEl) &&
                   Enum.TryParse<EconomyDayCycleScenario.DepositMode>(depEl.GetString(), ignoreCase: true, out var mode)
                ? mode
                : EconomyDayCycleScenario.DepositMode.Proceeds,
            FixedDepositAmount = root.TryGetProperty("fixedAmount", out var faEl) && faEl.TryGetInt64(out var fa) ? fa : 0,
            Hauler = root.TryGetProperty("hauler", out var haulerEl) && haulerEl.GetBoolean()
        };

        // Fresh-rig contract (the template runner's semantics): wipe prior
        // rows unless the caller opts into adoption.
        var fresh = !root.TryGetProperty("fresh", out var freshEl) || freshEl.GetBoolean();
        if (fresh)
        {
            try
            {
                EnsureFreshBotRow(botName, username);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "scenario '{Template}': fresh wipe failed for '{Bot}' — continuing with adoption semantics",
                    EconomyDayCycleScenario.ScenarioName, botName);
            }
        }

        HeadlessSession session;
        try
        {
            session = HeadlessSession.Provision(username, botName, Race.Nuian, Gender.Male, 10);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': provisioning failed for '{Bot}'",
                EconomyDayCycleScenario.ScenarioName, botName);
            return Err($"scenario: provisioning failed for '{botName}': {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var result = EconomyDayCycleScenario.Run(session.Character,
                new LiveScenarioWorldAdapter(session.Character), options);

            // Observable economy state AFTER the run, BEFORE deactivation —
            // the ledger snapshot the restart test reconciles against MySQL.
            var ledger = new
            {
                characterId = session.Character.Id,
                money = session.Character.Money,
                bankMoney = session.Character.Money2,
                laborPower = session.Character.LaborPower,
                bagItems = BagTemplateCounts(session.Character),
                bankItems = BankTemplateCounts(session.Character)
            };

            var payload = new
            {
                template = result.Template,
                passed = result.Passed,
                failStage = result.FailStage,
                failure = result.Failure?.ToString(),
                failReason = result.FailReason,
                gates = result.Gates,
                stages = result.Stages,
                criteria = result.Criteria,
                traceRecords = result.TraceRecords.Select(r => r.ToJson()).ToList(),
                actorRequests = result.ActorRequests,
                rigNotes = result.RigNotes,
                trace = result.TraceRecords
                    .Select(r => JsonSerializer.Deserialize<JsonElement>(r.ToJson()))
                    .ToArray(),
                evidence = result.Evidence(),
                characters = new[]
                {
                    // id = characters.id; objId = the live world object id.
                    new { name = session.Character.Name, level = session.Character.Level,
                          objId = session.Character.ObjId, id = session.Character.Id }
                },
                ledger
            };
            Logger.Info("scenario '{Template}': {Verdict} on '{Bot}' ({Stage}{Failure})",
                EconomyDayCycleScenario.ScenarioName, result.Passed ? "PASS" : "FAIL", botName,
                result.FailStage, result.Failure is { } f ? $", {f}" : "");
            return Ok(payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': run crashed on '{Bot}'",
                EconomyDayCycleScenario.ScenarioName, botName);
            return Err($"scenario: run crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                CharacterLifecycleService.Instance.Deactivate(session.Character, CharacterLifecycleReason.Logout);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "scenario '{Template}': deactivate failed for '{Bot}'",
                    EconomyDayCycleScenario.ScenarioName, botName);
            }
        }
    }

    /// <summary>
    /// M8 C5-slice-2 execution seam: runs <see cref="VillageDayCycle"/> (the
    /// slice-1 composer, unchanged) on TWO real provisioned bots through the
    /// live E2E bridge. Request (all fields optional except "template"):
    ///
    ///   {"cmd":"scenario","template":"m8-village-day-s1",
    ///    "farmer":"m8vfarmer","crafter":"m8vcrafter","cycleId":"m8v1-c0"}
    ///
    /// Flow: fresh-wipe both bot rows (the party provisioner always wipes —
    /// the nearest non-crop doodad bench → record profession + home +
    /// schedule (anchors, no phase yet) → run the half-day through the
    /// slice-1 composer → stamp lastPhase per villager → enqueue each
    /// villager's terminal trace records into <see cref="PlayerBotAuditSink"/>
    /// (the B4 flush path the SaveManager tick persists to playerbot_audit;
    /// direct GameplayActor legs never pass the B1 queue) → return the
    /// structured payload with a per-bot `ledger` block (observable state
    /// BEFORE deactivation) the restart test reconciles against MySQL. Both
    /// characters are deactivated afterwards.
    /// </summary>
    private string HandleVillageDayCycleScenario(JsonElement root)
    {
        var farmerName = (root.TryGetProperty("farmer", out var farmerEl) && farmerEl.GetString() is { Length: > 0 } farmerRaw
            ? farmerRaw
            : "m8vfarmer").NormalizeName();
        var crafterName = (root.TryGetProperty("crafter", out var crafterEl) && crafterEl.GetString() is { Length: > 0 } crafterRaw
            ? crafterRaw
            : "m8vcrafter").NormalizeName();
        var cycleId = root.TryGetProperty("cycleId", out var cy) && cy.GetString() is { Length: > 0 } cs
            ? cs
            : "m8v1-c0";

        var provisionError = ProvisionBotParty(VillageDayCycle.ScenarioName, [farmerName, crafterName], 10, out var sessions);
        if (provisionError != null)
            return provisionError;

        var farmerSession = sessions[0];
        var crafterSession = sessions[1];

        try
        {
            var farmerChar = farmerSession.Character;
            var crafterChar = crafterSession.Character;
            farmerChar.Level = 10;
            crafterChar.Level = 10;
            farmerChar.Money = EconomyDayCycleScenario.DefaultSeedMoney;
            crafterChar.Money = EconomyDayCycleScenario.DefaultSeedMoney;
            farmerChar.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;
            crafterChar.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;

            var pump = new EconomyDayCycleScenario.LiveCyclePump();
            var farmerActor = new GameplayActor(farmerChar);
            var crafterActor = new GameplayActor(crafterChar);
            var anchors = BotDailyAnchors.Template;

            // ---- Farmer prep: one mature owned crop at the farmer's feet.
            new PlayerBotController(farmerChar).StockInventory(EconomyDayCycleScenario.PotatoSeedItemId, 3);
            var farmPos = farmerChar.Transform.World.Position;
            var prep = farmerActor.Plant(EconomyDayCycleScenario.PotatoSeedItemId, farmPos,
                idempotencyKey: $"{cycleId}-prep-plant");
            prep = pump.Drive(farmerActor, prep, TimeSpan.FromSeconds(60));
            if (prep.State != ActorLifecycleState.Completed)
                return Err($"scenario: prep plant failed ({prep.State}: {prep.Detail ?? "no detail"})");
            var cropObjId = ReadVillageObjId(prep.Result);
            if (cropObjId == 0)
                return Err("scenario: prep plant completed without a crop ObjId");
            if (!pump.WaitForCropMaturity(farmerChar, cropObjId, TimeSpan.FromSeconds(180)))
                return Err($"scenario: prep crop {cropObjId} not harvestable within the maturity window");

            // ---- Crafter prep: the recipe rows into the bank through REAL deposit legs.
            var crafterCtl = new PlayerBotController(crafterChar);
            crafterCtl.StockInventory(EconomyDayCycleScenario.PotatoItemId, 1);
            crafterCtl.StockInventory(EconomyDayCycleScenario.WaterItemId, 1);
            foreach (var material in new[] { EconomyDayCycleScenario.PotatoItemId, EconomyDayCycleScenario.WaterItemId })
            {
                var deposit = crafterActor.DepositItem(material, idempotencyKey: $"{cycleId}-prep-deposit-{material}");
                deposit = pump.Drive(crafterActor, deposit, TimeSpan.FromSeconds(60));
                if (deposit.State != ActorLifecycleState.Completed)
                    return Err($"scenario: prep deposit of {material} failed ({deposit.State}: {deposit.Detail ?? "no detail"})");
            }

            // Nearest non-crop doodad bench (the economy ReqDoodadId=0
            // convention): the farmer's crop must be excluded — the farmer
            // leg harvests (deletes) it before the crafter leg resolves.
            var benchObjId = ResolveVillageBench(crafterChar, cropObjId);
            if (benchObjId == 0)
                return Err("scenario: no craft bench doodad in range");
            var bench = crafterChar.ParentWorld?.GetDoodad(benchObjId);
            var benchPos = bench?.Transform.World.Position ?? crafterChar.Transform.World.Position;
            TeleportWithRegionSync(crafterChar, benchPos + new System.Numerics.Vector3(2f, 0f, 0f));

            // ---- Metadata: profession + home + schedule (anchors, no phase yet).
            var store = PlayerBotMetadataStore.Instance;
            store.RecordProfession(farmerChar.Id, nameof(VillageDayCycle.VillageProfession.Farmer).ToLowerInvariant());
            store.RecordProfession(crafterChar.Id, nameof(VillageDayCycle.VillageProfession.Crafter).ToLowerInvariant());
            RecordVillageHome(store, farmerChar);
            RecordVillageHome(store, crafterChar);
            store.RecordSchedule(farmerChar.Id, VillageScheduleJson(farmerChar, cycleId, anchors, null));
            store.RecordSchedule(crafterChar.Id, VillageScheduleJson(crafterChar, cycleId, anchors, null));

            // ---- The half-day through the slice-1 composer, unchanged.
            var result = VillageDayCycle.Run(
            [
                new VillageDayCycle.VillagerSpec
                {
                    Name = farmerName,
                    Profession = VillageDayCycle.VillageProfession.Farmer,
                    Actor = farmerActor,
                    Anchors = anchors,
                    CropObjId = cropObjId,
                    FarmerSeedItemId = FarmerCycleScenario.ApprovedSeedItemId
                },
                new VillageDayCycle.VillagerSpec
                {
                    Name = crafterName,
                    Profession = VillageDayCycle.VillageProfession.Crafter,
                    Actor = crafterActor,
                    Anchors = anchors,
                    CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
                    {
                        CycleId = $"{cycleId}-{crafterName}-craft",
                        CraftId = EconomyDayCycleScenario.BoiledPotatoCraftId,
                        BenchObjId = benchObjId
                    },
                    CrafterPump = new VillageLivePump()
                }
            ], new VillageDayCycle.VillageDayOptions { CycleId = cycleId });

            var farmerEntry = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Farmer);
            var crafterEntry = result.Report.Villagers.Single(v => v.Profession == VillageDayCycle.VillageProfession.Crafter);
            BotSchedulePhase? farmerLast = farmerEntry.PhasesVisited.Count > 0 ? farmerEntry.PhasesVisited[^1] : null;
            BotSchedulePhase? crafterLast = crafterEntry.PhasesVisited.Count > 0 ? crafterEntry.PhasesVisited[^1] : null;

            // Stamp lastPhase per villager (write-through; hard-kill safe).
            store.RecordSchedule(farmerChar.Id, VillageScheduleJson(farmerChar, cycleId, anchors, farmerLast));
            store.RecordSchedule(crafterChar.Id, VillageScheduleJson(crafterChar, cycleId, anchors, crafterLast));

            // Audit: each villager's terminal trace records into the B4 sink
            // under its own character id (the same flush path the B1 queue
            // uses — direct actor legs never pass that queue).
            var sink = PlayerBotAuditSink.Instance;
            foreach (var record in farmerEntry.FarmerResult?.TraceRecords ?? Enumerable.Empty<ActorAuditRecord>())
                sink.Enqueue(farmerChar.Id, record.ToJson());
            foreach (var record in crafterEntry.CrafterResult?.TraceRecords ?? Enumerable.Empty<ActorAuditRecord>())
                sink.Enqueue(crafterChar.Id, record.ToJson());

            var villagers = new[]
            {
                VillageEvidence(farmerName, "farmer", farmerChar, farmerEntry, farmerLast,
                    farmerEntry.FarmerResult?.TraceRecords.Count ?? 0),
                VillageEvidence(crafterName, "crafter", crafterChar, crafterEntry, crafterLast,
                    crafterEntry.CrafterResult?.TraceRecords.Count ?? 0)
            };
            var payload = new
            {
                template = result.Scenario,
                passed = result.Passed,
                failStage = result.FailStage,
                failure = result.Failure?.ToString(),
                failReason = result.FailReason,
                stages = result.Stages,
                criteria = result.Criteria,
                traceRecords = result.TraceRecords.Select(r => r.ToJson()).ToList(),
                trace = result.TraceRecords
                    .Select(r => JsonSerializer.Deserialize<JsonElement>(r.ToJson()))
                    .ToArray(),
                evidence = string.Join("; ", result.Criteria.Select(c => $"{c.Name}={(c.Passed ? "pass" : "FAIL")}")) +
                    $" | {farmerName}: work={farmerEntry.WorkTicks} {farmerEntry.FarmerResult?.Report?.HarvestOutcome}" +
                    $" | {crafterName}: work={crafterEntry.WorkTicks} labor={crafterEntry.CrafterResult?.LaborCharged}",
                villagers,
                characters = new[]
                {
                    new { name = farmerChar.Name, level = farmerChar.Level, objId = farmerChar.ObjId, id = farmerChar.Id },
                    new { name = crafterChar.Name, level = crafterChar.Level, objId = crafterChar.ObjId, id = crafterChar.Id }
                },
                ledgers = new[]
                {
                    VillageLedger(farmerChar),
                    VillageLedger(crafterChar)
                }
            };
            Logger.Info("scenario '{Template}': {Verdict} on '{Farmer}'+'{Crafter}' ({Stage}{Failure})",
                VillageDayCycle.ScenarioName, result.Passed ? "PASS" : "FAIL", farmerName, crafterName,
                result.FailStage, result.Failure is { } f ? $", {f}" : "");
            return Ok(payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': run crashed on '{Farmer}'+'{Crafter}'",
                VillageDayCycle.ScenarioName, farmerName, crafterName);
            return Err($"scenario: run crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeactivateParty(VillageDayCycle.ScenarioName, sessions);
        }
    }

    /// <summary>
    /// M8 C5-soak execution seam: runs <see cref="VillageFullDayCycle"/> (the
    /// slice-4 composer, unchanged) on TWO real provisioned bots through the
    /// live E2E bridge. Request (all fields optional except "template"):
    ///
    ///   {"cmd":"scenario","template":"m8-village-fullday-s4",
    ///    "farmer":"m8vffarmer","crafter":"m8vfcrafter","cycleId":"m8v5-c0"}
    ///
    /// Flow: day prep identical to the half-day seam (mature owned crop +
    /// banked craft mats + bench) PLUS evening prep (banked saleable output
    /// per bot through real deposit legs + the general merchant resolve —
    /// villagers stay where the day left them: Sell has no range gate and
    /// home is each villager's own position) → record profession + home +
    /// schedule (anchors, no phase yet) → run the full-day composer →
    /// stamp lastPhase per villager → enqueue each actor's NEW audit slice
    /// (the run's records only — prep legs stay out) into
    /// <see cref="PlayerBotAuditSink"/> (the B4 flush path the SaveManager
    /// tick persists to playerbot_audit) → return the structured payload
    /// with per-bot `ledger` + evening evidence the restart test reconciles
    /// against MySQL. Both characters are deactivated afterwards.
    /// </summary>
    private string HandleVillageFullDayScenario(JsonElement root)
    {
        var farmerName = (root.TryGetProperty("farmer", out var farmerEl) && farmerEl.GetString() is { Length: > 0 } farmerRaw
            ? farmerRaw
            : "m8vffarmer").NormalizeName();
        var crafterName = (root.TryGetProperty("crafter", out var crafterEl) && crafterEl.GetString() is { Length: > 0 } crafterRaw
            ? crafterRaw
            : "m8vfcrafter").NormalizeName();
        var cycleId = root.TryGetProperty("cycleId", out var cy) && cy.GetString() is { Length: > 0 } cs
            ? cs
            : "m8v5-c0";

        var provisionError = ProvisionBotParty(VillageFullDayCycle.ScenarioName, [farmerName, crafterName], 10, out var sessions);
        if (provisionError != null)
            return provisionError;

        var farmerSession = sessions[0];
        var crafterSession = sessions[1];
        try
        {
            var farmerChar = farmerSession.Character;
            var crafterChar = crafterSession.Character;
            farmerChar.Level = 10;
            crafterChar.Level = 10;
            farmerChar.Money = EconomyDayCycleScenario.DefaultSeedMoney;
            crafterChar.Money = EconomyDayCycleScenario.DefaultSeedMoney;
            farmerChar.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;
            crafterChar.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;

            var pump = new EconomyDayCycleScenario.LiveCyclePump();
            var farmerActor = new GameplayActor(farmerChar);
            var crafterActor = new GameplayActor(crafterChar);
            var anchors = BotDailyAnchors.Template;

            // ---- Merchant resolve FIRST: the spawner path teleports the
            // adapter's character to the spawner (proximity spawn), so this
            // must run before the farmer plants at its feet and before the
            // crafter moves to the bench. The ObjId stays valid (same world).
            var merchantObjId = new LiveScenarioWorldAdapter(farmerChar)
                .ResolveNpcObjId(EconomyDayCycleScenario.GeneralMerchantNpcTemplateId);
            if (merchantObjId == 0)
                return Err($"scenario: general merchant {EconomyDayCycleScenario.GeneralMerchantNpcTemplateId} unresolvable in world");

            // ---- Farmer prep: one mature owned crop at the farmer's feet. ----
            new PlayerBotController(farmerChar).StockInventory(EconomyDayCycleScenario.PotatoSeedItemId, 3);
            var farmPos = farmerChar.Transform.World.Position;
            var prep = farmerActor.Plant(EconomyDayCycleScenario.PotatoSeedItemId, farmPos,
                idempotencyKey: $"{cycleId}-prep-plant");
            prep = pump.Drive(farmerActor, prep, TimeSpan.FromSeconds(60));
            if (prep.State != ActorLifecycleState.Completed)
                return Err($"scenario: prep plant failed ({prep.State}: {prep.Detail ?? "no detail"})");
            var cropObjId = ReadVillageObjId(prep.Result);
            if (cropObjId == 0)
                return Err("scenario: prep plant completed without a crop ObjId");
            if (!pump.WaitForCropMaturity(farmerChar, cropObjId, TimeSpan.FromSeconds(180)))
                return Err($"scenario: prep crop {cropObjId} not harvestable within the maturity window");
            var crafterCtl = new PlayerBotController(crafterChar);
            crafterCtl.StockInventory(EconomyDayCycleScenario.PotatoItemId, 1);
            crafterCtl.StockInventory(EconomyDayCycleScenario.WaterItemId, 1);
            foreach (var material in new[] { EconomyDayCycleScenario.PotatoItemId, EconomyDayCycleScenario.WaterItemId })
            {
                var deposit = crafterActor.DepositItem(material, idempotencyKey: $"{cycleId}-prep-deposit-{material}");
                deposit = pump.Drive(crafterActor, deposit, TimeSpan.FromSeconds(60));
                if (deposit.State != ActorLifecycleState.Completed)
                    return Err($"scenario: prep deposit of {material} failed ({deposit.State}: {deposit.Detail ?? "no detail"})");
            }

            var benchObjId = ResolveVillageBench(crafterChar, cropObjId);
            if (benchObjId == 0)
                return Err("scenario: no craft bench doodad in range");
            var bench = crafterChar.ParentWorld?.GetDoodad(benchObjId);
            var benchPos = bench?.Transform.World.Position ?? crafterChar.Transform.World.Position;
            TeleportWithRegionSync(crafterChar, benchPos + new System.Numerics.Vector3(2f, 0f, 0f));

            // ---- Evening prep: banked saleable output per bot (real deposit
            // legs). The merchant was resolved BEFORE the day prep (its
            // spawner path teleports). The evening sells every banked unit of
            // the output template — the day's own craft product (same
            // template) joins the stocked units in one sale.
            foreach (var (prepChar, prepActor) in new[] { (farmerChar, farmerActor), (crafterChar, crafterActor) })
            {
                new PlayerBotController(prepChar).StockInventory(EconomyDayCycleScenario.BoiledPotatoItemId, 3);
                var eveningStock = prepActor.DepositItem(EconomyDayCycleScenario.BoiledPotatoItemId,
                    idempotencyKey: $"{cycleId}-prep-evening-{prepChar.Name}");
                eveningStock = pump.Drive(prepActor, eveningStock, TimeSpan.FromSeconds(60));
                if (eveningStock.State != ActorLifecycleState.Completed)
                    return Err($"scenario: evening stock deposit failed for {prepChar.Name} ({eveningStock.State}: {eveningStock.Detail ?? "no detail"})");
            }

            // ---- Metadata: profession + home + schedule (anchors, no phase yet). ----
            var store = PlayerBotMetadataStore.Instance;
            store.RecordProfession(farmerChar.Id, nameof(VillageDayCycle.VillageProfession.Farmer).ToLowerInvariant());
            store.RecordProfession(crafterChar.Id, nameof(VillageDayCycle.VillageProfession.Crafter).ToLowerInvariant());
            RecordVillageHome(store, farmerChar);
            RecordVillageHome(store, crafterChar);
            store.RecordSchedule(farmerChar.Id, VillageScheduleJson(farmerChar, cycleId, anchors, null));
            store.RecordSchedule(crafterChar.Id, VillageScheduleJson(crafterChar, cycleId, anchors, null));

            var farmerHome = farmerChar.Transform.World.Position;
            var crafterHome = crafterChar.Transform.World.Position;
            var farmerAuditBase = farmerActor.AuditTrace.Count;
            var crafterAuditBase = crafterActor.AuditTrace.Count;

            // ---- The full day through the slice-4 composer, unchanged. ----
            var result = VillageFullDayCycle.Run(
            [
                new VillageFullDayCycle.FullDayVillager
                {
                    Name = farmerName,
                    DaySpec = new VillageDayCycle.VillagerSpec
                    {
                        Name = farmerName,
                        Profession = VillageDayCycle.VillageProfession.Farmer,
                        Actor = farmerActor,
                        Anchors = anchors,
                        CropObjId = cropObjId,
                        FarmerSeedItemId = FarmerCycleScenario.ApprovedSeedItemId
                    },
                    EveningOutputTemplateId = EconomyDayCycleScenario.BoiledPotatoItemId,
                    EveningMerchantObjId = merchantObjId,
                    EveningHome = farmerHome
                },
                new VillageFullDayCycle.FullDayVillager
                {
                    Name = crafterName,
                    DaySpec = new VillageDayCycle.VillagerSpec
                    {
                        Name = crafterName,
                        Profession = VillageDayCycle.VillageProfession.Crafter,
                        Actor = crafterActor,
                        Anchors = anchors,
                        CrafterOptions = new CrafterWorkstationCycle.CrafterWorkstationOptions
                        {
                            CycleId = $"{cycleId}-{crafterName}-craft",
                            CraftId = EconomyDayCycleScenario.BoiledPotatoCraftId,
                            BenchObjId = benchObjId
                        },
                        CrafterPump = new VillageLivePump()
                    },
                    EveningOutputTemplateId = EconomyDayCycleScenario.BoiledPotatoItemId,
                    EveningMerchantObjId = merchantObjId,
                    EveningHome = crafterHome
                }
            ], new VillageFullDayCycle.VillageFullDayOptions { CycleId = cycleId }, new VillageLiveMovePump());

            var farmerEntry = result.Report.Villagers.SingleOrDefault(v => v.Profession == VillageDayCycle.VillageProfession.Farmer);
            var crafterEntry = result.Report.Villagers.SingleOrDefault(v => v.Profession == VillageDayCycle.VillageProfession.Crafter);
            BotSchedulePhase? farmerLast = farmerEntry?.DayEntry?.PhasesVisited.Count > 0 ? farmerEntry.DayEntry.PhasesVisited[^1] : null;
            BotSchedulePhase? crafterLast = crafterEntry?.DayEntry?.PhasesVisited.Count > 0 ? crafterEntry.DayEntry.PhasesVisited[^1] : null;

            // Stamp lastPhase per villager (write-through; hard-kill safe).
            store.RecordSchedule(farmerChar.Id, VillageScheduleJson(farmerChar, cycleId, anchors, farmerLast));
            store.RecordSchedule(crafterChar.Id, VillageScheduleJson(crafterChar, cycleId, anchors, crafterLast));

            // Audit: each actor's NEW slice (the run's records only — prep
            // legs stay out) under its own character id (the same flush path
            // the B1 queue uses).
            var sink = PlayerBotAuditSink.Instance;
            var farmerNew = farmerActor.AuditTrace.Skip(farmerAuditBase).ToList();
            var crafterNew = crafterActor.AuditTrace.Skip(crafterAuditBase).ToList();
            foreach (var record in farmerNew)
                sink.Enqueue(farmerChar.Id, record.ToJson());
            foreach (var record in crafterNew)
                sink.Enqueue(crafterChar.Id, record.ToJson());

            var eveningByName = result.Report.EveningReport != null
                ? result.Report.EveningReport.Villagers.ToDictionary(e => e.Name)
                : new Dictionary<string, VillageEveningCycle.VillagerEveningEntry>();
            var villagers = new[]
            {
                FullDayEvidence(farmerName, "farmer", farmerChar, farmerEntry, farmerLast, farmerNew.Count, eveningByName),
                FullDayEvidence(crafterName, "crafter", crafterChar, crafterEntry, crafterLast, crafterNew.Count, eveningByName)
            };
            var payload = new
            {
                template = result.Scenario,
                passed = result.Passed,
                failStage = result.FailStage,
                failure = result.Failure?.ToString(),
                failReason = result.FailReason,
                stages = result.Stages,
                criteria = result.Criteria,
                traceRecords = result.TraceRecords.Select(r => r.ToJson()).ToList(),
                trace = result.TraceRecords
                    .Select(r => JsonSerializer.Deserialize<JsonElement>(r.ToJson()))
                    .ToArray(),
                evidence = string.Join("; ", result.Criteria.Select(c => $"{c.Name}={(c.Passed ? "pass" : "FAIL")}")) +
                    $" | {farmerName}: work={farmerEntry?.DayEntry?.WorkTicks ?? 0} {farmerEntry?.DayEntry?.FarmerResult?.Report?.HarvestOutcome}" +
                    $" | {crafterName}: work={crafterEntry?.DayEntry?.WorkTicks ?? 0} labor={crafterEntry?.DayEntry?.CrafterResult?.LaborCharged}",
                villagers,
                characters = new[]
                {
                    new { name = farmerChar.Name, level = farmerChar.Level, objId = farmerChar.ObjId, id = farmerChar.Id },
                    new { name = crafterChar.Name, level = crafterChar.Level, objId = crafterChar.ObjId, id = crafterChar.Id }
                },
                ledgers = new[]
                {
                    VillageLedger(farmerChar),
                    VillageLedger(crafterChar)
                }
            };
            Logger.Info("scenario '{Template}': {Verdict} on '{Farmer}'+'{Crafter}' ({Stage}{Failure})",
                VillageFullDayCycle.ScenarioName, result.Passed ? "PASS" : "FAIL", farmerName, crafterName,
                result.FailStage, result.Failure is { } f ? $", {f}" : "");
            return Ok(payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scenario '{Template}': run crashed on '{Farmer}'+'{Crafter}'",
                VillageFullDayCycle.ScenarioName, farmerName, crafterName);
            return Err($"scenario: run crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeactivateParty(VillageFullDayCycle.ScenarioName, sessions);
        }
    }

    /// <summary>One full-day villager's evidence block for the restart-equality test.</summary>
    private static object FullDayEvidence(string name, string profession, Character character,
        VillageFullDayCycle.VillagerFullDayEntry? entry, BotSchedulePhase? lastPhase, int traceCount,
        IReadOnlyDictionary<string, VillageEveningCycle.VillagerEveningEntry> evening)
    {
        evening.TryGetValue(name, out var eveningEntry);
        return new
        {
            name,
            profession,
            characterId = character.Id,
            phasesVisited = entry?.DayEntry?.PhasesVisited.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>(),
            lastPhase = lastPhase?.ToString(),
            workTicks = entry?.DayEntry?.WorkTicks ?? 0,
            nonWorkTicks = entry?.DayEntry?.NonWorkTicks ?? 0,
            laborBefore = entry?.DayEntry?.LaborBefore ?? 0,
            laborAfter = entry?.LaborAfterEvening ?? 0,
            traceCount,
            eveningRefund = eveningEntry?.Refund ?? 0,
            eveningSold = eveningEntry?.SoldCount ?? 0,
            eveningMarketComplete = eveningEntry?.MarketComplete ?? false,
            eveningReturnedHome = eveningEntry?.ReturnedHome ?? false
        };
    }

    /// <summary>One villager's evidence block for the restart-equality test.</summary>
    private static object VillageEvidence(string name, string profession, Character character,
        VillageDayCycle.VillagerDayEntry entry, BotSchedulePhase? lastPhase, int traceCount)
        => new
        {
            name,
            profession,
            characterId = character.Id,
            phasesVisited = entry.PhasesVisited.Select(p => p.ToString()).ToArray(),
            lastPhase = lastPhase?.ToString(),
            workTicks = entry.WorkTicks,
            nonWorkTicks = entry.NonWorkTicks,
            laborBefore = entry.LaborBefore,
            laborAfter = entry.LaborAfter,
            traceCount,
            farmerSeeds = entry.FarmerResult?.Report is { } fr
                ? new { fr.SeedsBeforeHarvest, fr.SeedsAfterHarvest, fr.SeedsAfterReplant }
                : null,
            crafterLabor = entry.CrafterResult?.LaborCharged
        };

    /// <summary>Observable per-bot state BEFORE deactivation — the pre-restart ledger.</summary>
    private static object VillageLedger(Character character)
        => new
        {
            characterId = character.Id,
            money = character.Money,
            bankMoney = character.Money2,
            laborPower = character.LaborPower,
            bagItems = BagTemplateCounts(character),
            bankItems = BankTemplateCounts(character)
        };

    /// <summary>Nearest world doodad excluding one ObjId (the ReqDoodadId=0 craft bench target).</summary>
    private static uint ResolveVillageBench(Character character, uint excludeObjId)
    {
        var world = character.ParentWorld;
        if (world == null)
            return 0;

        uint nearest = 0;
        var distance = float.MaxValue;
        foreach (var doodad in world.GetAllDoodads())
        {
            if (doodad.ObjId == excludeObjId)
                continue;
            var d = System.Numerics.Vector3.Distance(character.Transform.World.Position, doodad.Transform.World.Position);
            if (d < distance)
            {
                distance = d;
                nearest = doodad.ObjId;
            }
        }

        return nearest;
    }

    private static void RecordVillageHome(PlayerBotMetadataStore store, Character character)
    {
        var p = character.Transform.World.Position;
        store.RecordHome(character.Id, character.Transform.WorldId, character.Transform.ZoneId, p.X, p.Y, p.Z);
    }

    /// <summary>Schedule JSON for one villager: roam-loop descriptor + anchors + lastPhase.</summary>
    private static string VillageScheduleJson(Character character, string cycleId,
        BotDailyAnchors anchors, BotSchedulePhase? lastPhase)
    {
        var p = character.Transform.World.Position;
        var baseJson = JsonSerializer.Serialize(new
        {
            kind = "roam-loop",
            home = new[] { p.X, p.Y, p.Z },
            radius = 30.0,
            phase = 0,
            cycleId
        });
        return BotSchedulePayload.WithRuntimeState(baseJson, anchors, lastPhase);
    }

    private static uint ReadVillageObjId(object? result)
        => result switch
        {
            ulong u when u <= uint.MaxValue => (uint)u,
            uint ui => ui,
            int i when i > 0 => (uint)i,
            long l when l > 0 && l <= uint.MaxValue => (uint)l,
            _ => 0u
        };

    /// <summary>Live craft pump: ticks the actor until the in-flight craft reaches terminal.</summary>
    private sealed class VillageLivePump : ICrafterPump
    {
        public ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(100));
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }

            return request;
        }
    }
    /// <summary>Live return-home pump: polls the in-flight move to terminal on real time.</summary>
    private sealed class VillageLiveMovePump : IVillageMovePump
    {
        public ActorRequest Walk(GameplayActor actor, ActorRequest request, TimeSpan budget)
        {
            var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(100));
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }

            return request;
        }
    }

    /// <summary>Per-template bag counts for the ledger block.</summary>
    private static Dictionary<uint, int> BagTemplateCounts(Character character)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var item in character.Inventory.Bag.GetItemsSnapshot())
            counts[item.TemplateId] = counts.GetValueOrDefault(item.TemplateId) + item.Count;
        return counts;
    }

    /// <summary>Per-template bank (warehouse) counts for the ledger block.</summary>
    private static Dictionary<uint, int> BankTemplateCounts(Character character)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var item in character.Inventory.Warehouse.GetItemsSnapshot())
            counts[item.TemplateId] = counts.GetValueOrDefault(item.TemplateId) + item.Count;
        return counts;
    }

    /// <summary>
    /// Shared N-bot provisioning machinery (ROADMAP M7 hardening #1 — the
    /// generalized follow/assist provisioning): fresh-wipes EACH bot row
    /// (warn-and-continue = adoption semantics), provisions N plain
    /// headless sessions in input order, polls until ALL share one world
    /// instance (the PARTY-GATE precondition), then runs the explicit
    /// ActiveChar.Spawn() each headless activation needs. Returns null on
    /// success (<paramref name="sessions"/> filled in input order); on any
    /// failure the partially provisioned bots are deactivated and an error
    /// response string is returned.
    /// </summary>
    private string? ProvisionBotParty(string scenarioName, IReadOnlyList<string> botNames, byte level, out List<HeadlessSession> sessions)
    {
        sessions = [];

        // Fresh-rig hygiene for EVERY bot: prior runs' party memberships /
        // persisted rows would poison the PARTY-GATE.
        foreach (var botName in botNames)
        {
            try
            {
                EnsureFreshBotRow(botName, BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "scenario '{Template}': fresh wipe failed for '{Bot}' — continuing with adoption semantics",
                    scenarioName, botName);
            }
        }

        foreach (var botName in botNames)
        {
            var username = BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant();
            try
            {
                sessions.Add(HeadlessSession.Provision(username, botName, Race.Nuian, Gender.Male, level));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "scenario '{Template}': provisioning failed for '{Bot}'", scenarioName, botName);
                DeactivateParty(scenarioName, sessions);
                sessions = [];
                return Err($"scenario: provisioning failed for '{botName}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        // -------------------------------------------------- WORLD CONVERGE
        // Separately provisioned sessions must share ONE world instance or
        // the scenario's PARTY-GATE can never pass. Poll briefly —
        // embodiment lands each character in the booted world.
        var provisioned = sessions;
        var worldDeadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < worldDeadline &&
               (provisioned.Any(s => s.Character.ParentWorld == null) ||
                !provisioned.All(s => ReferenceEquals(s.Character.ParentWorld, provisioned[0].Character.ParentWorld))))
        {
            Thread.Sleep(250);
        }

        if (provisioned.Count == 0 || provisioned.Any(s => s.Character.ParentWorld == null) ||
            !provisioned.All(s => ReferenceEquals(s.Character.ParentWorld, provisioned[0].Character.ParentWorld)))
        {
            var detail = string.Join("; ", provisioned.Select(s =>
                $"'{s.Character.Name}' world {s.Character.ParentWorld?.Id.ToString() ?? "<none>"}"));
            DeactivateParty(scenarioName, provisioned);
            sessions = [];
            return Err($"scenario: provisioned bots did not converge into a shared world instance within 20s [{detail}]");
        }

        // Headless activation never runs CSNotifyInGamePacket's
        // ActiveChar.Spawn() — the human client's in-game notify is what
        // registers a character into the WorldInstance unit registry
        // (_units). Without it PartyInvite / MoveToUnit can never resolve
        // peers through GetUnit. SendPacket is Connection?-guarded, so the
        // visibility broadcasts are no-ops for headless sessions.
        foreach (var session in sessions)
            session.Character.Spawn();

        return null;
    }

    private void DeactivateParty(string scenarioName, IReadOnlyList<HeadlessSession> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                CharacterLifecycleService.Instance.Deactivate(session.Character, CharacterLifecycleReason.Logout);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "scenario '{Template}': deactivate failed for '{Bot}'",
                    scenarioName, session.Character.Name);
            }
        }
    }

    #endregion

    #region AUCTION-01 E2E seam (persistent headless bots + auction contract ops)

    /// <summary>
    /// Bridge-provisioned sessions that STAY EMBODIED between bridge calls,
    /// keyed by normalized bot name. Unlike the scenario templates (which
    /// provision and deactivate inside one synchronous call), this seam lets
    /// an E2E test split a flow across bridge calls — e.g. kill -9 the game
    /// process mid-flow and re-adopt the bots afterwards (HeadlessSession
    /// adoption semantics: same managed account, same character row).
    /// Additive test-control surface only; every mutation still flows through
    /// the ordinary engine paths (GameplayActor contract actions, AuctionManager).
    /// </summary>
    private static readonly ConcurrentDictionary<string, HeadlessSession> PersistentBotSessions = [];

    private string HandleProvision(JsonElement root)
    {
        var rawName = root.TryGetProperty("bot", out var b) ? b.GetString() : null;
        if (string.IsNullOrWhiteSpace(rawName))
            return Err("provision requires 'bot'");
        var botName = rawName.NormalizeName();
        var username = BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant();
        var level = (byte)GetInt(root, "level", 10);

        var fresh = !root.TryGetProperty("fresh", out var freshEl) || freshEl.GetBoolean();
        if (fresh)
        {
            try
            {
                EnsureFreshBotRow(botName, username);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "provision: fresh wipe failed for '{Bot}' — continuing with adoption semantics", botName);
            }
        }

        // Replace any stale embodied session under this name (a previous
        // provision call whose Deactivate never ran).
        if (PersistentBotSessions.TryRemove(botName, out var stale))
        {
            try
            {
                CharacterLifecycleService.Instance.Deactivate(stale.Character, CharacterLifecycleReason.Logout);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "provision: replacing stale session '{Bot}' failed (best-effort)", botName);
            }
        }

        HeadlessSession session;
        try
        {
            session = HeadlessSession.Provision(username, botName, Race.Nuian, Gender.Male, level);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "provision failed for '{Bot}'", botName);
            return Err($"provision failed for '{botName}': {ex.GetType().Name}: {ex.Message}");
        }

        PersistentBotSessions[botName] = session;
        Logger.Info("provision: '{Bot}' embodied (char {CharId}, money {Money})", botName, session.Character.Id, session.Character.Money);
        return Ok(new
        {
            name = session.Character.Name,
            id = session.Character.Id,
            objId = session.Character.ObjId,
            level = session.Character.Level,
            money = session.Character.Money
        });
    }

    private string HandleDeactivate(JsonElement root)
    {
        var rawName = root.TryGetProperty("bot", out var b) ? b.GetString() : null;
        if (string.IsNullOrWhiteSpace(rawName))
            return Err("deactivate requires 'bot'");
        var botName = rawName.NormalizeName();
        if (!PersistentBotSessions.TryRemove(botName, out var session))
            return Err($"deactivate: bot '{botName}' is not provisioned");
        try
        {
            CharacterLifecycleService.Instance.Deactivate(session.Character, CharacterLifecycleReason.Logout);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "deactivate failed for '{Bot}'", botName);
        }

        return Ok(new { removed = true, id = session.Character.Id });
    }

    /// <summary>
    /// G2-A5 acceptance bulk seeder: mints N managed bot accounts + character
    /// rows through the REAL provisioning path (the same
    /// <see cref="HeadlessSession.Provision"/> the 'provision' command uses),
    /// records each bot's playerbot_metadata home (the hard HasHome
    /// prerequisite for proximity materialization), then deactivates —
    /// leaving exactly what true dormancy discovers: a durable characters row
    /// on a HeadlessBot account, not embodied, with a known home.
    ///
    /// Request shape:
    ///   { "cmd": "seedDormant", "level": 5,
    ///     "bots": [ { "name": "DormNear001", "home": {"x":..,"y":..,"z":..} }, ... ] }
    /// Batched by the caller (each entry is a synchronous provision +
    /// deactivate round-trip); rows persist across game restarts.
    /// </summary>
    private string HandleSeedDormant(JsonElement root)
    {
        if (!root.TryGetProperty("bots", out var botsEl) ||
            botsEl.ValueKind != JsonValueKind.Array ||
            botsEl.GetArrayLength() == 0)
            return Err("seedDormant requires 'bots': [{name, home:{x,y,z}}, ...]");
        var level = (byte)GetInt(root, "level", 10);

        var seeded = new List<object>();
        foreach (var entry in botsEl.EnumerateArray())
        {
            var rawName = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(rawName))
                return Err("seedDormant: every bots[] entry requires 'name'");
            var botName = rawName.NormalizeName();
            var username = BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant();

            try
            {
                EnsureFreshBotRow(botName, username);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "seedDormant: fresh wipe failed for '{Bot}' — continuing with adoption semantics", botName);
            }

            HeadlessSession session;
            try
            {
                session = HeadlessSession.Provision(username, botName, Race.Nuian, Gender.Male, level);
            }
            catch (Exception ex)
            {
                return Err($"seedDormant: provision failed for '{botName}': {ex.GetType().Name}: {ex.Message}");
            }

            // Home metadata is the proximity prerequisite: a spec without a
            // recorded home is skipped forever by MaterializeNearbyDormantSpecs.
            var hasHome = false;
            if (entry.TryGetProperty("home", out var homeEl) && homeEl.ValueKind == JsonValueKind.Object)
            {
                var hx = homeEl.GetProperty("x").GetSingle();
                var hy = homeEl.GetProperty("y").GetSingle();
                var hz = homeEl.GetProperty("z").GetSingle();
                PlayerBotMetadataStore.Instance.RecordHome(
                    session.Character.Id,
                    session.Character.Transform.WorldId,
                    session.Character.Transform.ZoneId,
                    hx, hy, hz);

                // PB-004 companion: real dormant specs (dematerialized presence
                // citizens) carry the presence coordinator's roam-loop schedule,
                // which is what keeps a woken bot STEPPING after its wake.
                // Without it a materialized bot executes one step and goes
                // quiet again — faithful seeds need the same descriptor.
                PlayerBotMetadataStore.Instance.RecordSchedule(
                    session.Character.Id,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        kind = "roam-loop",
                        radius = 15f,
                        phase = seeded.Count, // per-bot phase seed, same role as BuildRoamScheduleJson's
                        loop = true,
                        home = new[] { hx, hy, hz }
                    }));
                hasHome = true;
            }
            try
            {
                CharacterLifecycleService.Instance.Deactivate(session.Character, CharacterLifecycleReason.Logout);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "seedDormant: deactivate failed for '{Bot}'", botName);
                return Err($"seedDormant: deactivate failed for '{botName}': {ex.Message}");
            }

            seeded.Add(new { name = session.Character.Name, id = session.Character.Id, hasHome });
        }

        Logger.Info("seedDormant: seeded {Count} dormant specs", seeded.Count);
        return Ok(new { seeded = seeded.Count, bots = seeded });
    }

    /// <summary>
    /// Resolves a persistent session by normalized bot name.
    /// </summary>
    private static bool TryResolvePersistentBot(string rawName, out Character? character, out string error)
    {
        character = null;
        error = "";
        if (string.IsNullOrWhiteSpace(rawName))
        {
            error = "auction op requires 'bot'";
            return false;
        }

        var botName = rawName.NormalizeName();
        if (!PersistentBotSessions.TryGetValue(botName, out var session) || session.Character == null)
        {
            error = $"bot '{botName}' is not provisioned on this boot (call cmd 'provision' first — a restart wipes the registry)";
            return false;
        }

        character = session.Character;
        return true;
    }

    /// <summary>
    /// Auction-house observation and contract-action ops over the persistent
    /// sessions. Ops:
    ///   rig    — set money and/or stock bag items (the AuctionHouseScenario
    ///            rig shape: ordinary character fields + normal acquisition)
    ///   post   — GameplayActor.PostAuction (real CSAuctionPostPacket path)
    ///   buy    — GameplayActor.BuyAuction (real CSBidAuctionPacket buy-now path)
    ///   lots   — dump the live AuctionManager lot collection
    ///   search — buyer-side filter over the live lots (the collection
    ///            SearchAuctionLots serves pages from)
    ///   mails  — dump MailManager.GetCurrentMailList for the bot
    ///   char   — observable character state (id/money/name)
    /// </summary>
    private string HandleAuctionOp(JsonElement root)
    {
        var op = root.GetProperty("op").GetString();
        switch (op)
        {
            case "lots":
            {
                return Ok(new { lots = LotDumps(AuctionManager.Instance.AuctionLots.Values) });
            }
            case "search":
            {
                if (!TryResolvePersistentBot(root.TryGetProperty("bot", out var sb) ? sb.GetString() : null, out var searcher, out var searchErr))
                    return Err(searchErr);
                var templateFilter = GetUInt(root, "itemTemplate");
                var matches = AuctionManager.Instance.AuctionLots.Values
                    .Where(l => l.Item != null && l.Item.TemplateId == templateFilter)
                    .ToList();
                return Ok(new
                {
                    searchedBy = searcher!.Name,
                    itemTemplate = templateFilter,
                    count = matches.Count,
                    lots = LotDumps(matches)
                });
            }
            case "mails":
            {
                if (!TryResolvePersistentBot(root.TryGetProperty("bot", out var mb) ? mb.GetString() : null, out var mailReader, out var mailErr))
                    return Err(mailErr);
                var mails = MailManager.Instance.GetCurrentMailList(mailReader!.Id).Values
                    .OrderBy(m => m.Id)
                    .Select(m => new
                    {
                        id = m.Id,
                        type = (int)m.MailType,
                        title = m.Title,
                        senderName = m.Header.SenderName,
                        receiverId = m.Header.ReceiverId,
                        copperCoins = m.Body.CopperCoins,
                        attachments = m.Body.Attachments.Select(a => new
                        {
                            itemId = a.Id,
                            templateId = a.TemplateId,
                            count = a.Count,
                            slotType = (int)a.SlotType
                        }).ToArray()
                    }).ToArray();
                return Ok(new { receiverId = mailReader.Id, mails });
            }
            case "char":
            {
                if (!TryResolvePersistentBot(root.TryGetProperty("bot", out var cb) ? cb.GetString() : null, out var ch, out var charErr))
                    return Err(charErr);
                return Ok(new
                {
                    name = ch!.Name,
                    id = ch.Id,
                    objId = ch.ObjId,
                    level = ch.Level,
                    money = ch.Money
                });
            }
        }

        // Everything below mutates — resolve the actor first.
        if (!TryResolvePersistentBot(root.TryGetProperty("bot", out var b) ? b.GetString() : null, out var character, out var err))
            return Err(err);

        switch (op)
        {
            case "rig":
            {
                if (root.TryGetProperty("money", out var moneyEl) && moneyEl.TryGetInt64(out var money))
                    character!.Money = money;
                var stockTemplate = GetUInt(root, "stockTemplate");
                if (stockTemplate > 0)
                    new PlayerBotController(character!).StockInventory(stockTemplate, GetInt(root, "stockCount", 1));
                return Ok(new { name = character!.Name, money = character.Money });
            }
            case "post":
            {
                var templateId = GetUInt(root, "itemTemplate");
                if (templateId == 0)
                    return Err("auction post requires 'itemTemplate'");

                var inBag = character!.Inventory.Bag.GetAllItemsByTemplate(templateId, -1, out var items, out _)
                            && items.Count > 0;
                if (!inBag)
                    return Err($"auction post: bot '{character.Name}' has no item of template {templateId} in bag");
                var itemId = items[0].Id;

                var duration = (AuctionDuration)GetInt(root, "duration", (int)AuctionDuration.AuctionDuration6Hours);
                var startPrice = GetInt(root, "startPrice", 100);
                var buyoutPrice = GetInt(root, "buyoutPrice", 1000);

                var actor = new GameplayActor(character);
                var request = actor.PostAuction(itemId, startPrice, buyoutPrice, duration);
                var lotId = request.Result is ulong ul ? ul : Convert.ToUInt64(request.Result ?? 0UL);
                return Ok(new
                {
                    state = request.State.ToString(),
                    itemId,
                    lotId,
                    failure = request.Failure.ToString(),
                    detail = request.Detail ?? ""
                });
            }
            case "buy":
            {
                var lotId = root.TryGetProperty("lotId", out var lidEl) && lidEl.TryGetUInt64(out var lid) ? lid : 0UL;
                if (lotId == 0)
                    return Err("auction buy requires 'lotId'");
                var price = GetInt(root, "price", 0);
                if (price <= 0)
                {
                    var lot = AuctionManager.Instance.AuctionLots.GetValueOrDefault(lotId);
                    if (lot == null)
                        return Err($"auction buy: lot {lotId} not found (already sold or expired)");
                    price = lot.DirectMoney;
                }

                var actor = new GameplayActor(character!);
                var request = actor.BuyAuction(lotId, price);
                return Ok(new
                {
                    state = request.State.ToString(),
                    lotId,
                    paid = price,
                    failure = request.Failure.ToString(),
                    detail = request.Detail ?? ""
                });
            }
            default:
                return Err($"unknown auction op '{op}'");
        }
    }
    /// <summary>
    /// Dominion slice-1 rig ops (test seam — the direct-manager-API declare
    /// route documented for the persistence proof):
    ///   declare — persist + broadcast a dominion via DominionManager.Declare
    ///             (zoneGroup, expeditionId, expeditionName, taxRate)
    ///   list    — dump the manager's in-memory dominion store (post-restart
    ///             this reflects what was reloaded from MySQL)
    /// </summary>
    private string HandleDominionOp(JsonElement root)
    {
        var op = root.GetProperty("op").GetString();
        switch (op)
        {
            case "declare":
            {
                var zoneGroup = GetUInt(root, "zoneGroup");
                if (zoneGroup == 0)
                    return Err("dominion declare requires 'zoneGroup'");
                var expeditionId = GetUInt(root, "expeditionId");
                var expeditionName = root.TryGetProperty("expeditionName", out var en) ? en.GetString() : "rig";
                var taxRate = GetInt(root, "taxRate", 50);

                var dominion = Core.Managers.DominionManager.BuildDominionData(
                    zoneGroup, expeditionId, 0, 0f, 0f, 0f, taxRate, DateTime.UtcNow);
                Core.Managers.DominionManager.Instance.Declare(dominion, expeditionName ?? "rig");
                return Ok(new { declared = zoneGroup, expeditionId, taxRate });
            }
            case "list":
            {
                var dominions = Core.Managers.DominionManager.Instance.Dominions
                    .Select(d => new
                    {
                        zoneGroupId = d.ZoneGroupId,
                        expeditionId = d.ExpeditionId,
                        expeditionName = d.ExpeditionName,
                        taxRate = d.TaxRate,
                        declaredAt = d.DeclaredAt
                    }).ToArray();
                return Ok(new { count = dominions.Length, dominions });
            }
            default:
                return Err($"unknown dominion op '{op}'");
        }
    }


    private static object[] LotDumps(IEnumerable<AuctionLot> lots)
        => lots.Select(l => new
        {
            id = l.Id,
            itemTemplate = l.Item?.TemplateId ?? 0,
            itemId = l.Item?.Id ?? 0,
            stackSize = l.Item?.Count ?? 0,
            clientId = l.ClientId,
            clientName = l.ClientName,
            startMoney = l.StartMoney,
            directMoney = l.DirectMoney,
            bidMoney = l.BidMoney,
            bidderId = l.BidderId,
            bidderName = l.BidderName,
            endTime = l.EndTime
        }).ToArray();

    #endregion

    #region MAIL-01 S3 E2E seam (real networked bots + real mail packets)

    /// <summary>
    /// Resolves a REAL networked session by character name (same discipline as
    /// HandleDrive: only characters that entered the world through the real
    /// login flow are drivable; the bridge never fabricates sessions).
    /// </summary>
    private static bool TryResolveNetworkedBot(string rawName, out Character? character, out string error)
    {
        character = null;
        error = "";
        if (string.IsNullOrWhiteSpace(rawName))
        {
            error = "mail op requires 'bot'";
            return false;
        }

        var connection = GameConnectionTable.Instance.GetConnections().FirstOrDefault(c =>
            c.ActiveChar != null &&
            string.Equals(c.ActiveChar.Name, rawName.NormalizeName(), StringComparison.OrdinalIgnoreCase));
        if (connection?.ActiveChar == null)
        {
            error = $"bot '{rawName}' is not in the world (no active networked session)";
            return false;
        }

        character = connection.ActiveChar;
        return true;
    }



    /// <summary>
    /// MAIL-01 slice-S3 observation/rig ops over REAL networked bot sessions.
    /// All gameplay mutations ride normal engine paths; the actual send /
    /// read / take / delete flows are driven by the test over the wire with
    /// real C2G mail packets. Ops:
    ///   rig    — set money and stock ONE equipment-grade item instance with
    ///            explicit grade/enchant state (durability, rune, tempers)
    ///   delay  — shrink MailManager.NormalMailDelay (existing runtime-tunable
    ///            static, same seam /settradepackmaildelay uses) so a Normal
    ///            mail delivers inside the E2E window
    ///   mailbox— position the bot at a live DoodadFuncNaviOpenMailbox doodad
    ///            (teleport to its spawner position, wait for the world's
    ///            NORMAL spawn path) and return the doodad ObjId + distance
    ///   char   — observable character state (id/money/unread counters)
    ///   mails  — dump MailManager.GetCurrentMailList incl. full attachment
    ///            instance fidelity (grade/durability/rune/tempers/details)
    ///   inv    — dump the bot's bag items (post-take fidelity check)
    /// </summary>
    private string HandleMailOp(JsonElement root)
    {
        var op = root.GetProperty("op").GetString();

        // Server-wide tuning op needs no bot.
        if (op == "delay")
        {
            var seconds = GetInt(root, "seconds", 15);
            Core.Managers.MailManager.NormalMailDelay = TimeSpan.FromSeconds(seconds);
            return Ok(new { normalMailDelaySeconds = seconds });
        }

        if (!TryResolveNetworkedBot(root.TryGetProperty("bot", out var b) ? b.GetString() : null, out var character, out var err))
            return Err(err);

        switch (op)
        {
            case "rig":
            {
                if (root.TryGetProperty("money", out var moneyEl) && moneyEl.TryGetInt64(out var money))
                    character!.Money = money;

                var templateId = GetUInt(root, "itemTemplate");
                Item item = null;
                if (templateId > 0)
                {
                    var before = character!.Inventory.Bag.Items
                        .Where(i => i.TemplateId == templateId).Select(i => i.Id).ToHashSet();
                    // Normal acquisition path (quest-supply shape), with the
                    // requested grade.
                    character.Inventory.Bag.AcquireDefaultItem(
                        ItemTaskType.QuestSupplyItems, templateId, GetInt(root, "count", 1), GetInt(root, "grade", 1));
                    item = character.Inventory.Bag.Items
                        .FirstOrDefault(i => i.TemplateId == templateId && !before.Contains(i.Id));
                    if (item == null)
                        return Err($"mail rig: no new bag item of template {templateId} appeared");

                    // Enchant-state rig on the SAME instance that will travel:
                    // durability + rune (enchant) + temper levels. The details
                    // blob written from these fields is the fidelity payload.
                    if (item is Items.EquipItem equip)
                    {
                        equip.Durability = (byte)GetInt(root, "durability", (int)equip.Durability);
                        equip.RuneId = (uint)GetLong(root, "runeId", (long)equip.RuneId);
                        equip.TemperPhysical = (ushort)GetInt(root, "temperPhysical", equip.TemperPhysical);
                        equip.TemperMagical = (ushort)GetInt(root, "temperMagical", equip.TemperMagical);
                        item.IsDirty = true; // details must reach MySQL on the save pass
                    }
                    else
                    {
                        return Err($"mail rig: template {templateId} is not an equipment-grade item (no details blob)");
                    }
                }

                return Ok(new
                {
                    name = character!.Name,
                    id = character.Id,
                    money = character.Money,
                    itemId = item?.Id ?? 0,
                    grade = item?.Grade ?? 0,
                    durability = (item as Items.EquipItem)?.Durability ?? 0,
                    runeId = (item as Items.EquipItem)?.RuneId ?? 0,
                    temperPhysical = (item as Items.EquipItem)?.TemperPhysical ?? 0,
                    temperMagical = (item as Items.EquipItem)?.TemperMagical ?? 0,
                    slot = item?.Slot ?? -1
                });
            }
            case "mailbox":
            {
                // Find an ACTIVE mailbox doodad in this real world. The
                // send packet validates the instance's CurrentFuncs and
                // proximity, so selecting from GetAllDoodads keeps this rig
                // on the same live object instead of manufacturing a doodad
                // or relying on an unavailable NPC-spawner collection.
                var mailbox = character!.ParentWorld.GetAllDoodads()
                    .FirstOrDefault(d => d.CurrentFuncs?.Any(f => f.FuncType == "DoodadFuncNaviOpenMailbox") == true);
                if (mailbox == null)
                    return Err("mailbox: no active DoodadFuncNaviOpenMailbox doodad in this world");

                var pos = mailbox.Transform.World.Position;
                TeleportWithRegionSync(character,
                    new System.Numerics.Vector3(pos.X + 2f, pos.Y + 2f, pos.Z),
                    mailbox.Transform.ZoneId);
                character.MarkDirty();

                var dist = System.Numerics.Vector3.Distance(
                    character.Transform.World.Position, mailbox.Transform.World.Position);
                return Ok(new
                {
                    doodadObjId = mailbox.ObjId,
                    doodadTemplateId = mailbox.TemplateId,
                    distance = dist,
                    withinRange = dist <= 5f
                });
            }
            case "char":
            {
                return Ok(new
                {
                    name = character!.Name,
                    id = character.Id,
                    objId = character.ObjId,
                    money = character.Money,
                    unread = new
                    {
                        sent = character.Mails.UnreadMailCount.Sent,
                        received = character.Mails.UnreadMailCount.Received
                    }
                });
            }
            case "mails":
            {
                var mails = Core.Managers.MailManager.Instance.AllPlayerMails.Values
                    .Where(m => m.Body.RecvDate <= DateTime.UtcNow &&
                                (m.Header.ReceiverId == character!.Id || m.Header.SenderId == character.Id))
                    .OrderBy(m => m.Id)
                    .Select(m => new
                    {
                        id = m.Id,
                        type = (int)m.MailType,
                        title = m.Title,
                        status = (int)m.Header.Status,
                        senderId = m.Header.SenderId,
                        senderName = m.Header.SenderName,
                        receiverId = m.Header.ReceiverId,
                        receiverName = m.Header.ReceiverName,
                        attachmentsHeader = m.Header.Attachments,
                        copperCoins = m.Body.CopperCoins,
                        returned = m.Header.Returned,
                        attachments = m.Body.Attachments.Select(a => new
                        {
                            itemId = a.Id,
                            templateId = a.TemplateId,
                            count = a.Count,
                            grade = a.Grade,
                            slotType = (int)a.SlotType,
                            slot = a.Slot,
                            durability = (a as Items.EquipItem)?.Durability ?? 0,
                            runeId = (a as Items.EquipItem)?.RuneId ?? 0,
                            temperPhysical = (a as Items.EquipItem)?.TemperPhysical ?? 0,
                            temperMagical = (a as Items.EquipItem)?.TemperMagical ?? 0,
                            detailB64 = ItemDetailB64(a)
                        }).ToArray()
                    }).ToArray();
                return Ok(new { charId = character.Id, mails });
            }
            case "inv":
            {
                var items = character!.Inventory.Bag.Items.Select(i => new
                {
                    itemId = i.Id,
                    templateId = i.TemplateId,
                    count = i.Count,
                    grade = i.Grade,
                    slotType = (int)i.SlotType,
                    slot = i.Slot,
                    durability = (i as Items.EquipItem)?.Durability ?? 0,
                    runeId = (i as Items.EquipItem)?.RuneId ?? 0,
                    temperPhysical = (i as Items.EquipItem)?.TemperPhysical ?? 0,
                    temperMagical = (i as Items.EquipItem)?.TemperMagical ?? 0,
                    detailB64 = ItemDetailB64(i)
                }).ToArray();
                return Ok(new { items });
            }
            default:
                return Err($"unknown mail op '{op}'");
        }
    }

    private static string ItemDetailB64(Item item)
    {
        if (item.Detail is { Length: > 0 })
            return Convert.ToBase64String(item.Detail);

        var detail = new PacketStream();
        detail.Write((byte)item.DetailType);
        item.WriteDetails(detail);
        return Convert.ToBase64String(detail.GetBytes());
    }


    private static long GetLong(JsonElement root, string name, long defaultValue = 0)
        => root.TryGetProperty(name, out var el) && el.TryGetInt64(out var v) ? v : defaultValue;

    #endregion

    #region B1 HOUSING-01 R-run E2E seam (persistent headless bots + real housing paths)

    /// <summary>
    /// B1 HOUSING-01 R-run seam (E2E-ONLY, additive): claim + construct +
    /// decorate a house through the REAL engine paths on a persistent
    /// headless bot, for the kill-9 byte-equality run. Ops:
    ///   rig       — set money + stock the canonical design item (21166 → 172)
    ///               and deco item (8229 → design 62) via the normal
    ///               acquisition path (PlayerBotController.StockInventory).
    ///   build     — attach a null-session GameConnection when the headless
    ///               character has none (the M5.2 rig AttachConnection shape:
    ///               packets encode and vanish) and call
    ///               GameplayActor.BuildHouse → HousingManager.Build (the exact
    ///               CSCreateHousePacket call), spiralling over the world's
    ///               housing-polygon centroids (+ M3b fallback grid) until the
    ///               engine Completes a placement. Rejections consume nothing.
    ///   construct — drive House.AddBuildAction to CurrentStep -1 (the state
    ///               transition CraftEffect's Building group performs).
    ///   decorate  — HousingManager.DecorateHouse (the CSDecorateHousePacket
    ///               path, DecoLimitEvaluator gate included).
    ///   status    — observable house + doodad counts for the runner.
    /// </summary>
    private string HandleHousingOp(JsonElement root)
    {
        const uint HouseDesignId = 172;
        const uint DesignItemTemplateId = 21166;
        const uint DecoDesignId = 62;
        const uint DecoItemTemplateId = 8229;
        const uint DecoDoodadTemplateId = 1256;

        if (!TryResolvePersistentBot(root.TryGetProperty("bot", out var b) ? b.GetString() : null, out var character, out var err))
            return Err(err);

        var op = root.GetProperty("op").GetString();
        switch (op)
        {
            case "rig":
            {
                if (root.TryGetProperty("money", out var moneyEl) && moneyEl.TryGetInt64(out var money))
                    character!.Money = money;
                var controller = new PlayerBotController(character!);
                controller.StockInventory(DesignItemTemplateId, GetInt(root, "designCount", 3));
                controller.StockInventory(DecoItemTemplateId, GetInt(root, "decoCount", 2));
                return Ok(new
                {
                    name = character!.Name,
                    id = character.Id,
                    money = character.Money,
                    designs = controller.InventoryCount(DesignItemTemplateId),
                    decos = controller.InventoryCount(DecoItemTemplateId)
                });
            }
            case "build":
            {
                var world = character!.ParentWorld;
                if (world == null)
                    return Err("housing build: bot has no parent world");
                if (HousingGameData.Instance.GetTemplate(HouseDesignId) == null)
                    return Err($"housing build: unknown house design {HouseDesignId}");
                if (character.Connection == null)
                {
                    var conn = new GameConnection(new E2eNullSession());
                    conn.ActiveChar = character;
                    conn.AccountId = character.AccountId;
                    character.Connection = conn;
                }

                var before = HousingManager.Instance.GetAllHouses().Select(h => h.Id).ToHashSet();
                var candidates = HousingBuildCandidates(world, character.Transform.World.Position.Z);
                var attempts = 0;
                var rejects = new List<string>();
                foreach (var pos in candidates)
                {
                    attempts++;
                    var actor = new GameplayActor(character);
                    var request = actor.BuildHouse(HouseDesignId, DesignItemTemplateId, pos, 0f,
                        idempotencyKey: $"b1-housing-{attempts}");
                    if (request.State == ActorLifecycleState.Completed)
                    {
                        var house = HousingManager.Instance.GetAllHouses()
                            .FirstOrDefault(h => h.OwnerId == character.Id && !before.Contains(h.Id));
                        if (house == null)
                            return Err($"housing build: engine Completed but no new house registered for '{character.Name}' (attempt {attempts})");
                        var p = house.Transform.World.Position;
                        return Ok(new
                        {
                            state = request.State.ToString(),
                            houseId = house.Id,
                            houseTlId = house.TlId,
                            currentStep = house.CurrentStep,
                            x = p.X,
                            y = p.Y,
                            z = p.Z,
                            attempts
                        });
                    }
                    if (rejects.Count < 5)
                        rejects.Add($"[{pos.X:F1},{pos.Y:F1},{pos.Z:F1}] {request.State}/{request.Failure}: {request.Detail ?? ""}");
                    if (request.State == ActorLifecycleState.Interrupted)
                        return Err($"housing build: AMBIGUOUS engine interrupt at attempt {attempts} ({request.Detail ?? "no detail"}) — placement may or may not have applied; aborting spiral. Rejects so far: {string.Join(" | ", rejects)}");
                }
                return Err($"housing build: engine refused all {attempts} candidates. Sample: {string.Join(" | ", rejects)}");
            }
            case "construct":
            {
                var house = ResolveHousingBotHouse(character!, root);
                if (house == null)
                    return Err($"housing construct: no house for bot '{character!.Name}' (build first)");
                var applied = 0;
                while (house.CurrentStep != -1 && applied < 10)
                {
                    house.AddBuildAction();
                    applied++;
                }
                house.IsDirty = true;
                return Ok(new
                {
                    houseId = house.Id,
                    houseTlId = house.TlId,
                    applied,
                    currentStep = house.CurrentStep,
                    currentAction = house.NumAction
                });
            }
            case "decorate":
            {
                var house = ResolveHousingBotHouse(character!, root);
                if (house == null)
                    return Err($"housing decorate: no house for bot '{character!.Name}' (build first)");
                character!.Inventory.Bag.GetAllItemsByTemplate(DecoItemTemplateId, -1, out var decoItems, out _);
                var decoItem = decoItems.FirstOrDefault();
                if (decoItem == null)
                    return Err($"housing decorate: bot has no deco item {DecoItemTemplateId} in bag (run rig first)");
                var lx = root.TryGetProperty("lx", out var lxEl) && lxEl.ValueKind == JsonValueKind.Number ? lxEl.GetSingle() : 1f;
                var ly = root.TryGetProperty("ly", out var lyEl) && lyEl.ValueKind == JsonValueKind.Number ? lyEl.GetSingle() : 0f;
                var lz = root.TryGetProperty("lz", out var lzEl) && lzEl.ValueKind == JsonValueKind.Number ? lzEl.GetSingle() : 2f;
                var ok = HousingManager.Instance.DecorateHouse(character, house.TlId, DecoDesignId,
                    new System.Numerics.Vector3(lx, ly, lz), System.Numerics.Quaternion.Identity,
                    house.ObjId, decoItem.Id);
                if (!ok)
                    return Err($"housing decorate: engine refused design {DecoDesignId} on house {house.Id} (limit gate?)");
                var doodad = house.ParentWorld.GetDoodadByHouseDbId(house.Id)
                    .FirstOrDefault(d => d.TemplateId == DecoDoodadTemplateId);
                return Ok(new
                {
                    decorated = ok,
                    houseId = house.Id,
                    doodadObjId = doodad?.ObjId ?? 0u,
                    doodadTemplate = DecoDoodadTemplateId
                });
            }
            case "status":
            {
                var houses = HousingManager.Instance.GetAllHouses()
                    .Where(h => h.OwnerId == character!.Id).OrderBy(h => h.Id).ToList();
                return Ok(new
                {
                    name = character!.Name,
                    id = character.Id,
                    houses = houses.Select(h => new
                    {
                        houseId = h.Id,
                        houseTlId = h.TlId,
                        template = h.TemplateId,
                        currentStep = h.CurrentStep,
                        currentAction = h.NumAction,
                        x = h.Transform.World.Position.X,
                        y = h.Transform.World.Position.Y,
                        z = h.Transform.World.Position.Z,
                        doodads = h.ParentWorld.GetDoodadByHouseDbId(h.Id).Select(d => new
                        {
                            objId = d.ObjId,
                            template = d.TemplateId,
                            attach = (int)d.AttachPoint
                        }).ToArray()
                    }).ToArray()
                });
            }
            default:
                return Err($"unknown housing op '{op}'");
        }
    }

    /// <summary>
    /// Newest house owned by the bot (or the explicit houseTlId): the B1
    /// run owns exactly one house, so newest-owned is unambiguous.
    /// </summary>
    private static AAEmu.Game.Models.Game.Housing.House? ResolveHousingBotHouse(Character character, JsonElement root)
    {
        var tlId = GetUInt(root, "houseTlId");
        var houses = HousingManager.Instance.GetAllHouses();
        if (tlId > 0)
            return houses.FirstOrDefault(h => h.TlId == (ushort)tlId);
        return houses
            .OrderByDescending(h => h.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Deterministic build-candidate spiral: housing-polygon centroids from
    /// the live world template (world coords) with a 3x3 clearing grid each,
    /// then the M3b fallback grid. Z comes from the height sampler with a
    /// caller-supplied fallback (validator-unrejected attempts consume
    /// nothing, so probing is safe).
    /// </summary>
    private static List<System.Numerics.Vector3> HousingBuildCandidates(AAEmu.Game.Models.Game.World.WorldInstance world, float fallbackZ)
    {
        var spots = new List<System.Numerics.Vector3>();
        try
        {
            var areas = world.Template.HousingZones.Values
                .SelectMany(v => v).OrderBy(a => a.Id).Take(8).ToList();
            foreach (var area in areas)
            {
                var pts = area.Points;
                if (pts == null || pts.Count == 0)
                    continue;
                var cx = pts.Average(p => p.X);
                var cy = pts.Average(p => p.Y);
                float gz;
                try
                {
                    var zk = WorldManager.Instance.GetZoneId(world.Template, cx, cy);
                    gz = WorldManager.Instance.GetHeight(zk, cx, cy, fallbackZ);
                }
                catch
                {
                    gz = fallbackZ;
                }
                foreach (var (dx, dy) in new[] { (0f, 0f), (10f, 0f), (-10f, 0f), (0f, 10f), (0f, -10f), (20f, 0f), (-20f, 0f), (0f, 20f), (0f, -20f) })
                    spots.Add(new System.Numerics.Vector3(cx + dx, cy + dy, gz));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "housing build: polygon-centroid candidates unavailable — M3b fallback grid only");
        }
        foreach (var (dx, dy) in new[] { (0f, 0f), (10f, 0f), (-10f, 0f), (0f, 10f), (0f, -10f), (20f, 0f), (-20f, 0f), (0f, 20f), (0f, -20f) })
            spots.Add(new System.Numerics.Vector3(20010f + dx, 20020f + dy, fallbackZ));
        return spots;
    }

    /// <summary>
    /// E2E-only null session for the B1 housing seam: lets a headless
    /// character traverse the connection-mediated Build path (the M5.2 rig
    /// AttachConnection shape). Packets encode and vanish.
    /// </summary>
    private sealed class E2eNullSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 0xE2E1;
        public System.Net.Sockets.Socket Socket => null!;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) => _attributes[name] = attribute;
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name)!;
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }

    /// <summary>
    /// B2 FARM-01 R-run seam (additive, E2E-only): plant / interact / harvest
    /// over the REAL M5 contracts on a persistent bot session — the exact
    /// calls the village/economy scenarios drive live
    /// (<see cref="AAEmu.Game.Core.Managers.Bots.GameplayActor.Plant"/>,
    /// <see cref="AAEmu.Game.Core.Managers.Bots.GameplayActor.Harvest"/>,
    /// <see cref="AAEmu.Game.Core.Managers.Bots.GameplayActor.Interact"/>).
    /// No direct DB writes, no bot-side placement: every mutation flows
    /// through the engine (DoodadManager.CreatePlayerDoodad,
    /// Doodad.Use → DoodadFuncCropHarvest/FruitPick → loot).
    /// Ops:
    ///   rig      — stock seeds + calves through the ordinary
    ///              PlayerBotController acquisition path, top up labor.
    ///   plant    — GameplayActor.Plant(seed item, bot pos + dx/dy).
    ///   find     — resolve live ObjIds by stable doodad DbIds (ObjIds are
    ///              reassigned on every boot; DbIds persist in MySQL).
    ///   status   — live doodad state per ObjId + bag counts per item.
    ///   interact — GameplayActor.Interact(objId, skill) (watering / feed).
    ///   harvest  — GameplayActor.Harvest(objId) (the crop-loop yield path).
    /// </summary>
    private string HandleFarmOp(JsonElement root)
    {
        const uint PotatoSeedItemId = 15659;
        const uint CalfItemId = 16225;

        if (!TryResolvePersistentBot(root.TryGetProperty("bot", out var b) ? b.GetString() : null, out var character, out var err))
            return Err(err);

        var op = root.GetProperty("op").GetString();
        switch (op)
        {
            case "rig":
            {
                var seeds = GetInt(root, "seeds", 5);
                var calves = GetInt(root, "calves", 2);
                var labor = GetInt(root, "labor", 5000);
                var controller = new PlayerBotController(character!);
                if (seeds > 0)
                    controller.StockInventory(PotatoSeedItemId, seeds);
                if (calves > 0)
                    controller.StockInventory(CalfItemId, calves);
                if (labor > 0)
                    character!.LaborPower = (short)Math.Clamp(labor, 0, short.MaxValue);
                return Ok(new
                {
                    name = character!.Name,
                    id = character.Id,
                    seeds = controller.InventoryCount(PotatoSeedItemId),
                    calves = controller.InventoryCount(CalfItemId),
                    labor = character.LaborPower,
                });
            }
            case "plant":
            {
                var seedItem = GetUInt(root, "seed");
                if (seedItem == 0)
                    return Err("farm plant requires 'seed' (plantable item template id)");
                var dx = GetFloat(root, "dx", 0f);
                var dy = GetFloat(root, "dy", 0f);
                var basePos = character!.Transform.World.Position;
                var pos = new System.Numerics.Vector3(basePos.X + dx, basePos.Y + dy, basePos.Z);
                var actor = new GameplayActor(character);
                var request = actor.Plant(seedItem, pos,
                    idempotencyKey: $"b2farm-plant-{seedItem}-{dx}-{dy}");
                if (request.State != ActorLifecycleState.Completed)
                    return Err($"farm plant: engine {request.State}/{request.Failure}: {request.Detail ?? "no detail"}");
                var objId = ReadVillageObjId(request.Result);
                var doodad = objId != 0 ? character.ParentWorld?.GetDoodad(objId) : null;
                return Ok(new
                {
                    state = request.State.ToString(),
                    objId,
                    dbId = doodad?.DbId ?? 0u,
                    template = doodad?.TemplateId ?? 0u,
                    phase = doodad?.FuncGroupId ?? 0u,
                    detail = request.Detail ?? "",
                });
            }
            case "find":
            {
                var dbIds = root.TryGetProperty("dbIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
                    ? idsEl.EnumerateArray().Select(e => e.GetUInt32()).ToList()
                    : [];
                var world = character!.ParentWorld;
                var found = new List<object>();
                foreach (var dbId in dbIds)
                {
                    var doodad = world?.GetAllDoodads().FirstOrDefault(d => d.DbId == dbId);
                    found.Add(doodad == null
                        ? new { dbId, found = false, objId = 0u, template = 0u, phase = 0u }
                        : new { dbId, found = true, objId = doodad.ObjId, template = doodad.TemplateId, phase = doodad.FuncGroupId });
                }
                return Ok(new { doodads = found.ToArray() });
            }
            case "status":
            {
                var objIds = root.TryGetProperty("objIds", out var oidsEl) && oidsEl.ValueKind == JsonValueKind.Array
                    ? oidsEl.EnumerateArray().Select(e => e.GetUInt32()).ToList()
                    : [];
                var itemTemplates = root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array
                    ? itemsEl.EnumerateArray().Select(e => e.GetUInt32()).ToList()
                    : [];
                var world = character!.ParentWorld;
                var doodads = new List<object>();
                foreach (var objId in objIds)
                {
                    var doodad = world?.GetDoodad(objId);
                    if (doodad == null)
                    {
                        doodads.Add(new { objId, found = false, dbId = 0u, template = 0u, phase = 0u, x = 0f, y = 0f, z = 0f });
                        continue;
                    }
                    var p = doodad.Transform.World.Position;
                    doodads.Add(new
                    {
                        objId, found = true, dbId = doodad.DbId, template = doodad.TemplateId,
                        phase = doodad.FuncGroupId, x = p.X, y = p.Y, z = p.Z,
                    });
                }
                var controller = new PlayerBotController(character);
                var counts = itemTemplates.Select(t => new { template = t, count = controller.InventoryCount(t) }).ToArray();
                return Ok(new
                {
                    name = character.Name,
                    id = character.Id,
                    labor = character.LaborPower,
                    money = character.Money,
                    doodads = doodads.ToArray(),
                    items = counts,
                });
            }
            case "interact":
            {
                var objId = GetUInt(root, "objId");
                var skill = GetUInt(root, "skill");
                if (objId == 0 || skill == 0)
                    return Err("farm interact requires 'objId' and 'skill'");
                var request = new GameplayActor(character!).Interact(objId, skill);
                var doodad = character!.ParentWorld?.GetDoodad(objId);
                return Ok(new
                {
                    state = request.State.ToString(),
                    failure = request.Failure.ToString(),
                    detail = request.Detail ?? "",
                    phase = doodad?.FuncGroupId ?? 0u,
                });
            }
            case "harvest":
            {
                var objId = GetUInt(root, "objId");
                if (objId == 0)
                    return Err("farm harvest requires 'objId'");
                var request = new GameplayActor(character!).Harvest(objId);
                var after = character!.ParentWorld?.GetDoodad(objId);
                return Ok(new
                {
                    state = request.State.ToString(),
                    failure = request.Failure.ToString(),
                    detail = request.Detail ?? "",
                    yield = request.Result is int y ? y : 0,
                    phaseAfter = after?.FuncGroupId ?? 0u,
                    deleted = after == null,
                });
            }
            default:
                return Err($"unknown farm op '{op}'");
        }
    }

    private static float GetFloat(JsonElement root, string name, float defaultValue = 0f)
        => root.TryGetProperty(name, out var el) && el.TryGetSingle(out var v) ? v : defaultValue;



    #endregion


    #region Fresh provisioning (template rig hygiene)

    /// <summary>
    /// Wipes the bot's persisted rows so the next provisioning call creates
    /// a FRESH rig: bot state (playerbot_audit + playerbot_metadata), owned
    /// doodads/items/containers, quests + completed_quests + characters
    /// (aaemu_game) and the managed account row (aaemu_login), plus the
    /// in-memory NameManager registry entry. The same row set the E2E harness
    /// cleanup deletes — scoped strictly to THIS bot's account. A template run
    /// must start from a clean slate: prior runs' accepted quests / completed
    /// flags would be enforced by the real accept gates and poison the rig —
    /// and since the wipe deletes the only character rows, the id allocator
    /// re-issues the same ids after a reboot, so surviving audit/metadata
    /// rows would collide with the new run (C5 day-scale soak finding F2).
    /// </summary>

    private static void EnsureFreshBotRow(string botName, string username)
    {
        var characterId = NameManager.Instance.GetCharacterId(botName);
        if (characterId == 0)
            return; // unregistered — nothing to wipe (fresh name)

        using var connection = MySQL.CreateConnection();
        foreach (var (table, column) in new[]
                 {
                     ("playerbot_audit", "character_id"),
                     ("playerbot_metadata", "character_id"),
                     ("doodads", "owner_id"),
                     ("items", "owner"),
                     ("item_containers", "owner_id"),
                 })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE {column} = @charId";
            cmd.Parameters.AddWithValue("@charId", characterId);
            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Logger.Debug(ex, "scenario wipe: {Table} delete FK-tolerant skip for character {CharacterId}", table, characterId); }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM quests WHERE owner = @charId";
            cmd.Parameters.AddWithValue("@charId", characterId);
            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Logger.Debug(ex, "scenario wipe: quests delete FK-tolerant skip for character {CharacterId}", characterId); }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM completed_quests WHERE owner = @charId";
            cmd.Parameters.AddWithValue("@charId", characterId);
            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Logger.Debug(ex, "scenario wipe: completed_quests delete FK-tolerant skip for character {CharacterId}", characterId); }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM characters WHERE id = @charId";
            cmd.Parameters.AddWithValue("@charId", characterId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM aaemu_login.users WHERE username = @username";
            cmd.Parameters.AddWithValue("@username", username);
            cmd.ExecuteNonQuery();
        }

        NameManager.Instance.RemoveCharacterId(characterId);
        Logger.Info("scenario: fresh provisioning wiped prior rows for '{Bot}' (char {CharId})", botName, characterId);
    }

    #endregion

    private static uint GetUInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.TryGetUInt32(out var v) ? v : 0u;

    private static int GetInt(JsonElement root, string name, int defaultValue = 0)
        => root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? v : defaultValue;

    /// <summary>
    /// Q4 live hunt-leg helper (E2E-ONLY): the caller's bag grouped by
    /// template id with summed counts — the before/after snapshots the
    /// <c>loot</c> op diffs into caller-delta proof.
    /// </summary>
    private static Dictionary<uint, int> CountBagByTemplate(Character character)
    {
        var counts = new Dictionary<uint, int>();
        var bag = character.Inventory?.Bag;
        if (bag == null)
            return counts;
        foreach (var item in bag.GetItemsSnapshot())
        {
            counts.TryGetValue(item.TemplateId, out var existing);
            counts[item.TemplateId] = existing + item.Count;
        }
        return counts;
    }
}
