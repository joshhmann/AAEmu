# G6 Discovery Plan — First Combat Action (READ-ONLY audit, develop @ 2026-09-20)

READ-ONLY. Nothing modified, built, or run. G3/G4 frozen — cited, never edited.
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
G6 mission: from 251-ACTIVE + G4-selected live 3475 + in-range + settled, initiate+execute
combat; stop before damage-detail decisions (boundary defined from code in §H).
`UNKNOWN` = not found in code, nothing inferred. `[INFERENCE]` = derived, marked as such.

Read-first context (restated freeze rules, not altered):
- G3 frozen at PASS-BEHAVIOR: 3512-first → 251L2 → AcceptQuest/Completed → active; regressions
  REPAIR ONLY (`g3-closeout-freeze.md` §d).
- G4 frozen at PASS-BEHAVIOR: 251-ACTIVE → objective resolved → source resolved →
  relevant+legal 3475 selected via `SetTarget`; widening (travel, pursuit, combat, credit,
  turn-in) is a new gate (`g4-closeout-freeze.md` §e).
- G5 stop-range for skill-2 autoattack: flat ≤ 3.0 m fixture-scoped (`QuestBehavior.cs:65`,
  quoting roam `HuntMeleeRange`); no canonical range exists, engine owns refusal
  (`g5-pursuit-semantics.md` §D).
- G4 §E ownership: `BaseUnit.CanAttack` is the combat-legality half — reuse by calling,
  never duplicate. G5 §G ownership: brain owns selection→dispatch + per-wake revalidation +
  stop-on-range; actor owns single-leg stepping + stuck + terminals; queue owns API arbitration.

---

## A. COMBAT MAP — per-verb preconditions, authorities, terminals

All actor verbs assert the A1 execution-thread marshal (`ExecutionBoundary.AssertOnExecutionThread`,
e.g. `GameplayActor.cs:616,644,783`) and serialize through `TryBegin` single-active
(`GameplayActor.cs:619,647,786`). Terminals for live-actor verbs are `ActorRequest`
(State/Result/Failure/Detail, caller-held, synchronous `S` contract); only queue-dispatched
verbs (`Cast`, `Target`, `Loot`, …) additionally get queue lifecycle + durable audit (`Q`/`Y`).
`AutoAttack`/`StopAutoAttack` have **no queue kind and no WebAPI/MCP route** — actor-internal /
bridge-scripted only (`capability-surface-matrix.md` §A2; `BotActionCommandQueue.cs:530-620`
`ExecuteKind` has `Target` `:569-570` and `Cast` `:572-573` arms but no AutoAttack arm;
`ToActionType` `:893-935` has no AutoAttack mapping; `ActorActionType.AutoAttack = 53`,
`StopAutoAttack = 54` exist at `IGameplayActor.cs:1088-1092` but are unreachable externally).
There is **no `UseSkill` actor verb at all** (`UseSkill` in `IGameplayActor.cs` is only the doc
comment naming `Character.UseSkill`; queue grep confirms no `UseSkill` kind).

### A.1 `SetTarget` (already frozen by G4; the combat preamble)

- Preconditions: `TryBegin` + `ResolveUnit(targetObjId)` non-null, else
  `RejectedAction, "target not found in world"` (`GameplayActor.cs:612-624`). NO alive gate,
  NO hostility gate, NO range gate — a dead/friendly/far unit still assigns.
- Effect: `Character.CurrentTarget = unit` + `SCTargetChangedPacket` broadcast
  (`GameplayActor.cs:626-635`); never clears (0/unknown rejected, `:630-632`).
- Terminal: synchronous `Completed("targeting {objId}")` (`:637`).
- Combat relevance: every combat verb below either sets or requires `CurrentTarget`;
  GOAP threat projection reads `ch.CurrentTarget` (`BotWorldStateProvider.cs:201-222`).

### A.2 `AutoAttack(targetObjId)` — the canonical G6 entry

- Preconditions (`GameplayActor.cs:779-827`): `TryBegin`; `ResolveUnit` + **`Hp > 0` else
  `RejectedAction, "auto attack target not found or dead"` (`:789-791`) — the ONLY actor verb
  with a dispatch-time alive gate. Template-exists for the derived skill id else
  `"unknown auto attack skill {id}"` (`:825-827`). NO learned check, NO mana check, NO
  cooldown check, NO range pre-flight, NO hostility check at dispatch.
- Skill choice (`:800-808`): flat `MathUtil.CalculateDistance` (`:801`) + `InferRole` (`:802`)
  + ranged-weapon presence (`:803-804`) → skill **4 iff `role == RangedPhysical ||
  (dist > 4.0f && hasRangedWeapon)`, else skill 2** (`:806-808`; ids
  `BasicMeleeAutoAttackSkillId = 2`, `BasicRangedAutoAttackSkillId = 4`,
  `CombatDecisionTree.cs:111-112`).
- Execution (`:829-847`): sets+broadcasts `CurrentTarget` when different (`:793-798`);
  idempotent fast-path when already auto-attacking same skill on same target (`:810-815`);
  cancels a differing prior task (`:817-823`); initial strike through
  `skill.Use(Character, caster, sct, skillObject, false /* bypassGcd=false */, out _)` (`:841`)
  — so the engine's GCD/cooldown/range gates apply to the first strike; then
  `Character.IsAutoAttack = true; Character.StartAutoSkill(skill)` (`:844-845`) starts the
  `UseAutoAttackSkillTask` loop on attack-delay cadence (`Unit.cs:601-613`).
- Terminal semantics: **synchronous `Completed("auto-attack started ({skill}) on {target}")`
  with the engine `SkillResult` attached** (`:847`) — completion means "loop started," NOT
  "damage landed." Damage lands asynchronously per attack-delay tick; a `TooFarRange` first
  strike still returns `Completed` with that result while the loop runs [INFERENCE from
  `:841-847` — the result value is recorded, not branched on].
