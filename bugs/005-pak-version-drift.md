# BUG-005 — game_pak (2023) vs compact.sqlite3 (2026) version drift

- **Status:** OPEN / watch item
- **Severity:** Low now, content-parity risk later
- **Component:** Client data vs server data versions

## Detail
- Server opcodes: `client_12_r208022`
- compact.sqlite3: r208088 (2026-01-23)
- game_pak: Feb 2023 client build (24.8 GB)

DB content may reference client assets the older pak lacks (or vice versa) — possible
missing models/textures, quest data quirks, or content gaps.

## Options
- (a) Leave as-is until something visibly breaks
- (b) Fetch the matching-era client/pak from the wiki MEGA links and re-test
- (c) Re-sync both files to the same revision for full parity

## Verification
Watch for: missing NPC models, invisible doodads, quests that can't complete, texture gaps.
