using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Operator assignment band (plan T1): the ONE module that can put an operator's
/// assignment in front of every other activity. It sits ABOVE the highest
/// ordinary band (Schedules = 100), so while an assignment is live this band
/// decides alone and no lower module's CanActivate is consulted — that is what
/// makes an assignment a preemption instead of a competing motivation.
///
/// The module owns no route and no per-bot state of its own: the assignment
/// lives in <see cref="IBotAssignmentRegistry"/> (one store for the HTTP plane,
/// the E2E bridge, the executor and this module), and the walk is executed by
/// the ordinary route layer through BotRoamStepExecutor's assigned-travel leg.
/// The module's job is the two things only the arbiter can do — decide whether
/// the assignment is honoured, and release the previous owner when it takes
/// over.
///
/// Bounded by construction: every assignment carries an expiry, so this band can
/// never hold election forever.
/// </summary>
public sealed class AssignedTravelActivityModule : IBotActivityModule
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IBotAssignmentRegistry _assignments;
    private readonly BotRoamStepExecutor _executor;

    public AssignedTravelActivityModule(IBotAssignmentRegistry assignments, BotRoamStepExecutor executor)
    {
        _assignments = assignments;
        _executor = executor;
    }

    /// <inheritdoc />
    public string Name => "AssignedTravel";

    /// <summary>
    /// Above every ordinary band (Schedules = 100): a live assignment decides
    /// alone. Do not lower this below an ordinary band — the assignment would
    /// then have to win a weight comparison instead of preempting.
    /// </summary>
    public int Priority => 110;

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredActivities => [BotAssignmentActivities.Travel];

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        if (!_assignments.TryGetLive(context.Bot.CharacterId, out var assignment) || assignment == null)
            return BotActivityDecision.Deny("no operator assignment");

        // A dead or incapacitated bot cannot walk anywhere: the executor's
        // assigned-travel leg still runs for a dead bot and fails the
        // assignment with the reason, while the recovery band keeps the wake.
        if (context.Bot.Character.IsDead || context.Bot.Character.Hp <= 0)
            return BotActivityDecision.Deny("the bot is dead or incapacitated");

        // No weight: this band holds one module, so a weight would only ever be
        // compared with itself.
        return BotActivityDecision.Allow(assignment.Activity);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        if (!_assignments.TryGetLive(context.Bot.CharacterId, out var assignment) || assignment == null)
            return new BotActivity(BotAssignmentActivities.Travel, Name);

        // The outgoing module was asked to retire before this activation (the
        // arbiter's Deactivate call). A refusal is recorded and the assignment
        // does NOT claim success — the operator can see that the previous owner
        // is still holding its work.
        if (context.Retirement is { Retired: false } refusal)
        {
            _assignments.Fail(context.Bot.CharacterId,
                $"the previous activity refused to retire ({refusal.ModuleName}/{refusal.ActivityName}): {refusal.Reason}");
            Logger.Warn("Bot assignment could not take over bot {CharacterId}: {Module}/{Activity} refused to retire — {Reason}",
                context.Bot.CharacterId, refusal.ModuleName, refusal.ActivityName, refusal.Reason);
            return new BotActivity(assignment.Activity, Name);
        }

        _assignments.MarkActive(context.Bot.CharacterId,
            context.Retirement is { Retired: true } retired
                ? $"elected by the arbiter; previous activity {retired.ModuleName}/{retired.ActivityName} retired"
                : "elected by the arbiter");

        return new BotActivity(assignment.Activity, Name);
    }

    /// <inheritdoc />
    public bool Deactivate(BotActivityContext context)
    {
        // This module holds no state of its own; what it does own is the bot's
        // armed assignment route, and that must go with the band — otherwise a
        // superseded assignment would keep a route armed with no owner.
        _executor.ReleaseAssignedTravel(context.Bot.CharacterId, "the assignment band was superseded");
        Logger.Info("Bot assignment band released: bot {CharacterId} — {Active} superseded {Activity}",
            context.Bot.CharacterId, context.ActiveActivity ?? "(none)", BotAssignmentActivities.Travel);
        return true;
    }
}
