#nullable enable

using System.Numerics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots.Combat;

/// <summary>
/// The combat brain's LIVE ADAPTER: turns one wake's actor + observation into a
/// <see cref="CombatBrainInputs"/>, runs <see cref="CombatBrain.Decide"/>, and
/// publishes the resulting engagement state to
/// <see cref="CombatBrainEngagement"/>.
///
/// The split is deliberate: <see cref="CombatBrain"/> is pure and unit-testable
/// without a world, and every live read (the world resolve, the hostility gate,
/// the cooldown/mana/range query, the bag scan, the buff scan) lives here — the
/// same one place the existing quest legs already do their live revalidation.
/// No second perception runs: candidates come from the wake's own
/// <c>NearbyNpcObjIds</c>, exactly as <c>QuestObjectiveTargetSelector</c> does.
///
/// Fail-closed contract: a candidate whose live resolution fails is COUNTED and
/// DROPPED (never carried as a zeroed row); an unreadable ratio/distance is NaN;
/// and an unresolvable consumable yields no heal arm rather than a fabricated
/// item id.
/// </summary>
public static class CombatBrainPlanner
{
    /// <summary>Per-wake candidate enumeration bound (the same discipline the quest sweep uses).</summary>
    public const int CandidateBound = 32;

    /// <summary>The wake's live measurement, before the pure decision runs.</summary>
    public readonly record struct Prepared(
        CombatBrainInputs Inputs,
        uint CommittedQuestTarget,
        int CandidateCount,
        int UnresolvedCount,
        int RejectedCount);

