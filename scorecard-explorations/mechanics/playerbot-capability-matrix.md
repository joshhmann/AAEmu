# PlayerBot Capability Matrix (Perceive / Decide / Act / Verify)

Populated from implementation reality at local source/test HEAD
`0ce518ac03a18de00fff1516aa9e794e8566bee6`. M5 proposal
`263ecc66c474ca1c5f4b085e86ef3e47f49fd1` adds the bounded
`BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` primitive,
integrated into `LevelingLoop`'s quest-accept choice. It preserves immutable
observed context, enforces hard legality before preference, bounds candidates,
selects deterministically by fixed priority/personality/tie-break, requires a
terminal postcondition, and dispatches through existing `GameplayActor`;
focused `BotDecisionProposalTests` pass **5/5**. This is a decision primitive
plus scoped quest consumer, not universal bot autonomy; broad M5 policy remains
open.

M6 cancellation `950cfd279` adds token/timeout-aware `BotDriveClient.CallAsync`
while sync `Call` remains compatible; A5 and A5Tier3 seed bridge calls are
async, and Tier3 workers share cancellation/deadline propagation with
cooperative stop and no `Thread.Abort`.
BotDriveClientCancellationTests pass **3/3**; SoakOwnershipTests **2/2**;
BotPresenceCoordinator **13/13**. Full normal-clone gate: **2504 total / 2503
passed / 0 failed / 1 skipped**, compiler **0/0**, MCP stdio **39 tools**. The
sole skip is `Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
`AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. No six-hour soak or M6
full-exit result exists; no H/UAT claim is made. Historical reports and
source/test SHA boundaries remain preserved.

## M2 loop reconciliation (2026-08-28)

M2's player loop is **clean reset → ordinary golden-path baseline
quest/progression → required first-mount/baseline state → restart/clean-state
persistence verification**. At source/test HEAD
`ba530bcebec12af2bc7dc0db7451a535665bbed3`, the focused deterministic A/R
normal-clone aggregate is **32/32 passed, 0 failed, 0 skipped** across
`HeadlessSessionProvisioningTests` 8/8, `M1M2ReplayScenarioRigTests` 3/3,
`M1M2ReplayCastWindowRigTests` 1/1, `PlayerbotPilotTests` 6/6,
`QuestScenarioTests` 12/12, `QuestScenarioTierTests` 1/1, and
`QuestDataCensusTests` 1/1. `PlayerbotPilotTests` 30/30 cycles and restart
2/2, and `M1M2ReplayScenario`'s fixed 16-quest order, are
ordered-manifest/contract proxy evidence; the replay mount criterion reports
no real mount. The tier test method pass does not promote its observed
4463 PASS / 110 FAIL / 14 SKIP over 4587 census to an M2 closure claim.
For M2, **Player closes loop = Unknown/H open** and **Bot closes loop
autonomously = Unknown/Open**: no `Observe → Discover → legal-choice`
decision closure is recorded. No live/client/H evidence is implied. The
original two-player/no-GM baseline remains Josh-owned and deferred.

## M3 loop reconciliation (2026-08-28)

M3a's player loop is the clean ordinary `Character` path **place/build →
plant/harvest → storage/coffer/furniture state → observable ownership/contents
result**. M3b restart persistence is a separate loop. The prior exact
source/test baseline `b9a72825f` recorded the M3 focused aggregate **178/178**:
M3a exit 1/1, M3b furniture 4/4, phase restart 10/10, property policy 11/11,
and repair scanner 13/13. Current source/test HEAD is
`a77ef878d8fcba297c32c0228e712e0695cc4887`, including source commit
`1a3f13dc1`; `HousingStorageFurnitureTests` passes 13/13 with authorized-owner
open and unauthorized refusal before `OpenedBy` mutation. The property replay
is an ordered scripted/fixture proxy; fixture `SetPosition`/service
preparation is not an acceptance path. Player/H UAT and live-client evidence
remain open.

## M4 loop reconciliation (2026-08-28)

