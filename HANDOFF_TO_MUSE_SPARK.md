# Engineering Handoff to Muse Spark

**Date**: 2026-09-06  
**Repository**: `/root/aaemu-dev` (`joshhmann/AAEmu`)  
**Active Branch**: `develop`  
**Latest Deployed Commit**: `8a4721775`  
**Production Host**: `192.168.0.165` (Container: `aaemu-game-1` on image `aaemu-game:presence-demo`)  

---

## 1. Executive Summary & Session Overview

During this session, we completed three feature tracks (Party Bots, Grounding Phase 2, Ships & Cargo Interaction), resolved the critical NPC Z-axis stutter/dust/jiggle and submersion defect, and verified all changes against the full test suite and archaeology gate before deploying live to the server.

All gates are **100% green**, the codebase knowledge graph is synchronized, and the live container on `192.168.0.165` is running with all ports active (`1237`, `1239`, `1250`) and registered with the login daemon.

---

## 2. Changes Implemented & Verified

### A. NPC Z-Stutter, Landing Dust, & Ground Submersion Fixes (`8a4721775`)
- **Root Cause 1 (Stutter, Landing Dust, & Breast/Hair Jiggle Loop)**:
  - In [`SCUnitStatePacket.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Packets/G2C/SCUnitStatePacket.cs), packet serialization was calling `_unit.Transform.Local.SetHeight(worldMgr.GetReferenceHeight(...))` every time the packet was sent.
  - In [`WorldManager.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Managers/World/WorldManager.cs), `GetReferenceHeight` had a legacy hardcoded branch for `IdleBehavior` and `HoldPositionBehavior` that returned the raw, unvalidated `ai.Owner.Spawner.Position.Z`.
  - When raw spawner coordinates were in the air (e.g. +1.5m), the packet snapped the NPC into mid-air, causing CryEngine client physics to drop the NPC to the ground. This triggered the landing impact event (dust particles + spring physics jiggle on hair/cloth/breasts). As soon as the NPC ticked or another packet arrived, it was snapped back up and dropped again in an infinite loop.
  - **Fix**: Removed the mutating `SetHeight` call from `SCUnitStatePacket` (serialization is now strictly read-only). Updated `GetReferenceHeight` in `WorldManager.cs` to prioritize deck volume hints (`TryGetDeckFloor`) and terrain/navmesh height over raw spawner Z.
