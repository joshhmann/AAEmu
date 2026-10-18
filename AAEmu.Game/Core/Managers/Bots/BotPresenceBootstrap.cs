using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// PRESENCE PROOF bootstrap (integration card t_6bad0654) — starts
/// <see cref="BotPresenceCoordinator"/> when the game server boots with the
/// presence demo enabled (Config.Local.json "Bots"."EnablePresenceDemo" or
/// AAEMU_PRESENCE_DEMO env), and — T2 supported arming — arms and enrolls the
/// configured roster when the deployment sets "Bots"."EnableStartupArming"
/// (or AAEMU_STARTUP_ARMING). Follows the BotDriveBridge precedent: runs at
/// assembly load, waits for the DI container, then waits for the world to be
/// ready before provisioning bots. When every switch is off (the shipped
/// default) it is a strict no-op: no thread, no socket, no DB writes.
/// </summary>
internal static class BotPresenceBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        var runPresence = BotPresenceCoordinator.IsEnabled();
        var runAutoRestore = BotAdminService.IsAutoRestoreEnabled();
        var runStartupArming = BotStartupArmingOptions.ReadEnabledFlag();
        if (!runPresence && !runAutoRestore && !runStartupArming)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for DI (world managers are registered during Program init).
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                if (SingletonContainer.ServiceProvider == null)
                    return;

                // Wait for the main world + character templates (spawn position)
                // + FULL world wiring. The MateManager guard mirrors
                // BotProvisioningControlHost (run-8): EnterWorld iterates ALL
                // worlds and NREs on any world whose MateManager is still null
                // (assigned moments after world creation, WorldManager.cs:528).
                // Window: 300s (3000 × 100ms). A cold boot took 55s+ on the
                // E2E stack and 51s on prod (2026-08-08 t_118484a7 re-run:
                // the old 60s window aborted 1s before world wiring completed,
                // leaving 0/3 bots provisioned — a slow MySQL warm-up would
                // silently kill the presence demo on prod too).
                var worldReady = false;
                for (var i = 0; i < 3000 && !worldReady; i++)
                {
                    var worlds = WorldManager.Instance.GetWorlds();
                    // Non-logging probe: GetWorld(DefaultInstanceId) logs [FATAL]
                    // per miss, and the world is polled every 100ms while it is
                    // still being created (~146 FATAL boot lines on the
                    // presence-enabled build). The worlds array is keyed by
                    // WorldInstance.Id (WorldManager.cs:491), so Any() over the
                    // same array is exactly equivalent — minus the log. The
                    // empty-table case reads as not-ready too (Any() is false),
                    // mirroring BotProvisioningControlHost's run-9 lesson.
                    worldReady = worlds.Any(w => w.Id == WorldManager.DefaultInstanceId)
                        && worlds is { Length: > 0 }
                        && worlds.All(w => w.MateManager != null)
                        && UnitManagers.CharacterManager.Instance.GetTemplate(Race.Nuian, Gender.Male) != null;
                    if (!worldReady)
                        await Task.Delay(100).ConfigureAwait(false);
                }

                if (!worldReady)
                {
                    BotPresenceCoordinator.LogWarn(
                        "world never became ready — presence demo / bot auto-restore / startup arming aborted");
                    return;
                }

                if (runPresence)
                {
                    NewCoordinator().Start(RosterConfig(BotPresenceCoordinator.ReadBotCount(), "presence"));
                }

                if (runStartupArming)
                {
                    // T2 supported arming: the explicit deployment switch drives
                    // the same enrollment flow through the one arming owner.
                    var coordinator = NewCoordinator();
                    var result = BotStartupArming.Enroll(
                        SingletonContainer.ServiceProvider.GetRequiredService<IPlayerBotScheduler>(),
                        RosterConfig(BotStartupArmingOptions.ReadBotCount(), BotStartupArmingOptions.AccountPrefix),
                        coordinator.Enroll);
                    BotPresenceCoordinator.LogWarn($"Startup arming: {result.Message}");
                }

                if (runAutoRestore)
                {
                    var admin = BotAdminService.FromContainer();
                    var result = admin.Restore();
                    BotPresenceCoordinator.LogWarn($"Auto-restore bots on boot: {result.Message}");
                }

                static BotPresenceCoordinator NewCoordinator() => new(
                    SingletonContainer.ServiceProvider.GetRequiredService<IPlayerBotManager>(),
                    SingletonContainer.ServiceProvider.GetRequiredService<IPlayerBotScheduler>(),
                    SingletonContainer.ServiceProvider.GetRequiredService<IPopulationDirector>(),
                    SingletonContainer.ServiceProvider.GetRequiredService<BotRoamStepExecutor>());

                // The roster shape every boot-time enrollment uses — ONE copy, so
                // the presence demo and the supported startup switch cannot drift.
                static BotPresenceCoordinator.BotPresenceConfig RosterConfig(int count, string accountPrefix) => new(
                    BotCount: count,
                    ZoneId: WorldManager.DefaultWorldTemplateId,
                    HomePosition: BotPresenceCoordinator.ReadHomePosition(),
                    RoamRadius: 30f,
                    RoamSpeed: 2.5f,
                    Level: 5,
                    // No prefix: names come from Data/Bots/bot_names.json
                    // (BotNameGenerator), and an index whose managed account
                    // already owns a bot re-enrols that bot under its own name.
                    NamePrefix: null,
                    AccountPrefix: accountPrefix,
                    MaxPresenceBots: BotPresenceCoordinator.ReadMaxPresenceBots());
            }
            catch (Exception ex)
            {
                // The presence demo / auto-restore / startup arming must never
                // take the server down.
                BotPresenceCoordinator.LogError(ex, "presence demo / auto-restore / startup arming bootstrap failed");
            }
        });
    }
}
