# Navigation Domain — System Archaeology Dossier

**Purpose:** decide HOW bot navigation gets solved (PB-001) before anyone touches
engine code. Every claim is graded:

| Grade | Meaning |
|---|---|
| **VERIFIED** | Author opened the cited file/query this session (@ develop `e672b9579`). |
| **STRONGLY_INFERRED** | Loader/consumer code verified; runtime data presence inferred from it. |
| **PLAUSIBLE** | Consistent with evidence but not directly checked. |
| **UNKNOWN** | Not determinable this session. |

Scope guard: this dossier decides strategy only. No engine code was written;
ROADMAP.md / STATUS.md / capability-matrix are owned by other sessions.

---

## 1. WHAT EXISTS TODAY

### 1.1 The bot movement path (VERIFIED)

The wire entry point is **`CSMoveUnitPacket`** (opcode `0x089`,
`AAEmu.Game/Core/Packets/C2G/CSOffsets.cs:133`; handler
`AAEmu.Game/Core/Packets/C2G/CSMoveUnitPacket.cs:9-113`). Its `Execute()`
switches over three move-type families and delegates:

- `ShipRequestMoveType` → ship request handling (`CSMoveUnitPacket.cs:70-87`)
- `VehicleMoveType` → `VehicleMovementModel.ApplyVehicleMove`
  (`CSMoveUnitPacket.cs:88-100`)
- `UnitMoveType` → `VehicleMovementModel.ApplyUnitMove`
  (`CSMoveUnitPacket.cs:101-108`)

**`VehicleMovementModel`**
(`AAEmu.Game/Models/Game/Units/Movements/VehicleMovementModel.cs:16-31`) is a
behavior-preserving extraction: the methods ARE the CSMoveUnitPacket handler
code — position apply + `SCOneUnitMovementPacket` broadcast +
`FinalizeTransform`. Its own doc-comment states the contract: "a client's
packet and the actor contract share one path."

Bots ride exactly this path. `GameplayActor.MoveTo`
(`AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:192`) creates a Move request;
each tick the leg advances by `_moveSpeed * elapsed` toward the destination
(`GameplayActor.cs:2883`) and lands through `VehicleMovementModel.ApplyUnitMove`
with `BuildCharacterMove` (`GameplayActor.cs:3065-3066`). There is **no
pathfinding**: the doc-comments say so explicitly ("straight-line lerp (no
pathfinding)" — `AdventurerSpikeScenario.cs:50`; "level-50 provisioning,
straight-line Move (no pathfinding)" — STATUS.md M7 notes, line 126). When
`BroadcastMovement` is off (headless rigs), the same state is applied without a
packet (`GameplayActor.cs:3049-3063`).

