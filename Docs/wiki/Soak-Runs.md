# Soak-Runs (E2E QA / soak / smoke on the testing server)

## Policy

All E2E **soak** and **QA** runs happen on the **testing server
`root@192.168.0.165`** in isolated lanes under `/root/aaemu-e2e-<lane>`. The
launcher itself runs on the dev host; only the isolated lane runs remotely. The
local dev host runs soaks for **smoke only** (short, throwaway checks); the
local host must stay free for build/gate work and must not be burned by
day-scale or multi-hour windows.

Each lane is fully isolated: its own E2E root, unique port block + DB port,
own `COMPOSE_PROJECT_NAME`, a fresh repo at the **current HEAD SHA**, and
PID-verified teardown. **pkill is banned.** Prod `/root/AAEmu` (ports
`1237/1239/1250/3306`) and sibling lanes are **never** touched.

## Tiers

| Tier | Window | What runs | Budgets |
|------|--------|-----------|---------|
| `smoke` | <5 min | ONE cheap E2E class (stack boots + live bridge handshake) | no |
| `qa` | <20 min | Named functional E2E class (default B1/B2/B6) | no |
| `soak` | long | Budgeted S-runs (B4 PACK/SLAVE/FARM, Dominion cycle, Village full-day) | asserted by the class |

## How to invoke
Run these commands from the **DEV HOST** in your repo checkout (`/root/aaemu-dev`). The suite rsyncs the working tree to .165 and builds there — **never invoke it on .165 itself**, which has no dev checkout.

```bash
# launched locally; execution is isolated on .165; artifacts rsynced back
./Scripts/e2e/run-qa-suite.sh --tier smoke                 # cheap lane smoke
./Scripts/e2e/run-qa-suite.sh --tier qa                     # B-series functional
./Scripts/e2e/run-qa-suite.sh --tier qa --target B6MerchantConservationE2eTests
./Scripts/e2e/run-qa-suite.sh --tier qa --target all       # every non-soak E2E class
./Scripts/e2e/run-qa-suite.sh --tier soak                   # B4 PACK/SLAVE/FARM + Dominion + Village full-day
./Scripts/e2e/run-qa-suite.sh --tier soak --target B4PackSoakE2eTests --minutes 360
./Scripts/e2e/run-qa-suite.sh --tier qa --lane smoke-rb --keep   # debug: skip teardown
```

Args: `--tier {smoke|qa|soak}`, `--target <FQCN|'all'>` (default tier-
appropriate), `--lane <name>` (default from target), `--minutes <n>` window
override, `--keep` skip teardown, `--artifacts <local-dir>`.

## Safety rules (HARD)

- **Never** touch prod `/root/AAEmu` on .165 — source is read-only rsync only.
- **Never** derive the prod ports `1237/1239/1250/3306` (script refuses if a
  lane port collides).
- **Never** touch a sibling lane's ports, DB, or compose project. Lane port
  derivation is deterministic per lane name; ports are checked free via `ss`
  before running (`Refuse to run if occupied`).
- **pkill is banned**: teardown kills only processes whose
  `/proc/<pid>/cwd` is under this lane root, then `docker compose -p <our-project>`
  down only.
- Lane-unique DB ports keep sibling DB volumes disjoint (own compose
  project + `.env`).

## Evidence contract

Every run emits, under the local artifacts path (default
`./soak-artifacts/<lane>/<timestamp>/`):

- the tested **HEAD SHA** (`git-head-sha.txt` — recorded before rsync);
- the **env** block (E2E_ROOT / ports / DB port / compose project / E2E_REBUILD=1 / E2E_GAME_HOST=127.0.0.1);
- canonical game-data **md5** `78b3bdbf038db3b927056106efdf91af` (verified before the run; mismatch = refuse);
- per-class logs, the class's report JSON/MD (already written under the lane's `logs/` by the class), `--minutes` budget override when given, artifact paths;
- a `RUN/PASS/FAIL` summary with artifact path + SHA.

Exit code 0 = all classes passed; otherwise the number of failed classes.

## Cleanup

`--keep` leaves the lane up for debugging (no teardown). Default teardown
(PID-verified, lane-scoped) stops our lane's Login/Game and drops our compose
project + DB volume; the lane directory (`/root/aaemu-e2e-<lane>`) and its
remote logs are left in place so evidence can be re-pulled. Sibling lanes are
never touched.

## Bot brain shadow harvest (`jev-shadow/v3`)

Comparing a recorded brain decision against a reviewer's own pick needs
per-decision arbitration records.
`JevShadow` (`AAEmu.Game/Core/Managers/Bots/JevShadow.cs`) is that recorder — one
JSONL row per sampled bot wake, schema `jev-shadow/v3`, carrying the brain's
candidate set (`goal`/`action`/`priority`/`personality_weight`/`reason`), the
incumbent's own pick (`selected_goal`/`selected_action`/`selected_candidate`),
and the optional per-brain frozen inputs (`travel`/`survival`/`needs`/`combat`).
It is a **copy** surface: it never decides, never mutates a `Character` or the
world, and has no runtime authority. The design rationale and the copy-surface
discipline are stated in the committed source itself —
[`JevShadow.cs`](../../AAEmu.Game/Core/Managers/Bots/JevShadow.cs) class and
`JevShadowRecord` documentation comments — and the recorded datasets are the
lane artifacts named below. *(An earlier revision of this page cited
`scorecard-explorations/mechanics/bot-brain-wiring-audit.md` for the design and
experiment spec. That file is a local-only historical audit and is **not tracked
in this repository**, so the link is withdrawn rather than presented as
published.)*

