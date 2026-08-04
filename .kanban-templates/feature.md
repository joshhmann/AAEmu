# FEATURE TEMPLATE — Track 2 / our-lane feature (strict workflow, fork-only)

> Fill every section. Delete nothing. This is the contract for the task.

## Feature
- **Vision link:** VISION.md (lane: Track 2 bots / other)
- **What players experience (user story):**
- **Domain touched:** (scorecard domain, e.g. siege/ranks/premium)
- **Canonical data:** tables in compact.sqlite3 that define it (from /tmp/tables.txt or SCORECARD.md)

## Plan (order matters)
1. **Branch:** `feat/<slug>` off develop
2. **Understand:** scorecard-explorations/<domain>.md + `graphify explain/affected` + read the manager(s) that will host it
3. **Design:** additive layer ONLY — must not break upstream pulls (no core-interface edits without abstraction; new managers/services hook in like peers)
4. **Implement:** commits per logical step (data load → runtime logic → packets → tests)
5. **Wire-up:** register in Program.cs / DI like peers; new packets: offsets + RegisterPacket in correct *Network
6. **SQL:** new MySQL state tables → SQL/updates/… + base SQL/aaemu_*.sql; sqlite reference data → loaders
7. **Tests:** AAEmu.UnitTests per step — `MethodName_Scenario_ExpectedResult`; integration where stateful

## Tools (use these, in this order)
- graphify: `cd /root/aaemu-dev && graphify explain "X" --graph graphify-out/graph.json`
- scorecard: `python3 /tmp/scorecard2.py` (regenerate) — but update SCORECARD.md in THIS branch
- build: `dotnet build --configuration Release AAEmu.slnx`
- compiler-check: `dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check`
- tests (full): `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --no-build`
- tests (filtered): `dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~<Name>"`
- live sqlite queries (for data understanding): ssh root@192.168.0.165 + python3 sqlite3 on /root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3

## Verify (ALL must pass before merge to fork develop)
- [ ] Release build: 0 errors
- [ ] compiler-check: "Compilation successful"
- [ ] Full test suite: 0 failed
- [ ] New tests cover each implemented step
- [ ] `graphify update .` (graph fresh)
- [ ] SCORECARD.md updated IN THIS BRANCH (domain row + coverage %)
- [ ] Exploration report updated if the feature changes the domain picture
- [ ] Lane separation respected: no changes that would make upstream sync painful

## Deliverables
- Commits: per logical step, present tense, conventional prefix, <72 chars
- Push: branch to fork origin ONLY (fork develop merge after green). **NO upstream PR.**
- Report: summary + test evidence + scorecard diff + deploy note
