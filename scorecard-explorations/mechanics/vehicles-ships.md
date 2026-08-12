# Vehicles & Ships (Slaves) — Canonical 1.2 Behavior + Data (SLAVE-01 dossier #1)

**Task:** t_19f18c63 (mechanic-research lane, dossier for M4-3; feeds t_4a91a4f5 summon/despawn, passenger+cargo, death/disconnect cleanup, portal/instance, restart recovery, stuck recovery — Rei-gated before Tai implements)
**Branch:** vehicles-ships-dossier
**Date:** 2026-08-11
**Scope:** evidence only, no code changes. Ground truth: joshhmann fork `develop` @ a31826b74 (slave subsystem **byte-identical to upstream AAEmu/AAEmu `develop` @ 31aff9869** — verified per-file, 0 diff lines on `SlaveManager.cs`, `Slave.cs`, `SummonSlave.cs`, `SlaveGameData.cs`, `ShipyardManager.cs`, `RepairSlaveEffect.cs`, `CSDespawnSlavePacket.cs`, `PortalManager.cs`); canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (r208022, 679 tables) + fork `Data/slave_attach_points.json` (15 ship models, comment-stripped parse). All claims below are flagged **[D]** data-verified, **[C]** code-verified, **[R]** research-derived (period wiki/forum, dated), or **[U]** upstream-issue-verified.

---

## 1. TL;DR

Canonical 1.2 vehicles/ships are **slaves** (internal name; AAEmu Docs/wiki/Code-Terminology.md). Two families:

- **Player-owned summon items** — the summon scroll/item carries a bound slave instance (row in MySQL `slaves`). Roster in 1.2 data: rowboat, harpoon speedboat (Clipper), speedboat, Eznan Cutter (east/west small sailing ships), merchant ship, fishing longliners, plus shipyard-built galleon/medium ships. Rowboats are quest-given; clippers/cutters/merchant ships come from shipyard blueprints (Mirage Isle) or rewards — completed shipyard → owner claims → **summon item**.
- **World-spawned slaves** — `Data/Worlds/<world>/slave_spawns.json` (e.g. main_world: Cotton Wagon 73, Blueglass Human Cannon 68), managed by `SlaveSpawner` with respawn/despawn timers; not player-bound.

**The canonical rule set (from 1.2 client error strings + period play records):**

