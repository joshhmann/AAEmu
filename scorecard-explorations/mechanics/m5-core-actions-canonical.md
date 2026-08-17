# M5 Core Actions — Canonical 1.2 Ground Truth (m5-core-actions-canonical.md)

**Task:** t_5189977b (M5.3 REQ-1, dossier first per mechanic-research doctrine — spec t_d837ee0b, Rei spec-gate ACCEPT t_a844e2b1)
**Branch:** docs/m5.3-canonical-dossier (from origin/develop @ 9cc400fd2)
**Date:** 2026-08-17
**Ground truth:** fork `develop` @ 9cc400fd2; canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (r208022); engine code as merged at 9cc400fd2.
**Scope:** (a) character foot movement, (b) targeting, (c) skill cast mechanics — the three mechanic domains REQ-M5.3-3/4 (Move/Stop), REQ-M5.3-5 (Target), REQ-M5.3-6 (Cast) must be reworked against. Observed/audit-record shapes are spec-owned, not mechanic-owned, and are out of scope here.

## 0. Flag legend

Every claim below carries one of:

- **[DV-code]** — *data-verified*: read directly from the engine source at 9cc400fd2 (file:line cited). Code is ground truth for what the fork DOES.
- **[DV-data]** — *data-verified*: read directly from compact.sqlite3 (r208022). Data is ground truth for canonical 1.2 values; per the fork rule, when data and code disagree, the DATA wins.
- **[RD-wiki]** — *research-derived*: contemporary wiki/forum source (URL + access date cited). Flagged because wiki content can disagree with 1.2 data or postdate the 1.2 era; used only as corroboration, never as an invented mechanic.
- **[GAP]** — a load-vs-enforce gap: the field/mechanic exists in 1.2 data or the code surface but is NOT enforced/executed on develop. No invented behavior is assumed for gaps; they are listed for the impl to decide.

---

## 1. (a) Character foot movement — walk/run, CSMoveUnitPacket, broadcasts, stop/halt

### 1.1 The client-authored movement model (overview)

**[DV-code]** Character foot movement in 1.2 is **client-authored**: the client computes the position, velocity and animation state every tick and sends it to the server; the server validates the target, applies the position to the transform, broadcasts it to nearby players, and finalizes the transform tree. There is NO server-side simulation of walking/running speeds. This is the same family of path the M5 DriveVehicle action already rides (`VehicleMovementModel`), and the M5.3 Move action must ride the same model — the spec explicitly names `CSMoveUnitPacket` as the reference path (spec §REQ-M5.3-3).

### 1.2 Wire surface

**[DV-code]** The client-side packet is `CSMoveUnitPacket` (opcode `0x089`, `CSOffsets.cs:133`, registered `GameNetwork.cs:150`). Payload ([DV-code] `CSMoveUnitPacket.cs:16-23`):

1. `objId` (Bc) — the object the client is controlling (the character itself for foot movement, or a Mate/Slave/Transfer when riding/driving).
2. `type` byte → `MoveTypeEnum` (`MoveType.cs:9-17`): `Default=0, Unit=1, Vehicle=2, Ship=3, ShipRequest=4, Transfer=5`.
3. The rest of the payload is parsed by the concrete `MoveType` (`MoveType.Read` + `UnitMoveType.Read`).

**[DV-code]** `MoveType` base fields (`MoveType.cs:19-62`): `Time` (ms since midnight), `Flags` (`MoveTypeFlags` byte), optional `ScType`/`Phase` (when `HasScTypeAndPhase` 0x10 is set), `X/Y/Z`, `VelX/VelY/VelZ` (shorts), `RotationX/Y/Z` (sbytes).

**[DV-code]** `UnitMoveType` adds (`UnitMoveType.cs:6-56`): `DeltaMovement[3]` (sbytes), `Stance` (sbyte; `GameStanceType`), `Alertness` (byte; `MoveTypeAlertness`), `ActorFlags` (byte; drives optional `FallVel`, `GcFlags/GcPartId/X2/Y2/Z2/RotationX2..Z2`, `GcId`, `ClimbData`).

**MoveTypeFlags** ([DV-code] `MoveTypeFlags.cs`): `None=0x00, _flag1=0x01, Moving=0x02, Stopping=0x04, InCombat=0x08, HasScTypeAndPhase=0x10, _flag6=0x20, StandingOnObject=0x40, _flag8=0x80`; `Jumping = Moving|Stopping (0x06)`. Comments in the packet (`CSMoveUnitPacket.cs:27-31`) document: `0x02 Moving`, `0x04 Stopping (released movement keys)`, `0x06 Jumping`, `0x40 Standing on something`.

