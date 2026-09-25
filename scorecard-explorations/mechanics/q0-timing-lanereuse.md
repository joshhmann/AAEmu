# Q0 timing + lane-reuse findings (read-only verification, 2026-09-18, branch develop)

## Task 1 — Q0ActorAcceptQuestPilotTests.cs confirmation

- (a) EXECUTE leg uses actor path, NOT bridge `drive accept` → **PASS**
  - `Q0ActorAcceptQuestPilotTests.cs:141-142`: builds `acceptBody` and `POST /api/actors/accept_quest`.
  - Terminal poll `PollTerminalAsync` on `/api/actors/actions/{traceId}` at `:228-246` (300 ms poll, 30 s timeout).
  - Bridge `drive` in this file is only: `charState` (`:96`), `quest wake waitMs=1000` (`:106-107`),
    `setLevel` + `teleportToNpc` (`:122-123`), `npcObjId` poll (`:124` → helper `:261-274`),
    `charPos` (`:125` → `:276-290`), `isActive` observation (`:109`, `:165` via `E2eQuestDriver.cs:420-421`).
  - No `op":"accept"` string exists in the Q0 file (header `:19-21` explicitly excludes it from EXECUTE).
- (b) Assertion checks authoritative quest-active state, not just ack → **PASS**
  - `:165` `activeAfter = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251)` (server-observed `isActive`).
  - `:167` `passed = state == "Completed" && activeAfter`; `:173-175` `Assert.True(passed, …)`.
  - Enroll leg also pins `activeBefore == False` (`:110-118`) so autonomy cannot pre-empt the claim.
- (c) No harness quest mutation outside the actor → **PASS**
  - File grep for `AddQuest|DoReport|FireEvent|RunCurrentStep|credit|TurnIn|CompleteQuest`: only header
    comments naming the engine path (`:18` `PlayerBotController.AcceptQuest → CharacterQuests.AddQuest`,
    `:24` range-gate note) and the report `callPath` (`:329`). Zero executable harness mutation calls.
  - DB helpers used: `EnsureUp`, `RestartGameServer`, `CleanupBotRows(BotAccount)` (`:77-84`) — cycle
    teardown only, pre-connect.
- (d) Failure diagnostics include char/npc ids, positions, outcome, refusal, audit → **PASS (with honest-UNAVAILABLE caveat)**
  - Accumulators `:70-73` (`charId, npcObjId, charPos, npcPos, distance, cmdOutcome, lastRefusal, audit`).
  - Populated `:159-164` (`actor_id`, `detail`, `failure`, `audit` payload or `UNAVAILABLE (no audit payload)`);
    assert text `:173-175` carries `quest/char/npc ids, char+npc pos, dist, outcome, refusal`.
  - Report writes fixture + audit JSON `:180-182` → `WriteReportAsync` `:315-354`.
  - Caveat: `ReadNpcPos` (`:292-296`) has no verified npc-pos bridge op and `ComputeDistance` (`:298-313`)
    fall back to `UNAVAILABLE` sentinels — the live report shows `npcPos=UNAVAILABLE, distance=UNAVAILABLE,
    lastRefusal=UNAVAILABLE`. Fields present, values honestly marked. `ReadTraceAsync` (`:248-259`)
    degrades to `UNAVAILABLE (ExType: msg)` instead of throwing.

## Task 2 — startup timing breakdown (CODE-ESTIMATE; report legs MEASURED)

Measured legs (quoted from `/root/aaemu-e2e-q0/logs/q0-actor-accept-pilot-report.json`):
`setupSeconds=153.2, execSeconds=0.3, wallSeconds=153.5`; legs: `enter-world 142.9s`
(`inWorld=True objId=22011`), `enroll 1.3s` (`stepped=False activeBefore=False`),
`fixture-stage 9.0s` (`lvl=10 npcObjId=44193 char=[145421,111381,1111] npc=[UNAVAILABLE…]`),
`explicit-accept 0.3s` (`cmd=Completed: quest 251 accepted (Npc/2425) … active=True`).
Note: the `enter-world` leg timer (`setupSw` from test start, `:52-100`) encloses `EnsureUp + RestartGameServer + ConnectAsync`.

