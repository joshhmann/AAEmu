# Gemini Handoff — AAEmu

## Current checkpoint

- The prior Mail S3/PB-007 reconciliation point `241d3e34d` is historical.
  The current clean checkout resolves `origin/develop` to
  `53360edc842d958247dc70aab498cb02ef0bba0e`; this is the current branch
  pointer verified for this handoff. `1638b007c` remains the historical first
  actor-route feature commit, not the branch head.
- Start from [`GEMINI-NEXT-INSTRUCTIONS.md`](GEMINI-NEXT-INSTRUCTIONS.md) for
  the safe temporary-worktree workflow, current MCP evidence boundary, and
  ordered continuation gates.
- Flash reports fifteen additional authenticated actor routes/tools beyond the
  earlier route-count checkpoint: `deposit_money`, `withdraw_money`,
  `deposit_item`, `withdraw_item`, `plant`, `harvest`, `craft`, `buy`, `sell`,
  `pack_pickup`, `put_down`, `load_pack_onto_vehicle`, `board_vehicle`,
  `unboard_vehicle`, and `drive_vehicle`. The current MCP catalog is 39 tools.
- The clean-gate result is SHA-pinned to
  `53360edc842d958247dc70aab498cb02ef0bba0e` from a normal clone:
  `./scripts/gate.sh`; Release build PASS (4 NU1903 warnings, 0 errors);
  compiler check **0/0**; unit suite **2490 total / 2489 passed / 0 failed /
  1 skipped**; MCP stdio smoke **39 tools**. The sole skip is
  `Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
  `AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. The linked-worktree gate
  failure is invalid infrastructure context (`RepoRoot` sees a `.git` file),
  not source evidence. Focused route/MCP/queue validation remains
  Flash-reported **53/53** (`BotActionControllerRouteTests` 2/2,
  `BotControlActionMcpTests` 33/33, `BotActionCommandQueueTests` 18/18).
- Flash reports that the live `discover_self_quests` MCP benchmark passed with
  `action_status` and `trace`, plus an independent MySQL character-row
  cross-check. This current benchmark remains unpinned, not a checked-in
  SHA-pinned artifact; no safe doodad interaction was attempted.
- The earlier asset-missing `mcp-live-smoke-2026-08-27.md` run at
  `7e109d550` remains historical; it recorded Game exiting before WebApi and
  is not the current benchmark verdict.
- Only the later actor expansion remains deferred: Party, Trade, Expedition,
  Auction, and related actions still lack authenticated routes. The Flash
  Deposit/Withdraw, Plant/Harvest, Craft, Buy/Sell, and Pack/Vehicle routes
  are not deferred.
- The exact gate counts below are historical where noted; this docs-only MCP
  wave does not build or validate unrelated source changes.
- The fork boundary is permanent: `origin` is the writable fork (`joshhmann/AAEmu`); `upstream` fetches only and its push URL is `DISABLED`. Never push a branch or PR upstream.
- Target client/data: ArcheAge 1.2, client revision `r208022`. `compact.sqlite3` is read-only canonical reference data; mutable state belongs in MySQL or an additive metadata schema.

## Mission and workflow

The product is a living Classic ArcheAge world. PlayerBots are the test force and population mechanism, not a second gameplay implementation. Preserve the existing Character/session/managers/packet architecture and use real engine paths; do not make a bot-only character, inventory, quest, property, combat, or economy path. New services use explicit dependencies and additive adapters/composition first; narrow core hooks need a concrete reason.

Use this loop for every slice:

