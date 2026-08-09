# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-09 14:45 PDT · by Nei
Branch of record: develop @ d1899128 · active work branch: feat/bot-appearance-factory @ 615c3719c

## Milestone state

**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v4 (permanent one-way upstream gate), community guidelines,
kanban template set (Nei), gate.sh verified, scorecard + 3 exploration
reports, graphify graph (17.6k nodes), shared skill aaemu-fork-workflow
enabled on all 4 profiles, LIVING-WORLD.md canon, ROADMAP.md locked-shape
2026-08-03 (date is canonical).

**M1 — Quest and progression spine: ✅ CLOSED**
Items 1-8 delivered; automated exit test GREEN — census headline
**153/153 runnable / 0 FAIL / 33 SKIP over 186 quests**; full gate
1148/1148. PROD DEPLOYED @ 94f498fc (2026-08-04, M1 engine-health
release — BUG-007/008/009/010/011/012 live). Deploy incident (39GB
container json.log) resolved; rotation fix shipped (t_264e1984 ✅).

**M6 — Deterministic playerbot framework: 🔶 presence-demo hotfix chain DONE — parity + soak open**
Presence demo (3 citizen bots embody + roam AT Josh's spawn, zone 179)
live via the hotfix3 deploy overlay. Hotfix chain on
feat/bot-appearance-factory: null-safe ForceDismount + inactivity-sweep
skip (1c1fdd721), null-safe VisualOptions (53c2baee5), restart-idempotent
provisioning (fa9037c3c), terrain-aware roam waypoints + above-home
probe + flat-arrival Z clamp (2ff6f19f3/8e4b2b6b0/a32ee64d2), env-driven
patrol-home override AAEMU_PRESENCE_HOME_X/Y/Z (c22575d9d), world-ready
poll widened to 300s for cold boot (96e45252a), race-appropriate
unit_model_params provisioning so bot bodies render (d0e5feb9d),
BotAppearanceFactory — randomized player-like looks + per-class starting
equipment (91b308d71, t_61814965). M6.6 player-parity requirements
landed in ROADMAP.md (74151e060). E2E harness committed (Scripts/e2e);
presence-demo compose overlay captured in-repo
(docker-compose.presence.yaml).

**E2E gates (GateSoakRunner, real Login+Game+MySQL, canonical data — evidence /root/aaemu-e2e/logs/):**
- **10-bot correctness: PASS** (2026-08-09) — tick invoke p95 0.014ms /
  max 0.20ms (limits 100/250), ActiveRegionTick worst 18ms / 0 overruns,
  DB writes 276.53 (limit 500), 0 physics/tick-overrun warnings.
- **25-bot stability: PASS** (2026-08-09) — H2 gate 1.00, tick invoke p95
  0.018ms / max 3.02ms, ActiveRegionTick worst 45ms / 0 overruns, DB
  writes 262.66 (limit 500), 0 warnings.

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | presence-demo image aaemu-game:presence-demo live via hotfix3 overlay; overlay now in-repo | ✅ deployed |
| Verifies | Rei | e2e gates 10-bot correctness + 25-bot stability PASS (2026-08-09) | ✅ done |
| Dispatches | Mai | presence-demo hotfix chain deployed (hotfix3 overlay) | ✅ done |
| Tracks | Nei | STATUS.md M6 presence-demo refresh; e2e harness + overlay committed | ✅ this commit |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| t_98415169 | M6.6 parity audit remainder: seed real skills + actabilities (bots 0/0 vs human 34) + bag supplies | hx-coder | ⏳ ready |
| — | In-client visual confirmation of distinct bot bodies (acceptance for any "bots visible" claim — ROADMAP 6.6) | Josh | ⏳ pending |
| — | M6 exit test: staged soak up to 10 bots × 6 hours (no-bot baseline + numeric budgets first) — not yet run | hx-coder | ⏳ pending |
| t_f198bb0e | M1-5d: harness extension — 14 unsupported act families (T3 SKIPs) | hx-coder | ⏳ ready |
| t_913c1d4a | verifier stale stub-registry false positives (CheckGuard/ItemGroup — the 3 WARNs) | hx-coder | ⏳ ready |
| t_bcf976ad | Wiki M0/M1 update — implement wiki-audit.md proposals | hx-researcher | blocked |
| — | feat/quest-scenario-harness (6e367585: T3 census + runnability.md + SCORECARD M1-5 entry) merge to develop | Tai | ⏳ no card yet |

## Legacy upstream item (predates one-way policy)

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)
- No new upstream branches or PRs are permitted; upstream is intake-only.

## Last scorecard update

- 2026-08-09 — this commit: STATUS.md M6 presence-demo refresh (M1 closed,
  hotfix chain + e2e gates 10-bot/25-bot PASS, M6.6 open items); e2e harness
  + presence overlay committed (06e6fcb4a, 615c3719c).
- 2026-08-04 — M1-5c closeout (t_cb64d872, 6e367585 on feat/quest-scenario-harness):
  SCORECARD.md quests-row runnability note 153/153 + M1-5 entry.

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
