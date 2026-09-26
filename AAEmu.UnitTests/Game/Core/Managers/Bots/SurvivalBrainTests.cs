using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// SurvivalBrain: the three-verdict chain (FLEE / HOLD / RECOVER), the
/// fail-closed unreadable-vitals hold, the out-of-combat low-hp recovery demand
/// that vetoes nothing, the single veto publisher, and the quest-leg consumer
/// that reads it.
///
/// These tests drive the PURE decision surface (<see cref="SurvivalBrain.Decide"/>)
/// from synthetic <see cref="SurvivalBrainInputs"/> — no world, no actor, no engine
/// — plus the published fact (<see cref="SurvivalVetoState"/>) and the one consumer
/// seam the quest legs already called (<c>QuestBehavior.IsSurvivalVetoed</c>). What
/// is pinned here is the contract a consumer observes: which verdict fires, which
/// named reason it reports, whether the veto stands, and where the flee leg is
/// sent.
/// </summary>
[NotInParallel]
public class SurvivalBrainTests
{
    private const uint ActorObjId = 4242;
    private const uint BoarObjId = 91_001;
    private static readonly Vector3 Self = new(100f, 100f, 10f);
    private static readonly Vector3 Boar = new(90f, 100f, 10f);

    [Before(Test)]
    public void Reset()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// A wake that reaches the plain healthy hold: readable full vitals, no fight
    /// on, no published retreat. Every row below starts here and flips exactly ONE
    /// verdict.
    /// </summary>
    private static SurvivalBrainInputs Healthy() => new(
        ActorObjId: ActorObjId,
        SelfHpRatio: 1.0f,
        CombatRetreatPublished: false,
        CommittedTargetObjId: 0,
        SelectedTargetObjId: 0,
        SelfPosition: Self,
        ThreatPosition: Vector3.Zero);

    // ------------------------------------------------------------ the three verdicts