1. **Evidence archaeology** — read canonical 1.2 data, existing code, packet offsets, and prior reports; mark VERIFIED, INFERRED, PLAUSIBLE, or UNKNOWN.
2. **Contract** — state the player-visible intent, packet/action, validation, mutation, broadcast, persistence, failure behavior, and evidence boundary.
3. **Vertical slice** — implement the smallest real engine path, with normal client/server lifecycle and no direct DB/Transform/ZoneId/GM shortcuts in the acceptance path.
4. **Client/server proof** — exercise authenticated TCP and inspect wire/state/database outcomes where the slice requires it.
5. **PlayerBot interaction** — compose deterministic bot behavior around ordinary gameplay actions; bots prove function, not feel.
6. **Regression** — preserve a rig contract test and a live scenario as appropriate; test negative/error/idempotency paths.
7. **Blocker** — file the observed failure with layer attribution and exact evidence rather than repairing a symptom.
8. **Repair** — fix the real engine/data/harness layer, retain the historical failure, and re-run the affected proof.
9. **Scaling** — measure only after behavior is correct; default-off machinery must remain neutral when unset.

`H=human` means an actual player completing the curated scenario. A scripted actor, headless client, rig, or bot is functional/proxy evidence (at most H=1 by old scorecard conventions), never H=2. Josh's human verdict remains separate from automated and live-bot evidence.

## What is actually landed

### Foundation and gameplay contract

- **M0–M7 foundation is in the ancestry.** M0 workflow/fork boundary/scorecard/roadmap foundation is closed. M1's quest/progression engine-health work and census are landed; M2's broad census/baseline work is recorded; M3a homestead construction/farming and M3b property/restart persistence are landed; M4 crafting, trade packs, vehicle lifecycle, and integrated economy/navigation proxy flows are landed; M5 A1/B1/M5.1/M5.2/M5.3 gameplay-actor contracts and real movement/action paths are landed; M6 headless lifecycle, presence, metadata, recovery, schedules, and scheduler machinery are landed with the caveats below; M7 adventurer, party, sustain, death recovery, and party-spike slices are landed. These milestones retain deferred human gates and do not imply full 1.2 completion.
- The actor surface includes real-path Observe/Move/Stop/Target/Cast, Interact, Loot, UseItem, Mount/Dismount, AcceptQuest, TurnInQuest, Plant/Harvest, Craft, PackPickup/PutDown, LoadPackOntoVehicle/DriveVehicle, Buy/Sell, party invite/accept/follow/assist, and other merged actions. The golden route and M5 backtrack rules require normal services and honest failure reasons.
- **Quest discovery and progression:** `DiscoverQuests` uses the real `CharacterQuests.AddQuest` pre-flight chain. Quest-surface work added Item, Sphere, Level, and self-discovery channels (about 801 previously hidden offers) plus `Talk` through `DoTalkMadeEvents`; `ConAcceptComponent` remains deliberately deferred because it is a stub with no player-observable precondition. The zone sweep found about 3,000 discoverable NPC/board quests across 57 zone groups, and the first perception-driven `LevelingLoopScenario` completes delivery/ItemGather and hunt objective chains (including quests 329 and 1652 in the recorded slice). This is not yet broad autonomous progression: objective reachability, more objective types, and roughly 900 channel offers remain gaps.
- First-class `InteractWith(doodad)` is landed (`13f502673`): it derives the use skill and fails closed on an observable-effect post-check. This is the contract used to avoid injecting a fake dungeon/portal interaction.

### MCP expansion (2026-08-27)
- MCP sidecars and the management gateway remain client-neutral; availability
  is not external-client actor lifecycle evidence.
- Historical coverage merge `8a22dcb4` and its 33-test / 19-tool smoke record
  are retained. The earlier five-route `1638b007c` checkpoint and its
  then-current validation remain historical, not the current catalog verdict.
- Flash reports fifteen additional authenticated actor routes/tools beyond that
  checkpoint: `deposit_money`, `withdraw_money`, `deposit_item`,
  `withdraw_item`, `plant`, `harvest`, `craft`, `buy`, `sell`, `pack_pickup`,
  `put_down`, `load_pack_onto_vehicle`, `board_vehicle`,
  `unboard_vehicle`, and `drive_vehicle`. The current catalog is 39 tools.
