# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-04 18:50 PDT · by Nei
Branch of record: develop @ 99e7c4ec · last upstream pull: 2026-08-03

## Milestone state

**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v3 (lane gate), community guidelines, kanban template set (Nei),
gate.sh verified, scorecard + 3 exploration reports, graphify graph (17.6k
nodes), shared skill aaemu-fork-workflow enabled on all 4 profiles,
LIVING-WORLD.md canon, ROADMAP.md locked-shape 2026-08-03 (version labels
retired 2026-08-04 — v1/v4/v6/v7 drift, the date is canonical).

**M1 — Quest and progression spine** (trimmed: shared engine fixes + golden
route). 5 of 6 work items done (see ROADMAP.md). Scenario-harness census
live: **T1 Solzreed golden zone 88 PASS / 9 FAIL / 0 SKIP; T2 fix families
22 PASS / 7 FAIL / 6 SKIP** (FAILs = harness-gap: step-suppression
sequencing + reward-stage KeyNotFoundException 'General'; tracked under
t_71ac7013 — runnability line NOT green until harness cards land).
BUG-006 kill-acceptor live on fork develop (prod deploy pending Josh);
BUG-007/008/009 gated (30c2b689); BUG-010 fix committed (581f0f17) + 3
defect fixes in the Rei gate queue (t_59a623c4: UnixTime, CheckSphere,
Ability seeds). Remaining: doodad phase/interaction objectives (quests
922/3889/3447) + census-FAIL triage. NOTE: tracked runnability.md still
shows the pre-BUG-010 run (86/11) — regenerates with the T3 census card
(t_cb64d872).

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | M1 batch merged (BUG-007/008/009, harness, Solzreed doc); defect fixes into Rei gate | ✅ merged — gate queue next |
| Verifies | Rei | gate queue t_59a623c4: BUG-010 UnixTime + CheckSphere + Ability seeds merge evidence | ⏳ todo — 3 branches |
| Dispatches | Mai | BUG-006 prod deploy coordination (aaemu box) | ⏳ waiting on Josh go-ahead |
| Tracks | Nei | STATUS.md + ROADMAP.md reconcile + M1 state refresh | ✅ done |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| t_59a623c4 | Rei gate: quest-defect fixes (BUG-010 + CheckSphere + Ability seeds) | Rei | ⏳ todo — merge to develop pending |
| t_bc9f131a | fix: QuestActCheckSphere 0xFF objective index (quest 1033) | hx-coder | 🏃 running |
| t_71ac7013 | harness: generator stage model v4 (selective parse + LetItDone) — gates runnability line | Tai → Rei | ⏳ ready |
| t_71e48494 | fix/quest-kill-acceptor — unstick kill-starter quests (BUG-006) | Tai → Rei | blocked — needs Josh: merge OK + prod deploy |

## Pending upstream PRs

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)

## Last scorecard update

- 2026-08-04 — BUG-010 closeout committed on fix/bug-010-unix-time (79b884c2):
  SCORECARD.md note FIXED/CLOSED (Rei attestation 2532) + census headline
  T1 88/97 / T2 22/35 — lands on develop via the Rei gate merge.
- 2026-08-04 — tracking reconcile: STATUS.md + ROADMAP.md M1 refresh + version
  labels retired (this commit).

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
