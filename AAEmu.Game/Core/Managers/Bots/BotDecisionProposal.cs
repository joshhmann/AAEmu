using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Immutable copy of one actor observation. Decision code must consume this
/// snapshot rather than reading Character/world state while selecting.
/// </summary>
public sealed record BotObservedContext
{
    public uint ActorId { get; init; }
    public Vector3 Position { get; init; }
    public uint CurrentTargetObjId { get; init; }
    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Mp { get; init; }
    public int MaxMp { get; init; }
    public IReadOnlyList<uint> NearbyCharacterObjIds { get; init; } = [];
    public IReadOnlyList<uint> NearbyNpcObjIds { get; init; } = [];
    public IReadOnlyList<uint> NearbyDoodadObjIds { get; init; } = [];
    public IReadOnlyList<uint> ActiveQuestIds { get; init; } = [];

    /// <summary>Copies the mutable observation lists before policy evaluation.</summary>
    public static BotObservedContext From(ActorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new BotObservedContext
        {
            ActorId = observation.ActorId,
            Position = observation.Position,
            CurrentTargetObjId = observation.CurrentTargetObjId,
            Hp = observation.Hp,
            MaxHp = observation.MaxHp,
            Mp = observation.Mp,
            MaxMp = observation.MaxMp,
            NearbyCharacterObjIds = Copy(observation.NearbyCharacterObjIds),
            NearbyNpcObjIds = Copy(observation.NearbyNpcObjIds),
            NearbyDoodadObjIds = Copy(observation.NearbyDoodadObjIds),
            ActiveQuestIds = Copy(observation.ActiveQuestIds)
        };
    }

    /// <summary>Perceives through the existing actor query surface.</summary>
    public static BotObservedContext Capture(IGameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return From(actor.Observe());
    }

    private static IReadOnlyList<uint> Copy(IReadOnlyList<uint> values)
        => Array.AsReadOnly(values.ToArray());
}

/// <summary>A named hard legality check for one proposed action.</summary>
public sealed record BotProposalPrecondition
{
    public string Name { get; }
    public Func<BotObservedContext, bool> IsSatisfied { get; }

    public BotProposalPrecondition(string name, Func<BotObservedContext, bool> isSatisfied)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A precondition needs a name.", nameof(name));
        Name = name;
        IsSatisfied = isSatisfied ?? throw new ArgumentNullException(nameof(isSatisfied));
    }
}

/// <summary>The observable state predicate expected after a terminal action.</summary>
public sealed record BotProposalPostcondition
{
    public string Description { get; }
    public Func<BotObservedContext, bool> IsSatisfied { get; }

    public BotProposalPostcondition(string description, Func<BotObservedContext, bool> isSatisfied)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A postcondition needs a description.", nameof(description));
        Description = description;
        IsSatisfied = isSatisfied ?? throw new ArgumentNullException(nameof(isSatisfied));
    }
}

/// <summary>
/// A legal, explainable proposal for one existing <see cref="IGameplayActor"/>
/// action. The proposal contains intent and policy metadata only; it does not
/// execute gameplay or create a second gameplay path.
/// </summary>
public sealed class BotDecisionProposal
{
    public const int MaxPersonalityWeight = 100;
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

    public string Goal { get; }
    public ActorActionType Action { get; }
    public uint TargetId { get; }
    public Vector3? Destination { get; }
    public uint SkillId { get; }
    public object? Payload { get; }
    public IReadOnlyList<BotProposalPrecondition> HardPreconditions { get; }
    public BotProposalPostcondition ExpectedPostcondition { get; }
    public string IdempotencyKey { get; }
    public TimeSpan Timeout { get; }
    public string Rationale { get; }
    public string PolicyVersion { get; }
    public int Priority { get; }
    public int PersonalityWeight { get; }
    public string TieBreakKey { get; }

    public BotDecisionProposal(
        string goal,
        ActorActionType action,
        uint targetId,
        BotProposalPostcondition expectedPostcondition,
        string idempotencyKey,
        TimeSpan timeout,
        string rationale,
        string policyVersion,
        int priority = 0,
        int personalityWeight = 0,
        string? tieBreakKey = null,
        Vector3? destination = null,
        uint skillId = 0,
        object? payload = null,
        IEnumerable<BotProposalPrecondition>? hardPreconditions = null)
    {
        Goal = RequireText(goal, nameof(goal));
        Action = action;
        TargetId = targetId;
        ExpectedPostcondition = expectedPostcondition ?? throw new ArgumentNullException(nameof(expectedPostcondition));
        IdempotencyKey = RequireText(idempotencyKey, nameof(idempotencyKey));
        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Timeout must be > 0 and <= {MaxTimeout}.");
        Timeout = timeout;
        Rationale = RequireText(rationale, nameof(rationale));
        PolicyVersion = RequireText(policyVersion, nameof(policyVersion));
        Priority = priority;
        PersonalityWeight = Math.Clamp(personalityWeight, -MaxPersonalityWeight, MaxPersonalityWeight);
        TieBreakKey = tieBreakKey ?? string.Empty;
        Destination = destination;
        SkillId = skillId;
        Payload = payload;
        HardPreconditions = Array.AsReadOnly((hardPreconditions ?? []).ToArray());
        if (HardPreconditions.Any(p => p == null))
            throw new ArgumentException("Hard preconditions cannot contain null entries.", nameof(hardPreconditions));
    }

