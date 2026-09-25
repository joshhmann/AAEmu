# G3 Stable-Start Gate — quest-travel route stilled, staged 3512 accept attempted (2026-09-20, branch develop)

Lane: adopted-only `E2E_ROOT=/root/aaemu-e2e-q0` (ports 2237/2239/2250/2260/2234/2280/3311,
`COMPOSE_PROJECT_NAME=q0pilot`, token `e2e-q0-pilot-token`). No restart/rebuild (lane warm
throughout). Budget consumed exactly as ordered: one unmodified baseline (charId 17) + one
test-file-only fix + one rerun (charId 18). Game bootstrap flag ON in the game process
(`AAEMU_QUEST_BOOTSTRAP_ENABLED=1`, pid 189877). Fixture: quest 251 (L2), giver 3512, level 10.

Read-first context: `G3AutonomousQuestAccept3512Tests.cs` (fixed harness + this gate's blocks),
`quest-discovery-resolution.md` (F-section: enroll-route drift decayed the START precondition),
`nearby-quest-contracts.md` (§1 state machine; travel fallback executor-owned, `ArmQuestTravel`
never re-arms over a live route, `BotRoamStepExecutor.cs:1271-1294`).

## A. Pre-stage route state

Enroll wake (`waitMs=1000`) ran one quest tick at spawn and armed the enroll route. Read-only
`quest observe` immediately after enroll identified it live:

- `questTravelReason = "walking to quest target at (15562,15354) from (15578,15382)"`
  (3597's spawner; 32.2 m from the Solzreed spawn `(15578.0,15382.1)`)
- `questTravelTarget = (15562,15354)`; `QuestDecideDetail = "DECIDE: no legal quest proposal:
  no proposals [swept=0 targets= offerings=0 inBand=0 legal=0 firstZero=sweep-empty raw=0
  trunc=0 actor=(15578.0,15382.1,126.5) zone=179 region=249072 outcomes=]"`
- Server log corroborates the single enroll tick
  (`cycle=quest-18-639254719001638343`, `botSameChar=true`, raw=0 at spawn).

## B. Route-clear result

Mechanism (existing canonical lifecycle seam, code-verified before use): `POST /api/actors/stop`
→ `BotActionController.Stop` (`BotActionController.cs:148-159`) → `BotActionCommandQueue`
(`TryResolveBot` resolves networked bots by character name, `:792-819`) → executes on
`_stepExecutor.GetOrCreateActor` (`BotActionCommandQueue.cs:433`) — the SAME executor live actor
the route pump drives — → `GameplayActor.Stop()` (interrupts the live Move + `BroadcastStop`,
`:497-520`). No new API, no direct state mutation.

Result: **seam UNAVAILABLE on the adopted lane** — verbatim
`POST /api/actors/stop → 404 {"Message":"Bot control API is disabled","StackTrace":null}`.
The game process env has `AAEMU_BOT_CTRL_TOKEN` but no `AAEMU_BOT_CTRL`, and lane
`Config.Local.json` carries no `Bots.EnableBotControl`; enabling either requires a game restart,
forbidden on a live lane. Recorded as the SETUP-relevant finding, not worked around.

Fallback (observational, zero mutation): settle-to-arrival — read-only `charPos` at 2 s cadence,
standstill = consecutive reads < 0.5 m flat, 60 s bound (disclosed), then a 5 s no-wake window
(re-arm ≥ 1.0 m would have meant `SETUP/route-rearms` and stopped the gate). Settle-first was
required because the live Move leg ticks in the world loop with no scheduler wakes, and
`teleportToNpc` does NOT cancel it (baseline §C). Result: **settled=True, rearmed=False** —
the route completed naturally at the ~3597 spawner and self-cadence quiesced with no live route.

## C. Teleport / staging proof

`teleportToNpc(3512)` response verbatim: `{"x":15655.91,"y":15172.51,"z":121.23622,"zoneId":179,
"spawnerId":3409}` — the CORRECT spawner (3409 @ 15655.91/15172.51), matching locked facts.

- charPos IMMEDIATE (≤1 s): `[15655.9,15172.5,121.2]` — exact landing.
- charPos +5 s: `[15655.9,15172.5,121.2]` — IDENTICAL, drift 0.0 m (route held still).
- Live giver 3512: objId **28952** (30 s poll resolved on first reads, not by timeout).
- Contrast baseline (charId 17, unsettled route): +5 s drifted 13.4 m to `[15649.9,15184.3]`
  (≈2.7 m/s, bearing-exact toward 3597: `(-6.1,+11.8)` vs target `(-93.9,+181.5)`, normalized
  `(-0.46,0.89)` both) — the decay the F-section predicted, now eliminated.

## D. START-range proof

Immediately before START (read-only, after the giver poll): preStart=`[15655.9,15172.5,121.2]`,
flat distance to the live-resolved spawner coords = **0.014 m** (REQUIRE ≤ 25 m → PASS),
drift-since-teleport = 0.0 m, giver objId 28952 live. Basis disclosed: NPCs spawn at their
spawner position and the spawner coords are the op's own live resolution. **Valid stable START.**
(Baseline START: 13.2 m flat — inside 25 m but decayed in transit and unwitnessed.)

## E. Per-bot wake / behavior proof

START = one bounded scheduler wake (`waitMs=30000`), then observe ONLY (no gameplay commands,
no mutation after START). Posture at START: snap=actor=`[15655.9,15172.5]`, questLegActive=False,
money=0, wakeSeq=0, traceWakeSeq=0, maxTraceWakeSeq=0.

Fresh-signal proof (the persisting `questTravelReason` is enroll residue through stop/settle and
is NEVER accepted as START wake proof):

- `decideChanged` = **False** — `QuestDecideDetail` post-START is byte-identical to pre-START
  (still the enroll tick: `actor=(15578.0,15382.1)` spawn pose; a START tick would overwrite with
  a new CycleId + staged pose).
- `actionsGrew` = False (questActionCount 0 → 0); `seqGrew` = False; `legLit` = False.
- Server log: exactly ONE char-18 quest tick (the enroll spawn tick above); **no staged tick exists**.
- Conclusion: **no quest tick ran for OUR bot at START**. The scheduler step the wake waited for
  (`stepped=True`, global `TotalStepsRun`) is unattributed — the observe snapshot is position-only.
- Contrast: the legacy per-bot proof reports PROVEN on the same payload via the stale travel
  reason. That signal is enroll residue and must not prove a START quest tick (debt J: the arbiter
  flips quest.progress → recovery.rest once travel is armed while the stale reason persists).

## F. Funnel (gated START, valid stable START at 0.0 m)

No staged sweep ran, so every production predicate below is UNOBSERVED rather than zero:

- raw sweep count at staged pos: NO SWEEP RAN (decide detail stale; no staged server tick).
- 3512 present in sweep: UNOBSERVED. DiscoverQuests calls: 0/0/0. Offerings: 0.
- 251 present: UNOBSERVED (offerings list never produced).
- band/active/eligibility survival: UNOBSERVED (`IsDiscoverable` never evaluated live at START;
  data-level: 251/L2 ∈ [1,9], Start-383 Level≥1 passes at level 10, fresh char clears supply).
- legal: 0. Selected: none. Dispatched: none.
- First-zero: **wake-unproven** — no production predicate evaluated at START. (The only observed
  `firstZero=sweep-empty` belongs to the enroll tick at spawn, 223 m from 3512.)

## G. 251 decision

No decision ran at START: accepts=0, lastAction=`-`/lastResult=`-`, authoritative active quests
`activeCount=0`, quest 251 NOT active. Nothing accepted, nothing rejected — the leg never engaged.

## H. Actor lifecycle + post-state

No quest-action audit rows (0 → 0); no Move/Stop/discovery dispatched by the harness at or after
START; bot position unchanged through START (standstill holds post-wake). Post-state: char
`[15655.9,15172.5,121.2]`, level 10 (fixture), money 0, activeQuests []. Cleanup UNAVAILABLE as
fixed-harness-documented (bridge `deactivate` refused the networked char,
`InvalidOperationException`; TCP disposed, registry entry left in place).

## I. Verdict

**UNKNOWN at HARNESS/wake-unproven.** Valid stable START (0.0 m, giver live, route stilled) +
proven per-bot wake absence (four fresh signals + server log) ⇒ no production verdict is
reachable; FAIL-BEHAVIOR would require a proven wake plus a named failing production predicate,
and none was evaluated. The baseline's `FAIL-BEHAVIOR/DISCOVERY` is reinterpreted: its wake proof
likewise rested on the stale enroll reason (START 13.2 m, discover 0/0/0, decision detail absent),
so it never observed staged discovery either.

## J. Files changed

- `AAEmu.IntegrationTests/E2e/G3AutonomousQuestAccept3512Tests.cs` — ONLY change (test-file-only
  fix): pre-stage route identify + stop-seam attempt + settle poll (`route-clear` leg), pre-START
  position + ≤25 m range gate (`start-range` leg), fresh-signal START tick proof, derived 4-class
  gate verdict (`stable-start-gate` leg), gate fields in the report fixture, `Token`/`WebApiBase`/
  `NewClient`/`GetStr`/`FlatDist` helpers. Zero game/behavior/nav/perception/combat/GOAP/
  scheduler/API/bridge edits. (Remainder of the worktree diff is pre-existing stacked lanes, not
  this gate.)
- Evidence JSON (new path; the `g3-quest-autonomy-3512-report.json` report was NOT overwritten):
  `$E2E_ROOT/logs/g3-stable-start-report.json`.
- This doc. Provenance: source rev `6c3efe25ecb68998544eb3f75699bc1674c7eef1`, runtime sqlite md5
  `78b3bdbf038db3b927056106efdf91af` (unchanged). Timings: setup 28.5 s / exec 0.2 s / wall 28.7 s.

## K. Exactly-one next action

Prove-or-fix START-wake quest-leg engagement for the staged bot: capture the arbiter's yielded
activity (`quest.*` vs `recovery.rest`) immediately around START with read-only observes; if
`quest.progress` is withheld while the stale enroll travel reason persists, that withholding —
not discovery — is the next failing transition to gate. No navigation, no kill-quest, no
game-behavior change until the wake is proven per-bot.
