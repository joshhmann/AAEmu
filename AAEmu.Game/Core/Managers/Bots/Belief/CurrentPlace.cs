#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Belief;

/// <summary>
/// Where the observer believes it is, resolved by the caller (layer 3's frame
/// memory plus whatever world knowledge the caller owns) and handed to
/// <see cref="BeliefInterpreter"/> as a pure input.
///
/// This is a NAMED PLACE, not a position: the interpreter never re-resolves a
/// region, sub-zone or zone from a coordinate, so a bot's place belief is
/// exactly what its driver established — never a second, drifting derivation.
///
/// <see cref="SemanticPlace"/> is the caller's semantic name for the current
/// area (for example the farm place key <see cref="BeliefInterpreter.FarmPlaceKey"/>).
/// It stays null when the caller has no vocabulary for this spot; a null place
/// makes every place-derived belief FACT unknown rather than false, so a
/// place-less driver can never assert "not at the farm".
/// </summary>
public readonly struct CurrentPlace
{
    /// <summary>Region (sector) id the observer stands in; 0 when unresolved.</summary>
    public int RegionId { get; init; }

    /// <summary>Sub-zone id the observer stands in; 0 when unresolved.</summary>
    public int SubZoneId { get; init; }

    /// <summary>Semantic place key, or null when the caller has no vocabulary for this spot.</summary>
    public string? SemanticPlace { get; init; }

    public override string ToString()
        => $"region={RegionId} subzone={SubZoneId} place={SemanticPlace ?? "unknown"}";
}
