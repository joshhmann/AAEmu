# AGENTS.md — AAEmu

Guidance for coding agents working in this repository.

## What this repo is

Open-source **ArcheAge** server emulator in **.NET** (`AAEmu.Login`, `AAEmu.Game`, shared `AAEmu.Commons`). Preferred local orchestration is **.NET Aspire** (`AAEmu.Aspire.AppHost`). Branch of record for active work: **`develop`**.

> **Fork boundary (permanent): NEVER push a branch or open a PR to
> AAEmu/AAEmu.** Upstream is intake-only: fetch its updates into a dedicated
> `sync/upstream-YYYY-MM-DD` branch, verify them, and merge only into
> joshhmann/AAEmu. Configure the upstream push URL as `DISABLED`; `origin` is
> the writable fork.

Target client: **ArcheAge 1.2** (`r208022`).

Human docs live under `Docs/wiki/` (synced to GitHub wiki). Prefer those over inventing setup steps.

## Upstream alignment (locked 2026-08-04 — applies to every change)

The fork stays community-shaped. Full text + per-rule verification notes:
[`Docs/wiki/Development-Conventions.md`](Docs/wiki/Development-Conventions.md). In short:

1. Target `develop` + .NET 10 (`global.json`).
2. Aspire AppHost for local contributor debugging; prod stays Docker Compose.
3. `compact.sqlite3` is read-only reference data — mutable state goes to MySQL
   or an additive bot metadata schema.
4. Config precedence: `Config.json` → `Configurations/*.json` →
   `Config.Local.json`; never put machine-specific hosts/secrets/paths in
   shared config.
5. Server listings via `GameServers` config — never legacy `game_servers`.
6. New managers/services: explicit constructor dependencies where supported;
   no hidden singleton lookup or undocumented startup order.
7. Startup loading can be parallel — shared mutable collections and init
   logic must be concurrency-safe.
8. AAEmu-native terminology in code, logs, cards, searches
   (Doodad/Mate/Slave/Transfer/Expedition/Dominion/Ability/ActAbility).
9. PlayerBots compose around ordinary `Character` records + normal gameplay
   services — never a parallel character/inventory/quest/property/economy
   implementation.
10. Additive layer: composition/adapters/extension points first; narrow,
    reviewed core hooks only; never a parallel gameplay path.

## Getting the stack running (players and contributors)

Use the in-repo skill — **not developer-only**:

- **[`.agents/skills/aaemu-setup/SKILL.md`](.agents/skills/aaemu-setup/SKILL.md)** — guided setup with HitL downloads  
- **[`.agents/skills/aaemu-setup/REFERENCE.md`](.agents/skills/aaemu-setup/REFERENCE.md)** — ports, configs, troubleshooting  
- **Inventory (skip re-downloads)** — use the host shell:  
  - PowerShell: `powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1`  
  - Bash: `bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh`

| Path | When | MySQL | Apps |
| --- | --- | --- | --- |
| **A – Aspire** | Docker Desktop or Podman available | Container via AppHost | Login/Game as host projects |
| **B – Standalone** | No container runtime | **Host MySQL 8 only** | Host `dotnet` Login then Game |

**No hybrids** for non-Docker: if there is no Docker/Podman, do not introduce containers for MySQL either.

**Assets:** always run the inventory script before downloading. Multi‑GB MEGA/Drive packages are **Human-in-the-Loop**; do not re-fetch when the script reports **OK**.

Wiki mirrors: `Docs/wiki/Installation-&-Setup.md`, `Aspire-Development-Guide.md`, `Dependencies-and-Downloads.md`, `Client.md`.

---

## Runtime architecture

```text
                    ┌─────────────────┐
  Client 1.2 ──────►│  AAEmu.Login    │◄── MySQL aaemu_login (accounts)
  (launcher)        │  :1237 public   │
                    │  :1234 internal │
                    └────────┬────────┘
                             │ GameServer register + enter-world
                    ┌────────▼────────┐
                    │  AAEmu.Game     │◄── MySQL aaemu_game (mutable state)
                    │  :1239 game     │◄── compact.sqlite3 (read-only reference)
                    │  :1250 stream   │◄── game_pak / client files
                    └─────────────────┘
                             ▲
              optional: Aspire AppHost (MySQL container + env injection)
```

