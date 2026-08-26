# SHIPS-01 Domain Dossier (2026-08-25 exploration)

Scorecard row at writing: ships = unexplored half of M4 vehicles (SLAVE-01 covered carts + basic lifecycle, W=2/A=1).
Repo: joshhmann/AAEmu `develop` @ `214bed8`; graphify graph built from `2b4b99c0` (verified ancestor of HEAD — fresh).
Scope discipline: cart/lifecycle content NOT re-covered; this file is ship-specific (shipyard build flow, naval physics, naval weapons, salvage).

## Verdict: ships are a DEEP, largely real implementation — far beyond stub

Sailing physics is a dedicated Jitter2 simulation with per-kind tuning, wind models, shore/ship/cliff/barrier interactions,
hull damage, harpoon tow ropes, and server-authoritative replication. The shipyard build flow is fully wired
(packet → design consumption → step model → completion ceremony → summon scroll). The sharpest gaps are
**content/data-driven** (no Dreadnaught/pirate-ship templates in 1.2 data), **shipyard frames are memory-only**
(lost on restart), and **salvage/sunken-ship gameplay does not exist server-side**.

---

## 1. Ship template inventory (compact.sqlite3, VERIFIED)

### 1.1 Slave kinds (`AAEmu/Game/Models/Game/Slaves/SlaveKind.cs:3-17`)
Naval kinds: `BigSailingShip=1`, `SmallSailingShip=2`, `Speedboat=3`, `Boat=4`, `Fishboat=9`;
post-1.2 placeholders: `MerchantShip=10`, `Leviathan=11`. `SlaveTemplate.IsABoat()` (`Models/Game/Slaves/SlaveTemplate.cs:35-38`)
covers Boat/Fishboat/Speedboat/MerchantShip (+ Small/Big sailing ships — see file for full set).

### 1.2 Ship slaves table (`slaves`, kind → name; read-only sqlite)
| Kind | Templates (id / model) |
| --- | --- |
| 1 BigSailingShip | carriage 7, galleon 9, yellow small sail 11, ferry 13/44/55, ray-skiff 17, mid-hull 19, auction clipper 23, broken houses 28/29, training dummies 32–34, ferrari joke 105, salvage buoy 109, mid sail S1–S3 139/142/143/145, cruise ship 63/83 |
| 2 SmallSailingShip | **Ezna clipper 21** (model 393), Growly clipper 24, **Merchant ship (무역선) 75** (model 1205), Bipa clipper 92, sea-war blue/red sails 96/98, salvaged merchant 101, lost wreck 120 |
| 3 Speedboat | **Harpoon clipper 14** (128), speedboat 52, Adventure clipper 76 (+ sample variants 81/82) |
| 4 Boat | **Rowboat 15** (129), memorial boat 91, duck boat 102, lonely-soul rowboat 103 |
| 9 Fishboat | Quiet researcher fishing boats 110/113/114 (models 1360/1383/1384) |

No Dreadnaught/PirateShip playable hull in 1.2 data (`EznaCutter..PirateShip` enum exists in code
`Models/Game/Slaves/SlaveInitialItems.cs:12-21` but PirateShip has no active slave row). "해상전장" (naval-battlefield) blue/red
clipper rows 96/98 exist — data present even if no scenario drives them.

### 1.3 Ship physics models (`ship_models`, 26 rows, ids 1–26)
Loaded into `ShipModelV1` (`Models/Game/Models/ShipModelV1.cs:3-26`): Velocity/Mass/MassCenter*/MassBoxSize*/WaterDensity/
WaterResistance/Accel/ReverseAccel/ReverseVelocity/TurnAccel/Keel* etc. Examples: id 1 mass 40000 vel 6.5;
id 13 mass 120000 vel 12.6 turn_accel 1.5; id 17 mass 800 vel 8.0 turn_accel 9.9 (rowboat-class).

### 1.4 Naval weapons as child slaves (`slave_bindings`)
- Clipper 21 binds **8× cannon slave 10** (대포, kind 8 `SlaveEquipment`) at attach points 9–16.
- Merchant 75 binds 2× cannon 10 (points 9,10).
- Harpoon clipper 14 and fishboat 110 bind **harpoon turret slave 48** (작살포, model 895) at point 9.
- Mount skills: cannon 10 → mount skill 16; turret 48 → 18 & 35 (`slave_mount_skills`) — firing rides the standard
  mounted-skill path [STRONGLY_INFERRED: exact driver-seat gating for ship cannons not traced end-to-end].
