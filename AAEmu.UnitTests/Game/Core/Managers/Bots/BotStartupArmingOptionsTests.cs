using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T2 supported arming — the two deployment keys. Hermetic: every case writes
/// its own Config.json / Config.Local.json into a temp directory and reads
/// through the app-path seam, so no test touches the shipped files. Env cases
/// save and restore the variable they set.
/// </summary>
[NotInParallel]
public class BotStartupArmingOptionsTests
{
    private const string ShippedBase =
        """
        {
            "Id": 1,
            "Bots": { "EnableStartupArming": false, "StartupBotCount": 2 }
        }
        """;

    private readonly List<string> _dirs = [];

    [After(Test)]
    public void Cleanup()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp directory must never fail a test.
            }
        }
    }

    private string Configs(string? baseJson, string? localJson = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aaemu-startup-arming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        if (baseJson is not null)
            File.WriteAllText(Path.Combine(dir, "Config.json"), baseJson);
        if (localJson is not null)
            File.WriteAllText(Path.Combine(dir, "Config.Local.json"), localJson);
        return dir;
    }

    private static T WithEnv<T>(string name, string? value, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Test]
    public async Task ReadEnabledFlag_NoConfigFiles_IsFalse()
    {
        var dir = Configs(baseJson: null);

        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsFalse();
        await Assert.That(BotStartupArmingOptions.ReadBotCount(dir)).IsEqualTo(2);
    }

    [Test]
    public async Task ReadEnabledFlag_ShippedDefault_IsFalse()
    {
        // The keys ship present and OFF: an unconfigured deployment arms nothing.
        var dir = Configs(ShippedBase);

        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsFalse();
        await Assert.That(BotStartupArmingOptions.ReadBotCount(dir)).IsEqualTo(2);
    }

    [Test]
    public async Task ReadEnabledFlag_LocalTrue_Arms()
    {
        var dir = Configs(ShippedBase, """{ "Bots": { "EnableStartupArming": true, "StartupBotCount": 4 } }""");

        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsTrue();
        await Assert.That(BotStartupArmingOptions.ReadBotCount(dir)).IsEqualTo(4);
    }

    [Test]
    public async Task ReadEnabledFlag_LocalFalse_OverridesBaseTrue()
    {
        // The deliberate difference from the subsystem's older "any true"
        // readers: a value PRESENT in the local file decides, so a machine
        // override can turn the switch back OFF. An OR of the two files would
        // report true here and the switch would be unsettable.
        var dir = Configs("""{ "Bots": { "EnableStartupArming": true } }""",
            """{ "Bots": { "EnableStartupArming": false } }""");

        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsFalse();
    }

    [Test]
    public async Task ReadEnabledFlag_KeyAbsentInBothFiles_IsFalse()
    {
        var dir = Configs("""{ "Bots": { "EnableE2EBridge": true } }""",
            """{ "Bots": { "E2EBridgePort": 1260 } }""");

        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsFalse();
    }

    [Test]
    public async Task ReadEnabledFlag_StringFlagForms_AreAccepted()
    {
        var dir = Configs("""{ "Bots": { "EnableStartupArming": "true" } }""");
        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsTrue();

        var off = Configs("""{ "Bots": { "EnableStartupArming": "false" } }""");
        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(off)).IsFalse();
    }

    [Test]
    public async Task ReadEnabledFlag_EnvWins_OverTheConfigFiles()
    {
        var on = Configs(ShippedBase, """{ "Bots": { "EnableStartupArming": false } }""");
        var withEnvOn = WithEnv(BotStartupArmingOptions.EnvEnabled, "1",
            () => BotStartupArmingOptions.ReadEnabledFlag(on));

        var off = Configs(ShippedBase, """{ "Bots": { "EnableStartupArming": true } }""");
        var withEnvOff = WithEnv(BotStartupArmingOptions.EnvEnabled, "0",
            () => BotStartupArmingOptions.ReadEnabledFlag(off));

        await Assert.That(withEnvOn).IsTrue();
        await Assert.That(withEnvOff).IsFalse();
    }

    [Test]
    public async Task ReadBotCount_EnvWins_AndIsClampedToMaxPresenceBots()
    {
        var dir = Configs(ShippedBase, """{ "Bots": { "StartupBotCount": 4 } }""");

        var fromEnv = WithEnv("AAEMU_PRESENCE_MAX_BOTS", "10", () =>
            WithEnv(BotStartupArmingOptions.EnvBotCount, "3", () => BotStartupArmingOptions.ReadBotCount(dir)));

        // Clamped, never trusted: the switch cannot enroll more bots than the
        // presence bound allows.
        var clamped = WithEnv("AAEMU_PRESENCE_MAX_BOTS", "10", () =>
            WithEnv(BotStartupArmingOptions.EnvBotCount, "50", () => BotStartupArmingOptions.ReadBotCount(dir)));

        // An unusable env value is not a count at all: it falls through to the
        // configured value (4) rather than arming the host for zero bots.
        var zero = WithEnv("AAEMU_PRESENCE_MAX_BOTS", "10", () =>
            WithEnv(BotStartupArmingOptions.EnvBotCount, "0", () => BotStartupArmingOptions.ReadBotCount(dir)));
        var nonsense = WithEnv("AAEMU_PRESENCE_MAX_BOTS", "10", () =>
            WithEnv(BotStartupArmingOptions.EnvBotCount, "not-a-number", () => BotStartupArmingOptions.ReadBotCount(dir)));

        await Assert.That(fromEnv).IsEqualTo(3);
        await Assert.That(clamped).IsEqualTo(10);
        await Assert.That(zero).IsEqualTo(4);
        await Assert.That(nonsense).IsEqualTo(4);
    }

    [Test]
    public async Task ReadBotCount_ConfigValue_IsUsed_AndClampedToThePresenceBound()
    {
        var dir = Configs(ShippedBase, """{ "Bots": { "StartupBotCount": 4 } }""");

        var atTen = WithEnv("AAEMU_PRESENCE_MAX_BOTS", "10", () => BotStartupArmingOptions.ReadBotCount(dir));
        var atTwo = WithEnv("AAEMU_PRESENCE_MAX_BOTS", "2", () => BotStartupArmingOptions.ReadBotCount(dir));

        await Assert.That(atTen).IsEqualTo(4);
        await Assert.That(atTwo).IsEqualTo(2);
    }

    [Test]
    public async Task ReadBotCount_AbsentEverywhere_IsTheDefault()
    {
        var dir = Configs("""{ "Bots": { "EnableStartupArming": true } }""");

        await Assert.That(BotStartupArmingOptions.ReadBotCount(dir))
            .IsEqualTo(BotStartupArmingOptions.DefaultBotCount);
    }

    [Test]
    public async Task ReadEnabledFlag_LocalBotsBlockWithoutTheKey_FallsThroughToBase()
    {
        // Presence-based precedence is per KEY, not per file: a local Bots
        // block that says nothing about the switch must not blank the base
        // file's value (review Q7 gap).
        var dir = Configs(
            """{ "Bots": { "EnableStartupArming": true } }""",
            """{ "Bots": { "StartupBotCount": 5 } }""");

        await Assert.That(BotStartupArmingOptions.ReadEnabledFlag(dir)).IsTrue();
    }

    [Test]
    public async Task ReadBotCount_LocalBotsBlockWithoutTheKey_FallsThroughToBase()
    {
        var dir = Configs(
            """{ "Bots": { "StartupBotCount": 4 } }""",
            """{ "Bots": { "EnableStartupArming": true } }""");

        await Assert.That(BotStartupArmingOptions.ReadBotCount(dir)).IsEqualTo(4);
    }
}
