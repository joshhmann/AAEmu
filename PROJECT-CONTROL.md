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
- **Audited source/evidence baseline:** `f8db90cc4ac6f5d3434c18e067335f2647bc484a`
- **Current branch:** `develop` may advance through documentation-only commits
  after that baseline; this follow-up does not change source or evidence claims.
- **Evidence date:** 2026-08-28

The audited source/evidence baseline above is newer than several cited reports.
Each report retains its own run date and SHA; an older report is not a rerun at
`f8db90cc4ac6f5d3434c18e067335f2647bc484a`. No claim below promotes a human
(`H`) result from bot, rig, or server-only activity.

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
| M2 Golden-path baseline | G1 census gate done; the original two-player human baseline remains Josh-owned and open. |
| M3a Homestead shell | Closed on scripted-actor proxy evidence; the contract replay and human result remain open. M3b persistence is separately closed. |
| M4 Trade/craft/transport | Merged/deployed engineering evidence exists; the normal movement/vehicle economic replay and human-feel decision remain open. |
| M5 Gameplay Actor Contract | Contract surface and bot-functional evidence are landed; H remains unknown. |
| M6 Deterministic playerbot framework | B4 restart engineering is complete, but the full M6 exit-label decision remains open; no current six-hour dormant-timer soak is claimed. |
| M7 Adventurer/party bots | Spike and party slices are landed; broader gameplay breadth and human-feel acceptance remain scoped follow-ups. |

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

| **M1 human gate — Solzreed route** | **Unknown (H open)** | **Unknown (proxy route; autonomous parity not recorded)** | **A/R/L proxy:** M1 census and restart evidence are retained; the M1/M2 control-plane replay completed the curated route through real actor actions. | **H:** Josh walks the reproducible golden route from reset, without GM repair, and records the feel verdict. | Josh | [Golden Route](Docs/wiki/Golden-Route-Solzreed.md) + M1 row in [evidence ledger](EVIDENCE-LEDGER.md) |
| **M2 human gate — original baseline** | **Unknown (H open)** | **Unknown (proxy route; autonomous parity not recorded)** | **A/R/L proxy:** G1 census and restart/clean-host legs passed; the full-route replay is explicitly proxy evidence. | **H:** two players/accounts complete the original baseline from reset with no GM repair; record deviations and verdict. | Josh | M2 row in [ROADMAP deferred gates](ROADMAP.md) + [evidence ledger](EVIDENCE-LEDGER.md) |
| **M3a human gate — contract replay** | **Unknown (H open)** | **Unknown (scripted-actor proxy; autonomous parity not recorded)** | **R proxy:** scripted actors covered placement, construction, crops, storage, and furniture in one session. | **L/H:** replay through `Housing.Build → Plant/Harvest → storage → craft` using normal contract actions; Josh records client feel rather than treating the rig as UAT. | M3a lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M3a ledger row |
| **M4 human gate — economic/navigation replay** | **Unknown (H open)** | **Unknown (authentic no-intervention parity not recorded)** | **R/L proxy:** integrated scripted route and per-object restart E2Es cover harvest → craft → pack → vehicle → sell; prior direct transform/zone assignment is not authentic acceptance. | **L/H:** run the route with normal movement and vehicle controls, no direct Transform/ZoneId/GM/reflection/DB shortcuts; confirm payout/labor conservation and feel. | M4 lane; Josh for H | [M4/M5.x packet](docs/JOSH-QAT-PACKET-M4-M5.x.md) + M4 ledger row |
| **M6 human/exit gate — B4 restart** | **N/A (restart gate)** | **Yes (R/L B4 bot replay)** | **R/L:** two-checkpoint `B4BotRestartPersistenceE2eTests` and direct metadata assertions pass; status says engineering complete. | **Decision/H where applicable:** Josh/maintainers record the full M6 exit label against the approved soak and B4 evidence; do not relabel the preserved soak or infer H from bots. | Josh / M6 maintainers | [M6 roadmap exit record](ROADMAP.md) + [evidence ledger](EVIDENCE-LEDGER.md) |
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
