# G4 Perception & Targets — §D–G (READ-ONLY audit, develop @ 2026-09-19)

READ-ONLY. Nothing modified, built, or run. G3 frozen — untouched.
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
G4 mission: from quest-251-ACTIVE, identify/select a valid hostile objective target; ends at selected, NO combat/nav.
Read-first context: `capability-surface-matrix.md`, `gap-senses-contracts.md`,
`brain-boundaries.md` (§2), `nearby-quest-contracts.md`. `UNKNOWN` = not found in code, nothing inferred.

---

## D. PERCEPTION SUFFICIENCY — what the snapshot exposes per nearby entity

### D.1 The two surfaces carry objIds only — zero per-entity attributes

`ActorObservation` (`AAEmu.Game/Core/Managers/Bots/ActorObservation.cs:22-91`):
`ActorId`, `Position`, `CurrentTargetObjId` (`:29`, 0 = none), Hp/Mp/Max (`:31-37`),
`NearbyCharacterObjIds` / `NearbyNpcObjIds` / `NearbyDoodadObjIds` (`:40-46`, bare `uint`
lists from 25 m `WorldManager.GetAround`, `GameplayActor.cs:262-264`),
`ActiveQuestIds` (`:49`), economy (`Money/BankMoney/LaborPower/BagItemCounts/BankItemCounts/CarriedPackTemplateId`,
`:52-67`), party (`:70-85`). **No template, hostile, alive, attackable, distance,
quest-relevance, zone, skill, cooldown field exists on any nearby entity.**
`BotObservedContext` (`BotDecisionProposal.cs:16-110`) is a 1:1 copy (`From` `:67-96`,
`Capture` `:99-103`) — same gaps by construction.

Per-entity table (nearby NPC via `NearbyNpcObjIds`):

| Needed for objective selection | In snapshot? | Where it actually lives |
|---|---|---|
| objId | YES (`:43`) | snapshot |
| templateId | NO | live `ParentWorld.GetNpc(objId).TemplateId` at resolve time |
| alive (Hp>0) | NO | live `npc.Hp <= 0` check at every selection site |
| hostile / attackable | NO | `CombatDecisionTree.IsHostileTarget` (`CombatDecisionTree.cs:73-102`) or `BaseUnit.CanAttack` (`BaseUnit.cs:54-138`); GOAP threat flags are a separate projection (`BotWorldStateProvider.cs:200-222`) |
| distance | NO | derived `MathUtil.CalculateDistance` / `Vector3.DistanceSquared` from live positions at every site |
| quest-relevance | NO | **nowhere as such** — must be derived (see D.3) |
| stealth-visible | NO | `Character.CanSeeTarget` applied only in `BotSurveySenses.ResolveOccupants` (`BotSurveySenses.cs:247-269`); plain `Observe` lists stealth units (note `:250-251`) |
| zone / world key | NO | live `Transform.ZoneId`; snapshot gap already filed (`brain-boundaries.md` §2.3 item 1) |

### D.2 How decision code resolves bare objIds today (all five sites re-resolve live)

1. **Adventurer spike** — `AdventurerSpikeScenario.SelectHostile` (`AdventurerSpikeScenario.cs:767-796`):
   iterate `observation.NearbyNpcObjIds` → `character.ParentWorld?.GetNpc(objId)` (`:780`) →
   optional template filter (`:782`) → skip `Hp <= 0` (`:784`) → `character.CanAttack(npc)` (`:786`) →
   nearest by `DistanceSquared` (`:788-793`). Observe-driven, closest to the G4 shape.
2. **Leveling loop** — `LevelingLoopScenario.SelectHuntTarget` (`LevelingLoopScenario.cs:2842-2893`):
   same shape + `QuestManager.CheckGroupNpc` group match (`:2859`) + victim **zone-group gate**
   (`:2877-2882`, compensates an engine gap — gap-senses §2.2 item 3) + exclusion set.
3. **Roam wildlife hunt** — `BotRoamStepExecutor.cs:835-875` (+ revalidation `:715-722`):
   does NOT consume the snapshot; scans `NearbyNpcProvider ?? WorldManager.GetAround<Npc>(…, HuntPerceptionRadius=45 m)`
   (`:838-840`, radius default `:108-110`) → private `IsAttackableWildlife` (`:2325-2348`) → nearest
   (`:849-854`) → pins `state.TargetNpcObjId` (`BotRoamState`, `:234-235`) with 30 s engagement cap (`:722`).
