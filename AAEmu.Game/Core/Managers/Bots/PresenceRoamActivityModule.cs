using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// G3-B3 second module: the EXISTING presence-demo roam behavior as an
/// arbitration module — today's baseline for schedule-less deployments
/// ("re-implement roam as a module", roadmap G3-B3).
///
/// CanActivate only while schedules are OFF (the schedule module owns bot
/// behavior when they are on) and the bot is not in battle. Activation is
/// zero-regression by construction: a bot that already walks a live route
/// keeps it untouched; only a ROUTE-LESS bot gets the standard deterministic
/// patrol loop re-armed through <see cref="IBotScheduleBehavior.ResumeRoam"/>
/// (the same descriptor-driven route the presence coordinator armed at
/// provision time).
/// </summary>
public sealed class PresenceRoamActivityModule : IBotActivityModule
{
    private readonly BotScheduleOptions _options;
    private readonly IBotScheduleBehavior _behavior;
    private readonly BotRoamStepExecutor _stepExecutor;
    private readonly Func<uint, PlayerBotMetadata> _metadataProvider;

    public string Name => "PresenceRoam";
    public int Priority { get; } = 50;

    public PresenceRoamActivityModule(
        BotScheduleOptions options,
        IBotScheduleBehavior behavior,
        BotRoamStepExecutor stepExecutor,
        Func<uint, PlayerBotMetadata>? metadataProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _metadataProvider = metadataProvider ?? DefaultMetadataProvider;
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.Enabled)
            return BotActivityDecision.Deny("schedules enabled — the schedule layer owns bot behavior");

        if (context.Bot.Character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        return BotActivityDecision.Allow("presence.roam");
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bot = context.Bot;

        // A live route IS the current activity — keep it (never reset walk
        // progress mid-patrol). Only a route-less bot gets the standard loop.
        if (_stepExecutor.GetRoamRoute(bot.CharacterId) is { IsFinished: false })
            return new BotActivity("presence.roam", Name);

        var metadata = _metadataProvider(bot.CharacterId);
        var fallbackCenter = metadata.HasHome
            ? new System.Numerics.Vector3(metadata.HomeX, metadata.HomeY, metadata.HomeZ)
            : bot.Character.Transform.World.Position;

        _behavior.ResumeRoam(bot, metadata.Schedule, fallbackCenter);
        return new BotActivity("presence.roam", Name);
    }

    private static PlayerBotMetadata DefaultMetadataProvider(uint characterId) =>
        PlayerBotMetadataStore.Instance.GetForRead(characterId);
}
