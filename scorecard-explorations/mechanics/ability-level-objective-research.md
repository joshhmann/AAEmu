# QuestActObjAbilityLevel — Research Dossier (2026-08-30)

Read-only research. **No code changed, no commit, no build, no soak/E2E root touched.**
Branch `develop`, local HEAD `62990d16b71d0b0f8a9304fe9470c93f6b4e4d46` (LevelLeg landed).
Canonical data: `AAEmu.Game/Data/compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af` (unchanged).
Sibling dossier: MateLevel (`/tmp/level-objective-action-scope.md` §MateLevel). Synthesis updates
STATUS/SCORECARD after both dossiers; this file does not.

---

## 1. Scope

Post-M7 readiness and closure → PB-002 (autonomous quest progression) → the
`QuestActObjAbilityLevel` objective family. Per STATUS.md 2026-08-30, the remaining
objective gaps are `QuestActObjAbilityLevel` (11 quests) and `QuestActObjMateLevel`
(7 live quests; 1 orphaned data row); this dossier covers the AbilityLevel family only.

This is **research, not implementation and not closure**: it establishes the canonical
data surface, the objective's exact semantics, every writer to ability XP/level, the
player-action contract, the actor-surface mapping, and a decision with the evidence
layer available. No STATUS/SCORECARD update is made here.

---

## 2. Canonical data (compact.sqlite3, read-only)

### 2.1 Raw rows — `quest_act_obj_ability_levels` (15 rows)

```sql
SELECT * FROM quest_act_obj_ability_levels ORDER BY id;
```

| id | ability_id | level | use_alias | quest_act_obj_alias_id |
|---|---|---|---|---|
| 1 | 0 | 40 | f | 0 |
| 2 | 1 | 20 | f | 0 |
| 3 | 4 | 30 | t | 2639 |
| 4 | 7 | 40 | f | 0 |
| 5 | 0 | 50 | t | 2683 |
| 7 | 1 | 50 | f | (null) |
| 9 | 7 | 50 | f | 0 |
| 11 | 6 | 50 | f | 0 |
| 12 | 10 | 50 | f | 0 |
| 13 | 5 | 50 | f | 0 |
| 14 | 8 | 50 | f | 0 |
| 15 | 3 | 50 | f | 0 |
| 16 | 4 | 50 | f | 0 |
| 17 | 9 | 50 | f | 0 |
| 18 | 2 | 50 | f | 0 |

Schema: `id` PK, `ability_id` (0 = General/all), `level`, `use_alias`, `quest_act_obj_alias_id`.
Alias rows (`quest_act_obj_aliases`): 2639 = "의지 30레벨 만들기" (Will 30), 2683 =
"모든 전투능력 50레벨 달성" (all combat abilities 50) — used by detail 3 (dangling) and
detail 5 (= quest 5967).

### 2.2 Linked Progress acts — 11 carrier quests

```sql
SELECT a.id AS act_id, a.act_detail_type, a.act_detail_id, c.id AS comp_id,
       c.component_kind_id, c.quest_context_id, qc.name, qc.level AS quest_level
FROM quest_acts a
JOIN quest_components c ON c.id = a.quest_component_id
JOIN quest_contexts qc ON qc.id = c.quest_context_id
WHERE a.act_detail_type = 'QuestActObjAbilityLevel'
ORDER BY a.act_detail_id;
```

**11 quests carry the act** (all Progress comps, kind 4, Level 50, category 55/5,
non-repeatable, zone 1):

| quest | name | act_detail_id | ability_id | level |
|---|---|---|---|---|
| 5967 | 신과 영웅의 발자취 | 5 | 0 (all) | 50 |
| 6069 | 거침없이 춤추는 격투의 칼날 | 7 | 1 (Fight) | 50 |
| 6070 | 고요할수록 진실된 이름, 마법 | 9 | 7 (Magic) | 50 |
| 6075 | 드넓은 초원을 질주하는 야성 | 11 | 6 (Wild) | 50 |
| 6076 | 그대, 사랑으로 치유하라 | 12 | 10 (Love) | 50 |
| 6077 | 죽음의 손길은 자비롭고 공평하게 | 13 | 5 (Death) | 50 |
| 6078 | 삶과 목숨, 모든 것은 주어진 사명을 위해 | 14 | 8 (Vocation) | 50 |
| 6079 | 풍요와 번성의 철옹성, 철벽 | 15 | 3 (Adamant) | 50 |
| 6080 | 올곧은 의지는 커다란 힘이 되리라 | 16 | 4 (Will) | 50 |
| 6081 | 낭만을 노래하는 영원한 방랑자 | 17 | 9 (Romance) | 50 |
| 6082 | 환술, 진실을 감추는 달콤한 속삭임 | 18 | 2 (Illusion) | 50 |

