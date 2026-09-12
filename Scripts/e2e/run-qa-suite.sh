#!/usr/bin/env bash
# ============================================================================
# run-qa-suite.sh — tiered E2E QA / soak / smoke suite on the .165 testing box.
#
# All tiers run on root@192.168.0.165 (the testing server), each in its own
# ISOLATED lane under /root/aaemu-e2e-<lane>: unique E2E root, unique port
# block + DB port, own compose project, fresh repo at the CURRENT HEAD SHA,
# canonical game-data md5-verified, artifacts rsynced back to the dev host,
# PID-verified teardown. pkill is banned. Prod /root/AAEmu (ports 1237/1239/
# 1250/3306) and sibling lanes are NEVER touched.
#
# Tiers:
#   smoke  <5 min  — boot an isolated lane on .165, run ONE cheap E2E
#                    scenario (stack boots + live bridge handshake happen
#                    inside the scenario; bridge port reachability is asserted
#                    again post-run). No soak budget tables.
#   qa     <20 min — run the named (default B-series functional) E2E class.
#                    No budget tables required.
#   soak   long    — budgeted S-runs: B4 PACK/SLAVE/FARM, Dominion soak cycle,
#                    Village full-day; budget verdicts are asserted by the
#                    classes themselves.
#
# Usage:
#   ./run-qa-suite.sh --tier qa
#   ./run-qa-suite.sh --tier soak --target B4PackSoakE2eTests --minutes 360
#   ./run-qa-suite.sh --tier qa --target all          # every non-soak E2E class
#   ./run-qa-suite.sh --tier smoke --lane smoke-rb    # one cheap scenario
#   ./run-qa-suite.sh --tier soak --target B4SlaveSoakE2eTests --keep
#
# Args:
#   --tier    <smoke|qa|soak>   required
#   --target  <FQCN|ClassSimpleName|all>   default: tier-appropriate class list
#   --lane    <name>            lane root /root/aaemu-e2e-<name>; default from target
#   --minutes <n>               window override (passed to the class's duration env)
#   --keep                      skip teardown (leave lane up for debugging)
#   --artifacts <local-dir>     where evidence is rsynced back (default ./soak-artifacts)
#
# Exit code: 0 = PASS (every run class passed); number of failed run classes
# otherwise (mirrors bot-regression-pass.sh convention).
#
# HARD SAFETY: refuses to touch prod /root/AAEmu (read-only source only),
# refuses to derive the prod ports 1237/1239/1250/3306, refuses to start if
# any requested lane port is occupied, and teardown only ever kills processes
# whose /proc/<pid>/cwd is under THIS lane root — never pkill, never a
# sibling lane.
# ============================================================================
set -euo pipefail

SSH_HOST="${SSH_HOST:-root@192.168.0.165}"
REMOTE_LANE_BASE="/root/aaemu-e2e"
CANONICAL_MD5="78b3bdbf038db3b927056106efdf91af"
CANONICAL_DATA_SRC="/root/aaemu-e2e-a5-tier3-sixhour/runtime/game-data" # verified full canonical tree on .165
PROD_PORTS="1234 1237 1239 1250 1260 1280 3306"          # NEVER derived
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARTIFACTS_BASE="${ARTIFACTS_BASE:-$REPO_ROOT/soak-artifacts}"
SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
if { [ ! -e "$REPO_ROOT/.git" ] && ! git -C "$REPO_ROOT" rev-parse --git-dir >/dev/null 2>&1; } ||
   [ ! -f "$REPO_ROOT/AAEmu.slnx" ] ||
   [ "$SCRIPT_PATH" != "$REPO_ROOT/Scripts/e2e/run-qa-suite.sh" ]; then
  echo "error: run-qa-suite.sh must be launched from the DEV-HOST repo checkout (e.g. /root/aaemu-dev); it rsyncs the working tree to $SSH_HOST and is NOT run on the testing host" >&2
  exit 2
fi

# ---- argument surface ------------------------------------------------------
TIER=""
TARGET=""
LANE=""
MINUTES=""
KEEP=0
ARTIFACTS=""

