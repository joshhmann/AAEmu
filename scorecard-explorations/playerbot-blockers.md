# PLAYERBOT_BLOCKER ledger

When a bot cannot continue playing normally, it files a blocker here.
Blockers outrank speculative features in the backlog. Layer tags:
BOT-SIDE / SERVER / DATA / UNKNOWN.

Format: ID · scenario · intended action · observed vs expected · layer ·
evidence · status (OPEN/FIXED/WONTFIX-with-reason).


## Current source/evidence checkpoint (superseded 2026-10-01)
- **Authoritative current source/evidence checkpoint (recorded 2026-10-01,
  read-only reconciliation, no build/test run):** current branch `develop`,
  `git rev-parse HEAD` = **`626548de92b8dca8af1f0f92e22d37c9fbe6074e`**.
  Working tree was **dirty** at that checkpoint (inherited uncommitted work, not
  authored by this reconciliation; the path count is deliberately omitted as
  volatile). **No evidence row
  below has been re-verified at `626548de`.** Every grade and evidence cell in
  this ledger stands on the revision at which it was actually measured.
- **Historical checkpoint (2026-08-28) — preserved verbatim, do not read as
  current:** Current source/test HEAD: `792774d7707b8b578b8d9975896e0a1ac719f361`
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

### PB-BRIDGE-01 · Test/control plane can perform gameplay (event-injection + fixture ops) — OPEN, removal tracked here (doc-alignment step 6)
- Scenario: any autonomy lane where the harness drives progress the bot did not earn (`BotDriveBridge` `drive/kill`, `drive/gather`, `drive/talk`, `drive/interact`, `drive/enterSphere`, `drive/cinemaStarted/Ended`, `drive/aggro`, `drive/zoneKill`, `drive/levelUp`, `drive/express`, plus fixture grants `drive/stock`, `drive/setLevel`, `homestead/kit`, `farm/rig`, positional staging `drive/teleportToNpc`, `homestead/move`, `farm/place`).
- Intended: test/control API spawns/removes/assigns/observes/waits/collects traces + explicitly labelled privileged setup only; no Test API verb may inject a `UnitEvents` progress event or synthesise a success (see `scorecard-explorations/mechanics/playerbot-api-architecture-2026-09-18.md` §1, §6.4).
- Observed vs expected: 20 event-injection call sites + 14 privileged fixture ops coexist with 11 real engine events reachable through `IGameplayActor`, producing indistinguishable-looking green evidence; bridge throwaway actors run off the execution boundary on a different ledger from the scheduler's actor.
- Layer: BOT-SIDE (control plane) + HARNESS.
- Status: OPEN.
- Evidence: API audit 2026-09-18 (HEAD `d0bea9e6a`); activity-spine rules forbid gameplay-by-bridge; no removal landed yet.
- Exit: event-injection ops removed or hard-gated out of autonomy lanes (fixture-only label + artifact disclosure), `/api/actors` enqueue path used for live claims; verify by grep for `KillNpc|TalkToNpc|GatherItem|AdvanceQuest` in bridge lane artifacts showing zero gameplay calls.

### PB-FARM-01 · Farming loop stalls: 3m merchant range check, 150m soil horizon, and maturation patrol fallthrough
- Scenario: playerbot executes needs-farm loop (`BotRoamStepExecutor` Branch 0b, `NeedsFarmActivityModule`) during autonomous roaming or live H-gate testing
- Intended: bot checks bag for seeds; if missing, locates nearest seed merchant, navigates to merchant, purchases seeds; navigates to public/owned farm soil; plants seeds; remains leashed to plot while waiting for crop maturity; harvests mature crop; deposits or sells yield.
- Observed vs expected:
  1. **3-Meter Merchant Discovery Trap**: `ResolveSeedMerchant` (`BotRoamStepExecutor.cs:1392`) starts search with `bestDist = GameplayActor.MaxShopRange` (hardcoded 3.0m). Any seed merchant further than 3.0m is ignored (`merchantObjId = 0`). In `NeedsDecisionScenario.cs:335`, `BuyProposal` fails precondition `merchant-configured` (`opts.MerchantNpcObjId != 0`), falling back to `RestProposal` (`actor.Stop()`). Bot freezes in place.
  2. **150-Meter Soil Horizon Trap**: `ResolveSoilTarget` (`BotRoamStepExecutor.cs:1535`) discovery spiral caps at `150f`. If spawned or roaming >150m from farm soil, 3 failed resolve attempts exhaust the budget (`state.NeedsFarmSoilAttempts > NeedsFarmMaxSoilAttempts`), causing permanent deferral.
  3. **WaitingMaturity Patrol Fallthrough**: During crop growth (`NeedsFarmLoopPhase.WaitingMaturity`), `StepNeedsFarmLeg` returns `false`, clearing `state.NeedsLegActive`. Execution falls through Branch 0b into Branch 2 (ambient roam patrol), walking the bot >150m away and abandoning the crop before it matures.
