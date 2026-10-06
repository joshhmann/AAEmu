#nullable enable

using System.Collections.Concurrent;
using System.Numerics;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots.Combat;

/// <summary>
/// The combat brain's MEMORY: one engagement per actor — the committed target,
/// its ranked score, the commitment time (the hysteresis window's clock), the
/// leash anchor captured at engage, and the published lifecycle state other legs
/// read.
///
/// Ownership: this is the ONLY combat-side store. The brain's decision function
/// stays pure over <see cref="CombatBrainInputs"/>; everything stateful lands
/// here, keyed by the actor's objId, bounded by a full clear (the same discipline
/// <c>QuestBehavior</c>'s per-actor caches use — memory-only, no persistence, no
/// background sweep).
///
/// The two FACTS this layer exports to other legs — the combat-brain-engaged
/// yield fact and the survival-veto precondition — are read through
/// <see cref="ShouldYieldPursuit"/> and <see cref="IsSurvivalVetoed"/>. Both are
/// computed from the stored state, so a consumer can never observe a fact the
/// brain did not publish, and a cleared engagement reads as "not engaged".
///
/// The leash anchor is the PREY's own live position, re-read on every published
/// wake — never a coordinate frozen at the moment the fight started. Freezing it
/// made a mob that had merely wandered read as a full-budget strayer on every
/// later wake: the drift is measured between the anchor and the prey's CURRENT
/// position, so a stale anchor keeps a fight that never moves pinned at its
/// widest while the bot stands on the prey. Refreshing it is not widening the
/// budget — the budget still comes from the template's <c>ReturnDistance</c>,
/// the same measure the engine's own <c>BaseCombatBehavior.ShouldReturn</c> puts
/// against <c>NpcAi.IdlePosition</c>, re-anchored to <c>HomePosition</c> on
/// combat entry (<c>NpcAi.AnchorLeashOnCombatEntry</c>).
/// </summary>
public static class CombatBrainEngagement
{
    /// <summary>Engagement memory bound per process (a full clear, never a per-entry eviction policy).</summary>
    public const int MemoryBound = 256;

    /// <summary>The leash budget used when the prey's template does not carry one (engine default, <c>BaseCombatBehavior</c>).</summary>
    public const float DefaultLeashBudgetM = 50f;

    /// <summary>Fraction of the leash budget at which the engagement is treated as at the edge (the mob is about to reset).</summary>
    public const float LeashEdgeFraction = 0.9f;

    /// <summary>One live engagement.</summary>
    public readonly record struct Engagement(
        uint IncumbentObjId,
        int IncumbentScore,
        DateTime CommittedUtc,
        Vector3 LeashAnchor,
        float LeashBudgetM,
        uint LastSkillUsed,
        DateTime LastCastUtc,
        bool CrowdControlled,
        bool Disengaging,
        DateTime UpdatedUtc)
    {
        /// <summary>True once the engagement has been running past the hysteresis window.</summary>
        public double AgeSeconds(DateTime nowUtc) => (nowUtc - CommittedUtc).TotalSeconds;

        /// <summary>True when the incumbent has strayed past the leash edge since the anchor was last refreshed — the mob is about to reset home.</summary>
        public bool AtLeashEdge(Vector3 targetPosition)
        {
            if (LeashBudgetM <= 0f)
                return false;
            var drift = MathUtil.CalculateDistance(LeashAnchor, targetPosition, false);
            return drift >= LeashBudgetM * LeashEdgeFraction;
        }
    }

    private static readonly ConcurrentDictionary<uint, Engagement> Engagements = new();

    /// <summary>Publishes (or refreshes) an actor's engagement. Called by the planner after a commit.</summary>
    public static void Publish(uint actorObjId, uint incumbentObjId, int incumbentScore, Vector3 leashAnchor,
        float leashBudgetM, bool crowdControlled, bool disengaging, DateTime nowUtc)
    {
        if (actorObjId == 0)
            return;
        if (Engagements.Count >= MemoryBound)
            Engagements.Clear();
        var committed = nowUtc;
        var lastSkill = 0u;
        var lastCast = DateTime.MinValue;
        if (Engagements.TryGetValue(actorObjId, out var prior))
        {
            if (prior.IncumbentObjId == incumbentObjId)
                committed = prior.CommittedUtc; // the window follows the TARGET, not the refresh
            else
            {
                // A NEW target starts a new rotation: carrying the previous
                // engagement's last skill into a fresh target would make the
                // combo chains fire a follow-up that has no opener.
                lastSkill = 0u;
                lastCast = DateTime.MinValue;
            }
            if (prior.IncumbentObjId == incumbentObjId)
            {
                lastSkill = prior.LastSkillUsed;
                lastCast = prior.LastCastUtc;
            }
        }
        Engagements[actorObjId] = new Engagement(incumbentObjId, incumbentScore, committed, leashAnchor,
            leashBudgetM, lastSkill, lastCast, crowdControlled, disengaging, nowUtc);
    }

