# Arc Utilization Index — Q0→G8c chain, harness hardening, and discovery lanes (2026-09-23, branch develop)

Lane referenced below: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (ports
2237/2239/2250/2260/2234/3311/1280, `COMPOSE_PROJECT_NAME=q0pilot`, token
`e2e-q0-pilot-token`). This is a documentation/utilization record. **Zero behavior
change in this pass**: no source, test, fixture, lane, or gate file is modified here.
No gate was re-run; every verdict below is *quoted from an existing artifact* or from
the session note that recorded the change, and the freeze docs remain authoritative
for their own gates. Where a claim is not backed by an artifact it is marked
`[INFERENCE]`.

Purpose: the 2026-09-20→09-23 work produced (i) a fully proven quest-251 loop, (ii) a
harness hardening program, (iii) several verb/instrumentation seams, and (iv) two
discovery lanes. All of it lives in scattered session notes, lane reports and dirty
source. This file is the single map from *item → status → evidence → next action* so
the work can actually be utilized rather than re-derived.

---

## (a) Status labels (how to read the index)

| Label | Means |
|---|---|
| **FROZEN-PASS** | A gate passed and has a written frozen contract; no new features/criteria on that path. |
| **PROVEN-LIVE** | The behavior was observed on the live q0 lane with a report on disk; not (yet) a frozen gate contract. |
| **LANDED-BUILD-CLEAN** | Implemented and compiler-verified; the *new* behavior has no live observation yet. |
| **DESIGN** | Specified/designed or audited; not implemented, or implemented-and-not-run by design. |
| **OPEN** | Gap without a converged run or without an accepted design. |

A label may carry a parenthetical qualifier that narrows it honestly (e.g.
`FROZEN-PASS (read-only evidence, no gate)`). Nothing here upgrades an evidence layer.

**Session-note root** (referred to below as `MUSE-<file>`):
`/root/.omp/agent/sessions/-aaemu-dev/2026-09-18T19-36-15-155Z_01a0b604-fc33-7000-8257-d2497359935f/`

---

## (b) Index — every item in the arc, one row each

| # | Item | Status | Primary evidence |
|---|---|---|---|
| 1 | Quest-251 chain Q0→G8c | **FROZEN-PASS** (G3–G8c) with one OPEN leg (G7c) | §c.1 |
| 2 | Harness methodology audit (A–M) | **DESIGN** (audit complete; recs 3–10 unimplemented) | §c.2 |
| 3 | K1 first-zero fail-fast | **LANDED-BUILD-CLEAN** (6 gates; never fired live) | §c.3 |
| 4 | K4 report/artifact preservation | **PROVEN-LIVE** (17 writers) | §c.4 |
| 5 | G7x scanner narrowing | **LANDED-BUILD-CLEAN** (G5 pilot + G7a–d; no run since) | §c.5 |
| 6 | Leash re-anchor fix | **PROVEN-LIVE** | §c.6 |
| 7 | Loop-release seam (`IsAutoAttack` Loot withhold) | **PROVEN-LIVE** (diag arm observed; release arm not yet) | §c.7 |
| 8 | Witness back-dating (G7a/G7b) | **PROVEN-LIVE** (G7a); **superseded** in G7b | §c.8 |
| 9 | G7b recognition-only conversion | **PROVEN-LIVE** | §c.9 |
| 10 | G7c chaining-intended relaxation + START loop-live tolerance | **LANDED-BUILD-CLEAN**; gate **OPEN** | §c.10 |
| 11 | G7d blocker-corpse staging | **PROVEN-LIVE** | §c.11 |
| 12 | Stale-proc guard rollout (all gates) | **PROVEN-LIVE** (17 gate files) | §c.12 |
| 13 | `IsMounted` observation | **LANDED-BUILD-CLEAN** (uncommitted, undeployed) | §c.13 |
| 14 | Trace archaeology (14 traces / 19 verbs / top-5) | **FROZEN-PASS (read-only evidence, no gate)** | §c.14 |
| 15 | Nav/world snapshot export feasibility | **DESIGN** | §c.15 |
| 16 | Von side-quest (decision-model service) | **OPEN** (calibrated; not usable as a classifier) | §c.16 |
| 17 | Codex side-lane (playertrace coverage / worklist) | **OPEN** (worklist landed; observation-mapping unfinished) | §c.17 |

---

## (c) Entries

### c.1 Quest-251 chain Q0→G8c — FROZEN-PASS (G3–G8c), G7c OPEN

**What it is.** The end-to-end proof that one ordinary bot accepts quest 251 at
recruiter 3512, identifies/pursues/kills boar 3475, takes corpse loot (item 4058 ×3),
earns objective credit to Ready, returns to the reporter, and turns in for the reward —
each leg owned by its own bounded gate with a frozen PASS-BEHAVIOR contract.

**Status.** G3, G4, G5, G6, G7a, G7b, G7d, G8a, G8b, G8c = frozen PASS. Q0 pilot +
scheduler microgate = PASS. Q251 sweep = PASS-CANONICAL *warm lane only*. **G7c
(loot→grant) is the one unproven leg** (§c.10).

**Evidence (reports, all under `/root/aaemu-e2e-q0/logs/`):**

