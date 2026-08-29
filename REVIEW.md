# Workstream A — Review of the last 12 commits (e9ace7f22..14388b702)

Evidence basis: `git show` full diffs, targeted greps, and the on-disk gate logs
`/tmp/aaemu-gate-tests.*.log` (timestamps 2026-08-29 06:07–09:49). No build/suite
was run by this review (delegated elsewhere).

## Per-commit verdicts (oldest first)

| Commit | Verdict |
|---|---|
| e9ace7f22 | OK |
| dae7fb05e | OK |
| 49f0aee07 | OK |
| 970d6a557 | OK |
| 69861b73c | ISSUES (transient regressions, fixed by fa56915e6/ac8953813 in range) |
| 7dde587fc | OK (docs) |
| 0492b7199 | ISSUES (fixed by ac8953813) |
| fa56915e6 | OK |
| ac8953813 | OK |
| 13b8bedb8 | OK (transient gate flakes pre-dating it, see note) |
| 63436d0a9 | ISSUES (docs: stale counts) |
| 14388b702 | ISSUES (docs: wrong skip identity, stale blocks) |

---

### 1. e9ace7f22 — PB-002 interaction objectives — **OK**

- `InteractionEffect.Apply` fallback **resolves `World.Interactions.Use`**: the class exists at
  `AAEmu.Game.Models.Game.World.Interactions.Use` (`AAEmu.Game/Models/Game/World/Interactions/Use.cs:10`);
  `"AAEmu.Game.Models.Game.World.Interactions." + "Use"` is byte-exact, and
  `typeof(InteractionEffect).Assembly` **is** AAEmu.Game, so the fallback
  (`InteractionEffect.cs:24-25`) always resolves. The first `Type.GetType` branch alone would
  usually already resolve (same assembly); the fallback is correct belt-and-braces.
- `SphereQuestManager` null guards are **behavior-preserving** when the dictionary IS set:
  `_sphereQuests` is initialized in `Load()` under `if (_sphereQuests == null)`
  (`SphereQuestManager.cs:40-41`); the two guards (`:235` — `?.GetValueOrDefault`, `:348` — early
  `return res`) only change the null-dict path. When set, `GetQuestSpheres` and
  `GetSpheresForQuest` behave identically. Callers already null-tolerant
  (`LevelingLoopScenario.cs:761/791` use `?.`).
- `CreateDetached`/`CreateCore` (`DoodadManager.cs:2788-2816`) is `internal`, skips
  `ParentWorld` pinning; `Create` keeps prior behavior. Nit (minor): `SpawnDoodadFromTemplate`
  (`HeadlessSession.cs:536-572`) pins `_parentWorld`/`_instanceId` via reflection
  (`BindingFlags.NonPublic`) — brittle, but test-only path and commented.
- `SpawnEffect.cs:37` null-chain (`caster?.ParentWorld?..`) — trivial hardening, no issue.

### 2. dae7fb05e — TickManager test stabilization — **OK**

- `Subscriber_RecordsPerSubscriberDuration_WithName` now gates on a `TaskCompletionSource`
  (`TickManagerTests.cs:70-74`): `Task.WhenAny(asyncDone.Task, Task.Delay(2000))` then a 50 ms
  settle for the async wrapper's finally block. Deterministic vs the old `Thread.Sleep(10)` +
  fixed 100 ms delay.
- The claim that this test is the gate's *skip* is **wrong** (see docs commit below): it carries no
  `[Explicit]`/`Skip` marker and **passes** in the 09:36–09:49 gate logs (e.g. it is absent from
  the failed/skipped lines of `LWS5Hw.log`, total 2531, 0 failed, 1 skipped).

### 3. 49f0aee07 — Sphere/Craft/Cinema legs + PlayCinema — **OK** (nits)

- `QuestActObjSphere` has `SphereId` (`QuestActObjSphere.cs:13`),
  `NpcId` (`:14`), and `ParentComponent` (inherited, `QuestActTemplate.cs:11`). All three used by
  `SphereLeg` exist.
