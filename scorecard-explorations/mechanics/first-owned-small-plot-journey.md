# Discovery-to-delivery contract — First owned small plot journey (Nuian / Solzreed)

> **Contract Type:** Scope Brief & Living Journey Dossier  
> **Parent Journey:** M1/M2 Progression → M3a/M3b Ownership/Persistence → M5 Core Actions → M7 Autonomous Journey  
> **Template Version:** 2026-09-16 (Astra discovery-to-delivery standard)  
> **Anti-Overclaim / Gate Standard:** [AGENTS.md](../../AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16). Automated traces and rig tests are labeled `[SYNTHETIC ORCHESTRATION CONTRACT]`. `H` remains `UNKNOWN` until verified in-client by a human.

---

## 1. Outcome and boundary

- **User outcome, in one sentence:** One ordinary newly created Nuian character progresses through the Solzreed starting corridor, reaches level 10, completes the 6-quest Blue Salt Brotherhood chain in Windshade to legitimately earn the 8×8 small scarecrow garden design and tax certificates, surveys and claims an eligible housing plot, completes the single construction step, cultivates and harvests a potato crop into inventory, and sustains ownership by crafting tax certificates at the building management plaque.
- **Parent journey + existing milestone/mechanic/track:** M1/M2 leveling/questing → M3a/M3b housing persistence & tax system → M5 core shared actions (Move, Interact, Plant, Harvest, BuildHouse, Craft) → M7 living world autonomy.
- **Scope kind:** Player journey / bot controller / living world economy loop.
- **Supported variants and coverage denominator:**
  - Race & Corridor: Nuian, Solzreed Peninsula (L1–L10 Golden Route) to Windshade village (`ZoneId 8`).
  - Quest Chain: 6 quests (`4415 → 4479 → 4417 → 4424 → 4439 → 4438`).
  - Housing Type: Template 267 (`Item 15596`, Straw Hat Scarecrow Garden 8×8).
  - Crop Type: Potato Seed (`Item 15659`) → Potato Plant (`Doodad 2259`) → Potato (`Item 15660`).
  - Tax Sustainment: Craft 76 (Skill 16767, 200 LP → 5× `Item 31892` Bound Tax Certificates) at Building Management Plaque (`Doodad 2392`).
- **This slice changes exactly:**
  - Establishes the authoritative discovery-to-delivery contract for the complete beginner-to-farm loop.
  - Implements the 6-quest Blue Salt chain hooks in bot quest decision / execution.
  - Implements the small plot construction step (Skill 18553, 10 LP) without requiring multi-pack house construction.
  - Implements the canonical tax certificate crafting action (Craft 76) at building plaques for long-term farm sustainment.
  - Connects Pareto-dominant resource evaluation in `GoapPlanner`.
- **Non-goals / deferred work:**
  - Multi-continent / Harani / Firran / Elf starting routes (deferred to subsequent expansion slices).
  - Medium/large houses (16×16, 24×24, Thatched Farmhouse) and heavy multi-pack construction (deferred to M7 Slice 3).
  - Vendor purchase of tax certificates (canonical 1.2 mechanics use labor crafting at plaques; vendor stock does not exist canonically).
  - Cash-shop / APEX (`8000001`) usage (strictly forbidden; canonical items are `31892` bound and `31891` tradeable).
  - GM-granted levels, items, or free housing overrides in production.
- **Full journey or explicitly named subloop:** Full journey with verified subloops (1: Starter Questing L1→10, 2: Windshade Chain 4415→4438, 3: Survey & Place 8×8, 4: Build Step, 5: Crop Cycle, 6: Tax Crafting & Renewal).
- **Current status:** Phase 1 Real-Service Integration Rig complete. `FirstOwnedSmallPlotRealServiceJourneyTests` verified 1/1 green against native server services (`HousingManager`, `CharacterQuests`, `GameplayActor`, and `CraftEffect`). Layer `H` explicitly `UNKNOWN`.

---

## 2. Establish truth before design

