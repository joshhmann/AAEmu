# Comprehensive Archaeology Report: Upstream Branches & 5.0 Client Deltas

**Target System:** AAEmu 1.2 (`r208022`)  
**Repository:** `joshhmann/AAEmu` (`develop` branch)  
**Evaluated Upstream Sources:** `https://github.com/AAEmu/AAEmu.git`  
- `client_version/5.0_client_(2018-12-25)` (commit `4da5794af1c8`)
- `client_version/3.5_client_(2025_01_11)` (commit `78fddc62a9d9`)
- `feature/boat_physics` (commit `07d29f84a537`)
- `feature/ucc` (commit `da29ac4b5280`)
- `client_version/1.2.5_client_(2015-03-18)` & `1.2_client_(r204073)`
- `client_version/3.0_client_(2017_04_20)`
- `client_version/10.8.1.0_client-Kakao-r651713-(2024-05-15)-Final-EU-NA`
**Date:** 2026-09-08  

---

## Executive Summary & The Core Architectural Insight

A deep archaeological inspection of the upstream repository branches yields a surprising and decisive conclusion:

> **Upstream branches (especially 5.0 and 3.5) help our roadmap most in the exact places we haven't deeply solved yet**—specifically deep gameplay domain state machines such as vehicle/ship attachment hierarchies, driver/passenger seat topologies, reactive quest event matrices, spatial quest spheres, auction sales trends, and resident community centers.
>
> **Conversely, these upstream branches do not replace or obsolete any of our fork's primary innovations**: our autonomous PlayerBots, PopulationDirector, Aspire local orchestration, Archaeology MCP tools, .NET 10 upgrade, headless simulated gameplay harnesses, and automated Tier 1–3 gating pipelines.

By treating upstream branches as an **archaeological blueprint for gameplay domain models and physics curves**, we can leapfrog months of manual packet sniffing and reverse-engineering, surgically porting mechanics into our modern, test-gated .NET 10 architecture.

---

## Part I: 5.0 Client Delta & Archaeology Tickets

Comparing `develop` against `upstream/client_version/5.0_client_(2018-12-25)` surfaces 4,863 additions across core managers. Below are the four structured tickets ready for backlog tracking.

---

### Ticket A: Vehicle / Slave 5.0 Delta

* **Target Subsystem:** `AAEmu.Game/Core/Managers/SlaveManager.cs`, `AAEmu.Game/Models/Game/Slaves/`, `AAEmu.Game/Models/Game/Units/Slave.cs`
* **Current 1.2 State:** Basic vehicle instantiation with skeletal socketing; limited passenger handling.
* **5.0 Archaeological Assets:**

#### 1. Attachment Topology (`_attachPoints`, `_slaveBindings`, `_slaveDoodadBindings`)
* **Sub-Vehicles on Vehicles (`SpawnSlaveSlaves`):** Supports mounting secondary vehicles (such as rowboats or gliders) onto parent ships (galleons/merchants).
* **Doodad Attachments (`CreateSlaveDoodads`):** Binds functional doodads (cannons, steering wheels, harpoons, radar, sails, cargo crates) to vehicle bone/slot sockets using `AttachPointKind` and `SlaveEquipSlots`.
* **Dynamic Query API:** `GetSlaveAttachPointsByOwnerId`, `GetDoodadAttachPointsByOwnerId`, `GetHealingAttachPointsByOwnerId`, `GetAttachPointBySlotId`.

#### 2. Seat & Driver Binding State (`BindSlave` / `UnbindSlave`)
* Differentiates between the **pilot/driver** (who holds movement authority) and **passengers** seated on benches or operating doodads.
* Tracks binding reason and state machine transitions via `AttachUnitReason` and `VehicleSeat.cs`.
* **Emergency Ejection (`RidersEscape`):** Auto-ejects riders when a vehicle capsizes, despawns, or falls into non-navigable geometry.