- The clean-gate result is SHA-pinned to
  `53360edc842d958247dc70aab498cb02ef0bba0e` from a normal clone:
  `./scripts/gate.sh`; Release build PASS (4 NU1903 warnings, 0 errors);
  compiler check **0/0**; unit suite **2490 total / 2489 passed / 0 failed /
  1 skipped**; MCP stdio smoke **39 tools**. The sole skip is
  `Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
  `AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. A linked-worktree gate
  failure is invalid infrastructure context (`RepoRoot` sees a `.git` file),
  not source evidence. Focused route/MCP/queue validation remains
  Flash-reported **53/53** (`BotActionControllerRouteTests` 2/2,
  `BotControlActionMcpTests` 33/33, `BotActionCommandQueueTests` 18/18).
- Flash reports that the live `discover_self_quests` MCP benchmark passed with
  `action_status`, `trace`, and an independent MySQL character-row
  cross-check. This is current unpinned evidence, not a checked-in
  SHA-pinned artifact; no safe doodad interaction was attempted.
- The prior asset-missing live-smoke run at `7e109d550` is historical, not the
  current verdict. Only the later Party, Trade, Expedition, Auction, and
  related actor expansion remains deferred; the fifteen Flash routes are not
  deferred.

### Navigation and scaling

- The existing CryEngine `.bai` navigation spine is real: loaders, forbidden polygons, nearest-node/height lookup, A*, and path reduction. The navigation slice fixed the PathNode G-cost defect, replaced the open set with a heap, and added a lazy per-block spatial grid. On the 81-route corridor rig, **81/81** still reached the goal; average detour improved **1.91x to 1.22x** and average planning time improved **6954 ms to 1187 ms** (total 563.3 s to 96.1 s). This is a measured engine rig, not proof that bots can yet traverse every interior or region.
- G2-A3 wake-storm machinery and G2-A5 true dormancy are default-off. A3 full live-TCP storm exercised 1,000 dormant registrations through proximity sweep/materialization/wake and clean dematerialization. A5's accepted follow-up exercised about 100 dormant rows and 10 embodied bots using a real live human trigger; the earlier report header says BLOCKED because its first harness had no dormant seed/trigger, but report §8 closes that harness gap and supplies the measurement. PB-004 (materialized bots did not wake/step) is fixed.
- Tier-3 shape is measured: 1,000 dormant seeded sequentially, 50 embodied, RSS **+0.13%** against the 50-active baseline, wake p95 **280.2 ms** at 1000/50, and steps/min parity **15003 vs 14995**. The 6-hour dormant-timer leg is still pending. Concurrent `seedDormant` corrupts state after about 100 bots; keep seeding sequential.

### Mail, economy, labor, and honor

- Mail return/expiry server logic and receive ownership hardening are landed. The **Mail S3 flow is complete and merged** at `31045d033`; there is no current Mail S3 partial to recover. `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets` passed **1/1 in 2m39s** on isolated MySQL/Docker: real send near a mailbox, kill-9/restart, `SlotType.Mail=5` attachment persistence, receiver retargeting, unread recount after world registration, list/read, sequential item and copper take, exact equipment instance details, and delete persistence. The unread-recount fix moved recount after `TryAddCharacter` and before human client initialization.
- The server-side return/bounce logic is rig-tested, but the client return opcode remains only **strongly inferred as `0x0a2`**. It needs a real 1.2 client capture before any opcode is assigned or registered. COD enforcement and expiry/bounce integration coverage remain open. The old checked-in `ships-rowboat`-style and mail dossier reports contain pre-recovery snapshots; use their later addenda plus STATUS/ROADMAP for landed state.
- **Merchant trio:** `cb514c42e` fixes the funds gate, `beaf9b82e` fixes buyback refund-on-refused-move, and `3ba33b3af` rolls back grant failure atomically; merge `e5db6d390` is on develop. `EconomyDayCycleE2eTests` proved money/bank/items conservation across kill-9 restart. This is bot/live-stack evidence, not a human shop-feel verdict.
- **Labor modes:** consumption is data-driven through skill labor costs and actability XP; caps are 2000 free / 5000 premium. Offline labor addition exists. Online regen machinery exists but is dead-by-default: `TimedRewardsManager.Initialize` has no caller, shipped labor amounts are zero, and no `Labor` config section enables it. Do not silently turn this on; LAB-A is an owner decision (schedule with explicit retail values and a test, or cleanly delete the dead task/config stubs).
- **Honor:** the owner ruling is landed (`8d5a0fb20`): Conflict kills award no honor, War kills use a 40-base value with the existing absolute 4-per-assist split (32 killer + 4/assist), and War victim loss remains −10 clamped at zero. Rig coverage exists; a live player-facing war cycle is not thereby proven.

