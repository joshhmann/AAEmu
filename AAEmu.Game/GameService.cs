using System.Diagnostics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Commons.Utils.Updater;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Network.Stream;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.IO;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Utils.Scripts;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NLog;

namespace AAEmu.Game;

public sealed class GameService : IHostedService, IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static TimeProvider s_timeProvider = TimeProvider.System;
    public static DateTime StartTime { get; private set; } = DateTime.UtcNow;
    public static TimeSpan TimeSinceStart => s_timeProvider.GetUtcNow().UtcDateTime.Subtract(StartTime);

    private readonly ManagerOrchestrator _orchestrator;

    public GameService(IServiceProvider serviceProvider, ManagerOrchestrator orchestrator, TimeProvider timeProvider)
    {
        SingletonContainer.ServiceProvider = serviceProvider;
        _orchestrator = orchestrator;
        s_timeProvider = timeProvider;
        StartTime = timeProvider.GetUtcNow().UtcDateTime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Info("Starting daemon: AAEmu.Game");

        // Boot-time profile (perf/e2e-speed): cumulative wall-clock stamps at
        // each startup phase boundary; the orchestrator logs per-manager
        // detail inside its batches. Pure instrumentation — no behavior change.
        var bootProfile = Stopwatch.StartNew();
        void BootPhase(string name) =>
            Logger.Info($"[boot-profile] phase '{name}' at {bootProfile.Elapsed.TotalSeconds:F2}s");

        BootPhase("process start");

        // Check for updates
        using (var connection = MySQL.CreateConnection())
        {
            if (!MySqlDatabaseUpdater.Run(connection, "aaemu_game", AppConfiguration.Instance.Connections.MySQLProvider.Database,
                    AppConfiguration.Instance.Connections.AutoApplyUpdates))
            {
                Logger.Fatal("Failed to update database!");
                Logger.Fatal("Press Ctrl+C to quit");
                return;
            }
        }

        BootPhase("db schema check");

        ClientFileManager.Initialize();
        if (ClientFileManager.Sources.Count == 0)
        {
            Logger.Fatal($"Failed up load client files! ({string.Join(", ", AppConfiguration.Instance.ClientData.Sources)})");
            Logger.Fatal("Press Ctrl+C to quit");
            return;
        }

        BootPhase("client data sources");

        var stopWatch = new Stopwatch();
        stopWatch.Start();

        // --- Auto-Attack system: ensure compact.sqlite3 has the 6 weapon-anim columns ---
        // The shipped ArcheAge compact.sqlite3 already contains these. This is a defensive
        // check for stripped/custom DBs and is a no-op if the columns are already present.
        Utils.DB.HoldablesSchemaCheck.EnsureColumns();

        // --- ID managers ---
        // All ID managers implement ILoadable and are handled by the orchestrator in Stage 2.
        // SkillTlIdManager.Instance.Initialize(); // static class, not migrated
        FormulaManager.Instance.Load();
        ItemManager.Instance.Load();
        ItemManager.Instance.LoadUserItems();

        BootPhase("formula/item direct loads");

        // --- Stage 2: Orchestrated parallel Load() ---
        // Managers implementing ILoadable are sorted by constructor dep graph and run in parallel batches.
        await _orchestrator.RunLoadAsync();

        BootPhase("orchestrated Load() batches");

        // --- Stage 3: Post-load special steps ---
        GameDataManager.Instance.PostLoadGameData();
        CashShopManager.Instance.EnabledShop();

        BootPhase("post-load game data");

        // --- Scripts ---
        if (AppConfiguration.Instance.Scripts.LoadStrategy == ScriptsConfig.LoadStrategyType.Compilation)
        {
            ScriptCompiler.Compile();
        }
        else
        {
            // (Preferred for debugging)
            // Use reflection to load scripts
            ScriptReflector.Reflect();
        }

        TimeManager.Instance.Start();
        TaskManager.Instance.Start();

        BootPhase("scripts + time/task managers");

        // --- Stage 4: Orchestrated parallel Initialize() ---
        await _orchestrator.RunInitializeAsync();

        BootPhase("orchestrated Initialize() batches");

        // --- Stage 5: World creation + network ---
        // Start main_world and other static instances
        WorldManager.Instance.CreateStaticInstances();
        WorldManager.Instance.Initialize();

        BootPhase("world creation");

        CharacterManager.Instance.CheckForDeletedCharacters();
        CharacterManager.Instance.StartOnlineTracking();

        GameNetwork.Instance.Start();
        StreamNetwork.Instance.Start();
        LoginNetwork.Instance.Start();

        BootPhase("network bind");

        stopWatch.Stop();
        Logger.Info($"Server started! Took {stopWatch.Elapsed}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Info("Stopping daemon...");

        // A1 converging teardown: disarm the bot host FIRST, while the world
        // and character managers are still alive, so every enrolled bot leaves
        // through its ordinary leave-save and the queued steps the stopped
        // game loop can no longer run are dropped with a record. Never fatal:
        // a partial teardown is logged with its failures.
        DisarmBotHost("host-shutdown");

        await SaveManager.Instance.StopAsync();

        // SpawnManager.Instance.Stop(); Moved to World Instance
        TaskManager.Instance.Stop();
        GameNetwork.Instance.Stop();
        StreamNetwork.Instance.Stop();
        LoginNetwork.Instance.Stop();

        /*
        HousingManager.Instance.Save();
        MailManager.Instance.Save();
        ItemManager.Instance.Save();
        */
        AIManager.Instance.Stop();
        WorldManager.Instance.Stop();

        TickManager.Instance.Stop();
        TimeManager.Instance.Stop();

        ClientFileManager.ClearSources();
    }

    /// <summary>
    /// A1 converging teardown: hand the bot host to its single owner
    /// (<see cref="IPlayerBotScheduler.Disarm"/>) so a stopping server never
    /// leaves a bot Active with its native work abandoned and never drops
    /// queued steps without a record. A no-op when the bot seam was never
    /// wired; a partial teardown is logged with its failures, never swallowed.
    /// </summary>
    private static void DisarmBotHost(string reason)
    {
        var scheduler = SingletonContainer.ServiceProvider?.GetService<IPlayerBotScheduler>();
        if (scheduler is null)
            return;

        try
        {
            var report = scheduler.Disarm(reason);
            if (!report.Converged)
                Logger.Warn("Bot host teardown did NOT converge: {Report}", report);
            else if (report.SchedulerWasRunning || report.BotsDeactivated > 0 || report.QueuedStepsDropped > 0)
                Logger.Info("Bot host disarmed on shutdown: {Report}", report);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Bot host disarm failed during shutdown");
        }
    }

    public void Dispose()
    {
        Logger.Info("Disposing...");

        LogManager.Flush();
    }
}
