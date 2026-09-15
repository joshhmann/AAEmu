using System.Collections.Concurrent;
using System.Numerics;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Tactical companion bot party formation and slot computation.
/// Uses a 2-column chevron formation behind the leader with stable hash
/// variation to prevent robotic stacking, along with spread positioning
/// and companion mode/role state tracking.
/// </summary>
public static class BotFormation
{
    public const float DefaultBaseSpacing = 2.5f;
    public const float DefaultMaxOrganicOffset = 0.35f;

    private static readonly ConcurrentDictionary<uint, CompanionState> _companionStates = new();

    /// <summary>
    /// Computes the 2-column chevron slot position behind a leader character.
    /// Slots alternate left/right: row = slot / 2, side = (slot % 2 == 0) ? -1f : 1f.
    /// Incorporates deterministic ±0.35m stable hash variation based on leader.Id and slot.
    /// </summary>
    /// <param name="leader">Party leader character.</param>
    /// <param name="slot">0-indexed slot number (0, 1, 2, ...).</param>
    /// <param name="baseSpacing">Spacing unit between rows/columns (default: 2.5m).</param>
    /// <param name="applyOrganicOffset">When true, adds deterministic stable hash jitter.</param>
    /// <returns>Computed world target position for the bot in this slot.</returns>
    public static Vector3 CalculateSlotPosition(
        Character leader,
        int slot,
        float baseSpacing = DefaultBaseSpacing,
        bool applyOrganicOffset = true)
    {
        ArgumentNullException.ThrowIfNull(leader);
        if (slot < 0)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot index must be non-negative.");
        if (baseSpacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(baseSpacing), "Base spacing must be positive.");

        int row = slot / 2;
        float side = (slot % 2 == 0) ? -1f : 1f;

        float yawDegrees = leader.Transform.World.Rotation.Z;
        var forward = GetForwardVector(yawDegrees);
        var right = GetRightVector(yawDegrees);

        float backDistance = (row + 1) * baseSpacing;
        float lateralDistance = side * baseSpacing;
        float jitter = applyOrganicOffset ? StableSignedVariation(leader.Id, slot, DefaultMaxOrganicOffset) : 0f;

        var leaderPos = leader.Transform.World.Position;
        var slotPos = leaderPos - (forward * backDistance) + (right * (lateralDistance + jitter));
        slotPos.Z = leaderPos.Z;

        return slotPos;
    }

    /// <summary>
    /// Computes a spread grid formation position behind the leader with specified columns and spacing.
    /// </summary>
    /// <param name="leader">Party leader character.</param>
    /// <param name="slot">0-indexed slot number.</param>
    /// <param name="totalMembers">Total member count.</param>
    /// <param name="baseDistance">Base distance behind the leader.</param>
    /// <param name="columns">Number of columns in the spread.</param>
    /// <param name="spacing">Lateral and longitudinal spacing between members.</param>
    /// <returns>Computed world target position in the spread formation.</returns>
    public static Vector3 CalculateSpreadPosition(
        Character leader,
        int slot,
        int totalMembers,
        float baseDistance,
        int columns,
        float spacing)
    {
        ArgumentNullException.ThrowIfNull(leader);
        if (slot < 0)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot index must be non-negative.");
        if (totalMembers <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalMembers), "Total members must be positive.");
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Columns must be at least 1.");
        if (baseDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(baseDistance), "Base distance must be non-negative.");
        if (spacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing must be positive.");

        float yawDegrees = leader.Transform.World.Rotation.Z;
        var forward = GetForwardVector(yawDegrees);
        var right = GetRightVector(yawDegrees);

        int row = slot / columns;
        int col = slot % columns;
        float lateralOffset = (col - ((columns - 1) / 2.0f)) * spacing;
        float backDistance = baseDistance + (row * spacing);

        var leaderPos = leader.Transform.World.Position;
        var spreadPos = leaderPos - (forward * backDistance) + (right * lateralOffset);
        spreadPos.Z = leaderPos.Z;

        return spreadPos;
    }

    /// <summary>
    /// Computes unit forward vector in horizontal XY plane for a yaw angle in degrees.
    /// 0° is North (+Y), 90° is East (+X), 180° is South (-Y), 270° is West (-X).
    /// </summary>
    public static Vector3 GetForwardVector(float yawDegrees)
    {
        float rad = yawDegrees * MathF.PI / 180f;
        return new Vector3(MathF.Sin(rad), MathF.Cos(rad), 0f);
    }

    /// <summary>
    /// Computes unit right vector in horizontal XY plane for a yaw angle in degrees.
    /// Perpendicular to forward vector: 0° is East (+X), 90° is South (-Y), 180° is West (-X), 270° is North (+Y).
    /// </summary>
    public static Vector3 GetRightVector(float yawDegrees)
    {
        float rad = yawDegrees * MathF.PI / 180f;
        return new Vector3(MathF.Cos(rad), -MathF.Sin(rad), 0f);
    }

    /// <summary>
    /// Computes a deterministic stable signed variation in [-maxOffset, +maxOffset] (default: ±0.35m).
    /// Pure function of leaderId and slot — zero frame-to-frame random jitter.
    /// </summary>
    public static float StableSignedVariation(uint leaderId, int slot, float maxOffset = DefaultMaxOrganicOffset)
    {
        // MurmurHash3 32-bit avalanche mixer
        uint h = leaderId ^ 0x9E3779B9u;
        h ^= (uint)(slot + 1) * 0x85EBCA6Bu;
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;

        float normalized = ((float)(h & 0x00FFFFFF) / 0x00FFFFFF) * 2.0f - 1.0f;
        return normalized * maxOffset;
    }

    // Companion State Management
    public static void SetCompanionMode(uint botId, CompanionMode mode, Vector3? stayPosition = null)
    {
        _companionStates.AddOrUpdate(
            botId,
            _ => new CompanionState(botId, mode, CompanionRole.Dps, stayPosition),
            (_, existing) => existing with { Mode = mode, StayPosition = stayPosition ?? existing.StayPosition }
        );
    }

    public static CompanionMode GetCompanionMode(uint botId)
    {
        return _companionStates.TryGetValue(botId, out var state) ? state.Mode : CompanionMode.Follow;
    }

    public static void SetCompanionRole(uint botId, CompanionRole role)
    {
        _companionStates.AddOrUpdate(
            botId,
            _ => new CompanionState(botId, CompanionMode.Follow, role),
            (_, existing) => existing with { Role = role }
        );
    }

    public static CompanionRole GetCompanionRole(uint botId)
    {
        return _companionStates.TryGetValue(botId, out var state) ? state.Role : CompanionRole.Dps;
    }

    public static CompanionState? GetCompanionState(uint botId)
    {
        return _companionStates.TryGetValue(botId, out var state) ? state : null;
    }

    public static void ClearCompanionStates()
    {
        _companionStates.Clear();
    }
}

public enum CompanionMode
{
    Follow,
    Stay
}

public enum CompanionRole
{
    Tank,
    Healer,
    Dps
}

public sealed record CompanionState(
    uint BotId,
    CompanionMode Mode,
    CompanionRole Role,
    Vector3? StayPosition = null
);