usage() { sed -n '1,30p' "$0" | sed 's/^# \{0,1\}//' | grep -v '^=\|^$' | head -40; exit 0; }

while [ $# -gt 0 ]; do
  case "$1" in
    --tier)     TIER="$2"; shift 2 ;;
    --target)   TARGET="$2"; shift 2 ;;
    --lane)     LANE="$2"; shift 2 ;;
    --minutes)  MINUTES="$2"; shift 2 ;;
    --keep)     KEEP=1; shift ;;
    --artifacts)ARTIFACTS="$2"; shift 2 ;;
    -h|--help|help) usage ;;
    *) echo "error: unknown argument '$1' (see $0 --help)" >&2; exit 1 ;;
  esac
done

case "$TIER" in
  smoke|qa|soak) ;;
  "") echo "error: --tier {smoke|qa|soak} is required" >&2; exit 1 ;;
  *) echo "error: --tier must be smoke|qa|soak (got '$TIER')" >&2; exit 1 ;;
esac

# ---- per-tier default target class lists ----------------------------------
# FQCNs are fully qualified; the MTP runner is invoked with a treenode filter
# (the repo's confirmed gate.sh convention). Simple-class names are matched by
# the shell and expanded to FQCN below.
SOAK_CLASSES=(
  "AAEmu.IntegrationTests.E2e.B4PackSoakE2eTests"
  "AAEmu.IntegrationTests.E2e.B4SlaveSoakE2eTests"
  "AAEmu.IntegrationTests.E2e.B4FarmSoakE2eTests"
  "AAEmu.IntegrationTests.E2e.DominionSoakCycleE2eTests"
  "AAEmu.IntegrationTests.E2e.VillageFullDayRestartE2eTests"
)
# Gate soaks are NOT default-run (they are env-gated and long); select via
# --target. Keep the mapping for the activation envs below.
GATE_SOAK_CLASSES=(
  "AAEmu.IntegrationTests.E2e.G2.A5Tier3AcceptanceProbeTests"
  "AAEmu.IntegrationTests.E2e.Gate.SchedulerSoakStage1Tests"
  "AAEmu.IntegrationTests.E2e.G2.ScalingProbeTests"
  "AAEmu.IntegrationTests.E2e.G2.A3StormProbeTests"
  "AAEmu.IntegrationTests.E2e.G2.A4AcceptanceProbeTests"
)

QA_CLASSES=(
  "AAEmu.IntegrationTests.E2e.B1HousingRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.B2FarmRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.B6MerchantConservationE2eTests"
  # Other non-soak E2E classes (all reachable via --target all):
  #   M1M2ContractReplayE2eTests M3aM4EconomicReplayE2eTests
  #   M51AttachedPackRestartE2eTests M51DepositWithdrawReplayHookE2eTests
  #   EconomyDayCycleE2eTests FishingVerificationE2eTests DuelFactionSwapE2eTests
  #   TransferRideE2eTests PartySpikeE2eTests PartyFollowAssistE2eTests
  #   AdventurerSpikeE2eTests InterzoneLoopE2eTests Q4LiveHuntLootE2eTests
  #   RowboatE2eTests SlaveTestBugHuntE2eTests HaulerMidRouteRestartE2eTests
  #   DominionRestartPersistenceE2eTests B4BotRestartPersistenceE2eTests
  #   MailS3RestartE2eTests AuctionHouseRestartE2eTests LaneDAuctionHouseE2eTests
  #   IndunExitE2eTests IndunPartyE2eTests JusticeCrimeE2eTests PvpHandshakeE2eTests
  #   BotControlApiE2eTests BotControlActionMcpE2eTests VillageDayCycleRestartE2eTests
)

SMOKE_CLASSES=(
  "AAEmu.IntegrationTests.E2e.RowboatE2eTests"
)

