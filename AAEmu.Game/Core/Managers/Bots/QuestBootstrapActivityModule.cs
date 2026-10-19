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

    /// <summary>
    /// Consecutive legs of the same retained quest failing the same way before
    /// the module abandons that quest (the T4 return path). One failure is
    /// tolerated: a rejected move or a single leg that ran out of its walk budget
    /// is a fact about one wake, not proof of a dead end.
    /// </summary>
    public int LegFailureGiveUpThreshold { get; init; } = 3;

    /// <summary>
    /// How long the module may hold one quest while the bot stands idle in the
    /// same spot with no leg, no live action and no change to that quest before
    /// it gives the quest up. Populated-area run 6: a bot held quest.progress
    /// for 435 s at one spot after a turn-in, every quest leg answering "no
    /// legal quest proposal", and no other give-up rule could see it because
    /// nothing was ever refused. Zero disables the rule.
    /// </summary>
    public TimeSpan IdleGiveUpAfter { get; init; } = TimeSpan.FromMinutes(3);

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

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredActivities => [ActivityName, DiscoveryActivityName];

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "quest.progress";
    /// <summary>Exploratory travel only; no quest is chosen until live perception admits one.</summary>
    public const string DiscoveryActivityName = "quest.discovery";

    private readonly QuestBootstrapModuleOptions _options;
    private readonly Func<ServerPressure> _pressureProbe;
    private readonly TimeProvider _time;

    public QuestBootstrapActivityModule(
        QuestBootstrapModuleOptions options,
        Func<ServerPressure>? pressureProbe = null,
        TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pressureProbe = pressureProbe ?? (() => ServerPressure.Healthy);
        _time = time ?? TimeProvider.System;
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

    // T4 return path (plan §7 T4, audit L3-b): this module's OWN legs, as the
    // executor reported them (the arbiter hands each fact to the module that
    // owned the leg, once). Identity is (quest, action, detail): the same leg
    // failing the same way is a dead end, a different failure is a fresh fact.
    // Dropped by Forget, by a leg that did not fail, by the give-up itself and
    // by a lifecycle generation change.
    private sealed record LegFailures(ulong Generation, uint QuestId, ActorActionType Action, string Detail, int Count);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, LegFailures> _legFailures = new();

    // A quest abandoned after repeated leg failures that has no Quest record to
    // defer (an offer the bot never accepted): remembered for this generation so
    // a fresh listing cannot re-pick the same dead end. An abandoned ACCEPTED
    // quest is deferred instead — the deferral's own fact-change rule is the
    // release rule, so this set must not hold a second copy of it.
    private sealed class AbandonedQuests(ulong generation)
    {
        public ulong Generation { get; } = generation;
        public System.Collections.Generic.HashSet<uint> Quests { get; } = [];
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, AbandonedQuests> _legAbandoned = new();

    // Idle watch: the quest this module holds and the facts seen when the bot
    // last stood still for it (quest state, spot). Any leg, live action, move
    // or quest change restarts it.
    private sealed record IdleWatch(ulong Generation, uint QuestId, string QuestFacts,
        System.Numerics.Vector3 Position, DateTime Since, DateTime LastSeen);

    // A hold must be seen on consecutive wakes to count as idle: time spent
    // while another module owned the wake (a fight, a rest) or while this
    // module denied (battle, recovery, server pressure) is not time spent
    // standing idle for the quest. Wakes come every few seconds.
    private static readonly TimeSpan IdleWatchMaxGap = TimeSpan.FromSeconds(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, IdleWatch> _idleWatch = new();

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
        _legFailures.TryRemove(characterId, out _);
        _legAbandoned.TryRemove(characterId, out _);
        _idleWatch.TryRemove(characterId, out _);
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
                    && (saved.Outcome.Reason.StartsWith("GIVE-UP/UNRESOLVED-TARGET", StringComparison.Ordinal)
                        || saved.Outcome.Reason.StartsWith(IdleGiveUpPrefix, StringComparison.Ordinal))))
                deferred.Quests.TryRemove(questId, out _);
            else if (saved.SourceAvailable && !sourceAvailable)
                deferred.Quests.TryUpdate(questId, saved with { SourceAvailable = false }, saved);
        }
        deferred.LastListing = listing;
    }

    /// <summary>
    /// The module's own terminal leg, as reported through the T4 return path.
    /// Returns the give-up reason when the SAME refused leg has now repeated
    /// <see cref="QuestBootstrapModuleOptions.LegFailureGiveUpThreshold"/> times
    /// for the retained quest, and null otherwise — no fact, a leg that was not
    /// refused (a completed leg clears the memory; an interrupted or expired one
    /// leaves the streak as it was), or a refusal that has not earned the bound
    /// yet (the quest is still tried). Only a refusal counts: a timed-out leg may
    /// still be recovered by the executor's unstick nudge or the quest runtime's
    /// own wake budget, both of which already own that class. Abandoning means:
    /// the purpose is released,
    /// the quest is deferred through the SAME owner the quest runtime's give-up
    /// verdicts use (so its facts decide when it may be tried again), and a quest
    /// with no <see cref="Quest"/> record to defer is remembered for this
    /// generation instead so the next listing cannot re-pick it.
    /// </summary>
    private string? OwnLegFailure(BotActivityContext context, uint characterId, QuestDeferrals deferred)
    {
        if (context.LastLegOutcome is not { } leg)
            return null;

        if (leg.State != ActorLifecycleState.Rejected)
        {
            // Only a hard refusal counts as a dead end: the world said no. A
            // timed-out leg may still be recovered (the executor's unstick nudge
            // and the quest runtime's own wake budget both own that class). A
            // completed leg is progress and clears the memory; an interrupted or
            // expired one leaves the streak as it was.
            if (leg.State == ActorLifecycleState.Completed)
                _legFailures.TryRemove(characterId, out _);
            return null;
        }

        // Attribution: the fact belongs to the activity the leg was issued for.
        // A stale fact for a quest the runtime already released (its own give-up
        // verdict) must not be charged to quest discovery.
        var discovery = string.Equals(leg.ActivityName, DiscoveryActivityName, StringComparison.Ordinal);
        var questId = discovery ? 0u : GetChosenQuest(characterId);
        if (!discovery && questId == 0)
        {
            _legFailures.TryRemove(characterId, out _);
            return null;
        }

        var detail = string.IsNullOrWhiteSpace(leg.Detail) ? leg.Action.ToString() : leg.Detail;
        var same = _legFailures.TryGetValue(characterId, out var saved)
            && saved.Generation == context.Bot.Generation
            && saved.QuestId == questId
            && saved.Action == leg.Action
            && string.Equals(saved.Detail, detail, StringComparison.Ordinal);
        var count = same ? saved!.Count + 1 : 1;
        if (count < _options.LegFailureGiveUpThreshold)
        {
            _legFailures[characterId] = new LegFailures(context.Bot.Generation, questId, leg.Action, detail, count);
            Logger.Debug("Quest leg failed bot {CharacterId}: quest {QuestId} {Action} ({Detail}) {Count}/{Threshold} — still trying",
                characterId, questId, leg.Action, detail, count, _options.LegFailureGiveUpThreshold);
            return null;
        }

        _legFailures.TryRemove(characterId, out _);
        var target = questId == 0 ? "quest discovery" : $"quest {questId}";
        var reason = $"gave up on {target} after {count} failed legs ({detail})";
        Logger.Warn("Quest give-up bot {CharacterId}: {Reason}", characterId, reason);
        if (questId == 0)
            return reason;

        // The deferral owner reads the retained purpose, so it must be asked
        // BEFORE the purpose is released here; the removal after it keeps the
        // purpose released on the paths where the owner declines for its own
        // reasons (a quest the runtime already replaced, a missing record).
        NotifyQuestGiveUp(characterId, new QuestGiveUp(questId, $"GIVE-UP/LEG-FAILED: {detail}", count,
            _options.LegFailureGiveUpThreshold));
        _chosenQuest.TryRemove(characterId, out _);
        if (!deferred.Quests.ContainsKey(questId))
            AbandonedFor(characterId, context.Bot.Generation).Quests.Add(questId);
        return reason;
    }

    /// <summary>
    /// The quest ids this bot gave up on with no quest record to defer, for the
    /// current lifecycle generation (a fresh generation starts with no memory).
    /// </summary>
    private AbandonedQuests AbandonedFor(uint characterId, ulong generation)
        => _legAbandoned.AddOrUpdate(characterId,
            _ => new AbandonedQuests(generation),
            (_, prior) => prior.Generation == generation ? prior : new AbandonedQuests(generation));

    private const string IdleGiveUpPrefix = "GIVE-UP/IDLE";

    /// <summary>
    /// Holds <paramref name="questId"/> for this wake, or gives it up when the
    /// bot has stood idle for it longer than
    /// <see cref="QuestBootstrapModuleOptions.IdleGiveUpAfter"/>: same spot
    /// (within 2 m), no live action, no leg reported back, and the quest's own
    /// status, step and objective counts unchanged. A bot that is walking,
    /// fighting, casting or getting credit is never idle, and only holds seen on
    /// consecutive wakes count (a gap while another module owned the bot
    /// restarts the watch). The give-up goes
    /// through the same deferral as the other give-ups, so the quest comes back
    /// once its facts change (or, like an unresolved target, once its source is
    /// seen again).
    /// </summary>
    private BotActivityDecision HoldQuest(BotActivityContext context, IGameplayActor actor,
        QuestDeferrals deferred, uint questId)
    {
        var characterId = context.Bot.CharacterId;
        if (_options.IdleGiveUpAfter <= TimeSpan.Zero)
            return BotActivityDecision.Allow(ActivityName);

        var now = _time.GetUtcNow().UtcDateTime;
        var position = actor.Character.Transform.World.Position;
        var quest = actor.Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        var facts = quest == null
            ? "offer"
            : $"{quest.Status}/{quest.Step}/{string.Join(",", quest.Objectives)}";
        var busy = actor.ActiveRequest is { IsTerminal: false } || context.LastLegOutcome != null;
        if (busy
            || !_idleWatch.TryGetValue(characterId, out var watch)
            || watch.Generation != context.Bot.Generation
            || watch.QuestId != questId
            || !string.Equals(watch.QuestFacts, facts, StringComparison.Ordinal)
            || System.Numerics.Vector3.Distance(watch.Position, position) > 2f
            || now - watch.LastSeen > IdleWatchMaxGap)
        {
            _idleWatch[characterId] = new IdleWatch(context.Bot.Generation, questId, facts, position, now, now);
            return BotActivityDecision.Allow(ActivityName);
        }
        if (now - watch.Since < _options.IdleGiveUpAfter)
        {
            _idleWatch[characterId] = watch with { LastSeen = now };
            return BotActivityDecision.Allow(ActivityName);
        }

        _idleWatch.TryRemove(characterId, out _);
        var idleSeconds = (int)(now - watch.Since).TotalSeconds;
        var reason = $"gave up on quest {questId} after standing idle {idleSeconds}s with nothing to do for it ({facts})";
        Logger.Warn("Quest give-up bot {CharacterId}: {Reason}", characterId, reason);
        NotifyQuestGiveUp(characterId, new QuestGiveUp(questId, $"{IdleGiveUpPrefix}: idle {idleSeconds}s", 1, 1));
        _chosenQuest.TryRemove(characterId, out _);
        if (!deferred.Quests.ContainsKey(questId))
            AbandonedFor(characterId, context.Bot.Generation).Quests.Add(questId);
        return BotActivityDecision.Deny(reason);
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
        // T4 return path (plan §7 T4, audit L3-b): the leg this module's own
        // activity issued came back terminal. A repeated identical failure is a
        // dead end, so the quest is abandoned — through the same deferral owner
        // the quest runtime's own give-up verdict uses — and this wake denies
        // with the reason, so the lower band takes over instead of the same wake
        // repeating.
        if (OwnLegFailure(context, characterId, deferred) is { } giveUpReason)
            return BotActivityDecision.Deny(giveUpReason);
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
            && (keptWork == null || keptWork.Leg != "hunt" || keptHunted || IsKnownHuntSearch(keptWork)))
        {
            _discoveryGeneration.TryRemove(characterId, out _);
            return HoldQuest(context, actor, deferred, kept);
        }
        if (kept != 0 && !IsCompleted(characterId, kept) && listing.QuestOffersUnknown)
        {
            _discoveryGeneration.TryRemove(characterId, out _);
            return HoldQuest(context, actor, deferred, kept);
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
            // Unknown offers are exactly what the discovery leg exists for, so
            // this is the one place the flag is answered with a walk instead of
            // a hold. Holding for an authoritative scan starves a quest-less
            // bot on any deployment whose presence patrol keeps a live Move
            // across the wake boundary: the arbitration runs before the step,
            // so it sees that patrol leg every wake, and the scan it is waiting
            // for never arrives. The discovery leg walks to an in-band offerer,
            // where the free actor finally scans; its own travel outranks the
            // patrol (ArmQuestTravel exempts the ROAM owner). The kept-purpose
            // hold above answers the same flag the same way for a bot that
            // already has a quest.
            if (listing.QuestOffersUnknown)
            {
                _chosenQuest.TryRemove(characterId, out _);
                _discoveryGeneration[characterId] = bot.Generation;
                return BotActivityDecision.Allow(DiscoveryActivityName);
            }
            // A deferred quest remains accepted on the Character, but is not
            // usable work for this owner until its source/progress changes.
            // It must not prevent the existing search for another quest.
            if (listing.ActiveWork.Any(work => !deferred.Quests.ContainsKey(work.QuestId))
                || listing.BlockedWork.Count != 0)
                return BotActivityDecision.Deny("opportunity listing names no actionable quest work");

            _chosenQuest.TryRemove(characterId, out _);
            _discoveryGeneration[characterId] = bot.Generation;
            return BotActivityDecision.Allow(DiscoveryActivityName);
        }
        _discoveryGeneration.TryRemove(characterId, out _);
        if (GetChosenQuest(characterId) != pick)
            Logger.Info("Quest purpose bot {CharacterId}: now quest {QuestId} (was {Previous})",
                characterId, pick, kept);
        _chosenQuest[characterId] = pick;
        return HoldQuest(context, actor, deferred, pick);
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
        // A quest abandoned after repeated leg failures is not re-picked while
        // this generation lasts: the deferral above carries the release rule for
        // accepted quests, this covers an offer that has no quest record to defer.
        if (_legAbandoned.TryGetValue(characterId, out var abandoned)
            && abandoned.Generation == deferred.Generation)
            blocked.UnionWith(abandoned.Quests);
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
            .Where(q => q.Leg != "hunt" || hunted.Contains(q.QuestId) || IsKnownHuntSearch(q))
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
    private static bool IsKnownHuntSearch(BotOpportunityListing.ActiveQuestWork work)
        => work.Leg == "hunt" && work.Reason.EndsWith("-search-location-known", StringComparison.Ordinal);

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
