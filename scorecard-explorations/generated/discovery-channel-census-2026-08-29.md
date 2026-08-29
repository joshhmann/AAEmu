# Discovery Channel Census — 2026-08-29

- **Date:** 2026-08-29
- **HEAD:** `14388b7021091d7796ff6f2ed63f62b0309820a1` (branch `develop`)
- **Data source:** `AAEmu.Game/Data/compact.sqlite3` — READ-ONLY (query-only, no writes)
- **Method:** `quest_components` (kind_id=2 = Start) ⋈ `quest_acts` ⋈ act-detail tables; distinct quests via `quest_context_id`
- **Related code:** `GameplayActor.DiscoverQuests` / `DiscoverSelfQuests`, `QuestManager.GetQuestsOfferedBy*`, `SphereQuestManager.GetQuestStartingSpheres`

## 1. Totals

| Metric | Count |
|---|---|
| `quest_contexts` (total quests) | **4,876** |
| Start components (`component_kind_id=2`) | 4,697 |
| Quests with ≥1 start accept act | 4,296 |
| Quests with **2** distinct accept channels | 77 |
| Quests with ≥3 distinct accept channels | 0 |
| Quests with no start accept act at all | 580 (23 covered by legacy accept tables: `item_accept_quests` 4, `doodad_func_quests` 3, `sphere_quests` 30, `accept_quest_effects` 0 — union 23) |

## 2. Channel distribution (START components, kind_id=2)

| Channel (act detail type) | Start acts | Distinct quests |
|---|---|---|
| `QuestActConAcceptNpc` | 3,078 | 2,797 |
| `QuestActConAcceptDoodad` | 203 | 203 |
| `QuestActConAcceptItem` | 342 | 342 |
| `QuestActConAcceptItemGain` | 72 | 25 |
| `QuestActConAcceptSphere` | 455 | 431 |
| `QuestActConAcceptLevelUp` | 3 | 3 |
| `QuestActConAcceptComponent` | 191 | 191 |
| `QuestActConAcceptNpcKill` | 1,055 | 381 |
| `QuestActConAcceptBuff` | 0 | 0 |
| `QuestActConAcceptItemEquip` | 0 | 0 |
| `QuestActConAcceptNpcEmotion` | 0 | 0 |
| `QuestActConAcceptSkill` | 0 | 0 |

The last four have act rows but **zero** land on a start component — all 7 rows (acts 20911/20919/20922/20923/21054/21061/21067) are dangling (no `quest_components` parent). Not a channel.

## 3. Wired vs unwired (as `GameplayActor` perceives)

### Wired — exact methods

| Channel | Method | Entry point |
|---|---|---|
| NPC | `QuestManager.GetQuestsOfferedByNpc` (`QuestActConAcceptNpc`) | `DiscoverQuests` (world target branch) |
| Doodad | `QuestManager.GetQuestsOfferedByDoodad` (`QuestActConAcceptDoodad`) | `DiscoverQuests` (world target branch) |
| Item | `QuestManager.GetQuestsOfferedByItem` (`QuestActConAcceptItem`) + `GetQuestsOfferedByItemGain` (`QuestActConAcceptItemGain`, Count-gated via `MeetsItemGainCounts`) | `DiscoverSelfQuests` step 1 (inventory bag) |
| Sphere | `SphereQuestManager.GetQuestStartingSpheres` (starters carrying `QuestActConAcceptSphere`; position-in-sphere + unit_req gate) | `DiscoverSelfQuests` step 2 |
| Level | `QuestManager.GetQuestsOfferedByLevel` (`QuestActConAcceptLevelUp`) | `DiscoverSelfQuests` step 3 |

`LevelingLoopScenario` calls `DiscoverQuests` per perceived NPC/doodad + `DiscoverSelfQuests()` once per sweep (wired in **970d6a557**).

Wired reachable quests (distinct, all channels, zero mutual overlap):

| Wired set | Distinct quests |
|---|---|
| World (NPC ∪ Doodad) | 3,000 |
| Self (Item ∪ ItemGain ∪ Sphere ∪ LevelUp) | **801** (342+25+431+3 — no overlap between these channels) |
| **All wired union** | **3,801** (3,000 ∩ 801 = ∅) |

### Unwired — 495 quests with zero wired accept surface