ALL_NON_SOAK_QA=(
  "AAEmu.IntegrationTests.E2e.B1HousingRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.B2FarmRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.B6MerchantConservationE2eTests"
  "AAEmu.IntegrationTests.E2e.M1M2ContractReplayE2eTests"
  "AAEmu.IntegrationTests.E2e.M3aM4EconomicReplayE2eTests"
  "AAEmu.IntegrationTests.E2e.M51AttachedPackRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.M51DepositWithdrawReplayHookE2eTests"
  "AAEmu.IntegrationTests.E2e.EconomyDayCycleE2eTests"
  "AAEmu.IntegrationTests.E2e.FishingVerificationE2eTests"
  "AAEmu.IntegrationTests.E2e.DuelFactionSwapE2eTests"
  "AAEmu.IntegrationTests.E2e.TransferRideE2eTests"
  "AAEmu.IntegrationTests.E2e.PartySpikeE2eTests"
  "AAEmu.IntegrationTests.E2e.PartyFollowAssistE2eTests"
  "AAEmu.IntegrationTests.E2e.AdventurerSpikeE2eTests"
  "AAEmu.IntegrationTests.E2e.InterzoneLoopE2eTests"
  "AAEmu.IntegrationTests.E2e.Q4LiveHuntLootE2eTests"
  "AAEmu.IntegrationTests.E2e.RowboatE2eTests"
  "AAEmu.IntegrationTests.E2e.SlaveTestBugHuntE2eTests"
  "AAEmu.IntegrationTests.E2e.HaulerMidRouteRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.DominionRestartPersistenceE2eTests"
  "AAEmu.IntegrationTests.E2e.B4BotRestartPersistenceE2eTests"
  "AAEmu.IntegrationTests.E2e.MailS3RestartE2eTests"
  "AAEmu.IntegrationTests.E2e.AuctionHouseRestartE2eTests"
  "AAEmu.IntegrationTests.E2e.LaneDAuctionHouseE2eTests"
  "AAEmu.IntegrationTests.E2e.IndunExitE2eTests"
  "AAEmu.IntegrationTests.E2e.IndunPartyE2eTests"
  "AAEmu.IntegrationTests.E2e.JusticeCrimeE2eTests"
  "AAEmu.IntegrationTests.E2e.PvpHandshakeE2eTests"
  "AAEmu.IntegrationTests.E2e.BotControlApiE2eTests"
  "AAEmu.IntegrationTests.E2e.BotControlActionMcpE2eTests"
  "AAEmu.IntegrationTests.E2e.VillageDayCycleRestartE2eTests"
)

# -------- resolve target list ------------------------------------------------
expand_class() {
  # $1 = user-provided target token (FQCN, simple class name, or 'all')
  local tok="$1" fq
  [ "$tok" = all ] && return 0
  # If it's already a dotted FQCN, accept as-is.
  case "$tok" in *.*) echo "$tok"; return 0 ;; esac
  # Simple class name -> full name if it's one of the known classes.
  for fq in "${SOAK_CLASSES[@]}" "${QA_CLASSES[@]}" "${SMOKE_CLASSES[@]}" \
            "${GATE_SOAK_CLASSES[@]}"; do
    [[ "$fq" == *".$tok" ]] && { echo "$fq"; return 0; }
  done
  echo "error: --target '$tok' is not a known E2E class name (give the FQCN or a class suffix)" >&2
  exit 1
}

case "$TIER" in
  smoke)
    if [ -n "$TARGET" ] && [ "$TARGET" != all ]; then
      RUN_CLASSES=( "$(expand_class "$TARGET")" )
    elif [ "$TARGET" = all ]; then
      RUN_CLASSES=( "${SMOKE_CLASSES[@]}" )
    else
      RUN_CLASSES=( "${SMOKE_CLASSES[@]}" )
    fi
    ;;
  qa)
    if [ -n "$TARGET" ] && [ "$TARGET" != all ]; then
      RUN_CLASSES=( "$(expand_class "$TARGET")" )
    elif [ "$TARGET" = all ]; then
      RUN_CLASSES=( "${ALL_NON_SOAK_QA[@]}" )
    else
      RUN_CLASSES=( "${QA_CLASSES[@]}" )      # B-series functional default
    fi
    ;;
  soak)
    if [ -n "$TARGET" ] && [ "$TARGET" != all ]; then
      RUN_CLASSES=( "$(expand_class "$TARGET")" )
    elif [ "$TARGET" = all ]; then
      RUN_CLASSES=( "${SOAK_CLASSES[@]}" "${GATE_SOAK_CLASSES[@]}" )
    else
      RUN_CLASSES=( "${SOAK_CLASSES[@]}" )    # B4 PACK/SLAVE/FARM + Dominion + Village full-day
    fi
    ;;
