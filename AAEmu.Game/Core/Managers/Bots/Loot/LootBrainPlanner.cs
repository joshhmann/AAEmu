#nullable enable

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>
/// The loot brain's LIVE ADAPTER: turns one wake's actor + pinned corpse into a
/// <see cref="LootBrainInputs"/>, runs <see cref="LootBrain.Decide"/>, and banks
/// the resulting terminal disposition into <see cref="LootLedger"/>.
///
/// The split is deliberate: <see cref="LootBrain"/> is pure and unit-testable
/// without a world, and every live read lives here — the corpse resolve, the
/// container probe, the engine's own tagging evidence, the wake census hostility
/// check, the worth read over the container's entries, and the bag's free slots.
/// No second perception runs: the safety census rides the wake's own
/// <c>ObservedContext.NearbyNpcObjIds</c>, exactly as the objective funnel's
/// candidate legality does.
///
/// Fail-closed contract: a corpse whose live resolution fails reads
/// <see cref="LootCorpseState.Unreadable"/> (never a fabricated dead corpse); an
/// unreadable container reads <see cref="LootReason.ContainerMissing"/>; an
/// unreachable census reads NOT safe; and an unreadable worth/space reads
/// <see cref="LootWorth.Unresolved"/>/<see cref="LootSpace.Unresolved"/>, which
/// PROCEED (those two arms may never fabricate a skip on an established corpse).
/// </summary>
public static class LootBrainPlanner
{
    /// <summary>
    /// The wake's live measurement, before the pure decision runs.
    /// </summary>
    public readonly record struct Prepared(
        LootBrainInputs Inputs,
        LootDecision Decision,
        LootClaimEvidence Evidence)
    {
        /// <summary>True when the corpse resolved to the dead shape this wake (the container tally is then meaningful).</summary>
        public bool CorpseResolved => Inputs.CorpseState == LootCorpseState.Dead;

        /// <summary>
        /// The ownership evidence and verdict as one space-free token for the lane:
        /// the tagger/team/contributor counts the classifier read, so a withhold
        /// on the ownership arm names WHY rather than only that.
        /// </summary>
        public string DescribeOwnership()
            => $"ownership={Inputs.Ownership}:tagger={Evidence.TaggerObjId}:tagTeam={Evidence.TagTeamId}" +
               $":teamResolved={(Evidence.TagTeamResolved ? "true" : "false")}" +
               $":contributors={Evidence.ContributorCount}:foreignContributors={Evidence.ForeignContributorCount}";
    }

    /// <summary>
    /// Everything the planner needs from the caller that is NOT a live world read:
    /// the pinned corpse identity, the caller's own gate readings (loot-once, the
    /// live auto-attack loop, the container's range), and the policy knobs.
    ///
    /// A value type carrying no engine type, so a unit test can drive the live
    /// adapter against a headless actor without constructing policy state.
    /// </summary>
    public readonly record struct Request(
        uint CorpseObjId,
        uint PreyTemplateId,
        long MinContainerValueCopper,
        bool AlreadyLooted,
        bool LoopLive,
        float SafeRadiusM,
        DateTime NowUtc);

    /// <summary>
    /// Builds the wake's inputs and evaluates the decision for the actor's PINNED
    /// quest corpse.
    ///
    /// <paramref name="request"/>.CorpseObjId 0 means the actor carries no pinned
    /// corpse at all: the inputs then read <see cref="LootCorpseState.Unreadable"/>
    /// and the chain withholds on the probe arm — never a fabricated corpse and
    /// never a fabricated skip.
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="observation">
    /// The wake's OWN observation snapshot (the candidate census the safety arm
    /// reads, plus the actor vitals the survival veto reads), or null when the
    /// caller cannot supply one — in which case the safety census reads unresolved
    /// and the chain withholds there rather than assuming safety. This is the one
    /// snapshot the wake already perceived: the planner never perceives again.
    /// </param>
    /// <param name="request">The caller's own gate readings and policy knobs.</param>
    public static Prepared Prepare(
        IGameplayActor actor,
        BotObservedContext? observation,
        in Request request)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var character = actor.Character;
        var corpseObjId = request.CorpseObjId;
        var preyTemplateId = request.PreyTemplateId;

