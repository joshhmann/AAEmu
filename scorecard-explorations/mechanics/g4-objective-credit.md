# G4 Objective + Credit — Quest 251 (ACTIVE → identified + selected)

CODE+DATA-ONLY audit, repo `/root/aaemu-dev`, branch `develop`, 2026-09-19/20. Nothing modified, built, or run (read-only `read`/`grep`/`glob` over code and `compact.sqlite3` via read-only selectors). G3 stays frozen (`g3-closeout-freeze.md`): no G3 file touched, no behavior changed.
G4 scope: from quest-251-ACTIVE, can production autonomy determine the next objective and identify/select a valid hostile target? G4 ends at identified+selected. No combat/nav/credit/turn-in. Canonical seam: `BotActionCommandQueue → GameplayActor → engine`.

Headline finding: **251's first progress step is NOT a kill objective — it is an item-gather (4058 ×3) whose only data-backed source is corpse loot from NPC template 3475.** The authoritative credit event carries an *item* identity, never an NPC identity. No production code resolves item→drop-source-NPC, so the quest→objective half is readable but the objective→valid-hostile-target half has **no production owner** (scenario-only code does it for hunt acts, and doodad-only code fails closed for this gather act).

---

## A. Quest 251 objective map (data facts first)

### A.1 Template (data fact — `quest_contexts` row id 251)

| Field | Value |
|---|---|
| id / name | 251 / `화난 멧돼지들` (Angry boars — English per `M1M2ReplayScenario.cs:94`) |
| LEVEL | 2 |
| zone_id / category_id / grade_id | 125 / 4 / 1 |
| repeatable / successive / selective | f / f / f |
| milestone_id | 5 |

Source: `AAEmu.Game/Data/compact.sqlite3:?q=SELECT * FROM quest_contexts WHERE id=251`.

### A.2 Component chain (data fact — `quest_components` + `quest_acts`)

Kind semantics: `AAEmu.Game/Models/Game/Quests/Static/QuestComponentKind.cs:3-14` — `Start=2, Supply=3, Progress=4, Ready=6, Reward=8` (also `Fail=5, Drop=7`).

| Component id | kind | next_component | or_unit_reqs | Act (act id / type / detail id) | Detail row → meaning |
|---|---|---|---|---|---|
| 383 | 2 Start | 0 | f | 333 / `QuestActConAcceptNpc` / 77 | `quest_act_con_accept_npcs` id 77 → **npc_id 3512** |
| 633 | 4 Progress | 0 | f | 10473 / `QuestActObjItemGather` / 616 | `quest_act_obj_item_gathers` id 616 → **item_id 4058, count 3**, highlight_doodad_id NULL, use_alias f, cleanup t, destroy_when_drop t |
| 638 | 6 Ready | 0 | f | 457 / `QuestActConReportNpc` / 104 | `quest_act_con_report_npcs` id 104 → **npc_id 3512** |
| 639 | 8 Reward | 0 | f | 36911 / `QuestActSupplyItem` / 4075 | `quest_act_supply_items` id 4075 → **item_id 18791 ×1** |

Sources: `quest_components WHERE quest_context_id=251`; `quest_acts WHERE quest_component_id IN (383,633,638,639)`; the four detail-table point queries above. All in `AAEmu.Game/Data/compact.sqlite3`.

**Quest → component → objective → target-template → count chain (the G4 answer):**

> Quest **251** → Progress component **633** → ObjItemGather act **10473** (detail 616) → item **4058 ×3** → NPC template **3475** (sole drop source, via loot pack 4530) → **≥3 kills + 3 successful loots**.

### A.3 Step-machine position after accept (code fact)

