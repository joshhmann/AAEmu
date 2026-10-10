---
name: brief-evolution
description: >
  Periodically improve the briefs a director sends to persistent coding-agent
  worker sessions by scoring which brief phrases actually changed worker
  behavior. Use when briefs need tuning, when a worker's output quality drops or
  starts drifting into scope creep and fabricated claims, or before spawning a
  long-lived worker session for a new workstream.
---

# Worker-brief evolution loop

A director spawns one long-lived agent session per **workstream** and follows up
in that same session. Briefs are code, not prose: they must be measured and
edited the same way. This skill is the maintenance loop for those briefs.

Run the loop every **3–5 worker turns**, or immediately when quality drops
(scope creep, unsourced claims, ignored constraints).

## Step 1 — COLLECT

For each recent worker turn record one row per turn:

- **Brief** — the exact text sent.
- **Obeyed / ignored** — which phrases landed, which were skipped, and in what
  way (scope creep? claims without labels? paraphrase instead of verbatim paste?).
- **Verification cost** — how many director read-backs (file re-reads, greps,
  command re-runs) were needed to confirm the claim. 0–1 = cheap, 3+ = expensive.

Write rows to `agent://` notes or a scratch list; do not create repo files.

## Step 2 — SCORE

Mark each phrase in each brief:

| Verdict | Meaning |
| --- | --- |
| **HELPED** | Complied, cheap to verify (0–1 read-backs) |
| **IGNORED** | Skipped entirely |
| **BACKFIRED** | Gamed: letter satisfied, spirit violated (e.g. pasted the requested output but fabricated the claims around it) |

A phrase ignored twice is **IGNORED**; a phrase that once BACKFIRED stays
suspicious — prefer a director-side check.

## Step 3 — PROMOTE / DEMOTE

- **HELPED** — keep; attach one canonical example line from a real turn.
- **IGNORED** — reword **once**: concrete verb + exact artifact
  (`Read AAEmu.Game/…` beats "check the game code"). Ignored again → move to
  the appendix and stop repeating it.
- **BACKFIRED** — delete it, or replace with a director-side check the worker
  cannot perform (director greps, re-runs the check, requires raw output).

Never encode a constraint you do not check yourself.

## Step 4 — TEMPLATE UPDATE

Rewrite the templates below, then bump the version block at the bottom: date +
one-line changelog. Keep the three shapes stable; change phrases, not structure.

### T1 — Read-only analysis brief

```text
Repo: <repo> at <HEAD>. Workstream: <topic>. READ-ONLY: no edits, no commits.
Task: answer <question> from the code only.
Evidence: paste relevant excerpts VERBATIM in fenced blocks with file:line.
Label every statement PROVEN (with the pasted evidence), INFERRED, or UNKNOWN.
Constraints: no push/deploy; no new files; stay inside <area>.
Deliverable: the answer + verbatim excerpts + a one-line UNKNOWN list.
```

### T2 — Implement + test brief

```text
Repo: <repo> at <HEAD>. Workstream: <topic>. IMPLEMENT.
Task: <task>, scoped to <files/area> — do not touch anything else.
Constraints: follow existing patterns; no push/deploy/commit; no drive-by
refactors, no extra tests, no new abstractions "while you're at it".
If a prerequisite is missing, stop and name it — do not stub or fake it.
Evidence: paste the diff hunks VERBATIM and the exact build/test command you
ran with its real output. Label claims PROVEN (output pasted) / INFERRED /
UNKNOWN.
Deliverable: changed files + verbatim diff + verbatim command output.
```

### T3 — Lane-run brief

```text
Repo: <repo> at <HEAD>. Workstream: <lane>. One workstream per session: do not
start unrelated work; report findings and wait.
Task: run <command/flow> and observe <behaviour>.
Constraints: read-only on the repo unless <exception>; no push/deploy; do not
"fix" what you were not asked to fix.
Evidence: paste raw logs VERBATIM (trim only the boilerplate banner) plus the
exact command line. PROVEN for anything a log shows, INFERRED for reasoning,
UNKNOWN for what you could not observe.
Deliverable: raw log + interpretation labeled as above + next-step question.
```

## Appendix — retired phrases

Phrases demoted here (ignored twice). Reuse only with a director-side check.

## Version

- 2026-10-05 — initial: three templates (read-only, implement+test,
  lane-run), COLLECT/SCORE/PROMOTE-DEMOTE loop, BACKFIRED rule.