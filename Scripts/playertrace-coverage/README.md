# AAEmu PlayerTrace Coverage Mapper

Coverage-guided human action trace analysis utility for AAEmu.

## Shared handoff — current execution order

User-supplied Muse handoff supersedes treating the gate and discovery work as
independent backlogs. Reported handoff: G7c START correction provisionally proved,
remaining wake-issuing wait; verb-proof designs Buy → Harvest → Plant → Sell → Mount;
Wave A = Buy + Harvest, Wave B = Plant + Sell, Mount after IsMounted. Designs and
provisional results are not completed verb proofs. User confirmed Muse is finished and authorized Codex to take over shared files.

Immediate order: finish/reconcile G7c's remaining wait in its existing gate; retrieve
and review the existing Wave A designs, then prove Buy/Harvest at their named layer;
continue Wave B and Mount only after their dependencies. Codex owns reconciliation
of its trace tooling with that existing program, not a second implementation queue.
The local tree confirms IsMounted is present as dirty source and no SummonMate
method is declared on IGameplayActor; deployment and live behavior are not inferred.
Wave A design located: `/root/.omp/agent/sessions/-aaemu-dev/2026-09-18T19-36-15-155Z_01a0b604-fc33-7000-8257-d2497359935f/verb-proof.md` (JSON content, design only).

Muse also reports 14 traces, 19 concrete verbs, 32 asserted-unobserved verbs and four
families without verbs; top discovery leads Loot, TurnInQuest, Harvest, Plant,
SummonMate. Retain these as reported census numbers until their source artifact is
located. Turn-in is observed: do not reopen the disproved zero-turn-in premise.
Loot needs the named missing capture, not a repeat of the combat capture. A missing
SummonMate method does not by itself establish that item-driven summoning is absent.
Nav snapshot export is a separate planning-only increment, never bot decision input.
Guide corpus does not yet exist; use engine/data/trace sources before broad scraping.

The tooling sequence below supports this shared order. It does not supersede it.

## Current delivery sequence — 2026-09-23

This is the tooling/discovery execution order supporting the existing PlayerBot
capability matrix, not a replacement milestone plan. Muse owns the active gameplay
gate; this work owns evidence discovery and capture specifications. Capture candidates
are an inventory, not an instruction to record every task.

| Order | Outcome | What is already available | Next action and stopping point |
| --- | --- | --- | --- |
| 1 — implemented | Trustworthy discovery queue | Corpus-aware triage, 7 restored packet types, corrected task assertions | Review tooling diff; Python 40/40. Full .NET gate remains failed as recorded below. |
| 2 — reviewed | Reuse the six candidate families | Existing consumable, dialogue, combat, quest and Mate files; per-task review notes | Keep wire-sequence reference; do not request replacement captures for missing payload fields. |
| 3 — mapped; sampling constraint found | Correlate actions with consequences | ActorObservation has vitals, bag/bank counts, party/target/position; current dirty source adds mounted state; E2E questState has counters | Reuse saved observations. Queued Observe can preempt world-owned actions: resolve non-preempting snapshot collection before passive live sampling. No server edit or live collector added. |
| 4 — evidence follow-up | Resolve banking/restart claim boundaries | Economy receipt covers bank money; PB-006 reboot entry cites unit tests | Locate item-round-trip and position/equipment restart receipts. Missing receipts remain unknown; do not run movement captures as persistence tests. |
| 5 — scoped | Resolve mail sender/receiver evidence | Mail S3 dossier documents historical seeded send/restart/read/take/delete | Locate raw S3 artifact; correlate mail identity and separate participant balances. New multi-actor task steps are explicit; automatic capture PASS is disabled for this scope. |
| 6 — dependency review | Select the next independent bot improvement | Capability matrix and Muse's bounded gates | Choose one missing perception/choice/action/consequence leg with disjoint ownership; define its observable exit. Reuse proved verbs; rerun gates for changed dependencies or integration questions. |