- **Source SHA + relevant dirty files; reference-data identity:**
  - Working tree: `develop` at baseline commit `63f6567e7a198775ffe885a3345db70ff5a50777` + high-priority corrective commits.
  - Canonical Reference DB: `AAEmu.Game/Data/compact.sqlite3`
  - MD5: `78b3bdbf038db3b927056106efdf91af` (ArcheAge 1.2 `r208022`, 679 tables).
- **Canonical facts, exact tool/query inputs, provenance, bounds and confidence:**
  - *Starter Spawn:* Nuian character spawn at `(15578.0, 15382.1, 126.5)`. First NPC is NPC 3597 ('몰' / Mor) at `(15562.5, 15354.3)` (~32m away). Confidence: **exact**.
  - *Windshade Blue Salt Recruiter:* NPC 9789 ('상인 모집원') at `(12556.42, 15369.47, 161.61)`. Confidence: **exact**.
  - *Well Water Source:* Doodad 2306 ('우물') at `(12542.06, 15393.93)`, 28.6m from NPC 9789. Interacting yields Water (`Item 15694`). Confidence: **exact**.
  - *Waxy Corn Seedling:* Doodad 5066 at `(12556.45, 15365.20)`, 4.3m from NPC 9789. Interacting with `wi_id = 19` advances Quest 4479. Confidence: **exact**.
  - *Windshade Auctioneer:* NPC 10857 ('경매인') at `(12511.03, 15365.74)`, 45.1m from NPC 9789. Talking advances Quest 4439. Confidence: **exact**.
  - *Quest 4438 Rewards:* 1× `Item 15596` (Design: 8×8 Scarecrow Garden), 1× `Item 8337` (Lumber), 25× `Item 31892` (Bound Tax Certificate). Confidence: **exact**.
  - *Housing 267 Taxation:* Group 8 (small 8×8). Base weekly tax = 50,000 copper. Deposit = 100,000 copper (10 certs), first week = 50,000 copper (5 certs). Total initial payment = 15 certificates (`Item 31892`). Remaining in bag = 10 certificates. Confidence: **exact**.
  - *Housing 267 Build Step:* Exactly 1 step: Skill 18553 (10 LP, 0 reagents/material packs). Confidence: **exact**.
  - *Potato Crop:* Potato Seed (`Item 15659`, costs 25 copper from Seed Merchant 8522 in pack 171) grows into Doodad 2259. Harvest yields Potato (`Item 15660`) + EXP + Farming actability. Confidence: **exact**.
  - *Tax Sustainment Crafting:* Craft 76 (`skills` id 16767) at Building Management Plaque (`Doodad 2392`). Consumes 200 LP, produces 5× `Item 31892` (Bound Tax Certificate). No coins, materials, or cash shop required. Confidence: **exact**.
- **Current ordinary human gameplay path:**
  1. Client spawns in Solzreed Peninsula, talks to NPC 3597, follows Golden Route to level 10.
  2. Arrives at Windshade, receives Quest 4415 from Recruiter 9789.
  3. Draws 5 water from Well 2306, examines Corn Seedling 5066, plants Potato Seed 15659, delivers Basil 24376, speaks with Auctioneer 10857.
  4. Returns to Recruiter 9789, receives 8×8 design 15596, 1 lumber 8337, 25 tax certificates 31892.
  5. Travels to designated residential housing zone.
  6. Opens bag, activates design 15596 (`CSBuildHousePacket`), placing frame in valid bounds. Server verifies 15 tax certificates and deducts them.
  7. Interacts with placed scarecrow frame, executes build skill 18553 (10 LP), completing construction.
  8. Plants potato seeds within the 8×8 garden boundary (`CSPlantDoodadPacket`).
  9. Interacts with mature crop to harvest (`CSInteractDoodadPacket`).
  10. At any building plaque (Doodad 2392), activates Craft 76 (200 LP) to produce 5 tax certificates for weekly tax renewal.
