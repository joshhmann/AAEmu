# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-04 20:35 PDT · by Nei
Branch of record: develop @ d1899128 · last upstream pull: 2026-08-03

## Milestone state

**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v4 (permanent one-way upstream gate), community guidelines,
kanban template set (Nei),
gate.sh verified, scorecard + 3 exploration reports, graphify graph (17.6k
nodes), shared skill aaemu-fork-workflow enabled on all 4 profiles,
LIVING-WORLD.md canon, ROADMAP.md locked-shape 2026-08-03 (version labels
retired 2026-08-04 — v1/v4/v6/v7 drift, the date is canonical).

**M1 — Quest and progression spine: ✅ delivered — Josh playtest in progress**
Items 1-8 delivered: shared engine defects fixed, golden route curated
(route doc live on wiki — Docs/wiki/Golden-Route-Solzreed.md), doodad
phase/interaction family resolved (T1 Solzreed 97/97). Automated exit test
GREEN — census headline **153/153 runnable / 0 FAIL / 33 SKIP over 186
quests** (T1 Solzreed 97/97; T2 29/29 + 6 SKIP; T3 27/27 + 27 SKIP); full
gate 1148/1148. Human playtest = Josh in progress (milestone decision
pending). PROD DEPLOYED @ 94f498fc (2026-08-04 20:30, M1 engine-health
release — BUG-007/008/009/010/011/012 live); verifier first live census
5 ERR / 128 WARN / 4 INFO over 4775 quests — data-fix backlog seeded, 3
WARNs are verifier stale-registry false positives → fix card t_913c1d4a.
Deploy incident: 39GB container json.log (100% disk) pre-deploy —
truncated; rotation fix shipped (t_264e1984 ✅).

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | M1 engine-health release deployed @ 94f498fc; 5c census (6e367585) on feat/quest-scenario-harness | ✅ deployed — 5c branch merge to develop next |
| Verifies | Rei | gate queue t_59a623c4 closed — BUG-010/011/012 merged to develop | ✅ done |
| Dispatches | Mai | deploy t_034305b3 done @ 94f498fc; log-rotation fix t_264e1984 done | ✅ done |
| Tracks | Nei | STATUS.md M1-5c closeout refresh | ✅ done |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| t_f198bb0e | M1-5d: harness extension — 14 unsupported act families (T3 SKIPs) | hx-coder | ⏳ ready |
| t_913c1d4a | verifier stale stub-registry false positives (CheckGuard/ItemGroup — the 3 WARNs) | hx-coder | ⏳ ready |
| t_bcf976ad | Wiki M0/M1 update — implement wiki-audit.md proposals | hx-researcher | blocked |
| — | feat/quest-scenario-harness (6e367585: T3 census + runnability.md + SCORECARD M1-5 entry) merge to develop | Tai | ⏳ no card yet |

## Legacy upstream item (predates one-way policy)

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)
- No new upstream branches or PRs are permitted; upstream is intake-only.

## Last scorecard update

- 2026-08-04 — M1-5c closeout (t_cb64d872, 6e367585 on feat/quest-scenario-harness):
  SCORECARD.md quests-row runnability note 153/153 + M1-5 entry; BUG-010
  census line GREEN — lands on develop with the harness branch merge.
- 2026-08-04 — this commit: STATUS.md M1-5c headline + engine-health deploy
  state refresh (94f498fc live, verifier first census, deploy incident).

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
