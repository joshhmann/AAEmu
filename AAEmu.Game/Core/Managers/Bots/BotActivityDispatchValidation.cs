namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>One wiring gap: an activity a registered module can elect that nothing carries.</summary>
internal readonly record struct BotActivityDispatchGap(string ModuleName, string? Activity, string Reason);

/// <summary>
/// The result of checking the registered module set against
/// <see cref="BotActivityDispatchSurface"/> (plan T9, audit L3-a).
/// </summary>
internal sealed record BotActivityDispatchReport(
    int ModuleCount,
    int DeclaredActivityCount,
    IReadOnlyList<BotActivityDispatchGap> Gaps)
{
    /// <summary>True when every registered module declares only carried activities.</summary>
    internal bool IsClean => Gaps.Count == 0;
}

/// <summary>
/// The startup half of the dispatcher-or-deny contract (plan T9, audit L3-a):
/// every activity a registered module can elect must be carried by something in
/// <see cref="BotActivityDispatchSurface"/>, and every registered module must
/// declare what it can elect.
///
/// This runs over the REAL module set the container holds and the REAL dispatch
/// surface the executor's gates resolve through — not a hand-kept parallel list
/// — so a module registered without a carrier (today: the fishing contest, whose
/// activity the executor has no branch for) is reported instead of discovered in
/// the field. The enforcement stays the arbiter's runtime refusal
/// (<see cref="IBotGoalArbiter.OwnerlessRefusalCount"/>): the plan's stop
/// condition forbids implementing the missing capability here.
///
/// What it does NOT cover (review D2 on card <c>player-ai-dispatcher-or-deny</c>):
/// the surface table is hand-kept, so deleting a dispatch branch while its rule
/// stays behind is not caught here — the table would still say the activity is
/// carried. That direction is covered by each family's own rig tests over the
/// real executor (removing the needs rule fails 35 of the 73 needs-farm tests,
/// recorded in E322 as red proof b), not by this check.
/// </summary>
internal static class BotActivityDispatchValidation
{
    /// <summary>Checks a module set and returns the gaps it found.</summary>
    internal static BotActivityDispatchReport Validate(IEnumerable<IBotActivityModule>? modules)
    {
        var gaps = new List<BotActivityDispatchGap>();
        var moduleCount = 0;
        var declared = 0;

        var served = new HashSet<BotMode>();
        foreach (var module in modules ?? [])
        {
            moduleCount++;
            served.UnionWith(module.ServedModes);
            var names = module.DeclaredActivities;
            if (names.Count == 0)
            {
                gaps.Add(new BotActivityDispatchGap(module.Name, null,
                    "the module declares no activity, so the arbiter cannot check what it elects"));
                continue;
            }

            foreach (var name in names)
            {
                declared++;
                if (!BotActivityDispatchSurface.IsCarried(name))
                    gaps.Add(new BotActivityDispatchGap(module.Name, name, "no dispatch owner carries this activity"));
            }
        }

        // The mode layer: every mode the arbiter fills needs an owner, or a bot
        // in that mode elects nothing (run 11: nobody owned a fight). Only
        // checked for a non-empty set, so an empty registry stays "nothing here".
        if (moduleCount > 0)
        {
            foreach (var mode in BotModes.ArbiterModes)
            {
                if (!served.Contains(mode))
                    gaps.Add(new BotActivityDispatchGap($"(mode {mode})", null,
                        "no registered module serves this mode, so a bot in it elects nothing"));
            }
        }

        return new BotActivityDispatchReport(moduleCount, declared, gaps);
    }

    /// <summary>One readable line per gap (log and test friendly).</summary>
    internal static IEnumerable<string> Describe(BotActivityDispatchReport report) =>
        report.Gaps.Select(gap => gap.Activity is null
            ? $"module {gap.ModuleName}: {gap.Reason}"
            : $"module {gap.ModuleName} declares {gap.Activity}: {gap.Reason}");
}
