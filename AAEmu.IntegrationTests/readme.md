# Setting up Integration Tests

1. Create a copy of ExampleConfig.json and save as Config.json
2. Update this new "Config.json" property to be "copied if newer"
2. Edit the Config.json %wildcards% using the same configurations you did in AAEmu.Game
3. Copy your sqlite database file from AAEmu.Game Data to the IntegrationTests compiled Data folder (bin/Debug.../Data)
4. Clean up the solution
5. All ready to run integration tests

## Permanent headless short-run acceptance

`AAEmu.IntegrationTests.E2e.BotShortRunTests.BotShortRunHeadless30Min` is an
opt-in, adopt-only acceptance harness. It does not launch or repair a server.
The runner must first prepare and boot an explicitly owned, isolated lane,
then run this test against that lane. Never adopt the shared
`/root/aaemu-e2e` root or another runner's processes, containers or ports.

For the runtime layout, private credentials and boot/teardown lifecycle, see
[`Scripts/e2e/README.md`](../Scripts/e2e/README.md). Its shared defaults are
not suitable for this harness. The existing integration-test filter convention
is documented in [`E2e/Gate/README.md`](E2e/Gate/README.md).

### Owned lane prerequisites

Before boot, choose and export the private lane's `E2E_ROOT` and
`COMPOSE_PROJECT_NAME`, plus all seven explicit ports:

- `E2E_INTERNAL_PORT`
- `E2E_LOGIN_PORT`
- `E2E_GAME_PORT`
- `E2E_STREAM_PORT`
- `E2E_WEBAPI_PORT`
- `E2E_BRIDGE_PORT`
- `E2E_DB_PORT`

The already-running lane must have a ready BotDriveBridge and authenticated
bot-control API. Enable `AAEMU_PRESENCE_IDLE_WAKE=1`,
`AAEMU_QUEST_BOOTSTRAP_ENABLED=1` and `AAEMU_BOT_CTRL=1` before boot, and
provide a private `AAEMU_BOT_CTRL_TOKEN` that matches the server. Preserve
these settings in the test invocation's environment. The harness reads
`DB_PASSWORD` from the existing private `<E2E_ROOT>/.env`; do not commit or
print that password or the control token.

The runtime and canonical `compact.sqlite3` identities must match. The
configured autosave interval is in minutes and must satisfy
`interval * 60 + 5 <= 325`; the harness does not change server save settings
or force a save.

### Build and invoke

From the repository root, build the existing integration-test project in
Release, then invoke the preserved class with the owned lane's environment:

```bash
dotnet build AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release
BOT_SHORT_RUN=1 dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release --no-build --filter-class AAEmu.IntegrationTests.E2e.BotShortRunTests
```

Without `BOT_SHORT_RUN=1`, the test skips; opting out is not acceptance.
Do not substitute `--filter` or `--treenode-filter` for the class filter.

### Measured behavior and evidence

The harness provisions one fresh level-10 character at its natural production
spawn and records the ordinary body-equipment inventory baseline. That
baseline is not proof of unseeded acquisition or a full starter kit. It does
not assign quests or routes, teleport the character, stage goals, or repair
gameplay. Exactly one enrollment wake occurs before START. During the
autonomous interval it observes passively at 1 Hz and may issue at most one
declared interrupt of its own eligible Running Move.

The autonomous interval stops immediately when all seven gates are proved,
or at 1,800 seconds total, or after 300 seconds without proved progress.
Elapsed time, partial counts and movement diagnostics cannot substitute for
the required evidence. Supplied-observation JSON consumer tests and replay
smokes are not fresh live gameplay proof.

After END, the harness removes its bot from the registry, releases its owned
headless session through the ordinary leave path, verifies registry absence,
and reads the durable character state after release and again at the fixed
325-second post-END margin. It checks observed completion bits, canonical
reward deltas and inventory conservation. All seven gates in one fresh
report, durable verification, successful owned-bot cleanup and no
infrastructure/evidence failure are required together; none alone is a pass.
The runner, not this harness, owns subsequent process and container teardown.

Each invocation writes a unique
`<E2E_ROOT>/logs/shortrun-<UTC>-<nonce>/` directory containing `report.json`,
`observations.jsonl`, `actor-audit.jsonl` and, when a stimulus is declared,
`controlled-stimulus.json`. The test prints the `SHORTRUN-REPORT` path.
There is no shared `/tmp/shortrun-evidence` output alias.

This bounded harness does not prove actual reporter distance or legal reach
at turn-in, an immutable Move-to-quest parent, H, restart behavior or general
Stage 2 autonomy. Historical accepted reports remain historical; they do not
replace a fresh report from this invocation.

## Seeded public cultivation/repeat acceptance

**Parent/capability:** the bounded farming child of autonomous cultivation.
`NeedsFarmLoopE2eTests.NeedsFarmLoop_SeededPublicCultivation_TwoHarvests_OnLiveServer`
tests production headless enrollment and autonomous `needs.farm` work, not
cross-domain goal selection. The existing externally-woken no-farm diagnostic
is separate and must not be included in this invocation.

