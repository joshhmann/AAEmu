#!/usr/bin/env python3
"""Fetch ALL open issues once (paginated), filter by labels in python."""
import subprocess
import json

all_issues = []
page = 1
while True:
    r = subprocess.run(
        ["gh", "api",
         f"repos/AAEmu/AAEmu/issues?state=open&per_page=100&page={page}",
         "--jq", ".[] | {number, title, labels: [.labels[].name]}"],
        capture_output=True, text=True)
    if r.returncode != 0 or not r.stdout.strip():
        break
    batch = []
    for line in r.stdout.splitlines():
        if line.strip():
            batch.append(json.loads(line))
    all_issues.extend(batch)
    if len(batch) < 100:
        break
    page += 1

# exclude PRs (they have pull_request key — but we didn't fetch it; filter by title prefix instead)
issues = [i for i in all_issues if not i["title"].startswith(("fix(", "feat(", "docs("))]

json.dump(issues, open("/tmp/issues.json", "w"))
from collections import Counter
print("total issues:", len(issues))
print(Counter(lbl for i in issues for lbl in i["labels"]).most_common(10))