| Rule | Canonical 1.2 | Fork/upstream engine |
|---|---|---|
| Summon | Use scroll/item → vehicle appears in front of owner; **one active slave per owner**; cannot summon while destroyed (repair first) | Implemented (`SlaveManager.Create`): auto-despawns previous slave; destroy-cooldown check; boat water-depth search; land height snap |
| Despawn | Owner-only, **must be near the vehicle** (312 `slave_despawn_near_the_slave`), **not in combat** (288), **not harpooned**, **no loaded packs** (417); portal-return animation ≈ `portal_time` (2–7 s in data) | Partial: packs gate (801 family), owner gate on `RemoveActiveSlave`; **no range check, no combat gate, no harpoon gate** — 312/288 never sent |
| Abandonment | Vehicle auto-despawns ~5–10 min after owner leaves it (period reports) | **Absent** — no owner-distance despawn timer |
| Passengers | Driver seat + passenger seats via model attach points; driver locked to owner (97 `slave_already_has_master`); cannot bind to dead vehicle (324) | Partial: driver lock via Owner's-Mark buff; **no range check on bind**, character seat positions not applied (TODO), 324 never sent |
| Cargo | Trade packs load into vehicle cargo slots (doodad boxes); **cannot despawn/teleport with packs aboard** (417/801); packs drop as free loot if vehicle despawns/dies | Implemented: box doodads + `DoodadFuncRecoverItem`; 801 gate on despawn and on cross-instance teleport; backpacks dropped to ground on death |
| Death | Vehicle destroyed → wreck/debris, packs drop, summon item broken; repair with **Gon's (Shatigon's) Sandglass** item, then a short wait before re-summon | Implemented: `DoDie` → debris + pack drop + item marked destroyed; `RepairSlaveEffect` + `repairable_slaves` mapping; **wait is 10 min (canonical reports say 5)** |
| Disconnect | Vehicle despawns when the character goes offline (char stays ~15 min after hard DC, then despawns with vehicles; packs drop if not relogged in time) | Hard DC → 10-min task then despawn; clean logout → immediate despawn; logout timer +10 min while a slave is active |
| Portals/instances | Vehicles do not enter instances; cross-instance teleport returns/despawns them | Implemented: cross-instance `UsePortal` → error 801 if packs, else despawn mates+slaves (upstream PR #1477); same-world portals leave the vehicle in place |
| Stuck | **Rider's Escape** skill (소환물 탈출, ids 22130/22131/22143): teleports the vehicle to you — within 20 m, 900 s cooldown | Implemented (`RidersEscape`): despawn packets → reposition → respawn; **no range check, no cooldown check server-side** |
| Restart | Vehicle state survives restart via the item-bound DB row (HP/MP); vehicle is re-summoned from the item, not auto-respawned in world | Implemented: MySQL `slaves` row per (owner, item); re-summon restores HP/MP only — **position is saved but never read back; no boot-time respawn of player slaves** |

**Headline engine fact:** unlike housing (which was also upstream-identical), the slave surface is *actively developed upstream* — full ship physics (PR #1402), marine mechanics (PRs #1400–1421), harpoon cannon (#1417), portal despawn (#1477), rider-state cleanup (#1481). The fork carries **zero slave-specific divergence**: every gap below is an upstream gap, so M4-3 work should be framed as upstream-aligned fixes, and upstream issues #768/#1404/#1443 document three of them.

---

## 2. Canonical data model (1.2, compact.sqlite3 r208022) **[D]**

### 2.1 `slaves` — 140 templates, 9 kinds

Schema: `id, name (KR), model_id, mountable, spawn_x_offset, spawn_y_offset, faction_id, level, cost, slave_kind_id, spawn_valid_area_range, slave_initial_item_pack_id, slave_customizing_id, customizable, portal_time, hp25/50/75_doodad_count`.

Kind census (matches server `SlaveKind` enum 1–9; kinds 10/11 MerchantShip/Leviathan are post-1.2, absent from data):

| kind_id | kind | count | representative 1.2 rows (id, KR name, model, portal_time) |
|---|---|---:|---|
| 1 | BigSailingShip | 24 | 9 갤리온 galleon (m115, 0.0), 11 황의 소형 범선 (m511), 139 중형 범선 (m1546, **2.0**), 23/61 오스테라 소형 범선 (m512/1024, 7.0), 109 무역선 인양 부이 (m1402) |
| 2 | SmallSailingShip | 13 | 21 이즈나 소형 범선 (m393, **7.0**), 75 무역선 merchant ship (m1205, **7.0**), 92 비파 소형 범선 (m1249, 7.0), 22/24/78/79/80/96/98/101/120/132 variants |
| 3 | Speedboat | 5 | **14 작살 쾌속정 harpoon speedboat = "Clipper" class (m128, 7.0)**, 52 쾌속정 (m128, 7.0), 76 모험의 쾌속정, 81/82 samples |
| 4 | Boat | 4 | **15 나룻배 rowboat (m129, 0.0)**, 91 아노의 추모선 (lvl 10), 102/103 duck/rowboat variants |
| 5 | Tank | 20 | (siege/arena vehicles) |
| 6 | Machine | 8 | (carts/machines) |
| 7 | SiegeWeapon | 48 | (trebuchets etc.) |
| 8 | SlaveEquipment | 15 | (cannon/sail part templates) |
| 9 | Fishboat | 3 | 110 조용한 탐구자의 어선 (m1360, **5.0**), 113 사나운 포식자의 어선 (m1383), 114 날렵한 여행자의 어선 (m1384) |

`portal_time` = despawn/summon portal-return animation seconds (server uses it as the despawn delay, `SlaveManager.Delete` `PortalTime ± 0.5 s`) **[C]**. `spawn_valid_area_range` = 50 on every row — loaded (`SlaveGameData.cs:55`) but **never used by any game logic** (grep: only loader + template) **[C]** — canonical "valid spawn area" validation missing.

### 2.2 The summon chain (scroll → skill → slave) **[D][C]**

- `item_summon_slaves` (67+ rows): item_id → slave_id. Player summon scrolls (items table): 15816 작살 쾌속정 소환 주문서 → slave 14; 27266 상속자의 작살 쾌속정 소환 주문서 → 14 (heir/quest clipper); 5223 이즈나 소형 범선 소환 주문서 → 21; 23398 무역선 소환 주문서 → 75; 25739 비파 소형 범선 소환 주문서 → 92; 17626 쾌속정 소환 주문서 → 52; rowboat scrolls 15817/16236/**17863 솔즈리드 나룻배 소환 주문서**/17906/17944/20054/21588 → 15; fishing boats 27135/27145 → 110/14(test).
- Every summon scroll shares `use_skill_id = 15802` ("공간의 문을 여는 중..." — opening a portal) → `skill_effects` → effect 29581 → `special_effects` type **60 = `SpecialType.SpawnSlave`** with all values 0. The slave id is **not** in the effect: `SpawnSlave.Execute` → `SlaveManager.Create(owner, skillData)` resolves `SummonSlaveTemplate.SlaveId` (loaded from `item_summon_slaves` by `ItemManager.cs:894`) **[C]**.
- Rider's Escape: skills 22130/22131/22143 소환물 탈출 → special effect type **90 = `EscapeMySlave`** (matches ArcheRage DB skill 22130 "Rider's Escape") **[D][R]**.
- Shipyard-built ships: `ShipyardManager.ShipyardCompleted` → `SlaveManager.Create(character, skillData, true, shipyard.Transform)` — the completed ship becomes a summon item too (matches IGN/TTH: "the owner of the blueprint can claim the ship, which becomes a summon item") **[C][R]**.

### 2.3 Seats, cannons, cargo — attach points and bindings

- `Data/slave_attach_points.json` (fork data, 15 models): Clipper m128, Rowboat m129, Eznan Cutter m393, Merchant Schooner m1205, Lutesong Junk m1249, Fish-Find Longliner m1360, Predator m1383, Albatross m1384, Luxury Liner m1046 + 6 more. Per-model `AttachPointKind` positions (Driver=1, Passenger0..6=2..8, Cannon0..19=9..56, Sail0/1, Helms=35, HealPoint0..9=36..45, LadderRear=58/59, Box0..15+=60..75…). Loaded by `SlaveGameData.LoadSlaveAttachmentPointLocations` (radians conversion) **[C][D]**.
- `slave_doodad_bindings` (576 rows): per-slave doodads. Merchant ship (75): 32 bindings — cannons (11,12), sails (21,22), ladders, lamps, helms (35), rear ladders (58,59) and **cargo boxes Box0..Box20 (attach 60–80 = 21 slots)** — matches IGN "Merchant Ship: 20 Trade package slots" (data has 21 box points) **[D]**. Clipper (14): 9 bindings — sail 21, lamps 28/29, helms 35, cannons 47–49 (Cannon10–12), rear ladders 58/59.
- `slave_bindings` (117 rows): child slaves (cannons) attached to parent slave — e.g. galleon-family cannons at Cannon0..7 (attach 9–16) **[D]**.
- `slave_healing_point_doodads` (81) — repair-point doodads spawned per HP band (`UpdateSlaveRepairPoints`, counts from `hp25/50/75_doodad_count`) **[C]**.
- `slave_drop_doodads` (45) — wreck debris on death **[C]**.
- `slave_initial_items` (24, in packs) + `slave_initial_buffs` (79) + `slave_passive_buffs` (18) + `slave_mount_skills` (324) + `unit_modifiers` (owner_type='Slave') **[D]**.
- `ship_models` (26 rows) — physics hull params (mass, mass_center, keel height, velocity) used by the ship-physics engine (`ModelManager.GetShipModel`) **[C][D]**.
- `repairable_slaves` (29 rows: slave_id → repair_slave_effect_id) + `repair_slave_effects` (4: id, health, mana). Mapping: slaves 21/92 → effect 2, slaves 14/52/76 → effect 4, … The repair items in 1.2 data: **20061 곤의 '한 줌' 모래시계 (Gon's Pinch sandglass), 23642 곤의 '한 숟갈' 모래시계 (Spoonful), 23641 곤의 '한 상자' 모래시계 (Handful), 27412 곤의 공구 상자 (toolbox)** — the "Shatigon's Sandglass" equivalents (KR "곤" = the repair NPC; NA name Shatigon per IGN/TTH) **[D][R]**.

### 2.4 Quest/plot acquisition of boats **[D]**

- Quest 3736 유서깊은 나룻배 ("The Venerable Rowboat") supplies item 20054 (venerable rowboat summon scroll) via `QuestActSupplyItem`; quests 2401 (두 번째 시합을 위해, Mirage arena chain) and 4568 (잔뿌리 염료의 재료) supply 15817 (rowboat scroll); item 17863 = Solzreed rowboat scroll (data; acquisition row not located in quest acts). Matches TTH/IGN: "You will receive a rowboat as a quest reward early on during the starting quests" / "Your Own Boat" quest at the docks.

---

## 3. Summon / despawn semantics

### 3.1 Canonical (1.2) **[R] + client error strings [D]**

- Summon from inventory item; vehicle materializes in front of the owner (boats: on the water; land vehicles: on the ground). One active summon per character — the canonical client error 99 `slave_spawn_error_already_spawned` exists for the second attempt (server instead auto-despawns the old one, see 3.2).
- Despawn is owner-initiated from the vehicle UI and requires: proximity (312 `slave_despawn_near_the_slave`), no combat (288 `slave_cannot_remove_while_in_combat`), no harpoon, no loaded packs (417 `slave_cannot_remove_while_in_carrying_backpack_doodad_items`). Steam guide 2015-03-30: "You cannot desummon a ship that is in combat, is harpooned, or has a tradepack on it."
- Abandonment: the vehicle despawns on its own after the owner leaves it — reddit r/archeage 2015-06 (39wd45): "If you get away from it for 5 minutes there's a despawn timer, when it does vanish it'll drop the packs on the ground"; 2015-04 (3401zb): "I can't unsummon it because it's out of range. It will eventually unsummon on its own (in like 10 minutes)." (2.5–10 min across reports; server-side timer of ~5–10 min.)

### 3.2 Fork/upstream engine **[C]**

- **Summon** — `SpawnSlave` special effect → `SlaveManager.Create` (`SlaveManager.cs:301–319`, `332–751`):
  1. `GetActiveSlaveByOwnerObjId` → if an active slave exists, `Save()` + `Delete()` it (auto-replace; the "already spawned" error is never sent).
  2. Destroy cooldown gate: if item `IsDestroyed > 0 || RepairStartTime > MinValue`, remaining = `RepairStartTime + 10 min − now`; if > 0 → error **540 `slave_spawn_error_need_repair_time`** with seconds; else reset `RepairStartTime = MinValue`.
  3. Position: owner transform + `SpawnYOffset` clamped 5–50 m to the front (X offset intentionally ignored — "visually nicer"); boats → water-level snap + front-depth search loop (up to 50 m + hull size, requires `surface − floor > massbox/keel` depth) + buoyancy submerge adjust; land vehicles → `GeoData.GetHeight` snap; rotation = owner yaw + 90°.
  4. DB: `SELECT * FROM slaves WHERE owner_type=0 AND owner_id=@player AND summoner=@player AND item_id=@item LIMIT 1` — restores name/HP/MP (**position columns read but discarded**); new slave gets `CharacterIdManager` id; `REPLACE INTO slaves` after spawn.
  5. Spawns bound doodads (`slave_doodad_bindings`, persistent ones saved), child slaves (`slave_bindings`, owner_type=2 rows looked up by parent dbId), applies initial items/buffs/bonuses, `SCSlaveCreatedPacket` + `SCMySlavePacket`, ship added to physics.
- **Despawn** — two entry points:
  - `CSDestroySlavePacket` → `RemoveActiveSlave` (owner check via `Summoner.ObjId`, then `Delete`) — the intended owner-despawn.
  - `CSDespawnSlavePacket` → `Delete(...)` **directly, no owner/range/combat check** — any player can despawn any slave by objId (canonical 312 range gate absent; used by the client for the vehicle "despawn" button).
  - `Delete` (`SlaveManager.cs:222–279`): `Save()` → unbind all passengers → **cargo gate**: if any attached doodad holds an item → error 801 `slave_equipment_loaded_item` and abort (unless `ignoreAttachedItemWarning`); despawn delay = `PortalTime − 0.5 s` for doodads/children and `PortalTime + 0.5 s` for the slave (portal-return animation); `SCSlaveDespawnPacket` + `SCSlaveRemovedPacket`; removed from world/physics; **DB row retained** (despawn ≠ delete).
- **One-slave-per-owner invariant**: `GetActiveSlaveByOwnerObjId` (used by summon, logout, portal, instant-teleport, item-delete paths) — matches canonical 99 semantics in effect, minus the error message.

---

## 4. Passenger + cargo attachment

### 4.1 Canonical **[R]**
- Driver seat = owner (or whomever the owner allows; "Owner's Mark"); passengers occupy seats/crew stations (cannons, harpoon, helms). Rowboat "can seat one other person" (IGN). Ships need a crew for cannons/harpoon (TTH). Passengers are bound to the vehicle and move with it; boarding via interaction doodads near the seat. Dead vehicles can't be boarded (324 `slave_cannot_bind_while_is_dead`).

### 4.2 Engine **[C]**
- `CSBindSlavePacket` → `BindSlave(connection, tlId)` → driver attach (no range check; `AttachUnitReason.NewMaster`). `AttachTo` special effect (type 53) → `BindSlave(character, objId, (AttachPointKind)value1, NewMaster)` — used by seat-interaction skills (passenger seats).
- `DoodadFuncAttachment.Use`: with a valid `BondKindId` → doodad seat bonding (`Seat.LoadPassenger`, `SCBondDoodadPacket`, sticky parent); with `BondKindId > BondInvalid == false` → **ship boarding**: `SlaveManager.BindSlave(character, owner.ParentObjId, AttachPointId, AttachUnitReason.BoardTransfer)`.
- `BindSlave` (167–195): seat-occupied check; **driver lock**: if attach point is Driver and the slave has the `OwnersMark` buff (`BuffConstants.OwnersMark`) and the character isn't the summoner → error **97 `slave_already_has_master`** (only 97 is sent; canonical 96 `slave_cannot_bind`, 324 dead-vehicle, 103 `slave_shut_off_to_unbind`, 102 `slave_unbind_first` are never sent). Attaches via `SCUnitAttachedPacket`, `SCSlaveBoundPacket` (driver), transform parenting; **TODO "move to attach point's position" — characters sit at local 0,0,0**, not at the model's seat offset.
- `UnbindSlave` (135–158): detach + `SCUnitDetachedPacket` + `BuffRemoveOn.Unmount` + harpoon-rope operator cleanup (`ShipHarpoonRopeController.OnOperatorLeftSlave`). `CSDiscardSlavePacket` (dismount button) → `UnbindSlave(AttachUnitReason.SlaveBinding)`. `Character.ForceDismount` (2078–2140) is the universal pre-teleport/pre-logout dismount (also chairs via `Bonding`).
- **Cargo**: box doodads (Box0..n per 2.3) with `DoodadFuncRecoverItem` — pack load = pack placed into doodad (item bound to doodad `ItemId/ItemTemplateId`); unload = `RecoverItem` → permission check via `AllowedToInteract` (owner/party/house-family gates), backpack auto-equip or bag insert. `Delete` refuses while `doodad.ItemId != 0 || ItemTemplateId != 0` (error 801) — the canonical 417 family.
- Zone-change propagation: `Slave.OnZoneChange` forwards to attached passengers.

---

## 5. Death / disconnect cleanup

### 5.1 Vehicle death **[C]**
`Slave.DoDie` (816–843): death packet → `DestroyAttachedItems` (848–936: backpacks → **dropped to ground as persistent doodads** with water/floor height logic and random ±1 m offset; non-pack held items destroyed; child doodads/slaves deleted, objIds released) → `DistributeSlaveDropDoodads` (941–967: wreck debris from `slave_drop_doodads`, on water or floor) → `MarkSummoningItemAsDestroyed` (972–981: `IsDestroyed=1`, `RepairStartTime=MinValue`, `SummonLocation=Zero`, `SCItemTaskSuccessPacket(ItemTaskType.MateDeath)`) → passengers unbound → ship removed from physics → **despawn scheduled at `Spawner.DespawnTime ?? 20 s`** (kept visible/selectable during death anim). HP triggers (25/50/75/100) drive `UpdateSlaveRepairPoints` (repair-point doodads appear as HP drops).

### 5.2 Repair **[C][D][R]**
- Canonical: destroyed ship repaired with Shatigon's/Gon's Sandglass (size per ship), then a short wait: reddit 2015-06 (382upz) "For clippers it is 5 sand and 5 minutes before it can be summoned" **[R]**.
- Engine: `RepairSlaveEffect` (Models/Game/Skills/Effects/RepairSlaveEffect.cs) — item-targeted repair skill from the sandglass item; validates via `SlaveGameData.HasRepairEffectId(slaveTemplate.SlaveId, effectId)` (`repairable_slaves`), sets `IsDestroyed=0` + `RepairStartTime=now` → summon blocked for **10 minutes** (error 540). **Deviation: 10 min vs canonical ~5 min.** `CSRepairSlaveItemsPacket` (repair-NPC flow) is a stub (reads npcId only).

### 5.3 Disconnect / logout **[C][R]**
- Clean logout: `EnterWorldManager.Leave` → logout timer **+10 min if a slave is active** (EnterWorldManager.cs:124–126) → `LeaveWorldTask` (164–202) → `RemoveAndDespawnAllActiveOwnedSlaves` (save + Delete, immediate despawn with portal anim).
- Hard DC: `GameConnection.OnDisconnect` (85–111) → `RemoveAndDespawnActiveOwnedMatesSlaves` → `ForceDismountAndDespawn` (Character.cs:2142–2163) → dismount + background `Thread.Sleep(10 min)` task → `RemoveAndDespawnAllActiveOwnedSlaves`. Re-login cancels the task (`CancelOwnedSlaveTask`, CharacterLifecycleService.cs:189–196).
- Canonical corroboration: reddit 2019 (emmmqb) "Stay off for 15 min, ur char will stay in game and despawn along with any summoned vehicles" (Unchained-era live); reddit 2014-10 (2h9nzz) "If you disconnect while your trade ship or farm cart is [out]… You need to relog before the ship or cart auto despawns or it drops all packs as free loot." **[R]** — the fork's 10-min hard-DC task matches the canonical "vehicle despawns shortly after owner goes offline"; the **pack-drop-on-despawn** canonical behavior is implemented for death but **not for despawn/abandonment** (upstream issue #1443 documents the pack-loss variant).

---

## 6. Portal / instance behavior

- `PortalManager.UsePortal` (387–442): cross-instance (`TeleportPosition.InstanceId != current`) → if active slave has loaded cargo → error 801 and **no teleport**; else despawn mates + slaves, then `SCLoadInstancePacket`. Same-world teleports (portal book, hereafter gates, worldgates within the world) do **not** touch the vehicle — it stays in the world (canonical: vehicles are world objects; only instance entry removes them) **[C]**.
- `InstantGameManager` blocks instant-teleport while a vehicle is summoned or a trade pack is equipped (InstantGameManager.cs:227–234) **[C]**.
- Upstream PR #1477 "fix(portal): despawn owned mates and slaves on instance teleport" is the origin of this behavior (merged into fork/upstream develop) **[U]**.

---

## 7. Stuck recovery

- **Rider's Escape** (skills 22130/22131/22143 소환물 탈출; special effect 90 `EscapeMySlave`) — canonical (ArcheRage DB, skill 22130): "Teleports your vehicle to safety if it becomes trapped in the terrain. Can be used within 20m of the trapped vehicle"; cast 2 s, cooldown 900 s, range 0–4 m **[D][R]**.
- Engine: `EscapeMySlave.Execute` → `SlaveManager.RidersEscape` (898–926): despawn packets → reposition at cast target (+spawn-offset nudge) → `Hide()` + `Spawn()` respawn. **No 20 m range check, no cooldown check server-side** (client-enforced only) **[C]**.
- Second unstick path (ships): the physics engine damages hulls that beach or grind static obstacles (`TickBeachedHullDamage`/`TickStaticObstacleHullDamage`, 1%/s — Slave.cs:711–784) and provides `GroundEscapeAssist` while grounded — the "grind yourself free or sink" behavior **[C]** (upstream marine-mechanics line).
- Third: since despawn is client-driven with no range gate in the fork, the despawn button works as an unstick even out of range (canonical would refuse with 312) **[C]**.

---

## 8. Restart recovery

- **Persistence row** — MySQL `slaves` (`SQL/aaemu_game.sql:493–510`): `id, item_id, template_id, attach_point, name, owner_type, owner_id, summoner, created_at, updated_at, hp, mp, x, y, z`; written by `Slave.Save` (`REPLACE INTO`; child slaves saved recursively, Slave.cs:1002–1047) and by `SaveManager`? **No** — slave saves are explicit (`Create`/`Delete`/disconnect paths), not part of the periodic SaveManager sweep **[C]**.
- **No boot-time respawn of player slaves**: the only `SELECT * FROM slaves` reads are the per-summon lookups (owner+item) and child-slave lookups (owner_type=2). On server restart, an out-vehicle does **not** reappear in the world; the owner re-summons from the item and HP/MP are restored (position columns are read but ignored — code comment: coords are "only required to show vehicle location after a server restart (if it was still summoned)" — i.e. intended for a client-side marker; no server path consumes them) **[C]**.
- Bound persistent doodads (persistent flag from `slave_doodad_bindings.persist`) are saved as `doodads` rows (owner_type=2) and re-spawned on re-summon via `SpawnPersistentDoodads(DoodadOwnerType.Slave, …)` (SlaveManager.cs:574–578) — the M3b persistence machinery reused **[C]**.
- World-spawned slaves: `SlaveSpawner` handles respawn (`RespawnTime`) and despawn (`DespawnTime`) per spawner JSON; lost on restart like all world spawns (re-initialized from JSON) **[C]**.
- Restart-recovery expectation for M4-3 (t_4a91a4f5): assert per-restart that (a) the DB row survives (item still bound, HP/MP correct), (b) re-summon works, (c) no duplicate slaves (id space shared with characters via `CharacterIdManager` — released on `DeleteSlaveById`) **[C]**.

---

## 9. Wire surface

**C2G (level 1):** `CSSpawnSlavePacket` (reads slaveId/x/y/z/rot/itemId/slot — **no handler action**; summon is skill-driven), `CSDespawnSlavePacket` → `Delete` (no owner check), `CSDestroySlavePacket` → `RemoveActiveSlave` (owner check), `CSBindSlavePacket` → `BindSlave` driver, `CSDiscardSlavePacket` → `UnbindSlave`, `CSChangeSlaveTargetPacket` (**stub**, reads only), `CSChangeSlaveEquipmentPacket` (**stub**, "TODO … coming soon"), `CSChangeSlaveNamePacket` (**stub**), `CSRepairSlaveItemsPacket` (**stub**, npcId only).

**G2C:** `SCSlaveCreatedPacket`, `SCSlaveStatePacket` (**hardcoded skillCount=0/tagCount=0**), `SCSlaveDespawnPacket`, `SCSlaveRemovedPacket`, `SCMySlavePacket` (map marker refresh, 5 s task `SendMySlaveTask`), `SCSlaveBoundPacket`, `SCEscapeSlavePacket`, `SCSlaveEquipmentChangedPacket` (full Write impl but **no sender exists** — grep `new SCSlaveEquipmentChangedPacket` = 0), plus unit packets (`SCUnitStatePacket`/`SCUnitAttachedPacket`/`SCUnitDetachedPacket`/`SCUnitsRemovedPacket`/`SCUnitDeathPacket`/`SCEnvDamagePacket`).

**Error codes (client strings = canonical rules; `ErrorMessageType.cs`):** 94 slave_start · 95 slave_cannot_spawn · 96 slave_cannot_bind · 97 slave_already_has_master · 98 slave_spawn_error_destroyed · 99 slave_spawn_error_already_spawned · 100 slave_spawn_ship_need_more_space · 101 slave_spawn_item_locked · 102 slave_unbind_first · 103 slave_shut_off_to_unbind · 104 slave_end · 288 slave_cannot_remove_while_in_combat · 312 slave_despawn_near_the_slave · 324 slave_cannot_bind_while_is_dead · 381 slave_cannot_repair_already_spawned · 382 slave_repaired · 417 slave_cannot_remove_while_in_carrying_backpack_doodad_items · 492 slave_ucc_imprinted · 540 slave_spawn_error_need_repair_time · 552 update_ucc_slave_exist · 801 slave_equipment_loaded_item (fork enum value; client key).

**Sent today (grep):** 97 (BindSlave driver lock), 101 (SummonSlave.CanDestroy), 540 (destroy cooldown), 801 (cargo-loaded gates), 417-family via 801; **never sent:** 95/96/98/99/100/102/103/288/312/324/381/382.

---

## 10. Engine gaps — fork vs upstream (both develop; diff = 0 lines on every slave file)

| # | Gap | Evidence | Status |
|---|---|---|---|
| 1 | **No despawn range check** (canonical 312) | `Delete`/`RemoveActiveSlave` never test distance; 312 never sent (grep) | upstream gap |
| 2 | **No despawn combat gate** (288) | no `IsInBattle` check on despawn paths; 288 never sent | upstream gap |
| 3 | **No harpooned-state despawn gate** | no harpoon-rope state check in `Delete` (rope state exists via `ShipHarpoonRopeController`) | upstream gap |
| 4 | **No abandonment despawn timer** (canonical ~5–10 min after owner leaves) | only death (20 s), logout (10-min task) and manual despawn paths exist | upstream gap |
| 5 | **Repair cooldown 10 min vs canonical ~5 min** | `Create` uses `RepairStartTime.AddMinutes(10)`; reddit 382upz "5 minutes" | upstream deviation |
| 6 | **`spawn_valid_area_range` (50) never enforced** | loaded, unreferenced; canonical 95/100 (need more space) never sent | upstream gap |
| 7 | **`CSDespawnSlavePacket` lacks owner check** | direct `Delete` call; any player can despawn by objId | upstream gap (security/abuse) |
| 8 | **No bind range/validity checks** | `BindSlave` checks seat-occupied + driver lock only; 96/102/103/324 never sent | upstream gap |
| 9 | **Character seat positions not applied** | `BindSlave` TODO "move to attach point's position"; chars sit at 0,0,0 | upstream gap |
| 10 | **`SCSlaveStatePacket` hardcodes 0 skills/0 tags** | packet writes constants | upstream gap |
| 11 | **Name/equipment/target/repair-NPC packets are stubs** | `CSChangeSlaveNamePacket`, `CSChangeSlaveEquipmentPacket`, `CSChangeSlaveTargetPacket`, `CSRepairSlaveItemsPacket` read-only; `SCSlaveEquipmentChangedPacket` exists but nothing sends it in a flow | upstream gap |
| 12 | **Position saved but never restored on re-summon** | `Save` writes x/y/z; `Create` read path discards them | upstream gap (restart-recovery nuance) |
| 13 | **No boot-time respawn of player slaves** | only per-summon SELECTs; world respawn only for `slave_spawns.json` spawners | upstream gap (R-dimension scope decision) |
| 14 | **Child-slave HP not persisted** | commented-out "TODO: Re-enable this when vehicle customization is enabled" (SlaveManager.cs:706–712) — child slaves always full HP | upstream gap |
| 15 | **Pack-loss on despawn** | upstream issue #1443 (open): packs vanish if placed into a despawning ship | upstream bug (open) |
| 16 | **Phantom ship equipment crash** | upstream issue #1404 (open): using a cannon after ship destroyed crashes; child doodads linger | upstream bug (open) |
| 17 | **Fishing boat fish-finder crash** | upstream issue #768 (open) | upstream bug (open) |

Verdict: **fork == upstream develop on every slave path** (verified 0-diff on 8 core files). M4-3 additions are upstream-aligned new work; three known bugs are already filed upstream.

---

## 11. Edge cases

1. **Despawn while passengers aboard** — `Delete` unbinds passengers first (UnbindSlave per attached char); passenger sees `SCUnitDetachedPacket`. No teleport-of-passengers; they are left at the vehicle's position. Canonical: riders are unseated on despawn (matches 1481 rider-state cleanup).
2. **Non-owner despawn** — `CSDespawnSlavePacket` allows it (gap #7); `CSDestroySlavePacket` owner-gated. Canonical is owner-only.
3. **Riding through a same-world portal** — vehicle stays; rider teleports with the vehicle left behind (canonical). Cross-instance: vehicle despawned before load.
4. **Death while mounted** — `DoDie` unbinds all passengers after death packet; passengers dismounted at death location.
5. **Destroyed vehicle + item deletion** — `SummonSlave.OnManuallyDestroyingItem` → `OnDeleteSlaveItem`: refuses while the slave is active (error 101 `SlaveSpawnItemLocked`); else cascades DELETE slave row + child doodads/slaves (recursive `DeleteSlaveById`) and releases the shared character/slave id.
6. **Boats in rivers** — summon depth search + `TODO: if not at ocean level, get actual target location water body height (for example rivers)` (SlaveManager.cs:414–415): river launches rely on the water surface of the target world; known TODO.
7. **Beached ships** — physics applies 1%/s hull damage while grounded against terrain or static obstacles (fork/upstream), plus `GroundEscapeAssist` to unstick; canonical "grind yourself free or sink".
8. **Two players, one driver seat** — second driver blocked by Owner's Mark (97) only if the buff is present; passenger seats are free-for-all (no party/owner restriction — canonical 1.2 allowed anyone to board).
9. **Slave death in combat vs logout** — no interaction: logout despawn is unconditional (canonical: logout is a "leave world" and despawns everything).

---

## 12. Implications for M4-3 (t_4a91a4f5) — design notes only

In canonicality order, the implementation must add/verify:

1. **Despawn gates** (client strings already in the enum): proximity → 312; combat → 288; harpooned → block; owner-only on `CSDespawnSlavePacket` too. Fix `Delete` to take a reason/checked path.
2. **Abandonment despawn timer**: owner-distance timer (~5–10 min canonical; suggest config) with pack-drop-to-ground on expiry (mirror `DestroyAttachedItems` behavior; closes #1443's pack-vanish family).
3. **Bind gates**: 96 (cannot bind), 324 (dead vehicle), 102/103 (unbind first / engine shut-off) where the client expects them; apply attach-point positions to characters (remove the TODO).
4. **Repair cooldown**: 10 min → 5 min (or `World.json` config), aligned with reddit/period reports; wire `CSRepairSlaveItemsPacket` + repair-NPC flow if in scope (381/382 errors).
5. **`spawn_valid_area_range`**: enforce at summon (95/100 errors) — boats need water/space, land vehicles need ground.
6. **Restart recovery** (R-dimension): assert DB row round-trip per restart (item-bound id, HP/MP, doodad persistence via M3b machinery); decide with Josh whether boot-time respawn of out-vehicles is in scope (canonical evidence: none — vehicles are re-summoned from items; the `slaves.x/y/z` columns exist for a client marker, not server respawn).
7. **Rider's Escape range/cooldown checks** (20 m, 900 s) server-side.
8. **Stub packets** (name/equipment/target) — only if M4-3 scope includes vehicle naming/customization; otherwise document as known-limited.
9. **Unit + E2E targets** (A-dimension): summon→ride→cargo-load→despawn-refused(801)→unload→despawn; death→pack-drop→sandglass repair→540 cooldown→re-summon; disconnect→10-min despawn; restart→row round-trip; portal-cross-instance→despawn; Rider's Escape teleport; per the fork's existing harness patterns (GateHarnessTests / E2e).

---

## 13. Evidence appendix

**Data (compact.sqlite3 r208022):** `slaves` (140 rows; kinds 1–9; portal_time 0–7 s; spawn_valid_area_range 50), `item_summon_slaves` (67+), `slave_doodad_bindings` (576; merchant ship 75 → 32 incl. Box0–20), `slave_bindings` (117), `slave_healing_point_doodads` (81), `slave_drop_doodads` (45), `slave_initial_items` (24) / `slave_initial_item_packs`, `slave_initial_buffs` (79), `slave_passive_buffs` (18), `slave_mount_skills` (324), `repairable_slaves` (29) + `repair_slave_effects` (4), `ship_models` (26), `items` (summon scrolls 15816/27266/5223/23398/25739/17626/15817/17863/20054/21588/27135; sandglasses 20061/23641/23642/27412, use_skills 17596/16561/17595/21651 → RepairSlaveEffect), `skills` (15802 summon; 22130/22131/22143 escape), `special_effects` (type 60/90), `quest_acts`/`quest_act_supply_items` (3736→20054; 2401/4568→15817). Queries run read-only.

**MySQL schema:** `SQL/aaemu_game.sql:493–510` (`slaves` row).

**Code paths (fork develop @ a31826b74 == upstream @ 31aff9869):** `Core/Managers/SlaveManager.cs` (Create 301–751, Delete 222–279, BindSlave 167–213, UnbindSlave 135–158, RidersEscape 898–926, RemoveActiveSlave 873–891, OnDeleteSlaveItem 1079–1100, UpdateSlaveRepairPoints 932–1027, SendMySlavePacketToAllOwners 823–839); `Models/Game/Units/Slave.cs` (DoDie 816–843, DestroyAttachedItems 848–936, DistributeSlaveDropDoodads 941–967, MarkSummoningItemAsDestroyed 972–981, Save 1002–1047, RegenTick 1049–1079, OnZoneChange 1081–1089, beached/obstacle damage 711–784); `GameData/SlaveGameData.cs` (all table loads 29–329, attach points 351–416); `Models/Game/Items/SummonSlave.cs` (+Template; details 29 bytes: SlaveType/SlaveDbId/IsDestroyed/RepairStartTime/SummonLocation); `Models/Game/Skills/Effects/RepairSlaveEffect.cs`; `SpecialEffects/SpawnSlave.cs` / `EscapeMySlave.cs` / `AttachTo.cs` / `DestroyAndSpawnSlave.cs`; `DoodadObj/Funcs/DoodadFuncAttachment.cs` / `DoodadFuncRecoverItem.cs`; `Core/Packets/C2G/CS{Spawn,Despawn,Destroy,Bind,Discard,ChangeSlaveName,ChangeSlaveEquipment,ChangeSlaveTarget,RepairSlaveItems}SlavePacket.cs`; `G2C/SC{SlaveCreated,SlaveState,SlaveDespawn,SlaveRemoved,MySlave,SlaveBound,EscapeSlave,SlaveEquipmentChanged}Packet.cs`; `Core/Managers/PortalManager.cs:387–442`; `Core/Managers/World/EnterWorldManager.cs:100–202`; `Core/Network/Connections/GameConnection.cs:85–111`; `Models/Game/Char/Character.cs:2078–2198`; `Core/Managers/ShipyardManager.cs:179–195`; `Core/Managers/InstantGameManager.cs:227–234`; `Models/Game/Slaves/SlaveSpawner.cs`; `SQL/aaemu_game.sql:493–510`; `Models/Game/ErrorMessageType.cs` (slave codes); `Models/Game/DoodadObj/Static/AttachPointKind.cs`; `Models/Game/Skills/Effects/SpecialEffectType.cs` (60 SpawnSlave, 72 DestroyAndSpawnSlave, 90 EscapeMySlave).

**History:** upstream marine-mechanics line — PRs #1400/1401 (marine mechanics), #1402 (full ship physics), #1406/1409/1414 (parts 4–6), #1417 (harpoon cannon), #1421 (part 7: inland water, harpoon rope, buoyancy), #1455 (death penalties/recovery), #1477 (portal despawn), #1481 (rider-state cleanup). Fork == upstream: 0 diff lines on all 8 core slave files vs upstream develop @ 31aff9869 (2026-08-11).

**Period behavior (research-derived, dated):**
- Ten Ton Hammer, 2014-07-25 "ArcheAge Ship Building and Repair Guide" — free quest rowboat; blueprints at Mirage (Gilda/Nui's Tears); 3-day platform protection; finished ship goes into inventory as summon; Shatigon's Sandglass repair.
- IGN ArcheAge wiki, 2014-09 "Ships"/"Rowboat" — shipyard → claim → summon item; Sandglass Pinch (rowboat, vendor) / Spoonful (clipper, longliner) / Handful (cutter, merchant ship); rowboat 2 seats; Eznan Cutter 8 cannons/4 pack slots; Merchant Ship 20 pack slots; "Your Own Boat" docks quest.
- Steam community guide, 2015-03-30 "A Basic Guide to Ships" — "You cannot desummon a ship that is in combat, is harpooned, or has a tradepack on it."
- reddit r/archeage — 2014-10-19 (2h9nzz) disconnect + trade ship/farm cart → relog before auto-despawn or packs drop as free loot; 2014-11 (2j8eod) "Thanks, Rider's Escape" — "It basically resummons the cart"; 2015-04 (3401zb) out-of-range despawn refusal + ~10-min auto-unsummon; 2015-06 (39wd45) ~5-min abandonment timer + packs drop; 2015-06 (382upz) clipper repair = 5 sand + 5 min before re-summon; 2019 (emmmqb) hard-DC → char stays ~15 min then despawns with vehicles.
- ArcheRage DB wiki, skill 22130 "Rider's Escape" — 0–4 m range, 2 s cast, 900 s cooldown, "teleports your vehicle to safety if trapped… within 20m of the trapped vehicle" (private-server DB page for the same 1.2 skill ids present in compact.sqlite3).

**Upstream issues:** #768 (fishing boat fish finder crash), #1404 (phantom ship equipment crash after destruction — child doodads linger), #1443 (packs placed into despawning merchant schooner are lost) — all open on develop.

*Dossier only — no code changed on this branch.*
