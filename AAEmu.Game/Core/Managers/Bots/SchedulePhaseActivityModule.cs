using System.Numerics;

using AAEmu.Game.Models.Game.Bots;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Shared GAME-clock hour source for the arbitration modules (TimeManager
/// scale, normalized to 0..24 — the same math BotScheduleService uses).
/// </summary>
internal static class BotActivityGameClock
{
    internal static float Hour()
    {
        var time = TimeManager.Instance.GetTime;
        var normalized = time % 24f;
        return normalized < 0f ? normalized + 24f : normalized;
    }
}

/// <summary>
/// G3-B3 first module: the EXISTING C1 schedule-phase behavior expressed as
/// an <see cref="IBotActivityModule"/>. Highest arbitration priority so a
/// resolved phase always outranks baseline presence roam and idle.
///
/// The activity NAME encodes the phase ("schedule.work", "schedule.rest",
/// "schedule.home", "schedule.travel") — the arbiter's change detection on
/// that name gives per-phase transition semantics, and the previous phase
/// feeds <see cref="BotScheduleResolver.Resolve"/>'s hysteresis.
///
/// Visible behavior is applied through the SAME
/// <see cref="IBotScheduleBehavior"/> seam BotScheduleService uses
/// (Work → ResumeRoam; Rest/Home/Travel → MoveToAnchor) — no parallel
/// movement path (AGENTS.md #9/#10). Anchors + last phase persist through
/// the B4 write-through path exactly like the service does.
///
/// Disabled while "Bots"."EnableSchedules" is off (the default): CanActivate
/// declines and arbitration falls through to presence roam / idle, which is
/// today's behavior byte-for-byte.
/// </summary>
public sealed class SchedulePhaseActivityModule : IBotActivityModule
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public const string ActivityPrefix = "schedule.";

    private readonly BotScheduleOptions _options;
    private readonly IBotScheduleBehavior _behavior;
    private readonly Func<float> _gameHourProvider;
    private readonly Func<uint, PlayerBotMetadata> _metadataProvider;
    private readonly Action<uint, string> _scheduleWriter;
    private readonly BotScheduleService? _authoritativeScheduleService;

    public string Name => "Schedules";
    public int Priority { get; } = 100;

    public SchedulePhaseActivityModule(
        BotScheduleOptions options,
        IBotScheduleBehavior behavior,
        Func<float>? gameHourProvider = null,
        Func<uint, PlayerBotMetadata>? metadataProvider = null,
        Action<uint, string>? scheduleWriter = null,
        BotScheduleService? authoritativeScheduleService = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        _gameHourProvider = gameHourProvider ?? BotActivityGameClock.Hour;
        _metadataProvider = metadataProvider ?? DefaultMetadataProvider;
        _scheduleWriter = scheduleWriter ?? DefaultScheduleWriter;
        _authoritativeScheduleService = authoritativeScheduleService;
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
            return BotActivityDecision.Deny("schedules disabled (Bots.EnableSchedules off)");

        // BotScheduleService is the authoritative production owner of
        // phase resolution/persistence when schedules are enabled. The
        // arbiter module remains available to isolated rigs, but must not
        // apply the same phase on a second path in production.
        if (_authoritativeScheduleService?.Options.Enabled == true)
            return BotActivityDecision.Deny("BotScheduleService owns schedule phases");

        if (context.Bot.Character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        return BotActivityDecision.Allow(ActivityPrefix + ResolvePhase(context).ToString().ToLowerInvariant());
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bot = context.Bot;
        var phase = ResolvePhase(context);
        var metadata = _metadataProvider(bot.CharacterId);
        var scheduleJson = metadata.Schedule;
        var anchors = ResolveAnchors(scheduleJson);
        var home = ResolveHome(bot.Character, metadata);

        switch (phase)
        {
            case BotSchedulePhase.Work:
                // Normal roam/presence behavior around the work anchor.
                _behavior.ResumeRoam(bot, scheduleJson, home);
                break;

            case BotSchedulePhase.Rest:
            case BotSchedulePhase.Home:
                // Walk HOME and idle there (Rest) / social idle near it (Home).
                _behavior.MoveToAnchor(bot, home);
                break;

            case BotSchedulePhase.Travel:
                // Walk THIS leg toward its destination: morning legs head to
                // work, evening legs head home.
                var toWork = BotScheduleResolver.IsMorningTravel(
                    anchors, context.GameHour, _options.TravelDurationHours);
                _behavior.MoveToAnchor(bot, toWork ? ResolveWorkCenter(scheduleJson, home) : home);
                break;
        }

        // Persist anchors + last phase through the B4 write-through path
        // (restart continuity — same contract as BotScheduleService).
        _scheduleWriter(bot.CharacterId,
            BotSchedulePayload.WithRuntimeState(scheduleJson, anchors, phase));

        Logger.Debug("SchedulePhaseActivityModule: bot {CharacterId} activated phase {Phase}",
            bot.CharacterId, phase);
        return new BotActivity(ActivityPrefix + phase.ToString().ToLowerInvariant(), Name);
    }

    /// <summary>
    /// Resolves this bot's current phase. The previous phase comes from the
    /// arbiter's active-activity name ("schedule.&lt;phase&gt;"), falling back
    /// to the persisted lastPhase — restart continuity without extra state.
    /// </summary>
    private BotSchedulePhase ResolvePhase(BotActivityContext context)
    {
        var scheduleJson = _metadataProvider(context.Bot.CharacterId).Schedule;
        var anchors = ResolveAnchors(scheduleJson);

        BotSchedulePhase? previous =
            TryParsePhase(context.ActiveActivity, out var tracked) ? tracked :
            BotSchedulePayload.TryReadLastPhase(scheduleJson, out var persisted) ? persisted :
            null;

        return BotScheduleResolver.Resolve(
            anchors, context.GameHour, previous, _options.HysteresisHours, _options.TravelDurationHours);
    }

    private static bool TryParsePhase(string? activeActivity, out BotSchedulePhase phase)
    {
        if (activeActivity != null &&
            activeActivity.StartsWith(ActivityPrefix, StringComparison.Ordinal) &&
            Enum.TryParse(activeActivity[ActivityPrefix.Length..], ignoreCase: true, out phase))
            return true;

        phase = default;
        return false;
    }

    private static BotDailyAnchors ResolveAnchors(string scheduleJson) =>
        BotSchedulePayload.TryReadAnchors(scheduleJson, out var stored) && stored.IsValid
            ? stored
            : BotDailyAnchors.Template;

    private static Vector3 ResolveHome(Models.Game.Char.Character bot, PlayerBotMetadata metadata) =>
        metadata.HasHome
            ? new Vector3(metadata.HomeX, metadata.HomeY, metadata.HomeZ)
            : bot.Transform.World.Position;

    private static Vector3 ResolveWorkCenter(string scheduleJson, Vector3 fallback) =>
        BotSchedulePayload.TryReadRoamDescriptor(scheduleJson, out var center, out _, out _)
            ? center
            : fallback;

    private static PlayerBotMetadata DefaultMetadataProvider(uint characterId) =>
        PlayerBotMetadataStore.Instance.GetForRead(characterId);

    private static void DefaultScheduleWriter(uint characterId, string scheduleJson) =>
        PlayerBotMetadataStore.Instance.RecordSchedule(characterId, scheduleJson);
}
