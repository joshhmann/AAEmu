namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// B4 playerbot metadata (M6 deferred gate #5): the per-bot durable state that
/// lives OUTSIDE the ordinary character row — personality, profession, home
/// position, schedule, behavior config and planner state. Persisted to
/// aaemu_game.playerbot_metadata by PlayerBotMetadataStore (write-through on
/// mutation + the periodic SaveManager tick, so the rows survive the E2E's
/// hard-kill restarts).
///
/// All fields carry empty defaults: a bot without a metadata row (never
/// recorded, or the table unreachable) reads as <see cref="Empty"/> and the
/// caller falls back to its own defaults (e.g. the presence demo's template
/// spawn home).
/// </summary>
public sealed record PlayerBotMetadata
{
    /// <summary>The characters.id this metadata belongs to.</summary>
    public uint CharacterId { get; init; }

    /// <summary>Free-form personality description (empty = unset).</summary>
    public string Personality { get; set; } = string.Empty;

    /// <summary>Bot profession (empty = unset).</summary>
    public string Profession { get; set; } = string.Empty;

    /// <summary>True when a home position has been recorded.</summary>
    public bool HasHome { get; set; }

    /// <summary>World the home position is in (0 = unset).</summary>
    public uint HomeWorldId { get; set; }

    /// <summary>Zone the home position is in (0 = unset).</summary>
    public uint HomeZoneId { get; set; }

    /// <summary>Home X.</summary>
    public float HomeX { get; set; }

    /// <summary>Home Y.</summary>
    public float HomeY { get; set; }

    /// <summary>Home Z.</summary>
    public float HomeZ { get; set; }

    /// <summary>Serialized bot schedule (JSON; empty = unset).</summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>Serialized behavior configuration (JSON; empty = unset).</summary>
    public string BehaviorConfig { get; set; } = string.Empty;

    /// <summary>Serialized planner state (JSON; empty = unset).</summary>
    public string PlannerState { get; set; } = string.Empty;

    /// <summary>The absent-row default: identity only, everything unset.</summary>
    public static PlayerBotMetadata Empty(uint characterId)
        => new() { CharacterId = characterId };
}