- Ship doodad bindings (`slave_doodad_bindings`): seats (doodad 3446, persist=t — e.g. clipper points 60–63, merchant 60–79,
  fishboat incl. 6655 chairs), helm/wheel doodads, lanterns, cargo mounts (58/59), figureheads.

## 2. Ship BUILDING flow — VERIFIED end-to-end in code

Chain: **design certificate item → CSCreateShipyardPacket → drydock Shipyard unit → CraftEffect contributions per step →
completion ceremony → summon scroll item → normal Slave summon**.

1. **Design items** (`items`): 도면 designs — Adventure clipper 23636 (use_skill 14879), Merchant 23698 (12745),
   fishing boat 28013 (22198); legacy designs 14782/15563/17705/17707/17861. Completed ship = summon scroll item
   (item_id column): 23618 Adventure clipper scroll, 23398 merchant scroll, 27135 fishboat scroll, all use_skill 15802
   (SummonSlave). VERIFIED via items table.
2. **Placement**: `CSCreateShipyardPacket` (0x0fc, `Packets/C2G/CSCreateShipyardPacket.cs:9-35`) reads template id, pos/rot,
   designItem id, AABB, autoUseAAPoint → `ShipyardManager.Create` (`Core/Managers/ShipyardManager.cs:40-94`):
   spawns a `Shipyard` Unit (BaseUnitType.Shipyard, Level 30) at step 0 model, registers in `_shipyard`.
3. **Bill of materials consumed AT PLACEMENT** (`RemoveRequiredItems`, ShipyardManager.cs:96-166): consumes the design item,
   taxes (`Taxations[TaxationId].Tax`), and **all reagents of the design's use skill up front**
   (e.g. 14879 → lumber 8318×10 + iron 8337×10; 12745 → 8337×100; 22198 → 4 mats ×1). DEVIATION vs retail where
   materials are donated per-contribution during construction; here contribution steps only tick counters.
4. **Contribution steps**: `shipyards` (14 rows; 5 active: 7 harpoon clipper, 8 Bipa clipper, 12 adventure clipper,
   13 merchant, 14/15 fishing boat) + `shipyard_steps` define ordered steps {step, model_id, skill_id, num_actions, max_hp}.
   Step skills are the plank packs: 14737 선박 건조-목재 (timber), 14738 -옷감 (fabric), 14740 -철재 (iron), 11823 legacy.
   Player casts the matching pack skill on the frame → `CraftEffect.Apply` WorldInteractionGroup.Craft branch
   (`Models/Game/Skills/Effects/CraftEffect.cs:47-84`): validates `usedSkill == currentStep.SkillId`, then
   `Shipyard.AddBuildAction()` (`Models/Game/Shipyard/Shipyard.cs:441-463`) advances NumAction→step→-1 (complete),
   broadcasts `SCShipyardStatePacket` after every action.
5. **Completion**: NOTE the seam — `CraftEffect`'s WorldInteractionGroup.Craft branch only ticks counters; when
   CurrentStep reaches −1 it updates ShipyardData but does NOT grant anything (`CraftEffect.cs:67-78`). The scroll grant +
   ceremony live in the switch's **default** branch: an interaction on the finished frame by the owner whose effect has no
   recognized wi group calls `ShipyardManager.ShipyardCompletedTask` (CraftEffect.cs:142-152), which grants the summon
   scroll (`Template.ItemId`), sets Step=1000 (launch-ceremony visual), schedules `ShipyardCompleteTask` after
   `CeremonyAnimTime` ms (ShipyardManager.cs:193-204); `ShipyardCompleted` (179-191) then summons the actual ship through
   `ParentWorld.SlaveManager.Create(character, skillData…)` using the new scroll and removes the frame.
   Which skill/effect the 1.2 client fires for this final "launch" interaction is data-dependent and UNVERIFIED live.