M4's clean ordinary `Character` loop is **gather/harvest → craft pack →
carry/place → load owned vehicle → drive normal route → unload → sell
specialty pack for reward → repeat**, with per-object restart/persistence as
applicable. `SellSpecialty` now composes the canonical
`CSSellBackpackGoodsPacket → SpecialtyManager.SellSpecialty` path with
ordinary merchant/pack checks, pack-consumption postcondition,
same-zone/no-pack refusal, repeat-cycle, and idempotency coverage. Current
source/test HEAD is `6ff68e1bb4a6afe08441308acb9a485b5133c42e`; focused results
are `M4ExitIntegratedSessionTests` 2/2,
`EconomyDayCycleScenarioRigTests` 4/4, and
`M3aM4ReplayScenarioRigTests` 2/2. Full normal-clone gate: 2498 total /
2497 passed / 0 failed / 1 skipped; compiler 0/0; MCP 39 tools. The skip
`Provision_Activate_Persist_Deactivate_RoundTrip` requires `AAEMU_LIVE_RIG`
and `AAEMU_E2E_DB_PASSWORD`; forced rebuild report 1067 warnings / 0 errors.
Player/Bot loop closure is **Unknown/Open**: replay is ordered
scripted/fixture proxy and direct setup shortcuts are outside authentic
acceptance. No live M4 restart/vehicle proof was run because the shared E2E
reset is unsafe. Human/client QAT remains open; historical evidence is
preserved.

## M5 actor decision/action loop reconciliation (2026-08-28)

The bounded `BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle`
primitive (`263ecc66c474ca1c5f4b085e86ef3e47f49fd1`) is integrated into
`LevelingLoop`'s quest-accept choice. It preserves immutable observed context,
enforces hard legality before preference, bounds candidates, selects
deterministically by fixed priority/personality/tie-break, requires a terminal
postcondition, and dispatches via existing `GameplayActor`.
`BotDecisionProposalTests` pass **5/5**.

This is a decision primitive plus a scoped quest consumer, not universal bot
autonomy. The bounded 254→255 `LevelingLoop` slice is the only current
autonomous consumer; broad M5 policy remains open. Existing actor-contract
evidence, M5.3 movement caveat, and H/client boundary remain unchanged.

## M6/M7 loop reconciliation (2026-08-28)

**M6:** clean ordinary `Character`/bot dormant → proximity wake/materialize →
scheduled action resumes → identity/inventory/position/metadata survive restart
→ safe dematerialization. Focused M6 **105/105**: BotPresenceCoordinator 13/13
(patrol + transfer-finalize regression), BotRoamStepExecutor 6/6,
PlayerBotScheduler 26/26, DeathWatch 5/5, Metadata 15/15, Manifest 13/13,
Manager 19/19, Headless provisioning 8/8. Cancellation commit `950cfd279` adds
token/timeout-aware `BotDriveClient.CallAsync` while sync `Call` remains
compatible; Tier3 workers share cancellation/deadline propagation and stop
cooperatively, with no `Thread.Abort`. BotDriveClientCancellationTests 3/3 and
SoakOwnershipTests 2/2 pass. No six-hour soak or M6 full-exit result exists;
this is A/R harness/proxy evidence and no H/UAT claim is implied.

