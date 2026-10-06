using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

/// <summary>
/// <see cref="UnitReqs.Validate"/> coverage for the time-of-day gate.
/// <list type="bullet">
/// <item><c>Tod</c> rows carrying <c>value1=0, value2=0</c> are the data's
/// UNSET sentinel (31 of the 51 <c>Tod</c> rows in <c>compact.sqlite3</c>,
/// including <c>owner_id=4</c> = 원거리 공격, the ranged auto-attack). Treating
/// them as a hard <c>[0,0]</c> window made every such skill refuse to fire for
/// all but ~6 game-seconds of the day cycle, because
/// <see cref="TimeManager.Start"/> pins the clock to 12:00.</item>
/// <item>Well-formed windows — including one that spans midnight
/// numerically — must keep enforcing the same inclusive range as before.</item>
/// </list>
/// </summary>
[NotInParallel]
public class UnitReqsTests
{
    /// <summary>Ranged auto-attack: <c>unit_reqs</c> rowid 2037, <c>owner_type='Skill', owner_id=4, kind_id=29</c>.</summary>
    private const uint RangedAutoAttackSkillId = 4;

    private static UnitReqs Tod(uint value1, uint value2) => new()
    {
        OwnerId = RangedAutoAttackSkillId,
        OwnerType = "Skill",
        KindType = UnitReqsKindType.Tod,
        Value1 = value1,
        Value2 = value2
    };

    private static UnitReqsValidationResult ValidateAt(float gameTimeHours, UnitReqs req)
    {
        var original = TimeManager.Instance.Get();
        try
        {
            TimeManager.Instance.Set(gameTimeHours);
            return req.Validate(new Npc(), new Npc());
        }
        finally
        {
            TimeManager.Instance.Set(original / 3600f);
        }
    }

    private static async Task AssertOkAsync(UnitReqsValidationResult result) =>
        await Assert.That(result.ResultKey)
            .IsEqualTo(SkillResultKeys.ok)
            .Because($"expected the requirement to pass, got '{result.ResultKey}'");

    private static async Task AssertRefusedAsync(UnitReqsValidationResult result) =>
        await Assert.That(result.ResultKey)
            .IsEqualTo(SkillResultKeys.skill_urk_tod)
            .Because($"expected a skill_urk_tod refusal, got '{result.ResultKey}'");

    #region Tod — unset sentinel

    [Test]
    public async Task Validate_TodUnsetSentinelAtNoon_ReturnsOk()
    {
        // Arrange: the exact (0,0) row shipped for skill 4.
        var req = Tod(0, 0);

        // Act
        var result = ValidateAt(12f, req);

        // Assert
        await Assert.That(result).IsNotNull();
        await AssertOkAsync(result);
    }

    [Test]
    public async Task Validate_TodUnsetSentinelAcrossWholeDay_ReturnsOk()
    {
        var req = Tod(0, 0);

        foreach (var hour in new[] { 0f, 3.5f, 6.25f, 12f, 18.75f, 23.99f })
        {
            await AssertOkAsync(ValidateAt(hour, req));
        }

        await Assert.That(true).IsTrue();
    }

    #endregion

    #region Tod — well-formed windows keep working

    [Test]
    public async Task Validate_TodWindowInsideRange_ReturnsOk()
    {
        // 600..800 == 06:00:00 .. 08:00:00
        var req = Tod(600, 800);

        await AssertOkAsync(ValidateAt(7f, req));
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Validate_TodWindowBelowRange_IsRefused()
    {
        var req = Tod(600, 800);

        await AssertRefusedAsync(ValidateAt(5.99f, req));
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Validate_TodWindowAboveRange_IsRefused()
    {
        var req = Tod(600, 800);

        await AssertRefusedAsync(ValidateAt(8.01f, req));
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Validate_TodWindowEnclosingMidnight_PassesAtMidnightHour()
    {
        // 0..300 == 00:00:00 .. 03:00:00 — a window that includes midnight numerically.
        var req = Tod(0, 300);

        await AssertOkAsync(ValidateAt(0f, req));
        await AssertOkAsync(ValidateAt(1.5f, req));
        await AssertOkAsync(ValidateAt(3f, req));
        await AssertRefusedAsync(ValidateAt(3.01f, req));

        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Validate_TodZeroValue1WithNonZeroValue2_StillEnforced()
    {
        // Only the (0,0) pair is the sentinel; value1=0 alone keeps its range meaning.
        var req = Tod(0, 300);

        await AssertRefusedAsync(ValidateAt(12f, req));
        await Assert.That(true).IsTrue();
    }

    #endregion

    #region Other kinds keep their existing zero-value conventions

    [Test]
    public async Task Validate_LevelZeroValue2MeansUnbounded_StaysUnchanged()
    {
        // Pre-existing convention: `(Value2 == 0 || unit.Level <= Value2)` — a zero
        // Value2 is "no upper bound", NOT a degenerate zero window.
        var unit = new Npc { Level = 45 };

        var openEnded = new UnitReqs { KindType = UnitReqsKindType.Level, Value1 = 10, Value2 = 0 };
        var bounded = new UnitReqs { KindType = UnitReqsKindType.Level, Value1 = 10, Value2 = 20 };

        await Assert.That(openEnded.Validate(unit, unit).ResultKey).IsEqualTo(SkillResultKeys.ok);
        await Assert.That(bounded.Validate(unit, unit).ResultKey).IsEqualTo(SkillResultKeys.skill_urk_level);
    }

    [Test]
    public async Task Validate_LevelBothZeroMeansNoConstraint_StaysUnchanged()
    {
        // The precedent this fix mirrors: `Level` reads `unit.Level >= Value1 && (Value2 == 0 || unit.Level <= Value2)`,
        // so an all-zero row imposes NO constraint at ANY level — zero is already this file's
        // "unset" idiom, and `Tod` was the outlier that read (0,0) as a hard zero-width window.
        var req = new UnitReqs { KindType = UnitReqsKindType.Level, Value1 = 0, Value2 = 0 };

        var levelZero = new Npc { Level = 0 };
        var levelOne = new Npc { Level = 1 };
        var levelHigh = new Npc { Level = 99 };

        await Assert.That(req.Validate(levelZero, levelZero).ResultKey).IsEqualTo(SkillResultKeys.ok);
        await Assert.That(req.Validate(levelOne, levelOne).ResultKey).IsEqualTo(SkillResultKeys.ok);
        await Assert.That(req.Validate(levelHigh, levelHigh).ResultKey).IsEqualTo(SkillResultKeys.ok);
    }

    [Test]
    public async Task Validate_AlwaysTrueKind_ReturnsOk_StaysUnchanged()
    {
        // CrimePoint / CrimeRecord / VerdictOnly are hard-coded passes.
        var unit = new Npc { Level = 7 };

        await Assert.That(new UnitReqs { KindType = UnitReqsKindType.VerdictOnly }.Validate(unit, unit).ResultKey)
            .IsEqualTo(SkillResultKeys.ok);
        await Assert.That(new UnitReqs { KindType = UnitReqsKindType.CrimePoint, Value1 = 5 }.Validate(unit, unit).ResultKey)
            .IsEqualTo(SkillResultKeys.ok);
    }

    [Test]
    public async Task Validate_UnhandledKind_ReturnsOk_StaysUnchanged()
    {
        var unit = new Npc();

        await Assert.That(new UnitReqs { KindType = (UnitReqsKindType)9999 }.Validate(unit, unit).ResultKey)
            .IsEqualTo(SkillResultKeys.ok);
    }

    #endregion
}