| Unwired channel | Distinct quests | Also wired | Unwired-only |
|---|---|---|---|
| `QuestActConAcceptNpcKill` | 381 | 1 (quest 3845) | **380** |
| `QuestActConAcceptComponent` | 191 | 76 (NPC/Doodad/ItemGain pairs, e.g. firewood chain 1955–1961/2140–2146, 5732–5756) | **115** |
| Buff/ItemEquip/NpcEmotion/Skill | 0 | — | 0 |
| **Remainder (unwired-only)** | | | **495** |

Notes:
- **NpcKill** is *engine-live*: `DoOnMonsterHuntEvents` auto-starts these with `QuestAcceptorType.Kill` via `BuildKillAcceptQuestIndex` / `GetQuestIdsFromKillAcceptNpc` (QuestManager.cs:411-446). The bot never surfaces them as offers — it has no kill-acceptor perception channel.
- **Component** is *deliberately deferred* (code comment): `QuestActConAcceptComponent.RunAct` is a stub returning true — no player-observable precondition, self-referencing starter pattern (mostly "help kill" chains keyed off `npc.EngageCombatGiveQuestId`).
- The `~900 unreachable` figure in `quest-discovery-sweep-2026-08-25.md` (§"Offer channels outside the v1 discovery surface") was **992** at that time (431+342+191+25+3) and **omitted NpcKill (381)** entirely. Post-`970d6a557`, 801 of those 992 are wired, leaving 191 (Component) — plus the never-listed 380 kill-only = **495 actual remainder**, not ~900.

## 4. Delta vs prior claims

- **"~801 now perceivable via Item (342+25), Sphere (431), Level (3)"** → **exact match, zero delta** at HEAD: 342 `AcceptItem` + 25 `AcceptItemGain` + 431 `AcceptSphere` + 3 `AcceptLevelUp` = 801 distinct quests, no channel overlap. The claim already reflects HEAD (self-discovery landed in `970d6a557`).
- **"~900 channel offers remain unreachable"** → stale **and** under-counted:
  - Computed from the pre-self-channel sweep (992 counted: 431+342+191+25+3; written as "~900+").
  - `970d6a557` (wire `DiscoverSelfQuests` into `LevelingLoopScenario`) converted 801 of those 992 into reachable offers → Component (191) is the residue of the original list.
  - The sweep **never enumerated `QuestActConAcceptNpcKill`** (381 quests, 380 unwired-only) — the true HEAD remainder is **495** (380 kill + 115 component), **not ~900**.
  - 77 quests carry both a wired and an unwired channel; they are already perceivable.

1. **~~`QuestActConAcceptNpcKill` — wire next (380 unwired-only quests, lowest gate complexity).~~ DONE 2026-08-29**
   Kill-offer perception landed at HEAD (uncommitted, working tree): `GameplayActor.DiscoverQuests` now surfaces quests whose Start component carries a `QuestActConAcceptNpcKill` act for the perceived NPC's template, with acceptor `QuestAcceptorType.Kill` (the exact triple `DoOnMonsterHuntEvents`' auto-start uses). No `QuestManager` change was needed — `BuildKillAcceptQuestIndex` / `GetQuestIdsFromKillAcceptNpc` already existed; no accept-path extension was required (`CharacterQuests.AddQuest` accepts `QuestAcceptorType.Kill` without special-casing). `LevelingLoopScenario` needed no edit: its `Perceive` sweep already merges every `DiscoverQuests` offering, so kill offers flow into the loop automatically (the 970d6a557 self-discovery merge shape). Verified by `LevelingLoopScenarioRigTests` — canonical quest 1947 (kill-accept + hunt NPC 4843 ×8, AutoComplete reward, gate Level ≥ 8, 1110 exp supply) discovered → accepted (`Kill/4843`) → hunted → auto-completed; fail-closed control (perceived NPC 7669 with ZERO accept acts of any kind) starves with nothing accepted. 16/16 rig tests pass, build clean.
2. **`QuestActConAcceptComponent` — defer (115 component-only quests, high gate complexity, low perceivability).**
   191 total, 76 already reachable via paired wired channels. `RunAct` is a stub returning true; true trigger is `npc.EngageCombatGiveQuestId` (aggro/combat perception, not an offer channel). Wiring requires an aggro-perception primitive for real value; until then it is correctly deferred. The 1960/2145 gated-chain orphans plus 1959→(dead 1958) make part of this set unreachable *by data* regardless (see `data-defects.md`).
3. **No further channels exist.** Buff / ItemEquip / NpcEmotion / Skill accept types have zero start acts (7 dangling rows only). The next frontier after kill-accept is not a channel but objective-side reachability (spawn-join sweep on `quest_act_obj_*` targets), which is a different axis.
