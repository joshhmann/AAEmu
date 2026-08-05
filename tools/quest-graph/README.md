# Game-data knowledge graphs

Queryable knowledge graphs over the ArcheAge 1.2 reference data + engine code,
built in graphify's graph.json format.

## Build

```bash
python3 tools/quest-graph/build-quest-graph.py <compact.sqlite3>      # quest data graph
python3 tools/gamedata-graph/build-gamedata-graph.py <compact.sqlite3> # all content tables
graphify merge-graphs graphify-out/graph.json graphify-out/quest-graph.json \
  graphify-out/gamedata-graph.json --out graphify-out/merged-graph.json
```

Outputs land in `graphify-out/` (gitignored; regenerable from the DB).

## Node/edge model

- Every content row is a node (`<table>:<id>`; quests reuse `quest:<id>` so the
  quest graph and the gamedata graph dedupe on merge).
- Every resolvable `*_id` column becomes an edge, validated against the target
  table's id set (see `COLUMN_TARGETS` / `ACT_TABLES`).
- Quest semantics: `contains` (quest→component→act), `accepts` / `reports-to` /
  `guards` / `talks-to` / `objective-npc` (quest→npc), `requires-item` /
  `rewards-item` (quest→item), `in-zone`, `in-milestone`, `precedes`
  (milestone quest_idx ordering), `requires-completion` (unit_reqs kind 31 =
  quest-completion prerequisites).

## Queries

```bash
graphify explain "Quest 1119" --graph graphify-out/quest-graph.json
graphify path "Npc 7548" "Quest 1897" --graph graphify-out/gamedata-graph.json
graphify query "what spawners place npc 7548" --graph graphify-out/gamedata-graph.json
```

## Per-zone readiness report

```bash
python3 tools/quest-graph/report-zone.py <compact.sqlite3> 9 124 125 > scorecard-explorations/solzreed-zone-report.md
```

Emits quest chains (kind-31 prerequisites), requirement gates, and the
missing-data checklist (quest-referenced NPCs without spawner rows, quest items
without templates). One zone at a time → the golden-path expansion pipeline.

## Known scope boundaries

- Spawner rows exist in `npc_spawner_npcs`; actual world placement (spawner →
  zone/position) lives outside compact.sqlite3 (world JSON data) - verify at
  server boot (sanity verifier / world spawn log).
- Localized text (263k rows) is not graphed as nodes; names are taken from
  name columns on each table.
