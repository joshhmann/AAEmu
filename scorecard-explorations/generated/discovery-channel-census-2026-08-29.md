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

## 2026-08-30 re-census at 9b8ba6317

- **Date:** 2026-08-30
- **HEAD:** `9b8ba63175b459c2073cb7c742637f07bbb3b9e1` (branch `develop`)
- **Data source:** `AAEmu.Game/Data/compact.sqlite3` — READ-ONLY (`mode=ro` URI, SELECT-only; md5
  `78b3bdbf038db3b927056106efdf91af`, same canonical DB as 2026-08-29 — the data did not move, only the code)
- **Method:** re-ran the §2 channel SQL (per-channel shape in `spawn-reachability-census-2026-08-29.md` §1);
  all counts are byte-identical to 2026-08-29 (data unchanged): Npc 2,797 · Doodad 203 · Item 342 ·
  ItemGain 25 · Sphere 431 · LevelUp 3 · NpcKill 381 · Component 191; wired 8-channel union **4,181**.
  The deltas below are **code deltas only** — three commits landed after this census was written.

### Code commits since the 2026-08-29 census

1. **`3827b5170` — kill-accept perception wired into `DiscoverQuests`.** The §3 "uncommitted, working tree"
   caveat is resolved: `GameplayActor.DiscoverQuests` now merges
   `QuestManager.GetQuestIdsFromKillAcceptNpc(acceptorTemplateId)` into the offering set with acceptor
   `QuestAcceptorType.Kill` (enum value 7). Prior claim "the bot never surfaces them as offers" is **stale at HEAD**.
2. **`7d0b80041` — `LevelingLoopScenario` pursues `PerceptionSnapshot.AutoStartedQuestIds`.** Engine
   auto-started quests (already ACTIVE, no discoverable offer) are surfaced as a fourth perception channel and
   pursued + turned in without an accept dispatch (rig test
   `LevelingLoop_EngageCombatAutoStart_CompletesComponentOnlyQuest6109` — canonical quest 6109 end-to-end).
3. **`a1653d67d` — `Unit.DoDie` kill event now carries the victim as `OnKillArgs.Target`**
   (Unit.cs:491). The §5 "engine-broken" verdict for aggro objectives is **obsolete at HEAD**:
   `QuestActObjAggro.OnKill`'s `e.Target is Npc npc && npc.TemplateId == q.AcceptorId` gate can now fire.
   Regression coverage: `QuestActObjAggroTests.NpcDoDie_EmitsVictimTarget_AndCreditsAggroObjective` /
   `OnKill_WithAttackerOrWrongNpcTarget_DoesNotCreditAggroObjective` (2/2).
4. (companion, not in the original task list) **`f5331ced7` — `AggroLeg`** pursues NPC-acceptor aggro forms
   through real kill credit; component forms with `AcceptorId == 0` remain fail-closed.

### Component-only 115 — re-bucketed at HEAD (auto-start engagement + aggro fix)

The 115 component-only quests (unchanged set: `QuestActConAcceptComponent` Start, no wired channel) split by
(a) their engage NPC carries `EngageCombatGiveQuestId` = the quest, and (b) Progress carries
`QuestActObjAggro`:

| Bucket | Quests | Defect? |
|---|---|---|
| **(c) both** — auto-startable AND aggro-progress | **30** | none at HEAD — `a1653d67d` unblocked the aggro objective |
| **(a) engage-only** — auto-startable, MonsterHunt/plain progress | **15** | none — 9 MonsterHunt, 6 no-progress (reward-only) |
| **(b) aggro-only** — aggro progress, no engage NPC | **0** | n/a (empty) |
| **(d) neither** — no engage-NPC tie, no aggro act | **70** | **genuinely unreachable by perception** — see below |

Reproduce (exact SQL):

```sql
WITH comp_only AS (  -- 115 component-only quests (same shape as the 2026-08-29 census)
  SELECT DISTINCT qc.quest_context_id AS qid
  FROM quest_components qc
  JOIN quest_acts qa ON qa.quest_component_id = qc.id
  JOIN quest_act_con_accept_components d ON d.id = qa.act_detail_id
  WHERE qc.component_kind_id = 2 AND qa.act_detail_type = 'QuestActConAcceptComponent'
    AND qc.quest_context_id NOT IN ( ... the 7 wired-channel SELECTs of §2 ... )
),
engaged AS (SELECT n.engage_combat_give_quest_id AS qid FROM npcs n
             WHERE n.engage_combat_give_quest_id > 0),
aggros AS (SELECT DISTINCT qc.quest_context_id AS qid
           FROM quest_components qc JOIN quest_acts qa ON qa.quest_component_id = qc.id
           JOIN quest_act_obj_aggros d ON d.id = qa.act_detail_id
           WHERE qc.component_kind_id = 4 AND qa.act_detail_type = 'QuestActObjAggro')
SELECT CASE WHEN e.qid IS NOT NULL AND a.qid IS NOT NULL THEN 'both'
            WHEN e.qid IS NOT NULL THEN 'engage'
            WHEN a.qid IS NOT NULL THEN 'aggro'
            ELSE 'neither' END AS bucket, COUNT(*) FROM comp_only co
LEFT JOIN engaged e ON e.qid = co.qid LEFT JOIN aggros a ON a.qid = co.qid GROUP BY bucket;
-- both=30 | engage=15 | aggro=0 | neither=70
```

