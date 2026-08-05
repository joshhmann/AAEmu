# NPC Visual Behavior — float / sit-pose / cloth / targeting (Recon B)

**Author:** Tai (evidence: hx-researcher, t_52ebb23f) · **Date:** 2026-08-04
**Scope:** NO code changes — data + hypotheses for the playtest findings (Josh 2026-08-04:
NPCs floating, sit poses "knees in", cloth physics freakouts, odd targeting; Solzreed +
"Bluemist Forest" (= Gweonid Forest, 1.0 name) and the Solzreed→Gweonid route).
**Companion:** `scorecard-explorations/npc-pathing.md` (Recon A — walking-into-hills, server-side movement).

## Evidence sources

| Source | Location | Notes |
|---|---|---|
| game_pak (24.8 GB, 218,069 entries) | aaemu box `/root/AAEmu/.server_files/AAEmu.Game/ClientData/game_pak` | header+FAT AES-128-CBC (XLGames key); file data raw. Heightmap + navmesh extracted with a scratch reader (python/pycryptodome, run on the box; script: workspace `recon_b_extract.py`/`recon_b_bai.py`) |
| `game/worlds/main_world/cells/NNN_NNN/client/terrain/heightmap.dat` | in game_pak | per-cell terrain grid (512×512 @ 2 m/unit; Hmap format incl. 33×33 node upscale; 85 empty cell-bbox placeholder nodes per file MUST be filtered before sector indexing — mirrors `WorldCell.LoadCellHeightMapFromClientData`) |
| `game/worlds/main_world/world.xml` | in game_pak | zone→cell/sector rects; zone ids here are **zone_key** (Solzreed=142/178/179, Gweonid=129/181/182) ≠ sqlite `zones.Id` (9/124/125, 1/127/128) |
| `game/worlds/main_world/paths/NNN_NNN/{net,verts}mission*.bai` | in game_pak | navmesh nodes (world-space after +path origin ×256); per-cell 4×4 path folders; **no zone-level .bai in the pak** (zone/ folders hold only xml) |
| `compact.sqlite3` (119 MB) | box `.server_files/AAEmu.Game/Data/` | npcs / npc_postures / npc_posture_sets / system_factions / system_faction_relations; **no `npc_models`/`npc_aggros`/`npc_factions` tables** in this build |
| `npc_spawns.json` (main_world, 25,118 rows) | box Data/Worlds (md5-identical to repo copy) | **JSONC** — inline `//` comments + trailing commas; stdlib JSON fails, server's JsonHelper tolerates |
| Runtime config | box `Configurations/World.json` | `GeoDataMode=true`, `PreLoadTerrain=false` (heightmaps load on demand per cell) |

Validation of the heightmap reader: spawn Z vs terrain Z agrees within 1 m for **93–97 % of spawns**
(zone 142: 1024/1098; zone 129: 1058/1085) — the data pipeline is sound; only the listed rows are anomalous.

Zone coverage: spawns exist in cells of zones 142 (w_solzreed_1) and 129 (w_gweonid_forest_1) only;
zones 178/179/181/182 share cells with those two (cell-level first-match used; their spawn counts
therefore fall under 142/129). 2,183 target-zone spawn rows analyzed across 18 heightmap cells.

---

## 1. FLOATING

### 1.1 What the server does with spawn Z

`NpcSpawnerNpc.SpawnNpc` (`AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs:97-104`):

```csharp
if (!npc.CanFly) {
    var newZ = ParentWorld.Template.GeoData.GetHeight(spawnerPos);   // nearest .bai navmesh node Z (or heightmap fallback)
    if (Math.Abs(spawnerPos.Z - newZ) < 1f) spawnerPos.Z = newZ;     // snap ONLY if within 1 m
}
```