#### 3. Vehicle Packets & Lifecycle
* **C2G Inbound:** `CSBindSlavePacket`, `CSDespawnSlavePacket`, `CSRepairSlaveItemsPacket`, `CSRemoveAllFieldSlavesPacket`.
* **G2C Outbound:** `SCSlaveBoundPacket`, `SCEscapeSlavePacket`, `SCSlaveStatusPacket`, `SCUpdateSlaveSourceItemPacket`, `SCSlaveEquipmentChangedPacket`.
* **Lifecycle & Despawn:** `UpdateSlaveRepairPoints` (damage/repair items), `RemoveAndDespawnAllActiveOwnedSlaves` (logout cleanup).

---

### Ticket B: Quest + Doodad Progression Delta

* **Target Subsystem:** `QuestManager.cs`, `QuestManagerEvents.cs`, `DoodadManager.cs`, `SphereQuestManager.cs`
* **Current 1.2 State:** Monolithic quest checks; linear objective polling.
* **5.0 Archaeological Assets:**

#### 1. Event-Driven Quest Dispatcher (`QuestManagerEvents.cs`)
Replaces poll loops with reactive event subscriptions:
* `DoOnMonsterHuntEvents`: Tracks individual NPC kills, NPC groups (`_groupNpcs`), and zone-wide monster kill counts.
* `DoOnTalkEvents`: NPC dialogue and NPC group interactions.
* `DoOnAggroEvents`: Objectives triggering upon entering combat with target mobs.
* `DoOnExpressFireEvents`: Objectives requiring specific player emotions/emotes directed at NPCs.
* `DoOnLevelUpEvents` / `OnAbilityLevelUp`: Auto-starts level-bracket quests via `QuestActConAcceptLevelUp`.
* `DoOnCraftEvents`: Crafting recipe completion quest objectives.

#### 2. Spatial Sphere Quests (`SphereQuestManager.cs` / `ISphereQuestManager.cs`)
* Triggers `DoOnEnterSphereEvents`, `DoOnExitSphereEvents`, and `DoOnEnterQuestStarterSphere` when crossing 3D sphere boundaries in the world.
* Drives location discovery quests, ambush spawns, and area-restricted objective timers (`OnTimerExpired`).

#### 3. Doodad Interaction & Phase Transitions (`DoodadManager.cs`)
* **Phase Progression:** Manages multi-stage growth, harvesting, opening/closing, and construction via `DoodadFunc` and `DoodadPhaseFunc`.
* **Material Consumption:** `DoodadFuncConsumeChangerItemList` enforces required consumable items (keys, water, fertilizer, tools) before triggering phase shifts.

---

### Ticket C: NPC & Navigation Delta

* **Target Subsystem:** `AiPathsManager.cs`, `AiGeodataManager.cs`, `NpcManager.cs`, `SpawnManager.cs`, `SubZoneManager.cs`
* **Current 1.2 State:** Relying on `.bai` static files; occasional terrain-drop or ceiling-float anomalies.
* **5.0 Archaeological Assets:**

#### 1. Spatial Constraints & Modifiers (`AiGeodataManager.cs`)
* `_forbiddenArea`: Polygon boundary definitions preventing NPCs from wandering into out-of-bounds terrain or aquatic voids.
* `_aiNavigationModifier`: Cost multipliers for varying terrain types (roads vs swamps vs steep incline).

#### 2. SubZone Polygon Containment (`SubZoneManager.cs`)
* Planar polygon evaluation (`IsPointInPolygon`) using XML zone definitions to accurately switch localized climate, weather, and territorial faction rules.

#### 3. Terrain & Elevation Stability (Cross-Referenced with `npc-cave-float-clamp`)
* **2D Combat Return Leash (`BaseCombatBehavior.cs`):** Leashes using `Vector2.Distance` (ignoring Z), preventing subterranean NPCs from evading instantly because their idle home Z resolved to the mountain surface above.
* **Ground-Rise Clamp:** Upward Z-jump clamping (`MaxGroundRise: 8.0m`) to prevent NPCs from snapping to cave ceilings during movement.

---

### Ticket D: Economy Delta

* **Target Subsystem:** `AuctionManager.cs`, `SpecialtyManager.cs`, `PublicFarmManager.cs`, `ResidentManager.cs`
* **Current 1.2 State:** Basic auction listings; trade packs and public farms partially implemented.
* **5.0 Archaeological Assets:**