**Ownership/setup:** the sole runner prepares and boots a fresh, explicitly
owned isolated lane using the private-root/project/seven-port requirements
above. The harness adopts it; it never calls `E2eStack.EnsureUp`, restarts,
publishes, repairs, or tears down shared processes. Declare before boot:
`AAEMU_NEEDS_FARM_ENABLED=1`, `AAEMU_QUEST_BOOTSTRAP_ENABLED=0`,
`AAEMU_PRESENCE_IDLE_WAKE=1`, bot control/bridge enabled and a private matching
`AAEMU_BOT_CTRL_TOKEN`. Homestead's actual module `Enabled` must be false;
priorities remain unchanged. Pin effective `World.GrowthRate=10`,
`World.LootRate=1`, ordinary labor consumption and `World.AutoSaveInterval=5`.
The declared ordinary persistence margin is `5 * 60 + 25 = 325s`.
The passive server projection must corroborate these rates/gates, autosave
interval and actual `DropRateMul=0` before START.

The disclosed fixture is one fresh ordinary level-one body, **three** potato
seeds (`15659`), labor `100`, no calves, and pre-enrollment staging off soil
near the Lilyut public farm (ground-height correction only before enrollment).
Fresh `HeadlessSession.Provision` uses ordinary
`AccountManager.GetAccountDetails` initialization, then a second authoritative
read/actual-account-ID check and `Character.InitializeLaborCache` before its
existing body save/activation. Never use the first INSERT's incomplete return
as persisted labor, invent defaults or create a fixture account row with SQL.
Before rigging, a passive unseeded-body snapshot must agree with its actual
game-account DB labor and owned character/account identity, with no actor or
enrollment. This records the ordinary configured default before the disclosed
labor100 override; it is not earned acquisition or an extra gameplay request.

Record all ordinary body/item instances, actual coins/labor and crop/bag/DB
baseline **before** the one enrollment wake. Preserve its returned snapshot
and the later START timestamp: enrollment may already start an approach Move;
that request is labelled pre-START, never disguised by re-baselining. Any
pre-START Plant/Harvest prevents acceptance. No post-START teleport, task,
route, goal, stock/repair, action script or second external wake is allowed.

**Finite acceptance:** one real own Running `OTHER:needs-farm-travel` Move
must be interrupted before planting. Require control `result_payload=true`,
the exact victim's terminal `Interrupted` **inside that control request's
requested/completed window**, a fresh different request, two actual physical
recovery samples, and the subsequent actual Plant. Samples observed before
the control-result poll remain evidence only after this exact join qualifies.
Both independently chosen crop
lifetimes need their own Plant GUID/selected decision cycle, one observed seed
cost, crop `2259` owner/public-farm/house identity, ObjId/DbId/PlantTime and DB
creation. Numeric IDs may recycle only after observed prior deletion and
later creation/PlantTime prove a fresh incarnation; ambiguous reuse fails.
For **each** lifetime require two distinct immature observations on increasing
actual decorator wake sequences (regression is a fault), with no Harvest for
that crop, then actual
phase `4457` whose **end-of-copy** server watermark precedes
`Harvest.requested_at`. Snapshot starts and successful lifecycle labels
cannot substitute. Projections are not atomic; ambiguous request/effect joins
fail. A request spanning polls is admissible only when the preceding passive
projection already witnessed its exact own Running GUID/action/target/request
time, began **strictly** after its request, and its terminal completion falls strictly
after that projection's END and before the next BEGIN; effect checks remain
unchanged. Preserve the first actual mature witness, not a later in-flight one.
All four admitted farm actions require opaque, nonempty, distinct decision
cycle identities; no string-format parsing substitutes for causality.
Actual request times must follow the prior farm completion. Selected
`needs.plant`/`needs.harvest` goal, policy and cycle must
originate before the actual `GameplayActor.NewRequest`: NeedsDecisionScenario
stages them after perception and clears pending metadata in `finally`, following
the existing QuestBehavior seam. The observer never backfills attribution.
Each audit poll must retain the previous terminal tail GUID; missing overlap
fails rather than hiding intermediate requests. Selected
request `wake_sequence` may remain null: it is disclosed, not inferred from
the snapshot's current wake sequence.

Each successful harvest also needs actual crop absence, potato `7992` gain
`2..4`, one labor spent and unchanged coins. Returned seeds and auxiliary
`19887` are optional; reconcile their actual deltas, never require a seed
return. Require **two actual harvest gains**, not merely a second Plant.
Record measured actor/crop 3D and horizontal distance at immature/mature
witnesses and post-harvest actor versus the last observed crop position.
Canonical skill `13980` has `max_range=4`; these observations are not an
atomic action-time range sample or proof of enforcement/cast/client parity.
Main reviews measured legality separately; no policy/range rule is added.

