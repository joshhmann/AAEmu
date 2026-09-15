# Analysis: Navmesh, Geodata, and Localization (from archeage-v5.0-patches)

**Target System:** AAEmu 1.2 (`r208022`)  
**Reference Source:** `https://github.com/devincean-gif/archeage-v5.0-patches` (Devin's 5.0.7.0 patch tree)  
**Date:** 2026-09-08  
**Scope:** Engineering review of navmesh/geodata fixes, terrain streaming, and database localization tools for evaluation and adoption in `joshhmann/AAEmu`.

---

## Executive Summary

The `archeage-v5.0-patches` repository contains several high-value solutions directly addressing two perennial emulator challenges:
1. **Navmesh & Geodata:** Underground/cave NPC floating, instant combat evading/leashing, dynamic player-driven floor recording, and cell streaming hitching during vehicle travel.
2. **Translations & Localization:** Korean-to-English translation mechanics leveraging the built-in `localized_texts` table in `compact.sqlite3`, transport `<id> DO NOT TRANSLATE` anomalies, and client-vs-server text separation.

This document details the mechanics of these patches and maps out their safe, compliant adoption for AAEmu 1.2 under our Upstream Alignment conventions (specifically Rule 3 regarding read-only reference data).

---

## 1. Navmesh & Geodata Deep Dive

### 1.1 The Cave NPC Floating Ceiling Bug (`npc-cave-float-clamp`)

#### The Problem & Root Cause
In caves, tunnels, and multi-tier structures, NPCs that aggro players float straight up through the cave ceiling toward the surface of the mountain.
- **Root Cause:** In the absence of `server_ai_geo_data.sqlite3` (or when the client asset pack lacks static collision meshes for interior structures), `WorldManager.GetHeight(zone, x, y, refZ)` fails its interior collision raycast.
- It falls back to the **terrain heightmap**, which only represents the outdoor mountain peak above the cave.
- When an NPC moves, each tick calculates its new Z toward this surface heightmap. As its reference Z rises, the error compounds, causing the NPC to "climb" into the sky/roof.

#### Devin's Solution A: Heuristic Ground Rise Clamp (`ClampGroundRise`)
- Added `World.SpawnHeight.MaxGroundRise` (default: **8.0m**, configurable in `World.json`).
- In movement height resolution (`TrackAndStoreCoordinates.cs`):
  ```csharp
  private static float ClampGroundRise(float resolved, float referenceZ)
  {
      var maxRise = AppConfiguration.Instance.World?.SpawnHeight?.MaxGroundRise ?? 8.0f;
      if (maxRise <= 0f || referenceZ == 0f)
          return resolved;
      if (resolved - referenceZ > maxRise)
          return referenceZ; // Treat sudden upward leap as cave ceiling; keep current floor Z
      return resolved;
  }
  ```
- **Result:** Legitimate gradual slopes (<8m rise per step) pass through, but sudden vertical snaps to surface mountains are rejected, holding the NPC firmly on the cave floor.

#### Devin's Solution B: 2D Combat Leash Distance (`BaseCombatBehavior.cs`)
- **Immediate Side Effect of Clamping:** Once NPCs were held on the cave floor, they began **instantly resetting/evading upon being attacked**.
- **Root Cause:** `BaseCombatBehavior.ShouldReturn` checked return distance in 3D using `Vector3.Distance`. Because the NPC's `IdlePosition.Z` had been resolved at spawn to the outdoor mountain surface (50m–100m above), the mob calculated itself as already being 50m+ away from home the instant combat started.
- **The Fix:** Switched return/leash calculations to 2D horizontal distance:
  ```csharp
  var ownerPos = Ai.Owner.Transform.World.Position;
  var targetPos = Ai.Owner.CurrentTarget.Transform.World.Position;
  var idlePos = Ai.IdlePosition;

  var distanceToTarget = Vector2.Distance(new Vector2(ownerPos.X, ownerPos.Y), new Vector2(targetPos.X, targetPos.Y));
  var distanceToIdlePosition = Vector2.Distance(new Vector2(ownerPos.X, ownerPos.Y), new Vector2(idlePos.X, idlePos.Y));

  return (distanceToTarget > returnDistance || distanceToIdlePosition > returnDistance)
      && distanceToIdlePosition <= absoluteReturnDistance;
  ```

#### Devin's Solution C: Player-Driven Navmesh Floor Recorder (`SaveGeoDataMode`)
Rather than manually constructing 3D collision meshes for every cave, Devin implemented an automated live recorder:
1. **Trigger:** Set `SaveGeoDataMode: true` in `World.json` or run `/sg true` in-game.
2. **Sampling:** In `ReportClientHeight`, whenever an authenticated player walks and their client Z differs from the outdoor terrain heightmap by `> 0.5m`, the server creates equilateral triangular floor polygons around the player's position.
3. **Persistence:** Written to MySQL tables `navigation_mesh_raw` and compacted into `navigation_mesh` (partitioned by `zone_id`).
4. **Server Boot:** `_navMeshCollector.LoadRawFromDatabaseAndCompact()` loads these floor triangles into memory at startup.
5. **Forgiving Spatial Query (`GetCorrectNpcHeight`):**
   - Traditional point-in-polygon queries require dense mesh coverage.
   - Devin added a 3.0m nearest-neighbor fallback: if a coordinate isn't strictly inside a triangle, it takes the height of the closest recorded floor sample within 3m whose Z is within `MaxGroundRise` of the reference Z.
6. **Spawn Grounding (`GetNearestNavFloor`):**
   - Used by `NpcSpawnerNpc` and spawner initialization to snap floating cave spawns down onto the recorded floor:
     ```csharp
     var navFloorZ = WorldManager.Instance.GetNearestNavFloor(zoneId, spawnPos.X, spawnPos.Y);
     if (!float.IsNaN(navFloorZ) && navFloorZ < spawnPos.Z - 1f)
         finalSpawnZ = navFloorZ;
     ```
7. **Export Utility (`scripts/dump-navmesh.sh`):**
   - Scrubs fall-damage anomalies and zone-transition glitches (`az > 500m`).
   - Dumps clean SQL ready to be shared across servers (`server_navmesh_floors.sql`).

---

### 1.2 Vehicle Chunk Streaming Latency (`vehicle-world-streaming`)

- **The Problem:** Driving high-speed vehicles (cars, wagons, speedboats) across world cell boundaries caused **300ms–480ms server tick spikes**.
- **Root Cause:** `WorldCell.VerifyCellLoaded()` loaded static geometry, heightmaps, and navmesh synchronously on the main simulation tick thread when an entity crossed a boundary.
- **The Solution:** Added `TerrainPreloadManager.cs`, a background worker that monitors entity velocities and pre-loads cells within a 2km radius asynchronously ahead of the vehicle trajectory.

---

## 2. Translations & Localization Deep Dive

### 2.1 In-Place Database Translation (`data-ops/apply-english-localization.py`)

#### The Discovery
In ArcheAge's `compact.sqlite3`, primary definition tables (`items`, `npcs`, `skills`, `quests`, `doodads`, `zones`) store their primary `.name` and `.desc` columns in **Korean** (e.g. `items.name` = `제작서: 개기일식 가죽 허리띠`).

However, `compact.sqlite3` ships with an extensive, built-in translation table: **`localized_texts`** (263,635 rows in 1.2; 442k+ rows in 5.0).
- Columns include: `tbl_name`, `tbl_column_name`, `idx` (corresponding to the parent table `id`), `ko`, `en_us`, `de`, `fr`, `ru`, `ja`, `zh_cn`, `zh_tw`.

#### The Translation Script
Devin's `data-ops/apply-english-localization.py` indexes this table and executes an automated batch update:
```sql
CREATE INDEX IF NOT EXISTS ix_lt_lookup ON localized_texts(tbl_name, tbl_column_name, idx);

UPDATE "{tbl}" SET "{col}" = (
    SELECT en_us FROM localized_texts lt
    WHERE lt.tbl_name = ? AND lt.tbl_column_name = ? AND lt.idx = "{tbl}".id
)
WHERE EXISTS (
    SELECT 1 FROM localized_texts lt
    WHERE lt.tbl_name = ? AND lt.tbl_column_name = ? AND lt.idx = "{tbl}".id
);
```

#### Architectural Impact: Client vs. Server
- **In-Game Client Text:** Unaffected. The ArcheAge 1.2 game client reads display strings from its local client `game_pak` (`game/text/`).
- **Server Text:** Translating the database transforms server-side operations:
  - Server logs show human-readable English entity names instead of Korean glyphs.
  - GM commands (e.g. `/item info`, `/spawn`, `/buff`) display English names.
  - Bot controllers (PlayerBots) and LLM-driven agents receive clear semantic labels.
  - Archaeology MCP queries (`query_sql`, `lookup_row`) immediately surface English text without manual cross-joins.

### 2.2 Public Transport Strings (`<id> DO NOT TRANSLATE`)
- Carriages and airships in 5.0 displayed `<id> DO NOT TRANSLATE` due to placeholder strings in unlocalized rows of `compact.sqlite3` or missing keys in the client-side localization dictionary.
- Resolved by either filling the missing strings into `localized_texts` or referencing the parent vehicle route definition.

---

## 3. Applicability Matrix & Action Plan for AAEmu 1.2

| Finding / Component | 5.0 Implementation | Status in AAEmu 1.2 | Action Plan & Recommendation |
|---|---|---|---|
| **2D Combat Leash** | `Vector2.Distance` in `BaseCombatBehavior.cs` | **Direct Hit** (1.2 has exact same 3D leash bug) | **High Priority Port:** Update 1.2 combat return/leash logic to use horizontal distance so cave mobs don't evade on aggro. |
| **Cave Rise Clamp** | `ClampGroundRise` with `MaxGroundRise: 8m` | **Direct Relevance** (1.2 is currently testing ±2m deadband / whitelist) | **Evaluation:** Compare Devin's upward-only threshold clamp against 1.2's deadband logic in `Npc.cs` / `WorldManager.cs`. |
| **Navmesh Floor Recorder** | `SaveGeoDataMode` writing to MySQL | Absent in 1.2 (1.2 uses `.bai` files via `AiGeodataManager`) | **Reference Tool:** Keep in reserve as an additive GM tool if specific 1.2 cave dungeons lack `.bai` geodata. |
| **Terrain Preloading** | Asynchronous `TerrainPreloadManager` | Synchronous cell loading in `WorldCell.cs` | **Performance Probe:** Monitor tick intervals during `TransferRideE2e` and high-speed vehicle physics; adopt if cell boundary stalls appear. |
| **Database Translation** | Direct in-place update of `compact.sqlite3` | **Locked Constraint:** Upstream Rule 3 forbids modifying canonical `compact.sqlite3` (MD5: `78b3bdbf038db3b927056106efdf91af`) | **Adopt with Seam:** Do NOT commit an overwritten canonical DB. Instead: <br>1. Leverage 1.2's `LocalizationManager.cs` (which already loads `localized_texts`).<br>2. Provide the script as an offline tool for generating local dev copies for testing/bot harnesses. |

---

## 4. Immediate Recommendations for Muse & Contributors

1. **Adopt the 2D Leash Fix:**
   Inspect `AAEmu.Game/Models/Game/AI/v2/Behaviors/BaseCombatBehavior.cs` (or 1.2 equivalent). Verify whether `Vector3.Distance` is used for idle return checks and replace with 2D planar distance.
2. **Corroborate Cave Z-Clamping:**
   Review 1.2's ongoing height-resolution telemetry. If mobs in subterranean areas (mines, basements, caves) climb toward surface coordinates, Devin's simple upward rise threshold (`MaxGroundRise`) provides a clean, self-contained guard.
3. **Localization in Code vs Data:**
   Rather than mutating `compact.sqlite3`, ensure that diagnostic and GM commands utilize `LocalizationManager.Instance.Get(tbl, col, id, fallback)` so that English names are cleanly resolved at runtime while maintaining byte-exact upstream database parity.