- Order: `Quest.GoToNextStep` — `NewQuestCode.cs:95-160`: Start → None → Supply → **Progress (Status=Progress)** → Ready (Status=Ready) → Reward → completed+drop. Status enum: `QuestStatus.cs:3-12` (Progress=1, Ready=3, Completed=5).
- `GameplayActor.AcceptQuest` drains Start→Supply→Progress automatically (`GameplayActor.cs:843-852`, `while Step is Start or Supply, guard<4, RunCurrentStep`).
- `GameplayActor.AdvanceQuest` = `quest.RunCurrentStep()` only (`GameplayActor.cs:863-879`); `RunCurrentStep` runs current-step components and advances on success (`NewQuestCode.cs:62-90`).
- Therefore from 251-ACTIVE the **first progress step served is component 633**, whose only objective act is the 4058×3 gather. `QuestActObjItemGather.RunAct` credits from live inventory: `SetObjective(inventory.GetItemsCount(4058)); return >= Count` (`QuestActObjItemGather.cs:25-30`). No kill counter, no NPC check, no prerequisites on the act (or_unit_reqs=f on all four components — data fact).
- Completion path: inventory 4058 ≥ 3 → step Progress→Ready → report at NPC 3512 → Reward (18791×1). Cleanup: `Cleanup=t, DestroyWhenDrop=t` → gathered meat consumed at turn-in/drop (`QuestActObjItemGather.cs:49-68`).
- Loader (template construction from the same DB): `QuestManager.cs:1342-1368` (`SELECT * FROM quest_act_obj_item_gathers`, `HighlightDoodadId = GetUInt32("highlight_doodad_id", 0)` at :1357 — NULL becomes 0; `Cleanup` default true :1361).

### A.4 Drop source: why NPC 3475 (data facts + one inference)

- `loots` id 10014: **loot_pack_id 4530 → item_id 4058, ×1–1, drop_rate 10000000, always_drop f** — the ONLY `loots` row with item 4058 (data fact). [INFERENCE] 10000000/10000000 scale = guaranteed roll when the pack is rolled; container/rate modifiers may still gate generation — not re-verified.
- `loot_pack_dropping_npcs` id 2093: **npc_id 3475 → loot_pack_id 4530, default_pack t** (data fact). 3475's other packs (1616, 8055 — non-default rows 5163/6851/79421) contain no 4058 [INFERENCE: not row-checked this pass beyond the 4058 query — the claim "only source" rests on the `loots WHERE item_id=4058` single-row result, which IS verified].
- `npcs` id 3475: `솔즈리드 멧돼지` (Solzreed Boar — English per `Creatures.xml:3014`), npc_kind_id 2, **LEVEL 2, faction_id 115, npc_grade_id 1, aggression f** (non-aggressive — data fact). Faction 115 = "standard monster wildlife" per the hunt gate comment (`BotRoamStepExecutor.cs:2330-2331`); aggression f [INFERENCE] means it will not aggro-first, but it is still attackable (faction-115 short-circuit returns true in `IsAttackableWildlife`, :2331-2332).
- Spawners (sqlite, IDs only): `npc_spawner_npcs WHERE member_id=3475` → spawner ids **3372, 3710, 3729, 8541** (weight 1, type Npc). sqlite `npc_spawners` carries NO coordinates (schema-verified) — world positions come from `npc_spawns.json`: five UnitId-3475 entries near the giver cluster, e.g. `(15632.18, 15117.8)`, `(15586.546, 15117.345)`, `(15687.089, 15011.301)` (`npc_spawns.json:183347-183386`). Giver 3512 spawner is at `(15655.91, 15172.51)` (contracts §4.2). [INFERENCE, computed] nearest cited boar ≈ 60 m flat from the giver — outside the 25 m quest sweep (`QuestBehavior.cs` via `MaxQuestDiscoverRange = MaxInteractRange = 25f`, `GameplayActor.cs:1196`), outside the 25 m `Observe()` neighborhood (`GameplayActor.cs:262-264`), and outside the 45 m default hunt radius (`BotRoamStepExecutor.cs:109-110`). A second 3475 cluster exists in `npc_spawns_solzreed_wildlife.json` (e.g. Id 981002 @ X 14466.0) — zone/cluster assignment UNKNOWN, not mapped this pass.
- Start prerequisites: quest LEVEL 2 gate is rig-proven at 3512 (`GameplayActorQuestActionsTests.cs:65`, "quest 251 is level 2; the real gate evaluates"). Per-component `unit_reqs` rows for component 383 were NOT queried this pass — UNKNOWN beyond the `CanComponentRun` gate existing (`GameplayActor.cs:1446-1449`).
- Corroboration (harness, not production): unit-test rig header "accept from NPC 3512, gather item 4058 x3, report to NPC 3512, reward item 18791 x1" (`GameplayActorQuestActionsTests.cs:20-22`, constants :36-40); route spec `(251, 3512, [(4058,3)], …, 3512, [(18791,1)])` (`M1M2ReplayScenario.cs:93-96`).

