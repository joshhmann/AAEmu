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
/// AAEMU_PRESENCE_DEMO env). Follows the BotDriveBridge precedent: runs at
/// assembly load, waits for the DI container, then waits for the world to be
/// ready before provisioning bots. When disabled (the default — prod config
/// never sets it) it is a strict no-op: no thread, no socket, no DB writes.
/// </summary>
internal static class BotPresenceBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (!BotPresenceCoordinator.IsEnabled())
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
                var worldReady = false;
                for (var i = 0; i < 600 && !worldReady; i++)
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
                        "world never became ready — presence demo aborted (no bots provisioned)");
                    return;
                }

                var coordinator = new BotPresenceCoordinator(
                    SingletonContainer.ServiceProvider.GetRequiredService<IPlayerBotManager>(),
                    SingletonContainer.ServiceProvider.GetRequiredService<IPlayerBotScheduler>(),
                    SingletonContainer.ServiceProvider.GetRequiredService<IPopulationDirector>(),
                    SingletonContainer.ServiceProvider.GetRequiredService<BotRoamStepExecutor>());

                var count = BotPresenceCoordinator.ReadBotCount();
                coordinator.Start(new BotPresenceCoordinator.BotPresenceConfig(
                    BotCount: count,
                    ZoneId: WorldManager.DefaultWorldTemplateId,
                    HomePosition: default,
                    RoamRadius: 30f,
                    RoamSpeed: 2.5f,
                    Level: 5,
                    NamePrefix: "Citizen",
                    AccountPrefix: "presence"));
            }
            catch (Exception ex)
            {
                // The presence demo must never take the server down.
                BotPresenceCoordinator.LogError(ex, "presence demo bootstrap failed");
            }
        });
    }
}
