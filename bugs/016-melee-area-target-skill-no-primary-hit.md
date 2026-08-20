# BUG-016 — Melee combo skills with target_area_radius + TargetSelection=Target never damage their primary target (skill 18131 confirmed)

**Status:** OPEN (found 2026-08-20 by the M7 adventurer-spike E2E; worked
around in the spike rotation — engine fix or data review pending)
**Severity:** MODERATE (skills execute, consume mana/gcd, play effects — but
deal 0 damage to the intended target; silent combat no-op)
**Affected:** confirmed: skill 18131 (3단 베기 hit 1 — Fight tree opener).
Suspected class: any melee skill with `target_area_radius > 0` AND
`TargetSelection=Target` — census pending.

## Symptom

During the M7 adventurer-spike E2E (`adventurer-spike-fox`, evidence
/root/aaemu-e2e/logs/m7-adventurer-spike-report.json), a level-50 bot cast
skill 18131 at foxes (npc 3492): **150/150 casts succeeded
(SkillResult.Success), 0 damage dealt** to the target. Verified with
temporary instrumentation (target Hp sampled pre/post cast; since reverted
byte-clean). Rotation fallback 18134 (finisher, `target_area_radius=0`)
damages normally.

## Root cause (file:line — from instrumentation)

`Skill.ApplyEffects` takes its AoE branch when `target_area_radius > 0`
(2 for 18131) and builds the effect-target list from
`WorldManager.GetAround(targetSelf, …)` around the target — **which excludes
the center object itself**. With `TargetSelection=Target` the caster's
selected target IS the center object, so the primary target is never in the
effect list. Area-0 skills hit the direct-target branch and work.

## Fix direction (not yet implemented)

Either engine-side (include the center object in the effect-target list when
`TargetSelection=Target`, or treat area+Target skills as primary-target +
splash) or data-side (review area values on single-target melee combo
skills). Needs the suspected-class census first (query compact.sqlite3
skills for `target_area_radius > 0` + Target selection on melee combo
chains) — the M7 combat scheduler work should not land before this is
understood, or rotations will silently no-op.

## Repro

E2E stack: provision a Fight-tree bot with real start skills, SetTarget a
hostile, Cast(18131, target) — cast completes, target Hp unchanged. Spike
scenario `adventurer-spike-fox` leads with 18134 for exactly this reason.
