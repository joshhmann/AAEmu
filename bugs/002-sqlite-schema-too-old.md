# BUG-002 — compact.sqlite3 schema too old for develop branch

- **Status:** FIXED (2026-08-02)
- **Severity:** High — game server FATAL on startup
- **Component:** Server data (compact.sqlite3)

## Symptom
- `[FATAL] SQLite - Server database does not exist: /app/Data/compact.sqlite3` then
- `SQLite Error 1: 'no such table: item_socket_chances'` in ItemManager.Load

## Root cause
2019-era compact.sqlite3 has 635 tables; the develop branch schema needs 679 (adds
`item_socket_chances` etc.). Old data file = crash later in boot, not at file-exists check.

## Fix
Replaced with the official current pack from the AAEmu wiki "Dependencies and Downloads"
page: `ArcheAge_Server_Compact_r208088_v1.2.4.13_update_2026-01-23.7z` (MEGA link; 17.5 MiB
archive → 114 MiB sqlite). Downloaded with `megatools` (`apt install megatools`), extracted
with `p7zip-full`.

## Note
The wiki page is JS-rendered; use `https://raw.githubusercontent.com/wiki/AAEmu/AAEmu/<Page>.md`
to read it as plain markdown and copy links.
