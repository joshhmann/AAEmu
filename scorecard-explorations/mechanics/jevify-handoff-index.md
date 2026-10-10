# Jevify Session Handoff — index

**Stub only.** This file exists to make the handoff findable from `scorecard-explorations/mechanics/`; it carries no
independent findings.

## The document

→ **[`SESSION-JEVIFY-HANDOFF.md`](../../SESSION-JEVIFY-HANDOFF.md)** — repo root.

That is the single consolidated record of the PlayerBot / Jevify session that ended on a 502. **Read it in full; do
not read this stub as a substitute.**

## What is in it

| § | contents |
|---|---|
| 1 | Provenance — HEAD `626548de`, branch `develop`, 46 dirty (28 tracked + 18 untracked), evidence tiers |
| **2** | **⚠️ Corrections to the prior brief — two material ones, verified against source** |
| 3 | JevShadow instrumentation: the three defects, the 256 wake-slot bug, the exception-path leak, the vacuous test |
| 4 | Allocation contract (554 B / 768 B) and build state |
| 5 | Bot-brain wiring audit — why the judge was the wrong tool; 165 live / 45 unwired; `Belief/` is the one gap |
| 6 | Data integrity — `HoldablesSchemaCheck` fix **present in tree, uncommitted, untested**; `.165` drift |
| 7 | Tooling — `Scripts/jev-shadow-harvest.sh`, `.env.example`, Soak-Runs harvest section |
| 8 | Documentation reconciliation (START-HERE, capability matrix, blockers) |
| 9 | **Three latent, UNFIXED test defects** sharing one singleton-leak root cause |
| 10 | Experiment design — falsifiers, oracle availability per class, cost, the L1/L2/L3 ladder |
| 11 | Next actions, cheapest first |
| 12 | Working judge API facts |
| 13 | Incidental: a 0-byte `compact.sqlite3` at repo root |

## Three things to read first

1. **§2 corrections.** Two claims in the handoff brief that was passed to the writer did not survive checking:
   `EntryPath` **is** in `SCORECARD.md:144` (as an explicitly unadopted proposal), and the dispatch ambiguity is
   **three** claimants, not two. Also: `JevShadowTests` declares **23** tests, not 22. Acting on the uncorrected
   version risks duplicate work.
2. **§3.2 — the pipeline has never been armed.** `AAEMU_JEV_SHADOW` appears nowhere at HEAD outside `JevShadow.cs`
   itself, and no `jev-shadow/v1` dataset exists anywhere on this filesystem. The "does Jev beat our brain" question
   is unanswerable because the dataset is **empty**, not because it is hard or expensive.
3. **§9 — three latent test defects that are NOT fixed**, despite passing. Two of them only pass because another test
   already constructed a shared singleton. Do not read a green gate as evidence these are gone.

## Evidence discipline

Per [`AGENTS.md`](../../AGENTS.md) §"Evidence honesty and anti-overclaim gate (2026-09-16)". The handoff separates
**declared / unit-test-only / rig-tier / live / H** throughout and never upgrades a claim. A commit subject is not
evidence; an exit gate is.

- **Nothing in that document is live-tier.**
- **`H` is UNKNOWN** for everything in the session. No human acceptance anywhere.

The full-gate result (`3920 total / 3919 passed / 0 failed / 1 skipped`, exit 0) is **reported, not
artifact-backed** — `scripts/gate.sh` asserts no test counts and no gate log survives on disk. **Correction
2026-10-01:** the handoff also said no `.trx` survives, and **14 `.trx` files do exist** (all gitignored,
untracked, under `AAEmu.UnitTests/bin/**/net10.0/TestResults/`). The newest,
`…/Release/net10.0/TestResults/unit.trx` (2026-09-24) parses to 3482 total / 3477 passed / 4 failed / 1
notExecuted — a **unit** run whose failing set is non-empty, so it neither matches nor corroborates the quoted
full-gate figures and is not evidence for them. The MCP smoke assertions (44 tools for BotControl, 24 tools /
679 tables for archaeology) **are** verifiable in source, and 44 — not 39 — is what current source expects.

## Provenance of this stub

Written 2026-10-01 by a read-only documentation pass (no builds, no tests, no `gate.sh`, no mutating git, no process or
container changes). Both files are **new and uncommitted**.
