# Deferred — NOT NOW register

Exploration record. Deprioritized items are preserved here, not lost.
Each entry carries its **reason** for deferral and its **exact revisit trigger**.
Nothing in this file is scheduled work; promotion back to an active lane
requires its trigger to fire.

- Convention: `NOT NOW` = deliberately parked. `NO-GO` = do not attempt
  without the stated evidence first.
- Related: `dropped-content-register.md` (content cut by data/design, not
  scheduling), `progression-board.md` (active lanes).

## 1. Q8 WAR-HONOR
- **Status:** NOT NOW — deferred by owner.
- **Reason:** Owner deprioritized; no active lane owns it.
- **Revisit trigger:** Owner lifts the deferral explicitly. Nothing else
  promotes it.

## 2. English-DB second server
- **Status:** NOT NOW — held.
- **Reason:** Marginal benefit: affects logs/GM tooling only; no client-text
  change. Cost of a second server (config, content drift, upkeep) exceeds
  the payoff today.
- **Revisit trigger:** Korean-glyph pain bites real debugging — i.e. a
  concrete case where unreadable Korean text materially slows or blocks a
  live debug session.
- **Note:** `LocalizationManager` runtime alternative exists (translate at
  display/log time instead of a second server). Prefer that shape if the
  trigger fires.

## 3. Navmesh full-port recorder
- **Status:** NO-GO.
- **Reason:** Full port rejected on cost/risk; no demonstrated need.
- **Revisit trigger:** ONLY on a demonstrated 1.2 cave-float repro — a
  reproducible in-game case of floating/pathing failure inside a 1.2 cave
  that a recorder would resolve.
- **Reference:** Read-only slice proposal at `/tmp/navmesh-teardown.md`.
  Do not expand scope beyond that slice without the trigger.

## 4. A5 12-hour soak
- **Status:** NOT NOW — recommendation-only.
- **Reason:** Long soak proposed as assurance, not tied to a live defect or
  gate.
- **Revisit trigger:** A soak trigger fires — e.g. a stability/memory
  regression, a release-gate requirement, or owner request naming the
  soak as an entry condition.

## 5. Quest-sphere user config step
- **Status:** NOT NOW — needs user machine action.
- **Reason:** Cannot proceed server-side; blocked on the user's machine.
- **Revisit trigger:** User completes the action, then the item returns as
  ready work.
- **Action required (user machine):**
  1. Move `/tmp/quest-sphere-clientdata` to a durable path.
  2. Apply the `Config.Local.json` snippet pointing `ClientData.Sources`
     at that durable path (machine-specific — never shared config).

## 6. H human acceptance
- **Status:** NOT NOW — owner-only by definition.
- **Reason:** Human acceptance can only be performed by the owner; it
  cannot be scheduled, staffed, or executed as a lane.
- **Revisit trigger:** Owner performs acceptance. There is no proxy trigger.

## 7. Phase-2 clamp-beyond-verified + Mira 9632 follow-up
- **Status:** NOT NOW — needs in-game proof.
- **Reason:** Extending the clamp beyond verified bounds (and the Mira 9632
  follow-up) has no standing without observed in-game evidence.
- **Revisit trigger:** In-game proof lands — a reproducible live observation
  (positions, zone, steps) justifying the wider clamp / Mira follow-up.

## 8. Stuck-rider / mate-dragons / well-gather
- **Stuck-rider:** NOT NOW — deferred pending live repro. **Revisit
  trigger:** a reproducible live stuck-rider case.
- **Mate-dragons:** NOT NOW — incompatible (does not fit current
  constraints). **Revisit trigger:** the incompatibility is removed or a
  compatible shape is proposed; until then it stays parked.
- **Well-gather:** NOT NOW — covered-code (existing coverage already
  exercises the path). **Revisit trigger:** a gap in that coverage is
  demonstrated (failing case the current code does not handle).

## 9. DB-volume gate
- **Status:** NOT NOW — deferred pending true shared execution boundary.
- **Reason:** No agreed shared execution boundary exists on which to hang
  a volume gate; gating now would be arbitrary.
- **Revisit trigger:** A true shared execution boundary is defined and
  adopted — then design the gate against it.
- **Note:** Statement counters live on as volume-only telemetry (signal,
  not a gate).

## 10. M10 slice-2 combat-lite, expansion candidates, census U-rows
- **Status:** NOT NOW — unshaped future.
- **Reason:** Ideas without shaping: no spec, no acceptance, no owning lane.
- **Revisit trigger:** Each promotes individually once shaped — i.e. given a
  concrete proposal with scope and acceptance criteria that an owner
  accepts onto a lane.
