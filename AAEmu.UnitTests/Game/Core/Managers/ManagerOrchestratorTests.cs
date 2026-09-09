using AAEmu.Game.Core.Managers;

using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Unit tests for ManagerOrchestrator — DAG building, topological sort, Lazy skipping, cycle detection.
/// These tests use private stub types so they have no external DB/file dependencies.
/// </summary>
public class ManagerOrchestratorTests
{
    // -------------------------------------------------------------------------
    // Stub interfaces / concrete types used across tests
    // -------------------------------------------------------------------------
    private interface IA : ILoadable;
    private interface IB : ILoadable;
    private interface IC : ILoadable;

    /// <summary>A has no dependencies.</summary>
    private class A : IA { public void Load() { } }

    /// <summary>B depends on A (takes IA in its constructor).</summary>
    private class B(IA a) : IB { public void Load() { } }

    /// <summary>C depends on B (takes IB in its constructor).</summary>
    private class C(IB b) : IC { public void Load() { } }

    // For cycle detection
    private interface IX : ILoadable;
    private interface IY : ILoadable;

    private class CycleX(IY y) : IX { public void Load() { } }
    private class CycleY(IX x) : IY { public void Load() { } }

    // For Lazy<T> skip test
    private interface IP : ILoadable;
    private interface IQ : ILoadable;