        // The survival veto reads the SAME frozen observation the rest of the wake
        // reads (plus the brain's own published retreat fact inside it). A missing
        // observation cannot establish the veto, and the safety arm withholds on
        // the same missing snapshot anyway.
        var survivalVetoed = observation != null && QuestBehavior.IsSurvivalVetoed(actor.ActorId, observation);

        // ------------------------------------------------------------- corpse
        // THE ONE corpse-identity rule (<see cref="LootCorpseProbe"/>), shared with
        // the corpse-approach travel seam so both read the same recycle verdict.
        // Unreadable covers both "no corpse pinned at all" and "no actor frame to
        // resolve one against", so a caller cannot mistake an absent corpse for a
        // lootable one.
        var resolved = LootCorpseProbe.Resolve(character, corpseObjId, preyTemplateId);
        var corpseState = resolved.State;
        var corpse = resolved.Corpse;

        // ---------------------------------------------------------- container
        var (containerReadable, containerCount, containerInRange, worth) =
            ReadContainer(character, corpse, request.MinContainerValueCopper);

        // ------------------------------------------------------------ claims
        var evidence = ReadClaimEvidence(character, corpse, actor.ActorId);

        // ------------------------------------------------------------ safety
        var (safetyResolved, safe, nearestHostile, corpseDistance) =
            ReadSafety(character, observation, request.SafeRadiusM, corpseObjId);

        // ------------------------------------------------------------- space
        var space = ReadSpace(character);

        // ------------------------------------------------------------ ledger
        var (priorDisposition, priorCause) = LootLedger.Read(actor.ActorId, corpseObjId);

        var inputs = new LootBrainInputs(
            ActorObjId: actor.ActorId,
            CorpseObjId: corpseObjId,
            SurvivalVetoed: survivalVetoed,
            CorpseState: corpseState,
            Ownership: LootBrain.ClassifyOwnership(evidence, corpseIsOurPinnedCorpse: corpseObjId != 0),
            ContainerReadable: containerReadable,
            ContainerItemCount: containerCount,
            ContainerInRange: containerInRange,
            LoopLive: request.LoopLive,
            AlreadyLooted: request.AlreadyLooted,
            SafetyResolved: safetyResolved,
            Safe: safe,
            CorpseDistanceM: corpseDistance,
            NearestHostileDistanceM: nearestHostile,
            Worth: worth,
            Space: space,
            PriorDisposition: priorDisposition,
            PriorReason: priorCause,
            NowUtc: request.NowUtc);