4. **Survey** — `BotSurveySenses.ResolveOccupants` (`BotSurveySenses.cs:242-271`): resolves
   NPC + doodad + character lists, applies `CanSeeTarget` stealth filter (`:250-251,:257,:266`),
   skips self (`:263-264`). The only stealth-correct resolver.
5. **Quest discovery** — `QuestBehavior.cs:112-157`: ignores `NearbyNpcObjIds` entirely, sweeps the
   `NearbyNpcProvider` for live `Npc` objects, nearest-first sorts before the take-3 cut
   (`:146-156`; `MaxDiscoverTargets = 3`, band `[1,9]` defaults in `QuestDecisionScenario.cs:40-46`),
   then `DiscoverQuests` per target with 25 m range gate inside dispatch
   (`GameplayActor.cs:1216-1222`; `MaxQuestDiscoverRange = MaxInteractRange = 25f`, `:1196,1461`).

Consequence (already filed, gap-senses §1.3 B-P1/B-P5/B-P6): bare-uint snapshot + live re-resolution
at every site = N+1 queries + TOCTOU (observe→dispatch drift; dispatch re-resolves fail-closed via
`ResolveUnit` → `ParentWorld?.GetUnit`, `GameplayActor.cs:4753-4760`, null → `RejectedAction`).

### D.3 Minimum missing fields for G4 objective selection (strengthen, no new architecture)

G4 needs, per nearby-NPC candidate: **(templateId, alive, hostile-via-CanAttack, visible-via-CanSeeTarget,
position/distance)** + quest-relevance. Recommendation (prefer existing surfaces):

1. **No snapshot schema change.** Add ONE pure helper shaped exactly like `SelectHuntTarget`
   (`LevelingLoopScenario.cs:2842-2893`) but for 251: candidates from `observation.NearbyNpcObjIds`,
   resolve via `ParentWorld.GetNpc`, filter `TemplateId == 3475` + `Hp > 0` + `character.CanAttack(npc)` +
   `character.CanSeeTarget(npc)` (stealth — the survey convention, `BotSurveySenses.cs:250-251`),
   nearest-first. This is a **derived projection** in brain-boundaries §2.2 terms: snapshot + live
   resolve at decision time, no second architecture, no cache (per nearby-quest §2.3 item 4: pure
   function over positions, never stored flags).
2. **Quest-relevance needs NO new field.** It is fully derivable from fields already in the snapshot:
   `ActiveQuestIds.Contains(251)` + `BagItemCounts[4058] < 3` + `template == 3475` + loot-gate held
   (holder has 251 ⇒ pack 4530 generates 4058; `LootPack.cs:195-201`, Q4 constants
   `Q4LiveHuntLootE2eTests.cs:37-39`). The relevance predicate is quest-state (snapshot) × loot-chain
   (static data), not a perception field. Only the static NPC→item link (3475→4058 via pack 4530)
   must live somewhere — a named constant beside the selector (Q4-test-constant precedent), NOT a
   snapshot column.
3. **Explicitly NOT needed for G4:** skills/cooldowns (B-P2 — selection ends before cast),
   doodad phase, merchant/party/housing/vehicle state, nav state, zone (no zone gate on ItemGather;
   zone-group precedent exists at `LevelingLoopScenario.cs:2877-2882` if ever needed).

Top-3 observation gaps for G4: (1) bare objIds — no per-entity template/alive/hostile/distance,
every site re-resolves live; (2) no stealth filter in `Observe` — G4 must apply `CanSeeTarget`
itself; (3) no quest↔prey linkage surface — 251 names an item, not an NPC, so relevance is a
derived loot-chain predicate, not a readable flag.

---

## E. HOSTILE ELIGIBILITY — engine gates for Character-A-may-attack-B

### E.1 Authoritative owner: `BaseUnit.CanAttack` — REUSE (query, never duplicate)

`AAEmu.Game/Models/Game/Units/BaseUnit.cs:54-138`. Evaluated top-down, fail-closed where stated:

| Gate | Rule | Cite |
|---|---|---|
| Null-faction permissive | either `Faction == null` → `true` (bare fixtures read attackable) | `:56-57` |
| Self-exclusion | `ObjId ==` → `false` | `:58-59` |
| Relation | `GetRelationStateTo(target)` gates the rest | `:60` |
| Mother-zone protection | attacker is `Character`, victim `MotherId` matches zone faction → `false` | `:76-80` |
| PvP safe-zone / flag / ForceAttack / team | owner-flag, `IsActivelyHostile`, `AreTeamMembers`, `ForceAttack` on Friendly | `:82-104` |
| NPC safety | open `TODO: fix npc safety` — commented out, NO gate today | `:109-112` |
| Zone-conflict peace | `ZoneConflict.BlocksPvpDamage` shields non-hostile players in Peace zones | `:130-133` |