### Env vars (read by the game process, default OFF)

| Var | Default | Meaning |
|------|---------|---------|
| `AAEMU_JEV_SHADOW` | unset (**OFF**) | `1`/`true`/`True` arms the recorder |
| `AAEMU_JEV_SHADOW_SAMPLE` | `1` | write every Nth eligible wake; values `< 1` read as `1` |
| `AAEMU_JEV_SHADOW_PATH` | `Logs/jev-shadow.jsonl` under the game app base dir | output JSONL |

`JevShadow.Enabled` / `SampleEvery` / `FilePath` are static properties
initialised from these env vars at class-init. While the flag is off every
entry point is a static flag read and a return — zero allocation, byte-identical
wakes. The default compose configs do not arm the flag: no `docker-compose*.yaml`
in the tree sets it. The harvest script below sets it explicitly, for its own
run only. `.env.example` documents the three vars with every one of them
commented out (`# AAEMU_JEV_SHADOW=1`), so that file arms nothing.

### Reading one row: rejections, legal losers, incumbent

`jev-shadow/v2` separates the three kinds of proposal so none has to be inferred
from another. Two are arrays; the incumbent is a single object:

| Field | JSON type | Means |
|-------|-----------|-------|
| `candidates[]` | array | **HARD-precondition rejections** — proposed, then refused as illegal |
| `legal_alternatives[]` | array | **legal losers** — permitted, but out-ranked in preference order |
| `selected_candidate` | object or `null` | the **incumbent** the game actually took, as its own score row |

`candidate_count` is **selector truth**, not a length of either array: how many
proposals the selector weighed = hard rejections + all legal proposals (winner
included). `rejection_count` is exactly `candidates.length`. So
`candidates: []` with `candidate_count: 2` is a legitimate shape — nothing
was illegal; two proposals were legal and one won.

**`null` vs `[]`.** A collected wake always serializes an array, empty when
there was none of that kind, so "collected, and there were none" never reads the
same as "not collected". `legal_alternatives: null` means no selector result was
captured for that wake (nothing was selected); `[]` means a selection ran and the
winner was the only legal proposal. `selected_candidate: null` likewise means no
incumbent was chosen.

**Worked example** — row 4 of a 5-row isolated-lane dataset
(`/root/aaemu-e2e-t2-sol/runtime/game/Logs/jev-shadow-live-recorder-02.jsonl`,
SHA256 `6d5f8bc8f59b5069a997a72f55dd31bd3cb1c6d00a25c186c1ca740382fe08a9`,
scenario `quest-bootstrap-decision`). The excerpt below is a projection of that
row, wrapped so it is valid JSON:

> **This dataset is historical `jev-shadow/v1`, captured before the version bump.**
> Its `"schema"` field really does say `jev-shadow/v1`, and it is deliberately
> **not** relabelled — rewriting a captured artifact would falsify the record.
> It is shown here because it happens to exhibit the corrected two-proposal
> semantics (the count fix and `legal_alternatives` both landed in the build that
> produced it), but that makes it a **mislabeled v1**: the harvester rejects it and
> it must **not** be fed to the v2 review. Use a newly captured `v2` run as v2
> evidence.

```json
{
  "candidates": [],
  "candidate_count": 2,
  "rejection_count": 0,
  "selected_candidate": {"goal":"quest.turn-in","action":"TurnInQuest","priority":30,"weight":0,"reason":"selected"},
  "legal_alternatives": [{"goal":"quest.accept","action":"AcceptQuest","priority":17,"weight":1,"reason":"legal; not selected"}]
}
```

Nothing was hard-rejected (`candidates: []`, `rejection_count: 0`). Two
proposals were legal: `TurnInQuest` at priority 30 won and became the
incumbent, and `AcceptQuest` at priority 17 was a legal loser. `candidate_count`
is 2 because both were weighed — the shape a `candidates`-only reading
mis-reports as "one candidate".

### Evidence: staged networked bot, scheduler wakes

The worked row is **scheduler-wake evidence from a staged networked bot**. It
came from `QuestPerceptionLoopE2eTests` on the `t2sol` lane (report
`quest-perception-loop-report.json`, `PASS`, scenario `quest-perception-loop`):
one bot logged in over the real login flow, was staged once at the start hub as
disclosed setup, and its wakes ran through the production
`PlayerBotScheduler` → `BotRoamStepExecutor.StepAsync` path with
`AAEMU_JEV_SHADOW=1` and `AAEMU_NEEDS_FARM_ENABLED=1`. **No presence-demo flags
were set** — this was a single staged test bot, not a presence population.

The **bot was staged, not human**: no human played anything and no client was
connected. These rows are **not** evidence of general autonomy and **not** of an
optimizer — the choice was made by the shipped selector over its shipped
candidate set, with no learned or search component involved. They show that the
arbitration ran and the recorder captured it. **`H` stays UNKNOWN.**

### Live `v2` capture: `t2sol` lane, 2026-10-02

The worked row above is `v1`. A **`v2`** dataset now exists on the same `t2sol`
lane and is the artifact to feed a v2 review:

