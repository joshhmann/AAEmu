#nullable enable

using System.Collections.Concurrent;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Caches abstract symbolic action sequences for (StateFlags, Labor, Gold, GoalName) tuples.
/// Preserves plan reuse while avoiding rigid instance binding (merchants, targets, POIs
/// are bound per-bot dynamically from BotContext at runtime).
///
/// Identity invariant: a template is only reusable when the resources that gate action
/// preconditions match too. Keying on flags alone would hand a labor/gold-rich bot the
/// plan found for the same flags with different resources (or vice versa) — the same
/// class of identity error the search itself avoids by bucketing dominance on Labor/Gold.
/// </summary>
public sealed class PlanTemplateCache
{
    private readonly ConcurrentDictionary<(ulong StateFlags, ushort Labor, uint Gold, string GoalName), IReadOnlyList<string>> _templates = [];

    public int TemplateCount => _templates.Count;

    public bool TryGetTemplate(ulong stateFlags, ushort labor, uint gold, string goalName, out IReadOnlyList<string> actionNames)
    {
        return _templates.TryGetValue((stateFlags, labor, gold, goalName), out actionNames!);
    }

    public void StoreTemplate(ulong stateFlags, ushort labor, uint gold, string goalName, IReadOnlyList<IGoapAction> planActions)
    {
        ArgumentNullException.ThrowIfNull(planActions);
        var names = planActions.Select(a => a.Name).ToList();
        _templates[(stateFlags, labor, gold, goalName)] = names;
    }

    public void Clear() => _templates.Clear();
}