| Component | Role |
| --- | --- |
| **Login** | Auth, world list, enter-world handoff. Public client TCP via **ASP.NET Core Kestrel**. Internal TCP for Game registration. |
| **Game** | World simulation: packets, managers, entities, combat, quests, housing, etc. Generic host + `GameService`. |
| **Stream** | Side channel on Game (`:1250`) for UCC/emblems and related transfers. |
| **Aspire AppHost** | Local orchestration only; does **not** replace Login/Game. |
| **SQLite `compact.sqlite3`** | **Read-only** static game data (items, NPCs, skills, quests templates, …). |
| **MySQL** | **Read/write** state: `aaemu_login` (accounts) + `aaemu_game` (characters, items, world). |

Sequence (play): start Login → start Game (registers with Login) → launcher/client auth on Login → select server → connect to Game.

Authoritative component diagram: [`Docs/wiki/Components.md`](Docs/wiki/Components.md).

---

## Solution and project map

| Path | Role |
| --- | --- |
| `AAEmu.slnx` | Solution entry (SDK from `global.json`: **.NET 10**) |
| `Directory.Packages.props` | Central package versions (CPM) — bump deps here |
| `Directory.Build.props` | Shared MSBuild props |
| `AAEmu.Commons/` | Shared network primitives (`PacketStream`, `PacketBase`), MySQL helpers, `Singleton<T>`, AAPak, utilities |
| `AAEmu.Login/` | Login server |
| `AAEmu.Game/` | Game server (largest codebase) |
| `AAEmu.Aspire.AppHost/` | .NET Aspire orchestrator |
| `AAEmu.ArchaeologyMcp/` | **Read-only archaeology MCP server** (greenfield, separate process; exposes `compact.sqlite3` + allowlisted repo roots as read-only MCP tools — see [`AAEmu.ArchaeologyMcp/README.md`](AAEmu.ArchaeologyMcp/README.md) and the [data-source inventory](scorecard-explorations/mechanics/archaeology-data-source-inventory.md)) |
| `AAEmu.UnitTests/` | xUnit unit tests (mirror source layout) |
| `AAEmu.IntegrationTests/` | Game-focused integration tests |
| `AAEmu.Login.IntegrationTests/` | Login + Testcontainers MySQL |
| `SQL/` | Base schema + incremental updates |
| `Docs/wiki/` | Human-facing setup and architecture docs |
| `Scripts/` | Build/start helpers (bat/ps1/sh) |
| `Tools/` | Offline utilities (e.g. WorldConverter) |
| `.client_files/` | **Local only** (gitignored): extracted 1.2 client + launcher |
| `.server_files/` | **Local only** (gitignored): Compose/runtime data, optional logs |
| `**/Config.Local.json` | **Local only** (gitignored): machine overrides |

Do not commit client packs, launcher binaries, `compact.sqlite3`, or secrets.

### Game project layout (high signal)

