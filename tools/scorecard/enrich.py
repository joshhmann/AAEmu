#!/usr/bin/env python3
"""Build final enriched scorecard: code-wiring + upstream issues."""
import json
import subprocess

issues = json.load(open("/tmp/issues.json"))
quests = sorted({str(i["number"]) for i in issues if "quest" in i["labels"]}, key=int)
sys_bugs = sorted(
    [i for i in issues if "bug" in i["labels"] and "quest" not in i["labels"]],
    key=lambda i: i["number"])

print("# ArcheAge Slums — Feature Completeness Scorecard (enriched)")
print()
print("Layers: (1) canonical 1.2 data surface (679 sqlite tables), (2) code wiring,")
print("(3) upstream issue tracker (AAEmu/AAEmu open issues, 2026-08-03).")
print()

r = subprocess.run(["python3", "/root/aaemu-dev/tools/scorecard/scorecard.py"], capture_output=True, text=True)
print(r.stdout)

print("## Upstream issue tracker — known gaps")
print()
print(f"- {len(issues)} open issues: 50 bug · 30 quest · 9 missing-data · 4 enhancement · 3 skill")
print()
print("### Quests reported broken (playtest targets, by ID)")
print()
print(", ".join(quests))
print()
print("### System-level bugs (non-quest)")
print()
for i in sys_bugs:
    labels = ",".join(l for l in i["labels"] if l not in ("bug", "stale"))
    print(f"- #{i['number']} [{labels or 'bug'}] {i['title'][:120]}")
