using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.Bots;

using Microsoft.Extensions.DependencyInjection;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Chatter bootstrap — starts <see cref="BotChatterService"/> when the game
/// server boots with the pre-LLM social layer enabled ("Bots"."EnableChatter"
/// in Config.Local.json / Config.json, or AAEMU_BOT_CHATTER_ENABLED=1).
/// Follows the BotPresenceBootstrap precedent: runs at assembly load, waits
/// for the DI container, then arms the tick-driven proximity scan. When
/// disabled (the default) both the bootstrap and the service are strict
/// no-ops: no thread, no tick subscription, no chat traffic.
/// </summary>
internal static class BotChatterBootstrap
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    [ModuleInitializer]
    internal static void Init()
    {
        if (!BotChatterOptions.ReadEnabledFlag())
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
                    .GetRequiredService<BotChatterService>();
                if (service.Start())
                    Logger.Info("BotChatterBootstrap: chatter armed");
            }
            catch (Exception ex)
            {
                // The social layer must never take the server down.
                Logger.Error(ex, "BotChatterBootstrap failed");
            }
        });
    }
}
