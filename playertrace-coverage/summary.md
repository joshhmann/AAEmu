# AAEmu PlayerTrace Coverage Report

> **Generated:** 2026-09-16 13:05:42 UTC
> **Repository State:** `develop` @ `63f6567e7a`

## 1. Corpus Summary

| Metric | Count |
| --- | --- |
| **Trace Files Analyzed** | 14 |
| **Total Records Parsed** | 49,999 |
| **Malformed Records Skipped** | 0 |
| **Distinct Scenarios Observed** | 14 |
| **Packet Records (in + out)** | 49,274 |
| **Movement Records** | 431 |
| **Semantic Event Records** | 266 |
| **Marker Records** | 1 |
| **Unique Observed Packets** | 74 (C2S: 21, S2C: 48) |
| **Unique Trace Event Types** | 17 |
| **Corpus Time Span** | 45302.2 seconds |

**Scenarios in Corpus:** `buy_chick, combat_basic_melee, combat_basic_ranged, dismount_horse, harvest_chicken, human_potato_plant, mount_horse, pick_potato, place_chick, quest_accept_and_turnin, sell_items, summon_dismiss_pet, talk_npc, use_bag_consumable`

## 2. Packet Coverage Overview

- **Observed & Mapped to AAEmu Source:** 74 packet classes
- **Observed Unmapped Packets (Schema Drift/Debug):** 0
- **Known AAEmu Packets Unobserved (Discovery Leads):** 670 classes

### Top Observed Packets

| Packet | Direction | Opcode | Count | Scenarios | Source Path |
| --- | --- | --- | --- | --- | --- |
| `SCOneUnitMovementPacket` | S2C | `0x06C` | 30,483 | 14 | `AAEmu.Game/Core/Packets/G2C/SCOneUnitMovementPacket.cs` |
| `FastPingPacket` | PROXY | `0x015` | 4,086 | 3 | `AAEmu.Game/Core/Packets/Proxy/FastPingPacket.cs` |
| `FastPongPacket` | PROXY | `0x016` | 4,086 | 3 | `AAEmu.Game/Core/Packets/Proxy/FastPongPacket.cs` |
| `PingPacket` | PROXY | `0x012` | 1,895 | 14 | `AAEmu.Game/Core/Packets/Proxy/PingPacket.cs` |
| `PongPacket` | PROXY | `0x013` | 1,895 | 14 | `AAEmu.Game/Core/Packets/Proxy/PongPacket.cs` |
| `SCTargetChangedPacket` | S2C | `0x084` | 1,765 | 12 | `AAEmu.Game/Core/Packets/G2C/SCTargetChangedPacket.cs` |
| `SCUnitModelPostureChangedPacket` | S2C | `0x103` | 1,502 | 14 | `AAEmu.Game/Core/Packets/G2C/SCUnitModelPostureChangedPacket.cs` |
| `CSMoveUnitPacket` | C2S | `0x089` | 1,399 | 14 | `AAEmu.Game/Core/Packets/C2G/CSMoveUnitPacket.cs` |
| `SCUnitPointsPacket` | S2C | `0x0BA` | 577 | 10 | `AAEmu.Game/Core/Packets/G2C/SCUnitPointsPacket.cs` |
| `CompressedGamePackets` | UNKNOWN | `0x000` | 187 | 4 | `AAEmu.Game/Core/Packets/CompressedGamePackets.cs` |
| `SCSkillEndedPacket` | S2C | `0x0A3` | 168 | 10 | `AAEmu.Game/Core/Packets/G2C/SCSkillEndedPacket.cs` |
| `SCSkillFiredPacket` | S2C | `0x0A2` | 165 | 10 | `AAEmu.Game/Core/Packets/G2C/SCSkillFiredPacket.cs` |
| `CSStartSkillPacket` | C2S | `0x052` | 146 | 9 | `AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs` |
| `SCAiAggroPacket` | S2C | `0x1C4` | 115 | 7 | `AAEmu.Game/Core/Packets/G2C/SCAiAggroPacket.cs` |
| `SCChatMessagePacket` | S2C | `0x0CE` | 79 | 14 | `AAEmu.Game/Core/Packets/G2C/SCChatMessagePacket.cs` |

## 3. Semantic Event Coverage

