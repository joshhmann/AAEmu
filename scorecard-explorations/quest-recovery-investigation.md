# Quest recovery investigation — E164

The bot recovered in ordinary gameplay; the current recovery checker cannot prove a chain that includes the first resumed move timing out. E163 remains FAILED. No gameplay or test code changed and no new server ran.

## What happened

All times below are UTC from immutable E163 request records. Character 1, actor 44105, enrollment generation 1 retained quest 251.

| Event | Time | Authoritative outcome |
| --- | --- | --- |
| Controlled interruption of e04c7bc1-36c2-4b9d-9b86-124673c9b029 | 18:45:16.9220193 | Interrupted, target 44319 |
| Fresh autonomous move ff041409-2f17-4572-9d5f-6477e1e0e2af | 18:45:22.0489691 | Running, same quest and target, new decision cycle |
| First resumed move ends | 18:45:31.148792 | TimedOut / Navigation: navigation budget exceeded |
| Ordinary attack of that same boar | 18:45:41.2831312 | AutoAttack Completed (attack started; this row alone is not a kill) |
| Another independently selected target, 44332 | 18:46:06.7924957 → 18:46:09.1940976 | Distinct pursuit request Completed |
| Ordinary item 4058 loot | 18:48:11, 18:49:22, 18:50:33 | Captured quantity 0 → 1 → 2 → 3 |
| Quest 251 turn-in, then independent quest 324 acceptance | 18:51:27 → 18:51:32 | Both Completed through ordinary gameplay |

Raw replay here means a Python inspection of persisted observations, not execution of the C# consumer. It inspected 894 OBSERVE snapshots and 5,309 distinct audit request IDs; no repeated terminal row changed. Nine observations captured the fresh move, with the same actor, generation, parent quest, target, policy and cycle. Between 18:45:22.9455734 and 18:45:30.9757423, actor displacement was 36.318 metres. The server log records initial target distance 48.2 metres, speed 4.5 metres/second, and elapsed budget 10.1 seconds at timeout. Source sets a 10-second pursuit budget. The next decision records target distance 3.2 metres and starts attacking it. These facts support an expiring chase with substantial progress, not a missing boar or a stationary bot. They do not establish every movement tick, terrain correctness, NPC fleeing behavior, or a universally appropriate chase budget. The request wall timestamps span about 9.10 seconds while the actor reports 10.1 elapsed; resolving that clock/tick difference is outside this proof-chain investigation. No `stuck:` terminal or teleport witness is credited.

## Why the evidence fails

BotShortRunEvidence.ObserveLive selects the first fresh qualified move once. ObserveMoveTerminal accepts only Completed or Interrupted for that exact fresh request. TimedOut therefore sets _recoveryScopeLost permanently. Later ObserveLive calls cannot choose another request because both that flag and the stored fresh GUID prevent reconsideration. TryControlledRecovery then excludes every later authoritative same-quest effect. This reproduces the existing failed verdict by source and captured input; it is not a newly executed regression test.

The rejection protects against false attribution. Simply adding TimedOut to successful outcomes would be wrong. The timeout remains a failed move, and later progress must be linked to a distinct, correctly owned, independently issued request and real quest effect.

## Request ownership and bounded recovery

GameplayActor.Tick expires the active request, records its terminal through Finish, and clears movement/craft state. Finish records the outcome once when the terminal transition succeeds and clears _active only if it is that exact request. ClearMovementState drops destination, waypoints, speed and recovery state. TryBegin rejects live overlap and refuses re-execution of an idempotency key whose prior outcome is Completed, Interrupted or TimedOut. Later accepted requests and immutable old terminal rows corroborate release in the captured case; they are not an exhaustive proof of duplicate-free dispatch in all cases.

TravelLegDispatch.MapLegOutcome keeps TimedOut as TimedOut for the matching target and move owner; it does not relabel the failure. Existing journey budgets/terminal decisions stay authoritative. This investigation does not change their give-up or source-readmission policy, or prove its entire live denominator. Same-NPC continuation and seeded negative eligibility evidence remain historical E157/E136; E163's 251 and 324 used different NPCs.

## One next bounded slice

Reproduce this interrupted → first resumed TimedOut → distinct same-quest request → authoritative loot chain as a deterministic consumer regression before editing the consumer. If justified, track a finite recovery chain inside the existing evidence consumer, preserving failed terminals, immutable ownership and explicit retirement. Proposed owned files: AAEmu.IntegrationTests/E2e/BotShortRunEvidence.cs and BotShortRunEvidenceTests.cs, plus their dossier/state. No gameplay scheduler, quest selection, navigation speed/budget, teleport, second brain, admin expansion or live campaign is needed for that reproduction.

Require a hard candidate-count bound as well as existing 900-second observation / 300-second no-progress limits. Missing ownership, mutated terminal tuple, reused victim GUID/cycle, wrong actor/generation/quest, snapshot discontinuity, ambiguous request-local effects, and exhausted bounds must still refuse recovery. Movement, timeout, queued interruption, or an attack-start row alone never earns quest progress. Preserve existing successful fallback/pursuit cases and negative tests. Acceptance requires red reproduction, green bounded chain, negative controls, appropriate local checks and Main review; live acceptance remains separate. Do not open an implementation record until these readiness facts and the fixed contract are reviewed.

## Provenance and review limits

Main develop HEAD 2f93a2fc998704835c856b68ebefaf406b9c2840 had seven inherited dirty documentation/UI paths. Candidate fix/quest-useful-choice-2026-10-11 HEAD 1da6ede32146ecfe234063c3329be47be97f4ce3 had 17 inherited dirty paths; all nine tested source hashes match E163. This dossier adds one documentation path, making 18 candidate dirty paths. Baseline/worktree/foreign-process ownership and exact old artifact SHA256s are in baseline.json and raw-audit.json. No live/build/check resources allocated; existing foreign processes untouched.

Catalog-first read-only archaeology MCP invoked with the prebuilt candidate Release DLL using list_sources/list_tables, then bounded read_file calls. Canonical compact.sqlite3 MD5 remains 78b3bdbf038db3b927056106efdf91af, 679 tables, version 1.2 r208022. Inputs, raw outputs and provenance are retained in mcp-*.jsonl/mcp-provenance.json. Code evidence is textual; source_id/version are null where returned, absolute source paths and intentionally truncated byte windows are preserved. See scorecard-explorations/mechanics/archaeology-mcp-acceptance.md. Integration/E2E source is read locally because archaeology excludes that root. No external corpus claim was needed to diagnose this AAEmu-specific captured chain.

Commands/environment: Python 3 JSONL streaming and SHA256/MD5 checks, git status/rev-parse/worktree inventory, bounded rg/sed source reads, prebuilt dotnet AAEmu.ArchaeologyMcp/bin/Release/net10.0/AAEmu.ArchaeologyMcp.dll on Linux with explicit AAEMU_ROOT/ARCHEAGE_DB_PATH and version; AAPak unset. No build/compiler/unit/integration gates rerun for this read-only investigation; no fresh pass/fail/skip or BotControl smoke claim. Historical E162 checks are retained as historical only. Main performed source/artifact review; no independent reviewer was dispatched. No commit, merge, push or deployment; E130 merge status remains E145/D40.

Seeded/bypassed steps are unchanged from E163: level-10 starter/equipment and one wake supplied; no assigned quest or target, injected completion/rewards/prerequisites, forced save, death, kill, loot or teleport. Capability before/after is unchanged; the diagnosis and next proof dependency are now more precise. No full autonomous loop, repeatability, human/client/H or parent milestone promotion.
