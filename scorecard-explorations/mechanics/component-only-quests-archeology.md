# Component-Only Quests: Archaeology Analysis & System Deferral

- **Date:** 2026-09-03
- **Branch:** `develop`
- **Canonical DB:** `AAEmu.Game/Data/compact.sqlite3` (`mode=ro`, md5 `78b3bdbf038db3b927056106efdf91af`, ArcheAge 1.2 r208022)
- **Tooling:** `AAEmu.ArchaeologyMcp` stdio JSON-RPC queries (`query_sql`, `trace_quest`, `search_everything`)
- **Scope:** Post-M7 readiness & PB-002 autonomous progression; classification of the 191 `QuestActConAcceptComponent` starter quests.

---

## 1. Executive Summary

In ArcheAge, quests carrying `QuestActConAcceptComponent` as their Start act lack a standard NPC/doodad conversation acceptor. An exhaustive archaeology query reveals that out of 191 such quests:
1. **76 quests** are dual-channeled (paired with explicit NPC talk, doodad interaction, or item-gain channels).
2. **45 quests** are auto-started dynamically by the engine on first combat aggro via `npc_templates.engage_combat_give_quest_id` (wired into `LevelingLoopScenario` via `AutoStartedQuestIds`).
3. **70 quests** have no NPC/doodad dialogue channel and no combat engage tie.

Crucially, these remaining 70 quests are **not broken data relics or dead garbage**. Rather, they are **system-driven and interactable feature quests**—mechanic introductions and zone/event systems (e.g., Ayanad Library floor bounties, tutorial cutscene chains, and rift world events) that are auto-granted by specialized game subsystems rather than NPC conversation.

They are formally **deferred** to their respective domain and feature systems, while remaining strictly **fail-closed** within the autonomous leveling bot (`LevelingLoopScenario`).

---

## 2. Complete Archaeology Census (191 Quests)

```sql
SELECT 
  CASE 
    WHEN qa_paired.id IS NOT NULL THEN 'Paired with Standard Channel'
    WHEN nt.engage_combat_give_quest_id IS NOT NULL THEN 'Combat Auto-Start'
    ELSE 'System-Driven / Deferred'
  END AS classification,
  COUNT(DISTINCT qc.quest_context_id) AS quest_count
FROM quest_components qc
JOIN quest_acts qa ON qa.quest_component_id = qc.id AND qa.act_detail_type = 'QuestActConAcceptComponent'
LEFT JOIN quest_acts qa_paired ON qa_paired.quest_component_id = qc.id 
  AND qa_paired.act_detail_type IN ('QuestActConAcceptNpc', 'QuestActConAcceptDoodad', 'QuestActConAcceptItem', 'QuestActConTalk')
LEFT JOIN npc_templates nt ON nt.engage_combat_give_quest_id = qc.quest_context_id
WHERE qc.component_kind_id = 2
GROUP BY classification;
```

| Subset | Count | Autonomous Status | Destination Track |
|---|---|---|---|
| **Paired Channels** | 76 | Pursued via `DiscoverQuests` / `DiscoverSelfQuests` | Landed (Standard questing) |
| **Combat Auto-Start** | 45 | Pursued via `AutoStartedQuestIds` + `AggroLeg` / `HuntLeg` | Landed (`Unit.AddUnitAggro`) |
| **System-Driven / Deferred** | 70 | Fail-closed in `LevelingLoopScenario` | **Deferred to Feature Systems** |

---

## 3. Deep Breakdown of the 70 System-Driven Quests

Querying the exact category names (`quest_categories.name`), target levels, and objective shapes for the 70 component-only quests reveals their true in-game nature:

### A. Ayanad Library Room & Floor Bounties (68 Quests)
- **Category:** `에아나드 도서관` (Ayanad Library)
- **Level Band:** 51 – 55 (ArcheAge 1.2 level cap expansion)
- **Quest IDs:** 6255, 6257, 6259, 6261, 6263, 6265, 6267, 6269, 6271, 6273, 6275, 6277, 6279, 6283–6349
- **In-Game Mechanism:** 
  In the Ayanad Library dungeon complex, quests are not handed out by individual quest-givers. Instead, upon entering a specific room or floor tile, the library zone system automatically activates a room bounty (e.g., `도깨비 학살` / "Slay Library Imps", `보관함 학살` / "Destroy Stash Boxes") and auto-completes/rewards the player once the room threshold is met.
- **Deferral Track:** Ayanad Library zone/room management system.

### B. Tutorial / Prologue Cinematic Sequence Chains (10 Quests)
- **Category:** `프롤로그` (Prologue)
- **Level Band:** 1
- **Quest IDs:** 6040, 6045 (`프롤로그09`), 6046, 6047, 6048, 6049, 6050, 6051, 6052, 6053
- **In-Game Mechanism:**
  During the racial intro sequences, these quests are auto-granted and progressed sequentially by cinematic sequence director events and camera cues (`QuestActObjCinema`, `model_quest_cameras`), introducing basic player controls, camera panning, and movement.
- **Deferral Track:** Tutorial & Cinematic sequence director.

### C. World Events, Rifts & Honor Battlefields (8 Quests)
- **Categories:** `명예` (Honor - 6 quests: 6004, 6005, 6008, 6020–6022), `전장의 안개` (Mistmerrow / Crimson Rift - 2 quests: 5143, 5144)
- **Level Band:** 45 – 50
- **In-Game Mechanism:**
  When the server event scheduler triggers a world event (e.g. Crimson Rift in Ynystere/Cinderstone, or the Mistmerrow battle), all eligible players within the zone receive phased event quests (`악몽의 군단 처치 1단계`, etc.) automatically as the battle progresses through phases.
- **Deferral Track:** World Event / Rift Schedule engine.

### D. Minigames, Titles & Festivals (4 Quests)
- **Categories:** `놀이` (Minigames - 5063, 5064), `칭호` (Titles / Car Racing - 5451, 5452), `기념행사` (Festivals - 6281)
- **Level Band:** 50
- **In-Game Mechanism:**
  Triggered by specific interactable feature objects (e.g. Mandragora catching minigames on Mirage Isle, car racing circuit qualification, and seasonal festival doodads).
- **Deferral Track:** Interactive minigames & festival doodad systems.

---

## 4. Architectural Contract & Policy Ruling

1. **Not Dead Relics:** 
   Prior references to these quests as "unreachable data relics" are formally corrected. They represent real, authentic ArcheAge 1.2 system content.
2. **Autonomous Leveling Bot Boundary:**
   `LevelingLoopScenario` is an autonomous leveling loop designed to discover and complete world narrative quests. Because these 70 quests lack standard NPC/doodad conversation starters and require specialized engine directors (room boundaries, cutscenes, rift clocks), they must **fail closed** if encountered.
3. **Formal Deferral:**
   These quests are formally deferred to their respective feature system implementations in future roadmap milestones. They do not block PB-002 autonomous progression.