- **SphereLeg seam**: `LevelingLoopScenario.cs:788-794` calls
  `QuestManager.Instance.DoOnEnterSphereEvents(actor.Character, sphereQuests[0], pos)` directly
  after `actor.Tick(500ms)`. This bypasses `SphereQuestTrigger.Tick`'s edge detection
  (`!oldInside && newInside`, `SphereQuest.cs:130-138`) and `CanTriggerSphere` gate
  (`SphereQuest.cs:117-121`), and the manager's per-trigger tick only runs under
  `Region.HasPlayerActivity()` (`SphereQuestManager.cs:160-162`, absent headless). The bypass is
  **documented** as a "Rig simulation seam" (`LevelingLoopScenario.cs:788-789`). The `oldPosition`
  argument is unused by `QuestActObjSphere.OnEnterSphere` (`QuestActObjSphere.cs:50-59`), so the
  970d6a557 change to pass the *current* position is harmless. Nit (minor): fidelity — no
  `CanTriggerSphere` unit-req check on the seam path.
- **CraftLeg cap exists at HEAD**: `maxAttempts = Math.Max(10, craftAct.Count * 3)` with a counted
  loop and a meaningful fail reason
  (`LevelingLoopScenario.cs:851,853,874-876`; added by fa56915e6). Without it (the 49f0aee07
  version) the loop was unbounded — fixed in range.
- **CinemaLeg**: objective check before (`GetObjective >= 1 → return`) and after PlayCinema
  (fail "played but objective remains 0"), plus `HasQuestCompleted` short-circuit added in
  970d6a557 (`LevelingLoopScenario.cs:883-898`). Credit path is engine-real:
  `QuestActObjCinema.OnCinemaEnded` requires `player.CurrentlyPlayingCinemaId == CinemaId`
  (`QuestActObjCinema.cs:73`), which only works because 970d6a557 sets
  `CurrentlyPlayingCinemaId` before firing the events (`GameplayActor.cs:1005`).
- Nit: `DriveRequest`'s mid-loop `CraftEffect.Apply` (`LevelingLoopScenario.cs:1265-1272`) is an
  inline simulation tick for headless craft — same documented-seam category.

### 4. 970d6a557 — self-quest discovery + CurrentlyPlayingCinemaId — **OK**

- `GameplayActor.DiscoverSelfQuests` exists (`GameplayActor.cs:903`) and returns
  `ActorRequest` carrying `record QuestSelfDiscoveryResult(IReadOnlyList<QuestOffering>)`
  (`ActorRequest.cs:217`); `LevelingLoopScenario.cs:421-427` consumes it via
  `selfRequest.Result is QuestSelfDiscoveryResult selfFound`. `ActorActionType.DiscoverSelfQuests`
  trace present in regenerated evidence.
- `Character.CurrentlyPlayingCinemaId` exists (`Character.cs:292`) and is consumed by
  `QuestActObjCinema` and `CSStartedCinemaPacket`/`CSCompletedCinemaPacket`.

### 5. dae7fb05e / 69861b73c / fa56915e6 — Mail COD + TickManager — **ISSUES (transient) / OK**

Task-specific check — `git diff 69861b73c..fa56915e6` on `CharacterMails.cs` shows:
- **aaPoint flag `false`: PRESENT.** `fa56915e6` flips
  `SCAttachmentTakenPacket(mailId, false, true, …)` → `(mailId, false, false, …)`
  (`CharacterMails.cs:318`). Parameter 3 **is** `aaPoint` (`SCAttachmentTakenPacket.cs:7-12`).
  In 69861b73c the flag was wrongly `true`; the money packet is separately emitted with
  `money:true` (`CharacterMails.cs:299`). **Finding (minor, fixed): aaPoint was `true` in 69861b73c
  — a wire regression — corrected in fa56915e6.**
- **Per-item split: NOT present in that diff.** 69861b73c *removed* the ZeromusXYZ per-item loop
  (replaced with one batched packet) and fa56915e6 did not restore it; the split only returns in
  **ac8953813** (`CharacterMails.cs:312-322`, restored `foreach` with one-item lists and the
  ZeromusXYZ comment). **Finding (minor, fixed in range): between 69861b73c and ac8953813
  (exclusive) the per-item split was missing — a transitory regression; final HEAD state is
  correct and covered by `GetAttached_MultiItem_EmitsOneAttachmentTakenPacketPerItem`
  (`MailCodLifecycleTests.cs:232-268`, asserts exactly 2 frames).**
- COD dispatch: insufficient-funds refusal (`CharacterMails.cs:186-193`, `MailNotEnoughMoney` +
  early `return false`), payment deduction + payment-mail dispatch to `Header.SenderId`
  (`CharacterMails.cs:260-289`, `Self.SubtractMoney(Inventory, codCost)`, `Extra=0`,
  `MailManager.Instance.Send(paymentMail)`).
