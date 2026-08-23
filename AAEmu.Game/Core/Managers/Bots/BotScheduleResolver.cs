namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// C1 Schedules v1 — pure game-clock → phase resolution (no state, no I/O;
/// hermetically testable).
///
/// Phase machine semantics (all hours are GAME-CLOCK hours, TimeManager scale):
///
///   1. BASE phase from the anchors:
///        Rest  when hour ∈ [RestStart, RestEnd)   (wrap-aware, e.g. 22→06)
///        Work  when hour ∈ [WorkStart, WorkEnd)
///        Home  otherwise (social / between-times)
///
///   2. HYSTERESIS (anti-flapping): a phase change away from the previous
///      phase only happens once the game clock has advanced at least
///      <paramref name="hysteresisHours"/> past the ENTRY edge of the new
///      phase (Work → WorkStart, Rest → RestStart, Home → the most recently
///      passed of WorkEnd/RestEnd). Inside the hysteresis window the
///      previous phase is held, so a clock oscillating around a boundary
///      never flaps.
///
///   3. TRAVEL overlay (applied to a stable HOME result):
///        morning leg — the last <paramref name="travelDurationHours"/>
///        before WorkStart resolves as Travel (walking TO work);
///        evening leg — after WorkEnd until min(HomeBy, RestStart) resolves
///        as Travel (walking home; the bot arrives by its HomeBy anchor).
///
/// The existing roam-loop behavior is untouched and remains the WORK-phase
/// behavior (and the fallback when schedules are disabled entirely).
/// </summary>
public static class BotScheduleResolver
{
    /// <summary>Default anti-flap window: 1/6 game-hour = 10 in-game minutes.</summary>
    public const float DefaultHysteresisHours = 1f / 6f;

    /// <summary>Default travel leg length: half a game-hour.</summary>
    public const float DefaultTravelDurationHours = 0.5f;

    public static BotSchedulePhase Resolve(
        BotDailyAnchors anchors,
        float hour,
        BotSchedulePhase? previous,
        float hysteresisHours = DefaultHysteresisHours,
        float travelDurationHours = DefaultTravelDurationHours)
    {
        var now = Normalize(hour);
        var basePhase = BasePhase(anchors, now);

        var stable = basePhase;
        if (previous is { } prev && basePhase != prev)
        {
            var entryEdge = EntryEdge(anchors, now, basePhase);
            if (HoursSince(now, entryEdge) < hysteresisHours)
                stable = prev; // inside the anti-flap window — hold the current phase
        }

        return ApplyTravelOverlay(anchors, now, stable, travelDurationHours);
    }

    /// <summary>The raw (pre-hysteresis) phase for a game hour.</summary>
    public static BotSchedulePhase BasePhase(BotDailyAnchors anchors, float hour)
    {
        var now = Normalize(hour);
        if (InWrapRange(now, anchors.RestStart, anchors.RestEnd))
            return BotSchedulePhase.Rest;
        if (InWrapRange(now, anchors.WorkStart, anchors.WorkEnd))
            return BotSchedulePhase.Work;
        return BotSchedulePhase.Home;
    }

    /// <summary>
    /// True when the bot's evening Travel leg heads toward the WORK anchor
    /// (morning); false when it heads home (evening). Used to pick the
    /// Travel movement destination.
    /// </summary>
    public static bool IsMorningTravel(BotDailyAnchors anchors, float hour, float travelDurationHours)
    {
        var now = Normalize(hour);
        var timeToWork = HoursUntil(now, anchors.WorkStart);
        return timeToWork > 0f && timeToWork <= travelDurationHours;
    }

    private static BotSchedulePhase ApplyTravelOverlay(
        BotDailyAnchors anchors, float hour, BotSchedulePhase phase, float travelDurationHours)
    {
        if (phase != BotSchedulePhase.Home)
            return phase;

        // Morning leg: approaching WorkStart.
        var timeToWork = HoursUntil(hour, anchors.WorkStart);
        if (timeToWork > 0f && timeToWork <= travelDurationHours)
            return BotSchedulePhase.Travel;

        // Evening leg: leaving WorkEnd, walking home until the HomeBy cutoff
        // (or RestStart when that comes first).
        var legLength = SmallestAheadDistance(anchors.WorkEnd, anchors.HomeBy, anchors.RestStart);
        if (legLength is { } length && HoursSince(hour, anchors.WorkEnd) < length)
            return BotSchedulePhase.Travel;

        return BotSchedulePhase.Home;
    }

    /// <summary>
    /// Wrap-aware distance (∈ (0, 24)) from <paramref name="reference"/> to
    /// the nearest strictly-ahead candidate; null when none lies ahead.
    /// </summary>
    private static float? SmallestAheadDistance(float reference, params float[] candidates)
    {
        float? best = null;
        foreach (var candidate in candidates)
        {
            var delta = HoursSince(candidate, reference);
            if (delta <= 0f)
                continue;
            if (best is not { } current || delta < current)
                best = delta;
        }

        return best;
    }

    /// <summary>The boundary whose crossing produces <paramref name="phase"/>.</summary>
    private static float EntryEdge(BotDailyAnchors anchors, float hour, BotSchedulePhase phase) =>
        phase switch
        {
            BotSchedulePhase.Work => anchors.WorkStart,
            BotSchedulePhase.Rest => anchors.RestStart,
            // Home is entered through whichever exit edge was passed most recently.
            _ => HoursSince(hour, anchors.RestEnd) <= HoursSince(hour, anchors.WorkEnd)
                ? anchors.RestEnd
                : anchors.WorkEnd
        };

    private static bool InWrapRange(float hour, float start, float end) =>
        AreClose(start, end)
            ? false // degenerate window = disabled
            : start < end
                ? hour >= start && hour < end
                : hour >= start || hour < end;

    /// <summary>Wrap-aware hours elapsed since <paramref name="sinceHour"/> (∈ [0, 24)).</summary>
    private static float HoursSince(float hour, float sinceHour) => Normalize(hour - sinceHour);

    /// <summary>Wrap-aware hours until <paramref name="untilHour"/> (∈ [0, 24)).</summary>
    private static float HoursUntil(float hour, float untilHour) => Normalize(untilHour - hour);

    private static float Normalize(float hour)
    {
        var normalized = hour % 24f;
        if (normalized < 0f)
            normalized += 24f;
        return normalized;
    }

    private static bool AreClose(float a, float b) => MathF.Abs(a - b) < 0.0001f;
}