6. **Frame decay/tax**: 1-min `ShipyardTickTask` applies TaxProtection buff until tax duration elapses, then Deterioration
   debuff draining 7 HP/s (222-282); frame death → RemoveShipyard (OnDeath hook, Shipyard.cs:435-439).

**Build-flow evidence strength: STRONG (VERIFIED)** — every link exists in code with packet-level state sync.
Untested live E2E: nobody has driven a real client/bot through placement→plank×N→ceremony→summon.

### Persistence gap (sharpest build-flow finding)
`ShipyardManager.Load` (284-343) loads ONLY templates from compact.sqlite3; there is NO MySQL persistence of placed
shipyard frames anywhere (`grep shipyard SQL/` = 0 matches). All half-built frames vanish on restart. Built ships DO persist:
`Slave.Save` writes MySQL `slaves` row (`Models/Game/Units/Slave.cs:1002-1047`), re-summon restores HP/name/child
doodads/cannons (`Core/Managers/SlaveManager.cs:358-389, 581-585, 643-734`). Destroyed ship marks summon item destroyed
and starts 10-min repair lockout (`SlaveManager.cs:481-499`, `Slave.cs:972-981`).

## 3. Boarding, seats, helmsman controls

- **Boarding**: `CSBindSlavePacket` (0x031) → `SlaveManager.BindSlave(connection, tlId)` — always binds
  **AttachPointKind.Driver** (`SlaveManager.cs:209-220`); ownership gate via OwnersMark buff blocks non-owner driving
  (183-187); dead-slave bind refused with canonical error 176-180. Passenger attachment map `AttachedCharacters[attachPoint]`
  exists and replicates via SCUnitAttachedPacket on AddVisibleObject (`Slave.cs:682-699`), but there is no C2G path that
  binds a *specific non-driver seat* on a slave — CSBoardingTransferPacket (0x067) is Transfer/carriage-only
  (`CSBoardingTransferPacket.cs:36-67`). Ship passenger seating therefore relies on seat DOODADS
  (DoodadFuncAttachment → Seat.LoadPassenger), not slave attach points [STRONGLY_INFERRED from binding data + transfer path].
- **Helmsman input**: client sends `CSMoveUnitPacket` with `ShipRequestMoveType` (throttle/steering sbytes);
  handler sets `slave.ThrottleRequest/SteeringRequest` with only a TODO on driver validation
  (`Packets/C2G/CSMoveUnitPacket.cs:70-87`). Physics thread smooths requests → Throttle/Steering, zeroes them without
  a driver (`Core/Managers/World/PhysicsManager.cs:564-600, 584-589`), and broadcasts authoritative
  `SCOneUnitMovementPacket(ShipMoveType)` with bank angle, wave pitch, ground pitch, smoothed velocity (700-814).
- **Unbind/despawn**: UnbindSlave detaches + triggers Unmount buffs (135-158); owner despawn gates owner-only/range 5 m
  error 312/in-combat 288 (`TryDespawnOwnedSlave`, 920-958); Delete refuses while holding trade packs (801 gate inside
  Delete, 229-286).

## 4. Sailing physics — what's REAL (VERIFIED, extensive)

Per-world `PhysicsManager` runs a dedicated Jitter2 thread (`TargetPhysicsTps`, fixed-step accumulator,
PhysicsManager.cs:167-215). Per ship tick pipeline: pending add/remove → `_physWorld.Step` → water/floor cache →
transform sync → `BoatPhysicsTick` → harpoon tension/tear recoil → shore resolve → current drift →
`ApplyForceAndTorque` per ship → ship–ship pairs → doodad contacts → static barriers → cliff interaction →
harpoon pair tow → beached/static hull damage ticks → ShipTuningDebug → movement broadcast (219-470).

`ShipController.ApplyForceAndTorque` (Physics/ShipController.cs:305-706) implements:
- Grounded/shoal state machine: latched grounding side (stern/bow), escape-assist ramp (GroundEscapeAssist),
  asymmetric throttle multipliers, anti-stall nudge, crawl-speed floor ±1 (418-500).
- Wind: Official model = +15% hard cutoff within ±15° of N↔S axis for sailing ships (235-245); optional Realistic model
  with SquareRig/LateenRig profiles rotating with game clock (117-253); river flow wins over open-sea wind (183-194).
