# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-30 · discovery-channel re-census at `9b8ba6317` (PB-002 evidence refresh; prior:
2026-08-28 · local source/test checkpoint reconciliation; prior:
(PB-007 live closure + PB-002 item-use evidence; prior:
2026-08-26
(Mail S3 acceptance and recovery reconciliation; G2-A5 + A4 near-term gates
MET with live evidence; PB-002 quest-discovery primitive and item-use slice
landed; PB-003 closed premise-refuted; PB-004 found-by-measurement + fixed same
day; first-class InteractWith doodad contract action; SERVER-PERF wave — see
scorecard-explorations/generated/g2-a3-storm-report.md)
Branch of record: develop; current local source/test HEAD is `9b8ba63175b459c2073cb7c742637f07bbb3b9e1`
(re-census checkpoint 2026-08-30; prior census checkpoint `da0fdc61a72a15111fddc8ac627a164a5f050558`). M5's
`BotDecisionProposal`/`BotDecisionSelector`/`BotDecisionCycle` bounded decision
primitive remains integrated in `LevelingLoop`'s accept choice at
`263ecc66c474ca1c5f4b085e86ef3e47f49fd1`; focused contract 5/5, scoped quest
consumer only, and broad M5 policy/universal autonomy remain open. M6 includes
`950cfd279` cancellation, `c97909f4f` population isolation, and opt-in
six-hour leg `155c82c66` integrated here.

**Hierarchy note:** Current work is under **Post-M7 readiness and closure**, an
umbrella scope rather than a new numbered milestone. PB-001/PB-002/PB-005/PB-007
are capability/blocker tracks; A3/A4/A5 are population/scaling acceptance
gates; slices sit inside those tracks or gates; H is deferred human/client
acceptance. M0–M7 are the landed foundation/product milestones. The roadmap
formally defines a future **M8 — Living Village**; readiness labels are not
renumbered as M8. See the authoritative [scope map](PROJECT-CONTROL.md#scope-map).
## 2026-08-29 — Post-M7 readiness and closure: PB-002 aggro slice

- **PB-002 result: PARTIAL capability closure, broad autonomy OPEN.** The
  `LevelingLoopScenario` now has an `AggroLeg` for canonical
  `QuestActObjAggro` forms whose live quest instance has a non-zero NPC
  acceptor template. It selects only normal perceived, attackable NPCs of
  that template, requires the owner to be present in the victim's real aggro
  ranking at a configured Rank1/Rank2/Rank3 threshold, then reuses the shared
  SetTarget → Cast → kill → Loot path. Completion is read back from live quest
  state after the real OnKill event boundary; no objective counter is written
  by the scenario.
- Canonical aggro census remains **37 rows, 30 attached progress acts, and
  30 distinct quests**; all 30 use `QuestActConAcceptComponent` and none use
  NPC/NPC-kill acceptance. This slice does not change acceptance-channel
  counts. The supported component/NPC-acceptor boundary is exercised by
  canonical quest **2432** (aggro act id **4**, NPC template **9**, Level 6).
  Component forms without an NPC acceptor or without a kill path that emits
  `OnKillArgs.Target = dead NPC` remain open and are explicitly fail-closed.
- Evidence layer is **A / rig (proxy/bot-functional)**, not H, restart, or soak:
  `LevelingLoopScenarioRigTests` **21/21** (including positive aggro and
  no-owner-attribution control), `QuestActObjAggroTests` **2/2**. No live
  client or human gameplay evidence is claimed; the separate A5 canary result
  is diagnostic only and is recorded below.
- Remaining broad PB-002 gaps include unsupported objective families
  (item-group resolution, cross-quest, level/ability/mount training,
  zone-scoped kills, and other named `KnownPrimitiveGaps`) plus the canonical
  interaction fixture's missing phase functions. **Next action:** keep the
  aggro boundary in the focused gate and extend only with another canonical
  target whose live ranking and kill event are observable; do not claim
  universal autonomous leveling or PB-002 closure.

## 2026-08-30 — Post-M7 readiness: discovery-channel re-census at 9b8ba6317 (PB-002 evidence refresh)

- **What changed:** three code commits (`3827b5170` kill-accept perception in `DiscoverQuests`,
  `7d0b80041` `AutoStartedQuestIds` pursuit, `a1653d67d` `OnKillArgs.Target = victim` in `DoDie`) plus
  `f5331ced7` (AggroLeg) landed after the 2026-08-29 census. Re-ran the same read-only SQL against
  canonical `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af`, unchanged) — **data counts are
  identical**; only the code deltas move the reachable frontier.
- **Corrected census:** perceivable 8-channel union still **4,181**; the loop now additionally pursues the
  **380 kill-only** quests (through the kill-accept channel) and **45 of the 115 component-only** quests
  (**30** auto-start + aggro — previously misclassified as "engine-broken", now unblocked by `a1653d67d`;
  **15** auto-start + MonsterHunt/plain) — **4,226 total pursuable at HEAD**. Remainder is **70** component-only
  quests with no engage-NPC tie and no aggro act (stub `RunAct` true-return, no perception primitive
  exists) — genuinely unreachable, correctly fail-closed. See
  `scorecard-explorations/generated/discovery-channel-census-2026-08-29.md` §"2026-08-30 re-census".
- **PB-002 impact:** the 2026-08-29 line "component forms without an NPC acceptor … remain open and are
  explicitly fail-closed" is **superseded for the 45 auto-startable forms** (they DO get an Npc acceptor
  triple via `AddQuestFromNpc` on first aggro); the boundary still holds for the **70** with no engage tie
  and for aggro forms whose live acceptor template resolves to 0. Full test coverage at this HEAD:
  `QuestActObjAggroTests` **2/2**, `LevelingLoopScenarioRigTests` **21/21** (includes the 6109 auto-start
  end-to-end). Evidence class is unchanged — deterministic rig / A, no live/human claim.


The six-hour stage is opt-in and requires `A5_TIER3_SIX_HOUR=1`,
`A5_TIER3_SIX_HOUR_MINUTES>=360`, and sample seconds 1 through 300. The
six-hour dormant soak DID execute at
`/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json` for
360.00003 minutes / 361 samples: DormantSpecs 1000 throughout, embodied 0,
materializations/dematerializations 0, queues/failures/save-skips 0, DB writes
delta 0, tick max 19.1ms, and save p95/max 85.7/100.8ms. Its RSS assertion
failed because the baseline was captured before deferred world startup settled
(baseline 1207.9MB, peak 5749.4MB, final 3744.1MB, budget +512MB); this is not
yet classified as a leak.

Evidence classification is **testing/canary diagnostic failure**, not live
human gameplay evidence and not an A5 pass. Correction `ccd4ea857` adds
explicit `A5_WARMUP_READY`, a post-quiescence baseline, separate startup-peak
accounting, and fail-fast post-baseline RSS breach handling with FULL/PARTIAL
report semantics. A corrected rerun (preferably a 12-hour testing soak) is
pending. The old bounded Tier3 rehearsal below remains historical evidence.

The prior full gate at `0ce518ac03a18de00fff1516aa9e794e8566bee6` remains
2504 total / 2503 passed / 0 failed / 1 skipped, compiler 0/0, MCP 39; no new
full gate was run for `da0fdc61`. No M6 full-exit or H/UAT claim is made;
historical reports and failed soak provenance remain preserved.

Josh human-QAT wave 4: Docs/JOSH-QAT-WAVE4.md (2026-08-25) — 8-pack for mail
return (0x0a2 hypothesis), mail ownership guards, labor regen, war-gated
honor, NPC grounding tour, boats, slavetest observation, Mirage walk.

## 2026-08-26 recovery reconciliation

- **Develop contents confirmed:** grounding fix `38c4997d3`, recovered
  Retribution wire-observability test branch `a4f7820ba`, merchant merge
  `e5db6d390` (the three merchant fixes are in that merge), and Mail S3
  acceptance `31045d033`; earlier committed features remain in the current
  ancestry.
- **PB-005 grounding:** **FIXED-PARTIAL** — positive-only clamp and intentional
  aerial/water/structure whitelist landed. The terrain-only replay corrects all
  593 non-whitelisted severe-positive rows and leaves 702 whitelisted rows
  unchanged; cave/deck/submerged behavior and duplicate-row decisions remain.
- **PB-007:** **FIXED / CLOSED for the narrow flagged-aggression handshake
  requirement** at behavioral gate evidence baseline
  `3871459d142fdd1767b9365a1de8d4cd3652ab0e` (current source/test HEAD is
  `792774d7707b8b578b8d9975896e0a1ac719f361`). Current report:
  `scorecard-explorations/generated/pvp-handshake-e2e-2026-08-27.md`.
  The isolated real-login/Game E2E observed a victim-matched, non-immune
  `SCUnitDamaged` frame, with immune frames excluded, `SkillFired=True`,
  Retribution 2167, bloodstain doodad 877, and the crime branch observable;
  PEACE-BLOCK also passed. The prior `/root/aaemu-e2e` report and failed/immune
  context remain historical. WAR-HONOR remains separately deferred; this does
  not close all PvP/honor scope.