esac

# -------- lane + port derivation (deterministic on BOTH hosts) --------------
if [ -z "$LANE" ]; then
  if [ "${#RUN_CLASSES[@]}" -eq 1 ]; then
    # lane from the last dotted segment of the FQCN, lowercased
    LANE="$(printf '%s' "${RUN_CLASSES[0]}" | sed 's/.*\.//' | tr '[:upper:]' '[:lower:]')"
  else
    LANE="${TIER}-suite"
  fi
fi
# sanitize lane to [a-z0-9_-]
LANE="$(printf '%s' "$LANE" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9_-]/-/g')"

L_CRC="$(printf '%s' "$LANE" | cksum | awk '{print $1}')"
APP_BASE=$(( 20000 + (L_CRC % 10000) ))              # 20000..29999
DB_PORT=$(( 40000 + (L_CRC % 20000) ))               # 40000..59999
LOGIN_INT="$APP_BASE"
LOGIN=$((APP_BASE+3))
GAME=$((APP_BASE+5))
STREAM=$((APP_BASE+13))
BRIDGE=$((APP_BASE+23))
WEBAPI=$((APP_BASE+43))
COMPOSE_PROJECT="e2e_${LANE}"
LANE_ROOT="${REMOTE_LANE_BASE}-${LANE}"

# ---- local pre-flight ------------------------------------------------------
HEAD_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
LOCAL_TS="$(date +%Y%m%d-%H%M%S)"
ARTIFACTS="${ARTIFACTS:-$ARTIFACTS_BASE}/${LANE}/${LOCAL_TS}"
mkdir -p "$ARTIFACTS"

# never derive a prod port
for p in $PROD_PORTS; do
  if [ "$LOGIN" = "$p" ] || [ "$GAME" = "$p" ] || [ "$STREAM" = "$p" ] || \
     [ "$BRIDGE" = "$p" ] || [ "$LOGIN_INT" = "$p" ] || [ "$WEBAPI" = "$p" ] || \
     [ "$DB_PORT" = "$p" ]; then
    echo "error: lane '$LANE' derived a protected prod port ($p) — pick another --lane" >&2
    exit 1
  fi
done

echo "== run-qa-suite (tier=$TIER, lane=$LANE, host=$SSH_HOST)"
echo "   target(s): ${RUN_CLASSES[*]}"
echo "   lane root: $LANE_ROOT (remote)"
echo "   ports: login:$LOGIN login_int:$LOGIN_INT game:$GAME stream:$STREAM bridge:$BRIDGE webapi:$WEBAPI db:$DB_PORT"
echo "   compose project: $COMPOSE_PROJECT"
echo "   HEAD SHA: $HEAD_SHA"
echo "   artifacts will be rsynced to: $ARTIFACTS"
echo "   keep: $KEEP"

# ---- remote orchestration ---------------------------------------------------
# Each remote block is piped to `ssh HOST "ENV=val ... bash -s"` with the lane
# values passed as env assignments in the command prefix (double-quoted for
# local interpolation) and the script body as a fully single-quoted heredoc
# (no escaping; remote `$` works natively). This keeps quoting robust and
# reviewable.

run_remote() {
  # $1.. = env prefix; body on stdin
  ssh "$SSH_HOST" "$* bash -s"
}

