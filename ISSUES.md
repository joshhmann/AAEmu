# AAEmu Server — Bug Log

Server: CT 133 `aaemu` (192.168.0.165, workhorse) — deployed 2026-08-02
Repo: /root/AAEmu (fork: joshhmann/AAEmu, develop) | Stack: db / login / adminer / game (docker compose)
Bug entries: see `bugs/` folder (one file per issue)

---

## Index

| ID | Title | Status |
|----|-------|--------|
| BUG-001 | Game container SIGSEGV (exit 139) during AiGameData load — musl/glibc NLua mismatch | FIXED; legacy upstream PR #1494 predates permanent one-way policy |
| BUG-002 | compact.sqlite3 schema too old for develop (missing item_socket_chances) | FIXED |
| BUG-003 | Missing game data files (compact.sqlite3 + game_pak) | RESOLVED |
| BUG-004 | Login advertises game server as 127.0.0.1 (LAN unreachable) | FIXED |
| BUG-005 | game_pak (2023) vs compact.sqlite3 (2026) version drift | RETIRED — paks verified byte-identical (md5 7f77c6a8) |
| BUG-006 | Kill-acceptor quests can never start (380 quests, e.g. 182/205/556/913/1208) — QuestActConAcceptNpcKill checked Npc acceptor, no code path set Kill | FIXED — branch fix/quest-kill-acceptor (2026-08-03) |
| BUG-008 | QuestActCheckGuard silently auto-completes escort/protect objectives (6 quests) — RunAct returned true unconditionally | FIXED — branch fix/quest-check-guard (2026-08-04) |
| BUG-009 | Item-group gather/use objectives stall (9 act rows; 4 live quests 5490/6578/6600/6615 + test 5489) — QuestActObjItemGroupGather/Use RunAct fell through to base stub | FIXED — branch fix/quest-item-group-objectives (2026-08-04) |
| BUG-010 | Helpers.UnixTime(long) clamps every timestamp > 59s to DateTime.MaxValue (DateTime.MaxValue.Second == 59) — all CheckTimer quests restore with Time=MaxValue, timer never expires | FIXED — branch fix/bug-010-unix-time (2026-08-04) |
| BUG-007 | Quest data defects fail silently — startup sanity verifier missing (M1-3) | FIXED — branch feat/quest-sanity-verifier (2026-08-04) |
| BUG-011 | QuestActCheckSphere can never pass + sphere entry crashes — Objectives[0xFF] write (quest 1033 Progress component 5065) | FIXED — branch fix/quest-check-sphere (2026-08-04) |
| BUG-012 | CharacterAbilities KeyNotFoundException 'General' on quest exp rewards (Ability1==General; ctor seeds Fight..Love only, ability1 DB column has no default, no client validation) — quests 250/6578/6600/6615 REWARD crash | FIXED — branch fix/char-abilities-general (2026-08-04) |
| BUG-013 | NPC sit poses render "knees in" — server sends sit anim ids the 1.2 client cannot play for the NPC's race/gender (missing .caf assets; ids 70/160 have none at all) | FIXED — branch fix/npc-sit-pose (2026-08-05) |
| BUG-014 | quest completed-block id wraps for quest ids >= 4,194,304 — ResetQuests recomputes a wrapped id, daily reset never clears, AddQuest refuses with QuestDailyLimit forever (live: 8000004) | FIXED — branch fix/bug-014-quest-completed-block-uint (2026-08-10) |
| BUG-015 | CharacterQuests.Save NREs on a null completed-block entry — concurrent mutation during enumeration yields a null block, disconnect save aborts BEFORE the active-quest REPLACE loop, quest rows lost | FIXED — branch fix/quest-save-null-guard (2026-08-10) |
| BUG-016 | Melee combo skills with target_area_radius + TargetSelection=Target never damage their primary target (skill 18131 confirmed — 150/150 successful casts, 0 damage; census: 415 skills in class, 13 player-learnable) — ApplyEffects AoE branch excludes the center object | FIXED — branch fix/bug-016-area-target-primary (2026-08-20); rig tests + 18131-led spike rotation as live regression |

## Audit findings (Kimi deep-dive 2026-08-09, t_0fda3cd3)

| ID | Finding | Status |
|----|---------|--------|
| AUDIT-001 | SaveManager.DoSave full-table sync save on every cycle — every in-world character REPLACEd each autosave tick (SaveManager.cs:94); at 1,000-bot scale the periodic save rewrites the whole character surface every cycle | CLOSED — dirty-tracking merged 5ed5d6493 (2026-08-10, t_8c18eb1c, Rei gate t_53025996 ACCEPT): dirty-only periodic saves, force-all retained on shutdown + /save |

## Production state (2026-08-02)

- **Website:** https://archeageslums.asslorde.com — nginx :8081, register API (rate-limited 5/10min), downloads, patch manifest
- **Game domain:** https://archeage.asslorde.com — split-horizon DNS (LAN → .165 via Unbound, WAN → 75.3.243.94)
- **WAN ports:** 1237 (login), 1239 (game), 1250 (stream) forwarded on OPNsense — VERIFIED open from internet
- **Launcher:** themed fork joshhmann/AAEmu-Launcher (f6f33f4), zip at /downloads/launcher/ArcheAge-Slums-Launcher.zip, aelcf → archeage.asslorde.com
- **Patch system:** /downloads/patch/ serves patchfiles.csv + .ver (118 files) — launcher auto-updates bin32/game
- **Client:** 8.8GB 7z (r208022) hosted at /downloads/client/
- **Security:** adminer 127.0.0.1:8080 only, MySQL 127.0.0.1:3306 only (d8a57a99)
- **Backups:** aaemu-backup.timer daily 05:30 PDT, /root/aaemu-backups, 14-day retention (MySQL dump + configs + pak md5)
- **Upstream:** PR https://github.com/AAEmu/AAEmu/pull/1494 — glibc Dockerfile fix; daily cron checks status → Discord

## Environment notes
- Login (client-facing): 1237 | Game: 1239, 1250 | adminer: 8080 (localhost) | mysql: 3306 (localhost)
- Launcher: Trion 1.2 auth (-t), server archeage.asslorde.com:1237
- AutoAccount: true — first login auto-creates account
- DB password + secret key: /root/AAEmu/.env and Config.json under .server_files/
- CRITICAL: the glibc Dockerfile fix (BUG-001) must survive every `docker-update-local.sh` — re-apply if the game crash-loops after an update