    /// <summary>P takes Q as Lazy&lt;IQ&gt; — should NOT create a dependency edge.</summary>
    private class P(Lazy<IQ> q) : IP { public void Load() { } }
    private class Q : IQ { public void Load() { } }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (ManagerOrchestrator orchestrator, IServiceProvider sp) Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        services.AddSingleton<IServiceCollection>(_ => services);
        services.AddSingleton<ManagerOrchestrator>();
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ManagerOrchestrator>(), sp);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task BuildBatches_ProducesCorrectTopologicalOrder()
    {
        // A → (no deps)  →  batch 0
        // B → depends on A → batch 1
        // C → depends on B → batch 2
        var (orchestrator, _) = Build(services =>
        {
            services.AddSingleton<A>();
            services.AddSingleton<IA>(sp => sp.GetRequiredService<A>());
            services.AddSingleton<B>();
            services.AddSingleton<IB>(sp => sp.GetRequiredService<B>());
            services.AddSingleton<C>();
            services.AddSingleton<IC>(sp => sp.GetRequiredService<C>());
        });

        var batches = orchestrator.BuildBatches<ILoadable>();

        await Assert.That(batches.Count).IsEqualTo(3);

        // Each batch should have exactly one manager
        await Assert.That(batches[0]).HasSingleItem();
        await Assert.That(batches[1]).HasSingleItem();
        await Assert.That(batches[2]).HasSingleItem();

        // Verify order: A first, then B, then C
        await Assert.That(batches[0][0]).IsTypeOf<A>();
        await Assert.That(batches[1][0]).IsTypeOf<B>();
        await Assert.That(batches[2][0]).IsTypeOf<C>();
    }

    [Test]
    public async Task BuildBatches_IndependentManagersAreInSameBatch()
    {
        // A and Q have no constructor deps on each other → both in batch 0
        var (orchestrator, _) = Build(services =>
        {
            services.AddSingleton<A>();
            services.AddSingleton<IA>(sp => sp.GetRequiredService<A>());
            services.AddSingleton<Q>();
            services.AddSingleton<IQ>(sp => sp.GetRequiredService<Q>());
        });

        var batches = orchestrator.BuildBatches<ILoadable>();

        await Assert.That(batches).HasSingleItem();
        await Assert.That(batches[0].Count).IsEqualTo(2);
    }

    [Test]
    public async Task BuildBatches_ThrowsOnCycle()
    {
        // CycleX depends on IY, CycleY depends on IX → cycle
        var (orchestrator, _) = Build(services =>
        {
            services.AddSingleton<CycleX>();
            services.AddSingleton<IX>(sp => sp.GetRequiredService<CycleX>());
            services.AddSingleton<CycleY>();
            services.AddSingleton<IY>(sp => sp.GetRequiredService<CycleY>());
        });

        var ex = Assert.Throws<InvalidOperationException>(() => orchestrator.BuildBatches<ILoadable>());
        await Assert.That(ex.Message).Contains("Cycle detected");
    }

    [Test]
    public async Task BuildBatches_SkipsLazyDependencies()
    {
        // P takes Lazy<IQ> — this should NOT be treated as a dependency on Q.
        // Therefore both P and Q have no unresolved deps and appear in batch 0.
        var (orchestrator, _) = Build(services =>
        {
            services.AddSingleton<Q>();
            services.AddSingleton<IQ>(sp => sp.GetRequiredService<Q>());
            // Register Lazy<IQ> explicitly so DI can satisfy P's constructor
            services.AddSingleton(sp => new Lazy<IQ>(sp.GetRequiredService<IQ>));
            services.AddSingleton<P>();
            services.AddSingleton<IP>(sp => sp.GetRequiredService<P>());
        });

        var batches = orchestrator.BuildBatches<ILoadable>();

        // Both should be in the first (and only) batch
        await Assert.That(batches).HasSingleItem();
        await Assert.That(batches[0].Count).IsEqualTo(2);
    }

    [Test]
    public async Task BuildBatches_EmptyRegistrations_ReturnsEmptyList()
    {
        var (orchestrator, _) = Build(_ => { });

        var batches = orchestrator.BuildBatches<ILoadable>();

        await Assert.That(batches).IsEmpty();
    }

    [Test]
    public async Task BuildBatches_ExcludesFactoryRegistrations()
    {
        // Only factory-registered (no ImplementationType) — should be excluded
        var (orchestrator, _) = Build(services =>
        {
            // Factory lambda → ImplementationType == null → excluded
            services.AddSingleton<IA>(_ => new A());
        });

        var batches = orchestrator.BuildBatches<ILoadable>();

        await Assert.That(batches).IsEmpty();
    }

    [Test]
    public async Task RunLoadAsync_CallsLoadOnAllManagers()
    {
        var loadCalled = new List<string>();

        // Register the list as a service so TrackingA can be type-registered (ImplementationType != null).
        // Factory lambdas produce ImplementationType == null and are excluded by the orchestrator.
        var services = new ServiceCollection();
        services.AddSingleton(loadCalled);
        services.AddSingleton<TrackingA>();
        services.AddSingleton<IServiceCollection>(_ => services);
        services.AddSingleton<ManagerOrchestrator>();
        var sp = services.BuildServiceProvider();
        var orchestrator = sp.GetRequiredService<ManagerOrchestrator>();

        await orchestrator.RunLoadAsync();

        await Assert.That(loadCalled).Contains("A");
    }

    // -------------------------------------------------------------------------
    // Boot resilience (ExecuteWithBootRetry) — stampede hardening: parallel
    // boot fires dozens of managers at MySQL at once and the connector
    // answers overload with internal failures. Transience needs
    // connector-internal evidence (MySqlException, or NRE thrown by /
    // through MySql.Data frames) — a manager-thrown NRE fails fast so a
    // retry never re-runs partial mutations. Failures keep the manager's
    // identity; retried Load bodies must be clear-first (boot-loader survey).
    // -------------------------------------------------------------------------

    [Test]
    public async Task ExecuteWithBootRetry_TransientMySqlThenSuccess_SucceedsAfterRetries()
    {
        // Real transient evidence (dead-port connection refused): the first
        // two attempts fail inside the connector, the third succeeds.
        var attempts = 0;
        ManagerOrchestrator.ExecuteWithBootRetry(() =>
        {
            attempts++;
            if (attempts < 3)
            {
                using var _ = new MySqlConnection(
                    "Server=127.0.0.1;Port=1;User ID=root;Password=e2e;Connection Timeout=2");
                _.Open();
            }
        }, "FlakyManager", "Load");

        await Assert.That(attempts).IsEqualTo(3);
    }

    [Test]
    public async Task ExecuteWithBootRetry_ManagerThrownNre_FailsImmediatelyWithoutRetry()
    {
        // Game-code NRE (test frames, no MySql.Data evidence): NOT transient —
        // retrying would re-run partial mutations, so it fails on attempt 1
        // with the manager's identity.
        var attempts = 0;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ManagerOrchestrator.ExecuteWithBootRetry(() =>
            {
                attempts++;
                throw new NullReferenceException("genuine manager defect");
            }, "BuggyManager", "Load"));

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(ex.Message).Contains("BuggyManager");
        await Assert.That(ex.InnerException).IsTypeOf<NullReferenceException>();
    }

    [Test]
    public async Task ExecuteWithBootRetry_NonTransient_ThrowsImmediatelyWithoutRetry()
    {
        var attempts = 0;
        var original = new InvalidOperationException("genuine manager defect");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ManagerOrchestrator.ExecuteWithBootRetry(() =>
            {
                attempts++;
                throw original;
            }, "BrokenManager", "Initialize"));

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(ex.Message).Contains("BrokenManager");
        await Assert.That(ReferenceEquals(ex.InnerException, original)).IsTrue();
    }

    [Test]
    public async Task ExecuteWithBootRetry_MySqlConnectionFailure_RetriedThenThrowsWithIdentity()
    {
        // A real connector failure (nothing listens on port 1): proves the
        // MySqlException arm of the transient classifier retries instead of
        // failing the boot on first contact.
        var attempts = 0;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ManagerOrchestrator.ExecuteWithBootRetry(() =>
            {
                attempts++;
                using var connection = new MySqlConnection(
                    "Server=127.0.0.1;Port=1;User ID=root;Password=e2e;Connection Timeout=2");
                connection.Open();
            }, "DbManager", "Load"));

        await Assert.That(attempts).IsEqualTo(ManagerOrchestrator.MaxBootAttempts);
        await Assert.That(ex.Message).Contains("DbManager");
    }


    [Test]
    public async Task ExecuteWithBootRetry_ClearFirstLoader_FailThenSucceed_LeavesExactlyOneCopy()
    {
        // The retry contract every retried Load body must keep
        // (clear-first, then fill — the CrimeManager / FriendManager /
        // NameManager shape): a transient mid-fill failure followed by a
        // successful re-run converges to exactly one copy of every row —
        // never duplicated, never partial.
        var store = new Dictionary<uint, string>();
        var attempts = 0;
        ManagerOrchestrator.ExecuteWithBootRetry(() =>
        {
            attempts++;
            store.Clear();
            store.Add(1u, "a");
            if (attempts == 1)
            {
                using var _ = new MySqlConnection(
                    "Server=127.0.0.1;Port=1;User ID=root;Password=e2e;Connection Timeout=2");
                _.Open(); // real MySqlException mid-fill
            }
            store.Add(2u, "b");
        }, "ClearingLoader", "Load");

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(store.Count).IsEqualTo(2);
        await Assert.That(store[1u]).IsEqualTo("a");
        await Assert.That(store[2u]).IsEqualTo("b");
    }

    [Test]
    public async Task IsTransientBootFailure_MySqlFramedNonNre_ReturnsFalse()
    {
        // Same MySql.Data frames as the production trace, but the wrong type:
        // the type gate holds — only connector-internal NREs (and
        // MySqlExceptions) are transient.
        var framed = Capture(() => { using var _ = new MySqlCommand("SELECT 1").ExecuteReader(); });

        await Assert.That(framed).IsTypeOf<InvalidOperationException>();
        await Assert.That(ManagerOrchestrator.IsTransientBootFailure(framed)).IsFalse();
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException("capture operation did not throw");
    }

    // Tracking helper — injected via type registration so ImplementationType is set in the descriptor.
    private class TrackingA(List<string> log) : ILoadable
    {
        public void Load() => log.Add("A");
    }
}