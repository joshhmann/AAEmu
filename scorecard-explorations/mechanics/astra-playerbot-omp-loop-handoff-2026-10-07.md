# Muse handoff through OMP /loop

Date: 2026-10-07. Status: instructions prepared; no loop launched.

OMP repeats a bounded instruction. The existing `.omp/state/` files retain the objective, active card, evidence and next action. The [Muse handoff](astra-playerbot-muse-handoff-2026-10-07.md) supplies engineering direction; the [P0–P6 path](astra-playerbot-rework-path-p0-p6-2026-10-07.md) supplies dependencies. Use one outcome per loop.

## Verified mechanism

Installed OMP v18.7.0 supports:

```text
/loop [count|duration] [--while|--until '<shell command>'] [prompt]
```

The submitted prompt repeats after each yield. `--while` continues on exit 0 and stops on exit 1; `--until` reverses that polarity. Other exit statuses and condition timeouts are errors. The condition is checked before iterations; stdout is not the predicate. Escape suspends the ongoing loop; `/loop` again disables it. Iteration/duration limits do not replace command-specific timeouts or prove task completion.

Installed-source evidence: [command registration](/root/.bun/install/global/node_modules/@oh-my-pi/pi-coding-agent/src/slash-commands/builtin-modes.ts:414), [argument parser](/root/.bun/install/global/node_modules/@oh-my-pi/pi-coding-agent/src/modes/loop-limit.ts:32), [condition evaluator](/root/.bun/install/global/node_modules/@oh-my-pi/pi-coding-agent/src/modes/loop-condition.ts:98). This is code inspection, not a live worker trial. Recheck syntax after upgrades. This recipe uses `/loop`; avoid layering a second goal-mode continuation mechanism over it.

## Updated starting point

At preparation, HEAD is `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus existing dirty work. CURRENT still summarizes E-39 and the quest-251 objective. The ledger reaches **E-49**, recording enrollment witnesses implemented and a short observation lane pending. Those are inherited ledger claims, not independently rerun verification.

**Inspect E-49 and any newer entries before proposing enrollment instrumentation again.** The older Muse guide starts from E-48. Its root-cause uncertainty still applies; the adjacent SQL error is not a demonstrated enrollment cause. Do not reserve the next E-ID or duplicate another worker's run. The initial outcome here is an evidence-backed next card. No live lane is authorized by this document.

## Bootstrap message

**Correction from the review through E-51:** on the next iteration, read the Muse handoff's **Required correction before enrollment repair** and the [review](astra-playerbot-omp-work-review-2026-10-07.md). Establish production designation, fresh activation versus live-character adoption, and the adjacent lifecycle effects before selecting a repair. The existing `Activate` path can reload the Character and clear its connection; an unregistered harness character alone does not justify a new production path. Check newer evidence and revise the active card accordingly; preserve the currently assigned mode and scope.

Open OMP in `/root/aaemu-dev`, select your configured Muse model, and send this before enabling `/loop`:

```text
Use scorecard-explorations/mechanics/astra-playerbot-omp-loop-handoff-2026-10-07.md
and its linked Muse handoff. Bootstrap one bounded REVIEW card for P0.1.
You may write the review/card and task-state records; no gameplay edits or lane
launch in this assignment. No commit, push, deployment or agent dispatch.

Read AGENTS.md, START-HERE.md and .omp/state/README.md. Reconcile CURRENT with
newest DECISIONS/EVIDENCE and the current dirty baseline. Read relevant project
authority sections, selected audit layers and immediate dependencies. Preserve
the existing objective, historical evidence, quarantine and unrelated work.
The handoff ledger reaches E-49: witnesses implemented, live verification pending.
Reopen that work; don't recreate it or treat targeted tests as live proof.

Record one active review card in CURRENT with outcome, mode REVIEW, permitted
files/actions, acceptance, SHA/dirty baseline, evidence IDs, next action,
stopping point and verifier. Link its deliverable. Correct stale summary facts
with evidence. Add exactly one line: Loop disposition: CONTINUE

