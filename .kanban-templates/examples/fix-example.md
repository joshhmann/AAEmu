# EXAMPLE — fix.md filled in (real task: t_71e48494, BUG-006 kill-acceptor)

> This is the fix.md v2 template filled with a REAL completed task, so new
> workers can see the expected shape and depth. Every section was actually
> filled this way. Copy the shape, not the content.

# FIX TEMPLATE — Track 1 canonical fix (strict workflow, lane gate: NO upstream PR)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a branch or open a PR to upstream AAEmu/AAEmu.**
> Everything stays in our own lane on joshhmann/AAEmu. This rule applies to
> every template in this directory and every task that uses one.

> Fill every section. Delete nothing. This is the contract for the task.

## Division routing (who owns which phase)

| Phase | Sister | Owns | Handoff out |
|-------|--------|------|-------------|
| Implement | **Tai** | branch, code, tests, evidence, graphify | branch + test evidence → Rei |
| Verify | **Rei** | QA gate: repro case, regression check, evidence signoff | verified status (file:line + test results) → Nei |
| Blocked / stuck / deploy | **Mai** | unblocking, logistics, handoffs, prod deploy to the aaemu box | field-ready state → Tai/Rei |
| Track | **Nei** | SCORECARD.md + STATUS.md + fix log currency | STATUS.md → everyone |

**Verification handoff contract (non-negotiable):**
- Tai **cannot** mark this task complete without Rei's evidence gate.
- Rei signs off with: file:line of the change + test results (fail-before/pass-after output pasted into the task).
- Prod deployment is Mai's coordination after Rei's signoff and the deployment decision.

Collaboration context: `sister-council` skill (how we convene), `affinity-system` skill (how we collaborate).

## Get up to speed (first 10 minutes, in order)

1. `cat /root/aaemu-dev/VISION.md` — two lanes + division routing
2. `cat /root/aaemu-dev/WORKFLOW.md` — process + lane gate
3. `grep -n "quests" /root/aaemu-dev/SCORECARD.md` — domain status → quests 85 tables / 70 wired / 82%
4. `ls /root/aaemu-dev/scorecard-explorations/` → quests.md present — read it
5. `cd /root/aaemu-dev && graphify explain "QuestActConAcceptNpcKill" --graph graphify-out/graph.json`
   and `graphify affected "QuestActConAcceptNpcKill" --depth 2`

## Canonical 1.2 grounding (NEVER invent mechanics)

- **The 1.2 data is the source of truth.** If code and data disagree, the DATA wins (we fix the code).
- Live sqlite: `ssh root@192.168.0.165` + python3 sqlite3 on `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3`
  → **verified live: 380 quests have ALL Start acts as `QuestActConAcceptNpcKill`** (182, 205, 556, 913,
  1057, 1079, 1082, 1089, 1165, 1208…), 1043 distinct NPCs, all resolving.
- Canonical resources: SCORECARD.md "Canonical resources" table.
- Upstream issues: #1208 (quest 1119) checked — it is a plain Npc-accept quest, NOT part of this family.

## Bug

- **Source:** quests explorer report — scorecard-explorations/quests.md §2-3; upstream issue family #1208 et al.
- **Symptom (user-visible):** quests whose Start component is a kill-accept act can never start — the client offers the quest, the server never advances it past Start; the quest sits stuck.
- **Root cause (code, file:line):** `QuestActConAcceptNpcKill.RunAct` (AAEmu.Game/Models/Game/Quests/Acts/QuestActConAcceptNpcKill.cs:19-25) is a copy-paste of the Npc accept check (`QuestAcceptorType.Npc`), and no code path ever adds a quest with a Kill acceptor (`QuestAcceptorType` had no Kill value; `EngageCombatGiveQuestId` at Unit.cs:1636-1640 uses `AddQuestFromNpc` → acceptor Npc; `DoOnMonsterHuntEvents` at QuestManagerEvents.cs:169-203 never offered quests).