| Cost bucket | Code that sets the wait | Poll / timeout / gate | MEASURED vs CODE-ESTIMATE |
|---|---|---|---|
| dotnet publish (Login+Game) | `E2eStack.cs:333-365` two `dotnet publish -c Release`; skip via `.publish-stamp` (`:344-350`, stamp `ComputePublishStamp` `:372-384`); `E2E_REBUILD=1` forces | No poll; full `dotnet publish` ×2 on cold, ~0 s on warm | UNKNOWN (report does not split; almost surely skipped or inside 142.9 s) |
| DB container + seed | `ResetDbVolume` `:165-171` (`compose down -v`); `EnsureDb` `:292-331` (`compose up -d db`, probe `SELECT COUNT(*) FROM users`, sleep 2000 ms) | 2 s poll, 180 s timeout | UNKNOWN (folded into setup; CODE-ESTIMATE tens of s cold) |
| Login readiness | `StartServers` `:503-512`: `StartServerProcess` login → `WaitTcp` login (`:583-601`) | 1 s TCP poll, 90 s timeout | UNKNOWN (folded into 142.9 s) |
| Game readiness (+ ManagerOrchestrator load) | `StartServers` `:512-516`: `WaitTcp` game 300 s + stream 300 s, `WaitBridge` 60 s (`:603-622`, `ping` 1 s poll), `WaitServerStarted` 300 s (`:633-657`, log gate `Server started!` 1 s poll); gate fires after `RunLoadAsync` (`GameService.cs:99`) + `RunInitializeAsync` (`:127`) + network bind (`:141-143`) → log `:148` | 1 s polls; 300/300/60/300 s timeouts | UNKNOWN split; bridge≠ready (`:624-632`: bridge binds ~80 s before managers load, hence the log gate) |
| Bridge readiness | `WaitBridge` `:603-622` (`{"cmd":"ping"}` → `pong`, 5 s call timeout) | 1 s poll, 60 s timeout | UNKNOWN (small once game is up) |
| WebAPI readiness | NOT in `E2eStack`; per-test `WaitForWebApiAsync` (`Q0:198-215`, `BotControlApiE2eTests.cs:216-234`): raw TCP connect to `WebApiPort`, **not** an HTTP health check | 1 s poll, 120 s timeout | UNKNOWN (small; port-open ≠ API-serving) |
| TCP auth (login→cookie→enter) | `BotNetworkSession.ConnectAsync` `:66-133`: Trion auth → `ACAuthResponse`/`ACLoginDenied`, world list, cookie, `X2EnterWorld` → response; each `ReadFrameUntil/ReadAnyOf` 20 s (`:87-88,97,104,125`) | 20 s per frame, immediate `SendPing` `:145` (30 s dead-account sweep) | UNKNOWN split; inside 142.9 s |
| char create/select/enter-world | `:148-192`: list → create-if-absent → select → `SCCharacterState` (20 s) → 1 s settle drain (`:169-174`) → spawn → `SCUnitState` (20 s) → `CSNotifyInGame` ×2; stream join fire-and-forget (`:195-210`); keepalive ping 8 s (`:286-296`) | 20 s per gate, 100 ms×10 settle | UNKNOWN split; inside 142.9 s |
| fixture staging | enroll `quest wake waitMs=1000` (`:106-107`) + `IsQuestActive` (`E2eQuestDriver.cs:420-421`); `setLevel`/`teleportToNpc` (`:122-123`); `PollNpcObjId` 30 s (`:261-274`, 1 s poll) | wake 1 s; npc poll 1 s × 30 s | MEASURED enroll 1.3 s, fixture-stage 9.0 s, explicit-accept 0.3 s (terminal poll 30 s / 300 ms, `:228-246`) |

Q0 pays one extra full game reboot: `EnsureUp` (`:77`) then `RestartGameServer` (`:78` → `:674-686`,
same 300/300/60/300 gates on `game-restart.log`). Cold Q0 ≈ full boot + game reboot + connect.

## Task 3 — lane reuse: ONE boot, Q0→Move→Interact→Cast with fresh characters

