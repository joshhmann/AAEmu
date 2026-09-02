#!/usr/bin/env bash
# a5-host-telemetry.sh — host-side telemetry sidecar for the isolated A5 calibration lane.
#
# Samples, once per INTERVAL, the host-side signals that distinguish "the game process
# was starved by the host" from "the game process stalled on its own":
#
#   /proc/<PID>/stat               process utime/stime deltas vs wall clock (CPU consumed)
#   /proc/<PID>/task/*/stat        per-thread aggregate + thread count (single-thread vs
#                                  process-wide deschedule signature)
#   /proc/stat                     steal-time delta (host oversubscription / co-tenants)
#   /proc/pressure/{cpu,io,memory} PSI some/full avg10 + total deltas (resource stalls)
#   /sys/fs/cgroup/cpu.stat        nr_periods / nr_throttled / throttled_usec deltas
#                                  (cgroup CPU quota throttling, when available)
#   dotnet-counters (optional)     System.Runtime GC counters -> <OUTPUT>.gc.jsonl, only
#                                  when the tool is installed; never required, never fatal
#
# Output: newline-delimited JSON (JSONL) appended to OUTPUT:
#   line 1: {"event":"start", ...}
#   lines : {"event":"sample","ts":<UTC ISO8601>,"elapsedMs":N,"pid":N,"alive":true,
#            "procUtimeMs":N,"procStimeMs":N,"procCpuMs":N,"procCpuPct":F,
#            "threads":N,"threadCpuMs":N,"threadCpuPct":F,
#            "stealMs":N,"stealPct":F,
#            "psiCpuSome10":F,"psiCpuFull10":F,"psiIoSome10":F,"psiIoFull10":F,
#            "psiMemSome10":F,"psiMemFull10":F,
#            "psiCpuSomeTotalDeltaUs":N,"psiCpuFullTotalDeltaUs":N,
#            "cgroupNrPeriods":N,"cgroupNrThrottled":N,"cgroupThrottledUs":N,
#            "cgroupThrottledDeltaUs":N,"cgroupThrottledCountDelta":N}
#   last  : {"event":"end","reason":"duration_elapsed|process_exited|interrupted",...}
#
# The first sample is a pure baseline: its delta fields are zeroed (baseline:1). All
# later samples carry deltas vs the previous sample, and CPU/steal percentages are
# per-interval (delta over the wall time since the previous sample), so a stalled
# second reads ~0% while a busy second reads ~100%.
#
# Usage:
#   bash Scripts/a5-host-telemetry.sh PID OUTPUT [DURATION] [INTERVAL]
#     PID       target process id (digits only; e.g. "$(cat "$E2E_ROOT/pids/game.pid")")
#     OUTPUT    JSONL file to append to (parent dir must exist and be writable)
#     DURATION  total seconds to sample (default 3600, max 86400)
#     INTERVAL  seconds between samples (default 1, min 1, max 60; DURATION >= INTERVAL)
#
# Exit codes:
#   0     full DURATION sampled (final line reason=duration_elapsed)
#   1     target process exited before DURATION elapsed (final line reason=process_exited)
#   2     usage / validation error (bad PID, bad OUTPUT, out-of-bounds duration/interval,
#         or target not alive at start)
#   130/143  interrupted by SIGINT/SIGTERM (final line reason=interrupted)
#
# The script never writes anywhere except OUTPUT (+ <OUTPUT>.gc.jsonl / .gc.err when
# dotnet-counters is used). It does not touch repo data, E2E roots, or the target
# process (read-only /proc access; kill -0 liveness checks only).

set -euo pipefail

CLK_TCK=$(getconf CLK_TCK 2>/dev/null || echo 100)
MAX_DURATION=86400
MAX_INTERVAL=60
MIN_INTERVAL=1

