# Automatic quest testing with Jev

- Audience: Contributors and agents using the existing observer
- Last verified against: E302 advisory-runner and E307 failed-portion controls through 2026-10-16 at `develop` / `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8` plus preserved dirty work; native controller/pure-consumer and synthetic subprocess evidence, not a live gameplay run

The test runner launches an existing quest test, reads its evidence, requests
normal cleanup on a repeated invalid action or stall, and reviews the result
with Jev. Engineering can work elsewhere while the bounded process runs.
Jev receives facts and acceptance criteria; it has no gameplay or source tools.

## Current testing execution contract

Follow the [all-agent PlayerBot local-repair contract](../../PROJECT-CONTROL.md#playerbot-local-repair-contract). D117 supersedes D116's local model-access blocker: request bounded Jev plan/coverage and results assistance, record unavailable/invalid/UNKNOWN outcomes, and continue otherwise authorized local checks. Deterministic assertions and native saved state remain authoritative; Jev has no gameplay/source tools and cannot grant integration, live execution or deployment. Supplied development prerequisites do not prove an earned loop. Explicit user pauses still apply.

The D116/E300 calls below are historical evidence, not a requirement to rerun them.
Earlier food tests had no Jev; later review is not retroactive coverage. E302 below
adds isolated runner verification, not new gameplay acceptance.

The existing exported `reviewPacket`/`validatePacket` library can review bounded non-quest evidence without fake quest IDs. Main authorized temporary artifact-only invocation glue using the installed public `NativeJudge` (`@oh-my-pi/pi-ai`18.10.3) and native credential discovery; no copied evaluator or new framework. The actual historical10436 and next-wave calls used:

```bash
nice -n 10 bun .omp/state/artifacts/food-supply-end-to-end-20261014/live-receipt-review/invoke-existing-native-jev.ts \
  .omp/state/artifacts/food-supply-end-to-end-20261014/live-receipt-review/jev10436-seam-input.json
nice -n 10 bun .omp/state/artifacts/food-supply-end-to-end-20261014/live-receipt-review/invoke-existing-native-jev.ts \
  .omp/state/artifacts/food-supply-end-to-end-20261014/live-receipt-review/jev-next-wave-plan-seam-input.json
```

Both completed through typesafe/jev-preview with one HTTP attempt and zero tools. The historical verdict remains FAIL and the plan remains INCONCLUSIVE; Jev chose unknown because exact adapter/current qualification was not yet observed. Main reconciled this with exact source review and explicit offline qualification authority, not model approval. Inputs, outputs, hashes, clocks and bounds are in [the invocation receipt](../../.omp/state/artifacts/food-supply-end-to-end-20261014/live-receipt-review/jev-two-actual-native-reviews-receipt.json). Reuse the same bounded library seam for postqualification recorded-results interpretation; keep source rigs, replay coverage and actual native gameplay distinct. No service/DB start follows from a review.

The final food source-qualification wave also obtained a real recorded-results review using the same invocation glue with `live-receipt-review/jev-final-source102-results-seam-input.json`. The [actual invocation receipt](../../.omp/state/artifacts/food-supply-end-to-end-20261014/live-receipt-review/jev-final-source102-results-invocation-receipt.json) records one HTTP attempt, zero tools and unknown/INCONCLUSIVE: the offline gate passed, but actual three-prefix replay and native food outcome remained missing. That explicit coverage boundary is required; five consumer controls are not a replay test and cannot earn native food acceptance.

## What was missing

AAEmu already has native quest actions, full passive quest audits, finite headless
tests, controlled interruption, normal character release and persistence checks.
The existing `JevShadow` copies frozen decisions and candidate actions into a
bounded queue. Its replay checks repeatability, which cannot establish that a
quest succeeded. The missing connection was automated execution, assertions and
a useful final engineering report. This implementation reuses the existing tests
and their cleanup rather than allocating a new game lane or changing bot behavior.

## Evidence and decisions

`observe.py` converts existing campaign snapshots or `quest/observe` audit records
into events with run, bot, source SHA, database, timestamp, raw line and field.
`evaluate.py` determines PASS, FAIL, STALLED or INCONCLUSIVE without a model.
`triage.py` reads the preserved normalized events before their detail is lost in
the verdict projection. It retains exact event/trace IDs, timestamps and raw
artifact/line/field references, selects the earliest recorded failed terminal,
telemetry fault or evaluator threshold, and supplies competing engineer-written
explanations with distinguishing read-only checks. The first discrepancy is an
observation, not the root cause.

`jev_review.ts` uses the installed OMP native judgment client (`NativeJudge`, or
`TypeSafeJudge` on older installations) to select an authored explanation/check
or UNKNOWN. It does not ask the model to copy the deterministic status. Reports
include the selected evidence, competing checks, provenance hashes and explicit
omissions; interpretation cannot change gameplay acceptance or its evidence layer.
Malformed, missing, contradictory or oversized essential evidence abstains before
credential discovery/model invocation. Packets are capped at 16KiB; exact references
are never shortened to make them fit.

For the declared quest-251 progression scope, PASS requires increasing required
items, a matching completed turn-in request, a newly set authoritative completion
bit, and a subsequently independently selected and accepted different quest.
A completed action request alone is insufficient. Native CharacterQuests flags
are runtime evidence; SQL saved flags are persistence evidence. A quest leaving
the active list is not completion. Selection independence is declared only when
the test setup was checked for assigned quests or targets.

Hunt-only tests opt in with `acceptance.objective =
{"mode":"native_hunt","act_id":26549,"required_progress":1}` and may omit
`item_id` and `item_count`. The declared threshold is a minimum witnessed native
counter value, with a positive increase during the window; it does not replace
the ordinary server's completion rules. Turn-in terminal, new completion bit and
independent accepted follow-up remain required. Item tests retain their existing
criteria when `objective` is omitted.

The existing campaign collector's bounded `log_lines` supplies native
`QuestObjectiveTargetDiag` counters. The adapter joins character, actor, quest
and act, establishes a baseline, and preserves raw line/hash/cycle/timestamp.
Initial positive counts, movement, attacks and deaths alone are insufficient.
Missing/truncated windows, actor changes or regressing counters fail closed.
The native bridge audit currently lacks act-specific counters and explicitly
returns a telemetry gap for hunt mode; use the owned snapshot collector.
Jev receives the bounded confirmed counter evidence and cannot change the verdict.

Movement alone does not reset meaningful objective progress. Repeated invalid
requests must match action, quest and failure reason. Fresh unchanged wake
sequence can indicate a scheduler stall; it does not prove the underlying cause.
Missing identity, broken audit continuity, contradictory outcomes, malformed data
or incomplete telemetry cannot establish PASS. Thresholds belong to the manifest.

## Run an existing owned test

First use the established private E2E lane procedure in
[the test infrastructure](../../Scripts/e2e/e2e-common.sh). Check ownership,
source/binary receipts, canonical assets, credentials and the explicit bot name.
This command does **not** provision a server, change the testing deployment,
or authorize use of another person's lane. Never use the sole-bot scheduler
shutdown test against the shared 30-bot deployment.

Create a manifest from [the example](../../Scripts/quest-test-observer/examples/manifest.json).
Use the actual source SHA, database identity, character ID and bot name, run ID,
start time, `input_format: "bridge-quest-observe"`, `collection_mode: "live-follow"`
and evidence layer. Add `native_contract` with the expected scenario, canonicalMd5,
runtimeMd5, PublishSourceIdentity, testAssemblySha256 and runtimeGameSha256
from the qualified build/lane receipts. Database identity is
`127.0.0.1:<port>/aaemu_game@<absolute E2E_ROOT>`. The final native report must
match these fields, bot name/ID and source HEAD before aggregate PASS. Native
runId is separately recorded; it is generated by the existing harness. Confirm `choices_unassigned` from the scenario setup. Do not
use the synthetic example's identities for a live run.

With the existing lane already ready and its environment configured:

```bash
BOT_PRODUCTIVE_RUN=1 python3 Scripts/quest-test-observer/execute.py \
  --manifest /private/run/manifest.json --out /private/run/new-results \
  --seconds 1700 --cleanup-seconds 500 -- \
  dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
  --configuration Release --no-build \
  --treenode-filter '/*/*/BotShortRunTests/ProductiveQuestHeadless15Min'
```

The wrapper gives this existing harness a new evidence directory and cooperative
stop-file path. On failure/stall it requests exit from observation, then waits
for the existing finally block: stop the owned actor, capture full stopped state,
release it and check ordinary persistence including the 325-second flush period.
It never treats the observer's narrower PASS as permission to bypass the original
recovery, cleanup or persistence assertions. Gameplay acceptance requires the
native test and observer to pass. Reviewer availability is recorded separately;
an API failure, invalid answer or UNKNOWN cannot change that gameplay verdict.

The supervisor owns only processes it starts, records PID/start identity, reaps
them, caps duration/output/RSS, and never retries a campaign. A hard resource cap
or exhausted cleanup budget can force termination; that explicitly leaves cleanup
unproved and forbids PASS. Check the owned lane before subsequent work. Existing
outer lane supervisors remain responsible for their own Login/Game/MySQL cleanup.
Native budgets require at least 1700 seconds plus a 500-second cleanup grace.
Any outer supervisor must allow this budget and final review (at least 2300
seconds); do not put this wrapper under an older 1700-second hard kill.
Commands should remain foreground processes; do not daemonize test grandchildren.

Jev runs once after deterministic observer conclusion, provisionally while the
owned native test/ordinary cleanup continues when possible; otherwise after final
conclusion. `JEV_REVIEW_STARTED` and `JEV_REVIEW_READY` report the review phase and
cleanup state. The final execution receipt separately records native assertions,
observer status, cleanup, reviewer status and reaped children. Pending cleanup is
never presented as verified acceptance. There is no automatic second review.

Review packets and calls are bounded. Native judging has a 15-second limit; the
runner caps reviewer input separately at 1MiB, and the reviewer at 20 seconds,
its configured RSS limit and 64KiB per output/error stream, including after exit.
Reviewer files do not consume the native test's output budget. Reviewer resource
failure stops only that owned reviewer, not ordinary native cleanup. The installed
SDK's transient-error retry is explicitly disabled for this path; endpoint redirects
are refused rather than followed. At most one HTTP attempt is allowed per review.
Credentials come from OMP auth storage, never command arguments or report output.
Backend errors and arbitrary model prose are not copied into reports.

Artifacts include original observations, normalized events, criterion/stall
notifications, deterministic verdict, native report, raw/prefix/event-file hashes,
execution custody and Jev report. `status` remains deterministic; `review_status`
is `completed`, `abstained`, `unavailable` or `invalid`. Exact source-bound evidence
and competing checks remain visible even when the chosen explanation is UNKNOWN.
Saved-evidence review is explicitly not a new gameplay observation.

## Failed-portion development and fresh acceptance

`execute.py --loop-mode development` uses the native loop's observed portions,
not the quest evaluator. The first failed portion requests normal cleanup; the
wrapper never retries automatically. Each attempt has a new output directory.
`report.json` must contain `passed` and the exact final `loopExecution` receipt.
The checkpoint binds source/binaries, reference data, fixture and character.

For a new adapter, use `LoopDevelopmentRun.Begin` **before mutation**, mark only
observed portions complete, and call `Finish` after owned cleanup and saved-state
verification. The adapter must reject ambiguous partial effects; a matching
snapshot alone does not authorize reissuing a paid action. Do not import old
actor handles or replay old requests.

The manifest uses the identity/source fields described above; in loop mode
`database_id` is the native adapter's actual stable fixture identity (the food
adapter uses the database UUID), not a fabricated quest identifier. Optional
`native_contract.portions` binds the ordered portion names before launch.
The wrapper supplies `AAEMU_LOOP_MODE`, `AAEMU_LOOP_RUN_ID`, `AAEMU_LOOP_OUTPUT`,
`AAEMU_LOOP_STOP_FILE` and, for explicit continuation,
`AAEMU_LOOP_RESUME` / `AAEMU_LOOP_RESUME_SHA256`. Pass the issued run ID to native
`Begin(..., invocationId: ...)`; copied success receipts from another invocation
cannot satisfy this launch.