**ActorFlags** ([DV-code] `MoveTypeActorFlags` in `MoveTypeFlags.cs:28-39`): `StandingOnSolid=0x04, Jumping=0x10, StandingOnObject=0x20, HangingFromObject=0x40, _flag8=0x80`.

### 1.3 The server handler path (what CSMoveUnitPacket executes)

**[DV-code]** `CSMoveUnitPacket.Execute` (`CSMoveUnitPacket.cs:48-113`):

1. Resolve `character = Connection.ActiveChar`; bail if null or `DisabledSetPosition` (teleport/instance transitions block movement).
2. Resolve `targetUnit = character.ParentWorld.GetBaseUnit(objId)`; bail with a warn if the object is gone (`Invalid target`).
3. Dispatch on the payload's concrete type:
   - `ShipRequestMoveType` → ships: writes `ThrottleRequest`/`SteeringRequest` onto the Slave; the physics engine drives subsequent movement (`CSMoveUnitPacket.cs:70-87`).
   - `VehicleMoveType` (ground vehicles) → `VehicleMovementModel.ApplySlaveMove(character, car, vmt)` (`CSMoveUnitPacket.cs:88-99`).
   - `UnitMoveType` (characters and mates) → `VehicleMovementModel.ApplyUnitMove(character, targetUnit, dmt)` (`CSMoveUnitPacket.cs:101-108`).
   - Unknown types → warn.

**[DV-code]** Foot movement for a character arrives as a `UnitMoveType` with the character's own `objId`, and executes `VehicleMovementModel.ApplyUnitMove` (`VehicleMovementModel.cs:67-177`). The method body is the extraction of the former inline handler (documented as behavior-preserving, `VehicleMovementModel.cs:16-31`). Order of operations:

1. **Mate/pet handling** (when the target is a Mate): removes move-cancelling buffs, starts/stops pet XP based on velocity (`VehicleMovementModel.cs:70-90`).
2. **Character handling** (when the target is a Character): remove move-cancelling buffs; if riding a pet, force-reparents if detached and returns (ride movement is the pet's move, not the character's); otherwise `player.SetPlayerMoved()` — the "first movement after load" hook that sends the MOTD and flips `FinishedLoading` (`Character.cs:2971-2981`, [DV-code]).
3. **Sticky-parent tracking**: reads `StandingOnObject` flag + `GcId` (`GcId 1` = "current parent" sentinel, ignored), and `HangingFromObject` actor flag; attaches/detaches/rebinds the transform to the object the unit stands on (`VehicleMovementModel.cs:122-162`).
4. **Position apply**: `targetUnit.Transform.Local.SetPosition(x, y, z, rotX, rotY, rotZ)` (`VehicleMovementModel.cs:165-168`).
5. **Movement broadcast**: `targetUnit.BroadcastPacket(new SCOneUnitMovementPacket(targetUnit.ObjId, dmt), true)` (`VehicleMovementModel.cs:169`).
6. **Transform finalize**: `targetUnit.Transform.FinalizeTransform()` — propagates the position to all children (passengers, packs) (`VehicleMovementModel.cs:170`).
7. **Fall damage**: if `FallVel > 0`, schedule `DoFallDamage(FallVel)` (`VehicleMovementModel.cs:172-176`).

### 1.4 Movement broadcast (what observers see)

**[DV-code]** `SCOneUnitMovementPacket` (opcode `0x6C`, `SCOffsets.cs:111`): `[bc objId][byte type][MoveType payload]` (`SCOneUnitMovementPacket.cs:12-17`). Sent via `GameObject.BroadcastPacket(packet, self:true)` → `WorldManager.GetAround(this, characters)` → every nearby Character gets the packet; when the moved object IS a Character, it also gets its own broadcast (`GameObject.cs:146-171`). There is also a batched `SCUnitMovementsPacket` (0x6B) for NPC/AI bulk updates (`SCUnitMovementsPacket.cs`).

**[DV-code]** The fork's own bot roaming broadcasts exactly this shape: `BotRoamStepExecutor.BuildMoveType` (`BotRoamStepExecutor.cs:262-283`) builds a `UnitMoveType` with velocity from facing (`AddDistanceToFront(4000, ...)`), `ActorFlags = 5` (walk), `DeltaMovement = [0, 63, 0]`, `Stance = Relaxed`, `Alertness = Idle`, and the executor broadcasts `SCOneUnitMovementPacket` per leg ([DV-code] `BotRoamStepExecutor.cs` leg-execution path; the E2E wire dump on 0x6C is documented in the presence-demo evidence). The M5.3 Move rework should mirror this builder (plus walk/run selection, §1.5).

### 1.5 Walk vs run — what canonical 1.2 defines

