# Stray Source Diffs — 2026-08-26

Foreign uncommitted modifications found in `git status --short` (strays from dead
investigation lanes). Full diffs captured verbatim below; all three restored to HEAD
afterward via `git checkout --`.

---

## 1. AAEmu.Game/Core/Managers/SlaveManager.cs

- **Found:** 2026-08-26 (`git status --short`, branch `develop` @ c96e61ff1)
- **Probable origin lane:** `slavetest-hunt`
- **Verdict:** Temporary PB-006 diagnostic instrumentation (`[water-diag]` log line at boat
  spawn water-surface query). Self-tagged "remove after root-cause capture"; root cause was
  captured and fixed upstream — safe to discard.

```diff
diff --git a/AAEmu.Game/Core/Managers/SlaveManager.cs b/AAEmu.Game/Core/Managers/SlaveManager.cs
index 64fe75a50..da17f3a01 100644
--- a/AAEmu.Game/Core/Managers/SlaveManager.cs
+++ b/AAEmu.Game/Core/Managers/SlaveManager.cs
@@ -429,6 +429,12 @@ public class SlaveManager(WorldInstance parentWorldInstance)
 
                 var worldWaterLevel = world.Water.GetWaterSurface(spawnPos.World.Position, out _);
                 spawnPos.Local.SetHeight(worldWaterLevel);
+                // [water-diag] TEMPORARY PB-006 instrumentation — remove after root-cause capture
+                Logger.Info("[water-diag] boat spawn query at ({0:F1},{1:F1},{2:F2}) -> waterSurface={3:F2} oceanLevel={4:F2} floorAtSpawn={5:F2} ownerZ={6:F2}",
+                    spawnPos.World.Position.X, spawnPos.World.Position.Y, spawnPos.World.Position.Z,
+                    worldWaterLevel, world.Template.OceanLevel,
+                    World.Template.GeoData.GetHeight(spawnPos.World.Position),
+                    owner?.Transform.World.Position.Z ?? -1f);
 
                 // temporary grab ship information so that we can use it to find a suitable spot in front to summon it
                 var tempShipModel = ModelManager.Instance.GetShipModel(slaveTemplate.ModelId);
```

---

## 2. AAEmu.Game/Core/Managers/World/PhysicsManager.cs

- **Found:** 2026-08-26 (`git status --short`, branch `develop` @ c96e61ff1)
- **Probable origin lane:** rowboat diagnostics (ship physics/buoyancy debugging)
- **Verdict:** Log-level escalation of `AddShip` from `Debug` to `Info` plus `[ship-diag]`
  position payload. Pure diagnostics, no behavioral change beyond logging volume — safe to
  discard.

```diff
diff --git a/AAEmu.Game/Core/Managers/World/PhysicsManager.cs b/AAEmu.Game/Core/Managers/World/PhysicsManager.cs
index 7e15a8d24..5fe80af67 100644
--- a/AAEmu.Game/Core/Managers/World/PhysicsManager.cs
+++ b/AAEmu.Game/Core/Managers/World/PhysicsManager.cs
@@ -507,7 +507,7 @@ public class PhysicsManager
         EnqueueAddBody(slave.RigidBody);
         _buoyancy.AddForRectangularParallelepiped(slave.RigidBody, 3);
 
-        Logger.Debug($"AddShip {slave.Name} -> {SimulationWorld.Template.Name}");
+        Logger.Info($"[ship-diag] AddShip {slave.Name} -> {SimulationWorld.Template.Name} pos={slave.Transform.World.Position}");
     }
 
     /// <summary>
```

---

## 3. AAEmu.Game/Models/Game/World/GameObject.cs

- **Found:** 2026-08-26 (`git status --short`, branch `develop` @ c96e61ff1)
- **Probable origin lane:** boats diagnostics (`[bc-diag]` broadcast-path tracing for slave
  movement packets)
- **Verdict:** Temporary PB-006 instrumentation inside the packet broadcast loop; adds an
  `Info` log per slave movement broadcast. Self-tagged "remove after root-cause capture" —
  safe to discard.

```diff
diff --git a/AAEmu.Game/Models/Game/World/GameObject.cs b/AAEmu.Game/Models/Game/World/GameObject.cs
index 6b8e20850..d61a31102 100644
--- a/AAEmu.Game/Models/Game/World/GameObject.cs
+++ b/AAEmu.Game/Models/Game/World/GameObject.cs
@@ -161,6 +161,10 @@ public class GameObject : IGameObject
             WorldManager.GetAround(this, characters);
             foreach (var character in characters)
                 character.SendPacket(packet);
+            // [bc-diag] TEMPORARY PB-006 instrumentation — remove after root-cause capture
+            if (this is Units.Slave sl && packet is SCOneUnitMovementPacket)
+                Logger.Info("[bc-diag] slave={0} obj={1} region={2} receivers={3} depth={4}",
+                    sl.Name, sl.ObjId, Region == null ? "NULL" : Region.Id.ToString(), characters.Count, t_broadcastDepth);
             if (self && this is Character chr)
                 chr.SendPacket(packet);
         }
```

---

## Disposition

All three files restored to HEAD with `git checkout --` on 2026-08-26. A fourth stray,
`Character.cs`, had already been superseded by merged code and dropped earlier.
Full gate re-run followed the restore to confirm no regression.
