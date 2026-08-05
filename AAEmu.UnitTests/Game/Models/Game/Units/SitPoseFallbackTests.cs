using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

/// <summary>
/// Sit-pose fallback remap tests. Ground truth: the 1.2 client (r208022) game_pak ships
/// fist_pos_sit_* .caf assets only for a subset of race/gender models; unplayable anim ids
/// are remapped to the closest playable sit anim. Stand/walk poses must pass through untouched.
/// </summary>
public class SitPoseFallbackTests
{
    // --- sit-range remaps (ids 25-224) ---

    [Test]
    public async Task Resolve_ElfMale_SitLean_ReturnsChairRest()
    {
        // fist_pos_sit_lean_idle (26) has no hariharan (elf) assets -> chair_rest (141)
        await Assert.That(SitPoseFallback.Resolve(26, (byte)Race.Elf, (byte)Gender.Male)).IsEqualTo(141u);
    }

    [Test]
    public async Task Resolve_ElfFemale_SitLean_ReturnsChairRest()
    {
        await Assert.That(SitPoseFallback.Resolve(26, (byte)Race.Elf, (byte)Gender.Female)).IsEqualTo(141u);
    }

    [Test]
    public async Task Resolve_ElfMale_SitCrouch_ReturnsFurniturerepair()
    {
        // fist_pos_sit_crouch_idle (25) has no hariharan assets -> crouch_furniturerepair (224)
        await Assert.That(SitPoseFallback.Resolve(25, (byte)Race.Elf, (byte)Gender.Male)).IsEqualTo(224u);
    }

    [Test]
    public async Task Resolve_ElfFemale_SitCrouch_ReturnsLivestock()
    {
        // crouch_furniturerepair (224) is male-only -> crouch_livestock (223) for females
        await Assert.That(SitPoseFallback.Resolve(25, (byte)Race.Elf, (byte)Gender.Female)).IsEqualTo(223u);
    }

    [Test]
    public async Task Resolve_NuianMale_CrouchInvestigation_ReturnsFurniturerepair()
    {
        // fist_pos_sit_crouch_investigation_idle (70) has no assets for anyone -> 224 (all males)
        await Assert.That(SitPoseFallback.Resolve(70, (byte)Race.Nuian, (byte)Gender.Male)).IsEqualTo(224u);
    }

    [Test]
    public async Task Resolve_NuianFemale_CrouchInvestigation_ReturnsLivestock()
    {
        await Assert.That(SitPoseFallback.Resolve(70, (byte)Race.Nuian, (byte)Gender.Female)).IsEqualTo(223u);
    }

    [Test]
    public async Task Resolve_ElfMale_CrouchInvestigation_ReturnsFurniturerepair()
    {
        await Assert.That(SitPoseFallback.Resolve(70, (byte)Race.Elf, (byte)Gender.Male)).IsEqualTo(224u);
    }

    [Test]
    public async Task Resolve_NuianFemale_ChairSnooze_ReturnsChairRest()
    {
        // fist_pos_sit_chair_snooze_idle (160) has no assets at all -> chair_rest (141)
        await Assert.That(SitPoseFallback.Resolve(160, (byte)Race.Nuian, (byte)Gender.Female)).IsEqualTo(141u);
    }

    [Test]
    public async Task Resolve_WarbornMale_ChairSnooze_ReturnsChairRest()
    {
        await Assert.That(SitPoseFallback.Resolve(160, (byte)Race.Warborn, (byte)Gender.Male)).IsEqualTo(141u);
    }

    [Test]
    public async Task Resolve_NuianMale_ChairNurseryDealer_Unchanged()
    {
        // 87 has nuian male assets -> playable, no remap
        await Assert.That(SitPoseFallback.Resolve(87, (byte)Race.Nuian, (byte)Gender.Male)).IsEqualTo(87u);
    }

    [Test]
    public async Task Resolve_NuianFemale_ChairNurseryDealer_ReturnsChairRest()
    {
        // 87 is male-only -> 141 for females
        await Assert.That(SitPoseFallback.Resolve(87, (byte)Race.Nuian, (byte)Gender.Female)).IsEqualTo(141u);
    }

    [Test]
    public async Task Resolve_DwarfFemale_SitLean_Unchanged()
    {
        // no playable lean/chair fallback exists for dwarf female -> keep the original id
        await Assert.That(SitPoseFallback.Resolve(26, (byte)Race.Dwarf, (byte)Gender.Female)).IsEqualTo(26u);
    }

    // --- out of sit range / non-sit poses must pass through ---

    [Test]
    public async Task Resolve_StandAnim_Unchanged()
    {
        // fist_pos_stn_armor_dealer_idle (100) — stand pose, out of scope
        await Assert.That(SitPoseFallback.Resolve(100, (byte)Race.Elf, (byte)Gender.Male)).IsEqualTo(100u);
    }

    [Test]
    public async Task Resolve_Zero_Unchanged()
    {
        await Assert.That(SitPoseFallback.Resolve(0, (byte)Race.Elf, (byte)Gender.Male)).IsEqualTo(0u);
    }

    [Test]
    public async Task Resolve_AboveSitRange_Unchanged()
    {
        await Assert.That(SitPoseFallback.Resolve(300, (byte)Race.Nuian, (byte)Gender.Female)).IsEqualTo(300u);
    }

    [Test]
    public async Task Resolve_UnknownRace_Unchanged()
    {
        await Assert.That(SitPoseFallback.Resolve(26, (byte)42, (byte)Gender.Male)).IsEqualTo(26u);
    }
}
