# G3 diagnosis repro — warm q0 lane, observe-only (2026-09-19)

Lane: `E2E_ROOT=/root/aaemu-e2e-q0`, game PID 522433, `COMPOSE_PROJECT_NAME=q0pilot`,
src rev `6c3efe25`, runtime sqlite md5 `78b3bdbf038db3b927056106efdf91af`.
Read-only lane fact ( contradicts test docstring `G3AutonomousQuestAcceptTests.cs:39-41`,
dated 2026-09-18): game environ **HAS** `AAEMU_QUEST_BOOTSTRAP_ENABLED=1`
(+ `AAEMU_BOT_CTRL=1`, token; nothing else `AAEMU_*`). New binaries since the docstring.

## Per-run table (3 runs used of 5; fresh timestamped account each; stopped: branch cause found)

| run (UTC) | charPos (bridge `charPos`) | giver 2425 objId | money | lvl (cmd) | wake | observe/audit | verdict |
|---|---|---|---|---|---|---|---|
| 07:36:2x | 14536.6,11117.1,110.8 | 44311 | 0 | 10 | stepped=True questLegActive=False accepts=0 activeCount=0 reason=[] | questLegActive=False questActions=0 accepts=0 last=None | NOT IMPLEMENTED @ RUN/no-autonomous-accept |
| 07:3x | 14536.6,11117.1,110.8 | 44311 | 0 | 10 | identical | identical | identical |
| 07:4x | 14536.6,11117.1,110.8 | 44311 | 0 | 10 | identical | identical | identical |

Byte-identical across runs (same spawner staging, same objId — NPC 2425 stable at 44311).
activeBefore=False every run (no pre-emption). Discovery count = 0 Discover rows;
dispatch = none (no request, lastAction empty). Selected activity: unobserved
(`questLegActive=False`, travel reason empty — leg never ran, see §2).

## 1. SCALE VERDICT: CONSISTENT (10x hypothesis KILLED)

All five G3 surfaces consume the **same** `System.Numerics.Vector3` world position,
game units (m), no scaling op anywhere on these paths:
- staging/teleport echo: `spawner.Position` XYZ → `Transform.Local.Position`
  (`BotDriveBridge.cs:803-805`); region-sync `AddVisibleObject` (`:79-85`).
- bridge `charPos`: `character.Transform.World.Position` XYZ + zoneId/instanceId/
  worldId/worldName (`BotDriveBridge.cs:960-969`). **Live: 14536.6,11117.1,110.8**
  (×3, identical) — executor magnitude (1e4). A ×10 bridge bug would read ~145366.
- giver position: same `Transform.World.Position` type (`GameplayActor.cs:984,1210`;
  objId lookup `BotDriveBridge.cs:816-819`). Live objId 44311 stable.
- travel-target coords: same floats, `:F0` (`BotRoamStepExecutor.cs:1231-1234`).
  Empty this lane (leg never ran) so no same-run numeric pair printable — the report
  prints observe xyz nowhere and reason stayed empty.
- discovery: `MaxQuestDiscoverRange = MaxInteractRange = 25f`
  (`GameplayActor.cs:1187,1423`); flat XY (`MathUtil.CalculateDistance` default
  `includeZAxis=false`, `MathUtil.cs:332-342`; applied `:1211`); scan
  `WorldManager.GetAround<Npc>(character, radius)` (`:2044`, `GameplayActor.cs:253`).
- `ActorObservation.Position = Character.Transform.World.Position`
  (`GameplayActor.cs:247`); nearby scans 25f (`:253-255`); quest snapshot XYZ
  (`BotQuestLoopObservation.cs:91-101`).
Conventions: X/Y planar, Z up; range=flat-2D, snapshots=full-3D; world identity via
zoneId/worldId. The "155781"-magnitude value originates outside these five surfaces.

## 2. DETERMINISM: DETERMINISTIC GIVEN INPUTS (3/3 identical)