- **Sent-tab DeleteMail actually removes from `_allPlayerMails`:** `DeleteMail(id, isSent=true)`
  emits `SCMailDeletedPacket` **and** calls `MailManager.Instance.DeleteMail(id)`
  (`CharacterMails.cs:358-363` → `MailManager.cs:99` removes from `_allPlayerMails` + releases id).
  Covered by `DeleteMail_SentTab_SenderOwns_EmitsDeletedPacket`
  (`MailCodLifecycleTests.cs:271-299`, asserts `ContainsKey(300)` false).
- Test coverage check: 5 tests at HEAD (4 at 69861b73c) — insufficient-funds, sufficient+
  dispatch (receiver 600, payment mail 400), partial-bag head/remainder split, one-packet-per-item,
  sent-tab delete. **Nit**: no test decodes the money/aaPoint flag bits from the captured frames
  (they only count opcodes) — the aaPoint fix is assertion-free.
- `NameManager` O(1): `StringComparer.OrdinalIgnoreCase` on the dictionary (`NameManager.cs:29,
  132`) with keys stored normalized (`AddCharacter` → `NormalizeName`, `:162-165`) — consistent;
  the removal of the O(n) fallback scan in fa56915e6 does not lose lookups for already-registered
  names.

### 6. 0492b7199 — PVP WAR-HONOR rig tests — **ISSUES** (fixed by ac8953813)

- **The `IsDead` compile failure: confirmed real, fixed in ac8953813.** `Unit.IsDead` is a
  read-only computed property (`get => Hp <= 0`, `Unit.cs:909-914`). 0492b7199's diff contains
  eleven `victim.Character.IsDead = false;` assignments (e.g. at the escalation stage boundaries)
  — that file would not compile. ac8953813 deletes **every** such assignment and re-arms via
  `victim.Character.Hp = victim.Character.MaxHp` (`PvpFlaggingRigTests.cs:365-405,558-573`),
  which flips `IsDead` implicitly. **Kill path is the real engine path**: `Kill()` sets `Hp = 0`
  then `DoDie(killer.Character, KillReason.Damage)` (`PvpFlaggingRigTests.cs:200-209`).
- **Second latent bug in 0492b7199 (also fixed in ac8953813):** thresholds `NumKills[i] = 2` for
  every stage violate `AddZoneKill`'s cumulative `>` semantics (`ZoneConflict.cs:72-96`, e.g.
  `Tension && KillCount > NumKills[0]` → with 2/2/2/2/2, kill #2 gives `2 > 2 = false` → no
  escalation). ac8953813 corrects to `[1,2,3,4,5]` (`PvpFlaggingRigTests.cs:349-352`). The
  respawn expectations were also wrong in 0492b7199 (15/30/45/60 vs the engine table
  `[15,30,60,90,…]`, `CharacterCombat.cs:31`); corrected to 15/30/60/90 and, separately,
  `DiedInPvpWarZone = false` reset added for the Peace-stage assertion
  (`PvpFlaggingRigTests.cs:403-407,557-578`). Note the transient gate failures at 09:46/09:48
  (`gXN2f3.log`, `Gt8q9J.log`) were mid-edit stale binaries casting `WorldManager._characters`
  to a `ulong` dictionary (old `PvpFlaggingRigTests.cs:713`) — that cast never existed in
  committed code; the 09:49 run (`LWS5Hw.log`, 2531 total / 0 failed) is clean.

### 7. 13b8bedb8 — WorldManager registration isolation — **OK**

- `WorldManager._characters` is `ConcurrentDictionary<uint, Character>` (`WorldManager.cs:91`) and
  the new registration code casts exactly to that (`PvpFlaggingRigTests.cs:712-714`); disposal is
  conditional `ReferenceEquals` removal (`:740-746`). Parallel-safe, no leak past the `using`.

### 8. Docs commits (63436d0a9, 14388b702) — **ISSUES: STALE CLAIMS in HANDOFF-GEMINI.md**

Gate-count claims themselves are grounded: **2531 total / 2530 passed / 0 failed / 1 skipped**
matches `/tmp/aaemu-gate-tests.LWS5Hw.log` (2026-08-29 09:49, before 13b8bedb8 which added no
tests). But:

