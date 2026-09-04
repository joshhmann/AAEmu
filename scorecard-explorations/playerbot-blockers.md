# PLAYERBOT_BLOCKER ledger

When a bot cannot continue playing normally, it files a blocker here.
Blockers outrank speculative features in the backlog. Layer tags:
BOT-SIDE / SERVER / DATA / UNKNOWN.

Format: ID · scenario · intended action · observed vs expected · layer ·
evidence · status (OPEN/FIXED/WONTFIX-with-reason).


## Current source/evidence checkpoint (2026-08-28)
- Current source/test HEAD: `792774d7707b8b578b8d9975896e0a1ac719f361`
  (`origin/develop`). Per-run soak ownership hardening is `799b698ad`:
  A5/A5Tier3 snapshot named account/character rows and clean only newly owned
  IDs in `finally`; sibling-preservation tests pass 2/2. No broad wildcard
  cleanup remains in those probes.
- Full normal-clone gate at 792: **2496 total / 2495 passed / 0 failed /
  1 skipped**, compiler **0/0**, MCP stdio smoke **39 tools**. The sole skip is
  `Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
  `AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`.
- IntegrationTests Release restore/build passed with 0 errors; restore emitted
  2 NU1903 and build emitted 2 NU1903 in this exact verification. Runtime
  evidence uses `E2eStack.SourceRevision` with an `unknown` fallback.
---

## OPEN

### PB-005 · NPC spawn Z is effectively unclamped — systemic ungrounded NPCs (floating / buried)
- Scenario: any placed NPC whose canonical `npc_spawns.json` z disagrees with local ground by ≥ 1 m (prod human report: "a lot of the NPCs are not really grounded — some floating, some under roads and clipping")
- Observed vs expected: the old spawn-time correction (`NpcSpawnerNpc.SpawnNpc`) only applied `GeoData.GetHeight` when |spawnerZ − newZ| < 1 m, allowing larger source-data offsets to reach clients unchanged
- Measured 2026-08-25 (offline engine-identical heightmap harness over all 25 118 main_world npc_spawns): of 23 058 defect-audited spawns (fly/swim excluded), 89.54 % grounded, 3.72 % minor float (0.5–2 m), **5.62 % severe float (> 2 m, worst +183.6 m)**, 2.91 % submerged (< −0.5 m, worst −270.3 m); plus 733 exact duplicate spawn rows
- Layer: SERVER (ineffective clamp) with DATA component (bad z clusters in `npc_spawns.json`, e.g. e_hasla_2 Citizens/Maid frozen at z=538.x on 355–430 terrain); remedy B duplicate/data overlay remains intentionally unimplemented pending owner decision
- Status: FIXED-PARTIAL — remedy C whitelist + remedy A positive-only spawn clamp landed 2026-08-26. Bounded replay of the audit matrix identifies 1 295 severe positive offsets: 702 intentional aerial/water/structure whitelist rows remain unchanged, while all **593 non-whitelisted severe-float rows** are corrected to sampled terrain at spawn. The clamp leaves all offsets below 2 m intact and preserves all negative offsets; cave/interior floors and road/deck meshes remain unobservable to the terrain-only sample, so submerged counts are not claimed fixed. Hasla frozen-Z Citizens/Maid/Ravra and Hasla Guard/Sentry families remain non-whitelisted. Duplicate-row ownership remains OPEN.
- Evidence: `scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md` (method §1, tables §2–4, classification §5); harness `/root/npc-grounding-harness/`; raw matrix `/tmp/ng.tsv`; targeted policy tests 13/13 pass; `dotnet build AAEmu.Game/AAEmu.Game.csproj --no-restore` pass; compact.sqlite3 SELECT-only, no behavior/data rows changed

### PB-001 · Straight-line movement blocks interior/travel gameplay — IMPLEMENTATION LANDED / BROAD COVERAGE OPEN 2026-09-03
- Scenario: bot travels beyond open courtyards (Deadmine tunnels, cross-region routes)
- Intended: navigate terrain/obstacles to reach objective
- Observed: straight-line walk; stuck detection fires (M7#5) but no route exists
- Layer: BOT + SERVER (no navmesh/waypoint network)
- Historical evidence (retained): M7 spike shortcuts on record; soak run-1 drowning (fixed at home-anchor level).
- Historical implementation report (retained): CryEngine `.bai` navigation engine hardened (G-cost fix, binary heap, per-block spatial grid, `BaiNavigationRigTests` 6/6 green, corridor detour improved from 1.91x to 1.22x). `IGameplayActor.NavigateTo` routed navigation contract landed supporting multi-waypoint navigation with automatic A* pathfinding over GeoData, waypoint stepping, and graceful fallback to direct leg.
- Current implementation/evidence (2026-09-03):
  1. `IGameplayActor.NavigateToUnit` landed across `IGameplayActor`, `GameplayActor`, and `PlayerBotControllerAdapter` (`BotActionKind.NavigateToUnit`), sharing A* GeoData pathfinding and waypoint stepping. `LevelingLoopScenario` wired for all hunt/grind/talk/turn-in/gather legs. Evidence: `GameplayActorNavigateTests` 8/8 green.
  2. **In-Game Dev Mapper (Manual Walk Mode)**: `DevMapperService` + in-game `/mapper walk <name>`, `/mapper mark <label>`, `/mapper stop`, `/mapper list`, `/mapper play <bot> <route>`. Automatically compacts straight waypoints and records doodad interactions, NPC talks, and combat casts into `Data/Routes/<name>.json` and `Data/Path/<name>.path`. Evidence: `DevMapperServiceTests` 5/5 green.
  3. **Bulk Navigation Toolchain (`Tools/Mapper/`)**:
     - `redline_to_path.py`: Converts 2D map lines/coordinates into 3D `.path` and `.json` routes with IDW ground Z-height estimation from NPC spawns.
     - `generate_zone_heatmap.py`: Plots 25,118 NPC spawns and carriage checkpoints into 2D vector maps (`.svg`) for Solzreed, Dewstone Plains, White Arden, and Marianople.
     - `extract_doodad_obstacles.py`: Correlates `doodad_spawns.json` with `Doodads.xml` across 15 structure categories to extract placed obstacles with keep-out radii.
  4. **Beyond Solzreed Inter-Zone Expansion (Lv 15–30)**:
     - Mapped Dewstone Plains (`w_garangdol_plains_1`, 2,745 NPCs, 327 obstacles), White Arden (`w_white_forest_1`, 948 NPCs, 107 obstacles), and Marianople (`w_marianople_1`, 1,692 NPCs, 252 obstacles).
     - Generated inter-zone arterial highway networks: `highway_solzreed_to_dewstone.path` (10.2 km, 402 waypoints) and `highway_dewstone_to_marianople.path` (4.0 km, 163 waypoints).
  5. **Doodad Obstacle Avoidance (`ObstacleManager`)**:
     - `ObstacleManager` indexes 1,395 placed obstacles into a 100m 2D spatial hash grid (`Data/Navigation/*_obstacles.json`).
     - Wired into `AiGeodataManager.CheckImpossibleWalk(Vector3 point)` so A* pathfinding automatically detours around fences, stone walls, closed gates, and buildings. Evidence: `ObstacleManagerTests` 3/3 green.
  6. **Full Gate Evidence**: Release build 0 errors, script compiler 0 errors / 0 warnings, **2,758 total tests (2,757 passed, 1 skipped)**, MCP stdio protocol smoke 39 tools, MCP archaeology gate smoke 24 tools. Commits: `805f23c59`, `b34e34263`, `a1d0ae664`, `fc5c9fc1b` on `origin/develop`.
- Status: IMPLEMENTATION LANDED / BROAD COVERAGE OPEN

### PB-002 · Progression ceiling: no viable quest content past curated Solzreed slice for bots — SCOPED SLICES LANDED / BROAD CLAIM OPEN 2026-09-03
- Scenario: bot finishes golden-route chain (~lvl 15-20 equivalent), seeks next quests
- Intended: continue leveling via real quest content and arterial inter-zone routes
- Observed: scoped actor/rig coverage now includes autonomous inter-zone highway transitions (Solzreed -> Dewstone Plains -> Marianople) and autonomous Nui shrine death recovery.
- Layer: DATA + BOT (quest discovery/perception, objective execution, inter-zone travel, death recovery)
- Historical failure evidence (retained): adventurer v1 runs curated chains only; bot stalls when no offerings exist in current starting zone.
- Current evidence:
  1. **Autonomous Inter-Zone Progression (`TryTransitionToNextZone`)**: When all quests in the current zone are exhausted within the target band, bots evaluate their level and region. If level $\ge 10$ in Solzreed, the bot autonomously transitions along the arterial highway to Dewstone Plains (Lilyut Crossing hub) and triggers fresh quest discovery. If level $\ge 20$ in Dewstone, it transitions to Marianople Capital Gate. Evidence: `LevelingLoop_InterZoneTravel_TransitionsToNextZoneHighway` passed.
  2. **Dewstone Early Quest Chain & Adaptive Perception Expansion**: Extended quest discovery perception band adaptively (`AdaptiveBand = true`) so leveling bots reaching Dewstone Plains discover early quest offerings (levels 10–20) including Afindelle (NPC 673) and Lord Royster (NPC 680). Added data-driven and fallback gather resolution in `GatherLeg` for doodads missing explicit `highlight_doodad_id`. Evidence: `LevelingLoop_DewstoneExpansion_DiscoversAndCompletesDewstoneQuestChain` passed.
  3. **Autonomous Death Recovery (`HandleDeathRecovery`)**: When a bot dies during leveling loops or combat, it enters death recovery, resurrects through the real `CharacterResurrection` engine path at the nearest Nui goddess shrine, relocates to the shrine anchor, and recovers HP/MP to safe threshold ($\ge 70\%$) before resuming. Evidence: `LevelingLoop_DeathRecovery_ResurrectsAtNuiAndRecoversHealth` passed.
  4. **Rig & Gate Evidence**: `LevelingLoopScenarioRigTests` **38/38** green (+3 tests). Full `./scripts/gate.sh` passed with 0 compiler errors/warnings, **2,774 total tests (2,773 passed, 1 skipped)**, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- Status: SCOPED SLICES LANDED / BROAD CLAIM OPEN

### PB-COMBAT · Tactical Combat Decision Tree & Class Spacing — IMPLEMENTATION LANDED 2026-09-03
- Scenario: playerbot engages in combat during leveling, grinding, and objective pursuit
- Intended: class-adaptive combat positioning (ranged kiting, melee reach), emergency survival disengage, and combo evaluation
- Observed: previously bots blindly traded blows at 3m melee until death regardless of class; now governed by deterministic `CombatDecisionTree`
- Layer: BOT + COMBAT (tactical positioning, ability-tree role inference, kiting, retreat)
- Current evidence:
  1. **Role Inference (`InferRole`)**: Evaluates `character.Ability1` to categorize class tactics (`Wild` -> `RangedPhysical`, `Magic`/`Death`/`Illusion` -> `RangedMagic`, `Love`/`Romance` -> `HealerSupport`, others -> `Melee`).
  2. **Emergency Survival Flee (`EmergencyFlee`)**: When HP drops below critical threshold ($\le 20\%$), bot disengages and navigates away from hostiles to avoid dying.
  3. **Tactical Spacing & Kiting (`KiteSpacing`)**: When enemies penetrate the minimum safe range of ranged physical/caster classes ($< 12\text{m}$), bot steps/kites back 10m to re-establish $12\text{–}22\text{m}$ firing spacing.
  4. **Melee Gap Closing (`CloseGap`)**: Melee bots close distance into engagement reach before firing skills.
  5. **Integrated into Loop Scenarios**: Wired into `LevelingLoopScenario.HuntLeg`, `LevelLeg`, and `AbilityLevelLeg`.
  6. **Unit & Gate Evidence**: `CombatDecisionTreeTests` **5/5** green; `LevelingLoopScenarioRigTests` **37/37** green. Full `./scripts/gate.sh` passed with **2,765 total tests (2,764 passed, 1 skipped)**.
- Status: IMPLEMENTATION LANDED / ADVANCED CLASS EXPANSION OPEN

### PB-BAG · Autonomous Bag Management, Vendoring & Durability Repair — IMPLEMENTATION LANDED 2026-09-03
- Scenario: playerbot fills inventory bag with loot/drops during questing and combat; equipment durability degrades over time
- Intended: autonomous inventory auditing, selling vendor junk, protecting quest items/consumables, and repairing gear at blacksmiths
- Observed: previously bots had no bag maintenance, would eventually hit full inventory (refusing quest item rewards) and broken gear (0 durability loss of stats); now governed by `BotBagManager` and `GameplayActor.Repair`
- Layer: BOT + INVENTORY + ECONOMY (bag capacity auditing, trash classification, vendoring, equipment maintenance)
- Current evidence:
  1. **Bag Auditing & Classification (`BotBagManager.AuditBag`)**: Computes capacity, free slots, fullness percentage, and classifies items into vendor junk vs protected assets.
  2. **Strict Item Protection (`BotBagManager.IsTrash`)**: Explicitly safeguards quest items (`Quest_Item`, quest weapons/armor/accessories, `LootQuestId`), active equipment, and essential sustain consumables (potions, food, water). Only explicit `Trash_*` categories (35, 98, 101, 102, 103, 104, 105) and gray/common non-equipment with refund value are sold.
  3. **Autonomous Vendoring (`BotBagManager.SellAllTrash`)**: Executes real `actor.Sell` path against merchant NPCs, reclaiming bag slots and converting junk into copper/silver.
  4. **Canonical Equipment Repair (`GameplayActor.Repair` & `BotBagManager.RepairAllEquipment`)**: Executes real `Character.DoRepair` engine path at blacksmith or merchant NPCs, restoring weapons and armor to `MaxDurability`.
  5. **Integrated into Leveling Loops (`LevelingLoopScenario.TryPerformBagMaintenance`)**: Automatically runs upon quest turn-ins at town/settlement hubs.
  6. **Unit & Gate Evidence**: `BotBagManagerTests` **4/4** green; `LevelingLoopScenarioRigTests` **37/37** green. Full `./scripts/gate.sh` passed with **2,769 total tests (2,768 passed, 1 skipped)**.
- Status: IMPLEMENTATION LANDED / ADVANCED GEAR UPGRADE OPEN

### PB-MOUNT · Autonomous Mount Riding on Arterial Highways & Travel Mobility — IMPLEMENTATION LANDED 2026-09-03
- Scenario: playerbot travels long distances along arterial highway networks (e.g. 10.2 km Solzreed -> Dewstone route) on foot at slow speed (5.4 m/s)
- Intended: autonomous mount summoning, mounting, high-speed arterial transit (~10.5 m/s, ~2x foot speed), and dismounting for combat/interaction
- Observed: previously bots had no mount integration during autonomous loops, traveling only on foot; now governed by `BotMountManager` and engine `VehicleMovementModel`
- Layer: BOT + VEHICLE + MOVEMENT (mount lifecycle, rider attachment, mate transform synchronization, speed scaling)
- Current evidence:
  1. **Autonomous Mount Manager (`BotMountManager`)**: Exposes `EnsureMounted`, `EnsureDismounted`, `IsMounted`, and travel speed constants (`MountedTravelSpeed = 10.5f`, `FootTravelSpeed = 5.4f`).
  2. **Clean Engine Attachment & Mate Transform Seeding**: Seeds mount companion with owner linkage, attaches player via `actor.Mount` / `MateManager.MountMate`, and synchronizes movement through `VehicleMovementModel.ApplyUnitMove(Character, mate, ...)` bypassing rider client-ignore rules.
  3. **Autonomous Arterial Travel Integration**: Wired into `LevelingLoopScenario.TryTransitionToNextZone` so bots mount during long-distance inter-zone transit and dismount upon reaching the destination quest hub.
  4. **Unit & Gate Evidence**: `BotMountManagerTests` **4/4** green; `LevelingLoopScenarioRigTests` **37/37** green. Full `./scripts/gate.sh` passed with 0 compiler errors/warnings, **2,773 total tests (2,772 passed, 1 skipped)**, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- Status: IMPLEMENTATION LANDED / ADVANCED COMBAT MOUNT EXPANSION OPEN

### PB-SOAK · Dormant-timer soak and cancellation blocker — OPEN
- Scenario: Tier-3 dormant bots and long-running scheduler validation.
- Observed: no six-hour dormant-timer soak exists in current evidence; no soak
  snapshots and ID-bound `finally` cleanup (`799b698ad`); sibling-preservation
  tests pass 2/2, with no broad wildcard cleanup in those probes.
- Harness blocker: `SeedBox` has synchronous bridge calls/native `Thread.Join`
  without hard cancellation.
- Layer: BOT + SERVER (harness lifecycle and cancellation).
- Evidence: current source/test HEAD
  `792774d7707b8b578b8d9975896e0a1ac719f361`; prior staged and historical
  reports remain preserved and are not relabeled as a current six-hour result.
- Status: OPEN


## FIXED (evidence retained)
### PB-007 · Flagged same-faction aggression handshake — FIXED / CLOSED 2026-08-27
- Scenario: PVP-01 slice 1 — two real Nuian TCP bots, attacker ForceAttack-flagged (CS 0x04f), casts Triple Slash 18131 on a co-located same-faction victim in e_steppe_belt (conflict group 14)
- Closure evidence: behavioral gate baseline `3871459d142fdd1767b9365a1de8d4cd3652ab0e`; current source/test HEAD is `792774d7707b8b578b8d9975896e0a1ac719f361`. Source commits `063beb7cd` (parser/live proof) and `b230bd8a2` (separate PB-002 item-use objective). Final isolated real-login/Game E2E passed 1/1 in 2m09.910s.
- AGGRESS-ALLOWED: victim-matched non-immune `SCUnitDamaged=True`; immune frames excluded=False; `SkillFired=True`; Retribution 2167=True; bloodstain doodad 877 objId 44294; crime branch observed.
- PEACE-BLOCK: passed with no victim-matched non-immune damage. WAR-HONOR remains intentionally deferred; broader PvP/honor scope is not closed.
- Historical failure context (retained): the prior immune-tagged/untrusted live result, login-protection window, and parser framing failure remain historical; the current report supersedes them without erasing that history.
- Evidence: `scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md`; deterministic parser tests 2/2.
- Status: FIXED / CLOSED for the narrow handshake requirement; WAR-HONOR separately deferred.

### PB-006 · Ship sailing physics non-functional live (2026-08-25) — FIXED 2026-08-26
- Layer: SERVER, but NOT where the original report pointed. Two findings:
- Finding 1 — "hull spawns ~100 m in air" was a MISDIAGNOSIS (no engine change needed):
  the internal ocean surface of main_world IS z=100 — data-driven from client
  `world.xml` attribute `oceanLevel="100"` (extracted from runtime game_pak) →
  `WorldTemplate.OceanLevel` → `WaterBodies.OceanLevel` (`InitWaterFromTemplate`),
  and the same value is the Jitter2 `Buoyancy` fluid-box surface. Live probe
  ([water-diag], isolated stack /root/aaemu-e2e-boat2): `GetWaterSurface` at the
  summon point returned exactly 100.00 = OceanLevel; the hull settled at z=99.4–99.7
  with vel=(0,0,0) — that IS floating at rest at the surface (draft), not hanging in
  air. The character's z=0.05 is ~100 m BELOW the surface (ground-level npc/teleport
  data; PB-005 territory). Client wire heights are internal−100
  (`Helpers.ConvertPosition`), so clients saw the boat correctly at ≈0.
  `PhysicsManager.DefaultWaterLevel` never fired: it is only a last-resort fallback
  behind `SimulationWorld.Template.OceanLevel`, which is always loaded for main_world.
- Finding 2 — zero replication ROOT-CAUSED & FIXED: the E2E bridge teleports
  (`BotDriveBridge.teleportToNpc` et al.) mutated `Transform.Local.Position`
  directly. For an idle character nothing ever runs `FinalizeTransform`/
  `AddVisibleObject` afterwards, so the character stayed registered in its PREVIOUS
  region (live proof: [bc-diag] receivers=0 on 1515 consecutive ship broadcasts;
  owner ghost-registered in region 249072 while the hull broadcast from 49617).
  Every proximity broadcast at the destination — ship SCOneUnitMovementPacket
  included — resolved ZERO receivers. Physics, encode path and GetAround were
  healthy all along; the receiver set was empty.
- Fix: `BotDriveBridge.TeleportWithRegionSync` (position+zone write followed by the
  normal `WorldManager.AddVisibleObject` region handoff), all four direct-mutation
  call sites routed through it. Commit f33ddf285 on branch fix/pb006-boats
  (.worktrees/boatsfix): + unit tests BotDriveBridgeTeleportRegionTests (defect
  shape + fixed contract, green), RowboatE2eTests harness committed with its two
  false-premise assertions corrected to the documented internal convention
  (`InternalOceanLevel = 100`; DEBUG-log sink for AddShip/RemoveShip counts via
  stack-local NLog console rule).
- Evidence: post-fix live run — SUMMON→BIND→HELM all green: 763 Ship frames in a
  15 s window, displacement 67.2 m under throttle, yaw-rate sign flips with steering
  reversal (+100→−8.7°/s, −100→+8.1°/s), unbind/despawn wire clean, no leaked body.

### PB-003 · Zone 46 Hadir Farm exit path (was: "no exit portal data")
- Scenario: party clears Hadir Farm, wants to leave
- Intended: exit doodad back to main world
- RESOLVED BY VERIFICATION 2026-08-25: the DATA premise was FALSE — canonical
  re-check (reference data first) found the exit portals shipped all along:
  `Data/Worlds/instance_hadir_farm/doodad_spawns.json` carries almighty 4289
  @(552.2, 499.8, 133.5) + 4927, wired in compact.sqlite3 (doodad_func_groups
  10546/12277 → doodad_funcs 12785/12937 DoodadFuncExitIndun, skill 17733
  '하디르의 농장 퇴장'). Static doodad spawns are JSON world data by design —
  compact.sqlite3 never held spawn rows, so no SQL patch was warranted. The real
  gap was E2E coverage (the earlier run probed NPCs only; its own game log read
  "Doodads spawned: 2" for world 100-instance_hadir_farm).
- Layer correction: was DATA — actually E2E-coverage gap, now CLOSED
- Evidence: isolated-stack party-clear-then-exit E2E PASS 2026-08-25
  (/root/aaemu-e2e-pb003/logs/indun-exit-e2e-report.json; test
  AAEmu.IntegrationTests.E2e.IndunExitE2eTests in .worktrees/pb003-exit):
  both members cast skill 17733 at live exit-portal objId → SCLoadInstancePacket
  (world 0, zone 179) → charPos main_world @ exact pre-entry anchor,
  instance 0; completion events 4601/4602 fired before exit; 11/11 stages green (re-proven green post-rebase on pb003/exit-e2e).
  Dossier addendum: mechanics/indun-domain.md (2026-08-25).

### PB-004 · Proximity-materialized dormant bots never step (scheduler not re-armed)
- Scenario: G2-A5 true dormancy — a live human enters ReducedProximityRadiusM (200 m) of dormant homes; PopulationDirector materializes specs into the world
- Intended: a materialized bot starts the fidelity ladder at Reduced and resumes scheduler stepping like any embodied bot
- Observed vs expected: `MaterializeNearbyDormantSpecs` sets `_fidelity[id] = BotFidelity.Reduced` but never calls `Wake()` (the Dormant→Reduced transition in the same sweep's `due` path exists precisely to "re-arm scheduler stepping"); proximity-materialized bots stand inert — steps/min = 0 with 10 bots embodied, no PlayerBotScheduler activity for the materialized ids
- Layer: SERVER
- Evidence: A5 acceptance run 2026-08-25T16:36Z, report §8.4 (`scorecard-explorations/generated/g2-a5-acceptance-report.md`); game-restart.log shows 10× "True dormancy: materialized …" with zero scheduler wake/step lines until dematerialization
- FIXED 2026-08-25 (6ba363a28): MaterializeNearbyDormantSpecs now Wake()s the scheduler on proximity materialization; dormancy-only boot idempotently starts IPlayerBotScheduler; bridge seedDormant records the roam schedule. Live proof: steps/min 0→3001 with 10 bots embodied, dematerialize-on-leave clean; rig tests MaterializeNearbyDormant_WakesScheduler_StepsResume etc.; full gate 2378/0/1. Post-fix numbers: report §10.

### PB-F1 · Duelists stuck IsInDuel forever when flag spawn fails
- Found by DuelManagerRigTests; RestoreFaction bare indexer + flag delete NRE inside stop catch-all
- Fixed f8252a37b

### PB-F2 · Environmental deaths (null killer) crashed mid-death
- Found by PartyLifecycleFaultMatrixTests; Unit.DoDie/CharacterCombat null-guards added c011e8a24

### PB-F3 · Journal-report gate auto-passed (466 quests)
- ConReportJournal `|| true` stub; fixed cab6e4dc9

### PB-F4 · Transfers could never be boarded (TlId shadowing)
- Fixed 3a534b539; live ride E2E green