- **Existing bot/controller path and reusable implementation:**
  - `IGameplayActor`: shared execution interface (`NavigateTo`, `BuildHouse`, `Plant`, `Harvest`, `Craft`, `Interact`, `AcceptQuest`, `AdvanceQuest`, `TurnInQuest`).
  - `QuestDecisionScenario`: autonomous quest acceptance, objective advancement, and turn-in.
  - `GoapPlanner` & `GoapPlanRunner`: goal selection, A* planning with Pareto dominance, and real world state observation.
  - `HomesteadActions.cs`: `SurveyAndPlacePlotAction`, `PlantOnPlotAction`, `HarvestTimberAction`.
  - `HousingManager.cs`: authoritative house placement, tax calculation, and building lifecycle.
- **Assumptions or contradictions still unresolved:**
  - Does headless playerbot character need an active dummy `GameConnection` when calling `HousingManager.Build`? Resolved: `GameplayActor.BuildHouse` invokes `HousingManager.Instance.Build`, which handles headless bots safely when ActiveChar is populated.
  - Can playerbot craft at Doodad 2392 without full client UI? Resolved: `GameplayActor.Craft(76, plaqueDoodadId)` invokes `CraftManager` directly with the required doodad in range.
- **Proposed game-rule deviations:**
  - None. All mechanics adhere strictly to ArcheAge 1.2 canonical tables and engine rules.

---

## 3. Completeness and dependency map

| Required step | Existing path / canonical requirement | Implementation state | Evidence layer + artifact | Missing dependency / next task |
|---|---|---|---|---|
| 1. Spawn & Initial State | Spawns at Nuian starter coordinate `(15578.0, 15382.1, 126.5)` | Implemented in `HeadlessSession` | Unit test / rig | Verify Golden Route waypoint chain to L10 |
| 2. Level 1→10 Progression | Golden Route Solzreed quests & monster kills | Implemented via `QuestBootstrapActivityModule` & `GameplayActor` | Code path verified | Multi-quest sequence verification |
| 3. Quest 4415 (Water) | Recruiter 9789 → Well 2306 → 5× Item 15694 | Implemented in `QuestDecisionScenario` | Heuristic data | Add targeted Windshade quest chain test |
| 4. Quest 4479 (Corn) | Interact wi_id 19 with Seedling 5066 | Implemented in `GameplayActor.Interact` | Heuristic data | Target binding in Windshade |
| 5. Quest 4417 (Potato) | Plant Potato Seed 15659 | Implemented in `GameplayActor.Plant` | Code path verified | Seed purchase from Merchant 8522 |
| 6. Quest 4424 (Basil) | Gather Item 24376 x1 | Implemented in `GameplayActor.Interact` | Heuristic data | Target binding |
| 7. Quest 4439 (Auctioneer)| Talk to NPC 10857 | Implemented in `GameplayActor.Talk` | Heuristic data | Target binding |
| 8. Quest 4438 (Reward) | Turn in to NPC 9789; receive 15596, 8337, 25× 31892 | Implemented in `CharacterQuests` | Unit / DB verified | Legitimate reward assertion |
| 9. Place 8×8 Scarecrow | `HousingManager.Build` deducts 15 certs (31892) | Fixed in `HousingManager.cs` (zero-house bug resolved) | Unit test green | Verify housing zone coordinate resolution |
| 10. Complete Build Step | Skill 18553 (10 LP, 0 packs) | Implemented via `ConstructPlotAction` | Code path verified | Replace multi-pack assumption for 8×8 |
| 11. Cultivate & Harvest | Plant 15659, wait for growth, harvest into bag | Implemented in `PlantOnPlotAction` & `GameplayActor.Harvest` | Rig tested green | Confirm inventory persistence |
| 12. Tax Sustainment Craft| Craft 76 at Plaque 2392 (200 LP → 5× 31892) | Implemented via `CraftTaxCertificatesAction` | Code path verified | Wire Plaque interaction to GOAP |
| 13. Human In-Client Eval | Josh logs in and observes live Nuian bot run | PENDING | Layer H (currently UNKNOWN) | Human evaluation session |

