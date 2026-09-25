# Movement-ownership report — pursuit vs quest-travel attribution (2026-09-20, branch develop)

Turn 2 (redeploy + reproduce once + correlate). Prior: `scorecard-explorations/mechanics/gap-nav-persist.md`
(§1 nav contract, §1.5 gaps); Turn-1 inventory + minimal telemetry (deployed this turn, build green).

## A. Ownership map (every production path that can write this bot's position)

| # | Writer | Owner tag | Entry / file:line |
|---|--------|-----------|-------------------|
| 1 | Tick Move branch → `ApplyCharacterMove` → `VehicleMovementModel.ApplyUnitMove` (broadcast on) | leg owner (staged) | GameplayActor.cs Tick (~4236), ApplyCharacterMove |
| 2 | Same, `BroadcastMovement=false` (roam forces false, BotRoamStepExecutor.cs:536) → direct `SetPosition`+`FinalizeTransform` | leg owner | GameplayActor.cs ApplyCharacterMove |
| 3 | Mate-mounted legs write the MATE transform | leg owner | same |
| 4 | Tick Drive branch → `ApplyVehicleMove` → `ApplySlaveMove`/`ApplyUnitMove` on vehicle | leg owner | GameplayActor.cs (~4332) |
| 5 | Unstick recovery: `_unstickWaypoint` steering inside the SAME Tick/request | `UNSTICK` (per-step override; leg keeps dispatch owner) | GameplayActor.cs Tick |
| 6 | Roam ground clamp: Z-only `SetPosition` AFTER every tick, outside any request | `OTHER:ground-clamp` (via `NoteExternalPositionWrite`) | BotRoamStepExecutor.cs:1089 |
| 7 | `BotAdminService.Go` fixture teleport + spawn/home restores | `FIXTURE_TELEPORT` (Go logs `BotFixtureTeleport`) | BotAdminService.cs:358,087,280; Presence:394,485; ScenarioRunner:343; DormantRegistry:345 |
| 8 | Death-watch resurrect + `SetPosition` | (logged by existing scheduler Info; no new tag) | PlayerBotScheduler.cs:539-545 |
| 9 | Skill/knockback engine effects; `BindSlave`/`Bonding` parenting (child world-pos follows parent); scenario `SetPosition` hops | `OTHER:<path>` (documented, not instrumented) | engine |
| 10 | Network `CSMoveUnitPacket` | `NETWORK_MOVE` — NULL path: headless bots have no connection | documented |
| — | Dispatch staging | `PURSUIT_MOVE_TO_UNIT` (QuestBehavior.DispatchPursuitMove), `QUEST_TRAVEL` (ArmQuestTravel→state.PendingMoveOwner), `ROAM` (SetRoamRoute), `HUNT` (hunt chase), `NAVIGATION` (Navigate* default), `OTHER:party-follow/party-assist/pvp-engage/butcher-approach/needs-farm-travel/needs-farm-leisure`, method defaults `OTHER:move-to/move-to-unit/drive-vehicle` | QuestBehavior.cs, BotRoamStepExecutor.cs, GameplayActor.cs |

Single-`_active` gate verified SINGLE actor instance per bot (states keyed by CharacterId;
queue+step+arbiter share `GetOrCreateActor`) — BUT position has NO exclusivity: clamp/teleports/
death-watch write with no request. Stale-actor risk if Character re-embodied (sweep even probes
`ReferenceEquals(bot.Character, actor.Character)`).

## B. Tick path

Scheduler wake → `BotRoamStepExecutor.StepAsync` → `actor.Tick(elapsed≤1s)` → Move branch →
`ApplyCharacterMove` → `ApplyUnitMove`-or-direct-`SetPosition` + `FinalizeTransform`.
Pumper: one Tick per scheduler wake (scan 100 ms, drain 10 ms, live cadence ~66 ms @15 Hz).
Quest leg gated on actor-idle (Executor:682-688, `BotBehaviorRuntime`:113-119) so it SKIPS while a
pursuit Move is Running — but `actor.Tick` at :1014 still advances movement on the same pump.
`Elapsed` is pumped clamped time (`MaxStepElapsed` 1 s), NOT wall-clock: 101 wall-s with internal
<10 s iff wakes stall ~10× (scheduler stopped/lease contention).

