using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using TUnit.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.World;

/// <summary>
/// Region position-query semantics (M5 A1): the position-based GetList
/// overload reads obj.Transform — the race witness the audit flagged
/// (Region.cs:401). The fix iterates under _objectsLock; these tests prove
/// the overload's distance/exclude behavior is unchanged and consistent
/// with region membership.
///
/// [NotInParallel]: constructing the region touches the WorldManager
/// singleton (AddObject → GetZoneId; null template → zone 0). The singleton
/// is seeded with a mock-backed WorldManager using the suite's missing-only
/// guard (never replace an established singleton — t_4f11a519).
/// </summary>
[NotInParallel]
public class RegionGetListTests
{
    private static readonly Lazy<WorldManager> SeededWorldManager = new(() =>
    {
        var manager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var field = typeof(WorldManager).BaseType!.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("Cannot locate Singleton<WorldManager>.s_instance");
        if (field.GetValue(null) == null)
            field.SetValue(null, manager);
        return manager;
    });

    private static WorldInstance CreateWorld()
    {
        _ = SeededWorldManager.Value;
        // Template must be non-null: WorldInstance.Finalize() → ToString()
        // dereferences Template.Name at process exit — a null template NREs
        // in the GC finalizer and crashes the test host (exit 134, session
        // never ends). Same shape as WorldManagerTests.CreateWorldTemplate.
        var template = new WorldTemplate { Id = 1, Name = "region-getlist-test-world" };
        return new WorldInstance(template, 0, true, 1);
    }

    private static GameObject ObjectAt(uint objId, Vector3 position)
    {
        var obj = new GameObject { ObjId = objId };
        obj.Transform.Local.SetPosition(position.X, position.Y, position.Z);
        return obj;
    }

    [Test]
    public async Task GetList_PositionOverload_ReturnsObjectsWithinRadius()
    {
        var region = new Region(CreateWorld(), 0, 0, 0);
        var nearA = ObjectAt(1, new Vector3(10f, 10f, 0f));
        var nearB = ObjectAt(2, new Vector3(10f, 20f, 0f));
        var far = ObjectAt(3, new Vector3(50f, 50f, 0f));
        region.AddObject(nearA);
        region.AddObject(nearB);
        region.AddObject(far);

        // radius 15 around (10,10): nearA (0) and nearB (10) inside, far (~56.6) outside
        var result = region.GetList(new List<GameObject>(), 0, 10f, 10f, 15f * 15f);

        await Assert.That(result).Contains(nearA);
        await Assert.That(result).Contains(nearB);
        await Assert.That(result).DoesNotContain(far);
    }

    [Test]
    public async Task GetList_PositionOverload_ExcludesObjId()
    {
        var region = new Region(CreateWorld(), 0, 0, 0);
        var self = ObjectAt(1, new Vector3(10f, 10f, 0f));
        var other = ObjectAt(2, new Vector3(11f, 11f, 0f));
        region.AddObject(self);
        region.AddObject(other);

        var result = region.GetList(new List<GameObject>(), self.ObjId, 10f, 10f, 20f * 20f);

        await Assert.That(result).DoesNotContain(self);
        await Assert.That(result).Contains(other);
    }

    [Test]
    public async Task GetList_PositionOverload_AfterRemove_ReflectsCurrentMembership()
    {
        var region = new Region(CreateWorld(), 0, 0, 0);
        var obj = ObjectAt(1, new Vector3(10f, 10f, 0f));
        region.AddObject(obj);
        region.RemoveObject(obj);

        var result = region.GetList(new List<GameObject>(), 0, 10f, 10f, 100f);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetList_PositionOverload_ConcurrentAddRemove_NoCollectionMutationErrors()
    {
        // The under-lock iteration must tolerate concurrent Add/Remove
        // (the old snapshot pattern existed for that; the lock iteration
        // preserves it — no "Collection was modified" style failures).
        var region = new Region(CreateWorld(), 0, 0, 0);
        for (uint i = 1; i <= 20; i++)
            region.AddObject(ObjectAt(i, new Vector3(10f + i, 10f, 0f)));

        var cts = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            uint next = 100;
            while (!cts.IsCancellationRequested)
            {
                region.AddObject(ObjectAt(next++, new Vector3(10f, 10f, 0f)));
                if (region.GetObjectIdsList(new List<uint>(), 0).Count > 0)
                {
                    var id = region.GetObjectIdsList(new List<uint>(), 0)[0];
                    var victim = region.GetObjectsList(new List<GameObject>(), 0)
                        .FirstOrDefault(o => o.ObjId == id);
                    if (victim != null)
                        region.RemoveObject(victim);
                }
            }
        });

        try
        {
            for (var pass = 0; pass < 500; pass++)
            {
                var result = region.GetList(new List<GameObject>(), 0, 10f, 10f, 30f * 30f);
                // No exception may escape the query under churn.
                await Assert.That(result).IsNotNull();
            }
        }
        finally
        {
            cts.Cancel();
            await churn;
        }
    }
}
