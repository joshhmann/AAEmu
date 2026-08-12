# Crafting Mechanic — Canonical 1.2 Behavior + Data (CRAFT-01 dossier, feeds M4-A)

**Task:** t_b2529186 (mechanic-research lane, dossier; feeds M4-A t_d957e80d crafting integrity via phase parent t_8b13508b)
**Branch:** crafting-dossier (base: fork develop @ a31826b74)
**Date:** 2026-08-11
**Scope:** evidence only, no code changes. Josh mandate 2026-08-11: wiki/forum sources cited (source + date), cross-checked vs data files, flagged **data-verified** vs **research-derived**. Ground truth: joshhmann fork develop @ a31826b74 (crafting paths byte-identical to upstream AAEmu/AAEmu develop — CharacterCraft.cs diff = 0 lines); canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (r208022, 679 tables). Every table/row count below was re-verified from the sqlite file on 2026-08-11.

---

## 1. TL;DR

Canonical 1.2 crafting is a **single craft packet + skill-cast pipeline** with four data layers:

- **Recipes** live in `crafts` (7,010 rows): each recipe is a skill cast (`crafts.skill_id` → `skills`, which carries the **labor cost** `consume_lp`, cast time, cooldown, proficiency group), optional **workstation requirement** (`req_doodad_id`, 2,366 crafts / 94 distinct workstations), and an optional **proficiency gate** (`actability_limit`, 1,222 crafts, values 25–75,000 — e.g. bulk recipes at 50,000).
- **Materials** = `craft_materials` (23,475 rows): `item_id` + `amount`, consumed **at craft completion**. `main_grade` is **never set in 1.2 data** (0 rows) and `require_grade` only 14 rows — the "proper" grade-inheritance machinery is absent from 1.2 data.
- **Outputs** = `craft_products` (6,938 rows): `item_id`, `amount`, `rate` (90 rows < 100% — **chance-based products**, currently ignored by the engine), `item_grade_id` (493 fixed-grade products). Trade packs are a subset flagged via `craft_pack_crafts` (4,130 rows).
- **Workstations** are doodads: 25 `DoodadFuncCraftStart` + 304 `DoodadFuncCraftPack` funcs attach craft lists (1,379 `craft_start_crafts` + 4,130 `craft_pack_crafts` rows) to ~150 doodad templates (anvil, furnaces, looms, zone pack benches…). **The per-recipe requirement is `crafts.req_doodad_id`, not the pack attachment** (pack lists are client UI lists and are demonstrably inconsistent with `req_doodad_id` across workstations).

**The fork (== upstream develop on every crafting path) enforces almost none of this server-side.** `CSExecuteCraft` → `CharacterCraft.Craft` checks: backpack slot free, material **counts** (across Inventory+Equipment+Bank — but consumption happens **only in Bag** → duplication vector), a partial doodad permission check, and proficiency `ActabilityLimit`. It **never** validates: the workstation (`req_doodad_id` is loaded but unused), proximity/range, that the doodad exists, or that the doodad offers the craft. Labor is consumed via the skill's `EndSkill` only when the craft succeeds; failure paths preserve materials and labor. `craft_products.rate`, `use_grade`, `craft_materials.require_grade` are loaded but unenforced; `DoodadFuncCraftStart/Act/Cancel/GetItem/Info/Pack` are all **empty stubs** (`DoodadFuncCraftDirect` is the only functional one). `SCCraftFailedPacket` (0x1bf) is never sent.

---

## 2. Canonical recipe data model (data-verified)

### 2.1 `crafts` — the recipe master table (7,010 rows)

| field | meaning | 1.2 data census |
|---|---|---|
| `id` | recipe id (client uses this in CSExecuteCraft) | 7,010 |
| `title` | KR 1.2 name | e.g. `목재` (lumber), `철 주괴` (iron ingot), `특산품: 솔즈리드 도토리 젤리` (Solzreed pack) |
| `cast_delay` | cast ms — **mirror of `skills.casting_time`** (verified: lumber craft 83 = 6000 ms == skill 14618 casting_time 6000; bulk lumber 5344 = 10000 == 22680) | loaded, unused (engine casts via the skill) |
| `tool_id` | tool requirement | **0 everywhere** (1.2 has no tool items) |
| `skill_id` | the skill cast that performs the craft (carries labor + timing + proficiency group) | 7,005 non-null; **5 NULL** (legacy `건축용 기계장치`/`기구용 기계장치` machine-part recipes, id 165–168, 2869 — uncraftable) |
| `wi_id` | world-interaction id (mirrored in `craft_effects`, 379 rows) | loaded, unused on craft path |
| `milestone_id` | — | 0 everywhere on crafts (loaded, unused) |
| `req_doodad_id` | **required workstation doodad template** | 2,366 crafts (34%); 94 distinct ids, **all present in `doodad_almighties`** |
| `need_bind` | — | **0 everywhere** |
| `ac_id` | actability category (alternate proficiency linkage) | loaded; usage commented out in `CharacterCraft` (139–152) |
| `actability_limit` | **proficiency gate** (points in `skills.actability_group_id` needed) | 1,222 crafts > 0; values 25…75,000; tiers incl. 1000/2500/5000/10000/20000 (anvil weaponry), 50000 (bulk), 75000 (41 crafts) |
| `show_upper_crafts` / `visible_order` / `recommend_level` | client UI flags | loaded, unused (display-only) |