**Bounds/stopping:** boot readiness `360s` belongs to the runner; the harness
has a separate fixture/baseline `360s`. Observation is at most `900s`, with
`300s` without authoritative progress and `5s` per bridge/HTTP/SQL call.
Canonical growth phases are `60000ms + 540000ms`: at GrowthRate10 ordinary
maturity is `43.8–60s` depending on matching climate, excluding timer lateness.
There is no accelerated/170ms maturity workaround. The observer freezes END
and removes its own bot **immediately after the second applied gain**; it
does not wait for a third choice/crop. Ordinary session release, registry
absence, per-lifetime DB deletion and full inventory/body/coin/labor
conservation are checked afterward. **Both** the after-release and after-flush
DB projections must agree with frozen END effects, actual XP/ActAbility
`id/point/step`, position, and unchanged exact owned quest/completed-quest/house
rows. A later matching save cannot excuse a mismatching first projection.
The durable SELECT projects stored FLOAT coordinates with `CAST(... AS DOUBLE)`
before reading XYZ, preserving their precision under the unchanged `0.01` tolerance.
Original unseeded body versus seeded baseline, and baseline versus both final
states, must preserve unrelated item instances including all DB metadata.
The separate ordinary-save wait is the full `325s`, using a monotonic clock
anchored **after** the first after-release DB snapshot/attempt, not frozen END.
A failed first read/check still receives that full wait and cannot skip the
second capture. The unbound-identity cleanup fallback preserves the same
margin without guessing/removing a bot. Fixture/baseline retains its separate
`360s` bound. Cleanup-window effects cannot promote gates.

HTTP default buffering has a `2MiB` `MaxResponseContentBufferSize`; the existing
bridge client's opt-in `2MiB` bound checks actual ingress bytes before UTF8
decode/JSON parse. The harness reserves bytes **before** either evidence log
write, sharing `184MiB` total including a `4MiB` identity/final-report reserve.
The sole runner separately enforces `64MiB` combined process pipes and `8MiB`
bounded metadata/cleanup: combined live bundle `256MiB`. Declared bounded NLog
sidefiles are not copied into that bundle; build/preparation receipts are
separate. Cap failure yields failed END, never acceptance or cleanup promotion.
Logging exhaustion does not disable own cleanup; a bounded failure report is
retained from the reserve.

The runner may set `AAEMU_AUTONOMOUS_FARM_RUN_ID` to a fresh leaf name under the
mission artifact root. Atomic `own-provision.json` first records intent and
then the actual returned identity **before** log writes; schema contains
`e2eRoot/runId/freshProvision/name/characterId/state/recordedAtUtc/reportPath`.
`characterId=0` is intent only, never cleanup authority. A missing/mismatched
binding is an explicit fault: no guessed identity, all-bot discovery or cleanup.
The supervisor/coordinator atomically preserves its first
`E2E_ROOT/runner-cap-fault.json` (`atUtc/reason/passed:false/promotionForbidden:true`).
Its presence aborts only fixture/OBSERVE, never `finally` cleanup or reserved
report writes. The runner retains owned services for the ordinary persistence
margin and performs verified owned shutdown; SIGTERM is **not** assumed to
execute harness `finally`. The sole runner owns subsequent lane teardown.

**Non-goals:** earned acquisition, private homestead, quest-to-farm strategic
handoff, general Stage 2, H and restart proof. No source-only handoff risk or
unit/rig replay is relabelled fresh live acceptance. Canonical state remains
Main's authority. Invocation evidence is supplemental under
`.omp/state/artifacts/autonomous-farm-2026-10-09/run-<actual-UTC>-<nonce>/`
(`report.json`, `observations.jsonl`, `actor-audit.jsonl`, `own-provision.json`),
not a new status authority; report the first failed predicate without repairs.

After Main releases the sole runner (not during worker implementation):

```bash
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --nologo
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter "/*/*/NeedsFarmModuleTests/*"
dotnet build AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release --nologo
dotnet AAEmu.IntegrationTests/bin/Release/net10.0/AAEmu.IntegrationTests.dll --filter-class AAEmu.IntegrationTests.E2e.NeedsFarmLoopEvidenceTests
BOT_AUTONOMOUS_FARM=1 dotnet AAEmu.IntegrationTests/bin/Release/net10.0/AAEmu.IntegrationTests.dll --filter-method AAEmu.IntegrationTests.E2e.NeedsFarmLoopE2eTests.NeedsFarmLoop_SeededPublicCultivation_TwoHarvests_OnLiveServer
```

First use bounded compiled-host help/list discovery to prove exact MTP filters;
retain the actual argv and DLL hash. The last command requires a separate live
release. An opt-in-unset exact positive invocation can prove only skip/no boot.

The exact positive method filter is required: the preserved older negative
diagnostic still owns its historical network/`EnsureUp` path. Without the
explicit opt-in this positive test skips; a skip is not acceptance. Resolve
any real selected-dispatch attribution prerequisite before a released live
run; supplied-observation consumer tests cannot provide production stamps.