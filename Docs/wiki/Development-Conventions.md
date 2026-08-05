# Development Conventions

- Audience: Contributors, reviewers, automation
- Last verified against: `develop` on 2026-08-04 (lock: Josh 2026-08-04)
- Scope: upstream-alignment rules for all fork work — every fix, feature, and task template

## The rules (locked)

These keep the fork community-shaped: upstream PRs stay an option, upstream
pulls stay clean, and nothing drifts into parallel gameplay implementations.

1. **Target AAEmu `develop` and .NET 10.** `global.json` pins SDK 10.0.0
   (rollForward latestMajor). Verified current.
2. **Local contributor debugging prefers the Aspire AppHost when practical.**
   Production stays on the current Docker Compose deployment —
   `deployments/production.json` records the `.165` stack (db/login/game/adminer,
   mysql:8.0.36). See the [Aspire Development Guide](Aspire-Development-Guide).
   Verified: prod manifest + `docker-compose.yaml` current.
3. **`compact.sqlite3` is read-only reference data.** Mutable bot, character,
   economy, schedule, memory, and runtime state must live in MySQL or an
   additive bot metadata schema. The harness/tests read reference copies
   (e.g. `/tmp/compact.sqlite3`) — never write to game data. Known exception
   (verified 2026-08-04): `AAEmu.Game/Utils/DB/HoldablesSchemaCheck.cs`
   best-effort `ALTER TABLE holdables` only when the 6 auto-attack anim
   columns are MISSING from a stripped/custom SQLite (shipped DB already has
   them; ReadWrite open never happens in the normal path). Migrate this to an
   additive schema when touched.
4. **Config precedence:** `Config.json` → `Configurations/*.json` →
   `Config.Local.json`. Machine-specific hosts, secrets, API endpoints,
   paths, and credentials stay OUT of shared config (`Config.Local.json` is
   gitignored for exactly this). Verified: AGENTS.md + working-with-config
   page agree.
5. **Server listings come from `GameServers` configuration** — do NOT
   reintroduce the legacy `aaemu_login.game_servers` approach. Verified: the
   alias/dangling-row audit (M1-1) found the legacy table dormant.
6. **New managers and services use explicit constructor dependencies where
   AAEmu supports them.** No hidden singleton lookup or undocumented startup
   order. (Note: the codebase historically uses `Singleton<T>` in
   `AAEmu.Commons` — new code prefers DI where the host provides it; review
   each case.)
7. **Startup loading can be parallel.** Shared mutable collections and
   initialization logic must be concurrency-safe.
8. **AAEmu-native terminology** in code, logs, task cards, and searches:

   | Term | Meaning | Old/informal |
   |---|---|---|
   | Doodad | crops, trees, furniture, doors | prop/object |
   | Mate | pets and mounts | companion/pet |
   | Slave | carts, cars, ships | vehicle |
   | Transfer | fixed-route transports | taxi/travel |
   | Expedition | guild | guild |
   | Dominion | castle/siege | castle/guild war |
   | Ability | combat skill tree | skill tree |
   | ActAbility | vocation/proficiency | profession/craft skill |

   (`Code-Terminology.md` covers additional engine terms — doodad/unit/mate/
   slave/actability/appellation.)

9. **PlayerBots compose around ordinary `Character` records and normal
   gameplay services** (roadmap M6.0: headless login accounts + additive
   `PlayerBotController`). Do NOT create a parallel character, inventory,
   quest, property, or economy implementation.
10. **Additive-layer rule (refined):** prefer composition, adapters, and
    existing extension points. Allow only narrow, reviewed core hooks when
    required to reuse the normal Character/session lifecycle — and NEVER
    create a parallel gameplay path.

## Where these live

- `ROADMAP.md` — "Upstream alignment rules" (standing rules, every milestone)
- `WORKFLOW.md` — the operational playbook (rules + the no-upstream-PR rule)
- `AGENTS.md` — in-repo agent guidance (compact 10-rule section)
- `.kanban-templates/README.md` — every future task card inherits the block
- `.kanban-templates/{fix,feature,explorer,tracking}.md` — alignment
  blockquote next to THE RULE on each template
- This page — canonical text + verification notes
- Shared division skill (`aaemu-fork-workflow`) — fleet-side copy

## Verification history

- 2026-08-04 (a54fe4f1 + gap-fix): all rules checked against current
  code/wiki before locking. Confirmed current: prod is Docker Compose
  (deployments/production.json: db/login/game/adminer, mysql:8.0.36), .NET 10
  (global.json 10.0.0; `dotnet --version` 10.0.110 on openclaw),
  `GameServers` config live in AAEmu.Login/Config.json, legacy
  `aaemu_login.game_servers` dormant (dropped 2026-01-31 update; FAQ page
  already corrected), config precedence verified in AAEmu.Game/Program.cs
  (Config.json → Configurations/*.json → Config.Local.json), sqlite opened
  `Mode=ReadOnly` in SQLite.cs. Terminology verified complete in
  Code-Terminology.md (all 8 terms present — Doodad/Mate/Slave/Transfer/
  Expedition/Dominion/Ability/ActAbility); this page adds the consolidated
  card-level table + old/informal mappings. PlayerBots not yet in code —
  rule 9 pins the roadmap M6.0 design (headless accounts + ordinary
  Character records + additive PlayerBotController) as a standing
  constraint. Known write-path exception to rule 3 documented above
  (HoldablesSchemaCheck).
