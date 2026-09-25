# G6 Closeout Freeze — PASS-BEHAVIOR (2026-09-20, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (same lane family as G3/G4/G5).
This is a documentation/reconciliation/freeze record. Zero behavior change in this pass
(no game source, tests, fixtures, or lane state touched; lane evidence read-only).

## (a) Final status

- **Status: PASS-BEHAVIOR (frozen).** Date: 2026-09-20. charId=41.
- **Evidence artifact:** `/root/aaemu-e2e-q0/logs/g6-first-combat-report.json`
  (`scenario=g6-first-combat-settled-start`, `verdict=PASS-BEHAVIOR`, `failBoundary=none`).
- **PASS-BEHAVIOR chain fields:** selected=37267 template=3475 @ endDist 1.2 m
  (started settled inside the 3.0 m hold); decide history landed
  `Target (targeting 37267)` → `Move` → `Stop (stopped)` → `AutoAttack
  (auto-attack started (2) on 37267)` with terminal Completed; loop live
  (`loopLive=True`) + target pinned (`targetPinned=True`); HP 286→259
  (`hpBeforeCombat=286`, `hpMinPost=259`, trailing-wake damage, `preyDied=False`,
  `preyAliveEnd=True`); side-effect zeros (meat 0→0, money/credit delta 0,
  `turnIns=0`, `questStillActive=True`, quest still Progress); exclusion scan
  clean (`castLootHits=NONE`, ~1 AutoAttack landing, zero Cast/Loot).
- **Code changes (already landed on develop, cited read-only — not made here):**
  `AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs` — `CombatGoal =
  "quest.objective-combat"` (:80-81); hold-confirm wake cache (:83-88);
  combat proposal construction additive in the 251 path (:148-159);
  `CombatProposal` (:677-748, priority `opts.ObjectiveCombatPriority` at :747,
  idempotency `quest:{actor}:{cycle}:combat:251:{target}` at :743);
  Dispatch arm `ActorActionType.AutoAttack when proposal.Goal == CombatGoal =>
  gameplayActor.AutoAttack(...)` (:816-821); `LogCombatOutcome` diagnostics
  (:319-320, :885-895); funnel combat column in `LogObjectiveFunnel` (:766-788).
  `AAEmu.Game/Core/Managers/Bots/QuestDecisionScenario.cs` —
  `ObjectiveCombatPriority = 23` (:65), ordered Target 25 > pursuit 24 >
  combat 23 > advance 20 (:53-66).

## (b) Frozen contract (decision → AutoAttack → loop live → HP delta, stop there)

From 251-ACTIVE + G4-selected live 3475 + flat ≤ 3.0 m + settled, autonomy
(QuestBehavior combat proposal at priority 23) dispatches `AutoAttack(selected)`
on the live actor → terminal `Completed("auto-attack started (2) on {target}")`
→ loop live (`IsAutoAttack`, `AutoAttackTask` non-null) → target pinned
(`CurrentTarget == selected`) → zero `Rejected` on the dispatch → trailing-wake
HP fall (286→259) observed, prey still alive, meat unchanged, quest Progress.
Damage is a trailing observation (spike/Q4 hpBefore→hpAfter idiom), NOT the gate
predicate — the gate predicate is loop-started + pinned. G6 stops there.

## (c) Proves / does-NOT-prove boundary

Proves:
- A quest-owned combat proposal (priority 23, below pursuit 24 so range-hold
  wins while closing/holding, above advance 20) fires once the G5 Stop leg
  hold-confirms and withdraws on a settled wake — same selected objId, no
  recompeting, per-wake live revalidation (resolve + alive + `IsHostileTarget`
  + `CanAttack` + visible + assigned + in-range).
- The dispatch rides the live actor's `AutoAttack` verb only (no queue kind
  exists for it by design — roam hunt-leg shape); the terminal means
  "loop started," and the loop is confirmed live with the target pinned.
- First damage lands on attack-delay ticks after the terminal (HP 286→259 across
  trailing wakes) with the prey alive and zero side effects — the
  Completed-means-loop-started-not-damage semantics in action.

Does-NOT-prove (explicitly out of scope, no claim):
- Rotation or skill choice (no `Cast`, no rotation contents; skill 2 vs 4 is
  engine-mediated by role/weapon/distance, not decided here).
- Combat to death, corpse handling, loot, 4058 credit, Ready transition,
  return travel, `Talk`, turn-in, rewards, persistence, multi-bot scale.
- Any quest other than 251; any target other than the G4-selected 3475.

## (d) Verification performed in this pass (read-only, no tests run)

- Report↔claim: selected 37267 / template 3475 / endDist 1.2 / HP 286→259 /
  prey alive / meat 0→0 / quest Progress / turnIns 0 — all match the artifact's
  `failingCondition` + evidence trail. PASS.