#### 1. Market Price & Volume Telemetry (`AuctionManager.cs`)
* Introduces `SalesData` and `SoldsData` concurrent dictionaries indexed by `(TemplateId, Grade)`.
* Caches historical sale prices and dates, providing market pricing trends, median valuation, and trading intelligence.

#### 2. Dynamic Trade Pack Supply & Demand (`SpecialtyManager.cs`)
* Payout calculations fluctuate dynamically based on cargo turn-in saturation across trade routes.

#### 3. Public Farm Automated Lifecycles (`PublicFarmManager.cs`)
* `PublicFarmTickStartTask` monitors crop defense duration (`CommonFarmGameData.Instance.GetDoodadGuardTime`).
* Upon expiration, automatically transitions crops to `DoodadOwnerType.System` with `FarmType.Invalid`, opening them to public harvesting.

#### 4. Community Centers / Resident System (`ResidentManager.cs` — Greenfield)
* Manages continental zones (`MaxCountResident = 29`), tracking `ZoneGroupId`, `DevelopmentStage`, resident tokens, and daily login contributions.

---

### Downstream Stability Audit & Migration Protocol

Before porting full domain models, port the surgical correctness fixes:
1. **`npc-pos-skill-aim`:** Fix ground/target-aimed skills targeting the caster instead of the destination coordinate (`Behavior.cs:167-175`).
2. **`corpse-casting-skill-controller`:** Guard against dead entities initiating skill casts (`Unit.cs:920-934`).
3. **`quest-offer-freeze-on-reject`:** Ensure client receives rejection packets (`CharacterQuests.cs:94`) rather than hanging UI dialogues.
4. **Migration Discipline:**
   - **Audit:** Verify bug presence in 1.2 via Archaeology MCP or targeted tests.
   - **Adapt:** Conform to .NET 10 constructor dependency injection (Rule 6); adjust wire opcodes to 1.2 specifications.
   - **Stress-Test:** Execute under `PopulationDirector` with simulated PlayerBots to guarantee zero tick latency or deadlock regression.

---

## Part II: Upstream Version Tree Map & High-Value Branches

Beyond 5.0, the upstream repository contains several mature feature and client branches:

```
AAEmu/AAEmu
├── client_version/1.2_client_(r204073)       --> Player trading & Item regrading
├── client_version/1.2.5_client_(2015-03-18)  --> Encryption double-call & SCMessageCount fix
├── client_version/3.0_client_(2017_04_20)    --> Guild Dominion & Lodestone Siege warfare
├── client_version/3.5_client_(2025_01_11)    --> Modernized auth, character creation, battle pets
├── client_version/5.0_client_(2018-12-25)    --> Vehicles, Quest matrices, Market history
├── client_version/10.8.1.0_client-Kakao...   --> Final retail EU/NA packet opcodes & schemas
├── feature/boat_physics                      --> Realistic naval momentum & steering loop
└── feature/ucc                               --> Custom crest/emblem uploads & CC skill interrupts
```

### 1. `client_version/3.5_client_(2025_01_11)` (Commit `78fddc62`)
*Maintained by NL0bP through mid-2026. The most cleanly refactored branch in upstream.*
* **Robust Login & Auth:** `KoreaAuthFlowFactory`, modernized `LoginSession` state machine, bitmask overlap fix in `AfsValue.FromULong`.
* **Character Creation Hardening:** Full validation of race, gender, and ability templates in `CharacterManager.Create` with null-safe equipment provisioning (stops client disconnect crashes on invalid creation packets).
* **Container Refactor:** Replaces internal fields with public `HoldingContainer` property across managers and packets.
* **Combat Pet Spells:** Introduces `BattlePetSpell` action slots and companion spell controllers.
* **Heir Abilities:** Presets and secondary resource models (`AbilitySetInfo`, `HighAbilityRsc`).