    /// <summary>
    /// Builds the wake's inputs and evaluates the decision.
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="observation">
    /// The wake's own observation snapshot, or null when the caller already holds
    /// a committed quest target and must NOT run a second competition. The frozen
    /// quest rule is explicit — the funnel's selection IS the commitment and no
    /// second candidate competition runs — so a caller passing a non-zero
    /// committed target supplies no census and the brain acts on that target
    /// alone. A caller passing null AND 0 (a free-roam engagement) gets no
    /// candidates at all, which commits to nothing (fail-closed) rather than
    /// inventing a target.
    /// </param>
    /// <param name="committedQuestTarget">
    /// The quest leg's own committed target objId (0 when this wake is not on a
    /// quest objective path).
    /// </param>
    /// <param name="allowSkillCasts">False when the engine's skill surface is unavailable this wake (unit rigs without a template).</param>
    /// <param name="nowUtc">The wake's clock reading (the brain never reads a clock itself).</param>
    public static Prepared Prepare(
        IGameplayActor actor,
        BotObservedContext? observation,
        uint committedQuestTarget,
        bool allowSkillCasts,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var character = actor.Character;
        var selfPosition = character?.Transform.World.Position ?? Vector3.Zero;
        var role = character != null ? CombatDecisionTree.InferRole(character) : CombatRole.Melee;
        var selfHpRatio = character != null ? Ratio(character.Hp, character.MaxHp) : float.NaN;
        var alive = character == null || !character.IsDead;

        // ------------------------------------------------ incumbent
        var incumbentObjId = committedQuestTarget != 0
            ? committedQuestTarget
            : CombatBrainEngagement.TryGet(actor.ActorId, out var stored) ? stored.IncumbentObjId : 0u;
        if (incumbentObjId == 0 && observation is { CurrentTargetObjId: not 0 })
            incumbentObjId = observation.CurrentTargetObjId;

        var incumbentValid = false;
        var incumbentUnknown = false;
        var incumbentPosition = Vector3.Zero;
        var incumbentDistance = float.NaN;
        var incumbentDrift = float.NaN;
        var leashBudget = CombatBrainEngagement.DefaultLeashBudgetM;
        Npc? incumbentNpc = null;
        if (incumbentObjId != 0)
        {
            incumbentNpc = ResolveCombatUnit(character, incumbentObjId);
            if (incumbentNpc == null)
            {
                // The world cannot resolve the objId at all. Distinguish a GONE
                // target from an UNREADABLE frame using the wake's own census: an
                // objId the census carried but the world lost is provably gone,
                // while an objId the census did not carry is merely unread (the
                // census is a bounded perception, never proof of absence). With no
                // census at all (the quest path's commitment — never a scan) the
                // target is read as UNKNOWN, so a live engagement is never
                // dropped on a frame this wake simply did not census.
                var censusCarried = false;
                if (observation != null)
                {
                    foreach (var objId in observation.NearbyNpcObjIds)
                    {
                        if (objId == incumbentObjId)
                        {
                            censusCarried = true;
                            break;
                        }
                    }
                }
                incumbentUnknown = observation == null || !censusCarried;
                incumbentValid = false;
            }
            else
            {
                incumbentValid = incumbentNpc.Hp > 0;
                incumbentPosition = incumbentNpc.Transform.World.Position;
                if (incumbentValid)
                {
                    incumbentDistance = MathUtil.CalculateDistance(selfPosition, incumbentPosition, false);
                    leashBudget = LeashBudgetOf(incumbentNpc);
                    incumbentDrift = DriftFromAnchor(actor.ActorId, incumbentPosition);
                }
            }
        }

        // ------------------------------------------------ candidates
        var candidates = new List<CombatCandidate>(Math.Min(observation?.NearbyNpcObjIds.Count ?? 0, CandidateBound));
        var unresolved = 0;
        var rejected = 0;
        var enemyCount = 0;
        var nearestEnemy = float.NaN;

        if (committedQuestTarget != 0 && incumbentNpc != null && incumbentValid)
        {
            // The quest path: the funnel's selection IS the commitment and the
            // frozen rule forbids a second competition, so the candidate set is
            // exactly that one row.
            enemyCount = 1;
            nearestEnemy = incumbentDistance;
            candidates.Add(CandidateRow(actor.ActorId, observation, incumbentNpc, incumbentObjId,
                incumbentDistance, questRelevant: true));
        }
        else if (committedQuestTarget == 0 && observation != null)
        {
            foreach (var objId in observation.NearbyNpcObjIds)
            {
                if (candidates.Count >= CandidateBound)
                    break;
                var npc = ResolveCombatUnit(character, objId);
                if (npc == null)
                {
                    unresolved++;
                    continue;
                }
                if (npc.Hp <= 0)
                {
                    rejected++;
                    continue;
                }
                if (character != null && !CombatDecisionTree.IsHostileTarget(character, npc))
                {
                    rejected++;
                    continue;
                }
                enemyCount++;
                var distance = MathUtil.CalculateDistance(selfPosition, npc.Transform.World.Position, false);
                if (float.IsNaN(nearestEnemy) || distance < nearestEnemy)
                    nearestEnemy = distance;
                candidates.Add(CandidateRow(actor.ActorId, observation, npc, objId, distance, questRelevant: false));
            }
        }

        // ------------------------------------------------ skills
        uint selectedSkill = 0;
        var skillMin = 0f;
        var skillMax = 0f;
        if (allowSkillCasts && character != null && incumbentNpc != null && incumbentValid)
        {
            selectedSkill = CombatDecisionTree.SelectPrioritizedSkill(character, incumbentNpc, role, null, LastSkillOf(actor.ActorId));
            if (selectedSkill != 0)
            {
                var template = SkillTemplateOf(selectedSkill);
                if (template != null)
                {
                    (skillMin, skillMax) = CombatDecisionTree.GetSkillEffectiveRange(template, character);
                }
                else
                {
                    (skillMin, skillMax) = CombatDecisionTree.GetKnownSkillRange(selectedSkill);
                }
            }
        }

        // ------------------------------------------------ crowd control + escape
        var crowdControlled = character != null && IsCrowdControlled(character);
        var ccAvailable = character != null && incumbentValid && HasEscapeTool(character, incumbentNpc, incumbentDistance);

        var inputs = new CombatBrainInputs(
            ActorObjId: actor.ActorId,
            Role: role,
            Alive: alive,
            SelfHpRatio: selfHpRatio,
            SelfLevel: character?.Level ?? 0,
            SelfPosition: selfPosition,
            EnemyCount: enemyCount,
            NearestEnemyDistanceM: nearestEnemy,
            TookDamageThisFrame: false, // no damage-transition surface exists on this wake's snapshot
            CrowdControlled: crowdControlled,
            CcAvailable: ccAvailable,
            IsAutoAttackLive: character?.IsAutoAttack == true,
            HealItemTemplateId: SelectHealItem(character),
            SkillMinRangeM: skillMin,
            SkillMaxRangeM: skillMax,
            SelectedSkillId: selectedSkill,
            LastSkillUsed: LastSkillOf(actor.ActorId),
            IncumbentObjId: incumbentObjId,
            IncumbentValid: incumbentValid,
            IncumbentUnknown: incumbentUnknown,
            IncumbentScore: incumbentNpc != null && incumbentValid
                ? IncumbentScoreOf(actor.ActorId, observation, character, selfPosition, incumbentObjId, incumbentNpc)
                : 0,
            CommitmentInForce: IsCommitmentInForce(actor.ActorId, nowUtc),
            IncumbentPosition: incumbentPosition,
            IncumbentDistanceM: incumbentDistance,
            IncumbentLeashDriftM: incumbentDrift,
            LeashBudgetM: leashBudget,
            Candidates: candidates,
            NowUtc: nowUtc);

        return new Prepared(inputs, committedQuestTarget, candidates.Count, unresolved, rejected);
    }

