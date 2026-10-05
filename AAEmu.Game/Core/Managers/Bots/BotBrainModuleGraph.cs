namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Why a declared brain-module graph was refused. The three causes are the
/// whole contract: a graph that names a module nobody declared, a graph that
/// declares the same thing twice, and a graph that asks for a circular
/// ordering are all refusals — never a warning, never a silent repair.
/// </summary>
public enum BotBrainModuleGraphError
{
    /// <summary>A module's <see cref="BotBrainModule.Requires"/> names a module the graph does not declare.</summary>
    UnknownDependency,

    /// <summary>Two declarations carry the same module name.</summary>
    DuplicateModule,

    /// <summary>One module lists the same dependency twice.</summary>
    DuplicateDependency,

    /// <summary>The declared edges cannot be ordered — one module transitively requires itself.</summary>
    DependencyCycle
}

/// <summary>
/// The named refusal <see cref="BotBrainModuleGraph.Validate(System.Collections.Generic.IReadOnlyList{BotBrainModule})"/>
/// throws. Carries the machine-readable <see cref="Error"/> beside the message so
/// a boot log line names the cause without parsing prose.
/// </summary>
public sealed class BotBrainModuleGraphException : InvalidOperationException
{
    public BotBrainModuleGraphException(BotBrainModuleGraphError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>The named cause of the refusal.</summary>
    public BotBrainModuleGraphError Error { get; }
}

/// <summary>
/// One DECLARED brain module: the module's name and the modules it requires to
/// be built first. The declaration is data only — it asserts an ordering edge,
/// it never loads, resolves, or runs anything.
/// </summary>
/// <param name="Name">The module name, unique within the graph.</param>
/// <param name="Requires">The module names that must come before this one. Empty when the module requires nothing.</param>
public sealed record BotBrainModule(string Name, IReadOnlyList<string> Requires);

/// <summary>
/// The declared dependency graph over the decision-layer brain modules, and the
/// OFFLINE validator that refuses an unorderable one.
///
/// WHY THIS EXISTS. The combat and loot brains reach travel through the travel
/// chain — <c>CombatTravelDispatch.Prepare</c> and <c>LootTravelDispatch.Prepare</c>
/// both call <c>TravelBrainPlanner.Prepare</c> — but nothing DECLARED that edge:
/// the coupling was real and invisible, and the quest director's gate
/// (<c>QuestDirector.Plan</c>) reads verbs, not modules, so it could not catch
/// it. A refactor that removed or reordered the travel chain would have shown up
/// as a behavior change on a live lane instead of as a load failure. This graph
/// is that edge, written down, and the validator is what makes the writing-down
/// load-bearing.
///
/// SAME SPIRIT AS agent-wow's <c>pkg/modules/discovery/manifest.go</c>: the
/// manifest is validated while it is being read — every name resolves, no
/// duplicates, no cycles — so a bad module graph is refused at load rather than
/// discovered by a wake. Only the checks are borrowed; AAEmu keeps its own
/// vocabulary and its own fail-closed direction.
///
/// FAIL-CLOSED, AND WHY IT IS NOT A WARNING. An unresolvable module name means
/// the graph no longer describes the code: either a brain was renamed, or an
/// edge was typed for a module that was never built. Either way a plan built from
/// this graph would be fiction, so the graph refuses. A cycle refuses for the
/// same reason with one step further out — there is no first module to build, so
/// no ordering exists at all.
///
/// VALIDATION IS NOT RUNTIME. Nothing here loads a module, resolves a service,
/// or reorders a wake; <see cref="Validate()"/> reads a static table and returns
/// an ordering the caller is free to ignore. The graph's only production caller
/// is the boot path (<c>Program.Main</c>), which validates before the host is
/// built — the same place the DB connectivity checks already refuse a boot.
/// </summary>
public static class BotBrainModuleGraph
{
    /// <summary>
    /// The declared modules, in dependency-first order.
    ///
    /// The three rows are exactly the decision-layer brains that share the travel
    /// chain today:
    ///   travel — the chain itself; it requires nothing, so it is the root every
    ///            other declared module resolves through.
    ///   combat — requires travel: the routed kite leg is a travel decision
    ///            (<c>CombatTravelDispatch.Prepare</c> → <c>TravelBrainPlanner.Prepare</c>),
    ///            and the combat brain banks that decision as its own evidence.
    ///   loot   — requires travel: the corpse approach leg is a travel decision
    ///            (<c>LootTravelDispatch.Prepare</c> → <c>TravelBrainPlanner.Prepare</c>),
    ///            and the loot brain asks the chain whether an approach exists.
    ///
    /// A brain with no cross-brain edge (the needs, survival and goap chains
    /// today) is deliberately NOT a row here: a row is a claim about an edge,
    /// and adding one for a module that has none would put an unproven claim in
    /// the same table the validator treats as proof. A brain earns its row when
    /// it first calls another brain.
    /// </summary>
    public static readonly IReadOnlyList<BotBrainModule> Modules =
    [
        new("travel", []),
        new("combat", ["travel"]),
        new("loot", ["travel"])
    ];

