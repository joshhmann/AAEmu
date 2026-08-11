using AAEmu.Game.Services.WebApi.Controllers;

namespace AAEmu.UnitTests.Game.Services.WebApi;

/// <summary>
/// Rig for the bot control API gate (P1 t_2ea94a20): enable flag + token
/// resolution + fixed-time token match. Pure decisions over injected env +
/// config contents (no disk, no singletons — mirrors the
/// BotPresenceCoordinator.IsEnabled semantics it was built against).
/// </summary>
[NotInParallel]
public class BotControlApiTests
{
    private const string FlaggedConfig = """{"Bots": {"EnableBotControl": true, "BotControlToken": "cfg-tok"}}""";
    private static readonly (string Name, string? Json)[] NoConfigs = [];

    // ---------------------------------------------------------------- enable

    [Test]
    public async Task IsEnabled_NoEnvNoConfig_IsFalse()
    {
        await Assert.That(BotControlSettings.IsEnabled(null, NoConfigs)).IsFalse();
    }

    [Test]
    public async Task IsEnabled_EnvOne_IsTrue()
    {
        await Assert.That(BotControlSettings.IsEnabled("1", NoConfigs)).IsTrue();
    }

    [Test]
    public async Task IsEnabled_EnvTrue_IsTrue()
    {
        await Assert.That(BotControlSettings.IsEnabled("true", NoConfigs)).IsTrue();
    }

    [Test]
    public async Task IsEnabled_EnvZero_IsFalse()
    {
        await Assert.That(BotControlSettings.IsEnabled("0", NoConfigs)).IsFalse();
    }

    [Test]
    public async Task IsEnabled_ConfigLocalFlag_IsTrue()
    {
        await Assert.That(BotControlSettings.IsEnabled(null, [("Config.Local.json", FlaggedConfig)])).IsTrue();
    }

    [Test]
    public async Task IsEnabled_ConfigJsonFallback_IsTrue_WhenLocalAbsent()
    {
        await Assert.That(BotControlSettings.IsEnabled(null,
            [("Config.Local.json", null), ("Config.json", FlaggedConfig)])).IsTrue();
    }

    [Test]
    public async Task IsEnabled_UnflaggedConfig_IsFalse()
    {
        await Assert.That(BotControlSettings.IsEnabled(null,
            [("Config.Local.json", """{"Bots": {"EnablePresenceDemo": true}}""")])).IsFalse();
    }

    [Test]
    public async Task IsEnabled_MalformedConfig_IsFalse()
    {
        await Assert.That(BotControlSettings.IsEnabled(null, [("Config.json", "{not json")])).IsFalse();
    }

    // ----------------------------------------------------------------- token

    [Test]
    public async Task GetToken_EnvWins_OverConfig()
    {
        var token = BotControlSettings.GetToken("env-tok", [("Config.Local.json", FlaggedConfig)]);
        await Assert.That(token).IsEqualTo("env-tok");
    }

    [Test]
    public async Task GetToken_ConfigFallback_WhenNoEnv()
    {
        var token = BotControlSettings.GetToken(null, [("Config.Local.json", FlaggedConfig)]);
        await Assert.That(token).IsEqualTo("cfg-tok");
    }

    [Test]
    public async Task GetToken_NothingConfigured_IsNull()
    {
        await Assert.That(BotControlSettings.GetToken(null, NoConfigs)).IsNull();
    }

    // ------------------------------------------------------------ token match

    [Test]
    public async Task TokenMatches_ExactToken_IsTrue()
    {
        await Assert.That(BotControlSettings.TokenMatches("env-tok", "env-tok", NoConfigs)).IsTrue();
    }

    [Test]
    public async Task TokenMatches_WrongToken_IsFalse()
    {
        await Assert.That(BotControlSettings.TokenMatches("wrong", "env-tok", NoConfigs)).IsFalse();
    }

    [Test]
    public async Task TokenMatches_NoConfiguredToken_FailsClosed()
    {
        await Assert.That(BotControlSettings.TokenMatches("anything", null, NoConfigs)).IsFalse();
    }

    [Test]
    public async Task TokenMatches_EmptyProvided_FailsClosed()
    {
        await Assert.That(BotControlSettings.TokenMatches(string.Empty, "env-tok", NoConfigs)).IsFalse();
    }

    [Test]
    public async Task TokenMatches_ConfigFallbackToken_IsTrue()
    {
        await Assert.That(BotControlSettings.TokenMatches("cfg-tok", null, [("Config.Local.json", FlaggedConfig)])).IsTrue();
    }
}