- Layer: BOT-SIDE (`BotRoamStepExecutor.cs`, `NeedsDecisionScenario.cs`)
- Status: OPEN
- Evidence: Live test failure with Josh/Muse on .165 test stack; source audit in `PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md`.
- **2026-09-15 note (read-only audit; status stays OPEN):** partial mitigations landed since the original report — (a) merchant-approach routing (`BotRoamStepExecutor.cs:1264-1273`: a perceived seed merchant beyond `MaxShopRange` is approached via `ArmFarmRoute` with phase `Traveling`, not frozen on); (b) `WaitingMaturity` patrol-fallthrough guards (`BotRoamStepExecutor.cs:932-934, 971-974`: ambient-roam branches refuse while `NeedsFarmPhase == WaitingMaturity`); (c) leashed `StepFarmLeisure` micro-wander (`BotRoamStepExecutor.cs:1426-1433`, called at :1218/:1236) keeps the bot active at the plot while waiting. The 150 m soil discovery cap remains by design; the original 3 m/150 m/fallthrough failure modes stay OPEN pending live proof.


### PB-005 · NPC spawn Z is effectively unclamped — systemic ungrounded NPCs (floating / buried)
- Scenario: any placed NPC whose canonical `npc_spawns.json` z disagrees with local ground by ≥ 1 m (prod human report: "a lot of the NPCs are not really grounded — some floating, some under roads and clipping")
- Observed vs expected: the old spawn-time correction (`NpcSpawnerNpc.SpawnNpc`) only applied `GeoData.GetHeight` when |spawnerZ − newZ| < 1 m, allowing larger source-data offsets to reach clients unchanged
- Measured 2026-08-25 (offline engine-identical heightmap harness over all 25 118 main_world npc_spawns): of 23 058 defect-audited spawns (fly/swim excluded), 89.54 % grounded, 3.72 % minor float (0.5–2 m), **5.62 % severe float (> 2 m, worst +183.6 m)**, 2.91 % submerged (< −0.5 m, worst −270.3 m); plus 733 exact duplicate spawn rows
- Layer: SERVER (ineffective clamp) with DATA component (bad z clusters and duplicate rows in `npc_spawns.json`)
- Status: DUPLICATES CLEANED 2026-09-03 / CLAMP FIXED-PARTIAL
- Current evidence:
  1. **Exact Duplicate Row Cleaning (2026-09-03)**: Excised all 733 duplicate spawn rows from `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` (reducing active spawn records from 25,118 to 24,385), preserving all 8,261 inline comments, disabled blocks, and formatting intact.
  2. **Spawn Clamp (2026-08-26)**: Remedy C whitelist + remedy A positive-only spawn clamp landed (`NpcSpawnerNpc.SpawnNpc`). All 593 non-whitelisted severe-float rows corrected to sampled terrain at spawn.
  3. **Gate Evidence**: Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, in-game script compilation succeeded, **2,778 total tests (2,777 passed, 1 skipped)**, MCP BotControl 39 tools, MCP Archaeology 24 tools.

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
  5. **Doodad Obstacle Avoidance & Detour Navigation (`ObstacleManager`)**:
     - `ObstacleManager` indexes 1,409 placed obstacles into a 100m 2D spatial hash grid (`Data/Navigation/*_obstacles.json`), including 14 newly extracted Sharpwind Mines dungeon obstacles (`cuttingwind_deadmine_obstacles.json`).
     - Wired into `AiGeodataManager.CheckImpossibleWalk(Vector3 point)` for A* exclusion and `GameplayActor.NavigateToInternal` with `ObstacleManager.FindDetour` to compute tangent bypass waypoints when GeoData mesh has no path or is unavailable. Evidence: `ObstacleManagerTests` 5/5 green, `GameplayActorNavigateTests` 9/9 green.
  6. **Sharpwind Mines (Cuttingwind Deadmines) 3D Dungeon Interior Corridor Navigation**:
     - Generated canonical 3D interior tunnel waypoint networks linking Entrance `(718.7, 329.9, 168.3)` $\rightarrow$ Iron Bridge `(679.0, 326.5, 166.9)` $\rightarrow$ Boss 1 Nerta's Right Hand Wera `(503.4, 327.6, 166.0)` $\rightarrow$ Boss 2 Sharpwind Mines Ogre `(547.3, 409.6, 153.9)` $\rightarrow$ Cavern Hazard Pass $\rightarrow$ Final Boss Bloody Hand Okaphe `(611.3, 642.1, 140.0)`:
       - `AAEmu.Game/Data/Path/dungeon_sharpwind_mines.path`: 220 dense 3D waypoints ($\le 3.5\text{m}$ spacing).
       - `AAEmu.Game/Data/Routes/dungeon_sharpwind_mines.json`: Complete `DevMapper` route action graph (616.9m distance, labeled milestone anchors).
     - Verified dungeon obstacle avoidance detours through `ObstacleManager` around hazardous powder kegs and explosives in the narrow cavern tunnel without getting stuck.
     - Evidence: `SharpwindMinesNavigationTests` **5/5** green; `DevMapperServiceTests` hardened against singleton directory leakage.
  7. **Full Gate Evidence**: Release build 0 errors, script compiler 0 errors / 0 warnings, **2,791 total tests (2,790 passed, 1 skipped)**, MCP stdio protocol smoke 39 tools, MCP archaeology gate smoke 24 tools.
