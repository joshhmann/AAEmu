# EXPLORER TEMPLATE — deep-dive / recon (feeds the scorecard)

> 🚫 **THE RULE (Josh, permanent — sits ABOVE every other rule in this repo):**
> **NEVER push a branch or open a PR to upstream AAEmu/AAEmu.** Upstream is
> intake-only; everything stays on joshhmann/AAEmu. This rule applies to
> every template in this directory and every task that uses one.

> 📐 **UPSTREAM ALIGNMENT (locked 2026-08-04 — applies to every card):** target
> `develop` + .NET 10; Aspire for local dev, prod stays Docker Compose;
> `compact.sqlite3` read-only; config precedence `Config.json` →
> `Configurations/*.json` → `Config.Local.json`; `GameServers` config, not
> legacy `game_servers`; explicit constructor deps where supported;
> parallel-safe startup loading; AAEmu-native terminology
> (Doodad/Mate/Slave/Transfer/Expedition/Dominion/Ability/ActAbility);
> PlayerBots compose around ordinary `Character` records; additive layer only
> (composition/adapters/extension points, narrow reviewed core hooks). Full
> text + verification: `Docs/wiki/Development-Conventions.md`.

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

1. `cat <repo>/VISION.md` — two lanes + division routing
2. `cat <repo>/WORKFLOW.md` — process + one-way upstream gate
3. `grep -n "<domain>" <repo>/SCORECARD.md` — domain status
4. `ls <repo>/scorecard-explorations/` — read prior reports for the domain
5. `cd <repo> && graphify explain "<Type>" --graph graphify-out/graph.json`
   and `graphify affected "<Type>" --depth 2` — map the neighborhood

## Canonical 1.2 grounding (NEVER invent mechanics)

- **The 1.2 data is the source of truth.** If code and data disagree, the DATA wins (we fix the code).
- Live sqlite (the canonical 1.2 surface, 679 tables):
  `ssh root@192.168.0.165` + python3 sqlite3 on `/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3`
- Canonical resource table: SCORECARD.md → "Canonical resources" (fandom wiki, Ten Ton Hammer 1.2-era guides, AAEmu GitHub issues, aa-classic reference behavior).
- If you cannot verify a claim, mark it **UNVERIFIED** — do not guess.

## Goal shape (pick ONE — a scattershot exploration is a wasted budget)

- **A scorecard domain** (zero-data-wired / partial / low-% row): why it's unwired,
  what the data defines, what wiring would take (est. size S/M/L)
- **An upstream issue family**: map issues → real quest IDs / mechanics → find the
  root-cause class in code (see quests.md for the canonical example)
- **A mechanic question**: how does X actually work in 1.2 — data + code + docs cross-check

## Evidence requirements (every claim must be checkable)

- **Code claims:** file:line (e.g. `QuestManager.cs:234-265`)
- **Data claims:** table name + query + row counts, from the LIVE sqlite on the aaemu box
- **Upstream claims:** issue number + what the issue actually says
- **No invented mechanics.** 1.2 data wins over guesses. Unverifiable → `UNVERIFIED`.

## Report format (`scorecard-explorations/<domain>.md`)

1. Header: date, scope, explorer, repo state (branch/commit)
2. Data surface: tables, row counts, what's wired vs ignored
3. Code wiring: managers/loaders with file:line
4. Gaps: concrete list, each with evidence + est. size (S/M/L)
5. Upstream issue mapping: issue # → real quest id / mechanic
6. Priority fix list: ranked candidates for fix/feature cards
7. Status section: what's verified, what's UNVERIFIED, open questions

## Budget awareness (CRITICAL — the quests explorer trap)

- **Write the report to disk EARLY (~30% of budget):** skeleton + whatever is verified
  so far, then commit it. Refine in 2-3 passes.
- NEVER keep findings only in your final message — if budget dies, the findings die.
- Collect file:line evidence as you go; don't try to re-verify at the end.
- If budget runs out: the committed partial report still has value; note what's
  missing at the top so a follow-up can finish it.

## Status / awareness (close the loop)

- Report committed → Nei updates SCORECARD.md (if the report changes a domain's picture) + STATUS.md
- Findings that need code → Nei files fix/feature cards with the evidence packet (context_snapshot)
- One-line "what changed" in the kanban comment

## Deliverables

- Commits: the report (`scorecard-explorations/<domain>.md`) + scorecard row change (if any)
- Push: to fork origin ONLY (develop via docs branch or direct). **NO upstream PR.**
- Report: the report file URL/path + one-line "what changed" + any cards Nei filed
