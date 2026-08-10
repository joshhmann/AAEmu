using System.Runtime.CompilerServices;
using AAEmu.Commons.Utils;
using NLog;

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
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [ModuleInitializer]
    internal static void Init()
    {
        _ = Task.Run(() => RunBridgeStartupAsync(
            () => SingletonContainer.ServiceProvider != null,
            () => BotDriveBridge.Instance.TryStart()));
    }

    /// <summary>
    /// Polls for the DI container, then starts the bridge when it is ready.
    /// Any failure is logged at error level — the bridge must never take the
    /// server down, and it must never die silently (Kimi audit 2026-08-09).
    /// <paramref name="maxPolls"/> and <paramref name="pollDelay"/> are test
    /// seams; production uses the defaults (600 × 100ms = 60s budget).
    /// </summary>
    internal static async Task RunBridgeStartupAsync(Func<bool> isReady, Action startBridge, int maxPolls = 600, TimeSpan pollDelay = default)
    {
        if (pollDelay == default)
            pollDelay = TimeSpan.FromMilliseconds(100);

        try
        {
            for (var i = 0; i < maxPolls && !isReady(); i++)
                await Task.Delay(pollDelay).ConfigureAwait(false);

            if (isReady())
                startBridge();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "E2E bridge bootstrap failed while waiting for DI or starting the bridge");
        }
    }
}
