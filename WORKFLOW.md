# AAEmu Fork — Fix/Feature Workflow (Tai's playbook)

Goal: safe, reviewable changes to joshhmann/AAEmu that keep the running
server stable. Ground truth: the repo's own AGENTS.md workflow + graphify
for understanding. Local dev env: openclaw (CT 124), /root/aaemu-dev.

## Environment

- Repo (dev): /root/aaemu-dev — fork clone, branch `develop`, tracks joshhmann/AAEmu
- Graph: /root/aaemu-dev/graphify-out/graph.json (17.5k nodes, 40.6k edges, 749 communities)
- SDK: .NET 10 (dotnet-sdk-10.0 on openclaw)
- Tests: `dotnet test` (AAEmu.UnitTests primary) — MUST pass before done
- Production: aaemu box (CT 133, 192.168.0.165) runs docker compose from /root/AAEmu
  - Deploy = push fork develop → pull on box → `docker compose up -d --build game`
  - NEVER deploy unbuilt/uncommitted code to prod

## Understanding layer (graphify)

Before touching code:
1. `cd /root/aaemu-dev && graphify explain "X"` — what a node is + its neighbors
2. `graphify affected "X" --depth 2` — blast radius: what breaks if X changes
3. `graphify path "A" "B"` — how two things connect
4. `graphify query "how does X work"` — BFS traversal for questions
5. After changes: `graphify update .` — re-extract (no LLM cost, keeps graph fresh)

## Fix/feature loop (per repo AGENTS.md + graph)

1. **Scope** — identify subsystem (manager/packet/model/gamedata); read 2-3
   representative files + the I* interface + existing tests. Use graphify
   explain/affected to map the neighborhood.
2. **Branch** — `git checkout -b fix/<slug> develop` (never commit to develop)
3. **Implement** — smallest change; match naming/placement/logging/error patterns
   of surrounding code. No drive-by refactors.
4. **Wire-up** — new manager/service: register in Program.cs like peers;
   new packet: offsets + RegisterPacket in correct *Network;
   new login handler: packet + handler + DI extension.
5. **SQL** — schema change: add SQL/updates/… AND update base SQL/aaemu_*.sql
6. **Test** — add/extend AAEmu.UnitTests tests (MethodName_Scenario_ExpectedResult);
   `dotnet build` + `dotnet test` must pass
7. **Verify** — full test suite green; graphify update . to refresh graph
8. **PR** — push branch to fork, open PR vs develop (following CONTRIBUTING.md:
   present tense, single squash commit, clean diff). Wait for Greptile review
   (they auto-review) + maintainer.

## Deploy to prod (only after PR merged to fork develop)

```bash
ssh aaemu  # CT 133
cd /root/AAEmu && git fetch fork && git checkout develop && git pull fork develop
docker compose up -d --build game   # rebuild with new code
docker compose ps                    # verify healthy
```

Rollback: `git revert` on the box + `docker compose up -d --build game`.

## Pitfalls

- CRITICAL: keep the glibc runtime change in AAEmu.Game/Dockerfile (BUG-001).
  If it regresses, game container SIGSEGVs during AiGameData load.
- The graph is tied to commit e00f57c — refresh with `graphify update .` after pulls.
- dotnet build needs the SDK; first restore is slow (~NuGet).
- Don't touch SQL/aaemu_login.sql casually — it seeds the login DB.
