# AAEmu Fork — Fix/Feature Workflow (Tai's playbook, v4)

Goal: safe, reviewable changes to joshhmann/AAEmu that keep the running server
stable and remain easy to reconcile with upstream updates. Upstream is
strictly intake-only.

> **🚫 THE RULE (Josh, 2026-08-03 — at the top, permanent):** **NEVER push a
> branch or open a PR to upstream AAEmu/AAEmu.** Every fix
> and every feature follows the full workflow — branch, separate commits,
> tests, evidence, and tracking—and stays in our fork. Upstream updates may be
> fetched into a dedicated sync branch and integrated after verification.

v4 changes: made the upstream relationship permanently one-way; separated the
fast developer gate from CI parity; made deployment/rollback service-aware;
and clarified scorecard/state ownership. The
community-standard quality bar remains useful internally, but outbound
branches and PRs are prohibited.

## Repository topology (canonical)

```
OPENCLAW (dev box)               GITHUB (fork)                  AAEMU BOX .165 (prod)
/root/aaemu-dev                  joshhmann/AAEmu                /root/AAEmu
origin = joshhmann/AAEmu  ─────►  develop ◄───────────────────  fork = joshhmann/AAEmu
(branch/work/test here)          (source of truth)              (docker compose runs here)
upstream = AAEmu/AAEmu (fetch-only intake; never used from production)
```

- **Host roles:** openclaw = development only. aaemu box = production only.
- **Source-of-truth rule:** GitHub fork `develop` is the only relay between
  them. Never copy source directly openclaw→prod; never develop in
  /root/AAEmu on prod; production never receives unreviewed working-tree
  changes.
- **Remote mapping:** openclaw's `origin` is the fork (dev default) and an
  `upstream` remote is read-only intake. Prod deploys only from its `fork`
  remote (`joshhmann/AAEmu`); even if another remote exists there, production
  never pulls or integrates upstream directly.
- **Push guard:** every development clone configures upstream with a disabled
  push URL while preserving its fetch URL:
  `git remote set-url --push upstream DISABLED`. Verify with `git remote -v`
  during workspace setup. `origin` is the only normal push target.
- **Deploy procedure** (Mai coordinates; exact-SHA, see ROADMAP §Deployment
  discipline):

```bash
ssh aaemu
cd /root/AAEmu
git status --short          # refuse if dirty (drift check)
git fetch fork
git switch develop
git merge --ff-only fork/develop   # production never generates merge commits
DEPLOY_SHA="$(git rev-parse HEAD)"
docker compose config --quiet
docker compose up -d --build <affected-services>
docker compose ps
echo "Deployed ${DEPLOY_SHA}"      # record in deployments/production.json
```

- **Rollback:** preserve branch history: `git switch --detach <previous-sha>`
  and rebuild the affected services. Return to `develop` only for a later
  forward deployment. For DB-changing releases, use the migration-specific
  rollback/restore plan recorded before deployment; never assume a code
  rollback can reverse a schema or data migration.
- **Milestone releases:** tag on the fork (`git tag living-village-m1-rc1
  <sha>`), prod deploys the exact tag/SHA.
- **Deployment manifest:** `deployments/production.json` (env, git SHA,
  deployed_at, milestone, DB backup, service health) — written by the
  deploy script, never hand-maintained.

### One-way upstream sync

Integrate upstream on development, never on production and never directly on
the fork's `develop` branch:

```bash
git fetch upstream
git fetch origin
git switch -c sync/upstream-YYYY-MM-DD origin/develop
git merge --no-ff upstream/develop
./scripts/gate.sh
dotnet test --project AAEmu.Login.IntegrationTests --configuration Release --no-build
git push origin sync/upstream-YYYY-MM-DD
```

Review upstream commits, migration/config changes, and conflicts on the sync
branch; run affected integration and golden-route scenarios; then merge only
into the fork. Refresh Graphify after the sync. Never push the sync branch to
`upstream`, and never use production as the merge workspace.

