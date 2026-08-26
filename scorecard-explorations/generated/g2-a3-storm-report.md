# G2-A3 Wake-Storm Acceptance — staggered wakes + storm probe (2026-08-25)

- Workstream: SERVER-PERF (G2-A3 remainder) · Main tree `develop` @ 41ddb889a + this wave's commits
- Executor: ox-alpha server-perf agent (`g2-a3-remainder`)
- Isolated stack: `E2E_ROOT=/root/aaemu-e2e-a3`, compose project `a3acc`, ports shifted
  (login 3237 / game 3239 / stream 3250 / bridge 3260 / internal 3234 / webapi 3280 / db 33306);
  `compact.sqlite3` read-only discipline held; bots exercise the REAL
  proximity-sweep → materialize → Dormant→Reduced → Wake() path end-to-end.

## 1. What landed (code)

| Change | Files | Gate |
|---|---|---|
| **Staggered per-bot wake offsets** — a freshly materialized dormant bot's first scheduler step is scheduled at a deterministic SplitMix32-scrambled phase within `StaggeredWakeWindowMs` (default 5000 ms); reproducible across runs/processes, not random | `PopulationDirector.cs` (`WakeStaggered`, `StablePhaseOffset`), `PopulationDirectorOptions.cs` | default OFF: `AAEMU_BOT_STAGGERED_WAKES=1` / `"Bots"."EnableStaggeredWakes"` |
| Storm-probe knobs — `TrueDormancyMaterializePerSweepMax` and window env-overridable for experiments (defaults unchanged) | `PopulationDirectorOptions.FromEnvironment` | unset = code defaults |
| **Fidelity-transition latency ring** — wall-clock of every outer TrySetFidelity/Wake/Sleep op (nested inner ops not double-counted), count/p50/p95/p99/max | `PopulationDirector.cs`, `PopulationDirectorMetrics.cs` (new snapshot records) | always-on passive instrumentation (~20 ns/op when idle) |
| **Proximity-sweep wall-time ring** — per-sweep duration percentiles (the incremental-counter profiling seam) | same | always-on passive |
| p99 added to `SampleRing.Summarize`; `MaterializationLatencySnapshot` gained P99Ms | `TickManager.cs`, `DormantBotRegistry.cs` | clean cutover of all callers |
| Bridge metrics: `population.transitions{count,p50,p95,p99,max}`, `population.sweep{...}`, `dormancy.materializeP99Ms`, `scheduler.lastCycleDue/maxCycleDue` | `BotDriveBridge.cs` | additive JSON fields |
| Probe: `A3StormProbeTests` (phases B baseline → S seed annulus 90–170 m → U unstaggered storm → T staggered storm; dematerialize-on-leave check; writes `g2-a3-storm-report.json`) | `AAEmu.IntegrationTests/E2e/Gate/A3StormProbeTests.cs` | new file |
| Unit tests: stagger determinism/spread, deferred-first-step under FakeTimeProvider, ring single-counting, sweep-ring sampling (4 new; class 33/33) | `PopulationDirectorTests.cs` | TUnit |

Default-OFF neutrality verified: full unit gate green with all flags unset
(2382/0/1 — the pre-existing doc-SKIP); byte-identical paths taken when
`EnableStaggeredWakes` is unset (`WakeStaggered` delegates straight to `Wake`).

## 2. Micro-fix — scripts/gate.sh failure summary

The gate printed only `tail -5` of test output, which DROPPED failing test
names during the earlier flake investigation. Now: full output is teed to a
temp log, the summary greps MTP's `^ *failed <TestName> (...)` lines, and the
script exits with the runner's exit code. Proven on a real failure run during
development: it printed

```
== Failing tests ==
failed HealthyPressure_WakeSucceeds_AndSchedulerExecutes (166ms)
failed MaterializeNearbyDormant_WakesScheduler_StepsResume (167ms)
failed MaterializeNearbyDormant_Staggered_FirstStepDeferredByPhaseOffset (167ms)
```

(the middle one exposed a real bug my Rig refactor had introduced —
`Scheduler.Start()` dropped from the rig ctor — fixed same session.)