### 2. `feature/boat_physics` (Commit `07d29f84`)
*Created by gene.aagenesis. Directly integrates with Ticket A.*
* **Dedicated Naval Thread (`BoatPhysicsManager.cs`):** Autonomous 50ms simulation loop covering all active ships (`BigSailingShip`, `SmallSailingShip`, `Fishboat`, `MerchantShip`, `Speedboat`, `Boat`).
* **Physics & Inertia Dynamics:**
  ```csharp
  // Realistic acceleration, angular rotation damping, and water drag
  slave.Speed += (slave.Throttle * 0.007874f) * (velAccel / 20f);
  slave.RotSpeed += (slave.Steering * 0.007874f) * (velAccel / 20f);
  if (slave.Steering == 0) slave.RotSpeed -= (slave.RotSpeed / 10);
  if (slave.Throttle == 0) slave.Speed -= (slave.Speed / 10);
  ```

### 3. `feature/ucc` (Commit `da29ac4b`)
*Created by gene.aagenesis.*
* **Crest / Emblem Upload Pipeline:** Client-to-Stream (`:1250`) image transfer protocols for user-created crests (sails, cloaks, picture frames).
* **Crowd-Control Interruption:** Cancels channeling/casting skills immediately when an entity dies or suffers CC (stun, knockback, silence).

### 4. `client_version/1.2.5_client_(2015-03-18)` & `1.2_client_(r204073)`
*Closest protocol relatives to our 1.2 r208022 baseline.*
* **Network Bugfix:** Eliminates double-invocation of encrypted server packet bodies and synchronizes `SCMessageCount` sequence numbers.
* **Direct Player-to-Player Trading:** Full implementation of `TradeManager`, trade invitation, gold staging, and transactional commitment (`SCTradeMadePacket`).
* **Item Regrading (`ItemGradeEnchanting.cs`):** Chance multipliers, failure downgrades, and socketing formulas.

### 5. `client_version/10.8.1.0_client-Kakao-r651713-(2024-05-15)-Final-EU-NA`
*The final official live Western retail build before sunset.*
* **Definitive Opcode Encyclopedia:** Complete mapping of opcodes, packet encryption keys, and data schemas for modern ArcheAge networking.

---

---

## Part IV: Source-Code Audit: `develop` vs. Upstream & 5.0 Fixes

A concrete, symbol-by-symbol comparison against the current `develop` tree reveals that **some fixes have already been quietly incorporated**, while several **critical bugs and missing subsystems remain unpatched**:

### 1. Fixes ALREADY PRESENT in `develop` (No Action Needed)

| Feature / Fix | Location in `develop` | Status & Verification |
|---|---|---|
| **Quest Offer Freeze on Reject** | `CharacterQuests.cs:98,124` | **Present.** Both duplicate quest and failed component checks send `Owner.SendErrorMessage(ErrorMessageType.AlreadyRequested);` to dismiss the cutscene dialog. |
| **AfsValue Bitmask Collision** | `ACJoinResponsePacket.cs:28,35` | **Present.** Uses `(uint)(afs >> 32)` and `1UL << 8`, preventing the bit 16 flag overlap that broke 3.5 auth. |
| **PacketStream Roundup Overflow** | `PacketStream.cs:191-193` | **Present.** Overflow clamp guard is active. |
| **Quest Event System** | `QuestManagerEvents.cs` | **Present & Superior.** `develop` has full event dispatching AND implements `GetQuestIdsFromKillAcceptNpc` using proper world-instance references (`ParentWorld.GetNpc`) rather than static singletons. |
| **Spatial Quest Volumes** | `SphereQuestManager.cs` | **Present.** Sphere quest manager and triggers are implemented and scoped to `WorldInstance`. |
| **Player Trading & Regrade** | `TradeManager.cs`, `ItemGradeEnchanting.cs` | **Present.** Player trade windows and item grade enchanting support are active. |
| **Naval Ship Physics** | `AAEmu.Game/Physics/` (`ShipController.cs`) | **Architecturally Superior.** While upstream's `feature/boat_physics` relied on a naive Euler thread (`velAccel / 20f`), our fork has a full **Jitter2 rigid-body physics engine** with buoyancy, whirlpool pull, shore contact, and harpoon tow physics. |

---

### 2. Verified Bugs & Gaps STILL MISSING in `develop` (High Priority Fixes)

