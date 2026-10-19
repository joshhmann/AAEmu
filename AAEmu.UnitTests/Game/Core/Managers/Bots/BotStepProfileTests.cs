using AAEmu.Game.Core.Managers.Bots;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

public class BotStepProfileTests
{
    [Test]
    public async Task Mark_ChargesTimeSinceThePreviousMark_AndRepeatsAddUp()
    {
        var phases = new List<(string Phase, long Ticks)>();
        BotStepProfile.Begin();
        BotStepProfile.Mark("a");
        Thread.Sleep(20);
        BotStepProfile.Mark("b");
        BotStepProfile.Mark("a");
        BotStepProfile.End(phases);

        await Assert.That(phases.Select(p => p.Phase)).IsEquivalentTo(["a", "b", "other"]);
        await Assert.That(BotStepProfile.ToMs(phases.Single(p => p.Phase == "b").Ticks)).IsGreaterThanOrEqualTo(15d);
        await Assert.That(BotStepProfile.Describe(phases, minMs: 10)).StartsWith("b=");
    }

    [Test]
    public async Task OutsideAnOpenProfile_MarksAreIgnored()
    {
        var phases = new List<(string Phase, long Ticks)>();
        BotStepProfile.Begin();
        BotStepProfile.End(phases);
        BotStepProfile.Mark("late");
        BotStepProfile.End(phases);

        await Assert.That(phases).IsEmpty();
    }
}