    /// <summary>Builds one candidate row from a resolved live NPC.</summary>
    private static CombatCandidate CandidateRow(
        uint actorObjId, BotObservedContext? observation, Npc npc, uint objId, float distance, bool questRelevant)
        => new(
            objId,
            CombatTargetScorer.AttentionScoreOf(attackTarget: true, hostileInAggro: true),
            distance,
            npc.Template?.Level ?? 0,
            npc.MaxHp > 0 ? (float)npc.Hp / npc.MaxHp : float.NaN,
            QuestRelevant: questRelevant || observation?.CurrentTargetObjId == objId,
            DriftFromAnchor(actorObjId, npc.Transform.World.Position));

    /// <summary>
    /// Runs the decision for a prepared wake WITHOUT publishing. The caller
    /// publishes only what it actually dispatches, through
    /// <see cref="PublishDispatched"/> — a proposal that loses selection must not
    /// leave an engagement behind.
    /// </summary>
    public static CombatBrainDecision Decide(in Prepared prepared) => CombatBrain.Decide(prepared.Inputs);

    /// <summary>
    /// Publishes the engagement transition for a dispatched decision. Called by the
    /// leg's own dispatcher, so the facts (engaged / disengaging / survival veto)
    /// always describe a verb that actually landed.
    ///
    /// The caller re-prepares the wake's inputs (the dispatch happens after the
    /// proposal was selected, so a fresh measurement is the honest one); the pure
    /// overload below exists for callers that already hold the inputs they decided
    /// from.
    /// </summary>
    public static CombatBrainDecision PublishDispatched(
        IGameplayActor actor, in CombatBrainDecision decision, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var inputs = Prepare(actor, null, decision.TargetObjId, allowSkillCasts: false, nowUtc).Inputs;
        return Publish(decision, inputs, nowUtc);
    }

    /// <summary>
    /// Publishes an engagement transition from inputs the caller already holds —
    /// the pure seam (no actor, no world read), for a caller that decided from its
    /// own snapshot.
    /// </summary>
    public static CombatBrainDecision PublishDecision(
        in CombatBrainDecision decision, in CombatBrainInputs inputs, DateTime nowUtc)
        => Publish(decision, inputs, nowUtc);