usage() {
  cat <<'EOF'
Host-side telemetry sidecar for the isolated A5 calibration lane.

  bash Scripts/a5-host-telemetry.sh PID OUTPUT [DURATION] [INTERVAL]

  PID       target process id (digits only; e.g. "$(cat "$E2E_ROOT/pids/game.pid")")
  OUTPUT    JSONL file to append to (parent dir must exist and be writable)
  DURATION  total seconds to sample (default 3600, max 86400)
  INTERVAL  seconds between samples (default 1, min 1, max 60; DURATION >= INTERVAL)

Examples:
  bash Scripts/a5-host-telemetry.sh "$(cat /root/aaemu-e2e-a5-calibration/pids/game.pid)" \
      /root/aaemu-e2e-a5-calibration/logs/host-telemetry.jsonl 3600 1
  bash Scripts/a5-host-telemetry.sh 3467065 /tmp/host-telemetry.jsonl 10 1

Output: JSONL (start line, one sample per interval, end line). See the script header
for the full field list. Optional GC counters go to <OUTPUT>.gc.jsonl only when
dotnet-counters is installed; their absence never fails the run.

Exit codes: 0 = full duration sampled; 1 = target exited early; 2 = usage/validation
error or target not alive at start; 130/143 = interrupted.
EOF
}

# ---- argument parsing (positional only; no shell evaluation of args) ----
POS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    -*) echo "error: unknown option: $1" >&2; usage >&2; exit 2 ;;
    *) POS+=("$1") ;;
  esac
  shift
done

