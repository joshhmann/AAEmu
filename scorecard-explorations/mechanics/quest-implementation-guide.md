# Implementing Sophisticated Quests in AAEmu — Engineering Guide

Grounded in the PB-002 quest-270 interaction slice (2026-08-29). Covers the
data archaeology, engine pipeline, and headless-rig conventions needed to
implement quests whose objectives are more than talk/kill/deliver: item-gated
doodad interactions, phase machines, reagent-gated skills, and chained
prerequisites.

## 1. Data archaeology — read the canonical DB first

`compact.sqlite3` is the read-only source of truth. Every id below was verified
with direct queries; never guess ids, phases, or gates.

### Quest topology
```sql
-- quest → components (kind 2=Start, 3=Supply, 4=Progress, 6=Report, 8=Reward)
SELECT qc.id, qc.name, qc.level, c.id, c.component_kind_id, c.next_component,
       a.id, a.act_detail_type, a.act_detail_id
FROM quest_contexts qc
JOIN quest_components c ON c.quest_context_id = qc.id
LEFT JOIN quest_acts a ON a.quest_component_id = c.id
WHERE qc.id IN (269, 270) ORDER BY qc.id, c.component_kind_id;
```
- `act_detail_type` names the act class (`QuestActObjInteraction`, …); the
  matching `quest_act_*` table keyed by `act_detail_id` carries the parameters.
- Start-component gates live in `unit_reqs` (`owner_type='QuestComponent'`,
  `owner_id = start component id`). Kinds that matter: `1` = Level, `31` =
  PreCompleteQuestContext (prerequisite quest), `42` = MotherFaction. The
  engine evaluates them in `UnitReqs.cs` — read the exact kind semantics there
  (e.g. `MotherFaction` dereferences `unit.Faction.MotherId`; a null Faction
  NREs, which is why headless characters must carry a faction).

### Doodad phase machines
```sql
-- groups per doodad (kind 1 = Start, 2 = Normal, 3 = End)
SELECT id, doodad_almighty_id, doodad_func_group_kind_id FROM doodad_func_groups
WHERE doodad_almighty_id = 687;   -- 161 (Start), 304 (Normal)
-- funcs per group: func_skill_id binds the interaction skill, next_phase advances
SELECT f.id, f.doodad_func_group_id, f.actual_func_id, f.actual_func_type,
       f.func_skill_id, f.next_phase FROM doodad_funcs f
WHERE f.doodad_func_group_id IN (161, 304);
```
- `DoodadManager.Create` sets `FuncGroupId = GetFuncGroupId()` = the kind-1
  group. `Doodad.Use(caster, skillId)` matches `GetFunc(groupId, skillId)`,
  runs the func, and advances phase when the func sets `ToNextPhase`.
- A func like `DoodadFuncRemoveItem` is the item gate: it consumes the item and
  only then sets `ToNextPhase` — no item, no phase move, no credit.

### Skills, reagents, effects
```sql
SELECT casting_time, target_type_id, effect_delay, channeling_time FROM skills WHERE id = 11229;
SELECT * FROM skill_reagents WHERE skill_id = 11229;   -- item 3900 x1
-- effect order matters: InteractionEffect 5124 FIRST, then SpawnEffects 20778-20782
```
- `target_type_id = 8` = Doodad. Cast-time skills schedule a `CastTask` on
  `TaskManager` — headless rigs have no ticker (see §4).
- Reagent validation happens inside `Skill.ApplyEffects`; a missing reagent
  cancels the skill before effects land — proven by the no-items control test
  (objective stays 0, phase stays 161). Do not re-derive this statically.

## 2. The interaction-credit pipeline (the part that must not be faked)

```
CSStartSkillPacket (SkillCastTargetType.Doodad)
  → Skill.Use (requirements, GCD, cast-time → CastTask)
  → Skill.Cast → ApplyEffects
  → InteractionEffect.Apply
      → World.Interactions.Use.Execute → Doodad.Use(caster, skillId)
          → DoFunc → DoodadFuncRemoveItem (consumes item, ToNextPhase → 304)
      → QuestManager.DoDoodadInteractionEvents
          → QuestActObjInteraction.OnInteraction (AddObjective)
```
Two rules from this slice:
1. **Enter through the outer skill pipeline.** Calling `Doodad.Use` directly
   skips `InteractionEffect` and therefore the quest event — the objective
   never credits. `GameplayActor.InteractWith` now derives the use skill from
   the doodad's func table (`ResolveInteractionSkill`) and drives
   `Skill.Use` with a `SkillCastTargetType.Doodad` target, exactly like the
   client packet.