        return new Prepared(inputs, LootBrain.Decide(inputs), evidence);
    }

    /// <summary>
    /// Banks a decided wake's TERMINAL SKIP. Called by the leg immediately after
    /// the decision, because a worth/space skip is final at decision time: the
    /// corpse is never reconsidered, so the bank can never lag an act.
    /// An undecided wake banks nothing — its causes can clear, and a take is
    /// banked by the DISPATCHER (only a Loot verb that actually ran its terminal
    /// course is a take).
    /// </summary>
    public static bool PublishSkip(in Prepared prepared, DateTime nowUtc)
        => prepared.Decision.Disposition == LootDisposition.Skipped
           && LootLedger.Bank(
               prepared.Inputs.ActorObjId, prepared.Inputs.CorpseObjId,
               LootDisposition.Skipped, prepared.Decision.Reason, nowUtc);

    /// <summary>
    /// Banks a TAKE for a corpse whose Loot verb ran its terminal course. Called
    /// by the dispatcher beside the quest leg's own loot-once memory, so the
    /// decision layer's bookkeeping and the funnel's withhold rule always describe
    /// the same fact.
    /// </summary>
    public static bool PublishTake(uint actorObjId, uint corpseObjId, DateTime nowUtc)
        => LootLedger.Bank(actorObjId, corpseObjId, LootDisposition.Take, LootReason.Emitted, nowUtc);

    // ------------------------------------------------------------- live reads

    /// <summary>
    /// The container probe: readable + non-empty entry count, and the WORTH
    /// verdict over every entry. An unreadable container reads
    /// <c>(false, 0, false, Unresolved)</c>; a resolved non-empty container is
    /// junk when EVERY entry <c>BotBagManager.IsTrash</c> classifies as vendor
    /// junk, or when the optional copper floor is set and the whole content is
    /// worth less than it. Never opens, mutates or transfers.
    /// </summary>
    private static (bool Readable, int Count, bool InRange, LootWorth Worth) ReadContainer(
        Character? character, Npc? corpse, long minContainerValueCopper)
    {
        if (character == null || corpse == null)
            return (false, 0, false, LootWorth.Unresolved);

        var owner = character.ParentWorld?.GetBaseUnit(corpse.ObjId);
        var container = owner?.LootingContainer;
        if (container == null)
            return (false, 0, false, LootWorth.Unresolved);

        var inRange = owner != null && MathUtil.CalculateDistance(
            character.Transform.World.Position, owner.Transform.World.Position, false)
            <= LootingContainer.MaxLootingRange;

        var count = container.Items.Count;
        if (count == 0)
            return (true, 0, inRange, LootWorth.Unresolved);

        // Worth is readable only when every entry's item resolved: one unreadable
        // row makes the whole verdict unresolved (never a fabricated "junk").
        long totalRefund = 0;
        var everyEntryJunk = true;
        foreach (var (_, entry) in container.Items)
        {
            var item = entry?.Item;
            if (item?.Template == null)
                return (true, count, inRange, LootWorth.Unresolved);
            if (item.Template.Refund > 0)
                totalRefund += (long)item.Template.Refund * item.Count;
            if (!BotBagManager.IsTrash(item))
                everyEntryJunk = false;
        }

        // Two INDEPENDENT skip criteria. The all-junk rule is the frozen
        // behaviour; the copper floor is opt-in (0 = off) and skips a container
        // whose whole content is worth less than the caller's bar REGARDLESS of
        // classification, so a caller can demand a minimum take value.
        var belowFloor = minContainerValueCopper > 0 && totalRefund < minContainerValueCopper;
        return (true, count, inRange,
            everyEntryJunk || belowFloor ? LootWorth.Junk : LootWorth.Worthwhile);
    }

    /// <summary>
    /// The engine's own loot-rights evidence for the corpse, read from the
    /// tagging surface (<c>Tagging.Tagger</c>/<c>Tagging.TagTeam</c>, resolved
    /// through <c>TeamManager</c>, plus <c>Tagging.GetAllContributors</c> for the
    /// damage census). Never a second attribution rule: every field is a live read
    /// of the engine's own memory, and an unreadable surface leaves the evidence
    /// empty (which the classifier resolves through the corpse's provenance).
    /// </summary>

    /// <summary>
    /// Cohort-membership resolver seam (tests only): when set, the fellow-bot
    /// fallback reads this instead of the provisioning service's DB lookup, so
    /// unit tests can name bot/bot and bot/stranger pairs without a login DB.
    /// Null (production default) means the live service answers.
    /// </summary>
    internal static Func<uint, bool>? CohortResolverForTests { get; set; }

    private static bool IsCohortBot(uint accountId)
    {
        var resolver = CohortResolverForTests;
        if (resolver != null)
        {
            try { return resolver(accountId); }
            catch { return false; }
        }
        return BotAccountProvisioningService.Instance.IsBotAccount(accountId);
    }

    internal static LootClaimEvidence ReadClaimEvidenceForCorpse(Character? character, Npc? corpse, uint actorObjId)
        => ReadClaimEvidence(character, corpse, actorObjId);

    private static LootClaimEvidence ReadClaimEvidence(Character? character, Npc? corpse, uint actorObjId)
    {
        if (character == null || corpse == null)
            return default;

        var tagging = corpse.CharacterTagging;
        if (tagging == null)
            return default;

        var tagger = tagging.Tagger;
        var tagTeamId = tagging.TagTeam;

        // The team reads are the engine's own registry lookups. A host where the
        // manager is not available cannot answer "whose claim is this", so the
        // evidence degrades to UNRESOLVED rather than to a fabricated absence —
        // which means a corpse carrying a tag-team claim withholds (fail-closed)
        // instead of being taken on an assumption.
        Models.Game.Team.Team? actorTeam = null;
        var actorInTagTeam = false;
        var tagTeamResolved = false;
        try
        {
            actorTeam = TeamManager.Instance.GetActiveTeamByUnit(character.Id);
            if (tagTeamId != 0)
            {
                var claimTeam = TeamManager.Instance.GetActiveTeam(tagTeamId);
                tagTeamResolved = claimTeam != null;
                actorInTagTeam = claimTeam != null && claimTeam.Members.Any(m =>
                    m?.Character != null && m.Character.Id == character.Id);
            }
        }
        catch
        {
            actorTeam = null;
            actorInTagTeam = false;
            tagTeamResolved = false;
        }

        var taggerSharesTeam = false;
        if (tagger != null && actorTeam != null)
            taggerSharesTeam = actorTeam.Members.Any(m => m?.Character != null && m.Character.Id == tagger.Id);
        if (!taggerSharesTeam && tagger != null && tagger.Id != character.Id)
        {
            // No engine party links the actor to the tagger (the kill21 shape:
            // four cooperating bots, never partied, so TagTeam stays 0 and the
            // claim reads Foreign). A fellow MANAGED bot is still the actor's
            // own cohort, not a stranger: both characters run on bot accounts
            // (BotAccountProvisioningService.IsBotAccount over AccountId), so
            // the claim resolves Party — the same verdict a real party yields
            // through the branch above. Fail-closed: any lookup failure leaves
            // the evidence untouched (Foreign), never a fabricated Party.
            try
            {
                taggerSharesTeam = IsCohortBot(character.AccountId) && IsCohortBot(tagger.AccountId);
            }
            catch
            {
                taggerSharesTeam = false;
            }
        }

        var contributorsReadable = false;
        var contributorCount = 0;
        var foreignContributors = 0;
        var actorIsContributor = false;
        try
        {
            var contributors = tagging.GetAllContributors(LootingContainer.MaxLootingRange);
            contributorsReadable = true;
            contributorCount = contributors.Count;
            foreach (var contributor in contributors)
            {
                if (contributor == null)
                    continue;
                if (contributor.Id == character.Id || contributor.ObjId == actorObjId)
                {
                    actorIsContributor = true;
                    continue;
                }
                if (actorTeam != null && actorTeam.Members.Any(m => m?.Character != null && m.Character.Id == contributor.Id))
                    continue;
                // Same cohort rule as the tagger branch: a fellow managed bot
                // is not a rival claim even with no engine party in place.
                try
                {
                    if (IsCohortBot(character.AccountId) && IsCohortBot(contributor.AccountId))
                        continue;
                }
                catch
                {
                }
                foreignContributors++;
            }
        }
        catch
        {
            contributorsReadable = false;
        }

        return new LootClaimEvidence(
            ActorObjId: actorObjId,
            TaggerObjId: tagger?.ObjId ?? 0,
            ActorIsTagger: tagger != null && (tagger.Id == character.Id || tagger.ObjId == actorObjId),
            TaggerSharesActorTeam: taggerSharesTeam,
            TagTeamId: tagTeamId,
            TagTeamResolved: tagTeamResolved,
            ActorInTagTeam: actorInTagTeam,
            ContributorsReadable: contributorsReadable,
            ContributorCount: contributorCount,
            ForeignContributorCount: foreignContributors,
            ActorIsContributor: actorIsContributor);
    }

    /// <summary>
    /// The safety census: must the actor STEP TOWARD the corpse through hostiles?
    /// The question the arm exists for is not "is a hostile near" but "does taking
    /// this corpse require walking into a fight" — taking a corpse is a zero-step,
    /// in-place verb (the take's own gate is <c>GameplayActor.Loot</c>'s own
    /// <c>LootingContainer.MaxLootingRange</c> check), so an actor already within
    /// reach of the corpse is never "walking into a fight".
    /// <para>
    /// The census therefore measures the CORPSE-RELATIVE approach corridor: the
    /// nearest hostile to the CORPSE, not to the actor. Candidates still come from
    /// the wake's OWN <c>ObservedContext.NearbyNpcObjIds</c> (never a world scan),
    /// each resolved live and checked with the combat layer's own hostility rule
    /// (<see cref="CombatDecisionTree.IsHostileTarget"/>).
    /// </para>
    /// <para>
    /// Fail-closed, unchanged: no observation at all reads UNRESOLVED (which
    /// withholds), and a candidate the world cannot resolve is counted as
    /// UNRESOLVED too — the census is a bounded perception, so an unreadable row
    /// is not proof of safety. An empty resolved census is resolved-and-safe.
    /// </para>
    /// </summary>
    internal static (bool Resolved, bool Safe, float NearestHostileM, float CorpseDistanceM) ReadSafety(
        Character? character, BotObservedContext? observation, float safeRadiusM,
        uint corpseObjId)
    {
        if (character == null || observation == null)
            return (false, false, float.NaN, float.NaN);

        var selfPosition = character.Transform.World.Position;

        // How far the actor still has to move for this take. A Loot verb is an
        // in-place action — the take's own gate is `GameplayActor.Loot`'s
        // `LootingContainer.MaxLootingRange` check, which is a REACH band, not a
        // contact band — so this is the distance the safety question is about:
        // zero means the corpse is already under the actor and there is no
        // approach left to refuse.
        // The corridor is measured FROM THE CORPSE, not from the actor: a hostile
        // crowding the corpse the actor is already standing on is beside the take,
        // not in the actor's way, and the actor-relative distance said nothing
        // about whether the corpse was reachable at all. An unresolvable pin keeps
        // the actor's own position as the anchor, so it can never fabricate safety.
        var anchor = selfPosition;
        var corpseDistance = float.NaN;
        if (corpseObjId != 0)
        {
            var corpse = character.ParentWorld?.GetNpc(corpseObjId);
            if (corpse != null)
            {
                anchor = corpse.Transform.World.Position;
                corpseDistance = MathUtil.CalculateDistance(selfPosition, anchor, false);
            }
        }

        var nearest = float.NaN;
        var unresolved = 0;
        foreach (var objId in observation.NearbyNpcObjIds)
        {
            if (objId == 0)
                continue;
            var npc = character.ParentWorld?.GetNpc(objId);
            if (npc == null)
            {
                unresolved++;
                continue;
            }
            if (!CombatDecisionTree.IsHostileTarget(character, npc))
                continue;
            var distance = MathUtil.CalculateDistance(anchor, npc.Transform.World.Position, false);
            if (float.IsNaN(nearest) || distance < nearest)
                nearest = distance;
        }

        if (unresolved > 0)
            return (false, false, nearest, corpseDistance);
        if (float.IsNaN(nearest))
            return (true, true, float.NaN, corpseDistance);
        return (true, nearest > safeRadiusM, nearest, corpseDistance);
    }

    /// <summary>
    /// The bag space read: <c>Bag.FreeSlotCount</c> (never a scan). An absent bag
    /// or an unlimited container (<c>ContainerSize &lt; 0</c>, whose free count the
    /// engine reports as a large constant) reads
    /// <see cref="LootSpace.Unresolved"/>, which PROCEEDS — a fabricated "full bag"
    /// on a character whose inventory could not be read would strand every drop.
    /// </summary>
    private static LootSpace ReadSpace(Character? character)
    {
        var bag = character?.Inventory?.Bag;
        if (bag == null)
            return LootSpace.Unresolved;
        if (bag.FreeSlotCount > 0)
            return LootSpace.Room;
        return LootSpace.Full;
    }
}