- Speed caps from `ship_models.Velocity/ReverseVelocity` × MoveSpeedMul × windMul; opposing-throttle brake tuning;
  non-linear approach curves for both linear accel and yaw rate; per-SlaveKind max yaw table overriding DB steer_vel
  (95-104: Boat 4.35°/s … Speedboat 5.85°/s); counter-steer boost; turn-speed slowdown multiplier (up to 10%).
- Coast drag caps, grounded damping, vertical velocity damping underwater, lateral velocity damping, upright stabilization
  torque (WaterVerticalVelocityDampPerSec etc., ShipMotionDefaults 41-85).
- Hull damage: beached 1%/s (TickBeachedHullDamage, Slave.cs:711-732), static-obstacle contact 1%/s with 0.35 s grace
  (739-766), ship–ship collision %HP with per-pair cooldown (ApplyShipHullCollisionDamage, 786-803; ShipShipInteraction).
- Buoyancy: `Buoyancy` force generator over ocean-level fluid box, rectangular-parallelepiped buoyancy volumes
  (PhysicsManager.cs:87-93, 508).
- Static barriers: per-world polyline walls ingested from BAI geo data with spatial grid
  (`Models/Game/World/ShipStaticBarrierZones.cs:1-40`, ShipStaticBarrierSpatialGrid).
- Siege AoE vs ships: siege-damage skills hit long hulls via mass-box OBB test, not just pivot radius
  (Physics/ShipSiegeAoEHit.cs:19-68) — naval combat vs castle cannons works.

## 5. Naval weapons — harpoons VERIFIED, cannons partial

- **Harpoon rope system is real**: `ShipHarpoonRopeController` — Launch skill 13749, Cut 13750, CSSkillControllerState sync,
  tension/tear with hull recoil impulse (`SkillControllers/ShipHarpoonRopeController.cs:20-21` header + PhysicsManager tick
  hooks 274-279, 317). Tow physics: terrain hooks AND ship-pair towing (`ShipHarpoonTowPhysics.ApplyTerrainHookTow`,
  `ApplyShipPairHarpoonTowImpulses`; taut-rope gating of crawl speed, ShipController.cs:716-735). Rope state struct
  `ShipHarpoonRopeState` per child turret.
- **Ship cannons**: exist as child slave units (cannon 10) bound to attach points with mount skills; damage/hp/repair
  machinery generic to slaves (repair points by HP thresholds, SlaveManager.UpdateSlaveRepairPoints 1034-1129). Exact
  player→cannon fire path not traced (see UNKNOWNs).

## 6. Treasure / sunken-ship salvage — mostly ABSENT

- No server-side shipwreck salvage system: grep `sunken|salvage|treasure` in AAEmu.Game finds treasure-map digging
  (land), treasure chests, and nothing that consumes ship wrecks. Client data has "Salvageable Shipwreck" doodad 1669 and
  salvaged-ship slave rows 101/109/120, but no code path spawns or recovers them [UNKNOWN → data present, behavior absent].
- Underwater treasure gimmick: Gimmick template 37 "Recovered Treasure Chest" floats to surface via
  `GimmickMovementFloatToSurface` (`Core/Managers/GimmickManager.cs:82-86`) — the only implemented underwater-recovery piece.
- Trade packs on ships: pack drop-on-death floats/sinks by depth <30 m rule (`Slave.DestroyAttachedItems`, 848-936).

## 7. Packet inventory (ship/shipyard family)

Offsets in `CSOffsets.cs`: CSSpawnSlave 0x02e, CSDespawnSlave 0x02f, CSDestroySlave 0x030, CSBindSlave 0x031,
CSDiscardSlave 0x032, CSChangeSlaveName 0x034, CSRepairSlaveItems 0x035, CSTurretState 0x036,
CSChangeSlaveEquipment 0x037, CSBoardingTransfer 0x067, CSCreateShipyard 0x0fc; commented out: 0xbf/0xc8/0xc9 field slaves.

