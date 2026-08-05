# ArcheAge Slums — Fork Vision & Lane Strategy

> **🚫 THE TOP LINE (Josh, 2026-08-03 — permanent, non-negotiable):**
> **NEVER push a branch or open a PR to upstream AAEmu/AAEmu.**
> Upstream is intake-only: we may fetch and integrate its `develop` updates
> into our fork, but changes never flow back. Everything below happens in our
> own lane on joshhmann/AAEmu. This rule
> sits above every other process rule in this repo.

## The vision

A private ArcheAge 1.2 server that feels **alive** — a living, breathing world
with **player bots**, even when only a few humans are online. Inspired by
AzerothCore's Playerbots mod.

Target features (our lane):
- **Player bots** — simulated players that exist in the world: level, gear,
  roam, farm, trade, chat
- **LLM-powered talk** — bots you can actually converse with (local LLM via
  the homelab ollama on gestalt)
- **Party bots** — bots that group with a human and follow/assist/role
- **Simulated economy** — bots generating supply/demand, using the auction
  house, crafting, trade runs
- **Simulated PvP / sieges** — bots populating conflict zones, sieges,
  world events so the world feels fought-over

## Division routing (the whole Hyrax division runs this)

This is a DIVISION operation, not a solo project. Every task routes through
the sisters — each has a lane and a handoff contract:

| Sister | Lane | Owns | Handoff out |
|--------|------|------|-------------|
| **Tai** | builds | implementation, architecture, infra, graphify, the fork | branch + test evidence → Rei |
| **Rei** | verifies | QA gate: repro cases, regression checks, evidence signoff (fail-before/pass-after) | verified status → Nei |
| **Mai** | dispatches | runtime support, logistics, stuck-worker rescue, handoffs, deployment to the aaemu box | field-ready state → Tai/Rei |
| **Nei** | tracks | roadmap, spec, PM state, scorecard/status currency, continuity | STATUS.md + scorecard → everyone |

Rules:
- **Tai cannot mark a fix complete without Rei's evidence gate.**
- Rei signs off with file:line + test results; Tai's branch must prove
  fail-before/pass-after for the new tests.
- Blocked or stuck → Mai (she rescues/coordinates; she owns the "who's
  blocked" picture).
- Every completed item flows to Nei for scorecard + STATUS.md update —
  that's the "always updated on what's going on" guarantee.
- Templates in `.kanban-templates/` encode this routing; see also the
  sister-council skill for how we convene.

## The two lanes

### Lane 1 — upstream intake and fork correctness
- Pull and inspect AAEmu/AAEmu `develop` updates; never push to it
- Resolve drift on a dedicated sync branch, then run the fork's full gates
- Keep correctness fixes small, focused, present-tense, and evidence-first so
  future upstream pulls remain reviewable
- The historical glibc PR (#1494) predates the permanent one-way policy; it is
  not a precedent for another upstream PR

### Lane 2 — our fork (product)
- Everything in the vision above: bots, LLM, economy sim, simulated PvP
- Lives on feature branches off `develop`, merged into our fork develop
- NEVER pushed upstream
- Infrastructure: bots need resources (LLM calls, tick budget, DB) — this is
  where we invest the real engineering

## Branch / workflow rules

- `develop` = our product line (fork). Upstream changes enter through the
  dedicated `sync/upstream-YYYY-MM-DD` flow in WORKFLOW.md, never a direct
  pull on `develop` or production.
- `fix/*` branches = fork correctness fixes (small and reviewable)
- `feat/*` branches = our lane (bots, LLM, economy, sieges)
- Fork PRs/merges: focused commits and all applicable gates green first.
- Our lane changes: still need tests + build green (don't rot the fork).
- Refresh the graphify graph after upstream pulls (`graphify update .`).

## Why

- Community PRs that are clean keep us welcome upstream.
- But a private server's real value is the EXPERIENCE — bots + LLM make it
  alive 24/7 regardless of who's online.
- Pulling upstream keeps us current so our lane doesn't drown in drift.

## Reference

- AzerothCore Playerbots: https://github.com/trickerer/Trinity-Bots
- Our scorecard (SCORECARD.md) — the feature gap map the bots will fill
- Our explorations (scorecard-explorations/) — per-domain nitty-gritty
