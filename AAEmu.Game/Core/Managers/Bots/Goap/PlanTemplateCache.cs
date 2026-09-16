#nullable enable

using System.Collections.Concurrent;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Caches abstract symbolic action sequences for (StateFlags, GoalName) pairs.
/// Preserves plan reuse while avoiding rigid instance binding (merchants, targets, POIs
/// are bound per-bot dynamically from BotContext at runtime).
/// </summary>
public sealed class PlanTemplateCache
{
    private readonly ConcurrentDictionary<(ulong StateFlags, string GoalName), IReadOnlyList<string>> _templates = [];

    public int TemplateCount => _templates.Count;

    public bool TryGetTemplate(ulong stateFlags, string goalName, out IReadOnlyList<string> actionNames)
    {
        return _templates.TryGetValue((stateFlags, goalName), out actionNames!);
    }

    public void StoreTemplate(ulong stateFlags, string goalName, IReadOnlyList<IGoapAction> planActions)
    {
        ArgumentNullException.ThrowIfNull(planActions);
        var names = planActions.Select(a => a.Name).ToList();
        _templates[(stateFlags, goalName)] = names;
    }

    public void Clear() => _templates.Clear();
}