- `GetHeight` (`AiGeodataManager.cs:259-333`) = Z of the **nearest .bai navmesh node** (netmission/vertsmission
  nodes across the cell's 16 path folders), falling back to the raw heightmap (`WorldTemplate.GetRawHeightMapHeight`).
- `CanFly` NPCs skip the check entirely — **all flying spawns keep their JSON Z unconditionally**.
- Consequence: a spawn whose JSON Z deviates ≥ 1 m from the geodata floor is **never corrected** — it keeps
  the data Z for life (idle, aggro, return). The float you see in-game IS the data Z minus the client's rendered terrain.

### 1.2 Census results (spawn Z vs heightmap terrain, |diff| buckets)

| Zone | Spawns | float ≥1 m | ≥2 m | sink ≤−1 m | mean \|diff\| | Z=0 rows |
|---|---|---|---|---|---|---|
| 142 w_solzreed_1 | 1,098 | 63 (5.7 %) | 58 | 11 (1.0 %) | ~2.0 m | 0 |
| 129 w_gweonid_forest_1 | 1,085 | 26 (2.4 %) | 20 | 1 | ~1.3 m | 0 |

99 rows (35 distinct NPCs) exceed ±1.5 m. They fall into four explainable classes:

**(a) Structure-floor offsets (NOT visible floats) — the castle cluster.** ~20 rows at
(12720, 16250) — 정예 근위병 10320 (8 rows), 공작 성 하인 엠마 10636, 콜린 12037, 방탕한 음유시인 12038,
거리의 장미 12039, 밤의 요정 12040, 그라일렌트 5925, 집사 이언스 5926, 공작 부인 에스텔 6990, 조이스 6992 —
all a very consistent **+4.5…+4.8 m**; and at (15560,13780): 론반 공작 3615 +4.1, 엘렌 공주 3616 +3.5,
헤라온 3617 +3.9; guards (경비병 3560 +10.4, 감시병 8176 +10.3, 왕좌 근위병 8771 +9.8) at (15500,13760).
The terrain heightmap is bare ground; it cannot see castle floors/ramparts. The constant offsets look like
floor heights, not data errors. **Confirm in-game: these NPCs stand on the castle floor, not in the air.**

**(b) Flyers (expected — CanFly skips the snap).** 독수리 1852 (22 rows, +40 m), 큰 바다 벌레 8563 (+69 m,
sight 4.0 — a sea creature), 먼바다 가시부리새 8616 (+100 m), 육식성 말벌 3451 (+12.5 m), 아로라라 1904 (+44.9 m).

**(c) Cave/underground mobs (expected — surface heightmap).** 동굴 거미 1900 (−148 m), 거미 군주 5922 (−117 m),
미쳐버린 광부 1881 (−120 m), 마법사 에르딜 6973 (−120 m), 다후타 교단 신관 1880 (−187 m). All in the
mountain block x 11800–13400, y 16300+ — cave interiors are below the terrain surface.

**(d) True data-defect floaters (the report's "sometimes float").** The strongest candidates are **non-flying,
open-ground NPCs**:
- 에노이르 569 (+91 m @ 10571,14718) and 에오카드 3672 (+89.8 m @ 10572,14719, 13 rows) — elf-faction
  (103) NPCs in Gweonid/Bluemist, model 16, NOT flyers. These hover ~90 m up. Prime "floating NPC" evidence.
- The +1.7…+1.8 m cluster at (10505,14860): 네서틴 576, 알리아 3688, 헤이스 6541, 티티라라 7733 — small
  consistent offset (porch or true float; needs in-game check).

### 1.3 Hypotheses (evidence→confirm)

| # | Hypothesis | Evidence now | Evidence to confirm |
|---|---|---|---|
| F1 | Spawn Z is a data defect on specific rows (not a systematic heightmap problem) | 93–97 % of rows sit on terrain; anomalies cluster per-NPC (e.g., 3672 ×13 rows all ≈ +90 m) | In-game: are the floaters exactly 569/3672-class NPCs, standing in mid-air at a fixed spot? |
| F2 | Floating NPCs are the same ones with bad Z in `npc_spawns.json` — the 1 m snap threshold never corrects them | NpcSpawnerNpc.cs:97-104; floaters' JSON Z vs navmesh Z also ≥1 m | /save or DB dump of a floater's runtime Z = JSON Z (never snapped) |
| F3 | Model anchor offset — ruled OUT as primary cause | offsets are per-NPC-consistent but vary 1.7…90 m; anchor offsets are per-model, not per-spawn-row | If anchor were the cause, ALL NPCs of one model would float equally |
| F4 | Navmesh-vs-heightmap disagreement shifts the snap | 74–81 % of spots agree within 1 m; 73–82 % of spawns within 1 m of a navmesh node | None needed — mechanism documented; real fix would snap to client terrain, not navmesh |

---

## 2. SIT POSES ("knees in")

### 2.1 What the server sends

- Per-NPC `npc_posture_set_id` → rows in `npc_postures` (anim_action_id, talk_anim, start_tod_time).
  689/1,050 target-zone NPCs have a posture set; **34 sets used in these zones contain sit animations**.
- `Npc.AnimActionId` (`Npc.cs:40-56`) picks the row whose `start_tod_time <=` current game time.
- Sent to clients two ways:
  1. `SCUnitStatePacket` on visibility (`Npc.AddVisibleObject`, `Npc.cs:1058-1063`) — includes
     `Unit.ModelPosture` → byte postureType + byte isLooted + **uint animActionId** + bool activate
     (`Unit.cs:1022-1060`). **That id alone drives the client pose — no sub-pose/params.**
  2. `SCUnitModelPostureChangedPacket` on time-of-day change (`TimeManager.cs:94-119`).
- `talk_anim` (e.g. `fist_pos_sit_chair_talk`) is loaded but **never sent** — only the numeric id matters.

### 2.2 The data (sit anim ids in use in Solzreed/Bluemist)

| posture set | NPCs in zone | anim id | talk_anim (data label) |
|---|---|---|---|
| 22 | 33 | 87 | fist_pos_sit_chair_nursery_dealer_talk |
| 224 | 20 | 223 | fist_pos_sit_crouch_livestock_talk |
| 225 | 19 | 224 | fist_pos_sit_crouch_furniturerepair_talk |
| 41 | 15 | 160/105 | fist_pos_sit_chair_talk |
| 3 | 12 | 92 | fist_pos_sit_chair_weaponshop_dealer_talk |
| 27 | 13 | 223 | fist_pos_sit_crouch_livestock_talk |
| 23 | 12 | 224 | fist_pos_sit_crouch_furniturerepair_talk |
| 17 | 8 | 26 | fist_pos_sit_lean_talk |
| 53 / 49 / 106 / 155 / 64 / 38 / 109 | 7/6/6/6/4/2/2 | 70, 75, 141, 183, 93, 155/65, 144 | investigation / gang / chair_rest / chair_crossleg / guitarist / sidesleep+drunken / chair_pure (엘렌 공주 3616, 에스텔 6990) |

### 2.3 Hypotheses

| # | Hypothesis | Evidence now | Evidence to confirm |
|---|---|---|---|
| S1 | **Anim-id table mismatch**: the ids (26…224) are KR-era values; the 1.2 client's anim table maps ids→animations differently (or lacks these ids) → client plays a fallback pose that reads as "knees in" | Server forwards the id verbatim; packet carries no pose params; ids come from the same dump lineage as the rest of the KR-era data | In-game: does EVERY sitter look wrong, or only some anim ids? (checklist below) |
| S2 | _talk anims used as idle loop: ids are labelled `*_talk` (one-shot) yet used as the standing pose; looping a talk anim yields contorted holds | anim labels in npc_postures.talk_anim; server ignores talk_anim | Compare a knees-in NPC's pose vs its `talk_anim` label — same pose family? |
| S3 | Missing sub-pose/state: official 1.2 sit uses a unit-state field the server never sets (posture packet is only postureType+animId) | SCUnitStatePacket shape (Unit.cs:1022-1060) | Packet trace vs 1.2 client expectations (would need a client-side capture) |

---

## 3. CLOTH PHYSICS FREAKOUTS

- **No server-side cloth data exists** (no `npc_models` table; `models` has no cloth flags) — cloth is a
  client-side CryEngine property of the .cgr models. The server cannot flag it on/off.
- Data proxies for "likely cloth-bearing NPCs": `npcs.equip_cloths_id` (equipped cloth/armor pack) and
  female humanoid models (10/11/16/17/19/631/1342).
- **Correlation with the pose/float clusters is strong on paper**: the castle cluster (a) is exactly the
  NPC set that (1) has sit posture sets (엘렌 공주 set 109, 음유시인 set 64, 경비병 set 29…), (2) wears
  cloth (equip_cloths 134/1199/1245/1316/1373…), and (3) sits 3.5–4.8 m above bare terrain. If the sit
  anim id is wrong (S1/S2), the skeleton pose is wrong → the client cloth sim (driven by bone transforms)
  jitters/explodes on exactly these NPCs.
- Hypothesis: **cloth freakout is pose-driven, not position-driven** — same NPCs as the broken sit poses;
  the float offsets themselves don't feed cloth sim (position is not a cloth input).
- Confirm in-game: are the cloth-freakout NPCs the sitting ones (same spots/anim)? Do their skirts relax
  when the NPC stands (combat or ToD switch)?

---

## 4. TARGETING ("odd targeting")

### 4.1 Aggro acquisition (all in `AAEmu.Game/Models/Game/AI/v2/Framework/Behavior.cs`)

| Gate | Code | Radius | Notes |
|---|---|---|---|
| `CheckAggression` | :215 | `AttackStartRangeScale × 10 m` | requires `npcs.aggression` (`'t'`/`'f'` TEXT; parsed OK by `GetBoolean(col,true)` — not a bug) |
| `CheckAlert` | :288 | `SightRangeScale × 15 m` | → Alert state |
| spawn-effect pull | NpcSpawnerSpawnEffect.cs:48 | `SightRangeScale × 30 m` | only for NPCs with spawn effects |
| visibility | `BaseUnit.CanSeeTarget` :133 | — | **stealth check only — NO line-of-sight / raycast anywhere in the aggro path** |
| cone + height | Behavior.cs:243-244, 318-319 | `IsFront(SightFovScale)` + \|ΔZ\| < ModelSize×Scale×1.5 (flyers ×3.5-4) | "in front" gated; not-in-front fallback at 1.5-2 × SightRangeScale |

Census (target-zone NPCs, n=1,050): sight_range_scale median **1.0**, max **5.0**; fov median 1.0;
attack_start_range_scale median 1.0, max 5.0. Hostile mobs (faction 115): 236, median sight 1.0
(→ 10–15 m), max 5.0 — **바다 벌레 family 8563-8566: 40–50 m aggro radius**.

### 4.2 Hypotheses

| # | Hypothesis | Evidence now | Evidence to confirm |
|---|---|---|---|
| T1 | **No LOS check → aggro through walls/hills** — a mob in range+cone attacks regardless of terrain/buildings between | CanSeeTarget = stealth only; aggro radius math is purely distance+FOV+height-gap | In-game: does the "odd targeting" happen when the mob is behind a ridge/wall (no direct sight line)? |
| T2 | **Elf-village NPCs are genuinely hostile to Nuians** — faction 103 (꿈의 유배자들 = Elf player faction) is `state_id=3` hostile to 101 (초승달 왕좌) in system_faction_relations; 213 target-zone spawns are faction 103 (에노이르 569, 에오카드 3672, 알리아 3688, 네서틴 576, 헤이스 6541, 경매장 직원 658 …) with aggression 't' for some | relations table; faction census | In-game: is the "odd targeting" actually these elf NPCs (in the Bluemist/Gweonid villages) attacking on sight? If Josh plays Nuian, that is retail-correct hostility, not a bug |
| T3 | **Floating mobs break the height-gap check** — a floater (class b/c) can't aggro a ground player (ΔZ too large), and a sunk cave mob aggroes nobody; conversely a player near a floater gets targeted from 10-40 m with no LOS | height-gap formula + floater census | Do floating mobs ignore the player, or attack from far away? |
| T4 | Ranged-vs-melee confusion — skill selection respects skill Min/MaxRange (`BaseCombatBehavior.RefreshSkillQueue:421`) but chase range is `AttackStartRangeScale × weaponRange`; with UseRangeMod off, ranged mobs walk into melee | MoveInRange :132-142 | Do "oddly targeting" mobs use ranged skills from range or walk in? |

### 4.3 Latent code notes (no action)

- `WorldTemplate.GetBaiByPos` TODO: "Pick the actually correct zone" returns the **first** zone loader —
  currently inert because the pak has no zone-level .bai (path-based loaders are used).
- `npc_spawns.json` is JSONC (comments + trailing commas) — stdlib parsers choke; the server's JsonHelper
  handles it. Any tooling must strip comments/trailing commas (see recon scripts).

---

## 5. "Confirm in-game" checklist (Josh, next session)

1. **Floaters**: teleport to (10571,14718) — is 에노이르 (elf NPC) hovering ~90 m up? Same for 에오카드
   (13 spawn rows around (10572,14719)). Do they float at every one of their spawn points?
2. **Castle cluster**: at (12720,16250) and (15560,13780) — do the 정예 근위병 / 론반 공작 / 엘렌 공주
   stand ON the castle floor (fine) or visibly in the air (bug)? Guards at (15500,13760) — on a rampart?
3. **Flyers/sea/cave**: confirm 독수리/바다 벌레/동굴 거미 float/sink is natural (they fly/swim/cave).
4. **Sitters**: find the weaponshop dealer (set 3, anim 92), nursery dealer (set 22, anim 87), chair sitters
   (set 41, anim 160), leaner (set 17, anim 26) — do ALL sit poses look broken ("knees in") or only some
   anim ids? Note WHICH anim ids look wrong (count of affected NPCs).
5. **Cloth**: are the cloth-freakout NPCs the same as the broken sitters (pose-driven)? Watch one during a
   ToD change (posture re-broadcast) and during combat (posture cleared) — does the skirt settle?
6. **Odd targeting**: when a mob "targets oddly", is there a wall/ridge between you and it (no LOS)? Is it
   the elf-village NPCs attacking (faction 103 hostile to Nuians — retail-correct)? Do floating mobs ever
   aggro you from the air?
7. **Float↔target link**: do the floating elf NPCs (faction 103, aggression) attack from 90 m up, or not at all?

## 6. Prior art reconciliation (branch history)

This branch already carried `4744e2de` ("NPC behavior catalog", committed before this card ran).
Its headline finding — **"server-side terrain heights in Solzreed are wrong by tens of meters"
(F1, measured +28/+94/+149 m at live character positions)** — does **not** reproduce with a
correct sampler, and is a measurement artifact of that commit's terrain reader:

| Ground-truth point (live standing character / spawn) | Client-visible Z | Prior commit's terrain | This card's terrain |
|---|---|---|---|
| char "Assholes" (15597.6, 15224.0) | 122.4 | 150.4 (+28) | **122.7 (+0.3)** |
| char "Dingus" (14947.1, 14232.6) | 123.3 | 272.0 (+149) | **123.2 (−0.1)** |
| Nuian spawn (15578, 15382) | 126.5 | 220.7 (+94) | **126.2 (−0.3)** |

- The prior census ("all 1,356 Solzreed spawns 5.7–197.6 m below terrain, median −106.7") also does
  not reproduce: the same rect sampled with the server-exact algorithm (empty-node filter +
  x-major sort + `sectorX*16+sectorY` index) gives **median diff 0.0 m, 97.1 % within ±1 m**.
  The likely bug in the prior reader: the 85 empty cell-bbox placeholder nodes per .dat were not
  filtered before sector indexing (they sort first by Min.X), plus band-space confusion from the
  node AABB frames (node box origins are not the file's own cell — e.g. `012_013`'s nodes carry
  frames of cell (4,2); the server only uses them for sort order, and relative order is
  frame-invariant, so indexing is unaffected) being mistaken for content misalignment.
- **Verified findings from the prior commit that stand** (folded in): 327 world-wide spawn rows with
  no Z field at all (sample: 14562/14566/14568/14896, 8566 sea worms ×6) — Newtonsoft default ⇒ 0 ⇒
  underwater; the live `NpcControllEffect RunCommandSet (ParamInt=155, stable keeper 11548)` log line
  as a pose-corruption path; `aggro_link_help_dist` 6.0 m / `AcceptAggroLink` pack-assist data;
  ranged `base_skill_id` examples (3459 → 10431, 8176 → 20273).
- The prior commit's `GetBaiByPos` first-zone claim is **inert in practice**: the pak contains no
  zone-level `.bai` (zone/ folders hold only xml), so `ZoneBaiLoader` stays empty and the path-based
  branch always runs. Its walk-into-hills §3 belongs to `npc-pathing.md` (Recon A).

Conclusion after reconciliation: **the heightmap data is sound; the float symptom is a per-row data
defect** (§1.2/1.3), not a terrain-frame problem. This card's report supersedes 4744e2de's F1/F2
as root-cause explanations while keeping its supporting data.

## 7. Suggested next steps (out of scope here)

- Fix class F1 if confirmed: correct the specific `npc_spawns.json` Z rows (or snap spawn Z to heightmap
  unconditionally at load — engine change, needs Rei's gate + a card).
- Sit class S1/S2: verify 1.2 anim-id semantics (client-side anim table) before touching data; candidate
  card: "sit pose data audit" (evidence step).
- Targeting T1 (LOS): if confirmed, add a raycast/LOS gate to CheckAggression/CheckAlert (engine change;
  needs the Jitter physics heightmap — GeoDataMode already on).
