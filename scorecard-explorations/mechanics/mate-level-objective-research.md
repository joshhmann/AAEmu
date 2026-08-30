# QuestActObjMateLevel — Research Dossier (2026-08-30)

Read-only research. **No code changed, no commit, no build, no soak/E2E root touched.**
Branch `develop`, local HEAD `62990d16b71d0b0f8a9304fe9470c93f6b4e4d46` (LevelLeg landed).
Canonical data: `AAEmu.Game/Data/compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af` (unchanged).
Sibling dossier: AbilityLevel (`scorecard-explorations/mechanics/ability-level-objective-research.md`).
Synthesis updates STATUS/SCORECARD after both dossiers; this file does not.

---

## 1. Scope

Post-M7 readiness and closure → PB-002 (autonomous quest progression) → the
`QuestActObjMateLevel` objective family. Per STATUS.md 2026-08-30, the remaining
objective gaps are `QuestActObjAbilityLevel` (11 quests) and `QuestActObjMateLevel`
(7 live quests; 1 orphaned data row); this dossier covers the MateLevel family only.

This is **research, not implementation and not closure**: it establishes the canonical
data surface, the objective's exact semantics, every writer to mate XP/level, the
player-action contract, the actor-surface mapping, and a decision with the evidence
layer available. No STATUS/SCORECARD update is made here.

---

## 2. Canonical data (compact.sqlite3, read-only)

### 2.1 Raw rows — `quest_act_obj_mate_levels` (10 rows)

```sql
SELECT m.id, m.item_id, m.LEVEL, m.cleanup, m.use_alias, m.quest_act_obj_alias_id,
       a.id AS act_id, a.quest_component_id, c.quest_context_id, c.component_kind_id
FROM quest_act_obj_mate_levels m
LEFT JOIN quest_acts a ON a.act_detail_id = m.id AND a.act_detail_type = 'QuestActObjMateLevel'
LEFT JOIN quest_components c ON c.id = a.quest_component_id
ORDER BY m.id;
```

| detail_id | item_id | LEVEL | cleanup | use_alias | alias_id | act_id | comp_id | quest_id | kind |
|---|---|---|---|---|---|---|---|---|---|
| 2 | 14878 | 50 | t | t | 2420 | 33000 | 23458 | 5430 | 4 |
| 3 | 8158 | 50 | t | f | 2423 | 33008 | 23466 | **NULL** | — |
| 4 | 8163 | 50 | t | f | 0 | 33212 | 23612 | **NULL** | — |
| 5 | 8162 | 50 | t | f | 0 | 33213 | 23467 | **NULL** | — |
| 6 | 8158 | 50 | t | t | 2467 | 33270 | 23643 | 5464 | 4 |
| 7 | 8162 | 50 | t | t | 2468 | 33271 | 23645 | 5465 | 4 |
| 8 | 8163 | 50 | t | t | 2469 | 33272 | 23647 | 5466 | 4 |
| 9 | 28420 | 50 | t | t | 2567 | 34550 | 25125 | 5812 | 4 |
| 10 | 28752 | 50 | t | t | 2568 | 34551 | 25126 | 5813 | 4 |
| 11 | 30263 | 50 | t | t | 2746 | 35465 | 25905 | 6015 | 4 |

Schema: `id` PK, `item_id` (the summon item), `level` (50 for all), `cleanup` (t for all —
the summon item is consumed when the objective credits), `use_alias`, `quest_act_obj_alias_id`.
Alias rows (`quest_act_obj_aliases`): 2420/2467/2468/2469/2567/2568/2746 = "50 레벨의
@ITEM_NAME(...) 소환" (summon a Level-50 <item>); 2423 = "장비를 착용하지 않은 레벨 50의
@ITEM_NAME(8158) 보유" (hold a Level-50 8158 without equipment).

### 2.2 Linked Progress acts — 6 live carrier quests + 4 orphaned detail rows

**6 quests carry the act** (all Progress comps, kind 4, Level 50, category 5/87,
repeatable, milestone 5, zone 1):

