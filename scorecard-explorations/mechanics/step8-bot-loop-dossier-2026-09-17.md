# Step 8 — Final Exit Dossier: ONE real bot loop, no fixture repair

Status: **LOOP PROVED** at layer **L** (live authenticated server) for the named
scope below. H (human/client feel) stays **UNKNOWN**.
Date: 2026-09-17. Workstream J, step 8 (`ROADMAP.md:354`).

Baseline: HEAD `533bfcae5becc0264946dbdd1ecde3141595b8b3` + dirty tree
(`git diff --check` CLEAN; 25 files modified, 7 untracked — all this wave's work).

Evidence artifact (authoritative): `/root/aaemu-e2e-b1housing/logs/step8-bot-loop-report.json`
(+ `.md`). Lane: `/root/aaemu-e2e-b1housing` (DB 56306, login 7337, game 7339,
bridge 7360, compose project `aaemu_b1housing`).

---

## 1. The named loop (one bot, one continuous session)

| # | Leg | Outcome | Evidence (from the report) |
|---:|---|---|---|
| 1 | kit-opt-in (the ONE labeled grant, **before** the loop) | **PROVED** | designs 1, certs 15 = required 15 |
| 2 | setup positioning (labeled, not traversal) | **PROVED as disclosed setup** | 209 engine-legal areas; bot staged at area centroid `15023.415,14276.028,120.744` |
| 3 | claim-place-plot (action 1) | **PROVED** | `Completed`, house 13, template 267, `currentStep 0`, design 1→0, certs 15→0, money 0→0 |
| 4 | observe → `hasLandPlot` | **PROVED** | `hasLandPlot true`, `plotConstructed false`, frame ObjId 44137, distance 0 |
| 5a | **recovery**: same-key retry refused, world unchanged | **PROVED** | `Rejected / RejectedAction: design item 15596 not found in inventory`; ownedHouses still 1; `recovered true` |
| 5b | **recovery**: no-frame bot dispatches **no** request | **PROVED** | `dispatched false`, `NoRequest`; that bot owns 0 houses |
| 6 | construct-plot (action 2, closes the loop) | **PROVED** | `Completed`; target ObjId **44137** (the resolved frame, not a skill id); skill 18553; step 0 → **-1**; `plotConstructed true` |
| 7 | conservation (pass 1) | **PROVED** | LP delta **10** = canonical `consume_lp`; gold 0→0; design 0; certs 15→0 |
| 8 | **repeat** — second full pass, distinct legal area + plot | **PROVED** | claim `Completed` house 14; construct `Completed`; `plotConstructed true`; `currentStep -1` |
| 9 | **restart** proof (PID-handle kill + reboot gate) | **PROVED** | killedPid 3620988; pass1 **byte-equal** (`0/267/-1`, owner 1); pass2 **byte-equal** (owner 3) |
| 10 | post-restart live re-read (durable object, not just a row) | **PROVED** | same character 1 re-entered; house 13 live again as ObjId 256, `currentStep -1`, `plotConstructed true`; unfinished-frame resolver correctly reports 0 |

**No fixture repair during the run.** Disclosed/limited, and only before the loop:
one labeled `homestead kit` opt-in (step-3 contract) and one labeled `homestead move`
setup positioning (same shape/label as the existing needs-farm `farm place` op).
In-run: no GM grants, no money/labor injection, no house-row writes, no state
overrides, no request completion, no injected events.

## 2. Loop-completeness checklist (ROADMAP required legs)

| Required leg | Verdict | Basis / gap |
|---|---|---|
| Acquire starting means | **PARTIAL (seeded, labeled)** | kit opt-in is a labeled grant; ordinary (unseeded) acquisition remains unproved — separate follow-up, as ROADMAP allows |
| Discover eligible targets | **PROVED** | 209 engine-legal housing areas resolved from canonical rule data; the action resolves the frame's own ObjId (44137) — **no fixed stand-in** |
| Travel and reach | **NOT PROVED by ordinary movement** | the loop owns no ordinary-movement drive on this lane; positioning is a disclosed setup-step. Reach is asserted *as a precondition* (frame within the skill's cast range at dispatch) |
| Select affordable/legal actions | **PROVED for this loop** | construct is gated on `Labor >= 10` and `HasLandPlot`; planner labor-gating is pinned at layer A |
| Plant/grow/harvest/craft/construct | **PROVED for construct-plot only** | exactly the two bound actions (claim, construct); plant/harvest/craft remain out of scope |
| Conserve resources and ownership | **PROVED** | design 1→0 once; certs 15→0 exactly the derived requirement; gold 0→0; LP delta 10 = canonical charge; ownership 1 plot per bot |
| Recover and repeat | **PROVED** | two named refusals recovered from (5a idempotency, 5b no-frame) + a second complete pass |
| Retain durable state | **PROVED** | byte-equal house rows across a real restart **and** a live post-restart object re-registration |
| Human/client experience | **UNKNOWN** | H is Josh-owned; never inferable from automation |

## 3. Engine findings (recorded, not worked around)

1. **Reach-gate asymmetry.** `GameplayActor.MaxInteractRange` is 25 m, but skill
   18553's own engine `max_range` is **10 m**. A construct dispatched from beyond
   10 m is refused by the skill pipeline with `TooFarRange` (observed live:
   `TooFarRange targetDist=13.45, maxRangeCheck=10 ... Skill 18553`,
   `logs/game.log`). The actor's pre-flight therefore admits a dispatch the engine
   will refuse. **Not fixed here** (no new breadth under the freeze) — recorded so
   the reach leg is judged against the gate that actually refuses.
2. **Construct effects are asynchronous.** Skill 18553 has `casting_time 10000` ms;
   the request reaching `Completed` is *not* the world postcondition. Completion is
   observed only via the polled live projection (`WaitForConstructedAsync`).
   Any test asserting `stepAfter` at request-completion time would be wrong.
3. **Persistence lag.** House rows are written by a `SaveManager` tick, not
   synchronously with the action; the explicit bridge `save` pass is required
   before any pre-restart snapshot (the B1 housing precedent).
4. **`Area.Id` is 0 for every housing area** on this world, so area identity cannot
   be distinguished by id; the loop distinguishes candidate areas by centroid.

## 4. Code changes landed (this step)

| File | Change |
|---|---|
| `AAEmu.Game/.../Goap/Actions/HomesteadActions.cs` | **Deleted the bare `Interact(ScarecrowBuildSkillId)` stand-in fallback**; added `ResolveOwnedUnfinishedFrame`; a missing frame now returns **no request** instead of a fabricated target |
| `AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs` | `homestead` seam extended: `positions` (engine-legal areas), `move` (labeled setup, like `farm place`), `construct`/`constructRetry`, and `observe` now reports `plotConstructed`, frame id/ObjId/distance, `buildSkillRange`, live house ObjId |
| `AAEmu.IntegrationTests/E2e/HomesteadBotLoopStep8E2eTests.cs` | **NEW** — the loop (claim → construct → recovery → repeat → restart → conservation → post-restart live re-read) |
| `AAEmu.UnitTests/.../HomesteadClaimPlacePlotTests.cs` | +3 tests: labor requirement == canonical `consume_lp`; no frame → no request; owned frame → request targets the frame ObjId with the build skill (9 → 12) |

Pre-fix #2 (`OwnedHouseId`): **already correct in production** — no `AAEmu.Game`
code reads `OwnedHouseId`; the provider uses the live housing scan only, and
`GoapRuntimeTests.LandPlot_StaleMemoryIdWithoutLiveHouse_False` enforces the stale
guard. No change was needed; the requirement is met and test-enforced.

## 5. Full gate re-run (exit validation)

| Suite | Result |
|---|---|
| CombatExecutorTests (step 6, independent re-run) | **9/9 passed** |
| HomesteadClaimPlacePlotTests (step 7) | **12/12 passed** (9 prior + 3 new) |
| GoapPlannerTests | **17/17** |
| GoapRuntimeTests | **18/18** |
| HeadlessSessionProvisioningTests | **12/12** |
| HomesteadRuntimeTests | **12/12** |
| GameplayActorM53CoreSurfaceTests | **13/13** |
| GameplayActorHouseBuildActionsTests | **14/14** |
| **Unit total** | **107/107 passed, 0 failed** |
| Step-8 loop E2E (live lane) | **1/1 passed** (run twice consecutively after the final edit) |
| Step-7 E2E `HomesteadClaimPlacePlotE2eTests` (regression) | **1/1 passed** |
| `git diff --check` | **CLEAN** |

Command (live E2E), with `E2E_ROOT=/root/aaemu-e2e-b1housing`, `E2E_DB_PORT=56306`,
`E2E_LOGIN_PORT=7337`, `E2E_GAME_PORT=7339`, `E2E_STREAM_PORT=7350`,
`E2E_BRIDGE_PORT=7360`, `COMPOSE_PROJECT_NAME=aaemu_b1housing`, `E2E_GAME_HOST=127.0.0.1`:

```
dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj -c Debug --no-build \
  --filter-class "AAEmu.IntegrationTests.E2e.HomesteadBotLoopStep8E2eTests"
```

Unit suites: `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Debug --no-build --treenode-filter=/*/*/<Class>/*`.

## 6. NOT PROVED — remainder (explicit)

- **Ordinary acquisition** of the design/certificates (the kit is a labeled grant).
- **Travel/reach by ordinary movement** (positioning is disclosed setup; the reach
  gate itself is asserted, not traversed). The 25 m vs 10 m asymmetry above is open.
- **Plant / grow / harvest / craft** legs — out of the loop's scope entirely.
- **H (human/client feel)** — UNKNOWN; Josh-owned.
- **Ten-bot / population claims** — explicitly not supported by this single-bot loop.
- **Seeded-run ≠ unseeded livelihood**: this is a labeled seeded start state.

**Verdict:** the one named loop (claim-plot → construct-plot, with recovery, repeat,
restart and conservation) is **PROVED at layer L** with no in-run fixture repair.
The seeded-acquisition, ordinary-travel and out-of-scope action legs remain
**NOT PROVED** and are named above rather than absorbed.
