# Deferred — NOT NOW register

Exploration record. Deprioritized items are preserved here, not lost.
Each entry carries its **reason** for deferral and its **exact revisit trigger**.
Nothing in this file is scheduled work; promotion back to an active lane
requires its trigger to fire.

- Convention: `NOT NOW` = deliberately parked. `NO-GO` = do not attempt
  without the stated evidence first.
- Related: `dropped-content-register.md` (content cut by data/design, not
  scheduling), `progression-board.md` (active lanes).

## 1. Q8 WAR-HONOR
- **Status:** NOT NOW — deferred by owner.
- **Reason:** Owner deprioritized; no active lane owns it.
- **Revisit trigger:** Owner lifts the deferral explicitly. Nothing else
  promotes it.

## 2. English-DB second server
- **Status:** NOT NOW — held.
- **Reason:** Marginal benefit: affects logs/GM tooling only; no client-text
  change. Cost of a second server (config, content drift, upkeep) exceeds
  the payoff today.
- **Revisit trigger:** Korean-glyph pain bites real debugging — i.e. a
  concrete case where unreadable Korean text materially slows or blocks a
  live debug session.
- **Note:** `LocalizationManager` runtime alternative exists (translate at
  display/log time instead of a second server). Prefer that shape if the
  trigger fires.

## 3. Navmesh full-port recorder
- **Status:** NO-GO.
- **Reason:** Full port rejected on cost/risk; no demonstrated need.
- **Revisit trigger:** ONLY on a demonstrated 1.2 cave-float repro — a
  reproducible in-game case of floating/pathing failure inside a 1.2 cave
  that a recorder would resolve.
- **Reference:** Read-only slice proposal at `/tmp/navmesh-teardown.md`.
  Do not expand scope beyond that slice without the trigger.

## 4. A5 12-hour soak
- **Status:** NOT NOW — recommendation-only.
- **Reason:** Long soak proposed as assurance, not tied to a live defect or
  gate.
- **Revisit trigger:** A soak trigger fires — e.g. a stability/memory
  regression, a release-gate requirement, or owner request naming the
  soak as an entry condition.

## 5. Quest-sphere user config step
- **Status:** NOT NOW — needs user machine action.
- **Reason:** Cannot proceed server-side; blocked on the user's machine.
- **Revisit trigger:** User completes the action, then the item returns as
  ready work.
- **Action required (user machine):**
  1. Move `/tmp/quest-sphere-clientdata` to a durable path.
  2. Apply the `Config.Local.json` snippet pointing `ClientData.Sources`
     at that durable path (machine-specific — never shared config).

## 6. H human acceptance
- **Status:** NOT NOW — owner-verdict only by definition.
- **Reason:** Only Josh performs the human verdict; contributors may prepare and
  support it. Its timing does not freeze bot/engineering lanes.
- **Revisit trigger:** Owner performs acceptance. There is no proxy trigger.
- Per-gate UNKNOWN vs recorded DEFERRED distinctions stand; this register does
  not convert all H cells to DEFERRED.

## 7. Phase-2 clamp-beyond-verified + Mira 9632 follow-up
- **Status:** NOT NOW — needs in-game proof.
- **Reason:** Extending the clamp beyond verified bounds (and the Mira 9632
  follow-up) has no standing without observed in-game evidence.
- **Revisit trigger:** In-game proof lands — a reproducible live observation
  (positions, zone, steps) justifying the wider clamp / Mira follow-up.

## 8. Stuck-rider / mate-dragons / well-gather
- **Stuck-rider:** NOT NOW — deferred pending live repro. **Revisit
  trigger:** a reproducible live stuck-rider case.
- **Mate-dragons:** NOT NOW — incompatible (does not fit current
  constraints). **Revisit trigger:** the incompatibility is removed or a
  compatible shape is proposed; until then it stays parked.
- **Well-gather:** NOT NOW — covered-code (existing coverage already
  exercises the path). **Revisit trigger:** a gap in that coverage is
  demonstrated (failing case the current code does not handle).

## 9. DB-volume gate
- **Status:** NOT NOW — deferred pending true shared execution boundary.
- **Reason:** No agreed shared execution boundary exists on which to hang
  a volume gate; gating now would be arbitrary.
- **Revisit trigger:** A true shared execution boundary is defined and
  adopted — then design the gate against it.
- **Note:** Statement counters live on as volume-only telemetry (signal,
  not a gate).

## 10. M10 slice-2 combat-lite, expansion candidates, census U-rows
- **Status:** NOT NOW — unshaped future.
- **Reason:** Ideas without shaping: no spec, no acceptance, no owning lane.
- **Revisit trigger:** Each promotes individually once shaped — i.e. given a
  concrete proposal with scope and acceptance criteria that an owner
  accepts onto a lane.

## 11. MySQL binary-prepare driver defect
- **Status:** SETTLED WORKAROUND, NO UPSTREAM FILING (fork direction:
  upstream intake-only; playerbots divergence).