| Issue / Subsystem | Location in `develop` | Upstream Fix & Evidence | Action Needed |
|---|---|---|---|
| **Corpse Skill Casting** | `Unit.cs:920-934` (`InterruptSkills`) | **Missing.** `InterruptSkills()` only checks `ActivePlotState` and `SkillTask`. It **fails to cancel `ActiveSkillController`**. When a mob dies, channeled skills (e.g. spider webs, breath attacks) continue channeling from dead corpses! | Add `if (ActiveSkillController != null) { ActiveSkillController.End(); ActiveSkillController = null; }` before the `SkillTask` check. |
| **Pos-Targeted Skill Aiming** | `Behavior.cs:173-177` | **Bug Present.** `skillCastTarget` sets `PosX/Y/Z` to `Ai.Owner.Transform.World.Position` (the caster) instead of `target.Transform.World.Position`. NPCs cast ground/AoE spells on themselves! | Change coordinate assignment to sample `target.Transform.World.Position`. |
| **UI Data Binary Corruption** | `CSSaveUIDataPacket.cs:12` | **Bug Present.** Reads UI payload using UTF-8 `stream.ReadString()`. Binary option blobs (like quest-tracker checkboxes, option key 5/6) are mangled by UTF-8 decoding and reset to default on every relog! | Change reader/writer to Latin-1 byte mapping (`System.Text.Encoding.Latin1`). Re-push option keys 4, 5, 6 in `CSSpawnCharacterPacket`. |
| **Vehicle Socket Topology** | `SlaveManager.cs` (1,275 lines vs 3,161 lines in 5.0) | **Missing.** `develop` lacks `SpawnSlaveSlaves` (sub-vehicles), `CreateSlaveDoodads` (cannons, crates, steering doodads), `AttachPointKind` socket mappings, and `RidersEscape`. | Port 5.0's attachment and seat binding models into our Jitter2 `SlaveManager`. |
| **Auction Market Telemetry** | `AuctionManager.cs` | **Missing.** Lacks `SalesData` and `SoldsData` concurrent volume and price history dictionaries. | Port 5.0's sales tracking dictionaries to support market-driven pricing and bot valuation. |
| **Resident Community Centers** | Greenfield (`ResidentManager.cs`) | **Missing.** Zero implementation in `develop`. Continental zones lack resident development stages and token contribution loops. | Port `ResidentManager` and integrate with local housing zones. |
| **Container Property Cleanup** | `Item.cs:145` & throughout | **Legacy Debt.** `develop` still references `public ItemContainer _holdingContainer { get; set; }` across 40+ call sites. | Port 3.5's public `HoldingContainer` property refactoring. |
| **Character Creation Hardening** | `CharacterManager.cs:520-585` | **Unchecked.** Invalid race, gender, or equipment template IDs can throw during creation, dropping the connection. | Add 3.5's pre-flight template validation and null checks in `CharacterManager.Create`. |

---

## Part V: Protocol Boundary Analysis: 1.2 vs. 3.5 vs. 5.0

A frequent question when reviewing newer branches is: **"Does 3.5 or 5.0 have all the packets and opcodes to implement things in 1.2?"**

The answer requires distinguishing between **wire protocols (client-specific)** and **gameplay domain models (server-side)**.

### 1. The Wire Protocol Boundary

| Metric / Dimension | ArcheAge 1.2 (`develop`) | ArcheAge 3.5 (`NL0bP`) | ArcheAge 5.0 (`AAEmu 5.0`) |
|---|:---:|:---:|:---:|
| **Packet Level Header** | `GamePacket(offset, 1)` | `GamePacket(offset, 4)` | `GamePacket(offset, 5)` |
| **CS (Client→Server) Opcodes** | 297 opcodes | 464 opcodes | 499 opcodes |
| **SC (Server→Client) Opcodes** | 523 opcodes | 818 opcodes | 868 opcodes |
| **Total Packet Handler Classes** | 730 classes | 741 classes | 878 classes |

