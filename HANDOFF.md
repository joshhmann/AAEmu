# AGENT HANDOFF — 2026-08-25 (session transfer doc)

New agent/harness starting cold? Read this, then STATUS.md → ROADMAP.md →
scorecard-explorations/playerbot-blockers.md → scorecard-explorations/
mechanics/playerbot-capability-matrix.md. Those four files ARE the working
memory; everything below is the delta on top.

## Where the project stands

- **Branch:** fork `joshhmann/AAEmu` develop @ `e672b9579` (pushed). Gate
  **2360/0/1** green at tip.
- **Milestones M0–M7 functionally complete** (quest spine, golden path,
  homestead, trade/transport, actor contract, bot framework, adventurer+party
  bots). This week added: fishing loop live-proven, expeditions for bots,
  duels verified + stuck-player bug fixed, transfers fixed (never worked
  before), quest noops un-broken (517 quests), zone war states, mate equip,
  item procs, goal arbitration, true dormancy, proximity fidelity.
- **Prod:** Mai deploying `2cc75ff9f`+ via kanban card t_3bf02699 (deploy +
  full bot-regression sweep + soak stage 1). Deploy card:
  `deployments/deploy-card-20260825.md`. Runbook:
  `Docs/wiki/Docker-Installation-Guide.md` § Production redeploy.
- **Test force:** `Scripts/e2e/bot-regression-pass.sh` runs all seven live
  bot scenarios in one command (needs Docker; republishes runtime first).

## In-flight / next wave

1. **A5 acceptance run** — rerun ScalingProbeTests with
   `AAEMU_BOT_TRUE_DORMANCY=1` + ~100 dormant registered / ~10 embodied;
   target numbers: RSS within 15% of no-bot baseline, materialize p95 < 3s.
2. **Quest-discovery perception primitive** (PB-002): bots find available
   quests nearby instead of scripted chains → unlocks autonomous leveling.
   Unblocks: PB-002 in playerbot-blockers.md.
3. **Doodad-interact contract action** — generalize fishing's portal
   injection into first-class `InteractWith(doodad)` (unlocks dungeon portals,
   fish stands, world interactables).
4. **A4 acceptance measurement**: autosave p95 < 2s @ 250 characters.

## Josh-owned decisions (do not guess)

1. Physics recalibration (~0.3/min case documented, ROADMAP M7 queue #7) —
   blocks long soaks
2. ConAcceptComponent design (274 quests)
3. Sports-fishing trigger design
4. Prod GO confirmation (handoff card filed)

## Hard rules (see AGENTS.md + ROADMAP)

- NEVER push branches/PRs to upstream AAEmu/AAEmu — one-way intake only
- compact.sqlite3 is READ-ONLY reference (SQL/patches/compact for overlays)
- Bots exercise REAL engine paths — an optimization that bypasses the system
  under test destroys its purpose
- H (human-feel) is never recorded from bot evidence — H stays UNKNOWN until
  Josh plays it
- New systems: reconstruct from evidence (dossiers in
  scorecard-explorations/mechanics/), grade VERIFIED/INFERRED/PLAUSIBLE/UNKNOWN
- Blockers go in playerbot-blockers.md, layer-tagged, feeding backlog

## Key commands

```bash
./scripts/gate.sh                                   # full unit gate (~50s)
E2E_REBUILD=1 ./Scripts/e2e/bot-regression-pass.sh  # full live bot sweep (~40min)
SCENARIOS="duels" ./Scripts/e2e/bot-regression-pass.sh   # subset
SCHEDULER_SOAK_MINUTES=30 dotnet test --project AAEmu.IntegrationTests --filter-class "AAEmu.IntegrationTests.E2e.Gate.SchedulerSoakStage1Tests"
dotnet publish AAEmu.Game/AAEmu.Game.csproj -c Release -o /root/aaemu-e2e/runtime/game  # refresh E2E runtime
./scripts/aaemu-prod-intake <sha>                   # prod deploy handoff card → Mai
```

## Gotchas

- E2E runtime does NOT auto-rebuild — publish or set E2E_REBUILD=1
- IntegrationTests = xUnit (`--filter-class`), UnitTests = TUnit
  (`--treenode-filter "/*/*/Class/*"`); treenode filter rejects `|`
- Subagent delegations: one at a time worked; parallel calls got cancelled
- compact.sqlite3 access in research: sqlite3 mode=ro only
- Test rigs: ZoneManager/ChatManager/etc. singletons need seeding — see
  ExpeditionManagerRigTests + PartyLifecycleFaultMatrixTests conventions
