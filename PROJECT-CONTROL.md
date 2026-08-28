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
**Audited source/evidence baseline:** `ded008de8d67ece8718e9235fd02503b43ceb6a1`
(`origin/develop`, verified current HEAD).

The M6 per-run ownership hardening is recorded at source/test commit
`c4f2296c` (movement guards complete) with full-suite singleton isolation at
`f6ff58e86`; the preceding M5/M4 history remains preserved. The M6 focused
aggregate is **105/105** with no failures: BotPresenceCoordinator 13/13
(including patrol and transfer-finalize regression), BotRoamStepExecutor 6/6,
PlayerBotScheduler 26/26, DeathWatch 5/5, Metadata 15/15, Manifest 13/13,
Manager 19/19, and Headless provisioning 8/8. The M7 focused aggregate is
**147/147** with no failures or skips: primary 36/36 and actor support 111/111.
No six-hour soak exists; `SeedBox` cancellation remains unresolved. No H/UAT
claim is inferred from these A/R proxy results.

## Authoritative records

| Record | Use |
|---|---|
| [`STATUS.md`](STATUS.md) | Current fork checkpoint, milestone narrative, open human gates, and recent reconciliations |
| [`ROADMAP.md`](ROADMAP.md) | Locked milestone requirements, deferred validation gates, and next-wave objectives |
| [`SCORECARD.md`](SCORECARD.md) | Mechanic evidence dimensions and conservative current scope |
| [`EVIDENCE-LEDGER.md`](EVIDENCE-LEDGER.md) | Append-only milestone evidence states and human-feel boundary |
| [`PLAYERBOT_BLOCKER ledger`](scorecard-explorations/playerbot-blockers.md) | Active bot blockers and retained resolutions |
| [`PlayerBot Capability Matrix`](scorecard-explorations/mechanics/playerbot-capability-matrix.md) | Perceive / Decide / Act / Verify and autonomous-loop view |

### Dossiers and reports

- Dossiers: [navigation](scorecard-explorations/mechanics/navigation-domain.md),
  [justice](scorecard-explorations/mechanics/justice-domain.md),
  [pvp](scorecard-explorations/mechanics/pvp-domain.md),
  [ships](scorecard-explorations/mechanics/ships-domain.md),
  [mail](scorecard-explorations/mechanics/mail-domain.md),
  [dominion](scorecard-explorations/mechanics/dominion-domain.md),
  [economy](scorecard-explorations/mechanics/economy-domain.md),
  [indun](scorecard-explorations/mechanics/indun-domain.md).
- Current or recently reconciled reports: [G2-A3 wake storm](scorecard-explorations/generated/g2-a3-storm-report.md), [G2-A5 acceptance](scorecard-explorations/generated/g2-a5-acceptance-report.md), [PB-007 handshake](scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md), [PB-002 leveling loop](scorecard-explorations/generated/leveling-loop-2026-08-25.md), [NPC grounding audit](scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md), [rowboat report](scorecard-explorations/generated/ships-rowboat-e2e-report.md), and [integrated MCP benchmark](scorecard-explorations/generated/integrated-mcp-e2e-benchmark-2026-08-27.md).
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
| M5 Gameplay Actor Contract | Contract surface and bot-functional evidence are landed; H remains unknown. |
| M6 Deterministic playerbot framework | Current source/test HEAD `ded008de8d67ece8718e9235fd02503b43ceb6a1`; clean ordinary Character/bot dormant → proximity wake/materialize → scheduled action → identity/inventory/position/metadata restart preservation → safe dematerialization. M6 focused A/R evidence **105/105**: BotPresenceCoordinator 13/13, BotRoamStepExecutor 6/6, PlayerBotScheduler 26/26, DeathWatch 5/5, Metadata 15/15, Manifest 13/13, Manager 19/19, Headless provisioning 8/8. Movement guards `c4f2296c`; singleton isolation `f6ff58e86`. No six-hour soak; SeedBox cancellation unresolved. |
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

