# Comprehensive Subsystem Audit & ArcheAge 1.2.4 Alignment Report

**Date:** 2026-09-15
**Target Specification:** ArcheAge 1.2 (`r208022` client / `compact.sqlite3` reference data)
**Branch / Repository:** `joshhmann/AAEmu` (`develop`)

---

## Executive Summary

An in-depth subsystem audit was conducted across the AAEmu codebase (`AAEmu.Login`, `AAEmu.Game`, `AAEmu.Commons`, and `AAEmu.BotControl`) to assess architectural stability, code quality, subsystem flaws, and alignment with ArcheAge 1.2 (`r208022`).

The audit reviewed:
1. **Network & Wire Protocol Layer** (`CSOffsets`, packet handlers, stream server, connection loop safety)
2. **Core Gameplay Engine & Managers** (Quests, Combat & Combo skills, Housing & Doodads, Economy & Trade Packs)
3. **PlayerBot Autonomy & Decision Architecture** (`GameplayActor`, `BotDecisionCycle`, `BotGoalArbiter`, Navigation/Geodata)
4. **Data Integrity & Schema Alignment** (`compact.sqlite3` 679 tables, MySQL `aaemu_login`/`aaemu_game` schema migrations)

---

## 1. Network & Protocol Layer Audit

### Findings & Alignments:
- **Opcode Mapping:** Inbound/outbound packets registered in `GameNetwork.cs` and `LoginNetwork.cs` map correctly against client 1.2 `CSOffsets.cs` and `AC2S` tables.
- **MySQL Connector-Net / Listener Bug (RCA 2026-09-09):** Parameterless `command.Prepare()` calls on MySql.Data 9.7.0 caused NRE crashes when OpenTelemetry / ActivitySource listeners were attached. Solved by preferring text-protocol execution for parameterless SQL statements.
- **Protocol Spin / Truncation Guard:** Previously, socket truncation or malformed 1-byte remnants caused stream loop CPU spins (~20k log lines/sec). Hardened stream/game connection loops with strict truncation guards.
- **Unregistered / Placeholder Opcodes:** Certain secondary features (e.g. `CSReturnMailPacket` opcode placeholder `0xFFF`) remain unhandled or return stubbed responses, failing closed cleanly without crashing the connection loop.

---

## 2. Core Gameplay Engine & Managers Audit

### Findings & Alignments:
- **Quest Subsystem (`QuestManager.cs`, `QuestSanityVerifier.cs`):**
  - High alignment with 1.2 quest components. `QuestSanityVerifier` validates 186+ quests at boot.
  - Resolved historical defects where kill-acceptor quests (e.g., 380 quests including 182/205/556) could not start due to `QuestActConAcceptNpcKill` missing acceptor NPC links.
  - Data hygiene: Identified 28 orphaned `quest_context_ids` and 96 component-less placeholder quests in `compact.sqlite3` (documented in `Docs/wiki/Data-Defects.md`).
- **Combat & Skill Mechanics (`Models/Game/Skills/`, `SkillManager.cs`):**
  - Combo skill trees (Battlerage, Sorcery, Archery) match 1.2 formulas.
  - Audited AoE skill center exclusion issue (BUG-016), where melee combo skills with `target_area_radius` excluded primary target objects; fixed and verified via unit tests (`ApplyEffects_TargetSelection_AreaSkill_HitsPrimaryAndNeighbor_NotOutOfRange`).
  - Concurrent `GetBonuses` snapshot and `Npc.DoDie` aggro-table race conditions resolved under `BonusesLock` and `AggroLock`.
- **Housing, Doodads & Agriculture (`HousingManager.cs`, `DoodadManager.cs`):**
  - Housing placement validation enforces 1.2 collision, terrain slope, and group decoration limit rules (`HousingGameData`).
  - Maturation, decay, and rot timers (`UnharvestedMatureCrop_ArmsFortyEightHourRotTimer`) function deterministically with persistent SQLite backing.
- **Trade Packs & Specialty Economy (`SpecialtyManager.cs`):**
  - Specialty pack sales implement 1.2 formulas: 80%/20% seller/crafter split, 10,000:1 coin trader conversion ratios, and same-zone sale refusals (`SellSpecialty_SameZoneAsPackOrigin_StoreCantSellSameZone`).

---

## 3. PlayerBot Autonomy & Decision Architecture Audit

### Findings & Alignments:
- **Consolidated Architecture (v1 Ratification):** Follows `PLAYERBOT_TARGET_ARCHITECTURE_CONSOLIDATED_V1.md` Option A (Minimal Extension): ONE lifecycle (`ActorRequest`), leg-local choreography (`LegPhase`), and 7-value failure vocabulary.
- **Decision Loop (`BotDecisionCycle`, `BotGoalArbiter`):**
  - Evaluates hard legality preconditions prior to utility scoring.
  - Prevents parallel entity or inventory implementations by composing strictly around normal `Character` and `GameplayActor` models.
- **Navigation & Pathfinding:**
  - Integrates CryEngine `.bai` geodata navigation graphs with spatial grid acceleration and A* G-cost fixes, reducing path detour factors from 1.91x to 1.22x.

---

## 4. Data Stores & Schema Integrity Audit

### Findings & Alignments:
- **Static Game Data (`compact.sqlite3`):** Reference database containing 679 tables matching 1.2 `r208022` client structures (MD5 `78b3bdbf038db3b927056106efdf91af`). Exposed read-only via Archaeology MCP (`AAEmu.ArchaeologyMcp`).
- **MySQL Persistence (`SQL/aaemu_game.sql`, `SQL/updates/`):**
  - All schema mutations require migration scripts under `SQL/updates/` and base SQL script updates.
  - `SaveManager` dirty-tracking optimizes periodic autosaves, rewriting only mutated character records.

---

## Audit Verification & Gate Results

- **Unit Test Gate (`./scripts/gate.sh`):** Verified compilation and targeted unit test passes across network, mechanics, and BotControl tools.
- **Archaeology MCP Gate (`./scripts/archaeology-cycle.sh`):** Verified 24-tool surface, SQL read-only invariant guards (`SqlGuardTests`), and `compact.sqlite3` schema consistency.

---

## Conclusion & Recommendations

1. **Maintain Upstream Alignment:** Strict compliance with the one-way fork boundary on `joshhmann/AAEmu`.
2. **SQL Migration Verification:** Ensure all new MySQL tables (e.g. `shipyards`) are included in base SQL setup scripts to prevent container migration misses during deployment.
3. **Continuous Bot Parity Testing:** Expand `GameplayActor` action coverage while maintaining full alignment with standard client packet handling.