G4 reuses this by calling it (directly or via the wrappers below). It is the combat-legality half.

### E.2 Reusable wrappers (query) vs advisory duplicates (do not gate on)

- **REUSE — `CombatDecisionTree.IsHostileTarget`** (`CombatDecisionTree.cs:73-102`, public static):
  dead→false (`:79-80`); faction-115 wildlife→true (`:84-85`); null-faction→true (`:87-88`);
  else `CanAttack` + `RelationState.Hostile` (`:93-96`); exception→true (`:98-101`, headless fallback).
  Canonical hostility test by its own doc comment (`:64-72`).
- **REUSE-WITH-CLEANUP — roam `IsAttackableWildlife`** (`BotRoamStepExecutor.cs:2325-2348`, PRIVATE):
  same rule, same shape. G4 MUST NOT copy a third version — call the public `IsHostileTarget`
  (or promote the roam one; two copies already exist — duplication risk §G.1).
- **ADVISORY ONLY — `IsSkillInRangeAndReady`** (`CombatDecisionTree.cs:233-274`): cooldown
  (`:243-244`) + mana (`:252-253`) + effective range w/ weapon override (`:257-259`, override
  `:162-182`, zero-range default 4.0 m `:178-179`, fallback table `:187-228`). Duplicates the engine
  pipeline (gap-senses §2.2 item 1). The actor deliberately does NOT gate range/cooldown/mana —
  `Cast` gates only template-exists + learned + target-resolve (`GameplayActor.cs:576-594`) and lets
  `UseSkill` refuse post-`Start` (`:609-622`). G4 (ends at selected) uses distance for ORDERING only.
- **Actor dispatch gates G4 inherits for free (fail-closed, cite-not-copy):** `SetTarget` rejects
  unresolvable (`GameplayActor.cs:548-550`, resolve→assign→broadcast `:552-561`); `Cast` rejects
  unknown/unlearned/unresolved (`:576-594`); `AutoAttack` refuses null-or-dead (`:715-717`); `Talk`
  resolves live NPC + 25 m range (`:987-994`); `Interact`/`Harvest` resolve DOODADS only
  (`:1472,:1532`) — doodad-vs-unit separation is by kind-specific lookup, no shared flag.

### E.3 Gate-by-gate verdict for G4 selection

alive/dead — live `Hp > 0` / `IsDead`, query at select + revalidate at use (roam 30 s-cap precedent
`:722`; GOAP `TargetDead` flag `:216-221`). hostile/friendly — `IsHostileTarget`, query. faction —
inside `CanAttack`, query. PvE/PvP — same call covers both (NPC branch + owner branch); PvP-only
extra `IsAttackablePlayer` + `CanAttackPlayer` seam (`BotRoamStepExecutor.cs:2487-2496`) is out of
G4 scope (251 prey are NPCs). attackable — `CanAttack`, query. protected NPC — ONLY the mother-zone
rule (`:76-80`); no other protected flag found; NPC safe-zone TODO open (`:109-112`) → UNKNOWN beyond
that. doodad-vs-unit — kind-specific `GetNpc` (G4 prey path), reuse. range — advisory ordering only
(actor has no range gate). zone — no gate needed for ItemGather (zone-group precedent exists if ever:
`LevelingLoopScenario.cs:2877-2882`). stealth — MUST apply `CanSeeTarget` (only survey does today).
self-exclusion — `CanAttack` `:58-59` (+ survey `:263-264` convention).

### E.4 Separation (hard rule for G4)

- **Quest-relevance** (should this kill advance 251?): owner = quest state + static loot chain.
  Predicate: `ActiveQuestIds ∋ 251` AND `BagItemCounts(4058) < 3` AND `npc.TemplateId == 3475`.
  All snapshot or static. Decided WITHOUT touching combat state.
- **Combat-legality** (may A attack this B now?): owner = engine (`CanAttack` + alive + visible).
  Queried live per candidate. Never cached, never stored.
- A candidate is selectable iff relevant AND legal. The two halves share nothing but the objId;
  merging them into one flag is PROHIBITED (stored-flag staleness, nearby-quest §2.3 item 4).

