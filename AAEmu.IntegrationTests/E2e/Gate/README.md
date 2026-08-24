# Gate Harness — staged soak runner (P2, slice #10)

Staged density/stability gate per ARCHITECTURE_REVIEW deliverable 8 + 10:

| Stage | Bots | Gate | This harness |
|---|---|---|---|
| 1 | 10 | Correctness: golden route + budgets | `Gate_Stage10_Correctness_Green` |
| 2 | **25** | **FIRST STABILITY GATE — hard stop until H2 lands** | `Gate_Stage25_FirstStabilityGate` (fails hard with "H2 NOT MERGED" when the server build lacks H2 metrics) |
| 3 | 50 | Soak ≥6h: no tick-budget overrun, no unrecovered loops, no DB corruption | `Gate_Stage50_Soak` (`GATE_SOAK_MINUTES=360`) |
| 4+ | 100/250/500/1000 | Profiling → mixed fidelity → region/event → final | Add a stage row (below) |

Rule (spec §15/§21-13): **1,000 persistent citizens, not 1,000 thinking clients.**

## What it does

1. Boots the REAL E2E stack (MySQL compose + Login + Game — same binaries as
   prod, canonical `compact.sqlite3`).
2. Embodies N bots through the REAL login/enter-world flow
   (`BotNetworkSession`), then drives golden-route quests through the
   BotDriveBridge (`E2eQuestDriver`).
3. Samples a metrics window:
   - **TickManager invoke p95/max** + **ActiveRegionTick worst pass** (H2
     bridge `metrics` command; worst-of-N samples across the window),
   - **PlayerBotScheduler wake latency** (bridge surface; reported n/a when
     the citizen path isn't wired — never a silent pass),
   - **DB writes** (MySQL `SHOW GLOBAL STATUS` Com_* deltas across the
     window, normalized per bot per minute),
   - **physics warning rate** + **tick overrun rate** (game-log scan for
     "Physics thread is running slow", "Tick took", ActiveRegionTick
     over-budget lines).
4. **Fails hard on the first budget overrun** — every budget verdict is
   asserted; a red verdict fails the stage.
5. Writes one markdown evidence file per stage under
   `<E2E_ROOT>/logs/gate-<stage>-<timestamp>.md`.

## How to run

```bash
# All gate stages except the 6h soak (soak is env-gated):
cd /root/aaemu-dev
dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  -c Release --filter-class AAEmu.IntegrationTests.GateHarnessTests

# One stage (single test):
dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  -c Release --filter-class AAEmu.IntegrationTests.GateHarnessTests \
  --filter-method AAEmu.IntegrationTests.GateHarnessTests.Gate_Stage10_Correctness_Green

# Stage 50 real soak (≥6h):
GATE_SOAK_MINUTES=360 dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  -c Release --filter-class AAEmu.IntegrationTests.GateHarnessTests \
  --filter-method AAEmu.IntegrationTests.GateHarnessTests.Gate_Stage50_Soak

# Stage 50 smoke (minutes, not hours):
GATE_SOAK_MINUTES=5 dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  -c Release --filter-class AAEmu.IntegrationTests.GateHarnessTests \
  --filter-method AAEmu.IntegrationTests.GateHarnessTests.Gate_Stage50_Soak
```

Notes:
- MTP filter flags: this project accepts `--filter-class` (fully-qualified
  class name) and `--filter-method` (fully-qualified method name —
  `Namespace.Class.Method`; a bare method name matches 0 tests).
  `--filter` and `--treenode-filter` do NOT work on the IntegrationTests
  project (unknown option).
- One stack, one suite at a time — the e2e collection serializes with the
  M2b suite; don't run a second stack concurrently (session-registry is
  per-process).
- E2E rig prerequisites (data rsync, compose file) are the same as the M2b
  suite — see `Scripts/e2e/README.md`.

## Scheduler-driven soak — stage 1 (`SchedulerSoakStage1Tests`)

