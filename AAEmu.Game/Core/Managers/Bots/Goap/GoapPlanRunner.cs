#nullable enable

using NLog;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Autonomous plan runner and decision runtime managing the full lifecycle of GOAP goals,
/// multi-tick action execution, intent preservation across interrupts, effect verification,
/// bounded failure recovery, and telemetry.
/// </summary>
public sealed class GoapPlanRunner : IGoapPlanRunner
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IBotWorldStateProvider _stateProvider;
    private readonly IGoalArbitrator _goalArbitrator;
    private readonly IGoapPlanner _planner;
    private readonly GoapActionRegistry _actionRegistry;
    private readonly PlanTemplateCache? _templateCache;
    private readonly IGoapTelemetrySink? _telemetrySink;

    public GoapGoal? PrimaryGoal { get; private set; }
    public GoapGoal? InterruptGoal { get; private set; }
    public GoapGoal? ActiveGoal => InterruptGoal ?? PrimaryGoal;

    public GoapPlanResult? ActivePlan { get; private set; }
    public int CurrentActionIndex { get; private set; }
    public GoapActionStatus CurrentActionStatus { get; private set; } = GoapActionStatus.NotStarted;
    public ActorRequest? CurrentActorRequest { get; private set; }

    public int ActionRetryCount { get; private set; }
    public int MaxActionRetries { get; init; } = 2;

    public IGoapAction? CurrentAction =>
        ActivePlan != null && CurrentActionIndex >= 0 && CurrentActionIndex < ActivePlan.Actions.Count
            ? ActivePlan.Actions[CurrentActionIndex]
            : null;

    public GoapPlanRunner(
        IBotWorldStateProvider? stateProvider = null,
        IGoalArbitrator? goalArbitrator = null,
        IGoapPlanner? planner = null,
        GoapActionRegistry? actionRegistry = null,
        PlanTemplateCache? templateCache = null,
        IGoapTelemetrySink? telemetrySink = null)
    {
        _stateProvider = stateProvider ?? new BotWorldStateProvider();
        _goalArbitrator = goalArbitrator ?? new GoalArbitrator();
        _planner = planner ?? new GoapPlanner();
        _actionRegistry = actionRegistry ?? GoapActionRegistry.Instance;
        _templateCache = templateCache;
        _telemetrySink = telemetrySink;
    }

    public void SetPrimaryGoal(GoapGoal? goal, BotContext context)
    {
        if (PrimaryGoal?.Name == goal?.Name)
            return;

        PrimaryGoal = goal;
        if (InterruptGoal == null)
        {
            InvalidatePlan("Primary goal changed explicitly", context);
            if (goal != null)
            {
                EmitTelemetry(GoapTelemetryEventType.GoalSelected, goal.Name, null, "Explicitly configured primary goal", 0, context);
            }
        }
    }

    public void InvalidatePlan(string reason, BotContext context)
    {
        if (ActivePlan != null || CurrentActionStatus != GoapActionStatus.NotStarted)
        {
            var actionName = CurrentAction?.Name;
            EmitTelemetry(GoapTelemetryEventType.PlanGenerated, ActiveGoal?.Name ?? "None", actionName, $"Plan invalidated: {reason}", 0, context);
            ActivePlan = null;
            CurrentActionIndex = 0;
            CurrentActionStatus = GoapActionStatus.NotStarted;
            CurrentActorRequest = null;
            ActionRetryCount = 0;
        }
    }

    public bool Tick(PlayerBotRuntime bot, IGameplayActor actor, BotContext context)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(context);

        // 1. SENSE: Project current observed world reality into BotWorldState
        var observedState = _stateProvider.Project(bot, context);

        // 2. REASON & INTERRUPT WATCHDOG: Check if urgent survival/combat interrupt is needed
        var arbitratedGoal = _goalArbitrator.ArbitrateGoal(bot, observedState, context, PrimaryGoal);

        bool isInterrupt = arbitratedGoal != null &&
            (arbitratedGoal.Name == "DefendSelf" || arbitratedGoal.Name == "Recover") &&
            arbitratedGoal.Name != PrimaryGoal?.Name;

        if (isInterrupt)
        {
            if (InterruptGoal?.Name != arbitratedGoal!.Name)
            {
                // New interrupt triggered! Preserve primary intent if not already captured
                if (PrimaryGoal == null && ActiveGoal != null && ActiveGoal.Name != arbitratedGoal.Name)
                {
                    PrimaryGoal = ActiveGoal;
                }

                InterruptGoal = arbitratedGoal;
                ActivePlan = null;
                CurrentActionIndex = 0;
                CurrentActionStatus = GoapActionStatus.NotStarted;
                CurrentActorRequest = null;
                ActionRetryCount = 0;

                EmitTelemetry(GoapTelemetryEventType.InterruptTriggered, InterruptGoal.Name, CurrentAction?.Name,
                    "High priority survival/combat interrupt triggered", observedState.Flags, context);
                EmitTelemetry(GoapTelemetryEventType.GoalSelected, InterruptGoal.Name, null,
                    "Interrupt goal selected by arbitrator", observedState.Flags, context);
            }
        }
        else
        {
            // No interrupt currently requested by arbitrator
            if (InterruptGoal != null)
            {
                // Interrupt has finished or is no longer required!
                var priorInterrupt = InterruptGoal.Name;
                InterruptGoal = null;

                // Check if primary intent is still valid and achievable
                if (PrimaryGoal != null)
                {
                    if (_goalArbitrator.IsGoalAchievable(PrimaryGoal, observedState, context))
                    {
                        EmitTelemetry(GoapTelemetryEventType.PrimaryIntentResumed, PrimaryGoal.Name, null,
                            $"Interrupt {priorInterrupt} cleared; primary intent resumed", observedState.Flags, context);
                        // Force plan regeneration from the bot's post-interrupt position & state
                        ActivePlan = null;
                        CurrentActionIndex = 0;
                        CurrentActionStatus = GoapActionStatus.NotStarted;
                        CurrentActorRequest = null;
                        ActionRetryCount = 0;
                    }
                    else
                    {
                        EmitTelemetry(GoapTelemetryEventType.GoalAbandoned, PrimaryGoal.Name, null,
                            "Primary intent is no longer achievable after interrupt", observedState.Flags, context);
                        PrimaryGoal = null;
                        ActivePlan = null;
                    }
                }
            }
        }

        // 3. ACTION EVALUATION: If an action is in-flight, evaluate its outcome
        if (CurrentActionStatus == GoapActionStatus.Running && ActivePlan != null && CurrentActionIndex < ActivePlan.Actions.Count)
        {
            var actionInFlight = ActivePlan.Actions[CurrentActionIndex];
            var status = actionInFlight.EvaluateStatus(bot, observedState, CurrentActorRequest, context);

            switch (status)
            {
                case GoapActionStatus.Succeeded:
                    EmitTelemetry(GoapTelemetryEventType.ActionSucceeded, ActiveGoal!.Name, actionInFlight.Name,
                        "Observed world state confirmed action effects", observedState.Flags, context);

                    context.Memory.ClearActionFailures(actionInFlight.Name);
                    CurrentActionIndex++;
                    CurrentActionStatus = GoapActionStatus.NotStarted;
                    CurrentActorRequest = null;
                    ActionRetryCount = 0;

                    if (CurrentActionIndex >= ActivePlan.Actions.Count)
                    {
                        ActivePlan = null;
                    }
                    break;

                case GoapActionStatus.Failed:
                    EmitTelemetry(GoapTelemetryEventType.ActionFailed, ActiveGoal!.Name, actionInFlight.Name,
                        "Action execution failed or effect unconfirmed in world", observedState.Flags, context);

                    context.Memory.RecordActionFailure(actionInFlight.Name);

                    if (ActionRetryCount < MaxActionRetries)
                    {
                        ActionRetryCount++;
                        CurrentActionStatus = GoapActionStatus.NotStarted;
                        CurrentActorRequest = null;
                        EmitTelemetry(GoapTelemetryEventType.ActionStarted, ActiveGoal.Name, actionInFlight.Name,
                            $"Retrying action (attempt {ActionRetryCount}/{MaxActionRetries})", observedState.Flags, context);
                    }
                    else
                    {
                        // Retry limit reached! Check if goal is still achievable
                        if (!_goalArbitrator.IsGoalAchievable(ActiveGoal, observedState, context))
                        {
                            EmitTelemetry(GoapTelemetryEventType.GoalAbandoned, ActiveGoal.Name, actionInFlight.Name,
                                "Goal abandoned: max retries reached and goal no longer achievable", observedState.Flags, context);
                            if (InterruptGoal != null)
                                InterruptGoal = null;
                            else
                                PrimaryGoal = null;

                            ActivePlan = null;
                            CurrentActionIndex = 0;
                            CurrentActionStatus = GoapActionStatus.NotStarted;
                            CurrentActorRequest = null;
                            ActionRetryCount = 0;
                            return false;
                        }

                        // Still achievable via alternate plan -> replan
                        EmitTelemetry(GoapTelemetryEventType.ReplanTriggered, ActiveGoal.Name, actionInFlight.Name,
                            "Max retries reached; triggering replan for alternate sequence", observedState.Flags, context);
                        ActivePlan = null;
                        CurrentActionIndex = 0;
                        CurrentActionStatus = GoapActionStatus.NotStarted;
                        CurrentActorRequest = null;
                        ActionRetryCount = 0;
                    }
                    break;

                case GoapActionStatus.Invalidated:
                    EmitTelemetry(GoapTelemetryEventType.ActionInvalidated, ActiveGoal!.Name, actionInFlight.Name,
                        "Action invalidated due to external world change or invalid target/POI", observedState.Flags, context);
                    EmitTelemetry(GoapTelemetryEventType.ReplanTriggered, ActiveGoal.Name, actionInFlight.Name,
                        "Triggering replan following action invalidation", observedState.Flags, context);

                    ActivePlan = null;
                    CurrentActionIndex = 0;
                    CurrentActionStatus = GoapActionStatus.NotStarted;
                    CurrentActorRequest = null;
                    ActionRetryCount = 0;
                    break;

                case GoapActionStatus.Running:
                default:
                    // In-flight execution continuing in world
                    return true;
            }
        }

        // 4. GOAL SATISFACTION CHECK: Is the active goal already satisfied in reality?
        if (ActiveGoal != null && ActiveGoal.IsSatisfied(observedState))
        {
            EmitTelemetry(GoapTelemetryEventType.GoalSatisfied, ActiveGoal.Name, null,
                "Observed world state satisfies goal target condition", observedState.Flags, context);

            if (InterruptGoal != null)
            {
                var finishedInterrupt = InterruptGoal.Name;
                InterruptGoal = null;
                if (PrimaryGoal != null && _goalArbitrator.IsGoalAchievable(PrimaryGoal, observedState, context))
                {
                    EmitTelemetry(GoapTelemetryEventType.PrimaryIntentResumed, PrimaryGoal.Name, null,
                        $"Interrupt {finishedInterrupt} satisfied; resuming primary intent", observedState.Flags, context);
                    ActivePlan = null;
                    CurrentActionIndex = 0;
                    CurrentActionStatus = GoapActionStatus.NotStarted;
                    CurrentActorRequest = null;
                    ActionRetryCount = 0;
                    return true;
                }
            }
            else
            {
                PrimaryGoal = null;
                ActivePlan = null;
                CurrentActionIndex = 0;
                CurrentActionStatus = GoapActionStatus.NotStarted;
                CurrentActorRequest = null;
                ActionRetryCount = 0;
                return false;
            }
        }

        // 5. INTENT ARBITRATION: Validate existing primary goal or select new active goal if none exists
        if (PrimaryGoal != null && !_goalArbitrator.IsGoalAchievable(PrimaryGoal, observedState, context))
        {
            EmitTelemetry(GoapTelemetryEventType.GoalAbandoned, PrimaryGoal.Name, null,
                "Primary goal is no longer achievable", observedState.Flags, context);
            PrimaryGoal = null;
            ActivePlan = null;
            CurrentActionIndex = 0;
            CurrentActionStatus = GoapActionStatus.NotStarted;
            CurrentActorRequest = null;
            ActionRetryCount = 0;
        }

        if (ActiveGoal == null)
        {
            PrimaryGoal = _goalArbitrator.ArbitrateGoal(bot, observedState, context, null);
            if (PrimaryGoal == null)
                return false; // Idle

            EmitTelemetry(GoapTelemetryEventType.GoalSelected, PrimaryGoal.Name, null,
                "Arbitrator selected new primary goal", observedState.Flags, context);
        }

        // 6. PLAN GENERATION / CACHE
        if (ActivePlan == null || !ActivePlan.Success)
        {
            // Attempt template cache resolution
            if (_templateCache != null && _templateCache.TryGetTemplate(observedState.Flags, observedState.Labor, observedState.Gold, ActiveGoal.Name, out var cachedActionNames))
            {
                // Template hit!
                EmitTelemetry(GoapTelemetryEventType.PlanTemplateHit, ActiveGoal.Name, null,
                    $"Plan template cache hit: {string.Join(" -> ", cachedActionNames)}", observedState.Flags, context);
            }

            var actions = _actionRegistry.GetAllActions();
            var planResult = _planner.Plan(bot, observedState, ActiveGoal, actions);

            if (!planResult.Success)
            {
                EmitTelemetry(GoapTelemetryEventType.GoalAbandoned, ActiveGoal.Name, null,
                    "Planner could not find any valid action sequence satisfying goal", observedState.Flags, context);

                if (InterruptGoal != null)
                    InterruptGoal = null;
                else
                    PrimaryGoal = null;

                ActivePlan = null;
                return false;
            }

            ActivePlan = planResult;
            CurrentActionIndex = 0;
            CurrentActionStatus = GoapActionStatus.NotStarted;
            CurrentActorRequest = null;
            ActionRetryCount = 0;

            EmitTelemetry(GoapTelemetryEventType.PlanGenerated, ActiveGoal.Name, null,
                $"Generated plan with {ActivePlan.Actions.Count} actions (Cost: {ActivePlan.TotalCost:F1})", observedState.Flags, context);

            if (_templateCache != null)
            {
                _templateCache.StoreTemplate(observedState.Flags, observedState.Labor, observedState.Gold, ActiveGoal.Name, ActivePlan.Actions);
            }
        }

        // 7. ACTION START
        if (ActivePlan != null && CurrentActionIndex < ActivePlan.Actions.Count && CurrentActionStatus == GoapActionStatus.NotStarted)
        {
            var nextAction = ActivePlan.Actions[CurrentActionIndex];
            if (!nextAction.CheckPreconditions(observedState))
            {
                // Preconditions violated by world state!
                EmitTelemetry(GoapTelemetryEventType.ActionInvalidated, ActiveGoal.Name, nextAction.Name,
                    "Observed world state does not satisfy action preconditions", observedState.Flags, context);
                EmitTelemetry(GoapTelemetryEventType.ReplanTriggered, ActiveGoal.Name, nextAction.Name,
                    "Triggering replan due to precondition invalidation", observedState.Flags, context);

                ActivePlan = null;
                CurrentActionIndex = 0;
                CurrentActionStatus = GoapActionStatus.NotStarted;
                CurrentActorRequest = null;
                return true;
            }

            CurrentActorRequest = nextAction.CreateActorRequest(bot, actor);
            CurrentActionStatus = GoapActionStatus.Running;

            EmitTelemetry(GoapTelemetryEventType.ActionStarted, ActiveGoal.Name, nextAction.Name,
                $"Dispatched actor request for {nextAction.Name}", observedState.Flags, context);
            return true;
        }

        return true;
    }

    private void EmitTelemetry(
        GoapTelemetryEventType eventType,
        string goalName,
        string? actionName,
        string reason,
        ulong stateFlags,
        BotContext context)
    {
        var ev = new GoapTelemetryEvent(
            CharacterId: 0,
            EventType: eventType,
            GoalName: goalName,
            ActionName: actionName,
            Reason: reason,
            StateFlags: stateFlags,
            TimestampUtc: context.GetUtcNow());

        _telemetrySink?.Record(ev);
    }
}
