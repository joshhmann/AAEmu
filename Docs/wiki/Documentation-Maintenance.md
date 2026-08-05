# Documentation Maintenance

- Audience: Contributors
- Last verified against: `develop` on August 5, 2026
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
