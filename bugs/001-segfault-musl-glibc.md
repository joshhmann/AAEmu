# BUG-001 — Game container crash-loops with SIGSEGV (exit 139) during AiGameData load

- **Status:** FIXED (2026-08-02)
- **Severity:** Critical — server never booted
- **Component:** AAEmu.Game Docker image (NLua native lib)
- **Reproduced:** Every boot, ~35s in, always at `GameDataManager - Loading AiGameData`

## Symptom
- `docker compose ps` shows `Restarting (0)` every ~35s — the `(0)` is misleading
- App log ends silently at `Loading AiGameData`, no error line, no stack trace
- Docker daemon log (`journalctl -u docker`) reveals the truth: `exitCode=139` (SIGSEGV)

## Root cause
NLua 1.7.9 bundles a **glibc-built** `liblua54.so` (Lua 5.4.8), but the game Dockerfile
uses `mcr.microsoft.com/dotnet/runtime:10.0-alpine` (musl libc). `ldd` shows unresolved
glibc fortify symbols: `__snprintf_chk`, `__memcpy_chk`, `__longjmp_chk`, `__fprintf_chk`.
The binary loads lazily and runs fine until the Lua parser hits an error needing number
formatting (`luaO_pushfstring` → `addnum2buff`), calls a missing symbol, and jumps to a
garbage address. First poison script is a npc_ai_params entry parsed during AiGameData load.

## Evidence
- gdb backtrace: `luaL_loadbufferx → luaY_parser → constructor → luaO_pushfstring → addnum2buff` at `0x7276` (garbage jump)
- `ldd /app/runtimes/linux-x64/native/liblua54.so`: `Error relocating ... __snprintf_chk: symbol not found`

## Fix
- `AAEmu.Game/Dockerfile`: runtime stage `dotnet/runtime:10.0-alpine` → `dotnet/runtime:10.0` (glibc/Debian)
- `apk add mysql-client` → `apt-get install -y --no-install-recommends default-mysql-client`
- Rebuild + recreate container. Original saved as `AAEmu.Game/Dockerfile.bak-musl`

## Upstream
AAEmu develop bug — alpine runtime + glibc NLua native lib. Should be filed on
https://github.com/AAEmu/AAEmu. Any `docker-update-local.sh` re-run will resurrect it;
re-apply the Dockerfile change after updates.