| quest | name | detail | item | summon NPC (level) |
|---|---|---|---|---|
| 5430 | 폭풍을 넘어 천둥을 내 손에 | 2 | 14878 폭풍질주 | 7043 (5) |
| 5464 | 잘 자란 칠흑의 릴리엇 말 납품 | 6 | 8158 | 5430 (5) |
| 5465 | 잘 자란 붉은 점박 릴리엇 말 납품 | 7 | 8162 | 5434 (5) |
| 5466 | 잘 자란 순백의 릴리엇 말 납품 | 8 | 8163 | 5435 (5) |
| 5812 | 잘 자란 날렵한 갈색 곰 납품 | 9 | 28420 | 13432 (5) |
| 5813 | 잘 자란 날렵한 눈보라 곰 납품 | 10 | 28752 | 13519 (5) |

All six demand a **Level-50 mate** (`SummonMate` impl 11; the summoned NPCs are all
level 5 — the mate must gain 45 levels). All `cleanup=t`: the summon item is consumed
by `CalculateObjective` when the objective credits.

**4 orphaned detail rows (3/4/5/11):** details 3/4/5 have `quest_acts` rows
(33008/33212/33213) whose `quest_component_id` (23466/23612/23467) exists in **no**
`quest_components` row — the owning contexts were deleted upstream. Detail 11's act
35465 → comp 25905 → quest_context 6015, whose `quest_contexts` row is deleted
(data-defects.md §7 orphan class, 28 ids; 6014/6015 were a mutual kind-36
ExceptComplete pair, both orphaned). The loader skips all four:
`QuestManager.cs:1479-1481` (`GetComponentByActTemplate` returns null → `continue`)
and `LoadBaseQuestActs` skips acts whose component is missing (`QuestManager.cs:497-500`).
They are inert dangling rows, never loaded, never evaluated.

### 2.3 Quest topology — all 6 live quests share one shape

```sql
SELECT qc.id, c.component_kind_id, a.act_detail_type, a.act_detail_id
FROM quest_contexts qc JOIN quest_components c ON c.quest_context_id = qc.id
LEFT JOIN quest_acts a ON a.quest_component_id = c.id
WHERE qc.id IN (5430,5464,5465,5466,5812,5813) ORDER BY qc.id, c.component_kind_id;
```

| kind | act | 5430 | 5464 | 5465 | 5466 | 5812 | 5813 |
|---|---|---|---|---|---|---|---|
| 2 Start | AcceptNpc | 4175→NPC 7054 | 4205→13453 | 4206→13454 | 4207→13455 | 4430→13454 | 4429→13455 |
| 4 Progress | ItemGather | 2666: 28219 ×1 | 2702: 28449 ×1 | 2705: 28449 ×1 | 2704: 28449 ×1 | 2846: 29767 ×1 + 2851: 28481 ×500 | 2845: 29767 ×1 + 2852: 28481 ×500 |
| 4 Progress | **MateLevel** | 2 (14878) | 6 (8158) | 7 (8162) | 8 (8163) | 9 (28420) | 10 (28752) |
| 6 Ready | ReportNpc | 4486→7054 | 4522→13453 | 4523→13454 | 4524→13455 | 4706→13453 | 4707→13453 |
| 8 Reward | SupplyItem | 28240 ×1 | 28458 ×1 | 28458 ×1 | 28458 ×1 | 29769 ×1 | 29769 ×1 |

Start gates (`unit_reqs`, owner_type `QuestComponent`, on comps 23456/23634/23637/23640/
25119/25122): **kind 1 (Level) value1=30** + **kind 10 (OwnItem) value1 = the summon
item** (rows 42196-42197, 42359-42360, 42365-42366, 42363-42364, 44470-44471,
44472-44473). `UnitReqs.cs:50-51` (Level) and `:69-71` (OwnItem) implement both.

Reward items are **sealed** versions: 28240 (번개를 머금은 폭풍질주) use-skill 22465 →
SpecialEffect 27 (GainItem) → unsealed 28087 천둥질주 (impl 11, summon NPC 13294 level 30);
28458 (봉인된 검은 화살) → 28138 검은 화살 (NPC 13308 level 30); 29769 (봉인된 날렵한
검은색 곰) → 28753 (NPC 13520 level 5). The rewards are better/equal mates, not the
trade-in item.

### 2.4 Summon-item grant sources (canonical)

