using System.Text.Json;

using AAEmu.Game.Models.Game.Bots;

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// C1 Schedules v1 — ADDITIVE JSON payload extension for the B4
/// playerbot_metadata.schedule TEXT column. The column's base document is
/// the deterministic roam-loop descriptor written by
/// <see cref="BotPresenceCoordinator.BuildRoamScheduleJson"/> (the B4 E2E
/// asserts byte-equal pre/post-restart snapshots of it). This helper NEVER
/// rewrites or reorders those keys: it only
///   - READS the extension keys <c>anchors</c> (daily schedule anchors) and
///     <c>lastPhase</c> (persisted phase), and
///   - MERGES those two keys into a copy of the document (all other keys
///     preserved verbatim, appended at the end in insertion order → the
///     output is deterministic for identical input).
///
/// NO SQL MIGRATION: the schedule column is already a JSON blob, so the
/// anchors + last phase ride inside it. Old rows without the keys load as
/// template anchors / unknown phase (B4-shape compatibility).
/// </summary>
public static class BotSchedulePayload
{
    /// <summary>Reads stored daily anchors from a schedule JSON. False when absent/malformed/invalid.</summary>
    public static bool TryReadAnchors(string? scheduleJson, out BotDailyAnchors anchors)
    {
        anchors = BotDailyAnchors.Template;
        if (!TryParseDocument(scheduleJson, out var document))
            return false;

        using (document)
        {
            return document.RootElement.TryGetProperty("anchors", out var element) &&
                   BotDailyAnchors.TryFromJsonElement(element, out anchors);
        }
    }

    /// <summary>Reads the persisted last phase from a schedule JSON.</summary>
    public static bool TryReadLastPhase(string? scheduleJson, out BotSchedulePhase phase)
    {
        phase = BotSchedulePhase.Home;
        if (!TryParseDocument(scheduleJson, out var document))
            return false;

        using (document)
        {
            if (!document.RootElement.TryGetProperty("lastPhase", out var element) ||
                element.ValueKind != JsonValueKind.String ||
                !Enum.TryParse(element.GetString(), ignoreCase: false, out phase))
                return false;
            return true;
        }
    }

    /// <summary>
    /// Reads the roam-loop descriptor the Work phase replays: center
    /// (<c>home</c> array), radius and per-bot seed (<c>phase</c>). Falls
    /// back to <paramref name="fallbackCenter"/> / 30m / id-derived seed.
    /// </summary>
    public static bool TryReadRoamDescriptor(string? scheduleJson,
        out Vector3 center, out float radius, out int seed)
    {
        center = Vector3.Zero;
        radius = 30f;
        seed = 0;

        if (!TryParseDocument(scheduleJson, out var document))
            return false;

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("home", out var home) &&
                home.ValueKind == JsonValueKind.Array &&
                home.GetArrayLength() == 3)
            {
                var coords = new float[3];
                var i = 0;
                foreach (var item in home.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out coords[i]))
                        return false;
                    i++;
                }

                center = new Vector3(coords[0], coords[1], coords[2]);
            }
            else
            {
                return false;
            }

            if (root.TryGetProperty("radius", out var r) &&
                r.ValueKind == JsonValueKind.Number && r.TryGetSingle(out var parsedRadius) &&
                parsedRadius > 0f)
                radius = parsedRadius;

            if (root.TryGetProperty("phase", out var p) &&
                p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var parsedSeed))
                seed = parsedSeed;

            return true;
        }
    }

    /// <summary>
    /// Returns <paramref name="scheduleJson"/> with the runtime state merged
    /// in additively (anchors always; lastPhase when known). Every other key
    /// of the input document is preserved verbatim.
    /// </summary>
    public static string WithRuntimeState(string? scheduleJson, BotDailyAnchors anchors, BotSchedulePhase? lastPhase)
    {
        Dictionary<string, JsonElement> document;
        try
        {
            document = string.IsNullOrWhiteSpace(scheduleJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(scheduleJson) ?? [];
        }
        catch (JsonException)
        {
            document = [];
        }

        document["anchors"] = JsonSerializer.SerializeToElement(new
        {
            homeBy = anchors.HomeBy,
            workStart = anchors.WorkStart,
            workEnd = anchors.WorkEnd,
            restStart = anchors.RestStart,
            restEnd = anchors.RestEnd
        });
        if (lastPhase is { } phase)
            document["lastPhase"] = JsonSerializer.SerializeToElement(phase.ToString());

        return JsonSerializer.Serialize(document);
    }

    /// <summary>
    /// Copies the schedule extensions (anchors/lastPhase) from an OLD
    /// schedule JSON onto a NEWLY built one. Byte-equality guarantee: when
    /// the old document carries NO extensions, <paramref name="newJson"/> is
    /// returned VERBATIM (the B4 restart snapshot stays byte-equal).
    /// </summary>
    public static string PreserveExtensions(string? oldScheduleJson, string newJson)
    {
        var hasAnchors = TryReadAnchors(oldScheduleJson, out var anchors);
        var hasPhase = TryReadLastPhase(oldScheduleJson, out var phase);
        if (!hasAnchors && !hasPhase)
            return newJson;

        return WithRuntimeState(newJson, hasAnchors ? anchors : BotDailyAnchors.Template,
            hasPhase ? phase : null);
    }

    private static bool TryParseDocument(string? scheduleJson, out JsonDocument document)
    {
        document = JsonDocument.Parse("{}"); // caller-owned placeholder (disposed by the caller)
        if (string.IsNullOrWhiteSpace(scheduleJson))
            return false;

        try
        {
            var parsed = JsonDocument.Parse(scheduleJson);
            document.Dispose();
            document = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
