using System.Collections.Concurrent;
using System.Diagnostics;
using MySql.Data.MySqlClient;

using NLog;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Core.Managers;

public class ManagerOrchestrator(IServiceProvider serviceProvider, IServiceCollection services)
{
    /// <summary>
    /// Builds batches of TInterface instances in topological order derived from constructor dependencies.
    /// Each batch can be executed in parallel; batches must be executed sequentially.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TInterface>> BuildBatches<TInterface>()
    {
        // 1. Collect all concrete types registered as singletons that implement TInterface.
        //    Exclude factory-registered (ImplementationType == null) redirect registrations.
        var participatingTypes = services
            .Where(sd => sd.Lifetime == ServiceLifetime.Singleton
                      && sd.ImplementationType != null
                      && typeof(TInterface).IsAssignableFrom(sd.ImplementationType))
            .Select(sd => sd.ImplementationType!)
            .Distinct()
            .ToList();

        // 2. Build adjacency: for each type, which other participating types it depends on
        //    (derived from constructor parameters, excluding Lazy<T> to avoid false edges from
        //    circular-dependency break patterns).
        var typeSet = participatingTypes.ToHashSet();
        var deps = participatingTypes.ToDictionary(
            t => t,
            t => GetConstructorDeps(t, typeSet));

        // 3. Kahn's topological sort → parallel batches
        var batches = new List<List<Type>>();
        var remaining = typeSet.ToHashSet();
        while (remaining.Count > 0)
        {
            var batch = remaining
                .Where(t => deps[t].All(d => !remaining.Contains(d)))
                .ToList();
            if (batch.Count == 0)
                throw new InvalidOperationException(
                    $"Cycle detected in manager dependency graph among: {string.Join(", ", remaining.Select(t => t.Name))}");
            batches.Add(batch);
            foreach (var t in batch)
                remaining.Remove(t);
        }

        // 4. Resolve instances
        return batches
            .Select(batch => (IReadOnlyList<TInterface>)batch
                .Select(t => (TInterface)serviceProvider.GetRequiredService(t))
                .ToList())
            .ToList();
    }

    /// <summary>
    /// Boot attempts per manager before a Load/Initialize failure is fatal.
    /// Parallel boot stampedes dozens of managers onto MySQL at once and the
    /// connector answers transient overload with internal failures (observed:
    /// NullReferenceException inside PreparableStatement.Execute with no
    /// game-code frames), so a single attempt is never the verdict.
    /// </summary>
    internal const int MaxBootAttempts = 3;

    /// <summary>Base backoff between boot retries (doubles per attempt).</summary>
    internal static readonly TimeSpan BootRetryBaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Whether a boot-phase exception is worth retrying: connector-level
    /// transients only — any MySqlException, or a NullReferenceException
    /// that is provably connector-internal (the production stampede
    /// signature). A manager-thrown NRE carries game-code frames and fails
    /// fast — retrying it would re-run partial mutations. Everything else
    /// (genuine code defects, cancelled boots) fails fast.
    /// </summary>
    internal static bool IsTransientBootFailure(Exception ex)
        => ex is MySqlException || (ex is NullReferenceException nre && IsConnectorInternal(nre));

    /// <summary>
    /// Connector-internal evidence for a NullReferenceException: the throwing
    /// method lives in MySql.Data, or its captured stack passes through
    /// MySql.Data frames. An unthrown exception (no trace) carries no
    /// evidence and is never transient.
    /// </summary>
    internal static bool IsConnectorInternal(NullReferenceException ex)
        => ex.TargetSite?.DeclaringType?.Namespace?.StartsWith("MySql.Data", StringComparison.Ordinal) == true
            || ex.StackTrace?.Contains("MySql.Data", StringComparison.Ordinal) == true;

    /// <summary>
    /// Runs one manager's Load/Initialize with per-manager fault isolation:
    /// transient failures retry with backoff; anything still failing after
    /// <see cref="MaxBootAttempts"/>, and any non-transient failure, throws
    /// with the manager's identity — never swallowed, never unattributed.
    /// Retried Load bodies must be re-runnable (clear/replace-first state);
    /// see the boot-loader survey — only clear-first loaders converge
    /// exactly-once under retry.
    /// </summary>
    internal static void ExecuteWithBootRetry(Action action, string managerName, string phase)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < MaxBootAttempts && IsTransientBootFailure(ex))
            {
                Logger.Warn(ex, "[boot-retry] {Phase} {Manager} attempt {Attempt}/{Max} failed ({Error}) — retrying",
                    phase, managerName, attempt, MaxBootAttempts, ex.GetType().Name);
                Thread.Sleep(BootRetryBaseDelay * (1 << (attempt - 1)));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[boot] {phase} {managerName} failed after {attempt} attempt(s): {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
    }


    private static List<Type> GetConstructorDeps(Type type, HashSet<Type> participating)
    {
        var ctor = type.GetConstructors().MaxBy(c => c.GetParameters().Length);
        if (ctor is null) return [];
        return ctor.GetParameters()
            .Select(p => p.ParameterType)
            // Skip Lazy<T> — these exist specifically to break circular deps at resolution time
            .Where(pt => !(pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Lazy<>)))
            // Map each interface parameter to the concrete type in the participating set
            .Select(pt => participating.FirstOrDefault(t => pt.IsAssignableFrom(t)))
            .Where(t => t is not null)
            .ToList()!;
    }

    /// <summary>Runs Load() on all ILoadable managers in dependency order, parallelising within each batch.</summary>
    public Task RunLoadAsync() => RunBatches<ILoadable>("Load", static m => m.Load);

    /// <summary>Runs Initialize() on all IInitializable managers in dependency order, parallelising within each batch.</summary>
    public Task RunInitializeAsync() => RunBatches<IInitializable>("Initialize", static m => m.Initialize);


    /// <summary>
    /// Boot-time profile (perf/e2e-speed): runs each dependency batch in
    /// parallel and logs the batch wall-clock plus the slowest managers, so
    /// startup cost can be attributed without touching any manager.
    /// </summary>
    private async Task RunBatches<T>(string phase, Func<T, Action> selector) where T : class
    {
        var total = Stopwatch.StartNew();
        foreach (var batch in BuildBatches<T>())
        {
            var batchSw = Stopwatch.StartNew();
            var timings = new ConcurrentDictionary<T, long>();
            await Task.WhenAll(batch.Select(m => Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                ExecuteWithBootRetry(selector(m), m.GetType().Name, phase);
                timings[m] = sw.ElapsedMilliseconds;
            })));
            var slowest = string.Join(", ",
                timings.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key.GetType().Name} {kv.Value}ms"));
            Logger.Info($"[boot-profile] {phase} batch of {batch.Count} took {batchSw.Elapsed.TotalSeconds:F2}s | slowest: {slowest}");
        }
        Logger.Info($"[boot-profile] {phase} total {total.Elapsed.TotalSeconds:F2}s");
    }

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
}

