using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World.Transform;

[NotInParallel]
public class TransformWorldBindingTests
{
    [Test]
    [Arguments(0u, 0u)]
    [Arguments(42u, 7u)]
    public async Task ParentWorld_UnpublishedWorld_PreservesSuppliedWorld(uint instanceId, uint templateId)
    {
        // Persistent Doodads are loaded before the fully initialized world is
        // published. Their actual world reference must not require a registry lookup.
        var world = new WorldInstance(new WorldTemplate { Id = templateId }, 0, true, instanceId);
        var doodad = new Doodad();

        doodad.ParentWorld = world;

        await Assert.That(doodad.ParentWorld).IsSameReferenceAs(world);
        await Assert.That(doodad.Transform.InstanceId).IsEqualTo(instanceId);
        await Assert.That(doodad.Transform.WorldId).IsEqualTo(templateId);
    }

    [Test]
    public async Task ParentWorld_AnotherUnpublishedWorld_UpdatesInstanceAndTemplate()
    {
        var first = new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 41);
        var second = new WorldInstance(new WorldTemplate { Id = 7 }, 0, true, 42);
        var doodad = new Doodad();
        doodad.ParentWorld = first;

        doodad.ParentWorld = second;

        await Assert.That(doodad.ParentWorld).IsSameReferenceAs(second);
        await Assert.That(doodad.Transform.InstanceId).IsEqualTo(second.Id);
        await Assert.That(doodad.Transform.WorldId).IsEqualTo(second.Template.Id);
    }

    [Test]
    public async Task InstanceId_DifferentPublishedWorld_ResolvesNewWorld()
    {
        var first = new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 41);
        var second = new WorldInstance(new WorldTemplate { Id = 7 }, 0, true, 42);
        var manager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(second.Id, second);
        typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, worlds);
        var singleton = typeof(Singleton<WorldManager>)
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = singleton.GetValue(null);
        singleton.SetValue(null, manager);
        try
        {
            var doodad = new Doodad { ParentWorld = first };

            doodad.Transform.InstanceId = second.Id;

            await Assert.That(doodad.ParentWorld).IsSameReferenceAs(second);
            await Assert.That(doodad.Transform.InstanceId).IsEqualTo(second.Id);
            await Assert.That(doodad.Transform.WorldId).IsEqualTo(second.Template.Id);
        }
        finally
        {
            singleton.SetValue(null, previous);
        }
    }

    [Test]
    public async Task ParentWorld_SameInstanceIdNewTemplate_SynchronizesTemplate()
    {
        var first = new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 41);
        var second = new WorldInstance(new WorldTemplate { Id = 7 }, 0, true, 41);
        var doodad = new Doodad { ParentWorld = first };

        doodad.ParentWorld = second;

        await Assert.That(doodad.ParentWorld).IsSameReferenceAs(second);
        await Assert.That(doodad.Transform.InstanceId).IsEqualTo(second.Id);
        await Assert.That(doodad.Transform.WorldId).IsEqualTo(second.Template.Id);
    }
}
