# Deploy card — 2026-08-25 (mechanics + bots mega-release)

## Target

- **SHA:** `f383a5a90` (fork develop tip, pushed)
- **Image tag after build:** retag to `aaemu-game:presence-demo` (overlay pins it)
- **Rollback:** snapshot `aaemu-game:rollback-pre-20260825` FIRST

## What's in this deploy (vs prod @ ~81676c0d6 / Aug 17)

| Area | Highlights |
|------|-----------|
| Quests | ConReportJournal noop fixed (466 quests), ConReportDoodad event leak, EtcItemObtain credit (~51 quests), census green |
| Combat | Duel stuck-player bug fixed + full accept path verified live; null-killer environmental-death NRE fixed |
| Economy | Trade functional (crash fix + handshake), auction expiry hardened + restart-proof, economy day-cycle w/ ledger reconciliation surviving kill -9 |
| World | Transfers boardable (TlId shadowing fix, never worked before), zone Peace/War enforcement, equip level gates |
| Social | Mail return/expiry, bot chatter v1 (OFF by default), schedules v1 (OFF by default) |
| Bots | Party spike live-proven, expedition actions, CastAt + fishing loop live-proven, N=10/20/30 scaling curve measured |
| Perf | Heap churn −38%/wake, allocation-free world-position path, spin-guard protocol fix |
| Ops | B4 playerbot_metadata store, silent-catch sweep, E2E harness fixes |

Full detail: STATUS.md 2026-08-23/24 blocks.

## Pre-deploy verification already done

- Full unit gate **2334/0/1** on tip
- Live E2Es on isolated stack (same build): fishing PASS, duel PASS,
  party-follow assist regression PASS, M1/M2 golden-route replay PASS
- All SQL changes additive (`CREATE TABLE IF NOT EXISTS`) — B4 tables
  self-heal on boot

## Steps (full runbook: Docs/wiki/Docker-Installation-Guide.md § Production redeploy)

```bash
ssh root@192.168.0.165 && cd /root/AAEmu
docker tag aaemu-game:presence-demo aaemu-game:rollback-pre-20260825   # FIRST
git fetch && git merge --ff-only f383a5a90
grep -c "runtime:10.0" AAEmu.Game/Dockerfile                            # glibc guard = 1+
docker compose --env-file /root/AAEmu/.env -p aaemu \
  -f docker-compose.yaml -f docker-compose.presence.yaml build game
docker tag aaemu-game:presence-demo-new aaemu-game:presence-demo        # RETAG (trap!)
docker compose --env-file /root/AAEmu/.env -p aaemu \
  -f docker-compose.yaml -f docker-compose.presence.yaml up -d --no-deps --force-recreate game
```

## Post-boot checks

1. No FATAL; passes AiGameData load (glibc OK)
2. Login log `Registered GameServer`; 1237/1239/1250 answering
3. Presence bots adopt + roam; **stay near home** (home-divergence fix)
4. `playerbot_metadata` + `playerbot_audit` tables exist
5. Port 1260 CLOSED (E2E bridge off)
6. Real client login; GM kit smoke (.kits, .teleport mirage)
7. New optional config (all OFF by default): `Bots.EnableChatter`,
   `Bots.EnableSchedules`, `Bots.PresenceManifest`, `Bots.MaxPresenceBots`

## Rollback

Retag snapshot → force-recreate game. B4 tables additive, no schema rollback.
