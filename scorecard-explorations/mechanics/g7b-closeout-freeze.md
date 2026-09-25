# G7b Closeout Freeze — PASS-BEHAVIOR (2026-09-20, branch develop)

> **SUPERSEDED IN PART — 2026-09-23.** The gate was converted to
> **recognition-only** after this freeze: the PASS contract is now
> `detectionHits>=1` + identity held + container/lootable transition observed,
> and landed Loot / LootDiag Completed / meat-money deltas are recorded as
> **G7c-owned evidence that can never fail this gate**. The container-probe
> failure clauses (`CONTAINER-UNPROVEN`, `LOOT-DISPATCHED`,
> `COMBAT-CAST-DETECTED`, `COMBAT-CREDIT-LEAK`) and the teardown-witness loot
> exclusion described below were deleted for that reason. Current contract lives
> in `AAEmu.IntegrationTests/E2e/G7bCorpseObserveGateTests.cs:12-33` and the run
> record `MUSE-g7b-conv.md`; evidence: `/root/aaemu-e2e-q0/logs/g7b-corpse-report.json`
> (PASS-BEHAVIOR/none). Everything else in this freeze (corpse promotion,
> identity continuity, read-before-act, zero-action at the time it was written)
> remains the historical record and the source of the G7c handoff.
> See `arc-utilization-index-2026-09-23.md` §c.9.

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (charId=45, quest 251, prey
template 3475, prey item 4058). This is a documentation/reconciliation/freeze
record. Zero behavior change in this pass; no tests were run for this
closeout (unit-test claims below are code-declared from source reads, not
rerun).

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-20.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g7b-corpse-report.json`
  (`verdict=PASS-BEHAVIOR`, `failBoundary=none`), evidence header
  `2026-09-20 23:27:57Z`.
- **PASS-BEHAVIOR chain fields:** corpse 44310 (3475, Hp 0, ObjId held —
  `corpseStart: corpse=44310 hp=0`, kill trail `wake=12 hp=0 DEAD`);
  quest-side naming 11622 detection signals
  (`:corpse=44310:container=1:lootable=true` across 149 observe wakes);
  container probe 1 (`corpseStart.containerProbe=1`,
  `failingCondition.container=1`); zero loot dispatched (observe
  `lootHits=[]`, detail `lootHits=0 castLoot=0`); quest still Progress
  (`questStillActive=true`, `endQuestStatus=Progress`, `turnIns=0`);
  meat 0→0 (`meatPre=0 meatPost=0`), credit delta 0, money delta 0
  (`sideEffects: item4058Delta=0 creditDelta=0 moneyDelta=0`).
  Quirk, recorded honestly: the `gate` leg row carries `passed=false` with
  detail `PASS-BEHAVIOR at none` — the verdict/failBoundary fields above are
  the authoritative PASS signal, same reading as the claim.
- **Code reference (read-only verification, no edits):**
  `AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs` — corpse record
  `QuestCorpseRecord` (L109) + `LastCorpse` per-actor map (L110) +
  `CorpseMemoryBound=256` (L111) + `NotePinnedCorpse` (L117-128) +
  `ProbeCorpse` read-only probe (L147-162) + recognition arms
  `NoteDeadSelection` (L180-189) / `ObservePinnedCorpse` (L201-238) +
  `Dispatch` switch (L958-991, no Loot arm); `QuestBehavior.cs` and the test
  file below are extraction-landed untracked, so `git diff` stat is empty by
  construction (same note as the G3 freeze).
- **Unit tests (existence verified by read, NOT rerun):**
  `AAEmu.UnitTests/Game/Core/Managers/Bots/QuestCorpseObserveTests.cs`
  (new file, 7 tests): `AliveToDead_PromotesCorpse`,
  `SameIdentity_PreservedAcrossWakes`, `IrrelevantDeadNpc_Ignored`,
  `ContainerProbe_NonEmpty_Lootable`, `ContainerProbe_Empty_NotLootable`,
  `CorpseFragment_IsParserSafe`,
  `ZeroAction_NoLootDispatched_ContainerUntouched`. Pass status is
  code-declared (asserts read in source); no test process was launched for
  this closeout per instructions.

## (b) Causal timeline (every stage HISTORICAL/SUPERSEDED except the final)

1. **G7a kill — HISTORICAL/SUPERSEDED as a stage, reused as the leg.**
   A fresh G7a-style run to authoritative death opens this gate
   (`g7aLeg: fresh G7a-style run to authoritative death; corpse START
   re-taken at the fresh corpse`); staged 2.0 m, `stageHp=286`,
   `drive-to-g6-end` pinned + stable, kill `258→0 dead=True atWake=12`.
   Record: report `legs.combat-stage/settle/drive-to-g6-end/kill`.
2. **Corpse START re-taken — HISTORICAL method, retained doctrine.**
   `corpse-start` leg: `corpse=44310 hp=0(REQUIRE 0) quest=Progress
   active=True meat=0 container(probe)=1 cycle=[ABSENT]`,
   `goneAtStart=false`. A despawned chain reads `HARNESS/corpse-gone`,
   never a loot failure (G7 split §H convention).
3. **Observe-only watch — FINAL, FROZEN.** 149 wakes / 11622 detection hits /
   0 loot hits over 196.4 s exec; quest-side fragments name the corpse with
   container + lootability and dispatch nothing. Record: §a.

## (c) Proves / does-NOT-prove boundary

Frozen contract — recognizes the corpse + observes lootability, stops there:

Proves:
- Dead-target→corpse promotion: the pinned 3475 selection's dead transition
  populates a per-bot corpse record (ObjId + TemplateId + QuestId +
  detecting cycle/time), quest-251/3475-guarded so irrelevant dead NPCs are
  ignored.
- Identity continuity: the same ObjId is held across wakes (engine: live Npc
  ObjId X stays corpse X until despawn); a recorded ObjId resolving live is
  dropped as recycled, a gone ObjId reads `:gone=true` with no container
  claim.
- Read-before-act: the loot-container probe mirrors `GameplayActor.Loot`'s
  legality gates (owner resolves, flat distance within
  `LootingContainer.MaxLootingRange`, non-empty container) as reads only —
  never opening, mutating, or transferring.
- Zero-action proof: recognition dispatches NOTHING — no Loot in the
  `Dispatch` switch (Target/Move/Stop/AutoAttack only), no `OpenBag`, no
  container mutation, bag 4058 stays 0.

Does-NOT-prove (explicitly out of scope, no claim — report `notClaimed`:
loot, objective-credit, turn-in, rotation, kill-teardown, any other quest):
- Loot dispatch (no `Loot` verb fired, container never taken).
- 4058 acquisition (bag count unchanged, `item4058Delta=0`).
- Objective credit (`creditDelta=0`; `SetObjective`/Ready path untouched).
- Ready status (quest stays Progress by assertion).
- Return travel, Talk/`InteractNpc`, turn-in, rewards.
- Persistence across restarts, multi-bot scale, any other quest.

## (d) Freeze rule

G7b is FROZEN at PASS-BEHAVIOR. No new verbs, no new acceptance criteria, no
loot firing on the G7b path. Regressions may REPAIR ONLY: a regression
restores the frozen chain (dead pinned 3475 → named corpse + `lootable=true`
fragment → zero dispatches → Progress/zeros) without widening scope; firing
loot is a G7c gate with its own START, not a G7b follow-on. Historical
FAIL/UNKNOWN labels elsewhere stay as run records.

## (e) Reusable E2E doctrine (lessons)

1. **Dead-target→corpse promotion:** the kill's pinned selection is the only
   corpse source — record from the target-dead / no-selection-with-pin
   withdrawal, never from a world scan.
2. **Identity continuity:** same-ObjId hold is mandatory; live-again means
   recycled (drop), gone means `:gone=true` (no container claim).
3. **Read-before-act:** probe lootability with the consumer's own legality
   gates as pure reads; policy stays in the verb (`Loot`), the observer only
   mirrors its preconditions.
4. **Zero-action proof:** an observe gate proves the negative — audit-trace
   Loot absence + container-count unchanged + bag-count unchanged, not just
   "no crash".

## (f) Architectural debt (follow-up ONLY — not G7b work)

- Corpse memory is a wake cache (256-entry full-clear bound), not a
  lifecycle-aware store; no despawn subscription, no per-corpse TTL.
- Probe duplicates `Loot`'s preconditions by reading instead of sharing a
  predicate; drift risk if `Loot`'s gates change.
- `gate`-leg `passed=false`-with-PASS-detail encoding (quirk noted in §a):
  future gates should emit a leg `passed=true` on PASS-BEHAVIOR.
- Cleanup gap recorded in-report: bridge deactivate threw for the networked
  char (`cleanupSummary=UNAVAILABLE`, registry entry left in place) — lane
  hygiene, not behavior.

## (g) G7c handoff

Starting state: corpse 44310 recognized quest-side with `lootable=true`, container 1, quest 251 ACTIVE/Progress, bag 4058 at 0. Task: from that recognized corpse, dispatch the canonical `Loot` exactly once through the real engine path so 4058 enters inventory with Q4 conservation holding; stop before credit assertions (objective/Ready belong to G7d).
