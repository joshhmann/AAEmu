using System.Runtime.CompilerServices;
using AAEmu.Commons.Utils;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// M2b-E2E bridge bootstrap — starts <see cref="BotDriveBridge"/> when the
/// game server boots with the bridge enabled (runtime Config.Local.json
/// "Bots"."EnableE2EBridge" or E2E_BRIDGE_ENABLED env).
///
/// Runs at assembly load (before Main); the bridge needs the DI container
/// (SingletonContainer.ServiceProvider, populated during Program
/// initialization) before reading config, so it polls briefly. When disabled
/// (the default — prod config never sets it) it is a strict no-op: no thread,
/// no socket.
/// </summary>
internal static class BotE2EBridgeBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);

                if (SingletonContainer.ServiceProvider != null)
                    BotDriveBridge.Instance.TryStart();
            }
            catch
            {
                // Bridge startup must never take the server down.
            }
        });
    }
}
