# Docker Installation Guide

- Audience: Operators and contributors
- Last verified against: `develop` on August 5, 2026
- Prerequisites: Docker runtime, Git, required AAEmu data files

## When to use this guide

Use this guide when you want containerized AAEmu without Aspire orchestration.

If you want the preferred contributor startup flow, use
[Aspire Development Guide](Aspire-Development-Guide).

## Prerequisites

1. Install Git.
1. Install Docker Desktop (Windows) or Docker Engine + Compose (Linux).
1. Place required files where scripts expect them:
   - `compact.sqlite3`
   - ArcheAge `game_pak`

## Initial install

1. Clone `https://github.com/AAEmu/AAEmu`.
1. Change directory to `Scripts`.
1. Run:
   - Windows: `docker-install-local.ps1`
   - Linux: `docker-install-local.sh`

## Update an existing install

1. Change directory to `Scripts`.
1. Run:
   - Windows: `docker-update-local.ps1`
   - Linux: `docker-update-local.sh`

## Production redeploy (CT 133) — read before updating

⚠️ **Do NOT use `docker-update-local.sh` / `.ps1` verbatim for the production
server.** The plain script drops `-p aaemu`, `--env-file`, the presence-demo
overlay (`docker-compose.presence.yaml`), and the rollback-image snapshot,
and rebuilds the whole stack `--no-cache`. Using it on prod recreates every
service without the overlay and can silently lose the rollback image.

The production game compose overlay pins the image tag
`aaemu-game:presence-demo` — after building a new image you MUST retag it to
that name, or the recreate silently keeps running the OLD image.

Redeploy recipe (as performed for M3b/M4):

```bash
ssh root@192.168.0.165        # prod tree at /root/AAEmu
cd /root/AAEmu

# 1) Snapshot the rollback image FIRST (thinpool/GC can eat old layers):
docker tag aaemu-game:presence-demo aaemu-game:rollback-pre-<label>

# 2) Fast-forward source to the pinned target SHA:
git fetch && git merge --ff-only <TARGET_SHA>

# 3) Sanity checks BEFORE build:
#    - AAEmu.Game/Dockerfile runtime stage must be Debian glibc
#      (mcr.microsoft.com/dotnet/runtime:10.0) — the BUG-001 musl SIGSEGV
#      fix; crash-loop exit 139 during AiGameData load = musl came back.
#    - docker-compose.yaml logging caps intact (game/login 50m x3,
#      db/adminer 25m x3) — prevents the 39GB json.log disk-full incident.
#    - E2E bridge stays OFF: no E2E_BRIDGE_ENABLED env, no
#      "EnableE2EBridge" key in .server_files Config*.json, port 1260 closed.

# 4) Build and swap ONLY the game service, keeping the pinned tag:
docker compose --env-file /root/AAEmu/.env -p aaemu \
  -f docker-compose.yaml -f docker-compose.presence.yaml \
  build game
docker tag <new-game-image> aaemu-game:presence-demo
docker compose --env-file /root/AAEmu/.env -p aaemu \
  -f docker-compose.yaml -f docker-compose.presence.yaml \
  up -d --no-deps --force-recreate game

# 5) Post-boot verification:
#    - no FATAL lines; boot passes AiGameData load (glibc OK)
#    - login log shows "Registered GameServer"; 1237/1239/1250 answering
#    - presence bots adopt + roam (3/3)
#    - MySQL tables playerbot_metadata + playerbot_audit exist
#      (self-healing schema creates them on boot; verify anyway)
#    - real client login from LAN; GM kit smoke (.kits, .teleport mirage)
```

Rollback: retag the snapshot back and force-recreate game only:

```bash
docker tag aaemu-game:rollback-pre-<label> aaemu-game:presence-demo
docker compose --env-file /root/AAEmu/.env -p aaemu \
  -f docker-compose.yaml -f docker-compose.presence.yaml \
  up -d --no-deps --force-recreate game
```

B4 bot tables are additive (`CREATE TABLE IF NOT EXISTS`) — no schema
rollback needed.

## Launch

From project root:

- Detached mode: `docker compose up -d`
- Dev/watch mode: `docker compose watch`

## Important configuration notes

### Login container runtime

Login server public networking is ASP.NET Core Kestrel-based.
The login container must use an ASP.NET runtime image.

### Server listing source

Server listings are configuration-driven (`GameServers`) and can be injected
through environment variables in compose, for example:

```text
GameServers__0__ID=1
GameServers__0__Name=AAEmu.Game
GameServers__0__Host=127.0.0.1
GameServers__0__Port=1239
```

Do not depend on MySQL `aaemu_login.game_servers` inserts.

## Troubleshooting

- Docker API or daemon not available: start Docker before running commands.
- Installation script fails on Windows policy: adjust execution policy for your
  user if needed.
- Services start but client cannot connect: verify `GameServers` host/port and
  exposed compose ports.

## Related

- [Home](Home)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Installation & Setup](Installation-&-Setup)
- [Mini troubleshoot guide](Mini-troubleshoot-guide)
