# Discovery-to-delivery contract — any zone, feature, mechanic or implementation

Use this common contract for every implementation. Fill it in the existing task/card
or domain dossier; do not create a parallel status system. Feature/fix templates add
their branch, test and review requirements. Scale detail to risk; unrelated fields
may be N/A with a reason. Fork-only; normal Characters and gameplay services;
canonical data read-only. This template grants no push/deploy authority.

## How to use this repeatedly

This is the common process, not a farm-specific checklist. Use it at two scales:

1. **Scope brief:** fill outcome, evidence inventory, coverage/dependency map and
   ordered slices for a whole journey, zone or mechanic. Research can finish while
   implementation remains open. Do not dispatch the entire brief as one giant card.
2. **Implementation slice:** link that brief, fill the changed subset and concrete
   acceptance/handoff. Reuse existing evidence with its original scope; do not copy
   or rerun the entire research dossier for every child task.

| Stage | Agent must produce | Exit to next stage |
|---|---|---|
| Scope | User outcome, parent, supported variants, non-goals, bounded coverage denominator | Destination is clear; material product choices resolved or explicitly held. |
| Discover | Existing docs/code/tests/corpus inventory, canonical requirements, graph relationships and source provenance | Facts and assumptions separated; missing facts get bounded research/capture tasks. |
| Reconcile | Requirement-by-requirement existing capability and evidence; contradictions, gaps and blockers | No completion inferred from wiring, tool output or test names. |
| Slice | Dependency-ordered cards with one observable outcome each; owner/verifier and exit tests | Only cards with sufficiently grounded requirements and available dependencies are Ready. |
| Implement and verify | Narrow shared-path change; unit/rig and required real-service/client evidence | Required scope proved, or explicit partial/blocked verdict with next action. |
| Reconcile and expand | Updated authoritative records; residual gaps; evidence-reuse decision for next variant | Expansion is a deliberate new scope, never an automatic grade promotion. |

Stop research when there is enough grounded information to dispatch the next safe
slice; record the bounded unknowns. Do not require an exhaustive world census to fix
one mechanic. Conversely, do not code around an unresolved gameplay-rule decision.

## 1. Outcome and boundary

- User outcome, in one sentence:
- Parent journey + existing milestone/mechanic/track:
- Scope kind: zone/instance / player journey / mechanic / bot controller / service/UI:
- Supported variants and coverage denominator (zone keys, level bands, objective
  families, item/skill categories, state transitions, configurations as applicable):
- This slice changes exactly:
- Non-goals / deferred work:
- Full journey or explicitly named subloop:
- Current status: Ready / In progress / Implementation complete–verification pending /
  Verified at named layer / Blocked. Record deployment and H separately.

## 2. Establish truth before design

- Source SHA + relevant dirty files; reference-data identity:
- Canonical facts, exact tool/query inputs, provenance, bounds and confidence:
- Current ordinary human gameplay path (packet → service → world/persistence):
- Existing bot/controller path and reusable implementation:
- Assumptions or contradictions still unresolved:
- Proposed game-rule deviations (require explicit product decision):

A template ID is not a runtime object ID. Item price is not merchant availability.
Quest display level is not its full eligibility. Trace prerequisite requirements,
rewards, item/skill identities and interaction targets. Verify before wiring.

### Source inventory and tool routing

Inventory existing sources before requesting another human trace or building a new
tool. Select relevant rows below; mark unrelated/unavailable ones N/A or UNKNOWN
with a reason. Availability does not justify running every tool on every task.