**[DV-data]** The 1.2 data surface does not carry absolute character speeds in a dedicated table; movement multipliers are attributes on `actor_models` (client-facing model metadata the server loads): for player models 10/11/16/17 (Ferre/Hariharan, Nuian races — `model_file` tells the race/sex), `game_forward_multiplier = 1.0`, `game_walk_multiplier = 0.45`, `game_sprint_multiplier = 1.5`, `game_backward_multiplier = 0.65` (Nuian/Hariharan models 16/17) or `0.7` (Ferre 10/11), `game_strafe_multiplier = 1.0`, `game_jump_height = 1.3`. (Verified on this branch against compact.sqlite3.)

**[DV-code]** On the wire, walk vs run is a *client animation state*, not a server-computed speed: the velocity doubles (`VelX/VelY/VelZ`, in the movement payload) and the `ActorFlags` byte encode the gait (`ActorFlags 5 = walk` in this fork's builders, [DV-code] `BotRoamStepExecutor.cs:276`, `VehicleMovementModel.BuildUnitMove:234`). The server never derives a walk/run speed from the data; it applies the client's position verbatim. **Consequence for REQ-M5.3-3: the Move action must emit `UnitMoveType` payloads (position + facing velocity + walk/run flags) and broadcast them — it must NOT compute its own animation gait.**

**[RD-wiki]** Contemporary description of the control scheme: "ArcheAge uses the standard tab-target control set up found in most modern MMOs" — ArcheAge Wiki, Game Mechanics, https://archeage.fandom.com/wiki/Game_Mechanics (accessed 2026-08-17). Walk toggle / run default is client-side binding; no server acceptance gate exists on gait choice ([DV-code] the packet handler applies whatever payload arrives).

### 1.6 Stop / halt semantics

**[DV-code]** There is NO dedicated "stop" packet for foot movement. Halting is:

1. **Client-side release of movement keys** → client sends a `CSMoveUnitPacket` with `Flags = Stopping (0x04)` (per the packet's own comment `0x04 : Stopping (released movement keys)`, `CSMoveUnitPacket.cs:27-31`) and zero/decaying velocity. The server executes the identical `ApplyUnitMove` path: applies the released position, broadcasts the `SCOneUnitMovementPacket` (with the Stopping flag so observers' clients snap the unit to a standstill), finalizes the transform.
2. **No further packets** when idle — the last broadcast carries the final position.

**[DV-code]** The engine generates the same stopping broadcast for teleport-like skills: `Blink.cs` and `TeleportToUnit.cs` build a `UnitMoveType` with `VelX=VelY=VelZ=0`, `Flags = Stopping | (IsInBattle ? InCombat : 0)`, empty `DeltaMovement`, and broadcast `SCOneUnitMovementPacket` (`Blink.cs:70-80`, `TeleportToUnit.cs:74-83`). This is the canonical "locomotion reset" broadcast shape — the M5.3 Stop rework should emit the same shape (position + Stopping flag + zero velocity) so observers actually see the halt.

**[DV-code]** The M5.3 Stop action is NOT the same as the wire halt: per spec REQ-M5.3-4 it interrupts the actor's running request (Interrupted, "stop requested") and is a no-op when idle ([DV-code] v1 `GameplayActor.Stop`, `GameplayActor.cs:168-180`). The mechanical ground truth the rework must honor: when a bot stops walking, it must ALSO emit the Stopping broadcast (§1.6.2) if any observer could have seen the walk — otherwise the client side keeps the bot walking forever (the M6 "frozen bot" class of bug, see skill file: presence bots DB-position fingerprint).

### 1.7 Movement-side buff/effect interactions (Move rework must not break these)

**[DV-code]** `VehicleMovementModel.RemoveEffects` (`VehicleMovementModel.cs:243-247`): any payload with non-zero velocity triggers `unit.Buffs.TriggerRemoveOn(BuffRemoveOn.Move)` — buffs flagged `RemoveOnMove` exit when the unit starts moving. Also triggered from the character move hook (`Character.cs:1782`). The Move action's broadcasts MUST set the same velocity-detectable payloads, exactly like a real client, or movement-triggered buff removal (e.g. meditation, still-walking stealth effects) will not fire.

### 1.8 Status on develop vs REQ-M5.3-3 (what the dossier OWNS)

**[DV-code]** **Move is KNOWN NON-CONFORMING on develop — confirmed.** The v1 Move action (`GameplayActor.cs:2185-2214`) advances the character through a **silent local Transform write** — `ApplyPosition` (`GameplayActor.cs:2253-2259`) does `transform.Local.SetRotationDegree(...)` + `transform.Local.SetPosition(next)` with NO packet broadcast, NO `UnitMoveType` payload, NO velocity semantics, NO buff-removal trigger. The DriveVehicle reference path (`GameplayActor.cs:2244-2249` → `VehicleMovementModel.ApplySlaveMove`) is the conforming pattern to copy for foot movement. Claims the rework must satisfy (spec REQ-M5.3-3): advance through the real client-authored movement path (`CSMoveUnitPacket`-equivalent = building `UnitMoveType` + broadcasting `SCOneUnitMovementPacket`), real movement broadcasts observed, arrival `ArrivalRadius 0.5f` → Completed, budget expiry → `TimedOut(Navigation)`, non-positive speed / non-finite destination → `Rejected(RejectedAction)`, busy → `Rejected(StateTransition)`.

---

## 2. (b) Targeting — the real engine target-set path for `Unit.CurrentTarget`

### 2.1 Wire surface

**[DV-code]** Client sends `CSChangeTargetPacket` (opcode `0x02C`, `CSOffsets.cs:45`, registered in `GameNetwork.cs`). Payload: a single `targetId` (Bc, `CSChangeTargetPacket.cs:14`). `0` = clear target.

### 2.2 The real target-set path (the exact assignment M5.3 Target must perform)

**[DV-code]** `CSChangeTargetPacket.Read` (`CSChangeTargetPacket.cs:12-23`) does the canonical assignment:

```csharp
Connection.ActiveChar.CurrentTarget = targetId > 0
    ? Connection.ActiveChar.ParentWorld.GetUnit(targetId)
    : null;
Connection.ActiveChar.BroadcastPacket(
    new SCTargetChangedPacket(Connection.ActiveChar.ObjId,
        Connection.ActiveChar.CurrentTarget?.ObjId ?? 0), true);
```

So the real engine target-set path is: **resolve via `WorldInstance.GetUnit(objId)` (the world's unit registry — NOT the base-object registry), assign the result to the `Unit.CurrentTarget` property (a plain auto-property, [DV-code] `Unit.cs:253` — no setter side-effects), then broadcast `SCTargetChangedPacket` (opcode `0x84`, `SCOffsets.cs:133`) to nearby players including self.**

**[DV-code]** `WorldInstance.GetUnit` ([DV-code] `WorldInstance.cs:456-459`) returns from `_units`; `GetBaseUnit` (414-417) returns from `_baseUnits`. The targeting path deliberately uses the unit registry — Doodads/Houses are NOT targetable via this packet (they live in other registries; `CurrentTarget` is typed `BaseUnit`, but the packet's resolution restricts actual assignable values to Units).

**[DV-code]** Clearing: `targetId == 0` → `CurrentTarget = null` + broadcast with targetId 0 (`CSChangeTargetPacket.cs:19-29`). Death cleanup: `killerUnit.CurrentTarget = null` on kill (`Unit.cs:561`).

### 2.3 Other writers of `CurrentTarget` (inventory the assignment surface)

**[DV-code]** The only production writer on the client-facing path is `CSChangeTargetPacket` (§2.2). The bot/actor layer writes it directly in `GameplayActor.SetTarget` (`GameplayActor.cs:182-194`): resolve via `Character.ParentWorld.GetUnit(objId)` (`ResolveUnit`, `GameplayActor.cs:2411-2418`), then `Character.CurrentTarget = unit`. This matches the packet assignment (same resolver, same property) but is missing the broadcast and the `null`-clear shape. **Finding: the actor's SetTarget currently performs the assignment WITHOUT the `SCTargetChangedPacket` broadcast the real path emits — any client observing the bot will not see its target change. This is the concrete delta REQ-M5.3-5 must close (spec: "SetTarget through the real engine targeting path (`Unit.CurrentTarget` — the exact assignment the engine's targeting performs") + observe-side reflection).**

Other writers found ([DV-code]): none in the packet/network layer; `Unit.cs:561` clears on death; bot `SetTarget` as above; GM/test commands write the property for debug.

### 2.4 Canonical semantics vs M5.3 Target

**[RD-wiki]** Tab-targeting: "ArcheAge uses the standard tab-target control set up found in most modern MMOs" — ArcheAge Wiki, Game Mechanics (accessed 2026-08-17). The 1.2-era client sends `CSChangeTargetPacket` on tab/click target selection; the server's CurrentTarget is the assignment + broadcast above ([DV-code]).

**[DV-code]** Acceptance mapping (spec REQ-M5.3-5 + exit test E5): unknown objId → `Rejected(RejectedAction)` (v1 does this via `ResolveUnit` null check, `GameplayActor.cs:186-190`); CurrentTarget set to the resolved unit — must go through `WorldInstance.GetUnit` (the packet resolver), not a component/reference registry; a subsequent Observe must reflect the target (the Observation surface already includes current-target context via the actor's state, `GameplayActor.cs` Observation region).

---

## 3. (c) Skill cast mechanics — casting_time, CastTask, start/end broadcasts, move-interrupt, mana/cooldown

### 3.1 Wire entry: CSStartSkillPacket

**[DV-code]** Client sends `CSStartSkillPacket` (opcode `0x052`, `CSOffsets.cs:81`, registered `GameNetwork.cs:98`). Payload (`CSStartSkillPacket.cs:43-56`): `skillId` (u32), `skillCasterType` (byte) + `SkillCaster` payload, `skillCastTargetType` (byte) + `SkillCastTarget` payload, `flag` byte (low nibble selects `SkillObject` type, parsed when > 0).

Dispatch branches in the packet handler ([DV-code] `CSStartSkillPacket.cs:70-168`):

| Branch | Condition | Action |
|---|---|---|
| Mount/slave skill | `SkillCasterMount` | skill.Use on the mate/slave + optional rider skill via `Character.UseSkill(mountAttachedSkill, riderTarget)` |
| Auto-attack re-send | `IsAutoAttack && skillId == AutoAttackTask.Skill.Template.Id` | returns success without re-executing |
| Default/common skill | `SkillManager.IsDefaultSkill \|\| IsCommonSkill` (non-item caster) | `skill.Use(...)`, starts auto-attack loop for basic combat skills (< 5000) |
| Item skill | `SkillCaster` is `SkillItem` | validates `UseSkillId` + bind rules, `skill.Use(...)` |
| **Learned character skill** | `Character.Skills.Skills.ContainsKey(skillId)` | `skill = new Skill(template, character); skill.Use(...)` — **this is the branch M5.3 Cast must mirror** |
| Variant of learned | `Skills.IsVariantOfSkill(skillId)` | `skill.Use(...)` |
| Unknown | fallthrough | logs warn, still attempts `skill.Use(...)` |

**[DV-code]** `Character.UseSkill(uint skillId, IUnit target)` — the exact call the spec names — is defined at `Unit.cs:1030-1041` (Character inherits; no Character override found): builds `Skill` from the template, `SkillCaster` (Unit type, objId = caster), `SkillCastTarget` (Unit type, objId = target), and calls `skill.Use(this, caster, sct, null, bypassGcd: true, out _)`. NOTE: the `Unit.UseSkill` wrapper passes `bypassGcd: true` — the packet's learned-skill branch calls `skill.Use` with `bypassGcd: false` (`CSStartSkillPacket.cs:153`). This is a real behavioral delta between the actor path (M5.3 Cast v1) and the exact packet path; the dossier records it as a finding (REQ-M5.3-6 says "the exact call CSStartSkillPacket's learned-skill branch makes" — v1 uses `Unit.UseSkill` which is byte-equivalent in effect application but GCD-bypassing; the rework must decide whether to keep bypassGcd or match the packet's false).

### 3.2 Skill.Use — the validation/cast-decision pipeline

**[DV-code]** `Skill.Use` (`Skill.cs:89-350`) is the full gate sequence; order matters, every refusal is an engine `SkillResult`:

1. **Source check** — caster must be a `Unit` else `InvalidSource` (`Skill.cs:92-96`).
2. **Requirements** — `UnitRequirementsGameData.Instance.CanUseSkill(Template, caster, casterCaster)`; failure → mapped error result + result value (`Skill.cs:104-114`).
3. **Personal cooldown** — `Template.CooldownTime > 0` && `unit.Cooldowns.CheckCooldown(Template.Id)` → `CooldownTime` (`Skill.cs:116-120`).
4. **GCD gate** — non-bypass path locks `unit.GcdLock`, enforces a 150 ms skill-use interval (500 ms for basic attacks 2/3/4), and `GlobalCooldown` expiry (`Skill.cs:122-158`).
5. **Buff cancellation on skill start** — `Template.CancelOngoingBuffs` (buff tags) (`Skill.cs:160-166`).
6. **Target resolution** — `GetInitialTarget` per `Template.TargetType` (Self/Friendly/Hostile/AnyUnit/Doodad/Item/Others/Building…), relation checks for friendly/hostile types (`Skill.cs:172-182`, 352-500).
7. **Unmount** — riding + `Template.Unmount` → dismount (`Skill.cs:184-194`).
8. **Mana pre-check** — `ManaCost(unit) > unit.Mp` → `LackMana` (BEFORE TlId allocation, so a lack-of-mana refusal never creates a TlId) (`Skill.cs:196-198`).
9. **TlId allocation** — `SkillTlIdManager.GetNextId(caster)` (`Skill.cs:200-203`).
10. **Plot kickoff** — if `Template.Plot != null` → `Task.Run(Plot.RunAsync(...))`; `PlotOnly` returns Success immediately (`Skill.cs:205-211`).
11. **Range check** — `MinRange`/`MaxRange` (skill or weapon-slot ranges) → `TooCloseRange`/`TooFarRange`; range failures release the TlId (`Skill.cs:213-261`).
12. **Casting time computation** — `Template.CastingTime > 0` → `castTime = unit.CastTimeMul * SkillModifiersCache(CastTime, Template.CastingTime)`, then `castTime *= CastTimeMultiplier` (`Skill.cs:309-313`). Zero/negative → immediate cast.

**Cast decision** ([DV-code] `Skill.cs:331-349`):

- `castTime > 0`: broadcast `SCSkillStartedPacket(Id, TlId, casterCaster, targetCaster, skill, skillObject)` with `BaseCastTimeDiv10 = castTime/10` and `RealCastTimeDiv10 = castTime/10` (`Skill.cs:334-338`); then `unit.SkillTask = new CastTask(...)` scheduled at `TimeSpan.FromMilliseconds(castTime)` via `TaskManager` (`Skill.cs:340-341`) — **this is the CastTask scheduling path REQ-M5.3-6 names**.
- `castTime == 0`: `Cast(...)` synchronously (`Skill.cs:343-347`).

### 3.3 CastTask — what fires at the end of the cast

**[DV-code]** `CastTask.Execute` (`CastTask.cs:15-21`): if `Skill.Cancelled` → return (a cancelled cast never fires); else `Skill.Cast(caster, casterCaster, target, targetCaster, skillObject)`.

### 3.4 Skill.Cast — the "spell goes off" moment (mana + cooldown CONSUMPTION)

**[DV-code]** `Skill.Cast` (`Skill.cs:615-752`) is where the spell actually fires; observable sequence:

1. **GCD set** — `Template.CustomGcd` or default 1000 ms (character) / 1500 ms (NPC), scaled by `GlobalCooldownMul` (`Skill.cs:619-626`).
2. **NPC skill-controller** handling for NPC casters (`Skill.cs:628-662`).
3. **`unit.SkillTask = null`** — the cast task is consumed (`Skill.cs:663`).
4. **Mana consumption** — `ConsumeMana(caster)` (`Skill.cs:665`; formula §3.7) via `ReduceCurrentMp` → broadcasts `SCUnitPointsPacket(ObjId, Hp, Mp)` to nearby + self ([DV-code] `Unit.cs:461-477`).
5. **Cooldown start** — `unit.Cooldowns.AddCooldown(Template.Id, Template.CooldownTime)` (`Skill.cs:666`; store §3.8).
6. **Reagent/item validation** for SkillItem casts (`Skill.cs:716-742`).
7. **Channeling or effect scheduling** — `Template.ChannelingTime > 0` → `StartChanneling` (channel buffs, channeling doodad, `SCSkillFiredPacket` broadcast, `EndChannelingTask` scheduled) (`Skill.cs:744-746`, 774-801); else `ScheduleEffects` (`Skill.cs:748-752`, 830-868): computes effect delay (EffectDelay + travel time via EffectSpeed + fire-anim CombatSyncTime), broadcasts `SCSkillFiredPacket` (0xA2) with `ComputedDelay`, then either applies effects immediately + `EndSkill`, or schedules `ApplySkillTask` (which calls `ApplyEffects` + `EndSkill` at the delay) (`ApplySkillTask.cs:15-19`).

### 3.5 Start/End broadcasts — the exact packets

**[DV-code]** `SCSkillStartedPacket` (opcode `0xA1`, `SCOffsets.cs:161`): fields `id, tl, caster, target, skillObject, RealCastTimeDiv10, BaseCastTimeDiv10, CastSynergy, ExtraDataFlag + optional byte/ushort/uint`; `SetSkillResult`/`SetResultUInt` mark refusal results (the packet the handler builds on a failed Use, `CSStartSkillPacket.cs:170-181` — a failed cast still gets a Started packet with error data and NO fired/ended packet) (`SCSkillStartedPacket.cs:16-87`).

**[DV-code]** `SCSkillEndedPacket` (opcode `0xA3`, `SCOffsets.cs:163`): payload is just `tlId` (`SCSkillEndedPacket.cs:6-20`). Sent by `EndSkill` on normal completion (`Skill.cs:1430`), by `Stop` on interruption (`Skill.cs:1459`), and by `StopSkill` (`Skill.cs:766`). `SCSkillStoppedPacket` (0xA4) carries `(objId, skillId)` and is the "stopped" twin for auto-attack paths (`Skill.cs:767`).

### 3.6 Move-interrupt rules — the canonical truth

**[DV-code]** **In the engine, MOVEMENT ALONE DOES NOT INTERRUPT A CAST.** The cast is only interrupted by:

1. **Explicit stop** — `CSStopCastingPacket` (opcode `0x054`, `CSOffsets.cs:82`, registered `GameNetwork.cs:99`): reads `tlId, plotTlId, objId`; validates it matches the active `SkillTask.TlId`; cancels the task and calls `Skill.Stop` (`CSStopCastingPacket.cs:12-67`). `Skill.Stop` (`Skill.cs:1446-1465`): ends channeling if channeling, removes toggle buff, broadcasts `SCCastingStoppedPacket (0xA5)` + `SCSkillEndedPacket`, sets `Cancelled = true`, clears `unit.SkillTask`, releases the TlId. A cancelled cast consumes NO mana and does NOT start the cooldown (both happen inside `Cast`, which never runs — §3.4).
2. **Plot cancellation** — plot-based skills accept `RequestCancellation` via the stop packet or plot logic (`CSStopCastingPacket.cs:23-36`).
3. **Cast/Cancelable flags** — the 1.2 data marks interruptibility per skill: `casting_cancelable`, `casting_delayable`, `channeling_cancelable`, `stop_casting_on_big_hit`, `stop_channeling_on_big_hit`, `stop_casting_by_turn`, `stop_channeling_on_start_skill` (all loaded at `SkillManager.cs:373-401`; **loaded but NOT enforced anywhere in the cast pipeline on develop — [GAP], grep of engine usage (excluding loader/template files) returns zero hits).**

**[DV-code]** Movement-related interruption in 1.2 = taking damage while casting may stagger you (big-hit flag) and certain buffs are removed on movement (`BuffRemoveOn.Move`), but the cast task itself is not cancelled by the movement packet. The client is responsible for deciding the cast is broken and sending `CSStopCastingPacket` (or the server's plot logic cancels). **The fork's Cast action must not invent a "move cancels cast" rule — the engine-true behavior is: Cast runs to completion unless stopped via the stop path or the skill's own plot logic.**

**[RD-wiki]** Corroboration: "You can cast while moving which aids in evasion" — ArcheAge Wiki, Combat, https://archeage.fandom.com/wiki/Combat (accessed 2026-08-17). Cast-time spells can be interrupted by damage; "cast delay is for spells with cast time. Channel spells completely ignore that feature" — r/archeage, "Regarding cast delay / interruption and channeled spells", 2014-11 (reddit.com/r/archeage/comments/2l5i6g). These are research-derived corroborations of the data/code behavior, flagged RD-wiki.

### 3.7 Mana cost — canonical formula

**[DV-code]** `Skill.ManaCost` (`Skill.cs:1572-1578`): `baseCost = ((GetAbLevel(AbilityId) - 1) * 1.6 + 8) * 3 / 3.65`; `cost = baseCost * Template.ManaLevelMd + Template.ManaCost`; final = `SkillModifiersCache(CastTime→ManaCost modifiers)`. Consumed in `ConsumeMana` (`Skill.cs:1580-1590`) → `ReduceCurrentMp` (`Unit.cs:461-477`) → `SCUnitPointsPacket` broadcast. **[DV-data]** Backing columns in `skills` (verified on this branch): `mana_cost`, `mana_level_md` (e.g. basic attacks: skill 2 mana_cost 300, skill 4 mana_cost 500; healing potion 10001 cooldown 30000 ms, feed 20595 casting_time 4000 ms). The pre-check at Use (`Skill.cs:196-198`) refuses BEFORE any allocation; true consumption happens at Cast.

### 3.8 Cooldowns — the storage and gate

**[DV-code]** `UnitCooldowns` (`UnitCooldowns.cs`): `ConcurrentDictionary<uint, DateTime> Cooldowns`; `AddCooldown(skillId, durationMs)` at cast fire (§3.4); `CheckCooldown` with 250 ms remainder tolerance (removes expired entries) at Use (§3.2 step 3); `RemoveCooldown` for GM/debug. `IgnoreSkillCooldowns` (GM flag) bypasses check and resets after EndSkill (`Skill.cs:1434-1438`). Documents: per-skill `cooldown_time` (ms) from compact.sqlite3.

---

## 4. Impl-gate notes (for the M5.3 impl cards, REQ-M5.3-3/4/5/6)

1. **Move rework = build UnitMoveType + broadcast.** Copy the DriveVehicle pattern (`GameplayActor.cs:2244-2249` → `VehicleMovementModel`); replace `ApplyPosition` (`GameplayActor.cs:2253-2259`). Emit `SCOneUnitMovementPacket` per leg with velocity-from-facing, stance/alertness/flags like `BotRoamStepExecutor.BuildMoveType`, and a Stopping broadcast when the leg ends or Stop runs. Preserve buff-removal semantics (velocity ≠ 0 triggers `BuffRemoveOn.Move`).
2. **Stop rework = interrupt + Stopping broadcast.** Interrupt the running request (existing v1 logic) AND emit the Stopping-shaped `SCOneUnitMovementPacket` at the halt position (Blink/TeleportToUnit shape, §1.6) so observers stop the bot.
3. **Target rework = add the SCTargetChangedPacket broadcast.** Keep `WorldInstance.GetUnit` resolution (already correct), add `Character.BroadcastPacket(new SCTargetChangedPacket(ObjId, CurrentTarget?.ObjId ?? 0), true)` and the null-clear shape for unknown/0 targets (§2.2).
4. **Cast rework = keep the real pipeline; decide bypassGcd.** v1 already goes through `Unit.UseSkill` → real engine pipeline (mana, cooldown, CastTask, broadcasts all engine-side). Document/decide the `bypassGcd: true` vs packet's `false` delta (§3.1 finding). Cast-time skills schedule `CastTask` — the actor must NOT shortcut it (see skill-file note: the B1 item-use path drives the cast synchronously to dodge the unreliable scheduler; that is a USE-item workaround, and the same double-cast risk noted there applies to Cast — the correct engine-true pattern is CastTask scheduling with `Skill.Cancelled` backstop, not synchronous forcing).
5. **Move-interrupt: do not invent rules.** Cast is NOT cancelled by movement in this engine (§3.6). Contract tests must assert engine-true behavior (cast completes unless stopped), not a wiki-derived "move breaks cast".

## 5. Sources table

| Claim area | Source | Type | Access/verification |
|---|---|---|---|
| Movement packet/wire/apply/broadcast | fork engine at 9cc400fd2 (`CSMoveUnitPacket.cs`, `VehicleMovementModel.cs`, `MoveType*.cs`, `GameObject.cs:146-171`, `BotRoamStepExecutor.cs:262-283`) | DV-code | read on branch |
| Walk/run multipliers, jump height | compact.sqlite3 `actor_models` (rows 10/11/16/17) | DV-data | queried 2026-08-17 |
| Targeting resolution/assignment/broadcast | fork engine (`CSChangeTargetPacket.cs`, `WorldInstance.cs:414-459`, `Unit.cs:253`, `SCTargetChangedPacket.cs`) | DV-code | read on branch |
| Cast pipeline (Use/Cast/CastTask/EndSkill), mana, cooldown | fork engine (`Skill.cs`, `CastTask.cs`, `ApplySkillTask.cs`, `UnitCooldowns.cs`, `Unit.cs:461-477`, `CSStartSkillPacket.cs`, `CSStopCastingPacket.cs`) | DV-code | read on branch |
| Skill casting_time/cooldown_time/mana columns + interrupt flags | compact.sqlite3 `skills` (e.g. 20595 casting_time 4000, 10001 cooldown 30000; 6,877 skills with casting_time > 0; 2 skills stop_casting_by_turn=t) | DV-data | queried 2026-08-17 |
| Tab-target control scheme | ArcheAge Wiki, Game Mechanics (fandom) | RD-wiki | accessed 2026-08-17 |
| Cast-while-moving | ArcheAge Wiki, Combat (fandom) | RD-wiki | accessed 2026-08-17 |
| Cast delay vs channeling / damage interruption | r/archeage, 2l5i6g (2014-11) | RD-wiki | accessed 2026-08-17 |
| Cast speed default 100% | ArcheAge Wiki, Cast Speed (fandom) | RD-wiki | accessed 2026-08-17 |

## 6. Open items tracked for the impl phase

- [GAP] `stop_casting_on_big_hit` / `stop_channeling_on_big_hit` / `casting_cancelable` / `casting_delayable` / `stop_casting_by_turn` loaded but unenforced on develop (§3.6) — decide whether M5.3 Cast must implement any of them (spec says Cast = one skill per request, no combat logic; likely out of scope, but the gate should record the decision).
- [FINDING] `Unit.UseSkill` passes `bypassGcd: true` vs the packet learned-branch's `false` (§3.1) — decision needed in the Cast impl card.
- [FINDING] Actor `SetTarget` missing the `SCTargetChangedPacket` broadcast (§2.3) — REQ-M5.3-5 delta.
- [FINDING] v1 Move silent-write confirmed (§1.8) — the REQ-M5.3-3 rework replaces `ApplyPosition`.