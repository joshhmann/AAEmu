# Dashboard evidence lanes — agent maintenance contract

The Roadmap & Scorecard and Bot Capability tabs (`dashboard_server.py`, tabs
`milestones`/`capabilities`) are
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
| PlayerBot capability evidence + grades (unchanged) | `SCORECARD.md` + progression ladder + capability matrix | Bot Capability evidence lane |
| PlayerBot supported-capability policy | [PROJECT-CONTROL supported-capability contract](../../PROJECT-CONTROL.md#playerbot-supported-capability-contract) | Bot Capability current policy/card and Roadmap scope |
| Approved PlayerBot direction + developed inventory | [PROJECT-CONTROL integration](../../PROJECT-CONTROL.md#playerbot-integration-direction) + [ROADMAP baseline](../../ROADMAP.md#current-developed-capability-baseline) / [approved roadmap](../../ROADMAP.md#playerbot-extension-roadmap) | Capability baseline and Roadmap current goals; implementation/proof remains separately scoped |
| New-session objective / allocation | [CURRENT session resume](../../.omp/state/CURRENT.md#session-resume) + [START-HERE bootstrap](../../START-HERE.md#new-session-bootstrap) | Onboarding/freshness pointers; historical owner/dispatch cards must not override current instructions |
| Operational card/work-record state | `playertrace-coverage/work_board.json` (ignored), seed `work_catalog.json` | Delivery & Sessions; never canonical scope/evidence/grade authority |
| Feature references / selection limits | `work_catalog.json` + registered original sources and current bounded previews | Bibliography; research/recorded proof is not fresh execution |

## Update triggers (any of these MUST refresh the tab in the same wave)

- A milestone state changes (ROADMAP/STATUS/EVIDENCE-LEDGER edited).
- A scorecard grade changes (SCORECARD.md edited).
- A gate count is re-run (new total/pass/fail/skip + SHA).
- An audit lands (new weakest-links / worklist items).
- A version pin or ruling changes (target banner, BUG-005 slot).
- A PlayerBot capability, evidence layer, fixture boundary, or ownership ruling changes.
- The user adopts or supersedes the goals/roadmap, or the new-session source hierarchy
  changes. Distinguish approved direction from proposal, implementation and proof.

## Procedure

1. Edit the tab markup in `dashboard_server.py` (`HTML_TEMPLATE`, milestones/capabilities panes).
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
- Human Trace Tasks are parity/packet evidence only. They never establish autonomous
  PlayerBot progression; the Bot Capability lane must disclose perception, decision,
  execution, verification, recovery, fixture state, and its next proof separately.
- The atlas is an observation surface. Route drawing, candidate generation, export, and
  upload are quarantined developer fixtures: label them non-authoritative and never
  present a fixture route as planner authority or navigation evidence.
- Current ownership copy must reflect actual production binding and named goal owners.
  A historical universal-GOAP diagram or packet-seam prohibition is not current
  inventory. Preserve historical checkpoint cards with explicit dates/layers.

## Delivery/session maintenance

Follow the [delivery/session workflow](README.md#delivery--sessions-workflow).
`work_dashboard.py` owns schema/readiness, file/resource/WIP conflicts, saved
contracts/history, exact displayed-context acknowledgements and atomic metadata.
`dashboard_work.js` owns forms, draft retention, previews/briefs and explicit operator
review; `dashboard_server.py` owns bounded same-origin/token/Host-guarded work routes.
`work_catalog.json` is a portable seed and bibliography, not a rewritten evidence ledger.
Existing human trace data/projections are separate and must remain byte-preserved.

Verify mutations/lifecycle, persistence/restart, failure/review boundaries with the
Python behavioral suite and actual isolated HTTP/browser controls. Registered previews
show current full-source digest and actual bounds; keep original revision/fixture limits.
Do not auto-launch OMP, shell, Game/DB, merge/deploy or canonical state writes. A UI
record's start/pause/close is metadata only; Main controls actual workers/resources.
Acceptance notes/citations are reported, not independently verified. Run the current
card's real proof before changing canonical claims. Static HTML cannot substitute
for the server-backed control surface; regenerate it after template changes.

## Framework note (deferred, not rejected)

The server stays stdlib-only Python (zero external dependencies). The bounded
delivery/session state machine, vanilla JS controls and curated catalog are separated
for a concrete persistence/ownership need, not a framework migration. Work operations
require POSIX safe descriptor reads/flock; legacy hosting remains import-compatible
without them. No authentication or Internet-hosting claim. If 3D ever lands, feed it
offline-baked height grids (`.client_files/survey-grids/`, `Tools/SurveyGridBaker`)
draped with per-zone DDS art, rather than a live-rendered world. No framework migration
without a scoped proposal first.
