# PLAYERBOT_BLOCKER ledger

When a bot cannot continue playing normally, it files a blocker here.
Blockers outrank speculative features in the backlog. Layer tags:
BOT-SIDE / SERVER / DATA / UNKNOWN.

Format: ID · scenario · intended action · observed vs expected · layer ·
evidence · status (OPEN/FIXED/WONTFIX-with-reason).

---

## OPEN

### PB-001 · Straight-line movement blocks interior/travel gameplay
- Scenario: bot travels beyond open courtyards (Deadmine tunnels, cross-region routes)
- Intended: navigate terrain/obstacles to reach objective
- Observed: straight-line walk; stuck detection fires (M7#5) but no route exists
- Layer: BOT + SERVER (no navmesh/waypoint network)
- Evidence: M7 spike shortcuts on record; soak run-1 drowning (fixed at home-anchor level)
- Status: OPEN — waypoint-network or coarse-route design needed before dungeon interiors

### PB-002 · Progression ceiling: no viable quest content past curated Solzreed slice for bots
- Scenario: bot finishes golden-route chain (~lvl 20 equivalent), seeks next quests
- Intended: continue leveling via real quest content
- Observed: bots provision artificial levels; no autonomous next-quest selection
- Layer: DATA + BOT (quest discovery/perception primitive missing: "find available quests at my level nearby")
- Evidence: adventurer v1 runs curated chains only
- Status: OPEN — needs QuestDiscovery perception primitive + zone sweep of runnable content

### PB-003 · Zone 46 Hadir Farm has no exit portal data
- Scenario: party clears Hadir Farm, wants to leave
- Intended: exit doodad back to main world
- Observed: zone ships no exit doodad spawn data (4289/4927 absent); no indun_events to spawn one
- Layer: DATA (compact.sqlite3 gap vs canonical)
- Evidence: indun-party-e2e-report.json; dossier indun-domain.md
- Status: OPEN — SQL patch candidate after canonical verification

## FIXED (evidence retained)

### PB-F1 · Duelists stuck IsInDuel forever when flag spawn fails
- Found by DuelManagerRigTests; RestoreFaction bare indexer + flag delete NRE inside stop catch-all
- Fixed f8252a37b

### PB-F2 · Environmental deaths (null killer) crashed mid-death
- Found by PartyLifecycleFaultMatrixTests; Unit.DoDie/CharacterCombat null-guards added c011e8a24

### PB-F3 · Journal-report gate auto-passed (466 quests)
- ConReportJournal `|| true` stub; fixed cab6e4dc9

### PB-F4 · Transfers could never be boarded (TlId shadowing)
- Fixed 3a534b539; live ride E2E green