## C. Pursuit trace (this run)

NONE — START never reached. Gate run 2026-09-20 09:40 PDT (`g5-moving-start-report.json`,
verdict UNKNOWN, legs=[adopt-lane]): adopt-lane OK (WebApi TCP + bridge ping), then
`BotNetworkSession.ConnectAsync` threw at :104 — 20 s timeout waiting `ACWorldCookiePacket`
after `CAEnterWorldPacket`. Auth + world-list succeeded; login never issued the cookie.
Zero quest-leg wakes, zero Move dispatches, zero sweep lines for our bot post-boot.

## D. Per-tick table, condensed (full table: `lane-logs/per-tick-table.md`)

| T (PDT) | kind | owner | detail |
|---|---|---|---|
| 09:33 | deploy | — | `dotnet publish AAEmu.Game -c Release` → lane runtime; UTF-16 strings confirm `BotMoveStart`/`BotMoveTerminal` + owner tokens in deployed dll |
| 09:38:42 | restart | — | old game (pid 3889168) killed; login logs gsId-1 disconnect |
| 09:39:49 | restart | — | new game (pid 1835221) `Server started!`; env `AAEMU_QUEST_BOOTSTRAP_ENABLED=1`, `AAEMU_BOT_CTRL=1`, token preserved; login+MySQL untouched |
| 09:39:49 | anomaly | — | game logs `LoginProtocolHandler - Connect to 127.0.0.1 established`, but login never logs the accept and never processes registration (35 B registration packet still sitting in login-side recv-Q minutes later) |
| 09:40:13 | setup-fail | — | test login `g5pursuit164013 connected`; login WARN `Cannot enter world: game server GameServerId 1 not available`; 20 s cookie timeout → `SETUP/enter-world` |
| post-boot | telemetry | — | 57 Info lines flow to `Server.log` (pipeline live); **0** `BotMoveStart`/`BotMoveTerminal`, **0** sweep lines — no leg dispatched since boot (lane quiescent) |

## E. Owner verdict

**(D) still UNKNOWN.** Missing signal, named: any post-START behavior — no enter-world cookie,
so no START snapshot, no pursuit leg, no travel leg, no terminal. No evidence for or against
A/B/C this run. The single ordered reproduction was voided at setup (zero behavior observed);
no rerun performed (identical failure certain while gsId 1 stays unavailable; reruns disallowed).

## F. South explanation

No new data. Leading hypothesis UNCHANGED: travel-owned arrival at 3597 (quest-travel fallback
target), NOT pursuit homing — `MoveToUnit`/`NavigateToUnit` resolve the target position ONCE at
dispatch (snapshot); a pursuit leg cannot home 240 m south onto a spawner. Pursuit-homing is
disfavored by construction; travel-arrival fits the endpoint exactly (see H).

## G. Terminal reason

None observed this run. Prior terminal UNKNOWN stands (no lifecycle NLog pre-telemetry).
Next run: `BotMoveTerminal` (Info for Move/Drive with owner+elapsed+detail) closes exactly this gap.

## H. Parking coordinate mapped

STATIC OWNER, with new live corroboration. (15562.5,15354.1,127.9) vs NPC 3597 (Mor) spawner
(15562.45,15354.32,127.99, `npc_spawns.json:185037`) = ~0.24 m delta, inside the 0.5 m arrival
box; Z matches the spawner (127.99), not the profile point (126.3). SAME point, not coincidence:
the endpoint IS the quest-330 acceptor / quest-travel target. Corroboration: pre-boot lane sweep
(`Server.log`, 03:09:41) shows lane bot charId 35 stationary at exactly (15562.5,15354.1,127.9)
with live NPC 3597 measured 0.3 m away in the same sweep (`eval=[44297:3597:0.3:0.5,…]`).

## I. Tick/budget finding

