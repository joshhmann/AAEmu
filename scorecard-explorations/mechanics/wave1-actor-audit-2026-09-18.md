> Provenance: reaudit workstreams A/B, 2026-09-18, HEAD d0bea9e6a011d97d60157718996cfea7013ea7af.
> Tracked copy: scorecard-explorations/mechanics/wave1-actor-audit-2026-09-18.md (authoritative; /tmp original ephemeral).
> Body below is byte-identical to the /tmp source.

# Actor Audit — every PlayerBot capability names its actor (read-only)

Doctrine: a bot capability must name a bot actor exercised through real engine paths;
human execution is never bot evidence; synthetic success never earns live/H verdicts.

## Gate/claim table

| Gate / claim | Current actor | Evidence file:line | Attribution check |
|---|---|---|---|
| M1 quest spine | human-performs (solo curated route, REQ-M1-10) | Field-Guide:522–547; SCORECARD H UNKNOWN | Clean: automated CLOSED kept separate; H UNKNOWN. |
| M2 golden path | human-performs (two players/clients) | Field-Guide:551–564 | Clean: bot baseline labeled proxy, never H=2. |
| M3a homestead shell | human-performs (two players, adjacent, one session) | Field-Guide:566–580; tab `dashboard_server.py:1664` | **Watch F1**: "COMPLETE on bot-functional/proxy" + tab "bot-functional COMPLETE" — proxy wording adjacent to a human gate; H UNKNOWN is stated, but a skimmer could bank the COMPLETE. |
| M3b persistence | human-performs (same two homesteads, N≥3 cycles) | Field-Guide:582–597 | **Watch F2**: R=2 engineering evidence is real but is not H; guide keeps H UAT open — correct. |
| M4 trade run | human-performs (four players, no GM repair) | Field-Guide:599–617; tab line 1676 | Clean: scripted proxy CLOSED, human deferred #4, H UNKNOWN. |
| M5/M5.3 feel | human-performs (movement regrade) / N-A core | Field-Guide:619–634 | Clean: N/A core, H U. |
| M6 soak/restart | none (operational gate) | Field-Guide:636–648 | Clean: H N/A with written reason. |
| M7 party | none (DO-NOT-RUN, prereq blocked) | Field-Guide:650–675; tab line 1694 | Clean iff blocked: invite-refusal = FAIL/blocker, never pass. **Watch F3**: M7B row tempts premature runs. |
| M8 village day | human-observes + automation-proves | Field-Guide:677–712; QUALIFIED runtime exit, C1/C2 H UNKNOWN | **Watch F4**: QUALIFIED ≠ H; PB-FARM-01 freeze/wander traps named. |
| M1B/M3B/M4B bot track | bot-performs (aspiration) | Field-Guide:452–458 | Open by declaration: rig evidence / contract-only / no live proof. Clean. |
| M7B/M8B bot track | none (mixed prereqs unverified) | Field-Guide:457–458, 500–516 | Clean: explicitly blocking, worksheet-only. |
| Wave One 1 (records) | none (docs) | ROADMAP:101 | N/A — no capability claimed. |
| Wave One 2 (evaluator) | synthetic harness | ROADMAP:102; reports carry `is_synthetic` | Clean post-fix: synthetic labeled, never live. |
| Wave One 3 (provisioning) | bot-performs via real `Provision` path | ROADMAP:103; `HeadlessSessionProvisioningTests` 12/12 | Clean: opt-in grant through production path; default grants nothing. |
| Wave One 4 (planner) | unit harness (no actor) | ROADMAP:104; `GoapPlannerTests` 17/17 | Clean: search-correctness only, no autonomy claim. |
| Wave One 5 (observations) | rig actor + live projection reads | ROADMAP:105; `GoapRuntimeTests` 18/18 | Clean: projection truthfulness, not task competence. |
| Wave One 6 (GCD executor) | synthetic rig actor + REAL Skill gate fields | ROADMAP:106; `CombatExecutorTests.cs:61–89` (`GameplayActorTestRig` actor; `GlobalCooldown`/`SkillLastUsed` asserted) | Clean: capability is timing ownership only. **Watch F5**: 9/9 must never be cited as combat competence. |
| Wave One 7 (one real action) | bot-performs, live lane, real `BuildHouse` | ROADMAP:107; `HomesteadClaimPlacePlotE2eTests` 1/1 | Clean: ordinary service, resolved target, conservation asserts. |
| Wave One 8 (loop) | bot-performs, single bot, live lane | ROADMAP:108; dossier legs 1–10 | **Flag F6**: leg 2 setup-positioning is disclosed setup, NOT traversal proof (dossier:21 says so); ten-bot claims explicitly unsupported (dossier:120). |
| 25 ambient citizens | liveness only (roam observable) | tab `dashboard_server.py:1629`; reconciled 25 active | **Watch F7**: presence ≠ capability; tab correctly normalizes invite-ignore (except M7) and interference=FAIL. |
| Dashboard Human Gates tab | human-performs BY DESIGN (worksheets for Josh) | `dashboard_server.py:1624–1708` | Clean: sends Josh to perform human gates only; bot rows labeled proxy/open; no bot claim rests on human hands. |

## Flagged list (file:line)

- **F1** `Docs/wiki/Human-Gate-Field-Guide.md:570-571`, `dashboard_server.py:1664` — "COMPLETE on bot-functional/proxy" beside human gate; recommend "COMPLETE (proxy only, H UNKNOWN)" phrasing.
- **F2** `Field-Guide.md:589-590` — R=2 vs H UAT boundary; currently correct, keep guarded.
- **F3** `Field-Guide.md:664-671`, tab 1694 — M7B must stay DO-NOT-RUN until consent path lands.
- **F4** `Field-Guide.md:683-687,694-695` — QUALIFIED exit + PB-FARM-01 traps; H UNKNOWN (not DEFERRED).
- **F5** `CombatExecutorTests.cs:35-89` — timing-ownership evidence; never combat competence.
- **F6** `step8-bot-loop-dossier-2026-09-17.md:21` + `:120` — setup≠traversal; single-bot ≠ population.
- **F7** `dashboard_server.py:1629` — ambient liveness framing; correct as written, recheck on any edit.

No record found letting a bot claim rest on human hands, synthetic mutation, or mere presence.