After fixing the demonstrated defect and running its focused checks, invoke the
same native test with a new manifest containing the new source/binary identities:

```bash
AAEMU_FOOD_SUPPLY_PROOF=1 python3 Scripts/quest-test-observer/execute.py \
  --loop-mode development --resume /private/run/failed-attempt \
  --manifest /private/run/fixed-manifest.json --out /private/run/retry-prepare \
  --seconds 1700 --cleanup-seconds 500 -- \
  dotnet test --test-modules AAEmu.IntegrationTests.dll \
  --root-directory /private/qualified/test-modules \
  --filter-query '/*/*/FoodSupplyEndToEndE2eTests/PaidSeed_NativeGrownGrain_OrdinaryBread_RecoveryAndIndependentRefill'
```

Admission verifies the previous execution finished without forced termination,
its checkpoint hash and matching identities. The native test then re-reads actual
state before adoption. A changed wallet, item identity, fixture or unresolved
effect refuses continuation; the wrapper cannot inspect the world on its behalf.
`DEVELOPMENT_PASS` is successful development continuation, **not full acceptance**.

The food adapter is the first integration. A first development invocation may
validate the existing historical prefix, or use an explicit fresh declaration:
`AAEMU_FOOD_INITIAL_STATE` points to JSON containing `actor`, `observer` and
`declaredGrowthRate`; actor/observer use the existing saved-state field names.
`AAEMU_FOOD_INITIAL_STATE_SHA256` binds those bytes. This declares observed state;
it does not create, reset or grant a fixture. Fresh acceptance uses
`--loop-mode acceptance`, **no `--resume` or historical prefix**, and that separate
declared initial fixture with no acquired food-chain inputs.