**M7:** ordinary `Character`/PlayerBot discovers/accepts a quest, navigates,
chooses legal hostiles, casts, receives kill credit, loots, sustains/retreats,
and completes/repeats; group variant adds party invite/follow/assist/death
recovery. Focused M7 **147/147** no-fail/no-skip: primary **36/36**
(Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4,
PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support
**111/111**. A/R rig/proxy only: hunt kill uses real DoOnMonsterHuntEvents
with fixture HP=0; Party spike is synthetic/fixture. No current live
authenticated-client run or H/UAT. Only bounded autonomous decision slice is
`LevelingLoop` 254→255; broad M7 decision, real damage/`Npc.DoDie`,
scheduler-driven route, party roles/regroup/restart/disconnect, mount/travel,
and H remain open.

---

Autonomous Loop = can a bot run this system's loop unattended end-to-end.

| System | Perceive | Decide | Act | Verify | Autonomous Loop |
|---|---|---|---|---|---|
| Movement | 🟡 positions via Observe; no terrain awareness | ✅ simple (straight-leg, standoff band, stuck detection) | ✅ MoveTo/MoveToUnit/DriveVehicle plus landed `NavigateTo` implementation (real CryEngine GeoData A* pathing, waypoint stepping, stuck detection, and straight-leg fallback) | ✅ tracked PB-001 five-test `GameplayActorNavigateTests` contract evidence: `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter '/*/*/GameplayActorNavigateTests/*'` → `Test run summary: Passed! total: 5 failed: 0 succeeded: 5 skipped: 0 duration: 1s 362ms`; `BaiNavigationRigTests` supplies GeoData/navmesh coverage. The preserved prototype waypoint test was invalid because it injected private state via reflection; do not claim waypoint coverage from it. PB-005 positive-only grounding clamp + intentional-floater whitelist landed, cave/deck/submerged and duplicate-row decisions remain | 🟡 broad interior/region traversal open |
| Combat | ✅ Observe (units, hp, targets) + causal traces (hp deltas) | ✅ rotation priority, sustain thresholds, no-progress skip | ✅ SetTarget/Cast (real skill pipeline) | ✅ kill credit + hp-delta traces; PB-007 narrow handshake live-proven at behavioral gate baseline `3871459d142fdd1767b9365a1de8d4cd3652ab0e` (current source/test HEAD `792774d7707b8b578b8d9975896e0a1ac719f361`): victim-matched non-immune `SCUnitDamaged`, immune exclusion, SkillFired, Retribution 2167, bloodstain 877, crime branch, and PEACE-BLOCK | ✅ party spike live-proven; broader PvP/honor and WAR-HONOR remain open |
| Quests | ✅ `Observe`/`DiscoverQuests` through the real AddQuest gate; titles are client-localized and zone-sweep coverage is open; channels include Item, Sphere, Level, and DiscoverSelfQuests | ✅ **Bounded decision primitive + scoped consumer:** `BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` selects the legal quest offering in `LevelingLoop` 254→255; immutable observed context, hard legality before preference, bounded candidates, deterministic fixed-priority/personality/tie-break, and terminal postcondition; `BotDecisionProposalTests` 5/5. Broad M5 policy remains open | ✅ AcceptQuest/TurnInQuest/AdvanceQuest/UseItem (real gates); canonical item-use quest 252: NPC 7653, item 7738, use skill 11596, act row 1600/detail 43; fail-closed quest 64 control | ✅ `LevelingLoopScenarioRigTests` 7/7; existing `leveling-loop-2026-08-25.md` report + JSONL trace | ✅ bounded 254→255 PlayerBot loop; this is not universal bot autonomy; **full M1 route remains Unknown/Open** |
| Loot | ✅ corpse/inventory via contract | ✅ loot-after-kill step | ✅ Loot action | ✅ item-granted criteria | ✅ within hunt loops |
| Vendors | ✅ money/inventory observable | ✅ trivial buy/sell rules | ✅ Buy/Sell actions (real shop paths); merchant trio fixes merged (`cb514c42e`, `beaf9b82e`, `3ba33b3af`, merge `e5db6d390`) | ✅ ledger conservation; live EconomyDayCycle conservation E2E passed across kill -9 restart | ✅ economy cycle live-proven |
| Mail | ✅ inbox, unread count, and attachment state observable | ✅ S3 send → restart → receive/take/delete decision path | ✅ server Send/read/take/delete packet paths with receive-path ownership guards; real `CSSendMailPacket` send and mailbox proximity | ✅ authenticated `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets` PASS 1/1 in 2m39s on isolated MySQL/Docker; kill-9/restart, `SlotType.Mail=5`, receiver retargeting, unread recount after registration, exact item-instance detail/grade/durability/rune/temper fidelity, copper, read transition, and delete persistence all asserted | ✅ restart-proven S3 flow; return opcode `0x0a2` STRONGLY_INFERRED pending real-client capture, COD/expiry follow-ups |
| Crafting | ✅ inventory/materials observable | ✅ recipe steps scripted | ✅ Craft action (real CharacterCraft) | ✅ products granted + materials consumed asserts | ✅ in economy cycle |
| Farming | ✅ crop growth observable (doodad phases) | 🟡 mature→harvest stage is scripted; no autonomous plot/target selection recorded | ✅ Plant/Harvest actions (real Doodad.Use) | ✅ doodad state + items | 🟡 ordered property replay; M3b restart persistence is separate |
| Housing | ✅ ownership/placeable observable | 🟡 build step scripted | ✅ BuildHouse action (real HousingManager.Build) | ✅ persisted rows across restarts | 🟡 M5.2 slice; decoration interior loops open |
| Trade packs | ✅ pack slot/bundle observable | 🟡 route steps scripted | ✅ PackPickup/PutDown/LoadPackOntoVehicle/DriveVehicle | ✅ payout conservation (mail + labor) | 🟡 M4 exit rig-proven; live replay = deferred gate #4 |
| Fishing | ✅ bite/labor observable | ✅ cast-retry loop | ✅ CastAt(position) (real plot 809) | ✅ labor/worm/loot deltas | ✅ FishingVerificationE2eTests live |
| Duels | ✅ challenge frames observable | ✅ accept/refuse rules | ✅ packet injection (CSChallengeDuel/StartDuel) | ✅ started frames + faction swap | 🟡 live E2E PASS; not an autonomous loop yet |
| Expeditions | ✅ membership observable | ✅ invite/accept rules | ✅ ExpeditionCreate/Invite/Accept/Leave actions | ✅ roster asserts | 🟡 rig-level lifecycle; not composed into bot gameplay |
| Parties | ✅ team registry + member state | ✅ follow/assist/fault rules | ✅ PartyInvite/Accept/FollowAssist/SpikeScenario | ✅ membership + kill credit | ✅ party spike live (3 bots vs elite) |
| Indun (dungeons) | ✅ instance isolation observable | 🟡 enter/clear steps scripted | ✅ first-class InteractWith(doodad) contract action (13f502673, derived use-skill + fail-closed effect post-check); exit path live-proven (PB-003 closed — data always existed); interior combat ✅ | ✅ room-clear events + isolation asserts | ✅ Hadir Farm E2E PASS ×2 + party-clear-then-exit 11/11 |
| Dominion (castle/siege) | ✅ schedule/state observable (phase cron Peace→Declare→Warmup→Siege→Payoff + SCSiegeAlertPacket) | 🟡 scripted (no combat/battle AI) | ✅ CSUpdateDominionTaxRate + declare via real packets | ✅ restart persistence E2E (kill -9 reload PASS) | 🟡 persistence proven, combat absent |
| Banking/storage | ✅ bank balances observable | ✅ deposit/withdraw rules | ✅ DepositMoney/Item, Withdraw actions | ✅ bank conservation across restart; Mail S3 copper transfer is separately asserted and does not change banking semantics | ✅ in economy cycle |
| Chat/social presence | ✅ proximity observable | ✅ greet/cooldown rules | ✅ real local-chat emission (BotChatterService) | ✅ sink capture tests | 🟡 greetings only; conversation depth open |
| Schedules & goal arbitration | ✅ game-time phase + pressure bands observable | ✅ BotGoalArbiter priority arbitration (deterministic, one active activity per wake) | ✅ Home/Work/Travel/Rest phase machine drives roam; arbiter selects module per wake (IBotActivityModule) | ✅ phase-transition + arbitration-transition asserts | 🟡 default OFF (`Bots.EnableSchedules` / `AAEMU_BOT_SCHEDULES_ENABLED`) — soak STAGE 1 live-proven; fidelity tiers additionally behind `AAEMU_BOT_TRUE_DORMANCY` / `AAEMU_BOT_PROXIMITY_FIDELITY` (see Scaling posture below) |

## MCP expansion (2026-08-27)
MCP sidecars and the management gateway remain client-neutral; availability is
not external-client actor lifecycle evidence. Historical coverage merge
`8a22dcb4` and its 33-test / 19-tool smoke record remain retained historical
evidence; earlier route-count checkpoints are superseded by the current
39-tool catalog.

Flash reports fifteen additional authenticated actor routes/tools:
`pack_pickup`, `put_down`, `load_pack_onto_vehicle`, `board_vehicle`,
`unboard_vehicle`, `drive_vehicle`, `buy`, `sell`, `craft`, `plant`, `harvest`,
`deposit_money`, `withdraw_money`, `deposit_item`, and `withdraw_item`, joining
`discover_quests`, `discover_self_quests`, `interact_with`, `talk`, and
`equip`.
The full normal-clone gate at source/test HEAD
`792774d7707b8b578b8d9975896e0a1ac719f361` is `./scripts/gate.sh`: **2496
total / 2495 passed / 0 failed / 1 skipped**, compiler **0/0**, MCP stdio
smoke **39 tools**. Focused PB-002 results: `LevelingLoopScenarioRigTests`
7/7, item-use 1/1, unsupported-objective 1/1, discovery 12/12, talk 5/5,
template registration 1/1; parser tests 2/2. The sole skip is
`Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
`AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. IntegrationTests Release
restore/build passed with 0 errors; restore emitted 2 NU1903 and build emitted
2 NU1903 in this exact verification. Do not substitute another full-gate count.

Flash reports a live `discover_self_quests` MCP benchmark passing with
`action_status`, `trace`, and an independent MySQL character-row cross-check;
no SHA-pinned benchmark artifact is checked in. The action cells above describe
contract/engine paths, not MCP exposure. Only later Party, Trade, Expedition,
Auction, and related actor expansion remains explicitly deferred and is not
claimed as MCP-exposed.


**PB-002 interaction candidate:** Item-use facts above remain unchanged. The
canonical interaction candidate failed for quest 270, doodad 687, interaction
skill 11229: the real path reaches `Doodad.Use`, but the spawned fixture exposes
no phase functions. No implementation landed; broad PB-002 remains open.
**Soak boundary:** No six-hour dormant-timer soak exists in current evidence,
so no soak result is claimed. A5/A5Tier3 use per-run named account/character
snapshots and ID-bound `finally` cleanup (`799b698ad`); sibling-preservation
tests pass 2/2, with no broad wildcard cleanup in those probes. Setup
cancellation is implemented by `950cfd279`: token/timeout-aware
`BotDriveClient.CallAsync` with compatible sync `Call`, plus cooperative
A5Tier3 worker cancellation/deadline propagation without `Thread.Abort`.
BotDriveClientCancellationTests pass 3/3. H/human-feel remains human-only and
UNKNOWN.

What exists today for running more bots without scaling cost linearly — all
of it default-OFF, so unset deployments behave byte-identically to before:

- **True dormancy (G2-A5 slice, e672b9579):** `DormantBotRegistry`
  (AAEmu.Game/Core/Managers/Bots/DormantBotRegistry.cs) keeps non-embodied
  bots as DB rows + metadata only — no Character materialized, no region
  presence, no per-second tick; materialize/dematerialize ride the real
  lifecycle and are proximity-budgeted. Wired via DI in Program.cs
  (`IDormantBotSource` → `MySqlDormantBotSource`); flag
  `Bots.EnableTrueDormancy` / `AAEMU_BOT_TRUE_DORMANCY`.
- **Proximity-fidelity tiers (G2-A3, d6cabcfd4):**
  `PopulationDirector.RefreshProximityFidelity` runs a sweep on TickManager:
  Full ≤75m / Reduced ≤200m / Dormant beyond (configurable radii in
  `PopulationDirectorOptions`), 2-sweep hysteresis, a per-sweep
  materialization cap (`TrueDormancyMaterializePerSweepMax`), and pressure
  demotion (`RefreshPressure`) once per sweep. Flag
  `Bots.EnableProximityFidelity` / `AAEMU_BOT_PROXIMITY_FIDELITY`; bootstrap
  via `PopulationDirectorProximityBootstrap`.
- **Goal arbitration (G3-B3, 0482ba3f0):** `BotGoalArbiter` +
  `IBotActivityModule` + the `BotGoalArbiterStepExecutor` decorator pick ONE
  active activity per bot per wake (schedule-phase P100 / presence-roam P50 /
  idle P0 first modules) — decision work stays priority-layered and cheap.
- **Budgets/clamps:** presence clamp `Bots.MaxPresenceBots` /
  `AAEMU_PRESENCE_MAX_BOTS` (default 10, `BotPresenceCoordinator.cs`);
  world-tick budget `WorldManager.ActiveRegionTickBudgetMs` = 100ms
  round-robin with drop/defer semantics; scheduler is a pure wake producer
  over a bounded worker pool (`PlayerBotScheduler`), every actor action
  carries a timeout budget with a queue no-wedge backstop
  (`BotActionCommandQueue`).
- **Measured basis:** g2-scaling-curve-report.json (VERIFIED at
  /root/aaemu-e2e/logs/g2-scaling-curve-report.json; E2E evidence dir, not
  in-repo): marginal embodied bot
  ≈16.5MB RSS; tick p95 0.42ms at 30 citizens vs the 100ms budget;
  baseload ~5.2GB world data), probes: `ScalingProbeTests` (N=10/20/30) and
  `SchedulerSoakStage1Tests` (10 citizens × 30min, ~90k steps, 0 failures).
  A5/A4 near-term gates MET 2026-08-25 (report §8/§9); wake-storm p99 at
  1,000 registered (A3 remainder) and FINAL Tier-3 still open.

## Highest-leverage gaps (one primitive unlocks many loops)
1. **QuestDiscovery perception** (PB-002) → ✅ LANDED 2026-08-25 (c1073d883) — autonomous leveling still needs the runnable-content sweep
2. **Routed navigation contract** (PB-001) → ✅ implementation + tracked five-test contract evidence; `BaiNavigationRigTests` covers GeoData/navmesh; broad interior/region traversal remains open
3. **Doodad-interact contract action** (generalize the fishing portal-injection into a first-class InteractWith(doodad)) → unlocks dungeon portals, convert/buy fish stands, world interactables
   ✅ CLOSED 2026-08-25 — first-class InteractWith(doodad) contract action landed (13f502673)
4. **Proximity-fidelity validation at scale** (A3/A5: machinery landed behind default-OFF gates — RefreshPressure driven, true dormancy slice in; A4 + A5 NEAR-TERM gates MET 2026-08-25, report §8/§9/§10; A3 remainder + FINAL Tier-3 still open) → unlocks 100+ bot villages cheaply
