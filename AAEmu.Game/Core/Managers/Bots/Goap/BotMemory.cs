#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Persistent memory record of a planted wilderness grove or illegal farm.
/// </summary>
public sealed record WildGroveRecord(
    Vector3 Position,
    uint DoodadId,
    DateTime PlantedAtUtc,
    TimeSpan MaturationDuration,
    uint SaplingTemplateId,
    bool IsHarvested = false)
{
    public bool IsMature(DateTime nowUtc) => nowUtc >= PlantedAtUtc + MaturationDuration && !IsHarvested;
}

/// <summary>
/// Rich persistent memory maintaining knowledge of the game world outside the compressed 64-bit GOAP state.
/// Holds known groves, POI locations, merchant locations, target histories, and action failures.
/// </summary>
public sealed class BotMemory
{
    private readonly List<WildGroveRecord> _groves = [];
    private readonly Dictionary<string, int> _actionFailures = [];

    public IReadOnlyList<WildGroveRecord> Groves => _groves;

    /// <summary>Consecutive failure count per action identifier for failure backoff and retry bounds.</summary>
    public IReadOnlyDictionary<string, int> ActionFailures => _actionFailures;

    public Vector3? KnownSeedMerchantPos { get; set; }
    public uint KnownSeedMerchantNpcId { get; set; }

    public Vector3? TargetWildFarmPos { get; set; }
    public string? TargetWildFarmPoiId { get; set; }
    public bool TargetWildFarmPoiInvalidated { get; set; }

    public uint LastTargetObjId { get; set; }
    public bool HasLootedCurrentTarget { get; set; }

    /// <summary>Explicit overrides for tests / simulated inventory layers.</summary>
    public bool? HasFoodOverride { get; set; }
    public bool? HasSaplingsOverride { get; set; }

    public void RecordGrove(WildGroveRecord grove)
    {
        _groves.Add(grove);
    }

    public void MarkGroveHarvested(uint doodadId)
    {
        for (int i = 0; i < _groves.Count; i++)
        {
            if (_groves[i].DoodadId == doodadId)
            {
                _groves[i] = _groves[i] with { IsHarvested = true };
            }
        }
    }

    public bool HasActiveGroves => _groves.Any(g => !g.IsHarvested);
    public bool HasMatureGroves(DateTime nowUtc) => _groves.Any(g => g.IsMature(nowUtc));

    public int GetActionFailures(string actionName) => _actionFailures.GetValueOrDefault(actionName, 0);

    public void RecordActionFailure(string actionName)
    {
        _actionFailures[actionName] = GetActionFailures(actionName) + 1;
    }

    public void ClearActionFailures(string actionName)
    {
        _actionFailures.Remove(actionName);
    }

    public void ClearAllFailures()
    {
        _actionFailures.Clear();
    }
}