- Status: IMPLEMENTATION LANDED / BROAD COVERAGE OPEN

### PB-002 · Progression ceiling: no viable quest content past curated Solzreed slice for bots — SCOPED SLICES LANDED / BROAD CLAIM OPEN 2026-09-03
- Scenario: bot finishes golden-route chain (~lvl 15-20 equivalent), seeks next quests
- Intended: continue leveling via real quest content and arterial inter-zone routes
- Observed: scoped actor/rig coverage now includes autonomous inter-zone highway transitions (Solzreed -> Dewstone Plains -> Marianople) and autonomous Nui shrine death recovery.
- Layer: DATA + BOT (quest discovery/perception, objective execution, inter-zone travel, death recovery)
- Historical failure evidence (retained): adventurer v1 runs curated chains only; bot stalls when no offerings exist in current starting zone.
- Current evidence:
  1. **Autonomous Inter-Zone Progression (`TryTransitionToNextZone`)**: When all quests in the current zone are exhausted within the target band, bots evaluate their level and region. If level $\ge 10$ in Solzreed, the bot autonomously transitions along the arterial highway to Dewstone Plains (Lilyut Crossing hub) and triggers fresh quest discovery. If level $\ge 20$ in Dewstone, it transitions to Marianople Capital Gate. Evidence: `LevelingLoop_InterZoneTravel_TransitionsToNextZoneHighway` passed.
  2. **Dewstone Early & Mid Quest Chain (Stages 1 & 2)**:
     - **Stage 1 (Levels 10–15)**: Extended quest discovery perception band adaptively (`AdaptiveBand = true`) so leveling bots discover early offerings including Afindelle (NPC 673) and Lord Royster (NPC 680, quests 1656 and 865). Added data-driven and fallback gather resolution in `GatherLeg` for doodads missing explicit `highlight_doodad_id`. Evidence: `LevelingLoop_DewstoneExpansion_DiscoversAndCompletesDewstoneQuestChain` passed.
     - **Stage 2 (Levels 15–20+)**: Canonical Dewstone mid-chain progression covering Quest 921 ("Visiting the Construction Site", Lord Royster 680 $\rightarrow$ Foreman 699, gated by prerequisite Quest 3706 "Return Orders") and Quest 931 ("Scarred Jacob", Detective Medd 5849 $\rightarrow$ hunt Scarred Jacob 714 $\rightarrow$ report Detective Medd 5849). Defensive index bounds checking landed on `QuestActTemplate.AddObjective`/`SetObjective`/`GetObjective`. Evidence: `LevelingLoop_DewstoneStage2_VisitConstructionSite_DiscoversAndCompletesDelivery` and `LevelingLoop_DewstoneStage2_ScarredJacob_HuntsBossAndReportsMedd` passed.
     - **Stage 3 (Levels 20–25 Dungeon Entry)**: Canonical Sharpwind Mines dungeon progression covering Quest 6381 ("Confusion in the Cutting Wind", Alistair 12162 $\rightarrow$ cull Monster Group 602 dungeon bosses Wera 12150, Ogre 12152, Okaphe 12188 $\rightarrow$ auto-complete). Hardened `LevelingLoopScenario.PursueObjectives` to guard against non-objective acts (`!act.CountsAsAnObjective`, e.g. `QuestActConAutoComplete`). Evidence: `LevelingLoop_DewstoneStage3_SharpwindMines_DiscoversAlistairAndClearsDungeonGroupHunt` passed.
     - **Stage 4 (Levels 24–28 Marianople Capital Expansion)**: Canonical Marianople capital progression covering Quest 321 ("Protect the Pasture!", Shepherd Yuno 440 $\rightarrow$ cull Goblin Marauders 4941 $\rightarrow$ report Yuno 440), Quest 173 ("Guards of Marianople", Shepherd Yuno 440 $\rightarrow$ deliver report to West Gate Captain Bryce 501 at Marianople Capital Gate), and Quest 188 ("Noryette Family's Garden", South Gate Captain Killian 903 $\rightarrow$ deliver report to Hunting Grounds Keeper Viarna 458 at Marianople Gardens, gated by Quest 174 prerequisite via `UnitReqs` kind 31 `CompleteQuestContext`). Hardened `GameplayActorTestRig.SeedQuestHunt` to dynamically sync `existingHunt.Count` with `huntCount`. Evidence: `LevelingLoopScenarioRigTests` **45/45** green (+4 tests).
   3. **Autonomous Death Recovery (`HandleDeathRecovery`)**: When a bot dies during leveling loops or combat, it enters death recovery, resurrects through the real `CharacterResurrection` engine path at the nearest Nui goddess shrine, relocates to the shrine anchor, and recovers HP/MP to safe threshold ($\ge 70\%$) before resuming. Evidence: `LevelingLoop_DeathRecovery_ResurrectsAtNuiAndRecoversHealth` passed.
   4. **Rig & Gate Evidence**: `LevelingLoopScenarioRigTests` **45/45** green (+4 tests).
