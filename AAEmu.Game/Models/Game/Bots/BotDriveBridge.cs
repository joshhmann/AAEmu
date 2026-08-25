using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
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
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "E2E bot drive bridge failed to start");
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
            case "seedDormant":
                return HandleSeedDormant(root);
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
                    totalStepsFailed = m.TotalStepsFailed,
                    totalStepsTimedOut = m.TotalStepsTimedOut,
                    avgWakeLatencyMs = m.AverageWakeLatencyMs,
                    maxWakeLatencyMs = m.MaxWakeLatencyMs,
                    workerUtilization = m.WorkerUtilization
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
                            materializeMaxMs = lat.MaxMs
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "gate metrics: dormant registry snapshot unavailable");
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
                    dormancy
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
                maxMs = m.MaxMs
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: save metrics unavailable");
            save = new { available = false, error = ex.Message };
        }

        return new
        {
            tick,
            regionTick,
            scheduler,
            population,
            save,
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
                character.Transform.Local.Position = new System.Numerics.Vector3(
                    spawner.Position.X, spawner.Position.Y, spawner.Position.Z);
                character.Transform.ZoneId = spawner.Position.ZoneId;
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
                _character.Transform.Local.Position = new System.Numerics.Vector3(
                    spawner.Position.X, spawner.Position.Y, spawner.Position.Z);
                _character.Transform.ZoneId = spawner.Position.ZoneId;

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
        if (templateName == EconomyDayCycleScenario.ScenarioName)
            return HandleEconomyDayCycleScenario(root);

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

            memberChar.Transform.Local.Position = leaderChar.Transform.Local.Position +
                new System.Numerics.Vector3(20f, 0f, 0f);
            memberChar.Transform.ZoneId = leaderChar.Transform.ZoneId;

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
                characters[i].Transform.Local.Position = leaderChar.Transform.Local.Position + offsets[(i - 1) % offsets.Length];
                characters[i].Transform.ZoneId = leaderChar.Transform.ZoneId;
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

    #region Fresh provisioning (template rig hygiene)

    /// <summary>
    /// Wipes the bot's persisted rows so the next provisioning call creates
    /// a FRESH rig: quests + completed_quests + characters (aaemu_game) and
    /// the managed account row (aaemu_login), plus the in-memory NameManager
    /// registry entry. The same row set the E2E harness cleanup deletes —
    /// scoped strictly to THIS bot's account. A template run must start
    /// from a clean slate: prior runs' accepted quests / completed flags
    /// would be enforced by the real accept gates and poison the rig.
    /// </summary>
    private static void EnsureFreshBotRow(string botName, string username)
    {
        var characterId = NameManager.Instance.GetCharacterId(botName);
        if (characterId == 0)
            return; // unregistered — nothing to wipe (fresh name)

        using var connection = MySQL.CreateConnection();
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
}