---

## F. TRAVEL / TARGET REUSE — what `ArmQuestTravel` picks and what hunt code resolves

### F.1 `ArmQuestTravel` / `ResolveQuestTravelTarget` destination families

`BotRoamStepExecutor.cs:1271-1294` (arm; never re-arms over a live route `:1274-1275`;
`BotPath.PathTo` `:1289`) → `ResolveQuestTravelTarget` (`:1302-1358`):

| Destination family | Rule | G4 verdict |
|---|---|---|
| Ready-reporter spawner | Ready quest whose report-NPC is unspawned → `TrySpawnerPosition` (`:1312-1327,1385-1393`, first spawner match, no multi-spawner disambiguation) | REUSABLE-WITH-CLEANUP for giver/report staging only — not prey |
| Nearest in-band offerer spawner | only when ZERO active quests; world-wide spawner scan + `OffersWalkableQuest` (band `[1,9]` + `IsDiscoverable`, `:1370-1382`) → nearest (`:1335-1355`) | REUSABLE-WITH-CLEANUP for discovery staging — not prey |
| Kill-objective location / prey spawner / POI / nearest-template | NEVER produced — no branch resolves prey, loot sources, or template positions | HEURISTIC/UNKNOWN — G4 must not route hunt travel through this function |
| Active-but-not-Ready quest (251 in Progress) | falls through both branches → returns null (`:1357`) → "no walkable quest target" (`:1281`) | GAP — the exact 251-ACTIVE state arms NO travel today |

`OffersWalkableQuest` mirrors band + `IsDiscoverable` (`:1372-1379`; `IsDiscoverable` =
`DiscoverRejectReason == ""`, `:1422-1423`; reject codes `NO_TEMPLATE/ALREADY_ACTIVE/SUPPLY_BLOCKED/
REQ_FAIL_START_*/COMPLETED_NON_REPEATABLE`, `:1434-1454`). Third copy of "should we walk here"
(gap-senses §2.2 item 2) — G4 prey-travel must not inherit offer-band logic.

### F.2 Does existing hunt code already resolve quest-valid targets? NO (with two extractable yeses)

- **Roam wildlife hunt** (`:835-875`): nearest-attackable-ANYTHING, no template filter, no quest link.
  Kills whatever is hostile. FIXTURE-ONLY for G4 purposes (opportunistic, quest-blind). `IsAttackableWildlife`
  gate is real but quest-indifferent.
- **GOAP `AcquireHostileTargetAction`** (`CombatActions.cs:19-23`): dispatches `SetTarget(0)` —
  `ResolveUnit(0)` is always null (`GameplayActor.cs:4753-4760`) → always `RejectedAction`. A STUB
  masquerading as acquisition. DUPLICATE/fixture-only — never cite as existing resolution (§G.7).
- **REUSABLE-WITH-CLEANUP — `AdventurerSpikeScenario.SelectHostile`** (`:767-796`): observe-driven,
  template-filterable, alive + `CanAttack` + nearest. Exact G4 selector shape (scenario-local, rig-driven).
- **REUSABLE-WITH-CLEANUP — `LevelingLoopScenario.SelectHuntTarget`** (`:2842-2893`): same +
  group-match + zone-group gate + exclusion set. The most complete selector; G4 needs its template
  branch only (251 has no group/zone act).
- Net: no production path resolves a quest-valid kill target today (roam = quest-blind, GOAP = stub,
  scenarios = rig-driven). G4 introduces the first production quest-valid selector by extracting —
  not copying — the `SelectHuntTarget`/`SelectHostile` shape with a 251-relevance wrapper (§E.4).

### F.3 Classification summary

AUTHORITATIVE: `BaseUnit.CanAttack`, `ResolveUnit`/`GetNpc` fail-closed resolution, `IsDiscoverable`
mirror order, loot quest-gate (`LootPack.cs:195-201`), 25 m/45 m range constants.
REUSABLE-WITH-CLEANUP: `SelectHostile`, `SelectHuntTarget`, `IsHostileTarget`, reporter/offerer-spawner
travel (non-prey), `CanSeeTarget` stealth convention. FIXTURE-ONLY: roam quest-blind hunt acquisition,
GOAP acquire stub, bridge `drive`/`teleportToNpc` staging. HEURISTIC: nearest-among-legal ordering,
60-no-op Ready guard (`QuestBehavior.cs:77-87`). DUPLICATE: §G below. UNKNOWN: sqlite-direct rows
(no `compact.sqlite3` file in this checkout — all quest-data claims via the `251.json` manifest mirror
+ code-cited constants), live-lane 3512 materialization (nearby-quest §4.2), `TargetWildFarmPoiInvalidated`
writer (gap-senses B-P4, untouched).

