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
**Audited source/evidence baseline:** current local source/test HEAD
(PB-001 Navigation Toolchain, In-Game Dev Mapper, Beyond Solzreed Expansion,
ObstacleManager pathfinding avoidance, and PB-002 Autonomous Inter-Zone
Progression & Nui Shrine Death Recovery landed; gate 2,760/2,759/0/1; prior
baseline `371b8a88a`). M5 proposal
`263ecc66c474ca1c5f4b085e86ef3e47f49fd1`, M6 cancellation `950cfd279`,
population isolation `c97909f4f`, and opt-in six-hour leg `155c82c66` are in
its ancestry.

The M5 bounded decision primitive remains integrated in `LevelingLoop`'s accept
choice: `BotDecisionProposal`, `BotDecisionSelector`, and `BotDecisionCycle`
preserve immutable observed context, enforce hard legality before preference,
bound candidates, select deterministically by fixed priority/personality/tie-break,
and require a terminal postcondition before existing `GameplayActor` dispatch.
Focused contract evidence is 5/5; this is a scoped quest consumer, not universal
bot autonomy, and broad M5 policy remains open.

M6 now includes the cancellation boundary, the c97909f4f baseline-population
isolation fix, and opt-in six-hour stage `155c82c66`. The stage is default
skipped, requires `A5_TIER3_SIX_HOUR=1`, minutes >=360, sample seconds 1..300,
and uses cooperative deadlines plus ID-bound `finally` cleanup. Readiness
evidence is the corrected 4721 rehearsal: 1/1, 1000 seeded, 50 embodied, 950
dormant, materialize p95 259.2ms, RSS +2.56%, 50 dematerialized, owned cleanup
zero; cancellation 3/3, ownership 2/2.

Operator command (requires Docker, read-only `/root/hl-cp-test` assets, and
`ensure-log-caps.sh` in the isolated E2E root):
`A5_TIER3_SIX_HOUR=1 A5_TIER3_SIX_HOUR_MINUTES=360 A5_TIER3_SIX_HOUR_SAMPLE_SECONDS=60 A5_DORMANT_COUNT=1000 E2E_ROOT=/root/aaemu-e2e-a5-tier3-sixhour E2E_LOGIN_PORT=4237 E2E_GAME_PORT=4239 E2E_STREAM_PORT=4250 E2E_BRIDGE_PORT=4260 E2E_INTERNAL_PORT=4234 E2E_WEBAPI_PORT=4280 E2E_DB_PORT=43306 DB_HOST_PORT=43306 COMPOSE_PROJECT_NAME=aaemu_a5_t3_sixhour E2E_REBUILD=1 dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release --filter-method AAEmu.IntegrationTests.E2e.G2.A5Tier3AcceptanceProbeTests.Probe_A5Tier3DormantTimers_SixHour`.

No six-hour execution or metrics are claimed here. No new full-gate total was
run at `da0fdc61`; prior full-gate 2504/2503/0/1 at 0ce remains historical.
H/UAT and M6 full-exit remain separate and unclaimed.

## Authoritative records

| Record | Use |
|---|---|
| [`STATUS.md`](STATUS.md) | Current fork checkpoint, milestone narrative, open human gates, and recent reconciliations |
| [`ROADMAP.md`](ROADMAP.md) | Locked milestone requirements, deferred validation gates, and next-wave objectives |
| [`SCORECARD.md`](SCORECARD.md) | Mechanic evidence dimensions and conservative current scope |
| [`EVIDENCE-LEDGER.md`](EVIDENCE-LEDGER.md) | Append-only milestone evidence states and human-feel boundary |
| [`PLAYERBOT_BLOCKER ledger`](scorecard-explorations/playerbot-blockers.md) | Active bot blockers and retained resolutions |
| [`PlayerBot Capability Matrix`](scorecard-explorations/mechanics/playerbot-capability-matrix.md) | Perceive / Decide / Act / Verify and autonomous-loop view |

### Scope map

The hierarchy below is authoritative for current planning and reporting:

- **M0–M7** — landed foundation/product milestones (with any separately
  recorded open human/client gates).