- **[major] HANDOFF-GEMINI.md:7 — wrong skip identity.** "The sole skip is
  `Subscriber_RecordsPerSubscriberDuration_WithName` (known timer-skew skip)". The actual sole
  skip in every recent gate log (lXaPw0 09:36, cs0Cew 09:44, LWS5Hw 09:49) is
  `Provision_Activate_Persist_Deactivate_RoundTrip` (live-rig gate, requires `AAEMU_LIVE_RIG=1` +
  `AAEMU_E2E_DB_PASSWORD`). The tick test is a *regular passing* test since dae7fb05e (no skip
  marker anywhere in `TickManagerTests.cs`); the old 06:11 log (`4KGWS4.log`) showing it failing
  predates the dae7fb05e fix (06:13).
- **[minor] PB-007 "7/7 vs 11/11" — stale 7/7.** `PvpFlaggingRigTests` has **11** `[Test]` at the
  referenced state (`grep -c` per commit: 0492b7199=11, HEAD=11; 7 was pre-0492b7199).
  HANDOFF-GEMINI.md:24 ("7/7 passed") and :176-177 ("verified with 7/7 … passing") are stale;
  HANDOFF.md:11 correctly says 11/11. `ZoneConflictTests` 13/13 is accurate (13 `[Test]`).
- **[minor] HANDOFF-GEMINI.md:27 — "MailCodLifecycleTests 4/4 (26/26…)"** stale: 5/5 at HEAD
  (27/27 across `Mail*` suites: 5+1+12+7+2). Correct at 69861b73c time; left behind by 63436d0a9.
- **[minor] HANDOFF-GEMINI.md:85-91 — orphaned stale gate block** ("full normal-clone gate at …
  `792774d7707…` is `./scripts/gate.sh`; **2496 total / 2495 passed…**; The sole skip is
  `Provision_Activate_Persist_Deactivate_RoundTrip`… **Do not substitute another full-gate
  count**"). Contradicts the header 2531 claim 80 lines above; not flagged historical (line 41's
  "historical where noted" does not cover it).