    /// <summary>
    /// Validates the production declarations and returns their dependency-first
    /// ordering. Called from the boot path before the host is built, so a
    /// mis-declared graph refuses the boot with the named error instead of
    /// surfacing later as a behavior difference on a live lane.
    /// </summary>
    /// <returns>The module names in dependency-first order (every module a
    /// module requires appears before it).</returns>
    /// <exception cref="BotBrainModuleGraphException">
    /// A declaration names an undeclared module, declares a name or a
    /// dependency twice, or the edges cannot be ordered (a cycle).
    /// </exception>
    public static IReadOnlyList<string> Validate()
        => Validate(Modules);

    /// <summary>
    /// Validates <paramref name="modules"/> offline and returns their
    /// dependency-first ordering. Pure: it reads the supplied declarations and
    /// allocates the ordering, and touches no engine state, no world and no
    /// service — which is what lets the failure cases be tested without a rig.
    ///
    /// Checks run in the order a reader needs them: names first (an unknown
    /// dependency makes every later count meaningless), then duplicates, then
    /// the ordering itself.
    /// </summary>
    /// <param name="modules">The declarations to validate. Not mutated.</param>
    /// <returns>The module names in dependency-first order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modules"/> is null.</exception>
    /// <exception cref="BotBrainModuleGraphException">
    /// A declaration names an undeclared module, declares a name or a
    /// dependency twice, or the edges cannot be ordered (a cycle).
    /// </exception>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BotBrainModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        // Pass 1 — every name is distinct, so the index below can rely on it.
        var declared = new Dictionary<string, BotBrainModule>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!declared.TryAdd(module.Name, module))
                throw new BotBrainModuleGraphException(BotBrainModuleGraphError.DuplicateModule,
                    $"BRAIN-MODULES/DUPLICATE-MODULE module={module.Name}");
        }

        // Pass 2 — every required name resolves to a declared module, and no
        // module lists the same requirement twice (a repeated edge is a typo,
        // never a second, stronger claim).
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var resolved = new List<string>(module.Requires.Count);
            foreach (var dependency in module.Requires)
            {
                if (!declared.ContainsKey(dependency))
                    throw new BotBrainModuleGraphException(BotBrainModuleGraphError.UnknownDependency,
                        $"BRAIN-MODULES/UNKNOWN-DEPENDENCY module={module.Name} dep={dependency}");
                if (resolved.Contains(dependency, StringComparer.Ordinal))
                    throw new BotBrainModuleGraphException(BotBrainModuleGraphError.DuplicateDependency,
                        $"BRAIN-MODULES/DUPLICATE-DEPENDENCY module={module.Name} dep={dependency}");
                resolved.Add(dependency);
            }
            edges[module.Name] = resolved;
        }

        // Pass 3 — Kahn's ordering. Every module whose requirements are all
        // placed is placed next; a module left over at the end transitively
        // requires itself, and the ones left over name the cycle's entry set.
        var order = new List<string>(modules.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new List<BotBrainModule>(modules);
        while (remaining.Count > 0)
        {
            var placedThisPass = 0;
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var module = remaining[i];
                var ready = true;
                foreach (var dependency in edges[module.Name])
                {
                    if (!placed.Contains(dependency))
                    {
                        ready = false;
                        break;
                    }
                }
                if (!ready)
                    continue;
                remaining.RemoveAt(i);
                placed.Add(module.Name);
                order.Add(module.Name);
                placedThisPass++;
            }
            if (placedThisPass == 0)
                throw new BotBrainModuleGraphException(BotBrainModuleGraphError.DependencyCycle,
                    $"BRAIN-MODULES/CYCLE modules={string.Join(',', remaining.ConvertAll(m => m.Name))}");
        }

        return order;
    }
}
