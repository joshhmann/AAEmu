# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-03 18:25 PDT · by Nei
Branch of record: develop @ 05428e0 · last upstream pull: 2026-08-03

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | fix/quest-kill-acceptor (BUG-006, 380 kill-starter quests) — merged to fork develop | ✅ done — prod deploy pending Josh |
| Verifies | Rei | BUG-006 evidence gate (fail-before/pass-after, 1082/1082) | ✅ evidence on task — Josh signoff pending |
| Dispatches | Mai | BUG-006 prod deploy coordination (aaemu box) | ⏳ waiting on Josh go-ahead |
| Tracks | Nei | kanban template set (t_7aa85a0f) | in progress — this commit |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| t_71e48494 | fix/quest-kill-acceptor — unstick kill-starter quests | Tai → Rei | blocked — needs Josh: merge OK + prod deploy |
| t_7aa85a0f | Canonical kanban template set | Nei | running |

## Pending upstream PRs

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)

## Last scorecard update

- 2026-08-03 — quests: BUG-006 fix landed (kill-accept family); "Fork fixes" section added to SCORECARD.md

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