- Status: SCOPED SLICES LANDED / BROAD CLAIM OPEN

### PB-COMBAT · Tactical Combat Decision Tree & Class Ability Combos — IMPLEMENTATION LANDED / 10-CLASS EXPANSION COMPLETE 2026-09-04
- Scenario: playerbot engages in combat during leveling, grinding, and objective pursuit
- Intended: class-adaptive combat positioning (ranged kiting, melee reach), emergency survival disengage, and class combo rotations across all ArcheAge ability trees
- Observed: previously bots blindly traded blows at 3m melee until death regardless of class; now governed by deterministic `CombatDecisionTree` with class-specific ability combo chains across all 10 core ArcheAge ability trees
- Layer: BOT + COMBAT (tactical positioning, ability-tree role inference, kiting, retreat, combo rotations)
- Current evidence:
  1. **Role Inference (`InferRole`)**: Evaluates `character.Ability1` to categorize class tactics (`Wild` -> `RangedPhysical`, `Magic`/`Death`/`Illusion` -> `RangedMagic`, `Love`/`Romance` -> `HealerSupport`, others -> `Melee`).
  2. **Canonical 10-Class Combo Rotations (`SelectPrioritizedSkill`)**:
     - **Battlerage (`Fight`)**: Charge (11918) [snare] -> Triple Slash (18131) [trip on snared] -> Whirlwind Slash (13282) [bonus damage on tripped].
     - **Defense (`Adamant`)**: Shield Slam (10399) [stuns] -> Bull Rush (10501) [trips stunned target & silences].
     - **Shadowplay (`Vocation`)**: Overwhelm (10648) [gap-close leap & stun] -> Shadowsmite (10496) [trips stunned target] -> Rapid Strikes (18125) [rapid filler].
     - **Sorcery (`Magic`)**: Flamebolt (10752) [inflicts Burn] -> Freezing Arrow (10667) [43% bonus on Burn + Freeze] -> Chain Lightning (11967).
     - **Witchcraft (`Illusion`)**: Enervate (10159) [mana burn / debuff] -> Earthen Grip (14376) [snare + life drain combo on enervated].
     - **Archery (`Wild`)**: Charged Bolt (16210) [inflicts Slow] -> Endless Arrows (14835) [bonus vs Slowed].
     - **Vitalism (`Love`)**: Resurgence (10547) [HoT buff when HP < 70%] -> Antithesis (10534) [damage/heal].
     - **Songcraft (`Romance`)**: Startling Strain (11934) [stuns & charms] -> Critical Discord (11973) [amplified damage vs charmed].
     - **Occultism (`Death`)**: Hell Spear (10135) [impale] -> Mana Stars (12759).
     - **Auramancy (`Will`)**: Thwart (16486) [shakes enemy / inspires self] -> Conversion Shield (11869) [anti-magic absorption].
  3. **Emergency Survival Flee (`EmergencyFlee`)**: When HP drops below critical threshold ($\le 20\%$), bot disengages and navigates away from hostiles to avoid dying.
  4. **Tactical Spacing & Kiting (`KiteSpacing`)**: When enemies penetrate the minimum safe range of ranged physical/caster classes ($< 12\text{m}$), bot steps/kites back 10m to re-establish $12\text{–}22\text{m}$ firing spacing.
  5. **Melee Gap Closing (`CloseGap`)**: Melee bots close distance into engagement reach before firing skills.
  6. **Integrated into Loop Scenarios**: Wired into `LevelingLoopScenario.HuntLeg`, `LevelLeg`, and `AbilityLevelLeg`.
  7. **Unit & Gate Evidence**: `CombatDecisionTreeTests` **13/13** green (+4 new class combo tests); `LevelingLoopScenarioRigTests` **45/45** green. Full `./scripts/gate.sh` passing with 0 compiler errors/warnings, in-game script compilation succeeded, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- Status: IMPLEMENTATION LANDED / 10-CLASS EXPANSION COMPLETE

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
  5. **Gear Scoring & Auto-Equipping Upgrades (`BotBagManager.CalculateGearScore`, `IsUpgrade`, `AutoEquipUpgrades`)**:
     - Evaluates gear score across item level, grade rarity, weapon DPS, and broken durability penalties.
     - Automatically equips upgrades from quest rewards or mob drops into optimal slots, displacing older gear into the bag.
     - Identifies displaced, strictly inferior gear as obsolete (`BotBagManager.IsObsoleteEquipment`), routing it into `AuditBag.TrashItems` for safe merchant sale.
     - Integrated directly into `LevelingLoopScenario.TryPerformBagMaintenance` and `EquipUpgrades`.
  6. **Unit & Gate Evidence**: `BotBagManagerTests` **7/7** green (+3 tests); `LevelingLoopScenarioRigTests` **40/40** green. Full `./scripts/gate.sh` passed cleanly with 0 compiler errors/warnings, in-game script compilation succeeded, **2,786 total tests (2,785 passed, 1 skipped)**, MCP BotControl 39 tools, MCP Archaeology 24 tools.