| Path under `AAEmu.Game/` | Role |
| --- | --- |
| `Program.cs` | Host builder, **DI registration** for managers/services |
| `GameService.cs` | Startup/shutdown lifecycle (`IHostedService`) |
| `Core/Managers/` | Runtime managers (`*Manager` / `I*Manager`); subfolders `Id/`, `UnitManagers/`, `World/`, `Stream/` |
| `Core/Network/` | `Game/`, `Login/`, `Stream/` networks + protocol handlers + connections |
| `Core/Packets/` | Wire packets by direction (see [Packet map](#packet-map-and-conventions)) |
| `GameData/` | Static loaders from SQLite (`IGameDataLoader`); `Framework/GameDataManager` |
| `Models/Game/` | Domain models (entities, skills, quests, world, items, …) |
| `Models/Tasks/` | Scheduled/async game tasks |
| `Physics/` | Ship/vehicle physics (Jitter2) |
| `Scripts/Commands/` | In-game GM/admin commands (`ICommand`) |
| `Scripts/SubCommands/` | Nested command implementations |
| `Services/` | WebApi, Discord bot |
| `IO/` | Client file / `game_pak` access |
| `Data/` | Runtime data files (`compact.sqlite3`, worlds JSON, paths) |
| `Configurations/` | Split JSON config fragments |

### Login project layout

| Path under `AAEmu.Login/` | Role |
| --- | --- |
| `Program.cs` | Kestrel + DI; options validation |
| `LoginService.cs` | Hosted service lifecycle |
| `Core/Network/Login/` | Public client TCP (Kestrel connection handler) |
| `Core/Network/Internal/` | Game ↔ Login internal protocol |
| `Core/Packets/{C2L,L2C,G2L,L2G}/` | Packet DTOs + offset constants |
| `Core/PacketHandlers/` | Handlers separate from packet types (DI-registered) |
| `Core/Controllers/` | Login / Game / Request controllers |
| `Core/Authentication/` | Auth flows (password, Korea challenge, OTP/2FA, reconnect) |
| `Core/Services/` | Password, 2FA, etc. |
| `Docs/networking.md` | Login networking deep-dive |

---

## Configuration rules

- Game config load order: `Config.json` → `Configurations/*.json` → **`Config.Local.json` (wins)**.
- Login: `Config.json` → `Config.Local.json` → env vars / command line (Aspire injects env).
- Login listings: **`GameServers` in config**, not MySQL `game_servers` inserts.
- `SecretKey` must match between Login and Game.
- `ClientData.Sources` should include the 1.2 `game_pak` (absolute path under `.client_files/` is fine).
- `compact.sqlite3` → `AAEmu.Game/Data/` (required for game data).
- `Config.Local.json` is copied to output on build when present in the project directory.

Details: [`Docs/wiki/Working-with-the-Config.json-files-and-server-listings.md`](Docs/wiki/Working-with-the-Config.json-files-and-server-listings.md).

---

## Data stores

| Store | Location / schema | Mutability | Used for |
| --- | --- | --- | --- |
| **compact.sqlite3** | `AAEmu.Game/Data/compact.sqlite3` | Read-only at runtime | Templates: items, NPCs, skills, quests, doodads, … via `GameData/*` |
| **MySQL aaemu_login** | `SQL/aaemu_login.sql` + `SQL/updates/*login*` | Read/write | Accounts, 2FA, bans |
| **MySQL aaemu_game** | `SQL/aaemu_game.sql` + `SQL/updates/*game*` | Read/write | Characters, inventories, housing, mails, auction, … |
| **Client files** | `game_pak` / extracted client | Read-only | Models, geodata, assets via `ClientFileManager` |
| **JSON configs** | `Config*.json`, `Configurations/`, `Data/**/*.json` | Config-time | Server params, worlds, spawns-related data |

### SQL change workflow

When code needs a schema change:

1. Add `SQL/updates/YYYY-MM-DD_aaemu_{login|game}_*.sql` (date orders application).
2. **Also** patch the base file `SQL/aaemu_login.sql` or `SQL/aaemu_game.sql`.
3. Servers apply relevant updates once at startup (`MySqlDatabaseUpdater`); applied scripts are recorded in an updates table.

See `SQL/updates/readme.txt`. Prefer `SQL/patches/compact/` only for intentional compact.sqlite3 fixups (not normal gameplay state).

---

## Game startup lifecycle

Entry: `AAEmu.Game/Program.cs` → host → `GameService.StartAsync`.

Approximate stages in `GameService.cs`:

1. **DB migrate** — MySQL updates for `aaemu_game`.
2. **Client files** — `ClientFileManager.Initialize()` (fatal if no sources).
3. **Early managers** — some loads still run before orchestration (e.g. Formula/Item user data paths may be hybrid during migration).
4. **`ManagerOrchestrator.RunLoadAsync()`** — all `ILoadable` managers in **dependency-ordered parallel batches** (topo sort from constructor deps).
5. **GameData post-load** — `GameDataManager.PostLoadGameData()`.
6. **Scripts** — compile or reflect `Scripts/Commands` (prefer reflection when debugging).
7. **`RunInitializeAsync()`** — all `IInitializable` managers, same batching rules.
8. **World + networks** — static instances, then `GameNetwork` / `StreamNetwork` / `LoginNetwork` start.

Shutdown stops networks, AI, world, ticks, and clears client sources.

`ManagerOrchestrator` (`Core/Managers/ManagerOrchestrator.cs`):

- Builds batches from DI singleton types implementing `ILoadable` / `IInitializable`.
- Edges come from constructor parameters; **`Lazy<T>` is ignored** (cycle break pattern).
- Cycles throw; fix by reordering deps or introducing `Lazy<T>`.

---

## Packet map and conventions

### Direction folders (Game)

| Folder | Prefix | Direction | Notes |
| --- | --- | --- | --- |
| `Core/Packets/C2G/` | `CS*` | Client → Game | Handled on game TCP; offsets in `CSOffsets.cs` |
| `Core/Packets/G2C/` | `SC*` | Game → Client | Server responses/events |
| `Core/Packets/C2S/` | `CT*` | Client → Stream | Stream server |
| `Core/Packets/S2C/` | `TC*` / stream | Stream → Client | Stream responses |
| `Core/Packets/G2L/` | `GL*` | Game → Login | Internal |
| `Core/Packets/L2G/` | `LG*` | Login → Game | Internal |
| `Core/Packets/Proxy/` | Proxy | Login/proxy-related | Legacy/proxy protocol helpers |

### Direction folders (Login)

| Folder | Prefix | Direction |
| --- | --- | --- |
| `Core/Packets/C2L/` | `CA*` | Client → Login |
| `Core/Packets/L2C/` | `AC*` | Login → Client |
| `Core/Packets/G2L/` / `L2G/` | `GL*` / `LG*` | Game ↔ Login |

### Patterns

**Game packets** (typical):

- One class per opcode; constructor passes offset + level: `GamePacket(CSOffsets.CSBuyItemsPacket, 1)`.
- Override `Read(PacketStream)` for inbound; outbound packets implement `Write`.
- Inbound: `Read` often contains behavior (legacy style). Prefer following neighbors; `Execute()` exists to separate decode vs behavior when used.
- Register new C2G/G2C types in `GameNetwork` (`RegisterPacket`). Stream/Login side networks have their own `RegisterPacket` lists.
- Access player via `Connection.ActiveChar`; world lookups via `ParentWorld`.

**Login packets**:

- Packet type (DTO) + **separate** `*PacketHandler` under `Core/PacketHandlers/` (cleaner DI style).
- Handlers registered via `ServiceCollectionExtensions` in PacketHandlers/Network.

Do not invent opcodes; match client 1.2 tables already in `*Offsets.cs`.

---

## Domain model and terminology

Use **code/wiki terms**, not modern player slang, in identifiers and discussions.

| Term | Meaning |
| --- | --- |
| **Doodad** | Spawnable object without a health bar (crops, doors, furniture) |
| **Unit** | Entity with health / combat participation |
| **NPC** | Non-player unit |
| **Mate** | Pet / mount companion |
| **Slave** | Vehicle (cart, ship, car) |
| **Transfer** | Fixed-route transport (carriage, airship) |
| **Expedition** | Guild |
| **Appellation** | Title |
| **Ability** | Class combat skill tree |
| **ActAbility** | Vocational skill |
| **Dominion** | Castle siege content (not GvG shorthand) |
| **Indun** | Instance dungeon |
| **Gimmick** | Moving unit-like object (e.g. elevator) |
| **Skills** under `Models/Game/Skills` | **Game combat mechanics**, not agent skills |

### Object hierarchy (simplified)

```text
GameObject          Models/Game/World/GameObject.cs
  └─ BaseUnit       factions, buffs
       ├─ Unit      stats, combat, skill controllers
       │    ├─ Character, Npc (+ Portal), Mate, Slave
       │    ├─ House, Shipyard, Gimmick, Transfer
       └─ Doodad (+ DoodadCoffer)
```

Full glossary: [`Docs/wiki/Code-Terminology.md`](Docs/wiki/Code-Terminology.md).

### Managers vs GameData vs Models

| Layer | Responsibility | Example |
| --- | --- | --- |
| **Models** | Entity state and domain behavior | `Character`, `Skill`, `Quest`, `Doodad` |
| **GameData** | Load/cache **static** templates from SQLite | `ItemGameData`, `NpcGameData`, `BuffGameData` |
| **Managers** | Runtime orchestration, spawns, persistence, systems | `ItemManager`, `WorldManager`, `QuestManager` |
| **Packets** | Wire protocol edge | `CSStartSkillPacket` → manager/model |

Static reference data → `GameData` + `compact.sqlite3`.  
Per-character / world mutable state → managers + MySQL.

---

## Where to change X (task routing)

| Task | Start here |
| --- | --- |
| Client packet handling (gameplay action) | `Core/Packets/C2G/CS*.cs` → related `*Manager` / model |
| Server→client notify | `Core/Packets/G2C/SC*.cs` |
| Login auth / world list | `AAEmu.Login/Core/PacketHandlers/`, `Authentication/`, `Controllers/` |
| New manager | Class + `I*` in `Core/Managers/`; implement `ILoadable`/`IInitializable` if needed; **register both concrete and interface in `Program.cs`** |
| Static template data | `GameData/*` + SQLite schema; optional `SQL/patches/compact/` |
| Character behavior | `Models/Game/Char/Character*.cs` + `UnitManagers/CharacterManager` |
| NPC / AI | `Models/Game/NPChar/`, `Models/Game/AI/v2/`, `AIManager` |
| Skills / buffs / effects | `Models/Game/Skills/` (large `Effects/` tree), `SkillManager`, `BuffGameData` |
| Quests | `Models/Game/Quests/`, `QuestManager` |
| Housing / doodads | `Models/Game/Housing/`, `DoodadObj/`, `HousingManager`, `DoodadManager` |
| World / zones / spawns | `Core/Managers/World/`, `Models/Game/World/`, `Data/Worlds/` |
| Ships / vehicle physics | `Physics/`, `Models/Game/Units/Slave.cs`, `SlaveManager` |
| GM commands | `Scripts/Commands/`, `Scripts/SubCommands/`, `CommandManager` |
| Schema migration | `SQL/updates/` + base `SQL/aaemu_*.sql` |
| Web API / Discord | `Services/WebApi/`, `Services/DiscordBotService.cs` |
| Shared serialization / MySQL util | `AAEmu.Commons/` |
| Package version bump | `Directory.Packages.props` |

---

## Build and test

```bash
# Tier 1 — Standard gate (every change, ~1 min):
./scripts/gate.sh

# Tier 1 with targeted class filter:
./scripts/gate.sh BotActionControllerRouteTests

# Tier 2 — Targeted Integration / E2E scenario (seconds):
dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --configuration Release --treenode-filter "/*/*/BotControlActionMcpE2eTests/*"

# Tier 3 — Heavy soak / storm probes (milestone validation, 35-45+ min):
# Runs unfiltered AAEmu.IntegrationTests (GateSoakRunner, scale probes)
```

- SDK: .NET **10** (`global.json`).
- Solution: `AAEmu.slnx`.
- **Tier 1 (Fast Gate — `./scripts/gate.sh`):** Release build + ScriptCompiler + `AAEmu.UnitTests` + MCP stdio protocol smoke.
- **Tier 2 (Targeted Integration):** Specific network, MCP API, or restart persistence scenarios via treenode filter.
- **Tier 3 (Heavy Soak / Probes):** Unfiltered `AAEmu.IntegrationTests` for milestone exits and scale benchmarking.
- Test projects: `AAEmu.UnitTests` (primary), `AAEmu.IntegrationTests`, `AAEmu.Login.IntegrationTests`.
- Unit test bases: `TestBase`, `SqliteTestBase`, `IntegrationTestBase`; mocks under `Utils/Mocks/`.
- Naming: `MethodName_Scenario_ExpectedResult` (see `AAEmu.UnitTests/README.md`).
- Subsystem test priorities: [`Docs/TestingPlan_en.md`](Docs/TestingPlan_en.md).

### Evidence record and gate classification

Every Tier 1/2/3 report MUST include the exact `git rev-parse HEAD` SHA,
command, environment and assets, build/compiler result, unit total/pass/fail/
skip counts with the skip identity, and whether downstream MCP stdio smoke
ran. An infrastructure or repository-root-resolution failure is not a green
gate. In particular, `./scripts/gate.sh` fails from linked worktrees because
`RepoRoot` sees a `.git` file; use a normal clone, or fix the script in a
separate code task, and never classify that failure as a source/code failure.

## Archaeology MCP — development-cycle checkpoints

The read-only archaeology MCP (`AAEmu.ArchaeologyMcp/`) is a client-neutral
data-access slice, not a milestone or capability-track closure. It exposes the
canonical ArcheAge 1.2 reference data (`compact.sqlite3`, 679 tables, md5
`78b3bdbf038db3b927056106efdf91af`, target 1.2 r208022) and allowlisted repo
source roots as read-only MCP tools. It does not change any PB/M7/A5 claim.
### When to invoke archaeology (contributor contract)

Contributors **MUST** invoke the read-only archaeology MCP when investigating or
changing **source, schema, protocol, client-data, quest/objective,
item/skill/NPC/mate/vehicle/world/physics behavior**, or when a change depends on
a **reference-data fact** (a value that must match `compact.sqlite3` or the 1.2
client). Ordinary unrelated changes (pure refactors, logging, plumbing that
touches no reference data) **MAY skip** it. When in doubt, run it — it is
read-only and cheap.

**Tool/source routing** (start broad, then corroborate):

- **Catalog first:** `list_sources` / `list_databases` / `list_tables` /
  `describe_table` to confirm the surface and pick the right table.
- **Bounded corroboration:** `query_sql` / `lookup_row` for a specific row or a
  bounded read-only query.
- **Source/code:** `search_everything` / `search_files` / `read_file` for repo
  source and allowlisted files.
- **Domain chains:** `trace_references` and the typed `trace_*` helpers /
  `find_quest_objectives` for cross-table and quest-objective relationships.
- **AAPak:** `list_pak_entries` / `read_pak_entry` **only** when
  `ARCHEAGE_PAK_PATH` is intentionally configured; otherwise they return a
  deterministic `not configured` error.
- **Never exposed:** MySQL (mutable state) and E2E/soak roots are excluded by
  default and must not be reached through archaeology.

**Evidence contract** — when a finding is recorded (dossier, report, card, or
commit message), include: the exact `git rev-parse HEAD`; the tool's
`source_id`/`path`/`version`; the query/tool inputs; the confidence label
(`exact` / `heuristic` / `textual`); truncation/bounds hit; the canonical DB md5
(`78b3bdbf038db3b927056106efdf91af`); and whether the evidence is **data/code**
vs **live/client/H**. When behavior is researched, link the acceptance dossier
[`scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`](scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
Classify evidence by layer: contract/reflection/fake mapping; deterministic
rig; live authenticated server/client; or human/client. Contract, reflection,
fake-mapping, and rig evidence do not prove live behavior; live bot/client
evidence does not prove human feel. `H` remains UNKNOWN until an actual human
completes the named scenario.

**Required before merge:** run `./scripts/archaeology-cycle.sh` **alongside**
`./scripts/gate.sh` for any change that invoked archaeology (and always for
archaeology MCP changes). `archaeology-cycle.sh` builds the MCP + archaeology
unit tests + smoke; `gate.sh` runs the full unit suite + BotControl smoke. Run
both before merge.

The smallest maintainable process is **deterministic local checks on every
archaeology change, with data refresh optional and explicit**. Do not run
expensive live AAPak/DB scans on every commit; the canonical DB and `game_pak`
are read-only reference data that change only on an intentional data update.

### Checkpoint 1 — before coding (source/catalog/version inventory)

Confirm the read-only surface is present and unchanged before planning a slice:

```bash
# Canonical DB md5 must match the recorded baseline (78b3bdbf…).
md5sum AAEmu.Game/Data/compact.sqlite3
# Catalog + table inventory via the stdio server (or the smoke script).
bash ./Scripts/mcp-archaeology-smoke.sh
```

Expected evidence: `compact.sqlite3` md5 `78b3bdbf038db3b927056106efdf91af`
(unchanged), 679 tables, `list_sources` shows the canonical DB + allowlisted
roots, and the 24-tool surface is present. Inventory the relevant source
(packets/managers/GameData) and the data tables the slice touches before
writing code.

### Checkpoint 2 — during coding (source/data cross-reference and relationship/acceptance query)

Cross-reference every source claim against canonical data using the read-only
tools, and run the acceptance query the change depends on:

```bash
# Relationship/acceptance query (example — use the tool the slice needs):
#   trace_skill / trace_item / trace_quest / trace_npc / trace_mate /
#   trace_vehicle / trace_crafting / trace_world_spawn / search_physics /
#   find_quest_objectives / trace_references / search_everything / query_sql
```

Expected evidence: the tool returns `ok=true` with a deterministic
`provenance` block (source_id, path, version, generated_at), and any
`exact`/`heuristic`/`textual` evidence label is reported honestly. Do not
invent data facts; verify them through `query_sql` (read-only) or the typed
domain helpers.

### Checkpoint 3 — before merge (MCP build + focused security tests + archaeology stdio smoke)

The canonical one-command archaeology pre-merge check is
`./scripts/archaeology-cycle.sh`, which runs all four phases from the repo
root:

```bash
./scripts/archaeology-cycle.sh
```

It runs, in order: (1) Release build of `AAEmu.ArchaeologyMcp`; (2) Release
build of `AAEmu.UnitTests`; (3) all archaeology-focused unit tests
(`AAEmu.UnitTests.ArchaeologyMcp` namespace, which includes `SqlGuardTests`,
`PakArchiveServiceTests`, `ArchaeologyMcpServerTests`, `ArchaeologyDomainTests`,
and `ArchaeologyServiceTests`); (4) the archaeology stdio smoke
(`Scripts/mcp-archaeology-smoke.sh`). It is read-only (builds write only to
`bin`/`obj`; no source, data, or config mutated) and never claims to run AAPak
(the AAPak tools report their deterministic unconfigured errors when
`ARCHEAGE_PAK_PATH` is unset).

Expected evidence: Release builds 0 errors; all archaeology-focused unit tests
pass; the archaeology smoke prints
`MCP archaeology stdio smoke passed: 24 tools … read-only`.

The explicit phases, if run individually:

```bash
# 1. MCP build (must be 0 errors).
dotnet build AAEmu.ArchaeologyMcp -c Release
# 2. Focused security + surface tests.
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/SqlGuardTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/PakArchiveServiceTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/ArchaeologyMcpServerTests/*"
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release \
  --treenode-filter "/*/*/ArchaeologyDomainTests/*"
# 3. Archaeology stdio smoke (24-tool protocol + read-only invariant).
bash ./Scripts/mcp-archaeology-smoke.sh
```

**Gate note:** `./scripts/gate.sh` runs, as its 4/5 step, the existing
**BotControl** MCP stdio smoke (`Scripts/mcp-stdio-smoke.sh`, 39 tools), and as
its **5/5 step** the **lightweight archaeology gate smoke**
(`Scripts/mcp-archaeology-gate-smoke.sh`, 24 tools) — so a lightweight
archaeology availability/read-only check **IS wired into `gate.sh`**. That
lightweight script requires no `game_pak`/client assets, no MySQL, and no
archaeology unit-test run; it exercises protocol/server availability, the
24-tool surface, the canonical repo-local `compact.sqlite3` (679+ tables), a
simple read-only `SELECT`, and read-only rejection of a `DROP`
(`ARCHEAGE_PAK_PATH` intentionally unset, so AAPak tools report their
deterministic unconfigured errors). The **full** archaeology smoke
(`Scripts/mcp-archaeology-smoke.sh`, 24 tools) and the archaeology-focused
unit tests are **not** wired into `gate.sh` — they run only in
`./scripts/archaeology-cycle.sh`, which runs **alongside** `./scripts/gate.sh`
so the archaeology-focused tests are not run twice in one gate pass. Run both
before merge.

### Checkpoint 4 — after merge / periodic refresh (acceptance dossier and md5/provenance review)

After an archaeology change merges, or on a periodic data refresh, re-verify
the acceptance surface against the current HEAD:

```bash
# Re-run the acceptance dossier queries and the smoke.
bash ./Scripts/mcp-archaeology-smoke.sh
# Canonical DB must remain unchanged (read-only invariant).
md5sum AAEmu.Game/Data/compact.sqlite3
```

Expected evidence: `compact.sqlite3` md5 still `78b3bdbf038db3b927056106efdf91af`;
the acceptance dossier
[`scorecard-explorations/mechanics/archaeology-mcp-acceptance.md`](scorecard-explorations/mechanics/archaeology-mcp-acceptance.md)
is re-read and its queries re-run against the current HEAD; provenance blocks
(source_id, path, version, generated_at) are reviewed for correctness. A data
refresh (AAPak/DB scan) is **optional** and only warranted when the canonical
data or `game_pak` actually changes — never on every commit.

---

## Code changes — read first, then match the repo

When asked to modify, fix, or improve code, **do not invent conventions**. Inspect the target area and the sources below, then mirror what is already there.

### Documentation-first scope check

Before planning or editing any non-trivial task, read the entire relevant current documentation set, not just a matching snippet. Start with:

1. `AGENTS.md` (rules)
2. `PROJECT-CONTROL.md` (authoritative scope map and records)
3. `STATUS.md` (current state/evidence)
4. `SCORECARD.md` (conservative evidence dimensions)
5. `ROADMAP.md` (milestone requirements and next work)
6. The relevant `Docs/wiki/`, dossier, evidence, and source/test docs for the task.

Establish the hierarchy before implementation: **milestone/umbrella → capability track or gate → current slice → evidence boundary → next action**. Do not describe a slice as a completed track or milestone. Use repository documentation indexes/maps and graph/document-relationship tooling, including Graphify when available, to find related docs and avoid missing cross-references; graph output supplements, never replaces, reading authoritative documents. When scope or evidence changes, update the authoritative status, scorecard, and roadmap records in the same wave. Handoff docs are execution notes, not the project source of truth. Preserve historical evidence and distinguish rig/proxy, live, and human evidence.


### Authoritative sources (in order)

1. **Neighboring code** in the same folder, namespace, and subsystem — this is the primary style guide.
2. **[`.editorconfig`](.editorconfig)** — formatting, naming, analyzer severities (IDE/CA rules). Run `dotnet build` to surface violations.
3. **[`CONTRIBUTING.md`](CONTRIBUTING.md)** — branch from `develop`, present-tense commits, tests with changes, follow project code style.
4. **`Docs/wiki/`** — domain language and architecture context:
   - [`Code-Terminology.md`](Docs/wiki/Code-Terminology.md) — game-object hierarchy, in-game terms.
   - [`Components.md`](Docs/wiki/Components.md) — Login/Game/Aspire roles and data stores.
   - [`Developer-Notes.md`](Docs/wiki/Developer-Notes.md) — manager DI and parallel loading.
   - [`Documentation-Maintenance.md`](Docs/wiki/Documentation-Maintenance.md) — when and how to update wiki pages.
   - [`Home.md`](Docs/wiki/Home.md) — documentation map.
5. **[`Docs/TestingPlan_en.md`](Docs/TestingPlan_en.md)** — subsystem map and testing priorities.
6. **Login networking** — [`AAEmu.Login/Docs/networking.md`](AAEmu.Login/Docs/networking.md).

### C# style highlights (from `.editorconfig`)

- 4-space indent, CRLF, UTF-8 BOM, file-scoped namespaces matching folder paths.
- `#nullable enable` at file top where the area already uses it.
- `var` preferred; block bodies for methods; expression bodies for properties/accessors.
- Naming: `_camelCase` instance fields, `s_camelCase` static fields, `PascalCase` types/members/locals-functions.
- Avoid `this.` qualification; sort `using` with `System.*` first; NLog `Logger` via `LogManager.GetCurrentClassLogger()`.

### Project patterns to follow

| Area | Location | Pattern |
| --- | --- | --- |
| **Managers** | `AAEmu.Game/Core/Managers/` | `*Manager` + `I*Manager`; many still extend `Singleton<T>` **and** are registered in DI. Newer code: constructor injection, `ILoadable` / `IInitializable`. Orchestrator runs Load/Initialize — **register new managers in `Program.cs`** (concrete + interface). |
| **Packets (Game)** | `Core/Packets/{C2G,G2C,...}/` | One class per packet; prefix = direction; offsets in `*Offsets.cs`; inherit `GamePacket` / stream variants; register in `*Network`. |
| **Packets (Login)** | `Packets/` + `PacketHandlers/` | Split DTO vs handler; handlers in DI. |
| **Game data** | `GameData/` | `IGameDataLoader` (`Load(SqliteConnection)`, `PostLoad`); discovered/orchestrated by `GameDataManager`. |
| **Models** | `Models/Game/` | Domain types separate from managers/packets; use wiki terminology. |
| **Network** | `Core/Network/` | Protocol handlers route to packet classes; connections in `Connections/`. |
| **ID allocation** | `Core/Managers/Id/` | Typed `*IdManager` per object kind. |
| **Commands** | `Scripts/Commands/` | `ICommand` implementations loaded by script reflector/compiler. |
| **Shared** | `AAEmu.Commons/` | Network primitives and utilities for Login + Game. |
| **Tests** | `AAEmu.UnitTests/` | xUnit; mirror source layout; reuse fixtures/mocks. |

Legacy `Singleton<T>.Instance` and static access still exist — **do not mass-migrate** unless the task explicitly calls for it. For new dependencies, follow the constructor-injection style used in recently touched managers. `SingletonContainer.ServiceProvider` bridges some legacy paths.

Circular manager deps: inject `Lazy<T>` so the orchestrator does not treat them as hard edges.

### Workflow for modifications and improvements

1. **Scope** — identify the subsystem (packet, manager, GameData, model, config) and read 2–3 representative files plus any `I*` interface and tests for that type.
2. **Implement** — smallest change that solves the task; match naming, file placement, logging, and error-handling patterns of the surrounding code.
3. **Wire-up** — new manager/service: register in `Program.cs` like peers; new game packet: offsets + `RegisterPacket` in the correct `*Network`; new login handler: packet + handler + DI extension.
4. **SQL** — if schema changes, add `SQL/updates/…` **and** update base `SQL/aaemu_*.sql`.
5. **Test** — add or extend tests in `AAEmu.UnitTests` when changing behavior; reuse existing patterns.
6. **Verify** — `dotnet build` and `dotnet test` must pass before claiming done.
7. **Document** — update `Docs/wiki/` only when user-facing setup, config, or behavior changes; follow `Documentation-Maintenance.md`. Do not add unsolicited markdown elsewhere.

**Avoid:** drive-by refactors, new frameworks, reformatting unrelated files, renaming domain terms away from wiki vocabulary, and broad style “cleanup” outside the requested change.

---

## Windows agent pitfalls

- Detach long-lived GUIs (launcher, server consoles) from the agent shell Job Object or they die when the command ends.
- Port **1234** often conflicted (e.g. ManyCam): remap Login `InternalNetwork` + Game `LoginNetwork` together or the client shows **Maintenance**.
- Aspire dashboard token is printed by AppHost at startup (`/login?t=...`).

---

## Out of scope unless asked

- Shipping client assets into git
- Silent multi‑GB MEGA re-downloads without inventory + HitL
- Changing default production Docker Compose passwords casually
- Full protocol reverse-engineering writeups without a concrete task
- Mass migration off `Singleton<T>` without an explicit request

---

## Quick verify (before saying “ready to play”)

1. Inventory script: client + compact + launcher **OK**
2. Login listens on **1237**
3. Game listens on **1239** / **1250**
4. Standalone: login log shows **Registered GameServer**
5. Launcher points at `.client_files/.../bin32/archeage.exe`, server `127.0.0.1`
