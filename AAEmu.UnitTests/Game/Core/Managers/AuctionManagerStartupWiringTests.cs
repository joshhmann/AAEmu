using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class AuctionManagerStartupWiringTests
{
    [Test]
    public async Task BuildBatches_IncludesAuctionManager_ExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AuctionManager>();
        services.AddSingleton<IAuctionManager>(sp => sp.GetRequiredService<AuctionManager>());
        services.AddSingleton(_ => Mock.Of<IItemManager>().Object);
        services.AddSingleton(_ => Mock.Of<INameManager>().Object);
        services.AddSingleton(_ => Mock.Of<IAuctionIdManager>().Object);
        services.AddSingleton(_ => Mock.Of<ILocalizationManager>().Object);
        services.AddSingleton(_ => Mock.Of<ITaskManager>().Object);

        await using var provider = services.BuildServiceProvider();
        var orchestrator = new ManagerOrchestrator(provider, services);

        var batches = orchestrator.BuildBatches<ILoadable>();
        var loadedManagers = batches.SelectMany(b => b).ToList();

        await Assert.That(loadedManagers.OfType<AuctionManager>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Constructor_DependsOnItemAndNameManagers_LoadRunsAfterThem()
    {
        // ManagerOrchestrator derives load order from constructor parameters; these
        // dependencies force AuctionManager.Load() into a batch after ItemManager
        // and NameManager complete their Load().
        var parameters = typeof(AuctionManager)
            .GetConstructors()
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        await Assert.That(parameters).Contains(typeof(IItemManager));
        await Assert.That(parameters).Contains(typeof(INameManager));
    }

    [Test]
    public async Task AuctionManager_ImplementsILoadable()
    {
        await Assert.That(typeof(ILoadable).IsAssignableFrom(typeof(AuctionManager))).IsTrue();
    }
}
