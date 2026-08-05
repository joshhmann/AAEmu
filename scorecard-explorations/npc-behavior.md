# NPC Behavior Recon — float, odd targeting, sit poses, cloth, pathing (M1 playtest findings)

**Author:** Tai (recon) · **Date:** 2026-08-04 · **Branch:** `npc-behavior-recon` (fork only, no upstream PR)
**Data provenance:**
- Spawns: `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` (25,118 entries, JSONC) — client-derived, loaded by `SpawnManager`
- Heightmaps: `game_pak` @ `root@192.168.0.165:/root/AAEmu/.server_files/AAEmu.Game/ClientData/game_pak` — `worlds/main_world/cells/*/client/terrain/heightmap.dat` (CryEngine `Hmap` format, parsed per `Models/ClientData/Hmap.cs` + `NodeCell.cs`)
- Static data: prod `compact.sqlite3` (`npcs`, `npc_postures`, `npc_aggro_links`, `npc_ai_params`, `npc_groups`, `zones`)
- Ground truth: live MySQL `characters` (2 active Solzreed characters), live container logs (`aaemu-game-1`), `Configurations/World.json` (GeoDataMode=true, PreLoadTerrain=false, HeightMapsEnable=true)
- Code: `/root/aaemu-dev` @ `develop` 94fb2f15