---

## §4. 251's WORLD TARGETS (data read directly; sqlite absent → manifest + spawns + code)

### 4.1 Required target: Solzreed Boar template 3475 — via loot, NOT via a kill act

251 "화난 멧돼지들" (`251.json:1-7`, level 2, zone 125): Start = `QuestActConAcceptNpc` npc 3512
(comp 383, `:18-30`); **Progress = `QuestActObjItemGather` item 4058 ×3** (comp 633, act 10473,
`251.json:31-43`); Ready = `QuestActConReportNpc` npc 3512 (comp 638, act 457/detail 104,
`:45-57`); Reward = item 18791 ×1 (`:58-72`). There is NO `MonsterHunt`/`MonsterGroupHunt` act —
the kill link is indirect: **Solzreed Boar 3475** (`Creatures.xml:3014`) → **loot pack 4530** →
**boar meat 4058**, generated only while the killer holds the quest (`LootPack.cs:145-147,195-201`;
`LootingContainer.cs:414-420`; Q4 canonical triple template/pack/meat at
`Q4LiveHuntLootE2eTests.cs:37-39`; Q4 "holds 251 so pack 4530 generates the meat", `:24-26,84-97`).
Exact `loot_quest_id = 251` row value: [INFERENCE] from the Q4 gate description — sqlite not in
checkout (UNKNOWN direct). 251.json `letItDone: false` (`:7`) confirms real objective work is required.

### 4.2 Spawners and giver geography

- **Giver 3512 "Mayor Gott"/Baragi recruiter: ONE spawner** in `main_world/npc_spawns.json:184418-184424`
  at **(15655.91, 15172.51, Z 121.24)** — no multi-spawner ambiguity (nearby-quest §4.2 concurring).
  DISCREPANCY (flag, not resolved): `SolzreedStarterCorridorProfile.BaragiVillageNpcPosition` cites
  (15320.1, 15024.4) for NPC 3512 (`SolzreedStarterCorridorProfile.cs:30-31`) — ~367 m from the spawner-file
  position. One of the two is stale; spawner file governs materialization. Starter spawn itself is
  (15578.0, 15382.1) (`:22`) — ~223 m from the spawner-file giver.
- **3475 near-cluster: FIVE static spawners** in the same file (`:183347-183386`, contiguous block —
  exactly 5, next entry is 3476) at (15586.55,15117.35), (15632.18,15117.80), (15624.56,15119.46),
  (15687.09,15011.30), (15616.46,14919.13). Flat distances from giver 3512: **~89 m, ~60 m, ~62 m,
  ~164 m, ~256 m** — nearest boar is ~60 m SE of the giver, all five S/SE within ~260 m.
- **Same-template unrelated mobs elsewhere: YES — NINE more 3475** in
  `main_world/npc_spawns_solzreed_wildlife.json` (ids 981002–981034, e.g. `:18-31,:78-91,:138-…`),
  clustered ~(14445-14491, 14389-14414) — **~1.4 km WSW of the giver**. Both files load: the loader
  globs `npc_spawns*.json` (`SpawnManager.cs:331-333`), so all 14 are live spawners. Wildlife entries
  carry `"RespawnTime": 15` (`:29`); main-file respawn fields not re-grounded this pass.
  Consequence for G4: template match alone is insufficient — selection MUST prefer the near-giver
  cluster (nearest-legal ordering does this for free when staged at 3512; a bot staged at the far
  ground would validly pick far boars — same credit, since ItemGather has no zone gate [INFERENCE]).
- **Presence near start:** staged-at-3512 ⇒ nearest prey ~60 m — outside the 25 m discovery/Observe
  radius (`GameplayActor.cs:262-264,1461`) and just outside the 45 m hunt radius (`:108-110`); G4
  selection from the giver doorstep sees NOTHING without a ~60 m+ close-in first (travel or re-stage).
  Starter-spawn ⇒ nearest boar ~270 m. No prey is observable from either staging point at rest.
- **Dynamic spawns:** none found — all 14 are static spawner entries; no event/dynamic/sphere spawn
  references 3475 in audited files. Respawn timing beyond the wildlife file's 15 s is UNKNOWN.