### A.5 Data-vs-inference ledger (A)

- Data facts: template row, all four component/act/detail rows, kind enum, loot row 10014, drop row 2093, NPC 3475 row, spawner ids, boar/giver JSON positions, step-machine order, RunAct recount, loader defaults.
- [INFERENCE]: drop_rate 10000000 = guaranteed (scale convention not re-grounded); "sole source" (rests on single-row item query — strong but one-directional); 60 m nearest-boar distance (computed from JSON coords); non-aggressive→won't-aggro-first (aggression flag semantics); start unit_reqs content (UNKNOWN).

---

## B. Credit authority (death → attribution → matching → mutation)

### B.1 THE authoritative semantic for 251: inventory-recount-on-acquire

**`QuestActObjItemGather.OnItemGather` — `AAEmu.Game/Models/Game/Quests/Acts/QuestActObjItemGather.cs:70-78` — is THE method.** It matches:

```text
questAct.Id == ActId AND args.ItemId == ItemId(4058)
→ SetObjective(owner.Inventory.GetItemsCount(4058))
```

Identity matched = **ITEM template id 4058 + quest-act id**. No objId, no NPC template, no family/group, no range, no party check — the handler does not even look at who died. Subscription is per-quest-instance, armed in `InitializeAction` (`:32-39`, seeds objective from live bag count) and removed in `FinalizeAction` (`:41-47`). Event shape: `OnItemGatherArgs{QuestId, ItemId, Count}` (`UnitEvents.cs:97-102`).

Fired by **`QuestManagerEvents.DoItemsAcquiredEvents`** (`QuestManagerEvents.cs:86-103`): unconditional `OnItemGather` + item-group fanout (`_groupItems` lookup, :98-102). Upstream producer: `Inventory` acquire path calls `DoItemsAcquiredEvents(Owner, templateId, count)` (`Inventory.cs:920-924`); consume path funnels through the same entry with negative count (`DoItemsConsumedEvents`, :78).

### B.2 The kill half: corpse production (needed to MAKE the loot, credits nothing for 251)

`Npc.DoDie` — `Npc.cs:839-1032` — attribution rules (killer = `BaseUnit killer` arg; quest events go to `Character` owners only):

1. Tag-team path: `CharacterTagging.TagTeam != 0` → live team members **within `LootingContainer.MaxLootingRange` (= 200f, `LootingContainer.cs:32`) of the victim** are eligible (`:843-861`).
2. Tagger path: no team → `CharacterTagging.Tagger` is eligible (`:863-872`).
3. Killer fallback: no eligible AND killer is Character → `DoOnMonsterHuntEvents(killer, this)` + full XP (`:876-887`).
4. Eligible loop: per eligible player `DoOnMonsterHuntEvents(pl, this)` (`:987`) with party-size XP scaling (`:890-982`).
5. TagShare fanout (when `TagShareEnabled`): all damage contributors within MaxLootingRange not already credited (`:999-1022`, `GetAllContributors(MaxLootingRange)` at :1013).
6. **Pets/mates: XP only** (`:975-981`, killer-fallback `:880-886`) — mates NEVER receive quest events. **Party: only in-range members** (distance check :855), never the whole roster regardless of range.

