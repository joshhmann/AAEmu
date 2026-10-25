using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
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
///     → PlayerBotMetadataStore.RecordHome/RecordSchedule (B4 persistence:
///       the patrol home + schedule hit playerbot_metadata on mutation, so a
///       hard-kill restart re-arms the SAME route from the store)
///     → PlayerBotScheduler.Wake (the due-time loop drives the actor)
///
/// DISABLED BY DEFAULT. Enabled only when the runtime Config.Local.json sets
/// "Bots": { "EnablePresenceDemo": true } (or the AAEMU_PRESENCE_DEMO env var
/// is 1/true); prod config never sets it. Bot count:
/// "Bots"."PresenceBotCount" / AAEMU_PRESENCE_BOT_COUNT (default 3, clamped
/// 1..DefaultMaxPresenceBots for the demo).
///
/// ROSTER (G2-A6): when a presence manifest is configured
/// ("Bots"."PresenceManifest" / env AAEMU_PRESENCE_MANIFEST) the demo bots
/// come from that JSON roster instead of the hardcoded 3-citizen loop (name,
/// race, gender and level per entry, optional per-bot patrol home). Unset →
/// the legacy hardcoded path runs untouched. The same configurable bot-count
/// safety bound applies to a manifest roster ("Bots"."MaxPresenceBots" /
/// AAEMU_PRESENCE_MAX_BOTS, default 10 — raised from the old hardcoded 10-bot
/// clamp without changing its default shape).
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
    private readonly Func<IReadOnlyList<PresenceManifestEntry>?>? _manifestProvider;
    // The name of the bot character a managed account already owns (T2
    // reconciliation seam; null when the account owns none). Injectable so the
    // rig stays free of the provisioning service and MySQL.
    private readonly Func<string, string?> _existingBotName;

    public sealed record BotPresenceConfig(
        int BotCount,
        uint ZoneId,
        Vector3 HomePosition,
        float RoamRadius,
        float RoamSpeed,
        byte Level,
        string NamePrefix,
        string AccountPrefix,
        int MaxPresenceBots = DefaultMaxPresenceBots);

    /// <summary>
    /// What one enrollment run actually did (T2 supported arming) — reported,
    /// never assumed. <see cref="Enrolled"/> counts the bots that reached the
    /// world in this run: newly provisioned or adopted from a row an earlier
    /// boot created, so a restart reports its re-enrollment instead of a false
    /// zero. <see cref="Adopted"/> is the in-process re-enrollment subset (a
    /// bot already registered under the roster index's name). The idempotent
    /// skip reports <see cref="Enrolled"/> 0 with <see cref="AlreadyEmbodied"/>
    /// true, which is the one shape a caller must never read as "enrolled".
    /// </summary>
    public readonly record struct BotEnrollmentOutcome(int Enrolled, int Adopted, bool AlreadyEmbodied)
    {
        /// <summary>True when this run put at least one bot in the world.</summary>
        public bool Any => Enrolled > 0;
    }

    /// <summary>
    /// DI-friendly constructor. Tests inject fakes for the loop pieces and
    /// custom home/provision delegates; production uses the defaults.
    /// <paramref name="manifestProvider"/> returns the parsed G2-A6 roster
    /// when a manifest is configured, or null for the legacy hardcoded path
    /// (the production default resolves config/env and loads the file).
    /// </summary>
    public BotPresenceCoordinator(
        IPlayerBotManager manager,
        IPlayerBotScheduler scheduler,
        IPopulationDirector director,
        BotRoamStepExecutor stepExecutor,
        Func<BotPresenceConfig, Vector3>? homeResolver = null,
        Func<string, string, Race, Gender, byte, HeadlessSession>? provisioner = null,
        Func<Vector3, uint, float>? groundHeightProvider = null,
        Func<IReadOnlyList<PresenceManifestEntry>?>? manifestProvider = null,
        Func<string, string?>? existingBotName = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _homeResolver = homeResolver ?? DefaultHomeResolver;
        _provisioner = provisioner ?? DefaultProvisioner;
        _groundHeightProvider = groundHeightProvider ?? DefaultGroundHeightProvider;
        _manifestProvider = manifestProvider ?? DefaultManifestProvider;
        _existingBotName = existingBotName ?? DefaultExistingBotName;
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

    /// <summary>
    /// The demo's bot-count safety bound. Historically hardcoded as the
    /// 1..10 clamp in <see cref="ReadBotCount"/>; G2-A6 makes the UPPER bound
    /// configurable ("Bots"."MaxPresenceBots" / env AAEMU_PRESENCE_MAX_BOTS)
    /// while keeping 10 as the default so an unconfigured deployment behaves
    /// exactly as before.
    /// </summary>
    internal const int DefaultMaxPresenceBots = 10;

    /// <summary>Reads the bot count from env / config (clamped 1..max).</summary>
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

        return ClampBotCount(count, ReadMaxPresenceBots());
    }

    /// <summary>
    /// Reads the configurable upper bound of the demo bot clamp
    /// (AAEMU_PRESENCE_MAX_BOTS env wins, then "Bots"."MaxPresenceBots" in
    /// Config.Local.json / Config.json; default
    /// <see cref="DefaultMaxPresenceBots"/>). Chosen over raising the old
    /// hardcoded clamp so deployments that want a bigger manifest roster can
    /// opt in per-environment without touching shared prod config.
    /// </summary>
    public static int ReadMaxPresenceBots()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS");
        if (int.TryParse(env, out var envMax) && envMax > 0)
            return envMax;

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
                    bots.TryGetProperty("MaxPresenceBots", out var m) &&
                    m.TryGetInt32(out var cfgMax) && cfgMax > 0)
                {
                    return cfgMax;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "BotPresenceCoordinator: failed to read {Path}", path);
            }
        }

        return DefaultMaxPresenceBots;
    }

    /// <summary>Pure clamp core (hermetic-testable): 1..max, max floored at 1.</summary>
    internal static int ClampBotCount(int count, int max)
        => Math.Clamp(count, 1, Math.Max(1, max));

    /// <summary>
    /// Resolves the presence-manifest path (G2-A6): env AAEMU_PRESENCE_MANIFEST
    /// wins, then "Bots"."PresenceManifest" in Config.Local.json / Config.json.
    /// Null when unset → the legacy hardcoded citizen loop runs untouched.
    /// </summary>
    public static string? ReadManifestPath()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

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
                    bots.TryGetProperty("PresenceManifest", out var m) &&
                    m.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var configured = m.GetString();
                    if (!string.IsNullOrWhiteSpace(configured))
                        return configured;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "BotPresenceCoordinator: failed to read {Path}", path);
            }
        }

        return null;
    }

    /// <summary>
    /// Production manifest source: resolves the configured path and loads the
    /// roster; null (= legacy hardcoded path) when unset or the load fails.
    /// </summary>
    internal static IReadOnlyList<PresenceManifestEntry>? DefaultManifestProvider()
    {
        var path = ReadManifestPath();
        if (path == null)
            return null;
        return PresenceManifestLoader.TryLoad(path, out var entries)
            ? entries
            : null;
    }

    /// <summary>
    /// What the most recent <see cref="Start"/> did (T2 supported arming). A
    /// fresh value per run: the coordinator instance is created per boot, and
    /// <see cref="Start"/> resets it before it enrolls anything.
    /// </summary>
    public BotEnrollmentOutcome LastEnrollment { get; private set; }

    /// <summary>
    /// T2 supported-arming seam: runs the enrollment and reports what it did,
    /// so the deployment switch cannot claim an enrollment that did not
    /// happen (review D1).
    /// </summary>
    public BotEnrollmentOutcome Enroll(BotPresenceConfig config)
    {
        Start(config);
        return LastEnrollment;
    }

    /// <summary>
    /// Runs the presence demo: provisions <see cref="BotPresenceConfig.BotCount"/>
    /// bots, spawns them, assigns Full fidelity, and arms a bounded roam route
    /// around home. Idempotent: a second call skips when bots are already up.
    ///
    /// G2-A6: when a manifest provider is wired and yields a non-empty
    /// roster, the bots come from the manifest (per-entry name/race/gender/
    /// level/home); otherwise the legacy hardcoded citizen loop runs.
    /// </summary>
    public bool Start(BotPresenceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        LastEnrollment = default;

        if (_director.EmbodiedCount >= config.BotCount)
        {
            Logger.Info("BotPresenceCoordinator: {Count} bots already embodied — skipping (idempotent)",
                _director.EmbodiedCount);
            LastEnrollment = new BotEnrollmentOutcome(0, 0, AlreadyEmbodied: true);
            return true;
        }

        IReadOnlyList<PresenceManifestEntry>? roster = null;
        try
        {
            roster = _manifestProvider?.Invoke();
        }
        catch (Exception ex)
        {
            // Manifest resolution must never take the demo down — fall back
            // to the legacy hardcoded path (G2-A6 failure isolation).
            Logger.Error(ex, "BotPresenceCoordinator: manifest provider threw — falling back to the legacy citizen loop");
        }

        if (roster is { Count: > 0 })
            return StartFromManifest(config, roster);

        return StartLegacy(config);
    }

    /// <summary>
    /// The manifest-driven roster path (G2-A6): each entry is provisioned,
    /// spawned, activated, given Full fidelity, and walked on its bounded
    /// patrol route through the SAME flow as the legacy loop. A failing entry
    /// is isolated: it logs and the rest of the roster still comes up. The
    /// roster is clamped to <see cref="BotPresenceConfig.MaxPresenceBots"/>.
    /// </summary>
    private bool StartFromManifest(BotPresenceConfig config, IReadOnlyList<PresenceManifestEntry> roster)
    {
        var max = Math.Max(1, config.MaxPresenceBots);
        var clampedRoster = roster.Count > max ? roster.Take(max).ToList() : roster;
        if (clampedRoster.Count < roster.Count)
            Logger.Warn("BotPresenceCoordinator: manifest roster {Total} clamped to {Max} (MaxPresenceBots)",
                roster.Count, clampedRoster.Count);

        var defaultHome = _homeResolver(config);
        Logger.Info("BotPresenceCoordinator: provisioning {Count} manifest bots (home {Home}, zone {ZoneId})",
            clampedRoster.Count, defaultHome, config.ZoneId);

        _scheduler.Arm("presence-manifest");

        var spawned = 0;
        for (var i = 0; i < clampedRoster.Count; i++)
        {
            var entry = clampedRoster[i];
            var accountName = $"{BotAccountProvisioningService.ManagedUsernamePrefix}{config.AccountPrefix}_{i + 1:D3}";

            try
            {
                Logger.Info(
                    "BotPresenceCoordinator: manifest bot {Name} ({Race}/{Gender}, level {Level}{Class}{Persona})",
                    entry.Name, entry.Race, entry.Gender, entry.Level,
                    entry.ClassAbility is null ? "" : $", class {entry.ClassAbility}",
                    entry.Personality is null ? "" : $", personality {entry.Personality}");

                var session = _provisioner(accountName, entry.Name, entry.Race, entry.Gender, entry.Level);
                if (session == null)
                {
                    Logger.Error("BotPresenceCoordinator: provisioner returned null for {Name}", entry.Name);
                    continue;
                }

                var character = session.Character;
                // Same B4 home precedence as the legacy path, but with the
                // per-entry manifest home as the explicit override (entry home
                // > config home override > persisted metadata > ACTUAL SPAWN
                // POSITION). Soak stage-1 finding (a): falling back to the
                // default demo home here anchored the patrol route kilometers
                // from where race-template provisioning actually spawned the
                // bot — the route was unreachable and the bots walked until
                // they drowned. When nothing explicit/persisted exists, the
                // spawn position IS the home.
                var explicitHome = entry.Home ?? config.HomePosition;
                var metadata = PlayerBotMetadataStore.Instance.GetForRead(character.Id);
                var home = ResolveHome(explicitHome, metadata, character.Transform.Local.Position);
                // A persisted patrol home does not authorize a teleport from
                // the ordinary character position restored by provisioning.
                if (explicitHome != default)
                    character.Transform.Local.SetPosition(home.X, home.Y, home.Z);

                if (!_manager.Spawn(character, "presence-demo"))
                {
                    Logger.Warn("BotPresenceCoordinator: spawn refused for {Name} — already registered?", entry.Name);
                    continue;
                }

                if (!_manager.Activate(character.Id, new BotContext { BotId = character.Id, Name = entry.Name }, "presence-demo"))
                {
                    Logger.Warn("BotPresenceCoordinator: activation failed for {Name}", entry.Name);
                    continue;
                }

                var reduced = _director.TrySetFidelity(character.Id, BotFidelity.Reduced, "presence-demo");
                if (reduced != FidelityTransitionResult.Applied)
                    Logger.Warn("BotPresenceCoordinator: fidelity Reduced {Result} for {Name}", reduced, entry.Name);
                var full = _director.TrySetFidelity(character.Id, BotFidelity.Full, "presence-demo");
                if (full != FidelityTransitionResult.Applied)
                    Logger.Warn("BotPresenceCoordinator: fidelity Full {Result} for {Name}", full, entry.Name);

                // Per-entry zone: a manifest home.zoneId steers the terrain
                // probes; otherwise the bot's own transform zone (legacy shape).
                var route = BuildRoamRoute(home, config.RoamRadius, i,
                    entry.HomeZoneId ?? character.Transform.ZoneId, _groundHeightProvider);
                _stepExecutor.SetRoamRoute(character, route);

                var store = PlayerBotMetadataStore.Instance;
                store.RecordHome(character.Id, character.Transform.WorldId, character.Transform.ZoneId,
                    home.X, home.Y, home.Z);
                var roamScheduleJson = BuildRoamScheduleJson(home, route, config.RoamRadius, i);
                store.RecordSchedule(character.Id,
                    BotSchedulePayload.PreserveExtensions(metadata.Schedule, roamScheduleJson));

                _scheduler.Wake(character.Id);
                spawned++;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BotPresenceCoordinator: failed to provision manifest bot {Name}", entry.Name);
            }
        }

        Logger.Info("BotPresenceCoordinator: presence demo up — {Spawned}/{Count} manifest bots roaming",
            spawned, clampedRoster.Count);
        LastEnrollment = new BotEnrollmentOutcome(spawned, 0, AlreadyEmbodied: false);
        return spawned > 0;
    }

    /// <summary>
    /// The LEGACY hardcoded demo loop (3 Nuian citizens by default) — kept
    /// byte-for-byte in behavior for deployments without a manifest.
    /// </summary>
    private bool StartLegacy(BotPresenceConfig config)
    {
        var defaultHome = _homeResolver(config);
        Logger.Info("BotPresenceCoordinator: provisioning {Count} citizen bots (home {Home}, zone {ZoneId})",
            config.BotCount, defaultHome, config.ZoneId);

        _scheduler.Arm("presence-demo");

        var spawned = 0;
        var adopted = 0;
        for (var i = 0; i < config.BotCount; i++)
        {
            var accountName = $"{BotAccountProvisioningService.ManagedUsernamePrefix}{config.AccountPrefix}_{i + 1:D3}";
            // The roster index's identity is its managed account (T2, audit
            // L1-e): when that account already owns a bot character, this
            // index re-enrols THAT character under the name it already has.
            // Generating a fresh name instead would step past the bot's own
            // registered name and provision a SECOND character for the same
            // index on every restart. Names are only generated for an index
            // whose account owns nothing yet; the name comes from the
            // data-backed pool, deterministic in the roster index.
            var name = _existingBotName(accountName)
                ?? BotNameGenerator.NameFor(i, config.NamePrefix, ProductionNameProbe);

            try
            {
                // In-process reconciliation: a bot already enrolled under this
                // name (a second Start with a larger count, or a re-arm) is
                // re-enrolled instead of provisioned again.
                var enrolled = FindEnrolledByName(name);
                if (enrolled is not null)
                {
                    if (ReEnroll(enrolled, i, config, defaultHome, name))
                        adopted++;
                    continue;
                }

                var session = _provisioner(accountName, name, Race.Nuian, Gender.Male, config.Level);
                if (session == null)
                {
                    Logger.Error("BotPresenceCoordinator: provisioner returned null for {Name}", name);
                    continue;
                }

                var character = session.Character;
                // B4 home resolution (playerbot_metadata store): an explicit
                // env override wins; else the bot's PERSISTED patrol home;
                // else the resolver default (template spawn in production).
                // Home anchors the patrol, not the saved character position.
                // Fire-and-forget —
                // the store never throws (no DB → empty metadata → default).
                var metadata = PlayerBotMetadataStore.Instance.GetForRead(character.Id);
                var home = ResolveHome(config.HomePosition, metadata, defaultHome);
                // Patrol-home relocation (t_118484a7 scope-add): when a home
                // override is configured, place the bot there as requested.
                // A stored patrol home alone must not replace the ordinary
                // Character.Load position on restart and make the bot walk
                // back to its quest from the starter area.
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

                ApplyFullFidelity(character.Id, name);

                // Bounded patrol route around home — offset each bot's start so
                // they don't all walk the same circle in lockstep. Waypoint Z is
                // terrain-aware (probing the same heightmap the executor clamps
                // against, via the bot's own zone) so every leg can actually
                // arrive (t_d7e45251 wedge fix).
                var route = BuildRoamRoute(home, config.RoamRadius, i, character.Transform.ZoneId, _groundHeightProvider);
                _stepExecutor.SetRoamRoute(character, route);

                // B4 metadata persistence (M6 deferred gate #5): write the
                // home actually used + the roam schedule through to
                // playerbot_metadata NOW (write-through; the E2E restarts are
                // hard kills, so shutdown-time persistence would lose them).
                // Fire-and-forget: the store never throws — a DB failure is
                // logged and the row stays dirty for the SaveManager tick.
                var store = PlayerBotMetadataStore.Instance;
                store.RecordHome(character.Id, character.Transform.WorldId, character.Transform.ZoneId,
                    home.X, home.Y, home.Z);
                // C1 Schedules v1 (additive hook): when BotScheduleService
                // has recorded daily-anchor/last-phase extensions into this
                // bot's schedule JSON, carry them across this re-record so
                // they survive a re-provision. With no extensions present
                // (the default and the B4 E2E shape) the payload passes
                // through VERBATIM — byte-equal restart snapshots hold.
                var roamScheduleJson = BuildRoamScheduleJson(home, route, config.RoamRadius, i);
                store.RecordSchedule(character.Id,
                    BotSchedulePayload.PreserveExtensions(metadata.Schedule, roamScheduleJson));

                _scheduler.Wake(character.Id);
                spawned++;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BotPresenceCoordinator: failed to provision {Name}", name);
            }
        }

        Logger.Info("BotPresenceCoordinator: presence demo up — {Spawned}/{Count} citizen bots roaming ({Adopted} re-enrolled)",
            spawned, config.BotCount, adopted);
        LastEnrollment = new BotEnrollmentOutcome(spawned + adopted, adopted, AlreadyEmbodied: false);
        // A re-enrolled bot counts as the roster coming up: the caller's
        // question is "are the configured bots in the world", not "did we
        // create rows this time" (T2 supported arming reports this result).
        return spawned > 0 || adopted > 0;
    }

    /// <summary>
    /// Production seam for <see cref="StartLegacy"/>'s name resolution: the
    /// name of the bot character this managed account already owns, or null
    /// when it owns none. The account row is provisioned idempotently here
    /// (the same call the provisioner makes a moment later) and its characters
    /// are read back from the game DB, so a restart re-adopts the existing bot
    /// instead of adding a second character to the same account (audit L1-e).
    /// </summary>
    private static string? DefaultExistingBotName(string accountName)
    {
        try
        {
            var account = BotAccountProvisioningService.Instance.ProvisionBotAccount(accountName);
            var owned = CharacterManager.LoadCharacters(account.AccountId);
            if (SelectOldestOwned(owned.Select(c => (c.Id, c.Name)).ToList()) is not { } selected)
                return null;

            // More than one row means a previous boot provisioned duplicates
            // (the pre-reconciliation behaviour); they are reported, never
            // silently enrolled. The rows left unenrolled are the NEWER ones —
            // the oldest is the one being re-enrolled (review D5).
            if (selected.Residue > 0)
                Logger.Warn("BotPresenceCoordinator: managed account {Account} owns {Count} characters — re-enrolling the oldest ({Name}); {Residue} newer duplicate row(s) stay unenrolled",
                    accountName, owned.Count, selected.Name, selected.Residue);
            return selected.Name;
        }
        catch (Exception ex)
        {
            // Name resolution must never take the demo down: fall back to the
            // generator (the pre-T2 behaviour).
            Logger.Warn(ex, "BotPresenceCoordinator: could not read the existing bot name for {Account} — generating a new one", accountName);
            return null;
        }
    }

    /// <summary>
    /// Pure selection seam for <see cref="DefaultExistingBotName"/> (T2, audit
    /// L1-e): the roster index re-enrols the OLDEST character its managed
    /// account owns, and reports how many newer duplicate rows stay
    /// unenrolled. Null when the account owns nothing. Split out so the rule
    /// is unit-testable without the account/character store.
    /// </summary>
    internal static (string Name, int Residue)? SelectOldestOwned(IReadOnlyList<(uint Id, string Name)> owned)
    {
        if (owned.Count == 0)
            return null;

        var oldest = owned.OrderBy(row => row.Id).First();
        return (oldest.Name, owned.Count - 1);
    }

    /// <summary>
    /// The enrolled bot holding <paramref name="name"/> (case-insensitive), or
    /// null. Mirrors BotAdminService.FindByName: <see cref="IPlayerBotManager"/>
    /// has no by-name lookup.
    /// </summary>
    private PlayerBotRuntime? FindEnrolledByName(string name)
        => _manager.GetAll().FirstOrDefault(r =>
            r.Character is { } character && character.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Re-enrols an already-registered bot for a roster index (T2, audit
    /// L1-e): brings it back to Active when it was left deactivated, re-arms
    /// its patrol route from its resolved home, re-records home and schedule
    /// and wakes it. No second character row and no second account.
    /// </summary>
    private bool ReEnroll(PlayerBotRuntime runtime, int index, BotPresenceConfig config, Vector3 defaultHome, string name)
    {
        var character = runtime.Character;
        var metadata = PlayerBotMetadataStore.Instance.GetForRead(character.Id);
        var home = ResolveHome(config.HomePosition, metadata, defaultHome);
        // Re-enrollment preserves current location unless explicitly relocated.
        if (config.HomePosition != default)
            character.Transform.Local.SetPosition(home.X, home.Y, home.Z);

        var previousState = runtime.State;
        if (previousState != PlayerBotState.Active &&
            !_manager.Activate(character.Id, new BotContext { BotId = character.Id, Name = name }, "presence-demo"))
        {
            Logger.Warn("BotPresenceCoordinator: re-enrollment activation failed for {Name} (state {State})", name, previousState);
            return false;
        }

        if (previousState != PlayerBotState.Active)
            ApplyFullFidelity(character.Id, name);

        var route = BuildRoamRoute(home, config.RoamRadius, index, character.Transform.ZoneId, _groundHeightProvider);
        _stepExecutor.SetRoamRoute(character, route);

        var store = PlayerBotMetadataStore.Instance;
        store.RecordHome(character.Id, character.Transform.WorldId, character.Transform.ZoneId,
            home.X, home.Y, home.Z);
        store.RecordSchedule(character.Id,
            BotSchedulePayload.PreserveExtensions(metadata.Schedule, BuildRoamScheduleJson(home, route, config.RoamRadius, index)));

        _scheduler.Wake(character.Id);
        Logger.Info("BotPresenceCoordinator: re-enrolled {Name} (id {Id}, state was {State}) — adopted, no new provision",
            name, character.Id, previousState);
        return true;
    }

    /// <summary>
    /// Full fidelity for the demo set (single-step ladder: Dormant → Reduced →
    /// Full). Shared by the fresh enrollment path and the reconciliation path
    /// so both leave the bot exactly as visible to a human nearby.
    /// </summary>
    private void ApplyFullFidelity(uint characterId, string name)
    {
        var reduced = _director.TrySetFidelity(characterId, BotFidelity.Reduced, "presence-demo");
        if (reduced != FidelityTransitionResult.Applied)
            Logger.Warn("BotPresenceCoordinator: fidelity Reduced {Result} for {Name}", reduced, name);
        var full = _director.TrySetFidelity(characterId, BotFidelity.Full, "presence-demo");
        if (full != FidelityTransitionResult.Applied)
            Logger.Warn("BotPresenceCoordinator: fidelity Full {Result} for {Name}", full, name);
    }

    /// <summary>
    /// Production "is this character name already taken?" probe — the live
    /// NameManager registry, which is loaded from the characters table at boot.
    /// Injected into <see cref="BotNameGenerator.NameFor"/> so name assignment
    /// itself stays a pure, hermetic function of the roster index. Only reached
    /// for an index whose managed account owns no bot character yet (T2
    /// reconciliation resolves the existing name first).
    /// </summary>
    private static bool ProductionNameProbe(string name)
        => NameManager.Instance.GetCharacterId(name) != 0;

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
    /// B4 home precedence (pure, hermetic-testable): an EXPLICIT home (the
    /// AAEMU_PRESENCE_HOME_X/Y/Z override) always wins; else the bot's
    /// PERSISTED home from playerbot_metadata (the restart re-arm); else the
    /// resolver default (Nuian template spawn in production).
    /// </summary>
    internal static Vector3 ResolveHome(Vector3 explicitHome, PlayerBotMetadata stored, Vector3 templateHome)
    {
        if (explicitHome != default)
            return explicitHome;
        if (stored is { HasHome: true })
            return new Vector3(stored.HomeX, stored.HomeY, stored.HomeZ);
        return templateHome;
    }

    /// <summary>
    /// The deterministic B4 schedule payload recorded next to the patrol
    /// home: a roam-loop descriptor (kind / waypoint count / radius /
    /// per-bot phase / loop flag) plus the home and waypoint coordinates.
    /// Rebuilt identically on every boot from the same home + seed, so the
    /// pre/post-restart store snapshots compare EQUAL.
    /// </summary>
    internal static string BuildRoamScheduleJson(Vector3 home, BotPath route, float radius, int phase)
    {
        var path = new float[route.Waypoints.Count][];
        for (var i = 0; i < path.Length; i++)
            path[i] = new[] { route.Waypoints[i].X, route.Waypoints[i].Y, route.Waypoints[i].Z };

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "roam-loop",
            waypoints = route.Waypoints.Count,
            radius,
            phase,
            loop = route.Mode == BotPath.LoopMode.Loop,
            home = new[] { home.X, home.Y, home.Z },
            path
        });
    }

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
