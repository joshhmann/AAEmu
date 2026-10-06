# START HERE — Agent Onboarding & Domain Lane Dispatcher

Last refreshed: 2026-10-01 · branch of record: `develop` (`joshhmann/AAEmu`) · planning baseline: `d0d58e5848829b7a85da3847d03da00a90cb7c68` (not a fresh gate) · repo HEAD at refresh: `626548de92b8dca8af1f0f92e22d37c9fbe6074e`

**Newest checkpoint is `STATUS.md` (updated 2026-09-18), not this router's 2026-09-15 dispatch view.** Read [`STATUS.md`](STATUS.md) first for live pulse, then use the sections below. This refresh corrected the lane scopes and the blocker-ledger reference; it promoted no evidence grade. The dispatch-source question it raised was settled by the owner on 2026-10-01 — see below and [`STATUS.md`](STATUS.md).

**Dispatch source — RESOLVED 2026-10-01 (owner-ratified).** Two records previously named different next-dispatch sources and neither declared the other superseded. The owner has ruled: **`STATUS.md` line 4 governs next-dispatch selection.** It names the conversion backlog (8 missing actor/paths, §E) in `scorecard-explorations/mechanics/wave1-scenario-conversion-2026-09-18.md`. The "Immediate dispatch priority" section below is **superseded as next-work selection** and is kept as requirement context; [`ROADMAP.md`](ROADMAP.md#current-delivery-direction) remains authoritative for milestone requirements, boundaries, exit criteria and the standing freeze. Size the next card from [`STATUS.md`](STATUS.md), not from the queue below.

This document is the **single authoritative router** for agents and developers entering this repository. It tells you what to read, what to ignore, which domain lane to enter, and where active backlogs and verification gates live.

---

## Universal workflow — every zone, feature and mechanic

Use the [discovery-to-delivery contract](.kanban-templates/implementation.md):
**scope → inventory archaeology/code/tests/corpus → reconcile gaps → order slices →
implement/verify → reconcile and expand**. Parent briefs define coverage and missing
dependencies; child cards prove bounded outcomes. For each next zone/variant, reuse
proved shared mechanics and verify the local differences. Farm is the current
application, not the limit of this process.

## Immediate dispatch priority — 2026-09-16 (SUPERSEDED as dispatch source by `STATUS.md`, 2026-10-01)

First product journey: [ordinary starter → owned small plot → harvest/replant](ROADMAP.md#first-owned-small-plot-journey).
Dispatch selection itself is governed by [`STATUS.md`](STATUS.md) line 4 (owner-ratified
2026-10-01); the pointers below are **requirement context**, not the next-work choice.
Use the [shared implementation template](.kanban-templates/implementation.md) for
every implementation; fill it once in the existing card/dossier. The canonical route
requires prerequisite quests and level-10 eligibility, not a direct level-7 shortcut.

Read the [high-priority corrective queue](ROADMAP.md#high-priority-corrective-queue--2026-09-16)
before expanding homestead/GOAP work. Synthetic tests prove their simulated scope,
not real gameplay loops. The [mandatory reporting gate](AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16)
requires explicit fixture/bypass disclosure, evidence layer and what remains unproved.
Core reliability and independent human-playability work may continue.

**Correction note (2026-10-01).** Both dates in this section were re-verified against
the commits and are correct as written: the corrective queue is dated **2026-09-16**
and the Wave One closeout exit is dated **2026-09-18** (`ROADMAP.md:90` program header,
`ROADMAP.md:108` exit row, `ROADMAP.md:110` freeze note). The freeze is **lifted only
for the verified claim→construct loop**; further GOAP actions, combat behavior, route
content, homestead breadth (plant/harvest/tax), multi-bot scenarios, ordinary
acquisition, and `H` remain **frozen/open**. See the dispatch-source ruling at the
top of this file and in [`STATUS.md`](STATUS.md).

## 1. What This Repo Is

Open-source **ArcheAge 1.2** (`r208022`) server emulator in .NET 10 (`AAEmu.Login`, `AAEmu.Game`, shared `AAEmu.Commons`). 
* **Product Doctrine:** *The living world is the feature; PlayerBots are just the mechanism that gives it life.*
* **Current dispatch:** [outcome-first slice queue](ROADMAP.md#near-term-slice-queue), not the oldest open board item. One observable outcome per slice; no automatic milestone promotion.
* **Milestone Horizon:** M8 (Living Village) → M9 (Emergent world systems) → M10 (Territory & siege).
* **Topology:**
```text
Dev box (/root/aaemu-dev, origin = writable fork)
  ──push──►  GitHub fork develop (joshhmann/AAEmu — source of truth)
  ──fetch/merge──►  Testing server .165 (/root/AAEmu, runs docker compose)
Upstream AAEmu/AAEmu: intake-only. NEVER push a branch or open a PR there.
```

---

## 2. Non-Negotiables (Violate These and the PR is Rejected)

1. **Fork Boundary is Permanent:** Upstream push URL is `DISABLED`. Upstream updates arrive only on `sync/upstream-YYYY-MM-DD` branches, verified, and merged into the fork.
2. **`H` Means Josh Playing:** Bot and scripted evidence is `A` (automated), never `H`. `H` stays `UNKNOWN` until Josh verifies human feel in-game (`SCORECARD.md`). Never average grades; weakest required dimension wins.
3. **Evidence Before Claims:** Every Tier 1/2/3 report MUST carry: exact SHA, command, build/compiler result, unit total/pass/fail/skip counts, and MCP smoke status.
4. **`compact.sqlite3` is Read-Only Reference:** (Canonical md5 `78b3bdbf038db3b927056106efdf91af`, 679 tables). Mutable state goes to MySQL (`aaemu_game` / `aaemu_login`). Never `Prepare()` a parameterless MySQL statement (connector-net race bug).
5. **Config Precedence:** `Config.json` → `Configurations/*.json` → `Config.Local.json` (wins, gitignored). Never place machine-specific hosts/secrets in shared config.
6. **Match the Neighbors:** New code must mirror neighboring patterns. No second conventions. Use official game terminology (Doodad, Mate, Slave, Transfer, Expedition, Ability, ActAbility).

---

## 3. The Gateway: Universal Onboarding (Read by ALL Agents)

Every agent must read these current records before selecting a slice. Historical boards supplement them; they do not set current dispatch:

| Order | Document | Purpose |
| :---: | :--- | :--- |
| **0** | [`.omp/state/CURRENT.md`](.omp/state/CURRENT.md) | **Canonical task state:** objective, status, confirmed/unverified, next action. Contract in [`.omp/state/README.md`](.omp/state/README.md). |
| **1** | [`START-HERE.md`](START-HERE.md) | **This router:** Architecture, non-negotiables, and domain lane selection. |
| **2** | [`AGENTS.md`](AGENTS.md) | **Core rules:** Workflows, coding conventions, packet offset maps, and gate policies. |
| **3** | [`STATUS.md`](STATUS.md) | **Live pulse:** Current checkpoint, active human gates, and today's changes. |
| **4** | [`VISION.md`](VISION.md) & [`PROJECT-CONTROL.md`](PROJECT-CONTROL.md#delivery-contract) | **Outcome and operating contract:** human playability, living world, ownership and bounded slices. |
| **5** | [`SCORECARD.md`](SCORECARD.md) & [`ROADMAP.md`](ROADMAP.md#current-delivery-direction) | **Evidence and dispatch:** proven boundaries, current queue, dependencies and exits. |

---

## 4. Domain Lanes (Pick Your Lane)

Select the domain lane that matches your current assignment:

```mermaid
flowchart TD
    Hub["START-HERE.md"] --> Gateway["Universal Core (AGENTS, STATUS, ROADMAP)"]
    Gateway --> Lanes{"Select Assigned Lane"}
    
    Lanes --> LaneA["Lane A: Core AAEmu Engine & Mechanics"]
    Lanes --> LaneB["Lane B: Living World & PlayerBots"]
    Lanes --> LaneC["Lane C: Human Traces & Task Dashboard"]
    Lanes --> LaneD["Lane D: Reference Archaeology (MCP)"]
    Lanes --> LaneE["Lane E: Devops, Testing & Deployment"]
```

---

### Lane A: Core AAEmu Engine & Mechanics
* **Agent Role:** Core server engineer working on packets, combat math, NPC behaviors, doodad scripts, quests, housing, trade packs, netcode, or MySQL schemas.
* **Prerequisite Reading:**
  * Architecture: [`Docs/wiki/Components.md`](Docs/wiki/Components.md)
  * Conventions: [`Docs/wiki/Development-Conventions.md`](Docs/wiki/Development-Conventions.md)
  * Network/Offsets: [`AAEmu.Login/Docs/networking.md`](AAEmu.Login/Docs/networking.md), [`AAEmu.Game/Core/Packets/C2G/CSOffsets.cs`](AAEmu.Game/Core/Packets/C2G/CSOffsets.cs), [`SCOffsets.cs`](AAEmu.Game/Core/Packets/G2C/SCOffsets.cs)
* **Where Unfinished Work Lives:**
  * [`scorecard-explorations/zero-wired-domains.md`](scorecard-explorations/zero-wired-domains.md) — Unimplemented client packets and unwired opcodes.
  * [`scorecard-explorations/partial-domains.md`](scorecard-explorations/partial-domains.md) — Half-built mechanics and missing formulas.
* **Code Scope:**
  * Inbound/Outbound Packets: `AAEmu.Game/Core/Packets/{C2G,G2C}/`
  * Managers: `AAEmu.Game/Core/Managers/` (must register in `Program.cs`)
  * Netcode: `AAEmu.Game/Core/Network/`
  * Data Loaders: `AAEmu.Game/GameData/`
  * Domain Models: `AAEmu.Game/Models/Game/`
  * Script Runtime, Math & Converters: `AAEmu.Game/Utils/`
  * GM & Test Commands: `AAEmu.Game/Scripts/Commands/`, `AAEmu.Game/Scripts/SubCommands/` (carve-outs: `Commands/BotCmd.cs` + `SubCommands/Bots/` are Lane B; `Commands/PlayerTraceCmd.cs` + `SubCommands/PlayerTrace/` are Lane C; `SubCommands/Mapper/` feeds Lane B nav data)
  * World Physics & Weather: `AAEmu.Game/Physics/`, `AAEmu.Game/Weather/`
  * Client File IO: `AAEmu.Game/IO/`
  * Host Composition: `AAEmu.Game/GameService.cs`, `AAEmu.Game/Program.cs`
  * Login Service: `AAEmu.Login/Core/`, `AAEmu.Login/Utils/`
  * Schemas: `SQL/updates/` + base `SQL/aaemu_{login,game}.sql` (both required!)
  * Shared Primitives (cross-lane): `AAEmu.Commons/` — this project holds primitives shared by Login, Game and the gate/tooling lanes (packet primitives, MySQL helpers, the AAPak reader, Gate/soak metric types), so ownership of an individual Commons file follows the *consumer* subsystem, not its location. Listing it here is a default home, not an exclusive claim.
* **Verification Gate:**
  * `./scripts/gate.sh` (fast gate)

---

### Lane B: Living World & PlayerBots
* **Agent Role:** AI & simulation engineer working on autonomous bot decision trees, combat rotations, leveling loop, dormancy/restoration, scheduling, bot GM commands, and town presence.
* **Prerequisite Reading:**
  * Master Architecture: [`PLAYERBOT_TARGET_ARCHITECTURE_CONSOLIDATED_V1.md`](PLAYERBOT_TARGET_ARCHITECTURE_CONSOLIDATED_V1.md)
  * Framework: [`PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md`](PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md)
  * Vision: [`LIVING-WORLD.md`](LIVING-WORLD.md)
  * **Rule #9 & #10:** PlayerBots compose around normal `Character` records and standard gameplay services. NEVER create parallel character, inventory, or quest implementations!
* **Where Unfinished Work Lives:**
  * [`scorecard-explorations/playerbot-blockers.md`](scorecard-explorations/playerbot-blockers.md) — Active and historical architectural blockers. The ledger is **not** numbered PB-001…PB-006: it carries `PB-FARM-01`, `PB-005`, `PB-001`, `PB-002`, `PB-COMBAT`, `PB-BAG`, `PB-MOUNT`, `PB-SOAK` under OPEN, plus `PB-007`, `PB-006`, `PB-003`, `PB-004` and the found-and-fixed `PB-F1`…`PB-F4` under FIXED. **`PB-006` is used twice** — once for ship sailing physics and once for PlayerBot reboot persistence, both FIXED — so **cite a blocker by title, not by ID alone**.
  * [`scorecard-explorations/deferred-not-now.md`](scorecard-explorations/deferred-not-now.md) — Explicit backlog of parked features (Sections 12–18: Village errands, multi-objective quests, food/potion consumption, knockback recovery, mount riding, party assists, roster generation).
  * [`scorecard-explorations/mechanics/playerbot-capability-matrix.md`](scorecard-explorations/mechanics/playerbot-capability-matrix.md) — Full/Partial/Missing capability matrix.
* **Code Scope:**
  * Bot Brain & Loop: `AAEmu.Game/Core/Managers/Bots/` (`CombatDecisionTree`, `GameplayActor`, `DormantBotRegistry`, `LevelingLoopScenario`)
  * GM Commands: `AAEmu.Game/Scripts/Commands/BotCmd.cs`, `AAEmu.Game/Scripts/SubCommands/Bots/`
  * Unit Tests: `AAEmu.UnitTests/Game/Core/Managers/Bots/`, `AAEmu.UnitTests/BotControl/`
  * Bot Drive & Actuation Surface: `AAEmu.BotControl/`, `AAEmu.BotControlMcp/`, `AAEmu.Game/Services/WebApi/` (bot-driving controllers: `BotActionController`, `BotControlController`, `BotControlSettings`; the other WebApi controllers are Lane A)
  * Nav & Route Data Tooling: `AAEmu.Game/Scripts/SubCommands/Mapper/`, `Tools/Mapper/`, `AAEmu.Game/Data/Routes/`, `AAEmu.Game/Data/Path/`
  * Bot E2E Harness: `AAEmu.IntegrationTests/E2e/BotControl*.cs`, `AAEmu.IntegrationTests/E2e/BotDriveClient.cs`, `AAEmu.IntegrationTests/E2e/BotNetworkSession.cs`, `AAEmu.IntegrationTests/E2e/BotTcpLink.cs`
* **Verification Gate:**
  * `./scripts/gate.sh BotActionControllerRouteTests` (targeted)
  * `./scripts/gate.sh` (full suite)

---

### Lane C: Human Action Traces & Task Dashboard
* **Agent Role:** Tooling & telemetry engineer creating task specifications, capturing empirical ground truth, running trace coverage analysis, and maintaining the web dashboard on port 8085.
* **Prerequisite Reading:**
  * Specification & Evaluation Standard: [`Scripts/playertrace-coverage/TASK_SPEC_AND_EVALUATION_GUIDE.md`](Scripts/playertrace-coverage/TASK_SPEC_AND_EVALUATION_GUIDE.md)
  * Tooling Architecture: [`Scripts/playertrace-coverage/README.md`](Scripts/playertrace-coverage/README.md)
* **Where Unfinished Work Lives:**
  * [`playertrace-coverage/dashboard_tasks.json`](playertrace-coverage/dashboard_tasks.json) — Live catalog of 31 tasks; check tasks with `"status": "Pending"`.
  * Web Dashboard: Run `python3 Scripts/playertrace-coverage/dashboard_server.py --port 8085` and browse to `http://<ip>:8085`.
  * Roadmap & Scorecard tab maintenance contract (mandatory whenever roadmap/scorecard records change): [`Scripts/playertrace-coverage/DASHBOARD_MAINTENANCE.md`](Scripts/playertrace-coverage/DASHBOARD_MAINTENANCE.md)
* **Tools & In-Game Actions:**
  * In-Game: `/trace start <scenario>` → execute action → `/trace stop`
  * Trace Evaluator: `python3 Scripts/playertrace-coverage/task_evaluator.py --task-id <id>`
  * Coverage Analysis: `python3 Scripts/playertrace-coverage/playertrace_coverage.py traces/player-actions --out playertrace-coverage`
* **Code Scope:**
  * Trace Tooling Suite: `Scripts/playertrace-coverage/` (evaluator, coverage engine, trace parser, packet inventory, dashboard server + assets)
  * In-Game Trace Command: `AAEmu.Game/Scripts/Commands/PlayerTraceCmd.cs`, `AAEmu.Game/Scripts/SubCommands/PlayerTrace/`
  * Trace Emitter: `AAEmu.Game/Core/Managers/Bots/PlayerTraceService.cs` (emits the JSONL lines this lane analyses)
  * Trace Output & Dashboards: `traces/player-actions/`, `traces/bot-trajectories/`, `playertrace-coverage/` (generated `dashboard.html`, `summary.{md,json}`, `*.csv`)
  * Tests: `AAEmu.UnitTests/Game/Core/Managers/Bots/PlayerTraceStateTests.cs`, `AAEmu.UnitTests/Game/Core/Managers/Bots/PlayerTraceServiceTests.cs`
* **Verification Gate:**
  * `python3 Scripts/playertrace-coverage/test_coverage_mapper.py`

---

### Lane D: Reference Archaeology (Archaeology MCP)
* **Agent Role:** Reverse engineer or data analyst querying canonical ArcheAge 1.2 reference data (`compact.sqlite3`, 679 tables, md5 `78b3bdbf038db3b927056106efdf91af`) to corroborate formulas, item stats, NPC spawn templates, and client facts before writing code.
* **Prerequisite Reading:**
  * MCP Server README: [`AAEmu.ArchaeologyMcp/README.md`](AAEmu.ArchaeologyMcp/README.md)
  * Data Source Inventory: [`scorecard-explorations/mechanics/archaeology-data-source-inventory.md`](scorecard-explorations/mechanics/archaeology-data-source-inventory.md)
  * Acceptance Dossier: [`scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`](scorecard-explorations/mechanics/archaeology-mcp-acceptance.md)
* **Workflow:**
  1. Catalog first: `list_sources` / `list_tables` / `describe_table`
  2. Corroborate facts: `query_sql` / `lookup_row`
  3. Relationship trace: `trace_skill`, `trace_item`, `trace_quest`, `trace_npc`, `find_quest_objectives`
* **Code Scope:**
  * MCP Server: `AAEmu.ArchaeologyMcp/` (tool surface, SQL guard, source catalog, read-only `game_pak` reader)
  * Dossier & Data-Source Inventory: `scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`, `scorecard-explorations/mechanics/archaeology-data-source-inventory.md`
  * Read-Only Reference DB Patches: `SQL/patches/compact/` (defect/patch SQL derived from `compact.sqlite3` findings; these target MySQL, they are not DB writes to `compact.sqlite3`)
  * Unit Tests: `AAEmu.UnitTests/ArchaeologyMcp/`
  * Read-Only Reference Probes (outside the MCP, but the same discipline — read `compact.sqlite3`/`game_pak` only, write nothing back): `Tools/SurveyGridBaker/`, `Tools/NavGCostProbe/`, `Tools/sit-pose-census/`. Other `Tools/*` entries are build/convert tools that write output, not archaeology probes.
* **Verification Gate:**
  * `./scripts/archaeology-cycle.sh` (MCP build + 156 archaeology tests + 24-tool smoke)

---

### Lane E: Devops, Testing & Deployment
* **Agent Role:** Systems engineer configuring developer environments, running .NET Aspire or host MySQL, running heavy integration/soak gates, and staging deployments to the `.165` test server.
* **Prerequisite Reading:**
  * Guided Setup: [`.agents/skills/aaemu-setup/SKILL.md`](.agents/skills/aaemu-setup/SKILL.md)
  * Aspire Guide: [`Docs/wiki/Aspire-Development-Guide.md`](Docs/wiki/Aspire-Development-Guide.md)
  * Deployment Playbook: [`WORKFLOW.md`](WORKFLOW.md)
  * Human Gate Field Guide: [`Docs/wiki/Human-Gate-Field-Guide.md`](Docs/wiki/Human-Gate-Field-Guide.md)
  * Soak Test Manual: [`Docs/wiki/Soak-Runs.md`](Docs/wiki/Soak-Runs.md)
* **Code Scope:**
  * Gate & Cycle Runners: `scripts/` (`gate.sh`, `archaeology-cycle.sh`, `run-a5-sixhour-soak.sh`, `aaemu-prod-intake`)
  * Host Orchestration & Install Scripts: `AAEmu.Aspire.AppHost/`, `Scripts/` (start/publish/watch, `docker-install-local.*`, `docker-update-local.*`, `e2e/`), `.agents/skills/aaemu-setup/scripts/`
  * Container & CI: `docker-compose.yaml`, `docker-compose.presence.yaml`, `Scripts/e2e/docker-compose.yaml`, `.github/workflows/`
  * Test Suites: `AAEmu.UnitTests/`, `AAEmu.IntegrationTests/` (incl. soak/gate harness `AAEmu.IntegrationTests/E2e/Gate/`)
  * Gate & Soak Metric Types: `AAEmu.Commons/Utils/Gate/` (shared with the gate harness, but consumed by Lane A/B as well — see the Lane A shared-primitives note)
  * Deployment Records: `deployments/`, `soak-artifacts/`, `WORKFLOW.md`
  * Other Repo Tools: `Tools/`, `tools/` (except the read-only archaeology probes listed under Lane D)
* **Verification Gates:**
  * Fast Gate: `./scripts/gate.sh` (~1 min)
  * Targeted E2E: `dotnet test AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --treenode-filter "..."`
  * Heavy Soak / Exit Gate: Unfiltered `AAEmu.IntegrationTests` (35–45+ min)

---

## 5. Summary Index of Tracking & Planning Documents

| Category | File | Description |
| :--- | :--- | :--- |
| **Rules & Workflow** | [`AGENTS.md`](AGENTS.md) | Universal agent rules, coding style, git boundaries. |
| **Rules & Workflow** | [`WORKFLOW.md`](WORKFLOW.md) | Branching, commits, deployment, rollback recipes. |
| **Roadmap & Progress** | [`ROADMAP.md`](ROADMAP.md) | M1 through M10 macro requirements and exit criteria. |
| **Roadmap & Progress** | [`ROADMAP_SCORECARD_SUMMARY.md`](ROADMAP.md) | see ROADMAP.md / SCORECARD.md for milestone completion state. |
| **Roadmap & Progress** | [`scorecard-explorations/progression-board.md`](scorecard-explorations/progression-board.md) | Historical execution board; current dispatch is ROADMAP.md. |
| **Backlog & Future Tasks**| [`scorecard-explorations/deferred-not-now.md`](scorecard-explorations/deferred-not-now.md) | Parked features & revisit triggers (Sections 1–18). |
| **Backlog & Future Tasks**| [`playertrace-coverage/dashboard_tasks.json`](playertrace-coverage/dashboard_tasks.json) | 31 concrete in-game tasks with checklists and packet specs. |
| **Gaps & Defects** | [`scorecard-explorations/playerbot-blockers.md`](scorecard-explorations/playerbot-blockers.md) | Architectural blockers — `PB-FARM-01`, `PB-001`…`PB-005`, `PB-COMBAT`/`PB-BAG`/`PB-MOUNT`/`PB-SOAK`, plus fixed `PB-F1`…`PB-F4` and a **duplicated `PB-006`**; cite by title. Corrected 2026-10-01 — this entry previously read "PB-001 through PB-006". |
| **Gaps & Defects** | [`scorecard-explorations/zero-wired-domains.md`](scorecard-explorations/zero-wired-domains.md) | Catalog of unimplemented wire packets and missing systems. |
| **World & Reference** | [`Docs/wiki/World-Continents-Races-and-Mirage-Isle.md`](Docs/wiki/World-Continents-Races-and-Mirage-Isle.md) | World zones, races, NPC distributions, islands & Mirage Isle architecture. |
| **Vision & Concepts** | [`LIVING-WORLD.md`](LIVING-WORLD.md) | Blueprint for living bot societies, trade, and economy. |
| **Vision & Concepts** | [`PROJECT-CONTROL.md`](PROJECT-CONTROL.md) / [`VISION.md`](VISION.md) | Target user experience, core pillars, and project scope. |
