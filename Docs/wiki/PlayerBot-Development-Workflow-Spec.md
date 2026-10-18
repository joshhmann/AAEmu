# PlayerBot development and verification workflow — delivery specification

- Audience: All coding agents, integration owners, reviewers, and contributors developing PlayerBot loops
- Last verified against: E301–E307 workflow/controller checks through 2026-10-16 at `develop` / `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8`, with inherited dirty work preserved; no new live gameplay verification
- Status: Tickets1/2 delivered; ticket3's isolated repair remains blocked on full qualification. D118 fast checks and D119 failed-portion controls are verified at their recorded layers. Native food continuation and overall productivity improvement remain unproved.
- Tracker: Delivery & Sessions, card `playerbot-development-verification-workflow`, under Shared autonomy; parent specification, not an executable allocation

## Problem statement

PlayerBot development spends too much time preparing, watching, repeating, and reviewing expensive whole-loop checks. Gameplay defects, test setup problems, missing observations, and cleanup failures are discovered together. Agents repeatedly pay for unrelated prerequisites to investigate one behavior, while per-patch permissions and copied evidence descriptions add coordination work.

The user reported more than twelve hours on a farm-to-food loop and similar development throughput across different coding models. The requirement is therefore agent- and model-independent and applies to every future PlayerBot loop: quests, farming, crafting, trade, recovery, cooperation, and transitions between activities. Barley is motivating evidence, not the product boundary. No causal comparison of model capability or complete timing breakdown has been established.

Existing cheap checks, native actor/scheduler fixtures, isolated integration scenarios, full gates, preserved evidence, the Jev observer/reviewer, and the Delivery & Sessions tracker already exist. The task is to make them support a shorter development cycle without lowering gameplay acceptance standards or adding another framework.

## Solution

Give an agent one bounded behavior-level outcome and authority to complete its local investigation, reproduction, implementation, and focused verification. Separate isolated development evidence from final joined-loop acceptance. Use the smallest real production behavior that can reproduce a defect; use whole-loop execution when the question actually requires the joined journey.

Jev assists plan review, evidence triage, and results interpretation but does not grant permission for local development. Unavailability or an inconclusive answer is recorded without blocking otherwise authorized local checks. Deterministic failures, independent review, ownership boundaries, and explicit live-execution controls remain authoritative.

Deliver the policy through the existing authoritative instructions, task briefs, tracker, and verification paths, then exercise one representative repair to demonstrate that the process works. Do not restart or take ownership of another agent's active repair merely to run the pilot.

## User stories

1. As the project owner, I want one development contract for every agent and every PlayerBot loop, so that delivery does not depend on a model-specific prompting workaround.
2. As an implementing agent, I want to reproduce a behavior with explicitly supplied prerequisites in an isolated environment using real production services, so that unrelated acquisition and waiting do not dominate each edit.
3. As a player and acceptance reviewer, I want the completed bot journey to retain its agreed ordinary acquisition, legality, timing, ownership, costs, consequences, recovery, continuation, and persistence requirements, so that isolated tests cannot manufacture a full-loop success.
4. As an implementing agent, I want authority to investigate, edit, and run focused checks within an agreed boundary without per-edit approval, so that I can finish a coherent repair instead of repeatedly submitting unapplied plans.
5. As an integration owner, I want escalation when scope expands, prerequisites are unavailable, or repeated failure contradicts the diagnosis, so that local autonomy cannot silently become an unrelated rewrite or uncontrolled live experiment.
6. As an implementing agent, I want a long-run failure reduced to a focused reproduction or a named missing observation, so that the next expensive run answers a new question.
7. As a reviewer, I want gameplay failure, invalid setup, insufficient observation, and cleanup failure distinguished, so that the chosen repair addresses the failed behavior rather than weakening its test.
8. As an agent using Jev, I want bounded assistance and explicit unavailable/inconclusive results without a local execution lock, so that model access does not become a new development bottleneck.
9. As a verifier, I want review and required broad qualification applied to the stable candidate, with relevant checks invalidated when that candidate changes, so that we retain regression protection without repeating whole gates for each diagnostic edit.
10. As a contributor, I want bounded runtime waiting handled by existing supervision while independent work proceeds safely, so that agents do not spend development time narrating polls or competing over one checkout and database.
11. As the project owner, I want a pilot report showing delivered behavior, failure-to-diagnosis time, expensive runs, repeated failures, and attended waiting, so that any productivity claim rests on observed work rather than test totals or model-call counts.

