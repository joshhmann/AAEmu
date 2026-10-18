# Developer Notes

- Audience: Contributors
- Last verified against: local focused-check commands on 2026-10-16, `develop` / `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8` plus dirty work; not full qualification or gameplay acceptance
- Prerequisites: None

## Fast focused development checks

During a bounded local repair, compile and test the affected class without
waiting for full static analysis on every edit:

```bash
./scripts/dev-test.sh unit BotActionCommandQueueTests
./scripts/dev-test.sh integration BotShortRunObservationBudgetTests
```

Both arguments are required. The command builds the selected project and its
dependencies in Release, then runs only its isolated test module. It selects
TUnit's `--treenode-filter` for unit tests and xUnit's `--filter-query` for
integration tests. Choose a specific class; quote any intentional wildcard.
Tests that require a live server still need their separately authorized,
isolated prerequisites. This command does not supply them or authorize a run.

Static diagnostic analyzers are deferred **only for this command** using
`RunAnalyzersDuringBuild=false`. Compilation, compiler diagnostics and source
generators still run. Build/test failures and zero-test selections fail the
command. Skipped tests count toward the minimum selection: inspect the reported
pass/fail/skip counts; an all-skipped run proves no behavior.

Reusable outputs and intermediates live in `.artifacts/dev-test/`, separate
from ordinary `bin/` and `obj/`. Each invocation prints its persistent log
directory under `.artifacts/dev-test/logs/` and streams build/test stdout and
stderr. Run one development build at a time per checkout because the cache is
shared across projects. Cold builds cost more than subsequent focused runs.

**This is not the qualification gate.** Once the candidate is stable, run:

```bash
./scripts/gate.sh
```

Normal analyzer policy is unchanged. The gate retains full stage logs in its
printed `/tmp/aaemu-gate.*` directory and still includes the in-game script
compiler, full unit suite and MCP smokes. Required domain checks, including the
archaeology cycle when applicable, still apply. Fast-check success cannot
replace final qualification or earned player/bot-loop acceptance. The known
analyzer runtime problem remains unresolved.

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
