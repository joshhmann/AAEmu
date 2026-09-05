# Q5 — NPC wildlife canonical skills data audit (Solzreed set) — dossier 2026-09-05

- Source HEAD: `dcb3ffe0c7a090c2760a9419ca2827873427bb48`
- Canonical DB md5: `78b3bdbf038db3b927056106efdf91af` (verified before AND after; unchanged)
- Date: 2026-09-05
- Evidence class: data/code only — per artifact: **A** (archaeology: read-only `compact.sqlite3` queries, `mode=ro`) for all verdict-table and loot-census rows; **R** (recorded: code shapes read, `NpcGameData`/`np_skills` loader + `Behavior.cs` filter/pick) for picker claims; **L**: none (no live run); **H UNKNOWN** (no human feel gate).

---

# Q5 — NPC Wildlife Canonical Skills Data Audit (Solzreed set)

## 1. Spawn-set enumeration

Source: `AAEmu.Game/Data/Worlds/main_world/npc_spawns_solzreed_wildlife.json` — 36 entries, `UnitId` (= `npcs.id`, verified against DB names) falls into exactly **4 templates × 9 spawns each**:

| UnitId (npcs.id) | Title in JSON | Name in DB (`npcs.name`) | Spawns | LEVEL | faction_id | ai_file_id |
|---|---|---|---|---|---|---|
| 172 | Plains Wolf | 평원 늑대 | 9 | 3 | 115 | 13 |
| 259 | Big Claw Boar Piglet | 큰발톱 새끼 멧돼지 | 9 | 2 | 115 | 13 |
| 3475 | Solzreed Boar | 솔즈리드 멧돼지 | 9 | 2 | 115 | 13 |
| 3492 | Solzreed Fox | 솔즈리드 여우 | 9 | 1 | 115 | 13 |

All four share `npc_template_id = 9`, `npc_kind_id = 2`, `npc_grade_id = 1`, `base_skill_id = 2`.

## 2. Verdict table (per-template)

Picker grounding (read, not changed): `AAEmu.Game/GameData/NpcGameData.cs:52` loads `SELECT * FROM np_skills`, keys by `OwnerId` (`SkillsForNpc`), binds via `BindSkillsToTemplate`; `AAEmu.Game/Models/Game/AI/v2/Framework/Behavior.cs:44-62,91-96` filters to off-cooldown (`!CheckCooldown`) and in-range (`MinRange..MaxRange`, or `Self` target), picks **uniform-random** (`skills[Random.Shared.Next(skills.Count)]`), and falls back to `BaseSkillId` when the list is empty. Melee hackfix at `Behavior.cs:99`: picked skill 2 at `targetDist > 4.0` returns `TooFarRange` (effective melee reach ~4 m despite `max_range`, see below).

| Template | `InCombat` np_skills rows (cond 0) | Other-condition rows | `BaseSkillId`-only? | Cooldowns / ranges | SkillUseParam1/2 | Verdict |
|---|---|---|---|---|---|
| 172 Plains Wolf | 0 | 0 (no rows at all) | YES — skill 2 only | skill 2: cooldown 300 ms, `min_range` 0 / `max_range` 25 (melee hackfix caps at ~4 m) | n/a (no rows) | **UNKNOWN** |
| 259 Boar Piglet | 0 | 0 (no rows at all) | YES — skill 2 only | same skill-2 stats as above | n/a (no rows) | **UNKNOWN** |
| 3475 Solzreed Boar | 0 | 0 (no rows at all) | YES — skill 2 only | same skill-2 stats as above | n/a (no rows) | **UNKNOWN** |
| 3492 Solzreed Fox | 0 | 0 (no rows at all) | YES — skill 2 only | same skill-2 stats as above | n/a (no rows) | **UNKNOWN** |

Rules applied: every live template has **zero** `np_skills` rows (table itself is populated — 7845 rows, `owner_type='Npc'`, keyed by `npcs.id`), so per the contract each gets verdict **UNKNOWN, never inferred** — even though the code fallback path (`pickedSkillId = BaseSkillId`) mechanically implies "single-attack with skill 2", that is code behavior, not canonical data, and is therefore NOT recorded as a data verdict. Likewise `SkillUseParam1/2` semantics are undocumented anywhere in the loaded path (params are read at `NpcGameData.cs:65-66` but nothing in `Behavior.cs`/`SkillManager.GetNpSkillTemplate` interprets them) — ambiguous by definition, reinforcing UNKNOWN.

Base skill 2 = 근접 공격 (melee attack): `cooldown_time=300` (units confirmed ms via `UnitCooldowns.AddCooldown` → `TimeSpan.FromMilliseconds`; note 250 ms grace in `CheckCooldown`, `UnitCooldowns.cs:28`), `casting_time=0`, `target_type_id=4`, `ignore_global_cooldown='f'`. (`SkillManager.GetNpSkillTemplate` dungeon `InCombat` filter is irrelevant here — zero rows to filter.)