Apply the loop-closure definition above to each row. The evidence column names
the current label and scope; the next-action column names any remaining
functional, live, or human/client gate.

**Evidence labels:** **A** = automated/contract; **R** = deterministic rig or
PlayerBot proxy; **L** = live authenticated server/client; **H** = human/client
feel. `H unknown` is intentional where Josh has not run the gate. A/R/L never
becomes UAT. “Missing action” is the next evidence action, not a claim that it
has already happened.

| Objective / gate | Player closes loop? | Bot closes loop autonomously? | Loop-closure evidence (A/R/L/H) | Remaining QAT/UAT action | Owner | Acceptance artifact |
|---|---|---|---|---|---|---|

| **M1 human gate — Solzreed route** | **Unknown (H open)** | **Yes for bounded 254→255; Unknown/Open for the full M1 route** | **M1 player loop:** from a clean Nuian character in Solzreed, discover legal quests, pursue their objectives through ordinary player actions, turn them in through the normal path, reach the first-mount unlock, and verify restart persistence. **A/R proxy:** `LevelingLoopScenario` closes the bounded 254→255 loop by `Observe → Discover → legal lowest-level choice → objective pursuit → turn-in → re-discover`; focused test 1/1 and `LevelingLoopScenarioRigTests` 7/7 at source/test baseline `7a572c08a32162988dedbf400bd9f8b608fb1974`, with evidence in [leveling-loop report](scorecard-explorations/generated/leveling-loop-2026-08-25.md). `M1M2ReplayScenario` is a 16-quest ordered scripted replay (55 actor records in the fixture report), includes fixture `Level=6` setup, and has no real-mount criterion; it is proxy evidence, not autonomous decision closure. | **H/UAT:** Josh walks the reproducible fresh-Nuian route from reset without GM repair, including first-mount, restart, Bloody Hand, and bounty-board checks, and records the feel verdict. | Josh | [Golden Route](Docs/wiki/Golden-Route-Solzreed.md) + [M1 row in evidence ledger](EVIDENCE-LEDGER.md) |
| **M2 human gate — original baseline** | **Unknown (H open)** | **Unknown/Open (ordered-manifest proxy; no Observe/Discover/legal-choice decision closure)** | **A/R proxy only:** at source/test HEAD `ba530bcebec12af2bc7dc0db7451a535665bbed3`, focused deterministic aggregate is 32/32 pass (the seven classes are recorded in the M2 milestone row); `PlayerbotPilotTests` 30/30 cycles and restart 2/2 are ordered-manifest/contract replay, while `M1M2ReplayScenario` is a fixed 16-quest order with a declared no-real-mount criterion. `QuestScenarioTierTests` itself is 1/1, but its observed per-quest census is 4463 PASS / 110 FAIL / 14 SKIP over 4587 (T1 fail 6280); these remain evidence findings, not an M2 closure claim. | **H:** two players/accounts complete the original baseline from a clean reset with no GM repair; record deviations and verdict. | Josh | M2 row in [ROADMAP deferred gates](ROADMAP.md) + [evidence ledger](EVIDENCE-LEDGER.md) |
| **M3a human gate — contract replay** | **Unknown (H open)** | **Unknown (scripted/fixture proxy; autonomous parity not demonstrated)** | **R proxy:** the ordinary loop is `Character → place/build → plant/harvest → storage/coffer/furniture state → observable ownership/contents result`; `M3aExitScenarioTests` is 1/1 with two scripted actors and one uninterrupted session, while `M3aM4ReplayScenario` follows ordered stages and fixture setup rather than selecting actions from observations. Prior exact source/test baseline `b9a72825f` recorded the M3 focused aggregate 178/178 (named slices: M3a exit 1/1, M3b furniture 4/4, phase restart 10/10, property policy 11/11, repair scanner 13/13). Current HEAD `a77ef878d8fcba297c32c0228e712e0695cc4887` includes source commit `1a3f13dc1`; `HousingStorageFurnitureTests` 13/13 adds unauthorized coffer refusal before `OpenedBy` mutation. Fixture SetPosition/direct service setup is not acceptance evidence. | **L/H:** run the loop with ordinary client actions and no direct Transform/ZoneId/GM/reflection/DB setup shortcuts; Josh records ownership, contents, and feel. No live-client claim is made by the proxy. | M3a lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M3a ledger row |
| **M3b property persistence gate** | **Unknown (engineering/re-entry gate; H open)** | **Unknown/Open (ordered persistence script; no autonomous re-entry decision closure)** | **R/L engineering evidence:** the separate persistence loop is place/decorate → restart/load/assert → plant → restart/load → observed in-flight save kill -9 → restart/assert → mature/harvest state → DB-container kill during save → restart/assert → final re-entry. `M3bExitPersistenceE2eTests` is a seeded/bridge/DB crash harness, not a PlayerBot loop. Prior exact source/test baseline `b9a72825f` recorded the M3 focused aggregate 178/178; current permission fix HEAD is `a77ef878d8fcba297c32c0228e712e0695cc4887`, with `HousingStorageFurnitureTests` 13/13. | **L/H:** run the M3a client loop separately, then execute the isolated restart/re-entry harness with preserved row/transform/phase/contents assertions; do not infer player feel or autonomous behavior from crash evidence. | M3b lane; maintainers/Josh | [ROADMAP M3b](ROADMAP.md) + [M3b ledger row](EVIDENCE-LEDGER.md) |
| **M4 human gate — economic/navigation replay** | **Unknown/Open (H open)** | **Unknown/Open (ordered scripted/fixture proxy; autonomous decision closure not demonstrated)** | **M4 player loop:** clean ordinary `Character` gather/harvest → craft pack → carry/place → load owned vehicle → drive normal route → unload → `SellSpecialty` reward → repeat, with per-object restart/persistence as applicable. `SellSpecialty` composes the canonical CSSellBackpackGoodsPacket → SpecialtyManager path, with merchant/pack checks, pack-consumption postcondition, same-zone/no-pack refusal, repeat-cycle, and idempotency coverage. Current source/test HEAD `6ff68e1bb4a6afe08441308acb9a485b5133c42e`; focused results: `M4ExitIntegratedSessionTests` 2/2, `EconomyDayCycleScenarioRigTests` 4/4, `M3aM4ReplayScenarioRigTests` 2/2. Full normal-clone gate: 2498 total / 2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools; skip `Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG` and `AAEMU_E2E_DB_PASSWORD`. Forced rebuild report: 1067 warnings / 0 errors. Existing property/economic replay is ordered scripted/fixture proxy and its direct setup shortcuts are not authentic acceptance; no live M4 restart/vehicle proof was run because the shared E2E reset is unsafe. | **L/H:** run the full route with normal client movement/vehicle controls and no direct Transform/ZoneId/GM/reflection/DB shortcuts; execute isolated restart/vehicle checks; Josh records reward, ownership, persistence, and feel. | M4 lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M4 ledger row |
| **M5 actor decision/action loop** | **Unknown (H/client gate where applicable)** | **Unknown/Open (universal decision loop)** | **Loop sentence:** a clean ordinary `Character` observes current state, chooses one legal objective/action, executes via `IGameplayActor`/normal `Character` services, observes terminal state/audit, and retries safely without duplicate effects. **A/R:** `GameplayActor`/M5 contract lifecycle, single-writer, failure taxonomy, timeout/stuck, idempotency, and audit; focused M5 tests total **316/316** (`BotGoalArbiterTests` 14/14, `GameplayActorM53CoreSurfaceTests` 13/13, `PlayerBotControllerAdapterTests` 5/5, `GameplayActorB1ContractLayerTests` 17/17, `GameplayActorTests` 30/30). `LevelingLoopScenario` is narrow autonomous 254→255; `BotScenarioRunner`/`M1M2`/`M3aM4` remain ordered proxy replays. | **A/R:** preserve the actor-contract proof, then add a reusable legal-candidate decision closure; **H/L:** run the ordinary player/client path where applicable. Existing fixed Priority-first `CanActivate`/FSM scheduling is not a reusable candidate/score/blackboard/rationale/replan/personality policy. | M5 lane; Josh for H/client | [M5 roadmap requirements](ROADMAP.md) + [M5 actor contract](SCORECARD.md) + [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md) |
| **M6 human/exit gate — B4 restart** | **N/A (restart gate)** | **Unknown/Open (harness-driven lifecycle; autonomous decision closure not demonstrated)** | **A/R:** current M6 focused evidence is **105/105** at `ded008de8d67ece8718e9235fd02503b43ceb6a1`: BotPresenceCoordinator 13/13 (patrol + transfer-finalize regression), BotRoamStepExecutor 6/6, PlayerBotScheduler 26/26, DeathWatch 5/5, Metadata 15/15, Manifest 13/13, Manager 19/19, Headless provisioning 8/8; `c4f2296c` movement guards and `f6ff58e86` test-scoped singleton isolation are verified. The loop is dormant → proximity wake/materialize → scheduled action → restart identity/inventory/position/metadata preservation → safe dematerialization. | **Decision/H:** no six-hour soak exists; SeedBox cancellation remains unresolved; Josh/maintainers record the M6 exit label without inferring H from bots. | Josh / M6 maintainers | [M6 roadmap exit record](ROADMAP.md) + [evidence ledger](EVIDENCE-LEDGER.md) |
| **M7 adventurer/party loop** | **Unknown (H/client gate open)** | **Unknown/Open (A/R rig/proxy only; broad autonomous decision closure not demonstrated)** | **A/R:** current focused M7 evidence is **147/147** no-fail/no-skip: primary 36/36 (Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4, PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support 111/111. Hunt kill uses real `DoOnMonsterHuntEvents` with fixture HP=0; Party spike is synthetic/fixture. | **L/H:** no current live authenticated-client run or H/UAT; keep bounded LevelingLoop 254→255 only. Broad decision closure, real damage/Npc.DoDie, scheduler-driven route, party roles/regroup/restart/disconnect, mount/travel remain open. | M7 bot lane; Josh for H | [M7 roadmap reconciliation](ROADMAP.md) + [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md) |
| **PB-001 routed navigation** | **Unknown (live/H open)** | **Unknown (contract/nav coverage; autonomous loop open)** | **A/R:** landed `IGameplayActor.NavigateTo`; tracked `GameplayActorNavigateTests` five-test run passes and `BaiNavigationRigTests` covers GeoData/navmesh. | **L/H:** exercise interior and cross-region routes on the live stack and have Josh assess movement feel; broad coverage is still open. | Navigation lane; Josh for H | [Blocker PB-001](scorecard-explorations/playerbot-blockers.md) + focused test result in [STATUS](STATUS.md) |
| **PB-002 autonomous progression** | **Unknown (broad route open)** | **Unknown (broad autonomous loop open)** | **A/R:** discovery/talk/template and `LevelingLoopScenarioRigTests` slices pass; item-use quest 252 is real-path evidence. Canonical interaction candidate quest 270 remains failed; broad claim is open. | **R/L/H:** expand runnable-content selection and objective execution, resolve or explicitly classify the quest-270 interaction, then run a live/client progression slice. | Quest/playerbot lane; Josh for H | [PB-002 blocker](scorecard-explorations/playerbot-blockers.md) + [leveling report](scorecard-explorations/generated/leveling-loop-2026-08-25.md) |
| **PB-005 NPC grounding** | **N/A (audit outcome)** | **N/A (audit outcome)** | **A:** terrain replay corrected 593 non-whitelisted severe-positive rows; 702 intentional whitelist rows are unchanged. Cave/interior, submerged classification, and duplicate ownership remain unresolved. | **H:** Josh runs the W4-5 grounding tour and records coordinates/screenshots; engineering then classifies cave/deck/submerged findings and duplicate rows. | Server/data lane; Josh for H | [Grounding audit](scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md) + [W4-5 packet](Docs/JOSH-QAT-WAVE4.md) |
| **PB-007 narrow PvP** | **Unknown (H open)** | **Unknown (live login, not PlayerBot parity)** | **L:** isolated real-login E2E passes the flagged-aggression handshake and Peace block; this closes only the narrow handshake. | **L/H:** run the deferred WAR-HONOR (>251 hostile kills plus conflict timer) and broader PvP/honor/client-feel scope; do not reuse the handshake pass. | PvP lane; Josh for H | [PB-007 report](scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md) + W4-4 packet |
| **A5 / Tier-3 dormancy** | **N/A (load gate)** | **N/A (load gate)** | **A/R/L:** A5 near-term gate passes with 100 dormant/10 embodied, RSS +2.09%, materialize p95 251.7 ms; G2-A3 1,000-bot transition p99 passes. | **A/R/L:** run an approved six-hour dormant-timer soak after the `SeedBox` synchronous bridge/`Thread.Join` cancellation blocker is addressed; preserve exact SHA, env, and cleanup evidence. No H claim is needed for this load gate. | Scaling/rig lane | [G2-A5 report](scorecard-explorations/generated/g2-a5-acceptance-report.md) + [G2-A3 report](scorecard-explorations/generated/g2-a3-storm-report.md) + PB-SOAK ledger entry |
| **Mail** | **Unknown (client/UAT open)** | **Unknown (no PlayerBot parity recorded)** | **L:** Mail S3 authenticated restart E2E passed equipment/copper persistence, ownership, unread count, take, and delete. | **L/H:** capture the real-client return opcode (0x0a2 remains strongly inferred), run W4-1/W4-2 ownership UI checks, and close COD plus expiry/bounce follow-ups. | Mail lane; Josh for client capture | [Mail dossier](scorecard-explorations/mechanics/mail-domain.md) + [W4 mail packet](Docs/JOSH-QAT-WAVE4.md) + S3 note in [STATUS](STATUS.md) |
| **Dominion** | **Unknown (client/UAT open)** | **Unknown (autonomous loop not recorded)** | **L:** slice-1 persistence, phase schedule/tax update, and kill-9 reload are recorded. | **L/H:** exercise real declare-trigger UI and later combat/siege-battle slices; current persistence does not imply combat or client UI acceptance. | Dominion lane; Josh for UI | [Dominion dossier](scorecard-explorations/mechanics/dominion-domain.md) + [ROADMAP slice](ROADMAP.md) |
| **Ships** | **Unknown (client/UAT open)** | **Unknown (autonomous loop not recorded)** | **L (current fix context) / historical:** PB-006 records the region-sync fix and live sailing proof; the checked-in rowboat report is the pre-fix failure and remains historical. | **L/H:** rerun W4-6 B1–B6 on the current source/deploy, including steering, disembark/despawn, passenger view where available, and shipyard restart caveats. | Ships lane; Josh for feel | [Ships dossier](scorecard-explorations/mechanics/ships-domain.md) + [W4-6 packet](Docs/JOSH-QAT-WAVE4.md) + historical [rowboat report](scorecard-explorations/generated/ships-rowboat-e2e-report.md) |
| **MCP boundaries** | **N/A (client-neutral boundary)** | **N/A (client-neutral boundary)** | **A/L:** current catalog is 39 tools; focused route/MCP/queue checks are reported 53/53 and the integrated benchmark proves authenticated management/action lifecycle plus DB cross-check. Managed headless bots are client-neutral; authenticated wire leg is explicitly blocked. | **L:** retain protocol and management evidence, add a client-login-allowed packet/state leg if needed, and document route/tool scope without presenting MCP or headless evidence as client UAT. | MCP/control-plane lane; Josh for client leg | [MCP benchmark](scorecard-explorations/generated/integrated-mcp-e2e-benchmark-2026-08-27.md) + MCP sections in [STATUS](STATUS.md) and [capability matrix](scorecard-explorations/mechanics/playerbot-capability-matrix.md) |

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