- Stop: `StopAutoAttack` cancels `AutoAttackTask`, clears `IsAutoAttack`, always
  `Completed("auto-attack stopped")` (`GameplayActor.cs:850-869`).

### A.3 `Cast(skillId, targetObjId)` — the skill verb (rotation / fallback)

- Preconditions (`GameplayActor.cs:640-668`): THREE dispatch gates only — (1) template exists
  (`:650-653`), (2) character knows it (`Skills.ContainsKey || IsVariantOfSkill ||
  IsDefaultSkill || IsCommonSkill`, `:658-663` — same rule as the `CSStartSkillPacket`
  learned-skill branch, `:171`), (3) `ResolveUnit` non-null (`:665-668`). NO alive gate
  (dead target reaches the engine → `TargetDied`), NO range/cooldown/mana gate.
- Execution: `request.Start` then `Character.UseSkill(skillId, target)` =
  `Unit.UseSkill` → `skill.Use(this, caster, sct, null, bypassGcd: true, out _)` (`Unit.cs:1069-1080`).
  Note: actor-Cast bypasses GCD (`bypassGcd=true`); actor-AutoAttack does NOT (`false`).
  GCD convergence is explicitly deferred to a future bounded executor (`GameplayActor.cs:679-682`).
- Terminal: `Success` → `Completed("skill {id} cast succeeded")` + async effect observation
  registration (`:684-694`); any other `SkillResult` → `RejectedAction, "skill {id} refused:
  {result}"` (`:696`). Observation failure ≠ action failure (`:690-691`).
- Retry: none in the actor. `CombatExecutor` (test-only, see §C) retries ONLY `CooldownTime`,
  `MaxAttempts = 3`, `WaitBudget = 2 s` (`CombatExecutor.cs:29-34,57-63,181`).

### A.4 `CastAt` — irrelevant for G6 (position-target skills only; 251 needs unit damage)

Documented for exclusion: same template/learned gates + reagent pre-flight, engine
`SkillCastPositionTarget` path (`GameplayActor.cs:699-777`).

### A.5 Target validation / skill legality / cooldown-GCD / resources (engine truth)

Order inside `Skill.Use` (`Skill.cs:99-266`):
1. `UnitRequirementsGameData.CanUseSkill` (ability/class/quest-context/unit-req rows;
   includes `UrkTargetNpcGroup`-family current-target gates, `SkillResult.cs:111-115`) → non-`ok`
   maps to `UnitReqsOrFail`-family refusal (`Skill.cs:107-115`).
2. Per-skill cooldown: `Template.CooldownTime > 0 && CheckCooldown` → `CooldownTime` (`:117-121`).
3. Attack-timing/GCD gate (unless `bypassGcd`): `SkillLastUsed + delay` (150 ms; 500 ms for
   skills 2/3/4 on characters) and `GlobalCooldown` → `CooldownTime` (`:123-159`).
4. `GetInitialTarget`: Hostile-type skills resolve the objId and apply relation-not-Hostile/Neutral
   + `!CanAttack` → null target → `NoTarget` (`Skill.cs:423-445`).
5. Mana: `ManaCost(unit) > unit.Mp` → `LackMana` (`:198-199`).
6. Range (see §E): weapon-override or template band → `TooCloseRange` / `TooFarRange` (`:218-266`).
7. Post-entry: `ConsumeMana` + `AddCooldown` (`:689-690`); GCD stamped (`:649`).

### A.6 Combat state / damage / death

- `Unit.IsDead ⇔ Hp <= 0` (`Unit.cs:946-952`); `ReduceCurrentHp` → `DoDie` fires
  `Events.OnDeath` + `ParentWorld.Events.OnUnitKilled` + killer `OnKill` (`Unit.cs:373-376,484-495`).
- `DoDie` clears both sides: victim `IsInBattle=false`, killer `CurrentTarget=null` +
  `SCTargetChangedPacket(killer, 0)` broadcast, `StopAutoSkill` on a character killer
  (`Unit.cs:544-567`). I.e. **death auto-clears the killer's target and stops its auto-skill** —
  G6 death detection can read `CurrentTarget == null` / `IsAutoAttack` decay, but authoritative
  detection is the victim's `Hp <= 0` / `IsDead` live read (roam precedent `:745-746`).
- `IsInBattle` is set by aggro/damage paths and decays on `DefaultCombatTimeout` since
  `LastCombatActivity` (`Unit.cs:1653-1657`); GOAP projects it as `InCombat`
  (`BotWorldStateProvider.cs:86-87`). It is advisory for G6, not a gate.
- Quest kill credit flows through the real `DoOnMonsterHuntEvents` (spike hunt loop relies on
  it, `AdventurerSpikeScenario.cs:402-407`); 251 is ItemGather not MonsterHunt, so credit flows
  through loot → `OnItemGather` (untouched; G7).

### A.7 `Loot` (G7, excluded — cited for the split)

Resolve (`GetBaseUnit`) + flat range ≤ `LootingContainer.MaxLootingRange = 200f`
(`LootingContainer.cs:32`) + non-empty container, else `Rejected`; `OpenBag(lootAll: true)`,
`Completed` with granted count; retry-after-success grants nothing (`GameplayActor.cs:1733-1762`;
Q4 conservation proof `Q4LiveHuntLootE2eTests.cs:145-161`).

---

## B. FIRST COMBAT ACTION — default skills at level 10, skill-2 check, canonical first step

### B.1 What a level-10 bot knows (grounded end)

