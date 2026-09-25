# G5 Pursuit Semantics — §§D–I (READ-ONLY audit, develop @ 2026-09-20)

READ-ONLY. Nothing modified, built, or run. G3/G4 frozen — untouched (cited, never edited).
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
G5 mission: selected 3475 target → approach/maintain pursuit to combat-engagement range, stop before attack.
G6 (out of scope here) starts autoattack / an a-skill once G5 holds range.
Read-first context: `gap-nav-persist.md` (§1 nav contract + §1.5 gaps), `brain-boundaries.md`
(§3 nav proposal), `g4-perception-targets.md` (§D resolver sites, §E CanAttack ownership).
`UNKNOWN` = not found in code, nothing inferred. `[INFERENCE]` = derived, marked as such.

---

## D. RANGE AUTHORITY — no canonical range; skill/weapon-dependent; no shared "can-attack-from-here"

### D.1 Engine truth is post-hoc refusal, not a query

`Skill.Use` (`AAEmu.Game/Models/Game/Skills/Skill.cs`) computes `skillRange` from
`ApplySkillModifiers(..., Template.MaxRange)` (`:219`), `targetDist` via
`unit.GetDistanceTo(target, true)` (`:220`), then:

| Engine check | Result | Cite |
|---|---|---|
| `targetDist < minRangeCheck` | `TooCloseRange` | `:250-256` |
| `targetDist > maxRangeCheck` (non-doodad/slave) | `TooFarRange` | `:261-266` |
| weapon override (`WeaponSlotForRangeId > 0`): min/max REPLACED by `HoldableTemplate.Min/MaxRange` | per-weapon band | `:236-248` |
| no weapon slot: fist default max 3.0 m | `maxWeaponRange = 3.0f` | `:238-239` |
| hostile-target gate (`SkillTargetType.Hostile`): relation not Hostile/Neutral AND `!CanAttack` → null target → `NoTarget` | legality inside range check | `:423-445`, `:173-183` |

There is **no engine "can I attack from here" query** — the engine answers only by refusing
a cast already attempted. Any brain-side range predicate is therefore an approximation.

### D.2 The three brain-side approximations (all advisory, none shared)

1. `CombatDecisionTree.IsSkillInRangeAndReady` (`AAEmu.Game/Core/Managers/Bots/CombatDecisionTree.cs:233-274`):
   cooldown (`:243-244`) + mana (`:252-253`) + `GetSkillEffectiveRange` distance band (`:257-259`),
   `GetKnownSkillRange` fallback when `SkillManager` is unloaded (`:265-271`). G4 already ruled:
   order-by at most, never gate legality on it (`g4-perception-targets.md` §E.2).
2. `GetSkillEffectiveRange` (`:162-182`): template min/max, weapon-slot override via
   `HoldableTemplate` (`:167-174`), **zero-max default 4.0 m** (`:177-179`).
3. `GetKnownSkillRange` fallback table (`:187-228`): melee autoattack skill 2 → `(0, 4)` (`:224`),
   ranged autoattack skill 4 → `(4, 20)` (`:225`), default `(0, 20)` (`:226`). Headless/mock only.

Engagement-band constants in play:

| Constant | Value | Cite |
|---|---|---|
| `HuntMeleeRange` (roam chase stop-to-cast) | 3.0 m | `BotRoamStepExecutor.cs:118-119` |
| `HuntChaseSpeed` | 4.5 m/s | `:121-122` |
| `DefaultMeleeMin/Max` (tree) | 1.0 / 3.5 m | `CombatDecisionTree.cs:58-59` |
| GOAP `TargetEngagementRange = DefaultMeleeMax` → `TargetInRange` iff 3-D dist ≤ 3.5 m | 3.5 m | `BotWorldStateProvider.cs:37`, `:209-214` |
| Roam hunt engage: Melee role → 3.0 m, else → 15.0 m | 3.0 / 15.0 m | `BotRoamStepExecutor.cs:775-776` (PvP mirror `:2395-2396`) |
| `InferRole`: Wild→RangedPhysical, Magic/Death/Illusion→RangedMagic, Love/Romance→Healer, else Melee | — | `CombatDecisionTree.cs:279-290` |
| NPC-side: `range = AttackStartRangeScale (× _maxWeaponRange if UseRangeMod)`; base-skill-2 fixup | per-template | `BaseCombatBehavior.cs:132-142` |
| `NpcTemplate.AttackStartRangeScale` | per-template data | `NpcTemplate.cs:42`, read at `NpcManager.cs:579` |

