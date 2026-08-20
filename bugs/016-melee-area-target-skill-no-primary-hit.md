# BUG-016 — Melee combo skills with target_area_radius + TargetSelection=Target never damage their primary target (skill 18131 confirmed)

**Status:** FIXED (branch `fix/bug-016-area-target-primary`, 2026-08-20)
**Severity:** MODERATE (skills execute, consume mana/gcd, play effects — but
deal 0 damage to the intended target; silent combat no-op)
**Affected:** confirmed live: skill 18131 (3단 베기 hit 1 — Fight tree
opener). Census on canonical compact.sqlite3 (`skills`: `target_area_radius
> 0 AND target_selection_id = 2` — Target): **415 skills** in the class
(damage_type breakdown: none=157, melee=75, magic=88, ranged=58, siege=37),
of which **13 player-learnable** (ability_id != 0): 10664, 11933, 12794,
13281, 13286, 18131, 23587, 23588, 23593, 23646, 23647, 23648, 23649. The
157 no-damage-type rows mean the gap also stripped BUFFS/DEBUFFS from
primary targets, not just damage.

## Symptom

During the M7 adventurer-spike E2E (`adventurer-spike-fox`, evidence
/root/aaemu-e2e/logs/m7-adventurer-spike-report.json), a level-50 bot cast
skill 18131 at foxes (npc 3492): **150/150 casts succeeded
(SkillResult.Success), 0 damage dealt** to the target. Verified with
temporary instrumentation (target Hp sampled pre/post cast; reverted
byte-clean after diagnosis).

## Root cause (file:line)

`Skill.ApplyEffects` (Skill.cs:934-939): the `TargetAreaRadius > 0` branch
built its target list from `WorldManager.GetAround(targetSelf, radius)` —
**which excludes the center object** (Region.GetList skips the query
objId) — and only re-added `targetSelf` for `TargetSelection == Source`.
With `TargetSelection == Target` the caster's selected target IS the center
object, so the primary target was never in the effect list. Area-0 skills
hit the direct-target branch and worked.

## Fix

`Skill.cs` AoE branch: re-add `targetSelf` for BOTH
`SkillTargetSelection.Source or SkillTargetSelection.Target`. Safety:
`FilterAoeUnits` applies the template's relation filter afterwards (no
friendly-fire), the later `Distinct()` dedupes (no double application), and
the `TargetAreaCount` cap orders by distance to targetSelf — the re-added
primary is distance 0, so it is always kept when the cap truncates
(semantics change on the record: the primary now COUNTS toward the cap,
matching retail behavior of "primary + splashed").

**Out of scope (recorded):** `SkillTargetSelection.Line` and `Location` —
their `targetSelf` semantics differ (a position/line, not necessarily the
intended unit); no evidence of the same defect there.

## Evidence

- Hermetic rig (TUnit): `SkillAreaTargetPrimaryTests`
  (AAEmu.UnitTests/Game/Models/Game/Skills/) — a recording-effect skill
  (radius 2, Target selection, Hostile relation) proves the primary target
  AND an in-radius neighbor receive the effect while an out-of-range unit
  does not (pre-fix the primary was never in the list); a Source-selection
  regression guard proves self-centered AoE still includes the caster.
  Rig notes: rig NPCs need direct Region membership (no
  AddVisibleObject/SCUnitStatePacket path) and a minimal NpcTemplate
  (useModelSize reads Scale).
- Live: the adventurer-spike rotation leads with 18131 again, so every
  `adventurer-spike-fox` E2E run regression-covers the fix with real
  damage on real foxes. Post-fix live evidence: run-11 (18131-only
  rotation, pre-chain) killed 2 foxes with 18131 alone before mana
  exhaustion — pre-fix 18131 did 0 damage in 150 casts; run-12
  (combo-chain rotation, both hits per round) PASS 1/1 in 2m08s, 3/3
  kills in 6 casts per fox.
- Rotation semantics note (spike follow-through): the hunt loop now casts
  the FULL chain per burst round instead of stopping at the first accepted
  skill — combo openers alone can't outpace leash-reset healing (run-11
  fail: 2/3 kills then LackMana).

## Repro (pre-fix)

E2E stack: provision a Fight-tree bot with real start skills, SetTarget a
hostile, Cast(18131, target) — cast completes, target Hp unchanged.