**Verdict up front:** the float symptom and the walk-into-hills symptom share one likely root cause — **server-side terrain heights in Solzreed are wrong by tens of meters** (measured +28 m and +149 m at two live character positions vs. the client's own ground truth). The odd-targeting symptom is consistent with **no LOS check existing anywhere in the codebase** plus target checks that depend on those same wrong heights. Sit poses/cloth are a separate, lower-confidence track.

---

## 1. FLOATING — spawn Z vs terrain

### 1.1 Solzreed spawn dataset
Zone rects for the three Solzreed zones (computed from client `worlds/main_world/world.xml` ZoneList, zone keys 142/178/179 = zone ids 9/124/125):

| zone key | zone id | name | world rect (X, Y) | cells |
|---|---|---|---|---|
| 142 | 9 | w_solzreed_1 | 13056–14976 × 13440–15872 | 12 |
| 178 | 124 | w_solzreed_2 | 14592–16256 × 13440–14144 | 2 |
| 179 | 125 | w_solzreed_3 | 14464–16256 × 14144–15936 | 6 |

- 1,356 spawns inside the rect. Z range **93.2 … 260.9**, median **131.7** (ocean level = 100; Solzreed is coastal — data Z values are plausible).
- No Z=0 and no missing-Z rows in Solzreed. World-wide, **327 spawns have no `Z` field at all** (Newtonsoft default ⇒ 0 ⇒ underground/water) — mostly outside Solzreed; worth a census pass of its own.
- Data-internal float candidates: 60 spawns sit **>15 m above the median Z of all spawns within 150 m** (n≥4 neighbors). Top offenders — prime spots for Josh to eyeball:

| ΔZ vs neighbors | UnitId | NPC (from npcs table) | Position | zone |
|---|---|---|---|---|
| +44.0 | 7673 | 난폭한 선돌 수호자 (Rampant Dolmen Guardian, aggro, scale 1.0) | (14697, 14976) Z 145.8 | 142 |
| +43.0 / +42.8 | 7987 / 502 | 자애로운 누이 여신 (Nui statue) / 누이 신전 신관 (Nui priest, scale 1.1) | (15137, 15123) Z ~145 | 179 |
| +41.1 | 3550 | 케로라라 (Kelorara, merchant) | (15358, 13949) Z 195.0 | 178 |
| +38.9 | 3648 | 머를린 (Merlinn) | (14461, 14916) Z 163.2 | 142 |
| +36.6 | 8156 | 사신 (Reaper, **scale 4.0**, sight 1.8) | (13858, 14924) Z 171.5 | 142 |
| +34.3 | 3454/3457/3459 | 망가진 시체 / 성채 병사 원혼 / 성채 궁수 원혼 (castle ghosts, scale 1.5–2.5) | (14067, 15340) Z 177.1 | 142 |
| +23.2 | 6994/7052/1852/1871 | 초원 여우 / 릴리엇 곰 / 독수리 / 잊혀진 전사 (Lilyut Hills animals) | (13179, 15779) Z ~213 | 142 |

Caveat: elevated clusters can be legitimate cliff/vista placements (e.g. 3 NPCs together at +34 m are likely all on one ledge). That is exactly what the in-game checklist settles.

### 1.2 How spawn Z is applied (code mechanism — the ±1 m guard)
`NpcSpawnerNpc.SpawnNpc` (AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs:97-104):

```csharp
if (!npc.CanFly) {
    var newZ = npcSpawner.ParentWorld.Template.GeoData.GetHeight(npcSpawner.Position.AsPositionVector());
    if (Math.Abs(npcSpawner.Position.Z - newZ) < 1f)
        npcSpawner.Position.Z = newZ;      // snap ONLY if within 1 m
}
```

- If the JSON Z is more than 1 m away from the server's terrain height, **the data Z stands as-is** — the NPC spawns exactly where the client data says, and any float/sink is pure data-vs-terrain mismatch.
- If the server's terrain height is itself wrong, the snap *pushes* the NPC to the wrong height (and the 1 m guard decides which NPCs get moved at all — a near-random-looking split of the population).

### 1.3 Terrain height quality — MEASURED WRONG in Solzreed (the big finding)
Reproduced the server's exact terrain sampling (`WorldCell.LoadCellHeightMapFromClientData`: filter `pHMData.Length > 0` → sort by AABB Min.X/Min.Y → index `sectorX*16+sectorY` → `NodeCell.GetHeight`, 2 m resolution) against the client `.dat` files and compared with ground truth:

| point | ground truth Z | server-algorithm terrain | error |
|---|---|---|---|
| Nuian spawn (CharTemplates.json) (15578, 15382) | 126.5 | 220.7 | **+94 m** |
| char "Assholes" (live, standing) (15597.6, 15224.0) | 122.4 | 150.4 | **+28 m** |
| char "Dingus" (live, standing, zone 179) (14947.1, 14232.6) | 123.3 | 272.0 | **+149 m** |

All 1,356 Solzreed spawns come out 5.7–197.6 m *below* the sampled terrain (median −106.7) — a systematic offset, not noise. Two independent explanations, both pointing at the same code:

1. **Per-cell `.dat` content does not match its file name.** Node AABB bands (cell-local expected 0–1024; observed, in cell-local terms after subtracting the cell origin):
   - `013_014` → band 1024–2048 (i.e. content describes a *neighboring* 1024 m tile, +1 cell both axes)
   - `014_014` → 1024–2048 (same band as 013_014 — two different files, same frame)
   - `014_013` (Dingus's cell) → 6144–7168 × 2048–3072 (~6 cells away)
   - `015_015` → 2048–3072; `020_020` → 4096–3072; `033_037` → 4096–6144
   No single modulus/offset maps name → content. `WorldCell.cs` never consults the AABB — it indexes the sorted node list blindly — so whatever tile the file really holds is treated as the named cell's terrain.
2. **`GetBaiByPos` returns the FIRST zone's `.bai` for every position** (WorldTemplate.cs:238: `return ZoneBaiLoader.Values.First(); // TODO: Pick the actually correct zone`) — active because prod `GeoDataMode=true`. The geodata height used by the spawn snap is the first-loaded zone's navmesh nodes for *all* of Solzreed, not the local zone's.

Consequence: the heights feeding (a) the spawn Z snap, (b) the physics terrain collider (`PhysicsManager.cs:103,127` builds the Jitter terrain shape from the same `HeightMap` arrays), and (c) AI height-gap checks are all unreliable. The client, meanwhile, renders its own terrain correctly — so NPCs visually float or sink relative to what Josh sees.

### 1.4 Float hypotheses, ranked

| # | Hypothesis | Evidence now | What confirms it |
|---|---|---|---|
| F1 | Server terrain height wrong (per-cell .dat frame mismatch) → snap + physics + height-gap all off | §1.3 measured +28/+149 m at live positions; band table | In-game `/height` at Dingus/Assholes coords vs 123; dump `HeightMap` via debug command; map .dat bands to world rects with a converter tool |
| F2 | Geodata height from wrong zone (GetBaiByPos first-zone TODO) | WorldTemplate.cs:238, GeoDataMode=true in prod | Enable one-zone-at-a-time test; log which zone key's bai is used per position |
| F3 | Data Z itself wrong for specific rows (absolute-Y from a different world origin) | 327 missing-Z spawns world-wide; Solzreed rows all *plausible* (93–261) | Josh: check specific outlier NPCs (§1.1 table) — if they float exactly at data Z, it's data |
| F4 | Model anchor / scale offset (tall models rendered from feet vs center) | 8156 사신 scale 4.0, 3457 scale 2.0 among outliers | Float height proportional to model height on same NPC type; client-side check |
| F5 | Mount/sub-model or client anim | no positive evidence | Floats only on mounts/vehicles |

---

## 2. TARGETING — aggro/odd targeting

### 2.1 Code survey (AI v2, `Models/Game/AI/v2/Framework/Behavior.cs` + `BaseUnit.cs`)

- **No line-of-sight check exists anywhere in AAEmu.Game** (grep for LOS/line-of-sight: 0 hits). `CanSeeTarget` (BaseUnit.cs:133) is a distance/height gate, not geometry. The client-side "targeting behind walls" hypothesis is therefore *structurally plausible* — the server never rejects a wall-hugging target.
- Sight/aggro trigger (`Behavior.cs:256, 298, 332`): scan radius = `SightRangeScale * 15f` (15 m default), trigger when `IsFront(owner, unit, SightFovScale)` (FOV cone — NPCs ignore targets behind them) AND `|ΔZ| < ModelSize*Scale*1.5–1.75` AND `CanAttack` AND (`range < 1 m` OR `CanSeeTarget`); "breathing down your neck" fallback at 1.5–2 × SightRangeScale.
- **The ΔZ gate uses the same broken terrain context**: a player standing 20 m up a cliff on correct client terrain can be invisible to a mob whose server-side Z context says the mob is 150 m lower/higher — and vice versa. This couples the targeting symptom directly to §1.3.
- `CanAttack` (BaseUnit.cs:53-112): faction relations + zone-faction protection; the NPC-vs-NPC safe-zone branch is commented out with `// TODO: fix npc safety` (lines 109-111) — zone faction can't shield NPCs from each other, so "neutrals hostile in towns" is a live possibility wherever faction relations misresolve.
- Aggro assist: `UpdateAggroHelp` (Behavior.cs:347-360) scans `AttackStartRangeScale * 200` m and links NPCs within `AggroLinkHelpDist` (6.0 m default in data) when `AcceptAggroLink`.

### 2.2 Data for the Solzreed cast (npcs table, compact.sqlite3)

| Field | Typical hostile (faction 115) | Typical civilian (faction 101) | Guards (167) / Nui (165) |
|---|---|---|---|
| `sight_range_scale` | 0.7–1.8 (7673: 0.7, 8156: 1.8, 8176: **2.5**) | 1.0 | 8176: 2.5 / 7987: 1.0 |
| `attack_start_range_scale` | 0.5–1.0 | 1.0 | 8176: 2.5 |
| `aggression` | 't' (monsters), some 'f' (7648 배고픈 불곰 is 'f'!) | 'f' | 8176/7987 't' |
| `aggro_link_help_dist` | 6.0 | 6.0 | 7987: 6.0, `aggro_link_sight_check`='t' |
| `return_distance` / `absolute_return_distance` | 50 / 200 (7987, 8145: 5/5) | 50 / 200 | — |
| `npc_ai_param_id` | 0 (default), 1058/660/1047/1122 (boss-ish), 2630 = `alertDuration = 0` (10666 와이어트) | 0, 2207, 2289… | — |
| `base_skill_id` | 2 (melee) — 3459 궁수 원혼: **10431**, 8176: **20273** (ranged) | 2 | — |

- Only 1 `npc_aggro_links` row for the whole cast: 3463 (피 묻은 손 돌격대원) → aggro link 37 (Bloody Fist strike squad — pack assist).
- `npc_groups`: 7673 leads "솔즈리드 우두머리 늑대" group; 8176 in "누이안 경비병 세트". Formation offsets exist per member — pack cohesion is group-driven.
- Faction mix at the same coordinates is normal for a hub (101 civilians + 115 monsters + 1/165/167 neutrals) — the odd-targeting reports most plausibly come from the missing LOS + broken ΔZ gate, not from faction data (with the `// TODO: fix npc safety` caveat for towns).

### 2.3 Targeting hypotheses

| # | Hypothesis | Evidence now | What confirms it |
|---|---|---|---|
| T1 | No LOS → mobs aggro/attack through walls & terrain | 0 LOS hits in code; Behavior.cs distance-only gates | Packet trace: `SCAiAggroPacket` from a mob with a wall between; /target while behind cover |
| T2 | ΔZ gate corrupted by wrong server terrain (§1.3) → targets "ignored" or "stolen" across elevation | §1.3 measurements; Behavior.cs:240,315 use ModelSize*Scale with world Z | Same mob, two players 10 m apart vertically — aggro differs from client expectation |
| T3 | FOV-only targeting (IsFront) reads as "ignores me when I'm behind it" | Behavior.cs:243,318 `MathUtil.IsFront` | Flank test: stand behind a stationary mob — retail 1.2 does react to back-attacks |
| T4 | Faction/zone-protection gap (`TODO: fix npc safety`) makes town NPCs attackable/hostile | BaseUnit.cs:109-111 commented | Attack a civilian in a guarded hub; check SCAiAggroPacket target |
| T5 | Client-side target picking (behind walls) — no server evidence either way | — | Josh: does the reticle snap through walls without any aggro? If no aggro follows, it's client-only cosmetic |

---

## 3. Walk-into-hills (pathing ignores elevation) — comment-thread item

- **Code location:** the physics terrain collider that mobs collide with is built in `PhysicsManager.cs:103` ("Add terrain shape based on height map") from the *same* `WorldCell.HeightMap` arrays (PhysicsManager.cs:127 `cell.GetHeightMapDataInCell`); AI movement runs through `Simulation`/AI `MoveTo` toward targets, and geodata heights come from `AiGeodataManager.GetHeight` (nearest `.bai` node, fallback `GetRawHeightMapHeight` — WorldTemplate.cs:118).
- **Terrain data:** heightmaps load at boot from the client pak (`WorldManager.LoadHeightmaps`, WorldManager.cs:646-663: "Loading heightmap of main_world") — files are `cells/*/client/terrain/heightmap.dat` (592 KB/cell, 512×512 @ 2 m, CryEngine Hmap v24). Boot line confirmed in code; container log buffer had rotated past boot, so the live "Loaded N/M heightmaps" line wasn't captured — that's a cheap log check for next session.
- If §1.3 stands (per-cell .dat frame mismatch), the physics terrain shape is wrong in the same places → mobs path straight through hills exactly where the terrain shape is absent/too low. **Highest fix potential of the three comment items**, and it shares the root cause with floating.

## 4. Sit poses "knees in" + cloth jitter — comment-thread items

- **Mechanism chain:** `npc_posture_sets`/`npc_postures` (compact.sqlite3) → `NpcManager.cs:709-729` loads `AnimActionId` + `start_tod_time` per posture set → `TimeManager.cs:109-115` picks posture by time-of-day → `SCUnitStatePacket` (Core/Packets/G2C/SCUnitStatePacket.cs:40) sends `ModelPostureType.ActorModelState` when `AnimActionId > 0`. The client plays the anim named by that id; a wrong id or a wrong sub-param (e.g. sit variant) renders as the "knees in" pose.
- **Live log evidence (aaemu-game-1):** `04:01:14 NpcControllEffect: CategoryId=RunCommandSet, ParamString=, ParamInt=155, caster=11548, target=11548` — the mount-stable keeper (11548 탈것 축사 관리인, aggression='t') was pushed through `AiGameData.GetAiCommands(155)` → `EnqueueAiCommands` (NpcControlEffect.cs:62-80) right around the playtest. Command-set-driven anim state is a plausible pose corruption path.
- **Cloth:** skirts/cloth sim is client-side; jitter usually follows bad pose/movement state, so test correlation with the same NPCs before chasing it (checklist below).
- Hypothesis: posture selection (`TimeManager` FirstOrDefault over an unordered list — TimeManager.cs:113-115) or the RunCommandSet anim state fights the sit pose; data-side `anim_action_id` values themselves are 1.2-native and most likely fine.

## 5. confirm in-game — checklist for Josh's next session

Positions (use `/teleport <x> <y>` or run to them; `Height` GM command exists — `Scripts/Commands/Height.cs`, also `TestHeight.cs` — prints server terrain height at a position, which is THE way to confirm §1.3 without code changes):

1. **Are the floaters the ones in §1.1's table?** Check the 10 spots listed (start with (14697,14976) 7673, (15137,15123) 7987/502, (13858,14924) 8156, (13179,15779) animals). Note: floating, sunk, or correct. Do the same NPC types float at *every* spawn of theirs or only some?
2. **Terrain probe:** run `/height` at (15597,15224) and (14947,14233) — if it prints ~270 at Dingus's old spot, §1.3's frame-mismatch is confirmed live. Also at the Nuian spawn (15578,15382): client says 126.
3. **Floaters vs sitters:** are the NPCs sitting "knees in" the same NPCs that float? (test posture while idle vs after RunCommandSet commands — e.g. the stable keeper 11548 near the mount stable).
4. **Walk-into-hills:** do mobs do it at the same spots every time (e.g. the hill between (13179,15779) and Solzreed village, or the castle ghosts' ridge (14067,15340))? Same spots → terrain-shape gap (§3); everywhere → generic physics issue. Also: idle patrolling vs combat-chase only?
5. **Targeting:** attack a mob while standing behind a wall/cliff (same elevation) — does it aggro? Stand 10–15 m above a mob on a cliff — does it aggro (ΔZ gate)? Attack a civilian in a town — does a guard react (`SCAiAggroPacket`)?
6. **Log capture:** save `docker logs aaemu-game-1` around the tests; the packet lines (`CSChangeTargetPacket`, `SCAiAggroPacket`, `StartSkill`) plus any `NpcControllEffect` lines are the evidence the T-table needs.
7. Boot-line check: `docker logs aaemu-game-1 | grep -i heightmap` right after a restart → "Loaded N/M heightmaps".

## 6. Follow-ups suggested (do NOT do in this card)

- **Terrain-frame mapping** — a small tool/pass over `heightmap.dat` AABB bands vs cell names (data available; parse script exists in the card workspace) to produce the definitive world→file mapping; then decide: fix indexing, or generate server-side heightmaps via `Tools/WorldConverter` (README documents exactly this "pre-generated heightmap data for use with AAEmu" flow — likely the intended path).
- Census pass on the 327 missing-Z spawns.
- LOS check design (cheap: heightmap raycast, data already loaded) for the T1 confirmation.
- Posture state-machine review (TimeManager FirstOrDefault + RunCommandSet interplay) once Josh confirms which NPCs sit wrong.

## 7. Reproduction notes

All analysis scripts live in the card workspace (`/root/.hermes/kanban/workspaces/t_350d36b1/repo/scratch/*.txt`): `analyze.txt` (zone rects + terrain sampling + deltas), `outliers.txt` (neighborhood float candidates), `paklist.txt` (AAPak FAT reader with AES-128 key from AAPak.cs), `hmbands*.txt` (AABB band survey), `npc8_aggro.txt`/`npc9_posture.txt` (sqlite pulls). Terrain sampling mirrors `WorldCell.LoadCellHeightMapFromClientData` + `NodeCell` exactly (filter → sort → index → `0.05*iOffset + (raw>>4)*iStep*0.05`).