    private static CombatBrainDecision Publish(
        in CombatBrainDecision decision, in CombatBrainInputs inputs, DateTime nowUtc)
    {
        if (decision.IsDisengaging)
        {
            if (CombatBrainEngagement.TryGet(inputs.ActorObjId, out _))
            {
                CombatBrainEngagement.PublishState(inputs.ActorObjId, inputs.CrowdControlled, true, nowUtc);
            }
            else
            {
                // The retreat began on a wake with no prior commitment (critical hp
                // on the engagement's first wake). It must still publish — the
                // Disengaging fact and the survival veto are what other legs read,
                // and withholding them here would leave the retreat invisible.
                CombatBrainEngagement.Publish(
                    inputs.ActorObjId, decision.TargetObjId, decision.TargetScore, inputs.IncumbentPosition,
                    inputs.LeashBudgetM, inputs.CrowdControlled, disengaging: true, nowUtc);
            }
            return decision;
        }

        if (decision.State == CombatBrainState.Vetoed)
        {
            CombatBrainEngagement.Clear(inputs.ActorObjId);
            return decision;
        }

        if (decision.TargetObjId == 0)
        {
            // Idle (nothing to commit to) — the engagement, if any, is over.
            if (decision.Arm is CombatArm.Commit or CombatArm.InvalidDrop)
                CombatBrainEngagement.Clear(inputs.ActorObjId);
            return decision;
        }

        if (decision.CommittedThisWake
            || !CombatBrainEngagement.TryGet(inputs.ActorObjId, out var current)
            || current.IncumbentObjId != decision.TargetObjId)
        {
            // A commitment that just started, the first one for this actor, or a
            // committed target that CHANGED (the quest funnel re-selected, or the
            // ranker displaced the incumbent) all re-anchor the engagement.
            var anchor = inputs.IncumbentPosition;
            CombatBrainEngagement.Publish(
                inputs.ActorObjId, decision.TargetObjId, decision.TargetScore, anchor,
                inputs.LeashBudgetM, inputs.CrowdControlled, disengaging: false, nowUtc);
        }
        else
        {
            CombatBrainEngagement.PublishState(inputs.ActorObjId, inputs.CrowdControlled, false, nowUtc);
        }

        return decision;
    }

    /// <summary>Records the skill a landed cast used, so the next wake's rotation continues the chain.</summary>
    public static void PublishSkillUsed(uint actorObjId, uint skillId, DateTime nowUtc)
        => CombatBrainEngagement.PublishSkillUsed(actorObjId, skillId, nowUtc);

    // ------------------------------------------------------------------ live reads

    /// <summary>
    /// Resolves a unit for combat: NPCs through the world's npc registry, and a
    /// character target through the unit registry. Never a region scan.
    /// </summary>
    private static Npc? ResolveCombatUnit(Character? character, uint objId)
    {
        if (character?.ParentWorld == null || objId == 0)
            return null;
        return character.ParentWorld.GetNpc(objId);
    }

    /// <summary>The prey's own leash budget from its template, or the engine default (50 m, <c>BaseCombatBehavior</c>).</summary>
    private static float LeashBudgetOf(Npc npc)
    {
        var fromTemplate = npc.Template?.ReturnDistance ?? 0f;
        return fromTemplate > 0f ? fromTemplate : CombatBrainEngagement.DefaultLeashBudgetM;
    }

    /// <summary>
    /// The prey's drift from the engagement's leash anchor. Before an anchor is
    /// published the drift reads 0 (there is nothing to be far from), which is
    /// the fail-closed direction: no fabricated "about to reset".
    /// </summary>
    private static float DriftFromAnchor(uint actorObjId, Vector3 targetPosition)
        => CombatBrainEngagement.TryGet(actorObjId, out var engagement)
           && engagement.LeashAnchor != Vector3.Zero
            ? MathUtil.CalculateDistance(engagement.LeashAnchor, targetPosition, false)
            : 0f;

    private static bool IsCommitmentInForce(uint actorObjId, DateTime nowUtc)
        => CombatBrainEngagement.TryGet(actorObjId, out var engagement)
           && engagement.IncumbentObjId != 0
           && engagement.AgeSeconds(nowUtc) < CombatTargetScorer.CommitmentWindow.TotalSeconds;

    private static uint LastSkillOf(uint actorObjId)
        => CombatBrainEngagement.TryGet(actorObjId, out var engagement) ? engagement.LastSkillUsed : 0u;