Verdict: **safe for happy-path actor legs with per-case fresh accounts + per-case cleanup, provided no
per-case restart, no seeded defect, and sequential execution.** `E2eStack` teardown primitives:
- `ResetDbVolume` (`:165-171`, private, only via `EnsureUp` `:144`) — full `compose down -v` + reseed.
  Lane reuse must call `EnsureUp` ONCE, never per case.
- `CleanupBotRows(accounts)` (`:920-963`) — deletes `quests`/`completed_quests`/`characters`/`users` for
  NAMED accounts only (bound params, `:967-977`). Correct per-case isolation primitive.
- `CleanupOwnedRows(rows)` (`:862-906`) — even narrower: by-id deletes (`characters`, `quests`,
  `completed_quests`, `playerbot_metadata` guarded) + `accounts`/`users` only when `AccountCreated`.
  Preferred when snapshotting (`SnapshotOwnedRows` `:825-840`, `FindNewOwnedRows` `:846-856`).
- `KillStaleServers` (`:198-227`) — kills `dotnet AAEmu.{Login,Game}.dll` only when `/proc cwd` is under
  this lane's `E2eRoot` (path-segment match, `:215-216`); never prod (cwd differs). Safe.
- `StopAll` (`:711-726`) — kills only held PID handles, clears `_stackUp` + boot times. Lane teardown.
- `StackActuallyUp` (`:177-192`) — TCP login + game + bridge `ping`; adopt-vs-reboot check in `EnsureUp` (`:114-153`).
- All e2e tests share `[Collection("e2e")]` (58 classes) → sequential within the assembly; a mid-lane
  `RestartGameServer` kills siblings (`E2eStack.cs:121-125` warns) — so the shared lane must not restart per case.

Exact env (from `E2eStack.cs:25-66,155-158`): `E2E_ROOT` (default `/root/aaemu-e2e`; Q0 lane
`/root/aaemu-e2e-q0`), `E2E_LOGIN_PORT`/`1237`, `E2E_GAME_PORT`/`1239`, `E2E_STREAM_PORT`/`1250`,
`E2E_BRIDGE_PORT`/`1260`, `E2E_INTERNAL_PORT`/`1234`, `E2E_WEBAPI_PORT`/`1280`, `E2E_DB_PORT`/`3306`,
`E2E_GAME_HOST` (default `192.168.0.165`), `COMPOSE_PROJECT_NAME` (or `E2E_COMPOSE_PROJECT`, else `e2e` or
root basename), plus `E2E_REBUILD`, `E2E_GROWTH_RATE`, `AAEMU_BOT_CTRL`/`AAEMU_BOT_CTRL_TOKEN` (set before
boot; Q0 `:63-64`), `AAEMU_QUEST_BOOTSTRAP_ENABLED`/`AAEMU_PRESENCE_HUNT*` (forwarded `:548-559`).
Isolated-lane pattern (B1/B2/B4 soaks): own root + shifted ports + own compose project + `GameHost 127.0.0.1`.
Prod-port refusal lives in TESTS not the stack (e.g. `B1HousingRestartE2eTests.cs:149-153`,
`B2FarmRestartE2eTests.cs:237-240` refuse root `/root/aaemu-e2e`, port `3306`, host `.165`, project `e2e`);
Q0 itself has NO such guard — add one if the lane hardens.

Must STAY cold-start (keep `EnsureUp` wipe / `RestartGameServer` / `RestoreCanonicalSqlite` paths):
restart-persistence suites (`AuctionHouseRestart`, `B1Housing`, `B2Farm`, `B4*`, `EconomyDayCycle`,
`DominionRestartPersistence` — kill-PID + byte-equal-row proofs), seeded-defect/seed-integrity rigs
(`ApplySeededDefect` `:766-777` / `RestoreCanonicalSqlite` `:753-758`), boot-gating/provisioning tests
(presence-demo log-gate), anything asserting fresh-DB (completed-quest leakage, `EnsureUp:134-146` cycle
isolation). Also keep the per-case `activeBefore==False` gate (`:110-118`): bridge `wake` autonomy on a
warm lane could pre-empt an explicit leg. Manager load is warmambre — fine for actor legs, INVALID for
load/persistence claims.
