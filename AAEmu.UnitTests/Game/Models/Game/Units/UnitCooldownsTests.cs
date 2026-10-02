
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public class UnitCooldownsTests
{
    [Test]
    public async Task AddCooldown_ShouldAddCooldown_WhenSkillNotExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        var duration = 5000u;

        // Act
        cooldowns.AddCooldown(skillId, duration);

        // Assert
        await Assert.That(cooldowns.Cooldowns.ContainsKey(skillId)).IsTrue();
    }

    [Test]
    public async Task AddCooldown_ShouldNotDuplicate_WhenSkillAlreadyExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        var duration1 = 5000u;
        var duration2 = 10000u;

        // Act
        cooldowns.AddCooldown(skillId, duration1);
        cooldowns.AddCooldown(skillId, duration2);

        // Assert
        await Assert.That(cooldowns.Cooldowns).HasSingleItem();
    }

    [Test]
    public async Task CheckCooldown_ShouldReturnFalse_WhenSkillNotExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        // Act
        var result = cooldowns.CheckCooldown(skillId);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CheckCooldown_ShouldReturnTrue_WhenCooldownIsActive()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        var duration = 60000u; // 60 seconds

        // Manually add a cooldown with a future end time
        cooldowns.Cooldowns.TryAdd(skillId, DateTime.UtcNow.AddMilliseconds(duration));

        // Act
        var result = cooldowns.CheckCooldown(skillId);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CheckCooldown_ShouldReturnFalseAndRemove_WhenCooldownExpired()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        // Manually add an expired cooldown
        cooldowns.Cooldowns.TryAdd(skillId, DateTime.UtcNow.AddSeconds(-1));

        // Act
        var result = cooldowns.CheckCooldown(skillId);

        // Assert
        await Assert.That(result).IsFalse();
        await Assert.That(cooldowns.Cooldowns.ContainsKey(skillId)).IsFalse();
    }

    [Test]
    public async Task RemoveCooldown_ShouldRemoveSkill_WhenExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        cooldowns.Cooldowns.TryAdd(skillId, DateTime.UtcNow.AddMinutes(1));

        // Act
        cooldowns.RemoveCooldown(skillId);

        // Assert
        await Assert.That(cooldowns.Cooldowns.ContainsKey(skillId)).IsFalse();
    }

    [Test]
    public async Task RemoveCooldown_RemovesTheTargetAndLeavesOthersIntact()
    {
        // The old test asserted nothing ("should not throw") and passed with
        // RemoveCooldown gutted to `{ }`. The absent-key case on its own cannot
        // detect that either: TryRemove on a missing key is a no-op whether the
        // method runs or not.
        //
        // The contract that IS observable is selective removal: the named skill
        // goes, every other cooldown survives, and an absent key is ignored.
        var cooldowns = new UnitCooldowns();
        var targetId = 100u;
        var otherId = 200u;
        var missingId = 300u;
        var expiry = DateTime.UtcNow.AddMinutes(1);
        cooldowns.Cooldowns.TryAdd(targetId, expiry);
        cooldowns.Cooldowns.TryAdd(otherId, expiry);

        // Act
        cooldowns.RemoveCooldown(targetId);
        cooldowns.RemoveCooldown(missingId);

        // Assert
        await Assert.That(cooldowns.Cooldowns.ContainsKey(targetId)).IsFalse()
            .Because("the named skill's cooldown must actually be removed");
        await Assert.That(cooldowns.Cooldowns.ContainsKey(otherId)).IsTrue()
            .Because("removal must be selective: other cooldowns survive");
        await Assert.That(cooldowns.Cooldowns.ContainsKey(missingId)).IsFalse()
            .Because("removing an absent skill is a no-op, not an error");
        await Assert.That(cooldowns.Cooldowns.Count).IsEqualTo(1)
            .Because("exactly one of the two seeded cooldowns remains");
        await Assert.That(cooldowns.Cooldowns[otherId]).IsEqualTo(expiry)
            .Because("the surviving cooldown must keep its original expiry");
    }

    [Test]
    [Arguments(0u)]
    [Arguments(1u)]
    [Arguments(100u)]
    [Arguments(999999u)]
    public async Task AddCooldown_ShouldAcceptVariousSkillIds(uint skillId)
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var duration = 5000u;

        // Act
        cooldowns.AddCooldown(skillId, duration);

        // Assert
        await Assert.That(cooldowns.Cooldowns.ContainsKey(skillId)).IsTrue();
    }

    [Test]
    [Arguments(0u)]
    [Arguments(100u)]
    [Arguments(60000u)]
    [Arguments(uint.MaxValue)]
    public async Task CheckCooldown_ShouldHandleVariousDurations(uint duration)
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        if (duration > 250) // Only add if it would be considered "active"
        {
            cooldowns.Cooldowns.TryAdd(skillId, DateTime.UtcNow.AddMilliseconds(duration));
            var result = cooldowns.CheckCooldown(skillId);
            await Assert.That(result).IsTrue();
        }
        else
        {
            cooldowns.Cooldowns.TryAdd(skillId, DateTime.UtcNow.AddMilliseconds(duration));
            var result = cooldowns.CheckCooldown(skillId);
            await Assert.That(result).IsFalse();
        }
    }
}