## Plan (order matters)

1. **Branch:** `fix/quest-kill-acceptor` off develop
2. **Understand:** graphify explain + Npc.cs death path (DoOnMonsterHuntEvents call sites Npc.cs:877/986/1019) + Unit.cs:1636-1640
3. **Implement:** `QuestAcceptorType.Kill = 7` (Static/QuestAcceptorType.cs); `RunAct` matches `Kill && AcceptorId == NpcId`; `QuestManager.BuildKillAcceptQuestIndex()` (NpcId→questIds from Start-component kill-accept acts, built in `Load()`); `DoOnMonsterHuntEvents` starts matching quests via `AddQuest(questId, false, Kill, npc.TemplateId)`; defensive despawn check in `AddQuestFromNpc`
4. **Wire-up:** n/a (existing manager paths)
5. **SQL:** none (no schema touched)
6. **Tests:** `QuestActConAcceptNpcKillTests` — `RunAct_WithKillAcceptorAndMatchingNpcId_ReturnsTrue`, `RunAct_WithKillAcceptorAndMismatchedNpcId_ReturnsFalse`, `RunAct_WithNpcAcceptor_ReturnsFalse` (regression), `RunAct_WithUnknownAcceptor_ReturnsFalse`

## Tools (use these, in this order)

- graphify: `cd /root/aaemu-dev && graphify explain "X" --graph graphify-out/graph.json`
- editor: read_file / patch / write_file in /root/aaemu-dev
- build: `dotnet build --configuration Release AAEmu.slnx`
- compiler-check: `dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check`
- tests (full): `./scripts/gate.sh`
- tests (filtered): `./scripts/gate.sh QuestActConAcceptNpcKillTests`

## Verify (ALL must pass before handoff to Rei)

- [x] Release build: 0 errors
- [x] compiler-check: "Compilation successful"
- [x] Full test suite: 0 failed (1082/1082, Debug + Release)
- [x] New test(s) fail without the fix, pass with it — BEFORE: `RunAct_WithKillAcceptorAndMatchingNpcId_ReturnsTrue` FAILED (Expected true), `RunAct_WithNpcAcceptor_ReturnsFalse` FAILED (Expected false); AFTER: all pass
- [x] `graphify update .` (17,605 nodes / 40,808 edges)
- [x] SCORECARD.md row updated IN THIS BRANCH ("Fork fixes" section, BUG-006 row)
- [x] Fix log: ISSUES.md index row + bugs/006-kill-accept-quests.md (status, root cause, files, tests)

## Rei verification gate (evidence required — this task is NOT done without it)

- [x] Rei: repro case / fail-before-pass-after output — attached (test run output on task t_71e48494)
- [x] Rei: regression check on neighbor paths — quests act family + QuestManagerEvents paths reviewed
- [x] Rei: signoff posted to the kanban task — file:line + test results (task comment)

## Status / awareness (close the loop — every task ends with "what changed")

- One-line: "BUG-006: added QuestAcceptorType.Kill + kill-accept quest start wiring; 380 quests unstartable → startable on kill; 3 commits (fix/test/docs) on fix/quest-kill-acceptor; merged to fork develop @05428e0; 1082/1082 tests; scorecard quests row + ISSUES.md/bugs/006 updated; prod deploy pending deployment decision."
- Nei: STATUS.md updated from this line (per-lane: Tai done / Rei evidence in / Mai deploy pending / Nei tracking).

## Deliverables

- Commits: b28ee5a `fix(quest): add Kill acceptor type and start kill-accept quests on NPC death` · 03f88e7 `test(quest): cover QuestActConAcceptNpcKill acceptor matching` · d385583 `docs: log BUG-006 kill-acceptor fix, update quests scorecard row`
- Push: branch to fork origin ONLY. **No upstream branch push or PR.**
- Report: summary + test evidence (fail-before/pass-after) + scorecard diff + STATUS.md one-liner — all on the kanban task comment
