# G5 Movement Handoff — Discovery (READ-ONLY, branch develop, 2026-09-20)

READ-ONLY discovery. Nothing modified, built, or run. Every code claim cites `file:line`
as read this pass; anything not found is marked `[INFERENCE]` / `UNKNOWN`.
Context (not re-verified except where re-read): `g4-discovery-plan.md` (§§H/I),
`brain-wake-ownership.md` (§1 wake edges 22–28, §3 roam map), `gap-nav-persist.md`
(§1 nav contract), `g3-closeout-freeze.md` + `g4-closeout-freeze.md` (G3/G4 frozen).
G5 mission: from the G4 end state (quest 251 ACTIVE, live 3475 selected via `SetTarget`),
approach/maintain pursuit to the combat-engagement boundary and STOP before attack.
Canonical seam: `BotActionCommandQueue → GameplayActor → engine`.

## A. MOVEMENT MATRIX (code-verified, not name-verified)

Owner of all four verbs: `GameplayActor` (action execution). Navigation orchestration
(`BotPath`/`PendingLeg`) is owned by the `BotRoamStepExecutor` route layer; behaviors
issue intents. Single-writer gate `TryBegin` busy-rejects (`GameplayActor.cs:4640-4671`).

| Verb | Entry | Owner | Destination | Snapshot vs re-read | Arrival radius | Moving-target support |
|---|---|---|---|---|---|---|
| `MoveTo` | `GameplayActor.cs:312-330` → `StartMove` `:418-437` | actor | absolute `Vector3`, finite check `:327-328` | static (absolute by construction) | 0.5 f flat + \|ΔZ\| ≤ 0.5 f (`ArrivalRadius` `:75`; Tick `:4218`) | NONE in-leg; per-wake re-issue only (see follow row) |
| `NavigateTo` | `GameplayActor.cs:332-341` → `NavigateToInternal` `:358-412` | actor | absolute `Vector3` | static | same 0.5 f box (`:366-367` no-op, `:4218`) | NONE in-leg |
| `MoveToUnit` | `GameplayActor.cs:482-495` | actor | unit resolved ONCE at dispatch `:491-493` (`ResolveUnit` → `ParentWorld?.GetUnit` `:4753-4760`), snapshot pos → `StartMove` | snapshot; `Tick` (`:4181-4269`) contains no `ResolveUnit` call — never re-resolves | same 0.5 f box | NONE in-leg (stale-point arrival) |
| `NavigateToUnit` | `GameplayActor.cs:343-356` | actor | resolved once `:351-353`, snapshot → `NavigateToInternal` `:355` | snapshot, same as above | same 0.5 f box | NONE in-leg |
| `Stop` | `GameplayActor.cs:497-520` | actor | n/a | n/a | n/a (halts mid-leg + `BroadcastStop` `:513-514`) | interrupts live Move; completes itself `"stopped"`; idle = Completed no-op |