- **Reason:** MySql.Data 9.7.0 PreparableStatement.Execute() NREs
  client-side between Prepare-response and Execute-send under parallel
  boot; no known issue matches (closest #116257 excluded: needs OTel +
  second-Execute), no fixed version (checked through 26.7.0); workaround
  = deleted explicit Prepare() calls (text protocol), proven by A/B
  (revert = fatal boot defect).
- **Revisit trigger:** ONLY if text-protocol shows measured regression
  (save-p95/tick budgets) or a newer driver line documents a
  prepared-statement race fix — then re-evaluate, never blindly upgrade.
- **Reference:** `/tmp/driver-research-tail.md` (outside repo; not copied
  in).

## 12. PlayerBot Farming Loop Wait Alternatives (Options 2 & 3)
- **Status:** NOT NOW — Option 1 (leashed homestead leisure/micro-wander) implemented and active.
- **Reason:** Option 1 resolved the immediate continental roaming/mob engagement while preserving organic bot presence around the crop. Options 2 and 3 are reserved for future bot personality / errand progression.
- **Option 2 (Village Errands with Recall Timer):** Bots embark on nearby village tasks (merchant restock, well-gathering, village patrol) during long crop growth windows, with a scheduled recall timer to return when crops mature.
- **Option 3 (Distinct Bot Personalities / Archetypes):** Differentiated bot archetypes configured via profile/weights — e.g. dedicated farmer archetype (spends entire day tending and loitering at the homestead) vs. adventurer/hybrid archetype (plants crops as a side activity and undertakes regional travels before returning).
- **Revisit trigger:** Owner requests multi-role bot diversity or larger town activity loops beyond the farm perimeter.

## 13. Autonomous Quest Multi-Objective Progression & Branching
- **Status:** NOT NOW — basic quest accept and turn-in (M5.3) is verified and closed; multi-objective quest progression is parked.
- **Reason:** Single-objective and delivery quests are proven. Multi-stage quests with sequential or branched objectives (e.g. kill N mobs -> collect drops -> trigger world doodad -> turn in) require a quest objective DAG resolver in `BotRoamStepExecutor` / `LevelingLoopScenario`.
- **Revisit trigger:** Leveling progression moves into multi-objective quest zones (e.g. Arcum Iris / Solzreed quest chains beyond initial intro) or owner completes trace `quest_multi_objective_progression`.

## 14. Out-of-Combat Food & Potion Resource Recovery Loop
- **Status:** NOT NOW — passive health and mana regeneration is active; automated consumable usage is parked.
- **Reason:** Bots currently replenish health and mana through natural regen between roaming cycles. Active food/soup and potion consumption requires inventory stock monitoring and item cooldown checks in `GameplayActor` and `CombatDecisionTree`.
- **Revisit trigger:** High-density mob grinding causes excessive bot downtime or player initiates sustain-heavy leveling tests, or trace `combat_food_potion_recovery` is collected.

## 15. Combat Knockback / Whirlwind Reactive Spacing & Ranged Weapon Swapping
- **Status:** NOT NOW — range-gated melee combos and continuous auto-attacks are active.
- **Reason:** Bots currently handle distance-based skill selection at pull time. Dynamic reactive spacing (detecting when an enemy mob's whirlwind or knockback launches the bot into the air or separates it >4m, and switching to bow/ranged skills while gap closers are on cooldown) is an enhancement.
- **Revisit trigger:** Mobs with whirlwind/knockback abilities cause observable combat stalls, or trace `combat_knockback_ranged_fallback` is captured.

## 16. Mount Summon, Waypoint Riding & Dismount Loop
- **Status:** NOT NOW — on-foot navigation, roaming, and sprint are active.
- **Reason:** Mount item casting (`Skill 10602`) and riding mechanics exist in engine, but autonomous bot pathfinding while mounted (handling companion unit mounting, speed adjustments, and auto-dismounting at waypoints) is deprioritized until long-distance regional travel is required.
- **Revisit trigger:** Bot travel distances between waypoints exceed 100m, making on-foot travel a primary bottleneck, or trace `travel_mount_and_ride` is captured.

## 17. Multi-Bot Party Coordination & Assist Targeting
- **Status:** NOT NOW — solo bot roaming and combat decision trees are complete.
- **Reason:** Multi-bot party formations (following party leader, target assisting via `CSChangeTargetPacket`, and chaining complementary crowd-control / combo skills such as Tank Stun -> Rogue Backstab) require inter-bot message passing.
- **Revisit trigger:** Group dungeon content (e.g. Sharpwind Mines / Palace Cellar) or party testing lane is initiated, or trace `bot_party_combat_assist` is captured.

## 18. PlayerBot Roster Manifest Generation & 120-Class Diversity
- **Status:** NOT NOW — individual bot spawning (`/bot add`, `/bot here`) and persistence (`/bot restore`) are closed.
- **Reason:** Automated batch population generation with randomized visual presets, racial factions, and class archetypes across all 10 ArcheAge ability trees (120 classes) is reserved for population scaling.
- **Revisit trigger:** Population scale benchmarks requiring 50+ diverse bots across starter zones, or owner requests automated world population fill.

