using AAEmu.Game.Core.Managers;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Pure scanner tests for the M3b-4 property repair tooling. No DB — the
/// scanner operates on a state view, so every corruption rule is testable
/// in isolation.
/// </summary>
public class PropertyRepairScannerTests
{
    private static readonly IReadOnlySet<uint> ValidTemplates = new HashSet<uint> { 1, 2, 139 };
    private static readonly IReadOnlySet<uint> ExistingChars = new HashSet<uint> { 10, 11, 12 };
    private static readonly IReadOnlyDictionary<uint, int> BuildSteps = new Dictionary<uint, int>
    {
        [1] = 2, // house_design_1: 2 build steps
        [2] = 2  // house_design_2: 2 build steps
    };

    private static PropertyStateView View(
        IReadOnlyList<HouseRow> houses,
        IReadOnlyList<DoodadRow> doodads = null,
        IReadOnlySet<uint> templates = null,
        IReadOnlySet<uint> chars = null,
        IReadOnlyDictionary<uint, int> steps = null)
        => new(
            houses,
            doodads ?? [],
            templates ?? ValidTemplates,
            chars ?? ExistingChars,
            steps ?? BuildSteps);

    private static HouseRow House(uint id, uint owner = 10, uint template = 1,
        float x = 100f, float y = 200f, float z = 10f, int step = -1, int action = 0)
        => new(id, 1, owner, template, x, y, z, step, action);

    private static DoodadRow Doodad(uint id, uint houseId = 0, uint ownerId = 0, byte ownerType = 3)
        => new(id, ownerId, ownerType, houseId);

    [Test]
    public async Task Scan_HealthyState_NoIssues()
    {
        // Two houses at DISTINCT positions, valid template, live owner.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, x: 100f, y: 200f), House(2, x: 500f, y: 600f)]));

        await Assert.That(issues).IsEmpty();
    }

    [Test]
    public async Task Scan_InvalidTemplateHouse_Flagged()
    {
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, template: 9999)]));

        var hit = issues.Single(i => i.Kind == PropertyRepairIssueKind.InvalidTemplateHouse);
        await Assert.That(hit.TargetId).IsEqualTo(1u);
        await Assert.That(hit.Detail.Contains("9999")).IsTrue();
    }

    [Test]
    public async Task Scan_OrphanedOwnerHouse_Flagged()
    {
        // Owner 999 does not exist in the characters set.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, owner: 999)]));

        var hit = issues.Single(i => i.Kind == PropertyRepairIssueKind.OrphanedOwnerHouse);
        await Assert.That(hit.TargetId).IsEqualTo(1u);
    }

    [Test]
    public async Task Scan_SystemOwnedHouse_NotFlaggedAsOrphaned()
    {
        // Owner 0 = system/NPC houses (the seed lodestones) — never orphaned.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, owner: 0)]));

        await Assert.That(issues.Any(i => i.Kind == PropertyRepairIssueKind.OrphanedOwnerHouse)).IsFalse();
    }

    [Test]
    public async Task Scan_OrphanedBoundDoodad_Flagged()
    {
        // Doodad bound to house 99 which does not exist in the house list.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1)],
            [Doodad(500, houseId: 99)]));

        var hit = issues.Single(i => i.Kind == PropertyRepairIssueKind.OrphanedBoundDoodad);
        await Assert.That(hit.TargetId).IsEqualTo(500u);
    }

    [Test]
    public async Task Scan_BoundDoodadOfExistingHouse_NotFlagged()
    {
        var issues = PropertyRepairScanner.Scan(View(
            [House(1)],
            [Doodad(500, houseId: 1)]));

        await Assert.That(issues.Any(i => i.Kind == PropertyRepairIssueKind.OrphanedBoundDoodad)).IsFalse();
    }

    [Test]
    public async Task Scan_OrphanedCharacterDoodad_Flagged()
    {
        // Character-owned doodad (owner_type 254) of deleted character 999.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1)],
            [Doodad(501, ownerId: 999, ownerType: PropertyRepairScanner.DoodadOwnerTypeCharacter)]));

        var hit = issues.Single(i => i.Kind == PropertyRepairIssueKind.OrphanedDoodadOwner);
        await Assert.That(hit.TargetId).IsEqualTo(501u);
    }

    [Test]
    public async Task Scan_CharacterDoodadOfExistingChar_NotFlagged()
    {
        var issues = PropertyRepairScanner.Scan(View(
            [House(1)],
            [Doodad(501, ownerId: 10, ownerType: PropertyRepairScanner.DoodadOwnerTypeCharacter)]));

        await Assert.That(issues.Any(i => i.Kind == PropertyRepairIssueKind.OrphanedDoodadOwner)).IsFalse();
    }

    [Test]
    public async Task Scan_DuplicateHouse_KeepsLowestId_FlagsLater()
    {
        // Two houses, same owner + template + position (within 0.25m of the
        // same 0.5m cell center — duplicates on re-entry).
        var issues = PropertyRepairScanner.Scan(View(
            [House(1), House(2, x: 100.1f, y: 200.1f)]));

        var dup = issues.Single(i => i.Kind == PropertyRepairIssueKind.DuplicateHouse);
        await Assert.That(dup.TargetId).IsEqualTo(2u); // the LATER id is the duplicate
        await Assert.That(dup.Detail.Contains("1")).IsTrue(); // references the kept house
    }

    [Test]
    public async Task Scan_OutOfRangeBuildStep_Flagged()
    {
        // Template 1 has 2 steps (0..1); current_step 5 is out of range.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, step: 5, action: 0)]));

        var hit = issues.Single(i => i.Kind == PropertyRepairIssueKind.OutOfRangeBuildStep);
        await Assert.That(hit.TargetId).IsEqualTo(1u);
    }

    [Test]
    public async Task Scan_NegativeAction_Flagged()
    {
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, step: -1, action: -3)]));

        var hit = issues.Single(i => i.Kind == PropertyRepairIssueKind.OutOfRangeBuildStep);
        await Assert.That(hit.TargetId).IsEqualTo(1u);
    }

    [Test]
    public async Task Scan_FinishedHouseStepMinusOne_NotFlagged()
    {
        // Finished house: current_step = -1 is the completed state, valid.
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, step: -1, action: 0)]));

        await Assert.That(issues.Any(i => i.Kind == PropertyRepairIssueKind.OutOfRangeBuildStep)).IsFalse();
    }

    [Test]
    public async Task Scan_InvalidTemplateSkipsStepCheck()
    {
        // A house whose template is invalid must be flagged once (invalid
        // template), not twice (the step check is meaningless without a template).
        var issues = PropertyRepairScanner.Scan(View(
            [House(1, template: 9999, step: 5)]));

        await Assert.That(issues.Count(i => i.Kind == PropertyRepairIssueKind.InvalidTemplateHouse)).IsEqualTo(1);
        await Assert.That(issues.Count(i => i.Kind == PropertyRepairIssueKind.OutOfRangeBuildStep)).IsEqualTo(0);
    }
}
