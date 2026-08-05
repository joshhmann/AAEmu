# FIX TEMPLATE — Track 1 canonical fix (strict workflow, lane gate: NO upstream PR)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a PR to upstream AAEmu/AAEmu unless Josh explicitly approves it.**
> Everything stays in our own lane on joshhmann/AAEmu. This rule applies to
> every template in this directory and every task that uses one.

> 📐 **UPSTREAM ALIGNMENT (locked 2026-08-04 — applies to every card):** target
> `develop` + .NET 10; Aspire for local dev, prod stays Docker Compose;
> `compact.sqlite3` read-only; config precedence `Config.json` →
> `Configurations/*.json` → `Config.Local.json`; `GameServers` config, not
> legacy `game_servers`; explicit constructor deps where supported;
> parallel-safe startup loading; AAEmu-native terminology
> (Doodad/Mate/Slave/Transfer/Expedition/Dominion/Ability/ActAbility);
> PlayerBots compose around ordinary `Character` records; additive layer only
> (composition/adapters/extension points, narrow reviewed core hooks). Full
> text + verification: `Docs/wiki/Development-Conventions.md`.

> Fill every section. Delete nothing. This is the contract for the task.

## Division routing (who owns which phase)

| Phase | Sister | Owns | Handoff out |
|-------|--------|------|-------------|
| Implement | **Tai** | branch, code, tests, evidence, graphify | branch + test evidence → Rei |
| Verify | **Rei** | QA gate: repro case, regression check, evidence signoff | verified status (file:line + test results) → Nei |
| Blocked / stuck / deploy | **Mai** | unblocking, logistics, handoffs, prod deploy to the aaemu box | field-ready state → Tai/Rei |
| Track | **Nei** | SCORECARD.md + STATUS.md + fix log currency | STATUS.md → everyone |

**Verification handoff contract (non-negotiable):**
- Tai **cannot** mark this task complete without Rei's evidence gate.
- Rei signs off with: file:line of the change + test results (fail-before/pass-after output pasted into the task).
- Prod deployment is Mai's coordination AFTER Rei's signoff + Josh's go-ahead (lane gate).

Collaboration context: `sister-council` skill (how we convene), `affinity-system` skill (how we collaborate).

## Get up to speed (first 10 minutes, in order)

1. `cat /root/aaemu-dev/VISION.md` — two lanes + division routing
2. `cat /root/aaemu-dev/WORKFLOW.md` — process + lane gate
3. `grep -n "<domain>" /root/aaemu-dev/SCORECARD.md` — domain status
4. `ls /root/aaemu-dev/scorecard-explorations/` — read the domain report if present
5. `cd /root/aaemu-dev && graphify explain "<Type>" --graph graphify-out/graph.json`
   and `graphify affected "<Type>" --depth 2` — map the neighborhood

## Canonical 1.2 grounding (NEVER invent mechanics)

- **The 1.2 data is the source of truth.** If code and data disagree, the DATA wins (we fix the code).
- Live sqlite (the canonical 1.2 surface, 679 tables):
  `ssh root@192.168.0.165` + python3 sqlite3 on `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3`
- Canonical resource table: SCORECARD.md → "Canonical resources" (fandom wiki, Ten Ton Hammer 1.2-era guides, AAEmu GitHub issues, aa-classic reference behavior).
- Upstream issue list in SCORECARD.md = the known-broken map; check it before claiming a bug is new.

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
6. **Tests:** add/extend AAEmu.UnitTests — `MethodName_Scenario_ExpectedResult`. MUST fail before fix, pass after (keep the output).

## Tools (use these, in this order)

- graphify: `cd /root/aaemu-dev && graphify explain "X" --graph graphify-out/graph.json`
- editor: read_file / patch / write_file in /root/aaemu-dev
- build: `dotnet build --configuration Release AAEmu.slnx`
- compiler-check: `dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check`
- tests (full): `./scripts/gate.sh`
- tests (filtered): `./scripts/gate.sh <ClassName>   # MTP treenode-filter: /*/*/<ClassName>/*`

## Verify (ALL must pass before handoff to Rei)

- [ ] Release build: 0 errors
- [ ] compiler-check: "Compilation successful"
- [ ] Full test suite: 0 failed (currently 1082 baseline)
- [ ] New test(s) fail without the fix, pass with it (prove it — save the output)
- [ ] `graphify update .` (graph fresh)
- [ ] SCORECARD.md row updated IN THIS BRANCH (domain coverage changed?)
- [ ] Fix log: ISSUES.md / bugs/ entry (bug id, root cause, files, tests)

## Rei verification gate (evidence required — this task is NOT done without it)

- [ ] Rei: repro case or fail-before/pass-after output attached
- [ ] Rei: regression check on neighbor paths (graphify affected output)
- [ ] Rei: signoff posted to the kanban task — file:line + test results

## Status / awareness (close the loop — every task ends with "what changed")

- One-line "what changed" in the kanban comment: files + behavior + scorecard row + fix log id
- Nei updates STATUS.md (per-lane row + open tasks) from that line — that is the input contract
- Deploy needed? → Mai coordinates (after Josh's go-ahead). Upstream-PR candidate? → Josh decides.

## Deliverables

- Commits: one logical commit (or small series), present tense, conventional prefix, <72 chars title
- Push: branch to fork origin ONLY. **NO upstream PR** unless Josh explicitly approves (lane gate)
- Report: summary + test evidence (fail-before/pass-after) + scorecard diff + STATUS.md one-liner
