# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-09-05 · A5 b2 prelaunch correction (docs-only); develop `ca7762d7d` / source `322390b32`
Branch of record: develop ca7762d7d (roadmap docs merge HEAD; runtime source/test 322390b32)
(combat bonus-snapshot + aggro-table kill races; prior `9ad5735b2`
bot-wildlife crash cluster). M5's
`BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` bounded decision
primitive remains integrated in `LevelingLoop`'s accept choice at
`263ecc66c474ca1c5f4b085e86ef3e47f49fd1`; focused contract 5/5, scoped quest
consumer only, and broad M5 policy/universal autonomy remain open. M6 includes
`950cfd279` cancellation, `c97909f4f` population isolation, and opt-in
six-hour leg `155c82c66` integrated here.
Current honest state (2026-09-05): deployed `135c4f14e` (source `322390b32`) healthy
per director session report (not freshly queried); full gate 2836/1/1 NOT green (1 PvP-honor flake);
soak #2 PASS (`g2-a5-tier3-sixhour-report.json` 2026-09-05T15:33:11Z, FULL 360.00008-min window,
`passed: true`, zero breaches — tested binary exactly `322390b32` source, report stamp `ca7762d7d`
is docs-only); A5 OPEN with FINAL = (shape) + (quiescence-budget) +
(actual timer progression, planned) — H is separate and NOT an A5 criterion; H states unchanged
(DEFERRED stays deferred, UNKNOWN stays unknown).

**Hierarchy note:** Current work is under **Post-M7 readiness and closure**, an
umbrella scope rather than a new numbered milestone. PB-001/PB-002/PB-005/PB-007
are capability/blocker tracks; A3/A4/A5 are population/scaling acceptance
gates; slices sit inside those tracks or gates; H is deferred human/client
acceptance. M0–M7 are the landed foundation/product milestones. The roadmap
formally defines a future **M8 — Living Village**; readiness labels are not
renumbered as M8. See the authoritative [scope map](PROJECT-CONTROL.md#scope-map).

## 2026-09-05 — A5 b2 prelaunch correction record (no runtime proof, A5 stays OPEN)

- **Correction:** pushed `948bf9662` (b2 helper build + full UnitTests gate)
  did NOT prove runtime; prior "canaries mature ~67 min at GrowthRate 3600"
  was wrong 1000x (14.4M ms / 3600 ≈ 4 s, not ~67 min).
- **Correction committed as `a88f4df20` on `948bf9662`, stack-free verification only (no
  commit/push, no runtime):** explicit per-isolated-run `E2E_GROWTH_RATE`
  (default stays 3600); 6h-canary rate 3 → ~80 min post-plant, due checked
  60–120 min INTO window; restart rate 120 → ~2 min; actual wither is the
  `DoodadFuncTimer` delay, not GrowthRate-divided; stack-aware seed IDs,
  owned canary discovery, in-window transfer observations, restart
  validation. Pre-fix RED unit regressions for sizing + restart false-passes;
  post-fix IntegrationTests Release build 0 errors + 25 exact-method pure
  facts pass (23 b2 validators + 2 RSS); full UnitTests gate 2844 total /
  2843 passed / 0 failed / 1 skipped, compiler 0/0, MCP 39+24 ran before the
  final IntegrationTests-only cleanup.
- **Runtime NOT RUN:** isolated real planting 2-from-stack, bounded restart,
  6h asserted soak; shape re-show also open; b1 historical pass unchanged;
  no live deployment / human-feel claim. **A5 OPEN.**

## 2026-09-05 — A5 soak #2 PASS: 6h quiescence-budget leg closed (docs-only record)

- **Fresh report `g2-a5-tier3-sixhour-report.json` PASS:** probe G2-A5 Tier-3 natural
  dormant-timer soak, runAtUtc 2026-09-05T15:33:11Z, config 1000 dormant / 360 min / 60 s,
  window 360.00008 FULL (`windowCompleted` true, `windowStatus` FULL), warmup `A5_WARMUP_READY`
  09:33:11Z (134.8 s, baseline 5635.5 MB), RSS growth 6.7 MB (budget 512), DB writes 0
  (114670→114670), SaveP95 2.25 ms / SaveMax 61 ms / 0 skips, sampleCount 361, `failures: []`,
  `passed: true`, leg RUN. Test binary duration 6h04m34s, 1/1. Report:
  `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json`.
- **Code identity (honesty note):** report stamps `ca7762d7d` (roadmap docs merge) because
  SourceRevision is read at report time; `git diff --stat 322390b32 ca7762d7d` = 7 markdown-only
  files (EVIDENCE-LEDGER, ROADMAP, SCORECARD, STATUS, navigation-domain,
  playerbot-capability-matrix, progression-board), zero code — tested binary is exactly
  `322390b32` source. Launch header HEAD was `322390b32`.
- **Roadmap meaning:** closes (b1) 6h quiescence-budget leg with a zero-breach post-change run.
  Still open: (a) SHAPE re-shown at the fixed tip (last measured 2026-08-26 pre-change), (b2)
  timer-progression assertion (A5-W2 unbuilt). **A5 stays OPEN.** "Preferably 12-hour" remains
  recommendation-only. H separate, unchanged.
- **Soak #1** (+72 min external-kill, cause UNKNOWN) stays recorded as partial evidence, not a pass.

## 2026-09-05 — Corrections (docs-only; dated history below preserved, current mirrors corrected)

- **Deploy receipt (director session report, not freshly queried):** .165 presence-demo is deployed at
  `135c4f14e` (source `322390b32`; HEAD itself is docs-only), observed healthy with 250-bot
  config/provisioning, latest 10-min window 0 errors / 0 boundary violations / 0 fast-move alerts.
- **Full gate NOT green:** `322390b32` gate = 2836 pass / 1 fail / 1 skip. The 1 FAIL is the known
  load-dependent PvP honor flake; isolated honor 11/11 is determinism evidence only, NOT proof the
  flake is unrelated. No green claim.
- **Test-runner ticks are not the verdict:** steady `[+0/x0/?0]` lines in soak logs are harness
  progress counters, not breach evidence. The verdict lives ONLY in
  `g2-a5-tier3-sixhour-report.json` (lowercase `failures` / `passed`).
- **Soak #1 cause UNKNOWN:** vanished at +72 min (ticks to 1h12m). The "EXTERNAL kill /
  SIGKILL-class teardown ~06:33 UTC, killer unknown" line below is an UNPROVED hypothesis (no exit
  markers/OOM/disk noted); historic OOM is NOT reliably excludable. Partial evidence only, not a pass.
