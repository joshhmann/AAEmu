# Activity-Spine Campaign (PlayerBot loop proof)

> Audience: contributors + agents. Last-verified: policy-reviewed 2026-10-10 at `fix/npc-spawner-population-count` / `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus inherited dirty work (unmerged; documentation only, no fresh gameplay verification).

Status: DESIGN + OPEN QUESTIONS. No lane verdicts yet — claims no live results.
Canonical record: [E-22](../../.omp/state/EVIDENCE.md).
Current applicability: this remains an **unratified proposal**, not a runtime
allocation. Its dated instance statuses and campaign-specific exits below are
historical design context. Bot scope follows the [supported-capability contract](../../PROJECT-CONTROL.md#playerbot-supported-capability-contract);
quest/farm/trade instances validate reusable capabilities, not the whole project
objective. See [current priority/allocation limits](../../STATUS.md#current-playerbot-policy-checkpoint).

This campaign proves **PlayerBot loops compose**, not that verbs fire.
Verbs were proven in isolation (scripted); the open question is composition:
can the bot chain observe→decide→act→recover across a full behavior loop
with no quest-specific code? Quests are instances of shapes, never targets.

## Shapes (not quests)

The director derives ~6 patterns from objective acts (`QuestDirector.cs:266`)
— KillX, GatherDoodad, InteractDoodad, Deliver, UseItem, Unknown — and runs
the *shape's* leg. Thousands of quests collapse into a handful of legs.
Coverage is per-shape on unseen instances, never per-quest checkboxes:

| Shape | Status | Instance |
|---|---|---|
| KillX/gather-via-kill | partial (251: all 4 bots DB-verified across runs, best single run 2/4, no 4/4 — [E-17](../../.omp/state/EVIDENCE.md)) | prey 3475 |
| Deliver/talk | in flight | quest 256 (accept 3516 → report 7651) |
| InteractDoodad | unproven | — |
| UseItem | unproven | — |

## Legs (loop → instance)

1. **Dialogue/delivery loop** — instance: quest 256 talk-only. Primitives:
   travel, talk/interact, observe, turn-in. Live run in flight.
2. **Grow-harvest loop** — instance: potato two-harvest. Primitives: travel,
   interact, maturity-wait, replant. Blocked-target + interruption recovery.
   Scoped, not yet run.
3. **Carry-trade loop** — instance: pack 26489 sale. Primitives: craft,
   carry, drive, sell, ledger reconcile + kill-9 restart. Scoped, not run.

## Rules (all legs)
- Natural start state; after START: no teleport/force-state/stocked items/
  event injection/manual play. Read-only diagnostics allowed.
- First-failure format: OBSERVED / FIRST FAILED PREDICATE / OWNER /
  ROOT CAUSE / SMALLEST FIX, classified (WORLD_STATE, NAVIGATION,
  QUEST_INTERPRETATION, INTERACTION, COMBAT, LOOT, RECOVERY, PERSISTENCE,
  CONTROL_PLANE, HARNESS, OTHER).
- DB proof required per leg; bridge must not game-play on the bot's behalf.
- One owned isolated lane at a time; owned-only teardown, verified clean.
- No quest-id-specific production code for any instance, ever.

## Open questions
- Delivery-path behavior end to end (leg-1 verdict pending)
- Maturity-wait vs patrol fallthrough (PB-FARM-01)
- Conservation across kill-9 restart (mail/pack/ledger)
- TakeMail actor action: add or prove unnecessary?

## Exit (all REQUIRED)
- One unseen instance per covered shape, no new quest-id constants in prod
- Second harvest from replant; pack payout formula + labor −60 + no dupe
- Intentional failure recovered per leg; H stays UNKNOWN

## Related
- [E-22](../../.omp/state/EVIDENCE.md) (spine scope + failure surfaces)
- [Development-Conventions](Development-Conventions.md) (upstream alignment)
- [Golden-Route-Solzreed](Golden-Route-Solzreed.md) (starter corridor reference)
