# AAEmu project control

**Placement:** this is the repository-root working page for the current control
rollup. `STATUS.md`, `ROADMAP.md`, `SCORECARD.md`, and `EVIDENCE-LEDGER.md`
are the authoritative records; this page links to them and keeps the objective
QAT/UAT queue in one place. It does not replace dated reports or the historical
wiki status page. For human-facing wiki navigation, start at
[`Docs/wiki/Home.md`](Docs/wiki/Home.md).

- **Project:** AAEmu fork (`joshhmann/AAEmu`), ArcheAge server emulator
- **Target:** ArcheAge 1.2 client and reference data `compact.sqlite3` r208022
  (read-only)
**Documentation HEAD (2026-09-12 reconciliation):** `c7351b242e4088e0db5ef7926659a948312cb2fb`.
That checkpoint is historical. Current delivery direction is the 2026-09-15
[outcome-first queue](ROADMAP.md#current-delivery-direction), with M8 → M9 as the horizon;
M8 has a qualified engineering/runtime exit, not human acceptance. Older source/test
checkpoints and per-slice counts below remain historical unless re-run; they are not
restamped as fresh results here. Current evidence narrative: [`STATUS.md`](STATUS.md);
current direction and requirements: [`ROADMAP.md`](ROADMAP.md).

## Delivery contract

Current operating direction — 2026-09-15. This section governs new dispatch;
dated evidence and locked milestone exit requirements below remain intact.

**Plan broadly; execute by observable outcome.** The hierarchy is product commitment
→ player journey → existing milestone/capability → bounded slice → evidence.
"Finish combat", "finish GOAP", and "make the village alive" are initiatives, not
executable cards. Conversely, one card per class or method hides integration risk.
A slice should carry one meaningful behavior across the layers it actually needs.

### Repeatable scope-to-delivery process

The [shared contract](.kanban-templates/implementation.md) applies to every zone,
feature, mechanic and service, not only the farm journey. Create a parent scope brief
with supported variants, evidence inventory (archaeology, code/tests, graph and
human/bot corpus), contradictions, missing requirements and ordered slices. Child
cards reference that brief and prove one outcome. Unknown facts receive bounded
research/capture tasks; they are not bypassed in implementation.

Each expansion records shared evidence reused, local differences and required
revalidation. Global defects have one owner and are linked from affected zones.
Closure names the supported scope and evidence layer; the next zone never inherits
a completion claim automatically. Reuse evidence while its dependencies hold;
avoid repeating whole-domain research for every child card.

### First concrete product journey

Current bot delivery target: [one ordinary starter → one owned small farm plot →
first harvest and repeat](ROADMAP.md#first-owned-small-plot-journey), not a house,
construction-pack economy or ten-bot village. GOAP is the intended choice mechanism;
a deterministic reference scenario proves the same ordinary actions without faking
their results. Follow the
[common implementation template](.kanban-templates/implementation.md) for all domains.
Canonical eligibility and existing service gaps determine the next slice, not a
speculative goal chain. Data/code verification is not loop completion.

### Required slice brief

Before coding, record these fields in the existing card/dossier, not a new parallel plan:

| Field | Required answer |
|---|---|
| Parent and outcome | Existing milestone/track, player journey, and one observable change. |
| Baseline | Exact source SHA + relevant dirty state; what is proved, suspected, or unknown. |
| Scope | In-scope paths, explicit non-goals, ordinary gameplay path reused. |
| Preconditions | Named starting state; fixture seeding versus naturally acquired resources. |
| Acceptance | Happy path, applicable refusal/interruption/recovery, persistent consequence; commands/scenarios and evidence layer. |
| Dependencies | Concrete blocking behavior or prerequisite artifact, not a vague milestone dependency. |
| Ownership | One implementer, independent verifier, tracker; deployment coordinator only when needed. |
| Stop and handoff | Exit evidence, remaining limitations, exact next slice; no autonomous scope expansion. |

**Ready** means the outcome and verification are bounded and dependencies available.
**In progress**, **implementation complete / verification pending**, **verified at a
named layer**, **deployed**, and **human accepted** are distinct states. A blocked
slice names the blocker and smallest unblock action. Do not label a card Done when
its required verification is still pending, or promote its parent automatically.

For bot cards, additionally record perception → choice → ordinary action → observed
postcondition. A planned effect is not proof of success. Use the existing action
lifecycle and scheduler; preserve intent if needed, never resurrect a terminal request.
Seeded replay proves execution under those preconditions, not autonomous acquisition.

### Evidence claim boundary

The [2026-09-16 corrective queue](ROADMAP.md#high-priority-corrective-queue--2026-09-16)
is high priority and supersedes new homestead/GOAP breadth. See
[AGENTS.md](AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16) for enforcement.

Every handoff states implemented / verified layer / not proved / fixture or bypass
steps / exact source and dirty state / command and environment / artifacts /
independent review / deployment state. A synthetic successful sequence is a legitimate
result only when labeled synthetic; no evaluator, dashboard or prose can promote it
to actual gameplay. Reviewers check provenance and actual state consequences, not
just green counts. Missing live evidence stays UNKNOWN. Existing achievements stand
within their original scope.

For a complete-loop claim, attach a leg-by-leg checklist: starting means/acquisition,
discovery, legal reach, ordinary action, verified consequence/conservation, recovery,
repeat, and applicable persistence; client/H evidence stays separate. Each leg has
evidence status and a concrete missing dependency/next task. Mark non-applicable legs
with a reason. Do not confuse unknown verification with absent implementation.
If any required leg is missing or unproved, the headline is **LOOP INCOMPLETE**, with
the specific gaps listed. Seeded subloop success does not close the complete livelihood.

### Priority, ownership, and cadence

- Josh owns product tradeoffs, declared supported journeys, and named human verdicts.
  Prepare short runnable human packets; do not ask Josh to diagnose every code failure.
- Tai implements; Rei independently verifies; Nei reconciles records; Mai coordinates
  blockers and authorized deployment. Role names do not imply signoff already occurred.
- Dispatch one active implementation slice per worker. Parallel work requires disjoint
  ownership and safe test resources; never run competing resets on the same E2E database.
- Prioritize loss/duplication/exploit/crash/deploy safety, then blocked progression,
  rule/conservation correctness, breadth, and polish. A client interaction blocker is
  functional correctness, not optional polish.
- At dispatch, check the newest source and evidence. At handoff, report actual versus
  expected outcome, changed claims, open risk, and next action. At each completed delivery
  wave, reconcile STATUS/ROADMAP/SCORECARD and the dashboard together where affected.
- Review broad journey coverage weekly or after a meaningful exit: what can a human now
  do, what can a bot now do without intervention, what still fails, and what is next?
  Preserve successful foundations; reduce repeated repair and uncertainty, not just backlog size.

Keep a small Ready queue; split later work only when its dependency is understood.
The queue is not permission to commit, push, deploy, or mutate external services.

## Authoritative records

| Record | Use |
|---|---|
| [`VISION.md`](VISION.md) | Human playability + living-world commitments, delivery lanes, independent acceptance (not test verdicts) |
| [`LIVING-WORLD.md`](LIVING-WORLD.md) | Living-world philosophy and architecture reference (not test verdicts) |
| [`STATUS.md`](STATUS.md) | Current fork checkpoint, milestone narrative, open human gates, and recent reconciliations |
| [`ROADMAP.md`](ROADMAP.md) | Locked milestone requirements, deferred validation gates, and next-wave objectives |
| [`SCORECARD.md`](SCORECARD.md) | Mechanic evidence dimensions and conservative current scope |
| [`EVIDENCE-LEDGER.md`](EVIDENCE-LEDGER.md) | Append-only milestone evidence states and human-feel boundary |
| [`PLAYERBOT_BLOCKER ledger`](scorecard-explorations/playerbot-blockers.md) | Active bot blockers and retained resolutions |
| [`PlayerBot Capability Matrix`](scorecard-explorations/mechanics/playerbot-capability-matrix.md) | Perceive / Decide / Act / Verify and autonomous-loop view |
| [`PlayerBot Progression Ladder`](scorecard-explorations/mechanics/playerbot-progression-ladder.md) | Canonical P → B → L → A → N → G → T grading frame, sprint classifications, loop dependencies, golden path (planning/docs only) |
| [`PlayerBot Progression Framework`](PLAYERBOT_PROGRESSION_AND_TESTING_FRAMEWORK.md) | Architectural audit, visual feature matrix, farming H-gate root cause diagnosis, and GOAP rollout policy |
| [`PlayerBot Target Architecture v1`](PLAYERBOT_TARGET_ARCHITECTURE_CONSOLIDATED_V1.md) | Ratified Option A minimal extension architecture, B2 thresholds, 14 locked invariants, Phase 0 evidence/integrity gates |

### Scope map

The hierarchy below is authoritative for current planning and reporting:

- **M0–M10 (including .5 steps)** — numbered milestones; current direction is M8 → M9.
- **Post-M7 readiness and closure** — a continuing cross-cutting umbrella, not a numbered
  milestone. It contains capability/blocker tracks, acceptance gates, and implementation slices.
- **PB-001, PB-002, PB-005, PB-007** — capability/blocker tracks.
- **A3, A4, A5** — population/scaling acceptance gates.
- **Slices** — implementation units within a track or gate.
- **H** — a separate human/client acceptance lane, not a global freeze on unrelated work.
- **Lane D world-systems / undefined-mechanics rows (2026-08-31 census)** — gap tracks/findings,
  not milestones and not M8; classification is per-row (truly undefined, data-only/hardcoded
  mismatch, partial/undefined dispatch, data-only/unwired, existing omission) — see the
  classification sentence in the [ROADMAP.md](ROADMAP.md) Lane D bullet.

See [`ROADMAP.md`](ROADMAP.md) for formal milestone entries and [`STATUS.md`](STATUS.md) for the
current evidence narrative.

### Dossiers and reports

- Dossiers: [navigation](scorecard-explorations/mechanics/navigation-domain.md),
  [justice](scorecard-explorations/mechanics/justice-domain.md),
  [pvp](scorecard-explorations/mechanics/pvp-domain.md),
  [ships](scorecard-explorations/mechanics/ships-domain.md),
  [mail](scorecard-explorations/mechanics/mail-domain.md),
  [dominion](scorecard-explorations/mechanics/dominion-domain.md),
  [economy](scorecard-explorations/mechanics/economy-domain.md),
  [indun](scorecard-explorations/mechanics/indun-domain.md),
  [undefined world-mechanics](scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md),
  [A5 physics/tick stall investigation](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md).
- Archaeology MCP development-cycle checkpoints (before coding: source/catalog/version inventory; during coding: source/data cross-reference and relationship/acceptance query; before merge: MCP build + focused security tests + archaeology stdio smoke; after merge/periodic refresh: acceptance dossier and md5/provenance review): see [AGENTS.md](AGENTS.md) "Archaeology MCP — development-cycle checkpoints". The canonical one-command pre-merge check is `./scripts/archaeology-cycle.sh` (builds + archaeology unit tests + full smoke), run alongside `./scripts/gate.sh`. `./scripts/gate.sh` runs the existing BotControl smoke (4/5) plus the **lightweight archaeology gate smoke** (`Scripts/mcp-archaeology-gate-smoke.sh`, 24 tools, 5/5 — no game_pak/MySQL/archaeology unit tests); the **full** archaeology smoke (`Scripts/mcp-archaeology-smoke.sh`) and the archaeology-focused unit tests are **not** duplicated in `gate.sh` — they run only in `archaeology-cycle.sh`. **Contributor contract:** contributors MUST invoke archaeology when investigating/changing source, schema, protocol, client-data, quest/objective, item/skill/NPC/mate/vehicle/world/physics behavior, or any change depending on a reference-data fact; ordinary unrelated changes MAY skip it. Tool/source routing, the evidence contract (HEAD, source_id/path/version, query inputs, confidence label, truncation/bounds, canonical DB md5, data/code vs live/client/H), and the required pre-merge `./scripts/archaeology-cycle.sh` alongside `./scripts/gate.sh` are defined in [AGENTS.md](AGENTS.md) "Archaeology MCP — development-cycle checkpoints".
- Current or recently reconciled reports: [G2-A3 wake storm](scorecard-explorations/generated/g2-a3-storm-report.md), [G2-A5 acceptance](scorecard-explorations/generated/g2-a5-acceptance-report.md), [PB-007 handshake](scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md), [PB-002 leveling loop](scorecard-explorations/generated/leveling-loop-2026-08-25.md), [NPC grounding audit](scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md), [rowboat report](scorecard-explorations/generated/ships-rowboat-e2e-report.md), and [integrated MCP benchmark](scorecard-explorations/generated/integrated-mcp-e2e-benchmark-2026-08-27.md).
- Undefined world-mechanics census (2026-08-31): [undefined-world-mechanics-2026-08-31.md](scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md) — read-only data+code dossier (HEAD `0f8254dc3d914193d432fb842169e9bb07075508`, DB md5 `78b3bdbf038db3b927056106efdf91af`, 1.2 r208022) identifying four new high-confidence ledger gaps (AGGRO-PACK-01, RESPAWN-LADDER-01, AUCTION-BANK-DOODAD-01, NPC-INTERACTION-01), refreshing BOOK-01 (verified unwired), formalizing INDUN-01 (existing dossier, Lane D slices S1-S4), and recording exploration-only/medium/rejected surfaces.
- Human packets: [`Docs/JOSH-QAT-WAVE4.md`](Docs/JOSH-QAT-WAVE4.md) and [`docs/JOSH-QAT-PACKET-M4-M5.x.md`](docs/JOSH-QAT-PACKET-M4-M5.x.md). They are instructions, not verdicts.

### Setup, development, and testing

- [Installation and setup](Docs/wiki/Installation-&-Setup.md) · [Aspire development](Docs/wiki/Aspire-Development-Guide.md) · [dependencies and downloads](Docs/wiki/Dependencies-and-Downloads.md) · [development conventions](Docs/wiki/Development-Conventions.md) · [client](Docs/wiki/Client.md)
- [Testing plan](Docs/TestingPlan_en.md) (Tier 1 gate, targeted E2E, and soak evidence rules) · [testing progress](Docs/TestingProgress_en.md) (historical March 2026 snapshot; not a current count)

## Milestone and objective state

This is a short index, not a second status narrative. See [`STATUS.md`](STATUS.md)
and [`ROADMAP.md`](ROADMAP.md) for dates, requirements, and historical context.

| Objective | Current state at the evidence date |
|---|---|
| M1 Quest/progression spine | Closed on automated evidence; the Solzreed human route remains Josh-owned and open. |
| M2 Golden-path baseline | Deterministic docs/test reconciliation at source/test HEAD `ba530bcebec12af2bc7dc0db7451a535665bbed3`: clean reset → ordinary golden-path baseline quest/progression → required first-mount/baseline state → restart/clean-state persistence verification. Focused A/R proxy run: 32/32 passed, 0 failed, 0 skipped — `HeadlessSessionProvisioningTests` 8/8, `M1M2ReplayScenarioRigTests` 3/3, `M1M2ReplayCastWindowRigTests` 1/1, `PlayerbotPilotTests` 6/6, `QuestScenarioTests` 12/12, `QuestScenarioTierTests` 1/1, `QuestDataCensusTests` 1/1. `PlayerbotPilotTests` 30/30 cycles and restart 2/2 are ordered-manifest/contract proxy evidence; `M1M2ReplayScenario` is fixed 16-quest ordering and its mount criterion reports no real mount. `QuestScenarioTierTests` method pass is not M2 full closure: observed census 4463 PASS / 110 FAIL / 14 SKIP over 4587, with T1 failure quest 6280; remaining findings are historical evidence findings. Player closes loop = Unknown/H open; Bot closes loop autonomously = Unknown/Open. Original two-player/no-GM human baseline remains Josh-owned and open. |
| M4 Trade/craft/transport | Engineering chain is implemented, but Player/Bot loop closure remains Unknown/Open. The clean ordinary `Character` loop is gather/harvest → craft pack → carry/place → load owned vehicle → drive normal route → unload → sell specialty pack for reward → repeat, with per-object restart/persistence as applicable. `SellSpecialty` now composes the canonical CSSellBackpackGoodsPacket → SpecialtyManager path with ordinary merchant/pack checks, pack-consumption postcondition, same-zone/no-pack refusal, repeat-cycle, and idempotency coverage. Current source/test HEAD `6ff68e1bb4a6afe08441308acb9a485b5133c42e`; current focused results are M4ExitIntegratedSessionTests 2/2, EconomyDayCycleScenarioRigTests 4/4, and M3aM4ReplayScenarioRigTests 2/2. Full normal-clone gate at this HEAD: 2498 total / 2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools; skip `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG` + `AAEMU_E2E_DB_PASSWORD`. Forced rebuild report: 1067 warnings / 0 errors. Existing replay is ordered scripted/fixture proxy with direct setup shortcuts outside authentic acceptance; shared E2E reset is unsafe, so no live M4 restart/vehicle proof is claimed. Human/client QAT remains open. |
| M5 Gameplay Actor Contract | `BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` landed at `263ecc66c474ca1c5f4b085e86ef3e47f49fd1`; bounded decision primitive integrated into `LevelingLoop` accept choice; focused contract 5/5. Hard legality precedes deterministic fixed-priority/personality/tie-break selection and terminal postcondition; broad M5 policy/universal autonomy remains open. |
| M6 Deterministic playerbot framework | Recorded lifecycle evidence stands: cancellation `950cfd279`, population isolation `c97909f4f`, and opt-in six-hour leg `155c82c66`; B4 metadata persistence recorded; A5 is CLOSED per the 2026-09-08 conjunction (see [`ROADMAP.md`](ROADMAP.md) and [`STATUS.md`](STATUS.md)). M6 full-exit remains unclaimed; H/UAT separate. |
| M7 Adventurer/party bots | Recorded A/R evidence **147/147** (primary 36/36 + actor support 111/111) stands as scoped evidence including the historical bounded `LevelingLoop` 254→255 slice plus later recorded PB-COMBAT/BAG/MOUNT, Dewstone/interzone/death-recovery, and Q6 live-leg extensions — see [`STATUS.md`](STATUS.md) and the [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md). Broad autonomous decisions, live authenticated client, and H/UAT remain open. |
| M8 Living Village | QUALIFIED engineering/runtime exit 2026-09-08 (tested revision `a4d35fee8`): day-scale C5 re-soak 24/24 ledger cycles, 4/4 kill-9 restarts with byte-equality, scheduler VALID, physics/autosave PASS; one accepted 1112 ms transient; DB-volume INVALID by design; H stays UNKNOWN. See [`ROADMAP.md`](ROADMAP.md) M8 contracts and [`STATUS.md`](STATUS.md) 2026-09-08 entry. |
| M9 Emergent world systems | Next direction, not closed: needs/economy/crime-justice/piracy-convoy/imperfect-information-rumor/village-identity systems must interact observably; proposed substrate order R1 → N1 → J1 remains proposed, not approved by this reconciliation. |

## Loop-Closure Definition of Done

For a loop-shaped feature, the human-readable DoD is a named, closed player
loop:

1. Start from an explicit clean/reset or other documented precondition state.
2. Use the ordinary player-facing action path.
3. Observe the expected world/client result.
4. Verify the persistent or terminal consequence.
5. Where the contract requires it, verify repeat/restart/error behavior and
   relevant refusal, idempotency, ownership, or economy invariants. Do not add
   checks that the feature contract does not need.

Preconditions may be seeded by a fixture or normal setup, but the acceptance
path has **no intervention**. In a PlayerBot parity run, no human, GM/admin,
direct-DB/state, `Transform`, `ZoneId`, or manual state intervention is allowed:
the path may not inject quest events, mutate runtime state, use GM/admin repair,
or bypass the ordinary engine path.

A PlayerBot that closes the same loop autonomously under those constraints is
sufficient for that loop's functional/bot-parity gate. It does not close the
client-wire, UI, feel, or human UAT gate. Evidence labels remain separate:
**A** = automated/contract; **R** = rig or PlayerBot proxy; **L** = live
authenticated server/client; **H** = human/client feel. A/R/L never promotes
H.

Read-only UI, passive systems, and continuous services are exceptions to the
loop shape: define an explicit observable outcome instead (for example, a
rendered value/state update, scheduled effect over a stated interval, or
service health/throughput/recovery result).

## Bot Decision Architecture

Reuse the existing `BotGoalArbiter`, schedules, scenario runners,
`GameplayActor`, and `Character` services. Use an FSM/state machine for
lifecycle and legality; use utility/goal scoring to choose among currently
legal objectives (such as hunger, HP, full bag, or travel only where ordinary
services expose those signals). 

**GOAP rollout policy (reconciled 2026-09-15):** Preserve the committed planner
foundation; its presence is not proof of production integration or autonomy.
Runtime work in the dirty tree is work in progress, not a shipped failure or exit.
Integrate one verified action chain at a time. First prove resource-aware search
identity, truthful observations, concrete target binding, and the existing single
action lifecycle. Then compare a bounded planner-driven loop with the deterministic
baseline. Expand only after observed execution and recovery pass. Do not build a
second scheduler, parallel gameplay rules, or speculative caches to compensate for
unclosed actions. This supersedes the blanket "GOAP deferred" wording, not parity
requirements or the existing architecture invariants.

### PlayerBot ownership checkpoint — 2026-09-16

One strategic decision authority does not imply one monolithic executor. GOAP owns
goal selection, plan selection, replanning, and abandonment. Bounded executors own
their internal work: combat owns rotation/range inside a kill request; navigation
owns route/arrival inside a move request; interaction owns cast/interaction completion.
`ActorRequest` owns lifecycle, timeout, idempotency, and audit; `GameplayActor` is the
thin adapter to ordinary `Character` and manager seams; AAEmu core owns every gameplay
effect and persistent state change.

`BotGoalArbiter`, activity modules, and imperative `BotRoam` branches are transitional
goal sources/scheduler adapters, not additional strategic owners. `CombatDecisionTree`,
`BotPath`, and actor polling are executor policy/implementation, not independent
activities. World-state overrides and direct `PlayerBotController` event/inventory calls
are fixture/control-plane only and cannot support an autonomous claim. Retain ordinary
Character embodiment, scheduler, dormancy, and population controls.

Packet-owned purchase, planting, housing, and merchant semantics remain an explicit
unresolved seam. Do not extract a new service or imitate their packet handlers as part
of this program: mark them unavailable to autonomous claims until a separately approved
seam strategy exists. Learned-skill GCD convergence is deferred until a bounded combat
executor owns wait/retry timing; harvest remains a separate asynchronous-completion design task.

Personality supplies weights or tie-breakers, not alternate gameplay rules.
This architecture must not create parallel inventory, quest, combat, or other
gameplay implementations.

The matrix below records two binary questions for every objective: **Player
closes loop?** (the named formal scenario — not whether Josh has ever played) and **Bot
closes loop autonomously?** A `No` or `Unknown` is preserved as evidence, not inferred
from a proxy; H/UAT remains separate. Bot-functional/autonomous testing may proceed before
formal human signoff; open H gates block only their own acceptance claims.

## Objective QAT/UAT matrix
Apply the loop-closure definition above to each row. The first rows record
milestone-scoped gates for M1–M7; the rows prefixed **Post-M7 readiness** are
child tracks or gates under the umbrella defined in the [scope map](#scope-map),
not peer milestones. The evidence column names the current label and scope; the
next-action column names any remaining functional, live, or human/client gate.

**Evidence labels:** **A** = automated/contract; **R** = deterministic rig or
**PlayerBot proxy; **L** = live authenticated server/client; **H** =
human/client feel. `H unknown` is intentional where Josh has not run the gate.
A/R/L never becomes UAT. “Missing action” is the next evidence action, not a
claim that it has already happened.

| Objective / gate | Player closes loop? | Bot closes loop autonomously? | Loop-closure evidence (A/R/L/H) | Remaining QAT/UAT action | Owner | Acceptance artifact |
|---|---|---|---|---|---|---|

| **M1 human gate — Solzreed route** | **Unknown (H open)** | **Yes for bounded 254→255; Unknown/Open for the full M1 route** | **M1 player loop:** from a clean Nuian character in Solzreed, discover legal quests, pursue their objectives through ordinary player actions, turn them in through the normal path, reach the first-mount unlock, and verify restart persistence. **A/R proxy:** `LevelingLoopScenario` closes the bounded 254→255 loop by `Observe → Discover → legal lowest-level choice → objective pursuit → turn-in → re-discover`; focused test 1/1 and `LevelingLoopScenarioRigTests` 7/7 at source/test baseline `7a572c08a32162988dedbf400bd9f8b608fb1974`, with evidence in [leveling-loop report](scorecard-explorations/generated/leveling-loop-2026-08-25.md). `M1M2ReplayScenario` is a 16-quest ordered scripted replay (55 actor records in the fixture report), includes fixture `Level=6` setup, and has no real-mount criterion; it is proxy evidence, not autonomous decision closure. | **H/UAT:** Josh walks the reproducible fresh-Nuian route from reset without GM repair, including first-mount, restart, Bloody Hand, and bounty-board checks, and records the feel verdict. | Josh | [Golden Route](Docs/wiki/Golden-Route-Solzreed.md) + [M1 row in evidence ledger](EVIDENCE-LEDGER.md) |
| **M2 human gate — original baseline** | **Unknown (H open)** | **Unknown/Open (ordered-manifest proxy; no Observe/Discover/legal-choice decision closure)** | **A/R proxy only:** at source/test HEAD `ba530bcebec12af2bc7dc0db7451a535665bbed3`, focused deterministic aggregate is 32/32 pass (the seven classes are recorded in the M2 milestone row); `PlayerbotPilotTests` 30/30 cycles and restart 2/2 are ordered-manifest/contract replay, while `M1M2ReplayScenario` is a fixed 16-quest order with a declared no-real-mount criterion. `QuestScenarioTierTests` itself is 1/1, but its observed per-quest census is 4463 PASS / 110 FAIL / 14 SKIP over 4587 (T1 fail 6280); these remain evidence findings, not an M2 closure claim. | **H:** two players/accounts complete the original baseline from a clean reset with no GM repair; record deviations and verdict. | Josh | M2 row in [ROADMAP deferred gates](ROADMAP.md) + [evidence ledger](EVIDENCE-LEDGER.md) |
| **M3a human gate — contract replay** | **Unknown (H open)** | **Unknown (scripted/fixture proxy; autonomous parity not demonstrated)** | **R proxy:** the ordinary loop is `Character → place/build → plant/harvest → storage/coffer/furniture state → observable ownership/contents result`; `M3aExitScenarioTests` is 1/1 with two scripted actors and one uninterrupted session, while `M3aM4ReplayScenario` follows ordered stages and fixture setup rather than selecting actions from observations. Prior exact source/test baseline `b9a72825f` recorded the M3 focused aggregate 178/178 (named slices: M3a exit 1/1, M3b furniture 4/4, phase restart 10/10, property policy 11/11, repair scanner 13/13). Current HEAD `a77ef878d8fcba297c32c0228e712e0695cc4887` includes source commit `1a3f13dc1`; `HousingStorageFurnitureTests` 13/13 adds unauthorized coffer refusal before `OpenedBy` mutation. Fixture SetPosition/direct service setup is not acceptance evidence. Informal observation (2026-09-12, H stays U): Josh reports running trade packs to build a home — positive context, not a curated PASS. | **L/H:** run the loop with ordinary client actions and no direct Transform/ZoneId/GM/reflection/DB setup shortcuts; Josh records ownership, contents, and feel. No live-client claim is made by the proxy. | M3a lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M3a ledger row |
| **M3b property persistence gate** | **Unknown (engineering/re-entry gate; H open)** | **Unknown/Open (ordered persistence script; no autonomous re-entry decision closure)** | **R/L engineering evidence:** the separate persistence loop is place/decorate → restart/load/assert → plant → restart/load → observed in-flight save kill -9 → restart/assert → mature/harvest state → DB-container kill during save → restart/assert → final re-entry. `M3bExitPersistenceE2eTests` is a seeded/bridge/DB crash harness, not a PlayerBot loop. Prior exact source/test baseline `b9a72825f` recorded the M3 focused aggregate 178/178; current permission fix HEAD is `a77ef878d8fcba297c32c0228e712e0695cc4887`, with `HousingStorageFurnitureTests` 13/13. | **L/H:** run the M3a client loop separately, then execute the isolated restart/re-entry harness with preserved row/transform/phase/contents assertions; do not infer player feel or autonomous behavior from crash evidence. | M3b lane; maintainers/Josh | [ROADMAP M3b](ROADMAP.md) + [M3b ledger row](EVIDENCE-LEDGER.md) |
| **M4 human gate — economic/navigation replay** | **Unknown/Open (H open)** | **Unknown/Open (ordered scripted/fixture proxy; autonomous decision closure not demonstrated)** | **M4 player loop:** clean ordinary `Character` gather/harvest → craft pack → carry/place → load owned vehicle → drive normal route → unload → `SellSpecialty` reward → repeat, with per-object restart/persistence as applicable. `SellSpecialty` composes the canonical CSSellBackpackGoodsPacket → SpecialtyManager path, with merchant/pack checks, pack-consumption postcondition, same-zone/no-pack refusal, repeat-cycle, and idempotency coverage. Current source/test HEAD `6ff68e1bb4a6afe08441308acb9a485b5133c42e`; focused results: `M4ExitIntegratedSessionTests` 2/2, `EconomyDayCycleScenarioRigTests` 4/4, `M3aM4ReplayScenarioRigTests` 2/2. Full normal-clone gate: 2498 total / 2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools; skip `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG` and `AAEMU_E2E_DB_PASSWORD`. Forced rebuild report: 1067 warnings / 0 errors. Existing property/economic replay is ordered scripted/fixture proxy and its direct setup shortcuts are not authentic acceptance; no live M4 restart/vehicle proof was run because the shared E2E reset is unsafe. Informal observation (2026-09-12, H stays U): Josh reports running trade packs to build a home — positive context, not the full loop or restart proof. | **L/H:** run the full route with normal client movement/vehicle controls and no direct Transform/ZoneId/GM/reflection/DB shortcuts; execute isolated restart/vehicle checks; Josh records reward, ownership, persistence, and feel. | M4 lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M4 ledger row |
| **M5 actor decision/action loop** | **Unknown (H/client gate where applicable)** | **Unknown/Open (universal decision loop)** | **A/R:** `BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` at `263ecc66c474ca1c5f4b085e86ef3e47f49fd1` provide immutable observed context, legality-before-preference, bounded candidates, deterministic fixed-priority/personality/tie-break selection, terminal postcondition, and existing `GameplayActor` dispatch in `LevelingLoop`'s quest-accept choice; `BotDecisionProposalTests` 5/5. This is a decision primitive plus scoped quest consumer, not universal bot autonomy; broad M5 policy remains open. | **A:** extend scoped consumers beyond the quest-accept choice; **H:** only where a consumer has a client-feel gate. | M5 lane; Josh only for human-relevant consumer feel | [ROADMAP M5](ROADMAP.md) + [M5 ledger row](EVIDENCE-LEDGER.md) |
| **M6 human/exit gate — B4 restart** | **N/A (restart gate)** | **Unknown/Open (harness-driven lifecycle; autonomous decision closure not demonstrated)** | **A/R:** recorded lifecycle evidence stands (cancellation `950cfd279`, population isolation `c97909f4f`, default-skipped six-hour leg `155c82c66`); B4 metadata persistence recorded; A5 CLOSED per the 2026-09-08 conjunction. M6 full-exit remains unclaimed; H/UAT separate. | **A/R:** scheduler-driven full-exit evidence only; **H:** N/A for the operational restart/soak criterion (separate visibility/feel caveats live in the ledger). | M6 lane | [ROADMAP M6](ROADMAP.md) + [M6 ledger rows](EVIDENCE-LEDGER.md) |
| **M7 adventurer/party loop** | **Unknown (H/client gate open)** | **Unknown/Open (A/R rig/proxy only; broad autonomous decision closure not demonstrated)** | **A/R:** current focused M7 evidence is **147/147** no-fail/no-skip: primary 36/36 (Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4, PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support 111/111. Hunt kill uses real `DoOnMonsterHuntEvents` with fixture HP=0; Party spike is synthetic/fixture. | **L/H:** no current live authenticated-client run or H/UAT; keep bounded LevelingLoop 254→255 only. Broad decision closure, real damage/Npc.DoDie, scheduler-driven route, party roles/regroup/restart/disconnect, mount/travel remain open. | M7 bot lane; Josh for H | [M7 roadmap reconciliation](ROADMAP.md) + [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md) |
| **M8 — Living Village gate** | **Unknown (H open)** | **Unknown/Open (no autonomous village loop demonstrated)** | **A/R/L:** QUALIFIED engineering/runtime exit 2026-09-08 (24/24 ledger, 4/4 kill-9, scheduler VALID, physics/autosave PASS, one accepted 1112 ms transient, DB-volume INVALID by design; tested `a4d35fee8`); H UNKNOWN. Village activity is scenario/rig-driven; no fresh-character-to-village autonomous loop. | **L/H:** day-scale coexistence + feel with human-owned home/farm; **A/R:** autonomous profession cycling. | Village lane; Josh for H | [ROADMAP M8](#m8--living-village-first-true-vision-release-aaemu-living-village) + [STATUS 2026-09-08](STATUS.md) |
| **M9 — Emergent systems direction** | **Unknown (H open)** | **Unknown (no propagation demonstrated)** | **Proposed substrate only:** R1 rumor graph → N1 needs/composer → J1 theft/report/justice; R2/R3 later. No per-system exit test has run. | **A/R:** per-system propagation proof (A's output changes B's behavior); **H:** real-player feel once systems interact. | M9 lanes; Josh for H | [ROADMAP M9](#m9--emergent-world-systems-the-world-simulator-layer) |
| **Post-M7 readiness — PB-001 routed navigation track** | **Unknown (live/H open)** | **Unknown (contract/nav coverage; autonomous loop open)** | **A/R:** landed `IGameplayActor.NavigateTo` and `NavigateToUnit`; wired into `LevelingLoopScenario` for hunt prey, grind targets, talk NPCs, turn-in reporters, and gather doodads. Tracked `GameplayActorNavigateTests` eight-test run (8/8) passes and `BaiNavigationRigTests` (6/6) covers GeoData/navmesh. | **L/H:** exercise interior and cross-region routes on the live stack and have Josh assess movement feel; broad coverage is still open. | Navigation lane; Josh for H | [Blocker PB-001](scorecard-explorations/playerbot-blockers.md) + focused test result in [STATUS](STATUS.md) |
| **Post-M7 readiness — PB-002 autonomous progression track** | **Unknown (broad route open)** | **Unknown (broad autonomous loop open)** | **A/R rig/proxy only:** landed objective families are interaction, item-use, item-group use/gather, Sphere, Craft, Cinema, MonsterHunt/MonsterGroupHunt, Aggro (partial), ZoneKill, EtcItemObtain, CompleteQuest, Level, MateLevel, and AbilityLevel. Focused results: LevelingLoopScenarioRigTests 35/35, QuestActObjAggroTests 2/2, QuestEtcItemObtainRigTests 3/3, QuestZoneKillVictimRigTests 2/2, PvpFlaggingRigTests 11/11; Game/UnitTests Release builds 0 errors. 70 component-only forms without engage tie deferred to specialized tracks (68 Ayanad Library bounties, 10 Prologue sequence chains, 6 Honor / 2 Rift events, 4 minigame/title/festival) — fail-closed and excluded from ordinary leveling acceptance. | **Next:** live authenticated progression; human/client evidence. | Quest/playerbot lane; Josh for H | [PB-002 status](STATUS.md) |
| **Post-M7 readiness — PB-005 NPC grounding track** | **N/A (audit outcome)** | **N/A (audit outcome)** | **A:** terrain replay corrected 593 non-whitelisted severe-positive rows; 702 intentional whitelist rows are unchanged. Cave/interior, submerged classification, and duplicate ownership remain unresolved. | **H:** Josh runs the W4-5 grounding tour and records coordinates/screenshots; engineering then classifies cave/deck/submerged findings and duplicate rows. | Server/data lane; Josh for H | [Grounding audit](scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md) + [W4-5 packet](Docs/JOSH-QA-WAVE4.md) |
| **Post-M7 readiness — PB-007 narrow PvP track** | **Unknown (H open)** | **Unknown (live login, not PlayerBot parity)** | **L:** isolated real-login E2E passes the flagged-aggression handshake and Peace block; this closes only the narrow handshake. | **L/H:** run the deferred WAR-HONOR (>251 hostile kills plus conflict timer) and broader PvP/honor/client-feel scope; do not reuse the handshake pass. | PvP lane; Josh for H | [PB-007 report](scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md) + W4-4 packet |
| **Post-M7 readiness — A5 / Tier-3 dormancy gate** | **N/A (load gate)** | **N/A (load gate)** | **A/R/L:** A5 CLOSED 2026-09-08 — (a) SHAPE re-shown 4/4 at engine `a23cdc33e` (1000/1000 registered, 50 embodied, RSS Δ -12.9%, wake p95 256.2 ms); (b1) soak #2 PASS 2026-09-05; (b2) asserted soak + bounded restart PASS 2026-09-06/05. Earlier 12h-soak timing RCA and memory-pressure diagnosis remain linked history, not current actions. | **A/R/L:** none open for this gate; new regressions get their own scoped card. No H claim is needed for this load gate. | Scaling/rig lane | [G2-A5 report](scorecard-explorations/generated/g2-a5-acceptance-report.md) + [A5 stall dossier](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md) |
| **Mail** | **Unknown (client/UAT open)** | **Unknown (no PlayerBot parity recorded)** | **L:** Mail S3 authenticated restart E2E passed equipment/copper persistence, ownership, unread count, take, and delete. | **L/H:** capture the real-client return opcode (0x0a2 remains strongly inferred), run W4-1/W4-2 ownership UI checks, and close COD plus expiry/bounce follow-ups. | Mail lane; Josh for client capture | [Mail dossier](scorecard-explorations/mechanics/mail-domain.md) + [W4 mail packet](Docs/JOSH-QAT-WAVE4.md) + S3 note in [STATUS](STATUS.md) |
| **Dominion** | **Unknown (client/UAT open)** | **Unknown (autonomous loop not recorded)** | **L:** slice-1 persistence, phase schedule/tax update, and kill-9 reload are recorded. | **L/H:** exercise real declare-trigger UI and later combat/siege-battle slices; current persistence does not imply combat or client UI acceptance. | Dominion lane; Josh for UI | [Dominion dossier](scorecard-explorations/mechanics/dominion-domain.md) + [ROADMAP slice](ROADMAP.md) |
| **Ships** | **Unknown (client/UAT open)** | **Unknown (autonomous loop not recorded)** | **L (current fix context) / historical:** PB-006 records the region-sync fix and live sailing proof; the checked-in rowboat report is the pre-fix failure and remains historical. | **L/H:** rerun W4-6 B1–B6 on the current source/deploy, including steering, disembark/despawn, passenger view where available, and shipyard restart caveats. | Ships lane; Josh for feel | [Ships dossier](scorecard-explorations/mechanics/ships-domain.md) + [W4-6 packet](Docs/JOSH-QAT-WAVE4.md) + historical [rowboat report](scorecard-explorations/generated/ships-rowboat-e2e-report.md) |
| **INDUN-01 instance-dungeon loop** | **Unknown (H open)** | **Unknown (no bot-party clear-then-exit parity recorded)** | **L (exit leg only):** PB-003 closed — `IndunExitE2eTests` 11/11 (entry skill 17731 → bosses 10166/10167 dead → completion events 4601/4602 → exit portal 4289/skill 17733 → SCLoadInstancePacket world 0/zone 179, both members at pre-entry anchor). C/W structural evidence in indun-domain.md; low-level dungeons 45/46/47/50/51/52 have zero completion hooks; cooldowns memory-only. | **L/H:** run Lane D S1 bot-party clear-then-exit (Hadir Farm 46) through the real portal doodad via PartySpikeScenario/ProvisionBotParty seams; S2 completion hook ruling; S3 cooldown persistence + channel-select + non-blocking loader; S4 phase scripting deferred. H stays UNKNOWN. | Indun/party lane; Josh for H | [Indun dossier](scorecard-explorations/mechanics/indun-domain.md) + [undefined-mechanics dossier](scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md) + [ROADMAP Lane D INDUN slices](ROADMAP.md) |
| **Undefined / data-only / partial-dispatch world-mechanics ledger rows (AGGRO-PACK-01, AUCTION-BANK-DOODAD-01, RESPAWN-LADDER-01, NPC-INTERACTION-01, BOOK-01)** | **Unknown (H open)** | **Unknown (no gameplay parity — discovery rows only)** | **Data+code only:** read-only census dossier (HEAD `0f8254dc3d914193d432fb842169e9bb07075508`, DB md5 `78b3bdbf038db3b927056106efdf91af`) — classification is **not uniform**: truly undefined (player-visible, no-op/unloaded dispatch — aggro packs 130/643; AH/bank kiosk 7983 no-op funcs), data-only/hardcoded mismatch (respawn ladder 10 rows vs hardcoded, refines COMBAT-01), partial/undefined dispatch (npc_interaction_sets 111/114 on 142 NPCs), BOOK-01 data-only/unwired (72/1206/1873/846 + 551 item links). All W=0/A=0/H=U. | **C/W:** Lane D C-dimension audits per row, then scoped W slices with acceptance criteria (pack shared-pull; canonical respawn ladder on `SCUnitDeathPacket`; kiosk opens AH/bank; canonical interaction menus). H stays UNKNOWN until Josh runs. | Lane D world-systems + combat/AI lanes | [undefined-mechanics dossier](scorecard-explorations/mechanics/undefined-world-mechanics-2026-08-31.md) + [SCORECARD ledger](SCORECARD.md) + [ROADMAP Lane D tracks](ROADMAP.md) |
| **MCP boundaries** | **N/A (client-neutral boundary)** | **N/A (client-neutral boundary)** | **A/L:** current catalog is 39 tools; focused route/MCP/queue checks are reported 53/53 and the integrated benchmark proves authenticated management/action lifecycle plus DB cross-check. Managed headless bots are client-neutral; authenticated wire leg is explicitly blocked. Separately, the greenfield read-only **archaeology MCP** (`AAEmu.ArchaeologyMcp/`, 24 tools) is a client-neutral data-access slice — see [STATUS](STATUS.md) and [SCORECARD](SCORECARD.md). | **L:** retain protocol and management evidence, add a client-login-allowed packet/state leg if needed, and document route/tool scope without presenting MCP or headless evidence as client UAT. | MCP/control-plane lane; Josh for client leg | [MCP benchmark](scorecard-explorations/generated/integrated-mcp-e2e-benchmark-2026-08-27.md) + MCP sections in [STATUS](STATUS.md) and [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md) |

## Update protocol

1. When an objective or milestone state changes, update the authoritative source
   first: `ROADMAP.md` for requirements and gates, `STATUS.md` for the current
   checkpoint, `SCORECARD.md` for mechanic evidence, and
   `EVIDENCE-LEDGER.md` for milestone evidence-state transitions.
2. When a blocker, capability, dossier, or QAT/UAT result changes, update the
   owning source (`scorecard-explorations/playerbot-blockers.md`, the capability
   matrix, the relevant dossier, or a dated report / Josh packet). Preserve the
   old report and date; append a new result instead of rewriting history.
3. Refresh this page's evidence date, source-head note, links, and only the
   affected matrix row. Do not copy a second milestone narrative here; link to
   the authoritative prose.
4. Keep evidence labels explicit. A/R/L evidence can establish function,
   restart, or load according to its artifact; only an actual Josh/client run
   can establish H/UAT. If no rerun exists at the page's source head, say so.
5. Run Markdown formatting/link/path checks before committing docs-only changes.