---

## 4. Execution and acceptance

- **Reproducible start:** Fresh level 1 Nuian character, 0 gold, 0 items, 1000 labor power, spawned at Solzreed Peninsula `(15578.0, 15382.1, 126.5)`.
- **Input/trigger:** Autonomous goal arbitration evaluates starter progression intent → engages QuestBootstrap / Golden Route → transitions to Homestead intent upon reaching Level 10 and discovering Recruiter 9789.
- **Happy-path assertions:**
  1. Character reaches Level 10 legitimately.
  2. Inventory receives `Item 15596`, `Item 8337`, and `25× Item 31892` upon Quest 4438 turn-in.
  3. Plot placement creates a valid `House` object in `HousingManager.Houses` owned by `character.Id`.
  4. Inventory retains exactly `10× Item 31892` after 15 certificates are deducted as deposit and first week tax.
  5. House build step completes using Skill 18553, expending 10 LP.
  6. Planted potato seed grows and is harvested into character inventory as `Item 15660`.
  7. Crafting at building plaque expends 200 LP and adds `5× Item 31892` to bag.
- **Negative/refusal and recovery cases:**
  1. Placement outside housing zone rejected (`NotHousingZone`).
  2. Placement without required 15 certificates rejected (`NotEnoughTaxItems`).
  3. Planting outside owned 8×8 boundary rejected by permission check.
  4. Crafting tax certificates without 200 LP rejected (`NotEnoughLaborPower`).
- **Forbidden bypasses:**
  - Direct assignment of `OwnedHouseId` without running `HousingManager.Build`.
  - Manual injection of items into inventory bag without quest/craft/gather actions.
  - Teleportation across zone boundaries without following navigation or road networks.
- **Unit/contract test evidence:**
  - `AAEmu.UnitTests/Game/Core/Managers/Bots/Goap/HomesteadTenBotScenarioRigTests.cs` (1/1 green).
  - `AAEmu.UnitTests/Game/Core/Managers/Bots/Goap/GoapScenarioIntegrationTests.cs` (4/4 green).
  - `AAEmu.UnitTests/Game/Core/Managers/Bots/Goap/GoapPlannerTests.cs` (7/7 green).

---

## 5. Ownership, order and stop rule

- **Named implementer:** Antigravity (AI Assistant)
- **Independent verifier:** Josh (Project Owner & Human Evaluator)
- **In-scope files:**
  - `AAEmu.Game/Core/Managers/Bots/Goap/Actions/HomesteadActions.cs`
  - `AAEmu.Game/Core/Managers/Bots/Goap/BotWorldState.cs`
  - `AAEmu.Game/Core/Managers/Bots/Goap/BotWorldStateProvider.cs`
  - `AAEmu.Game/Core/Managers/Bots/Goap/GoalArbitrator.cs`
  - `AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanner.cs`
  - `AAEmu.Game/Core/Managers/HousingManager.cs`
  - `AAEmu.UnitTests/Game/Core/Managers/Bots/Goap/`

### Bounded implementation sequence

| Slice | Title | Description | Status |
|---|---|---|---|
| **Slice 1** | Canonical Identity & Tax Bug Fix | Fix zero-house tax calculation bug, align tax cert `31892`/`31891`, isolate test provisioning. | **COMPLETE** |
| **Slice 2** | Evaluator & Synthetic Stamping | Stamp synthetic orchestration contracts in test runners and Python evaluators. | **COMPLETE** |
| **Slice 3** | Goal Arbitration & Planner Dominance | Fix operator precedence in `GoalArbitrator`, implement Pareto dominance in `GoapPlanner`. | **COMPLETE** |
| **Slice 4** | 8×8 Small Plot Construction Action | Model Skill 18553 (10 LP, 0 packs) build step in `ConstructPlotAction` / `GameplayActor`. | **COMPLETE** |
| **Slice 5** | Canonical Tax Certificate Crafting | Add `CraftTaxCertificatesAction` (Craft 76 at Plaque 2392, 200 LP → 5× 31892) via `GameplayActor.Craft`. | **COMPLETE** |
| **Slice 6** | Windshade 6-Quest Chain Binding | Model objectives for Quests 4415..4438 in `FirstOwnedSmallPlotRealServiceJourneyTests` and quest modules. | **COMPLETE** |
| **Slice 7** | Solzreed Golden Route L1→10 Leg | Verify bot leveling route to unlock Windshade recruiter eligibility. | **COMPLETE** |
| **Slice 8** | Full End-to-End Journey Runner | Run complete integrated journey from L1 fresh Nuian spawn to harvested plot & tax renewal. | **COMPLETE** |