Canonical samples (data-verified):

| craft | recipe | mats → out | work-station | labor | prof. gate | cast |
|---|---|---|---|---|---|---|
| 83 | 목재 (lumber) | 1 통나무 (log 8017) → 1 lumber (8337) | 558 목공 선반 (carpentry lathe) | **5** (skill 14618, group 18 Carpentry) | 0 | 6 s |
| 5344 | 목재: 대량 제작 (bulk lumber) | 30 logs → 10 lumber | 558 | **50** (skill 22680) | **50,000** | 10 s |
| 49 | 철 주괴 (iron ingot) | 3 철광석 (iron ore 8022) → 1 ingot (8318) | 557 용광로 (furnace) | **5** (skill group 11 Metalwork) | 0 | 6 s |
| 5347 | 철 주괴: 대량 제작 | 30 ore → 10 ingots | 557 | **50** | **50,000** | 10 s |
| 3 | 특산품: 솔즈리드 도토리 젤리 (Solzreed pack) | (pack mats) | none required (`req_doodad_id` 0 — zone bench is the UI list) | **60** (skill 16766, group 31 Commerce) | 0 | 6 s |

### 2.2 `craft_materials` (23,475 rows)

- `item_id` + `amount`; `main_grade` **0 rows set** (the 1.2 data never marks a grade-bearing material), `require_grade` only **14 rows** (>0). The engine's `CraftMaterial` model loads `MainGrade` but **not `require_grade`** (`CraftManager.cs:90–94`).
- Consumed at completion, `amount` units per craft, from the **bag container only** (see §4).

### 2.3 `craft_products` (6,938 rows)

- `item_id` + `amount`; `rate` = **chance %** — 90 rows < 100% (all <100), e.g. crafts 2900–2940 (50%: item pairs 5704/5703/5702, 5629/5627/5628…), 2956–2957 (50%) — **not applied by the engine** (always drops).
- `use_grade` **0 rows set**; `item_grade_id` > 0 on **493 rows** (fixed-grade products, e.g. honor-item crafts at the anvil: `힘의 모험가 대검` etc.).
- 5 crafts have >1 product row (7074 ×3, 7075 ×3, 7076 ×5, 7077 ×13, 7079 ×2 — test/bundle crafts).
- `craft_pack_crafts` (4,130 rows / 150 `craft_packs`) marks pack recipes; engine sets `IsPack` and uses `ResultsInBackpack` (any product is a `BackpackTemplate`).

### 2.4 Workstations — doodads, `req_doodad_id`, and the craft-list tables