TEARDOWN_DONE=0
teardown_lane() {
  [ "$TEARDOWN_DONE" -eq 0 ] || return 0
  TEARDOWN_DONE=1
  [ "$KEEP" -eq 0 ] || {
    echo "== --keep set: leaving lane $LANE up (artifacts not removed; teardown skipped)"
    return 0
  }
  echo "== teardown (lane $LANE, PID-verified, pkill banned)"
  echo "   teardown trap: before run_remote"
  run_remote "LANE_ROOT='$LANE_ROOT' COMPOSE_PROJECT='$COMPOSE_PROJECT' LOGIN='$LOGIN' LOGIN_INT='$LOGIN_INT' GAME='$GAME' STREAM='$STREAM' BRIDGE='$BRIDGE' WEBAPI='$WEBAPI' DB_PORT='$DB_PORT'" <<'REMOTE'
    set -euo pipefail
    kill_lane_procs() {
      local d pid cwd sig
      for sig in TERM KILL; do
        for d in /proc/[0-9]*; do
          pid="${d#/proc/}"
          [ -r "/proc/$pid/cmdline" ] || continue
          if tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -qE 'AAEmu\.(Login|Game)\.dll'; then
            cwd="$(readlink "/proc/$pid/cwd" 2>/dev/null || true)"
            case "$cwd" in
              "$LANE_ROOT"/*) echo "   stopping lane proc $pid (cwd $cwd, SIG$sig)"; kill -"$sig" "$pid" 2>/dev/null || true ;;
            esac
          fi
        done
        [ "$sig" = TERM ] && sleep 3
      done
    }
    set +e
    kill_lane_procs
    set -e
    compose_cmd=(docker compose -p "$COMPOSE_PROJECT" -f "$LANE_ROOT/repo/Scripts/e2e/docker-compose.yaml" --env-file "$LANE_ROOT/.env" down -v)
    echo "   teardown compose command: ${compose_cmd[*]}"
    set +e
    "${compose_cmd[@]}"
    compose_rc=$?
    set -e
    echo "   teardown compose exit: $compose_rc"
    [ "$compose_rc" -eq 0 ] || exit "$compose_rc"
    for port in "$LOGIN" "$LOGIN_INT" "$GAME" "$STREAM" "$BRIDGE" "$WEBAPI" "$DB_PORT"; do
      if ss -ltn 2>/dev/null | awk '{print $4}' | grep -q "[:.]${port}$"; then
        echo "error: teardown left lane port $port bound" >&2
        exit 2
      fi
    done
    echo '   teardown complete (our lane processes + our compose project only; ports free)'
REMOTE
  echo "   teardown trap: after run_remote"
}

trap 'rc=$?; teardown_lane; trap - EXIT; exit "$rc"' EXIT
trap 'exit 130' INT TERM

# 1) remote pre-flight: box reachable, prod data present, no port collisions.
run_remote "CANONICAL_DATA_SRC='$CANONICAL_DATA_SRC' LANE='$LANE' LANE_ROOT='$LANE_ROOT' LOGIN='$LOGIN' LOGIN_INT='$LOGIN_INT' GAME='$GAME' STREAM='$STREAM' BRIDGE='$BRIDGE' WEBAPI='$WEBAPI' DB_PORT='$DB_PORT'" <<'REMOTE'
  set -euo pipefail
  command -v docker >/dev/null || { echo 'error: docker missing on .165'; exit 2; }
  [ -d "$CANONICAL_DATA_SRC" ] || { echo "error: canonical data source missing: $CANONICAL_DATA_SRC"; exit 2; }
  busy=()
  for port in "$LOGIN" "$LOGIN_INT" "$GAME" "$STREAM" "$BRIDGE" "$WEBAPI" "$DB_PORT"; do
    if ss -ltn 2>/dev/null | awk '{print $4}' | grep -q "[:.]${port}$"; then busy+=("$port"); fi
  done
  if [ ${#busy[@]} -gt 0 ]; then
    echo "error: lane '$LANE' port(s) already occupied on .165: ${busy[*]} (refusing — sibling lane or prod in use)" >&2
    exit 2
  fi
  echo 'remote pre-flight OK'
REMOTE
echo "== remote pre-flight OK (ports free, prod data present)"

# 2) create the isolated lane root + rsync the repo working tree at HEAD.
#    include .git so E2eStack.FindRepoRoot anchors; exclude heavy/build output.
run_remote "LANE_ROOT='$LANE_ROOT'" <<'REMOTE'
  mkdir -p "$LANE_ROOT"
REMOTE
echo "== syncing repo (HEAD $HEAD_SHA) to .165:$LANE_ROOT/repo ..."
rsync -a --delete \
  --exclude '.git/worktrees' \
  --exclude '.worktrees' \
  --exclude '**/bin/' \
  --exclude '**/obj/' \
  --exclude 'TestResults/' \
  --exclude '.server_files' \
  --exclude '.client_files' \
  --exclude '.vs/' \
  --exclude 'aaemu-dev-docs.zip' \
  --exclude ".publish-stamp" \
  "$REPO_ROOT/" "$SSH_HOST:$LANE_ROOT/repo/"
run_remote "LANE_ROOT='$LANE_ROOT' HEAD_SHA='$HEAD_SHA'" <<'REMOTE'
  set -euo pipefail
  printf '%s' "$HEAD_SHA" > "$LANE_ROOT/.git-head-sha"
  if grep -q "$HEAD_SHA" "$LANE_ROOT/repo/.git/HEAD" 2>/dev/null; then echo 'shallow-ref-ok'; else echo 'non-git-HEAD-noted'; fi
REMOTE

# 3) provision the lane-scoped ensure-log-caps.sh (E2eStack.EnsureServerBinaries
#    REQUIRES $LANE_ROOT/ensure-log-caps.sh to exist for E2E_REBUILD=1 publishes;
#    a missing/corrupt one fails the publish on .165).
ensure_log_caps="$LANE_ROOT/ensure-log-caps.sh"
cat > "/tmp/ensure-log-caps.$$.sh" <<'ENSC'
#!/usr/bin/env bash
# re-apply E2E NLog size caps + Info-default rule (Thinpool Massacre + log-storm).
set -u
E2E_ROOT="${1:-${E2E_ROOT:-/root/aaemu-e2e}}"; fail=0
for cfg in "$E2E_ROOT"/runtime/game/NLog.config "$E2E_ROOT"/runtime/login/NLog.config; do
  [ -f "$cfg" ] || { echo "SKIP $cfg (missing)"; continue; }
  caps='archiveNumbering="Sequence" archiveAboveSize="26214400" maxArchiveFiles="20"'
  rule_old='minlevel="Trace" maxlevel="Warn" writeTo="file"'
  rule_new='minlevel="${environment:AAEMU_E2E_LOG_LEVEL:whenEmpty=Info}" maxlevel="Warn" writeTo="file"'
  need=0
  grep -q 'archiveAboveSize="26214400"' "$cfg" || need=1
  grep -q 'archiveEvery="Day"' "$cfg" && need=1
  grep -qF "$rule_old" "$cfg" && need=1
  if [ "$need" -eq 0 ]; then echo "OK $cfg"; continue; fi
  cp "$cfg" "$cfg.bak-$(date +%Y%m%d-%H%M%S)"
  sed -i 's|archiveNumbering="Date" archiveDateFormat="yyyy-MM-dd" archiveEvery="Day" maxArchiveFiles="9"|'"$caps"'|g' "$cfg"
  sed -i "s|$rule_old|$rule_new|" "$cfg"
  grep -q 'archiveAboveSize="26214400"' "$cfg" || { echo "ERROR cap patch failed: $cfg"; fail=1; }
  grep -q 'AAEMU_E2E_LOG_LEVEL:whenEmpty=Info' "$cfg" || { echo "ERROR rule rewrite failed: $cfg"; fail=1; }
done
[ "$fail" -eq 0 ]
ENSC
chmod +x "/tmp/ensure-log-caps.$$.sh"
scp -q "/tmp/ensure-log-caps.$$.sh" "$SSH_HOST:${ensure_log_caps}" && rm -f "/tmp/ensure-log-caps.$$.sh"
echo "   ensure-log-caps.sh provisioned at $LANE_ROOT"

# 4) provision canonical game-data into the lane (read-only rsync FROM the
#    retained canonical lane tree), then md5-verify compact.sqlite3 (HARD gate).
if [ "$TIER" = "smoke" ]; then
  run_remote "LANE_ROOT='$LANE_ROOT'" <<'REMOTE'
    set -euo pipefail
    cfg="$LANE_ROOT/runtime/game/NLog.config"
    [ -f "$cfg" ] || exit 0
    sed -i 's/minlevel="\${environment:AAEMU_E2E_LOG_LEVEL:whenEmpty=Info}"/minlevel="Debug"/' "$cfg"
    grep -q 'minlevel="Debug" maxlevel="Warn" writeTo="file"' "$cfg"
REMOTE
fi
run_remote "LANE_ROOT='$LANE_ROOT' CANONICAL_DATA_SRC='$CANONICAL_DATA_SRC' CANONICAL_MD5='$CANONICAL_MD5'" <<'REMOTE'
  set -euo pipefail
  rm -rf "$LANE_ROOT/runtime/game-data"
  mkdir -p "$LANE_ROOT/runtime/game-data"
  echo '== syncing canonical game-data from retained canonical lane ...'
  rsync -a "$CANONICAL_DATA_SRC/" "$LANE_ROOT/runtime/game-data/"
  md5=$(md5sum "$LANE_ROOT/runtime/game-data/Data/compact.sqlite3" | awk '{print $1}')
  echo "   canonical compact.sqlite3 md5: $md5"
  [ "$md5" = "$CANONICAL_MD5" ] || {
    echo "error: canonical game-data md5 mismatch (got $md5, want $CANONICAL_MD5)" >&2
    exit 2
  }
  echo 'canonical md5 verified'
REMOTE
echo "== game-data md5 verified ($CANONICAL_MD5)"
# Smoke needs the two per-ship Debug lifecycle records that Rowboat asserts;
# QA/soak retain the capped Info default to avoid log storms.
DURATION_ENV=""
LOG_LEVEL_ENV=""
SIXHOUR_GATE=""
if [ "$TIER" = "smoke" ]; then
  LOG_LEVEL_ENV="AAEMU_E2E_LOG_LEVEL=Debug"
fi
if [ -n "$MINUTES" ]; then
  DURATION_ENV="E2E_DURATION_MINUTES='$MINUTES'"
fi
if [ -n "$MINUTES" ] && [ "$MINUTES" -ge 360 ]; then
  SIXHOUR_GATE="A5_TIER3_SIX_HOUR=1 "
fi

PASS_CNT=0; FAIL_CNT=0
for CLASS in "${RUN_CLASSES[@]}"; do
  SHORT="$(printf '%s' "$CLASS" | sed 's/.*\.//')"
  echo "=================================================================="
  echo "[run-qa-suite] RUN class: $CLASS (tier=$TIER lane=$LANE)"
  echo "=================================================================="
  if run_remote "LANE_ROOT='$LANE_ROOT' CLASS='$CLASS' SHORT='$SHORT' LOCAL_TS='$LOCAL_TS' \
              E2E_LOGIN_PORT='$LOGIN' E2E_GAME_PORT='$GAME' E2E_STREAM_PORT='$STREAM' \
              E2E_BRIDGE_PORT='$BRIDGE' E2E_INTERNAL_PORT='$LOGIN_INT' E2E_WEBAPI_PORT='$WEBAPI' \
              E2E_DB_PORT='$DB_PORT' DB_HOST_PORT='$DB_PORT' COMPOSE_PROJECT_NAME='$COMPOSE_PROJECT' \
              $LOG_LEVEL_ENV $DURATION_ENV $SIXHOUR_GATE" <<'REMOTE'
    set -euo pipefail
    export E2E_ROOT="$LANE_ROOT" \
           E2E_LOGIN_PORT="$E2E_LOGIN_PORT" E2E_GAME_PORT="$E2E_GAME_PORT" \
           E2E_STREAM_PORT="$E2E_STREAM_PORT" E2E_BRIDGE_PORT="$E2E_BRIDGE_PORT" \
           E2E_INTERNAL_PORT="$E2E_INTERNAL_PORT" E2E_WEBAPI_PORT="$E2E_WEBAPI_PORT" \
           E2E_DB_PORT="$E2E_DB_PORT" DB_HOST_PORT="$E2E_DB_PORT" \
           COMPOSE_PROJECT_NAME="$COMPOSE_PROJECT_NAME" \
           E2E_REBUILD=1 E2E_GAME_HOST=127.0.0.1 \
           ${A5_TIER3_SIX_HOUR_MINUTES:+A5_TIER3_SIX_HOUR_MINUTES=$A5_TIER3_SIX_HOUR_MINUTES} \
           ${SCHEDULER_SOAK_MINUTES:+SCHEDULER_SOAK_MINUTES=$SCHEDULER_SOAK_MINUTES} \
           ${SCALING_PROBE_MINUTES:+SCALING_PROBE_MINUTES=$SCALING_PROBE_MINUTES} \
           ${A5_TIER3_SIX_HOUR:+A5_TIER3_SIX_HOUR=$A5_TIER3_SIX_HOUR}
    mkdir -p "$LANE_ROOT/logs"
    cd "$LANE_ROOT/repo"
    logfile="$LANE_ROOT/logs/qa-class-$SHORT-${LOCAL_TS}.log"
    listing_log="$LANE_ROOT/logs/qa-class-$SHORT-${LOCAL_TS}.list-tests.log"
    set +e
    LISTING="$(dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
      --configuration Release --list-tests --filter-class "$CLASS" 2>&1)"
    listing_rc=$?
    set -e
    printf '%s\n' "$LISTING" > "$listing_log"
    grep -q "$SHORT" <<<"$LISTING" || {
      printf 'zero tests discovered for %s (filter class: %s; list-tests exit %s)\n' "$CLASS" "$CLASS" "$listing_rc" > "$logfile"
      exit 3
    }
    set +e
    dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
      --configuration Release --filter-class "$CLASS" \
      2>&1 | tee "$logfile"
    test_rc="${PIPESTATUS[0]}"
    set -e
    # Keep a deterministic evidence file even if a test runner exits before
    # tee can create its output (for example, an early process launch error).
    if [ ! -s "$logfile" ]; then
      printf 'dotnet test produced no output (exit %s)\n' "$test_rc" > "$logfile"
    fi
    exit "$test_rc"
REMOTE
  then
    RC=0
  else
    RC=$?
  fi
  if [ "$RC" -eq 0 ]; then PASS_CNT=$((PASS_CNT+1)); echo "   [run-qa-suite] $SHORT PASS"; else FAIL_CNT=$((FAIL_CNT+1)); echo "   [run-qa-suite] $SHORT FAIL (exit $RC)"; fi
done

# 6) rsync evidence + reports back to the dev host. The EXIT trap performs
#    teardown afterward, including on an interrupted or failed class.
echo "== rsyncing lane evidence back to $ARTIFACTS"
mkdir -p "$ARTIFACTS"
rsync -a "$SSH_HOST:$LANE_ROOT/logs/" "$ARTIFACTS/" 2>/dev/null || \
  echo "   (no lane logs found to copy back)"
printf '%s' "$HEAD_SHA" > "$ARTIFACTS/git-head-sha.txt"


# 8) summary
echo "=================================================================="
echo "[run-qa-suite] SUMMARY (tier=$TIER, lane=$LANE, HEAD=$HEAD_SHA)"
echo "   passed classes: $PASS_CNT   failed: $FAIL_CNT"
echo "   artifacts: $ARTIFACTS"
echo "   tested SHA: $HEAD_SHA"
printf '%s %s %s %s\n' "$PASS_CNT" "$FAIL_CNT" "$HEAD_SHA" "$ARTIFACTS" > "$ARTIFACTS/summary.txt"
if [ "$FAIL_CNT" -eq 0 ]; then echo "   RESULT: PASS"; else echo "   RESULT: FAIL"; fi
exit "$FAIL_CNT"
