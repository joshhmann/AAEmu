# PB-007 live handshake — current source-pinned report (2026-08-27)

## HEAD

`3871459d142fdd1767b9365a1de8d4cd3652ab0e`

## Exact command

```bash
E2E_ROOT=/tmp/aaemu-pb007-final-e2e E2E_LOGIN_PORT=14237 E2E_GAME_PORT=14239 E2E_STREAM_PORT=14250 E2E_BRIDGE_PORT=14260 E2E_INTERNAL_PORT=14234 E2E_WEBAPI_PORT=14280 E2E_DB_PORT=14306 COMPOSE_PROJECT_NAME=aaemu_pb007_final E2E_REBUILD=1 dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj --no-build --filter-method 'AAEmu.IntegrationTests.E2e.PvpHandshakeE2eTests.ForceFlag_Aggression_Handshake_And_PeaceBlock_OnLiveServer' --output Detailed --no-ansi
```

Result: passed 1/1 in 2m09.910s.

## Environment and ports

- `E2E_ROOT=/tmp/aaemu-pb007-final-e2e` (fresh isolated root; cleaned after capture)
- `COMPOSE_PROJECT_NAME=aaemu_pb007_final`
- Login `14237`; Game `14239`; Stream `14250`; BotDrive bridge `14260`; internal `14234`; WebApi `14280`; isolated MySQL `14306`
- Existing assets only; no client/server asset downloads

## Stage results

- `PROVISION`: PASS — two real Nuian TCP bots, level 40, co-located; attacker objId 22011, victim objId 44109.
- `HOMELAND-SHIELD`: PASS — no combat frames at spawn zone 179.
- `RELOCATE-STEPPE`: PASS — both bots at zone 136 in conflict group 14.
- `LIVE-ZONE-STATE`: PASS — conflict group 14 exists and is open; boot state is Peace.
- `FLAG-FORCEATTACK`: PASS — `SCForceAttackSet` enabled and Bloodlust 1482 observed.
- `AGGRESS-ALLOWED`: PASS — victim-matched non-immune `SCUnitDamaged=True`; immune frames excluded=False; `SkillFired=True`; Retribution 2167=True; bloodstain doodad 877 objId 44294; crime branch observed.
- `PEACE-BLOCK`: PASS — no victim-matched non-immune `SCUnitDamaged` with ForceAttack OFF; immune frames excluded=False.
- `WAR-HONOR`: DEFERRED — intentionally not passed; requires more than 251 real hostile kills and the conflict timer.
- Deterministic compressed-parser tests: PASS 2/2.

## Evidence boundaries

This report closes only the narrowly defined PB-007 flagged-aggression handshake requirement. It does not claim all PvP or honor scope, does not pass `WAR-HONOR`, and does not promote H (human) evidence. Historical immune-tagged/untrusted live results and prior parser/framing failure context remain preserved in the dated documentation and reports.
