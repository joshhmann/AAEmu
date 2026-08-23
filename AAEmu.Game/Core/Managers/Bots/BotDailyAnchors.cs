using System.Text.Json;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// C1 Schedules v1 (M8/G4): the daily lifecycle phase a persistent bot is
/// resolved into by <see cref="BotScheduleResolver"/> from the server GAME
/// clock (TimeManager hours) and the bot's <see cref="BotDailyAnchors"/>.
/// </summary>
public enum BotSchedulePhase : byte
{
    /// <summary>At/near home, social idle (the between-times phase).</summary>
    Home = 0,

    /// <summary>Normal presence/roam behavior around the bot's work anchor.</summary>
    Work = 1,

    /// <summary>Walking between home and the work anchor (bounded legs).</summary>
    Travel = 2,

    /// <summary>Walked home and idling there (night).</summary>
    Rest = 3
}

/// <summary>
/// C1 Schedules v1: per-bot daily anchors expressed in GAME-CLOCK hours
/// (0..24, TimeManager.GetTime scale):
///   - <see cref="WorkStart"/>/<see cref="WorkEnd"/> — the work window;
///   - <see cref="RestStart"/>/<see cref="RestEnd"/> — the rest window
///     (may wrap midnight, e.g. 22 → 06);
///   - <see cref="HomeBy"/> — the latest hour the bot is back HOME after
///     work (the evening Travel phase ends here).
/// Anything outside Work and Rest resolves as Home/social.
///
/// Persisted ADDITIVELY inside the existing playerbot_metadata.schedule
/// TEXT column as the JSON keys <c>anchors</c> (+ <c>lastPhase</c>) — see
/// <see cref="BotSchedulePayload"/>. Bots without stored anchors run the
/// <see cref="Template"/> (work 08-18, rest 22-06, home by 20).
/// </summary>
public sealed record BotDailyAnchors
{
    public const float DefaultHomeBy = 20f;
    public const float DefaultWorkStart = 8f;
    public const float DefaultWorkEnd = 18f;
    public const float DefaultRestStart = 22f;
    public const float DefaultRestEnd = 6f;

    /// <summary>Latest game hour the bot is home after work (evening Travel cutoff).</summary>
    public float HomeBy { get; init; } = DefaultHomeBy;

    /// <summary>Game hour the work window opens.</summary>
    public float WorkStart { get; init; } = DefaultWorkStart;

    /// <summary>Game hour the work window closes.</summary>
    public float WorkEnd { get; init; } = DefaultWorkEnd;

    /// <summary>Game hour the rest window opens (may be &gt; WorkEnd → wraps midnight).</summary>
    public float RestStart { get; init; } = DefaultRestStart;

    /// <summary>Game hour the rest window closes (may be &lt; RestStart → wraps midnight).</summary>
    public float RestEnd { get; init; } = DefaultRestEnd;

    /// <summary>The shipped default schedule (work 08-18, rest 22-06, home by 20).</summary>
    public static BotDailyAnchors Template { get; } = new();

    /// <summary>
    /// Structural validity: every anchor inside [0, 24) and no degenerate
    /// windows (equal edges disable that window; the resolver treats them as
    /// such anyway). Both windows may wrap midnight (e.g. rest 22→06, or a
    /// night shift 22→06); invalid stored anchors fall back to
    /// <see cref="Template"/>.
    /// </summary>
    public bool IsValid =>
        InDay(HomeBy) && InDay(WorkStart) && InDay(WorkEnd) && InDay(RestStart) && InDay(RestEnd) &&
        !AreClose(WorkStart, WorkEnd) &&
        !AreClose(RestStart, RestEnd);

    private static bool InDay(float hour) => hour is >= 0f and < 24f;

    private static bool AreClose(float a, float b) => MathF.Abs(a - b) < 0.0001f;

    /// <summary>Parses the <c>anchors</c> object of a stored schedule JSON. False when absent/malformed.</summary>
    public static bool TryFromJsonElement(JsonElement element, out BotDailyAnchors anchors)
    {
        anchors = Template;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        float homeBy = DefaultHomeBy, workStart = DefaultWorkStart, workEnd = DefaultWorkEnd,
            restStart = DefaultRestStart, restEnd = DefaultRestEnd;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetSingle(out var value))
                continue;
            switch (property.Name)
            {
                case "homeBy": homeBy = value; break;
                case "workStart": workStart = value; break;
                case "workEnd": workEnd = value; break;
                case "restStart": restStart = value; break;
                case "restEnd": restEnd = value; break;
            }
        }

        var candidate = new BotDailyAnchors
        {
            HomeBy = homeBy, WorkStart = workStart, WorkEnd = workEnd,
            RestStart = restStart, RestEnd = restEnd
        };
        if (!candidate.IsValid)
            return false;

        anchors = candidate;
        return true;
    }
}