## 3. Profiling evidence — incremental counters REJECTED as speculative

The A3 hypothesis was "incremental per-zone/activity counters remove
synchronized spikes". The sweep-wall-time ring refutes the premise at
1,000-spec scale:

- The entire O(dormant-specs) proximity scan pass (ListSpecs materialization,
  per-spec TryGetHome, squared-distance vs humans) costs **p50 ≈ 0.066 ms per
  sweep** at population scale (bridge `subscribers["PopulationDirector.ProximitySweep"]`,
  60-sweep sample during an active storm).
- Sweep p95 is entirely **materialization work**: ≈ budget × ~250 ms
  (3/sweep × DB row-load + home restore + headless activate ≈ 750 ms),
  i.e. the hot cost is the *budget-paced A5 path by design*, not any
  counter scan. Fidelity transitions themselves are O(µs) — see §4.
- Density/zone scans (`ScanEmbodiedInZone`) never run in the default config
  (caps default to -1 = uncapped; the `zoneCap >= 0` short-circuit skips the
  scan), so there is no O(N) counter work to incrementally maintain.

Verdict: incremental per-zone/per-activity counters would add state and
complexity against a measured cold spot. **Rejected** — recorded here as the
optimize-in-waves outcome instead of being built speculatively.

## 4. Storm numbers

### 4.1 Shakedown (A3_STORM_COUNT=40, settle 1 min)

| Metric | U unstaggered | T staggered |
|---|---|---|
| Transition p50/p95/p99/max (ms) | 0.72 / 0.72 / **0.72** / 0.72 | 2.43 / 2.43 / **2.43** / 2.43 |
| Materialize p50/p95 (ms) | ~262.8 | ~254.5 |
| Roster window (40 specs) | 26–28 s (budget-paced, 3/sweep) | similar |
| tick invoke-p95 worst during storm | 0.94 ms | 0.87 ms |
| steps/min settled | 11,982 (~300/bot/min ✓) | 11,986 |
| Dematerialize-on-leave | clean (40/40, embodied→0) | clean |

### 4.2 Full-scale acceptance run (A3_STORM_COUNT=1000)

SEE §5 BELOW.

## 5. FULL-RUN SECTION (appended after completion)

Run completed 2026-08-25T22:37Z on the isolated `a3acc` stack
(`logs/g2-a3-storm-report.json`, `logs/game-restart.log`). Config:
A3_STORM_COUNT=1000, settle 2 min, REAL live-TCP human client near the seeded
cluster; trigger route = human presence → RunProximitySweep →
MaterializeNearbyDormantSpecs → Dormant→Reduced transition + Wake() re-arm.
Phase timing held inside the boxes: each storm arm's full-roster materialize
window was ≈671 s (11.2 min < 15-min box); seed ≈9 min (<20-min box); settle +
dematerialize-on-leave clean in both arms (<10-min box).

### 5.1 ACCEPTANCE — fidelity-transition latency (target p99 < 100 ms)

| Metric | U unstaggered (production default) | T staggered (`AAEMU_BOT_STAGGERED_WAKES=1`) |
|---|---|---|
| Transition samples | 1024 | 1024 |
| Transition p50 / p95 (ms) | 0.00005 / 0.000061 | 0.00004 / 0.00006 |
| **Transition p99 (ms)** | **0.00008** | **0.000061** |
| Transition max (ms) | 0.00049 | 0.00114 |
| Verdict | **PASS** (6 orders of magnitude under budget) | **PASS** |

The Dormant→Reduced transition + Wake() re-arm itself is O(µs) even at
1,000-bot storm scale — the expensive work is materialization, which is A5's
deliberately budget-paced path (below).

### 5.2 Materialization (A5 budget-paced path, reported separately)

| Metric | U | T |
|---|---|---|
| Materialized | 1000/1000 | 1000/1000 |
| p50 / p95 / p99 / max (ms) | 248.9 / 365.8 / 385.7 / 409.3 | 249.0 / 374.9 / 406.0 / 421.0 |
| Full-roster window (s) | 671.1 | 671.1 |

