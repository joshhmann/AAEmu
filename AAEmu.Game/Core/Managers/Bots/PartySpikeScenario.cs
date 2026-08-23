using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M7 party spike (the last open M7 feature — the first multi-actor combat
/// consumer of the bridge seam): ONE real party (leader + 2 member bots)
/// takes down a single elite group encounter end-to-end through the M5
/// IGameplayActor contract ONLY:
///
///   PARTY-GATE (all N in one real TeamManager party, leader is owner, same
///   world) → RALLY (members MoveToUnit the leader, interleaved round-robin
///   Drive on ONE coordinator loop) → ENGAGE (leader SetTarget on the elite;
///   members assist by copying CurrentTarget) → COORDINATED HUNT (bounded
///   rounds, round-robin per member: OWN sustain check first — retreat +
///   heal potion + regen via the shared
///   <see cref="AdventurerSpikeScenario.RunSustainEpisode"/> primitive —
///   then standoff-band maintenance, then the burst-cast rotation on the
///   SHARED target) → kill inside the leash-reset window (a live elite
///   heals to full when its fight drags, so three attackers must down it
///   within the bounded rounds) → optional loot / fail-closed.
///
/// Encounter default: NPC template 1870 (level 13, grade Strong(7), ~3,500
/// maxHp, hostile faction 115, spawned in main_world) — a genuine GROUP
/// encounter precisely because of the leash-reset full-heal.
///
/// Multi-caster safety (engine-verified): each UseSkill news up its own
/// Skill instance (cooldowns/GCD are per-character), and Npc aggro splits
/// threat across attackers — so all three bots take hits and EACH runs its
/// own sustain loop.
///
/// World-agnostic (M3aM4ReplayScenario pattern): the SAME code drives the
/// unit rig and the live world — rigs inject fixture ids + a rig runtime
/// through <see cref="IPartySpikeRuntime"/>. The kill seam is explicit and
/// mirrors <see cref="AdventurerSpikeScenario.ISpikeRuntime.EnsureKillCredit"/>:
/// LIVE relies on real cast damage; RIG applies the documented synthetic
/// kill through the REAL QuestManager.DoOnMonsterHuntEvents entry point.
///
/// Failure classification uses the spec §17 taxonomy
/// (<see cref="ActorFailureReason"/>), never "bot got stuck". Trace: every
/// actor's audit records merged into ONE list in execution order.
/// </summary>
public static class PartySpikeScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Bridge scenario key (<see cref="BotDriveBridge"/> dispatch).</summary>
    public const string ScenarioName = "m7-party-spike";

    /// <summary>
    /// Default group-encounter target: npc template 1870 — level 13,
    /// grade Strong(7), est. ~3,500 maxHp, hostile faction 115 (compact.sqlite3).
    /// </summary>
    public const uint DefaultEliteNpcTemplateId = 1870u;

    /// <summary>Heal potions stocked per bot at provisioning (8518: 90 s cd — several per bot for retries).</summary>
    public const int DefaultHealPotionCount = 5;

    /// <summary>Party-spike parameters (live defaults = canonical ids; unit rigs inject fixture values).</summary>
    public sealed record PartySpikeOptions
    {
        /// <summary>The elite NPC template id to hunt (1870).</summary>
        public uint EliteNpcTemplateId { get; init; } = DefaultEliteNpcTemplateId;

        /// <summary>
        /// Skill ids in priority order per burst round — the same
        /// rotation-fallback rule as the solo spike (unlearned/cooldown
        /// refusals fall through to the next). Live default: the 18131-led
        /// Triple-Slash chain (BUG-016 fix regression coverage).
        /// </summary>
        public uint[] CastRotation { get; init; } =
            [AdventurerSpikeScenario.TripleSlashSkillId, AdventurerSpikeScenario.TripleSlashFinisherSkillId];

        /// <summary>
        /// Heal item used once per recovery round through the real UseItem
        /// contract path. Live default 8518 ("2단계 치유 물약" — fixed 2900
        /// heal, 90 s cd, req level 20; verified HealEffect row in canonical
        /// compact.sqlite3). A Rejected use (not bagged / on cooldown) is
        /// tolerated — out-of-combat regen is the documented fallback.
        /// </summary>
        public uint HealItemTemplateId { get; init; } = 8518;

        // ---- rally ----

        /// <summary>Distance at or below which a member holds formation around the leader.</summary>
        public float FollowDistance { get; init; } = 3f;

        public float MoveSpeed { get; init; } = 5f;

        public TimeSpan MoveTimeout { get; init; } = TimeSpan.FromSeconds(30);

        // ---- travel legs (close-in / sustain retreat) ----

        public float TravelSpeed { get; init; } = 6f;

        public TimeSpan TravelTimeout { get; init; } = TimeSpan.FromSeconds(90);

        // ---- engagement band ----

        /// <summary>Max distance (m) from which a member's rotation may start.</summary>
        public float EngageRange { get; init; } = 3f;

        /// <summary>Min comfortable distance (m) — 0 = melee close straight onto the elite.</summary>
        public float StandoffMin { get; init; } = 0f;

        // ---- sustain (per member) ----

        /// <summary>HP ratio below which a member disengages and recovers.</summary>
        public float SustainThreshold { get; init; } = 0.35f;

        /// <summary>HP ratio a recovering member returns at.</summary>
        public float ResumeThreshold { get; init; } = 0.8f;

        /// <summary>Sustain retreat leg length (m) along the threat→member vector.</summary>
        public float RetreatDistance { get; init; } = 10f;

        /// <summary>Bounded recovery rounds per episode before the run fails (Starvation).</summary>
        public int SustainMaxRounds { get; init; } = 30;

        // ---- coordinated hunt bounds ----

        /// <summary>
        /// The leash-reset window proxy: bounded coordinated rounds to down
        /// the elite. A live elite leash-resets to FULL HP when the fight
        /// drags — the whole party must burn it down inside this bound.
        /// </summary>
        public int MaxHuntRounds { get; init; } = 150;

        /// <summary>Max casts per member per round (repeat-while-alive burst chain).</summary>
        public int BurstCasts { get; init; } = 8;

        /// <summary>Loot the corpse after the kill. Default false: the elite
        /// carries no loot-table guarantee — the run records a rig note
        /// instead of gambling the verdict on an empty-corpse Rejected.</summary>
        public bool AttemptLoot { get; init; }
    }

    /// <summary>
    /// Runtime seam (M3aM4ReplayScenario.IScenarioPump pattern): how
    /// in-flight requests are driven, how the killing blow lands, and how
    /// recovery ticks. LIVE sleeps real time (the game loop applies cast
    /// damage + regen) and never fakes a kill; RIG ticks deterministically
    /// and applies the documented synthetic kill credit.
    /// </summary>
    public interface IPartySpikeRuntime
    {
        /// <summary>Advances an in-flight request until terminal or timeout.</summary>
        ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait);

        /// <summary>True when the elite is down (real death: DoDie ran / Hp drained).</summary>
        bool TargetDown(Npc target);

        /// <summary>
        /// Kill-credit seam. LIVE: real cast damage only. RIG: the documented
        /// synthetic kill through the REAL QuestManager.DoOnMonsterHuntEvents
        /// entry point (bare fixture NPCs carry no template/AI/spawner
        /// scaffolding for a full DoDie — same convention as the adventurer
        /// spike rig).
        /// </summary>
        bool EnsureKillCredit(GameplayActor actor, Npc target);

        /// <summary>One recovery beat during sustain (live sleep / rig regen fake).</summary>
        void RecoveryTick(Character character);
    }

    // ------------------------------------------------------------------ run

    /// <summary>Live entry (bridge dispatch): default options + the live runtime.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(IReadOnlyList<Character> party)
        => Run(party, new LivePartySpikeRuntime(), new PartySpikeOptions());

    /// <summary>Live-runtime convenience overload with caller-supplied options.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(IReadOnlyList<Character> party, PartySpikeOptions options)
        => Run(party, new LivePartySpikeRuntime(), options);

    /// <summary>Testable core: inject the runtime + options (unit rigs pass fixture values).</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(
        IReadOnlyList<Character> party, IPartySpikeRuntime runtime, PartySpikeOptions options)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        foreach (var character in party)
            ArgumentNullException.ThrowIfNull(character);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var rigNotes = new List<string>();
        var traceRecords = new List<ActorAuditRecord>();
        // One GameplayActor per party character; audit records merge into
        // traceRecords in execution order as actions drain.
        var actors = party.Select(c => new GameplayActor(c)).ToArray();
        var merger = new TraceMerger(actors, traceRecords);

        try
        {
            // ------------------------------------------------- PARTY-GATE
            // Party truth comes from the engine registry (TeamManager), not
            // Character.InParty alone — same discipline as the follow/assist
            // scenario, generalized to N members.
            if (party.Count < 2 || party.Count > 5)
                return Fail("PARTY-GATE", ActorFailureReason.WrongDecision,
                    $"party size {party.Count} outside the engine's 2..5 party range",
                    rigNotes, stages, criteria, traceRecords);

            var leader = party[0];
            var leaderTeam = TeamManager.Instance.GetActiveTeamByUnit(leader.Id);
            if (leaderTeam == null || !leaderTeam.IsParty || leaderTeam.OwnerId != leader.Id)
                return Fail("PARTY-GATE", ActorFailureReason.StateTransition,
                    $"leader {leader.Name} is not the owner of an active party " +
                    $"(team {leaderTeam?.Id.ToString() ?? "<null>"}, owner {leaderTeam?.OwnerId.ToString() ?? "<null>"})",
                    rigNotes, stages, criteria, traceRecords);

            for (var i = 1; i < party.Count; i++)
            {
                var member = party[i];
                var memberTeam = TeamManager.Instance.GetActiveTeamByUnit(member.Id);
                if (memberTeam == null || memberTeam.Id != leaderTeam.Id || !memberTeam.IsMember(member.Id))
                    return Fail("PARTY-GATE", ActorFailureReason.StateTransition,
                        $"member {member.Name} is not an active member of team {leaderTeam.Id}",
                        rigNotes, stages, criteria, traceRecords);
                if (leader.ParentWorld == null || !ReferenceEquals(leader.ParentWorld, member.ParentWorld))
                    return Fail("PARTY-GATE", ActorFailureReason.FidelityError,
                        $"leader and member {member.Name} do not share a world instance",
                        rigNotes, stages, criteria, traceRecords);
            }

            if (options.FollowDistance < 0f || options.MoveSpeed <= 0f || options.MoveTimeout <= TimeSpan.Zero ||
                options.TravelSpeed <= 0f || options.TravelTimeout <= TimeSpan.Zero ||
                options.MaxHuntRounds <= 0 || options.BurstCasts <= 0)
                return Fail("RIG", ActorFailureReason.WrongDecision,
                    "distances must be non-negative; speeds, timeouts, hunt/burst bounds must be positive",
                    rigNotes, stages, criteria, traceRecords);

            // ------------------------------------------------------ RALLY
            // Members close formation onto the leader — ALL rally legs are
            // created up front and driven INTERLEAVED (round-robin Tick on
            // this one coordinator thread), never one blocking pump per
            // actor. Same execution context the solo spike's runtime drives
            // casts from (the bridge thread; unpinned boundary).
            var rallyLegs = new List<(GameplayActor Actor, Character Member, ActorRequest Request)>();
            for (var i = 1; i < party.Count; i++)
            {
                var member = party[i];
                var distanceBefore = MathUtil.CalculateDistance(
                    member.Transform.World.Position, leader.Transform.World.Position, true);
                if (distanceBefore > options.FollowDistance)
                {
                    var request = actors[i].MoveToUnit(leader.ObjId, options.MoveSpeed, options.MoveTimeout);
                    merger.Collect();
                    rallyLegs.Add((actors[i], member, request));
                }
                else
                {
                    stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                        "RALLY-HOLD", 0, "Completed", leader.ObjId.ToString(),
                        $"{member.Name} already within follow distance ({distanceBefore:0.###})"));
                }
            }

            DriveAllInterleaved(runtime, rallyLegs.Select(l => (l.Actor, l.Request)), merger);
            foreach (var (_, member, request) in rallyLegs)
            {
                stages.Add(Stage("RALLY", leader.ObjId, request) with
                {
                    StepObserved = member.ObjId.ToString(),
                    StatusObserved = $"{request.State} [{member.Name} → leader]"
                });
                merger.Collect();
                if (request.State != ActorLifecycleState.Completed)
                    return Fail("RALLY", request.Failure ?? ActorFailureReason.Navigation,
                        $"rally leg of {member.Name} not completed: {request.State} ({request.Detail ?? "no detail"})",
                        rigNotes, stages, criteria, traceRecords);
                var distanceAfter = MathUtil.CalculateDistance(
                    member.Transform.World.Position, leader.Transform.World.Position, true);
                if (distanceAfter > options.FollowDistance)
                    return Fail("RALLY", ActorFailureReason.Navigation,
                        $"{member.Name} stopped {distanceAfter:0.###} from leader (threshold {options.FollowDistance:0.###})",
                        rigNotes, stages, criteria, traceRecords);
            }

            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "all-members-rallied", true,
                $"{party.Count - 1}/{party.Count - 1} members within {options.FollowDistance:0.###} m of the leader"));

            // ----------------------------------------------------- ENGAGE
            // Leader targets the elite; every member ASSISTS by copying
            // CurrentTarget (the exact follow/assist contract composition).
            var target = FindNearestAliveElite(leader, options.EliteNpcTemplateId);
            if (target == null)
                return Fail("ENGAGE", ActorFailureReason.WrongDecision,
                    $"elite npc template {options.EliteNpcTemplateId} not found alive/attackable in the shared world",
                    rigNotes, stages, criteria, traceRecords);

            var engage = actors[0].SetTarget(target.ObjId);
            merger.Collect();
            stages.Add(Stage("ENGAGE", target.ObjId, engage));
            if (engage.State != ActorLifecycleState.Completed)
                return Fail("ENGAGE", engage.Failure ?? ActorFailureReason.RejectedAction,
                    $"leader SetTarget({target.ObjId}) failed: {engage.Detail}",
                    rigNotes, stages, criteria, traceRecords);

            for (var i = 1; i < party.Count; i++)
            {
                var assist = actors[i].SetTarget(target.ObjId);
                merger.Collect();
                stages.Add(Stage("ASSIST", target.ObjId, assist) with
                {
                    StepObserved = party[i].ObjId.ToString(),
                    StatusObserved = $"{assist.State} [{party[i].Name}]"
                });
                if (assist.State != ActorLifecycleState.Completed)
                    return Fail("ENGAGE", assist.Failure ?? ActorFailureReason.RejectedAction,
                        $"assist of {party[i].Name} failed: {assist.Detail}",
                        rigNotes, stages, criteria, traceRecords);
            }

            var allAssisting = party.All(c => c.CurrentTarget?.ObjId == target.ObjId);
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "all-members-assist-leader-target", allAssisting,
                $"shared target {target.ObjId}; member targets [{string.Join(", ", party.Select(c => c.CurrentTarget?.ObjId ?? 0))}]"));

            // -------------------------------------------- COORDINATED HUNT
            // Bounded rounds; round-robin PER MEMBER: own sustain FIRST,
            // then standoff-band maintenance, then the burst-cast rotation
            // on the SHARED target. Kill must land inside the round bound —
            // the leash-reset window proxy.
            var killed = false;
            var rounds = 0;
            while (!killed && rounds < options.MaxHuntRounds)
            {
                rounds++;
                if (runtime.TargetDown(target))
                    break;

                // ---- PHASE A: SUSTAIN SWEEP — every member's vitals check
                // runs BEFORE anyone swings this round: a member below the
                // threshold exits the damage rotation first (retreat from
                // the elite, recover via heal item + regen beat, re-engage
                // next round).
                for (var i = 0; i < party.Count; i++)
                {
                    var member = party[i];
                    if (member.MaxHp <= 0 || (float)member.Hp / member.MaxHp >= options.SustainThreshold)
                        continue;

                    var recovered = AdventurerSpikeScenario.RunSustainEpisode(
                        member, actors[i], target,
                        options.RetreatDistance, options.TravelSpeed, options.TravelTimeout,
                        options.HealItemTemplateId, options.ResumeThreshold, options.SustainMaxRounds,
                        request => runtime.Drive(actors[i], request, options.TravelTimeout),
                        () => runtime.RecoveryTick(member),
                        stages, traceRecords);
                    merger.Collect();
                    if (!recovered)
                        return Fail("SUSTAIN", ActorFailureReason.Starvation,
                            $"recovery exhausted for {member.Name}: {options.SustainMaxRounds} rounds without reaching the " +
                            $"resume threshold (hp {member.Hp}/{member.MaxHp})",
                            rigNotes, stages, criteria, traceRecords);
                }

                if (runtime.TargetDown(target))
                {
                    killed = true;
                    break;
                }

                // ---- PHASE B: COMBAT SWEEP — round-robin damage rotation on
                // the SHARED target: standoff-band maintenance, then the
                // burst-cast chain.
                for (var i = 0; i < party.Count && !killed; i++)
                {
                    var member = party[i];
                    var actor = actors[i];

                    if (runtime.TargetDown(target))
                    {
                        killed = true;
                        break;
                    }

                    // STANDOFF BAND — melee default closes straight onto the
                    // elite; ranged keeps [StandoffMin, EngageRange]. A failed
                    // leg just skips this member's cast round.
                    if (!MaintainBand(member, actor, target, runtime, options, stages, traceRecords, merger))
                        continue;

                    // BURST CHAIN on the SHARED target — every skill cast
                    // once per burst round (Rejected skipped + recorded); the
                    // chain breaks early when the elite drops.
                    var hpRoundStart = target.Hp;
                    var executedAny = false;
                    for (var burst = 0; burst < options.BurstCasts && !killed; burst++)
                    {
                        var castExecuted = false;
                        foreach (var skillId in options.CastRotation)
                        {
                            if (runtime.TargetDown(target))
                            {
                                killed = true;
                                break;
                            }
                            var hpBefore = target.Hp;
                            var cast = actor.Cast(skillId, target.ObjId);
                            merger.Collect();
                            var castBase = Stage("HUNT-CAST", target.ObjId, cast);
                            stages.Add(castBase with
                            {
                                StepObserved = member.ObjId.ToString(),
                                StatusObserved = $"{castBase.StatusObserved} [{member.Name} hp {member.Hp}/{member.MaxHp}, target hp {hpBefore}→{target.Hp}]"
                            });
                            if (cast.State != ActorLifecycleState.Rejected)
                                castExecuted = true;
                        }
                        if (!castExecuted)
                            break; // the whole rotation refused — next round
                        executedAny = true;
                        if (runtime.EnsureKillCredit(actor, target))
                            killed = true;
                    }

                    if (killed)
                        break;

                    // Leash-reset evidence: casts executed but the elite's HP
                    // ended the round AT or ABOVE where it started — the
                    // reset window is being missed; the detail feeds tuning.
                    if (executedAny && target.Hp >= hpRoundStart)
                        rigNotes.Add($"round {rounds}: {member.Name} executed casts with no net progress " +
                                     $"(elite hp pinned at {target.Hp}) — leash-reset window pressure");
                }
            }

            if (!killed)
                return Fail("HUNT", ActorFailureReason.Starvation,
                    $"elite {options.EliteNpcTemplateId} still alive after {rounds} coordinated rounds (bound {options.MaxHuntRounds}; " +
                    $"hp {target.Hp}/{target.MaxHp}) — the party missed the leash-reset window",
                    rigNotes, stages, criteria, traceRecords);

            stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                "HUNT-KILL", rounds, "credited", target.ObjId.ToString(),
                $"elite down in round {rounds}/{options.MaxHuntRounds} (party of {party.Count})"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "elite-killed-within-bounds", killed,
                $"elite {target.ObjId} (template {options.EliteNpcTemplateId}) down after {rounds} coordinated rounds (bound {options.MaxHuntRounds})"));

            // ------------------------------------------------------- LOOT
            // Optional: the elite carries no loot-table guarantee, so the
            // default run records a rig note instead of looting.
            if (options.AttemptLoot)
            {
                var loot = actors[0].Loot(target.ObjId);
                merger.Collect();
                stages.Add(Stage("LOOT", target.ObjId, loot));
                if (loot.State == ActorLifecycleState.Rejected)
                    rigNotes.Add($"corpse loot of {target.ObjId} Rejected (tolerated): {loot.Detail ?? "n/a"}");
            }
            else
            {
                rigNotes.Add($"corpse loot skipped (no loot-table guarantee for elite template {options.EliteNpcTemplateId})");
            }

            // ----------------------------------------------------- VERIFY
            var finalTeam = TeamManager.Instance.GetActiveTeamByUnit(leader.Id);
            var partyIntact = finalTeam != null && finalTeam.Id == leaderTeam.Id &&
                              finalTeam.OwnerId == leader.Id &&
                              party.All(c => finalTeam.IsMember(c.Id));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "party-intact-after-kill", partyIntact,
                finalTeam == null
                    ? "team dissolved during the run"
                    : $"team {finalTeam.Id}, owner {finalTeam.OwnerId}, members {party.Count(c => finalTeam.IsMember(c.Id))}/{party.Count}"));

            var lifecycleComplete = AssertTraceCompleteness(traceRecords, out var lifecycleDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", lifecycleComplete, lifecycleDetail));

            var passed = criteria.All(c => c.Passed);
            return new BotScenarioRunner.ScenarioRunResult
            {
                Template = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                Failure = passed ? null : ActorFailureReason.WrongDecision,
                FailReason = passed ? "" : string.Join("; ", criteria.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")),
                RigNotes = rigNotes,
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                ActorRequests = traceRecords.Count
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "party spike crashed");
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", rigNotes, stages, criteria, traceRecords);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Interleaved round-robin pump for concurrent move legs: ONE
    /// coordinator loop Ticks every non-terminal actor in rotation until
    /// all legs terminate or the budget expires. Never one blocking pump
    /// per actor.
    /// </summary>
    private static void DriveAllInterleaved(IPartySpikeRuntime runtime,
        IEnumerable<(GameplayActor Actor, ActorRequest Request)> legs, TraceMerger merger)
    {
        var pending = legs.Where(l => !l.Request.IsTerminal).ToList();
        if (pending.Count == 0)
            return;

        var maxWait = pending.Max(l => l.Request.Timeout ?? TimeSpan.FromSeconds(30));
        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        var tick = TimeSpan.FromMilliseconds(50);
        while (Environment.TickCount64 < deadline)
        {
            pending = pending.Where(l => !l.Request.IsTerminal).ToList();
            if (pending.Count == 0)
                break;
            foreach (var (actor, _) in pending)
                actor.Tick(tick);
            merger.Collect();
            Thread.Sleep(5); // rig-friendly pacing; live legs stay inside their real budgets
        }
    }

    /// <summary>One standoff-band check for one member before casting.</summary>
    private static bool MaintainBand(Character member, GameplayActor actor, Npc target,
        IPartySpikeRuntime runtime, PartySpikeOptions options,
        List<BotScenarioRunner.ScenarioStageVerdict> stages, List<ActorAuditRecord> traceRecords,
        TraceMerger merger)
    {
        var memberPos = member.Transform.World.Position;
        var targetPos = target.Transform.World.Position;
        var distance = Vector3.Distance(memberPos, targetPos);

        if (options.StandoffMin > 0f && distance < options.StandoffMin)
        {
            var away = memberPos - targetPos;
            if (away.LengthSquared() < 0.01f)
                away = new Vector3(1, 0, 0);
            var stopRange = Math.Max(options.StandoffMin, options.EngageRange - 0.5f);
            var backOff = actor.MoveTo(targetPos + Vector3.Normalize(away) * stopRange,
                options.TravelSpeed, options.TravelTimeout);
            merger.Collect();
            backOff = runtime.Drive(actor, backOff, options.TravelTimeout);
            merger.Collect();
            stages.Add(Stage("HUNT-RANGE-BACK", target.ObjId, backOff) with
            {
                StepObserved = member.ObjId.ToString(),
                StatusObserved = $"{backOff.State} [{member.Name} dist {distance:F1} < standoff {options.StandoffMin:F1}]"
            });
            return backOff.State == ActorLifecycleState.Completed;
        }

        if (distance <= options.EngageRange)
            return true;

        var closeIn = options.StandoffMin > 0f
            ? actor.MoveTo(BandPoint(memberPos, targetPos, options), options.TravelSpeed, options.TravelTimeout)
            : actor.MoveToUnit(target.ObjId, options.TravelSpeed, options.TravelTimeout);
        merger.Collect();
        closeIn = runtime.Drive(actor, closeIn, options.TravelTimeout);
        merger.Collect();
        stages.Add(Stage("HUNT-CLOSE", target.ObjId, closeIn) with
        {
            StepObserved = member.ObjId.ToString(),
            StatusObserved = $"{closeIn.State} [{member.Name} dist {distance:F1} > engage {options.EngageRange:F1}]"
        });
        return closeIn.State == ActorLifecycleState.Completed;
    }

    /// <summary>The standoff-band destination: on the member↔target line, just inside the far band edge.</summary>
    private static Vector3 BandPoint(Vector3 memberPos, Vector3 targetPos, PartySpikeOptions options)
    {
        var away = memberPos - targetPos;
        if (away.LengthSquared() < 0.01f)
            away = new Vector3(1, 0, 0);
        var stopRange = Math.Max(options.StandoffMin, options.EngageRange - 0.5f);
        return targetPos + Vector3.Normalize(away) * stopRange;
    }

    /// <summary>
    /// Server-side world scan for the shared target: the nearest ALIVE NPC
    /// matching the elite template that the leader can attack
    /// (BaseUnit.CanAttack — faction-based; bare rig NPCs read attackable).
    /// </summary>
    private static Npc? FindNearestAliveElite(Character leader, uint templateId)
    {
        if (leader.ParentWorld == null)
            return null;
        Npc? best = null;
        var bestDistance = float.MaxValue;
        var position = leader.Transform.World.Position;
        foreach (var npc in leader.ParentWorld.GetAllNpcs())
        {
            if (npc.TemplateId != templateId || npc.Hp <= 0 || !leader.CanAttack(npc))
                continue;
            var distance = Vector3.DistanceSquared(position, npc.Transform.World.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = npc;
            }
        }
        return best;
    }

    /// <summary>Merges every actor's new audit records into ONE sink list in drain order.</summary>
    private sealed class TraceMerger
    {
        private readonly (GameplayActor Actor, int Consumed)[] _sources;
        private readonly List<ActorAuditRecord> _sink;

        public TraceMerger(IEnumerable<GameplayActor> actors, List<ActorAuditRecord> sink)
        {
            _sources = actors.Select(a => (a, 0)).ToArray();
            _sink = sink;
        }

        public void Collect()
        {
            for (var i = 0; i < _sources.Length; i++)
            {
                var (actor, consumed) = _sources[i];
                var trace = actor.AuditTrace;
                while (consumed < trace.Count)
                    _sink.Add(trace[consumed++]);
                _sources[i] = (actor, consumed);
            }
        }
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, uint target, ActorRequest request)
        => new(name, 1, request.State.ToString(), target.ToString(), request.Detail ?? "");

    /// <summary>
    /// Lifecycle correctness over the MERGED multi-actor trace: every
    /// Completed action carries Requested → Accepted → Completed; execution
    /// actions additionally carry Running. No Rejected record ever carries
    /// Running (refusals are pre-flight). Same law as the solo spike.
    /// </summary>
    private static bool AssertTraceCompleteness(List<ActorAuditRecord> records, out string detail)
    {
        var incomplete = records
            .Where(r => r.Result == ActorLifecycleState.Completed)
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")) ||
                        (r.Action != ActorActionType.Target && r.Action != ActorActionType.Observe &&
                         !r.StateChanges.Any(s => s.Contains("Running"))))
            .ToList();
        var rejectedRunning = records
            .Where(r => r.Result == ActorLifecycleState.Rejected && r.StateChanges.Any(s => s.Contains("Running")))
            .ToList();
        detail = $"records={records.Count} actors={records.Select(r => r.ActorId).Distinct().Count()} " +
                 $"completed={records.Count(r => r.Result == ActorLifecycleState.Completed)} " +
                 $"incomplete={incomplete.Count} rejected-with-running={rejectedRunning.Count}";
        return records.Count > 0 && incomplete.Count == 0 && rejectedRunning.Count == 0;
    }

    private static BotScenarioRunner.ScenarioRunResult Fail(
        string stage, ActorFailureReason? failure, string reason,
        List<string> rigNotes,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        List<ActorAuditRecord> traceRecords)
        => new()
        {
            Template = ScenarioName,
            Passed = false,
            FailStage = stage,
            Failure = failure,
            FailReason = reason,
            RigNotes = rigNotes,
            Gates = [],
            Stages = stages,
            Criteria = criteria,
            TraceRecords = traceRecords,
            ActorRequests = traceRecords.Count
        };
}

