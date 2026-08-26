# B5 — PLAYER_MODE / TEST_MODE Leakage Audit (G3-B5)

*2026-08-26 · worktree `.worktrees/b5lib` @ `feat/b5-scenario-library` · method: grep consumer census +
call-graph trace (graphify-out/GRAPH_REPORT.md, commit 2b4b99c0 — 23626 nodes/57326 edges) + negative
unit tests (`AAEmu.UnitTests/Game/Core/Managers/Bots/B5SeamLeakageAuditTests.cs`, 6 tests)*

**Verdict summary**

| Seam | Verdict | Negative test(s) |
|---|---|---|
| (a) Bridge metrics surface (`BotDriveBridge` cmd `metrics` → `CollectGateMetrics`) | **PROVEN-UNREACHABLE** (gated + loopback + private dispatch; surface pinned by reflection test) | `BridgeDarkByDefault_PlayerSessionConfigNeverOpensListener`, `BridgeControlSurface_DispatchAndMetricsArePrivate_NoGamePathEntryPoints` |
| (b) Headless-roam `BroadcastMovement` opt-out | **PROVEN-UNREACHABLE** from player sessions (single writer = roam executor; observer stream preserved under opt-out) | `PlayerVisibleSession_DefaultFlagTrue_PerApplyMoveStillBroadcasts`, `OptOutConfinedToRoamExecutor_ObserverMovementStreamStillFlows`, `NonRoamContractActions_NeverTouchOptOutFlag` |
| (c) Rig seed hooks (`GameplayActorTestRig.Seed*`) | **PROVEN-UNREACHABLE** (compile-time: rig type lives only in the unit-test assembly; zero shipping-assembly references) | `RigSeedHooks_ConfinedToTestAssemblies_ShippingAssembliesUnreferenced` |

No seam required a source fix; no REACHABLE-FIXED / REACHABLE-OPEN findings.

---

## (a) Bridge metrics surface — PROVEN-UNREACHABLE

**What it is.** `BotDriveBridge` (`AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs`) is a loopback-only
JSON/TCP test-control channel: cmds `ping / stats / metrics / transfers / drive / save / scenario /
provision / deactivate / auction / seedDormant`. The metrics op lands in `CollectGateMetrics()`
(BotDriveBridge.cs:279), aggregating TickManager p50/p95, region-tick budgets, PlayerBotScheduler wake
latency, PopulationDirector and SaveManager autosave metrics.

**Gate.** Disabled unless runtime `Config.Local.json` sets `"Bots": {"EnableE2EBridge": true}` or env
`E2E_BRIDGE_ENABLED=1|true`; port `"Bots"."E2EBridgePort"` / `E2E_BRIDGE_PORT` (default 1260), bound to
127.0.0.1 only (`TryStart()` → `ReadConfig()` BotDriveBridge.cs:88–166). Prod config never sets it.
The only activation path is `[ModuleInitializer] BotE2EBridgeBootstrap.Init`
(`BotE2EBridgeBootstrap.cs:25-29`), which polls for the DI container and no-ops when disabled.

**Consumer census (grep, whole tree).** `BotDriveBridge.Instance` is referenced ONLY from
`BotE2EBridgeBootstrap.cs:27`. No packet handler, manager, scheduler, controller, or autonomous-decision
surface in `AAEmu.Game` consults bridge state; the bridge executes ops ON bots on demand
(PlayerBotController surfaces), never the reverse — autonomy cannot read it. `TeleportWithRegionSync`
(internal, BotDriveBridge.cs:72) is called only within BotDriveBridge.cs itself (:666, :829, :1077,
:1241); its region-integrity contract is separately pinned by `BotDriveBridgeTeleportRegionTests`.

**Negative proof.**
- `BridgeDarkByDefault_PlayerSessionConfigNeverOpensListener`: with the env gate closed, `IsRunning`
  stays false after `TryStart()` AND nothing accepts connections on 127.0.0.1:1260. Since
  `ServeClientAsync` → `HandleCommand` → `CollectGateMetrics` runs only for clients accepted by that
  listener, a dark listener ⇒ the metrics surface has NO reachable path from any session shape.
- `BridgeControlSurface_DispatchAndMetricsArePrivate_NoGamePathEntryPoints`: reflection pins the public
  API of `BotDriveBridge` to exactly `TryStart` (+ read-only properties); `HandleCommand`,
  `CollectGateMetrics`, `AcceptLoopAsync`, `ReadConfig` are non-public — a future "public helper" would
  fail this test instead of silently opening a game-path entry point around the gate.

**Residual risk (accepted, documented):** an operator who flips `EnableE2EBridge=true` on a live server
opens a loopback control channel by design; unreachability here is from *player-visible sessions and
autonomy*, not from misconfiguration. Config-level protection (prod never ships the flag) is cited at
`AAEmu.IntegrationTests/E2e/E2eStack.cs:395` — the E2E stack template is the only known enabler.

## (b) Headless-roam BroadcastMovement opt-out — PROVEN-UNREACHABLE