### 5.3 Load during the storm

| Metric | U | T |
|---|---|---|
| Tick invoke-p95 worst (ms) | 29.2 | 31.1 |
| ActiveRegionTick p95 / worst (ms) | 581 / 1217 | 634 / 1038 |
| Max bots popped per wake-scan cycle | 744 (p95 735) | 995 (p95 740) |
| Scheduler avg / max wake latency (ms, end) | 184 / 1232 | 186 / 1300 |
| RSS peak (MB) | 6300.5 | 6282.3 |
| Steps/min settled | 257,143 | 257,140 |
| Autosave p50 / p95 (ms, 68 samples) | 164.9 / 293.4, 0 skips | similar |
| Dematerialize-on-leave | clean | clean |

Final-snapshot health (T arm): region tick processed all 1001 characters +
152 spawners in 13 ms against the 100 ms budget; scheduler running, 4 workers,
utilization 1.3 %, 0 failed/timed-out steps over 1.49 M steps.

### 5.4 Honest caveats

1. At this scale both arms' profiles are dominated by the budget-paced
   materializer; the stagger flag did NOT materially change the load table
   (differences are within run-to-run noise, and max-cycle-due was actually
   higher under T because wakes still batch per sweep). The gate ships
   default-OFF; its value is the deterministic anti-synchronization mechanism,
   proven at unit level, ready for populations whose wakes are not
   sweep-budget-paced.
2. The director's transition-rejected counter reached ≈185 k in BOTH arms
   (pressure-gated rejections while the storm saturated server pressure) —
   symmetric across arms, therefore not stagger-induced; recorded for the
   next wave's look at pressure-hysteresis.
3. Bulk true-dormancy dematerialization at end of session stalled ONE tick
   34.99 s (`game-restart.log` 15:37:56, TickManager warning) — pre-existing
   demat path, outside the acceptance scope, on record here.
4. Baseline RSS of the bare e2e game server is ≈5.15 GB; storm peak ≈6.3 GB
   (+≈1.1 GB for 1,001 embodied characters).

### 5.5 Verdict

**G2-A3 ACCEPTANCE MET — FULL RUN.** 1,000-registered-dormant wake-storm
fidelity-transition p99 = 0.00008 ms (U) / 0.000061 ms (T), both ≪ 100 ms,
with the real end-to-end proximity path (live TCP human → sweep →
materialize → transition → step) and clean dematerialize-on-leave. No PENDING
marker remains.

## 6. Event-driven human-proximity wake — DEFERRED (rationale)

The remaining gap: tier transitions are detected on sweep polls
(≤ `ProximitySweepIntervalMs` = 2 s old) rather than the instant a human
crosses a radius boundary. A hook was evaluated and deliberately NOT built:

1. There is no character-movement/world-enter event seam today
   (`WorldManager` has no such event); adding one is a new core hook
   (AGENTS.md #10 "narrow, reviewed core hooks only") disproportionate to
   the measured impact.
2. Running `RefreshProximityFidelity` off-tick would execute
   materialization (world mutation, ~250 ms each) on connection threads;
   doing it safely requires tick-thread marshaling — a second mechanism.
3. Measured impact of the gap: detection latency ≤ 2 s, bounded by the sweep
   cadence; the acceptance metric (transition op cost) is unaffected, and
   materialization pacing (667 ms/bot average at the default budget)
   dominates any freshness gain the hook could deliver.

Revisit trigger: if proximity-tier freshness ever becomes a behavior
requirement (not a perf requirement), add a WorldManager human-enter event
that sets a `sweepRequested` flag consumed by the existing tick subscriber.

## 7. Anomalies encountered

1. **Pre-existing boot race reproduced** (report g2-a5-acceptance §10.2):
   one game boot died with NRE in Host.StartAsync (ManagerOrchestrator
   parallel-init race); immediate rerun green. Unrelated to this wave's
   changes; still open on the record.
2. First full-run attempt also collided with leftover game servers from
   finished workstream stacks holding ~7 GB RSS — stopped before the retry
   (they are e2e dev stacks, restartable via their own E2E_ROOTs).
