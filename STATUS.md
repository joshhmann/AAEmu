# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-10 03:35 PDT · by Nei
Branch of record: develop @ 6c8515611

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
(docker-compose.presence.yaml). GM bot commands deployed P0
(t_7b4f9423).

**M6.6 open items — RESOLVED 2026-08-10 (three-card verification sweep t_120bb6c9 / t_509ef8c2 / t_1ed9881f):**
- **Parity audit t_98415169: ✅ CLOSED** — PARITY_AUDIT.md delivered 08-08; CRITICAL (factory-in-lineage) + MODERATE (skills/actabilities/bag) gaps closed by fix/parity-seeding @ 45cd3f3a9 (t_747a1c44): live-verified 34 actabilities/bot + skills row + bag byte-identical to human Asssaa (t_120bb6c9); LOW residual gaps tracked in PARITY_AUDIT.md (template/ambiance routes).
- **In-client visual acceptance: ✅ PASS (wire-level) — rendered screenshots pending Josh's client** — real X2 protocol client session received unit-state for all 3 Citizen bots (17× 0x69 distinct objIds/names + 164× 0x6C, all walking, t_509ef8c2); Josh sighting ACCEPTED 08-09. No Windows client in lab → rendered screenshot confirmation awaits Josh. ⚠️ Defect found: adopt-heal force-stamps demo blob → looks collapse to 1 on reboot (t_555ed207; fix pushed fix/adopt-heal-keeps-factory-look @ cdf6d4a62, awaiting Rei gate; prod needs re-provision after merge).
- **6h/10-bot soak: ⚠️ FAIL (numeric budget) — operational criteria all PASS** — full 6h window completed (attempt 3): 10/10 bots connected, 0 crash, 0 disconnect, RSS flat 3418-3453MB, tick p95 0.02ms, DB writes 262/500 — but physics slow-thread warnings 0.03/min vs 0 limit (11 transient single-frame WARNs, first = boot spike 459ms 21:25:18 PDT). Regression card t_eecc5604 filed: RCA or budget recalibration (precedent t_2006451f). Evidence: soak-report-20260810.md + gate-10-soak-20260810-102503.md (attached t_1ed9881f). Caveat: PlayerBotScheduler NOT enabled this run — scheduler-driven soak still required if M6 exit mandates it.

**E2E gates (GateSoakRunner, real Login+Game+MySQL, canonical data — evidence /root/aaemu-e2e/logs/):**
- **10-bot correctness: PASS** (2026-08-09) — tick invoke p95 0.014ms /
  max 0.20ms (limits 100/250), ActiveRegionTick worst 18ms / 0 overruns,
  DB writes 276.53 (limit 500), 0 physics/tick-overrun warnings.
- **25-bot stability: PASS** (2026-08-09) — H2 gate 1.00, tick invoke p95
  0.018ms / max 3.02ms, ActiveRegionTick worst 45ms / 0 overruns, DB
  writes 262.66 (limit 500), 0 warnings.
- **6h/10-bot soak: ⚠️ FAIL (physics budget) — operational PASS** (2026-08-10,
  t_1ed9881f) — 10/10 connected 6h, 0 crash/disconnect, RSS flat, tick p95
  0.02ms; 11 transient physics WARNs (0.03/min vs 0) → t_eecc5604.

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | presence-demo image aaemu-game:presence-demo live via hotfix3 overlay; overlay now in-repo | ✅ deployed |
| Verifies | Rei | e2e gates 10-bot correctness + 25-bot stability PASS (2026-08-09) | ✅ done |
| Dispatches | Mai | presence-demo hotfix chain deployed (hotfix3 overlay) | ✅ done |
| Tracks | Nei | STATUS.md M6.6 closeout 08-10 — parity audit CLOSED, in-client wire PASS, soak operational PASS / budget FAIL (t_eecc5604) | ✅ this commit |

## Open tasks (kanban, AAEmu lane)

| ID | Title | Lane | Status |
|----|-------|------|--------|
| — | In-client bot sighting | Josh / Rei | ✅ **ACCEPTED** 08-09 — Josh saw bots; Rei wire-confirmed 3 distinct bodies (t_509ef8c2); rendered screenshots pending Josh's client |
| t_1ed9881f | M6 exit test: 6h/10-bot soak (numeric budgets) | hx-coder | ✅ done 08-10 — full 6h, 10/10, 0 crash/dc, RSS flat; **budget FAIL** on physics warnings (11 transient) → t_eecc5604 |
| t_eecc5604 | M6 regression: physics slow-thread warnings 0.03/min vs 0 (11x in 6h soak) | tai | 🔶 running — RCA or budget recalibration (precedent t_2006451f) |
| t_555ed207 | Fix: adopt-heal force-stamps demo blob — looks collapse to 1 on reboot | tai | 🔶 blocked — fix pushed cdf6d4a62, awaiting Rei gate |
| t_f198bb0e | M1-5d: harness extension — 14 unsupported act families (T3 SKIPs) | hx-coder | ⏳ ready |
| t_913c1d4a | verifier stale stub-registry false positives (CheckGuard/ItemGroup — the 3 WARNs) | hx-coder | ⏳ ready |
| t_bcf976ad | Wiki M0/M1 update — implement wiki-audit.md proposals | hx-researcher | blocked |
| — | feat/quest-scenario-harness (6e367585: T3 census + runnability.md + SCORECARD M1-5 entry) merge to develop | Tai | ⏳ no card yet |

## Legacy upstream item (predates one-way policy)

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)
- No new upstream branches or PRs are permitted; upstream is intake-only.

## Last scorecard update

- 2026-08-10 — this commit: STATUS.md M6.6 closeout — parity audit
  t_98415169 CLOSED (seeding gaps live-verified 45cd3f3a9), in-client
  wire-level PASS (t_509ef8c2) with appearance defect t_555ed207 pending
  Rei gate, 6h/10-bot soak operational PASS but harness FAIL on physics
  budget (t_1ed9881f → regression t_eecc5604).
- 2026-08-09 — this commit: progression-board.md refresh (M1 CLOSED,
  M2b-E2E DONE, M2c kill-acceptor + ZoneKill landed, M6 hotfix chain done
  + 6h soak running) and STATUS.md drift fix (parity audit t_98415169
  done, in-client sighting ACCEPTED, soak running t_1ed9881f).
- 2026-08-09 — earlier: STATUS.md M6 presence-demo refresh (M1 closed,
  hotfix chain + e2e gates 10-bot/25-bot PASS, M6.6 open items); e2e
  harness + presence overlay committed (06e6fcb4a, 615c3719c).
- 2026-08-04 — M1-5c closeout (t_cb64d872, 6e367585 on feat/quest-scenario-harness):
  SCORECARD.md quests-row runnability note 153/153 + M1-5 entry.

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
