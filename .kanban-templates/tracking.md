# TRACKING / STATUS CONVENTION — how the fork stays always-current

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a PR to upstream AAEmu/AAEmu unless Josh explicitly approves it.**
> Everything stays in our own lane on joshhmann/AAEmu.

This is Nei's lane (tracks): the "always updated on what's going on" guarantee.
The convention is **event-driven and low-overhead** — updates happen when work
happens, not on a timer.

## The five rules

1. **STATUS.md at repo root** — the one-line "current state" per-lane view.
   Nei updates it whenever something changes: a task completes, a branch merges,
   a deploy happens, an upstream PR status changes. If nothing changed, it doesn't
   move. Format below.
2. **Scorecard rows are touched in the same branch as the work** — never as a
   separate commit/branch. `SCORECARD.md` + `scorecard-explorations/` are living
   docs (WORKFLOW.md v3 tracking discipline).
3. **Fix log** — `ISSUES.md` index row + `bugs/NNN-slug.md` per bug (codified
   format: status, severity, component, discovered-via, symptom, root cause with
   file:line, fix, verification). See `bugs/006-kill-accept-quests.md` as the model.
4. **Exploration reports are committed** to `scorecard-explorations/<domain>.md`
   (see `explorer.md` template) — knowledge lives in the repo, not in chat logs.
5. **Every completed kanban task ends with a one-line "what changed"** (kanban
   comment + STATUS.md input) — that line is the input contract for rule 1.
   Without it, Nei cannot keep STATUS.md current.

## STATUS.md format (lives at repo root, fork-local)

```markdown
# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: <YYYY-MM-DD HH:MM PDT> · by Nei
Branch of record: develop @ <short-sha> · last upstream pull: <date>

## Per-lane
| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | <branch / task> | <state> |
| Verifies | Rei | <gate / review> | <state> |
| Dispatches | Mai | <deploy / unblocking> | <state> |
| Tracks | Nei | <tracking / spec> | <state> |

## Open tasks (kanban, AAEmu lane)
| ID | Title | Lane | Status |
|----|-------|------|--------|

## Pending upstream PRs
- <#NNN> — <title> — <state>

## Last scorecard update
- <date> — <domain> row: <what changed>

## Rules
- STATUS.md is fork-local — never in an upstream PR
- One screen max; if it outgrows, archive the old section (git history keeps it)
- Nei owns it; any sister requests an update by posting a "what changed" one-liner
```

## Low-overhead rules of thumb

- A STATUS.md edit is a one-line diff, made in the same push cycle as the work it
  records (docs branch or direct develop commit — never upstream).
- If a sister forgets the one-liner, Nei asks for it on the task — she does not
  reconstruct state from memory (stale guesses corrupt the board).
- The kanban board stays the live task list; STATUS.md is the *summarized* view —
  don't duplicate card bodies into it.

## Division context

This convention is part of the division operating model (VISION.md → Division
routing; `.kanban-templates/README.md`). Collaboration context: `sister-council`
and `affinity-system` skills.