- The scenario rig learns skills through the normal path: `character.Skills.AddSkill(skillId)`
  per `template.Skills` (`BotScenarioRunner.cs:296-307`), and sets `Ability1/2/3` from
  `template.AbilityTrees` (`:260-265`).
- The fox-cull spike (closest production-shaped combat demo) provisions `AbilityTrees =
  [AbilityType.Fight]` + appearance gear and casts rotation `[18131, 18134]`
  (`BotScenarioTemplates.cs:380-384`; `AdventurerSpikeScenario.cs:122`): 18131 "3단 베기"
  first hit leads (BUG-016 fixed), 18134 (area_radius=0) is the fallback
  (`AdventurerSpikeScenario.cs:69-82`; Q4 fallback `Q4LiveHuntLootE2eTests.cs:250-255`).
- 18131 is "the Fight ability-1 start skill: Hostile target, 0 cast/cooldown, 4 m range,"
  learned through `CharacterManager.ApplyPlayerProgression`
  (`AdventurerSpikeScenario.cs:31-33,69-71`). The fox template runs at Level 50 in the spike
  because foxes leash-reset and a level-30 run starved on mana (`BotScenarioTemplates.cs:386-394`);
  the G4/G5 fixture pins Level 10 (`G4QuestObjectiveTargetTests.cs:51`,
  `G5QuestPursuitGateTests.cs:62`).
- **Exact learned set of a production level-10 bot: UNKNOWN** (progression table not read this
  pass). What IS grounded: whatever `Skills` holds, `SelectPrioritizedSkill` falls back to
  `bot.Skills.Skills.Keys` when no explicit list is passed (`CombatDecisionTree.cs:302-308`).

### B.2 Skill-2 assumption check

- Skill 2 = `BasicMeleeAutoAttackSkillId` (`CombatDecisionTree.cs:111`). `AutoAttack` needs
  only template-existence (`GameplayActor.cs:825-827`); whether skill-2's template row loads
  headless is UNKNOWN (sqlite absent; `SkillManager` loads from live data). If the row is
  missing the verb fails closed with `"unknown auto attack skill 2"` — observable, not silent.
- Skill-2 range: fallback table `(0, 4)` (`CombatDecisionTree.cs:224`); engine truth =
  skill-2's own template band, UNKNOWN until read live. Fist-default (weapon-override skills
  with no weapon) max 3.0 m (`Skill.cs:238-239`) — a lower bound on what the engine may enforce.
- No `IsDefaultSkill`/`IsCommonSkill` dependence for AutoAttack (no learned gate at all);
  for Cast, 18131 passes gate 2 only if learned/variant/default/common (`GameplayActor.cs:658-661`).

### B.3 AutoAttack-vs-Cast canonical first step (from code, not invented)

- **Existing production behavior always opens with `AutoAttack`, then layers `Cast`:**
  roam hunt: `if (!IsAutoAttack) actor.AutoAttack(objId)` (`BotRoamStepExecutor.cs:831-834`),
  then `SelectPrioritizedSkill` → `actor.Cast` on `HuntCastInterval` 800 ms cadence (`:836-850`,
  interval `:116`); PvP mirror identical (`:2522-2540`); party-assist uses Cast-only with
  hardcoded 18131 (`:615-620`) — assist, not hunt.
- **Scenario demonstrations open with `SetTarget` → range-maintenance → `Cast` rotation, never
  AutoAttack:** spike `SetTarget` (`AdventurerSpikeScenario.cs:468-475`) → `MaintainRange`
  (`:486-487`, `EngageRange = 3f` default `:192`, band-edge `BandPoint` `:914-921`) → burst
  `actor.Cast` per rotation skill with hpBefore→hpAfter per stage (`:503-525`); Q4
  `HuntKillAsync` casts 18131 (fallback 18134) to 60-cast budget with hpBefore/hpAfter/alive
  triple observation (`Q4LiveHuntLootE2eTests.cs:207-277`); M53 `SetTarget → Cast(90001)`
  (`M53CoreSurfaceExitScenario.cs:91-103`); Halcyona `CurrentTarget=` direct write (fixture!) +
  `Cast` (`HalcyonaSkirmishScenario.cs:181-182`).
- **Do not invent policy:** G6's first step is therefore a choice between two code-grounded
  precedents — (i) production hunt shape (AutoAttack first, Cast layered), (ii) scenario shape
  (Cast-only rotation). §H recommends (i) on production-ownership grounds; the plan does not
  prescribe rotation contents (no 251-vs-boar rotation exists in code — the 18131/18134 rotation
  is quest-250/fox-specific).

---

## C. PRODUCTION OWNERSHIP — who decides / requests / keeps alive / detects death / stops