## Implementation decisions

### Scope and existing ownership

This is a cross-cutting development workflow for all PlayerBot capability families and all implementing agents, independent of provider, model, or role nickname. It is not a new gameplay policy, a replacement bot brain, or a second tracker.

Use the existing parent brief and bounded implementation-card model. The parent describes the common contract; child tickets deliver reviewable outcomes. Starting a tracker record is not permission to deploy, mutate live state, or launch a worker. The current implementation owner retains their files and resources.

### Development evidence versus acceptance evidence

An isolated development case may supply inventory, injury, quest state, targets, or other prerequisites needed to exercise its declared behavior. Setup must be explicit, isolated, and separated from execution. Exercise the ordinary production rules at the behavior under test; supplying prerequisites does not authorize fabricated callbacks, rewards, terminals, or effects during that behavior.

Reports must name the supplied state, actual production path, observed outcome, and what remains unproved. Contract, rig, seeded real-service, earned joined-loop, and human/client evidence remain distinct. An isolated pass never claims acquisition or independent choice it did not exercise.

Final acceptance retains the journey's agreed initial state and all applicable legs. Requirements remain outcome-specific: do not attach every possible gameplay requirement to every slice. Existing historical failures and human observations retain their original meanings. Reuse compatible evidence without relabeling segmented histories as a new continuous run.

### Bounded local authority

Before implementation, agree the behavior, allowed files, isolated resources, acceptance, owner/verifier responsibilities, and stop/escalation conditions once. Within that boundary the implementer owns investigation, reproduction, edits, and focused check iterations without intermediate exact-patch approval.

Escalate material scope or product changes, unavailable prerequisites, ownership conflicts, and repeated failure of the same diagnosis. Review the completed candidate before integration or live execution. Shared-state operations, deployment, acceptance changes, and expanded resources still need their existing explicit authorization. No numeric retry budget is invented by this specification.

### Failure-driven verification

A long-run failure must lead to either a smaller reproduction through the relevant production behavior or a concrete explanation of why a real runtime is necessary and which missing observation the next bounded run will capture.

Before repeating an expensive run, state the changed code, input, or evidence question, the expected discriminating observation, and why a smaller check is insufficient. Do not repeat unchanged runs merely to see whether they pass.

Classify failures by their observed boundary: gameplay, setup, observation, or cleanup; retain uncertainty when evidence cannot distinguish them. Validate the harness's relevant control and observation behavior independently where possible. Use preserved failure records to exercise evaluators, and real actor/scheduler/service paths when timing or ownership is essential.

Focused checks belong inside local iteration. Required full gates qualify a stable candidate before the relevant integration/release boundary. Subsequent relevant changes invalidate affected results; previous qualification is not a blanket pass. Preserve existing merge gates and scenario-specific acceptance rather than silently deleting them.

### Stop, repair, retry the failed portion, then continue — D119

New loop harnesses name the observable portions of the journey and use the
shared `LoopDevelopmentRun` control with the existing `execute.py` supervisor.
The food harness is the first adapter; older scenarios are not automatically
converted. A portion ends only when its real outcome is observed, not merely
because an actor request completed.

1. **Stop on the first failure.** Save that portion and reason immediately,
   request ordinary owned cleanup, and preserve every prior attempt.
2. **Repair the smallest demonstrated defect.** Use the isolated D118 command,
   `./scripts/dev-test.sh integration <Class>` or `unit <Class>`, for development.
   Fast checks skip expensive analysis and cannot qualify the final candidate.
3. **Admit an explicit continuation before any mutation.** Check the prior
   checkpoint's hash, fixture/character/reference-data identity, ordered portions,
   settled effects and exact current saved state. The native adapter separately
   reconciles partially completed work. Changed code/binaries get new identities;
   old failure records remain unchanged. Missing or ambiguous state refuses retry.
4. **Continue from retained resources.** Re-observe ordinary gameplay; never
   replay old actor requests, refund costs, restore saved world state, grant
   missing materials or rerun acquisition merely to reach the failure.
