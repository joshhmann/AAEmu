using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest-bootstrap module options. Default OFF: quest pursuit never
/// arbitrates unless the deployment opts in (env
/// <c>AAEMU_QUEST_BOOTSTRAP_ENABLED=1</c>, mirroring
/// <see cref="NeedsFarmModuleOptions"/>).
/// </summary>
public sealed record QuestBootstrapModuleOptions
{
    /// <summary>Master gate. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>Copper below which a quest-less bot still seeks work (the copper bootstrap).</summary>
    public long GoldTarget { get; init; } = 1000;

    /// <summary>Reads the deployment gate from the environment (default OFF).</summary>
    public static QuestBootstrapModuleOptions FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED");
        return new QuestBootstrapModuleOptions { Enabled = env is "1" or "true" or "True" };
    }
}

/// <summary>
/// Copper-bootstrap driver, arbitration half: when the deployment opts in
/// and the bot has quest work (active quests to advance/turn in, or no
/// copper to earn it with), bots arbitrate into <c>quest.progress</c>
/// instead of roaming.
///
/// Arbitration rank 58: above NeedsFarm (55) so a broke bot quests for
/// copper before the farm leg rests, below fishing contest (60) so events
/// still preempt work, below conflict-join (75), below schedules (100).
/// Eligibility only — the quest leg itself runs through
/// <see cref="QuestDecisionScenario"/> on the step path; this module never
/// accepts, advances, or turns in.
///
/// World-budget observance: denies at High/Critical pressure.
/// </summary>
public sealed class QuestBootstrapActivityModule : IBotActivityModule
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    public string Name => "QuestBootstrap";

    public int Priority { get; } = 58;

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "quest.progress";
    /// <summary>Exploratory travel only; no quest is chosen until live perception admits one.</summary>
    public const string DiscoveryActivityName = "quest.discovery";

    private readonly QuestBootstrapModuleOptions _options;
    private readonly Func<ServerPressure> _pressureProbe;

    public QuestBootstrapActivityModule(
        QuestBootstrapModuleOptions options,
        Func<ServerPressure>? pressureProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pressureProbe = pressureProbe ?? (() => ServerPressure.Healthy);
    }

    /// <summary>
    /// Chosen purpose per bot: the quest the module picked from the
    /// opportunity listing (0 = none). Memory-only, keyed by character id —
    /// the same discipline as the arbiter's own active-activity memory. Kept
    /// during ordinary waiting (a Running hold or a rest/wait wake never
    /// clears it); reconsidered only on terminal results or changed
    /// conditions (completion, disappearance, failure — read live below).
    /// Cleared by <see cref="Forget"/> on lifecycle teardown.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> _chosenQuest = new();

    /// <summary>Latest terminal outcome per bot (completed quest ids feed the next pick).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, System.Collections.Generic.HashSet<uint>> _completedQuests = new();

    // Deferral belongs to this existing goal owner, not to the giver or the
    // gameplay quest. It lasts only while the observed quest/source facts are
    // unchanged and is discarded on lifecycle generation change or Forget.
    private sealed record DeferredQuest(Quest Quest, QuestStatus Status, QuestComponentKind Step,
        int[] Objectives, bool SourceAvailable, QuestGiveUp Outcome);

    private sealed class QuestDeferrals(ulong generation)
    {
        public ulong Generation { get; } = generation;
        public BotOpportunityListing.OpportunityListing? LastListing { get; set; }
        public System.Collections.Concurrent.ConcurrentDictionary<uint, DeferredQuest> Quests { get; } = new();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, QuestDeferrals> _deferrals = new();

    // Admission memory, not spawner knowledge: a busy actor can only retain
    // discovery already elected for this activation. Unknown offers never
    // create a new discovery disposition.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ulong> _discoveryGeneration = new();

    /// <summary>Current chosen quest for a bot (0 = none).</summary>
    public uint GetChosenQuest(uint characterId)
        => _chosenQuest.TryGetValue(characterId, out var questId) ? questId : 0;

    /// <summary>Drops a bot's purpose memory (lifecycle teardown).</summary>
    public void Forget(uint characterId)
    {
        _chosenQuest.TryRemove(characterId, out _);
        _completedQuests.TryRemove(characterId, out _);
        _discoveryGeneration.TryRemove(characterId, out _);
        _deferrals.TryRemove(characterId, out _);
    }

    /// <summary>
    /// Feeds a terminal result back into the next pick: a completed quest is
    /// remembered (never re-picked) and the purpose is released so the next
    /// wake re-decides from the fresh listing.
    /// </summary>
    public void NotifyQuestTerminal(uint characterId, uint questId, bool completed)
    {
        if (completed)
        {
            var set = _completedQuests.GetOrAdd(characterId, static _ => new System.Collections.Generic.HashSet<uint>());
            lock (set)
                set.Add(questId);
        }
        _chosenQuest.TryRemove(characterId, out _);
    }


    /// <summary>
    /// Retires only the currently retained quest after its existing budget
    /// emits a named verdict. No quest is dropped or marked completed. Stale
    /// results for another quest and unearned verdicts cannot release the owner.
    /// </summary>
    public void NotifyQuestGiveUp(uint characterId, QuestGiveUp giveUp)
    {
        if (giveUp.Threshold <= 0 || giveUp.Wakes < giveUp.Threshold
            || string.IsNullOrWhiteSpace(giveUp.Reason)
            || GetChosenQuest(characterId) != giveUp.QuestId
            || !_deferrals.TryGetValue(characterId, out var deferred)
            || QuestLegActor.Resolve(characterId)?.Character.Quests.ActiveQuests.GetValueOrDefault(giveUp.QuestId) is not { } quest)
            return;

        deferred.Quests[giveUp.QuestId] = new DeferredQuest(quest, quest.Status, quest.Step,
            [.. quest.Objectives], SourceAvailable(deferred.LastListing, giveUp.QuestId), giveUp);
        _chosenQuest.TryRemove(characterId, out _);
    }

    private static bool SourceAvailable(BotOpportunityListing.OpportunityListing? listing, uint questId)
        => listing != null && (listing.ViableHunts.Any(h => h.QuestId == questId)
            || listing.ActiveWork.Any(w => w.QuestId == questId
                && (w.Reason.EndsWith("-source-perceived", StringComparison.Ordinal)
                    || w.Reason.EndsWith("-target-perceived", StringComparison.Ordinal))));

    private static void RefreshDeferrals(Character character, QuestDeferrals deferred,
        BotOpportunityListing.OpportunityListing listing)
    {
        foreach (var (questId, saved) in deferred.Quests)
        {
            var quest = character.Quests.ActiveQuests.GetValueOrDefault(questId);
            var sourceAvailable = SourceAvailable(listing, questId);
            if (!ReferenceEquals(quest, saved.Quest) || quest == null
                || quest.Status != saved.Status || quest.Step != saved.Step
                || !quest.Objectives.SequenceEqual(saved.Objectives)
                || (!saved.SourceAvailable && sourceAvailable
                    && saved.Outcome.Reason.StartsWith("GIVE-UP/UNRESOLVED-TARGET", StringComparison.Ordinal)))
                deferred.Quests.TryRemove(questId, out _);
            else if (saved.SourceAvailable && !sourceAvailable)
                deferred.Quests.TryUpdate(questId, saved with { SourceAvailable = false }, saved);
        }
        deferred.LastListing = listing;
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
            return BotActivityDecision.Deny("quest bootstrap disabled (QuestBootstrapModuleOptions.Enabled off)");

        var character = context.Bot.Character;
        if (character.IsDead)
            return BotActivityDecision.Deny("bot is dead");

        if (character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        if (_pressureProbe() is { } pressure && pressure >= ServerPressure.High)
            return BotActivityDecision.Deny($"held: server pressure {pressure} (world-budget observance)");

    // ONE existing goal owner: the quest module owns WHICH quest (the
    // BotBehaviorRuntime owns the running job once dispatched — that
    // split is load-bearing, never crossed here). The pick reads the
    // read-only opportunity listing off the bot's LIVE executor actor
    // (the same actor the quest leg will dispatch on — never a second
    // actor, never a temp GameplayActor), never a second scan: eligible
    // quest offers passing the real accept gate, viable hunts serving an
    // ACTIVE quest, accepted-quest work from actual quest state, recovery
    // needs from live vitals.
        var bot = context.Bot;
        var actor = QuestLegActor.Resolve(bot.CharacterId);
        if (actor == null)
            return BotActivityDecision.Deny("no live quest-leg actor for opportunity listing");
        var listing = BotOpportunityListing.List(actor);
        if (!listing.CoverageValid)
            return BotActivityDecision.Deny($"opportunity coverage invalid ({listing.CoverageReason})");
        if (listing.Recovery.NeedsRecovery)
            return BotActivityDecision.Deny($"recovery owns the wake ({listing.Recovery.Reason})");

        // Keep the chosen purpose during ordinary waiting: a live,
        // still-actionable, still-uncompleted purpose stays picked without
        // re-ranking (no churn while a Running hold or a wait wake passes).
        // Actionable reads actual quest state (accepted work with a named leg,
        // a fresh offer, or live prey) — never offer/prey presence alone.
        // Unknown-offer observations never prove disappearance: when the
        // listing flags busy-refused quest offers, the eligible set is
        // incomplete, so a purpose that is neither completed nor hunt-visible
        // is held (not cleared, not re-picked) until an authoritative scan.
        // A genuinely blocked quest (named blocker) releases immediately.
        var characterId = bot.CharacterId;
        var deferred = _deferrals.AddOrUpdate(characterId,
            _ => new QuestDeferrals(bot.Generation),
            (_, prior) => prior.Generation == bot.Generation ? prior : new QuestDeferrals(bot.Generation));
        RefreshDeferrals(character, deferred, listing);
        if (_discoveryGeneration.TryGetValue(characterId, out var discoveryGeneration)
            && discoveryGeneration != bot.Generation)
            _discoveryGeneration.TryRemove(characterId, out _);
        if (_chosenQuest.TryGetValue(characterId, out var kept)
            && !IsCompleted(characterId, kept)
            && listing.BlockedWork.Any(b => b.QuestId == kept))
        {
            var blocker = listing.BlockedWork.First(b => b.QuestId == kept).Blocker;
            _chosenQuest.TryRemove(characterId, out _);
            return BotActivityDecision.Deny($"quest {kept} blocked ({blocker})");
        }
        // Keep the accepted purpose only while the listing still shows a live
        // reason to hold it: a named active-work leg, a fresh offer, or live
        // prey. An accepted quest whose objective leg withdrew (no active-work
        // row) and whose hunt row is gone releases here so the policy below
        // re-picks the next actionable quest — absence from the narrow
        // offer/prey lists alone never releases while an active-work leg
        // persists (the accept→objective→return chain continues without
        // another offer or live prey).
        var keptWork = kept != 0
            ? listing.ActiveWork.FirstOrDefault(q => q.QuestId == kept)
            : null;
        var keptOffered = kept != 0 && listing.EligibleQuests.Any(q => q.QuestId == kept);
        var keptHunted = kept != 0 && listing.ViableHunts.Any(h => h.QuestId == kept);
        if (kept != 0
            && !IsCompleted(characterId, kept)
            && (keptWork != null || keptOffered || keptHunted)
            && (keptWork == null || keptWork.Leg != "hunt" || keptHunted))
        {
            _discoveryGeneration.TryRemove(characterId, out _);
            return BotActivityDecision.Allow(ActivityName);
        }
        if (kept != 0 && !IsCompleted(characterId, kept) && listing.QuestOffersUnknown)
        {
            _discoveryGeneration.TryRemove(characterId, out _);
            return BotActivityDecision.Allow(ActivityName);
        }

        // Reconsider on actual results or changed conditions: completion,
        // blockage, a withdrawn objective leg with no live hunt/offer, or
        // disappearance releases the purpose (NotifyQuestTerminal feeds
        // terminals; a named blocker or absence from an AUTHORITATIVE fresh
        // listing proves the quest is done or unusable) and the deterministic
        // policy re-picks below.
        if (kept != 0 && (IsCompleted(characterId, kept)
            || listing.BlockedWork.Any(b => b.QuestId == kept)
            || (!listing.ActiveWork.Any(q => q.QuestId == kept)
                && !listing.EligibleQuests.Any(q => q.QuestId == kept)
                && !listing.ViableHunts.Any(h => h.QuestId == kept))))
            _chosenQuest.TryRemove(characterId, out _);

        var pick = PickQuest(characterId, listing, deferred);
        if (pick == 0)
        {
            if (listing.QuestOffersUnknown)
                return context.ActiveActivity == DiscoveryActivityName
                    && _discoveryGeneration.TryGetValue(characterId, out discoveryGeneration)
                    && discoveryGeneration == bot.Generation
                    ? BotActivityDecision.Allow(DiscoveryActivityName)
                    : BotActivityDecision.Deny("quest offers unknown (actor busy) — holding for an authoritative scan");
            if (listing.ActiveWork.Count != 0 || listing.BlockedWork.Count != 0)
                return BotActivityDecision.Deny("opportunity listing names no actionable quest work");

            _chosenQuest.TryRemove(characterId, out _);
            _discoveryGeneration[characterId] = bot.Generation;
            return BotActivityDecision.Allow(DiscoveryActivityName);
        }
        _discoveryGeneration.TryRemove(characterId, out _);
        _chosenQuest[characterId] = pick;
        return BotActivityDecision.Allow(ActivityName);
    }

    /// <summary>
    /// Small deterministic policy over the listing (no new selector, no
    /// second scan). Order: (1) an ACTIVE quest with a viable hunt and unmet
    /// need (nearest hunt wins — ordinary hunting where mechanics permit);
    /// (2) accepted work with a named NON-HUNT leg (credited progress,
    /// perceived source/target, supported resource legs, then unresolved work —
    /// talk/delivery/gather/return/wait legs continue without another offer
    /// or live prey); (3) the nearest observed eligible offer. Quest ids
    /// settle equal usefulness/cost. A hunt-leg row without live prey never
    /// outranks a quest with prey or an offer (a prey-less hunt hold would
    /// pin the bot). Completed quests (terminal feedback) and blocked quests
    /// (named blockers) never re-pick.
    /// </summary>
    private uint PickQuest(uint characterId, BotOpportunityListing.OpportunityListing listing, QuestDeferrals deferred)
    {
        var blocked = new HashSet<uint>(listing.BlockedWork.Select(b => b.QuestId));
        blocked.UnionWith(deferred.Quests.Keys);
        var hunted = new HashSet<uint>(listing.ViableHunts.Select(h => h.QuestId));
        var hunt = listing.ViableHunts
            .Where(h => !IsCompleted(characterId, h.QuestId) && h.HaveCount < h.NeedCount && !blocked.Contains(h.QuestId))
            .OrderBy(h => h.DistanceM)
            .ThenBy(h => h.TargetObjId)
            .FirstOrDefault();
        if (hunt != null)
        {
            Logger.Debug("Quest choice bot {CharacterId}: quest {QuestId}, unmet hunt {Have}/{Need}, target distance {DistanceM}m",
                characterId, hunt.QuestId, hunt.HaveCount, hunt.NeedCount, hunt.DistanceM);
            return hunt.QuestId;
        }
        var workCandidates = listing.ActiveWork
            .Where(q => !IsCompleted(characterId, q.QuestId) && !blocked.Contains(q.QuestId))
            .Where(q => q.Leg != "hunt" || hunted.Contains(q.QuestId))
            .OrderByDescending(WorkUsefulness)
            .ThenBy(q => q.QuestId)
            .ToArray();
        var work = workCandidates.FirstOrDefault();
        if (work != null)
        {
            Logger.Debug("Quest choice bot {CharacterId}: quest {QuestId}, active {Leg}, usefulness {Usefulness}, fact {Reason}; alternatives {Alternatives}",
                characterId, work.QuestId, work.Leg, WorkUsefulness(work), work.Reason,
                string.Join("; ", workCandidates.Skip(1).Take(8).Select(q => $"quest {q.QuestId}: usefulness {WorkUsefulness(q)}, {q.Reason}")));
            return work.QuestId;
        }
        var offerCandidates = listing.EligibleQuests
            .Where(q => !IsCompleted(characterId, q.QuestId) && !blocked.Contains(q.QuestId))
            .OrderBy(q => float.IsFinite(q.DistanceM) && q.DistanceM >= 0 ? q.DistanceM : float.PositiveInfinity)
            .ThenBy(q => q.QuestId)
            .ToArray();
        var eligible = offerCandidates.FirstOrDefault();
        if (eligible != null)
            Logger.Debug("Quest choice bot {CharacterId}: quest {QuestId}, eligible offer distance {DistanceM}m, fact {Reason}; alternatives {Alternatives}",
                characterId, eligible.QuestId, eligible.DistanceM, eligible.Reason,
                string.Join("; ", offerCandidates.Skip(1).Take(8).Select(q => $"quest {q.QuestId}: distance {q.DistanceM}m, {q.Reason}")));
        return eligible?.QuestId ?? 0;
    }

    // Only already-observed facts: Ready/credited is progress, not completion;
    // source perception is availability, not reachability or a safe-fight estimate.
    // Retention above this policy always wins over a newly better candidate.
    private static int WorkUsefulness(BotOpportunityListing.ActiveQuestWork work)
        => work.Leg is "advance" or "return" or "turn-in" ? 3
            : work.Reason.EndsWith("-source-perceived", StringComparison.Ordinal)
                || work.Reason.EndsWith("-target-perceived", StringComparison.Ordinal) ? 2
            : work.Leg is "delivery" or "use-item" or "gather" ? 1 : 0;

    private bool IsCompleted(uint characterId, uint questId)
    {
        if (!_completedQuests.TryGetValue(characterId, out var set) || set == null)
            return false;
        lock (set)
            return set.Contains(questId);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var discovery = _discoveryGeneration.TryGetValue(context.Bot.CharacterId, out var generation)
            && generation == context.Bot.Generation;
        return new BotActivity(discovery ? DiscoveryActivityName : ActivityName, Name);
    }
}

/// <summary>
/// Test/live bridge: resolves the bot's LIVE quest-leg actor (the same
/// actor the executor's quest leg dispatches on) for the opportunity
/// listing. Production wires the executor's GetBotState actor; tests stage
/// a per-bot actor. Never creates an actor — null when the bot has no live
/// quest-leg state yet (the module denies, the listing waits).
/// </summary>
public static class QuestLegActor
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, IGameplayActor> Staged = new();
    private static Func<uint, IGameplayActor?>? _resolver;

    /// <summary>Production wiring: resolve through the live executor state.</summary>
    public static void SetResolver(Func<uint, IGameplayActor?>? resolver) => _resolver = resolver;

    /// <summary>Test seam: stage a live actor for a bot.</summary>
    public static void Stage(uint characterId, IGameplayActor actor) => Staged[characterId] = actor;

    /// <summary>Test seam: drop a staged actor.</summary>
    public static void Clear(uint characterId) => Staged.TryRemove(characterId, out _);

    /// <summary>Test seam: drop all staged actors.</summary>
    public static void ClearAll() => Staged.Clear();

    /// <summary>Resolves the live actor (staged test actor first, then production).</summary>
    public static IGameplayActor? Resolve(uint characterId)
        => Staged.TryGetValue(characterId, out var staged)
            ? staged
            : _resolver?.Invoke(characterId);
}