- **[minor] stale HEAD refs / WAR-HONOR contradiction in same doc**: :154 (table: "current
  source/test HEAD is `792774d…`; **WAR-HONOR intentionally deferred**"), :200, :211 ("WAR-HONOR
  remains intentionally deferred"), :231, :238-239 — all still name `792774d7707…` as current and
  call WAR-HONOR deferred, directly contradicting the updated PB-007 completion claim at
  :169-177. Would misdirect the next agent (the checklist tells Gemini not to touch WAR-HONOR).
- 7dde587fc itself: OK — correctly moved to 2526/2525 with the tick-test skip claim (already
  wrong at that point, same issue as above), and its PB-002/PB-007/Mail prose matches the code.

## Workstream B — six-hour dormant-timer soak (A5 / Tier-3)

### Runner location
- Machinery: `950cfd279` = cooperative cancellation of dormant seeding (`BotDriveClient.CallAsync`
  + token; `A5Tier3AcceptanceProbeTests.cs` seed path; `BotDriveClientCancellationTests`).
  `155c82c66` = the opt-in six-hour stage itself (+310 lines in
  `AAEmu.IntegrationTests/E2e/Gate/A5Tier3AcceptanceProbeTests.cs`).
- Soak test: `AAEmu.IntegrationTests.E2e.G2.A5Tier3AcceptanceProbeTests.Probe_A5Tier3DormantTimers_SixHour`
  (`[Fact]`, `[Collection("e2e")]`, `A5Tier3AcceptanceProbeTests.cs:237`). Default-`Assert.Skip`ped
  unless `A5_TIER3_SIX_HOUR=1` (`:241-246`).
- Bounded sibling (the readiness rehearsal, 15m20s historically): the same class's
  `Probe_A5Tier3Shape_Acceptance` (`:68`, `SCALING_PROBE_MINUTES` default 2, `A5_DORMANT_COUNT`
  default 1000, `A5_EMBODIED_COUNT` 50). There is **no** short-circuit of the six-hour method
  itself: `A5_TIER3_SIX_HOUR_MINUTES < 360` throws.

### Exact command line (from `ROADMAP.md:30` / `playerbot-capability-matrix.md:23`)
```bash
A5_TIER3_SIX_HOUR=1 A5_TIER3_SIX_HOUR_MINUTES=360 A5_TIER3_SIX_HOUR_SAMPLE_SECONDS=60 \
A5_DORMANT_COUNT=1000 \
E2E_ROOT=/root/aaemu-e2e-a5-tier3-sixhour \
E2E_LOGIN_PORT=4237 E2E_GAME_PORT=4239 E2E_STREAM_PORT=4250 E2E_BRIDGE_PORT=4260 \
E2E_INTERNAL_PORT=4234 E2E_WEBAPI_PORT=4280 E2E_DB_PORT=43306 DB_HOST_PORT=43306 \
COMPOSE_PROJECT_NAME=aaemu_a5_t3_sixhour E2E_REBUILD=1 \
dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release \
--filter-method AAEmu.IntegrationTests.E2e.G2.A5Tier3AcceptanceProbeTests.Probe_A5Tier3DormantTimers_SixHour
```
(Isolated root + shifted ports + own compose project = the documented pattern; default ports are
1237/1239/1250/1260/1234/1280, default root `/root/aaemu-e2e`.)

### Env vars / config
- Required by the test: `A5_TIER3_SIX_HOUR=1`; `MINUTES>=360`; `SAMPLE_SECONDS` 1..300;
  `A5_DORMANT_COUNT` (1000 canonical; asserts ≥95% seeded). The test itself sets
  `AAEMU_BOT_TRUE_DORMANCY=1` + `AAEMU_BOT_PROXIMITY_FIDELITY=1` before the game-server restart
  (`A5Tier3AcceptanceProbeTests.cs:277-279`) and seeds via the bridge `seedDormant` command
  (30-min hard `SeedBox` deadline; hardcoded home 15578.042/15382.122/126.484 for this stage).
- MySQL: `E2eStack.EnsureUp()` boots the compose stack; `DB_PASSWORD` is generated and persisted
  to `$E2E_ROOT/.env` (`E2eStack.cs:233-243`) — no manual password needed for *this* stage.
  (`AAEMU_LIVE_RIG` + `AAEMU_E2E_DB_PASSWORD` gate a *different* unit test,
  `HeadlessSessionProvisioningLiveTests`.) `E2E_REBUILD=1` forces the Login/Game publish.
- Budgets asserted (a failure fails the test): embodied==0, dormantSpecs≥seeded,
  materialization/dematerialization counters==0, uptime non-regressing, tick p95≤100 ms / max≤250 ms,
  region≤200 ms, all scheduler queues empty + 0 failures + 0 save-skips, save p95≤4000 ms /
  max≤10000 ms, RSS growth≤512 MB, DB writes≤500/min (Com_insert/update/delete/replace delta).

### Evidence format
- Writes `$E2E_ROOT/logs/g2-a5-tier3-sixhour-report.json`
  (`A5Tier3AcceptanceProbeTests.cs:499-536`): probe label, runAtUtc, `commit =
  E2eStack.SourceRevision` (git HEAD or "unknown"), config {dormantTarget, seededCount,
  windowMinutes, sampleSeconds}, budgets{}, window minutes, initial/final DB writes + per-minute
  rate, full `samples[]` (timestamp, uptimeMs, RSS, embodied, dormantSpecs, materializations,
  dematerializations, tick p95/max, region ms, scheduler depths/failures, save p95/max/skips,
  dbWrites), failures[], `passed`, and `sixHourDormantTimersLeg: "RUN"` (window ≥ 360).
- Ownership hygiene: `_allPlayerMails`-style row scoping via `SnapshotOwnedRows` /
  `FindNewOwnedRows` / `CleanupOwnedRows` around the seed (ID-bound `finally`, per `799b698ad`).

### Can it run here today?
**No — a full soak cannot be executed here.** The stack is partially up: the shared
`e2e-db-1` MySQL container is running on 127.0.0.1:3306, but **no Login/Game servers are bound
to the e2e ports** (1237/1239/1250/1260/1234/1280 all closed — the two running `dotnet`
AAEmu processes belong to the *pb007 live-proof* rig under `/tmp/aaemu-pb007-live-proof-e2e/runtime`,
whose own ports are also closed), the dedicated root `/root/aaemu-e2e-a5-tier3-sixhour` does not
exist, and no `g2-a5-tier3-sixhour-report.json` exists. A run would require
`E2eStack.EnsureUp()` to rebuild/republish + seed 1000 dormant specs + run ≥6 h — out of scope to
start here. Only the default **skip** path is executable without the stack; the bounded
`Probe_A5Tier3Shape_Acceptance` rehearsal also requires the full stack. So: the soak is
**PENDING** exactly as STATUS.md and HANDOFF-GEMINI.md:181-182 state — nothing misclaimed.
