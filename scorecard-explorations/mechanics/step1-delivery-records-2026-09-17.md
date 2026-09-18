> **Durability note:** this `scorecard-explorations/mechanics/` path is the committed, authoritative copy.
> The `scorecard-explorations/generated/` copy is superseded (gitignored per `.gitignore:27`, untracked).
> Copied 2026-09-17 at HEAD `533bfcae5becc0264946dbdd1ecde3141595b8b3`; body identical below.

# Step-1 Delivery Records — 2026-09-17 (reconcile delivery records and truthful handoff)

Step-1 project-control record. Docs only: no gameplay change, no test run, no promotion,
no milestone closure. Every claim below is either traced to a named artifact or marked
UNKNOWN. Ledger discipline applies (`EVIDENCE-LEDGER.md:24-33`): append-only, no citation =
no transition, scripted-actor evidence never flips state 7 (H), H stays UNKNOWN everywhere
in this wave.

## Baseline (C1/C2)

- HEAD at record time: `533bfcae5becc0264946dbdd1ecde3141595b8b3` (`git rev-parse HEAD`).
- Dirty at record time (6 files, `git status --short`): `PROJECT-CONTROL.md`, `ROADMAP.md`,
  `SCORECARD.md`, `STATUS.md`, `Scripts/playertrace-coverage/dashboard_server.py`,
  `playertrace-coverage/dashboard.html`. No staged changes.
- Canonical DB md5: `78b3bdbf038db3b927056106efdf91af` per ledger change log (not re-verified
  this pass; no data claims made here).
- Scoped commits are ancestors of HEAD (verified `git merge-base --is-ancestor` for all three).
- `[Test]` counts below were measured at the HEAD worktree, NOT at each claim commit, and
  are therefore context — not claim-time evidence. Claim-time run results were not recorded
  anywhere found → Verified stays UNKNOWN for all three deliveries.

## Delivery 1: GOAP Phase 3 runtime + route catalog (scoped)

- Commit(s) / blob:            `c273c6da729740489472ffff7741758b813f18c8` (2026-09-16)  [committed]
- HEAD at claim time:          UNKNOWN (no record ties the claim to a HEAD/dirty state) · dirty: UNKNOWN
- Scoped files:                `AAEmu.Game/Core/Managers/Bots/Goap/` — `GoapPlanRunner.cs` (new, +397),
  `GoapScenarioIntegrationTests.cs` (+421), `GoapTelemetry.cs` (+72), `GoalArbitrator.cs` (+139),
  `BotWorldStateProvider.cs` (+117), `FarmingActions.cs` (+102), `BotMemory.cs` (+84),
  `CombatActions.cs` (+70), `GoapBotStepExecutor.cs` (+66), `RecoveryActions.cs` (+60),
  `PlanTemplateCache.cs` (+31), `BotContext.cs` (+27), `GoapActionStatus.cs` (+25),
  plus interfaces/`GoapPlanner.cs` (+14/-) and 8 `AAEmu.Game/Data/Routes/highway_*.json`
  fixture route files (+286..+1297 each); `GoapRuntimeTests.cs` (+150)
- Intent (one line):           Ship the GOAP Phase 3 runtime engine with an atlas route
  catalog and candidate route generator.
- Implemented:                 partial — engine + registry + telemetry code present;
  evidence: `AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanRunner.cs` (new file) @ `c273c6da7`
- Verified @ layer:            UNKNOWN — no command, result, or artifact recorded at claim
  time. Context only: `GoapRuntimeTests.cs` (7 `[Test]`) and `GoapScenarioIntegrationTests.cs`
  (4 `[Test]`) exist at HEAD; claim-time outcome NOT RUN / not recorded.
- Deployed:                    unknown (ledger names no deploy record for this commit)
- NOT PROVED:                  live navigation over the 8 route JSONs; planner optimality or
  cost claims; any L (live) or H (human) behavior; route quarantining came later (`cac214ff0`)
- Seeded / bypassed steps:     route JSONs shipped as fixture data (later quarantined as
  fixtures by `cac214ff0`); no GM grants found in this commit's stat — beyond that UNKNOWN