| Source / tool | What to inspect and record | What it cannot prove alone |
|---|---|---|
| Authoritative docs, mechanic/zone dossiers | Current requirement, historical artifact, claim date, open tasks; START-HERE → PROJECT-CONTROL/STATUS/SCORECARD/ROADMAP | A dated statement is not a current run. |
| Archaeology MCP | Catalog first (`list_sources`, `list_tables`, `describe_table`); bounded `query_sql`/`lookup_row`; `trace_*`, `find_quest_objectives`, `trace_references` for dependencies; source tools for corroboration | Static rows or heuristic joins do not prove live behavior, complete eligibility or client feel. |
| Source/tests + Graphify | Real handlers/services/models/persistence; test setup/assertions/bypasses; related callers/docs. Record graph revision and traversal bounds; read actual source | Graph edges, a class existing or a test named E2E are not execution evidence. |
| Human/client trace corpus | Existing scenario specs, raw trace identities and revisions, start/end boundaries, actual actors, target IDs, timing, resource/world consequences, error paths and observer verdict | Packet presence/similarity or capture PASS does not establish semantic correctness, full journey coverage or H acceptance. |
| Bot/server logs and live scenario artifacts | Authenticated execution, real target/action outcomes, before/after state, refusal/recovery, environment, restart and load scope | Self-reported intent or success text is not authoritative state; live bot proof is not H. |
| Client assets / AAPak | Only intentionally configured read-only inputs; record version and bounded entry lookup | Assets describe possibilities, not current deployment wiring. |

For each material claim fill this small evidence index (rows may reference existing
artifacts instead of duplicating them):

| Claim / requirement | Source artifact + path/hash/revision | Exact query/filter/scenario and bounds | Evidence layer + confidence | Finding / contradiction / missing evidence |
|---|---|---|---|---|
| | | | | |

Preserve archaeology's source_id/path/version, canonical DB hash, exact inputs and
truncation. For corpus evidence record producer (human/live bot/rig), scenario and
build, actor/world, relevant config, input artifact/hash and evaluator revision.
Unknown source identity stays UNKNOWN. Derived summaries inherit input limitations.

### Corpus workflow and missing-evidence tasks

1. Locate relevant existing traces/specifications and inspect their raw evidence.
   The coverage mapper's packet families/recommendations guide discovery only.
2. Compare observed client sequence/timing/results with canonical requirements and
   the actual server path. Separate capture validity, behavior correctness and H.
3. Record disagreements explicitly: expected requirement, observed result, source
   revisions, plausible explanations and the smallest distinguishing probe. Do not
   silently choose the convenient source or assume static data encodes every rule.
4. If a needed fact is absent, create one capture/reproduction brief: named question,
   exact starting conditions, normal actions, expected signals/postconditions, negative
   case where relevant, collector/owner and evidence layer. Human action uses the
   [trace task standard](../Scripts/playertrace-coverage/TASK_SPEC_AND_EVALUATION_GUIDE.md).
5. Reuse [existing corpus tooling](../Scripts/playertrace-coverage/README.md). Put any
   exploratory regeneration in an isolated output directory; preserve raw traces and
   prior artifacts. No live writes or human acceptance by inference.
6. If safe independent implementation can proceed, state why the missing trace is
   not its dependency. Otherwise mark that slice blocked on the named evidence task.

## 3. Completeness and dependency map

For each required step, distinguish missing implementation from missing evidence.
Planning status does not replace evidence grades.

| Required step | Existing path / canonical requirement | Implementation state | Evidence layer + artifact | Missing dependency / next task |
|---|---|---|---|---|
| Initial state / acquire prerequisites | | | | |
| Discover and bind eligible targets | | | | |
| Travel / legal reach | | | | |
| Execute ordinary action / timing | | | | |
| Verify consequence / conservation / ownership | | | | |
| Refusal / interruption / recovery | | | | |
| Repeat and applicable persistence | | | | |
| Human/client observation (separate) | | | | |

No required gap may be hidden by a fixture. Use N/A with a reason for non-loop work;
replace this map with its explicit observable service/UI outcome where appropriate.

## 4. Execution and acceptance

- Reproducible start: character/account, level, inventory, resources, quest/ownership
  state, world/zone, config/rates/feature flags; distinguish natural setup from seeding.
