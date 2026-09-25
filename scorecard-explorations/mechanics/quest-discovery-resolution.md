# Quest Discovery Resolution — sweep-diagnostic pass (2026-09-19, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (ports 2237/2239/2250/2260/2234/2280/3311,
`COMPOSE_PROJECT_NAME=q0pilot`, token `e2e-q0-pilot-token`). ONE rebuild+restart consumed by this
pass (game-side sweep diagnostics require redeploy — sweep I/O exists only server-side; no
client-visible or bridge-only alternative). Justification recorded; no second restart performed.
MySQL + login + DB volume untouched. Runtime sqlite md5 unchanged `78b3bdbf038db3b927056106efdf91af`
(source rev `6c3efe25ecb68998544eb3f75699bc1674c7eef1` + worktree).

Read-first context: `nearby-quest-contracts.md` (§1 state machine, §4 fixture 251@3512),
`fresh-bot-scheduler-resolution.md` (scheduler PASS, G3 FAIL at DISCOVERY/no-legal-proposal, travel
fallback to the 3597 spawner), `brain-g3-funnel.md` (travel-vs-proposal identity universes),
`G3AutonomousQuestAccept3512Tests.cs` (fixed harness — used as-is, unmodified).

## A. Call path (exact, code-verified — no inference)

Scheduler wake → `BotGoalArbiterStepExecutor.StepAsync` (arbitrate → inner roam step) →
`BotRoamStepExecutor.StepQuestLeg` (`BotRoamStepExecutor.cs:1212-1259`): actor-type gate, provider =
`NearbyNpcProvider ?? DefaultNearbyNpcs`, per-bot `BotBehaviorRuntime.Tick` with
`CycleId = quest-{charId}-{ticks}` (band [1,9] / cap 3 defaults) → `QuestBehavior.Run`
(`QuestBehavior.cs:57-...`): Capture #1 → active-quest advance/turn-in proposals → **sweep**
`nearbyNpcs(character, GameplayActor.MaxQuestDiscoverRange = MaxInteractRange = 25f flat)`
(`:108-135`) → first-3 take **in provider enumeration order, no distance sort** (`:130-134`) →
per-target `actor.DiscoverQuests(objId, key)` (`:142-146`) → band + not-active filter → accept
proposals compete in ONE `BotDecisionSelector.Select` → `BotDecisionCycle.Execute` → dispatch
(`AcceptQuest` with `(offering, targetObjId)` payload). Landed → true; DECIDE-no-proposal →
`Blocked`/travel fallback `ArmQuestTravel` (`:1256-1279`).

