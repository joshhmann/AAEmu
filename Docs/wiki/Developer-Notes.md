# Developer Notes

- Audience: Contributors
- Last verified against: `develop` on August 5, 2026
- Prerequisites: None

## August 2026 — M0/M1-era notes (fork)

The fork's milestone work (see [Project Status](Project-Status)) has produced
contributor-facing machinery worth knowing about:

- **Quest sanity verifier (BUG-007)** — at startup, `QuestManager.Load`
  cross-checks quest templates and fails loudly on unknown act types, broken
  references, and orphaned rows (14 tests).
- **Scenario harness + census (M1-5b)** — engine-level full-lifecycle quest
  driver in `AAEmu.UnitTests` (START → PROGRESS → READY → REWARD → PERSIST)
  with per-quest verdicts; see [Quest Test Harness](Quest-Test-Harness).
- **Local quality gate** — `scripts/gate.sh` is the real gate: Release build +
  in-game script compiler-check + full unit test suite. Upstream CI on fork
  PRs is unreliable; run the gate locally.
- **Graph refresh after pulls** — `graphify update .` keeps the semantic code
  graph (`graphify-out/graph.json`) in sync with the tree.
- **Fix-log convention** — every bug fix logs to `bugs/NNN-*.md` (BUG-006 …
  BUG-010 so far) and updates the scorecard in the same commit.
- **Scorecard fork-fixes layer** — SCORECARD.md tracks which of the 679 SQL
  tables are wired vs. stubbed, plus a fork-fixes layer documenting our
  deviations from the upstream surface.

## Architecture notes: manager DI and parallel loading

PRs `#1363` and `#1366` migrated manager construction toward dependency
injection and completed follow-up fixes for parallel loading.

What this means for contributors:

- Manager dependencies are increasingly explicit in constructors.
- Startup manager loading is now less manual and more dependency-driven.
- Parallel initialization surfaced and fixed some concurrency issues.

Operational impact for wiki setup docs is small:

- No user-facing launch command change beyond existing setup workflows.
- Main effects are internal maintainability, testability, and startup behavior.

## Related

- [Home](Home)
- [Project Status](Project-Status)
- [Quest Test Harness](Quest-Test-Harness)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Components](Components)