/// <summary>
/// LIVE world runtime (bridge dispatch path): in-flight Move legs advance
/// via actor ticks against real time — kills are REAL (the combined party
/// rotation's damage must down the elite through Npc.DoDie); nothing is
/// faked here. Same shape as <see cref="LiveSpikeRuntime"/>.
/// </summary>
public sealed class LivePartySpikeRuntime : PartySpikeScenario.IPartySpikeRuntime
{
    /// <summary>Pump cadence for movement legs.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
    {
        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        while (!request.IsTerminal && Environment.TickCount64 < deadline)
        {
            actor.Tick(TickInterval);
            Thread.Sleep(TickInterval);
        }
        return request;
    }

    public bool TargetDown(Npc target)
        => target.Hp <= 0 || target.DeadTime != DateTime.MinValue;

    public bool EnsureKillCredit(GameplayActor actor, Npc target)
    {
        // Real cast damage only — no synthetic credit live. Damage lands
        // ~200 ms after UseSkill returns (EffectDelay → ApplySkillTask on
        // the game loop), so poll briefly before reporting the elite as
        // still up; short on purpose — the leash-reset window punishes idle
        // waiting.
        var deadline = Environment.TickCount64 + 400;
        do
        {
            if (TargetDown(target))
                return true;
            Thread.Sleep(100);
        } while (Environment.TickCount64 < deadline);
        return false;
    }

    public void RecoveryTick(Character character)
    {
        // live: let the game loop tick (regen + potion effects apply).
        Thread.Sleep(500);
    }
}