| Event Category & Name | Category | Count | Scenarios |
| --- | --- | --- | --- |
| `packet:packet_out` | packet | 41,634 | 14 |
| `packet:packet_in` | packet | 7,640 | 14 |
| `movement:movement_update` | movement | 247 | 5 |
| `skill:skill_requested` | skill | 146 | 9 |
| `movement:move_begin` | movement | 92 | 9 |
| `movement:move_stop` | movement | 92 | 9 |
| `skill:instant_cast` | skill | 91 | 6 |
| `lifecycle:trace_started` | lifecycle | 14 | 14 |
| `lifecycle:trace_stopped` | lifecycle | 13 | 13 |
| `world:doodad_phase_changed` | world | 8 | 4 |
| `refusal:skill_refused` | refusal | 6 | 1 |
| `resource:money_changed` | resource | 4 | 4 |
| `interaction:doodad_use` | interaction | 4 | 3 |
| `skill:cast_started` | skill | 3 | 3 |
| `resource:labor_changed` | resource | 2 | 2 |

## 4. Most Common Action Sequences & Skeletons

*(High-frequency movement updates and keepalive pings normalized/collapsed)*

### Top 2-Gram Transitions

| Transition | Count | Scenarios Seen |
| --- | --- | --- |
| `FastPongPacket` &rarr; `FastPingPacket` | 4078 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `FastPingPacket` &rarr; `FastPongPacket` | 2575 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `SCTargetChangedPacket` &rarr; `movement` | 1720 | `combat_basic_melee,combat_basic_ranged,dismount_horse,human_potato_plant,pick_potato,quest_accept_and_turnin,use_bag_consumable` |
| `movement` &rarr; `FastPongPacket` | 1239 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `movement` &rarr; `SCTargetChangedPacket` | 1220 | `combat_basic_melee,combat_basic_ranged,dismount_horse,harvest_chicken,human_potato_plant,mount_horse,pick_potato,quest_accept_and_turnin,sell_items,summon_dismiss_pet,talk_npc,use_bag_consumable` |
| `FastPingPacket` &rarr; `movement` | 844 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `FastPingPacket` &rarr; `SCTargetChangedPacket` | 454 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `SCUnitPointsPacket` &rarr; `SCSkillFiredPacket` | 165 | `combat_basic_melee,combat_basic_ranged,dismount_horse,harvest_chicken,human_potato_plant,mount_horse,pick_potato,quest_accept_and_turnin,summon_dismiss_pet,use_bag_consumable` |
| `movement` &rarr; `SCUnitPointsPacket` | 153 | `combat_basic_melee,combat_basic_ranged,dismount_horse,harvest_chicken,human_potato_plant,pick_potato,quest_accept_and_turnin` |
| `CompressedGamePackets` &rarr; `SCSkillEndedPacket` | 129 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |

### Top 3-Gram Action Skeletons

| Sequence Pattern | Count | Scenarios Seen |
| --- | --- | --- |
| `FastPingPacket -> FastPongPacket -> FastPingPacket` | 2573 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `FastPongPacket -> FastPingPacket -> FastPongPacket` | 2571 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `movement -> FastPongPacket -> FastPingPacket` | 1235 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `movement -> SCTargetChangedPacket -> movement` | 1189 | `combat_basic_melee,combat_basic_ranged,dismount_horse,human_potato_plant,pick_potato,quest_accept_and_turnin,use_bag_consumable` |
| `SCTargetChangedPacket -> movement -> FastPongPacket` | 963 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `FastPongPacket -> FastPingPacket -> movement` | 842 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `FastPingPacket -> movement -> SCTargetChangedPacket` | 617 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `SCTargetChangedPacket -> movement -> SCTargetChangedPacket` | 506 | `combat_basic_melee,combat_basic_ranged,dismount_horse,quest_accept_and_turnin` |
| `FastPongPacket -> FastPingPacket -> SCTargetChangedPacket` | 452 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |
| `FastPingPacket -> SCTargetChangedPacket -> movement` | 452 | `combat_basic_melee,combat_basic_ranged,quest_accept_and_turnin` |

## 5. Candidate Action Families

> [!CAUTION]
> **CORRECTNESS RULE**: Packet and event similarity provides structural evidence, NOT proof of identical server semantics.
> Candidate families are labeled **POSSIBLE RELATED ACTIONS**. Inspect server code before making architectural assumptions.