if [[ ${#POS[@]} -lt 2 || ${#POS[@]} -gt 4 ]]; then
  echo "error: expected PID OUTPUT [DURATION] [INTERVAL]" >&2
  usage >&2
  exit 2
fi

PID="${POS[0]}"
OUTPUT="${POS[1]}"
DURATION="${POS[2]:-3600}"
INTERVAL="${POS[3]:-1}"

# ---- validation (digits-only regexes; nothing from args is ever evaluated) ----
if [[ ! "$PID" =~ ^[0-9]+$ ]] || (( PID <= 0 )); then
  echo "error: PID must be a positive integer, got: ${PID@Q}" >&2
  exit 2
fi
if [[ ! "$DURATION" =~ ^[0-9]+$ ]] || (( DURATION < 1 || DURATION > MAX_DURATION )); then
  echo "error: DURATION must be an integer in [1, $MAX_DURATION], got: ${DURATION@Q}" >&2
  exit 2
fi
if [[ ! "$INTERVAL" =~ ^[0-9]+$ ]] || (( INTERVAL < MIN_INTERVAL || INTERVAL > MAX_INTERVAL )); then
  echo "error: INTERVAL must be an integer in [$MIN_INTERVAL, $MAX_INTERVAL], got: ${INTERVAL@Q}" >&2
  exit 2
fi
if (( DURATION < INTERVAL )); then
  echo "error: DURATION ($DURATION) must be >= INTERVAL ($INTERVAL)" >&2
  exit 2
fi
if [[ -z "$OUTPUT" ]]; then
  echo "error: OUTPUT must not be empty" >&2
  exit 2
fi
OUTDIR=$(dirname "$OUTPUT")
if [[ ! -d "$OUTDIR" ]]; then
  echo "error: output directory does not exist: $OUTDIR" >&2
  exit 2
fi
if [[ ! -w "$OUTDIR" ]]; then
  echo "error: output directory is not writable: $OUTDIR" >&2
  exit 2
fi
if [[ -d "$OUTPUT" ]]; then
  echo "error: OUTPUT is a directory: $OUTPUT" >&2
  exit 2
fi

# ---- /proc readers (all guarded; never fail the run) ----
# Sets PROC_UTIME / PROC_STIME (ticks) for the process; returns 1 if unreadable.
read_proc_stat() {
  local stat rest
  stat=$(<"/proc/$1/stat") || return 1
  rest=${stat##*) }   # strip "pid (comm) " up to the LAST ')' — comm may contain ')'
  local -a f=()
  read -r -a f <<< "$rest" || true
  PROC_UTIME=${f[11]:-0}   # overall field 14 (utime)
  PROC_STIME=${f[12]:-0}   # overall field 15 (stime)
}

# Sets THREAD_UTIME / THREAD_STIME (ticks, aggregate) and THREAD_COUNT.
read_thread_cpu() {
  local f rest ut=0 st=0 n=0
  local -a a=()
  for f in "/proc/$1/task"/*/stat; do
    [[ -r "$f" ]] || continue
    rest=$(<"$f") || continue
    rest=${rest##*) }
    read -r -a a <<< "$rest" || true
    ut=$(( ut + ${a[11]:-0} ))
    st=$(( st + ${a[12]:-0} ))
    n=$(( n + 1 ))
  done
  THREAD_UTIME=$ut
  THREAD_STIME=$st
  THREAD_COUNT=$n
}

# Sets STEAL_TICKS from /proc/stat (field 9 of the aggregate cpu line).
read_steal() {
  local v
  v=$(awk '/^cpu / {print $9}' /proc/stat 2>/dev/null) || v=""
  STEAL_TICKS=${v:-0}
}

# Prints one PSI value: psi_val <file> <some|full> <avg10|avg60|avg300|total>; "0" on any failure.
psi_val() {
  local v
  v=$(awk -v key="$2" -v field="$3" '
    $1 == key { for (i = 2; i <= NF; i++) { split($i, a, "="); if (a[1] == field) { print a[2]; exit } } }
  ' "$1" 2>/dev/null) || v=""
  [[ -n "$v" ]] && echo "$v" || echo "0"
}

# Sets CGROUP_NR_PERIODS / CGROUP_NR_THROTTLED / CGROUP_THROTTLED_USEC (0 when absent).
read_cgroup() {
  CGROUP_NR_PERIODS=0
  CGROUP_NR_THROTTLED=0
  CGROUP_THROTTLED_USEC=0
  [[ -r /sys/fs/cgroup/cpu.stat ]] || return 0
  local p t u
  p=$(awk '/^nr_periods / {print $2}' /sys/fs/cgroup/cpu.stat 2>/dev/null) || p=""
  t=$(awk '/^nr_throttled / {print $2}' /sys/fs/cgroup/cpu.stat 2>/dev/null) || t=""
  u=$(awk '/^throttled_usec / {print $2}' /sys/fs/cgroup/cpu.stat 2>/dev/null) || u=""
  CGROUP_NR_PERIODS=${p:-0}
  CGROUP_NR_THROTTLED=${t:-0}
  CGROUP_THROTTLED_USEC=${u:-0}
}

# Prints one-decimal percentage of delta_ms over wall_ms.
pct_of() {
  awk -v d="$1" -v w="$2" 'BEGIN { if (w > 0) printf "%.1f", d * 100.0 / w; else printf "0.0" }'
}

# ---- state ----
START_MS=$(date +%s%3N)
START_ISO=$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)
DURATION_MS=$(( DURATION * 1000 ))
SAMPLE_N=0
STOP=0
SIG=""
REASON=""

LAST_PROC_UTIME_MS=0
LAST_THREAD_UTIME_MS=0
LAST_THREAD_STIME_MS=0
LAST_STEAL_MS=0
LAST_PSI_CPU_SOME_TOTAL=0
LAST_PSI_CPU_FULL_TOTAL=0
LAST_CGROUP_THROTTLED_USEC=0
LAST_CGROUP_NR_THROTTLED=0
LAST_SAMPLE_MS=0

GC_CHILD=""
GC_MODE="unavailable"

cleanup() {
  if [[ -n "$GC_CHILD" ]] && kill -0 "$GC_CHILD" 2>/dev/null; then
    kill "$GC_CHILD" 2>/dev/null || true
    wait "$GC_CHILD" 2>/dev/null || true
  fi
}
trap cleanup EXIT
trap 'STOP=1; SIG=INT' INT
trap 'STOP=1; SIG=TERM' TERM

# ---- target liveness at start ----
if ! kill -0 "$PID" 2>/dev/null; then
  echo "error: target pid $PID is not alive" >&2
  exit 2
fi
COMM="?"
if [[ -r "/proc/$PID/comm" ]]; then
  COMM=$(<"/proc/$PID/comm")
fi
COMM=${COMM//[^[:print:]]/}

# ---- optional GC counters (only when the tool exists; never fatal) ----
if command -v dotnet-counters >/dev/null 2>&1; then
  dotnet-counters monitor --process-id "$PID" --counters System.Runtime --format json \
    >"$OUTPUT.gc.jsonl" 2>"$OUTPUT.gc.err" &
  GC_CHILD=$!
  GC_MODE="dotnet-counters"
fi

# ---- start line ----
printf '%s\n' "{\"event\":\"start\",\"pid\":$PID,\"comm\":\"$COMM\",\"output\":\"$OUTPUT\",\"duration\":$DURATION,\"interval\":$INTERVAL,\"gc\":\"$GC_MODE\",\"host\":\"$(hostname)\",\"clkTck\":$CLK_TCK,\"startedAt\":\"$START_ISO\"}" >>"$OUTPUT"

# ---- one sample ----
sample() {
  local now_ms now_iso wall_ms interval_ms alive=0
  now_ms=$(date +%s%3N)
  now_iso=$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)
  wall_ms=$(( now_ms - START_MS ))
  if (( LAST_SAMPLE_MS == 0 )); then
    interval_ms=$wall_ms
  else
    interval_ms=$(( now_ms - LAST_SAMPLE_MS ))
  fi
  if kill -0 "$PID" 2>/dev/null; then alive=1; fi

  local pu=0 ps=0 tu=0 ts=0 tc=0 st=0
  if (( alive )); then
    if read_proc_stat "$PID"; then pu=$PROC_UTIME; ps=$PROC_STIME; fi
    read_thread_cpu "$PID"
    tu=$THREAD_UTIME; ts=$THREAD_STIME; tc=$THREAD_COUNT
    read_steal
    st=$STEAL_TICKS
  fi

  local pu_ms ps_ms cpu_ms tu_ms ts_ms tcu_ms st_ms
  pu_ms=$(( pu * 1000 / CLK_TCK ))
  ps_ms=$(( ps * 1000 / CLK_TCK ))
  cpu_ms=$(( pu_ms + ps_ms ))
  tu_ms=$(( tu * 1000 / CLK_TCK ))
  ts_ms=$(( ts * 1000 / CLK_TCK ))
  tcu_ms=$(( tu_ms + ts_ms ))
  st_ms=$(( st * 1000 / CLK_TCK ))

  local dpu dtu dst dcu dsteal
  dpu=$(( pu_ms - LAST_PROC_UTIME_MS ))
  dtu=$(( tu_ms - LAST_THREAD_UTIME_MS ))
  dst=$(( ts_ms - LAST_THREAD_STIME_MS ))
  dcu=$(( dtu + dst ))
  dsteal=$(( st_ms - LAST_STEAL_MS ))
  (( dpu < 0 )) && dpu=0
  (( dtu < 0 )) && dtu=0
  (( dst < 0 )) && dst=0
  (( dcu < 0 )) && dcu=0
  (( dsteal < 0 )) && dsteal=0

  local c10 cf10 i10 if10 m10 mf10 cst cft dcst dcft
  c10=$(psi_val /proc/pressure/cpu some avg10)
  cf10=$(psi_val /proc/pressure/cpu full avg10)
  i10=$(psi_val /proc/pressure/io some avg10)
  if10=$(psi_val /proc/pressure/io full avg10)
  m10=$(psi_val /proc/pressure/memory some avg10)
  mf10=$(psi_val /proc/pressure/memory full avg10)
  cst=$(psi_val /proc/pressure/cpu some total)
  cft=$(psi_val /proc/pressure/cpu full total)
  dcst=$(( cst - LAST_PSI_CPU_SOME_TOTAL ))
  dcft=$(( cft - LAST_PSI_CPU_FULL_TOTAL ))
  (( dcst < 0 )) && dcst=0
  (( dcft < 0 )) && dcft=0

  read_cgroup
  local dthr dthrcnt
  dthr=$(( CGROUP_THROTTLED_USEC - LAST_CGROUP_THROTTLED_USEC ))
  dthrcnt=$(( CGROUP_NR_THROTTLED - LAST_CGROUP_NR_THROTTLED ))
  (( dthr < 0 )) && dthr=0
  (( dthrcnt < 0 )) && dthrcnt=0

  # First sample is a pure baseline: deltas are zeroed, later samples carry
  # deltas vs the previous sample.
  local baseline=0
  if (( SAMPLE_N == 0 )); then
    baseline=1
    dpu=0; dcu=0; dsteal=0; dcst=0; dcft=0; dthr=0; dthrcnt=0
  fi

  local proc_cpu_pct thread_cpu_pct steal_pct
  proc_cpu_pct=$(pct_of "$dpu" "$interval_ms")
  thread_cpu_pct=$(pct_of "$dcu" "$interval_ms")
  steal_pct=$(pct_of "$dsteal" "$interval_ms")

  LAST_PROC_UTIME_MS=$pu_ms
  LAST_THREAD_UTIME_MS=$tu_ms
  LAST_THREAD_STIME_MS=$ts_ms
  LAST_STEAL_MS=$st_ms
  LAST_PSI_CPU_SOME_TOTAL=$cst
  LAST_PSI_CPU_FULL_TOTAL=$cft
  LAST_CGROUP_THROTTLED_USEC=$CGROUP_THROTTLED_USEC
  LAST_CGROUP_NR_THROTTLED=$CGROUP_NR_THROTTLED
  LAST_SAMPLE_MS=$now_ms

  local j
  j="{\"event\":\"sample\",\"ts\":\"$now_iso\",\"elapsedMs\":$wall_ms,\"pid\":$PID,\"alive\":$alive"
  j+=",\"baseline\":$baseline"
  j+=",\"procUtimeMs\":$pu_ms,\"procStimeMs\":$ps_ms,\"procCpuMs\":$cpu_ms,\"procCpuPct\":$proc_cpu_pct"
  j+=",\"threads\":$tc,\"threadCpuMs\":$tcu_ms,\"threadCpuPct\":$thread_cpu_pct"
  j+=",\"stealMs\":$st_ms,\"stealPct\":$steal_pct"
  j+=",\"psiCpuSome10\":$c10,\"psiCpuFull10\":$cf10,\"psiIoSome10\":$i10,\"psiIoFull10\":$if10,\"psiMemSome10\":$m10,\"psiMemFull10\":$mf10"
  j+=",\"psiCpuSomeTotalDeltaUs\":$dcst,\"psiCpuFullTotalDeltaUs\":$dcft"
  j+=",\"cgroupNrPeriods\":$CGROUP_NR_PERIODS,\"cgroupNrThrottled\":$CGROUP_NR_THROTTLED,\"cgroupThrottledUs\":$CGROUP_THROTTLED_USEC"
  j+=",\"cgroupThrottledDeltaUs\":$dthr,\"cgroupThrottledCountDelta\":$dthrcnt}"
  printf '%s\n' "$j" >>"$OUTPUT"
}

# ---- main loop ----
while (( STOP == 0 )); do
  sample
  SAMPLE_N=$(( SAMPLE_N + 1 ))
  if ! kill -0 "$PID" 2>/dev/null; then
    REASON="process_exited"
    break
  fi
  if (( $(date +%s%3N) - START_MS >= DURATION_MS )); then
    REASON="duration_elapsed"
    break
  fi
  sleep "$INTERVAL" &
  wait $! 2>/dev/null || true
done

if [[ -z "$REASON" ]]; then
  REASON="interrupted"
fi

# ---- end line ----
local_final_alive=0
kill -0 "$PID" 2>/dev/null && local_final_alive=1
END_ISO=$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)
END_MS=$(date +%s%3N)
printf '%s\n' "{\"event\":\"end\",\"ts\":\"$END_ISO\",\"elapsedMs\":$(( END_MS - START_MS )),\"pid\":$PID,\"alive\":$local_final_alive,\"reason\":\"$REASON\",\"samples\":$SAMPLE_N}" >>"$OUTPUT"

case "$REASON" in
  duration_elapsed) exit 0 ;;
  process_exited) exit 1 ;;
  interrupted) [[ "$SIG" == "INT" ]] && exit 130 || exit 143 ;;
  *) exit 2 ;;
esac