| Leg | Report | Verdict |
|---|---|---|
| Q0 accept pilot | `q0-actor-accept-pilot-report.json` | PASS |
| Scheduler delivery | `scheduler-microgate-report.json` | PASS |
| G3 accept@3512 | `g3-truncfix-gate-report.json`, `g3-refill-gate-report.json` | PASS-BEHAVIOR / PASS-CANONICAL |
| G4 objective target | `g4-objective-target-gate-report.json` | PASS |
| G5 pursuit | `g5-moving-start-report.json` | PASS-BEHAVIOR |
| G6 combat | `g6-first-combat-report.20260922T173238674Z.json` | PASS-BEHAVIOR |
| G7a kill | `g7a-kill-report.json` | PASS-BEHAVIOR |
| G7b corpse recognition | `g7b-corpse-report.json` | PASS-BEHAVIOR |
| G7c loot→grant | `g7c-loot-report.json` | **NOT passing** (UNKNOWN/HARNESS/fixture-overshoot as of the last write) |
| G7d credit→Ready | `g7d-credit-report.json` | PASS-BEHAVIOR |
| G8a reporter resolved | `g8a-resolve-report.json` | PASS-BEHAVIOR |
| G8b return + dialogue | `g8b-return-report.json` | PASS-BEHAVIOR |
| G8c TurnIn + reward | `g8c-turnin-report.json` | PASS-BEHAVIOR |
| Sweep (integration) | `q251-sweep-report.json` | PASS-CANONICAL (warm lane) |

**Frozen contracts to read (never re-derive these):**
`scorecard-explorations/mechanics/g3-closeout-freeze.md`, `g4-closeout-freeze.md`,
`g5-closeout-freeze.md`, `g6-closeout-freeze.md`, `g7b-closeout-freeze.md`,
`g8a-closeout-freeze.md`, `g8b-closeout-freeze.md`, `g8c-closeout-freeze.md`.
G7a/G7d have no dedicated freeze doc; their contracts live in
`g7-decomposition.md` §J.4 (PASS shapes) plus the reports above.

**How to utilize.** Read `g8c-closeout-freeze.md` §g first: it is the one-paragraph
map of the whole arc and the reference table for every leg. Then, per leg, run only
that gate file by `--filter-method`; every gate adopts the warm lane and refuses to
rebuild. Do **not** treat G7c as proven because its neighbours are: the chain has a
gap exactly at loot→grant.

**Caveat that costs time if missed.** `g6-first-combat-report.json` (bare pointer) now
holds `UNKNOWN / SETUP/quest-prehold` from a later route-collision attempt; the PASS
record is the **stamped** file `.20260922T173238674Z`. Same pattern for G7c: the bare
`g7c-loot-report.json` currently holds the fixture-overshoot UNKNOWN, while the
behaviour FAIL is the stamped `g7c-loot-report.20260923T133239002Z.json`.

### c.2 Harness methodology audit — DESIGN (complete, partially acted on)

**What it is.** A read-only 13-section audit (A–M) of whether the gate system behaves
as a deterministic fail-fast harness or leans on long-running agent judgement, with a
ranked top-10 improvement list and a migration plan.

**Status.** Audit complete; recommendations #1 (K1) and #4 (K4) implemented (§c.3,
§c.4); #2, #3, #5–#10 unimplemented.

**Evidence.** `MUSE-harness-audit.md` (≈49 KB, sections A–M + Final Summary); the same
audit is still readable as `history://harness-audit` (the aborted subagent transcript —
same report payload, useful if the session directory is pruned).

**Key findings (quoted).**
- Verdicts: *"PARTIALLY — verdicts: YES (every bot gate reaches PASS/FAIL/HARNESS
  without any LLM; only human-feel gate H needs Josh). Efficient operation: NO."*
- Biggest wasted runtime: long wake loops (300 s pursue/combat/drive, 600 s kill,
  4×1080 s G7d cycles) running to deadline after wake 2 already proved no per-bot
  quest tick.
- No advisor/agent hook exists inside the test loop; all verdicts are `Assert`-based.
- Best existing patterns to copy: G4 single-wake funnel verdict, G3 stable-start/
  arbiter gate, G8a tick-quorum windows, always-written reports.