NPC bounding radius is real but generic: `GetDistanceTo` subtracts the target's actor-model
radius AND the caster's radius (`BaseUnit.cs:205-235`, esp. `:228-232`), `Unit.ModelSize =
Model.Radius × Scale` (`Unit.cs:48-54`). **Boar-3475-specific radius: UNKNOWN** (the
`ModelManager` actor-model row for 3475's model was not read; sqlite absent in this checkout).
`Skill.cs` itself carries `TODO: Do a check based on model size or bounding box instead` (`:232-233`).

### D.3 What `AutoAttack` (the likely G6 entry) actually gates

`GameplayActor.AutoAttack` (`GameplayActor.cs:705-774`): resolve (`:715`, via `ResolveUnit :4753-4760`)
→ **null-or-dead reject** (`:716-717`) → set+broadcast `CurrentTarget` (`:720-724`) → measure flat
dist (`:727`) → `InferRole` (`:728`) + ranged-weapon presence (`:729-730`) →
**skill 4 iff `role == RangedPhysical || (dist > 4.0f && hasRangedWeapon)`, else skill 2**
(`:732-734`; ids `BasicMeleeAutoAttackSkillId = 2`, `BasicRangedAutoAttackSkillId = 4`,
`CombatDecisionTree.cs:111-112`) → template-exists reject (`:751-753`) → `skill.Use(...)` (`:767`,
engine range refusal §D.1 applies here) → `StartAutoSkill` loop (`:769-771`).
`Cast` gates only template-exists + learned + resolve (`:576-594`); range/cooldown/mana are left
to engine refusal (`:609-622`), retried (if at all) by `CombatExecutor` — `CooldownTime` is the
ONLY retryable result, `MaxAttempts = 3`, `WaitBudget = 2 s` (`CombatExecutor.cs:29-34, :57-63, :181`).

Non-combat ranges for contrast (pursuit must not confuse these with engagement):
`MaxInteractRange = 25 f` (`GameplayActor.cs:1461`) enforced by Talk (`:992-994`), InteractNpc
(`:1092-1094`), DiscoverQuests (`:1196`, `:1219-1222`), Interact (`:1504-1505`), Harvest
(`:1535-1536`); `MaxHarvestInteractRange = 3.0f` defined (`:1464`) but the Harvest gate reads
`MaxInteractRange` (`:1535-1536`) — apparent dead/alt constant, noted not resolved;
`MaxShopRange = 3 f` (`:2758-2759`, check `:2780-2782`); craft bench `skillTemplate.MaxRange
else DefaultCraftRange = 5 f` (`:3729-3730`, `:3798-3801`).

### D.4 Verdict + G5 stop-range

- **One canonical range? NO.** Range is skill×weapon×role-dependent; no shared
  "can-attack-from-here" helper exists (three approximations §D.2 + engine post-hoc refusal §D.1).
- **Existing method to reuse:** `IsSkillInRangeAndReady` for ORDERING only; `GetSkillEffectiveRange`
  once G6 names its skill; engine `SkillResult` (`TooFarRange`/`TooCloseRange`) as ground truth.
- **G5 stop-range if G6 opens with autoattack skill 2: flat distance ≤ 3.0 m**
  (`HuntMeleeRange`, `BotRoamStepExecutor.cs:118-119`). Rationale: 3.0 m sits inside EVERY melee
  authority — `DefaultMeleeMax` 3.5, skill-2 fallback `(0, 4)`, fist-default max 3.0 (boundary),
  GOAP `TargetInRange` 3.5 — while the engine template row for skill 2 stays UNKNOWN (sqlite
  absent), so the minimum of the brain authorities is the honest pick.
- **Ranged-role tension (flagged, not solved):** skill-4 fallback min is 4.0 m (`:225`); a
  RangedPhysical-role bot holding 3.0 m may eat `TooCloseRange` from the engine. Starter-bot
  `Ability1` was not grounded this pass → G6 must confirm the actual role/skill before widening
  the stop band.
- **If G6 opens with an a-skill instead:** stop-range source = `GetSkillEffectiveRange(template,
  bot)` (`CombatDecisionTree.cs:162-182`) for THAT skill id — UNKNOWN until the skill is named.

---

## E. MOVING-TARGET SEMANTICS — snapshot-once proven; no retrack, no replan

### E.1 Snapshot-once proof (dispatch cites + Tick verified-absence)

- `MoveToUnit` (`GameplayActor.cs:482-495`): `ResolveUnit` (`:491`) → null-reject (`:492-493`) →
  `StartMove(request, unit.Transform.World.Position, speed)` (`:494`). The live unit is discarded;
  only coordinates enter `_moveTarget`.
- `NavigateToUnit` (`:343-356`): identical resolve (`:351-353`) → snapshot into
  `NavigateToInternal` (`:355`).
- `IGameplayActor` contract words it: "resolved at request time" (`IGameplayActor.cs:107-115`).
- **Tick never re-resolves (verified absence):** the Move branch (`:4181-4269`) reads only
  `_moveTarget` / `_moveWaypoints` / `_unstickWaypoint` (`:4181`, `:4188`, `:4204-4216`,
  `:4230-4238`); across `:4084-4307` there are zero `ResolveUnit`/`GetUnit`/`CanAttack`/
  `CanSeeTarget`/`Hp`/`ParentWorld`-write references. (All `ResolveUnit` call sites are
  dispatch-time: `:351`, `:491`, `:548`, `:592`, `:715`, plus non-movement `:912`, `:1724`,
  `:1834`, `:1910`.)
- **No repath either:** `_moveWaypoints` is WRITTEN only at dispatch (`:382` navmesh, `:402`
  detour, `:432` null-on-direct, `:474-477` test seam) and DEQUEUED in Tick (`:4210` blend,
  `:4233` advance); nothing recomputes on obstacle-appear, navmesh-update, target-teleport,
  or target-move.

### E.2 Behavior table (target moves / dies / despawns / zones / hides / turns friendly / unreachable)

| Event mid-leg | Leg behavior | Terminal |
|---|---|---|
| Target moves | walks to stale snapshot | `Completed("arrived")` at stale point (`:4240-4244`) — success-wrong |
| Target dies (`Hp ≤ 0`) | no Hp read in Tick → same stale walk | same `Completed("arrived")` |
| Target despawns (`RemoveObject` unregisters `_units`, `WorldInstance.cs:598-612`) | leg holds coordinates, unaffected | same `Completed("arrived")` |
| Target zone-changes | same (leg has no target link at all) | same |
| Target goes invisible/stealthed | no `CanSeeTarget` in Tick | same |
| Target turns non-hostile (faction flip) | no `CanAttack` in Tick | same |
| Destination unreachable / walked into geometry | stuck detector (§H) | `TimedOut(Navigation, "stuck: no progress…")` (`:4437-4441`) |
| Destination far but walkable | walks to budget | `TimedOut(Navigation, "navigation budget exceeded")` (`:4096-4106`) |
| Self zone-change mid-leg | UNKNOWN — Tick contains no `ParentWorld` write and no zone-transfer call; `FinalizeTransform` delegates to `SetPosition` with no world switch in the audited range (`Transform.cs:466-507`) | UNKNOWN |
| Target already at snapshot within arrival box at dispatch | full-lifecycle no-op | `Completed("already at destination")` (`:369-370`, `:427-428`) |

The only in-repo compensations are brain-side and local: roam drift re-issue
(`PendingLeg.Destination` vs live pos > 2.0 m → `Stop` + `MoveTo`, `:781-790`; party-follow
3.0 m variant `:602-611`), short chase budgets (5 s `:579`, 10 s `:789`/`:924`, PvP 10 s
`:2405`), 30 s engagement cap with per-wake dead/invalid revalidation (`:712-722`).

### E.3 Verdict

**SNAPSHOT-ONCE (retrack absent).** G5 MUST NOT claim pursuit for stale-coordinate walking:
arrival `Completed("arrived")` proves coordinates were reached, nothing about the target.
G5's legitimate claim shape: per-wake live revalidation (the `QuestObjectiveTargetSelector.Evaluate`
pattern — resolve `ParentWorld.GetNpc`, filter dead/template/hostile/attack/stealth, nearest-first,
`QuestObjectiveTargetSelector.cs:141-220`) + drift re-issue on the roam precedent (> 2 m) +
count "in pursuit" only while the target live-resolves within stop-range at terminal time.

---

## F. VALIDITY DURING PURSUIT — dispatch-gated, mid-leg absent; recycling risk recorded

### F.1 Checklist (available check → dispatch use → mid-leg use)

| Check | Primitive | Dispatch (fail-closed) | Mid-leg (Tick) |
|---|---|---|---|
| Exists | `ResolveUnit` → `ParentWorld?.GetUnit` (`:4753-4760`); `GetUnit` bare-dict lookup (`WorldInstance.cs:456-459`) | `*ToUnit` null→`RejectedAction` (`:352-353`, `:492-493`); `SetTarget` (`:548-550`); `Cast` (`:592-594`) | NONE (§E.1) |
| Alive | `IsDead ⇔ Hp <= 0` (`Unit.cs:946-952`) | `AutoAttack` null-or-dead reject (`:716-717`) | NONE |
| Visible | `CanSeeTarget`: `IsVisible` + no stealth tag (`BaseUnit.cs:145-151`) | G4 selector stealth filter (`QuestObjectiveTargetSelector.cs:193-198`); survey-only elsewhere (`BotSurveySenses.cs:250-251`) | NONE |
| Hostile/legal | `CanAttack` (`BaseUnit.cs:54-138`); `IsHostileTarget` (`CombatDecisionTree.cs:73-102`); roam `IsAttackableWildlife` (`BotRoamStepExecutor.cs:2325-2348`) | G4 selector hostile+attack filters (`:181-192`); roam revalidation (`:719-722`) | NONE |
| Quest-relevant | `IsRelevant`: 251 active + 4058 < 3 (`QuestObjectiveTargetSelector.cs:101-108`) | selector funnel (`:116-231`); proposal priority (`QuestBehavior.cs:399-426`) | NONE (correct — brain-owned) |
| Same-world | registries are `ParentWorld`-scoped (`:4759`; `WorldInstance.cs:456-459`) | NO guard in any Move verb (signatures carry bare `Vector3`/objId, `IGameplayActor.cs:96-115`) | NONE; sole same-world guard in repo is roam party-follow (`leader.ParentWorld == …`, `:561`) — the exception proving the absence |

### F.2 Bare-ObjId staleness / recycling — recorded, NOT solved

Ids ARE recycled: `ReleaseId` frees the bit (`IdManager.cs:153-172`, `_nextFreeId` rewind
`:165-166`), `GetNextId` reissues the lowest free id (`:180-214`); `ObjectIdManager` spans
`0x100–0xFFFFFFFE` (`ObjectIdManager.cs:8-14`). Despawn removes the registry entry
(`WorldInstance.cs:608-609`) so a dead/despawned id resolves to null (safe direction: stale
pursuit fails at next dispatch revalidation). But a RECYCLED id resolves via a bare
dictionary hit (`:456-459`) with **no generation check** — a stale objId can bind a DIFFERENT
unit. Per mission scope, universal identity is NOT solved here; G5 mitigates (never stores
objIds acrosslegs without revalidation: template + alive + hostile + visible + nearest-ordering
per wake, the `Evaluate` shape) and records the residual.

---

## G. ROUTE OWNERSHIP / CANCELLATION — actor owns the leg, caller owns the intent

### G.1 Field ownership

- Actor owns: `_active` single-writer (`TryBegin`, `:4640-4671`; busy → accept-then-`Rejected
  StateTransition "actor busy with …"` `:4642-4646`); movement state `_moveTarget` /
  `_moveWaypoints` / `_moveSpeed` (`:4039-4041`), trapezoid state (`:4043-4051`), stuck tracking
  (`:4058-4061`), drive state (`:4063-4065`); cleared on EVERY terminal path via
  `ClearMovementState` (`:4509-4519`) — arrival (`:4243`), stuck (`:4440`), budget (`:4104`),
  interrupt (`InterruptActive :4673-4685`, also clears craft `:4684`).
- Queue owns: `_history` terminal records (`BotActionCommandQueue.cs:251`), `_apiOwned`
  in-flight map (`:259`), API-vs-internal arbitration (§G.2).
- Brain owns (by precedent, never by actor reach-in): roam `Path` + `PendingLeg` + `TargetNpcObjId`
  (`BotRoamStepExecutor.cs:221-222, :234-235`); GOAP `CurrentActorRequest` (+ plan/index/retries,
  `GoapPlanRunner.cs:23-33` per `brain-boundaries.md` §1.1); `CombatExecutor._pending` single-cast
  (`CombatExecutor.cs:80`, `:94-102`, `Cancel :194-198`) — cast layer, not movement.

### G.2 Who cancels what, and what it costs

| Canceller | Mechanism | Record | Cite |
|---|---|---|---|
| Any caller, movement-aware | `Stop()` — `BroadcastStop` if walking + `InterruptActive` + own `Completed("stopped")` request | audited stop | `:497-520` |
| Behavior abandoning its own leg | `PreemptCurrent(reason)` — same halt broadcast, NO new request; **caller must clear its reference** (`PendingLeg = null` / `CurrentActorRequest = null`, "notifies by return value, never by reaching into behavior state") | interrupted leg keeps its own `Interrupted` record | `:523-536`; contract `IGameplayActor.cs:123-137` |
| Queue control plane | `Interrupt(traceId)` — exact-trace match only; mismatch/terminal → `false`, silent no-op | interrupted leg's terminal | `:797-803`; queue mapping `:635-649` |
| New API command vs running API leg | queue busy gate → `Rejected(StateTransition, "actor busy with {Move}")`; caller polls to terminal first | rejection | `:440-448` |
| New API command vs internal leg (roam/scenario) | queue preempts via `actor.Interrupt` so the command lands | `Interrupted` (actor-audited) | `:451-455` |
| Scheduler stopped / actor budget | actor budget (`:4096-4106`) + queue backstop over `_apiOwned` only (`:369-410`, `Expire :405`) | `TimedOut` | — |

### G.3 Behavior-side precedents (the patterns G5 copies, not the code)

- Range-reached: `PreemptCurrent("hunt in engage range")` + `PendingLeg = null` (`:794-798`).
- Target-change (new best): `Stop()` + `PendingLeg = null` + rebroadcast (`:867-871`).
- Death/invalid/30 s-cap: `PreemptCurrent("hunt target down")` + `PendingLeg = null` + clear
  `CurrentTarget` broadcast (`:724-764`).
- Drift: `Stop()` + reissue `MoveTo` (`:785-790`).
- GOAP: `DropInFlightRequest` → `PreemptCurrent` + null (`GoapPlanRunner.cs:49-54`); approach
  invalidated on lost/dead target (`CombatActions.cs:66-68`); approach itself dispatches
  `MoveToUnit(CurrentTarget)` (`:53-58`) — snapshot-once, same §E caveat.
- Target-selection change effects: the actor NEVER follows — no subscription exists. A stale leg
  keeps walking unless its owner stops/preempts it. Abandoning a running movement without
  `Stop`/`PreemptCurrent`/`Interrupt` leaves it live until arrival/stuck/budget.

### G.4 Smallest G5 ownership boundary (proposal, no implementation)

Brain (G5) owns: selection→dispatch (`MoveToUnit`/`NavigateToUnit` on the G4-selected objId),
per-wake revalidation (Evaluate-shaped), `Stop`-on-range-reached (audited halt, dossier §1.6
standstill), `Stop`+reissue-on-drift (> 2 m precedent), abandon via `Stop` (audited) or
`PreemptCurrent`+null (silent) on target-change/goal-change. Actor owns: single-leg stepping,
stuck detection, terminals. Queue owns: API arbitration (untouched). G5 writes NOTHING to
`_move*`/`_active`/`ClearMovementState` and mints no new queue kinds (matrix §C precedent:
`Navigate` kinds are queue-only already, `BotActionCommandQueue.cs:546-564`).

---

## H. STUCK / REPATH — one bounded nudge, then fail-fast; repath absent

- Detector (`UpdateMoveStuckState`, `:4408-4442`): 3-D displacement `Vector3.Distance(position,
  _lastProgressPosition) > ArrivalRadius` resets (`:4415-4420`); else accumulates (`:4422-4424`);
  `NoProgressWindow = 2.5 s` (`:116`, `:130`) vs `DefaultMoveTimeout = 30 s` (`:78`) — stuck
  always lands first. `≤ Zero` disables (test seam, `:4412-4413`) → full-budget ride.
- Recovery: ONE lateral nudge, 2 m (`UnstickNudgeDistance :119`), alternating sides
  (`BuildUnstickWaypoint :4450-4463`, sign `:4456`), `MaxUnstickNudges = 1` (`:137`), steered as
  `legTarget` (`:4188`); waypoint reached → resume with fresh tracking (`:4220-4228`).
  Exhausted → `Expire(Navigation, "stuck: no progress {t}s")` (`:4437-4441`) + halt + clear.
  (`Expire`-not-`Interrupt` because `Interrupt` carries no §17 reason — `:4395-4401`.)
- Identical for ALL FOUR verbs: single Tick branch (`:4181-4269`), all `NewRequest(... Move ...)`
  (`:321`, `:336`, `:347`, `:487`), queue kind-map folds Move/MoveToUnit/Navigate to `Move`
  (`:895`). Only the route SEEDING differs (navmesh `:373-394` / detour `:397-409` / direct).
- `DriveVehicle` DIFFERS: branch `:4272-4306` contains NO `UpdateMoveStuckState` call —
  budget-only expiry; a beached ship burns the full 30 s (gap-nav §1.5.5 concurring).
- Unreachable: NEVER `Rejected` — no reachability preflight exists (`NavigateToInternal :358-412`
  degrades navmesh-miss → detour → straight line with no degraded-signal); `ApplyCharacterMove`
  (`:4318-4350`) sets position with no collision check (mate/broadcast branches only). Cost per
  bad leg ≈ 2.5 s + 1 nudge → `TimedOut`. Z-mismatch: arrival needs `|ΔZ| ≤ 0.5` (`:4218`), Z
  stepped proportionally (`:4253-4268`, fraction `:4260`); no terrain clamp in the actor path
  (ground clamp is roam-side, `:1040-1064`).
- **What G5 may legitimately prove:** bounded failure (stuck/budget terminals with
  `Navigation` reason), dispatch-time rejection for gone targets, arrival-at-live-target.
  **Robust repath is ABSENT — stated plainly.** Any retry policy lives brain-side, bounded
  (`CombatExecutor` MaxAttempts/WaitBudget precedent), never unbounded re-issue.

---

## I. LOCAL VS STRATEGIC — partial honor; local pursuit sufficient at ≤45 m; cross-zone OUT

- Implementation honors the split PARTIALLY: `MoveTo` = `StartMove` direct (`:329` → `:418-437`);
  `NavigateTo`/`NavigateToUnit` = `NavigateToInternal` (`:340`, `:355`) with `GeoDataMode`-gated
  A* (`:373-380`, `Count > 1` gate `:380`), obstacle detour (`:397-409`), silent straight-line
  fallback (`:411`). The interface discloses the fallback (`IGameplayActor.cs:98-115`).
  Unimplemented (still proposal, `brain-boundaries.md` §3): moving-target re-resolve, mid-leg
  repath, world-key guard, reacquisition/`TargetGone` vs `Unreachable` vs `WrongWorld` split.
- **Staged-nearby 3475 (≤45 m): local pursuit is sufficient (YES).** One `MoveToUnit` leg runs
  the same proven Tick branch; at this scale failure modes are bounded by the stuck detector
  (§H), not by planning. `NavigateToUnit` adds value ONLY where navmesh exists — GeoData
  availability in this deployment is UNKNOWN (`GeoDataMode` config + `.bai` presence not
  verified this pass) — and is otherwise the identical fallback leg.
- **Cross-zone / portals / ships stay OUT, with justification:** destinations are bare
  `Vector3`/objId with no world/instance key (signatures `IGameplayActor.cs:96-115`; requests
  carry no instance id); every lookup is `ParentWorld`-scoped (`:4759`; `WorldInstance.cs:456-459`);
  Tick never re-zones (verified absence §E.1); vehicle motion is `Drive`-only (`:2165+`,
  budget-only expiry §H) and ferry/slave-transfer seats have no Move-path branch (only the mate
  branch `:4330-4336`). Interzone needs the unimplemented `WrongWorld` guard — out of G5 scope
  by contract absence, not by choice.