| Owner | Decides attack? | Owns request? | Keeps alive across wakes? | Detects death? | Stops? | Class |
|---|---|---|---|---|---|---|
| `BotRoamStepExecutor` wildlife hunt (`:737-900`) | YES — scan→engage→cast loop, quest-blind | YES — live-actor `AutoAttack`/`Cast`/`MoveTo`/`Stop`/`PreemptCurrent` | YES — `TargetNpcObjId` + `TargetEngagedUtc` + 30 s cap + per-wake revalidation (`:739-791`) | YES — `targetUnit == null \|\| Hp <= 0 \|\| !IsAttackableWildlife \|\| 30 s` (`:745-748`), then clears `CurrentTarget` broadcast + `StopAutoAttack` + preempt (`:772-790`) | YES — `StopAutoAttack` + target-clear + `PreemptCurrent("hunt target down")` (`:772-789`) | **PRODUCTION** (only live-actor combat initiator today; quest-blind → FIXTURE-ONLY for 251 purposes) |
| `BotRoamStepExecutor` PvP engage (`:2452-2584`) | YES — player-target validate→engage→scan | YES — same verbs + `MoveToUnit` | YES — `TargetPlayerObjId` across wakes | YES — same shape (`:2470-2480`) | YES | **PRODUCTION** (out of G6 scope — NPC prey only) |
| `QuestBehavior` (+ `QuestObjectiveTargetSelector`) | NO combat today — Target + pursuit Move/Stop only; `"Never Cast, AutoAttack, Loot, or credit — those verbs are unreachable from here"` (`QuestBehavior.cs:518-519`); `Dispatch` handles only Advance/Accept/TurnIn/Target/pursuit-Move/pursuit-Stop (`:657-684`) |owns Target + pursuit legs | pursuit: `LastPursuitIssue` drift cache (`:85-86`) + per-wake funnel reselect | pursuit-invalid only (`target-dead/not-hostile/not-attackable/not-visible`, `:550-556`); no kill detection | pursuit legs only | **PRODUCTION** (G6 extends here — the quest-valid combat owner) |
| `GameplayActor` | NO — executes, never decides; range/cooldown/mana deliberately NOT gated (`:679-682`) | owns request lifecycle (`TryBegin`, `_active`) + movement stepping + stuck + terminals | movement `_move*` only; combat loop owned by `StartAutoSkill`/`TaskManager` (`Unit.cs:601-613`), not by the request | NO — `AutoAttack`/`Cast` do not watch HP | NO auto-stop (death stops via engine `DoDie`→`StopAutoSkill`, `Unit.cs:555-564`) | **PRODUCTION** (executor) |
| GOAP (`AcquireHostileTarget`/`ApproachTarget`/`ExecuteCombatCombo`/`LootCorpse`, `CombatActions.cs`) | shapes only | via ephemeral `new GameplayActor` (`:21,55,102,147`) | NO — `CurrentActorRequest` in runner, plan index not combat-specific | `TargetDead` flag (`BotWorldStateProvider.cs:216-221`) → `Succeeded`/`Invalidated` (`CombatActions.cs:66-68,113-118`) | NO tower-owned stop | **STALE** (acquire dispatches `SetTarget(0)` → `ResolveUnit(0)` null → always `Rejected`, `GameplayActor.cs:4868-4871`; never a wire target — G4 §G.7) |
| `CombatDecisionTree` (`Evaluate`/`SelectPrioritizedSkill`/`IsSkillInRangeAndReady`/`InferRole`) | advisory WHAT (skill choice, spacing, flee) — no dispatch | NO | NO (`lastSkillUsed` is caller-held: roam `state.LastSkillUsed :784,850`) | NO | NO | **REUSABLE** (pure; roam already composes it `:801,838`) |
| `CombatExecutor` | NO — single-cast wait/retry helper | owns `_pending` cast (`:80,94-102`) | one logical cast within `WaitBudget` | NO | `Cancel` (`:194-198`) | **FIXTURE-ONLY** (test-only; never wired to a live actor leg) |
| Scenarios (`AdventurerSpike`, `LevelingLoop.SelectHuntTarget`, `M53`, `Halcyona`, `PartySpike`) | rig-driven (harness chose) | ephemeral actor, trace discarded (`S`/`E`) | run-local (`credited`, `noProgress`, exclusion sets) | live HP reads (`target.Hp <= 0` mid-chain break `:518-519`; `noProgress` skip `:536-553`) | run ends | **SCENARIO-ONLY** (engine-true, never autonomy; Halcyona `CurrentTarget=` write is a fixture bypass) |
| `BotGoalArbiter` / `GameplayActorStepExecutor` / scheduler | no combat dispatch found (grep: no `AutoAttack`/`Cast`/`SetTarget` hits) | — | — | — | — | **UNKNOWN** for combat (arbiter owns `quest.progress` vs `recovery.rest` arbitration per G3 only) |
| `OutOfCombatRecoveryModule` | NO attack (food/UseItem/sit regen, `OutOfCombatRecoveryModule.cs:336-340`) | recovery steps | stage machine | trigger conditions reference combat state (`:337`) | stands up at HP/MP ≥ 95% | **PRODUCTION** (adjacent: post-combat recovery owner; G6 must not fight it — see §I) |
| `PlayerBotController` (`AcceptQuest`/`KillNpc`/… event-fire) | NO (synthetic `Character.Events.On*`, no world action) | — | — | — | — | **DUPLICATE** (bypass surface; never cite as combat evidence — matrix §A4) |

One-liner: today only the quest-blind roam hunt initiates combat on the live actor; the
quest-valid path (QuestBehavior) stops at Target + pursuit, GOAP acquire is a dead stub, and the
tree/executor advise but never dispatch — so G6's owner is QuestBehavior composing the roam
hunt shape with the G4 selector.

---

## D. LEGALITY — hostility, CanAttack, skill legality, range, dead/alive, visibility, faction, type, current-target reqs

Layering (hard rule, G4 §E.4 extended): **QuestBehavior WHY** (should this kill advance 251?)
× **combat WHAT** (which skill/spacing per the tree) × **actor-engine WHETHER** (may it fire now?).

