# BUG-004 — Login server advertises game server as 127.0.0.1

- **Status:** FIXED (2026-08-02, local config change)
- **Severity:** Medium — LAN clients can't reach the world
- **Component:** docker-compose.yaml (GameServers env injection)

## Symptom
Client connects to login, world select hangs or fails — login server tells the client the
game server lives at `127.0.0.1` (the container's own loopback).

## Root cause
`docker-compose.yaml` ships:
```yaml
GameServers__0__Host: 127.0.0.1
```
Fine for same-box testing, wrong for LAN play.

## Fix
```yaml
GameServers__0__Host: 192.168.0.165
```
in `/root/AAEmu/docker-compose.yaml`.

## Note
Lives in the compose file, not `.server_files` configs — a compose regen/update will clobber it.