`/root/aaemu-e2e-t2-sol/runtime/game/Logs/jev-shadow-v2-quest-perception-01.jsonl`
— SHA256 `3aa299631b08af1103126ece71fcd9b9406241e5f85eb1f9dd7b16d1721ed514`,
source/test HEAD `626548de92b8dca8af1f0f92e22d37c9fbe6074e` (working tree **dirty**
at capture — `git status --porcelain=v1 -uall` reported **81** paths, inherited
uncommitted work; the SHA names the base revision only and nothing below was
re-verified at it), scenario `quest-bootstrap-decision`,
`AAEMU_JEV_SHADOW_SAMPLE=1`, output path as above. **4 rows, all carrying
`"schema": "jev-shadow/v2"`** — no mixed-schema content, so this is a genuine v2
dataset and not another mislabeled v1.

Row 3 is the two-legal-proposal shape: `TurnInQuest` at priority **30** selected,
with `AcceptQuest` at priority **17** as the legal loser. The quest-perception
test itself passed **1/1**.

Two fields in this dataset are empty **by design**, not by loss:

- **`candidates[]` is `[]` on all 4 rows.** `JevShadow.cs` builds that array in a
  loop over `rejections` only — it is populated from hard-precondition refusals.
  Nothing was illegal in these wakes, so it is legitimately empty; the legal
  proposals live in `legal_alternatives[]`, and the winner in `selected_candidate`.
  This is the shape the worked v1 row above illustrates too, for the same reason.
- **`wake_sequence` is `null` on all 4 rows.** It is staged only by
  `BotGoalArbiterStepExecutor.StageWakeSequence`, which guards on its inner
  executor being a `BotRoamStepExecutor` (`if (_inner is not BotRoamStepExecutor
  roam) return;`). Under that two-layer composition — arbiter decorator wrapping
  the roam step — the stamp is staged onto the inner actor and read back on the
  next request; where the inner executor is not the roam surface, or the wake was
  never staged, the field stays `null`. A `null` is the not-attributed case the
  field exists to identify, **not a defect**.

**Caveats — what this capture does not establish:**

- The bot was **staged** at the start hub as disclosed setup, as in the run above;
  it is not a human-played session.
- **One bot, 4 decision wakes.** This is not a wake census — it is a smoke capture
  proving the v2 schema and the arbitration record, not behavioural breadth.
- **No outcome field *in this row* is independent state verification.**
  `request.state` is the actor request's terminal state; `decide_detail` is the
  actor's own `Request.Detail` string (`BotRoamStepExecutor.cs:1566`); and
  `completed_quest_ids` is a set-difference over `ActiveQuestIds`
  (`QuestBehavior.cs:959`) — it means **"left the active set"**, not "quest
  completed". `CharacterQuests.DropQuest` removes from that same set on the
  client abandon packet (`CSDropQuestContextPacket.cs:11`) and on GM commands
  (`QuestCommandUtil.cs:134,158`, `forcibly:true` **clearing** the completed
  flag), as do the `AddQuest` duplicate/forbidden paths. So row 3's
  `completed_quest_ids: [6198]` cannot be read as proof that quest 6198
  completed. This is a **naming hazard in the schema**, not a proven defect in
  any run.