### Family: Doodad Placement / World Creation `[Confidence: HIGH]`
- **Rationale:** Scenarios share CSCreateDoodadPacket and world:doodad_created without invoking Skill.Use or CastTask lifecycles.
- **Member Scenarios:** `human_potato_plant, place_chick`
- **Common Packets:** `CSCreateDoodadPacket, CSMoveUnitPacket, CSSendChatMessagePacket, PingPacket, PongPacket, SCChatMessagePacket, SCDoodadCreatedPacket, SCItemTaskSuccessPacket, SCOneUnitMovementPacket, SCUnitModelPostureChangedPacket`
- **Common Events:** `lifecycle:trace_started, packet:packet_in, packet:packet_out, world:doodad_created, world:doodad_phase_changed`
- **Key Differences Among Members:**
  - `human_potato_plant`: `CSBuyItemsPacket, CSChangeTargetPacket, CSInteractNPCEndPacket, CSInteractNPCPacket, CSStartInteractionPacket, CSStartSkillPacket (+19 more)`
  - `place_chick`: `lifecycle:trace_stopped`

### Family: Skill-Mediated Doodad Gathering / Harvesting `[Confidence: HIGH]`
- **Rationale:** Scenarios execute CSStartInteractionPacket followed by CSStartSkillPacket, cast lifecycle, and doodad phase modifications.
- **Member Scenarios:** `combat_basic_melee, harvest_chicken, human_potato_plant, pick_potato, quest_accept_and_turnin`
- **Common Packets:** `CSChangeTargetPacket, CSMoveUnitPacket, CSStartSkillPacket, PingPacket, PongPacket, SCChatMessagePacket, SCOneUnitMovementPacket, SCSkillEndedPacket, SCSkillFiredPacket, SCTargetChangedPacket, SCTimeOfDayPacket, SCUnitModelPostureChangedPacket, SCUnitPointsPacket`
- **Common Events:** `lifecycle:trace_started, movement:move_begin, movement:move_stop, packet:packet_in, packet:packet_out, skill:skill_requested`
- **Key Differences Among Members:**
  - `combat_basic_melee`: `CSInteractNPCPacket, CSStartInteractionPacket, CompressedGamePackets, FastPingPacket, FastPongPacket, SCAggroTargetChangedPacket (+16 more)`
  - `harvest_chicken`: `CSSendChatMessagePacket, SCCharacterLaborPowerChangedPacket, SCDoodadPhaseChangedPacket, SCErrorMsgPacket, SCExpChangedPacket, SCGamePointChangedPacket (+7 more)`
  - `human_potato_plant`: `CSBuyItemsPacket, CSCreateDoodadPacket, CSInteractNPCEndPacket, CSInteractNPCPacket, CSSendChatMessagePacket, CSStartInteractionPacket (+15 more)`
  - `pick_potato`: `CSSendChatMessagePacket, SCBmPointPacket, SCCharacterLaborPowerChangedPacket, SCDoodadPhaseChangedPacket, SCDoodadRemovedPacket, SCDoodadsCreatedPacket (+13 more)`
  - `quest_accept_and_turnin`: `CSChangeMateTargetPacket, CSCompleteQuestContextPacket, CSCreateSkillControllerPacket, CSInteractNPCEndPacket, CSInteractNPCPacket, CSMountMatePacket (+48 more)`

### Family: NPC Interaction & Commercial Services `[Confidence: HIGH]`
- **Rationale:** Scenarios communicate with NPCs via CSInteractNPCPacket / SCNpcInteractionSkillListPacket; specialized branches handle buy/sell item lists.
- **Member Scenarios:** `buy_chick, combat_basic_melee, human_potato_plant, quest_accept_and_turnin, sell_items, talk_npc`
- **Common Packets:** `CSInteractNPCPacket, CSMoveUnitPacket, CSStartInteractionPacket, PingPacket, PongPacket, SCAiAggroPacket, SCChatMessagePacket, SCNpcInteractionSkillListPacket, SCOneUnitMovementPacket, SCTimeOfDayPacket, SCUnitModelPostureChangedPacket`
- **Common Events:** `lifecycle:trace_started, packet:packet_in, packet:packet_out`
- **Key Differences Among Members:**
  - `buy_chick`: `CSBuyItemsPacket, CSSaveTutorialPacket, CSSendChatMessagePacket, SCItemTaskSuccessPacket, SCTutorialSavedPacket, lifecycle:trace_stopped (+1 more)`
  - `combat_basic_melee`: `CSChangeTargetPacket, CSStartSkillPacket, CompressedGamePackets, FastPingPacket, FastPongPacket, SCAggroTargetChangedPacket (+21 more)`
  - `human_potato_plant`: `CSBuyItemsPacket, CSChangeTargetPacket, CSCreateDoodadPacket, CSInteractNPCEndPacket, CSSendChatMessagePacket, CSStartSkillPacket (+20 more)`
  - `quest_accept_and_turnin`: `CSChangeMateTargetPacket, CSChangeTargetPacket, CSCompleteQuestContextPacket, CSCreateSkillControllerPacket, CSInteractNPCEndPacket, CSMountMatePacket (+53 more)`
  - `sell_items`: `CSChangeTargetPacket, CSListSoldItemPacket, CSSellItemsPacket, CSSendChatMessagePacket, SCGotMailPacket, SCItemTaskSuccessPacket (+6 more)`
  - `talk_npc`: `CSChangeTargetPacket, CSInteractNPCEndPacket, CSSendChatMessagePacket, SCTargetChangedPacket, lifecycle:trace_stopped, movement:move_begin (+1 more)`

