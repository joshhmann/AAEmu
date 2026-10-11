using Xunit;

namespace AAEmu.IntegrationTests.E2e;

// Pure setup contracts. These tests do not boot servers or touch Docker/MySQL.
public class E2eSetupGuardTests
{
    [Fact]
    public void LogGuard_FreshHost_UsesRepositoryCopy()
    {
        WithScratch((runtime, repo) =>
        {
            var expected = Path.Combine(repo, "Scripts", "e2e", "ensure-log-caps.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            File.WriteAllText(expected, "repo");
            Assert.Equal(expected, E2eStack.ResolveLogCapsGuard(runtime, repo));
        });
    }

    [Fact]
    public void LogGuard_RuntimeOverride_WinsOverRepositoryCopy()
    {
        WithScratch((runtime, repo) =>
        {
            var expected = Path.Combine(runtime, "ensure-log-caps.sh");
            File.WriteAllText(expected, "runtime");
            var fallback = Path.Combine(repo, "Scripts", "e2e", "ensure-log-caps.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            File.WriteAllText(fallback, "repo");
            Assert.Equal(expected, E2eStack.ResolveLogCapsGuard(runtime, repo));
        });
    }

    [Fact]
    public void LogGuard_NeitherCopyExists_FailsBeforeBoot()
        => WithScratch((runtime, repo) =>
            Assert.Throws<FileNotFoundException>(() => E2eStack.ResolveLogCapsGuard(runtime, repo)));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public void FreshSeed_PreviousQuestRows_Refused(long active, long completed)
        => Assert.Throws<InvalidOperationException>(() => E2eStack.ValidateFreshQuestState(active, completed));

    [Fact]
    public void FreshSeed_EmptyQuestTables_Accepted()
        => E2eStack.ValidateFreshQuestState(0, 0);

    private static void WithScratch(Action<string, string> check)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "aaemu guard " + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(scratch, "runtime-root");
        var repo = Path.Combine(scratch, "repo");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(repo);
        try { check(runtime, repo); }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
