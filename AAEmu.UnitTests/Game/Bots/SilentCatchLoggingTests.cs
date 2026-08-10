using NLog;
using NLog.Config;
using NLog.Targets;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// Kimi audit follow-up (t_13ebabfd, 2026-08-09) — silent catches: the
/// appearance factory's equipment source (BotAppearanceFactory.cs) and the
/// E2E bridge bootstrap (BotE2EBridgeBootstrap.cs) swallowed failures with
/// no log, so gearless bots and a dead bridge shipped unseen.
///
/// These tests prove each site now emits a DISTINCT error-level log line
/// carrying the failing context (ability / race / gender, or the bootstrap
/// phase) plus the exception.
///
/// Capture: ONE shared MemoryTarget is installed into the global NLog config
/// for the whole class run (LogManager.Configuration is process-global —
/// per-test swaps would race parallel tests). The rule is scoped to the two
/// bot logger names only, and the previous config is restored after the
/// class. Assertions are marker-based (each test looks for its own unique
/// message substring), so shared-target cross-talk is harmless.
/// </summary>
[NotInParallel]
public class SilentCatchLoggingTests
{
    private static readonly MemoryTarget Target = new()
    {
        Layout = "${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}"
    };

    private static LoggingConfiguration _previousConfig;

    [Before(HookType.Class)]
    public static void InstallLogCapture()
    {
        _previousConfig = LogManager.Configuration;

        var config = new LoggingConfiguration();
        config.AddRuleForAllLevels(Target, "AAEmu.Game.Models.Game.Bots.CharacterManagerEquipmentSource");
        config.AddRuleForAllLevels(Target, "AAEmu.Game.Models.Game.Bots.BotE2EBridgeBootstrap");
        LogManager.Configuration = config;
        Target.Logs.Clear();
    }

    [After(HookType.Class)]
    public static void RestoreLogConfig()
    {
        LogManager.Configuration = _previousConfig ?? new LoggingConfiguration();
        Target.Logs.Clear();
    }

    // ------------------------------------------------------- appearance factory

    [Test]
    public async Task EquipmentSource_ManagerUnavailable_LogsDistinctErrorLine_WithAbilityContext()
    {
        // In the unit-test host CharacterManager.Instance cannot be
        // constructed (DI singleton, no parameterless ctor) — accessing it
        // throws, which is the exact "manager not loaded" failure the audit
        // flagged: bots used to ship gearless with zero log evidence.
        var source = new CharacterManagerEquipmentSource();

        var plan = source.GetAbilityEquipment((byte)AbilityType.Fight);

        // Graceful degradation must be preserved (empty plan, no throw).
        await Assert.That(plan.Items).IsNotNull();
        await Assert.That(plan.Ability).IsEqualTo((byte)AbilityType.Fight);

        var line = Target.Logs.SingleOrDefault(l => l.Contains("starting-equipment lookup failed"));
        await Assert.That(line).IsNotNull();
        await Assert.That(line).Contains("ERROR");
        await Assert.That(line).Contains("ability 1");
        await Assert.That(line).Contains("gearless");
    }

    [Test]
    public async Task EquipmentSource_ManagerUnavailable_BodyItems_LogsDistinctErrorLine_WithRaceGenderContext()
    {
        var source = new CharacterManagerEquipmentSource();

        var body = source.GetBodyItems(Race.Nuian, Gender.Male);

        await Assert.That(body).IsNotNull();

        var line = Target.Logs.SingleOrDefault(l => l.Contains("body-item lookup failed"));
        await Assert.That(line).IsNotNull();
        await Assert.That(line).Contains("ERROR");
        await Assert.That(line).Contains("race Nuian");
        await Assert.That(line).Contains("gender Male");
        await Assert.That(line).Contains("without body items");
    }

    // ------------------------------------------------------- bridge bootstrap

    [Test]
    public async Task BridgeBootstrap_StartThrows_LogsErrorLevelLine()
    {
        // Force the previously-silent catch: DI is ready, bridge start fails.
        await BotE2EBridgeBootstrap.RunBridgeStartupAsync(
            () => true,
            () => throw new InvalidOperationException("deliberate bridge start failure"));

        var line = Target.Logs.SingleOrDefault(l => l.Contains("bridge bootstrap failed"));
        await Assert.That(line).IsNotNull();
        await Assert.That(line).Contains("ERROR");
        await Assert.That(line).Contains("deliberate bridge start failure");
    }

    [Test]
    public async Task BridgeBootstrap_ReadinessNeverArrives_IsSilentNoop()
    {
        // isReady never true → start never invoked, no log, no throw. The
        // bootstrap must stay a strict no-op when the server never wires DI.
        await BotE2EBridgeBootstrap.RunBridgeStartupAsync(
            () => false,
            () => throw new InvalidOperationException("must not be called"),
            maxPolls: 3, pollDelay: TimeSpan.Zero);

        await Assert.That(Target.Logs.Any(l => l.Contains("must not be called"))).IsFalse();
    }
}