## Upstream alignment rules (Josh, locked 2026-08-04)

These sit with THE RULE — they keep the fork community-shaped. Full text in
`ROADMAP.md` §Standing rules + `Docs/wiki/Development-Conventions.md`
(current-state verification notes included there).

1. Target AAEmu `develop`, .NET 10 (global.json).
2. Aspire AppHost for LOCAL contributor debugging; prod stays on the current
   Docker Compose deployment (deployments/production.json).
3. `compact.sqlite3` = read-only reference data. Mutable state → MySQL or an
   additive bot metadata schema.
4. Config precedence `Config.json` → `Configurations/*.json` →
   `Config.Local.json`; machine-specific hosts/secrets/paths never in shared
   config.
5. Server listings via `GameServers` config — never legacy
   `aaemu_login.game_servers`.
6. Explicit constructor dependencies where AAEmu supports them; no hidden
   singleton lookup / undocumented startup order.
7. Startup loading can be parallel — concurrency-safe collections + init.
8. AAEmu-native terminology: Doodad/Mate/Slave/Transfer/Expedition/Dominion/
   Ability/ActAbility (see wiki table).
9. PlayerBots compose around ordinary `Character` records + normal gameplay
   services — no parallel gameplay implementation.
10. Additive layer: composition/adapters/extension points first; narrow
    reviewed core hooks only; never a parallel gameplay path.

## PlayerBot scale architecture (locked 2026-08-07 — review t_be295ecf)

Code-validated by the architecture review card t_be295ecf (21/21 spec
sections reviewed; 11 confirmed, 10 corrected). Full record: `ROADMAP.md`
§M5/M6 + `docs/playerbot-scale-architecture-review.md`. Locked invariants:

- **Embodiment: real Characters, no fake client.** A bot citizen = real
  managed `aaemu_login` account (account_type=HeadlessBot, blocked from
  client login) + ordinary `characters` row + production `HeadlessSession`
  + `PlayerBotController` via `ICharacterLifecycleService.ActivateHeadless`.
  No fake client, no network socket, no login-handshake emulation — packets
  no-op through the null-safe sink. The M2b pilot's DB-row-less
  `HeadlessSession.Create` is an E2E fixture only, never the production path.
- **Scheduler: dedicated `IPlayerBotScheduler`** — due-time priority queue +
  bounded 4-8 worker pool + per-bot execution lease. NEVER add bots to
  `AIManager` (single-lock, all-AI serial); NEVER one TickManager
  subscription per bot (linear sync list); exactly one async wake-scan
  subscription or a dedicated thread.
- **Fidelity states: Dormant / Reduced / Full** (Tier labels retired).
  `PopulationDirector` is the ONLY fidelity authority; no-downgrade guard in
  combat/slave/pack/trial/party/saving.
- **Density gates:** 10 correctness → **25 FIRST STABILITY GATE (hard stop
  until H2)** → 50 soak ≥6h → 100 profiling → 250 mixed fidelity (≥60%
  dormant/reduced) → 500 → 1000 (≥80% dormant/reduced). 1,000 persistent
  citizens, not 1,000 thinking clients.
- **P0 core hooks (the only core changes required):** H1 Region activity
  split — bots don't count toward Region player activity by default, only
  full-fidelity bots/humans wake NPC AI/spawners/area triggers/sphere quests
  (explicit bot-activity opt-in); H2 ActiveRegionTick async/time-budgeted —
  the #1491-class starvation fix, verified unfixed upstream, so the 25-bot
  gate stands.
- Fork-only lane unchanged: all bot work stays on the fork; docs branches
  follow the normal fix/feat/docs flow.

## Environment