`DiscoverQuests` (`GameplayActor.cs:1198-1278`): execution-thread assert → `TryBegin` (busy →
terminal Rejected, audit row kept) → resolve NPC-first-then-doodad (`GetNpc`/`GetDoodad` by objId;
both null → Reject `not found in world`) → flat range re-check vs `MaxQuestDiscoverRange`
(`:1220`, default `CalculateDistance` = flat) → linkage
`GetQuestsOfferedByNpc(template)` (Start components carrying `QuestActConAcceptNpc.NpcId ==
template) + kill channel → fail-closed `IsDiscoverable` per candidate → `Complete` with
`QuestDiscoveryResult`. **Every terminal outcome mints an audit row** (`Finish`, `:4686-4705`), so
`discoverCalls=0` in the bridge payload rigorously implies the sweep evaluated zero targets
(sweep-empty), never a silent drop.

`DefaultNearbyNpcs` (`BotRoamStepExecutor.cs:2064-2072`): `WorldManager.GetAround<Npc>(character,
radius)` when `Region != null`, else world-registry scan with the same radius. `GetAround`
(`WorldManager.cs:1237-1257`) returns EMPTY when `Region == null`; otherwise current-region or
neighbor-region `GetList` (region array/insertion order — **not distance order**).

Travel fallback (`ResolveQuestTravelTarget`, `:1271-1336`): Ready-reporter spawner first, else —
with zero active quests — nearest spawner **world-wide** (`Vector3.Distance`, 3D) whose
`OffersWalkableQuest` (in-band + `IsDiscoverable`) passes. Separate identity universe from
discovery by design (spawner templates vs live objIds), not a contradiction.

## B. Funnel counts (diagnostic run 2026-09-19 20:26:58Z, charId=15, ONE quest tick observed)

`QuestSweepDiag` (Server.log 13:27:00 local):
`cycle=quest-15-… char(ActorId)=44283 actor=(15578.0,15382.1,126.5) zone=179 region=249072
raw=0 eval=[] trunc=0 outcomes=[] offers=0 inBand=0`
→ DECIDE `firstZero=sweep-empty`, `legal=0`. Bridge `discover=0/0/0`, `questActions=0`.
Identity probe: `botSameChar=true botCharId=15 actorCharId=15 botPos==actorPos==(spawn)`.

So: raw provider enumeration = 0 at the Solzreed spawn point with a HEALTHY region (249072,
non-null — region-null path excluded), actor/bot identity single and pose-agreeing (two-instance
and stale-pose hypotheses excluded for this tick). Ordering/truncation vacuous at raw=0.

## C. 3512 trace (explicit)

- Template→quest linkage (data-verified, lane sqlite): quest 251 (L2) Start component 383 →
  act 333 `QuestActConAcceptNpc` → detail 77 → **npc 3512**. `offers(3512)` contains 251 by
  construction (`GetQuestsOfferedByNpc`).
- 3512 spawner (main_world/npc_spawns.json:184418): UnitId 3512 @ (15655.91,15172.51,121.24);
  DB spawner 3409, schedule always-on (start/end 0, activation t). Single entry.
- Live 3512 objId during the diagnostic run: **never observed** — the run died at SETUP (see F).
  Pre-reboot run: objId 37273 (spawned after correct staging; see F).
- 3512-in-sweep: absent — the only observed sweep ran at spawn (223 m from 3512's spawner), raw=0.
  Whether 3512 enumerates (and at what index/distance) from its staged position is UNOBSERVED.

## D. Reject reasons (from actual predicates — no generic INVALID)

`IsDiscoverable` refactored to delegate to `DiscoverRejectReason` (same predicates, same order;
`GameplayActor.cs:1422-1454`). Codes: `NO_TEMPLATE`, `NO_QUEST_STATE`, `ALREADY_ACTIVE`,
`SUPPLY_BLOCKED`, `REQ_FAIL_START_{componentId}`, `COMPLETED_NON_REPEATABLE`. `DiscoverQuests`
`Complete` detail now carries `[cands=N reject=qid:CODE,…]` (bounded 6 + overflow). No live
rejections observed this run (zero targets evaluated). Data-level pre-check for the fixture:
251/Start-383 req = Level kind (min 1, max 0 = none) → passes at any level ≥ 1 (level-10 fixture
clears it); supply gate is pack-backpack only (fresh bot clears); 330/Start-1520 reqs =
Level≥1 + `ExceptCompleteQuestContext(6198)` (fresh char clears) → 3597 walkable, consistent with
the observed fallback.

## E. Identity map

| Layer | 251 chain | 330 chain (fallback) |
|---|---|---|
| Quest (level, band [1,9]) | 251 L2 ✓ | 330 L1 ✓ |
| Giver template | 3512 (act 333/detail 77) | 3597 |
| Spawner (JSON) | (15655.91,15172.51) | (15562.45,15354.32) |
| Starter spawn | (15578.0,15382.1) — 223 m from 3512, 32 m from 3597 | same |
| Travel pick from spawn | — (farther) | 3597 (nearest walkable; correct from spawn) |

`ActorId 44283 == Character ObjId 44283` (actor id allocator = obj id); `Character.Id 15` = DB id.
No mixed-identity bug in the decision path (`botSameChar=true`). Travel (spawner-template scan)
vs discovery (live-objId scan) genuinely differ — documented, not a failure.

## F. Root cause

**Observed (proven this pass): the only quest tick ran at the SPAWN position, pre-staging,
with raw=0 (region healthy). The staged-giver discovery was never swept — setup died first.**
Pre-reboot `discover=0/0/0` is consistent with the same shape (travel armed from spawn
`(15578,15382)` → the enroll-time tick is the one that swept), but pre-reboot traces cannot
confirm or exclude a post-staging tick (no diagnostics then) — so for the staged case the
verdict is **setup-blocked, mechanism below**, not a quest-logic root cause.

**New blocking finding (reproduced once, post-reboot): `teleportToNpc(3512)` staged the bot at
3597's spawner `(15562.7,15354.7,127.9)` — exact XYZ match — and NPC 3512 never materialized
(objId 0, full 30 s poll).** Eliminated: walk-back (2.5 m/s × 30 s = 75 m < 204 m required;
pre-reboot 23 m walk in 9 s matches 2.5 m/s exactly and is the control case), error+walk (bot
would rest at spawn; and post-reboot the enroll wake demonstrably armed the spawn→3597 route),
spawn-delay (≤10 s ⊂ 30 s poll; player-at-spawner forces spawn). Remaining: the spawner lookup
`GetAllSpawners().SelectMany().FirstOrDefault(s => s.UnitId == 3512)` returned the wrong spawner
(or the world it searched is not what we assume). Mechanism UNKNOWN — needs live spawner-state
introspection (no code change needed to get it: the op already returns `{spawnerId,x,y,z}`, the
fixed test just ignores it). No production quest-logic defect is demonstrated; **no fix applied**
(deliberately — a fix now would be a guess).

## G. Fix: none

Allowed-list review: ordering (unsorted take-3) not demonstrated causal (raw=0); region-null not
observed (region healthy); no predicate ever rejected live (no candidates); spawn-timing covered
by the 30 s poll; the wrong-spawner teleport is fixture/bridge code with unknown mechanism.
General-for-the-bug + narrow-in-scope cannot both be satisfied → no change to behavior. No rerun
(rerun only if fixed, per budget).

## H. G3 result (diagnostic run, unmodified test)

`UNKNOWN` at `SETUP/npc-unresolved` (wall 33.1 s, charId=15): enter-world ✓, enroll ✓
(`steppedGlobal=False`, `activeBefore=False`), fixture-stage ✗ (`giverObjId=0`,
`char=[15562.7,15354.7,127.9]`). Wake leg never reached (exec 0 s). Prior report (charId=12,
FAIL-BEHAVIOR/DISCOVERY) reinterpreted per F: its travel arm is enroll-time (spawn), and its
23 m staged-pos offset equals the enroll-route walk during the 9 s poll — the "staged ≤25 m"
precondition decayed in transit, and its wake-PROVEN rests on the persisting travel reason.

## I. Files changed (diagnostics only, additive, bounded; zero behavior-semantic change)

- `AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs` — sweep census (raw/enumCap/take-3
  semantics byte-identical, early-cut preserved), per-candidate `objId:templateId:flat:3d`
  (cap 8), per-target `DiscoverQuests` outcome tokens (cap 3), one `Info QuestSweepDiag` line
  per wake, DECIDE detail extended with parser-safe keys (`raw/trunc/actor/zone/region/outcomes`;
  `BotQuestFunnel.TryParseFailDetail` ignores unknown keys — 7/7 projection tests pass).
  Null-safe (`Character => null!` fixtures still land Blocked).
- `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs` — `DiscoverRejectReason` (D codes);
  `IsDiscoverable` delegates (identical order); `DiscoverQuests Complete` detail gains bounded
  `[cands=N reject=…]`.
- `AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs` — `BotRoamState.QuestDecideDetail`
  (overwritten every quest tick, observe-only) + bot-vs-actor identity probe `Info` line.
- `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs` — observe payload gains `questDecideDetail`
  (existing `questTravelReason`/travel-target fields preserved — one edit initially dropped them
  and it was restored before build).
- Scoped unit proof: BotBrainProjectionTests 7/7, BotDecisionProposalTests 5/5,
  LevelingLoopStage4PerceptionOrderTests 4/4. GameplayActorQuestActionsTests 8/9 — the one
  failure (`AcceptQuest_SameKeyRetry` dedupe-detail assertion) is in sibling-modified
  `ActorRequest.cs` idempotency territory, untouched by this pass (HEAD-alone does not compile
  against the stacked worktree, so no cleaner isolation exists; recorded, not fixed).
- Lane left running the diagnostic binary (behavior-identical + Info lines); no second restart.

## J. Untouched debt (recorded, not fixed)

- Scheduler `wakeSeq`/`traceWakeSeq`/`maxTraceWakeSeq` all 0 in enroll leg while a step provably
  ran (per-bot wake counters vs trace stamps still unmatched) — scheduler lane's open item.
- `ActivateHeadless` → `EnterWorld(character, null)` clobbers the TCP back-pointer (log line
  `PlayerBot embodied (id 15, objId 44283)` confirms the path ran).
- `deactivate` for networked chars refused again (`InvalidOperationException`; TCP disposed,
  manager/arbiter/executor registry residue grows by one entry per run).
- `StepAsync` self-reschedules while a route is live (cadence after armed travel) AND the
  arbiter flips quest.progress → recovery.rest: bots walk armed routes with no further quest
  ticks, while the stale `QuestTravelReason` keeps satisfying the test's per-bot wake proof.
  Harness should join wake proof to `CycleId`/trace stamps, not to the persisting reason.
- `ResolveQuestTravelTarget` uses 3D `Vector3.Distance` while discovery gates on 25 m flat.
- Pre-reboot char-12 trace remains ambiguous (no diagnostics then) — do not cite it as staged
  discovery evidence.

## K. Exactly-one next step

Manually drive the fixture ops once against a fresh login session (throwaway script, read-only,
no redeploy, no test change) and record the `teleportToNpc(3512)` RESPONSE (`spawnerId,x,y,z`)
plus the immediate (pre-walk) `charPos`: response ≈ 3597's coords proves the spawner lookup
returns the wrong spawner (then fix the lookup: deterministic spawner resolution + response
assertion); response ≈ 3512's coords proves post-teleport transit (then fix the fixture timing:
re-stage after the enroll route settles or clear the route pre-stage). Until that discriminates,
no production or fixture code changes.
