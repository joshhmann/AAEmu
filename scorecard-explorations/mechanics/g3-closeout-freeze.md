# G3 Closeout Freeze — PASS-BEHAVIOR (2026-09-20, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (ports 2237/2239/2250/2260/2234/2280/3311,
`COMPOSE_PROJECT_NAME=q0pilot`, token `e2e-q0-pilot-token`). This is a
documentation/reconciliation/freeze record. Zero behavior change in this pass.

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-20.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g3-truncfix-gate-report.json`
  (`gate_rerun_verdict=PASS-BEHAVIOR`, `classification=PASS-BEHAVIOR`), chained to
  `/root/aaemu-e2e-q0/logs/g3-refill-gate-report.json` (`gateVerdict=PASS-BEHAVIOR`,
  `failBoundary=none`, charId=22).
- **PASS-BEHAVIOR chain fields (truncfix rerun):** 3512 evaluated FIRST
  (`evaluated_targets=[44322,44323,44358]`, giver index 0 @ 0.0 m flat, raw enum #8);
  `44322:Completed:offers=251L2`; dispatch `AcceptQuest/Completed
  [quest 251 accepted (Npc/3512)]`; `active251=true`, `accepts=1`, `questActions=4`;
  wake `PROVEN per-bot charId=22`; `startFlatM=0.0`.
- **Fix reference:** `AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs`, sweep loop
  (report: old L113-148 → new L113-158) + comment (old L89-93 → new L89-92).
  Diff verification (read-only, direct read — the file is extraction-landed
  untracked, so `git diff` stat is empty by construction): **sort-before-take YES**
  (`ordered = candidates.OrderBy(flat).ThenBy(ObjId)`, take-3 cut at L151-156 runs
  over the ordered sequence); **deterministic ObjId tiebreak YES** (`ThenBy(n =>
  n.ObjId)`, L150); **unrelated semantic change NO** (same `MaxDiscoverTargets`
  cap, band/active filters, selector, dispatch, legality gates; raw diagnostics
  `rawCount/rawShown/rawIds` stay enumeration order; actorPos-missing fallback
  keeps enumeration order). Build/deploy per report: Game Release 0 errors,
  republished binary + supervised restart (pid 2540765), readiness confirmed.

## (b) Causal timeline (every stage HISTORICAL/SUPERSEDED except the final)

1. **Wrong fixture — HISTORICAL/SUPERSEDED.** Quest 251 ∉ offers(2425); 2425 offers
   only L30 (out of band [1,9]). G3-with-giver-2425 could never pass. Fixture
   corrected to true giver 3512 (Start component 383 → ConAcceptNpc 3512; spawner
   3409 @ 15655.91/15172.51). Record: roadmap §1, nearby-quest-contracts §4.
2. **Global-counter wake proof — HISTORICAL/SUPERSEDED as sole proof.** Per-bot
   `wakeSeq` decorator counter lane-proven (1→92) but enroll poll could exit on a
   leftover's global step; `maxTraceWakeSeq` deployed but unproven-live. Replaced
   by fresh-signal START-tick proof (decideChanged/actionsGrew/seqGrew/legLit +
   server log). Record: roadmap §2/§4.
3. **Bootstrap config — HISTORICAL/SUPERSEDED as a suspect.** Game-side
   `AAEMU_QUEST_BOOTSTRAP_ENABLED=1` verified present in the live game process
   environ on every gate run; never the blocker. Record: nearby-quest-resolution §B.
4. **Route-settling teleport — HISTORICAL method, retained doctrine.** Live enroll
   route (spawn → 3597 spawner) decayed the START precondition (baseline drift
   13.4 m); stop-seam UNAVAILABLE (`/api/actors/stop` → 404, control gate
   disabled); settle-to-arrival poll (standstill < 0.5 m, 60 s bound + 5 s no-wake
   window) → `settled=True rearmed=False`, START 0.014 m, drift 0.0 m. Record:
   g3-stable-start-gate §A-D.
5. **Recovery arbitration — HISTORICAL/SUPERSEDED verdict, retained finding.**
  `quest.progress → recovery.rest` flip at the START window (charId 19) via the
  additive `arbiterActivity` field; proximate (then-)FAIL-ARBITRATION at
  ARBITRATION/no-quest-activity (HISTORICAL, superseded by §§6-8). Record: g3-arbiter-gate §B-C.
6. **Stale vitals — HISTORICAL/SUPERSEDED as blocker.** Leading hypothesis
   (`setLevel(10)` raised MaxHp without refill → HP fraction < 0.75) confirmed by
   refill probe (hpBefore 720/2286); HP+MP refill leg → fractions 1.000/1.000 →
   `PROCEED-DISCOVERY`, activity held `quest.progress` throughout. Record:
   g3-refill-gate-report.json fixture (`refillOk=true`, `arbiterGateVerdict=
   PROCEED-DISCOVERY`).
7. **Truncation — HISTORICAL root cause, FIXED.** First-3-take in provider
   enumeration order cut the giver (enum #8 of raw=12); rawIds diagnostics proved
   evaluated set `[37257,44311,44319]`, giver absent. Ordering-only fix (§a) →
   evaluated `[44322,44323,44358]`, giver first → accept. Record:
   g3-rawids-gate-report.json (`classification=TRUNCATION`), truncfix report
   §`sweep_order_observed`.
8. **PASS — FINAL, FROZEN.** PASS-BEHAVIOR chain (§a). No further gate stages.

## (c) Proves / does-NOT-prove boundary

Proves:
- One staged bot at the true giver (0.0 m, giver live) autonomously accepts
  in-range in-band quest 251 through the executor live actor (direct
  `GameplayActor.AcceptQuest` dispatch, not via `BotActionCommandQueue`).
- Per-bot START wake attribution (fresh quest-tick signals + server log, not the
  stale enroll travel reason, not global `TotalStepsRun`).
- Deterministic discovery ordering (nearest-first flat + ObjId tiebreak) before
  the take-3 cut; raw diagnostics stay enumeration order.
- Arbitration yields `quest.progress` at START when vitals are refilled
  (recovery-withholding hypothesis confirmed and retired by refill, not by code).

Does-NOT-prove (explicitly out of scope, no claim):
- Navigation to the giver (out-of-range gate NOT RUN; travel fallback still
  executor-owned straight-`MoveTo`, navmesh dead code on the quest path).
- Talk/kill objective pursuit (no `Talk`/`InteractNpc` dispatch in the quest leg;
  talk-objective quests stall by design — contracts §1.5).
- Turn-in, credit, combat, any other quest, multi-bot scale, persistence,
  planner behavior.

## (d) Freeze rule

G3 is FROZEN at PASS-BEHAVIOR. No new features, no new acceptance criteria, no
new journey stages on the G3 path. Regressions may REPAIR ONLY: a regression
restores the frozen chain (3512-first → 251L2 → AcceptQuest/Completed → active)
without widening scope; any widening is a new gate with its own fixture, not a
G3 follow-on. Historical FAIL/UNKNOWN labels elsewhere stay as run records and
MUST be read as superseded by this freeze (roadmap §4 annotated accordingly).

## (e) Reusable E2E doctrine

1. **Actor identity first:** prove `snap==actor==spawner` and single-`Character`
   (`botSameChar`) before any production verdict; dual-object divergence voids
   the run, not the code.
2. **Per-bot wake proof, never global:** join wake→terminal on fresh per-bot
   signals (`QuestDecideDetail` overwrite + CycleId, audit-row growth, leg-lit)
   plus server-log tick; global step counters and persisting travel reasons are
   contrast-only and MUST NOT prove a START tick.
3. **Stable START:** settle live routes before staging (stop-seam attempt, then
   settle poll with re-arm guard); gate START on measured flat range (≤ 25 m)
   plus zero drift-since-teleport with the live-resolved giver objId.
4. **Fixture normalization:** verify giver linkage (Start component →
   ConAcceptNpc), band/level eligibility, spawner singularity, and live
   materialization; refill vitals after `setLevel` (HP/MP fractions ≥ 0.95)
   before attributing arbitration outcomes.
5. **Funnel diagnostics:** every silent `continue` gets a tally; DECIDE detail
   carries parser-safe `raw/trunc/actor/zone/region/outcomes` keys; rawIds census
   rides inside the detail cut so truncation is observable, not inferred.
6. **First-zero rule:** name the earliest failing predicate (`sweep-empty` /
   `no-offerings` / `all-filtered` / arbitration-withheld) and stop; never chase
   downstream predicates past a failing upstream one.
7. **Deterministic ordering before truncation:** any take-N cut over world
   enumeration MUST sort (distance + ObjId tiebreak) first; enumeration order is
   region insertion order, never proximity.

## (f) Architectural debt (follow-up ONLY — not G3 work)

- Navigation-intent survival: behavior cannot request movement; travel is
  executor-owned with no intent channel or per-purpose arrival radius.
- Behavior ownership/cancellation: runtime `Cancel` is observation-only;
  preemption stays with the owning leg via `PreemptCurrent`.
- Arbiter utility: priority+eligibility first-allow-wins; no utility, commitment,
  or hysteresis (Phase 4).
- Per-bot `CycleId` primitive: wake→terminal join key exists for quest; not yet
  a universal harness join across behaviors.
- Movement cancellation: `teleportToNpc` does not cancel a live Move; stop-seam
  gated behind `AAEMU_BOT_CTRL`.
- 3D-vs-flat: travel target scan uses 3D `Vector3.Distance`; discovery gates on
  25 m flat.
- World-aware nav: route pump uses straight `MoveTo`, never `NavigateTo`;
  navmesh/detour machinery dead on the quest path; no fail-fast UNREACHABLE
  terminal.

## (g) G4 handoff

Starting state: quest 251 ACTIVE on the bot (this freeze's end state). Question:
objective identification and valid-hostile-target identification for 251's first
progress step (which objective the step machine serves next, and which live
target — if any — the engine legality gates accept for it). Ending state: target
identified + selected (a named live objective target the step path can consume).
No travel, no combat execution, no objective credit, no turn-in — G4 stops at
identification/selection.