| item | sources |
|---|---|
| 14878 폭풍질주 | **merchant pack 192** (명예 상인, honor merchant) goods 5596, honor_price **20,000**; pack 192 sold by NPCs 7054/8555/8594/8595 |
| 8158/8162/8163 | **foal doodad growth chains** (doodads 4699/4744/4745 칠흑/붉은 점박/순백 릴리엇 망아지): 6-step `DoodadFuncUse` chain (skills 18035 귀여워 해주기 / 17850 소환수 먹이 먹이기 / 18037 물 먹이기 / 18038 아기 씻기기 / 17355 놀아주기 / 17387 회수하기) ending in `DoodadFuncLootItem` 2571/2617/2618 → the summon item; foal items 23679/23683/23684 (impl 9 SpawnDoodad, use-skill 13139) also drop from real packs (8353, 261 rows; 8082) |
| 28420/28752 | **zero** canonical grant sources |
| 30263 (orphan 6015) | zero |

The foal chains are real but long (6 interactions each with feed/play skills; the
interaction skills carry `SkillUse` special effects 5858/7016/8403 → buffs 17352/17645/
19405, no XP). The honor merchant path for 14878 requires 20,000 honor — a real
currency with no bounded headless grind. **The summon item is the acquisition weak
link for every quest; a rig must fixture-grant it (LevelLeg precedent: item 33027 has
zero grant sources and is fixture-granted as setup only).**

### 2.5 Gather-item grant sources (all real)

- 28219 빛나는 번개의 정수 (5430): merchant pack 192 goods 6674 (honor merchant).
- 28449 우량마 품질 증명서 (5464-5466): merchant pack 154 goods 5294 (price 5,000,000,
  sold by ~100 merchants incl. NPC 1051/7072/5118/…); loots 74113/74132 (packs 8290/8291,
  drop_rate 1029/10000000 — rare).
- 29767 1등급 곰 품질 증명서 (5812/5813): merchant pack 154 goods 5618 (price 1,000,000).
- 28481 벌꿀 ×500 (5812/5813): loots 73098/73099/73102/73325/77850 (packs 7942/7943/7944/
  8013/9431, 1-2/4-6/12-16/24-30/80 per roll) — real NPC drops, bounded (~20-40 kills for 500).

---

## 3. Objective semantics — `QuestActObjMateLevel.cs` (read fully)

`AAEmu.Game/Models/Game/Quests/Acts/QuestActObjMateLevel.cs` (whole file, 100 lines):

- Fields: `ItemId` (uint), `Level` (byte), `Cleanup` (bool), `UseAlias`,
  `QuestActObjAliasId`; `CountsAsAnObjective => true`.
- **`CalculateObjective(Quest)`** (`:23-58`): scans `quest.Owner.Inventory`
  `GetAllItemsByTemplate(null, ItemId, -1, ...)`; for each `SummonMate` item, checks
  `summonMate.DetailLevel >= Level`; on match `SetObjective(quest, 1)` and, if
  `Cleanup`, `ConsumeItem(QuestRemoveSupplies, TemplateId, 1, summonMate)` (the summon
  item is deleted); returns the mate's `DetailLevel` or 0.
- **`RunAct`** (`:60-68`): `CalculateObjective` → returns `res > 0`. Pure live-state
  check at step evaluation.
- **`InitializeAction`** (`:76-78`): subscribes `quest.Owner.Events.OnMateLevelUp +=
  questAct.OnMateLevelUp`.
- **`FinalizeAction`** (`:80-84`): unsubscribes.
- **`OnMateLevelUp`** (`:92-99`): guards `questAct.Id != ActId`, then
  `CalculateObjective` (which `SetObjective(1)` + `RequestEvaluation` on success).

**Key headless fact:** `Mate.AddExp` fires `owner.Events.OnMateLevelUp` directly
(`Mate.cs:568`) — **NOT gated on `Connection`** (contrast `Character.AddExp`'s
`DoOnLevelUpEvents` gate at `Character.cs:1532-1535`). The event fires for headless
characters registered in `WorldManager`, so the act's handler → `SetObjective(1)` →
`RequestEvaluation` → `EnqueueEvaluation` → `RunCurrentStep` works headless. The
objective also credits from live state at step evaluation (`RunAct`), so both credit
paths are headless-safe.

