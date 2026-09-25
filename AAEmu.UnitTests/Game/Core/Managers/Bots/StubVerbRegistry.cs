using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests.Director;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Stage 3 verb-registry test double: the production table with named overrides
/// layered on top, so a test can force one verb Open (or absent) and observe the
/// plan's fail-closed gate without reaching into any global state. Delegates to
/// the production registry for every key it does not override.
/// </summary>
internal sealed class StubVerbRegistry : IVerifiedVerbRegistry
{
    private readonly Dictionary<string, VerbGate> _overrides = new(StringComparer.Ordinal);

    /// <summary>Forces <paramref name="verbKey"/> to <paramref name="status"/>, optionally naming the gate/evidence it would carry.</summary>
    public StubVerbRegistry Force(string verbKey, VerbGateStatus status, string gateId = "", string evidencePath = "")
    {
        _overrides[verbKey] = new VerbGate(verbKey, status, gateId, evidencePath);
        return this;
    }

    /// <summary>Removes <paramref name="verbKey"/> from the registry entirely (no gate owns it).</summary>
    public StubVerbRegistry Remove(string verbKey)
    {
        _overrides[verbKey] = VerbGate.Absent(verbKey);
        return this;
    }

    public VerbGate Resolve(string verbKey)
        => _overrides.TryGetValue(verbKey, out var gate)
            ? gate
            : VerifiedVerbRegistry.Instance.Resolve(verbKey);
}
