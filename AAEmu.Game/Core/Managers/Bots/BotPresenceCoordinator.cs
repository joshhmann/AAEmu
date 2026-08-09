using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// PRESENCE PROOF coordinator (integration card t_6bad0654) — wires the
/// proven pieces into one living loop:
///
///   HeadlessSession.Provision (production path: real account + character
///   rows, ActivateHeadless embodiment)
///     → PlayerBotManager.Spawn/Activate (real lifecycle adapter — region
///       placement included)
///     → PopulationDirector.TrySetFidelity(Full) (the demo set gets full
///       presence)
///     → BotRoamStepExecutor.SetRoamRoute (BotPath loop around the spawn)
///     → PlayerBotScheduler.Wake (the due-time loop drives the actor)
///
/// DISABLED BY DEFAULT. Enabled only when the runtime Config.Local.json sets
/// "Bots": { "EnablePresenceDemo": true } (or the AAEMU_PRESENCE_DEMO env var
/// is 1/true); prod config never sets it. Bot count:
/// "Bots"."PresenceBotCount" / AAEMU_PRESENCE_BOT_COUNT (default 3, clamped
/// 1..10 for the demo).
///
/// The coordinator runs once the world is ready (main world loaded + spawn
/// templates available). All bots are provisioned through the production
/// path and walked on a bounded patrol route around their spawn position —
/// Option A visibility (ground clamp + 4-6 Hz movement broadcast) lives in
/// the step executor.
/// </summary>
public sealed class BotPresenceCoordinator
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotManager _manager;
    private readonly IPlayerBotScheduler _scheduler;
    private readonly IPopulationDirector _director;
    private readonly BotRoamStepExecutor _stepExecutor;
    private readonly Func<BotPresenceConfig, Vector3> _homeResolver;
    private readonly Func<string, string, Race, Gender, byte, HeadlessSession> _provisioner;
    private readonly Func<Vector3, uint, float> _groundHeightProvider;

    public sealed record BotPresenceConfig(
        int BotCount,
        uint ZoneId,
        Vector3 HomePosition,
        float RoamRadius,
        float RoamSpeed,
        byte Level,
        string NamePrefix,
        string AccountPrefix);

    /// <summary>
    /// DI-friendly constructor. Tests inject fakes for the loop pieces and
    /// custom home/provision delegates; production uses the defaults.
    /// </summary>
    public BotPresenceCoordinator(
        IPlayerBotManager manager,
        IPlayerBotScheduler scheduler,
        IPopulationDirector director,
        BotRoamStepExecutor stepExecutor,
        Func<BotPresenceConfig, Vector3>? homeResolver = null,
        Func<string, string, Race, Gender, byte, HeadlessSession>? provisioner = null,
        Func<Vector3, uint, float>? groundHeightProvider = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _homeResolver = homeResolver ?? DefaultHomeResolver;
        _provisioner = provisioner ?? DefaultProvisioner;
        _groundHeightProvider = groundHeightProvider ?? DefaultGroundHeightProvider;
    }

    /// <summary>
    /// Reads the runtime config gate. True only when the demo is explicitly
    /// enabled (Config.Local.json "Bots"."EnablePresenceDemo" or env).
    /// </summary>
    public static bool IsEnabled()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_DEMO");
        if (env is "1" or "true" or "True")
            return true;

        foreach (var fileName in new[] { "Config.Local.json", "Config.json" })
        {
            var path = Path.Combine(FileManager.AppPath, fileName);
            if (!File.Exists(path))
                continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Bots", out var bots) &&
                    bots.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    bots.TryGetProperty("EnablePresenceDemo", out var flag) &&
                    flag.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "BotPresenceCoordinator: failed to read {Path}", path);
            }
        }

        return false;
    }

    /// <summary>Logs a warning through the coordinator logger (bootstrap/DI helper).</summary>
    public static void LogWarn(string message)
        => Logger.Warn(message);

    /// <summary>Logs an error through the coordinator logger (bootstrap/DI helper).</summary>
    public static void LogError(Exception ex, string message)
        => Logger.Error(ex, message);

    /// <summary>
    /// Height above home.Z the route builder probes terrain from. The
    /// heightmap call (WorldManager.GetHeight → GeoData) raycasts DOWNWARD
    /// from the given Z; probing from home.Z misses when the terrain floor
    /// sits ABOVE the flat route height (prod: floor 128–135 vs home
    /// 126.484), the heightmap fallback returns 0 for the same cells, and
    /// the waypoint falls back to flat home.Z → the bot can never arrive
    /// there (the t_d7e45251 wedge — reproduced on prod 2026-08-08: all 3
    /// bots frozen at the 135° waypoint, clamped Z 127.98 vs route Z
    /// 126.484). Probing from above the floor makes the raycast hit the
    /// same surface the executor's live clamp hits. 50m covers any
    /// plausible relief inside a 27m patrol circle.
    /// </summary>
    internal const float TerrainProbeMargin = 50f;

    /// <summary>Reads the bot count from env / config (clamped 1..10).</summary>
    public static int ReadBotCount(int fallback = 3)
    {
        var count = fallback;
        var env = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT");
        if (int.TryParse(env, out var envCount) && envCount > 0)
            count = envCount;

        foreach (var fileName in new[] { "Config.Local.json", "Config.json" })
        {
            var path = Path.Combine(FileManager.AppPath, fileName);
            if (!File.Exists(path))
                continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Bots", out var bots) &&
                    bots.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    bots.TryGetProperty("PresenceBotCount", out var c) &&
                    c.TryGetInt32(out var cfgCount) && cfgCount > 0)
                {
                    count = cfgCount;
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "BotPresenceCoordinator: failed to read {Path}", path);
            }
        }

        return Math.Clamp(count, 1, 10);
    }

    /// <summary>
    /// Runs the presence demo: provisions <see cref="BotPresenceConfig.BotCount"/>
    /// bots, spawns them, assigns Full fidelity, and arms a bounded roam route
    /// around home. Idempotent: a second call skips when bots are already up.
    /// </summary>
    public bool Start(BotPresenceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_director.EmbodiedCount >= config.BotCount)
        {
            Logger.Info("BotPresenceCoordinator: {Count} bots already embodied — skipping (idempotent)",
                _director.EmbodiedCount);
            return true;
        }

        var home = _homeResolver(config);
        Logger.Info("BotPresenceCoordinator: provisioning {Count} citizen bots (home {Home}, zone {ZoneId})",
            config.BotCount, home, config.ZoneId);

        _scheduler.Start();

        var spawned = 0;
        for (var i = 0; i < config.BotCount; i++)
        {
            var accountName = $"{BotAccountProvisioningService.ManagedUsernamePrefix}{config.AccountPrefix}_{i + 1:D3}";
            var name = $"{config.NamePrefix}{i + 1:D2}";

            try
            {
                var session = _provisioner(accountName, name, Race.Nuian, Gender.Male, config.Level);
                if (session == null)
                {
                    Logger.Error("BotPresenceCoordinator: provisioner returned null for {Name}", name);
                    continue;
                }

                var character = session.Character;
                // Patrol-home relocation (t_118484a7 scope-add): when a home
                // override is configured, embody the bot AT the override
                // position (not the template spawn) so a logging-in human at
                // that spot sees the bots instantly. The adapter's
                // AddVisibleObject placement then registers the bot in the
                // home's region graph. No-op when the override is unset
                // (template spawn — the default demo layout).
                if (config.HomePosition != default)
                    character.Transform.Local.SetPosition(home.X, home.Y, home.Z);

                if (!_manager.Spawn(character, "presence-demo"))
                {
                    Logger.Warn("BotPresenceCoordinator: spawn refused for {Name} — already registered?", name);
                    continue;
                }

                if (!_manager.Activate(character.Id, new BotContext { BotId = character.Id, Name = name }, "presence-demo"))
                {
                    Logger.Warn("BotPresenceCoordinator: activation failed for {Name}", name);
                    continue;
                }

                // Full fidelity for the demo set (single-step ladder: Dormant → Reduced → Full).
                var reduced = _director.TrySetFidelity(character.Id, BotFidelity.Reduced, "presence-demo");
                if (reduced != FidelityTransitionResult.Applied)
                    Logger.Warn("BotPresenceCoordinator: fidelity Reduced {Result} for {Name}", reduced, name);
                var full = _director.TrySetFidelity(character.Id, BotFidelity.Full, "presence-demo");
                if (full != FidelityTransitionResult.Applied)
                    Logger.Warn("BotPresenceCoordinator: fidelity Full {Result} for {Name}", full, name);

                // Bounded patrol route around home — offset each bot's start so
                // they don't all walk the same circle in lockstep. Waypoint Z is
                // terrain-aware (probing the same heightmap the executor clamps
                // against, via the bot's own zone) so every leg can actually
                // arrive (t_d7e45251 wedge fix).
                var route = BuildRoamRoute(home, config.RoamRadius, i, character.Transform.ZoneId, _groundHeightProvider);
                _stepExecutor.SetRoamRoute(character, route);

                _scheduler.Wake(character.Id);
                spawned++;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BotPresenceCoordinator: failed to provision {Name}", name);
            }
        }

        Logger.Info("BotPresenceCoordinator: presence demo up — {Spawned}/{Count} citizen bots roaming",
            spawned, config.BotCount);
        return spawned > 0;
    }

    /// <summary>
    /// Builds a bounded patrol route (Loop) around home: a rounded square with
    /// per-bot angular offset so bots visibly spread. All waypoints lie within
    /// RoamRadius of home (BotPath.AllWaypointsWithin safety contract).
    /// </summary>
    /// <param name="home">Route center (the bot's spawn/home position).</param>
    /// <param name="radius">Flat radius of the patrol circle.</param>
    /// <param name="seed">Per-bot phase offset (degrees, 45° steps).</param>
    /// <param name="zoneId">Bot's zone (the same ZoneId the executor clamp uses).</param>
    /// <param name="groundHeightProvider">Terrain probe; 0 = no heightmap data.</param>
    internal static BotPath BuildRoamRoute(Vector3 home, float radius, int seed,
        uint zoneId = 0, Func<Vector3, uint, float>? groundHeightProvider = null)
    {
        var heightProvider = groundHeightProvider ?? DefaultGroundHeightProvider;
        var waypoints = new List<Vector3>();
        var startAngle = seed * 45f; // per-bot offset so they don't sync
        for (var i = 0; i < 8; i++)
        {
            var angle = (startAngle + i * 45f).DegToRad();
            var x = home.X + MathF.Cos(angle) * radius * 0.9f;
            var y = home.Y + MathF.Sin(angle) * radius * 0.9f;

            // Terrain-aware waypoint Z (t_d7e45251 wedge fix): the roam
            // executor ground-clamps Z to the heightmap every step, so a
            // waypoint built flat at home.Z can never be "arrived at" when
            // terrain deviates > ArrivalRadius from home.Z (prod: 8.68m gap
            // at the 315° waypoint → leg timeout → bot frozen at the
            // waypoint). Probe the terrain at build time, from ABOVE the
            // terrain (home.Z + TerrainProbeMargin) — GetHeight's geodata
            // raycast searches downward, so probing from the flat route Z
            // misses when the floor sits above it and the heightmap
            // fallback returns 0 for the same cells; 0 = still no data →
            // keep home.Z (the executor's 0 = skip-clamp convention, so a
            // data-less zone is no worse than before).
            var terrainZ = heightProvider(new Vector3(x, y, home.Z + TerrainProbeMargin), zoneId);
            waypoints.Add(new Vector3(x, y, terrainZ != 0f ? terrainZ : home.Z));
        }

        return new BotPath(waypoints, BotPath.LoopMode.Loop, BotPath.ArrivalRadiusDefault, radius * 0.2f);
    }

    /// <summary>
    /// Default terrain probe: the same WorldManager heightmap call the roam
    /// executor uses for its step-3a clamp (GetReferenceHeight with a null
    /// AI — the plain terrain-height branch), so route Z and clamped Z come
    /// from the same source.
    /// </summary>
    internal static float DefaultGroundHeightProvider(Vector3 position, uint zoneId)
        => WorldManager.Instance.GetReferenceHeight(null, position.X, position.Y, position.Z, zoneId);

    /// <summary>
    /// Default home: the Nuian character template spawn (Solzreed — the
    /// known-good world; the same position a fresh player character appears
    /// at, so the bots are immediately visible to a logging-in human).
    /// An EXPLICIT <see cref="BotPresenceConfig.HomePosition"/> (the
    /// env-driven patrol-home override, AAEMU_PRESENCE_HOME_X/Y/Z) wins over
    /// the template spawn — that is how the demo patrol is relocated to a
    /// specific player's spawn (t_118484a7 scope-add: bots AT Josh's
    /// position so the sighting is instant).
    /// </summary>
    internal static Vector3 DefaultHomeResolver(BotPresenceConfig config)
    {
        if (config.HomePosition != default)
            return config.HomePosition;

        var template = UnitManagers.CharacterManager.Instance.GetTemplate(Race.Nuian, Gender.Male);
        var spawn = template?.SpawnPosition;
        return spawn != null
            ? new Vector3(spawn.X, spawn.Y, spawn.Z)
            : config.HomePosition;
    }

    /// <summary>
    /// Reads the optional patrol-home override from the environment
    /// (AAEMU_PRESENCE_HOME_X/Y/Z — the demo-patrol relocation knob,
    /// t_118484a7). Returns default (Vector3.Zero) when unset or partial —
    /// the coordinator then falls back to the template spawn.
    /// </summary>
    public static Vector3 ReadHomePosition()
    {
        var x = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_HOME_X");
        var y = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_HOME_Y");
        var z = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_HOME_Z");
        if (float.TryParse(x, out var fx) && float.TryParse(y, out var fy) && float.TryParse(z, out var fz))
            return new Vector3(fx, fy, fz);
        return default;
    }

    private static HeadlessSession DefaultProvisioner(string username, string name, Race race, Gender gender, byte level)
    {
        // P1 t_61814965: citizens are born through the appearance factory —
        // the same shape a player create produces (randomized-but-valid Face
        // params + per-class starting equipment). The seed is the stable FNV
        // hash of the NAME: every citizen keeps the SAME born look across
        // reboots while differing from every other citizen.
        var spec = new BotAppearanceSpec(race, gender, Seed: BotAppearanceFactory.Fnv1a(name), Name: name);
        return HeadlessSession.Provision(username, spec, level);
    }
}