- Reverted / superseded:       UNKNOWN — `GoapPlanner.cs` touched here (±14) and GOAP pareto
  work followed in `901296119`; supersede relationship not diffed, not claimed
- Owner / verifier / tracker:  owner UNKNOWN · verifier pending (no ledger signoff names this
  commit) · tracker Nei (ledger maintainer, by role — not a review of this delivery)
- Deployment authority:        Mai lane; not authorized, not claimed
- H:                           UNKNOWN
- Records touched:             none cite this commit (finding D1); this file is the first record
- Next action:                 run the two test classes at a named SHA and record command +
  result + artifact, or keep Verified UNKNOWN

## Delivery 2: homestead/housing loop via GOAP (scoped)

- Commit(s) / blob:            `91aa560b508bc346394aa05317ce18ebc0ffe110` (2026-09-16)  [committed]
- HEAD at claim time:          UNKNOWN · dirty: UNKNOWN
- Scoped files:                `HomesteadActions.cs` (new, +389), `HomesteadRuntimeTests.cs`
  (new, +496), `BotWorldStateProvider.cs` (+55), `GoalArbitrator.cs` (+48),
  `GoapActionRegistry.cs` (+22), `BotMemory.cs` (+16), `BotWorldState.cs` (+9)
- Intent (one line):           Implement the homestead/housing progression loop as GOAP actions.
- Implemented:                 partial — action surface present WITH test-override branches;
  evidence: `AAEmu.Game/Core/Managers/Bots/Goap/Actions/HomesteadActions.cs` (new file) @
  `91aa560b5`; override gates at `HomesteadActions.cs:49-50`
  (`HasScarecrowDesignOverride`/`HasTaxCertificatesOverride`) and `:192`
  (`HasLandPlotOverride`/`OwnedHouseId`) — line numbers at HEAD worktree, not claim commit
- Verified @ layer:            UNKNOWN — no claim-time command/result/artifact. Context only:
  `HomesteadRuntimeTests.cs` (11 `[Test]` at HEAD); outcome not recorded.
- Deployed:                    unknown (no ledger deploy record for this commit)
- NOT PROVED:                  ordinary acquisition (vs seeded kit); real target binding (fixed
  IDs/zero workbench target per ROADMAP loop inventory); live loop; any H
- Seeded / bypassed steps:     test-override branches above; `AcquireScarecrowAction` returns
  `Running` unless overrides set (D12 — step 3/5/7 correctness finding, not an evidence claim)
- Reverted / superseded:       none known; later `63f6567e7` extends (+50) and `92c14bbac`
  changes the construction seam underneath — interaction not analyzed here
- Owner / verifier / tracker:  owner UNKNOWN · verifier pending · tracker Nei (by role)
- Deployment authority:        Mai lane; not authorized, not claimed
- H:                           UNKNOWN
- Records touched:             none cite this commit (D1); this file is the first record
- Next action:                 step 2 owns evaluator/report repair for this loop's traces; step 3
  owns override reachability (D11/D12)

## Delivery 3: starter progression + /bot home (scoped)

- Commit(s) / blob:            `63f6567e7a198775ffe885a3345db70ff5a50777` (2026-09-16)  [committed]
- HEAD at claim time:          UNKNOWN · dirty: UNKNOWN (records later cite this SHA + "dirty
  tree" without enumerating it — the dirty list was never recorded → UNKNOWN)
- Scoped files:                `HomesteadActivityModule.cs` (new, +65), `BotHomeSubCommand.cs`
  (new, +260), `BotRoamStepExecutor.cs` (+52), `HomesteadActions.cs` (+50),
  `GoalArbitrator.cs` (+7), `HeadlessSession.cs` (+8), `Program.cs` (+1), `BotCmd.cs` (+1),
  `HomesteadRuntimeTests.cs` (+56), `Docs/wiki/PlayerBot-GOAP-Planning-Architecture.md` (±72)
- Intent (one line):           Enable starter homestead progression and the `/bot home` command.
- Implemented:                 partial — wiring present, behavior unreachable by default;
  evidence: `AAEmu.Game/Core/Managers/Bots/HomesteadActivityModule.cs` (new file) @
  `63f6567e7`; `Enabled { get; set; } = false` at `HomesteadActivityModule.cs:27`
  (HEAD worktree); no production setter sets it true (D11)
- Verified @ layer:            UNKNOWN — no claim-time command/result/artifact on record
- Deployed:                    unknown (no ledger deploy record for this commit)
- NOT PROVED:                  that `bot home run`'s "Autonomous homestead progression
  activated" message corresponds to an activation (arbiter `CanActivate` denies while
  disabled — D11); any live/human exit