`DoOnMonsterHuntEvents` — `QuestManagerEvents.cs:169-213` — fans out per credited owner: `OnMonsterHunt{NpcId=npc.TemplateId, Count=1}` (:177-182) + monster-group fanout (:185-194) + `OnZoneKill{ZoneGroupId=victim zone group, Killer, Victim}` (:197-202) + **kill-accept fanout**: quests with `QuestActConAcceptNpcKill` for the template auto-start with `(Kill, templateId)` acceptor (:204-212; `GetQuestIdsFromKillAcceptNpc`, `QuestManager.cs:443-446`). **For 251 this entire method is progress-neutral**: 251 has no hunt acts subscribed and is not kill-accepted — verified by the act inventory in §A.2 (its only objective act is the ItemGather).

### B.3 Kill-objective matching (general semantics — G4 eligibility derivation)

`QuestActObjMonsterHunt.OnMonsterHunt` (`QuestActObjMonsterHunt.cs:40-47`): `questAct.Id == ActId AND args.NpcId == NpcId` → `AddObjective(+args.Count)`. Identity matched = **NPC TEMPLATE id** (args carry `npc.TemplateId`, never objId — `QuestManagerEvents.cs:179`; shape `OnMonsterHuntArgs{NpcId, Count, Transform}`, `UnitEvents.cs:83-88`). Siblings: group hunts match via `QuestManager.CheckGroupNpc(groupId, npcId)` (`QuestManager.cs:136-139`); zone kills carry the victim's zone group (`QuestManagerEvents.cs:196-202`; scenario comments claim `QuestActObjZoneKill` does not itself gate on it — scenario-claimed, NOT re-verified in the act file this pass). `OnKill{Target, Killer, Victim}` (`UnitEvents.cs:322-325`, fired `Unit.cs:494-495`) drives only `QuestActObjAggro`-family acts, not hunt/gather acts.

### B.4 Corpse → objective chain for 251 (end-to-end credit path)

`Unit.DoDie` → `LootingContainer.GenerateLoot(killer)` (`Unit.cs:532-533`; rolls `loot_pack_dropping_npcs` entries incl. pack 4530 — `LootingContainer.cs:81,204-208`) → `GameplayActor.Loot` (`GameplayActor.cs:1659-1688`: loot-range gate :1668, non-empty gate :1673-1674, `OpenBag(lootAll:true)` :1682 — the exact client packet call) → inventory acquire → `DoItemsAcquiredEvents` → `OnItemGather{4058}` → recount → `RunCurrentStep` Progress→Ready.

### B.5 G4 eligibility rule (derived from B.1–B.4, NOT from any bot file)

A live hostile target is **quest-valid for 251** iff ALL hold:
1. `npc.TemplateId == 3475` — the only data-backed drop source of 4058 (§A.4);
2. attackable by the owner (`BaseUnit.CanAttack` faction relation; faction-115 wildlife passes the production hunt gate);
3. kill yields loot rights for the owner under the DoDie rules (solo bot: tagger-or-killer-fallback covers it; party: within 200 m MaxLootingRange);
4. corpse lootable within loot range (`GameplayActor.cs:1668`) so the acquire event can fire.
Credit-time identity = item 4058 (B.1). Kill-time identity = template 3475 (drop table). **No method anywhere matches objId** — selection (objId) and credit (template/item) live on different keys, so any selector must carry the template→objId resolution explicitly.

---

## C. Existing bot path (production vs scenario, owners, duplication)

### C.1 Ownership table

