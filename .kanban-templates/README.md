# .kanban-templates — AAEmu fork task templates (onboarding-grade)

## Evidence and loop-completeness handoff (2026-09-16)

- Implemented:
- Verified scenario and evidence layer (synthetic / deterministic rig / live / human):
- Seeded setup, mocks, manual state changes or other bypasses:
- Not proved:
- Required loop legs and missing/partial/blocked/unknown dependencies:
- Smallest next task for each missing requirement (N/A only with a reason):
- Exact SHA + dirty state, command/environment, artifact/input provenance:
- Independent review and deployment state (unknown/not run if absent):

If any required leg is unproved, headline **LOOP INCOMPLETE**. A synthetic pass
cannot establish an actual gameplay loop; an evaluator cannot upgrade its input's
evidence layer. Follow [AGENTS.md](../AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16)
and the [priority queue](../ROADMAP.md#high-priority-corrective-queue--2026-09-16).

## Current slice contract (2026-09-15)

Use [PROJECT-CONTROL](../PROJECT-CONTROL.md#delivery-contract) and the
[current queue](../ROADMAP.md#near-term-slice-queue), not historical board order.
Fill: parent/outcome; SHA + dirty baseline; scope/non-goals; initial state and seed
disclosure; acceptance + evidence layer; dependencies; implementer/verifier;
stop/handoff. One reviewable outcome per card, not one class or entire subsystem.
Report implementation, verification, deployment, and human acceptance separately.
Existing workflow gates still apply; no signoff or deployment permission is implied.

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo, and
> it is repeated at the top of every template on purpose):** **NEVER push a
> branch or open a PR to upstream AAEmu/AAEmu.** Upstream is intake-only;
> everything we produce stays on joshhmann/AAEmu.

> 📐 **UPSTREAM ALIGNMENT (Josh, locked 2026-08-04 — applies to every card):**
> 1) target develop + .NET 10; 2) Aspire for local dev, prod stays Docker
> Compose; 3) `compact.sqlite3` read-only — mutable state in MySQL/additive
> schema; 4) config precedence Config.json → Configurations/*.json →
> Config.Local.json, no secrets in shared config; 5) `GameServers` config, not
> legacy `game_servers`; 6) explicit constructor deps where supported; 7)
> parallel-safe startup loading; 8) AAEmu-native terminology (Doodad/Mate/
> Slave/Transfer/Expedition/Dominion/Ability/ActAbility); 9) PlayerBots
> compose around ordinary Character records — no parallel gameplay paths; 10)
> additive layer = composition/adapters/extension points first, narrow
> reviewed core hooks only. Full text + verification: ROADMAP.md,
> WORKFLOW.md, Docs/wiki/Development-Conventions.md.

> ✂️ **CARD SIZING (Josh, locked 2026-08-04 — break steps down):** one card =
> one independently reviewable outcome with one primary risk and a bounded
> verification plan. Do not size work in agent turns. **2026-09-15 operational
> clarification:** reproduction, implementation, regression proof, and records
> normally belong to the same outcome slice. Split a large investigation or an
> independently useful prerequisite when it has its own acceptance boundary.
> A cross-layer change is not automatically too large; a second independent
> outcome or primary risk is the signal to split. Repeated timeouts trigger a
> scope/blocker review, not an automatic push of incomplete work. Hand off exact
> files, evidence, remaining work, and review state. Push/deploy only when authorized
> and applicable gates hold. This replaces the former 60/100-turn sizing heuristics.

## What this is

The canonical task template set for the ArcheAge Slums fork. Every kanban card
for this repo starts from one of these files. New workers/explorers: read this
README, then the template's **Get up to speed** section — first 10 minutes of
any task is orientation, not guessing.

## Common implementation contract

Every new zone, feature, mechanic or journey uses
[implementation.md](implementation.md) as the shared **discovery-to-delivery process**:
scope → inventory archaeology/code/tests/corpus → reconcile gaps → order slices →
implement/verify → reconcile and expand. Use a parent scope brief, then link it from
bounded feature/fix cards. Fill once in the existing card/dossier; do not duplicate
it across documents. Next-zone work reuses proved shared mechanics and verifies the
local delta. Pure plumbing/UI uses an observable outcome with justified N/A loop fields.
The first filled product application is the
[owned-small-plot journey](../ROADMAP.md#first-owned-small-plot-journey).

## Pick your template

| Task type | Template | Output lands in |
|-----------|----------|-----------------|
| Every implementation | `implementation.md` + relevant feature/fix workflow | Existing card/dossier: outcome, gaps, acceptance, honest handoff |
| Bug fix (Track 1, upstream-shaped) | `fix.md` | branch + tests → Rei gate → scorecard + `bugs/NNN` |
| Feature (Track 2, our lane) | `feature.md` | branch + tests → Rei gate → scorecard |
| Deep-dive / recon (knowledge, no code) | `explorer.md` | `scorecard-explorations/<domain>.md` |
| Zone/instance content audit | `zone-audit.md` | `scorecard-explorations/zones/<zone-key>.md` + zone ledger |
| Status / currency convention | `tracking.md` | `STATUS.md` at repo root |

## The division (who owns which phase)

| Phase | Sister | Owns | Handoff out |
|-------|--------|------|-------------|
| Implement | **Tai** | branch, code, tests, evidence, graphify, the fork | branch + test evidence → Rei |
| Verify | **Rei** | QA gate: repro, regression, evidence signoff (fail-before/pass-after) | verified status (file:line + tests) → Nei |
| Dispatch / support / deploy | **Mai** | runtime support, stuck-worker rescue, handoffs, prod deploy to the aaemu box | field-ready state → Tai/Rei |
| Track | **Nei** | roadmap, spec, PM state, scorecard/STATUS currency, continuity | STATUS.md + scorecard → everyone |

Two non-negotiables:
- **Tai cannot mark a fix/feature complete without Rei's evidence gate.**
- **Blocked or stuck → Mai** (she owns the "who's blocked" picture).

Collaboration context: `sister-council` skill (how we convene), `affinity-system` skill (how we collaborate).

## First 10 minutes (every task, every sister, in order)

> **WORKSPACE:** never work in `/root/aaemu-dev` (Tai's shared tree). Use YOUR
> card workspace. A shared local clone initially points `origin` at the seed
> directory, so reset its remotes before any push:
>
> ```bash
> git clone --shared /root/aaemu-dev <workspace>/repo
> cd <workspace>/repo
> git remote set-url origin https://github.com/joshhmann/AAEmu.git
> git remote add upstream https://github.com/AAEmu/AAEmu.git
> git remote set-url --push upstream DISABLED
> cp -r /root/aaemu-dev/graphify-out ./
> ```
>
> The graph copy is read-only input until `graphify update .` is intentionally
> run in the card clone. Commit and push only to the fork from there.

1. `cat <repo>/VISION.md` — two lanes + division routing
2. `cat <repo>/WORKFLOW.md` — process + one-way upstream gate
3. `grep -n "<domain>" <repo>/SCORECARD.md` — domain status
4. `ls <repo>/scorecard-explorations/` — read the domain report if present
5. `cd <repo> && graphify explain "<Type>" --graph graphify-out/graph.json`
   and `graphify affected "<Type>" --depth 2` — map the neighborhood
6. `git remote -v` — verify `origin` is the fork and upstream's push URL is
   `DISABLED`; if not: `git remote set-url --push upstream DISABLED`

Never skip 5 — the graph is how we find blast radius before touching code.

## Base docs (read in order of need)

| Doc | Role |
|-----|------|
| `VISION.md` | strategy: two lanes + division routing |
| `WORKFLOW.md` | Tai's playbook v4: process, one-way gate, deploy, pitfalls |
| `SCORECARD.md` | 679-table canonical surface vs code wiring; canonical resources table |
| `scorecard-explorations/` | per-domain nitty-gritty reports |
| `COMMUNITY-GUIDELINES.md` | historical upstream-awareness reference; never authorizes an outbound PR |
| `AGENTS.md` | repo architecture, conventions, task routing |
| `ISSUES.md` + `bugs/` | fix log (index + one file per bug) |

## Examples (real filled shapes)

- `examples/fix-example.md` — BUG-006 kill-acceptor quest fix (real completed task, t_71e48494)
- `examples/feature-example.md` — shape example: premium → labor regen wiring (grounded in the zero-wired-domains report; NOT a queued task)
- `examples/explorer-example.md` — quests explorer deep-dive (real report, `scorecard-explorations/quests.md`)

## Status convention (one line)

Every task ends with a "what changed" one-liner → Nei keeps `STATUS.md` current.
Full convention: `tracking.md`.

## Maintenance

- Templates are fork-local — never included in upstream PRs.
- Update a template when a workflow lesson lands (e.g. new gate, new pitfall) —
  one commit, `docs: template ...` prefix, on the docs lane.
- The lane gate rule text at the top is canonical; do not soften it.