The existing task Notes fields contain WHAT / KNOWN / NEXT / DONE WHEN and review
findings. Regenerate into an isolated output directory to see those notes beside
capture identities. No gameplay evidence grade, catalog completion status, or H
verdict is changed by this worklist.

## Overview

As human action traces are collected via `PlayerTraceService`, manual examination of every trace file becomes impractical. The **PlayerTrace Coverage Mapper** analyzes trace corpora against the authoritative AAEmu packet inventory, producing:

- Comprehensive packet and semantic event coverage metrics.
- Noise-filtered, normalized action sequence skeletons and n-gram transitions.
- Scenario-to-feature matrix and Jaccard similarity indices.
- Conservative candidate action family groupings (labeled `POSSIBLE RELATED ACTIONS`).
- Coverage gap taxonomy and ranked next-trace recommendations.
- Baseline delta comparison across successive play sessions.

> [!CAUTION]
> **CORRECTNESS MANDATE — PACKET SIMILARITY IS NOT SEMANTIC EQUIVALENCE**
>
> Packet co-occurrence and sequence similarity provide structural evidence of how client-server interactions unfold. They **do not prove** that two actions share the same underlying server implementation or game mechanics.
>
> For instance, placing a chick and planting a potato both emit `CSCreateDoodadPacket`, but potato planting involves crop growth timers while chick placement instantiates an animal entity. Harvesting a crop and gathering an animal product both use `CSStartInteractionPacket` &rarr; `CSStartSkillPacket`, but interact with distinct game data templates.
>
> Never treat candidate action families as proof of gameplay equivalence without corroborating server handler implementations.

---

## Directory & File Structure

```text
Scripts/playertrace-coverage/
├── playertrace_coverage.py            # Main CLI entry point & report generator
├── coverage_engine.py                 # Streaming metrics, n-grams, similarity, gap analytics
├── packet_inventory.py                # Static scanner for AAEmu packet classes & opcodes
├── trace_parser.py                    # High-throughput, fault-tolerant JSONL stream reader
├── task_evaluator.py                  # Automated task evaluation & packet assertion engine
├── dashboard_server.py                # Interactive web dashboard HTTP server (:8085)
├── infer_homestead_traces.py          # Homestead trace evaluator (evidence-honesty gated)
├── test_coverage_mapper.py            # Unit test suite (python3 -m unittest)
├── test_infer_homestead_traces.py     # Evaluator regression suite (synthetic/live verdicts)
├── TASK_SPEC_AND_EVALUATION_GUIDE.md  # Standard schema & evaluation protocol documentation
└── README.md                          # This documentation
```

---

## Requirements

- Python 3.9+ (Standard Library only — zero external pip dependencies).

---

## Usage

### 1. Full Corpus Analysis

Run against the default trace directory (`traces/player-actions/`):

```bash
python3 Scripts/playertrace-coverage/playertrace_coverage.py \
    traces/player-actions \
    --repo-root . \
    --out playertrace-coverage
```

### 2. Compare Against a Baseline (Delta Mode)

After a new play session, compare current findings with a previous run:

```bash
python3 Scripts/playertrace-coverage/playertrace_coverage.py \
    traces/player-actions \
    --repo-root . \
    --out playertrace-coverage-new \
    --baseline playertrace-coverage/summary.json
```

### 3. Filter to Specific Scenarios or Thresholds

```bash
python3 Scripts/playertrace-coverage/playertrace_coverage.py \
    traces/player-actions \
    --scenario pick_potato harvest_chicken \
    --similarity-threshold 0.40 \
    --min-count 2
```

---

## Interactive Task Dashboard

Launch the interactive web dashboard for real-time task instructions, required checklists, one-click copy, and trace evaluation:

```bash
python3 Scripts/playertrace-coverage/dashboard_server.py --port 8085 --host 0.0.0.0 --repo-root .
```

Access in your browser:
- LAN / Remote: `http://192.168.0.187:8085`
- Localhost: `http://localhost:8085`

---

## Automated Task Evaluation

Verify a trace file against target packet assertions and lifecycle integrity:

```bash
# Evaluate a specific trace file against a task ID
python3 Scripts/playertrace-coverage/task_evaluator.py \
    traces/player-actions/buy_chick__Dingus__20260914_072441.jsonl \
    --task-id buy_chick

# Auto-discover and evaluate the latest trace for a task ID
python3 Scripts/playertrace-coverage/task_evaluator.py \
    --task-id combat_basic_melee
```

For schema and protocol details, see [`TASK_SPEC_AND_EVALUATION_GUIDE.md`](TASK_SPEC_AND_EVALUATION_GUIDE.md).

---

## Generated Artifacts

The tool generates the following outputs in the target `--out` directory:

| Artifact | Format | Description |
| --- | --- | --- |
| `summary.md` | Markdown | Executive coverage report, sequence skeletons, candidate families, and next traces. |
| `summary.json` | JSON | Machine-readable analytics snapshot (`schema_version: 1`) for dashboards or CI gates. |
| `packet-coverage.csv` | CSV | All AAEmu packets: count, scenarios seen, source path, opcode, and status (`OBSERVED_AND_KNOWN`, `DISCOVERY_LEAD`, `OBSERVED_UNMAPPED`). |
| `event-coverage.csv` | CSV | Non-packet semantic events (`skill`, `world`, `interaction`, `resource`, `movement`). |
| `scenario-matrix.csv` | CSV | Matrix of observed packet/event occurrence counts across scenarios. |
| `packet-transitions.csv` | CSV | Adjacent 2-gram normalized event transitions with frequency and scenario membership. |
| `sequence-patterns.csv` | CSV | Common 2-gram and 3-gram action skeletons. |
| `scenario-similarity.csv` | CSV | Pairwise scenario Jaccard similarity and transition overlap. |
| `cooccurrence.csv` | CSV | Pairwise event co-occurrence across scenarios with joint support indices. |

---

## Homestead Trace Inference (evidence-honesty gated)

Infer bot goal/milestone chains from an `ActorAuditRecord` JSONL trace into a machine
(`homestead_10bot_inference_report.json`) and human (`homestead_10bot_inference_report.md`)
report:

```bash
python3 Scripts/playertrace-coverage/infer_homestead_traces.py \
    scorecard-explorations/generated/m7-homestead-10bot-spike.jsonl \
    --out-dir playertrace-coverage
```

Evidence rules enforced by the evaluator (AGENTS.md evidence-honesty gate):

- **The evaluator cannot upgrade its input's evidence layer.** A trace carrying an
  explicit `[SYNTHETIC` marker in *any* string field is reported as a synthetic
  orchestration contract. A trace that declares no layer is reported `UNKNOWN` — never
  assumed live. A fully synthetic successful trace can never earn a live verdict.
- **Milestone detection is heuristic text matching**, not resource conservation or
  verified intent. It is labelled as such in both outputs.
- **Only authoritative completions count**: `result: "Completed"` plus a terminal
  `Completed (…)` entry in `state_changes`. Failed/cancelled requests, duplicate
  `trace_id`s, reordered/interleaved actor records and success-sounding detail text
  without a postcondition are reported as run-integrity violations that block a clean
  verdict; a de-duplicated milestone log prevents repeated events from inflating counts.
- **Provenance is recorded**: input path + sha256, input mtime (also the report
  timestamp), producing command, evaluator git revision + sha256, source HEAD + dirty
  state, evidence layer and `is_synthetic`. Anything unobtainable is `UNKNOWN`.
- The report separates **Synthetic Verdict** from **Live Verdict** and states
  **Gameplay Loop Proof: UNPROVED/UNKNOWN**; the Honesty Notice is gated on
  `is_synthetic`.

Regression suite: `python3 -m unittest discover -s Scripts/playertrace-coverage -p "test_*.py"`
(`test_infer_homestead_traces.py`).

---

## Running Unit Tests

Execute the automated test suite with standard Python unittest:

```bash
python3 -m unittest discover -s Scripts/playertrace-coverage -p "test_*.py" -v
```

