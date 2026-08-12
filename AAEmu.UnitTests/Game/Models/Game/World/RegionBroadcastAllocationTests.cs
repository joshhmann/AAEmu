using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

/// <summary>
/// Broadcast-path allocation regression tests (Kimi audit 2026-08-09, card
/// t_921a7be5). The old Region.GetList* copied the FULL region object array
/// (`new GameObject[_objectsSize]` + Array.Copy) on every call — at ~5k
/// movement broadcasts/sec × up to 9 neighbor regions that is ~45k array
/// allocations/sec. The fixed implementation iterates under the region lock
/// (zero allocations) and short-circuits Character scans on regions that hold
/// no characters at all.
///
/// Measurement seam: GC.GetAllocatedBytesForCurrentThread() deltas around
/// warm, repeated scans. A pre-sized result list keeps List.Add from growing
/// (that growth is caller-owned and amortized, not per-scan).
/// </summary>
public class RegionBroadcastAllocationTests
{
    private const int CharacterCount = 50;
    private const int ScanIterations = 100_000;

    private static readonly List<Character> Characters = BuildCharacters();

    private static List<Character> BuildCharacters()
    {
        var list = new List<Character>(CharacterCount);
        for (uint i = 1; i <= CharacterCount; i++)
            list.Add(new Character(new UnitCustomModelParams()) { ObjId = i });
        return list;
    }

    private static Region CreateRegion(GameObject[] objects, int charactersSize, Region[]? neighbors = null)
    {
        var region = new Region(null!, 0, 0, 0);
        SetPrivateField(region, "_objects", objects);
        SetPrivateField(region, "_objectsSize", objects.Length);
        SetPrivateField(region, "_charactersSize", charactersSize);
        if (neighbors != null)
            SetPrivateField(region, "_neighbors", neighbors);
        return region;
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Cannot locate field {name} on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    [Test]
    public async Task GetList_WithCharacters_ReturnsMatchingObjects()
    {
        var region = CreateRegion(Characters.Cast<GameObject>().ToArray(), CharacterCount);

        var result = new List<Character>();
        region.GetList(result, exclude: 0);

        // Compare ObjIds: Character property getters hit unseeded singletons
        // (FormulaManager), so reference-equality comparisons are out.
        await Assert.That(result.Select(c => c.ObjId))
            .IsEquivalentTo(Characters.Select(c => c.ObjId));
    }

    [Test]
    public async Task GetList_Exclude_OmitsRequestedObject()
    {
        var region = CreateRegion(Characters.Cast<GameObject>().ToArray(), CharacterCount);

        var result = new List<Character>();
        region.GetList(result, exclude: 3);

        await Assert.That(result.Count).IsEqualTo(CharacterCount - 1);
        await Assert.That(result.Any(c => c.ObjId == 3)).IsFalse();
    }

    [Test]
    public async Task GetList_ShortCircuit_RegionWithoutCharacters_ReturnsEmpty()
    {
        // Region with only NPCs (charactersSize == 0) — a Character scan must
        // short-circuit to empty without touching the object array.
        var npc = new Npc { ObjId = 1 };
        var region = CreateRegion([npc], charactersSize: 0);

        var result = new List<Character>();
        region.GetList(result, exclude: 0);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetList_ShortCircuit_PreservesCallerList_Unchanged()
    {
        // Short-circuit contract: the caller's list is returned untouched
        // (no Clear, no append) when the region has no characters.
        var npc = new Npc { ObjId = 1 };
        var region = CreateRegion([npc], charactersSize: 0);

        var result = new List<Character> { new(new UnitCustomModelParams()) { ObjId = 77 } };
        region.GetList(result, exclude: 0);

        await Assert.That(result.Select(c => c.ObjId)).IsEquivalentTo([77u]);
    }

    [Test]
    public async Task GetList_ShortCircuit_NpcScanStillWorksOnCharacterlessRegion()
    {
        // The short-circuit is Character-specific: NPC scans must still iterate.
        var npc = new Npc { ObjId = 1 };
        var region = CreateRegion([npc], charactersSize: 0);

        var result = new List<Npc>();
        region.GetList(result, exclude: 0);

        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetList_AllocationFree_FlatAllocationUnderRepeatedScans()
    {
        var region = CreateRegion(Characters.Cast<GameObject>().ToArray(), CharacterCount);
        var result = new List<Character>(CharacterCount); // pre-sized: caller growth is amortized, not per-scan

        // Warm up (JIT tiering + list shape).
        for (var i = 0; i < 1_000; i++)
        {
            result.Clear();
            region.GetList(result, 0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ScanIterations; i++)
        {
            result.Clear();
            region.GetList(result, 0);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Old code: 1 full GameObject[50] allocation per call → ~400+ bytes ×
        // 100k calls = tens of MB. Fixed code: zero per-call allocations.
        await Assert.That(allocated < 1024)
            .IsTrue()
            .Because($"100k region scans must not allocate per call (old code: tens of MB); saw {allocated} bytes");
    }

    [Test]
    public async Task GetList_ShortCircuit_CharacterlessRegion_ZeroAllocation()
    {
        var npc = new Npc { ObjId = 1 };
        var region = CreateRegion([npc], charactersSize: 0);
        var result = new List<Character>(1);

        for (var i = 0; i < 1_000; i++)
        {
            result.Clear();
            region.GetList(result, 0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ScanIterations; i++)
        {
            result.Clear();
            region.GetList(result, 0);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated < 1024)
            .IsTrue()
            .Because($"characterless-region short-circuit must be allocation-free; saw {allocated} bytes");
    }

    [Test]
    public async Task GetAround_AllocationFreeOverload_FillsNeighborhoodCharacters()
    {
        // obj sits in region R; neighbors carry characters + an NPC-only region.
        var obj = new GameObject { ObjId = 999 };
        var r = CreateRegion([obj], charactersSize: 0,
            neighbors:
            [
                CreateRegion([Characters[0], Characters[1]], charactersSize: 2),
                CreateRegion([new Npc { ObjId = 700 }], charactersSize: 0),
            ]);
        obj.Region = r;

        var result = new List<Character> { new(new UnitCustomModelParams()) }; // junk to prove Clear-first
        WorldManager.GetAround(obj, result);

        await Assert.That(result.Select(c => c.ObjId))
            .IsEquivalentTo([Characters[0].ObjId, Characters[1].ObjId]);
    }

    [Test]
    public async Task GetAround_AllocatingOverload_MatchesAllocationFree()
    {
        var obj = new GameObject { ObjId = 999 };
        var r = CreateRegion([obj], charactersSize: 0,
            neighbors:
            [
                CreateRegion([Characters[0], Characters[1]], charactersSize: 2),
                CreateRegion([Characters[2]], charactersSize: 1),
            ]);
        obj.Region = r;

        var viaFree = new List<Character>();
        WorldManager.GetAround(obj, viaFree);
        var viaAllocating = WorldManager.GetAround<Character>(obj);

        await Assert.That(viaFree.Select(c => c.ObjId))
            .IsEquivalentTo(viaAllocating.Select(c => c.ObjId));
    }
}
