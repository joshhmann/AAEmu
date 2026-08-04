# .kanban-templates — AAEmu fork task templates (onboarding-grade)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo, and
> it is repeated at the top of every template on purpose):** **NEVER push a PR to
> upstream AAEmu/AAEmu unless Josh explicitly approves it.** Everything stays in
> our own lane on joshhmann/AAEmu.

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

1. `cat /root/aaemu-dev/VISION.md` — two lanes + division routing
2. `cat /root/aaemu-dev/WORKFLOW.md` — process + lane gate
3. `grep -n "<domain>" /root/aaemu-dev/SCORECARD.md` — domain status
4. `ls /root/aaemu-dev/scorecard-explorations/` — read the domain report if present
5. `cd /root/aaemu-dev && graphify explain "<Type>" --graph graphify-out/graph.json`
   and `graphify affected "<Type>" --depth 2` — map the neighborhood

Never skip 5 — the graph is how we find blast radius before touching code.

## Base docs (read in order of need)

| Doc | Role |
|-----|------|
| `VISION.md` | strategy: two lanes + division routing |
| `WORKFLOW.md` | Tai's playbook v3: process, gate, deploy, pitfalls |
| `SCORECARD.md` | 679-table canonical surface vs code wiring; canonical resources table |
| `scorecard-explorations/` | per-domain nitty-gritty reports |
| `COMMUNITY-GUIDELINES.md` | upstream-PR compliance layer (only relevant with Josh's go-ahead) |
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