**6069 is a Josh-ruled DROP** (2026-08-09, `SQL/patches/compact/2026-08-09-drop-wi6-6069.sql`,
register §8: "GO drop — zero accept surfaces, objective can never credit — dead data").
The patch deletes quest_contexts 6069 / comps 26119–26122 / acts 35730+35732 / unit_reqs
45196 / texts / bubbles; **act-detail rows 7 and 4002 are retained** (shared-style, inert).
The canonical reference file still counts 6069 (the drop is applied to runtime copies), so
the canonical census is 11 carriers; **live carriers = 10** (5967 + 6070/6075–6082).

### 2.3 Orphaned / deleted quest contexts — 4 dangling act rows

```sql
SELECT a.id AS act_id, a.act_detail_type, a.act_detail_id, a.quest_component_id
FROM quest_acts a WHERE a.id IN (34805,34806,34807,34808);
-- comps 17272/17263/25446/25447: 0 rows in quest_components
-- quest_contexts for those comps: 0 rows
```

Detail ids **1, 2, 3, 4** are referenced by `quest_acts` rows 34805–34808 whose
`quest_component_id` (17272/17263/25446/25447) exists in **no** `quest_components` row —
the owning contexts were deleted upstream (data-defects.md §7 orphan class, 28 ids).
The loader skips them: `QuestManager.cs:1080-1082` (`GetComponentByActTemplate` returns
null → `continue`). They are inert dangling rows, never loaded, never evaluated.

### 2.4 Start/accept components and unit_reqs gates

```sql
SELECT ur.owner_id AS comp_id, ur.kind_id, ur.value1, ur.value2
FROM unit_reqs ur
WHERE ur.owner_type = 'QuestComponent' AND ur.owner_id IN (
  SELECT c.id FROM quest_components c WHERE c.quest_context_id IN (5967,6069,6070,6075,6076,6077,6078,6079,6080,6081,6082))
ORDER BY ur.owner_id, ur.kind_id;
```

- **5967** Start comp 25706: **10 `unit_reqs` kind-2 (Ability) gates, value1 = 1..10
  (every ability), value2 = 50** — the accept gate is the objective itself
  (self-satisfying: the quest is only accept-able once all abilities are already 50).
  Start also carries **12 `QuestActConAcceptNpc` acts** (4517–4527, 4586 → NPCs
  879/880/1506/2586/3577/5109/5111/5392/8630/9073/9657/8705 — the class trainers,
  levels 25–50, all `ability_changer='t'`).
- **6069** Start comp 26119: 1 kind-1 (Level) gate, value1 = 50.
- **6070/6075–6082** (9 quests): Start comps 26123/26139/26143/26147/26151/26155/26159/
  26163/26167: 1 kind-1 (Level) gate, value1 = 50 each. **No Start acts at all.**

### 2.5 Accept-channel status — zero accept surfaces for 10 of 11

```sql
SELECT 'item_accept_quests' AS tbl, quest_id, item_id FROM item_accept_quests WHERE quest_id IN (5967,6069,6070,6075,6076,6077,6078,6079,6080,6081,6082)
UNION ALL SELECT 'doodad_func_quests', quest_id, quest_kind_id FROM doodad_func_quests WHERE quest_id IN (...)
UNION ALL SELECT 'accept_quest_effects', quest_id, 0 FROM accept_quest_effects WHERE quest_id IN (...)
UNION ALL SELECT 'sphere_quests', quest_id, quest_trigger_id FROM sphere_quests WHERE quest_id IN (...)
UNION ALL SELECT 'quest_act_con_accept_components', quest_context_id, 0 FROM quest_act_con_accept_components WHERE quest_context_id IN (...);
```

**0 rows across all five accept tables for all 11 quests.** The only accept path in the
family is 5967's 12 `QuestActConAcceptNpc` trainer acts — which are gated by the 10
ability unit_reqs (self-satisfying). The 9 single-ability quests (6070/6075–6082) have
**zero accept surfaces** — identical shape to 6069, which Josh ruled GO drop for exactly
this reason.