- Implementation↔plan: AutoAttack only (Dispatch :821, "No Cast, no rotation,
  no Loot, no credit — those are G7"); priority 23 below pursuit 24, above
  advance 20 (QuestDecisionScenario :53-66); `IsHostileTarget` + `CanAttack`
  reused, never duplicated (QuestBehavior :704-705). PASS.
- G3/G4/G5 test files untouched: `git status --porcelain` shows no modified
  (`M`) entries — the mechanics dir has untracked doc files only, zero
  modifications to tracked test files. PASS.
- No kill/loot/credit/turn-in code added for G6: Dispatch switch carries only
  the pre-existing TurnIn arms plus the G4 Target, G5 Move/Stop, and G6
  AutoAttack arms; grep over QuestBehavior.cs finds no `Loot(`/`Cast(`/
  credit/`OnItemGather` on the combat path. PASS.
- No doc labels G6 unproven: grep over the mechanics dir finds no
  G6-unproven/NOT-PROVEN/TODO label; G5 §g's "no damage reasoning belongs in
  G6's opening pass" is G6's incoming scope question, now answered — left as
  historical record, no reconciling edit needed. PASS (no edit made).

## (e) Lessons (reusable E2E doctrine, G6 additions)

1. **Combat-proposal ownership:** the quest-valid combat owner is QuestBehavior
   composing the roam hunt shape (`AutoAttack` first, casts layered later) with
   the G4 selector — GOAP acquire stays a dead stub, the tree advises but never
   dispatches, scenarios demonstrate but never autonomize.
2. **Eligibility-vs-legality split:** relevance (`IsRelevant`: 251 active +
   4058 < 3 + template 3475) shares nothing with legality (alive/hostile/
   attackable/visible) but the objId; merging into one flag is PROHIBITED.
3. **Completed-means-loop-started-not-damage:** the `AutoAttack` terminal
   promises the loop, not the wound; assert loop-live + pinned at the gate
   instant, read HP on trailing wakes.
4. **Trailing-damage proof pattern:** hpBefore (proposal time) → hpAfter
   (post-dispatch + subsequent wakes) with the prey alive and exclusion scan
   clean is the honest first-combat proof — it decouples the dispatch clock
   from the attack-delay clock.
5. **Zero-side-effect gate shape:** meat/money/credit/turn-in zeros plus
   quest-still-Progress at the gate instant is what keeps a combat gate from
   silently becoming a kill/loot/credit gate.

## (f) Freeze rule

G6 is FROZEN at PASS-BEHAVIOR. No new verbs, no rotation, no kill/loot/credit/
turn-in wiring on the G6 path. Regressions may REPAIR ONLY: a regression
restores the frozen chain (23-proposal → AutoAttack → Completed-started →
loop live + pinned → trailing HP fall, zeros held) without widening scope;
any widening (rotation, kill, loot, credit, turn-in) is a new gate (G7) with
its own fixture, not a G6 follow-on.

## (g) Architectural debt (follow-up ONLY — recorded, untouched, not G6 work)

- Rotation / skill readiness: which `Cast` rotation is legal at production
  level (learned set, cooldown/GCD perception) is still UNKNOWN — G6 opens
  with AutoAttack precisely to avoid depending on it.
- Cooldown/GCD perception: `IsSkillInRangeAndReady`/`SelectPrioritizedSkill`
  need a loaded `SkillManager`; actor-Cast bypasses GCD while AutoAttack
  respects it — convergence deferred to a future bounded executor.
- Ranged roles: RangedPhysical starters pick skill 4 (4.0 m minimum) vs the
  3.0 m hold → possible `TooCloseRange`; fixture pinned a melee (Fight) bot.
- Model radius: boar-3475 radius vs flat-3.0 m hold may disagree ~1 m; engine
  refusal is ground truth, not the funnel number.
- Leash/DPS: boar leash-reset vs level-10 DPS may cap future gates at
  first-damage (spike needed Level 50); fixture concession, not scope merge.
- Combat teardown ownership: 30 s engagement cap + `StopAutoAttack` +
  target-clear shape exists in roam; quest-side teardown (incl.
  `HasLootedCurrentTarget`/`HasCorpseToLoot`/`TargetDead` seam) is G7's.
- Kill/loot/credit pipeline: loot → `OnItemGather` 4058×3 → Ready is untouched
  by construction; `OutOfCombatRecoveryModule` (priority 85) may preempt
  post-fight wakes — observe, never disable.

## (h) G7 handoff

Starting state: quest 251 ACTIVE with a damaging-but-alive 3475 target under a
live quest-owned AutoAttack loop (this freeze's end state). Question: from that
loop, sustain to kill → observe death via victim HP (killer target-clear as
corroboration) → loot the corpse → 4058×3 credit → Ready — each as its own
bounded gate (kill / loot / credit), with a G7 discovery pass first to name the
rotation, teardown owner, and fixture level before any implementation.