    /// <summary>Re-publishes the lifecycle flags without touching the commitment, its window, or the rotation.</summary>
    public static void PublishState(uint actorObjId, bool crowdControlled, bool disengaging, DateTime nowUtc)
    {
        if (actorObjId == 0)
            return;
        if (!Engagements.TryGetValue(actorObjId, out var current))
            return;
        Engagements[actorObjId] = current with
        {
            CrowdControlled = crowdControlled,
            Disengaging = disengaging,
            UpdatedUtc = nowUtc
        };
    }

    /// <summary>
    /// Records the skill a landed cast used, so the next wake's rotation continues
    /// the combo chain (the engine's own <c>lastSkillUsed</c> discipline). A
    /// non-zero skill also anchors a commitment if none was published — a landed
    /// cast is itself engagement evidence.
    /// </summary>
    public static void PublishSkillUsed(uint actorObjId, uint skillId, DateTime nowUtc)
    {
        if (actorObjId == 0 || skillId == 0)
            return;
        if (!Engagements.TryGetValue(actorObjId, out var current))
            return;
        Engagements[actorObjId] = current with
        {
            LastSkillUsed = skillId,
            LastCastUtc = nowUtc,
            UpdatedUtc = nowUtc
        };
    }

    /// <summary>
    /// Advances the leash anchor to where the committed prey stands NOW — the
    /// PER-WAKE half of "the anchor is the prey's live position" (the other half
    /// is the publish, which starts and re-points the engagement).
    ///
    /// This deliberately is not done in <see cref="Publish"/> alone. Publish is
    /// reached only from a DISPATCHED decision, so every wake the brain takes
    /// away — the disengage break, the survival veto, the crowd-control hold —
    /// returns before anything dispatches and Publish never runs. Leaving the
    /// advance there made the anchor freeze on exactly the wake that most needs
    /// it to move: one frame of drift past the edge, and the frozen anchor
    /// re-fired the leash on every later wake for the same prey, forever.
    ///
    /// The anchor is a MEASUREMENT frame, not a decision outcome. Drift means
    /// "how far the prey has covered since the last readable frame", so the
    /// frame advances whether or not this wake's proposal is dispatched — the
    /// same reason the prey's live position is read on every wake. Only the
    /// measurement moves: <see cref="Engagement.CommittedUtc"/>, the rotation
    /// and the lifecycle flags are carried verbatim, so an advance never
    /// restarts the commitment window and never clears a published veto.
    ///
    /// Fails closed: no engagement, no anchor, or a DIFFERENT committed target
    /// means there is nothing to advance — the anchor is then whatever the
    /// publish that starts or re-points that engagement banks.
    /// </summary>
    public static void Reanchor(uint actorObjId, uint incumbentObjId, Vector3 leashAnchor,
        float leashBudgetM, DateTime nowUtc)
    {
        if (actorObjId == 0 || incumbentObjId == 0 || leashAnchor == Vector3.Zero)
            return;
        if (!Engagements.TryGetValue(actorObjId, out var current))
            return;
        if (current.IncumbentObjId != incumbentObjId || current.LeashAnchor == Vector3.Zero)
            return;
        Engagements[actorObjId] = current with
        {
            LeashAnchor = leashAnchor,
            LeashBudgetM = leashBudgetM > 0f ? leashBudgetM : current.LeashBudgetM,
            UpdatedUtc = nowUtc
        };
    }

    /// <summary>Reads an actor's engagement, if one is published.</summary>
    public static bool TryGet(uint actorObjId, out Engagement engagement)
        => Engagements.TryGetValue(actorObjId, out engagement);

    /// <summary>Drops an actor's engagement (target provably gone, or the engagement ended).</summary>
    public static void Clear(uint actorObjId) => Engagements.TryRemove(actorObjId, out _);

    /// <summary>Clears every engagement (the world-reset / test seam, mirroring the other bot memory clears).</summary>
    public static void ClearAll() => Engagements.Clear();

    /// <summary>
    /// THE COMBAT-BRAIN-ENGAGED YIELD FACT. True while a committed engagement is
    /// live for this actor and the retreat leg does not own it — the fact the
    /// pursuit Stop leg reads to yield a settled wake, exactly as the G6
    /// hold-confirm withdrawal does.
    ///
    /// Fail-closed: no published engagement (or a cleared one) reads false, so a
    /// consumer can never yield to an engagement the brain did not publish.
    /// </summary>
    public static bool ShouldYieldPursuit(uint actorObjId)
        => Engagements.TryGetValue(actorObjId, out var engagement)
           && engagement.IncumbentObjId != 0
           && !engagement.Disengaging;

    /// <summary>
    /// THE SURVIVAL-VETO PRECONDITION. True while the actor is breaking contact,
    /// so a survival condition owns the wake and other legs must not act.
    ///
    /// Reads the published state only; the authoritative live veto is
    /// <see cref="CombatBrainInputs.SurvivalVetoed"/>, which the planner also
    /// evaluates directly.
    /// </summary>
    public static bool IsSurvivalVetoed(uint actorObjId)
        => Engagements.TryGetValue(actorObjId, out var engagement) && engagement.Disengaging;

    /// <summary>Test-only diagnostic: the number of published engagements.</summary>
    public static int Count => Engagements.Count;
}