Same inputs (fresh broke bot, staged at 2425 spawner, flag on in-game) → same
signature. But the test's verdict label is **misattributed on this lane**: `bootstrapOn`
is read from **runner** env (`E2eStack.cs:1042,1050`; runner lacks the flag) while the
game has it ON — so "bootstrap module off" (`QuestBootstrapActivityModule.cs:68-69`)
is false cause here. Branch-cause chain (code-grounded):
- QuestBootstrap IS registered (`Program.cs:318`), options from env at startup
  (`:310`, `QuestBootstrapActivityModule.cs:21-24`), default pressure probe Healthy
  (`:55-61`), single shared executor singleton (`Program.cs:269-288`,
  inner binding `:328-332`).
- For a broke (0 < GoldTarget 1000, `:84-87`), alive, non-battling fresh bot every
  higher module denies by default env (Schedules 100 off `:76-81`; Recovery 85 full-HP
  deny `OutOfCombatRecoveryModule.cs:480`; Conflict 75; Fishing 60 off;
  Homestead 60 disabled; NeedsFarm 55 < 58) → arbiter deterministically holds
  `quest.progress` (`BotGoalArbiter.cs:157-218`, first-Allow-wins, priority-desc).
- Had our bot's arbitrated step executed, `StepQuestLeg` would run
  (`BotRoamStepExecutor.cs:661-668`) and the travel reason would be NON-empty at
  minimum (`ArmQuestTravel` sets it on every no-work path, `:1218-1241`).
  Reason is empty ⇒ our bot completed no arbitrated step in either wake window.
- Yet `stepped=True` in ~0.2s: the wake detector polls **global**
  `TotalStepsRun` (`BotDriveBridge.cs:4381,4401-4411`) with zero per-bot attribution,
  so any other Active bot's step satisfies it. Stale runtimes accumulate: G-tests
  `Spawn`+`Activate` (`:4385-4397`) and never `Remove`/`Forget` (G3 finally only
  `session.Dispose`); route-holding ones re-step on cadence (`BotRoamStepExecutor.cs:
  1116-1118`, `PlayerBotScheduler.cs:429-447`); dormancy/presence/auto-restore all
  default-off. Idle ones go dormant (`DormantTask`), so churn depends on leftovers —
  either way the signal is unattributable.
NET: observed behavior deterministic; branch cause = unattributable wake signal +
stale-Active-bot accumulation, NOT a bootstrap flag outage. True arbitration outcome
for our bot (quest.progress held vs IsInBattle-deny) is unobservable from this signal.

## 3. LEAKAGE VERDICT: STATE LEAK (test-signal-relevant; per-bot arbitration inputs clean)

Survives between fresh-character runs (warm lane, no reboot):
- `PlayerBotManager` registry entries (never `Remove`d), executor `_states`
  incl. `Actor.AuditTrace`, `QuestTravelTarget/Reason`, `Path` (`BotRoamStepExecutor.cs:
  356,376-380`), arbiter `_activeActivity` (never `Forget`n,
  `BotGoalArbiter.cs:88,238`), scheduler leases/schedules — keyed by characterId, so
  fresh accounts don't cross-talk, but stale Active entries keep stepping and pollute
  the global step counter the G3 wake detector relies on.
- Benign/shared (no per-run reset needed): `PlanTemplateCache` (keyed templates,
  deterministic reuse), `WildFarmPoiRegistry` (read-only), fishing `s_settledContests`
  (global occurrence ids, irrelevant), chatter cooldowns (per-bot ids; zone budget is
  cross-bot but chatter-irrelevant), `ActorIdempotency` (per-actor; `CycleId` embeds
  per-wake ticks, `BotRoamStepExecutor.cs:1194` → no cross-run false dedupe).
- DB rows ARE cleaned per run (`E2eStack.CleanupBotRows`).
Proposal only (do NOT implement): after `session.Dispose`, `manager.Remove(charId)` +
`arbiter.Forget(charId)` + executor state drop; and prove OUR step executed (e.g. gate
the wake poll on a per-bot observable such as `questLegActive` flip / audit-trace
growth / per-bot step counter) instead of global `TotalStepsRun`.

## 4. MONEY RECORD (fresh-character starting copper; GoldTarget input — NOT altered)

Run1 0 · Run2 0 · Run3 0 (wake `money` field, `BotQuestLoopObservation.cs:97` ←
`Character.Money`). 0 < GoldTarget 1000 (`QuestBootstrapModuleOptions`:18,
`NeedsFarm`:22) → the broke-leg of `CanActivate` (`:84-87`) is satisfied live.