Headless roam adds its own layer in **`BotRoamStepExecutor`**
(`BotRoamStepExecutor.cs:21-30`): a `BotPath` waypoint loop issued as
consecutive `MoveTo` legs (`:115-118, :176-180`), ground-clamp of Z to the
heightmap via `WorldManager.GetReferenceHeight` ("the Simulation.cs:394
pattern", `:232-235`), and throttled `SCOneUnitMovementPacket` broadcast at
~4-6 Hz vs the NPC ~10 Hz cadence (`:26-30`). `BotPath`
(`Core/Managers/Bots/BotPath.cs:19-38, :82-83`) interpolates flat X/Y with
proportional Z through the engine's `AddDistanceToFront`.

Patrol routes are generated terrain-aware rounded squares around a home anchor
(`BotPresenceCoordinator.cs:507-557`); schedule phases reuse the identical
surface (`BotScheduleBehavior.cs:10-18, :67-71` — "schedules add no parallel
movement system"). Headless embodiment itself requires explicit placement into
the region graph, because a headless bot never sends a first CSMoveUnitPacket
(`PlayerBotLifecycleAdapter.cs:19-56`).

### 1.2 Stuck detection (VERIFIED)

M7 hardening #5 lives in `GameplayActor`:

- `DefaultNoProgressWindow = 2.5 s` (`GameplayActor.cs:111`)
- `MaxUnstickNudges = 1` bounded lateral recovery legs of
  `UnstickNudgeDistance = 2 f` (`GameplayActor.cs:114-132`)
- Per-tick sample `UpdateMoveStuckState`: displacement > arrival radius resets
  the timer; expiry schedules the nudge or fails the request
  (`GameplayActor.cs:2876-2881, :2985-2994`)
- Declaration semantics: `TimedOut(ActorFailureReason.Navigation)` via
  `ActorRequest.Expire` (`GameplayActor.cs:2993-2994`); `Navigation` is one of
  seven spec §17 reasons — "bot got stuck" is never a reason
  (`IGameplayActor.cs:28-31, :843-847`).

Arrival radius is 0.5 f ("same checkpoint model as Simulation.RangeToCheckPoint",
`GameplayActor.cs:69-70`). STATUS.md confirms the live behavior: "movement
stuck detection — NoProgressWindow 2.5s → TimedOut(Navigation) 'stuck' + one
unstick nudge" (STATUS.md:237-238). PB-001 records what happens next when there
is no route: the bot fails and the objective starves
(`playerbot-blockers.md:14-20`). The soak run-1 drowning incident (bots walked
4.3 km into the sea because the fallback home anchored routes kilometers from
the spawn) is recorded at STATUS.md:291 and `BotPresenceCoordinator.cs:384-388`;
it was fixed at the home-anchor level, not by navigation.

### 1.3 World spatial structures available (VERIFIED)

`AAEmu.Game/Core/Managers/World/WorldManager.cs`:

- **Regions**: 64 m sectors (`REGION_SIZE = 64`, `:122`); each world instance
  holds a `Region[CellX*SectorsPerCell, CellY*SectorsPerCell]` grid
  (`WorldManager.cs:564-574`). Region membership drives visibility
  (`AddVisibleObject`, `:1042-1094`), neighborhood polling
  (`GetNeighbors` with `REGION_NEIGHBORHOOD_SIZE = 2`, `:140-144, :969-982`),
  and cheap proximity queries (`GetAround`, `:1164-1175`).
- **Zones**: `ZoneKeyByRegions[sx, sy]` maps any coordinate to its zone key
  (`GetZoneId`, `:804-814`) — a ready-made coarse zone graph keyed off the same
  region lattice.
- **Tick budget**: `ActiveRegionTick` processes characters round-robin and
  mates/slaves/spawners drop-defer under a hard 100 ms pass budget
  (`WorldManager.cs:152-257`) — the scaling envelope any per-bot pathfinding
  must respect (also playerbot-capability-matrix.md:52-54).
- **Terrain**: heightmap constants (`SECTOR_HMAP_RESOLUTION`,
  `CELL_HMAP_RESOLUTION`, `:130-137`), `LoadHeightmaps()` gated on
  `AppConfiguration.Instance.HeightMapsEnable` (`:714-731`),
  `GetHeight(zoneKey…)`/`GetHeight(Transform)` that consult **GeoData first,
  then heightmaps** (`:826-859`).
- Water bodies are modeled (`Models/Game/World/WaterBodies.cs`,
  `WaterBodyArea.cs` — file listing verified; semantics not read this session:
  PLAUSIBLE that they suffice for swim/drown gating).

`Models/Game/World/` also carries `Zone/Area` definitions, `WorldInstance`,
`ShipStaticBarrierZones`, and the CryEngine XML mirrors (`XmlWorld*` — file
listing verified; contents not read: UNKNOWN depth).

### 1.4 Geodata / client-file assets the server already loads (VERIFIED code; data presence STRONGLY_INFERRED)

`ClientFileManager` (`AAEmu.Game/IO/ClientFileManager.cs:6-171`) abstracts
multiple sources — extracted directories or `game_pak` AAPak archives
(`AddSource`, `:20-66`; configured in `Configurations/ClientData.json`, whose
`Sources` list includes `ClientData/game_pak` and `.server_files` mirrors).

Under `World.GeoDataMode` (**default `true`** in
`Configurations/World.json:19`) the server loads **CryEngine AI navigation
data (.bai files)** from the client data:

- `BaseBaiLoader.LoadBaiFilesFromFolder`
  (`Models/CryEngine/Loaders/BaseBaiLoader.cs:21-190`) parses four families per
  zone/cell folder: `areasmission*.bai` (areas incl. **forbidden areas**),
  `netmission*.bai` (**navigation node/link graph**),
  `vertsmission*.bai` (obstacle vertices), `hidemission*.bai`.
- Loading seams: per-zone at boot (`WorldTemplate.LoadZoneBaiFiles`,
  `Models/Game/World/WorldTemplate.cs:216-233`) and per-cell on demand
  (`WorldCell.LoadBaiFiles`, `Models/Game/World/WorldCell.cs:59-125`).
- `AiGeoDataManager` (`Core/Managers/AiGeodataManager.cs:17-536`) exposes the
  graph: `GetAvailablePoints` over `LinkDescriptorList` (`:21-24`), forbidden-
  area polygon tests (`CheckImpossibleWalk`/`IsInPolygon`, `:33-76`), nearest
  node search (`FindСlosestToTheCurrent`, `:191-252`), navmesh-based height
  (`GetHeight`, `:259-333`), path smoothing (Douglas–Peucker `:99-185`,
  `ReducePath` with slope-gated node skipping `:388-420`), and
  `StickToFloor` (`:515-535`).
- **A\* already exists**: `PathNode.FindPath(WorldInstance, start, goal)`
  (`Models/Game/AI/AStar/PathNode.cs:74-154`) searches the netmission graph
  with forbidden-area filtering in `GetNeighbours` (`:187-234`), a loop cap
  (`maxLoopsLeft`, `:102`), and Douglas–Peucker post-reduction (`:119`).
- NPC combat behaviors call it under GeoDataMode: `Npc.FindPath(Unit)`
  (`Models/Game/NPChar/Npc.cs:1457-1467`) invoked from
  `BaseCombatBehavior.cs:145-162` (with an explicit perf-warning stopwatch,
  `:154-159`) and flytrap AI. A GM visualization command exercises the whole
  chain: `Scripts/Commands/TestNavMesh.cs:52-86` (closest-node lookup →
  FindPath → ReducePath → doodad markers).
- The same .bai polygons feed ship collision: `ShipStaticBarrierBaiIngestor`
  lazily converts coastal `AreasMissionReader` polygons into
  `ShipStaticBarrier` entries under GeoDataMode
  (`Core/Managers/World/ShipStaticBarrierBaiIngestor.cs:13-16, :76-78`;
  consumed by `PhysicsManager.cs:359-363`).
- NPC cliff safety: where navmesh is missing, a slope/step gate stops the
  straight-line fallback from climbing walls (`Npc.IsStepBlocked`,
  `Npc.cs:1170-1201, :1319-1328`; config `NpcMaxStepHeight = 0.5`,
  `Configurations/World.json:21`).

Known weak points in this machinery (all VERIFIED in code):

1. `GetBaiByPos` returns "the actually correct zone" as an open TODO — it
   currently returns the first zone loader (`WorldTemplate.cs:235-247`).
2. Nearest-node and height lookups are linear scans over all nodes of a chunk/
   zone (`AiGeodataManager.cs:232-248, :270-314`; `Npc.cs:1201-1204` documents
   that this is "far too expensive for per-tick per-candidate sampling").
3. The A\* neighbor expansion has a **G-cost bug**:
   `PathLengthFromStart = (source − EndPointPos).Length()` — distance *to the
   goal*, not accumulated from start (`PathNode.cs:226`); the old correct call
   sits commented out. Heuristic quality is degraded, not correctness of
   connectivity.
4. `FindClosestIndexPoint` initializes `minDistance = 0f` and only improves on
   strictly smaller distances (`AiGeodataManager.cs:363-379`) — dead/broken
   helper (unused by the main path: PLAUSIBLE, callers not exhaustively
   enumerated).

Whether the deployed 1.2 `game_pak` contains full `netmission*.bai` coverage
for both continents is **UNKNOWN this session** — this machine has no client
data installed (`.client_files/` absent, `.server_files/` contains only
readme.txt; bash listing verified). The loaders above are production code paths
in upstream AAEmu, hence STRONGLY_INFERRED usable, but coverage quality must be
measured before commitment (see §5).

### 1.5 How NPCs move today (VERIFIED)

- **Route engine**: `Simulation` (`Models/Game/Units/Route/Simulation.cs:25-203`)
  — checkpoint walker (`RangeToCheckPoint = 0.5f`, `MoveStepIndex`) plus a GM
  **route recorder** ("rec"/"save" writes `./Data/Path/*.path` as pipe-
  delimited x|y|z rows, `Simulation.cs:74-92, :190-203`). This is how the
  existing `.path` files were authored.
- **AI paths**: `AiPathsManager` loads `Data/Path/*.path`
  (`Core/Managers/AiPathsManager.cs:9-105`); `AiPathHandler` walks them as a
  looping queue of `AiPathPoint`s with per-point actions (speed, flags,
  loop toggles) (`Models/Game/AI/v2/Controls/AiPathHandler.cs:14-121`);
  spawners attach them via the `FollowPath` name (`NPChar/NpcSpawnerNpc.cs:131-134`)
  sourced from the JSON spawn data (`Data/Worlds/main_world/npc_spawns.json`,
  e.g. lines 12156, 62339); AI command sets reference them too
  (`ai_commands` rows sampled this session; comment table in
  `Models/Game/AI/Enums/AIEnums.cs:2-7`). Coverage is tiny: **13 `.path`
  files, 271 total points** (bash count), **5 spawners with `FollowPath`**
  in main_world spawns.
- **Chase/flee**: straight-line with the geodata A\* overlay described in §1.4.

### 1.6 How ships/slaves/transfers/mates move today (VERIFIED)

- **Ships (Slaves)**: full Jitter2 rigid-body simulation — `ShipController`
  (`Physics/ShipController.cs:22-83`) with throttle/steering/wind/buoyancy
  force models; static-barrier collision ingested from .bai polygons (§1.4).
  Continuous steering, not waypoint rails.
- **Transfers (fixed-route transports)**: canonical waypoint-rail followers.
  Route data comes from two sources joined by name: compact.sqlite3 table
  `transfer_paths` (schema + **444 rows** verified by query this session;
  loader `GameData/TransferGameData.cs:112-135`) and per-zone client XML
  `game/worlds/<world>/level_design/zone/<zoneId>/transfer_path.xml`, parsed to
  world coordinates at load (`TransferGameData.cs:138-215`). A spawned
  Transfer chains its road segments into `Routes` and starts at segment 0
  (`Models/Game/Units/Transfers/TransferSpawner.cs:62-68`);
  `TransferManager.TransferTick` ticks every active transfer
  (`Core/Managers/TransferManager.cs:39-53`); `Transfer.MoveTo` walks
  checkpoints (`MoveStepIndex`), handles waits, reverses direction at ends, and
  broadcasts `SCOneUnitMovementPacket` for the motor and every attached child
  (`Models/Game/Units/Transfer.cs:428-494, :557-666`).
- **Mates (pets/mounts)**: no server-side follow-AI was found — mounts/pets
  ride `VehicleMovementModel.ApplyUnitMove` (the client sends their moves);
  sticky-parent tracking detaches on dismount
  (`VehicleMovementModel.cs:160-162`). Bot-side "follow" composes ordinary
  `MoveToUnit` legs instead (`PartyFollowAssistScenario.cs:7-13, :96-99`).
  Grade: absence-of-follow-AI is VERIFIED-by-search (grep over
  FollowOwner/follow patterns returned nothing server-authoritative); whether
  canonical clients handle mate pathfinding client-side is UNKNOWN.

---

## 2. WHAT CANONICAL 1.2 DATA OFFERS

### 2.1 compact.sqlite3 (VERIFIED against the local copy, 679 tables enumerated)

- **No navmesh, no generic waypoint/path tables.** Table-name scan for
  path/way/nav/road matched only map-marker doodad funcs (`doodad_func_navi_*`
  — minimap navigation markers, unrelated to movement) and the transfer family.
- `transfer_paths` (+ `transfers`, `transfer_bindings`,
  `transfer_binding_doodads`): real, populated — **444 transfer path segments**,
  owner_type `Transfer` throughout.
- `ai_files` / `ai_commands` / `ai_command_sets`: AI script text + command
  sets (325 sets counted); commands of category `FollowPath` reference
  **file names**, not embedded coordinates — the coordinates live in
  `Data/Path/*.path`, which are fork-authored recordings, not canonical data
  (13 files / 271 points, §1.5).
- `npc_spawner_npcs` has **no `follow_path` column** (query error verified);
  the follow-path association arrives via the JSON spawn export instead.

**Grade:** compact.sqlite3 offers essentially nothing for bot routing beyond
transfer rails. Any claim of a hidden canonical waypoint table would be
fabrication — none exists in this schema.

### 2.2 Client files (game_pak)

- **.bai navigation data** — `netmission*.bai` node/link graphs +
  `areasmission*.bai` forbidden areas + `vertsmission*.bai` obstacles: loader
  support VERIFIED (§1.4); presence and coverage in the specific 1.2
  (`r208022`) pak: STRONGLY_INFERRED from loader maturity + upstream usage,
  UNKNOWN measured. These are zone-graph networks (designer-authored AI
  navigation), not dense Recast-style navmeshes — expect sparse corridor
  graphs, which suits coarse bot travel but not precise interior navigation.
- **`transfer_path.xml` per zone** — VERIFIED loader; canonical waypoint rails
  for carriages/airships.
- **Heightmaps** — cell height data read from client data
  (`LoadCellHeightMapFromClientData` call chain, `WorldManager.cs:687-709`,
  `WorldCell.cs:123-125`); `Tools/WorldConverter` README VERIFIED: offline tool
  that generates pre-baked heightmap data from an unpacked `game/worlds` tree
  (`Tools/WorldConverter/README.MD:1-12`).
- What the server cannot read today: no Recast/GNX mesh parser, no road/network
  graph beyond the above. UNKNOWN whether the pak ships additional unused nav
  formats (would require a pak inventory on a machine with client files).

**Usable-vs-not verdict:** the client data gives us (1) a sparse AI node-link
graph with forbidden areas — already wired to A\*, (2) transfer rails —
already wired to a follower, (3) heightmaps — already wired to clamping.
Nothing else is needed for coarse travel; nothing present solves dungeon
interiors better than the node graph already does.

---

## 3. NEIGHBORING EVIDENCE — constrained movement the project already solved

1. **Transfer rails** (§1.6): shared immutable route data + dumb per-follower
   checkpoint walk + movement broadcast. Scales flat: 444 route segments are
   loaded once; every carriage costs only tick arithmetic. This is the
   architecture shape the fidelity ladder wants for background bots.
2. **Ship physics steering** (§1.6): proves continuous steering under a shared
   physics thread is viable for vehicles, and that GeoDataMode-derived barriers
   already constrain it. Not reusable for ground bots (water-only model), but
   the PhysicsManager seam (`PhysicsManager.cs:359-363`) shows how optional
   geodata layers plug in.
3. **Mate/character riding** (§1.6): one movement model serves packets and
   actors — the bot Drive action is byte-equivalent with a client driver
   (`EconomyDayCycleScenario.cs:974-978`, `GameplayActor.cs:2929-2942`).
   Consequence: whatever route source we add, execution stays on
   `VehicleMovementModel` — no parallel movement system (AGENTS.md rules #9/#10).
4. **NPC A\* chase** (§1.4): the project already trusts `PathNode.FindPath` for
   live NPC combat under the default config. Reusing it for bots is a
   composition, not a new subsystem.
5. **Dormancy/proximity tiers** (capability matrix, Scaling posture
   `playerbot-capability-matrix.md:27-66`): non-embodied bots are DB rows with
   no tick; materialization is proximity-budgeted. A background-travel bot can
   therefore traverse a coarse route mostly dormant, waking only for leg
   checkpoints — the scaling cost of routing becomes amortized planning, not
   per-bot-per-tick search.

Reusable pattern synthesis: **immutable shared route/graph data (loaded once) +
cheap per-follower checkpoint advance + single movement-model execution +
stuck detection as the safety net.**

---

## 4. STRATEGY DECISION FRAME

### Option (a) — Waypoint network built from NPC path data

- **Evidence strength:** WEAK for coverage. Total corpus is 13 hand-recorded
  `.path` files / 271 points tied to specific quests and 5 spawners (§1.5).
  There is no continent-scale network to build from.
- **Build cost:** low mechanically (loader + graph exists), high editorially —
  someone must hand-record thousands of points.
- **Scaling cost:** excellent once authored (immutable shared data, cf.
  transfers).
- **Failure modes:** coverage gaps force straight-line fallbacks exactly where
  terrain is hardest; stale recordings silently break on world edits.
- **Verdict:** rejected as backbone. Keep as an *authoring tool* (the
  Simulation recorder) for last-mile connectors later.

### Option (b) — Coarse region-graph routing + local straight-leg steering with stuck recovery

- **Evidence strength:** strong for the *steering half* (MoveTo legs, ground
  clamp, stuck detection, slope gate all VERIFIED §1.1-§1.3); the *routing
  half* does not exist — a zone adjacency graph with traversability weights is
  unbuilt, and "traversable between zones" is exactly the knowledge the heightmap
  alone cannot give (ridgelines, mountain walls).
- **Build cost:** moderate — new router + traversability model; risk of
  rediscovering, at zone granularity, information the .bai node graph already
  encodes.
- **Scaling cost:** good if routes are cached per (origin-zone, dest) pair and
  shared.
- **Failure modes:** straight legs inside a zone still die on interior
  obstacles (Deadmine-class tunnels, PB-001's own example); water bodies need
  special-casing (soak drowning precedent).
- **Verdict:** useful as *degradation tier* and for dormant-phase coarse
  progress, not as the first slice's core.

### Option (c) — Client-navmesh (.bai) ingestion

- **Evidence strength:** strongest. Loaders, graph manager, A\*, GM
  visualization, and live NPC consumers all exist and run under the default
  config (§1.4). The data ships in the pak the server already mounts.
- **Build cost:** lowest of the three — the slice is composition + hardening,
  not construction. Known defects to fix en route: `GetBaiByPos` zone TODO
  (`WorldTemplate.cs:235-238`), linear-scan hotspots
  (`AiGeodataManager.cs:232-248, 270-314`), A\* G-cost bug
  (`PathNode.cs:226`).
- **Scaling cost:** controllable — the graph is immutable shared data; per-bot
  cost is FindPath calls. Policy: compute once per planned leg-set at scheduler
  wake, cache the reduced waypoint list, execute as ordinary MoveTo legs;
  dormant bots skip search entirely between wakes. Matches the measured
  envelope (tick p95 0.42 ms at 30 citizens vs 100 ms budget —
  playerbot-capability-matrix.md:58-63).
- **Failure modes:** navmesh holes → `FindPath` returns `[]` (empty-navmesh
  branch documented at `Npc.cs:1320-1322`); sparse designer graphs may route
  bots along odd corridors; zone-boundary selection bug could mis-pick chunks
  near borders; unmeasured pak coverage.

### Recommendation — FIRST VERTICAL SLICE

**(c) as spine, (b) as degradation tier:** a shared, immutable
navigation-graph service over the existing .bai loaders + hardened
`PathNode.FindPath`, exposed to bots as a *route planner* that converts one
cross-region goal into a cached list of waypoints executed through the
EXISTING contract surface (`IGameplayActor.MoveTo` legs via `BotPath` /
roam executor), with the current stuck detection unchanged as the per-leg
safety net and fall back to plain straight-leg + unstick nudge for any leg
whose plan fails. Sized to one independently testable scenario: **one
background-travel bot makes one cross-region leg** (e.g. Solzreed home → a
destination across a zone boundary ≥ ~2 km), consistent with the fidelity
ladder: the bot travels coarse while outside player proximity (dormant between
wakes, materializing near players per PopulationDirector radii), and every
executed meter rides the real CSMoveUnit/VehicleMovementModel path.

Explicitly out of scope for the slice: dungeon interiors (needs curated node
coverage verification), swimming route-planning (water treated as
blocker/no-go at planning time), vehicle-mounted travel, PvP-aware routing.

#### Slice PASS criteria

1. **Setup honesty:** E2E environment has real client data sources mounted
   (`ClientFileManager.Sources` non-empty) and `GeoDataMode=true`; the run
   aborts with a DATA-layer blocker record otherwise (mirrors PB-ledger
   discipline).
2. **Plan:** given a goal beyond one zone boundary, the planner produces a
   waypoint route of ≥ 2 intermediate waypoints whose total length is finite
   and whose endpoints snap to the graph (`FindСlosestToTheCurrent` returns
   non-null at both ends). Exactly **one** `FindPath` invocation occurs per
   planned route (asserted), and the reduced waypoint list is cached for the
   bot's wake series.
3. **Execution:** the bot reaches within `ArrivalRadius` (0.5 m) of the final
   waypoint chain terminus without teleport and without any direct Transform
   assignment — every applied position flows through
   `VehicleMovementModel.ApplyUnitMove`/roam-executor clamp+broadcast
   (auditable via the actor's structured trace, `IGameplayActor.cs:32`).
4. **Safety:** zero silent wedges — every leg either completes, or terminates
   in a spec §17 reason; stuck declarations trigger the existing
   nudge→TimedOut(Navigation) sequence and are visible in the trace; a
   planner-empty route (`FindPath == []`) degrades to the straight-leg tier
   and is recorded as such, never fabricated as success.
5. **Observer visibility:** during materialized legs, nearby clients receive
   `SCOneUnitMovementPacket` broadcasts (region probe or human client).
6. **Scaling invariant:** with the bot dormant between wakes, no navigation
   work runs per dormant second (wake-driven only); per-wake navigation cost
   stays inside the scheduler's action timeout budget
   (playerbot-capability-matrix.md:54-57).
7. **Regression guard:** existing rigs that default `ReturnQuestId 0` /
   short-leg scenarios remain green — the planner is additive behind the
   actor surface, no behavior change when no route is requested.

If criterion 2 fails systematically in the target corridor due to .bai holes,
that is a decisive, evidence-grade finding: it downgrades option (c)'s data
pillar and promotes (b) with hand-authored connectors — the experiment is
worth running for that measurement alone.

---

## 5. OPEN QUESTIONS FOR THE OWNER

1. **Pak coverage measurement** (blocking for (c)): on a machine with the 1.2
   game_pak, enumerate `game/worlds/main_world/**/netmission*.bai` and measure
   node density + link continuity along the Solzreed↔neighboring-zone
   corridors. Acceptance for (c) stands or falls here.
2. **A\* G-cost bug** (`PathNode.cs:226`): fix locally, or check upstream
   AAEmu for a correction to fold in during the next sync window?
3. **`GetBaiByPos` zone selection TODO** (`WorldTemplate.cs:235-238`): is
   per-position correct zone resolution required for border zones in the
   slice corridor, or is first-zone acceptable there?
4. **Performance envelope:** the codebase already warns when `FindPath` is slow
   (`BaseCombatBehavior.cs:154-159`). Do we want a hard per-plan time budget
   with fail-closed to straight-leg, and what budget number matches the
   scheduler's timeout philosophy?
5. **Water policy:** should the planner treat `WaterBodies` as hard blockers
   (walk around lakes/rivers) or allow swim legs? The soak drowning incident
   (STATUS.md:291) suggests blockers-first for background bots.
6. **Transfers as transport:** with boarding fixed (PB-F4), should long
   inter-city legs prefer riding real Transfer rails (cheaper + more believable)
   with walking only between rail access points? That would make §2.1's 444
   transfer segments part of the bot travel fabric.
7. **Ownership:** which session owns the future `NavigationService` engine
   code, and does this dossier's slice definition get promoted into ROADMAP as
   the M-next navigation card?
8. **Canonical fidelity check:** the .bai graphs are designer AI nets, not
   player-path data. Is bot travel allowed to look slightly "NPC-ish"
   (corridor-snapped), or does the fidelity ladder require road-hugging that
   would push toward transfer-rail riding + hand-authored town connectors?

---

## Appendix — key file index (all opened this session)

| Topic | File |
|---|---|
| Wire packet | `AAEmu.Game/Core/Packets/C2G/CSMoveUnitPacket.cs`; opcode `CSOffsets.cs:133` |
| Shared movement model | `AAEmu.Game/Models/Game/Units/Movements/VehicleMovementModel.cs` |
| Actor movement + stuck | `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs` |
| Roam executor / routes | `Core/Managers/Bots/BotRoamStepExecutor.cs`, `BotPath.cs`, `BotPresenceCoordinator.cs`, `BotScheduleBehavior.cs` |
| Regions/zones/heights | `Core/Managers/World/WorldManager.cs`; `Models/Game/World/{WorldTemplate,WorldCell}.cs` |
| .bai navmesh loaders | `Models/CryEngine/Loaders/BaseBaiLoader.cs` |
| Graph manager + A\* | `Core/Managers/AiGeodataManager.cs`; `Models/Game/AI/AStar/PathNode.cs`; `AI/AStar/AiNavigation.cs` |
| NPC consumers | `Models/Game/NPChar/Npc.cs`; `AI/v2/Behaviors/BaseCombatBehavior.cs`; `Scripts/Commands/TestNavMesh.cs` |
| NPC route engine | `Models/Game/Units/Route/Simulation.cs`; `Core/Managers/AiPathsManager.cs`; `AI/v2/Controls/AiPathHandler.cs` |
| Transfers | `GameData/TransferGameData.cs`; `Core/Managers/TransferManager.cs`; `Models/Game/Units/Transfer.cs`; `Transfers/TransferSpawner.cs` |
| Ships | `Physics/ShipController.cs`; `Core/Managers/World/PhysicsManager.cs`; `ShipStaticBarrierBaiIngestor.cs` |
| IO/config | `IO/ClientFileManager.cs`; `Configurations/ClientData.json`; `Configurations/World.json` |
| Tooling | `Tools/WorldConverter/README.MD` |
| Ledger context | `playerbot-blockers.md` (PB-001); `playerbot-capability-matrix.md` (Movement row, Scaling posture); `STATUS.md` (M7 notes); `AGENTS.md` |

---

## Addendum — 2026-08-25 pak coverage + corridor measurement (nav-coverage-probe session)

Answers §5 open question 1. Method: (1) direct `AAPak` enumeration of the deployed 1.2
runtime pak (`/root/aaemu-e2e/runtime/game-data/ClientData/game_pak`, 16 GB);
(2) a standalone offline probe (`/root/aaemu-nav-paktool/navprobe`, uncommitted rig)
that re-implements the `NetMissionReader`/`AreasMissionReader` binary parse and the
`PathNode.FindPath` traversal semantics — **byte-exact cross-validated against the
engine's own parsers** (same file/node/link totals, see T2) via a scratch project
referencing `AAEmu.Game` (`.worktrees/nav-probe/Tools/NavProbeScratch`, uncommitted);
(3) live isolated-stack boot (`E2E_ROOT=/root/aaemu-e2e-nav`, compose project
`navacc`, GeoDataMode=true) with READ-ONLY `[navprobe]` log telemetry added to
`WorldTemplate.LoadZoneBaiFiles` and `WorldCell.LoadBaiFiles` (uncommitted in
`.worktrees/nav-probe`; passive, inert unless logged). No engine behavior changed.

### T1 — pak `.bai` inventory (VERIFIED)

| Family | Files | main_world? |
|---|---|---|
| netmission*.bai | 9,447 | yes — all under `paths/<X>_<Y>/` (256 m blocks) |
| areasmission*.bai | 9,447 | yes, same blocks |
| vertsmission*.bai | 9,447 | yes, same blocks |
| hidemission*.bai | 7,399 | yes |
| fnavmission / roadnavmission / v3dmission / waypt3dsfcmission | 22 each | **NO — dungeon worlds only** |

Total pak entries 218,068; total .bai 35,828. The §2.2 UNKNOWN "additional unused nav
formats" is resolved: they exist but never for main_world.

### T2 — main_world census (VERIFIED; engine-parser-identical)

| Metric | Value |
|---|---|
| path-blocks with netmission0.bai | 7,937 (all >20 B → all eligible for engine load) |
| parse failures | 0 (standalone rig AND engine `NetMissionReader`: identical) |
| total nodes / links | 3,561,122 / 12,035,252 (both parsers agree exactly) |
| zero-node files | 0 |

### T3 — structural finding: zone loaders are NOT how main_world loads (VERIFIED live)

Boot telemetry: `[navprobe] main_world: ZoneKeys=122, zones with zone-folder .bai=0,
ZoneBaiLoaders=0 — path-block lazy loading active: True`. All main_world graph data is
per-path-block; `WorldTemplate.LoadZoneBaiFiles()` registers nothing for it and the
§1.4 defect #1 (`GetBaiByPos` first-zone TODO) is **unreachable for main_world**
(`ZoneBaiLoader.Count == 0` → per-position `PathBaiLoader` branch runs). Live cell
loads during one presence-demo run confirmed the lazy path (16 readers = 4×4 blocks
per cell), e.g. Marianople cell (10,11): 45,674 nodes / 160,684 links; Solzreed coast
cells (14,14)/(15,14)/(15,15): 15,616 / 9,807 / 1,064 nodes.

### T4 — corridor probe Solzreed shore ↔ Marianople (VERIFIED measurement)

Corridor rectangle blocks[41..61]×[45..61] (357 blocks, ~5.2 km direct between anchors
from `Data/Portal/respawns.json`): **357/357 blocks carry a navgraph**; 465,485 nodes,
1,512,526 links, 22,219 forbidden-area polygons. Probe matrix: 9 start points around
the Solzreed shore anchor × 9 goals around Marianople (±192 m grid).

| Probe | Result |
|---|---|
| A* (engine semantics incl. G-cost bug, loop cap, DP-reduce) | **81/81 GoalReached**, 0 NoGraphPath, 0 LoopExhausted |
| BFS reachability (same filtered graph) | 81/81 reachable; every start snap reaches the SAME component of **453,616 nodes** (97.5 % of corridor nodes; only 680 isolated nodes total) |
| Path length (post-DP) | avg 9,607 m vs 5,256 m direct (**detour ratio 1.83×**), range 8,202–11,352 m |
| Raw waypoint chain | ~600–950 hops pre-reduction |
| Plan time (rig) | ~80–175 ms per cross-region plan; engine's linear openSet scan makes real-engine cost superlinear in frontier size |

### Data-quality caveat (VERIFIED anomaly, impact bounded)

369 of 2,872 checked main_world `areasmission0.bai` files declare absurd point counts
in later sections (e.g. 4.19 B points vs <40 KB remaining). Engine parsing is defensive
(per-file try/catch keeps partial data, `BaseBaiLoader.cs:75-78`), so this degrades
forbidden-area coverage locally but does not crash. One affected block sits near the
corridor (059_052); probes still passed 81/81. Root cause in canonical data: UNKNOWN.

### VERDICT — criterion 2 of the slice: **GO**

Canonical `.bai` coverage along a real cross-region corridor is complete, connected,
and dense enough for the waypoint-spine slice (option (c)). Measured fix order for the
three known defects:

1. **`PathNode.cs:226` G-cost bug** — first; pure-local change, directly distorts plan
   quality and search size at corridor scale (measured here).
2. **Linear-scan hotspots** (`AiGeodataManager.FindСlosestToTheCurrent` /
   `GetHeight`, nearest-node-in-reader) — second; per-plan costs are already
   ~100 ms+ at 5 km scale on a faithful replica, and every expansion re-scans.
3. **`GetBaiByPos` zone TODO** — demoted: measured moot for main_world (T3); revisit
   only if dungeon-zone graphs ever join bot routing.

Artifacts (uncommitted, session-scoped): `/root/aaemu-nav-paktool/`
(pak enumerator + navprobe rig + outputs `pak-bai-listing.txt`,
`corridor-probe.txt`); `.worktrees/nav-probe/Tools/NavProbeScratch/` (engine
cross-check); `[navprobe]` diagnostics in `WorldTemplate.cs`/`WorldCell.cs`
(uncommitted in `.worktrees/nav-probe`). compact.sqlite3 untouched; no pushes.

---

## Addendum — 2026-08-25 nav slice executed: G-cost fix + spatial index measured (nav-slice session)

Executes the GO verdict's measured defect-fix order (1 and 2; defect 3 stays demoted per T3).
All engine edits, builds and tests in isolated worktree `.worktrees/nav-slice`, branch
`nav/slice-gcost` (base `41ddb889a`); main tree touched only for this file. compact.sqlite3
read-only (a copy was placed into the worktree for DB-backed unit tests); no pushes.

### Changes

1. **`PathNode.cs:226` G-cost bug fixed** (`1b8bf260e`): G now accumulates walked cost
   (parent G + link length) instead of storing distance-to-goal. The loop cap
   (`maxLoopsLeft`) and Douglas–Peucker post-pass contracts are unchanged.
2. **Binary-heap openSet** (same commit): `PriorityQueue` on F with insertion-sequence
   tie-break reproduces the old stable `OrderBy(F)` selection; hash-set closed/open
   membership keyed on exact position equality; stale heap entries skipped without
   burning loop budget. Diagnostics: `ExpandedNodesLastSearch`.
3. **Per-block spatial grid** (`0d6736282`): new `BaiPointGrid<T>` (64 m world-space
   buckets over one 256 m path-block, ring-spiral exact-minimum queries) built lazily on
   first query per loaded block — never eager over the 3.56 M-node world. Wired into
   `BaseBaiLoader.FindClosestNetMissionNode`/`FindClosestVertexPoint`,
   `AiGeoDataManager.FindСlosestToTheCurrent` and `GetHeight` (netmission-first tie order
   preserved). Grids reset on additive reload (`ClearData`).

### T5 — before/after on the real engine chain (VERIFIED measurement)

Rig: `Tools/NavGCostProbe` drives the REAL chain (ClientFileManager → BaseBaiLoader →
NetMissionReader/AreasMissionReader → AiGeoDataManager → PathNode.FindPath) headlessly
over the T4 corridor rectangle blocks[41..61]×[45..61] (357 blocks, 465,485 nodes,
1,512,526 links — engine-parser totals identical to T2). Matrix = T4's 9×9 Solzreed-shore ↔
Marianople grid. BASELINE ran on unmodified `develop` code at the same commit; both runs
identical matrix order.

| Metric | Baseline (buggy G, linear scans) | After (G-fix + heap + grid) |
|---|---|---|
| Success | **81/81 GoalReached** | **81/81 GoalReached** (0 NoGraphPath, 0 LoopExhausted) |
| Detour ratio (path/direct) | 1.91× (avg 10,014 m vs 5,253 m) | **1.22×** (avg 6,419 m) |
| Path length range | 7,961–13,659 m | 5,838–7,020 m — **all 81 paths shorter than baseline's best** |
| Total plan time (81 plans) | 563.3 s | **96.1 s** |
| Avg / max plan time | 6,954 ms / 23,634 ms | **1,187 ms / 1,709 ms** (~5.9× faster avg) |
| Node expansions avg / max | 18,360 / 55,866 | 52,611 / 57,911 |

Honest notes: expansions rose ~2.9× because true A* explores more of the graph than the
greedy-degraded search did; each expansion is far cheaper (heap + grid), so wall-clock plan
time still dropped ~5.9×. Plan quality improved outright: every post-fix path is shorter
than every baseline path, and the detour ratio (1.22×) beats the T4 rig's buggy-G 1.83× by
a wide margin. Residual detour reflects the designer-authored sparse node graph plus DP
tolerance, not search error. Remaining max plan time (~1.7 s) is dominated by expansion
volume near the loop cap on the longest legs; a per-plan time budget with straight-leg
fail-closed (§5 open question 4) is the natural next lever.

### Tests (VERIFIED green)

New rig `AAEmu.UnitTests/Game/Navigation/BaiNavigationRigTests.cs` (TUnit, loads real
.bai through the real NetMissionReader read-only from game_pak; self-skips without data):

- `RealNetMissionBlocks_ParseWithLinkContinuity` — parse + zero dangling links
- `SpatialIndex_MatchesLinearScan_OnRealNodes` — exact-minimum agreement, 64 samples/block
- `SpatialIndex_VertexGrid_MatchesLinearScan` — obstacle-vertex grid vs linear scan
- `FindPath_GCostAccumulatesAlongWalkedPath_PrefersCheapRoute` — synthetic weighted graph;
  regression guard that A* takes the cheap bent chain over the greedy hub decoy
- `FindPath_IsDeterministic_OnRealData` — identical input → identical waypoint list
- `FindPath_ReachesGoals_OnlyWhenBfsConnectsThem` — BFS reachability agreement

Full `AAEmu.UnitTests` run in the worktree: **2385 total, 2384 passed, 1 skipped (env-
conditional), 0 failed**. NPC combat consumers call the same `Npc.FindPath` →
`PathNode.FindPath` surface; changes are strictly-better-or-equal there (same or better
paths in less time, success rate unchanged).

Artifacts: `/tmp/navprobe-baseline.txt`, `/tmp/navprobe-after.txt` (raw matrix dumps);
committed rig + probe under `.worktrees/nav-slice`. Branch tip at addendum time:
`7e5d96e74`.
