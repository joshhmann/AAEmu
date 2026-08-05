# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-04 18:30 PDT · by Nei
Branch of record: develop @ 99e7c4ec · last upstream pull: 2026-08-03

## Milestone state

**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v3 (lane gate), community guidelines, kanban template set (Nei),
gate.sh verified, scorecard + 3 exploration reports, graphify graph (17.6k
nodes), shared skill aaemu-fork-workflow enabled on all 4 profiles,
LIVING-WORLD.md canon, ROADMAP.md v7 (locked shape, Codex-reviewed).

**M1 — Quest and progression spine** (trimmed: shared engine fixes + golden
route). Quest-runnability census live: **T1 88/97 PASS, T2 22/35 PASS**.
BUG-010 (UnixTime) FIXED/CLOSED; remaining census FAILs are harness-gap,
tracked under t_71ac7013 — runnability line NOT green until those land.

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | BUG-010 fix (fix/bug-010-unix-time) — 581f0f17, gate 1129/1129 | ✅ done — closeout committed by Nei |
| Verifies | Rei | BUG-010 evidence gate (attestation comment 2532, PASS) | ✅ signed off |
| Dispatches | Mai | BUG-006 prod deploy coordination (aaemu box) | ⏳ waiting on Josh go-ahead |
| Tracks | Nei | BUG-010 closeout (STATUS.md + scorecard) | ✅ done |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| t_71ac7013 | harness: generator stage model v4 (selective parse + LetItDone) — 1897 SUPPLY/PROGRESS/REWARD etc. | Tai → Rei | ⏳ ready — gates the runnability line |
| t_bc9f131a | fix: QuestActCheckSphere 0xFF objective index (quest 1033) | hx-coder | 🏃 running |
| t_71e48494 | fix/quest-kill-acceptor — unstick kill-starter quests (BUG-006) | Tai → Rei | blocked — needs Josh: merge OK + prod deploy |

## Pending upstream PRs

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)

## Last scorecard update

- 2026-08-04 — BUG-010 FIXED/CLOSED (attestation 2532): SCORECARD.md note
  updated with census headline T1 88/97 / T2 22/35 PASS; harness-gap FAILs
  remain open under t_71ac7013. Golden-Route-Solzreed.md (M1-6 wiki doc)
  committed on develop 99e7c4ec by Tai — kept/tracked, nothing stray.

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