- **The claim is about this schema, not about the whole lane.** The same wake
  *is* corroborated outside the row, and that corroboration has two distinct
  strengths:
  - **Exact-cycle (reviewer's stated link).** `runtime/game/Logs/Server.log:28663`
    carries this row's own `cycle_id` (`quest-1-639265655676529911`). The
    **finalization lines themselves (28664–28673) carry no `cycle_id`** — they
    are bracketed by it and fall in the same second, showing `QuestManager`
>    finalization and `[Quest] for player: Qploopbff9dc60, quest: 6198 removed.`
    for char 1. That is a **temporal association** with the cycle, not a direct
    terminal record keyed by it.
  - **Identity-sequence.** `logs/archive-pre-g7d-20261002T192157Z.jsonl` records
    `stagedQuest.completedInSession=true` and
    `completedBitPersistedAfterRestart=true` for quest 6198 (a durable flag read
    back after restart), and `followUpQuest.accepted=true` /
    `activeRowPersistedAfterRestart=true` for quest 251 with `completed: null`.
    That report carries **no `cycle_id` and no trace id**, so it joins on
    bot/quest/run identity and sequence — correlation, not proof of *this* wake.
- **Comparative quality remains UNKNOWN.** Corroborating the taken pick is not
  evidence about the outranked alternative: no artifact observes what the
  alternative would have done. Its outcome is **unobserved, not failed**.

  This capture has now been read offline — see
  [`Scripts/jev-arbiter-review.py`](../../Scripts/jev-arbiter-review.py) below.
  No `H` acceptance exists. `H` stays UNKNOWN.

The historical `v1` dataset and its section above are unchanged.

### Offline arbiter review (`Scripts/jev-arbiter-review.py`)

Reads a captured `jev-shadow/v3` dataset — and a historical v2 one on request —
and reports, per wake: the incumbent the game took, the legal alternatives it
outranked, the hard-precondition rejections, and the outcome **split into three
separately-labelled layers**
(request terminal state / actor self-report / active-set delta) so a request
completion is never read as a gameplay consequence.

```bash
python3 Scripts/jev-arbiter-review.py <dataset.jsonl> --out report.json
```

This is a **review of a frozen record, not a replay**: it never re-runs
`BotDecisionSelector`, so every priority it prints is the copy the shipped
selector itself saw.

It **refuses** a dataset it cannot read faithfully and exits `1`: any file
outside the live schema (by default v3 only; `--historic` additionally admits
v2, and under that flag a mixed v2/v3 file is readable with each row reporting
its own identity presence — v1 is never readable, because its
`candidate_count` meant `rejections + 1` and could not see a legal loser), a
malformed or wrongly-typed row (each row must be a JSON object carrying a
non-empty unique string `cycle_id`, a string `scenario` and an integer
`candidate_count`; `candidates` and `legal_alternatives` must be lists of
candidate rows carrying `goal`/`action`/`priority`/`weight` — plus
`target_id` on v3 rows — or null for `legal_alternatives`; `selected_candidate`
such an object or null; `request` an object or null), or a row whose
`candidate_count` contradicts its own arrays. A **duplicate or empty
`cycle_id`** is refused because it is the only key a supplied claim binds to.
A `--out` path that cannot be written is reported the same way.

#### Supplying external outcomes, and comparing a reviewer's pick

The capture carries no verifiable outcome of its own. A reviewer who has one —
a server log line, a lane test report, a DB read — supplies it explicitly, and a
reviewer who disagrees with a pick re-picks a candidate. **Nothing is inferred**
by the tool, and a supplied entry is **bound or refused** (exit `1`, never a
silent note): its `dataset_sha256` must match this capture and its `cycle_id`
must name exactly one wake here.

> **The tool binds the DECLARATION, not the source.** It never opens or reads
> the file named in `source`, and cannot check that it says what the claim
> asserts. Provenance of that artifact stays a **human reviewer's** judgement —
> the tool checks that a claim is *addressed correctly*, never that it is
> *true*.

#### Supplying external outcomes, and comparing a reviewer's pick


The capture carries no verifiable outcome of its own. A reviewer who has one —
a server log line, a lane test report, a DB read — supplies it explicitly, and a
reviewer who disagrees with a pick re-picks a candidate. **Nothing is inferred**
by the tool, and a supplied entry is **bound or refused** (exit `1`, never a
silent note): its `dataset_sha256` must match this capture and its `cycle_id`
must name exactly one wake here.

> **The tool binds the DECLARATION, not the source.** It never opens or reads
> the file named in `source`, and cannot check that it says what the claim
> asserts. Provenance of that artifact stays a **human reviewer's** judgement —
> the tool checks that a claim is *addressed correctly*, never that it is
> *true*.

```bash
python3 Scripts/jev-arbiter-review.py <dataset.jsonl> \
  --outcome-claims <outcomes.json> --critique <critique.json> --out report.json
```

**Both envelopes must carry `dataset_sha256`**, the sha256 of the capture they
were written against; a mismatch is refused outright, because a claim judged
against different rows says nothing about these. A critique envelope must also
carry **`reviewer`**. Every entry must name a `cycle_id` the capture actually
contains; an unknown cycle is refused, not reported.

*Outcome claims* (`--outcome-claims`) need six strings — `cycle_id`, `claim`,
`basis`, `source`, `join`, `class`.

| `exact-cycle` | the artifact is stated to carry the row's own `cycle_id` — a direct link the **reviewer asserts**; the tool cannot verify it (see above) |
| `identity-sequence` | matches on bot/quest/run only — correlation, not proof of this wake |
| `none` | an unbound assertion |

| `class` | means |
|---|---|
| `authoritative-state` | read back from durable state (completed bit, ledger) |
| `engine-log` | the engine's own log line |
| `actor-self-report` | the actor describing its own success |
| `reviewer-opinion` | **refused here** — belongs in a critique envelope |

*Reviewer critique* (`--critique`) needs `reviewer`, `dataset_sha256`, and a
`critique` list whose entries name `cycle_id`, `chosen`, `claim`, and `basis`.
`chosen` must index a **legal** candidate.

| `chosen` | means |
|---|---|
| `selected_candidate` | the incumbent the game took |
| `legal_alternatives[n]` | the n-th legal loser |
| `candidates[n]` | **refused** — a hard precondition rejection the engine already refused as illegal cannot be re-chosen |

Choosing from a wake whose `legal_alternatives` is `null` is refused too: the
lane never collected them, so there is nothing to choose.

The tool then **compares** the challenger's pick against the frozen incumbent
**by the reference it named**, and reports `AGREES` or `DISAGREES`.
`selected_candidate` *is* the incumbent by construction; any
`legal_alternatives[n]` is by definition **not** the one the game took.

It deliberately does **not** compare the rank tuple. Rank equality is not
identity: two different proposals can score identically — a band tie, or two
quests offered at the same level — and a rank-based verdict would report
`AGREES` for a challenger that picked a different objective than the game did.
When two picks share a rank but differ in `target_id`, the report names that
explicitly rather than letting the collision pass as agreement.

**Benefit is always `UNKNOWN`** — no artifact observes what the other candidate
would have done, so a `DISAGREES` verdict is a difference of opinion, never an
improvement.

Format examples live beside the tool as
`Scripts/jev-arbiter-review.outcomes.example.json` and
`Scripts/jev-arbiter-review.critique.example.json`.

> **Both are FORMAT TEMPLATES, not evidence — but they do NOT both "produce no
> verdict", and the difference is deliberate.**
>
> **The critique example produces NO verdict: it is REFUSED.** Run against its
> own dataset it exits `1` and prints nothing comparative —
> `critique reviewer 'EXAMPLE-REVIEWER (illustrative; replace with a real
> identity)' is an ILLUSTRATIVE placeholder, not a real reviewer; refusing to
> emit an AGREES/DISAGREES verdict from filler`. Supplying **both** envelopes
> still exits `1` on the same critique check, because the critique is evaluated
> and refused before any comparison is rendered. `jev-arbiter-review.py` refuses
> any critique whose `reviewer` is an illustrative placeholder
> (`EXAMPLE-REVIEWER`, `illustrative`, `placeholder`,
> `replace with a real identity`, `TODO`, `CHANGEME`) — without that check the
> shipped file would print a confident-looking `DISAGREES`/`AGREES` pair
> attributed to nobody, which is fabricated evidence.
>
> **The outcomes-only example DOES print assertions, and that is not a
> defect.** Run against its own dataset it exits `0` and renders one `EXTERNAL
> [<join>/<class>]` block per claim — each restating the claim text, its `basis`
> and its cited `source`. Those are **reviewer-declared assertions rendered
> verbatim, not a verdict**: no `AGREES`/`DISAGREES` comparison is produced
> (there is no challenger to compare, so `critic_reviewer` is absent), and
> comparative quality stays `UNKNOWN`. The tool **never opens the cited source**
> to confirm the claim, so provenance remains a human reviewer's judgement — it
> binds the declaration, not the truth. Read them as *addressed correctly*, not
> as *verified*.

**No Jev model call was made, and none is possible here.** No model caller is
configured in this repository, so this tool is **preparation for a future
Jev-assisted policy review** — it produces the artifact such a review would
read. It issues no recommendation about the policy table: it does not say the
turn-in-first ordering was right or wrong, and the dataset cannot support one.

**The one concrete finding from the `v2` capture** (historical — 4 rows, and
still read with `--historic`). Over its four wakes every `candidate_count`
reconciles, and exactly one wake carried a legal alternative: `TurnInQuest`
p30/w0 was taken while `AcceptQuest` p17/w1 was legal and outranked. The
capture proves the v2 arbitration record is readable and internally consistent —
and nothing more about quality. **The v2 rows carry no `target_id`,** so that
competing wake stays unresolvable: the reviewer can see the `AcceptQuest` p17
loser existed, never whether it named the same quest the turn-in did.
>
> That gap is what the [`v3` capture below](#live-v3-capture-isolated-jev-lane-2026-10-03)
> closes. It is a **separate run, not a re-run of this one** — the two files
> share no `cycle_id` — but it shows the *same shape*, a p30/w0 turn-in taken
> over a p17/w1 `AcceptQuest`, this time with the two identities attached:
> **target 6198 vs target 251, distinct quests.** The shapes line up; the rows
> are not the same row. The first v3 review states the same standing
> conclusion: comparative quality **UNKNOWN**.

**What it explicitly does not establish.** No reward, experience, or labor
field exists in the schema, so whether the taken pick beat the alternative is
**not derivable** and no such claim is made. Every `travel`/`survival`/`needs`/
`combat` block is null — true of all 4 v2 rows **and all 18 v3 rows** (measured,
not assumed) — so neither capture **can** feed `JevReplayHarness`.

**No arbitration change follows from it.** `QuestOptions` priorities are frozen
policy, and neither dataset supports a change: the v2 capture is one bot and
four wakes, the v3 capture 18 wakes of which exactly **one** is legally
contestable. An **external outcome
envelope** now exists (see above), so an outcome can be *recorded and
addressed* against a wake — but that is still a **reviewer-declared assertion**,
not an in-row authoritative postcondition. An outcome field read back from
authoritative state and carried **inside** the row remains **not applied**, and
would need a fresh harvest to validate.
Evidence tier: offline review of a rig-tier capture. Not live, not autonomy,
not `H`.

### Running it

`Scripts/jev-shadow-harvest.sh` boots an **isolated E2E lane** (real Login+Game
binaries, lane-unique ports and compose project, PID-scoped teardown). Both
processes inherit the shadow switch; only Game has the recorder. It refuses an
already-running lane server or occupied game port before boot. Choose ports
that are free on this host and provision the isolated lane's game data first:

```bash
# Export the same isolated settings for both the read-only preflight and harvest.
export E2E_ROOT=/root/aaemu-e2e-jev COMPOSE_PROJECT_NAME=jev
export E2E_INTERNAL_PORT=28327 E2E_LOGIN_PORT=28330 E2E_GAME_PORT=28332
export E2E_STREAM_PORT=28340 E2E_BRIDGE_PORT=28350 E2E_WEBAPI_PORT=28360
export E2E_DB_PORT=48320 DB_HOST_PORT=48320 E2E_GAME_HOST=127.0.0.1 E2E_BRIDGE=1
export AAEMU_PRESENCE_DEMO=1 AAEMU_PRESENCE_BOT_COUNT=10 AAEMU_PRESENCE_MAX_BOTS=10
export AAEMU_QUEST_BOOTSTRAP_ENABLED=1 AAEMU_NEEDS_FARM_ENABLED=1
export AAEMU_JEV_SHADOW_PATH=/root/aaemu-e2e-jev/runtime/game/Logs/jev-shadow.jsonl
./Scripts/jev-shadow-harvest.sh --minutes 30 --dry-run
# Only after the preflight succeeds; the script stops its own servers afterward:
./Scripts/jev-shadow-harvest.sh --minutes 30 --sample 1 --drain-seconds 30
```

`--minutes <n>`, `--sample <n>`, `--out <path>`, `--force` (allow overwriting
an existing **non-empty** dataset), `--keep` (leave the lane up), `--drain-seconds
<n>`, `--dry-run`, `--allow-prod-host` (explicit production opt-in; it still
requires explicit project and remapped ports). `--help` prints the full surface.

**`--dry-run` verifies, it does not merely echo the env.** Without an explicit
production opt-in, it requires an isolated root; either way it requires a
non-default project and remapped ports, refuses running lane Login/Game processes
and occupied ports, and checks the dataset path, lane log-cap guard, and canonical
compact SQLite md5. An existing lane DB is accepted only when its port and
credentials match that lane. It starts no servers or writes; unmet conditions
are reported before boot.

**Exit codes.** `0` — with `--dry-run`, all prerequisites checked are present;
on a real run, the dataset was produced and verified. `1` — a direct validation
or runtime failure (for example a shared project/port, invalid path, or failed
boot). `2` — a missing required environment variable, production-host guard,
existing dataset without `--force`, unknown argument, or aggregated preflight
failure (occupied port, drifted canonical data, or running lane server).
`--dry-run` never produces a dataset; its `0` means only that preflight passed.

**Bot population = the in-game `PlayerBotManager`/`PlayerBotScheduler` wake
path.** The presence harvest is one way to populate it, using
`AAEMU_PRESENCE_DEMO` / `AAEMU_PRESENCE_BOT_COUNT` /
`AAEMU_PRESENCE_MAX_BOTS` / `AAEMU_PRESENCE_MANIFEST` to create a multi-bot
population; `BotPresenceCoordinator` then starts `PlayerBotScheduler`, which
leases each bot and runs `BotRoamStepExecutor.StepAsync` — the call chain whose
`StepQuestLeg` / `StepNeedsFarmLeg` call the recorder. Headless (no client, no
human in the loop), repeatable, and already proven by the scheduler stage-1 soak.

That is the multi-bot route, not the only one that yields arbitration rows. A
targeted E2E scenario can also put a single staged bot on the same scheduler
path: `QuestPerceptionLoopE2eTests` did so without presence-demo flags, and the
recorder captured real quest arbitration. The split is scripted setup and wake
cadence on the rig's side; **which actions the scheduler picks are still
normal gameplay arbitration** — the bot accepted and turned in quest 6198
through the production quest leg and real report path. Such rows support the
same arbitration contract as a presence run, from a narrower population.

The WebApi/BotControl actuation surface remains distinct: it drives
*individual* actions (`/api/actors/*`) rather than the brain's own arbitration.

### Verifying the output

The script verifies and prints; it never asserts a success it did not measure:

1. the dataset exists at the resolved path (under the lane app base),
2. it is non-empty,
3. it reports complete (newline-terminated) row count + byte size,
4. **every** row parses as JSON and carries `"schema": "jev-shadow/v3"` — the
   check is whole-file, not first-line, so a dataset that opens with a v2 row and
   then carries v1 rows is rejected as mixed-schema,
5. it reports any torn trailing line and any malformed rows.

Anything it cannot verify it prints as `UNVERIFIED` and exits non-zero. **Row
count is a lower bound**, not proof of completeness: the writer is a background
thread draining a bounded 1024-line queue, drop-newest when full, and there is
no flush join on any shutdown path — lines queued at exit are lost by design and
drops are not recorded in the file.

### Schema versions: `jev-shadow/v1`, `/v2`, `/v3`

**`v3` (2026-10-02) is ADDITIVE, and is what the recorder writes today** —
"live" here means the *current writer schema*; a real v3 lane capture now exists
too (see below). Every candidate row —
hard rejections, legal alternatives, and the incumbent — now also carries
`target_id`, copied from the proposal's own `BotDecisionProposal.TargetId`.

Before v3 a reviewer could see that a legal alternative existed and how it
scored, but never **which objective it referred to**, so a challenger could not
be joined to the quest it named. That was the concrete gap the v2 capture left
open: its one competing wake showed `TurnInQuest p30` taken and `AcceptQuest
p17` outranked, with no way to tell whether the loser named the same quest.

`target_id` is copied verbatim, so its **meaning depends on the action**: the
quest id for the quest goals, an in-world object id for a `Target`/`Move`
proposal. Read it together with `action`, never alone. A proposal that names no
target records `0`, which is a real value and never an absent field — that is
why the member is a non-nullable `uint` rather than an optional.

Nothing else moves: `candidate_count` keeps its v2 selector-truth meaning, and
the three candidate kinds stay in the same three arrays.

#### Archaeology evidence for the quest `target_id` semantics

Recorded per the `AGENTS.md` archaeology contract. **This is data/code evidence
about quest identity, not a claim about a v3 capture.**

- **Source:** `compact.sqlite3`, `/root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3`,
  version `1.2 r208022`, opened read-only; md5 `78b3bdbf038db3b927056106efdf91af`.
- **Source SHA:** `git rev-parse HEAD` = `4b4670d16b36bdfb69b842206e8bf0cd46b18c2f`
  (working tree dirty; SHA names the base revision only).
- **Tools:** `list_sources`, `query_sql`.
- **Query and result (confidence: `exact`, not truncated, limit 100, row_count 2):**
  `SELECT id,name,LEVEL,category_id,REPEATABLE,zone_id FROM quest_contexts WHERE id IN (6198,251)`
  → `6198` "바라기 마을로" `LEVEL 1`; `251` "화환 맹공쟁이" `LEVEL 2`.
- **A negative worth recording:** `quest_tasks` holds exactly **one** row
  (`COUNT(*)=1`, `id=1`) and is **not** the quest catalog — querying quest ids
  there returns zero rows and would silently read as "quest 6198 does not exist".
  The catalog is `quest_contexts` (4876 rows). Quest identity must be read from
  there.
- **Why it matters here:** it independently confirms the band arithmetic behind
  `QuestBehavior.AcceptRank` (`priority = AcceptPriority + max(0, BandMax − level)`,
  shipped defaults 10/9). The v2 capture's p18 and p17 accepts imply levels 1 and
  2; reference data says 6198 **is** level 1 and 251 **is** level 2. The decode
  and the game data agree, which is why v3's `target_id` is worth capturing: it
  makes that join direct instead of inferred.


`v2` was a **behavioural** cutover, not an additive one:

- `legal_alternatives[]` was added — the proposals that were legal but lost the
  preference order, previously invisible to a review.
- `candidate_count` was redefined as **selector truth** (hard rejections + every
  legal proposal, winner included). In v1 it was the `ActorRequest` stamp
  `rejections + 1`, which structurally cannot see a legal loser and therefore
  under-reported any wake that had one.

**Version rules.** A v1 reader must not consume a v2 row: the same field means
something different. `Scripts/jev-shadow-harvest.sh` refuses anything that is
not v3, **including a mixed file**, rather than accepting a first line that
happens to be current.
`Scripts/jev-arbiter-review.py` reads v3 by default. Under the explicit
`--historic` flag it also admits **v2, and a mixed v2/v3 file**: a v3 row must
carry `target_id` and a v2 row must not, each row validated against its own
schema, and `target_identity` is reported **per row** so a v2 row in a mixed
file reads `target=ABSENT` rather than inheriting v3's "present". **v1 is not
readable at all** — its `candidate_count` cannot see a legal loser.

Existing datasets under lane `runtime/game/Logs/` are **historical and stay in
the version they were written as**. They are never rewritten or relabelled;
re-harvest to a fresh `--out` path instead of appending to one.

### Live `v3` capture: isolated `jev` lane, 2026-10-03

The v3 recorder is proven at the **unit seam** (`JevShadowTests`, three distinct
`target_id` values surviving to the wire, mutation-verified) **and now on a real
isolated lane**. This is the first `jev-shadow/v3` dataset in existence.

**Evidence tier: Tier 2, isolated rig — NOT human/client live.** The bots are
scripted and headless; no client and no human are involved. **`H` stays
UNKNOWN.**

`/root/aaemu-e2e-jev/runtime/game/Logs/jev-shadow-v3-narrow.jsonl` —
SHA256 `ae6363b56f0b9854dee57c827430ef42227b1e7768deefbcef296d87df0c5f0d`,
26949 bytes, **18 rows**, all 18 rows `jev-shadow/v3`.

**Provenance.**

- Source revision `git rev-parse HEAD` =
  `4b4670d16b36bdfb69b842206e8bf0cd46b18c2f` (branch `develop`), **working tree
  dirty — 12 paths** at capture. The SHA names the base revision only; the
  capture is from the dirty tree, which is what carries the uncommitted `v3`
  recorder change (`JevShadow.cs`, `JevShadowTests.cs` — at HEAD the constant
  still reads `jev-shadow/v2`).
- Lane `/root/aaemu-e2e-jev`, compose project `jev`, ports 28327/28330/28332/
  28340/28350/28360, DB 48320. Canonical `compact.sqlite3` md5 verified
  `78b3bdbf038db3b927056106efdf91af` (hardlink-cloned from the `t2sol` lane —
  `e2e-boot.sh --clone-data-from` would also have *booted* the lane, so
  `e2e_clone_game_data` was called directly).
- Capture command (exact; `--filter-class` is the only filter this MTP runner
  accepts — it rejects both `--treenode-filter` and `--nologo`, each of which
  runs **zero** tests):

  ```bash
  E2E_ROOT=/root/aaemu-e2e-jev COMPOSE_PROJECT_NAME=jev E2E_GAME_HOST=127.0.0.1 \
  E2E_INTERNAL_PORT=28327 E2E_LOGIN_PORT=28330 E2E_GAME_PORT=28332 \
  E2E_STREAM_PORT=28340 E2E_BRIDGE_PORT=28350 E2E_WEBAPI_PORT=28360 \
  E2E_DB_PORT=48320 DB_HOST_PORT=48320 \
  AAEMU_QUEST_BOOTSTRAP_ENABLED=1 AAEMU_NEEDS_FARM_ENABLED=1 \
  AAEMU_JEV_SHADOW=1 AAEMU_JEV_SHADOW_SAMPLE=1 \
  AAEMU_JEV_SHADOW_PATH=/root/aaemu-e2e-jev/runtime/game/Logs/jev-shadow-v3-narrow.jsonl \
    dotnet test --project AAEmu.IntegrationTests/AAEmu.IntegrationTests.csproj \
      --configuration Release \
      --filter-class AAEmu.IntegrationTests.E2e.QuestPerceptionLoopE2eTests \
      --output Detailed
  ```

  **3/3 passed, 7m 30s 779ms, exit 0** at the same SHA. The recorder arms in
  the game process because `E2eStack.StartServerProcess` builds a
  `ProcessStartInfo` that inherits the ambient environment (it explicitly
  forwards only the two `*_ENABLED` flags; `AAEMU_JEV_SHADOW*` is inherited).
- **Project gate** at the same SHA: `./scripts/gate.sh` → Release build passed,
  ScriptCompiler **0 errors / 0 warnings**, unit suite **3958 total / 3957
  succeeded / 0 failed / 1 skipped** in 2m 24s (the skip is
  `Provision_Activate_Persist_Deactivate_RoundTrip`, the known live-rig guard
  requiring `AAEMU_LIVE_RIG=1` + `AAEMU_E2E_DB_PASSWORD`, not a regression), MCP
  stdio smoke **44 tools**, MCP archaeology gate smoke **24 tools, 679 tables,
  read-only**. Exit `0`.
- **Sibling isolation held.** The occupying `t2sol` lane was never signalled or
  reconfigured (game pid and login pid both alive, game still listening on
  `:27332` afterwards); the shared `/root/aaemu-e2e` lane stayed up; and the
  canonical md5 was re-checked as unchanged in all three lanes after the run.

Verified directly against the file (not taken on report):

- **18/18 rows carry `"schema": "jev-shadow/v3"`** — no v1/v2 content.
- **`candidate_count` reconciles on every row** (hard rejections + legal
  proposals + winner), checked independently of the tool.
- **`target_id` present on 77/77 candidate entries** across all three kinds.
- **18 distinct, non-empty `cycle_id`s** — no duplicate, none empty.
- Whole-file parse clean; no torn trailing line.
- **No Jev critique was run against this file, and none exists.** It was
  reviewed for readability only —
  `python3 Scripts/jev-arbiter-review.py <dataset> --out report.json` → exit
  `0`, dataset accepted, header `targets : all present`. No `--critique`
  envelope was supplied, so there is **no challenger pick and no
  `AGREES`/`DISAGREES` verdict** for this capture; the reviewer's standing
  `COMPARATIVE QUALITY: UNKNOWN` line is the only verdict it prints. The one
  contested row is therefore *reviewable*, not *reviewed*.

**The row this capture exists to produce** — `quest-3-639266106976280171`,
scenario `quest-bootstrap-decision`, character 3, `landed:TurnInQuest`:

| | goal / action | priority | weight | `target_id` |
|---|---|---|---|---|
| incumbent | `quest.turn-in` / `TurnInQuest` | 30 | 0 | **6198** |
| legal loser | `quest.accept` / `AcceptQuest` | 17 | 1 | **251** |

Within the captures documented on this page, this is the **first row where the
taken pick and the outranked alternative carry distinct target identities** —
the join v3 added and v2 could not express.

The **analogous historical v2 contested wake** is a *different row in a
different capture*: `quest-1-639265655676529911` in the 4-row `t2sol` v2 file
(character 1, captured 2026-10-02), not this wake. The two files share **no
`cycle_id` at all**. Both show the same *shape* — a p30 turn-in taken over a p17
`AcceptQuest` — but they are separate runs, not a before/after of one event, and
nothing here pairs them. What v2 could not resolve was that same-wake question:
the reviewer could see an `AcceptQuest` p17 alternative existed, but with no
`target_id` could never tell whether it named the same quest as the turn-in.

**What this capture does NOT establish.**

- **Only 1 of the 18 rows is legally contestable.** 14 are needs-leg rows with
  hard rejections and **zero** legal alternatives; a critique cannot re-pick a
  hard rejection (the engine already refused it as illegal). 3 quest rows are
  single-offer, 1 is a `DECIDE` with no selection.
- **Rig-tier, disclosed-setup staging.** The bot was staged at the start hub;
  this is not traversal proof and not human play. **`H` stays UNKNOWN.**
- **No quality claim.** The capture proves the arbitration record and its
  identity join; it says nothing about which pick was better. Comparative
  quality and benefit remain **UNKNOWN** — no artifact observes what the
  alternative would have produced.

> **Lane note.** `E2eStack.EnsureUp()` calls `ResetDbVolume()`
> (`docker compose -p jev down -v`), so the lane DB is re-seeded on every run:
> this route re-provisions rather than reusing an existing database. All
> operations are lane-scoped; the occupied `t2sol` lane was verified untouched
> (canonical md5 unchanged across all three lanes).
>
> The earlier **4-row `t2sol` capture remains `v2`** and is historical — read it
> with `--historic`. It is never relabelled as v3.

### Evidence honesty — read before quoting a harvest

A harvest produces **synthetic / rig-tier** decision records from an isolated
lane running scripted bots. It is **contract and orchestration evidence only**:

- It is **NOT** proof of live human playability. No human played anything.
- It is **NOT** proof of bot autonomy. The bots ran a loop whose enablement,
  patrol home and scenario pacing are rig-controlled.
- **`H` stays UNKNOWN.** Human acceptance still requires the actual named human
  run in the client (`SCORECARD.md`), per the evidence-honesty gate in
  [`AGENTS.md`](../../AGENTS.md#evidence-honesty-and-anti-overclaim-gate-2026-09-16).
- A large row count is not evidence of a good decision; it is evidence that the
  recorder ran. A human or agent **critique** may disagree with a recorded
  pick, but nothing here scores that disagreement: determining whether any pick
  was *better* would require separately observed, comparable outcomes for both
  candidates, and no such artifact exists for these captures.
- Also per the audit: only the legs that actually call the recorder emit rows
  (quest, needs, and the per-brain captures from the same wake). Combat/travel
  arbitration rows depend on those capture call sites being live — treat
  `null` brain blocks as "did not run", not "no data".

Record the run like any other lane: HEAD SHA, lane root, window, expected bot
count, sampling rate, out path, row/byte counts — see **Evidence contract**
above.
