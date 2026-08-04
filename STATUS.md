# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-03 21:30 PDT · by Tai
Branch of record: develop @ c9c3880 · last upstream pull: 2026-08-03

## Milestone state

**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v3 (lane gate), community guidelines, kanban template set (Nei),
gate.sh verified, scorecard + 3 exploration reports, graphify graph (17.6k
nodes), shared skill aaemu-fork-workflow enabled on all 4 profiles,
LIVING-WORLD.md canon, ROADMAP.md v7 (locked shape, Codex-reviewed).

**Next: M1 — Quest and progression spine** (trimmed: shared engine fixes +
golden route). First task: reconcile parked BUG-006 (merge decision).

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | fix/quest-kill-acceptor (BUG-006, 380 kill-starter quests) — merged to fork develop | ✅ done — prod deploy pending Josh |
| Verifies | Rei | BUG-006 evidence gate (fail-before/pass-after, 1082/1082) | ✅ evidence on task — Josh signoff pending |
| Dispatches | Mai | BUG-006 prod deploy coordination (aaemu box) | ⏳ waiting on Josh go-ahead |
| Tracks | Nei | M0 closeout + M1 card assembly | ✅ M0 closed |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| t_71e48494 | fix/quest-kill-acceptor — unstick kill-starter quests | Tai → Rei | blocked — needs Josh: merge OK + prod deploy |

## Pending upstream PRs

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)

## Last scorecard update

- 2026-08-03 — quests: BUG-006 fix landed (kill-accept family); "Fork fixes" section added to SCORECARD.md

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