- Repo (dev): /root/aaemu-dev — fork clone, branch `develop`, tracks joshhmann/AAEmu
- Graph: /root/aaemu-dev/graphify-out/graph.json (17.5k nodes, 40.6k edges, 749 communities)
- Scorecard: /root/aaemu-dev/SCORECARD.md (679-table canonical surface vs code wiring vs upstream issues)
- Explorations: /root/aaemu-dev/scorecard-explorations/ (per-domain nitty-gritty reports)
- SDK: .NET 10 (dotnet-sdk-10.0 on openclaw)
- Production: aaemu box (CT 133, 192.168.0.165) runs docker compose from /root/AAEmu

## Understanding layer (graphify)

1. `cd /root/aaemu-dev && graphify explain "X"` — node + neighbors
2. `graphify affected "X" --depth 2` — blast radius
3. `graphify path "A" "B"` — connection path
4. `graphify query "how does X work"` — BFS question traversal
5. After changes: `graphify update .` — refresh (no LLM cost)

## Fork merge quality bar

### CI gates (.github/workflows/dotnetcore.yml) — ALL must pass:
1. `dotnet restore`
2. `dotnet build --configuration Release --no-restore` ← Release, not Debug
3. **`dotnet run --project AAEmu.Game compiler-check`** ← the sneaky gate: compiles the
   in-game Lua/C# scripts. A new Scripts/ file with a typo fails here.
4. `dotnet test --project AAEmu.UnitTests --configuration Release --coverage` ← coverage
   measured + uploaded to Coveralls. New behavior WITHOUT new tests drops coverage.
5. `dotnet test --project AAEmu.Login.IntegrationTests` ← Testcontainers MySQL

Sonar + CodeQL run on push to develop/master. Regardless of which checks run on
the private fork, reviews require a clear what/why, risk notes, verification
evidence, and no dead code or obvious performance traps.

## Fix/feature loop (per repo AGENTS.md + graph + CI)

1. **Scope** — subsystem + 2-3 representative files + I* interface + tests.
   Use graphify explain/affected. Consult SCORECARD.md + explorations for known gaps.
2. **Branch** — `git checkout -b fix/<slug> develop` (never commit to develop)
3. **Implement** — smallest change; match naming/placement/logging/error patterns.
   No drive-by refactors, no reformatting unrelated files, no new frameworks.
4. **Wire-up** — new manager/service: register in Program.cs like peers;
   new packet: offsets + RegisterPacket in correct *Network;
   new login handler: packet + handler + DI extension.
   New deps: constructor injection (Lazy<T> for circular manager deps) — NOT
   more Singleton<T> static access.
5. **SQL** — schema change: add SQL/updates/… AND update base SQL/aaemu_*.sql
6. **Test** — add/extend AAEmu.UnitTests (MethodName_Scenario_ExpectedResult),
   reuse TestBase/SqliteTestBase/IntegrationTestBase + mocks.
7. **Verify — two explicit gates:**

   Fast developer gate (`scripts/gate.sh`; build + compiler-check + unit tests):
   ```bash
   ./scripts/gate.sh
   ```

   CI-parity gate before merge/push (coverage-enabled unit tests plus Login
   integration tests; requires its Testcontainers runtime):
   ```bash
   dotnet test --project AAEmu.UnitTests --configuration Release --no-build --coverage
   dotnet test --project AAEmu.Login.IntegrationTests --configuration Release --no-build
   ```

   Also run `AAEmu.IntegrationTests` when the touched subsystem or milestone
   scenario is covered there. If Docker/Testcontainers is unavailable, the
   task is locally verified but not merge-ready; record the missing gate and
   run it in CI or an equipped verification workspace.
8. **Graph refresh** — `graphify update .`
9. **Fork review** — push the branch only to the fork. Use a fork PR or the
   Rei gate with: Problem / Root cause (evidence) / Fix / Verification / Notes.
   Never push the branch or open a PR to AAEmu/AAEmu.

## Deploy to prod (exact-SHA — see §Repository topology for full procedure)

