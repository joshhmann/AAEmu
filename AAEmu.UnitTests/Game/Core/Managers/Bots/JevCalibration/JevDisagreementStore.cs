#nullable enable

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// Shared JSON options for the calibration records: named floating-point
/// literals so NaN/Unreadable measurements survive the round-trip (the brains'
/// honesty contract encodes "could not be measured" as NaN), and string enums
/// so a stored record stays readable in the lane corpus.
/// </summary>
public static class JevCalibrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Encodes a frozen inputs/decisions payload for the disagreement record.</summary>
    public static string Encode<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    /// <summary>Decodes a frozen inputs/decisions payload from a disagreement record.</summary>
    public static T Decode<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)!;
}

/// <summary>
/// One teacher-review row: a frozen wake whose replay disagreed with the live
/// verdict (or whose sampling triggers fired), with the frozen inputs and both
/// verdicts carried as JSON so the row is self-contained in the corpus.
/// </summary>
public sealed record JevDisagreementRecord(
    string SchemaVersion,
    string Brain,
    string WakeId,
    DateTime FrozenAtUtc,
    string InputsJson,
    string ExpectedJson,
    string ActualJson,
    ImmutableArray<string> Triggers);

/// <summary>
/// Writes and reads <see cref="JevDisagreementRecord"/> rows as single JSON
/// files. Write → read → equal is the schema contract, pinned by the
/// calibration tests.
/// </summary>
public static class JevDisagreementStore
{
    /// <summary>Current row schema version. Bump when the record shape changes.</summary>
    public const string CurrentSchemaVersion = "jev-disagreement/1";

    /// <summary>Builds a row for a diverged travel replay.</summary>
    public static JevDisagreementRecord FromMismatch(
        in JevTravelWake wake, in TravelDecisionPayload actual, ImmutableArray<string> triggers, DateTime frozenAtUtc)
        => new(CurrentSchemaVersion, nameof(JevBrain.Travel), wake.WakeId, frozenAtUtc,
            JevCalibrationJson.Encode(wake.Inputs), JevCalibrationJson.Encode(wake.Expected),
            actual.Json, triggers);

    /// <summary>Builds a row for a diverged survival replay.</summary>
    public static JevDisagreementRecord FromMismatch(
        in JevSurvivalWake wake, in SurvivalDecisionPayload actual, ImmutableArray<string> triggers, DateTime frozenAtUtc)
        => new(CurrentSchemaVersion, nameof(JevBrain.Survival), wake.WakeId, frozenAtUtc,
            JevCalibrationJson.Encode(wake.Inputs), JevCalibrationJson.Encode(wake.Expected),
            actual.Json, triggers);

    /// <summary>Builds a row for a diverged needs replay.</summary>
    public static JevDisagreementRecord FromMismatch(
        in JevNeedsWake wake, in NeedsDecisionPayload actual, ImmutableArray<string> triggers, DateTime frozenAtUtc)
        => new(CurrentSchemaVersion, nameof(JevBrain.Needs), wake.WakeId, frozenAtUtc,
            JevCalibrationJson.Encode(wake.Inputs), JevCalibrationJson.Encode(wake.Expected),
            actual.Json, triggers);

    /// <summary>Builds a row for a diverged combat replay.</summary>
    public static JevDisagreementRecord FromMismatch(
        in JevCombatWake wake, in CombatDecisionPayload actual, ImmutableArray<string> triggers, DateTime frozenAtUtc)
        => new(CurrentSchemaVersion, nameof(JevBrain.Combat), wake.WakeId, frozenAtUtc,
            JevCalibrationJson.Encode(wake.Inputs), JevCalibrationJson.Encode(wake.Expected),
            actual.Json, triggers);

    /// <summary>Writes one row to <paramref name="path"/>.</summary>
    public static void Write(string path, JevDisagreementRecord record)
        => File.WriteAllText(path, JsonSerializer.Serialize(record, JevCalibrationJson.Options));

    /// <summary>Reads one row from <paramref name="path"/>.</summary>
    public static JevDisagreementRecord Read(string path)
        => JsonSerializer.Deserialize<JevDisagreementRecord>(File.ReadAllText(path), JevCalibrationJson.Options)!;
}

/// <summary>Carries a replayed travel decision as JSON for the disagreement row.</summary>
public readonly record struct TravelDecisionPayload(string Json)
{
    /// <summary>Freezes a replayed travel decision as JSON.</summary>
    public static TravelDecisionPayload From(in AAEmu.Game.Core.Managers.Bots.Travel.TravelDecision decision)
        => new(JevCalibrationJson.Encode(decision));
}

/// <summary>Carries a replayed survival decision as JSON for the disagreement row.</summary>
public readonly record struct SurvivalDecisionPayload(string Json)
{
    /// <summary>Freezes a replayed survival decision as JSON.</summary>
    public static SurvivalDecisionPayload From(in AAEmu.Game.Core.Managers.Bots.Survival.SurvivalBrainDecision decision)
        => new(JevCalibrationJson.Encode(decision));
}

/// <summary>Carries a replayed needs decision as JSON for the disagreement row.</summary>
public readonly record struct NeedsDecisionPayload(string Json)
{
    /// <summary>Freezes a replayed needs decision as JSON.</summary>
    public static NeedsDecisionPayload From(in AAEmu.Game.Core.Managers.Bots.Needs.NeedsBrainDecision decision)
        => new(JevCalibrationJson.Encode(decision));
}

/// <summary>Carries a replayed combat decision as JSON for the disagreement row.</summary>
public readonly record struct CombatDecisionPayload(string Json)
{
    /// <summary>Freezes a replayed combat decision as JSON.</summary>
    public static CombatDecisionPayload From(in AAEmu.Game.Core.Managers.Bots.Combat.CombatBrainDecision decision)
        => new(JevCalibrationJson.Encode(decision));
}
