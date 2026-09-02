# A5 Physics / Tick Timing Stall Investigation Dossier (2026-09-01) — read-only

Durable exploration dossier. Source: the verified read-only investigation
report `/tmp/physics-stall-investigation.md` (SHA-256
`6892e5e0c794e3c129019e5b53af5b6b79220549b68bab1645d951c21609ee01`), re-checked
against this repository (source SHAs verified via `git log`; soak report
`a5-report-ct133.json` re-read via `jq`). **No code/data edited, no tests run,
no commits.** Every measurement below is preserved exactly from the source
report; nothing is re-derived from raw logs that are no longer on disk (see
Limitations).

**Verdict: UNKNOWN, with host-level scheduling/CPU steal as the leading
hypothesis; software hotspot and physics workload are ruled out by the 0-work
pass profile; GC pause is unlikely given SustainedLowLatency but was NOT
measured during the soak. Budgets are NOT relaxed.**

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
  in-window** (per STATUS.md/ROADMAP.md triage — the triage text records
  "571 in the 12h game log (570 in window)"; the raw 12h game log is no
  longer on disk, see Limitations), recurring at a **~76 s cadence**
  (gaps 75–80 s ×375, 30 s ×126).
- **566/571 coincide same-second with physics-slow warnings.**
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
- The tick loop never logged `Tick took` in any available soak log: 0
  `Tick took` lines in the retained 6h canary logs (`game.log`,
  `game-restart.log`, `soak-run-*.log` — verified on disk); the 12h raw log
  is gone. `ActiveRegionTick` is subscribed `useAsync: true`
  (`WorldManager.cs:437`), so a slow region pass runs on the thread pool and
  cannot block the tick loop — the #1491 fix holds (the
  `find-tick-starvation.sh` pair detector finds 0 pairs in the 6h canary
  logs).
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
- Physics-warning detail is **source-report/STATUS-derived, not
  on-disk-verifiable**: the source report records 44 physics warnings across
  boots (mostly 65–126 ms gaps) and the 12h triage records physics values
  278–554 ms tracking the region stalls — i.e., both threads descheduled
  together. The retained on-disk logs verify only 6 physics warnings
  (70–100 ms @ 15:42:25–35 in `game-restart.log`); the 44-warning count and
  the 65–126 ms distribution come from the now-absent `Server.log` (see
  Limitations).

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
2. **The ~76 s cadence** (gaps 75–80 s ×375, 30 s ×126) is regular and
   external-looking: it is not a multiple of the 60 s sample, the 5 min
   autosave, the 2 s proximity sweep, or any tick subscriber interval. A
   periodic external source (another container/VM on the same LXC host —
   e.g., backups, monitoring, cron) is the leading hypothesis.
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
| Log events (per STATUS triage) | 571 ActiveRegionTick overruns in the 12h game log (570 in window), ~76 s cadence, 566/571 same-second physics-slow, deferred 0 characters |

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
  `seedDormant` (25 specs/batch), not the idle-stall class. They did not
  produce `Tick took` warnings (async region pass). Both lines are present
  verbatim in the retained `game.log`; no `Tick took` line exists in any
  retained log.

---

## 5. Limitations

1. **The 12h raw game log is not on disk.** Only the report JSON
   (`a5-report-ct133.json`) survives; the 571-event cadence analysis
   (75–80 s ×375, 30 s ×126, 566/571 physics coincidence) is taken from the
   STATUS.md/ROADMAP.md triage text, not re-derived from the raw log.
2. **The 6h canary's `Server.log` is also absent.** Retained on-disk files
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
   the single biggest gap; it is why the classification stays UNKNOWN.
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
^-
  bridge `metrics.tick.subscribers` breakdown so the 282.9 ms invoke max
  can be attributed (sync subscriber vs tick-thread deschedule).
^-
  CPU subset (`taskset -c`) vs unpinned, to discriminate host contention
   from process-internal descheduling.

## Instrumentation availability (2026-09-02 — no new result)

The following in-process instrumentation is now available to the next
calibration run (config-gated, **disabled by default**; this is a statement
of availability, not evidence of any new measurement):

- **Physics per-iteration telemetry** (`World.PhysicsTelemetry` in
  `Configurations/World.json`; `Enabled` default `false`): bounded rings
  (cap 1024) capture per-iteration wall loop gap, sleep overshoot,
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

---

## Data/code vs live/client/H boundary

- All evidence in this dossier is **data** (soak report JSONs + retained
  on-disk logs) and **code** (repo C# source inspection) only — testing/
  canary operational evidence, not live human gameplay. Runtime-event
  details cited from the source report that are not verifiable from retained
  files (44 physics warnings, the `21:23:11` 959/938/942 ms stall, the
  `10:45:26` 101 ms overrun) are explicitly marked source-report-derived
  throughout.
- **No game server was launched for this investigation, no client run, no
  authenticated run, no H claim.** `H` (human/client feel) is **UNKNOWN**.
- The 12h soak ran at SHA `1ce4664f` — **pre-remediation** of the
  ActiveRegionTick deep-copy hotspot (`1801baf98` landed after the run).
- No milestone promotion is implied; budgets are unchanged; historical
  reports and failed-soak provenance remain preserved.