Code answers from Turn 1 stand (see B). Live validation blocked: quest-leg silence-while-Running
and pumped-vs-wall elapsed could not be observed (no Running leg existed). The mechanism that
produced the original 101 s quest-leg silence is consistent with the idle-gate (branch skipped
while Running → no sweep lines), but that join remains code-derived, not log-joined.

## J. Fix-or-none

**NONE.** No (A)-class defect identified (no verdict possible); no production behavior change
this turn. Shipped telemetry only (deployed, build green): `MoveOwner` on request + audit
(`move_owner` JSON), `BotMoveStart`/`BotMoveTerminal` lifecycle lines, per-apply
`CurrentMoveOwner`/`LastAppliedPosition`, six-file patch in `lane-logs/telemetry.patch`
(file-scoped; files also carry other turns' uncommitted work — hunks identified by owner-tag
strings; no foreign hunk touched).

## K. Exactly-one next step

Bounce the q0 **login** server (its internal accept path is the wedged component — game→login
registration unread since ~02:40; game-only restarts cannot heal it), then run the G5
moving-START gate exactly once and join `BotMoveStart` → sweep dist-trail → `BotMoveTerminal`.

## Provenance / budgets

- src HEAD `6c3efe25` + dirty tree (telemetry + others' uncommitted work; deployed binary =
  post-publish 09:33 PDT). Prior reports archived pre-overwrite:
  `/tmp/g5-moving-start-report.json.pre-ownership-2026-09-20T163600Z`,
  `/tmp/g5-pursuit-gate-report.json.pre-ownership-2026-09-20T163600Z`.
- Budgets consumed: 1 publish, 1 game restart (standard `E2eStack.RestartGameServer`, temp
  runner deleted), 1 gate run (voided at SETUP, 21 s), 0 reruns. Login pid 2079399 and
  `q0pilot-db-1` untouched throughout. Stop.

## §L. Rerun addendum (2026-09-20, post-login-bounce)

### L1. Bounce method + verification
No login-only restart exists in `E2eStack` (only `StopAll`/`StartServers`), so the
else-branch was used: clean SIGTERM (+10 s wait, SIGKILL fallback) of pid 2079399, then a
supervised boot of the untouched `runtime/login/AAEmu.Login.dll` with the byte-identical
replica of the live process environ (97 vars, incl. `E2E_*` ports, `AAEMU_BOT_CTRL=1`,
token), same cwd, stdout→`logs/login.log` (prior 51-line log backed up to
`/tmp/q0-login.log.pre-bounce-20260920`). Game, MySQL, DB volume, flags, binaries untouched.
New login pid 1888425: `Registered GameServer GameServerId 1` at 09:48:16, fresh socket pair
with recv-Q 0/0 (the 35 B stuck registration drained by replacing the wedged listener).
Readiness: TCP 2237/2239/2250/2280/2260 open + bridge pong. This confirms the §K diagnosis:
the old login's internal accept path was wedged; game-only restarts could never heal it.

### L2. Rerun verdict
G5 moving-START gate, unmodified, once (prior report archived to
`/tmp/g5-moving-start-report.json.pre-rerun-20260920T164900Z`): wall 314.7 s, setup 13 s,
exec 301.7 s → **FAIL-BEHAVIOR/PURSUIT-NO-PROGRESS** (first-failing predicate; seenMove and
seenMoveRunning true, progress false). Zero combat (casts 0, meat 0→0, quest still active,
turnIns 0, prey alive). Bot: charId 38 / actor 1324 / `G5pursuit164852`; target live 3475
objId 44309; spawner [15586.5,15117.3,130.9]; staged 15.0 m.

