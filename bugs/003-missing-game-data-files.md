# BUG-003 — Missing game data files (compact.sqlite3 + game_pak)

- **Status:** RESOLVED (2026-08-02)
- **Severity:** Expected prerequisite gap — game container cannot boot without them
- **Component:** Server data (client assets)

## Symptom
- `[FATAL] Program - No client worlds data has been found, please check the readme.txt file
  inside the ClientData folder` (game_pak missing)
- `Server database does not exist` (compact.sqlite3 missing)

## Root cause
The Docker guide lists both files as prerequisites; they are NOT in the repo. They must be
sourced from an ArcheAge 1.2 client / the AAEmu wiki download links.

## Fix
- compact.sqlite3 (2026-01-23, official) → `.server_files/AAEmu.Game/Data/`
- game_pak (24.8 GB, Feb 2023 client) → `.server_files/AAEmu.Game/ClientData/`
- ClientData.json already includes `ClientData/game_pak` in its sources — no config change needed
