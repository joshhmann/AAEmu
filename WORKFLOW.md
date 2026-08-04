# AAEmu Fork — Fix/Feature Workflow (Tai's playbook, v2)

Goal: safe, reviewable changes to joshhmann/AAEmu that keep the running
server stable AND pass upstream (AAEmu/AAEmu) merge gates when we push PRs.
v2 adds the upstream-compliance layer: CI gates, Greptile review
expectations, and the PR-ready checklist (from Nei's COMMUNITY-GUIDELINES.md).

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

## THE UPSTREAM MERGE BAR (what CI + Greptile check)

### CI gates (.github/workflows/dotnetcore.yml) — ALL must pass:
1. `dotnet restore`
2. `dotnet build --configuration Release --no-restore` ← Release, not Debug
3. **`dotnet run --project AAEmu.Game compiler-check`** ← the sneaky gate: compiles the
   in-game Lua/C# scripts. A new Scripts/ file with a typo fails here.
4. `dotnet test --project AAEmu.UnitTests --configuration Release --coverage` ← coverage
   measured + uploaded to Coveralls. New behavior WITHOUT new tests drops coverage.
5. `dotnet test --project AAEmu.Login.IntegrationTests` ← Testcontainers MySQL

Sonar + CodeQL run on push to develop/master (quality gates, no PR block).

### Greptile AI review (auto-runs on PRs):
- Scores confidence 0-5 and flags concrete regressions. Pre-empt it:
  - Every change must have a clear "what/why" in the PR body
  - Native libs / runtime / package changes: show the `ldd`/build evidence
  - Don't leave dead code, unused vars, or obvious perf traps (it reads the diff)
- It reviewed #1494 at 5/5 with the evidence-first writeup. That's the template.

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
7. **Verify — the FULL local gate** (mirrors CI):
   ```bash
   dotnet build --configuration Release
   dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check
   dotnet test --project AAEmu.UnitTests --configuration Release --no-build
   ```
   All green before anything else.
8. **Graph refresh** — `graphify update .`
9. **PR** — push branch to fork; PR vs develop with:
   - single squashed commit, present tense ("fix(docker): …")
   - PR body: Problem / Root cause (with evidence) / Fix / Verification / Notes
   - mention what you tested locally (build + compiler-check + tests)
   - don't rush the merge — Greptile comments in ~minutes; maintainers follow

## Deploy to prod (only after PR merged to fork develop)

```bash
ssh aaemu
cd /root/AAEmu && git fetch fork && git checkout develop && git pull fork develop
docker compose up -d --build game
docker compose ps   # verify healthy
```

Rollback: `git revert` on the box + `docker compose up -d --build game`.

## PR-READY CHECKLIST (pre-push)

- [ ] Branch from develop, single logical change
- [ ] Commit message: present tense, conventional prefix, <72 chars title
- [ ] PR body: Problem / Root cause (evidence) / Fix / Verification / Notes
- [ ] Release build passes
- [ ] compiler-check passes (scripts compile!)
- [ ] Unit tests pass, new behavior has new tests
- [ ] No drive-by refactors / unrelated formatting
- [ ] Greptile pre-empted: no dead code, no obvious traps
- [ ] SQL updates if schema touched

## Pitfalls

- CRITICAL: keep the glibc runtime change in AAEmu.Game/Dockerfile (BUG-001).
  If it regresses, game container SIGSEGVs during AiGameData load.
- The `compiler-check` gate: any Scripts/ C# file must compile. Test it locally.
- Coveralls coverage: `--coverage` flags in CI — weak test additions on hot paths
  drop the % and can fail the gate.
- Graph tied to commit — refresh with `graphify update .` after pulls.
- Don't touch SQL/aaemu_login.sql casually — it seeds the login DB.
- Follow Nei's COMMUNITY-GUIDELINES.md for anything that differs from this doc.
