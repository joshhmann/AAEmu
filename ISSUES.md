# AAEmu Server — Bug Log

Server: CT 133 `aaemu` (192.168.0.165, workhorse) — deployed 2026-08-02
Repo: /root/AAEmu | Stack: db / login / adminer / game (docker compose)
Bug entries: see `bugs/` folder (one file per issue)

---

## Index

| ID | Title | Status |
|----|-------|--------|
| BUG-001 | Game container SIGSEGV (exit 139) during AiGameData load — musl/glibc NLua mismatch | FIXED |
| BUG-002 | compact.sqlite3 schema too old for develop (missing item_socket_chances) | FIXED |
| BUG-003 | Missing game data files (compact.sqlite3 + game_pak) | RESOLVED |
| BUG-004 | Login advertises game server as 127.0.0.1 (LAN unreachable) | FIXED |
| BUG-005 | game_pak (2023) vs compact.sqlite3 (2026) version drift | OPEN |

## Environment notes
- Login (client-facing): 1237 | Game: 1239, 1250 | adminer: 8080 | mysql: 3306
- Launcher: Trion 1.2 auth (-t), server 192.168.0.165:1237 (defaults match)
- AutoAccount: true — first login auto-creates account
- DB password + secret key: /root/AAEmu/.env and Config.json under .server_files/
- CRITICAL: the glibc Dockerfile fix (BUG-001) must survive every `docker-update-local.sh` — re-apply if the game crash-loops after an update
