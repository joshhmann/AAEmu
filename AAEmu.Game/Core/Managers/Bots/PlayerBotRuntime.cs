using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Lifecycle state of a registered player bot runtime (slice #5, registry).
/// </summary>
public enum PlayerBotState : byte
{
    /// <summary>Known to the manager, not embodied in any world.</summary>
    Registered = 0,

    /// <summary>Embodied in the world through the lifecycle service.</summary>
    Active = 1,

    /// <summary>Embodiment torn down; record retained for diagnostics/audit.</summary>
    Deactivated = 2
}

/// <summary>
/// Registry entry owned by <see cref="PlayerBotManager"/>: the bot's ordinary
/// Character record plus the manager's runtime ownership bookkeeping.
///
/// Composition rule (AGENTS.md #9): bots are ordinary Character records —
/// the registry entry carries the Character, it does not duplicate it.
/// </summary>
public sealed class PlayerBotRuntime
{
    public uint CharacterId { get; }

    public Character Character { get; }

    /// <summary>Current lifecycle state. Mutated by the manager under <see cref="Sync"/>.</summary>
    public PlayerBotState State { get; internal set; } = PlayerBotState.Registered;

    /// <summary>
    /// Runtime ownership: the subsystem that requested the current lifecycle
    /// transition (e.g. "population-director", "scheduler", "admin").
    /// Empty when no owner holds the bot (Registered/Deactivated).
    /// </summary>
    public string Owner { get; internal set; } = string.Empty;

    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

    public DateTime? ActivatedAtUtc { get; internal set; }

    public DateTime? DeactivatedAtUtc { get; internal set; }

    public string? LastDeactivateReason { get; internal set; }

    /// <summary>Per-entry lock serialising state transitions for THIS bot.</summary>
    internal object Sync { get; } = new();

    public PlayerBotRuntime(Character character, string owner)
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
        CharacterId = character.Id;
        Owner = owner;
    }

    public override string ToString() => $"bot:{CharacterId} ({Character.Name}) [{State}]";
}
