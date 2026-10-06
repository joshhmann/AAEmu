using System.Numerics;

using AAEmu.Game.Core.Managers;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Startup-wiring coverage for <see cref="ObstacleManager"/>, the manager that
/// indexes the shipped <c>Data/Navigation/*_obstacles.json</c> catalogs for bot
/// navigation avoidance.
///
/// These tests pin the two properties the runtime depends on:
///  - the manager is reachable through the orchestrated <see cref="ILoadable"/>
///    boot path (<see cref="ManagerOrchestrator.RunLoadAsync"/>), which is what
///    production invokes — not a bespoke call site;
///  - its default data directory is anchored to the application directory, so the
///    catalogs resolve regardless of the process working directory.
///
/// They run entirely on a temp fixture directory, so no test needs the real
/// shipped catalogs.
/// </summary>
[NotInParallel]
public class ObstacleManagerStartupWiringTests
{
    private const string FixtureCatalog = """
    {
      "zone": "startup_fixture",
      "totalObstacles": 2,
      "obstacles": [
        { "templateId": 9001, "name": "Fixture Gate", "category": "gate", "x": 10.0, "y": 0.0, "z": 10.0, "yaw": 0.0, "keepOutRadius": 3.0 },
        { "templateId": 9002, "name": "Fixture Tower", "category": "building", "x": 400.0, "y": 400.0, "z": 20.0, "yaw": 1.0, "keepOutRadius": 8.0 }
      ]
    }
    """;

    private string _dataDir = null!;

    [Before(Test)]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "aaemu-obstacle-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [After(Test)]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Mirrors the production registration shape (Program.cs registers the
    /// concrete manager as a type-registered singleton so the orchestrator can
    /// both see it and resolve it). The orchestrator excludes factory
    /// registrations, so a factory registration would silently drop the manager
    /// out of the load batches.
    /// </summary>
    private static (ManagerOrchestrator Orchestrator, ServiceProvider Provider) BuildStartup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ObstacleManager>();
        services.AddSingleton<IServiceCollection>(_ => services);
        services.AddSingleton<ManagerOrchestrator>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<ManagerOrchestrator>(), provider);
    }

    [Test]
    public async Task ObstacleManager_IsParticipatingInTheOrchestratedLoadBatches()
    {
        var (orchestrator, provider) = BuildStartup();
        await using var _ = provider;
        provider.GetRequiredService<ObstacleManager>();

        var loaded = orchestrator.BuildBatches<ILoadable>().SelectMany(b => b).ToList();

        await Assert.That(loaded.OfType<ObstacleManager>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task StartupLoad_WithCatalogPresent_IndexesObstaclesAndQueriesThem()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "startup_fixture_obstacles.json"), FixtureCatalog);

        var (orchestrator, provider) = BuildStartup();
        await using var _ = provider;
        var manager = provider.GetRequiredService<ObstacleManager>();
        manager.DataDirectory = _dataDir;

        await orchestrator.RunLoadAsync();

        await Assert.That(manager.TotalObstacleCount).IsEqualTo(2);

        // The gate sits on the direct line, so both the point query and the
        // detour path must see it — this is the data that was dead at runtime.
        await Assert.That(manager.IsBlocked(new Vector3(10f, 0f, 10f))).IsTrue();
        await Assert.That(manager.FindDetour(new Vector3(0f, 0f, 10f), new Vector3(20f, 0f, 10f)).Count)
            .IsGreaterThanOrEqualTo(2);

        // ...and a point far from both obstacles stays walkable.
        await Assert.That(manager.IsBlocked(new Vector3(1000f, 1000f, 10f))).IsFalse();
    }

    [Test]
    public async Task StartupLoad_WithNoCatalogPresent_FailsOpen()
    {
        // Empty directory: the loader must leave the manager unpopulated rather
        // than throwing, and every query must degrade to "nothing blocks you".
        var (orchestrator, provider) = BuildStartup();
        await using var _ = provider;
        var manager = provider.GetRequiredService<ObstacleManager>();
        manager.DataDirectory = _dataDir;

        await orchestrator.RunLoadAsync();

        await Assert.That(manager.TotalObstacleCount).IsEqualTo(0);
        await Assert.That(manager.IsBlocked(new Vector3(10f, 0f, 10f))).IsFalse();
        await Assert.That(manager.FindDetour(new Vector3(0f, 0f, 10f), new Vector3(20f, 0f, 10f))).IsEmpty();
    }

    [Test]
    public async Task StartupLoad_WithMissingDirectory_FailsOpen()
    {
        var (orchestrator, provider) = BuildStartup();
        await using var _ = provider;
        var manager = provider.GetRequiredService<ObstacleManager>();
        manager.DataDirectory = Path.Combine(_dataDir, "does-not-exist");

        await orchestrator.RunLoadAsync();

        await Assert.That(manager.TotalObstacleCount).IsEqualTo(0);
        await Assert.That(manager.IsBlocked(new Vector3(10f, 0f, 10f))).IsFalse();
    }

    [Test]
    public async Task Load_IsIdempotent_AcrossBootRetry()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "startup_fixture_obstacles.json"), FixtureCatalog);

        var (orchestrator, provider) = BuildStartup();
        await using var _ = provider;
        var manager = provider.GetRequiredService<ObstacleManager>();
        manager.DataDirectory = _dataDir;

        await orchestrator.RunLoadAsync();
        await Assert.That(manager.TotalObstacleCount).IsEqualTo(2);

        // ManagerOrchestrator.ExecuteWithBootRetry re-runs a manager's Load on
        // transient boot failures, so Load must converge exactly-once.
        manager.Load();
        manager.Load();

        await Assert.That(manager.TotalObstacleCount).IsEqualTo(2);
        // A duplicated index would show up as duplicate rows in a nearby query.
        await Assert.That(manager.GetNearbyObstacles(new Vector3(10f, 0f, 10f), 1f).Count).IsEqualTo(1);
    }

    [Test]
    public async Task DefaultDataDirectory_IsAnchoredToAppDirectory_NotWorkingDirectory()
    {
        // Regression guard for the real defect: the default used to be the bare
        // relative "Data/Navigation", which only resolved when the process
        // happened to start inside the deployment output folder. Launching via
        // `dotnet run --project AAEmu.Game` from the repo root booted with zero
        // obstacles even though the catalogs were shipped to the output dir.
        var defaultDirectory = new ObstacleManager().DataDirectory;

        await Assert.That(Path.IsPathRooted(defaultDirectory)).IsTrue();
        await Assert.That(Path.GetFullPath(defaultDirectory))
            .IsEqualTo(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data", "Navigation")));
        await Assert.That(Directory.Exists(defaultDirectory)).IsTrue();
    }
}