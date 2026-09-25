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
/// The leash anchor is the PREY's own home captured at engage, not the bot's
/// position: the point of the leash arm is that the MOB is about to reset home,
/// which is a property of its drift from where it spawned. The engine itself
/// measures this against <c>NpcAi.IdlePosition</c> with the template's
/// <c>ReturnDistance</c> (default 50 m) — see <c>BaseCombatBehavior.ShouldReturn</c>.
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

        /// <summary>True when the incumbent has drifted past the leash edge — the mob is about to reset home.</summary>
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