---

## G. DUPLICATION RISKS — quest/objective/target/combat logic hiding spots

| # | Location (owner) | What's hidden there | Risk if G4 copies instead of reusing |
|---|---|---|---|
| G.1 | `BotRoamStepExecutor.IsAttackableWildlife` (roam executor, `:2325-2348`, private) vs `CombatDecisionTree.IsHostileTarget` (`:73-102`, public) | SAME hostility rule twice (115/null-faction/`CanAttack`/Hostile) | third copy drifts on the next faction/safety change — G4 calls the public one |
| G.2 | Hunt acquisition ×4: roam scan (`:835-875`), `SelectHostile` (`AdventurerSpikeScenario.cs:767-796`), `SelectHuntTarget` (`LevelingLoopScenario.cs:2842-2893`), GOAP acquire stub (`CombatActions.cs:19-23`) | four hostile-selection implementations, one a guaranteed-reject stub | G4 becomes the fifth — extract ONE selector, delete nothing (G3 frozen, scenarios rig-owned) |
| G.3 | Sweep ×3: `QuestBehavior` provider sweep (`QuestBehavior.cs:112-157`) vs ignored `NearbyNpcObjIds` vs `QuestDecisionScenario` sweep (`QuestDecisionScenario.cs:125-132` per nearby-quest §2.2) | three NPC-neighborhood scans per wake family | G4 adds a fourth scan — consume `observation.NearbyNpcObjIds` like the spike/loop selectors do |
| G.4 | `IsSkillInRangeAndReady` (`CombatDecisionTree.cs:233-274`) vs engine `UseSkill` truth | cooldown/mana/range evaluated behavior-side AND engine-side (gap-senses §2.2 item 1) | G4 gates legality on the advisory copy — order by it at most, never reject by it |
| G.5 | `OffersWalkableQuest` (`:1370-1382`) vs `IsDiscoverable` (`:1422-1423`) vs scenario band filter (`QuestDecisionScenario.cs:40-46` defaults) | "should we walk here" three times, offer-shaped | G4 prey-travel inherits offer-band logic that returns null for active quests (§F.1 gap) |
| G.6 | GOAP threat flags (`BotWorldStateProvider.cs:200-222`) vs roam `isDeadOrInvalid` (`:719-722`) | target-validity (alive/hostile/range/dead) projected twice, different shapes | G4 revalidation invents a third shape — follow the roam predicate (it owns live legs) |
| G.7 | GOAP `AcquireHostileTargetAction.CreateActorRequest` → `SetTarget(0)` (`CombatActions.cs:19-23`) | dead path: `ResolveUnit(0)` null → always `Rejected` (`GameplayActor.cs:4753-4760`) | citing it as "existing hunt resolution" or wiring G4 through it — it can never land |
| G.8 | `QuestBehavior` header claim (`QuestBehavior.cs:26-28`): "objective pursuit (kill, gather, talk) rides the existing hunt/interact branches" | kill-pursuit is delegated by comment to the quest-BLIND roam hunt (§F.2) — no quest→prey link exists behind the sentence | G4 trusts the comment and reuses roam hunt as "quest pursuit" — it will kill foxes (3492) as happily as boars (3475) |
| G.9 | `BotWorldStateProvider` bag/housing/CurrentTarget live scans (`BotWorldStateProvider.cs:94-101,152-177,201-222`) bypassing `ActorObservation` (brain-boundaries §2.1) | GOAP senses outside the canonical snapshot | G4 reads vitals/threat anywhere but the snapshot — same bypass, new caller |
| G.10 | Turn-in reporter `GetNpcByTemplateId` world-wide first-match (`QuestBehavior.cs:210` per nearby-quest §1.3 item 2; `ResolveQuestTravelTarget` `:1323`, `TrySpawnerPosition` `:1387-1389`) | world-wide, order-unspecified, no range check | G4 resolves "the" boar by template world-wide — 1.4 km-away instance wins by enumeration luck; nearest-ordering is load-bearing, not cosmetic |

G4 reuse order (cheapest first): call `IsHostileTarget` (G.1) → extract `SelectHuntTarget`-shaped
selector with 3475 filter + `CanSeeTarget` (G.2/G.3) → relevance predicate from snapshot fields
(§E.4) → nearest-legal ordering (G.10) → never touch travel, GOAP acquire, or skill-readiness (G.5/G.7/G.4).
