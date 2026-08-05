# Dropped Content Register — quest_contexts & dangling rows

**Purpose:** durable record of every quest context / data row DROPPED from the
fork's canonical 1.2 data handling, with decision provenance, execution cards,
and restore pointers. "In case we need to know" — grep this file before
re-adding, restoring, or re-flagging any id listed here.

**Decision authority:** Josh (2026-08-05 chat, msg 1534679020862701689):
*"Unblock granted, if they're orphans we prob don't need to code em in."*
Drop = data-level deletion / prune via SQL patches + verifier allowlist
removal. No code written to keep dead content alive.

All ids reference the canonical `compact.sqlite3` (md5
`78b3bdbf0383db3b927056106efdf91af`) — READ-ONLY reference; drops are applied
via `SQL/patches/compact/*.sql` guarded DELETEs + in-memory overlay where
needed, never by editing the reference file.

---

## 1. Dummy shells — 1391

| Field | Value |
|---|---|
| Quest | 1391 마을을 지켜라 ("Protect the Village") |
| Shape | 0 components / 0 acts; milestone_id=5, let_it_done='t', cat 27, zone 0, lvl 0 |
| Verdict | data-defects.md §6 (c) drop — deliberate dummy shell, allowlist-masked to INFO |
| Drop action | delete `quest_contexts` row 1391 + remove 1391 from verifier allowlist (QuestSanityVerifier.cs:93 "dummy shells" group) so a regression re-reports at WARN |
| Execution card | t_5a61cee3 (impl, ready) → Rei gate t_70ae1bba → census t_e239aa09 |
| Rig | fix/no-components-1391-rig @ 405e85b5 — flip to assert absence |
| Restore pointer | None — no canonical content exists (that's why it's a shell). Rebuild only if a real quest with this shape is sourced from client data. |

## 2. QUEST_NO_START cluster — 23 legacy tutorial shells

| Field | Value |
|---|---|
| Quests | **1533, 1535–1549, 1551–1554, 1640, 1830, 1831** (1534/1550 are pure id gaps — nothing to delete) |
| Shape | each has exactly one kind-8 (Reward) comp with QuestActSupplyCopper + QuestActSupplyExp; 1830/1831 "UNUSED" empty; zero Start comps, zero accept surfaces |
| Origin | legacy 1.0-era numbered tutorial step list (튜토리얼… 메인퀘저널), zone 1 `w_gweonid_forest_1` (old Nuian starter), cat 28 — superseded by the Solzreed opening (golden route) |
| Verdict | data-defects.md §5 (c) drop |
| Drop action | delete 23 quest_contexts + their quest_components/quest_acts rows via SQL patch; remove cluster ids from verifier allowlist (QuestSanityVerifier.cs:84-109) |
| Execution card | t_5140fb35 (impl, ready) → Rei gate t_f884383f → census t_d5e7d11f |
| Rig | fix/no-start-1533-rig @ 9370e985 — flip to assert absence |
| Restore pointer | **These shells are the skeleton to reuse if a 1.2-era tutorial is ever rebuilt** (data-defects.md §5). |

## 3. Orphaned quest_contexts — 8 (of 28 audited)

| Field | Value |
|---|---|
| Quests | **745, 1421, 1954, 1955, 1956, 1957, 1958, 2140** (full bodies survive: 3–10 comps each; context rows + texts deleted upstream) |
| Chain | cat-34 crafting chain: 1954→1955→1956→1957→1958→1959(live)→1960→1961→2140→2141→2142→2143→2144(live)→2145→2146 (data-defects.md §4) |
| Verdict | data-defects.md §7 (c) drop all 28 audited orphans; this card covers the 8 in the M1 widened backlog |
| Drop action | prune dangling unit_reqs gates **16064, 19197, 19198, 19201, 19205, 19207** (+ optional sphere_quests 418, sphere_accept_quest_quests 3) via `SQL/patches/compact/2026-08-05-drop-8-orphaned-contexts.sql` |
| Already pruned (do NOT re-prune) | unit_reqs 16000 + item_accept rows 5133/6420 — covered by `2026-08-04-fix-quest-data-defects.sql` on develop |
| Execution card | t_0ac25620 (ready) → Rei gate (to be filed on impl block) |
| Correction on record | data-defects.md's "745 blocks quest 2951's Supply" is an **id-collision misread**: unit_reqs 16000 is Skill-owned (gates skill 12913 가방 증기), engine keys by (owner_type, owner_id); 2951's real gates resolve — the prune is hygiene, NOT a 2951 unblock |
| Restore pointer | Chain is ruled dead (data-defects.md §4). Restoring any orphan requires the full chain context; do not re-add single contexts. |

## 4. Dangling accept-acts (chain B prune, not a context drop)

| Row | Details |
|---|---|
| quest 2145 Reward comp 9927 | accept-act `quest_act_con_accept_components` id 89 + `quest_acts` 14121 → 2146 (dropped orphan) |
| quest 1960 comp 9794 (sibling) | accept-act 75 → 1961 (dropped orphan) |
| Execution card | t_60a559ab (impl, running) → Rei gate t_53baa876 → census t_20b1bfb7 |

## 5. Related fix — NOT dropped (for contrast)

| Item | Status |
|---|---|
| quests 330/776/777 COMPONENT_NEXT_MISSING | **FIXED** via additive in-memory overlay QuestDataOverlay (1520→1521, 3480→3482, 3488→11591) — branch fix/next-missing-776-777 @ aa35a503, Rei gate t_d8a8c798. 330 is golden-route (step 3, zero runtime impact). |

---

## How to check if an id is in this register

```bash
grep -n "745\|1421\|1391\|1533\|2140\|1954" scorecard-explorations/dropped-content-register.md
```

Before filing any future quest-defect card, check this file — a "missing"
quest may be here by decision, not by accident.
