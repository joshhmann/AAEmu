# ArcheAge Slums — Fork Vision & Lane Strategy

> **🚫 THE TOP LINE (Josh, 2026-08-03 — permanent, non-negotiable):**
> **NEVER push a PR to upstream AAEmu/AAEmu unless Josh explicitly approves it.**
> Everything below happens in our own lane on joshhmann/AAEmu. This rule
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

### Lane 1 — upstream (community)
- Clean bug fixes, following AAEmu community guidelines exactly
- PR'd to AAEmu/AAEmu (their CI gates + Greptile review — see WORKFLOW.md)
- Small, focused, present-tense, evidence-first
- Example: glibc Dockerfile fix (#1494) — merged-able shape
- KEEP THIS LANE CLEAN. It's our reputation + our upstream sync path.

### Lane 2 — our fork (product)
- Everything in the vision above: bots, LLM, economy sim, simulated PvP
- Lives on feature branches off `develop`, merged into our fork develop
- NEVER pushed upstream unless the community explicitly wants it
- Infrastructure: bots need resources (LLM calls, tick budget, DB) — this is
  where we invest the real engineering

## Branch / workflow rules

- `develop` = our product line (fork). Upstream merges land here via
  `git pull upstream develop`, resolved, committed.
- `fix/*` branches = upstream lane candidates (small, PR-able)
- `feat/*` branches = our lane (bots, LLM, economy, sieges)
- Every PR to upstream: single squash commit, CI gates green locally first.
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
