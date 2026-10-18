# Documentation Maintenance

- Audience: Contributors
- Last verified against: documentation-reviewed 2026-10-14 at `develop` / `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8` plus preserved dirty work; no new gameplay verification
- Prerequisites: None

## Goal

Keep the wiki aligned with merged behavior in `develop`.

## How wiki pages publish

Wiki pages are plain Markdown files under `Docs/wiki/` in the repository.
Pushing changes to the `develop` branch triggers
`.github/workflows/wiki-sync.yml`, which publishes the folder to the GitHub
wiki of the repo the workflow runs in:

- Fork `develop` → `joshhmann/AAEmu`'s wiki (the normal lane).
- The upstream `AAEmu/AAEmu` wiki is out of scope. The fork never pushes a
  branch, PR, or wiki change upstream.

So the workflow is: edit files under `Docs/wiki/` → commit on a docs branch →
merge to fork `develop` → the wiki updates on push. There is no separate wiki
editing surface to maintain.

## Update checklist after merged PRs

1. Identify pages impacted by launch, config, networking, packaging, or data
   behavior changes.
1. Update affected pages and cross-links.
1. Add or adjust migration notes when old instructions become invalid.
1. Run doc quality checks.
1. Commit following the contributor guidelines.

**Every touch bumps the `Last verified against` stamp** — the stamp is the
wiki's currency signal, so even a one-line edit must refresh it.

## After design reviews and grilling sessions

Record the decisions and useful findings, not a transcript dump. Update the
existing records in the same work:

| Outcome of the discussion | Record |
|---|---|
| Confirmed choice, reason and non-goals | [DECISIONS](../../.omp/state/DECISIONS.md) |
| Verified finding, source/query and evidence limits | [EVIDENCE](../../.omp/state/EVIDENCE.md) |
| Approved next outcome, dependencies and remaining gaps | [ROADMAP](../../ROADMAP.md), the existing delivery card and [CURRENT](../../.omp/state/CURRENT.md#session-resume) |
| Changed capability or evidence boundary | [STATUS](../../STATUS.md) and [SCORECARD](../../SCORECARD.md); retain historical results |
| Implemented player/bot behavior, setup or configuration | Relevant wiki page, with its verified layer and deployment state |

Keep open ideas and external design inspiration labelled as proposals. Approval
of a direction does not mean its features are implemented or deployed; unit or
fixture proof does not establish live play or human acceptance. Link the original
decision/evidence rather than copying it into every document. Before starting
code, use the [existing discovery-to-delivery contract](../../.kanban-templates/implementation.md)
to turn a sufficiently grounded decision into one bounded outcome.

## Writing conventions

- Use page metadata at top (`Audience`, `Last verified against`).
- Prefer relative Markdown links for internal wiki references.
- Add a short `Related` section on major pages.
- Keep instructions path-based and explicit.
- Prefer `Config.Local.json` examples for machine-specific values.

## Cross-linking model

1. `Home` is the primary table of contents.
1. Setup pages link to config and troubleshooting pages.
1. Troubleshooting pages link back to setup and FAQ.
1. Reference pages link to setup where relevant.

## Related

- [Home](Home)
- [Developer Notes](Developer-Notes)
- [Aspire Development Guide](Aspire-Development-Guide)