#### Why Packets Cannot Be Copy-Pasted Between Generations:
1. **Opcode Number Remapping:** XLGames periodically reassigned numerical opcode IDs across releases. An opcode like `0x0031` in 1.2 corresponds to a different packet in 3.5 or 5.0.
2. **Packet Level Framing:** Client 1.2 strictly expects Level 1 packet headers. Transmitting a Level 5 packet results in immediate client socket disconnection.
3. **Features Absent in 1.2 Client UI:**
   * **Resident Community Centers (`ResidentManager` / `SCResident*`):** Added in ArcheAge 3.0. The 1.2 client binary has no UI dialogs, resident token slots, or map overlays for community center development.
   * **Auction Sold Record Tab (`CSAuctionSearchSoldRecordPacket`):** The historical market sales browser was introduced in 3.0. The 1.2 client auction UI only features the lowest-price lookup (`CSAuctionLowestPricePacket`).
   * **Heir & High Abilities:** Added in 3.5/4.0. The 1.2 client cannot render or assign Heir skill levels.

---

### 2. The Critical Breakthrough: Substantial 1.2 Wire Support Already Exists

When auditing `AAEmu.Game/Core/Packets/C2G/CSOffsets.cs` and `G2C/SCOffsets.cs`, **our 1.2 tree already exposes substantial wire support for the major 1.2-era systems audited so far**:

```csharp
// Slaves & Vehicles in 1.2 Offsets:
CSSpawnSlavePacket           = 0x02e;
CSDespawnSlavePacket         = 0x02f;
CSDestroySlavePacket         = 0x030;
CSBindSlavePacket            = 0x031; // Seat / Pilot binding!
CSChangeSlaveNamePacket      = 0x034;
CSRepairSlaveItemsPacket     = 0x035;
CSChangeSlaveEquipmentPacket = 0x037;
CSTurretStatePacket          = 0x03b;
CSBoardingTransferPacket     = 0x03d;
SCSlaveCreatedPacket         = 0x61;
SCSlaveDespawnPacket         = 0x63;
SCSlaveBoundPacket           = 0x64;  // Informs client of passenger/driver attachment!
SCMySlavePacket              = 0x66;
SCEscapeSlavePacket          = 0x67;  // Ejection!
SCSlaveEquipmentChangedPacket= 0x68;

// Attached Doodads in 1.2 Offsets:
CSUnbondDoodadPacket         = 0x0cd;
CSCreateDoodadPacket         = 0x0e6;
CSChangeDoodadPhasePacket    = 0x0e9;
SCDoodadCreatedPacket        = 0x10c; // Renders cannons, steering wheels, harpoons!
SCDoodadChangedPacket        = 0x10e;
SCDoodadPhaseChangedPacket   = 0x10f;
```

#### The Calibration Note: Not All Archaeology Is Finished
We must avoid claiming that *"all packet archaeology is finished"*. The very same offset table deliberately maps several unresolved entries to `0xfff` (e.g. `CSChangeSlaveTargetPacket`, `CSBuySpecialtyItemPacket`, `CSChangeHousePayPacket`, `CSSetTeamOfficerPacket`, quest reset/cheat packets, doodad UCC save, etc.). Some of these genuinely may not exist in `r208022`; others simply await live network capture verification.

#### The Vehicle Insight
Look at what 1.2 already knows:
- `CSSpawnSlave`
- `CSDespawnSlave`
- `CSDestroySlave`
- `CSBindSlave`
- `CSChangeSlaveName`
- `CSRepairSlaveItems`
- `CSTurretState`
- `CSChangeSlaveEquipment`
- `CSBoardingTransfer`

The 1.2 client is effectively shouting: **"I already know what vehicles, turrets, equipment, and boarding are—send me the server state!"**

This makes the 5.0 `SlaveManager` work much less intimidating: **we are not trying to teach an ancient client a 5.0 feature**. We are examining a later AAEmu implementation to reconstruct server behavior for a feature the 1.2 client already possessed.

---

## Part VI: Proposed Order of Fixes (Action Plan for Muse & Contributors)

