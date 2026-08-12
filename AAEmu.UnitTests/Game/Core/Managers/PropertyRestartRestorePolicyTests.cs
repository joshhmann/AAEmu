using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M3b-3 (t_743866f9): server restart restoration, disconnect/logout cleanup, and
/// orphan/duplicate prevention. These tests pin the hermetic policy seams that decide
/// which DB rows may be loaded at boot:
///  - <see cref="HousingManager.ShouldLoadHouseRow"/>: a `housings` row is loadable only
///    when its template exists in game data AND it still has an owner + account. Owner-less
///    rows are demolished/expired houses whose row survived a crash (mid-save kill); loading
///    them spawns a zombie house that can never be saved again.
///  - <see cref="SpawnManager.ShouldLoadPersistentDoodad"/>: a persistent `doodads` row is
///    loadable only when its template exists, and (Housing owner type) its owning house and
///    parent doodad still exist. Orphaned children (parent row lost in a mid-save kill)
///    must not be dropped into the world as floating, un-parented doodads.
///
/// The DB-touching halves (SaveDirtyHousesForCharacter, DeleteHouseRowImmediately,
/// LoadPlayerHousing, SpawnPersistentDoodads) are exercised by the M3b EXIT gate's E2E
/// restart scenario (t_accb1c63) — unit rigs must not open MySQL connections
/// (MySQL.CreateConnection() performs a live Open()).
/// </summary>
public class PropertyRestartRestorePolicyTests
{
    // ------------------------------------------------------------------ ShouldLoadHouseRow

    [Test]
    public async Task ShouldLoadHouseRow_UnknownTemplate_SkipsRow()
    {
        var load = HousingManager.ShouldLoadHouseRow(null, ownerId: 42, accountId: 7, out var skipReason);

        await Assert.That(load).IsFalse();
        await Assert.That(skipReason).Contains("template");
    }

    [Test]
    public async Task ShouldLoadHouseRow_OwnerlessRow_SkipsRow()
    {
        var template = new HousingTemplate { Id = 1 };
        var load = HousingManager.ShouldLoadHouseRow(template, ownerId: 0, accountId: 7, out var skipReason);

        await Assert.That(load).IsFalse();
        await Assert.That(skipReason).Contains("owner");
    }

    [Test]
    public async Task ShouldLoadHouseRow_AccountlessRow_SkipsRow()
    {
        var template = new HousingTemplate { Id = 1 };
        var load = HousingManager.ShouldLoadHouseRow(template, ownerId: 42, accountId: 0, out var skipReason);

        await Assert.That(load).IsFalse();
        await Assert.That(skipReason).Contains("owner");
    }

    [Test]
    public async Task ShouldLoadHouseRow_ValidRow_Loads()
    {
        var template = new HousingTemplate { Id = 1 };
        var load = HousingManager.ShouldLoadHouseRow(template, ownerId: 42, accountId: 7, out var skipReason);

        await Assert.That(load).IsTrue();
        await Assert.That(skipReason).IsNull();
    }

    // ------------------------------------------------------------------ ShouldLoadPersistentDoodad

    [Test]
    public async Task ShouldLoadPersistentDoodad_UnknownTemplate_SkipsRow()
    {
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            null, DoodadOwnerType.Housing, houseId: 5, owningHouse: new House { Id = 5 },
            parentDoodad: 0, parentFound: false, out var skipReason);

        await Assert.That(load).IsFalse();
        await Assert.That(skipReason).Contains("template");
    }

    [Test]
    public async Task ShouldLoadPersistentDoodad_HousingOwnerHouseMissing_SkipsRow()
    {
        var template = new DoodadTemplate { Id = 100 };
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            template, DoodadOwnerType.Housing, houseId: 5, owningHouse: null,
            parentDoodad: 0, parentFound: false, out var skipReason);

        await Assert.That(load).IsFalse();
        await Assert.That(skipReason).Contains("house");
    }

    [Test]
    public async Task ShouldLoadPersistentDoodad_HousingParentDoodadMissing_SkipsRow()
    {
        var template = new DoodadTemplate { Id = 100 };
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            template, DoodadOwnerType.Housing, houseId: 5, owningHouse: new House { Id = 5 },
            parentDoodad: 77, parentFound: false, out var skipReason);

        await Assert.That(load).IsFalse();
        await Assert.That(skipReason).Contains("parent");
    }

    [Test]
    public async Task ShouldLoadPersistentDoodad_HousingHousePresentNoParent_Loads()
    {
        var template = new DoodadTemplate { Id = 100 };
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            template, DoodadOwnerType.Housing, houseId: 5, owningHouse: new House { Id = 5 },
            parentDoodad: 0, parentFound: false, out var skipReason);

        await Assert.That(load).IsTrue();
        await Assert.That(skipReason).IsNull();
    }

    [Test]
    public async Task ShouldLoadPersistentDoodad_HousingParentPresent_Loads()
    {
        var template = new DoodadTemplate { Id = 100 };
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            template, DoodadOwnerType.Housing, houseId: 5, owningHouse: new House { Id = 5 },
            parentDoodad: 77, parentFound: true, out var skipReason);

        await Assert.That(load).IsTrue();
        await Assert.That(skipReason).IsNull();
    }

    [Test]
    public async Task ShouldLoadPersistentDoodad_NonHousingOwnerHouseMissing_StillLoads()
    {
        // Conservative scope: only Housing-type rows are orphan-checked. A crop/pack row
        // whose owning house is gone keeps the pre-existing warn-and-load behavior — the
        // house link is a reference value only for non-Housing owner types.
        var template = new DoodadTemplate { Id = 100 };
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            template, DoodadOwnerType.Slave, houseId: 5, owningHouse: null,
            parentDoodad: 0, parentFound: false, out var skipReason);

        await Assert.That(load).IsTrue();
        await Assert.That(skipReason).IsNull();
    }

    [Test]
    public async Task ShouldLoadPersistentDoodad_ValidRow_Loads()
    {
        var template = new DoodadTemplate { Id = 100 };
        var load = SpawnManager.ShouldLoadPersistentDoodad(
            template, DoodadOwnerType.Housing, houseId: 0, owningHouse: null,
            parentDoodad: 0, parentFound: false, out var skipReason);

        await Assert.That(load).IsTrue();
        await Assert.That(skipReason).IsNull();
    }
}