- **Mail S3:** **PASS / LANDED** in `31045d033` — authenticated
  `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
  passed 1/1 in 2m39s on isolated MySQL/Docker. Restart, instance-faithful
  equipment attachment, ownership, unread-count, take, and delete assertions
  passed; no live-client confirmation of inferred return opcode is implied.
- **PB-001 routed navigation:** **IMPLEMENTATION + TRACKED FIVE-TEST
  CONTRACT EVIDENCE** — `IGameplayActor.NavigateTo` supports CryEngine
  GeoData A* routing, dynamic waypoint stepping, stuck detection, and
  straight-leg fallback. Source/test commits: `0c57ef0c9` (tracked
  `GameplayActorNavigateTests`) and `57b6e2960` (linked-worktree helper
  compatibility). Focused result:
  `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter '/*/*/GameplayActorNavigateTests/*'`
  → `Test run summary: Passed! total: 5 failed: 0 succeeded: 5 skipped: 0 duration: 1s 362ms`.
  `BaiNavigationRigTests` supplies the GeoData/navmesh coverage. The preserved
  prototype waypoint test was invalid because it injected private state via
  reflection; do not claim waypoint coverage from it. `BotActionCommandQueueTests`
  covers the first-class `Navigate` queue action.
- **PB-002 autonomous leveling loop:** **SCOPED ACTOR/RIG SLICES LANDED;
  BROAD CLAIM OPEN** — `LevelingLoopScenario` and related actor/rig slices
  cover selected perception-driven quest steps. Current item-use coverage drives
  `QuestActObjItemUse` through real `GameplayActor.UseItem` for canonical quest
  252 (NPC 7653, item 7738, use skill 11596, act row 1600/detail 43), with
  fail-closed canonical quest 64 control. **Historical/pre-fix interaction
  context:** the earlier quest-270 candidate (doodad 687, interaction skill
  11229) reached `Doodad.Use`, but its spawned fixture exposed no phase
  functions; the old failure and “No implementation landed” conclusion are
  preserved as historical evidence only. **Current result:** quest **269→270**
  interaction is landed and verified at the deterministic rig layer through
  `QuestActObjInteraction` / `GameplayActor.InteractWith`; the current
  `LevelingLoopScenarioRigTests` total is **21/21** after the aggro work.
  Broad PB-002 remains open with no live or human breadth claim.
**Behavioral gate evidence (normal-clone full gate at source/test HEAD
`792774d7707b8b578b8d9975896e0a1ac719f361`):** Release build PASS, compiler
check 0/0, unit **2496 total / 2495 passed / 0 failed / 1 skipped**, and MCP
stdio **39 tools**. The sole skip is
`Provision_Activate_Persist_Deactivate_RoundTrip`, requiring
`AAEMU_LIVE_RIG=1` and `AAEMU_E2E_DB_PASSWORD`. IntegrationTests Release
restore/build passed with 0 errors; restore emitted 2 NU1903 and build emitted
2 NU1903 in this exact verification. Focused PB-002 results:
`LevelingLoopScenarioRigTests` 7/7, item-use 1/1, unsupported-objective 1/1,
discovery 12/12, talk 5/5, and template registration 1/1. Parser tests
passed 2/2. These are the current 792 gate counts; no different full-gate
result is claimed.
## Current provenance and soak boundaries (2026-08-28)
- Runtime evidence records `E2eStack.SourceRevision` from `git rev-parse HEAD`
  and reports `unknown` when the checkout/archive cannot resolve a revision.
  The current local source/test pointer is `da0fdc61a72a15111fddc8ac627a164a5f050558`.
- The corrected bounded Tier3 rehearsal at `4721cbd306cbf346bfe38b7373d5adf479b6231f`
  passed 1/1 in 15m20.984s: 1000 seeded, 50 embodied, 950 dormant,
  materialize p95 259.2ms, RSS +2.56%, 50 dematerialized, owned cleanup zero.
  This is readiness evidence only.
- `155c82c66` adds the opt-in six-hour natural dormant-timer test. The old
  default-skipped/no-result wording is historical pre-execution context only.
  The executed canary report is
  `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json`:
  360.00003 minutes / 361 samples, DormantSpecs 1000, embodied 0,
  materializations/dematerializations 0, queues/failures/save-skips 0, DB
  writes delta 0, tick max 19.1ms, save p95/max 85.7/100.8ms. RSS assertion
  failed on the pre-quiescence baseline (1207.9MB → peak 5749.4MB → final
  3744.1MB; budget +512MB), not yet classified as a leak.
- This is a testing/canary diagnostic failure, not live human gameplay and not
  an A5 pass. Correction `ccd4ea857` adds `A5_WARMUP_READY`, post-quiescence
  baseline, separate startup peak, fail-fast post-baseline RSS breach, and
  FULL/PARTIAL report semantics. A corrected rerun, preferably 12 hours,
  remains pending.
- Cancellation focused tests pass 3/3; ownership focused tests pass 2/2.
  The prior full gate at 0ce remains historical: 2504 total / 2503 passed /
  0 failed / 1 skipped, compiler 0/0, MCP 39. No new full gate was run at da0.
- Human/H boundaries remain intact: bot, rig, MCP, and live-stack evidence is
  functional/proxy evidence; H/human-feel acceptance remains Josh-owned.

## 2026-08-27 MCP expansion
- **Client-neutral integration:** MCP sidecars and the management gateway remain
  client-neutral; they do not constitute external-client actor evidence.
- **Historical coverage retained:** merge `8a22dcb4` recorded the earlier
  33-test contract coverage and 19-tool stdio smoke; earlier route-count
  checkpoints are historical and superseded by the current 39-tool catalog.
- **Flash route expansion:** fifteen authenticated actor routes/tools landed:
  Deposit/Withdraw money and items, Plant/Harvest, Craft, Buy/Sell,
  PackPickup/PutDown/LoadPackOntoVehicle, and Board/Unboard/DriveVehicle.
- **MCP catalog:** now **39 tools**. Flash reports focused validation:
  `BotActionControllerRouteTests` 2/2, `BotControlActionMcpTests` 33/33,
  `BotActionCommandQueueTests` 18/18; protocol smoke 39 tools. The clean gate
  is SHA-pinned at the audited source baseline above from a normal clone.
- **Live benchmark:** Flash reports `discover_self_quests` MCP benchmark
  passed with `action_status` and `trace`, plus an independent MySQL
  character-row cross-check. This remains unpinned benchmark evidence; no
  SHA-pinned artifact is checked in. No safe doodad interaction was attempted.
- **Historical smoke retained:** the earlier
  `mcp-live-smoke-2026-08-27.md` report-only run at `7e109d550` is retained as
  historical evidence of an asset-missing Game exit before WebApi; it is not
  the current MCP benchmark verdict.
- **Deferred action-surface boundary:** only later Party, Trade, Expedition,
  Auction, and related actor expansion remains deferred and is not claimed as
  MCP-exposed.

## Deferred validation gates (bot-backtrack program, 2026-08-12)

Prior human-test waivers are **authorized sequencing, not misconduct**.
Earned engineering evidence stands; these are explicitly deferred
validation. Bots prove function; Josh proves feel. H = actual player only —
scripted-actor/bot evidence is proxy/bot-functional, never H=2. Full table:
ROADMAP.md "Deferred validation gates".

1. **M1 human route** — Josh walks Solzreed (Open Decision #1).
2. **Original M2 human baseline** — two players, no GM repair; Josh-owned.
3. **M3a contract replay** — Phase 2 via M5.1 actions (Plant/Harvest/Craft/PackPickup/PutDown).
4. **M4 economic/navigation replay** — Phase 2 contract replay; normal movement/vehicle controls (direct Transform/ZoneId assignment FAILS the gate).
5. **M6 B4 restart scenario** — Phase 3; bot identity/inventory/position/
   schedule survive restart. **Engineering COMPLETE 2026-08-20** (store
   built + 2-checkpoint replay re-run with direct metadata assertions,
   PASS) — what remains is the full-M6-exit LABEL decision, not code.

## Milestone state

**2026-08-25 wave (e672b9579 → 6ba363a28):**
PB-002 quest-discovery perception primitive LANDED (c1073d883) —
IGameplayActor.DiscoverQuests through the real CharacterQuests.AddQuest
pre-flight chain; offer linkage = QuestActConAcceptNpc/Doodad acts;
canonical smoke vs compact.sqlite3 PASS. First-class InteractWith doodad
contract action (13f502673) — derived use-skill, fail-closed observable-
effect post-check. G2/A5 harness instrumentation merged (4e3004f33) —
dormancy latency ring, bridge seedDormant command, worktree-safe E2eStack,
ScalingProbeTests env tiers, new A5AcceptanceProbeTests +
A4AcceptanceProbeTests. Roam allocation-budget guard widened 512→768B/step
(d989f6639) — zero-margin boundary flake, JIT-variance evidence documented.
**G2-A5 NEAR-TERM GATE MET:** ~100 dormant/~10 embodied constructed via real
paths (live TCP human trigger); RSS +2.09% vs baseline (<15% target);
materialize p95 251.7ms pre-fix / 260.1ms post-PB-004-fix (<3s target).
Evidence: scorecard-explorations/generated/g2-a5-acceptance-report.md §8+§10.
**G2-A4 GATE MET:** autosave p95 393.1ms @ 250 active characters (target
<2000ms; actual headroom 80.3%), 0 save skips — report §9. **PB-003 CLOSED,
premise REFUTED:** exit portals always shipped — static spawns are JSON world
data (Data/Worlds/instance_hadir_farm/doodad_spawns.json 4289/4927),
compact.sqlite3 never held them; party-clear-then-exit E2E PASS 11/11
post-rebase (IndunExitE2eTests, .worktrees/pb003-exit branch pb003/exit-e2e
@ 9e824d34f); ledger flipped FIXED with layer correction DATA→E2E-coverage.
**PB-004 FOUND AND FIXED same day (6ba363a28):** materialized dormant bots
never stepped — no Wake() on proximity materialization + dormancy-only boot
never started the scheduler loop; post-fix steps/min 0→3001 with 10 embodied,
dematerialize-on-leave clean; seedDormant records roam schedule. New engine
finding logged, NOT fixed (report §10.2): pre-existing ManagerOrchestrator
parallel-init boot race (IdManager.GetNextId NRE ← ItemContainer ctor ←
ItemManager.LoadUserItems), one observed crash, rerun green. Navigation
dossier landed: scorecard-explorations/mechanics/navigation-domain.md
(487 lines) — server already loads CryEngine .bai nav graphs + has A* with
three verified defects (wrong zone loader selection WorldTemplate.cs:235-238,
linear nearest-node scans AiGeodataManager.cs:232-314, PathNode G-cost bug
PathNode.cs:226); strategy = compose/harden the existing .bai spine.


**2026-08-25 wave 3 (lane integration, develop @ 9a8fbc8e8):**
**G2-A3 ACCEPTANCE MET (full run):** 1,000-registered-dormant wake-storm
fidelity-transition p99 = 0.00008 ms unstaggered / 0.000061 ms staggered vs
the <100 ms bar — both arms 1000/1000 materialized through the REAL live-TCP-
human → proximity-sweep → materialize path, clean dematerialize-on-leave.
Incremental per-zone/activity counters REJECTED on profiling evidence (sweep
scan pass p50 ≈ 0.066 ms; hot cost is the budget-paced materializer by
design); event-driven human-proximity wake DEFERRED with rationale — full
numbers: scorecard-explorations/generated/g2-a3-storm-report.md §4.2/§5.
IdManager boot race (§10.2 above) FIXED same day: lazy-init guard,
95bf502ad. **Nav G-cost fix measured:** detour factor 1.91×→1.22×, plan avg
6954→1187 ms, rig 81/81 held (nav/slice-gcost @ 7e5d96e74; lazy per-block
.bai spatial grid + heap openSet included). **First autonomous leveling
loop:** LevelingLoopScenario completes the delivery+ItemGather chain
unprompted 254→255, XP +620/+680 through real quest gates
(bots/leveling-loop @ 2a124be70); remaining primitives captured in the
capability-matrix / blockers taxonomy.

**2026-08-25 wave 4 (research + promotion docs lane — six domain dossiers +
master census):** completeness census landed
(scorecard-explorations/generated/mechanic-inventory-2026-08-25.md): 65
canonical player-facing 1.2 systems enumerated against opcode families,
compact.sqlite3 domains, and the 65-manager code surface — 32 tracked, 33
newly proposed; SCORECARD.md now carries all 33 stable-ID rows (ledger
coverage 64/65 ≈ 98%) plus dossier-grounded grade promotions (MERCHANT-01
W=2/A=2 stale-row fix; its three engine defects were then open and are fixed
in the recovery below; CRIME/TRIAL/PVP C=2).
Six domain dossiers landed under mechanics/: **justice** (chain one of the
most completely reconstructed systems — gap is E2E proof, not code; prison
labor/escape genuinely absent), **economy** (vendor loop rig-tested AND
live-proven via m8 reconcile across kill -9; labor regen tick DEAD —
TimedRewardsManager.Initialize has no caller; CSReturnMailPacket real 1.2
value likely unrecoverable), **pvp** (CanAttack chokepoint ordering + honor
formulas reconstructed; duel-bounds mystery solved: flag doodad 5014 +
hardcoded 75 m poll, no geodata ring exists), **dominion** (richest dead-end
domain — complete wire formats + data, zero runtime beyond a hardcoded
DeclareDominion broadcast), **ships** (Jitter2 sailing physics REAL per-kind
tuned; shipyard frames memory-only on restart), **mail** (instance-faithful
attachments; SECURITY finding: 4 of 5 receive paths lack ReceiverId checks).
Indun addendum refuted PB-003's data premise before its E2E closed it.
- Quest-surface branch `bots/quest-surface` (@ 5fe08432e): discovery
  CHANNELS v2 — ~801 previously-hidden quests now perceivable via Item
  (342+25) / Sphere (431) / Level (3) channels + DiscoverSelfQuests
  (ConAcceptComponent deferred = stub) — plus Talk contract action (Talk =
  46) through the real DoTalkMadeEvents pipeline, fail-closed pre/post-checks;
  full unit suite green (2400/2400 + pre-existing skip).
- Kill-leg branch `bots/kill-leg` (@ f369f4c84): hunt objectives
  (MonsterHunt/MonsterGroupHunt) added to LevelingLoopScenario with
  cast-burst rotation, loot-per-corpse, no-progress exclusion; quests 329 +
  1652 complete unprompted (+620/+680 XP); suite 2393/2394 green.
- **Historical Tier-3 shape measurement (g2-a5-acceptance-report.md §11):**
  1,000 dormant seeded through real provisioning / exactly 50 embodied; RSS
  +0.13% vs the 50-active baseline; wake-to-visible p95 280.2ms; steps/min
  parity 15003 vs 14995. The later six-hour canary did execute; see the
  current A5 diagnostic above. Concurrent `seedDormant` corrupts server state
  after ~100 bots (documented; seeding stays sequential). Per-run ownership
  hardening `799b698ad` separately scopes A5/A5Tier3 cleanup to newly observed
  account/character IDs; sibling-preservation tests pass 2/2.

**2026-08-26 wave 7 (validation infrastructure + dominion/pvp/crime
verticals):** **G3-B5 DONE** — behavioral scenario library promoted
**PVP-01 MAJOR FINDING + PARTIAL FIX:** flagged-aggression composed flow
initially FAILED live — handshake + target acquisition passed but zero damage
applied because buff 2423 "LoggedOn" grants ~20 s full damage-immunity at
login, and the immune early-return skipped both HP loss and the crime branch.
The engine fix extracts `RegisterCrimeForAttempt` and invokes it on the immune
path; apply-loop exceptions are logged and rethrown; the E2E waits out the
protection window. Post-fix real damage, bloodstain, and crime chain execute;
ZONE-01 Peace enforcement + homeland mother-shield remain LIVE-verified.
**PB-007 narrow closure (2026-08-27, behavioral gate evidence baseline
`3871459d142fdd1767b9365a1de8d4cd3652ab0e`; current source/test HEAD
`792774d7707b8b578b8d9975896e0a1ac719f361`):** the final isolated live E2E
passed 1/1 in 2m09.910s. `AGGRESS-ALLOWED` observed
victim-matched non-immune `SCUnitDamaged=True`, immune frames excluded=False,
`SkillFired=True`, Retribution 2167=True, and bloodstain doodad 877 objId
44294; the crime branch was observed. `PEACE-BLOCK` passed with no
victim-matched non-immune damage. The deterministic compressed-parser tests
passed 2/2. `WAR-HONOR` remains intentionally deferred (>251 hostile kills
plus the conflict timer); no all-PvP or honor-scope closure is claimed.
**CRIME-01 vertical LIVE-PROVEN:** JusticeCrimeE2eTests
8 stages incl. restart persistence + wanted seam PASS; engine fix MarkDirty()
on CrimePoint/InfamyPoint setters (silent-persistence-vanish bug).
E2E PASS — branch d42e708f5→66f124533. Combat/siege-battle explicitly NOT
implemented (later slices); declare-trigger UI path still UNKNOWN

**MAIL S3 ACCEPTANCE (2026-08-26, `31045d033`):** the authenticated real-packet
E2E `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
passed 1/1 in 2m39s on isolated MySQL/Docker. It exercised real
`CSSendMailPacket`, mailbox proximity, equipment item instance + copper,
kill-9/restart, persisted `SlotType.Mail=5`, receiver ownership retargeting,
unread count 1 after registration, read transition to 0,
`CSListMail`/`CSReadMail`, `CSTakeAttachmentSequentially`, exact item
detail/grade/durability/rune/temper fidelity, copper transfer, and
`CSDeleteMail` persistence deletion. Root cause: `Character.Load` recounted
before world registration; recount now occurs after `TryAddCharacter` and
before human client initialization. Return opcode `0x0a2` remains
STRONGLY_INFERRED pending real-client capture; COD and expiry/bounce remain
open follow-ups.
**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v4 (permanent one-way upstream gate), community guidelines,
kanban template set (Nei), gate.sh verified, scorecard + 3 exploration
reports, graphify graph (17.6k nodes), shared skill aaemu-fork-workflow
enabled on all 4 profiles, LIVING-WORLD.md canon, ROADMAP.md locked-shape
2026-08-03 (date is canonical).