    [Test]
    public async Task HealthyOutOfCombat_HoldsAndVetoesNothing()
    {
        var decision = SurvivalBrain.Decide(Healthy());

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.Healthy);
        await Assert.That(decision.Verb).IsEqualTo(SurvivalVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.Veto).IsFalse();
        await Assert.That(decision.FightEvidence).IsFalse();
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("healthy");
    }

    [Test]
    public async Task CriticalHpWithALiveCommitment_FleesWithTheSharedAnchor()
    {
        var inputs = Healthy() with
        {
            SelfHpRatio = 0.19f,
            CommittedTargetObjId = BoarObjId,
            ThreatPosition = Boar
        };

        var decision = SurvivalBrain.Decide(inputs);

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.HpCritical);
        await Assert.That(decision.Verb).IsEqualTo(SurvivalVerb.Move);
        await Assert.That(decision.HasVerb).IsTrue();
        await Assert.That(decision.IsFlee).IsTrue();
        await Assert.That(decision.Veto).IsTrue();
        await Assert.That(decision.ThreatObjId).IsEqualTo(BoarObjId);
        await Assert.That(decision.FightEvidence).IsTrue();

        // The destination is the ONE shared retreat anchor, step for step — not a
        // second copy of the escape ray.
        var expected = TravelBrain.SafeAnchor(Self, Boar, TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(decision.Destination).IsEqualTo(expected);
        await Assert.That(Vector3.Distance(Self, expected)).IsCloseTo(25f, 0.01f);
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("hp-critical");
    }

    [Test]
    public async Task CriticalHpWithASelectedTarget_Flees()
    {
        // A selection is fight evidence on its own: the quest path's committed
        // target is not necessarily published as an engagement yet.
        var decision = SurvivalBrain.Decide(Healthy() with
        {
            SelfHpRatio = 0.10f,
            SelectedTargetObjId = BoarObjId,
            ThreatPosition = Boar
        });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.HpCritical);
        await Assert.That(decision.FightEvidence).IsTrue();
    }

    [Test]
    public async Task WoundedOutOfCombat_RecoversWithoutAVetoOrAVerb()
    {
        var decision = SurvivalBrain.Decide(Healthy() with { SelfHpRatio = 0.40f });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Recover);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.OutOfCombatWounded);
        await Assert.That(decision.IsRecover).IsTrue();
        await Assert.That(decision.Verb).IsEqualTo(SurvivalVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.Veto).IsFalse();
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("out-of-combat-wounded");
    }

    [Test]
    public async Task AtTheRecoverLine_DoesNotRecover()
    {
        // The recover arm is STRICTLY below the existing recovery module's own
        // trigger, so the two agree at the boundary instead of fighting over it.
        var decision = SurvivalBrain.Decide(Healthy() with { SelfHpRatio = SurvivalBrain.RecoverHpThreshold });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.Healthy);
    }

    [Test]
    public async Task AHealthyFight_HoldsAndLeavesTheWakeWithTheCombatBrain()
    {
        var decision = SurvivalBrain.Decide(Healthy() with
        {
            SelfHpRatio = 0.80f,
            CommittedTargetObjId = BoarObjId,
            ThreatPosition = Boar
        });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.FightOwnsTheWake);
        await Assert.That(decision.Veto).IsFalse();
        await Assert.That(decision.FightEvidence).IsTrue();
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("fight-live");
    }

    // ------------------------------------------------------------ the lethal / retreat arms

    [Test]
    public async Task Incapacitated_HoldsButStillVetoes()
    {
        // Readable vitals at zero: the actor cannot act, so the survival condition
        // owns the wake — the ONE hold that still vetoes.
        var decision = SurvivalBrain.Decide(Healthy() with { SelfHpRatio = 0f });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.Incapacitated);
        await Assert.That(decision.Veto).IsTrue();
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("incapacitated");
    }

    [Test]
    public async Task PublishedCombatRetreat_FleesEvenWithHealthyVitals()
    {
        // The combat brain's own disengage (a leash edge, or a critical threat with
        // no escape tool) is authoritative on its own: this layer carries the fact
        // forward rather than re-deriving the combat cause.
        var decision = SurvivalBrain.Decide(Healthy() with
        {
            CombatRetreatPublished = true,
            CommittedTargetObjId = BoarObjId,
            ThreatPosition = Boar
        });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.CombatRetreat);
        await Assert.That(decision.Veto).IsTrue();
        await Assert.That(decision.Destination).IsNotNull();
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("combat-retreat");
    }

    [Test]
    public async Task Incapacitated_BeatsAPublishedRetreat()
    {
        // A down actor asks for no action; the retreat fact must not fabricate a
        // Move on a corpse.
        var decision = SurvivalBrain.Decide(Healthy() with
        {
            SelfHpRatio = 0f,
            CombatRetreatPublished = true,
            CommittedTargetObjId = BoarObjId
        });

        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.Incapacitated);
        await Assert.That(decision.HasVerb).IsFalse();
    }

    [Test]
    public async Task PublishedRetreat_NamesTheRetreatWhenCriticalHpIsAlsoTrue()
    {
        // Both arms want a flee. The published retreat is evaluated first and names
        // itself, so a lane can tell a combat-driven retreat from a raw hp emergency,
        // while the veto and the verb are identical either way.
        var decision = SurvivalBrain.Decide(Healthy() with
        {
            SelfHpRatio = 0.10f,
            CombatRetreatPublished = true,
            SelectedTargetObjId = BoarObjId,
            ThreatPosition = Boar
        });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.CombatRetreat);
        await Assert.That(decision.Veto).IsTrue();
    }

    // ------------------------------------------------------------ fail-closed directions

    [Test]
    public async Task UnreadableVitals_HoldsWithANamedReasonAndNoVeto()
    {
        // An unknown maximum reads NaN — never 0 (which would fabricate a veto)
        // and never 1 (which would pretend a comparison was made).
        var decision = SurvivalBrain.Decide(Healthy() with { SelfHpRatio = float.NaN });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.VitalsUnreadable);
        await Assert.That(decision.Veto).IsFalse();
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(float.IsNaN(decision.HpRatio)).IsTrue();
        await Assert.That(SurvivalBrain.Token(decision.Reason)).IsEqualTo("vitals-unreadable");
    }

    [Test]
    public async Task UnreadableVitals_AreNamedRatherThanAFabricatedHealth()
    {
        // The distinction that matters: an unreadable frame is NOT "healthy" — a
        // consumer reading the lane can tell the two apart.
        var unreadable = SurvivalBrain.Decide(Healthy() with { SelfHpRatio = float.NaN });
        var healthy = SurvivalBrain.Decide(Healthy());

        await Assert.That(unreadable.Reason).IsNotEqualTo(healthy.Reason);
        await Assert.That(unreadable.Veto).IsEqualTo(healthy.Veto);
    }

    [Test]
    public async Task OutOfCombatLowHp_IsARecoveryDemand_NotAVeto()
    {
        // THE GATE: a low bar with no fight on is a recovery concern for the
        // existing recovery module — never a veto over the quest legs.
        var inputs = Healthy() with { SelfHpRatio = 0.05f };
        var decision = SurvivalBrain.Decide(inputs);

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Recover);
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.OutOfCombatCritical);
        await Assert.That(decision.Veto).IsFalse();
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.FightEvidence).IsFalse();

        // The same bar WITH a selection is the flee — the gate is the fight
        // evidence, not the number.
        var engaged = SurvivalBrain.Decide(inputs with { SelectedTargetObjId = BoarObjId, ThreatPosition = Boar });
        await Assert.That(engaged.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(engaged.Veto).IsTrue();
    }

    [Test]
    public async Task AnUnreadThreatPosition_StillYieldsAUsableFleeDestination()
    {
        // The threat could not be resolved: the shared anchor's own fail-closed
        // path still defines an escape, so the Move verb never sees a NaN.
        var decision = SurvivalBrain.Decide(Healthy() with
        {
            SelfHpRatio = 0.15f,
            SelectedTargetObjId = BoarObjId,
            ThreatPosition = Vector3.Zero
        });

        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(decision.Destination).IsNotNull();
        var destination = decision.Destination!.Value;
        await Assert.That(float.IsFinite(destination.X)).IsTrue();
        await Assert.That(float.IsFinite(destination.Y)).IsTrue();
        await Assert.That(float.IsFinite(destination.Z)).IsTrue();
        await Assert.That(Vector3.Distance(Self, destination)).IsCloseTo(25f, 0.01f);
    }

    // ------------------------------------------------------------ the veto publisher + consumer

    [Test]
    public async Task Publisher_RecordsTheFact_AndAnUnpublishedActorIsNotVetoed()
    {
        // Fail-closed: no published fact reads NOT vetoed, so a consumer can never
        // inherit a veto nobody published.
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsFalse();
        await Assert.That(SurvivalVetoState.TryGet(ActorObjId, out _)).IsFalse();

        var vetoed = SurvivalVetoState.Publish(
            ActorObjId, Healthy() with { SelfHpRatio = 0.1f, CommittedTargetObjId = BoarObjId }, out var decision);

        await Assert.That(vetoed).IsTrue();
        await Assert.That(decision.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsTrue();
        await Assert.That(SurvivalVetoState.TryGet(ActorObjId, out var record)).IsTrue();
        await Assert.That(record.Verdict).IsEqualTo(SurvivalVerdict.Flee);
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.HpCritical);
        await Assert.That(record.HpRatio).IsEqualTo(0.1f);
    }

    [Test]
    public async Task Publisher_RepublishOnAClearedCause_ClearsTheVeto()
    {
        // The fact is recomputed per wake: a cause that clears must not leave a
        // stale veto standing over the quest legs.
        SurvivalVetoState.Publish(ActorObjId, Healthy() with { SelfHpRatio = 0.1f, CommittedTargetObjId = BoarObjId }, out _);
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsTrue();

        SurvivalVetoState.Publish(ActorObjId, Healthy(), out _);
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsFalse();
        await Assert.That(SurvivalVetoState.TryGet(ActorObjId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.Healthy);
    }

    [Test]
    public async Task Publisher_IgnoresAZeroActor()
    {
        SurvivalVetoState.Publish(0, Healthy() with { SelfHpRatio = 0f }, out _);
        await Assert.That(SurvivalVetoState.Count).IsEqualTo(0);
    }

    [Test]
    public async Task QuestLegConsumer_ReadsThePublishedVeto()
    {
        // The quest legs' own precondition seam: it publishes what it evaluated
        // and reports the same fact the publisher holds, so the funnel's withhold
        // and the lane's recorded verdict are one fact.
        var healthy = new BotObservedContext { ActorId = ActorObjId, Hp = 100, MaxHp = 100 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, healthy)).IsFalse();
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsFalse();

        // A down actor: the consumer vetoes AND the publisher now holds that fact.
        var down = new BotObservedContext { ActorId = ActorObjId, Hp = 0, MaxHp = 100 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, down)).IsTrue();
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsTrue();

        // Critical hp WITH a selected target is a fight emergency; the same hp
        // with no target is a recovery concern, not this veto.
        var criticalEngaged = new BotObservedContext
        {
            ActorId = ActorObjId, Hp = 15, MaxHp = 100, CurrentTargetObjId = BoarObjId
        };
        var criticalIdle = new BotObservedContext { ActorId = ActorObjId, Hp = 15, MaxHp = 100 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, criticalEngaged)).IsTrue();
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsTrue();
        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, criticalIdle)).IsFalse();
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsFalse();
    }

    [Test]
    public async Task QuestLegConsumer_UnreadableVitalsPublishANamedHoldAndVetoNothing()
    {
        var unreadable = new BotObservedContext { ActorId = ActorObjId, Hp = 0, MaxHp = 0 };

        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, unreadable)).IsFalse();
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsFalse();
        await Assert.That(SurvivalVetoState.TryGet(ActorObjId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.VitalsUnreadable);
    }

    [Test]
    public async Task QuestLegConsumer_HonoursTheCombatBrainsPublishedRetreat()
    {
        // The frozen behaviour, preserved: a combat retreat published by the
        // combat brain vetoes the quest legs regardless of the vitals snapshot.
        var healthy = new BotObservedContext { ActorId = ActorObjId, Hp = 100, MaxHp = 100 };
        CombatBrainEngagement.Publish(ActorObjId, BoarObjId, 100, Vector3.Zero, 50f,
            crowdControlled: false, disengaging: true, DateTime.UtcNow);

        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, healthy)).IsTrue();
        await Assert.That(SurvivalVetoState.TryGet(ActorObjId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.CombatRetreat);
    }

    [Test]
    public async Task LootPlannerVetoRead_AgreesWithTheQuestLegConsumer()
    {
        // The loot planner reads the same seam, so both consumers observe one rule.
        var criticalEngaged = new BotObservedContext
        {
            ActorId = ActorObjId, Hp = 15, MaxHp = 100, CurrentTargetObjId = BoarObjId
        };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(ActorObjId, criticalEngaged)).IsTrue();
        await Assert.That(SurvivalVetoState.IsVetoed(ActorObjId)).IsTrue();
    }

    // ------------------------------------------------------------ purity

    /// <summary>
    /// NO WORLD ACCESS: <see cref="SurvivalBrain"/> and <see cref="SurvivalVetoState"/>
    /// are the pure decision + publish surface, and their IL must not reference a
    /// single engine type — no world, no manager singleton, no Character/Npc/Item,
    /// and (per <c>PerceptionStackSurfaceTests</c>) nothing from the perception
    /// stack. Everything a wake needs arrives on <see cref="SurvivalBrainInputs"/>.
    ///
    /// <see cref="SurvivalBrainPlanner"/> is deliberately NOT covered — it is the
    /// live adapter and its live reads are its whole job.
    /// </summary>
    [Test]
    public async Task Decide_ReferencesNoEngineType()
    {
        var offenders = new List<string>();
        var scanned = 0;
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in (Type[])[typeof(SurvivalBrain), typeof(SurvivalVetoState)])
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetMethodBody() == null)
                    continue;
                scanned++;
                foreach (var referenced in ReferencedTypes(method))
                {
                    if (referenced == null)
                        continue;
                    if (IsEngineType(referenced))
                        offenders.Add($"{type.FullName}.{method.Name} → {referenced.FullName}");
                }
            }
        }

        await Assert.That(scanned).IsGreaterThan(4)
            .Because("the IL scan must actually walk the survival decision surface");
        await Assert.That(offenders).IsEmpty()
            .Because("the survival decision surface is pure: every live read belongs to SurvivalBrainPlanner — offenders: "
                     + string.Join("; ", offenders.Take(20)));
    }

    /// <summary>
    /// True when a type lives outside the pure survival surface (engine models,
    /// managers, or the perception/belief stack).
    ///
    /// The TRAVEL brain's own pure surface is exempt for exactly one reason: its
    /// <c>SafeAnchor</c> is the single shared implementation of the retreat step,
    /// which this brain's flee leg MUST call rather than copy (and which is itself
    /// held pure by <c>TravelBrainTests.Decide_ReferencesNoEngineType</c>). No other
    /// sibling brain is reachable from here — the combat layer's thresholds are
    /// const folds, which leave no IL reference at all.
    /// </summary>
    private static bool IsEngineType(Type type)
    {
        var name = type.FullName ?? "";
        if (name.StartsWith("AAEmu.Game.Models.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Core.Managers.", StringComparison.Ordinal)
            && !name.StartsWith("AAEmu.Game.Core.Managers.Bots.Survival.Survival", StringComparison.Ordinal)
            && !name.StartsWith("AAEmu.Game.Core.Managers.Bots.Travel.Travel", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.GameData.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Utils.", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static IEnumerable<Type?> ReferencedTypes(MethodBase method)
        => IlScan.ReferencedTypes(method);
}