### Family: Mate / Mount Companion Operations `[Confidence: HIGH]`
- **Rationale:** Scenarios operate on companion entities (mount/dismount/pet summon) using Mate network primitives.
- **Member Scenarios:** `dismount_horse, mount_horse, quest_accept_and_turnin, summon_dismiss_pet`
- **Common Packets:** `CSCreateSkillControllerPacket, CSMoveUnitPacket, PingPacket, PongPacket, SCChatMessagePacket, SCOneUnitMovementPacket, SCSkillEndedPacket, SCSkillFiredPacket, SCTargetChangedPacket, SCTimeOfDayPacket, SCUnitModelPostureChangedPacket, SCUnitPointsPacket`
- **Common Events:** `lifecycle:trace_started, lifecycle:trace_stopped, packet:packet_in, packet:packet_out`
- **Key Differences Among Members:**
  - `dismount_horse`: `CSSendChatMessagePacket, CSUnMountMatePacket, SCUnitDetachedPacket, movement:move_begin, movement:move_stop, movement:movement_update`
  - `mount_horse`: `CSChangeMateTargetPacket, CSChangeTargetPacket, CSMountMatePacket, CSSendChatMessagePacket, CSStartSkillPacket, SCItemTaskSuccessPacket (+6 more)`
  - `quest_accept_and_turnin`: `CSChangeMateTargetPacket, CSChangeTargetPacket, CSCompleteQuestContextPacket, CSInteractNPCEndPacket, CSInteractNPCPacket, CSMountMatePacket (+51 more)`
  - `summon_dismiss_pet`: `CSChangeMateTargetPacket, CSRemoveMatePacket, CSSendChatMessagePacket, CSStartSkillPacket, SCItemTaskSuccessPacket, SCMateSpawnedPacket (+7 more)`

## 6. Scenario Similarity Pairs

| Scenario A | Scenario B | Jaccard Sim | Transition Overlap | Classification |
| --- | --- | --- | --- | --- |
| `harvest_chicken` | `pick_potato` | 0.84 | 0.51 | POSSIBLE RELATED ACTIONS |
| `mount_horse` | `summon_dismiss_pet` | 0.78 | 0.52 | POSSIBLE RELATED ACTIONS |
| `sell_items` | `talk_npc` | 0.74 | 0.43 | POSSIBLE RELATED ACTIONS |
| `combat_basic_melee` | `combat_basic_ranged` | 0.71 | 0.41 | POSSIBLE RELATED ACTIONS |
| `mount_horse` | `use_bag_consumable` | 0.69 | 0.19 | POSSIBLE RELATED ACTIONS |
| `summon_dismiss_pet` | `use_bag_consumable` | 0.67 | 0.23 | POSSIBLE RELATED ACTIONS |
| `buy_chick` | `sell_items` | 0.62 | 0.31 | POSSIBLE RELATED ACTIONS |
| `buy_chick` | `talk_npc` | 0.62 | 0.35 | POSSIBLE RELATED ACTIONS |
| `dismount_horse` | `summon_dismiss_pet` | 0.59 | 0.19 | POSSIBLE RELATED ACTIONS |
| `human_potato_plant` | `pick_potato` | 0.59 | 0.39 | POSSIBLE RELATED ACTIONS |
| `combat_basic_ranged` | `use_bag_consumable` | 0.58 | 0.13 | POSSIBLE RELATED ACTIONS |
| `harvest_chicken` | `human_potato_plant` | 0.57 | 0.33 | POSSIBLE RELATED ACTIONS |
| `combat_basic_melee` | `quest_accept_and_turnin` | 0.56 | 0.27 | POSSIBLE RELATED ACTIONS |
| `dismount_horse` | `talk_npc` | 0.54 | 0.12 | POSSIBLE RELATED ACTIONS |
| `dismount_horse` | `use_bag_consumable` | 0.54 | 0.11 | POSSIBLE RELATED ACTIONS |