5. **Run fresh acceptance separately.** A continued success is
   `DEVELOPMENT_PASS`, not `PASS`. Acceptance rejects checkpoint/history input and
   requires a separately declared initial fixture and uninterrupted whole journey.

Each attempt writes native progress, an immutable final checkpoint and its native
report. A forced termination, unresolved cleanup or unverified persistence is
non-resumable. This is a local harness control, not a checkpoint service or a
new gameplay path. It does not waive normal timers or authorize another owner's
services. The paused food fixture and earned inventory remain held.

The loop supervisor intentionally bypasses the quest-specific evaluator/Jev CLI;
it must not invent quest IDs for food. Its review status explains that limitation.
Request coherent-wave advice through the already documented generic read-only
`reviewPacket` seam; missing advice cannot override deterministic outcomes.
See [runner usage](Quest-Test-Observer.md#failed-portion-development-and-fresh-acceptance)
for inputs and evidence boundaries.

### Jev policy and D116 reconciliation

The user explicitly selected advisory Jev for local development. This supersedes D116's requirement to hold an otherwise authorized local testing wave merely because Jev is unavailable. Request bounded plan/coverage and recorded-results reviews at coherent repair-wave boundaries, not between every command.

Unavailable, invalid, or UNKNOWN model results must be visible and must not change deterministic outcomes. Agents may continue authorized local development and focused verification; a concrete issue identified by any review is assessed on its evidence. Independent review and required acceptance remain in place. This policy does not silently broaden live execution or deployment permissions.

Update conflicting current workflow instructions and worker briefs together, retaining D116's historical decision/results as history with an explicit successor reference. Do not rewrite historical runs to claim Jev participation. Reuse the existing read-only adapter and deterministic evaluator; no model gameplay/source tools, new evaluator, or automatic live retry.

### Supervision, evidence, and concurrent work

Reuse existing bounded supervisors and normal cleanup. Notify on decisive failure, meaningful progress, missing evidence, resource trouble, or completion rather than requiring an agent to watch every poll. Independent development during waits must use nonconflicting ownership and isolated build/runtime resources; never mutate a frozen candidate or share resettable acceptance state.

Reuse existing manifests, commands, results, and artifact references instead of manually copying changing identities and counts into new packets. Add only narrowly justified reporting changes; do not build a telemetry platform or generalized checkpoint service.

### Rollout and durable records

Reconcile the common operating contract, current project-control/state instructions, implementation briefs, observer documentation, and dashboard workflow guidance. Preserve source-of-truth ownership and active work. Capability status and human/live evidence must not be promoted by a documentation change. Update affected status/roadmap/scorecard records only to report the workflow rollout and its honest limits.

Choose one available bounded repair as a pilot after its owner and resources are established. The current recovery/removal work is a possible source of prior art, not a required takeover or a restriction to food. Record which behavior changed, the local reproduction/fix result, review, broad qualification, and any separate live acceptance.

Measure observed failure-to-diagnosis time, diagnosis/edit time where available, build/startup time, required gameplay waiting, review/approval waiting, whole-loop runs per repair, repeated occurrences of the same failure, and engineer-attended waiting. Missing timestamps remain unknown. A pilot may establish operability without proving a numerical speedup; numerical improvement requires a comparable recorded baseline.

## Testing decisions

### Highest useful verification seams

For workflow delivery, use the existing Delivery & Sessions card/checkpoint handoff and one actual bounded repair performed through the revised contract. This is higher-value than tests that merely search instructions for approved wording.

For the pilot's gameplay behavior, use the existing public production action or lifecycle boundary through the real actor, scheduler, and ordinary service where relevant. Existing native fixtures and isolated integration scenarios are prior art. Verify authoritative effects and failure behavior, not mock forwarding or helper internals. No new verification seam is required by this specification.

### Acceptance scenarios

1. **Bounded agent autonomy:** after a complete repair contract is approved, an implementer produces a reproduction, applies the fix, and runs focused checks without per-edit approvals. Its saved handoff records actual results and limits. No integration/live permission is inferred.
2. **Evidence separation:** a case with supplied prerequisites can pass its isolated assertions while the joined journey remains unproved. The handoff and reviewer do not promote it to acquisition, autonomous choice, full-loop, or human evidence.
3. **Unavailable or inconclusive Jev:** exercise the existing review-status handling with a controlled unavailable/inconclusive case, without requiring an external outage. Authorized local checks can complete, reviewer status stays explicit, and native failure cannot become PASS. This proves local nonblocking behavior, not real provider availability or cause accuracy.
4. **Long-run failure becomes a focused check:** use one preserved or newly observed failure. Demonstrate a failed-before/passed-after production-behavior case where practical. If a runtime is indispensable, record why and show the distinguishing observation; do not replay unrelated acquisition solely to reach it.
5. **Harness honesty:** cover the relevant setup/observation/cleanup failure using existing replay or native seams. Missing observations remain unknown, and unfinished work cannot earn completed-loop acceptance. Do not add unrelated generic harness cases to the pilot.
6. **Boundary enforcement:** show that an out-of-scope prerequisite or resource request is recorded as an escalation rather than silently applied. Do not stage a destructive live operation to prove this.
7. **Stable-candidate qualification:** retain exact source/dirty identity, required focused and broad checks, independent review, and deployment state. Changed relevant bytes cannot inherit an unrelated prior green gate.
8. **Productivity evidence:** produce a pilot report with the recorded measures above and honest unavailable fields. Report delivered behavior and actual expensive runs; do not promise a percentage improvement without comparable evidence.

Publication verification for this specification is a successful revision-checked tracker write followed by a fresh read of the saved card and linked specification. It is control-plane evidence only, not execution of any acceptance scenario above.

## Out of scope

- Implementing this entire parent specification as one agent task or allocating work simply by publishing it.
- Restricting the policy to Sol, barley, food, or a particular model/provider.
- Replacing the Character, scheduler, actor lifecycle, gameplay services, bot brain, test framework, or project tracker.
- Lowering agreed loop acceptance, fabricating acquisition or rewards, suppressing failures, or promoting fixture evidence to live/human proof.
- Editing another owner's active repair, restarting their processes, resetting preserved failed fixtures, or modifying live characters.
- Automatic campaigns, silent live retries, shared-state mutation, deployment, commits, pushes, or upstream publication.
- A universal faster-timer policy, new checkpoint framework, broad telemetry platform, or mandatory exhaustive human parity.
- A claim that coding models have equal capability or that this process has already saved a measured amount of time.

## Further notes

### Decisions and conflicts

The user selected: (1) separate isolated development from earned acceptance; (2) give agents the complete bounded local repair cycle; and (3) make Jev advisory rather than a local blocker. The user explicitly broadened the contract to all agents and every future PlayerBot loop. These are settled decisions, not questions for another interview.

At specification time, D116 and current observer/dashboard instructions treated model access as a local testing blocker. E301/E302 reconciled those instructions and verified the advisory runner, retaining the historical evidence. No new ADR was required for this reversible operating-policy change.

### Allocations and remaining qualification

The three tickets selected the owned instruction/tooling changes and an available queue-drain repair, without taking over paused food work. E303 records a real failed-before/passed-after native case, a separate executable smoke, independent review and measured costs. The required full gate timed out in Release build; one changed diagnostic reached `AAEmu.IntegrationTests` compilation and also timed out. This identifies the observed boundary, not its cause. Independent checks cannot replace the missing full-gate result.

Ticket 3 remains blocked on that qualification. Any unrelated build/source repair needs its own bounded ownership decision; no unchanged retry or gameplay resume follows. There is no comparable timing baseline or measured speedup. Existing E295 human farm evidence and E298–E300 food/verification records retain their original scope and failure status.

### Related

- [Project delivery and ownership contract](../../PROJECT-CONTROL.md#delivery-contract)
- [Existing implementation brief](../../.kanban-templates/implementation.md)
- [PlayerBot extension roadmap](../../ROADMAP.md#playerbot-extension-roadmap)
- [Current work and ownership](../../.omp/state/CURRENT.md#session-resume)
- [D116 historical Jev testing decision](../../.omp/state/DECISIONS.md#d116--require-jev-assistance-in-every-testing-wave)
- [Evidence ledger](../../.omp/state/EVIDENCE.md)
- [Existing observer and Jev workflow](Quest-Test-Observer.md)
- [Delivery & Sessions tracker contract](../../Scripts/playertrace-coverage/README.md#delivery--sessions-workflow)
