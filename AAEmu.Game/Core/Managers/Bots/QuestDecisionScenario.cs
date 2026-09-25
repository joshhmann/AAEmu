using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest bootstrap decision scenario (copper-bootstrap leg): one quest action
/// per scheduler wake through the behavior runtime's hosted
/// <see cref="QuestBehavior"/> — advance, turn-in, discover, and accept
/// compete in a single shared <see cref="BotDecisionSelector"/> pass
/// (Finding D: accept is not split out).
///
/// This class is the recomposed shell: it keeps the public contract
/// (<see cref="ScenarioName"/>, <see cref="QuestOptions"/>,
/// <see cref="QuestRunResult"/>, <see cref="Run"/>) byte-identical for the
/// existing callers (roam executor quest leg, unit tests) while the whole
/// decision implementation lives in <see cref="QuestBehavior"/> under
/// <see cref="BotBehaviorRuntime"/> lifecycle. No semantic change.
/// </summary>
public static class QuestDecisionScenario
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "quest-bootstrap-decision";

    /// <summary>
    /// Scenario parameters. Defaults configure no discovery band, so a
    /// default run only advances active quests and never accepts (never throws).
    /// Constructed by the caller (band/cap defaults stay outside the behavior).
    /// </summary>
    public sealed record QuestOptions
    {
        /// <summary>Idempotency namespace for this run's legs.</summary>
        public string CycleId { get; init; } = "q1";

        /// <summary>Policy version stamped on every proposal.</summary>
        public string PolicyVersion { get; init; } = "quest-v1";

        /// <summary>Inclusive availability band for offering choice.</summary>
        public byte BandMin { get; init; } = 1;

        /// <summary>Inclusive availability band for offering choice.</summary>
        public byte BandMax { get; init; } = 9;

        /// <summary>How many nearby NPCs to sweep for offerings per wake.</summary>
        public int MaxDiscoverTargets { get; init; } = 3;

        // ---- fixed priorities (policy; personality weight stays 0) ----
        /// <summary>E2E-ONLY test override (default-off): when true, a selected
        /// quest turn-in proposal stays observable (eligibility + selection flow
        /// through the normal leg) but dispatch is withheld — the quest stays
        /// Ready/active and no turn-in lands. Production never sets this.</summary>
        public bool WithholdTurnIn { get; init; } = false;
        public int TurnInPriority { get; init; } = 30;
        /// <summary>G4 objective-target priority: above advance/accept (pursue
        /// the held 251 objective first), below turn-in (a Ready quest still
        /// reports first).</summary>
        public int ObjectiveTargetPriority { get; init; } = 25;
        /// <summary>G5 pursuit priority: just below the G4 objective-target
        /// selection (25) so assignment wins first, above advance (20) so a
        /// live pursuit leg outranks step-machine work. The G4 Target proposal
        /// yields via its target-unassigned precondition once assigned, which
        /// is when this proposal can win a wake.</summary>
        public int ObjectivePursuitPriority { get; init; } = 24;
        /// <summary>G6 combat priority: just below the G5 pursuit leg (24) so
        /// range-hold (Move/Stop) always wins while closing or holding, above
        /// advance (20) so a live combat leg outranks step-machine work. The
        /// pursuit Stop leg yields via hold-confirm withdrawal once settled,
        /// which is when this proposal can win a wake.</summary>
        public int ObjectiveCombatPriority { get; init; } = 23;
        /// <summary>G7c loot priority: just below the G6 combat leg (23) so a
        /// live combat decision always wins while the target is alive, above
        /// advance (20) so a lootable corpse is taken before step-machine
        /// work. Fires only on a recognized lootable corpse (post-death wakes
        /// withdraw pursuit/combat, so this is when the proposal can win).</summary>
        public int ObjectiveLootPriority { get; init; } = 22;
        /// <summary>G8b return priority: below the G5 pursuit leg (24), G6
        /// combat (23), and G7c loot (22) so live objective work always wins
        /// while closing/fighting/looting, above advance (20) so the return
        /// leg outranks step-machine work. Fires only on Ready 251 with a
        /// live 3512 reporter (Progress wakes withdraw it, so this is when
        /// the proposal can win). While live it owns the wake over the
        /// priority-30 TurnIn via wake-scoped arbitration (not priority) —
        /// the TurnIn proposal is withheld that wake and the withhold seam
        /// still guards any TurnIn that does get selected.</summary>
        public int ObjectiveReturnPriority { get; init; } = 21;
        public int AdvancePriority { get; init; } = 20;
        public int AcceptPriority { get; init; } = 10;
    }

    /// <summary>Structured run result — decision-path evidence attached.</summary>
    public sealed class QuestRunResult
    {
        public required string Scenario { get; init; }
        public bool WorkSelected { get; init; }
        /// <summary>The action the deterministic selector chose (null = run failed before selection).</summary>
        public ActorActionType? SelectedAction { get; init; }
        /// <summary>The dispatched request (null when nothing was dispatched).</summary>
        public ActorRequest? Request { get; init; }
        /// <summary>Why each non-selected candidate was refused (legality-before-preference evidence).</summary>
        public IReadOnlyList<BotProposalRejection> Rejections { get; init; } = [];
        public string Explanation { get; init; } = "";
        /// <summary>Quest ids completed by this leg (left ActiveQuests during the wake).</summary>
        public IReadOnlyList<uint> CompletedQuestIds { get; init; } = [];
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        /// <summary>
        /// Per-leg wake evidence, in plan/leg evaluation order: which leg ran,
        /// whether it emitted, and the leg's own named detail (its enter gate's
        /// skip reason when withheld, otherwise its validate/dispatch diag).
        /// </summary>
        public IReadOnlyList<LegEvidence> LegEvidence { get; init; } = [];
        /// <summary>
        /// Every plan that failed its fixture gate this wake, named with the
        /// plan's own stage and reason. Non-empty means those quests' legs were
        /// deliberately not driven — never a silent skip, whether or not the
        /// wake found other work.
        /// </summary>
        public IReadOnlyList<QuestPlanFailure> PlanFailures { get; init; } = [];
        /// <summary>The actor's full audit trace, in execution order.</summary>
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
    }

    /// <summary>
    /// Runs one quest-bootstrap decision cycle on a live actor (delegates to
    /// <see cref="QuestDirector.Run"/>, which assembles the wake's plans; the
    /// behavior's leg loop drives them. Contract unchanged).
    /// </summary>
    public static QuestRunResult Run(
        IGameplayActor actor,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs,
        QuestOptions? options = null)
        => QuestDirector.Run(actor, nearbyNpcs, options);
}