Food continuation requires either matching passive character state or authoritative
proof that the retained actor is unloaded and cannot materialize unexpectedly,
followed by cache validation after ordinary adoption and before policy wake.
No active lease/wake, unresolved economic effect or remaining crop is transferable.
Unknown settlement is non-resumable rather than permission to erase or repeat it.
Actual farm growth, healing, refill and persistence still use ordinary assertions
and timing. No command here releases the explicit service/earned-state hold.

The food adapter's passive unloaded-actor probe reads the full bridge response
envelope; ordinary `Call`/`CallAsync` still reject bridge errors. Absence requires
the exact resolver miss, complete owned Dormant registry entry, disabled automatic
materializers, zero scheduler work and the permitted connection count—not a
population field absent from the real metrics response.

Cleanup uses ordinary bot removal, which owns exact retirement before leave/save.
Do not submit a separate `Stop` action against busy native work. Keep the
325-second passive settlement and saved-state/ledger checks: removal success
alone does not prove persistence. Saved position disagreement must refuse a
checkpoint; never widen position bounds or restore a row to conceal load loss.
For unloaded admission, the current food adapter supports the owned Linux
`dotnet AAEmu.Game.dll` launch with readable process mappings and both retained
configuration files. Binary/configuration change times must precede Game's
startup second. The actual automatic-population gate readers combine their
specific environment/config inputs; a false local value is not assumed to
override an enabled base value. Unknown proof refuses rather than changing flags.