2. **Credit is an engine event, never a hand-written counter.** The scenario
   leg reads `act.GetObjective(quest)` from live quest state and requires it to
   increase after each action; anything else is fake progress.

## 3. Scenario support (LevelingLoopScenario pattern)

- One `InteractionLeg`-style method per objective type, dispatched from
  `PursueObjectives`' switch. Unsupported types stay in `KnownPrimitiveGaps`
  and fail closed with a reason naming the missing primitive.
- Source resolution is data-driven: `HighlightDoodadId ?? DoodadId` matched
  against **perceived** doodads only (Observe → region graph), never a world
  scan.
- Bounded attempts (one per perceived source), and every completed action must
  move the objective counter or the leg fails with `WrongDecision`.

## 4. Headless rig conventions (the seams that took 5 hours to find)

- **Canonical data scope**: `CanonicalInteractionDataScope` (IDisposable)
  captures/restores `SkillManager` + `DoodadManager` singletons, installs fresh
  instances with Moq deps, calls `.Load()` for full canonical data, restores on
  `Dispose`. Order: `PlayerbotPilotRig.SeedPilotSingletons()` (real QuestManager
  + UnitRequirementsGameData) → scope → `GameplayActorTestRig.Seed()` →
  item seeds. Never let a scope leak past the test — the suite shares
  process-wide singletons.
- **Detached factory**: `DoodadManager.CreateDetached(..., attachToWorld: false)`
  exists because the property `ParentWorld` setter walks
  `Transform.InstanceId → WorldManager.GetWorld(id)`, which returns null for
  unregistered fixture worlds → NRE. `HeadlessSession.SpawnDoodadFromTemplate`
  uses it, then pins `_parentWorld`/`_instanceId` backing fields and joins the
  local region graph (guarded by `World.Regions != null`).
- **Synchronous cast completion**: headless actors have no game-loop ticker.
  Mirror `UseItem`'s seam — after `Skill.Use` schedules a `CastTask`, verify
  `Character.SkillTask` identity, clear it, call `skill.Cast(...)` inline, set
  `skill.Cancelled = true` so the queued task exits without replaying.
- **Kill seam**: bare fixture NPCs can't run `Npc.DoDie`; use the documented
  `RigKillSeam` (`QuestManager.DoOnMonsterHuntEvents` directly) — the exact
  entry point `DoDie` calls for a character killer.
- **Null-chain hardening**: `SpawnEffect.Apply` needs
  `caster?.ParentWorld?.SpawnManager?.GetNpcSpawner(...)` — headless worlds have
  no SpawnManager and skill 11229's five trailing SpawnEffects would NRE.
  Production worlds are unaffected (SpawnManager exists).

## 5. Test recipe (positive + fail-closed controls)

1. **Positive**: full autonomous chain through `LevelingLoopScenario.Run` —
   assert link order, pursuit type, phase transition, inventory drain, and the
   audit-trace subsequence (accept → interact → advance → turn-in).
2. **No-items control**: accept directly, strip the gate items, `InteractWith`
   → Rejected, objective 0, phase unchanged.
3. **Wrong-phase control**: force the post-interaction phase first → no funcs
   → Rejected, no credit, items intact.
4. **Unsupported control**: pick a quest whose objective type is still in
   `KnownPrimitiveGaps` — and re-verify it stays unsupported as support lands
   (quest 64's interaction act became reachable, so it had to be replaced).
   Watch for unseeded singletons the chosen quest's gates touch (e.g.
   `SphereGameData` for sphere objectives).

## 6. Evidence classification

Rig tests are deterministic contract evidence against canonical data — never
live or human proof. State the layer explicitly; `H` stays UNKNOWN until a
human completes the scenario on a live stack.