**M1 — Quest and progression spine: ✅ CLOSED**
Items 1-8 delivered; automated exit test GREEN — census headline
**153/153 runnable / 0 FAIL / 33 SKIP over 186 quests**; full gate
1148/1148. PROD DEPLOYED @ 94f498fc (2026-08-04, M1 engine-health
release — BUG-007/008/009/010/011/012 live). Deploy incident (39GB
container json.log) resolved; rotation fix shipped (t_264e1984 ✅).
M1 closed on automated evidence (M1-M3 audit t_5b1f5494); human playtest
verdict open (Open Decision #1, pending Josh — C5) — **explicit deferred
gate: M1 human route (bot-backtrack program)**.
**M1 loop-closure reconciliation (source/test baseline
`7a572c08a32162988dedbf400bd9f8b608fb1974`):** the player loop is a clean
Nuian Solzreed progression from quest discovery through ordinary objectives
and turn-ins to the first-mount unlock, with restart persistence checked
separately. `LevelingLoopScenario` provides a bounded autonomous PlayerBot
loop for quests 254→255: `Observe → Discover → legal lowest-level choice →
objective pursuit → turn-in → re-discover`. The focused 254→255 test is 1/1;
`LevelingLoopScenarioRigTests` is 7/7. Evidence is the existing
`scorecard-explorations/generated/leveling-loop-2026-08-25.md` report and its
JSONL trace; this reconciliation does not regenerate either report.
`M1M2ReplayScenario` remains a 16-quest ordered scripted proxy with 55 fixture
actor records, fixture `Level=6` setup, and no real-mount criterion. It does
not establish autonomous decision closure for the full M1 route. Josh's
fresh-character Solzreed route, first-mount summon, restart, Bloody Hand, and
bounty-board checks remain separate H/UAT gates.
**M2 — Golden-path baseline: ✅ DONE on historical G1 evidence; current loop
reconciliation remains proxy/open (2026-08-28)**
M2 redefined (2026-08-10 audit, in ROADMAP) into the M2a–M2d census sweep.
G1 GATE @ 7f5c179f7: 4,579 live = 4,573 PASS + 6 doc-SKIP, 0 unexplained;
full gate 1495/0/1 (t_971d275b / gate card t_4221f85c). Baseline legs
Rei-gated: automated (t_c6eb12ec / t_1998cfd8 PASS), restart (t_cca63225 /
t_c069bacd PASS + live probe t_92a41fe6 2/2), clean-host (t_52755daa /
t_819930ef PASS-WITH-FIXES). Human leg DEFERRED to M4 close (t_46bf9b84) —
**explicit deferred gate: original M2 human baseline, Josh-owned
(bot-backtrack program; bots may stand in for the AUTOMATED baseline only,
never H=2)**.

**M2 loop-closure reconciliation (A/R, source/test HEAD
`ba530bcebec12af2bc7dc0db7451a535665bbed3`, 2026-08-28):** M2's player loop
is defined as **clean reset → ordinary golden-path baseline
quest/progression → required first-mount/baseline state →
restart/clean-state persistence verification**. The focused deterministic
normal-clone command was
`dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build --treenode-filter '/*/*/<Class>/*'`,
run with `<Class>` replaced by `HeadlessSessionProvisioningTests`,
`M1M2ReplayScenarioRigTests`, `M1M2ReplayCastWindowRigTests`,
`PlayerbotPilotTests`, `QuestScenarioTests`, `QuestScenarioTierTests`, and
`QuestDataCensusTests`. The aggregate was **32/32 passed, 0 failed, 0
skipped**: 8/8, 3/3, 1/1, 6/6, 12/12, 1/1, and 1/1 respectively.

This is A/R deterministic fixture/proxy evidence only: `PlayerbotPilotTests`
completed 30/30 cycles and its restart check passed 2/2, but both are ordered
manifest/contract replay. `M1M2ReplayScenario` is a fixed 16-quest ordering;
its declared mount criterion reports **no real mount** in the headless fixture.
`QuestScenarioTierTests` itself passed 1/1 because every manifest ran and the
report was written; its observed per-quest census is **4463 PASS / 110 FAIL /
14 SKIP over 4587** (4463/4573 runnable), including T1 failure quest 6280.
The remaining census results are historical evidence findings, not an M2 full
closure claim. No live server, client, or human/H evidence is implied.

The loop matrix therefore remains **Player closes loop = Unknown (H open)** and
**Bot closes loop autonomously = Unknown/Open** for M2: the current evidence is
ordered-manifest/contract replay and contains no `Observe → Discover →
legal-choice` decision closure for the M2 baseline. The original two-player,
no-GM human baseline remains an explicit Josh-owned deferred gate.

**M3a — Homestead shell: ✅ CLOSED on scripted-actor (proxy) evidence (2026-08-10, Rei gate t_449875bd ACCEPT; H reconciled 2026-08-12)**
Merged @ 4d0427b96; two-player exit via M3aExitScenarioTests (M5-stand-in:
2 scripted actors, adjacent 16m, ONE session — placement → construction →
crops → storage → furniture). Scorecard HOUSING-01 / FARM-01 C/W/A = 2;
**H = U (proxy/bot-functional only — scripted actors; H UNKNOWN until Josh
runs it; M3a contract replay = explicit deferred gate)**.

**M3b — Property persistence and recovery: ✅ CLOSED (2026-08-11, EXIT gate t_accb1c63 PASS)**
M3b-1..4 merged (5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea); EXIT E2E
f5b00c686 PASS 7m08s — N=3 crash cycles incl. kill -9 mid-save (INNODB_TRX-
observed) + container kill, 16 rows/boot, no loss/dup; autosave p95 1301ms
< 2000ms at 25 bots + 2 homesteads. PROPERTY-01 R = 2 (U→2 in f5b00c686).

**M3 loop reconciliation (2026-08-28; current source/test HEAD
`a77ef878d8fcba297c32c0228e712e0695cc4887`):** M3a's clean ordinary
Character loop is **place/build → plant/harvest → storage/coffer/furniture
state → observable ownership/contents result**; M3b restart persistence is a
separate loop. The prior exact source/test baseline `b9a72825f` recorded the
M3 focused aggregate **178/178**, with named slices M3a exit 1/1, M3b furniture
4/4, phase restart 10/10, property policy 11/11, and repair scanner 13/13.
Source commit `1a3f13dc1` is included at the current HEAD; the focused
`HousingStorageFurnitureTests` run is **13/13**, including unauthorized
coffer refusal before `OpenedBy` mutation and authorized-owner opening.
These are scripted/fixture proxy stages: property replay follows an ordered
sequence and fixture setup includes direct `SetPosition`/service preparation,
which is not an acceptance path. Player/H UAT remains open; no live-client
claim is made.

**M4 — Trade, crafting and transport integrity: ✅ MERGED + DEPLOYED (2026-08-12; H reconciled 2026-08-12)**
Pinned audited SHA **95bb1c78e** (merge: M4 EXIT integrated playable release,
t_97e59ffc, **Rei gate PASS** t_abe87eaf ACCEPT) — crafting integrity
(bag-scope material check + level-10 pack gate) + trade packs + vehicle
lifecycle + integrated session evidence. **PROD DEPLOYED** to CT 133 by Mai
(t_442f3016): image `aaemu-game:presence-demo` @ 6d5a07cf49a5 built from
pinned 95bb1c78e; rollback tag `presence-demo-rollback-pre-m4` (3ddcf7a4bdbc);
prod startup test PASS 37 min (0 restarts, 0 FATAL, 3/3 bots roaming, real
client accepted); manifest deploy/m4-manifest @ 03d3442bd (deliberately NOT
develop — fork develop carries M5-lane content). Gates: unit 1778/0/1;
M4ExitIntegratedSessionTests (4 scripted actors: harvest → craft pack →
slave cargo → 3-leg route → sell, 2× 124540 mails); restart E2E kill -9 PASS
(2m12s/3m09s/7m03s); CRAFT-01 / PACK-01 / SLAVE-01 C/W/A/R = 2, **H = U
(proxy/bot-functional only — scripted actors; H UNKNOWN until Josh runs it;
M4 economic/navigation replay = explicit deferred gate)**. Human playtest of
the integrated release remains the deployment-lane follow-up pending Josh GO.

**M4 loop reconciliation (2026-08-28; current source/test HEAD
`6ff68e1bb4a6afe08441308acb9a485b5133c42e`):** The clean ordinary
`Character` loop is **gather/harvest → craft pack → carry/place → load owned
vehicle → drive normal route → unload → sell specialty pack for reward →
repeat**, with per-object restart/persistence as applicable.
`SellSpecialty` now uses the canonical
`CSSellBackpackGoodsPacket → SpecialtyManager.SellSpecialty` path, with
ordinary merchant/pack checks, pack-consumption postcondition, same-zone and
no-pack refusal, repeat-cycle, and idempotency coverage. Focused results:
`M4ExitIntegratedSessionTests` 2/2,
`EconomyDayCycleScenarioRigTests` 4/4, and
`M3aM4ReplayScenarioRigTests` 2/2. The full normal-clone gate at this
source/test HEAD is **2498 total / 2497 passed / 0 failed / 1 skipped**;
compiler **0/0**; MCP **39 tools**. The skipped
`Provision_Activate_Persist_Deactivate_RoundTrip` requires
`AAEMU_LIVE_RIG` and `AAEMU_E2E_DB_PASSWORD`. A forced rebuild reported
1067 warnings / 0 errors. M4 Player/Bot loop closure remains
**Unknown/Open**: replay is ordered scripted/fixture proxy and direct setup
shortcuts are outside authentic acceptance. No live M4 restart/vehicle proof
was run because the shared E2E reset is unsafe. Human/client QAT remains open;
historical evidence is preserved.

**M5 — Gameplay Actor Contract: ✅ COMPLETE (2026-08-17 sync 2026-08-20)**
A1 (marshal bot steps onto the game loop — the M6-exit-blocking retroactive
fix) + B1 core action surface (Interact · Loot · UseItem · Mount/Dismount ·
AcceptQuest · TurnInQuest, each through real engine paths) merged to fork
develop @ 761d1e81a (Rei gates t_d06d8dd9 / t_ebfc9b35; merged-tree re-verify
1850/0/1 via Phase 3 t_9340e85d). **M5.1 economic extension — ALL MERGED:**
Plant (t_b1d7c430) · PackPickup/PutDown (t_64ecf525) · Buy/Sell (t_8741b03d) ·
control-plane API (t_7b6d7a4b) · MCP sidecar (t_446228b5) · first consumer
(t_52b2b084) · salvage wave Deposit/Withdraw (t_78ce17a2), Harvest
(t_234da01a), BoardVehicle (t_15343fdd), Craft rig+impl (t_6b5ac43e,
t_cffb71ad) — all done per kanban. Phase-2 prerequisites LoadPackOntoVehicle
(t_a7756a00) + DriveVehicle (t_eaf1754d) merged @ 6c2429ae0 + 6edbf0cbb.
Housing.Build = M5.2 merged @ 3396d9ef1 (Rei gate t_ebf36737 ACCEPT).
**M5.3 core surface (Observe/Move/Stop/Target/Cast): MERGED 2026-08-17 @
6b4ffe1d2** — canonical verification + exit scenario (t_c73d6293), Move rework
(8e9c0713a), SetTarget broadcast + ExecutionBoundary rework (t_09e1c671, Rei
gate t_5fa9bd73 ACCEPT), full gate 2102/0/1 on merged tree. hytest GM kit
(level/labor/gold/portals) merged @ 782ac3b3c (t_e1cf82c9, Josh ruling
2026-08-17) + .teleport mirage (t_42e24eca) — human-test fast-forward lane
LIVE. BACKTRACK Phase 1 (t_61a0eebb + full-route follow-up t_15787275) and
Phase 2 (t_b4f455b0) both DONE.
**H = UNKNOWN** — proxy/bot-functional evidence only; the five deferred human
gates below remain Josh-owned.

**M5 actor decision/action loop reconciliation (2026-08-28; source/test
checkpoint `9ddc322feee4f06c55df9f429e8da3ed573c1b85`):** The loop sentence is
“a clean ordinary `Character` observes current state, chooses one legal
objective/action, executes via `IGameplayActor`/normal `Character` services,
observes terminal state/audit, and retries safely without duplicate effects.”
`GameplayActor`/the M5 contract proves the request lifecycle, single-writer
gate, failure taxonomy, timeout/stuck handling, idempotency, and audit; focused
tests are **316/316**: `BotGoalArbiterTests` 14/14,
`GameplayActorM53CoreSurfaceTests` 13/13, `PlayerBotControllerAdapterTests`
5/5, `GameplayActorB1ContractLayerTests` 17/17, and `GameplayActorTests`
30/30. This is actor-contract evidence, not universal bot decision closure:
`LevelingLoopScenario` closes only the narrow autonomous 254→255 choice/pursuit
slice, while `BotScenarioRunner`/`M1M2`/`M3aM4` remain ordered proxy replays.
Player closure is **Unknown/H or client-gated where applicable**; the universal
PlayerBot decision loop is **Unknown/Open**. Fixed Priority-first
`CanActivate`/FSM scheduling exists, but no reusable candidate/score/blackboard/
rationale/replan/personality policy is claimed. Existing M5.3 canonical
movement caveat and formal regrade wording remain unchanged; H stays separate.

**M6 player-loop reconciliation (2026-08-28; current source/test HEAD
`da0fdc61a72a15111fddc8ac627a164a5f050558`):** A clean ordinary
`Character`/bot becomes dormant → proximity wake/materialize → resumes its
scheduled action → preserves identity, inventory, position, and metadata
through restart → dematerializes safely. `c97909f4f` isolates the baseline
presence roster before dormant seeding; `950cfd279` provides cooperative
bridge cancellation; `155c82c66` adds the natural six-hour dormant-timer stage
with explicit controls, cooperative deadline, and ID-bound cleanup. Focused
M6 evidence remains **105/105**. Corrected Tier3 readiness at
`4721cbd306cbf346bfe38b7373d5adf479b6231f` is historical 1/1: 1000 seeded,
embodied 50, dormant 950, materialized 50, p95 259.2ms, RSS +2.56%, 50
dematerialized, owned cleanup zero. The six-hour canary DID execute and
produced `/root/aaemu-e2e-a5-tier3-sixhour/logs/g2-a5-tier3-sixhour-report.json`
for 360.00003 minutes / 361 samples: DormantSpecs 1000, embodied 0,
materializations/dematerializations 0, queues/failures/save-skips 0, DB writes
delta 0, tick max 19.1ms, save p95/max 85.7/100.8ms. RSS assertion failed on
the pre-quiescence baseline (1207.9MB baseline, 5749.4MB peak, 3744.1MB final,
budget +512MB), not yet classified as a leak. Testing/canary diagnostic
failure only, not an A5 pass or live/human evidence. `ccd4ea857` corrects
warmup/baseline/peak handling; a preferably 12-hour rerun remains pending.
**M7 player-loop reconciliation (2026-08-28; current source/test HEAD
`ded008de8d67ece8718e9235fd02503b43ceb6a1`):** An ordinary
`Character`/PlayerBot discovers and accepts a quest, navigates, chooses legal
hostiles, casts, receives kill credit, loots, sustains/retreats, and
completes/repeats; the group variant adds party invite/follow/assist/death
recovery. Focused M7 evidence is **147/147** with no failures or skips:
primary **36/36** (Adventurer 12, PartySpike 4, PartyLifecycleFaultMatrix 4,
PartyFollowAssist 4, DeathWatch 5, LevelingLoop 7) plus actor support
**111/111**. Evidence is A/R rig/proxy: hunt kill uses real
`DoOnMonsterHuntEvents` with fixture HP=0; Party spike is synthetic/fixture.
There is no current live authenticated-client run and no H/UAT result.
`LevelingLoop` remains bounded autonomous 254→255 only. Broad autonomous
decision closure, real damage/`Npc.DoDie`, scheduler-driven route, party
roles/regroup/restart/disconnect, mount/travel, and H remain open.

**M7 — Adventurer and party bots: 🔶 GATING SPIKE DONE (2026-08-20) + heal/retreat landed**
One adventurer cleared quest 250 (Solzreed fox cull) end-to-end through the
M5 contract — accept at the real board → travel → hostile select (CanAttack)
→ burst Cast rotation → 3/3 REAL kills → 3 corpse loots → quest complete.
Rig 4/4 green + E2E PASS 1/1 2m15s (37 trace records; evidence
m7-adventurer-spike-report.json). Gate 2125/0/1. Spike found BUG-016
(18131-class melee skills never hit their primary target — FIXED same day,
census 415/13, 18131-led combo-chain rotation is the live regression) +
leash-reset/mana realities now recorded in ROADMAP M7. **Adventurer v1
sustain (heal/retreat) landed 2026-08-20**: the hunt loop checks vitals
before engaging — below threshold (0.35) the bot retreats along the
threat→bot vector, recovers (configured heal item through the real UseItem
path when bagged; out-of-combat regen fallback), and re-engages at 0.8 —
bounded rounds fail CLOSED with Starvation. Rig E-M7-3/4/5 green; live
exercise awaits level-appropriate content (foxes can't hurt the level-50
spike bot — recorded). Potion data note **CORRECTED 2026-08-23** (the old
"no direct-heal potion" claim was WRONG): direct-heal potions DO exist —
8515/8518/15580/15581 verified in canonical compact.sqlite3 —
HealItemTemplateId default flipped 0→8518 (c98da8a53).
Spike shortcuts on the record: level-50 provisioning, straight-line Move (no
pathfinding), death/resurrection (**RESOLVED 2026-08-20** —
scheduler death watch + CharacterResurrection, see M6 exit blockers below).
**Distance maintenance landed 2026-08-20**: the hunt loop keeps a standoff
band [StandoffMin, EngageRange] before the cast burst — too far closes in
(melee default: straight onto the unit, the proven live behavior; ranged:
to the band edge, never face-planting the target), too close (ranged
StandoffMin > 0) backs off along the threat→bot vector. Rig E-M7-6/7 green
(band-edge stop distances asserted through a position-recording runtime);
melee defaults byte-identical in behavior (EngageRange 3 = the old
hardcode). Rig gotcha recorded: Character.MaxHp computes from Level via
FormulaManager — rig tests that set Level must also refill Hp or the
sustain loop fires on every run.
**Equip contract action landed 2026-08-20** (M7 equip-upgrades
prerequisite): `IGameplayActor.Equip(itemTemplateId)` moves a bagged item
into equipment through the real CSSwapItemsPacket Inventory→Equipment path
(Inventory.SplitOrMoveItem, SwapItems task) — the engine's
EquipmentContainer.CanAccept validates slot compatibility before anything
moves, and the slot pick uses the engine's own GetAllowedGearSlots table
(first EMPTY allowed slot, else first allowed = client equip-over-occupied
swap). Full idempotency discipline (same-key pre-flight refusal + fresh-key
engine backstop). Rig 7/7 (GameplayActorEquipTests). Engine gap on record:
no level/requirement gate on the equip path (CanAccept checks slot only).
Rig gotcha recorded: SeedEquipItemTemplate must run AFTER CreateActor
(ItemManager is DI-only, seeded by the rig's Seed()).
**Equip upgrades landed 2026-08-20**: the spike hunt loop evaluates bagged
equippables after each corpse loot and equips upgrades through the Equip
contract — the upgrade rule mirrors the contract's own slot pick (first
EMPTY allowed slot else first allowed; equip when the slot is empty or the
candidate's template Level beats the occupant's), and level discipline
lives in the scenario (LevelRequirement ≤ bot level; the engine has no
equip level gate). Rig E-M7-8 green (two equips through the contract, the
equal-Level third sword stays bagged); live no-op honesty: fox loot is
flavor, so the stage records nothing on the live stack unless real gear
drops.
**Live failure found + hardened 2026-08-20 (E2E rebuild run):** a fox
pinned at full 217 HP across 100+ successful casts (leash-stuck class —
damage never lands) starved the hunt at 2/3 in 150 attempts. The hunt loop
now carries a NO-PROGRESS SKIP: a target that takes zero net damage across
NoProgressSkipRounds (3) executed-cast rounds is excluded from reselection
(exclusion only, never a kill credit; HUNT-SKIP stage) and the hunt moves
on. Rig E-M7-9 (one unkillable fox of four — skip fires, cull completes
with the healthy three). Open question on record: WHY a freshly spawned
fox can enter the pinned-HP state at all (cold-boot correlation: both
rebuild-run E2E failures showed it, warm runs never did). **Follow-up
2026-08-22:** an isolated forced-rebuild E2E cold start passed 1/1 in
2m57s: 3/3 foxes took damage and died, with no `HUNT-SKIP` and no observed
return-home/full-HP reset. The first cast samples can remain unchanged while
effects are scheduled asynchronously, so they are not alone evidence of a
damage failure. The original failure is therefore **not reproduced and root
cause remains UNKNOWN**; no speculative Npc AI change was merged. A second
repeat stalled in local harness startup before it produced a scenario
report, so it is not gameplay evidence.
**Return-to-NPC leg landed 2026-08-20**: the spike is now the M7-worded
short quest chain — after the 250 cull completes, the bot travels to the
quest-330 acceptor (golden route §1a step 3: Npc 3597, no objectives,
report Npc 3511), accepts through the real AddQuest gate, travels to the
report NPC, and turns in through the real packet path, draining the step
machine to completion (M1M2 replay shape). Rig E-M7-10 green (both quests
completed-and-dropped; contract vocabulary gains the second accept + the
turn-in). Rigs default the leg off (ReturnQuestId 0 keeps the one-quest
shape); live defaults run the chain.
**Adventurer v1 feature list COMPLETE 2026-08-20** — targeting, skill
priority, distance maintenance, heal/retreat, loot, equip upgrades,
return to quest NPC, death recovery all landed. Party v1 open.
Scheduling unblocked per the roadmap's spike gate. H UNKNOWN.
**Party v1 slice 1 landed 2026-08-21** — invite/join contract actions
(PartyInvite = 34, PartyAccept = 35) on IGameplayActor + the
PlayerBotControllerAdapter, through the real engine paths:
TeamManager.AskToJoin via the target-object overload (the exact
CSInviteToTeamPacket call, skipping the global name registry so headless
rigs resolve) and TeamManager.ReplyToJoinTeam (the exact
CSReplyToJoinTeamPacket call; invitation.TeamId 0 → engine CreateNewTeam,
else AddMember on the inviter's team). The engine's refusals on both
paths are SILENT voids, so the contract pre-flights (pending invitation /
already a member / no pending invitation → StateTransition, engine never
entered) and post-checks the observable outcomes (invitation record for
invite; Character.InParty + active-team membership for accept).
TeamManager.GetActiveInvitation went private→public (the invitation
record IS the observable outcome the actor must inspect). Rig upgrades:
the TeamManager seed now wires real ChatChannel instances and
incrementing team ids (bare mocks NRE'd CreateNewTeam's chat wiring and
collided every team on id 0), FriendMananger is seeded with an empty
friends table (Character.InParty's setter NRE'd headless), and
JoinActorWorld moves a second actor's character into the host session's
world (each CreateActor gets its OWN world; a party needs one). Rig
GameplayActorPartyTests 6/6 green. Follow-up slices: follow leader /
assist target (scenario surface), then the M7 party spike. H UNKNOWN.
**Party v1 slice 2 landed 2026-08-22** — `PartyFollowAssistScenario`
composes existing actor actions instead of growing the contract surface:
the member verifies it and the leader are active members of the same real
party (and that the supplied leader is the team's owner), follows through
`MoveToUnit`, then copies the leader's ordinary `CurrentTarget` through
`SetTarget`. The scenario fails closed for wrong party/world/leader state
or a leader without a target. Rig `PartyFollowAssistScenarioRigTests` 4/4
green: distant follow + assist, in-formation hold + assist, non-party
pre-flight refusal, and no-target refusal. Full gate 2163/0/1. Remaining:
M7 party spike. H UNKNOWN.
**M7 PARTY SPIKE COMPLETE 2026-08-23 (c98da8a53)** — `PartySpikeScenario`
(template m7-party-spike): a real 3-bot party completes rally → assist →
kill of elite NPC 1870 inside the leash window — live E2E PASS over the
generalized multi-actor bridge seam (`HandlePartyFollowAssistScenario`
generalized to N actors). Causal cast-effect traces landed in the same pass:
ActorAuditRecord v2 additive fields target_hp_before/target_hp_after,
effect_observed, effect_wait_ms (delayed effects now distinguishable from
failed hits). **Party v1 feature list COMPLETE — Adventurer v1 (2026-08-20)
AND Party v1 both done; scheduling unblocked.** H UNKNOWN.

**2026-08-23 mechanics sweep (8c198f13d → f3bb787ce, all pushed, gate green
at each step):** C2 social v1 — BotChatterService, 8 archetypes × 4 lines,
cooldowns/budgets/combat-suppressed, default OFF (Bots.EnableChatter /
AAEMU_BOT_CHATTER_ENABLED); movement stuck detection — NoProgressWindow 2.5s
→ TimedOut(Navigation) "stuck" + one unstick nudge. C1 schedules v1 —
BotScheduleService Home/Work/Travel/Rest phase machine w/ hysteresis,
persisted additively inside the schedule JSON blob (B4 byte-equality
preserved), default OFF (Bots.EnableSchedules / AAEMU_BOT_SCHEDULES_ENABLED);
economy day-cycle v0 (m8-economy-cycle-v0) — buy seed→plant→harvest→craft→
sell→deposit with explicit ledger + reconciliation laws, live E2E incl.
kill -9 restart ledger-equality PASS. Engine/harness hardening — equip
level_requirement gate in EquipmentContainer.CanAccept (engine hole closed);
M51AttachedPackRestartE2eTests closes the M5.1 attached-pack GAP FLAG
(survives kill -9, PASS ×2); M3aM4ReplayScenario warm-world AuditTrace.Last
crash fix; E2eStack.CleanupBotRows SQL IN-list fix; silent-catch sweep
80→0. A6 manifest provisioning — presence_manifest.json (Bots.PresenceManifest
/ AAEMU_PRESENCE_MANIFEST), AAEMU_PRESENCE_MAX_BOTS clamp default 10;
QuestActEtcItemObtain credit path (~51 live quests fixed); hauler leg on the
economy cycle (pack craft→LoadPackOntoVehicle→DriveVehicle→gold trader→
deposit); mail ReturnMail + expiry bounce/destruction semantics
(CSReturnMailPacket opcode confirmed 0xfff placeholder — NOT registered).
Trade functional (TRADE-01): OkTrade cancel-then-finish KeyNotFoundException
fixed; both-locked AND both-ok gate (was !a && !b single-side exploit);
TradeOffer/TradePutup/TradeLockOk contract actions;
TradeHandshakeScenarioRigTests 5/5. Auction expiry sweep hardened (per-lot
isolation, null-safe missing-item expiry, mail-fail no longer wedges lots,
_auctionTaskScheduled guard); AuctionHouseRestartE2eTests PASS (post→buy→
settle→expiry-mail across kill -9, 3m26s); E2eStack.RestartGameServer(afterStop)
seam.

**2026-08-24 sweep wave 2 + soak STAGE 1 (f3bb787ce → 3a534b539):**
G3-B3 DONE — IBotActivityModule + BotGoalArbiter (priority-based single-active
activity per bot per wake; schedule-phase P100 / presence-roam P50 / idle P0
first modules via IBotStepExecutor decorator); dead PlayerBotBehaviorController
stack deleted; fixed latent DI gap so Bots.EnableSchedules actually arms
BotScheduleService. Mechanics slices (0482ba3f0) — ITEM-01: item_proc_bindings
loaded + GetItemProcBindings, UnitProcs factory seam (items can carry procs);
MATE-01: mate_equip_packs/pack_groups/pack_items/slot_packs loaded in
MateGameData, fail-closed IsMateEquipAllowed legality at
MateEquipmentContainer.CanAccept, latent EquipmentContainer null-Owner
level-gate bypass fixed for mates; HOUSING-01 FIX-2: terrain/overlap/cap/race
checks verified as landed + two-thread build-race regression via real
HousingManager.Build; ZONE-01: hard-coded Conflict boot state removed →
data-driven Peace default (legacy World.ConflictZonesStartAtConflict flag kept
for tests), Peace-state PvP protection at the BaseUnit.CanAttack chokepoint
(fail-open when no conflict entry; Hostile stays attackable). TRANSFER-01
functional + LIVE PROVEN (3a534b539) — CSBoardingTransferPacket TlId shadowing
FIXED (multi-part transfers share the master's TlId but seats exist only on
child parts; FirstOrDefault always resolved the seatless master — boarding
could never bond); read-only `transfers` bridge dump command;
TransferRideE2eTests LIVE PASS (board Marianople Gondola tlId=1 ap=2
BondChairDouble → ride route samples → disembark at current position).
Scheduler-driven soak STAGE 1 executed (4e460305b) — SchedulerSoakStage1Tests,
10 manifest-provisioned citizens × 30min through real IPlayerBotScheduler
wakes; TWO VALID runs: ~90k steps, 0 failed/timed-out, wake avg ~99ms, DB
writes 14–19/min/citizen, tick+region budgets PASS. Engine findings on record:
(a) manifest roster entries without home spawn start at race-template position
but walk to the patrol-default Nuian home (run-1 elves walked 4.3km and
drowned); (b) physics slow-thread rate ~3× scheduler-disabled baseline
(0.23–0.27/min vs calibrated 0.031–0.067) — same-world clause far inside
budget, recalibration = M6-exit decision; (c) heap churn to ~5.9GB under roam
vs flat 3.4GB band.
**Soak follow-ups landed (615a645c9 / 2703fd46e):** (a) FIXED — manifest
route anchors at ACTUAL spawn when no explicit/persisted home (regression
test in BotPresenceManifestTests); (c) roam heap churn cut 38%/wake —
BroadcastMovement opt-out skips the per-apply movement packet for headless
roam (throttled executor broadcast remains), shared WalkDelta payload;
BotRoamAllocationTests pins 512B/wake (pre-fix 789B). **Run 3 on fixed build:
ALL budgets PASS except physics warnings 0.17/min vs 0.1** (same-world 2/30,
worst pass 110ms vs 40ms) — **OPEN JOSH DECISION:** recalibrate per-minute
aggregate (~0.3/min or severity+same-world-only) per t_18fccd09 precedent.
Remaining engineering follow-up: Transform.FinalizeTransform allocation per
move (engine-wide, benefits players too).

**2026-08-24 mechanics rounds 3-5 (0482ba3f0 → 0d1a6b8fe):**
Round 3 (0482ba3f0) — G3-B3 DONE: IBotActivityModule + BotGoalArbiter
(single-active activity per bot per wake; schedule P100 / roam P50 / idle P0;
dead PlayerBotBehaviorController stack deleted); ITEM-01 item_proc_bindings
wired; MATE-01 equip-pack legality; HOUSING-01 race regression; ZONE-01 Peace
boot + CanAttack enforcement. Round 4 — TRANSFER-01 fixed + live-proven
(TlId shadowing); soak STAGE 1 executed + run 3 verified home/churn fixes;
physics recalibration = OPEN JOSH DECISION (~0.3/min case recorded). Round 5
(cab6e4dc9 → 0d1a6b8fe) — M1 Lane-B: ConReportJournal wired-noop FIXED (466
quests auto-passed the journal gate; 59 instantly completable) +
ConReportDoodad FinalizeQuest double-subscribe leak fixed; golden-route
replay re-PASS; full 50-act audit clean except ConAcceptComponent (NOVEL-
MECHANICS, Josh design call). FISH-01: dossier + CastAt(position) contract
action + FishingVerificationE2eTests LIVE PASS (labor/worm/proficiency/loot
through plot 809); DoodadFuncBuyFish double-credit fixed. C7: expedition
contract actions ExpeditionCreate/Invite/Accept/Leave + lifecycle rig. C9:
duel rig found & fixed stuck-duel bug (RestoreFaction NRE in stop catch-all
left both players permanently IsInDuel). M7#2 party lifecycle fault matrix
(4 rigs: member/leader death → membership preserved through real
CharacterResurrection; invitation retry; target-loss fail-closed) — surfaced
+ fixed null-killer environmental-death NRE in Unit.DoDie/CharacterCombat.
M7#6 npc state telemetry: aggro-change snapshots (hp/pct/top-aggro) +
return-home entry Debug logs — fox pinned-HP diagnosable without speculative
AI changes.

**M6 exit blockers (as of 2026-08-20):** physics-warning regression
t_eecc5604 ✅ done · adopt-heal fix t_555ed207 ✅ done (merged; prod
re-provision verified by presence deploy chain) · **B4 playerbot_metadata
store ✅ done 2026-08-20** · **6.2 death/resurrection ✅ done 2026-08-20**
(CharacterResurrection core shared with the packet path + scheduler death
watch: dead bots stop getting work steps, poll, resurrect at the nearest
return portal after a 5s delay with the real 10%/debuff semantics,
server-side relocation through Character.SetPosition, then normal stepping
resumes — 5 rig tests green) · PlayerBotScheduler
scheduler-driven soak still open if M6 exit mandates it. **(STAGE 1 EXECUTED
2026-08-24 — see 08-24 sweep above: 10 citizens × 30min, ~90k steps, 0 failures,
budgets PASS; three open engine findings; full exit-label decision incl.
physics recalibration still open.)** **Exit-label note
(reconciled 2026-08-12, bot-backtrack):** soak verdict = "passed revised
approved budgets" — full M6 exit label NOT claimed; **B4 restart-persistence
scenario = explicit deferred gate**.

**M6 — Deterministic playerbot framework: 🔶 presence-demo hotfix chain DONE — parity + soak open**
Presence demo (3 citizen bots embody + roam AT Josh's spawn, zone 179)
live via the hotfix3 deploy overlay. Hotfix chain on
feat/bot-appearance-factory: null-safe ForceDismount + inactivity-sweep
skip (1c1fdd721), null-safe VisualOptions (53c2baee5), restart-idempotent
provisioning (fa9037c3c), terrain-aware roam waypoints + above-home
probe + flat-arrival Z clamp (2ff6f19f3/8e4b2b6b0/a32ee64d2), env-driven
patrol-home override AAEMU_PRESENCE_HOME_X/Y/Z (c22575d9d), world-ready
poll widened to 300s for cold boot (96e45252a), race-appropriate
unit_model_params provisioning so bot bodies render (d0e5feb9d),
BotAppearanceFactory — randomized player-like looks + per-class starting
equipment (91b308d71, t_61814965). M6.6 player-parity requirements
landed in ROADMAP.md (74151e060). E2E harness committed (Scripts/e2e);
presence-demo compose overlay captured in-repo
(docker-compose.presence.yaml). GM bot commands deployed P0
(t_7b4f9423).

**M6.6 open items — RESOLVED 2026-08-10 (three-card verification sweep t_120bb6c9 / t_509ef8c2 / t_1ed9881f):**
- **Parity audit t_98415169: ✅ CLOSED** — PARITY_AUDIT.md delivered 08-08; CRITICAL (factory-in-lineage) + MODERATE (skills/actabilities/bag) gaps closed by fix/parity-seeding @ 45cd3f3a9 (t_747a1c44): live-verified 34 actabilities/bot + skills row + bag byte-identical to human Asssaa (t_120bb6c9); LOW residual gaps tracked in PARITY_AUDIT.md (template/ambiance routes).
- **In-client visual acceptance: ✅ PASS (wire-level) — rendered screenshots pending Josh's client** — real X2 protocol client session received unit-state for all 3 Citizen bots (17× 0x69 distinct objIds/names + 164× 0x6C, all walking, t_509ef8c2); Josh sighting ACCEPTED 08-09. No Windows client in lab → rendered screenshot confirmation awaits Josh. ⚠️ Defect found: adopt-heal force-stamps demo blob → looks collapse to 1 on reboot (t_555ed207; fix pushed fix/adopt-heal-keeps-factory-look @ cdf6d4a62, awaiting Rei gate; prod needs re-provision after merge).
- **6h/10-bot soak: ⚠️ FAIL (numeric budget) — operational criteria all PASS** — full 6h window completed (attempt 3): 10/10 bots connected, 0 crash, 0 disconnect, RSS flat 3418-3453MB, tick p95 0.02ms, DB writes 262/500 — but physics slow-thread warnings 0.03/min vs 0 limit (11 transient single-frame WARNs, first = boot spike 459ms 21:25:18 PDT). Regression card t_eecc5604 filed: RCA or budget recalibration (precedent t_2006451f). Evidence: soak-report-20260810.md + gate-10-soak-20260810-102503.md (attached t_1ed9881f). Caveat: PlayerBotScheduler NOT enabled this run — scheduler-driven soak still required if M6 exit mandates it.

**G2-A4 save path (1,000-bot item): 🔶 implementation MERGED — acceptance measurement open**
SaveManager dirty-tracking merged to fork develop @ 5ed5d6493 (2026-08-10, t_8c18eb1c,
Rei gate t_53025996 ACCEPT): the periodic autosave now persists ONLY dirty characters
(Character.IsDirty/MarkDirty chokepoints; SaveManager.GetCharactersToSave;
DoSave(saveAllCharacters) force-all on shutdown + /save), closing the Kimi audit
finding "DoSave full-table sync save on every cycle" (t_0fda3cd3 → ROADMAP G2-A4).
Evidence: SaveManagerTests 10/10 (incl. 1,000-character simulated load), merged-tree
gate 1575/0/1, M2bE2e restart-persistence 5/5 (t_2ee39438 — disconnect save path
untouched). Remaining for A4: autosave p95 < 2s at 250 characters + zero _isSaving
skips at the milestone gate.

## E2E gates (GateSoakRunner, real Login+Game+MySQL, canonical data — evidence /root/aaemu-e2e/logs/):
- **10-bot correctness: PASS** (2026-08-09) — tick invoke p95 0.014ms /
  max 0.20ms (limits 100/250), ActiveRegionTick worst 18ms / 0 overruns,
  DB writes 276.53 (limit 500), 0 physics/tick-overrun warnings.
- **25-bot stability: PASS** (2026-08-09) — H2 gate 1.00, tick invoke p95
  0.018ms / max 3.02ms, ActiveRegionTick worst 45ms / 0 overruns, DB
  writes 262.66 (limit 500), 0 warnings.
- **6h/10-bot soak: ⚠️ FAIL (physics budget) — operational PASS** (2026-08-10,
  t_1ed9881f) — 10/10 connected 6h, 0 crash/disconnect, RSS flat, tick p95
  0.02ms; 11 transient physics WARNs (0.03/min vs 0) → t_eecc5604.

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | M4 prod image 6d5a07cf49a5 live (t_442f3016); M5.1 salvage wave next (Deposit/Withdraw t_78ce17a2 → Harvest → BoardVehicle → Craft split); M5.2 Housing.Build t_94761d55 running | 🔶 running |
| Verifies | Rei | M4 gate PASS t_abe87eaf; M5 gates t_d06d8dd9 / t_ebfc9b35 done | ✅ done |
| Dispatches | Mai | M4 deploy to CT 133 + prod startup verification (t_442f3016) | ✅ done |
| Tracks | Nei | 08-13 (t_c9f0d7f6): M5.1 recovery sync — salvage order + Phase-2 prereqs + Housing.Build scope mirrored across ROADMAP/STATUS/SCORECARD/progression-board/wiki; branch of record 983b35736 (ls-remote verified) | ✅ this card |

## Open tasks (kanban, AAEmu lane)

**2026-08-20 cleanup sync (Kimi, Josh-directed):** every AAEmu card listed
here on 08-13 is now `done` in kanban — including the M5.1 salvage wave
(t_78ce17a2 / t_234da01a / t_15343fdd / t_6b5ac43e / t_cffb71ad), M6
regression t_eecc5604, adopt-heal t_555ed207, harness extension t_f198bb0e,
verifier stub-registry t_913c1d4a, authority envelope t_5999b370 (ACTIVATED,
closed t_b1002aad), and both backtrack phases (t_61a0eebb / t_15787275 /
t_b4f455b0). Human test packet t_2b654349 delivered. No open AAEmu-lane
engineering cards remain.

**New 2026-08-20 — production defect found + fixed (this pass):** a stray
half-closed LAN client wedged the stream-port (:1250) receive loop into a
zero-progress spin (~20k ERROR lines/sec, 174% CPU) because PacketStream
over-reads log-and-return-0 instead of throwing, making packetLen == 0 on a
1-byte remnant. Fixed on branch fix/protocol-spin-guard: truncation guard +
malformed-length close in Stream/Game/Game-side-Login protocol handlers +
5 regression tests. Prod mitigation: game container restarted 2026-08-20
(spin cleared, boot clean, 0 FATAL).

**Known deployment gap:** prod CT 133 image 32978f3613e3 = develop @ ~81676c0d6
(08-17 teleport-mirage). Missing from prod: M5.3 rework (6b4ffe1d2), hytest GM
kit (782ac3b3c), spin-guard fix. Rebuild + redeploy recommended before QAT.

**Remaining Josh-owned (deferred gates, bots cannot substitute):**
M1 Solzreed human route · original M2 two-player baseline · M3a contract
replay · M4 economic/navigation replay. hytest GM
kit + .teleport mirage + GM access (t_01a893c7, deployed t_d8658d50) are the
fast-forward lane for these. (M6 B4 restart scenario engineering completed
2026-08-20 — the remaining piece there is the M6 exit-label decision; the
B4 line item is now FULLY closed: metadata store + audit-trace flush.)

**New 2026-08-20 — B4 playerbot_metadata store (this pass):** the M6.0
metadata list (personality/schedule/profession/home/behavior/planner state)
now has a table and a store — `PlayerBotMetadataStore` (self-healing schema,
write-through REPLACE on mutation for hard-kill safety + dirty flush in the
SaveManager transaction), presence demo resolves home explicit-env →
persisted → template and records home + roam-loop schedule per bot. B4
restart replay extended to assert metadata directly: PASS 1/1 (4m39s,
evidence gate-m6-reconcile-b4-20260820-162058.md). Full unit gate 2121/0/1
(+15 store tests). Branch feat/b4-playerbot-metadata. NOT yet deployed to
prod — presence-demo home resolution gains a persisted fallback, so prod
deploy is a separate Josh decision.

## Legacy upstream item (predates one-way policy)

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)
- No new upstream branches or PRs are permitted; upstream is intake-only.

## Last scorecard update

- 2026-08-26 — Mail S3 acceptance and recovery reconciliation on develop @
  `31045d033`: authenticated real-packet
  `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
  PASS 1/1 in 2m39s on isolated MySQL/Docker; instance-faithful
  equipment+copper restart flow, ownership guards, unread recount lifecycle,
  sequential take, and delete persistence all passed. PB-005 remains
  FIXED-PARTIAL; the historical PB-007 status was OPEN/narrowed with corrected
  live rerun pending; final gate **2480/0/1**. This predates the current
  source-pinned closure above.
- 2026-08-26 — prior recovery snapshot (superseded by the Mail S3
  acceptance above): develop @ `e5db6d390` carried grounding `38c4997d3`,
  recovered Retribution test branch `a4f7820ba`, and merchant merge
  `e5db6d390`; PB-005 was FIXED-PARTIAL, and the historical PB-007 status was
  OPEN/narrowed. Mail S3 was still recorded incomplete/uncommitted. Final gate
  **2480/0/1**.

- 2026-08-24 — this commit: SCORECARD promotions from the post-f3bb787ce sweep —
  ZONE-01 W=2/A=1 (data-driven Peace boot state + CanAttack enforcement;
  rig-tested, no live PvP scenario yet) · MATE-01 W=2/A=1 (mate equip-pack data
  + fail-closed legality; no live equip E2E yet) · TRANSFER-01 W=2/A=1
  (TlId-shadowing fix; live board/ride/disembark E2E PASS) · ITEM-01 evidence
  note (item_proc_bindings loader + UnitProcs seam; grades conservative) ·
  HOUSING-01 evidence note (build-race regression added; grades unchanged) ·
  BOT-02 note (scheduler soak STAGE 1 executed; staged ladder continues). H
  stays UNKNOWN everywhere. Branch of record → 3a534b539.
- 2026-08-23 — this commit: SCORECARD promotions from the 08-23 sweep —
  AUCTION-01 W/A/R → 2 (live E2E incl. kill -9 restart pin, strongest of the
  sweep) · TRADE-01 W=2/A=1 (trade handshake rig + engine fixes; A stays
  honest: rig-level) · PARTY-01 W=2/A=1 (party spike live E2E c98da8a53) ·
  MAIL-01 A=1 (return+expiry rig-tested; CSReturnMailPacket opcode still an
  unregistered 0xfff placeholder) · QUEST-01 EtcItemObtain credit note
  (~51 quests). H stays UNKNOWN everywhere. Branch of record → f3bb787ce.
- 2026-08-13 — **canonical sync (t_c9f0d7f6)**: M5.1 recovery plan recorded —
  Kimi memo + Codex reconciliation (salvage order Deposit/Withdraw → Harvest →
  BoardVehicle → Craft split with card ids; work preserved, no
  re-implementation); LoadPackOntoVehicle (t_a7756a00) + DriveVehicle
  (t_eaf1754d) recorded as genuine Phase-2 prerequisites; Housing.Build =
  M5.2 contract card t_94761d55 in Josh-approved Phase-2 scope (impl open);
  Phase 1 t_61a0eebb stays open (min-slice evidence only; follow-up
  t_15787275); control-plane API / MCP sidecar / first consumer marked DONE
  (were queued); branch of record → 983b35736 (ls-remote verified); H stays
  UNKNOWN everywhere.
- 2026-08-12 — **bot-backtrack Phase 0.2 reconciliation (t_4ec066d3)**: M3a/M4
  H grades corrected — scripted-actor evidence is proxy/bot-functional, H =
  UNKNOWN until Josh runs it; SCORECARD H dimension = actual player only
  (never H=2); deferred gates recorded (M1 human route, original M2 human
  baseline, M3a contract replay, M4 economic/navigation replay, M6 B4 restart
  scenario); waivers visible in ROADMAP/SCORECARD/STATUS — branch
  docs/phase0-2-reconcile, Rei gate t_ee64e86b.
- 2026-08-12 — tracking refresh (t_773f9651): STATUS.md M4 row (merged +
  deployed @ pinned 95bb1c78e, Rei PASS, prod 6d5a07cf49a5, t_442f3016) +
  M5 A1/B1 row (develop @ 761d1e81a, ls-remote verified) + M6 exit blockers;
  branch-of-record → 761d1e81a; hermes-ops SLO window relabeled sidecar/shadow
  baseline (f31f829, decision log 8a0fb09).
- 2026-08-11 — this commit: M1-M3 audit C4 tracking refresh (t_b3980118,
  audit t_5b1f5494 PASS WITH NOTES) — STATUS.md M2/M3a/M3b rows +
  branch-of-record 4ded92c61; ROADMAP.md M3a/M3b closeout lines + M1
  status reconcile (closed on automated evidence, human playtest verdict
  open — C5); progression-board.md M3a/M3b rows.
- 2026-08-10 — this commit: post-merge tracking for SaveManager dirty-tracking
  (merged 5ed5d6493, t_8c18eb1c / Rei gate t_53025996 ACCEPT) — SCORECARD.md fork-fix
  entry + PROG-01 save-path pointer, ISSUES.md AUDIT-001 closure, STATUS.md G2-A4
  note, ROADMAP.md A4 implementation annotation.
- 2026-08-10 — this commit: STATUS.md M6.6 closeout — parity audit
  t_98415169 CLOSED (seeding gaps live-verified 45cd3f3a9), in-client
  wire-level PASS (t_509ef8c2) with appearance defect t_555ed207 pending
  Rei gate, 6h/10-bot soak operational PASS but harness FAIL on physics
  budget (t_1ed9881f → regression t_eecc5604).
- 2026-08-09 — this commit: progression-board.md refresh (M1 CLOSED,
  M2b-E2E DONE, M2c kill-acceptor + ZoneKill landed, M6 hotfix chain done
  + 6h soak running) and STATUS.md drift fix (parity audit t_98415169
  done, in-client sighting ACCEPTED, soak running t_1ed9881f).
- 2026-08-09 — earlier: STATUS.md M6 presence-demo refresh (M1 closed,
  hotfix chain + e2e gates 10-bot/25-bot PASS, M6.6 open items); e2e
  harness + presence overlay committed (06e6fcb4a, 615c3719c).
- 2026-08-04 — M1-5c closeout (t_cb64d872, 6e367585 on feat/quest-scenario-harness):
  SCORECARD.md quests-row runnability note 153/153 + M1-5 entry.

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
