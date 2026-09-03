# AAEmu.ArchaeologyMcp — read-only archaeology MCP server

MCP stdio server exposing the **ArcheAge 1.2 reference data** (`compact.sqlite3`)
and allowlisted repo source roots as read-only MCP tools. Any MCP client
(Claude, Cursor, Gemini, Codex, or another implementation) can spawn the same
process and use the same newline-delimited JSON-RPC contract.

- **Transport:** MCP stdio (newline-delimited JSON-RPC 2.0), matching the
  `AAEmu.BotControl` / `AAEmu.BotControlMcp` convention (no MCP SDK).
- **Read-only by construction:** SQLite connections open `Mode=ReadOnly`; SQL
  is allow-listed; file paths must resolve inside allowlisted roots; no shell
  execution; no mutation tools.
- **Separate process:** zero code inside the game process. A crashed MCP
  client cannot affect the world.

## Tools

| Tool | Purpose |
|------|---------|
| `list_sources` | Catalog of allowlisted sources with metadata (source_id, source_type, path, logical_domain, version, encoding, size, searchable, notes). Includes a `game_pak` entry (source_type `aapak`) when `ARCHEAGE_PAK_PATH` is configured |
| `list_databases` | Available SQLite databases (canonical `compact.sqlite3` + any `*.sqlite3` copies in the data root) |
| `list_tables` | Tables/views in a database (default `compact.sqlite3`) |
| `describe_table` | Column info (name, type, notnull, pk) for a table |
| `query_sql` | Read-only SQL: `SELECT` / `WITH` / `EXPLAIN` / schema-read `PRAGMA` only; parameterized; bounded rows/columns/timeout |
| `read_file` | Read a text file from an allowlisted root (bounded to 1 MiB; optional byte offset/limit) |
| `search_files` | Regex-search files under an allowlisted root (bounded results; optional glob filter) |
| `list_pak_entries` | List AAPak (`game_pak`) entry metadata (name, size, offset, md5, timestamps) matching a regex, bounded to a result cap (default 5000); only the file table is read, no contents streamed |
| `read_pak_entry` | Read one named AAPak (`game_pak`) entry, bounded to `max_bytes` (default 1 MiB); returns metadata plus base64 content; rejects missing entries and traversal/absolute/backslash names |
| `lookup_row` | Fetch one row by primary key `id` from a table (default `compact.sqlite3`); table/columns validated via introspection, `id` parameterized, bounded to one row |
| `search_everything` | Search every text-bearing column of real tables plus allowlisted source files for a term (bounded; per-hit table/column/id provenance) |
| `trace_references` | Bounded reference trace of an identifier across tables (declared FKs = `exact`, name-convention = `heuristic`, value matches = `textual`) plus source-file matches |
| `find_quest_objectives` | Quest objective rows across `quest_act_obj_*` families, joined to `quest_acts` / `quest_components` / `quest_contexts` (optional `quest_id` / `objective_id` / `family` filters) |
| `trace_skill` | Look up `skills` by id or name |
| `trace_item` | Look up `items` by id or name |
| `trace_quest` | Look up `quest_contexts` by id or name, with linked `quest_components` and act counts |
| `trace_npc` | Look up `npcs` by id or name |
| `trace_doodad` | Look up `doodad_almighties` by id or name |
| `trace_mate` | Look up `item_summon_mates` by id or `item_id`, joined to NPC names |
| `trace_vehicle` | Look up `vehicle_models` by id (no name column exists; model columns are returned) |
| `trace_crafting` | Look up `crafts` by id or title |
| `trace_world_spawn` | World spawns from `Data/Worlds/world_spawns.json` (by name/zone) plus `npc_spawners` rows by name |
| `search_physics` | Search `physical_*` tables (enchant abilities, explosion effects) |
| `compare_source_data` | Compare a table's row counts and an ordered sample between the canonical DB and a `file:<name>` copy |

Every tool result is a JSON object with a deterministic `provenance` block:

```json
{
  "ok": true,
  "data": { "...": "..." },
  "provenance": {
    "tool": "query_sql",
    "source_id": "compact.sqlite3",
    "path": "/root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3",
    "version": "1.2 r208022",
    "generated_at": "2026-08-31T06:39:58.1551527+00:00",
    "truncated": false
  }
}
```

