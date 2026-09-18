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

    public Vector3? KnownHousingZonePos { get; set; }
    public Vector3? KnownWorkbenchPos { get; set; }
    public uint? OwnedHouseId { get; set; }
    public Vector3? OwnedHousePos { get; set; }

    // ---------------------------------------------------------------------
    // Simulated-inventory override layer (fixture gate)
    //
    // These properties describe a *pretend* bag/plot state, never engine state.
    // Production never writes them and must never read them: a plan/action that
    // accepted an override would report a success the world never observed.
    // They therefore read as null until a caller explicitly opts in via
    // EnableFixtureOverrides() — test rigs, scenario harnesses and the
    // HomesteadTenBot/SyntheticJourney fixtures. Every setter still stores the
    // value (so rigs can seed before enabling), but consumers only see it once
    // the gate is open.
    // ---------------------------------------------------------------------

    private bool _fixtureOverridesEnabled;
    private bool? _hasFoodOverride;
    private bool? _hasSaplingsOverride;
    private bool? _hasScarecrowDesignOverride;
    private bool? _hasTaxCertificatesOverride;
    private bool? _hasLandPlotOverride;
    private bool? _hasTimberOverride;
    private bool? _hasBuildingMaterialsOverride;
    private bool? _homeConstructedOverride;

    /// <summary>True once the fixture override layer has been explicitly enabled.</summary>
    public bool FixtureOverridesEnabled => _fixtureOverridesEnabled;

    /// <summary>
    /// Opens the simulated-inventory override layer. Call only from test rigs,
    /// scenario fixtures and harnesses — never from production provisioning,
    /// the step executor's context factory, or a GM command path.
    /// </summary>
    public void EnableFixtureOverrides() => _fixtureOverridesEnabled = true;

    /// <summary>Simulated "bag holds edible food" — null unless fixtures are enabled.</summary>
    public bool? HasFoodOverride
    {
        get => _fixtureOverridesEnabled ? _hasFoodOverride : null;
        set => _hasFoodOverride = value;
    }

    /// <summary>Simulated "bag holds saplings" — null unless fixtures are enabled.</summary>
    public bool? HasSaplingsOverride
    {
        get => _fixtureOverridesEnabled ? _hasSaplingsOverride : null;
        set => _hasSaplingsOverride = value;
    }

    /// <summary>Simulated "bag holds the scarecrow design" — null unless fixtures are enabled.</summary>
    public bool? HasScarecrowDesignOverride
    {
        get => _fixtureOverridesEnabled ? _hasScarecrowDesignOverride : null;
        set => _hasScarecrowDesignOverride = value;
    }

    /// <summary>Simulated "bag holds tax certificates" — null unless fixtures are enabled.</summary>
    public bool? HasTaxCertificatesOverride
    {
        get => _fixtureOverridesEnabled ? _hasTaxCertificatesOverride : null;
        set => _hasTaxCertificatesOverride = value;
    }

    /// <summary>Simulated "owns a plot" — null unless fixtures are enabled.</summary>
    public bool? HasLandPlotOverride
    {
        get => _fixtureOverridesEnabled ? _hasLandPlotOverride : null;
        set => _hasLandPlotOverride = value;
    }

    /// <summary>Simulated "bag holds timber" — null unless fixtures are enabled.</summary>
    public bool? HasTimberOverride
    {
        get => _fixtureOverridesEnabled ? _hasTimberOverride : null;
        set => _hasTimberOverride = value;
    }

    /// <summary>Simulated "holds building materials" — null unless fixtures are enabled.</summary>
    public bool? HasBuildingMaterialsOverride
    {
        get => _fixtureOverridesEnabled ? _hasBuildingMaterialsOverride : null;
        set => _hasBuildingMaterialsOverride = value;
    }

    /// <summary>Simulated "home construction finished" — null unless fixtures are enabled.</summary>
    public bool? HomeConstructedOverride
    {
        get => _fixtureOverridesEnabled ? _homeConstructedOverride : null;
        set => _homeConstructedOverride = value;
    }

    public void RecordGrove(WildGroveRecord grove)
    {
        _groves.Add(grove);
    }

    public void AddGrove(uint doodadId, Vector3 position, DateTime plantedAtUtc, TimeSpan? maturationDuration = null, uint saplingTemplateId = 0)
    {
        _groves.Add(new WildGroveRecord(position, doodadId, plantedAtUtc, maturationDuration ?? TimeSpan.Zero, saplingTemplateId));
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