---

## Interpreting Outputs

1. **Discovery Leads vs Missing Gameplay**:
   Unobserved AAEmu packet classes are classified as `DISCOVERY_LEAD`, not missing gameplay requirements. Many packets belong to server-internal, GM/admin, proxy, or legacy mechanics not expected during ordinary player gameplay.
2. **Normalized Action Sequences**:
   High-frequency background traffic (pings/pongs, server time broadcasts, operator `/trace` chat commands) and continuous unit movement jitter (`SCOneUnitMovementPacket`) are collapsed in normalized sequence analysis so that core gameplay mechanics are clearly exposed.
3. **Candidate Families**:
   Groupings represent observed structural overlap. Always review the `key_differences` section to see what distinguishes members within a family.

## Evidence-aware capture triage

The normal mapper command also reconciles the task catalog against the selected
corpus. Use an isolated `--out` directory while reviewing results; `--tasks-file`
overrides the default `<repo-root>/playertrace-coverage/dashboard_tasks.json`.
`summary.json` includes `task_triage`, catalog SHA-256, capture SHA-256 values,
per-file assertion results and structural reuse candidates. The Markdown report
includes a task/evidence table. Existing task statuses are never rewritten.

- `CAPTURE_AVAILABLE`: at least one exact-scenario file passes capture assertions.
- `CAPTURE_REVIEW`: exact-scenario files exist but have failures or caveats.
- `SPEC_REVIEW`: unresolved packet names or no assertions; repair the specification
  before requesting play time. An unresolved name is not proof of missing gameplay.
- `REUSE_REVIEW`: another scenario contains all requested packets/events in one file.
  Inspect its action window and semantics; structural overlap is not equivalence.
- `EVIDENCE_LOCATION_REVIEW`: catalog says Complete, but evidence is absent from the
  selected corpus. Locate it before requesting another capture.
- `CAPTURE_NEEDED`: no exact capture or complete structural candidate was found.
  Only these rows enter the next-capture recommendations, ordered by catalog priority.

Assertions are never pooled across files. Declared scenario identity takes precedence
over filename inference; multi-scenario files are candidates only. Required semantic
events gate availability, in addition to the existing evaluator's packet/integrity
checks. Producer build and gameplay correctness remain UNKNOWN; these dispositions
neither verify bot capability nor promote H. FastPing/FastPong are removed from
normalized sequences while retained in raw packet counts.

### Current tooling slice (2026-09-23)

Parent: discovery-to-delivery corpus workflow supporting PlayerBot capability work.
Outcome: prevent redundant capture requests and route specification/evidence problems
before new human capture. Baseline: `6c3efe25ecb68998544eb3f75699bc1674c7eef1`
with pre-existing dirty gameplay, gates, dashboard and control records. Owner: Codex;
independent verification pending. Scope: mapper/triage/tests only; Muse's gates and
runtime behavior remain outside this slice. No database, fixture or gameplay changes.
Reference-data dependence: none; packet names are checked against the existing static
inventory, not assumed to establish semantics. Graph tooling was unavailable.

Acceptance: exact capture reuse; refusal/missing-event review; unknown-name review;
no cross-file pooled pass; alternate/mixed scenarios remain structural candidates;
raw keepalive counts retained. Python tests use synthetic traces; the local corpus
run is discovery evidence only. Stop at a reviewable mapper change and isolated report;
remaining work includes specification repair, review of candidate windows and linking
verified bot evidence. No commit/deployment or gameplay/scorecard promotion is claimed.

Verification at the above SHA + existing dirty tree and this tooling diff:
Linux x64, .NET 10.0.110, Python 3.12.3; local corpus 14 files / 49,999 records /
0 malformed; canonical SQLite MD5 `78b3bdbf038db3b927056106efdf91af`.
No MySQL/client interaction or AAPak scan was used for this slice.

- `python3 -m unittest discover -s Scripts/playertrace-coverage -p 'test_*.py'`:
  37/37 passed, 0 failed/skipped (synthetic tooling tests).