### 2.6 Quest supply / reward data

```sql
SELECT qasi.id AS act_id, qasi.item_id, qasi.count, qasi.grade_id, qasi.cleanup
FROM quest_act_supply_items qasi WHERE qasi.id IN (3958,4002,4008,4009,4010,4011,4012,4013,4014,4015,4016);
SELECT qasa.id AS act_id, qasa.appellation_id FROM quest_act_supply_appellations qasa WHERE qasa.id = 245;
```

- **5967** Reward comp 25709: `QuestActSupplyAppellation` 245 → appellation **191
  (능력자)** + `QuestActSupplyItem` 3958 → item **30012** (열두 신과 영웅 이야기 원고 ×1,
  cleanup=t). Ready comp 25708: 12 `QuestActConReportNpc` acts (4752–4762, 4815).
- **6069/6070/6075–6082** Reward comps: `QuestActSupplyItem` 4002/4008–4016 → item
  **30757** (열두 신과 영웅의 유산 ×1, cleanup=t). No Ready acts (auto-complete shape).
- `quest_supplies` is **level-keyed** (id 1..55 → exp/copper per level), not quest-keyed;
  quest reward XP is the level-based pool consumed at `Quest.cs:380-391`.

---

## 3. Objective semantics — `QuestActObjAbilityLevel.cs` (read fully)

`AAEmu.Game/Models/Game/Quests/Acts/QuestActObjAbilityLevel.cs` (whole file, 44 lines):

- Fields: `AbilityId` (AbilityType), `Level` (byte), `UseAlias`, `QuestActObjAliasId`;
  `CountsAsAnObjective => true`.
- **`RunAct(Quest, QuestAct, int)`** — the ONLY behavior:
  - `AbilityId > 0`: reads `quest.Owner.Abilities.Abilities[AbilityId].Exp` →
    `ExperienceManager.Instance.GetLevelFromExp(exp, out _)` → returns
    `abLevel >= Level`.
  - `AbilityId == 0` (General): loops `i` from `AbilityType.General + 1` to
    `AbilityType.None` (i.e. abilities 1..10), returns false if **any** ability is
    below `Level`, true only if all are ≥ Level.
- **No `SetObjective` call, no `InitializeAction` override, no `FinalizeAction`
  override, no event subscription.** The act is a **pure live-state check evaluated at
  step evaluation**: `QuestStep.RunComponents` → `QuestComponent.RunComponent`
  (`QuestComponent.cs:45-67`) → `QuestAct.RunAct` (`QuestAct.cs:49-50`) →
  `Template.RunAct(quest, this, count)`. The `currentObjectiveCount` parameter is
  ignored. The objective counter is never written by this act.

**What is compared and when:** at every step evaluation, the live `Exp` of the named
ability (or all abilities) on the quest owner is converted to a level via the
ExperienceManager curve and compared `>= Level`. There is no event-driven credit; the
value must already be at/above the threshold when the step is evaluated.

---

## 4. Mutation path — every writer to ability XP/level

### 4.1 Writers (exhaustive)

| Writer | Location | Effect | Callers |
|---|---|---|---|
| `CharacterAbilities.AddActiveExp(int)` | `CharacterAbilities.cs:56-73` | Splits delta across the **three active trees** (Ability1..3), each clamped to `GetExpForLevel(MaxPlayerLevel)`; `MarkDirty` | **Only** `Character.AddExp(exp, true)` |
| `CharacterAbilities.AddExp(AbilityType, int)` | `CharacterAbilities.cs:44-53` | Per-tree direct add | **Zero callers** in game code (dead API; TODO SCAbilityExpChangedPacket) |
| `CharacterAbilities.Swap` | `CharacterAbilities.cs:74-118` | Tree swap only; copies Ability1.Exp into a newly-chosen Ability2/3; seeds unchosen trees at 42,000 XP (level 10) | `CSSwapAbilityPacket` (0x096) only |
| `CharacterAbilities.SetAbility` | `CharacterAbilities.cs:27-30` | Order only, no XP | create/load path |
| `BotScenarioRunner` ability-exp rig | `BotScenarioRunner.cs:267-293` | Direct `Exp` writes (exp saturation) | **test/rig only** |
| gate-probe exp swap | `BotScenarioRunner.cs:379-386` | `probeRecord.Exp = 0` | **test/rig only** |

