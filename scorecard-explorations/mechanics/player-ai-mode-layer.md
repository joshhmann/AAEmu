# Player AI mode layer — every mode has exactly one owner

Design note for the card that follows T10 (2026-10-18). Not an implementation record.

## Why

Live run 11 (E325): the bot was fighting a boar for quest 251. Every activity module denies while the
bot `IsInBattle`, so the arbiter elected `(none)`. Nothing fought back, the boar kept hitting, the battle
never ended, so nothing could ever be elected again — a deadlock. Runs 8 and 10 passed only because their
fights ended before a re-election.

Standard game AI splits a bot's brain into layers. The project already has two of them; the third, the
top "mode" layer, was missing, so being attacked had no owner.

| Layer | Question it answers | What exists |
|---|---|---|
| Mode | Am I dead, fighting, or free? | **Missing as a contract** (this card) |
| Choice (inside "free") | What should I do now? | `BotGoalArbiter` bands + commitment |
| Action | Is this request running, done or failed? | `ActorRequest` lifecycle |

## The modes and their owners

| Mode | When | Owner | Status |
|---|---|---|---|
| Dead | `IsDead` | The scheduler's death watch (resurrects after `ResurrectDelay`; the arbiter never sees a dead bot) | Exists |
| Fighting | `IsInBattle` and an attacker is in reach | **New** `SelfDefenceActivityModule` (`defend.self`, priority 90) | This card |
| Fighting, critical HP | HP below the flee threshold | The executor's survival gate (`StepSurvivalWake`), which already runs before every activity | Exists |
| Free | otherwise | The existing modules (quest, farm, roam, recovery, idle …) | Exists |

Out-of-combat low health stays with `OutOfCombatRecovery` (a Free-mode activity), as today.

## Rules

1. **One owner per mode.** Each module declares the modes it serves (`ServedModes`, default Free). The
   startup dispatch check reports any mode with no serving module, next to the existing "activity with no
   carrier" check.
2. **Fighting reuses the existing fight code.** The conflict engagement's approach → auto-attack → skill
   cast block is lifted into one shared helper; the conflict step (players) and the new self-defence step
   (whoever is attacking the bot: an NPC whose aggro table or target names the bot, or a player the bot is
   actively hostile with) both call it.
3. **Leaving a fight cleans up.** When self-defence is no longer elected the bot stops auto-attack and
   clears its target, the same cleanup the conflict step does on target loss.
4. **No second brain.** The arbiter, scheduler and action lifecycle are unchanged; self-defence is one more
   module and one more executor branch. An operator assignment (priority 110) still outranks it.

## Proof

- Unit: a bot in battle with an NPC attacker elects `defend.self` and the executor attacks that NPC; with
  the module removed the arbiter elects `(none)` (the run-11 shape, red); leaving the fight stops
  auto-attack; the dispatch check reports a missing Fighting owner.
- Live: the T10 quest lane again, expecting the chain to complete with fights that no longer stall.

## Not in scope

Smarter combat (kiting, healing, target priority) — the quest's combat brain stays where it is; flee
behaviour stays with the survival gate; group/party combat; PvP policy beyond fighting back.
