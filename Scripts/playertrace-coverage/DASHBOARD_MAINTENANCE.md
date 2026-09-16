# Dashboard Roadmap & Scorecard tab — agent maintenance contract

The Roadmap & Scorecard tab (`dashboard_server.py`, tab `map`/`milestones`) is a
**rendered view** of the authoritative records. It owns no facts. Every number,
state, and link on it must trace to a record below — or render as OUT-OF-REPO.

## Source hierarchy (authoritative → rendered)

| Fact | Authoritative home | Tab section |
|---|---|---|
| Target client/DB pin | `Docs/wiki/Client.md` + archaeology provenance | Target banner |
| Milestone requirements + gates | `ROADMAP.md` | M0–M10 rows |
| Mechanic grades + evidence | `SCORECARD.md` | Weakest-links, row states |
| Milestone evidence transitions | `EVIDENCE-LEDGER.md` (append-only) | Freshness + claimed states |
| Human-gate ownership | `Docs/wiki/Human-Gate-Field-Guide.md` | H-gate intake |
| Live checkpoint narrative | `STATUS.md` | Freshness line |

## Update triggers (any of these MUST refresh the tab in the same wave)

- A milestone state changes (ROADMAP/STATUS/EVIDENCE-LEDGER edited).
- A scorecard grade changes (SCORECARD.md edited).
- A gate count is re-run (new total/pass/fail/skip + SHA).
- An audit lands (new weakest-links / worklist items).
- A version pin or ruling changes (target banner, BUG-005 slot).

## Procedure

1. Edit the tab markup in `dashboard_server.py` (`HTML_TEMPLATE`, milestones pane).
2. Regenerate the static copy: `bash Scripts/playertrace-coverage/regen_static_dashboard.sh`.
3. Syntax check: `python3 -Werror -m py_compile Scripts/playertrace-coverage/dashboard_server.py`.
4. Serve and screenshot the tab in Chromium (target banner + one scrolled section minimum); confirm other tabs still load.
5. Commit the `.py` + regenerated `dashboard.html` together (never one without the other).

## Rendering rules (from AUDIT-REPO-2026-09-15 — do not regress)

- Scope every evidence letter inline (ledger-A automated vs ladder-A autonomous vs G society vs H/R/S/L/W/C).
- Data living outside the repo renders as OUT-OF-REPO, never as verified.
- H-gate intake stays visually separated from A/R/L evidence; only a Josh run flips H.
- Freshness line carries date + HEAD SHA; out-of-date counts are removed, not left to mislead.
- Weakest-links and code-bug worklists are first-class rows, not footnotes.

## Framework note (deferred, not rejected)

The server stays stdlib-only Python (zero deps, single file) until a concrete
need forces otherwise (auth, multi-user writes, or a 3D atlas). If 3D ever
lands, feed it the offline-baked height grids (`.client_files/survey-grids/`,
`Tools/SurveyGridBaker`) draped with per-zone DDS art — the same recipe the
reference atlas uses — rather than a live-rendered world. No framework migration
without a scoped proposal first.