### 4.2 `Character.AddExp(expDelta, shouldAddAbilityExp)` — the only gameplay source

`Character.cs:1495-1536`: applies `World.ExpRate`, recomputes level, then
`if (shouldAddAbilityExp) Abilities.AddActiveExp(expDelta)` (`:1518-1519`), sends
`SCExpChangedPacket`, and fires `DoOnLevelUpEvents` **only when `Connection != null`**
(`:1532-1535`).

Every `AddExp(exp, true)` caller (the only ability-XP source):

| Caller | Location | Condition |
|---|---|---|
| `Npc.DoDie` kill XP (solo) | `Npc.cs:879` | character killer, after `DoOnMonsterHuntEvents` |
| `Npc.DoDie` team share | `Npc.cs:974` | party member `plKillXp` |
| Quest level-based reward pool | `Quest.cs:390` | `QuestRewardExpPool > 0` at step end |
| `QuestActSupplyExp` | `Quests/Acts/QuestActSupplyExp.cs:21` | act execution |
| `AddExp` special effect | `Skills/Effects/SpecialEffects/AddExp.cs:58` | effect apply on Character |
| `Character.ChangeLabor` labor-spend XP | `Character.cs:1652` | formula 19 `((pc_level*4.5+37.5)/5)*labor_power` (e.g. 60 labor at level 30 = 2,070 XP) |
| GM `AddXP` | `Scripts/Commands/AddXP.cs:46` | GM command |
| GM `ChangeLevel` | `Scripts/Commands/ChangeLevel.cs:122` | GM command |

### 4.3 Is `OnAbilityLevelUp` consumed by the quest act? **No.**

- `QuestManagerEvents.DoOnLevelUpEvents` fires `owner.Events?.OnAbilityLevelUp(...)`
  (`QuestManagerEvents.cs:266`) — inside the character level-up event, after `OnLevelUp`.
- `UnitEvents.OnAbilityLevelUp` is declared (`UnitEvents.cs:56`).
- `IQuestAct.OnAbilityLevelUp` (`IQuestAct.cs:74`) → `QuestAct.OnAbilityLevelUp`
  (`QuestAct.cs:210-213`) → `QuestActTemplate.OnAbilityLevelUp` (`QuestActTemplate.cs:366-369`)
  which is a **virtual no-op**.
- **No quest act overrides it** — `QuestActObjAbilityLevel` has no handler at all
  (contrast: `QuestActObjLevel.InitializeAction` subscribes `OnLevelUp`,
  `QuestActObjMateLevel` subscribes `OnMateLevelUp`). The event is fired but nothing
  consumes it; the objective can only credit through the step-evaluation live-state check.

### 4.4 Packet surface (ability selection / spend / learn)

| Packet | Opcode | Behavior |
|---|---|---|
| `CSLearnSkillPacket` | 0x092 | `CharacterSkills.AddSkill(skillId)` — spends **skill points** (`GetSkillPointsForLevel`), never ability XP |
| `CSLearnBuffPacket` | 0x093 | passive buffs |
| `CSResetSkillsPacket` | 0x094 | resets skills |
| `CSSwapAbilityPacket` | 0x096 | `Abilities.Swap` — tree swap only |
| `CSStartInteractionPacket` | 0x068 | opens trainer UI (`SkillsEnum.ChangeSkillsets` when `npc.Template.AbilityChanger`) — **no follow-up packet exists** |
| `SCAbilityExpChangedPacket` | 0xff | defined (`SCAbilityExpChangedPacket.cs:7`) but **never sent** (TODO in `AddExp`) |

**No C2G packet, no `IGameplayActor` action, and no `PlayerBotController` seam writes
ability XP or level.** `PlayerBotController` exposes only `LevelUp()` (synthetic
`OnLevelUp` fire, `PlayerBotController.cs:103-104` — explicitly banned as a grind
mechanism by the LevelLeg precedent) and `AggroNpc`; no ability seam.

---

## 5. Player action contract

**UNKNOWN — no real 1.2 client action exists in source that can increase the objective's
value.**

- The only ability-XP writer reachable from gameplay is `AddActiveExp`, fed by
  character-XP sources (kill, quest reward, labor spend, effects). It feeds **only the
  three active trees** and is a passive share — there is no player *action* that targets
  ability XP.
