# PlayerBot Capability Matrix (Perceive / Decide / Act / Verify)

Populated from implementation reality @ develop `31045d033` (= origin/develop
head, 2026-08-26; Mail S3 acceptance and recovery reconciliation).
Legend: ✅ through real engine paths · 🟡 partial/rig-only · ❌ missing.
Autonomous Loop = can a bot run this system's loop unattended end-to-end.

| System | Perceive | Decide | Act | Verify | Autonomous Loop |
|---|---|---|---|---|---|
| Movement | 🟡 positions via Observe; no terrain awareness | ✅ simple (straight-leg, standoff band, stuck detection) | ✅ MoveTo/MoveToUnit/DriveVehicle (real CSMoveUnit path); nav A* G-cost fix MEASURED (nav/slice-gcost @ 7e5d96e74; lazy per-block .bai spatial grid + heap openSet): detour factor 1.91×→1.22×, plan avg 6954→1187 ms, 81/81 corridor rig held | ✅ arrival events + stuck reasons; PB-005 positive-only grounding clamp + intentional-floater whitelist landed, but cave/deck/submerged and duplicate-row decisions remain | 🟡 open courtyards only — PB-001; PB-005 FIXED-PARTIAL |
| Combat | ✅ Observe (units, hp, targets) + causal traces (hp deltas) | ✅ rotation priority, sustain thresholds, no-progress skip | ✅ SetTarget/Cast (real skill pipeline) | ✅ kill credit + hp-delta traces; PB-007 corrected rig proves Retribution first + Refresh broadcasts, corrected live rerun pending | ✅ party spike live-proven |
| Quests | ✅ DiscoverQuests through the real AddQuest gate (PB-002, c1073d883); titles are client-localized and zone-sweep coverage still open; **channels v2 (2026-08-25, branch bots/quest-surface):** ~801 previously-hidden quests now perceivable — Item 342+25 (ConAcceptItem/ItemGain), Sphere 431 (geometry via GetQuestStartingSpheres), Level 3 (+ DiscoverSelfQuests); ConAcceptComponent channel DEFERRED = stub (true-return, no player-perceivable precondition) | ✅ FIRST AUTONOMOUS LEVELING SLICE (2026-08-25): `LevelingLoopScenario` — discover → lowest-level offering in band → accept → data-driven objective pursuit → turn-in → re-discover; no scripted chain ids in decision logic | ✅ AcceptQuest/TurnInQuest/AdvanceQuest (real gates); gather objectives via InteractWith on HighlightDoodadId sources (real OnItemGather credit); Talk contract action (Talk = 46) fires DoTalkMadeEvents once per active talk-objective quest through the real pipeline, fail-closed pre-flight (range/unresolvable npcObjId) + observable-delta post-check | ✅ rig-proven: Solzreed 254→255 completed unprompted, +1300 exp through real quest_supplies turn-in path (`leveling-loop-2026-08-25.md`); fail-closed proofs: band starvation → Starvation; unsupported objective type (5650 QuestActObjTalkNpcGroup) stops naming the missing primitive | 🟡 one chain segment rig-level; hunt legs not composed (primitives exist), talk/sphere/cinema/craft objective gaps named in `KnownPrimitiveGaps`; live E2E open |
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

## Scaling posture (2026-08-25)

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
2. **Waypoint-network movement** (PB-001) → unlocks dungeons, cross-region caravans, believable travel
3. **Doodad-interact contract action** (generalize the fishing portal-injection into a first-class InteractWith(doodad)) → unlocks dungeon portals, convert/buy fish stands, world interactables
   ✅ CLOSED 2026-08-25 — first-class InteractWith(doodad) contract action landed (13f502673)
4. **Proximity-fidelity validation at scale** (A3/A5: machinery landed behind default-OFF gates — RefreshPressure driven, true dormancy slice in; A4 + A5 NEAR-TERM gates MET 2026-08-25, report §8/§9/§10; A3 remainder + FINAL Tier-3 still open) → unlocks 100+ bot villages cheaply