Closes the M6-exit caveat "previous soaks ran with PlayerBotScheduler
DISABLED": boots the stack with `AAEMU_PRESENCE_DEMO=1` + a 10-citizen
`AAEMU_PRESENCE_MANIFEST` roster, so every bot's work flows through the real
`IPlayerBotScheduler` lease/wake path, then samples a 30-minute window.

```bash
SCHEDULER_SOAK_MINUTES=30 E2E_REBUILD=1 \
  dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  -c Release --filter-class AAEmu.IntegrationTests.E2e.Gate.SchedulerSoakStage1Tests
```

- **Run-validity contract:** INVALID unless bridge `metrics` shows
  `scheduler.available=true` AND `totalStepsRun>0` (and still growing at
  window end) — a silently-disabled scheduler can never read as green.
- **Budgets** mirror the repo numerics: `GateBudgets` defaults +
  `GateStages.SoakBudgets` idle-stage overrides (region 200 ms, tick-overrun
  0.1/min); scheduler step timeouts enforced at 0 (zero-tolerance mirrors
  step failures). RSS recorded informational only.
- **Evidence:** `$E2E_ROOT/logs/scheduler-soak-stage1-{ts}.json|.md`.
- **Manifest note:** every roster entry pins an explicit patrol home. Without
  it, `BotPresenceCoordinator.StartFromManifest` skips relocation and hands
  bots whose race-template spawn differs from the Nuian-male default home a
  patrol circle thousands of meters away — run 1 walked all five Elf citizens
  4.3 km into the sea (drowned; blood-decal doodad 878). Engine card: route
  center must follow the bot's actual spawn when no home is configured.

## Budgets (defaults)

| Budget | Default | Meaning |
|---|---|---|
| TickManager invoke p95 | ≤ 100 ms | tick loop's own warn threshold |
| TickManager invoke max | ≤ 250 ms | single-pass hard ceiling |
| ActiveRegionTick worst pass | ≤ 100 ms | H2 hard budget |
| ActiveRegionTick overruns | 0 | any over-budget pass is red |
| Scheduler avg wake latency | ≤ 250 ms | enforced when scheduler ran steps |
| Scheduler max wake latency | ≤ 1000 ms | |
| Scheduler step failures | 0 | any throw is red |
| DB writes | ≤ 500/min/embodied-char | catches AI-step-loop writes (calibrated: 277 measured on stage-10 golden route @ AutoSave 0.2; denominator = network bots + presence citizens when `AAEMU_PRESENCE_DEMO=1`) |
| Physics warnings | ≤ 0.1/min | physics thread running slow (calibrated: 0.031/min 6h soak pre-fix, 0.067/min post-fix — t_eecc5604; 0.1 ≈ 1.5-3.3× headroom) |
| Physics warnings same-world | ≤ 30 in any 60s | no-sustained-slow clause: 31+ warnings on one world within 60s = thread cannot keep up (hard fail; ceilings 3-in-8s on the 2026-08-10 re-soak and 8-in-59s per world on the 2026-08-11 360-min re-soak boot storm → 30 ≈ 3.75× headroom) |
| Tick overrun warnings | 0/min | "Tick took" / over-budget lines |
| Autosave duration p95 | ≤ 4000 ms | save-pass p95 (recalibrated 2000→4000 — see below) |
| Autosave duration max | ≤ 10000 ms | worst single pass ceiling: a one-off commit stall under p95 still freezes the tick — hard fail |

Budgets live in `AAEmu.Commons/Utils/Gate/GateBudgetEvaluator.cs`
(`GateBudgets` record). Tune per stage in `GateStage.cs` — never in the
evaluator.

### DB-write budget normalization (presence-aware, t_b4eb35e9)

The DB-write budget divides the window's `Com_*` delta by the snapshot's
**embodied-character count** — the stage's network bots PLUS the
presence-demo citizens (`PresenceBotCount`), which the runner reads from the
same `AAEMU_PRESENCE_DEMO` / `AAEMU_PRESENCE_BOT_COUNT` env contract the game
server consumes. Presence citizens persist at the same save cadence as
network bots, so their writes are normal load, not a write loop; without the
presence-aware denominator a stage-10 presence run false-REDs (measured
529.06/500 on bots only vs 264.53/500 per embodied char — inside the 266-277
calibration band, 2026-08-09). A plain run (`AAEMU_PRESENCE_DEMO` unset) has
`PresenceBotCount=0` and normalizes per bot exactly as before.

