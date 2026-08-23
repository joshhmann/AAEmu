using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils;

using Microsoft.Extensions.DependencyInjection;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Schedule bootstrap — starts <see cref="BotScheduleService"/> when the
/// game server boots with C1 Schedules v1 enabled ("Bots"."EnableSchedules"
/// in Config.Local.json / Config.json, or AAEMU_BOT_SCHEDULES_ENABLED=1).
/// Follows the BotChatterBootstrap precedent: runs at assembly load, waits
/// for the DI container, then arms the tick-driven phase scan. When disabled
/// (the default) both the bootstrap and the service are strict no-ops: no
/// thread, no tick subscription, no metadata writes, no behavior changes.
/// </summary>
internal static class BotScheduleBootstrap
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    [ModuleInitializer]
    internal static void Init()
    {
        if (!BotScheduleOptions.ReadEnabledFlag())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for DI (the bot registry is registered during Program init).
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                if (SingletonContainer.ServiceProvider == null)
                    return;

                var service = SingletonContainer.ServiceProvider
                    .GetRequiredService<BotScheduleService>();
                if (service.Start())
                    Logger.Info("BotScheduleBootstrap: schedules armed");
            }
            catch (Exception ex)
            {
                // The schedule layer must never take the server down.
                Logger.Error(ex, "BotScheduleBootstrap failed");
            }
        });
    }
}
