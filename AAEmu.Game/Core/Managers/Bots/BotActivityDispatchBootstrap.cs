using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils;

using Microsoft.Extensions.DependencyInjection;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Runs the dispatcher-or-deny validation once the container exists and keeps
/// its report readable (plan T9, audit L3-a).
///
/// Unlike the neighbouring bootstraps this one has no feature switch: the
/// contract it checks is about the WIRING itself — a registered module that can
/// elect work no dispatch owner performs is a bug in every deployment, not a
/// feature to enable — and the check is one pass over the registered module
/// list. It never throws into the host and never changes behaviour: the
/// enforcement is the arbiter's runtime refusal, this only reports.
/// </summary>
internal static class BotActivityDispatchBootstrap
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>The report from the boot-time pass (null = the pass has not run yet).</summary>
    internal static BotActivityDispatchReport? LastReport { get; private set; }

    [ModuleInitializer]
    internal static void Init()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for DI (the bot registry is registered during Program init).
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                if (SingletonContainer.ServiceProvider == null)
                    return;

                var modules = SingletonContainer.ServiceProvider.GetService<IEnumerable<IBotActivityModule>>();
                var report = BotActivityDispatchValidation.Validate(modules);
                LastReport = report;

                if (report.IsClean)
                {
                    Logger.Info(
                        "BotActivityDispatchBootstrap: {Modules} modules, {Declared} declared activities, every one carried",
                        report.ModuleCount, report.DeclaredActivityCount);
                    return;
                }

                foreach (var line in BotActivityDispatchValidation.Describe(report))
                    Logger.Warn("BotActivityDispatchBootstrap: {Gap}", line);
            }
            catch (Exception ex)
            {
                // The wiring report must never take the server down.
                Logger.Error(ex, "BotActivityDispatchBootstrap failed");
            }
        });
    }
}