- Seeded / bypassed steps:     starter-kit grant path via the new subcommand (demonstration
  provisioning — step 3 isolation scope, D11)
- Reverted / superseded:       partially superseded in behavior by `92c14bbac` (house
  construction routed through `CraftEffect`; fixture-completion semantics per STATUS) —
  recorded as behavior change, not as new evidence
- Owner / verifier / tracker:  owner UNKNOWN · verifier pending · tracker Nei (by role)
- Deployment authority:        Mai lane; not authorized, not claimed
- H:                           UNKNOWN
- Records touched:             cited as baseline (not as evidence) in `ROADMAP.md:97,220`,
  `STATUS.md:53,83`, `playertrace-coverage/summary.md:4`, journey dossier `:41,:180` (D1)
- Next action:                 step 3 owns opt-in isolation + activation-message truth (D11)

## Follower commits after 63f6567e7 (committed-without-record, D1)

One line each; full §2.2 blocks deferred until step 1 reconciles them (C14):

- `a49d24472` (2026-09-17) chore(hygiene) — ignore worktrees, drop tracked pyc, normalize csv
  eol — committed-without-record.
- `b4526515b` (2026-09-17) feat(core) — instance transitions, dungeon terrain snap, dueling
  disconnect safety — committed-without-record.
- `901296119` (2026-09-17) feat(playerbots) — GOAP pareto planning, starter homestead
  progression, role combat, test isolation — committed-without-record (possible GOAP
  supersede context for Delivery 1; not diffed, not claimed).
- `e7d6c13d4` (2026-09-17) feat(deploy) — package SQL updates in container images, auto-apply
  updates — committed-without-record.
- `4a7e97db1` (2026-09-17) docs(governance) — outcome-first dispatch, evidence honesty,
  progression roadmap — committed-without-record.
- `cac214ff0` (2026-09-17) feat(telemetry) — evidence lanes, fixture-route quarantine,
  homestead trace report — committed-without-record.
- `df25a3a80` (2026-09-17) test(isolation) — hermetic cleanup for test bags/duel
  characters/craft-effect null safety — committed-without-record.
- `92c14bbac` (2026-09-17) fix(playerbots) — house construction through `CraftEffect`
  (construction-seam behavior change; fixture-completion semantics) — committed-without-record.
- `533bfcae5` (2026-09-17) docs(evidence) — construction-seam fixture boundary note —
  current HEAD — committed-without-record.

## Dashboard freshness (same wave, §6 item 5)

- Freshness line (`Planning freshness` meta-val) was stale: `2026-09-16 · source
  63f6567e7a198775ffe885a3345db70ff5a50777 + dirty tree`.
- Updated 2026-09-17 to HEAD `533bfcae5becc0264946dbdd1ecde3141595b8b3 + dirty tree ·
  small-plot journey INCOMPLETE` in `Scripts/playertrace-coverage/dashboard_server.py`
  and regenerated `playertrace-coverage/dashboard.html` via
  `Scripts/playertrace-coverage/regen_static_dashboard.sh` (server template owns the html;
  never hand-edit). Verdict/journey state unchanged — still INCOMPLETE, no new claim.

## Loop verdict (C12)

LOOP INCOMPLETE — missing/unproved: ordinary acquisition; concrete target binding; actual
travel/reach over fixture routes; action-chain consequences with resource conservation;
recovery/repeat; restart behavior for this loop; H. See ROADMAP required
loop-completeness inventory (`ROADMAP.md:230-245`).
