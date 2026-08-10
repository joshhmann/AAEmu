# M6-light sideload — Roam + Safety + Behaviors (extension note)

> Tai (builder) · 2026-08-06 · card t_5aec3250 · branch feat/m6-light-sideload @ 093b52f0
> Extends the M2b pilot (t_db550fca @ 0f736e5a). Companion artifact:
> `PlayerbotM6LightTests` (24 rigs) + the pilot suite (regression guard).

## What this layer is

A parallel-track expansion of the M2b PlayerBotController from quest-drive-only
toward the full M6.0 framework (ROADMAP §6.2/6.3), as Josh specified: **roam,
safety, behaviors** — additive only, in parallel with M2a/M2c. It is NOT the
full M6 (no BotManager/ticks in the world loop, no follow/defend/loot, no
persistence); it is the M6-light slice: the primitives + explicit state
machine the full framework will compose, with unit rigs proving each one.

Composition rule (AAEmu AGENTS.md #9/#10) is structural:

- **Ordinary Character records** — movement writes go through the character's
  real `Transform` (`Local.SetPosition`, the same facility `Simulation.MoveTo`
  uses for NPCs); no bot position model exists.
- **Normal gameplay services** — quest work still flows exclusively through
  the M2b `PlayerBotController` → engine paths (`AddQuest` / `UnitEvents` /
  `DoReportEvents`). The behavior layer only SEQUENCES; it never touches
  quest state.
- **Additive layer** — three new files under `AAEmu.Game/Models/Game/Bots/`,
  zero changes to the pilot's `HeadlessSession`/`PlayerBotController`, zero
  core hooks. No new movement system: `BotPath` uses `MathUtil`
  (`CalculateDistance` / `AddDistanceToFront` / `CalculateAngleFrom`) and the
  same checkpoint/arrival model as `Units/Route/Simulation`
  (`RangeToCheckPoint` 0.5f).

## The three primitives

### 1. Roam — `BotPath` (waypoint/pathing)

An ordered waypoint list walked with **bounded per-tick steps**
(`MaxStepPerTick` — a tick may never move further than its budget) and the
engine's arrival-radius model (a leg completes when within
`ArrivalRadiusDefault` 0.5f; a step that LANDS within the radius completes in
the same call). Modes: `Once` (finish), `Loop` (patrol wrap), `PingPong`
(walk out and back). Z interpolates proportionally; pure-vertical legs
(waypoint directly above/below) are handled explicitly.

Bounded roam is enforced at proposal time: `AllWaypointsWithin(center,
radius)` rejects any route with a waypoint outside the bot's safe zone —
the behavior controller refuses such routes (`TryStartRoam` returns false).

### 2. Safety — `BotSafetyMonitor` (M6.2 safety-FIRST slice)

Per-tick observer with **first-reason-wins abort semantics** (the latched
`BotStopReason` is the evidence; `Reset()` is the only clearing path):

| Guard | Trigger | M6.2 item |
|---|---|---|
| Stuck | position moved < epsilon for `StuckThresholdTicks` while on a nav leg | stuck detection |
| NavigationTimeout | one leg exceeds `NavigationTimeoutTicks` | navigation timeout |
| OutOfBounds | bot outside `SafeRadius` of home while working | world-state guard |
| InventoryFull | free slots ≤ threshold | inventory-full handling |
| TickBudgetExceeded | session ticks > `TickBudget` | tick budget accounting |

The **combat gate** is the M6-light "no combat until quest-drive needs it"
rule made mechanical: `CombatAllowed` is off by default, and the only caller
that can open it is `PlayerBotBehaviorController.TryGrantCombat()`, which
refuses unless quest-drive is the active state with work pending. Even with
the gate open, `CanEngageCombat` is false while the bot is stopped. There is
no combat implementation anywhere in this layer — the gate is the guard for
the future M6 combat path.

### 3. Behaviors — `BotBehaviorStack` + `PlayerBotBehaviorController`

Explicit states **Idle → Roam → QuestDrive → Return** on a small stack
(higher states preempt lower ones). Tick order is fixed:

1. **Stop handling first** — a latched stop aborts work: manual stops idle
   in place; every other reason safe-returns home (and a bot already home
   just idles — no return-leg re-push loop).
2. **Safety observation** — every work tick passes through the monitor
   (roam legs are navigation; quest ticks are not, so a paused roam leg
   never accrues nav time).
3. **Quest-drive is the primary mode** — while `QuestWorkPending`, the stack
   forces QuestDrive and runs ONE `QuestDriveStep` per tick; roam yields.
   When work reports done, the stack pops back to the preempted state.
4. **State dispatch** — Roam advances the route (and pops to Idle when a
   Once route finishes); Return walks `PathTo(home)` and pops on arrival.

Quest work is injected through the `QuestDriveStep` seam — the natural
implementation is the M2b `PlayerbotQuestDriver`; the behavior layer only
sequences. `Resume()` clears a stop and restores quest-drive primary.

## Rig evidence

`PlayerbotM6LightTests` (24 rigs, all green):

- **Roam (8):** bounded straight-line arrival, step clamp never exceeds
  `MaxStepPerTick`, arrival-radius early snap, Z interpolation (vertical +
  diagonal), loop wrap, ping-pong reversal, bounds guard, single-leg `PathTo`.
- **Safety (7):** stuck threshold, nav timeout, out-of-bounds, inventory-full,
  tick budget, combat gate (default-off / grant / stop-block / reset), first-
  reason-wins + reset.
- **Behaviors (9):** default Idle, roam→idle, out-of-bounds route rejection,
  quest-drive preemption + resume, quest-drive primary blocking roam, safe
  return after stuck stop (reason preserved), manual stop idles in place,
  combat grant only during quest-drive, quest work paused while stopped +
  resumed by `Resume()`.

Regression guard: `PlayerbotPilotTests` 6/6 green (pilot untouched, zero
changes to `HeadlessSession`/`PlayerBotController`). Full gate run in the
card evidence.

## Out of scope (future M6 tracks, deliberately not here)

- Follow / defend-self / assist / loot behaviors (6.3) — need combat + party,
  which are later fidelity tiers.
- Death/resurrection, mount-state repair, unreachable-object recovery (6.2) —
  need live-world facilities the headless rig cannot exercise.
- World-loop tick integration (`BotManager`, tick registration) — the full
  M6.1 core; this layer is the controller-side primitives it will drive.
- Any combat implementation — the gate exists; the system does not.
