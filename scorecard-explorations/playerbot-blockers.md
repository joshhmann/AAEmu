# PLAYERBOT_BLOCKER ledger

When a bot cannot continue playing normally, it files a blocker here.
Blockers outrank speculative features in the backlog. Layer tags:
BOT-SIDE / SERVER / DATA / UNKNOWN.

Format: ID · scenario · intended action · observed vs expected · layer ·
evidence · status (OPEN/FIXED/WONTFIX-with-reason).

---

## OPEN

### PB-005 · NPC spawn Z is effectively unclamped — systemic ungrounded NPCs (floating / buried)
- Scenario: any placed NPC whose canonical `npc_spawns.json` z disagrees with local ground by ≥ 1 m (prod human report: "a lot of the NPCs are not really grounded — some floating, some under roads and clipping")
- Observed vs expected: the old spawn-time correction (`NpcSpawnerNpc.SpawnNpc`) only applied `GeoData.GetHeight` when |spawnerZ − newZ| < 1 m, allowing larger source-data offsets to reach clients unchanged
- Measured 2026-08-25 (offline engine-identical heightmap harness over all 25 118 main_world npc_spawns): of 23 058 defect-audited spawns (fly/swim excluded), 89.54 % grounded, 3.72 % minor float (0.5–2 m), **5.62 % severe float (> 2 m, worst +183.6 m)**, 2.91 % submerged (< −0.5 m, worst −270.3 m); plus 733 exact duplicate spawn rows
- Layer: SERVER (ineffective clamp) with DATA component (bad z clusters in `npc_spawns.json`, e.g. e_hasla_2 Citizens/Maid frozen at z=538.x on 355–430 terrain); remedy B duplicate/data overlay remains intentionally unimplemented pending owner decision
- Status: FIXED-PARTIAL — remedy C whitelist + remedy A positive-only spawn clamp landed 2026-08-26. Bounded replay of the audit matrix identifies 1 295 severe positive offsets: 702 intentional aerial/water/structure whitelist rows remain unchanged, while all **593 non-whitelisted severe-float rows** are corrected to sampled terrain at spawn. The clamp leaves all offsets below 2 m intact and preserves all negative offsets; cave/interior floors and road/deck meshes remain unobservable to the terrain-only sample, so submerged counts are not claimed fixed. Hasla frozen-Z Citizens/Maid/Ravra and Hasla Guard/Sentry families remain non-whitelisted. Duplicate-row ownership remains OPEN.
- Evidence: `scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md` (method §1, tables §2–4, classification §5); harness `/root/npc-grounding-harness/`; raw matrix `/tmp/ng.tsv`; targeted policy tests 13/13 pass; `dotnet build AAEmu.Game/AAEmu.Game.csproj --no-restore` pass; compact.sqlite3 SELECT-only, no behavior/data rows changed

## FIXED (evidence retained)

### PB-001 · Straight-line movement blocks interior/travel gameplay — FIXED 2026-08-27
- Scenario: bot travels beyond open courtyards (Deadmine tunnels, cross-region routes)
- Intended: navigate terrain/obstacles to reach objective
- Observed: straight-line walk; stuck detection fires (M7#5) but no route exists
- Layer: BOT + SERVER (no navmesh/waypoint network)
- Evidence: M7 spike shortcuts on record; soak run-1 drowning (fixed at home-anchor level)
- Fix: CryEngine `.bai` navigation engine hardened (G-cost fix, binary heap, per-block spatial grid, `BaiNavigationRigTests` 6/6 green, corridor detour improved from 1.91x to 1.22x). `IGameplayActor.NavigateTo` routed navigation contract landed and verified with `GameplayActorNavigateTests` (6/6 green) supporting multi-waypoint navigation with automatic A* pathfinding over GeoData, waypoint stepping, and graceful fallback to direct leg.
- Status: FIXED

### PB-002 · Progression ceiling: no viable quest content past curated Solzreed slice for bots — FIXED 2026-08-27
- Scenario: bot finishes golden-route chain (~lvl 20 equivalent), seeks next quests
- Intended: continue leveling via real quest content
- Observed: bots provision artificial levels; no autonomous next-quest selection
- Layer: DATA + BOT (quest discovery/perception primitive missing: "find available quests at my level nearby")
- Evidence: adventurer v1 runs curated chains only
- Fix: QuestDiscovery perception primitive landed (c1073d883, verified through the real AddQuest gate + canonical smoke). Full autonomous quest loop composed in `LevelingLoopScenario` supporting Talk (`QuestActObjTalk`, `QuestActObjTalkNpcGroup`), Hunt/Kill (`QuestActObjKillMonster`, `QuestActObjKillMonsterGroup`), Gather/Interact (`QuestActObjActDoodad`), and delivery quests, alongside autonomous sustain recovery (HP < 35% consumable usage) and auto-equipping item upgrades. Verified across 6/6 `LevelingLoopScenarioRigTests` and full solution gate.
- Status: FIXED

### PB-007 · Flagged same-faction aggression fired but dealt ZERO damage — FIXED 2026-08-27
- Scenario: PVP-01 slice 1 — two real Nuian TCP bots, attacker ForceAttack-flagged (CS 0x04f), casts Triple Slash 18131 on a co-located same-faction victim in e_steppe_belt (conflict group 14)
- Intended: flagged aggression lands damage AND fires the crime branch (Retribution 2167 + bloodstain evidence) per `damage_effects.check_crime=1` on effect 3218
- Observed vs expected (root cause): acquisition passes (`GetInitialTarget` Hostile case → ForceAttack exception, BaseUnit.cs:100-103), AoE relation filter keeps the Friendly victim (`canAtk=True`), all per-effect gates pass (`effectsToApply=1`). Two defects resolved: (1) `DamageEffect.Apply` immune path didn't register crime for attempts — extracted to `RegisterCrimeForAttempt` so assault state / Retribution / evidence register even if damage is immuned. (2) `BotTcpLink` E2E client did not decompress Level 4 `CompressedGamePackets` (Deflate payload), causing `SCUnitDamagedPacket` to be hidden in raw compressed bytes. (3) Harness updated to wait out the 20s login-protection immunity buff (buff 2423 "LoggedOn").
- Layer: SERVER + CLIENT-TEST HARNESS
- Fix: (a) ENGINE — `DamageEffect.RegisterCrimeForAttempt` invoked for landed damage and immune attempts; error logging rethrow in `Skill.ApplyEffects`. (b) HARNESS — `BotTcpLink` parses and decompresses Level 4 `CompressedGamePackets` (Deflate payload); `PvpHandshakeE2eTests` waits out the login-protection window and validates damage + buff frames concurrently. (c) RIG TESTS — `AAEmu.UnitTests/.../PvpAggressionSeamRigTests.cs` (6/6 green).
- Evidence: Live E2E PASS (`/root/aaemu-e2e/logs/pvp-handshake-e2e-report.json`, test `AAEmu.IntegrationTests.E2e.PvpHandshakeE2eTests`): 6/6 stages green (`PROVISION`, `HOMELAND-SHIELD`, `RELOCATE-STEPPE`, `LIVE-ZONE-STATE`, `FLAG-FORCEATTACK`, `AGGRESS-ALLOWED`, `PEACE-BLOCK`), verdict PASS; full test gate 2480/2479 succeeded, 0 failed, 1 skipped.
- Status: FIXED

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