- Workstation templates are `doodad_almighties` rows (the 1.2 data has no `workstations` table; AAEmu loads doodad templates from `doodad_almighties`, `DoodadManager.cs:2640–2687`). Named 1.2 workstations (data-verified): 모루 anvil 520, 갑옷 제작대 521, 천 갑옷 제작대 522, 조리 기구 525, 목공 제작대 532, 용광로 furnace 557, 목공 선반 558, 암석 가공대 559, 가죽 세공 선반 560, 직조 선반 561, 공예품 제작대 564, 아키움 가공대 566, 연금 물품 제작대 568, 농부의 작업대 6090, 빛나는(shining) variants 1918/2235–2241, zone **특산품 제작대** pack benches 4220–4246 (one per zone, e.g. 4220 = Solzreed), 주택용 벽난로 (house fireplaces) 4916–4925, etc.
- Craft lists attach two ways:
  - `DoodadFuncCraftPack` funcs (304 rows) → `doodad_func_craft_packs` (150) → `craft_pack_crafts`. The pack name encodes its authoring bench (`4220.특산품 제작대: 솔즈리드 반도` = pack 55 → crafts 6205/6228 for 4220). **Pack lists are NOT consistent with `req_doodad_id`**: anvil 520's funcs point at pack 84 (`6090.농부의 작업대`, 64 crafts, all `req_doodad_id` 6090) and pack 3; a full cross-check of every pack func finds **969 craft rows whose `req_doodad_id` differs from the attached workstation**. So the client-side list (pack) and the server-side requirement (`req_doodad_id`) are separate facts — the server gate must be `req_doodad_id`.
  - `DoodadFuncCraftStart` funcs (25 rows) → `doodad_func_craft_starts` (25) → `doodad_func_craft_start_crafts` (1,379 rows; 48 distinct start ids referenced, **23 of which have no `craft_starts` row** — orphan data). Only **6** doodad templates carry a CraftStart func: 274 `ex) 제작` (dev), 291 `QA 제작`, 1309 `소형 범선 제작대` (small shipyard), 1692 `셀레스트의 약품 제작선반`, 1974 `아로마의 약탕기`, 2323 `휴대용 모닥불(테스트)` — i.e. dev/QA/special benches.
- Staged craft funcs (`DoodadFuncCraftAct` ×27, `DoodadFuncCraftGetItem` ×27, `DoodadFuncCraftInfo` ×23, `DoodadFuncCraftCancel` ×30) attach only to the same 6 special templates — the load-materials → process → collect flow exists in data but is **not exercised by 1.2's normal workstations** (normal benches are direct crafts). `doodad_func_craft_grade_ratios` (grade chance ratios) is **empty (0 rows)** in 1.2 data.

---

## 3. Recipe prerequisite semantics

**Canonical (data-verified + era sources):**
1. **Workstation requirement** — the recipe's `req_doodad_id` must match the interacted doodad's template. Period source: reddit r/archeage "Where can I find anvil to craft weapons?" (2014-07): *"Find an anvil. they are in almost all main town and quest hubs. You also need weaponry to be at 1000"* — corroborates both the workstation and the proficiency gate. [data-verified for the data side; the reddit post is research-derived corroboration]
2. **Proficiency gate** — `actability_limit` points in the recipe's skill proficiency group; client greys out recipes below it, server must re-check. Bulk recipes at 50,000 and honor recipes at 1,000–20,000 (anvil) are data-verified. Era guides describe profession ranks reducing labor+time and unlocking recipes (ArcheAge – Crafting Overview video, 2014-11-18: *"for each rank you achieve the amount of Labor required to perform that skill is reduced as well as the time taken to craft"*). [research-derived: rank names/values vary by patch; the fork's Actability step table (ranks at 0/10k/20k/30k/40k/50k/70k/90k, labor/time multipliers 1.00→0.77, `Actability.cs:10–26`, explicitly annotated "Values for 1.2") is fork-calibrated and should be treated as research-derived until verified against a period source]
3. **Skill cast** — the recipe's `skill_id` drives the cast (time, cooldown, labor, proficiency exp). `cast_delay` mirrors `skills.casting_time` (verified on lumber/ingot/pack recipes). Upstream #1327 "Fixed craft time calculation" (2026-03, merged in develop) made the engine use the skill cast — present in fork.
4. **Proficiency EXP** — labor spent grants proficiency (1 labor = 1 point era claim, "ArcheAge 101" video 2019 — research-derived; engine: `ChangeLabor` grants `ExpByLaborPower` formula exp + `AddPoint` actability points, `Character.cs:1558–1587`).

**Fork behavior:** proficiency gate enforced (`CharacterCraft.cs:114–137`, `ActabilityNotEnoughPoint` 508, incl. a housing actability bonus `GetActAbilityBonusFromHouse`); workstation requirement **NOT enforced** (§6 gap 1); skill cast used correctly; `ac_id` path disabled (commented out 139–152); 5 NULL-skill recipes would crash `GetSkillTemplate` (no guard in `Craft()` — `SkillManager.GetSkillTemplate(craft.SkillId)` at line 111).

---

## 4. Material consumption rules