| Responsibility | Current owner | Notes |
|---|---|---|
| read-active-quests | `QuestBehavior.Run` (production wake leg) | `BotObservedContext.Capture` → `ActiveQuestIds` (`QuestBehavior.cs:70-71`); one `AdvanceProposal` per non-Ready/non-Completed quest + turn-ins (`:75-88`). Observation seam: `GameplayActor.Observe` (`GameplayActor.cs:222-288`, quests at :265). |
| resolve-objective (act type → pursuit) | **NO production owner.** `LevelingLoopScenario.PursueObjectives` (scenario-only) | Production `QuestBehavior` never reads Progress act templates — `AdvanceQuest` blindly runs the step machine (`GameplayActor.cs:863-879`). The ONLY type→leg resolver is `PursueObjectives` (`LevelingLoopScenario.cs:813-819`, dispatch :830-1070), reachable only via `RunAsScenario` ← `BotScenarioRunner` (`BotScenarioRunner.cs:170-171`) ← headless `BotDriveBridge` (`BotDriveBridge.cs:1187`). |
| ItemGather→corpse-loot source resolution | **NO owner anywhere** | `GatherLeg` resolves DOODAD sources only: highlight id, else seed-herb special-case, else doodad func-chain scan (`LevelingLoopScenario.cs:2259-2316`); 251 has highlight NULL→0 and no granting doodad → fail-closed "missing gather-source resolution primitive" (:2311-2315). Data to build it exists: `ItemManager.GetLootPackIdByNpcId` (`ItemManager.cs:122-123`), `LootGameData.GetPack` (`LootGameData.cs:175-178`). |
| find-hostiles (production) | `BotRoamStepExecutor` wildlife loop | `NearbyNpcProvider ?? WorldManager.GetAround<Npc>` @ 45 m (`:838-840`, radius :109-110) → nearest passing `IsAttackableWildlife` (`:842-855`; gate :2325-2348 incl. faction-115 short-circuit :2331-2332). **Quest-blind** — no template filter. Gated off while any quest/need/homestead leg lands work (`:711`). |
| find-hostiles (scenario) | `LevelingLoopScenario.SelectHuntTarget` | Observe-driven (`NearbyNpcObjIds`), alive + template/group/zone match + `CanAttack`, nearest (`:2842-2893`). Hunt/group/zone/level-grind match modes (:2858-2868). |
| find-hostiles (scenario rig) | `AdventurerSpikeScenario` SelectHostile primitive | Nearest alive attackable, optional template filter (`AdventurerSpikeScenario.cs:761-790`). |
| quest-valid-target filter | scenario-only (`SelectHuntTarget`) | Production has NO quest→template filter on any scan. |
| choose-target | roam: nearest attackable (`:849-854`); hunt leg: nearest matching (`:2884-2890`) | Same argmin shape twice — duplication (C.2-D1). |
| assign-target (SetTarget) | `GameplayActor.SetTarget` (`GameplayActor.cs:538-545`) — canonical | `HuntLeg` uses it (`LevelingLoopScenario.cs:2694-2699`, refusal fails closed). The roam loop BYPASSES it: writes `Character.CurrentTarget` + `SCTargetChangedPacket` directly (`BotRoamStepExecutor.cs:769-773, 865-866`) — seam bypass (C.2-D2). |
| combat initiation (production) | two shapes, no single owner | Roam: `AutoAttack` (:806) + `Cast` (:820) picked by `CombatDecisionTree.SelectPrioritizedSkill` w/ `InferRole` (`:775-816`). Hunt leg: `SetTarget` → `CombatDecisionTree.Evaluate` (flee/kite/close-gap, :2702-2738) → `AutoAttack` → cast rotation → `Loot` (:2746-2818). `CombatExecutor` (`CombatExecutor.cs:54`) is TEST-ONLY (zero production references; only `CombatExecutorTests.cs`). — duplication (C.2-D3). |
| hostile predicate | `CombatDecisionTree.IsHostileTarget` (`CombatDecisionTree.cs:73+`) | Shared by GOAP (`BotWorldStateProvider.cs:206-207`, `TargetIsHostile` flag) — the fourth spelling of "hostile" (C.2-D4). |
| quest travel / target selection | `BotRoamStepExecutor.ArmQuestTravel` / `ResolveQuestTravelTarget` (`:1271-1358`) | Handles ONLY Ready-reporter-spawner (:1310-1327) + nothing-active discovery (:1334-1355). With 251 ACTIVE in Progress → returns null → `"no walkable quest target"` (`:1281`). No progress-objective travel exists. |
| GOAP quest/combat state | `BotWorldStateProvider` — quest-blind | Only NPC reference is the hostility flag (:206-207) (contracts §2.1; re-grepped this pass). |
| quest helpers | `QuestManager.GetTemplate` (:61-64), `QuestTemplate.GetComponents(kind)` (`QuestTemplate.cs:33-38`), `CheckGroupNpc` (:136-139), `GetGroupItems` (:114-117) | All exist and are used by the scenario legs; production quest leg uses `GetTemplate` only for turn-in reporter resolution (`QuestBehavior.cs:288-322`). |
| 251-specific handling (production) | NONE | 251 knowledge exists ONLY in harness: unit-rig header+constants (`GameplayActorQuestActionsTests.cs:20-22, 36-40`), route spec (`M1M2ReplayScenario.cs:93-96`), pilot probe (`PilotProbeTests.cs:58`), E2E defect seed (`E2eStack.cs:761-764`). |
| decision policy (band/cap/priority) | `QuestDecisionScenario.QuestOptions` (`QuestDecisionScenario.cs:31-52`: band [1,9], take-3, turn-in 30 / advance 20 / accept 10) | Quest leg only — no objective/target policy exists. |

