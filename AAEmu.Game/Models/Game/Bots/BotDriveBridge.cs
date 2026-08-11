using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
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
            case "drive":
                return HandleDrive(root);
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
                population = new
                {
                    available = true,
                    dormant = m.DormantCount,
                    reduced = m.ReducedCount,
                    full = m.FullCount,
                    embodied = m.Embodied,
                    pressure = m.Pressure.ToString(),
                    transitionsApplied = m.TotalTransitionsApplied,
                    transitionsRejected = m.TotalTransitionsRejected
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "gate metrics: population metrics unavailable");
            population = new { available = false, error = ex.Message };
        }

        return new
        {
            tick,
            regionTick,
            scheduler,
            population,
            uptimeMs = Environment.TickCount64
        };
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

    private static string Ok(object data)
        => JsonSerializer.Serialize(new { ok = true, data });

    private static string Err(string error)
        => JsonSerializer.Serialize(new { ok = false, error });

    private static uint GetUInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.TryGetUInt32(out var v) ? v : 0u;

    private static int GetInt(JsonElement root, string name, int defaultValue = 0)
        => root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? v : defaultValue;
}