### L3. Per-leg correlation (new telemetry; full table: `lane-logs/per-tick-table.md`)
| T (PDT) | trace | kind | owner | dest | pos-before → pos-after | dist | terminal |
|---|---|---|---|---|---|---|---|
| 09:48:55 | 80df1fe6 | Move | QUEST_TRAVEL | (15562.5,15354.3) Mor | spawn (15578,15382) → ~23 m S | 238→~215 | Interrupted @9.4 s `interrupted by controller` (= accept_quest API pre-hold via queue preempt) |
| 09:49:05–09 | 36× legs | Move | QUEST_TRAVEL | (15562.5,15354.3) | — (no write) | — | Rejected @0.0 s `StateTransition: actor busy` — route-advance site firing per scheduler step (~9/s) against the Running pursuit; single-writer HELD |
| 09:49:05 | cf6ceeaf | MoveToUnit 44309 | PURSUIT_MOVE_TO_UNIT | snapshot (15585.8,15116.6) @4.5 | staged (15601.5,15117.3) → (15585.8,15118.4) | 15.7→~0 | Completed @3.7 s `arrived` (15.7/4.5 = 3.49 s + profile ramp ✓) |
| 09:49:09 | 047d171f | Move | QUEST_TRAVEL | (15562.5,15354.3) Mor @2.5 | spawner area → ~150 m S | 238→~88 | TimedOut @60.1 s `Navigation: navigation budget exceeded` (full RoamLegTimeout; stuck detector silent — continuous displacement resets it) |
| 09:50:09 | 373271e0 | Move | QUEST_TRAVEL | (15562.5,15354.3) Mor @2.5 | ~88 m out → Mor | ~88→~0 | Interrupted @35.6 s `stop requested` (executor flat-arrival Stop; cumulative ≈239 m ≈ full distance) |
| 09:50:45 | 2b02bbd3 | Move | QUEST_TRAVEL | (15562.5,15354.3) | at Mor | ~0 | Completed @0.0 s `already at destination` (inside 0.5 m box) |
| 09:50:47→09:54 | wakes | — | — | parked (15562.5,15354.1,127.9); quest wakes land AdvanceQuest no-ops; funnel `no-selection` (only 3597@0.3 m + 10688@5.3 m in perception); pursuit withdrawn | — | — |
Bridge trail (bot→pinned spawner, per wake): 14.9 → 4.8 → 1.3 → … → 238.0 pinned. Funnel
dist-trail caveat: `ReadFunnels` re-reads the cumulative log without a cursor, so the
15.6↔15.7 trail re-appends the same two setup measurements every wake (274 = 2×137) — the
live distance evidence is the bridge trail, not the funnel trail. Verdict unaffected
(seenStop=false → NO-STOP would follow even with a perfect trail).

### L4. Classification: (B) travel/route-owned arrival at 3597
Pursuit is EXONERATED by its own math: it walked the correct snapshot vector (15.7 m west,
3.7 s ≈ 15.7/4.5 + ramp) and `Completed(arrived)`. NO-PROGRESS cause: the live boar moved off
during the leg (snapshot-stale arrival — the documented §1.5.1 semantics, no defect), no
mid-leg retrack exists by design (quest leg skipped while Running), and after arrival the actor
was route-owned until parking, then out of perception range. (A) ruled out; no local defect.
Arming cause (exact): `ArmQuestTravel` armed `PathTo(Mor)` during the pre-quest settle window
(09:48:55; ActiveQuests empty → nearest in-band offerer spawner); its interruption left
`state.Path` (Mor, unfinished) + terminal `PendingLeg` stale; route-advance re-issued Mor legs
on the first idle wake after pursuit Completed (09:49:09) — prior termination opened the
single-writer, NOT preemption (36 travel attempts during pursuit all busy-rejected).
South displacement = two QUEST_TRAVEL legs (60.1 s + 35.6 s @2.5 m/s ≈ 239 m ≈ full 238 m).
Terminal reasons: Navigation-timeout (leg 1), flat-arrival Stop + already-there noop (leg 2).
Tick/budget live check: legs ticked 1:1 wall (3.7/4 s, 60.1/60 s, 35.6/36 s) — continuous
stepping while live; the sparse-wake divergence mode from §B/I was not exercised this run.

### L5. Fix + next step
Fix: NONE (rule: narrow fix only for (A); not (A)). Candidate follow-up, NOT implemented:
stale-route resume policy — route-advance re-issues an interrupted travel route's remaining
legs without any quest-decision participation (36 futile busy-rejects + full southward walk
came from one stale `Path`). Exactly-one next step: decide drop-vs-resume for interrupted
travel routes, then implement + gate-test.
