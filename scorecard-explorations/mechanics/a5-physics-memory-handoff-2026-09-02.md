# A5 Physics / Memory Investigation — Durable Handoff (2026-09-02)

Durable handoff for the current A5 physics/tick-stall + memory-pressure
investigation. Read-only: **no code/data/deployment/soak roots edited, no
commit.** This page is a coordination handoff for Mai/operator; the
authoritative evidence record remains the read-only dossier
[`a5-physics-stall-investigation-2026-09-01.md`](a5-physics-stall-investigation-2026-09-01.md)
and the authoritative records it mirrors (STATUS / ROADMAP / SCORECARD /
PROJECT-CONTROL / EVIDENCE-LEDGER).

**Evidence-layer discipline (preserved throughout):** every claim below is
labeled either **user/Mai operational evidence** (reported by the user/Mai,
not independently reproduced by this tool call) or **independently verified
repository/telemetry evidence** (re-derived here from on-disk files via
`jq`/`grep`/`ps`). The two are never conflated. `H` (human/client feel) is
**UNKNOWN** everywhere; no A5 pass is claimed; budgets are NOT relaxed.

---

## 1. Current HEAD / provenance

| Item | Value |
|---|---|
| Repo | `/root/aaemu-dev` (fork joshhmann/AAEmu, ArcheAge 1.2 emulator, .NET 10) |
| Branch | `develop` |
| **HEAD** | `b83724f4949669a1439cbef8b86218ac6a723d35` (verified `git rev-parse HEAD`) — "Add config-gated physics telemetry for A5 stall investigation" |
| Prior A5 dossier HEAD | `0f8254dc3d914193d432fb842169e9bb07075508` (the dossier's recorded HEAD; current HEAD is 1 commit ahead) |
| 12h soak source SHA | `1ce4664f96705850136dc9d46999070fac9763fb` (report `commit` field; **pre-remediation** of the ActiveRegionTick hotspot fix `1801baf98…`) |
| ActiveRegionTick remediation | `1801baf987d70eb8f2ac64ac3a9fa84e470e74e8` (landed after the 12h run) |
| 12h soak report | `/root/aaemu-dev/a5-report-ct133.json` (FULL, `passed=false` on timing only) |
| 6h canary report | `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json` (FULL window, RSS-fail) |
| Dossier source report SHA-256 | `6892e5e0c794e3c129019e5b53af5b6b79220549b68bab1645d951c21609ee01` |

---

## 2. Production CT133 post-memory state — exact Mai evidence (user/Mai operational)

Recorded from the user's latest live operational evidence after the CT133
memory remediation (preferred action: CT133 → 16 GB). **User/Mai operational
evidence — not H/human gameplay feel, not independently reproduced by this
tool call.** Exact labels preserved from dossier section 8:

- **Running Game PID:** 3057037 (at time of report; **no longer running** as
  of this handoff — see §7).
- **Deployment since:** 20:06 UTC; **~10.5 h observation**.
- **CT host:** 16 GiB RAM / 8 GiB swap.
- **Cgroups:** effective CT and game-container cgroups `memory.max=max`,
  `memory.swap.max=max`; `memory.events` shows **zero OOM / zero max hits**.
- **Usage:** CT 4.2 GB; game container 2.8 GB.
- **Game process:** VmRSS 2.67 GB, VmData 4.27 GB, **VmSwap 0 kB**
  (pre-restart ~129 MB).
- **Stack memory:** game 2.6 GiB / db 467.5 MiB / login 43.2 MiB /
  adminer 8.8 MiB / register-api 15 MB.
- **GC trace capture:** alive at 5.3 MB and growing; **GC events live in
  nettrace, not ordinary logs**.
- **Behavior:** 17 physics warnings across the ~10.5 h observation, worst
  **340 ms**; 22 spikes in the first 2 h post-restart, worst **807 ms** (a
  distinct reported window/class from the ~10.5 h count); **500 ms+ signature
  absent in the later observed period** (the ~10.5 h window's worst is
  340 ms) — the 807 ms first-2 h spike predates that absence, which is not
  claimed for the first-2 h window.

**Classification (user/Mai operational):** the post-change observation
**strongly supports** the prod CT133 memory-pressure/swap hypothesis — it
does **not fully prove** it: residual ~300 ms events keep another cause open.
The historical 12 h soak classification remains **UNKNOWN**; **no A5 pass is
claimed**; budgets unchanged.

---

## 3. Historical soak boundary (unchanged, preserved)

- **12h corrected soak** (SHA `1ce4664f`, report `a5-report-ct133.json`,
  FULL 720.000044 min / 721 samples): `passed=false` on **timing only**.
  Seven distinct sampled breaches — region 299/288/308/291/297/291 ms
  (budget 200 ms) and tick max 282.9 ms (budget 250 ms). 571 ActiveRegionTick
  overruns in-window at ~76 s cadence; 566/571 same-second physics-slow;
  deferred 0 characters (descheduling time, not work time). RSS growth
  155.9 MB < 512 MB budget. **Ran pre-remediation** (`1ce4664f` predates
  `1801baf98`).
- **6h canary** (`g2-a5-tier3-sixhour-report.json`): FULL window, tick max
  19.1 ms, region max 47 ms (no timing breach); RSS assertion failed because
  baseline capture preceded deferred world-startup quiescence (baseline
  1207.9 MB → peak 5749.4 MB → final 3744.1 MB, budget +512 MB) — a
  testing/canary diagnostic failure, not yet classified as a leak.
- **Soak-time classification remains UNKNOWN:** the soak host had **0 swap**
  and no in-soak host/GC telemetry was collected, so memory/swap does NOT
  explain the 12 h soak breaches. The memory/swap diagnosis applies to the
  prod CT133 environment only.

---

## 4. Current calibration lane status (independently verified — NEW)

The calibration-lane telemetry run recorded in dossier §7.3a as a **1-hour**
run has **continued running** and is now a **~21.7 h** continuous
observation. This is **independently verified** here from on-disk files
(`/root/aaemu-e2e-a5-calibration/`), not user/Mai-reported.

| Item | Value (verified) |
|---|---|
| Root | `/root/aaemu-e2e-a5-calibration/` |
| Game PID | **2814356** (alive, ~21.7 h elapsed, RSS ~3.03 GB) — **ephemeral** |
| Login PID | **2776246** (alive) — **ephemeral** |
| Game log | `logs/game.log` (boot 02:18:52 PDT 09-02; ~4,696 lines) |
| Host telemetry | `logs/host-telemetry.jsonl` — **59,054 samples @ 1 s**, 09:21:37Z 09-02 → 07:01Z 09-03 |
| Physics telemetry | enabled (`Config.Local.json`: `PhysicsTelemetry.Enabled=true`, `SamplePeriodSeconds=60`, `SlowIterationMs=100`) |
| Sidecar | `logs/host-telemetry-sidecar.log` (relaunched hourly; last 06:52:09Z 09-03) |

**Host-telemetry coverage gap (verified):** samples exist for 09:21Z–10:21Z
and 14:19Z–07:01Z 09-03, but **no samples 10:21Z–14:19Z 09-02** (the 13:00Z
hour has 0 samples). Any event in that gap has no host telemetry.

**Physics warnings in the calibration game log (verified, 4 total):**

| Time (PDT) | UTC | In telemetry window? | Values |
|---|---|---|---|
| 06:52:23 09-02 | 13:52:23Z | **NO — in the 10:21Z–14:19Z gap** | 139 / 128 ms |
| 11:22:22 09-02 | 18:22:22Z | **YES** | 343 / 361 ms |

**NEW in-window finding (independently verified):** the 11:22:22 PDT
(18:22:22Z 09-02) physics warnings fall **inside** the host-telemetry window.
At that exact second the telemetry shows a **process CPU spike** —
`procCpuPct=25.3`, `threadCpuPct=26.3` — with **`stealPct=0.0`,
`psiCpuFull10=0.00`, `psiIoFull10=0.33`, `psiMemFull10=0.00`,
`cgroupNrThrottled=0`**. The physics-telemetry line at 11:22:23 confirms
`main_world` loopGap max=343 ms, sleepOvershoot max=337.8 ms, step p95=0,
bodies=0, forces=4. **This is the first physics warning captured WITH host
telemetry, and it shows the stall coincides with a process-internal CPU
spike and ZERO host-contention signals (no steal, no CPU PSI, no cgroup
throttling).** This is consistent with a process-internal deschedule (e.g.
GC/thread-pool/another thread) rather than host steal — but it is a single
event, not causal proof, and does not change the UNKNOWN soak-time
classification. The 06:52:23 warnings are in the telemetry gap and have no
host coverage.

**Calibration lane otherwise clean (verified):** 0 `Tick took` warnings, 0
ActiveRegionTick overruns in the calibration game log; tick invoke max
0.0–0.2 ms; 0 in-window physics warnings other than the two events above.
**This is a calibration-lane telemetry run, NOT a soak; no A5 pass is
claimed.**

---

## 5. What is completed

- **Dossier** `a5-physics-stall-investigation-2026-09-01.md` (read-only)
  records: two distinct failure modes (Mode A region overrun, Mode B tick
  invoke max), the 12h soak pre-remediation boundary, the memory-pressure
  diagnosis (§7, user/live operational), the 1-hour calibration-lane
  telemetry run (§7.3a), the post-remediation follow-up (§8, user/Mai
  operational), and the durable planning item `A5-MEMORY-01` (§7.4).
- **ActiveRegionTick remediation** `1801baf98` landed (snapshot reuse +
  `CharacterSnapshotMs`/`SpawnerScanMs` telemetry; regression tests 2/2).
- **Config-gated physics telemetry** landed at current HEAD `b83724f49`
  (`World.PhysicsTelemetry`, disabled by default; bounded rings; exposed to
  the E2E bridge as `metrics.physics`).
- **CT133 memory remediation applied** (CT133 → 16 GB; user/Mai operational
  evidence shows VmSwap 0 kB post-restart vs ~129 MB pre-restart, zero
  OOM/max hits, 500 ms+ signature absent in the later observed period).
- **Calibration lane** has now run ~21.7 h with host telemetry and physics
  telemetry enabled, producing the first in-window warning-with-telemetry
  observation (§4).

---

## 6. What remains open

- **A5 remains formally OPEN/UNCLOSED.** No post-change A5 soak with zero
  budget breaches has completed. No A5 pass is claimed.
- **Soak-time classification remains UNKNOWN** (soak host had 0 swap, no
  in-soak host/GC telemetry).
- **Prod CT133 memory-pressure hypothesis is strongly supported but not
  fully proven** — residual ~300 ms events keep another cause open.
- **The new in-window calibration finding (§4)** — a process CPU spike with
  zero host contention at the warning second — is a single event, not causal
  proof; it needs correlation with GC/thread telemetry before it can move
  the classification.
- **No GC/nettrace capture is currently running** (verified: no `*.nettrace`
  files on disk, no dotnet-trace/dotnet-counters/collect processes). The
  user/Mai-reported GC trace capture (alive at 5.3 MB) is not present in the
  current environment.
- **`A5-MEMORY-01`** (planning item, not a fake issue) remains OPEN:
  acceptance = before/after CT133 VmSwap/zram/GC/RSS telemetry plus a
  post-remediation full soak with zero breaches.

---

## 7. Precise next steps for Mai / operator

1. **Continue GC/nettrace capture** on the prod CT133 Game process. GC
   events live in nettrace, not ordinary logs. (The user/Mai-reported GC
   trace capture is not present in the current environment; re-establish it
   if it lapsed.)
2. **Correlate residual warnings with GC/thread/process/host telemetry.**
   The new in-window calibration finding (§4) — a process CPU spike with
   zero steal/PSI/cgroup throttling at the warning second — is the strongest
   lead: correlate the 11:22:22 PDT (18:22:22Z 09-02) event against GC
   pause events and thread-level CPU to determine whether the stall is
   GC/thread-pool-internal rather than host steal. Also close the
   host-telemetry gap (10:21Z–14:19Z 09-02) so the 06:52:23 PDT warnings get
   coverage on any future run.
3. **Then run a comparable post-change A5 soak with zero budget breaches**
   before closing A5. Use the current HEAD (post-remediation + physics
   telemetry) or the deliberate SHA choice per dossier §6; enable the
   host-telemetry sidecar and physics telemetry for the full window; keep
   the pinned (`taskset -c`) control arm as an option.
4. **Do not** relax budgets, claim A5 passed, or claim H on any of the above.

---

## 8. Exact budgets (NOT relaxed)

| Budget | Value |
|---|---|
| Region pass | ≤ 200 ms |
| Tick invoke max | ≤ 250 ms |
| Tick p95 | ≤ 100 ms |
| Save p95 / max | 4000 / 10000 ms |
| RSS growth | ≤ 512 MB |
| DormantSpecs minimum | 1000 |
| DB writes | ≤ 500 / min |
| Scheduler failures / save skips / queue depth | 0 |

(Verified from `a5-report-ct133.json` `budgets` and
`A5Tier3AcceptanceProbeTests.cs:246-250`.)

---

## 9. Required evidence fields for any A5 closure claim

A post-change A5 soak that would support closing A5 must record, at minimum:

- **Report JSON** with `commit` (exact source SHA), `runAtUtc`, `probe`,
  `passed`, and the full `budgets` block (as `a5-report-ct133.json` does).
- **Window:** FULL duration in minutes + sample count @ 60 s.
- **Dormancy:** DormantSpecs 1000/1000, embodied 0, materializations/
  dematerializations 0/0.
- **Timing:** region p50/p95/p99/max + breach count; tick invoke p50/p95/
  max + breach count; save p95/max.
- **RSS:** baseline / startup peak / steady peak / growth (post-quiescence
  baseline per `A5_WARMUP_READY`).
- **Workload health:** scheduler queues/failures/save-skips, DB writes delta.
- **Host telemetry** (per-second sidecar): process/thread CPU wall, `/proc/stat`
  steal deltas, PSI cpu/io/memory, cgroup `cpu.stat` throttling deltas —
  covering the FULL window with no gaps.
- **GC evidence:** GC pause events (nettrace / dotnet-counters) or an explicit
  statement that none were collected.
- **Per-subscriber tick attribution** (bridge `metrics.tick.subscribers`) to
  attribute any tick-max breach.
- **Zero budget breaches** (region ≤ 200 ms, tick-max ≤ 250 ms, tick-p95
  ≤ 100 ms) across the full window.

---

## 10. Safe deployment / stop conditions

- **Safe to deploy/keep running:** the calibration lane (game PID 2814356,
  login PID 2776246) is a bounded telemetry run with no soak verdict; it may
  be left running or stopped without affecting any A5 claim. Prod CT133
  presence-demo continues to run normally (no soak, no A5 claim).
- **Stop conditions for the calibration lane:** when the operator has enough
  in-window warning-with-telemetry samples to correlate (or after a bounded
  window), or when the host-telemetry sidecar is no longer being relaunched.
  Stopping it does not change any evidence state.
- **Do NOT** stop or restart prod CT133 Game without re-establishing GC/
  nettrace capture first, since GC events live only in nettrace.
- **Do NOT** run a new A5 soak until the post-change soak criteria in §7/§9
  are met (host telemetry covering the full window, GC capture, zero-breach
  target).

---

## 11. Boundaries (preserved)

- **No H claim.** `H` (human/client feel) is **UNKNOWN** everywhere. A/R/L
  evidence never promotes H.
- **No budget relaxation.** The §8 budgets stand.
- **No false "A5 passed" language.** A5 remains formally OPEN/UNCLOSED until
  a post-change soak with zero budget breaches completes.
- **No new implementation scope** is implied by this handoff.
- **Evidence labels are explicit:** user/Mai operational evidence (§2) is
  distinct from independently verified repository/telemetry evidence (§1, §3,
  §4). The two are never conflated.
- **No code/data/client/soak changes, no commit** were made for this handoff.

---

## 12. Ephemeral testing-lane paths / PIDs (valid at handoff time)

These are **ephemeral** and may change or stop at any time; re-verify before
relying on them.

| Lane | Root | Game PID | Login PID | Status |
|---|---|---|---|---|
| Calibration lane | `/root/aaemu-e2e-a5-calibration/` | 2814356 | 2776246 | alive ~21.7 h |
| 6h canary (historical) | `/root/aaemu-e2e-a5-tier3-sixhour/` | — | — | completed (report JSON retained) |
| Prod CT133 Game (user/Mai-reported) | — | 3057037 (reported) | — | **not running** as of this handoff |

No secrets are recorded in this handoff.