**What is compared and when:** the `DetailLevel` on the **inventory summon item**,
which is written only by `Mate.UpdateMateItemData` (`Mate.cs:517-531`) via
`ItemManager.Instance.GetItemByItemId(ItemId)` — the item instance must be registered
in the global item registry for the detail write to land. `DbInfo.Xp/Level` are always
updated (`Mate.cs:559-560`). **Rig-seam risk (UNKNOWN until verified):** a
fixture-granted summon item must be registered in `_allItems` or `UpdateMateItemData`
silently no-ops and `CalculateObjective` never sees the level rise.

---

## 4. Mutation path — every writer to mate XP/level

### 4.1 Writers (exhaustive)

| Writer | Location | Effect | Callers |
|---|---|---|---|
| `Mate.AddExp(int)` | `Mate.cs:536-571` | The **only** XP/level writer. Applies `World.ExpRate`, recomputes level via `ExperienceManager.GetLevelFromExp(exp, Level, out _, mate: true)`, caps at `MaxMateLevel` (50), writes `SummonMate.DetailMateExp/DetailLevel` via `UpdateMateItemData`, updates `DbInfo`, sends `SCExpChangedPacket`, and on level-up broadcasts `SCLevelChangedPacket` + fires `owner.Events.OnMateLevelUp` | see below |
| `Mate.StartUpdateXp` | `Mate.cs:659-670` | Arms `MateXpUpdateTask` (60 s wall-clock) | `VehicleMovementModel.ApplyUnitMove` (`:76-77`) when a ridden mate moves (VelX/VelY ≠ 0); stopped on stop (`:78-79`) |
| `MateXpUpdateTask` | `Tasks/Mate/MateXpUpdateTask.cs:16` | `mate.AddExp(300)` every 60 s while armed | the task itself |
| GM `ChangeLevel` | `Scripts/Commands/ChangeLevel.cs:61` | `mate.AddExp(expForTargetLevel)` | GM command (banned for loops) |

### 4.2 `Mate.AddExp` callers (the only gameplay XP sources)

| Caller | Location | Condition |
|---|---|---|
| `Npc.DoDie` kill share (solo) | `Npc.cs:883` | every **active** mate of the killer gets `KillExp` |
| `Npc.DoDie` team share | `Npc.cs:978` | party member `mateKillXp` |
| `AddExp` special effect | `Skills/Effects/SpecialEffects/AddExp.cs:54` | effect apply on a `Mate` target; `IsMaxLevel` → `skill.Cancelled = true` + error (no consumption) |
| GM `ChangeLevel` | `ChangeLevel.cs:61` | GM command |

`KillExp` formula (kind 34, owner Npc): `((level*5+90) + if_negative(level-51,0,(level-50)*980)) * npc_grade`
(`unit_formulas` id 233). A level-50 grade-1 NPC = **340 XP/kill** → kill-share to
Level 50 (2,021,250 XP) ≈ **5,945 kills**. Riding timer = 300 XP/60 s ≈ **112 h**.
Both unbounded for a rig.

### 4.3 Is `OnMateLevelUp` consumed by the quest act? **Yes — and headless-safe.**

`Mate.AddExp` (`Mate.cs:568`) → `owner.Events.OnMateLevelUp` (`UnitEvents.cs:55`) →
`QuestAct.OnMateLevelUp` (`QuestAct.cs:200-203`) → `QuestActObjMateLevel.OnMateLevelUp`
(`:92-99`) → `CalculateObjective` → `SetObjective(1)` + `RequestEvaluation`. No
`Connection` gate anywhere in this chain (verified: `Mate.cs:562` `owner.SendPacket`
no-ops headless; the event fires regardless).

### 4.4 The canonical mate-XP consumable chain (NEW finding — the honest XP source)

`AddExp` special effect (SpecialType 28) has an explicit `case Units.Mate` branch
(`AddExp.cs:50-56`). Canonical skills with target_type 16 (`SkillTargetType.Others`,
relation 1 Friendly) carry it:

| item | name | use_skill | AddExp value | price | grant sources |
|---|---|---|---|---|---|
| 27733 | 소환수 성장의 물약 | 22003 | 5,000 | 100 | loots 72437/77630 (packs 7827/9336) |
| 27990 | 모험가의 소환수 성장의 물약 | 22003 | 5,000 | 100 | loots 72684/74289/74442/78396/8000165 |
| 27736 | 강력한 소환수 성장의 물약 | 22014 | 10,000 | 300 | — |
| 27991 | 모험가의 강력한 소환수 성장의 물약 | 22014 | 10,000 | 300 | loots 72686/8000163 |
| 28329 | 시간을 새긴 유리병 | 22581 | 12,000 | 20,000 | — |
| 29040 | 파트라슈 빵 | 23085 | **50,000** | 100 | loots 73375/74876/8000033/8000156 (packs 8018/8353/8000003/8000019) |

