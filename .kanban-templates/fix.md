# FIX TEMPLATE — Track 1 canonical fix (strict workflow, lane gate: NO upstream PR)

> Fill every section. Delete nothing. This is the contract for the task.

## Bug
- **Source:** upstream issue #___ / quest ID ___ / exploration report: scorecard-explorations/___.md
- **Symptom (user-visible):**
- **Root cause (code, file:line):**

## Plan (order matters)
1. **Branch:** `fix/<slug>` off develop (never commit to develop)
2. **Understand:** `graphify explain <type>` + `graphify affected <type> --depth 2` + read 2-3 representative files + existing tests
3. **Implement:** smallest change; match surrounding naming/patterns; no drive-by refactors
4. **Wire-up** (if new class/manager/packet): register in Program.cs like peers / offsets + RegisterPacket in correct *Network
5. **SQL** (if schema touched): add SQL/updates/… AND update base SQL/aaemu_*.sql
6. **Tests:** add/extend AAEmu.UnitTests — `MethodName_Scenario_ExpectedResult`. MUST fail before fix, pass after.

## Tools (use these, in this order)
- graphify: `cd /root/aaemu-dev && graphify explain "X" --graph graphify-out/graph.json`
- editor: read_file / patch / write_file in /root/aaemu-dev
- build: `dotnet build --configuration Release AAEmu.slnx`
- compiler-check: `dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check`
- tests (full): `./scripts/gate.sh`
- tests (filtered): `./scripts/gate.sh <ClassName>   # MTP treenode-filter: /*/*/<ClassName>/*`

## Verify (ALL must pass before commit)
- [ ] Release build: 0 errors
- [ ] compiler-check: "Compilation successful"
- [ ] Full test suite: 0 failed (currently 1078 baseline)
- [ ] New test(s) fail without the fix, pass with it (prove it)
- [ ] `graphify update .` (graph fresh)
- [ ] SCORECARD.md row updated IN THIS BRANCH (domain coverage changed?)
- [ ] Fix log: ISSUES.md / bugs/ entry (bug id, root cause, files, tests)

## Deliverables
- Commits: one logical commit (or small series), present tense, conventional prefix, <72 chars title
- Push: branch to fork origin ONLY. **NO upstream PR** unless Josh explicitly approves (lane gate)
- Report: summary + test evidence + scorecard diff
