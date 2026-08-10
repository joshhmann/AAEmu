#!/usr/bin/env bash
# ensure-log-caps.sh — re-apply E2E NLog size caps + Info-default file rule
# (Thinpool Massacre prevention + log-storm prevention).
#
# Why: `dotnet publish` (E2E_REBUILD=1, or missing DLLs) copies NLog.config from
# the repo source tree (/root/aaemu-dev/AAEmu.{Game,Login}/NLog.config) into the
# runtime dirs. Repo configs are now size-capped (Sequence/25MB×20, merged via
# d3de7202b) with an Info-default file rule
# (${environment:AAEMU_E2E_LOG_LEVEL:whenEmpty=Info}, merged via
# fix/log-rule-info-default, t_aac423cd) — but older trees or partial merges can
# still publish configs that are size-capped yet carry the legacy Trace file
# rule (Trace storm ~1.2GB/hr at default boot). This guard must therefore
# rewrite BOTH properties on ANY config shape, not just uncapped ones.
#
# This script idempotently re-applies to every runtime NLog.config under E2E_ROOT:
#   - file + errors targets: Sequence rotation, archiveAboveSize=26214400 (25MB),
#     maxArchiveFiles=20  (matches live CT 133 caps)
#   - file rule: minlevel=Info default; TRACE only via AAEMU_E2E_LOG_LEVEL=Trace
# The rule rewrite fires even when the config is already size-capped (the
# d3de7202b merge left exactly that shape: caps present, Trace rule).
# Usage:
#   E2E_ROOT=/root/aaemu-e2e ./ensure-log-caps.sh          # default root
#   ./ensure-log-caps.sh /custom/e2e/root                  # explicit root
# Exit 0 if all configs capped + Info-default, 1 otherwise. No restarts — live
# processes pick the change up via NLog autoReload (touch the config to force an
# immediate reload after a publish).

set -u

E2E_ROOT="${1:-${E2E_ROOT:-/root/aaemu-e2e}}"
fail=0
changed=0

for cfg in "$E2E_ROOT"/runtime/game/NLog.config "$E2E_ROOT"/runtime/login/NLog.config; do
    if [ ! -f "$cfg" ]; then
        echo "SKIP  $cfg (missing)"
        continue
    fi

    # Cap markers we want on every File target.
    caps='archiveNumbering="Sequence" archiveAboveSize="26214400" maxArchiveFiles="20"'
    # Steady-state file rule: Info default, TRACE opt-in via
    # AAEMU_E2E_LOG_LEVEL=Trace — validated form (t_172d8bef static validation).
    rule_old='minlevel="Trace" maxlevel="Warn" writeTo="file"'
    rule_new='minlevel="${environment:AAEMU_E2E_LOG_LEVEL:whenEmpty=Info}" maxlevel="Warn" writeTo="file"'

    need_patch=0
    if ! grep -q 'archiveAboveSize="26214400"' "$cfg"; then
        need_patch=1
    fi
    if grep -q 'archiveEvery="Day"' "$cfg"; then
        need_patch=1
    fi
    # The Trace file rule must be rewritten on ANY config shape — including
    # configs that are already size-capped (t_aac423cd: d3de7202b merged caps
    # but left the rule at Trace; the old need_patch gate short-circuited here
    # and the rule rewrite never ran).
    if grep -qF "$rule_old" "$cfg"; then
        need_patch=1
    fi

    if [ "$need_patch" -eq 0 ]; then
        echo "OK    $cfg (already capped + Info-default rule)"
        continue
    fi

    echo "PATCH $cfg (uncapped pattern and/or Trace rule found)"
    cp "$cfg" "$cfg.bak-$(date +%Y%m%d-%H%M%S)"
    # Replace daily rotation attrs with the capped Sequence pattern on both
    # file and errors targets (they share the identical attribute string).
    sed -i 's|archiveNumbering="Date" archiveDateFormat="yyyy-MM-dd" archiveEvery="Day" maxArchiveFiles="9"|'"$caps"'|g' "$cfg"
    # Rewrite the file rule to the Info-default env-renderer form. Idempotent:
    # if the config already carries the env-renderer rule this sed matches
    # nothing and leaves it untouched.
    sed -i "s|$rule_old|$rule_new|" "$cfg"
    changed=$((changed + 1))

    if ! grep -q 'archiveAboveSize="26214400"' "$cfg"; then
        echo "ERROR $cfg cap patch did not apply — manual fix needed"
        fail=1
    fi
    if ! grep -q 'AAEMU_E2E_LOG_LEVEL:whenEmpty=Info' "$cfg"; then
        echo "ERROR $cfg rule rewrite did not apply — manual fix needed"
        fail=1
    fi
done

echo
echo "configs checked: 2, patched: $changed, failures: $fail"
echo "TRACE debugging: export AAEMU_E2E_LOG_LEVEL=Trace before boot (do NOT leave on)."
[ "$fail" -eq 0 ]