`truncated` is set on both the `data` object and the `provenance` block when a
row/byte/result limit was hit.

## Setup

```bash
# Optional environment (all defaults are repo-local; never machine-specific):
export AAEMU_ROOT=/root/aaemu-dev            # repo root (auto-resolved from app dir)
export ARCHEAGE_DATA_ROOT=/root/aaemu-dev/AAEmu.Game/Data
export ARCHEAGE_DB_PATH=/root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3
export ARCHEAGE_DB_VERSION="1.2 r208022"
export ARCHEAGE_PAK_PATH=/root/aaemu-e2e/runtime/game-data/ClientData/game_pak  # optional AAPak archive
export ARCHEAGE_PAK_VERSION="1.2 r208022"                       # optional pak provenance label

dotnet run --project AAEmu.ArchaeologyMcp
```

The server is a pure pipe: MCP clients spawn it as a subprocess and speak
JSON-RPC on stdin/stdout.

## MCP client example

```json
{
  "mcpServers": {
    "aaemu_archaeology": {
      "command": "dotnet",
      "args": ["run", "--project", "/root/aaemu-dev/AAEmu.ArchaeologyMcp", "--no-launch-profile"],
      "env": {
        "AAEMU_ROOT": "/root/aaemu-dev"
      }
    }
  }
}
```

A published binary is preferable for persistent registrations:

```bash
dotnet publish AAEmu.ArchaeologyMcp -c Release -o /opt/aaemu-archaeology
```