Loop execution records the quest-only Jev CLI as `not_applicable`, with its
reason. Use the existing generic `reviewPacket` seam above for bounded advisory
plan/results review; do not invent quest IDs or pretend that review ran.
Legacy quest execution is unchanged when `--loop-mode` is absent.

## Advisory-runner verification — E302

[E302](../../.omp/state/EVIDENCE.md#e302--advisory-jev-runner-verified-with-controlled-processes)
records 76 passing Python checks, 12 passing Bun checks and nine actual
`execute.py` command-line scenarios with controlled synthetic test/reviewer inputs.
The existing supervisor, observer/evaluator and UNKNOWN review adapter were used;
the external SDK transport was supplied, with no credential discovery or network
request. These checks do not prove a real provider outage or gameplay success.

A reproduced defect let valid non-object reviewer JSON (`[]`) interrupt the
supervisor after the test succeeded. It now records `review_status: invalid`
and preserves the deterministic result; a real-subprocess regression covers it.
Reviewer launch failure, completed UNKNOWN, invalid output and reviewer
deadline/output/RSS limits leave the test result authoritative. A favorable
review cannot reverse a test failure; missing observations stay INCONCLUSIVE.
The output-limit scenario exercised review during ordinary synthetic cleanup.
Forced test termination still records cleanup as unproved, never PASS.

## Fast checks and historical evidence

```bash
python3 -m unittest discover -s Scripts/quest-test-observer -p 'test_*.py'
bun test ./Scripts/quest-test-observer/test_jev_review.ts
python3 Scripts/quest-test-observer/run.py observe \
  --manifest Scripts/quest-test-observer/examples/manifest.json \
  --snapshots Scripts/quest-test-observer/examples/events.jsonl \
  --out /tmp/new-quest-observer-example
bun Scripts/quest-test-observer/jev_review.ts /tmp/new-quest-observer-example/verdict.json
# For an aggregate review verdict outside the observer directory:
# bun Scripts/quest-test-observer/jev_review.ts /private/run/review-verdict.json \
#   --events /private/run/observer/events.jsonl
```

The example is simulated evidence, explicitly labelled. PASS still needs the
declared authoritative effects; a sampled Completed actor request is not credit.
Older historical verdicts using retired criterion fields abstain; replay their
unchanged normalized events through the current deterministic evaluator in a new
directory rather than rewriting the old report or adding a second acceptance rule.
The replay retains its original labels and is not a fresh live run.
Historical receipts: `.omp/state/artifacts/jev-test-observer-20261012/`.
Bounded triage receipts: `.omp/state/artifacts/jev-failure-triage-20261013/`.

Validation includes success, repeated invalid actions, movement without objective
progress, a scheduler stall and missing telemetry. Automatic process tests prove
cooperative stop, cleanup, reaping and refusal to override a native test failure.
Actual Jev calls reviewed representative evidence without gameplay tools.
Historical E163 native audit replay proves the narrower progression assertions;
**the original E163 overall FAILED remains FAILED** because its recovery contract
was stronger. E192 rolling brain history lost continuity and was correctly
INCONCLUSIVE rather than silently accepted.

The movement fixture ending at 20 seconds did not cross its 30-second inactivity
threshold and correctly stayed INCONCLUSIVE. A separate extended fixture crosses
the same threshold and produces STALLED; the first result is preserved.

## Observation effort and limits

The historical E163 stream contains 892 observation snapshots over about 895
seconds. The evaluator reduces those to four meaningful notifications and runs
in a few seconds on preserved evidence. Jev reviews one bounded summary instead
of receiving a request for every snapshot. A separate streaming proof kept its
supervisor running after launch while Main performed source work. This measures
records and notifications; it does not measure human minutes saved or make fresh
live gameplay faster. Normal persistence waits still apply.

The runner has been exercised with simulated test processes and historical
authoritative gameplay evidence. D109 additionally proves real bounded native
judgment on five labeled synthetic cases and preserved E280 evidence, zero-call
missing-telemetry abstention, provisional reporting during synthetic cleanup,
and actual installed-SDK retry suppression against a synthetic local HTTP503.
These do not prove failure-cause accuracy, engineer time saved, fresh live shadow
coverage, human/client acceptance, autonomous-loop or parent completion. No server
deployment or gameplay intervention is part of this reporting change.

The [Felworld source](https://github.com/felworld/azerothcore/blob/139dca9f2e10a8a676d7b81b823cc2f6cfe651b9/src/server/game/Felworld/FelworldEvents.cpp)
records per-character game events, while its
[LLM trace schema](https://github.com/felworld/mod-llm/blob/eed19041ddd5ffbb9fa4c5b17e0b909e385ae796/data/sql/db-characters/base/mod_llm_trace.sql)
records model requests and responses. Those support the same useful distinction:
record a decision and its actual outcome separately. These are external source
patterns, not AAEmu gameplay authority or evidence of Felworld live behavior.