All are `use_skill_as_reagent = t` (consumed on use), `need_learn = f`, `auto_learn = t`,
no mana, no reagents. `Skill.GetInitialTarget` resolves `Others` from the cast target
(`Skill.cs:451-467`); a Mate is a `BaseUnit` in `ParentWorld.GetBaseUnit`, so
`UseItem(29040, mateObjId)` targets the active mate and the `AddExp` effect lands on it.
**Level 50 = 2,021,250 XP → 41 × 50,000-XP 파트라슈 빵 (29040), a bounded grind.**
The honor-merchant potions (18390 경험치 성장의 물약, honor 300, 10,000 XP; 129 성장의 돌,
honor 5,000, 200,000 XP) are target_type 0 Self — they hit the character, not the mate.

---

## 5. Player action contract

**PARTIAL — real 1.2 client actions exist that can raise mate XP/level, but the
canonical quests' magnitude makes most of them unbounded; the potion chain is the
bounded one.**

| Action | Packet | Grants XP? | Notes |
|---|---|---|---|
| Summon mate | `CSStartSkillPacket` SkillItem branch (use-skill 10602/15174/22700 → SpawnPet 24 → `SpawnMount`) | No | State change only; required precondition |
| Mount / ride | `CSMountMatePacket` 0x0a7 / `CSUnMountMatePacket` 0x0a8 | No (mounting alone) | Riding XP needs movement |
| Move while riding | `CSMoveUnitPacket` UnitMoveType → `VehicleMovementModel.ApplyUnitMove` → `StartUpdateXp` | **Yes, 300 XP/60 s** | 112 h to Level 50 — unbounded |
| Kill with active mate | (combat) → `Npc.DoDie` → `mate.AddExp(KillExp)` | **Yes, KillExp/mate** | ~5,945 kills at 340 XP — unbounded |
| Feed/train mate | **No such action exists** | — | `DoodadFuncFeed` feeds doodad livestock, not mates; `MateMakeGetUp`/`HealPet` are TODO stubs |
| Mate equipment | `CSChangeMateEquipmentPacket` 0x0a9 | No | State change only |
| **Use mate-growth potion** | `CSStartSkillPacket` SkillItem branch, target = mate | **Yes, 5k-50k per use** | **Bounded: 41 × 50k potions** |
| GM `ChangeLevel` | GM command | Yes | Banned for loops |

**UNKNOWN / missing capture:** whether 1.2 had a mate feeding/training action beyond
the foal-growth doodad chains (the foal chains grant the summon item, not XP) and
whether the client sends anything else on potion use. The potion chain itself is fully
evidenced in canonical data + code; no client capture is needed for it.

---

## 6. Actor surface mapping

`IGameplayActor` exposes 50 actions (`ActorActionType` 0-49). The relevant ones:

| Action | Location | Maps to |
|---|---|---|
| `UseItem(itemTemplateId, targetObjId)` | `GameplayActor.cs:1257-1339` | Real `Skill.Use` with `SkillItem` caster; `targetObjId` resolved via `ResolveUnit` → `ParentWorld.GetUnit(objId)` (`:3983-3990`) — **a Mate is a Unit registered by `AddObject` (`WorldInstance.cs:580`), so mate-targeted potion use works today** |
| `Mount(mateObjId)` | `GameplayActor.cs:1590-1628` | `MateManager.MountMate` (CSMountMatePacket path); ownership + driver-seat preflight |
| `Dismount(mateObjId)` | `GameplayActor.cs:1630-1660` | `MateManager.UnMountMate` (CSUnMountMatePacket path) |
| `DriveVehicle(vehicleObjId, dest, speed)` | `GameplayActor.cs:1668-1705` | Mate branch applies `VehicleMovementModel.ApplyUnitMove` per leg (`:3766-3768`) → `StartUpdateXp` when moving |
| `Cast`/`SetTarget` | `GameplayActor.cs:383-455` | combat path (kill-share) |

