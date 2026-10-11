namespace AAEmu.UnitTests.Game.Quests.Scenario;

public class QuestScenarioDiagnosticTests
{
    [Test]
    [Arguments(4, "objective[1]")]
    [Arguments(20, "step")]
    [Arguments(21, "acceptorType")]
    [Arguments(22, "componentId")]
    [Arguments(26, "acceptorId")]
    [Arguments(30, "time")]
    public async Task PersistMismatch_KnownLayout_NamesChangedField(int offset, string field)
    {
        var before = new byte[38];
        var after = new byte[38];
        after[offset] = 1;
        var reason = QuestScenarioAssertions.DescribePersistDiff(before, after);
        await Assert.That(reason).Contains($"byte {offset}");
        await Assert.That(reason).Contains($"field {field}");
    }

    [Test]
    public async Task PersistMismatch_ShortLayout_ReportsOffsetWithoutReadingPastEnd()
    {
        var reason = QuestScenarioAssertions.DescribePersistDiff([0], [1]);
        await Assert.That(reason).Contains("unknown layout");
    }

    [Test]
    public async Task PersistMismatch_DifferentLengths_NamesBothSizes()
    {
        await Assert.That(QuestScenarioAssertions.DescribePersistDiff([0], [0, 1]))
            .IsEqualTo("length 1 -> 2 bytes");
    }
}