| Gate | Authority | Cite | Layer |
|---|---|---|---|
| Quest-relevance | `IsRelevant`: 251 active + 4058 < 3 (`QuestObjectiveTargetSelector.cs:101-108`) + template 3475 | `:101-108,152-155` | WHY (snapshot, no combat state) |
| Alive | live `Hp > 0` / `IsDead`; `AutoAttack` dispatch rejects null-or-dead; selector `dead` funnel; roam 30 s-cap revalidation | `GameplayActor.cs:789-791`; `QuestObjectiveTargetSelector.cs:169-174`; `BotRoamStepExecutor.cs:745-748`; `Unit.cs:946-952` | WHETHER (query per wake + dispatch) |
| Hostile | `IsHostileTarget`: dead→false; faction-115 →true; null-faction →true; else `CanAttack` + Hostile (`CombatDecisionTree.cs:73-102`); roam private duplicate (`BotRoamStepExecutor.cs:2426-2449`) — call the public one | `:73-102` | WHETHER |
| `CanAttack` (faction/relation/safe-zone/zone-conflict) | `BaseUnit.cs:54-138`: null-faction permissive `:56-57`; self `:58-59`; mother-zone `:76-80`; PvP safe/flag/ForceAttack/team `:82-104`; NPC-safety TODO open `:109-112`; `BlocksPvpDamage` `:130-133`; else relation==Hostile `:137` | `:54-138` | WHETHER (engine owns) |
| Skill legality (Hostile-type) | relation not Hostile/Neutral + `!CanAttack` → null → `NoTarget` | `Skill.cs:423-445,173-183` | WHETHER (engine, post-hoc) |
| Skill legality (unit-reqs: ability, TargetNpcGroup, quest-context…) | `UnitRequirementsGameData.CanUseSkill` → `UnitReqsOrFail`-family | `Skill.cs:107-115`; `SkillResult.cs:60-61,111-115` | WHETHER (engine; includes current-target reqs e.g. skill 11641 → TargetNpcGroup 54 per `M1M2ReplayScenario.cs:114,459-464`) |
| Range | engine bands only (see §E) | `Skill.cs:218-266` | WHETHER (engine refusal) |
| Dead/alive mid-fight | victim `Hp` live read; killer target auto-cleared + auto-skill stopped by `DoDie` | `Unit.cs:484-495,544-567` | WHETHER (engine event) |
| Visibility | `CanSeeTarget`: `IsVisible` + no stealth tag (`BaseUnit.cs:145-151`); selector `stealth` funnel; survey-only elsewhere | `:145-151`; `QuestObjectiveTargetSelector.cs:193-198` | WHETHER |
| Faction/type | inside `CanAttack` + template 3475 match (kind-specific `GetNpc`) | `BaseUnit.cs:54-138`; `QuestObjectiveTargetSelector.cs:152` | WHY×WHETHER split at the objId |
| Current-target reqs | some skills read `CurrentTarget` (TargetNpcGroup); `AutoAttack`/`Cast(unit)` set it from the objId param, so passing the selected objId satisfies both at once | `GameplayActor.cs:793-798`; `M1M2ReplayScenario.cs:459-464` | WHAT×WHETHER bridge |

Separation enforced: relevance (snapshot × static loot chain 3475→4530→4058, `LootPack.cs:145-147,195-201`
per G4) shares nothing with legality but the objId; merging into one flag is PROHIBITED (G4 §E.4).

---

## E. RANGE AUTHORITY — truth source per verb; no silent 3.0 m promotion

- **Engine truth (only authority):** `Skill.Use` computes `skillRange` from
  `ApplySkillModifiers(Template.MaxRange)` (`Skill.cs:219`), `targetDist` via radius-subtracting
  `GetDistanceTo(target, true)` (`:220`; `BaseUnit.cs:205-235`), weapon override replaces the band
  (`Skill.cs:236-248`, fist default max 3.0 m `:238-239`), then `TooCloseRange` (`:250-256`) /
  `TooFarRange` (`:261-266`). No "can I attack from here" query exists — post-hoc refusal only.
- **Skill 2 (autoattack-melee):** truth = skill-2's template row band, UNKNOWN (sqlite absent).
  Brain approximations: fallback `(0, 4)` (`CombatDecisionTree.cs:224`); zero-max default 4.0 m
  (`:177-179`); `DefaultMeleeMax` 3.5 (`:59`); GOAP `TargetEngagementRange = DefaultMeleeMax`
  (`BotWorldStateProvider.cs:37`, 3-D check `:211-213`).
- **Weapon skills (18131-class):** truth = template band as overridden by
  `WeaponSlotForRangeId → HoldableTemplate.Min/MaxRange` (`CombatDecisionTree.cs:162-182`;
  `Skill.cs:236-248`). Spike documents 18131 as 4 m (`AdventurerSpikeScenario.cs:31-33`).
- **Ranged autoattack (skill 4):** fallback `(4, 20)` (`CombatDecisionTree.cs:225`) — note the
  4.0 m MINIMUM: a RangedPhysical bot held at 3.0 m may eat `TooCloseRange` (G5 §D.4 tension;
  `AutoAttack` picks skill 4 for RangedPhysical regardless of distance, `GameplayActor.cs:806-808`).
- **G5 3.0 m is fixture-scoped, NOT promoted:** `PursuitStopRadiusM = 3.0f` is documented as
  "fixture-scoped, NOT a universal combat-range rule — the engine owns real range refusal"
  (`QuestBehavior.cs:58-65`). It sits inside every melee authority (3.5, (0,4), fist 3.0 boundary)
  while skill-2's row stays UNKNOWN. G6 MUST NOT promote 3.0 m to a gate: use it as the
  dispatch floor (pursuit holds there), let `TooFarRange`/`TooCloseRange` be ground truth, and
  name the skill whose band is claimed (`GetSkillEffectiveRange` once the skill is named).

---

## F. SKILL STATE — what decision code can know; gate-or-actor-refusal for G6 minimalism