The 30 "both" quests are the exact list the 2026-08-29 doc named as engine-broken (1408, 1443, 2432, 2979, 3349, 3400,
3477, 3564, 3583, 3694, 3927, 3930, 4321, 4325, 4326, 4385, 4863, 4944, 5033, 5277, 5876, 5879, 5883,
5884, 5885, 5886, 5887, 5969, 5970, 5971). The prior "engine-broken" claim attributed the blockage to the
kill event never carrying the victim — `a1653d67d` fixed that; the quests' aggro acts (all 30) now credit
through the real `Unit.DoDie` path.

```sql
SELECT co.qid, e.npc_ids FROM comp_only co
JOIN (SELECT n.engage_combat_give_quest_id AS qid, GROUP_CONCAT(n.id) AS npc_ids FROM npcs n
      WHERE n.engage_combat_give_quest_id > 0 GROUP BY n.engage_combat_give_quest_id) e ON e.qid = co.qid
ORDER BY co.qid;
```

### Auto-start path re-verification (compact.sqlite3 schema + `Unit.AddUnitAggro`)

- **Field name:** `npcs.engage_combat_give_quest_id` (type `INT`; present in the `CREATE TABLE npcs` DDL).
  Code: `NpcManager.cs:570` loads it into `NpcTemplate.EngageCombatGiveQuestId` (`NpcTemplate.cs:71`);
  **48** NPC templates carry a non-zero value.
- **Trigger semantics:** `Unit.AddUnitAggro` (Unit.cs:1646) first-aggro block — on the FIRST entry added to
  the victim NPC's `AggroTable` (`AggroTable.TryAdd` new-key branch, Unit.cs:1688-1694), if the unit is a
  `Character` and `npc.Template.EngageCombatGiveQuestId > 0`, the engine calls
  `player.Quests.AddQuestFromNpc(id, npc.ObjId)` (Unit.cs:1699-1707) which starts the quest with the
  **Npc acceptor triple** (`QuestAcceptorType.Npc` + NPC template id, `CharacterQuests.cs:190-197`).
  Confirmation: nearby monster entering the NPC's aggro list = the exact trigger; no other precondition.
- **Fail-closed control:** NPC 7669 (no engage field) starts nothing on first aggro — unchanged.

### Bucket (d) — 70 genuinely unreachable (stub `RunAct` true-return, no actionable precondition)

5063, 5064, 5143, 5144, 5451, 5452, 6004, 6005, 6008, 6020, 6021, 6022, 6040, 6045–6053, 6213, 6214, 6216,
6224, 6255, 6257, 6259, 6261, 6263–6349 (all odd 6255–6349 plus 6284/6286/6288/6290/6292/6294/6296/6298/
6300/6302/6304/6306/6308/6310/6312/6314/6316/6318/6320/6322/6324/6326/6329/6331/6333/6335/6337/6339/6341/
6343/6345/6347/6349). Progress shapes: 31 MonsterHunt, 23 MonsterGroupHunt, 6 Cinema, 3 Sphere,
3 Interaction, 4 no-progress — but **no NPC template in the DB carries `engage_combat_give_quest_id`
matching these quest ids**, and the Start component's `QuestActConAcceptComponent.RunAct` is a stub
returning true with no player-perceivable precondition. The bot has no primitive to even learn these quests
exist. They are NOT blocked by the aggro fix; they are blocked by the component channel's stub semantics
itself (deferred by design since the 2026-08-29 census).

### Recommendation — what the loop can now pursue

| Set | Count | Status at HEAD |
|---|---|---|
| Kill-only (380) | 380 | **Pursued via `DiscoverQuests` kill-accept channel** (`3827b5170`); 263 fully spawn-reachable |
| Component 30 "both" (auto-start + aggro) | 30 | **Pursued via `AutoStartedQuestIds` + `AggroLeg`** (`7d0b80041` + `a1653d67d` + `f5331ced7`) |
| Component 15 engage-only (auto-start + MonsterHunt/plain) | 15 | **Pursued via `AutoStartedQuestIds`** + ordinary hunt/reward legs |
| Component 70 "neither" | 70 | **Unreachable** — no perception primitive stubs these in; correctly out of reach |

**Total perceivable at HEAD:** 4,181 (8-channel wired union) + **45 auto-started component quests**
(30 both + 15 engage-only) = **4,226** quests the loop can pursue end-to-end. This is a strict +45 over the
2026-08-29 figure of 4,181, and a strict −70 on the "unreachable" remainder (495 → 70; the 380 kill-only
subset is now perceived, and 45 of the 115 component-only are now engine-startable).