- **Soak #2 clock (formula, never a promised time):** the 6h window is measured from the post-warmup
  baseline — `RunDormantTimerSoakAsync` starts its Stopwatch only after `WaitForRssQuiescenceAsync`
  (`AAEmu.IntegrationTests/E2e/Gate/A5Tier3AcceptanceProbeTests.cs`, report comment "window is
  measured from the post-warmup baseline"). Log header start 09:28:34Z includes
  build/boot/seed/quiescence; the `A5_WARMUP_READY` marker 09:33:11Z means the window likely starts
  there → ETA ≈ warmup-ready + 6h (≈15:33Z+ finalize). SUPERSEDES the fixed "ETA ~15:28" below. No
  pass claim until the fresh report lands with `passed=true`.
- **A5 FINAL triad (binding; H separate, never an A5 criterion):** FINAL = (a) SHAPE: 1,000
  registered / ≤50 embodied, RSS within 15% of the 50-only baseline, wake-to-visible p95 < 3 s
  (SHAPE MEASURED 2026-08-26) + (b) QUIESCENCE-BUDGET leg over 6h: per-sample runtime-metrics
  counters within budget with NOBODY embodied (soak #2 is this leg's candidate) + (c) ACTUAL TIMER
  PROGRESSION: harvest/travel timers advance over the 6h — PLANNED, no probe assertion yet, so a
  zero-breach `passed=true` alone proves quiescence, never progression. A5 remains OPEN until all
  three hold. "Preferably 12-hour" is a recommendation-only follow-up, never the exit gate: exit =
  near-term MET + shape MEASURED + 6h legs PENDING. H states are recorded per milestone (DEFERRED
  stays deferred, UNKNOWN stays unknown) — H is never flipped by soak evidence.
- **Spline:** `5fdb7a385` (spline/corner-blending) is branch-only (`/root/aaemu-splinework`), NOT
  merged; receipts are unit-only. Corner-geometry smoothing (lateral deviation / heading rate /
  broadcast slew) is UNPROVEN — no navigation proof claim. Acceptance gate (clauses A1–A4) is
  reviewed in `scorecard-explorations/mechanics/navigation-domain.md`.
- **Wildlife prerequisites (canonical, already in engine):** NPC loot via `LootingContainer.GenerateLoot`
  (atomic under ItemsLock) + the `Unit.DoDie` loot path; livestock doodad butcher (`DoodadFuncButcher`,
  `Butcher` interaction, canonical cow chain). A missing NPC-corpse→butcher link is NOT a canonical
  requirement — it is not specced as a gap.
- **Loot semantics (current source, not guaranteed-already):** `GameplayActor.Loot`
  (`AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:1277-1305`) may Complete with granted 0
  (`$"nothing to loot … (empty or already looted)"`, :1303-1305) — granted counts the CONTAINER
  delta (`before - container.Items.Count`, :1294-1302, engine removes each granted entry via
  `TryReserveLootItem`), never the per-caller bag. Specs REQUIRE per-caller bag/money deltas
  contrasted with the container delta (caller grant vs no-op `Completed(0)` vs concurrent foreign
  take) — never container deltas alone, never already-guaranteed outcomes.
- **B5 scope (INDEX already DONE, not missing):** scenario INDEX landed 2026-08-26 (`46fe4332d`,
  `scorecard-explorations/generated/b5-scenario-library-2026-08-26.md`); C1 schedules (`62f13fdc7`)
  and C2 social (`8c198f13d`) DONE by historic evidence. The NEW B5 contract is runner evidence
  reliability only (set -u coverage, pipefail absence, hardcoded E2E root, scenario-selection
  collision) — no code fixes, no respec of landed items.
- **Roadmap/spec links (2026-09-05, docs-only, revised):**
  [roadmap zoom-out](ROADMAP.md) (`## Post-M7 readiness — roadmap zoom-out` — mandatory acceptance,
  queue table + Q1–Q8, correction register, A5 triad reconciliation, M8 contracts, Historical note) ·
  [corner-blending acceptance gate](scorecard-explorations/mechanics/navigation-domain.md)
  (`## Addendum — 2026-09-05 corner-blending acceptance gate`, clauses A1–A4, branch NOT merged).

## 2026-09-05 — Closeout: loot-test gap closed, combat kill-races fixed, soak #1 externally killed, soak #2 rerun on `322390b32` (docs-only)

- **Loot-race test gap closed — `7c0772f12 test(combat): loot GenerateLoot concurrent-generation regression`** (deterministic AlwaysDrop pack, 40x16-thread hammer, mutation-checked fail-pre/pass-post). Pushed to origin.
- **Testing deploy:** .165 presence-demo rebuilt on `7c0772f12`, rollback tag `rollback-pre-7c0772f12` kept, healthy, 5-min baseline 0/0/0, 250 bots roaming.
- **Live triage new findings:** Effect 15109/skill 16210 IndexOutOfRange (GetBonuses snapshot-copy race) + InvalidOperationException in ClearAggroOfUnit via Npc.DoDie (aggro-table race), Effects 15109/1134.
- **Combat kill-races fixed — `322390b32 fix(combat): close bonus-snapshot and aggro-table kill races`** (+360/-84: BonusesLock whole-body incl. UpdateGearBonuses slot reset, static AggroLock after per-unit proved insufficient, BuffToleranceTests hammer + new NpcAggroRaceTests; public API stable; lock-ordering audited no-nesting). Full gate 2836 pass/1 fail/1 skip (sole fail = known load-dependent PvP honor flake, 11/11 isolated green), MCP smokes 39 + 24 pass. Pushed to origin.
- **Soak #1 died at +72min by EXTERNAL kill** (zero-failure ticks to the end, healthy game ticks, no exit markers/OOM/disk; SIGKILL-class session teardown ~06:33 UTC) — partial evidence only, not a pass; killer unknown.
- **Soak #2 rerun ON `322390b32`:** orphan `aaemu_a5_t3_sixhour-db-1` cleared, stale Aug-29 report renamed `.bak-pre322390b32`, launched detached HUP-proof (session leader, PPID=1), log `soak-run-20260905-022834.log`, early ticks [+0/x0/?0] advancing, ETA ~15:28 UTC 2026-09-05. Calibration/pb007/dev-DB/.165 lanes untouched.
- **A5 stays OPEN:** no zero-breach post-change run yet; soak #2 is the candidate. No soak-pass claim until the fresh report lands with `passed=true`.

## 2026-09-05 — Docs/correlation: HEAD `9ad5735b2` record, live triage, A5 calibration correlation, 6h soak in-flight (read-only)

- **Source/test HEAD now `9ad5735b2`** (was `f5e7a1980`): four commits landed 2026-09-04 —
  `da06470ab` SusManager reset on GM-bot teleports (suppresses false `moving a bit fast` alerts);
  `16112c24c` skill plots run via new `ExecutionBoundary.RunUnscoped` (AsyncLocal bot-step scope leaked onto plot threads, tripping the M5 boundary write assertion live: thread 47 vs boundary 24);
  `a38484f9e` trapezoidal speed profile for actor Move legs (ramp at MoveAcceleration $12\text{m/s}^2$, brake along $v = \sqrt{2ad}$ at MoveDeceleration $14\text{m/s}^2$);
  `9ad5735b2` bot-wildlife crash cluster (Buffs tolerance dup key, LootingContainer loot race now atomic under ItemsLock, plot-thread bonus torn reads).
- **Live triage 2026-09-05 ~05:36 UTC** (`docker logs aaemu-game-1 --since 2h` on 192.168.0.165): **7 total** =
  7 `threw on target` + 0 `EXECUTION BOUNDARY VIOLATION` + 0 `moving a bit fast`. All 7 are
  `Effect 15109 (DamageEffect)` of skill 16210 `IndexOutOfRangeException` on wildlife targets
  (04:55–05:19 UTC). Game container restarted ~46 min prior (`aaemu-game-1` Up 46 minutes, healthy);
  siblings healthy (`aaemu_a5_t3_sixhour-db-1` Up 15h, login Up 14h, db Up 9d, adminer Up 9d).
- **A5 calibration correlation** (`/tmp/opencode/a5_corr.py`): 10 `Physics thread is running slow`
  warnings vs **214,900** host-telemetry samples (2026-09-02 → 2026-09-05). At-warning windows show
  stealPct 0.00, psiCpuFull10 0.00, cgroupThrottledUs 0.00, psiMemSome10 0.00 — zero host contention
  ⇒ in-process pause/GC suspect, not host steal. Calibration lane PID 2814356 healthy (elapsed ~68h).
- **A5 Tier-3 6h soak IN FLIGHT — not passed**: `Probe_A5Tier3DormantTimers_SixHour` running
  (PIDs 3589460/3590814; log header `start: 2026-09-05T05:21:27Z` = 22:21 PDT 2026-09-04, git HEAD
  `9ad5735b2`, `E2E_REBUILD=1`; ETA ~11:21 UTC 2026-09-05); log `soak-run-20260904-222127.log`
  (filename in PDT) shows steady `[+0/x0/?0]` ticks. Report
  `g2-a5-tier3-sixhour-report.json` is still the stale 2026-08-30 run (commit `46129ae`,
  `passed=false` on timing only). No soak-pass claim until the fresh report lands with `passed=true`.

## 2026-09-03 — Post-M7 readiness: PB-005 Clean 733 Duplicate NPC Spawns in World Data

- **Cleaned 733 Duplicate NPC Spawns (`main_world/npc_spawns.json`):** Excised all 733 exact duplicate spawn records from the canonical world data file (`AAEmu.Game/Data/Worlds/main_world/npc_spawns.json`), reducing active spawn rows from 25,118 to 24,385.
  - **Preserved File Integrity:** Retained all 8,261 inline Korean/English template name comments (e.g. `// Gorgon`, `// Royal Falcon`), disabled commented blocks, and standard 4-space JSON formatting.
  - **Deduplication Key:** Resolved duplicates by `(UnitId, Position.X, Position.Y, Position.Z)` at centimeter precision ($0.01\text{m}$).
- **Evidence:** Script compilation check passed with 0 errors and 0 warnings. Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, **2,778 total tests (2,777 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools.

## 2026-09-03 — Post-M7 readiness: PB-002 Dewstone Plains Early Quest Chain & Adaptive Perception Expansion

- **Dewstone Plains Early Quest Chain Expansion:** Extended the leveling bot progression beyond starting zone boundaries into canonical early Dewstone Plains content (Lilyut Crossing / Royster's Ford / Afindelle camp).
  - **Adaptive Perception Band (`AdaptiveBand = true`):** Automatically computes dynamic level bounds (`[effectiveBandMin..effectiveBandMax]`) for characters level $\ge 10$, allowing them to perceive and pursue quests across zone transition bands without hardcoding narrow level ranges.
  - **Canonical Seed Constants:** Added standard template IDs for Dewstone early progression in `LevelingLoopScenario`: Afindelle (NPC 673), Lord Royster (NPC 680), Constable Brann (NPC 679), Medd (NPC 5849), canonical quests 44 (Wounded Afindelle), 328 (Royster's Danger), 48 (Bandit Hunt), 55 (Crisis Delivery), and herbs doodad/item templates (2796 / 5264).
  - **Data-Driven & Fallback Gather Resolution (`GatherLeg`):** Expanded `GatherLeg` with fallback doodad discovery when `HighlightDoodadId == 0`, inspecting perceived doodad function chains and loot packs to resolve gather objectives data-driven.
- **Evidence:** `LevelingLoopScenarioRigTests` **38/38** green (+1 test). Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, **2,774 total tests (2,773 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools.

## 2026-09-03 — Post-M7 readiness: PB-MOUNT Autonomous Mount Riding on Arterial Highways & Travel Mobility

- **Autonomous Mount Management & Travel Mobility (`BotMountManager`):** Implemented automated mount summoning, mounting, high-speed travel (~10.5 m/s vs 5.4 m/s foot travel), and dismounting for combat/interaction.
  - **Mount Lifecycle (`EnsureMounted`, `EnsureDismounted`, `IsMounted`):** Spawns and links character steed/snowlion companion, sets world transform/instance backing without invoking headless-incompatible resolvers, and boards rider via `actor.Mount` / `MateManager.MountMate`.
  - **Engine Movement Synchronization (`GameplayActor.ApplyCharacterMove`):** Detects active mounted companion and redirects character movement commands to the mount (`VehicleMovementModel.ApplyUnitMove(Character, mate, ...)`), bypassing engine rider client-ignore rules while keeping the transform hierarchy synchronized.
  - **Highway Transit Integration:** Wired into `LevelingLoopScenario.TryTransitionToNextZone` so bots automatically mount up during long-distance inter-zone transit (Solzreed -> Dewstone -> Marianople) and dismount upon reaching the destination quest hub.
- **Evidence:** `BotMountManagerTests` **4/4** green; `LevelingLoopScenarioRigTests` **37/37** green. Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, **2,773 total tests (2,772 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools.

## 2026-09-03 — Post-M7 readiness: PB-BAG Autonomous Bag Management, Vendoring & Durability Repair

- **Autonomous Bag Management & Gear Maintenance (`BotBagManager`):** Implemented automated inventory capacity auditing, vendor junk classification, and equipment durability maintenance.
  - **Inventory Auditing (`AuditBag`):** Tracks total capacity, free slots, used slots, fullness percent, damaged equipment count, and estimated junk salvage revenue. Detects near-full bags ($\le 2$ slots).
  - **Strict Asset Protection (`IsTrash`):** Guarantees quest items (`Quest_Item`, quest equipment, `LootQuestId`), active weapons/armor, and essential sustain items (potions, food, water) are never sold. Filters for `Trash_*` categories (35, 98, 101, 102, 103, 104, 105) and common refundables.
  - **Autonomous Vendoring (`SellAllTrash`):** Pumps real `actor.Sell` transactions against merchant NPCs, reclaiming inventory slots and converting mob loot into copper.
  - **First-Class Equipment Repair (`GameplayActor.Repair` & `BotBagManager.RepairAllEquipment`):** Exposed `ActorActionType.Repair` on `IGameplayActor` and implemented canonical `Character.DoRepair` interaction with blacksmith/merchant NPCs, restoring weapons and armor to `MaxDurability`. Added null safety to `Character.DoRepair` packet dispatch and `ItemManager._config` lookups.
  - **Leveling Loop Integration:** Automatically triggers maintenance on quest turn-ins at settlement hubs in `LevelingLoopScenario`.
- **Evidence:** `BotBagManagerTests` **4/4** green; `LevelingLoopScenarioRigTests` **37/37** green. Full `./scripts/gate.sh` passed with 0 compiler errors/warnings, **2,769 total tests (2,768 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools.

## 2026-09-03 — Post-M7 readiness: PB-COMBAT Tactical Combat Decision Tree & Class Ability Combos

- **Tactical Combat Decision Tree (`CombatDecisionTree`):** Implemented deterministic decision tree for playerbot combat. Evaluates health status, class roles (`CombatRole.Melee`, `RangedPhysical`, `RangedMagic`, `HealerSupport`), tactical spacing, and class-specific combo rotations.
  - **Role Inference (`InferRole`):** Derives primary tactical style from starting specialization `character.Ability1` (`Wild` -> Ranged physical, `Magic`/`Death`/`Illusion` -> Caster, `Love`/`Romance` -> Support, others -> Melee).
  - **Class-Specific Combo Rotations (`SelectPrioritizedSkill`):**
    - **Battlerage (`Fight`)**: Charge (11918) [snare] -> Triple Slash (18131) [trip on snared] -> Whirlwind Slash (13282) [bonus damage on tripped].
    - **Sorcery (`Magic`)**: Flamebolt (10752) [inflicts Burn] -> Freezing Arrow (10667) [43% bonus on Burn + Freeze] -> Chain Lightning (11967).
    - **Archery (`Wild`)**: Charged Bolt (16210) [inflicts Slow] -> Endless Arrows (14835) [bonus vs Slowed].
    - **Vitalism (`Love`)**: Resurgence (10547) [HoT buff when HP < 70%] -> Antithesis (10534) [damage/heal].
    - **Occultism (`Death`)**: Hell Spear (10135) [impale] -> Mana Stars (12759).
  - **Emergency Survival Flee (`EmergencyFlee`):** When HP drops below critical safety threshold ($\le 20\%$), disengages combat and sprints away from hostiles to prevent death.
  - **Tactical Kiting & Spacing (`KiteSpacing`):** Ranged archers and magic casters dynamically backpedal 10m when hostiles penetrate melee range ($< 12\text{m}$), maintaining the optimal $12\text{–}22\text{m}$ damage band.
  - **Melee Gap Closing (`CloseGap`):** Melee bots navigate to target reach before casting close-quarters skill rotations.
- **Scenario Integration:** Wired into `LevelingLoopScenario.HuntLeg`, `LevelLeg`, and `AbilityLevelLeg`.
- **Evidence:** `CombatDecisionTreeTests` **9/9** green (+4 combo tests); `LevelingLoopScenarioRigTests` **38/38** green. Full `./scripts/gate.sh` passed with 0 compiler errors/warnings, **2,778 total tests (2,777 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools.

## 2026-09-03 — Post-M7 readiness: PB-002 Autonomous Inter-Zone Progression & Nui Shrine Death Recovery Loop

- **Autonomous Inter-Zone Leveling Progression (`TryTransitionToNextZone`):** When bots exhaust all available quest offerings in their current starting zone, they evaluate their level against zone transition gates. Level $\ge 10$ in Solzreed transitions along the arterial highway to Dewstone Plains (Lilyut Crossing hub) to trigger fresh quest discovery. Level $\ge 20$ in Dewstone transitions to Marianople Capital Gate.
- **Autonomous Death Recovery (`HandleDeathRecovery`):** When a bot dies in combat or during leveling loops, it enters death recovery, resurrects via the real `CharacterResurrection` engine path at the nearest Nui goddess shrine, relocates to the shrine anchor, and recovers HP/MP to safe operating threshold ($\ge 70\%$) before resuming quest pursuit.
- **Evidence:** `LevelingLoopScenarioRigTests` **37/37** passed (+2 tests: `LevelingLoop_DeathRecovery_ResurrectsAtNuiAndRecoversHealth` and `LevelingLoop_InterZoneTravel_TransitionsToNextZoneHighway`). Full `./scripts/gate.sh` passed with 0 compiler errors/warnings, **2,760 total tests (2,759 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools.

## 2026-09-03 — Post-M7 readiness: In-Game Dev Mapper, Navigation Toolchain, Obstacle Avoidance & Beyond Solzreed Expansion

- **In-Game Dev Mapper (Manual Walk Mode):** Implemented `DevMapperService` and in-game `/mapper` commands (`walk`, `mark`, `stop`, `list`, `play`). Traces character movement with distance/bearing compaction (1.5m, 20°), hooks doodad interactions, NPC talks, and combat casts. Dual exports to standard CryEngine `Data/Path/<name>.path` and rich action graphs in `Data/Routes/<name>.json`. Added volatile lock-free check for zero-overhead in normal bot loops and wired disconnect cleanup in `CharacterLifecycleService.Deactivate`. Unit tests: `DevMapperServiceTests` 5/5 passed.
- **Bulk Navigation Toolchain (`Tools/Mapper/`):**
  - `redline_to_path.py`: Converts annotated 2D coordinates/map lines into continuous 3D `.path` and `.json` waypoints with ground Z-height estimation from NPC spawns.
  - `generate_zone_heatmap.py`: Plots 25,118 world NPC spawns and carriage checkpoints into 2D vector maps (`.svg`) revealing roads and settlement hubs.
  - `extract_doodad_obstacles.py`: Correlates `doodad_spawns.json` against `Doodads.xml` across 15 structure categories, extracting coordinates and keep-out collision radii.
- **Beyond Solzreed Inter-Zone Expansion (Levels 15–30):**
  - Mapped Dewstone Plains (`w_garangdol_plains_1`, 2,745 NPCs, 7 carriage checkpoints, 327 obstacles), White Arden (`w_white_forest_1`, 948 NPCs, 13 checkpoints, 107 obstacles), and Marianople (`w_marianople_1`, 1,692 NPCs, 17 checkpoints, 252 obstacles).
  - Built arterial inter-zone highway network connecting Wardton $\rightarrow$ Lilyut Crossing $\rightarrow$ Dewstone Plains (`highway_solzreed_to_dewstone.path`, 10.2 km, 402 waypoints) and Dewstone $\rightarrow$ Royster's Camp $\rightarrow$ Marianople City Gate (`highway_dewstone_to_marianople.path`, 4.0 km, 163 waypoints).
- **Physical Obstacle Avoidance (`ObstacleManager`):**
  - Created `ObstacleManager` indexing 1,395 placed obstacles across Solzreed, Dewstone, White Arden, and Marianople into a 100m 2D spatial hash grid (`Data/Navigation/*_obstacles.json`).
  - Wired into `AiGeodataManager.CheckImpossibleWalk(Vector3 point)` so A* pathfinding expands around fences, stone walls, closed gates, and buildings.
  - Sub-microsecond collision queries: `IsBlocked(point)`, `IntersectsObstacle(from, to)`, `GetNearbyObstacles(point, radius)`. Unit tests: `ObstacleManagerTests` 3/3 passed.
- **Evidence:** Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, **2,758 total tests (2,757 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools. Commits: `805f23c59`, `b34e34263`, `a1d0ae664`, `fc5c9fc1b` on `origin/develop`.

## 2026-09-03 — Post-M7 readiness: PB-001 NavigateToUnit contract and LevelingLoopScenario integration

- **PB-001 routed navigation expansion:** Landed `IGameplayActor.NavigateToUnit(uint targetObjId, ...)` across `IGameplayActor`, `GameplayActor`, and `PlayerBotControllerAdapter` (`BotActionKind.NavigateToUnit`), sharing the A* GeoData pathfinder and waypoint stepper with `NavigateTo`.
- **LevelingLoopScenario integration:** Replaced straight-line `actor.MoveToUnit` calls with `actor.NavigateToUnit` in `HuntLeg` (hunt prey, talk NPCs), `LevelLeg` (grind targets), `AbilityLevelLeg` (ability grind targets), and `TurnIn` (report NPCs); wired `actor.NavigateTo` into `GatherLeg`, `GroupGatherLeg`, and `TurnIn` (report doodads) for sources/targets beyond 3 meters.
- **Evidence:** `GameplayActorNavigateTests` **8/8** passed (+3 tests: `TargetNotFound` rejected-action, `AlreadyAtUnit` immediate completion, `WithoutGeoData` direct leg arrival). `BaiNavigationRigTests` **6/6** passed. `LevelingLoopScenarioRigTests` **35/35** passed. Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, **2,750 tests (2,749 passed, 1 skipped)**, 39 MCP BotControl tools, and 24 MCP Archaeology tools. Broad live stack navigation remains open.

## 2026-09-03 — Post-M7 readiness and closure: PB-002 QuestActObjAbilityLevel support and 70 component-only deferral

- **PB-002 objective family closure:** `QuestActObjAbilityLevel` is now fully supported in `LevelingLoopScenario` via `AbilityLevelLeg`.
  - Dispatches grinding of perceived hostiles through `GameplayActor.CastSkill` and real engine `AddExp` / `AddActiveExp`.
  - Fails closed (`WrongDecision`) when the required ability is not one of the character's 3 active abilities (`Ability1`, `Ability2`, `Ability3`).
  - No synthetic objective credit or fake XP writes; uses live `ExperienceManager.GetLevelFromExp(ability.Exp)`.
- **Ruling on the 70 component-only quests:**
  - Archeology MCP database census confirmed: of 191 component quests, 76 are paired with talk/doodad/gather discovery channels, and 45 are auto-started via `npc.Template.EngageCombatGiveQuestId`.
  - Breakdown of the remaining 70: **68 Ayanad Library floor/room bounties** (category 에아나드 도서관, level 51–55), **10 Prologue cinematic sequence chains** (category 프롤로그), **6 Honor / 2 Mistmerrow Rift world-events** (categories 명예, 전장의 안개), and **4 minigame/title/regional triggers** (categories 놀이, 칭호, 기념행사).
  - Rather than dead relics, these are specialized zone/event/script-driven quests without ordinary NPC/doodad/combat offer channels.
  - **Ruling**: Formally **deferred** to their respective future systems (Ayanad Library mechanics, tutorial director, rift/world-schedule engine). For PB-002 autonomous leveling, they fail closed and are excluded from the ordinary acceptance frontier.
- **Evidence:** `LevelingLoopScenarioRigTests` **35/35** passed (+2 ability level tests: normal completion and inactive fail-closed control). `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, 2,747 tests (2,746 passed, 1 skipped), 39 MCP BotControl tools, and 24 MCP Archaeology tools. Broad autonomous loop and human/client testing remain open.
## 2026-08-30 — Post-M7 readiness / scaling gate: A5 timing triage, ActiveRegionTick remediation, next calibration

- **The 12-hour testing/canary report at SHA
  `1ce4664f96705850136dc9d46999070fac9763fb` remains valid evidence:** FULL
  720.000044 minutes / 721 samples; dormancy 1000/1000, embodied 0,
  materializations/dematerializations 0/0, scheduler queues/failures/save-skips
  0, DB writes 0, RSS growth 155.9MB < 512MB budget. Overall `passed=false` on
  timing only.
- **Timing evidence (classification unchanged):** seven distinct sampled
  breaches — region 299/288/308/291/297/291ms (budget 200ms) and tick max
  282.9ms (budget 250ms); 571 ActiveRegionTick overrun log events in-window
  recurring ~76s; 566/571 same-second physics-slow warnings; no
  workload/DB/RSS correlation. Classification remains **UNKNOWN host-level
  physics-thread stall vs host steal** — not a relaxed threshold and not an
  A5 pass.
- **Remediation `1801baf987d70eb8f2ac64ac3a9fa84e470e74e8` landed:**
  ActiveRegionTick now reuses ONE character snapshot and a direct
  active-spawner scan (`SpawnManager.GetActiveNpcSpawners` /
  `NpcSpawner.IsPlayerInSpawnRadius(IReadOnlyList<Character>)`), avoiding the
  old `GetAllSpawners()` deep-copy and nested per-spawner `GetAllCharacters()`
  enumeration; added `CharacterSnapshotMs`/`SpawnerScanMs` telemetry (bridge
  `metrics`); no-player and active-player regression tests pass 2/2
  (`ActiveRegionTickSpawnerScanTests`); builds clean. It reduces
  allocation/work but still scans O(spawners) per pass — no zero-cost claim.
- **Next action:** run bounded testing/canary calibration with the same warmup
  and phase metrics, inspect `SpawnerScanMs`/`CharacterSnapshotMs`/physics
  stalls; only then decide code fix vs budget calibration and another 12h run.
## 2026-09-01 — A5 physics/tick stall investigation dossier (read-only)

- **Dossier:**
  [`scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md`](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md) —
  read-only investigation (source report SHA-256 `6892e5e0…`, soak source SHA
  `1ce4664f…`); no code/data edited, no tests run, no commit.
- **Two distinct failure modes confirmed:** Mode A = ActiveRegionTick
  region-pass overrun (6 sampled breaches 299/288/308/291/297/291ms vs 200ms
  budget; 571 log overruns at ~76s cadence; 566/571 same-second physics-slow;
  deferred 0 characters → descheduling time, not work time). Mode B =
  TickManager invoke-max overrun (1 sampled breach 282.9ms vs 250ms budget at
  16:48:02Z with region = 5ms that sample — NOT the region pass; sync
  subscriber or tick-thread deschedule, per-subscriber attribution not
  measured).
- **12h soak ran PRE-remediation:** SHA `1ce4664f…` predates the
  ActiveRegionTick deep-copy hotspot fix `1801baf98…` (landed after the run);
  the 0-work pass profile makes an allocation hotspot an implausible stall
  cause regardless.
- **Classification unchanged: UNKNOWN, host-level scheduling/CPU steal
  leading hypothesis.** Software hotspot and physics workload ruled out by the
  0-work profile (zero rigid bodies, ~0ms physics work); GC pause unlikely
  given SustainedLowLatency (`105b4d5ed`) but NOT measured during the soak. No
  host metrics were collected during either soak — the single biggest gap.
- **Budgets NOT relaxed.** 200ms region / 250ms tick-max / 100ms tick-p95
  stand.
- **Exact next calibration:** bounded 6h rerun (same SHA `1ce4664f` or current
  HEAD) with a 1s host-telemetry sidecar — per-second process/thread CPU wall
  (`/proc/<pid>/stat` + `/proc/<pid>/task/*/stat`), `/proc/stat` steal deltas,
  PSI (`/proc/pressure/{cpu,io,memory}`), cgroup `cpu.stat` throttling deltas,
  GC pause events (dotnet-counters / `DOTNET_EnableEventLog`), per-subscriber
  tick attribution (bridge `metrics.tick.subscribers`), and a pinned
  (`taskset -c`) control arm. Success criterion: stall seconds coincide with
  steal/PSI/cgroup spikes or all-thread deschedules → classification moves to
  host-scheduling; stalls with zero host signals and pinned CPUs → return to
  the process (GC, thread-pool starvation, sync subscribers).
## 2026-09-02 — A5 memory-pressure diagnosis (user/live operational evidence; additive)

- **Dossier updated:**
  [`scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md`](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md) —
  new section 7 records the memory-pressure diagnosis from **user/live
  operational evidence** (not H/human gameplay feel, not independently
  reproduced by this tool call).
- **Evidence (exact labels preserved):** prod CT133 presence-demo healthy
  ~6 days, no soak, **1,647 physics-slow warnings over 9 days**,
  simultaneous both-world spikes ~500–575 ms with matching values (example
  573/574 ms); prod Game ~130,228 kB VmSwap on an 8 GB CT with 512 MB zram,
  swappiness 60, Game VmData ~4.7 GB, MySQL/Login/Adminer/API sharing the
  ceiling; comparison/contrast soak (CT124, not a matched A/B) 0 KB swap
  on 48 GB RAM and zero warnings in 12 h; user live 573 ms spike
  **coincided with** a .NET BGC thread, ~25 MB RSS drop, and swap-in
  clustering (single reported coincidence, not causal proof).
- **Diagnosis:** memory pressure/swap + background GC/page faults is a
  **strongly supported provisional infrastructure root cause** (Mai's
  CT133 diagnosis, user/live operational evidence) for the
  **user-reported current PROD CT133 only** — **no longer merely UNKNOWN
  host scheduling for that environment**. The soak-time classification
  remains UNKNOWN: the soak host had **0 swap** and no in-soak host/GC
  telemetry was collected, so memory/swap does NOT explain the 12 h soak
  breaches; budgets NOT relaxed. **A5 remains formally OPEN/UNCLOSED**
  until CT133 memory remediation is applied and a comparable post-change
  run confirms the warnings disappear; no H claim, no new implementation
  scope.
- **Next action — memory remediation first:** preferred CT133 memory
  increase to 16 GB; alternatives `DOTNET_GCHeapHardLimit` calibration or
  disabling swap with OOM risk; require before/after memory/swap/GC
  telemetry, then rerun the post-remediation soak.
- **1-hour calibration lane telemetry run (2026-09-02, no new soak
  result):** host sidecar ~3,388 samples, 0 steal/CPU PSI/throttling;
  physics loop max 62 ms at boot and ≤ 40 ms steady; 0 in-window
  physics-slow warnings; no A5 pass claim.
## 2026-09-02 — A5 post-remediation follow-up (user/Mai operational evidence; additive)

- **Dossier updated:**
  [`scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md`](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md) —
  new section 8 records the post-remediation follow-up from **user/Mai
  operational evidence** (not H/human gameplay feel, not independently
  reproduced by this tool call).
- **Provenance (exact labels preserved):** running Game PID 3057037;
  deployment since 20:06 UTC; ~10.5 h observation; CT host 16 GiB RAM /
  8 GiB swap; effective CT and game-container cgroups
  `memory.max=max`, `memory.swap.max=max`; `memory.events` zero OOM / zero
  max hits; CT 4.2 GB / game container 2.8 GB; Game VmRSS 2.67 GB, VmData
  4.27 GB, **VmSwap 0 kB** (pre-restart ~129 MB); stack memory game
  2.6 GiB / db 467.5 MiB / login 43.2 MiB / adminer 8.8 MiB /
  register-api 15 MB; GC trace capture alive 5.3 MB and growing; no GC
  events in ordinary logs because events are in nettrace.
- **Behavior (exact labels preserved):** 17 physics warnings across the
  ~10.5 h observation, worst 340 ms; 22 spikes in the first 2 h
  post-restart, worst 807 ms — a distinct reported window/class from the
  ~10.5 h warning count; **500 ms+ signature absent in the later observed
  period** (the ~10.5 h window's worst is 340 ms) — the 807 ms first-2 h
  spike predates that absence, which is not claimed for the first-2 h
  window.
- **Classification:** **strongly supports** the prod CT133
  memory-pressure/swap hypothesis, **not fully proving** it — residual
  ~300 ms events keep another cause open. Historical 12 h soak
  classification remains **UNKNOWN**; **no A5 pass is claimed**; budgets
  unchanged.
- **Next closure criteria:** continue GC/nettrace capture; correlate
  residual warnings with GC/thread/process/host telemetry; then run a
  comparable post-change A5 soak with **zero budget breaches** before
  closing A5. **A5 remains formally OPEN/UNCLOSED**; no H claim, no
  budget relaxation, no new implementation scope. Labeled user/Mai
  operational evidence, not H/human gameplay and not independently
  reproduced here. No code/data/client/soak changes, no commit.
## 2026-08-30 — Post-M7 readiness and closure: PB-002 MateLevel rig proof and canonical-data boundary

- **PB-002 remains PARTIAL / configurable support, not universal closure.**
  `QuestActObjMateLevel` is supported by a configurable `MateLeg`, not closed
  across all canonical carriers or all progression routes.
- `MateLeg` uses explicit `LoopOptions.MateGrowthItemId`; the default value `0`
  fails closed. It must use the real item-use path and never write mate XP,
  level, or the quest objective directly.
- **Deterministic rig proof:** `GameplayActor.UseItem(item, mateObjId)` →
  real `AddExp` effect → `Mate.AddExp` → headless-safe `OnMateLevelUp` →
  objective credit. **41 uses of 50k XP** reach **2,021,250 mate XP / level
  50**.
- Focused evidence is `GameplayActorMateLevelRigTests` **5/5** and
  `LevelingLoopScenarioRigTests` **33/33**; Game/UnitTests Release builds
  report **0 errors**. This is deterministic rig/proxy evidence only: there is
  no live authenticated or human/client evidence.
- The canonical item **29040** / skill **23085** is blocked by
  `MotherFactionOnly=5`; no canonical faction satisfies it. Do not claim
  canonical live-carrier closure. A data ruling/fix or an evidenced alternative
  canonical growth-item source is required.
- `QuestActObjAbilityLevel` remains blocked: no ordinary player action raises a
  specific ability level, nine quests have zero accept surfaces, and quest
  **5967** is self-gated by ten Ability ≥ 50 requirements.
- **Next action:** decide/fix the canonical 23085 data issue or document an
  evidenced canonical growth-item source; then run live testing. Broad PB-002
  progression and human/client acceptance remain open.


## 2026-08-29 — Post-M7 readiness and closure: PB-002 aggro slice (historical/pre-fix context)

- **Historical result:** the `AggroLeg` supports canonical `QuestActObjAggro`
  forms whose live quest instance has a non-zero NPC acceptor template. It
  selects perceived, attackable NPCs, requires the owner's real aggro ranking,
  and reuses SetTarget → Cast → kill → Loot; completion is read from live
  quest state after the real OnKill boundary.
- The 2026-08-29 gap wording below is superseded by the 2026-08-30 re-census
  and later objective-family landings. Preserve it as historical evidence, not
  as the current PB-002 gap list.

## 2026-08-30 — Post-M7 readiness: PB-002 current objective frontier (historical/pre-rig snapshot; stale wording retained)

- **Scope:** Post-M7 readiness and closure → PB-002 quest progression.
  **PB-002 remains PARTIAL with broad autonomous progression OPEN.** Current
  evidence is deterministic rig/proxy (A/R), not live authenticated progression
  and not human/client evidence.
- **Landed objective families:** interaction, item-use, item-group use/gather,
  Sphere, Craft, Cinema, MonsterHunt/MonsterGroupHunt, Aggro (partial),
  ZoneKill, EtcItemObtain, CompleteQuest, and Level. CheckTimer and
  SupplyRemoveItem are non-objective gate/cleanup acts, not gaps.
- **Remaining objective gaps (2026-08-30 research decision):**
  `QuestActObjAbilityLevel` is **blocked / not implementable now** — 15 raw
  rows, 11 carrier quests (10 live after the 6069 drop), 9 single-ability
  quests (6070/6075–6082) have zero accept surfaces, and quest 5967 is
  self-gated by ten Ability ≥ 50 requirements; an exhaustive source trace found
  no ordinary player action/packet that raises a specific ability's level
  (only `Character.AddExp(exp, true)` distributes active-tree XP). Needs a data
  ruling/drop and/or a 1.2 trainer client capture. `QuestActObjMateLevel` is
  **implementable after a narrow rig proof** — 10 raw rows, 6 live carrier
  quests (5430/5464/5465/5466/5812/5813), 4 dangling act rows; canonical
  mate-targeted growth consumables exist and ordinary
  `GameplayActor.UseItem(item, mateObjId)` can target a Mate, with the real
  `AddExp` effect → `Mate.AddExp` → headless-safe `OnMateLevelUp` as the credit
  path (2,021,250 XP to level 50; 29040 at 50k XP = bounded 41 uses). The gap
  stays until the rig proof (fixture summon-item registration in `ItemManager`
  + headless targeting) passes. Aggro remains partial for forms without a
  resolvable NPC acceptor, ranking, or kill event. Separately, 70
  component-only quests with no engage tie remain genuinely unreachable as an
  acceptance channel; acceptance reachability is distinct from objective
  support.
- **Focused current evidence:** `LevelingLoopScenarioRigTests` **32/32**,
  `QuestActObjAggroTests` **2/2**, `QuestEtcItemObtainRigTests` **3/3**,
  `QuestZoneKillVictimRigTests` **2/2**, `PvpFlaggingRigTests` **11/11**;
  Game/UnitTests Release builds report 0 errors. These results do not close
  broad PB-002 autonomy.
- **Evidence layer for Ability/Mate:** canonical SQL + code archaeology only
  (dossiers `scorecard-explorations/mechanics/ability-level-objective-research.md`
  and `mate-level-objective-research.md`); no implementation, no live
  authenticated run, and no human/client evidence for either family.
- **Remaining next actions:** (1) Mate rig proof (fixture summon-item
  registration in `ItemManager` + headless mate-targeted potion use); (2) data
  hygiene for the dangling act rows (Ability 34805–34808; Mate 33008/33212/
  33213/35465) per data-defects §7; (3) ability data ruling/drop for
  6070/6075–6082 and 5967 trainer-flow client capture; rulings for the 70
  unreachable acceptance forms/data; live authenticated progression; and
  human/client evidence.

### Current PB-002 implementation notes

- `QuestActObjCompleteQuest` is pursued through bounded prerequisite
  composition; the engine, not the scenario, sets the completed-quest flag.
- `QuestActObjLevel` is pursued through LevelLeg and real kill/XP boundaries;
  the scenario never writes XP or level. `KnownPrimitiveGaps` therefore contains
  only the Ability/Mate gaps for objective levels.
- The current branch is `develop` at landed commits; do not describe these
  slices as uncommitted or local-only.

## 2026-08-30 — Post-M7 readiness: PB-002 complete-quest composition + classifier reclassification (landed current state)

- The CompleteQuest composition and non-objective classifier changes are landed
  in the current `develop` tree. The detailed behavior and evidence are
  summarized above; this heading is retained to preserve the dated record.

## 2026-08-30 — Post-M7 readiness: PB-002 level-objective pursuit (landed current state)

- The Level objective pursuit is landed in the current `develop` tree. The
  detailed behavior and evidence are summarized above; this heading is
  retained to preserve the dated record.

## 2026-08-30 — Post-M7 readiness: discovery-channel re-census at 9b8ba6317 (PB-002 evidence refresh)

- **What changed:** three code commits (`3827b5170` kill-accept perception in `DiscoverQuests`,
  `7d0b80041` `AutoStartedQuestIds` pursuit, `a1653d67d` `OnKillArgs.Target = victim` in `DoDie`) plus
  `f5331ced7` (AggroLeg) landed after the 2026-08-29 census. Re-ran the same read-only SQL against
  canonical `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af`, unchanged) — **data counts are
  identical**; only the code deltas move the reachable frontier.
- **Corrected census:** perceivable 8-channel union still **4,181**; the loop now additionally pursues the
  **380 kill-only** quests (through the kill-accept channel) and **45 of the 115 component-only** quests
  (**30** auto-start + aggro — previously misclassified as "engine-broken", now unblocked by `a1653d67d`;
  **15** auto-start + MonsterHunt/plain) — **4,226 total pursuable at HEAD**. Remainder is **70** component-only
  quests with no engage-NPC tie and no aggro act (stub `RunAct` true-return, no perception primitive
  exists) — genuinely unreachable, correctly fail-closed. See
  `scorecard-explorations/generated/discovery-channel-census-2026-08-29.md` §"2026-08-30 re-census".
- **PB-002 impact:** the 2026-08-29 line "component forms without an NPC acceptor … remain open and are
  explicitly fail-closed" is **superseded for the 45 auto-startable forms** (they DO get an Npc acceptor
  triple via `AddQuestFromNpc` on first aggro); the boundary still holds for the **70** with no engage tie
  and for aggro forms whose live acceptor template resolves to 0. Full test coverage at this HEAD:
  `QuestActObjAggroTests` **2/2**, `LevelingLoopScenarioRigTests` **21/21** (includes the 6109 auto-start
  end-to-end). Evidence class is unchanged — deterministic rig / A, no live/human claim.

## 2026-08-30 — Post-M7 readiness: PB-002 complete-quest composition + classifier reclassification (landed current state; historical snapshot wording below)

- **Historical snapshot correction:** the former `QuestActObjLevel` gap wording
  is superseded by the landed `LevelLeg`. Current remaining objective gaps are
  only `QuestActObjAbilityLevel` (11 quests) and `QuestActObjMateLevel` (7 live
  quests; 1 orphaned data row); aggro remains partial at its resolvability
  boundary. CompleteQuest is landed, and CheckTimer/SupplyRemoveItem are
  non-objective gate/cleanup acts.


- **PB-002 result: PARTIAL capability closure; broad autonomy OPEN; aggro boundary UNCHANGED.** The
  `LevelingLoopScenario` gained a `CompleteQuestLeg` for `QuestActObjCompleteQuest` (canonical 11 carrier
  quests / 53 Progress acts; prerequisite chains verified against compact.sqlite3 md5
  `78b3bdbf038db3b927056106efdf91af`, unchanged). The act has NO event subscription: its `RunAct`
  credits the objective from LIVE `HasQuestCompleted(prereq)` at step evaluation. The leg re-perceives
  the prerequisite through normal `Perceive`/`DiscoverQuests` channels, accepts it through the real
  accept path, pursues its own objectives through the existing legs, turns it in through the real report
  path, and lets the parent's REAL step evaluation credit the objective — the completed flag is produced
  by the engine's own `SetCompletedQuestFlag` at the prerequisite's drop-time, NEVER written by the
  scenario. Recursion is bounded by `LoopOptions.MaxCompleteQuestDepth` (default 3) plus an ancestor
  stack cycle guard; sibling prerequisites of one step share neither (each act gets its own child
  ancestor set). An already-completed prerequisite is a no-op; an undiscoverable prerequisite, an
  unknown prerequisite template, or a prerequisite that completes without its flag set fails closed
  naming the exact quest id.
- **Classifier reclassification (no pursuit legs added):** `QuestActCheckTimer` (canonical 68 rows,
  Progress 2) and `QuestActSupplyRemoveItem` (canonical 61 rows, Progress 1) are NOT objectives —
  both are `CountsAsAnObjective=false` with `RunAct` returning true unconditionally (timer is a
  gate that the engine arms via `QuestTimeoutTask` → `FailQuest` on expiry with no quest-side clock
  seam; supply-remove is inventory cleanup executed by the act itself). Both are passed through in
  `PursueObjectives`; neither is a `KnownPrimitiveGaps` entry anymore.
- **Historical snapshot correction:** the former `QuestActObjLevel` gap wording
  is superseded by the landed `LevelLeg`. Current remaining objective gaps are
  only `QuestActObjAbilityLevel` (11 quests) and `QuestActObjMateLevel` (7 live
  quests; 1 orphaned data row); aggro remains partial at its resolvability
  boundary. CompleteQuest is landed, and CheckTimer/SupplyRemoveItem are
  non-objective gate/cleanup acts.
  Broad PB-002 autonomy remains OPEN; evidence is rig/proxy only.
## 2026-08-30 — Post-M7 readiness: PB-002 level-objective pursuit (historical pre-landing snapshot)

- **PB-002 result: PARTIAL capability closure; broad autonomy OPEN; aggro boundary UNCHANGED.** The
  `LevelingLoopScenario` gained a `LevelLeg` for `QuestActObjLevel` — the canonical **1 quest, 6250**
  "새로운 당신을 위한 선물" (Start `QuestActConAcceptItem` 442 → item 33027, Progress
  `QuestActObjLevel` 14 → **Level 30**, Reward `QuestActSupplyItem` 4158/4161 + `QuestActConAutoComplete`
  1712; no Ready step → auto-completes). The act credits from **LIVE `Owner.Level` at step evaluation**
  (`QuestActObjLevel.RunAct` reads `quest.Owner.Level >= Level` and `SetObjective(1)`); the headless
  `OnLevelUp` event is unavailable (`Character.AddExp` fires `DoOnLevelUpEvents` only when
  `Connection != null`) and is never faked. The leg grinds perceived hostiles through the **real kill
  path** — LIVE: real cast damage → `Npc.DoDie` → `Character.AddExp(KillExp, true)`; RIG: the
  documented test-only `ILevelXpSeam` at the REAL `Character.AddExp` boundary (mirroring DoDie's
  character-XP grant, `Npc.cs:879`). The scenario NEVER writes XP or level; a bounded kill budget
  (`LoopOptions.MaxLevelGrindKills`, default 64) fails closed (`Starvation`) when the level cannot
  rise. Item 33027 has ZERO canonical grant sources (GM-granted starter) — rigs fixture-grant it as
  setup only.
- **Gap reclassification:** `QuestActObjLevel` is REMOVED from `KnownPrimitiveGaps` (pursued now).
  `QuestActObjAbilityLevel` (11 quests) and `QuestActObjMateLevel` (7 quests) remain named gaps with
  no-player-action reasons (ability exp only rises via the character-XP share `AddActiveExp` with no
  `OnAbilityLevelUp` handler; mate level only via `Mate.AddExp` kill share / `MateXpUpdateTask`
  demanding a Level-50 breed mate). `QuestActObjAggro` remains the same PARTIAL boundary; no broad
  PB-002 closure is claimed.
- Evidence layer is **A / rig (proxy/bot-functional)**, not H, restart, or soak:
  `LevelingLoopScenarioRigTests` **32/32** (30 existing + 2 new: level-objective positive
  completing through live level state, and no-XP-source fail-closed control), `QuestActObjAggroTests`
  **2/2**, `QuestEtcItemObtainRigTests` **3/3**, `QuestZoneKillVictimRigTests` **2/2**,
  `PvpFlaggingRigTests` **11/11**, full `AAEmu.UnitTests` suite green. Release builds of `AAEmu.Game`
  and `AAEmu.UnitTests` are 0 errors; `git diff --check` clean. The
  uncommitted/local wording in this retained snapshot is historical; current
  branch state is landed. No E2E/soak/`.worktrees`/generated-JSONL was touched.


The six-hour stage is opt-in and requires `A5_TIER3_SIX_HOUR=1`,
`A5_TIER3_SIX_HOUR_MINUTES>=360`, and sample seconds 1 through 300. The
six-hour dormant soak DID execute at
`/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json` for
360.00003 minutes / 361 samples: DormantSpecs 1000 throughout, embodied 0,
materializations/dematerializations 0, queues/failures/save-skips 0, DB writes
delta 0, tick max 19.1ms, and save p95/max 85.7/100.8ms. Its RSS assertion
failed because the baseline was captured before deferred world startup settled
(baseline 1207.9MB, peak 5749.4MB, final 3744.1MB, budget +512MB); this is not
yet classified as a leak.

Evidence classification is **testing/canary diagnostic failure**, not live
human gameplay evidence and not an A5 pass. Correction `ccd4ea857` adds
explicit `A5_WARMUP_READY`, a post-quiescence baseline, separate startup-peak
accounting, and fail-fast post-baseline RSS breach handling with FULL/PARTIAL
report semantics. A corrected rerun (preferably a 12-hour testing soak) is
pending. The old bounded Tier3 rehearsal below remains historical evidence.

**Corrected 12-hour testing/canary soak (2026-08-30, SHA
`1ce4664f96705850136dc9d46999070fac9763fb`):** completed FULL — 720.000044
minutes / 721 samples. DormantSpecs 1000/1000, embodied 0,
materializations/dematerializations 0, scheduler queues/failures/save-skips 0,
DB writes 0, RSS baseline 2383.4MB, startup peak 2471.3MB, steady peak
2539.3MB, RSS growth 155.9MB < 512MB budget. Overall `passed=false` on timing
only: seven distinct timing-breach samples (six failure strings after dedupe)
— region 299/288/308/291/297/291ms (budget 200ms) and one tick max 282.9ms
(budget 250ms). No RSS failure. Triage: 571 ActiveRegionTick overruns in the
12h game log (570 in window) at a recurring ~76s cadence (gaps 75–80s ×375,
30s ×126); 566/571 coincide same-second with physics-slow warnings; region
passes report deferred 0 characters; physics values 278–554ms track the
region stalls. All other workload metrics stayed healthy. No GC evidence;
classification is **UNKNOWN / host-level physics-thread stall versus
scheduler/host steal** — not a confirmed code regression and not a pass.
Prior dormant soaks showed no recurring 100–300ms region stalls; the active
1000-bot storm is not comparable. Next action (historical — superseded by the 2026-08-30 scaling-gate
entry above): isolate/inspect testing-host CPU steal and physics-thread
diagnostics, then a bounded timing reproduction; budgets are NOT relaxed yet.
The A5 warmup correction (`ccd4ea857`) is validated; the corrected 12h rerun is
complete but timing triage remains open. Evidence is testing/canary operational
evidence, not live human gameplay.

The prior full gate at `0ce518ac03a18de00fff1516aa9e794e8566bee6` remains
2504 total / 2503 passed / 0 failed / 1 skipped, compiler 0/0, MCP 39; no new
full gate was run for `da0fdc61`. No M6 full-exit or H/UAT claim is made;
historical reports and failed soak provenance remain preserved.

Josh human-QAT wave 4: Docs/JOSH-QAT-WAVE4.md (2026-08-25) — 8-pack for mail
return (0x0a2 hypothesis), mail ownership guards, labor regen, war-gated
honor, NPC grounding tour, boats, slavetest observation, Mirage walk.

## 2026-08-26 recovery reconciliation

- **Develop contents confirmed:** grounding fix `38c4997d3`, recovered
  Retribution wire-observability test branch `a4f7820ba`, merchant merge
  `e5db6d390` (the three merchant fixes are in that merge), and Mail S3
  acceptance `31045d033`; earlier committed features remain in the current
  ancestry.
- **PB-005 grounding:** **FIXED-PARTIAL** — positive-only clamp and intentional
  aerial/water/structure whitelist landed. The terrain-only replay corrects all
  593 non-whitelisted severe-positive rows and leaves 702 whitelisted rows
  unchanged; cave/deck/submerged behavior and duplicate-row decisions remain.
- **PB-007:** **FIXED / CLOSED for the narrow flagged-aggression handshake
  requirement** at behavioral gate evidence baseline
  `3871459d142fdd1767b9365a1de8d4cd3652ab0e` (current source/test HEAD is
  `792774d7707b8b578b8d9975896e0a1ac719f361`). Current report:
  `scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md`.
  The isolated real-login/Game E2E observed a victim-matched, non-immune
  `SCUnitDamaged` frame, with immune frames excluded, `SkillFired=True`,
  Retribution 2167, bloodstain doodad 877, and the crime branch observable;
  PEACE-BLOCK also passed. The prior `/root/aaemu-e2e` report and failed/immune
  context remain historical. WAR-HONOR remains separately deferred; this does
  not close all PvP/honor scope.

- **Mail S3:** **PASS / LANDED** in `31045d033` — authenticated
  `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
  passed 1/1 in 2m39s on isolated MySQL/Docker. Restart, instance-faithful
  equipment attachment, ownership, unread-count, take, and delete assertions
  passed; no live-client confirmation of inferred return opcode is implied.
- **PB-001 routed navigation:** **IMPLEMENTATION + TRACKED FIVE-TEST
  CONTRACT EVIDENCE** — `IGameplayActor.NavigateTo` supports CryEngine
  GeoData A* routing, dynamic waypoint stepping, stuck detection, and
  straight-leg fallback. Source/test commits: `0c57ef0c9` (tracked
  `GameplayActorNavigateTests`) and `57b6e2960` (linked-worktree helper
  compatibility). Focused result:
  `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter '/*/*/GameplayActorNavigateTests/*'`
  → `Test run summary: Passed! total: 5 failed: 0 succeeded: 5 skipped: 0 duration: 1s 362ms`.
  `BaiNavigationRigTests` supplies the GeoData/navmesh coverage. The preserved
  prototype waypoint test was invalid because it injected private state via
  reflection; do not claim waypoint coverage from it. `BotActionCommandQueueTests`
  covers the first-class `Navigate` queue action.
- **PB-002 autonomous leveling loop:** **SCOPED ACTOR/RIG SLICES LANDED;
  BROAD CLAIM OPEN** — `LevelingLoopScenario` and related actor/rig slices
  cover selected perception-driven quest steps. Current item-use coverage drives
  `QuestActObjItemUse` through real `GameplayActor.UseItem` for canonical quest
  252 (NPC 7653, item 7738, use skill 11596, act row 1600/detail 43), with
  fail-closed canonical quest 64 control. **Historical/pre-fix interaction
  context:** the earlier quest-270 candidate (doodad 687, interaction skill
  11229) reached `Doodad.Use`, but its spawned fixture exposed no phase
  functions; the old failure and “No implementation landed” conclusion are
  preserved as historical evidence only. **Current result:** quest **269→270**
  interaction is landed and verified at the deterministic rig layer through
  `QuestActObjInteraction` / `GameplayActor.InteractWith`; the current
  `LevelingLoopScenarioRigTests` total is **21/21** after the aggro work.
  Broad PB-002 remains open with no live or human breadth claim.
**Behavioral gate evidence (normal-clone full gate at source/test HEAD
`792774d7707b8b578b8d9975896e0a1ac719f361`):** Release build PASS, compiler
check 0/0, unit **2496 total / 2495 passed / 0 failed / 1 skipped**, and MCP
stdio **39 tools**. The sole skip is
`Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
`AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. IntegrationTests Release
restore/build passed with 0 errors; restore emitted 2 NU1903 and build emitted
2 NU1903 in this exact verification. Focused PB-002 results:
`LevelingLoopScenarioRigTests` 7/7, item-use 1/1, unsupported-objective 1/1,
discovery 12/12, talk 5/5, and template registration 1/1. Parser tests
passed 2/2. These are the current 792 gate counts; no different full-gate
result is claimed.
## Current provenance and soak boundaries (2026-08-28)
- Runtime evidence records `E2eStack.SourceRevision` from `git rev-parse HEAD`
  and reports `unknown` when the checkout/archive cannot resolve a revision.
  The current local source/test pointer is `da0fdc61a72a15111fddc8ac627a164a5f050558`.
- The corrected bounded Tier3 rehearsal at `4721cbd306cbf346bfe38b7373d5adf479b6231f`
  passed 1/1 in 15m20.984s: 1000 seeded, 50 embodied, 950 dormant,
  materialize p95 259.2ms, RSS +2.56%, 50 dematerialized, owned cleanup zero.
  This is readiness evidence only.
- `155c82c66` adds the opt-in six-hour natural dormant-timer test. The old
  default-skipped/no-result wording is historical pre-execution context only.
  The executed canary report is
  `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json`:
  360.00003 minutes / 361 samples, DormantSpecs 1000, embodied 0,
  materializations/dematerializations 0, queues/failures/save-skips 0, DB
  writes delta 0, tick max 19.1ms, save p95/max 85.7/100.8ms. RSS assertion
  failed on the pre-quiescence baseline (1207.9MB → peak 5749.4MB → final
  3744.1MB; budget +512MB), not yet classified as a leak.
- This is a testing/canary diagnostic failure, not live human gameplay and not
  an A5 pass. Correction `ccd4ea857` adds `A5_WARMUP_READY`, post-quiescence
  baseline, separate startup peak, fail-fast post-baseline RSS breach, and
  FULL/PARTIAL report semantics. A corrected rerun, preferably 12 hours,
  remains pending.
- **Corrected 12-hour testing/canary soak (2026-08-30, SHA
  `1ce4664f96705850136dc9d46999070fac9763fb`):** FULL — 720.000044 minutes /
  721 samples; DormantSpecs 1000/1000, embodied 0, materializations/
  dematerializations 0, scheduler queues/failures/save-skips 0, DB writes 0,
  RSS baseline 2383.4MB, startup peak 2471.3MB, steady peak 2539.3MB, growth
  155.9MB < 512MB. `passed=false` on timing only: seven distinct breach
  samples (six failure strings after dedupe) — region 299/288/308/291/297/291ms
  (budget 200) and tick max 282.9ms (budget 250). Triage: 571 ActiveRegionTick
  overruns in the 12h game log (570 in window), recurring ~76s cadence,
  566/571 same-second with physics-slow warnings, deferred 0 characters;
  physics 278–554ms tracks the stalls. Classification **UNKNOWN / host-level
  physics-thread stall versus scheduler/host steal** — not a confirmed code
  regression, not a pass. Next: isolate/inspect testing-host CPU steal and
  physics-thread diagnostics, bounded timing reproduction; budgets not
  relaxed. Warmup correction validated; timing triage remains open. Evidence
  is testing/canary operational, not live human gameplay.
- Cancellation focused tests pass 3/3; ownership focused tests pass 2/2.
  The prior full gate at 0ce remains historical: 2504 total / 2503 passed /
  0 failed / 1 skipped, compiler 0/0, MCP 39. No new full gate was run at da0.
- Human/H boundaries remain intact: bot, rig, MCP, and live-stack evidence is
  functional/proxy evidence; H/human-feel acceptance remains Josh-owned.

## 2026-08-27 MCP expansion
- **Client-neutral integration:** MCP sidecars and the management gateway remain
  client-neutral; they do not constitute external-client actor evidence.
- **Historical coverage retained:** merge `8a22dcb4` recorded the earlier
  33-test contract coverage and 19-tool stdio smoke; earlier route-count
  checkpoints are historical and superseded by the current 39-tool catalog.
- **Flash route expansion:** fifteen authenticated actor routes/tools landed:
  Deposit/Withdraw money and items, Plant/Harvest, Craft, Buy/Sell,
  PackPickup/PutDown/LoadPackOntoVehicle, and Board/Unboard/DriveVehicle.
- **MCP catalog:** now **39 tools**. Flash reports focused validation:
  `BotActionControllerRouteTests` 2/2, `BotControlActionMcpTests` 33/33,
  `BotActionCommandQueueTests` 18/18; protocol smoke 39 tools. The clean gate
  is SHA-pinned at the audited source baseline above from a normal clone.
- **Live benchmark:** Flash reports `discover_self_quests` MCP benchmark
  passed with `action_status` and `trace`, plus an independent MySQL
  character-row cross-check. This remains unpinned benchmark evidence; no
  SHA-pinned artifact is checked in. No safe doodad interaction was attempted.
- **Historical smoke retained:** the earlier
  `mcp-live-smoke-2026-08-27.md` report-only run at `7e109d550` is retained as
  historical evidence of an asset-missing Game exit before WebApi; it is not
  the current MCP benchmark verdict.
- **Deferred action-surface boundary:** only later Party, Trade, Expedition,
  Auction, and related actor expansion remains deferred and is not claimed as
  MCP-exposed.

## 2026-08-31 — Archaeology MCP (greenfield read-only data server)

- **Greenfield, separate-process, read-only MCP.** `AAEmu.ArchaeologyMcp/` is a
  new MCP stdio server (no MCP SDK; newline-delimited JSON-RPC 2.0, matching
  the `AAEmu.BotControlMcp` convention) that runs as its own process and opens
  SQLite with `Mode=ReadOnly`. It exposes the canonical ArcheAge 1.2 reference
  data (`compact.sqlite3`) and allowlisted repo source roots as read-only MCP
  tools. It is **client-neutral** (any MCP client can spawn the same process)
  and does not change any PB/M7/A5 claim.
- **Current tool surface: 24 tools.** Raw catalog/files/SQLite
  (`list_sources`, `list_databases`, `list_tables`, `describe_table`,
  `query_sql`, `read_file`, `search_files`); AAPak list/read
  (`list_pak_entries`, `read_pak_entry`); cross-cutting search
  (`search_everything`, `trace_references`, `find_quest_objectives`); typed
  domain helpers (`trace_skill`, `trace_item`, `trace_quest`, `trace_npc`,
  `trace_doodad`, `trace_mate`, `trace_vehicle`, `trace_crafting`,
  `trace_world_spawn`, `search_physics`); plus `lookup_row` and
  `compare_source_data`.
- **Canonical DB:** `AAEmu.Game/Data/compact.sqlite3`, **679 tables**, md5
  `78b3bdbf038db3b927056106efdf91af` (unchanged), target ArcheAge **1.2
  r208022**. Read-only invariant: the md5 is unchanged after any tool run.
- **Allowlisted roots + optional AAPak.** Primary root `AAEmu.Game/Data/`;
  secondary roots `AAEmu.Game/` source, `SQL/`, `tools/`, `Scripts/*census.sh`,
  `scorecard-explorations/`. E2E/soak roots, MySQL (mutable state), and secrets
  are excluded by default; extra roots only via explicit
  `ARCHEAGE_EXTRA_ROOTS` opt-in. The `game_pak` AAPak archive is reachable
  read-only through `list_pak_entries`/`read_pak_entry` only when
  `ARCHEAGE_PAK_PATH` is configured (bounded listing, 1 MiB reads, never
  streamed wholesale); otherwise both tools return a deterministic
  `not configured` error.
- **Strict controls:** SQL allow-list (`SELECT`/`WITH`/`EXPLAIN`/schema-read
  `PRAGMA` only; no mutation keywords, no multi-statement batches, no comments);
  read-only connections; row/column/byte bounds; a native
  `sqlite3_progress_handler` deadline (10 s) because `Microsoft.Data.Sqlite`
  ignores `CommandTimeout`; path/symlink guards on `read_file`/`search_files`/
  `list_databases`/`file:<name>` ids; no shell execution; no mutation tools.
- **Focused evidence is code/tests/smoke, not live client/H.** Evidence is
  `SqlGuardTests`, `ArchaeologyMcpServerTests` (24-tool surface),
  `ArchaeologyDomainTests`, `PakArchiveServiceTests`, and the deterministic
  `Scripts/mcp-archaeology-smoke.sh` protocol+read-only smoke. This is
  **A/R/L** (function/restart/load per artifact) evidence only; **H stays
  UNKNOWN** — no live client, no human run.
- **Current known limits:** no MySQL surface (mutable state, excluded); no full
  extracted client tree (only `game_pak` archive + decompiled UI Lua subset);
  no graph output (graphify builders exist but `graphify-out/` is empty);
  `search_everything`/`trace_references` are bounded text/schema-driven
  (evidence labels `exact`/`heuristic`/`textual` are never overstated);
  `search_physics` covers only the `physical_*` effect tables (no
  collision/geometry data exists in the canonical DB).
- **Development-cycle checkpoints:** the smallest maintainable process is
  deterministic local checks on every archaeology change (before coding:
  source/catalog/version inventory; during coding: source/data cross-reference
  and relationship/acceptance query; before merge: MCP build + focused
  security tests + archaeology stdio smoke; after merge/periodic refresh:
  acceptance dossier and md5/provenance review). The canonical one-command
  pre-merge check is `./scripts/archaeology-cycle.sh` (builds + archaeology
  unit tests + full smoke), run alongside `./scripts/gate.sh`. `./scripts/gate.sh`
  runs the existing BotControl smoke (4/5) plus the **lightweight archaeology
  gate smoke** (`Scripts/mcp-archaeology-gate-smoke.sh`, 24 tools, 5/5 — no
  game_pak/MySQL/archaeology unit tests); the **full** archaeology smoke
  (`Scripts/mcp-archaeology-smoke.sh`) and the archaeology-focused unit tests
  are **not** duplicated in `gate.sh` — they run only in `archaeology-cycle.sh`.
- **Contributor contract:** contributors MUST invoke archaeology when
  investigating/changing source, schema, protocol, client-data,
  quest/objective, item/skill/NPC/mate/vehicle/world/physics behavior, or any
  change depending on a reference-data fact; ordinary unrelated changes MAY
  skip it. Tool/source routing, the evidence contract (HEAD, source_id/path/
  version, query inputs, confidence label, truncation/bounds, canonical DB
  md5, data/code vs live/client/H), and the required pre-merge
  `./scripts/archaeology-cycle.sh` alongside `./scripts/gate.sh` are defined
  in [AGENTS.md](AGENTS.md) "Archaeology MCP — development-cycle checkpoints".
- **Links:** [`AAEmu.ArchaeologyMcp/README.md`](AAEmu.ArchaeologyMcp/README.md)
  and the authoritative data-source inventory
  [`scorecard-explorations/mechanics/archaeology-data-source-inventory.md`](scorecard-explorations/mechanics/archaeology-data-source-inventory.md).

## 2026-08-31 — World-mechanics gap tracks/findings census (read-only dossier + ledger reconciliation)

- **Dossier:** [`scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md`](scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md) —
  data+code evidence only, provenance HEAD `0f8254dc3d914193d432fb842169e9bb07075508`, canonical DB md5 `78b3bdbf038db3b927056106efdf91af` (unchanged), target 1.2 r208022. No gameplay/code/data change, no live/client/H claim; H UNKNOWN everywhere.
- **Four genuinely new high-confidence gap rows added to the SCORECARD global ledger** — **not all the same classification** (all W=0/A=0, conservative, H=U): **AGGRO-PACK-01** (**truly undefined** — `aggro_links` 130 / `npc_aggro_links` 643, 572 NPCs / 126 packs / 111 multi-member; no pack-membership consumer — the AI help path is a distance+faction heuristic only); **RESPAWN-LADDER-01** (data-only/hardcoded mismatch — `resurrection_waiting_times` 10 rows vs `CharacterCombat` hardcoded ladder, siege ladder + 600 s penalty ignored; refines existing **COMBAT-01**, not genuinely undefined); **AUCTION-BANK-DOODAD-01** (**truly undefined** — 2+2 doodad-func rows, spawned kiosk 7983 in arche_mall, no-op/unloaded funcs; hardcoded NPC path unreachable with zero `banker=1`/`auctioneer=1` NPCs); **NPC-INTERACTION-01** (partial/undefined dispatch — `npc_interaction_sets` 111 / `npc_interactions` 114, 142 NPCs / 107 sets; loader/model only, hardcoded interaction menu).
- **BOOK-01 refreshed** from "wiring unverified" to **verified UNWIRED** (books 72 / pages 1206 / contents 1873 / elems 846; `item_open_papers` 551; `ItemImpl.OpenPaper=23` no handler, no book packet, no-op doodad).
- **INDUN-01 formalized** as an existing-dossier ledger row (NOT a new discovery): the read-only roadmap mechanics-gaps audit confirmed INDUN-01 had **zero ledger rows** (mechanic-inventory row-22 "tracked" claim contradicted — real coverage 63/65); row added citing `indun-domain.md` + PB-003 exit E2E 11/11 (layer DATA→E2E-coverage) with Lane D slices S1 bot-party clear-then-exit (Hadir Farm 46), S2 completion hook (45/46/47/50/51/52), S3 cooldown persistence/channel-select/non-blocking loader; S4 phase scripting deferred; H UNKNOWN.
- **Exploration-only:** NPC-GROUP-01 (loaded, zero consumers, non-player-facing — no row). **Medium signals captured, not rows:** `common_farms` data-table gap behind tracked PUBLICFARM-01; `climates`/`zone_climates` partial (growth wired, weather-state absent); `merchant_packs` label-only; `content_configs`/`world_var_defaults`/`world_spec_configs` unknown-semantics data surface.
- **Rejected candidates** recorded in the dossier: INDUN full phase scripting (deferred S4), FISH sports-fishing, PRISON labor/escape, SLAVE naval sub-scope, MAIL 0x0a2/COD, DOMINION combat slices, PVP WAR-HONOR, 70 component-only quests, LABOR regen tick, physics/collision "domain" (no canonical data).
- This narrative update is additive; all historical blocks preserved. No commit.

## Deferred validation gates (bot-backtrack program, 2026-08-12)

Prior human-test waivers are **authorized sequencing, not misconduct**.
Earned engineering evidence stands; these are explicitly deferred
validation. Bots prove function; Josh proves feel. H = actual player only —
scripted-actor/bot evidence is proxy/bot-functional, never H=2. Full table:
ROADMAP.md "Deferred validation gates".

1. **M1 human route** — Josh walks Solzreed (Open Decision #1).
2. **Original M2 human baseline** — two players, no GM repair; Josh-owned.
3. **M3a contract replay** — Phase 2 via M5.1 actions (Plant/Harvest/Craft/PackPickup/PutDown).
4. **M4 economic/navigation replay** — Phase 2 contract replay; normal movement/vehicle controls (direct Transform/ZoneId assignment FAILS the gate).
5. **M6 B4 restart scenario** — Phase 3; bot identity/inventory/position/
   schedule survive restart. **Engineering COMPLETE 2026-08-20** (store
   built + 2-checkpoint replay re-run with direct metadata assertions,
   PASS) — what remains is the full-M6-exit LABEL decision, not code.

## Milestone state

**2026-08-25 wave (e672b9579 → 6ba363a28):**
PB-002 quest-discovery perception primitive LANDED (c1073d883) —
IGameplayActor.DiscoverQuests through the real CharacterQuests.AddQuest
pre-flight chain; offer linkage = QuestActConAcceptNpc/Doodad acts;
canonical smoke vs compact.sqlite3 PASS. First-class InteractWith doodad
contract action (13f502673) — derived use-skill, fail-closed observable-
effect post-check. G2/A5 harness instrumentation merged (4e3004f33) —
dormancy latency ring, bridge seedDormant command, worktree-safe E2eStack,
ScalingProbeTests env tiers, new A5AcceptanceProbeTests +
A4AcceptanceProbeTests. Roam allocation-budget guard widened 512→768B/step
(d989f6639) — zero-margin boundary flake, JIT-variance evidence documented.
**G2-A5 NEAR-TERM GATE MET:** ~100 dormant/~10 embodied constructed via real
paths (live TCP human trigger); RSS +2.09% vs baseline (<15% target);
materialize p95 251.7ms pre-fix / 260.1ms post-PB-004-fix (<3s target).
Evidence: scorecard-explorations/generated/g2-a5-acceptance-report.md §8+§10.
**G2-A4 GATE MET:** autosave p95 393.1ms @ 250 active characters (target
<2000ms; actual headroom 80.3%), 0 save skips — report §9. **PB-003 CLOSED,
premise REFUTED:** exit portals always shipped — static spawns are JSON world
data (Data/Worlds/instance_hadir_farm/doodad_spawns.json 4289/4927),
compact.sqlite3 never held them; party-clear-then-exit E2E PASS 11/11
post-rebase (IndunExitE2eTests, .worktrees/pb003-exit branch pb003/exit-e2e
@ 9e824d34f); ledger flipped FIXED with layer correction DATA→E2E-coverage.
**PB-004 FOUND AND FIXED same day (6ba363a28):** materialized dormant bots
never stepped — no Wake() on proximity materialization + dormancy-only boot
never started the scheduler loop; post-fix steps/min 0→3001 with 10 embodied,
dematerialize-on-leave clean; seedDormant records roam schedule. New engine
finding logged, NOT fixed (report §10.2): pre-existing ManagerOrchestrator
parallel-init boot race (IdManager.GetNextId NRE ← ItemContainer ctor ←
ItemManager.LoadUserItems), one observed crash, rerun green. Navigation
dossier landed: scorecard-explorations/mechanics/navigation-domain.md
(487 lines) — server already loads CryEngine .bai nav graphs + has A* with
three verified defects (wrong zone loader selection WorldTemplate.cs:235-238,
linear nearest-node scans AiGeodataManager.cs:232-314, PathNode G-cost bug
PathNode.cs:226); strategy = compose/harden the existing .bai spine.


**2026-08-25 wave 3 (lane integration, develop @ 9a8fbc8e8):**
**G2-A3 ACCEPTANCE MET (full run):** 1,000-registered-dormant wake-storm
fidelity-transition p99 = 0.00008 ms unstaggered / 0.000061 ms staggered vs
the <100 ms bar — both arms 1000/1000 materialized through the REAL live-TCP-
human → proximity-sweep → materialize path, clean dematerialize-on-leave.
Incremental per-zone/activity counters REJECTED on profiling evidence (sweep
scan pass p50 ≈ 0.066 ms; hot cost is the budget-paced materializer by
design); event-driven human-proximity wake DEFERRED with rationale — full
numbers: scorecard-explorations/generated/g2-a3-storm-report.md §4.2/§5.
IdManager boot race (§10.2 above) FIXED same day: lazy-init guard,
95bf502ad. **Nav G-cost fix measured:** detour factor 1.91×→1.22×, plan avg
6954→1187 ms, rig 81/81 held (nav/slice-gcost @ 7e5d96e74; lazy per-block
.bai spatial grid + heap openSet included). **First autonomous leveling
loop:** LevelingLoopScenario completes the delivery+ItemGather chain
unprompted 254→255, XP +620/+680 through real quest gates
(bots/leveling-loop @ 2a124be70); remaining primitives captured in the
capability-matrix / blockers taxonomy.

**2026-08-25 wave 4 (research + promotion docs lane — six domain dossiers +
master census):** completeness census landed
(scorecard-explorations/generated/mechanic-inventory-2026-08-25.md): 65
canonical player-facing 1.2 systems enumerated against opcode families,
compact.sqlite3 domains, and the 65-manager code surface — 32 tracked, 33
newly proposed; SCORECARD.md now carries all 33 stable-ID rows (ledger
coverage 64/65 ≈ 98%) plus dossier-grounded grade promotions (MERCHANT-01
W=2/A=2 stale-row fix; its three engine defects were then open and are fixed
in the recovery below; CRIME/TRIAL/PVP C=2).
Six domain dossiers landed under mechanics/: **justice** (chain one of the
most completely reconstructed systems — gap is E2E proof, not code; prison
labor/escape genuinely absent), **economy** (vendor loop rig-tested AND
live-proven via m8 reconcile across kill -9; labor regen tick DEAD —
TimedRewardsManager.Initialize has no caller; CSReturnMailPacket real 1.2
value likely unrecoverable), **pvp** (CanAttack chokepoint ordering + honor
formulas reconstructed; duel-bounds mystery solved: flag doodad 5014 +
hardcoded 75 m poll, no geodata ring exists), **dominion** (richest dead-end
domain — complete wire formats + data, zero runtime beyond a hardcoded
DeclareDominion broadcast), **ships** (Jitter2 sailing physics REAL per-kind
tuned; shipyard frames memory-only on restart), **mail** (instance-faithful
attachments; SECURITY finding: 4 of 5 receive paths lack ReceiverId checks).
Indun addendum refuted PB-003's data premise before its E2E closed it.
- Quest-surface branch `bots/quest-surface` (@ 5fe08432e): discovery
  CHANNELS v2 — ~801 previously-hidden quests now perceivable via Item
  (342+25) / Sphere (431) / Level (3) channels + DiscoverSelfQuests
  (ConAcceptComponent deferred = stub) — plus Talk contract action (Talk =
  46) through the real DoTalkMadeEvents pipeline, fail-closed pre/post-checks;
  full unit suite green (2400/2400 + pre-existing skip).
- Kill-leg branch `bots/kill-leg` (@ f369f4c84): hunt objectives
  (MonsterHunt/MonsterGroupHunt) added to LevelingLoopScenario with
  cast-burst rotation, loot-per-corpse, no-progress exclusion; quests 329 +
  1652 complete unprompted (+620/+680 XP); suite 2393/2394 green.
- **Historical Tier-3 shape measurement (g2-a5-acceptance-report.md §11):**
  1,000 dormant seeded through real provisioning / exactly 50 embodied; RSS
  +0.13% vs the 50-active baseline; wake-to-visible p95 280.2ms; steps/min
  parity 15003 vs 14995. The later six-hour canary did execute; see the
  current A5 diagnostic above. Concurrent `seedDormant` corrupts server state
  after ~100 bots (documented; seeding stays sequential). Per-run ownership
  hardening `799b698ad` separately scopes A5/A5Tier3 cleanup to newly observed
  account/character IDs; sibling-preservation tests pass 2/2.

**2026-08-26 wave 7 (validation infrastructure + dominion/pvp/crime
verticals):** **G3-B5 DONE** — behavioral scenario library promoted
**PVP-01 MAJOR FINDING + PARTIAL FIX:** flagged-aggression composed flow
initially FAILED live — handshake + target acquisition passed but zero damage
applied because buff 2423 "LoggedOn" grants ~20 s full damage-immunity at
login, and the immune early-return skipped both HP loss and the crime branch.
The engine fix extracts `RegisterCrimeForAttempt` and invokes it on the immune
path; apply-loop exceptions are logged and rethrown; the E2E waits out the
protection window. Post-fix real damage, bloodstain, and crime chain execute;
ZONE-01 Peace enforcement + homeland mother-shield remain LIVE-verified.
**PB-007 narrow closure (2026-08-27, behavioral gate evidence baseline
`3871459d142fdd1767b9365a1de8d4cd3652ab0e`; current source/test HEAD
`792774d7707b8b578b8d9975896e0a1ac719f361`):** the final isolated live E2E
passed 1/1 in 2m09.910s. `AGGRESS-ALLOWED` observed
victim-matched non-immune `SCUnitDamaged=True`, immune frames excluded=False,
`SkillFired=True`, Retribution 2167=True, and bloodstain doodad 877 objId
44294; the crime branch was observed. `PEACE-BLOCK` passed with no
victim-matched non-immune damage. The deterministic compressed-parser tests
passed 2/2. `WAR-HONOR` remains intentionally deferred (>251 hostile kills
plus the conflict timer); no all-PvP or honor-scope closure is claimed.
**CRIME-01 vertical LIVE-PROVEN:** JusticeCrimeE2eTests
8 stages incl. restart persistence + wanted seam PASS; engine fix MarkDirty()
on CrimePoint/InfamyPoint setters (silent-persistence-vanish bug).
E2E PASS — branch d42e708f5→66f124533. Combat/siege-battle explicitly NOT
implemented (later slices); declare-trigger UI path still UNKNOWN

**MAIL S3 ACCEPTANCE (2026-08-26, `31045d033`):** the authenticated real-packet
E2E `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
passed 1/1 in 2m39s on isolated MySQL/Docker. It exercised real
`CSSendMailPacket`, mailbox proximity, equipment item instance + copper,
kill-9/restart, persisted `SlotType.Mail=5`, receiver ownership retargeting,
unread count 1 after registration, read transition to 0,
`CSListMail`/`CSReadMail`, `CSTakeAttachmentSequentially`, exact item
detail/grade/durability/rune/temper fidelity, copper transfer, and
`CSDeleteMail` persistence deletion. Root cause: `Character.Load` recounted
before world registration; recount now occurs after `TryAddCharacter` and
before human client initialization. Return opcode `0x0a2` remains
STRONGLY_INFERRED pending real-client capture; COD and expiry/bounce remain
open follow-ups.
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
M1 closed on automated evidence (M1-M3 audit t_5b1f5494); human playtest
verdict open (Open Decision #1, pending Josh — C5) — **explicit deferred
gate: M1 human route (bot-backtrack program)**.
**M1 loop-closure reconciliation (source/test baseline
`7a572c08a32162988dedbf400bd9f8b608fb1974`):** the player loop is a clean
Nuian Solzreed progression from quest discovery through ordinary objectives
and turn-ins to the first-mount unlock, with restart persistence checked
separately. `LevelingLoopScenario` provides a bounded autonomous PlayerBot
loop for quests 254→255: `Observe → Discover → legal lowest-level choice →
objective pursuit → turn-in → re-discover`. The focused 254→255 test is 1/1;
`LevelingLoopScenarioRigTests` is 7/7. Evidence is the existing
`scorecard-explorations/generated/leveling-loop-2026-08-25.md` report and its
JSONL trace; this reconciliation does not regenerate either report.
`M1M2ReplayScenario` remains a 16-quest ordered scripted proxy with 55 fixture
actor records, fixture `Level=6` setup, and no real-mount criterion. It does
not establish autonomous decision closure for the full M1 route. Josh's
fresh-character Solzreed route, first-mount summon, restart, Bloody Hand, and
bounty-board checks remain separate H/UAT gates.
**M2 — Golden-path baseline: ✅ DONE on historical G1 evidence; current loop
reconciliation remains proxy/open (2026-08-28)**
M2 redefined (2026-08-10 audit, in ROADMAP) into the M2a–M2d census sweep.
G1 GATE @ 7f5c179f7: 4,579 live = 4,573 PASS + 6 doc-SKIP, 0 unexplained;
full gate 1495/0/1 (t_971d275b / gate card t_4221f85c). Baseline legs
Rei-gated: automated (t_c6eb12ec / t_1998cfd8 PASS), restart (t_cca63225 /
t_c069bacd PASS + live probe t_92a41fe6 2/2), clean-host (t_52755daa /
t_819930ef PASS-WITH-FIXES). Human leg DEFERRED to M4 close (t_46bf9b84) —
**explicit deferred gate: original M2 human baseline, Josh-owned
(bot-backtrack program; bots may stand in for the AUTOMATED baseline only,
never H=2)**.

**M2 loop-closure reconciliation (A/R, source/test HEAD
`ba530bcebec12af2bc7dc0db7451a535665bbed3`, 2026-08-28):** M2's player loop
is defined as **clean reset → ordinary golden-path baseline
quest/progression → required first-mount/baseline state →
restart/clean-state persistence verification**. The focused deterministic
normal-clone command was
`dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter '/*/*/<Class>/*'`,
run with `<Class>` replaced by `HeadlessSessionProvisioningTests`,
`M1M2ReplayScenarioRigTests`, `M1M2ReplayCastWindowRigTests`,
`PlayerbotPilotTests`, `QuestScenarioTests`, `QuestScenarioTierTests`, and
`QuestDataCensusTests`. The aggregate was **32/32 passed, 0 failed, 0
skipped**: 8/8, 3/3, 1/1, 6/6, 12/12, 1/1, and 1/1 respectively.

This is A/R deterministic fixture/proxy evidence only: `PlayerbotPilotTests`
completed 30/30 cycles and its restart check passed 2/2, but both are ordered
manifest/contract replay. `M1M2ReplayScenario` is a fixed 16-quest ordering;
its declared mount criterion reports **no real mount** in the headless fixture.
`QuestScenarioTierTests` itself passed 1/1 because every manifest ran and the
report was written; its observed per-quest census is **4463 PASS / 110 FAIL /
14 SKIP over 4587** (4463/4573 runnable), including T1 failure quest 6280.
The remaining census results are historical evidence findings, not an M2 full
closure claim. No live server, client, or human/H evidence is implied.

The loop matrix therefore remains **Player closes loop = Unknown (H open)** and
**Bot closes loop autonomously = Unknown/Open** for M2: the current evidence is
ordered-manifest/contract replay and contains no `Observe → Discover →
legal-choice` decision closure for the M2 baseline. The original two-player,
no-GM human baseline remains an explicit Josh-owned deferred gate.

**M3a — Homestead shell: ✅ CLOSED on scripted-actor (proxy) evidence (2026-08-10, Rei gate t_449875bd ACCEPT; H reconciled 2026-08-12)**
Merged @ 4d0427b96; two-player exit via M3aExitScenarioTests (M5-stand-in:
2 scripted actors, adjacent 16m, ONE session — placement → construction →
crops → storage → furniture). Scorecard HOUSING-01 / FARM-01 C/W/A = 2;
**H = U (proxy/bot-functional only — scripted actors; H UNKNOWN until Josh
runs it; M3a contract replay = explicit deferred gate)**.

**M3b — Property persistence and recovery: ✅ CLOSED (2026-08-11, EXIT gate t_accb1c63 PASS)**
M3b-1..4 merged (5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea); EXIT E2E
f5b00c686 PASS 7m08s — N=3 crash cycles incl. kill -9 mid-save (INNODB_TRX-
observed) + container kill, 16 rows/boot, no loss/dup; autosave p95 1301ms
< 2000ms at 25 bots + 2 homesteads. PROPERTY-01 R = 2 (U→2 in f5b00c686).

**M3 loop reconciliation (2026-08-28; current source/test HEAD
`a77ef878d8fcba297c32c0228e712e0695cc4887`):** M3a's clean ordinary
Character loop is **place/build → plant/harvest → storage/coffer/furniture
state → observable ownership/contents result**; M3b restart persistence is a
separate loop. The prior exact source/test baseline `b9a72825f` recorded the
M3 focused aggregate **178/178**, with named slices M3a exit 1/1, M3b furniture
4/4, phase restart 10/10, property policy 11/11, and repair scanner 13/13.
Source commit `1a3f13dc1` is included at the current HEAD; the focused
`HousingStorageFurnitureTests` run is **13/13**, including unauthorized
coffer refusal before `OpenedBy` mutation and authorized-owner opening.
These are scripted/fixture proxy stages: property replay follows an ordered
sequence and fixture setup includes direct `SetPosition`/service preparation,
which is not an acceptance path. Player/H UAT remains open; no live-client
claim is made.

**M4 — Trade, crafting and transport integrity: ✅ MERGED + DEPLOYED (2026-08-12; H reconciled 2026-08-12)**
Pinned audited SHA **95bb1c78e** (merge: M4 EXIT integrated playable release,
t_97e59ffc, **Rei gate PASS** t_abe87eaf ACCEPT) — crafting integrity
(bag-scope material check + level-10 pack gate) + trade packs + vehicle
lifecycle + integrated session evidence. **PROD DEPLOYED** to CT 133 by Mai
(t_442f3016): image `aaemu-game:presence-demo` @ 6d5a07cf49a5 built from
pinned 95bb1c78e; rollback tag `presence-demo-rollback-pre-m4` (3ddcf7a4bdbc);
prod startup test PASS 37 min (0 restarts, 0 FATAL, 3/3 bots roaming, real
client accepted); manifest deploy/m4-manifest @ 03d3442bd (deliberately NOT
develop — fork develop carries M5-lane content). Gates: unit 1778/0/1;
M4ExitIntegratedSessionTests (4 scripted actors: harvest → craft pack →
slave cargo → 3-leg route → sell, 2× 124540 mails); restart E2E kill -9 PASS
(2m12s/3m09s/7m03s); CRAFT-01 / PACK-01 / SLAVE-01 C/W/A/R = 2, **H = U
(proxy/bot-functional only — scripted actors; H UNKNOWN until Josh runs it;
M4 economic/navigation replay = explicit deferred gate)**. Human playtest of
the integrated release remains the deployment-lane follow-up pending Josh GO.

**M4 loop reconciliation (2026-08-28; current source/test HEAD
`6ff68e1bb4a6afe08441308acb9a485b5133c42e`):** The clean ordinary
`Character` loop is **gather/harvest → craft pack → carry/place → load owned
vehicle → drive normal route → unload → sell specialty pack for reward →
repeat**, with per-object restart/persistence as applicable.
`SellSpecialty` now uses the canonical
`CSSellBackpackGoodsPacket → SpecialtyManager.SellSpecialty` path, with
ordinary merchant/pack checks, pack-consumption postcondition, same-zone and
no-pack refusal, repeat-cycle, and idempotency coverage. Focused results:
`M4ExitIntegratedSessionTests` 2/2,
`EconomyDayCycleScenarioRigTests` 4/4, and
`M3aM4ReplayScenarioRigTests` 2/2. The full normal-clone gate at this
source/test HEAD is **2498 total / 2497 passed / 0 failed / 1 skipped**;
compiler **0/0**; MCP **39 tools**. The skipped
`Provision_Activate_Persist_Deactivate_RoundTrip` requires
`AAEMU_LIVE_RIG` and `AAEMU_E2E_DB_PASSWORD`. A forced rebuild reported
1067 warnings / 0 errors. M4 Player/Bot loop closure remains
**Unknown/Open**: replay is ordered scripted/fixture proxy and direct setup
shortcuts are outside authentic acceptance. No live M4 restart/vehicle proof
was run because the shared E2E reset is unsafe. Human/client QAT remains open;
historical evidence is preserved.

**M5 — Gameplay Actor Contract: ✅ COMPLETE (2026-08-17 sync 2026-08-20)**
A1 (marshal bot steps onto the game loop — the M6-exit-blocking retroactive
fix) + B1 core action surface (Interact · Loot · UseItem · Mount/Dismount ·
AcceptQuest · TurnInQuest, each through real engine paths) merged to fork
develop @ 761d1e81a (Rei gates t_d06d8dd9 / t_ebfc9b35; merged-tree re-verify
1850/0/1 via Phase 3 t_9340e85d). **M5.1 economic extension — ALL MERGED:**
Plant (t_b1d7c430) · PackPickup/PutDown (t_64ecf525) · Buy/Sell (t_8741b03d) ·
control-plane API (t_7b6d7a4b) · MCP sidecar (t_446228b5) · first consumer
(t_52b2b084) · salvage wave Deposit/Withdraw (t_78ce17a2), Harvest
(t_234da01a), BoardVehicle (t_15343fdd), Craft rig+impl (t_6b5ac43e,
t_cffb71ad) — all done per kanban. Phase-2 prerequisites LoadPackOntoVehicle
(t_a7756a00) + DriveVehicle (t_eaf1754d) merged @ 6c2429ae0 + 6edbf0cbb.
Housing.Build = M5.2 merged @ 3396d9ef1 (Rei gate t_ebf36737 ACCEPT).
**M5.3 core surface (Observe/Move/Stop/Target/Cast): MERGED 2026-08-17 @
6b4ffe1d2** — canonical verification + exit scenario (t_c73d6293), Move rework
(8e9c0713a), SetTarget broadcast + ExecutionBoundary rework (t_09e1c671, Rei
gate t_5fa9bd73 ACCEPT), full gate 2102/0/1 on merged tree. hytest GM kit
(level/labor/gold/portals) merged @ 782ac3b3c (t_e1cf82c9, Josh ruling
2026-08-17) + .teleport mirage (t_42e24eca) — human-test fast-forward lane
LIVE. BACKTRACK Phase 1 (t_61a0eebb + full-route follow-up t_15787275) and
Phase 2 (t_b4f455b0) both DONE.
**H = UNKNOWN** — proxy/bot-functional evidence only; the five deferred human
gates below remain Josh-owned.

**M5 actor decision/action loop reconciliation (2026-08-28; source/test
checkpoint `9ddc322feee4f06c55df9f429e8da3ed573c1b85`):** The loop sentence is
“a clean ordinary `Character` observes current state, chooses one legal
objective/action, executes via `IGameplayActor`/normal `Character` services,
observes terminal state/audit, and retries safely without duplicate effects.”
`GameplayActor`/the M5 contract proves the request lifecycle, single-writer
gate, failure taxonomy, timeout/stuck handling, idempotency, and audit; focused
tests are **316/316**: `BotGoalArbiterTests` 14/14,
`GameplayActorM53CoreSurfaceTests` 13/13, `PlayerBotControllerAdapterTests`
5/5, `GameplayActorB1ContractLayerTests` 17/17, and `GameplayActorTests`
30/30. This is actor-contract evidence, not universal bot decision closure:
`LevelingLoopScenario` closes only the narrow autonomous 254→255 choice/pursuit
slice, while `BotScenarioRunner`/`M1M2`/`M3aM4` remain ordered proxy replays.
Player closure is **Unknown/H or client-gated where applicable**; the universal
PlayerBot decision loop is **Unknown/Open**. Fixed Priority-first
`CanActivate`/FSM scheduling exists, but no reusable candidate/score/blackboard/
rationale/replan/personality policy is claimed. Existing M5.3 canonical
movement caveat and formal regrade wording remain unchanged; H stays separate.

**M6 player-loop reconciliation (2026-08-28; current source/test HEAD
`da0fdc61a72a15111fddc8ac627a164a5f050558`):** A clean ordinary
`Character`/bot becomes dormant → proximity wake/materialize → resumes its
scheduled action → preserves identity, inventory, position, and metadata
through restart → dematerializes safely. `c97909f4f` isolates the baseline
presence roster before dormant seeding; `950cfd279` provides cooperative
bridge cancellation; `155c82c66` adds the natural six-hour dormant-timer stage
with explicit controls, cooperative deadline, and ID-bound cleanup. Focused
M6 evidence remains **105/105**. Corrected Tier3 readiness at
`4721cbd306cbf346bfe38b7373d5adf479b6231f` is historical 1/1: 1000 seeded,
embodied 50, dormant 950, materialized 50, p95 259.2ms, RSS +2.56%, 50
dematerialized, owned cleanup zero. The six-hour canary DID execute and
produced `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json`
for 360.00003 minutes / 361 samples: DormantSpecs 1000, embodied 0,
materializations/dematerializations 0, queues/failures/save-skips 0, DB writes
delta 0, tick max 19.1ms, save p95/max 85.7/100.8ms. RSS assertion failed on
the pre-quiescence baseline (1207.9MB baseline, 5749.4MB peak, 3744.1MB final,
budget +512MB), not yet classified as a leak. Testing/canary diagnostic
failure only, not an A5 pass or live/human evidence. `ccd4ea857` corrects
warmup/baseline/peak handling; a preferably 12-hour rerun remains pending.
**Corrected 12-hour testing/canary soak (2026-08-30, SHA
`1ce4664f96705850136dc9d46999070fac9763fb`):** FULL — 720.000044 minutes /
721 samples; DormantSpecs 1000/1000, embodied 0, materializations/
dematerializations 0, scheduler queues/failures/save-skips 0, DB writes 0,
RSS baseline 2383.4MB, startup peak 2471.3MB, steady peak 2539.3MB, growth
155.9MB < 512MB. `passed=false` on timing only: seven distinct breach samples
(six failure strings after dedupe) — region 299/288/308/291/297/291ms (budget
200) and tick max 282.9ms (budget 250). Triage: 571 ActiveRegionTick overruns
in the 12h game log (570 in window), recurring ~76s cadence, 566/571
same-second with physics-slow warnings, deferred 0 characters; physics
278–554ms tracks the stalls. Classification **UNKNOWN / host-level
physics-thread stall versus scheduler/host steal** — not a confirmed code
regression, not a pass. Next: isolate/inspect testing-host CPU steal and
physics-thread diagnostics, bounded timing reproduction; budgets not relaxed.
Warmup correction validated; timing triage remains open. Evidence is
testing/canary operational, not live human gameplay.
**M7 player-loop reconciliation (2026-08-28; current source/test HEAD
`ded008de8d67ece8718e9235fd02503b43ceb6a1`):** An ordinary
`Character`/PlayerBot discovers and accepts a quest, navigates, chooses legal
hostiles, casts, receives kill credit, loots, sustains/retreats, and
completes/repeats; the group variant adds party invite/follow/assist/death
recovery. Focused M7 evidence is **147/147** with no failures or skips:
primary **36/36** (Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4,
PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support
**111/111**. Evidence is A/R rig/proxy: hunt kill uses real
`DoOnMonsterHuntEvents` with fixture HP=0; Party spike is synthetic/fixture.
There is no current live authenticated-client run and no H/UAT result.
`LevelingLoop` remains bounded autonomous 254→255 only. Broad autonomous
decision closure, real damage/`Npc.DoDie`, scheduler-driven route, party
roles/regroup/restart/disconnect, mount/travel, and H remain open.

**M7 — Adventurer and party bots: 🔶 GATING SPIKE DONE (2026-08-20) + heal/retreat landed**
One adventurer cleared quest 250 (Solzreed fox cull) end-to-end through the
M5 contract — accept at the real board → travel → hostile select (CanAttack)
→ burst Cast rotation → 3/3 REAL kills → 3 corpse loots → quest complete.
Rig 4/4 green + E2E PASS 1/1 2m15s (37 trace records; evidence
m7-adventurer-spike-report.json). Gate 2125/0/1. Spike found BUG-016
(18131-class melee skills never hit their primary target — FIXED same day,
census 415/13, 18131-led combo-chain rotation is the live regression) +
leash-reset/mana realities now recorded in ROADMAP M7. **Adventurer v1
sustain (heal/retreat) landed 2026-08-20**: the hunt loop checks vitals
before engaging — below threshold (0.35) the bot retreats along the
threat→bot vector, recovers (configured heal item through the real UseItem
path when bagged; out-of-combat regen fallback), and re-engages at 0.8 —
bounded rounds fail CLOSED with Starvation. Rig E-M7-3/4/5 green; live
exercise awaits level-appropriate content (foxes can't hurt the level-50
spike bot — recorded). Potion data note **CORRECTED 2026-08-23** (the old
"no direct-heal potion" claim was WRONG): direct-heal potions DO exist —
8515/8518/15580/15581 verified in canonical compact.sqlite3 —
HealItemTemplateId default flipped 0→8518 (c98da8a53).
Spike shortcuts on the record: level-50 provisioning, straight-line Move (no
pathfinding), death/resurrection (**RESOLVED 2026-08-20** —
scheduler death watch + CharacterResurrection, see M6 exit blockers below).
**Distance maintenance landed 2026-08-20**: the hunt loop keeps a standoff
band [StandoffMin, EngageRange] before the cast burst — too far closes in
(melee default: straight onto the unit, the proven live behavior; ranged:
to the band edge, never face-planting the target), too close (ranged
StandoffMin > 0) backs off along the threat→bot vector. Rig E-M7-6/7 green
(band-edge stop distances asserted through a position-recording runtime);
melee defaults byte-identical in behavior (EngageRange 3 = the old
hardcode). Rig gotcha recorded: Character.MaxHp computes from Level via
FormulaManager — rig tests that set Level must also refill Hp or the
sustain loop fires on every run.
**Equip contract action landed 2026-08-20** (M7 equip-upgrades
prerequisite): `IGameplayActor.Equip(itemTemplateId)` moves a bagged item
into equipment through the real CSSwapItemsPacket Inventory→Equipment path
(Inventory.SplitOrMoveItem, SwapItems task) — the engine's
EquipmentContainer.CanAccept validates slot compatibility before anything
moves, and the slot pick uses the engine's own GetAllowedGearSlots table
(first EMPTY allowed slot, else first allowed = client equip-over-occupied
swap). Full idempotency discipline (same-key pre-flight refusal + fresh-key
engine backstop). Rig 7/7 (GameplayActorEquipTests). Engine gap on record:
no level/requirement gate on the equip path (CanAccept checks slot only).
Rig gotcha recorded: SeedEquipItemTemplate must run AFTER CreateActor
(ItemManager is DI-only, seeded by the rig's Seed()).
**Equip upgrades landed 2026-08-20**: the spike hunt loop evaluates bagged
equippables after each corpse loot and equips upgrades through the Equip
contract — the upgrade rule mirrors the contract's own slot pick (first
EMPTY allowed slot else first allowed; equip when the slot is empty or the
candidate's template Level beats the occupant's), and level discipline
lives in the scenario (LevelRequirement ≤ bot level; the engine has no
equip level gate). Rig E-M7-8 green (two equips through the contract, the
equal-Level third sword stays bagged); live no-op honesty: fox loot is
flavor, so the stage records nothing on the live stack unless real gear
drops.
**Live failure found + hardened 2026-08-20 (E2E rebuild run):** a fox
pinned at full 217 HP across 100+ successful casts (leash-stuck class —
damage never lands) starved the hunt at 2/3 in 150 attempts. The hunt loop
now carries a NO-PROGRESS SKIP: a target that takes zero net damage across
NoProgressSkipRounds (3) executed-cast rounds is excluded from reselection
(exclusion only, never a kill credit; HUNT-SKIP stage) and the hunt moves
on. Rig E-M7-9 (one unkillable fox of four — skip fires, cull completes
with the healthy three). Open question on record: WHY a freshly spawned
fox can enter the pinned-HP state at all (cold-boot correlation: both
rebuild-run E2E failures showed it, warm runs never did). **Follow-up
2026-08-22:** an isolated forced-rebuild E2E cold start passed 1/1 in
2m57s: 3/3 foxes took damage and died, with no `HUNT-SKIP` and no observed
return-home/full-HP reset. The first cast samples can remain unchanged while
effects are scheduled asynchronously, so they are not alone evidence of a
damage failure. The original failure is therefore **not reproduced and root
cause remains UNKNOWN**; no speculative Npc AI change was merged. A second
repeat stalled in local harness startup before it produced a scenario
report, so it is not gameplay evidence.
**Return-to-NPC leg landed 2026-08-20**: the spike is now the M7-worded
short quest chain — after the 250 cull completes, the bot travels to the
quest-330 acceptor (golden route §1a step 3: Npc 3597, no objectives,
report Npc 3511), accepts through the real AddQuest gate, travels to the
report NPC, and turns in through the real packet path, draining the step
machine to completion (M1M2 replay shape). Rig E-M7-10 green (both quests
completed-and-dropped; contract vocabulary gains the second accept + the
turn-in). Rigs default the leg off (ReturnQuestId 0 keeps the one-quest
shape); live defaults run the chain.
**Adventurer v1 feature list COMPLETE 2026-08-20** — targeting, skill
priority, distance maintenance, heal/retreat, loot, equip upgrades,
return to quest NPC, death recovery all landed. Party v1 open.
Scheduling unblocked per the roadmap's spike gate. H UNKNOWN.
**Party v1 slice 1 landed 2026-08-21** — invite/join contract actions
(PartyInvite = 34, PartyAccept = 35) on IGameplayActor + the
PlayerBotControllerAdapter, through the real engine paths:
TeamManager.AskToJoin via the target-object overload (the exact
CSInviteToTeamPacket call, skipping the global name registry so headless
rigs resolve) and TeamManager.ReplyToJoinTeam (the exact
CSReplyToJoinTeamPacket call; invitation.TeamId 0 → engine CreateNewTeam,
else AddMember on the inviter's team). The engine's refusals on both
paths are SILENT voids, so the contract pre-flights (pending invitation /
already a member / no pending invitation → StateTransition, engine never
entered) and post-checks the observable outcomes (invitation record for
invite; Character.InParty + active-team membership for accept).
TeamManager.GetActiveInvitation went private→public (the invitation
record IS the observable outcome the actor must inspect). Rig upgrades:
the TeamManager seed now wires real ChatChannel instances and
incrementing team ids (bare mocks NRE'd CreateNewTeam's chat wiring and
collided every team on id 0), FriendMananger is seeded with an empty
friends table (Character.InParty's setter NRE'd headless), and
JoinActorWorld moves a second actor's character into the host session's
world (each CreateActor gets its OWN world; a party needs one). Rig
GameplayActorPartyTests 6/6 green. Follow-up slices: follow leader /
assist target (scenario surface), then the M7 party spike. H UNKNOWN.
**Party v1 slice 2 landed 2026-08-22** — `PartyFollowAssistScenario`
composes existing actor actions instead of growing the contract surface:
the member verifies it and the leader are active members of the same real
party (and that the supplied leader is the team's owner), follows through
`MoveToUnit`, then copies the leader's ordinary `CurrentTarget` through
`SetTarget`. The scenario fails closed for wrong party/world/leader state
or a leader without a target. Rig `PartyFollowAssistScenarioRigTests` 4/4
green: distant follow + assist, in-formation hold + assist, non-party
pre-flight refusal, and no-target refusal. Full gate 2163/0/1. Remaining:
M7 party spike. H UNKNOWN.
**M7 PARTY SPIKE COMPLETE 2026-08-23 (c98da8a53)** — `PartySpikeScenario`
(template m7-party-spike): a real 3-bot party completes rally → assist →
kill of elite NPC 1870 inside the leash window — live E2E PASS over the
generalized multi-actor bridge seam (`HandlePartyFollowAssistScenario`
generalized to N actors). Causal cast-effect traces landed in the same pass:
ActorAuditRecord v2 additive fields target_hp_before/target_hp_after,
effect_observed, effect_wait_ms (delayed effects now distinguishable from
failed hits). **Party v1 feature list COMPLETE — Adventurer v1 (2026-08-20)
AND Party v1 both done; scheduling unblocked.** H UNKNOWN.

**2026-08-23 mechanics sweep (8c198f13d → f3bb787ce, all pushed, gate green
at each step):** C2 social v1 — BotChatterService, 8 archetypes × 4 lines,
cooldowns/budgets/combat-suppressed, default OFF (Bots.EnableChatter /
AAEMU_BOT_CHATTER_ENABLED); movement stuck detection — NoProgressWindow 2.5s
→ TimedOut(Navigation) "stuck" + one unstick nudge. C1 schedules v1 —
BotScheduleService Home/Work/Travel/Rest phase machine w/ hysteresis,
persisted additively inside the schedule JSON blob (B4 byte-equality
preserved), default OFF (Bots.EnableSchedules / AAEMU_BOT_SCHEDULES_ENABLED);
economy day-cycle v0 (m8-economy-cycle-v0) — buy seed→plant→harvest→craft→
sell→deposit with explicit ledger + reconciliation laws, live E2E incl.
kill -9 restart ledger-equality PASS. Engine/harness hardening — equip
level_requirement gate in EquipmentContainer.CanAccept (engine hole closed);
M51AttachedPackRestartE2eTests closes the M5.1 attached-pack GAP FLAG
(survives kill -9, PASS ×2); M3aM4ReplayScenario warm-world AuditTrace.Last
crash fix; E2eStack.CleanupBotRows SQL IN-list fix; silent-catch sweep
80→0. A6 manifest provisioning — presence_manifest.json (Bots.PresenceManifest
/ AAEMU_PRESENCE_MANIFEST), AAEMU_PRESENCE_MAX_BOTS clamp default 10;
QuestActEtcItemObtain credit path (~51 live quests fixed); hauler leg on the
economy cycle (pack craft→LoadPackOntoVehicle→DriveVehicle→gold trader→
deposit); mail ReturnMail + expiry bounce/destruction semantics
(CSReturnMailPacket opcode confirmed 0xfff placeholder — NOT registered).
Trade functional (TRADE-01): OkTrade cancel-then-finish KeyNotFoundException
fixed; both-locked AND both-ok gate (was !a && !b single-side exploit);
TradeOffer/TradePutup/TradeLockOk contract actions;
TradeHandshakeScenarioRigTests 5/5. Auction expiry sweep hardened (per-lot
isolation, null-safe missing-item expiry, mail-fail no longer wedges lots,
_auctionTaskScheduled guard); AuctionHouseRestartE2eTests PASS (post→buy→
settle→expiry-mail across kill -9, 3m26s); E2eStack.RestartGameServer(afterStop)
seam.

**2026-08-24 sweep wave 2 + soak STAGE 1 (f3bb787ce → 3a534b539):**
G3-B3 DONE — IBotActivityModule + BotGoalArbiter (priority-based single-active
activity per bot per wake; schedule-phase P100 / presence-roam P50 / idle P0
first modules via IBotStepExecutor decorator); dead PlayerBotBehaviorController
stack deleted; fixed latent DI gap so Bots.EnableSchedules actually arms
BotScheduleService. Mechanics slices (0482ba3f0) — ITEM-01: item_proc_bindings
loaded + GetItemProcBindings, UnitProcs factory seam (items can carry procs);
MATE-01: mate_equip_packs/pack_groups/pack_items/slot_packs loaded in
MateGameData, fail-closed IsMateEquipAllowed legality at
MateEquipmentContainer.CanAccept, latent EquipmentContainer null-Owner
level-gate bypass fixed for mates; HOUSING-01 FIX-2: terrain/overlap/cap/race
checks verified as landed + two-thread build-race regression via real
HousingManager.Build; ZONE-01: hard-coded Conflict boot state removed →
data-driven Peace default (legacy World.ConflictZonesStartAtConflict flag kept
for tests), Peace-state PvP protection at the BaseUnit.CanAttack chokepoint
(fail-open when no conflict entry; Hostile stays attackable). TRANSFER-01
functional + LIVE PROVEN (3a534b539) — CSBoardingTransferPacket TlId shadowing
FIXED (multi-part transfers share the master's TlId but seats exist only on
child parts; FirstOrDefault always resolved the seatless master — boarding
could never bond); read-only `transfers` bridge dump command;
TransferRideE2eTests LIVE PASS (board Marianople Gondola tlId=1 ap=2
BondChairDouble → ride route samples → disembark at current position).
Scheduler-driven soak STAGE 1 executed (4e460305b) — SchedulerSoakStage1Tests,
10 manifest-provisioned citizens × 30min through real IPlayerBotScheduler
wakes; TWO VALID runs: ~90k steps, 0 failed/timed-out, wake avg ~99ms, DB
writes 14–19/min/citizen, tick+region budgets PASS. Engine findings on record:
(a) manifest roster entries without home spawn start at race-template position
but walk to the patrol-default Nuian home (run-1 elves walked 4.3km and
drowned); (b) physics slow-thread rate ~3× scheduler-disabled baseline
(0.23–0.27/min vs calibrated 0.031–0.067) — same-world clause far inside
budget, recalibration = M6-exit decision; (c) heap churn to ~5.9GB under roam
vs flat 3.4GB band.
**Soak follow-ups landed (615a645c9 / 2703fd46e):** (a) FIXED — manifest
route anchors at ACTUAL spawn when no explicit/persisted home (regression
test in BotPresenceManifestTests); (c) roam heap churn cut 38%/wake —
BroadcastMovement opt-out skips the per-apply movement packet for headless
roam (throttled executor broadcast remains), shared WalkDelta payload;
BotRoamAllocationTests pins 512B/wake (pre-fix 789B). **Run 3 on fixed build:
ALL budgets PASS except physics warnings 0.17/min vs 0.1** (same-world 2/30,
worst pass 110ms vs 40ms) — **OPEN JOSH DECISION:** recalibrate per-minute
aggregate (~0.3/min or severity+same-world-only) per t_18fccd09 precedent.
Remaining engineering follow-up: Transform.FinalizeTransform allocation per
move (engine-wide, benefits players too).

**2026-08-24 mechanics rounds 3-5 (0482ba3f0 → 0d1a6b8fe):**
Round 3 (0482ba3f0) — G3-B3 DONE: IBotActivityModule + BotGoalArbiter
(single-active activity per bot per wake; schedule P100 / roam P50 / idle P0;
dead PlayerBotBehaviorController stack deleted); ITEM-01 item_proc_bindings
wired; MATE-01 equip-pack legality; HOUSING-01 race regression; ZONE-01 Peace
boot + CanAttack enforcement. Round 4 — TRANSFER-01 fixed + live-proven
(TlId shadowing); soak STAGE 1 executed + run 3 verified home/churn fixes;
physics recalibration = OPEN JOSH DECISION (~0.3/min case recorded). Round 5
(cab6e4dc9 → 0d1a6b8fe) — M1 Lane-B: ConReportJournal wired-noop FIXED (466
quests auto-passed the journal gate; 59 instantly completable) +
ConReportDoodad FinalizeQuest double-subscribe leak fixed; golden-route
replay re-PASS; full 50-act audit clean except ConAcceptComponent (NOVEL-
MECHANICS, Josh design call). FISH-01: dossier + CastAt(position) contract
action + FishingVerificationE2eTests LIVE PASS (labor/worm/proficiency/loot
through plot 809); DoodadFuncBuyFish double-credit fixed. C7: expedition
contract actions ExpeditionCreate/Invite/Accept/Leave + lifecycle rig. C9:
duel rig found & fixed stuck-duel bug (RestoreFaction NRE in stop catch-all
left both players permanently IsInDuel). M7#2 party lifecycle fault matrix
(4 rigs: member/leader death → membership preserved through real
CharacterResurrection; invitation retry; target-loss fail-closed) — surfaced
+ fixed null-killer environmental-death NRE in Unit.DoDie/CharacterCombat.
M7#6 npc state telemetry: aggro-change snapshots (hp/pct/top-aggro) +
return-home entry Debug logs — fox pinned-HP diagnosable without speculative
AI changes.

**M6 exit blockers (as of 2026-08-20):** physics-warning regression
t_eecc5604 ✅ done · adopt-heal fix t_555ed207 ✅ done (merged; prod
re-provision verified by presence deploy chain) · **B4 playerbot_metadata
store ✅ done 2026-08-20** · **6.2 death/resurrection ✅ done 2026-08-20**
(CharacterResurrection core shared with the packet path + scheduler death
watch: dead bots stop getting work steps, poll, resurrect at the nearest
return portal after a 5s delay with the real 10%/debuff semantics,
server-side relocation through Character.SetPosition, then normal stepping
resumes — 5 rig tests green) · PlayerBotScheduler
scheduler-driven soak still open if M6 exit mandates it. **(STAGE 1 EXECUTED
2026-08-24 — see 08-24 sweep above: 10 citizens × 30min, ~90k steps, 0 failures,
budgets PASS; three open engine findings; full exit-label decision incl.
physics recalibration still open.)** **Exit-label note
(reconciled 2026-08-12, bot-backtrack):** soak verdict = "passed revised
approved budgets" — full M6 exit label NOT claimed; **B4 restart-persistence
scenario = explicit deferred gate**.

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

**G2-A4 save path (1,000-bot item): 🔶 implementation MERGED — acceptance measurement open**
SaveManager dirty-tracking merged to fork develop @ 5ed5d6493 (2026-08-10, t_8c18eb1c,
Rei gate t_53025996 ACCEPT): the periodic autosave now persists ONLY dirty characters
(Character.IsDirty/MarkDirty chokepoints; SaveManager.GetCharactersToSave;
DoSave(saveAllCharacters) force-all on shutdown + /save), closing the Kimi audit
finding "DoSave full-table sync save on every cycle" (t_0fda3cd3 → ROADMAP G2-A4).
Evidence: SaveManagerTests 10/10 (incl. 1,000-character simulated load), merged-tree
gate 1575/0/1, M2bE2e restart-persistence 5/5 (t_2ee39438 — disconnect save path
untouched). Remaining for A4: autosave p95 < 2s at 250 characters + zero _isSaving
skips at the milestone gate.

## E2E gates (GateSoakRunner, real Login+Game+MySQL, canonical data — evidence /root/aaemu-e2e/logs/):
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
| Builds | Tai | M4 prod image 6d5a07cf49a5 live (t_442f3016); M5.1 salvage wave next (Deposit/Withdraw t_78ce17a2 → Harvest → BoardVehicle → Craft split); M5.2 Housing.Build t_94761d55 running | 🔶 running |
| Verifies | Rei | M4 gate PASS t_abe87eaf; M5 gates t_d06d8dd9 / t_ebfc9b35 done | ✅ done |
| Dispatches | Mai | M4 deploy to CT 133 + prod startup verification (t_442f3016) | ✅ done |
| Tracks | Nei | 08-13 (t_c9f0d7f6): M5.1 recovery sync — salvage order + Phase-2 prereqs + Housing.Build scope mirrored across ROADMAP/STATUS/SCORECARD/progression-board/wiki; branch of record 983b35736 (ls-remote verified) | ✅ this card |

## Open tasks (kanban, AAEmu lane)

**2026-08-20 cleanup sync (Kimi, Josh-directed):** every AAEmu card listed
here on 08-13 is now `done` in kanban — including the M5.1 salvage wave
(t_78ce17a2 / t_234da01a / t_15343fdd / t_6b5ac43e / t_cffb71ad), M6
regression t_eecc5604, adopt-heal t_555ed207, harness extension t_f198bb0e,
verifier stub-registry t_913c1d4a, authority envelope t_5999b370 (ACTIVATED,
closed t_b1002aad), and both backtrack phases (t_61a0eebb / t_15787275 /
t_b4f455b0). Human test packet t_2b654349 delivered. No open AAEmu-lane
engineering cards remain.

**New 2026-08-20 — production defect found + fixed (this pass):** a stray
half-closed LAN client wedged the stream-port (:1250) receive loop into a
zero-progress spin (~20k ERROR lines/sec, 174% CPU) because PacketStream
over-reads log-and-return-0 instead of throwing, making packetLen == 0 on a
1-byte remnant. Fixed on branch fix/protocol-spin-guard: truncation guard +
malformed-length close in Stream/Game/Game-side-Login protocol handlers +
5 regression tests. Prod mitigation: game container restarted 2026-08-20
(spin cleared, boot clean, 0 FATAL).

**Known deployment gap:** prod CT 133 image 32978f3613e3 = develop @ ~81676c0d6
(08-17 teleport-mirage). Missing from prod: M5.3 rework (6b4ffe1d2), hytest GM
kit (782ac3b3c), spin-guard fix. Rebuild + redeploy recommended before QAT.

**Remaining Josh-owned (deferred gates, bots cannot substitute):**
M1 Solzreed human route · original M2 two-player baseline · M3a contract
replay · M4 economic/navigation replay. hytest GM
kit + .teleport mirage + GM access (t_01a893c7, deployed t_d8658d50) are the
fast-forward lane for these. (M6 B4 restart scenario engineering completed
2026-08-20 — the remaining piece there is the M6 exit-label decision; the
B4 line item is now FULLY closed: metadata store + audit-trace flush.)

**New 2026-08-20 — B4 playerbot_metadata store (this pass):** the M6.0
metadata list (personality/schedule/profession/home/behavior/planner state)
now has a table and a store — `PlayerBotMetadataStore` (self-healing schema,
write-through REPLACE on mutation for hard-kill safety + dirty flush in the
SaveManager transaction), presence demo resolves home explicit-env →
persisted → template and records home + roam-loop schedule per bot. B4
restart replay extended to assert metadata directly: PASS 1/1 (4m39s,
evidence gate-m6-reconcile-b4-20260820-162058.md). Full unit gate 2121/0/1
(+15 store tests). Branch feat/b4-playerbot-metadata. NOT yet deployed to
prod — presence-demo home resolution gains a persisted fallback, so prod
deploy is a separate Josh decision.

## Legacy upstream item (predates one-way policy)

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)
- No new upstream branches or PRs are permitted; upstream is intake-only.

## Last scorecard update

- 2026-08-26 — Mail S3 acceptance and recovery reconciliation on develop @
  `31045d033`: authenticated real-packet
  `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
  PASS 1/1 in 2m39s on isolated MySQL/Docker; instance-faithful
  equipment+copper restart flow, ownership guards, unread recount lifecycle,
  sequential take, and delete persistence all passed. PB-005 remains
  FIXED-PARTIAL; the historical PB-007 status was OPEN/narrowed with corrected
  live rerun pending; final gate **2480/0/1**. This predates the current
  source-pinned closure above.
- 2026-08-26 — prior recovery snapshot (superseded by the Mail S3
  acceptance above): develop @ `e5db6d390` carried grounding `38c4997d3`,
  recovered Retribution test branch `a4f7820ba`, and merchant merge
  `e5db6d390`; PB-005 was FIXED-PARTIAL, and the historical PB-007 status was
  OPEN/narrowed. Mail S3 was still recorded incomplete/uncommitted. Final gate
  **2480/0/1**.

- 2026-08-24 — this commit: SCORECARD promotions from the post-f3bb787ce sweep —
  ZONE-01 W=2/A=1 (data-driven Peace boot state + CanAttack enforcement;
  rig-tested, no live PvP scenario yet) · MATE-01 W=2/A=1 (mate equip-pack data
  + fail-closed legality; no live equip E2E yet) · TRANSFER-01 W=2/A=1
  (TlId-shadowing fix; live board/ride/disembark E2E PASS) · ITEM-01 evidence
  note (item_proc_bindings loader + UnitProcs seam; grades conservative) ·
  HOUSING-01 evidence note (build-race regression added; grades unchanged) ·
  BOT-02 note (scheduler soak STAGE 1 executed; staged ladder continues). H
  stays UNKNOWN everywhere. Branch of record → 3a534b539.
- 2026-08-23 — this commit: SCORECARD promotions from the 08-23 sweep —
  AUCTION-01 W/A/R → 2 (live E2E incl. kill -9 restart pin, strongest of the
  sweep) · TRADE-01 W=2/A=1 (trade handshake rig + engine fixes; A stays
  honest: rig-level) · PARTY-01 W=2/A=1 (party spike live E2E c98da8a53) ·
  MAIL-01 A=1 (return+expiry rig-tested; CSReturnMailPacket opcode still an
  unregistered 0xfff placeholder) · QUEST-01 EtcItemObtain credit note
  (~51 quests). H stays UNKNOWN everywhere. Branch of record → f3bb787ce.
- 2026-08-13 — **canonical sync (t_c9f0d7f6)**: M5.1 recovery plan recorded —
  Kimi memo + Codex reconciliation (salvage order Deposit/Withdraw → Harvest →
  BoardVehicle → Craft split with card ids; work preserved, no
  re-implementation); LoadPackOntoVehicle (t_a7756a00) + DriveVehicle
  (t_eaf1754d) recorded as genuine Phase-2 prerequisites; Housing.Build =
  M5.2 contract card t_94761d55 in Josh-approved Phase-2 scope (impl open);
  Phase 1 t_61a0eebb stays open (min-slice evidence only; follow-up
  t_15787275); control-plane API / MCP sidecar / first consumer marked DONE
  (were queued); branch of record → 983b35736 (ls-remote verified); H stays
  UNKNOWN everywhere.
- 2026-08-12 — **bot-backtrack Phase 0.2 reconciliation (t_4ec066d3)**: M3a/M4
  H grades corrected — scripted-actor evidence is proxy/bot-functional, H =
  UNKNOWN until Josh runs it; SCORECARD H dimension = actual player only
  (never H=2); deferred gates recorded (M1 human route, original M2 human
  baseline, M3a contract replay, M4 economic/navigation replay, M6 B4 restart
  scenario); waivers visible in ROADMAP/SCORECARD/STATUS — branch
  docs/phase0-2-reconcile, Rei gate t_ee64e86b.
- 2026-08-12 — tracking refresh (t_773f9651): STATUS.md M4 row (merged +
  deployed @ pinned 95bb1c78e, Rei PASS, prod 6d5a07cf49a5, t_442f3016) +
  M5 A1/B1 row (develop @ 761d1e81a, ls-remote verified) + M6 exit blockers;
  branch-of-record → 761d1e81a; hermes-ops SLO window relabeled sidecar/shadow
  baseline (f31f829, decision log 8a0fb09).
- 2026-08-11 — this commit: M1-M3 audit C4 tracking refresh (t_b3980118,
  audit t_5b1f5494 PASS WITH NOTES) — STATUS.md M2/M3a/M3b rows +
  branch-of-record 4ded92c61; ROADMAP.md M3a/M3b closeout lines + M1
  status reconcile (closed on automated evidence, human playtest verdict
  open — C5); progression-board.md M3a/M3b rows.
- 2026-08-10 — this commit: post-merge tracking for SaveManager dirty-tracking
  (merged 5ed5d6493, t_8c18eb1c / Rei gate t_53025996 ACCEPT) — SCORECARD.md fork-fix
  entry + PROG-01 save-path pointer, ISSUES.md AUDIT-001 closure, STATUS.md G2-A4
  note, ROADMAP.md A4 implementation annotation.
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