- The 1.2-era "training" flow (spend ability points / learn from trainer NPC) is not
  implemented server-side: `CSStartInteractionPacket` opens the trainer UI but no
  follow-up packet exists in `CSOffsets.cs`; `CSLearnSkillPacket` spends skill points,
  not ability XP.
- **Missing prerequisite (named):** a client packet capture of the 1.2 trainer flow
  (what the client sends after the ChangeSkillsets interaction to spend ability points
  or grant ability XP), or retail 1.2 data describing the mechanic, or a new reviewed
  action contract built on that capture. Until one exists, no honest player-like action
  can be composed.

---

## 6. Actor surface mapping

`IGameplayActor` (`Core/Managers/Bots/IGameplayActor.cs`) exposes **50 actions**
(`ActorActionType` 0–49: Observe, Move, NavigateTo, Stop, SetTarget, Cast, CastAt,
Interact, InteractWith, Loot, UseItem, Equip, AcceptQuest, AdvanceQuest, TurnInQuest,
TurnInDoodad, AutoTurnIn, DiscoverQuests, DiscoverSelfQuests, Talk, PlayCinema, Mount,
Dismount, PackPickup, PutDown, Buy, Sell, SellSpecialty, AuctionPost, AuctionBuy, Plant,
Harvest, HouseBuild, Drive, LoadPackOntoVehicle, BoardVehicle, UnboardVehicle,
DepositMoney, WithdrawMoney, DepositItem, WithdrawItem, Craft, PartyInvite, PartyAccept,
TradeOffer, TradePutup, TradeLockOk, ExpeditionCreate, ExpeditionInvite,
ExpeditionAccept, ExpeditionLeave).

**None of them touch ability XP or ability level.** There is no Train/Ability action.
The mutation path (AddActiveExp via character XP) is reachable only *indirectly* through
the existing kill/quest/labor paths, and none of those can target a specific ability tree.

**Explicit prohibition for a future leg:** a `QuestActObjAbilityLevel` pursuit leg MUST
NOT call `CharacterAbilities.AddActiveExp`/`AddExp` directly, MUST NOT assign
`Ability.Exp`/level, and MUST NOT write the quest objective counter. The LevelLeg
precedent (`LevelingLoopScenario.cs:1055-1058`) is the binding pattern: the value must
rise through the engine's own gameplay paths, and the objective must credit from live
state at step evaluation. Since no gameplay path can raise a *specific* ability's level
on demand, no honest leg exists.

---

## 7. Candidate implementation assessment

**Can combat/quest XP through existing actions honestly satisfy any canonical quest? No.**

- **Quest gates first:** the 9 single-ability quests (6070/6075–6082) are **unreachable
  data** — zero accept surfaces (0 rows across all five accept tables, §2.5), identical
  to 6069's Josh-ruled drop shape. A test could only reach them by fixture-accepting a
  quest no player can ever accept — that is not representative of any real channel.
- **5967** is accept-able (12 trainer NPCs) but its accept gate is **self-satisfying**:
  the 10 kind-2 ability unit_reqs (all abilities ≥ 50) are the objective itself. A
  character who can accept it has already completed it.
- **Even if accepted:** the objective is a passive character-XP share. `AddActiveExp`
  feeds only the three active trees; the single-ability quests gate on specific trees
  (e.g. 6075 = Wild 6) that may not be active. The act has no `SetObjective` and no
  event — it is a pure live-state check, so a rig could only "pass" by pre-seeding
  ability Exp, which is exactly the fake progression the loop rejects
  (`LevelingLoopScenario.cs:597-600` fail-closed reason; `PursueObjectives` default
  `:865-868`).
- **Fake fixture credit is not production evidence:** the `BotScenarioRunner` ability-exp
  rig (`BotScenarioRunner.cs:267-293`) and `BotScenarioTemplates.AbilityPrerequisiteGate`
  (`BotScenarioTemplates.cs:93-140`, rigs Fight/Magic/Love to 50) are provisioning/test
  seams for gate tests (quest 5531), not player actions; they must not be cited as
  production ability-level progression.

**Why a test would not be representative:** any deterministic rig test for this family
would have to either (a) fixture-accept an unreachable quest, or (b) pre-seed ability
Exp to satisfy a live-state check — both violate the loop's no-fake-progress rule and
prove nothing about a real player channel.

