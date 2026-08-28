# PlayerBot Capability Matrix (Perceive / Decide / Act / Verify)

Populated from implementation reality at current source/test HEAD
`792774d7707b8b578b8d9975896e0a1ac719f361` (`origin/develop`, 2026-08-28).
Per-run soak ownership hardening `799b698ad` snapshots named account/character
rows, cleans only newly owned IDs in A5/A5Tier3 `finally` paths, and has
sibling-preservation tests 2/2. The full normal-clone gate at 792 is **2496
total / 2495 passed / 0 failed / 1 skipped**, compiler **0/0**, MCP stdio
**39 tools**. The sole skip is
`Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
`AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. IntegrationTests Release
restore/build passed with 0 errors; restore emitted 2 NU1903 and build emitted
2 NU1903 in this exact verification. Runtime evidence uses
`E2eStack.SourceRevision` with unknown fallback.
Autonomous Loop = can a bot run this system's loop unattended end-to-end.

| System | Perceive | Decide | Act | Verify | Autonomous Loop |
|---|---|---|---|---|---|
| Movement | 🟡 positions via Observe; no terrain awareness | ✅ simple (straight-leg, standoff band, stuck detection) | ✅ MoveTo/MoveToUnit/DriveVehicle plus landed `NavigateTo` implementation (real CryEngine GeoData A* pathing, waypoint stepping, stuck detection, and straight-leg fallback) | ✅ tracked PB-001 five-test `GameplayActorNavigateTests` contract evidence: `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter '/*/*/GameplayActorNavigateTests/*'` → `Test run summary: Passed! total: 5 failed: 0 succeeded: 5 skipped: 0 duration: 1s 362ms`; `BaiNavigationRigTests` supplies GeoData/navmesh coverage. The preserved prototype waypoint test was invalid because it injected private state via reflection; do not claim waypoint coverage from it. PB-005 positive-only grounding clamp + intentional-floater whitelist landed, cave/deck/submerged and duplicate-row decisions remain | 🟡 broad interior/region traversal open |
| Combat | ✅ Observe (units, hp, targets) + causal traces (hp deltas) | ✅ rotation priority, sustain thresholds, no-progress skip | ✅ SetTarget/Cast (real skill pipeline) | ✅ kill credit + hp-delta traces; PB-007 narrow handshake live-proven at behavioral gate baseline `3871459d142fdd1767b9365a1de8d4cd3652ab0e` (current source/test HEAD `792774d7707b8b578b8d9975896e0a1ac719f361`): victim-matched non-immune `SCUnitDamaged`, immune exclusion, SkillFired, Retribution 2167, bloodstain 877, crime branch, and PEACE-BLOCK | ✅ party spike live-proven; broader PvP/honor and WAR-HONOR remain open |
| Quests | ✅ DiscoverQuests through the real AddQuest gate (PB-002); titles are client-localized and zone-sweep coverage is open; channels include Item, Sphere, Level, and DiscoverSelfQuests | ✅ FIRST AUTONOMOUS LEVELING SLICE: discover → lowest-level offering in band → accept → data-driven objective pursuit → turn-in → re-discover; QuestActObjItemUse now covered through real GameplayActor.UseItem | ✅ AcceptQuest/TurnInQuest/AdvanceQuest/UseItem (real gates); canonical item-use quest 252: NPC 7653, item 7738, use skill 11596, act row 1600/detail 43; fail-closed quest 64 control | ✅ LevelingLoopScenarioRigTests 7/7; item-use 1/1; unsupported-objective 1/1; discovery 12/12; talk 5/5; template registration 1/1 | 🟡 scoped actor/rig coverage; broad autonomous progression and live/human breadth remain open |
| Loot | ✅ corpse/inventory via contract | ✅ loot-after-kill step | ✅ Loot action | ✅ item-granted criteria | ✅ within hunt loops |
| Vendors | ✅ money/inventory observable | ✅ trivial buy/sell rules | ✅ Buy/Sell actions (real shop paths); merchant trio fixes merged (`cb514c42e`, `beaf9b82e`, `3ba33b3af`, merge `e5db6d390`) | ✅ ledger conservation; live EconomyDayCycle conservation E2E passed across kill -9 restart | ✅ economy cycle live-proven |
| Mail | ✅ inbox, unread count, and attachment state observable | ✅ S3 send → restart → receive/take/delete decision path | ✅ server Send/read/take/delete packet paths with receive-path ownership guards; real `CSSendMailPacket` send and mailbox proximity | ✅ authenticated `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets` PASS 1/1 in 2m39s on isolated MySQL/Docker; kill-9/restart, `SlotType.Mail=5`, receiver retargeting, unread recount after registration, exact item-instance detail/grade/durability/rune/temper fidelity, copper, read transition, and delete persistence all asserted | ✅ restart-proven S3 flow; return opcode `0x0a2` STRONGLY_INFERRED pending real-client capture, COD/expiry follow-ups |
| Crafting | ✅ inventory/materials observable | ✅ recipe steps scripted | ✅ Craft action (real CharacterCraft) | ✅ products granted + materials consumed asserts | ✅ in economy cycle |
| Farming | ✅ crop growth observable (doodad phases) | ✅ mature→harvest rule | ✅ Plant/Harvest actions (real Doodad.Use) | ✅ doodad state + items | ✅ in economy cycle (+ restart persistence) |
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
so no soak result is claimed. A5/A5Tier3 now use per-run named
account/character snapshots and ID-bound `finally` cleanup (`799b698ad`);
sibling-preservation tests pass 2/2, with no broad wildcard cleanup in those
probes. `SeedBox` has synchronous bridge calls/native `Thread.Join` without
hard cancellation. H/human-feel remains human-only and UNKNOWN.

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