Pathfinding (`NavigateToInternal`, `GameplayActor.cs:358-412`): navmesh A* ONLY when
`parentWorld.Template.GeoData != null && GeoDataMode` (`:373-394`); else obstacle detour
(`:397-409`); else silent straight-line `StartMove` (`:411`) with NO distinct
terminal/detail for fallback. `path.Count > 1` required (`:380`), else fall-through.
Zones/worlds: destinations are bare `Vector3` (no world/instance/zone id); every lookup
is `Character.ParentWorld`; `Tick` never changes `ParentWorld`/`InstanceId`
(`gap-nav-persist.md` §1.5.4, not re-verified line-by-line this pass). Interzone = walk
meaninglessly to budget expiry. Stuck detection (`UpdateMoveStuckState`, `:4408-4442`):
3-D displacement > 0.5 f resets timer (`:4415`); else accumulate over `NoProgressWindow`
2.5 s (`:116`); one 2 m lateral nudge (`UnstickNudgeDistance` `:119`,
`MaxUnstickNudges = 1` `:137`); then fail-fast `TimedOut(Navigation, "stuck: no
progress…")` (`:4437-4441`). `NoProgressWindow <= 0` disables (test seam `:4412`).
Drive branch (`:4272-4306`) has NO stuck detection — budget expiry only (verified: no
`UpdateMoveStuckState` call in that branch). Replan: NONE — `_moveWaypoints` dequeued
once; waypoint advance resets progress (`:4230-4237`); corner blend 1 f (`:166`,
`:4204-4216`); trapezoid profile (`:4362-4373`); no mid-leg repath on any event.
Timeout: `DefaultMoveTimeout` 30 s (`:78`); `Elapsed` accrues only in `Running` via
`Tick` (`ActorRequest.cs:222-226`, advanced `:4091`); expiry → `Expire` with
`ActorTimeoutPolicy.ReasonFor` (Move/Drive → Navigation, else Starvation)
(`GameplayActor.cs:4779-4783`). Cancellation: `Stop()` (`:497-520`);
`PreemptCurrent(reason)` (`:523-536`, bool, no own record, clears movement state via
`InterruptActive` `:4673-4685`); queue preempts world-internal legs for API commands
(`BotActionCommandQueue.cs:440-455`) but REJECTS when an API leg owns the actor
(`:445-449`); queue backstop iterates ONLY `_apiOwned` (`:370-417`) — internal legs are
ticked solely by scheduler wakes (`BotRoamStepExecutor.cs:1006-1014`), so a stopped
scheduler leaves an internal Move `Running` forever (single-writer wedge).
Terminal states: `Requested → Accepted → Running → Completed | Rejected | Interrupted |
TimedOut` (`ActorRequest.cs:9-13`; `IsTerminal` `:162-163`). Movement terminals:
`Completed("arrived")` (`:4242`), `Completed("already at destination")` (`:349-350`,
`:427-428` — full lifecycle, never skips `Running`), `TimedOut(Navigation, "navigation
budget exceeded")` (`:4102-4103`), `TimedOut(Navigation, "stuck: …")` (`:4438`),
`Rejected(RejectedAction, speed/finite/target-not-found)` (`:325-328`, `:360-363`,
`:471-473`, `:352-353`), `Rejected(StateTransition, busy/dedupe)` (`:4645`, `:4663`),
`Interrupted("stop requested")` (`:515`), queue-backstop `TimedOut` from enqueue time
(`BotActionCommandQueue.cs:402-413`).

