#!/usr/bin/env bash
# hermes-verify-appearance.sh — focused ad-hoc verification for t_61814965.
set -u
cd /root/aaemu-dev || exit 2
pass=0; fail=0
check() {
  if [ "$2" -eq 0 ]; then echo "PASS: $1"; pass=$((pass+1));
  else echo "FAIL: $1"; fail=$((fail+1)); fi
}
echo "== 1/3 build =="
dotnet build --configuration Release AAEmu.slnx --nologo -v q > /tmp/hermes-verify-build.log 2>&1
check "solution Release build" $?
echo "== 2/3 changed-behavior test classes =="
for cls in BotAppearanceFactoryTests BotAppearanceDefaultsTests BotPresenceCoordinatorTests; do
  ./scripts/gate.sh "$cls" > "/tmp/hermes-verify-${cls}.log" 2>&1
  rc=$?
  failed=$(grep -oP 'failed: \K[0-9]+' "/tmp/hermes-verify-${cls}.log" | head -1)
  total=$(grep -oP 'total: \K[0-9]+' "/tmp/hermes-verify-${cls}.log" | head -1)
  echo "  ${cls}: total=${total:-?} failed=${failed:-?} rc=${rc}"
  check "${cls}" $rc
done
echo "== 3/3 runtime data probe =="
python3 /tmp/hermes-verify-data.py > /tmp/hermes-verify-data.log 2>&1
check "sqlite catalogs cover all 8 models + no ModelIdFor drift" $?
echo "== verdict =="
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ] && echo "VERIFIED" || echo "NOT VERIFIED"
exit "$fail"