```bash
ssh aaemu
cd /root/AAEmu
git status --short          # refuse if dirty (drift check)
git fetch fork
git switch develop
git merge --ff-only fork/develop
DEPLOY_SHA="$(git rev-parse HEAD)"
docker compose up -d --build <affected-services>
docker compose ps
echo "Deployed ${DEPLOY_SHA}"   # record in deployments/production.json
```

Choose affected services from the diff: Game changes rebuild `game`; Login or
authentication changes rebuild `login`; shared/Commons changes rebuild both.
SQL/config/orchestration changes require an explicit deployment card rather
than defaulting to `game`. After startup, verify ports, container health, and
GameServer registration before recording success.

Rollback: `git switch --detach <previous-sha>` + rebuild the same affected
services. DB-changing releases follow the manifest's pre-approved migration
rollback/restore procedure.

## Tracking discipline (every change, no exceptions)

- **Every fix** → one branch (`fix/<slug>`), one logical commit (or a small
  series), tests added/extended, scorecard row updated if the fix changes a
  domain's status.
- **Every feature** → one branch (`feat/<slug>`), commits per logical step,
  tests per step, scorecard row added with the new coverage.
- **Scorecard updates** happen in the same PR/branch when the work materially
  changes a measured row or evidence claim. Do not manufacture a scorecard
  diff for documentation-only, tooling-only, or behavior-neutral work.
- Commit messages: present tense, conventional prefix, <72 chars title.
- Commit identity — the actual contributor authors the work, never a shared
  machine identity such as `root@openclaw`:
  - default: the sister who did it (`git -c user.name="Tai" -c
    user.email="tai@asslorde.com" commit ...`) — Tai/Rei/Nei/Mai @ asslorde.com
  - fallback for collective changes: `Hyraxknot Division
    <division@asslorde.com>`
  - Review or deployment approval does not change authorship. Preserve the
    real author/co-author metadata; never rewrite a commit to impersonate an
    approver or merge operator.
- Branch merged to fork `develop` only after: CI-parity gate green; relevant
  subsystem integration tests green; Graphify refreshed when structural code
  changed; and scorecard evidence updated when materially affected.
- Fix log: add a line to `ISSUES.md`/`bugs/` when fixing a known bug
  (reference the bug id, root cause, files changed, tests added).

### State ownership and freshness

- Kanban is the source of truth for live task ownership/status.
- GitHub fork `develop` SHA is the source of truth for merged code.
- `deployments/production.json` is the source of truth for production state.
- `STATUS.md` is a human-readable cache, not a second task database. Nei
  updates it after merge/deploy events, and verifies its recorded `develop`
  SHA before publishing. Feature branches should provide a one-line handoff;
  they should not all edit `STATUS.md` and create avoidable merge conflicts.

## FORK-REVIEW CHECKLIST (pre-merge)

- [ ] Branch from develop, single logical change
- [ ] Commit message: present tense, conventional prefix, <72 chars title
- [ ] PR body: Problem / Root cause (evidence) / Fix / Verification / Notes
- [ ] Release build passes
- [ ] compiler-check passes (scripts compile!)
- [ ] Unit tests pass, new behavior has new tests
- [ ] No drive-by refactors / unrelated formatting
- [ ] No dead code or obvious performance traps
- [ ] Push target verified as the fork, never AAEmu/AAEmu
- [ ] SQL updates if schema touched

## Pitfalls

- CRITICAL: keep the glibc runtime change in AAEmu.Game/Dockerfile (BUG-001).
  If it regresses, game container SIGSEGVs during AiGameData load.
- The `compiler-check` gate: any Scripts/ C# file must compile. Test it locally.
- Coveralls coverage: `--coverage` flags in CI — weak test additions on hot paths
  drop the % and can fail the gate.
- Graph tied to commit — refresh with `graphify update .` after pulls.
- Don't touch SQL/aaemu_login.sql casually — it seeds the login DB.
- `COMMUNITY-GUIDELINES.md` is upstream-awareness reference material only; it
  never authorizes an outbound branch or PR.