---

## 8. Decision

**Data ruling/drop for the 9 single-ability quests (6069 precedent); 5967 remains a gap
blocked on client capture — the family is NOT implementable now.**

| Quest set | Decision | Rationale |
|---|---|---|
| 6070, 6075–6082 (9 quests) | **Data ruling/drop** | Zero accept surfaces, identical shape to 6069 (Josh-ruled GO drop 2026-08-09, register §8). Needs a Josh ruling to drop (or wire accept surfaces); no code path can ever be entered. |
| 5967 (all-abilities) | **Client-capture blocked** | Acceptable only via trainers whose gate is the objective itself (self-satisfying). No player action raises a specific ability's level; the 1.2 trainer flow (spend → ability XP) is unimplemented and no follow-up packet exists. A training contract requires a client capture of the 1.2 flow first. |
| detail rows 1–4 (dangling acts 34805–34808) | **Hygiene (data-defects §7)** | Orphaned contexts deleted upstream; loader skips them. Prune per data-defects.md §7 precedent. |

**Evidence layer available:** canonical data (SQL, §2) + code trace (§3–§6) = **rig-layer
(A/R proxy) evidence only**. No live authenticated-server run and no human/client
evidence exists for this family; `H` stays UNKNOWN. The 6069 drop precedent is a
registered Josh ruling (`dropped-content-register.md` §8, `SQL/patches/compact/
2026-08-09-drop-wi6-6069.sql`).

---

## 9. Next research questions (ordered)

1. **Josh data ruling on 6070/6075–6082** — drop (6069 precedent, register §8 pattern:
   guarded SQL patch, quest-scoped rows, shared act-detail rows untouched) or wire
   accept surfaces. This is the only implementable next step.
2. **Client packet capture of the 1.2 trainer flow** — what does the client send after
   `CSStartInteractionPacket` (ChangeSkillsets) to spend ability points / grant ability
   XP? Needed before any training action contract can be designed for 5967.
3. **Retail 1.2 mechanic confirmation** — did 1.2 have a spend-ability-points mechanic
   at all, or was ability XP purely the character-XP share? (wiki/retail data; decides
   whether 5967's objective is even reachable in retail).
4. **5967 self-satisfying gate ruling** — the 10 kind-2 ability unit_reqs on Start comp
   25706 make the accept gate equal the objective; rule whether this is a data defect
   (like the 70 unreachable acceptance forms) or intended.
5. **Prune the 4 dangling act rows** (34805–34808, detail ids 1–4) per data-defects.md
   §7 hygiene, alongside the MateLevel 6015 orphan rows.

---

## Evidence index (file:line)

| Claim | Evidence |
|---|---|
| 15 raw rows / 11 carriers / 4 dangling | §2.1–2.3 SQL; `QuestManager.cs:1072-1090` loader, `:1080-1082` skip |
| 6069 drop ruling | `SQL/patches/compact/2026-08-09-drop-wi6-6069.sql`; `dropped-content-register.md` §8 |
| Act semantics (live-state check, no event) | `QuestActObjAbilityLevel.cs:22-44`; `QuestComponent.cs:45-67`; `QuestAct.cs:49-50` |
| AddActiveExp only writer | `CharacterAbilities.cs:56-73`; `Character.cs:1518-1519` |
| AddExp(exp,true) callers | `Npc.cs:879,974`; `Quest.cs:390`; `QuestActSupplyExp.cs:21`; `AddExp.cs:58`; `Character.cs:1652`; `AddXP.cs:46`; `ChangeLevel.cs:122` |
| OnAbilityLevelUp unconsumed | `QuestManagerEvents.cs:266`; `QuestActTemplate.cs:366-369`; `QuestAct.cs:210-213`; no override in `QuestActObjAbilityLevel.cs` |
| No packet/actor writes ability XP | §4.4; `IGameplayActor.cs` ActorActionType 0–49; `PlayerBotController.cs:103-107` |
| Fail-closed gap entry | `LevelingLoopScenario.cs:597-600`, `:865-868` |
| Rig-only ability exp writes | `BotScenarioRunner.cs:267-293,379-386`; `BotScenarioTemplates.cs:93-140` |
| LevelLeg no-fake-progress precedent | `LevelingLoopScenario.cs:1055-1058` |
