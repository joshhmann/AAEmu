using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using AAEmu.Commons.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Bot metadata persistence bootstrap — starts the periodic dirty-flush timer
/// when the game server boots, and guarantees the mandatory final flush on
/// process exit (normal shutdown, Ctrl+C, SIGTERM).
///
/// Runs at assembly load (before Main), mirroring BotE2EBridgeBootstrap: it
/// needs the DI container (SingletonContainer.ServiceProvider, populated
/// during Program initialization), so it polls briefly. When no container
/// appears (unit-test processes), every path is a strict no-op — no timer,
/// no signal hooks that touch the manager.
/// </summary>
internal static class BotPersistenceBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Start the periodic flush once the DI container exists.
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);

                if (SingletonContainer.ServiceProvider != null)
                    BotPersistenceManager.Instance.Initialize();
            }
            catch
            {
                // Persistence startup must never take the server down.
            }
        });

        // Mandatory final flush on every exit path. All hooks are idempotent
        // (ShutdownAsync flushes once) and guarded by the initialized flag, so
        // they are strict no-ops in processes that never booted the manager.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => BotPersistenceManager.ShutdownFlushIfInitialized();
        Console.CancelKeyPress += (_, _) => BotPersistenceManager.ShutdownFlushIfInitialized();

        try
        {
            PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                _ => BotPersistenceManager.ShutdownFlushIfInitialized());
        }
        catch
        {
            // Platform without posix signal registration — ProcessExit still covers graceful exits.
        }
    }
}