- `python3 Scripts/playertrace-coverage/playertrace_coverage.py traces/player-actions --repo-root . --out /tmp/aaemu-trace-efficiency-updated-20260923`:
  31 tasks: 3 available, 1 capture review, 8 specification reviews, 4 structural
  reuse reviews, 15 capture candidates. Acceptance assertions verified the three
  redundant recommendations absent; both fast keepalives absent from normalized
  transition/pattern outputs, with raw counts 4086 each preserved.
- `./scripts/archaeology-cycle.sh`: both Release builds 0 errors (2/6 warnings);
  156 total / 156 passed / 0 failed / 0 skipped; 24-tool full archaeology stdio
  smoke passed, 679 tables, read-only. ScriptCompiler not part of this command.
- `./scripts/gate.sh`: Release build succeeded; ScriptCompiler 0 errors/warnings;
  3436 total / 3377 passed / 58 failed / 1 skipped. Skip:
  `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG=1`
  and `AAEMU_E2E_DB_PASSWORD`. Downstream BotControl and lightweight archaeology
  smoke did NOT run after test failure. Gameplay failures include expected
  `StateTransition` versus actual `RejectedAction`; no causal baseline comparison
  was performed and no gameplay fix is included. This is NOT a green gate.

Logs: `/tmp/aaemu-trace-efficiency-python-20260923.log`,
`/tmp/aaemu-trace-efficiency-archaeology-20260923.log`,
`/tmp/aaemu-trace-efficiency-gate-20260923.log`; detailed unit output:
`/tmp/aaemu-gate-tests.TUtYSc.log`. Reports/logs are local temporary artifacts,
not durable milestone evidence. Shared dashboard source/generated artifacts and
all gameplay/evidence grades were left untouched by this tooling slice.

### Task-specification follow-up (2026-09-23)

The catalog now carries explicit WHAT / KNOWN / NEXT / DONE WHEN notes for the
8 specification-review tasks and 4 initial reuse-review tasks. These notes render
in the existing dashboard Notes field and the generated Markdown task worklist;
JSON includes `work_brief`. All 31 task identities, statuses and checklists remain
unchanged. Three assertion lists were corrected: single-equipment repair selects
`CSRepairSingleEquipmentPacket` and explicitly chooses the single-item variant;
consumable recovery removes nonexistent `CSUseItemPacket`; mounted travel uses
`CSUnMountMatePacket`. Repair durability/money and consumable recovery remain
manual consequence requirements, not inferred from packet presence.

The fresh isolated report `/tmp/aaemu-trace-efficiency-next-20260923/summary.md`
contains 3 available / 1 capture review / 5 specification reviews / 6 structural
reuse reviews / 16 capture candidates. Candidate means corpus discovery only,
not authorization or priority to capture; first link it to a missing bot requirement.
The original 8/4 counts above describe the pre-correction catalog and are historical.

Next concrete order:
1. Repair the static packet-inventory omission of registered `CSExecuteCraft`
   (its class name lacks `Packet`), then ground craft completion assertions.
2. Inspect existing consumable, mounted-travel and dialogue windows for the exact
   missing consequence/variant; reuse only what each file actually establishes.
3. Reconcile trade participants, mail send-versus-receive steps and banking evidence.
   Locate the PB-006 persistence receipt separately; movement packets cannot prove it.
4. Request a new capture only for a named remaining question blocking bot work.