- `scripts/gate.sh` is a **separate** unit/MCP gate and never runs the E2E gates
  (audit #K10 asks for a header comment saying so).

**How to utilize.** Read `MUSE-harness-audit.md` §K before designing any new gate: the
top-10 is a ready backlog. §L carries the target harness shape
(preflight → readiness smoke → fixture → START → step assertions → fail-fast →
structured report → post-hoc advisor). §M gives the incremental migration order —
implement in that order, not as a rewrite.

### c.3 K1 first-zero fail-fast — LANDED-BUILD-CLEAN (never fired live)

**What it is.** A 2-consecutive-wake idle exit: when `questDecideDetail` is
byte-identical and `HasPerBotStepSignal == false` twice in a row, the wake window ends
immediately with the already-frozen boundary name (`DISCOVERY/sweep-empty` or
`ARBITRATION/no-quest-activity`), instead of burning the 300/600 s budget.

**Status.** LANDED-BUILD-CLEAN. Rolled to G5, G6, G7a, G7b, G7c, G7d (two exit blocks
per drive loop where a restage exists). **Not** in G8a/G8b/G8c/G4/sweep/microgate. No
lane report contains a `K1 idle-window exit` line ⇒ it has never fired live; PASS paths
always show a fresh decision by wake 1–2, and every idle block is placed *after* the
pass break with explicit guards, so it cannot trip a warm PASS.

**Evidence.** `MUSE-k1-rollout.md` (per-file hunks, guards, verdict-branch wiring,
"no new boundary strings" invariant), `MUSE-failfast-impl.md` (`k1_predicate`),
`MUSE-harness-audit.md` §K #1 for the rationale/budget figures. Verify in source with
`grep -n idleWakes AAEmu.IntegrationTests/E2e/G*.cs`.

**How to utilize.** The cheapest real test of this work is a **cold-lane** run of any
K1-enabled gate (G5 is the cheapest): expected output is an evidence line
`- K1 idle-window exit after N wakes (...) : DISCOVERY/sweep-empty` and a verdict at
that boundary in ≪ the budget. On a warm lane it is unobservable by construction — do
not "verify" it warm. Extending K1 to G8a/b/c is audit #1's remaining half.

### c.4 K4 report/artifact preservation — PROVEN-LIVE

**What it is.** Every gate writes its report twice: a timestamped
`logs/<gate>-report.<utc>.json` (millisecond precision so same-second reruns do not
collide) and then a `File.Copy` refresh of the bare `logs/<gate>-report.json` as a
"latest pointer". Red artifacts survive green reruns.

**Status.** PROVEN-LIVE — 17 report writers carry it and stamped files are on disk for
G7a, G7b, G7c, G7d and G8-era gates.

**Evidence.** `MUSE-failfast-impl.md` (`k4_scheme`, per-site line ranges);
`grep -rl "File.Copy(stamped" AAEmu.IntegrationTests/E2e/` → 16 files
(G1 ×2, G2, G3 ×2, G4, G5, G6, G7a-d, G8a-c, `SchedulerDeliveryMicroGate`) plus
`Quest251FullChainSweepTests` with its own `File.Copy(sweepStamped, …)` = **17**.
Live examples:
`/root/aaemu-e2e-q0/logs/g7a-kill-report.20260923T035250559Z.json`,
`g7c-loot-report.20260923T133239002Z.json`.

**How to utilize.** When triaging any gate, read the **stamped** file matching the run
timestamp — the bare pointer may belong to a later/different attempt (this is exactly
what happened to G6 and to G7c, §c.1). Never delete a stamped red artifact after a
rerun; it is the only record that the failure happened.

### c.5 G7x scanner narrowing — LANDED-BUILD-CLEAN

**What it is.** The behaviour gates' game-log exclusion scanners (`ScanGameLogCastLoot`)
matched any line containing `char=<id>` plus a bare `Cast|AutoAttack|Loot` word. The
per-wake `QuestObjectiveTargetDiag` line is emitted by `LogObjectiveFunnel`
**pre-arbitration and pre-dispatch**, so its `combat=[...verb=AutoAttack...]` fragment is
a *proposal description*, not an audit row — and it false-positived gates as
"COMBAT-CAST-DETECTED".

**Status.** LANDED-BUILD-CLEAN: G5 (pilot) + G7a, G7b, G7c, G7d now `continue` on
`QuestObjectiveTargetDiag` inside their **exclusion** scanners (`ScanGameLogCastLoot`)
and store the full line (not a `[..220]` slice). G6 and G8b were not touched: G6's
`ScanCombatExclusion` reads decide/live strings (not the game log), and G8b's
`QuestObjectiveTargetDiag` use is a positive funnel collector, not an exclusion scan —
those positive collectors are intentionally left reading the diag line. No run has
exercised the narrowed scanner yet.

**Evidence.** `MUSE-g7x-narrow.md` (+7/−1 lines per file, build 0 errors),
`MUSE-scanner-dx.md` (the root-cause diagnosis: the matched token was in the
unrecoverable truncated tail; every independent behaviour proxy was zero),
`MUSE-window-narrow.md`/`MUSE-g7a-witness.md` (the teardown-witness bounding that
superseded one of the two mitigations). Source: `grep -n "selection-time proposal
diagnostics" AAEmu.IntegrationTests/E2e/*.cs` → exactly the 5 files above.

**How to utilize.** Any new combat-adjacent exclusion scan MUST skip
`QuestObjectiveTargetDiag` (proposal-time) and only score real audit rows /
`landed` decide strings. When a gate reports COMBAT-CAST-DETECTED, first check whether
the hit is a proposal-diagnostic before treating it as a behaviour failure.

### c.6 Leash re-anchor fix — PROVEN-LIVE

**What it is.** Path-following used to drag each NPC's leash origin (`IdlePosition`)
along the route, so a patrolling mob was permanently "far from home": `ShouldReturn`
was true on the very tick it acquired aggro and `ReturnState` stripped the aggro — mobs
leashed themselves instead of fighting. Fix: stop walking `IdlePosition`, and
re-anchor it to `HomePosition` on **combat entry** (`NpcAi.SetCurrentBehavior(BehaviorKind)`,
covering every AiCharacter including per-character `GoToCombat()` overrides).

**Status.** PROVEN-LIVE (direct behavioural proof), with a deployment caveat.

**Evidence.** `MUSE-flee-ai.md` (discovery: `ShouldReturn` semantics, defaults 50 m/200 m,
template overrides), `MUSE-flee-fix.md` (fix + proof). Files:
`AAEmu.Game/Models/Game/AI/v2/Controls/AiPathHandler.cs:103-113`,
`AAEmu.Game/Models/Game/AI/v2/Behaviors/Common/FollowUnitBehavior.cs:45-50`,
`AAEmu.Game/Models/Game/AI/v2/Framework/NpcAi.cs:128-136` (`CombatEntryKinds`),
`:150-153` (`AnchorLeashOnCombatEntry`), `:166` (call site in
`SetCurrentBehavior(BehaviorKind)`).
Proof of behaviour: live `adventurer-spike-fox` hunt PASS in 165 s with 4 returns,
pos↔idle separation 4.0/6.4/5.2/5.7 m and **zero** returns > 50 m; pre-fix the
separation equalled the walked patrol distance (tens of metres).
Deployment proof at the time: lane `AAEmu.Game.dll` md5 == repo `bin/Release` md5.
**Caveat:** that md5 equality no longer holds (repo `bin` has been rebuilt since; lane
runtime dll is from 2026-09-22 18:56). Re-verify/redeploy before any gate that depends
on combat not self-leashing (`g6-closeout-freeze.md` §g debt: boar leash-reset vs
level-10 DPS).

**How to utilize.** Any hunt/combat gate that sees the prey kite away or `HARNESS/leash-reset`
HP-sawtooth should first confirm the deployed dll contains the fix, then re-read
`NpcAi.CombatEntryKinds` semantics. This also retires the `g6-closeout-freeze.md` §g
"leash-reset" debt for *patrolling* NPCs `[INFERENCE]`, not for the template-level
`ReturnDistance` fixture bound.

### c.7 Loop-release seam — PROVEN-LIVE (diag arm), release arm still OPEN

**What it is.** A production seam in `QuestBehavior.LootProposal`: while the looting
actor's auto-attack loop is still live (`character.IsAutoAttack`), the Loot proposal is
withheld and logs `validate=loop-live:target=<objId>:container=<n>:lootable=true:verb=none`.
Release is engine-guaranteed (the auto-attack task self-terminates on target
null/`Hp<=0`), so loot lands at/after teardown **by construction** — no timer, no new
state.

**Status.** PROVEN-LIVE for the withhold+diagnostic arm: the live G7c run at
`20260923T133239002Z` contains
`CORPSE START: loop-live provisional (validate=ok withheld pre-teardown): 1 loop-live arm(s), maxContainer=2`.
The **release arm** (`validate=ok`) has not yet been observed on the corpse (§c.10).

**Evidence.** `MUSE-loot-release.md` (one condition + diag + comment; build 0 errors;
`QuestBehavior.cs:1043-1047` summary, `:1079-1091` gate),
`MUSE-loot-race.md` (read-only design analysis: pre-death there is no corpse at all, so
the only ordering hazard is the live-loop race), G7c report above.

**How to utilize.** Any gate that needs loot ordering must (a) treat `loop-live` as
"correctly withheld, not a failure", and (b) wait for the `validate=ok` release arm —
never fabricate `containerBefore` from the provisional arm. G7c's START block does
exactly this (§c.10).

### c.8 Witness back-dating (G7a/G7b) — PROVEN-LIVE (G7a); superseded in G7b

**What it is.** The teardown witness (`IsAutoAttack` false) lands on the *wake after*
the last live tick, so a post-teardown Loot was being attributed to the exclusion
window and failed G7a. Back-dating anchors the witness to the **last true wake**
(`teardownLastLiveWake`) and routes everything at/after it into
`postTeardownLootEvidence` (G7c-owned, non-failing).

**Status.** PROVEN-LIVE in G7a (`teardownFalseWake=13`, real post-teardown loot
recorded as evidence, gate PASSes). **The G7b copy was deleted** by the recognition-only
conversion (§c.9) because recognition timing now comes from `detectionWakes` and no
loot-exclusion window exists — so do not look for witness machinery in G7b.

**Evidence.** `MUSE-g7a-witness.md` (back-date confirmation + stale-proc guard fired +
PASS in 1 m 13 s), `MUSE-window-narrow.md` (both files' hunk list),
`MUSE-g7b-conv.md` (`fail_chain_removed`: the witness machinery removed and why).

**How to utilize.** Reuse the *pattern* (anchor = last true sample; false sample only
closes the window) for any "state ended but a trailing sample arrived" gate. Current
anchors: `G7aQuestKillGateTests.cs:119` (`postTeardownLootEvidence`), `:163`
(`teardownFalseWake`), `:172` (`teardownLastLiveWake`); verify with
`grep -n teardownLastLiveWake`.

### c.9 G7b recognition-only conversion — PROVEN-LIVE

**What it is.** G7b's contract was narrowed to *recognition only*: PASS requires the
quest leg to NAME our corpse (`detectionHits>=1`), hold identity across wakes, and
observe a container/lootable transition. Loot, grant, meat and money deltas became
explicit **G7c-owned evidence that can never fail this gate**.

**Status.** PROVEN-LIVE — `g7b-corpse-report.json` = PASS-BEHAVIOR / none, with the
header `G7b corpse-RECOGNITION gate (... loot recorded as G7c-owned evidence)`.

**Evidence.** `MUSE-g7b-conv.md` (whole-file contract rewrite; the deleted fail clauses:
`LOOT-DISPATCHED`, `COMBAT-CAST-DETECTED`, `COMBAT-CREDIT-LEAK`, `CONTAINER-UNPROVEN`),
`g7b-corpse-report.json` (+ stamped runs `.20260923T074007229Z`, `.20260923T074351611Z`).
Superseded artefact: `g7b-closeout-freeze.md` still describes the pre-conversion
container-probe gate — see the supersession banner added in this pass.

**How to utilize.** Read the **gate header** (`G7bCorpseObserveGateTests.cs:12-33`)
plus `MUSE-g7b-conv.md`, not the freeze doc, for the current contract. The doctrine to
copy: one gate = one predicate family; downstream consequences are recorded as
attributed evidence and owned by the gate that asserts them.

### c.10 G7c chaining relaxation + START loop-live tolerance — LANDED-BUILD-CLEAN; gate OPEN

**What it is.** Two coupled gate changes:
1. **CHAINING-INTENDED relaxation** — 251 needs 4058×3 and one corpse yields ~1, so
   production *must* pursue further corpses. The old failure arm
   `landedLootCount > 1 || foreignLoot > 0` conflated same-corpse re-dispatch (a real
   violation) with legal chaining. Replaced by `CorpseLootedTwice(decideHistory)`
   (per-ObjId idempotency: fail only if ANY one corpse is looted twice); `foreignLoot`
   stays as evidence. The quest gate relaxed from Progress-only to *held-not-past-Ready*
   (`questHeldBeforeReady`), and the watch now ends on our corpse's terminal grant.
2. **START loop-live tolerance** — the corpse-START fixture proof may start
   *provisionally* on a `validate=loop-live` arm naming our corpse with `container>=1`,
   then wait a **bounded** `WakeWaitMs` for the `validate=ok` release arm before opening
   the loot watch; `containerBefore` always reads the release arm only. The bounded
   change adds exactly **one** `quest wake` inside the existing START window (a test
   cannot sleep its way to a fresh quest tick). Transport failure becomes
   `HARNESS/release-wake`, not a behaviour failure.

**Status.** LANDED-BUILD-CLEAN (build 0 errors). **The gate itself is OPEN**: the run at
`20260923T133239002Z` issued the release wake and still produced `lootOkDiags=0`
⇒ `FAIL-BEHAVIOR/CORPSE-UNOBSERVED`. The newest run
(`20260923T133502266Z`, currently the bare pointer) failed even earlier with
`UNKNOWN / HARNESS/fixture-overshoot` ("prey died before START", twice) — a *staging*
failure, not a loot verdict.

**Evidence.** `MUSE-g7c-intent.md` (the full CHAINING-INTENDED verdict + exact line
changes), `MUSE-g7c-relax.md` (three hunks: `CorpseLootedTwice`, `questHeldBeforeReady`,
terminal-grant watch end), `MUSE-g7c-start.md` (START block `792-818`, `ScanLootLoopLive`
`1190-1213`), `MUSE-g7c-run.md`/`g7c-run2.md` (the two failing runs),
`MUSE-g7c-fixture.md` (why the extracted `DriveToG6End` helper had drifted from G7a).
Source (current lines — note these drifted from the session notes' numbers):
`G7cLootGrantGateTests.cs:151` (`questHeldBeforeReady` decl), `:792-846` (START
loop-live provisional + bounded release-wake + ok-release wait), `:964` (watch ends on
terminal grant), `:981` (held-not-past-Ready compute), `:1036` (`landedLootCount > 1 ||
CorpseLootedTwice(decideHistory)`), `:1066` (`!questHeldBeforeReady`), `:1237`
(`ScanLootLoopLive`), `:1333` (`CorpseLootedTwice`).
Design context: `scorecard-explorations/mechanics/g7-loot-credit.md` §"G7c release-wake
follow-up — 2026-09-23".

**Rejected alternative (do not re-litigate).** `MISSING-STOP` (force production to stop
looting after one grant) was rejected: it contradicts need-driven relevance
(`QuestObjectiveTargetSelector.cs:101-108`), requires inventing policy + a latch beside
`LootedCorpses`, and G7d's own contract already asserts one grant *per corpse*.

**How to utilize.** Next action is the §d queue item 1/2. Read `MUSE-g7c-start.md` plus
the current gate block before touching anything; the open question is narrow — why the
post-release quest wake does not produce a `validate=ok` arm naming our corpse inside
`WakeWaitMs`.

### c.11 G7d blocker-corpse staging — PROVEN-LIVE

**What it is.** G7d's staging watch used to poll HP only and time out blind when the
spawner was denied by an **unlooted corpse** (`NpcSpawner.CanSpawn` refuses while a
corpse holds the spawner). It now classifies the blocker (`BLOCKER/unlooted-wait` vs
`BLOCKER/empty-wait`), dispatches **exactly one** real-actor drain per blocked watch
(capped to the remaining deadline) to clear it, and distinguishes the two harness
boundaries `HARNESS/stage-range` (spawner empty) vs `HARNESS/stage-range-blocked`
(denied by a corpse).

**Status.** PROVEN-LIVE: `g7d-credit-report.json` = PASS-BEHAVIOR/none with the blocker
path exercised (corpses 40581 and 17534 each drained once, then `empty-wait` counts,
then successful restage) and the claim "granted meat [0→3], objectives [3,0,0,0,0],
quest Ready".

**Evidence.** `MUSE-g7d-stage.md` (hunks L1163-1232 stage watch, L1270-1301 helpers,
L873 `harnessesOnly`, header note), `MUSE-g7d-run2.md` (the blocker evidence),
`/root/aaemu-e2e-q0/logs/g7d-credit-report.json`. Current lines:
`G7dQuestCreditGateTests.cs:53` (boundary doc), `:875` (`harnessesOnly` incl.
`stage-range-blocked`), `:1153-1230` (blocked-vs-empty stage watch + one drain),
`:1272` (`NpcProbe`), `:1297` (`DispatchCorpseDrain`).

**How to utilize.** Any gate that stages at a *spawner* (G6/G7a/b/c/d) inherits this
hazard. Copy the three-state staging doctrine: name the blocker, drain once (staging
exception, disclosed in the header), and fail as `HARNESS/*` when the spawner is
denied — never as a behaviour failure.

### c.12 Stale-proc guard rollout — PROVEN-LIVE

**What it is.** Every lane's game server binds the same wildcard `*:1280` (Kestrel
`SO_REUSEPORT`), so a WebApi request can hash to a *stale foreign lane* and return
`404 bot control API is disabled` mid-gate. The guard runs at the adopt step: it scans
listeners on the WebApi port, resolves each pid's cwd via `/proc/<pid>/cwd`, and
terminates foreign listeners (SIGTERM → SIGKILL after 10 s). G7c uses the **refusal**
variant (`AssertLaneOwnsWebApiListeners`): it throws instead of killing, because a test
must not stop another lane to win exclusive routing.

**Status.** PROVEN-LIVE. G7a's run killed two foreign pids (1278066, 1735771) leaving
only the lane pid on :1280; current G7c reports contain
`stale-proc guard (shared :1280 listeners outside this lane): all observed listeners
belong to the adopted lane`.

**Evidence.** `MUSE-guard-roll.md` (15 files, G7a byte-identical helper, per-file line
ranges; G7a = template, G7b = pre-existing void variant), `MUSE-g7a-witness.md`
(`stale_proc_guard.fired=true`, pids + results), `MUSE-flee-fix.md` (the incident that
motivated it: 3 processes on `*:1280`, 3/3 G6 attempts died at `SETUP/quest-prehold`).
Source: `grep -l "stale-proc guard" AAEmu.IntegrationTests/E2e/*.cs` → 17 files
(G1 ×2, G2, G3 ×2, G4, G5, G6, G7a–d, G8a–c, `Quest251FullChainSweep`,
`SchedulerDeliveryMicroGate`); the template helpers are
`G7aQuestKillGateTests.cs:1610` (`KillForeignWebApiListeners`)
and `G7cLootGrantGateTests.cs:1788` (`AssertLaneOwnsWebApiListeners`, the refusal
variant), each called from the adopt step (e.g. G7c `:159-160`).

**How to utilize.** When a gate fails at `SETUP/quest-prehold` or any WebApi-routed leg,
read the guard line in the evidence first. Any **new** gate must install the guard at
its adopt step (copy the G7a helper; G7c's refusal variant is the preferred shape for
tests that must not mutate other lanes).

### c.13 `IsMounted` observation — LANDED-BUILD-CLEAN (uncommitted, undeployed)

**What it is.** One engine-truth field on the actor observation surface:
`ActorObservation.IsMounted` (`+3` lines) built from
`BotMountManager.IsMounted(Character)` → `MateManager.GetIsMounted` in
`GameplayActor.Observe()` (`+1` line). Chosen over `Character.IsRiding` (a plain
settable bool also cleared by the slave/ship path) for mate-authority.

**Status.** LANDED-BUILD-CLEAN (`dotnet build AAEmu.Game` 0 errors). It is **dirty
source**: it is not deployed to the q0 lane runtime and cannot be observed live. No
gate asserts it yet.

**Evidence.** `MUSE-ismounted.md` (accessor choice + exact lines),
`MUSE-obs-map.md` (the gap table: mount state was previously MISSING from every
surface — `ActorObservation`, `charState`, quest observe, `/api/actors/observe` — while
engine truth existed but was unprojected). Source:
`AAEmu.Game/Core/Managers/Bots/ActorObservation.cs:87-88`,
`AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:306`;
unit coverage for the engine truth already exists in
`AAEmu.UnitTests/Game/Core/Managers/Bots/BotMountManagerTests.cs`.

**How to utilize.** This is the prerequisite for the Mount/Dismount verb proof (§c.14
top-5 & §d). Sequence: deploy the dll to the lane → observe `isMounted` before/after a
`mount`/`dismount` action → then a G2-shape capability gate with a state delta (today
the audit only proves the *action* Completed, not the state). `obs-map.md` also lists
the next larger projection gap: per-quest `step/status/objectives` are missing from the
unified `ActorObservation` snapshot (present only on the bridge surface).

### c.14 Trace archaeology (14 traces / 19 concrete verbs / top-5) — FROZEN-PASS (read-only evidence, no gate)

**What it is.** A read-only census of the 14 human play traces
(`traces/player-actions/*.jsonl`, char *Dingus*, 2026-09-14) mapped against the
`IGameplayActor` verb surface: 19 verbs with trace evidence, 32 asserted-but-unobserved,
4 families with no verb at all.

**Status.** FROZEN-PASS as an evidence record; no gate has been built from it.

**Evidence.** `MUSE-trace-arch.md` (full inventory table, coverage map, top-5, schema
gaps), `MUSE-trace-find.md` (where traces live and how `/trace start` names them),
`MUSE-trace-ground.md` (schema limits: only packet names/opcodes/skill events; **no**
quest/item/template IDs). Reproducible corpus: `von-trace-eval-bundle-2026-09-23.zip`
(14 traces + inventory + canonical).

**19 concrete verbs.** Move, Stop, Target, Cast, AutoAttack, Interact, InteractWith,
UseItem, Mount, Dismount, DismissMate, Buy, Sell, Plant, Harvest, AcceptQuest,
AdvanceQuest, TurnInQuest, InteractNpc.

**Top-5 next-verb candidates.** Loot (0 records but 7 unlooted deaths; chain-critical
4058×3), TurnInQuest (wire-exact — this **refutes** `g7-human-trace-251.md` §1's
zero-turn-in claim), Harvest (2 crop species + phase/labor deltas), Plant (doodad
create + seed buy), SummonMate (NEW family: item-skill 10602 + `SCMateSpawnedPacket` ×4,
**no method exists on `IGameplayActor`**).

**Four families without a verb.** SummonMate; ObserveDoodad/AwaitPhase (doodad phase
transitions are the plant/harvest completion signal); SubZoneArrival (segmented travel
the movement matrix cannot describe); SkillReadiness/Refusal (readiness is only visible
post-hoc as `skill_refused`).

**Hard limits the census proves.** Traces emit `money_changed`, `labor_changed`,
`doodad_phase_changed` but **no bag-delta and no mate-state events** — 49,261/49,274
packet records carry only `{Packet, Opcode}`. Traces prove *ordering and human
behaviour only*; every consequence must close on `ActorObservation.BagItemCounts/Money/
LaborPower` plus live object state, never on a trace line.

**How to utilize.** Use this census to choose the next verb proof and its observation
requirements (see `MUSE-verb-proof.md` for the designs); never cite a trace as
consequence evidence. Turn-in is observed — do not reopen the disproved zero-turn-in
premise.

### c.15 Nav / world snapshot export feasibility — DESIGN

**What it is.** A feasibility probe for exporting a bbox world snapshot (spawner JSON +
DB joins) as a cached, planning-only world layer, plus the increments ranked around it.

**Status.** DESIGN. Nothing implemented; explicitly *not* bot decision input.

**Evidence.** `MUSE-nav-viz.md`. Key facts: authoritative positions are JSON, not DB
(`npc_spawns.json` 24,385 active entries/7,023 templates; `doodad_spawns.json` 42,649);
`compact.sqlite3` has no xyz spawn table (md5 `78b3bdbf038db3b927056106efdf91af`);
navmesh is real CryEngine `.bai` (7,937 blocks / 3.5 M nodes) and must never be exported
raw. Measured prototype: 67,034 records → 3.5 MB JSON in 0.44 s, zero join misses.
Renderers already exist (`dashboard_server.py` `/api/map/*` + `dashboard_map.js` canvas
layers). Honesty constraint: project doctrine forbids world-scan decisions
(`QuestObjectiveTargetSelector.cs:16`; 25–45 m perception), so a snapshot must be
labelled planning-only, exactly like `Tools/SurveyGridBaker/Program.cs:228`
(`planning_only = true`).

**How to utilize.** Increment order: (1) snapshot export (smallest; offline/GM path,
zero behaviour change), (2) dashboard entity layer (reuse the existing canvas),
(3) waypoint derivation feeding `RoadNetworkService`, and only then (4) more hardcoded
routes. Stamp source md5 + capture time on every snapshot and surface its age; never
let it reach a decision layer.

### c.16 Von side-quest — OPEN (calibrated; not usable as a classifier)

**What it is.** An external decision-model service (`http://192.168.0.17:8000`) probed
as a potential trace/action-family classifier for the corpus.

**Status.** OPEN / not useful as a trace classifier. Endpoint and protocol are mapped;
agreement on hand-labelled traces is 1/6 on naive first-N-event prefixes, and only 4/6
on engineered whole-trace shape summaries — all with confidence < 0.40.

**Evidence.** `MUSE-von-ping.md` (reachable; `von-decision-server 1.1.0`, engine
`von-1.1`; real route is `POST /v1/systemone`, question-wrapped payload; models
`von-latest`, `von-1.1.0`, `jev-latest`), `MUSE-von-agree.md` (the 6-trace probe and
its failure analysis: the first ~30 events of every trace are the identical harness
envelope, so the state is degenerate and the model collapses to a constant `dialogue`
label; confidence was non-informative — mean 0.020 correct vs 0.018 wrong).

**How to utilize.** If Von is reused: never feed raw prefixes — pass only distinctive
packet/skill/world-event names, and treat any `dialogue` answer with confidence < 0.05
as ABSTAIN. Skill-id-only families (combat Skill 2/16064, farming Skill 13625+doodad)
remain out of its reach. The bundle for re-running the calibration is
`von-trace-eval-bundle-2026-09-23.zip` (14 traces + `verb-inventory.md` + `families.txt`
+ `canonical.md`).

### c.17 Codex side-lane (playertrace coverage / worklist) — OPEN

**What it is.** A parallel, non-gameplay evidence lane: the trace coverage mapper
(`Scripts/playertrace-coverage/`), its worklist, and the reconciliation of the trace
tooling with the Muse handoff order.

**Status.** OPEN. Worklist + triage landed in the dirty tree; the next named step
(observation mapping) is unfinished.

**Evidence.** `Scripts/playertrace-coverage/README.md:5-24` (the shared handoff /
execution order — this is the canonical statement of *who proves what next*),
`README.md:32-51` (the 6-row delivery sequence), `MUSE-codex-hunt.md` (session scope:
which files were touched, what remained unfinished, stop cause),
`MUSE-codex-odd.md` (oddity triage: the summary's capture recommendations and the
hard-coded gap logic were already handled by corpus-aware triage / evidence-aware
mapper; `7/7` packet types restored; 40/40 Python tests), `MUSE-obs-map.md`
(observation-mapping table).

**How to utilize.** Read `README.md:5-24` before dispatching any verb work: it fixes the
order (G7c wait → Wave A Buy+Harvest → Wave B Plant+Sell → Mount after IsMounted) and
the evidence boundary ("traces prove ordering; consequences close on observations").
The lane owns *evidence discovery and capture specs*, never gameplay implementation.

---

## (d) Open-items queue (dependency order)

Each row is blocked by the row above it unless noted. "Done when" is the observable
exit; no row may widen a frozen gate (§c.1 contracts).

| # | Item | Blocked by | Done when | Read first |
|---|---|---|---|---|
| 1 | **G7c wake-issuing wait** — make the post-release quest wake actually produce the `validate=ok` release arm naming our corpse inside `WakeWaitMs` | — | A G7c run in which the START block logs an `ok` arm and `containerBefore >= 1`; or a named boundary proving why no arm can appear (e.g. leg genuinely produces no further tick after release) | `MUSE-g7c-start.md`, `MUSE-g7c-run2.md`, `G7cLootGrantGateTests.cs:792-846` |
| 2 | **G7c run** — the loot→grant PASS itself (one Completed Loot, conservation `grant == containerBefore − containerAfter`, `containerAfter == 0`, per-corpse idempotency, quest held-not-past-Ready) | 1; also needs staging to stop dying (`HARNESS/fixture-overshoot` on the latest attempt) | `g7c-loot-report.json` (stamped) = PASS-BEHAVIOR at `none`; G7c joins the frozen set | §c.10 + `MUSE-g7c-intent.md` (the exact contract being asserted) |
| 3 | **Wave A verb proofs: Buy + Harvest** | 2 (G7c owns the loot/grant seam both will reuse) | Two capability gates at their named layer: **Buy** = G2-shape capability + conservation leg (money `−price×count` exact, bag `+n`, refusal variants with zero delta); **Harvest** = G7c-shape fixture (grow doodad to mature phase → START → one harvest → single-use proof: phase change, bag `+yield`, labor `−1`, second harvest Rejected) | `MUSE-verb-proof.md` (rank 1–2 designs, dossiers, gate shapes), `README.md:9-14` |
| 4 | **Snapshot export** — bbox world JSON, planning-only | none hard; deliberately after the gameplay proofs so it cannot be mistaken for perception | An offline/GM path emitting `aaemu.world-snapshot/v1` for a bbox, stamped with source md5 + capture time | `MUSE-nav-viz.md` §2 (data inventory, schema) and increment 1 |
| 5 | **Von agreement** — decide whether the external model is usable at all | 3/4 (needs the real consequence-shaped states the verb proofs produce; the current states are degenerate) | Either a re-run with consequence-rich states scoring materially above the 1/6 prefix baseline with informative confidence, or an explicit no-go recorded | `MUSE-von-agree.md` ("recommendation_if_reused"), `MUSE-von-ping.md` (protocol) |

**Independent, not blocked (may run anytime, in parallel):** K1 cold-lane observation
(§c.3), K1 extension to G8a/b/c, harness-audit recs #2/#3/#5–#10 (§c.2), Wave B
(Plant + Sell, gated on Wave A's designs + IsMounted order), redeploy + live check of
`IsMounted` (§c.13), and the Codex observation-mapping step (§c.17).

---

## (e) Utilization quick-start

| If you want to… | Read | Then |
|---|---|---|
| Understand the whole proven loop | `g8c-closeout-freeze.md` §g | run any gate by `--filter-method`, adopt-only |
| Triage a failed gate | the **stamped** report + `MUSE-harness-audit.md` §D/§G | check the stale-proc guard line, then the first-zero name |
| Add a new behaviour gate | `MUSE-harness-audit.md` §J (good patterns) + §L (target shape) | copy G7a's stale-proc guard + K1 idle exit + K4 writer |
| Choose the next verb to prove | `MUSE-trace-arch.md` top-5 + `MUSE-verb-proof.md` | Wave A designs are already written — implement, don't re-design |
| Know what an observation can prove | `MUSE-obs-map.md` | `IsMounted` is landed-but-undeployed; quest counters still unprojected |
| Change combat/leash behaviour | `MUSE-flee-ai.md` + `MUSE-flee-fix.md` | verify the deployed dll first |
| Feed a model / classify traces | `MUSE-von-agree.md` | do not use raw prefixes; consequence-free states are unclassifiable |

---

## (f) Proves / does-NOT-prove boundary (for this index)

**Proves.** (i) Each item above has an artifact on disk and a status that matches what
that artifact actually shows; (ii) the quest-251 chain's only unproven leg is G7c;
(iii) K1 has never fired live and K4 is live; (iv) the leash fix and the loop-release
seam are deployed-and-observed while `IsMounted` is neither; (v) the trace corpus
cannot carry consequence evidence by construction.

**Does-NOT-prove.** Nothing here re-verifies a gate, a build, or a deployment. No frozen
contract is widened, and no historical FAIL is promoted. Verdicts are quoted, not
re-derived; where the quoted source is a session note rather than a report, the entry
says so. Session-note paths under `/root/.omp/agent/sessions/...` are process-local
archaeology: if they disappear, the corresponding source hunks remain the authority.

## (g) Reconciliation performed in this pass

- `g7b-closeout-freeze.md` — supersession banner added (recognition-only conversion of
  2026-09-23 supersedes the container-probe gates described there).
- `g7-loot-credit.md` §"G7c release-wake follow-up" — status line added (release-wake
  seam landed; runs and their outcomes; pointer to this index).
- No other file touched. No gate run; no build; no lane interaction.