### Autosave budget recalibration (ah-conservation load shape, t_0d576fdb)

The autosave p95 budget is 4000 ms — recalibrated from 2000 ms on 2026-08-13
because the Stage10 load shape changed, not because the save path regressed.

**Mechanism.** The bridge `save` trigger (`BotDriveBridge.HandleSave`) dirties
ALL houses and calls `DoSave(true)` → `saveAllCharacters=true`, which forces
EVERY in-world character through the save pass (not just dirty ones). The
`ah-conservation` auction scenario (t_52b2b084) provisions a 25-actor fleet
(money + items + auction lots + mail), so each forced full save persists the
fleet's accumulated state → pass cost grows from ~34 ms (pre-scenario
baseline) to ~2 s at gate scale. `AuctionManager.Save` persists only dirty
lots (REPLACE INTO) — the cost is fleet state, not an auction-write loop.

**Measurement.** 8 post-rebuild Stage10 runs on 2026-08-13 (10 bots, 3-min
window): steady p95 band **1945–2666 ms**, 4 of 8 over the old 2000 limit;
one run at 543 ms (fleet not in its active phase — pass cost is
fleet-overlap-dependent). No quest regressions in any run; the ah-conservation
scenario itself passes in-gate (conservation exact 247250/247250).

**Why 4000.** ~1.5× headroom over the worst measured pass (2666 ms); the
plain shape (no ah-conservation template) measured 547 ms — ~7.3× margin
(34 ms is the pre-scenario baseline). The 10000 ms single-pass ceiling is
unchanged — a genuine save-path stall still trips the max verdict.

**25-homesteads note.** The M3b homesteads stage declares no
ScenarioTemplates, so the global default raise doubles its budget by
inheritance (2000 → 4000). Bounded: H2-gated, no hardcoded 2s assertions,
10000 ms ceiling still the stall guard. Revisit a per-stage override when
H2 lands.

## How to add a stage (100/250/500/1000)

1. Add a config row in `AAEmu.IntegrationTests/E2e/Gate/GateStage.cs`:
   ```csharp
   public static GateStageConfig Stage100 { get; } = new()
   {
       Name = "100-profiling",
       BotCount = 100,
       RequireH2 = true,
       WindowMinutes = 5,
       QuestSubset = 1,               // profiling focus, not correctness
       ScenarioTemplates = ["level22-gate", "ability-gate", "cat34-daily"], // P1 t_5efae4f1
       Budgets = new GateBudgets()    // or tighter per-stage budgets
   };
   ```
   `ScenarioTemplates` runs the template rig (parameterized bot test rigs:
   level/abilities/prereq-gated quest scenarios through the IGameplayActor
   contract) against the live stack as part of the stage — every template
   must PASS or the stage is red. Empty array = skip (the pre-template
   stages' behavior).
2. Add a test entry point in `GateHarnessTests.cs`:
   ```csharp
   [Fact] [Trait("Category", "e2e")]
   public async Task Gate_Stage100_Profiling()
       => Assert.True((await GateSoakRunner.RunStageAsync(GateStages.Stage100)).Passed);
   ```
3. Add the deliverable-8 gate rule to the README table.
4. For mixed-fidelity stages (250+), the bridge already exposes
   PopulationDirector fidelity counts (`population` block of `metrics`) —
   extend `GateMetricsSnapshot` + the evaluator with a
   `dormant+reduced ≥ X%` verdict.

## Evidence contract

Every stage run writes `gate-<stage>-<timestamp>.md` under
`<E2E_ROOT>/logs/` with the budget table (measured/limit/verdict), the
failure list (each entry = a regression card), and the raw snapshot JSON.
The stage-10 file is the P2 completion evidence; Rei re-runs
`Gate_Stage10_Correctness_Green` to attest.