    /// <summary>
    /// The committed target's ranked score, so the hysteresis bar is computed
    /// against the SAME arithmetic the ranker uses (never a second scoring rule).
    /// </summary>
    private static int IncumbentScoreOf(
        uint actorObjId, BotObservedContext? observation, Character? character, Vector3 selfPosition,
        uint incumbentObjId, Npc incumbentNpc)
    {
        var row = new CombatCandidate(
            incumbentObjId,
            CombatTargetScorer.AttentionScoreOf(attackTarget: true, hostileInAggro: true),
            MathUtil.CalculateDistance(selfPosition, incumbentNpc.Transform.World.Position, false),
            incumbentNpc.Template?.Level ?? 0,
            incumbentNpc.MaxHp > 0 ? (float)incumbentNpc.Hp / incumbentNpc.MaxHp : float.NaN,
            QuestRelevant: observation?.CurrentTargetObjId == incumbentObjId,
            DriftFromAnchor(actorObjId, incumbentNpc.Transform.World.Position));
        return CombatTargetScorer.Score(row, character?.Level ?? 0, CombatBrain.AggroBandM);
    }

    /// <summary>
    /// Crowd control on the OBSERVER, using the engine's own buff conditions (the
    /// same predicate <c>BaseCombatBehavior</c> applies to NPCs): stun, sleep,
    /// root, knockdown, fastened. A silence blocks casts but not movement, so it
    /// is deliberately NOT part of this predicate — movement must stay legal.
    /// </summary>
    private static bool IsCrowdControlled(Character character)
        => character.Buffs != null
           && character.Buffs.HasEffectsMatchingCondition(e =>
               e.Template.Stun || e.Template.Sleep || e.Template.Root || e.Template.Knockdown || e.Template.Fastened);

    /// <summary>
    /// Whether a crowd-control / escape tool is ready against the committed
    /// target: one of the learned CC skills, in range and off cooldown through the
    /// engine's own gate. An empty learned set reads false (nothing to escape
    /// with), never true.
    /// </summary>
    private static bool HasEscapeTool(Character character, Npc? target, float distance)
    {
        if (target == null || character.Skills?.Skills == null || character.Skills.Skills.Count == 0
            || character.Buffs == null)
        {
            return false;
        }
        return CombatDecisionTree.IsSkillInRangeAndReady(character, target, CombatDecisionTree.DefenseShieldSlamSkillId, distance)
            || CombatDecisionTree.IsSkillInRangeAndReady(character, target, CombatDecisionTree.ShadowplayOverwhelmSkillId, distance)
            || CombatDecisionTree.IsSkillInRangeAndReady(character, target, CombatDecisionTree.BattlerageChargeSkillId, distance)
            || CombatDecisionTree.IsSkillInRangeAndReady(character, target, CombatDecisionTree.WitchcraftEarthenGripSkillId, distance)
            || CombatDecisionTree.IsSkillInRangeAndReady(character, target, CombatDecisionTree.SongcraftStartlingStrainSkillId, distance);
    }

    /// <summary>
    /// The health consumable the heal arm would use, or 0 when the bag carries
    /// none that the engine's item-use path can actually apply. The extra gate
    /// (a use skill whose template resolves) is deliberate: an item the engine
    /// would reject is not a heal, so naming it would fabricate a verb.
    /// </summary>
    private static uint SelectHealItem(Character? character)
    {
        if (character == null)
            return 0;
        var item = OutOfCombatRecoveryModule.SelectConsumableItem(character, ConsumablePreference.Health);
        var template = item?.Template;
        if (template == null || template.UseSkillId == 0)
            return 0;
        return SkillTemplateOf(template.UseSkillId) != null ? template.Id : 0u;
    }

    private static SkillTemplate? SkillTemplateOf(uint skillId)
        => skillId == 0 ? null : Core.Managers.SkillManager.Instance?.GetSkillTemplate(skillId);

    private static float Ratio(int value, int max)
        => max > 0 ? (float)value / max : float.NaN;
}