### Ships, justice, Dominion, and audits

- **TestSlave** remains a GM/developer observation tool, not player-facing feature evidence. It is useful for controlled slave inspection but does not replace a real client scenario.
- **Boat region-sync repair:** the initial rowboat report was a clean failure caused by test-control teleports leaving the owner in the old region, not by dead physics or a 100 m spawn bug. `f33ddf285` added `BotDriveBridge.TeleportWithRegionSync` and routed the direct-mutation sites through normal region handoff. The post-fix live rowboat sequence completed its **12 stages** after region sync: summon/bind/helm movement and steering evidence were visible, including 763 ship frames in 15 seconds, 67.2 m displacement, yaw-rate sign reversal, and clean unbind/despawn. The checked-in generated rowboat report's opening FAIL is historical; the ships-domain §13 addendum is the corrected verdict.
- **Justice crime vertical:** `dcda357a6` / merge `937f16d6d` landed the live crime slice and `MarkDirty()` persistence fix. The real stack passed **8/8 stages**: same-faction kill evidence (bloodstain 878 with Owner=A/Data=B), real `CSReportCrimePacket`, CrimePoint/InfamyPoint change, MySQL crime row and character fields, restart persistence, and the wanted threshold seam. The kill used the documented GM-assist fallback because the safe-zone/mother-shield blocked the attempted ForceAttack kill; this proves the crime vertical, not the unresolved jury/client ordering.
- **Dominion slice 1** is landed (`66f124533` merge): canonical siege zones/settings/plans load, additive `aaemu_game.dominions` persistence, tax-rate packet round trip, phase cron/`SCSiegeAlertPacket`, and kill-9 persistence. Combat/siege battle is explicitly not implemented; declare-trigger UI remains unknown.
- **G3-B5** is done (`46fe4332d`, documented in `3fc64ae26`): seven behavioral scenarios are indexed, failure attribution is named, and all three test-only seams are proven unreachable from player sessions/autonomy by six negative tests.
- **NPC grounding:** `38c4997d3` landed the positive-only clamp plus intentional aerial/water/structure whitelist. The terrain-only replay corrected **593** non-whitelisted severe-positive rows and preserved **702** whitelisted rows. The underlying audit measured 25,118 rows, 23,058 defect-audited rows, 1,295 severe-positive offsets, 670 submerged/suspect rows, and 733 duplicate rows. Terrain-only evidence cannot classify cave/deck/submerged interiors or canonical duplicate ownership; this remains FIXED-PARTIAL, not a claim that every NPC is grounded.

## Evidence and benchmarks

Label evidence correctly; do not turn a rig or a bot into a human claim.

