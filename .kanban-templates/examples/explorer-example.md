# EXAMPLE — explorer.md filled in (real deep-dive: quests domain report)

> This is the explorer.md template filled with a REAL exploration (the quests
> deep-dive that produced scorecard-explorations/quests.md and discovered the
> BUG-006 family). It is the canonical example — including the budget lesson.

# EXPLORER TEMPLATE — deep-dive / recon (feeds the scorecard)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a PR to upstream AAEmu/AAEmu unless Josh explicitly approves it.**
> Everything stays in our own lane on joshhmann/AAEmu. This rule applies to
> every template in this directory and every task that uses one.

> Fill every section. Delete nothing. This is the contract for the task.
> Explorations produce KNOWLEDGE, not code. Output = a report committed to
> `scorecard-explorations/<domain>.md`. No code changes unless the report
> explicitly spawns a fix/feature card.

## Division routing (who owns which phase)

| Phase | Sister | Owns | Handoff out |
|-------|--------|------|-------------|
| Explore | any sister | the deep-dive itself, evidence, the report | report → Nei |
| Route | **Nei** | turns findings into fix/feature cards with the evidence packet | cards → Tai/Rei |
| Verify | **Rei** | only if the report makes code claims (evidence must be checkable) | signoff → Nei |
| Track | **Nei** | SCORECARD.md row + STATUS.md update from the report | STATUS.md → everyone |

Notes: no Rei gate for pure reports (no code) — but every claim must be
verifiable. Mai is only involved if the exploration needs box access
(ssh aaemu, prod data reads).

Collaboration context: `sister-council` skill (how we convene), `affinity-system` skill (how we collaborate).

## Get up to speed (first 10 minutes, in order)

1. `cat /root/aaemu-dev/VISION.md` — two lanes + division routing
2. `cat /root/aaemu-dev/WORKFLOW.md` — process + lane gate
3. `grep -n "quests" /root/aaemu-dev/SCORECARD.md` → quests row: 85 tables / 70 wired / 82%
4. `ls /root/aaemu-dev/scorecard-explorations/` → quests.md exists? (first run: no — this report creates it)
5. `cd /root/aaemu-dev && graphify explain "QuestManager" --graph graphify-out/graph.json` and `graphify affected "QuestManager" --depth 2`

## Canonical 1.2 grounding (NEVER invent mechanics)

- **The 1.2 data is the source of truth.** If code and data disagree, the DATA wins (we fix the code).
- Live sqlite: `ssh root@192.168.0.165` + python3 sqlite3 on `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3`
  → verified live: quest_act_obj_aliases has **2,746 rows** but is never SELECTed (0 refs in AAEmu.Game).
- Canonical resources: SCORECARD.md "Canonical resources" table.
- If you cannot verify a claim, mark it **UNVERIFIED** — do not guess.

## Goal shape (pick ONE — a scattershot exploration is a wasted budget)

- **A scorecard domain** (zero-data-wired / partial / low-% row): ✅ THIS RUN —
  quests domain (85 tables, 82% wired) + the upstream "30 quest issues" cluster:
  why broken quests are broken, which are engine bugs vs data gaps vs world-lifecycle.

## Evidence requirements (every claim must be checkable)

- **Code claims:** file:line — e.g. `QuestManager.Load()` at QuestManager.cs:234-265; `LoadDetailQuestActTemplates` at :531 (64 separate `quest_act_*` SELECT blocks); `RunAct` copy-paste at QuestActConAcceptNpcKill.cs:19-25; `AddQuest` at CharacterQuests.cs:69-159; `GoToNextStep` at NewQuestCode.cs:95-161
- **Data claims:** table + query + row counts — `quest_act_obj_aliases`: 2,746 rows, 0 code refs; `quest_contexts`/`quest_components`/`quest_acts` loaded; `quest_cameras`/`quest_names`/`quest_mail_*` (5)/`quest_tasks`/`quest_monster_groups`: zero refs
- **Upstream claims:** issue # + what it says — #1208→quest 1119 (Arcum Iris), #1255→quest 922 (Explosives Pit doodad, TODO at QuestActObjInteraction.cs:60), #1257→quest 111 (Red Poppy spawner), #1329→quest 3889 (item-use, sphere 789), #1450→quest 3447 (doodad 4252 lifecycle)
- **No invented mechanics.** Unverifiable → `UNVERIFIED`.

## Report format (`scorecard-explorations/quests.md`)

1. Header: date 2026-08-03, scope quests engine, explorer, repo state
2. Data surface: 65 quest_act_* tables — 64 loaded + 1 never SELECTed (aliases)
3. Code wiring: QuestManager load path file:line, runtime model, accept/progress/complete flows, persistence
4. Gaps: alias table dangling FKs; zero-ref quest tables (cameras, names, mail, tasks, monster_groups); tutorial category 45 skipped at :508-510
5. Upstream issue mapping: 5 issues → real quest ids + root-cause class
6. Priority fix list: **#1 kill-acceptor family (→ t_71e48494)**; aliases gap; per-issue follow-ups
7. Status section: verified items, open questions (doodad phase-change TODO)

## Budget awareness (CRITICAL — the quests explorer trap)

- ✅ Done here: report skeleton written to disk after the first pass (~30% of
  budget) with the load-path evidence; refined in 2 more passes; committed as
  `docs: scorecard explorations — quests (...)` BEFORE the fix work started.
- ⚠️ The trap this template exists to prevent: a sibling explorer (partial-domains
  run) kept findings only in the final message — when budget died, half the
  evidence was lost and had to be re-collected. Write EARLY, commit, refine.

## Status / awareness (close the loop)

- Report committed → SCORECARD.md quests row unchanged (82%) but report adds
  "Fork fixes" pipeline: BUG-006 card filed (t_71e48494) with the evidence packet.
- One-line "what changed": "quests explorer report landed; found 380-quest
  kill-acceptor bug family (filed as BUG-006/fix card) + quest_act_obj_aliases
  gap (2,746 rows unwired); 5 upstream issues mapped to real quest ids."

## Deliverables

- Commits: the report (`scorecard-explorations/quests.md`)
- Push: fork origin ONLY. **NO upstream PR.**
- Report: scorecard-explorations/quests.md + one-line "what changed" + card t_71e48494 filed
