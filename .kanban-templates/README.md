# .kanban-templates — AAEmu fork task templates (onboarding-grade)

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
> verification plan. Do not size work in agent turns. Defect chain template:
> 1) test rig + fail-before → 2) implementation + pass-after → 3) census +
> docs + push, each parent-gated on the previous. Every card ends pushed or
> as a deliverable file with an explicit path — never end holding un-pushed
> commits. If a step exceeds ~2/3 budget: push what exists, split the rest
> into a child card. If discovery reveals a second subsystem, schema migration,
> or independent failure mode, split it into a child card before implementation
> expands. **HARD LAW (2026-08-04, iteration exhaustion = design
> failure): deliverable must fit in ONE sentence (no "and then / also /
> plus" — split FIRST, in the SAME batch as the parent); total card ≤ ~60
> turns; TWO timeouts ⇒ mandatory split on the third retry; worker at ~100
> turns ⇒ STOP, push what works, comment what's left. Violating this is a
> card-design bug, not a worker problem.**

## What this is

The canonical task template set for the ArcheAge Slums fork. Every kanban card
for this repo starts from one of these files. New workers/explorers: read this
README, then the template's **Get up to speed** section — first 10 minutes of
any task is orientation, not guessing.

## Pick your template

| Task type | Template | Output lands in |
|-----------|----------|-----------------|
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