Target-follow (all snapshot + per-wake re-issue; NO in-leg retrack anywhere):
party-follow `MoveTo(leaderPos)` 5 s budget + 3 m drift re-issue
(`BotRoamStepExecutor.cs:600-611`); party-assist `MoveToUnit(leaderTarget)` 5 s
(`:577-580`); roam hunt chase `MoveTo(targetPos)` 10 s + 2 m drift re-issue
(`:781-790`); butcher `MoveTo(butcherPos)` 10 s + 2 m drift (`:916-925`); `HuntLeg`
CloseGap `NavigateToUnit` with travel timeout (`LevelingLoopScenario.cs:2728-2729`);
GOAP `ApproachTargetAction` `MoveToUnit(CurrentTarget)`
(`Goap/Actions/CombatActions.cs:53-58`); spike `MaintainRange` — melee closes straight
onto unit via `MoveToUnit`, ranged closes to band edge via `MoveTo`
(`AdventurerSpikeScenario.cs:892-897`; same shape `PartySpikeScenario.cs:566-571`).
Combat-approach (roam inline): range check `dist > engageRange` with melee
`HuntMeleeRange` 3.0 f (`BotRoamStepExecutor.cs:118-119`) vs ranged 15.0 f (`:776`);
in-range → `PreemptCurrent("hunt in engage range")` + `PendingLeg = null` (`:794-798`),
face target (`:800-802`), `AutoAttack` (`:804-807`), skill rotation every
`HuntCastInterval` 800 ms (`:116`, `:809-831`) via `SelectPrioritizedSkill`.
`CombatDecisionTree.Evaluate` bands (`CombatDecisionTree.cs:481-618`): melee `CloseGap`
beyond `maxMeleeRange` (default `DefaultMeleeMax` 3.5 f `:59`; `HuntLeg` passes
`HuntEngageRange` 3 f `:2706`), gap-closer skills first (Charge ≤ 12 m, Overwhelm ≤ 10 m,
`:561-564`); ranged band [`DefaultRangedMin` 12 f, `DefaultRangedMax` 22 f] (`:104-105`)
with kite/close-gap outside it (`:522-550`); `EmergencyFlee` ≤ 20% HP with 25 m flee
vector (`:502-515`); no usable skill + melee → `CloseGap` onto `targetPos` (`:591-600`).
Skill reach reference: melee skills 4 m, ranged 20 m (`GetKnownSkillRange`, `:188-228`).
`AutoAttack` rejects null-or-dead (`GameplayActor.cs:715-717`).
Route state: actor movement fields cleared as a unit (`ClearMovementState`, `:4509-4519`;
fresh tracking per leg `:4378-4385`); route-layer slot `state.PendingLeg` + `BotPath`
(`CurrentTarget` waypoint, `IsFinished`, `ArrivalRadius` default 0.5 f —
`BotPath.cs:37-38,65-77`); route pump issues `MoveTo` when idle (`BotRoamStepExecutor.cs:989-1004`),
flat-arrival `Stop` (`:1016-1026`), terminal-advance + next `MoveTo` (`:1027-1038`);
`ArmQuestTravel`/`ArmFarmRoute` arm `Path`, never behavior state (`:1271-1294`).

## B. G4→MOVEMENT HANDOFF: NONE (explicit)

G4 end state: `QuestBehavior` per-active-quest path evaluates `Evaluate` once per wake
and a selection competes as a `Target` proposal dispatched through
`gameplayActor.SetTarget` ONLY (`QuestBehavior.cs:74-107` proposal path, `:480-482`
dispatch arm; priority `ObjectiveTargetPriority = 25`, above advance 20/accept 10,
below turn-in 30 — `QuestDecisionScenario.cs:49-55`). The proposal carries NO movement:
`Dispatch` (`:466-483`) has no `Move` arm for it, and the postcondition is
`observed.CurrentTargetObjId == selected` (`:418-420`) — pure assignment.
`SetTarget` itself is terminal-synchronous (`Complete(request, $"targeting …")`,
`GameplayActor.cs:563`) and `Tick` advances ONLY the active request (`:4088-4091`) and
issues no new requests (verified across `:4084-4307`: `Tick` calls `BroadcastStop`,
`Finish`, `ClearMovementState`, `ApplyCharacterMove`/`ApplyVehicleMove` plus
craft/putdown/cast-effect drains — no `MoveTo`/`NavigateTo`/`SetTarget` call). So
nothing auto-continues after `SetTarget` lands. The full chain is:

`SetTarget lands (Completed, terminal)` → `StepQuestLeg` sees `landed == true` →
returns `true` WITHOUT calling `ArmQuestTravel` (travel is the `else` branch,
`BotRoamStepExecutor.cs:1247-1258`) → route pump sees `TargetNpcObjId == 0` (quest code
never writes roam state) → NOTHING. Next wake re-runs the same selection (stable
reselect per the G4 freeze) → NONE, every wake.

Each hypothesized edge verified absent:
- QuestBehavior travel branch: `ResolveQuestTravelTarget` (`:1302-1358`) reads ONLY the
  Ready-reporter spawner (`:1312-1327`) and the nothing-active in-band offerer
  (`:1334-1355`); 251-in-Progress falls through both → `null` → `"no walkable quest
  target"` (`:1281`). It never reads `Character.CurrentTarget`, the objective funnel,
  or the selected objId (signature takes the actor only for quest/world reads).
  Quest travel destination is INDEPENDENT of the selected target — verified, not
  inferred. It also never re-arms over a live route (`:1273-1275`).
