# PLAYERBOT_BLOCKER ledger

When a bot cannot continue playing normally, it files a blocker here.
Blockers outrank speculative features in the backlog. Layer tags:
BOT-SIDE / SERVER / DATA / UNKNOWN.

Format: ID · scenario · intended action · observed vs expected · layer ·
evidence · status (OPEN/FIXED/WONTFIX-with-reason).

---

## OPEN

### PB-001 · Straight-line movement blocks interior/travel gameplay
- Scenario: bot travels beyond open courtyards (Deadmine tunnels, cross-region routes)
- Intended: navigate terrain/obstacles to reach objective
- Observed: straight-line walk; stuck detection fires (M7#5) but no route exists
- Layer: BOT + SERVER (no navmesh/waypoint network)
- Evidence: M7 spike shortcuts on record; soak run-1 drowning (fixed at home-anchor level)
- Status: OPEN — waypoint-network or coarse-route design needed before dungeon interiors

### PB-002 · Progression ceiling: no viable quest content past curated Solzreed slice for bots
- Scenario: bot finishes golden-route chain (~lvl 20 equivalent), seeks next quests
- Intended: continue leveling via real quest content
- Observed: bots provision artificial levels; no autonomous next-quest selection
- Layer: DATA + BOT (quest discovery/perception primitive missing: "find available quests at my level nearby")
- Evidence: adventurer v1 runs curated chains only
- Status: OPEN — QuestDiscovery perception primitive LANDED 2026-08-25 (c1073d883, verified through the real AddQuest gate + canonical smoke); remaining open = zone-by-zone runnable-content sweep (which offers exist within walking distance per zone/level band — spawn positions joined with the new QuestManager offer indexes) + autonomous leveling composition on top. Zone sweep LANDED 2026-08-25: scorecard-explorations/generated/quest-discovery-sweep-2026-08-25.md (3,000 discoverable quests / 57 zone groups mapped; recommended loops Solzreed→Lilyut→Gweonid and the 45-quest Tiger Spine→Mahadevi→Sunrise chain; ~900 quests on offer channels not yet surfaced by DiscoverQuests). First autonomous loop segment LANDED (bots/leveling-loop): delivery+ItemGather chain completes unprompted; remaining = kill-leg composition, talk/interaction/cinema primitives, live E2E.




### PB-005 · NPC spawn Z is effectively unclamped — systemic ungrounded NPCs (floating / buried)
- Scenario: any placed NPC whose canonical `npc_spawns.json` z disagrees with local ground by ≥ 1 m (prod human report: "a lot of the NPCs are not really grounded — some floating, some under roads and clipping")
- Observed vs expected: the only spawn-time correction (`NpcSpawnerNpc.SpawnNpc`, `AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs:97-104`) applies `GeoData.GetHeight` only when |spawnerZ − newZ| < 1f, and `WorldManager.GetReferenceHeight` (`WorldManager.cs:907-921`) returns spawner Z verbatim for flyers and Idle/HoldPosition AI; `SCUnitStatePacket.cs:137-140` then sends that height to clients — so data z errors persist to players forever
- Measured 2026-08-25 (offline engine-identical heightmap harness over all 25 118 main_world npc_spawns): of 23 058 defect-audited spawns (fly/swim excluded), 89.54 % grounded, 3.72 % minor float (0.5–2 m), **5.62 % severe float (> 2 m, worst +183.6 m)**, 2.91 % submerged (< −0.5 m, worst −270.3 m); plus 733 exact duplicate spawn rows
- Layer: SERVER (ineffective clamp) with DATA component (bad z clusters in `npc_spawns.json`, e.g. e_hasla_2 Citizens/Maid frozen at z=538.x on 355–430 terrain); remedy options A–C in `scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md` §6
- Status: OPEN-PENDING-OWNER
- Evidence: `scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md` (method §1, tables §2–4, classification §5); harness `/root/npc-grounding-harness/`; raw matrix `/tmp/ng.tsv`; READ-ONLY audit, compact.sqlite3 SELECT-only, no behavior changed

### PB-006 · Ship sailing physics non-functional live: hull spawns ~100 m in air, zero movement replication (2026-08-25)
- Layer: SERVER (+ contributing water-surface DATA fallback)
- Evidence: SHIPS-01 slice-1 rowboat E2E (`AAEmu.IntegrationTests.E2e.RowboatE2eTests`,
  isolated stack /root/aaemu-e2e-boat). Real item-use summon chain works end-to-end
  (scroll 15817 → skill 15802 → SpawnSlave → SCSlaveCreated on the wire), but the hull
  registers/ticks in Jitter2 at pos Z≈99.7 with vel=(0,0,0) forever —
  `PhysicsManager.DefaultWaterLevel = 100f` fallback poisons the spawn-height query at
  genuine ocean coords (character z=0.05 nearby) — and ZERO SCOneUnitMovementPacket(Ship)
  frames reach the client despite per-tick `slave.BroadcastPacket` execution.
  Full stage table + log excerpts: scorecard-explorations/generated/ships-rowboat-e2e-report.md.
- Status: OPEN — fix needs (a) water-surface/ocean-level correctness in SlaveManager boat
  spawn, (b) physics→client movement replication for Slave units (GetAround/encode path).


## FIXED (evidence retained)
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