| Area | Verified result | Evidence type and limit |
|---|---|---|
| A5 true dormancy | Around 100 dormant / 10 embodied through the real provisioning and live-TCP human proximity trigger; RSS **+2.09%** over the no-bot baseline; materialization around **260 ms p95** (260.1 ms post-PB-004 fix; earlier p95 251.7 ms) | Live isolated stack with a human network trigger and bots; not a Josh feel/H verdict. |
| A4 autosave | **393.1 ms p95 @ 250 active characters**, zero save skips | Live isolated stack, bot load, real SaveManager; not human evidence. |
| A3 wake storm | At 1,000 registrations, fidelity transition p99 **~0.00008 ms unstaggered / ~0.000061 ms staggered**; 1000/1000 materialized in both arms | Live isolated stack with a real TCP human trigger; scheduler/materializer measurement, not human feel. Incremental counters were rejected as a measured cold spot. |
| Tier 3 | **+0.13% RSS**, wake p95 **280.2 ms @ 1000/50**, steps/min **15003 vs 14995** | Live scaling probe. Six-hour dormant-timer advancement is still unmeasured. |
| Navigation | **81/81**, detour **1.91→1.22**, average plan **6954→1187 ms** | Headless rig driving the real ClientFileManager/Bai/A*/grid chain; not a live client route. |
| Mail S3 | **1/1 in 2m39**, restart/item/copper/ownership/unread/read/take/delete assertions passed | Authenticated real-packet integration E2E on isolated MySQL/Docker; no live human-client return-opcode confirmation. |
| Rowboat | **12 stages after region sync**; 763 ship frames/15 s, 67.2 m displacement, steering sign flip, clean unbind/despawn | Live wire/stack E2E after `f33ddf285`; the old generated report's FAIL was the pre-fix stale-region run. |
| Justice crime | **8 stages** including evidence, report, points, crime row, restart, wanted seam | Live stack with bots and a documented GM-assist kill fallback; jury UI/order is not covered. |
| Merchant/economy | Restart conservation of money, bank, and item counts | Live bot economy cycle across kill-9; not human shop feel. |
| Grounding | 593 corrected / 702 whitelist preserved | Offline engine-identical terrain harness plus targeted policy tests; terrain-only, not cave/deck/submerged truth. |

The requested checked-in `pvp-handshake-e2e-report.md` is not present in this checkout. Current PB-007 evidence is in `playerbot-blockers.md`, `pvp-domain.md`, `STATUS.md`, and the `PvpHandshakeE2eTests` source. Do not invent a missing report or treat the prior live run's immune-tagged frame as proof of a non-immune damage frame.

## Active blockers and partials

- **PB-001 — routed navigation:** **IMPLEMENTATION LANDED / TEST EVIDENCE
  PARTIAL** — `IGameplayActor.NavigateTo` is implemented with CryEngine
  navmesh A* routing, dynamic waypoint stepping, stuck detection, and
  straight-leg fallback. The named six-test `GameplayActorNavigateTests` file
  is not tracked in this checkout; it exists only as a prototype under
  `.worktrees/recovery`. Do not treat that prototype as checked-in proof.
- **PB-002 — autonomous leveling loop:** **SCOPED ACTOR/RIG SLICES LANDED;
  BROAD CLAIM OPEN** — `LevelingLoopScenario` and its actor/rig slices cover
  selected perception-driven quest steps, but broad autonomous quest-loop
  coverage, live-server breadth, and human/client breadth remain open. Do not
  promote the scoped implementation to broad completion.