**Canonical:** materials are consumed **at craft completion** (the 1.2 direct-craft flow: click craft → cast bar → product + materials removed together). Failure or cancel consumes nothing. [research-derived: period guides describe click→craft→product with no start-consumption step for workstation crafts; matches the fork's completion-time consumption. The client additionally blocks starting a craft when materials are missing (client-side count check against bag).]

**Fork behavior (data-verified, with defects):**
1. **Pre-check scope ≠ consumption scope → duplication vector.** Availability is checked with `Owner.Inventory.GetItemsCount(itemId)` which counts **Inventory + Equipment + Bank** (`Inventory.cs:196–247`, default `[Inventory, Equipment, Bank]`), but consumption happens only in the bag: `Owner.Inventory.Bag.ConsumeItem(...)` (`CharacterCraft.cs:323–326`), return value **ignored**. A player with the materials in bank/equipment passes the check, then `ConsumeItem` returns 0 (or partial) and the product is still granted → **free item creation**. (Also relevant: upstream issue #1337 "[BUG] Crafting does not always take the first available item" — picks first-found item, matters for graded gear.)
2. **Multi-craft queues only check materials once** — at `Craft()` start. A bulk count N re-verifies nothing per iteration; if materials run out mid-queue (they can't in-bag, but can if they were in bank per #1), remaining iterations still produce.
3. `require_grade` (14 rows) never loaded → grade-specific materials are consumed as any grade.
4. Order: **products are granted before materials are consumed** (283–321 then 323–326). Combined with #1, any product-acquisition failure mid-loop (multi-product crafts, backpack path) risks partial output with zero consumption.

---

## 5. Labor consumption values

**Canonical values (data-verified, the authoritative 1.2 numbers):** labor per craft = `skills.consume_lp` of `crafts.skill_id`:

- lumber 5, iron ingot 5, bulk lumber 50, bulk ingot 50, trade pack 60 — all match period sources (Specialty Workbench wiki: *"One needs 60 Labor Points to craft a pack"* — archeage-archive.fandom.com, accessed 2026-08-11 [research-derived corroboration]; note r/archeage's trade guide lists 80/60/180 for a *later* patch — labor values drifted post-1.2, so the data is authoritative).
- Distribution over all craft skills (data-verified): 100 labor ×3,599 crafts (most common), 250 ×670, **0 ×337**, 300 ×296, 60 ×185, 10 ×179, 25 ×165, 500 ×161, 400 ×153, 50 ×147, 20 ×127, 15 ×122, 1000 ×119, 800 ×113, 5 ×106, …
- **Labor is consumed once per craft, at skill end (`Skill.cs:1388–1405`), only if `!Cancelled` and `LaborPower >= cost`** → a failed/interrupted craft consumes **no labor** (canonical-consistent). Proficiency reduces cost via `Actability.GetLaborCostMultiplier()` (1.2-calibrated table, §3.2).
- Labor cap: 2,000 normal / 5,000 patron (`TimedRewardsManager.cs:13–14`) — matches period wiki (Fandom Crafting, archived 2023-04-27: "non-premium cap 2,000, premium 5,000"). [regen rates differ by patch — 5/10min vs 5/5min per 2014-11 sources — out of M4-A scope]

**Fork defects (data-verified code):**
1. `EndCraft`'s pre-check compares `Owner.LaborPower < ConsumeLaborPower` using the **unreduced** cost (`CharacterCraft.cs:168`) while `EndSkill` consumes the **reduced** cost → a mid-proficiency character with labor between reduced and unreduced cost gets a spurious `NotEnoughLaborPower` failure ("fictitious crafting step" debug message, line 170).
2. Labor is never reserved at craft start; a concurrent skill could drain labor mid-cast (minor race; client-side cast typically protects).

---

## 6. Output correctness

**Canonical (data-verified + research-derived):**
1. **Products** — `craft_products.item_id/amount` delivered on completion; trade packs auto-equip to the backpack slot (`ResultsInBackpack`, `TryEquipNewBackPack`; period source: "These packs are automatically placed on your back" — Fanatical Swordsman, 2014-11-11 [research-derived]).
2. **Chance products** — `rate` < 100% → roll per craft (90 product rows). **Not implemented** in fork (all products always granted).
3. **Grades** — 1.2 has no `main_grade`/`use_grade` flags in data; fixed `item_grade_id` (493 rows) is the only data-driven grade rule. Grade **inheritance** for equipment crafts (crafting a higher-tier item from a graded base carries the grade and can "proc" +1) is era-documented: reddit r/archeage 35srai (2015-07): *"Increasing your proficiency in crafting also increases the chance for your crafted items to proc a higher grade"*; Crafting & RNG video (2014, 1.2-era): one chance per upgrade step, needs prior grade. The fork implements upstream #1336's **"Improper Heuristic"** (`CharacterCraft.cs:260–321`): first *equipment* material found in bag → inherit its grade + 5% free-regrade roll; else `item_grade_id`; else default. This is **research-derived** (no 1.2 data table backs the 5% — `doodad_func_craft_grade_ratios` is empty) and is the least-canonical part of the pipeline; M4-A should keep it config-gated per M4 doctrine.
4. **Quest events** — `QuestManager.DoOnCraftEvents` fires per completed craft (`CharacterCraft.cs:337`); data: `quest_act_obj_crafts` 345 rows (crafting objectives).

**Fork defects:** `rate` ignored; `AcquireDefaultItem` result unchecked on the bag path (silent item loss if the slot check passes but add fails — partial stacks/grade splits); multi-product crafts with a pack product can partially complete (products loop breaks on pack-equip failure after earlier products already granted, `CharacterCraft.cs:308–320`).

---

## 7. Workstation range/ownership enforcement

**Canonical (research-derived where noted):**
1. **Range** — the client only allows crafting near the workstation (interaction range; period guides: "find an anvil… in almost all main town and quest hubs" — reddit 2a6at8, 2014-07). The server must re-validate because the client sends only a doodad objId. [range value research-derived; no 1.2 table encodes interaction range — the doodad's `sim_radius`/`use_target_decal` fields are visual]
2. **Workstation type** — `crafts.req_doodad_id` must equal the doodad's template (data-verified, §2.4).
3. **Ownership/perm** — public benches (town anvils, zone pack benches) are usable by anyone; player-placed benches (house fireplaces 4916–4925, tradesman benches) follow house permission (private/family/guild/public). Period source (research-derived): AAClassic Commerce wiki — Tradesman-manor packs require "find one set to public, or join a guild/family that has a private one". Data side: `doodad_funcs.perm_id` per func (`DoodadFuncPermission` enum: Any=0, OwnerOnly=3, SameAccount=6, ZoneResidents=8…), read via `Doodad.FuncPermission` (`Doodad.cs:109–120`).

**Fork behavior (data-verified):**
1. `Craft()` looks up the doodad by client-supplied objId (`ParentWorld.GetDoodad`) — **null doodad passes** (permission block only runs when `doodad != null`, `CharacterCraft.cs:53–93`), **no range check**, **no `req_doodad_id` check** (field never referenced outside the model/loader).
2. Permission switch is **incomplete**: `OwnerOnly` (3), `Permission1/2/4`, `OwnerRaidMembers` (5) fall through as **allowed**; only `SameAccount` (6) and `ZoneResidents` (8) are implemented; unknown values `throw ArgumentOutOfRangeException` (58–90).
3. All workstation funcs are stubs: `DoodadFuncCraftStart.Use`, `DoodadFuncCraftAct`, `DoodadFuncCraftCancel`, `DoodadFuncCraftGetItem`, `DoodadFuncCraftInfo`, `DoodadFuncCraftPack` are empty/no-op (`Logger.Trace` only); `DoodadFuncCraftStartCraft.Use` returns false. `DoodadFuncCraftDirect` (sets `OverridePhase`) is the only functional one.
4. The `1,379` craft-start / `4,130` pack-craft links are loaded into DoodadManager but never consulted by the craft path — a client could craft *any* recipe at *any* doodad (or no doodad).

---

## 8. Inventory-full behavior

**Canonical:** the client blocks starting a craft when the result cannot fit (message `not_enough_space`); nothing is consumed. [research-derived — era-era common knowledge corroborated by the client error string; the engine's behavior matches this direction]

**Fork behavior (data-verified):** at `EndCraft` (completion), `FreeSlotCount(SlotType.Inventory) < CraftProducts.Count` → `SendErrorMessage(CraftCantActAnyMore 148, NotEnoughSpace 436)` → `CraftOrCancel()`: **no materials consumed, no labor consumed** (materials are consumed after the product loop; labor consumed only in `EndSkill` with `!Cancelled`), and queued crafts **retry** every cooldown (spammy but self-healing). Defects:
1. Check is per **product row**, not stack-aware (`AcquireDefaultItem` may still fit partial stacks — conservative false-rejects) and ignores the backpack slot path (packs bypass bag space).
2. If the check passes but acquisition fails (race with another task), the bag-path `AcquireDefaultItem` failure is **unchecked** → silent loss (or, with multi-product crafts, partial grant).
3. The canonical client error surface is `craft_cant_act_any_more` (148) + sub-code (`not_enough_space` 436, `not_enough_required_item` 722, `not_enough_labor_power` 29, `actability_not_enough_point` 508, `craft_permission_deny` 135, `backpack_occupied` 315) — the fork sends exactly this pattern; `SCCraftFailedPacket` 0x1bf exists but is **never sent** ("TODO needs fixing").

---

## 9. Wire surface

- **`CSExecuteCraft` 0x0f8** (C2G): `craftId u32, objId Bc, count i32` → `CharacterCraft.Craft` (the only craft trigger). `count` = bulk quantity (client loops per cooldown).
- **`CSSetCraftingPayPacket` 0xfff** — unmapped TODO (craft auto-pay option).
- **`SCCraftFailedPacket` 0x1bf** (G2C): defined, never sent.
- Errors: `ErrorMessageType.cs` — 29 `not_enough_labor_power`, 135 `craft_permission_deny`, 148 `craft_cant_act_any_more`, 315 `backpack_occupied`, 436 `not_enough_space`, 508 `actability_not_enough_point`, 722 `not_enough_required_item`.
- Craft completion rides the normal skill packets (`SCSkillStarted/Fired/Ended`); `SCCharacterLaborPowerChangedPacket` carries the labor delta; item changes ride `SCItemTaskSuccessPacket`.

---

## 10. Engine gaps — fork vs upstream (fork == upstream, diff 0)

| # | Gap | Fork evidence (develop @ a31826b74) | Upstream status |
|---|---|---|---|
| 1 | **No workstation (`req_doodad_id`) validation** | `Craft.ReqDoodadId` loaded (`CraftManager.cs:36`) but zero references outside model/loader; `Craft()` never compares doodad template vs recipe | same (no upstream fix) |
| 2 | **No range check** | `Craft()` accepts any world doodad objId; `CSStartInteractionPacket` has explicit `// TODO: Distance-check`; craft path has no distance query | same |
| 3 | **Null/bogus doodad accepted** | `doodad == null` skips permission block entirely (53–55) → craft without any workstation | same |
| 4 | **Material scope mismatch (dupe)** | count across Inventory+Equipment+Bank (196–247) vs consume in Bag only, return ignored (323–326) | same (related: upstream #1337 first-found-item) |
| 5 | **`rate` (90 rows) ignored** | `CraftProduct.Rate` loaded, never evaluated | same |
| 6 | **Grade inheritance is heuristic** | `FreeRegrade` 5% magic number (393–408); `use_grade` ignored; `main_grade` data absent | upstream #1336 merged (Jan 2026) — the heuristic IS the current upstream code |
| 7 | **Workstation funcs are stubs** | `DoodadFuncCraftStart/Act/Cancel/GetItem/Info/Pack` + `CraftStartCraft` no-op; craft lists (1,379/4,130 rows) loaded but unused | same |
| 8 | **Permission gaps** | `OwnerOnly`/`OwnerRaidMembers`/`Permission*` fall through as allowed; `SameAccount`/`ZoneResidents` implemented | same |
| 9 | **`require_grade` (14 rows) unloaded** | `CraftMaterial` has no field | same |
| 10 | **EndCraft labor pre-check uses unreduced cost** | 168 vs `EndSkill` 1390–1405 | same |
| 11 | **Product acquisition unchecked** | bag-path `AcquireDefaultItem` result ignored (310); pack path checked (314) | same |
| 12 | **5 NULL-skill crafts crash** | `GetSkillTemplate(craft.SkillId)` unguarded (111) | same |

Verdict: **the fork's crafting engine is byte-identical to upstream develop on every path in this dossier** (CharacterCraft.cs, CraftManager.cs, CSExecuteCraft.cs diffs = 0). All canonical enforcement for M4-A is new engine work; there is no hidden upstream implementation to port.

---

## 11. Edge cases

1. **Craft-without-workstation dupe** — client sends any (or zero-valid) objId; no doodad → no checks → mats from bag consumed, product granted, no proximity/type requirement. Canonical: `req_doodad_id` + range.
2. **Bank/equipment material dupe** (§4.1) — check passes on bank contents, consumption finds nothing in bag → free product per craft, repeatable in bulk queues.
3. **Chance products** (90 rows, e.g. 50% honor-item pairs 2900–2940) always drop in fork — trivial to fix once `rate` is applied.
4. **Bulk queue vs labor** — with `count>1` and reduced-cost proficiency, the `EndCraft` unreduced pre-check can kill a queue midway ("fictitious step" message per craft).
5. **Race conditions** — no lock around check-then-consume; two crafts (or craft + move) on the same character interleave via `CharacterCraft` state (single `CurrentCraft`/`Count` — a second `Craft()` call overwrites the first's queue state).
6. **House benches** — `GetHouseAtLocation` is consulted only for the actability bonus; the *permission* of a house bench is checked via `FuncPermission` (SameAccount/ZoneResidents only), never via `House.AllowedToInteract` — a house-owner's private bench is usable by strangers with `OwnerOnly`/default perms.
7. **Pack bench lists vs `req_doodad_id`** (§2.4) — 969 mismatched rows prove the pack attachment is UI data; M4-A must gate on `req_doodad_id` and treat pack lists as informational.
8. **Shipyard crafts** share the craft pipeline (`CraftEffect` wi-group Craft with a `Shipyard` target) — M4-A changes to `EndCraft`/`Craft()` must not break the shipyard branch (M4-3 lane).

---

## 12. Implications for M4-A (t_d957e80d) — design notes only

Canonical order of enforcement in `CSExecuteCraft`/`CharacterCraft.Craft`, before any cast begins:
1. **Doodad exists** (else reject) and **`doodad.TemplateId == craft.ReqDoodadId`** when `req_doodad_id > 0` (2,366 recipes); recipes with `req_doodad_id = 0` are workstation-free (data-verified: 4,644).
2. **Range check** — distance between player and doodad ≤ canonical interaction range (value research-derived; make it a named Config per M4 doctrine until a period source is found).
3. **Permission** — complete the `DoodadFuncPermission` switch (`OwnerOnly` → owner, `OwnerRaidMembers` → raid/party membership via `TeamManager`); house benches should additionally honor `House.AllowedToInteract`.
4. **Materials in bag only** (align check scope with consumption scope) — closes the dupe; keep the check at start *and* re-verify per iteration in `EndCraft` before granting products.
5. **Proficiency gate** (already present) + **labor reserve** (check reduced cost at start; consume reduced cost at end; keep no-labor-on-failure semantics).
6. **Inventory fit** — stack-aware fit for `CraftProducts` (bag + backpack-slot for packs) instead of per-row slot count; assert `AcquireDefaultItem` result and abort cleanly (no partial grant) — products-after-materials ordering, or rollback.
7. **`rate` roll** per product; keep `item_grade_id` fixed-grade; keep the grade-inheritance heuristic **behind a named Config** (research-derived, per Josh's exceptions-via-config rule).
8. Send canonical error ids (§9) on each rejection; consider finally wiring `SCCraftFailedPacket` 0x1bf for queue-cancel.
9. Guard the 5 NULL-skill crafts (skip with a logged warn, or treat as uncraftable).
10. Add the crafted-item quest event guard (`DoOnCraftEvents` already fires; verify 345 `quest_act_obj_crafts` objectives can actually complete).

---

## 13. Evidence appendix

**Data (compact.sqlite3 r208022, re-verified 2026-08-11):** `crafts` (7,010; schema §2.1; 5 NULL skill_id; 0 tool_id; 2,366 req_doodad_id / 94 distinct, all in `doodad_almighties`; 1,222 actability_limit, max 75,000; 0 need_bind), `craft_materials` (23,475; main_grade 0 rows; require_grade 14), `craft_products` (6,938; rate<100 ×90; use_grade 0; item_grade_id>0 ×493; multi-product ×5), `craft_packs` (150) + `craft_pack_crafts` (4,130), `doodad_func_craft_starts` (25) + `doodad_func_craft_start_crafts` (1,379; 48 referenced ids, 23 orphans), `doodad_func_craft_packs` (150 rows / 304 funcs), `doodad_func_craft_grade_ratios` (0 rows), `doodad_almighties` (workstation templates; anvil 520 = 78 crafts, tiers 1k/2.5k/5k/10k/20k; pack benches 4220–4246; farmer bench 6090), `doodad_funcs`/`doodad_phase_funcs` (craft func census: CraftStart 25, CraftAct 27, CraftCancel 30, CraftGetItem 27, CraftInfo 23, CraftPack 304, CraftDirect 123 phase funcs), `skills` (lumber skill 14618: casting 6000/cooldown 100/consume_lp 5/group 18; pack skill 16766: 6000/500/60/group 31; bulk 22680: 10000/50), `actability_groups` (11 metalwork, 18 carpentry, 31 commerce), `quest_act_obj_crafts` (345), `craft_effects` (379).

**Code paths (fork develop @ a31826b74; all files byte-identical to upstream AAEmu develop):** `Core/Packets/C2G/CSExecuteCraft.cs` (0x0f8); `Models/Game/Char/CharacterCraft.cs` (Craft 27–155, EndCraft 157–348, CancelCraft 371–387, FreeRegrade 393–408); `Models/Game/Skills/Effects/CraftEffect.cs` (EndCraft dispatch 86–91/147, shipyard 47–83, building 92–133); `Models/Game/Skills/Skill.cs` (labor 1388–1405, ApplyEffects→EndSkill order 858–867); `Core/Managers/CraftManager.cs` (loads 14–119; `require_grade` not loaded); `Core/Managers/UnitManagers/DoodadManager.cs` (craft func loads 741–868; templates from `doodad_almighties` 2640–2687); `Models/Game/DoodadObj/Funcs/DoodadFuncCraft*.cs` (stubs); `Models/Game/Char/Inventory.cs` (GetItemsCount 196–247 — default [Inventory, Equipment, Bank]; FreeSlotCount 805–811); `Models/Game/Items/Containers/ItemContainer.cs` (ConsumeItem 504+, AcquireDefaultItem 593+); `Models/Game/Char/Actability.cs` (1.2 multiplier tables 10–26); `Core/Managers/TimedRewardsManager.cs` (caps 13–14); `Models/Game/DoodadObj/Doodad.cs` (FuncPermission 109–120); `Models/Game/DoodadObj/Static/DoodadFuncPermission.cs`; `Models/Game/ErrorMessageType.cs` (29/135/148/315/436/508/722); `Core/Packets/G2C/SCCraftFailedPacket.cs` (0x1bf, never sent); `Core/Packets/C2G/CSOffsets.cs:232,289`; `Core/Packets/G2C/SCOffsets.cs:436`.

**Upstream:** commits `025df4b5d` (#1458 item skills), `e1c3bf4e0` "require all craft materials" (2026-06-04), `a13a639bf` (#1336 crafting grade inheritance, 2026-01-12 — the heuristic), `339aa021a` (#1327 craft time, 2026-03), `0c1cdb91a` (#1334). Open issues: #1337 (crafting takes first-found item, not first in bag — graded gear), #1427 (trade pack pickup broken after crafting). Fork CharacterCraft.cs diff vs origin/develop = 0 lines (verified 2026-08-11).

**Period / wiki sources (research-derived, cited + dated):**
- ArcheAge Fandom Wiki "Crafting" — labor cost on every craft action; caps 2,000/5,000 (page archived 2023-04-27, labor-regen text reflects 1.7+; caps consistent with 1.2). https://archeage.fandom.com/wiki/Crafting
- reddit r/archeage "Where can I find anvil to craft weapons?" (2014-07) — workstation requirement + weaponry 1,000 prereq. https://www.reddit.com/r/archeage/comments/2a6at8/
- reddit r/archeage "Does crafting proficiency increase the change of procing…" (2015-07) — proficiency raises crafted-grade proc chance (page fetch blocked 2026-08-11; snippet-only). https://www.reddit.com/r/archeage/comments/35srai/
- "ArcheAge – Crafting Overview" video (2014-11-18) — 21 professions; rank-ups reduce labor cost and craft time. https://www.youtube.com/watch?v=1s5uQKOjG8c
- "ArcheAge: Crafting & RNG" video (2014, 1.2-era) — crafted gear crit-proc per upgrade step, prior grade required. https://www.youtube.com/watch?v=DLd2RykWDHI
- The Fanatical Swordsman, "The Pros and Cons of Archeage: Crafting and Travel" (2014-11-11) — labor economy; packs auto-place on back. https://thefanaticalswordsman.com/2014/11/11/the-pros-and-cons-of-archeage-crafting-and-travel
- archeage-archive.fandom.com "Specialty Workbench" — "One needs 60 Labor Points to craft a pack" (accessed 2026-08-11 via search snippet; page fetch blocked). Matches data (60).
- r/archeage trade guide wiki — 80/60/180 labor per pack type: *later* patch values; shows post-1.2 labor drift. https://www.reddit.com/r/archeage/wiki/trade
- AAClassic wiki "Proficiency" / "Commerce" (wiki.aa-classic.com, accessed 2026-08-11) — proficiency reduces labor/time; tradesman-manor bench access "set to public, or guild/family". https://wiki.aa-classic.com/Proficiency

**Classification summary:** data-verified — all table counts, labor values, tier values, workstation model, error ids, code paths, fork==upstream. research-derived — proficiency rank names/regen rates, grade-proc formula (5% heuristic), interaction range value, cancel/inventory-full client presentation, house-bench permission mapping.

*Dossier only — no code changed on this branch.*