    private static string RequireText(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", parameter);
        return value;
    }
}

/// <summary>Why one proposal was not selected.</summary>
public sealed record BotProposalRejection(BotDecisionProposal Proposal, string Reason);

/// <summary>Explainable result of deterministic proposal selection.</summary>
public sealed record BotDecisionSelection(
    BotDecisionProposal? Proposal,
    IReadOnlyList<BotProposalRejection> Rejections,
    string Explanation)
{
    public bool HasProposal => Proposal != null;
}

/// <summary>
/// Bounded deterministic selector. Legality is evaluated before preference;
/// personality can only adjust an already-legal proposal and is clamped to the
/// contract bound. Fixed priority remains the primary ordering key.
/// </summary>
public static class BotDecisionSelector
{
    public const int MaxCandidates = 64;

    public static BotDecisionSelection Select(
        BotObservedContext context,
        IEnumerable<BotDecisionProposal> proposals,
        int maxCandidates = MaxCandidates)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proposals);
        if (maxCandidates is < 1 or > MaxCandidates)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));

        var candidates = proposals.ToList();
        if (candidates.Count > maxCandidates)
            return new BotDecisionSelection(null, [],
                $"candidate bound exceeded ({candidates.Count} > {maxCandidates})");
        if (candidates.Any(p => p == null))
            throw new ArgumentException("Decision proposals cannot contain null entries.", nameof(proposals));

        var rejections = new List<BotProposalRejection>();
        var legal = new List<(BotDecisionProposal Proposal, int Index)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var proposal = candidates[index];
            var failed = proposal.HardPreconditions.FirstOrDefault(precondition =>
            {
                try
                {
                    return !precondition.IsSatisfied(context);
                }
                catch (Exception ex)
                {
                    rejections.Add(new BotProposalRejection(proposal,
                        $"precondition '{precondition.Name}' failed: {ex.GetType().Name}"));
                    return true;
                }
            });
            if (failed != null)
            {
                if (!rejections.Any(r => ReferenceEquals(r.Proposal, proposal)))
                    rejections.Add(new BotProposalRejection(proposal, $"precondition '{failed.Name}' was not satisfied"));
                continue;
            }
            legal.Add((proposal, index));
        }

        if (legal.Count == 0)
            return new BotDecisionSelection(null, Array.AsReadOnly(rejections.ToArray()),
                rejections.Count == 0 ? "no proposals" : "no legal proposal");

        var winner = legal
            .OrderByDescending(x => x.Proposal.Priority)
            .ThenByDescending(x => Math.Clamp(x.Proposal.PersonalityWeight, -BotDecisionProposal.MaxPersonalityWeight, BotDecisionProposal.MaxPersonalityWeight))
            .ThenBy(x => x.Proposal.TieBreakKey, StringComparer.Ordinal)
            .ThenBy(x => x.Index)
            .First().Proposal;
        return new BotDecisionSelection(winner, Array.AsReadOnly(rejections.ToArray()),
            $"selected {winner.Goal}/{winner.Action}: {winner.Rationale} (policy {winner.PolicyVersion})");
    }
}

/// <summary>Terminal result of dispatching one selected proposal through an actor.</summary>
public sealed record BotDecisionExecution(
    BotObservedContext Observation,
    BotDecisionProposal Proposal,
    ActorRequest Request,
    BotObservedContext? TerminalObservation,
    bool ExpectedPostconditionSatisfied);

/// <summary>
/// Small execution bridge for a selected proposal. The caller supplies the
/// existing actor method mapping; this bridge only enforces legality, dispatch,
/// and terminal observation before the next scheduler wake replans.
/// </summary>
public static class BotDecisionCycle
{
    public static BotDecisionExecution Execute(
        IGameplayActor actor,
        BotObservedContext observation,
        BotDecisionProposal proposal,
        Func<IGameplayActor, BotDecisionProposal, ActorRequest> dispatch)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(dispatch);
        if (observation.ActorId != actor.ActorId)
            throw new ArgumentException("Observation belongs to another actor.", nameof(observation));

        var selection = BotDecisionSelector.Select(observation, [proposal]);
        if (!selection.HasProposal)
            throw new InvalidOperationException($"Selected proposal is no longer legal: {selection.Explanation}");

        var request = dispatch(actor, proposal) ?? throw new InvalidOperationException("Decision dispatch returned null.");
        if (!request.IsTerminal)
            return new BotDecisionExecution(observation, proposal, request, null, false);

        var terminal = BotObservedContext.Capture(actor);
        var postconditionSatisfied = proposal.ExpectedPostcondition.IsSatisfied(terminal);
        return new BotDecisionExecution(observation, proposal, request, terminal, postconditionSatisfied);
    }
}