To maximize velocity while preventing regressions, fixes are organized into seven dependency-ordered phases:

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Phase 0: Protocol Truth Table (Offset Classification Standards)         │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│ Phase 1: Tiny Correctness Fixes (Corpse Cast, Pos Aim, UI Data)         │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│ Phase 2: Character / Auth Hardening (Template & Malformed Input Guards) │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│ Phase 3: Vehicle & Slave Socket Topology (5.0 State + 1.2 Wire + Jitter)│
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│ Phase 4: Doodad & Public Farm Lifecycle (Simulation & Bot Farmer Loop)  │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│ Phase 5: Server-Side Economy Telemetry (SalesData / SoldsData)          │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│ Phase 6: UCC Stream Pipeline & Skill CC Interruption                    │
└─────────────────────────────────────────────────────────────────────────┘
```

---

### Phase 0: Protocol Truth Table (Classification Standard)
Before porting wire packets or state machines, lock down what we actually know by classifying opcodes under strict evidence labels (mirroring our existing `CSReturnMailPacket` practice):
* **`VERIFIED`:** Confirmed by live client traffic capture or packet dump.
* **`DEFINED`:** Exists in current `CSOffsets.cs` / `SCOffsets.cs` with an active hex opcode.
* **`INFERRED`:** Strong structural/offset evidence; needs capture verification before production lock.
* **`UNRESOLVED`:** Mapped to `0xfff` or unknown.
* **`NOT_IN_1.2`:** Confirmed later-client feature (e.g. Resident community centers, Auction Sold Record tab).

---

### Phase 1: Tiny Correctness Fixes (Immediate)
* **Goal:** Eliminate proven combat anomalies, ghost channeling, and client UI data loss with a tiny blast radius.
* **Target Files:**
  1. `AAEmu.Game/Models/Game/Units/Unit.cs:920-934` (`InterruptSkills`):
     * *Change:* Add `if (ActiveSkillController != null) { ActiveSkillController.End(); ActiveSkillController = null; }` before the `SkillTask` null check.
     * *Effect:* Stops dead mob/boss corpses from continuing to cast channeled attacks (e.g. spider webs).
  2. `AAEmu.Game/Models/Game/AI/v2/Framework/Behavior.cs:172-177`:
     * *Change:* Replace `var pos = Ai.Owner.Transform.World.Position;` with `var pos = target.Transform.World.Position;`.
     * *Effect:* Mobs with position-targeted AoE spells aim at the player instead of casting on themselves.
  3. `AAEmu.Game/Core/Packets/C2G/CSSaveUIDataPacket.cs:12` & `G2C/SCResponseUIDataPacket.cs`:
     * *Change:* Replace UTF-8 `ReadString()` with byte-exact Latin-1 decoding (`System.Text.Encoding.Latin1.GetString(stream.ReadBytes(len))`), and mirror on write.
     * *Effect:* Prevents binary corruption of quest-tracker checkboxes and option blobs on relog.
* **Verification:** Run `./scripts/gate.sh` and targeted unit/AI behavior tests.

---

### Phase 2: Character / Auth Hardening (from 3.5)
* **Goal:** Protect against malformed or corrupted creation payloads without doing unnecessary syntax housekeeping.
* **Target Files:**
  1. `AAEmu.Game/Core/Managers/UnitManagers/CharacterManager.cs:520-585`:
     * *Change:* Port 3.5's pre-flight validation for ability sets, race/gender template combinations, and null-safe equip item creation in `CharacterManager.Create`.
     * *Effect:* Invalid creation payloads trigger `SCCharacterCreationFailedPacket` rather than throwing unhandled exceptions and dropping the TCP session.
  2. *Scope Clarification:* **Do not** automatically rename `_holdingContainer` across the entire codebase just because 3.5 did. Treat container renaming as pure housekeeping unless a subsequent port explicitly requires the property abstraction.
* **Verification:** Fast gate + `CharacterCreationTests`.

---

### Phase 3: Vehicle & Slave Socket Topology (5.0 State + 1.2 Wire + Jitter2 Physics)
* **Goal:** Reconstruct vehicle and naval attachment state machines while preserving our superior Jitter2 physics engine.
* **Target Files:**
  1. `AAEmu.Game/Core/Managers/SlaveManager.cs`:
     * *Port from 5.0:* `CreateSlaveDoodads` (cannons, steering wheels, harpoons, cargo crates), `SpawnSlaveSlaves` (sub-vehicles), `_attachPoints`, `AttachPointKind`, driver/passenger attachment state machines, and `RidersEscape`.
     * *Wire to 1.2:* Emit 1.2 Level 1 packets: `SCSlaveBoundPacket` (`0x64`), `SCDoodadCreatedPacket` (`0x10c`), and `SCEscapeSlavePacket` (`0x67`).
  2. `AAEmu.Game/Physics/ShipController.cs`:
     * *Preserve:* Retain our Jitter2 rigid-body mass, buoyancy, and harpoon tow physics. Wire the steering doodad interaction directly into `ShipController` angular torque and throttle.
* **The Formula:**
  $$\text{5.0 Topology/State Machine} + \text{1.2 Wire Protocol} + \text{Our Jitter2 Physics} = \text{Best Implementation of the Three}$$
* **Verification:** `TransferRideE2e` scenario + in-game clipper summon with functional cannon and wheel.

---

### Phase 4: Doodad & Public Farm Lifecycle
* **Goal:** Advance world simulation and directly support autonomous bot farmer loops.
* **Target Files:**
  1. `AAEmu.Game/Core/Managers/UnitManagers/DoodadManager.cs`:
     * *Change:* Enforce `DoodadFuncConsumeChangerItemList` item consumption (keys, water, fertilizer, tools) before triggering phase transitions.
  2. `AAEmu.Game/Core/Managers/PublicFarmManager.cs`:
     * *Change:* Port `PublicFarmTickStartTask` to audit crop defense duration (`CommonFarmGameData.Instance.GetDoodadGuardTime`) and release expired crops to `DoodadOwnerType.System` for public gathering.
* **Verification:** Automated unit test simulating crop expiration and bot harvesting.

---

### Phase 5: Server-Side Economy Telemetry (from 5.0)
* **Goal:** Historical pricing intelligence for PlayerBots, admin dashboards, and economy simulation.
* **Target Files:**
  1. `AAEmu.Game/Core/Managers/AuctionManager.cs`:
     * *Change:* Port `SalesData` and `SoldsData` concurrent dictionaries tracking price history and volumes by `(TemplateId, Grade)`.
     * *Scope:* Keep strictly server-side (do not emit 5.0 `SCAuctionSoldRecordPacket` to 1.2 client).
* **Rationale:** The 1.2 human client does not need a sales-history UI for the server to benefit from historical pricing; bots, GM tools, and economic simulation consume this data internally.
* **Verification:** Unit tests verifying `SalesData` price recording upon buyout completion.

---

### Phase 6: UCC Stream Pipeline & Skill CC Interruption (from `feature/ucc`)
* **Goal:** Enable User Created Content image uploading and enforce crowd-control combat interruptions.
* **Target Files:**
  1. `AAEmu.Game/Core/Network/Stream/StreamNetwork.cs` & `AAEmu.Game/Core/Packets/C2S/`:
     * *Change:* Wire custom crest PNG upload handling over `:1250`, validating image bounds and broadcasting texture hashes.
  2. `AAEmu.Game/Models/Game/Skills/Skill.cs` & `Unit.cs`:
     * *Change:* Port skill cast cancellation hooks on crowd-control application (stun, silence, sleep, knockback).
* **Verification:** Skill interruption unit tests + testcrest upload script.

---

## Part VII: The Grand Archaeological Model

The relationship between the versions is now completely clear:

```
                    1.2 RETAIL BEHAVIOR
                            ↑
                            |
                    compatibility authority
                            |
        +-------------------+-------------------+
        |                   |                   |
     our develop           3.5                 5.0
     best physics      hardening/cleanup   mature state machines
     quest work        protocol lessons    vehicles/economy
     bot systems                            later bug fixes
        \                   |                   /
         \__________________|__________________/
                            |
                      selective ports
```

We are not trying to turn 1.2 into 3.5 or 5.0.  
**We are asking ten years of AAEmu development to help us finish the 1.2 game they started with.**