| Signal | In `ActorObservation`/`BotObservedContext`? | Where decision code reads it |
|---|---|---|
| Available (learned) skills | NO | live `Character.Skills.Skills.Keys` (`CombatDecisionTree.cs:304-305`); actor gate mirrors it (`GameplayActor.cs:658-661`) |
| Cooldown | NO | live `bot.Cooldowns.CheckCooldown(skillId)` (`CombatDecisionTree.cs:243-244`); engine re-checks (`Skill.cs:117-121`) |
| GCD / attack-timing | NO | live `unit.GlobalCooldown`, `SkillLastUsed` (`Skill.cs:123-159`; `Unit.cs:266-269`); actor-Cast bypasses (`Unit.cs:1079` `true`), actor-AutoAttack respects (`GameplayActor.cs:841` `false`) |
| Mana/range | Mp/MaxMp YES (`ActorObservation.cs:35-37`); per-skill cost/range NO | live `bot.Mp < template.ManaCost` + `GetSkillEffectiveRange` (`CombatDecisionTree.cs:252-259`); engine re-checks (`Skill.cs:198-199,218-266`) |
| HP (self) | YES (`:31-33`) | snapshot + live `character.Hp/MaxHp` (spike sustain `:456`; tree flee `:499`) |
| Current target | YES as objId (`:29`) | snapshot `CurrentTargetObjId` + live `Character.CurrentTarget` (GOAP `:201`; roam `:795-799`) |
| Target HP/alive | NO (bare objIds) | live `npc.Hp <= 0` per wake (selector `:169`; roam `:745-746`) |

**Decision for G6 minimalism: actor-refusal, NOT decision-gate.** Rationale from code:
(1) the actor deliberately does not gate range/cooldown/mana — `Cast` gates template+learned+resolve
only (`GameplayActor.cs:650-668`) and `UseSkill` refuses post-entry (`:679-696`); duplicating the
pipeline behavior-side is the filed G.4 duplication risk; (2) `IsSkillInRangeAndReady` is ADVISORY
(ordering only, never legality — G4 §E.2); (3) every engine refusal is an observable `Rejected`
with the `SkillResult` in the detail, so a first-action gate can propose → dispatch → read the
terminal without pre-checking. Decision code needs exactly: selected objId (have), in-range
settled (have, G5), relevance (have, `IsRelevant`), and the verb-specific dispatch preconditions
above (resolve + template-exists + learned-for-Cast). Everything else the engine answers post-hoc.

---

## G. DECISION TREE — existing target→approach→attack shapes; reuse vs extract vs narrow vs canonical

1. **Roam hunt leg (PRODUCTION, target→approach→attack complete):** revalidate
   (`:739-791`) → `dist > engageRange` (Melee 3.0 / else 15.0, `:800-802`) → `MoveTo` chase with
   > 2 m drift re-issue (`:804-817`) → in-range `PreemptCurrent("hunt in engage range")` (`:823-824`)
   → face (`:827`) → `AutoAttack` once (`:831-834`) → `SelectPrioritizedSkill` + `Cast` per
   800 ms (`:836-850`) → death/invalid/30 s-cap teardown (`:750-790`). **REUSE as the shape.**
2. **Spike hunt round (SCENARIO, Observe→Select→SetTarget→band→burst):** `SelectHostile`
   (`AdventurerSpikeScenario.cs:767-796`) → `SetTarget` (`:468-475`) → `MaintainRange` band
   (`:870-907`) → rotation burst with hpBefore→hpAfter per cast (`:503-525`) → loot → advance
   (`:561-603`). **EXTRACT the observation idiom** (hpBefore/hpAfter/alive triple,
   no-progress exclusion `:536-553`) into gate diagnostics; do not wire production through it.
3. **`LevelingLoop.SelectHuntTarget` (SCENARIO helper):** template/group/zone-match +
   `CanAttack` + nearest (`LevelingLoopScenario.cs:2842-2893`). Superseded for 251 by the frozen
   G4 selector (same shape + relevance + stealth). **Do not touch.**
4. **GOAP acquire→approach→combo→loot chain (STALE):** `AcquireHostileTargetAction` dead stub
   (`CombatActions.cs:19-23`); `ApproachTargetAction` `MoveToUnit(CurrentTarget)` (`:53-58`);
   `ExecuteCombatComboAction` `Cast(SkillId, CurrentTarget)` default skill **139** (`:87,100-105`);
   `LootCorpseAction` (`:144-149`). **NARROW proposal: do not revive** — the default-139 combo and
   SetTarget(0) acquire are fixture values, never a wire target.
5. **QuestBehavior Target + pursuit (PRODUCTION, frozen):** `ObjectiveTargetProposal` priority 25
   (`QuestDecisionScenario.cs:53`) + `PursuitProposal` priority 24 (`:59`) → `Dispatch` Target /
   pursuit-Move / pursuit-Stop (`QuestBehavior.cs:657-684`). **CANONICAL action: add the third
   proposal** — a combat proposal at priority < 24 (below pursuit, so range-hold wins) dispatching
   `AutoAttack` (+ optionally `Cast`) on the SAME selected objId, gated on the pursuit
   in-range predicate. No new queue kind (AutoAttack has none — dispatch through the live actor
   like roam does), no new audit shape, no travel/credit/loot wiring.

---

## H. CONTRACT — smallest honest G6 gate (chosen from semantics)

Candidate terminals, eliminated in order:
- ❌ `Cast Completed(Success)` — Success means "engine accepted," damage may land a tick later
  (effect delay, `GameplayActor.cs:686-691`); Q4 observes death on hpBefore/hpAfter/alive edges,
  not on the terminal (`Q4LiveHuntLootE2eTests.cs:243-245`).
- ❌ `damage > 0` on the first action — damage is asynchronous (plot/attack-delay) and
  weapon-scaling (weapon-less 18131 deals ZERO — `BotScenarioTemplate.cs:83-85`); asserting it on
  the dispatch terminal couples two different clocks.
- ❌ `TargetDead` — that is a kill gate (G7), not a first-action gate.
- ✅ **`AutoAttack-started`: `AutoAttack` returns terminal `Completed` with
  `"auto-attack started ({skill}) on {target}"`, the loop is live (`IsAutoAttack`,
  `AutoAttackTask` non-null), target pinned (`CurrentTarget == selected`), and zero
  `Rejected` on the dispatch.** This is exactly what the verb's semantics promise (`:847`)
  and what roam asserts before layering casts (`:831-834`).

