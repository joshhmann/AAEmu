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
| Physics warnings same-world | ≤ 2 in any 60s | no-sustained-slow clause: 3+ warnings on one world within 60s = thread cannot keep up (hard fail) |
| Tick overrun warnings | 0/min | "Tick took" / over-budget lines |

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
       Budgets = new GateBudgets()    // or tighter per-stage budgets
   };
   ```
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