If this card conflicts with the objective or another worker owns these files,
record the conflict and Loop disposition: STOP_BLOCKED. Do not silently replace
the objective. Report the initialized card and wait. Bootstrap does not execute
the card.
```

These fields extend CURRENT; they do not create another state store. One integrating worker owns the state files. Do not run competing loops over the same card or shared lane.

## Repeating command

After bootstrap records CONTINUE, paste this single line in OMP chat:

```text
/loop 6 --while 'rg -q "^Loop disposition: CONTINUE$" .omp/state/CURRENT.md' Read scorecard-explorations/mechanics/astra-playerbot-omp-loop-handoff-2026-10-07.md and execute one iteration of its Iteration contract for the active card in .omp/state/CURRENT.md.
```

Six is a starting iteration budget. Missing CONTINUE stops repetition; a missing file errors. Budget exhaustion leaves the task incomplete unless acceptance was actually met. Inspect recorded evidence and the remaining action before restarting.

## Iteration contract

1. **Read fresh state.** Read CURRENT, relevant decisions, newest evidence and the active card's changed paths. Reconcile other-worker changes before editing. Inspect only the selected layer and dependencies needed for this card. For enrollment work, apply the linked required correction before selecting or implementing a repair: designation contract, embodiment/adoption distinction, full lifecycle effects, and a legitimately enrolled positive case.
2. **Do the next authorized action.** REVIEW produces evidence and a concrete brief. IMPLEMENT changes assigned code and runs appropriate checks. LIVE PROOF runs its assigned isolated scenario. Repetition grants no additional scope or mode change. Apply the existing implementation template and archaeology contract when applicable.
3. **Observe before advancing.** Record exact excerpts, commands/artifacts and evidence IDs. Separate CODE, LIVE-RECORDED, INFERENCE, DIRECTION and UNKNOWN. Progress means new evidence, a scoped change or a resolved acceptance predicate. Repeating the same hypothesis or check without new cause is not progress. Preserve normal gameplay, canonical ownership and the audit's Do Not Touch sections. No new threshold or fixture gift to manufacture a pass.
4. **Checkpoint before yielding.** Append evidence/decisions as needed. Update CURRENT's actual status, confirmed/unverified facts, deliverable, next action and disposition. Preserve implementation / verification / deployment / human acceptance boundaries. Report the delta and remaining uncertainty.
5. **Continue or stop explicitly.** Continue only with a useful action inside this card and assigned mode. A failed proof yields evidence; it does not authorize an unassigned repair. Stop on a blocker, completed acceptance, or pending independent review. Do not automatically choose the next phase.

| Exact line in CURRENT | Meaning |
|---|---|
| `Loop disposition: CONTINUE` | Concrete authorized next action remains. |
| `Loop disposition: STOP_DONE` | Card acceptance is met and evidenced. A review card may end at a completed brief; that is not implementation acceptance. |
| `Loop disposition: STOP_BLOCKED` | Record the prerequisite/conflict and what would resolve it. |
| `Loop disposition: STOP_REVIEW` | Assigned work awaits independent verification or the next assigned mode; name that check. |

These are proposed operating labels, not built-in OMP statuses. Interruption or exhausted budget must leave unfinished work recorded honestly. A new card needs its own recorded scope/mode and dependency check. One card does not close a whole layer or P-phase.

## Verification of the handoff

The guard reads a model-written field. It stops repeated prompting; it does **not** independently prove acceptance. The verifier inspects the final diff, acceptance artifacts, baseline and intervention record. Required implementation gates use the final tree and record SHA, commands, environment, counts and smoke outcomes. Existing red gates remain red; documentation review has no gameplay gate claim.

After 3–5 worker turns, apply the repository brief-evolution method to ignored constraints, unsourced claims and read-back cost. Tune from observed outputs; no effectiveness score exists yet. Keep the current card bounded so failures remain attributable.
