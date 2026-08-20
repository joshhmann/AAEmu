using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M7 gating spike (ROADMAP M7: "a scoped spike — one adventurer clearing a
/// short quest chain end-to-end — gates scheduling") — ONE adventurer bot
/// clears the Solzreed notice-board fox cull end-to-end through the M5
/// IGameplayActor contract ONLY:
///
///   accept (quest 250 at the notice-board doodad 5047) → travel (Move to
///   the hunting ground) → hunt loop (Observe → nearest attackable fox
///   (npc 3492) → SetTarget → Cast rotation) ×3 → Loot each corpse →
///   auto-complete (250 is an auto-report quest — no return leg)
///
/// Quest data verified against Docs/wiki/Golden-Route-Solzreed.md §1a (step
/// 1: quest 250, accept Doodad 5047, kill 3× fox npc 3492, auto turn-in,
/// 110 exp), the canonical compact.sqlite3 (quest_contexts 250, npcs 3492
/// "솔즈리드 여우" level 1 faction 115, doodad_almighties 5047 "푯말", skill
/// 18131 "3단 베기" — the Fight ability-1 start skill: Hostile target, 0
/// cast/cooldown, 4 m range) and the booted world spawn data
/// (main_world/npc_spawns.json: 10 fox spawners around
/// (15468-15594, 15212-15341); doodad_spawns.json: board 5047 at
/// (15522.954, 15285.898, 130.47)).
///
/// World-agnostic (M3aM4ReplayScenario pattern): the SAME code drives the
/// unit rig and the live world — the rig injects fixture ids + a rig
/// runtime through <see cref="ISpikeRuntime"/>. The kill seam is explicit:
/// the LIVE runtime relies on real cast damage (the fox dies through the
/// real Npc.DoDie → QuestManager.DoOnMonsterHuntEvents chain); the RIG
/// runtime applies the kill through the REAL
/// QuestManager.DoOnMonsterHuntEvents entry point directly (the exact call
/// Npc.DoDie makes for a character killer) because bare fixture NPCs carry
/// no template/AI/spawner scaffolding for a full DoDie — documented
/// rig-faked damage; real damage is the E2E's job.
///
/// Failure classification uses the spec §17 taxonomy (ActorFailureReason),
/// never "bot got stuck". Move is straight-line lerp (no pathfinding) —
/// travel legs are kept short and the choice documented per run. Bot
/// death/resurrection does not exist anywhere and is OUT OF SCOPE.
/// H stays UNKNOWN — scripted evidence is proxy/bot-functional only.
/// </summary>
public static class AdventurerSpikeScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key (registered in <see cref="BotScenarioTemplates"/>).</summary>
    public const string ScenarioName = "adventurer-spike-fox";

    // ---- Canonical quest-250 ids (compact.sqlite3 r208022 + golden route).
    public const uint FoxQuestId = 250;
    public const uint NoticeBoardDoodadTemplateId = 5047;
    public const uint FoxNpcTemplateId = 3492;
    public const int FoxKillCount = 3;

    /// <summary>
    /// 3단 베기 THIRD hit (Triple Slash combo finisher) — a Fight ability-1
    /// start skill a provisioned bot learns through
    /// CharacterManager.ApplyPlayerProgression. Chosen over the first hit
    /// (18131) because of a LIVE-VERIFIED engine gap: 18131 has
    /// target_area_radius=2 with TargetSelection=Target, and ApplyEffects'
    /// AoE branch never includes targetSelf (WorldManager.GetAround
    /// excludes the center object) — 150/150 successful casts dealt ZERO
    /// damage to the primary target (m7 spike diagnosis, 2026-08-20).
    /// 18134 has area_radius=0 → possibleTargets = [the fox]. Both stay in
    /// the rotation: 18134 lands the damage, 18131 records the quirk as
    /// trace evidence on the fallback path.
    /// </summary>
    public const uint TripleSlashFinisherSkillId = 18134;

    /// <summary>3단 베기 first hit (18131) — see the finisher note above.</summary>
    public const uint TripleSlashSkillId = 18131;

    /// <summary>Spike parameters (live defaults = canonical ids; unit rigs inject fixture values).</summary>
    public sealed record SpikeOptions
    {
        /// <summary>The kill quest to clear (250 — Solzreed fox cull).</summary>
        public uint QuestId { get; init; } = FoxQuestId;

        /// <summary>Acceptor kind (250 is a notice-board doodad accept).</summary>
        public QuestAcceptorType AcceptorType { get; init; } = QuestAcceptorType.Doodad;

        /// <summary>
        /// Acceptor doodad TEMPLATE id (5047) — the acceptor identity the
        /// quest's accept act matches (QuestActConAcceptDoodad semantics),
        /// also resolved through the world adapter for the approach leg.
        /// </summary>
        public uint AcceptorDoodadTemplateId { get; init; } = NoticeBoardDoodadTemplateId;

        /// <summary>Hunt-target NPC template id (3492 — Solzreed fox).</summary>
        public uint TargetNpcTemplateId { get; init; } = FoxNpcTemplateId;

        /// <summary>Kill objective count (250 needs 3 foxes).</summary>
        public int RequiredKills { get; init; } = FoxKillCount;

        /// <summary>
        /// Skill ids in priority order — the hunt loop casts the first one
        /// the engine does not Reject (unlearned/cooldown refusals fall
        /// through to the next). Live default: the Fight start skill.
        /// </summary>
        public uint[] CastRotation { get; init; } = [TripleSlashFinisherSkillId, TripleSlashSkillId];

        /// <summary>
        /// Quest 250's objectives are kills only — corpse loot is flavor.
        /// An empty-corpse Rejected loot is tolerated (recorded as evidence)
        /// when this is true; it fails the run when false.
        /// </summary>
        public bool LootOptional { get; init; } = true;

        /// <summary>
        /// Optional fixed hunting-ground destination (unit rigs: a short
        /// straight-line leg from the spawn). When null (live), the travel
        /// leg targets the resolved fox itself (MoveToUnit).
        /// </summary>
        public Vector3? HuntingGround { get; init; }

        /// <summary>Move-leg pace (m/s) and per-leg budget.</summary>
        public float TravelSpeed { get; init; } = 6f;
        public TimeSpan TravelTimeout { get; init; } = TimeSpan.FromSeconds(90);

        /// <summary>Overall bound on hunt iterations (select/cast rounds).</summary>
        public int MaxHuntAttempts { get; init; } = 24;

        /// <summary>Bounded re-observe/re-travel retries when no attackable target is visible.</summary>
        public int NoTargetRetries { get; init; } = 4;

        /// <summary>
        /// Max casts per hunt round on one target (repeat-while-alive burst).
        /// Live prey leash-resets to full HP when the fight drags (Npc
        /// return-to-idle heals), so the kill must land within the reset
        /// window — one cast per round is too slow. Rig runtimes exit the
        /// burst after the first cast (the synthetic kill applies at once).
        /// </summary>
        public int BurstCasts { get; init; } = 8;
    }

    /// <summary>
    /// World-adaptation seam (M3aM4ReplayScenario.IScenarioPump pattern):
    /// how in-flight requests are driven and how the killing blow lands.
    /// The LIVE runtime sleeps real time (the game loop applies cast
    /// damage) and never fakes a kill; the RIG runtime ticks the actor
    /// deterministically and applies the documented synthetic kill through
    /// the real DoOnMonsterHuntEvents credit path.
    /// </summary>
    public interface ISpikeRuntime
    {
        /// <summary>Advances an in-flight request until terminal or timeout.</summary>
        ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait);

        /// <summary>True when the target is down (real death: DoDie ran / Hp drained).</summary>
        bool TargetDown(Npc target);

        /// <summary>
        /// Kill-credit seam. LIVE: returns <see cref="TargetDown"/> — the
        /// cast rotation's real damage must have done it. RIG: applies the
        /// killing blow through the REAL
        /// QuestManager.DoOnMonsterHuntEvents entry (the exact call
        /// Npc.DoDie makes for a character killer — group/zone/kill-accept
        /// fanout included) and marks the fixture NPC down so the alive
        /// filter excludes it; documented rig-faked damage.
        /// </summary>
        bool EnsureKillCredit(GameplayActor actor, Npc target);

        /// <summary>
        /// Loot-fixture seam (unit rigs only): seeds the corpse's
        /// LootingContainer so the Loot contract action has something to
        /// grant. The LIVE runtime is a no-op (real DoDie generates loot).
        /// </summary>
        void PrepareLootCorpse(Npc corpse);
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Live entry (bridge dispatch): default options + the live runtime.
    /// The live hunt budget is larger than a rig's: real fox HP against a
    /// level-10 start-skill rotation takes multiple casts per kill, and
    /// cooldown rounds count against the same bound.
    /// </summary>
    public static BotScenarioRunner.ScenarioRunResult Run(Character character, BotScenarioRunner.IScenarioWorldAdapter world)
        => Run(character, world, new LiveSpikeRuntime(), new SpikeOptions { MaxHuntAttempts = 150 });

    /// <summary>Testable core: inject the runtime + options (unit rigs pass fixture ids).</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(
        Character character, BotScenarioRunner.IScenarioWorldAdapter world,
        ISpikeRuntime runtime, SpikeOptions options)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);

        var actor = new GameplayActor(character);
        var rigNotes = new List<string>();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        // Hunt-loop state (read across the run, asserted at VERIFY).
        var credited = new HashSet<uint>();
        var lootOutcomes = new List<string>();

        try
        {
            // ------------------------------------------------ 1. ACCEPT
            // Resolve the notice board and walk to it first (notice-board
            // fidelity: the accept act is the board's), then accept through
            // the real AddQuest gate (mother-faction kind-42/148 + level 1
            // are the real unit_reqs on quest 250's start component).
            var boardObjId = world.ResolveDoodadObjId(options.AcceptorDoodadTemplateId);
            if (boardObjId == 0)
                return Fail("ACCEPT", ActorFailureReason.WrongDecision,
                    $"acceptor doodad template {options.AcceptorDoodadTemplateId} unresolvable in scenario world",
                    rigNotes, stages, criteria, traceRecords);

            var board = character.ParentWorld?.GetDoodad(boardObjId);
            if (board != null)
            {
                var approach = actor.MoveTo(board.Transform.World.Position, options.TravelSpeed, options.TravelTimeout);
                Collect(actor, traceRecords);
                stages.Add(Stage("ACCEPT-APPROACH", boardObjId, approach));
                approach = runtime.Drive(actor, approach, options.TravelTimeout);
                Collect(actor, traceRecords);
                if (approach.State != ActorLifecycleState.Completed)
                    return Fail("ACCEPT", approach.Failure ?? ActorFailureReason.Navigation,
                        $"approach to notice board {boardObjId} not completed: {approach.State} ({approach.Detail ?? "n/a"})",
                        rigNotes, stages, criteria, traceRecords);
            }
            else
            {
                rigNotes.Add($"acceptor doodad objId {boardObjId} resolved but not readable as a Doodad — approach leg skipped");
            }

            var accept = actor.AcceptQuest(options.QuestId, options.AcceptorType, options.AcceptorDoodadTemplateId);
            Collect(actor, traceRecords);
            stages.Add(Stage("ACCEPT", options.QuestId, accept));
            if (accept.State != ActorLifecycleState.Completed)
                return Fail("ACCEPT", accept.Failure ?? ActorFailureReason.RejectedAction,
                    $"accept refused by engine gate: {accept.Detail}", rigNotes, stages, criteria, traceRecords);

            var postAccept = actor.Observe();
            Collect(actor, traceRecords);
            stages.Add(Stage("ACCEPT-OBSERVE", 0, actor.AuditTrace.Last()));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "quest-active-after-accept", postAccept.ActiveQuestIds.Contains(options.QuestId),
                $"active quests after accept: [{string.Join(", ", postAccept.ActiveQuestIds)}]"));

            // ------------------------------------------------ 2. TRAVEL
            // Straight-line lerp only (no pathfinding) — the leg is short
            // by construction (rig: a fixed nearby hunting ground; live:
            // the fox cluster sits 30-60 m from the board).
            ActorRequest travel;
            uint travelTargetId;
            if (options.HuntingGround is { } ground)
            {
                travelTargetId = 0;
                travel = actor.MoveTo(ground, options.TravelSpeed, options.TravelTimeout);
            }
            else
            {
                travelTargetId = world.ResolveNpcObjId(options.TargetNpcTemplateId);
                if (travelTargetId == 0)
                    return Fail("TRAVEL", ActorFailureReason.WrongDecision,
                        $"target npc template {options.TargetNpcTemplateId} unresolvable in scenario world (no hunting ground configured)",
                        rigNotes, stages, criteria, traceRecords);
                travel = actor.MoveToUnit(travelTargetId, options.TravelSpeed, options.TravelTimeout);
            }
            Collect(actor, traceRecords);
            stages.Add(Stage("TRAVEL", travelTargetId, travel));
            travel = runtime.Drive(actor, travel, options.TravelTimeout);
            Collect(actor, traceRecords);
            if (travel.State != ActorLifecycleState.Completed)
                return Fail("TRAVEL", travel.Failure ?? ActorFailureReason.Navigation,
                    $"travel leg not completed: {travel.State} ({travel.Detail ?? "n/a"})",
                    rigNotes, stages, criteria, traceRecords);

            // ------------------------------------------------ 3. HUNT
            // Observe → nearest attackable fox → SetTarget → Cast rotation,
            // until the kill objective is met. Kill credit flows through
            // the real DoOnMonsterHuntEvents; each credited kill is looted
            // (corpses are fresh then) and the step machine advances through
            // the contract (auto-complete quests drop from ActiveQuests).
            var kills = 0;
            var noTarget = 0;
            var attempts = 0;
            (string Stage, ActorFailureReason Failure, string Reason)? huntFailure = null;
            while (kills < options.RequiredKills && attempts < options.MaxHuntAttempts)
            {
                attempts++;
                var observation = actor.Observe();
                Collect(actor, traceRecords);

                var target = SelectHostile(character, observation, options.TargetNpcTemplateId, credited);
                if (target == null)
                {
                    noTarget++;
                    if (noTarget > options.NoTargetRetries)
                    {
                        huntFailure = ("HUNT", ActorFailureReason.WrongDecision,
                            $"no attackable npc template {options.TargetNpcTemplateId} visible after {options.NoTargetRetries} re-observe/re-travel retries " +
                            $"(nearby npcs: [{string.Join(", ", observation.NearbyNpcObjIds)}])");
                        break;
                    }

                    // Re-travel toward another live fox (server-side world
                    // read for the leg target; the KILL decision stays
                    // Observe-driven).
                    var next = FindNearestFox(character, options.TargetNpcTemplateId, credited);
                    if (next == null)
                        continue; // nothing left in the world — counts against the retry bound
                    var reApproach = actor.MoveToUnit(next.ObjId, options.TravelSpeed, options.TravelTimeout);
                    Collect(actor, traceRecords);
                    stages.Add(Stage("HUNT-RETRAVEL", next.ObjId, reApproach));
                    reApproach = runtime.Drive(actor, reApproach, options.TravelTimeout);
                    Collect(actor, traceRecords);
                    if (reApproach.State != ActorLifecycleState.Completed)
                    {
                        huntFailure = ("HUNT", reApproach.Failure ?? ActorFailureReason.Navigation,
                            $"re-travel to fox {next.ObjId} not completed: {reApproach.State} ({reApproach.Detail ?? "n/a"})");
                        break;
                    }
                    continue;
                }
                noTarget = 0;

                var targetRequest = actor.SetTarget(target.ObjId);
                Collect(actor, traceRecords);
                if (targetRequest.State != ActorLifecycleState.Completed)
                {
                    huntFailure = ("HUNT", targetRequest.Failure ?? ActorFailureReason.RejectedAction,
                        $"SetTarget on fox {target.ObjId} not completed: {targetRequest.Detail}");
                    break;
                }

                // Foxes ROAM (live AI): close into melee reach before casting
                // and after every range refusal, or the 4 m rotation starves
                // (run-4 live failure signature: endless TooFarRange).
                if (Vector3.Distance(character.Transform.World.Position, target.Transform.World.Position) > 3f)
                {
                    var closeIn = actor.MoveToUnit(target.ObjId, options.TravelSpeed, options.TravelTimeout);
                    Collect(actor, traceRecords);
                    stages.Add(Stage("HUNT-CLOSE", target.ObjId, closeIn));
                    closeIn = runtime.Drive(actor, closeIn, options.TravelTimeout);
                    Collect(actor, traceRecords);
                    if (closeIn.State != ActorLifecycleState.Completed)
                        continue; // leg failed — re-observe next round (bounded by attempts)
                }

                // Cast rotation in a burst: repeat while the target is up
                // (server-side alive read), bounded per round. Live prey
                // leash-resets mid-fight — the kill must land inside the
                // reset window. The stage detail carries the target's HP
                // before/after — the damage-landing evidence the trace needs.
                var targetDown = false;
                for (var burst = 0; burst < options.BurstCasts && !targetDown; burst++)
                {
                    var castExecuted = false;
                    foreach (var skillId in options.CastRotation)
                    {
                        var hpBefore = target.Hp;
                        var cast = actor.Cast(skillId, target.ObjId);
                        Collect(actor, traceRecords);
                        var castStage = Stage("HUNT-CAST", target.ObjId, cast);
                        stages.Add(castStage with
                        {
                            StatusObserved = $"{castStage.StatusObserved} [target hp {hpBefore}→{target.Hp}]"
                        });
                        if (cast.State != ActorLifecycleState.Rejected)
                        {
                            castExecuted = true;
                            break;
                        }
                    }
                    if (!castExecuted)
                        break; // the whole rotation refused — re-observe next round
                    targetDown = runtime.EnsureKillCredit(actor, target);
                }
                if (!targetDown)
                    continue; // target still up — re-observe and cast again

                kills++;
                credited.Add(target.ObjId);
                stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                    "HUNT-KILL", kills, "credited", target.ObjId.ToString(), $"kill {kills}/{options.RequiredKills}"));

                // -------------------------------------------- 4. LOOT (per corpse)
                runtime.PrepareLootCorpse(target);
                var loot = actor.Loot(target.ObjId);
                Collect(actor, traceRecords);
                stages.Add(Stage("LOOT", target.ObjId, loot));
                if (loot.State == ActorLifecycleState.Rejected)
                {
                    lootOutcomes.Add($"corpse {target.ObjId}: Rejected ({loot.Detail ?? "n/a"})");
                    if (!options.LootOptional)
                    {
                        huntFailure = ("LOOT", loot.Failure ?? ActorFailureReason.RejectedAction,
                            $"loot of corpse {target.ObjId} refused and loot is not optional: {loot.Detail}");
                        break;
                    }
                }
                else
                {
                    lootOutcomes.Add($"corpse {target.ObjId}: {loot.State} ({loot.Detail ?? "n/a"})");
                }

                // Advance the step machine through the contract (250
                // auto-completes when the objective count is met — the
                // completion drops the quest from ActiveQuests, the
                // terminal engine behavior).
                if (character.Quests?.ActiveQuests.ContainsKey(options.QuestId) == true)
                {
                    var advance = actor.AdvanceQuest(options.QuestId);
                    Collect(actor, traceRecords);
                    stages.Add(Stage("HUNT-ADVANCE", options.QuestId, advance));
                    if (advance.State != ActorLifecycleState.Completed)
                    {
                        huntFailure = ("HUNT", advance.Failure ?? ActorFailureReason.StateTransition,
                            $"advance refused after kill {kills}: {advance.Detail}");
                        break;
                    }
                }
            }

            if (huntFailure != null)
                return Fail(huntFailure.Value.Stage, huntFailure.Value.Failure, huntFailure.Value.Reason,
                    rigNotes, stages, criteria, traceRecords);
            if (kills < options.RequiredKills)
                return Fail("HUNT", ActorFailureReason.Starvation,
                    $"hunt budget exhausted: {kills}/{options.RequiredKills} kills in {attempts} attempts (max {options.MaxHuntAttempts})",
                    rigNotes, stages, criteria, traceRecords);

            // ------------------------------------------------ 5. COMPLETE
            // Quest 250 auto-completes (golden route §1a: turn-in "auto") —
            // no report leg. The kill advance takes the quest to
            // Reward/Completed; the completion itself (reward → flag set →
            // drop from ActiveQuests) is one more step-machine pass. Drain
            // the same engine evaluations the TurnIn contract action drains,
            // bounded, through AdvanceQuest.
            var drainGuard = 0;
            while (character.Quests?.ActiveQuests.ContainsKey(options.QuestId) == true && drainGuard++ < 8)
            {
                var drain = actor.AdvanceQuest(options.QuestId);
                Collect(actor, traceRecords);
                stages.Add(Stage("COMPLETE-ADVANCE", options.QuestId, drain));
                if (drain.State != ActorLifecycleState.Completed)
                    return Fail("COMPLETE", drain.Failure ?? ActorFailureReason.StateTransition,
                        $"completion advance refused: {drain.Detail}", rigNotes, stages, criteria, traceRecords);
            }

            var finalObserve = actor.Observe();
            Collect(actor, traceRecords);
            stages.Add(Stage("COMPLETE-OBSERVE", 0, actor.AuditTrace.Last()));

            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "kills-credited", kills == options.RequiredKills,
                $"{kills}/{options.RequiredKills} fox kills credited through DoOnMonsterHuntEvents"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "loot-recorded", lootOutcomes.Count > 0,
                $"{lootOutcomes.Count} corpse loot attempt(s): {string.Join(" | ", lootOutcomes)}"));

            var stillActive = finalObserve.ActiveQuestIds.Contains(options.QuestId);
            var completedFlag = character.Quests?.HasQuestCompleted(options.QuestId) == true;
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                $"quest-{options.QuestId}-completed", completedFlag && !stillActive,
                $"quest {options.QuestId}: completed flag={completedFlag}, active={stillActive}"));

            var lifecycle = AssertTraceCompleteness(traceRecords, out var lifecycleDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", lifecycle, lifecycleDetail));

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
            Logger.Error(ex, "adventurer spike crashed");
            return Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", rigNotes, stages, criteria, traceRecords);
        }
    }

    // ------------------------------------------------------- hostile selection

    /// <summary>
    /// The hostile-selection primitive: from one observation, the nearest
    /// ALIVE NPC the actor can attack (BaseUnit.CanAttack — faction-based;
    /// bare fixture NPCs read attackable), optionally template-filtered,
    /// skipping already-credited kills. Pure-ish and hermetic-testable:
    /// objIds resolve through the character's own world registry.
    /// </summary>
    internal static Npc? SelectHostile(Character character, ActorObservation observation,
        uint? templateFilter, IReadOnlySet<uint>? excludedObjIds = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(observation);

        Npc? best = null;
        var bestDistance = float.MaxValue;
        var position = character.Transform.World.Position;
        foreach (var objId in observation.NearbyNpcObjIds)
        {
            if (excludedObjIds?.Contains(objId) == true)
                continue;
            if (character.ParentWorld?.GetNpc(objId) is not { } npc)
                continue;
            if (templateFilter is { } filter && npc.TemplateId != filter)
                continue;
            if (npc.Hp <= 0)
                continue;
            if (!character.CanAttack(npc))
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

    /// <summary>
    /// Server-side world scan for the re-travel leg: the nearest alive,
    /// uncredited fox in the whole world (the Observe radius is 25 m — the
    /// live fox cluster is spread over ~130 m, so consecutive kills can
    /// need a short MoveToUnit hop). The KILL decision stays Observe-driven;
    /// this only picks the leg destination.
    /// </summary>
    private static Npc? FindNearestFox(Character character, uint templateId, IReadOnlySet<uint> excluded)
    {
        if (character.ParentWorld == null)
            return null;
        Npc? best = null;
        var bestDistance = float.MaxValue;
        var position = character.Transform.World.Position;
        foreach (var npc in character.ParentWorld.GetAllNpcs())
        {
            if (npc.TemplateId != templateId || npc.Hp <= 0 || excluded.Contains(npc.ObjId))
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

    // ------------------------------------------------------- result helpers

    /// <summary>New audit records emitted since the last snapshot (allows multi-record actions).</summary>
    private static void Collect(GameplayActor actor, List<ActorAuditRecord> traceRecords)
    {
        foreach (var record in actor.AuditTrace.Skip(traceRecords.Count))
            traceRecords.Add(record);
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, uint target, ActorRequest request)
        => new(name, 1, request.State.ToString(), target.ToString(), request.Detail ?? "");

    /// <summary>Stage verdict from an audit record (observation stages).</summary>
    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, uint target, ActorAuditRecord record)
        => new(name, 1, record.Result.ToString(), target.ToString(), record.Detail ?? "");

    /// <summary>
    /// Lifecycle correctness: every Completed action carries Requested →
    /// Accepted → Completed; execution actions additionally carry Running
    /// (Observe/Target complete immediately without one). No Rejected
    /// record ever carries Running (refusals are pre-flight).
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
        detail = $"records={records.Count} completed={records.Count(r => r.Result == ActorLifecycleState.Completed)} " +
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
/// on the game loop — the runtime only ticks the actor's own movement and
/// waits on real time. Kills are REAL: the cast rotation's damage must
/// down the fox (Npc.DoDie → QuestManager.DoOnMonsterHuntEvents); nothing
/// is faked here.
/// </summary>
public sealed class LiveSpikeRuntime : AdventurerSpikeScenario.ISpikeRuntime
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
        // Real cast damage only — no synthetic credit live. The skill's
        // damage effect lands ~200 ms after UseSkill returns (EffectDelay →
        // ApplySkillTask on the game loop), so poll briefly before reporting
        // the target as still up; the poll stays SHORT on purpose — prey
        // roams and regen ticks, so the hunt loop must re-cast quickly
        // rather than idle-wait per round.
        var deadline = Environment.TickCount64 + 400;
        do
        {
            if (TargetDown(target))
                return true;
            Thread.Sleep(100);
        } while (Environment.TickCount64 < deadline);
        return false;
    }

    public void PrepareLootCorpse(Npc corpse)
    {
        // live: real DoDie generates the corpse's loot — nothing to seed
    }
}