**G6 gate (proposed):** from 251-ACTIVE + G4-selected live 3475 + flat ≤ 3.0 m + settled,
autonomy (QuestBehavior combat proposal) dispatches `AutoAttack(selected)` on the live actor →
terminal `Completed(auto-attack started)` → loop live → target pinned → no Cast/Loot/credit yet.
Damage>0 becomes a trailing observation (hpBefore→hpAfter across the next wakes, spike/Q4 idiom),
NOT the gate predicate. **Boundary:** G6 stops at loop-started + first-damage-observed; rotation
choice, kill, corpse loot, 4058 credit, Ready transition, and turn-in are G7+ (see §I).

Why AutoAttack-first and not Cast-first: (i) it is the production hunt shape (§B.3-i) vs the
scenario-only Cast shape; (ii) `AutoAttack` carries the only dispatch-time alive gate (`:790-791`)
and the idempotent already-active fast-path (`:810-815`), so re-wakes are safe; (iii) skill choice
is engine-mediated (skill 2 vs 4 from role+weapon+distance) rather than a hardcoded 18131 the bot
may not know; (iv) Cast-layering already exists per-wake in roam (`:836-850`) and can follow once
the loop is proven.

---

## I. G6/G7 SPLIT — kill/loot/credit/turn-in stay separate

Separable, with one load-bearing seam:

- **Kill (G7a):** sustained loop + `SelectPrioritizedSkill`/`Cast` layering (roam `:836-850`),
  death observed via victim `Hp <= 0` (Q4 triple-edge `:244`) with killer-side corroboration
  (`CurrentTarget == null` post-`DoDie`, `Unit.cs:551,567`). No new verb needed.
- **Loot (G7b):** `Loot(corpseObjId)` ≤ 200 m, non-empty container (`GameplayActor.cs:1733-1762`);
  Q4 conservation shape (granted == before−after, caller deltas match) reusable as the gate
  predicate (`Q4LiveHuntLootE2eTests.cs:145-161`); retry-after-success grants nothing (`:163-184`).
- **Credit (G7c):** loot → `OnItemGather` 4058×3 → step machine → Ready. Untouched by G6 by
  construction (combat proposal carries no quest payload; meatPre==meatPost asserted like G5).
- **Turn-in (G7d):** existing `TurnIn*` proposals, priority 30, already production
  (`QuestBehavior.cs:380-437`; `QuestDecisionScenario.cs:49`).
- **The seam that joins them:** `HasLootedCurrentTarget` memory + `HasCorpseToLoot`/`TargetDead`
  flags (`BotMemory.cs:42-43`; `BotWorldStateProvider.cs:216-225`) and the roam 30 s engagement
  cap + `StopAutoAttack` + target-clear teardown (`BotRoamStepExecutor.cs:772-790`). G6 must leave
  these alone (no clearing, no looting) so G7 has something to observe.
- **Inseparable-if:** боar leash-reset (Npc return-to-idle heals; spike needed Level 50 to kill
  inside the window, `BotScenarioTemplates.cs:386-394`) may force G6's fixture to prove
  first-damage only, with kill/loot/credit demonstrated at higher level or not at all — that is
  a fixture concession, not a scope merge. Also `OutOfCombatRecoveryModule` (priority 85,
  `:78`) may preempt post-fight wakes; G6 observes only, never disables it.

---

## J. FIXTURE + DIAGNOSTICS + TAXONOMY + TRACES

### J.1 Starting state (copy G4/G5, add nothing)

Fresh bot, Level 10, vitals refilled (HP/MP ≥ 0.95 — G3 doctrine), 251 ACTIVE via real accept at
true giver 3512, staged at a live 3475 spawner (`teleportToNpc(3475)` + `PollNpcObjId` proof),
bag 4058 < 3, `snap==actor` gap < 0.5 m, route settled — then pursuit closes to ≤ 3.0 m and holds
`Stop`+settled (G5 end state). START = one bounded scheduler wake; observe-only thereafter
(G4 `:24-28`; G5 `:326-359`).

### J.2 Observe-only rules (G6 adds: never desecrate the fight)