**Explicit prohibition for a future leg:** a `MateLeg` MUST NOT call `Mate.AddExp`
directly, MUST NOT assign `Mate.Experience`/`Level` or `SummonMate.DetailLevel`, and
MUST NOT write the quest objective counter. The LevelLeg precedent
(`LevelingLoopScenario.cs:1055-1058`) is the binding pattern: XP must rise through the
engine's own gameplay paths (here: the `AddExp` special effect on canonical
mate-targeted potions via `UseItem`, or the kill-share/riding paths), and the objective
must credit from the engine's own `OnMateLevelUp` event / live state at step evaluation.

---

## 7. Candidate implementation assessment

**Can a bounded mate-combat or riding scenario honestly satisfy any canonical quest? No
— but a bounded potion scenario can.**

- **Kill-share:** level-50 grade-1 NPC = 340 XP/kill (`unit_formulas` 233) →
  ~5,945 kills to Level 50. Unbounded for a rig; the LevelLeg's 64-kill budget is
  ~2% of the way there.
- **Riding:** 300 XP/60 s (`MateXpUpdateTask.cs:16`) → ~112 h. Unbounded; the rig's
  `TaskManager` is a mock (the `M1M2ReplayScenarioRigTests.TaskManagerTicker` precedent
  exists but the timer is 60 s wall-clock).
- **Potion chain (bounded):** 41 × 파트라슈 빵 (29040, 50,000 XP each) through the
  real `UseItem` → `Skill.Use` → `AddExp` effect → `Mate.AddExp` → `OnMateLevelUp` →
  act credit. The potions are canonical items with canonical grant sources (real NPC
  loot packs 8018/8353/8000003/8000019; price 100, sellable). **This is not fake XP** —
  it is the engine's own `AddExp` special effect on canonical consumables, exactly the
  `AddExp.cs:50-56` Mate branch retail used.
- **Item-source constraints:** the summon item must be fixture-granted (14878 needs
  20,000 honor; 8158/8162/8163 need the long foal chains; 28420/28752/30263 have zero
  sources) — same precedent as LevelLeg's item 33027. The gather acts are real:
  certificates merchant-buyable (28449 5M copper / 29767 1M copper, pack 154), honey
  from real NPC drops (~20-40 kills for 500).
- **Time budget:** accept (Level 30 + OwnItem gates) → gather (1 certificate or 500
  honey) → summon (fixture item) → 41 potion uses → report → reward. Bounded and
  deterministic; the potion uses are the dominant cost (~41 actions).
- **Rig-seam risk (UNKNOWN):** `UpdateMateItemData` writes `DetailLevel` only when
  `ItemManager.GetItemByItemId(ItemId)` resolves (`Mate.cs:519-527`); a fixture-granted
  summon item must be registered in the global item registry or the objective never
  credits. Must be verified in the rig before the leg is claimed.

---

## 8. Decision

**Implementable after a narrow new action contract — a `MateLeg` pursuit case in
`LevelingLoopScenario` composing existing `UseItem` actions (summon + mate-targeted
growth potions), with the engine's own `OnMateLevelUp` event as the credit path.**

| Quest set | Decision | Rationale |
|---|---|---|
| 5430, 5464, 5465, 5466, 5812, 5813 (6 live) | **Implementable after a narrow new action contract** | The potion chain (29040/27733/27736/27991/28329 via `UseItem` with mate target) is canonical and bounded (41 × 50k XP); `OnMateLevelUp` is headless-safe (no Connection gate, `Mate.cs:568`); gather acts are real; summon item fixture-granted per LevelLeg precedent. The new contract is the `MateLeg` pursuit case + rig seams (item-registry registration, potion seeding). |
| detail rows 3/4/5/11 (dangling acts 33008/33212/33213/35465) | **Hygiene (data-defects §7)** | Orphaned contexts deleted upstream; loader skips them (`QuestManager.cs:1479-1481`, `:497-500`). Prune per data-defects.md §7 precedent alongside the AbilityLevel dangling rows. |
| 6015 orphan (detail 11) | **Hygiene (data-defects §7)** | 6014/6015 mutual kind-36 ExceptComplete pair, both orphaned (data-defects.md §7 row; quest-reachability.md §2). |

**Evidence layer available:** canonical data (SQL, §2) + code trace (§3-§6) = **rig-layer
(A/R proxy) evidence only**. No live authenticated-server run and no human/client
evidence exists for this family; `H` stays UNKNOWN. The potion chain is data- and
code-verified but has never been exercised end-to-end; the item-registry registration
question (§7) must be resolved in the rig before any claim.