## 7. Coverage Gaps & Blind Spots

### `[UNTRACED ACTION FAMILY]` Basic Combat & Auto-Attack Lifecycle
- **Description:** No human trace currently records hostile target selection, basic melee/ranged attack sequences, damage exchange, or combat state transitions.
- **Target AAEmu Packets:** `CSSetTargetPacket, CSAttackPacket, CSCancelSkillPacket, SCDamagePacket, SCSkillDamagePacket`
- **Suggested Remediation:** Collect trace for basic melee combat against a stationary hostile mob (e.g., target -> engage -> attack -> defeat).

### `[UNTRACED ACTION FAMILY]` Direct Player-to-Player Trading
- **Description:** P2P trade window initialization, item placement, trade lock, and trade commitment have zero representation in the corpus.
- **Target AAEmu Packets:** `CSTradeLockPacket, CSPutupTradeItemPacket, CSAskTradePacket, SCTradeStartPacket`
- **Suggested Remediation:** Trace bilateral trade between two player characters with item & gold exchange.

### `[UNTRACED ACTION FAMILY]` Recipe Crafting & Production
- **Description:** Workstation interaction, recipe consumption, crafting progress bar, and crafted item insertion into inventory remain untraced.
- **Target AAEmu Packets:** `CSCraftPacket, SCCraftStartedPacket, SCCraftEndedPacket`
- **Suggested Remediation:** Trace crafting a basic item at a carpentry/blacksmithing workbench.

### `[UNTRACED ACTION FAMILY]` Quest Acceptance, Objective Progression & Turn-In
- **Description:** NPC dialogue quest acceptance, objective tracker updates, and quest reward handoff have not been traced.
- **Target AAEmu Packets:** `CSQuestStartWithPacket, CSQuestCompletePacket, SCQuestStatusPacket`
- **Suggested Remediation:** Trace picking up an introductory quest from an NPC, completing objective, and turning it in.

### `[PACKET KNOWN BUT CONTEXT UNKNOWN]` Consumable / Bag Item Activation
- **Description:** Client inventory item consumption (potions, food, quest items) exists in server code but has no trace corroboration.
- **Target AAEmu Packets:** `CSUseBagItemPacket, CSDeleteItemPacket, SCItemCooldownPacket`
- **Suggested Remediation:** Trace using a bread or potion from the player inventory bag.

### `[TRACE TOOLING LIMITATION]` Absence of Action-End Intent Markers & UI Dialog Hooks
- **Description:** Traces currently log /trace mark as instantaneous events without duration boundaries. Client-side UI dialog choices are only partially visible through network request packets.
- **Suggested Remediation:** Standardize '/trace mark start:<action>' and '/trace mark end:<action>' pairs to delimit complex interaction windows.

## 8. Recommended Next Human Traces (Ranked)

| Rank | Suggested Scenario | Priority | Target Family | Rationale |
| --- | --- | --- | --- | --- |
| **#1** | `combat_basic_melee` | **HIGH** | Hostile Unit Combat | Fills the entire untraced Combat action family; validates targeting, GCD, auto-attack, damage packets, and target death handling. |
| **#2** | `quest_accept_and_turnin` | **HIGH** | Quest Lifecycle | Bridges NPC interaction with quest progression and inventory reward delivery; exercises dialogue choice branches. |
| **#3** | `craft_single_item` | **HIGH** | Crafting & Processing | Exercises workbench doodad interaction, recipe labor deduction, and output item creation, filling the untraced Crafting family. |
| **#4** | `use_bag_consumable` | **MEDIUM** | Inventory & Consumables | Tests client-initiated inventory item consumption (CSUseBagItemPacket) without workstation/NPC dependencies. |
| **#5** | `trade_direct_player` | **MEDIUM** | Player-to-Player Trading | Validates two-player interaction, trade synchronization, item locking, and atomic inventory commit. |

---
*Report generated by AAEmu PlayerTrace Coverage Mapper v1.0*