- Roam `CurrentTarget` inspection: the hunt branch keys off `state.TargetNpcObjId`
  (`:711-714`), never off `Character.CurrentTarget` except to SYNC-WRITE it
  (`:769-773`) and to clear it on drop (`:751-755`). G4 `SetTarget` writes
  `Character.CurrentTarget` (`GameplayActor.cs:552`) but no code path copies that into
  `state.TargetNpcObjId` (quest behavior has no access to `BotRoamState`; its
  `Dispatch` calls actor verbs only). The hunt loop therefore never sees the G4
  target. Worse for auto-approach: while 251 is active with a live boar present, the
  quest leg lands `Completed` every wake, so `QuestLegActive == true` and the hunt
  gate (`!state.QuestLegActive`, `:711`) SUPPRESSES hunt that wake — selection actively
  preempts pursuit.
- Combat/hunt ownership: roam inline hunt is quest-blind (`IsAttackableWildlife`,
  `:2325-2348`, no template filter) and requires `EnableWildlifeHunt`
  (`:105-106, :711`); `HuntLeg` (SetTarget → `Evaluate` → CloseGap `NavigateToUnit` →
  cast burst → loot, `LevelingLoopScenario.cs:2591-2825`) is scenario/rig-driven, not on
  the production wake path (production wake = Scheduler → arbiter decorator → roam
  executor per `brain-wake-ownership.md` §1); GOAP `AcquireHostileTargetAction` is a
  `SetTarget(0)` stub that ALWAYS rejects (`0` → `ResolveUnit` null → `Rejected`,
  `CombatActions.cs:19-23` + `GameplayActor.cs:4755-4756`) — never a wire target;
  `ApproachTargetAction` (`MoveToUnit` on `CurrentTarget`, `:53-58`) is reachable only
  through GOAP plans, which run only under `homestead.*` activity
  (`BotRoamStepExecutor.cs:697-706`) — unreachable while the arbiter holds `quest.*`.
  `CombatExecutor` test-only per `g4-closeout-freeze.md` §f (NOT re-read this pass).
- `BotBehaviorRuntime`: hosts the quest tick only; owns no navigation state
  (`Path`/`PendingLeg` stay route-side), `NextWake` hint never consumed
  (`BotBehaviorRuntime.cs:32-44`) — no movement edge.

## C. ROAM HUNT OWNERSHIP (classify each; no rewrite)

Scope: `BotRoamStepExecutor.cs` hunt loop (`:708-876`), engage block (`:766-833`),
butcher approach (`:888-950`) as pattern reference, route pump (`:989-1038`),
state (`:234-246`), constants (`:95-123`).