| Packet | Status |
| --- | --- |
| CSCreateShipyardPacket (C2G) | IMPLEMENTED — full decode, creates frame |
| SCShipyardStatePacket (G2C) | IMPLEMENTED — full ShipyardData marshal |
| CSSpawnSlave / CSDespawnSlave / CSDestroySlave / CSDiscardSlave / CSBindSlave / CSChangeSlaveName | IMPLEMENTED (classes exist, handlers wired) |
| CSRepairSlaveItems / CSChangeSlaveEquipment | CLASSES EXIST — repair/equip paths partially wired (repair points exist; equipment customization TODO'd in SlaveManager.cs:537) |
| CSChangeSlaveTarget (0xfff) | STUB — opcode placeholder "not in offsets" (CSOffsets.cs:52) |
| CSBoardingTransfer (0x067) | IMPLEMENTED for Transfers only, not slaves |
| SCSlaveCreated/State/Bound/Removed/Despawn/EquipmentChanged, SCMySlave, SCShipyardState (G2C) | IMPLEMENTED (7 slave/shipyard entries in SCOffsets) |
| Helm movement | CSMoveUnitPacket ShipRequestMoveType IMPLEMENTED; driver validation TODO |

## 8. Behavioral contract (as-implemented)

- Build flow: use design → client sends CSCreateShipyard → server validates+consumes design/tax/all mats → Shipyard frame
  spawns (step-model grows visually) → players cast step skill (timber/fabric/iron packs) N times per step →
  SCShipyardState broadcast each hit → final step: owner gets summon scroll, ceremony anim (12–27 s), frame removed,
  scroll summons real ship (water-depth-aware spawn scan up to 50 m+LOA in front, SlaveManager.cs:419-462).
- Sail flow: board (bind Driver) → WASD → ShipRequestMoveType → physics thread integrates → SCOneUnitMovementType
  broadcast to all → disembark via UnbindSlave; no-driver ships coast to stop with zeroed input.
- Death: ship DoDie → unbind passengers, drop packs, debris doodads per `slave_drop_doodads`, mark item destroyed,
  remove from physics, despawn ~20 s (Slave.cs:816-843). Repair: 10-min lockout then re-summon.
- Persistence matrix: built ship = MySQL-persistent (re-summon restores state); shipyard FRAME = memory-only (restart loss);
  ship position saved but intentionally unused on re-summon (SlaveManager.cs:380-385 comment).

## 9. SLICE PLAN (smallest safe first)

**Slice 1 — Rowboat E2E (recommended first)**: summon rowboat (slave 15, item summon scroll or `/slave spawn 15`)
→ water-depth spawn assert → bind driver → inject CSMoveUnitPacket ShipRequestMoveType throttle/steer →
observe SCOneUnitMovementPacket stream + Transform displacement > X over T seconds → steer reversal changes heading sign →
UnbindSlave → despawn clean (no leaked RigidBody: `Physics._shipControllers` empty for Id).
PASS: all asserts green with zero error logs; PASS criteria measurable via bridge charPos + packet tap (same seams as indun E2E stack).

**Slice 2 — Clipper build flow**: grant design 23636 + mats 8318×10/8337×10 + tax money → CSCreateShipyardPacket inject →
assert frame spawned at step 0 model → cast step skills (14737/14738/14740 per shipyard_steps for template 12) until
CurrentStep −1 → assert scroll 23618 granted + ceremony scheduled + frame removed → use scroll → assert Slave 76 spawned
on water with 8 cannon children. PASS: inventory deltas exact, SCShipyardState sequence monotonic.

**Slice 3 — Clipper sail + cannon presence**: bind driver on built clipper, throttle run ≥10 s, assert speed ≤
ship_models cap × wind mul; assert AttachedSlaves contains 8× slave 10 with mount skill 16; harpoon turret fire on
harpoon clipper variant (Launch 13749 → rope engaged → tow impulse visible in velocity).

Slice 1 is S (all seams proven), Slice 2 M (multi-step orchestration + inventory bookkeeping), Slice 3 M-L (physics timing assertions flaky-prone; needs tolerance windows).

## 10. Sharpest UNKNOWNs

1. **Passenger seat binding on ships**: no C2G slave-seat packet besides Driver bind; seat doodads (3446) presumably carry
   boarding via DoodadFuncAttachment — needs live confirmation whether 1.2 clients can actually board clipper passenger
   seats against this server.
2. **Cannon firing path**: mount skill 16 on cannon child-slave — which seat/interaction lets a player cast it, and does
   projectile/AoE work vs ships (ShipSiegeAoEHit covers siege only)?
3. **Live E2E absence**: zero runtime evidence anyone ever completed a ship build or sailed a big hull on this fork;
   physics constants look production-tuned (recent heavy iteration per comments) but no recorded proof run.
4. **Salvage content**: client data rows exist (wreck slaves 101/120, buoy 109, Salvageable Shipwreck doodad 1669) with no
   server behavior — deliberate 1.2 scope cut or missing feature, unknown.
5. **Shipyard frame restart loss**: confirmed memory-only; unknown whether upstream AAEmu has a save path worth porting.
6. **Launch-ceremony trigger**: `ShipyardCompletedTask` is reachable only via CraftEffect's default (ungrouped wi)
   branch (`CraftEffect.cs:142-152`) — the exact client interaction/skill that fires it on a finished frame is
   data-dependent and unverified; if no such effect exists in 1.2 data, completed frames would strand at step −1.

## 11. `/testslave` ("`/slavetest`") bug-hunt — packet errors + naked slaves ROOT-CAUSED & FIXED (2026-08-25)

Human prod report: "running the /slavetest GM command produces PACKET ERRORS, and the spawned slaves appear to be
missing their clothing/equipment". There is no command literally named `slavetest`; the report maps to
`/testslave` (alias `/test_slave`, `Scripts/Commands/TestSlave.cs`). Isolated-stack repro
(E2E_ROOT=/root/aaemu-e2e-stest, COMPOSE_PROJECT_NAME=stestacc, ports 2337/2339/2350/2360/2334/2380/db 24306)
reproduced both symptoms and proved causality (baseline in-world window: 0 marshal errors; post-command window:
3).

### Root causes (both from TestSlave hand-rolling a `new Slave` instead of using SlaveManager.Create)

1. **Packet errors — null slave Name**: the hand-rolled Slave never set `Name`. Every G2C packet serializing the
   unit (`SCUnitStatePacket.cs:72 stream.Write(_unit.Name)`) then threw server-side
   `[ERROR] PacketStream - Error writing string. System.ArgumentNullException: Value cannot be null. (Parameter 's')`
   at PacketStream.Write(String) — 3× per spawn in the repro log. A mid-write abort truncates the G2C frame, which
   the real client surfaces as its generic "packet error". `SlaveManager.Create` sets
   `Name = template.Name`, so the retail path never trips this.
2. **Missing clothes/parts — InitialItems + doodad bindings skipped**: the retail summon path equips
   `slave_initial_items` (clothes/sails/parts per `SlaveInitialItemPackId`,
   `SlaveManager.cs` "Equip it's default items" block), spawns `Template.DoodadBindings`, and applies bonuses.
   The hand-rolled spawn did NONE of that — no equipment serialization ever reached the client.

### Fix (small, safe, clean cutover)

`TestSlave.Execute` now delegates to `character.ParentWorld.SlaveManager.Create(character, null, 73)` — the exact
path `/slave spawn <templateId>` uses. Template 73 (cotton-field cart, model 1008) carries level 50 / faction 143,
identical to the previously hardcoded values; it has no initial-item pack in data (verified in compact.sqlite3),
but ship templates do (pack 1 = EznaCutter parts incl. item 28852+, pack 4 = harpoon clipper), and those now equip
correctly on any GM-spawned hull via the shared path.

### Verification

E2E `AAEmu.IntegrationTests.E2e.SlaveTestBugHuntE2eTests.TestSlave_GMCommand_Spawn_CapturesErrors` (committed):
sends `/testslave` as a real CSSendChatMessagePacket over an authenticated bot link on a fresh stack; asserts
baseline window has 0 marshal errors, ≥1 SCSlaveCreatedPacket broadcast is received post-command, and 0 marshal
errors post-fix. PASS against rebuilt runtime; dotnet build AAEmu.Game + IntegrationTests green.
Pre-existing, NOT touched here: CSChangeSlaveEquipmentPacket vehicle-customization TODO (SlaveManager ~537) and
the CSChangeSlaveTargetPacket placeholder remain open gaps.