Harness issues wake + observe only; never `Cast`/`AutoAttack`/`Loot`/move/attack after START
(G5 `:674-676`). New for G6: never clear `CurrentTarget`, never `StopAutoAttack`, never loot the
corpse if the prey drops (that is G7's evidence); the quest-blind roam hunt (`EnableWildlifeHunt`)
must be OFF or its `Cast`/`AutoAttack` voids the run (G5 `ScanCombat` rationale `:881-884`).

### J.3 Funnel fields (extend, never rename)

`CycleId / target(template 3475, objId) / skill(id 2 vs 4 + reason role/dist/weapon) /
rangeM(flat, engine band claim) / HP(hpBefore→hpAfter, targetAlive) / trace(audit rows) /
terminal(Completed-started vs Rejected-{reason}) / rejects(unresolved/dead/template/hostile/
attack/stealth + cast-refusals by SkillResult: not-learned/TooFar/TooClose/CooldownTime/
LackMana/NoTarget/…)`. Per-cast stage detail carries `[target hp {before}→{after}]` (spike `:512-515`).

### J.4 PASS + predicate FAILs

- **PASS:** selected 3475 + in-range + settled → combat proposal dispatched `AutoAttack(selected)`
  → terminal `Completed(auto-attack started)` → `IsAutoAttack` live + `CurrentTarget == selected`
  → zero `Rejected` on the dispatch; meat 4058 unchanged; quest still Progress; prey HP observed
  falling on trailing wakes (informational).
- **FAILs (first-zero rule):** `COMBAT-NO-PROPOSAL` (no combat proposal — relevance/range/priority
  miswired) / `COMBAT-NOT-DISPATCHED` (proposal lost selection) / `COMBAT-REJECTED-{reason}`
  (resolve/dead/template-missing/not-learned) / `COMBAT-NO-LOOP` (Completed but `IsAutoAttack`
  false — engine refused the first strike silently) / `COMBAT-WRONG-SKILL` (skill 4 where 2
  expected or vice versa — role/weapon audit) / `COMBAT-COMBAT-DETECTED-EARLY` (Cast/Loot before
  AutoAttack-started — layering violated) / `TARGET-LOST` (prey dead/despawned before dispatch —
  reselect job, not combat's).

### J.5 Existing human/engine trace refs (Q4 hunt/loot known)

- Q4 live hunt: `drive cast` 18131→18134-fallback loop to 60-cast budget, hpBefore/hpAfter/alive
  triple, re-anchor on `TooFarRange` streaks (`Q4LiveHuntLootE2eTests.cs:207-277`); loot
  conservation + retry-empty (`:118-184`).
- Spike: `SetTarget → MaintainRange → burst-Cast` with per-stage hp deltas, sustain episodes,
  no-progress exclusion, loot-per-corpse, advance-per-kill (`AdventurerSpikeScenario.cs:402-603`).
- G5 zero-combat scan: `landed (Cast|AutoAttack|Loot)` regex over decide/live + game.log window
  scan (`G5QuestPursuitGateTests.cs:885-932`) — **invert for G6**: expect exactly one landed
  `AutoAttack`, zero `Cast`/`Loot` at the gate instant.
- Engine traces: `PlayerTraceService` records per-skill events with range
  (`PlayerTraceService.cs:340-344`) and refusals (`:408-412`); `QuestObjectiveTargetDiag`
  funnel line (`QuestBehavior.cs:639-655`); `ActorAuditRecord.ToJson` trace shape (M53 `:16`).

---

## K. RESIDUAL RISKS + UNKNOWNS (honest list)

1. Skill-2 template row presence headless: UNKNOWN (fails closed, observable).
2. Production level-10 learned set (which Cast rotation, if any, is legal): UNKNOWN — G6 opens
   with AutoAttack precisely to avoid depending on it.
3. Boar-3475 model radius (engine `GetDistanceTo` subtracts both radii, `BaseUnit.cs:228-232`):
   UNKNOWN — flat 3.0 m hold vs engine-measured distance may disagree by ~1 m; ground truth is
   the engine refusal, not the funnel number.
4. Leash-reset vs level-10 DPS (spike needed 50): may cap G6 at loop-started + first-damage.
5. Ranged-role starters (`Ability1 == Wild → RangedPhysical`, `CombatDecisionTree.cs:283-290`):
   skill-4 pick with 4.0 m minimum vs 3.0 m hold → possible `TooCloseRange`; fixture should pin a
   melee (Fight) bot first.
6. `IsSkillInRangeAndReady`/`SelectPrioritizedSkill` call `SkillManager.Instance` — null-safety
   falls back to `GetKnownSkillRange`, but live behavior needs the manager loaded (`:247-271`).
7. Bare-objId recycling (`IdManager.cs:153-214`; no generation check, G5 §F.2) — mitigate by
   per-wake template+alive+hostile+visible reselect, never storing objIds across legs.
8. `BotGoalArbiter` combat interplay: UNKNOWN (no combat dispatch found; recovery module priority
   85 may preempt post-fight wakes — observe, don't disable).

---

## FINAL VERDICT BLOCK

- **DISCOVERY: COMPLETE.** All ten questions answered from code with file:line cites; residual
  unknowns (K1–K8) are named and none blocks the plan.
- **Current combat path:** `QuestBehavior` (Target priority 25 + pursuit Move/Stop priority 24)
  → stops at 3.0 m hold. The only live-actor combat initiator is the quest-blind roam hunt
  (`AutoAttack` → `SelectPrioritizedSkill`/`Cast`, `BotRoamStepExecutor.cs:831-850`); GOAP acquire
  is a dead stub; tree/executor advise but never dispatch.
- **Missing seam:** a quest-owned combat proposal in `QuestBehavior` (priority < 24, same selected
  objId) dispatching the live-actor `AutoAttack` verb — no new queue kind (none exists for
  AutoAttack by design), no new audit shape, no travel/credit/loot wiring. `Dispatch` needs one
  new arm; the funnel needs skill-id + terminal + cast-refusal fields.
- **Boundary:** G6 ends at `AutoAttack-started` (Completed + loop live + target pinned) with
  first-damage as trailing observation; rotation/kill/loot/credit/turn-in are G7a–d (§I).
- **Implementation (narrow proposal):** (1) combat proposal constructor (relevance + in-range +
  settled preconditions, idempotency `quest:{actor}:{cycle}:combat:251:{target}`); (2) `Dispatch`
  arm → `gameplayActor.AutoAttack(targetId)`; (3) funnel/diagnostics extension (§J.3);
  (4) gate test cloning G5 observe-only shape with the inverted combat scan (§J.5). Reuse roam
  teardown + spike hp-triple idioms; extract nothing; revive nothing.
- **Success contract:** 251-ACTIVE + selected 3475 + ≤3.0 m + settled → autonomy dispatches
  `AutoAttack` → `Completed(auto-attack started)` → `IsAutoAttack` + `CurrentTarget == selected`
  → meat unchanged, quest Progress, zero Cast/Loot at the gate instant.
- **Next action:** write the G6 gate spec (fixture §J.1 + predicates §J.4) and implement the
  combat proposal + Dispatch arm behind it; confirm skill-2 template row + Fight-starter learned
  set live before fixing the fixture level.
