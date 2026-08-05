# EXAMPLE — feature.md filled in (SHAPE EXAMPLE: premium → labor regen wiring)

> ⚠️ SHAPE EXAMPLE — this is a realistic fill of the feature.md v2 template,
> grounded in the REAL zero-wired-domains explorer report (premium domain,
> 4 tables, 0% wired). It is NOT a queued task. Use it to see the expected
> depth and shape for a Track 2 feature card.

# FEATURE TEMPLATE — Track 2 / our-lane feature (strict workflow, fork-only)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a branch or open a PR to upstream AAEmu/AAEmu.**
> Everything stays in our own lane on joshhmann/AAEmu. This rule applies to
> every template in this directory and every task that uses one.

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
- Prod deployment is Mai's coordination after Rei's signoff and the deployment decision.

Collaboration context: `sister-council` skill (how we convene), `affinity-system` skill (how we collaborate).

## Get up to speed (first 10 minutes, in order)

1. `cat /root/aaemu-dev/VISION.md` — two lanes + division routing
2. `cat /root/aaemu-dev/WORKFLOW.md` — process + lane gate
3. `grep -n "premium" /root/aaemu-dev/SCORECARD.md` → premium: 4 tables / 0 wired / 0% — zero-data-wired domain
4. `ls /root/aaemu-dev/scorecard-explorations/` → zero-wired-domains.md present — read the premium section (smallest meaningful slice: "Read benefits/grades into manager; drive labor from premium_benefits instead of hardcoded 5000")
5. `cd /root/aaemu-dev && graphify explain "LaborManager" --graph graphify-out/graph.json` and `graphify affected "LaborManager" --depth 2`

## Canonical 1.2 grounding (NEVER invent mechanics)

- **The 1.2 data is the source of truth.** If code and data disagree, the DATA wins (we fix the code).
- Live sqlite: `ssh root@192.168.0.165` + python3 sqlite3 on `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3`
  → premium_benefits (2 rows), premium_configs (1), premium_grades (2), premium_points (0 rows).
- Canonical resources: SCORECARD.md "Canonical resources" table — premium/labor behavior reference: fandom wiki labor page, Ten Ton Hammer 1.2-era labor guide.
- A Track 2 feature adds what the 1.2 data already defines — never invents parallel mechanics.

## Feature

- **Vision link:** VISION.md (Track 2 — our lane: the world feels alive; economy sim feeds on correct labor)
- **What players experience (user story):** as a player with premium status, my labor regen follows the 1.2 premium_benefits rates instead of a hardcoded flat rate — and the server reads the same numbers the client expects
- **Domain touched:** premium (scorecard row: 4 tables / 0% wired) + labor (existing system)
- **Canonical data:** premium_benefits (2 rows), premium_grades (2), premium_configs (1), premium_points (0 — read-only reference; dynamic points stay in MySQL)

## Plan (order matters)

1. **Branch:** `feat/premium-labor` off develop
2. **Understand:** scorecard-explorations/zero-wired-domains.md (premium §) + `graphify explain/affected` on LaborManager + read LaborManager and its DI registration
3. **Design:** additive layer ONLY — new `PremiumManager` (loads premium_* tables, mirrors 1.2 rows) hooks into labor regen via the existing manager interface; no core-interface edits
4. **Implement:** commits per logical step: PremiumManager + loaders → labor integration → packets (if premium state is client-visible) → tests
5. **Wire-up:** register PremiumManager concrete + interface in Program.cs like peers (ILoadable)
6. **SQL:** none for reference data (sqlite loaders); dynamic premium points (if needed) → SQL/updates/… + base SQL/aaemu_game.sql
7. **Tests:** AAEmu.UnitTests per step — `PremiumManager_Load_ReadsBenefits`, `LaborRegen_UsesPremiumBenefit_WhenPremium`, `LaborRegen_FallsBackToBase_WhenNotPremium`

## Tools (use these, in this order)

- graphify: `cd /root/aaemu-dev && graphify explain "X" --graph graphify-out/graph.json`
- scorecard: `python3 /tmp/scorecard2.py` (regenerate) — update SCORECARD.md in THIS branch (premium 0% → N%)
- build: `dotnet build --configuration Release AAEmu.slnx`
- compiler-check: `dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check`
- tests (full): `./scripts/gate.sh`
- tests (filtered): `./scripts/gate.sh LaborManager`
- live sqlite queries (for data understanding): ssh root@192.168.0.165 + python3 sqlite3 on /root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3

## Verify (ALL must pass before handoff to Rei)

- [ ] Release build: 0 errors
- [ ] compiler-check: "Compilation successful"
- [ ] Full test suite: 0 failed (currently 1082 baseline)
- [ ] New tests cover each implemented step
- [ ] `graphify update .` (graph fresh)
- [ ] SCORECARD.md updated IN THIS BRANCH (premium row + coverage %)
- [ ] Exploration report updated (zero-wired-domains.md premium section — mark wired)
- [ ] Lane separation respected: no changes that would make upstream sync painful

## Rei verification gate (evidence required — this task is NOT done without it)

- [ ] Rei: feature behavior verified against acceptance criteria (premium labor rates match 1.2 data)
- [ ] Rei: regression check on neighbor paths (labor regen for non-premium players)
- [ ] Rei: signoff posted to the kanban task — file:line + test results

## Status / awareness (close the loop — every task ends with "what changed")

- One-line: "feat/premium-labor: PremiumManager reads premium_benefits/grades/configs from compact.sqlite3; labor regen uses premium rate for premium players, base fallback otherwise; SCORECARD premium row 0%→N%; zero-wired-domains.md updated; tests N/N; deploy pending Josh (lane gate)."
- Nei: STATUS.md per-lane rows updated from that line.

## Deliverables

- Commits: per logical step, present tense, conventional prefix, <72 chars
- Push: branch to fork origin ONLY (fork develop merge after green). **NO upstream PR.**
- Report: summary + test evidence + scorecard diff + deploy note (Mai coordinates)