- **PB-005 — grounding FIXED-PARTIAL:** 593 non-whitelisted severe-positive rows were corrected and 702 intentional whitelist rows preserved. Cave/deck/submerged classification and the 733 duplicate-row ownership decision remain open. No negative-offset clamp and no duplicate deletion without canonical evidence/owner approval.
- **PB-007 — open but narrowed:** rig proof passes through real `Skill.Use`; same-faction `ForceAttack` damage lowers victim HP, Retribution is present, and first application/Refresh wire evidence exists. The live non-immune, victim-matched `SCUnitDamaged` frame is still unproven. The login `LoggedOn` buff 2423 protects all damage for roughly 20 seconds; the engine now records the crime attempt even on that immune path. Do not call the live slice closed from the rig or from an immune-tagged frame.
- **Justice:** the crime vertical is complete, but jury summon packet ordering/client capture remains unknown. Prison sentencing/teleport/buff exist; prison labor, escape tunnels, guards, and release-on-expiry are absent. Treat those as separate scope decisions.
- **Dominion:** persistence/tax/phase slice is complete; combat/siege-battle and declare-trigger UI are not.
- **Mail:** Mail S3 is merged and complete; no S3 partial remains. Open follow-ups are real-client confirmation of the return opcode (candidate `0x0a2`, still not a fact), COD enforcement, and expiry/bounce integration proof.
- **Scaling:** Tier-3 six-hour dormant-timer soak remains pending. Keep dormant seeding sequential because concurrent `seedDormant` corrupts server state at roughly 100 bots. Historical M6 soak evidence passed revised approved budgets, but that does not erase the remaining exit-label/deferred-gate decisions.
- **Human H gates:** M1 Solzreed route, original M2 two-player baseline, M3a contract replay, M4 economic/navigation replay, and the human-feel dimensions generally remain Josh-owned UNKNOWN. Bot completion is not permission to promote H.

## Surviving worktrees and safety

The current untracked `.worktrees/` directory contains these retained survivors/artifact directories:

- Recovery artifact directories with SQL snapshots: `.worktrees/pvpfix`, `.worktrees/pvphs`, `.worktrees/boatsfix`, `.worktrees/justice1`, `.worktrees/dominion1`, `.worktrees/tier3`, and `.worktrees/mails3`.
- Registered retained worktrees: `.worktrees/b1-interact-loot` (`land-b1-interact-loot`), `.worktrees/crafting-dossier` (`crafting-dossier`), `.worktrees/gate-t6c952150` (`gate/t6c952150-verify`), `.worktrees/m3-canonical-audit` (`m3-canonical-audit`), `.worktrees/trade-packs-dossier` (`trade-packs-dossier`), and `.worktrees/vehicles-ships-dossier` (`vehicles-ships-dossier`).
- Detached retained probes: `.worktrees/nav-probe` at `41ddb889a`, `.worktrees/pb007-live` at `9359b5a38`, `.worktrees/rowboat` at `bfbea4093`, and `.worktrees/rei-g1-rv2` at `ae4ccf385`.
- An additional registered rig worktree is outside `.worktrees/`: `/root/.hermes/kanban/workspaces/t_6b5ac43e/rig-repo` at `e7e7ef0fe`.

Do not add `.worktrees/` wholesale to a commit. Do not delete, reset, or overwrite a survivor before inspecting its branch, dirty state, and evidence role. In particular, `.worktrees/rowboat` and `.worktrees/nav-probe` are stale/research duplicates, not proof that their unmerged changes are on develop; avoid confusing them with the landed region-sync and nav-slice commits. `git worktree list --porcelain` also shows prunable `/tmp` research worktrees; do not use those as current source. The nested `.hermes`/`rig-repo` topology anomaly is preserved for owner reconciliation; do not manipulate it.

The compact SQLite database is SELECT-only. Never patch it in place; use reviewed code/config or an additive SQL/MySQL migration when mutable state is required.

## Immediate next instructions for Gemini

1. From a clean temporary worktree, run the clean gate and publish its output
   with the exact HEAD SHA, command, environment/assets, build/compiler result,
   unit totals (including skip identity), and downstream MCP-smoke result.
2. Land the PB-001 real-data `GameplayActorNavigateTests` contract tests, or
   explicitly explain why they cannot be landed; the existing prototype under
   `.worktrees/recovery` is not tracked evidence.
3. Narrow PB-002 to the landed actor/rig slices; keep broad autonomous
   quest-loop coverage and live/human breadth open.