- **Post-M7 readiness and closure** — the current umbrella scope; it is not a
  new numbered milestone. It contains:
  - **PB-001, PB-002, PB-005, PB-007** — capability/blocker tracks.
  - **A3, A4, A5** — population/scaling acceptance gates.
  - **slices** — implementation units within a track or gate.
  - **H** — human/client acceptance, deferred separately from automated,
    rig/proxy, and live evidence.
  - **Lane D world-systems / undefined-mechanics rows (2026-08-31 census)** —
    new gap tracks/findings: discovery-ledger rows AGGRO-PACK-01 and
    AUCTION-BANK-DOODAD-01 (**truly undefined**, player-visible, no-op/
    unloaded dispatch), RESPAWN-LADDER-01 (**data-only/hardcoded mismatch**
    refining the existing **COMBAT-01**), NPC-INTERACTION-01
    (**partial/undefined dispatch**), BOOK-01 refreshed
    (**data-only/unwired**), INDUN-01 formalized (**existing omission, not a
    new discovery**) with Lane D slices S1-S4 (existing dossier, PB-003 exit
    leg 11/11); the umbrella "undefined-mechanics" label is a track name, not
    a shared classification — see the classification sentence in the
    [ROADMAP.md](ROADMAP.md) Lane D bullet. All five discovery rows are
    W=0/A=0/H=U; not a milestone, not M8.

The roadmap separately defines a future **M8 — Living Village**; this scope map
does not promote readiness tracks or acceptance gates into M8, and no other
official M8 number is implied. See [`ROADMAP.md`](ROADMAP.md) for the formal M8
roadmap entry and [`STATUS.md`](STATUS.md) for the current evidence narrative.

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
| M0 Foundation | Closed with Josh signoff (2026-08-03). |
| M1 Quest/progression spine | Closed on automated evidence; the Solzreed human route remains Josh-owned and open. |
| M2 Golden-path baseline | Deterministic docs/test reconciliation at source/test HEAD `ba530bcebec12af2bc7dc0db7451a535665bbed3`: clean reset → ordinary golden-path baseline quest/progression → required first-mount/baseline state → restart/clean-state persistence verification. Focused A/R proxy run: 32/32 passed, 0 failed, 0 skipped — `HeadlessSessionProvisioningTests` 8/8, `M1M2ReplayScenarioRigTests` 3/3, `M1M2ReplayCastWindowRigTests` 1/1, `PlayerbotPilotTests` 6/6, `QuestScenarioTests` 12/12, `QuestScenarioTierTests` 1/1, `QuestDataCensusTests` 1/1. `PlayerbotPilotTests` 30/30 cycles and restart 2/2 are ordered-manifest/contract proxy evidence; `M1M2ReplayScenario` is fixed 16-quest ordering and its mount criterion reports no real mount. `QuestScenarioTierTests` method pass is not M2 full closure: observed census 4463 PASS / 110 FAIL / 14 SKIP over 4587, with T1 failure quest 6280; remaining findings are historical evidence findings. Player closes loop = Unknown/H open; Bot closes loop autonomously = Unknown/Open. Original two-player/no-GM human baseline remains Josh-owned and open. |
| M4 Trade/craft/transport | Engineering chain is implemented, but Player/Bot loop closure remains Unknown/Open. The clean ordinary `Character` loop is gather/harvest → craft pack → carry/place → load owned vehicle → drive normal route → unload → sell specialty pack for reward → repeat, with per-object restart/persistence as applicable. `SellSpecialty` now composes the canonical CSSellBackpackGoodsPacket → SpecialtyManager path with ordinary merchant/pack checks, pack-consumption postcondition, same-zone/no-pack refusal, repeat-cycle, and idempotency coverage. Current source/test HEAD `6ff68e1bb4a6afe08441308acb9a485b5133c42e`; current focused results are M4ExitIntegratedSessionTests 2/2, EconomyDayCycleScenarioRigTests 4/4, and M3aM4ReplayScenarioRigTests 2/2. Full normal-clone gate at this HEAD: 2498 total / 2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools; skip `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG` + `AAEMU_E2E_DB_PASSWORD`. Forced rebuild report: 1067 warnings / 0 errors. Existing replay is ordered scripted/fixture proxy with direct setup shortcuts outside authentic acceptance; shared E2E reset is unsafe, so no live M4 restart/vehicle proof is claimed. Human/client QAT remains open. |
| M5 Gameplay Actor Contract | `BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` landed at `263ecc66c474ca1c5f4b085e86ef3e47f49fd1`; bounded decision primitive integrated into `LevelingLoop` accept choice; focused contract 5/5. Hard legality precedes deterministic fixed-priority/personality/tie-break selection and terminal postcondition; broad M5 policy/universal autonomy remains open. |
| M6 Deterministic playerbot framework | Current source/test HEAD `da0fdc61a72a15111fddc8ac627a164a5f050558`; cancellation `950cfd279`, population isolation `c97909f4f`, and opt-in six-hour leg `155c82c66` are integrated. Six-hour stage is default skipped and requires explicit `A5_TIER3_SIX_HOUR=1`, minutes >=360, sample seconds 1..300; it carries cooperative deadline and ID-bound finally cleanup. Corrected 4721 readiness rehearsal passed 1/1 with 1000 seeded, 50 embodied, 950 dormant, p95 259.2ms, RSS +2.56%, 50 dematerialized, cleanup zero. No six-hour execution, M6 full-exit, or H/UAT claim. |
| M7 Adventurer/party bots | Current focused A/R evidence **147/147**: primary 36/36 (Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4, PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support 111/111. Bounded LevelingLoop 254→255 is the only autonomous decision slice; broad autonomous decisions, live authenticated client, and H/UAT remain open. |

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
services expose those signals). Optional behavior-tree or GOAP layers are
orchestration tools only where justified. Personality supplies weights or
tie-breakers, not alternate gameplay rules. This architecture must not create
parallel inventory, quest, combat, or other gameplay implementations.

