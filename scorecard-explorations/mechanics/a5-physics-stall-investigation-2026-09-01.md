# A5 Physics / Tick Timing Stall Investigation Dossier (2026-09-01) — read-only

Durable exploration dossier. Source: the verified read-only investigation
report `/tmp/physics-stall-investigation.md` (SHA-256
`6892e5e0c794e3c129019e5b53af5b6b79220549b68bab1645d951c21609ee01`), re-checked
against this repository (source SHAs verified via `git log`; soak report
`a5-report-ct133.json` re-read via `jq`). **No code/data edited, no tests run,
no commits.** Every measurement below is preserved exactly from the source
report; nothing is re-derived from raw logs that are no longer on disk (see
Limitations).

**Verdict (updated 2026-09-02): memory pressure/swap + background GC/page
faults is a **strongly supported provisional infrastructure root cause**
for the **user-reported current PROD CT133 presence-demo only**, based on
user/live operational evidence. It does NOT explain the 12 h soak
breaches: the soak host had **0 swap** and no in-soak host/GC telemetry,
so the soak-time classification remains **UNKNOWN**. **A5 remains formally
OPEN/UNCLOSED** until a comparable post-change run confirms the warnings
disappear. Budgets are NOT relaxed. The CT133 memory remediation (preferred
action: CT133 → 16 GB) has been applied and the post-change observation is
recorded in section 8: it **strongly supports** the memory-pressure/swap
hypothesis but does **not fully prove** it (residual ~300 ms events keep
another cause open); next closure criteria are continued GC/nettrace
capture, correlation of residual warnings with GC/thread/process/host
telemetry, then a comparable post-change A5 soak with zero budget
breaches (see section 8).**

## Provenance

