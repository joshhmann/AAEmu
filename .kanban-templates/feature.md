# FEATURE TEMPLATE — Track 2 / our-lane feature (strict workflow, fork-only)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a branch or open a PR to upstream AAEmu/AAEmu.** Upstream is
> intake-only; everything stays on joshhmann/AAEmu. This rule applies to
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
| Track | **Nei** | SCORECARD.md + STATUS.md + exploration report currency | STATUS.md → everyone |

**Verification handoff contract (non-negotiable):**
- Tai **cannot** mark this task complete without Rei's evidence gate.
- Rei signs off with: file:line of the change + test results (fail-before/pass-after output pasted into the task).
- Prod deployment is Mai's coordination after Rei's signoff and the explicit
  deployment decision; this never authorizes an upstream push or PR.

Collaboration context: `sister-council` skill (how we convene), `affinity-system` skill (how we collaborate).

## Get up to speed (first 10 minutes, in order)

1. `cat <repo>/VISION.md` — two lanes + division routing
2. `cat <repo>/WORKFLOW.md` — process + one-way upstream gate
3. `grep -n "<domain>" <repo>/SCORECARD.md` — domain status
4. `ls <repo>/scorecard-explorations/` — read the domain report if present
5. `cd <repo> && graphify explain "<Type>" --graph graphify-out/graph.json`
   and `graphify affected "<Type>" --depth 2` — map the neighborhood

## Canonical 1.2 grounding (NEVER invent mechanics)

- **The 1.2 data is the source of truth.** If code and data disagree, the DATA wins (we fix the code).
- Live sqlite (the canonical 1.2 surface, 679 tables):
  `ssh root@192.168.0.165` + python3 sqlite3 on `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3`
- Canonical resource table: SCORECARD.md → "Canonical resources" (fandom wiki, Ten Ton Hammer 1.2-era guides, AAEmu GitHub issues, aa-classic reference behavior).
- A Track 2 feature adds what the 1.2 data already defines — never invents parallel mechanics.

## Feature

- **Vision link:** VISION.md (lane: Track 2 bots / other)
- **What players experience (user story):**
- **Domain touched:** (scorecard domain, e.g. siege/ranks/premium)
- **Mechanic IDs touched:** (SCORECARD.md global ledger; add a stable ID if new)
- **Zone keys touched:** (`zone_group_id` + name or `instance_*`; `global` if none)
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

- graphify: `cd <repo> && graphify explain "X" --graph graphify-out/graph.json`
- scorecard: update mechanic/zone evidence rows from reproducible queries and
  artifacts; do not depend on an untracked `/tmp` generator
- build: `dotnet build --configuration Release AAEmu.slnx`
- compiler-check: `dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check`
- tests (full): `./scripts/gate.sh`
- tests (filtered): `./scripts/gate.sh <ClassName>   # MTP treenode-filter: /*/*/<ClassName>/*`
- live sqlite queries (for data understanding): ssh root@192.168.0.165 + python3 sqlite3 on /root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3

## Verify (ALL must pass before handoff to Rei)

- [ ] Release build: 0 errors
- [ ] compiler-check: "Compilation successful"
- [ ] Fast local gate: 0 failed (do not hard-code a stale test-count baseline)
- [ ] CI-parity coverage + Login integration gates: 0 failed before merge
- [ ] New tests cover each implemented step
- [ ] `graphify update .` (graph fresh)
- [ ] SCORECARD.md evidence grades/links updated in this branch when changed;
      table-wiring percentage is not treated as feature completion
- [ ] Exploration report updated if the feature changes the domain picture
- [ ] Lane separation respected: no changes that would make upstream sync painful

## Rei verification gate (evidence required — this task is NOT done without it)

- [ ] Rei: feature behavior verified against the acceptance criteria (user story)
- [ ] Rei: regression check on neighbor paths (graphify affected output)
- [ ] Rei: signoff posted to the kanban task — file:line + test results

## Status / awareness (close the loop — every task ends with "what changed")

- One-line "what changed" in the kanban comment: files + behavior + scorecard row + exploration diff
- Nei updates STATUS.md (per-lane row + open tasks) from that line — that is the input contract
- Deploy needed? → Mai coordinates after the deployment decision. There are no
  upstream-PR candidates; upstream is intake-only.

## Deliverables

- Commits: per logical step, present tense, conventional prefix, <72 chars
- Push: branch to fork origin ONLY (fork develop merge after green). **NO upstream PR.**
- Report: summary + test evidence + scorecard diff + deploy note
