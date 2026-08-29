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

## 5. Follow-up 2026-08-29 — Component channel: engine auto-start VERIFIED, reachable subset implemented

### Engine auto-start path — VERIFIED (headless-reachable)

`npc.EngageCombatGiveQuestId` is a REAL engine trigger, not a stub:

- `Unit.AddUnitAggro` first-aggro block (`AAEmu.Game/Models/Game/Units/Unit.cs:1701-1705`): on the FIRST aggro-table entry for a unit, `if (npc.Template.EngageCombatGiveQuestId > 0 && player is not null)` → `if (!player.Quests.IsQuestComplete(id) && !player.Quests.HasQuest(id)) player.Quests.AddQuestFromNpc(id, npc.ObjId)`.
- `CharacterQuests.AddQuestFromNpc` (`AAEmu.Game/Models/Game/Char/CharacterQuests.cs:190-200`) → `AddQuest(questId, false, QuestAcceptorType.Npc, npc.TemplateId)` — the quest starts with the **Npc acceptor triple** (type Npc + template id), exactly like a talk-offer accept.
- `QuestActConAcceptComponent.RunAct` (`AAEmu.Game/Models/Game/Quests/Acts/QuestActConAcceptComponent.cs:17-21`) is a stub returning true — the Start step passes, so the quest proceeds to Progress. The real gate is the `unit_reqs` on the Start component (`UnitRequirementsGameData.CanComponentRun`), which is real data.
- Headless-reachable: `npc.AddUnitAggro(AggroKind.Damage, character, 1)` on a region-joined fixture NPC with `Template.EngageCombatGiveQuestId` set auto-starts the quest through the real path (proven by `LevelingLoopScenarioRigTests.EngageCombat_AutoStart_StartsComponentOnlyQuestWithNpcAcceptor`).

### Reachable subset implemented

The 115 component-only quests split by Progress act type:

- **MonsterHunt / MonsterGroupHunt progress — fully completable** (the reachable subset): engage → auto-start → kill credit via `QuestManager.DoOnMonsterHuntEvents` (`AAEmu.Game/Core/Managers/QuestManagerEvents.cs:169-218`, fires `OnMonsterHunt` → `QuestActObjMonsterHunt.OnMonsterHunt` → `AddObjective`) → auto-complete. Canonical quest **6109** (입관심사원 윈 처치): engage NPC **14364** (level 52), Start = `QuestActConAcceptComponent` only (0 AcceptNpc / 0 kill-accept acts — pure component channel), Progress = MonsterHunt npc 14364 ×1, Reward = SupplyExp 125400 + AutoComplete; start gate unit_reqs Level ≥ 50. Alternates: 6133 (NPC 14360, Level ≥ 51), 6157 (NPC 14330, Level ≥ 53), 4661 (NPC 11936, Level ≥ 45), 6238-6242 (NPCs 14615-14619, Level ≥ 50, repeatable).
- `LevelingLoopScenario` now surfaces auto-started quests as a fourth perception channel (`PerceptionSnapshot.AutoStartedQuestIds` — active quests not already offered) and, when no band offering exists, pursues + turns them in **without an explicit accept dispatch** (the quest is already active; a note records the auto-start). No synthetic AcceptQuest record is ever emitted.

### Why-not (aggro-objective subset) — documented, NOT faked

The majority of the 115 (1408, 1443, 2432, 2979, 3349, 3400, 3477, 3564, 3583, 3694, 3927, 3930, 4321, 4325, 4326, 4385, 4863, 4944, 5033, 5277, 5969, 5970, 5971, …) carry **QuestActObjAggro** progress — these are **engine-broken and cannot complete**:

- `QuestActObjAggro.OnKill` (`AAEmu.Game/Models/Game/Quests/Acts/QuestActObjAggro.cs:66`) requires `e.Target is Npc npc && npc.TemplateId == q.AcceptorId` — the kill event's Target must be the slain NPC.
- The engine's OnKill raises **never set Target to the victim**: `Unit.cs:440` `attackerUnit.Events.OnKill(attackerUnit, new OnKillArgs { Target = attackerUnit })` (Target = the attacker) and `Unit.cs:491` `killerUnit?.Events.OnKill(this, new OnKillArgs { Killer = killerUnit, Victim = this })` (Target null).
- Consequence: the aggro objective can never credit, so those quests are stuck at Progress forever. Completion is not faked; the honest verdict is that the aggro subset is unreachable until the OnKill event carries the victim as Target (an engine fix outside this task's scope).

### Fail-closed control

`LevelingLoopScenarioRigTests.EngageCombat_NoEngageQuestId_FailsClosedStartsNothing` proves an NPC with no `EngageCombatGiveQuestId` (7669) starts NOTHING on first aggro — the auto-start gate is the template field, and the loop never invents a quest the engine starts none.