| Item | Location | Class | Reason |
|---|---|---|---|
| chase `MoveTo(targetPos)` 10 s + 2 m drift re-issue | `:778-790` | REUSABLE | snapshot + per-wake re-issue IS the pursuit shape; needs 3475 filter + G5 stop radius |
| engage-range preempt + face + `AutoAttack` + cast rotation | `:792-831` | REUSABLE | combat-approach stop + engage pattern; G5 takes the stop, drops the attack |
| range checks (melee 3.0 / ranged 15.0) | `:118-119`, `:774-776` | REUSABLE | combat-range authority pattern; G5 sets its own stop-before-attack radius beside it |
| 45 m perception scan + nearest-wins | `:108-110`, `:835-855` | NEEDS EXTRACTION | mechanics reusable; quest-blind filter must gain template-3475 relevance |
| target lifecycle fields (`TargetNpcObjId/TargetEngagedUtc/LastScanUtc/LastCastUtc/LastSkillUsed`) | `:234-238` | NEEDS EXTRACTION | behavior-owned target lifecycle (brain-wake §3 already assigns to `HuntBehavior`); G5 pursuit state belongs there, not in executor |
| 30 s cap + dead/invalid drop + loot + disengage + `PreemptCurrent("hunt target down")` | `:719-764` | NEEDS EXTRACTION (lifecycle) / loot SCENARIO-ONLY | revalidation + drop shape reusable; `Loot` + corpse handling out of G5 scope (stop before attack) |
| hunt re-issue while target valid (unbounded, drift-gated only) | `:781-790` | NEEDS EXTRACTION | no retry bound beyond 30 s cap + 10 s leg budget; G5 needs an explicit bounded retry (gap-nav §2.2c) |
| `PendingLeg` + `BotPath` pump (issue/advance/flat-arrival stop) | `:989-1038` | REUSABLE | navigation-owned; G5 pursuit legs MUST ride this slot, never a second leg variable |
| route scheduling + cadence (`ActiveCadence`, hunt/butcher/route flags) | `:69-82`, `:1138-1145` | REUSABLE | host layer; pursuit is scheduled by it, not around it |
| actor stuck detection (2.5 s + 1 nudge + fail-fast) | `GameplayActor.cs:4408-4442` | REUSABLE | actor layer; hunt carries no own stuck logic (correct — keep it that way) |
| `IsAttackableWildlife` | `:2325-2348` | DUPLICATE | second "hostile?" spelling beside public `IsHostileTarget` (`CombatDecisionTree.cs:73-102`); G4 §G.1 prohibits a third copy — G5 calls the public one + template filter |
| `CurrentTarget` direct-write bypass (hunt sync + acquire) | `:769-773`, `:865-866` | STALE | G4 D2: new code rides canonical `SetTarget` (`GameplayActor.cs:538-563`), never direct writes |
| `HuntLeg` (SetTarget→Evaluate→CloseGap→burst→loot) | `LevelingLoopScenario.cs:2591-2825` | SCENARIO-ONLY | rig-driven shape reference for approach-then-engage; not on the production wake path |
| `SelectHuntTarget` (snapshot candidates + `CanAttack` + nearest) | `:2842-2893` | SCENARIO-ONLY (shape donor) | G4 already extracted this shape into `QuestObjectiveTargetSelector.Evaluate` (`QuestObjectiveTargetSelector.cs:116-231`); G5 reuses the selector, not the scenario |
| `ArmQuestTravel` / `ResolveQuestTravelTarget` | `:1271-1358` | SCENARIO-ONLY (for pursuit) | Ready-reporter / nothing-active destination families; prey-travel must never inherit offer-band logic (G4 §F.1) |
| butcher approach (`MoveTo` + `MaxInteractRange` gate + `Interact`) | `:910-950` | SCENARIO-ONLY | doodad domain; pattern reference only (approach → range gate → act) |
| party follow/assist (`MoveTo` leader / `MoveToUnit` leader-target) | `:559-611` | SCENARIO-ONLY | different owner/trigger (party coordination); follow shape reference only |
| GOAP `AcquireHostileTargetAction` (`SetTarget(0)` stub) | `Goap/Actions/CombatActions.cs:19-23` | FIXTURE-ONLY | always-`Rejected`; never a wire target (G4 §G.7) |
| GOAP `ApproachTargetAction` (`MoveToUnit` on `CurrentTarget`) | `:53-58` | UNKNOWN | verb shape fits pursuit, but combat-plan wiring onto the quest path not verified this pass; homestead-gated today (`:697-706`) |
| `CombatExecutor` | (not re-read) | UNKNOWN | freeze doc labels test-only (`g4-closeout-freeze.md` §f); not verified this pass |
| distance-threshold constants (45/3.0/15.0/2.0 m, 30 s, 10 s, 1.2 s, 800 ms) | `:95-123`, `:776`, `:782-783`, `:722`, `:789` | REUSABLE pattern, SCENARIO-ONLY values | named-constant + env-override pattern carries over; G5 tunes its own stop radius |