- **Root Cause 2 (Bend-Knee & Half-Body Submersion Clipping)**:
  - In [`NpcGroundingPolicy.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/NPChar/NpcGroundingPolicy.cs), `NegativeClampSeverityM` was `-2.0f`. Any outdoor NPC whose database spawner Z was sunken by `-0.3m`, `-0.5m` (knee height), or `-1.0m` (half body) was treated as sub-threshold and left buried in the terrain.
  - **Fix**: Tightened `NegativeClampSeverityM` to `-0.1f` (clamping any outdoor unit submerged more than 10cm, while preserving subterranean/cave dwellers via `IsCaveOrInterior`), and tightened `ClampSeverityM` to `0.5f`. Updated regression assertions in [`NpcGroundingPolicyTests.cs`](file:///root/aaemu-dev/AAEmu.UnitTests/Game/NPChar/NpcGroundingPolicyTests.cs).

### B. Option 1: Party Bots (PB-002) (`ee1e17b2b`)
- **Auto-Accept**: In [`BotRoamStepExecutor.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs), pending party invitations are auto-accepted (`actor.PartyAccept()`).
- **Formation Follow**: When a bot is a party member, it tracks the leader. If distance exceeds the formation radius ($3.5\text{m}$), the bot moves toward the leader.
- **Combat Assist**: If the party leader targets an enemy unit within $25\text{m}$, the bot acquires the leader's target and casts offensive abilities.
- **Unit Test**: Added `Step_InPartyAsMember_FollowsLeaderAndAssistsTarget` in [`BotRoamStepExecutorTests.cs`](file:///root/aaemu-dev/AAEmu.UnitTests/Game/Core/Managers/Bots/BotRoamStepExecutorTests.cs).

### C. Option 2: Grounding Phase 2 (PB-005) (`ee1e17b2b`)
- **Deck Volume Spawn Z Integration**: In [`NpcSpawnerNpc.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs), `NpcGroundingPolicy.TryGetDeckFloor` is evaluated before terrain heightmap lookup.
- **Wardton Dock Volume**: Added `wardton_dock_pier` AABB (`minX: 15150.0, maxX: 15300.0, minY: 13600.0, maxY: 13700.0, minZ: 100.0, maxZ: 125.0, floorZ: 107.61`) in [`deck_volumes.json`](file:///root/aaemu-dev/AAEmu.Game/Data/Navigation/deck_volumes.json).
- **Unit Tests**: [`NpcGroundingPhase1Tests.cs`](file:///root/aaemu-dev/AAEmu.UnitTests/Game/NPChar/NpcGroundingPhase1Tests.cs) passes 7/7.

### D. Option 3: Ships & Cargo Transport (PB-005/006) (`ee1e17b2b`)
- **Cargo Pack Loading via Interaction**: In [`Use.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/World/Interactions/Use.cs), added interaction paths on vehicle storage box doodads (`PackVehicleService.IsPackStorageBoxDoodad`) and direct vehicle units (`Slave`), calling `PackVehicleService.TryLoadCarriedPack(player, slave, out _)`.
- **Passenger Seat Synchronization**: In [`VehicleSeat.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/DoodadObj/VehicleSeat.cs), synchronized passenger unbind on slave vehicles by removing the passenger from `slave.AttachedCharacters` upon unload.
- **Unit Test**: Added `VehicleSeat_UnLoadPassenger_RemovesFromSlaveAttachedCharacters` in [`SlaveLifecycleTests.cs`](file:///root/aaemu-dev/AAEmu.UnitTests/Game/Core/Managers/SlaveLifecycleTests.cs).

### E. Core 5.0 Fixes & Rowboat Despawn Return (`ff21da699`)
- Rowboat de-summon returns summoning item back to player inventory.
- Physics and collision patches from 5.0 backported and validated.

---

## 3. Verification & Gate Evidence

| Check | Command | Result |
|---|---|---|
| **Tier 1 Gate** | `./scripts/gate.sh` | **PASS** — Build OK, ScriptCompiler 0 errors, **2882 passed, 0 failed, 1 skipped**, BotControl MCP smoke 39 tools OK, Archaeology gate smoke 24 tools/679 tables OK |
| **Archaeology Cycle** | `./scripts/archaeology-cycle.sh` | **PASS** — **156 passed, 0 failed**, stdio smoke 24 tools/679 tables OK |
| **Knowledge Graph** | `graphify update .` | **PASS** — 28,699 nodes, 71,201 edges, 1,115 communities synchronized |
| **Git Push** | `git push origin develop` | **PASS** — Pushed to `joshhmann/AAEmu` (`8a4721775`) (boundary maintained: upstream push disabled) |
| **Live Host** | `192.168.0.165` | **HEALTHY** — Container `aaemu-game-1` running image `aaemu-game:presence-demo`, registered with login server, ports 1237, 1239, 1250 active |

---

## 4. Key Code Locations for Muse

- **NPC Grounding & Decks**:
  - Grounding policy & clamp thresholds: [`AAEmu.Game/Models/Game/NPChar/NpcGroundingPolicy.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/NPChar/NpcGroundingPolicy.cs)
  - Deck volumes AABB definitions: [`AAEmu.Game/Data/Navigation/deck_volumes.json`](file:///root/aaemu-dev/AAEmu.Game/Data/Navigation/deck_volumes.json)
  - World reference height calculation: [`AAEmu.Game/Core/Managers/World/WorldManager.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Managers/World/WorldManager.cs)
  - Spawner placement logic: [`AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs)
- **Bot Party & Follow**:
  - Party coordinator & step execution: [`AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs)
  - Bot actions & combat casting: [`AAEmu.Game/Core/Managers/Bots/GameplayActor.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs)
- **Vehicles & Cargo**:
  - World interaction: [`AAEmu.Game/Models/Game/World/Interactions/Use.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/World/Interactions/Use.cs)
  - Pack vehicle logic: [`AAEmu.Game/Core/Managers/World/PackVehicleService.cs`](file:///root/aaemu-dev/AAEmu.Game/Core/Managers/World/PackVehicleService.cs)
  - Vehicle passenger seats: [`AAEmu.Game/Models/Game/DoodadObj/VehicleSeat.cs`](file:///root/aaemu-dev/AAEmu.Game/Models/Game/DoodadObj/VehicleSeat.cs)

---

## 5. Recommended Next Workstreams for Muse

1. **Additional Deck Volumes**:
   - Add AABB deck volumes to [`deck_volumes.json`](file:///root/aaemu-dev/AAEmu.Game/Data/Navigation/deck_volumes.json) for other major docks and elevated structures (e.g. Marianople city platforms, Ezna harbor piers, Sanddeep docks, Two Crowns wharves) where level geometry sits significantly above water or terrain heightmaps.
2. **Bot Party Formation Roles & Healer Logic**:
   - Expand `BotRoamStepExecutor` party behavior to assign formation offsets (e.g., tank in front, healer behind) and add reactive healing/buffing when party members drop below health thresholds.
3. **Vehicle Cargo Unload & Turn-In**:
   - Implement trade pack unloading interaction from vehicle storage boxes directly to specialty trade merchants or back into the character's backpack slot.
4. **Maintenance Verification**:
   - Verify `git status` stays clean and always run `./scripts/gate.sh` and `./scripts/archaeology-cycle.sh` before pushing.