- Input/trigger and exact expected ordinary world/client outcome:
- Happy-path assertion and authoritative before/after state:
- Negative/refusal and retry/idempotency cases applicable to the change:
- Interruption/recovery, repeat and restart cases required by the claim:
- Exact build/test/scenario commands, isolated resources, required assets:
- Bypasses forbidden during acceptance:
- Unit/contract test evidence expected:
- Live-server/client evidence expected; H scenario if required:

For bots: GOAP/policy may choose; shared actions execute; authoritative state verifies.
A deterministic reference route may select known actions, but may not manually make
them succeed. No second scheduler, request lifecycle or gameplay implementation.
Separate seeded live subloop, complete acquisition-to-repeat loop, and adaptive
autonomy. Passing one does not prove the others.

## 5. Ownership, order and stop rule

- Named implementer / independent verifier / tracker:
- Concrete predecessor task or acceptance dependency:
- In-scope files and safe parallel-work boundaries:
- Small implementation sequence (one outcome, not an entire subsystem):
- Stop when / escalate if:
- Next slice after this one:

Do not widen the goal to avoid a blocker or add items to merchants to make a test
pass. Record the blocker and the smallest corrective slice. Complete a single-bot
proof before scaling the same claim to multiple bots/regions.

### Task queue derived from the gap map

| Priority / task | Parent requirement + concrete outcome | Kind (research/capture/fix/feature/validation) | Dependencies | Acceptance layer + test | Owner/verifier | Ready/blocked and reason |
|---|---|---|---|---|---|---|
| | | | | | | |

One global-mechanic defect has one owning task; affected zones/journeys link to it.
Do not clone the same bug per zone or create a zone-specific gameplay bypass.
Prioritize safety/conservation and blocked progression before additional breadth.

### Next zone, feature or variant — reuse plus delta

Before expanding, record **what transfers, what differs, and what must be re-proved**:

| Expansion | Reuse only within its proved scope | Inspect and verify the delta |
|---|---|---|
| Next zone / instance | Shared quest/action/service behavior and applicable regression tests | Canonical zone/world keys, quest prerequisites/objective families, spawns/services, faction rules, boundaries/terrain/navigation, transitions and a named local route. |
| New mechanic / feature | Existing ordinary action lifecycle, authorization, persistence and diagnostics | New canonical rule/state machine, packet/service path, resources/ownership/timing, refusals, concurrency and persistence obligations. |
| Broader bot autonomy | Verified actions and baseline journey | New perception, choices, unavailable/stale targets, alternate routes, interruption/replanning, actual self-acquisition/repetition; population load is a separate claim. |
| Service / UI / tooling | Existing interfaces and relevant tests | Input/output contract, authorization/failure modes, rendered state or service outcome; gameplay-loop fields may be N/A. |

Evidence reuse requires a named original scope, SHA/data/config identity, and a
reason dependencies are unchanged. Changes to handlers, canonical data, config,
objective family, target type, environment or scale trigger the relevant recheck.
A second zone is not verified because the first passed; nor should all shared engine
work be repeated from scratch. Maintain a bounded supported-coverage matrix in the
existing mechanic/zone ledger, with unsupported/unknown variants explicit.

## 6. Evidence-backed handoff

- Implemented / committed or WIP:
- Verified: exact scenario, evidence layer, SHA + dirty state, command/environment,
  pass/fail/skip and identities, artifact paths, build/compiler and MCP status as applicable:
- Seeded/mocked/manual steps, input provenance and evaluator limitations:
- **Not proved / missing or blocked required legs:**
- Verdict: **LOOP INCOMPLETE** if any required leg is unproved; otherwise name the
  exact closed scope and layer (never just "fully verified").
- Independent review: actual signoff or pending; deployment: actual revision or unknown:
- H: actual named human verdict or UNKNOWN:
- Records/dashboard updated; next concrete task:

Follow [AGENTS.md](../AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16).
Synthetic tests are valid evidence of their synthetic scope. Evaluators cannot
upgrade their inputs to real gameplay. Missing provenance remains UNKNOWN.