## 3. Loot-pack census (per template)

| Template | `loot_pack_dropping_npcs` rows (`npc_id → pack`, default?) | Pack contents found in `loots` / `loot_groups` / `loot_actability_groups` |
|---|---|---|
| 172 Plains Wolf | **none** | — |
| 259 Boar Piglet | **none** | — |
| 3475 Solzreed Boar | 4530 (default `t`); 1616 (`f` ×2 rows); 8055 (`f`) | 4530: 1 loot row — item **4058 솔즈리드 멧돼지 고기** (Solzreed Boar Meat), group 0, drop_rate 10000000, 1–1, always_drop `f`; no group/actability rows. 1616: **NO content rows anywhere** (unresolved, see OQ-1). 8055: loot row item **29203 농민의 주머니**, group 1, drop_rate 1, 1–1 + `loot_groups` (829, 8055, 1, **2000000**, 0); no actability rows. |
| 3492 Solzreed Fox | 1763 (`f` ×2 rows); 8055 (`f`) | 1763: **NO content rows anywhere** (unresolved, see OQ-1). 8055: same shared-pack content as above. |

Notes: 8055 is a widely-shared pack (hundreds of npc_ids reference it). The doubled rows (3475→1616 twice: ids 5163/6851; 3492→1763 twice: ids 5180/6868) are distinct row ids with identical npc+pack — recorded as observed, not interpreted. `np_passive_buffs` and `npc_initial_buffs` are empty for all four templates. Sibling cross-check: 1616 is also referenced by npc 4033, 1763 by npc 4050 — equally content-less, so the gap is pack-side, not template-side.

## 4. Repro queries (all read-only, `mode=ro`)

```sql
-- skill rows per template (all returned 0; table has 7845 rows total, owner_type='Npc')
SELECT COUNT(*) FROM np_skills WHERE owner_id IN (172, 259, 3475, 3492); -- per-id counts also 0
SELECT * FROM np_skills WHERE owner_id IN (172,259,3475,3492);           -- empty set
SELECT DISTINCT owner_type FROM np_skills;                                -- ('Npc',)
-- template base data
SELECT id, name, npc_template_id, base_skill_id FROM npcs WHERE id IN (172,259,3475,3492);
-- base skill stats
SELECT id, name, cooldown_time, casting_time, min_range, max_range, target_type_id, ignore_global_cooldown
  FROM skills WHERE id = 2;
-- loot census
SELECT * FROM loot_pack_dropping_npcs WHERE npc_id IN (172,259,3475,3492);
SELECT id, "group", item_id, drop_rate, min_amount, max_amount, loot_pack_id, grade_id, always_drop
  FROM loots WHERE loot_pack_id IN (4530,1616,1763,8055);
SELECT * FROM loot_groups WHERE pack_id IN (4530,1616,1763,8055);
SELECT * FROM loot_actability_groups WHERE loot_pack_id IN (4530,1616,1763,8055);
SELECT id, name FROM items WHERE id IN (4058, 29203);
SELECT * FROM np_passive_buffs WHERE owner_id IN (172,259,3475,3492);    -- empty
SELECT * FROM npc_initial_buffs WHERE npc_id IN (172,259,3475,3492);     -- empty
```

Code refs: `NpcGameData.cs:52-73,159-162,236-244`; `Behavior.cs:44-62,91-120`; `SkillManager.cs:227-241`; `SkillUseConditionKind.cs` (`InCombat=0 … OnAlert=7`); `UnitCooldowns.cs:13-31` (ms + 250 ms grace).

## 5. Integrity — md5 before/after (contract: 78b3bdbf038db3b927056106efdf91af)

- Before: `78b3bdbf038db3b927056106efdf91af` ✔ matches contract
- After:  `78b3bdbf038db3b927056106efdf91af` ✔ unchanged (all SQLite opens were `mode=ro`; **no repo files written** — `git status` shows only pre-existing user modifications under `scorecard-explorations/` plus untracked `.worktrees/`, none mine)

## 6. Open questions (for loot-proof follow-up / designers)

- **OQ-1:** Packs **1616** (boar) and **1763** (fox) have zero rows in `loots`, `loot_groups`, and `loot_actability_groups`. Where is their content defined (server code? second DB? quest-gated drops)? This is the key input the loot-proof task must resolve — do these mobs drop *nothing* from these packs today?
- **OQ-2:** `drop_rate` scale unverified (is 10000000 = 100%? what does 2000000 on group 8055/1 or drop_rate 1 on item 29203 mean mechanically, with group/dice resolution?). Not inferred.
- **OQ-3:** `SkillUseParam1/2` semantics are dead weight in the loaded path (read, never interpreted) — confirm with 1.2 retail captures whether params ever gated wildlife skill choice before relying on them.
- **OQ-4:** 172 (wolf) and 259 (piglet) carry **no loot packs at all** — intended (non-lootable critters) or data gap? Flag for loot-proof task, not decided here.