---

## 9. Next research questions (ordered)

1. **Verify the rig seam:** does a fixture-granted summon item get registered in
   `ItemManager._allItems` so `UpdateMateItemData` (`Mate.cs:519-527`) writes
   `DetailLevel`? If not, name the registration point (inventory add path) — this is
   the only hard blocker for the MateLeg.
2. **Confirm potion targeting headless:** `UseItem(29040, mateObjId)` →
   `GetInitialTarget` Others (`Skill.cs:451-467`) → `AddExp` effect on the Mate —
   verify the effect actually applies to a headless active mate (no Connection
   dependency in `AddExp.cs:50-56`).
3. **Josh data ruling on the summon-item acquisition:** fixture-grant (LevelLeg
   precedent) vs. wiring the foal growth chains (doodads 4699/4744/4745/7125) as the
   honest acquisition path for 8158/8162/8163 — the foal chains are real but 6-step
   and long; 28420/28752 have zero sources.
4. **Prune the 4 dangling act rows** (33008/33212/33213/35465, detail ids 3/4/5/11) per
   data-defects.md §7 hygiene, alongside the AbilityLevel dangling rows (34805-34808).
5. **Client capture (optional, only if a training contract is wanted):** whether 1.2
   had a mate feeding/training action beyond the foal doodad chains; the potion chain
   needs no capture.

---

## Evidence index (file:line)

| Claim | Evidence |
|---|---|
| 10 raw rows / 6 live carriers / 4 dangling | §2.1-2.2 SQL; `QuestManager.cs:1471-1495` loader, `:1479-1481` skip; `:497-500` act skip |
| 6015 orphan (data-defects §7) | `scorecard-explorations/data-defects.md` §7 (28 ids incl. 6014/6015); `quest-reachability.md` §2 |
| Quest topology (AcceptNpc/ItemGather/MateLevel/ReportNpc/SupplyItem) | §2.3 SQL; unit_reqs rows 42196-42197 etc.; `UnitReqs.cs:50-51,69-71` |
| Summon items → NPCs level 5 | `item_summon_mates` rows 9/13/14/17/129/136/161; `CharacterMates.cs:57-135` SpawnMount |
| Act semantics (live-state + OnMateLevelUp) | `QuestActObjMateLevel.cs:23-99`; `Mate.cs:568`; `UnitEvents.cs:55`; `QuestAct.cs:200-203` |
| Mate.AddExp only writer | `Mate.cs:536-571`; callers `Npc.cs:883,978`; `AddExp.cs:54`; `MateXpUpdateTask.cs:16`; `ChangeLevel.cs:61` |
| OnMateLevelUp headless-safe (no Connection gate) | `Mate.cs:562-568` vs `Character.cs:1532-1535` |
| KillExp formula | `unit_formulas` id 233 (kind 34, owner Npc) |
| Riding timer | `MateXpUpdateTask.cs:16` (300/60 s); `VehicleMovementModel.cs:76-79`; `Mate.cs:659-676` |
| Potion chain (AddExp Mate branch) | `AddExp.cs:50-56`; skills 22003/22014/22581/23085 (target_type 16, relation 1); items 27733/27990/27736/27991/28329/29040; loots 72437/72684/72686/73375/74289/74442/74876/77630/78396/8000033/8000156/8000163/8000165 |
| Level 50 = 2,021,250 XP | `levels` id 50 `total_mate_exp`; `ExperienceManager.cs:193-194` MaxMateLevel=50 |
| UseItem targets a Mate today | `GameplayActor.cs:1257-1339` (`targetObjId` → `ResolveUnit` `:3983-3990`); `WorldInstance.cs:580` (Mate in `_units`) |
| Foal growth chains (summon-item acquisition) | doodads 4699/4744/4745/7125 func chains; `doodad_func_loot_items` 2571/2617/2618/2975; foal items 23679/23683/23684/29624 |
| Fail-closed gap entry | `LevelingLoopScenario.cs:587-608` (KnownPrimitiveGaps), `:865-868` (PursueObjectives default) |
| LevelLeg no-fake-progress precedent | `LevelingLoopScenario.cs:1055-1058`; item 33027 zero grant sources (fixture-granted) |