4. Pursue PB-007 proof using a victim-matched, non-immune live
   `SCUnitDamaged` frame with the existing HP/Retribution/crime checks.
5. Preserve the nested `.hermes`/`rig-repo` topology anomaly for owner
   reconciliation; do not manipulate that topology.

## Exact next steps for Gemini

1. **Establish the checkpoint before editing.** Follow
   [`GEMINI-NEXT-INSTRUCTIONS.md`](GEMINI-NEXT-INSTRUCTIONS.md): inspect the
   dirty main checkout read-only, fetch `origin/develop`, and create a clean
   temporary worktree. Confirm the live `origin/develop` SHA there; do not run
   the gate from the dirty main checkout. The prior `241d3e34d` relationship
   and the 2479/0/1 Mail S3 checkpoint are historical.
2. **Do not rerun PB-007 live blindly.** First use the targeted TUnit selector and/or add a narrowly scoped server branch trace. Preserve corrected packet framing and dump buff state/immune status. Close PB-007 only after a victim-matched, non-immune `SCUnitDamaged` frame is observed on the real server, alongside the existing HP/Retribution/crime checks. When the selector works, the known form is:
   ```bash
   dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build \
     --treenode-filter '/*/*/PvpAggressionSeamRigTests/<method>' \
     --output Normal --log-level Error
   ```
3. **Choose one high-leverage gameplay slice and state its evidence contract before code.** Preferred options are: (a) justice trial packet-order/client capture if a suitable client/capture is available; (b) Mail return client confirmation; or (c) the PB-001 navigation route-planner/coarse-travel slice. Keep canonical data and human-vs-bot boundaries explicit.
4. **Handle PB-005 owner decisions separately.** Classify cave/deck/submerged rows only with canonical/client evidence. Do not add a negative-offset clamp, delete duplicate rows, or reclassify whitelist entries without a registered owner decision and evidence.
5. **For every new slice, add/update both a rig proof and a live scenario where applicable, file a blocker for any failure, and update `SCORECARD.md`, `ROADMAP.md`, and `STATUS.md` in the same documentation wave.** Preserve old evidence and label rig/live/human types rather than rewriting history.
6. **Run the scoped tests and commit the scoped change.** IntegrationTests convention is `--filter-class <fully-qualified-class-name>`. TUnit uses `--treenode-filter` as above when it resolves. `--nologo` is rejected by the MTP front-end in prior runs; omit it. Push only to the writable origin fork, never upstream.
7. **Live MCP benchmark:** Flash reports the authenticated
   `discover_self_quests` benchmark passed with `action_status`, `trace`, and
   an independent MySQL character-row cross-check; this remains unpinned
   evidence pending a clean SHA-pinned rerun. No safe doodad interaction was
   attempted. The fifteen Flash route families are landed; only later Party,
   Trade, Expedition, Auction, and related actor expansion remains deferred.

## Human-only actions

- Use the QAT packet at `Docs/JOSH-QAT-WAVE4.md` for the owner-controlled wave: mail return hypothesis, ownership guards, labor regen, war-gated honor, NPC grounding, boats, TestSlave observation, and Mirage walk.
- Production may need a deployment at or beyond the current fork head before QAT; the historical production image is not proof of the current docs/source checkpoint.
- Obtain real client packet captures for jury summon ordering, Dominion declare/UI behavior, and the mail return opcode. A server/rig trace cannot substitute for those client contracts.
- Josh must provide all H/human-feel verdicts, including the golden route and deferred M2–M4 replay gates.

## Stop conditions

Do not guess packet IDs, formulas, canonical coordinates, or timers. Do not claim that a bot's success feels like a human's success. Stop when evidence conflicts; preserve both claims with provenance and isolate flaky infrastructure from gameplay conclusions. Never turn a missing capture into a plausible opcode, and never turn a rig-only result into live proof. Never reset/delete a survivor worktree without inspection. Never push upstream.