**What it is.** Soak fix 615a645c9: `GameplayActor.BroadcastMovement` (GameplayActor.cs:105, default
`true`). When false, `ApplyCharacterMove` applies movement state without constructing the per-apply
`SCOneUnitMovementPacket` (GameplayActor.cs:3373–3397); visibility is preserved because
`BotRoamStepExecutor.StepAsync` does its own throttled 4–6 Hz broadcast (BotRoamStepExecutor.cs:249–260).

**Call-graph trace.** The flag has exactly ONE writer:
`BotRoamStepExecutor.StepAsync` (BotRoamStepExecutor.cs:166–168, enforced at every state creation site),
and exactly ONE reader: `GameplayActor.ApplyCharacterMove` (GameplayActor.cs:3378). Full-tree grep found
no other reference in shipping or test code. `BotRoamStepExecutor` is reached only through the bot
presence/scheduler stack (`PresenceRoamActivityModule`, `IdleActivityModule`, `BotScheduleBehavior`,
`BotScheduleService`, `BotPresenceBootstrap`, `BotPresenceCoordinator`, `BotAdminService`,
`BotActionCommandQueue`) — all of which drive PlayerBot characters. Human players never get a
`GameplayActor`: `new GameplayActor(` occurs in shipping code only inside `Core/Managers/Bots/*`
scenario/executor code (10 sites, all bot-side); player clients move via CSMoveUnitPacket handlers that
call `VehicleMovementModel.ApplyUnitMove` directly and never touch the flag.

**Negative proof.**
- `PlayerVisibleSession_DefaultFlagTrue_PerApplyMoveStillBroadcasts`: a fresh rig actor keeps the flag
  TRUE and a direct contract Move emits real `SCOneUnitMovementPacket`s (packet-capture connection) —
  the player-visible default path is untouched.
- `OptOutConfinedToRoamExecutor_ObserverMovementStreamStillFlows`: stepping a REAL GameplayActor through
  `BotRoamStepExecutor` flips the flag off AND the capture stream still receives movement packets —
  proving the opt-out suppresses only the redundant per-apply packet while observers (player-visible
  sessions near the bot) keep receiving updates. This is the gate ROADMAP requires before ANY default-ON
  flip of proximity fidelity/true dormancy (ROADMAP.md:2476–2478).
- `NonRoamContractActions_NeverTouchOptOutFlag`: scenario-style direct actions (Move leg, SetPosition)
  leave the flag true — nothing outside the executor writes it.

**Autonomy:** the opt-out affects only how a headless-roaming BOT's own movement is replicated; no
decision path reads it (reader census above). Zero-bot worlds emit zero bot-originated packets either
way.

## (c) Rig seed hooks — PROVEN-UNREACHABLE

**What they are.** `GameplayActorTestRig` (`AAEmu.UnitTests/Game/Core/Managers/Bots/
GameplayActorTestRig.cs:86`) and its seed hooks — `Seed()` (one-shot singleton seeding + per-call
SusManager/ModelManager healing via reflection into `Singleton<T>.s_instance`),
`SeedTradeItemTemplate` / `RegisterPlainItemTemplate` / `SeedMerchantPack` (item-template dictionary
mutations), `EnsureIncrementingItemIds`, world-id allocator — mutate process-wide engine singletons and
template dictionaries that would be catastrophic if reachable from a live server or consulted by an
autonomous controller.

**Consumer census (grep, whole tree).** All ~65 referencing files are under `AAEmu.UnitTests/**`. The
only two matches in `AAEmu.Game` are doc-comment mentions
(`M53CoreSurfaceExitScenario.cs:32`, `AuctionHouseScenario.cs:111`) — no code reference. Nothing in
`AAEmu.IntegrationTests` seeds via this rig (it uses the live bridge/E2eStack instead).

**Negative proof.** `RigSeedHooks_ConfinedToTestAssemblies_ShippingAssembliesUnreferenced` asserts over
the loaded assembly reference graph that `AAEmu.Game`, `AAEmu.Commons`, and `AAEmu.Login` reference
neither `AAEmu.UnitTests` nor `AAEmu.IntegrationTests` — i.e., the isolation is a compile-time fact, not
a naming convention. A shipping assembly cannot even name the rig, so no player-session path or
autonomous decision can consult a seed hook.

---

## Method notes & caveats

- Call-graph aid: `/root/aaemu-dev/graphify-out/GRAPH_REPORT.md` was built from commit `2b4b99c0`
  (stale vs audit HEAD `43ebb9f9d`+edits); it was used for community navigation only — every seam claim
  above is grounded in fresh whole-tree greps run in this worktree (2026-08-26).
- Negative tests are deterministic unit tests in `AAEmu.UnitTests` (TUnit); the bridge-dark test also
  probes the default TCP port so a half-open listener cannot hide behind `IsRunning`.
- Test-environment prerequisite recorded: `Data/compact.sqlite3` (read-only, md5-checked copy of the
  canonical game DB) must exist under the test bin `Data/` for rigs that call `FormulaManager.Load()`.
  The file is opened with `Mode=ReadOnly` (`AAEmu.Game/Utils/DB/SQLite.cs:19`) — compact.sqlite3 is never
  written by tests.
- Development-loop rule 5 status update (ROADMAP.md:1369–1372): the three seams are now enforced by
  negative tests rather than inspection-only assertions.