- Status: IMPLEMENTATION LANDED / ADVANCED GEAR UPGRADE LANDED

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

### PB-SOAK · Dormant-timer soak and cancellation blocker — HARNESS HARDENED / A5 CONJUNCTION CLOSED 2026-09-08 (evidence reconciled 2026-10-01)
- Scenario: Tier-3 dormant bots and long-running scheduler validation.
- **Correction (2026-10-01, evidence reconciliation — no grade promotion).** The
  previous wording here read "no six-hour dormant-timer soak exists in current
  evidence" and "6-hour soak remains open pending dedicated operational
  environment soak validation". That wording was stale as of 2026-09-06 and is
  withdrawn; the runs below exist. This correction records *evidence*, it does
  not promote any PlayerBot capability, autonomy, population, or H claim, and
  the tier-3 legs are evidenced at their own tested revisions — NOT at current
  HEAD `626548de92b8dca8af1f0f92e22d37c9fbe6074e`.
  - **G2-A5 FINAL Tier-3 exit conjunction** = (a) SHAPE + (b1) 6h quiescence-budget
    leg + (b2) actual timer progression (ROADMAP "A5 Tier-3 contract
    reconciliation", 2026-09-08).
  - **(a) SHAPE re-shown 4/4**, engine `a23cdc33eef55af18bfb1535f32ca9fc3ed62834`:
    `/root/aaemu-e2e-a5-shape/logs/g2-a5-tier3-report.json`, `runAtUtc`
    `2026-09-08T08:43:32.8072192Z` — verified in this report: `passed` verdicts
    `thousandDormantRegistered=true`, `embodiedWithin50=true`,
    `rssWithin15PctOfBaseline=true` (`rssDeltaPct -12.9`, 6204.9 → 5404.7 MB
    medians), `materializeP95Under3s=true` (`materializeP95Ms 256.2`); 50/50
    dematerialized on human leave. Note the report's own
    `sixHourDormantTimersLeg` field reads `PENDING — ... (out of scope for this
    measurement)`, i.e. (a) does not itself evidence (b1).
  - **(b1) 6h quiescence-budget leg PASS**, runAtUtc `2026-09-05T15:33:11.8271513Z`:
    `passed=true`, `failures=[]`, `windowCompleted=true`, `windowStatus=FULL`,
    window 360.0000879666667 min, `sampleCount=361`, `rssGrowthMb=6.7`
    (budget 512), DB writes 0/min. **Tested binary `322390b32`**; the report's
    `commit` field reads `ca7762d7daff9d0d7b6789ef43a7a00dff325626`, which is a
    docs-only commit — the tested *source* revision is `322390b32`.
  - **(b2) asserted soak PASS on .165**, runAtUtc `2026-09-06T02:49:08Z`,
    run 6h16m05s 1/1: `passed=true`, `failures=[]`, FULL 360.00001-min window,
    360 samples, RSS growth 25.4 MB (budget 512). **Tested binary `02903804b`.**
    STATUS records its own scoping caveat: "b2 passed AT revision `02903804b`,
    not at current HEAD ... the letter of the evidence is the tested revision."
  - **(b2) bounded restart leg PASS**, `Probe_A5Tier3RestartConservesDormantTimers`
    1/1 in 3m34s, HEAD `f2ef4b16f` (docs-only over b2 code `54f7e741a`),
    isolated root `/root/aaemu-e2e-a5-restart` (62xx ports, DB 63306).
  - All five revisions above are ancestors of current HEAD (262 / 194 / 82
    commits back respectively), so the runs are on this line of development —
    but **not re-verified at HEAD**.
- Harness blocker (RESOLVED, retained): `SeedBox` synchronous bridge calls and native `Thread.Join`
  were replaced by asynchronous `BotDriveClient.CallAsync`, cooperative `CancellationToken`
  propagation, and `Task.WhenAll` (`950cfd279`). Furthermore, `WaitBoot`, `WaitEmbodied`,
  `WaitDormantDiscovered`, and `SampleWindow` in `A5Tier3AcceptanceProbeTests` were hardened
  to accept `CancellationToken` and replace blocking `Thread.Sleep` with cooperative
  `cancellationToken.WaitHandle.WaitOne(...)`, aborting immediately on cancellation/timeout.
  Evidence: `BotDriveClientCancellationTests` 3/3 passed.
- Layer: BOT + SERVER (harness lifecycle and cancellation).
- **Still open (unchanged by the reconciliation above):** per-run soak ownership
  hardening is `799b698ad` (A5/A5Tier3 snapshot names account/character rows and
  cleans only newly owned IDs in `finally`; sibling-preservation tests 2/2). No
  tier-3 soak leg has been re-run at current HEAD `626548de`. The
  "preferably 12-hour" soak remains a ROADMAP *recommendation*, never a gate.
  Budgets were not relaxed and must not be.
- Status: HARNESS HARDENED / A5 CONJUNCTION CLOSED 2026-09-08 — tier-3 legs
  evidenced at their tested revisions, not re-verified at HEAD.


### PB-CORPSE-01 · Quest corpse-memory pin never records: 7/7 identity asserts return TryGetCorpse-false — OPEN, owner needed
- Scenario: quest bot kills its pinned prey via full decision-stack QuestDecisionScenario.Run on the real headless actor; any later wake asks QuestBehavior.TryGetCorpse(actor.ActorId, out var corpse) for the corpse record (ObjId + TemplateId + QuestId + detecting cycle).
- Intended: AliveToDead transition promotes the dead pinned target into the bounded per-bot record; re-observation preserves identity; irrelevant dead NPC never pins; fresh kill re-pins over stale; unresolved corpse keeps its pin.
- Observed vs expected: all 7 identity asserts fail with the SAME signature — TryGetCorpse returns false (no record created). Expected IsTrue() + corpse.ObjId == pinned objId. Nothing dispatches Loot off the missing record.
- Failing tests: AAEmu.UnitTests/Game/Core/Managers/Bots/QuestCorpseObserveTests.cs:58 AliveToDead_PromotesCorpse (:75 TryGetCorpse IsTrue); :85 SameIdentity_PreservedAcrossWakes (:100 IsTrue); :108 IrrelevantDeadNpc_Ignored (:126 IsTrue, :120 pre-kill IsFalse passes); QuestCorpseRepinTests.cs:58 FreshKill_ReplacesStaleRecord (:71/:90 IsTrue); :98 IrrelevantDead_WithStaleRecord_KeepsStale (:110 IsTrue); :218 RepinnedCorpse_PreservesIdentityAcrossWakes (same signature); QuestCorpseLootTests.cs:251 UnresolvedCorpse_PinKept_NoRelease (:267 IsTrue).
- Layer: BOT-SIDE (QuestBehavior corpse-memory) + HARNESS (fixture staging shared 251/3475 funnel, verified benign).
- Status: OPEN, owner needed.
- Evidence: RED-ON-CLEAN 7/7 at pristine HEAD e6e401129 (clean worktree /root/aaemu-cleanhead, per-test isolated processes, 143-233ms each; logs /tmp/cleanhead-logs/<Class>.<Method>.log). Contamination ruled out: rig hunk additive-safe, fixture blocks disjoint, all classes NotInParallel, names never keys.
- Suspected area: QuestBehavior.cs:642-647 NotePinnedCorpse guard (objId==0 || templateId != fixture.PreyTemplate → return) and/or funnel call sites :776/:859/:972/:1098 not reaching the pin arm. TryGetCorpse :664-667 (plain dict read) NOT suspect.
- Bounded next step: read QuestBehavior.cs:640-700 FIRST, trace which funnel arm the kill wake takes (:852-975), log whether NotePinnedCorpse is reached and with what objId/templateId vs fixture.PreyTemplate.
- Non-scope: not the reload-reconcile chain, not TalkChain, not stall/advance (PB-HELDOUT-01), not S5 arithmetic (PB-HELDOUT-S5).

### PB-HELDOUT-01 · Recovery mis-arbitration: S10 travel-vs-advance picks wrong; S7 second actor never retargets — OPEN, owner needed
- Scenario: heldout recovery scenarios drive QuestDecisionScenario.Run on staged fixtures (travel-arm prey at 26m; two actors racing one corpse then live prey).
- Intended: S10 resolves Move with destination toward live prey spawner; S7 second actor leaves the spent corpse and retargets live prey.
- Observed vs expected: S10 got AdvanceQuest where Move expected (:238); S7 retargeted=false (:135, second actor stays on spent corpse).
- Failing tests: HeldoutRecoveryTests.cs:227 Scenario10_TravelArm_PreyAt26m (:237 WorkSelected IsTrue, :238 Move, :239 Destination.HasValue IsTrue); :99 Scenario7_ContendedCorpseRace (:135 retargeted IsTrue).
- Layer: BOT-SIDE (dispatch/stall arbitration + spent-corpse release arm).
- Status: OPEN, owner needed.
- Evidence: RED-ON-CLEAN 2/2 at e6e401129 (isolated, 143-233ms; logs /tmp/cleanhead-logs/HeldoutRecoveryTests.<Method>.log).
- Suspected area: stall/advance arbitration (travel-arm vs AdvanceQuest dispatch, dispatch=held/stall-close-in Pursuit region) + spent-corpse release arm QuestBehavior.cs:935-938 / :1072-1075 (IsLootDispatched/terminal-empty exclusion holding 2nd actor on spent pin).
- Bounded next step: read QuestBehavior.cs dispatch/stall Pursuit-leg region FIRST, repro S7 2nd-actor wakes, log which arm holds CurrentTarget.
- Non-scope: not reload-reconcile, not TalkChain, not pin creation (PB-CORPSE-01), not S5 arithmetic, not this filing lane.

### PB-HELDOUT-S5 · Suite-invalid: regen-outpaces-DPS kill assert unsatisfiable by its own arithmetic — OPEN, owner needed (fix shape known, unapproved)
- Scenario: HeldoutScenarioTests Scenario5 stages npc Hp=MaxHp=100 then loops 30 wakes with npc.Hp = Min(MaxHp, Hp-2+5) (net +3/wake clamped at max).
- Intended (doc comment :232-236): the stall detector must trip instead of yielding forever.
- Observed vs expected: assert :260 (npc.Hp <= 0 IsTrue) can NEVER pass — from full HP the expression is Min(100,103)=100 every wake. The body asserts a kill the staging arithmetic precludes; no stall fact (SelectedAction/reason/count) is asserted.
- Failing test: HeldoutScenarioTests.cs:238 Scenario5_RegenOutpacesDps_NoKillWithin30Wakes (:260 assert).
- Layer: HARNESS (test-body arithmetic only; engine not suspect).
- Status: OPEN, owner needed; fix shape known (assert stall evidence — SelectedAction/reason/stall-count — not kill) but unapproved; test-body-only change when authorized.
- Evidence: RED-ON-CLEAN 1/1 at e6e401129; log /tmp/cleanhead-logs/HeldoutScenarioTests.Scenario5.log. E-19 suite-invalid postmortem concurs.
- Bounded next step: read HeldoutScenarioTests.cs:232-264 FIRST; pick ONE contract (stall-trips vs kill-lands) and align body/assert test-body-only.
- Non-scope: not reload-reconcile, not TalkChain, not cards 1/2, no engine change.

## FIXED (evidence retained)
### PB-007 · Flagged same-faction aggression handshake — FIXED / CLOSED 2026-08-27
- Scenario: PVP-01 slice 1 — two real Nuian TCP bots, attacker ForceAttack-flagged (CS 0x04f), casts Triple Slash 18131 on a co-located same-faction victim in e_steppe_belt (conflict group 14)
- Closure evidence: behavioral gate baseline `3871459d142fdd1767b9365a1de8d4cd3652ab0e`; current source/test HEAD is `792774d7707b8b578b8d9975896e0a1ac719f361`. Source commits `063beb7cd` (parser/live proof) and `b230bd8a2` (separate PB-002 item-use objective). Final isolated real-login/Game E2E passed 1/1 in 2m09.910s.
- AGGRESS-ALLOWED: victim-matched non-immune `SCUnitDamaged=True`; immune frames excluded=False; `SkillFired=True`; Retribution 2167=True; bloodstain doodad 877 objId 44294; crime branch observed.
- PEACE-BLOCK: passed with no victim-matched non-immune damage. WAR-HONOR remains intentionally deferred; broader PvP/honor scope is not closed.
- Historical failure context (retained): the prior immune-tagged/untrusted live result, login-protection window, and parser framing failure remain historical; the current report supersedes them without erasing that history.
- Evidence: `scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md`; deterministic parser tests 2/2.
- Status: FIXED / CLOSED for the narrow handshake requirement; WAR-HONOR separately deferred.

### PB-006 (ship-sailing) · Ship sailing physics non-functional live (2026-08-25) — FIXED 2026-08-26
- **⚠ Duplicate ID notice (added 2026-10-01).** The ID `PB-006` is used by TWO
  distinct blockers in this ledger. This one is **"Ship sailing physics
  non-functional live"** (FIXED 2026-08-26). The other is **"PlayerBot Reboot
  Persistence & Restoration Gap (Position Wipe & Inactive State)"** (FIXED
  2026-09-14). History is NOT renumbered. **Cite both by title, never by ID
  alone.**
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