| Item | Value |
|---|---|
| Repo | `/root/aaemu-dev` (fork joshhmann/AAEmu, ArcheAge 1.2 emulator, .NET 10) |
| Branch | `develop` |
| **HEAD** | `0f8254dc3d914193d432fb842169e9bb07075508` (verified `git rev-parse HEAD`) |
| Source report | `/tmp/physics-stall-investigation.md` |
| **Report SHA-256** | `6892e5e0c794e3c129019e5b53af5b6b79220549b68bab1645d951c21609ee01` |
| 12h soak report | `/root/aaemu-dev/a5-report-ct133.json` (FULL, `passed=false` on timing only) |
| **Soak source SHA** | `1ce4664f96705850136dc9d46999070fac9763fb` (report `commit` field; verified `git log`) |
| 6h canary report | `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json` (FULL window, RSS-fail) |
| Evidence layers | **data** (soak report JSONs + retained on-disk logs) and **code** (repo C# source) only; source-report runtime-log details not verifiable from retained files are marked as such (see Limitations). |
| No live/client/H claims | **No game server launched for this investigation, no client run, no authenticated run.** Evidence is testing/canary operational evidence, not live human gameplay. `H` (human/client feel) is **UNKNOWN**. |

## Sources consulted (as recorded by the source report)

- `STATUS.md`, `ROADMAP.md`, `SCORECARD.md`, `PROJECT-CONTROL.md`
- `Scripts/find-tick-starvation.sh`
- `AAEmu.IntegrationTests/E2e/Gate/A5Tier3AcceptanceProbeTests.cs`
- `AAEmu.Game/Core/Managers/TickManager.cs`
- `AAEmu.Game/Core/Managers/World/WorldManager.cs` (ActiveRegionTick)
- `AAEmu.Game/Core/Managers/World/PhysicsManager.cs`
- `AAEmu.Game/Core/Managers/SaveManager.cs`
- `AAEmu.Game/AAEmu.Game.csproj`, `AAEmu.Game/Program.cs`
- Soak reports and logs under `/root/aaemu-e2e-a5-tier3-sixhour/`
- `/root/aaemu-dev/a5-report-ct133.json`
- Prior RCA transcripts (`t_eecc5604`, `t_1ed9881f`)

Verification performed for this dossier (read-only): `git rev-parse HEAD`,
`sha256sum /tmp/physics-stall-investigation.md`, `jq` reads of
`a5-report-ct133.json` (`commit`, `runAtUtc`, `probe`, `passed`, `budgets`),
and `git log` confirmation that `1ce4664f9`, `1801baf98`, `ccd4ea857`, and
`105b4d5ed` exist in the repo. No repo file was edited by the source
investigation and none is edited by this dossier.

---

## 1. The two distinct failure modes

The A5 Tier-3 dormant-timer soak enforces two separate timing budgets
(`A5Tier3AcceptanceProbeTests.cs:246-248`): region pass ≤ 200 ms and tick
invoke max ≤ 250 ms (the in-engine budgets are 100 ms region /
100 ms tick-warn). They failed independently and are different mechanisms.

### Mode A — ActiveRegionTick overrun (region budget)
- **12h corrected soak** (SHA `1ce4664f`, report `a5-report-ct133.json`,
  FULL 720.000044 min / 721 samples): **6 sampled breaches** —
  region 299 / 288 / 308 / 291 / 297 / 291 ms (budget 200 ms).
- **571 ActiveRegionTick overrun log events in the 12h game log, 570
  in-window** (verified on disk from the retained
  `/tmp/a5-12h-evidence/game-restart.log`: 571 `ActiveRegionTick took`
  events, 570 within the soak window, 1 pre-window boot event), recurring
  at a **~76 s cadence** — actual gap clusters measured from the retained
  log: **75–80 s ×293** and **29–31 s ×126** (the earlier "75–80 s ×375"
  figure is historical triage text from STATUS.md/ROADMAP.md and is not
  re-derived from the retained log).
- **566/571 coincide same-second with physics-slow warnings** (verified on
  disk: 566 of 571 region-event seconds also contain a physics-slow
  warning; the retained log contains 1,348 physics-slow warning lines).
- **14 `TickManager - Tick took` events in the retained 12h log, 10 of
  them > 250 ms** (verified on disk; the earlier "0 `Tick took` lines in
  any available soak log" statement is superseded — see Limitations).
- Every region pass reports **`deferred 0 characters`** — with 0 embodied
  bots the pass has essentially no work (empty character snapshot, no
  mates/slaves, spawner scan with zero players). The wall-clock overrun is
  therefore **descheduling time, not work time**.
- Region distribution over 721 samples: p50 = 5 ms, p95 = 21 ms,
  p99 = 27 ms, max = 308 ms. The breaches are extreme outliers on an
  otherwise 5–30 ms pass.

### Mode B — TickManager invoke-max overrun (tick budget)
- **1 sampled breach: tick max 282.9 ms** (budget 250 ms) at
  `2026-08-30T16:48:02Z`. Critically, **region = 5 ms in that same sample** —
  the tick breach is NOT the region pass.
- The tick loop logged `Tick took` in the retained 12h log: **14
  `TickManager - Tick took` events, 10 > 250 ms** (verified on disk in
  `/tmp/a5-12h-evidence/game-restart.log`); the retained 6h canary logs
  (`game.log`, `game-restart.log`, `soak-run-*.log`) contain **0** `Tick
  took` lines (verified on disk). `ActiveRegionTick` is subscribed
  `useAsync: true` (`WorldManager.cs:437`), so a slow region pass runs on
  the thread pool and cannot block the tick loop — the #1491 fix holds
  (the `find-tick-starvation.sh` pair detector finds 0 pairs in the 6h
  canary logs).
- The 282.9 ms invoke max with region = 5 ms and 0 embodied bots must come
  from a **sync subscriber** (TeamManager 500 ms, BotActionCommandQueue
  Drain, BotChatterService.Scan, BotScheduleService.Scan,
  PlayerBotScheduler.TickDrain, PopulationDirector.ProximitySweep — all
  `useAsync: false`) or from **descheduling of the tick thread itself**
  (GC pause / host steal / thread-pool starvation delaying the async
  dispatch). No per-subscriber breakdown was captured in the report, so the
  attribution is not directly measured.
- Tick distribution over 721 samples: invoke max p50 = 0.045 ms,
  p95 = 14.2 ms, p99 = 15.3 ms, max = 282.9 ms. Again a single extreme
  outlier.

---

## 2. What the evidence rules out

### Software hotspot — NOT supported
- Region pass does ~0 work at 0 embodied (deferred 0 characters, empty
  snapshot); tick p95 = 0.02 ms; scheduler queues/failures/save-skips all 0
  across all 721 samples; DB writes 0; RSS flat (growth 155.9 MB < 512 MB
  budget).
- The pre-remediation deep-copy hotspot (`GetAllSpawners()` +
  per-spawner `GetAllCharacters()`) was already fixed by `1801baf98`
  (reuse one character snapshot + direct `GetActiveNpcSpawners` scan) —
  landed after this run; the 12h run predates it, but the 0-work pass
  profile makes an allocation hotspot an implausible cause of 100–300 ms
  stalls anyway.

### Physics workload — NOT supported
- The dormant soak has **zero rigid bodies** in the physics world (bodies
  are slave-only; `EnqueueAddBody` is dead code for NPCs — verified in the
  prior RCA `t_eecc5604`). The physics thread's real work per iteration is
  ~0 ms; the slow-thread warning fires when the inter-iteration gap exceeds
  one full 40 ms target step and **measures pure thread-descheduling time**,
  not physics cost.
- Physics-warning detail: the retained 12h log
  (`/tmp/a5-12h-evidence/game-restart.log`) verifies **1,348 physics-slow
  warning lines** on disk; the source report additionally records 44
  physics warnings across the 6h canary boots (mostly 65–126 ms gaps) and
  the 12h triage records physics values 278–554 ms tracking the region
  stalls — i.e., both threads descheduled together. The retained 6h canary
  logs verify only 6 physics warnings (70–100 ms @ 15:42:25–35 in
  `game-restart.log`); the 44-warning count and the 65–126 ms distribution
  come from the now-absent `Server.log` (see Limitations).
### GC pause — largely eliminated, no evidence
- `Program.cs:53` sets `GCSettings.LatencyMode = SustainedLowLatency`
  (post-`t_eecc5604` fix, `105b4d5ed`), which keeps full-GC compactions on
  the background concurrent path (STW 2–10 ms class). The prior M6 soak's
  70–459 ms blocking-compaction class was eliminated by this change.
- No GC logs were collected during the 12h run; no GC evidence exists either
  way, but the configured mode makes multi-hundred-ms STW pauses unlikely.

### Scheduler / queue / DB / RSS correlation — none
- DueQueueDepth / EventQueueDepth / InFlight = 0, SchedulerFailures = 0,
  SaveSkips = 0, DB writes delta = 0, RSS growth 155.9 MB — all healthy
  across the full window. SaveP95 distribution over 721 samples: p50 =
  4.17 ms, p90 = 13.13 ms, p95 = 17.18 ms, p99 = 21.33 ms, max = 76.63 ms;
  SaveMax max = 296.044 ms (590/721 samples > 100 ms but far under the
  10 s budget; autosave every 5 min).

---

## 3. What the evidence points to

**Host-level scheduling / CPU steal (process-wide descheduling), with the
classification remaining UNKNOWN because no host metrics were collected
during the soak windows.**

Supporting observations:
1. **Both threads stall in the same second** — 566/571 region overruns
   coincide with physics-slow warnings; the physics thread and the
   thread-pool region pass are independent threads, so a same-second stall
   of both is a process-wide or host-wide descheduling event, not a
   single-thread code issue.
2. **The ~76 s cadence** (actual gap clusters measured from the retained
   12h log: 75–80 s ×293, 29–31 s ×126; the earlier "75–80 s ×375" figure
   is historical triage text) is regular and external-looking: it is not a
   multiple of the 60 s sample, the 5 min autosave, the 2 s proximity
   sweep, or any tick subscriber interval. A periodic external source
   (another container/VM on the same LXC host — e.g., backups, monitoring,
   cron) is the leading hypothesis.
3. **A ~1 s process-wide stall is recorded** in the source report at
   `21:23:11` in the 6h canary runtime log: physics slow 959 / 938 ms (both
   worlds, same second) + ActiveRegionTick 942 ms — a ~1 s full-process
   deschedule with zero workload. **This event is source-report-derived and
   NOT verifiable from retained files** (the `Server.log` that contained it
   is absent; no retained log contains `21:23:11` or 959/938/942 ms values —
   see Limitations).
4. **The 6h canary (first run) had zero timing breaches** (tick max
   19.1 ms, region max 47 ms full-window — the 47 ms sample is the first,
   pre-quiescence sample at 2026-08-29T21:58:08Z; excluding it the region
   max is 33 ms) — the same code, same 1000 dormant specs, same host,
   different 6h window. The stall pattern is not deterministic in the
   process; it varies run to run, consistent with host contention rather
   than a fixed code path.
5. **The 282.9 ms tick max with region = 5 ms** is a tick-thread/sync-
   subscriber deschedule, again not workload (0 embodied bots).

Current host state (measured 2026-09-01, NOT during the soaks): LXC
container, 32 visible CPUs, cgroup `cpu.max` = 100000 (no quota),
`nr_throttled` = 0, `/proc/stat` steal = 0, PSI cpu some avg10 = 0.00,
load 1.6–2.7 on 32 cores. The host is idle now; nothing about the current
state can confirm or refute contention during 2026-08-29/30.

**Verdict: UNKNOWN, with host-level scheduling/CPU steal as the leading
hypothesis; software hotspot and physics workload are ruled out by the
0-work pass profile; GC pause is unlikely given SustainedLowLatency.**
This matches the classification already recorded in STATUS.md/ROADMAP.md.
Budgets are NOT relaxed.

---

## 4. Exact measurements

### 12h corrected soak — `a5-report-ct133.json` (SHA `1ce4664f`, FULL)
| Metric | Value |
|---|---|
| Window | 720.000044 min, 721 samples @ 60 s |
| DormantSpecs | 1000/1000 throughout |
| Embodied / materializations / dematerializations | 0 / 0 / 0 |
| Scheduler queues / failures / save-skips | 0 / 0 / 0 |
| DB writes delta | 0 |
| RSS baseline / startup peak / steady peak / growth | 2383.4 / 2471.3 / 2539.3 MB / +155.9 MB (< 512 MB) |
| Region p50 / p95 / p99 / max | 5 / 21 / 27 / 308 ms |
| Region breaches (> 200 ms) | 6: 299, 288, 308, 291, 297, 291 ms |
| Tick invoke max p50 / p95 / p99 / max | 0.045 / 14.2 / 15.3 / 282.9 ms |
| Tick breaches (> 250 ms) | 1: 282.9 ms @ 16:48:02Z (region = 5 ms that sample) |
| SaveP95 p50 / p90 / p95 / p99 / max | 4.17 / 13.13 / 17.18 / 21.33 / 76.63 ms |
| SaveMax max | 296.044 ms (budget 4000 / 10000 ms) |
| Log events (retained `/tmp/a5-12h-evidence/game-restart.log`, verified on disk) | 571 ActiveRegionTick overruns (570 in-window, 1 pre-window boot), ~76 s cadence (75–80 s ×293, 29–31 s ×126), 566/571 same-second physics-slow, 1,348 physics-slow lines, 14 `Tick took` events (10 > 250 ms), deferred 0 characters |

### 6h canary (first run) — `g2-a5-tier3-sixhour-report.json` (FULL window, RSS-fail)
| Metric | Value |
|---|---|
| Window | 360.00003 min, 361 samples @ 60 s |
| Tick max | 19.1 ms (no breach) |
| Region max | 47 ms full-window (no breach; the 47 ms sample is the first, pre-quiescence sample at 2026-08-29T21:58:08Z — excluding it the max is 33 ms) |
| SaveP95 max / SaveMax max | 85.7 / 100.8 ms |
| RSS | baseline 1207.9 MB (pre-quiescence) → peak 5749.4 MB → final 3744.1 MB; budget +512 MB failed — baseline captured before deferred world startup settled; not classified as a leak |
| Physics warnings (source-report, all boots) | 44 total per source report (mostly 65–126 ms gaps; one 959/938 ms pair + 942 ms region @ 21:23:11) — **source-report-derived, NOT on-disk-verifiable**; retained logs verify only 6 warnings (70–100 ms @ 15:42:25–35, `game-restart.log`) |
| Region overruns (retained logs) | 2 on-disk: 1138/1261 ms @ 14:56:19-20 in `game.log` (during concurrent 4-connection seeding — real workload). Source report additionally records 942 ms @ 21:23:11 and 101 ms @ 10:45:26 — **source-report-derived, NOT on-disk-verifiable** |

### Seeding-phase overruns (6h canary, `game.log` — on-disk verified)
- `14:56:19` ActiveRegionTick 1138 ms and `14:56:20` 1261 ms, both
  interleaved with `BotAccountProvisioningService` provisioning lines —
  these are genuine workload spikes during concurrent 4-connection
  retained 6h canary log (the retained 12h log does contain 14 `Tick took`
  events — see Mode B and Limitations).
  retained log.

---

## 5. Limitations

1. **The 12h raw game log is not on disk, but a retained restart log
   verifies the key events.** The report JSON (`a5-report-ct133.json`)
   survives, and the retained `/tmp/a5-12h-evidence/game-restart.log`
   (12h soak window, 21:53:26–09:55:59 local) verifies on disk: 571
   `ActiveRegionTick took` events (570 in-window, 1 pre-window boot),
   cadence clusters 75–80 s ×293 and 29–31 s ×126, 566/571 same-second
   physics-slow coincidence, 1,348 physics-slow warning lines, and 14
   `TickManager - Tick took` events (10 > 250 ms). The earlier
   "75–80 s ×375" figure is historical triage text from
   STATUS.md/ROADMAP.md and is not re-derived from the retained log.
2. **The 6h canary's `Server.log` is absent.** Retained on-disk files
   under `/root/aaemu-e2e-a5-tier3-sixhour/logs/` are: the report JSON
   (`g2-a5-tier3-sixhour-report.json`), `game.log` (boot + seeding; contains
   the two 1138/1261 ms ActiveRegionTick overruns @ 14:56:19-20 and no
   physics warnings), `game-restart.log` (contains 6 physics warnings
   70–100 ms @ 15:42:25–35), `login.log`, and three `soak-run-*.log`
   (test-runner output). Consequently the source report's runtime-log
   details — 44 physics warnings across boots (mostly 65–126 ms gaps), the
   `21:23:11` 959/938/942 ms ~1 s stall, and the `10:45:26` 101 ms region
   overrun — are **source-report/STATUS-derived and NOT verifiable from
   retained files**; they are marked as such wherever cited in this dossier.
   The retained logs verify only the report-JSON metrics plus the boot
   warnings/overruns listed above.
3. **No host metrics were collected during either soak** — no process
   CPU/wall, `/proc/stat` steal deltas, PSI, or cgroup throttling. This is
   the single biggest gap; it is why the classification stays UNKNOWN. (The
   2026-09-02 1-hour calibration-lane run collected host telemetry — see
   section 7.3a — but that is a separate, non-soak run.)
4. **60 s sample cadence** — the 7 sampled breaches are a coarse view of
   571 log events; the true stall distribution (durations, exact cadence)
   is finer than the report captures.
5. **No per-subscriber tick breakdown in the report** — the bridge metrics
   surface exposes per-subscriber rings, but the report records only
   invoke p95/max, so the 282.9 ms tick max cannot be attributed to a
   specific subscriber.
6. **No GC logs** during the 12h run; GC-pause exclusion rests on the
   configured SustainedLowLatency mode and the prior M6 RCA, not on
   per-run measurement.
7. Current host metrics (steal = 0, no throttling, idle PSI) are a
   snapshot taken after the fact and cannot confirm or refute contention
   during the soak windows.

---

## 6. Recommended bounded experiment + host metrics

**Experiment (bounded, 6h window is sufficient to reproduce the ~76 s
cadence):** rerun the dormant-timer soak with a host-telemetry sidecar
sampling every 1 s for the full window. **SHA choice is deliberate and
must not be blurred:** rerunning at SHA `1ce4664f` reproduces the original
pre-remediation conditions (the run that produced the 571-event/7-breach
evidence); rerunning at current HEAD tests the post-remediation code
(`1801baf98` ActiveRegionTick snapshot reuse + `CharacterSnapshotMs`/
`SpawnerScanMs` telemetry). The host-telemetry sidecar below is a
recommended measurement detail for either arm, not a new commitment beyond
the existing next action (bounded calibration, then decide code fix vs
budget calibration):

- **Process CPU/wall:** per-second deltas of `/proc/<game-pid>/stat`
  utime+stime vs wall (or `pidstat -p <pid> 1`); also thread-level
  (`/proc/<pid>/task/*/stat`) to see whether the stall is one thread or all
  threads (process-wide vs single-thread signature).
- **CPU steal:** per-second `/proc/stat` `steal` field deltas (the host is
  an LXC guest on a shared Threadripper host — steal is the direct measure
  of host contention).
- **PSI:** `/proc/pressure/{cpu,io,memory}` sampled per second; correlate
  `some`/`full` spikes with the ~76 s stall cadence.
- **Cgroup throttling:** `/sys/fs/cgroup/cpu.stat` `nr_throttled` /
  `throttled_usec` deltas (currently 0, but must be measured during the
  run).
- **GC:** `DOTNET_EnableEventLog`/dotnet-counters GC pause events, or at
  minimum `dotnet-counters monitor --counters System.Runtime` for
  `gc-pause` — to definitively include/exclude the GC class.
- **Per-subscriber tick attribution:** extend the report to record the
  bridge `metrics.tick.subscribers` breakdown so the 282.9 ms invoke max
  can be attributed (sync subscriber vs tick-thread deschedule).
- **Control arm:** same soak with the game process pinned to a dedicated
  CPU subset (`taskset -c`) vs unpinned, to discriminate host contention
  from process-internal descheduling.

## Instrumentation availability (2026-09-02 — no new result)

The following in-process instrumentation is now available to the next
calibration run (config-gated, **disabled by default**; this is a statement
of availability, not evidence of any new measurement):

- **Physics per-iteration telemetry** (`World.PhysicsTelemetry` in
  `Configurations/World.json`; `Enabled` default `false`): bounded rings
  sized to the configured sample period (default 60s; bounded
  configuration maximum) capture per-iteration wall loop gap, sleep overshoot,
  `PhysicsWorld.Step` duration, broadcast duration, and pending-action /
  body / ship / force counts. A periodic aggregate log line is emitted at
  most once per `SamplePeriodSeconds` (WARN when the window's max loop gap
  exceeds `SlowIterationMs`, DEBUG otherwise) — no per-iteration INFO spam,
  no unbounded allocations. Exposed to the E2E bridge as
  `metrics.physics`. Force count is a live `ForceGenerator` instance count
  (Interlocked, no lock on the physics thread).
- **ActiveRegionTick** already exposes `CharacterSnapshotMs` /
  `SpawnerScanMs` (bridge `metrics.regionTick`); **TickManager** already
  exposes per-subscriber invoke attribution (bridge `metrics.tick.subscribers`).

Enabling `World.PhysicsTelemetry.Enabled` in the test server's
`Config.Local.json` (or `Configurations/World.json`) arms the per-iteration
physics sampling for the next bounded 6h calibration; the host-telemetry
sidecar and pinned control arm from §6 remain the recommended measurement
details for either arm.

**Success criterion:** if stall seconds coincide with steal/PSI/cgroup
spikes or with all-thread deschedules, the classification moves from
UNKNOWN to host-scheduling; if stalls persist with zero host signals and
pinned CPUs, the investigation must return to the process (GC events,
thread-pool starvation, sync subscribers).

**Budgets are NOT relaxed.** The 200 ms region / 250 ms tick-max / 100 ms
tick-p95 budgets stand; the experiment is to attribute the breach source,
not to re-baseline it.

## 7. Memory-pressure diagnosis (2026-09-02 — user/live operational evidence, additive)

New diagnosis recorded from the user's latest live operational evidence.
This is **user/live operational evidence** (not H/human gameplay feel, and
not independently reproduced by this tool call). It supersedes the
"UNKNOWN host scheduling" framing as the leading hypothesis for the prod
CT133 environment; the soak-time classification itself remains UNKNOWN
because no host/GC/swap telemetry was collected during the soak windows.
The diagnosis is a **strongly supported provisional infrastructure root
cause** (Mai's CT133 diagnosis, user/live operational evidence): **A5
remains formally OPEN/UNCLOSED** until CT133 memory remediation is applied
and a comparable post-change run confirms the warnings disappear. No H
claim, no budget relaxation, no new implementation scope.

### 7.1 Evidence (user/live operational, exact labels preserved)

- **Production CT133 presence-demo (user-reported):** healthy ~6 days, no
  soak; **1,647 physics-slow warnings over 9 days**; simultaneous both-world
  spikes around 500–575 ms with matching values (example 573/574 ms).
- **Prod Game (user-reported):** ~130,228 kB VmSwap on an 8 GB CT with
  512 MB zram, swappiness 60; Game VmData ~4.7 GB; MySQL/Login/Adminer/API
  share the same ceiling.
- **Comparison/contrast soak (CT124, not a matched A/B):** 0 KB swap on
  CT124 with 48 GB RAM and **zero warnings in 12 h**.
- **User live observation (single coincidence, not causal proof):** a live
  573 ms spike **coincided with** a .NET BGC (background GC) thread, a
  ~25 MB RSS drop, and swap-in clustering — recorded as consistent with,
  not proof of, a memory-pressure mechanism.

### 7.2 Diagnosis

Memory pressure/swap + background GC/page faults is a **strongly
supported provisional infrastructure root cause** for the
**user-reported current PROD CT133 presence-demo only** — **no longer
merely UNKNOWN host scheduling for that environment**. The prod Game
process is swapping on an 8 GB CT (VmSwap ~130 MB, 512 MB zram, swappiness
60) while sharing the ceiling with MySQL/Login/Adminer/API; the
comparison/contrast soak on a 48 GB CT (CT124) shows 0 KB swap and zero
warnings in 12 h. The live 573 ms spike coinciding with a BGC thread, a
~25 MB RSS drop, and swap-in clustering is **consistent with** (not proof
of) background-GC compaction + page-fault/swap-in stalls under memory
pressure. This is not claimed as independently reproduced by this tool
call; it is the user's live operational evidence recorded verbatim.

**Scope boundary — the 12 h soak is NOT explained by memory/swap:** the
12 h soak ran on a host with **0 swap** (no VmSwap, no zram pressure) and
no in-soak host/GC telemetry was collected, so the soak-time classification
remains **UNKNOWN**. The memory/swap diagnosis applies to the prod CT133
environment; it does not retroactively explain the soak breaches.

### 7.3 Next action — memory remediation first

1. **Preferred:** increase CT133 memory to **16 GB**.
2. **Alternatives:** `DOTNET_GCHeapHardLimit` calibration, or disabling
   swap (with OOM risk).
3. **Required before/after telemetry:** memory/swap/GC telemetry
   (VmSwap, zram/swap usage, GC pause events, RSS) before and after the
   remediation.
4. **Then:** rerun the post-remediation soak.

**Budget/claim boundaries (preserved):** budgets are unchanged (200 ms
region / 250 ms tick-max / 100 ms tick-p95). The old 12 h report
(`1ce4664f…`) was **pre-remediation**; **no new soak pass is claimed** by
this diagnosis. The soak-time classification remains UNKNOWN pending the
remediation + telemetry + rerun. **A5 remains formally OPEN/UNCLOSED**
until CT133 memory remediation is applied and a comparable post-change run
confirms the warnings disappear; no H claim, no budget relaxation, no new
implementation scope.

### 7.3a 1-hour calibration lane telemetry run (2026-09-02 — no new soak result)

A bounded 1-hour calibration-lane run executed with the host-telemetry
sidecar and physics per-iteration telemetry enabled (root
`/root/aaemu-e2e-a5-calibration/`; host sidecar
`logs/host-telemetry.jsonl`; physics telemetry in
`runtime/game/Logs/Server.log`). **This is a calibration-lane telemetry
run, NOT a soak; no A5 pass is claimed.**

- **Host sidecar:** ~3,388 samples @ 1 s; **0 steal**, **0 CPU PSI**,
  **0 cgroup throttling** across the window.
- **Physics loop (per-iteration telemetry, both worlds):** max loop gap
  **62 ms at boot** (arche_mall_world, 02:18:34) and **≤ 40 ms steady**
  (occasional 41 ms samples); step p95 ~0 ms; **0 in-window physics-slow
  warnings** (the only 2 `Physics thread is running slow` lines, at
  06:52:23 local, fall outside the host-telemetry window).
- **Tick/region:** 0 `Tick took` warnings, 0 ActiveRegionTick overruns in
  the calibration game log; tick invoke max 0.1 ms.
- **Interpretation:** the calibration host showed no steal/PSI/throttling
  and no in-window physics warnings; this is consistent with the earlier
  "no host contention observed when measured" picture and does not
  reproduce the 12 h soak breaches. It is **not** a soak result and does
  not change the UNKNOWN soak-time classification.

### 7.4 Planning item — A5-MEMORY-01 (durable, not a fake issue)

No live kanban board file exists in this repo (only `.kanban-templates/`
and the milestone `scorecard-explorations/progression-board.md`), so this
planning item is recorded here in the A5 dossier as a clearly labeled
planning item — **not** a fake GitHub issue.

- **ID:** `A5-MEMORY-01`
- **Type:** planning item (durable dossier card; not a fake issue)
- **Status:** OPEN (planning)
- **Owner:** A5 lane / ops (CT133 host)
- **Goal:** eliminate memory-pressure/swap stalls behind the prod CT133
  physics-slow warnings and both-world timing spikes.
- **Action:** (1) increase CT133 memory to 16 GB (preferred), or
  `DOTNET_GCHeapHardLimit` calibration, or disable swap with OOM risk;
  (2) capture before/after memory/swap/GC telemetry (VmSwap, zram/swap,
  GC pause events, RSS); (3) rerun the post-remediation soak.
- **Acceptance:** before/after CT133 VmSwap/zram/GC/RSS telemetry showing
  the remediation effect, **plus a post-remediation full soak with zero
  breaches** (region ≤ 200 ms, tick-max ≤ 250 ms, tick-p95 ≤ 100 ms);
  budgets unchanged; no A5 pass claimed until that soak.
- **Evidence:** user/live operational evidence (section 7.1);
  comparison/contrast soak CT124 0 KB swap / zero warnings in 12 h.
## 8. Post-remediation follow-up (2026-09-02 — user/Mai operational evidence, additive)

Follow-up recorded from the user's latest live operational evidence after
the CT133 memory remediation (preferred action: CT133 → 16 GB). This is
**user/Mai operational evidence** — not H/human gameplay feel, and not
independently reproduced by this tool call. It is recorded verbatim and
additively; all prior sections remain historical and preserved.

### 8.1 Provenance (user/Mai operational, exact labels preserved)

- **Running Game PID:** 3057037.
- **Deployment since:** 20:06 UTC; **~10.5 h observation**.
- **CT host:** 16 GiB RAM / 8 GiB swap.
- **Cgroups:** effective CT and game-container cgroups
  `memory.max=max`, `memory.swap.max=max`; `memory.events` shows **zero
  OOM / zero max hits**.
- **Usage:** CT 4.2 GB; game container 2.8 GB.
- **Game process:** VmRSS 2.67 GB, VmData 4.27 GB, **VmSwap 0 kB**
  (pre-restart ~129 MB).
- **Stack memory:** game 2.6 GiB / db 467.5 MiB / login 43.2 MiB /
  adminer 8.8 MiB / register-api 15 MB.
- **GC trace capture:** alive at 5.3 MB and growing.
- **No GC events in ordinary logs** because the events are in nettrace.

### 8.2 Behavior (user/Mai operational, exact labels preserved)

- **17 physics warnings across the ~10.5 h observation**, worst **340 ms**.
- **22 spikes in the first 2 h post-restart**, worst **807 ms** — a
  distinct reported window/class from the ~10.5 h warning count.
- **500 ms+ signature absent in the later observed period** (the ~10.5 h
  window's worst is 340 ms). The 807 ms first-2 h spike predates that
  absence; the absence is not claimed for the first-2 h window.

### 8.3 Classification

The post-change observation **strongly supports** the prod CT133
memory-pressure/swap hypothesis — it does **not fully prove** it: residual
~300 ms events keep another cause open. The historical 12 h soak
classification remains **UNKNOWN**; **no A5 pass is claimed**. Budgets are
unchanged (200 ms region / 250 ms tick-max / 100 ms tick-p95).

### 8.4 Next closure criteria

1. **Continue GC/nettrace capture** (GC trace capture alive at 5.3 MB and
   growing; GC events live in nettrace, not ordinary logs).
2. **Correlate residual warnings** with GC/thread/process/host telemetry.
3. **Then run a comparable post-change A5 soak with zero budget breaches**
   before closing A5.

**A5 remains formally OPEN/UNCLOSED** until the post-change soak with zero
breaches completes. No H claim, no budget relaxation, no new implementation
scope; no code/data/client/soak changes, no commit.

---

## Data/code vs live/client/H boundary

- All evidence in this dossier is **data** (soak report JSONs + retained
  on-disk logs) and **code** (repo C# source inspection) only — testing/
  canary operational evidence, not live human gameplay. Runtime-event
  details cited from the source report that are not verifiable from retained
  files (44 physics warnings, the `21:23:11` 959/938/942 ms stall, the
  `10:45:26` 101 ms overrun) are explicitly marked source-report-derived
  throughout.
- **2026-09-02 follow-up (section 8):** post-remediation observation
  recorded from **user/Mai operational evidence** (Game PID 3057037,
  deployment since 20:06 UTC, ~10.5 h; CT 16 GiB RAM / 8 GiB swap;
  cgroups `memory.max=max` / `memory.swap.max=max` with zero OOM/max
  hits; CT 4.2 GB / container 2.8 GB; Game VmRSS 2.67 GB, VmData 4.27 GB,
  VmSwap 0 kB vs pre-restart ~129 MB; stack game 2.6 GiB / db 467.5 MiB /
  login 43.2 MiB / adminer 8.8 MiB / register-api 15 MB; GC trace capture
  alive 5.3 MB and growing; GC events in nettrace, not ordinary logs) —
  17 physics warnings across the ~10.5 h observation (worst 340 ms) and
  22 spikes in the first 2 h post-restart (worst 807 ms) as distinct
  reported windows/classes; **500 ms+ signature absent in the later
  observed period** (the ~10.5 h window's worst is 340 ms) — the 807 ms
  first-2 h spike predates that absence, which is not claimed for the
  first-2 h window. This **strongly supports** the prod CT133
  memory-pressure/swap hypothesis but does **not fully prove** it
  (residual ~300 ms events keep another cause open); the historical 12 h
  soak classification remains **UNKNOWN**; **no A5 pass is claimed**;
  budgets unchanged. Next closure criteria: continue GC/nettrace capture,
  correlate residual warnings with GC/thread/process/host telemetry, then
  run a comparable post-change A5 soak with zero budget breaches before
  closing A5. Labeled user/Mai operational evidence, not H/human gameplay
  and not independently reproduced here.
- **No game server was launched for this investigation, no client run, no
  authenticated run, no H claim.** `H` (human/client feel) is **UNKNOWN**.
- The 12h soak ran at SHA `1ce4664f` — **pre-remediation** of the
  ActiveRegionTick deep-copy hotspot (`1801baf98` landed after the run).
- No milestone promotion is implied; budgets are unchanged; historical
  reports and failed-soak provenance remain preserved.