The matrix below records two binary questions for every objective: **Player
closes loop?** and **Bot closes loop autonomously?** A `No` or `Unknown` is
preserved as evidence, not inferred from a proxy; H/UAT remains separate.

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
| **M3a human gate — contract replay** | **Unknown (H open)** | **Unknown (scripted/fixture proxy; autonomous parity not demonstrated)** | **R proxy:** the ordinary loop is `Character → place/build → plant/harvest → storage/coffer/furniture state → observable ownership/contents result`; `M3aExitScenarioTests` is 1/1 with two scripted actors and one uninterrupted session, while `M3aM4ReplayScenario` follows ordered stages and fixture setup rather than selecting actions from observations. Prior exact source/test baseline `b9a72825f` recorded the M3 focused aggregate 178/178 (named slices: M3a exit 1/1, M3b furniture 4/4, phase restart 10/10, property policy 11/11, repair scanner 13/13). Current HEAD `a77ef878d8fcba297c32c0228e712e0695cc4887` includes source commit `1a3f13dc1`; `HousingStorageFurnitureTests` 13/13 adds unauthorized coffer refusal before `OpenedBy` mutation. Fixture SetPosition/direct service setup is not acceptance evidence. | **L/H:** run the loop with ordinary client actions and no direct Transform/ZoneId/GM/reflection/DB setup shortcuts; Josh records ownership, contents, and feel. No live-client claim is made by the proxy. | M3a lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M3a ledger row |
| **M3b property persistence gate** | **Unknown (engineering/re-entry gate; H open)** | **Unknown/Open (ordered persistence script; no autonomous re-entry decision closure)** | **R/L engineering evidence:** the separate persistence loop is place/decorate → restart/load/assert → plant → restart/load → observed in-flight save kill -9 → restart/assert → mature/harvest state → DB-container kill during save → restart/assert → final re-entry. `M3bExitPersistenceE2eTests` is a seeded/bridge/DB crash harness, not a PlayerBot loop. Prior exact source/test baseline `b9a72825f` recorded the M3 focused aggregate 178/178; current permission fix HEAD is `a77ef878d8fcba297c32c0228e712e0695cc4887`, with `HousingStorageFurnitureTests` 13/13. | **L/H:** run the M3a client loop separately, then execute the isolated restart/re-entry harness with preserved row/transform/phase/contents assertions; do not infer player feel or autonomous behavior from crash evidence. | M3b lane; maintainers/Josh | [ROADMAP M3b](ROADMAP.md) + [M3b ledger row](EVIDENCE-LEDGER.md) |
| **M4 human gate — economic/navigation replay** | **Unknown/Open (H open)** | **Unknown/Open (ordered scripted/fixture proxy; autonomous decision closure not demonstrated)** | **M4 player loop:** clean ordinary `Character` gather/harvest → craft pack → carry/place → load owned vehicle → drive normal route → unload → `SellSpecialty` reward → repeat, with per-object restart/persistence as applicable. `SellSpecialty` composes the canonical CSSellBackpackGoodsPacket → SpecialtyManager path, with merchant/pack checks, pack-consumption postcondition, same-zone/no-pack refusal, repeat-cycle, and idempotency coverage. Current source/test HEAD `6ff68e1bb4a6afe08441308acb9a485b5133c42e`; focused results: `M4ExitIntegratedSessionTests` 2/2, `EconomyDayCycleScenarioRigTests` 4/4, `M3aM4ReplayScenarioRigTests` 2/2. Full normal-clone gate: 2498 total / 2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools; skip `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG` and `AAEMU_E2E_DB_PASSWORD`. Forced rebuild report: 1067 warnings / 0 errors. Existing property/economic replay is ordered scripted/fixture proxy and its direct setup shortcuts are not authentic acceptance; no live M4 restart/vehicle proof was run because the shared E2E reset is unsafe. | **L/H:** run the full route with normal client movement/vehicle controls and no direct Transform/ZoneId/GM/reflection/DB shortcuts; execute isolated restart/vehicle checks; Josh records reward, ownership, persistence, and feel. | M4 lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M4 ledger row |
| **M5 actor decision/action loop** | **Unknown (H/client gate where applicable)** | **Unknown/Open (universal decision loop)** | **A/R:** `BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` at `263ecc66c474ca1c5f4b085e86ef3e47f49fd1` provide immutable observed context, legality-before-preference, bounded candidates, deterministic fixed-priority/personality/tie-break selection, terminal postcondition, and existing `GameplayActor` dispatch in `LevelingLoop`'s quest-accept choice; `BotDecisionProposalTests` 5/5. This is a decision primitive plus scoped quest consumer, not universal bot autonomy; broad M5 policy remains open. |
| **M6 human/exit gate — B4 restart** | **N/A (restart gate)** | **Unknown/Open (harness-driven lifecycle; autonomous decision closure not demonstrated)** | **A/R:** current M6 focused evidence 105/105; `c97909f4f` isolates baseline population, `950cfd279` provides cooperative cancellation, and `155c82c66` adds the default-skipped six-hour natural dormant-timer stage. Corrected rehearsal at 4721 passed 1/1 with 1000 seeded, 50 embodied, 950 dormant, materialize p95 259.2ms, RSS +2.56%, 50 dematerialized, owned cleanup zero. No six-hour execution, M6 full-exit, or H/UAT result. |
| **M7 adventurer/party loop** | **Unknown (H/client gate open)** | **Unknown/Open (A/R rig/proxy only; broad autonomous decision closure not demonstrated)** | **A/R:** current focused M7 evidence is **147/147** no-fail/no-skip: primary 36/36 (Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4, PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support 111/111. Hunt kill uses real `DoOnMonsterHuntEvents` with fixture HP=0; Party spike is synthetic/fixture. | **L/H:** no current live authenticated-client run or H/UAT; keep bounded LevelingLoop 254→255 only. Broad decision closure, real damage/Npc.DoDie, scheduler-driven route, party roles/regroup/restart/disconnect, mount/travel remain open. | M7 bot lane; Josh for H | [M7 roadmap reconciliation](ROADMAP.md) + [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md) |
| **Post-M7 readiness — PB-001 routed navigation track** | **Unknown (live/H open)** | **Unknown (contract/nav coverage; autonomous loop open)** | **A/R:** landed `IGameplayActor.NavigateTo` and `NavigateToUnit`; wired into `LevelingLoopScenario` for hunt prey, grind targets, talk NPCs, turn-in reporters, and gather doodads. Tracked `GameplayActorNavigateTests` eight-test run (8/8) passes and `BaiNavigationRigTests` (6/6) covers GeoData/navmesh. | **L/H:** exercise interior and cross-region routes on the live stack and have Josh assess movement feel; broad coverage is still open. | Navigation lane; Josh for H | [Blocker PB-001](scorecard-explorations/playerbot-blockers.md) + focused test result in [STATUS](STATUS.md) |
| **Post-M7 readiness — PB-002 autonomous progression track** | **Unknown (broad route open)** | **Unknown (broad autonomous loop open)** | **A/R rig/proxy only:** landed objective families are interaction, item-use, item-group use/gather, Sphere, Craft, Cinema, MonsterHunt/MonsterGroupHunt, Aggro (partial), ZoneKill, EtcItemObtain, CompleteQuest, Level, MateLevel, and AbilityLevel. Focused results: LevelingLoopScenarioRigTests 35/35, QuestActObjAggroTests 2/2, QuestEtcItemObtainRigTests 3/3, QuestZoneKillVictimRigTests 2/2, PvpFlaggingRigTests 11/11; Game/UnitTests Release builds 0 errors. 70 component-only forms without engage tie deferred to specialized tracks (68 Ayanad Library bounties, 10 Prologue sequence chains, 6 Honor / 2 Rift events, 4 minigame/title/festival) — fail-closed and excluded from ordinary leveling acceptance. | **Next:** live authenticated progression; human/client evidence. | Quest/playerbot lane; Josh for H | [PB-002 status](STATUS.md) |
| **Post-M7 readiness — PB-005 NPC grounding track** | **N/A (audit outcome)** | **N/A (audit outcome)** | **A:** terrain replay corrected 593 non-whitelisted severe-positive rows; 702 intentional whitelist rows are unchanged. Cave/interior, submerged classification, and duplicate ownership remain unresolved. | **H:** Josh runs the W4-5 grounding tour and records coordinates/screenshots; engineering then classifies cave/deck/submerged findings and duplicate rows. | Server/data lane; Josh for H | [Grounding audit](scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md) + [W4-5 packet](Docs/JOSH-QA-WAVE4.md) |
| **Post-M7 readiness — PB-007 narrow PvP track** | **Unknown (H open)** | **Unknown (live login, not PlayerBot parity)** | **L:** isolated real-login E2E passes the flagged-aggression handshake and Peace block; this closes only the narrow handshake. | **L/H:** run the deferred WAR-HONOR (>251 hostile kills plus conflict timer) and broader PvP/honor/client-feel scope; do not reuse the handshake pass. | PvP lane; Josh for H | [PB-007 report](scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md) + W4-4 packet |
| **Post-M7 readiness — A5 / Tier-3 dormancy gate** | **N/A (load gate)** | **N/A (load gate)** | **A/R/L:** A5 near-term gate passes with 100 dormant/10 embodied, RSS +2.09%, materialize p95 251.7 ms; G2-A3 1,000-bot transition p99 passes. **2026-09-01:** the corrected 12h soak (SHA `1ce4664f…`) completed FULL with RSS within budget but `passed=false` on timing — two distinct failure modes (region overrun vs tick invoke max), classification UNKNOWN / host-level scheduling/CPU steal leading hypothesis, budgets NOT relaxed; see the read-only [A5 stall dossier](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md). **2026-09-02:** memory-pressure diagnosis from user/live operational evidence (prod CT133 1,647 physics-slow warnings over 9 days, both-world ~500–575 ms matching spikes; prod Game ~130,228 kB VmSwap on 8 GB CT with 512 MB zram, swappiness 60; comparison/contrast soak 0 KB swap on CT124 with 48 GB RAM, zero warnings in 12 h; live 573 ms spike coincided with .NET BGC thread, ~25 MB RSS drop, swap-in clustering — single reported coincidence, not causal proof) — a **strongly supported provisional infrastructure root cause** (Mai's CT133 diagnosis, user/live operational evidence) for the **user-reported current PROD CT133 only**, no longer merely UNKNOWN host scheduling for that environment; soak-time classification remains UNKNOWN (soak host had 0 swap, no in-soak host/GC telemetry — memory/swap does NOT explain the 12 h soak breaches); **A5 remains formally OPEN/UNCLOSED** until CT133 memory remediation is applied and a comparable post-change run confirms the warnings disappear; next action memory remediation first (preferred CT133 → 16 GB; alternatives `DOTNET_GCHeapHardLimit` calibration or disabling swap with OOM risk), before/after memory/swap/GC telemetry, then rerun the post-remediation soak; 1-hour calibration-lane telemetry run (2026-09-02) is no new soak result (host sidecar ~3,388 samples, 0 steal/CPU PSI/throttling, physics loop max 62 ms at boot and ≤ 40 ms steady, 0 in-window physics-slow warnings); planning item `A5-MEMORY-01` in the dossier (acceptance = before/after CT133 VmSwap/zram/GC/RSS telemetry plus post-remediation full soak with zero breaches); old 12 h report pre-remediation, no new soak pass claimed, budgets unchanged. **Post-remediation follow-up (2026-09-02, user/Mai operational evidence):** section 8 of the A5 dossier records the post-change observation (Game PID 3057037, deployment since 20:06 UTC, ~10.5 h; CT 16 GiB RAM / 8 GiB swap; cgroups `memory.max=max`/`memory.swap.max=max`, zero OOM/max hits; CT 4.2 GB / container 2.8 GB; Game VmRSS 2.67 GB, VmData 4.27 GB, VmSwap 0 kB vs pre-restart ~129 MB; stack game 2.6 GiB / db 467.5 MiB / login 43.2 MiB / adminer 8.8 MiB / register-api 15 MB; GC trace capture alive 5.3 MB and growing; GC events in nettrace) — 17 physics warnings across the ~10.5 h observation (worst 340 ms) and 22 spikes in the first 2 h post-restart (worst 807 ms) as distinct reported windows/classes; 500 ms+ signature absent in the later observed period (the ~10.5 h window's worst is 340 ms) — the 807 ms first-2 h spike predates that absence, which is not claimed for the first-2 h window; **strongly supports** the prod CT133 memory-pressure/swap hypothesis, **not fully proving** it (residual ~300 ms events keep another cause open); historical 12 h soak classification remains UNKNOWN; no A5 pass claimed; budgets unchanged; next closure criteria = continue GC/nettrace capture, correlate residual warnings with GC/thread/process/host telemetry, then a comparable post-change A5 soak with zero budget breaches before closing A5; labeled user/Mai operational evidence, not H/human gameplay and not independently reproduced here. | **A/R/L:** continue GC/nettrace capture and correlate residual warnings with GC/thread/process/host telemetry, then run a comparable post-change A5 soak with **zero budget breaches** before closing A5 (section 8.4 of the A5 dossier); the earlier bounded 6h calibration rerun (same SHA `1ce4664f` or current HEAD, 1s host-telemetry sidecar, pinned `taskset -c` control arm) remains the historical measurement detail for either arm; setup cancellation safety is implemented by `950cfd279` (`BotDriveClient.CallAsync` token/timeout, compatible sync `Call`, cooperative A5Tier3 worker deadline/stop, no `Thread.Abort`). Preserve exact SHA, env, and cleanup evidence. No H claim is needed for this load gate. | Scaling/rig lane | [G2-A5 report](scorecard-explorations/generated/g2-a5-acceptance-report.md) + [G2-A3 report](scorecard-explorations/generated/g2-a3-storm-report.md) + [A5 stall dossier](scorecard-explorations/mechanics/a5-physics-stall-investigation-2026-09-01.md) + PB-SOAK ledger entry |
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