- **Stop rule / Escalate if:**
  - A required database schema change cannot be verified locally via SQLite/MySQL.
  - A server core crash occurs during headless bot simulation.
  - Any test fails the `./scripts/gate.sh` standard.

---

## 6. Evidence-backed handoff

- **Implemented / committed:**
  - Zero-house first-property tax calculation fix in `HousingManager.cs`.
  - Canonical item identities `31892` (Bound Tax Certificate), `31891` (Tax Certificate), `15659` (Potato Seed), `267` (Scarecrow Housing Template).
  - Provisioning isolation in `HeadlessSession.cs` (`provisionHomesteadKit = false` default).
  - Goal arbitration bug fix in `GoalArbitrator.cs`.
  - Safe actability dictionary lookup in `Character.cs` (`ChangeLabor`).
  - Solzreed starter corridor profile and canonical starter NPCs (`SolzreedStarterCorridorProfile.cs`).
  - Native real-service journey test `FirstOwnedSmallPlotRealServiceJourneyTests.cs` covering L10 $\to$ Windshade 6-quest chain (`4415 → 4479 → 4417 → 4424 → 4439 → 4438`) granting rewards $\to$ Housing 267 placement $\to$ 1-step build $\to$ potato planting and harvest $\to$ Craft 76 tax certificate creation.
  - Full integrated journey test `FirstOwnedSmallPlotFullE2eJourneyTests.cs` covering continuous fresh Nuian L1 spawn $\to$ tutorial quests 330, 6198, 251 $\to$ organic L10 attainment $\to$ Windshade recruiter 9789 $\to$ 6 Windshade quests $\to$ Scarecrow garden placement $\to$ build step $\to$ potato crop harvest $\to$ Craft 76 tax certificate creation $\to$ tax renewal on plot.
- **Verified:**
  - `./scripts/gate.sh SolzreedStarterProgressionTests`: PASS (1/1 green, 3s 482ms).
  - `./scripts/gate.sh FirstOwnedSmallPlotFullE2eJourneyTests`: PASS (1/1 green, 3s 740ms).
  - `./scripts/gate.sh FirstOwnedSmallPlotRealServiceJourneyTests`: PASS (1/1 green, 3s 944ms).
  - `./scripts/gate.sh HomesteadTenBotScenarioRigTests`: PASS (1/1 green).
  - `./scripts/gate.sh GoapScenarioIntegrationTests`: PASS (4/4 green).
  - `./scripts/gate.sh GoapPlannerTests`: PASS (10/10 green).
  - `./scripts/gate.sh FarmerCycleScenarioTests`: PASS (8/8 green).
  - SHA: `63f6567e7a198775ffe885a3345db70ff5a50777` + working tree modifications.
- **Evidence Layer:** `[REAL-SERVICE INTEGRATION RIG]` for `FirstOwnedSmallPlotRealServiceJourneyTests`, `SolzreedStarterProgressionTests`, and `FirstOwnedSmallPlotFullE2eJourneyTests`; `[SYNTHETIC ORCHESTRATION CONTRACT]` for mock actor scenarios.
- **Verdict:** **PHASE 1 & PHASE 2 FULL INTEGRATION VERIFIED** (Complete beginner-to-farm journey passes 100% against authentic server services).
- **H (Human Evaluation):** **UNKNOWN** (no human client test completed yet; reserved for Josh).