## Manual smoke (raw JSON-RPC)

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"query_sql","arguments":{"sql":"SELECT id, name FROM items WHERE id = 29040"}}}' \
| dotnet run --project AAEmu.ArchaeologyMcp
```

`Scripts/mcp-archaeology-smoke.sh` runs the full deterministic protocol +
read-only smoke (initialize / tools/list / list_sources / list_tables /
query_sql SELECT + rejected DROP / read_file / search_files /
list_pak_entries / read_pak_entry).

## Security

- **SQL allow-list** (`SqlGuard`): only single `SELECT` / `WITH` / `EXPLAIN` /
  schema-read `PRAGMA` statements. Rejected: `INSERT` / `UPDATE` / `DELETE` /
  `DROP` / `ALTER` / `CREATE` / `REPLACE` / `ATTACH` / `DETACH` / `VACUUM` /
  `REINDEX` / `TRUNCATE` / `GRANT` / `REVOKE`, any `PRAGMA` value assignment,
  multi-statement batches (semicolons), and **any SQL comment** (obfuscation
  is not allowed to hide keywords or semicolons).
- **Read-only connection:** every SQLite connection opens with
  `Mode=ReadOnly` — the DB cannot be mutated even if a statement slips past
  the allow-list. The canonical `compact.sqlite3` md5 is unchanged after any
  tool run.
- **Path guards:** `read_file` / `search_files` normalize paths, reject `..`
  traversal and absolute paths outside the allow-list, and resolve symlinks —
  a symlink escaping an allowed root is rejected. `search_files` globs must
  stay inside the root (absolute/rooted globs and `..` segments are
  rejected), and build output (`bin`/`obj` directories) is never searched.
  `list_databases` and `file:<name>` database ids apply the same symlink
  guard, so a symlinked `*.sqlite3` escaping the data root is never surfaced
  or opened.
- **Bounds:** `query_sql` caps rows (default 100, max 1000), columns (50), and
  query time (10 s). The deadline is enforced natively via
  `sqlite3_progress_handler` (SQLite's `SQLITE_INTERRUPT`), because
  `Microsoft.Data.Sqlite` ignores `CommandTimeout` for SQLite; an
  over-deadline query returns a deterministic `query timed out after 10s`
  error. `read_file` caps at 1 MiB. `search_files` caps results (500), skips
  files over 1 MiB, and fails deterministically on regex timeout
  (catastrophic backtracking) instead of hanging.
- **AAPak archive surface:** `list_pak_entries` / `read_pak_entry` open the
  archive with `openAsReadOnly: true` and never write, create, or mutate it;
  only the file table is read into memory (no full-archive streaming). Entry
  names are validated to reject absolute paths, backslashes, and `..`
  traversal before any lookup; reads are bounded to 1 MiB; there is no
  extraction to arbitrary disk. The archive is closed deterministically after
  each call. When `ARCHEAGE_PAK_PATH` is unset, both tools return a
  deterministic `not configured` error.
- **Excluded by default:** `.client_files`, `.server_files`, `.worktrees`,
  E2E/soak roots, MySQL (mutable state), and secrets. Extra roots are only
  added by explicit `ARCHEAGE_EXTRA_ROOTS` opt-in.

## Limits

- **Domain helpers are schema-driven and honest about gaps.** `search_everything`
  and `trace_references` discover tables/columns dynamically from
  `sqlite_master` / `PRAGMA table_info`; wrappers report `supported: false`
  with a reason when their table is absent (e.g. `trace_mate` on a DB without
  `item_summon_mates`). Evidence labels are never overstated: `exact` only for
  declared foreign keys, `heuristic` for name-convention links, `textual` for
  value matches.
- **No physics/collision data exists in the canonical DB.** `search_physics`
  only covers the `physical_*` effect tables (`physical_enchant_abilities`,
  `physical_explosion_effects`); there are no collision/geometry tables.
- **`vehicle_models` has no name column** — `trace_vehicle` matches by id and
  returns the model columns (`normal`, `damaged50`, …).
- **Quest objectives are linked by convention, not FK.** `find_quest_objectives`
  joins `quest_acts.act_detail_type`/`act_detail_id` to the matching
  `quest_act_obj_*` family table (snake_case of the type name); the linkage is
  labeled `heuristic` because no foreign key is declared.
- **`compare_source_data` is a row-count + ordered-sample comparison**, not a
  full diff; it requires a `file:<name>` copy of the DB in the data root.
- `search_files` is line-based regex over text files; binary files and files
  over 1 MiB are skipped.
- `query_sql` does not support `PRAGMA` with table arguments beyond the
  allowlisted schema-read set (`table_info`, `index_list`, `index_info`,
  `index_xinfo`, `foreign_key_list`, `table_list`, `database_list`).
- The metadata cache (`ARCHEAGE_CACHE_DIR`) is optional; when set, table
  lists and column info are cached on disk keyed by DB path + mtime. A stale
  or missing cache entry is simply rebuilt; cache writes never fail a request.
- `game_pak` (24.8 GB) is not streamed; only the entry indexes under
  `/root/aaemu-pak-lua/*.txt` are reachable via explicit extra-root opt-in.
  The AAPak archive itself is reachable read-only through
  `list_pak_entries` / `read_pak_entry` when `ARCHEAGE_PAK_PATH` is set
  (bounded listing and 1 MiB reads; never streamed wholesale).

## Tests

Focused tests live in `AAEmu.UnitTests/ArchaeologyMcp/`:

- `SqlGuardTests` — SELECT/WITH/EXPLAIN/schema PRAGMA accepted; every
  mutation keyword, multi-statement batch, comment obfuscation, and PRAGMA
  assignment rejected.
- `ArchaeologyMcpServerTests` — JSON-RPC framing (initialize / tools/list /
  tools/call / notifications / errors) and the 24-tool surface (including
  the AAPak archive tools and their unconfigured deterministic errors).
- `ArchaeologyDomainTests` — domain helpers on a temp DB with quest/skill
  tables: bounded search, exact-vs-heuristic trace evidence, quest-objective
  family discovery, honest unsupported results, and source-data comparison.
- `PakArchiveServiceTests` — AAPak archive surface against a tiny archive
  created in temp (no local 24.8 GB assets): bounded listing with regex
  filter and result cap, bounded single-entry reads, traversal/absolute/
  backslash name rejection, missing-entry and unconfigured behavior,
  read-only archive invariant, and provenance metadata.

```bash
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/SqlGuardTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/ArchaeologyMcpServerTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/ArchaeologyServiceTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/ArchaeologyDomainTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/PakArchiveServiceTests/*"
```

`scripts/archaeology-cycle.sh` runs the full archaeology pre-merge cycle: Release
builds of `AAEmu.ArchaeologyMcp` and `AAEmu.UnitTests`, all archaeology-focused
unit tests, and `Scripts/mcp-archaeology-smoke.sh`.