### PB-006 (reboot-persistence) · PlayerBot Reboot Persistence & Restoration Gap (Position Wipe & Inactive State) — FIXED 2026-09-14
- **⚠ Duplicate ID notice (added 2026-10-01).** The ID `PB-006` is used by TWO
  distinct blockers in this ledger. This one is **"PlayerBot Reboot Persistence
  & Restoration Gap (Position Wipe & Inactive State)"** (FIXED 2026-09-14). The
  other is **"Ship sailing physics non-functional live"** (FIXED 2026-08-26).
  History is NOT renumbered. **Cite both by title, never by ID alone.**
- Scenario: PlayerBots created via `/bot add`, `/bot here`, manifests, or scripts persist across server reboots without loss of level, XP, equipment, quest progress, or world position
- Observed vs expected:
  1. Dormancy materialization unconditionally overwrote saved DB coordinates back to home/spawn (`<0, 0, 50>` / Nuian spawn) inside `RestoreHomePosition`
  2. Character SQL serialization swapped `roll` and `yaw` on `Character.Save`
  3. `Character.SaveDirectlyToDatabase` missed flushing `ItemManager.Instance.Save()` inside the transaction
  4. Server reboot left offline bot rows dormant in MySQL with no command to re-embody them unless proximity fidelity was enabled and a human player walked within 200m
- Layer: SERVER (Core / Managers / Bots / Character)
- FIXED 2026-09-14:
  1. Position preservation in `DormantBotRegistry.RestoreHomePosition`: preserves existing non-zero DB coordinates
  2. Fixed roll/pitch/yaw SQL parameter alignment in `Character.Save`
  3. Added `ItemManager.Instance?.Save(sqlConnection, transaction)` to `Character.SaveDirectlyToDatabase`
  4. Added `/bot restore [all|<name>|<id>]` command in `BotCmd` / `BotRestoreSubCommand` / `BotAdminService.Restore`
  5. Added configurable startup auto-restore (`BotAdminService.IsAutoRestoreEnabled` / `BotPresenceBootstrap`)
  6. Unit tests: `DormantBotRegistryTests` (10/10 green), `BotAdminServiceTests` (32/32 green), full gate 3211/0/1