### C.2 Duplication flagged (explicit)

- **D1 — two nearest-hostile selections**: roam `bestNpc` loop (`BotRoamStepExecutor.cs:842-855`) vs `SelectHuntTarget` (`LevelingLoopScenario.cs:2846-2890`). Same argmin-over-perceived-NPCs shape; different filters (none vs quest-template), different radii (45 m vs Observe 25 m), different homes (production executor vs scenario).
- **D2 — target assignment bypass**: `GameplayActor.SetTarget` (canonical, idempotent, audited) vs roam-loop direct `Character.CurrentTarget` writes + manual `SCTargetChangedPacket` (`:769-773, :865-866`). Same duplication family as the G3-era direct-mutation findings; the party-follow path also uses `actor.SetTarget` (`:571`), so the roam hunt loop is the odd one out.
- **D3 — two combat-initiation shapes**: roam inline AutoAttack+Cast (`:804-831`) vs HuntLeg SetTarget→Evaluate→burst→Loot (`:2694-2821`). `CombatExecutor` sits unused in production. Any G4 combat consumer must pick one — HuntLeg's is the only quest-credited one.
- **D4 — four "hostile?" spellings**: `IsAttackableWildlife` (faction-115 short-circuit + CanAttack + Hostile relation, :2325-2348), `SelectHuntTarget`'s `CanAttack`-only check (:2868), `CombatDecisionTree.IsHostileTarget` (:73+), AdventurerSpike's `CanAttack` filter (:786). For 3475 (faction 115, aggression f) all four agree "attackable" TODAY, but they can disagree on other templates (e.g. neutral-but-attackable) — a G4 selector must name exactly one.
- **D5 — two observation radii on the quest path**: 25 m Observe/discover vs 45 m hunt scan. From the G3 end state (at giver 3512) the nearest data-cited 3475 is ~60 m out [INFERENCE, §A.4] — perceivable by NEITHER. G4 identification can still be data-driven (template 3475 + spawner positions), but live *selection* needs travel (out of G4 scope) or a perception extension.

### C.3 What G4 must build (read-out, not a plan)

1. **Objective reader (production)**: Progress acts of 251 via `QuestManager.GetTemplate(251).GetComponents(Progress)` → act 10473 = ItemGather(4058×3). No owner today (C.1 row 2).
2. **Item→NPC resolver**: 4058 → pack 4530 (`loots`) → template 3475 (`loot_pack_dropping_npcs` / `ItemManager.GetLootPackIdByNpcId`). No owner today (C.1 row 3) — the single genuinely missing primitive.
3. **Quest-valid selector**: template-3475 + alive + `CanAttack` over perceived NPCs (one of the D4 spellings, named), assign via `GameplayActor.SetTarget` (not the D2 bypass). Ends at selected — the HuntLeg/D3 machinery and `ArmQuestTravel` stay out of scope.