Source evidence: SHA `6c3efe25ecb68998544eb3f75699bc1674c7eef1` + dirty tree.
Read-only archaeology `list_sources`, then `read_file` for
`AAEmu.Game/Core/Packets/C2G/` files `CSExecuteCraft.cs`, `CSStartTradePacket.cs`,
`CSRepairSingleEquipmentPacket.cs`, `CSRepairAllEquipmentsPacket.cs`,
`CSSwapItemsPacket.cs`, `CSSendMailPacket.cs`, `CSReadMailPacket.cs`,
`CSUnMountMatePacket.cs`. All reads untruncated; textual/code evidence only,
not live/client/H. Tool source_id/version returned null (not inferred); provenance
paths are the named files under `/root/aaemu-dev/`. Raw responses:
`/tmp/aaemu-trace-spec-archaeology.jsonl`. DB MD5 unchanged:
`78b3bdbf038db3b927056106efdf91af`. Evidence boundary:
[archaeology acceptance dossier](../../scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
Consumable corroboration is the existing `use_bag_consumable` capture's
`CSStartSkillPacket` at line 283 and its passing packet assertions; item effect and
recovery are not established by that occurrence. GameNetwork registers
`CSExecuteCraft`; registration is corroboration only, not gameplay verification.

Follow-up verification: Python suite 37/37, 0 failed/skipped; catalog comparison
against HEAD preserved statuses/checklists and confirmed exactly three target-list
changes; all 12 notes rendered into the generated report; scoped whitespace check
passed. No full .NET rerun for these catalog/report changes; the failed full gate
above remains the last gate result. Independent review pending; not deployed;
no gameplay or H promotion. Muse's gameplay/gate files were untouched.

### Current worklist after scanner repair (2026-09-23)

The scanner now recognizes direct `GamePacket`/`LoginPacket`/`PacketBase`
inheritance even when a class name omits `Packet`; ordinary helper classes remain
excluded. Current inventory: 751 (previous 744). `CSExecuteCraft` resolves to
`0x0F8`. Synthetic scanner regression verifies primary-constructor and ordinary
inheritance, opcode/direction, and helper exclusion.

Crafting capture assertions now name `CSExecuteCraft` and `SCItemTaskSuccessPacket`;
trade explicitly selects the initiator/item variant and requires its own request,
offer, lock, confirmation and terminal trade packet. Banking names item swap/task
responses but still requires locating the existing Complete receipt and proving
both transfer directions/conservation. Their notes retain earlier findings followed
by explicit corrections. The reboot task now has `evaluation_kind: state_comparison`:
it routes to `STATE_EVIDENCE_REVIEW`, never new packet capture recommendations.

Current 31-task breakdown: 18 capture candidates, 6 reuse reviews, 3 available,
1 capture review, 1 evidence-location review (banking), 1 specification review
(mail send/receive), 1 state-evidence review (restart). These are discovery states,
not a directive to execute 18 captures. Mail is the remaining specification repair;
its steps only send, while its assertions/description claim receiving. Next: map
the two participants and locate existing Mail S3 evidence before new capture.

Verification: Python **39/39**, 0 failures/skips; real scanner identifies the
7 newly included types; regeneration succeeded; catalog IDs/statuses/checklists
unchanged; scoped whitespace check passed. Same SHA/dirty-tree/environment as above.
No new .NET gate result is claimed; the recorded failed full gate remains open.
No source/runtime changes, live activity, deployment, independent signoff or H claim.

Corroboration: read-only archaeology `list_sources`, then `read_file` for
`AAEmu.Game/Core/Packets/C2G/{CSCanStartTradePacket,CSTradeOkPacket}.cs`,
`AAEmu.Game/Core/Managers/TradeManager.cs`,
`AAEmu.Game/Models/Game/Items/SlotType.cs`,
`AAEmu.Game/Models/Game/Char/CharacterCraft.cs`, and
`AAEmu.Game/Models/Game/Items/Containers/ItemContainer.cs`.
All `ok=true`, no truncation; textual/code evidence; returned source_id/version
null. Raw provenance and source contents are retained locally in
`/tmp/aaemu-trace-spec-followup-archaeology.jsonl`. Canonical DB identity and
[acceptance boundary](../../scorecard-explorations/mechanics/archaeology-mcp-acceptance.md)
remain as recorded above. Tooling changes do not establish client semantics.

### Evidence lookup and reuse review (2026-09-23)

Mail task now uses `evaluation_kind: multi_actor` and separate sender/receiver
scenario names. `MULTI_ACTOR_REVIEW` requires correlation; a complete packet set in
one file cannot promote the task. Assertions name actual send/read/sequential-take/
attachment-taken types. Catalog statuses/checklists remain historical and unchanged.

Banking receipt inspected: `/root/aaemu-e2e/logs/m8-economy-cycle-report.json` plus
`m8-economy-cycle-reconcile.md`. It reports DepositMoney, 120 bank copper and restart
retention; no DepositItem/WithdrawItem stages. It is not the warehouse item round-trip
receipt; producer SHA is absent from the inspected report. PB-006 reboot section in
`scorecard-explorations/playerbot-blockers.md` records historical unit counts, not a
linked raw live restart receipt. Its numeric ID also appears for ship physics: use
the full section title. Mail S3 remains historical dossier evidence, not a fresh run.

All six structural reuse cases now have inspected-input limitations and a next
observation in task Notes. In particular SCUnitPointsPacket and
SCQuestContextUpdatedPacket records in the selected consumable/quest captures contain
Packet/Opcode only, not HP/MP or objective counters. Producer build remains UNKNOWN;
file identities/hashes are in the generated triage report. This is textual/raw-corpus
review, not gameplay or human acceptance. Existing packet-only traces cannot resolve
those state questions merely by repetition.

Verification at unchanged HEAD plus the documented dirty tree: Python **40/40**,
0 failed/skipped; isolated mapper regeneration and scoped whitespace check passed.
No gameplay code or live state changed; no new .NET gate, deployment or independent
review claimed. The failed full gate remains the last full-gate result.

### Observation surface and next implementation boundary (2026-09-23)

Source at the unchanged HEAD plus concurrent dirty gameplay work exposes:

| Required consequence | Existing surface | Remaining boundary |
| --- | --- | --- |
| Recovery | ActorObservation Hp/MaxHp/Mp/MaxMp, BagItemCounts | Item identity, combat state and passive-regeneration attribution; no causal claim from a delta alone |
| Banking | BagItemCounts, BankItemCounts, Money, BankMoney | Counts aggregate by template; instance fidelity needs richer state |
| Quest progress | ActiveQuestIds; E2E-only drive questState returns step/status/objectives | Match quest/actor/wake identity; bridge reads are fixtures, not autonomous behavior |
| Mounted travel | Position and IsMounted in the current dirty source | Concurrent implementation, not this tooling change; deployment not verified |
| Party assist | InParty, PartyOwnerId, PartyLeaderObjId/TargetObjId, CurrentTargetObjId | Link the decision and authoritative action consequence |

**Concrete constraint:** `BotActionCommandQueue.Execute` applies its busy-request
gate to Observe as well as gameplay actions. A live API-owned request causes rejection;
a live world-owned request is interrupted before Observe executes. `GameplayActor.Observe`
then creates/completes an actor request. Thus a query endpoint is not automatically
passive. Do not use repeated `/api/actors/observe` calls as background sampling for an
autonomy gate. A proposed collector was removed before any live use after this review;
no collector or gameplay changes are included.

Next bounded implementation question: obtain an immutable observation on the existing
execution boundary without interrupting/replacing the active ActorRequest. Review the
current actor/request ownership before choosing a seam; do not merely skip the queue's
busy check. Acceptance must show the same active request, continued movement/action,
no added Interrupted audit, and coherent snapshot state. This is a proposed follow-up,
not authorization to overlap Muse's queue/actor edits or a claim the behavior is fixed.
Saved observations can already be inspected without this change.

Provenance: read-only MCP `list_sources` then `read_file` for
`AAEmu.Game/Core/Managers/Bots/{ActorObservation,BotActionCommandQueue,GameplayActor}.cs`
and `AAEmu.Game/Services/WebApi/Controllers/BotActionController.cs`; all ok and
untruncated, source_id/version null, paths under `/root/aaemu-dev/`. Raw responses:
`/tmp/aaemu-observation-surface-archaeology.jsonl`. Textual/code finding only;
not live reproduction. Canonical DB baseline and acceptance dossier above apply.
Five task notes now carry this observation mapping and the sampling constraint.
